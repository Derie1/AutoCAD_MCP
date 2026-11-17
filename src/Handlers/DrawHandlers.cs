using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;

namespace AcadMcpBridge.Handlers;

/// <summary>Tools that create entities. All go through Write.Run (one UNDO step per call).</summary>
internal static class DrawHandlers
{
    /// <summary>Applies the optional common properties: layer, color.</summary>
    private static void Common(Write.Ctx ctx, Entity e, JsonObject a)
    {
        string? layer = Args.Str(a, "layer");
        if (layer != null) e.Layer = ctx.Layer(layer);
        if (a["color"] is JsonNode c) e.Color = Entities.ColorFromJson(c, "color");
    }

    public static JsonObject CreateLine(JsonObject a) => Write.Run("create_line", ctx =>
    {
        var start = Args.Point(a, "start", required: true)!.Value;
        var end = Args.Point(a, "end", required: true)!.Value;
        if (start.IsEqualTo(end))
            throw new BridgeException("bad_request", "'start' and 'end' are the same point");
        var line = new Line(start, end);
        line.SetDatabaseDefaults(ctx.Db);
        Common(ctx, line, a);
        ctx.Add(ctx.CurrentSpace(), line);
        var result = Entities.Summary(line, ctx.Tr);
        result["length"] = Math.Round(line.Length, 9);
        return result;
    });
}
