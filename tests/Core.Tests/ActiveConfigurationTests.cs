using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using V2rayNMonitor.Core;
using V2rayNMonitor.Host;
using Xunit;

namespace Core.Tests;

// Entirely synthetic 95f68d9 shapes. These exercise the production read-only profile
// reader and guard/ledger together, not Windows process discovery or a real Xray core.
public sealed class ActiveConfigurationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "active-config-synthetic-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Start = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private string Profiles => Path.Combine(directory, "profiles.db");
    private string History => Path.Combine(directory, "history.db");
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    private const string Gui = """
        {"IndexId":"A","GuiItem":{"EnableStatistics":true,"DisplayRealTimeSpeed":false},"TunModeItem":{"EnableTun":false},
         "CoreBasicItem":{"DefFingerprint":"chrome","DefUserAgent":"Synthetic-UA"},
         "HysteriaItem":{"UpMbps":100,"DownMbps":100,"HopInterval":30}}
        """;
    private const string Configuration = """
        {"stats":{},"policy":{"system":{"statsOutboundUplink":true,"statsOutboundDownlink":true}},
         "metrics":{"listen":"127.0.0.1:12345"},"outbounds":[
         {"tag":"proxy","protocol":"vless","settings":{"address":"node.synthetic.invalid","port":443,"id":"00000000-0000-0000-0000-000000000001","encryption":"none"},
          "streamSettings":{"network":"xhttp","security":"tls","tlsSettings":{"serverName":"front.synthetic.invalid","fingerprint":"chrome","alpn":["h2"]},
          "xhttpSettings":{"host":"front.synthetic.invalid","path":"/synthetic-a","mode":"auto","extra":{"scMaxEachPostBytes":1234,"noGRPCHeader":true}}}},
         {"tag":"direct","protocol":"freedom"},{"tag":"block","protocol":"blackhole"}]}
        """;
    private static Dictionary<string, string?> Profile(string id = "A", string subscription = "one") => new()
    {
        ["IndexId"] = id, ["Subid"] = subscription, ["Remarks"] = "Same synthetic name", ["ConfigType"] = "5", ["CoreType"] = "2",
        ["Address"] = "node.synthetic.invalid", ["Port"] = "443", ["Password"] = "00000000-0000-0000-0000-000000000001", ["Username"] = "",
        ["Network"] = "xhttp", ["ProtoExtra"] = "{\"VlessEncryption\":\"none\"}",
        ["TransportExtra"] = """
            {"Host":" front.synthetic.invalid ","Path":" /synthetic-a ","XhttpMode":"auto","XhttpExtra":"{\"noGRPCHeader\":true,\"scMaxEachPostBytes\":1234}"}
            """,
        ["StreamSecurity"] = "tls", ["Sni"] = "", ["Alpn"] = "h2", ["Fingerprint"] = null,
        ["PublicKey"] = null, ["ShortId"] = null, ["SpiderX"] = null, ["Mldsa65Verify"] = null,
        ["AllowInsecure"] = "false", ["Cert"] = null, ["CertSha"] = null, ["EchConfigList"] = null,
        ["VerifyPeerCertByName"] = null, ["Finalmask"] = null
    };
    private void CreateProfiles(params Dictionary<string, string?>[] profiles)
    {
        Directory.CreateDirectory(directory);
        using var db = new SqliteConnection($"Data Source={Profiles};Pooling=False"); db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "CREATE TABLE ProfileItem (" + string.Join(',', profiles[0].Keys.Select(key => key + (key is "ConfigType" or "CoreType" or "Port" ? " INTEGER" : " TEXT"))) + ")";
        command.ExecuteNonQuery();
        foreach (var profile in profiles)
        {
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO ProfileItem (" + string.Join(',', profile.Keys) + ") VALUES (" + string.Join(',', profile.Keys.Select(key => "$" + key)) + ")";
            foreach (var (key, value) in profile) command.Parameters.AddWithValue("$" + key, (object?)value ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }
    private string Read(string id, string configuration = Configuration, string gui = Gui)
        => WindowsDiscovery.ReadProfile(Profiles, id, Bytes(gui), Bytes(configuration), out _);
    private static bool Matches(Dictionary<string, string?> profile, string configuration = Configuration, string gui = Gui)
        => XrayParser.MatchesSelectedConnection(profile, Bytes(gui), Bytes(configuration), out _);
    private static string Change(string source, Action<JsonObject> change)
    { var root = JsonNode.Parse(source)!.AsObject(); change(root); return root.ToJsonString(); }
    private static JsonObject Proxy(JsonObject root) => root["outbounds"]![0]!.AsObject();
    private static XraySnapshot Snapshot(string node = "A", string process = "core-1", string generation = "same-configuration")
        => new("instance", node, "Same synthetic name", process, generation, Start.AddMinutes(-1), Start.AddMinutes(-2),
            "listener-1", new Uri("http://127.0.0.1:12345/debug/vars"));
    private static CounterSample Sample(XraySnapshot snapshot, int second, long upload, long download = 0)
        => new(snapshot.InstanceId, snapshot.NodeId, snapshot.NodeName, snapshot.ProcessIdentity, snapshot.Generation, Start.AddSeconds(second), upload, download);
    private static QuerySpec All => new(Start.AddMinutes(-1), Start.AddHours(1), "UTC");
    private static string Accept(Ledger ledger, XrayGenerationGuard guard, XraySnapshot snapshot, int second, long upload, long download = 0)
    {
        Assert.True(guard.Observe(snapshot, out _));
        Assert.True(guard.Accept(snapshot, snapshot, out _));
        var result = ledger.Accept(Sample(snapshot, second, upload, download), guard.RequiresBaseline);
        if (result != "stale") guard.AcknowledgePersistence();
        return result;
    }

    private const string AnyTlsConfiguration = """
        {"inbounds":[{"type":"mixed","tag":"mixed","listen":"127.0.0.1","listen_port":12344}],
         "outbounds":[{"tag":"proxy","type":"anytls","server":"node.synthetic.invalid","server_port":443,
         "password":"Synthetic-AnyTLS-Only","tls":{"enabled":true,"server_name":"front.synthetic.invalid","alpn":["h2"]}},
         {"tag":"direct","type":"direct"}],"route":{"final":"proxy"}}
        """;
    private static byte[] StatisticsEndpoint => Bytes("""{"listen":"127.0.0.1:12345"}""");
    private static Dictionary<string, string?> AnyTlsProfile(string id = "A")
    {
        var profile = Profile(id);
        profile["ConfigType"] = "11"; profile["Password"] = "Synthetic-AnyTLS-Only";
        profile["Network"] = "raw"; profile["ProtoExtra"] = "{}";
        profile["TransportExtra"] = """{"Host":"front.synthetic.invalid"}""";
        return profile;
    }
    private string ReadAnyTls(string id = "A", string configuration = AnyTlsConfiguration)
        => WindowsDiscovery.ReadProfile(Profiles, id, Bytes(Gui), Bytes(configuration), out _, StatisticsEndpoint);
    private static bool MatchesAnyTls(Dictionary<string, string?> profile, string configuration = AnyTlsConfiguration, string gui = Gui)
        => SingboxParser.MatchesSelectedConnection(profile, Bytes(gui), Bytes(configuration), out _);

    [Fact]
    public void AnyTlsUsesSelectedIndexIdAndIgnoresXrayCorePreferenceInThisFork()
    {
        CreateProfiles(AnyTlsProfile(), AnyTlsProfile("B"));
        Assert.Equal("Same synthetic name", ReadAnyTls("A"));
        Assert.Equal("Same synthetic name", ReadAnyTls("B"));
        Assert.True(SingboxParser.TryReadConfiguration(Bytes(AnyTlsConfiguration), StatisticsEndpoint, out var parsed, out _));
        Assert.Equal("sing-box", parsed!.CoreName);
        Assert.Equal("anytls", parsed.Protocol);
        Assert.False(parsed.Scope!.IsPartial);
    }

    [Theory]
    [InlineData("Password", "Synthetic-Changed")]
    [InlineData("Sni", "changed.synthetic.invalid")]
    [InlineData("Alpn", "http/1.1")]
    [InlineData("AllowInsecure", "true")]
    [InlineData("Fingerprint", "firefox")]
    public void AnyTlsRejectsStaleAuthenticationOrTls(string field, string value)
    {
        var changed = AnyTlsProfile("B"); changed[field] = value;
        CreateProfiles(AnyTlsProfile(), changed);
        Assert.Equal("mapping_changed", Assert.Throws<WindowsDiscovery.DiscoveryException>(() => ReadAnyTls("B")).State);
    }

    [Fact]
    public void AnyTlsUsesExactClientTlsDefaultsAndCertificatePinNormalization()
    {
        Assert.True(MatchesAnyTls(AnyTlsProfile())); // DefFingerprint is not emitted in this branch.
        var explicitFingerprint = AnyTlsProfile(); explicitFingerprint["Fingerprint"] = "chrome";
        var withFingerprint = Change(AnyTlsConfiguration, root => Proxy(root)["tls"]!["utls"] =
            JsonNode.Parse("""{"enabled":true,"fingerprint":"chrome"}"""));
        Assert.True(MatchesAnyTls(explicitFingerprint, withFingerprint));
        Assert.False(MatchesAnyTls(AnyTlsProfile(), withFingerprint));
        var hexPin = AnyTlsProfile(); hexPin["CertSha"] = new string('a', 64);
        Assert.True(MatchesAnyTls(hexPin)); // Hex pins are ignored by the fixed sing-box generator.
        var pin = Convert.ToBase64String(Enumerable.Repeat((byte)42, 32).ToArray());
        var pinned = AnyTlsProfile(); pinned["CertSha"] = pin; pinned["AllowInsecure"] = "true";
        var pinConfiguration = Change(AnyTlsConfiguration, root =>
            Proxy(root)["tls"]!["certificate_public_key_sha256"] = new JsonArray(pin));
        Assert.True(MatchesAnyTls(pinned, pinConfiguration));
        Assert.False(MatchesAnyTls(pinned, AnyTlsConfiguration));
        var fragmented = Change(Gui, root => root["CoreBasicItem"]!["EnableFragment"] = true);
        Assert.False(MatchesAnyTls(AnyTlsProfile(), gui: fragmented));
        var fragmentConfiguration = Change(AnyTlsConfiguration, root =>
        { Proxy(root)["tls"]!["fragment"] = true; Proxy(root)["tls"]!["record_fragment"] = true; });
        Assert.True(MatchesAnyTls(AnyTlsProfile(), fragmentConfiguration, fragmented));
    }

    [Theory]
    [InlineData("Cert", "Synthetic unsupported certificate")]
    [InlineData("EchConfigList", "Synthetic unsupported ECH")]
    public void AnyTlsUnsupportedAdvancedFieldsRemainUnknown(string field, string value)
    {
        var profile = AnyTlsProfile(); profile[field] = value; CreateProfiles(profile);
        Assert.Equal("unsupported_connection", Assert.Throws<WindowsDiscovery.DiscoveryException>(() => ReadAnyTls()).State);
    }

    [Theory]
    [InlineData("""{"listen":"0.0.0.0:12345"}""")]
    [InlineData("""{"listen":"localhost:12345"}""")]
    [InlineData("""{"listen":"127.0.0.1:0"}""")]
    [InlineData("""{"listen":"127.0.0.1:12345","outbounds":["direct"]}""")]
    [InlineData("""{"listen":"127.0.0.1:12345","listen":"127.0.0.1:23456"}""")]
    public void AnyTlsStatisticsFileCannotOverrideConfigurationOrBindPublicly(string file)
    {
        Assert.False(SingboxParser.TryReadConfiguration(Bytes(AnyTlsConfiguration), Bytes(file), out _, out _));
    }

    [Fact]
    public void AnyTlsAdditionalOutboundsDoNotBlockTheVerifiedProxy()
    {
        var extra = Change(AnyTlsConfiguration, root => root["outbounds"]!.AsArray().Add(JsonNode.Parse(
            """{"type":"anytls","tag":"another-proxy","server":"other.synthetic.invalid","server_port":443,"password":"Synthetic"}""")));
        CreateProfiles(AnyTlsProfile());
        Assert.True(SingboxParser.TryReadConfiguration(Bytes(extra), StatisticsEndpoint, out _, out _));
        Assert.Equal("Same synthetic name", ReadAnyTls(configuration: extra));
    }

    [Fact]
    public void AnyTlsGeneratedMultiOutboundShapeReportsOnlyRoutedProxyCoverage()
    {
        // Counts mirror the observed shape; all tags, endpoints and authentication are invented.
        var configuration = Change(AnyTlsConfiguration, root =>
        {
            var outbounds = root["outbounds"]!.AsArray();
            for (var i = 0; i < 30; i++) outbounds.Add(JsonNode.Parse($$"""
                {"type":"anytls","tag":"synthetic-anytls-{{i}}","server":"extra.synthetic.invalid",
                 "server_port":443,"password":"Synthetic"}
                """));
            for (var i = 0; i < 3; i++) outbounds.Add(JsonNode.Parse($$"""
                {"type":"vmess","tag":"synthetic-vmess-{{i}}","server":"extra.synthetic.invalid",
                 "server_port":443,"uuid":"00000000-0000-0000-0000-000000000002"}
                """));
            foreach (var type in new[] { "selector", "urltest" })
                for (var i = 0; i < 2; i++) outbounds.Add(JsonNode.Parse($$"""
                    {"type":"{{type}}","tag":"synthetic-{{type}}-{{i}}","outbounds":["proxy","direct"]}
                    """));
            root["route"]!["rules"] = JsonNode.Parse("""
                [{"domain_suffix":["group.synthetic.invalid"],"outbound":"synthetic-selector-0"},
                 {"domain_suffix":["test.synthetic.invalid"],"outbound":"synthetic-urltest-0"}]
                """);
        });
        CreateProfiles(AnyTlsProfile());
        Assert.Equal("Same synthetic name", ReadAnyTls(configuration: configuration));
        Assert.True(SingboxParser.TryReadConfiguration(Bytes(configuration), StatisticsEndpoint, out var parsed, out _));
        Assert.Equal(new ProxyCollectionScope(37, 4), parsed!.Scope);
        Assert.Equal("routed_proxy", parsed.Scope!.Basis);
        Assert.True(parsed.Scope.IsPartial);
    }

    [Fact]
    public void AnyTlsUncollectedChainDoesNotChangeTheVerifiedProxyIdentity()
    {
        var configuration = Change(AnyTlsConfiguration, root => root["outbounds"]!.AsArray().Add(JsonNode.Parse(
            """
            {"type":"anytls","tag":"synthetic-chain","server":"other.synthetic.invalid","server_port":443,
             "password":"Synthetic","detour":"proxy"}
            """)));
        CreateProfiles(AnyTlsProfile());
        Assert.Equal("Same synthetic name", ReadAnyTls(configuration: configuration));
        Assert.True(SingboxParser.TryReadConfiguration(Bytes(configuration), StatisticsEndpoint, out var parsed, out _));
        Assert.Equal(new ProxyCollectionScope(1, 0), parsed!.Scope);
    }

    [Theory]
    [InlineData("""{"tag":"proxy","type":"direct"}""")]
    [InlineData("""{"tag":"","type":"anytls"}""")]
    [InlineData("""{"tag":"another-proxy"}""")]
    public void AnyTlsCannotVerifyMissingOrConflictingOutboundIdentity(string outbound)
    {
        var configuration = Change(AnyTlsConfiguration, root => root["outbounds"]!.AsArray().Add(JsonNode.Parse(outbound)));
        Assert.False(SingboxParser.TryReadConfiguration(Bytes(configuration), StatisticsEndpoint, out _, out _));
    }

    [Fact]
    public void AnyTlsTargetChainAndUnverifiedSessionOptionsAreRejected()
    {
        var detour = Change(AnyTlsConfiguration, root => Proxy(root)["detour"] = "direct");
        Assert.False(SingboxParser.TryReadConfiguration(Bytes(detour), StatisticsEndpoint, out _, out _));
        var sessionOption = Change(AnyTlsConfiguration, root => Proxy(root)["idle_session_timeout"] = "1s");
        Assert.False(MatchesAnyTls(AnyTlsProfile(), sessionOption));
        var existingApi = Change(AnyTlsConfiguration, root => root["experimental"] =
            JsonNode.Parse("""{"v2ray_api":{"listen":"127.0.0.1:23456"}}"""));
        Assert.False(SingboxParser.TryReadConfiguration(Bytes(existingApi), StatisticsEndpoint, out _, out _));
        var duplicateTls = AnyTlsConfiguration.Replace("\"enabled\":true", "\"enabled\":false,\"enabled\":true");
        Assert.False(SingboxParser.TryReadConfiguration(Bytes(duplicateTls), StatisticsEndpoint, out _, out _));
        Assert.False(MatchesAnyTls(AnyTlsProfile(), duplicateTls));
        var invalidNetwork = AnyTlsProfile(); invalidNetwork["Network"] = "unknown";
        Assert.True(MatchesAnyTls(invalidNetwork)); // Fixed client normalizes unsupported networks to raw.
    }

    [Fact]
    public void CrossCoreSwitchAndReturnCreateBaselinesWithoutRecoveringOldNode()
    {
        Directory.CreateDirectory(directory);
        using var ledger = new Ledger(History);
        var guard = new XrayGenerationGuard();
        var anytls = Snapshot() with { CoreName = "sing-box", ProcessIdentity = "anytls-1" };
        Assert.Equal("baseline", Accept(ledger, guard, anytls, 0, 1000));
        Assert.Equal("recorded", Accept(ledger, guard, anytls, 1, 1020));
        var xray = Snapshot("B", "xray-1");
        Assert.Equal("baseline", Accept(ledger, guard, xray, 2, 5000));
        Assert.Equal("recorded", Accept(ledger, guard, xray, 3, 5030));
        var returned = anytls with { ProcessIdentity = "anytls-2" };
        Assert.Equal("baseline", Accept(ledger, guard, returned, 4, 10000));
        Assert.Equal("recorded", Accept(ledger, guard, returned, 5, 10040));
        Assert.Equal("90", ledger.Query(All).Upload);
    }

    [Fact]
    public void V2RayResponseRequiresBothCountersAndPreservesInt64Precision()
    {
        static byte[] Varint(ulong value)
        {
            var bytes = new List<byte>();
            while (value >= 128) { bytes.Add((byte)((value & 127) | 128)); value >>= 7; }
            bytes.Add((byte)value); return bytes.ToArray();
        }
        static byte[] Stat(string name, ulong? value)
        {
            var text = Encoding.UTF8.GetBytes(name);
            var stat = new List<byte> { 0x0a }; stat.AddRange(Varint((ulong)text.Length)); stat.AddRange(text);
            if (value.HasValue) { stat.Add(0x10); stat.AddRange(Varint(value.Value)); }
            var encoded = new List<byte> { 0x0a }; encoded.AddRange(Varint((ulong)stat.Count)); encoded.AddRange(stat);
            return encoded.ToArray();
        }
        static byte[] Frame(params byte[][] stats)
        {
            var message = stats.SelectMany(x => x).ToArray(); var result = new byte[message.Length + 5];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(1), (uint)message.Length);
            message.CopyTo(result, 5); return result;
        }
        var up = Stat("outbound>>>proxy>>>traffic>>>uplink", long.MaxValue);
        var down = Stat("outbound>>>proxy>>>traffic>>>downlink", null); // Actual zero is omitted in proto3.
        var direct = Stat("outbound>>>direct>>>traffic>>>uplink", 999);
        Assert.True(V2RayStatistics.TryReadResponse(Frame(up, down, direct), out var counters));
        Assert.Equal(new XrayCounters(long.MaxValue, 0), counters);
        Assert.False(V2RayStatistics.TryReadResponse(Frame(up), out _));
        Assert.False(V2RayStatistics.TryReadResponse(Frame(), out _));
        Assert.False(V2RayStatistics.TryReadResponse(Frame(up, up, down), out _));
        Assert.False(V2RayStatistics.TryReadResponse(Frame(Stat("outbound>>>proxy>>>traffic>>>uplink", ulong.MaxValue), down), out _));
        var compressed = Frame(up, down); compressed[0] = 1;
        Assert.False(V2RayStatistics.TryReadResponse(compressed, out _));
        Assert.False(V2RayStatistics.TryReadResponse(Frame(up, down).AsSpan(0, 10), out _));
    }

    [Fact]
    public void V2RayBatchKeepsRouteTagsIndependentAndDoesNotInferMembersOrTotals()
    {
        var frame = StatisticsFrame(
            ("outbound>>>proxy>>>traffic>>>uplink", 101),
            ("outbound>>>proxy>>>traffic>>>downlink", 202),
            ("outbound>>>other>>>traffic>>>uplink", 303),
            ("outbound>>>other>>>traffic>>>downlink", 404),
            ("outbound>>>group>>>traffic>>>uplink", 505),
            ("outbound>>>group>>>traffic>>>downlink", 606),
            ("outbound>>>direct>>>traffic>>>uplink", 999));
        Assert.True(V2RayStatistics.TryReadOutboundsResponse(frame, ["proxy", "other", "group"], out var result));
        Assert.Equal("routed_outbound", result!.Basis);
        Assert.Equal(3, result.Counters.Count);
        Assert.Equal(new XrayCounters(101, 202), result.Counters["proxy"]);
        Assert.Equal(new XrayCounters(303, 404), result.Counters["other"]);
        Assert.Equal(new XrayCounters(505, 606), result.Counters["group"]);
        Assert.Empty(result.UnavailableOutbounds);
        Assert.False(result.Counters.ContainsKey("direct"));
        Assert.True(V2RayStatistics.TryReadResponse(frame, out var legacy));
        Assert.Equal(result.Counters["proxy"], legacy);
    }

    [Fact]
    public void V2RayBatchMissingOrDuplicatePairDoesNotDiscardIndependentCompletePair()
    {
        var frame = StatisticsFrame(
            ("outbound>>>proxy>>>traffic>>>uplink", null),
            ("outbound>>>proxy>>>traffic>>>downlink", long.MaxValue),
            ("outbound>>>missing-down>>>traffic>>>uplink", 123),
            ("outbound>>>duplicate>>>traffic>>>uplink", 123),
            ("outbound>>>duplicate>>>traffic>>>uplink", 123),
            ("outbound>>>duplicate>>>traffic>>>downlink", 456),
            ("outbound>>>invalid>>>traffic>>>uplink", ulong.MaxValue),
            ("outbound>>>invalid>>>traffic>>>downlink", 1),
            ("outbound>>>Proxy>>>traffic>>>uplink", 789),
            ("outbound>>>Proxy>>>traffic>>>downlink", null));
        Assert.True(V2RayStatistics.TryReadOutboundsResponse(frame,
            ["proxy", "missing-down", "missing-both", "duplicate", "invalid", "Proxy"], out var result));
        Assert.Equal(2, result!.Counters.Count);
        Assert.Equal(new XrayCounters(0, long.MaxValue), result.Counters["proxy"]);
        Assert.Equal(new XrayCounters(789, 0), result.Counters["Proxy"]);
        Assert.Equal("missing_counter", result.UnavailableOutbounds["missing-down"]);
        Assert.Equal("missing_counter", result.UnavailableOutbounds["missing-both"]);
        Assert.Equal("duplicate_counter", result.UnavailableOutbounds["duplicate"]);
        Assert.Equal("invalid_counter", result.UnavailableOutbounds["invalid"]);
        Assert.True(V2RayStatistics.TryReadResponse(frame, out _));
    }

    [Fact]
    public void V2RayBatchDoesNotInventIdleZeroForAbsentCounters()
    {
        Assert.True(V2RayStatistics.TryReadOutboundsResponse(StatisticsFrame(), ["proxy", "other"], out var result));
        Assert.Empty(result!.Counters);
        Assert.Equal(2, result.UnavailableOutbounds.Count);
        Assert.False(V2RayStatistics.TryReadResponse(StatisticsFrame(), out _));
    }

    [Fact]
    public void V2RayBatchRejectsMalformedWireWithoutReturningAPartialSnapshot()
    {
        var valid = StatisticsFrame(
            ("outbound>>>proxy>>>traffic>>>uplink", 1),
            ("outbound>>>proxy>>>traffic>>>downlink", 2));
        var compressed = valid.ToArray(); compressed[0] = 1;
        var unknownWire = valid.ToArray(); unknownWire[5] = 0x0f;
        foreach (var frame in new[] { compressed, valid[..^1], unknownWire, new byte[65537] })
        {
            Assert.False(V2RayStatistics.TryReadOutboundsResponse(frame, ["proxy", "other"], out var result));
            Assert.Null(result);
        }
    }

    [Fact]
    public void V2RayBatchRejectsAmbiguousRequestedTags()
    {
        foreach (var tags in new[] { Array.Empty<string>(), new[] { "" }, new[] { "proxy", "proxy" }, new[] { "a>>>b" } })
            Assert.Throws<ArgumentException>(() => V2RayStatistics.TryReadOutboundsResponse(StatisticsFrame(), tags, out _));
    }

    [Fact]
    public async Task V2RayBatchUsesOneBoundedReadOnlyRpcAndKeepsUnavailableTags()
    {
        var frame = StatisticsFrame(
            ("outbound>>>proxy>>>traffic>>>uplink", 10),
            ("outbound>>>proxy>>>traffic>>>downlink", 20),
            ("outbound>>>other>>>traffic>>>uplink", 30),
            ("outbound>>>other>>>traffic>>>downlink", 40));
        using var handler = new StatisticsHandler(frame);
        using var client = new HttpClient(handler);
        var result = await V2RayStatistics.ReadOutboundsAsync(client, new Uri("http://127.0.0.1:10814/"),
            ["proxy", "other", "unregistered"], CancellationToken.None);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(new XrayCounters(10, 20), result!.Counters["proxy"]);
        Assert.Equal(new XrayCounters(30, 40), result.Counters["other"]);
        Assert.Equal("missing_counter", result.UnavailableOutbounds["unregistered"]);
    }

    [Fact]
    public async Task V2RayBatchRpcFailureNeverProducesAvailableCounters()
    {
        using var handler = new StatisticsHandler(StatisticsFrame(), status: "13");
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => V2RayStatistics.ReadOutboundsAsync(client,
            new Uri("http://127.0.0.1:10814/"), ["proxy", "other"], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => V2RayStatistics.ReadOutboundsAsync(client,
            new Uri("http://localhost:10814/"), ["proxy"], CancellationToken.None));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public void V2RayMembersKeepMemberRouteAndAggregateDimensionsSeparate()
    {
        var frame = StatisticsFrame(
            ("monitor>>>member>>>business>>>traffic>>>uplink", 11),
            ("monitor>>>member>>>business>>>traffic>>>downlink", 12),
            ("monitor>>>member>>>proxy>>>traffic>>>uplink", long.MaxValue),
            ("monitor>>>member>>>proxy>>>traffic>>>downlink", null),
            ("monitor>>>business>>>traffic>>>uplink", 21),
            ("monitor>>>business>>>traffic>>>downlink", 22),
            ("monitor>>>excluded>>>traffic>>>uplink", 31),
            ("monitor>>>excluded>>>traffic>>>downlink", 32),
            ("monitor>>>unknown>>>traffic>>>uplink", null),
            ("monitor>>>unknown>>>traffic>>>downlink", 42),
            ("outbound>>>proxy>>>traffic>>>uplink", 901),
            ("outbound>>>proxy>>>traffic>>>downlink", 902));
        Assert.True(V2RayStatistics.TryReadMembersResponse(frame, ["business", "proxy"], out var result));
        Assert.Equal("actual_member", result!.Basis);
        Assert.Equal(new XrayCounters(11, 12), result.Members["business"]);
        Assert.Equal(new XrayCounters(long.MaxValue, 0), result.Members["proxy"]);
        Assert.Equal(new XrayCounters(21, 22), result.Totals["business"]);
        Assert.Equal(new XrayCounters(31, 32), result.Totals["excluded"]);
        Assert.Equal(new XrayCounters(0, 42), result.Totals["unknown"]);
        Assert.Empty(result.UnavailableMembers);
        Assert.Empty(result.UnavailableTotals);
        Assert.True(V2RayStatistics.TryReadResponse(frame, out var legacy));
        Assert.Equal(new XrayCounters(901, 902), legacy);
    }

    [Fact]
    public void V2RayMembersPreserveIndependentPairsWhenMembersOrTotalsAreUnavailable()
    {
        var frame = StatisticsFrame(
            ("monitor>>>member>>>good>>>traffic>>>uplink", 1),
            ("monitor>>>member>>>good>>>traffic>>>downlink", 2),
            ("monitor>>>member>>>missing>>>traffic>>>uplink", 3),
            ("monitor>>>member>>>duplicate>>>traffic>>>uplink", 4),
            ("monitor>>>member>>>duplicate>>>traffic>>>uplink", 4),
            ("monitor>>>member>>>duplicate>>>traffic>>>downlink", 5),
            ("monitor>>>member>>>invalid>>>traffic>>>uplink", ulong.MaxValue),
            ("monitor>>>member>>>invalid>>>traffic>>>downlink", 6),
            ("monitor>>>business>>>traffic>>>uplink", 7),
            ("monitor>>>business>>>traffic>>>downlink", 8),
            ("monitor>>>excluded>>>traffic>>>uplink", 9),
            ("monitor>>>excluded>>>traffic>>>uplink", 9),
            ("monitor>>>excluded>>>traffic>>>downlink", 10),
            ("monitor>>>unknown>>>traffic>>>uplink", ulong.MaxValue),
            ("monitor>>>unknown>>>traffic>>>downlink", 11));
        Assert.True(V2RayStatistics.TryReadMembersResponse(frame,
            ["good", "missing", "absent", "duplicate", "invalid"], out var result));
        Assert.Equal(new XrayCounters(1, 2), Assert.Single(result!.Members).Value);
        Assert.Equal("missing_counter", result.UnavailableMembers["missing"]);
        Assert.Equal("missing_counter", result.UnavailableMembers["absent"]);
        Assert.Equal("duplicate_counter", result.UnavailableMembers["duplicate"]);
        Assert.Equal("invalid_counter", result.UnavailableMembers["invalid"]);
        Assert.Equal(new XrayCounters(7, 8), Assert.Single(result.Totals).Value);
        Assert.Equal("duplicate_counter", result.UnavailableTotals["excluded"]);
        Assert.Equal("invalid_counter", result.UnavailableTotals["unknown"]);
    }

    [Fact]
    public void V2RayMembersDoNotTreatTest2RouteCountersAsMemberEvidenceOrIdleZero()
    {
        var routeOnly = StatisticsFrame(
            ("outbound>>>proxy>>>traffic>>>uplink", 100),
            ("outbound>>>proxy>>>traffic>>>downlink", 200));
        Assert.True(V2RayStatistics.TryReadMembersResponse(routeOnly, ["proxy"], out var result));
        Assert.Empty(result!.Members);
        Assert.Empty(result.Totals);
        Assert.Equal("missing_counter", result.UnavailableMembers["proxy"]);
        Assert.Equal(3, result.UnavailableTotals.Count);
        Assert.All(result.UnavailableTotals.Values, value => Assert.Equal("missing_counter", value));
    }

    [Fact]
    public void V2RayMembersRejectMalformedWireWithoutPartialResults()
    {
        var frame = StatisticsFrame(
            ("monitor>>>member>>>proxy>>>traffic>>>uplink", 1),
            ("monitor>>>member>>>proxy>>>traffic>>>downlink", 2));
        var compressed = frame.ToArray(); compressed[0] = 1;
        var unknownWire = frame.ToArray(); unknownWire[5] = 0x0f;
        foreach (var invalid in new[] { compressed, frame[..^1], unknownWire, new byte[65537] })
        {
            Assert.False(V2RayStatistics.TryReadMembersResponse(invalid, ["proxy"], out var result));
            Assert.Null(result);
        }
    }

    [Fact]
    public void V2RayMembersAllowTotalsOnlyAndRejectAmbiguousTags()
    {
        Assert.True(V2RayStatistics.TryReadMembersResponse(StatisticsFrame(), [], out var totalsOnly));
        Assert.Empty(totalsOnly!.Members);
        Assert.Empty(totalsOnly.UnavailableMembers);
        Assert.Equal(3, totalsOnly.UnavailableTotals.Count);
        foreach (var tags in new[] { new[] { "" }, new[] { "proxy", "proxy" }, new[] { "a>>>b" } })
            Assert.Throws<ArgumentException>(() => V2RayStatistics.TryReadMembersResponse(StatisticsFrame(), tags, out _));
        Assert.True(V2RayStatistics.TryReadMembersResponse(StatisticsFrame(), ["proxy", "Proxy"], out var distinct));
        Assert.Equal(2, distinct!.UnavailableMembers.Count);
    }

    [Fact]
    public async Task V2RayMembersUseOneReadOnlyRpcAndKeepRequestFailuresUnavailable()
    {
        using var handler = new StatisticsHandler(StatisticsFrame(
            ("monitor>>>member>>>proxy>>>traffic>>>uplink", 10),
            ("monitor>>>member>>>proxy>>>traffic>>>downlink", 20)));
        using var client = new HttpClient(handler);
        var result = await V2RayStatistics.ReadMembersAsync(client, new Uri("http://127.0.0.1:10814/"),
            ["proxy", "other"], CancellationToken.None);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(new XrayCounters(10, 20), result!.Members["proxy"]);
        Assert.Equal("missing_counter", result.UnavailableMembers["other"]);
        await Assert.ThrowsAsync<ArgumentException>(() => V2RayStatistics.ReadMembersAsync(client,
            new Uri("http://localhost:10814/"), ["proxy"], CancellationToken.None));
        Assert.Equal(1, handler.Requests);
        using var failedHandler = new StatisticsHandler(StatisticsFrame(), status: "13");
        using var failedClient = new HttpClient(failedHandler);
        await Assert.ThrowsAsync<HttpRequestException>(() => V2RayStatistics.ReadMembersAsync(failedClient,
            new Uri("http://127.0.0.1:10814/"), ["proxy"], CancellationToken.None));
    }

    [Fact]
    public void V2RayProbesAreSeparateFromBusinessAndMissingSupportIsUnavailable()
    {
        var frame = StatisticsFrame(
            ("monitor>>>probe>>>urltest>>>member>>>proxy>>>traffic>>>uplink", 11),
            ("monitor>>>probe>>>urltest>>>member>>>proxy>>>traffic>>>downlink", 12),
            ("monitor>>>probe>>>urltest>>>business>>>traffic>>>uplink", 11),
            ("monitor>>>probe>>>urltest>>>business>>>traffic>>>downlink", 12),
            ("monitor>>>business>>>traffic>>>uplink", 901),
            ("monitor>>>business>>>traffic>>>downlink", 902));
        Assert.True(V2RayStatistics.TryReadProbesResponse(frame, ["proxy", "idle"], out var probes));
        Assert.Equal("urltest", probes!.ProbeKind);
        Assert.Equal(new XrayCounters(11, 12), Assert.Single(probes.Members).Value);
        Assert.Equal(new XrayCounters(11, 12), Assert.Single(probes.Totals).Value);
        Assert.Equal("missing_counter", probes.UnavailableMembers["idle"]);
        Assert.Equal(2, probes.UnavailableTotals.Count);
        Assert.True(V2RayStatistics.TryReadMembersResponse(frame, ["proxy"], out var business));
        Assert.Empty(business!.Members);
        Assert.Equal(new XrayCounters(901, 902), business.Totals["business"]);
        Assert.True(V2RayStatistics.TryReadProbesResponse(StatisticsFrame(
            ("monitor>>>member>>>proxy>>>traffic>>>uplink", 100),
            ("monitor>>>member>>>proxy>>>traffic>>>downlink", 200)), ["proxy"], out var oldCore));
        Assert.Empty(oldCore!.Members);
        Assert.Empty(oldCore.Totals);
        Assert.Equal("missing_counter", oldCore.UnavailableMembers["proxy"]);
        Assert.All(oldCore.UnavailableTotals.Values, value => Assert.Equal("missing_counter", value));
    }

    [Fact]
    public void V2RayProbesPreserveValidPairsAndRejectMalformedFrames()
    {
        const string prefix = "monitor>>>probe>>>urltest>>>";
        var frame = StatisticsFrame(
            (prefix + "member>>>good>>>traffic>>>uplink", (ulong)long.MaxValue),
            (prefix + "member>>>good>>>traffic>>>downlink", 0),
            (prefix + "member>>>duplicate>>>traffic>>>uplink", 1),
            (prefix + "member>>>duplicate>>>traffic>>>uplink", 1),
            (prefix + "member>>>duplicate>>>traffic>>>downlink", 2),
            (prefix + "member>>>invalid>>>traffic>>>uplink", ulong.MaxValue),
            (prefix + "member>>>invalid>>>traffic>>>downlink", 2));
        Assert.True(V2RayStatistics.TryReadProbesResponse(frame, ["good", "duplicate", "invalid"], out var result));
        Assert.Equal(new XrayCounters(long.MaxValue, 0), Assert.Single(result!.Members).Value);
        Assert.Equal("duplicate_counter", result.UnavailableMembers["duplicate"]);
        Assert.Equal("invalid_counter", result.UnavailableMembers["invalid"]);
        Assert.False(V2RayStatistics.TryReadProbesResponse(frame[..^1], ["good"], out var malformed));
        Assert.Null(malformed);
        Assert.Throws<ArgumentException>(() => V2RayStatistics.TryReadProbesResponse(frame, ["a>>>b"], out _));
    }

    [Fact]
    public async Task V2RayProbesUseOneBoundedReadOnlyRpc()
    {
        using var handler = new StatisticsHandler(StatisticsFrame(
            ("monitor>>>probe>>>urltest>>>member>>>proxy>>>traffic>>>uplink", 10),
            ("monitor>>>probe>>>urltest>>>member>>>proxy>>>traffic>>>downlink", 20)));
        using var client = new HttpClient(handler);
        var result = await V2RayStatistics.ReadProbesAsync(client, new Uri("http://127.0.0.1:10814/"),
            ["proxy"], CancellationToken.None);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(new XrayCounters(10, 20), result!.Members["proxy"]);
        await Assert.ThrowsAsync<ArgumentException>(() => V2RayStatistics.ReadProbesAsync(client,
            new Uri("http://localhost:10814/"), ["proxy"], CancellationToken.None));
        Assert.Equal(1, handler.Requests);
    }

    private const string MemberConfiguration = """
        {"inbounds":[{"type":"mixed","tag":"in"}],"outbounds":[
        {"tag":"proxy","type":"anytls","server":"node.synthetic.invalid","password":"Synthetic-Credential"},
        {"tag":"other","type":"anytls","server":"node.synthetic.invalid","password":"Synthetic-Credential"},
        {"tag":"direct","type":"direct"},
        {"tag":"group","type":"selector","outbounds":["proxy","other","direct"],"default":"other"},
        {"tag":"auto","type":"urltest","outbounds":["proxy","other"]},
        {"tag":"chain","type":"socks","detour":"proxy"},
        {"tag":"unsupported","type":"synthetic-unsupported"},
        {"tag":"invalid>>>tag","type":"socks"}]}
        """;

    private static XraySnapshot MemberSnapshot(string configuration = MemberConfiguration)
        => Snapshot(generation: XrayParser.Hash(Bytes(configuration)) + ":synthetic-config-and-sidecar-generation")
            with { CoreName = "sing-box" };

    [Fact]
    public void MemberFriendlyNameUsesExplicitGroupMembershipWithoutChangingLedgerIdentity()
    {
        const string tag = "12345-proxy-US-1x-1-Synthetic A";
        var config = MemberConfiguration.Replace("\"other\"", JsonSerializer.Serialize(tag), StringComparison.Ordinal)
            .Replace("\"group\"", "\"12345-proxy-US-1x\"", StringComparison.Ordinal);
        var snapshot = MemberSnapshot(config);
        Assert.True(SingboxParser.TryReadMemberCatalog(snapshot, Bytes(config), out var catalog, out _));
        var member = catalog!.Candidates[tag];
        Assert.Equal("【US-1x】 1-Synthetic A", member.DisplayName);
        Assert.Equal(tag, member.OutboundTag);
        Assert.Null(member.IndexId);
        Assert.Null(catalog.Candidates["proxy"].DisplayName);
        var counters = ScopeCounters(10, 20);
        counters = counters with { Members = new Dictionary<string, XrayCounters>
            { ["proxy"] = new(10, 0), [tag] = new(20, 0) } };
        var batch = ScopeCollector.Project(new(snapshot, catalog, ["proxy", tag]), counters, Start);
        using var ledger = ScopeLedger.CreateNew(History);
        ScopeCollector.Commit(ledger, batch with
        {
            Readings = batch.Readings.Select(x => x.Scope.OutboundTag == tag
                ? x with { Scope = x.Scope with { Name = tag } } : x).ToArray()
        });
        var id = ledger.GetScopes("actual_member").Single(x => x.Scope.OutboundTag == tag).Id;
        var commit = ScopeCollector.Commit(ledger, batch with { At = Start.AddSeconds(1) });
        var renamed = ledger.GetScopes("actual_member").Single(x => x.Id == id);
        Assert.Equal(member.DisplayName, renamed.Scope.Name);
        Assert.Equal(tag, renamed.Scope.OutboundTag);
        Assert.Null(renamed.Scope.NodeId);
        Assert.Equal("recorded", commit.Scopes[id]);
        Assert.DoesNotContain(ledger.Query(new("actual_member", Start.AddSeconds(-1), Start.AddMinutes(1), "UTC")).Gaps,
            x => x.Reason == "identity_change");
    }

    [Fact]
    public void MemberSharedBySelectorsKeepsOriginalTagInsteadOfGuessingAGroup()
    {
        var config = MemberConfiguration.Replace("\"type\":\"urltest\"", "\"type\":\"selector\"", StringComparison.Ordinal);
        Assert.True(SingboxParser.TryReadMemberCatalog(MemberSnapshot(config), Bytes(config), out var catalog, out _));
        Assert.Null(catalog!.Candidates["other"].DisplayName);
        Assert.Equal("other", catalog.Candidates["other"].OutboundTag);
        Assert.Null(catalog.Candidates["other"].IndexId);
    }

    [Fact]
    public void SingboxMemberCatalogPreservesIndependentIdentityAndOnlyVerifiedProxyIndexId()
    {
        var snapshot = MemberSnapshot();
        Assert.True(SingboxParser.TryReadMemberCatalog(snapshot, Bytes(MemberConfiguration), out var catalog, out var reason));
        Assert.Equal("ready", reason);
        Assert.Equal(snapshot.InstanceId, catalog!.InstanceId);
        Assert.Equal(snapshot.Generation, catalog.Generation);
        Assert.Equal(snapshot.ProcessIdentity, catalog.ProcessIdentity);
        Assert.Equal(2, catalog.Candidates.Count);
        Assert.Equal(new SingboxMemberIdentity("proxy", "anytls", "A"), catalog.Candidates["proxy"]);
        Assert.Null(catalog.Candidates["other"].IndexId); // Even an identical endpoint/credential is not another IndexId proof.
        Assert.Equal("group_not_member", catalog.UnavailableOutbounds["group"]);
        Assert.Equal("group_not_member", catalog.UnavailableOutbounds["auto"]);
        Assert.Equal("excluded_outbound", catalog.UnavailableOutbounds["direct"]);
        Assert.Equal("dependent_outbound", catalog.UnavailableOutbounds["chain"]);
        Assert.Equal("unsupported_member_type", catalog.UnavailableOutbounds["unsupported"]);
        Assert.Equal("invalid_statistics_tag", catalog.UnavailableOutbounds["invalid>>>tag"]);
        var serialized = JsonSerializer.Serialize(catalog);
        Assert.DoesNotContain("Synthetic-Credential", serialized);
        Assert.DoesNotContain("node.synthetic.invalid", serialized);
        Assert.DoesNotContain("Same synthetic name", serialized);
    }

    [Fact]
    public void SingboxMemberCatalogRequiresMatchingConfigurationAndCore()
    {
        foreach (var snapshot in new[] { MemberSnapshot() with { CoreName = "Xray" },
            MemberSnapshot() with { Generation = "unrelated-generation" }, MemberSnapshot() with { InstanceId = "" } })
        {
            Assert.False(SingboxParser.TryReadMemberCatalog(snapshot, Bytes(MemberConfiguration), out var catalog, out var reason));
            Assert.Null(catalog);
            Assert.Equal("snapshot_changed", reason);
        }
        var changed = MemberConfiguration.Replace("\"default\":\"other\"", "\"default\":\"proxy\"");
        Assert.False(SingboxParser.TryReadMemberCatalog(MemberSnapshot(), Bytes(changed), out _, out _));
        var next = MemberSnapshot(changed) with { ProcessIdentity = "core-2", NodeId = "B" };
        Assert.True(SingboxParser.TryReadMemberCatalog(next, Bytes(changed), out var catalog2, out _));
        Assert.Equal("core-2", catalog2!.ProcessIdentity);
        Assert.Equal("B", catalog2.Candidates["proxy"].IndexId);
        Assert.Null(catalog2.Candidates["other"].IndexId); // Group selection never supplies a member's subscription identity.
    }

    [Fact]
    public void SingboxMemberCatalogRejectsAmbiguousOrOutOfScopeConfiguration()
    {
        foreach (var config in new[] {
            MemberConfiguration.Replace("\"tag\":\"other\"", "\"tag\":\"proxy\""),
            MemberConfiguration.Replace("\"tag\":\"other\"", "\"tag\":\"other\",\"tag\":\"other\""),
            MemberConfiguration.Replace("\"type\":\"mixed\"", "\"type\":\"tun\""),
            MemberConfiguration.Replace("\"inbounds\":", "\"endpoints\":[{\"type\":\"wireguard\"}],\"inbounds\":"),
            "{}", "not-json" })
        {
            Assert.False(SingboxParser.TryReadMemberCatalog(MemberSnapshot(config), Bytes(config), out var catalog, out var reason));
            Assert.Null(catalog);
            Assert.Equal("unsupported_configuration", reason);
        }
    }

    private static byte[] StatisticsFrame(params (string Name, ulong? Value)[] stats)
    {
        static void Varint(List<byte> bytes, ulong value)
        {
            while (value >= 128) { bytes.Add((byte)((value & 127) | 128)); value >>= 7; }
            bytes.Add((byte)value);
        }
        var message = new List<byte>();
        foreach (var (name, value) in stats)
        {
            var text = Encoding.UTF8.GetBytes(name);
            var stat = new List<byte> { 0x0a }; Varint(stat, (ulong)text.Length); stat.AddRange(text);
            if (value.HasValue) { stat.Add(0x10); Varint(stat, value.Value); }
            message.Add(0x0a); Varint(message, (ulong)stat.Count); message.AddRange(stat);
        }
        var frame = new byte[message.Count + 5];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1), (uint)message.Count);
        message.CopyTo(frame, 5);
        return frame;
    }

    private sealed class StatisticsHandler(byte[] frame, string status = "0") : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(V2RayStatistics.QueryPath, request.RequestUri!.AbsolutePath);
            Assert.Equal(HttpVersion.Version20, request.Version);
            Assert.Equal(HttpVersionPolicy.RequestVersionExact, request.VersionPolicy);
            Assert.Equal("application/grpc", request.Content!.Headers.ContentType!.MediaType);
            Assert.Equal(new byte[] { 0, 0, 0, 0, 2, 0x10, 0 }, await request.Content.ReadAsByteArrayAsync(cancellationToken));
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Version = HttpVersion.Version20,
                Content = new ByteArrayContent(frame)
            };
            response.Content.Headers.ContentType = new("application/grpc");
            response.TrailingHeaders.Add("grpc-status", status);
            return response;
        }
    }

    [Theory]
    [InlineData("one")]
    [InlineData("another-subscription")]
    public void SameAndCrossSubscriptionDuplicatesUseOnlySelectedIndexId(string subscription)
    {
        var other = Profile("B", subscription);
        var unrelated = Profile("C"); unrelated["ProtoExtra"] = "not valid JSON";
        CreateProfiles(Profile(), other, unrelated);
        Assert.Equal("Same synthetic name", Read("A"));
        Assert.Equal("Same synthetic name", Read("B"));
        Assert.Equal("unknown_node", Assert.Throws<WindowsDiscovery.DiscoveryException>(() => Read("missing")).State);
    }

    [Fact]
    public void DuplicatedIndexIdItselfRemainsAmbiguous()
    {
        CreateProfiles(Profile(), Profile());
        Assert.Equal("ambiguous_node", Assert.Throws<WindowsDiscovery.DiscoveryException>(() => Read("A")).State);
    }

    [Theory]
    [InlineData("ProtoExtra", "{\"VlessEncryption\":\"changed-encryption\"}")]
    [InlineData("ProtoExtra", "{\"VlessEncryption\":\"none\",\"Flow\":\"xtls-rprx-vision\"}")]
    [InlineData("TransportExtra", "{\"Host\":\"front.synthetic.invalid\",\"Path\":\"/synthetic-b\",\"XhttpMode\":\"auto\"}")]
    [InlineData("Sni", "other.synthetic.invalid")]
    [InlineData("Fingerprint", "firefox")]
    [InlineData("AllowInsecure", "true")]
    [InlineData("Alpn", "http/1.1")]
    [InlineData("Password", "00000000-0000-0000-0000-000000000002")]
    public void SameEndpointSelectionDoesNotAcceptStaleMeaningfulConfiguration(string field, string value)
    {
        var changed = Profile("B"); changed[field] = value;
        CreateProfiles(Profile(), changed);
        Assert.Equal("Same synthetic name", Read("A"));
        Assert.Equal("mapping_changed", Assert.Throws<WindowsDiscovery.DiscoveryException>(() => Read("B")).State);
    }

    [Fact]
    public void XhttpExtrasAreStructuralAndGlobalTlsFallbackIsChecked()
    {
        Assert.True(Matches(Profile())); // Extra object key order differs between sources.
        var changedExtra = Configuration.Replace("\"noGRPCHeader\":true", "\"noGRPCHeader\":false");
        Assert.False(Matches(Profile(), changedExtra));
        Assert.False(Matches(Profile(), gui: Gui.Replace("\"chrome\"", "\"firefox\"")));
        var explicitFingerprint = Profile(); explicitFingerprint["Fingerprint"] = "chrome";
        Assert.True(Matches(explicitFingerprint, gui: Gui.Replace("\"chrome\"", "\"firefox\"")));
        Assert.False(Matches(Profile(), Configuration.Replace("\"path\":\"/synthetic-a\"", "\"path\":\"/synthetic-a\",\"path\":\"/synthetic-a\"")));
    }

    [Fact]
    public void BoundInterfaceWithXhttpDownloadSettingsIsExplicitlyOutsideTheProjection()
    {
        var config = Change(Configuration, root => Proxy(root)["streamSettings"]!["xhttpSettings"]!["extra"]!["downloadSettings"]
            = new JsonObject { ["sockopt"] = new JsonObject { ["interface"] = "synthetic-adapter" } });
        var gui = Change(Gui, root => root["CoreBasicItem"]!["BindInterface"] = "synthetic-adapter");
        Assert.False(XrayParser.MatchesSelectedConnection(Profile(), Bytes(gui), Bytes(config), out var reason));
        Assert.Equal("unsupported_connection", reason);
    }

    [Fact]
    public void VlessMissingEncryptionIsNotInventedAsNone()
    {
        var profile = Profile(); profile["ProtoExtra"] = "{}";
        Assert.False(Matches(profile));
        Assert.True(Matches(profile, Change(Configuration, root => Proxy(root)["settings"]!.AsObject().Remove("encryption"))));
    }

    [Fact]
    public void VmessRawDefaultsAndWsUserAgentFollowPinnedGeneration()
    {
        var profile = Profile();
        profile["ConfigType"] = "1"; profile["Network"] = " ws "; profile["StreamSecurity"] = "";
        profile["ProtoExtra"] = "{\"AlterId\":\"7\",\"VmessSecurity\":\"unknown\"}";
        profile["TransportExtra"] = "{}";
        var config = Change(Configuration, root =>
        {
            var proxy = Proxy(root); proxy["protocol"] = "vmess";
            proxy["settings"]!.AsObject().Remove("encryption");
            proxy["settings"]!["security"] = "auto"; proxy["settings"]!["alterId"] = 7;
            proxy["streamSettings"] = JsonNode.Parse("{\"network\":\"raw\"}");
        });
        CreateProfiles(profile);
        Assert.Equal("Same synthetic name", Read("A", config));
        Assert.False(Matches(profile, config.Replace("\"alterId\":7", "\"alterId\":8")));
        profile["Network"] = "ws"; profile["TransportExtra"] = "{\"Host\":\" ws.synthetic.invalid \",\"Path\":\" /socket \"}";
        config = Change(config, root => Proxy(root)["streamSettings"] = JsonNode.Parse("""
            {"network":"ws","wsSettings":{"host":"ws.synthetic.invalid","path":"/socket","headers":{"User-Agent":"Synthetic-UA"}}}
            """));
        Assert.True(Matches(profile, config));
        Assert.False(Matches(profile, config, Gui.Replace("Synthetic-UA", "Different-UA")));
    }

    private static (Dictionary<string, string?> Profile, string Config) Hysteria()
    {
        var profile = Profile(); profile["ConfigType"] = "7"; profile["Password"] = "Synthetic-Hysteria-Auth";
        profile["Network"] = ""; profile["TransportExtra"] = "{}"; profile["ProtoExtra"] = "{}";
        profile["Sni"] = "hy.synthetic.invalid";
        var config = Change(Configuration, root =>
        {
            var proxy = Proxy(root); proxy["protocol"] = "hysteria";
            proxy["settings"] = JsonNode.Parse("{\"address\":\"node.synthetic.invalid\",\"port\":443,\"version\":2}");
            proxy["streamSettings"] = JsonNode.Parse("""
                {"network":"hysteria","security":"tls","tlsSettings":{"serverName":"hy.synthetic.invalid","fingerprint":"chrome","alpn":["h2"]},
                 "hysteriaSettings":{"version":2,"auth":"Synthetic-Hysteria-Auth"},
                 "finalmask":{"quicParams":{"congestion":"brutal","brutalUp":"100mbps","brutalDown":"100mbps"},"udp":[]}}
                """);
        });
        return (profile, config);
    }

    [Fact]
    public void OrdinaryHysteria2RetainsAuthTlsAndGuiDefaults()
    {
        var (profile, config) = Hysteria(); CreateProfiles(profile);
        Assert.Equal("Same synthetic name", Read("A", config));
        Assert.False(Matches(profile, config.Replace("100mbps", "101mbps")));
        Assert.Equal("mapping_changed", Assert.Throws<WindowsDiscovery.DiscoveryException>(() => Read("A", config.Replace("Synthetic-Hysteria-Auth", "Other-Auth"))).State);
        var absentDefaults = Change(Gui, root => root.Remove("HysteriaItem"));
        Assert.True(Matches(profile, config, absentDefaults));
        var emptyDefaults = Change(Gui, root => root["HysteriaItem"] = new JsonObject());
        Assert.False(Matches(profile, config, emptyDefaults));
    }

    [Fact]
    public void HysteriaRealmRequiresAnExplicitlySupportedNormalization()
    {
        var (profile, config) = Hysteria(); profile["ProtoExtra"] = "{\"Hy2RealmUrl\":\"synthetic-realm-value\"}";
        Assert.False(XrayParser.MatchesSelectedConnection(profile, Bytes(Gui), Bytes(config), out var reason));
        Assert.Equal("unsupported_connection", reason);
    }

    [Theory]
    [InlineData(5, "", "5")]
    [InlineData(4, "", "30")]
    [InlineData(4, "7-9", "7-9")]
    public void HysteriaPortHoppingUsesExactProfileAndGlobalFallback(int configured, string explicitInterval, string expected)
    {
        var (profile, config) = Hysteria();
        profile["ProtoExtra"] = JsonSerializer.Serialize(new { Ports = "4000:5000", HopInterval = explicitInterval, SalamanderPass = "  Synthetic-Mask  ", GeckoMinPacketSize = "9", GeckoMaxPacketSize = "19" });
        config = Change(config, root =>
        {
            var mask = Proxy(root)["streamSettings"]!["finalmask"]!;
            mask["quicParams"]!["udpHop"] = new JsonObject { ["ports"] = "4000-5000", ["interval"] = expected };
            mask["udp"] = JsonNode.Parse("[{\"type\":\"salamander\",\"settings\":{\"password\":\"Synthetic-Mask\",\"packetSize\":\"9-19\"}}]");
        });
        var gui = Change(Gui, root => root["HysteriaItem"]!["HopInterval"] = configured);
        Assert.True(Matches(profile, config, gui));
        Assert.False(Matches(profile, config.Replace("4000-5000", "5001-6000"), gui));
        Assert.False(Matches(profile, config.Replace("Synthetic-Mask", "Other-Mask"), gui));
    }

    [Theory]
    [InlineData("Network", "kcp")]
    [InlineData("Cert", "synthetic nonblank PEM")]
    [InlineData("Finalmask", "{\"tcp\":[]}")]
    public void AdvancedUncomparedConnectionBranchesAreExplicitlyUnsupported(string field, string value)
    {
        var profile = Profile(); profile[field] = value;
        Assert.False(XrayParser.MatchesSelectedConnection(profile, Bytes(Gui), Bytes(Configuration), out var reason));
        Assert.Equal("unsupported_connection", reason);
    }

    [Fact]
    public void FullyIdenticalAToBToAKeepsIndependentHistoryAndFreshBaselines()
    {
        CreateProfiles(Profile(), Profile("B"));
        using var ledger = new Ledger(History); var guard = new XrayGenerationGuard();
        Assert.Equal(Read("A"), Read("B"));
        Assert.Equal("baseline", Accept(ledger, guard, Snapshot(), 0, 1000));
        Assert.Equal("recorded", Accept(ledger, guard, Snapshot(), 1, 1010));
        Assert.Equal("baseline", Accept(ledger, guard, Snapshot("B"), 2, 9000));
        Assert.Equal("recorded", Accept(ledger, guard, Snapshot("B"), 3, 9020));
        Assert.Equal("baseline", Accept(ledger, guard, Snapshot(), 4, 15000));
        Assert.Equal("recorded", Accept(ledger, guard, Snapshot() with { NodeName = "Renamed A" }, 5, 15003));
        var result = ledger.Query(All);
        Assert.Equal("33", result.Upload); Assert.Equal("0", result.UnallocatedUpload); Assert.Equal(2, result.GapCount);
        Assert.Equal("13", result.Nodes.Single(x => x.Key == "instance/A").Upload);
        Assert.Equal("20", result.Nodes.Single(x => x.Key == "instance/B").Upload);
        Assert.Equal("Renamed A", result.Nodes.Single(x => x.Key == "instance/A").Name);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ObservedSwitchBeforeFailedSampleCannotRecoverBTrafficUnderAAfterRestart(bool switchDuringSample)
    {
        Directory.CreateDirectory(directory);
        using (var ledger = new Ledger(History))
        {
            var guard = new XrayGenerationGuard(); Accept(ledger, guard, Snapshot(), 0, 100); Accept(ledger, guard, Snapshot(), 1, 110);
            if (switchDuringSample) Assert.False(guard.Accept(Snapshot(), Snapshot("B"), out _));
            else Assert.True(guard.Observe(Snapshot("B"), out _)); // B was seen before an HTTP failure.
            Assert.True(guard.RequiresBaseline);
            ledger.RecordGap(Start.AddSeconds(2), switchDuringSample ? "snapshot_changed" : "interface_unavailable", guard.RequiresBaseline);
            guard.AcknowledgePersistence();
            ledger.RecordGap(Start.AddSeconds(3), "interface_unavailable"); // Later outage cannot restore the removed cursor.
        }
        using (var ledger = new Ledger(History))
        {
            var restartedMonitor = new XrayGenerationGuard();
            Assert.Equal("baseline", Accept(ledger, restartedMonitor, Snapshot(), 4, 9000));
            Accept(ledger, restartedMonitor, Snapshot(), 5, 9007);
            Assert.Equal("17", ledger.Query(All).Upload); Assert.Equal("0", ledger.Query(All).UnallocatedUpload);
            Assert.Equal(1, ledger.Query(All).GapCount);
        }
    }

    [Fact]
    public void FailedBaselineWriteRetainsBoundaryAndRetryCannotReuseOldCursor()
    {
        Directory.CreateDirectory(directory);
        using var ledger = new Ledger(History); var guard = new XrayGenerationGuard();
        Accept(ledger, guard, Snapshot(), 0, 100); Accept(ledger, guard, Snapshot(), 1, 110);
        Assert.False(guard.Accept(Snapshot(), Snapshot("B"), out _));
        Assert.True(guard.Observe(Snapshot(), out _));
        using var db = new SqliteConnection($"Data Source={History};Pooling=False"); db.Open(); using var command = db.CreateCommand();
        command.CommandText = "CREATE TRIGGER synthetic_failure BEFORE INSERT ON cursors BEGIN SELECT RAISE(ABORT,'synthetic'); END;"; command.ExecuteNonQuery();
        Assert.Throws<SqliteException>(() => Accept(ledger, guard, Snapshot(), 2, 9000));
        Assert.True(guard.RequiresBaseline); Assert.Equal(0, ledger.Query(All).GapCount); Assert.Equal("10", ledger.Query(All).Upload);
        command.CommandText = "DROP TRIGGER synthetic_failure;"; command.ExecuteNonQuery();
        Assert.Equal("baseline", Accept(ledger, guard, Snapshot(), 3, 9000));
        Accept(ledger, guard, Snapshot(), 4, 9010);
        Assert.Equal("20", ledger.Query(All).Upload); Assert.Equal("0", ledger.Query(All).UnallocatedUpload);
    }

    [Fact]
    public void FailedCursorInvalidationRollsBackTheGapAndDoesNotAcknowledgeBoundary()
    {
        Directory.CreateDirectory(directory); using var ledger = new Ledger(History); var guard = new XrayGenerationGuard();
        Accept(ledger, guard, Snapshot(), 0, 100); Accept(ledger, guard, Snapshot(), 1, 110);
        Assert.True(guard.Observe(Snapshot("B"), out _));
        using var db = new SqliteConnection($"Data Source={History};Pooling=False"); db.Open(); using var command = db.CreateCommand();
        command.CommandText = "CREATE TRIGGER synthetic_failure BEFORE DELETE ON cursors BEGIN SELECT RAISE(ABORT,'synthetic'); END;"; command.ExecuteNonQuery();
        Assert.Throws<SqliteException>(() => ledger.RecordGap(Start.AddSeconds(2), "mapping_changed", guard.RequiresBaseline));
        Assert.True(guard.RequiresBaseline); Assert.Equal(0, ledger.Query(All).GapCount);
        command.CommandText = "DROP TRIGGER synthetic_failure;"; command.ExecuteNonQuery();
        ledger.RecordGap(Start.AddSeconds(3), "mapping_changed", guard.RequiresBaseline); guard.AcknowledgePersistence();
        Assert.Equal("baseline", Accept(ledger, guard, Snapshot(), 4, 9000));
        Assert.Equal("10", ledger.Query(All).Upload); Assert.Equal("0", ledger.Query(All).UnallocatedUpload);
    }

    [Fact]
    public void ProfileMismatchBeforeSnapshotInvalidatesContinuityAndStaleSampleCannotClearIt()
    {
        var changed = Profile("B"); changed["ProtoExtra"] = "{\"VlessEncryption\":\"other\"}";
        CreateProfiles(Profile(), changed);
        using var ledger = new Ledger(History); var guard = new XrayGenerationGuard();
        Accept(ledger, guard, Snapshot(), 0, 100); Accept(ledger, guard, Snapshot(), 1, 110);
        Assert.Equal("mapping_changed", Assert.Throws<WindowsDiscovery.DiscoveryException>(() => Read("B")).State);
        guard.InvalidateContinuity(); // Same disposition as the production DiscoveryException catch.
        Assert.Equal("stale", Accept(ledger, guard, Snapshot(), 1, 110));
        Assert.True(guard.RequiresBaseline);
        Assert.Equal("baseline", Accept(ledger, guard, Snapshot(), 2, 9000));
        Assert.Equal("10", ledger.Query(All).Upload); Assert.Equal("0", ledger.Query(All).UnallocatedUpload);
    }

    [Fact]
    public void PureHttpOutageAndMonitorRestartStillRecoverOnlyAsUnallocated()
    {
        Directory.CreateDirectory(directory);
        using (var ledger = new Ledger(History))
        {
            var guard = new XrayGenerationGuard(); Accept(ledger, guard, Snapshot(), 0, 100);
            Assert.True(guard.Observe(Snapshot(), out _)); Assert.False(guard.RequiresBaseline);
            ledger.RecordGap(Start.AddSeconds(1), "interface_unavailable", guard.RequiresBaseline);
        }
        using (var ledger = new Ledger(History))
        {
            var guard = new XrayGenerationGuard();
            Assert.Equal("unallocated", Accept(ledger, guard, Snapshot(), 2, 130));
            Assert.Equal("30", ledger.Query(All).UnallocatedUpload); Assert.Equal("30", ledger.Query(All).Upload);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CoreRestartOrEitherCounterRollbackCannotCreateLargeOrNegativeDeltas(bool restart)
    {
        Directory.CreateDirectory(directory); using var ledger = new Ledger(History); var guard = new XrayGenerationGuard();
        Accept(ledger, guard, Snapshot(), 0, 100, 100); Accept(ledger, guard, Snapshot(), 1, 110, 120);
        var next = Snapshot(process: restart ? "core-2" : "core-1");
        Assert.Equal(restart ? "baseline" : "reset", Accept(ledger, guard, next, 2, 9000, restart ? 9000 : 10));
        Accept(ledger, guard, next, 3, 9003, restart ? 9004 : 14);
        Assert.Equal("13", ledger.Query(All).Upload); Assert.Equal("24", ledger.Query(All).Download);
    }

    private static ScopeCapture ScopeFixture(IReadOnlyList<string>? enabled = null, XraySnapshot? snapshot = null)
    {
        var bound = snapshot ?? MemberSnapshot();
        Assert.True(SingboxParser.TryReadMemberCatalog(bound, Bytes(MemberConfiguration), out var catalog, out _));
        return new(bound, catalog, enabled ?? ["proxy", "other"]);
    }
    private static V2RayMemberStatistics ScopeCounters(long proxy = 0, long other = 0) =>
        new(new Dictionary<string, XrayCounters> { ["proxy"] = new(proxy, 0), ["other"] = new(other, 0) },
            new Dictionary<string, string>(), new Dictionary<string, XrayCounters>
            { ["business"] = new(proxy + other, 0), ["excluded"] = new(7, 0), ["unknown"] = new(9, 0) },
            new Dictionary<string, string>());

    [Fact]
    public void ScopeSidecarIsStrictAndLegacyParserDoesNotSilentlyEnableMembers()
    {
        var json = Bytes("""{"listen":"127.0.0.1:10814","monitor_members":["proxy","other"]}""");
        Assert.True(SingboxParser.TryReadMemberStatisticsSettings(json, out var endpoint, out var members));
        Assert.Equal(10814, endpoint!.Port); Assert.Equal(["proxy", "other"], members);
        Assert.False(SingboxParser.TryReadStatisticsEndpoint(json, out _));
        foreach (var invalid in new[] {
            """{"listen":"0.0.0.0:10814","monitor_members":["proxy"]}""",
            """{"listen":"127.0.0.1:10814","monitor_members":["proxy","proxy"]}""",
            """{"listen":"127.0.0.1:10814","monitor_members":["bad>>>tag"]}""",
            """{"listen":"127.0.0.1:10814","monitor_members":null}""",
            """{"listen":"127.0.0.1:10814","monitor_members":[] }""",
            """{"listen":"127.0.0.1:10814","route":{}}""" })
            Assert.False(SingboxParser.TryReadMemberStatisticsSettings(Bytes(invalid), out _, out _));
    }

    [Fact]
    public void ScopeProjectionUsesVerifiedIndexOnlyForProxyAndPreservesAllDimensions()
    {
        var capture = ScopeFixture(["proxy"]);
        var batch = ScopeCollector.Project(capture, ScopeCounters(10, 20), Start);
        Assert.Equal(5, batch.Readings.Count);
        var proxy = Assert.Single(batch.Readings, x => x.Scope.OutboundTag == "proxy");
        Assert.Equal("A", proxy.Scope.NodeId);
        Assert.Equal("index_id", proxy.Scope.IdentityKind);
        var other = Assert.Single(batch.Readings, x => x.Scope.OutboundTag == "other");
        Assert.Null(other.Scope.NodeId); Assert.Null(other.Upload);
        Assert.Equal("member_statistics_disabled", other.UnavailableReason);
        Assert.Equal("outbound", other.Scope.IdentityKind);
        Assert.Contains(XrayParser.Hash(Bytes(MemberConfiguration)), other.Scope.IdentityValue);
        Assert.Equal(30, Assert.Single(batch.Readings, x => x.Scope.Basis == "business").Upload);
        Assert.Equal(7, Assert.Single(batch.Readings, x => x.Scope.Basis == "excluded").Upload);
        Assert.Equal(9, Assert.Single(batch.Readings, x => x.Scope.Basis == "unknown").Upload);
        Assert.DoesNotContain("Synthetic-Credential", JsonSerializer.Serialize(batch));
        Assert.Throws<ArgumentException>(() => ScopeCollector.Project(ScopeFixture(["group"]), ScopeCounters(), Start));
    }

    [Fact]
    public async Task ScopeWindowsBoundaryRejectsChangedCoreBeforeAnyBatchCanBeCommitted()
    {
        using var discovery = new WindowsDiscovery();
        var first = ScopeFixture();
        var second = ScopeFixture(snapshot: first.Snapshot with { ProcessIdentity = "core-2" });
        var calls = 0;
        var reading = await discovery.ReadScopesBoundAsync(() => calls++ == 0 ? first : second,
            (_, _) => Task.FromResult<(V2RayMemberStatistics?, XrayCounters?)>((ScopeCounters(100, 200), null)), CancellationToken.None);
        Assert.Null(reading.Batch);
        Assert.True(reading.InvalidateContinuity);
        Assert.True(discovery.RequiresBaseline);
    }

    [Fact]
    public async Task ScopeWindowsPipelineAndLedgerHandlePartialCountersAndPersistenceAcknowledgment()
    {
        using var discovery = new WindowsDiscovery();
        using var ledger = ScopeLedger.CreateNew(History);
        var capture = ScopeFixture();
        var first = await discovery.ReadScopesBoundAsync(() => capture,
            (_, _) => Task.FromResult<(V2RayMemberStatistics?, XrayCounters?)>((ScopeCounters(100, 200), null)), CancellationToken.None);
        ScopeCollector.Commit(ledger, first.Batch! with { At = Start });
        discovery.AcknowledgePersistence();
        var partial = ScopeCounters(110, 220) with
        { Members = new Dictionary<string, XrayCounters> { ["proxy"] = new(110, 0) }, UnavailableMembers = new Dictionary<string, string> { ["other"] = "missing_counter" } };
        var next = await discovery.ReadScopesBoundAsync(() => capture,
            (_, _) => Task.FromResult<(V2RayMemberStatistics?, XrayCounters?)>((partial, null)), CancellationToken.None);
        ScopeCollector.Commit(ledger, next.Batch! with { At = Start.AddSeconds(1) });
        var query = new ScopeQuery("actual_member", Start.AddMinutes(-1), Start.AddMinutes(2), "UTC");
        Assert.Equal("10", ledger.Query(query).Upload);
        Assert.Contains(ledger.Query(query).Gaps, x => x.IsOpen && x.Reason == "missing_counter");
        Assert.False(discovery.RequiresBaseline);
    }

    [Fact]
    public void ScopeCollectorGapInvalidationAndWholeMissingRpcDoNotFabricateZeroOrRecovery()
    {
        using var ledger = ScopeLedger.CreateNew(History);
        ledger.RecordGap(Start.AddSeconds(-1), "unavailable", true);
        ScopeCollector.Commit(ledger, ScopeCollector.Project(ScopeFixture(), ScopeCounters(100, 200), Start));
        ledger.RecordGap(Start.AddSeconds(1), "mapping_changed", true);
        ScopeCollector.Commit(ledger, ScopeCollector.Project(ScopeFixture(), ScopeCounters(500, 600), Start.AddSeconds(2)));
        var query = new ScopeQuery("actual_member", Start.AddMinutes(-1), Start.AddMinutes(2), "UTC");
        Assert.Equal("0", ledger.Query(query).Upload);
        Assert.All(ledger.Query(query).Gaps, x => Assert.False(x.IsOpen));
        var missing = new V2RayMemberStatistics(new Dictionary<string, XrayCounters>(), new Dictionary<string, string>(),
            new Dictionary<string, XrayCounters>(), new Dictionary<string, string>());
        var batch = ScopeCollector.Project(ScopeFixture(), missing, Start.AddSeconds(3));
        Assert.All(batch.Readings, x => { Assert.Null(x.Upload); Assert.Null(x.Download); Assert.NotNull(x.UnavailableReason); });
        ScopeCollector.Commit(ledger, batch);
        Assert.Equal("0", ledger.Query(query).Upload);
        Assert.Throws<ArgumentException>(() => ScopeCollector.Commit(ledger,
            ScopeCollector.Project(ScopeFixture(), ScopeCounters(900, 900), Start.AddSeconds(2.5))));
    }

    [Fact]
    public void ScopeXrayProjectionPreservesLegacyBasisAndIntegerCounters()
    {
        var batch = ScopeCollector.ProjectLegacy(Snapshot(), new(9007199254740993, 7), Start, true);
        var reading = Assert.Single(batch.Readings);
        Assert.Equal("legacy_proxy", reading.Scope.Basis);
        Assert.Equal("legacy_node", reading.Scope.IdentityKind);
        Assert.True(reading.InvalidateContinuity);
        Assert.Equal(9007199254740993, reading.Upload);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScopeAutostartPreservesModeAndKeepsDistinctDirectories(bool scopeMode)
    {
        const string executable = @"C:\Synthetic Program\v2rayN-Monitor.exe";
        const string root = @"F:\Synthetic Data\scope-ledger";
        var command = MonitorStartup.Command(executable, root, scopeMode);
        Assert.StartsWith("\"" + executable + "\" --background", command);
        Assert.EndsWith("--data-dir \"" + root + "\"", command);
        Assert.Equal(scopeMode, command.Contains(" --scope-ledger ", StringComparison.Ordinal));
        Assert.NotEqual(command, MonitorStartup.Command(executable, root, !scopeMode));
        Assert.NotEqual(command, MonitorStartup.Command(executable, root + "-other", scopeMode));
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
