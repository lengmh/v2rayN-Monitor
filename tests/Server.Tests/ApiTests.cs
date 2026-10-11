using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using V2rayNMonitor.Server;
using Xunit;
namespace V2rayNMonitor.Tests;
public sealed class ApiTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "monitor-api-" + Guid.NewGuid());
    private MonitorServer server = null!;
    private HttpClient client = null!;
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(root, "web"));
        File.WriteAllText(Path.Combine(root, "web", "index.html"), "<!doctype html><title>Synthetic test</title>");
        server = await MonitorServer.StartAsync(root, Path.Combine(root, "web"));
        client = new(new HttpClientHandler { UseCookies = true, UseProxy = false }) { BaseAddress = new(server.Address) };
    }
    private async Task Login()
    {
        client.DefaultRequestHeaders.Add("X-Monitor-Request", "1");
        var token = new Uri(server.IssueLaunchUrl()).Fragment[1..];
        var response = await client.PostAsJsonAsync("/api/session", new { token });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
    [Fact] public async Task ReadAndWriteApisRequireSession()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/settings", new { })).StatusCode);
        await Login(); Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/status")).StatusCode);
    }
    [Fact] public async Task BootstrapTokenIsOneUseAndCookieHttpOnlyStrict()
    {
        client.DefaultRequestHeaders.Add("X-Monitor-Request", "1");
        var token = new Uri(server.IssueLaunchUrl()).Fragment[1..];
        var result = await client.PostAsJsonAsync("/api/session", new { token });
        var cookie = Assert.Single(result.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", cookie.ToLowerInvariant()); Assert.Contains("samesite=strict", cookie.ToLowerInvariant());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/session", new { token })).StatusCode);
    }
    [Fact] public async Task HostAndOriginAreValidatedEvenWithSession()
    {
        await Login(); client.DefaultRequestHeaders.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/status")).StatusCode);
        client.DefaultRequestHeaders.Remove("Origin"); client.DefaultRequestHeaders.Host = "evil.example";
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/status")).StatusCode);
    }
    [Fact] public async Task RealLedgerRejectsDemoAndInvalidQueries()
    {
        await Login();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/demo", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/history?start=bad&end=bad")).StatusCode);
        Assert.Empty(server.Ledger.GetNodes());
    }
    [Fact] public async Task QueryAndExportPreserveBytePrecisionAndEscapeFormulaNames()
    {
        await Login(); var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        server.Ledger.Accept(new("test", "one", " =Synthetic", "process1", "config1", at, 0, 0));
        server.Ledger.Accept(new("test", "one", " =Synthetic", "process1", "config1", at.AddSeconds(1), 9007199254740993, 7));
        const string query = "?start=2026-01-01T00:00:00Z&end=2026-01-02T00:00:00Z&timeZoneId=UTC";
        var json = await client.GetFromJsonAsync<JsonElement>("/api/history" + query);
        Assert.Equal("9007199254740993", json.GetProperty("upload").GetString());
        var csv = await client.GetStringAsync("/api/export" + query);
        Assert.Contains("9007199254740993", csv); Assert.Contains("' =Synthetic", csv);
    }
    [Fact] public async Task RecoveredQueriesAndExportsDiscloseContainedVersusPartialIntervals()
    {
        await Login();
        var at = new DateTimeOffset(2026, 1, 1, 10, 15, 0, TimeSpan.Zero);
        server.Ledger.Accept(new("test", "one", "Synthetic", "process1", "config1", at, 0, 0));
        server.Ledger.Accept(new("test", "one", "Synthetic", "process1", "config1", at.AddMinutes(30), 1200, 2800));
        const string full = "?start=2026-01-01T10:00:00Z&end=2026-01-01T11:00:00Z&timeZoneId=UTC&bucket=hour";
        var json = await client.GetFromJsonAsync<JsonElement>("/api/history" + full);
        Assert.Equal("1200", json.GetProperty("upload").GetString());
        Assert.Equal("2800", json.GetProperty("recoveredDownload").GetString());
        Assert.Equal("1200", json.GetProperty("nodes")[0].GetProperty("recoveredUpload").GetString());
        Assert.Equal(1, json.GetProperty("series").GetArrayLength());
        Assert.False(json.GetProperty("gaps")[0].GetProperty("isOpen").GetBoolean());
        Assert.Contains("recovered-contained", await client.GetStringAsync("/api/export" + full));
        const string partial = "?start=2026-01-01T10:20:00Z&end=2026-01-01T10:30:00Z&timeZoneId=UTC";
        json = await client.GetFromJsonAsync<JsonElement>("/api/history" + partial);
        Assert.Equal("0", json.GetProperty("upload").GetString());
        Assert.Equal("1200", json.GetProperty("partialUpload").GetString());
        Assert.Contains("recovered-partial", await client.GetStringAsync("/api/export" + partial));
    }

    [Fact] public async Task RepresentativeProviderCsvPassesPreviewAndCommitThroughHttp()
    {
        await Login();
        const int rowCount = 19_870;
        const string row = "Synthetic-A,1,1024,1024,1 KiB,2026-01-01 10:00:00\n";
        var csv = "node_name,rate,origin_traffic,origin_x_rate,traffic,log_time\n" + string.Concat(Enumerable.Repeat(row, rowCount));
        var request = new { source = "synthetic-http", timeZoneId = "UTC", csv };
        var preview = await client.PostAsJsonAsync("/api/imports/preview", request);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var body = await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(rowCount, body.GetProperty("rowCount").GetInt32());
        var commit = await client.PostAsJsonAsync("/api/imports/commit", request);
        Assert.Equal(HttpStatusCode.OK, commit.StatusCode);
        var batches = await client.GetFromJsonAsync<JsonElement>("/api/imports");
        Assert.Equal(1, batches.GetArrayLength());
        Assert.Equal(rowCount, batches[0].GetProperty("rowCount").GetInt32());
        Assert.Equal((rowCount * 1024L).ToString(), batches[0].GetProperty("rawBytes").GetString());
        Assert.Empty(server.Ledger.GetNodes());
    }
    [Theory]
    [InlineData("=x")][InlineData(" +x")][InlineData("-x")][InlineData("@x")][InlineData("\tx")]
    public void SpreadsheetFormulaPrefixesEscaped(string value) => Assert.StartsWith("\"'", MonitorServer.Cell(value));

    [Fact] public async Task DecimalProviderCsvRoundTripsPreviewCommitListAndDuplicateWithoutChangingLocalLedger()
    {
        await Login();
        const string csv = """
            node_name,rate,origin_traffic,origin_x_rate,traffic,log_time
            Synthetic-A,1,1000,1000,synthetic,2026-01-01 00:00:00
            Synthetic-A,0.8,1000,800,synthetic,2026-01-01 00:01:00
            Synthetic-A,0.8,1001,800.800000000123,synthetic,2026-01-01 00:02:00
            """;
        var request = new { source = "synthetic-decimal-http", timeZoneId = "UTC", csv };
        foreach (var route in new[] { "/api/imports/preview", "/api/imports/commit" })
        {
            var response = await client.PostAsJsonAsync(route, request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("2600.800000000123", body.GetProperty("billedBytes").GetString());
            Assert.Equal("3001", body.GetProperty("rawBytes").GetString());
            Assert.False(body.GetProperty("duplicate").GetBoolean());
        }
        var duplicate = await client.PostAsJsonAsync("/api/imports/commit", request);
        Assert.True((await duplicate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("duplicate").GetBoolean());
        var batches = await client.GetFromJsonAsync<JsonElement>("/api/imports");
        Assert.Equal(1, batches.GetArrayLength());
        Assert.Equal("2600.800000000123", batches[0].GetProperty("billedBytes").GetString());
        Assert.Empty(server.Ledger.GetNodes());
        var history = await client.GetFromJsonAsync<JsonElement>("/api/history?start=2026-01-01T00:00:00Z&end=2026-01-02T00:00:00Z&timeZoneId=UTC");
        Assert.Equal("0", history.GetProperty("upload").GetString());
        Assert.Equal("0", history.GetProperty("download").GetString());
    }

    [Theory]
    [InlineData("0.00000000000000000000000000001")]
    [InlineData("79228162514264337593543950336")]
    [InlineData("-1")]
    public async Task InvalidDecimalProviderCsvReturnsBadRequestAndNoBatch(string billed)
    {
        await Login();
        var csv = "node_name,rate,origin_traffic,origin_x_rate,traffic,log_time\nSynthetic-A,1,1,0.1,synthetic,2026-01-01 00:00:00\n"
            + $"Synthetic-B,1,1,{billed},synthetic,2026-01-01 00:01:00";
        var request = new { source = "synthetic-invalid-http", timeZoneId = "UTC", csv };
        foreach (var route in new[] { "/api/imports/preview", "/api/imports/commit" })
        {
            var response = await client.PostAsJsonAsync(route, request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("无损", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        }
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/api/imports")).GetArrayLength());
        Assert.Empty(server.Ledger.GetNodes());
    }
    public async Task DisposeAsync() { client.Dispose(); await server.DisposeAsync(); Directory.Delete(root, true); }
}
