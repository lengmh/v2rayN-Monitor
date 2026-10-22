using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
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
    private readonly List<string> componentStages = [];
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
    private static Dictionary<string, string?> PolicyGroup(string children = "", string subscription = "one") =>
        new(Profile("G"))
        {
            ["Remarks"] = "Synthetic policy group", ["ConfigType"] = "101", ["Address"] = "", ["Port"] = "0",
            ["ProtoExtra"] = JsonSerializer.Serialize(new { ChildItems = children, SubChildItems = subscription, MultipleLoad = 1 })
        };
    private static string PolicyConfiguration(int count = 2) => Change(Configuration, root =>
    {
        var proxy = Proxy(root).DeepClone();
        root["outbounds"]!.AsArray().RemoveAt(0);
        for (var i = 0; i < count; i++)
        {
            var member = proxy.DeepClone();
            member["tag"] = count == 1 ? "proxy" : $"proxy-{i + 1}-Same synthetic name";
            root["outbounds"]!.AsArray().Insert(i, member);
        }
        if (count > 1) root["routing"] = JsonNode.Parse("""
            {"balancers":[{"tag":"proxy-balancer","selector":["proxy"],
              "strategy":{"type":"leastLoad","settings":{"expected":1,"tolerance":0.2,"maxRTT":"5000ms"}}}]}
            """);
    });

    [Fact]
    public void SelectedPolicyGroupDoesNotRequireSingleProxyAnchor()
    {
        CreateProfiles(new(Profile("unsupported")) { ["ConfigType"] = "11" }, Profile("A"), Profile("B"), PolicyGroup());
        Assert.Equal("Synthetic policy group", WindowsDiscovery.ReadProfile(Profiles, "G", Bytes(Gui),
            Bytes(PolicyConfiguration()), out var configuration, allowMemberStatistics: true));
        Assert.Equal("policy-group", configuration.Protocol);
        Assert.Equal("Xray", configuration.CoreName);
    }

    [Fact]
    public void PolicyGroupWithOneExplicitMemberStillBindsItsConnection()
    {
        CreateProfiles(Profile("A"), PolicyGroup("A", ""));
        Assert.Equal("Synthetic policy group", WindowsDiscovery.ReadProfile(Profiles, "G", Bytes(Gui),
            Bytes(PolicyConfiguration(1)), out _, allowMemberStatistics: true));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SelectedPolicyGroupProjectsOnlyMembersAndPreservesBaselineRatesAndGaps(int count)
    {
        CreateProfiles(Profile("A"), Profile("B"), count == 1 ? PolicyGroup("A", "") : PolicyGroup());
        var config = Bytes(PolicyConfiguration(count));
        var name = WindowsDiscovery.ReadProfile(Profiles, "G", Bytes(Gui), config, out var configuration, allowMemberStatistics: true);
        var snapshot = Snapshot("G") with
        {
            NodeName = name, Generation = XrayParser.Hash(config) + ":1", SelectedPolicyGroup = true,
            PolicyGroupSingleMemberName = configuration.PolicyGroupSingleMemberName
        };
        Assert.True(XrayParser.TryReadOutboundCatalog(snapshot, config, out var catalog, out _));
        Assert.Equal(count, catalog!.Candidates.Count);
        Assert.All(catalog.Candidates.Values, member =>
        {
            Assert.Equal("Same synthetic name", member.CapturedName!.OriginalName);
            Assert.Equal(name, member.CapturedName.GroupLabel);
        });
        var capture = new ScopeCapture(snapshot, null, []) { XrayOutbounds = catalog };
        using var discovery = new WindowsDiscovery();
        using var ledger = ScopeLedger.CreateNew(History);
        ScopeCollector.Commit(ledger, ScopeCollector.ProjectLegacy(Snapshot(), new(9000, 8000), Start.AddSeconds(-1), false));
        async Task<ScopeCollectorReading> Read(long up, long down, bool omitLast = false) =>
            await discovery.ReadScopesBoundAsync(() => capture, (_, _) =>
                Task.FromResult<(V2RayMemberStatistics?, XrayCounters?, V2RayProbeStatistics?, V2RayOutboundStatistics?)>((null, null, null,
                    new(catalog.Candidates.Keys.Take(count - (omitLast ? 1 : 0)).ToDictionary(tag => tag, _ => new XrayCounters(up, down)),
                        new Dictionary<string, string>()))), CancellationToken.None, includeProbes: true);
        var first = await Read(100, 200);
        Assert.Equal("connected", first.State);
        Assert.Equal(name, first.CurrentNode);
        Assert.Equal("unsupported", first.Probes!.State);
        Assert.All(first.Batch!.Readings, member =>
        {
            Assert.Equal("actual_member", member.Scope.Basis);
            Assert.Null(member.Scope.NodeId);
        });
        var firstBatch = first.Batch with { At = Start };
        Assert.All(ScopeCollector.Commit(ledger, firstBatch).Scopes.Values, state => Assert.Equal("baseline", state));
        Assert.Equal("0", ledger.Query(new ScopeQuery("local_proxy", Start.AddMinutes(-1), Start.AddMinutes(1), "UTC")).Upload);
        discovery.AcknowledgePersistence();
        var second = await Read(113, 229);
        var secondBatch = second.Batch! with { At = Start.AddSeconds(1) };
        ScopeCollector.Commit(ledger, secondBatch);
        var result = ledger.Query(new ScopeQuery("local_proxy", Start.AddMinutes(-1), Start.AddMinutes(1), "UTC"));
        Assert.Equal((count * 13).ToString(CultureInfo.InvariantCulture), result.Upload);
        Assert.Equal((count * 29).ToString(CultureInfo.InvariantCulture), result.Download);
        Assert.Equal((result.Upload, result.Download), ScopeCollector.Rates(firstBatch, secondBatch));
        Assert.Single(ledger.GetScopes("legacy_proxy"));
        var missing = await Read(120, 240, omitLast: true);
        Assert.Equal("partial", missing.State);
        var missingBatch = missing.Batch! with { At = Start.AddSeconds(2) };
        ScopeCollector.Commit(ledger, missingBatch);
        Assert.Equal((null, null), ScopeCollector.Rates(secondBatch, missingBatch));
        Assert.Contains(ledger.Query(new ScopeQuery("local_proxy", Start.AddMinutes(-1), Start.AddMinutes(1), "UTC")).Gaps,
            gap => gap.Reason == "missing_counter" && gap.IsOpen);
    }

    [Theory]
    [InlineData("credential")]
    [InlineData("transport")]
    [InlineData("member")]
    [InlineData("selector")]
    [InlineData("strategy")]
    [InlineData("chain")]
    public void SelectedPolicyGroupDoesNotBypassMemberOrConfigurationBinding(string change)
    {
        CreateProfiles(Profile("A"), Profile("B"), PolicyGroup());
        var config = Change(PolicyConfiguration(), root =>
        {
            switch (change)
            {
                case "credential": Proxy(root)["settings"]!["id"] = "00000000-0000-0000-0000-000000000002"; break;
                case "transport": Proxy(root)["streamSettings"]!["tlsSettings"]!["serverName"] = "other.synthetic.invalid"; break;
                case "member": root["outbounds"]!.AsArray().RemoveAt(1); break;
                case "selector": root["routing"]!["balancers"]![0]!["selector"]![0] = "other"; break;
                case "strategy": root["routing"]!["balancers"]![0]!["strategy"]!["type"] = "random"; break;
                case "chain": Proxy(root)["proxySettings"] = new JsonObject { ["tag"] = "direct" }; break;
            }
        });
        var failure = Assert.Throws<WindowsDiscovery.DiscoveryException>(() =>
            WindowsDiscovery.ReadProfile(Profiles, "G", Bytes(Gui), Bytes(config), out _, allowMemberStatistics: true));
        Assert.Equal(change == "chain" ? "unsupported_configuration" : "mapping_changed", failure.State);
    }

    [Fact]
    public void PolicyGroupFilterAndLegacyModeAreNotIgnored()
    {
        var group = PolicyGroup();
        group["ProtoExtra"] = """{"SubChildItems":"one","Filter":"does-not-match","MultipleLoad":1}""";
        CreateProfiles(Profile("A"), Profile("B"), group);
        Assert.Equal("mapping_changed", Assert.Throws<WindowsDiscovery.DiscoveryException>(() =>
            WindowsDiscovery.ReadProfile(Profiles, "G", Bytes(Gui), Bytes(PolicyConfiguration()), out _, allowMemberStatistics: true)).State);
        Assert.Equal("unsupported_mode", Assert.Throws<WindowsDiscovery.DiscoveryException>(() =>
            WindowsDiscovery.ReadProfile(Profiles, "G", Bytes(Gui), Bytes(PolicyConfiguration()), out _)).State);
    }

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

    [Fact]
    public void IdenticalPeriodicGuiReplacementRetainsCounterContinuity()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "gui.json");
        var bytes = Bytes(Gui);
        File.WriteAllBytes(path, bytes);
        var writtenAt = new DateTimeOffset(File.GetLastWriteTimeUtc(path));
        using var ledger = new Ledger(History);
        var guard = new XrayGenerationGuard();
        Accept(ledger, guard, Snapshot(), 0, 100);
        Accept(ledger, guard, Snapshot(), 1, 110);

        var replacement = path + "_temp";
        File.WriteAllBytes(replacement, bytes);
        File.SetLastWriteTimeUtc(replacement, writtenAt.UtcDateTime.AddSeconds(1));
        File.Move(replacement, path, true); // The pinned client saves through an atomic replacement.

        Assert.True(WindowsDiscovery.GuiFileUnchanged(path, bytes, writtenAt));
        Assert.Equal("recorded", Accept(ledger, guard, Snapshot(), 2, 130));
        Assert.Equal("30", ledger.Query(All).Upload);
        Assert.Equal(0, ledger.Query(All).GapCount);
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("defaults")]
    [InlineData("statistics")]
    public void ChangedGuiReplacementStillRejectsTheCapturedSnapshot(string change)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "gui.json");
        var bytes = Bytes(Gui);
        File.WriteAllBytes(path, bytes);
        var writtenAt = new DateTimeOffset(File.GetLastWriteTimeUtc(path));
        var changed = change switch
        {
            "selection" => Gui.Replace("\"IndexId\":\"A\"", "\"IndexId\":\"B\""),
            "defaults" => Gui.Replace("chrome", "firefox"),
            _ => Gui.Replace("\"EnableStatistics\":true", "\"EnableStatistics\":false")
        };
        File.WriteAllText(path, changed);
        File.SetLastWriteTimeUtc(path, writtenAt.UtcDateTime.AddSeconds(1));
        Assert.False(WindowsDiscovery.GuiFileUnchanged(path, bytes, writtenAt));
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

    [Fact]
    public async Task JointScopeReaderProjectsBothDimensionsFromOneReadOnlyResponse()
    {
        using var handler = new StatisticsHandler(StatisticsFrame(
            ("monitor>>>member>>>proxy>>>traffic>>>uplink", 9007199254740993),
            ("monitor>>>member>>>proxy>>>traffic>>>downlink", 7),
            ("monitor>>>probe>>>urltest>>>member>>>proxy>>>traffic>>>uplink", 13),
            ("monitor>>>probe>>>urltest>>>member>>>proxy>>>traffic>>>downlink", 17)));
        using var client = new HttpClient(handler);
        var result = await V2RayStatistics.ReadScopesAsync(client, new Uri("http://127.0.0.1:10814/"),
            ["proxy", "missing"], CancellationToken.None);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(new XrayCounters(9007199254740993, 7), result!.Members.Members["proxy"]);
        Assert.Equal(new XrayCounters(13, 17), result.Probes.Members["proxy"]);
        Assert.Equal("missing_counter", result.Probes.UnavailableMembers["missing"]);
        Assert.Equal("missing_counter", result.Members.UnavailableMembers["missing"]);
    }

    private static string PreparationConfiguration => MemberConfiguration.Replace("\"password\":", "\"server_port\":443,\"password\":", StringComparison.Ordinal);

    [Fact]
    public void CapabilityPreparationUsesOnlyIndependentMembersAndDoesNotExposeCredentials()
    {
        var config = PreparationConfiguration;
        var result = CoreCapabilityPreparation.PrepareSidecar(MemberSnapshot(config), Bytes(config), null, "127.0.0.1:10814");
        Assert.Equal(SidecarPreparationState.Prepared, result.State);
        Assert.True(SingboxParser.TryReadMemberStatisticsSettings(Bytes(result.Plan!.Json), out _, out var members));
        Assert.Equal(new[] { "other", "proxy" }, members);
        Assert.DoesNotContain("Synthetic-Credential", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("node.synthetic.invalid", JsonSerializer.Serialize(result));
        Assert.True(CoreCapabilityPreparation.IsCurrent(result.Plan, MemberSnapshot(config), Bytes(config), null));
    }

    [Theory]
    [InlineData("0.0.0.0:10814")]
    [InlineData("localhost:10814")]
    [InlineData("127.0.0.1:0")]
    [InlineData("127.0.0.1:10814/path")]
    public void CapabilityPreparationRejectsNonNumericOrNonLoopbackEndpoint(string listen)
    {
        var config = PreparationConfiguration;
        var result = CoreCapabilityPreparation.PrepareSidecar(MemberSnapshot(config), Bytes(config), null, listen);
        Assert.Equal(SidecarPreparationState.InvalidEndpoint, result.State);
        Assert.Null(result.Plan);
    }

    [Theory]
    [InlineData("{\"listen\":\"127.0.0.1:10814\",\"future\":{\"keep\":true}}")]
    [InlineData("{\"listen\":\"127.0.0.1:10814\",\"listen\":\"127.0.0.1:10815\"}")]
    [InlineData("")]
    [InlineData("[]")]
    public void CapabilityPreparationPreservesUnsupportedExistingBytes(string text)
    {
        var previous = Bytes(text);
        var config = PreparationConfiguration;
        var result = CoreCapabilityPreparation.PrepareSidecar(MemberSnapshot(config), Bytes(config), previous, "127.0.0.1:10814");
        Assert.Equal(SidecarPreparationState.UnsupportedExistingSidecar, result.State);
        Assert.Null(result.Plan);
        Assert.Equal(Bytes(text), previous);
    }

    [Fact]
    public void CapabilityPreparationDistinguishesConfirmationFromSemanticNoOp()
    {
        var config = PreparationConfiguration;
        var snapshot = MemberSnapshot(config);
        var old = Bytes("{\"listen\":\"127.0.0.1:10814\"}");
        var change = CoreCapabilityPreparation.PrepareSidecar(snapshot, Bytes(config), old, "127.0.0.1:10814");
        Assert.Equal(SidecarPreparationState.ConfirmationRequired, change.State);
        var same = Bytes("{\"monitor_members\":[\"proxy\",\"other\"], \"listen\":\"127.0.0.1:10814\"}");
        Assert.Equal(SidecarPreparationState.AlreadyMatched,
            CoreCapabilityPreparation.PrepareSidecar(snapshot, Bytes(config), same, "127.0.0.1:10814").State);
        Assert.Equal(SidecarPreparationState.ConfirmationRequired,
            CoreCapabilityPreparation.PrepareSidecar(snapshot, Bytes(config), same, "[::1]:10814").State);
        Assert.True(CoreCapabilityPreparation.IsCurrent(change.Plan!, snapshot, Bytes(config), old));
        Assert.False(CoreCapabilityPreparation.IsCurrent(change.Plan!, snapshot, Bytes(config), null));
        Assert.False(CoreCapabilityPreparation.IsCurrent(change.Plan!, snapshot, Bytes(config), same));
        Assert.False(CoreCapabilityPreparation.IsCurrent(change.Plan!, snapshot, Bytes(config + " "), old));
        foreach (var altered in new[] { snapshot with { InstanceId = "other" }, snapshot with { NodeId = "B" },
            snapshot with { ProcessIdentity = "restarted" }, snapshot with { Generation = "changed" }, snapshot with { CoreName = "Xray" } })
            Assert.False(CoreCapabilityPreparation.IsCurrent(change.Plan!, altered, Bytes(config), old));
    }

    [Fact]
    public void CapabilityPreparationRequiresVerifiedAnyTlsAnchorAndMatchingGeneration()
    {
        var config = PreparationConfiguration;
        var snapshot = MemberSnapshot(config);
        foreach (var altered in new[] { snapshot with { NodeId = "" }, snapshot with { ProcessIdentity = "" },
            snapshot with { Generation = "changed" }, snapshot with { CoreName = "Xray" } })
            Assert.Equal(SidecarPreparationState.InvalidConfiguration,
                CoreCapabilityPreparation.PrepareSidecar(altered, Bytes(config), null, "127.0.0.1:10814").State);
        Assert.Equal(SidecarPreparationState.InvalidConfiguration,
            CoreCapabilityPreparation.PrepareSidecar(MemberSnapshot(), Bytes(MemberConfiguration), null, "127.0.0.1:10814").State);
    }

    [Theory]
    [InlineData(1, false, "--scope-ledger", 1)]
    [InlineData(1, true, "--probe-ledger", 2)]
    [InlineData(2, false, "--scope-ledger", 0)]
    [InlineData(2, true, "--probe-ledger", 1)]
    [InlineData(3, false, "--probe-ledger", 0)]
    [InlineData(3, true, "--probe-ledger", 0)]
    public void CapabilityPreparationKeepsV3AndOrdersExplicitUpgradeSteps(int version, bool probes, string flag, int count)
    {
        var result = CoreCapabilityPreparation.PrepareLedger(version, probes);
        Assert.Equal(flag, result.StartupFlag);
        Assert.Equal(count, result.Steps.Count);
        if (version == 1) Assert.Equal(LedgerUpgradeStep.MultiScope, result.Steps[0]);
        if (probes && version < 3) Assert.Equal(LedgerUpgradeStep.Probes, result.Steps[^1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void CapabilityPreparationRejectsUnknownLedgerVersions(int version)
        => Assert.Throws<ArgumentOutOfRangeException>(() => CoreCapabilityPreparation.PrepareLedger(version, true));
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
            .Replace("\"group\"", "\"12345-proxy-US-1x\"", StringComparison.Ordinal)
            .Replace("\"auto\"", "\"12345-proxy-US-1x-auto\"", StringComparison.Ordinal)
            .Replace("\"type\":\"selector\",\"outbounds\":[\"proxy\",", "\"type\":\"selector\",\"outbounds\":[\"12345-proxy-US-1x-auto\",\"proxy\",", StringComparison.Ordinal);
        var snapshot = MemberSnapshot(config);
        Assert.True(SingboxParser.TryReadMemberCatalog(snapshot, Bytes(config), out var catalog, out _));
        var member = catalog!.Candidates[tag];
        Assert.Equal("【US-1x】 Synthetic A", member.DisplayName);
        Assert.Equal(new CapturedNodeName("Synthetic A", "US-1x"), member.CapturedName);
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
    public void UnrelatedUrltestReferenceDoesNotSupplyAGroupMultiplierOrOriginalName()
    {
        const string tag = "12345-proxy-US-1x-1-Synthetic A";
        var config = MemberConfiguration.Replace("\"other\"", JsonSerializer.Serialize(tag), StringComparison.Ordinal)
            .Replace("\"group\"", "\"12345-proxy-US-1x\"", StringComparison.Ordinal);
        Assert.True(SingboxParser.TryReadMemberCatalog(MemberSnapshot(config), Bytes(config), out var catalog, out _));
        Assert.Null(catalog!.Candidates[tag].CapturedName);
        Assert.Null(catalog.Candidates[tag].DisplayName);
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
    public void ProbeProjectionUsesVerifiedMembersAndKeepsUnavailableScopesInTheJointBatch()
    {
        var capture = ScopeFixture(enabled: ["proxy"]);
        var probes = new V2RayProbeStatistics(new Dictionary<string, XrayCounters>
            { ["proxy"] = new(100, 200), ["other"] = new(999, 999), ["unverified"] = new(999, 999) },
            new Dictionary<string, string>(), new Dictionary<string, XrayCounters>
            { ["business"] = new(100, 200) }, new Dictionary<string, string>());
        var batch = ScopeCollector.ProjectWithProbes(capture, ScopeCounters(10, 20), probes, Start);
        var member = batch.Readings.Single(x => x.Scope.Basis == "probe_urltest_member" && x.Scope.OutboundTag == "proxy");
        var business = batch.Readings.Single(x => x.Scope.Basis == "actual_member" && x.Scope.OutboundTag == "proxy");
        Assert.Equal(business.Scope with { Basis = "probe_urltest_member" }, member.Scope);
        Assert.Equal(100L, member.Upload);
        var disabled = batch.Readings.Single(x => x.Scope.Basis == "probe_urltest_member" && x.Scope.OutboundTag == "other");
        Assert.Equal("probe_statistics_disabled", disabled.UnavailableReason);
        Assert.Null(disabled.Upload);
        Assert.DoesNotContain(batch.Readings, x => x.Scope.OutboundTag == "unverified");
        Assert.Equal("missing_counter", batch.Readings.Single(x => x.Scope.Basis == "probe_urltest_excluded").UnavailableReason);
        using var ledger = ScopeLedger.CreateNew(History, includeProbes: true);
        ScopeCollector.Commit(ledger, batch);
        var next = ScopeCollector.ProjectWithProbes(capture, ScopeCounters(15, 20), probes with
        {
            Members = new Dictionary<string, XrayCounters> { ["proxy"] = new(107, 211) },
            Totals = new Dictionary<string, XrayCounters> { ["business"] = new(107, 211) }
        }, Start.AddSeconds(1));
        ScopeCollector.Commit(ledger, next);
        var query = new ScopeQuery("probe_urltest_member", Start.AddSeconds(-1), Start.AddMinutes(1), "UTC");
        Assert.Equal("7", ledger.Query(query).Upload);
        Assert.Equal("11", ledger.Query(query).Download);
        Assert.Equal("5", ledger.Query(query with { Basis = "actual_member" }).Upload);
        Assert.Contains(ledger.Query(query).Gaps, x => x.IsOpen);
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScopeWindowsBoundaryRejectsChangedCoreBeforeAnyBatchCanBeCommitted(bool includeProbes)
    {
        using var discovery = new WindowsDiscovery();
        var first = ScopeFixture();
        var second = ScopeFixture(snapshot: first.Snapshot with { ProcessIdentity = "core-2" });
        var calls = 0;
        var reading = await discovery.ReadScopesBoundAsync(() => calls++ == 0 ? first : second,
            (_, _) => Task.FromResult<(V2RayMemberStatistics?, XrayCounters?, V2RayProbeStatistics?, V2RayOutboundStatistics?)>((ScopeCounters(100, 200), null, null, null)),
            CancellationToken.None, includeProbes);
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
            (_, _) => Task.FromResult<(V2RayMemberStatistics?, XrayCounters?, V2RayProbeStatistics?, V2RayOutboundStatistics?)>((ScopeCounters(100, 200), null, null, null)), CancellationToken.None);
        ScopeCollector.Commit(ledger, first.Batch! with { At = Start });
        discovery.AcknowledgePersistence();
        var partial = ScopeCounters(110, 220) with
        { Members = new Dictionary<string, XrayCounters> { ["proxy"] = new(110, 0) }, UnavailableMembers = new Dictionary<string, string> { ["other"] = "missing_counter" } };
        var next = await discovery.ReadScopesBoundAsync(() => capture,
            (_, _) => Task.FromResult<(V2RayMemberStatistics?, XrayCounters?, V2RayProbeStatistics?, V2RayOutboundStatistics?)>((partial, null, null, null)), CancellationToken.None);
        ScopeCollector.Commit(ledger, next.Batch! with { At = Start.AddSeconds(1) });
        var query = new ScopeQuery("actual_member", Start.AddMinutes(-1), Start.AddMinutes(2), "UTC");
        Assert.Equal("10", ledger.Query(query).Upload);
        Assert.Contains(ledger.Query(query).Gaps, x => x.IsOpen && x.Reason == "missing_counter");
        Assert.False(discovery.RequiresBaseline);
    }

    [Fact]
    public async Task ProbeWindowsPipelinePersistsJointBatchAndOldCoreKeepsBusinessCollecting()
    {
        using var discovery = new WindowsDiscovery();
        using var ledger = ScopeLedger.CreateNew(History, includeProbes: true);
        var capture = ScopeFixture();
        V2RayProbeStatistics Probes(long n) => new(
            new Dictionary<string, XrayCounters> { ["proxy"] = new(n, 0), ["other"] = new(2 * n, 0) },
            new Dictionary<string, string>(),
            new Dictionary<string, XrayCounters> { ["business"] = new(3 * n, 0), ["excluded"] = new(0, 0), ["unknown"] = new(0, 0) },
            new Dictionary<string, string>());
        async Task<ScopeCollectorReading> Read(long business, V2RayProbeStatistics probes) =>
            await discovery.ReadScopesBoundAsync(() => capture,
                (_, _) => Task.FromResult<(V2RayMemberStatistics?, XrayCounters?, V2RayProbeStatistics?, V2RayOutboundStatistics?)>((ScopeCounters(business, business), null, probes, null)),
                CancellationToken.None, includeProbes: true);
        var first = await Read(100, Probes(1000));
        Assert.Equal("partial", first.Probes!.State); // Synthetic catalog also contains unsupported chain/outbound paths.
        ScopeCollector.Commit(ledger, first.Batch! with { At = Start });
        discovery.AcknowledgePersistence();
        var query = new ScopeQuery("probe_urltest_member", Start.AddMinutes(-1), Start.AddMinutes(1), "UTC");
        Assert.Equal("0", ledger.Query(query).Upload);
        Assert.Null(ledger.Query(query).EarliestRecord);
        var next = await Read(110, Probes(1007));
        ScopeCollector.Commit(ledger, next.Batch! with { At = Start.AddSeconds(1) });
        discovery.AcknowledgePersistence();
        Assert.Equal("21", ledger.Query(query).Upload);
        Assert.Equal("20", ledger.Query(query with { Basis = "actual_member" }).Upload);
        Assert.NotNull(ledger.Query(query with { Start = Start.AddDays(10), End = Start.AddDays(11) }).EarliestRecord);
        var old = await Read(115, new(new Dictionary<string, XrayCounters>(), new Dictionary<string, string>(),
            new Dictionary<string, XrayCounters>(), new Dictionary<string, string>()));
        Assert.Equal("unsupported", old.Probes!.State);
        ScopeCollector.Commit(ledger, old.Batch! with { At = Start.AddSeconds(2) });
        Assert.Equal("30", ledger.Query(query with { Basis = "actual_member" }).Upload);
        Assert.Equal("21", ledger.Query(query).Upload);
        Assert.Contains(ledger.Query(query).Gaps, gap => gap.IsOpen && gap.Reason == "missing_counter");
    }

    [Fact]
    public void ProbeExtensionIsPinnedAndDoesNotBroadenLegacyApproval()
    {
        Assert.True(WindowsDiscovery.IsApprovedExtension(WindowsDiscovery.ApprovedMemberSingboxSha256, true));
        Assert.True(WindowsDiscovery.IsApprovedExtension(WindowsDiscovery.ApprovedProbeSingboxSha256, true));
        Assert.False(WindowsDiscovery.IsApprovedExtension(WindowsDiscovery.ApprovedProbeSingboxSha256, false));
        Assert.True(WindowsDiscovery.IsApprovedExtension(WindowsDiscovery.ApprovedSingboxSha256, false));
        Assert.False(WindowsDiscovery.IsApprovedExtension(new string('0', 64), true));
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
    public async Task XrayProbeApprovalChangesInvalidateTheWholeRead()
    {
        var before = new ScopeCapture(Snapshot() with { XrayProbeExtensionVerified = true }, null, []);
        var after = before with { Snapshot = before.Snapshot with { XrayProbeExtensionVerified = false } };
        var captures = 0;
        using var discovery = new WindowsDiscovery();
        var result = await discovery.ReadScopesBoundAsync(() => captures++ == 0 ? before : after,
            (_, _) => Task.FromResult<(V2RayMemberStatistics?, XrayCounters?, V2RayProbeStatistics?, V2RayOutboundStatistics?)>(
                (null, null, null, null)), CancellationToken.None, includeProbes: true);
        Assert.Null(result.Batch);
        Assert.Equal("snapshot_changed", result.State);
        Assert.True(result.InvalidateContinuity);
    }

    [Fact]
    public void XrayProbeCountersRequireVersionedContractAndKeepBadPairsUnknown()
    {
        const string json = """
            {"stats":{"outbound":{"proxy":{"uplink":1000,"downlink":2000}}},
             "monitor_probes":{"version":1,"kind":"observatory","members":{
               "proxy":{"uplink":9007199254740993,"downlink":9223372036854775807},
               "duplicate":{"uplink":1,"downlink":2},"duplicate":{"uplink":3,"downlink":4},
               "bad":{"uplink":-1,"downlink":2},"extra":{"uplink":99,"downlink":99}},
               "totals":{"business":{"uplink":100,"downlink":200},
                 "excluded":{"uplink":0,"downlink":0},"unknown":{"uplink":1.5,"downlink":0}}}}
            """;
        Assert.True(XrayParser.TryReadProbeCounters(Bytes(json), ["proxy", "duplicate", "bad", "missing"], out var probes));
        Assert.Equal(new XrayCounters(9007199254740993, long.MaxValue), Assert.Single(probes!.Members).Value);
        Assert.Equal("duplicate_counter", probes.UnavailableMembers["duplicate"]);
        Assert.Equal("invalid_counter", probes.UnavailableMembers["bad"]);
        Assert.Equal("missing_counter", probes.UnavailableMembers["missing"]);
        Assert.Equal("invalid_counter", probes.UnavailableTotals["unknown"]);
        Assert.Equal(new XrayCounters(0, 0), probes.Totals["excluded"]);
        Assert.True(XrayParser.TryReadOutboundsCounters(Bytes(json), ["proxy"], out var outbounds));
        Assert.Equal(new XrayCounters(1000, 2000), outbounds!.Counters["proxy"]);
        Assert.False(XrayParser.TryReadProbeCounters(Bytes(json.Replace("\"version\":1", "\"version\":2")), ["proxy"], out _));
        Assert.False(XrayParser.TryReadProbeCounters(Bytes(json.Replace("\"version\":1", "\"version\":1,\"version\":1")), ["proxy"], out _));
        Assert.False(XrayParser.TryReadProbeCounters(Bytes("""{"observatory":{"proxy":{"delay":12}}}"""), ["proxy"], out _));
    }

    [Fact]
    public async Task XrayProbePipelineKeepsIndependentBaselinesNamesAndMissingIntervals()
    {
        var config = Bytes(Change(Configuration, root =>
        {
            root["outbounds"]!.AsArray().Add(JsonNode.Parse(
                """{"tag":"123-proxy-US-1x-1-Synthetic 2x","protocol":"socks","settings":{"address":"127.0.0.1","port":1081}}"""));
            root["routing"] = JsonNode.Parse("""{"balancers":[{"tag":"group","selector":["123-proxy-US-1x"]}]}""");
        }));
        var snapshot = Snapshot() with { Generation = XrayParser.Hash(config) + ":1", XrayProbeExtensionVerified = true };
        Assert.True(XrayParser.TryReadOutboundCatalog(snapshot, config, out var catalog, out _));
        var capture = new ScopeCapture(snapshot, null, []) { XrayOutbounds = catalog };
        using var discovery = new WindowsDiscovery();
        using var ledger = ScopeLedger.CreateNew(History, includeProbes: true, includeDisplayMetadata: true);
        const string tag = "123-proxy-US-1x-1-Synthetic 2x";
        async Task<ScopeCollectorReading> Read(long n, bool missing = false) =>
            await discovery.ReadScopesBoundAsync(() => capture, (_, _) =>
                Task.FromResult<(V2RayMemberStatistics?, XrayCounters?, V2RayProbeStatistics?, V2RayOutboundStatistics?)>((
                    null, null,
                    new(new Dictionary<string, XrayCounters> { ["proxy"] = new(n, n) }
                        .Concat(missing ? Enumerable.Empty<KeyValuePair<string, XrayCounters>>() :
                            new Dictionary<string, XrayCounters> { [tag] = new(n * 2, n * 3) })
                        .ToDictionary(x => x.Key, x => x.Value),
                        missing ? new Dictionary<string, string> { [tag] = "missing_counter" } : new Dictionary<string, string>(),
                        new Dictionary<string, XrayCounters>
                        {
                            ["business"] = new(n * 3, n * 4), ["excluded"] = new(0, 0), ["unknown"] = new(0, 0)
                        }, new Dictionary<string, string>()),
                    new(new Dictionary<string, XrayCounters> { ["proxy"] = new(n * 10, n * 10), [tag] = new(n * 20, n * 20) },
                        new Dictionary<string, string>()))),
                CancellationToken.None, includeProbes: true);
        var first = await Read(100);
        Assert.Equal("available", first.Probes!.State);
        ScopeCollector.Commit(ledger, first.Batch! with { At = Start });
        discovery.AcknowledgePersistence();
        var query = new ScopeQuery("probe_urltest_member", Start.AddMinutes(-1), Start.AddMinutes(2), "UTC");
        Assert.Equal("0", ledger.Query(query).Upload);
        var second = await Read(107);
        ScopeCollector.Commit(ledger, second.Batch! with { At = Start.AddSeconds(1) });
        var history = ledger.Query(query);
        Assert.Equal("21", history.Upload);
        Assert.Equal("28", history.Download);
        var member = Assert.Single(history.Scopes, x => x.Name == "Synthetic 2x");
        Assert.Equal("US-1x", Assert.Single(member.Display!.Groups));
        Assert.Equal(2, history.Scopes.Count);
        Assert.Equal("210", ledger.Query(query with { Basis = "local_proxy" }).Upload);
        var partial = await Read(110, missing: true);
        Assert.Equal("partial", partial.Probes!.State);
        ScopeCollector.Commit(ledger, partial.Batch! with { At = Start.AddSeconds(2) });
        ScopeCollector.Commit(ledger, (await Read(115)).Batch! with { At = Start.AddSeconds(3) });
        var restored = ledger.Query(query);
        Assert.Equal("45", restored.Upload);
        Assert.Equal("60", restored.Download);
        Assert.Equal("16", restored.RecoveredUpload);
        Assert.Contains(restored.Gaps, x => x.Reason == "missing_counter" && !x.IsOpen);
        Assert.Equal(history.Scopes.Select(x => x.ScopeId), restored.Scopes.Select(x => x.ScopeId));
    }

    [Fact]
    public async Task XrayMultiOutboundPipelinePreservesProxyAndCollectsActualLeavesFromOneResponse()
    {
        var config = Bytes(Change(Configuration, root =>
        {
            root["outbounds"]!.AsArray().Add(JsonNode.Parse(
                """{"tag":"123-proxy-US-1x-1-Synthetic 2x","protocol":"socks","settings":{"address":"127.0.0.1","port":1081}}"""));
            root["routing"] = JsonNode.Parse("""{"balancers":[{"tag":"group","selector":["123-proxy-US-1x"]}]}""");
        }));
        var snapshot = Snapshot() with { Generation = XrayParser.Hash(config) + ":1" };
        Assert.True(XrayParser.TryReadOutboundCatalog(snapshot, config, out var catalog, out _));
        var capture = new ScopeCapture(snapshot, null, []) { XrayOutbounds = catalog };
        using var discovery = new WindowsDiscovery();
        using var ledger = ScopeLedger.CreateNew(History, includeProbes: true, includeDisplayMetadata: true);
        ScopeCollector.Commit(ledger, ScopeCollector.ProjectLegacy(snapshot, new(500, 0), Start.AddSeconds(-1), false));
        var oldProxy = Assert.Single(ledger.GetScopes("legacy_proxy")).Id;
        async Task<ScopeCollectorReading> Read(string members, long proxy) =>
            await discovery.ReadScopesBoundAsync(() => capture, (_, _) =>
            {
                Assert.True(XrayParser.TryReadOutboundsCounters(Bytes("{\"stats\":{\"outbound\":{\"proxy\":{\"uplink\":" +
                    proxy.ToString(CultureInfo.InvariantCulture) + ",\"downlink\":0}," + members + "}}}"),
                    catalog!.Candidates.Keys.ToArray(), out var counters));
                return Task.FromResult<(V2RayMemberStatistics?, XrayCounters?, V2RayProbeStatistics?, V2RayOutboundStatistics?)>((null, null, null, counters));
            }, CancellationToken.None, includeProbes: true);
        const string member = "\"123-proxy-US-1x-1-Synthetic 2x\"";
        var first = await Read(member + ":{\"uplink\":100,\"downlink\":200}", 500);
        Assert.Equal("connected", first.State);
        Assert.False(first.Scope!.IsPartial);
        Assert.Equal("unsupported", first.Probes!.State);
        var firstCommit = ScopeCollector.Commit(ledger, first.Batch! with { At = Start });
        Assert.Equal(oldProxy, Assert.Single(ledger.GetScopes("legacy_proxy")).Id);
        Assert.Equal("recorded", firstCommit.Scopes[oldProxy]);
        discovery.AcknowledgePersistence();
        var next = await Read(member + ":{\"uplink\":113,\"downlink\":229}", 507);
        ScopeCollector.Commit(ledger, next.Batch! with { At = Start.AddSeconds(1) });
        var query = new ScopeQuery("local_proxy", Start.AddMinutes(-1), Start.AddMinutes(2), "UTC");
        var history = ledger.Query(query);
        Assert.Equal("20", history.Upload);
        Assert.Equal("29", history.Download);
        Assert.Equal("91", history.Display!.EstimatedBilledBytes);
        Assert.Equal("7", ledger.Query(query with { Basis = "legacy_proxy" }).Upload);
        Assert.Equal("13", ledger.Query(query with { Basis = "actual_member" }).Upload);
        Assert.Equal(2, history.Scopes.Count);
        var partial = await Read(member + ":{\"uplink\":120}", 510);
        Assert.Equal("partial", partial.State);
        ScopeCollector.Commit(ledger, partial.Batch! with { At = Start.AddSeconds(2) });
        Assert.Equal("23", ledger.Query(query).Upload);
        Assert.Contains(ledger.Query(query).Gaps, x => x.Reason == "missing_counter" && x.IsOpen);
        Assert.Null(Assert.Single(history.Scopes, x => x.Name.Contains("Synthetic 2x", StringComparison.Ordinal)).Display!.MergeKey);
        var recovered = await Read(member + ":{\"uplink\":130,\"downlink\":240}", 513);
        ScopeCollector.Commit(ledger, recovered.Batch! with { At = Start.AddSeconds(3) });
        var resumed = ledger.Query(query);
        Assert.Equal("43", resumed.Upload);
        Assert.Equal("40", resumed.Download);
        Assert.Equal("17", resumed.RecoveredUpload);
        Assert.Contains(resumed.Gaps, x => x.Reason == "missing_counter" && !x.IsOpen);
    }

    [Fact]
    public async Task XrayPartialDiscoveryPersistsCoverageAndDoesNotMixPreviousMemberCounters()
    {
        CreateProfiles(Profile());
        var config = Change(Configuration, root =>
        {
            root["outbounds"]!.AsArray().Add(new JsonObject { ["tag"] = "other", ["protocol"] = "vless" });
            root["routing"] = JsonNode.Parse("""{"balancers":[{"tag":"group","selector":["other"]}]}""");
        });
        Assert.Equal("Same synthetic name", WindowsDiscovery.ReadProfile(Profiles, "A", Bytes(Gui),
            Bytes(config), out var configuration));
        var snapshot = Snapshot() with { Scope = configuration.Scope };
        var capture = new ScopeCapture(snapshot, null, []);
        using var discovery = new WindowsDiscovery();
        using var ledger = ScopeLedger.CreateNew(History, includeProbes: true);
        async Task<ScopeCollectorReading> Read(long up, long down) =>
            await discovery.ReadScopesBoundAsync(() => capture,
                (_, _) => Task.FromResult<(V2RayMemberStatistics?, XrayCounters?, V2RayProbeStatistics?, V2RayOutboundStatistics?)>((null, new(up, down), null, null)),
                CancellationToken.None, includeProbes: true);
        var previous = ScopeCollector.ProjectWithProbes(ScopeFixture(), ScopeCounters(900, 800),
            new(new Dictionary<string, XrayCounters>(), new Dictionary<string, string>(),
                new Dictionary<string, XrayCounters>(), new Dictionary<string, string>()), Start.AddSeconds(-1));
        ScopeCollector.Commit(ledger, previous);
        var first = await Read(100, 200);
        Assert.Equal("connected", first.State);
        Assert.Equal(configuration.Scope, first.Scope);
        Assert.Equal("unsupported", first.Probes!.State);
        Assert.All(ScopeCollector.Commit(ledger, first.Batch! with { At = Start }).Scopes.Values,
            status => Assert.Equal("baseline", status));
        discovery.AcknowledgePersistence();
        var second = await Read(113, 229);
        ScopeCollector.Commit(ledger, second.Batch! with { At = Start.AddSeconds(1) });
        var result = ledger.Query(new ScopeQuery("legacy_proxy", Start.AddMinutes(-1), Start.AddMinutes(2), "UTC"));
        Assert.Equal("13", result.Upload);
        Assert.Equal("29", result.Download);
        Assert.All(result.Records, row => Assert.True(row.PartialCoverage));
        Assert.Contains(result.Gaps, gap => gap.Reason == "uncollected_outbounds");
        Assert.Equal("0", ledger.Query(new ScopeQuery("actual_member", Start.AddMinutes(-1), Start.AddMinutes(2), "UTC")).Upload);
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
        var probes = MonitorStartup.Command(executable, root, scopeMode, probeMode: true);
        Assert.Contains(" --scope-ledger --probe-ledger ", probes);
        Assert.EndsWith("--data-dir \"" + root + "\"", probes);
        Assert.NotEqual(command, probes);
    }

    private static CoreComponent SyntheticComponent(byte[] bytes) => new("synthetic", "synthetic-1", "synthetic.exe",
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), SingboxParser.ClientVersion,
        "win-x64", "synthetic-source", "synthetic-only", bytes.Length);

    [Fact]
    public void ComponentCatalogReusesApprovedIdentitiesWithoutClaimingDeployment()
    {
        Assert.Equal(2, CoreComponents.Catalog.Count);
        var singbox = Assert.Single(CoreComponents.Catalog, x => x.Id == "sing-box-statistics");
        Assert.True(WindowsDiscovery.IsApprovedExtension(singbox.Sha256, true));
        Assert.False(WindowsDiscovery.IsApprovedExtension(singbox.Sha256, false));
        Assert.Equal(WindowsDiscovery.ApprovedProbeXraySha256,
            Assert.Single(CoreComponents.Catalog, x => x.Id == "xray-probes").Sha256);
        Assert.All(CoreComponents.Catalog, x =>
        {
            Assert.Equal(SingboxParser.ClientVersion, x.ClientVersion);
            Assert.Equal("win-x64", x.Runtime);
            Assert.Null(x.ExpectedBytes); // Release ZIP sizes must not become EXE sizes.
            Assert.False(string.IsNullOrWhiteSpace(x.Version));
            Assert.False(string.IsNullOrWhiteSpace(x.License));
        });
        Assert.Equal(CoreFileState.UnknownComponent,
            CoreComponents.Verify("untrusted", directory, SingboxParser.ClientVersion, "win-x64").State);
    }

    [Fact]
    public void ComponentFileIdentityMatchIsReadOnlyAndDoesNotRunTheFile()
    {
        Directory.CreateDirectory(directory);
        var content = Bytes("synthetic non-executable core contents");
        var path = Path.Combine(directory, "synthetic.exe");
        File.WriteAllBytes(path, content);
        var written = File.GetLastWriteTimeUtc(path);
        var result = CoreComponents.VerifyFile(SyntheticComponent(content), path, SingboxParser.ClientVersion, "win-x64");
        Assert.Equal(CoreFileState.IdentityMatched, result.State);
        Assert.Equal(content.Length, result.ActualBytes);
        foreach (var component in CoreComponents.Catalog)
            Assert.Equal(CoreFileState.DigestMismatch,
                CoreComponents.Verify(component.Id, path, SingboxParser.ClientVersion, "win-x64").State);
        Assert.Equal(content, File.ReadAllBytes(path));
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        Assert.Single(Directory.GetFiles(directory));
    }

    [Theory]
    [InlineData("truncated", nameof(CoreFileState.SizeMismatch))]
    [InlineData("extended", nameof(CoreFileState.SizeMismatch))]
    [InlineData("different-version", nameof(CoreFileState.DigestMismatch))]
    public void ComponentFileMismatchNeverRepairsOrOverwrites(string mutation, string expected)
    {
        Directory.CreateDirectory(directory);
        var original = Bytes("synthetic-version-1");
        var changed = mutation switch
        {
            "truncated" => original[..^1],
            "extended" => [.. original, (byte)'!'],
            _ => Bytes("synthetic-version-2")
        };
        var path = Path.Combine(directory, "synthetic.exe");
        File.WriteAllBytes(path, changed);
        Assert.Equal(expected,
            CoreComponents.VerifyFile(SyntheticComponent(original), path, SingboxParser.ClientVersion, "win-x64").State.ToString());
        Assert.Equal(changed, File.ReadAllBytes(path));
    }

    [Fact]
    public void ComponentFileWithNoPublishedExeSizeStillRequiresTheExactDigest()
    {
        Directory.CreateDirectory(directory);
        var bytes = Bytes("synthetic");
        var path = Path.Combine(directory, "synthetic.exe");
        File.WriteAllBytes(path, bytes);
        var component = SyntheticComponent(bytes) with { ExpectedBytes = null };
        Assert.Equal(CoreFileState.IdentityMatched,
            CoreComponents.VerifyFile(component, path, SingboxParser.ClientVersion, "win-x64").State);
        File.AppendAllText(path, "different-version");
        Assert.Equal(CoreFileState.DigestMismatch,
            CoreComponents.VerifyFile(component, path, SingboxParser.ClientVersion, "win-x64").State);
    }

    [Theory]
    [InlineData("missing", nameof(CoreFileState.Missing))]
    [InlineData("client", nameof(CoreFileState.UnsupportedClient))]
    [InlineData("runtime", nameof(CoreFileState.UnsupportedRuntime))]
    [InlineData("digest", nameof(CoreFileState.InvalidDescriptor))]
    [InlineData("size", nameof(CoreFileState.InvalidDescriptor))]
    public void ComponentUnavailableInputsRemainUnavailableWithoutCreatingFiles(string kind, string expected)
    {
        var component = SyntheticComponent(Bytes("synthetic"));
        if (kind == "digest") component = component with { Sha256 = "not-a-digest" };
        if (kind == "size") component = component with { ExpectedBytes = 0 };
        var result = CoreComponents.VerifyFile(component, Path.Combine(directory, "missing.exe"),
            kind == "client" ? "unknown-client" : SingboxParser.ClientVersion, kind == "runtime" ? "linux-x64" : "win-x64");
        Assert.Equal(expected, result.State.ToString());
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void ComponentUnreadablePathDoesNotEscapeAsAnUncaughtIoError()
    {
        Directory.CreateDirectory(directory);
        Assert.Equal(CoreFileState.Unreadable,
            CoreComponents.VerifyFile(SyntheticComponent(Bytes("synthetic")), directory, SingboxParser.ClientVersion, "win-x64").State);
        Assert.Empty(Directory.GetFiles(directory));
    }

    private static readonly Uri ComponentRelease = new(
        "https://github.com/lengmh/v2rayN-Monitor/releases/download/v0.0.0-synthetic/synthetic-win-x64.zip");
    private static readonly byte[] SyntheticCoreBytes = Bytes("synthetic core version 1; not an executable");
    private static readonly byte[] SyntheticLicenseBytes = Bytes("Synthetic fixture license only");
    private static readonly byte[] ExistingCoreBytes = Bytes("previous installed synthetic core");
    private static readonly DateTime ExistingCoreWritten = new(2020, 1, 2, 3, 4, 6, DateTimeKind.Utc);

    private static string ComponentDigest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static byte[] ComponentZip(params (string Name, byte[] Content, int Attributes)[] files)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Name, CompressionLevel.NoCompression);
                entry.ExternalAttributes = file.Attributes;
                using var content = entry.Open();
                content.Write(file.Content);
            }
        return output.ToArray();
    }

    private static byte[] ValidComponentZip() => ComponentZip(
        ("synthetic.exe", SyntheticCoreBytes, 0), ("LICENSE.txt", SyntheticLicenseBytes, 0));

    private static CoreComponentAsset ComponentAsset(byte[] archive) => new(ComponentRelease,
        archive.Length, ComponentDigest(archive),
        [
            new("synthetic.exe", SyntheticCoreBytes.Length, ComponentDigest(SyntheticCoreBytes)),
            new("LICENSE.txt", SyntheticLicenseBytes.Length, ComponentDigest(SyntheticLicenseBytes))
        ]);

    private string ExistingComponent()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "installed-synthetic.exe");
        File.WriteAllBytes(path, ExistingCoreBytes);
        File.SetLastWriteTimeUtc(path, ExistingCoreWritten);
        return path;
    }

    private void AssertExistingComponentUnchanged(string path)
    {
        Assert.Equal(ExistingCoreBytes, File.ReadAllBytes(path));
        Assert.Equal(ExistingCoreWritten, File.GetLastWriteTimeUtc(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(directory));
        Assert.Empty(Directory.GetDirectories(directory));
    }

    private async Task<CorePreparation> PrepareComponent(CoreComponentAsset? asset,
        ComponentDownloadHandler handler, string? existingFile = null, CancellationToken token = default,
        TimeSpan? timeout = null, CoreComponent? component = null)
    {
        var result = await CoreComponentDownloads.PrepareAsync(component ?? SyntheticComponent(SyntheticCoreBytes),
            asset, existingFile, SingboxParser.ClientVersion, "win-x64", handler, token, timeout);
        if (result.StagingDirectory is { } stage) componentStages.Add(stage);
        return result;
    }

    private void AssertComponentFailure(CorePreparation result, CorePreparationState state, string existingFile)
    {
        Assert.Equal(state, result.State);
        Assert.Null(result.FilePath);
        Assert.Null(result.StagingDirectory);
        AssertExistingComponentUnchanged(existingFile);
    }

    private static HttpResponseMessage ComponentResponse(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes)
    };

    private sealed class ComponentDownloadHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        internal List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Headers.Authorization);
            Assert.Null(request.Headers.Referrer);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.False(request.Headers.Contains("Proxy-Authorization"));
            Assert.Null(request.Content);
            Requests.Add(request.RequestUri!);
            return Task.FromResult(send(request));
        }
    }

    // Non-seekable bodies avoid StreamContent synthesizing a Content-Length header.
    private sealed class ComponentBodyStream(byte[] bytes, int chunkSize = 65536, bool knownLength = false) : Stream
    {
        internal int BytesRead { get; private set; }
        internal Func<CancellationToken, Task>? BeforeRead { get; init; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => knownLength ? bytes.Length : throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (BeforeRead is not null) await BeforeRead(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(Math.Min(buffer.Length, chunkSize), bytes.Length - BytesRead);
            bytes.AsMemory(BytesRead, count).CopyTo(buffer);
            BytesRead += count;
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ComponentDownloadProductionCatalogHasNoIndependentPublishedAsset()
    {
        var existing = ExistingComponent();
        foreach (var component in CoreComponents.Catalog)
        {
            Assert.Null(CoreComponentDownloads.PublishedAsset(component.Id));
            using var handler = new ComponentDownloadHandler(_ => throw new Xunit.Sdk.XunitException("Network is forbidden."));
            AssertComponentFailure(await PrepareComponent(CoreComponentDownloads.PublishedAsset(component.Id),
                handler, existing, component: component), CorePreparationState.AssetUnavailable, existing);
            Assert.Empty(handler.Requests);
        }
        var unknown = await CoreComponentDownloads.PrepareAsync("unknown-synthetic-component", existing,
            SingboxParser.ClientVersion, "win-x64", CancellationToken.None);
        Assert.Equal(CorePreparationState.UnknownComponent, unknown.State);
        Assert.Null(unknown.FilePath);
        AssertExistingComponentUnchanged(existing);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComponentDownloadReusesExactExistingFileWithoutNetworkOrWrites(bool assetAvailable)
    {
        Directory.CreateDirectory(directory);
        var existing = Path.Combine(directory, "synthetic.exe");
        File.WriteAllBytes(existing, SyntheticCoreBytes);
        File.SetLastWriteTimeUtc(existing, ExistingCoreWritten);
        using var handler = new ComponentDownloadHandler(_ => throw new Xunit.Sdk.XunitException("Network is forbidden."));
        var result = await PrepareComponent(assetAvailable ? ComponentAsset(ValidComponentZip()) : null, handler, existing);
        Assert.Equal(CorePreparationState.ExistingFileMatched, result.State);
        Assert.Equal(CoreFileState.IdentityMatched, result.FileState);
        Assert.Equal(existing, result.FilePath);
        Assert.Null(result.StagingDirectory);
        Assert.Empty(handler.Requests);
        Assert.Equal(SyntheticCoreBytes, File.ReadAllBytes(existing));
        Assert.Equal(ExistingCoreWritten, File.GetLastWriteTimeUtc(existing));
        Assert.Equal(new[] { existing }, Directory.GetFiles(directory));
    }

    [Theory]
    [InlineData("client", nameof(CoreFileState.UnsupportedClient))]
    [InlineData("runtime", nameof(CoreFileState.UnsupportedRuntime))]
    [InlineData("version", nameof(CoreFileState.InvalidDescriptor))]
    [InlineData("digest", nameof(CoreFileState.InvalidDescriptor))]
    public async Task ComponentDownloadRejectsUnsupportedTargetsBeforeNetwork(string kind, string fileState)
    {
        var existing = ExistingComponent();
        var component = SyntheticComponent(SyntheticCoreBytes);
        if (kind == "version") component = component with { Version = "" };
        if (kind == "digest") component = component with { Sha256 = "untrusted" };
        using var handler = new ComponentDownloadHandler(_ => throw new Xunit.Sdk.XunitException("Network is forbidden."));
        var result = await CoreComponentDownloads.PrepareAsync(component, ComponentAsset(ValidComponentZip()),
            existing, kind == "client" ? "unknown-client" : SingboxParser.ClientVersion,
            kind == "runtime" ? "linux-x64" : "win-x64", handler, CancellationToken.None);
        AssertComponentFailure(result, kind is "client" or "runtime"
            ? CorePreparationState.UnsupportedTarget : CorePreparationState.InvalidDescriptor, existing);
        Assert.Equal(fileState, result.FileState.ToString());
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComponentDownloadVerifiesFlatAllowlistAndUsesFreshUnauthenticatedRedirectRequests(bool redirect)
    {
        var existing = ExistingComponent();
        var archive = ValidComponentZip();
        var cdn = new Uri("https://release-assets.githubusercontent.com/synthetic/asset?signature=synthetic-only");
        using var handler = new ComponentDownloadHandler(request =>
        {
            if (redirect && request.RequestUri == ComponentRelease)
            {
                var response = new HttpResponseMessage(HttpStatusCode.Found);
                response.Headers.Location = cdn;
                response.Headers.TryAddWithoutValidation("Set-Cookie", "synthetic=must-not-be-forwarded; Secure");
                return response;
            }
            return ComponentResponse(archive);
        });
        var result = await PrepareComponent(ComponentAsset(archive), handler, existing);
        Assert.Equal(CorePreparationState.DownloadVerified, result.State);
        Assert.NotNull(result.StagingDirectory);
        Assert.Equal(Path.Combine(result.StagingDirectory!, "synthetic.exe"), result.FilePath);
        Assert.Equal(SyntheticCoreBytes, File.ReadAllBytes(result.FilePath!));
        Assert.Equal(SyntheticLicenseBytes, File.ReadAllBytes(Path.Combine(result.StagingDirectory!, "LICENSE.txt")));
        Assert.Equal(new[] { "LICENSE.txt", "synthetic.exe" },
            Directory.GetFiles(result.StagingDirectory!).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Empty(Directory.GetDirectories(result.StagingDirectory!));
        Assert.Equal(redirect ? new[] { ComponentRelease, cdn } : [ComponentRelease], handler.Requests);
        AssertExistingComponentUnchanged(existing);
    }

    [Theory]
    [InlineData("http://github.com/lengmh/v2rayN-Monitor/releases/download/v1/asset.zip")]
    [InlineData("https://github.com/other/v2rayN-Monitor/releases/download/v1/asset.zip")]
    [InlineData("https://github.com/lengmh/v2rayN-Monitor/releases/latest/download/asset.zip")]
    [InlineData("https://github.com/lengmh/v2rayN-Monitor/releases/download/latest/asset.zip")]
    [InlineData("https://github.com/lengmh/v2rayN-Monitor/releases/download/v1/nested/asset.zip")]
    [InlineData("https://github.com/lengmh/v2rayN-Monitor/releases/download/v1/asset.exe")]
    [InlineData("https://github.com/lengmh/v2rayN-Monitor/releases/download/v1/asset.zip?token=synthetic")]
    [InlineData("https://synthetic:secret@github.com/lengmh/v2rayN-Monitor/releases/download/v1/asset.zip")]
    [InlineData("https://github.com:444/lengmh/v2rayN-Monitor/releases/download/v1/asset.zip")]
    [InlineData("https://github.com/lengmh/v2rayN-Monitor/releases/download/v1/asset.zip#fragment")]
    [InlineData("https://github.com.synthetic.invalid/lengmh/v2rayN-Monitor/releases/download/v1/asset.zip")]
    public async Task ComponentDownloadRejectsNonFixedReleaseSourcesBeforeNetwork(string url)
    {
        var existing = ExistingComponent();
        var asset = ComponentAsset(ValidComponentZip()) with { Url = new Uri(url) };
        using var handler = new ComponentDownloadHandler(_ => throw new Xunit.Sdk.XunitException("Network is forbidden."));
        AssertComponentFailure(await PrepareComponent(asset, handler, existing),
            CorePreparationState.InvalidDescriptor, existing);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("outer-digest")]
    [InlineData("outer-size")]
    [InlineData("outer-cap")]
    [InlineData("no-files")]
    [InlineData("duplicate-name")]
    [InlineData("reserved-partial")]
    [InlineData("nested-allowlist")]
    [InlineData("file-digest")]
    [InlineData("file-size")]
    [InlineData("component-digest")]
    [InlineData("component-size")]
    public async Task ComponentDownloadRejectsInvalidAssetIdentitiesBeforeNetwork(string kind)
    {
        var existing = ExistingComponent();
        var asset = ComponentAsset(ValidComponentZip());
        var files = asset.Files.ToArray();
        asset = kind switch
        {
            "outer-digest" => asset with { Sha256 = "not-a-digest" },
            "outer-size" => asset with { Bytes = 0 },
            "outer-cap" => asset with { Bytes = 256L * 1024 * 1024 + 1 },
            "no-files" => asset with { Files = [] },
            "duplicate-name" => asset with { Files = [.. files, files[1] with { Name = "license.TXT" }] },
            "reserved-partial" => asset with { Files = [.. files, files[1] with { Name = "download.partial" }] },
            "nested-allowlist" => asset with { Files = [files[0], files[1] with { Name = "nested/LICENSE.txt" }] },
            "file-digest" => asset with { Files = [files[0], files[1] with { Sha256 = "invalid" }] },
            "file-size" => asset with { Files = [files[0], files[1] with { Bytes = 0 }] },
            "component-digest" => asset with { Files = [files[0] with { Sha256 = ComponentDigest(Bytes("wrong version")) }, files[1]] },
            _ => asset with { Files = [files[0] with { Bytes = files[0].Bytes + 1 }, files[1]] }
        };
        using var handler = new ComponentDownloadHandler(_ => throw new Xunit.Sdk.XunitException("Network is forbidden."));
        AssertComponentFailure(await PrepareComponent(asset, handler, existing),
            CorePreparationState.InvalidDescriptor, existing);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("http://release-assets.githubusercontent.com/synthetic.zip")]
    [InlineData("https://github.com/lengmh/v2rayN-Monitor/releases/download/v1/other.zip")]
    [InlineData("https://objects.githubusercontent.com/synthetic.zip")]
    [InlineData("https://release-assets.githubusercontent.com.synthetic.invalid/synthetic.zip")]
    [InlineData("https://synthetic:secret@release-assets.githubusercontent.com/synthetic.zip")]
    [InlineData("https://release-assets.githubusercontent.com:444/synthetic.zip")]
    [InlineData("https://release-assets.githubusercontent.com/synthetic.zip#fragment")]
    [InlineData("/relative-asset.zip")]
    [InlineData("missing")]
    [InlineData("loop")]
    public async Task ComponentDownloadRejectsUntrustedOrUnboundedRedirects(string target)
    {
        var existing = ExistingComponent();
        using var handler = new ComponentDownloadHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            if (target != "missing") response.Headers.Location = new Uri(target == "loop"
                ? "https://release-assets.githubusercontent.com/synthetic.zip" : target, UriKind.RelativeOrAbsolute);
            return response;
        });
        AssertComponentFailure(await PrepareComponent(ComponentAsset(ValidComponentZip()), handler, existing),
            CorePreparationState.DownloadFailed, existing);
        Assert.Equal(target == "loop" ? 4 : 1, handler.Requests.Count);
    }

    [Theory]
    [InlineData("404", nameof(CorePreparationState.DownloadFailed))]
    [InlineData("500", nameof(CorePreparationState.DownloadFailed))]
    [InlineData("partial-response", nameof(CorePreparationState.DownloadFailed))]
    [InlineData("request-error", nameof(CorePreparationState.DownloadFailed))]
    [InlineData("content-encoding", nameof(CorePreparationState.InvalidArchive))]
    [InlineData("content-length", nameof(CorePreparationState.InvalidArchive))]
    [InlineData("truncated", nameof(CorePreparationState.InvalidArchive))]
    [InlineData("outer-digest", nameof(CorePreparationState.InvalidArchive))]
    [InlineData("not-a-zip", nameof(CorePreparationState.InvalidArchive))]
    public async Task ComponentDownloadRejectsHttpAndArchiveCorruptionWithoutTouchingInstallation(string kind, string state)
    {
        var existing = ExistingComponent();
        var archive = kind == "not-a-zip" ? Bytes("synthetic invalid ZIP") : ValidComponentZip();
        var asset = ComponentAsset(archive);
        using var handler = new ComponentDownloadHandler(_ =>
        {
            if (kind == "request-error") throw new HttpRequestException("Synthetic HTTP failure");
            var body = kind == "truncated" ? archive[..^1] : archive.ToArray();
            if (kind == "outer-digest") body[0] ^= 1;
            var response = ComponentResponse(body);
            if (kind is "404" or "500") response.StatusCode = (HttpStatusCode)int.Parse(kind, CultureInfo.InvariantCulture);
            if (kind == "partial-response") response.StatusCode = HttpStatusCode.PartialContent;
            if (kind == "content-encoding") response.Content.Headers.ContentEncoding.Add("gzip");
            if (kind == "content-length") response.Content.Headers.ContentLength = archive.Length + 1;
            if (kind == "truncated") response.Content.Headers.ContentLength = archive.Length;
            return response;
        });
        var result = await PrepareComponent(asset, handler, existing);
        AssertComponentFailure(result, Enum.Parse<CorePreparationState>(state), existing);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("case-alias")]
    [InlineData("traversal")]
    [InlineData("absolute")]
    [InlineData("installed-absolute")]
    [InlineData("backslash")]
    [InlineData("nested")]
    [InlineData("alternate-stream")]
    [InlineData("symlink")]
    [InlineData("reparse-point")]
    [InlineData("directory")]
    [InlineData("wrong-version")]
    [InlineData("short-file")]
    [InlineData("long-file")]
    [InlineData("sidecar-digest")]
    public async Task ComponentDownloadRejectsUnlistedPathsLinksAndWrongFilesEvenWithValidOuterDigest(string kind)
    {
        var existing = ExistingComponent();
        var core = SyntheticCoreBytes.ToArray();
        if (kind == "wrong-version") core[23] ^= 1; // Same size, different file identity.
        if (kind == "short-file") core = core[..^1];
        if (kind == "long-file") core = [.. core, (byte)'!'];
        var name = kind switch
        {
            "traversal" => "../synthetic.exe",
            "absolute" => "/synthetic.exe",
            "installed-absolute" => existing,
            "backslash" => @"..\synthetic.exe",
            "nested" => "nested/synthetic.exe",
            "alternate-stream" => "synthetic.exe:stream",
            _ => "synthetic.exe"
        };
        var attributes = kind switch
        {
            "symlink" => unchecked((int)0xA1FF0000),
            "reparse-point" => (int)FileAttributes.ReparsePoint,
            "directory" => (int)FileAttributes.Directory,
            _ => 0
        };
        var license = SyntheticLicenseBytes.ToArray();
        if (kind == "sidecar-digest") license[0] ^= 1;
        var files = new List<(string Name, byte[] Content, int Attributes)> { (name, core, attributes) };
        if (kind != "missing") files.Add(("LICENSE.txt", license, 0));
        if (kind == "extra") files.Add(("unlisted.exe", Bytes("synthetic unlisted file"), 0));
        if (kind == "duplicate") files[1] = files[0];
        if (kind == "case-alias") files[1] = ("SYNTHETIC.EXE", core, 0);
        var archive = ComponentZip(files.ToArray());
        using var handler = new ComponentDownloadHandler(_ => ComponentResponse(archive));
        AssertComponentFailure(await PrepareComponent(ComponentAsset(archive), handler, existing),
            CorePreparationState.InvalidArchive, existing);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComponentDownloadBoundsStreamingBodyWithAbsentOrMisleadingContentLength(bool advertisedLength)
    {
        var existing = ExistingComponent();
        var archive = ValidComponentZip();
        var oversized = new byte[archive.Length + 2 * 1024 * 1024];
        archive.CopyTo(oversized, 0);
        using var body = new ComponentBodyStream(oversized);
        using var handler = new ComponentDownloadHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
            if (advertisedLength) response.Content.Headers.ContentLength = archive.Length;
            else Assert.Null(response.Content.Headers.ContentLength);
            return response;
        });
        AssertComponentFailure(await PrepareComponent(ComponentAsset(archive), handler, existing),
            CorePreparationState.InvalidArchive, existing);
        Assert.InRange(body.BytesRead, archive.Length + 1, archive.Length + 65536);
        Assert.True(body.BytesRead < oversized.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComponentDownloadCancellationAndDeadlineInterruptAnAlreadyStartedBody(bool timeout)
    {
        var existing = ExistingComponent();
        var archive = ValidComponentZip();
        using var cancellation = new CancellationTokenSource();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interrupted = false;
        ComponentBodyStream? body = null;
        body = new ComponentBodyStream(archive, chunkSize: 8)
        {
            BeforeRead = async token =>
            {
                if (body!.BytesRead == 0) return;
                blocked.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { interrupted = true; throw; }
            }
        };
        using var stream = body;
        using var handler = new ComponentDownloadHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
        });
        var preparation = PrepareComponent(ComponentAsset(archive), handler, existing, cancellation.Token,
            timeout ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(10));
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (!timeout) cancellation.Cancel();
        var result = await preparation.WaitAsync(TimeSpan.FromSeconds(5));
        AssertComponentFailure(result, timeout ? CorePreparationState.TimedOut : CorePreparationState.Cancelled, existing);
        Assert.Equal(timeout, !cancellation.IsCancellationRequested);
        Assert.True(interrupted);
        Assert.InRange(stream.BytesRead, 1, archive.Length - 1);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComponentFileHashCancellationInterruptsAnAlreadyStartedRead(bool timeout)
    {
        using var cancellation = new CancellationTokenSource();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ComponentBodyStream? stream = null;
        stream = new ComponentBodyStream(SyntheticCoreBytes, chunkSize: 8, knownLength: true)
        {
            BeforeRead = async token =>
            {
                if (stream!.BytesRead == 0) return;
                blocked.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };
        using var input = stream;
        if (timeout) cancellation.CancelAfter(TimeSpan.FromMilliseconds(500));
        var verification = CoreComponents.VerifyStreamAsync(SyntheticComponent(SyntheticCoreBytes), input, cancellation.Token);
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (!timeout) cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verification.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.InRange(input.BytesRead, 1, SyntheticCoreBytes.Length - 1);
    }

    [Fact]
    public async Task ComponentFileAsyncVerificationHonorsCancellationBeforeOpeningTheFile()
    {
        var existing = ExistingComponent();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CoreComponents.VerifyFileAsync(
            SyntheticComponent(SyntheticCoreBytes), existing, SingboxParser.ClientVersion, "win-x64", cancellation.Token));
        AssertExistingComponentUnchanged(existing);
    }

    [Fact]
    public async Task ComponentDownloadRetriesUseIndependentStagingAndDoNotDamageOtherPreparations()
    {
        var existing = ExistingComponent();
        var archive = ValidComponentZip();
        using (var failedHandler = new ComponentDownloadHandler(_ => ComponentResponse(Bytes("synthetic corrupt download"))))
            AssertComponentFailure(await PrepareComponent(ComponentAsset(archive), failedHandler, existing),
                CorePreparationState.InvalidArchive, existing);
        using var firstHandler = new ComponentDownloadHandler(_ => ComponentResponse(archive));
        var first = await PrepareComponent(ComponentAsset(archive), firstHandler, existing);
        Assert.Equal(CorePreparationState.DownloadVerified, first.State);
        using var retryHandler = new ComponentDownloadHandler(_ => ComponentResponse(archive));
        var retry = await PrepareComponent(ComponentAsset(archive), retryHandler, existing);
        Assert.Equal(CorePreparationState.DownloadVerified, retry.State);
        Assert.NotEqual(first.StagingDirectory, retry.StagingDirectory);
        Assert.NotEqual(first.FilePath, retry.FilePath);
        Assert.Equal(SyntheticCoreBytes, File.ReadAllBytes(first.FilePath!));
        Assert.Equal(SyntheticCoreBytes, File.ReadAllBytes(retry.FilePath!));
        AssertExistingComponentUnchanged(existing);
    }

    private sealed class InstallFixture
    {
        internal readonly string Root, ClientPath, DataRoot, Candidate, Target;
        internal readonly CoreComponent Component;
        internal CoreInstallObservation Observation;
        internal Action<CoreInstallCheckpoint>? Fault;
        private readonly Dictionary<string, byte[]> untouched = [];
        internal InstallFixture(string root, string id = "sing-box-statistics", bool appData = false, bool existing = true)
        {
            Root = root;
            DataRoot = Path.Combine(root, appData ? "appdata/v2rayN" : "portable");
            ClientPath = Path.Combine(appData ? Path.Combine(root, "program") : DataRoot, "v2rayN.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(ClientPath)!);
            File.WriteAllBytes(ClientPath, Bytes("synthetic client identity; never executable"));
            Component = CoreComponents.Catalog.Single(x => x.Id == id) with
            { Sha256 = ComponentDigest(SyntheticCoreBytes), ExpectedBytes = SyntheticCoreBytes.Length };
            Target = Path.Combine(DataRoot, "bin", id == "sing-box-statistics" ? "sing_box" : "xray", Component.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
            if (existing) File.WriteAllBytes(Target, ExistingCoreBytes);
            Candidate = Path.Combine(root, "candidate.bin");
            File.WriteAllBytes(Candidate, SyntheticCoreBytes);
            Observation = new(ClientPath, DataRoot, Path.Combine(root, "appdata/v2rayN"), Component.ClientVersion,
                "win-x64", [new(101, 1, Start, ClientPath, "synthetic command line must not persist", "v2rayN.exe")]);
            foreach (var relative in new[] { "monitor/history.db", "monitor/provider.db", "monitor/settings.json",
                "portable-not-selected/bin/xray/xray.exe", "data/guiNConfig.json", "data/monitor-stats.json" })
            {
                var path = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                untouched[path] = Bytes("synthetic preserved " + relative);
                File.WriteAllBytes(path, untouched[path]);
            }
            var sidecar = Path.Combine(Path.GetDirectoryName(Target)!, "monitor-stats.json");
            untouched[sidecar] = Bytes("synthetic existing sidecar");
            File.WriteAllBytes(sidecar, untouched[sidecar]);
        }
        internal CoreComponentInstaller Installer() => new(Component, () => Observation, point => Fault?.Invoke(point));
        internal string Backup(Guid id) => Path.Combine(Path.GetDirectoryName(Target)!, ".monitor-core-backups", id.ToString("N"));
        internal void Preserved()
        {
            foreach (var (path, bytes) in untouched) Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Equal(SyntheticCoreBytes, File.ReadAllBytes(Candidate));
        }
        internal void Running()
        {
            Observation = Observation with { Processes = [.. Observation.Processes,
                new(202, 101, Start.AddSeconds(1), Target, "synthetic run", Component.FileName)] };
        }
        internal void Stopped() => Observation = Observation with { Processes = [Observation.Processes[0]] };
    }
    private InstallFixture Installation(string id = "sing-box-statistics", bool appData = false, bool existing = true) =>
        new(Path.Combine(directory, "install-" + Guid.NewGuid().ToString("N")), id, appData, existing);
    private static async Task<CoreInstallPlan> Ready(CoreComponentInstaller installer, InstallFixture files)
    {
        var result = await installer.PrepareAsync(files.Candidate, CancellationToken.None);
        Assert.Equal(CoreInstallState.Ready, result.State);
        return Assert.IsType<CoreInstallPlan>(result.Plan);
    }

    [Theory]
    [InlineData("sing-box-statistics", false, true)]
    [InlineData("sing-box-statistics", true, false)]
    [InlineData("xray-probes", true, true)]
    [InlineData("xray-probes", false, false)]
    public async Task ComponentInstallBacksUpReplacesAndRestoresAcrossFreshSession(string id, bool appData, bool existing)
    {
        var f = Installation(id, appData, existing);
        var service = f.Installer();
        var plan = await Ready(service, f);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(f.Target)!, ".monitor-install.lock")));
        var installed = await service.InstallAsync(plan, CancellationToken.None);
        Assert.Equal(CoreInstallState.InstalledPendingReload, installed.State);
        var operation = Assert.IsType<Guid>(installed.OperationId);
        Assert.Equal(SyntheticCoreBytes, File.ReadAllBytes(f.Target));
        Assert.Equal(existing, File.Exists(Path.Combine(f.Backup(operation), "previous.exe")));
        if (existing) Assert.Equal(ExistingCoreBytes, File.ReadAllBytes(Path.Combine(f.Backup(operation), "previous.exe")));
        var journal = File.ReadAllText(Path.Combine(f.Backup(operation), "install.json"));
        Assert.Contains("InstalledPendingReload", journal);
        Assert.DoesNotContain(f.DataRoot, journal);
        Assert.DoesNotContain("synthetic command line", journal);
        f.Observation = f.Observation with { Processes = [f.Observation.Processes[0] with { Pid = 303, StartedAt = Start.AddMinutes(1) }] };
        var restored = await f.Installer().RestoreAsync(operation, CancellationToken.None);
        Assert.Equal(CoreInstallState.Restored, restored.State);
        Assert.Equal(existing, File.Exists(f.Target));
        if (existing) Assert.Equal(ExistingCoreBytes, File.ReadAllBytes(f.Target));
        Assert.Equal(CoreInstallState.AlreadyRestored,
            (await f.Installer().RestoreAsync(operation, CancellationToken.None)).State);
        Assert.True(Directory.Exists(f.Backup(operation)));
        if (!existing) Assert.Single(Directory.GetFiles(f.Backup(operation), "removed-*.exe"));
        f.Preserved();
    }

    [Theory]
    [InlineData("running", nameof(CoreInstallState.CoreRunning))]
    [InlineData("missing-parent", nameof(CoreInstallState.UnknownTarget))]
    [InlineData("multiple", nameof(CoreInstallState.UnknownTarget))]
    [InlineData("incomplete", nameof(CoreInstallState.UnknownTarget))]
    [InlineData("unknown-process", nameof(CoreInstallState.UnknownTarget))]
    [InlineData("version", nameof(CoreInstallState.UnsupportedTarget))]
    [InlineData("runtime", nameof(CoreInstallState.UnsupportedTarget))]
    [InlineData("unrelated-root", nameof(CoreInstallState.UnknownTarget))]
    [InlineData("missing-core-folder", nameof(CoreInstallState.UnsafePath))]
    public async Task ComponentInstallPreflightRejectsUnverifiedTargetsWithoutWrites(string kind, string expected)
    {
        var f = Installation();
        switch (kind)
        {
            case "running": f.Running(); break;
            case "missing-parent": f.Observation = f.Observation with { Processes = [] }; break;
            case "multiple": f.Observation = f.Observation with { Processes = [.. f.Observation.Processes, f.Observation.Processes[0] with { Pid = 404 }] }; break;
            case "incomplete": f.Observation = f.Observation with { Complete = false }; break;
            case "unknown-process": f.Observation = f.Observation with { Processes = [.. f.Observation.Processes, new(202, 101, Start, "", "", "xray.exe", "process_path_unverified")] }; break;
            case "version": f.Observation = f.Observation with { ClientVersion = "unknown" }; break;
            case "runtime": f.Observation = f.Observation with { Runtime = "linux-x64" }; break;
            case "unrelated-root": f.Observation = f.Observation with { DataRoot = f.Root }; break;
            case "missing-core-folder": Directory.Move(Path.GetDirectoryName(f.Target)!, Path.Combine(f.Root, "saved-core")); break;
        }
        var before = Directory.GetFiles(f.Root, "*", SearchOption.AllDirectories).OrderBy(x => x).ToArray();
        var result = await f.Installer().PrepareAsync(f.Candidate, CancellationToken.None);
        Assert.Equal(expected, result.State.ToString()); Assert.Null(result.Plan);
        Assert.Equal(before, Directory.GetFiles(f.Root, "*", SearchOption.AllDirectories).OrderBy(x => x));
    }

    [Fact]
    public async Task ComponentInstallAlreadyMatchingFileDoesNotCreateBackups()
    {
        var f = Installation(); File.WriteAllBytes(f.Target, SyntheticCoreBytes);
        Assert.Equal(CoreInstallState.AlreadyMatched, (await f.Installer().PrepareAsync(f.Candidate, CancellationToken.None)).State);
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(f.Target)!, ".monitor-core-backups")));
        f.Preserved();
    }

    [Theory]
    [InlineData("old-file", nameof(CoreInstallState.PlanChanged))]
    [InlineData("parent", nameof(CoreInstallState.PlanChanged))]
    [InlineData("client", nameof(CoreInstallState.PlanChanged))]
    [InlineData("candidate", nameof(CoreInstallState.InvalidCandidate))]
    [InlineData("started", nameof(CoreInstallState.CoreRunning))]
    public async Task ComponentInstallRechecksTheConfirmedPlan(string change, string expected)
    {
        var f = Installation(); var service = f.Installer(); var plan = await Ready(service, f);
        if (change == "old-file") File.WriteAllText(f.Target, "synthetic intervening version");
        if (change == "parent") f.Observation = f.Observation with { Processes = [f.Observation.Processes[0] with { StartedAt = Start.AddSeconds(1) }] };
        if (change == "client") File.AppendAllText(f.ClientPath, "changed");
        if (change == "candidate") File.AppendAllText(f.Candidate, "wrong version");
        if (change == "started") f.Running();
        var old = File.ReadAllBytes(f.Target);
        Assert.Equal(expected, (await service.InstallAsync(plan, CancellationToken.None)).State.ToString());
        Assert.Equal(old, File.ReadAllBytes(f.Target));
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(f.Target)!, ".monitor-core-backups")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComponentInstallRejectsFileAndTransactionLocks(bool targetLock)
    {
        var f = Installation(); var service = f.Installer(); var plan = await Ready(service, f);
        var path = targetLock ? f.Target : Path.Combine(Path.GetDirectoryName(f.Target)!, ".monitor-install.lock");
        using var held = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(CoreInstallState.FileBusy, (await service.InstallAsync(plan, CancellationToken.None)).State);
        held.Dispose(); Assert.Equal(ExistingCoreBytes, File.ReadAllBytes(f.Target)); f.Preserved();
    }

    [Fact]
    public async Task ComponentInstallWindowsReadOnlyTargetKeepsOriginalAndBackup()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows attribute enforcement, exercised on Windows CI/local.
        var f = Installation(); var service = f.Installer(); var plan = await Ready(service, f);
        File.SetAttributes(f.Target, FileAttributes.ReadOnly);
        try
        {
            var result = await service.InstallAsync(plan, CancellationToken.None);
            Assert.Equal(CoreInstallState.PermissionDenied, result.State);
            Assert.Equal(ExistingCoreBytes, File.ReadAllBytes(f.Target));
            Assert.Equal(ExistingCoreBytes, File.ReadAllBytes(Path.Combine(f.Backup(result.OperationId!.Value), "previous.exe")));
            f.Preserved();
        }
        finally { File.SetAttributes(f.Target, FileAttributes.Normal); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComponentInstallFailureBeforeOrAfterReplacementPreservesOriginal(bool after)
    {
        var f = Installation(); var service = f.Installer(); var plan = await Ready(service, f);
        f.Fault = point => { if (point == (after ? CoreInstallCheckpoint.AfterReplace : CoreInstallCheckpoint.BackupVerified)) throw new IOException("Synthetic failure"); };
        var result = await service.InstallAsync(plan, CancellationToken.None);
        Assert.Equal(after ? CoreInstallState.FailedRestored : CoreInstallState.StorageFailed, result.State);
        Assert.Equal(ExistingCoreBytes, File.ReadAllBytes(f.Target));
        Assert.Equal(ExistingCoreBytes, File.ReadAllBytes(Path.Combine(f.Backup(result.OperationId!.Value), "previous.exe")));
        f.Preserved();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComponentInstallCancellationDoesNotMisreportAnAlreadyReplacedFile(bool after)
    {
        var f = Installation(); var service = f.Installer(); var plan = await Ready(service, f);
        using var cancellation = new CancellationTokenSource();
        f.Fault = point => { if (point == (after ? CoreInstallCheckpoint.AfterReplace : CoreInstallCheckpoint.BeforeReplace)) cancellation.Cancel(); };
        var result = await service.InstallAsync(plan, cancellation.Token);
        Assert.Equal(after ? CoreInstallState.InstalledPendingReload : CoreInstallState.Cancelled, result.State);
        Assert.Equal(after ? SyntheticCoreBytes : ExistingCoreBytes, File.ReadAllBytes(f.Target)); f.Preserved();
    }

    [Theory]
    [InlineData("target")]
    [InlineData("backup")]
    [InlineData("journal")]
    [InlineData("journal-previous")]
    [InlineData("client")]
    [InlineData("running")]
    public async Task ComponentInstallRestoreRefusesInterveningChanges(string kind)
    {
        var f = Installation(); var service = f.Installer(); var plan = await Ready(service, f);
        var installed = await service.InstallAsync(plan, CancellationToken.None);
        Assert.Equal(CoreInstallState.InstalledPendingReload, installed.State);
        var operation = installed.OperationId!.Value;
        if (kind == "target") File.WriteAllText(f.Target, "synthetic newer user version");
        if (kind == "backup") File.WriteAllText(Path.Combine(f.Backup(operation), "previous.exe"), "synthetic damaged backup");
        if (kind == "journal") File.WriteAllText(Path.Combine(f.Backup(operation), "install.json"), "{}");
        if (kind == "journal-previous")
        {
            var path = Path.Combine(f.Backup(operation), "install.json");
            var record = JsonNode.Parse(File.ReadAllText(path))!.AsObject(); record.Remove("Previous");
            File.WriteAllText(path, record.ToJsonString());
        }
        if (kind == "client") File.AppendAllText(f.ClientPath, "changed");
        if (kind == "running") f.Running();
        var before = File.ReadAllBytes(f.Target);
        var result = await f.Installer().RestoreAsync(operation, CancellationToken.None);
        Assert.Equal(kind == "running" ? CoreInstallState.CoreRunning : CoreInstallState.PlanChanged, result.State);
        Assert.Equal(before, File.ReadAllBytes(f.Target)); f.Preserved();
    }

    [Fact]
    public async Task ComponentInstallRecoveryRequiredCanResumeFromTheDurableJournal()
    {
        var f = Installation(); var service = f.Installer(); var plan = await Ready(service, f);
        f.Fault = point => { if (point == CoreInstallCheckpoint.AfterReplace) { f.Running(); throw new IOException("Synthetic post-replacement failure"); } };
        var result = await service.InstallAsync(plan, CancellationToken.None);
        Assert.Equal(CoreInstallState.RecoveryRequired, result.State);
        Assert.Equal(SyntheticCoreBytes, File.ReadAllBytes(f.Target));
        f.Stopped(); f.Fault = null;
        Assert.Equal(CoreInstallState.Restored, (await f.Installer().RestoreAsync(result.OperationId!.Value, CancellationToken.None)).State);
        Assert.Equal(ExistingCoreBytes, File.ReadAllBytes(f.Target)); f.Preserved();
    }

    [Fact]
    public async Task ComponentInstallDoesNotOverwriteAPathSwappedAfterBackup()
    {
        var f = Installation(); var service = f.Installer(); var plan = await Ready(service, f);
        var unexpected = Bytes("synthetic intervening atomic replacement");
        f.Fault = point =>
        {
            if (point != CoreInstallCheckpoint.BeforeReplace) return;
            var file = Path.Combine(f.Root, "intervening.exe"); File.WriteAllBytes(file, unexpected);
            File.Replace(file, f.Target, null);
        };
        var result = await service.InstallAsync(plan, CancellationToken.None);
        Assert.Equal(CoreInstallState.PlanChanged, result.State);
        Assert.Equal(unexpected, File.ReadAllBytes(f.Target)); f.Preserved();
    }

    [Fact]
    public async Task ComponentInstallRejectsLinkedCoreDirectory()
    {
        var f = Installation(); var link = Path.GetDirectoryName(f.Target)!;
        var actual = Path.Combine(f.Root, "saved-core"); Directory.Move(link, actual);
        if (OperatingSystem.IsWindows())
        {
            var start = new System.Diagnostics.ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" + link.Replace("'", "''") + "' -Target '" + actual.Replace("'", "''") + "' | Out-Null");
            using var process = System.Diagnostics.Process.Start(start)!; await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, actual);
        try
        {
            Assert.Equal(CoreInstallState.UnsafePath, (await f.Installer().PrepareAsync(f.Candidate, CancellationToken.None)).State);
            Assert.Equal(ExistingCoreBytes, File.ReadAllBytes(Path.Combine(actual, f.Component.FileName)));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task ComponentInstallFailedFirstMoveDoesNotRemoveAnotherIdenticalInstall()
    {
        var f = Installation(existing: false); var service = f.Installer(); var plan = await Ready(service, f);
        f.Fault = point => { if (point == CoreInstallCheckpoint.BeforeReplace) File.WriteAllBytes(f.Target, SyntheticCoreBytes); };
        var result = await service.InstallAsync(plan, CancellationToken.None);
        Assert.Equal(CoreInstallState.RecoveryRequired, result.State);
        Assert.Equal(SyntheticCoreBytes, File.ReadAllBytes(f.Target)); f.Preserved();
    }

    [Fact]
    public async Task ComponentInstallRefusesBackupChangedBeforeReplacement()
    {
        var f = Installation(); var service = f.Installer(); var plan = await Ready(service, f);
        f.Fault = point =>
        {
            if (point != CoreInstallCheckpoint.BeforeReplace) return;
            var backup = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(f.Target)!, "previous.exe", SearchOption.AllDirectories));
            File.WriteAllText(backup, "synthetic damaged backup");
        };
        var result = await service.InstallAsync(plan, CancellationToken.None);
        Assert.Equal(CoreInstallState.PlanChanged, result.State);
        Assert.Equal(ExistingCoreBytes, File.ReadAllBytes(f.Target)); f.Preserved();
    }

    [Fact]
    public async Task ComponentInstallWindowsRejectsAnActiveImageUnderAHardLinkAlias()
    {
        if (!OperatingSystem.IsWindows()) return; // Physical file IDs are a Windows installation guard.
        var f = Installation(); var alias = Path.Combine(f.Root, "xray.exe");
        var start = new System.Diagnostics.ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$ErrorActionPreference='Stop'; New-Item -ItemType HardLink -Path '" + alias.Replace("'", "''") + "' -Target '" + f.Target.Replace("'", "''") + "' | Out-Null");
        using var process = System.Diagnostics.Process.Start(start)!; await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode);
        f.Observation = f.Observation with { Processes = [.. f.Observation.Processes,
            new(202, 101, Start.AddSeconds(1), alias, "synthetic run", "xray.exe")] };
        Assert.Equal(CoreInstallState.CoreRunning, (await f.Installer().PrepareAsync(f.Candidate, CancellationToken.None)).State);
        Assert.Equal(ExistingCoreBytes, File.ReadAllBytes(f.Target)); f.Preserved();
    }

    public void Dispose()
    {
        foreach (var stage in componentStages)
        {
            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetTempPath()), Path.GetDirectoryName(stage));
            Assert.StartsWith("v2rayn-monitor-component-", Path.GetFileName(stage));
            if (Directory.Exists(stage))
            {
                Assert.False((File.GetAttributes(stage) & FileAttributes.ReparsePoint) != 0);
                foreach (var file in Directory.GetFiles(stage))
                {
                    Assert.False((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0);
                    File.Delete(file);
                }
                Directory.Delete(stage);
            }
        }
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
