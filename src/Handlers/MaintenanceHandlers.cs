using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadMcpBridge.Handlers;

/// <summary>Drawing cleanup: purge of unused definitions and audit.</summary>
internal static class MaintenanceHandlers
{
    private static readonly string[] Kinds =
        ["layers", "linetypes", "blocks", "text_styles", "dim_styles", "table_styles", "mleader_styles", "regapps"];

    /// <summary>
    /// Removes unused named objects. dry_run (default true) purges inside a transaction that is then
    /// aborted, so the preview lists exactly what a real purge removes, nested ones included.
    /// </summary>
    public static JsonObject Purge(JsonObject a)
    {
        var kinds = Args.StrList(a, "kinds") ?? [.. Kinds];
        foreach (var k in kinds)
            if (!Kinds.Contains(k)) throw new BridgeException("bad_request", $"unknown kind '{k}'; kinds: {string.Join(", ", Kinds)}");
        var mask = CatalogHandlers.Wildcard(Args.Str(a, "names"));
        bool dryRun = Args.Bool(a, "dry_run", true);

        return Write.Run("purge", ctx =>
        {
            var db = ctx.Db;
            var tr = ctx.Tr;
            var candidates = new Dictionary<ObjectId, (string Kind, string Name)>();
            void Add(string kind, ObjectId id, string name)
            {
                if (mask == null || mask.IsMatch(name)) candidates[id] = (kind, name);
            }

            foreach (var kind in kinds)
            {
                switch (kind)
                {
                    case "layers":
                        foreach (ObjectId id in (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead))
                        {
                            var r = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                            if (r.IsDependent || id == db.Clayer || r.Name == "0" || r.Name.Equals("Defpoints", StringComparison.OrdinalIgnoreCase)) continue;
                            Add(kind, id, r.Name);
                        }
                        break;
                    case "linetypes":
                        foreach (ObjectId id in (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead))
                        {
                            var r = (LinetypeTableRecord)tr.GetObject(id, OpenMode.ForRead);
                            if (r.IsDependent || id == db.ByLayerLinetype || id == db.ByBlockLinetype || id == db.ContinuousLinetype || id == db.Celtype) continue;
                            Add(kind, id, r.Name);
                        }
                        break;
                    case "blocks":
                        foreach (ObjectId id in (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead))
                        {
                            var r = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                            if (r.IsLayout || r.IsAnonymous || r.IsFromExternalReference || r.IsDependent) continue;
                            Add(kind, id, r.Name);
                        }
                        break;
                    case "text_styles":
                        foreach (ObjectId id in (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead))
                        {
                            var r = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                            if (r.IsDependent || r.IsShapeFile || id == db.Textstyle || r.Name.Equals("Standard", StringComparison.OrdinalIgnoreCase)) continue;
                            Add(kind, id, r.Name);
                        }
                        break;
                    case "dim_styles":
                        foreach (ObjectId id in (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead))
                        {
                            var r = (DimStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                            if (r.IsDependent || id == db.Dimstyle || r.Name.Equals("Standard", StringComparison.OrdinalIgnoreCase)) continue;
                            Add(kind, id, r.Name);
                        }
                        break;
                    case "table_styles":
                        Dict(db.TableStyleDictionaryId, db.Tablestyle);
                        break;
                    case "mleader_styles":
                        Dict(db.MLeaderStyleDictionaryId, db.MLeaderstyle);
                        break;
                    case "regapps":
                        foreach (ObjectId id in (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead))
                        {
                            var r = (RegAppTableRecord)tr.GetObject(id, OpenMode.ForRead);
                            if (r.IsDependent || r.Name.Equals("ACAD", StringComparison.OrdinalIgnoreCase)) continue;
                            Add(kind, id, r.Name);
                        }
                        break;
                }

                void Dict(ObjectId dictId, ObjectId current)
                {
                    foreach (DBDictionaryEntry e in (DBDictionary)tr.GetObject(dictId, OpenMode.ForRead))
                        if (e.Value != current && !e.Key.Equals("Standard", StringComparison.OrdinalIgnoreCase)) Add(kind, e.Value, e.Key);
                }
            }

            // Purge in passes: removing a block can free the layers, linetypes and blocks it used.
            var removed = new List<(string Kind, string Name)>();
            int passes = 0;
            while (candidates.Count > 0) // ObjectIdCollection(empty array) throws IndexOutOfRange
            {
                var ids = new ObjectIdCollection(candidates.Keys.ToArray());
                db.Purge(ids);
                if (ids.Count == 0) break;
                passes++;
                foreach (ObjectId id in ids)
                {
                    tr.GetObject(id, OpenMode.ForWrite).Erase();
                    removed.Add(candidates[id]);
                    candidates.Remove(id);
                }
            }

            ctx.Rollback = dryRun;
            var byKind = new JsonObject();
            foreach (var g in removed.GroupBy(r => r.Kind).OrderBy(g => Array.IndexOf(Kinds, g.Key)))
                byKind[g.Key] = new JsonArray(g.Select(r => (JsonNode)r.Name).OrderBy(n => n.ToString(), StringComparer.OrdinalIgnoreCase).ToArray());
            return new JsonObject
            {
                ["dry_run"] = dryRun,
                ["total"] = removed.Count,
                ["passes"] = passes,
                [dryRun ? "would_remove" : "removed"] = byKind,
                ["note"] = dryRun && removed.Count > 0 ? "nothing was changed; call again with dry_run: false to purge" : null,
            };
        }, preview: dryRun);
    }

    /// <summary>
    /// AUDIT through COM AuditInfo. With AUDITCTL = 1 AutoCAD writes a report (.adt) next to a saved
    /// drawing; it is read for the error counts and removed again if it did not exist before.
    /// </summary>
    public static JsonObject Audit(JsonObject a)
    {
        var s = AuditBegin(a);
        try
        {
            dynamic com = s.Doc.GetAcadDocument();
            UndoTracker.AsOwnCommand(() => com.AuditInfo(s.Fix));
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            throw new BridgeException("autocad_error", $"AUDIT failed: {ex.Message}", 500);
        }
        finally { AuditRestore(s); }
        return AuditReport(s);
    }

    /// <summary>
    /// Batch mode (AcCoreConsole has no COM): the AUDIT command inside MCPBATCH. Only the asynchronous
    /// Editor.CommandAsync works there; the synchronous Editor.Command crashes the console.
    /// </summary>
    public static async Task<JsonObject> AuditInBatch(JsonObject a)
    {
        var s = AuditBegin(a);
        try { await s.Doc.Editor.CommandAsync("_.AUDIT", s.Fix ? "_Y" : "_N"); }
        finally { AuditRestore(s); }
        return AuditReport(s);
    }

    private sealed record AuditState(Document Doc, bool Fix, string? Adt, bool Existed, DateTime Before, object OldCtl);

    private static AuditState AuditBegin(JsonObject a)
    {
        var doc = JsonUtil.ActiveDocument();
        if (JsonUtil.Busy(doc))
            throw new BridgeException("busy", $"AutoCAD is running the command {doc.CommandInProgress}; finish or cancel it first", 409);
        bool fix = Args.Bool(a, "fix");
        if (fix && doc.IsReadOnly) throw new BridgeException("read_only", "the drawing is open read-only; audit without fix", 409);

        string? adt = doc.IsNamedDrawing ? Path.ChangeExtension(doc.Name, ".adt") : null;
        bool existed = adt != null && File.Exists(adt);
        var before = existed ? File.GetLastWriteTimeUtc(adt!) : DateTime.MinValue;
        object oldCtl = AcApp.GetSystemVariable("AUDITCTL");
        AcApp.SetSystemVariable("AUDITCTL", adt != null ? 1 : 0);
        return new AuditState(doc, fix, adt, existed, before, oldCtl);
    }

    private static void AuditRestore(AuditState s)
    {
        AcApp.SetSystemVariable("AUDITCTL", s.OldCtl);
        // Measured: AUDIT without fix leaves an empty UNDO group, AUDIT with fix leaves none
        // (its repairs cannot be undone step by step).
        if (!s.Fix) UndoTracker.Recorded(s.Doc, false);
    }

    private static JsonObject AuditReport(AuditState s)
    {
        string? adt = s.Adt;
        var o = new JsonObject { ["fixed_requested"] = s.Fix };
        if (adt != null && File.Exists(adt) && File.GetLastWriteTimeUtc(adt) >= s.Before)
        {
            string text = File.ReadAllText(adt);
            // "Total errors found 0 fixed 0"
            var m = Regex.Matches(text, @"errors\s+found\s+(\d+)\s+fixed\s+(\d+)", RegexOptions.IgnoreCase);
            if (m.Count > 0)
            {
                o["errors_found"] = int.Parse(m[^1].Groups[1].Value);
                o["errors_fixed"] = int.Parse(m[^1].Groups[2].Value);
            }
            var erased = Regex.Match(text, @"Erased\s+(\d+)\s+objects", RegexOptions.IgnoreCase);
            if (erased.Success) o["objects_erased"] = int.Parse(erased.Groups[1].Value);
            // The report pads with spaces and carriage returns (progress output): keep the meaningful lines.
            var lines = text.Split('\n').Select(l => l.Split('\r').Select(x => x.Trim()).LastOrDefault(x => x.Length > 0) ?? "").Where(l => l.Length > 0).ToList();
            string compact = string.Join("\n", lines);
            o["report"] = compact.Length > 6000 ? compact[^6000..] : compact;
            if (!s.Existed) try { File.Delete(adt); } catch { /* leave it */ }
        }
        else
        {
            o["report"] = null;
            o["note"] = adt == null
                ? "the drawing is not saved, so AutoCAD writes no audit report; the audit ran, see AutoCAD's command line"
                : "AutoCAD wrote no audit report; see AutoCAD's command line";
        }
        return o;
    }
}
