using System.Globalization;
using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace V2rayNMonitor.Core;

/// <summary>Opt-in, single-writer v2/v3 ledger. Existing files are never initialized or migrated here.</summary>
public sealed class ScopeLedger : IDisposable
{
    private const long Minute = TimeSpan.TicksPerMinute;
    private const long ShortInterval = 10 * TimeSpan.TicksPerSecond;
    private readonly object gate = new();
    private readonly SqliteConnection connection;
    private readonly TrafficDisplayStore? display;
    public bool SupportsDisplayMetadata => display is not null;
    private bool disposed;
    internal static readonly string SchemaSql = ReadSchema("ScopeLedgerV2.sql");
    internal static readonly string ProbeUpgradeSql = ReadSchema("ScopeLedgerV3.sql");
    private static readonly string[] BusinessBases = ["legacy_proxy", "actual_member", "business", "excluded", "unknown", "routed_outbound"];
    private static readonly string[] ProbeBases = ["probe_urltest_member", "probe_urltest_business", "probe_urltest_excluded", "probe_urltest_unknown"];
    public bool SupportsProbes { get; }
    public IReadOnlyList<string> Bases => Array.AsReadOnly(SupportsProbes ? [.. BusinessBases, .. ProbeBases] : BusinessBases);

    public ScopeLedger(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false, Cache = SqliteCacheMode.Private
        }.ToString());
        try
        {
            connection.Open();
            // Check before any persistent PRAGMA: opening a v1 file must not change it.
            var version = Scalar(null, "PRAGMA user_version;");
            if (version is not (2 or 3))
                throw new InvalidOperationException("An explicitly initialized v2 or v3 history ledger is required.");
            SupportsProbes = version == 3;
            ValidateSchema(connection, null, version);
            display = TrafficDisplayStore.Open(connection);
            Execute(null, "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;");
        }
        catch { connection.Dispose(); throw; }
    }

    /// <summary>Reserves a new file; refuses every existing path, including an empty file. No migration API.</summary>
    public static ScopeLedger CreateNew(string dbPath, bool includeProbes = false, bool includeDisplayMetadata = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        if (dbPath == ":memory:") throw new ArgumentException("Use a new, explicit file path.", nameof(dbPath));
        var path = Path.GetFullPath(dbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        // Reuse v1's schema initializer only on the file just reserved. Failure leaves evidence, not deletion.
        using (new Ledger(path)) { }
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString()))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                PRAGMA foreign_keys=ON;
                SELECT (SELECT COUNT(*) FROM nodes)+(SELECT COUNT(*) FROM cursors)
                     +(SELECT COUNT(*) FROM minutes)+(SELECT COUNT(*) FROM unallocated)+(SELECT COUNT(*) FROM gaps);
                """;
            if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                throw new InvalidOperationException("New history initialization requires an empty ledger.");
            using var transaction = db.BeginTransaction();
            command.Transaction = transaction;
            command.CommandText = SchemaSql + (includeProbes ? "\n" + ProbeUpgradeSql : "") +
                (includeDisplayMetadata ? "\n" + TrafficDisplayStore.Schema : "");
            command.ExecuteNonQuery();
            ValidateSchema(db, transaction, includeProbes ? 3 : 2);
            transaction.Commit();
        }
        return new ScopeLedger(path);
    }

    public ScopeCommit Accept(ScopeBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentException.ThrowIfNullOrWhiteSpace(batch.InstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(batch.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(batch.Generation);
        ArgumentNullException.ThrowIfNull(batch.Readings);
        ArgumentNullException.ThrowIfNull(batch.RetiredScopeIds);
        // Copy caller-owned lists once; canonical ordering also fixes replay semantics.
        var readings = batch.Readings.ToArray();
        foreach (var reading in readings) Validate(reading, batch.InstanceId);
        readings = readings.OrderBy(x => x.Scope.Basis, StringComparer.Ordinal)
            .ThenBy(x => x.Scope.IdentityKind, StringComparer.Ordinal)
            .ThenBy(x => x.Scope.IdentityValue, StringComparer.Ordinal).ToArray();
        if (readings.Select(x => Identity(x.Scope)).Distinct().Count() != readings.Length)
            throw new ArgumentException("Duplicate measurement scope.", nameof(batch));
        var tagged = readings.Where(x => x.Scope.OutboundTag is not null).ToArray();
        if (tagged.Select(x => (x.Scope.Basis, x.Scope.OutboundTag)).Distinct().Count() != tagged.Length)
            throw new ArgumentException("A counter tag cannot represent two identities in the same basis.", nameof(batch));
        var retired = batch.RetiredScopeIds.Order().ToArray();
        if (retired.Any(x => x <= 0) || retired.Distinct().Count() != retired.Length)
            throw new ArgumentException("Invalid retirement list.", nameof(batch));
        var at = batch.At.UtcTicks;
        var fingerprint = XrayParser.Hash(JsonSerializer.Serialize(new
        {
            batch.InstanceId, batch.SessionId, batch.Generation, At = at, Readings = readings, Retired = retired
        }));
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            using (var command = Command(transaction,
                "SELECT at_ticks,fingerprint FROM scope_batches WHERE instance_id=$instance;", ("$instance", batch.InstanceId)))
            using (var reader = command.ExecuteReader())
                if (reader.Read() && at <= reader.GetInt64(0))
                {
                    if (at == reader.GetInt64(0) && !reader.IsDBNull(1) && reader.GetString(1) == fingerprint)
                        return new("duplicate", new Dictionary<long, string>());
                    throw new ArgumentException("Stale or conflicting complete batch.", nameof(batch));
                }

            var ids = readings.Select(x => Register(transaction, batch.InstanceId, x.Scope)).ToArray();
            var active = ids.ToHashSet();
            var directory = new HashSet<long>();
            using (var command = Command(transaction,
                "SELECT scope_id FROM measurement_scopes WHERE instance_id=$instance;", ("$instance", batch.InstanceId)))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) directory.Add(reader.GetInt64(0));
            var known = new Dictionary<long, Cursor>();
            using (var command = Command(transaction, """
                SELECT c.scope_id,c.session_id,c.generation,c.at_ticks,c.upload,c.download,c.interrupted,c.continuity_valid
                FROM scope_cursors c JOIN measurement_scopes s ON s.scope_id=c.scope_id WHERE s.instance_id=$instance;
                """, ("$instance", batch.InstanceId)))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    known.Add(reader.GetInt64(0), new(reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
                        reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6) != 0, reader.GetInt64(7) != 0));
            // Include unavailable-first and already-invalid scopes until they return.
            if (retired.Any(x => !directory.Contains(x) || active.Contains(x)) ||
                !directory.Where(x => !active.Contains(x)).Order().SequenceEqual(retired))
                throw new ArgumentException("The complete directory must explicitly retire every omitted cursor.", nameof(batch));
            if (known.Values.Any(x => at <= x.At))
                throw new ArgumentException("Batch does not advance all persisted cursors.", nameof(batch));

            foreach (var id in retired)
            {
                Gap(transaction, id, known.TryGetValue(id, out var cursor) ? cursor.At : at, at, "scope_retired", true);
                Execute(transaction, "UPDATE scope_cursors SET interrupted=1,continuity_valid=0 WHERE scope_id=$id;", ("$id", id));
            }
            var statuses = new Dictionary<long, string>();
            for (var i = 0; i < readings.Length; i++)
            {
                var reading = readings[i];
                var id = ids[i];
                known.TryGetValue(id, out var previous);
                if (reading.UnavailableReason is not null)
                {
                    display?.Observe(transaction, "scope:" + id, null, at, previous?.At);
                    Gap(transaction, id, previous?.At ?? at, at, reading.UnavailableReason, true);
                    Execute(transaction, """
                        UPDATE scope_cursors SET interrupted=1,
                            continuity_valid=CASE WHEN $invalidate=1 THEN 0 ELSE continuity_valid END WHERE scope_id=$id;
                        """, ("$invalidate", reading.InvalidateContinuity || (previous is not null &&
                            (previous.Session != batch.SessionId || previous.Generation != batch.Generation)) ? 1 : 0), ("$id", id));
                    statuses.Add(id, "unavailable");
                    continue;
                }
                var status = "baseline";
                var scope = reading.Scope;
                var snapshot = TrafficDisplayStore.Capture(scope.Basis,
                    scope.NodeId is null ? null : Key(batch.InstanceId, scope.NodeId),
                    scope.CapturedName ?? (scope.NodeId is null ? null : new(scope.Name)));
                var captured = display?.Observe(transaction, "scope:" + id, snapshot, at, previous?.At);
                var continuous = previous is not null && previous.Valid && !reading.InvalidateContinuity &&
                    previous.Session == batch.SessionId && previous.Generation == batch.Generation;
                var reset = previous is not null && (reading.Upload!.Value < previous.Upload || reading.Download!.Value < previous.Download);
                if (previous is not null && (!continuous || reset))
                {
                    Gap(transaction, id, previous.At, at, reset && continuous ? "counter_reset" : "identity_change");
                    status = reset && continuous ? "reset" : "baseline";
                }
                else if (continuous)
                {
                    var up = checked(reading.Upload!.Value - previous!.Upload);
                    var down = checked(reading.Download!.Value - previous.Download);
                    if (previous.Interrupted || at - previous.At > ShortInterval)
                    {
                        Execute(transaction, """
                            INSERT INTO scope_unallocated(start_ticks,end_ticks,scope_id,upload,download,quality)
                            VALUES($start,$end,$id,$up,$down,'recovered-unallocated');
                            """, ("$start", previous.At), ("$end", at), ("$id", id), ("$up", up), ("$down", down));
                        if (!previous.Interrupted) Gap(transaction, id, previous.At, at, "long_interval");
                        status = "unallocated";
                    }
                    else { Allocate(transaction, previous.At, at, id, up, down, captured); status = "recorded"; }
                }
                Execute(transaction, """
                    INSERT INTO scope_cursors(scope_id,session_id,generation,at_ticks,upload,download,interrupted,continuity_valid)
                    VALUES($id,$session,$generation,$at,$up,$down,0,1)
                    ON CONFLICT(scope_id) DO UPDATE SET session_id=excluded.session_id,generation=excluded.generation,
                        at_ticks=excluded.at_ticks,upload=excluded.upload,download=excluded.download,interrupted=0,continuity_valid=1;
                    UPDATE scope_gaps SET end_ticks=MAX(end_ticks,$at),is_open=0
                        WHERE scope_id=$id AND origin='collector' AND is_open=1;
                    """, ("$id", id), ("$session", batch.SessionId), ("$generation", batch.Generation),
                    ("$at", at), ("$up", reading.Upload), ("$down", reading.Download));
                if (reading.PartialCoverage)
                    Gap(transaction, id, continuous && status is "recorded" or "unallocated" ? previous!.At : at,
                        at, "uncollected_outbounds");
                statuses.Add(id, status);
            }
            Execute(transaction, """
                INSERT INTO scope_batches(instance_id,at_ticks,fingerprint) VALUES($instance,$at,$fingerprint)
                ON CONFLICT(instance_id) DO UPDATE SET at_ticks=excluded.at_ticks,fingerprint=excluded.fingerprint;
                """, ("$instance", batch.InstanceId), ("$at", at), ("$fingerprint", fingerprint));
            Execute(transaction, """
                UPDATE scope_gaps SET end_ticks=MAX(end_ticks,$at),is_open=0
                WHERE scope_id IS NULL AND origin='collector' AND is_open=1;
                """, ("$at", at));
            transaction.Commit();
            return new("committed", statuses);
        }
    }

    /// <summary>An unverified capture interrupts known scopes; no instance or node is fabricated.</summary>
    public void RecordGap(DateTimeOffset at, string reason, bool invalidateContinuity)
    {
        if (!Category(reason)) throw new ArgumentException("Use a safe diagnostic category.", nameof(reason));
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            var known = new List<(long Id, long At)>();
            using (var command = Command(transaction, """
                SELECT s.scope_id,COALESCE(c.at_ticks,$at) FROM measurement_scopes s
                LEFT JOIN scope_cursors c ON c.scope_id=s.scope_id;
                """, ("$at", at.UtcTicks)))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) known.Add((reader.GetInt64(0), reader.GetInt64(1)));
            foreach (var item in known) Gap(transaction, item.Id, Math.Min(item.At, at.UtcTicks), at.UtcTicks, reason, true);
            if (known.Count == 0)
                Execute(transaction, """
                    INSERT INTO scope_gaps(start_ticks,end_ticks,scope_id,reason,is_open,origin)
                    SELECT $at,$at,NULL,$reason,1,'collector'
                    WHERE NOT EXISTS(SELECT 1 FROM scope_gaps WHERE scope_id IS NULL AND origin='collector' AND is_open=1);
                    UPDATE scope_gaps SET end_ticks=MAX(end_ticks,$at)
                    WHERE scope_id IS NULL AND origin='collector' AND is_open=1;
                    """, ("$at", at.UtcTicks), ("$reason", reason));
            Execute(transaction, """
                UPDATE scope_cursors SET interrupted=1,continuity_valid=CASE WHEN $invalid=1 THEN 0 ELSE continuity_valid END;
                UPDATE scope_batches SET at_ticks=$at,fingerprint=NULL WHERE at_ticks<$at;
                """, ("$at", at.UtcTicks), ("$invalid", invalidateContinuity ? 1 : 0));
            transaction.Commit();
        }
    }

    public IReadOnlyList<ScopeInfo> GetScopes(string basis) => GetScopes(basis, default);

    public IReadOnlyList<ScopeInfo> GetScopes(string basis, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateQueryBasis(basis);
        EnterQueryGate(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            using var command = Command(null, """
                SELECT s.scope_id,s.instance_id,s.basis,s.identity_kind,s.identity_value,s.name,n.node_id,s.outbound_tag
                FROM measurement_scopes s LEFT JOIN nodes n ON n.node_key=s.node_key WHERE (s.basis=$basis OR ($basis='local_proxy' AND s.basis IN ('actual_member','legacy_proxy'))) ORDER BY s.scope_id;
                """, ("$basis", basis));
            using var reader = command.ExecuteReader();
            var result = new List<ScopeInfo>();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(new(reader.GetInt64(0), reader.GetString(1),
                    new(reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                        reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7))));
            }
            return result;
        }
        finally { Monitor.Exit(gate); }
    }

    public IReadOnlyList<NodeInfo> GetDisplayNodes(string basis, CancellationToken cancellationToken = default)
    {
        EnterQueryGate(cancellationToken);
        try
        {
            var directory = GetScopes(basis, cancellationToken);
            return directory.GroupBy(x => x.Scope.NodeId is null ? "scope:" + x.Id :
                    "merge:" + TrafficDisplayStore.MergeKey(x.Scope.Basis, Key(x.InstanceId, x.Scope.NodeId)), StringComparer.Ordinal)
                .Select(group =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var first = group.First();
                    var name = display?.CurrentName("scope:" + first.Id)?.OriginalName ??
                        TrafficDisplayStore.HistoricalMemberName(first.Scope.Basis, first.Scope.Name, first.Scope.OutboundTag)?.OriginalName ??
                        first.Scope.Name;
                    return new NodeInfo(group.Count() > 1 ? group.Key : "scope:" + first.Id, name);
                }).ToArray();
        }
        finally { Monitor.Exit(gate); }
    }

    private void EnterQueryGate(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (!Monitor.TryEnter(gate, 25)) cancellationToken.ThrowIfCancellationRequested();
    }

    public ScopeHistory Query(ScopeQuery query, CancellationToken cancellationToken = default,
        Action<ScopeQueryMetrics>? observer = null)
    {
        if (cancellationToken.IsCancellationRequested)
            observer?.Invoke(new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 0, 0));
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(query);
        ValidateQueryBasis(query.Basis);
        if (query.End <= query.Start || query.Bucket is not ("hour" or "day" or "week" or "month" or "year"))
            throw new ArgumentException("Invalid query interval or bucket.", nameof(query));
        if (query.Page < 0 || query.PageSize is < 1 or > 1000 || query.ScopeId is <= 0)
            throw new ArgumentOutOfRangeException(nameof(query));
        var zone = TimeZoneInfo.FindSystemTimeZoneById(query.TimeZoneId);
        var ids = query.ScopeIds is null ? null : JsonSerializer.Serialize(query.ScopeIds);
        var parameters = new (string, object?)[]
        {
            ("$basis", query.Basis), ("$id", query.ScopeId), ("$ids", ids), ("$name", null),
            ("$start", query.Start.UtcTicks), ("$end", query.End.UtcTicks)
        };
        var clock = Stopwatch.StartNew();
        var wait = TimeSpan.Zero; var held = TimeSpan.Zero; var aggregation = TimeSpan.Zero;
        long rowsRead = 0, metadataRowsRead = 0;
        var entered = false; var aggregationStarted = false;
        var phaseStart = TimeSpan.Zero;
        try
        {
            while (!(entered = Monitor.TryEnter(gate, 25))) cancellationToken.ThrowIfCancellationRequested();
            wait = clock.Elapsed; phaseStart = wait;
            var storedRows = new List<StoredTraffic>();
            var directory = new Dictionary<long, StoredScope>();
            var projections = new Dictionary<long, ScopeProjection>();
            var snapshots = new Dictionary<string, TrafficDisplayStore.Snapshot>(StringComparer.Ordinal);
            var rates = new Dictionary<string, BigInteger>(StringComparer.Ordinal);
            var rawMetadata = new List<TrafficDisplayStore.StoredPart>();
            Dictionary<TrafficDisplayStore.RecordKey, List<TrafficDisplayStore.Part>>? metadata = null;
            var matchingScopes = new HashSet<long>();
            var storedGaps = new List<StoredGap>();
            string? earliest = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                // Copy every component from one transaction before releasing the single-writer gate.
                using var transaction = connection.BeginTransaction();
                var recordParameters = parameters;
                var selectedIds = ids;
                if (query.ScopeName is not null)
                {
                    using (var command = Command(transaction, """
                        SELECT scope_id,basis,name,node_key,outbound_tag FROM measurement_scopes
                        WHERE (basis=$basis OR ($basis='local_proxy' AND basis IN ('actual_member','legacy_proxy')))
                            AND ($id IS NULL OR scope_id=$id)
                            AND ($ids IS NULL OR scope_id IN (SELECT value FROM json_each($ids)));
                        """, parameters))
                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            directory.Add(reader.GetInt64(0), new(reader.GetString(1), reader.GetString(2),
                                reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
                        }
                    var candidates = new HashSet<long>();
                    foreach (var (id, scope) in directory)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (scope.Name == query.ScopeName) { matchingScopes.Add(id); candidates.Add(id); }
                        if (Projection(id, directory, projections).Historical?.OriginalName == query.ScopeName)
                            candidates.Add(id);
                    }
                    if (display is not null)
                    {
                        var entities = directory.Keys.ToDictionary(id => "scope:" + id.ToString(CultureInfo.InvariantCulture), id => id, StringComparer.Ordinal);
                        var namedProbe = query.Basis.StartsWith("probe_urltest_", StringComparison.Ordinal);
                        rawMetadata = display.ReadRaw(transaction, namedProbe ? 0 : query.Start.UtcTicks,
                            namedProbe ? DateTimeOffset.MaxValue.UtcTicks : query.End.UtcTicks, entities.Keys.ToArray(), cancellationToken,
                            scan: query.Basis == "local_proxy" && query.ScopeId is null && query.ScopeIds is null);
                        metadataRowsRead += rawMetadata.Count;
                        foreach (var part in rawMetadata)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!snapshots.TryGetValue(part.Snapshot, out var snapshot))
                                snapshots.Add(part.Snapshot, snapshot = JsonSerializer.Deserialize<TrafficDisplayStore.Snapshot>(part.Snapshot)
                                    ?? throw new InvalidOperationException("Invalid stored display snapshot."));
                            if (snapshot.OriginalName == query.ScopeName) candidates.Add(entities[part.Key.Entity]);
                        }
                        rawMetadata.RemoveAll(part => !candidates.Contains(entities[part.Key.Entity]));
                    }
                    // Names select candidate identities only; each stored fragment is still checked below.
                    selectedIds = JsonSerializer.Serialize(candidates.Order().ToArray());
                    recordParameters = parameters.Select(x => x.Item1 == "$ids" ? ("$ids", (object?)selectedIds) : x).ToArray();
                }
                using (var command = Command(transaction, RecordsSql + " ORDER BY start_ticks DESC,scope_id,kind,row_id;", recordParameters))
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        storedRows.Add(ReadStoredTraffic(reader, directory));
                        rowsRead++;
                    }
                if (display is not null)
                {
                    if (query.ScopeName is not null)
                    {
                        if (query.Basis.StartsWith("probe_urltest_", StringComparison.Ordinal))
                        {
                            // Earliest named-probe lookup needs captured names outside the requested interval.
                            metadata = TrafficDisplayStore.Parse(rawMetadata, cancellationToken, snapshots);
                            rawMetadata.Clear();
                        }
                    }
                    else
                    {
                        var entities = new List<string>();
                        foreach (var id in directory.Keys)
                            entities.Add("scope:" + id.ToString(CultureInfo.InvariantCulture));
                        rawMetadata = display.ReadRaw(transaction, query.Start.UtcTicks, query.End.UtcTicks, entities, cancellationToken,
                            scan: query.Basis == "local_proxy" && query.ScopeId is null && query.ScopeIds is null);
                        metadataRowsRead += rawMetadata.Count;
                    }
                }
                using (var command = Command(transaction, """
                    SELECT g.scope_id,g.origin,g.start_ticks,g.end_ticks,g.reason,g.is_open
                    FROM scope_gaps g LEFT JOIN measurement_scopes s ON s.scope_id=g.scope_id
                    WHERE (g.scope_id IS NULL OR ((s.basis=$basis OR ($basis='local_proxy' AND s.basis IN ('actual_member','legacy_proxy'))) AND ($id IS NULL OR s.scope_id=$id)
                        AND ($ids IS NULL OR s.scope_id IN (SELECT value FROM json_each($ids)))))
                        AND ((g.start_ticks < $end AND (g.end_ticks > $start OR g.is_open=1))
                            OR (g.start_ticks=g.end_ticks AND g.start_ticks >= $start AND g.start_ticks < $end))
                    ORDER BY g.start_ticks,g.id;
                    """, parameters))
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        storedGaps.Add(new(reader.IsDBNull(0) ? null : reader.GetInt64(0), reader.GetString(1),
                            reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4), reader.GetInt64(5) != 0));
                    }
                if (query.Basis.StartsWith("probe_urltest_", StringComparison.Ordinal) && query.ScopeName is not null)
                {
                    using var command = Command(transaction, RecordsSql + " ORDER BY start_ticks,scope_id,kind,row_id;",
                        ("$basis", query.Basis), ("$id", query.ScopeId), ("$ids", selectedIds), ("$name", null),
                        ("$start", 0), ("$end", DateTimeOffset.MaxValue.UtcTicks));
                    using var reader = command.ExecuteReader();
                    while (earliest is null && reader.Read())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var record = ReadStoredTraffic(reader, directory);
                        rowsRead++;
                        var projection = Projection(record.ScopeId, directory, projections);
                        foreach (var row in Project(record, projection, metadata, rates))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if ((row.Display.OriginalName ?? projection.Name) == query.ScopeName) { earliest = Iso(record.Start); break; }
                        }
                    }
                }
                else if (query.Basis.StartsWith("probe_urltest_", StringComparison.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var command = Command(transaction, """
                        SELECT MIN(r.start_ticks) FROM (
                            SELECT scope_id,start_ticks FROM scope_minutes
                            UNION ALL SELECT scope_id,start_ticks FROM scope_unallocated
                        ) r JOIN measurement_scopes s ON s.scope_id=r.scope_id
                        WHERE s.basis=$basis AND ($id IS NULL OR s.scope_id=$id)
                            AND ($ids IS NULL OR s.scope_id IN (SELECT value FROM json_each($ids)));
                        """, parameters);
                    if (command.ExecuteScalar() is long ticks) earliest = Iso(ticks);
                }
                cancellationToken.ThrowIfCancellationRequested();
                transaction.Commit();
            }
            finally
            {
                held = clock.Elapsed - phaseStart;
                Monitor.Exit(gate);
                entered = false;
            }
            phaseStart = clock.Elapsed; aggregationStarted = true;
            metadata ??= TrafficDisplayStore.Parse(rawMetadata, cancellationToken, snapshots);
            rawMetadata.Clear();
            var gaps = new List<ScopeGap>(storedGaps.Count);
            foreach (var gap in storedGaps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                gaps.Add(new(gap.ScopeId, gap.Origin, Iso(gap.Start), Iso(gap.End), gap.Reason, gap.IsOpen));
            }
            var total = new Bytes(); var recovered = new Bytes(); var partial = new Bytes(); var unbucketed = new Bytes();
            var series = new Dictionary<string, (long At, Bytes Total, Bytes Recovered, TrafficDisplayStore.Summary Display)>(StringComparer.Ordinal);
            var scopes = new Dictionary<long, (string Name, Bytes Total, Bytes Recovered)>();
            var estimate = new TrafficDisplayStore.Summary();
            var scopeDisplay = new Dictionary<long, TrafficDisplayStore.Summary>();
            var scopeMerge = new Dictionary<long, string?>();
            var comparisons = new Dictionary<string, (Bytes Bytes, TrafficDisplayStore.Summary Display)>(StringComparer.Ordinal);
            var rows = new List<ScopeTrafficRow>();
            var labels = new Dictionary<long, string>();
            string Label(long ticks)
            {
                if (!labels.TryGetValue(ticks, out var label))
                    labels.Add(ticks, label = Ledger.BucketLabel(new(ticks, TimeSpan.Zero), zone, query.Bucket));
                return label;
            }
            long count = 0;
            foreach (var record in storedRows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var projection = Projection(record.ScopeId, directory, projections);
                foreach (var row in Project(record, projection, metadata, rates))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var comparisonName = row.Display.OriginalName ?? projection.Name;
                    if (query.ScopeName is not null && comparisonName != query.ScopeName) continue;
                    matchingScopes.Add(record.ScopeId);
                    var start = record.Start; var end = record.End; var id = record.ScopeId;
                    var up = row.Upload; var down = row.Download;
                    var recovery = record.Kind == 1;
                    if (!query.NonZeroRecords || up != 0 || down != 0)
                    {
                        if (count >= (long)query.Page * query.PageSize && rows.Count < query.PageSize)
                            rows.Add(new(projection.Basis, id, comparisonName, Iso(start), Iso(end),
                                Text(up), Text(down), record.Quality, record.PartialCoverage) { Display = row.Display.Result() });
                        count++;
                    }
                    if (recovery && (start < query.Start.UtcTicks || end > query.End.UtcTicks)) { partial.Add(up, down); continue; }
                    total.Add(up, down);
                    estimate.Add(row.Display);
                    if (!scopeDisplay.TryGetValue(id, out var summary)) scopeDisplay.Add(id, summary = new());
                    summary.Add(row.Display); scopeMerge[id] = row.Display.MergeKey;
                    if (!comparisons.TryGetValue(comparisonName, out var comparison)) comparisons.Add(comparisonName, comparison = (new(), new()));
                    comparison.Bytes.Add(up, down);
                    comparison.Display.Add(row.Display);
                    if (recovery) recovered.Add(up, down);
                    if (!scopes.TryGetValue(id, out var scope))
                        scopes.Add(id, scope = (comparisonName, new Bytes(), new Bytes()));
                    scope.Total.Add(up, down);
                    if (recovery) scope.Recovered.Add(up, down);
                    var label = Label(start);
                    if (recovery && label != Label(end - 1))
                    { unbucketed.Add(up, down); continue; }
                    if (!series.TryGetValue(label, out var bucket))
                        series.Add(label, bucket = (start, new Bytes(), new Bytes(), new()));
                    bucket.Total.Add(up, down);
                    bucket.Display.Add(row.Display);
                    if (recovery) bucket.Recovered.Add(up, down);
                    if (start < bucket.At) series[label] = (start, bucket.Total, bucket.Recovered, bucket.Display);
                }
            }
            if (query.ScopeName is not null)
                gaps.RemoveAll(x => x.ScopeId is { } id && !matchingScopes.Contains(id));
            cancellationToken.ThrowIfCancellationRequested();
            return new(query.Basis, total.Up, total.Down, recovered.Up, recovered.Down, partial.Up, partial.Down,
                unbucketed.Up, unbucketed.Down,
                series.OrderBy(x => x.Value.At).Select(x => new SeriesPoint(x.Key, x.Value.Total.Up, x.Value.Total.Down,
                    x.Value.Recovered.Up, x.Value.Recovered.Down) { Display = x.Value.Display.Result() }).ToArray(),
                scopes.OrderBy(x => x.Key).Select(x => new ScopeTotal(x.Key, x.Value.Name, x.Value.Total.Up,
                    x.Value.Total.Down, x.Value.Recovered.Up, x.Value.Recovered.Down)
                    { Display = scopeDisplay[x.Key].Result(scopeMerge[x.Key]) }).ToArray(), rows, count, gaps)
            {
                EarliestRecord = earliest, Display = estimate.Result(), DisplayMetadataEnabled = SupportsDisplayMetadata,
                ComparisonNodes = comparisons.Select(x => new NodeTotal(x.Key, x.Key, x.Value.Bytes.Up, x.Value.Bytes.Down)
                    { Display = x.Value.Display.Result() }).ToArray()
            };
        }
        finally
        {
            if (entered) Monitor.Exit(gate);
            if (wait == TimeSpan.Zero) wait = clock.Elapsed;
            if (aggregationStarted) aggregation = clock.Elapsed - phaseStart;
            observer?.Invoke(new(wait, held, aggregation, clock.Elapsed, rowsRead, metadataRowsRead));
        }
    }

    private readonly record struct StoredTraffic(long Start, long End, long ScopeId, long Upload, long Download,
        string Quality, int Kind, bool PartialCoverage);
    private readonly record struct StoredGap(long? ScopeId, string Origin, long Start, long End, string Reason, bool IsOpen);
    private sealed record StoredScope(string Basis, string Name, string? NodeKey, string? Tag);
    private sealed record ScopeProjection(string Basis, string Name, string Entity, string? MergeKey, CapturedNodeName? Historical);

    private static StoredTraffic ReadStoredTraffic(SqliteDataReader reader, Dictionary<long, StoredScope> directory)
    {
        var id = reader.GetInt64(2);
        if (!directory.ContainsKey(id))
            directory.Add(id, new(reader.GetString(10), reader.GetString(3),
                reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetString(12)));
        return new(reader.GetInt64(0), reader.GetInt64(1), id, reader.GetInt64(4), reader.GetInt64(5),
            reader.GetString(6), reader.GetInt32(7), reader.GetInt64(9) != 0);
    }

    private static ScopeProjection Projection(long id, Dictionary<long, StoredScope> directory,
        Dictionary<long, ScopeProjection> projections)
    {
        if (!projections.TryGetValue(id, out var projection))
        {
            var scope = directory[id];
            projections.Add(id, projection = new(scope.Basis, scope.Name, "scope:" + id.ToString(CultureInfo.InvariantCulture),
                scope.NodeKey is null ? null : TrafficDisplayStore.MergeKey(scope.Basis, scope.NodeKey),
                TrafficDisplayStore.HistoricalMemberName(scope.Basis, scope.Name, scope.Tag)));
        }
        return projection;
    }

    private static IEnumerable<TrafficDisplayStore.ValueSlice> Project(StoredTraffic row, ScopeProjection scope,
        Dictionary<TrafficDisplayStore.RecordKey, List<TrafficDisplayStore.Part>>? metadata,
        Dictionary<string, BigInteger> rates)
    {
        var parts = metadata?.GetValueOrDefault(new(scope.Entity, row.Kind, row.Start, row.End));
        foreach (var part in TrafficDisplayStore.SplitValues(parts, row.Upload, row.Download, scope.MergeKey, rates))
            yield return part.Display.OriginalName is null && scope.Historical is not null
                ? part with { Display = part.Display with { OriginalName = scope.Historical.OriginalName, GroupLabel = scope.Historical.GroupLabel } }
                : part;
    }

    private const string RecordsSql = """
        WITH coverage_gaps AS MATERIALIZED (
            SELECT scope_id,start_ticks,end_ticks FROM scope_gaps WHERE reason='uncollected_outbounds'
        )
        SELECT m.start_ticks AS start_ticks,m.start_ticks+600000000 AS end_ticks,m.scope_id AS scope_id,s.name,
            m.upload,m.download,
            CASE WHEN m.synthetic=1 THEN 'synthetic' WHEN m.approximate=1 THEN 'approximate' ELSE 'measured' END AS quality,
            0 AS kind,0 AS row_id,
            EXISTS(SELECT 1 FROM coverage_gaps g WHERE g.scope_id=m.scope_id
                AND g.start_ticks < m.start_ticks+600000000 AND g.end_ticks > m.start_ticks) AS partial_coverage,
            s.basis AS basis,s.node_key AS node_key,s.outbound_tag AS outbound_tag
        FROM scope_minutes m JOIN measurement_scopes s ON s.scope_id=m.scope_id
        WHERE (s.basis=$basis OR ($basis='local_proxy' AND s.basis IN ('actual_member','legacy_proxy'))) AND ($id IS NULL OR s.scope_id=$id) AND ($name IS NULL OR s.name=$name COLLATE BINARY)
            AND ($ids IS NULL OR s.scope_id IN (SELECT value FROM json_each($ids)))
            AND m.start_ticks >= $start AND m.start_ticks < $end
        UNION ALL
        SELECT u.start_ticks,u.end_ticks,u.scope_id,s.name,u.upload,u.download,
            CASE WHEN u.start_ticks >= $start AND u.end_ticks <= $end THEN 'recovered-contained' ELSE 'recovered-partial' END,
            1,u.id,
            EXISTS(SELECT 1 FROM coverage_gaps g WHERE g.scope_id=u.scope_id
                AND g.start_ticks < u.end_ticks AND g.end_ticks > u.start_ticks), s.basis,s.node_key,s.outbound_tag
        FROM scope_unallocated u JOIN measurement_scopes s ON s.scope_id=u.scope_id
        WHERE (s.basis=$basis OR ($basis='local_proxy' AND s.basis IN ('actual_member','legacy_proxy'))) AND ($id IS NULL OR s.scope_id=$id) AND ($name IS NULL OR s.name=$name COLLATE BINARY)
            AND ($ids IS NULL OR s.scope_id IN (SELECT value FROM json_each($ids)))
            AND u.start_ticks < $end AND u.end_ticks > $start
        """;

    public IReadOnlyList<ScopeTrafficRow> Export(ScopeQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateQueryBasis(query.Basis);
        if (query.End <= query.Start || query.ScopeId is <= 0) throw new ArgumentException("Invalid export interval or scope.");
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            var metadata = display?.Read(transaction, query.Start.UtcTicks, query.End.UtcTicks);
            using var command = Command(transaction, RecordsSql + " ORDER BY start_ticks DESC,scope_id,kind,row_id;",
                ("$basis", query.Basis), ("$id", query.ScopeId), ("$name", null),
                ("$ids", query.ScopeIds is null ? null : JsonSerializer.Serialize(query.ScopeIds)),
                ("$start", query.Start.UtcTicks), ("$end", query.End.UtcTicks));
            using var reader = command.ExecuteReader();
            var rows = new List<ScopeTrafficRow>();
            while (reader.Read())
            foreach (var row in ReadDisplayRecords(reader, metadata))
            {
                if (query.ScopeIds is not null && !query.ScopeIds.Contains(row.ScopeId)) continue;
                if (query.ScopeName is not null && (row.Display!.OriginalName ?? row.Name) != query.ScopeName) continue;
                if (rows.Count == 100000) throw new ArgumentException("导出最多 100000 条记录，请缩小范围。");
                rows.Add(row);
            }
            return rows;
        }
    }

    private static IEnumerable<ScopeTrafficRow> ReadDisplayRecords(SqliteDataReader reader,
        Dictionary<TrafficDisplayStore.RecordKey, List<TrafficDisplayStore.Part>>? metadata)
    {
        var id = reader.GetInt64(2); var start = reader.GetInt64(0); var end = reader.GetInt64(1);
        var up = Text(reader.GetInt64(4)); var down = Text(reader.GetInt64(5)); var basis = reader.GetString(10);
        var parts = metadata?.GetValueOrDefault(new("scope:" + id, reader.GetInt32(7), start, end));
        var mergeKey = reader.IsDBNull(11) ? null : TrafficDisplayStore.MergeKey(basis, reader.GetString(11));
        var historical = TrafficDisplayStore.HistoricalMemberName(basis, reader.GetString(3),
            reader.IsDBNull(12) ? null : reader.GetString(12));
        foreach (var part in TrafficDisplayStore.Split(parts, up, down, mergeKey))
            yield return new(basis, id, part.Name ?? historical?.OriginalName ?? reader.GetString(3), Iso(start), Iso(end),
                part.Upload, part.Download, reader.GetString(6), reader.GetInt64(9) != 0)
            {
                Display = part.Name is null && historical is not null
                    ? part.Display with { OriginalName = historical.OriginalName, Groups = [historical.GroupLabel!] }
                    : part.Display
            };
    }

    private long Register(SqliteTransaction transaction, string instance, MeasurementScope scope)
    {
        var nodeKey = scope.NodeId is null ? null : Key(instance, scope.NodeId);
        if (nodeKey is not null)
            Execute(transaction, """
                INSERT INTO nodes(node_key,instance_id,node_id,name) VALUES($key,$instance,$node,$name)
                ON CONFLICT(node_key) DO NOTHING;
                """, ("$key", nodeKey), ("$instance", instance), ("$node", scope.NodeId), ("$name", scope.Name));
        if (nodeKey is not null && Scalar(transaction,
            "SELECT COUNT(*) FROM nodes WHERE node_key=$key AND instance_id=$instance AND node_id=$node;",
            ("$key", nodeKey), ("$instance", instance), ("$node", scope.NodeId)) != 1)
            throw new InvalidOperationException("The verified node association conflicts with stored identity.");
        Execute(transaction, """
            INSERT INTO measurement_scopes(instance_id,basis,identity_kind,identity_value,node_key,outbound_tag,name)
            VALUES($instance,$basis,$kind,$value,$key,$tag,$name)
            ON CONFLICT(instance_id,basis,identity_kind,identity_value) DO UPDATE SET
                name=excluded.name,outbound_tag=excluded.outbound_tag
                WHERE measurement_scopes.node_key IS excluded.node_key;
            """, ("$instance", instance), ("$basis", scope.Basis), ("$kind", scope.IdentityKind),
            ("$value", scope.IdentityValue), ("$key", nodeKey), ("$tag", scope.OutboundTag), ("$name", scope.Name));
        using var command = Command(transaction, """
            SELECT scope_id,node_key FROM measurement_scopes
            WHERE instance_id=$instance AND basis=$basis AND identity_kind=$kind AND identity_value=$value;
            """, ("$instance", instance), ("$basis", scope.Basis), ("$kind", scope.IdentityKind), ("$value", scope.IdentityValue));
        using var reader = command.ExecuteReader();
        if (!reader.Read() || (reader.IsDBNull(1) ? null : reader.GetString(1)) != nodeKey)
            throw new InvalidOperationException("Conflicting stored scope association.");
        return reader.GetInt64(0);
    }

    private void Allocate(SqliteTransaction transaction, long start, long end, long id, long upload, long download, string? captured)
    {
        long allocatedUp = 0, allocatedDown = 0;
        var approximate = MinuteStart(start) != MinuteStart(end - 1);
        for (var from = start; from < end;)
        {
            var minute = MinuteStart(from);
            var to = Math.Min(end, minute + Minute);
            var cumulativeUp = to == end ? upload : (long)((BigInteger)upload * (to - start) / (end - start));
            var cumulativeDown = to == end ? download : (long)((BigInteger)download * (to - start) / (end - start));
            var sliceUp = cumulativeUp - allocatedUp; var sliceDown = cumulativeDown - allocatedDown;
            long oldUp = 0, oldDown = 0;
            using (var command = Command(transaction,
                "SELECT upload,download FROM scope_minutes WHERE start_ticks=$at AND scope_id=$id;", ("$at", minute), ("$id", id)))
            using (var reader = command.ExecuteReader())
                if (reader.Read()) { oldUp = reader.GetInt64(0); oldDown = reader.GetInt64(1); }
            Execute(transaction, """
                INSERT INTO scope_minutes(start_ticks,scope_id,upload,download,approximate,synthetic)
                VALUES($at,$id,$up,$down,$approximate,0)
                ON CONFLICT(start_ticks,scope_id) DO UPDATE SET upload=excluded.upload,download=excluded.download,
                    approximate=MAX(scope_minutes.approximate,excluded.approximate);
                """, ("$at", minute), ("$id", id), ("$up", checked(oldUp + sliceUp)),
                ("$down", checked(oldDown + sliceDown)), ("$approximate", approximate ? 1 : 0));
            display?.Add(transaction, "scope:" + id, minute, minute + Minute, sliceUp, sliceDown, captured);
            allocatedUp = cumulativeUp; allocatedDown = cumulativeDown; from = to;
        }
    }

    private void Gap(SqliteTransaction transaction, long id, long start, long end, string reason, bool open = false)
    {
        if (open && Scalar(transaction,
            "SELECT COUNT(*) FROM scope_gaps WHERE scope_id=$id AND origin='collector' AND is_open=1;", ("$id", id)) != 0)
            Execute(transaction, """
                UPDATE scope_gaps SET end_ticks=MAX(end_ticks,$end)
                WHERE scope_id=$id AND origin='collector' AND is_open=1;
                """, ("$id", id), ("$end", end));
        else if (!open && reason == "uncollected_outbounds" && Scalar(transaction, """
            SELECT COUNT(*) FROM scope_gaps WHERE scope_id=$id AND origin='collector'
                AND is_open=0 AND reason=$reason AND end_ticks=$start;
            """, ("$id", id), ("$reason", reason), ("$start", start)) != 0)
            Execute(transaction, """
                UPDATE scope_gaps SET end_ticks=$end WHERE scope_id=$id AND origin='collector'
                    AND is_open=0 AND reason=$reason AND end_ticks=$start;
                """, ("$id", id), ("$reason", reason), ("$start", start), ("$end", end));
        else Execute(transaction, """
            INSERT INTO scope_gaps(start_ticks,end_ticks,scope_id,reason,is_open,origin)
            VALUES($start,$end,$id,$reason,$open,'collector');
            """, ("$start", start), ("$end", end), ("$id", id), ("$reason", reason), ("$open", open ? 1 : 0));
    }

    private void Validate(ScopeReading reading, string instance)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(reading.Scope);
        var scope = reading.Scope;
        ValidateBasis(scope.Basis);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.IdentityValue);
        ArgumentNullException.ThrowIfNull(scope.Name);
        if (reading.UnavailableReason is null
            ? reading.Upload is null || reading.Download is null || reading.Upload < 0 || reading.Download < 0
            : reading.Upload is not null || reading.Download is not null || !Category(reading.UnavailableReason))
            throw new ArgumentException("Supply one complete nonnegative pair or a safe unavailable category.");
        var valid = scope.Basis switch
        {
            "legacy_proxy" => scope.IdentityKind == "legacy_node" && !string.IsNullOrWhiteSpace(scope.NodeId) &&
                scope.IdentityValue == Key(instance, scope.NodeId) && scope.OutboundTag is null,
            "actual_member" or "probe_urltest_member" when scope.IdentityKind == "index_id" => !string.IsNullOrWhiteSpace(scope.NodeId) &&
                scope.IdentityValue == scope.NodeId && !string.IsNullOrWhiteSpace(scope.OutboundTag),
            "actual_member" or "routed_outbound" or "probe_urltest_member" => scope.IdentityKind == "outbound" && scope.NodeId is null &&
                OutboundIdentity(scope.IdentityValue, scope.OutboundTag),
            _ => scope.IdentityKind == "aggregate" && scope.IdentityValue == scope.Basis &&
                scope.NodeId is null && scope.OutboundTag is null
        };
        if (!valid) throw new ArgumentException("Invalid measurement basis or verified identity association.");
    }

    private static bool OutboundIdentity(string value, string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return false;
        try
        {
            var pair = JsonSerializer.Deserialize<string[]>(value);
            return pair is { Length: 2 } && pair[0] is { Length: 64 } && pair[0].All(Uri.IsHexDigit) &&
                pair[0] == pair[0].ToUpperInvariant() && pair[1] == tag && JsonSerializer.Serialize(pair) == value;
        }
        catch (JsonException) { return false; }
    }

    private static bool Category(string reason) => reason.Length is > 0 and <= 64 &&
        reason.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    private static (string, string, string) Identity(MeasurementScope scope) =>
        (scope.Basis, scope.IdentityKind, scope.IdentityValue);
    // A read-only view across successive core sessions; never a persisted measurement basis.
    private void ValidateQueryBasis(string basis)
    {
        if (basis != "local_proxy") ValidateBasis(basis);
    }

    private void ValidateBasis(string basis)
    {
        if (!BusinessBases.Contains(basis, StringComparer.Ordinal) &&
            !(SupportsProbes && ProbeBases.Contains(basis, StringComparer.Ordinal)))
            throw new ArgumentException("Select exactly one supported measurement basis.", nameof(basis));
    }

    internal static void ValidateSchema(SqliteConnection db, SqliteTransaction? transaction, long version)
    {
        if (version is not (2 or 3)) throw new InvalidOperationException("Unsupported history version.");
        var schema = SchemaSql;
        if (version == 3)
        {
            var directory = Regex.Match(ProbeUpgradeSql, @"CREATE TABLE measurement_scopes_v3 [\s\S]*?;").Value
                .Replace("CREATE TABLE measurement_scopes_v3", "CREATE TABLE measurement_scopes", StringComparison.Ordinal);
            schema = Regex.Replace(schema, @"CREATE TABLE measurement_scopes [\s\S]*?;", _ => directory);
        }
        foreach (Match match in Regex.Matches(schema, @"CREATE (?:TABLE|(?:UNIQUE )?INDEX) (\w+)[\s\S]*?;"))
        {
            using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT sql FROM sqlite_master WHERE name=$name;";
            command.Parameters.AddWithValue("$name", match.Groups[1].Value);
            var actual = command.ExecuteScalar() as string;
            if (actual is null || NormalizeSql(actual) != NormalizeSql(match.Value))
                throw new InvalidOperationException("Unsupported history schema for its declared version.");
        }
        ScopeLedgerMigration.CheckIntegrity(db, transaction);
    }

    private static string NormalizeSql(string sql) => Regex.Replace(sql.Trim().TrimEnd(';'), @"\s+", " ")
        .Replace("CREATE TABLE \"measurement_scopes\"", "CREATE TABLE measurement_scopes", StringComparison.Ordinal);
    private static string ReadSchema(string resource)
    {
        using var stream = typeof(ScopeLedger).Assembly.GetManifestResourceStream("V2rayNMonitor.Core." + resource)
            ?? throw new InvalidOperationException("Missing history schema resource.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    private SqliteCommand Command(SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    private void Execute(SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(transaction, sql, parameters); command.ExecuteNonQuery();
    }
    private long Scalar(SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(transaction, sql, parameters);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; connection.Dispose(); disposed = true; }
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
    private static string Key(string instance, string node) => Uri.EscapeDataString(instance) + "/" + Uri.EscapeDataString(node);
    private static long MinuteStart(long ticks) => ticks - ticks % Minute;
    private static string Iso(long ticks) => new DateTimeOffset(ticks, TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture);
    private static string Text(long bytes) => bytes.ToString(CultureInfo.InvariantCulture);
    private sealed record Cursor(string Session, string Generation, long At, long Upload, long Download, bool Interrupted, bool Valid);
    private sealed class Bytes
    {
        private BigInteger upload, download;
        public string Up => upload.ToString(CultureInfo.InvariantCulture);
        public string Down => download.ToString(CultureInfo.InvariantCulture);
        public void Add(long up, long down) { upload += up; download += down; }
    }
}
