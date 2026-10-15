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
/// Read-only adapter for compatible v2rayN 7.25.1 builds and a verified routed proxy.
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
    internal const string ApprovedSingboxSha256 = "C053F7C9D82C7FC241E60045ADFB399A44F4FDEB4E12CEF91C5F95244E3301BA";
    internal const string ApprovedMemberSingboxSha256 = "B1C6F3AA48F21AE070EF283D10AE946C189421F7C9F498CCAA5657D2B0E7E15E";
    // CI 38751295368, artifact 11980964000: outer digest, provenance and inner EXE verified.
    internal const string ApprovedProbeSingboxSha256 = "595B4B3FEDE70FC23CA645DCF7B5A4AB6F91E5E7938DB0CA77D50EB0F8376695";
    private string? verifiedExtension;

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
            readingMetrics = true;
            var counters = before.CoreName == "sing-box"
                ? await V2RayStatistics.ReadAsync(client, before.MetricsEndpoint, timeout.Token).ConfigureAwait(false)
                : await ReadXrayCounters(before.MetricsEndpoint, timeout.Token).ConfigureAwait(false);
            var observedAt = DateTimeOffset.UtcNow;
            readingMetrics = false;
            var after = Capture(dataRoot);
            if (!guard.Accept(before, after, out reason)) return GuardFailure(reason, after.NodeName);
            if (counters is null)
                return Unknown("missing_counters", "proxy 上下行累计计数缺失或无效；未知不能记为零。", after.NodeName);
            return new(new CounterSample(after.InstanceId, after.NodeId, after.NodeName,
                after.ProcessIdentity, after.Generation, observedAt, counters!.Upload, counters.Download,
                after.Scope?.IsPartial == true),
                "connected", $"已按 v2rayN 活动配置读取 {after.CoreName} proxy 原始累计字节。",
                after.NodeName, guard.RequiresBaseline, after.CoreName, after.Scope);
        }
        catch (DiscoveryException e)
        {
            guard.InvalidateContinuity();
            return Unknown(e.State, e.SafeMessage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return Unknown("collector_stopped", "采集已停止；未完成的采样保留为缺口。"); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return Unknown("interface_unavailable", "内核统计读取超时；当前流量未知。"); }
        catch (HttpRequestException)
        { return Unknown("interface_unavailable", "内核统计接口暂时不可用；当前流量未知。"); }
        catch (IOException) when (readingMetrics)
        { return Unknown("interface_unavailable", "内核统计响应中断；当前流量未知。"); }
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

    public async Task<ScopeCollectorReading> ReadScopesAsync(string dataRoot, CancellationToken cancellationToken,
        bool includeProbes = false)
    {
        if (!OperatingSystem.IsWindows()) return new(null, "unsupported_platform", "多范围发现只支持 Windows。");
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            guard.InvalidateContinuity();
            return new(null, "needs_directory", "请选择 v2rayN 数据目录。", InvalidateContinuity: true);
        }
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ScopeCapture CaptureMembers()
            {
                // This delegate is invoked by a portable testable pipeline; keep its native boundary explicit.
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                var snapshot = Capture(dataRoot, true, out var catalog, out var enabled);
                return new(snapshot, catalog, enabled);
            }
            return await ReadScopesBoundAsync(CaptureMembers, async (capture, token) =>
            {
                try
                {
                    if (capture.Snapshot.CoreName != "sing-box")
                        return (null, await ReadXrayCounters(capture.Snapshot.MetricsEndpoint, token).ConfigureAwait(false), null);
                    var statistics = await V2RayStatistics.ReadScopesAsync(client, capture.Snapshot.MetricsEndpoint,
                        capture.Catalog!.Candidates.Keys.ToArray(), token).ConfigureAwait(false);
                    return (statistics?.Members, null, statistics?.Probes);
                }
                catch (IOException) { throw new HttpRequestException("Statistics response interrupted."); }
            },
                cancellationToken, includeProbes).ConfigureAwait(false);
        }
        catch (DiscoveryException e)
        { guard.InvalidateContinuity(); return new(null, e.State, e.SafeMessage, InvalidateContinuity: true); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return new(null, "collector_stopped", "采集已停止。", InvalidateContinuity: guard.RequiresBaseline); }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException)
        { return new(null, "interface_unavailable", "统计接口暂时不可用，未知不能记为零。", InvalidateContinuity: guard.RequiresBaseline); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException
            or ManagementException or Win32Exception or ArgumentException or InvalidOperationException or System.Security.SecurityException)
        { guard.InvalidateContinuity(); return new(null, "unavailable", "无法核实进程、配置或身份，本次保留缺口。", InvalidateContinuity: true); }
        finally { gate.Release(); }
    }

    // The test seam supplies synthetic captures/counters; native process discovery is not bypassed in production.
    internal async Task<ScopeCollectorReading> ReadScopesBoundAsync(Func<ScopeCapture> capture,
        Func<ScopeCapture, CancellationToken, Task<(V2RayMemberStatistics? Members, XrayCounters? Legacy, V2RayProbeStatistics? Probes)>> read,
        CancellationToken cancellationToken, bool includeProbes = false)
    {
        var before = capture();
        if (!guard.Observe(before.Snapshot, out var reason))
            return new(null, reason, "配置、核心或身份尚未稳定。", before.Snapshot.NodeName, guard.RequiresBaseline);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var counters = await read(before, timeout.Token).ConfigureAwait(false);
        var at = DateTimeOffset.UtcNow;
        var after = capture();
        if (!guard.Accept(before.Snapshot, after.Snapshot, out reason))
            return new(null, reason, "采样前后核心、配置或身份变化，本次保留缺口。", after.Snapshot.NodeName, true);
        ScopeBatch batch;
        ProbeCollectionStatus? probeStatus = null;
        if (after.Snapshot.CoreName == "sing-box")
        {
            // An unusable whole RPC remains a complete directory of unavailable scopes.
            var stats = counters.Members ?? new V2RayMemberStatistics(new Dictionary<string, XrayCounters>(),
                new Dictionary<string, string>(), new Dictionary<string, XrayCounters>(), new Dictionary<string, string>());
            if (includeProbes)
            {
                var probes = counters.Probes ?? new V2RayProbeStatistics(new Dictionary<string, XrayCounters>(),
                    new Dictionary<string, string>(), new Dictionary<string, XrayCounters>(), new Dictionary<string, string>());
                batch = ScopeCollector.ProjectWithProbes(after, stats, probes, at, guard.RequiresBaseline);
                var readings = batch.Readings.Where(x => x.Scope.Basis.StartsWith("probe_urltest_", StringComparison.Ordinal)).ToArray();
                var state = counters.Probes is null ? "unavailable"
                    : probes.Members.Count == 0 && probes.Totals.Count == 0 &&
                        probes.UnavailableMembers.Values.Concat(probes.UnavailableTotals.Values).All(x => x == "missing_counter") ? "unsupported"
                    : readings.Any(x => x.UnavailableReason is not null || x.PartialCoverage) ? "partial" : "available";
                probeStatus = new(true, state, state switch
                {
                    "unsupported" => "当前核心未提供 URLTest 探测计数；业务采集继续，探测量保持未知。",
                    "unavailable" => "URLTest 探测响应不可用，保留缺口。",
                    "partial" => "URLTest 探测部分范围计数未知，未补零。",
                    _ => "已读取 URLTest 探测流字节；业务与探测分别保存，首次基线不回填。"
                });
            }
            else batch = ScopeCollector.Project(after, stats, at, guard.RequiresBaseline);
        }
        else if (counters.Legacy is { } legacy)
        {
            batch = ScopeCollector.ProjectLegacy(after.Snapshot, legacy, at, guard.RequiresBaseline);
            if (includeProbes) probeStatus = new(true, "unsupported", "当前 Xray 路径未接入内部探测计量；业务采集继续。");
        }
        else return new(null, "missing_counters", "proxy 计数不可用。", after.Snapshot.NodeName, guard.RequiresBaseline);
        return new(batch, "connected", after.Snapshot.CoreName == "sing-box"
            ? "实际成员维度；business、excluded、unknown 单独保存，不相加。"
            : "Xray proxy 延续 legacy_proxy 口径，不冒称实际成员。", after.Snapshot.NodeName, guard.RequiresBaseline, probeStatus);
    }

    private async Task<XrayCounters?> ReadXrayCounters(Uri endpoint, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Statistics interface is unavailable.");
        if (response.Content.Headers.ContentLength is > MaxMetricsBytes) return null;
        using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await responseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (bytes.Length + count > MaxMetricsBytes) return null;
            bytes.Write(buffer, 0, count);
        }
        return XrayParser.TryReadCounters(bytes.ToArray(), out var counters) ? counters : null;
    }

    [SupportedOSPlatform("windows")]
    private XraySnapshot Capture(string root) => Capture(root, false, out _, out _);

    [SupportedOSPlatform("windows")]
    private XraySnapshot Capture(string root, bool memberMode, out SingboxMemberCatalog? catalog,
        out IReadOnlyList<string> enabledMembers)
    {
        catalog = null; enabledMembers = [];
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
        var singbox = SingboxParser.HasAnyTlsProxy(configBytes);
        var statisticsPath = Path.Combine(root, "bin", "sing_box", "monitor-stats.json");
        FileStamp? statisticsStamp = null;
        byte[]? statisticsBytes = null;
        if (singbox && File.Exists(statisticsPath))
            statisticsBytes = ReadStableFile(statisticsPath, 4096, out statisticsStamp);
        var node = ReadProfile(databasePath, selection.IndexId, guiBytes, configBytes, out var configuration, statisticsBytes, memberMode);
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
        if (singbox && version.ProductVersion != SingboxParser.ClientVersion)
            throw Fail("unsupported_version", "AnyTLS 配置核对仅针对已核实的定制 v2rayN 7.25.1 构建；当前客户端版本尚未核实。");
        var corePath = singbox ? Path.Combine(root, "bin", "sing_box", "sing-box-client.exe")
            : Path.Combine(root, "bin", "xray", "xray.exe");
        var cores = processes.Where(p => p.ParentPid == parent.Pid && IsCoreName(p.Name)).ToArray();
        if (cores.Length == 0) throw Fail("core_not_running", "未找到该 v2rayN 启动的标准 config.json 核心进程。");
        if (cores.Length != 1) throw Fail("ambiguous_instance", "发现多个核心子进程；暂不归属流量。");
        var core = cores[0];
        if (singbox && !core.Name.Equals("sing-box-client.exe", StringComparison.OrdinalIgnoreCase))
            throw Fail("unsupported_core", "当前 sing-box 尚未加载 Monitor 统计扩展；请安装经核实的扩展后择时重新加载核心。");
        if (!ProcessBinding.TryBindCore(parent, core, corePath, ReadArguments(core.CommandLine), out var bindingReason))
            throw ProcessFailure(bindingReason);
        var coreStartedAt = core.StartedAt!.Value;
        var parentStartedAt = parent.StartedAt!.Value;
        var listenerIdentity = ReadListenerIdentity(configuration!.MetricsEndpoint, core.Pid);
        if (singbox) VerifyExtension(core, memberMode);
        if (Stamp(guiPath) != guiStamp || Stamp(configPath) != configStamp
            || (singbox && Stamp(statisticsPath) != statisticsStamp))
            throw Fail("snapshot_changed", "配置在读取过程中发生变化；本次采样保留为缺口。");
        var instanceId = XrayParser.Hash(root.ToUpperInvariant());
        var processIdentity = XrayParser.Hash(string.Join('|', core.Pid, coreStartedAt.UtcTicks, core.ExecutablePath.ToUpperInvariant(),
            parent.Pid, parentStartedAt.UtcTicks, parent.ExecutablePath.ToUpperInvariant()));
        var generation = XrayParser.Hash(configBytes) + ":" + configStamp.WrittenAt.UtcTicks.ToString(CultureInfo.InvariantCulture);
        if (singbox) generation += ":" + XrayParser.Hash(statisticsBytes!) + ":" + statisticsStamp!.WrittenAt.UtcTicks;
        var writtenAt = singbox && statisticsStamp!.WrittenAt > configStamp.WrittenAt ? statisticsStamp.WrittenAt : configStamp.WrittenAt;
        var snapshot = new XraySnapshot(instanceId, selection.IndexId, node, processIdentity, generation, coreStartedAt,
            writtenAt, listenerIdentity, configuration.MetricsEndpoint, configuration.CoreName, configuration.Scope);
        if (singbox && memberMode)
        {
            if (!SingboxParser.TryReadMemberCatalog(snapshot, configBytes, out catalog, out _) ||
                !SingboxParser.TryReadMemberStatisticsSettings(statisticsBytes!, out _, out enabledMembers))
                throw Fail("unsupported_member_configuration", "成员目录或 sidecar 范围无法核实；不猜测归属。");
            var candidates = catalog!.Candidates;
            if (enabledMembers.Any(x => !candidates.ContainsKey(x)))
                throw Fail("unsupported_member_configuration", "成员目录或 sidecar 范围无法核实；不猜测归属。");
        }
        return snapshot;
    }

    private void VerifyExtension(ProcessInfo core, bool memberMode = false)
    {
        var stamp = Stamp(core.ExecutablePath);
        var identity = string.Join('|', core.Pid, core.StartedAt!.Value.UtcTicks, core.ExecutablePath, stamp, memberMode);
        if (identity == verifiedExtension) return;
        using var stream = new FileStream(core.ExecutablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        if (!IsApprovedExtension(hash, memberMode) || Stamp(core.ExecutablePath) != stamp)
            throw Fail("unsupported_core", "sing-box 扩展与此预览已核实的构建不一致；当前流量保持未知。");
        verifiedExtension = identity;
    }

    internal static bool IsApprovedExtension(string hash, bool memberMode) => memberMode
        ? hash is ApprovedMemberSingboxSha256 or ApprovedProbeSingboxSha256
        : hash == ApprovedSingboxSha256;

    private static bool IsCoreName(string name) => name.Equals("xray.exe", StringComparison.OrdinalIgnoreCase)
        || name.Equals("sing-box.exe", StringComparison.OrdinalIgnoreCase)
        || name.Equals("sing-box-client.exe", StringComparison.OrdinalIgnoreCase);

    internal static string ReadProfile(string path, string indexId, byte[] guiBytes, byte[] configBytes,
        out XrayEffectiveConfiguration configuration, byte[]? statisticsBytes = null, bool allowMemberStatistics = false)
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
            if (type == 11 && statisticsBytes is null)
                throw Fail("unsupported_core", "此版本的 AnyTLS 需要 Monitor 统计扩展及回环端点文件；当前流量保持未知。");
            // Classify the selected protocol before parsing Xray-only fields so AnyTLS does
            // not misleadingly ask the user to enable Xray metrics in a sing-box config.
            var parsed = type == 11
                ? SingboxParser.TryReadConfiguration(configBytes, statisticsBytes!, out var parsedConfiguration, out var configurationReason, allowMemberStatistics)
                : XrayParser.TryReadConfiguration(configBytes, out parsedConfiguration, out configurationReason);
            if (!parsed)
                throw configurationReason == "invalid_statistics_configuration"
                    ? Fail(configurationReason, "Monitor 统计端点文件无效；仅允许一个数值回环地址及非零端口，当前流量保持未知。")
                    : configurationReason is "statistics_unavailable" or "statistics_disabled"
                    ? Fail(configurationReason, "有效配置没有可用的回环 Xray metrics 统计；请在 v2rayN 中检查统计设置。")
                    : type == 11
                    ? Fail("unsupported_mode", "无法核实独立的 AnyTLS proxy 计量范围；proxy 链、TUN、endpoints 或已有自定义统计配置尚未支持。")
                    : Fail("unsupported_mode", "仅支持可核实的单 proxy 出站；自定义配置、策略组、代理链和多代理出站暂不支持。");
            configuration = parsedConfiguration!;
            if ((type == 11 ? "anytls" : XrayParser.ProtocolForConfigType(type)) != configuration.Protocol)
                throw Fail("unsupported_mode", "当前节点类型或内核与已读取的 Xray 配置不一致。");
            address = reader.GetString(4);
            port = reader.GetInt32(5);
            password = reader.IsDBNull(6) ? "" : reader.GetString(6);
            username = reader.IsDBNull(7) ? "" : reader.GetString(7);
            for (var i = 8; i < reader.FieldCount; i++)
                selectedFields.Add(reader.GetName(i), reader.IsDBNull(i) ? null : reader.GetString(i));
            if (reader.Read()) throw Fail("ambiguous_node", "当前节点标识不唯一；暂不采集。");
        }
        var identity = type == 11 ? XrayParser.ServerIdentity("anytls", address, port, "", password) : "";
        if (type != 11 && !XrayParser.TryProfileIdentity(type, coreType, address, port, password, username, out identity))
            throw Fail("unsupported_mode", "当前节点类型或内核与已读取的 Xray 配置不一致。");
        if (identity != configuration.ServerIdentity)
            throw Fail("mapping_changed", "当前节点与核心有效配置不一致；等待重新加载后再采集。");
        var matches = type == 11
            ? SingboxParser.MatchesSelectedConnection(selectedFields, guiBytes, configBytes, out var matchReason)
            : XrayParser.MatchesSelectedConnection(selectedFields, guiBytes, configBytes, out matchReason);
        if (!matches)
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
        using var query = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, CreationDate, ExecutablePath, CommandLine, Name FROM Win32_Process WHERE Name = 'v2rayN.exe' OR Name = 'xray.exe' OR Name = 'sing-box.exe' OR Name = 'sing-box-client.exe'");
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
            if (count is not (4 or 5)) return [];
            string Arg(int index) => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * IntPtr.Size)) ?? "";
            return Enumerable.Range(0, count).Select(Arg).ToArray();
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
            if (matches != 1) throw Fail("listener_unverified", "未找到唯一的内核回环统计监听；当前流量未知。");
            return $"{address}:{endpoint.Port}:{expectedPid}";
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    private static bool SamePath(string a, string b) => ProcessBinding.SamePath(a, b);
    private static DiscoveryException ProcessFailure(string state) => Fail(state, state switch
    {
        "process_commandline_unverified" => "普通权限无法读取内核启动命令行，无法核实有效配置归属；本次采样保留为缺口。Monitor 不会自动提权。",
        "process_path_unverified" => "普通权限无法核实 v2rayN 或内核程序路径；本次采样保留为缺口。Monitor 不会自动提权。",
        "process_identity_changed" => "进程身份或启动信息在读取时不一致；本次采样保留为缺口。",
        "process_binding_mismatch" => "内核父子进程、程序路径或标准启动命令不匹配；不猜测有效配置归属。",
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
