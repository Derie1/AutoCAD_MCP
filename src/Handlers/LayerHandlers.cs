using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;

namespace AcadMcpBridge.Handlers;

internal static class LayerHandlers
{
    public static JsonObject CreateLayer(JsonObject a)
    {
        string name = Args.Str(a, "name", required: true)!;
        try { SymbolUtilityServices.ValidateSymbolName(name, false); }
        catch { throw new BridgeException("bad_request", $"'{name}' is not a valid layer name"); }

        return Write.Run("create_layer", ctx =>
        {
            var lt = (LayerTable)ctx.Tr.GetObject(ctx.Db.LayerTableId, OpenMode.ForWrite);
            if (lt.Has(name)) throw new BridgeException("already_exists", $"layer '{name}' already exists; use set_layer_state", 409);
            // Continuous by default, as the LAYER command does; a new record has no linetype otherwise.
            var layer = new LayerTableRecord { Name = name, LinetypeObjectId = ctx.Db.ContinuousLinetype };
            lt.Add(layer);
            ctx.Tr.AddNewlyCreatedDBObject(layer, true);
            Apply(ctx, layer, a);
            return Describe(ctx, layer);
        });
    }

    /// <summary>Changes one or more layers (names with wildcards). Every listed change is applied to each.</summary>
    public static JsonObject SetLayerState(JsonObject a)
    {
        var names = Args.StrList(a, "names") ?? throw new BridgeException("bad_request", "'names' is required");
        string? newName = Args.Str(a, "new_name");
        if (newName != null)
        {
            if (names.Count != 1 || names[0].Contains('*') || names[0].Contains('?'))
                throw new BridgeException("bad_request", "'new_name' needs exactly one layer name without wildcards");
            try { SymbolUtilityServices.ValidateSymbolName(newName, false); }
            catch { throw new BridgeException("bad_request", $"'{newName}' is not a valid layer name"); }
        }
        var patterns = names.Select(n => new System.Text.RegularExpressions.Regex(
            "^" + System.Text.RegularExpressions.Regex.Escape(n).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToList();

        return Write.Run("set_layer_state", ctx =>
        {
            var lt = (LayerTable)ctx.Tr.GetObject(ctx.Db.LayerTableId, OpenMode.ForRead);
            var changed = new JsonArray();
            foreach (ObjectId id in lt)
            {
                var layer = (LayerTableRecord)ctx.Tr.GetObject(id, OpenMode.ForRead);
                if (!patterns.Any(p => p.IsMatch(layer.Name))) continue;
                layer.UpgradeOpen();
                if (newName != null)
                {
                    if (layer.Name == "0" || layer.IsDependent)
                        throw new BridgeException("bad_request", $"layer '{layer.Name}' cannot be renamed");
                    if (lt.Has(newName)) throw new BridgeException("already_exists", $"layer '{newName}' already exists", 409);
                    layer.Name = newName;
                }
                Apply(ctx, layer, a);
                changed.Add(Describe(ctx, layer));
            }
            if (changed.Count == 0) throw new BridgeException("not_found", $"no layer matches {string.Join(", ", names)}", 404);
            return new JsonObject { ["count"] = changed.Count, ["layers"] = changed };
        });
    }

    private static void Apply(Write.Ctx ctx, LayerTableRecord layer, JsonObject a)
    {
        if (a["color"] is JsonNode c)
        {
            var color = Entities.ColorFromJson(c, "color");
            if (color.IsByLayer || color.IsByBlock) throw new BridgeException("bad_request", "a layer color must be an ACI number or #RRGGBB");
            layer.Color = color;
        }
        if (Args.Str(a, "linetype") is string ltName)
        {
            var ltId = Props.Linetype(ctx.Db, ctx.Tr, ltName);
            if (ltId == ctx.Db.ByLayerLinetype || ltId == ctx.Db.ByBlockLinetype)
                throw new BridgeException("bad_request", "a layer linetype cannot be ByLayer or ByBlock");
            layer.LinetypeObjectId = ltId;
        }
        if (a["lineweight"] is JsonNode lw) layer.LineWeight = Props.LineWeight(lw, "lineweight", allowByLayer: false);
        if (Args.Str(a, "description") is string d) layer.Description = d;
        if (a["plottable"] != null) layer.IsPlottable = Args.Bool(a, "plottable");
        if (a["on"] != null) layer.IsOff = !Args.Bool(a, "on");
        if (a["locked"] != null) layer.IsLocked = Args.Bool(a, "locked");

        bool makeCurrent = Args.Bool(a, "current");
        if (a["frozen"] != null)
        {
            bool frozen = Args.Bool(a, "frozen");
            if (frozen && (layer.ObjectId == ctx.Db.Clayer || makeCurrent))
                throw new BridgeException("bad_request", $"the current layer '{layer.Name}' cannot be frozen");
            layer.IsFrozen = frozen;
        }
        if (makeCurrent)
        {
            if (layer.IsFrozen) throw new BridgeException("bad_request", $"frozen layer '{layer.Name}' cannot be made current");
            ctx.Db.Clayer = layer.ObjectId;
        }
    }

    private static JsonObject Describe(Write.Ctx ctx, LayerTableRecord l) => new()
    {
        ["name"] = l.Name,
        ["color"] = Entities.ColorToJson(l.Color),
        ["linetype"] = Props.LayerLinetypeName(ctx.Tr, l),
        ["lineweight"] = l.LineWeight.ToString(),
        ["on"] = !l.IsOff,
        ["frozen"] = l.IsFrozen,
        ["locked"] = l.IsLocked,
        ["plottable"] = l.IsPlottable,
        ["current"] = l.ObjectId == ctx.Db.Clayer,
    };
}
