using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;

namespace AcadMcpBridge.Handlers;

/// <summary>Copying definitions (layers, linetypes, styles, blocks) from another DWG or template.</summary>
internal static class ImportHandlers
{
    private static readonly string[] Kinds =
        ["layers", "linetypes", "text_styles", "dim_styles", "table_styles", "mleader_styles", "blocks"];

    public static JsonObject ImportFromDrawing(JsonObject a)
    {
        string path = FileHandlers.FullPath(a, "path", [".dwg", ".dwt"]);
        if (!File.Exists(path)) throw new BridgeException("not_found", $"file '{path}' does not exist", 404);
        var kinds = Args.StrList(a, "kinds")
                    ?? throw new BridgeException("bad_request", $"'kinds' is required: one or more of {string.Join(", ", Kinds)}");
        foreach (var k in kinds)
            if (!Kinds.Contains(k)) throw new BridgeException("bad_request", $"unknown kind '{k}'; kinds: {string.Join(", ", Kinds)}");
        var mask = CatalogHandlers.Wildcard(Args.Str(a, "names"));
        string onConflict = Args.Str(a, "on_conflict") ?? "skip";
        if (onConflict is not ("skip" or "replace")) throw new BridgeException("bad_request", "'on_conflict' must be skip or replace");

        return Write.Run("import_from_drawing", ctx =>
        {
            var db = ctx.Db;
            if (ctx.Doc.IsNamedDrawing && Path.GetFullPath(ctx.Doc.Name).Equals(path, StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("bad_request", "the source is the active drawing itself");

            using var src = new Database(false, true);
            try { src.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, null); }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                throw new BridgeException("bad_file", $"cannot read '{path}': {ex.ErrorStatus}", 400);
            }

            var before = Snapshot(db, ctx.Tr);
            var imported = new JsonObject();
            var replaced = new JsonObject();
            var skipped = new JsonObject();
            int total = 0;
            using (var str = src.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (var kind in kinds)
                {
                    var (srcOwner, dstOwner) = Owners(kind, src, db);
                    var have = Names(kind, db, ctx.Tr);
                    var take = new ObjectIdCollection();
                    var newNames = new List<string>();
                    var replNames = new List<string>();
                    var skipNames = new List<string>();
                    foreach (var (name, id) in Entries(kind, src, srcOwner, str))
                    {
                        if (mask != null && !mask.IsMatch(name)) continue;
                        bool exists = have.Contains(name);
                        if (exists && onConflict == "skip") { skipNames.Add(name); continue; }
                        take.Add(id);
                        (exists ? replNames : newNames).Add(name);
                    }
                    if (take.Count > 0)
                        db.WblockCloneObjects(take, dstOwner, new IdMapping(),
                            onConflict == "replace" ? DuplicateRecordCloning.Replace : DuplicateRecordCloning.Ignore, false);
                    imported[kind] = List(newNames);
                    if (replNames.Count > 0) replaced[kind] = List(replNames);
                    if (skipNames.Count > 0) skipped[kind] = List(skipNames);
                    total += take.Count;
                }
                str.Commit();
            }

            // Objects the imported ones depend on (a layer's linetype, a style's text style, a block's layers).
            var after = Snapshot(db, ctx.Tr);
            var alsoAdded = new JsonObject();
            foreach (var kind in Kinds)
            {
                var added = after[kind].Except(before[kind], StringComparer.OrdinalIgnoreCase)
                    .Where(n => !(imported[kind] is JsonArray arr && arr.Any(x => x!.ToString().Equals(n, StringComparison.OrdinalIgnoreCase))))
                    .ToList();
                if (added.Count > 0) alsoAdded[kind] = List(added);
            }

            if (total == 0 && skipped.Count == 0)
                throw new BridgeException("not_found", $"nothing in '{Path.GetFileName(path)}' matches kinds {string.Join(", ", kinds)}"
                                                      + (mask != null ? $" and names '{Args.Str(a, "names")}'" : ""), 404);
            return new JsonObject
            {
                ["source"] = path,
                ["imported"] = imported,
                ["replaced"] = replaced,
                ["skipped_existing"] = skipped,
                ["also_added"] = alsoAdded,
                ["note"] = skipped.Count > 0 ? "existing definitions were kept; on_conflict: replace overwrites them" : null,
            };
        });
    }

    private static JsonArray List(IEnumerable<string> names) =>
        new(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Select(n => (JsonNode)n).ToArray());

    private static (ObjectId Src, ObjectId Dst) Owners(string kind, Database src, Database dst) => kind switch
    {
        "layers" => (src.LayerTableId, dst.LayerTableId),
        "linetypes" => (src.LinetypeTableId, dst.LinetypeTableId),
        "text_styles" => (src.TextStyleTableId, dst.TextStyleTableId),
        "dim_styles" => (src.DimStyleTableId, dst.DimStyleTableId),
        "table_styles" => (src.TableStyleDictionaryId, dst.TableStyleDictionaryId),
        "mleader_styles" => (src.MLeaderStyleDictionaryId, dst.MLeaderStyleDictionaryId),
        "blocks" => (src.BlockTableId, dst.BlockTableId),
        _ => throw new BridgeException("bad_request", $"unknown kind '{kind}'"),
    };

    /// <summary>Importable named objects of one kind: no xref-dependent, anonymous or layout records,
    /// no built-in ones (layer 0, ByLayer/ByBlock/Continuous).</summary>
    private static IEnumerable<(string Name, ObjectId Id)> Entries(string kind, Database db, ObjectId owner, Transaction tr)
    {
        if (kind is "table_styles" or "mleader_styles")
        {
            foreach (DBDictionaryEntry e in (DBDictionary)tr.GetObject(owner, OpenMode.ForRead)) yield return (e.Key, e.Value);
            yield break;
        }
        foreach (ObjectId id in (SymbolTable)tr.GetObject(owner, OpenMode.ForRead))
        {
            var r = (SymbolTableRecord)tr.GetObject(id, OpenMode.ForRead);
            if (r.IsDependent) continue;
            if (r is BlockTableRecord b && (b.IsLayout || b.IsAnonymous || b.IsFromExternalReference)) continue;
            if (r is LayerTableRecord && r.Name == "0") continue;
            if (r is LinetypeTableRecord && (id == db.ByLayerLinetype || id == db.ByBlockLinetype || id == db.ContinuousLinetype)) continue;
            if (r is TextStyleTableRecord t && t.IsShapeFile) continue;
            yield return (r.Name, id);
        }
    }

    private static HashSet<string> Names(string kind, Database db, Transaction tr)
    {
        var (_, owner) = Owners(kind, db, db);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (kind is "table_styles" or "mleader_styles")
            foreach (DBDictionaryEntry e in (DBDictionary)tr.GetObject(owner, OpenMode.ForRead)) set.Add(e.Key);
        else
            foreach (ObjectId id in (SymbolTable)tr.GetObject(owner, OpenMode.ForRead))
                set.Add(((SymbolTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name);
        return set;
    }

    private static Dictionary<string, HashSet<string>> Snapshot(Database db, Transaction tr) =>
        Kinds.ToDictionary(k => k, k => Names(k, db, tr));
}
