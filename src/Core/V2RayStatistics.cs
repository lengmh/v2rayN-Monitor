using System.Buffers.Binary;
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
        return TryReadResponse(bytes.ToArray(), out var counters) ? counters : null;
    }

    public static bool TryReadResponse(ReadOnlySpan<byte> frame, out XrayCounters? counters)
    {
        counters = null;
        if (frame.Length < 5 || frame.Length > MaxResponseBytes || frame[0] != 0
            || BinaryPrimitives.ReadUInt32BigEndian(frame[1..]) != frame.Length - 5) return false;
        var message = frame[5..];
        long? upload = null, download = null;
        while (!message.IsEmpty)
        {
            if (!Field(ref message, out var field, out var wire)) return false;
            if (field != 1)
            {
                if (!Skip(ref message, wire)) return false;
                continue;
            }
            if (wire != 2 || !Block(ref message, out var stat) || !ReadStat(stat, out var name, out var value)) return false;
            switch (name)
            {
                case "outbound>>>proxy>>>traffic>>>uplink":
                    if (upload is not null) return false;
                    upload = value; break;
                case "outbound>>>proxy>>>traffic>>>downlink":
                    if (download is not null) return false;
                    download = value; break;
            }
        }
        if (upload is null || download is null) return false; // Missing is never a fabricated zero.
        counters = new(upload.Value, download.Value);
        return true;
    }

    private static bool ReadStat(ReadOnlySpan<byte> message, out string name, out long value)
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
                if (hasValue || wire != 0 || !Varint(ref message, out var number) || number > long.MaxValue) return false;
                value = (long)number;
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
