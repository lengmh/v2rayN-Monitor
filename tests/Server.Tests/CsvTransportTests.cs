using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using V2rayNMonitor.Server;
using Xunit;

namespace V2rayNMonitor.Tests;

public sealed class CsvTransportTests : IAsyncLifetime
{
    private const string Header = "node_name,rate,origin_traffic,origin_x_rate,traffic,log_time\r\n";
    private const string RowPrefix = "\"Synthetic-012345678901234567\",\"1\",\"1\",\"1\",\"1 B";
    private const string RowSuffix = "\",\"2026-01-01 00:00:00\"\r\n";
    private readonly string root = Path.Combine(Path.GetTempPath(), "monitor-csv-http-" + Guid.NewGuid());
    private MonitorServer server = null!;
    private HttpClient client = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(root, "web"));
        server = await MonitorServer.StartAsync(root, Path.Combine(root, "web"));
        client = new(new HttpClientHandler { UseCookies = true, UseProxy = false }) { BaseAddress = new(server.Address) };
        client.DefaultRequestHeaders.Add("X-Monitor-Request", "1");
        var token = new Uri(server.IssueLaunchUrl()).Fragment[1..];
        using var response = await client.PostAsJsonAsync("/api/session", new { token });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JsonEscapingAllowsCsvAtBothDecodedLimitsThroughPreviewAndCommit(bool escapeEveryCharacter)
    {
        var csv = CsvAtByteLimit();
        Assert.Equal(4_000_000, Encoding.UTF8.GetByteCount(csv));
        var json = escapeEveryCharacter ? UnicodeEscapedJson(csv) : BrowserJson(csv);
        Assert.True(Encoding.UTF8.GetByteCount(json) > 4_100_000);
        if (escapeEveryCharacter) Assert.True(Encoding.UTF8.GetByteCount(json) > 24_000_000);

        foreach (var route in new[] { "/api/imports/preview", "/api/imports/commit" })
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(route, content);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(50_000, body.GetProperty("rowCount").GetInt32());
            Assert.Equal("50000", body.GetProperty("rawBytes").GetString());
            Assert.Equal("50000", body.GetProperty("billedBytes").GetString());
        }
        var batches = await client.GetFromJsonAsync<JsonElement>("/api/imports");
        Assert.Equal(1, batches.GetArrayLength());
        Assert.Equal(50_000, batches[0].GetProperty("rowCount").GetInt32());
        Assert.Empty(server.Ledger.GetNodes());
    }

    [Theory]
    [InlineData("bytes", "4 MB")]
    [InlineData("rows", "50,000")]
    public async Task DecodedCsvLimitsStillRejectPreviewAndCommitWithoutSaving(string limit, string error)
    {
        var csv = limit == "bytes" ? CsvAtByteLimit() + " "
            : Header + string.Concat(Enumerable.Repeat(RowPrefix + RowSuffix, 50_001));
        foreach (var route in new[] { "/api/imports/preview", "/api/imports/commit" })
        {
            using var content = new StringContent(BrowserJson(csv), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(route, content);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Contains(error, body.GetProperty("error").GetString());
        }
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/api/imports")).GetArrayLength());
        Assert.Empty(server.Ledger.GetNodes());
    }

    [Fact]
    public async Task MalformedJsonStillReturnsBadRequestWithoutSaving()
    {
        foreach (var route in new[] { "/api/imports/preview", "/api/imports/commit" })
        {
            using var content = new StringContent("{\"csv\":", Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(route, content);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/api/imports")).GetArrayLength());
    }

    [Fact]
    public async Task JsonTransportRemainsBounded()
    {
        // Valid JSON with excess whitespace must be refused before deserialization/import.
        using var content = new StringContent("{}" + new string(' ', 24_100_000 - 1), Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/imports/commit") { Content = content };
        // Let Kestrel reject Content-Length before a large upload races its connection close.
        request.Headers.ExpectContinue = true;
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/api/imports")).GetArrayLength());
    }

    private static string CsvAtByteLimit()
    {
        // Synthetic quoted rows, padded in the display-only field to exactly 4 MB.
        const int rowCount = 50_000;
        var padding = 4_000_000 - Encoding.UTF8.GetByteCount(Header)
            - rowCount * Encoding.UTF8.GetByteCount(RowPrefix + RowSuffix);
        var csv = new StringBuilder(Header);
        for (var i = 0; i < rowCount; i++)
            csv.Append(RowPrefix).Append('x', padding / rowCount + (i < padding % rowCount ? 1 : 0)).Append(RowSuffix);
        return csv.ToString();
    }

    private static string BrowserJson(string csv) => JsonSerializer.Serialize(
        new { source = "synthetic-transport", timeZoneId = "UTC", csv },
        new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static string UnicodeEscapedJson(string csv)
    {
        // Escaping every ASCII character as \uXXXX is valid JSON and gives the worst expansion.
        var json = new StringBuilder("{\"source\":\"synthetic-transport\",\"timeZoneId\":\"UTC\",\"csv\":\"");
        foreach (var character in csv) json.Append("\\u").Append(((int)character).ToString("x4"));
        return json.Append("\"}").ToString();
    }

    public async Task DisposeAsync()
    {
        client.Dispose();
        await server.DisposeAsync();
        Directory.Delete(root, true);
    }
}
