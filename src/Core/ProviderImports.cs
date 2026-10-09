using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.VisualBasic.FileIO;

namespace V2rayNMonitor.Core;

public sealed record ImportRequest(string Source, string TimeZoneId, string Csv);
public sealed record ImportPreview(string Hash, int RowCount, string RawBytes, string BilledBytes, string FirstTime, string LastTime, bool Duplicate, IReadOnlyList<string> Warnings);
public sealed record ImportBatch(string Hash, string Source, string ImportedAt, int RowCount, string RawBytes, string BilledBytes, string FirstTime, string LastTime);

/// <summary>A separate, user-imported provider ledger. It never changes local accounting.</summary>
public sealed class ProviderImports : IDisposable
{
    private readonly SqliteConnection db;
    private readonly object gate = new();
    private sealed record Row(string Name, decimal Rate, long Raw, long Billed, string Display, DateTimeOffset At);
    public ProviderImports(string path)
    {
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
          PRAGMA journal_mode=WAL;
          CREATE TABLE IF NOT EXISTS import_batches(source TEXT NOT NULL,hash TEXT NOT NULL,imported_at TEXT NOT NULL,row_count INTEGER NOT NULL,raw_bytes INTEGER NOT NULL,billed_bytes INTEGER NOT NULL,first_time TEXT NOT NULL,last_time TEXT NOT NULL,time_zone TEXT NOT NULL,PRIMARY KEY(source,hash));
          CREATE TABLE IF NOT EXISTS provider_records(source TEXT NOT NULL,hash TEXT NOT NULL,row_number INTEGER NOT NULL,node_name TEXT NOT NULL,rate TEXT NOT NULL,raw_bytes INTEGER NOT NULL,billed_bytes INTEGER NOT NULL,display_text TEXT NOT NULL,at_utc TEXT NOT NULL,PRIMARY KEY(source,hash,row_number));
          """;
        cmd.ExecuteNonQuery();
    }
    public ImportPreview Preview(ImportRequest request) { lock (gate) return Prepare(request).Preview; }
    public ImportPreview Commit(ImportRequest request)
    {
        lock (gate)
        {
            var (preview, rows) = Prepare(request);
            if (preview.Duplicate) return preview;
            if (preview.Warnings.Count != 0) throw new ArgumentException(preview.Warnings[0]);
            using var tx = db.BeginTransaction();
            using var batch = db.CreateCommand(); batch.Transaction = tx;
            batch.CommandText = "INSERT INTO import_batches VALUES($source,$hash,$now,$count,$raw,$billed,$first,$last,$zone)";
            Add(batch, ("$source", request.Source.Trim()), ("$hash", preview.Hash), ("$now", Iso(DateTimeOffset.UtcNow)), ("$count", rows.Count), ("$raw", long.Parse(preview.RawBytes, CultureInfo.InvariantCulture)), ("$billed", long.Parse(preview.BilledBytes, CultureInfo.InvariantCulture)), ("$first", preview.FirstTime), ("$last", preview.LastTime), ("$zone", request.TimeZoneId));
            batch.ExecuteNonQuery();
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i]; using var cmd = db.CreateCommand(); cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO provider_records VALUES($source,$hash,$row,$name,$rate,$raw,$billed,$display,$at)";
                Add(cmd, ("$source", request.Source.Trim()), ("$hash", preview.Hash), ("$row", i), ("$name", row.Name), ("$rate", row.Rate.ToString(CultureInfo.InvariantCulture)), ("$raw", row.Raw), ("$billed", row.Billed), ("$display", row.Display), ("$at", Iso(row.At)));
                cmd.ExecuteNonQuery();
            }
            tx.Commit(); return preview;
        }
    }
    public IReadOnlyList<ImportBatch> List()
    {
        lock (gate)
        {
            using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT hash,source,imported_at,row_count,raw_bytes,billed_bytes,first_time,last_time FROM import_batches ORDER BY imported_at DESC LIMIT 200";
            using var r = cmd.ExecuteReader(); var result = new List<ImportBatch>();
            while (r.Read()) result.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt64(4).ToString(CultureInfo.InvariantCulture), r.GetInt64(5).ToString(CultureInfo.InvariantCulture), r.GetString(6), r.GetString(7)));
            return result;
        }
    }
    private (ImportPreview Preview, List<Row> Rows) Prepare(ImportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Source) || request.Source.Trim().Length > 100) throw new ArgumentException("请填写 1–100 字符的服务商/账户来源。");
        if (request.Csv is null || Encoding.UTF8.GetByteCount(request.Csv) > 4_000_000) throw new ArgumentException("CSV 上限为 4 MB。");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(request.TimeZoneId);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Csv))).ToLowerInvariant();
        using var reader = new TextFieldParser(new StringReader(request.Csv.TrimStart('\uFEFF'))) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        reader.SetDelimiters(",");
        var header = reader.ReadFields();
        string[] expected = ["node_name", "rate", "origin_traffic", "origin_x_rate", "traffic", "log_time"];
        if (header is null || !header.SequenceEqual(expected)) throw new ArgumentException("CSV 表头须为 node_name,rate,origin_traffic,origin_x_rate,traffic,log_time。");
        var rows = new List<Row>(); long raw = 0, billed = 0;
        while (!reader.EndOfData)
        {
            var fields = reader.ReadFields();
            if (fields is null) break;
            if (fields.Length != 6 || rows.Count >= 10000) throw new ArgumentException("CSV 须每行六列，最多 10000 行。");
            if (string.IsNullOrWhiteSpace(fields[0]) || fields[0].Length > 500 || fields[4].Length > 500) throw new ArgumentException($"第 {rows.Count + 2} 行名称或显示文本无效。");
            if (!decimal.TryParse(fields[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rate) || rate < 0 ||
                !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var rowRaw) || rowRaw < 0 ||
                !long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var rowBilled) || rowBilled < 0) throw new ArgumentException($"第 {rows.Count + 2} 行数值无效；原始量与计费量须为非负整数字节。");
            if (!DateTime.TryParseExact(fields[5], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) throw new ArgumentException($"第 {rows.Count + 2} 行时间须为 yyyy-MM-dd HH:mm:ss。");
            local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local)) throw new ArgumentException("CSV 含夏令时跳变/重复时间，无法无歧义导入。请选择正确的固定时区重新导出。");
            var at = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
            checked { raw += rowRaw; billed += rowBilled; }
            rows.Add(new(fields[0], rate, rowRaw, rowBilled, fields[4], at));
        }
        if (rows.Count == 0) throw new ArgumentException("CSV 没有数据行。");
        var first = Iso(rows.Min(r => r.At)); var last = Iso(rows.Max(r => r.At));
        using var check = db.CreateCommand();
        check.CommandText = "SELECT hash FROM import_batches WHERE source=$source AND (hash=$hash OR (first_time <= $last AND last_time >= $first))";
        Add(check, ("$source", request.Source.Trim()), ("$hash", hash), ("$first", first), ("$last", last));
        using var existing = check.ExecuteReader(); var duplicate = false; var overlap = false;
        while (existing.Read()) { if (existing.GetString(0) == hash) duplicate = true; else overlap = true; }
        IReadOnlyList<string> warnings = overlap && !duplicate ? ["该来源存在时间范围重叠的不同文件。缺少稳定记录 ID，初版拒绝提交以避免重复记账；请导出不重叠的记录。最早/最晚时间不代表完整覆盖。"] : [];
        return (new(hash, rows.Count, raw.ToString(CultureInfo.InvariantCulture), billed.ToString(CultureInfo.InvariantCulture), first, last, duplicate, warnings), rows);
    }
    private static void Add(SqliteCommand cmd, params (string Name, object Value)[] values) { foreach (var item in values) cmd.Parameters.AddWithValue(item.Name, item.Value); }
    private static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    public void Dispose() { lock (gate) db.Dispose(); }
}
