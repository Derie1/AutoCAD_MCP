using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace AcadMcpServer;

/// <summary>
/// batch_run / batch_status: the same tool steps over many drawings without an AutoCAD window.
/// One AcCoreConsole.exe per drawing (several in parallel); each loads the installed plugin, runs the
/// MCPBATCH command on the drawing (job file in, result file out, see src\Batch.cs) and quits.
/// Jobs live in this server process; batch_run waits up to wait_seconds, then batch_status follows up.
/// </summary>
internal sealed class BatchRunner(JsonArray tools)
{
    public const string RunTool = "batch_run";
    public const string StatusTool = "batch_status";

    private const int MaxFiles = 2000;
    private const int MaxSteps = 50;
    private const int MaxReportChars = 60_000;
    private const int KeptJobs = 20;

    /// <summary>Tools that do not change the drawing (printing writes PDF files only).</summary>
    private static readonly HashSet<string> ReadTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "get_document_info", "find_entities", "get_entity_properties", "get_system_variable",
        "list_layers", "list_blocks", "list_layouts", "list_xrefs", "list_styles",
        "get_table", "extract_block_attributes", "plot_to_pdf",
    };

    /// <summary>Same list as the plugin's Batch.NotInBatch (checked here before any console starts).</summary>
    private static readonly Dictionary<string, string> NotInBatch = new(StringComparer.OrdinalIgnoreCase)
    {
        ["health_check"] = "it checks the bridge in the running AutoCAD",
        ["list_documents"] = "a batch opens, saves and closes each drawing itself",
        ["new_document"] = "a batch opens, saves and closes each drawing itself",
        ["open_document"] = "a batch opens, saves and closes each drawing itself",
        ["set_active_document"] = "a batch opens, saves and closes each drawing itself",
        ["close_document"] = "a batch opens, saves and closes each drawing itself",
        ["save_document"] = "a batch opens, saves and closes each drawing itself",
        ["save_as"] = "a batch opens, saves and closes each drawing itself",
        ["zoom"] = "a batch has no screen or view",
        ["capture_view"] = "a batch has no screen or view",
        ["get_selection"] = "a batch has no screen or selection",
        ["set_selection"] = "a batch has no screen or selection",
        ["undo"] = "a batch has no undo",
        ["run_command"] = "AutoCAD commands are not available in batches yet",
        ["eval_lisp"] = "AutoLISP is not available in batches yet",
        [RunTool] = "a batch cannot start another batch",
        [StatusTool] = "a batch cannot start another batch",
    };

    /// <summary>AutoCAD year -> registry release key and plugin build folder.</summary>
    private static readonly (string Year, string Release, string Series)[] Versions =
    [
        ("2027", "R26.0", "R26"),
        ("2026", "R25.1", "R25"),
        ("2025", "R25.0", "R25"),
    ];

    private readonly ConcurrentDictionary<string, Job> _jobs = new();
    private int _counter;

    public static bool Handles(string tool) => tool is RunTool or StatusTool;

    // ---------------------------------------------------------------- batch_run

    public async Task<JsonObject> RunAsync(JsonObject a, CancellationToken serverShutdown)
    {
        var steps = ParseSteps(a);
        bool writes = CheckSteps(steps);
        bool goOn = Bool(a, "continue_on_error");
        var files = ListFiles(a);
        var targets = SaveTargets(a, files, writes, out SaveMode mode);
        string? root = Str(a, "folder") is { Length: > 0 } rootFolder ? Path.GetFullPath(rootFolder) : null;
        CheckPlots(steps, files, root);
        var console = PickConsole(Str(a, "autocad_version"));
        int parallel = Int(a, "parallel", DefaultParallel(), 1, 8);
        int fileTimeout = Int(a, "file_timeout_seconds", 300, 10, 3600);
        int wait = Int(a, "wait_seconds", 600, 0, 600);

        string id = $"b{DateTime.Now:HHmmss}-{Interlocked.Increment(ref _counter)}";
        var job = new Job(id, files, console, parallel, Path.Combine(Path.GetTempPath(), "acadmcp_batch", id))
        {
            Mode = mode,
            Targets = targets,
            Root = root,
        };
        foreach (var old in _jobs.Values.Where(j => j.Finished).OrderBy(j => j.Started).SkipLast(KeptJobs - 1).ToList())
            _jobs.TryRemove(old.Id, out _);
        _jobs[id] = job;
        job.Task = Task.Run(() => RunJob(job, steps, goOn, fileTimeout, serverShutdown));

        await Task.WhenAny(job.Task, Task.Delay(TimeSpan.FromSeconds(wait), serverShutdown));
        return Report(job, 0);
    }

    private static JsonArray ParseSteps(JsonObject a)
    {
        if (a["steps"] is not JsonArray given || given.Count == 0)
            throw new ToolException("'steps' must be a non-empty array of {\"tool\": ..., \"args\": {...}}");
        if (given.Count > MaxSteps) throw new ToolException($"at most {MaxSteps} steps per batch");
        var steps = new JsonArray();
        for (int i = 0; i < given.Count; i++)
        {
            if (given[i] is not JsonObject s || s["tool"]?.ToString() is not { Length: > 0 } tool)
                throw new ToolException($"step {i + 1}: expected {{\"tool\": \"...\", \"args\": {{...}}}}");
            var args = new JsonObject();
            if (s["args"] is JsonObject given2)
            {
                foreach (var (k, v) in given2)
                    if (v != null) args[k == "from_point" ? "from" : k] = v.DeepClone();
            }
            else if (s["args"] != null) throw new ToolException($"step {i + 1}: 'args' must be an object");
            steps.Add(new JsonObject { ["tool"] = tool, ["args"] = args });
        }
        return steps;
    }

    /// <summary>Throws for a step that cannot run in a batch; true when a step changes the drawings.</summary>
    private bool CheckSteps(JsonArray steps)
    {
        bool writes = false;
        for (int i = 0; i < steps.Count; i++)
        {
            string tool = steps[i]!["tool"]!.ToString();
            var args = (JsonObject)steps[i]!["args"]!;
            string where = $"step {i + 1} ({tool})";
            if (NotInBatch.TryGetValue(tool, out string? why)) throw new ToolException($"{where} cannot run in a batch: {why}.");
            var schema = tools.FirstOrDefault(t => t?["name"]?.ToString() == tool)
                ?? throw new ToolException($"{where}: there is no such tool.");
            if (ReadTools.Contains(tool)) continue;
            if (tool == "audit" && args["fix"]?.GetValue<bool>() != true) continue;
            var dryRun = schema["inputSchema"]?["properties"]?["dry_run"];
            if (dryRun != null && (args["dry_run"] ?? dryRun["default"])?.GetValue<bool>() == true) continue;
            writes = true;
        }
        return writes;
    }

    private enum SaveMode { None, Copy, InPlace }

    /// <summary>A file's path below 'folder' (subfolders are kept in output folders), else its name.</summary>
    private static string Relative(string file, string? root) =>
        root != null && file.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(root, file) : Path.GetFileName(file);

    /// <summary>
    /// plot_to_pdf in a batch: 'path' is the folder for the PDFs of all drawings. Each drawing gets
    /// folder\(subfolder)\name.pdf, or with separate_files folder\(subfolder)\name-layout.pdf.
    /// </summary>
    private static void CheckPlots(JsonArray steps, List<string> files, string? root)
    {
        foreach (var step in steps)
        {
            if (step!["tool"]!.ToString() != "plot_to_pdf") continue;
            var args = (JsonObject)step["args"]!;
            string folder = Str(args, "path") ?? throw new ToolException("plot_to_pdf in a batch needs path: the folder for the PDFs");
            if (!Path.IsPathFullyQualified(folder)) throw new ToolException($"plot_to_pdf path '{folder}' is not a full path");
            if (folder.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                throw new ToolException("in a batch, plot_to_pdf path is a FOLDER for the PDFs (each drawing gets <name>.pdf, " +
                    "or <name>-<layout>.pdf with separate_files); give a folder");
            if (Bool(args, "separate_files")) continue; // file names depend on the layouts: checked per drawing
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var existing = new List<string>();
            foreach (string f in files)
            {
                string pdf = PdfFor(folder, f, root, false);
                if (seen.TryGetValue(pdf, out string? other))
                    throw new ToolException($"'{other}' and '{f}' would both print to '{pdf}'; give them separately");
                seen[pdf] = f;
                if (File.Exists(pdf)) existing.Add(pdf);
            }
            if (existing.Count > 0 && !Bool(args, "overwrite"))
                throw new ToolException($"{existing.Count} PDF file(s) already exist, e.g. '{existing[0]}'; ask the user, then pass " +
                    "overwrite: true in the plot_to_pdf args to replace them, or choose another folder. Nothing was run.");
        }
    }

    private static string PdfFor(string folder, string file, string? root, bool separate)
    {
        string dir = Path.GetFullPath(Path.Combine(folder, Path.GetDirectoryName(Relative(file, root)) ?? ""));
        return separate ? dir : Path.Combine(dir, Path.GetFileNameWithoutExtension(file) + ".pdf");
    }

    /// <summary>The steps for one drawing: plot_to_pdf gets that drawing's PDF path (its folder is created).</summary>
    private static JsonArray StepsFor(JsonArray steps, string file, string? root)
    {
        var copy = (JsonArray)steps.DeepClone();
        foreach (var step in copy)
        {
            if (step!["tool"]!.ToString() != "plot_to_pdf") continue;
            var args = (JsonObject)step["args"]!;
            bool separate = Bool(args, "separate_files");
            string target = PdfFor(Str(args, "path")!, file, root, separate);
            Directory.CreateDirectory(separate ? target : Path.GetDirectoryName(target)!);
            args["path"] = target;
        }
        return copy;
    }

    /// <summary>
    /// Where each drawing is saved when the steps change it (null: not saved). Changing steps need an explicit
    /// choice that the model asks the user for: copies in an output folder, or over the originals with .bak.
    /// </summary>
    private static string?[] SaveTargets(JsonObject a, List<string> files, bool writes, out SaveMode mode)
    {
        mode = SaveMode.None;
        var targets = new string?[files.Count];
        string? save = Str(a, "save");
        if (!writes) return targets; // nothing changes: opened read-only, nothing saved
        if (save == null)
            throw new ToolException("The steps change the drawings, and the user has not said where to save them. Ask the user, " +
                "then call again with either save: \"copy\" and output_folder (changed drawings are saved there, the originals " +
                "stay as they are) or save: \"in_place\" with confirm: true (the originals are overwritten, the previous " +
                "version of each stays next to it as .bak). Nothing was run.");
        if (save == "in_place")
        {
            if (!Bool(a, "confirm"))
                throw new ToolException("save: \"in_place\" overwrites the original drawings (each keeps a .bak); it needs confirm: true " +
                    "after the user agreed. Nothing was run.");
            mode = SaveMode.InPlace;
            for (int i = 0; i < files.Count; i++) targets[i] = files[i];
            return targets;
        }
        if (save != "copy") throw new ToolException("'save' must be \"copy\" or \"in_place\"");

        string output = Str(a, "output_folder") ?? throw new ToolException("save: \"copy\" needs output_folder (a full path)");
        if (!Path.IsPathFullyQualified(output)) throw new ToolException($"output_folder '{output}' is not a full path");
        output = Path.GetFullPath(output);
        string? root = Str(a, "folder") is { Length: > 0 } f ? Path.GetFullPath(f) : null;
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var existing = new List<string>();
        for (int i = 0; i < files.Count; i++)
        {
            string target = Path.GetFullPath(Path.Combine(output, Relative(files[i], root)));
            if (string.Equals(target, files[i], StringComparison.OrdinalIgnoreCase))
                throw new ToolException($"output_folder is the drawings' own folder ('{files[i]}' would overwrite itself); " +
                    "choose another folder, or save: \"in_place\" with confirm: true");
            if (seen.TryGetValue(target, out string? other))
                throw new ToolException($"'{other}' and '{files[i]}' would both be saved as '{target}'; give them separately");
            seen[target] = files[i];
            if (File.Exists(target)) existing.Add(target);
            targets[i] = target;
        }
        if (existing.Count > 0 && !Bool(a, "overwrite"))
            throw new ToolException($"{existing.Count} file(s) already exist in output_folder, e.g. '{existing[0]}'; ask the user, " +
                "then pass overwrite: true to replace them, or choose an empty folder. Nothing was run.");
        mode = SaveMode.Copy;
        return targets;
    }

    private static List<string> ListFiles(JsonObject a)
    {
        var found = new List<string>();
        if (a["files"] is JsonArray list)
            foreach (var f in list)
            {
                string path = f?.ToString() ?? "";
                if (!Path.IsPathFullyQualified(path)) throw new ToolException($"'{path}' is not a full path");
                if (!File.Exists(path)) throw new ToolException($"file '{path}' does not exist");
                found.Add(Path.GetFullPath(path));
            }
        if (Str(a, "folder") is { Length: > 0 } folder)
        {
            if (!Directory.Exists(folder)) throw new ToolException($"folder '{folder}' does not exist");
            string pattern = Str(a, "pattern") is { Length: > 0 } p ? p : "*.dwg";
            var option = Bool(a, "recursive") ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            found.AddRange(Directory.EnumerateFiles(folder, pattern, option)
                .Where(f => f.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase));
        }
        if (a["files"] == null && Str(a, "folder") == null) throw new ToolException("give 'folder' (with 'pattern') or 'files'");
        var distinct = found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinct.Count == 0) throw new ToolException("no DWG files found");
        if (distinct.Count > MaxFiles) throw new ToolException($"{distinct.Count} files: at most {MaxFiles} per batch; split the folder");
        return distinct;
    }

    private static int DefaultParallel() => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    // ---------------------------------------------------------------- consoles

    private sealed record ConsoleInfo(string Year, string Exe, string Plugin);

    /// <summary>The newest AutoCAD whose AcCoreConsole and plugin build are installed (or the asked year).</summary>
    private static ConsoleInfo PickConsole(string? year)
    {
        var missing = new List<string>();
        foreach (var (y, release, series) in Versions)
        {
            if (year != null && year != y) continue;
            string? exe = ConsolePath(y, release);
            string? plugin = PluginPath(series);
            if (exe != null && plugin != null) return new ConsoleInfo(y, exe, plugin);
            if (year != null)
                throw new ToolException(exe == null
                    ? $"AutoCAD {y} is not installed (no accoreconsole.exe)"
                    : $"the AutoCAD MCP plugin for AutoCAD {y} is not installed ({series} build); run the installer and choose AutoCAD {y}");
            if (exe != null) missing.Add(y);
        }
        if (year != null && !Versions.Any(v => v.Year == year))
            throw new ToolException("'autocad_version' must be 2025, 2026 or 2027");
        throw new ToolException(missing.Count > 0
            ? $"the AutoCAD MCP plugin is not installed for AutoCAD {string.Join(", ", missing)}; run the installer"
            : "no AutoCAD 2025-2027 with accoreconsole.exe was found");
    }

    private static string? ConsolePath(string year, string release)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Autodesk\AutoCAD\{release}");
                foreach (string product in key?.GetSubKeyNames() ?? [])
                {
                    using var sub = key!.OpenSubKey(product);
                    if (sub?.GetValue("AcadLocation") is string dir && File.Exists(Path.Combine(dir, "accoreconsole.exe")))
                        return Path.Combine(dir, "accoreconsole.exe");
                }
            }
            catch (Exception) { /* fall back to the default folder */ }
        }
        string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Autodesk", $"AutoCAD {year}", "accoreconsole.exe");
        return File.Exists(fallback) ? fallback : null;
    }

    /// <summary>The plugin next to this server (bundle\Contents\Server -> Contents\R25|R26), else the user's bundle.</summary>
    private static string? PluginPath(string series)
    {
        string? here = Path.GetDirectoryName(typeof(BatchRunner).Assembly.Location);
        var candidates = new List<string>();
        if (here != null) candidates.Add(Path.GetFullPath(Path.Combine(here, "..", series, "AcadMcpBridge.dll")));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Autodesk", "ApplicationPlugins", "AcadMcpBridge.bundle", "Contents", series, "AcadMcpBridge.dll"));
        return candidates.FirstOrDefault(File.Exists);
    }

    // ---------------------------------------------------------------- running

    private sealed class Job(string id, List<string> files, ConsoleInfo console, int parallel, string workDir)
    {
        public string Id { get; } = id;
        public List<string> Files { get; } = files;
        public ConsoleInfo Console { get; } = console;
        public int Parallel { get; } = parallel;
        public string WorkDir { get; } = workDir;
        public DateTime Started { get; } = DateTime.Now;
        public DateTime? Ended { get; set; }
        public JsonObject?[] Results { get; } = new JsonObject?[files.Count];
        public CancellationTokenSource Cancel { get; } = new();
        public Task Task { get; set; } = Task.CompletedTask;
        public string? Failure { get; set; }
        public bool Finished => Ended != null;
        public SaveMode Mode { get; init; }
        public string?[] Targets { get; init; } = [];
        public string? Root { get; init; }
    }

    private async Task RunJob(Job job, JsonArray steps, bool goOn, int fileTimeout, CancellationToken serverShutdown)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(job.Cancel.Token, serverShutdown);
        try
        {
            Directory.CreateDirectory(job.WorkDir);
            string script = Path.Combine(job.WorkDir, "batch.scr");
            File.WriteAllText(script, $"_.NETLOAD\r\n\"{job.Console.Plugin}\"\r\n_MCPBATCH\r\n_.QUIT\r\n_Y\r\n", new UTF8Encoding(false));

            using var gate = new SemaphoreSlim(job.Parallel);
            var tasks = job.Files.Select(async (file, i) =>
            {
                try { await gate.WaitAsync(linked.Token); }
                catch (OperationCanceledException) { job.Results[i] = Skipped(file, "cancelled before it started"); return; }
                try { job.Results[i] = Unwritable(job, file) ?? await RunFile(job, i, file, script, steps, goOn, fileTimeout, linked.Token); }
                catch (Exception ex) { job.Results[i] = Failed(file, ex.Message); }
                finally { gate.Release(); }
            }).ToList();
            await Task.WhenAll(tasks);
        }
        catch (Exception ex) { job.Failure = ex.Message; }
        finally
        {
            job.Ended = DateTime.Now;
            try { Directory.Delete(job.WorkDir, true); } catch (Exception) { /* a killed console may still hold a file */ }
        }
    }

    private static async Task<JsonObject> RunFile(Job job, int index, string file, string script, JsonArray steps,
                                                  bool goOn, int timeoutSeconds, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string dir = Path.Combine(job.WorkDir, index.ToString("D4"));
        Directory.CreateDirectory(dir);
        string jobFile = Path.Combine(dir, "job.json");
        string resultFile = Path.Combine(dir, "result.json");
        var plan = new JsonObject
        {
            ["steps"] = StepsFor(steps, file, job.Root),
            ["continue_on_error"] = goOn,
            ["result"] = resultFile,
        };
        string? target = job.Targets[index];
        if (target != null)
        {
            plan["save_as"] = target;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        }
        File.WriteAllText(jobFile, plan.ToJsonString(Json.Options), new UTF8Encoding(false));

        // /isolate: the console gets its own user profile. Without it the console writes its working folder
        // into the user's LogFilePath (shared "<<Unnamed Profile>>"), and AutoCAD later logs into a deleted folder.
        string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AcadMcpBridge", "console", job.Console.Year);
        Directory.CreateDirectory(profile);
        var psi = new ProcessStartInfo(job.Console.Exe)
        {
            // Read-only when nothing is saved: then a drawing open in AutoCAD can still be read.
            Arguments = $"/i \"{file}\" /s \"{script}\"{(job.Mode == SaveMode.None ? " /readonly" : "")} " +
                        $"/isolate acadmcp_batch \"{profile}\"",
            WorkingDirectory = dir, // the console writes its command-line log here
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.Unicode,
        };
        psi.Environment["ACADMCP_BATCH_JOB"] = jobFile;

        // The first console with a new isolated profile copies the user's AutoCAD folders into it; several
        // doing that at once fail ("Copying folder failed"). Until one has finished, consoles start one by one.
        string ready = Path.Combine(profile, "acadmcp_profile_ready");
        SemaphoreSlim? held = null;
        if (!File.Exists(ready))
        {
            await ProfileGate.WaitAsync(ct);
            if (File.Exists(ready)) ProfileGate.Release();
            else held = ProfileGate;
        }
        // Opened for writing, AutoCAD puts lock files (.dwl, .dwl2) next to the drawing; a stopped console leaves them.
        string[] locks = [Path.ChangeExtension(file, ".dwl"), Path.ChangeExtension(file, ".dwl2")];
        bool[] hadLock = locks.Select(File.Exists).ToArray();
        try
        {
            return await Start(psi, file, resultFile, timeoutSeconds, sw, ct, () =>
            {
                if (held != null && Directory.Exists(Path.Combine(profile, "Roaming")))
                    try { File.WriteAllText(ready, DateTime.Now.ToString("s")); } catch (IOException) { }
                for (int k = 0; k < locks.Length; k++)
                    if (!hadLock[k] && File.Exists(locks[k])) try { File.Delete(locks[k]); } catch (IOException) { }
            });
        }
        finally { held?.Release(); }
    }

    private static readonly SemaphoreSlim ProfileGate = new(1, 1);

    private static async Task<JsonObject> Start(ProcessStartInfo psi, string file, string resultFile, int timeoutSeconds,
                                                Stopwatch sw, CancellationToken ct, Action exited)
    {
        var tail = new Queue<string>();
        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) =>
        {
            string line = (e.Data ?? "").Replace("\0", "").Trim();
            if (line.Length == 0) return;
            lock (tail) { tail.Enqueue(line); if (tail.Count > 20) tail.Dequeue(); }
        };
        proc.ErrorDataReceived += (_, _) => { };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        string? stopped = null;
        try { await proc.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            stopped = ct.IsCancellationRequested ? "cancelled" : $"did not finish within {timeoutSeconds} s (file_timeout_seconds)";
            try { proc.Kill(entireProcessTree: true); } catch (Exception) { }
            try { await proc.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { }
        }
        exited();

        var o = new JsonObject { ["file"] = file };
        if (stopped == null && File.Exists(resultFile))
        {
            var r = JsonNode.Parse(File.ReadAllText(resultFile)) as JsonObject ?? new JsonObject();
            o["ok"] = r["ok"]?.GetValue<bool>() == true;
            if (r["error"] != null) { o["code"] = r["code"]?.DeepClone(); o["error"] = r["error"]?.DeepClone(); }
            o["steps"] = r["steps"]?.DeepClone();
            foreach (string key in new[] { "changed", "saved_to", "version", "backup", "save_error" })
                if (r[key] != null) o[key] = r[key]!.DeepClone();
            if (o["save_error"] != null) o["ok"] = false;
        }
        else
        {
            string text;
            lock (tail) text = string.Join("\n", tail);
            o["ok"] = false;
            o["error"] = stopped != null ? $"the console {stopped}; it was stopped"
                : text.Contains("MCPBATCH", StringComparison.OrdinalIgnoreCase) && text.Contains("Unknown command", StringComparison.OrdinalIgnoreCase)
                    ? "the installed AutoCAD MCP plugin has no batch mode (older than 0.6.2) or did not load; reinstall it"
                    : $"the console ended without a result (exit code {proc.ExitCode}); the drawing may be damaged or not a DWG";
            if (stopped == null) o["console_output"] = text;
        }
        o["ms"] = sw.ElapsedMilliseconds;
        return o;
    }

    private static JsonObject Failed(string file, string error) => new() { ["file"] = file, ["ok"] = false, ["error"] = error };
    private static JsonObject Skipped(string file, string why) => new() { ["file"] = file, ["ok"] = false, ["skipped"] = why };

    /// <summary>When the drawing will be changed: a read-only or locked (open in AutoCAD, by another user) file is skipped.</summary>
    private static JsonObject? Unwritable(Job job, string file)
    {
        if (job.Mode == SaveMode.None) return null;
        if (File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly))
            return Skipped(file, "the file is read-only (file attribute); it was not changed");
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Skipped(file, "the file is in use (open in AutoCAD or by another user or program); close it and run the batch " +
                "again for this file; it was not changed");
        }
    }

    // ---------------------------------------------------------------- batch_status and the report

    public JsonObject Status(JsonObject a)
    {
        string? id = Str(a, "job_id");
        if (id == null)
            return new JsonObject
            {
                ["jobs"] = new JsonArray(_jobs.Values.OrderByDescending(j => j.Started).Select(j => (JsonNode)new JsonObject
                {
                    ["job_id"] = j.Id,
                    ["status"] = State(j),
                    ["files"] = j.Files.Count,
                    ["started"] = j.Started.ToString("yyyy-MM-dd HH:mm:ss"),
                }).ToArray()),
            };
        if (!_jobs.TryGetValue(id, out var job)) throw new ToolException($"no batch job '{id}' (jobs are kept only while the MCP server runs)");
        if (Bool(a, "cancel") && !job.Finished)
        {
            job.Cancel.Cancel();
            job.Task.Wait(TimeSpan.FromSeconds(20));
        }
        return Report(job, Int(a, "offset", 0, 0, int.MaxValue));
    }

    private static string State(Job j) =>
        j.Failure != null ? "failed" : !j.Finished ? "running" : j.Cancel.IsCancellationRequested ? "cancelled" : "done";

    private static JsonObject Report(Job job, int offset)
    {
        var done = job.Results.Where(r => r != null).ToList();
        var o = new JsonObject
        {
            ["job_id"] = job.Id,
            ["status"] = State(job),
            ["autocad_version"] = job.Console.Year,
            ["parallel"] = job.Parallel,
            ["total_files"] = job.Files.Count,
            ["finished_files"] = done.Count,
            ["ok_files"] = done.Count(r => r!["ok"]?.GetValue<bool>() == true),
            ["failed_files"] = done.Count(r => r!["ok"]?.GetValue<bool>() != true && r!["skipped"] == null),
            ["skipped_files"] = done.Count(r => r!["skipped"] != null),
            ["changed_files"] = done.Count(r => r!["changed"]?.GetValue<bool>() == true),
            ["saved_files"] = done.Count(r => r!["saved_to"] is JsonValue),
            ["save"] = job.Mode switch { SaveMode.Copy => "copy", SaveMode.InPlace => "in_place", _ => "none (read-only)" },
            ["elapsed_s"] = Math.Round(((job.Ended ?? DateTime.Now) - job.Started).TotalSeconds, 1),
        };
        if (job.Failure != null) o["error"] = job.Failure;

        // Finished files in list order, from offset, as many as fit.
        var files = new JsonArray();
        int size = 0, next = -1;
        for (int i = offset; i < job.Results.Length; i++)
        {
            var r = job.Results[i];
            if (r == null) continue;
            int len = r.ToJsonString(Json.Options).Length;
            if (files.Count > 0 && size + len > MaxReportChars) { next = i; break; }
            files.Add(r.DeepClone());
            size += len;
        }
        o["offset"] = offset;
        o["files"] = files;
        if (next >= 0)
        {
            o["next_offset"] = next;
            o["note"] = $"the report is long: call batch_status with job_id \"{job.Id}\" and offset {next} for the next files";
        }
        else if (!job.Finished)
            o["note"] = $"still running: call batch_status with job_id \"{job.Id}\" later (cancel: true stops it)";
        return o;
    }

    // ---------------------------------------------------------------- arguments

    private static string? Str(JsonObject a, string name) => a[name] is JsonValue v ? v.ToString() : null;

    private static bool Bool(JsonObject a, string name) =>
        a[name] is JsonValue v && v.TryGetValue(out bool b) && b;

    private static int Int(JsonObject a, string name, int fallback, int min, int max)
    {
        if (a[name] is not JsonValue v) return fallback;
        if (v.TryGetValue(out int i)) return Math.Clamp(i, min, max);
        if (v.TryGetValue(out double d)) return Math.Clamp((int)d, min, max);
        throw new ToolException($"'{name}' must be a number");
    }
}
