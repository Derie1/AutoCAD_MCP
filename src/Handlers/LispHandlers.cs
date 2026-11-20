using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace AcadMcpBridge.Handlers;

/// <summary>
/// eval_lisp: an AutoLISP expression evaluated in the active drawing, the result returned as JSON.
/// Uses acedEvaluateLisp (accore.dll, exported but undocumented; present in AutoCAD 2025-2027) inside the
/// command context, with the same frame as run_command (one UNDO group, dialogs off, echo captured).
/// Only when the user allowed commands (CommandAccess).
/// </summary>
internal static class LispHandlers
{
    [DllImport("accore.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "?acedEvaluateLisp@@YAHPEB_WAEAPEAUresbuf@@@Z")]
    private static extern int AcedEvaluateLisp(string lisp, out IntPtr result);

    private const int RTNORM = 5100;
    private const string Var = "acadmcp:r";

    /// <summary>acedEvaluateLisp reports success as 1 (measured in 2026/2027), not RTNORM.</summary>
    private static bool Ok(int status) => status is 1 or RTNORM;

    public static Task<JsonObject> EvalLisp(JsonObject a)
    {
        CommandAccess.Require("eval_lisp");
        var doc = JsonUtil.ActiveDocument();
        if (JsonUtil.Busy(doc))
            throw new BridgeException("busy", $"AutoCAD is running the command {doc.CommandInProgress}; finish or cancel it first", 409);
        string expr = (Args.Str(a, "expression", required: true) ?? "").Trim();
        if (expr.Length == 0) throw new BridgeException("bad_request", "'expression' must not be empty");
        // An unclosed form makes AutoLISP wait for the rest on the command line: AutoCAD would hang.
        if (Unbalanced(expr) is string problem) throw new BridgeException("bad_request", $"'expression': {problem}");
        if (Waits(expr) is string fn)
            throw new BridgeException("bad_request",
                $"'{fn}' cannot be used in eval_lisp: {(fn.StartsWith("command") || fn == "vl-cmdf"
                    ? "commands from AutoLISP hang AutoCAD here; run them with run_command"
                    : "it waits for the user to pick or type in AutoCAD; pass the values in the expression instead")}");
        Log.Info($"eval_lisp {a.ToJsonString()}");
        return Run(doc, expr);
    }

    private static async Task<JsonObject> Run(Document doc, string expr)
    {
        JsonNode? value = null;
        string? lispError = null, printed = null;
        int status = 0;
        // The expression runs as the body of a lambda under vl-catch-all-apply: several forms are allowed,
        // the last one's value is returned, and a LISP error comes back as a message instead of *error*.
        string wrapped =
            $"(progn (setq {Var} (vl-catch-all-apply (function (lambda () {expr}\n)))) " +
            $"(if (vl-catch-all-error-p {Var}) (list \"ACADMCP-ERROR\" (vl-catch-all-error-message {Var})) " +
            $"(list \"ACADMCP-OK\" {Var})))";

        var r = await CommandHandlers.Execute(doc, _ =>
        {
            status = AcedEvaluateLisp(wrapped, out IntPtr rb);
            if (Ok(status) && rb != IntPtr.Zero)
            {
                using var buf = (ResultBuffer)DisposableWrapper.Create(typeof(ResultBuffer), rb, true);
                var items = buf.AsArray();
                // ("ACADMCP-OK" value) or ("ACADMCP-ERROR" message). The returned list may come without its
                // opening RTLB (then the buffer is just the elements): read it as a sequence either way.
                var seq = new JsonArray();
                for (int i = 0; i < items.Length;)
                {
                    if ((LispDataType)items[i].TypeCode is LispDataType.ListEnd or LispDataType.DottedPair) { i++; continue; }
                    seq.Add(Read(items, ref i));
                }
                var top = seq.Count == 1 && seq[0] is JsonArray only ? only : seq;
                string tag = top.Count > 0 ? top[0]?.ToString() ?? "" : "";
                var second = top.Count > 1 ? top[1]?.DeepClone() : null;
                if (tag == "ACADMCP-ERROR") lispError = second?.ToString();
                else value = second;

                if (lispError == null && Ok(AcedEvaluateLisp($"(vl-prin1-to-string {Var})", out IntPtr p)) && p != IntPtr.Zero)
                {
                    using var pb = (ResultBuffer)DisposableWrapper.Create(typeof(ResultBuffer), p, true);
                    printed = pb.AsArray().FirstOrDefault().Value as string;
                }
            }
            AcedEvaluateLisp($"(setq {Var} nil)", out IntPtr none);
            if (none != IntPtr.Zero) ((ResultBuffer)DisposableWrapper.Create(typeof(ResultBuffer), none, true)).Dispose();
            return Task.CompletedTask;
        });

        string? error = lispError
            ?? (!Ok(status) ? $"AutoLISP could not evaluate the expression (status {status}): check the parentheses and quotes" : null)
            ?? r.Failure?.Message;
        return new JsonObject
        {
            ["value"] = error == null ? value : null,
            ["lisp"] = printed == null ? null : CommandHandlers.Tail(printed, 20000),
            ["error"] = error,
            ["completed"] = error == null && r.Pending == null,
            ["cancelled_waiting_for_input"] = r.Pending != null,
            ["waiting_command"] = r.Pending,
            ["last_prompt"] = r.LastPrompt,
            ["output"] = CommandHandlers.Tail(Output(r.Body), 8000),
            ["still_active"] = r.StillActive,
        };
    }

    /// <summary>What the expression printed (princ, prompt …): the echo starts on the "Command:" line.</summary>
    private static string Output(List<string> body)
    {
        var lines = body.ToList();
        if (lines.Count > 0 && lines[0].StartsWith("Command:", StringComparison.OrdinalIgnoreCase))
            lines[0] = lines[0]["Command:".Length..].TrimStart();
        return string.Join("\n", lines).Trim();
    }

    /// <summary>
    /// Functions that would block AutoCAD when evaluated through acedEvaluateLisp: command calls (measured:
    /// command-s hangs, command fails) and functions that wait for the user's input. Strings and comments
    /// are ignored; ssget is allowed only with a selection mode string such as "_X".
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex WaitingCall = new(
        @"\(\s*(command-s|command|vl-cmdf|getpoint|getcorner|getdist|getangle|getorient|getint|getreal|getstring|getkword|" +
        @"entsel|nentsel|nentselp|getfiled|ssget(?!\s*""))(?=[\s()]|$)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    internal static string? Waits(string s)
    {
        // Blank out strings and comments, keep the code.
        var code = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"')
            {
                code.Append('"');
                for (i++; i < s.Length && s[i] != '"'; i++) { if (s[i] == '\\') i++; code.Append('x'); }
                code.Append('"');
            }
            else if (c == ';')
            {
                bool block = i + 1 < s.Length && s[i + 1] == '|';
                int end = block ? s.IndexOf("|;", i + 2, StringComparison.Ordinal) : s.IndexOf('\n', i);
                i = end < 0 ? s.Length : (block ? end + 1 : end - 1);
                code.Append(' ');
            }
            else code.Append(c);
        }
        var m = WaitingCall.Match(code.ToString());
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    /// <summary>
    /// Checks parentheses and string quotes, skipping strings ("…" with \ escapes) and comments
    /// (; to the end of the line, ;| … |;). Returns what is wrong, or null.
    /// </summary>
    internal static string? Unbalanced(string s)
    {
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"')
            {
                for (i++; i < s.Length && s[i] != '"'; i++)
                    if (s[i] == '\\') i++;
                if (i >= s.Length) return "a string is not closed (missing \")";
            }
            else if (c == ';' && i + 1 < s.Length && s[i + 1] == '|')
            {
                int end = s.IndexOf("|;", i + 2, StringComparison.Ordinal);
                if (end < 0) return "a ;| comment is not closed with |;";
                i = end + 1;
            }
            else if (c == ';') { while (i < s.Length && s[i] != '\n') i++; }
            else if (c == '(') depth++;
            else if (c == ')' && --depth < 0) return "there is a ')' without a matching '('";
        }
        return depth > 0 ? $"{depth} '(' not closed" : null;
    }

    /// <summary>One LISP value from a result buffer: atoms, lists (JSON arrays), dotted pairs ([car, cdr]).</summary>
    private static JsonNode? Read(TypedValue[] items, ref int i)
    {
        if (i >= items.Length) return null;
        var tv = items[i++];
        switch ((LispDataType)tv.TypeCode)
        {
            case LispDataType.ListBegin:
                var list = new JsonArray();
                while (i < items.Length)
                {
                    var code = (LispDataType)items[i].TypeCode;
                    if (code is LispDataType.ListEnd or LispDataType.DottedPair) { i++; break; }
                    list.Add(Read(items, ref i));
                }
                return list;
            case LispDataType.Nil: return null;
            case LispDataType.T_atom: return true;
            case LispDataType.Int16: return Convert.ToInt64(tv.Value);
            case LispDataType.Int32: return Convert.ToInt64(tv.Value);
            case LispDataType.Double:
            case LispDataType.Angle:
            case LispDataType.Orientation: return Math.Round(Convert.ToDouble(tv.Value), 12);
            case LispDataType.Text: return tv.Value as string;
            case LispDataType.Point2d when tv.Value is Point2d p2: return new JsonArray(Math.Round(p2.X, 9), Math.Round(p2.Y, 9));
            case LispDataType.Point3d when tv.Value is Point3d p3: return JsonUtil.Point(p3);
            case LispDataType.ObjectId when tv.Value is ObjectId id:
                return new JsonObject { ["entity"] = id.IsNull ? null : id.Handle.ToString() };
            case LispDataType.SelectionSet when tv.Value is SelectionSet ss:
                var ids = ss.GetObjectIds();
                return new JsonObject
                {
                    ["selection_set"] = ids.Length,
                    ["handles"] = new JsonArray(ids.Take(1000).Select(x => (JsonNode)x.Handle.ToString()).ToArray()),
                };
            case LispDataType.Void:
            case LispDataType.None: return null;
            default: return tv.Value?.ToString();
        }
    }
}
