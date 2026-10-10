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
    [Fact] public void OverlappingDifferentFilesAreRejectedWithoutPartialImport()
    {
        imports.Commit(Request("Synthetic-A,1,1,1,1 B,2026-01-01 10:00:00"));
        var conflict = Request("Synthetic-A,1,2,2,2 B,2026-01-01 10:00:00");
        Assert.NotEmpty(imports.Preview(conflict).Warnings);
        Assert.Throws<ArgumentException>(() => imports.Commit(conflict)); Assert.Single(imports.List());
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
