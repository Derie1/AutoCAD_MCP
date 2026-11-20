using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Geometry;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadMcpBridge;

internal static class JsonUtil
{
    /// <summary>Point as [x, y, z] in WCS, rounded to 1e-9.</summary>
    public static JsonArray Point(Point3d p) =>
        new(Math.Round(p.X, 9), Math.Round(p.Y, 9), Math.Round(p.Z, 9));

    /// <summary>Any AutoCAD value (sysvar, xdata, dynamic property) as JSON.</summary>
    public static JsonNode? FromObject(object? v) => v switch
    {
        null => null,
        string s => s,
        bool b => b,
        short or int or long or byte => Convert.ToInt64(v),
        double d => Math.Round(d, 12),
        float f => Math.Round((double)f, 9),
        Point3d p => Point(p),
        Point2d p => new JsonArray(Math.Round(p.X, 9), Math.Round(p.Y, 9)),
        Vector3d vec => new JsonArray(Math.Round(vec.X, 9), Math.Round(vec.Y, 9), Math.Round(vec.Z, 9)),
        Autodesk.AutoCAD.DatabaseServices.ObjectId id => id.IsNull ? null : id.Handle.ToString(),
        _ => v.ToString(),
    };

    /// <summary>The active document or a "no_document" error.</summary>
    public static Document ActiveDocument() =>
        AcApp.DocumentManager.MdiActiveDocument
        ?? throw new BridgeException("no_document", "no drawing is open in AutoCAD");

    /// <summary>A command is running in the drawing (in a batch, MCPBATCH itself does not count).</summary>
    public static bool Busy(Document doc) =>
        !string.IsNullOrEmpty(doc.CommandInProgress)
        && !(Batch.IsConsole && doc.CommandInProgress.Equals("MCPBATCH", StringComparison.OrdinalIgnoreCase));
}
