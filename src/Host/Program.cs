using CounterSample = V2rayNMonitor.Core.CounterSample;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using V2rayNMonitor.Core;
using V2rayNMonitor.Server;

namespace V2rayNMonitor.Host;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var demo = args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "V2rayN-Monitor", demo ? "demo" : "data");
        var index = Array.IndexOf(args, "--data-dir");
        if (index >= 0 && index + 1 < args.Length) root = Path.GetFullPath(args[index + 1]);
        // One writer per data directory, including launches with alternate command-line spellings.
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())))[..24];
        using var mutex = new Mutex(true, @"Local\V2rayNMonitor-" + identity, out var created);
        if (!created) { MessageBox.Show("v2rayN Monitor 已在此数据目录运行，请从系统托盘打开。", "v2rayN Monitor"); return; }
        var probeMode = args.Contains("--probe-ledger", StringComparer.OrdinalIgnoreCase);
        var scopeMode = probeMode || args.Contains("--scope-ledger", StringComparer.OrdinalIgnoreCase);
        try { Application.Run(new TrayContext(root, demo, scopeMode, probeMode)); }
        catch (Exception) { MessageBox.Show(scopeMode
            ? "多范围启动失败。--scope-ledger 使用 v2，--probe-ledger 使用 v3；请选择全新目录或已受控迁移的对应账本，不会自动升级。请检查目录权限、磁盘空间及应用文件。原有历史保留。"
            : "启动失败。请确认数据目录可写、磁盘空间足够且应用文件完整。历史数据库不会被自动删除。", "v2rayN Monitor", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { mutex.ReleaseMutex(); }
    }
}

internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon tray;
    private readonly Icon trayIcon;
    private readonly MonitorServer server;
    private readonly CancellationTokenSource stop = new();
    private readonly Task collectTask;
    private readonly string directory;
    public TrayContext(string dataDir, bool demo, bool scopeMode = false, bool probeMode = false)
    {
        directory = dataDir;
        server = MonitorServer.StartAsync(dataDir, Path.Combine(AppContext.BaseDirectory, "wwwroot"), demo,
            useScopeLedger: scopeMode, useProbeLedger: probeMode).GetAwaiter().GetResult();
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开统计页面", null, (_, _) => Open());
        menu.Items.Add("选择 v2rayN 数据目录…", null, (_, _) => ChooseDirectory());
        menu.Items.Add("打开 Monitor 数据目录", null, (_, _) => Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true }));
        if (!demo)
        {
            var startup = new ToolStripMenuItem("登录 Windows 时启动") { Checked = IsStartupEnabled(scopeMode, probeMode) };
            startup.Click += (_, _) =>
            {
                try
                {
                    using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                    if (startup.Checked) key.DeleteValue("V2rayNMonitor", false);
                    else key.SetValue("V2rayNMonitor", MonitorStartup.Command(Environment.ProcessPath!, directory, scopeMode, probeMode));
                    startup.Checked = !startup.Checked;
                }
                catch (Exception) { MessageBox.Show("无法更新开机启动设置，请检查当前用户权限。", "v2rayN Monitor"); }
            };
            menu.Items.Add(startup);
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出并停止采集", null, (_, _) => ExitThread());
        trayIcon = LoadTrayIcon();
        tray = new NotifyIcon { Icon = trayIcon, Text = demo ? "v2rayN Monitor · 演示" : "v2rayN Monitor", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => Open();
        collectTask = demo ? Task.CompletedTask : Task.Run(CollectAsync);
        if (!Environment.GetCommandLineArgs().Contains("--background")) Open();
    }
    private static Icon LoadTrayIcon()
    {
        // Embedded for portable/self-contained publishing; no working-directory dependency.
        using var stream = typeof(TrayContext).Assembly.GetManifestResourceStream("V2rayNMonitor.Host.AppIcon.ico")
            ?? throw new InvalidOperationException("Application icon resource is missing.");
        using var icon = new Icon(stream, SystemInformation.SmallIconSize);
        return (Icon)icon.Clone();
    }
    private bool IsStartupEnabled(bool scopeMode, bool probeMode)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue("V2rayNMonitor") is string value &&
            string.Equals(value, MonitorStartup.Command(Environment.ProcessPath ?? "__invalid__", directory, scopeMode, probeMode), StringComparison.OrdinalIgnoreCase);
    }
    private void Open()
    {
        try { Process.Start(new ProcessStartInfo(server.IssueLaunchUrl()) { UseShellExecute = true }); }
        catch (Exception) { MessageBox.Show("无法打开系统浏览器，请检查默认浏览器设置。采集仍会继续。", "v2rayN Monitor"); }
    }
    private void ChooseDirectory()
    {
        using var dialog = new FolderBrowserDialog { Description = "选择包含 guiConfigs 和 binConfigs 的 v2rayN 数据根目录", UseDescriptionForTitle = true };
        if (dialog.ShowDialog() == DialogResult.OK)
        {
            server.UpdateSettings(server.Settings with { V2rayNPath = dialog.SelectedPath }); Open();
        }
    }
    private async Task CollectAsync()
    {
        using var discovery = new WindowsDiscovery();
        if (server.Scopes is { } scopes)
        {
            await CollectScopesAsync(discovery, scopes);
            return;
        }
        CounterSample? previous = null;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var reading = await discovery.ReadAsync(server.Settings.V2rayNPath, stop.Token);
                if (reading.Sample is { } sample)
                {
                    var outcome = server.Ledger.Accept(sample, reading.InvalidateContinuity);
                    if (outcome != "stale") discovery.AcknowledgePersistence();
                    string? up = null, down = null;
                    if (outcome == "recorded" && previous is not null && previous.SessionId == sample.SessionId && previous.Generation == sample.Generation && previous.NodeId == sample.NodeId && sample.At > previous.At && sample.Upload >= previous.Upload && sample.Download >= previous.Download)
                    {
                        var seconds = (decimal)(sample.At - previous.At).TotalSeconds;
                        up = decimal.Floor((sample.Upload - previous.Upload) / seconds).ToString(CultureInfo.InvariantCulture);
                        down = decimal.Floor((sample.Download - previous.Download) / seconds).ToString(CultureInfo.InvariantCulture);
                    }
                    previous = sample;
                    var state = outcome is "recorded" or "unallocated" ? "collecting" : outcome;
                    var message = outcome switch
                    {
                        "recorded" => $"正在采集 {reading.CoreName} proxy 原始字节。",
                        "unallocated" => "采集已恢复；已找回连续会话的区间流量，细分时间未知，详见统计说明。",
                        _ => "已建立采样基线；节点切换或核心重启后从新基线开始记录。"
                    };
                    server.SetStatus(new(state, message, reading.CurrentNode, sample.At, up, down, Scope: reading.Scope));
                }
                else
                {
                    server.Ledger.RecordGap(DateTimeOffset.UtcNow, reading.State, reading.InvalidateContinuity);
                    discovery.AcknowledgePersistence(); previous = null;
                    server.SetStatus(new(reading.State, reading.Message, reading.CurrentNode));
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception)
            {
                previous = null;
                server.SetStatus(new("error", "采集或写入失败，已有历史保留。请检查统计设置、目录权限和磁盘空间。"));
                try
                {
                    // If committing a discovered boundary failed, do not turn its next retry
                    // into recoverable outage bytes from an earlier node.
                    server.Ledger.RecordGap(DateTimeOffset.UtcNow, "collector_error", discovery.RequiresBaseline);
                    discovery.AcknowledgePersistence();
                }
                catch (Exception) { /* Failed writes do not advance the durable cursor or acknowledge the guard. */ }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(1), stop.Token); }
            catch (OperationCanceledException) { break; }
        }
    }
    private async Task CollectScopesAsync(WindowsDiscovery discovery, ScopeLedger ledger)
    {
        var basis = "actual_member";
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var reading = await discovery.ReadScopesAsync(server.Settings.V2rayNPath, stop.Token, ledger.SupportsProbes);
                if (reading.Batch is { } batch)
                {
                    var result = ScopeCollector.Commit(ledger, batch);
                    discovery.AcknowledgePersistence();
                    var unavailable = result.Scopes.Values.Count(x => x == "unavailable");
                    basis = batch.Readings.Any(x => x.Scope.Basis == "actual_member") ? "actual_member" : "legacy_proxy";
                    var baseline = result.Scopes.Values.All(x => x is "baseline" or "reset");
                    server.SetStatus(new(unavailable != 0 ? "partial" : baseline ? "baseline" : "collecting",
                        reading.Message + (unavailable == 0 ? "" : $" {unavailable} 个范围计数未知，未补零。"),
                         reading.CurrentNode, batch.At, Scope: reading.Scope, CollectionBasis: basis, Probes: reading.Probes));
                }
                else
                {
                    ledger.RecordGap(DateTimeOffset.UtcNow, reading.State, reading.InvalidateContinuity);
                    discovery.AcknowledgePersistence();
                    server.SetStatus(new(reading.State, reading.Message, reading.CurrentNode,
                        CollectionBasis: basis, Probes: ledger.SupportsProbes
                            ? new(true, "unavailable", reading.Message) : null));
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception)
            {
                try
                {
                    ledger.RecordGap(DateTimeOffset.UtcNow, "collector_error", discovery.RequiresBaseline);
                    discovery.AcknowledgePersistence();
                }
                catch (Exception) { /* Failed persistence must not acknowledge the generation guard. */ }
                server.SetStatus(new("error", "多范围采集或原子写入失败，已有历史保留。", CollectionBasis: basis,
                    Probes: ledger.SupportsProbes ? new(true, "unavailable", "探测采集或写入失败，已有历史保留。") : null));
            }
            try { await Task.Delay(TimeSpan.FromSeconds(1), stop.Token); }
            catch (OperationCanceledException) { break; }
        }
    }
    protected override void ExitThreadCore()
    {
        stop.Cancel(); collectTask.GetAwaiter().GetResult();
        server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        tray.Visible = false; tray.Dispose(); trayIcon.Dispose(); stop.Dispose(); base.ExitThreadCore();
    }
}
