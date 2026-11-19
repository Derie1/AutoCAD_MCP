using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AcadMcpBridge;

/// <summary>Type-specific properties for get_entity_properties, on top of Entities.Summary.</summary>
internal static class EntityProps
{
    private static double Deg(double rad) => Math.Round(rad * 180 / Math.PI, 9);
    private static double R(double v) => Math.Round(v, 9);
    private static JsonArray P2(Point2d p) => new(R(p.X), R(p.Y));

    public static JsonObject Full(Entity e, Transaction tr, bool withXdata)
    {
        var o = Entities.Summary(e, tr);
        o["visible"] = e.Visible;
        o["transparency"] = e.Transparency.IsByLayer ? "ByLayer"
                          : e.Transparency.IsByBlock ? "ByBlock"
                          : (JsonNode)e.Transparency.Alpha;

        switch (e)
        {
            case Line l:
                o["start"] = JsonUtil.Point(l.StartPoint);
                o["end"] = JsonUtil.Point(l.EndPoint);
                o["length"] = R(l.Length);
                o["angle_deg"] = Deg(l.Angle);
                break;
            case Circle c:
                o["center"] = JsonUtil.Point(c.Center);
                o["radius"] = R(c.Radius);
                o["area"] = R(c.Area);
                o["circumference"] = R(c.Circumference);
                break;
            case Arc a:
                o["center"] = JsonUtil.Point(a.Center);
                o["radius"] = R(a.Radius);
                o["start_angle_deg"] = Deg(a.StartAngle);
                o["end_angle_deg"] = Deg(a.EndAngle);
                o["length"] = R(a.Length);
                break;
            case Polyline pl:
                var verts = new JsonArray();
                for (int i = 0; i < pl.NumberOfVertices; i++)
                {
                    var v = new JsonObject { ["point"] = P2(pl.GetPoint2dAt(i)) };
                    double bulge = pl.GetBulgeAt(i);
                    if (bulge != 0) v["bulge"] = R(bulge);
                    double sw = pl.GetStartWidthAt(i), ew = pl.GetEndWidthAt(i);
                    if (sw != 0 || ew != 0) { v["start_width"] = R(sw); v["end_width"] = R(ew); }
                    verts.Add(v);
                }
                o["closed"] = pl.Closed;
                o["vertices"] = verts;
                o["length"] = R(pl.Length);
                if (pl.Closed) o["area"] = R(pl.Area);
                o["elevation"] = R(pl.Elevation);
                break;
            case Polyline2d or Polyline3d:
                var pts = new JsonArray();
                foreach (ObjectId vid in (System.Collections.IEnumerable)e)
                {
                    var vx = tr.GetObject(vid, OpenMode.ForRead);
                    if (vx is Vertex2d v2) pts.Add(JsonUtil.Point(v2.Position));
                    else if (vx is PolylineVertex3d v3) pts.Add(JsonUtil.Point(v3.Position));
                }
                o["closed"] = e is Polyline2d p2 ? p2.Closed : ((Polyline3d)e).Closed;
                o["vertices"] = pts;
                break;
            case DBText t:
                o["text"] = t.TextString;
                o["position"] = JsonUtil.Point(t.Position);
                o["height"] = R(t.Height);
                o["rotation_deg"] = Deg(t.Rotation);
                o["style"] = StyleName(t.TextStyleId, tr);
                o["justify"] = t.Justify.ToString();
                break;
            case MText m:
                o["text"] = m.Text;          // plain text
                o["contents"] = m.Contents;  // with formatting codes
                o["position"] = JsonUtil.Point(m.Location);
                o["height"] = R(m.TextHeight);
                o["width"] = R(m.Width);
                o["rotation_deg"] = Deg(m.Rotation);
                o["style"] = StyleName(m.TextStyleId, tr);
                o["attachment"] = m.Attachment.ToString();
                break;
            case BlockReference br:
                o["position"] = JsonUtil.Point(br.Position);
                o["rotation_deg"] = Deg(br.Rotation);
                o["scale"] = new JsonArray(R(br.ScaleFactors.X), R(br.ScaleFactors.Y), R(br.ScaleFactors.Z));
                o["attributes"] = Attributes(br, tr);
                if (br.IsDynamicBlock) o["dynamic_properties"] = DynamicProps(br);
                break;
            case Dimension d:
                AcadMcpBridge.Handlers.DimensionHandlers.Fill(o, d, tr);
                break;
            case Hatch h:
                o["pattern"] = h.PatternName;
                o["solid"] = h.IsSolidFill;
                try { o["area"] = R(h.Area); } catch { }
                break;
        }

        if (e is Table table)
        {
            o["rows"] = table.Rows.Count;
            o["columns"] = table.Columns.Count;
            o["position"] = JsonUtil.Point(table.Position);
            o["table_style"] = ((DBObject)tr.GetObject(table.TableStyle, OpenMode.ForRead)) is TableStyle ts ? ts.Name : null;
        }

        if (withXdata && e.XData != null)
        {
            var x = new JsonArray();
            foreach (TypedValue tv in e.XData.AsArray())
                x.Add(new JsonArray(tv.TypeCode, JsonUtil.FromObject(tv.Value)));
            o["xdata"] = x;
        }
        return o;
    }

    public static JsonArray Attributes(BlockReference br, Transaction tr)
    {
        var list = new JsonArray();
        foreach (ObjectId id in br.AttributeCollection)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not AttributeReference ar) continue;
            list.Add(new JsonObject
            {
                ["tag"] = ar.Tag,
                ["value"] = ar.IsMTextAttribute ? ar.MTextAttribute.Text : ar.TextString,
                ["invisible"] = ar.Invisible,
                ["handle"] = ar.Handle.ToString(),
            });
        }
        return list;
    }

    private static JsonArray DynamicProps(BlockReference br)
    {
        var list = new JsonArray();
        foreach (DynamicBlockReferenceProperty p in br.DynamicBlockReferencePropertyCollection)
        {
            if (p.PropertyName == "Origin") continue;
            var o = new JsonObject
            {
                ["name"] = p.PropertyName,
                ["value"] = JsonUtil.FromObject(p.Value),
                ["read_only"] = p.ReadOnly,
                ["units"] = p.UnitsType.ToString(),
            };
            var allowed = p.GetAllowedValues();
            if (allowed is { Length: > 0 }) o["allowed"] = new JsonArray(allowed.Select(JsonUtil.FromObject).ToArray());
            list.Add(o);
        }
        return list;
    }

    private static string? StyleName(ObjectId id, Transaction tr) =>
        id.IsNull ? null : ((TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name;
}
