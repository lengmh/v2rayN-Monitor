using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace V2rayNMonitor.Core;

public sealed record CapturedNodeName(string OriginalName, string? GroupLabel = null);
public sealed record TrafficDisplay(string? OriginalName, IReadOnlyList<string> Groups, string? MergeKey,
    string? Rate, string RateStatus, string? EstimatedBilledBytes, string EstimateCoveredBytes, string EstimateUnknownBytes);

// This optional extension shares the history transaction. Opening an old ledger never creates it.
internal sealed class TrafficDisplayStore(SqliteConnection db)
{
    internal const string Schema = """
        CREATE TABLE ui_display_cursors (
            entity_key TEXT PRIMARY KEY, at_ticks INTEGER NOT NULL, snapshot TEXT);
        CREATE TABLE ui_display_records (
            entity_key TEXT NOT NULL, kind INTEGER NOT NULL CHECK(kind IN (0,1)),
            start_ticks INTEGER NOT NULL, end_ticks INTEGER NOT NULL,
            snapshot TEXT NOT NULL,
            upload INTEGER NOT NULL CHECK(typeof(upload)='integer' AND upload>=0),
            download INTEGER NOT NULL CHECK(typeof(download)='integer' AND download>=0),
            PRIMARY KEY(entity_key,kind,start_ticks,end_ticks,snapshot));
        """;

    internal sealed record Snapshot(string OriginalName, string? GroupLabel, string? MergeKey, string? Rate);
    internal sealed record Part(Snapshot Snapshot, long Upload, long Download);
    internal sealed record Slice(string? Name, string Upload, string Download, TrafficDisplay Display);
    internal readonly record struct ValueSlice(long Upload, long Download, DisplayValues Display);
    internal readonly record struct DisplayValues(string? OriginalName, string? GroupLabel, string? MergeKey, string? Rate,
        BigInteger Covered, BigInteger Unknown, BigInteger? Units)
    {
        internal TrafficDisplay Result() => new(OriginalName, GroupLabel is null ? [] : [GroupLabel], MergeKey, Rate,
            Units is null ? "unknown" : Unknown != 0 ? "partial" : "known",
            Units is { } units ? ProviderDecimal.FormatScaledUnits(units) : null,
            Covered.ToString(CultureInfo.InvariantCulture), Unknown.ToString(CultureInfo.InvariantCulture));
    }
    internal readonly record struct RecordKey(string Entity, int Kind, long Start, long End);
    internal readonly record struct StoredPart(RecordKey Key, string Snapshot, long Upload, long Download);
    internal static string MergeKey(string basis, string key) => basis + ":" + XrayParser.Hash(key);

    // Older member records saved the friendly label and generated tag before display snapshots existed.
    internal static CapturedNodeName? HistoricalMemberName(string basis, string name, string? tag)
    {
        if (basis is not ("actual_member" or "probe_urltest_member") || tag is null || !name.StartsWith('【')) return null;
        var close = name.IndexOf("】 ", StringComparison.Ordinal);
        var separator = tag.IndexOf("-proxy-", StringComparison.Ordinal);
        if (close <= 1 || separator <= 0 || !tag[..separator].All(char.IsAsciiDigit)) return null;
        var label = name[1..close];
        var tagged = tag[(separator + 7)..];
        if (!tagged.StartsWith(label + "-", StringComparison.Ordinal)) return null;
        var member = tagged[(label.Length + 1)..];
        var ordinal = member.IndexOf('-');
        if (ordinal <= 0 || ordinal + 1 >= member.Length ||
            !int.TryParse(member[..ordinal], NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
            number <= 0 || member[..ordinal] != number.ToString(CultureInfo.InvariantCulture)) return null;
        var original = member[(ordinal + 1)..];
        var saved = name[(close + 2)..];
        return saved == member || saved == original ? new(original, label) : null;
    }

    internal CapturedNodeName? CurrentName(string entity)
    {
        using var command = Command(null, "SELECT snapshot FROM ui_display_cursors WHERE entity_key=$key;", ("$key", entity));
        return command.ExecuteScalar() is string json && JsonSerializer.Deserialize<Snapshot>(json) is { } value
            ? new(value.OriginalName, value.GroupLabel) : null;
    }

    internal static TrafficDisplayStore? Open(SqliteConnection db, SqliteTransaction? transaction = null)
    {
        using var count = db.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = "SELECT count(*) FROM sqlite_schema WHERE name IN ('ui_display_cursors','ui_display_records');";
        if (Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture) == 0) return null;
        foreach (Match m in Regex.Matches(Schema, @"CREATE TABLE (\w+)[\s\S]*?;"))
        {
            using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT sql FROM sqlite_schema WHERE name=$name;";
            command.Parameters.AddWithValue("$name", m.Groups[1].Value);
            static string Normalize(string x) => Regex.Replace(x.Trim().TrimEnd(';'), @"\s+", " ");
            if (command.ExecuteScalar() is not string actual || Normalize(actual) != Normalize(m.Value))
                throw new InvalidOperationException("Unsupported UI display metadata schema.");
        }
        return new(db);
    }

    internal static Snapshot? Capture(string basis, string? nodeKey, CapturedNodeName? name)
    {
        if (name is null || basis is not ("actual_member" or "legacy_proxy" or "probe_urltest_member")) return null;
        var rates = Regex.Matches(name.OriginalName, @"(?<![\p{L}\p{N}_.])([0-9]+(?:\.[0-9]+)?)\s*(?:[xX×]|倍率|倍)(?![\p{L}\p{N}])")
            .Cast<Match>().Select(m => m.Groups[1].Value).ToArray();
        var parsed = rates.Select(x => ProviderDecimal.TryParse(x, out var value) ? value.ToString() : null).ToArray();
        var rate = parsed.Length == 0 ? "1" : parsed.All(x => x is not null) && parsed.Distinct().Count() == 1 ? parsed[0] : null;
        // Only a verified IndexId can combine outbounds; names and connection similarity cannot.
        return new(name.OriginalName, name.GroupLabel, nodeKey is null ? null : MergeKey(basis, nodeKey), rate);
    }

    internal string? Observe(SqliteTransaction transaction, string key, Snapshot? snapshot, long at, long? previousAt)
    {
        var text = snapshot is null ? null : JsonSerializer.Serialize(snapshot);
        string? old = null; long? oldAt = null;
        using (var previous = Command(transaction, "SELECT snapshot,at_ticks FROM ui_display_cursors WHERE entity_key=$key;", ("$key", key)))
        using (var reader = previous.ExecuteReader())
            if (reader.Read()) { old = reader.IsDBNull(0) ? null : reader.GetString(0); oldAt = reader.GetInt64(1); }
        using var update = Command(transaction, """
            INSERT INTO ui_display_cursors(entity_key,at_ticks,snapshot) VALUES($key,$at,$snapshot)
            ON CONFLICT(entity_key) DO UPDATE SET at_ticks=excluded.at_ticks,snapshot=excluded.snapshot;
            """, ("$key", key), ("$at", at), ("$snapshot", text));
        update.ExecuteNonQuery();
        return oldAt == previousAt && old == text ? text : null; // A rename or old-writer boundary remains unknown.
    }

    internal void Add(SqliteTransaction transaction, string key, long start, long end, long upload, long download, string? snapshot)
    {
        if (snapshot is null) return;
        long up = 0, down = 0;
        using (var old = Command(transaction, """
            SELECT upload,download FROM ui_display_records
            WHERE entity_key=$key AND kind=0 AND start_ticks=$start AND end_ticks=$end AND snapshot=$snapshot;
            """, ("$key", key), ("$start", start), ("$end", end), ("$snapshot", snapshot)))
        using (var reader = old.ExecuteReader())
            if (reader.Read()) { up = reader.GetInt64(0); down = reader.GetInt64(1); }
        using var command = Command(transaction, """
            INSERT INTO ui_display_records(entity_key,kind,start_ticks,end_ticks,snapshot,upload,download)
            VALUES($key,0,$start,$end,$snapshot,$up,$down)
            ON CONFLICT(entity_key,kind,start_ticks,end_ticks,snapshot) DO UPDATE SET upload=excluded.upload,download=excluded.download;
            """, ("$key", key), ("$start", start), ("$end", end), ("$snapshot", snapshot),
            ("$up", checked(up + upload)), ("$down", checked(down + download)));
        command.ExecuteNonQuery();
    }

    internal List<StoredPart> ReadRaw(SqliteTransaction? transaction, long start, long end,
        IReadOnlyList<string>? entities = null, CancellationToken cancellationToken = default, bool scan = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (entities is { Count: 0 }) return [];
        // Preserve the original table-scan part order even when the entity index is used.
        // Broad local-proxy queries avoid entity-index lookups followed by a rowid sort.
        using var command = Command(transaction, $"""
            SELECT entity_key,kind,start_ticks,end_ticks,snapshot,upload,download FROM ui_display_records{(scan ? " NOT INDEXED" : "")}
            WHERE start_ticks<$end AND end_ticks>$start
            """ + (entities is null ? "" : " AND entity_key IN (SELECT value FROM json_each($entities))") + " ORDER BY rowid;",
            ("$start", start), ("$end", end),
            ("$entities", entities is null ? null : JsonSerializer.Serialize(entities)));
        using var reader = command.ExecuteReader();
        var result = new List<StoredPart>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = new RecordKey(reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetInt64(3));
            result.Add(new(key, reader.GetString(4), reader.GetInt64(5), reader.GetInt64(6)));
        }
        return result;
    }

    internal Dictionary<RecordKey, List<Part>> Read(SqliteTransaction? transaction, long start, long end,
        CancellationToken cancellationToken = default) =>
        Parse(ReadRaw(transaction, start, end, cancellationToken: cancellationToken), cancellationToken);

    internal static Dictionary<RecordKey, List<Part>> Parse(IReadOnlyList<StoredPart> rows,
        CancellationToken cancellationToken = default, Dictionary<string, Snapshot>? snapshots = null)
    {
        snapshots ??= new(StringComparer.Ordinal);
        var result = new Dictionary<RecordKey, List<Part>>();
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!snapshots.TryGetValue(row.Snapshot, out var snapshot))
                snapshots.Add(row.Snapshot, snapshot = JsonSerializer.Deserialize<Snapshot>(row.Snapshot)
                    ?? throw new InvalidOperationException("Invalid stored display snapshot."));
            if (!result.TryGetValue(row.Key, out var parts)) result.Add(row.Key, parts = []);
            parts.Add(new(snapshot, row.Upload, row.Download));
        }
        return result;
    }

    internal static TrafficDisplay Describe(IReadOnlyList<Part>? parts, string upload, string download, string? mergeKey)
    {
        var total = BigInteger.Parse(upload, CultureInfo.InvariantCulture) + BigInteger.Parse(download, CultureInfo.InvariantCulture);
        BigInteger capturedUp = 0, capturedDown = 0, covered = 0, estimated = 0;
        var names = new HashSet<string>(StringComparer.Ordinal); var groups = new HashSet<string>(StringComparer.Ordinal);
        var rates = new HashSet<string>(StringComparer.Ordinal); var hasRate = false;
        foreach (var part in parts ?? [])
        {
            capturedUp += part.Upload; capturedDown += part.Download;
            names.Add(part.Snapshot.OriginalName);
            if (part.Snapshot.GroupLabel is { } group) groups.Add(group);
            if (part.Snapshot.Rate is not { } text) continue;
            if (!ProviderDecimal.TryParse(text, out var rate)) throw new InvalidOperationException("Invalid captured multiplier.");
            var bytes = (BigInteger)part.Upload + part.Download;
            covered += bytes; estimated += bytes * rate.ScaledUnits; rates.Add(text); hasRate = true;
        }
        if (capturedUp > BigInteger.Parse(upload, CultureInfo.InvariantCulture) || capturedDown > BigInteger.Parse(download, CultureInfo.InvariantCulture))
            throw new InvalidOperationException("Display evidence exceeds its history record.");
        var complete = capturedUp + capturedDown == total && parts is { Count: > 0 };
        return new(complete && names.Count == 1 ? names.Single() : null, groups.Order(StringComparer.Ordinal).ToArray(), mergeKey,
            hasRate && covered == total && rates.Count == 1 ? rates.Single() : null,
            !hasRate ? "unknown" : covered != total ? "partial" : rates.Count > 1 ? "mixed" : "known",
            hasRate ? ProviderDecimal.FormatScaledUnits(estimated) : null, covered.ToString(CultureInfo.InvariantCulture),
            (total - covered).ToString(CultureInfo.InvariantCulture));
    }

    internal static IEnumerable<Slice> Split(IReadOnlyList<Part>? parts, string upload, string download, string? mergeKey)
    {
        // One minute can contain two names/rates. Preserve their captured contributions rather
        // than labelling the whole minute with the latest name or multiplying a mixed total.
        var remainingUp = BigInteger.Parse(upload, CultureInfo.InvariantCulture);
        var remainingDown = BigInteger.Parse(download, CultureInfo.InvariantCulture);
        BigInteger capturedUp = 0, capturedDown = 0;
        foreach (var part in parts ?? []) { capturedUp += part.Upload; capturedDown += part.Download; }
        if (capturedUp > remainingUp || capturedDown > remainingDown)
            throw new InvalidOperationException("Display evidence exceeds its history record.");
        foreach (var part in parts ?? [])
        {
            var up = part.Upload.ToString(CultureInfo.InvariantCulture); var down = part.Download.ToString(CultureInfo.InvariantCulture);
            remainingUp -= part.Upload; remainingDown -= part.Download;
            yield return new(part.Snapshot.OriginalName, up, down, Describe([part], up, down, mergeKey));
        }
        if (remainingUp != 0 || remainingDown != 0 || parts is not { Count: > 0 })
        {
            var up = remainingUp.ToString(CultureInfo.InvariantCulture); var down = remainingDown.ToString(CultureInfo.InvariantCulture);
            yield return new(null, up, down, Describe(null, up, down, mergeKey));
        }
    }

    internal static IEnumerable<ValueSlice> SplitValues(IReadOnlyList<Part>? parts, long upload, long download,
        string? mergeKey, Dictionary<string, BigInteger> rates)
    {
        BigInteger capturedUp = 0, capturedDown = 0;
        foreach (var part in parts ?? []) { capturedUp += part.Upload; capturedDown += part.Download; }
        if (capturedUp > upload || capturedDown > download)
            throw new InvalidOperationException("Display evidence exceeds its history record.");
        foreach (var part in parts ?? [])
        {
            upload -= part.Upload; download -= part.Download;
            var total = (BigInteger)part.Upload + part.Download;
            BigInteger? units = null;
            if (part.Snapshot.Rate is { } text)
            {
                if (!rates.TryGetValue(text, out var rate))
                {
                    if (!ProviderDecimal.TryParse(text, out var parsed))
                        throw new InvalidOperationException("Invalid captured multiplier.");
                    rates.Add(text, rate = parsed.ScaledUnits);
                }
                units = total * rate;
            }
            yield return new(part.Upload, part.Download,
                new(part.Snapshot.OriginalName, part.Snapshot.GroupLabel, mergeKey, part.Snapshot.Rate,
                    units is null ? 0 : total, units is null ? total : 0, units));
        }
        if (upload != 0 || download != 0 || parts is not { Count: > 0 })
            yield return new(upload, download, new(null, null, mergeKey, null, 0, (BigInteger)upload + download, null));
    }

    internal sealed class Summary
    {
        private BigInteger covered, unknown, units;
        private bool seen, hasEstimate, mixedName, mixedRate;
        private string? name, rate;
        private readonly HashSet<string> groups = new(StringComparer.Ordinal);
        // Final display values lose mixed/zero-observation state; derived summaries retain it.
        internal sealed record State(string Covered, string Unknown, string Units, bool Seen,
            bool HasEstimate, bool MixedName, bool MixedRate, string? Name, string? Rate, string[] Groups);
        internal State Save() => new(covered.ToString(CultureInfo.InvariantCulture), unknown.ToString(CultureInfo.InvariantCulture),
            units.ToString(CultureInfo.InvariantCulture), seen, hasEstimate, mixedName, mixedRate, name, rate,
            groups.Order(StringComparer.Ordinal).ToArray());
        internal void Merge(State state)
        {
            covered += BigInteger.Parse(state.Covered, CultureInfo.InvariantCulture);
            unknown += BigInteger.Parse(state.Unknown, CultureInfo.InvariantCulture);
            units += BigInteger.Parse(state.Units, CultureInfo.InvariantCulture);
            hasEstimate |= state.HasEstimate;
            if (state.Seen) { Observe(state.Name, state.Rate); mixedName |= state.MixedName; mixedRate |= state.MixedRate; }
            foreach (var group in state.Groups) groups.Add(group);
        }
        internal static (BigInteger Covered, BigInteger Unknown, BigInteger? Units) Amounts(TrafficDisplay value) =>
            (BigInteger.Parse(value.EstimateCoveredBytes, CultureInfo.InvariantCulture),
                BigInteger.Parse(value.EstimateUnknownBytes, CultureInfo.InvariantCulture),
                value.EstimatedBilledBytes is { } estimate ? Units(estimate) : null);
        internal void Add(TrafficDisplay value) => Add(value, Amounts(value));
        internal void Add(TrafficDisplay value, (BigInteger Covered, BigInteger Unknown, BigInteger? Units) amounts)
        {
            covered += amounts.Covered;
            unknown += amounts.Unknown;
            if (amounts.Units is { } estimate) { units += estimate; hasEstimate = true; }
            Observe(value.OriginalName, value.Rate);
            foreach (var group in value.Groups) groups.Add(group);
        }
        internal void Add(DisplayValues value)
        {
            covered += value.Covered;
            unknown += value.Unknown;
            if (value.Units is { } estimate) { units += estimate; hasEstimate = true; }
            Observe(value.OriginalName, value.Rate);
            if (value.GroupLabel is { } group) groups.Add(group);
        }
        private void Observe(string? originalName, string? capturedRate)
        {
            if (seen) { mixedName |= name != originalName; mixedRate |= rate != capturedRate; }
            else { name = originalName; rate = capturedRate; seen = true; }
        }
        internal TrafficDisplay Result(string? mergeKey = null)
        {
            return new(mixedName ? null : name, groups.Order(StringComparer.Ordinal).ToArray(),
                mergeKey, unknown == 0 && !mixedRate ? rate : null,
                !hasEstimate ? "unknown" : unknown != 0 ? "partial" : !mixedRate && rate is not null ? "known" : "mixed",
                hasEstimate ? ProviderDecimal.FormatScaledUnits(units) : null, covered.ToString(CultureInfo.InvariantCulture), unknown.ToString(CultureInfo.InvariantCulture));
        }
        private static BigInteger Units(string text)
        {
            // Query totals may exceed the per-record 96-bit bound; preserve their exact scale.
            var point = text.IndexOf('.'); var scale = point < 0 ? 0 : text.Length - point - 1;
            return BigInteger.Parse(text.Replace(".", ""), CultureInfo.InvariantCulture) * BigInteger.Pow(10, 28 - scale);
        }
    }

    private SqliteCommand Command(SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = db.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
}
