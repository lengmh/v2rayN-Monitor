using System.Buffers.Binary;
using System.Text;
using V2rayNMonitor.Core;
using V2rayNMonitor.Host;
using Xunit;

namespace Core.Tests;

// Pure synthetic evidence, apart from the optional current-process Windows API smoke test.
// No real v2rayN process, configuration, endpoint, or credentials are used.
public sealed class ProcessBindingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly string Root = Path.GetFullPath(Path.Combine("synthetic", "v2rayN"));
    private static readonly string CorePath = Path.Combine(Root, "bin", "xray", "xray.exe");
    private static ProcessInfo Parent => new(100, 1, Now, Path.Combine(Root, "v2rayN.exe"), "", "v2rayN.exe");
    private static ProcessInfo Core => new(200, 100, Now.AddSeconds(1), CorePath, $"\"{CorePath}\" run -c config.json", "xray.exe");
    private static string[] Arguments => [CorePath, "run", "-c", "config.json"];
    private static NativeProcessInfo Native => new(Core.Pid, Core.StartedAt, Core.ExecutablePath, Core.CommandLine, true);

    [Fact]
    public void SingboxExtensionRequiresItsExactStandardCommandAndParent()
    {
        var path = Path.Combine(Root, "bin", "sing_box", "sing-box-client.exe");
        var core = Core with { Name = "sing-box-client.exe", ExecutablePath = path, CommandLine = $"\"{path}\" run -c config.json --disable-color" };
        string[] args = [path, "run", "-c", "config.json", "--disable-color"];
        Assert.True(ProcessBinding.TryBindCore(Parent, core, path, args, out _));
        Assert.False(ProcessBinding.TryBindCore(Parent, core, path, args[..4], out _));
        Assert.False(ProcessBinding.TryBindCore(Parent, core, path, [path, "run", "-c", "other.json", "--disable-color"], out _));
        Assert.False(ProcessBinding.TryBindCore(Parent, core with { ParentPid = 999 }, path, args, out _));
        Assert.False(ProcessBinding.TryBindCore(Parent, Core, CorePath, [CorePath, "run", "-c", "config.json", "--disable-color"], out _));
    }

    [Fact]
    public void MissingWmiFieldsCanBeFilledOnlyWithSameProcessNativeEvidence()
    {
        var enrichedParent = ProcessBinding.Reconcile(Parent with { ExecutablePath = "" },
            new(Parent.Pid, Parent.StartedAt, Parent.ExecutablePath, "", true));
        var enriched = ProcessBinding.Reconcile(Core with { ExecutablePath = "", CommandLine = "" }, Native);
        Assert.Null(enriched.Failure);
        Assert.Equal(Core.ExecutablePath, enriched.ExecutablePath);
        Assert.Equal(Core.CommandLine, enriched.CommandLine);
        Assert.True(ProcessBinding.TryBindCore(enrichedParent, enriched, CorePath, Arguments, out var reason));
        Assert.Equal("ready", reason);
    }

    [Fact]
    public void PathFallbackAloneNeverProvesCommandLineOrEffectiveConfiguration()
    {
        var enriched = ProcessBinding.Reconcile(Core with { ExecutablePath = "", CommandLine = "" }, Native with { CommandLine = "" });
        Assert.Equal(CorePath, enriched.ExecutablePath);
        Assert.False(ProcessBinding.TryBindCore(Parent, enriched, CorePath, null, out var reason));
        Assert.Equal("process_commandline_unverified", reason);
    }

    [Fact]
    public void AccessDeniedFallbackPreservesKnownWmiEvidenceButCannotInventMissingFields()
    {
        Assert.Equal(Core, ProcessBinding.Reconcile(Core, null));
        var unknown = ProcessBinding.Reconcile(Core with { ExecutablePath = "", CommandLine = "" }, null);
        Assert.Equal("process_path_unverified", unknown.Failure);
        Assert.False(ProcessBinding.TryBindCore(Parent, unknown, CorePath, Arguments, out _));
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("creation")]
    [InlineData("unknown_creation")]
    [InlineData("exited")]
    [InlineData("path")]
    [InlineData("command_line")]
    public void NativeConflictsRejectEvenCompleteWmiEvidence(string conflict)
    {
        var native = conflict switch
        {
            "pid" => Native with { Pid = 201 },
            "creation" => Native with { StartedAt = Native.StartedAt!.Value.AddTicks(10) },
            "unknown_creation" => Native with { StartedAt = null },
            "exited" => Native with { IsAlive = false },
            "path" => Native with { ExecutablePath = Path.Combine(Root, "other", "xray.exe") },
            _ => Native with { CommandLine = Native.CommandLine.Replace("config.json", "other.json") }
        };
        var enriched = ProcessBinding.Reconcile(Core, native);
        Assert.Equal("process_identity_changed", enriched.Failure);
        Assert.False(ProcessBinding.TryBindCore(Parent, enriched, CorePath, Arguments, out _));
    }

    [Fact]
    public void ReusedPidCannotCombineOldWmiParentWithNewNativeProcess()
    {
        var enriched = ProcessBinding.Reconcile(Core with { ExecutablePath = "", CommandLine = "" },
            Native with { StartedAt = Now.AddMinutes(1) });
        Assert.Equal("process_identity_changed", enriched.Failure);
        Assert.Equal("", enriched.ExecutablePath);
        Assert.Equal("", enriched.CommandLine);
    }

    [Fact]
    public void KernelCreationMustMatchWmiMicrosecondWithoutChangingCanonicalIdentity()
    {
        var native = Native with { StartedAt = Native.StartedAt!.Value.AddTicks(9) };
        var enriched = ProcessBinding.Reconcile(Core, native);
        Assert.Null(enriched.Failure);
        Assert.Equal(Core.StartedAt, enriched.StartedAt);
        Assert.Equal("process_identity_changed", ProcessBinding.Reconcile(Core,
            native with { StartedAt = Native.StartedAt!.Value.AddTicks(-1) }).Failure);
    }

    [Fact]
    public void NativeAvailabilityDoesNotCreateRestartOrClearPoisonedGeneration()
    {
        var enriched = ProcessBinding.Reconcile(Core, Native with { StartedAt = Native.StartedAt!.Value.AddTicks(9) });
        var wmiOnly = ProcessBinding.Reconcile(Core, null);
        Assert.Equal(enriched, wmiOnly);
        var nativeAgain = ProcessBinding.Reconcile(Core, Native with { StartedAt = Native.StartedAt!.Value.AddTicks(9) });
        Assert.Equal(wmiOnly, nativeAgain);
        var enrichedParent = ProcessBinding.Reconcile(Parent,
            new(Parent.Pid, Parent.StartedAt!.Value.AddTicks(9), Parent.ExecutablePath, Parent.CommandLine, true));
        Assert.Equal(enrichedParent, ProcessBinding.Reconcile(Parent, null));
        XraySnapshot Snapshot(ProcessInfo process, string generation) => new("synthetic-instance", "synthetic-node", "Synthetic",
            $"{process.Pid}:{process.StartedAt!.Value.UtcTicks}", generation, process.StartedAt.Value,
            Now, "synthetic-listener:200", new Uri("http://127.0.0.1:12345/debug/vars"));
        var guard = new XrayGenerationGuard();
        Assert.True(guard.Observe(Snapshot(enriched, "A"), out _));
        Assert.False(guard.Observe(Snapshot(enriched, "B"), out _));
        Assert.False(guard.Observe(Snapshot(wmiOnly, "B"), out var reason));
        Assert.Equal("mapping_changed", reason);
        Assert.False(guard.Observe(Snapshot(nativeAgain, "B"), out _));
        var restarted = ProcessBinding.Reconcile(Core with { StartedAt = Now.AddMinutes(1) },
            Native with { StartedAt = Now.AddMinutes(1) });
        Assert.NotEqual(enriched.StartedAt, restarted.StartedAt);
        Assert.True(guard.Observe(Snapshot(restarted, "B"), out _));
    }

    [Fact]
    public void UnknownWmiCreationDoesNotBindItsParentPidToNativeDetails()
    {
        var enriched = ProcessBinding.Reconcile(Core with { StartedAt = null }, Native);
        Assert.Equal("process_identity_unverified", enriched.Failure);
        Assert.False(ProcessBinding.TryBindCore(Parent, enriched, CorePath, Arguments, out _));
    }

    [Theory]
    [InlineData("parent_pid")]
    [InlineData("parent_pid_reuse")]
    [InlineData("executable_path")]
    [InlineData("argv_executable")]
    [InlineData("relative_executable")]
    [InlineData("config_path")]
    [InlineData("extra_argument")]
    public void StandardCoreBindingStillRequiresEveryExistingGate(string mismatch)
    {
        var parent = Parent;
        var core = Core;
        var arguments = Arguments;
        switch (mismatch)
        {
            case "parent_pid": core = core with { ParentPid = 99 }; break;
            case "parent_pid_reuse": parent = parent with { StartedAt = Now.AddMinutes(1) }; break;
            case "executable_path": core = core with { ExecutablePath = Path.Combine(Root, "other", "xray.exe") }; break;
            case "argv_executable": arguments[0] = Path.Combine(Root, "other", "xray.exe"); break;
            case "relative_executable": arguments[0] = "xray.exe"; break;
            case "config_path": arguments[3] = "other.json"; break;
            case "extra_argument": arguments = [.. arguments, "--other"]; break;
        }
        Assert.False(ProcessBinding.TryBindCore(parent, core, CorePath, arguments, out var reason));
        Assert.Equal("process_binding_mismatch", reason);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void NativeUnicodeStringIsDecodedOnlyInsideBoundedLocalBuffer(int pointerSize)
    {
        var bytes = CommandLineBuffer(Core.CommandLine, pointerSize);
        Assert.True(ProcessBinding.TryReadCommandLineBuffer(bytes, 0x1000, pointerSize, out var commandLine));
        Assert.Equal(Core.CommandLine, commandLine);
    }

    [Theory]
    [InlineData("short_header")]
    [InlineData("odd_length")]
    [InlineData("length_exceeds_maximum")]
    [InlineData("maximum_out_of_bounds")]
    [InlineData("pointer_before_buffer")]
    [InlineData("pointer_in_header")]
    [InlineData("pointer_after_buffer")]
    [InlineData("pointer_overflow")]
    [InlineData("odd_pointer")]
    [InlineData("nul_in_string")]
    [InlineData("invalid_utf16")]
    [InlineData("oversize")]
    [InlineData("unknown_pointer_size")]
    public void MalformedNativeCommandLineDoesNotBecomeTrustedBinding(string malformed)
    {
        var bytes = CommandLineBuffer("synthetic", 8);
        var pointerSize = 8;
        switch (malformed)
        {
            case "short_header": bytes = bytes[..15]; break;
            case "odd_length": BinaryPrimitives.WriteUInt16LittleEndian(bytes, 3); break;
            case "length_exceeds_maximum": BinaryPrimitives.WriteUInt16LittleEndian(bytes, 22); break;
            case "maximum_out_of_bounds": BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), 100); break;
            case "pointer_before_buffer": BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), 0x0FFE); break;
            case "pointer_in_header": BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), 0x1008); break;
            case "pointer_after_buffer": BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), 0x1100); break;
            case "pointer_overflow": BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), ulong.MaxValue); break;
            case "odd_pointer": BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), 0x1011); break;
            case "nul_in_string": bytes = CommandLineBuffer("a\0b", 8); break;
            case "invalid_utf16": BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(16), 0xD800); break;
            case "oversize": bytes = new byte[ProcessBinding.MaxCommandLineBufferBytes + 1]; break;
            case "unknown_pointer_size": pointerSize = 16; break;
        }
        Assert.False(ProcessBinding.TryReadCommandLineBuffer(bytes, 0x1000, pointerSize, out var commandLine));
        Assert.Equal("", commandLine);
    }

    [Fact]
    public void NativeReaderIsPlatformGuardedAndCanReadOnlyTheTestProcessOnWindows()
    {
        var evidence = WindowsProcessReader.Read(Environment.ProcessId);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Null(evidence); // No Windows entry point is invoked on Linux/macOS.
            return;
        }
        Assert.NotNull(evidence);
        Assert.Equal(Environment.ProcessId, evidence.Pid);
        Assert.NotNull(evidence.StartedAt);
        Assert.True(evidence.IsAlive);
        Assert.False(string.IsNullOrWhiteSpace(evidence.ExecutablePath));
        Assert.False(string.IsNullOrWhiteSpace(evidence.CommandLine));
    }

    private static byte[] CommandLineBuffer(string commandLine, int pointerSize)
    {
        var payload = Encoding.Unicode.GetBytes(commandLine);
        var headerSize = pointerSize == 8 ? 16 : 8;
        var bytes = new byte[headerSize + payload.Length + 2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, checked((ushort)payload.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), checked((ushort)(payload.Length + 2)));
        if (pointerSize == 8) BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), 0x1000UL + (ulong)headerSize);
        else BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 0x1000U + (uint)headerSize);
        payload.CopyTo(bytes, headerSize);
        return bytes;
    }
}
