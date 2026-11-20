using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadMcpBridge;

/// <summary>
/// Batch mode: the plugin inside AcCoreConsole.exe (no window, no HTTP, no ribbon, no COM).
/// The MCP server starts one console per drawing with a script that loads this DLL and runs
/// MCPBATCH. MCPBATCH reads a job file (path in ACADMCP_BATCH_JOB, or typed at the prompt):
///   { "steps": [ { "tool": "list_layers", "args": { ... } }, ... ],
///     "continue_on_error": false,      // default: stop at the first failed step
///     "save_as": "C:\\out\\a.dwg",     // optional: save the drawing here if a step changed it
///                                      //   (may be the drawing's own path; same DWG version as the source)
///     "result": "C:\\jobs\\a.result.json" }  // default: next to the job file
/// and writes { ok, file, steps: [ { tool, ok, result | code + error, ms } ], changed, saved_to, ms }.
/// </summary>
internal static class Batch
{
    public const string JobEnvVar = "ACADMCP_BATCH_JOB";

    /// <summary>True inside AcCoreConsole.exe.</summary>
    public static bool IsConsole { get; } =
        Process.GetCurrentProcess().ProcessName.Equals("accoreconsole", StringComparison.OrdinalIgnoreCase);

    private const string OneFile = "a batch opens, saves and closes each drawing itself";
    private const string NoScreen = "a batch has no screen or view";

    /// <summary>Tools a batch step cannot use, with the reason.</summary>
    public static readonly IReadOnlyDictionary<string, string> NotInBatch =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["health_check"] = "it checks the bridge in the running AutoCAD",
            ["list_documents"] = OneFile,
            ["new_document"] = OneFile,
            ["open_document"] = OneFile,
            ["set_active_document"] = OneFile,
            ["close_document"] = OneFile,
            ["save_document"] = OneFile + " (the batch's save settings decide where it goes)",
            ["save_as"] = OneFile + " (the batch's save settings decide where it goes)",
            ["zoom"] = NoScreen,
            ["capture_view"] = NoScreen,
            ["get_selection"] = NoScreen + " and no selection",
            ["set_selection"] = NoScreen + " and no selection",
            ["undo"] = "a batch has no undo: a drawing changes on disk only when the batch saves it",
            ["run_command"] = "AutoCAD commands are not available in batches yet",
            ["eval_lisp"] = "AutoLISP is not available in batches yet",
        };

    /// <summary>Tools that run AutoCAD commands in a batch (asynchronously, inside MCPBATCH).</summary>
    private static readonly IReadOnlyDictionary<string, Func<JsonObject, Task<JsonObject>>> AsyncInBatch =
        new Dictionary<string, Func<JsonObject, Task<JsonObject>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["audit"] = Handlers.MaintenanceHandlers.AuditInBatch,
        };

    /// <summary>MCPBATCH: runs the job on the drawing the console opened.</summary>
    public static async Task<string> Run(string jobPath)
    {
        var sw = Stopwatch.StartNew();
        string resultPath = Path.ChangeExtension(jobPath, ".result.json");
        var output = new JsonObject();
        try
        {
            if (!File.Exists(jobPath)) throw new BridgeException("not_found", $"job file '{jobPath}' does not exist", 404);
            var job = JsonNode.Parse(File.ReadAllText(jobPath)) as JsonObject
                ?? throw new BridgeException("bad_request", "the job file must hold a JSON object");
            if (Args.Str(job, "result") is { Length: > 0 } r) resultPath = r;
            output = await RunJob(job);
        }
        catch (Exception ex)
        {
            var (code, message, _) = BridgeServer.ErrorOf(ex);
            output["ok"] = false;
            output["code"] = code;
            output["error"] = message;
            Log.Error("batch job failed", ex);
        }
        output["plugin"] = BridgeApp.PluginVersion;
        output["autocad"] = BridgeApp.Host.AcadVer;
        output["ms"] = sw.ElapsedMilliseconds;

        File.WriteAllText(resultPath, output.ToJsonString(Json.Options), new UTF8Encoding(false));
        int failed = (output["steps"] as JsonArray)?.Count(s => s?["ok"]?.GetValue<bool>() == false) ?? 0;
        return $"{((bool?)output["ok"] == true ? "ok" : "FAILED")}, {failed} failed step(s), " +
               $"saved: {output["saved_to"]?.ToString() ?? "no"}; result in {resultPath}";
    }

    private static async Task<JsonObject> RunJob(JsonObject job)
    {
        var doc = JsonUtil.ActiveDocument();
        var routes = Routes.All();
        bool goOn = Args.Bool(job, "continue_on_error");
        var steps = job["steps"] as JsonArray ?? throw new BridgeException("bad_request", "the job needs a 'steps' array");

        int changesBefore = UndoTracker.RealChanges;
        bool auditFixed = false;
        var results = new JsonArray();
        bool allOk = true;
        foreach (var node in steps)
        {
            var step = node as JsonObject ?? throw new BridgeException("bad_request", "each step must be a JSON object");
            string tool = Args.Str(step, "tool") ?? "";
            var args = step["args"] as JsonObject ?? new JsonObject();
            var res = new JsonObject { ["tool"] = tool };
            doc.Editor.WriteMessage($"\nMCPBATCH step {results.Count + 1}: {tool}\n"); // console output shows where a crash happened
            var sw = Stopwatch.StartNew();
            try
            {
                if (NotInBatch.TryGetValue(tool, out string? why))
                    throw new BridgeException("not_in_batch", $"'{tool}' cannot run in a batch: {why}");
                if (!routes.TryGetValue(tool, out var route))
                    throw new BridgeException("unknown_tool", $"no tool '{tool}'", 404);
                // The step gets its own copy: handlers may keep references into the arguments.
                var copy = (JsonObject)args.DeepClone();
                var result = AsyncInBatch.TryGetValue(tool, out var run) ? await run(copy) : route.RunHere(copy);
                res["ok"] = true;
                res["result"] = result;
                if (tool.Equals("audit", StringComparison.OrdinalIgnoreCase) && (int?)result["errors_fixed"] > 0) auditFixed = true;
            }
            catch (Exception ex)
            {
                var (code, message, status) = BridgeServer.ErrorOf(ex);
                if (status >= 500) Log.Error($"batch step {tool} failed", ex);
                res["ok"] = false;
                res["code"] = code;
                res["error"] = message;
                allOk = false;
            }
            res["ms"] = sw.ElapsedMilliseconds;
            results.Add(res);
            if (!allOk && !goOn) break;
        }

        // Not DBMOD: AUDIT without fix sets it although nothing changed.
        bool changed = UndoTracker.RealChanges > changesBefore || auditFixed;
        var o = new JsonObject
        {
            ["file"] = doc.Name,
            ["steps"] = results,
            ["changed"] = changed,
        };

        string? saveAs = Args.Str(job, "save_as");
        if (!string.IsNullOrEmpty(saveAs) && changed && (allOk || goOn))
        {
            try
            {
                var saved = await Save(doc, saveAs);
                foreach (var kv in saved.ToList()) { saved.Remove(kv.Key); o[kv.Key] = kv.Value; }
            }
            catch (Exception ex)
            {
                var (code, message, _) = BridgeServer.ErrorOf(ex);
                Log.Error("batch save failed", ex);
                o["save_error"] = $"{code}: {message}";
                allOk = false;
            }
        }
        else o["saved_to"] = null;
        o["ok"] = allOk;
        return o;
    }

    /// <summary>
    /// Saves the console's drawing in the DWG version it was opened in. Onto its own file only the SAVEAS
    /// command works (Database.SaveAs/Save fail on the open file; QSAVE would upgrade it to 2018);
    /// SAVEAS keeps the previous file as .bak. Another path: Database.SaveAs.
    /// </summary>
    private static async Task<JsonObject> Save(Document doc, string path)
    {
        path = Path.GetFullPath(path);
        string? dir = Path.GetDirectoryName(path);
        if (dir == null || !Directory.Exists(dir))
            throw new BridgeException("not_found", $"folder '{dir}' does not exist", 404);
        var db = doc.Database;
        var o = new JsonObject { ["saved_to"] = path, ["version"] = db.OriginalFileVersion.ToString() };
        if (!string.Equals(LongPath(doc.Name), LongPath(path), StringComparison.OrdinalIgnoreCase))
        {
            db.SaveAs(path, db.OriginalFileVersion);
            return o;
        }

        string format = db.OriginalFileVersion switch
        {
            DwgVersion.AC1032 => "2018", DwgVersion.AC1027 => "2013", DwgVersion.AC1024 => "2010",
            DwgVersion.AC1021 => "2007", DwgVersion.AC1800 => "2004", DwgVersion.AC1015 => "2000",
            DwgVersion.AC1014 => "R14",
            var v => throw new BridgeException("unsupported_version", $"cannot save a drawing of version {v} in place"),
        };
        string bak = Path.ChangeExtension(doc.Name, ".bak");
        var started = DateTime.UtcNow.AddSeconds(-2);
        DateTime? bakBefore = File.Exists(bak) ? File.GetLastWriteTimeUtc(bak) : null;
        object oldBak = AcApp.GetSystemVariable("ISAVEBAK");
        AcApp.SetSystemVariable("ISAVEBAK", 1);
        // The drawing's own name: AutoCAD takes the same file under another spelling (8.3) for a different one.
        path = doc.Name;
        o["saved_to"] = path;
        try { await doc.Editor.CommandAsync("_.SAVEAS", format, path, "_Y"); }
        finally { AcApp.SetSystemVariable("ISAVEBAK", oldBak); }
        if (File.GetLastWriteTimeUtc(path) < started)
            throw new BridgeException("save_failed", $"AutoCAD did not save '{path}' (see the console output)", 500);
        // The .bak is the renamed previous file, so it keeps that file's time.
        o["backup"] = File.Exists(bak) && File.GetLastWriteTimeUtc(bak) != bakBefore ? bak : null;
        return o;
    }

    /// <summary>Full path with 8.3 short names expanded (C:\Users\ABCDEF~1 -> C:\Users\AbcDefGh).</summary>
    private static string LongPath(string path)
    {
        path = Path.GetFullPath(path);
        var sb = new StringBuilder(1024);
        uint n = GetLongPathName(path, sb, (uint)sb.Capacity);
        return n > 0 && n < sb.Capacity ? sb.ToString() : path;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint GetLongPathName(string shortPath, StringBuilder longPath, uint size);
}
