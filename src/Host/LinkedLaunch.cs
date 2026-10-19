using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using V2rayNMonitor.Core;

namespace V2rayNMonitor.Host;

/// <summary>
/// Optional shortcut-based joint launch. Does not hook v2rayN, modify its configuration,
/// or create a second long-running collector.
/// </summary>
internal static class LinkedLaunch
{
    internal const string Option = "--launch-v2rayn";

    internal static string? RequestedExecutable(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (!string.Equals(args[i], Option, StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("联动启动缺少 v2rayN.exe 路径。");
            return args[i + 1];
        }
        return null;
    }

    internal static bool IsV2rayNExecutable(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        string.Equals(Path.GetFileName(path), "v2rayN.exe", StringComparison.OrdinalIgnoreCase);

    // Shortcut arguments are a Windows command line, not a shell script.
    // Backslashes before the closing quote must be doubled (e.g. --data-dir "C:\").
    internal static string QuoteWindowsArgument(string path)
    {
        if (path.Contains('"') || path.Contains('\0'))
            throw new ArgumentException("路径包含不能用于 Windows 启动参数的字符。");
        var trailingSlashes = 0;
        for (var i = path.Length - 1; i >= 0 && path[i] == '\\'; i--) trailingSlashes++;
        return "\"" + path + new string('\\', trailingSlashes) + "\"";
    }

    internal static string BuildArguments(string v2rayNExecutable, string monitorDataDirectory)
    {
        if (!IsV2rayNExecutable(v2rayNExecutable) ||
            !Path.IsPathFullyQualified(v2rayNExecutable) ||
            !Path.IsPathFullyQualified(monitorDataDirectory))
            throw new ArgumentException("请选择有效的 v2rayN.exe 和 Monitor 数据目录。");
        return "--background --data-dir " + QuoteWindowsArgument(monitorDataDirectory) +
               " " + Option + " " + QuoteWindowsArgument(v2rayNExecutable);
    }

    // Match WindowsDiscovery's portable-root / local-AppData-root instance selection.
    // Refuse a stale shortcut instead of launching a different client than the collector reads.
    internal static bool MatchesConfiguredDataRoot(string executable, string configuredRoot, string localAppData)
    {
        if (!IsV2rayNExecutable(executable) || !Path.IsPathFullyQualified(executable) ||
            string.IsNullOrWhiteSpace(configuredRoot) || !Path.IsPathFullyQualified(configuredRoot))
            return false;
        var executableDirectory = Path.GetDirectoryName(Path.GetFullPath(executable));
        return executableDirectory is not null &&
            (ProcessBinding.SamePath(executableDirectory, configuredRoot)
                || ProcessBinding.SamePath(configuredRoot, Path.Combine(localAppData, "v2rayN")));
    }

    internal static string ReadConfiguredDataRoot(string monitorDirectory)
    {
        var settingsFile = Path.Combine(monitorDirectory, "settings.json");
        if (!File.Exists(settingsFile)) return "";
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsFile), options)?.V2rayNPath ?? "";
    }

    internal static void EnsureConfiguredDataRoot(string executable, string configuredRoot)
    {
        if (!MatchesConfiguredDataRoot(executable, configuredRoot,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)))
            throw new InvalidOperationException(
                "联动程序与 Monitor 配置的数据目录不对应或尚未设置。请核对统计设置，并重新创建联动快捷方式。");
    }

    // Prefer an available/abandoned owner mutex over a stale ready signal.
    // Waiting on it acquires it; always release that lease.
    internal static bool WaitForMonitorReady(Mutex ownerMutex, WaitHandle ready, TimeSpan timeout)
    {
        try
        {
            var signaled = WaitHandle.WaitAny([ownerMutex, ready], timeout);
            if (signaled == 1) return true;
            if (signaled == 0) ownerMutex.ReleaseMutex();
            return false;
        }
        catch (AbandonedMutexException)
        {
            ownerMutex.ReleaseMutex();
            return false;
        }
    }

    internal static void StartV2rayNIfNeeded(string executable)
    {
        if (!IsV2rayNExecutable(executable) || !Path.IsPathFullyQualified(executable))
            throw new ArgumentException("需要选择 v2rayN.exe 的绝对路径。");
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("v2rayN.exe 不存在。");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();

        // Serialize launches from rapidly repeated shortcut clicks, including a delayed UAC prompt.
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(executable.ToUpperInvariant())))[..24];
        using var launchMutex = new Mutex(false, @"Local\V2rayNMonitor-Launch-" + identity);
        var owned = false;
        try
        {
            try { owned = launchMutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned) throw new IOException("另一次联动启动尚未完成，已取消本次重复请求。");

            var running = Process.GetProcessesByName("v2rayN");
            try
            {
                if (running.Length > 1)
                    throw new InvalidOperationException("存在多个 v2rayN 进程，暂不尝试启动另一实例。");
                if (running.Length == 1)
                {
                    // A same-path client in another Windows login session is not our active instance.
                    using var current = Process.GetCurrentProcess();
                    if (running[0].SessionId != current.SessionId)
                        throw new InvalidOperationException("v2rayN 在另一 Windows 登录会话运行，暂不复用该进程。");
                    // The limited-access path query also works for many elevated processes;
                    // unreadable stays unknown instead of being treated as a confirmed match.
                    var existing = WindowsProcessReader.Read(running[0].Id);
                    if (existing is null || !existing.IsAlive || string.IsNullOrWhiteSpace(existing.ExecutablePath))
                        throw new InvalidOperationException("无法核实已运行的 v2rayN 程序路径，暂不重复启动。");
                    if (!ProcessBinding.SamePath(existing.ExecutablePath, executable))
                        throw new InvalidOperationException("另一目录下的 v2rayN 已运行；不会静默改用其他实例。");
                    return;
                }
            }
            finally { foreach (var process in running) process.Dispose(); }

            using var started = Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executable)!
            });
            if (started is null) throw new InvalidOperationException("Windows 未能启动 v2rayN。");
        }
        finally { if (owned) launchMutex.ReleaseMutex(); }
    }

    internal static string CreateDesktopShortcut(string v2rayNExecutable, string monitorDataDirectory)
    {
        var monitorExecutable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(monitorExecutable) ||
            !string.Equals(Path.GetFileName(monitorExecutable), "V2rayN-Monitor.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请从发布的 V2rayN-Monitor.exe 创建联动快捷方式。");
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktop) || !Directory.Exists(desktop))
            throw new DirectoryNotFoundException("无法找到当前用户的桌面目录。");

        for (var i = 1; i <= 999; i++)
        {
            var name = i == 1 ? "v2rayN + Monitor.lnk" : $"v2rayN + Monitor ({i}).lnk";
            var shortcut = Path.Combine(desktop, name);
            if (File.Exists(shortcut)) continue;
            try { CreateShortcut(shortcut, monitorExecutable, v2rayNExecutable, monitorDataDirectory); }
            catch (IOException) when (File.Exists(shortcut)) { continue; }
            return shortcut;
        }
        throw new IOException("桌面已有太多同名快捷方式，请先清理。");
    }

    internal static void CreateShortcut(string shortcutPath, string monitorExecutable,
        string v2rayNExecutable, string monitorDataDirectory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!File.Exists(v2rayNExecutable) || !File.Exists(monitorExecutable))
            throw new FileNotFoundException("启动程序已移动或不存在。");
        if (File.Exists(shortcutPath)) throw new IOException("快捷方式已存在，不会覆盖。");
        var arguments = BuildArguments(v2rayNExecutable, monitorDataDirectory);
        var shortcutDirectory = Path.GetDirectoryName(Path.GetFullPath(shortcutPath))
            ?? throw new ArgumentException("快捷方式目标目录无效。");
        var temporary = Path.Combine(shortcutDirectory, ".v2rayn-monitor-" + Guid.NewGuid().ToString("N") + ".lnk");
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new InvalidOperationException("Windows 快捷方式组件不可用。");
            var shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("无法创建 Windows 快捷方式组件。");
            try
            {
                dynamic shellObject = shell;
                object shortcut = shellObject.CreateShortcut(temporary);
                try
                {
                    dynamic link = shortcut;
                    link.TargetPath = monitorExecutable;
                    link.Arguments = arguments;
                    link.WorkingDirectory = Path.GetDirectoryName(monitorExecutable)!;
                    link.IconLocation = v2rayNExecutable + ",0";
                    link.Description = "联动启动 v2rayN 和 Monitor；采集在后台持续运行";
                    link.Save();
                }
                finally
                {
                    if (Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                }
            }
            finally
            {
                if (Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            }

            // File.Move without overwrite is atomic on the same directory. An existing shortcut
            // is never clobbered, including if another creator wins between check and save.
            File.Move(temporary, shortcutPath);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
