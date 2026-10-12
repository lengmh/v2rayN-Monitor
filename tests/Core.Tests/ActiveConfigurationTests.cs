using System.Globalization;
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

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
