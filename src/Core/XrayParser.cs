using System.Globalization;
using System.Collections.ObjectModel;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace V2rayNMonitor.Core;

/// <summary>Pure readers for the pinned v2rayN 7.25.1/Xray metrics contract.</summary>
public static class XrayParser
{
    public const string StatisticsPath = "/debug/vars";

    public static bool TryReadCounters(ReadOnlyMemory<byte> json, out XrayCounters? counters)
    {
        counters = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!Property(document.RootElement, "stats", out var stats)
                || !Property(stats, "outbound", out var outbound)
                || !Property(outbound, "proxy", out var proxy)
                || !Property(proxy, "uplink", out var up)
                || !Property(proxy, "downlink", out var down)
                || !up.TryGetInt64(out var upload) || !down.TryGetInt64(out var download)
                || upload < 0 || download < 0)
                return false;
            counters = new(upload, download);
            return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return false; }
    }

    public static bool TryReadSelection(ReadOnlyMemory<byte> json, out XrayGuiSelection? selection)
    {
        selection = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!Text(root, "IndexId", out var indexId) || string.IsNullOrWhiteSpace(indexId)
                || !Property(root, "GuiItem", out var gui)
                || !Boolean(gui, "EnableStatistics", out var statistics)
                || !Boolean(gui, "DisplayRealTimeSpeed", out var speed)
                || !Property(root, "TunModeItem", out var tun)
                || !Boolean(tun, "EnableTun", out var enableTun)) return false;
            selection = new(indexId, statistics, speed, enableTun);
            return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return false; }
    }

    /// <summary>Reads all verified tag pairs from one expvar response; missing pairs never become zero.</summary>
    public static bool TryReadOutboundsCounters(ReadOnlyMemory<byte> json, IReadOnlyCollection<string> tags,
        out V2RayOutboundStatistics? counters)
    {
        counters = null;
        if (tags.Count == 0 || tags.Any(x => string.IsNullOrWhiteSpace(x) || x.Contains(">>>", StringComparison.Ordinal)) ||
            tags.Distinct(StringComparer.Ordinal).Count() != tags.Count) throw new ArgumentException("Distinct outbound tags are required.");
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!Property(document.RootElement, "stats", out var stats) || !Property(stats, "outbound", out var outbounds) ||
                outbounds.ValueKind != JsonValueKind.Object) return false;
            var complete = new Dictionary<string, XrayCounters>(StringComparer.Ordinal);
            var unavailable = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                var entries = outbounds.EnumerateObject().Where(x => x.NameEquals(tag)).ToArray();
                if (entries.Length == 0) { unavailable.Add(tag, "missing_counter"); continue; }
                if (entries.Length != 1) { unavailable.Add(tag, "duplicate_counter"); continue; }
                var entry = entries[0].Value;
                if (!Property(entry, "uplink", out var up) || !Property(entry, "downlink", out var down))
                {
                    unavailable.Add(tag, entry.ValueKind == JsonValueKind.Object &&
                        entry.EnumerateObject().GroupBy(x => x.Name, StringComparer.Ordinal).Any(x => x.Count() > 1)
                        ? "invalid_counter" : "missing_counter");
                    continue;
                }
                if (up.ValueKind != JsonValueKind.Number || down.ValueKind != JsonValueKind.Number ||
                    !up.TryGetInt64(out var upload) || !down.TryGetInt64(out var download) || upload < 0 || download < 0)
                    unavailable.Add(tag, "invalid_counter");
                else complete.Add(tag, new(upload, download));
            }
            counters = new(new ReadOnlyDictionary<string, XrayCounters>(complete), new ReadOnlyDictionary<string, string>(unavailable));
            return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return false; }
    }

    /// <summary>Builds independent outbound identities from the already bound standard configuration.</summary>
    public static bool TryReadOutboundCatalog(XraySnapshot snapshot, ReadOnlyMemory<byte> json,
        out XrayOutboundCatalog? catalog, out string reason)
    {
        catalog = null; reason = "snapshot_changed";
        if (snapshot.CoreName != "Xray" || string.IsNullOrEmpty(snapshot.InstanceId) ||
            !snapshot.Generation.StartsWith(Hash(json.Span) + ":", StringComparison.Ordinal)) return false;
        if (!TryReadConfiguration(json, out var configuration, out reason) || configuration!.MetricsEndpoint != snapshot.MetricsEndpoint)
            return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            reason = "unsupported_configuration";
            if (!UniqueKeys(root)) return false;
            var entries = root.GetProperty("outbounds").EnumerateArray().ToArray();
            var groups = new List<string[]>();
            if (Property(root, "routing", out var routing) && Property(routing, "balancers", out var balancers))
            {
                var groupTags = new HashSet<string>(StringComparer.Ordinal);
                foreach (var group in balancers.EnumerateArray())
                {
                    if (!Text(group, "tag", out var groupTag) || string.IsNullOrWhiteSpace(groupTag) || !groupTags.Add(groupTag) ||
                        !Property(group, "selector", out var selectors) || selectors.ValueKind != JsonValueKind.Array) return false;
                    var values = selectors.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : "").ToArray();
                    if (values.Length == 0 || values.Any(string.IsNullOrWhiteSpace) || values.Distinct(StringComparer.Ordinal).Count() != values.Length)
                        return false;
                    groups.Add(values);
                }
            }
            var candidates = new Dictionary<string, XrayOutboundIdentity>(StringComparer.Ordinal);
            var unavailable = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                var tag = entry.GetProperty("tag").GetString()!;
                var protocol = entry.GetProperty("protocol").GetString()!;
                if (string.IsNullOrWhiteSpace(tag) || tag.Contains(">>>", StringComparison.Ordinal)) return false;
                if (protocol is "freedom" or "blackhole") continue;
                if (!SupportedProtocol(protocol) || !TryServerIdentity(entry, protocol, out _))
                { unavailable.Add(tag, "unsupported_member_type"); continue; }
                CapturedNodeName? name = null;
                if (tag == "proxy") name = new(snapshot.NodeName);
                else
                {
                    var parents = groups.Where(x => x.Any(prefix => tag.StartsWith(prefix, StringComparison.Ordinal))).ToArray();
                    if (parents.Length == 1 && parents[0].Length == 1)
                    {
                        var prefix = parents[0][0];
                        if (tag.StartsWith(prefix + "-", StringComparison.Ordinal))
                        {
                            var member = tag[(prefix.Length + 1)..];
                            var split = member.IndexOf('-');
                            if (split > 0 && int.TryParse(member[..split], out var number) && number > 0 &&
                                member[..split] == number.ToString(CultureInfo.InvariantCulture) && split + 1 < member.Length)
                            {
                                var separator = prefix.IndexOf("-proxy-", StringComparison.Ordinal);
                                var label = separator > 0 && prefix[..separator].All(char.IsAsciiDigit) ? prefix[(separator + 7)..] : prefix;
                                name = new(member[(split + 1)..], label);
                            }
                        }
                    }
                }
                candidates.Add(tag, new(tag, protocol, name));
            }
            if (!candidates.ContainsKey("proxy")) return false;
            catalog = new(snapshot.InstanceId, snapshot.Generation, snapshot.ProcessIdentity,
                new ReadOnlyDictionary<string, XrayOutboundIdentity>(candidates),
                new ReadOnlyDictionary<string, string>(unavailable), groups.Count);
            reason = "ready"; return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { return false; }
    }

    public static bool TryReadConfiguration(ReadOnlyMemory<byte> json,
        out XrayEffectiveConfiguration? configuration, out string reason)
    {
        configuration = null;
        reason = "unsupported_configuration";
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!Property(root, "metrics", out var metrics) || !Text(metrics, "listen", out var listen)
                || !TryLoopbackEndpoint(listen, out var endpoint))
            {
                reason = "statistics_unavailable";
                return false;
            }
            if (!Property(root, "stats", out var stats) || stats.ValueKind != JsonValueKind.Object
                || !Property(root, "policy", out var policy) || !Property(policy, "system", out var system)
                || !Boolean(system, "statsOutboundUplink", out var up) || !up
                || !Boolean(system, "statsOutboundDownlink", out var down) || !down)
            {
                reason = "statistics_disabled";
                return false;
            }
            if (HasChaining(root)
                || !Property(root, "outbounds", out var outbounds) || outbounds.ValueKind != JsonValueKind.Array)
                return false;
            var groups = 0;
            if (Property(root, "routing", out var routing) && Property(routing, "balancers", out var balancers))
            {
                if (balancers.ValueKind != JsonValueKind.Array) return false;
                groups = balancers.GetArrayLength();
            }
            var uncollected = 0;
            JsonElement? proxy = null;
            var tags = new HashSet<string>(StringComparer.Ordinal);
            foreach (var outbound in outbounds.EnumerateArray())
            {
                if (!Text(outbound, "tag", out var tag) || !tags.Add(tag)
                    || !Text(outbound, "protocol", out var protocol)) return false;
                if (tag == "proxy")
                {
                    if (proxy.HasValue || !SupportedProtocol(protocol)) return false;
                    proxy = outbound;
                }
                else if (protocol is not ("freedom" or "blackhole")) uncollected++;
            }
            if (!proxy.HasValue || !Text(proxy.Value, "protocol", out var proxyProtocol)
                || !TryServerIdentity(proxy.Value, proxyProtocol, out var serverIdentity)) return false;
            configuration = new(endpoint!, proxyProtocol, serverIdentity, Scope:
                new ProxyCollectionScope(uncollected, groups, "xray_outbound_proxy"));
            reason = "ready";
            return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        { return false; }
    }

    public static bool TryLoopbackEndpoint(string listen, out Uri? endpoint)
    {
        endpoint = null;
        if (!IPEndPoint.TryParse(listen, out var parsed) || !IPAddress.IsLoopback(parsed.Address)
            || parsed.Port is < 1 or > 65535) return false;
        endpoint = new UriBuilder(Uri.UriSchemeHttp, parsed.Address.ToString(), parsed.Port, StatisticsPath).Uri;
        return true;
    }

    public static string? ProtocolForConfigType(int type) => type switch
    {
        1 => "vmess", 3 => "shadowsocks", 4 => "socks", 5 => "vless", 6 => "trojan", 7 => "hysteria", 10 => "http", _ => null
    };

    // CoreType may be null when v2rayN chooses its protocol default. The caller must still
    // prove that the active process is Xray; a profile preference is not process evidence.
    public static bool TryProfileIdentity(int configType, int? coreType, string address, int port,
        string password, string username, out string identity)
    {
        identity = "";
        var protocol = ProtocolForConfigType(configType);
        if (protocol is null || coreType is not (null or 2) || string.IsNullOrWhiteSpace(address)
            || port is < 1 or > 65535) return false;
        if (protocol == "hysteria" && string.IsNullOrEmpty(password)) return false;
        if (protocol is "socks" or "http" && (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)))
            (username, password) = ("", "");
        identity = ServerIdentity(protocol, address, port, protocol is "socks" or "http" ? username : "", password);
        return true;
    }

    // This comparison value is transient and never written to the ledger or exposed by the API.
    public static string ServerIdentity(string protocol, string address, int port, string user, string secret)
        => Hash(JsonSerializer.Serialize(new[] { protocol, address.ToLowerInvariant(), port.ToString(CultureInfo.InvariantCulture), user,
            protocol is "vmess" or "vless" && Guid.TryParse(secret, out var id) ? id.ToString("D") : secret }));

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Hash(ReadOnlySpan<byte> value) => Convert.ToHexString(SHA256.HashData(value));

    /// <summary>
    /// Checks the selected 95f68d9 profile's connection fields, not uniqueness among profiles.
    /// Inputs are transient: neither profile values nor their fingerprints enter the ledger.
    /// This is a bounded projection of the pinned generator, not a replacement generator.
    /// </summary>
    public static bool MatchesSelectedConnection(IReadOnlyDictionary<string, string?> profile,
        ReadOnlyMemory<byte> guiJson, ReadOnlyMemory<byte> configurationJson, out string reason)
    {
        reason = "mapping_changed";
        try
        {
            string P(string key) => profile.TryGetValue(key, out var value) ? value ?? "" : "";
            using var config = JsonDocument.Parse(configurationJson);
            using var gui = JsonDocument.Parse(guiJson);
            using var protocol = JsonDocument.Parse(string.IsNullOrWhiteSpace(P("ProtoExtra")) ? "{}" : P("ProtoExtra"));
            using var transport = JsonDocument.Parse(string.IsNullOrWhiteSpace(P("TransportExtra")) ? "{}" : P("TransportExtra"));
            var proto = protocol.RootElement;
            var trans = transport.RootElement;
            var defaults = Read(gui.RootElement, "CoreBasicItem");
            var proxy = Read(config.RootElement, "outbounds").EnumerateArray().Single(x => Value(x, "tag") == "proxy");
            var settings = Read(proxy, "settings");
            var stream = Read(proxy, "streamSettings");
            var kind = Value(proxy, "protocol");
            if (proto.ValueKind != JsonValueKind.Object || trans.ValueKind != JsonValueKind.Object) return false;
            if (kind == "vless" && (!Same(settings, "encryption", Value(proto, "VlessEncryption"))
                || !Same(settings, "flow", Nonblank(Value(proto, "Flow"))))) return false;
            if (kind == "vmess")
            {
                var cipher = Value(proto, "VmessSecurity");
                if (cipher is not ("aes-128-gcm" or "chacha20-poly1305" or "auto" or "none" or "zero")) cipher = "auto";
                var alterId = int.TryParse(Value(proto, "AlterId"), out var number) ? number : 0;
                if (!Same(settings, "security", cipher) || Number(settings, "alterId") != alterId) return false;
            }
            if (kind == "shadowsocks")
            {
                var method = Value(proto, "SsMethod");
                if (method is not ("aes-256-gcm" or "aes-128-gcm" or "chacha20-poly1305" or "chacha20-ietf-poly1305"
                    or "xchacha20-poly1305" or "xchacha20-ietf-poly1305" or "none" or "plain"
                    or "2022-blake3-aes-128-gcm" or "2022-blake3-aes-256-gcm" or "2022-blake3-chacha20-poly1305")) method = "none";
                if (!Same(settings, "method", method) || Flag(settings, "uot") != Flag(proto, "Uot")) return false;
            }
            if (kind == "http" && !SameJsonText(Read(settings, "headers"), Value(proto, "HttpHeaders"))) return false;

            var network = P("Network") is "raw" or "xhttp" or "kcp" or "grpc" or "ws" or "httpupgrade" ? P("Network") : "raw";
            if (kind == "hysteria") network = "hysteria";
            // These advanced branches require parsing a separate mask/certificate language.
            // Do not silently accept a stale config by ignoring them.
            if (network == "kcp" || !string.IsNullOrWhiteSpace(P("Cert")) || !string.IsNullOrWhiteSpace(P("Finalmask"))
                || (kind == "hysteria" && !string.IsNullOrWhiteSpace(Value(proto, "Hy2RealmUrl"))))
            { reason = "unsupported_connection"; return false; }
            if (!Same(stream, "network", network)) return false;
            var host = network == "grpc" ? Value(trans, "GrpcAuthority").Trim()
                : network is "raw" or "xhttp" or "ws" or "httpupgrade" ? Value(trans, "Host").Trim() : "";
            var path = Value(trans, "Path").Trim();
            var security = P("StreamSecurity") == "reality" ? "reality"
                : kind == "hysteria" || P("StreamSecurity") == "tls" ? "tls" : "";
            if (!Same(stream, "security", security)) return false;
            if (security.Length != 0)
            {
                var tls = Read(stream, security + "Settings");
                var fingerprint = string.IsNullOrWhiteSpace(P("Fingerprint")) ? Value(defaults, "DefFingerprint") : P("Fingerprint");
                var sni = P("Sni").Trim();
                if (security == "tls" && sni.Length == 0 && host.Length != 0) sni = List(host).FirstOrDefault() ?? "";
                if (!Same(tls, "serverName", sni) || !Same(tls, "fingerprint", fingerprint)) return false;
                if (security == "reality")
                {
                    if (!Same(tls, "publicKey", P("PublicKey")) || !Same(tls, "shortId", P("ShortId"))
                        || !Same(tls, "spiderX", P("SpiderX")) || !Same(tls, "mldsa65Verify", P("Mldsa65Verify"))) return false;
                }
                else
                {
                    if (!SameList(Read(tls, "alpn"), List(P("Alpn")))
                        || !Same(tls, "echConfigList", Nonblank(P("EchConfigList")))
                        || !Same(tls, "verifyPeerCertByName", Nonblank(P("VerifyPeerCertByName")))
                        || !Same(tls, "pinnedPeerCertSha256", Nonblank(P("CertSha")))
                        || Flag(tls, "allowInsecure") != (string.IsNullOrWhiteSpace(P("CertSha")) && P("AllowInsecure") == "true")
                        || Flag(tls, "disableSystemRoot") || !Empty(Read(tls, "certificates"))) return false;
                }
            }

            var userAgent = Nonblank(Value(defaults, "DefUserAgent"));
            switch (network)
            {
                case "xhttp":
                    var xhttp = Read(stream, "xhttpSettings");
                    if (!string.IsNullOrWhiteSpace(Value(defaults, "BindInterface"))
                        && !Empty(Read(Read(xhttp, "extra"), "downloadSettings")))
                    { reason = "unsupported_connection"; return false; }
                    var mode = Value(trans, "XhttpMode").Trim();
                    if (mode is not ("auto" or "packet-up" or "stream-up" or "stream-one")) mode = "";
                    if (!Same(xhttp, "host", host) || !Same(xhttp, "path", path) || !Same(xhttp, "mode", mode)
                        || !SameJsonText(Read(xhttp, "extra"), Value(trans, "XhttpExtra"))) return false;
                    break;
                case "ws":
                case "httpupgrade":
                    var http = Read(stream, network + "Settings");
                    if (!Same(http, "host", host) || !Same(http, "path", path)
                        || !Same(Read(http, "headers"), "User-Agent", userAgent)) return false;
                    break;
                case "grpc":
                    var grpc = Read(stream, "grpcSettings");
                    if (!Same(grpc, "authority", host) || !Same(grpc, "serviceName", Value(trans, "GrpcServiceName").Trim())
                        || Flag(grpc, "multiMode") != (Value(trans, "GrpcMode").Trim() == "multi")
                        || !Same(grpc, "user_agent", userAgent)) return false;
                    break;
                case "raw":
                    var header = Read(Read(stream, "rawSettings"), "header");
                    var httpHeader = Value(trans, "RawHeaderType").Trim() == "http";
                    if (!Same(header, "type", httpHeader ? "http" : "")) return false;
                    if (httpHeader)
                    {
                        var request = Read(header, "request");
                        if (!SameList(Read(request, "path"), (path.Length == 0 ? "/" : path).Split(','))
                            || !SameList(Read(Read(request, "headers"), "Host"), host.Split(','))) return false;
                    }
                    break;
                case "hysteria":
                    if (!MatchesHysteriaOptions(stream, proto, Read(gui.RootElement, "HysteriaItem"))) return false;
                    break;
            }
            reason = "ready";
            return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or OverflowException)
        { reason = "unsupported_connection"; return false; }
    }

    private static bool MatchesHysteriaOptions(JsonElement stream, JsonElement proto, JsonElement defaults)
    {
        var mask = Read(stream, "finalmask");
        var quic = Read(mask, "quicParams");
        var hop = Read(quic, "udpHop");
        var ports = Value(proto, "Ports");
        var hopping = ports.IndexOfAny([':', '-', ',']) >= 0;
        var interval = Value(proto, "HopInterval");
        if (string.IsNullOrWhiteSpace(interval))
        {
            var seconds = Number(defaults, "HopInterval");
            interval = seconds >= 5 ? seconds.ToString(CultureInfo.InvariantCulture) : "30";
        }
        if (hopping ? !Same(hop, "ports", ports.Replace(':', '-')) || !Same(hop, "interval", interval) : !Empty(hop)) return false;
        int Bandwidth(string key)
        {
            var value = Read(proto, key);
            return !Empty(value) && value.TryGetInt32(out var n) && n >= 0 ? n : Empty(defaults) ? 100 : Number(defaults, key);
        }
        var up = Bandwidth("UpMbps"); var down = Bandwidth("DownMbps");
        if (!Same(quic, "congestion", up > 0 || down > 0 ? "brutal" : "bbr")
            || !Same(quic, "brutalUp", up > 0 ? $"{up}mbps" : "") || !Same(quic, "brutalDown", down > 0 ? $"{down}mbps" : "")) return false;
        var udp = Read(mask, "udp");
        var password = Value(proto, "SalamanderPass");
        if (string.IsNullOrWhiteSpace(password)) return Empty(udp);
        if (udp.ValueKind != JsonValueKind.Array || udp.GetArrayLength() != 1) return false;
        var entry = udp[0]; var options = Read(entry, "settings");
        var min = Value(proto, "GeckoMinPacketSize"); var max = Value(proto, "GeckoMaxPacketSize");
        return Same(entry, "type", "salamander") && Same(options, "password", password.Trim())
            && Same(options, "packetSize", string.IsNullOrWhiteSpace(min) && string.IsNullOrWhiteSpace(max) ? "" : $"{min}-{max}");
    }

    // Optional generated fields may be omitted/null, but duplicate keys and wrong scalar
    // types are never defaults. This also prevents nested extra JSON from hiding ambiguity.
    private static JsonElement Read(JsonElement root, string key)
    {
        if (root.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return default;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
        var matches = root.EnumerateObject().Where(x => x.NameEquals(key)).ToArray();
        if (matches.Length > 1) throw new JsonException();
        return matches.Length == 0 ? default : matches[0].Value;
    }
    private static string Value(JsonElement root, string key)
    {
        var item = Read(root, key);
        return item.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "" : item.GetString()!;
    }
    private static bool Same(JsonElement root, string key, string expected) => Value(root, key) == expected;
    private static bool Flag(JsonElement root, string key)
    {
        var item = Read(root, key);
        return item.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? false : item.GetBoolean();
    }
    private static int Number(JsonElement root, string key)
    {
        var item = Read(root, key);
        return item.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? 0 : item.GetInt32();
    }
    private static string Nonblank(string value) => string.IsNullOrWhiteSpace(value) ? "" : value;
    private static string[] List(string value) => string.IsNullOrWhiteSpace(value) ? []
        : value.Replace("\r", "").Replace("\n", "").Split(',', StringSplitOptions.RemoveEmptyEntries);
    private static bool Empty(JsonElement item) => item.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
        || (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() == 0);
    private static bool SameList(JsonElement actual, string[] expected) => expected.Length == 0 ? Empty(actual)
        : actual.ValueKind == JsonValueKind.Array && actual.EnumerateArray().Select(x => x.GetString()).SequenceEqual(expected);
    private static bool SameJsonText(JsonElement actual, string expected)
    {
        if (string.IsNullOrWhiteSpace(expected)) return Empty(actual);
        using var parsed = JsonDocument.Parse(expected);
        void NoDuplicates(JsonElement item)
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in item.EnumerateObject()) { if (!keys.Add(p.Name)) throw new JsonException(); NoDuplicates(p.Value); }
            }
            else if (item.ValueKind == JsonValueKind.Array) foreach (var child in item.EnumerateArray()) NoDuplicates(child);
        }
        NoDuplicates(parsed.RootElement); NoDuplicates(actual);
        return actual.ValueKind != JsonValueKind.Undefined && JsonElement.DeepEquals(actual, parsed.RootElement);
    }

    private static bool TryServerIdentity(JsonElement proxy, string protocol, out string identity)
    {
        identity = "";
        if (!Property(proxy, "settings", out var settings)) return false;
        // Pinned v2rayN 7.25.1 emits flat settings. Legacy or mixed representations
        // are outside this adapter's version contract and must not be guessed.
        if (Property(settings, "vnext", out _) || Property(settings, "servers", out _)) return false;
        var server = settings;
        if (!Text(server, "address", out var address) || string.IsNullOrWhiteSpace(address)
            || !Property(server, "port", out var portValue) || !portValue.TryGetInt32(out var port)
            || port is < 1 or > 65535) return false;
        string user = "", secret = "";
        if (protocol == "hysteria")
        {
            // fqfqgo/v2rayN 95f68d9 (7.25.1): Hysteria2 is Xray protocol "hysteria".
            // Its credential is streamSettings.hysteriaSettings.auth, NOT settings.password.
            if (!Property(server, "version", out var version) || !version.TryGetInt32(out var protocolVersion) || protocolVersion != 2
                || !Property(proxy, "streamSettings", out var stream) || !Text(stream, "network", out var network) || network != "hysteria"
                || !Property(stream, "hysteriaSettings", out var hysteria)
                || !Property(hysteria, "version", out var transportVersion) || !transportVersion.TryGetInt32(out var transport) || transport != 2
                || !Text(hysteria, "auth", out secret) || string.IsNullOrEmpty(secret)) return false;
        }
        else if (protocol is "vmess" or "vless")
        {
            if (!Text(server, "id", out secret)) return false;
        }
        else if (protocol is "trojan" or "shadowsocks")
        {
            if (!Text(server, "password", out secret)) return false;
        }
        else
        {
            var hasUser = Text(server, "user", out user);
            var hasPassword = Text(server, "pass", out secret);
            if (hasUser != hasPassword) return false;
            if (hasUser && (string.IsNullOrEmpty(user) != string.IsNullOrEmpty(secret))) return false;
        }
        identity = ServerIdentity(protocol, address, port, user, secret);
        return true;
    }

    private static bool SupportedProtocol(string protocol)
        => protocol is "vmess" or "vless" or "trojan" or "shadowsocks" or "socks" or "http" or "hysteria";

    private static bool HasChaining(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.Object)
            foreach (var property in item.EnumerateObject())
            {
                if (property.Name == "proxySettings" && property.Value.ValueKind != JsonValueKind.Null) return true;
                if (property.Name == "dialerProxy" && property.Value.ValueKind != JsonValueKind.Null
                    && !(property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() == "")) return true;
                if (HasChaining(property.Value)) return true;
            }
        if (item.ValueKind == JsonValueKind.Array)
            foreach (var child in item.EnumerateArray()) if (HasChaining(child)) return true;
        return false;
    }

    private static bool UniqueKeys(JsonElement item) => item.ValueKind switch
    {
        JsonValueKind.Object => item.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() == item.EnumerateObject().Count() &&
            item.EnumerateObject().All(x => UniqueKeys(x.Value)),
        JsonValueKind.Array => item.EnumerateArray().All(UniqueKeys),
        _ => true
    };

    // Duplicate critical JSON keys are ambiguous. Do not silently choose the last one.
    private static bool Property(JsonElement root, string name, out JsonElement value)
    {
        value = default;
        if (root.ValueKind != JsonValueKind.Object) return false;
        var count = 0;
        foreach (var property in root.EnumerateObject())
            if (property.NameEquals(name)) { value = property.Value; count++; }
        return count == 1;
    }
    private static bool Text(JsonElement root, string name, out string value)
    {
        value = "";
        if (!Property(root, name, out var item) || item.ValueKind != JsonValueKind.String) return false;
        value = item.GetString()!;
        return true;
    }
    private static bool Boolean(JsonElement root, string name, out bool value)
    {
        value = false;
        if (!Property(root, name, out var item) || item.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = item.GetBoolean();
        return true;
    }
}
