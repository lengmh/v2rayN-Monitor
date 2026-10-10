using V2rayNMonitor.Server;
using Xunit;
using Xunit.Sdk;
namespace V2rayNMonitor.Tests;

[CollectionDefinition(nameof(LifecycleCollection), DisableParallelization = true)]
public sealed class LifecycleCollection { }

[Collection(nameof(LifecycleCollection))]
public sealed class LifecycleTests
{
    [Fact]
    public async Task SynchronousTrayLifecycleDoesNotRequirePumpingUiContext()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new NonPumpingContext();
        var phase = "waiting for the lifecycle worker";
        var root = Path.Combine(Path.GetTempPath(), "monitor-lifecycle-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var thread = new Thread(() =>
        {
            // WinForms exits synchronously. A lifecycle await must not depend on this UI queue.
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                Volatile.Write(ref phase, "starting the server");
                var server = MonitorServer.StartAsync(root, root).GetAwaiter().GetResult();
                Volatile.Write(ref phase, "stopping and disposing the server");
                server.DisposeAsync().AsTask().GetAwaiter().GetResult();
                completion.TrySetResult();
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true };
        thread.Start();
        await WaitForLifecycleAsync(completion.Task, context, () => Volatile.Read(ref phase));
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task GuardRejectsPostedContinuationWithoutPumpingIt()
    {
        var context = new NonPumpingContext();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackRan = false;
        var guarded = WaitForLifecycleAsync(pending.Task, context, () => "synthetic lifecycle");
        context.Post(_ => callbackRan = true, null);
        var error = await Assert.ThrowsAsync<XunitException>(() => guarded);
        Assert.Contains("UI synchronization context", error.Message);
        Assert.False(callbackRan);
    }

    private static async Task WaitForLifecycleAsync(Task completion, NonPumpingContext context, Func<string> phase)
    {
        // A posted continuation is the deadlock signal. The separate watchdog only bounds
        // infrastructure stalls; cold Kestrel/SQLite startup on Windows is not a 10-second SLA.
        try { await Task.WhenAny(completion, context.Posted).WaitAsync(TimeSpan.FromSeconds(60)); }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Lifecycle watchdog expired while {phase()}; no UI continuation was posted.");
        }
        if (context.Posted.IsCompleted)
            throw new XunitException($"Lifecycle posted to the non-pumping UI synchronization context while {phase()}:\n{await context.Posted}");
        await completion;
    }

    private sealed class NonPumpingContext : SynchronizationContext
    {
        private readonly TaskCompletionSource<string> posted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string> Posted => posted.Task;
        public override void Post(SendOrPostCallback callback, object? state) => posted.TrySetResult(Environment.StackTrace);
    }
}
