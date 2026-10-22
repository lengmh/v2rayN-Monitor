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
        using (new V2rayNMonitor.Core.Ledger(Path.Combine(root, "history.db"))) { } // Existing unextended v1 fixture.
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
    private async Task StartCapabilityServer(Func<string, CancellationToken, Task<V2rayNMonitor.Core.CoreCapabilityInspection>> inspect,
        int version = 1, bool demo = false)
    {
        await server.DisposeAsync(); client.Dispose();
        if (version > 1) V2rayNMonitor.Core.ScopeLedgerMigration.Migrate(Path.Combine(root, "history.db"), Path.Combine(root, "capability-v1.db"));
        if (version == 3) V2rayNMonitor.Core.ScopeLedgerMigration.MigrateProbes(Path.Combine(root, "history.db"), Path.Combine(root, "capability-v2.db"));
        server = await MonitorServer.StartAsync(root, Path.Combine(root, "web"), demo: demo,
            useScopeLedger: version > 1, useProbeLedger: version == 3, inspectCoreCapabilities: inspect);
        server.UpdateSettings(server.Settings with { V2rayNPath = root });
        client = new(new HttpClientHandler { UseCookies = true, UseProxy = false }) { BaseAddress = new(server.Address) };
    }
    private static V2rayNMonitor.Core.CoreCapabilityInspection SyntheticCapability() => new("identified",
        [new("sing-box-statistics", "Synthetic component", "Synthetic version", "identity_matched", "asset_unavailable", "Synthetic license")]);

    [Fact]
    public async Task CapabilityEndpointUsesExistingAuthenticationAndAcceptsNoPathsOrWrites()
    {
        var calls = 0;
        await StartCapabilityServer((path, token) =>
        {
            calls++; Assert.Equal(root, path); Assert.True(token.CanBeCanceled);
            return Task.FromResult(SyntheticCapability());
        });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/core-capabilities")).StatusCode);
        await Login();
        using var foreign = new HttpRequestMessage(HttpMethod.Get, "/api/core-capabilities");
        foreign.Headers.Add("Origin", "https://synthetic.invalid");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(foreign)).StatusCode);
        using var host = new HttpRequestMessage(HttpMethod.Get, "/api/core-capabilities");
        host.Headers.Host = "synthetic.invalid";
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(host)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/core-capabilities?path=untrusted")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsJsonAsync("/api/core-capabilities", new { path = "untrusted" })).StatusCode);
        Assert.Equal(0, calls);
        byte[] ReadHistory()
        {
            using var input = new FileStream(Path.Combine(root, "history.db"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var bytes = new MemoryStream();
            input.CopyTo(bytes);
            return bytes.ToArray();
        }
        var historyBefore = ReadHistory();
        var response = await client.GetAsync("/api/core-capabilities");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(root, text);
        Assert.Equal(1, calls);
        Assert.Equal(historyBefore, ReadHistory());
    }

    [Theory]
    [InlineData(1, "")]
    [InlineData(2, "--scope-ledger")]
    [InlineData(3, "--probe-ledger")]
    public async Task CapabilityEndpointSeparatesLedgerAndCollectorFromFileIdentity(int version, string mode)
    {
        await StartCapabilityServer((_, _) => Task.FromResult(SyntheticCapability()), version);
        await Login();
        var result = await client.GetFromJsonAsync<V2rayNMonitor.Core.CoreCapabilitiesResponse>("/api/core-capabilities");
        Assert.Equal(version, result!.LedgerVersion);
        Assert.Equal(mode, result.StartupMode);
        Assert.Equal("setup", result.CollectorState);
        Assert.Null(result.LastSample);
        Assert.Equal("identity_matched", Assert.Single(result.Components).FileState);
    }

    [Fact]
    public async Task CapabilityEndpointRejectsConcurrentAndStaleReadsThenAllowsRetry()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await StartCapabilityServer(async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return SyntheticCapability();
        });
        await Login();
        var pending = client.GetAsync("/api/core-capabilities");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync("/api/core-capabilities")).StatusCode);
            var settings = server.Settings;
            server.UpdateSettings(settings with { V2rayNPath = "" });
            server.UpdateSettings(settings); // An ABA change must still invalidate the in-flight read.
        }
        finally { release.TrySetResult(); }
        Assert.Equal(HttpStatusCode.Conflict, (await pending).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/core-capabilities")).StatusCode);
    }

    [Fact]
    public async Task CapabilityEndpointReleasesItsGateAfterFailureWithoutLeakingPaths()
    {
        var calls = 0;
        await StartCapabilityServer((_, _) =>
        {
            if (++calls == 1) throw new IOException("Private path: " + root);
            return Task.FromResult(SyntheticCapability());
        });
        await Login();
        var failed = await client.GetAsync("/api/core-capabilities");
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.DoesNotContain(root, await failed.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/core-capabilities")).StatusCode);
    }

    [Fact]
    public async Task CapabilityEndpointDoesNotInspectRealTargetsInDemoMode()
    {
        await StartCapabilityServer((_, _) => throw new InvalidOperationException("Must not inspect"), demo: true);
        await Login();
        var result = await client.GetFromJsonAsync<V2rayNMonitor.Core.CoreCapabilitiesResponse>("/api/core-capabilities");
        Assert.Equal("demo", result!.TargetState);
        Assert.Empty(result.Components);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledHistoryReadDoesNotPreventTheNextRequestOrCollection(bool scopes)
    {
        if (scopes)
        {
            await server.DisposeAsync(); client.Dispose();
            V2rayNMonitor.Core.ScopeLedgerMigration.Migrate(Path.Combine(root, "history.db"), Path.Combine(root, "synthetic-before-cancellation.db"));
            server = await MonitorServer.StartAsync(root, Path.Combine(root, "web"), useScopeLedger: true);
            client = new(new HttpClientHandler { UseCookies = true, UseProxy = false }) { BaseAddress = new(server.Address) };
        }
        await Login();
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var method = typeof(MonitorServer).GetMethod("QueryHistory",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var query = new V2rayNMonitor.Core.QuerySpec(at, at.AddDays(1), "UTC");
        var error = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
            method.Invoke(server, [query, scopes ? "local_proxy" : "legacy_proxy", cancellation.Token]));
        Assert.IsType<OperationCanceledException>(error.InnerException);
        if (scopes)
        {
            var member = new V2rayNMonitor.Core.MeasurementScope("actual_member", "index_id", "synthetic", "Synthetic", "synthetic", "proxy");
            server.Scopes!.Accept(new("synthetic", "core", "config", at, [new(member, 0, 0)], []));
            server.Scopes.Accept(new("synthetic", "core", "config", at.AddSeconds(1), [new(member, 7, 0)], []));
        }
        else
        {
            server.Ledger.Accept(new("synthetic", "node", "Synthetic", "core", "config", at, 0, 0));
            server.Ledger.Accept(new("synthetic", "node", "Synthetic", "core", "config", at.AddSeconds(1), 7, 0));
        }
        var history = await client.GetFromJsonAsync<JsonElement>("/api/history?start=2026-01-01T00:00:00Z&end=2026-01-02T00:00:00Z&timeZoneId=UTC");
        Assert.Equal("7", history.GetProperty("upload").GetString());
    }

    [Fact]
    public async Task DisplaySnapshotsKeepHistoricalNameFiltersAndExactEstimatesInExport()
    {
        await server.DisposeAsync(); client.Dispose();
        V2rayNMonitor.Core.ScopeLedgerMigration.InitializeDisplay(Path.Combine(root, "history.db"), Path.Combine(root, "display-backup.db"));
        server = await MonitorServer.StartAsync(root, Path.Combine(root, "web"));
        client = new(new HttpClientHandler { UseCookies = true, UseProxy = false }) { BaseAddress = new(server.Address) };
        await Login();
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 4; i++)
            server.Ledger.Accept(new("synthetic", "node", i < 2 ? "Original 0.5x" : "Renamed 2倍",
                "core", "config", at.AddSeconds(i), i * 10, 0));
        const string query = "?start=2026-01-01T00:00:00Z&end=2026-01-02T00:00:00Z&timeZoneId=UTC";
        var history = await client.GetFromJsonAsync<JsonElement>("/api/history" + query);
        Assert.True(history.GetProperty("displayMetadataEnabled").GetBoolean());
        Assert.Equal("25", history.GetProperty("display").GetProperty("estimatedBilledBytes").GetString());
        Assert.Equal("25", history.GetProperty("series")[0].GetProperty("display").GetProperty("estimatedBilledBytes").GetString());
        Assert.Equal("10", history.GetProperty("display").GetProperty("estimateUnknownBytes").GetString());
        var old = await client.GetFromJsonAsync<JsonElement>("/api/history" + query + "&nodeName=Original%200.5x");
        Assert.Equal("10", old.GetProperty("upload").GetString());
        Assert.Equal("5", old.GetProperty("display").GetProperty("estimatedBilledBytes").GetString());
        Assert.Equal("5", old.GetProperty("comparisonNodes")[0].GetProperty("display").GetProperty("estimatedBilledBytes").GetString());
        var csv = await client.GetStringAsync("/api/export" + query + "&nodeName=Original%200.5x");
        Assert.Contains("original_name,group_labels,rate,estimated_billed_bytes,estimate_covered_bytes,estimate_unknown_bytes", csv);
        Assert.Contains("\"Original 0.5x\",\"\",\"0.5\",\"5\",\"10\",\"0\"", csv);
        Assert.DoesNotContain("Renamed", csv);
    }

    [Fact]
    public async Task ScopeDisplayFiltersUseVerifiedOpaqueKeysAndCapturedNames()
    {
        await server.DisposeAsync(); client.Dispose();
        var path = Path.Combine(root, "history.db");
        V2rayNMonitor.Core.ScopeLedgerMigration.Migrate(path, Path.Combine(root, "v1-before-scope.db"));
        V2rayNMonitor.Core.ScopeLedgerMigration.InitializeDisplay(path, Path.Combine(root, "display-backup.db"));
        server = await MonitorServer.StartAsync(root, Path.Combine(root, "web"), useScopeLedger: true);
        client = new(new HttpClientHandler { UseCookies = true, UseProxy = false }) { BaseAddress = new(server.Address) };
        await Login();
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var scope = new V2rayNMonitor.Core.MeasurementScope("actual_member", "index_id", "private-synthetic-index",
            "【US-0.5x】 1-Original", "private-synthetic-index", "proxy")
            { CapturedName = new("Original", "US-0.5x") };
        foreach (var second in new[] { 0, 1 })
            server.Scopes!.Accept(new("synthetic", "core", "config", at.AddSeconds(second), [new(scope, second * 11, 0)], []));
        const string query = "?start=2026-01-01T00:00:00Z&end=2026-01-02T00:00:00Z&timeZoneId=UTC";
        var history = await client.GetFromJsonAsync<JsonElement>("/api/history" + query);
        var merge = history.GetProperty("nodes")[0].GetProperty("display").GetProperty("mergeKey").GetString()!;
        Assert.DoesNotContain("private-synthetic-index", history.GetRawText());
        var named = await client.GetFromJsonAsync<JsonElement>("/api/history" + query + "&nodeName=Original");
        Assert.Equal("11", named.GetProperty("upload").GetString());
        Assert.Equal("11", named.GetProperty("display").GetProperty("estimatedBilledBytes").GetString());
        Assert.Equal("1", named.GetProperty("display").GetProperty("rate").GetString());
        Assert.Equal("11", named.GetProperty("comparisonNodes")[0].GetProperty("display").GetProperty("estimatedBilledBytes").GetString());
        Assert.Equal("11", named.GetProperty("series")[0].GetProperty("display").GetProperty("estimatedBilledBytes").GetString());
        var grouped = await client.GetFromJsonAsync<JsonElement>("/api/history" + query + "&nodeKey=" + Uri.EscapeDataString("merge:" + merge));
        Assert.Equal("11", grouped.GetProperty("upload").GetString());
        var nodes = await client.GetFromJsonAsync<JsonElement>("/api/nodes");
        Assert.Equal("Original", nodes[0].GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/history" + query + "&nodeKey=merge:unverified")).StatusCode);
    }
    [Fact] public async Task OptInScopeApiKeepsDimensionsSeparateAndExportCarriesBasis()
    {
        await server.DisposeAsync();
        client.Dispose();
        V2rayNMonitor.Core.ScopeLedgerMigration.Migrate(Path.Combine(root, "history.db"), Path.Combine(root, "synthetic-v1-backup.db"));
        server = await MonitorServer.StartAsync(root, Path.Combine(root, "web"), useScopeLedger: true);
        client = new(new HttpClientHandler { UseCookies = true, UseProxy = false }) { BaseAddress = new(server.Address) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/scope-history")).StatusCode);
        await Login();
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var member = new V2rayNMonitor.Core.MeasurementScope("actual_member", "index_id", "synthetic-id", " =Synthetic member", "synthetic-id", "proxy");
        var business = new V2rayNMonitor.Core.MeasurementScope("business", "aggregate", "business", "Synthetic business");
        var scopes = server.Scopes!;
        scopes.Accept(new("synthetic-instance", "synthetic-core", "synthetic-config", at,
            [new(member, 0, 0), new(business, 0, 0)], []));
        scopes.Accept(new("synthetic-instance", "synthetic-core", "synthetic-config", at.AddSeconds(1),
            [new(member, 9007199254740993, 7), new(business, 9007199254740999, 20)], []));
        const string query = "?start=2026-01-01T00:00:00Z&end=2026-01-02T00:00:00Z&timeZoneId=UTC";
        var history = await client.GetFromJsonAsync<JsonElement>("/api/history" + query);
        Assert.Equal("local_proxy", history.GetProperty("basis").GetString());
        Assert.Equal("9007199254740993", history.GetProperty("upload").GetString());
        var filtered = await client.GetFromJsonAsync<JsonElement>("/api/history" + query + "&nodeName=" + Uri.EscapeDataString(" =Synthetic member"));
        Assert.Equal("9007199254740993", filtered.GetProperty("upload").GetString());
        var aggregate = await client.GetFromJsonAsync<JsonElement>("/api/scope-history" + query + "&basis=business");
        Assert.Equal("business", aggregate.GetProperty("basis").GetString());
        Assert.Equal("9007199254740999", aggregate.GetProperty("upload").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/scope-history" + query)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/scope-history" + query + "&basis=all")).StatusCode);
        var nodes = await client.GetFromJsonAsync<JsonElement>("/api/nodes");
        Assert.StartsWith("scope:", nodes[0].GetProperty("key").GetString());
        Assert.False(nodes[0].TryGetProperty("identityValue", out _)); // No configuration digest/IndexId leaked by the public list.
        var csv = await client.GetStringAsync("/api/export" + query);
        Assert.Contains("quality,basis,scope_key", csv);
        Assert.Contains("\"actual_member\"", csv);
        Assert.Contains("\"scope:", csv);
        Assert.Contains("' =Synthetic member", csv);
        Assert.Contains("9007199254740993", csv);
    }

    [Fact] public async Task LocalHistoryKeepsBothCoreHistoriesAcrossCurrentCoreChanges()
    {
        await server.DisposeAsync();
        client.Dispose();
        V2rayNMonitor.Core.ScopeLedgerMigration.Migrate(Path.Combine(root, "history.db"), Path.Combine(root, "synthetic-v1-backup.db"));
        server = await MonitorServer.StartAsync(root, Path.Combine(root, "web"), useScopeLedger: true);
        client = new(new HttpClientHandler { UseCookies = true, UseProxy = false }) { BaseAddress = new(server.Address) };
        await Login();
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var member = new V2rayNMonitor.Core.MeasurementScope("actual_member", "index_id", "synthetic-member", "Same synthetic name", "synthetic-member", "proxy");
        var legacy = new V2rayNMonitor.Core.MeasurementScope("legacy_proxy", "legacy_node", "synthetic/synthetic-xray",
            "Same synthetic name", "synthetic-xray");
        var aggregate = new V2rayNMonitor.Core.MeasurementScope("business", "aggregate", "business", "Synthetic total");
        server.Scopes!.Accept(new("synthetic", "singbox", "config", at,
            [new(member, 0, 0), new(aggregate, 0, 0)], []));
        server.Scopes.Accept(new("synthetic", "singbox", "config", at.AddSeconds(1),
            [new(member, 9007199254740993, 7), new(aggregate, 9007199254740993, 7)], []));
        var retired = server.Scopes.GetScopes("actual_member").Concat(server.Scopes.GetScopes("business")).Select(x => x.Id).ToArray();
        server.Scopes.Accept(new("synthetic", "xray", "config", at.AddMinutes(1),
            [new(legacy, 100, 0, PartialCoverage: true)], retired));
        server.Scopes.Accept(new("synthetic", "xray", "config", at.AddMinutes(1).AddSeconds(1),
            [new(legacy, 111, 13, PartialCoverage: true)], retired));
        const string query = "?start=2026-01-01T00:00:00Z&end=2026-01-02T00:00:00Z&timeZoneId=UTC";
        foreach (var current in new[] { "legacy_proxy", "actual_member" })
        {
            server.SetStatus(new("collecting", "Synthetic", null, at, CollectionBasis: current));
            var history = await client.GetFromJsonAsync<JsonElement>("/api/history" + query + "&pageSize=1&nonZeroRecords=true");
            Assert.Equal("local_proxy", history.GetProperty("basis").GetString());
            Assert.Equal("9007199254741004", history.GetProperty("upload").GetString());
            Assert.Equal("20", history.GetProperty("download").GetString());
            Assert.Equal(2, history.GetProperty("recordCount").GetInt32());
            Assert.Equal(2, history.GetProperty("nodes").GetArrayLength());
            Assert.True(history.GetProperty("partialCoverage").GetBoolean());
            var nodes = await client.GetFromJsonAsync<JsonElement>("/api/nodes");
            Assert.Equal(2, nodes.GetArrayLength());
            var key = nodes[0].GetProperty("key").GetString();
            var filtered = await client.GetFromJsonAsync<JsonElement>("/api/history" + query + "&nodeKey=" + key);
            Assert.Equal("9007199254740993", filtered.GetProperty("upload").GetString());
            var named = await client.GetFromJsonAsync<JsonElement>("/api/history" + query + "&nodeName=Same%20synthetic%20name");
            Assert.Equal("9007199254741004", named.GetProperty("upload").GetString());
            var second = await client.GetFromJsonAsync<JsonElement>("/api/history" + query + "&pageSize=1&page=1&nonZeroRecords=true");
            Assert.NotEqual(history.GetProperty("records")[0].GetProperty("nodeKey").GetString(),
                second.GetProperty("records")[0].GetProperty("nodeKey").GetString());
            var csv = await client.GetStringAsync("/api/export" + query);
            Assert.Contains("\"actual_member\"", csv);
            Assert.Contains("\"legacy_proxy\"", csv);
            Assert.DoesNotContain("\"local_proxy\"", csv);
            Assert.DoesNotContain("Synthetic total", csv);
        }
    }

    [Fact] public async Task OrdinaryHistorySelectorKeepsQueriesNodesAndExportsInOneBasis()
    {
        await server.DisposeAsync(); client.Dispose();
        V2rayNMonitor.Core.ScopeLedgerMigration.Migrate(Path.Combine(root, "history.db"), Path.Combine(root, "synthetic-before-dimensions.db"));
        server = await MonitorServer.StartAsync(root, Path.Combine(root, "web"), useScopeLedger: true);
        client = new(new HttpClientHandler { UseCookies = true, UseProxy = false }) { BaseAddress = new(server.Address) };
        await Login();
        var scopes = new[]
        {
            new V2rayNMonitor.Core.MeasurementScope("actual_member", "index_id", "member", "Synthetic member", "member", "proxy"),
            new V2rayNMonitor.Core.MeasurementScope("legacy_proxy", "legacy_node", "synthetic/legacy", "Synthetic legacy", "legacy"),
            new V2rayNMonitor.Core.MeasurementScope("routed_outbound", "outbound",
                JsonSerializer.Serialize(new[] { new string('A', 64), "route" }), "Synthetic route", OutboundTag: "route"),
            new V2rayNMonitor.Core.MeasurementScope("business", "aggregate", "business", "business"),
            new V2rayNMonitor.Core.MeasurementScope("excluded", "aggregate", "excluded", "excluded"),
            new V2rayNMonitor.Core.MeasurementScope("unknown", "aggregate", "unknown", "unknown")
        };
        var amounts = new long[] { 11, 13, 17, 101, 0, 0 };
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        foreach (var second in new[] { 0, 1 })
            server.Scopes!.Accept(new("synthetic", "core", "config", at.AddSeconds(second),
                scopes.Select((scope, i) => scope.Basis == "unknown"
                    ? new V2rayNMonitor.Core.ScopeReading(scope, UnavailableReason: "missing_counter")
                    : new(scope, second * amounts[i], 0)).ToArray(), []));
        var status = await client.GetFromJsonAsync<JsonElement>("/api/status");
        var bases = status.GetProperty("historyBases").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Equal(new[] { "local_proxy", "legacy_proxy", "actual_member", "business", "excluded", "unknown", "routed_outbound" }, bases);
        const string query = "?start=2026-01-01T00:00:00Z&end=2026-01-02T00:00:00Z&timeZoneId=UTC";
        var combined = await client.GetFromJsonAsync<JsonElement>("/api/history" + query);
        Assert.Equal("24", combined.GetProperty("upload").GetString());
        for (var i = 0; i < scopes.Length; i++)
        {
            var basis = scopes[i].Basis;
            var nodes = await client.GetFromJsonAsync<JsonElement>("/api/nodes?basis=" + basis);
            Assert.Single(nodes.EnumerateArray());
            Assert.False(nodes[0].TryGetProperty("identityValue", out _));
            var key = nodes[0].GetProperty("key").GetString();
            var result = await client.GetFromJsonAsync<JsonElement>("/api/history" + query + "&basis=" + basis + "&nodeKey=" + key);
            Assert.Equal(basis, result.GetProperty("basis").GetString());
            Assert.Equal(amounts[i].ToString(), result.GetProperty("upload").GetString());
            var csv = await client.GetStringAsync("/api/export" + query + "&basis=" + basis + "&nodeKey=" + key);
            if (basis == "unknown")
            {
                Assert.Empty(result.GetProperty("nodes").EnumerateArray());
                Assert.Equal(0, result.GetProperty("recordCount").GetInt32());
                Assert.True(result.GetProperty("gapCount").GetInt32() > 0);
                Assert.DoesNotContain("\"unknown\"", csv);
            }
            else
            {
                Assert.Single(result.GetProperty("nodes").EnumerateArray());
                Assert.Equal(1, result.GetProperty("recordCount").GetInt32());
                Assert.Contains("\"" + basis + "\"", csv);
                foreach (var other in scopes.Where(x => x.Basis != basis))
                    Assert.DoesNotContain("\"" + other.Basis + "\"", csv);
            }
        }
        foreach (var invalid in new[] { "all", "actual_member,business", "business&basis=excluded", "probe_urltest_member" })
            foreach (var path in new[] { "/api/nodes", "/api/history", "/api/export" })
                Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(path + query + "&basis=" + invalid)).StatusCode);
    }

    [Fact] public async Task LegacyHistoryDoesNotAdvertiseOrAcceptUnrecordedScopeDimensions()
    {
        await Login();
        var status = await client.GetFromJsonAsync<JsonElement>("/api/status");
        Assert.Equal(new[] { "local_proxy" }, status.GetProperty("historyBases").EnumerateArray().Select(x => x.GetString()));
        foreach (var basis in new[] { "actual_member", "business", "excluded", "unknown", "legacy_proxy", "routed_outbound" })
            foreach (var path in new[] { "/api/nodes", "/api/history", "/api/export" })
                Assert.Equal(HttpStatusCode.BadRequest,
                    (await client.GetAsync(path + "?start=2026-01-01T00:00:00Z&end=2026-01-02T00:00:00Z&basis=" + basis)).StatusCode);
    }

    [Fact] public async Task ProbeHistoryUsesExplicitBasisWhileDefaultHistoryAndExportRemainBusinessOnly()
    {
        await server.DisposeAsync();
        client.Dispose();
        var provider = File.ReadAllBytes(Path.Combine(root, "provider.db"));
        V2rayNMonitor.Core.ScopeLedgerMigration.Migrate(Path.Combine(root, "history.db"), Path.Combine(root, "synthetic-v1-backup.db"));
        V2rayNMonitor.Core.ScopeLedgerMigration.MigrateProbes(Path.Combine(root, "history.db"), Path.Combine(root, "synthetic-v2-backup.db"));
        Assert.Equal(provider, File.ReadAllBytes(Path.Combine(root, "provider.db")));
        server = await MonitorServer.StartAsync(root, Path.Combine(root, "web"), useProbeLedger: true);
        client = new(new HttpClientHandler { UseCookies = true, UseProxy = false }) { BaseAddress = new(server.Address) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/scope-history?basis=probe_urltest_member")).StatusCode);
        await Login();
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var member = new V2rayNMonitor.Core.MeasurementScope("actual_member", "index_id", "synthetic", "Synthetic business", "synthetic", "proxy");
        var probe = member with { Basis = "probe_urltest_member", Name = "Synthetic probe" };
        var aggregate = new V2rayNMonitor.Core.MeasurementScope("probe_urltest_business", "aggregate", "probe_urltest_business", "Synthetic probes");
        foreach (var second in new[] { 0, 1 })
            server.Scopes!.Accept(new("synthetic-instance", "synthetic-core", "synthetic-config", at.AddSeconds(second),
                [new(member, second * 17, 0), new(probe, second * 9007199254740993, second * 7),
                 new(aggregate, second * 9007199254740993, second * 7)], []));
        const string query = "?start=2026-01-01T00:00:00Z&end=2026-01-02T00:00:00Z&timeZoneId=UTC";
        var business = await client.GetFromJsonAsync<JsonElement>("/api/history" + query);
        Assert.Equal("17", business.GetProperty("upload").GetString());
        var probes = await client.GetFromJsonAsync<JsonElement>("/api/scope-history" + query + "&basis=probe_urltest_member");
        Assert.Equal("probe_urltest_member", probes.GetProperty("basis").GetString());
        Assert.Equal("9007199254740993", probes.GetProperty("upload").GetString());
        Assert.Equal("7", probes.GetProperty("download").GetString());
        var page = await client.GetFromJsonAsync<JsonElement>("/api/history" + query + "&basis=probe_urltest_member&nonZeroRecords=true");
        Assert.Equal("9007199254740993", page.GetProperty("upload").GetString());
        Assert.Equal("Synthetic probe", page.GetProperty("nodes")[0].GetProperty("name").GetString());
        Assert.StartsWith("2026-01-01T00:00:00", page.GetProperty("earliestRecord").GetString());
        var probeNodes = await client.GetFromJsonAsync<JsonElement>("/api/nodes?basis=probe_urltest_member");
        Assert.Single(probeNodes.EnumerateArray());
        Assert.Equal("Synthetic probe", probeNodes[0].GetProperty("name").GetString());
        Assert.False(probeNodes[0].TryGetProperty("identityValue", out _));
        var probeCsv = await client.GetStringAsync("/api/export" + query + "&basis=probe_urltest_member");
        Assert.Contains("\"probe_urltest_member\"", probeCsv);
        Assert.Contains("Synthetic probe", probeCsv);
        Assert.DoesNotContain("Synthetic business", probeCsv);
        var total = await client.GetFromJsonAsync<JsonElement>("/api/scope-history" + query + "&basis=probe_urltest_business");
        Assert.Equal(probes.GetProperty("upload").GetString(), total.GetProperty("upload").GetString());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/api/scope-history" + query + "&basis=actual_member,probe_urltest_member")).StatusCode);
        var csv = await client.GetStringAsync("/api/export" + query);
        Assert.DoesNotContain("Synthetic probe", csv);
        Assert.DoesNotContain("probe_urltest", csv);
        var nodes = await client.GetFromJsonAsync<JsonElement>("/api/nodes");
        Assert.Single(nodes.EnumerateArray());
        Assert.Equal("Synthetic business", nodes[0].GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/history" + query + "&basis=business,probe_urltest_member")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/nodes?basis=probe_urltest_member&basis=probe_urltest_business")).StatusCode);
        var status = await client.GetFromJsonAsync<JsonElement>("/api/status");
        Assert.True(status.GetProperty("probes").GetProperty("historyEnabled").GetBoolean());
    }

    [Fact] public async Task ProbeStartupCreatesV3ExplicitlyAndRejectsModeMismatchWithoutMigration()
    {
        var fresh = Path.Combine(root, "synthetic-probe-data");
        await using (var probe = await MonitorServer.StartAsync(fresh, Path.Combine(root, "web"), useProbeLedger: true))
            Assert.True(probe.Scopes!.SupportsProbes);
        var v3 = File.ReadAllBytes(Path.Combine(fresh, "history.db"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MonitorServer.StartAsync(fresh, Path.Combine(root, "web"), useScopeLedger: true));
        Assert.Equal(v3, File.ReadAllBytes(Path.Combine(fresh, "history.db")));
        var old = Path.Combine(root, "synthetic-member-data");
        await using (var member = await MonitorServer.StartAsync(old, Path.Combine(root, "web"), useScopeLedger: true))
            Assert.False(member.Scopes!.SupportsProbes);
        var v2 = File.ReadAllBytes(Path.Combine(old, "history.db"));
        var provider = File.ReadAllBytes(Path.Combine(old, "provider.db"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MonitorServer.StartAsync(old, Path.Combine(root, "web"), useProbeLedger: true));
        Assert.Equal(v2, File.ReadAllBytes(Path.Combine(old, "history.db")));
        Assert.Equal(provider, File.ReadAllBytes(Path.Combine(old, "provider.db")));
        await Login();
        var status = await client.GetFromJsonAsync<JsonElement>("/api/status");
        Assert.False(status.GetProperty("probes").GetProperty("historyEnabled").GetBoolean());
        const string query = "?start=2026-01-01T00:00:00Z&end=2026-01-02T00:00:00Z";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/history" + query + "&basis=probe_urltest_member")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/export" + query + "&basis=probe_urltest_member")).StatusCode);
    }

    [Fact] public async Task ScopeStartupDoesNotAutomaticallyMigrateOrTouchProviderDatabase()
    {
        await server.DisposeAsync();
        var history = File.ReadAllBytes(Path.Combine(root, "history.db"));
        var provider = File.ReadAllBytes(Path.Combine(root, "provider.db"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MonitorServer.StartAsync(root, Path.Combine(root, "web"), useScopeLedger: true));
        Assert.Equal(history, File.ReadAllBytes(Path.Combine(root, "history.db")));
        Assert.Equal(provider, File.ReadAllBytes(Path.Combine(root, "provider.db")));
        server = await MonitorServer.StartAsync(root, Path.Combine(root, "web"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonZeroDetailOptionFiltersPaginationButNeverTotalsOrRawExport(bool scopeMode)
    {
        if (scopeMode)
        {
            await server.DisposeAsync();
            client.Dispose();
            V2rayNMonitor.Core.ScopeLedgerMigration.Migrate(Path.Combine(root, "history.db"), Path.Combine(root, "synthetic-v1-backup.db"));
            server = await MonitorServer.StartAsync(root, Path.Combine(root, "web"), useScopeLedger: true);
            client = new(new HttpClientHandler { UseCookies = true, UseProxy = false }) { BaseAddress = new(server.Address) };
        }
        await Login();
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        foreach (var second in new[] { 0, 1, 61 })
        {
            if (scopeMode)
                server.Scopes!.Accept(new("synthetic-instance", "synthetic-core", "synthetic-config", at.AddSeconds(second),
                    [new(new("actual_member", "index_id", "active", "Synthetic active", "active", "active"), second == 0 ? 0 : 5, second == 0 ? 0 : 7),
                     new(new("actual_member", "index_id", "idle", "Synthetic idle", "idle", "idle"), 0, 0)], []));
            else
            {
                server.Ledger.Accept(new("synthetic-active-instance", "active", "Synthetic active", "core", "config", at.AddSeconds(second), second == 0 ? 0 : 5, second == 0 ? 0 : 7));
                server.Ledger.Accept(new("synthetic-idle-instance", "idle", "Synthetic idle", "core", "config", at.AddSeconds(second), 0, 0));
            }
        }
        const string query = "?start=2026-01-01T00:00:00Z&end=2026-01-02T00:00:00Z&timeZoneId=UTC";
        var all = await client.GetFromJsonAsync<JsonElement>("/api/history" + query);
        var filtered = await client.GetFromJsonAsync<JsonElement>("/api/history" + query + "&nonZeroRecords=true&pageSize=1");
        Assert.Equal(4, all.GetProperty("recordCount").GetInt32());
        Assert.Equal(1, filtered.GetProperty("recordCount").GetInt32());
        Assert.Equal("5", filtered.GetProperty("records")[0].GetProperty("upload").GetString());
        foreach (var field in new[] { "upload", "download", "series", "nodes", "gaps" })
            Assert.Equal(all.GetProperty(field).GetRawText(), filtered.GetProperty(field).GetRawText());
        var secondPage = await client.GetFromJsonAsync<JsonElement>("/api/history" + query + "&nonZeroRecords=true&pageSize=1&page=1");
        Assert.Empty(secondPage.GetProperty("records").EnumerateArray());
        Assert.Equal(await client.GetStringAsync("/api/export" + query),
            await client.GetStringAsync("/api/export" + query + "&nonZeroRecords=true"));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/history" + query + "&nonZeroRecords=invalid")).StatusCode);
    }

    [Fact] public async Task ReadAndWriteApisRequireSession()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/settings", new { })).StatusCode);
        await Login(); Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/status")).StatusCode);
    }
    [Fact] public async Task PartialCollectionIsExplicitInStatusHistoryAndExport()
    {
        await Login();
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        server.SetStatus(new("collecting", "Synthetic partial collector", "Synthetic node",
            Scope: new V2rayNMonitor.Core.ProxyCollectionScope(37, 4)));
        server.Ledger.Accept(new("test", "one", "Synthetic node", "process1", "config1", at, 100, 200, true));
        server.Ledger.Accept(new("test", "one", "Synthetic node", "process1", "config1", at.AddSeconds(1), 110, 220, true));
        var status = await client.GetFromJsonAsync<JsonElement>("/api/status");
        Assert.Equal("routed_proxy", status.GetProperty("scope").GetProperty("basis").GetString());
        Assert.True(status.GetProperty("scope").GetProperty("isPartial").GetBoolean());
        Assert.Equal(37, status.GetProperty("scope").GetProperty("uncollectedOutbounds").GetInt32());
        const string query = "?start=2026-01-01T00:00:00Z&end=2026-01-02T00:00:00Z&timeZoneId=UTC";
        var history = await client.GetFromJsonAsync<JsonElement>("/api/history" + query);
        Assert.Equal("10", history.GetProperty("upload").GetString());
        Assert.Equal("20", history.GetProperty("download").GetString());
        Assert.True(history.GetProperty("partialCoverage").GetBoolean());
        Assert.True(history.GetProperty("records")[0].GetProperty("partialCoverage").GetBoolean());
        Assert.Equal("uncollected_outbounds", history.GetProperty("gaps")[0].GetProperty("reason").GetString());
        Assert.Contains("measured;uncollected_outbounds", await client.GetStringAsync("/api/export" + query));
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
    [Fact] public async Task LocalExportIncludesEveryRowBeyondOnePageAndStaysSeparateFromProviderData()
    {
        await Login();
        server.Ledger.SeedDemo(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var imported = await client.PostAsJsonAsync("/api/imports/commit", new
        {
            source = "synthetic-provider", timeZoneId = "UTC",
            csv = "node_name,rate,origin_traffic,origin_x_rate,traffic,log_time\nProvider-only,1,100,100,synthetic,2025-12-31 10:00:00"
        });
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        const string query = "?start=2024-01-01T00:00:00Z&end=2026-01-02T00:00:00Z&timeZoneId=UTC&page=2&pageSize=1";
        var history = await client.GetFromJsonAsync<JsonElement>("/api/history" + query);
        var response = await client.GetAsync("/api/export" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        var csv = await response.Content.ReadAsStringAsync();
        var rows = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
        Assert.Equal(1200, rows.Length);
        Assert.Equal(rows.Length, rows.Distinct().Count());
        Assert.Equal(history.GetProperty("upload").GetString(), rows.Sum(row => long.Parse(row.Split(',')[3], System.Globalization.CultureInfo.InvariantCulture)).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(history.GetProperty("download").GetString(), rows.Sum(row => long.Parse(row.Split(',')[4], System.Globalization.CultureInfo.InvariantCulture)).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.DoesNotContain("Provider-only", csv);
    }

    [Fact] public async Task LocalExportRejectsTooManyRowsBeforeReturningCsv()
    {
        await Login();
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        server.Ledger.Accept(new("test", "one", "Synthetic", "process1", "config1", at, 0, 0));
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = Path.Combine(root, "history.db"), Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                WITH RECURSIVE synthetic(n) AS (VALUES(0) UNION ALL SELECT n+1 FROM synthetic WHERE n<100000)
                INSERT INTO minutes(start_ticks,node_key,upload,download)
                SELECT {at.UtcTicks}+n*{TimeSpan.TicksPerMinute},'test/one',1,2 FROM synthetic;
                """;
            command.ExecuteNonQuery();
        }
        var response = await client.GetAsync("/api/export?start=2026-01-01T00:00:00Z&end=2027-01-01T00:00:00Z&timeZoneId=UTC");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("100000", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
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

    [Fact] public async Task ProviderHistoryIsQueryableBySourceNodeAndDateWithoutChangingLocalTotals()
    {
        await Login();
        const string csv = """
            node_name,rate,origin_traffic,origin_x_rate,traffic,log_time
            Synthetic-A,0.8,1001,800.800000000123,synthetic,2026-01-01 10:00:00
            Synthetic-A,0,99,0,synthetic,2026-01-02 10:00:00
            Synthetic-B,1,200,200,synthetic,2026-01-01 11:00:00
            """;
        var committed = await client.PostAsJsonAsync("/api/imports/commit", new { source = "source-one", timeZoneId = "UTC", csv });
        Assert.Equal(HttpStatusCode.OK, committed.StatusCode);
        await client.PostAsJsonAsync("/api/imports/commit", new { source = "source-two", timeZoneId = "UTC", csv });
        const string query = "?source=source-one&start=2026-01-01T00:00:00Z&end=2026-01-03T00:00:00Z&timeZoneId=UTC";
        var response = await client.GetAsync("/api/provider/history" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("1300", result.GetProperty("rawBytes").GetString());
        Assert.Equal("1000.800000000123", result.GetProperty("billedBytes").GetString());
        Assert.Equal(3, result.GetProperty("recordCount").GetInt32());
        Assert.Equal(2, result.GetProperty("nodes").GetArrayLength());
        Assert.Equal(2, result.GetProperty("series").GetArrayLength());
        var filtered = await client.GetFromJsonAsync<JsonElement>("/api/provider/history" + query + "&nodeKey=Synthetic-A&pageSize=1");
        Assert.Equal("1100", filtered.GetProperty("rawBytes").GetString());
        Assert.Equal("800.800000000123", filtered.GetProperty("billedBytes").GetString());
        Assert.Equal(2, filtered.GetProperty("recordCount").GetInt32());
        Assert.Equal(1, filtered.GetProperty("records").GetArrayLength());
        var exported = await client.GetStringAsync("/api/provider/export" + query);
        Assert.Contains("800.800000000123", exported);
        Assert.Contains("source-one", exported);
        Assert.DoesNotContain("source-two", exported);
        Assert.Empty(server.Ledger.GetNodes());
    }

    [Fact] public async Task ExactDisplayNameFilterAggregatesDifferentIdsWithoutMergingTheirHistories()
    {
        await Login();
        var at = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        foreach (var (id, name, amount) in new[] { ("a", "Same", 10), ("b", "Same", 25), ("c", "same", 900), ("d", "Same ", 700) })
        {
            server.Ledger.Accept(new("test-" + id, id, name, "p", "g", at, 0, 0));
            server.Ledger.Accept(new("test-" + id, id, name, "p", "g", at.AddSeconds(1), amount, 0));
        }
        const string query = "?start=2026-01-01T00:00:00Z&end=2026-01-02T00:00:00Z&timeZoneId=UTC&bucket=hour&nodeName=Same";
        var result = await client.GetFromJsonAsync<JsonElement>("/api/history" + query);
        Assert.Equal("35", result.GetProperty("upload").GetString());
        Assert.Equal(2, result.GetProperty("nodes").GetArrayLength());
        Assert.Equal(2, result.GetProperty("recordCount").GetInt32());
        Assert.Equal("35", result.GetProperty("series")[0].GetProperty("upload").GetString());
        Assert.Equal(4, server.Ledger.GetNodes().Count);
        var csv = await client.GetStringAsync("/api/export" + query);
        Assert.DoesNotContain("\"same\"", csv); Assert.DoesNotContain("\"Same \"", csv);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(501)]
    public async Task StoredLongDisplayNamesRemainQueryableWithoutTruncation(int length)
    {
        await Login();
        var name = new string('N', length);
        var at = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        server.Ledger.Accept(new("test", "one", name, "p", "g", at, 0, 0));
        server.Ledger.Accept(new("test", "one", name, "p", "g", at.AddSeconds(1), 7, 0));
        var response = await client.GetAsync("/api/history?start=2026-01-01T00:00:00Z&end=2026-01-02T00:00:00Z&timeZoneId=UTC&nodeName=" + name);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("7", result.GetProperty("upload").GetString());
        Assert.Equal(name, result.GetProperty("nodes")[0].GetProperty("name").GetString());
    }

    [Fact] public async Task OverlappingProviderSnapshotsPreviewAndCommitOnlyNewOccurrences()
    {
        await Login();
        const string header = "node_name,rate,origin_traffic,origin_x_rate,traffic,log_time\n";
        const string shared = "Synthetic-A,0.8,10,8.000000000123,display,2026-01-01 10:00:00\n";
        var a = new { source = "synthetic-overlap", timeZoneId = "UTC", csv = header + shared + shared };
        var b = new { a.source, a.timeZoneId, csv = header + shared + shared + shared +
            "Synthetic-B,0.5,20,10.000000000123,display,2026-01-01 11:00:00" };
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/imports/commit", a)).StatusCode);
        var preview = await client.PostAsJsonAsync("/api/imports/preview", b);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var body = await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(4, body.GetProperty("rowCount").GetInt32());
        Assert.Equal(2, body.GetProperty("newRowCount").GetInt32());
        Assert.Equal(2, body.GetProperty("skippedRowCount").GetInt32());
        Assert.Equal("18.000000000246", body.GetProperty("newBilledBytes").GetString());
        var commit = await client.PostAsJsonAsync("/api/imports/commit", b);
        Assert.Equal(HttpStatusCode.OK, commit.StatusCode);
        body = await commit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("newRowCount").GetInt32());
        var result = await client.GetFromJsonAsync<JsonElement>("/api/provider/history?source=synthetic-overlap&start=2026-01-01T00:00:00Z&end=2026-01-02T00:00:00Z");
        Assert.Equal(4, result.GetProperty("recordCount").GetInt32());
        Assert.Equal("50", result.GetProperty("rawBytes").GetString());
        Assert.Equal("34.000000000492", result.GetProperty("billedBytes").GetString());
        var duplicate = await client.PostAsJsonAsync("/api/imports/commit", b);
        body = await duplicate.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("duplicate").GetBoolean());
        Assert.Equal(0, body.GetProperty("newRowCount").GetInt32());
        Assert.Equal(4, body.GetProperty("skippedRowCount").GetInt32());
        var batches = await client.GetFromJsonAsync<JsonElement>("/api/imports");
        Assert.Equal(2, batches.GetArrayLength());
        Assert.Empty(server.Ledger.GetNodes());
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
