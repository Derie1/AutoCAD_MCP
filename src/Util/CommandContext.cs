using System.Diagnostics;
using Autodesk.AutoCAD.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadMcpBridge;

/// <summary>
/// ExecuteInCommandContextAsync with a guard for a measured AutoCAD behaviour (PLAN section 10, 2026/2027):
/// after a command sent to a drawing through COM SendCommand (another program, or vla-sendcommand), requests
/// for the command context of THAT drawing stay queued, although AutoCAD is idle — no lock, regen, focus,
/// key press or SendStringToExecute releases them; activating another drawing and then this one again does.
/// So: a request that has not started after a moment is cancelled (it will never run late), the drawing is
/// re-activated through another open drawing, and the request is queued once more. Without another open
/// drawing, or when that does not help, the call fails with "busy" and nothing is done.
/// Call it on the main thread, with the drawing active.
/// </summary>
internal static class CommandContext
{
    private const int StartMs = 1500;

    /// <summary>Runs <paramref name="body"/> in the command context of the active drawing <paramref name="doc"/>.</summary>
    public static async Task Run(Document doc, Func<Task> body)
    {
        for (int attempt = 0; ; attempt++)
        {
            int state = 0; // 0 = queued, 1 = running, 2 = abandoned
            var run = AcApp.DocumentManager.ExecuteInCommandContextAsync(async _ =>
            {
                if (Interlocked.CompareExchange(ref state, 1, 0) != 0) return; // abandoned: never run late
                await body();
            }, null);

            var sw = Stopwatch.StartNew();
            while (Volatile.Read(ref state) == 0 && !run.IsCompleted && sw.ElapsedMilliseconds < StartMs)
                await Task.Delay(50);
            if (Interlocked.CompareExchange(ref state, 2, 0) != 0)
            {
                if (attempt > 0) Log.Info($"command context started after re-activating {FileName(doc)}");
                await run;
                return;
            }

            Log.Info($"command context of {FileName(doc)} did not start within {StartMs} ms (attempt {attempt + 1})");
            if (attempt > 0 || !await Reactivate(doc))
                throw new BridgeException("busy",
                    "AutoCAD does not start commands in this drawing (this happens after another program sent it a " +
                    "command through COM SendCommand); switch to another drawing and back in AutoCAD, then try again. " +
                    "Nothing was done", 409);
        }
    }

    /// <summary>Activates another open drawing, then <paramref name="doc"/> again. False when impossible.</summary>
    private static async Task<bool> Reactivate(Document doc)
    {
        var dm = AcApp.DocumentManager;
        var other = dm.Cast<Document>().FirstOrDefault(d => d != doc);
        if (other == null || dm.MdiActiveDocument != doc) return false;
        var wait = TimeSpan.FromSeconds(10);
        // Each switch as its own main-thread message: two switches in a row do not release the drawing.
        await MainThread.InvokeAsync(() => dm.MdiActiveDocument = other, wait);
        await Task.Delay(200);
        await MainThread.InvokeAsync(() => dm.MdiActiveDocument = doc, wait);
        await Task.Delay(200);
        return await MainThread.InvokeAsync(() => dm.MdiActiveDocument == doc, wait);
    }

    private static string FileName(Document d) => Path.GetFileName(d.Name);
}
