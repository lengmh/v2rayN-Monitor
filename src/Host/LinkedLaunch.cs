using System.Diagnostics;
using System.Runtime.InteropServices;

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

    internal static void StartV2rayNIfNeeded(string executable)
    {
        if (!IsV2rayNExecutable(executable)) throw new ArgumentException("目标必须是 v2rayN.exe。");
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("v2rayN.exe 不存在。");

        // v2rayN instances are already outside the supported multi-instance collector scope.
        // A second start here could disturb the selected running instance.
        var running = Process.GetProcessesByName("v2rayN");
        try { if (running.Length != 0) return; }
        finally { foreach (var process in running) process.Dispose(); }

        using var started = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        });
        if (started is null) throw new InvalidOperationException("Windows 未能启动 v2rayN。");
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
            CreateShortcut(shortcut, monitorExecutable, v2rayNExecutable, monitorDataDirectory);
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
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows 快捷方式组件不可用。");
        var shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("无法创建 Windows 快捷方式组件。");
        try
        {
            dynamic shellObject = shell;
            object shortcut = shellObject.CreateShortcut(shortcutPath);
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
    }
}
