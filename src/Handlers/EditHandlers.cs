using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
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
        if (JsonUtil.Busy(doc))
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
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool started = false;
        try
        {
            await CommandContext.Run(doc, async () =>
            {
                started = true;
                await doc.Editor.CommandAsync("_.UNDO", steps.ToString());
            });
        }
        finally
        {
            UndoTracker.EndOwnUndo(doc, started ? steps : 0); // not started: nothing was undone
            if (sw.ElapsedMilliseconds > 5000)
                Log.Info($"undo {steps} step(s) took {sw.ElapsedMilliseconds} ms");
        }
        try { doc.Editor.UpdateScreen(); } catch { }
        return new JsonObject { ["undone"] = changes, ["remaining_mcp_changes"] = UndoTracker.Available(doc) };
    }

    // ---------------------------------------------------------------- properties

    public static JsonObject SetEntityProperties(JsonObject a)
    {
        var dynamic = Args.Obj(a, "dynamic_properties") is { Count: > 0 } dp ? dp : null;
        bool any = a["layer"] != null || a["color"] != null || a["linetype"] != null || a["lineweight"] != null
                   || a["linetype_scale"] != null || a["transparency"] != null || a["visible"] != null || dynamic != null
                   || a["style"] != null;
        if (!any) throw new BridgeException("bad_request",
            "nothing to change: pass layer, color, linetype, lineweight, linetype_scale, transparency, visible, style or dynamic_properties");
        var targets = Targets.Prepare(a, "set_entity_properties");
        if (targets.Preview != null) return targets.Preview;

        return Write.Run("set_entity_properties", ctx =>
        {
            string? layer = Args.Str(a, "layer") is string l ? ctx.Layer(l) : null;
            var color = a["color"] is JsonNode c ? Entities.ColorFromJson(c, "color") : null;
            ObjectId? linetype = Args.Str(a, "linetype") is string lt ? Props.Linetype(ctx.Db, ctx.Tr, lt) : null;
            LineWeight? lineweight = a["lineweight"] is JsonNode lw ? Props.LineWeight(lw, "lineweight") : null;
            double? ltScale = Args.Num(a, "linetype_scale");
            if (ltScale is <= 0) throw new BridgeException("bad_request", "'linetype_scale' must be > 0");
            Autodesk.AutoCAD.Colors.Transparency? transparency =
                a["transparency"] is JsonNode tp ? Props.Transparency(tp, "transparency") : null;
            bool? visible = a["visible"] != null ? Args.Bool(a, "visible") : null;

            if (dynamic != null)
            {
                // All targets must be dynamic block references, otherwise nothing changes.
                var wrong = targets.Ids
                    .Where(id => ctx.Tr.GetObject(id, OpenMode.ForRead) is not BlockReference br || br is Table || !br.IsDynamicBlock)
                    .Select(id => id.Handle.ToString()).ToList();
                if (wrong.Count > 0)
                    throw new BridgeException("bad_request",
                        $"dynamic_properties: {wrong.Count} of {targets.Ids.Count} objects are not dynamic block references " +
                        $"(e.g. {string.Join(", ", wrong.Take(10))}); nothing was changed");
            }
            var dynamicResults = new JsonArray();
            var styleSetter = Args.Str(a, "style") is string styleName ? StyleSetter(ctx, targets.Ids, styleName) : null;

            foreach (var id in targets.Ids)
            {
                var e = (Entity)ctx.Tr.GetObject(id, OpenMode.ForWrite);
                if (dynamic != null && e is BlockReference dbr)
                {
                    BlockHandlers.SetDynamic(dbr, dynamic);
                    if (dynamicResults.Count < 20)
                    {
                        var now = new JsonObject();
                        foreach (DynamicBlockReferenceProperty p in dbr.DynamicBlockReferencePropertyCollection)
                            if (dynamic.Any(kv => kv.Key.Equals(p.PropertyName, StringComparison.OrdinalIgnoreCase)))
                                now[p.PropertyName] = JsonUtil.FromObject(p.Value);
                        dynamicResults.Add(new JsonObject { ["handle"] = id.Handle.ToString(), ["dynamic_properties"] = now });
                    }
                }
                if (layer != null) e.Layer = layer;
                if (color != null) e.Color = color;
                if (linetype is ObjectId ltId) e.LinetypeId = ltId;
                if (lineweight is LineWeight w) e.LineWeight = w;
                if (ltScale is double s) e.LinetypeScale = s;
                if (transparency is { } t) e.Transparency = t;
                if (visible is bool v) e.Visible = v;
                styleSetter?.Invoke(e);
            }
            var result = new JsonObject { ["changed"] = targets.Ids.Count };
            if (dynamic != null) result["dynamic_blocks"] = dynamicResults;
            return result;
        });
    }

    /// <summary>
    /// "style" of set_entity_properties: the style kind follows the object type - TEXT/MTEXT text style,
    /// DIMENSION dimension style (its overrides are dropped), ACAD_TABLE table style (cell-level text style
    /// and height overrides are dropped), MULTILEADER multileader style. Checked for all targets first.
    /// </summary>
    private static Action<Entity> StyleSetter(Write.Ctx ctx, List<ObjectId> ids, string name)
    {
        var tr = ctx.Tr;
        ObjectId Lookup(string kind, Func<bool> has, Func<ObjectId> get) =>
            has() ? get() : throw new BridgeException("not_found", $"{kind} style '{name}' does not exist (see list_styles)", 404);

        ObjectId? text = null, dim = null, table = null, mleader = null;
        var unsupported = new List<string>();
        foreach (var id in ids)
        {
            switch (tr.GetObject(id, OpenMode.ForRead))
            {
                case DBText or MText:
                    var tt = (TextStyleTable)tr.GetObject(ctx.Db.TextStyleTableId, OpenMode.ForRead);
                    text ??= Lookup("text", () => tt.Has(name), () => tt[name]);
                    break;
                case Dimension:
                    var dt = (DimStyleTable)tr.GetObject(ctx.Db.DimStyleTableId, OpenMode.ForRead);
                    dim ??= Lookup("dimension", () => dt.Has(name), () => dt[name]);
                    break;
                case Table:
                    var td = (DBDictionary)tr.GetObject(ctx.Db.TableStyleDictionaryId, OpenMode.ForRead);
                    table ??= Lookup("table", () => td.Contains(name), () => td.GetAt(name));
                    break;
                case MLeader:
                    var md = (DBDictionary)tr.GetObject(ctx.Db.MLeaderStyleDictionaryId, OpenMode.ForRead);
                    mleader ??= Lookup("multileader", () => md.Contains(name), () => md.GetAt(name));
                    break;
                default:
                    unsupported.Add(id.Handle.ToString());
                    break;
            }
        }
        if (unsupported.Count > 0)
            throw new BridgeException("bad_request",
                $"'style' applies to texts, dimensions, tables and multileaders; {unsupported.Count} objects are none of these " +
                $"(e.g. {string.Join(", ", unsupported.Take(10))}); nothing was changed");

        return e =>
        {
            switch (e)
            {
                case DBText t: t.TextStyleId = text!.Value; break;
                case MText m: m.TextStyleId = text!.Value; break;
                case Dimension d:
                    d.DimensionStyle = dim!.Value;
                    d.SetDimstyleData((DimStyleTableRecord)tr.GetObject(dim.Value, OpenMode.ForRead));
                    d.RecomputeDimensionBlock(true);
                    break;
                case Table t:
                    t.TableStyle = table!.Value;
                    // Drop only the cells' own text style / height, other cell formatting stays.
                    for (int r = 0; r < t.Rows.Count; r++)
                        for (int c = 0; c < t.Columns.Count; c++)
                        {
                            var o = t.GetOverrides(r, c, 0);
                            var keep = o & ~(CellProperties.TextStyle | CellProperties.TextHeight);
                            if (keep != o) t.SetOverrides(r, c, 0, keep);
                        }
                    t.GenerateLayout();
                    break;
                case MLeader ml: ml.MLeaderStyle = mleader!.Value; break;
            }
        };
    }

    // ---------------------------------------------------------------- transform

    /// <summary>move / copy / rotate / scale / mirror, each as one matrix applied to all targets.</summary>
    public static JsonObject TransformEntities(JsonObject a)
    {
        string op = Args.Str(a, "operation", required: true)!.ToLowerInvariant();
        Matrix3d m;
        bool keepOriginal = false;
        int copies = 1;
        switch (op)
        {
            case "move":
            case "copy":
                Vector3d v;
                if (Args.Point(a, "vector") is Point3d vec) v = vec - Point3d.Origin;
                else v = Args.Point(a, "to", required: true)!.Value - Args.Point(a, "from", required: true)!.Value;
                if (v.IsZeroLength()) throw new BridgeException("bad_request", "the displacement is zero");
                m = Matrix3d.Displacement(v);
                if (op == "copy") { keepOriginal = true; copies = Args.Int(a, "count", 1, 1, 1000); }
                break;
            case "rotate":
                double angle = Args.Num(a, "angle_deg", required: true)!.Value * Math.PI / 180;
                m = Matrix3d.Rotation(angle, Vector3d.ZAxis, Args.Point(a, "base", required: true)!.Value);
                break;
            case "scale":
                double f = Args.Num(a, "factor", required: true)!.Value;
                if (f <= 0) throw new BridgeException("bad_request", "'factor' must be > 0");
                m = Matrix3d.Scaling(f, Args.Point(a, "base", required: true)!.Value);
                break;
            case "mirror":
                var p1 = Args.Point(a, "p1", required: true)!.Value;
                var p2 = Args.Point(a, "p2", required: true)!.Value;
                if (p1.IsEqualTo(p2)) throw new BridgeException("bad_request", "'p1' and 'p2' must differ");
                m = Matrix3d.Mirroring(new Line3d(p1, p2));
                keepOriginal = Args.Bool(a, "keep_original", true);
                break;
            default:
                throw new BridgeException("bad_request", "'operation' must be move, copy, rotate, scale or mirror");
        }

        var targets = Targets.Prepare(a, op);
        if (targets.Preview != null) return targets.Preview;

        return Write.Run("transform_entities", ctx =>
        {
            var created = new JsonArray();
            foreach (var id in targets.Ids)
            {
                var e = (Entity)ctx.Tr.GetObject(id, OpenMode.ForRead);
                if (!keepOriginal)
                {
                    e.UpgradeOpen();
                    e.TransformBy(m);
                    continue;
                }
                var owner = (BlockTableRecord)ctx.Tr.GetObject(e.OwnerId, OpenMode.ForWrite);
                var step = m;
                for (int i = 0; i < copies; i++)
                {
                    var copy = e.GetTransformedCopy(step);
                    ctx.Add(owner, copy);
                    created.Add(copy.Handle.ToString());
                    step = step.PreMultiplyBy(m); // the next copy one step further
                }
            }
            var r = new JsonObject { ["operation"] = op, ["affected"] = targets.Ids.Count };
            if (keepOriginal) r["created_handles"] = created;
            return r;
        });
    }

    // ---------------------------------------------------------------- delete

    public static JsonObject DeleteEntities(JsonObject a)
    {
        var targets = Targets.Prepare(a, "delete");
        if (targets.Preview != null) return targets.Preview;
        return Write.Run("delete_entities", ctx =>
        {
            foreach (var id in targets.Ids)
                ((Entity)ctx.Tr.GetObject(id, OpenMode.ForWrite)).Erase();
            return new JsonObject { ["deleted"] = targets.Ids.Count };
        });
    }

    // ---------------------------------------------------------------- selection and system variables

    /// <summary>Selects objects with grips for the user (PickFirst); an empty list clears the selection.</summary>
    public static JsonObject SetSelection(JsonObject a)
    {
        var handles = Args.StrList(a, "handles") ?? throw new BridgeException("bad_request", "'handles' is required (an empty list clears)");
        var doc = JsonUtil.ActiveDocument();
        var ids = new List<ObjectId>();
        using (var tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
        {
            foreach (var h in handles) ids.Add(Entities.OpenEntity(tr, doc.Database, h, OpenMode.ForRead).ObjectId);
            tr.Commit();
        }
        doc.Editor.SetImpliedSelection(ids.ToArray());
        try { doc.Editor.UpdateScreen(); } catch { }
        return new JsonObject { ["selected"] = ids.Count };
    }

    public static JsonObject SetSystemVariable(JsonObject a)
    {
        string name = Args.Str(a, "name", required: true)!.ToUpperInvariant();
        var node = a["value"] ?? throw new BridgeException("bad_request", "'value' is required");
        object current;
        try { current = AcApp.GetSystemVariable(name); }
        catch { throw new BridgeException("not_found", $"unknown system variable {name}", 404); }

        object value;
        switch (current)
        {
            case short: value = (short)Args.Int(a, "value", 0, short.MinValue, short.MaxValue); break;
            case int: value = Args.Int(a, "value", 0); break;
            case double: value = Args.Num(a, "value", required: true)!.Value; break;
            case string: value = Args.Str(a, "value", required: true)!; break;
            case Point3d: value = Args.Point(a, "value", required: true)!.Value; break;
            case Point2d:
                var p = Args.Point(a, "value", required: true)!.Value;
                value = new Point2d(p.X, p.Y);
                break;
            default:
                throw new BridgeException("bad_request", $"{name} has a value type that cannot be set here ({current.GetType().Name})");
        }

        return Write.Run("set_system_variable", ctx =>
        {
            try { AcApp.SetSystemVariable(name, value); }
            catch (Exception ex)
            {
                throw new BridgeException("bad_request",
                    $"AutoCAD refused {name} = {node.ToJsonString()}: {ex.Message} (read-only variable or value out of range)");
            }
            return new JsonObject
            {
                ["name"] = name,
                ["old"] = JsonUtil.FromObject(current),
                ["new"] = JsonUtil.FromObject(AcApp.GetSystemVariable(name)),
            };
        });
    }
}
