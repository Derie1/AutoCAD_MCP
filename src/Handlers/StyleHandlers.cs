using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.GraphicsInterface;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadMcpBridge.Handlers;

/// <summary>
/// Annotation styles: text, dimension, table and multileader styles. Each tool creates the style when
/// it does not exist ("create or change") and returns its resulting values. One call = one UNDO step.
/// </summary>
internal static class StyleHandlers
{
    private const double Deg = Math.PI / 180;

    // ---------------------------------------------------------------- text styles

    public static JsonObject SetTextStyle(JsonObject a) => Write.Run("set_text_style", ctx =>
    {
        string name = Args.Str(a, "name", required: true)!;
        var tt = (TextStyleTable)ctx.Tr.GetObject(ctx.Db.TextStyleTableId, OpenMode.ForRead);
        bool created = !tt.Has(name);
        TextStyleTableRecord st;
        if (created)
        {
            if (Args.Str(a, "font") == null) throw new BridgeException("bad_request", $"text style '{name}' is new: 'font' is required");
            CheckName(name);
            st = new TextStyleTableRecord { Name = name };
            tt.UpgradeOpen();
            tt.Add(st);
            ctx.Tr.AddNewlyCreatedDBObject(st, true);
        }
        else st = (TextStyleTableRecord)ctx.Tr.GetObject(tt[name], OpenMode.ForWrite);

        if (Args.Str(a, "font") is string font)
        {
            font = font.Trim();
            if (font.EndsWith(".shx", StringComparison.OrdinalIgnoreCase))
            {
                st.Font = new FontDescriptor("", false, false, 0, 0);
                st.FileName = font;
            }
            else
            {
                // TrueType: by family name ("ISOCPEUR", "Arial"); a .ttf file name is accepted too.
                string family = font.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(font) : font;
                st.FileName = "";
                st.Font = new FontDescriptor(family, Args.Bool(a, "bold"), Args.Bool(a, "italic"), 0, 34);
                st.BigFontFileName = "";
            }
        }
        if (Args.Str(a, "big_font") is string big) st.BigFontFileName = big;
        if (Args.Num(a, "height") is double h)
        {
            if (h < 0) throw new BridgeException("bad_request", "'height' must be >= 0 (0 = ask on each text)");
            st.TextSize = h;
        }
        if (Args.Num(a, "width_factor") is double w)
        {
            if (w <= 0) throw new BridgeException("bad_request", "'width_factor' must be > 0");
            st.XScale = w;
        }
        if (Args.Num(a, "oblique_deg") is double ob)
        {
            if (Math.Abs(ob) > 85) throw new BridgeException("bad_request", "'oblique_deg' must be from -85 to 85");
            st.ObliquingAngle = ob * Deg;
        }
        if (Args.Bool(a, "current")) ctx.Db.Textstyle = st.ObjectId;

        var o = DescribeText(ctx, st);
        o["created"] = created;
        return o;
    });

    private static JsonObject DescribeText(Write.Ctx ctx, TextStyleTableRecord st) => new()
    {
        ["name"] = st.Name,
        ["font"] = !string.IsNullOrEmpty(st.Font.TypeFace) ? st.Font.TypeFace : st.FileName,
        ["font_file"] = string.IsNullOrEmpty(st.FileName) ? null : st.FileName,
        ["truetype"] = !string.IsNullOrEmpty(st.Font.TypeFace) || !st.FileName.EndsWith(".shx", StringComparison.OrdinalIgnoreCase),
        ["big_font"] = string.IsNullOrEmpty(st.BigFontFileName) ? null : st.BigFontFileName,
        ["height"] = Math.Round(st.TextSize, 9),
        ["width_factor"] = Math.Round(st.XScale, 9),
        ["oblique_deg"] = Math.Round(st.ObliquingAngle / Deg, 6),
        ["current"] = st.ObjectId == ctx.Db.Textstyle,
    };

    // ---------------------------------------------------------------- dimension styles

    public static JsonObject SetDimStyle(JsonObject a) => Write.Run("set_dim_style", ctx =>
    {
        string name = Args.Str(a, "name", required: true)!;
        var dt = (DimStyleTable)ctx.Tr.GetObject(ctx.Db.DimStyleTableId, OpenMode.ForRead);
        bool created = !dt.Has(name);
        DimStyleTableRecord ds;
        if (created)
        {
            CheckName(name);
            ds = new DimStyleTableRecord();
            if (Args.Str(a, "based_on") is string baseName)
            {
                if (!dt.Has(baseName)) throw new BridgeException("not_found", $"dimension style '{baseName}' does not exist", 404);
                ds.CopyFrom((DimStyleTableRecord)ctx.Tr.GetObject(dt[baseName], OpenMode.ForRead));
            }
            else
            {
                using var current = ctx.Db.GetDimstyleData(); // the current style with its overrides
                ds.CopyFrom(current);
            }
            ds.Name = name;
            dt.UpgradeOpen();
            dt.Add(ds);
            ctx.Tr.AddNewlyCreatedDBObject(ds, true);
        }
        else
        {
            if (a["based_on"] != null) throw new BridgeException("bad_request", "'based_on' only applies when the style is created");
            ds = (DimStyleTableRecord)ctx.Tr.GetObject(dt[name], OpenMode.ForWrite);
        }

        if (Args.Str(a, "text_style") is string ts) ds.Dimtxsty = TextStyleId(ctx, ts);
        if (Positive(a, "text_height") is double th) ds.Dimtxt = th;
        if (NonNegative(a, "arrow_size") is double asz) ds.Dimasz = asz;
        if (Args.Str(a, "arrow") is string arrow) { ds.Dimsah = false; ds.Dimblk = DimensionHandlers.ArrowBlock(ctx, arrow); }
        if (NonNegative(a, "ext_offset") is double exo) ds.Dimexo = exo;
        if (NonNegative(a, "ext_extend") is double exe) ds.Dimexe = exe;
        if (Args.Num(a, "text_gap") is double gap) ds.Dimgap = gap;
        if (a["decimals"] != null) ds.Dimdec = Args.Int(a, "decimals", 2, 0, 8);
        if (Positive(a, "dim_scale") is double sc) ds.Dimscale = sc;
        if (Args.Str(a, "fit") is string fit)
        {
            var f = DimensionHandlers.Fit(fit);
            ds.Dimatfit = f.AtFit;
            ds.Dimtix = f.Tix;
        }
        if (Args.Str(a, "text_vertical") is string tv)
        {
            ds.Dimtad = tv.ToLowerInvariant() switch
            {
                "centered" => 0,
                "above" => 1,
                "outside" => 2,
                "jis" => 3,
                "below" => 4,
                _ => throw new BridgeException("bad_request", "'text_vertical' must be centered, above, outside, jis or below"),
            };
        }
        if (Args.Bool(a, "current"))
        {
            ctx.Db.Dimstyle = ds.ObjectId;
            ctx.Db.SetDimstyleData(ds); // also clears the current DIM* overrides
        }

        var o = DescribeDim(ctx, ds);
        o["created"] = created;
        return o;
    });

    private static JsonObject DescribeDim(Write.Ctx ctx, DimStyleTableRecord ds) => new()
    {
        ["name"] = ds.Name,
        ["text_style"] = ((TextStyleTableRecord)ctx.Tr.GetObject(ds.Dimtxsty, OpenMode.ForRead)).Name,
        ["text_height"] = Math.Round(ds.Dimtxt, 9),
        ["arrow"] = ds.Dimblk.IsNull ? "closed_filled" : ((BlockTableRecord)ctx.Tr.GetObject(ds.Dimblk, OpenMode.ForRead)).Name,
        ["arrow_size"] = Math.Round(ds.Dimasz, 9),
        ["ext_offset"] = Math.Round(ds.Dimexo, 9),
        ["ext_extend"] = Math.Round(ds.Dimexe, 9),
        ["text_gap"] = Math.Round(ds.Dimgap, 9),
        ["decimals"] = ds.Dimdec,
        ["dim_scale"] = Math.Round(ds.Dimscale, 9),
        ["fit"] = DimensionHandlers.FitName(ds.Dimatfit, ds.Dimtix),
        ["text_vertical"] = ds.Dimtad switch { 0 => "centered", 1 => "above", 2 => "outside", 3 => "jis", 4 => "below", _ => ds.Dimtad.ToString() },
        ["current"] = ds.ObjectId == ctx.Db.Dimstyle,
    };

    // ---------------------------------------------------------------- table styles

    private static readonly (string Key, RowType Row, string CellStyle)[] TableRows =
        [("title", RowType.TitleRow, "_TITLE"), ("header", RowType.HeaderRow, "_HEADER"), ("data", RowType.DataRow, "_DATA")];

    public static JsonObject SetTableStyle(JsonObject a) => Write.Run("set_table_style", ctx =>
    {
        string name = Args.Str(a, "name", required: true)!;
        var dict = (DBDictionary)ctx.Tr.GetObject(ctx.Db.TableStyleDictionaryId, OpenMode.ForRead);
        bool created = !dict.Contains(name);
        TableStyle ts;
        if (created)
        {
            CheckName(name);
            ts = new TableStyle();
            string baseName = Args.Str(a, "based_on") ?? "Standard";
            if (dict.Contains(baseName))
                ts.CopyFrom((TableStyle)ctx.Tr.GetObject(dict.GetAt(baseName), OpenMode.ForRead));
            else if (a["based_on"] != null)
                throw new BridgeException("not_found", $"table style '{baseName}' does not exist", 404);
            var id = ts.PostTableStyleToDatabase(ctx.Db, name);
            ctx.Tr.AddNewlyCreatedDBObject(ts, true);
            ts = (TableStyle)ctx.Tr.GetObject(id, OpenMode.ForWrite);
        }
        else
        {
            if (a["based_on"] != null) throw new BridgeException("bad_request", "'based_on' only applies when the style is created");
            ts = (TableStyle)ctx.Tr.GetObject(dict.GetAt(name), OpenMode.ForWrite);
        }

        if (Args.Str(a, "text_style") is string tsn)
        {
            var id = TextStyleId(ctx, tsn);
            foreach (var r in TableRows) ts.SetTextStyle(id, (int)r.Row);
        }
        // text_height: one number for all rows, or {title, header, data}.
        switch (a["text_height"])
        {
            case null: break;
            case JsonObject per:
                foreach (var kv in per)
                {
                    var row = TableRows.FirstOrDefault(r => r.Key.Equals(kv.Key, StringComparison.OrdinalIgnoreCase));
                    if (row.Key == null) throw new BridgeException("bad_request", "'text_height' keys must be title, header, data");
                    ts.SetTextHeight(PositiveValue(kv.Value, "text_height." + kv.Key), (int)row.Row);
                }
                break;
            default:
                double all = PositiveValue(a["text_height"], "text_height");
                foreach (var r in TableRows) ts.SetTextHeight(all, (int)r.Row);
                break;
        }
        if (NonNegative(a, "horizontal_margin") is double hm) ts.HorizontalCellMargin = hm;
        if (NonNegative(a, "vertical_margin") is double vm) ts.VerticalCellMargin = vm;
        if (Args.Bool(a, "current")) ctx.Db.Tablestyle = ts.ObjectId;

        var o = DescribeTable(ctx, ts, name);
        o["created"] = created;
        return o;
    });

    private static JsonObject DescribeTable(Write.Ctx ctx, TableStyle ts, string name)
    {
        var heights = new JsonObject();
        var styles = new JsonObject();
        foreach (var r in TableRows)
        {
            heights[r.Key] = Math.Round(ts.TextHeight(r.Row), 9);
            var id = ts.TextStyle(r.Row);
            styles[r.Key] = id.IsNull ? null : ((TextStyleTableRecord)ctx.Tr.GetObject(id, OpenMode.ForRead)).Name;
        }
        return new JsonObject
        {
            ["name"] = name,
            ["text_style"] = styles,
            ["text_height"] = heights,
            ["horizontal_margin"] = Math.Round(ts.HorizontalCellMargin, 9),
            ["vertical_margin"] = Math.Round(ts.VerticalCellMargin, 9),
            ["current"] = ts.ObjectId == ctx.Db.Tablestyle,
        };
    }

    // ---------------------------------------------------------------- multileader styles

    public static JsonObject SetMLeaderStyle(JsonObject a) => Write.Run("set_mleader_style", ctx =>
    {
        string name = Args.Str(a, "name", required: true)!;
        var dict = (DBDictionary)ctx.Tr.GetObject(ctx.Db.MLeaderStyleDictionaryId, OpenMode.ForRead);
        bool created = !dict.Contains(name);
        MLeaderStyle ms;
        if (created)
        {
            CheckName(name);
            ms = new MLeaderStyle();
            string baseName = Args.Str(a, "based_on") ?? "Standard";
            if (dict.Contains(baseName))
                ms.CopyFrom((MLeaderStyle)ctx.Tr.GetObject(dict.GetAt(baseName), OpenMode.ForRead));
            else if (a["based_on"] != null)
                throw new BridgeException("not_found", $"multileader style '{baseName}' does not exist", 404);
            var id = ms.PostMLeaderStyleToDb(ctx.Db, name);
            ctx.Tr.AddNewlyCreatedDBObject(ms, true);
            ms = (MLeaderStyle)ctx.Tr.GetObject(id, OpenMode.ForWrite);
        }
        else
        {
            if (a["based_on"] != null) throw new BridgeException("bad_request", "'based_on' only applies when the style is created");
            ms = (MLeaderStyle)ctx.Tr.GetObject(dict.GetAt(name), OpenMode.ForWrite);
        }

        if (Args.Str(a, "text_style") is string tsn) ms.TextStyleId = TextStyleId(ctx, tsn);
        if (Positive(a, "text_height") is double th) ms.TextHeight = th;
        if (Args.Str(a, "arrow") is string arrow) ms.ArrowSymbolId = DimensionHandlers.ArrowBlock(ctx, arrow);
        if (NonNegative(a, "arrow_size") is double asz) ms.ArrowSize = asz;
        if (NonNegative(a, "landing_gap") is double lg) ms.LandingGap = lg;
        if (NonNegative(a, "dogleg_length") is double dl)
        {
            ms.EnableDogleg = dl > 0;
            if (dl > 0) ms.DoglegLength = dl;
        }
        if (Args.Bool(a, "current")) ctx.Db.MLeaderstyle = ms.ObjectId;

        var o = DescribeMLeader(ctx, ms, name);
        o["created"] = created;
        return o;
    });

    internal static JsonObject DescribeMLeader(Write.Ctx ctx, MLeaderStyle ms, string name) => new()
    {
        ["name"] = name,
        ["text_style"] = ms.TextStyleId.IsNull ? null : ((TextStyleTableRecord)ctx.Tr.GetObject(ms.TextStyleId, OpenMode.ForRead)).Name,
        ["text_height"] = Math.Round(ms.TextHeight, 9),
        ["arrow"] = ms.ArrowSymbolId.IsNull ? "closed_filled" : ((BlockTableRecord)ctx.Tr.GetObject(ms.ArrowSymbolId, OpenMode.ForRead)).Name,
        ["arrow_size"] = Math.Round(ms.ArrowSize, 9),
        ["landing_gap"] = Math.Round(ms.LandingGap, 9),
        ["dogleg_length"] = ms.EnableDogleg ? Math.Round(ms.DoglegLength, 9) : 0,
        ["current"] = ms.ObjectId == ctx.Db.MLeaderstyle,
    };

    // ---------------------------------------------------------------- helpers

    internal static ObjectId TextStyleId(Write.Ctx ctx, string name)
    {
        var tt = (TextStyleTable)ctx.Tr.GetObject(ctx.Db.TextStyleTableId, OpenMode.ForRead);
        if (!tt.Has(name)) throw new BridgeException("not_found", $"text style '{name}' does not exist (create it with set_text_style)", 404);
        return tt[name];
    }

    private static void CheckName(string name)
    {
        try { SymbolUtilityServices.ValidateSymbolName(name, false); }
        catch { throw new BridgeException("bad_request", $"'{name}' is not a valid style name"); }
    }

    private static double? Positive(JsonObject a, string key) =>
        Args.Num(a, key) is double v ? (v > 0 ? v : throw new BridgeException("bad_request", $"'{key}' must be > 0")) : null;

    private static double? NonNegative(JsonObject a, string key) =>
        Args.Num(a, key) is double v ? (v >= 0 ? v : throw new BridgeException("bad_request", $"'{key}' must be >= 0")) : null;

    private static double PositiveValue(JsonNode? n, string key) =>
        n is JsonValue v && v.TryGetValue(out double d) && d > 0 ? d : throw new BridgeException("bad_request", $"'{key}' must be a number > 0");
}
