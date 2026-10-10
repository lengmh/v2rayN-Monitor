using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace V2rayNMonitor.Host;

/// <summary>Read-only, limited-access enrichment. No VM_READ, debug privilege, or elevation.</summary>
internal static class WindowsProcessReader
{
    private const uint QueryLimitedInformation = 0x1000;
    private const uint StillActive = 259;
    private const int ProcessCommandLineInformation = 60;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int StatusBufferTooSmall = unchecked((int)0xC0000023);
    private const int StatusBufferOverflow = unchecked((int)0x80000005);

    internal static NativeProcessInfo? Read(int pid)
    {
        if (!OperatingSystem.IsWindows() || pid <= 0) return null;
        using var handle = OpenProcess(QueryLimitedInformation, false, pid);
        if (handle.IsInvalid) return null;
        var actualPid = GetProcessId(handle);
        if (!GetProcessTimes(handle, out var created, out _, out _, out _))
            return new((int)actualPid, null, "", "", false);
        var path = new StringBuilder(32768);
        var size = path.Capacity;
        var image = QueryFullProcessImageNameW(handle, 0, path, ref size) ? path.ToString() : "";
        var commandLine = ReadCommandLine(handle);
        var alive = GetExitCodeProcess(handle, out var exitCode) && exitCode == StillActive;
        return new(checked((int)actualPid), new DateTimeOffset(DateTime.FromFileTimeUtc(created)), image, commandLine, alive);
    }

    private static string ReadCommandLine(SafeProcessHandle handle)
    {
        // Class 60 is an internal Windows interface, not a supported public API contract.
        // Microsoft requires runtime linking for NtQueryInformationProcess. Resolve only
        // from System32; unsupported/access-denied/malformed results remain unknown.
        // Primary implementation evidence: psutil release-7.0.0, arch/windows/proc_info.c.
        if (!NativeLibrary.TryLoad("ntdll.dll", typeof(WindowsProcessReader).Assembly,
            DllImportSearchPath.System32, out var library)) return "";
        try
        {
            if (!NativeLibrary.TryGetExport(library, "NtQueryInformationProcess", out var address)) return "";
            var query = Marshal.GetDelegateForFunctionPointer<QueryProcessInformation>(address);
            var status = query(handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out var size);
            if (status is not (StatusInfoLengthMismatch or StatusBufferTooSmall or StatusBufferOverflow)
                || size < Marshal.SizeOf<UnicodeString>() || size > ProcessBinding.MaxCommandLineBufferBytes) return "";
            var memory = Marshal.AllocHGlobal(size);
            try
            {
                // Exactly one bounded read; a growing/changing response is a gap this time.
                status = query(handle, ProcessCommandLineInformation, memory, size, out var returned);
                if (status != 0 || returned < Marshal.SizeOf<UnicodeString>() || returned > size) return "";
                var bytes = new byte[returned];
                Marshal.Copy(memory, bytes, 0, returned);
                var baseAddress = IntPtr.Size == 8 ? unchecked((ulong)memory.ToInt64()) : unchecked((uint)memory.ToInt32());
                return ProcessBinding.TryReadCommandLineBuffer(bytes, baseAddress, IntPtr.Size, out var value) ? value : "";
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        finally { NativeLibrary.Free(library); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int QueryProcessInformation(SafeProcessHandle process, int informationClass,
        IntPtr information, int informationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
}
