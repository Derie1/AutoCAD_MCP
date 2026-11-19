using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AcadMcpBridge.Handlers;

/// <summary>
/// View tools: zoom and the current layout. They change only what the user sees, not the drawing,
/// so the undo tool never reverts them instead of a real change (see Zoom and SwitchLayout).
/// </summary>
internal static class ViewHandlers
{
    public static JsonObject Zoom(JsonObject a)
    {
        var doc = Ready();
        var db = doc.Database;
        string mode = (Args.Str(a, "mode")
                       ?? (a["handles"] != null || a["filter"] != null ? "entities" : a["p1"] != null ? "window" : "extents"))
                      .ToLowerInvariant();
        double padding = Args.Num(a, "padding") ?? 0.05;
        if (padding is < 0 or > 1) throw new BridgeException("bad_request", "'padding' must be from 0 to 1 (share of the window size)");

        Extents3d? ext;
        int count = 0;
        switch (mode)
        {
            case "extents":
                ext = SpaceExtents(db, db.CurrentSpaceId, out count);
                if (ext == null) throw new BridgeException("empty", "the current space has no visible objects to zoom to", 404);
                break;
            case "window":
                var p1 = Args.Point(a, "p1", true)!.Value;
                var p2 = Args.Point(a, "p2", true)!.Value;
                ext = new Extents3d(
                    new Point3d(Math.Min(p1.X, p2.X), Math.Min(p1.Y, p2.Y), Math.Min(p1.Z, p2.Z)),
                    new Point3d(Math.Max(p1.X, p2.X), Math.Max(p1.Y, p2.Y), Math.Max(p1.Z, p2.Z)));
                break;
            case "entities":
                ext = EntitiesExtents(db, a, out count);
                break;
            default:
                throw new BridgeException("bad_request", "'mode' must be extents, window or entities");
        }

        // No document lock: a lock would add an UNDO group, and the undo tool must not revert a zoom
        // instead of the assistant's change. SetCurrentView works without one.
        var view = doc.Editor.GetCurrentView();
        Fit(view, ext.Value, padding);
        doc.Editor.SetCurrentView(view);
        var result = Describe(doc);
        result["mode"] = mode;
        if (mode != "window") result["objects"] = count;
        result["zoomed_to"] = new JsonObject { ["min"] = JsonUtil.Point(ext.Value.MinPoint), ["max"] = JsonUtil.Point(ext.Value.MaxPoint) };
        return result;
    }

    public static JsonObject SetCurrentLayout(JsonObject a)
    {
        var doc = Ready();
        string name = Args.Str(a, "name", true)!;
        var lm = LayoutManager.Current;
        string previous = lm.CurrentLayout;
        string? exact = null;
        using (var tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
        {
            var dict = (DBDictionary)tr.GetObject(doc.Database.LayoutDictionaryId, OpenMode.ForRead);
            foreach (var entry in dict)
                if (entry.Key.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                    (name.Equals("model", StringComparison.OrdinalIgnoreCase) && entry.Key.Equals("Model", StringComparison.OrdinalIgnoreCase)))
                    exact = entry.Key;
            tr.Commit();
        }
        if (exact == null) throw new BridgeException("not_found", $"no layout named '{name}'; see list_layouts", 404);

        if (!exact.Equals(previous, StringComparison.Ordinal))
            SwitchLayout(doc, exact);
        var result = Describe(doc);
        result["previous_layout"] = previous;
        return result;
    }

    /// <summary>Layout, space and the visible window of the current view (in the view's own plane).</summary>
    public static JsonObject Describe(Document doc)
    {
        var db = doc.Database;
        var view = doc.Editor.GetCurrentView();
        return new JsonObject
        {
            ["layout"] = LayoutManager.Current.CurrentLayout,
            ["space"] = db.TileMode ? "model" : db.CurrentSpaceId == SymbolUtilityServices.GetBlockModelSpaceId(db) ? "model_in_viewport" : "paper",
            ["view_center"] = new JsonArray(Math.Round(view.CenterPoint.X, 6), Math.Round(view.CenterPoint.Y, 6)),
            ["view_width"] = Math.Round(view.Width, 6),
            ["view_height"] = Math.Round(view.Height, 6),
            ["plan_view"] = view.ViewDirection.IsParallelTo(Vector3d.ZAxis) && view.ViewTwist == 0,
        };
    }

    /// <summary>
    /// Layout switch: needs a document lock, and a lock in application context is an UNDO group of
    /// its own. It is recorded for the undo tool as an empty group, so "undo" still reverts the
    /// assistant's last real change (and switches the layout back along the way).
    /// </summary>
    private static void SwitchLayout(Document doc, string name)
    {
        try
        {
            using (doc.LockDocument())
                LayoutManager.Current.CurrentLayout = name;
        }
        finally
        {
            UndoTracker.Recorded(doc, false);
        }
    }

    private static Document Ready()
    {
        var doc = JsonUtil.ActiveDocument();
        if (!string.IsNullOrEmpty(doc.CommandInProgress))
            throw new BridgeException("busy", $"AutoCAD is running the command {doc.CommandInProgress}; finish or cancel it first", 409);
        return doc;
    }

    /// <summary>Extents of the visible objects of a space (layers off/frozen skipped, like ZOOM Extents).</summary>
    public static Extents3d? SpaceExtents(Database db, ObjectId spaceId, out int count)
    {
        count = 0;
        Extents3d? ext = null;
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var hidden = HiddenLayers(db, tr);
        var space = (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForRead);
        bool firstViewport = true;
        foreach (ObjectId id in space)
        {
            if (id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not Entity e || !e.Visible) continue;
            if (e is Viewport)
            {
                // The first viewport of a layout is the sheet itself, not content.
                if (firstViewport) { firstViewport = false; continue; }
            }
            if (hidden.Contains(e.LayerId)) continue;
            if (!TryExtents(e, out var x)) continue;
            Add(ref ext, x);
            count++;
        }
        tr.Commit();
        return ext;
    }

    private static Extents3d EntitiesExtents(Database db, JsonObject a, out int count)
    {
        count = 0;
        var handles = Args.StrList(a, "handles");
        var filter = Args.Obj(a, "filter");
        if ((handles == null) == (filter == null))
            throw new BridgeException("bad_request", "mode 'entities' needs either 'handles' or 'filter' (find_entities keys)");

        Extents3d? ext = null;
        var current = db.CurrentSpaceId;
        var elsewhere = new List<string>();
        using (var tr = db.TransactionManager.StartOpenCloseTransaction())
        {
            IEnumerable<ObjectId> ids = handles != null
                ? handles.Select(h => Entities.OpenEntity(tr, db, h, OpenMode.ForRead).ObjectId).ToList()
                : new EntityFilter(filter!).Collect(db, tr);
            foreach (var id in ids)
            {
                var e = (Entity)tr.GetObject(id, OpenMode.ForRead);
                if (e.OwnerId != current)
                {
                    // A filter without 'space' looks everywhere; keep only what can be seen here.
                    if (handles != null || filter!["space"] != null) elsewhere.Add(id.Handle.ToString());
                    continue;
                }
                if (!TryExtents(e, out var x)) continue;
                Add(ref ext, x);
                count++;
            }
            tr.Commit();
        }
        if (elsewhere.Count > 0)
            throw new BridgeException("other_space",
                $"{elsewhere.Count} objects are not in the current space/layout (e.g. {string.Join(", ", elsewhere.Take(5))}); " +
                "switch with set_current_layout first", 409);
        return ext ?? throw new BridgeException("not_found", "no objects with extents found in the current space", 404);
    }

    private static HashSet<ObjectId> HiddenLayers(Database db, Transaction tr)
    {
        var set = new HashSet<ObjectId>();
        foreach (ObjectId id in (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead))
        {
            var l = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
            if (l.IsOff || l.IsFrozen) set.Add(id);
        }
        return set;
    }

    private static bool TryExtents(Entity e, out Extents3d ext)
    {
        try { ext = e.GeometricExtents; return true; }
        catch { ext = default; return false; }
    }

    private static void Add(ref Extents3d? total, Extents3d x)
    {
        if (total == null) { total = x; return; }
        var t = total.Value;
        t.AddExtents(x);
        total = t;
    }

    /// <summary>Sets the view so the WCS box fits with the given padding, keeping the window's aspect ratio.</summary>
    private static void Fit(ViewTableRecord view, Extents3d ext, double padding)
    {
        var wcsToDcs = (Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) *
                        Matrix3d.Displacement(view.Target - Point3d.Origin) *
                        Matrix3d.PlaneToWorld(view.ViewDirection)).Inverse();
        Extents3d? dcs = null;
        var lo = ext.MinPoint;
        var hi = ext.MaxPoint;
        foreach (var x in new[] { lo.X, hi.X })
            foreach (var y in new[] { lo.Y, hi.Y })
                foreach (var z in new[] { lo.Z, hi.Z })
                {
                    var p = new Point3d(x, y, z).TransformBy(wcsToDcs);
                    Add(ref dcs, new Extents3d(p, p));
                }
        var d = dcs!.Value;
        double w = d.MaxPoint.X - d.MinPoint.X, h = d.MaxPoint.Y - d.MinPoint.Y;
        double size = Math.Max(w, h);
        if (size <= 1e-9) size = 1;
        w = Math.Max(w, size * 1e-3) * (1 + 2 * padding);
        h = Math.Max(h, size * 1e-3) * (1 + 2 * padding);
        double ratio = view.Height > 0 ? view.Width / view.Height : 1;
        if (w / h > ratio) h = w / ratio; else w = h * ratio;

        view.CenterPoint = new Point2d((d.MinPoint.X + d.MaxPoint.X) / 2, (d.MinPoint.Y + d.MaxPoint.Y) / 2);
        view.Width = w;
        view.Height = h;
    }
}
