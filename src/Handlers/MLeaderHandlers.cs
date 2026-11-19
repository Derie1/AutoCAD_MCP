using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AcadMcpBridge.Handlers;

/// <summary>Multileaders (MLEADER) with text content.</summary>
internal static class MLeaderHandlers
{
    /// <summary>
    /// points: [arrow point, …, last point near the text]; extra_leaders: more arrows to the same text,
    /// each a list in the same order. Text sits at text_position or next to the last point.
    /// </summary>
    public static JsonObject CreateMLeader(JsonObject a) => Write.Run("create_mleader", ctx =>
    {
        var leaders = new List<List<Point3d>> { Points(a["points"], "points") };
        if (a["extra_leaders"] is JsonArray extra)
            for (int i = 0; i < extra.Count; i++) leaders.Add(Points(extra[i], $"extra_leaders[{i}]"));
        else if (a["extra_leaders"] != null)
            throw new BridgeException("bad_request", "'extra_leaders' must be a list of point lists");
        string text = Args.Str(a, "text", required: true)!;
        if (text.Length == 0) throw new BridgeException("bad_request", "'text' must not be empty");

        var dict = (DBDictionary)ctx.Tr.GetObject(ctx.Db.MLeaderStyleDictionaryId, OpenMode.ForRead);
        ObjectId styleId = ctx.Db.MLeaderstyle;
        if (Args.Str(a, "style") is string sn)
        {
            if (!dict.Contains(sn)) throw new BridgeException("not_found", $"multileader style '{sn}' does not exist (see set_mleader_style)", 404);
            styleId = dict.GetAt(sn);
        }
        var style = (MLeaderStyle)ctx.Tr.GetObject(styleId, OpenMode.ForRead);
        double height = Args.Num(a, "text_height") ?? style.TextHeight;
        if (height <= 0) throw new BridgeException("bad_request", "'text_height' must be > 0");

        var ml = new MLeader();
        ml.SetDatabaseDefaults(ctx.Db);
        ml.MLeaderStyle = styleId;
        ml.ContentType = ContentType.MTextContent;

        var main = leaders[0];
        var last = main[^1];
        double side = main.Count > 1 && last.X < main[^2].X ? -1 : 1; // text on the side the leader points to
        var givenPos = Args.Point(a, "text_position");
        var textPos = givenPos
                      ?? last + new Vector3d(side * ((style.EnableDogleg ? style.DoglegLength : 0) + style.LandingGap), 0, 0);

        var mt = new MText();
        mt.SetDatabaseDefaults(ctx.Db);
        mt.Contents = text.Replace("\r\n", "\n").Replace("\n", "\\P");
        mt.TextStyleId = style.TextStyleId.IsNull ? ctx.Db.Textstyle : style.TextStyleId;
        mt.TextHeight = height;
        mt.Location = textPos;
        // Text grows away from the leader: anchored at its left edge when the leader comes from the left.
        mt.Attachment = side < 0 ? AttachmentPoint.TopRight : AttachmentPoint.TopLeft;
        ml.MText = mt;
        if (a["text_height"] != null) ml.TextHeight = height;

        int firstLine = -1;
        foreach (var pts in leaders)
        {
            int line = ml.AddLeaderLine(pts[^1]);
            if (firstLine < 0) firstLine = line;
            for (int i = pts.Count - 2; i >= 0; i--) ml.AddFirstVertex(line, pts[i]);
        }
        ml.TextLocation = textPos;
        if (givenPos == null)
        {
            // AutoCAD moves the leader's last point to the text's attachment point (e.g. the middle of the
            // first line); move the text instead, so the leader ends exactly where it was asked to.
            var shift = last - ml.GetLastVertex(firstLine);
            if (shift.Length > 1e-9) ml.TextLocation = textPos + shift;
        }

        Props.ApplyCommon(ctx, ml, a);
        ctx.Add(ctx.CurrentSpace(), ml);
        return EntityProps.Full(ml, ctx.Tr, withXdata: false);
    });

    private static List<Point3d> Points(JsonNode? n, string name)
    {
        if (n is not JsonArray arr || arr.Count < 2)
            throw new BridgeException("bad_request", $"'{name}' must be a list of at least 2 points: the arrow point first, the point near the text last");
        var list = arr.Select((p, i) => Args.ToPoint(p!, $"{name}[{i}]")).ToList();
        for (int i = 1; i < list.Count; i++)
            if (list[i].IsEqualTo(list[i - 1])) throw new BridgeException("bad_request", $"'{name}' has two equal points in a row");
        return list;
    }

    /// <summary>Properties for get_entity_properties.</summary>
    internal static void Fill(JsonObject o, MLeader ml, Transaction tr)
    {
        o["mleader_style"] = ((MLeaderStyle)tr.GetObject(ml.MLeaderStyle, OpenMode.ForRead)).Name;
        if (ml.ContentType == ContentType.MTextContent && ml.MText is MText mt)
        {
            o["text"] = mt.Text;
            o["text_height"] = Math.Round(mt.TextHeight, 9);
            o["text_position"] = JsonUtil.Point(ml.TextLocation);
        }
        else o["content"] = ml.ContentType.ToString();
        var leaders = new JsonArray();
        foreach (int li in ml.GetLeaderIndexes())
            foreach (int line in ml.GetLeaderLineIndexes(li))
            {
                var pts = new JsonArray();
                int n = ml.VerticesCount(line);
                Point3d? prev = null;
                for (int i = 0; i < n; i++)
                {
                    var v = ml.GetVertex(line, i);
                    if (prev is Point3d pp && pp.IsEqualTo(v)) continue; // the landing start repeats the last point
                    pts.Add(JsonUtil.Point(v));
                    prev = v;
                }
                leaders.Add(pts);
            }
        o["leaders"] = leaders;
    }
}
