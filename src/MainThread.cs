using System.Windows.Forms;

namespace AcadMcpBridge;

/// <summary>
/// Runs work on AutoCAD's main (UI) thread. The AutoCAD API must not be called
/// from the HTTP listener's thread pool, so each request is posted to a hidden
/// control created on the main thread in Initialize(), and the caller waits
/// with a timeout. If AutoCAD is stuck in a modal dialog the request times out
/// instead of hanging the MCP client.
/// </summary>
internal static class MainThread
{
    private static Control? _control;

    public static void Init()
    {
        var c = new Control();
        c.CreateControl();
        _ = c.Handle; // force the window handle on this (the main) thread
        _control = c;
    }

    public static async Task<T> InvokeAsync<T>(Func<T> work, TimeSpan timeout)
    {
        var control = _control ?? throw new BridgeException("not_ready", "dispatcher not initialized");
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        int state = 0; // 0 = queued, 1 = running, 2 = abandoned by timeout

        control.BeginInvoke(() =>
        {
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0) return; // caller gave up
            try { tcs.TrySetResult(work()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });

        var finished = await Task.WhenAny(tcs.Task, Task.Delay(timeout)).ConfigureAwait(false);
        if (finished != tcs.Task)
        {
            if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
                throw new BridgeException("busy",
                    $"AutoCAD did not pick up the request within {timeout.TotalSeconds:0} s " +
                    "(a modal dialog or a long operation is blocking it)");
            // Already running: wait for it to finish rather than leave it half done.
        }
        return await tcs.Task.ConfigureAwait(false);
    }
}
