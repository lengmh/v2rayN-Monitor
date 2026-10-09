using V2rayNMonitor.Server;
using Xunit;
namespace V2rayNMonitor.Tests;
public sealed class LifecycleTests
{
    [Fact]
    public async Task SynchronousTrayLifecycleDoesNotRequirePumpingUiContext()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var root = Path.Combine(Path.GetTempPath(), "monitor-lifecycle-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var thread = new Thread(() =>
        {
            // WinForms exits synchronously. A lifecycle await must not depend on this UI queue.
            SynchronizationContext.SetSynchronizationContext(new NonPumpingContext());
            try
            {
                var server = MonitorServer.StartAsync(root, root).GetAwaiter().GetResult();
                server.DisposeAsync().AsTask().GetAwaiter().GetResult();
                completion.TrySetResult();
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true };
        thread.Start();
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException) { throw new TimeoutException(NonPumpingContext.Posted); }
        Assert.Null(NonPumpingContext.Posted);
        Directory.Delete(root, true);
    }
    private sealed class NonPumpingContext : SynchronizationContext
    {
        public static string? Posted;
        public override void Post(SendOrPostCallback callback, object? state) { Posted = Environment.StackTrace; }
    }
}
