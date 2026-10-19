using System.Buffers.Binary;
using System.Text;

namespace V2rayNMonitor.Host;

// These values live only for a discovery snapshot. Never persist command lines or raw paths.
internal sealed record ProcessInfo(int Pid, int ParentPid, DateTimeOffset? StartedAt,
    string ExecutablePath, string CommandLine, string Name, string? Failure = null);

internal sealed record NativeProcessInfo(int Pid, DateTimeOffset? StartedAt,
    string ExecutablePath, string CommandLine, bool IsAlive);

/// <summary>Pure reconciliation shared with synthetic tests; no Windows API calls.</summary>
internal static class ProcessBinding
{
    internal const int MaxCommandLineBufferBytes = 65536 + 16;

    internal static ProcessInfo Reconcile(ProcessInfo wmi, NativeProcessInfo? native)
    {
        if (wmi.Pid <= 0 || wmi.ParentPid < 0 || wmi.StartedAt is null)
            return wmi with { Failure = "process_identity_unverified" };
        if (native is not null)
        {
            // CIM_DATETIME has microsecond precision; FILETIME has 100 ns precision.
            // Do not allow a millisecond/second tolerance that could hide PID reuse.
            if (!native.IsAlive || native.Pid != wmi.Pid || native.StartedAt is null
                || wmi.StartedAt.Value.UtcTicks / 10 != native.StartedAt.Value.UtcTicks / 10)
                return wmi with { Failure = "process_identity_changed" };
            if (Present(wmi.ExecutablePath) && Present(native.ExecutablePath)
                && !SamePath(wmi.ExecutablePath, native.ExecutablePath))
                return wmi with { Failure = "process_identity_changed" };
            if (Present(wmi.CommandLine) && Present(native.CommandLine)
                && !string.Equals(wmi.CommandLine, native.CommandLine, StringComparison.Ordinal))
                return wmi with { Failure = "process_identity_changed" };
            wmi = wmi with
            {
                ExecutablePath = Present(wmi.ExecutablePath) ? wmi.ExecutablePath : native.ExecutablePath,
                CommandLine = Present(wmi.CommandLine) ? wmi.CommandLine : native.CommandLine
            };
        }
        // Keep one canonical UTC precision on both native and WMI-only paths. Changing
        // only the evidence source is not a core restart and must not clear a poisoned guard.
        wmi = wmi with { StartedAt = new DateTimeOffset(wmi.StartedAt.Value.UtcTicks / 10 * 10, TimeSpan.Zero) };
        return Present(wmi.ExecutablePath) && Path.IsPathFullyQualified(wmi.ExecutablePath)
            ? wmi : wmi with { Failure = "process_path_unverified" };
    }

    internal static bool TryBindCore(ProcessInfo parent, ProcessInfo core, string expectedPath,
        IReadOnlyList<string>? arguments, out string reason)
    {
        reason = parent.Failure ?? core.Failure ?? "process_identity_unverified";
        if (parent.Failure is not null || core.Failure is not null
            || parent.StartedAt is null || core.StartedAt is null) return false;
        reason = "process_binding_mismatch";
        if (parent.Pid <= 0 || core.Pid <= 0 || core.Pid == parent.Pid || core.ParentPid != parent.Pid
            || core.StartedAt < parent.StartedAt || !SamePath(expectedPath, core.ExecutablePath)) return false;
        reason = "process_commandline_unverified";
        if (!Present(core.CommandLine) || arguments is null) return false;
        reason = "process_binding_mismatch";
        var singbox = core.Name.Equals("sing-box-client.exe", StringComparison.OrdinalIgnoreCase);
        if (arguments.Count != (singbox ? 5 : 4) || !SamePath(arguments[0], core.ExecutablePath)
            || arguments[1] != "run" || arguments[2] != "-c" || arguments[3] != "config.json"
            || (singbox && arguments[4] != "--disable-color")) return false;
        reason = "ready";
        return true;
    }

    internal static bool SamePath(string a, string b) => Present(a) && Present(b)
        && Path.IsPathFullyQualified(a) && Path.IsPathFullyQualified(b)
        && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    // Parse only our bounded local copy. Never dereference the returned UNICODE_STRING pointer.
    internal static bool TryReadCommandLineBuffer(ReadOnlySpan<byte> buffer, ulong address,
        int pointerSize, out string commandLine)
    {
        commandLine = "";
        if (pointerSize is not (4 or 8)) return false;
        var headerSize = pointerSize == 8 ? 16 : 8;
        if (buffer.Length < headerSize || buffer.Length > MaxCommandLineBufferBytes) return false;
        var length = BinaryPrimitives.ReadUInt16LittleEndian(buffer);
        var maximumLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer[2..]);
        var pointer = pointerSize == 8
            ? BinaryPrimitives.ReadUInt64LittleEndian(buffer[8..])
            : BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]);
        if (length == 0 || (length & 1) != 0 || (maximumLength & 1) != 0 || length > maximumLength
            || pointer < address || pointer - address < (ulong)headerSize
            || pointer - address > (ulong)buffer.Length) return false;
        var offset = (int)(pointer - address);
        if ((offset & 1) != 0 || maximumLength > buffer.Length - offset) return false;
        try
        {
            var value = new UnicodeEncoding(false, false, true).GetString(buffer.Slice(offset, length));
            if (!Present(value) || value.Contains('\0')) return false;
            commandLine = value;
            return true;
        }
        catch (DecoderFallbackException) { return false; }
    }

    private static bool Present(string value) => !string.IsNullOrWhiteSpace(value);
}
