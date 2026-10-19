-- S5.8 explicit v2 -> v3 upgrade; only the measurement directory changes.
-- Caller: stop writers, verify v2, take SQLite backup, disable foreign_keys
-- BEFORE beginning ONE transaction, compare all old rows and unchanged DDL,
-- run integrity_check/foreign_key_check, then commit. Re-enable foreign_keys.

CREATE TABLE measurement_scopes_v3 (
    scope_id INTEGER PRIMARY KEY,
    instance_id TEXT NOT NULL CHECK(length(instance_id) > 0),
    basis TEXT NOT NULL CHECK(basis IN
        ('legacy_proxy','actual_member','business','excluded','unknown','routed_outbound',
         'probe_urltest_member','probe_urltest_business','probe_urltest_excluded','probe_urltest_unknown')),
    identity_kind TEXT NOT NULL CHECK(identity_kind IN
        ('legacy_node','index_id','outbound','aggregate')),
    identity_value TEXT NOT NULL CHECK(length(identity_value) > 0),
    node_key TEXT REFERENCES nodes(node_key),
    outbound_tag TEXT,
    name TEXT NOT NULL,
    UNIQUE(instance_id,basis,identity_kind,identity_value),
    CHECK(
        (basis='legacy_proxy' AND identity_kind='legacy_node' AND node_key IS NOT NULL AND outbound_tag IS NULL)
        OR (basis IN ('actual_member','probe_urltest_member') AND identity_kind='index_id'
            AND node_key IS NOT NULL AND outbound_tag IS NOT NULL)
        OR (basis IN ('actual_member','routed_outbound','probe_urltest_member') AND identity_kind='outbound'
            AND node_key IS NULL AND outbound_tag IS NOT NULL)
        OR (basis IN ('business','excluded','unknown',
                     'probe_urltest_business','probe_urltest_excluded','probe_urltest_unknown')
            AND identity_kind='aggregate' AND identity_value=basis
            AND node_key IS NULL AND outbound_tag IS NULL)
    )
);
INSERT INTO measurement_scopes_v3
    (scope_id,instance_id,basis,identity_kind,identity_value,node_key,outbound_tag,name)
    SELECT scope_id,instance_id,basis,identity_kind,identity_value,node_key,outbound_tag,name
    FROM measurement_scopes;
DROP TABLE measurement_scopes;
ALTER TABLE measurement_scopes_v3 RENAME TO measurement_scopes;
PRAGMA user_version=3;
