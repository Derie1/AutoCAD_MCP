using System.Globalization;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.Colors;
using Color = Autodesk.AutoCAD.Colors.Color;
using Autodesk.AutoCAD.DatabaseServices;

namespace AcadMcpBridge;

/// <summary>
/// Entities are addressed by handle (hex string, stable across sessions), never by ObjectId.
/// Summary() is the common "passport" every tool returns for an entity.
/// </summary>
internal static class Entities
{
    public static ObjectId Resolve(Database db, string handle)
    {
        if (!long.TryParse(handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long value))
            throw new BridgeException("bad_request", $"'{handle}' is not a handle (hex string such as \"2A3F\")");
        if (!db.TryGetObjectId(new Handle(value), out ObjectId id) || id.IsNull || id.IsErased || !id.IsValid)
            throw new BridgeException("not_found", $"no object with handle {handle} in this drawing", 404);
        return id;
    }

    public static Entity OpenEntity(Transaction tr, Database db, string handle, OpenMode mode)
    {
        var id = Resolve(db, handle);
        return tr.GetObject(id, mode) as Entity
               ?? throw new BridgeException("not_found", $"handle {handle} is not a drawing entity", 404);
    }

    /// <summary>DXF type name ("LINE", "INSERT", "ACAD_TABLE"…), read without opening the object.</summary>
    public static string DxfName(ObjectId id) => id.ObjectClass.DxfName;

    public static JsonObject Summary(Entity e, Transaction tr)
    {
        var o = new JsonObject
        {
            ["handle"] = e.Handle.ToString(),
            ["type"] = e.GetRXClass().DxfName,
            ["layer"] = e.Layer,
            ["color"] = ColorToJson(e.Color),
            ["linetype"] = e.Linetype,
            ["lineweight"] = e.LineWeight.ToString(),
            ["space"] = SpaceName(e.OwnerId, tr),
        };
        if (e is BlockReference br) o["block"] = EffectiveBlockName(br, tr);
        var bounds = Bounds(e);
        if (bounds != null) o["bounds"] = bounds;
        return o;
    }

    public static JsonObject? Bounds(Entity e)
    {
        try
        {
            var ext = e.GeometricExtents;
            return new JsonObject { ["min"] = JsonUtil.Point(ext.MinPoint), ["max"] = JsonUtil.Point(ext.MaxPoint) };
        }
        catch { return null; } // entities without extents (empty text, rays…)
    }

    /// <summary>"Model", a layout name for paper space, or "block:NAME" for entities inside a block.</summary>
    public static string SpaceName(ObjectId ownerId, Transaction tr)
    {
        if (tr.GetObject(ownerId, OpenMode.ForRead) is not BlockTableRecord btr) return "";
        if (!btr.IsLayout) return "block:" + btr.Name;
        return ((Layout)tr.GetObject(btr.LayoutId, OpenMode.ForRead)).LayoutName;
    }

    /// <summary>Block name as the user sees it (dynamic blocks report their definition, not *U123).</summary>
    public static string EffectiveBlockName(BlockReference br, Transaction tr)
    {
        var id = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
        return ((BlockTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name;
    }

    /// <summary>"ByLayer", "ByBlock", an ACI number (1-255) or "#RRGGBB".</summary>
    public static JsonNode? ColorToJson(Color c) => c.ColorMethod switch
    {
        ColorMethod.ByLayer => "ByLayer",
        ColorMethod.ByBlock => "ByBlock",
        ColorMethod.ByAci => (int)c.ColorIndex,
        ColorMethod.ByColor or ColorMethod.ByPen => $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}",
        _ => c.ToString(),
    };

    public static Color ColorFromJson(JsonNode n, string name)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue(out double d) && d == Math.Floor(d) && d is >= 0 and <= 256)
                return Color.FromColorIndex(ColorMethod.ByAci, (short)d);
            if (v.TryGetValue(out string? s))
            {
                if (s.Equals("ByLayer", StringComparison.OrdinalIgnoreCase)) return Color.FromColorIndex(ColorMethod.ByLayer, 256);
                if (s.Equals("ByBlock", StringComparison.OrdinalIgnoreCase)) return Color.FromColorIndex(ColorMethod.ByBlock, 0);
                if (s.Length == 7 && s[0] == '#' && int.TryParse(s.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
                    return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            }
        }
        throw new BridgeException("bad_request", $"'{name}' must be \"ByLayer\", \"ByBlock\", an ACI number 1-255 or \"#RRGGBB\"");
    }
}
