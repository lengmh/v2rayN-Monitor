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
        // Parse only here; when this instance owns the data directory, open the ledger and
        // start the collector *before* launching v2rayN. Do not lose early startup bytes.
        string? linkedExecutable;
        try { linkedExecutable = demo ? null : LinkedLaunch.RequestedExecutable(args); }
        catch (ArgumentException)
        {
            MessageBox.Show("联动快捷方式的启动参数不完整，请从 Monitor 托盘重新创建。",
                "v2rayN Monitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        // One writer per data directory, including launches with alternate command-line spellings.
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())))[..24];
        using var mutex = new Mutex(true, @"Local\V2rayNMonitor-" + identity, out var created);
        if (!created)
        {
            if (linkedExecutable is null)
                MessageBox.Show("v2rayN Monitor 已在此数据目录运行，请从系统托盘打开。", "v2rayN Monitor");
            else LaunchLinked(linkedExecutable, root);
            return;
        }
        try { Application.Run(new TrayContext(root, demo, linkedExecutable)); }
        catch (Exception) { MessageBox.Show("启动失败。请确认数据目录可写、磁盘空间足够且应用文件完整。历史数据库不会被自动删除。", "v2rayN Monitor", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { mutex.ReleaseMutex(); }
    }

    internal static void LaunchLinked(string executable, string monitorDirectory)
    {
        try
        {
            // Recheck at use-time. A shortcut can survive moves and settings changes.
            LinkedLaunch.EnsureConfiguredDataRoot(executable, LinkedLaunch.ReadConfiguredDataRoot(monitorDirectory));
            LinkedLaunch.StartV2rayNIfNeeded(executable);
        }
        catch (InvalidOperationException ex)
        {
            // All direct LinkedLaunch InvalidOperationException messages are fixed safe text.
            MessageBox.Show(ex.Message, "v2rayN Monitor · 联动启动未完成",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception)
        {
            MessageBox.Show("联动启动未完成：请检查 v2rayN.exe 路径、管理员确认或另一启动中的实例。" +
                " Monitor 不会修改配置；可从托盘查看当前采集状态。",
                "v2rayN Monitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
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
    public TrayContext(string dataDir, bool demo, string? linkedExecutable = null)
    {
        directory = dataDir;
        server = MonitorServer.StartAsync(dataDir, Path.Combine(AppContext.BaseDirectory, "wwwroot"), demo).GetAwaiter().GetResult();
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开统计页面", null, (_, _) => Open());
        menu.Items.Add("选择 v2rayN 数据目录…", null, (_, _) => ChooseDirectory());
        menu.Items.Add("打开 Monitor 数据目录", null, (_, _) => Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true }));
        if (!demo)
        {
            menu.Items.Add("创建 v2rayN + Monitor 联动启动快捷方式…", null, (_, _) => CreateLinkedShortcut());
            var startup = new ToolStripMenuItem("登录 Windows 时启动") { Checked = IsStartupEnabled() };
            startup.Click += (_, _) =>
            {
                try
                {
                    using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                    if (startup.Checked) key.DeleteValue("V2rayNMonitor", false);
                    else key.SetValue("V2rayNMonitor", $"\"{Environment.ProcessPath}\" --background --data-dir \"{directory}\"");
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
        if (!demo && linkedExecutable is not null) Program.LaunchLinked(linkedExecutable, directory);
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
    private bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue("V2rayNMonitor") is string value && value.Contains(Environment.ProcessPath ?? "__invalid__", StringComparison.OrdinalIgnoreCase);
    }
    private void CreateLinkedShortcut()
    {
        using var picker = new OpenFileDialog
        {
            Title = "选择要与 Monitor 联动启动的 v2rayN.exe",
            Filter = "v2rayN 程序 (v2rayN.exe)|v2rayN.exe",
            CheckFileExists = true,
            Multiselect = false
        };
        if (picker.ShowDialog() != DialogResult.OK) return;
        try
        {
            LinkedLaunch.EnsureConfiguredDataRoot(picker.FileName, server.Settings.V2rayNPath);
            var shortcut = LinkedLaunch.CreateDesktopShortcut(picker.FileName, directory);
            MessageBox.Show($"已创建桌面快捷方式：{Path.GetFileName(shortcut)}。\n请从新快捷方式启动 v2rayN；原启动入口不会自动带起 Monitor。",
                "v2rayN Monitor", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "v2rayN Monitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception)
        {
            MessageBox.Show("无法创建联动快捷方式，请确认 v2rayN.exe 的位置、桌面目录及当前用户权限。",
                "v2rayN Monitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
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
                        "recorded" => "正在采集 Xray 普通单节点原始字节。",
                        "unallocated" => "采集已恢复；已找回连续会话的区间流量，细分时间未知，详见统计说明。",
                        _ => "已建立采样基线；节点切换或核心重启后从新基线开始记录。"
                    };
                    server.SetStatus(new(state, message, reading.CurrentNode, sample.At, up, down));
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
    protected override void ExitThreadCore()
    {
        stop.Cancel(); collectTask.GetAwaiter().GetResult();
        server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        tray.Visible = false; tray.Dispose(); trayIcon.Dispose(); stop.Dispose(); base.ExitThreadCore();
    }
}
