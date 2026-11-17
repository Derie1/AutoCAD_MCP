using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;

namespace AcadMcpBridge.Handlers;

/// <summary>AutoCAD tables (ACAD_TABLE). Reading here; writing in step 6.</summary>
internal static class TableHandlers
{
    /// <summary>Cells per call when no range is given; larger tables are read in row ranges.</summary>
    public const int MaxCellsPerCall = 5000;

    /// <summary>
    /// Contents of a table. Arguments: handle (or none = the one table the user has selected),
    /// rows / columns = [first, last] (0-based, inclusive), format = plain | raw | value, details.
    /// </summary>
    public static JsonObject GetTable(JsonObject a)
    {
        var doc = JsonUtil.ActiveDocument();
        var db = doc.Database;
        string format = Args.Str(a, "format") ?? "plain";
        if (format is not ("plain" or "raw" or "value"))
            throw new BridgeException("bad_request", "'format' must be \"plain\", \"raw\" or \"value\"");
        bool details = Args.Bool(a, "details");

        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var table = Open(tr, doc, Args.Str(a, "handle"));
        int nRows = table.Rows.Count, nCols = table.Columns.Count;

        var (c1, c2) = Range(a, "columns", nCols);
        bool rowsGiven = a["rows"] != null;
        var (r1, r2) = Range(a, "rows", nRows);
        bool truncated = false;
        int width = c2 - c1 + 1;
        if (!rowsGiven && (r2 - r1 + 1) * width > MaxCellsPerCall)
        {
            r2 = r1 + Math.Max(1, MaxCellsPerCall / width) - 1;
            truncated = true;
        }

        var cells = new JsonArray();
        var allMerges = Merges(table);
        var formulas = new JsonArray();
        var locked = new JsonArray();
        var blocks = new JsonArray();
        for (int r = r1; r <= r2; r++)
        {
            var row = new JsonArray();
            for (int c = c1; c <= c2; c++)
            {
                Cell cell = table.Cells[r, c];
                var mr = MergeOf(allMerges, r, c);
                bool anchor = mr == null || (mr[0] == r && mr[1] == c);

                JsonNode? value = anchor ? CellValue(cell, format) : null;
                var state = cell.State ?? CellStates.None;
                bool isLocked = (state & (CellStates.ContentLocked | CellStates.ContentReadOnly)) != 0;
                if (isLocked && anchor) locked.Add(new JsonObject { ["row"] = r, ["column"] = c, ["state"] = state.ToString() });

                string? formula = null;
                if (anchor)
                    foreach (CellContent content in cell.Contents)
                    {
                        if (content.HasFormula) formula ??= content.Formula;
                        if (!content.BlockTableRecordId.IsNull)
                            blocks.Add(BlockCell(r, c, content, tr));
                    }
                if (formula != null)
                    formulas.Add(new JsonObject { ["row"] = r, ["column"] = c, ["formula"] = formula, ["value"] = value?.DeepClone() });

                if (!details) { row.Add(value); continue; }
                var d = new JsonObject { ["value"] = value };
                if (!anchor) d["merged_into"] = new JsonArray(mr![0], mr[1]);
                if (formula != null) d["formula"] = formula;
                if (isLocked) d["locked"] = true;
                if (cell.IsLinked == true) d["linked"] = true;
                if (cell.DataType is { } dt && dt.DataType != DataType.Unknown) d["data_type"] = dt.DataType.ToString();
                row.Add(d);
            }
            cells.Add(row);
        }

        var result = new JsonObject
        {
            ["handle"] = table.Handle.ToString(),
            ["layer"] = table.Layer,
            ["space"] = Entities.SpaceName(table.OwnerId, tr),
            ["position"] = JsonUtil.Point(table.Position),
            ["table_style"] = table.TableStyleName,
            ["rows"] = nRows,
            ["columns"] = nCols,
            ["range"] = new JsonObject { ["rows"] = new JsonArray(r1, r2), ["columns"] = new JsonArray(c1, c2) },
            ["truncated"] = truncated,
            ["row_styles"] = new JsonArray(Enumerable.Range(r1, r2 - r1 + 1).Select(r => (JsonNode?)table.Rows[r].Style).ToArray()),
            ["row_heights"] = new JsonArray(Enumerable.Range(r1, r2 - r1 + 1).Select(r => (JsonNode?)Math.Round(table.Rows[r].Height, 6)).ToArray()),
            ["column_widths"] = new JsonArray(Enumerable.Range(c1, c2 - c1 + 1).Select(c => (JsonNode?)Math.Round(table.Columns[c].Width, 6)).ToArray()),
            ["cells"] = cells,
            // merged ranges that touch the range read
            ["merged"] = new JsonArray(allMerges.Where(m => m[0] <= r2 && m[2] >= r1 && m[1] <= c2 && m[3] >= c1)
                                                .Select(m => (JsonNode?)new JsonArray(m[0], m[1], m[2], m[3])).ToArray()),
            ["formulas"] = formulas,
            ["locked_cells"] = locked,
            ["block_cells"] = blocks,
            ["data_links"] = DataLinks(table, tr),
        };
        if (truncated)
            result["hint"] = $"table has {nRows} rows; read the rest with rows=[{r2 + 1}, {nRows - 1}] (in parts if large)";
        if (table.HasSubSelection && table.SubSelection is { } sel)
            result["selected_cells"] = new JsonArray(sel.TopRow, sel.LeftColumn, sel.BottomRow, sel.RightColumn);
        tr.Commit();
        return result;
    }

    /// <summary>
    /// Table by handle; with no handle, the single table in the user's selection, or the single
    /// table in the drawing.
    /// </summary>
    public static Table Open(Transaction tr, Autodesk.AutoCAD.ApplicationServices.Document doc, string? handle, OpenMode mode = OpenMode.ForRead)
    {
        var db = doc.Database;
        if (handle != null)
            return Entities.OpenEntity(tr, db, handle, mode) as Table
                   ?? throw new BridgeException("bad_request", $"handle {handle} is not an AutoCAD table (ACAD_TABLE)");

        var sel = doc.Editor.SelectImplied();
        var selected = sel.Status == PromptStatus.OK && sel.Value != null
            ? sel.Value.GetObjectIds().Where(id => id.ObjectClass.DxfName == "ACAD_TABLE").ToList()
            : [];
        if (selected.Count == 1) return (Table)tr.GetObject(selected[0], mode);
        if (selected.Count > 1)
            throw new BridgeException("bad_request", $"{selected.Count} tables are selected; pass 'handle'");

        var all = new EntityFilter(new JsonObject { ["types"] = "ACAD_TABLE" }).Collect(db, tr);
        if (all.Count == 1) return (Table)tr.GetObject(all[0], mode);
        throw new BridgeException(all.Count == 0 ? "not_found" : "bad_request",
            all.Count == 0 ? "the drawing has no AutoCAD tables" : $"the drawing has {all.Count} tables; pass 'handle' (see find_entities types=TABLE)",
            all.Count == 0 ? 404 : 400);
    }

    private static (int, int) Range(JsonObject a, string name, int count)
    {
        if (a[name] is not JsonArray arr) return (0, count - 1);
        int first, last;
        try { first = arr[0]!.GetValue<int>(); last = arr.Count > 1 ? arr[1]!.GetValue<int>() : first; }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or NullReferenceException or ArgumentOutOfRangeException)
        { throw new BridgeException("bad_request", $"'{name}' must be [first, last] (0-based)"); }
        if (first < 0 || last < first || last >= count)
            throw new BridgeException("bad_request", $"'{name}' [{first}, {last}] is outside 0..{count - 1}");
        return (first, last);
    }

    /// <summary>
    /// Merged ranges of the whole table as [top, left, bottom, right]. AutoCAD reports
    /// IsMerged / GetMergeRange reliably only on the top-left cell of a merge, so the ranges are
    /// collected from those cells and the other cells are looked up in the list.
    /// </summary>
    public static List<int[]> Merges(Table t)
    {
        var list = new List<int[]>();
        for (int r = 0; r < t.Rows.Count; r++)
            for (int c = 0; c < t.Columns.Count; c++)
            {
                var cell = t.Cells[r, c];
                if (cell.IsMerged != true) continue;
                CellRange mr;
                try { mr = cell.GetMergeRange(); } catch { continue; }
                if (mr == null || mr.IsNull || mr.TopRow != r || mr.LeftColumn != c) continue;
                if (mr.BottomRow == r && mr.RightColumn == c) continue;
                list.Add([mr.TopRow, mr.LeftColumn, mr.BottomRow, mr.RightColumn]);
            }
        return list;
    }

    public static int[]? MergeOf(List<int[]> merges, int r, int c) =>
        merges.FirstOrDefault(m => r >= m[0] && r <= m[2] && c >= m[1] && c <= m[3]);

    private static JsonNode? CellValue(Cell cell, string format)
    {
        try
        {
            switch (format)
            {
                case "raw": return cell.TextString;
                case "value":
                    var v = cell.Value;
                    return v is string s && s.Length == 0 ? null : JsonUtil.FromObject(v);
                default:
                    var text = cell.GetTextString(FormatOption.IgnoreMtextFormat);
                    return string.IsNullOrEmpty(text) ? null : text;
            }
        }
        catch { return null; } // unknown content types
    }

    private static JsonObject BlockCell(int r, int c, CellContent content, Transaction tr)
    {
        var btr = (BlockTableRecord)tr.GetObject(content.BlockTableRecordId, OpenMode.ForRead);
        var attrs = new JsonObject();
        foreach (ObjectId id in btr)
            if (id.ObjectClass.DxfName == "ATTDEF" && tr.GetObject(id, OpenMode.ForRead) is AttributeDefinition ad && !ad.Constant)
                attrs[ad.Tag] = content.GetBlockAttributeValue(id);
        return new JsonObject { ["row"] = r, ["column"] = c, ["block"] = btr.Name, ["attributes"] = attrs };
    }

    private static JsonArray DataLinks(Table table, Transaction tr)
    {
        var list = new JsonArray();
        foreach (ObjectId id in table.GetDataLink())
            if (tr.GetObject(id, OpenMode.ForRead) is DataLink dl)
                list.Add(new JsonObject { ["name"] = dl.Name, ["source"] = dl.ConnectionString });
        return list;
    }

    /// <summary>
    /// Attribute values of block references as a table: columns = handle, block, space, layer,
    /// position, then one column per attribute tag. Same filters as find_entities.
    /// </summary>
    public static JsonObject ExtractBlockAttributes(JsonObject a)
    {
        var filterArgs = (JsonObject)a.DeepClone();
        filterArgs["types"] = "INSERT";
        var filter = new EntityFilter(filterArgs);
        var onlyTags = Args.StrList(a, "tags");
        int offset = Args.Int(a, "offset", 0, 0);
        int limit = Args.Int(a, "limit", 500, 1, 5000);

        var db = JsonUtil.ActiveDocument().Database;
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var ids = filter.Collect(db, tr);
        var withAttrs = ids.Where(id => ((BlockReference)tr.GetObject(id, OpenMode.ForRead)).AttributeCollection.Count > 0).ToList();

        var tags = new List<string>();
        var rows = new List<(BlockReference br, Dictionary<string, string> values)>();
        foreach (var id in withAttrs.Skip(offset).Take(limit))
        {
            var br = (BlockReference)tr.GetObject(id, OpenMode.ForRead);
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId aid in br.AttributeCollection)
            {
                if (tr.GetObject(aid, OpenMode.ForRead) is not AttributeReference ar) continue;
                if (onlyTags != null && !onlyTags.Contains(ar.Tag, StringComparer.OrdinalIgnoreCase)) continue;
                values[ar.Tag] = ar.IsMTextAttribute ? ar.MTextAttribute.Text : ar.TextString;
                if (!tags.Contains(ar.Tag, StringComparer.OrdinalIgnoreCase)) tags.Add(ar.Tag);
            }
            rows.Add((br, values));
        }

        var columns = new JsonArray("handle", "block", "space", "layer", "position");
        foreach (var t in tags) columns.Add(t);
        var data = new JsonArray();
        foreach (var (br, values) in rows)
        {
            var row = new JsonArray(br.Handle.ToString(), Entities.EffectiveBlockName(br, tr),
                                    Entities.SpaceName(br.OwnerId, tr), br.Layer, JsonUtil.Point(br.Position));
            foreach (var t in tags) row.Add(values.TryGetValue(t, out var v) ? v : null);
            data.Add(row);
        }
        tr.Commit();
        return new JsonObject
        {
            ["total"] = withAttrs.Count,
            ["offset"] = offset,
            ["count"] = data.Count,
            ["has_more"] = offset + data.Count < withAttrs.Count,
            ["columns"] = columns,
            ["rows"] = data,
        };
    }
}
