using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadMcpBridge.Handlers;

internal static class EditHandlers
{
    /// <summary>
    /// Reverts the last MCP changes with AutoCAD's own UNDO. Refuses when the user has run a
    /// command since (UndoTracker), so the user's work is never undone by the assistant.
    /// </summary>
    public static Task<JsonObject> Undo(JsonObject a)
    {
        var doc = JsonUtil.ActiveDocument();
        int steps = Args.Int(a, "steps", 1, 1, 100);
        if (!string.IsNullOrEmpty(doc.CommandInProgress))
            throw new BridgeException("busy", $"AutoCAD is running the command {doc.CommandInProgress}; finish or cancel it first", 409);

        int available = UndoTracker.Available(doc);
        if (available == 0)
            throw new BridgeException("nothing_to_undo",
                "there is no MCP change to undo in this drawing: nothing was changed through MCP, " +
                "or the user has run a command since (then use UNDO in AutoCAD)", 409);
        if (steps > available)
            throw new BridgeException("bad_request", $"only {available} MCP change(s) can be undone, not {steps}");

        return RunUndo(doc, steps, UndoTracker.StepsFor(doc, steps));
    }

    /// <param name="changes">real MCP changes to revert</param>
    /// <param name="steps">AutoCAD UNDO steps, including empty groups of failed calls</param>
    private static async Task<JsonObject> RunUndo(Document doc, int changes, int steps)
    {
        UndoTracker.BeginOwnUndo();
        try
        {
            await AcApp.DocumentManager.ExecuteInCommandContextAsync(
                async _ => await doc.Editor.CommandAsync("_.UNDO", steps.ToString()), null);
        }
        finally
        {
            UndoTracker.EndOwnUndo(doc, steps);
        }
        try { doc.Editor.UpdateScreen(); } catch { }
        return new JsonObject { ["undone"] = changes, ["remaining_mcp_changes"] = UndoTracker.Available(doc) };
    }
}
