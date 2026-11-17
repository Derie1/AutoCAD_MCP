using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadMcpBridge.Handlers;

internal static class QueryHandlers
{
    public const int DefaultLimit = 200;
    public const int MaxLimit = 2000;

    /// <summary>Entities matching EntityFilter, paged: {total, offset, count, items[]}.</summary>
    public static JsonObject FindEntities(JsonObject a)
    {
        var filter = new EntityFilter(a);
        int offset = Args.Int(a, "offset", 0, 0);
        int limit = Args.Int(a, "limit", DefaultLimit, 1, MaxLimit);

        var db = JsonUtil.ActiveDocument().Database;
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var ids = filter.Collect(db, tr);
        var items = new JsonArray();
        foreach (var id in ids.Skip(offset).Take(limit))
            items.Add(Entities.Summary((Entity)tr.GetObject(id, OpenMode.ForRead), tr));
        tr.Commit();

        return new JsonObject
        {
            ["total"] = ids.Count,
            ["offset"] = offset,
            ["count"] = items.Count,
            ["has_more"] = offset + items.Count < ids.Count,
            ["items"] = items,
        };
    }

    /// <summary>Full properties of one or more entities by handle (max 100 per call).</summary>
    public static JsonObject GetEntityProperties(JsonObject a)
    {
        var handles = Args.StrList(a, "handles") ?? (Args.Str(a, "handle") is string h ? new List<string> { h } : null)
            ?? throw new BridgeException("bad_request", "'handles' (or 'handle') is required");
        if (handles.Count > 100) throw new BridgeException("bad_request", "at most 100 handles per call");
        bool xdata = Args.Bool(a, "include_xdata");

        var db = JsonUtil.ActiveDocument().Database;
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var items = new JsonArray();
        foreach (var handle in handles)
        {
            try { items.Add(EntityProps.Full(Entities.OpenEntity(tr, db, handle, OpenMode.ForRead), tr, xdata)); }
            catch (BridgeException ex) { items.Add(new JsonObject { ["handle"] = handle, ["error"] = ex.Message, ["code"] = ex.Code }); }
        }
        tr.Commit();
        return new JsonObject { ["count"] = items.Count, ["items"] = items };
    }

    /// <summary>Objects the user has selected in AutoCAD (the PickFirst set).</summary>
    public static JsonObject GetSelection(JsonObject a)
    {
        int limit = Args.Int(a, "limit", DefaultLimit, 1, MaxLimit);
        var doc = JsonUtil.ActiveDocument();
        var res = doc.Editor.SelectImplied();
        ObjectId[] ids = res.Status == PromptStatus.OK && res.Value != null ? res.Value.GetObjectIds() : Array.Empty<ObjectId>();
        var items = new JsonArray();
        using (var tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
        {
            foreach (var id in ids.Take(limit))
                if (tr.GetObject(id, OpenMode.ForRead) is Entity e) items.Add(Entities.Summary(e, tr));
            tr.Commit();
        }
        return new JsonObject
        {
            ["total"] = ids.Length,
            ["count"] = items.Count,
            ["has_more"] = ids.Length > items.Count,
            ["items"] = items,
        };
    }

    /// <summary>Values of system variables; unknown names get an "error" entry instead of failing the call.</summary>
    public static JsonObject GetSystemVariable(JsonObject a)
    {
        var names = Args.StrList(a, "names") ?? throw new BridgeException("bad_request", "'names' is required");
        var values = new JsonObject();
        foreach (var name in names)
        {
            try { values[name.ToUpperInvariant()] = JsonUtil.FromObject(AcApp.GetSystemVariable(name)); }
            catch { values[name.ToUpperInvariant()] = new JsonObject { ["error"] = "unknown system variable" }; }
        }
        return new JsonObject { ["values"] = values };
    }
}
