using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;

namespace AcadMcpBridge;

/// <summary>Parsing of property values shared by entity and layer tools.</summary>
internal static class Props
{
    /// <summary>"ByLayer", "ByBlock", "Default" or millimetres (0.25, 0.5 …) from AutoCAD's fixed list.</summary>
    public static LineWeight LineWeight(JsonNode n, string name, bool allowByLayer = true)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue(out string? s))
            {
                if (allowByLayer && s.Equals("ByLayer", StringComparison.OrdinalIgnoreCase)) return Autodesk.AutoCAD.DatabaseServices.LineWeight.ByLayer;
                if (allowByLayer && s.Equals("ByBlock", StringComparison.OrdinalIgnoreCase)) return Autodesk.AutoCAD.DatabaseServices.LineWeight.ByBlock;
                if (s.Equals("Default", StringComparison.OrdinalIgnoreCase)) return Autodesk.AutoCAD.DatabaseServices.LineWeight.ByLineWeightDefault;
            }
            if (v.TryGetValue(out double mm))
            {
                int hundredths = (int)Math.Round(mm * 100);
                if (Enum.IsDefined(typeof(LineWeight), hundredths) && hundredths >= 0) return (LineWeight)hundredths;
            }
        }
        throw new BridgeException("bad_request",
            $"'{name}' must be {(allowByLayer ? "\"ByLayer\", \"ByBlock\", " : "")}\"Default\" or one of the AutoCAD lineweights in mm: " +
            "0, 0.05, 0.09, 0.13, 0.15, 0.18, 0.2, 0.25, 0.3, 0.35, 0.4, 0.5, 0.53, 0.6, 0.7, 0.8, 0.9, 1, 1.06, 1.2, 1.4, 1.58, 2, 2.11");
    }

    /// <summary>Linetype id by name; loads it from acadiso.lin / acad.lin when the drawing does not have it yet.</summary>
    public static ObjectId Linetype(Database db, Transaction tr, string name)
    {
        if (name.Equals("ByLayer", StringComparison.OrdinalIgnoreCase)) return db.ByLayerLinetype;
        if (name.Equals("ByBlock", StringComparison.OrdinalIgnoreCase)) return db.ByBlockLinetype;
        var lt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
        if (!lt.Has(name))
        {
            string file = db.Measurement == MeasurementValue.Metric ? "acadiso.lin" : "acad.lin";
            try { db.LoadLineTypeFile(name, file); } catch { /* reported below */ }
            lt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
            if (!lt.Has(name))
                throw new BridgeException("not_found", $"linetype '{name}' is neither in the drawing nor in {file}", 404);
        }
        return lt[name];
    }

    /// <summary>"ByLayer", "ByBlock" or percent 0-90.</summary>
    public static Autodesk.AutoCAD.Colors.Transparency Transparency(JsonNode n, string name)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue(out string? s))
            {
                if (s.Equals("ByLayer", StringComparison.OrdinalIgnoreCase))
                    return new Autodesk.AutoCAD.Colors.Transparency(Autodesk.AutoCAD.Colors.TransparencyMethod.ByLayer);
                if (s.Equals("ByBlock", StringComparison.OrdinalIgnoreCase))
                    return new Autodesk.AutoCAD.Colors.Transparency(Autodesk.AutoCAD.Colors.TransparencyMethod.ByBlock);
            }
            if (v.TryGetValue(out double p) && p is >= 0 and <= 90)
                return new Autodesk.AutoCAD.Colors.Transparency((byte)Math.Round(255 * (100 - p) / 100));
        }
        throw new BridgeException("bad_request", $"'{name}' must be \"ByLayer\", \"ByBlock\" or a percentage 0-90");
    }
}
