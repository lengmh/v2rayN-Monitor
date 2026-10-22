using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using V2rayNMonitor.Core;
using Xunit;

namespace V2rayNMonitor.Tests;

// Every CSV and legacy database below is generated synthetic data.
public sealed class ProviderDecimalImportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "monitor-decimal-tests-" + Guid.NewGuid());
    private string PathFor(string name = "provider.db") => Path.Combine(root, name);
    private const string Header = "node_name,rate,origin_traffic,origin_x_rate,traffic,log_time\n";
    private static ImportRequest Request(params string[] rows) => new("synthetic-decimal", "UTC", Header + string.Join('\n', rows));
    private static string Row(string billed, string raw = "1", string rate = "0.8") => $"Synthetic-A,{rate},{raw},{billed},synthetic,2026-01-01 00:00:00";
    public ProviderDecimalImportTests() => Directory.CreateDirectory(root);

    [Fact]
    public void QueryPreservesLargeCrossBatchTotalsAndSeparatesSourcesAndCalendarDays()
    {
        using var imports = new ProviderImports(PathFor());
        var first = Request(Row("79228162514264337593543950335"));
        imports.Commit(first);
        imports.Commit(first with { Csv = first.Csv.Replace("2026-01-01", "2026-01-02") });
        imports.Commit(first with { Source = "another-source" });
        var query = new QuerySpec(new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero), "Asia/Shanghai");
        var result = imports.Query(first.Source, query);
        Assert.Equal("158456325028528675187087900670", result.BilledBytes);
        Assert.Equal("2", result.RawBytes);
        Assert.Equal(2, result.Series.Count);
        Assert.Equal("2026-01-01", result.Series[0].Label);
        Assert.Equal("2026-01-02", result.Series[1].Label);
        Assert.Equal(2, imports.Sources().Count);
        Assert.Single(imports.GetNodes(first.Source));
        Assert.Equal("1", imports.Query("another-source", query).RawBytes);
        var page = imports.Query(first.Source, query with { Page = 1, PageSize = 1 });
        Assert.Equal(result.BilledBytes, page.BilledBytes);
        Assert.Single(page.Records);
        Assert.Empty(imports.Query(first.Source, query with { NodeKey = "missing" }).Records);
    }

    [Fact]
    public void FiftyThousandRowsKeepExactTotalsAndBoundedPagesWhileExportIsOneSnapshot()
    {
        using var imports = new ProviderImports(PathFor());
        var request = new ImportRequest("synthetic-large-query", "UTC", Header + string.Concat(Enumerable.Repeat(Row("0.8") + "\n", 50000)));
        imports.Commit(request);
        var query = new QuerySpec(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero), "UTC", Page: 7, PageSize: 50);
        var result = imports.Query(request.Source, query);
        Assert.Equal(50000, result.RecordCount);
        Assert.Equal(50, result.Records.Count);
        Assert.Equal("50000", result.RawBytes);
        Assert.Equal("40000", result.BilledBytes);
        var exported = imports.Export(request.Source, query);
        Assert.Equal(50000, exported.Count);
        imports.Commit(Request(Row("3")) with { Source = request.Source, Csv = (Header + Row("3")).Replace("2026-01-01", "2026-01-02") });
        Assert.Equal(50000, exported.Count);
        Assert.All(exported, row => Assert.Equal("0.8", row.BilledBytes));
        Assert.Equal(50001, imports.Query(request.Source, query).RecordCount);
    }

    [Fact]
    public void DecimalPreviewCommitReopenAndListRetainCsvValuesExactlyAndStayIdempotent()
    {
        var request = Request(Row("1000", "1000", "1"), Row("800", "1000"), Row("800.800000000123", "1001"));
        using (var imports = new ProviderImports(PathFor()))
        {
            var preview = imports.Preview(request);
            Assert.Equal("3001", preview.RawBytes);
            Assert.Equal("2600.800000000123", preview.BilledBytes);
            Assert.Empty(imports.List());
            var result = imports.Commit(request);
            Assert.Equal(preview.RawBytes, result.RawBytes);
            Assert.Equal(preview.BilledBytes, result.BilledBytes);
            Assert.False(result.Duplicate);
        }
        using (var reopened = new ProviderImports(PathFor()))
        {
            var batch = Assert.Single(reopened.List());
            Assert.Equal("3001", batch.RawBytes);
            Assert.Equal("2600.800000000123", batch.BilledBytes);
            Assert.True(reopened.Preview(request).Duplicate);
            Assert.True(reopened.Commit(request).Duplicate);
        }
        using var db = Open();
        Assert.Equal("2600.800000000123", Scalar(db, "SELECT billed_bytes FROM import_batches"));
        Assert.Equal("text", Scalar(db, "SELECT typeof(billed_bytes) FROM import_batches"));
        Assert.Equal("800.800000000123", Scalar(db, "SELECT billed_bytes FROM provider_records WHERE row_number=2"));
        Assert.Equal("text", Scalar(db, "SELECT typeof(billed_bytes) FROM provider_records WHERE row_number=2"));
        Assert.Equal("0.8", Scalar(db, "SELECT rate FROM provider_records WHERE row_number=2"));
        Assert.Equal(3L, Scalar(db, "SELECT COUNT(*) FROM provider_records"));
    }

    [Theory]
    [InlineData("9007199254740993.123456789012", "9007199254740993.123456789012")]
    [InlineData("79228162514264337593543950335", "79228162514264337593543950335")]
    [InlineData("0.0000000000000000000000000001", "0.0000000000000000000000000001")]
    [InlineData("000800.8000000001230000000000000000000", "800.800000000123")]
    [InlineData("000.00000000000000000000000000000", "0")]
    public void BoundedValuesAndInsignificantZerosAreStoredWithoutRounding(string value, string canonical)
    {
        // A deliberately unrelated rate and raw amount prove that billed values are never recomputed.
        using (var imports = new ProviderImports(PathFor()))
        {
            var request = Request(Row(value, "7", value));
            Assert.Equal(canonical, imports.Preview(request).BilledBytes);
            Assert.Equal(canonical, imports.Commit(request).BilledBytes);
        }
        using var reopened = new ProviderImports(PathFor());
        Assert.Equal(canonical, Assert.Single(reopened.List()).BilledBytes);
        using var db = Open();
        Assert.Equal(canonical, Scalar(db, "SELECT rate FROM provider_records"));
        Assert.Equal(canonical, Scalar(db, "SELECT billed_bytes FROM provider_records"));
        Assert.Equal("text", Scalar(db, "SELECT typeof(billed_bytes) FROM provider_records"));
    }

    [Fact]
    public void AdditionNormalizesExactCarryWithoutLosingFractionalDigits()
    {
        using var imports = new ProviderImports(PathFor());
        var request = Request(Row("0.0000000000000000000000000001"), Row("0.9999999999999999999999999999"));
        Assert.Equal("1", imports.Commit(request).BilledBytes);
        Assert.Equal("1", Assert.Single(imports.List()).BilledBytes);
    }

    [Theory]
    [InlineData("79228162514264337593543950334", "0.1", "0.9")]
    [InlineData("0.1", "0.9", "79228162514264337593543950334")]
    [InlineData("0.9", "79228162514264337593543950334", "0.1")]
    public void FinalExactCarryIsIndependentOfIntermediateRepresentationAndRowOrder(string first, string second, string third)
    {
        using var imports = new ProviderImports(PathFor());
        var request = Request(Row(first), Row(second), Row(third));
        Assert.Equal("79228162514264337593543950335", imports.Preview(request).BilledBytes);
        Assert.Equal("79228162514264337593543950335", imports.Commit(request).BilledBytes);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e3")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("1.2.3")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("0.00000000000000000000000000001")]
    [InlineData("1.23456789012345678901234567891")]
    [InlineData("79228162514264337593543950336")]
    [InlineData("7922816251426433759354395033.6")]
    public void InvalidOrInexactRatesAndBilledValuesRejectWholeBatch(string invalid)
    {
        using var imports = new ProviderImports(PathFor());
        foreach (var invalidRow in new[] { Row(invalid), Row("1", rate: invalid) })
        {
            var request = Request(Row("0.1"), invalidRow);
            Assert.Contains("第 3 行数值无效", Assert.Throws<ArgumentException>(() => imports.Preview(request)).Message);
            Assert.Throws<ArgumentException>(() => imports.Commit(request));
        }
        Assert.Empty(imports.List());
        using var db = Open(); Assert.Equal(0L, Scalar(db, "SELECT COUNT(*) FROM provider_records"));
    }

    [Theory]
    [InlineData("1.1")]
    [InlineData("-1")]
    [InlineData("9223372036854775808")]
    public void RawBytesRemainNonnegativeInt64Integers(string raw)
    {
        using var imports = new ProviderImports(PathFor());
        Assert.Throws<ArgumentException>(() => imports.Commit(Request(Row("0.1", raw))));
        Assert.Empty(imports.List());
    }

    [Theory]
    [InlineData("79228162514264337593543950335", "1")]
    [InlineData("7922816251426433759354395033.5", "0.01")]
    public void OverflowOrUnrepresentableExactTotalRejectsBatchRatherThanRounding(string first, string second)
    {
        using var imports = new ProviderImports(PathFor());
        var request = Request(Row(first), Row(second));
        Assert.Throws<OverflowException>(() => imports.Preview(request));
        Assert.Throws<OverflowException>(() => imports.Commit(request));
        Assert.Empty(imports.List());
        using var db = Open(); Assert.Equal(0L, Scalar(db, "SELECT COUNT(*) FROM provider_records"));
    }

    [Fact]
    public void MidCommitSqliteFailureRollsBackBothDecimalRecordsAndBatchAndAllowsRetry()
    {
        using var imports = new ProviderImports(PathFor());
        using var db = Open();
        Execute(db, "CREATE TRIGGER synthetic_failure BEFORE INSERT ON provider_records WHEN NEW.row_number=1 BEGIN SELECT RAISE(ABORT, 'synthetic failure'); END;");
        var request = Request(Row("800.800000000123"), Row("0.2"));
        Assert.Throws<SqliteException>(() => imports.Commit(request));
        Assert.Empty(imports.List());
        Assert.Equal(0L, Scalar(db, "SELECT COUNT(*) FROM provider_records"));
        Execute(db, "DROP TRIGGER synthetic_failure;");
        Assert.Equal("801.000000000123", imports.Commit(request).BilledBytes);
        Assert.True(imports.Commit(request).Duplicate);
    }

    [Fact]
    public void LegacyIntegerDatabaseMigratesExactlyAndKeepsFileIdempotencyAcrossReopen()
    {
        var request = CreateLegacy();
        using (var imports = new ProviderImports(PathFor()))
        {
            var batch = Assert.Single(imports.List());
            Assert.Equal("9223372036854775807", batch.BilledBytes);
            Assert.Equal("9223372036854775807", batch.RawBytes);
            Assert.True(imports.Commit(request).Duplicate);
            Assert.Equal("0.1234567890123456789012345678", imports.Commit(Request(Row("0.1234567890123456789012345678")) with { Source = "synthetic-new" }).BilledBytes);
        }
        using var reopened = new ProviderImports(PathFor());
        Assert.Equal(2, reopened.List().Count);
        using var db = Open();
        foreach (var table in new[] { "import_batches", "provider_records" })
        {
            Assert.Equal("TEXT", Scalar(db, $"SELECT type FROM pragma_table_info('{table}') WHERE name='billed_bytes'"));
            Assert.Equal("9223372036854775807", Scalar(db, $"SELECT billed_bytes FROM {table} WHERE source='synthetic-decimal'"));
            Assert.Equal("text", Scalar(db, $"SELECT typeof(billed_bytes) FROM {table} WHERE source='synthetic-decimal'"));
            Assert.Equal("integer", Scalar(db, $"SELECT typeof(raw_bytes) FROM {table} WHERE source='synthetic-decimal'"));
        }
    }

    [Fact]
    public void FailedSecondTableMigrationRollsBackFirstTableAndRetainsLegacyData()
    {
        CreateLegacy();
        using (var db = Open()) Execute(db, "UPDATE provider_records SET billed_bytes=0.5;");
        Assert.Throws<InvalidOperationException>(() => new ProviderImports(PathFor()));
        using var check = Open();
        foreach (var table in new[] { "import_batches", "provider_records" })
            Assert.Equal("INTEGER", Scalar(check, $"SELECT type FROM pragma_table_info('{table}') WHERE name='billed_bytes'"));
        Assert.Equal(long.MaxValue, Scalar(check, "SELECT billed_bytes FROM import_batches"));
        Assert.Equal("real", Scalar(check, "SELECT typeof(billed_bytes) FROM provider_records"));
        Assert.Equal(0L, Scalar(check, "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE '%_decimal_migration'"));
        // The failed constructor releases its connection, and a repaired synthetic legacy DB can be retried.
        Execute(check, "UPDATE provider_records SET billed_bytes=9223372036854775807;");
        using var retry = new ProviderImports(PathFor()); Assert.Single(retry.List());
    }

    [Fact]
    public void LegacyProviderOpenDoesNotEnableReferencesAndExplicitUpgradePreservesBothTablesAndOldOwnership()
    {
        var originalRequest = CreateLegacy();
        var rewritten = originalRequest with { Csv = originalRequest.Csv.Replace(",synthetic,", ",different display,", StringComparison.Ordinal) };
        using (var imports = new ProviderImports(PathFor()))
        {
            Assert.False(imports.TrackingAvailable);
            Assert.True(imports.Commit(rewritten).Duplicate); // Old zero-new behavior is retained until explicit enable.
            using (var check = Open())
                Assert.Equal(0L, Scalar(check, "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'provider_batch_%';"));
            var before = BaseSnapshot(PathFor());
            var backup = PathFor("before-references.db");
            var enabled = imports.EnableBatchTracking(backup);
            Assert.Equal(backup, enabled.BackupPath);
            Assert.Equal(1, enabled.PreservedBatches);
            Assert.Equal(1, enabled.PreservedRecords);
            Assert.True(imports.TrackingAvailable);
            Assert.Equal(before, BaseSnapshot(backup));
            Assert.Equal(before, BaseSnapshot(PathFor()));
            using (var saved = new SqliteConnection($"Data Source={backup};Mode=ReadOnly;Pooling=False"))
            {
                saved.Open();
                Assert.Equal(0L, Scalar(saved, "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'provider_batch_%';"));
            }
            var legacy = Assert.Single(imports.List());
            Assert.False(legacy.ReferencesComplete);
            Assert.Null(legacy.FileRowCount);
            var denied = imports.PreviewRevoke(legacy.Source, legacy.Hash);
            Assert.False(denied.CanRevoke);
            Assert.Null(denied.DeletedRowCount);
            Assert.Null(denied.RetainedRawBytes);
            Assert.Contains("旧批次", denied.Reason);
            Assert.Throws<InvalidOperationException>(() => imports.Revoke(denied.Source, denied.Hash, denied.Token));

            var tracked = imports.Commit(rewritten);
            Assert.False(tracked.Duplicate);
            Assert.Equal(0, tracked.NewRowCount);
            var revoke = imports.PreviewRevoke(rewritten.Source, tracked.Hash);
            Assert.True(revoke.CanRevoke);
            Assert.Equal(0, revoke.DeletedRowCount);
            Assert.Equal(1, revoke.RetainedRowCount);
            Assert.Equal("9223372036854775807", revoke.RetainedRawBytes);
            Assert.Equal("9223372036854775807", revoke.RetainedBilledBytes);
            imports.Revoke(revoke.Source, revoke.Hash, revoke.Token);
            Assert.Equal(before, BaseSnapshot(PathFor()));
        }
        using var reopened = new ProviderImports(PathFor());
        Assert.True(reopened.TrackingAvailable);
        Assert.False(Assert.Single(reopened.List()).ReferencesComplete);
    }

    [Fact]
    public void ExplicitTrackingUpgradeRefusesExistingBackupAndDoesNotChangeLegacyTables()
    {
        CreateLegacy();
        using var imports = new ProviderImports(PathFor());
        var before = BaseSnapshot(PathFor());
        var backup = PathFor("existing-backup.db");
        File.WriteAllText(backup, "Synthetic existing backup");
        Assert.Throws<IOException>(() => imports.EnableBatchTracking(backup));
        Assert.False(imports.TrackingAvailable);
        Assert.Equal(before, BaseSnapshot(PathFor()));
        Assert.Equal("Synthetic existing backup", File.ReadAllText(backup));
        Assert.Single(imports.List()); // Its connection can be reused after the failed exclusive step.
    }

    [Fact]
    public void ExplicitTrackingUpgradeDdlFailureRollsBackAndRetainsFreshBackup()
    {
        CreateLegacy();
        using (var occupied = Open())
            Execute(occupied, "CREATE TABLE provider_batch_rows_record(synthetic TEXT);");
        using var imports = new ProviderImports(PathFor());
        var before = BaseSnapshot(PathFor());
        var backup = PathFor("failed-upgrade-backup.db");
        Assert.Throws<SqliteException>(() => imports.EnableBatchTracking(backup));
        Assert.False(imports.TrackingAvailable);
        Assert.Equal(before, BaseSnapshot(PathFor()));
        Assert.Equal(before, BaseSnapshot(backup));
        using var check = Open();
        Assert.Equal(0L, Scalar(check, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('provider_batch_tracking','provider_batch_rows');"));
        Assert.Single(imports.List());
    }

    [Fact]
    public void BatchRevokePreviewKeepsInt64BoundaryAndSubByteDecimalWithoutRounding()
    {
        using var imports = new ProviderImports(PathFor());
        var committed = imports.Commit(Request(Row("9007199254740993.123456789012", "9007199254740993")));
        var tinyRequest = Request(Row("0.0000000000000000000000000001", "7"));
        var tiny = imports.Commit(tinyRequest with { Csv = tinyRequest.Csv.Replace("2026-01-01", "2026-01-02", StringComparison.Ordinal) });
        var preview = imports.PreviewRevoke("synthetic-decimal", committed.Hash);
        Assert.Equal("9007199254740993", preview.DeletedRawBytes);
        Assert.Equal("9007199254740993.123456789012", preview.DeletedBilledBytes);
        var total = imports.Query("synthetic-decimal", new(new(2025, 12, 31, 0, 0, 0, TimeSpan.Zero),
            new(2026, 1, 3, 0, 0, 0, TimeSpan.Zero), "UTC"));
        Assert.Equal("9007199254741000", total.RawBytes);
        Assert.Equal("9007199254740993.1234567890120000000000000001", total.BilledBytes);
        imports.Revoke(preview.Source, preview.Hash, preview.Token);
        var small = imports.PreviewRevoke("synthetic-decimal", tiny.Hash);
        Assert.Equal("7", small.DeletedRawBytes);
        Assert.Equal("0.0000000000000000000000000001", small.DeletedBilledBytes);
        imports.Revoke(small.Source, small.Hash, small.Token);
        Assert.Empty(imports.List());
    }

    private static string BaseSnapshot(string path)
    {
        using var db = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        db.Open();
        var rows = new List<object?[]>();
        foreach (var sql in new[]
        {
            "SELECT type,name,tbl_name,sql FROM sqlite_master WHERE tbl_name IN ('import_batches','provider_records') ORDER BY type,name;",
            "SELECT * FROM import_batches ORDER BY source,hash;",
            "SELECT * FROM provider_records ORDER BY source,hash,row_number;"
        })
        {
            using var command = db.CreateCommand(); command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read()) rows.Add([sql, .. Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue)]);
        }
        return System.Text.Json.JsonSerializer.Serialize(rows);
    }

    private ImportRequest CreateLegacy()
    {
        var request = Request(Row("9223372036854775807", "9223372036854775807", "1"));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Csv))).ToLowerInvariant();
        using var db = Open();
        Execute(db, """
            CREATE TABLE import_batches(source TEXT NOT NULL,hash TEXT NOT NULL,imported_at TEXT NOT NULL,row_count INTEGER NOT NULL,raw_bytes INTEGER NOT NULL,billed_bytes INTEGER NOT NULL,first_time TEXT NOT NULL,last_time TEXT NOT NULL,time_zone TEXT NOT NULL,PRIMARY KEY(source,hash));
            CREATE TABLE provider_records(source TEXT NOT NULL,hash TEXT NOT NULL,row_number INTEGER NOT NULL,node_name TEXT NOT NULL,rate TEXT NOT NULL,raw_bytes INTEGER NOT NULL,billed_bytes INTEGER NOT NULL,display_text TEXT NOT NULL,at_utc TEXT NOT NULL,PRIMARY KEY(source,hash,row_number));
            """);
        using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO import_batches VALUES('synthetic-decimal',$hash,'2026-01-02T00:00:00.0000000Z',1,9223372036854775807,9223372036854775807,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z','UTC');
            INSERT INTO provider_records VALUES('synthetic-decimal',$hash,0,'Synthetic-A','1',9223372036854775807,9223372036854775807,'synthetic','2026-01-01T00:00:00.0000000Z');
            """;
        command.Parameters.AddWithValue("$hash", hash); command.ExecuteNonQuery();
        return request;
    }
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = PathFor(), Pooling = false }.ToString());
        db.Open(); return db;
    }
    private static object? Scalar(SqliteConnection db, string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; return cmd.ExecuteScalar(); }
    private static void Execute(SqliteConnection db, string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
    public void Dispose() => Directory.Delete(root, true);
}
