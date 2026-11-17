using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;

namespace AcadMcpBridge;

/// <summary>
/// The objects a changing tool acts on: either explicit "handles" or a "filter" (the same keys as
/// find_entities). Resolved read-only BEFORE the drawing is locked, so a dry run or a refusal
/// leaves no UNDO group behind.
///   dry_run: true  -> nothing changes, the answer lists what would be affected
///   filter         -> needs confirm: true (bulk change the user should have agreed to)
///   locked layers  -> the call is refused, nothing changes
/// </summary>
internal sealed class Targets
{
    public const int MaxHandles = 2000;

    public List<ObjectId> Ids { get; } = [];
    public JsonObject? Preview { get; private set; }

    public static Targets Prepare(JsonObject a, string action, bool checkLocked = true)
    {
        var handles = Args.StrList(a, "handles");
        var filter = Args.Obj(a, "filter");
        if ((handles == null) == (filter == null))
            throw new BridgeException("bad_request", "pass either 'handles' or 'filter' (find_entities keys), not both");
        if (handles is { Count: > MaxHandles })
            throw new BridgeException("bad_request", $"at most {MaxHandles} handles per call; use 'filter' for more");

        var t = new Targets();
        var db = JsonUtil.ActiveDocument().Database;
        var lockedHandles = new List<string>();
        using (var tr = db.TransactionManager.StartOpenCloseTransaction())
        {
            if (handles != null)
                foreach (var h in handles.Distinct(StringComparer.OrdinalIgnoreCase))
                    t.Ids.Add(Entities.OpenEntity(tr, db, h, OpenMode.ForRead).ObjectId);
            else
                t.Ids.AddRange(new EntityFilter(filter!).Collect(db, tr));

            if (checkLocked)
            {
                var locked = LockedLayers(db, tr);
                if (locked.Count > 0)
                    foreach (var id in t.Ids)
                        if (locked.Contains(((Entity)tr.GetObject(id, OpenMode.ForRead)).Layer))
                            lockedHandles.Add(id.Handle.ToString());
            }

            if (Args.Bool(a, "dry_run"))
            {
                var sample = new JsonArray();
                foreach (var id in t.Ids.Take(50))
                    sample.Add(Entities.Summary((Entity)tr.GetObject(id, OpenMode.ForRead), tr));
                t.Preview = new JsonObject
                {
                    ["dry_run"] = true,
                    ["action"] = action,
                    ["would_affect"] = t.Ids.Count,
                    ["on_locked_layers"] = lockedHandles.Count,
                    ["sample"] = sample,
                };
            }
            tr.Commit();
        }
        if (t.Preview != null) return t;

        if (t.Ids.Count == 0)
            throw new BridgeException("not_found", "no objects match", 404);
        if (lockedHandles.Count > 0)
            throw new BridgeException("locked_layer",
                $"{lockedHandles.Count} of {t.Ids.Count} objects are on locked layers (e.g. {string.Join(", ", lockedHandles.Take(10))}); " +
                "unlock the layers or leave these objects out. Nothing was changed.", 409);
        if (filter != null && !Args.Bool(a, "confirm"))
            throw new BridgeException("confirm_required",
                $"{action} would affect {t.Ids.Count} objects selected by a filter; check with dry_run and repeat with confirm: true", 409);
        return t;
    }

    private static HashSet<string> LockedLayers(Database db, Transaction tr)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ObjectId id in (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead))
        {
            var l = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
            if (l.IsLocked) set.Add(l.Name);
        }
        return set;
    }
}
