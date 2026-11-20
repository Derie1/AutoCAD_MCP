using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using PlotType = Autodesk.AutoCAD.DatabaseServices.PlotType;

namespace AcadMcpBridge.Handlers;

/// <summary>
/// Printing layouts to PDF with the plot engine (foreground plot, device "DWG To PDF.pc3").
/// Each layout keeps its page setup (paper size, scale, plot style, rotation); only the device is
/// replaced, with the PDF paper size closest to the layout's own.
/// </summary>
internal static class PlotHandlers
{
    private const string Device = "DWG To PDF.pc3";

    private sealed record Sheet(string Layout, ObjectId Id, PlotSettings Settings, string Paper, string Area, string Scale);

    public static JsonObject PlotToPdf(JsonObject a)
    {
        var doc = JsonUtil.ActiveDocument();
        if (JsonUtil.Busy(doc))
            throw new BridgeException("busy", $"AutoCAD is running the command {doc.CommandInProgress}; finish or cancel it first", 409);
        if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
            throw new BridgeException("busy", "AutoCAD is already plotting; wait until it finishes", 409);

        bool separate = Args.Bool(a, "separate_files");
        bool overwrite = Args.Bool(a, "overwrite");
        string target = separate ? FileHandlers.FullPath(a, "path", null) : FileHandlers.FullPath(a, "path", [".pdf"]);
        if (separate && !Directory.Exists(target))
            throw new BridgeException("not_found", $"folder '{target}' does not exist (with separate_files, 'path' is a folder)", 404);
        if (!separate && !Directory.Exists(Path.GetDirectoryName(target)))
            throw new BridgeException("not_found", $"folder '{Path.GetDirectoryName(target)}' does not exist", 404);

        var db = doc.Database;
        var sheets = new List<Sheet>();
        using (var tr = db.TransactionManager.StartOpenCloseTransaction())
        {
            foreach (var (name, id) in Layouts(a, db, tr))
                sheets.Add(Prepare(a, (Layout)tr.GetObject(id, OpenMode.ForRead), id));
            tr.Commit();
        }

        string drawing = Path.GetFileNameWithoutExtension(doc.Name);
        var files = separate
            ? sheets.Select(s => Path.Combine(target, Safe($"{drawing}-{s.Layout}.pdf"))).ToList()
            : [target];
        foreach (var f in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(f)) continue;
            if (!overwrite)
                throw new BridgeException("file_exists", $"'{f}' already exists; pass overwrite: true to replace it (ask the user first)", 409);
            try { using var _ = new FileStream(f, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { throw new BridgeException("file_in_use", $"'{f}' is open in another program (a PDF viewer?); close it first", 409); }
        }

        var started = DateTime.Now;
        object oldBackground = AcApp.GetSystemVariable("BACKGROUNDPLOT");
        AcApp.SetSystemVariable("BACKGROUNDPLOT", 0);
        try
        {
            using (doc.LockDocument())
            {
                // The plot engine prints only the current layout (eLayoutNotCurrent otherwise): each sheet's
                // layout is made current while it prints, and the user's layout comes back at the end.
                var lm = LayoutManager.Current;
                string userLayout = lm.CurrentLayout;
                try
                {
                    if (separate)
                        for (int i = 0; i < sheets.Count; i++) Plot(doc, [sheets[i]], files[i]);
                    else Plot(doc, sheets, target);
                }
                finally
                {
                    try { if (!lm.CurrentLayout.Equals(userLayout, StringComparison.OrdinalIgnoreCase)) lm.CurrentLayout = userLayout; }
                    catch { /* the user's layout is gone: leave it */ }
                    UndoTracker.Recorded(doc, false); // the lock leaves an empty UNDO group
                }
            }
        }
        finally { AcApp.SetSystemVariable("BACKGROUNDPLOT", oldBackground); }

        var outFiles = new JsonArray();
        for (int i = 0; i < files.Count; i++)
        {
            var fi = new FileInfo(files[i]);
            outFiles.Add(new JsonObject
            {
                ["path"] = files[i],
                ["bytes"] = fi.Exists ? fi.Length : 0,
                ["layouts"] = separate ? new JsonArray(sheets[i].Layout) : new JsonArray(sheets.Select(s => (JsonNode)s.Layout).ToArray()),
            });
        }
        return new JsonObject
        {
            ["files"] = outFiles,
            ["sheets"] = new JsonArray(sheets.Select(s => (JsonNode)new JsonObject
            {
                ["layout"] = s.Layout,
                ["paper"] = s.Paper,
                ["area"] = s.Area,
                ["scale"] = s.Scale,
            }).ToArray()),
            ["seconds"] = Math.Round((DateTime.Now - started).TotalSeconds, 1),
        };
    }

    /// <summary>The layouts to plot, in tab order: 'layouts' names, "*" for all paper layouts, default the current one.</summary>
    private static List<(string Name, ObjectId Id)> Layouts(JsonObject a, Database db, Transaction tr)
    {
        var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        var all = new List<(string Name, ObjectId Id, int Tab)>();
        foreach (DBDictionaryEntry e in dict)
        {
            var lo = (Layout)tr.GetObject(e.Value, OpenMode.ForRead);
            all.Add((lo.LayoutName, e.Value, lo.TabOrder));
        }
        all.Sort((x, y) => x.Tab.CompareTo(y.Tab));

        var wanted = Args.StrList(a, "layouts");
        if (wanted == null || wanted.Count == 0)
        {
            string current = LayoutManager.Current.CurrentLayout;
            return all.Where(l => l.Name.Equals(current, StringComparison.OrdinalIgnoreCase)).Select(l => (l.Name, l.Id)).ToList();
        }
        if (wanted.Count == 1 && wanted[0] is "*" or "all")
        {
            var paper = all.Where(l => !l.Name.Equals("Model", StringComparison.OrdinalIgnoreCase)).Select(l => (l.Name, l.Id)).ToList();
            return paper.Count > 0 ? paper : throw new BridgeException("not_found", "the drawing has no paper layouts; plot 'Model' instead", 404);
        }
        var result = new List<(string, ObjectId)>();
        foreach (var w in wanted)
        {
            var hit = all.FirstOrDefault(l => l.Name.Equals(w, StringComparison.OrdinalIgnoreCase));
            if (hit.Name == null)
                throw new BridgeException("not_found", $"no layout '{w}'; layouts: {string.Join(", ", all.Select(l => l.Name))}", 404);
            if (!result.Any(r => r.Item2 == hit.Id)) result.Add((hit.Name, hit.Id));
        }
        return result;
    }

    /// <summary>Plot settings for one layout: its page setup with the PDF device, plus the arguments.</summary>
    private static Sheet Prepare(JsonObject a, Layout lo, ObjectId id)
    {
        var psv = PlotSettingsValidator.Current;
        var ps = new PlotSettings(lo.ModelType);
        ps.CopyFrom(lo);

        // Paper size of the layout's own page setup, to find the closest PDF paper.
        var size = ps.PlotPaperSize; // millimetres, as set up (width, height)
        bool hadDevice = size.X > 1 && size.Y > 1;
        psv.SetPlotConfigurationName(ps, Device, null);
        psv.RefreshLists(ps);

        if (Args.Str(a, "paper") is string paper) SetPaper(psv, ps, paper);
        else Closest(psv, ps, hadDevice ? size.X : 420, hadDevice ? size.Y : 297);

        if (Args.Str(a, "plot_style") is string style)
        {
            var styles = psv.GetPlotStyleSheetList().Cast<string>().ToList();
            var hit = styles.FirstOrDefault(s => s.Equals(style, StringComparison.OrdinalIgnoreCase)
                                                 || Path.GetFileNameWithoutExtension(s).Equals(style, StringComparison.OrdinalIgnoreCase))
                      ?? throw new BridgeException("not_found", $"no plot style '{style}'; available: {string.Join(", ", styles)}", 404);
            psv.SetCurrentStyleSheet(ps, hit);
        }

        string area;
        if (lo.ModelType)
        {
            string mode = Args.Str(a, "area") ?? (a["p1"] != null ? "window" : "extents");
            switch (mode)
            {
                case "extents":
                    psv.SetPlotType(ps, PlotType.Extents);
                    break;
                case "window":
                    var p1 = Args.Point(a, "p1", required: true)!.Value;
                    var p2 = Args.Point(a, "p2", required: true)!.Value;
                    psv.SetPlotWindowArea(ps, new Extents2d(Math.Min(p1.X, p2.X), Math.Min(p1.Y, p2.Y), Math.Max(p1.X, p2.X), Math.Max(p1.Y, p2.Y)));
                    psv.SetPlotType(ps, PlotType.Window);
                    break;
                case "page_setup":
                    break;
                default:
                    throw new BridgeException("bad_request", "'area' must be extents, window or page_setup");
            }
            if (mode != "page_setup")
            {
                if (Args.Num(a, "scale") is double sc)
                {
                    if (sc <= 0) throw new BridgeException("bad_request", "'scale' must be > 0 (100 means 1:100)");
                    psv.SetUseStandardScale(ps, false);
                    psv.SetCustomPrintScale(ps, new CustomScale(1, sc));
                }
                else
                {
                    psv.SetUseStandardScale(ps, true);
                    psv.SetStdScaleType(ps, StdScaleType.ScaleToFit);
                }
                psv.SetPlotCentered(ps, true);
                // Turn the sheet so that a wide area prints on a wide sheet.
                Extents2d? box = mode == "window" ? ps.PlotWindowArea
                    : lo.Database.Extmin.X <= lo.Database.Extmax.X
                        ? new Extents2d(lo.Database.Extmin.X, lo.Database.Extmin.Y, lo.Database.Extmax.X, lo.Database.Extmax.Y)
                        : null;
                string media = ps.CanonicalMediaName;
                if (box is Extents2d b && MediaSize(media) is (double w, double h) && Math.Abs(w - h) > 1e-6)
                {
                    bool wideArea = b.MaxPoint.X - b.MinPoint.X >= b.MaxPoint.Y - b.MinPoint.Y;
                    var rotation = PlotRotation.Degrees000;
                    if (wideArea != w > h)
                    {
                        // The same paper turned (e.g. A4 297 x 210) gives an upright PDF page; rotate only without it.
                        bool bleed = media.Contains("full_bleed", StringComparison.OrdinalIgnoreCase);
                        string? turned = psv.GetCanonicalMediaNameList(ps).Cast<string>().FirstOrDefault(c =>
                            MediaSize(c) is (double cw, double ch) && Math.Abs(cw - h) < 0.5 && Math.Abs(ch - w) < 0.5
                            && c.Contains("full_bleed", StringComparison.OrdinalIgnoreCase) == bleed);
                        if (turned != null) psv.SetCanonicalMediaName(ps, turned);
                        else rotation = PlotRotation.Degrees090;
                    }
                    psv.SetPlotRotation(ps, rotation);
                }
            }
            area = mode;
        }
        else
        {
            if (a["area"] != null || a["scale"] != null)
                throw new BridgeException("bad_request", "'area' and 'scale' apply to Model only; paper layouts print with their page setup");
            area = ps.PlotType.ToString().ToLowerInvariant();
        }

        string scale = ps.UseStandardScale && ps.StdScaleType == StdScaleType.ScaleToFit
            ? "fit"
            : $"{Math.Round(ps.CustomPrintScale.Numerator, 6)}:{Math.Round(ps.CustomPrintScale.Denominator, 6)}";
        return new Sheet(lo.LayoutName, id, ps, psv.GetLocaleMediaName(ps, ps.CanonicalMediaName), area, scale);
    }

    /// <summary>Paper size (width, height) in mm from a canonical media name such as ISO_A3_(420.00_x_297.00_MM).</summary>
    private static (double W, double H)? MediaSize(string canonical)
    {
        var m = Regex.Match(canonical, @"\((\d+(?:\.\d+)?)_x_(\d+(?:\.\d+)?)_(MM|Inches)\)", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        double k = m.Groups[3].Value.Equals("MM", StringComparison.OrdinalIgnoreCase) ? 1 : 25.4;
        return (double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) * k,
                double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) * k);
    }

    /// <summary>
    /// The PDF paper closest in size to w x h mm (either orientation), full-bleed preferred: the layout keeps
    /// its paper size when its own printer is replaced. (SetClosestMediaName fails with eInvalidInput here.)
    /// </summary>
    private static void Closest(PlotSettingsValidator psv, PlotSettings ps, double w, double h)
    {
        string? best = null;
        double bestScore = double.MaxValue;
        foreach (string c in psv.GetCanonicalMediaNameList(ps))
        {
            if (MediaSize(c) is not (double cw, double ch)) continue;
            double score = Math.Min(Math.Abs(cw - w) + Math.Abs(ch - h), Math.Abs(cw - h) + Math.Abs(ch - w))
                           + (Math.Abs(cw - w) + Math.Abs(ch - h) <= Math.Abs(cw - h) + Math.Abs(ch - w) ? 0 : 0.1)
                           + (c.Contains("full_bleed", StringComparison.OrdinalIgnoreCase) ? 0 : 0.2);
            if (score < bestScore) { bestScore = score; best = c; }
        }
        if (best != null) psv.SetCanonicalMediaName(ps, best);
    }

    /// <summary>Sets a PDF paper by canonical or display name, or by a short name such as "A3" (ISO, landscape-agnostic).</summary>
    private static void SetPaper(PlotSettingsValidator psv, PlotSettings ps, string paper)
    {
        var canon = psv.GetCanonicalMediaNameList(ps).Cast<string>().ToList();
        var shown = canon.Select(c => psv.GetLocaleMediaName(ps, c)).ToList();
        int i = canon.FindIndex(c => c.Equals(paper, StringComparison.OrdinalIgnoreCase));
        if (i < 0) i = shown.FindIndex(c => c.Equals(paper, StringComparison.OrdinalIgnoreCase));
        if (i < 0 && Regex.IsMatch(paper, @"^[ABC]\d$", RegexOptions.IgnoreCase))
            i = shown.FindIndex(c => Regex.IsMatch(c, $@"^ISO full bleed {Regex.Escape(paper)} \(", RegexOptions.IgnoreCase));
        if (i < 0)
        {
            var near = shown.Where(s => s.Contains(paper, StringComparison.OrdinalIgnoreCase)).Take(15).ToList();
            throw new BridgeException("not_found", $"no PDF paper '{paper}'" + (near.Count > 0
                ? $"; similar: {string.Join("; ", near)}"
                : "; use a short ISO name such as A3, or a full name like 'ISO full bleed A3 (420.00 x 297.00 MM)'"), 404);
        }
        psv.SetCanonicalMediaName(ps, canon[i]);
    }

    private static void Plot(Document doc, List<Sheet> sheets, string file)
    {
        using var engine = PlotFactory.CreatePublishEngine();
        using var progress = new PlotProgressDialog(false, sheets.Count, true);
        progress.set_PlotMsgString(PlotMessageIndex.DialogTitle, "AI companion: PDF");
        progress.OnBeginPlot();
        progress.IsVisible = true;
        engine.BeginPlot(progress, null);
        for (int i = 0; i < sheets.Count; i++)
        {
            var lm = LayoutManager.Current;
            if (!lm.CurrentLayout.Equals(sheets[i].Layout, StringComparison.OrdinalIgnoreCase)) lm.CurrentLayout = sheets[i].Layout;
            var info = new PlotInfo { Layout = sheets[i].Id, OverrideSettings = sheets[i].Settings };
            new PlotInfoValidator { MediaMatchingPolicy = MatchingPolicy.MatchEnabled }.Validate(info);
            if (i == 0) engine.BeginDocument(info, doc.Name, null, 1, true, file);
            progress.OnBeginSheet();
            engine.BeginPage(new PlotPageInfo(), info, i == sheets.Count - 1, null);
            engine.BeginGenerateGraphics(null);
            engine.EndGenerateGraphics(null);
            engine.EndPage(null);
            progress.OnEndSheet();
        }
        engine.EndDocument(null);
        progress.OnEndPlot();
        engine.EndPlot(null);
    }

    private static string Safe(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
