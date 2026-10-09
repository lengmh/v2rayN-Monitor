using V2rayNMonitor.Core;
using Xunit;
namespace V2rayNMonitor.Tests;
public sealed class ProviderImportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "monitor-tests-" + Guid.NewGuid());
    private readonly ProviderImports imports;
    public ProviderImportTests() { Directory.CreateDirectory(root); imports = new(Path.Combine(root, "provider.db")); }
    private const string Header = "node_name,rate,origin_traffic,origin_x_rate,traffic,log_time\n";
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
    public void Dispose() { imports.Dispose(); Directory.Delete(root, true); }
}
