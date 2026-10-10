using CounterSample = V2rayNMonitor.Core.CounterSample;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using V2rayNMonitor.Core;

namespace V2rayNMonitor.Host;

/// <summary>
/// Read-only adapter for compatible v2rayN 7.25.1 builds, ordinary single-proxy Xray mode.
/// Never starts/stops a core or writes its configuration/profile records. SQLite is opened
/// ReadOnly; SQLite can still participate in the existing database's shared-memory locking.
/// Real Windows/core integration remains a separate acceptance requirement.
/// </summary>
public sealed class WindowsDiscovery : IDisposable
{
    private const int MaxConfigurationBytes = 4 * 1024 * 1024;
    private const int MaxMetricsBytes = 1024 * 1024;
    private readonly HttpClient client = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(2)
    }) { Timeout = TimeSpan.FromSeconds(3) };
    private readonly XrayGenerationGuard guard = new();
    private readonly SemaphoreSlim gate = new(1, 1);

    // The single collection loop calls this only after Ledger.Accept/RecordGap commits.
    public void AcknowledgePersistence() => guard.AcknowledgePersistence();
    public bool RequiresBaseline => guard.RequiresBaseline;

    public async Task<CollectorReading> ReadAsync(string dataRoot, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return Unknown("unsupported_platform", "采集器只支持 Windows。尚未进行 Windows 实机验收。");
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            guard.InvalidateContinuity();
            return Unknown("needs_directory", "请选择包含 guiConfigs 的 v2rayN 数据目录。");
        }
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var readingMetrics = false;
        try
        {
            var before = Capture(dataRoot);
            if (!guard.Observe(before, out var reason)) return GuardFailure(reason, before.NodeName);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var request = new HttpRequestMessage(HttpMethod.Get, before.MetricsEndpoint);
            readingMetrics = true;
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Unknown("interface_unavailable", "Xray 统计接口暂时不可用；当前流量未知。", before.NodeName);
            if (response.Content.Headers.ContentLength is > MaxMetricsBytes)
                return Unknown("invalid_metrics", "统计响应超出允许大小；当前流量未知。", before.NodeName);
            using var responseStream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await responseStream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
            {
                if (bytes.Length + count > MaxMetricsBytes)
                    return Unknown("invalid_metrics", "统计响应超出允许大小；当前流量未知。", before.NodeName);
                bytes.Write(buffer, 0, count);
            }
            var observedAt = DateTimeOffset.UtcNow;
            readingMetrics = false;
            var after = Capture(dataRoot);
            if (!guard.Accept(before, after, out reason)) return GuardFailure(reason, after.NodeName);
            if (!XrayParser.TryReadCounters(bytes.ToArray(), out var counters))
                return Unknown("missing_counters", "proxy 上下行累计计数缺失或无效；未知不能记为零。", after.NodeName);
            return new(new CounterSample(after.InstanceId, after.NodeId, after.NodeName,
                after.ProcessIdentity, after.Generation, observedAt, counters!.Upload, counters.Download),
                "connected", "已按 v2rayN 活动配置读取 Xray proxy 原始累计字节。", after.NodeName, guard.RequiresBaseline);
        }
        catch (DiscoveryException e)
        {
            guard.InvalidateContinuity();
            return Unknown(e.State, e.SafeMessage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return Unknown("collector_stopped", "采集已停止；未完成的采样保留为缺口。"); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return Unknown("interface_unavailable", "Xray 统计读取超时；当前流量未知。"); }
        catch (HttpRequestException)
        { return Unknown("interface_unavailable", "Xray 统计接口暂时不可用；当前流量未知。"); }
        catch (IOException) when (readingMetrics)
        { return Unknown("interface_unavailable", "Xray 统计响应中断；当前流量未知。"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException
            or ManagementException or Win32Exception or ArgumentException
            or InvalidOperationException or System.Security.SecurityException)
        {
            guard.InvalidateContinuity();
            // Exception messages can contain local paths, SQL parameters, or raw response fragments.
            // Keep diagnostics deliberately constant and do not log the exception or input files.
            return Unknown("unavailable", "无法只读核实 v2rayN 配置、数据库、进程或统计接口；当前流量未知。");
        }
        finally { gate.Release(); }
    }

    [SupportedOSPlatform("windows")]
    private static XraySnapshot Capture(string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Path.IsPathFullyQualified(root)) throw Fail("needs_directory", "请选择 v2rayN 数据目录的完整路径。");
        var guiPath = Path.Combine(root, "guiConfigs", "guiNConfig.json");
        var databasePath = Path.Combine(root, "guiConfigs", "guiNDB.db");
        var configPath = Path.Combine(root, "binConfigs", "config.json");
        var guiBytes = ReadStableFile(guiPath, 1024 * 1024, out var guiStamp);
        if (!XrayParser.TryReadSelection(guiBytes, out var selection))
            throw Fail("unsupported_configuration", "无法读取 v2rayN 7.25.1 的当前节点及统计设置。");
        if (selection!.EnableTun) throw Fail("unsupported_mode", "首版暂不支持 TUN；不将其流量猜测归给当前节点。");
        if (!selection.StatisticsRequested)
            throw Fail("statistics_disabled", "请在 v2rayN 中手动开启流量统计或实时速度显示，并让 v2rayN 重新加载核心。Monitor 不会修改设置。");
        var configBytes = ReadStableFile(configPath, MaxConfigurationBytes, out var configStamp);
        var node = ReadProfile(databasePath, selection.IndexId, guiBytes, configBytes, out var configuration);
        var processes = ReadProcesses();
        var parents = processes.Where(p => p.Name.Equals("v2rayN.exe", StringComparison.OrdinalIgnoreCase)).ToArray();
        // An unreadable candidate cannot be silently discarded in favour of a readable one.
        var unverifiedParent = parents.FirstOrDefault(p => p.Failure is not null);
        if (unverifiedParent is not null) throw ProcessFailure(unverifiedParent.Failure!);
        var localAppDataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "v2rayN");
        var isAppDataRoot = SamePath(root, localAppDataRoot);
        var matchingParents = parents.Where(p => p.ExecutablePath.Length != 0
            && (SamePath(Path.GetDirectoryName(p.ExecutablePath)!, root) || isAppDataRoot)).ToArray();
        if (matchingParents.Length == 0) throw Fail("core_not_running", "未找到与所选数据目录对应的 v2rayN 进程。");
        if (matchingParents.Length != 1 || (isAppDataRoot && parents.Length != 1))
            throw Fail("ambiguous_instance", "多个 v2rayN 进程使数据目录归属不明确；暂不采集。");
        var parent = matchingParents[0];
        var version = FileVersionInfo.GetVersionInfo(parent.ExecutablePath);
        if (version.FileMajorPart != 7 || version.FileMinorPart != 25 || version.FileBuildPart != 1)
            throw Fail("unsupported_version", "当前发现路径仅针对 v2rayN 7.25.1 兼容构建；其他版本尚未核实。");
        var corePath = Path.Combine(root, "bin", "xray", "xray.exe");
        var cores = processes.Where(p => p.ParentPid == parent.Pid && p.Name.Equals("xray.exe", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (cores.Length == 0) throw Fail("core_not_running", "未找到该 v2rayN 启动的标准 Xray config.json 进程；其他内核或启动方式暂不支持。");
        if (cores.Length != 1) throw Fail("ambiguous_instance", "发现多个 Xray 子进程；暂不归属流量。");
        var core = cores[0];
        if (!ProcessBinding.TryBindCore(parent, core, corePath, ReadArguments(core.CommandLine), out var bindingReason))
            throw ProcessFailure(bindingReason);
        var coreStartedAt = core.StartedAt!.Value;
        var parentStartedAt = parent.StartedAt!.Value;
        var listenerIdentity = ReadListenerIdentity(configuration!.MetricsEndpoint, core.Pid);
        if (Stamp(guiPath) != guiStamp || Stamp(configPath) != configStamp)
            throw Fail("snapshot_changed", "配置在读取过程中发生变化；本次采样保留为缺口。");
        var instanceId = XrayParser.Hash(root.ToUpperInvariant());
        var processIdentity = XrayParser.Hash(string.Join('|', core.Pid, coreStartedAt.UtcTicks, core.ExecutablePath.ToUpperInvariant(),
            parent.Pid, parentStartedAt.UtcTicks, parent.ExecutablePath.ToUpperInvariant()));
        var generation = XrayParser.Hash(configBytes) + ":" + configStamp.WrittenAt.UtcTicks.ToString(CultureInfo.InvariantCulture);
        return new(instanceId, selection.IndexId, node, processIdentity, generation, coreStartedAt,
            configStamp.WrittenAt, listenerIdentity, configuration.MetricsEndpoint);
    }

    internal static string ReadProfile(string path, string indexId, byte[] guiBytes, byte[] configBytes, out XrayEffectiveConfiguration configuration)
    {
        configuration = null!;
        if (!File.Exists(path)) throw Fail("missing_database", "所选目录中没有 guiConfigs/guiNDB.db。");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false,
            DefaultTimeout = 1
        }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Credentials are used only in this stack frame to check the selected configuration.
        // They are never copied into the monitor's database, public API, or diagnostics.
        command.CommandText = """
            SELECT IndexId, Remarks, ConfigType, CoreType, Address, Port, Password, Username,
                Network, ProtoExtra, TransportExtra, StreamSecurity, Sni, Alpn, Fingerprint,
                PublicKey, ShortId, SpiderX, Mldsa65Verify, AllowInsecure, Cert, CertSha,
                EchConfigList, VerifyPeerCertByName, Finalmask
            FROM ProfileItem WHERE IndexId = $id
            """;
        command.Parameters.AddWithValue("$id", indexId);
        int type;
        int? coreType;
        string name, address, password, username;
        int port;
        var selectedFields = new Dictionary<string, string?>(StringComparer.Ordinal);
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw Fail("unknown_node", "当前 IndexId 在配置库中不存在；不按名称推测归属。");
            name = reader.IsDBNull(1) ? "" : reader.GetString(1);
            type = reader.GetInt32(2);
            coreType = reader.IsDBNull(3) ? null : reader.GetInt32(3);
            if (type == 11)
                throw Fail("unsupported_core", "此版本的 AnyTLS 使用 sing-box，当前 Xray 采集器尚未覆盖；流量保持未知。");
            // Classify the selected protocol before parsing Xray-only fields so AnyTLS does
            // not misleadingly ask the user to enable Xray metrics in a sing-box config.
            if (!XrayParser.TryReadConfiguration(configBytes, out var parsedConfiguration, out var configurationReason))
                throw configurationReason is "statistics_unavailable" or "statistics_disabled"
                    ? Fail(configurationReason, "有效配置没有可用的回环 Xray metrics 统计；请在 v2rayN 中检查统计设置。")
                    : Fail("unsupported_mode", "仅支持可核实的单 proxy 出站；自定义配置、策略组、代理链和多代理出站暂不支持。");
            configuration = parsedConfiguration!;
            if (XrayParser.ProtocolForConfigType(type) != configuration.Protocol)
                throw Fail("unsupported_mode", "当前节点类型或内核与已读取的 Xray 配置不一致。");
            address = reader.GetString(4);
            port = reader.GetInt32(5);
            password = reader.IsDBNull(6) ? "" : reader.GetString(6);
            username = reader.IsDBNull(7) ? "" : reader.GetString(7);
            for (var i = 8; i < reader.FieldCount; i++)
                selectedFields.Add(reader.GetName(i), reader.IsDBNull(i) ? null : reader.GetString(i));
            if (reader.Read()) throw Fail("ambiguous_node", "当前节点标识不唯一；暂不采集。");
        }
        if (!XrayParser.TryProfileIdentity(type, coreType, address, port, password, username, out var identity))
            throw Fail("unsupported_mode", "当前节点类型或内核与已读取的 Xray 配置不一致。");
        if (identity != configuration.ServerIdentity)
            throw Fail("mapping_changed", "当前节点与核心有效配置不一致；等待重新加载后再采集。");
        if (!XrayParser.MatchesSelectedConnection(selectedFields, guiBytes, configBytes, out var matchReason))
            throw matchReason == "unsupported_connection"
                ? Fail(matchReason, "当前连接包含尚未核实的配置字段；本次采样保留为缺口。")
                : Fail("mapping_changed", "活动配置的协议、传输或 TLS 字段与核心有效配置不一致；等待重新加载后再采集。");
        // Other IndexIds (in this or another subscription) are intentionally irrelevant.
        // Attribution follows the selected record; it is not proof of core-side uniqueness.
        transaction.Commit(); // Ends a read-only transaction; no profile or setting is changed.
        return string.IsNullOrWhiteSpace(name) ? "未命名节点" : name;
    }

    private static byte[] ReadStableFile(string path, int maximumBytes, out FileStamp stamp)
    {
        stamp = Stamp(path);
        if (stamp.Length is <= 0 || stamp.Length > maximumBytes)
            throw Fail("invalid_configuration", "配置文件为空或过大；暂不读取。");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[checked((int)stamp.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1 || Stamp(path) != stamp)
            throw Fail("snapshot_changed", "配置在读取过程中发生变化；本次采样保留为缺口。");
        return bytes;
    }

    private static FileStamp Stamp(string path)
    {
        var info = new FileInfo(path);
        return new(info.Length, new DateTimeOffset(info.LastWriteTimeUtc));
    }

    [SupportedOSPlatform("windows")]
    private static List<ProcessInfo> ReadProcesses()
    {
        var result = new List<ProcessInfo>();
        using var query = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, CreationDate, ExecutablePath, CommandLine, Name FROM Win32_Process WHERE Name = 'v2rayN.exe' OR Name = 'xray.exe'");
        query.Options.Timeout = TimeSpan.FromSeconds(3);
        using var rows = query.Get();
        foreach (ManagementObject row in rows)
        {
            using (row)
            {
                var created = row["CreationDate"] as string;
                // Wildcard CIM dates are not a known creation identity.
                DateTimeOffset? startedAt = string.IsNullOrEmpty(created) || created.Contains('*') ? null
                    : new DateTimeOffset(ManagementDateTimeConverter.ToDateTime(created)).ToUniversalTime();
                var wmi = new ProcessInfo(Convert.ToInt32(row["ProcessId"], CultureInfo.InvariantCulture),
                    Convert.ToInt32(row["ParentProcessId"], CultureInfo.InvariantCulture),
                    startedAt, row["ExecutablePath"] as string ?? "", row["CommandLine"] as string ?? "", row["Name"] as string ?? "");
                result.Add(ProcessBinding.Reconcile(wmi, WindowsProcessReader.Read(wmi.Pid)));
            }
        }
        return result;
    }

    private static string[]? ReadArguments(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || commandLine.Contains('\0')) return null;
        var argv = CommandLineToArgvW(commandLine, out var count);
        if (argv == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (count != 4) return [];
            string Arg(int index) => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * IntPtr.Size)) ?? "";
            return [Arg(0), Arg(1), Arg(2), Arg(3)];
        }
        finally { _ = LocalFree(argv); }
    }

    private static string ReadListenerIdentity(Uri endpoint, int expectedPid)
    {
        var address = IPAddress.Parse(endpoint.Host.Trim('[', ']'));
        var ipv6 = address.AddressFamily == AddressFamily.InterNetworkV6;
        var family = ipv6 ? 23 : 2; // AF_INET6 / AF_INET
        var size = 0;
        var error = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0); // TCP_TABLE_OWNER_PID_LISTENER
        if (error != 122 && error != 0) throw new Win32Exception((int)error);
        var memory = Marshal.AllocHGlobal(size);
        try
        {
            error = GetExtendedTcpTable(memory, ref size, false, family, 3, 0);
            if (error != 0) throw new Win32Exception((int)error);
            var count = Marshal.ReadInt32(memory);
            var rowSize = ipv6 ? 56 : 24;
            if (count < 0 || 4L + (long)count * rowSize > size) throw Fail("listener_unverified", "无法核实统计监听进程。");
            var matches = 0;
            for (var i = 0; i < count; i++)
            {
                var row = IntPtr.Add(memory, 4 + i * rowSize);
                var portOffset = ipv6 ? 20 : 8;
                var port = (Marshal.ReadByte(row, portOffset) << 8) | Marshal.ReadByte(row, portOffset + 1);
                if (port != endpoint.Port) continue;
                var rawAddress = new byte[ipv6 ? 16 : 4];
                Marshal.Copy(IntPtr.Add(row, ipv6 ? 0 : 4), rawAddress, 0, rawAddress.Length);
                var localAddress = new IPAddress(rawAddress);
                if (!localAddress.Equals(address)) continue;
                var pid = Marshal.ReadInt32(row, ipv6 ? 52 : 20);
                if (pid != expectedPid) throw Fail("listener_unverified", "统计端口属于其他进程；暂不读取。");
                matches++;
            }
            if (matches != 1) throw Fail("listener_unverified", "未找到唯一的 Xray 回环统计监听；当前流量未知。");
            return $"{address}:{endpoint.Port}:{expectedPid}";
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    private static bool SamePath(string a, string b) => ProcessBinding.SamePath(a, b);
    private static DiscoveryException ProcessFailure(string state) => Fail(state, state switch
    {
        "process_commandline_unverified" => "普通权限无法读取 Xray 启动命令行，无法核实有效配置归属；本次采样保留为缺口。Monitor 不会自动提权。",
        "process_path_unverified" => "普通权限无法核实 v2rayN 或 Xray 程序路径；本次采样保留为缺口。Monitor 不会自动提权。",
        "process_identity_changed" => "进程身份或启动信息在读取时不一致；本次采样保留为缺口。",
        "process_binding_mismatch" => "Xray 父子进程、程序路径或标准启动命令不匹配；不猜测有效配置归属。",
        _ => "无法只读核实进程创建身份；本次采样保留为缺口。"
    });
    private CollectorReading Unknown(string state, string message, string? node = null) => new(null, state, message, node, guard.RequiresBaseline);
    private CollectorReading GuardFailure(string state, string? node) => Unknown(state,
        state == "waiting_for_core" ? "配置已更新但核心尚未重新加载；本次采样保留为缺口。"
        : "节点、配置、进程或监听在采样边界发生变化；本次采样保留为缺口。", node);
    private static DiscoveryException Fail(string state, string message) => new(state, message);
    public void Dispose() { client.Dispose(); gate.Dispose(); }

    private sealed record FileStamp(long Length, DateTimeOffset WrittenAt);
    internal sealed class DiscoveryException(string state, string safeMessage) : Exception
    {
        public string State { get; } = state;
        public string SafeMessage { get; } = safeMessage;
    }
    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);
    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
