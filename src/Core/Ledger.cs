using System.Globalization;
using System.Numerics;
using Microsoft.Data.Sqlite;

namespace V2rayNMonitor.Core;

/// <summary>A single-writer, integer-byte ledger. Only UTC minute aggregates are retained.</summary>
public sealed class Ledger : IDisposable
{
    private const long Minute = TimeSpan.TicksPerMinute;
    private const long ShortInterval = 10 * TimeSpan.TicksPerSecond;
    private readonly object gate = new();
    private readonly SqliteConnection connection;
    private bool disposed;

    public Ledger(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        if (dbPath != ":memory:")
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private, Pooling = false
        }.ToString());
        try
        {
            connection.Open();
            Execute(null, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
            var version = Scalar(null, "PRAGMA user_version;");
            if (version > 1) throw new InvalidOperationException("This ledger was created by a newer application version.");
            using var transaction = connection.BeginTransaction();
            Execute(transaction, """
                CREATE TABLE IF NOT EXISTS nodes (
                    node_key TEXT PRIMARY KEY, instance_id TEXT NOT NULL,
                    node_id TEXT NOT NULL, name TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS cursors (
                    instance_id TEXT PRIMARY KEY, node_key TEXT NOT NULL REFERENCES nodes(node_key),
                    session_id TEXT NOT NULL, generation TEXT NOT NULL, at_ticks INTEGER NOT NULL,
                    upload INTEGER NOT NULL CHECK(upload >= 0), download INTEGER NOT NULL CHECK(download >= 0),
                    interrupted INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE IF NOT EXISTS minutes (
                    start_ticks INTEGER NOT NULL, node_key TEXT NOT NULL REFERENCES nodes(node_key),
                    upload INTEGER NOT NULL CHECK(typeof(upload) = 'integer' AND upload >= 0),
                    download INTEGER NOT NULL CHECK(typeof(download) = 'integer' AND download >= 0),
                    approximate INTEGER NOT NULL DEFAULT 0, synthetic INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY(start_ticks, node_key));
                CREATE TABLE IF NOT EXISTS unallocated (
                    id INTEGER PRIMARY KEY, start_ticks INTEGER NOT NULL, end_ticks INTEGER NOT NULL,
                    node_key TEXT NOT NULL REFERENCES nodes(node_key),
                    upload INTEGER NOT NULL CHECK(upload >= 0), download INTEGER NOT NULL CHECK(download >= 0),
                    quality TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS gaps (
                    id INTEGER PRIMARY KEY, start_ticks INTEGER NOT NULL, end_ticks INTEGER NOT NULL,
                    node_key TEXT, reason TEXT NOT NULL, is_open INTEGER NOT NULL DEFAULT 0);
                CREATE INDEX IF NOT EXISTS minutes_node_time ON minutes(node_key, start_ticks);
                CREATE INDEX IF NOT EXISTS unallocated_time ON unallocated(start_ticks, end_ticks);
                CREATE INDEX IF NOT EXISTS gaps_time ON gaps(start_ticks, end_ticks);
                PRAGMA user_version=1;
                """);
            transaction.Commit();
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Commits aggregates and their cumulative cursor in the same SQLite transaction.</summary>
    public string Accept(CounterSample sample, bool invalidateContinuity = false)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentException.ThrowIfNullOrWhiteSpace(sample.InstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sample.NodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sample.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sample.Generation);
        ArgumentNullException.ThrowIfNull(sample.NodeName);
        if (sample.Upload < 0 || sample.Download < 0)
            throw new ArgumentOutOfRangeException(nameof(sample), "Cumulative counters must be nonnegative integer bytes.");
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            var previous = ReadCursor(transaction, sample.InstanceId);
            var at = sample.At.UtcTicks;
            var key = Key(sample.InstanceId, sample.NodeId);
            var sameIdentity = !invalidateContinuity && previous is not null && previous.NodeKey == key &&
                previous.Session == sample.SessionId && previous.Generation == sample.Generation;
            if (previous is not null && at <= previous.At)
            {
                if (at == previous.At && sameIdentity && sample.Upload == previous.Upload && sample.Download == previous.Download)
                {
                    UpsertNode(transaction, key, sample.InstanceId, sample.NodeId, sample.NodeName);
                    transaction.Commit();
                    return "duplicate";
                }
                return "stale";
            }
            UpsertNode(transaction, key, sample.InstanceId, sample.NodeId, sample.NodeName);
            var status = "baseline";
            if (previous is not null)
            {
                var reset = sample.Upload < previous.Upload || sample.Download < previous.Download;
                if (!sameIdentity || reset)
                {
                    // A larger new-process counter is just as unsafe to subtract as a smaller one.
                    if (!previous.Interrupted)
                        InsertGap(transaction, previous.At, at, null, sameIdentity ? "counter_reset" : "identity_change");
                    status = sameIdentity ? "reset" : "baseline";
                }
                else
                {
                    var upload = checked(sample.Upload - previous.Upload);
                    var download = checked(sample.Download - previous.Download);
                    var duration = checked(at - previous.At);
                    if (previous.Interrupted || duration > ShortInterval)
                    {
                        Execute(transaction, """
                            INSERT INTO unallocated(start_ticks,end_ticks,node_key,upload,download,quality)
                            VALUES($start,$end,$key,$up,$down,'recovered-unallocated');
                            """, ("$start", previous.At), ("$end", at), ("$key", key), ("$up", upload), ("$down", download));
                        if (!previous.Interrupted) InsertGap(transaction, previous.At, at, key, "long_interval");
                        status = "unallocated";
                    }
                    else
                    {
                        Allocate(transaction, previous.At, at, key, upload, download);
                        status = "recorded";
                    }
                }
            }
            Execute(transaction, """
                INSERT INTO cursors(instance_id,node_key,session_id,generation,at_ticks,upload,download,interrupted)
                VALUES($instance,$key,$session,$generation,$at,$up,$down,0)
                ON CONFLICT(instance_id) DO UPDATE SET node_key=excluded.node_key,
                    session_id=excluded.session_id,generation=excluded.generation,at_ticks=excluded.at_ticks,
                    upload=excluded.upload,download=excluded.download,interrupted=0;
                UPDATE gaps SET end_ticks=MAX(end_ticks,$at),is_open=0 WHERE is_open=1;
                """, ("$instance", sample.InstanceId), ("$key", key), ("$session", sample.SessionId),
                ("$generation", sample.Generation), ("$at", at), ("$up", sample.Upload), ("$down", sample.Download));
            if (sample.PartialCoverage)
            {
                // Coverage gaps do not interrupt this verified counter. Persist the unknown
                // remainder alongside its bytes/cursor so history cannot present a full total.
                var start = sameIdentity && (status is "recorded" or "unallocated") ? previous!.At : at;
                var gap = sameIdentity ? Scalar(transaction, """
                    SELECT COALESCE(MAX(id),0) FROM gaps
                    WHERE node_key=$key AND reason='uncollected_outbounds' AND is_open=0 AND end_ticks=$start;
                    """, ("$key", key), ("$start", start)) : 0;
                if (gap != 0)
                    Execute(transaction, "UPDATE gaps SET end_ticks=$at WHERE id=$id;", ("$at", at), ("$id", gap));
                else InsertGap(transaction, start, at, key, "uncollected_outbounds");
            }
            transaction.Commit();
            return status;
        }
    }

    /// <summary>Coalesces an outage; a later proven-continuous delta stays explicitly unallocated.</summary>
    public void RecordGap(DateTimeOffset at, string reason, bool invalidateContinuity = false)
    {
        ArgumentNullException.ThrowIfNull(reason);
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            var ticks = at.UtcTicks;
            // Store a diagnostic category, never an arbitrary exception containing endpoints or secrets.
            var category = reason.Length is > 0 and <= 64 && reason.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
                ? reason : "collector_gap";
            if (Scalar(transaction, "SELECT COUNT(*) FROM gaps WHERE is_open=1;") == 0)
            {
                // The single active collector's last successful observation is the latest
                // persisted cursor; historical instances must not extend a new outage.
                var start = Math.Min(ticks, Scalar(transaction, "SELECT COALESCE(MAX(at_ticks),$at) FROM cursors;", ("$at", ticks)));
                InsertGap(transaction, start, ticks, null, category, true);
            }
            else
                Execute(transaction, "UPDATE gaps SET end_ticks=MAX(end_ticks,$at) WHERE is_open=1;", ("$at", ticks));
            // A known or unverified mapping boundary is not recoverable even if selection
            // later returns to the exact same IndexId/config/process. Preserve the gap and
            // history, but atomically discard the old cumulative cursor before any restart.
            Execute(transaction, invalidateContinuity ? "DELETE FROM cursors;" : "UPDATE cursors SET interrupted=1;");
            transaction.Commit();
        }
    }

    /// <summary>
    /// Known totals include recovered intervals fully contained by the query.
    /// Partially overlapping recovery is disclosed separately and never apportioned.
    /// </summary>
    public HistoryResult Query(QuerySpec query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.End <= query.Start) throw new ArgumentException("The end must be after the start.", nameof(query));
        if (query.Page < 0 || query.PageSize is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(query), "Use a nonnegative page and a page size from 1 to 1000.");
        if (query.Bucket is not ("hour" or "day" or "week" or "month" or "year"))
            throw new ArgumentException("Unknown time bucket.", nameof(query));
        var zone = TimeZoneInfo.FindSystemTimeZoneById(query.TimeZoneId);
        var parameters = new (string, object?)[]
        {
            ("$start", query.Start.UtcTicks), ("$end", query.End.UtcTicks), ("$node", query.NodeKey), ("$name", query.NodeName)
        };
        lock (gate)
        {
            ThrowIfDisposed();
            // A read transaction keeps the multiple summary/page queries at one snapshot.
            using var transaction = connection.BeginTransaction();
            var totals = new Bytes();
            var unallocated = new Bytes();
            var recovered = new Bytes();
            var partial = new Bytes();
            var unbucketed = new Bytes();
            var series = new Dictionary<string, (long FirstTick, Bytes Bytes, Bytes Recovered)>(StringComparer.Ordinal);
            var nodes = new Dictionary<string, (string Name, Bytes Bytes, Bytes Recovered)>(StringComparer.Ordinal);
            using (var command = Command(transaction, """
                SELECT m.start_ticks,m.node_key,n.name,m.upload,m.download
                FROM minutes m JOIN nodes n ON n.node_key=m.node_key
                WHERE m.start_ticks >= $start AND m.start_ticks < $end AND ($node IS NULL OR m.node_key=$node)
                    AND ($name IS NULL OR n.name=$name COLLATE BINARY)
                ORDER BY m.start_ticks;
                """, parameters))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var up = reader.GetInt64(3);
                    var down = reader.GetInt64(4);
                    totals.Add(up, down);
                    var label = BucketLabel(new DateTimeOffset(reader.GetInt64(0), TimeSpan.Zero), zone, query.Bucket);
                    if (!series.TryGetValue(label, out var bucket))
                    {
                        bucket = (reader.GetInt64(0), new Bytes(), new Bytes());
                        series.Add(label, bucket);
                    }
                    bucket.Bytes.Add(up, down);
                    var key = reader.GetString(1);
                    if (!nodes.TryGetValue(key, out var node))
                    {
                        node = (reader.GetString(2), new Bytes(), new Bytes());
                        nodes.Add(key, node);
                    }
                    node.Bytes.Add(up, down);
                }
            }
            using (var command = Command(transaction, """
                SELECT u.start_ticks,u.end_ticks,u.node_key,n.name,u.upload,u.download
                FROM unallocated u JOIN nodes n ON n.node_key=u.node_key
                WHERE u.start_ticks < $end AND u.end_ticks > $start AND ($node IS NULL OR u.node_key=$node)
                    AND ($name IS NULL OR n.name=$name COLLATE BINARY);
                """, parameters))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    var start = reader.GetInt64(0);
                    var end = reader.GetInt64(1);
                    var up = reader.GetInt64(4);
                    var down = reader.GetInt64(5);
                    unallocated.Add(up, down);
                    if (start < query.Start.UtcTicks || end > query.End.UtcTicks)
                    {
                        partial.Add(up, down);
                        continue;
                    }
                    recovered.Add(up, down);
                    totals.Add(up, down);
                    var key = reader.GetString(2);
                    if (!nodes.TryGetValue(key, out var node))
                    {
                        node = (reader.GetString(3), new Bytes(), new Bytes());
                        nodes.Add(key, node);
                    }
                    node.Bytes.Add(up, down);
                    node.Recovered.Add(up, down);
                    var label = BucketLabel(new DateTimeOffset(start, TimeSpan.Zero), zone, query.Bucket);
                    if (label != BucketLabel(new DateTimeOffset(end - 1, TimeSpan.Zero), zone, query.Bucket))
                    {
                        unbucketed.Add(up, down);
                        continue;
                    }
                    if (!series.TryGetValue(label, out var bucket))
                    {
                        bucket = (start, new Bytes(), new Bytes());
                        series.Add(label, bucket);
                    }
                    bucket.Bytes.Add(up, down);
                    bucket.Recovered.Add(up, down);
                    if (start < bucket.FirstTick) series[label] = (start, bucket.Bytes, bucket.Recovered);
                }
            var count = checked((int)Scalar(transaction, """
                SELECT (SELECT COUNT(*) FROM minutes WHERE start_ticks >= $start AND start_ticks < $end AND ($node IS NULL OR node_key=$node)
                            AND ($name IS NULL OR node_key IN (SELECT node_key FROM nodes WHERE name=$name COLLATE BINARY)))
                     + (SELECT COUNT(*) FROM unallocated WHERE start_ticks < $end AND end_ticks > $start AND ($node IS NULL OR node_key=$node)
                            AND ($name IS NULL OR node_key IN (SELECT node_key FROM nodes WHERE name=$name COLLATE BINARY)));
                """, parameters));
            var gaps = checked((int)Scalar(transaction, """
                SELECT COUNT(*) FROM gaps
                WHERE ((start_ticks < $end AND end_ticks > $start)
                    OR (start_ticks=end_ticks AND start_ticks >= $start AND start_ticks < $end))
                    AND ($node IS NULL OR node_key=$node OR node_key IS NULL)
                    AND ($name IS NULL OR node_key IS NULL OR node_key IN (SELECT node_key FROM nodes WHERE name=$name COLLATE BINARY));
                """, parameters));
            var partialCoverage = Scalar(transaction, """
                SELECT EXISTS(SELECT 1 FROM gaps
                WHERE reason='uncollected_outbounds'
                    AND ((start_ticks < $end AND end_ticks > $start)
                        OR (start_ticks=end_ticks AND start_ticks >= $start AND start_ticks < $end))
                    AND ($node IS NULL OR node_key=$node)
                    AND ($name IS NULL OR node_key IN (SELECT node_key FROM nodes WHERE name=$name COLLATE BINARY)));
                """, parameters) != 0;
            var gapDetails = new List<GapInfo>();
            using (var command = Command(transaction, """
                SELECT start_ticks,end_ticks,reason,is_open FROM gaps
                WHERE ((start_ticks < $end AND end_ticks > $start)
                    OR (start_ticks=end_ticks AND start_ticks >= $start AND start_ticks < $end))
                    AND ($node IS NULL OR node_key=$node OR node_key IS NULL)
                    AND ($name IS NULL OR node_key IS NULL OR node_key IN (SELECT node_key FROM nodes WHERE name=$name COLLATE BINARY))
                ORDER BY start_ticks DESC LIMIT 200;
                """, parameters))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    gapDetails.Add(new(Iso(reader.GetInt64(0)), Iso(reader.GetInt64(1)), reader.GetString(2), reader.GetInt64(3) != 0));
            var records = new List<TrafficRow>();
            using (var command = RecordCommand(transaction, query, query.PageSize, checked((long)query.Page * query.PageSize)))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) records.Add(ReadRecord(reader));
            transaction.Commit();
            return new HistoryResult(Iso(query.Start.UtcTicks), Iso(query.End.UtcTicks), totals.UploadText, totals.DownloadText,
                unallocated.UploadText, unallocated.DownloadText, gaps,
                series.OrderBy(x => x.Value.FirstTick).Select(x => new SeriesPoint(x.Key, x.Value.Bytes.UploadText, x.Value.Bytes.DownloadText, x.Value.Recovered.UploadText, x.Value.Recovered.DownloadText)).ToArray(),
                nodes.OrderByDescending(x => x.Value.Bytes.Upload + x.Value.Bytes.Download).ThenBy(x => x.Key, StringComparer.Ordinal)
                    .Select(x => new NodeTotal(x.Key, x.Value.Name, x.Value.Bytes.UploadText, x.Value.Bytes.DownloadText, x.Value.Recovered.UploadText, x.Value.Recovered.DownloadText)).ToArray(),
                records, count)
            {
                RecoveredUpload = recovered.UploadText, RecoveredDownload = recovered.DownloadText,
                PartialUpload = partial.UploadText, PartialDownload = partial.DownloadText,
                UnbucketedUpload = unbucketed.UploadText, UnbucketedDownload = unbucketed.DownloadText,
                Gaps = gapDetails, PartialCoverage = partialCoverage
            };
        }
    }

    /// <summary>Materializes a bounded export from one SQLite snapshot, without page-by-page reads.</summary>
    public IReadOnlyList<TrafficRow> Export(QuerySpec query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.End <= query.Start) throw new ArgumentException("The end must be after the start.", nameof(query));
        lock (gate)
        {
            ThrowIfDisposed();
            // The single SELECT fixes the snapshot; the gate keeps the collector off this connection.
            using var command = RecordCommand(null, query, 100001, 0);
            using var reader = command.ExecuteReader();
            var rows = new List<TrafficRow>();
            while (reader.Read())
            {
                if (rows.Count == 100000) throw new ArgumentException("导出最多 100000 条分钟记录，请缩小日期范围或选择单个节点。");
                rows.Add(ReadRecord(reader));
            }
            return rows;
        }
    }

    private SqliteCommand RecordCommand(SqliteTransaction? transaction, QuerySpec query, int limit, long offset) =>
        Command(transaction, """
            SELECT m.start_ticks AS start_ticks,m.start_ticks+$minute AS end_ticks,m.node_key AS node_key,n.name,m.upload,m.download,
                CASE WHEN m.synthetic=1 THEN 'synthetic' WHEN m.approximate=1 THEN 'approximate' ELSE 'measured' END AS quality,
                0 AS kind,0 AS row_id,
                EXISTS(SELECT 1 FROM gaps g WHERE g.node_key=m.node_key AND g.reason='uncollected_outbounds'
                    AND g.start_ticks < m.start_ticks+$minute AND g.end_ticks > m.start_ticks) AS partial_coverage
            FROM minutes m JOIN nodes n ON n.node_key=m.node_key
            WHERE m.start_ticks >= $start AND m.start_ticks < $end AND ($node IS NULL OR m.node_key=$node)
                AND ($name IS NULL OR n.name=$name COLLATE BINARY)
            UNION ALL
            SELECT u.start_ticks,u.end_ticks,u.node_key,n.name,u.upload,u.download,
                CASE WHEN u.start_ticks >= $start AND u.end_ticks <= $end THEN 'recovered-contained' ELSE 'recovered-partial' END,
                1 AS kind,u.id AS row_id,
                EXISTS(SELECT 1 FROM gaps g WHERE g.node_key=u.node_key AND g.reason='uncollected_outbounds'
                    AND g.start_ticks < u.end_ticks AND g.end_ticks > u.start_ticks)
            FROM unallocated u JOIN nodes n ON n.node_key=u.node_key
            WHERE u.start_ticks < $end AND u.end_ticks > $start AND ($node IS NULL OR u.node_key=$node)
                AND ($name IS NULL OR n.name=$name COLLATE BINARY)
            ORDER BY start_ticks DESC,node_key,kind,row_id LIMIT $limit OFFSET $offset;
            """,
            ("$start", query.Start.UtcTicks), ("$end", query.End.UtcTicks), ("$node", query.NodeKey), ("$name", query.NodeName),
            ("$minute", Minute), ("$limit", limit), ("$offset", offset));

    private static TrafficRow ReadRecord(SqliteDataReader reader) =>
        new(Iso(reader.GetInt64(0)), Iso(reader.GetInt64(1)), reader.GetString(2),
            reader.GetString(3), Text(reader.GetInt64(4)), Text(reader.GetInt64(5)), reader.GetString(6), reader.GetInt64(9) != 0);

    public IReadOnlyList<NodeInfo> GetNodes()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var command = Command(null, "SELECT node_key,name FROM nodes ORDER BY name COLLATE NOCASE,node_key;");
            using var reader = command.ExecuteReader();
            var nodes = new List<NodeInfo>();
            while (reader.Read()) nodes.Add(new NodeInfo(reader.GetString(0), reader.GetString(1)));
            return nodes;
        }
    }

    /// <summary>Populates only an entirely empty ledger with explicitly synthetic historical examples.</summary>
    public void SeedDemo(DateTimeOffset now)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            if (Scalar(transaction, "SELECT (SELECT COUNT(*) FROM nodes)+(SELECT COUNT(*) FROM cursors)+(SELECT COUNT(*) FROM minutes)+(SELECT COUNT(*) FROM gaps)+(SELECT COUNT(*) FROM unallocated);") != 0)
                return;
            var anchor = MinuteStart(now.UtcTicks) - Minute;
            for (var node = 0; node < 3; node++)
            {
                var nodeId = $"node-{node + 1}";
                var key = Key("synthetic-demo", nodeId);
                UpsertNode(transaction, key, "synthetic-demo", nodeId, $"Synthetic node {node + 1}");
                for (var day = 0; day < 400; day++)
                {
                    var at = checked(anchor - day * TimeSpan.TicksPerDay - node * 17 * Minute);
                    var factor = 1L + ((day * 37 + node * 23) % 89);
                    AddMinute(transaction, at, key, factor * 1_048_576, factor * (node + 3) * 8_388_608, false, true);
                }
            }
            transaction.Commit();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            connection.Dispose();
            disposed = true;
        }
    }

    private void Allocate(SqliteTransaction transaction, long start, long end, string key, long upload, long download)
    {
        var duration = end - start;
        var allocatedUpload = 0L;
        var allocatedDownload = 0L;
        var approximate = MinuteStart(start) != MinuteStart(end - 1);
        for (var from = start; from < end;)
        {
            var minute = MinuteStart(from);
            var to = Math.Min(end, minute + Minute);
            // Cumulative integer proportions put the remainder in the final slice, conserving every byte.
            var cumulativeUpload = to == end ? upload : (long)((BigInteger)upload * (to - start) / duration);
            var cumulativeDownload = to == end ? download : (long)((BigInteger)download * (to - start) / duration);
            AddMinute(transaction, minute, key, checked(cumulativeUpload - allocatedUpload),
                checked(cumulativeDownload - allocatedDownload), approximate);
            allocatedUpload = cumulativeUpload;
            allocatedDownload = cumulativeDownload;
            from = to;
        }
    }

    private void AddMinute(SqliteTransaction transaction, long start, string key, long upload, long download, bool approximate, bool synthetic = false)
    {
        var oldUpload = 0L;
        var oldDownload = 0L;
        using (var command = Command(transaction, "SELECT upload,download FROM minutes WHERE start_ticks=$at AND node_key=$key;", ("$at", start), ("$key", key)))
        using (var reader = command.ExecuteReader())
            if (reader.Read()) { oldUpload = reader.GetInt64(0); oldDownload = reader.GetInt64(1); }
        // SQLite would promote overflowing integer addition to REAL; explicitly check before writing.
        var sumUpload = checked(oldUpload + upload);
        var sumDownload = checked(oldDownload + download);
        Execute(transaction, """
            INSERT INTO minutes(start_ticks,node_key,upload,download,approximate,synthetic)
            VALUES($at,$key,$up,$down,$approximate,$synthetic)
            ON CONFLICT(start_ticks,node_key) DO UPDATE SET upload=excluded.upload,download=excluded.download,
                approximate=MAX(minutes.approximate,excluded.approximate),synthetic=MAX(minutes.synthetic,excluded.synthetic);
            """, ("$at", start), ("$key", key), ("$up", sumUpload), ("$down", sumDownload),
            ("$approximate", approximate ? 1 : 0), ("$synthetic", synthetic ? 1 : 0));
    }

    private Cursor? ReadCursor(SqliteTransaction transaction, string instance)
    {
        using var command = Command(transaction, "SELECT node_key,session_id,generation,at_ticks,upload,download,interrupted FROM cursors WHERE instance_id=$instance;", ("$instance", instance));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new Cursor(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6) != 0) : null;
    }

    private void UpsertNode(SqliteTransaction transaction, string key, string instance, string node, string name) =>
        Execute(transaction, """
            INSERT INTO nodes(node_key,instance_id,node_id,name) VALUES($key,$instance,$node,$name)
            ON CONFLICT(node_key) DO UPDATE SET name=excluded.name;
            """, ("$key", key), ("$instance", instance), ("$node", node), ("$name", name));

    private void InsertGap(SqliteTransaction transaction, long start, long end, string? key, string reason, bool open = false) =>
        Execute(transaction, "INSERT INTO gaps(start_ticks,end_ticks,node_key,reason,is_open) VALUES($start,$end,$key,$reason,$open);",
            ("$start", start), ("$end", end), ("$key", key), ("$reason", reason), ("$open", open ? 1 : 0));

    private SqliteCommand Command(SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private void Execute(SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(transaction, sql, parameters);
        command.ExecuteNonQuery();
    }

    private long Scalar(SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(transaction, sql, parameters);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
    private static string Key(string instance, string node) => Uri.EscapeDataString(instance) + "/" + Uri.EscapeDataString(node);
    private static long MinuteStart(long ticks) => ticks - ticks % Minute;
    private static string Iso(long ticks) => new DateTimeOffset(ticks, TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture);
    private static string Text(long bytes) => bytes.ToString(CultureInfo.InvariantCulture);
    internal static string BucketLabel(DateTimeOffset at, TimeZoneInfo zone, string bucket)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone);
        return bucket switch
        {
            // Include the offset so the repeated hour at DST fallback remains two distinct buckets.
            "hour" => local.ToString("yyyy-MM-dd'T'HH':00'zzz", CultureInfo.InvariantCulture),
            "week" => local.Date.AddDays(-(((int)local.DayOfWeek + 6) % 7)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "month" => local.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            "year" => local.ToString("yyyy", CultureInfo.InvariantCulture),
            _ => local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        };
    }

    private sealed record Cursor(string NodeKey, string Session, string Generation, long At, long Upload, long Download, bool Interrupted);
    private sealed class Bytes
    {
        public BigInteger Upload { get; private set; }
        public BigInteger Download { get; private set; }
        public string UploadText => Upload.ToString(CultureInfo.InvariantCulture);
        public string DownloadText => Download.ToString(CultureInfo.InvariantCulture);
        public void Add(long upload, long download) { Upload += upload; Download += download; }
    }
}
