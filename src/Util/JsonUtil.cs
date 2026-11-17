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

    /// <summary>The active document or a "no_document" error.</summary>
    public static Document ActiveDocument() =>
        AcApp.DocumentManager.MdiActiveDocument
        ?? throw new BridgeException("no_document", "no drawing is open in AutoCAD");
}
