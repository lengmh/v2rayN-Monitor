using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace V2rayNMonitor.Core;

/// <summary>Bounded unary QueryStats reader. No reset call, streaming cache, or global traffic fallback.</summary>
public static class V2RayStatistics
{
    public const string QueryPath = "/v2ray.core.app.stats.command.StatsService/QueryStats";
    private const int MaxResponseBytes = 65536;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static async Task<XrayCounters?> ReadAsync(HttpClient client, Uri endpoint, CancellationToken cancellationToken)
    {
        var result = await ReadOutboundsAsync(client, endpoint, ["proxy"], cancellationToken).ConfigureAwait(false);
        return result is not null && result.Counters.TryGetValue("proxy", out var counters) ? counters : null;
    }

    /// <summary>
    /// Reads route-tag counters in one reset=false RPC. A group tag is not its actual member,
    /// and the returned dimensions must not be added to member or chain-hop counters.
    /// </summary>
    public static async Task<V2RayOutboundStatistics?> ReadOutboundsAsync(HttpClient client, Uri endpoint,
        IReadOnlyCollection<string> outboundTags, CancellationToken cancellationToken)
    {
        var tags = ValidateTags(outboundTags);
        if (endpoint.Scheme != "http" || !IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var address)
            || !IPAddress.IsLoopback(address)) throw new ArgumentException("A numeric loopback endpoint is required.");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, QueryPath))
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            // QueryStatsRequest.reset (field 2) is explicitly false.
            Content = new ByteArrayContent([0, 0, 0, 0, 2, 0x10, 0])
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        request.Headers.TryAddWithoutValidation("TE", "trailers");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.Version.Major != 2 || !response.IsSuccessStatusCode
            || response.Content.Headers.ContentType?.MediaType is not ("application/grpc" or "application/grpc+proto"))
            throw new HttpRequestException("Statistics RPC is unavailable.");
        if (response.Content.Headers.ContentLength is > MaxResponseBytes) return null;
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (bytes.Length + count > MaxResponseBytes) return null;
            bytes.Write(buffer, 0, count);
        }
        var status = response.TrailingHeaders.TryGetValues("grpc-status", out var trailers) ? trailers
            : response.Headers.TryGetValues("grpc-status", out var headers) ? headers : [];
        if (!status.SequenceEqual(["0"])) throw new HttpRequestException("Statistics RPC did not succeed.");
        return TryReadOutboundsResponse(bytes.ToArray(), tags, out var counters) ? counters : null;
    }

    public static bool TryReadResponse(ReadOnlySpan<byte> frame, out XrayCounters? counters)
    {
        counters = null;
        if (!TryReadOutboundsResponse(frame, ["proxy"], out var result)
            || !result!.Counters.TryGetValue("proxy", out counters)) return false;
        return true;
    }

    public static bool TryReadOutboundsResponse(ReadOnlySpan<byte> frame, IReadOnlyCollection<string> outboundTags,
        out V2RayOutboundStatistics? counters)
    {
        counters = null;
        var tags = ValidateTags(outboundTags);
        if (frame.Length < 5 || frame.Length > MaxResponseBytes || frame[0] != 0
            || BinaryPrimitives.ReadUInt32BigEndian(frame[1..]) != frame.Length - 5) return false;
        var message = frame[5..];
        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            expected.Add(CounterName(tag, "uplink"));
            expected.Add(CounterName(tag, "downlink"));
        }
        var values = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var duplicates = new HashSet<string>(StringComparer.Ordinal);
        while (!message.IsEmpty)
        {
            if (!Field(ref message, out var field, out var wire)) return false;
            if (field != 1)
            {
                if (!Skip(ref message, wire)) return false;
                continue;
            }
            if (wire != 2 || !Block(ref message, out var stat) || !ReadStat(stat, out var name, out var value)) return false;
            if (expected.Contains(name) && !values.TryAdd(name, value)) duplicates.Add(name);
        }
        var complete = new Dictionary<string, XrayCounters>(StringComparer.Ordinal);
        var unavailable = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            var up = CounterName(tag, "uplink");
            var down = CounterName(tag, "downlink");
            if (duplicates.Contains(up) || duplicates.Contains(down)) unavailable.Add(tag, "duplicate_counter");
            else if ((values.TryGetValue(up, out var upload) && upload > long.MaxValue)
                || (values.TryGetValue(down, out var download) && download > long.MaxValue))
                unavailable.Add(tag, "invalid_counter");
            else if (!values.TryGetValue(up, out upload) || !values.TryGetValue(down, out download))
                unavailable.Add(tag, "missing_counter"); // Missing is never a fabricated zero.
            else complete.Add(tag, new((long)upload, (long)download));
        }
        counters = new(new ReadOnlyDictionary<string, XrayCounters>(complete),
            new ReadOnlyDictionary<string, string>(unavailable));
        return true;
    }

    private static string CounterName(string tag, string direction) => $"outbound>>>{tag}>>>traffic>>>{direction}";

    private static string[] ValidateTags(IReadOnlyCollection<string> outboundTags)
    {
        ArgumentNullException.ThrowIfNull(outboundTags);
        var tags = outboundTags.ToArray();
        if (tags.Length == 0 || tags.Any(tag => string.IsNullOrEmpty(tag) || tag.Contains(">>>", StringComparison.Ordinal))
            || tags.Distinct(StringComparer.Ordinal).Count() != tags.Length)
            throw new ArgumentException("Use distinct, nonempty outbound tags without the statistics delimiter.", nameof(outboundTags));
        return tags;
    }

    private static bool ReadStat(ReadOnlySpan<byte> message, out string name, out ulong value)
    {
        name = "";
        value = 0; // proto3 omits the scalar field for an actual zero.
        var hasName = false;
        var hasValue = false;
        while (!message.IsEmpty)
        {
            if (!Field(ref message, out var field, out var wire)) return false;
            if (field == 1)
            {
                if (hasName || wire != 2 || !Block(ref message, out var text)) return false;
                try { name = Utf8.GetString(text); }
                catch (DecoderFallbackException) { return false; }
                hasName = true;
            }
            else if (field == 2)
            {
                if (hasValue || wire != 0 || !Varint(ref message, out value)) return false;
                hasValue = true;
            }
            else if (!Skip(ref message, wire)) return false;
        }
        return hasName && name.Length != 0;
    }

    private static bool Field(ref ReadOnlySpan<byte> message, out ulong field, out int wire)
    {
        var valid = Varint(ref message, out var key);
        field = key >> 3;
        wire = (int)(key & 7);
        return valid && field is > 0 and <= 0x1fffffff;
    }

    private static bool Block(ref ReadOnlySpan<byte> message, out ReadOnlySpan<byte> block)
    {
        block = default;
        if (!Varint(ref message, out var length) || length > (ulong)message.Length) return false;
        block = message[..(int)length];
        message = message[(int)length..];
        return true;
    }

    private static bool Skip(ref ReadOnlySpan<byte> message, int wire)
    {
        if (wire == 0) return Varint(ref message, out _);
        if (wire == 2) return Block(ref message, out _);
        var length = wire == 1 ? 8 : wire == 5 ? 4 : -1;
        if (length < 0 || message.Length < length) return false;
        message = message[length..];
        return true;
    }

    private static bool Varint(ref ReadOnlySpan<byte> message, out ulong value)
    {
        value = 0;
        for (var shift = 0; shift < 70; shift += 7)
        {
            if (message.IsEmpty) return false;
            var next = message[0];
            message = message[1..];
            if (shift == 63 && next > 1) return false;
            value |= (ulong)(next & 0x7f) << shift;
            if (next < 0x80) return true;
        }
        return false;
    }
}
