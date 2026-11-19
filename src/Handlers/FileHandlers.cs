using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadMcpBridge.Handlers;

/// <summary>
/// Opening, creating, switching and closing drawings. These run in the application context (the
/// main-thread dispatcher is not a command), where AutoCAD allows changing the set of documents.
/// Every other tool keeps working with the active drawing only.
/// </summary>
internal static class FileHandlers
{
    public static JsonObject NewDocument(JsonObject a)
    {
        Ready();
        string template = Template(Args.Str(a, "template"));
        var doc = AcApp.DocumentManager.Add(template);
        Activate(doc);
        var o = Describe(doc);
        o["template"] = template;
        return o;
    }

    public static JsonObject OpenDocument(JsonObject a)
    {
        Ready();
        string path = FullPath(a, "path", [".dwg", ".dxf", ".dwt"]);
        if (!File.Exists(path)) throw new BridgeException("not_found", $"file '{path}' does not exist", 404);
        bool readOnly = Args.Bool(a, "read_only");

        if (Find(path) is Document open)
        {
            Activate(open);
            var r = Describe(open);
            r["already_open"] = true;
            return r;
        }
        // A file in use elsewhere makes AutoCAD ask in a modal dialog, which would block the bridge.
        if (!readOnly && InUse(path))
            throw new BridgeException("file_in_use", $"'{path}' is in use by another program or user; open it with read_only: true", 409);

        var doc = AcApp.DocumentManager.Open(path, readOnly);
        Activate(doc);
        var o = Describe(doc);
        o["already_open"] = false;
        return o;
    }

    public static JsonObject SetActiveDocument(JsonObject a)
    {
        Ready();
        var doc = Pick(a, required: true)!;
        Activate(doc);
        return Describe(doc);
    }

    public static JsonObject CloseDocument(JsonObject a)
    {
        Ready();
        var doc = Pick(a, required: false) ?? JsonUtil.ActiveDocument();
        bool save = Args.Bool(a, "save"), discard = Args.Bool(a, "discard_changes");
        if (save && discard) throw new BridgeException("bad_request", "use either 'save' or 'discard_changes', not both");
        if (!string.IsNullOrEmpty(doc.CommandInProgress))
            throw new BridgeException("busy", $"'{Name(doc)}' is running the command {doc.CommandInProgress}; finish or cancel it first", 409);

        bool modified = Modified(doc);
        string name = Name(doc);
        string? path = doc.IsNamedDrawing ? doc.Name : null;
        if (save)
        {
            if (path == null) throw new BridgeException("bad_request", $"'{name}' has never been saved; save it with save_as first");
            if (doc.IsReadOnly) throw new BridgeException("read_only", $"'{name}' is open read-only; it cannot be saved", 409);
        }
        else if (modified && !discard)
            throw new BridgeException("unsaved_changes",
                $"'{name}' has unsaved changes; pass save: true to save them or discard_changes: true to lose them", 409);

        if (save) doc.CloseAndSave(path!);
        else doc.CloseAndDiscard();

        var active = AcApp.DocumentManager.MdiActiveDocument;
        return new JsonObject
        {
            ["closed"] = name,
            ["path"] = path,
            ["saved"] = save,
            ["discarded_changes"] = !save && modified,
            ["active_document"] = active == null ? null : Name(active),
            ["open_documents"] = AcApp.DocumentManager.Count,
        };
    }

    // ---- helpers ----

    /// <summary>Refuses while the active drawing runs a command or a dialog is open.</summary>
    private static void Ready()
    {
        var active = AcApp.DocumentManager.MdiActiveDocument;
        if (active != null && !string.IsNullOrEmpty(active.CommandInProgress))
            throw new BridgeException("busy", $"AutoCAD is running the command {active.CommandInProgress}; finish or cancel it first", 409);
        if (!AcApp.IsQuiescent && active != null)
            throw new BridgeException("busy", "AutoCAD is busy (a command or a dialog is open)", 409);
    }

    private static void Activate(Document doc)
    {
        var dm = AcApp.DocumentManager;
        if (dm.MdiActiveDocument != doc) dm.MdiActiveDocument = doc;
    }

    internal static string Name(Document d) => Path.GetFileName(d.Name);

    /// <summary>Unsaved changes of any open drawing (DBMOD is readable only for the active one).</summary>
    internal static bool? ModifiedOrNull(Document d)
    {
        try
        {
            if (d == AcApp.DocumentManager.MdiActiveDocument) return Convert.ToInt32(AcApp.GetSystemVariable("DBMOD")) != 0;
            dynamic com = d.GetAcadDocument();
            return !(bool)com.Saved;
        }
        catch { return null; }
    }

    private static bool Modified(Document d) => ModifiedOrNull(d) ?? true; // unknown: assume there are changes

    internal static JsonObject Describe(Document d) => new()
    {
        ["name"] = Name(d),
        ["path"] = d.IsNamedDrawing ? d.Name : null,
        ["active"] = d == AcApp.DocumentManager.MdiActiveDocument,
        ["read_only"] = d.IsReadOnly,
        ["has_unsaved_changes"] = ModifiedOrNull(d),
        ["open_documents"] = AcApp.DocumentManager.Count,
    };

    /// <summary>The document named by 'path' (full path) or 'name' (file name, e.g. "Drawing1.dwg").</summary>
    private static Document? Pick(JsonObject a, bool required)
    {
        string? path = Args.Str(a, "path"), name = Args.Str(a, "name");
        if (path == null && name == null)
            return required ? throw new BridgeException("bad_request", "give 'name' (file name) or 'path' of an open drawing (see list_documents)") : null;
        if (path != null)
            return Find(FullPath(a, "path", null))
                   ?? throw new BridgeException("not_found", $"'{path}' is not open (see list_documents)", 404);

        var hits = new List<Document>();
        foreach (Document d in AcApp.DocumentManager)
        {
            string n = Name(d);
            if (n.Equals(name, StringComparison.OrdinalIgnoreCase) || Path.GetFileNameWithoutExtension(n).Equals(name, StringComparison.OrdinalIgnoreCase))
                hits.Add(d);
        }
        return hits.Count switch
        {
            1 => hits[0],
            0 => throw new BridgeException("not_found", $"no open drawing named '{name}' (see list_documents)", 404),
            _ => throw new BridgeException("ambiguous", $"{hits.Count} open drawings are named '{name}'; give 'path' instead"),
        };
    }

    private static Document? Find(string fullPath)
    {
        foreach (Document d in AcApp.DocumentManager)
            if (d.IsNamedDrawing && string.Equals(Path.GetFullPath(d.Name), fullPath, StringComparison.OrdinalIgnoreCase))
                return d;
        return null;
    }

    /// <summary>A fully qualified path with one of the allowed extensions (null: any).</summary>
    internal static string FullPath(JsonObject a, string arg, string[]? extensions)
    {
        string p = Args.Str(a, arg, required: true)!.Trim().Trim('"');
        if (!Path.IsPathFullyQualified(p))
            throw new BridgeException("bad_request", $"'{arg}' must be a full path, e.g. C:\\Projects\\plan.dwg");
        p = Path.GetFullPath(p);
        if (extensions != null && !extensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
            throw new BridgeException("bad_request", $"'{arg}' must end with {string.Join(", ", extensions)}");
        return p;
    }

    private static bool InUse(string path)
    {
        try { using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return false; } // read-only file: AutoCAD opens it read-only itself
    }

    /// <summary>
    /// The template to start from: a full path, or a file name looked up in AutoCAD's template folder
    /// (Options > Files) and then in the template copies of the installation (UserDataCache\…\Template,
    /// used when the profile has no template folder). No template: the QNEW template from the options,
    /// else acadiso.dwt or acad.dwt by MEASUREINIT.
    /// </summary>
    private static string Template(string? given)
    {
        var folders = new List<string>();
        string qnew = "";
        try
        {
            dynamic files = ((dynamic)Autodesk.AutoCAD.ApplicationServices.Application.AcadApplication).Preferences.Files;
            folders.AddRange(((string)files.TemplateDwgPath).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            qnew = (string)files.QNewTemplateFile;
        }
        catch { /* COM preferences unavailable */ }
        try
        {
            string cache = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? "", "UserDataCache");
            if (Directory.Exists(cache))
                folders.AddRange(Directory.GetDirectories(cache).Select(d => Path.Combine(d, "Template")));
        }
        catch { /* no installation copies */ }
        folders = folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (given == null)
        {
            if (!string.IsNullOrWhiteSpace(qnew) && File.Exists(qnew)) return qnew;
            given = Convert.ToInt32(AcApp.GetSystemVariable("MEASUREINIT")) == 1 ? "acadiso.dwt" : "acad.dwt";
        }
        else if (!given.EndsWith(".dwt", StringComparison.OrdinalIgnoreCase) && !given.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase))
            given += ".dwt";

        if (Path.IsPathFullyQualified(given))
            return File.Exists(given) ? given : throw new BridgeException("not_found", $"template '{given}' does not exist", 404);
        foreach (var f in folders)
            if (File.Exists(Path.Combine(f, given))) return Path.Combine(f, given);

        var available = folders.SelectMany(f => Directory.GetFiles(f, "*.dwt")).Select(Path.GetFileName)
                               .Distinct(StringComparer.OrdinalIgnoreCase).Take(40).ToList();
        throw new BridgeException("not_found", $"template '{given}' not found" +
            (available.Count > 0 ? $"; available: {string.Join(", ", available)}" : "; give a full path to a .dwt file"), 404);
    }
}
