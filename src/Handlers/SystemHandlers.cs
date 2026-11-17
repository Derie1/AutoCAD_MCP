using System.Text.Json.Nodes;

namespace AcadMcpBridge.Handlers;

internal static class SystemHandlers
{
    /// <summary>
    /// Runs on the HTTP thread, never touches the AutoCAD API, so it answers
    /// even while AutoCAD is busy. "product" lets the Python server check that
    /// it reached AutoCAD and not some other program on the port.
    /// </summary>
    public static JsonObject Health(JsonObject _)
    {
        var s = BridgeApp.Server;
        var h = BridgeApp.Host;
        return new JsonObject
        {
            ["product"] = "AutoCAD",
            ["bridge"] = "AcadMcpBridge",
            ["version"] = BridgeApp.PluginVersion,
            ["build_series"] = BuildInfo.Series,
            ["host_product"] = h.Product,
            ["acadver"] = h.AcadVer,
            ["acad_build"] = h.AppVersion,
            ["dotnet"] = Environment.Version.ToString(),
            ["port"] = s?.Port,
            ["routes"] = s?.RouteCount,
            ["ribbon"] = RibbonUi.State,
            ["requests"] = s?.RequestCount,
        };
    }
}
