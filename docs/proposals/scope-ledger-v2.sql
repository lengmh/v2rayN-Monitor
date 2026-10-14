-- S5.6 schema/mapping source, embedded by opt-in ScopeLedger.CreateNew for NEW EMPTY files.
-- Explicit offline ScopeLedgerMigration uses this same source; normal startup never migrates v1.
-- Caller must verify a supported v1 database, take a SQLite backup, enable
-- foreign_keys, begin ONE write transaction, and validate before committing.
-- No BEGIN/COMMIT here: failed validation must roll back DDL and user_version.

CREATE TABLE measurement_scopes (
    scope_id INTEGER PRIMARY KEY,
    instance_id TEXT NOT NULL CHECK(length(instance_id) > 0),
    basis TEXT NOT NULL CHECK(basis IN
        ('legacy_proxy','actual_member','business','excluded','unknown','routed_outbound')),
    identity_kind TEXT NOT NULL CHECK(identity_kind IN
        ('legacy_node','index_id','outbound','aggregate')),
    identity_value TEXT NOT NULL CHECK(length(identity_value) > 0),
    node_key TEXT REFERENCES nodes(node_key),
    outbound_tag TEXT,
    name TEXT NOT NULL,
    UNIQUE(instance_id,basis,identity_kind,identity_value),
    CHECK(
        (basis='legacy_proxy' AND identity_kind='legacy_node' AND node_key IS NOT NULL AND outbound_tag IS NULL)
        OR (basis='actual_member' AND identity_kind='index_id' AND node_key IS NOT NULL AND outbound_tag IS NOT NULL)
        OR (basis IN ('actual_member','routed_outbound') AND identity_kind='outbound'
            AND node_key IS NULL AND outbound_tag IS NOT NULL)
        OR (basis IN ('business','excluded','unknown') AND identity_kind='aggregate'
            AND identity_value=basis AND node_key IS NULL AND outbound_tag IS NULL)
    )
);

CREATE TABLE scope_cursors (
    scope_id INTEGER PRIMARY KEY REFERENCES measurement_scopes(scope_id),
    session_id TEXT NOT NULL,
    generation TEXT NOT NULL,
    at_ticks INTEGER NOT NULL CHECK(typeof(at_ticks)='integer'),
    upload INTEGER NOT NULL CHECK(typeof(upload)='integer' AND upload >= 0),
    download INTEGER NOT NULL CHECK(typeof(download)='integer' AND download >= 0),
    interrupted INTEGER NOT NULL DEFAULT 0 CHECK(interrupted IN (0,1)),
    continuity_valid INTEGER NOT NULL DEFAULT 1 CHECK(continuity_valid IN (0,1))
);

CREATE TABLE scope_batches (
    instance_id TEXT PRIMARY KEY CHECK(length(instance_id) > 0),
    at_ticks INTEGER NOT NULL CHECK(typeof(at_ticks)='integer'),
    fingerprint TEXT
);

CREATE TABLE scope_minutes (
    start_ticks INTEGER NOT NULL CHECK(typeof(start_ticks)='integer'),
    scope_id INTEGER NOT NULL REFERENCES measurement_scopes(scope_id),
    upload INTEGER NOT NULL CHECK(typeof(upload)='integer' AND upload >= 0),
    download INTEGER NOT NULL CHECK(typeof(download)='integer' AND download >= 0),
    approximate INTEGER NOT NULL DEFAULT 0 CHECK(approximate IN (0,1)),
    synthetic INTEGER NOT NULL DEFAULT 0 CHECK(synthetic IN (0,1)),
    PRIMARY KEY(start_ticks,scope_id)
);

CREATE TABLE scope_unallocated (
    id INTEGER PRIMARY KEY,
    start_ticks INTEGER NOT NULL CHECK(typeof(start_ticks)='integer'),
    end_ticks INTEGER NOT NULL CHECK(typeof(end_ticks)='integer' AND end_ticks >= start_ticks),
    scope_id INTEGER NOT NULL REFERENCES measurement_scopes(scope_id),
    upload INTEGER NOT NULL CHECK(typeof(upload)='integer' AND upload >= 0),
    download INTEGER NOT NULL CHECK(typeof(download)='integer' AND download >= 0),
    quality TEXT NOT NULL
);

CREATE TABLE scope_gaps (
    id INTEGER PRIMARY KEY,
    start_ticks INTEGER NOT NULL CHECK(typeof(start_ticks)='integer'),
    end_ticks INTEGER NOT NULL CHECK(typeof(end_ticks)='integer' AND end_ticks >= start_ticks),
    scope_id INTEGER REFERENCES measurement_scopes(scope_id),
    reason TEXT NOT NULL,
    is_open INTEGER NOT NULL DEFAULT 0 CHECK(is_open IN (0,1)),
    origin TEXT NOT NULL CHECK(origin IN ('legacy','collector'))
);
-- Legacy NULL-node gaps can overlap; preserve them exactly. New outages coalesce per scope.
CREATE UNIQUE INDEX scope_gap_open ON scope_gaps(scope_id)
    WHERE is_open=1 AND origin='collector' AND scope_id IS NOT NULL;
CREATE INDEX scope_minutes_time ON scope_minutes(scope_id,start_ticks);
CREATE INDEX scope_unallocated_time ON scope_unallocated(scope_id,start_ticks,end_ticks);
CREATE INDEX scope_gaps_time ON scope_gaps(scope_id,start_ticks,end_ticks);

INSERT INTO measurement_scopes(instance_id,basis,identity_kind,identity_value,node_key,outbound_tag,name)
    SELECT instance_id,'legacy_proxy','legacy_node',node_key,node_key,NULL,name FROM nodes;
INSERT INTO scope_minutes(start_ticks,scope_id,upload,download,approximate,synthetic)
    SELECT m.start_ticks,s.scope_id,m.upload,m.download,m.approximate,m.synthetic
    FROM minutes m JOIN measurement_scopes s ON s.node_key=m.node_key AND s.basis='legacy_proxy';
INSERT INTO scope_unallocated(id,start_ticks,end_ticks,scope_id,upload,download,quality)
    SELECT u.id,u.start_ticks,u.end_ticks,s.scope_id,u.upload,u.download,u.quality
    FROM unallocated u JOIN measurement_scopes s ON s.node_key=u.node_key AND s.basis='legacy_proxy';
INSERT INTO scope_gaps(id,start_ticks,end_ticks,scope_id,reason,is_open,origin)
    SELECT g.id,g.start_ticks,g.end_ticks,s.scope_id,g.reason,g.is_open,'legacy'
    FROM gaps g LEFT JOIN measurement_scopes s ON s.node_key=g.node_key AND s.basis='legacy_proxy';
INSERT INTO scope_cursors(scope_id,session_id,generation,at_ticks,upload,download,interrupted,continuity_valid)
    SELECT s.scope_id,c.session_id,c.generation,c.at_ticks,c.upload,c.download,c.interrupted,0
    FROM cursors c JOIN measurement_scopes s
        ON s.node_key=c.node_key AND s.instance_id=c.instance_id AND s.basis='legacy_proxy';
INSERT INTO scope_batches(instance_id,at_ticks,fingerprint)
    SELECT instance_id,MAX(at_ticks),NULL FROM cursors GROUP BY instance_id;

-- Keep original local tables intact. provider.db is separate and must not be migrated here.
-- v2 accounting writes scope_* only; nodes may receive new verified associations (insert-only).
-- Caller must compare every mapped row, inspect foreign_key_check and integrity_check,
-- before committing this version marker. Do not use a provider.db connection.
PRAGMA user_version=2;
