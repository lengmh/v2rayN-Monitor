using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace V2rayNMonitor.Core;

public sealed partial class ScopeLedger
{
    // Optional derived state. Normal opening never creates it; raw minutes remain authoritative.
    internal sealed class HistoricalSummaries(string path, TimeProvider time) : IDisposable
    {
        private const long Day = TimeSpan.TicksPerDay, Offset = 8 * TimeSpan.TicksPerHour;
        private const string Rule = "daily-v1|Asia/Shanghai|UTC+08-after-1992|exact-display-state|nonzero-top50";
        private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
        private readonly CancellationTokenSource stopping = new();
        private readonly object workGate = new();
        private Task worker = Task.CompletedTask;
        private BuildRequest? pending;
        private bool running, disposed;
        internal sealed record BuildRequest(long[] Days, long[] ForceDays);
        private sealed record Rank(long Start, long Id, int Kind, long Row, int Part) : IComparable<Rank>
        {
            public int CompareTo(Rank? other)
            {
                if (other is null) return -1;
                var c = other.Start.CompareTo(Start); if (c != 0) return c;
                c = Id.CompareTo(other.Id); if (c != 0) return c;
                c = Kind.CompareTo(other.Kind); if (c != 0) return c;
                c = Row.CompareTo(other.Row); return c != 0 ? c : Part.CompareTo(other.Part);
            }
        }
        private sealed record Detail(Rank Rank, ScopeTrafficRow Row);
        private sealed record ScopePart(long Id, string Name, string Up, string Down,
            TrafficDisplayStore.Summary.State Display, string? Merge, Rank First, Rank Last);
        private sealed record NamedPart(string Name, string Up, string Down,
            TrafficDisplayStore.Summary.State Display, Rank First);
        private sealed record Piece(long Day, long At, string Up, string Down, long Count,
            TrafficDisplayStore.Summary.State Display, ScopePart[] Scopes, NamedPart[] Names, Detail[] Details);
        private sealed record Raw(StoredTraffic Traffic, long Row);
        private sealed record Chunk(List<Raw> Rows, Dictionary<long, StoredScope> Directory,
            List<TrafficDisplayStore.StoredPart> Metadata);
        private sealed record Entry(long Day, long Revision, long? StoredRevision, string? Rule, string? Payload, string? Hash);
        private sealed class Amount
        {
            public BigInteger Up, Down;
            public void Add(long up, long down) { Up += up; Down += down; }
            public void Add(string up, string down) { Up += BigInteger.Parse(up, CultureInfo.InvariantCulture); Down += BigInteger.Parse(down, CultureInfo.InvariantCulture); }
            public string U => Up.ToString(CultureInfo.InvariantCulture);
            public string D => Down.ToString(CultureInfo.InvariantCulture);
        }
        private sealed class ScopeSum(string name, Rank rank, string? merge)
        {
            public readonly Amount Total = new(), Recovered = new();
            public readonly TrafficDisplayStore.Summary Display = new();
            public string Name = name;
            public Rank First = rank, Last = rank;
            public string? Merge = merge;
            public void Order(string value, Rank first, Rank last, string? key)
            {
                if (first.CompareTo(First) < 0) { First = first; Name = value; }
                if (last.CompareTo(Last) > 0) { Last = last; Merge = key; }
            }
        }
        private sealed class NamedSum(Rank first)
        {
            public Rank First = first;
            public readonly Amount Total = new();
            public readonly TrafficDisplayStore.Summary Display = new();
        }
        private sealed class BucketSum(long at)
        {
            public long At = at;
            public readonly Amount Total = new(), Recovered = new();
            public readonly TrafficDisplayStore.Summary Display = new();
        }
        private sealed class Aggregate(ScopeQuery query)
        {
            public readonly Amount Total = new(), Recovered = new(), Partial = new(), Unbucketed = new();
            public readonly TrafficDisplayStore.Summary Display = new();
            public readonly Dictionary<long, ScopeSum> Scopes = [];
            public readonly Dictionary<string, NamedSum> Names = new(StringComparer.Ordinal);
            public readonly Dictionary<string, BucketSum> Series = new(StringComparer.Ordinal);
            public readonly List<Detail> Details = [];
            public long Count;
            private string Label(long ticks) => Ledger.BucketLabel(new(ticks, TimeSpan.Zero), Zone, query.Bucket);
            public ScopeSum Scope(long id, string name, Rank first, Rank last, string? merge)
            {
                if (!Scopes.TryGetValue(id, out var sum)) Scopes.Add(id, sum = new(name, first, merge));
                sum.Order(name, first, last, merge); return sum;
            }
            public NamedSum Name(string name, Rank first)
            {
                if (!Names.TryGetValue(name, out var sum)) Names.Add(name, sum = new(first));
                if (first.CompareTo(sum.First) < 0) sum.First = first;
                return sum;
            }
            private BucketSum Bucket(long at)
            {
                var label = Label(at);
                if (!Series.TryGetValue(label, out var sum)) Series.Add(label, sum = new(at));
                sum.At = Math.Min(sum.At, at); return sum;
            }
            public void Add(Piece piece)
            {
                Total.Add(piece.Up, piece.Down); Display.Merge(piece.Display); Count += piece.Count;
                foreach (var p in piece.Scopes)
                {
                    var sum = Scope(p.Id, p.Name, p.First, p.Last, p.Merge);
                    sum.Total.Add(p.Up, p.Down); sum.Display.Merge(p.Display);
                }
                foreach (var p in piece.Names)
                {
                    var sum = Name(p.Name, p.First); sum.Total.Add(p.Up, p.Down); sum.Display.Merge(p.Display);
                }
                if (piece.Scopes.Length != 0)
                {
                    var bucket = Bucket(piece.At); bucket.Total.Add(piece.Up, piece.Down); bucket.Display.Merge(piece.Display);
                }
                Details.AddRange(piece.Details);
            }
            public void Add(Raw raw, ScopeProjection projection, TrafficDisplayStore.ValueSlice value, int part)
            {
                var r = raw.Traffic;
                var rank = new Rank(r.Start, r.ScopeId, r.Kind, raw.Row, part);
                var name = value.Display.OriginalName ?? projection.Name;
                var up = value.Upload; var down = value.Download;
                if (up != 0 || down != 0)
                {
                    Count++;
                    // Bound raw-tail detail retention too, rather than allocating all returned rows.
                    if (Details.Count < 50)
                        Details.Add(new(rank, new(projection.Basis, r.ScopeId, name, Iso(r.Start), Iso(r.End),
                            Text(up), Text(down), r.Quality, r.PartialCoverage) { Display = value.Display.Result() }));
                }
                var recovery = r.Kind == 1;
                if (recovery && (r.Start < query.Start.UtcTicks || r.End > query.End.UtcTicks)) { Partial.Add(up, down); return; }
                Total.Add(up, down); Display.Add(value.Display);
                var scope = Scope(r.ScopeId, name, rank, rank, value.Display.MergeKey);
                scope.Total.Add(up, down); scope.Display.Add(value.Display);
                var named = Name(name, rank); named.Total.Add(up, down); named.Display.Add(value.Display);
                if (recovery) { Recovered.Add(up, down); scope.Recovered.Add(up, down); }
                if (recovery && Label(r.Start) != Label(r.End - 1)) { Unbucketed.Add(up, down); return; }
                var bucket = Bucket(r.Start); bucket.Total.Add(up, down); bucket.Display.Add(value.Display);
                if (recovery) bucket.Recovered.Add(up, down);
            }
            public Piece Save(long day)
            {
                return new(day, Series.Count == 0 ? day : Series.Values.Min(x => x.At), Total.U, Total.D, Count,
                    Display.Save(), Scopes.Select(x => new ScopePart(x.Key, x.Value.Name, x.Value.Total.U, x.Value.Total.D,
                        x.Value.Display.Save(), x.Value.Merge, x.Value.First, x.Value.Last)).ToArray(),
                    Names.Select(x => new NamedPart(x.Key, x.Value.Total.U, x.Value.Total.D, x.Value.Display.Save(), x.Value.First)).ToArray(),
                    Details.OrderBy(x => x.Rank).Take(50).ToArray());
            }
            public ScopeHistory Finish(List<ScopeGap> gaps, bool metadata)
            {
                return new(query.Basis, Total.U, Total.D, Recovered.U, Recovered.D, Partial.U, Partial.D, Unbucketed.U, Unbucketed.D,
                    Series.OrderBy(x => x.Value.At).Select(x => new SeriesPoint(x.Key, x.Value.Total.U, x.Value.Total.D,
                        x.Value.Recovered.U, x.Value.Recovered.D) { Display = x.Value.Display.Result() }).ToArray(),
                    Scopes.OrderBy(x => x.Key).Select(x => new ScopeTotal(x.Key, x.Value.Name, x.Value.Total.U, x.Value.Total.D,
                        x.Value.Recovered.U, x.Value.Recovered.D) { Display = x.Value.Display.Result(x.Value.Merge) }).ToArray(),
                    Details.OrderBy(x => x.Rank).Take(query.PageSize).Select(x => x.Row).ToArray(), Count, gaps)
                {
                    Display = Display.Result(), DisplayMetadataEnabled = metadata,
                    ComparisonNodes = Names.OrderBy(x => x.Value.First).Select(x => new NodeTotal(x.Key, x.Key,
                        x.Value.Total.U, x.Value.Total.D) { Display = x.Value.Display.Result() }).ToArray()
                };
            }
        }
        internal static readonly IReadOnlyDictionary<string, string> Definitions = CreateDefinitions();
        internal static string SchemaSql => string.Join("\n", Definitions.Values) +
            $"\nINSERT INTO history_summary_days(day,revision) SELECT DISTINCT {DayExpr("start_ticks")},1 FROM scope_minutes;";
        internal static bool IsInitialized(SqliteConnection db, SqliteTransaction? tx)
        {
            using var c = Cmd(db, tx, "SELECT name,sql FROM sqlite_schema WHERE name GLOB 'history_summary_*';");
            using var reader = c.ExecuteReader(); var actual = new Dictionary<string, string>(StringComparer.Ordinal);
            while (reader.Read()) actual.Add(reader.GetString(0), reader.GetString(1));
            if (actual.Count == 0) return false;
            if (actual.Count != Definitions.Count || Definitions.Any(x => !actual.TryGetValue(x.Key, out var sql) || NormalizeSql(sql) != NormalizeSql(x.Value)))
                throw new InvalidOperationException("Unsupported or incomplete historical summary extension; use the offline initialization/repair procedure.");
            return true;
        }
        private static string DayExpr(string ticks) => $"(({ticks}+{Offset})/{Day})*{Day}-{Offset}";
        private static IReadOnlyDictionary<string, string> CreateDefinitions()
        {
            var sql = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["history_summary_days"] = "CREATE TABLE history_summary_days(day INTEGER PRIMARY KEY,revision INTEGER NOT NULL);",
                ["history_summary_cache"] = "CREATE TABLE history_summary_cache(day INTEGER PRIMARY KEY,revision INTEGER NOT NULL,rule TEXT NOT NULL,payload TEXT NOT NULL,sha256 TEXT NOT NULL);"
            };
            foreach (var table in new[] { "scope_minutes", "ui_display_records" })
            foreach (var op in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                var body = "";
                var fields = table == "scope_minutes" ? new[] { "start_ticks", "scope_id", "upload", "download", "approximate", "synthetic" } :
                    new[] { "entity_key", "kind", "start_ticks", "end_ticks", "snapshot", "upload", "download" };
                var when = op == "UPDATE" ? " WHEN " + string.Join(" OR ", fields.Select(f => $"OLD.{f} IS NOT NEW.{f}")) : "";
                foreach (var row in op == "UPDATE" ? new[] { "OLD", "NEW" } : new[] { op == "DELETE" ? "OLD" : "NEW" })
                {
                    var conditions = table == "ui_display_records" ? row + ".kind=0" : "1";
                    if (row == "NEW" && op == "UPDATE") conditions += " AND (" +
                        (table == "ui_display_records" ? "OLD.kind!=0 OR " : "") + DayExpr("NEW.start_ticks") + "!=" + DayExpr("OLD.start_ticks") + ")";
                    body += $"INSERT INTO history_summary_days(day,revision) SELECT {DayExpr(row + ".start_ticks")},1 WHERE {conditions} ON CONFLICT(day) DO UPDATE SET revision=revision+1;";
                }
                var name = "history_summary_" + table + "_" + op.ToLowerInvariant();
                sql.Add(name, $"CREATE TRIGGER {name} AFTER {op} ON {table}{when} BEGIN {body} END;");
            }
            sql.Add("history_summary_scope_update", $"""
                CREATE TRIGGER history_summary_scope_update AFTER UPDATE OF basis,name,node_key,outbound_tag ON measurement_scopes
                WHEN OLD.basis IS NOT NEW.basis OR OLD.name IS NOT NEW.name OR OLD.node_key IS NOT NEW.node_key OR OLD.outbound_tag IS NOT NEW.outbound_tag
                BEGIN INSERT INTO history_summary_days(day,revision)
                    SELECT DISTINCT {DayExpr("start_ticks")},1 FROM scope_minutes WHERE scope_id=NEW.scope_id
                    ON CONFLICT(day) DO UPDATE SET revision=revision+1; END;
                """);
            foreach (var op in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                var body = "";
                foreach (var row in op == "UPDATE" ? new[] { "OLD", "NEW" } : new[] { op == "DELETE" ? "OLD" : "NEW" })
                    body += $"INSERT INTO history_summary_days(day,revision) SELECT DISTINCT {DayExpr("m.start_ticks")},1 FROM scope_minutes m WHERE {row}.reason='uncollected_outbounds' AND m.scope_id={row}.scope_id AND m.start_ticks<{row}.end_ticks AND m.start_ticks+{Minute}>{row}.start_ticks ON CONFLICT(day) DO UPDATE SET revision=revision+1;";
                var relevant = op == "UPDATE" ? "(OLD.reason='uncollected_outbounds' OR NEW.reason='uncollected_outbounds') AND (" +
                    string.Join(" OR ", new[] { "scope_id", "start_ticks", "end_ticks", "reason" }.Select(f => $"OLD.{f} IS NOT NEW.{f}")) + ")" :
                    (op == "DELETE" ? "OLD" : "NEW") + ".reason='uncollected_outbounds'";
                var name = "history_summary_gap_" + op.ToLowerInvariant();
                sql.Add(name, $"CREATE TRIGGER {name} AFTER {op} ON scope_gaps WHEN {relevant} BEGIN {body} END;");
            }
            return sql;
        }
        private SqliteConnection Open(bool writable)
        {
            var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = writable ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly,
                Pooling = false, Cache = SqliteCacheMode.Private, DefaultTimeout = 1 }.ToString());
            try
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = writable ? "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL; PRAGMA busy_timeout=1000;" : "PRAGMA busy_timeout=1000;";
                command.ExecuteNonQuery(); return db;
            }
            catch { db.Dispose(); throw; }
        }
        private static SqliteCommand Cmd(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object?)[] args)
        {
            var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
            foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
            return cmd;
        }

        private static bool Eligible(ScopeQuery q)
        {
            if (q.Basis != "local_proxy" || q.TimeZoneId != Zone.Id || q.ScopeId is not null || q.ScopeIds is not null ||
                q.ScopeName is not null || !q.NonZeroRecords || q.Page != 0 || q.PageSize is < 1 or > 50 || q.Start.Year is < 1992 or >= 9999 || q.End <= q.Start) return false;
            var start = TimeZoneInfo.ConvertTime(q.Start, Zone).DateTime;
            var end = TimeZoneInfo.ConvertTime(q.End, Zone).DateTime;
            if (start.TimeOfDay != TimeSpan.Zero || start.Day != 1) return false;
            return q.Bucket == "day" && end == start.AddMonths(1) ||
                q.Bucket == "month" && start.Month == 1 && end == start.AddYears(1);
        }
        private static ScopeQuery Daily(long day) => new("local_proxy", new(day, TimeSpan.Zero), new(day + Day, TimeSpan.Zero), Zone.Id, "day") { NonZeroRecords = true };
        private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        private static Piece? Valid(Entry entry)
        {
            if (entry.Rule != Rule || entry.StoredRevision != entry.Revision || entry.Payload is null || entry.Hash != Hash(entry.Payload)) return null;
            try { var piece = JsonSerializer.Deserialize<Piece>(entry.Payload); return piece?.Day == entry.Day ? piece : null; }
            catch (JsonException) { return null; }
        }
        private static List<Entry> Entries(SqliteConnection db, SqliteTransaction tx, long start, long end, CancellationToken cancellationToken)
        {
            using var c = Cmd(db, tx, """
                SELECT d.day,d.revision,c.revision,c.rule,c.payload,c.sha256 FROM history_summary_days d
                LEFT JOIN history_summary_cache c ON c.day=d.day WHERE d.day >= $start AND d.day < $end ORDER BY d.day DESC;
                """, ("$start", start), ("$end", end));
            using var r = c.ExecuteReader(); var rows = new List<Entry>();
            while (r.Read()) { cancellationToken.ThrowIfCancellationRequested(); rows.Add(new(r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetInt64(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5))); }
            return rows;
        }
        private static Chunk Read(SqliteConnection db, SqliteTransaction tx, ScopeQuery query, int kind, ref long traffic, ref long displayRows, CancellationToken cancellationToken)
        {
            var directory = new Dictionary<long, StoredScope>(); var rows = new List<Raw>();
            using (var cmd = Cmd(db, tx, "SELECT * FROM (" + RecordsSql + ") WHERE kind=$kind ORDER BY start_ticks DESC,scope_id,kind,row_id;",
                ("$basis", "local_proxy"), ("$id", null), ("$ids", null), ("$name", null),
                ("$start", query.Start.UtcTicks), ("$end", query.End.UtcTicks), ("$kind", kind)))
            using (var reader = cmd.ExecuteReader())
                while (reader.Read()) { cancellationToken.ThrowIfCancellationRequested(); rows.Add(new(ReadStoredTraffic(reader, directory), reader.GetInt64(8))); traffic++; }
            var metadata = new List<TrafficDisplayStore.StoredPart>();
            if (directory.Count != 0)
            {
                var entities = JsonSerializer.Serialize(directory.Keys.Select(x => "scope:" + x.ToString(CultureInfo.InvariantCulture)).ToArray());
                // The existing PK can bound each entity/kind. Preserve rowid part order exactly.
                using var cmd = Cmd(db, tx, """
                    SELECT entity_key,kind,start_ticks,end_ticks,snapshot,upload,download FROM ui_display_records
                    WHERE entity_key IN (SELECT value FROM json_each($entities)) AND kind=$kind
                        AND start_ticks<$end AND end_ticks>$start ORDER BY rowid;
                    """, ("$entities", entities), ("$kind", kind), ("$start", query.Start.UtcTicks), ("$end", query.End.UtcTicks));
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    metadata.Add(new(new(reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetInt64(3)),
                        reader.GetString(4), reader.GetInt64(5), reader.GetInt64(6))); displayRows++;
                }
            }
            return new(rows, directory, metadata);
        }
        private static void ProjectInto(Aggregate aggregate, Chunk chunk, CancellationToken cancellationToken)
        {
            var metadata = TrafficDisplayStore.Parse(chunk.Metadata, cancellationToken);
            var projections = new Dictionary<long, ScopeProjection>(); var rates = new Dictionary<string, BigInteger>(StringComparer.Ordinal);
            foreach (var row in chunk.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var scope = Projection(row.Traffic.ScopeId, chunk.Directory, projections); var part = 0;
                foreach (var value in Project(row.Traffic, scope, metadata, rates)) aggregate.Add(row, scope, value, part++);
            }
        }
        private static List<ScopeGap> ReadGaps(SqliteConnection db, SqliteTransaction tx, ScopeQuery query, CancellationToken cancellationToken)
        {
            using var cmd = Cmd(db, tx, """
                SELECT g.scope_id,g.origin,g.start_ticks,g.end_ticks,g.reason,g.is_open
                FROM scope_gaps g LEFT JOIN measurement_scopes s ON s.scope_id=g.scope_id
                WHERE (g.scope_id IS NULL OR s.basis IN ('actual_member','legacy_proxy'))
                    AND ((g.start_ticks<$end AND (g.end_ticks>$start OR g.is_open=1))
                        OR (g.start_ticks=g.end_ticks AND g.start_ticks >= $start AND g.start_ticks<$end))
                ORDER BY g.start_ticks,g.id;
                """, ("$start", query.Start.UtcTicks), ("$end", query.End.UtcTicks));
            using var r = cmd.ExecuteReader(); var gaps = new List<ScopeGap>();
            while (r.Read()) { cancellationToken.ThrowIfCancellationRequested(); gaps.Add(new(r.IsDBNull(0) ? null : r.GetInt64(0), r.GetString(1), Iso(r.GetInt64(2)), Iso(r.GetInt64(3)), r.GetString(4), r.GetInt64(5) != 0)); }
            return gaps;
        }
        internal ScopeHistory? TryQuery(ScopeQuery query, CancellationToken cancellationToken,
            Action<ScopeQueryMetrics>? observer, out BuildRequest? request)
        {
            request = null;
            if (!Eligible(query)) return null;
            cancellationToken.ThrowIfCancellationRequested();
            var clock = Stopwatch.StartNew();
            long traffic = 0, displayRows = 0; var hits = 0; var rawDays = 0;
            var pieces = new List<Piece>(); var chunks = new List<Chunk>(); List<ScopeGap> gaps = [];
            var today = DayStart(time.GetUtcNow().UtcTicks); var allRaw = false;
            try
            {
                using var db = Open(false); using var tx = db.BeginTransaction(deferred: true);
                var entries = Entries(db, tx, query.Start.UtcTicks, query.End.UtcTicks, cancellationToken);
                var validated = new List<(Entry Entry, Piece? Piece)>();
                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    validated.Add((entry, entry.Day < today ? Valid(entry) : null));
                }
                var missing = validated.Where(x => x.Entry.Day < today && x.Piece is null).Select(x => x.Entry).ToArray();
                if (missing.Length != 0) request = new(missing.Select(x => x.Day).ToArray(),
                    missing.Where(x => x.StoredRevision == x.Revision && x.Rule == Rule).Select(x => x.Day).ToArray());
                allRaw = validated.All(x => x.Piece is null);
                if (!allRaw)
                {
                    foreach (var (entry, piece) in validated)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (piece is not null) { pieces.Add(piece); hits++; }
                        else { chunks.Add(Read(db, tx, Daily(entry.Day), 0, ref traffic, ref displayRows, cancellationToken)); rawDays++; }
                    }
                    chunks.Add(Read(db, tx, query, 1, ref traffic, ref displayRows, cancellationToken));
                    gaps = ReadGaps(db, tx, query, cancellationToken);
                }
                tx.Commit();
            }
            catch (SqliteException)
            {
                // Optional derived reads must not hide an otherwise readable raw ledger.
                request = null; return null;
            }
            // No usable materialization: let the caller execute the original Query exactly once.
            if (allRaw) return null;
            var snapshot = clock.Elapsed;
            var aggregate = new Aggregate(query);
            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var part = new Aggregate(query); ProjectInto(part, chunk, cancellationToken);
                aggregate.Total.Add(part.Total.U, part.Total.D); aggregate.Recovered.Add(part.Recovered.U, part.Recovered.D);
                aggregate.Partial.Add(part.Partial.U, part.Partial.D); aggregate.Unbucketed.Add(part.Unbucketed.U, part.Unbucketed.D);
                aggregate.Display.Merge(part.Display.Save()); aggregate.Count += part.Count; aggregate.Details.AddRange(part.Details);
                foreach (var (id, value) in part.Scopes)
                {
                    var sum = aggregate.Scope(id, value.Name, value.First, value.Last, value.Merge);
                    sum.Total.Add(value.Total.U, value.Total.D); sum.Recovered.Add(value.Recovered.U, value.Recovered.D); sum.Display.Merge(value.Display.Save());
                }
                foreach (var (name, value) in part.Names)
                { var sum = aggregate.Name(name, value.First); sum.Total.Add(value.Total.U, value.Total.D); sum.Display.Merge(value.Display.Save()); }
                foreach (var (label, value) in part.Series)
                {
                    if (!aggregate.Series.TryGetValue(label, out var sum)) aggregate.Series.Add(label, sum = new(value.At));
                    sum.At = Math.Min(sum.At, value.At); sum.Total.Add(value.Total.U, value.Total.D);
                    sum.Recovered.Add(value.Recovered.U, value.Recovered.D); sum.Display.Merge(value.Display.Save());
                }
            }
            foreach (var piece in pieces) { cancellationToken.ThrowIfCancellationRequested(); aggregate.Add(piece); }
            cancellationToken.ThrowIfCancellationRequested();
            var response = aggregate.Finish(gaps, true);
            observer?.Invoke(new(TimeSpan.Zero, TimeSpan.Zero, clock.Elapsed - snapshot, clock.Elapsed, traffic, displayRows)
            { HistoricalSummaryDays = hits, RawSummaryDays = rawDays, SnapshotRead = snapshot });
            return response;
        }
        private static long DayStart(long ticks) => ((ticks + Offset) / Day) * Day - Offset;
        internal void Request(BuildRequest? request)
        {
            if (request is null) return;
            lock (workGate)
            {
                if (disposed) return;
                pending = pending is null ? request : new(pending.Days.Concat(request.Days).Distinct().ToArray(),
                    pending.ForceDays.Concat(request.ForceDays).Distinct().ToArray());
                if (!running) { running = true; worker = Task.Run(BuildPendingAsync); }
            }
        }
        private async Task BuildPendingAsync()
        {
            var cancellationToken = stopping.Token;
            try
            {
                while (true)
                {
                    BuildRequest request;
                    lock (workGate)
                    {
                        if (pending is null) { running = false; return; }
                        request = pending; pending = null;
                    }
                    long[] days;
                    using (var db = Open(false)) using (var tx = db.BeginTransaction(deferred: true))
                    {
                        using var c = Cmd(db, tx, """
                            SELECT d.day FROM history_summary_days d LEFT JOIN history_summary_cache c ON c.day=d.day
                            WHERE d.day IN (SELECT value FROM json_each($days)) AND d.day<$today
                                AND (c.day IS NULL OR c.revision!=d.revision OR c.rule!=$rule
                                    OR d.day IN (SELECT value FROM json_each($forced))) ORDER BY d.day DESC;
                            """, ("$days", JsonSerializer.Serialize(request.Days)), ("$forced", JsonSerializer.Serialize(request.ForceDays)),
                            ("$today", DayStart(time.GetUtcNow().UtcTicks)), ("$rule", Rule));
                        using var reader = c.ExecuteReader(); var selected = new List<long>();
                        while (reader.Read()) { cancellationToken.ThrowIfCancellationRequested(); selected.Add(reader.GetInt64(0)); }
                        days = selected.ToArray(); tx.Commit();
                    }
                    // A single day per batch; read/compute never holds the collector's gate.
                    foreach (var day in days)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        BuildDay(day, cancellationToken);
                        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SqliteException or InvalidOperationException or JsonException)
            {
                // Keep revisions dirty. A later eligible request can retry; never spin on a failure.
                lock (workGate) { running = false; pending = null; }
            }
        }
        private void BuildDay(long day, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Chunk chunk; long revision; long traffic = 0, displayRows = 0;
            using (var db = Open(false)) using (var tx = db.BeginTransaction(deferred: true))
            {
                var entry = Entries(db, tx, day, day + Day, cancellationToken).SingleOrDefault();
                if (entry is null || Valid(entry) is not null) return;
                revision = entry.Revision;
                chunk = Read(db, tx, Daily(day), 0, ref traffic, ref displayRows, cancellationToken); tx.Commit();
            }
            var aggregate = new Aggregate(Daily(day)); ProjectInto(aggregate, chunk, cancellationToken);
            var payload = JsonSerializer.Serialize(aggregate.Save(day)); var hash = Hash(payload);
            cancellationToken.ThrowIfCancellationRequested();
            using (var db = Open(true)) using (var tx = db.BeginTransaction())
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var check = Cmd(db, tx, "SELECT revision FROM history_summary_days WHERE day=$day;", ("$day", day));
                if (check.ExecuteScalar() is long current && current == revision)
                {
                    using var c = Cmd(db, tx, """
                        INSERT INTO history_summary_cache(day,revision,rule,payload,sha256) VALUES($day,$rev,$rule,$payload,$hash)
                        ON CONFLICT(day) DO UPDATE SET revision=excluded.revision,rule=excluded.rule,payload=excluded.payload,sha256=excluded.sha256;
                        """, ("$day", day), ("$rev", revision), ("$rule", Rule), ("$payload", payload), ("$hash", hash));
                    c.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }
        public void Dispose()
        {
            Task active;
            lock (workGate)
            {
                if (disposed) return;
                disposed = true; pending = null; stopping.Cancel(); active = worker;
            }
            active.GetAwaiter().GetResult();
            stopping.Dispose();
        }
    }
}
