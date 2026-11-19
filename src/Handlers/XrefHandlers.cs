using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AcadMcpBridge.Handlers;

/// <summary>External references: attach / overlay, reload, unload, detach, bind, change the path.</summary>
internal static class XrefHandlers
{
    /// <summary>Attaches (or overlays) a DWG and inserts one reference to it in the current space.</summary>
    public static JsonObject AttachXref(JsonObject a) => Write.Run("attach_xref", ctx =>
    {
        string path = FileHandlers.FullPath(a, "path", [".dwg"]);
        if (!File.Exists(path)) throw new BridgeException("not_found", $"file '{path}' does not exist", 404);
        string? host = ctx.Doc.IsNamedDrawing ? Path.GetFullPath(ctx.Doc.Name) : null;
        if (host != null && host.Equals(path, StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("bad_request", "a drawing cannot reference itself");

        string name = Args.Str(a, "name") ?? Path.GetFileNameWithoutExtension(path);
        try { SymbolUtilityServices.ValidateSymbolName(name, false); }
        catch { throw new BridgeException("bad_request", $"'{name}' is not a valid block name; give 'name'"); }
        var bt = (BlockTable)ctx.Tr.GetObject(ctx.Db.BlockTableId, OpenMode.ForRead);
        if (bt.Has(name))
            throw new BridgeException("name_in_use", $"a block or external reference named '{name}' already exists; give another 'name'", 409);

        string pathType = Args.Str(a, "path_type") ?? (host != null ? "relative" : "full");
        if (pathType is not ("relative" or "full"))
            throw new BridgeException("bad_request", "'path_type' must be relative or full");
        if (pathType == "relative" && host == null)
            throw new BridgeException("bad_request", "a relative path needs the drawing to be saved first (save_as); use path_type: full");
        if (pathType == "relative" && !Path.GetPathRoot(host!)!.Equals(Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("bad_request", "the file is on another drive than the drawing; use path_type: full");

        bool overlay = Args.Bool(a, "overlay");
        double scale = Args.Num(a, "scale") ?? 1;
        if (scale == 0) throw new BridgeException("bad_request", "'scale' must not be 0");
        var pos = Args.Point(a, "position") ?? Point3d.Origin;
        double rot = (Args.Num(a, "rotation_deg") ?? 0) * Math.PI / 180;

        var id = overlay ? ctx.Db.OverlayXref(path, name) : ctx.Db.AttachXref(path, name);
        if (id.IsNull) throw new BridgeException("autocad_error", $"AutoCAD could not attach '{path}'", 500);
        var btr = (BlockTableRecord)ctx.Tr.GetObject(id, OpenMode.ForWrite);
        if (pathType == "relative")
        {
            string rel = Path.GetRelativePath(Path.GetDirectoryName(host!)!, path);
            btr.PathName = rel.StartsWith("..") ? rel : ".\\" + rel;
        }
        else btr.PathName = path; // AutoCAD would store it relative to the current folder (REFPATHTYPE)

        var br = new BlockReference(pos, id) { ScaleFactors = new Scale3d(scale), Rotation = rot };
        br.SetDatabaseDefaults(ctx.Db);
        Props.ApplyCommon(ctx, br, a);
        ctx.Add(ctx.CurrentSpace(), br);

        var o = Describe(btr, ctx.Tr);
        o["handle"] = br.Handle.ToString();
        o["position"] = JsonUtil.Point(pos);
        o["scale"] = scale;
        o["rotation_deg"] = Args.Num(a, "rotation_deg") ?? 0;
        o["layer"] = br.Layer;
        return o;
    });

    /// <summary>reload / unload / detach / bind / insert / repath for named external references.</summary>
    public static JsonObject ManageXrefs(JsonObject a)
    {
        string action = (Args.Str(a, "action", required: true) ?? "").ToLowerInvariant();
        if (action is not ("reload" or "unload" or "detach" or "bind" or "insert" or "repath"))
            throw new BridgeException("bad_request", "'action' must be reload, unload, detach, bind, insert or repath");
        var names = Args.StrList(a, "names") ?? throw new BridgeException("bad_request", "'names' is required: xref names (see list_xrefs) or '*' for all");

        return Write.RunLocked("manage_xrefs", doc =>
        {
            var db = doc.Database;
            var ids = new ObjectIdCollection();
            var picked = new List<string>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var all = new List<BlockTableRecord>();
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId id in bt)
                {
                    var b = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (b.IsFromExternalReference && !b.IsDependent) all.Add(b);
                }
                if (all.Count == 0) throw new BridgeException("not_found", "the drawing has no external references", 404);
                foreach (var n in names)
                {
                    var hits = n == "*" ? all : all.Where(b => b.Name.Equals(n, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (hits.Count == 0)
                        throw new BridgeException("not_found", $"no external reference '{n}'; available: {string.Join(", ", all.Select(b => b.Name))}", 404);
                    foreach (var b in hits)
                        if (!ids.Contains(b.ObjectId)) { ids.Add(b.ObjectId); picked.Add(b.Name); }
                }

                if (action == "repath")
                {
                    if (ids.Count != 1) throw new BridgeException("bad_request", "repath changes one external reference at a time");
                    string path = FileHandlers.FullPath(a, "path", [".dwg"]);
                    if (!File.Exists(path)) throw new BridgeException("not_found", $"file '{path}' does not exist", 404);
                    var b = (BlockTableRecord)tr.GetObject(ids[0], OpenMode.ForWrite);
                    b.PathName = path;
                }
                else if (action is "bind" or "insert")
                {
                    var notLoaded = ids.Cast<ObjectId>().Select(id => (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead))
                                       .Where(b => b.XrefStatus != XrefStatus.Resolved).Select(b => b.Name).ToList();
                    if (notLoaded.Count > 0)
                        throw new BridgeException("not_loaded", $"only loaded references can be bound; not loaded: {string.Join(", ", notLoaded)} (reload or repath them first)", 409);
                }
                tr.Commit();
            }

            switch (action)
            {
                case "reload": case "repath": db.ReloadXrefs(ids); break;
                case "unload": db.UnloadXrefs(ids); break;
                case "bind": db.BindXrefs(ids, false); break;
                case "insert": db.BindXrefs(ids, true); break;
                case "detach": foreach (ObjectId id in ids) db.DetachXref(id); break;
            }

            var o = new JsonObject { ["action"] = action, ["names"] = new JsonArray(picked.Select(n => (JsonNode)n).ToArray()) };
            o["xrefs"] = CatalogHandlers.ListXrefs(new JsonObject())["xrefs"]!.DeepClone();
            return o;
        });
    }

    private static JsonObject Describe(BlockTableRecord b, Transaction tr) => new()
    {
        ["name"] = b.Name,
        ["path"] = b.PathName,
        ["status"] = b.XrefStatus.ToString(),
        ["overlay"] = b.IsFromOverlayReference,
    };
}
