using V2rayNMonitor.Server;
var directory = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "v2rayn-monitor-demo");
await using var server = await MonitorServer.StartAsync(directory, Path.Combine(AppContext.BaseDirectory, "wwwroot"), demo: true);
Console.WriteLine("Synthetic demo only. Open this one-use link within two minutes (do not share it):");
Console.WriteLine(server.IssueLaunchUrl());
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
try { await Task.Delay(Timeout.Infinite, stop.Token); } catch (OperationCanceledException) { }
