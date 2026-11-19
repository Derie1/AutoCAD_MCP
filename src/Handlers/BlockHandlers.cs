using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AcadMcpBridge.Handlers;

internal static class BlockHandlers
{
    /// <summary>
    /// Inserts a block that is defined in the drawing: position, scale, rotation, attribute values
    /// {TAG: value} (others get their defaults) and dynamic block properties {name: value}.
    /// </summary>
    public static JsonObject InsertBlock(JsonObject a) => Write.Run("insert_block", ctx =>
    {
        string name = Args.Str(a, "name", required: true)!;
        var bt = (BlockTable)ctx.Tr.GetObject(ctx.Db.BlockTableId, OpenMode.ForRead);
        if (!bt.Has(name)) throw new BridgeException("not_found", $"block '{name}' is not defined in this drawing (see list_blocks)", 404);
        var btr = (BlockTableRecord)ctx.Tr.GetObject(bt[name], OpenMode.ForRead);
        if (btr.IsLayout || btr.IsFromExternalReference)
            throw new BridgeException("bad_request", $"'{name}' is a layout or an xref, not a block");

        var scale = Scale(a);
        var br = new BlockReference(Args.Point(a, "position", required: true)!.Value, btr.ObjectId)
        {
            ScaleFactors = scale,
            Rotation = (Args.Num(a, "rotation_deg") ?? 0) * Math.PI / 180,
        };
        br.SetDatabaseDefaults(ctx.Db);
        Props.ApplyCommon(ctx, br, a);
        ctx.Add(ctx.CurrentSpace(), br);

        // Attributes: one reference per non-constant definition.
        var values = Args.Obj(a, "attributes") ?? [];
        var tags = new List<string>();
        foreach (ObjectId id in btr)
        {
            if (id.ObjectClass.DxfName != "ATTDEF" || ctx.Tr.GetObject(id, OpenMode.ForRead) is not AttributeDefinition ad || ad.Constant) continue;
            tags.Add(ad.Tag);
            var ar = new AttributeReference();
            ar.SetAttributeFromBlock(ad, br.BlockTransform);
            if (FindValue(values, ad.Tag) is string v) SetText(ar, v);
            br.AttributeCollection.AppendAttribute(ar);
            ctx.Tr.AddNewlyCreatedDBObject(ar, true);
        }
        var unknown = values.Select(kv => kv.Key).Where(k => !tags.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
        if (unknown.Count > 0)
            throw new BridgeException("bad_request", $"block '{name}' has no attribute {string.Join(", ", unknown)}; " +
                                                     $"its attributes: {(tags.Count > 0 ? string.Join(", ", tags) : "none")}");

        if (Args.Obj(a, "dynamic_properties") is { Count: > 0 } dyn)
        {
            if (!br.IsDynamicBlock) throw new BridgeException("bad_request", $"block '{name}' is not a dynamic block");
            SetDynamic(br, dyn);
        }
        return EntityProps.Full(br, ctx.Tr, withXdata: false);
    });

    /// <summary>
    /// Sets attribute values. Either the same "values" {TAG: value} for the objects chosen by
    /// handles / filter (see Targets), or "updates" [{handle, values}] with different values per block.
    /// </summary>
    public static JsonObject UpdateBlockAttributes(JsonObject a)
    {
        var plan = new List<(ObjectId id, JsonObject values)>();
        if (a["updates"] is JsonArray updates)
        {
            if (a["handles"] != null || a["filter"] != null || a["values"] != null)
                throw new BridgeException("bad_request", "use either 'updates' or handles/filter + 'values'");
            var handles = new JsonArray();
            foreach (var u in updates)
            {
                if (u is not JsonObject uo || Args.Str(uo, "handle") is not string h || Args.Obj(uo, "values") is not JsonObject v)
                    throw new BridgeException("bad_request", "each update must be {\"handle\": \"…\", \"values\": {TAG: value}}");
                handles.Add(h);
                plan.Add((ObjectId.Null, v));
            }
            var t = Targets.Prepare(new JsonObject { ["handles"] = handles, ["dry_run"] = Args.Bool(a, "dry_run") }, "update_block_attributes");
            if (t.Preview != null) return t.Preview;
            if (t.Ids.Count != plan.Count) throw new BridgeException("bad_request", "a handle appears twice in 'updates'");
            for (int i = 0; i < plan.Count; i++) plan[i] = (t.Ids[i], plan[i].values);
        }
        else
        {
            var values = Args.Obj(a, "values") ?? throw new BridgeException("bad_request", "'values' {TAG: value} (or 'updates') is required");
            var t = Targets.Prepare(a, "update_block_attributes");
            if (t.Preview != null) return t.Preview;
            plan.AddRange(t.Ids.Select(id => (id, values)));
        }

        return Write.Run("update_block_attributes", ctx =>
        {
            int blocks = 0, changed = 0;
            var notBlocks = new List<string>();
            var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, values) in plan)
            {
                if (ctx.Tr.GetObject(id, OpenMode.ForRead) is not BlockReference br || br is Table)
                {
                    notBlocks.Add(id.Handle.ToString());
                    continue;
                }
                blocks++;
                var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ObjectId aid in br.AttributeCollection)
                {
                    if (ctx.Tr.GetObject(aid, OpenMode.ForRead) is not AttributeReference ar) continue;
                    if (FindValue(values, ar.Tag) is not string v) continue;
                    found.Add(ar.Tag);
                    ar.UpgradeOpen();
                    SetText(ar, v);
                    changed++;
                }
                foreach (var kv in values) if (!found.Contains(kv.Key)) missing.Add(kv.Key);
            }
            if (changed == 0)
                throw new BridgeException("not_found", notBlocks.Count == plan.Count
                    ? "none of the objects is a block reference"
                    : $"no attribute with tag {string.Join(", ", missing)} on these blocks", 404);
            var r = new JsonObject { ["blocks"] = blocks, ["attributes_changed"] = changed };
            if (notBlocks.Count > 0) r["skipped_not_blocks"] = new JsonArray(notBlocks.Select(h => (JsonNode?)h).ToArray());
            if (missing.Count > 0) r["tags_missing_on_some_blocks"] = new JsonArray(missing.Select(h => (JsonNode?)h).ToArray());
            return r;
        });
    }

    // ---------------------------------------------------------------- helpers

    private static Scale3d Scale(JsonObject a)
    {
        switch (a["scale"])
        {
            case null: return new Scale3d(1);
            case JsonValue v when v.TryGetValue(out double s) && s != 0: return new Scale3d(s);
            case JsonArray arr when arr.Count is 2 or 3:
                try
                {
                    double x = arr[0]!.GetValue<double>(), y = arr[1]!.GetValue<double>();
                    double z = arr.Count == 3 ? arr[2]!.GetValue<double>() : 1;
                    if (x != 0 && y != 0 && z != 0) return new Scale3d(x, y, z);
                }
                catch (InvalidOperationException) { }
                break;
        }
        throw new BridgeException("bad_request", "'scale' must be a non-zero number or [x, y] / [x, y, z]");
    }

    private static string? FindValue(JsonObject values, string tag)
    {
        foreach (var kv in values)
            if (kv.Key.Equals(tag, StringComparison.OrdinalIgnoreCase))
                return kv.Value switch
                {
                    null => "",
                    JsonValue v when v.TryGetValue(out string? s) => s,
                    _ => kv.Value.ToJsonString().Trim('"'),
                };
        return null;
    }

    private static void SetText(AttributeReference ar, string value)
    {
        if (ar.IsMTextAttribute)
        {
            var m = ar.MTextAttribute;
            m.Contents = value.Replace("\r\n", "\n").Replace("\n", "\\P");
            ar.MTextAttribute = m;
            ar.UpdateMTextAttribute();
        }
        else ar.TextString = value;
    }

    private static bool SameValue(object? a, object? b)
    {
        if (a is string sa || b is string) return string.Equals(a?.ToString(), b?.ToString(), StringComparison.OrdinalIgnoreCase);
        try { return Math.Abs(Convert.ToDouble(a) - Convert.ToDouble(b)) < 1e-9; }
        catch { return Equals(a, b); }
    }

    internal static void SetDynamic(BlockReference br, JsonObject dyn)
    {
        var props = br.DynamicBlockReferencePropertyCollection;
        foreach (var kv in dyn)
        {
            DynamicBlockReferenceProperty? p = null;
            foreach (DynamicBlockReferenceProperty x in props)
                if (x.PropertyName.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)) { p = x; break; }
            if (p == null)
            {
                var names = props.Cast<DynamicBlockReferenceProperty>().Where(x => x.PropertyName != "Origin").Select(x => x.PropertyName);
                throw new BridgeException("bad_request", $"no dynamic property '{kv.Key}'; available: {string.Join(", ", names)}");
            }
            if (p.ReadOnly) throw new BridgeException("bad_request", $"dynamic property '{p.PropertyName}' is read-only");
            var allowed = p.GetAllowedValues();
            string AllowedText() => allowed is { Length: > 0 } ? $"; allowed: {string.Join(", ", allowed)}" : "";
            object value;
            try
            {
                value = p.Value switch
                {
                    double => kv.Value!.GetValue<double>(),
                    short => (short)kv.Value!.GetValue<double>(),
                    int => (int)kv.Value!.GetValue<double>(),
                    _ => kv.Value is JsonValue jv && jv.TryGetValue(out string? s) ? s : kv.Value!.ToJsonString(),
                };
            }
            catch (Exception ex) when (ex is not BridgeException)
            {
                throw new BridgeException("bad_request", $"value {kv.Value?.ToJsonString()} has the wrong type for '{p.PropertyName}'{AllowedText()}");
            }
            // AutoCAD silently snaps a value outside a list parameter to the nearest allowed one; refuse instead.
            if (allowed is { Length: > 0 } && !allowed.Any(x => SameValue(x, value)))
                throw new BridgeException("bad_request", $"value {kv.Value?.ToJsonString()} is not accepted for '{p.PropertyName}'{AllowedText()}");
            try
            {
                p.Value = value;
            }
            catch (Exception ex) when (ex is not BridgeException)
            {
                throw new BridgeException("bad_request", $"value {kv.Value?.ToJsonString()} is not accepted for '{p.PropertyName}'{AllowedText()}");
            }
            // Limits / increments of a parameter can change the value too.
            if (!SameValue(p.Value, value))
                throw new BridgeException("bad_request",
                    $"AutoCAD changed {kv.Value?.ToJsonString()} to {JsonUtil.FromObject(p.Value)?.ToJsonString()} for '{p.PropertyName}' " +
                    "(parameter limits or increments); nothing was changed");
        }
    }
}
