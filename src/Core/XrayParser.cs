using System.Globalization;
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
            if (HasChaining(root) || HasNonemptyBalancers(root)
                || !Property(root, "outbounds", out var outbounds) || outbounds.ValueKind != JsonValueKind.Array)
                return false;
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
                else if (!((tag == "direct" && protocol == "freedom")
                    || (tag == "block" && protocol == "blackhole"))) return false;
            }
            if (!proxy.HasValue || !Text(proxy.Value, "protocol", out var proxyProtocol)
                || !TryServerIdentity(proxy.Value, proxyProtocol, out var serverIdentity)) return false;
            configuration = new(endpoint!, proxyProtocol, serverIdentity);
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
        1 => "vmess", 3 => "shadowsocks", 4 => "socks", 5 => "vless", 6 => "trojan", 10 => "http", _ => null
    };

    // This comparison value is transient and never written to the ledger or exposed by the API.
    public static string ServerIdentity(string protocol, string address, int port, string user, string secret)
        => Hash(JsonSerializer.Serialize(new[] { protocol, address.ToLowerInvariant(), port.ToString(CultureInfo.InvariantCulture), user,
            protocol is "vmess" or "vless" && Guid.TryParse(secret, out var id) ? id.ToString("D") : secret }));

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Hash(ReadOnlySpan<byte> value) => Convert.ToHexString(SHA256.HashData(value));

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
        if (protocol is "vmess" or "vless")
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
        => protocol is "vmess" or "vless" or "trojan" or "shadowsocks" or "socks" or "http";

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

    private static bool HasNonemptyBalancers(JsonElement root)
        => Property(root, "routing", out var routing) && Property(routing, "balancers", out var balancers)
            && (balancers.ValueKind != JsonValueKind.Array || balancers.GetArrayLength() != 0);

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
