using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadMcpBridge.Handlers;

/// <summary>Read-only lists of the drawing's named objects: documents, layers, blocks, layouts, xrefs, styles.</summary>
internal static class CatalogHandlers
{
    public static JsonObject ListDocuments(JsonObject _)
    {
        var dm = AcApp.DocumentManager;
        var active = dm.MdiActiveDocument;
        var items = new JsonArray();
        foreach (Document d in dm)
        {
            items.Add(new JsonObject
            {
                ["name"] = Path.GetFileName(d.Name),
                ["path"] = d.IsNamedDrawing ? d.Name : null,
                ["active"] = d == active,
                ["read_only"] = d.IsReadOnly,
                // DBMOD is only readable for the active drawing.
                ["has_unsaved_changes"] = d == active ? Convert.ToInt32(AcApp.GetSystemVariable("DBMOD")) != 0 : null,
            });
        }
        return new JsonObject { ["count"] = items.Count, ["documents"] = items };
    }

    public static JsonObject ListLayers(JsonObject a)
    {
        bool withCounts = Args.Bool(a, "with_counts", true);
        var name = Wildcard(Args.Str(a, "name"));
        var db = JsonUtil.ActiveDocument().Database;
        using var tr = db.TransactionManager.StartOpenCloseTransaction();

        var counts = withCounts ? CountByLayer(db, tr) : null;
        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        var items = new JsonArray();
        foreach (ObjectId id in lt)
        {
            var l = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
            if (name != null && !name.IsMatch(l.Name)) continue;
            var o = new JsonObject
            {
                ["name"] = l.Name,
                ["color"] = Entities.ColorToJson(l.Color),
                ["linetype"] = ((LinetypeTableRecord)tr.GetObject(l.LinetypeObjectId, OpenMode.ForRead)).Name,
                ["lineweight"] = l.LineWeight.ToString(),
                ["on"] = !l.IsOff,
                ["frozen"] = l.IsFrozen,
                ["locked"] = l.IsLocked,
                ["plottable"] = l.IsPlottable,
                ["current"] = id == db.Clayer,
                ["from_xref"] = l.IsDependent,
                ["description"] = string.IsNullOrEmpty(l.Description) ? null : l.Description,
            };
            if (counts != null) o["entities"] = counts.TryGetValue(l.Name, out int n) ? n : 0;
            items.Add(o);
        }
        tr.Commit();
        return new JsonObject { ["count"] = items.Count, ["layers"] = items };
    }

    public static JsonObject ListBlocks(JsonObject a)
    {
        bool anonymous = Args.Bool(a, "include_anonymous");
        bool xrefs = Args.Bool(a, "include_xrefs");
        var name = Wildcard(Args.Str(a, "name"));
        var db = JsonUtil.ActiveDocument().Database;
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        var items = new JsonArray();
        foreach (ObjectId id in bt)
        {
            var b = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
            if (b.IsLayout || (b.IsAnonymous && !anonymous) || (b.IsFromExternalReference && !xrefs) || b.IsDependent && !xrefs) continue;
            if (name != null && !name.IsMatch(b.Name)) continue;

            var attrs = new JsonArray();
            int entityCount = 0;
            foreach (ObjectId eid in b)
            {
                entityCount++;
                if (eid.ObjectClass.DxfName == "ATTDEF" && tr.GetObject(eid, OpenMode.ForRead) is AttributeDefinition ad)
                    attrs.Add(new JsonObject
                    {
                        ["tag"] = ad.Tag,
                        ["prompt"] = ad.Prompt,
                        ["default"] = ad.TextString,
                        ["constant"] = ad.Constant,
                        ["invisible"] = ad.Invisible,
                        ["multiline"] = ad.IsMTextAttributeDefinition,
                    });
            }

            items.Add(new JsonObject
            {
                ["name"] = b.Name,
                ["dynamic"] = b.IsDynamicBlock,
                ["xref"] = b.IsFromExternalReference,
                ["anonymous"] = b.IsAnonymous,
                ["references"] = CountReferences(b, tr),
                ["entities"] = entityCount,
                ["attributes"] = attrs,
                ["origin"] = JsonUtil.Point(b.Origin),
                ["units"] = b.Units.ToString(),
                ["description"] = string.IsNullOrEmpty(b.Comments) ? null : b.Comments,
            });
        }
        tr.Commit();
        return new JsonObject { ["count"] = items.Count, ["blocks"] = items };
    }

    public static JsonObject ListLayouts(JsonObject _)
    {
        var db = JsonUtil.ActiveDocument().Database;
        string current = LayoutManager.Current.CurrentLayout;
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        var layouts = new List<Layout>();
        foreach (DBDictionaryEntry e in dict) layouts.Add((Layout)tr.GetObject(e.Value, OpenMode.ForRead));
        layouts.Sort((x, y) => x.TabOrder.CompareTo(y.TabOrder));

        var items = new JsonArray();
        foreach (var l in layouts)
        {
            var btr = (BlockTableRecord)tr.GetObject(l.BlockTableRecordId, OpenMode.ForRead);
            int entities = 0, viewports = 0;
            foreach (ObjectId id in btr)
            {
                entities++;
                if (id.ObjectClass.DxfName == "VIEWPORT") viewports++;
            }
            // Every paper layout has one overall viewport that is the sheet itself.
            if (!l.ModelType && viewports > 0) { viewports--; entities--; }
            items.Add(new JsonObject
            {
                ["name"] = l.LayoutName,
                ["model"] = l.ModelType,
                ["tab_order"] = l.TabOrder,
                ["current"] = l.LayoutName.Equals(current, StringComparison.OrdinalIgnoreCase),
                ["entities"] = entities,
                ["viewports"] = l.ModelType ? null : viewports,
                ["plot_device"] = l.PlotConfigurationName,
                ["paper"] = l.CanonicalMediaName,
                ["plot_style_table"] = l.CurrentStyleSheet,
                ["plot_rotation"] = l.PlotRotation.ToString(),
            });
        }
        tr.Commit();
        return new JsonObject { ["count"] = items.Count, ["layouts"] = items };
    }

    public static JsonObject ListXrefs(JsonObject _)
    {
        var db = JsonUtil.ActiveDocument().Database;
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        var items = new JsonArray();
        foreach (ObjectId id in bt)
        {
            var b = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
            if (!b.IsFromExternalReference) continue;
            items.Add(new JsonObject
            {
                ["name"] = b.Name,
                ["path"] = b.PathName,
                ["status"] = b.XrefStatus.ToString(),
                ["overlay"] = b.IsFromOverlayReference,
                ["nested"] = b.IsDependent,
                ["references"] = CountReferences(b, tr),
            });
        }
        tr.Commit();
        return new JsonObject { ["count"] = items.Count, ["xrefs"] = items };
    }

    public static JsonObject ListStyles(JsonObject a)
    {
        var kinds = Args.StrList(a, "kinds") ?? ["text", "dimension", "table", "multileader", "linetype"];
        var db = JsonUtil.ActiveDocument().Database;
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var result = new JsonObject();
        foreach (var kind in kinds)
        {
            switch (kind.ToLowerInvariant())
            {
                case "text":
                    result["text"] = Records<TextStyleTableRecord>(tr, db.TextStyleTableId, db.Textstyle, s => s.IsShapeFile ? null : new JsonObject
                    {
                        ["font"] = s.FileName,
                        ["big_font"] = string.IsNullOrEmpty(s.BigFontFileName) ? null : s.BigFontFileName,
                        ["truetype"] = string.IsNullOrEmpty(s.Font.TypeFace) ? null : s.Font.TypeFace,
                        ["height"] = s.TextSize,
                        ["width_factor"] = s.XScale,
                        ["oblique_deg"] = Math.Round(s.ObliquingAngle * 180 / Math.PI, 6),
                    });
                    break;
                case "dimension":
                    result["dimension"] = Records<DimStyleTableRecord>(tr, db.DimStyleTableId, db.Dimstyle, s => new JsonObject
                    {
                        ["text_height"] = s.Dimtxt,
                        ["arrow_size"] = s.Dimasz,
                        ["scale"] = s.Dimscale,
                    });
                    break;
                case "linetype":
                    result["linetype"] = Records<LinetypeTableRecord>(tr, db.LinetypeTableId, db.Celtype, s => new JsonObject
                    {
                        ["description"] = s.AsciiDescription,
                    });
                    break;
                case "table":
                    result["table"] = Dictionary(tr, db.TableStyleDictionaryId, db.Tablestyle);
                    break;
                case "multileader":
                    result["multileader"] = Dictionary(tr, db.MLeaderStyleDictionaryId, db.MLeaderstyle);
                    break;
                default:
                    throw new BridgeException("bad_request",
                        $"unknown style kind '{kind}'; use text, dimension, table, multileader, linetype");
            }
        }
        tr.Commit();
        return result;
    }

    // ---------------------------------------------------------------- helpers

    private static JsonArray Records<T>(Transaction tr, ObjectId tableId, ObjectId currentId, Func<T, JsonObject?> details)
        where T : SymbolTableRecord
    {
        var items = new JsonArray();
        foreach (ObjectId id in (SymbolTable)tr.GetObject(tableId, OpenMode.ForRead))
        {
            var r = (T)tr.GetObject(id, OpenMode.ForRead);
            var d = details(r);
            if (d == null) continue; // e.g. shape files in the text style table
            var o = new JsonObject { ["name"] = r.Name, ["current"] = id == currentId };
            foreach (var kv in d.ToList()) { d.Remove(kv.Key); o[kv.Key] = kv.Value; }
            items.Add(o);
        }
        return items;
    }

    private static JsonArray Dictionary(Transaction tr, ObjectId dictId, ObjectId currentId)
    {
        var items = new JsonArray();
        foreach (DBDictionaryEntry e in (DBDictionary)tr.GetObject(dictId, OpenMode.ForRead))
            items.Add(new JsonObject { ["name"] = e.Key, ["current"] = e.Value == currentId });
        return items;
    }

    /// <summary>Insertions of a block, counting the anonymous copies of dynamic blocks.</summary>
    private static int CountReferences(BlockTableRecord b, Transaction tr)
    {
        int n = b.GetBlockReferenceIds(true, false).Count;
        if (b.IsDynamicBlock)
            foreach (ObjectId anon in b.GetAnonymousBlockIds())
                n += ((BlockTableRecord)tr.GetObject(anon, OpenMode.ForRead)).GetBlockReferenceIds(true, false).Count;
        return n;
    }

    /// <summary>Entities per layer in model space and all layouts (not inside block definitions).</summary>
    private static Dictionary<string, int> CountByLayer(Database db, Transaction tr)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var all = new EntityFilter(new JsonObject());
        foreach (var space in all.Spaces(db, tr))
            foreach (ObjectId id in space)
                if (tr.GetObject(id, OpenMode.ForRead) is Entity e)
                    counts[e.Layer] = counts.TryGetValue(e.Layer, out int n) ? n + 1 : 1;
        return counts;
    }

    private static Regex? Wildcard(string? pattern) =>
        pattern == null ? null
            : new Regex("^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
