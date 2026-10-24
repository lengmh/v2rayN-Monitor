using System.Globalization;
using System.Numerics;
using System.Text;
using Microsoft.Data.Sqlite;
using V2rayNMonitor.Core;
using Xunit;
namespace V2rayNMonitor.Tests;
public sealed class ProviderImportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "monitor-tests-" + Guid.NewGuid());
    private readonly ProviderImports imports;
    public ProviderImportTests() { Directory.CreateDirectory(root); imports = new(Path.Combine(root, "provider.db")); }
    private const string Header = "node_name,rate,origin_traffic,origin_x_rate,traffic,log_time\n";
    private const string SyntheticRow = "Synthetic-A,1,1,1,1 B,2026-01-01 00:00:00\n";
    private static ImportRequest Request(string rows) => new("synthetic-provider", "UTC", Header + rows);
    [Fact] public void DuplicateFileIsIdempotentAndLegitimateDuplicateRowsRemain()
    {
        var request = Request("Synthetic-A,0,2097152,0,0 B,2026-01-01 10:00:00\nSynthetic-A,0,2097152,0,0 B,2026-01-01 10:00:00");
        Assert.Equal(2, imports.Commit(request).RowCount);
        Assert.True(imports.Commit(request).Duplicate);
        var batch = Assert.Single(imports.List()); Assert.Equal("4194304", batch.RawBytes); Assert.Equal("0", batch.BilledBytes);
    }
    [Fact] public void OverlappingExportAddsOnlyUnseenBillingRows()
    {
        imports.Commit(Request("Synthetic-A,0.8,10,8.000000000123,display,2026-01-01 10:00:00\nSynthetic-B,1,20,20,display,2026-01-01 11:00:00"));
        var overlap = Request("Synthetic-B,1.00,20,20.000,changed display,2026-01-01 11:00:00\nSynthetic-C,0.5,30,15.000000000123,display,2026-01-01 12:00:00");
        var preview = imports.Preview(overlap);
        Assert.Empty(preview.Warnings);
        Assert.Equal(2, preview.RowCount);
        Assert.Equal(1, preview.NewRowCount);
        Assert.Equal(1, preview.SkippedRowCount);
        Assert.Equal("30", preview.NewRawBytes);
        Assert.Equal("15.000000000123", preview.NewBilledBytes);
        var committed = imports.Commit(overlap);
        Assert.Equal(1, committed.NewRowCount);
        Assert.Equal(1, committed.SkippedRowCount);
        Assert.Equal("15.000000000123", committed.NewBilledBytes);
        var result = imports.Query(overlap.Source, new(new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), "UTC", "hour"));
        Assert.Equal(3, result.RecordCount);
        Assert.Equal("60", result.RawBytes);
        Assert.Equal("43.000000000246", result.BilledBytes);
        Assert.Equal(2, imports.List().Count);
        Assert.Equal(1, imports.List().Single(b => b.Hash == preview.Hash).RowCount);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OverlappingSnapshotsUseOccurrenceCountsAndRemainIdempotentAcrossReopen(bool reverse)
    {
        const string x = "Synthetic-X,0.8,10,8.000000000123,display,2026-01-01 10:00:00\n";
        var a = Request(x + x + "Synthetic-Y,1,20,20,display,2026-01-01 11:00:00\n");
        var b = Request(x + x + x + "Synthetic-Z,0.5,30,15.000000000123,display,2026-01-01 12:00:00\n");
        imports.Commit(reverse ? b : a);
        var pending = imports.Preview(reverse ? a : b);
        Assert.Equal(reverse ? 1 : 2, pending.NewRowCount);
        Assert.Equal(2, pending.SkippedRowCount);
        imports.Commit(reverse ? a : b);
        var query = new QuerySpec(new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), "UTC", "hour");
        var result = imports.Query(a.Source, query);
        Assert.Equal(5, result.RecordCount);
        Assert.Equal("80", result.RawBytes);
        Assert.Equal("59.000000000492", result.BilledBytes);
        Assert.Equal(3, result.Records.Count(r => r.NodeName == "Synthetic-X"));
        Assert.Equal(2, imports.List().Count);

        // Same snapshot with different text/order has no new occurrences.
        var rewritten = a with { Csv = "\uFEFF" + Header + string.Join("\r\n", a.Csv[Header.Length..].TrimEnd('\n').Split('\n').Reverse())
            .Replace(",0.8,10,8.000000000123,", ",0.800,00010,8.0000000001230,").Replace(",display,", ",another display,") + "\r\n" };
        using var reopened = new ProviderImports(Path.Combine(root, "provider.db"));
        var duplicate = reopened.Commit(rewritten);
        Assert.False(duplicate.Duplicate); // A distinct zero-new snapshot still owns its complete references.
        Assert.Equal(0, duplicate.NewRowCount);
        Assert.Equal(3, duplicate.SkippedRowCount);
        Assert.Equal("0", duplicate.NewRawBytes);
        Assert.Equal("0", duplicate.NewBilledBytes);
        Assert.Equal(3, reopened.List().Count);
        Assert.Equal(5, reopened.Query(a.Source, query).RecordCount);
        Assert.Equal(4, reopened.Commit(b with { Source = "another-provider" }).NewRowCount);
        Assert.Equal(4, reopened.Query("another-provider", query).RecordCount);
    }
    [Fact] public void CommitRechecksOccurrencesAfterPreview()
    {
        var first = Request("Synthetic-A,1,10,10,display,2026-01-01 10:00:00");
        var next = Request("Synthetic-A,1.00,10,10.00,changed display,2026-01-01 10:00:00\nSynthetic-B,1,20,20,display,2026-01-01 11:00:00");
        Assert.Equal(2, imports.Preview(next).NewRowCount);
        imports.Commit(first);
        var committed = imports.Commit(next);
        Assert.Equal(1, committed.NewRowCount);
        Assert.Equal(1, committed.SkippedRowCount);
        Assert.Equal("20", committed.NewRawBytes);
        Assert.Equal(2, imports.List().Count);
    }
    [Fact] public void OverlappingDifferentFilesAreRejectedWithoutPartialImport()
    {
        imports.Commit(Request("Synthetic-A,1,1,1,1 B,2026-01-01 10:00:00"));
        var conflict = Request("Synthetic-A,1,2,2,2 B,2026-01-01 10:00:00\nSynthetic-B,1,3,3,display,2026-01-01 11:00:00");
        Assert.NotEmpty(imports.Preview(conflict).Warnings);
        Assert.Throws<ArgumentException>(() => imports.Commit(conflict)); Assert.Single(imports.List());
    }
    [Fact] public void ChangedBillingRowWithReducedOldOccurrenceCountIsAConflict()
    {
        const string old = "Synthetic-A,1,10,10,display,2026-01-01 10:00:00\n";
        const string changed = "Synthetic-A,0.5,20,10,display,2026-01-01 10:00:00\n";
        imports.Commit(Request(old + old));
        var replacement = Request(old + changed + "Synthetic-B,1,30,30,display,2026-01-01 11:00:00");
        Assert.NotEmpty(imports.Preview(replacement).Warnings);
        Assert.Throws<ArgumentException>(() => imports.Commit(replacement));
        Assert.Single(imports.List());
        var query = new QuerySpec(new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), "UTC", "hour");
        Assert.Equal(2, imports.Query(replacement.Source, query).RecordCount);
        Assert.Equal("20", imports.Query(replacement.Source, query).RawBytes);

        // All old occurrences retained: the different billing row is an additional event.
        var retained = Request(old + old + changed);
        Assert.Empty(imports.Preview(retained).Warnings);
        Assert.Equal(1, imports.Commit(retained).NewRowCount);
        var result = imports.Query(retained.Source, query);
        Assert.Equal(3, result.RecordCount);
        Assert.Equal("40", result.RawBytes);
        Assert.Equal("30", result.BilledBytes);
    }
    [Fact] public void BadRowsNeverPartiallyCommit()
    {
        Assert.Throws<ArgumentException>(() => imports.Commit(Request("Synthetic-A,1,1,1,1 B,2026-01-01 10:00:00\nSynthetic-B,1,NaN,2,2 B,2026-01-01 10:01:00")));
        Assert.Empty(imports.List());
    }
    [Fact] public void LargeByteValuesRetainIntegerPrecisionAndQuotedNames()
    {
        var result = imports.Commit(Request("\"Synthetic, A\",1,9007199254740993,9007199254740993,display only,2026-01-01 10:00:00"));
        Assert.Equal("9007199254740993", result.RawBytes);
    }
    [Fact] public void AmbiguousDstIsNotGuessed()
    {
        var request = Request("Synthetic-A,1,1,1,1 B,2026-11-01 01:30:00") with { TimeZoneId = "America/New_York" };
        Assert.Throws<ArgumentException>(() => imports.Preview(request));
    }
    [Fact] public void MaliciousNamesRemainPlainData()
    {
        var preview = imports.Commit(Request("=HYPERLINK(1),1,1,1,<script>,2026-01-01 10:00:00"));
        Assert.Equal(1, preview.RowCount);
    }

    [Fact]
    public void SyntheticThreeDayExportPreviewsAndCommitsAll19870RowsExactlyOnce()
    {
        const int rowCount = 19_870;
        // Entirely synthetic: quoted Unicode names, varied rates, and nearly three days of records.
        string[] values = ["0,2097152,0", "1.5,3145728,4718592", "2,4194304,8388608"];
        var rows = new StringBuilder();
        var start = new DateTime(2026, 1, 1);
        for (var i = 0; i < rowCount; i++)
            rows.Append("\"Synthetic, 节点-").Append(i % 5).Append("\",").Append(values[i % 3])
                .Append(",display only,").Append(start.AddSeconds(i * 13).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n');
        var request = Request(rows.ToString());
        Assert.True(Encoding.UTF8.GetByteCount(request.Csv) < ProviderImports.MaxCsvBytes);

        var preview = imports.Preview(request);
        Assert.Equal(rowCount, preview.RowCount);
        Assert.Equal("62504566784", preview.RawBytes);
        Assert.Equal("86808985600", preview.BilledBytes);
        Assert.Equal("2026-01-01T00:00:00.0000000Z", preview.FirstTime);
        Assert.Equal("2026-01-03T23:44:57.0000000Z", preview.LastTime);
        Assert.False(preview.Duplicate);
        Assert.Empty(preview.Warnings);
        Assert.Empty(imports.List());
        AssertStored(0, 0, 0);

        var committed = imports.Commit(request);
        Assert.Equal(preview.Hash, committed.Hash);
        Assert.Equal(preview.RowCount, committed.RowCount);
        Assert.Equal(preview.RawBytes, committed.RawBytes);
        Assert.Equal(preview.BilledBytes, committed.BilledBytes);
        Assert.False(committed.Duplicate);
        AssertStored(rowCount, 62_504_566_784, 86_808_985_600);

        Assert.True(imports.Preview(request).Duplicate);
        Assert.True(imports.Commit(request).Duplicate);
        var batch = Assert.Single(imports.List());
        Assert.Equal(preview.Hash, batch.Hash);
        Assert.Equal(rowCount, batch.RowCount);
        Assert.Equal(preview.RawBytes, batch.RawBytes);
        Assert.Equal(preview.BilledBytes, batch.BilledBytes);
        AssertStored(rowCount, 62_504_566_784, 86_808_985_600);
    }

    [Theory]
    [InlineData(10_000)]
    [InlineData(10_001)]
    [InlineData(50_000)]
    public void FormerLimitAndNewLimitAreAccepted(int rowCount)
    {
        Assert.Equal(50_000, ProviderImports.MaxRows);
        var request = RepeatedRows(rowCount);
        Assert.True(Encoding.UTF8.GetByteCount(request.Csv) < ProviderImports.MaxCsvBytes);
        Assert.Equal(rowCount, imports.Preview(request).RowCount);
        Assert.Equal(rowCount, imports.Commit(request).RowCount);
        Assert.Equal(rowCount, Assert.Single(imports.List()).RowCount);
        AssertStored(rowCount, rowCount, rowCount);
    }

    [Fact]
    public void OneRowAboveLimitIsRejectedWithoutSavingRowsOrBatch()
    {
        var request = RepeatedRows(50_001);
        Assert.True(Encoding.UTF8.GetByteCount(request.Csv) < ProviderImports.MaxCsvBytes);
        Assert.Throws<ArgumentException>(() => imports.Preview(request));
        Assert.Throws<ArgumentException>(() => imports.Commit(request));
        Assert.Empty(imports.List());
        AssertStored(0, 0, 0);
    }

    [Theory]
    [InlineData("Synthetic-invalid,1,NaN,1,display,2026-01-01 00:00:00", typeof(ArgumentException))]
    [InlineData("Synthetic-overflow,1,9223372036854775807,1,display,2026-01-01 00:00:00", typeof(OverflowException))]
    [InlineData("Synthetic-overflow,1,1,79228162514264337593543950335,display,2026-01-01 00:00:00", typeof(OverflowException))]
    public void InvalidLastRowInLargeFileNeverSavesPartialRowsOrBatch(string lastRow, Type exceptionType)
    {
        var valid = RepeatedRows(19_869);
        var request = valid with { Csv = valid.Csv + lastRow };
        Assert.Throws(exceptionType, () => imports.Preview(request));
        Assert.Throws(exceptionType, () => imports.Commit(request));
        Assert.Empty(imports.List());
        AssertStored(0, 0, 0);
    }

    [Fact]
    public void FourMegabyteLimitCountsUtf8BytesAndAcceptsExactBoundary()
    {
        Assert.Equal(4_000_000, ProviderImports.MaxCsvBytes);
        const int rowCount = 10_000;
        const string prefix = "Synthetic-节点,1,1,1,";
        const string suffix = ",2026-01-01 00:00:00\n";
        var padding = ProviderImports.MaxCsvBytes - Encoding.UTF8.GetByteCount(Header)
            - rowCount * Encoding.UTF8.GetByteCount(prefix + suffix);
        var rows = new StringBuilder();
        for (var i = 0; i < rowCount; i++)
        {
            var displayLength = padding / rowCount + (i < padding % rowCount ? 1 : 0);
            Assert.InRange(displayLength, 0, 500);
            rows.Append(prefix).Append('x', displayLength).Append(suffix);
        }
        var request = Request(rows.ToString());
        Assert.Equal(ProviderImports.MaxCsvBytes, Encoding.UTF8.GetByteCount(request.Csv));
        Assert.True(request.Csv.Length < ProviderImports.MaxCsvBytes);
        Assert.Equal(rowCount, imports.Preview(request).RowCount);
        Assert.Equal(rowCount, imports.Commit(request).RowCount);

        // A valid extra ASCII character in the first name exceeds the byte cap, but not the character cap.
        var oversized = request with { Csv = request.Csv.Insert(Header.Length, "x") };
        Assert.Equal(ProviderImports.MaxCsvBytes + 1, Encoding.UTF8.GetByteCount(oversized.Csv));
        Assert.True(oversized.Csv.Length < ProviderImports.MaxCsvBytes);
        Assert.Contains("4 MB", Assert.Throws<ArgumentException>(() => imports.Preview(oversized)).Message);
        Assert.Contains("4 MB", Assert.Throws<ArgumentException>(() => imports.Commit(oversized)).Message);
        Assert.Single(imports.List());
        AssertStored(rowCount, rowCount, rowCount);
    }

    [Fact]
    public void BatchReferencesRevokeOwnerRetainsOverlappingSnapshotAndSupportsReimport()
    {
        Assert.True(imports.TrackingAvailable);
        var a = imports.Commit(Request(DistinctRows(0, 100)));
        var b = imports.Commit(Request(DistinctRows(20, 100)));
        Assert.Equal(20, b.NewRowCount);
        Assert.Equal(120, imports.Query("synthetic-provider", AllProvider()).RecordCount);
        var preview = imports.PreviewRevoke("synthetic-provider", a.Hash);
        Assert.True(preview.CanRevoke);
        Assert.Equal(100, preview.RowCount);
        Assert.Equal(20, preview.DeletedRowCount);
        Assert.Equal(80, preview.RetainedRowCount);
        Assert.Equal("20", preview.DeletedRawBytes);
        Assert.Equal("2", preview.DeletedBilledBytes);
        Assert.Equal("80", preview.RetainedRawBytes);
        Assert.Equal("8", preview.RetainedBilledBytes);
        Assert.Equal(preview, imports.Revoke(preview.Source, preview.Hash, preview.Token));
        var remaining = imports.Query(preview.Source, AllProvider());
        Assert.Equal(100, remaining.RecordCount);
        Assert.Equal("100", remaining.RawBytes);
        Assert.Equal("10", remaining.BilledBytes);
        Assert.False(imports.PreviewRevoke(preview.Source, a.Hash).CanRevoke);

        // Retained A-owned rows still exist. New A-owned records must use unoccupied IDs.
        Assert.Equal(20, imports.Commit(Request(DistinctRows(0, 100))).NewRowCount);
        Assert.Equal(120, imports.Query(preview.Source, AllProvider()).RecordCount);
        Revoke(b.Hash);
        Assert.Equal(100, imports.Query(preview.Source, AllProvider()).RecordCount);
        Revoke(a.Hash);
        Assert.Empty(imports.List());
        Assert.Equal(0, imports.Query(preview.Source, AllProvider()).RecordCount);
        Assert.Equal(100, imports.Commit(Request(DistinctRows(0, 100))).NewRowCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BatchReferencesRespectMultiplicityAndBothRevocationOrders(bool reverse)
    {
        const string x = "Synthetic-X,0.8,10,8.000000000123,display,2026-01-01 10:00:00\n";
        var a = imports.Commit(Request(x + x + "Synthetic-Y,1,20,20,display,2026-01-01 11:00:00\n"));
        var b = imports.Commit(Request(x + x + x + "Synthetic-Z,0.5,30,15.000000000123,display,2026-01-01 12:00:00\n"));
        Assert.Equal(5, imports.Query("synthetic-provider", AllProvider()).RecordCount);
        using var reopened = new ProviderImports(Path.Combine(root, "provider.db"));
        var first = reopened.PreviewRevoke("synthetic-provider", reverse ? b.Hash : a.Hash);
        reopened.Revoke(first.Source, first.Hash, first.Token);
        var remaining = reopened.Query(first.Source, AllProvider());
        Assert.Equal(reverse ? 3 : 4, remaining.RecordCount);
        Assert.Equal(reverse ? "40" : "60", remaining.RawBytes);
        Assert.Equal(reverse ? "36.000000000246" : "39.000000000492", remaining.BilledBytes);
        var last = reopened.PreviewRevoke(first.Source, reverse ? a.Hash : b.Hash);
        reopened.Revoke(last.Source, last.Hash, last.Token);
        Assert.Equal("0", reopened.Query(first.Source, AllProvider()).RawBytes);
        Assert.Equal("0", reopened.Query(first.Source, AllProvider()).BilledBytes);
    }

    [Fact]
    public void BatchReferencesTrackFullyOverlappingZeroNewFileAndRetainOnlyItsMultiplicity()
    {
        var a = imports.Commit(Request(SyntheticRow + SyntheticRow));
        var subset = Request(SyntheticRow.Replace(",1 B,", ",another display,"));
        var preview = imports.Preview(subset);
        Assert.False(preview.Duplicate);
        Assert.Equal(0, preview.NewRowCount);
        var b = imports.Commit(subset);
        Assert.False(b.Duplicate);
        Assert.True(imports.Commit(subset).Duplicate);
        var batch = imports.List().Single(row => row.Hash == b.Hash);
        Assert.Equal(0, batch.RowCount);
        Assert.Equal(1, batch.FileRowCount);
        Assert.Equal("1", batch.FileRawBytes);
        Assert.Equal("1", batch.FileBilledBytes);
        Assert.True(batch.ReferencesComplete);
        var revoke = imports.PreviewRevoke(subset.Source, a.Hash);
        Assert.Equal(1, revoke.DeletedRowCount);
        Assert.Equal(1, revoke.RetainedRowCount);
        imports.Revoke(revoke.Source, revoke.Hash, revoke.Token);
        Assert.Equal(1, imports.Query(subset.Source, AllProvider()).RecordCount);
        Revoke(b.Hash);
        Assert.Equal(0, imports.Query(subset.Source, AllProvider()).RecordCount);
    }

    [Fact]
    public void BatchRevokeRevalidatesPreviewAndDoesNotAffectOtherSources()
    {
        var a = imports.Commit(Request(DistinctRows(0, 10)));
        var pending = imports.PreviewRevoke("synthetic-provider", a.Hash);
        imports.Commit(Request(DistinctRows(5, 10)));
        Assert.Throws<InvalidOperationException>(() => imports.Revoke(pending.Source, pending.Hash, pending.Token));
        Assert.Equal(15, imports.Query(pending.Source, AllProvider()).RecordCount);
        var current = imports.PreviewRevoke(pending.Source, pending.Hash);
        imports.Commit(Request(DistinctRows(0, 10)) with { Source = "synthetic-other" });
        imports.Revoke(current.Source, current.Hash, current.Token);
        Assert.Equal(10, imports.Query(current.Source, AllProvider()).RecordCount);
        Assert.Equal(10, imports.Query("synthetic-other", AllProvider()).RecordCount);
    }

    [Fact]
    public void BatchRevokeTokenCannotAuthorizeADifferentBatchInTheSameSource()
    {
        var a = imports.Commit(Request(DistinctRows(0, 5)));
        var b = imports.Commit(Request(DistinctRows(10, 5)));
        var preview = imports.PreviewRevoke("synthetic-provider", a.Hash);
        Assert.Throws<InvalidOperationException>(() => imports.Revoke(preview.Source, b.Hash, preview.Token));
        Assert.Equal(2, imports.List().Count);
        Assert.Equal(10, imports.Query(preview.Source, AllProvider()).RecordCount);
    }

    [Fact]
    public void BatchRevokeSqliteFailureRollsBackReferencesRecordsAndBatch()
    {
        var a = imports.Commit(Request(DistinctRows(0, 5)));
        var preview = imports.PreviewRevoke("synthetic-provider", a.Hash);
        using var db = new SqliteConnection($"Data Source={Path.Combine(root, "provider.db")};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "CREATE TRIGGER synthetic_revoke_failure BEFORE DELETE ON import_batches BEGIN SELECT RAISE(ABORT,'synthetic'); END;";
        command.ExecuteNonQuery();
        Assert.Throws<SqliteException>(() => imports.Revoke(preview.Source, preview.Hash, preview.Token));
        Assert.Equal(preview, imports.PreviewRevoke(preview.Source, preview.Hash));
        Assert.Equal(5, imports.Query(preview.Source, AllProvider()).RecordCount);
        command.CommandText = "DROP TRIGGER synthetic_revoke_failure;";
        command.ExecuteNonQuery();
        imports.Revoke(preview.Source, preview.Hash, preview.Token);
        Assert.Empty(imports.List());
        Assert.Equal(0, imports.Query(preview.Source, AllProvider()).RecordCount);
    }

    [Fact]
    public void ReferenceInsertFailureRollsBackWholeImportAndAllowsRetry()
    {
        using var db = new SqliteConnection($"Data Source={Path.Combine(root, "provider.db")};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "CREATE TRIGGER synthetic_reference_failure BEFORE INSERT ON provider_batch_rows WHEN NEW.row_number=1 BEGIN SELECT RAISE(ABORT,'synthetic'); END;";
        command.ExecuteNonQuery();
        Assert.Throws<SqliteException>(() => imports.Commit(Request(SyntheticRow + SyntheticRow)));
        Assert.Empty(imports.List());
        command.CommandText = "SELECT COUNT(*) FROM provider_batch_tracking;";
        Assert.Equal(0L, command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM provider_records;";
        Assert.Equal(0L, command.ExecuteScalar());
        command.CommandText = "DROP TRIGGER synthetic_reference_failure;";
        command.ExecuteNonQuery();
        Assert.Equal(2, imports.Commit(Request(SyntheticRow + SyntheticRow)).NewRowCount);
    }

    private void Revoke(string hash)
    {
        var preview = imports.PreviewRevoke("synthetic-provider", hash);
        Assert.True(preview.CanRevoke);
        imports.Revoke(preview.Source, preview.Hash, preview.Token);
    }
    private static string DistinctRows(int start, int count) => string.Concat(Enumerable.Range(start, count)
        .Select(i => $"Synthetic-{i},1,1,0.1,display,2026-01-01 00:00:00\n"));
    private static QuerySpec AllProvider() => new(new(2025, 12, 31, 0, 0, 0, TimeSpan.Zero), new(2026, 1, 3, 0, 0, 0, TimeSpan.Zero), "UTC");
    private static ImportRequest RepeatedRows(int count) => Request(string.Concat(Enumerable.Repeat(SyntheticRow, count)));

    private void AssertStored(long count, long raw, long billed)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "provider.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*), COALESCE(SUM(raw_bytes),0) FROM provider_records";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(count, reader.GetInt64(0));
        Assert.Equal(raw, reader.GetInt64(1));
        reader.Close();
        // SQLite SUM would coerce decimal text to REAL; do not use it for provider accounting.
        command.CommandText = "SELECT billed_bytes,typeof(billed_bytes) FROM provider_records";
        using var values = command.ExecuteReader();
        var total = BigInteger.Zero;
        while (values.Read()) { Assert.Equal("text", values.GetString(1)); total += BigInteger.Parse(values.GetString(0), CultureInfo.InvariantCulture); }
        Assert.Equal(new BigInteger(billed), total);
    }
    public void Dispose() { imports.Dispose(); Directory.Delete(root, true); }
}
