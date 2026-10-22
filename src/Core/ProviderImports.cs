using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.VisualBasic.FileIO;

namespace V2rayNMonitor.Core;

public sealed record ImportRequest(string Source, string TimeZoneId, string Csv);
public sealed record ImportPreview(string Hash, int RowCount, string RawBytes, string BilledBytes, string FirstTime, string LastTime, bool Duplicate, IReadOnlyList<string> Warnings)
{
    public int NewRowCount { get; init; }
    public int SkippedRowCount { get; init; }
    public string NewRawBytes { get; init; } = "0";
    public string NewBilledBytes { get; init; } = "0";
}
public sealed record ImportBatch(string Hash, string Source, string ImportedAt, int RowCount, string RawBytes, string BilledBytes, string FirstTime, string LastTime)
{
    public bool ReferencesComplete { get; init; }
    public int? FileRowCount { get; init; }
    public string? FileRawBytes { get; init; }
    public string? FileBilledBytes { get; init; }
}
public sealed record ProviderTrackingUpgrade(string BackupPath, long PreservedBatches, long PreservedRecords);
public sealed record ProviderRevokePreview(string Source, string Hash, string Token, bool CanRevoke, string? Reason,
    int? RowCount, int? DeletedRowCount, int? RetainedRowCount, string? DeletedRawBytes,
    string? DeletedBilledBytes, string? RetainedRawBytes, string? RetainedBilledBytes);

/// <summary>A separate, user-imported provider ledger. It never changes local accounting.</summary>
public sealed class ProviderImports : IDisposable
{
    public const int MaxRows = 50_000;
    public const int MaxCsvBytes = 4_000_000;
    private readonly SqliteConnection db;
    private readonly string databasePath;
    private readonly object gate = new();
    private readonly record struct RecordId(string Hash, int Number);
    private sealed record PreparedImport(ImportPreview Preview, List<Row> Rows, List<RecordId?> References, bool Tracking);
    private sealed record RevokePlan(ProviderRevokePreview Preview, List<RecordId> Deleted);
    private const string TrackingSchema = """
        CREATE TABLE provider_batch_tracking(
            source TEXT NOT NULL,hash TEXT NOT NULL,file_row_count INTEGER NOT NULL,
            file_raw_bytes INTEGER NOT NULL,file_billed_bytes TEXT NOT NULL,PRIMARY KEY(source,hash));
        CREATE TABLE provider_batch_rows(
            source TEXT NOT NULL,hash TEXT NOT NULL,row_number INTEGER NOT NULL,
            record_hash TEXT NOT NULL,record_row_number INTEGER NOT NULL,
            PRIMARY KEY(source,hash,row_number),UNIQUE(source,hash,record_hash,record_row_number));
        CREATE INDEX provider_batch_rows_record ON provider_batch_rows(source,record_hash,record_row_number);
        """;
    private readonly record struct BillingKey(string Name, ProviderDecimal Rate, long Raw, ProviderDecimal Billed, long At);
    private sealed record Row(string Name, ProviderDecimal Rate, long Raw, ProviderDecimal Billed, string Display, DateTimeOffset At)
    {
        public BillingKey Key => new(Name, Rate, Raw, Billed, At.UtcTicks);
    }
    public ProviderImports(string path)
    {
        databasePath = path == ":memory:" ? path : Path.GetFullPath(path);
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        try
        {
            db.Open();
            using var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA journal_mode=WAL;"; cmd.ExecuteNonQuery();
            InitializeSchema();
        }
        catch { db.Dispose(); throw; }
    }

    private void InitializeSchema()
    {
        // Rebuild both legacy tables in one transaction. Binding a decimal string to an
        // INTEGER/NUMERIC column is unsafe: SQLite can silently store it as a binary float.
        using var tx = db.BeginTransaction();
        using var existing = db.CreateCommand(); existing.Transaction = tx;
        existing.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
        var fresh = (long)existing.ExecuteScalar()! == 0;
        EnsureDecimalTable(tx, "import_batches", "source TEXT NOT NULL,hash TEXT NOT NULL,imported_at TEXT NOT NULL,row_count INTEGER NOT NULL,raw_bytes INTEGER NOT NULL,billed_bytes TEXT NOT NULL,first_time TEXT NOT NULL,last_time TEXT NOT NULL,time_zone TEXT NOT NULL,PRIMARY KEY(source,hash)",
            "source,hash,imported_at,row_count,raw_bytes,CAST(billed_bytes AS TEXT),first_time,last_time,time_zone");
        EnsureDecimalTable(tx, "provider_records", "source TEXT NOT NULL,hash TEXT NOT NULL,row_number INTEGER NOT NULL,node_name TEXT NOT NULL,rate TEXT NOT NULL,raw_bytes INTEGER NOT NULL,billed_bytes TEXT NOT NULL,display_text TEXT NOT NULL,at_utc TEXT NOT NULL,PRIMARY KEY(source,hash,row_number)",
            "source,hash,row_number,node_name,rate,raw_bytes,CAST(billed_bytes AS TEXT),display_text,at_utc");
        if (fresh) Execute(tx, TrackingSchema);
        _ = HasTracking(tx);
        tx.Commit();
    }

    public bool TrackingAvailable { get { lock (gate) return HasTracking(null); } }

    /// <summary>Explicit, backed-up upgrade only. Legacy batch references are never inferred.</summary>
    public ProviderTrackingUpgrade EnableBatchTracking(string backupPath)
    {
        lock (gate)
        {
            if (HasTracking(null)) throw new InvalidOperationException("批次引用已启用，无需重复升级。");
            if (databasePath == ":memory:") throw new InvalidOperationException("升级要求已有的文件账本。");
            CheckMigrationPath(databasePath, mustExist: true);
            ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
            var backup = Path.GetFullPath(backupPath);
            if (StringComparer.OrdinalIgnoreCase.Equals(databasePath, backup))
                throw new ArgumentException("备份必须是不同的新文件。");
            CheckMigrationPath(backup, mustExist: false);
            using var held = new FileStream(databasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var timeout = db.DefaultTimeout;
            try
            {
                db.DefaultTimeout = 1;
                Execute(null, "PRAGMA locking_mode=EXCLUSIVE; BEGIN EXCLUSIVE; COMMIT;");
                if (HasTracking(null)) throw new InvalidOperationException("批次引用状态已变化，请重新检查。");
                CheckIntegrity(null);
                using (new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                using var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = backup, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
                snapshot.Open();
                db.BackupDatabase(snapshot);
                using var tx = db.BeginTransaction();
                CompareOriginal(tx, snapshot);
                Execute(tx, TrackingSchema);
                _ = HasTracking(tx);
                CompareOriginal(tx, snapshot);
                CheckIntegrity(tx);
                using var count = db.CreateCommand(); count.Transaction = tx;
                count.CommandText = "SELECT COUNT(*) FROM import_batches;";
                var batches = (long)count.ExecuteScalar()!;
                count.CommandText = "SELECT COUNT(*) FROM provider_records;";
                var records = (long)count.ExecuteScalar()!;
                tx.Commit();
                return new(backup, batches, records);
            }
            finally
            {
                // Disposing the exclusive SQLite connection is what releases its retained lock.
                db.Close();
                db.DefaultTimeout = timeout;
                db.Open();
            }
        }
    }

    private bool HasTracking(SqliteTransaction? tx)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('provider_batch_tracking','provider_batch_rows');";
        var count = (long)command.ExecuteScalar()!;
        if (count == 0) return false;
        if (count != 2) throw new InvalidOperationException("批次引用结构不完整，保留原数据，请检查备份。");
        foreach (var (table, expected) in new[]
        {
            ("provider_batch_tracking", "source:TEXT:1|hash:TEXT:2|file_row_count:INTEGER:0|file_raw_bytes:INTEGER:0|file_billed_bytes:TEXT:0"),
            ("provider_batch_rows", "source:TEXT:1|hash:TEXT:2|row_number:INTEGER:3|record_hash:TEXT:0|record_row_number:INTEGER:0")
        })
        {
            command.CommandText = $"PRAGMA table_info({table});";
            using var reader = command.ExecuteReader();
            var columns = new List<string>();
            while (reader.Read()) columns.Add($"{reader.GetString(1)}:{reader.GetString(2)}:{reader.GetInt64(5)}");
            if (string.Join('|', columns) != expected)
                throw new InvalidOperationException("批次引用结构不受支持，未执行操作。");
        }
        return true;
    }

    private void Execute(SqliteTransaction? tx, string sql)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = sql; command.ExecuteNonQuery();
    }

    private void CheckIntegrity(SqliteTransaction? tx)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "PRAGMA integrity_check;";
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetString(0) != "ok" || reader.Read())
            throw new InvalidOperationException("服务商账本完整性检查失败，未启用批次引用。");
    }

    private static void CheckMigrationPath(string path, bool mustExist)
    {
        if (OperatingSystem.IsWindows() && path.AsSpan(Path.GetPathRoot(path)!.Length).Contains(':'))
            throw new ArgumentException("升级路径不能使用备用数据流。");
        if (mustExist && !File.Exists(path)) throw new ArgumentException("需要已有的服务商账本。");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("升级路径不能使用文件链接。");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(path)!); parent is not null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("升级路径不能经过目录链接。");
        if (!Directory.Exists(Path.GetDirectoryName(path))) throw new ArgumentException("备份目录必须已存在。");
    }

    private void CompareOriginal(SqliteTransaction tx, SqliteConnection snapshot)
    {
        foreach (var sql in new[]
        {
            "SELECT type,name,tbl_name,sql FROM sqlite_master WHERE tbl_name IN ('import_batches','provider_records') ORDER BY type,name;",
            "SELECT * FROM import_batches ORDER BY source,hash;",
            "SELECT * FROM provider_records ORDER BY source,hash,row_number;"
        })
        {
            using var actual = db.CreateCommand(); actual.Transaction = tx; actual.CommandText = sql;
            using var saved = snapshot.CreateCommand(); saved.CommandText = sql;
            using var a = actual.ExecuteReader();
            using var b = saved.ExecuteReader();
            if (a.FieldCount != b.FieldCount) throw new InvalidOperationException("原服务商账本列发生变化。");
            while (a.Read())
            {
                if (!b.Read()) throw new InvalidOperationException("原服务商账本记录发生变化。");
                for (var i = 0; i < a.FieldCount; i++)
                    if (!Equals(a.GetValue(i), b.GetValue(i))) throw new InvalidOperationException("原服务商账本值发生变化。");
            }
            if (b.Read()) throw new InvalidOperationException("原服务商账本记录缺失。");
        }
    }

    private void EnsureDecimalTable(SqliteTransaction tx, string table, string columns, string legacyProjection)
    {
        // Identifiers and projections are fixed application constants, never CSV input.
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = $"CREATE TABLE IF NOT EXISTS {table}({columns});"; cmd.ExecuteNonQuery();
        cmd.CommandText = $"PRAGMA table_info({table});";
        string? billedType = null;
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) if (reader.GetString(1) == "billed_bytes") billedType = reader.GetString(2);
        if (string.Equals(billedType, "TEXT", StringComparison.OrdinalIgnoreCase)) return;
        if (!string.Equals(billedType, "INTEGER", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("服务商账本的计费量列类型不受支持；数据库未迁移。");
        cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE typeof(billed_bytes) <> 'integer' OR billed_bytes < 0;";
        if ((long)cmd.ExecuteScalar()! != 0)
            throw new InvalidOperationException("旧服务商账本含非整数或负数计费量，无法无损迁移；数据库未迁移。");
        cmd.CommandText = $"""
            CREATE TABLE {table}_decimal_migration({columns});
            INSERT INTO {table}_decimal_migration SELECT {legacyProjection} FROM {table};
            DROP TABLE {table};
            ALTER TABLE {table}_decimal_migration RENAME TO {table};
            """;
        cmd.ExecuteNonQuery();
    }
    public ImportPreview Preview(ImportRequest request)
    {
        lock (gate)
        {
            using var tx = db.BeginTransaction();
            var result = Prepare(request, tx).Preview;
            tx.Commit(); return result;
        }
    }
    public ImportPreview Commit(ImportRequest request)
    {
        lock (gate)
        {
            using var tx = db.BeginTransaction();
            var prepared = Prepare(request, tx);
            var preview = prepared.Preview;
            if (preview.Duplicate) return preview;
            if (preview.Warnings.Count != 0) throw new ArgumentException(preview.Warnings[0]);
            using var batch = db.CreateCommand(); batch.Transaction = tx;
            batch.CommandText = "INSERT INTO import_batches VALUES($source,$hash,$now,$count,$raw,$billed,$first,$last,$zone)";
            var source = request.Source.Trim();
            Add(batch, ("$source", source), ("$hash", preview.Hash), ("$now", Iso(DateTimeOffset.UtcNow)), ("$count", preview.NewRowCount), ("$raw", long.Parse(preview.NewRawBytes, CultureInfo.InvariantCulture)), ("$billed", preview.NewBilledBytes), ("$first", preview.FirstTime), ("$last", preview.LastTime), ("$zone", request.TimeZoneId));
            batch.ExecuteNonQuery();
            if (prepared.Tracking)
            {
                using var tracking = db.CreateCommand(); tracking.Transaction = tx;
                tracking.CommandText = """
                    INSERT INTO provider_batch_tracking VALUES($source,$hash,$count,$raw,$billed)
                    ON CONFLICT(source,hash) DO UPDATE SET file_row_count=excluded.file_row_count,
                        file_raw_bytes=excluded.file_raw_bytes,file_billed_bytes=excluded.file_billed_bytes;
                    """;
                Add(tracking, ("$source", source), ("$hash", preview.Hash), ("$count", preview.RowCount),
                    ("$raw", long.Parse(preview.RawBytes, CultureInfo.InvariantCulture)), ("$billed", preview.BilledBytes));
                tracking.ExecuteNonQuery();
            }
            using var next = db.CreateCommand(); next.Transaction = tx;
            next.CommandText = "SELECT COALESCE(MAX(row_number),-1) FROM provider_records WHERE source=$source AND hash=$hash;";
            Add(next, ("$source", source), ("$hash", preview.Hash));
            var number = checked(Convert.ToInt32(next.ExecuteScalar(), CultureInfo.InvariantCulture) + 1);
            for (var i = 0; i < prepared.Rows.Count; i++)
            {
                var row = prepared.Rows[i];
                var id = prepared.References[i];
                if (id is null)
                {
                    id = new(preview.Hash, number++);
                    using var cmd = db.CreateCommand(); cmd.Transaction = tx;
                    cmd.CommandText = "INSERT INTO provider_records VALUES($source,$hash,$row,$name,$rate,$raw,$billed,$display,$at)";
                    Add(cmd, ("$source", source), ("$hash", id.Value.Hash), ("$row", id.Value.Number), ("$name", row.Name), ("$rate", row.Rate.ToString()), ("$raw", row.Raw), ("$billed", row.Billed.ToString()), ("$display", row.Display), ("$at", Iso(row.At)));
                    cmd.ExecuteNonQuery();
                }
                if (prepared.Tracking)
                {
                    using var reference = db.CreateCommand(); reference.Transaction = tx;
                    reference.CommandText = "INSERT INTO provider_batch_rows VALUES($source,$hash,$row,$record,$number);";
                    Add(reference, ("$source", source), ("$hash", preview.Hash), ("$row", i),
                        ("$record", id.Value.Hash), ("$number", id.Value.Number));
                    reference.ExecuteNonQuery();
                }
            }
            tx.Commit(); return preview;
        }
    }
    public IReadOnlyList<ImportBatch> List()
    {
        lock (gate)
        {
            var tracking = HasTracking(null);
            using var cmd = db.CreateCommand(); cmd.CommandText = tracking ? """
                SELECT b.hash,b.source,b.imported_at,b.row_count,b.raw_bytes,b.billed_bytes,b.first_time,b.last_time,
                    t.file_row_count,t.file_raw_bytes,t.file_billed_bytes
                FROM import_batches b LEFT JOIN provider_batch_tracking t USING(source,hash)
                ORDER BY b.imported_at DESC LIMIT 200
                """ : "SELECT hash,source,imported_at,row_count,raw_bytes,billed_bytes,first_time,last_time FROM import_batches ORDER BY imported_at DESC LIMIT 200";
            using var r = cmd.ExecuteReader(); var result = new List<ImportBatch>();
            while (r.Read())
            {
                var complete = tracking && !r.IsDBNull(8);
                result.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt64(4).ToString(CultureInfo.InvariantCulture), r.GetString(5), r.GetString(6), r.GetString(7))
                {
                    ReferencesComplete = complete, FileRowCount = complete ? r.GetInt32(8) : null,
                    FileRawBytes = complete ? r.GetInt64(9).ToString(CultureInfo.InvariantCulture) : null,
                    FileBilledBytes = complete ? r.GetString(10) : null
                });
            }
            return result;
        }
    }
    public IReadOnlyList<string> Sources()
    {
        lock (gate)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT source FROM import_batches ORDER BY source";
            using var reader = cmd.ExecuteReader();
            var result = new List<string>();
            while (reader.Read()) result.Add(reader.GetString(0));
            return result;
        }
    }

    public IReadOnlyList<NodeInfo> GetNodes(string source)
    {
        ValidateSource(source);
        lock (gate)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT node_name FROM provider_records WHERE source=$source ORDER BY node_name";
            Add(cmd, ("$source", source));
            using var reader = cmd.ExecuteReader();
            var result = new List<NodeInfo>();
            while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(0)));
            return result;
        }
    }

    private sealed class QueryTotal
    {
        private BigInteger raw;
        private BigInteger billed;
        public void Add(long rawBytes, ProviderDecimal billedBytes) { raw += rawBytes; billed += billedBytes.ScaledUnits; }
        public string Raw => raw.ToString(CultureInfo.InvariantCulture);
        public string Billed => ProviderDecimal.FormatScaledUnits(billed);
    }
    private static void ValidateSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Length > 100)
            throw new ArgumentException("请选择有效的服务商来源。");
    }

    public ProviderRevokePreview PreviewRevoke(string source, string hash)
    {
        ValidateBatch(source, hash);
        lock (gate)
        {
            using var tx = db.BeginTransaction();
            var result = PrepareRevoke(source.Trim(), hash, tx).Preview;
            tx.Commit();
            return result;
        }
    }

    public ProviderRevokePreview Revoke(string source, string hash, string expectedToken)
    {
        ValidateBatch(source, hash);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedToken);
        lock (gate)
        {
            using var tx = db.BeginTransaction();
            var prepared = PrepareRevoke(source.Trim(), hash, tx);
            var preview = prepared.Preview;
            if (!preview.CanRevoke) throw new InvalidOperationException(preview.Reason);
            if (!string.Equals(preview.Token, expectedToken, StringComparison.Ordinal))
                throw new InvalidOperationException("批次或引用状态已变化，请重新预览后再撤销。");
            using var command = db.CreateCommand(); command.Transaction = tx;
            Add(command, ("$source", preview.Source), ("$hash", preview.Hash));
            command.CommandText = "DELETE FROM provider_batch_rows WHERE source=$source AND hash=$hash;";
            if (command.ExecuteNonQuery() != preview.RowCount)
                throw new InvalidOperationException("批次引用不完整，未撤销。");
            foreach (var id in prepared.Deleted)
            {
                using var remove = db.CreateCommand(); remove.Transaction = tx;
                remove.CommandText = "DELETE FROM provider_records WHERE source=$source AND hash=$hash AND row_number=$number;";
                Add(remove, ("$source", preview.Source), ("$hash", id.Hash), ("$number", id.Number));
                if (remove.ExecuteNonQuery() != 1) throw new InvalidOperationException("批次记录已变化，未撤销。");
            }
            command.CommandText = "DELETE FROM import_batches WHERE source=$source AND hash=$hash;";
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("批次已变化，未撤销。");
            // Keep the owner marker: retained physical rows are not legacy rows after revocation.
            tx.Commit();
            return preview;
        }
    }

    private static void ValidateBatch(string source, string hash)
    {
        ValidateSource(source);
        if (hash is not { Length: 64 } || !hash.All(char.IsAsciiHexDigit) || hash != hash.ToLowerInvariant())
            throw new ArgumentException("请选择有效的导入批次。");
    }

    private RevokePlan PrepareRevoke(string source, string hash, SqliteTransaction tx)
    {
        var tracking = HasTracking(tx);
        var token = SourceToken(source, hash, tracking, tx);
        RevokePlan Unavailable(string reason) => new(new(source, hash, token, false, reason,
            null, null, null, null, null, null, null), []);
        if (!tracking) return Unavailable("此服务商账本尚未启用批次引用，请先显式备份升级；原数据不会删除。");
        using var command = db.CreateCommand(); command.Transaction = tx;
        Add(command, ("$source", source), ("$hash", hash));
        command.CommandText = """
            SELECT t.file_row_count FROM import_batches b
            LEFT JOIN provider_batch_tracking t USING(source,hash)
            WHERE b.source=$source AND b.hash=$hash;
            """;
        var count = command.ExecuteScalar();
        if (count is null) return Unavailable("导入批次不存在或已撤销。");
        if (count is DBNull) return Unavailable("旧批次没有完整引用，无法安全撤销；其记录受保护。");
        var expectedCount = Convert.ToInt32(count, CultureInfo.InvariantCulture);
        command.CommandText = """
            SELECT r.hash,r.row_number,r.raw_bytes,r.billed_bytes,
                EXISTS(SELECT 1 FROM provider_batch_tracking owner WHERE owner.source=r.source AND owner.hash=r.hash)
                AND NOT EXISTS(
                    SELECT 1 FROM provider_batch_rows other JOIN import_batches active USING(source,hash)
                    WHERE other.source=r.source AND other.record_hash=r.hash
                        AND other.record_row_number=r.row_number AND other.hash<>$hash)
            FROM provider_batch_rows ref JOIN provider_records r
                ON r.source=ref.source AND r.hash=ref.record_hash AND r.row_number=ref.record_row_number
            WHERE ref.source=$source AND ref.hash=$hash ORDER BY ref.row_number;
            """;
        var deleted = new List<RecordId>();
        var deletedTotal = new QueryTotal();
        var retainedTotal = new QueryTotal();
        var seen = new HashSet<RecordId>();
        var rowCount = 0;
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                if (!ProviderDecimal.TryParse(reader.GetString(3), out var billed))
                    throw new InvalidOperationException("服务商账本含无法读取的计费量。");
                if (!seen.Add(new(reader.GetString(0), reader.GetInt32(1))))
                    return Unavailable("批次引用重复，无法安全撤销；原记录保留。");
                if (reader.GetInt64(4) != 0)
                {
                    deleted.Add(new(reader.GetString(0), reader.GetInt32(1)));
                    deletedTotal.Add(reader.GetInt64(2), billed);
                }
                else retainedTotal.Add(reader.GetInt64(2), billed);
                rowCount++;
            }
        if (rowCount != expectedCount || deleted.Distinct().Count() != deleted.Count)
            return Unavailable("批次引用不完整，无法安全撤销；原记录保留。");
        return new(new(source, hash, token, true, null, rowCount, deleted.Count, rowCount - deleted.Count,
            deletedTotal.Raw, deletedTotal.Billed, retainedTotal.Raw, retainedTotal.Billed), deleted);
    }

    private string SourceToken(string source, string batchHash, bool tracking, SqliteTransaction tx)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Append(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            digest.AppendData(BitConverter.GetBytes(bytes.Length));
            digest.AppendData(bytes);
        }
        Append(source);
        Append(batchHash);
        foreach (var table in tracking ? new[] { "import_batches", "provider_records", "provider_batch_tracking", "provider_batch_rows" }
                     : new[] { "import_batches", "provider_records" })
        {
            Append(table);
            using var command = db.CreateCommand(); command.Transaction = tx;
            command.CommandText = $"SELECT * FROM {table} WHERE source=$source ORDER BY hash" +
                (table is "provider_records" or "provider_batch_rows" ? ",row_number;" : ";");
            Add(command, ("$source", source));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                Append("row");
                for (var i = 0; i < reader.FieldCount; i++)
                    Append(reader.IsDBNull(i) ? "null" : reader.GetFieldType(i).Name + ":" +
                        Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture));
            }
        }
        return Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
    }

    public ProviderHistory Query(string source, QuerySpec query)
    {
        ValidateSource(source);
        if (query.End <= query.Start || query.Page < 0 || query.PageSize is < 1 or > 1000 ||
            query.Bucket is not ("hour" or "day" or "week" or "month" or "year"))
            throw new ArgumentException("统计范围、分桶或分页无效。");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(query.TimeZoneId);
        lock (gate)
        {
            // Stream one snapshot. SQLite SUM would coerce exact billing text to floating point.
            // Retain only group totals and the requested page, not every imported record.
            using var cmd = QueryCommand(source, query);
            var records = new List<ProviderRecord>();
            var totals = new QueryTotal();
            var series = new Dictionary<string, (DateTimeOffset First, QueryTotal Total)>(StringComparer.Ordinal);
            var nodes = new Dictionary<string, QueryTotal>(StringComparer.Ordinal);
            var count = 0;
            var offset = checked(query.Page * query.PageSize);
            using (var reader = cmd.ExecuteReader())
                while (reader.Read())
                {
                    var billedText = reader.GetString(3);
                    if (!ProviderDecimal.TryParse(billedText, out var billed))
                        throw new InvalidOperationException("服务商账本含无法读取的计费量。");
                    var raw = reader.GetInt64(2);
                    var atText = reader.GetString(4);
                    var at = DateTimeOffset.Parse(atText, CultureInfo.InvariantCulture);
                    var name = reader.GetString(0);
                    totals.Add(raw, billed);
                    if (!nodes.TryGetValue(name, out var node)) { node = new(); nodes.Add(name, node); }
                    node.Add(raw, billed);
                    var label = Ledger.BucketLabel(at, zone, query.Bucket);
                    if (!series.TryGetValue(label, out var bucket)) bucket = (at, new QueryTotal());
                    bucket.Total.Add(raw, billed);
                    series[label] = (at < bucket.First ? at : bucket.First, bucket.Total);
                    if (count >= offset && records.Count < query.PageSize) records.Add(ReadRecord(reader));
                    count = checked(count + 1);
                }
            return new(source, totals.Raw, totals.Billed,
                series.OrderBy(pair => pair.Value.First).Select(pair => new ProviderPoint(pair.Key, pair.Value.Total.Raw, pair.Value.Total.Billed)).ToArray(),
                nodes.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new ProviderNode(pair.Key, pair.Value.Raw, pair.Value.Billed)).ToArray(),
                records, count);
        }
    }

    public IReadOnlyList<ProviderRecord> Export(string source, QuerySpec query)
    {
        ValidateSource(source);
        if (query.End <= query.Start) throw new ArgumentException("统计范围无效。");
        lock (gate)
        {
            using var cmd = QueryCommand(source, query);
            cmd.CommandText += " LIMIT 100001";
            var rows = new List<ProviderRecord>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (rows.Count == 100000) throw new ArgumentException("导出最多 100000 条记录，请缩小日期范围或选择单个节点。");
                rows.Add(ReadRecord(reader));
            }
            return rows;
        }
    }

    private SqliteCommand QueryCommand(string source, QuerySpec query)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT node_name,rate,raw_bytes,billed_bytes,at_utc FROM provider_records
            WHERE source=$source AND at_utc >= $start AND at_utc < $end
                AND ($node IS NULL OR node_name=$node)
            ORDER BY at_utc DESC,hash,row_number
            """;
        Add(cmd, ("$source", source), ("$start", Iso(query.Start)), ("$end", Iso(query.End)),
            ("$node", (object?)query.NodeKey ?? DBNull.Value));
        return cmd;
    }
    private static ProviderRecord ReadRecord(SqliteDataReader reader) =>
        new(reader.GetString(4), reader.GetString(0), reader.GetString(1),
            reader.GetInt64(2).ToString(CultureInfo.InvariantCulture), reader.GetString(3));

    private PreparedImport Prepare(ImportRequest request, SqliteTransaction tx)
    {
        if (string.IsNullOrWhiteSpace(request.Source) || request.Source.Trim().Length > 100) throw new ArgumentException("请填写 1–100 字符的服务商/账户来源。");
        if (request.Csv is null || Encoding.UTF8.GetByteCount(request.Csv) > MaxCsvBytes) throw new ArgumentException("CSV 上限为 4 MB。");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(request.TimeZoneId);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Csv))).ToLowerInvariant();
        using var reader = new TextFieldParser(new StringReader(request.Csv.TrimStart('\uFEFF'))) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        reader.SetDelimiters(",");
        var header = reader.ReadFields();
        string[] expected = ["node_name", "rate", "origin_traffic", "origin_x_rate", "traffic", "log_time"];
        if (header is null || !header.SequenceEqual(expected)) throw new ArgumentException("CSV 表头须为 node_name,rate,origin_traffic,origin_x_rate,traffic,log_time。");
        var rows = new List<Row>(); long raw = 0;
        while (!reader.EndOfData)
        {
            var fields = reader.ReadFields();
            if (fields is null) break;
            if (fields.Length != 6) throw new ArgumentException("CSV 每条数据记录须有六列。");
            if (rows.Count >= MaxRows) throw new ArgumentException($"CSV 最多 {MaxRows.ToString("N0", CultureInfo.InvariantCulture)} 条数据记录（不含表头）。");
            if (string.IsNullOrWhiteSpace(fields[0]) || fields[0].Length > 500 || fields[4].Length > 500) throw new ArgumentException($"第 {rows.Count + 2} 行名称或显示文本无效。");
            if (!ProviderDecimal.TryParse(fields[1], out var rate) ||
                !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var rowRaw) || rowRaw < 0 ||
                !ProviderDecimal.TryParse(fields[3], out var rowBilled)) throw new ArgumentException($"第 {rows.Count + 2} 行数值无效；原始量须为非负 Int64 整数字节，倍率与计费量须为可无损表示的非负十进制数（96 位系数、最多 28 位小数）。");
            if (!DateTime.TryParseExact(fields[5], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) throw new ArgumentException($"第 {rows.Count + 2} 行时间须为 yyyy-MM-dd HH:mm:ss。");
            local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local)) throw new ArgumentException("CSV 含夏令时跳变/重复时间，无法无歧义导入。请选择正确的固定时区重新导出。");
            var at = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
            checked { raw += rowRaw; }
            rows.Add(new(fields[0], rate, rowRaw, rowBilled, fields[4], at));
        }
        if (rows.Count == 0) throw new ArgumentException("CSV 没有数据行。");
        var billed = ProviderDecimal.Sum(rows.Select(row => row.Billed));
        var first = Iso(rows.Min(r => r.At)); var last = Iso(rows.Max(r => r.At));
        using var check = db.CreateCommand(); check.Transaction = tx;
        check.CommandText = "SELECT COUNT(*) FROM import_batches WHERE source=$source AND hash=$hash";
        Add(check, ("$source", request.Source.Trim()), ("$hash", hash));
        var alreadyImported = (long)check.ExecuteScalar()! != 0;
        var tracking = HasTracking(tx);
        var additions = new List<Row>();
        var references = new List<RecordId?>();
        var conflicts = new HashSet<(string Name, long At)>();
        if (!alreadyImported)
        {
            var keys = rows.Select(row => row.Key).ToHashSet();
            var groups = keys.Select(key => (key.Name, key.At)).ToHashSet();
            var counts = new Dictionary<BillingKey, Queue<RecordId>>();
            using var command = db.CreateCommand(); command.Transaction = tx;
            command.CommandText = """
                SELECT node_name,rate,raw_bytes,billed_bytes,at_utc,hash,row_number FROM provider_records
                WHERE source=$source AND at_utc >= $first AND at_utc <= $last
                ORDER BY hash,row_number
                """;
            Add(command, ("$source", request.Source.Trim()), ("$first", first), ("$last", last));
            using (var existing = command.ExecuteReader())
                while (existing.Read())
                {
                    if (!ProviderDecimal.TryParse(existing.GetString(1), out var rate) ||
                        !ProviderDecimal.TryParse(existing.GetString(3), out var amount))
                        throw new InvalidOperationException("服务商账本含无法读取的倍率或计费量。");
                    var key = new BillingKey(existing.GetString(0), rate, existing.GetInt64(2), amount,
                        DateTimeOffset.Parse(existing.GetString(4), CultureInfo.InvariantCulture).UtcTicks);
                    if (keys.Contains(key))
                    {
                        if (!counts.TryGetValue(key, out var occurrences)) counts[key] = occurrences = new();
                        occurrences.Enqueue(new(existing.GetString(5), existing.GetInt32(6)));
                    }
                    else if (groups.Contains((key.Name, key.At))) conflicts.Add((key.Name, key.At));
                }
            // Match each stored occurrence once, preserving legal repeats within a snapshot.
            foreach (var row in rows)
            {
                if (counts.TryGetValue(row.Key, out var remaining) && remaining.Count > 0)
                    references.Add(remaining.Dequeue());
                else { additions.Add(row); references.Add(null); }
            }
            // A changed row alongside fewer old occurrences may be a revision, not new billing.
            foreach (var (key, remaining) in counts)
                if (remaining.Count > 0) conflicts.Add((key.Name, key.At));
        }
        var newRaw = additions.Aggregate(0L, (sum, row) => checked(sum + row.Raw));
        var newBilled = ProviderDecimal.Sum(additions.Select(row => row.Billed));
        IReadOnlyList<string> warnings = additions.Any(row => conflicts.Contains((row.Name, row.At.UtcTicks))) ?
            ["同一来源的同时间、同名记录有计费字段或出现次数变化，且新文件未保留全部原记录，无法判断是否修订。请核对来源、时区或重新导出；本批不会导入，也不会覆盖旧记录。"] : [];
        return new(new ImportPreview(hash, rows.Count, raw.ToString(CultureInfo.InvariantCulture), billed.ToString(), first, last,
            alreadyImported || (!tracking && additions.Count == 0), warnings)
        {
            NewRowCount = additions.Count, SkippedRowCount = rows.Count - additions.Count,
            NewRawBytes = newRaw.ToString(CultureInfo.InvariantCulture), NewBilledBytes = newBilled.ToString()
        }, rows, references, tracking);
    }
    private static void Add(SqliteCommand cmd, params (string Name, object Value)[] values) { foreach (var item in values) cmd.Parameters.AddWithValue(item.Name, item.Value); }
    private static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    public void Dispose() { lock (gate) db.Dispose(); }
}
