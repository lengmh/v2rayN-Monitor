using System.Text;
using V2rayNMonitor.Core;
using Xunit;

namespace Core.Tests;

// Deliberately synthetic inputs. No real endpoint, user credential, or profile is embedded.
public sealed class XrayTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    private static string Metrics(string proxy) => "{\"stats\":{\"outbound\":{\"proxy\":" + proxy + ",\"direct\":{\"uplink\":999,\"downlink\":999}}}}";

    [Fact]
    public void MetricsKeepsRawInt64AndOnlyProxyOutbound()
    {
        Assert.True(XrayParser.TryReadCounters(Bytes(Metrics("{\"uplink\":9007199254740993,\"downlink\":9223372036854775807}")), out var counts));
        Assert.Equal(9007199254740993L, counts!.Upload);
        Assert.Equal(long.MaxValue, counts.Download);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"uplink\":1}")]
    [InlineData("{\"uplink\":1,\"downlink\":null}")]
    [InlineData("{\"uplink\":-1,\"downlink\":2}")]
    [InlineData("{\"uplink\":1.5,\"downlink\":2}")]
    [InlineData("{\"uplink\":1,\"downlink\":9223372036854775808}")]
    [InlineData("{\"uplink\":\"1\",\"downlink\":2}")]
    [InlineData("{\"uplink\":1,\"uplink\":3,\"downlink\":2}")]
    public void InvalidOrMissingCountersAreUnknownNotZero(string proxy)
    {
        Assert.False(XrayParser.TryReadCounters(Bytes(Metrics(proxy)), out var counts));
        Assert.Null(counts);
    }

    [Fact]
    public void ExplicitZeroCountersRemainKnown()
    {
        Assert.True(XrayParser.TryReadCounters(Bytes(Metrics("{\"uplink\":0,\"downlink\":0}")), out var counts));
        Assert.Equal(new XrayCounters(0, 0), counts);
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("{\"stats\":{\"inbound\":{\"proxy\":{\"uplink\":1,\"downlink\":2}}}}")]
    [InlineData("{\"stats\":{\"outbound\":{\"direct\":{\"uplink\":1,\"downlink\":2}}}}")]
    [InlineData("{\"error\":\"no statistics\"}")]
    public void MissingInterfaceContractDoesNotBecomeZero(string json)
    {
        Assert.False(XrayParser.TryReadCounters(Bytes(json), out var counts));
        Assert.Null(counts);
    }

    [Theory]
    [InlineData("127.0.0.1:12345")]
    [InlineData("[::1]:12345")]
    public void EndpointIsOnlyReadonlyExpvarWithoutQueryOrReset(string listen)
    {
        Assert.True(XrayParser.TryLoopbackEndpoint(listen, out var endpoint));
        Assert.Equal("http", endpoint!.Scheme);
        Assert.Equal("/debug/vars", endpoint.AbsolutePath);
        Assert.Equal("", endpoint.Query);
        Assert.Equal("", endpoint.UserInfo);
    }

    [Theory]
    [InlineData("0.0.0.0:12345")]
    [InlineData("[::]:12345")]
    [InlineData("192.0.2.1:12345")]
    [InlineData("example.invalid:12345")]
    [InlineData("127.0.0.1:0")]
    [InlineData("http://127.0.0.1:12345/reset")]
    public void NonLoopbackOrNonLiteralEndpointIsRejected(string listen)
        => Assert.False(XrayParser.TryLoopbackEndpoint(listen, out _));

    [Fact]
    public void GuiSelectionRequiresPinnedPascalCaseFields()
    {
        const string gui = "{\"IndexId\":\"synthetic-A\",\"GuiItem\":{\"EnableStatistics\":true,\"DisplayRealTimeSpeed\":true},\"TunModeItem\":{\"EnableTun\":false}}";
        Assert.True(XrayParser.TryReadSelection(Bytes(gui), out var selected));
        Assert.Equal("synthetic-A", selected!.IndexId);
        Assert.False(XrayParser.TryReadSelection(Bytes(gui.Replace("IndexId", "indexId")), out _));
        Assert.False(XrayParser.TryReadSelection(Bytes(gui.Replace(",\"DisplayRealTimeSpeed\":true", "")), out _));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public void EitherV2rayNStatisticsSwitchEnablesMetrics(bool statistics, bool speed, bool expected)
        => Assert.Equal(expected, new XrayGuiSelection("synthetic-A", statistics, speed, false).StatisticsRequested);

    private static string Configuration(string extraOutbound = "", string proxyExtra = "", string routing = "") => """
        {"stats":{},"policy":{"system":{"statsOutboundUplink":true,"statsOutboundDownlink":true}},
         "metrics":{"listen":"127.0.0.1:12345"},"outbounds":[
         {"tag":"proxy","protocol":"vless","settings":{"address":"node.invalid","port":443,"id":"00000000-0000-0000-0000-000000000001"}
        """ + proxyExtra + "},{\"tag\":\"direct\",\"protocol\":\"freedom\"},{\"tag\":\"block\",\"protocol\":\"blackhole\"}" + extraOutbound + "]" + routing + "}";

    [Fact]
    public void OrdinarySingleProxyConfigurationCarriesOnlySafeIdentity()
    {
        Assert.True(XrayParser.TryReadConfiguration(Bytes(Configuration()), out var config, out var reason));
        Assert.Equal("ready", reason);
        Assert.Equal("vless", config!.Protocol);
        Assert.Equal(XrayParser.ServerIdentity("vless", "node.invalid", 443, "", "00000000-0000-0000-0000-000000000001"), config.ServerIdentity);
        Assert.DoesNotContain("node.invalid", config.ToString());
        Assert.DoesNotContain("00000000", config.ToString());
    }

    [Theory]
    [InlineData(",\"proxySettings\":{\"tag\":\"other\"}")]
    [InlineData(",\"streamSettings\":{\"sockopt\":{\"dialerProxy\":\"other\"}}")]
    public void ChainingIsRejected(string proxyExtra)
        => Assert.False(XrayParser.TryReadConfiguration(Bytes(Configuration(proxyExtra: proxyExtra)), out _, out _));

    [Fact]
    public void MultipleProxiesAndBalancersKeepOnlyVerifiedProxyWithPartialCoverage()
    {
        var json = Configuration(",{\"tag\":\"proxy-2\",\"protocol\":\"vless\"}",
            routing: ",\"routing\":{\"balancers\":[{\"tag\":\"group\",\"selector\":[\"proxy\"]}]}");
        Assert.True(XrayParser.TryReadConfiguration(Bytes(json), out var config, out _));
        Assert.Equal(new ProxyCollectionScope(1, 1, "xray_outbound_proxy"), config!.Scope);
        Assert.True(config.Scope!.IsPartial);
        Assert.Contains("其他 1 个代理出站未采集", config.Scope.Description);
        var snapshot = Snapshot() with { Scope = config.Scope };
        var reading = Assert.Single(ScopeCollector.ProjectLegacy(snapshot, new(101, 202), Now, false).Readings);
        Assert.True(reading.PartialCoverage);
        Assert.Equal(101L, reading.Upload);
        Assert.Equal(202L, reading.Download);
        Assert.Equal("legacy_proxy", reading.Scope.Basis);
    }

    [Theory]
    [InlineData(",{\"tag\":\"proxy\",\"protocol\":\"vless\"}", "")]
    [InlineData(",{\"tag\":\"other\",\"protocol\":\"vless\",\"proxySettings\":{\"tag\":\"proxy\"}}", "")]
    [InlineData("", ",\"routing\":{\"balancers\":{}}")]
    public void AmbiguousOrChainedMultiOutboundConfigurationsRemainRejected(string outbound, string routing)
        => Assert.False(XrayParser.TryReadConfiguration(Bytes(Configuration(outbound, routing: routing)), out _, out _));

    [Fact]
    public void MissingCounterEnablementIsNotAssumed()
    {
        Assert.False(XrayParser.TryReadConfiguration(Bytes(Configuration().Replace("\"statsOutboundUplink\":true", "\"statsOutboundUplink\":false")), out _, out var reason));
        Assert.Equal("statistics_disabled", reason);
    }

    [Theory]
    [InlineData("vmess", "id", "synthetic-id", "")]
    [InlineData("vless", "id", "synthetic-id", "")]
    [InlineData("trojan", "password", "synthetic-password", "")]
    [InlineData("shadowsocks", "password", "synthetic-password", "")]
    [InlineData("socks", "pass", "synthetic-password", "synthetic-user")]
    [InlineData("http", "pass", "synthetic-password", "synthetic-user")]
    public void PinnedFlatSettingsSupportOrdinaryProtocols(string protocol, string credentialKey, string secret, string user)
    {
        var settings = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["address"] = "node.invalid", ["port"] = 443, [credentialKey] = secret
        });
        if (user.Length != 0) settings = settings[..^1] + ",\"user\":\"synthetic-user\"}";
        var json = Configuration().Replace("\"protocol\":\"vless\"", "\"protocol\":\"" + protocol + "\"")
            .Replace("{\"address\":\"node.invalid\",\"port\":443,\"id\":\"00000000-0000-0000-0000-000000000001\"}", settings);
        Assert.True(XrayParser.TryReadConfiguration(Bytes(json), out var config, out _));
        Assert.Equal(XrayParser.ServerIdentity(protocol, "node.invalid", 443, user, secret), config!.ServerIdentity);
    }

    [Fact]
    public void LegacyOrMixedSettingsAreOutsidePinnedVersionContract()
    {
        var legacy = Configuration().Replace("{\"address\":\"node.invalid\",\"port\":443,\"id\":\"00000000-0000-0000-0000-000000000001\"}",
            "{\"vnext\":[{\"address\":\"node.invalid\",\"port\":443,\"users\":[{\"id\":\"00000000-0000-0000-0000-000000000001\"}]}]}");
        Assert.False(XrayParser.TryReadConfiguration(Bytes(legacy), out _, out _));
        var mixed = Configuration().Replace("\"address\":\"node.invalid\"", "\"vnext\":[],\"address\":\"node.invalid\"");
        Assert.False(XrayParser.TryReadConfiguration(Bytes(mixed), out _, out _));
    }

    [Fact]
    public void EmptyNullableDialerProxyDoesNotCreateAChain()
        => Assert.True(XrayParser.TryReadConfiguration(Bytes(Configuration(proxyExtra: ",\"streamSettings\":{\"sockopt\":{\"dialerProxy\":\"\"}}")), out _, out _));

    [Fact]
    public void OnlyActualGuidsAreCaseNormalized()
    {
        Assert.NotEqual(XrayParser.ServerIdentity("vless", "node.invalid", 443, "", "CustomPassword"),
            XrayParser.ServerIdentity("vless", "node.invalid", 443, "", "custompassword"));
        Assert.Equal(XrayParser.ServerIdentity("vless", "node.invalid", 443, "", "AAAAAAAA-0000-0000-0000-000000000001"),
            XrayParser.ServerIdentity("vless", "NODE.INVALID", 443, "", "aaaaaaaa-0000-0000-0000-000000000001"));
    }

    [Fact]
    public void CredentialFieldSeparatorsCannotCollide()
        => Assert.NotEqual(XrayParser.ServerIdentity("socks", "node.invalid", 443, "u\np1", "p2"),
            XrayParser.ServerIdentity("socks", "node.invalid", 443, "u", "p1\np2"));

    private static XraySnapshot Snapshot(string node = "A", string process = "pid:100:start:1", string generation = "hash:1")
        => new("synthetic-instance", node, "Synthetic " + node, process, generation, Now, Now.AddSeconds(-1),
            "127.0.0.1:12345:100", new Uri("http://127.0.0.1:12345/debug/vars"));

    [Fact]
    public void StableBracketingSnapshotsAreAccepted()
    {
        var guard = new XrayGenerationGuard();
        Assert.True(guard.Accept(Snapshot(), Snapshot(), out _));
    }

    [Fact]
    public void SelectionOnlySwitchRejectsTheRaceButAllowsANewStableBaseline()
    {
        var guard = new XrayGenerationGuard();
        Assert.True(guard.Observe(Snapshot(), out _));
        Assert.False(guard.Accept(Snapshot(), Snapshot(node: "B"), out _));
        Assert.True(guard.RequiresBaseline);
        Assert.True(guard.Accept(Snapshot(node: "B"), Snapshot(node: "B"), out _));
        Assert.True(guard.RequiresBaseline);
        guard.AcknowledgePersistence();
        Assert.False(guard.RequiresBaseline);
        Assert.True(guard.Observe(Snapshot(), out _));
        Assert.True(guard.RequiresBaseline);
        Assert.True(guard.Observe(Snapshot(node: "B", process: "pid:100:start:2"), out _));
    }

    [Fact]
    public void RenameWithinTheSameSelectionIsDisplayOnly()
    {
        var guard = new XrayGenerationGuard();
        Assert.True(guard.Accept(Snapshot(), Snapshot() with { NodeName = "Renamed synthetic" }, out _));
        Assert.False(guard.RequiresBaseline);
    }

    [Fact]
    public void ChangedConfigCannotReuseOldProcessEvenIfCountersWouldBeLarger()
    {
        var guard = new XrayGenerationGuard();
        Assert.True(guard.Observe(Snapshot(), out _));
        Assert.False(guard.Observe(Snapshot(generation: "hash:2"), out _));
        // The guard never inspects counter magnitude. A restart with larger counters is still a new session.
        var restarted = Snapshot(process: "pid:200:start:2", generation: "hash:2");
        Assert.True(guard.Accept(restarted, restarted, out _));
        Assert.NotEqual(Snapshot().ProcessIdentity, restarted.ProcessIdentity);
    }

    [Fact]
    public void ConfigNewerThanCoreCannotBeAttributed()
    {
        var guard = new XrayGenerationGuard();
        var changed = Snapshot() with { ConfigurationWrittenAt = Now.AddSeconds(1) };
        Assert.False(guard.Observe(changed, out var reason));
        Assert.Equal("waiting_for_core", reason);
        Assert.False(guard.Observe(Snapshot(), out _));
        Assert.True(guard.Observe(Snapshot(process: "pid:100:start:2"), out _));
    }

    [Fact]
    public void ListenerOrProcessRaceRejectsSample()
    {
        var guard = new XrayGenerationGuard();
        Assert.False(guard.Accept(Snapshot(), Snapshot() with { ListenerIdentity = "127.0.0.1:12345:200" }, out _));
        guard = new();
        Assert.False(guard.Accept(Snapshot(), Snapshot(process: "pid:100:start:2"), out _));
    }
}
