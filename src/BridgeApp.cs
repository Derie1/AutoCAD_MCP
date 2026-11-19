using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: ExtensionApplication(typeof(AcadMcpBridge.BridgeApp))]
[assembly: CommandClass(typeof(AcadMcpBridge.BridgeCommands))]

namespace AcadMcpBridge;

/// <summary>
/// Plugin entry point. Loading the DLL only prepares the dispatcher; the HTTP
/// bridge starts with the MCPSTART command (never on its own).
/// </summary>
public sealed class BridgeApp : IExtensionApplication
{
    public const string PluginVersion = "0.4.2";

    internal static BridgeServer? Server { get; private set; }

    /// <summary>Host facts captured once on the main thread (health_check runs off it).</summary>
    internal static HostInfo Host { get; private set; } = new("", "", "", "");

    public void Initialize()
    {
        MainThread.Init();
        UndoTracker.Init();
        RibbonUi.Init();
        Host = HostInfo.Capture();
        Log.Info($"Loaded {PluginVersion} ({BuildInfo.Series}) in {Host.Product} {Host.AcadVer}, .NET {Environment.Version}");
    }

    public void Terminate()
    {
        try { Server?.Stop(); } catch { /* AutoCAD is closing */ }
    }

    internal static string Start()
    {
        if (Server is { IsListening: true })
            return $"already listening on {Server.Prefix}";
        var config = BridgeConfig.Load();
        var server = new BridgeServer(config, Routes.All());
        server.Start();
        Server = server;
        return $"listening on {server.Prefix}";
    }

    internal static string Stop()
    {
        if (Server is not { IsListening: true })
            return "not running";
        Server.Stop();
        return "stopped";
    }

    internal static string Status() =>
        Server is { IsListening: true } s
            ? $"listening on {s.Prefix}, {s.RouteCount} routes, {s.RequestCount} requests served"
            : "not running (type MCPSTART)";
}

internal sealed record HostInfo(string Product, string AcadVer, string VerNum, string AppVersion)
{
    public static HostInfo Capture()
    {
        static string Sys(string name)
        {
            try { return Convert.ToString(AcApp.GetSystemVariable(name)) ?? ""; }
            catch { return ""; }
        }
        return new HostInfo(Sys("PRODUCT"), Sys("ACADVER"), Sys("_VERNUM"), AcApp.Version.ToString());
    }
}

internal static class BuildInfo
{
#if ACAD_R26
    public const string Series = "R26";
#else
    public const string Series = "R25";
#endif
}

public sealed class BridgeCommands
{
    [CommandMethod("MCPSTART", CommandFlags.Session)]
    public void McpStart() => Run(BridgeApp.Start);

    [CommandMethod("MCPSTOP", CommandFlags.Session)]
    public void McpStop() => Run(BridgeApp.Stop);

    [CommandMethod("MCPSTATUS", CommandFlags.Session)]
    public void McpStatus() => Run(BridgeApp.Status);

    private static void Run(Func<string> action)
    {
        Editor? ed = AcApp.DocumentManager.MdiActiveDocument?.Editor;
        string message;
        try { message = action(); }
        catch (System.Exception ex)
        {
            message = "error: " + ex.Message;
            Log.Error("command failed", ex);
        }
        ed?.WriteMessage($"\nAutoCAD MCP Bridge {BridgeApp.PluginVersion}: {message}\n");
    }
}
