using System.Text;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Geometry;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadMcpBridge.Handlers;

/// <summary>
/// run_command: any AutoCAD command with its answers, in the active drawing, as one UNDO step.
/// Only when the user allowed commands (CommandAccess).
/// </summary>
internal static class CommandHandlers
{
    /// <summary>Commands that end the session, load foreign code or start programs.</summary>
    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "QUIT", "EXIT", "CLOSE", "CLOSEALL", "SHELL", "SH", "NETLOAD", "APPLOAD", "ARX", "VBALOAD", "VBARUN",
    };

    public static Task<JsonObject> RunCommand(JsonObject a)
    {
        CommandAccess.Require("run_command");
        var doc = JsonUtil.ActiveDocument();
        if (!string.IsNullOrEmpty(doc.CommandInProgress))
            throw new BridgeException("busy", $"AutoCAD is running the command {doc.CommandInProgress}; finish or cancel it first", 409);

        string command = (Args.Str(a, "command", required: true) ?? "").Trim();
        if (command.Length == 0 || command.Contains(' '))
            throw new BridgeException("bad_request", "'command' is one command name such as \"_.LINE\" or \"-LAYER\"; its answers go to 'inputs'");
        string bare = command.TrimStart('_', '.', '-', '+', '\'');
        if (Forbidden.Contains(bare))
            throw new BridgeException("forbidden", $"{bare} cannot be run through run_command (it closes AutoCAD or drawings, loads code or starts programs); use the dedicated tools", 403);

        var tokens = new List<object> { command };
        if (a["inputs"] is JsonArray inputs)
            for (int i = 0; i < inputs.Count; i++) tokens.Add(Token(inputs[i], i));
        else if (a["inputs"] != null)
            throw new BridgeException("bad_request", "'inputs' must be a list: strings, numbers, points [x, y] or \"\" for Enter");

        Log.Info($"run_command {a.ToJsonString()}");
        return Run(doc, command, tokens);
    }

    private static object Token(JsonNode? n, int i)
    {
        switch (n)
        {
            case null: return "";
            case JsonValue v when v.TryGetValue(out string? s): return s;
            case JsonValue v when v.TryGetValue(out double d): return d;
            case JsonArray: return Args.ToPoint(n, $"inputs[{i}]");
            default: throw new BridgeException("bad_request", $"inputs[{i}] must be a string, a number or a point [x, y]");
        }
    }

    /// <summary>A command of the user's kind is running (not the command-context wrapper EXECUTEFUNCTION).</summary>
    private static bool Active(Document doc) =>
        !string.IsNullOrEmpty(doc.CommandInProgress)
        && !doc.CommandInProgress.Equals("EXECUTEFUNCTION", StringComparison.OrdinalIgnoreCase);

    private static async Task<JsonObject> Run(Document doc, string command, List<object> tokens)
    {
        var ed = doc.Editor;
        var dm = AcApp.DocumentManager;
        var log = CommandLog.Begin();
        string? pending = null;
        Exception? failure = null;
        List<(string Name, object Old)>? dialogs = null;
        // System variable changes are recorded by UNDO: switch the dialogs off and back on INSIDE the
        // command's UNDO group, so undoing the call does not leave them switched off.
        void DialogsOff() => dialogs = SysVars.Set(("FILEDIA", 0), ("CMDDIA", 0));
        void DialogsBack() { if (dialogs != null) { SysVars.Restore(dialogs); dialogs = null; } }
        UndoTracker.BeginOwnCommands();
        try
        {
            await dm.ExecuteInCommandContextAsync(async _ =>
            {
                await ed.CommandAsync("_.UNDO", "_BEgin");
                DialogsOff();
                try { await ed.CommandAsync(tokens.ToArray()); }
                catch (Exception ex) { failure = ex; }
                if (Active(doc)) pending = doc.CommandInProgress; // the answers ran out
                else
                {
                    DialogsBack();
                    await ed.CommandAsync("_.UNDO", "_End");
                }
            }, null);

            if (pending != null)
            {
                // The command still waits for input: cancel it like Esc, then close the UNDO group.
                // (A "\x03" passed to CommandAsync is only another answer, not a cancel.)
                doc.SendStringToExecute("\x03\x03", true, false, false);
                for (int i = 0; i < 200 && Active(doc); i++) await Task.Delay(50);
                await dm.ExecuteInCommandContextAsync(async _ =>
                {
                    DialogsBack();
                    await ed.CommandAsync("_.UNDO", "_End");
                }, null);
            }
        }
        finally
        {
            DialogsBack(); // only if something above failed half-way
            UndoTracker.EndOwnCommands();
            UndoTracker.Recorded(doc, true);
        }
        string text = log.End();
        try { ed.UpdateScreen(); } catch { }

        // Only what the command printed: after "UNDO _BEgin", before the closing "UNDO _End".
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r').Trim()).Where(l => l.Length > 0).ToList();
        int from = lines.FindIndex(l => l.EndsWith("_BEgin", StringComparison.OrdinalIgnoreCase));
        int to = lines.FindLastIndex(l => l.StartsWith("Command: _.UNDO", StringComparison.OrdinalIgnoreCase)
                                          || l.StartsWith("_.UNDO", StringComparison.OrdinalIgnoreCase));
        var body = lines.Skip(from + 1).Take((to > from ? to : lines.Count) - from - 1).ToList();
        string? lastPrompt = null;
        if (pending != null)
        {
            // "Specify next point or [Undo]:" then "Command: *Cancel*" (or the prompt and *Cancel* on one line)
            int c = body.FindIndex(l => l.Contains("*Cancel*"));
            string line = c >= 0 ? body[c].Replace("*Cancel*", "").Trim() : (body.Count > 0 ? body[^1] : "");
            lastPrompt = line.Length > 0 && !line.Equals("Command:", StringComparison.OrdinalIgnoreCase) ? line
                       : c > 0 ? body[c - 1] : null;
            // Drop the cancellation echo itself.
            if (c >= 0) body = body.Take(c).ToList();
        }
        if (failure == null && body.Any(l => l.Contains("Unknown command", StringComparison.OrdinalIgnoreCase)))
            failure = new InvalidOperationException($"unknown command {command}");

        return new JsonObject
        {
            ["command"] = command,
            ["completed"] = pending == null && failure == null,
            ["cancelled_waiting_for_input"] = pending != null,
            ["waiting_command"] = pending,
            ["last_prompt"] = lastPrompt,
            ["error"] = failure?.Message,
            ["command_line"] = Tail(string.Join("\n", body), 4000),
            ["still_active"] = Active(doc) ? doc.CommandInProgress : null,
        };
    }

    private static string Tail(string s, int max) => s.Length <= max ? s : "…" + s[^max..];
}

/// <summary>Temporarily changed system variables.</summary>
internal static class SysVars
{
    public static List<(string Name, object Old)> Set(params (string Name, object Value)[] vars)
    {
        var old = new List<(string, object)>();
        foreach (var (name, value) in vars)
        {
            try
            {
                old.Add((name, AcApp.GetSystemVariable(name)));
                AcApp.SetSystemVariable(name, value);
            }
            catch (Exception ex) { Log.Error($"cannot set {name}", ex); }
        }
        return old;
    }

    public static void Restore(List<(string Name, object Old)> old)
    {
        foreach (var (name, value) in old)
            try { AcApp.SetSystemVariable(name, value); } catch (Exception ex) { Log.Error($"cannot restore {name}", ex); }
    }
}

/// <summary>
/// What appeared on the command line during a call, through AutoCAD's own command-line log (LOGFILEMODE)
/// in the folder the user's settings give (LOGFILEPATH is never changed: AutoCAD refuses to set it back
/// to a folder that is gone). If the user keeps a log, the part written during the call is read; otherwise
/// the log is switched on for the call, and afterwards the file is cut back to its old length (or deleted).
/// </summary>
internal sealed class CommandLog : IDisposable
{
    private readonly bool _own;
    private readonly string? _file;
    private readonly long _start;
    private readonly bool _existed;
    private bool _ended;

    private CommandLog(bool own, string? file, long start, bool existed)
    {
        _own = own; _file = file; _start = start; _existed = existed;
    }

    public static CommandLog Begin()
    {
        try
        {
            bool userLog = Convert.ToInt32(AcApp.GetSystemVariable("LOGFILEMODE")) == 1;
            // Measure BEFORE switching on: AutoCAD writes a header line as soon as the log opens.
            string before = Convert.ToString(AcApp.GetSystemVariable("LOGFILENAME")) ?? "";
            bool existed = before.Length > 0 && File.Exists(before);
            long start = existed ? new FileInfo(before).Length : 0;
            if (!userLog) AcApp.SetSystemVariable("LOGFILEMODE", 1);
            string file = Convert.ToString(AcApp.GetSystemVariable("LOGFILENAME")) ?? "";
            if (!file.Equals(before, StringComparison.OrdinalIgnoreCase)) { existed = false; start = 0; }
            return new CommandLog(!userLog, file, start, existed);
        }
        catch (Exception ex)
        {
            Log.Error("command-line log not available", ex);
            return new CommandLog(false, null, 0, true);
        }
    }

    /// <summary>Stops logging (if it was started here) and returns the text written during the call.</summary>
    public string End()
    {
        if (_ended) return "";
        _ended = true;
        string text = "";
        try
        {
            if (_own) AcApp.SetSystemVariable("LOGFILEMODE", 0);
            if (_file == null || !File.Exists(_file)) return "";
            using (var fs = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                fs.Seek(Math.Min(_start, fs.Length), SeekOrigin.Begin);
                using var reader = new StreamReader(fs, Encoding.Default);
                text = reader.ReadToEnd();
            }
        }
        catch (Exception ex) { Log.Error("command-line log", ex); }
        if (_own && _file != null) CleanUp(_file, _existed, _start);
        return text;
    }

    /// <summary>
    /// Leaves the user's log folder as it was: deletes the log file it created or cuts the user's file back.
    /// AutoCAD may still hold the file right after LOGFILEMODE = 0: then try again when AutoCAD is idle.
    /// </summary>
    private static void CleanUp(string file, bool existed, long start, int attempt = 0)
    {
        try
        {
            if (!File.Exists(file)) return;
            if (!existed) File.Delete(file);
            else using (var fs = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) fs.SetLength(start);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (attempt >= 20) { Log.Error($"cannot clean up the command-line log {file}", ex); return; }
            void Once(object? s, EventArgs e) { AcApp.Idle -= Once; CleanUp(file, existed, start, attempt + 1); }
            AcApp.Idle += Once;
        }
    }

    public void Dispose() => End();
}
