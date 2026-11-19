using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadMcpBridge;

/// <summary>
/// Frame for every tool that changes the drawing:
///   - refuses while a command runs (busy) or on a read-only drawing;
///   - locks the document under the command name "MCP_&lt;TOOL&gt;", so the whole call is one UNDO step;
///   - one transaction, committed only if the body succeeds (an error leaves the drawing untouched);
///   - counts the change for the undo tool and refreshes the screen.
/// </summary>
internal static class Write
{
    internal sealed class Ctx(Document doc, Transaction tr)
    {
        public Document Doc { get; } = doc;
        public Database Db { get; } = doc.Database;
        public Transaction Tr { get; } = tr;

        /// <summary>Current space (model space or the active layout), opened for write.</summary>
        public BlockTableRecord CurrentSpace() => (BlockTableRecord)Tr.GetObject(Db.CurrentSpaceId, OpenMode.ForWrite);

        public T Add<T>(BlockTableRecord space, T entity) where T : Entity
        {
            space.AppendEntity(entity);
            Tr.AddNewlyCreatedDBObject(entity, true);
            return entity;
        }

        /// <summary>Checks that a layer exists, returns its exact name.</summary>
        public string Layer(string name)
        {
            var lt = (LayerTable)Tr.GetObject(Db.LayerTableId, OpenMode.ForRead);
            if (!lt.Has(name)) throw new BridgeException("not_found", $"layer '{name}' does not exist; create it first", 404);
            return ((LayerTableRecord)Tr.GetObject(lt[name], OpenMode.ForRead)).Name;
        }
    }

    public static JsonObject Run(string tool, Func<Ctx, JsonObject> body)
    {
        var doc = JsonUtil.ActiveDocument();
        if (!string.IsNullOrEmpty(doc.CommandInProgress))
            throw new BridgeException("busy", $"AutoCAD is running the command {doc.CommandInProgress}; finish or cancel it first", 409);
        if (doc.IsReadOnly)
            throw new BridgeException("read_only", "the drawing is open read-only", 409);

        string cmd = "MCP_" + tool.ToUpperInvariant();
        JsonObject result;
        bool committed = false;
        try
        {
            using (doc.LockDocument(DocumentLockMode.Write, cmd, cmd, false))
            using (var tr = doc.TransactionManager.StartTransaction())
            {
                try
                {
                    result = body(new Ctx(doc, tr));
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.OnLockedLayer)
                {
                    throw new BridgeException("locked_layer", "the object is on a locked layer; unlock the layer first", 409);
                }
                tr.Commit();
                committed = true;
            }
        }
        finally
        {
            // The named document lock opens an UNDO group even when the call fails and the
            // transaction is aborted; the tracker must know about that empty group too.
            UndoTracker.Recorded(doc, committed);
        }
        try { doc.Editor.UpdateScreen(); } catch { /* cosmetic */ }
        return result;
    }

    /// <summary>
    /// Like Run, but without an outer transaction: for database operations that must not run inside one
    /// (binding, detaching, reloading external references). The body opens its own transactions.
    /// </summary>
    public static JsonObject RunLocked(string tool, Func<Document, JsonObject> body)
    {
        var doc = JsonUtil.ActiveDocument();
        if (!string.IsNullOrEmpty(doc.CommandInProgress))
            throw new BridgeException("busy", $"AutoCAD is running the command {doc.CommandInProgress}; finish or cancel it first", 409);
        if (doc.IsReadOnly)
            throw new BridgeException("read_only", "the drawing is open read-only", 409);

        string cmd = "MCP_" + tool.ToUpperInvariant();
        JsonObject result;
        bool done = false;
        try
        {
            using (doc.LockDocument(DocumentLockMode.Write, cmd, cmd, false))
            {
                result = body(doc);
                done = true;
            }
        }
        finally { UndoTracker.Recorded(doc, done); }
        try { doc.Editor.Regen(); } catch { /* cosmetic */ }
        return result;
    }
}

/// <summary>
/// The UNDO groups MCP has added to each drawing, newest last: true = a real change,
/// false = an empty group left by a failed call. Any user command clears the list, so the
/// undo tool never reverts the user's own work.
/// </summary>
internal static class UndoTracker
{
    private static readonly Dictionary<Document, List<bool>> Groups = [];
    private static bool _ownUndoRunning;

    public static void Init()
    {
        var dm = AcApp.DocumentManager;
        foreach (Document d in dm) Watch(d);
        dm.DocumentCreated += (_, e) => Watch(e.Document);
        dm.DocumentToBeDestroyed += (_, e) => Groups.Remove(e.Document);
    }

    private static void Watch(Document d)
    {
        d.CommandEnded += (_, e) => UserCommand(d, e.GlobalCommandName);
        d.CommandCancelled += (_, e) => UserCommand(d, e.GlobalCommandName);
    }

    private static void UserCommand(Document d, string name)
    {
        if (_ownUndoRunning || name.StartsWith("MCP_", StringComparison.OrdinalIgnoreCase)) return;
        Groups.Remove(d);
    }

    private static List<bool> List(Document d) =>
        Groups.TryGetValue(d, out var l) ? l : Groups[d] = [];

    public static void Recorded(Document d, bool realChange) => List(d).Add(realChange);

    /// <summary>Real MCP changes that can be undone.</summary>
    public static int Available(Document d) => Groups.TryGetValue(d, out var l) ? l.Count(x => x) : 0;

    /// <summary>UNDO steps needed to revert the last <paramref name="changes"/> real changes (empty groups included).</summary>
    public static int StepsFor(Document d, int changes)
    {
        var l = List(d);
        int steps = 0, real = 0;
        for (int i = l.Count - 1; i >= 0 && real < changes; i--)
        {
            steps++;
            if (l[i]) real++;
        }
        return steps;
    }

    public static void BeginOwnUndo() => _ownUndoRunning = true;

    public static void EndOwnUndo(Document d, int steps)
    {
        _ownUndoRunning = false;
        var l = List(d);
        l.RemoveRange(Math.Max(0, l.Count - steps), Math.Min(steps, l.Count));
    }
}
