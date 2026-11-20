using AcadMcpBridge.Handlers;
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
    public const string PluginVersion = "0.7.0";

    internal static BridgeServer? Server { get; private set; }

    /// <summary>Host facts captured once on the main thread (health_check runs off it).</summary>
    internal static HostInfo Host { get; private set; } = new("", "", "", "");

    public void Initialize()
    {
        Host = HostInfo.Capture();
        UndoTracker.Init();
        CommandAccess.Init();
        if (Batch.IsConsole)
        {
            // AcCoreConsole (batch mode): no HTTP, ribbon or dialogs. The crash recovery belongs to the
            // AutoCAD window: run here, it could reset what the running AutoCAD is in the middle of.
            Log.Info($"Loaded {PluginVersion} ({BuildInfo.Series}) in AcCoreConsole {Host.AcadVer}, pid {Environment.ProcessId}");
            return;
        }
        MainThread.Init();
        RibbonUi.Init();
        SysVars.RecoverAfterCrash();
        CommandLog.RecoverAfterCrash();
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
        (Server is { IsListening: true } s
            ? $"listening on {s.Prefix}, {s.RouteCount} routes, {s.RequestCount} requests served"
            : "not running (type MCPSTART)")
        + $"; commands and AutoLISP: {CommandAccess.Describe()}";
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

    /// <summary>Allows (after a warning) or turns off run_command / eval_lisp for this session.</summary>
    [CommandMethod("MCPCOMMANDS", CommandFlags.Session)]
    public void McpCommands() => Run(() =>
    {
        CommandAccess.Toggle();
        return CommandAccess.Allowed
            ? "commands and AutoLISP are allowed for this session (MCPCOMMANDS again turns them off)"
            : "commands and AutoLISP are off";
    });

    /// <summary>
    /// Batch mode only (AcCoreConsole): runs a job file on the open drawing, see <see cref="Batch"/>.
    /// A document command (not Session), so that steps such as audit can run AutoCAD commands.
    /// </summary>
    [CommandMethod("MCPBATCH")]
    public async void McpBatch()
    {
        var ed = AcApp.DocumentManager.MdiActiveDocument?.Editor;
        string message;
        try
        {
            if (!Batch.IsConsole) message = "MCPBATCH runs only in AcCoreConsole (batch processing)";
            else
            {
                string? job = Environment.GetEnvironmentVariable(Batch.JobEnvVar);
                if (string.IsNullOrWhiteSpace(job) && ed != null)
                {
                    var r = ed.GetString(new PromptStringOptions("\nJob file: ") { AllowSpaces = true });
                    job = r.Status == PromptStatus.OK ? r.StringResult : null;
                }
                message = string.IsNullOrWhiteSpace(job) ? "no job file" : await Batch.Run(job.Trim().Trim('"'));
            }
        }
        catch (System.Exception ex)
        {
            message = "error: " + ex.Message;
            Log.Error("MCPBATCH failed", ex);
        }
        ed?.WriteMessage($"\nAutoCAD MCP Bridge {BridgeApp.PluginVersion}: {message}\n");
    }

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
