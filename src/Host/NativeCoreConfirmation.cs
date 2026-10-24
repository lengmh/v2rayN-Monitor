using System.Runtime.InteropServices;

namespace V2rayNMonitor.Host;

internal static class NativeCoreConfirmation
{
    [ThreadStatic] private static bool showing;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    internal static bool Show(CoreActionPrompt prompt, CancellationToken token)
    {
        if (token.IsCancellationRequested || showing) return false;
        showing = true;
        try { return ShowCore(prompt, token); }
        finally { showing = false; }
    }

    private static bool ShowCore(CoreActionPrompt prompt, CancellationToken token)
    {
        using var form = new Form
        {
            Text = prompt.Title, Width = 720, Height = 520, StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false, MaximizeBox = false, AutoScaleMode = AutoScaleMode.Dpi
        };
        var details = new TextBox
        {
            Text = prompt.Message.Replace("\n", Environment.NewLine), ReadOnly = true, Multiline = true,
            ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8)
        };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        var accept = new Button { Text = "确认本次操作", DialogResult = DialogResult.OK, AutoSize = true };
        buttons.Controls.Add(cancel); buttons.Controls.Add(accept);
        form.Controls.Add(details); form.Controls.Add(buttons);
        form.CancelButton = cancel;
        form.AcceptButton = cancel;
        // A hidden process startup can suppress the first native ShowWindow even though
        // WinForms reports Visible=true. Explicit consent must never wait in a hidden window.
        form.Shown += (_, _) => { ShowWindow(form.Handle, 5 /* SW_SHOW */); form.Activate(); };
        using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += (_, _) => { if (token.IsCancellationRequested) form.Close(); };
        timer.Start();
        return form.ShowDialog() == DialogResult.OK && !token.IsCancellationRequested;
    }

    internal static async Task<bool> ShowAsync(Control dispatcher, CoreActionPrompt prompt, CancellationToken token)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (token.IsCancellationRequested) return false;
        try
        {
            dispatcher.BeginInvoke(() =>
            {
                try { completion.TrySetResult(Show(prompt, token)); }
                catch (Exception error) { completion.TrySetException(error); }
            });
        }
        catch (InvalidOperationException) { completion.TrySetResult(false); }
        try { return await completion.Task.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
    }
}
