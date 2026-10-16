using V2rayNMonitor.Host;
using Xunit;

namespace V2rayNMonitor.Core.Tests;

public class LinkedLaunchTests
{
    [Fact]
    public void ReadsOptionalLaunchTargetAndRejectsMissingArgument()
    {
        Assert.Null(LinkedLaunch.RequestedExecutable(["--background"]));
        Assert.Equal(@"C:\Proxy\v2rayN.exe",
            LinkedLaunch.RequestedExecutable(["--background", "--LAUNCH-V2RAYN", @"C:\Proxy\v2rayN.exe"]));
        Assert.Throws<ArgumentException>(() => LinkedLaunch.RequestedExecutable(["--launch-v2rayn"]));
        Assert.Throws<ArgumentException>(() =>
            LinkedLaunch.RequestedExecutable(["--launch-v2rayn", "--data-dir", "unused"]));
    }

    [Theory]
    [InlineData("v2rayN.exe", true)]
    [InlineData("V2RAYN.EXE", true)]
    [InlineData("V2RAYN.exe.bak", false)]
    [InlineData("not-v2rayN.exe", false)]
    public void OnlyAcceptsExpectedExecutableName(string candidate, bool expected)
    {
        Assert.Equal(expected, LinkedLaunch.IsV2rayNExecutable(candidate));
    }

    [Fact]
    public void EscapesTrailingBackslashesInShortcutArguments()
    {
        Assert.Equal("\"C:" + new string('\\', 2) + "\"",
            LinkedLaunch.QuoteWindowsArgument(@"C:\"));
        Assert.Throws<ArgumentException>(() => LinkedLaunch.QuoteWindowsArgument("invalid\"path"));
        Assert.Throws<ArgumentException>(() => LinkedLaunch.QuoteWindowsArgument("bad\0path"));

        var executable = Path.Combine(Path.GetTempPath(), "合成 路径", "v2rayN.exe");
        var dataDirectory = Path.Combine(Path.GetTempPath(), "Monitor Data");
        var arguments = LinkedLaunch.BuildArguments(executable, dataDirectory);
        Assert.Contains("--background --data-dir ", arguments);
        Assert.Contains(LinkedLaunch.QuoteWindowsArgument(dataDirectory), arguments);
        Assert.Contains("--launch-v2rayn " + LinkedLaunch.QuoteWindowsArgument(executable), arguments);
    }

    [Fact]
    public void ConfiguredRootMatchesOnlyPortableDirectoryOrDedicatedAppDataRoot()
    {
        var portable = Path.Combine(Path.GetTempPath(), "portable instance");
        var executable = Path.Combine(portable, "v2rayN.exe");
        var other = Path.Combine(Path.GetTempPath(), "other install");
        var localAppData = Path.Combine(Path.GetTempPath(), "synthetic user", "AppData", "Local");
        Assert.True(LinkedLaunch.MatchesConfiguredDataRoot(executable, portable, localAppData));
        Assert.True(LinkedLaunch.MatchesConfiguredDataRoot(executable,
            Path.Combine(localAppData, "v2rayN"), localAppData));
        Assert.False(LinkedLaunch.MatchesConfiguredDataRoot(executable, other, localAppData));
        Assert.False(LinkedLaunch.MatchesConfiguredDataRoot(executable, "", localAppData));
        Assert.False(LinkedLaunch.MatchesConfiguredDataRoot("v2rayN.exe", portable, localAppData));
        Assert.False(LinkedLaunch.MatchesConfiguredDataRoot(Path.Combine(portable, "not-v2rayN.exe"),
            portable, localAppData));
    }

    [Fact]
    public void ReadsPersistedConfiguredRootWithoutEditingIt()
    {
        var directory = Path.Combine(Path.GetTempPath(), "v2rayn-monitor-synthetic-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.Equal("", LinkedLaunch.ReadConfiguredDataRoot(directory));
            var settings = Path.Combine(directory, "settings.json");
            var root = Path.Combine(directory, "合成数据目录");
            File.WriteAllText(settings, System.Text.Json.JsonSerializer.Serialize(
                new V2rayNMonitor.Core.AppSettings(root, "UTC"),
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
            Assert.Equal(root, LinkedLaunch.ReadConfiguredDataRoot(directory));
            Assert.True(File.Exists(settings));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void RejectsInvalidOrMissingExecutableWithoutStartingAnything()
    {
        Assert.Throws<ArgumentException>(() => LinkedLaunch.StartV2rayNIfNeeded("not-v2rayN.exe"));
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "v2rayN.exe");
        Assert.Throws<FileNotFoundException>(() => LinkedLaunch.StartV2rayNIfNeeded(missing));
    }

    [Fact]
    public void WritesActualWindowsShortcutWithoutReplacingExistingFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "v2rayn-monitor-synthetic-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var v2rayN = Path.Combine(directory, "v2rayN.exe");
            var monitor = Path.Combine(directory, "V2rayN-Monitor.exe");
            var shortcut = Path.Combine(directory, "Joint Launch.lnk");
            File.WriteAllText(v2rayN, "synthetic placeholder, never executed");
            File.WriteAllText(monitor, "synthetic placeholder, never executed");

            Exception? failure = null;
            var sta = new Thread(() =>
            {
                try
                {
                    LinkedLaunch.CreateShortcut(shortcut, monitor, v2rayN, directory);
                    var bytes = File.ReadAllBytes(shortcut);
                    Assert.Throws<IOException>(() =>
                        LinkedLaunch.CreateShortcut(shortcut, monitor, v2rayN, directory));
                    Assert.Equal(bytes, File.ReadAllBytes(shortcut));
                    Assert.Empty(Directory.GetFiles(directory, ".v2rayn-monitor-*.lnk"));
                }
                catch (Exception ex) { failure = ex; }
            });
            sta.SetApartmentState(ApartmentState.STA);
            sta.Start();
            Assert.True(sta.Join(TimeSpan.FromSeconds(15)), "Windows shortcut test did not complete.");
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            Assert.True(File.Exists(shortcut));
            Assert.True(new FileInfo(shortcut).Length > 0);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
