using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AcadMcpBridge.Handlers;

/// <summary>
/// Tools that create entities in the current space (model space or the active layout).
/// All go through Write.Run (one UNDO step per call) and return the new entity's properties.
/// </summary>
internal static class DrawHandlers
{
    private const double Deg = Math.PI / 180;

    /// <summary>Common properties: layer, color, linetype, lineweight (ByLayer unless given).</summary>
    private static void Common(Write.Ctx ctx, Entity e, JsonObject a) => Props.ApplyCommon(ctx, e, a);

    /// <summary>Defaults, common properties, append, describe.</summary>
    private static JsonObject Add(Write.Ctx ctx, Entity e, JsonObject a)
    {
        e.SetDatabaseDefaults(ctx.Db);
        Common(ctx, e, a);
        ctx.Add(ctx.CurrentSpace(), e);
        return EntityProps.Full(e, ctx.Tr, withXdata: false);
    }

    private static double Positive(JsonObject a, string name)
    {
        double v = Args.Num(a, name, required: true)!.Value;
        if (v <= 0) throw new BridgeException("bad_request", $"'{name}' must be > 0");
        return v;
    }

    // ---------------------------------------------------------------- geometry

    public static JsonObject CreateLine(JsonObject a) => Write.Run("create_line", ctx =>
    {
        var start = Args.Point(a, "start", required: true)!.Value;
        var end = Args.Point(a, "end", required: true)!.Value;
        if (start.IsEqualTo(end))
            throw new BridgeException("bad_request", "'start' and 'end' are the same point");
        return Add(ctx, new Line(start, end), a);
    });

    /// <summary>2D polyline: points [[x, y], …], optional bulges (one per vertex), closed, constant width.</summary>
    public static JsonObject CreatePolyline(JsonObject a) => Write.Run("create_polyline", ctx =>
    {
        if (a["points"] is not JsonArray pts || pts.Count < 2)
            throw new BridgeException("bad_request", "'points' must be a list of at least 2 points");
        var bulges = a["bulges"] as JsonArray;
        if (bulges != null && bulges.Count != pts.Count)
            throw new BridgeException("bad_request", $"'bulges' must have one value per point ({pts.Count})");
        double width = Args.Num(a, "width") ?? 0;
        if (width < 0) throw new BridgeException("bad_request", "'width' must be >= 0");

        var pl = new Polyline();
        for (int i = 0; i < pts.Count; i++)
        {
            var p = Args.ToPoint(pts[i]!, $"points[{i}]");
            double bulge = bulges?[i]?.GetValue<double>() ?? 0;
            pl.AddVertexAt(i, new Point2d(p.X, p.Y), bulge, width, width);
        }
        pl.Closed = Args.Bool(a, "closed");
        pl.Elevation = Args.Num(a, "elevation") ?? 0;
        return Add(ctx, pl, a);
    });

    public static JsonObject CreateCircle(JsonObject a) => Write.Run("create_circle", ctx =>
        Add(ctx, new Circle(Args.Point(a, "center", required: true)!.Value, Vector3d.ZAxis, Positive(a, "radius")), a));

    /// <summary>Arc by center + radius + angles (degrees, counter-clockwise) or by three points [start, middle, end].</summary>
    public static JsonObject CreateArc(JsonObject a) => Write.Run("create_arc", ctx =>
    {
        Arc arc;
        if (a["points"] is JsonArray three)
        {
            if (three.Count != 3) throw new BridgeException("bad_request", "'points' must be [start, middle, end]");
            var p = three.Select((n, i) => Args.ToPoint(n!, $"points[{i}]")).ToArray();
            // CircularArc3d does not reject collinear points, it returns a meaningless arc.
            var cross = (p[1] - p[0]).CrossProduct(p[2] - p[0]);
            if (cross.Length <= 1e-9 * Math.Max(1, (p[2] - p[0]).Length * (p[1] - p[0]).Length))
                throw new BridgeException("bad_request", "the three points are on one line (or coincide)");
            CircularArc3d ca;
            try { ca = new CircularArc3d(p[0], p[1], p[2]); }
            catch { throw new BridgeException("bad_request", "the three points are on one line"); }
            var c = ca.Center;
            double a0 = Math.Atan2(p[0].Y - c.Y, p[0].X - c.X), a2 = Math.Atan2(p[2].Y - c.Y, p[2].X - c.X);
            // AutoCAD arcs run counter-clockwise; swap ends if the points go clockwise.
            bool ccw = (p[1] - p[0]).CrossProduct(p[2] - p[1]).Z > 0;
            arc = ccw ? new Arc(c, ca.Radius, a0, a2) : new Arc(c, ca.Radius, a2, a0);
        }
        else
        {
            arc = new Arc(Args.Point(a, "center", required: true)!.Value, Positive(a, "radius"),
                          Args.Num(a, "start_angle_deg", required: true)!.Value * Deg,
                          Args.Num(a, "end_angle_deg", required: true)!.Value * Deg);
        }
        return Add(ctx, arc, a);
    });

    // ---------------------------------------------------------------- text

    private static ObjectId TextStyle(Write.Ctx ctx, JsonObject a)
    {
        string? name = Args.Str(a, "style");
        if (name == null) return ctx.Db.Textstyle;
        var st = (TextStyleTable)ctx.Tr.GetObject(ctx.Db.TextStyleTableId, OpenMode.ForRead);
        if (!st.Has(name)) throw new BridgeException("not_found", $"text style '{name}' does not exist (see list_styles)", 404);
        return st[name];
    }

    private static AttachmentPoint Attachment(JsonObject a, string name, AttachmentPoint fallback)
    {
        string? s = Args.Str(a, name);
        if (s == null) return fallback;
        // accept "MiddleCenter", "middle_center", "Middle Center"
        string key = s.Replace("_", "").Replace(" ", "");
        foreach (AttachmentPoint ap in Enum.GetValues<AttachmentPoint>())
            if (ap.ToString().Equals(key, StringComparison.OrdinalIgnoreCase)) return ap;
        throw new BridgeException("bad_request",
            $"'{name}' must be one of TopLeft, TopCenter, TopRight, MiddleLeft, MiddleCenter, MiddleRight, " +
            "BottomLeft, BottomCenter, BottomRight (text also BaseLeft, BaseCenter, BaseRight)");
    }

    /// <summary>Single-line text; justify sets the alignment point (default BaseLeft = insertion point).</summary>
    public static JsonObject CreateText(JsonObject a) => Write.Run("create_text", ctx =>
    {
        string text = Args.Str(a, "text", required: true)!;
        var pos = Args.Point(a, "position", required: true)!.Value;
        var t = new DBText();
        t.SetDatabaseDefaults(ctx.Db);
        t.TextString = text;
        t.TextStyleId = TextStyle(ctx, a);
        t.Height = Args.Num(a, "height") is double h ? (h > 0 ? h : throw new BridgeException("bad_request", "'height' must be > 0"))
                                                     : ctx.Db.Textsize;
        t.Rotation = (Args.Num(a, "rotation_deg") ?? 0) * Deg;
        var just = Attachment(a, "justify", AttachmentPoint.BaseLeft);
        t.Position = pos;
        if (just != AttachmentPoint.BaseLeft)
        {
            t.Justify = just;
            t.AlignmentPoint = pos;
        }
        Common(ctx, t, a);
        ctx.Add(ctx.CurrentSpace(), t);
        if (just != AttachmentPoint.BaseLeft) t.AdjustAlignment(ctx.Db);
        return EntityProps.Full(t, ctx.Tr, withXdata: false);
    });

    /// <summary>Multiline text. "\n" in the text becomes a paragraph break; MTEXT codes are kept.</summary>
    public static JsonObject CreateMText(JsonObject a) => Write.Run("create_mtext", ctx =>
    {
        string text = Args.Str(a, "text", required: true)!.Replace("\r\n", "\n").Replace("\n", "\\P");
        var m = new MText();
        m.SetDatabaseDefaults(ctx.Db);
        m.Location = Args.Point(a, "position", required: true)!.Value;
        m.Contents = text;
        m.TextStyleId = TextStyle(ctx, a);
        m.TextHeight = Args.Num(a, "height") is double h ? (h > 0 ? h : throw new BridgeException("bad_request", "'height' must be > 0"))
                                                         : ctx.Db.Textsize;
        m.Width = Args.Num(a, "width") ?? 0; // 0 = no wrapping
        m.Rotation = (Args.Num(a, "rotation_deg") ?? 0) * Deg;
        m.Attachment = Attachment(a, "attachment", AttachmentPoint.TopLeft);
        Common(ctx, m, a);
        ctx.Add(ctx.CurrentSpace(), m);
        return EntityProps.Full(m, ctx.Tr, withXdata: false);
    });

    // ---------------------------------------------------------------- dimensions

    /// <summary>"aligned" (parallel to p1-p2) or "linear" (horizontal / vertical / at angle_deg).</summary>
    public static JsonObject CreateDimension(JsonObject a) => Write.Run("create_dimension", ctx =>
    {
        string kind = (Args.Str(a, "kind") ?? "aligned").ToLowerInvariant();
        var p1 = Args.Point(a, "p1", required: true)!.Value;
        var p2 = Args.Point(a, "p2", required: true)!.Value;
        var loc = Args.Point(a, "dim_line_point", required: true)!.Value;
        if (p1.IsEqualTo(p2)) throw new BridgeException("bad_request", "'p1' and 'p2' must differ");
        string text = Args.Str(a, "text_override") ?? "";

        ObjectId style = ctx.Db.Dimstyle;
        if (Args.Str(a, "style") is string sn)
        {
            var dt = (DimStyleTable)ctx.Tr.GetObject(ctx.Db.DimStyleTableId, OpenMode.ForRead);
            if (!dt.Has(sn)) throw new BridgeException("not_found", $"dimension style '{sn}' does not exist (see list_styles)", 404);
            style = dt[sn];
        }

        Dimension dim = kind switch
        {
            "aligned" => new AlignedDimension(p1, p2, loc, text, style),
            "linear" => new RotatedDimension((Args.Num(a, "angle_deg") ?? 0) * Deg, p1, p2, loc, text, style),
            _ => throw new BridgeException("bad_request", "'kind' must be \"aligned\" or \"linear\""),
        };
        dim.SetDatabaseDefaults(ctx.Db);
        dim.DimensionStyle = style;
        Common(ctx, dim, a);
        ctx.Add(ctx.CurrentSpace(), dim);
        dim.RecomputeDimensionBlock(true);
        return EntityProps.Full(dim, ctx.Tr, withXdata: false);
    });
}
