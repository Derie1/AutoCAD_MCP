using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadMcpBridge.Handlers;

/// <summary>
/// Dimension appearance as per-object overrides (the way the Properties palette does it):
/// text height, arrows, text style, decimals, overall scale, fit and text position.
/// Shared by create_dimension and modify_dimension.
/// </summary>
internal static class DimensionHandlers
{
    private static readonly string[] Keys =
        ["text_override", "text_height", "arrow_size", "arrow", "text_style", "decimals", "dim_scale", "fit", "text_position", "text_leader"];

    private static readonly Dictionary<string, (int AtFit, bool Tix)> Fits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["best"] = (3, false),          // AutoCAD default: whatever fits best goes outside
        ["arrows"] = (1, false),        // move arrows outside first
        ["text"] = (2, false),          // move text outside first
        ["both_outside"] = (0, false),  // text and arrows outside together
        ["text_inside"] = (3, true),    // always keep text between the extension lines
    };

    /// <summary>Changes existing dimensions (handles or filter); one UNDO step.</summary>
    public static JsonObject ModifyDimension(JsonObject a)
    {
        bool any = Keys.Any(k => a[k] != null) || a["style"] != null || Args.Bool(a, "reset_overrides");
        if (!any) throw new BridgeException("bad_request",
            "nothing to change: pass style, reset_overrides or one of " + string.Join(", ", Keys));
        var targets = Targets.Prepare(a, "modify_dimension");
        if (targets.Preview != null) return targets.Preview;

        return Write.Run("modify_dimension", ctx =>
        {
            var wrong = targets.Ids.Where(id => ctx.Tr.GetObject(id, OpenMode.ForRead) is not Dimension)
                                   .Select(id => id.Handle.ToString()).ToList();
            if (wrong.Count > 0)
                throw new BridgeException("bad_request",
                    $"{wrong.Count} of {targets.Ids.Count} objects are not dimensions (e.g. {string.Join(", ", wrong.Take(10))}); nothing was changed");
            if (a["text_position"] != null && targets.Ids.Count > 1)
                throw new BridgeException("bad_request", "'text_position' can be set for one dimension at a time");

            ObjectId? style = Args.Str(a, "style") is string sn ? Style(ctx, sn) : null;
            var items = new JsonArray();
            foreach (var id in targets.Ids)
            {
                var d = (Dimension)ctx.Tr.GetObject(id, OpenMode.ForWrite);
                if (style is ObjectId s) d.DimensionStyle = s;
                if (Args.Bool(a, "reset_overrides") || style != null)
                {
                    // Style values become the object's values again (removes earlier overrides' effect).
                    var rec = (DimStyleTableRecord)ctx.Tr.GetObject(d.DimensionStyle, OpenMode.ForRead);
                    d.SetDimstyleData(rec);
                }
                Apply(ctx, d, a);
                d.RecomputeDimensionBlock(true);
                if (items.Count < 50) items.Add(Describe(d, ctx.Tr, withHandle: true));
            }
            return new JsonObject { ["changed"] = targets.Ids.Count, ["dimensions"] = items };
        });
    }

    internal static ObjectId Style(Write.Ctx ctx, string name)
    {
        var dt = (DimStyleTable)ctx.Tr.GetObject(ctx.Db.DimStyleTableId, OpenMode.ForRead);
        if (!dt.Has(name)) throw new BridgeException("not_found", $"dimension style '{name}' does not exist (see list_styles)", 404);
        return dt[name];
    }

    /// <summary>Applies the appearance arguments to a dimension that is already in the database.</summary>
    internal static void Apply(Write.Ctx ctx, Dimension d, JsonObject a)
    {
        if (a["text_override"] != null) d.DimensionText = Args.Str(a, "text_override") ?? "";
        if (Args.Num(a, "dim_scale") is double sc)
        {
            if (sc <= 0) throw new BridgeException("bad_request", "'dim_scale' must be > 0");
            d.Dimscale = sc;
        }
        if (Args.Num(a, "text_height") is double th)
        {
            if (th <= 0) throw new BridgeException("bad_request", "'text_height' must be > 0");
            d.Dimtxt = th;
        }
        if (Args.Num(a, "arrow_size") is double asz)
        {
            if (asz < 0) throw new BridgeException("bad_request", "'arrow_size' must be >= 0");
            d.Dimasz = asz;
        }
        if (a["decimals"] != null) d.Dimdec = Args.Int(a, "decimals", 2, 0, 8);
        if (Args.Str(a, "text_style") is string ts)
        {
            var tt = (TextStyleTable)ctx.Tr.GetObject(ctx.Db.TextStyleTableId, OpenMode.ForRead);
            if (!tt.Has(ts)) throw new BridgeException("not_found", $"text style '{ts}' does not exist (see list_styles)", 404);
            d.TextStyleId = tt[ts];
        }
        if (Args.Str(a, "arrow") is string arrow)
        {
            d.Dimsah = false;
            d.Dimblk = ArrowBlock(ctx, arrow);
        }
        if (Args.Str(a, "fit") is string fit)
        {
            if (!Fits.TryGetValue(fit, out var f))
                throw new BridgeException("bad_request", $"'fit' must be one of {string.Join(", ", Fits.Keys)}");
            d.Dimatfit = f.AtFit;
            d.Dimtix = f.Tix;
        }
        if (Args.Point(a, "text_position") is { } tp)
        {
            // 2 = text moves freely, dimension line stays; 1 = the same with a leader to the dimension line.
            d.Dimtmove = Args.Bool(a, "text_leader") ? 1 : 2;
            d.TextPosition = tp;
        }
    }

    /// <summary>
    /// Arrowhead block by AutoCAD name ("_ARCHTICK", "_OBLIQUE", "_DOT", "_NONE"…; "" or "closed_filled" = default).
    /// Predefined arrow blocks are created on first use by setting DIMBLK; the user's DIMBLK is restored.
    /// </summary>
    private static ObjectId ArrowBlock(Write.Ctx ctx, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Equals("closed_filled", StringComparison.OrdinalIgnoreCase) || name == ".")
            return ObjectId.Null;
        var bt = (BlockTable)ctx.Tr.GetObject(ctx.Db.BlockTableId, OpenMode.ForRead);
        if (bt.Has(name)) return bt[name];
        string withUnderscore = name.StartsWith('_') ? name : "_" + name;
        if (bt.Has(withUnderscore)) return bt[withUnderscore];

        string old = AcApp.GetSystemVariable("DIMBLK") as string ?? "";
        try
        {
            AcApp.SetSystemVariable("DIMBLK", withUnderscore);
            var id = ctx.Db.Dimblk;
            if (id.IsNull) throw new BridgeException("bad_request", $"unknown arrowhead '{name}'");
            return id;
        }
        catch (Exception ex) when (ex is not BridgeException)
        {
            throw new BridgeException("bad_request",
                $"unknown arrowhead '{name}'; use a block name or an AutoCAD arrow: _ARCHTICK, _OBLIQUE, _DOT, _DOTSMALL, " +
                "_OPEN, _OPEN30, _OPEN90, _CLOSED, _CLOSEDBLANK, _SMALL, _ORIGIN, _NONE, _INTEGRAL, _BOXFILLED, _DATUMFILLED, or closed_filled");
        }
        finally
        {
            try { AcApp.SetSystemVariable("DIMBLK", old.Length == 0 ? "." : old); } catch { }
        }
    }

    /// <summary>Effective appearance of a dimension (style values with the object's overrides applied).</summary>
    internal static JsonObject Describe(Dimension d, Transaction tr, bool withHandle = false)
    {
        var o = new JsonObject();
        if (withHandle) o["handle"] = d.Handle.ToString();
        Fill(o, d, tr);
        return o;
    }

    internal static void Fill(JsonObject o, Dimension d, Transaction tr)
    {
        o["measurement"] = Math.Round(d.Measurement, 9);
        o["text_override"] = string.IsNullOrEmpty(d.DimensionText) ? null : d.DimensionText;
        o["dim_style"] = d.DimensionStyleName;
        o["text_height"] = Math.Round(d.Dimtxt, 9);
        o["arrow_size"] = Math.Round(d.Dimasz, 9);
        o["dim_scale"] = Math.Round(d.Dimscale, 9);
        o["decimals"] = d.Dimdec;
        try { o["text_style"] = ((TextStyleTableRecord)tr.GetObject(d.TextStyleId, OpenMode.ForRead)).Name; } catch { }
        o["arrow"] = d.Dimblk.IsNull ? "closed_filled" : ((BlockTableRecord)tr.GetObject(d.Dimblk, OpenMode.ForRead)).Name;
        o["fit"] = d.Dimtix ? "text_inside" : Fits.FirstOrDefault(kv => kv.Value.AtFit == d.Dimatfit && !kv.Value.Tix).Key;
        o["text_position"] = JsonUtil.Point(d.TextPosition);
        o["text_moved"] = !d.UsingDefaultTextPosition;
    }
}
