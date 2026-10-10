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
        client = new(new HttpClientHandler { UseCookies = true }) { BaseAddress = new(server.Address) };
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
    public async Task DisposeAsync() { client.Dispose(); await server.DisposeAsync(); Directory.Delete(root, true); }
}
