using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;

namespace AcadMcpBridge.Handlers;

/// <summary>
/// Writing to AutoCAD tables. Locked cells (content locked or read-only, which includes cells
/// linked to Excel) are never written: the call fails before anything changes and names the
/// cells, so the user can unlock them in AutoCAD first. There is deliberately no override.
/// </summary>
internal static class TableWriteHandlers
{
    private const CellStates LockedStates = CellStates.ContentLocked | CellStates.ContentReadOnly;

    // ---------------------------------------------------------------- cells

    /// <summary>
    /// Writes cells: "cells" [{row, column, value}] or "values" (2D array) placed at "start"
    /// [row, column] — or at the cells the user has selected in the table when start is omitted.
    /// Values: text (MTEXT codes kept), numbers, "=formula", null = clear.
    /// "add_rows": append rows (copying the last row's format) when the values run past the end.
    /// </summary>
    public static JsonObject SetTableCells(JsonObject a)
    {
        var doc = JsonUtil.ActiveDocument();
        string? handle = Args.Str(a, "handle");
        bool addRows = Args.Bool(a, "add_rows");
        var writes = new List<(int r, int c, JsonNode? v)>();
        string tableHandle;
        int rowsNeeded;

        // Read-only phase: resolve the table and the cells, refuse before anything is locked.
        using (var tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
        {
            var table = TableHandlers.Open(tr, doc, handle);
            tableHandle = table.Handle.ToString();
            if (a["cells"] is JsonArray list)
            {
                foreach (var item in list)
                {
                    if (item is not JsonObject o) throw new BridgeException("bad_request", "each cell must be {row, column, value}");
                    writes.Add((Args.Int(o, "row", -1, 0), Args.Int(o, "column", -1, 0), o["value"]?.DeepClone()));
                    if (writes[^1].r < 0 || writes[^1].c < 0 || !o.ContainsKey("value"))
                        throw new BridgeException("bad_request", "each cell needs row, column and value (null clears)");
                }
            }
            else if (a["values"] is JsonArray rows)
            {
                int r0, c0;
                if (a["start"] is JsonArray st && st.Count == 2)
                {
                    try { r0 = st[0]!.GetValue<int>(); c0 = st[1]!.GetValue<int>(); }
                    catch { throw new BridgeException("bad_request", "'start' must be [row, column]"); }
                }
                else if (table.HasSubSelection && table.SubSelection is { } sel) { r0 = sel.TopRow; c0 = sel.LeftColumn; }
                else throw new BridgeException("bad_request", "'start' [row, column] is required (or select cells in the table in AutoCAD)");
                for (int i = 0; i < rows.Count; i++)
                {
                    if (rows[i] is not JsonArray row) throw new BridgeException("bad_request", "'values' must be a 2D array [[...], [...]]");
                    for (int j = 0; j < row.Count; j++) writes.Add((r0 + i, c0 + j, row[j]?.DeepClone()));
                }
            }
            else throw new BridgeException("bad_request", "pass 'cells' [{row, column, value}] or 'values' (2D array) with 'start'");
            if (writes.Count == 0) throw new BridgeException("bad_request", "nothing to write");

            int nRows = table.Rows.Count, nCols = table.Columns.Count;
            rowsNeeded = writes.Max(w => w.r) + 1;
            var badCol = writes.FirstOrDefault(w => w.c >= nCols);
            if (badCol != default) throw new BridgeException("bad_request", $"column {badCol.c} is outside the table (0..{nCols - 1})");
            if (rowsNeeded > nRows && !addRows)
                throw new BridgeException("bad_request", $"row {rowsNeeded - 1} is outside the table (0..{nRows - 1}); pass add_rows: true to append rows");

            var merges = TableHandlers.Merges(table);
            var locked = new List<string>();
            foreach (var (r, c, _) in writes.Where(w => w.r < nRows))
            {
                var m = TableHandlers.MergeOf(merges, r, c);
                if (m != null && (m[0] != r || m[1] != c))
                    throw new BridgeException("bad_request", $"cell ({r}, {c}) is inside the merged range starting at ({m[0]}, {m[1]}); write to that cell");
                var cell = table.Cells[r, c];
                if (((cell.State ?? CellStates.None) & LockedStates) != 0) locked.Add($"({r}, {c})");
                foreach (CellContent content in cell.Contents)
                    if (!content.BlockTableRecordId.IsNull)
                        throw new BridgeException("bad_request", $"cell ({r}, {c}) holds a block; it cannot be overwritten with text");
            }
            if (locked.Count > 0)
                throw new BridgeException("cell_locked",
                    $"{locked.Count} cell(s) are locked: {string.Join(", ", locked.Take(20))}. Locked cells (also cells linked to Excel) " +
                    "are not written. Ask the user to unlock them in AutoCAD (select the cells, right-click > Locking > Unlocked) " +
                    "and try again. Nothing was written.", 409);
            tr.Commit();
        }

        return Write.Run("set_table_cells", ctx =>
        {
            var table = (Table)Entities.OpenEntity(ctx.Tr, ctx.Db, tableHandle, OpenMode.ForWrite);
            table.SuppressRegenerateTable(true);
            int added = 0;
            if (rowsNeeded > table.Rows.Count)
            {
                added = rowsNeeded - table.Rows.Count;
                int last = table.Rows.Count - 1;
                table.InsertRowsAndInherit(table.Rows.Count, last, added);
            }
            foreach (var (r, c, v) in writes) WriteCell(table.Cells[r, c], v);
            table.SuppressRegenerateTable(false);
            table.RecomputeTableBlock(true);
            return new JsonObject { ["handle"] = tableHandle, ["written"] = writes.Count, ["rows_added"] = added, ["rows"] = table.Rows.Count };
        });
    }

    private static void WriteCell(Cell cell, JsonNode? v)
    {
        switch (v)
        {
            case null:
                cell.TextString = "";
                return;
            case JsonValue jv when jv.TryGetValue(out string? s):
                if (s.StartsWith('=') && s.Length > 1)
                {
                    if (cell.Contents.Count == 0) cell.Contents.Add();
                    cell.Contents[0].Formula = s;
                }
                else cell.TextString = s;
                return;
            case JsonValue jv when jv.TryGetValue(out double d):
                // SetValue(d, ParseOption…) leaves the cell empty; the Value property keeps the number
                // typed. If AutoCAD still has nothing, fall back to text.
                string asText = d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                cell.Value = d;
                if (cell.Value is null || (cell.Value is string sv && sv.Length == 0))
                {
                    cell.TextString = asText;
                    return;
                }
                // Without a format AutoCAD shows 15 decimals ("1.500000000000000"): show as many
                // decimals as the number was given with (decimal units). The format must be set on
                // the cell content; Cell.DataFormat does not change what is displayed.
                int dot = asText.IndexOf('.');
                int decimals = dot < 0 || asText.Contains('E') ? 0 : Math.Min(8, asText.Length - dot - 1);
                if (cell.Contents.Count > 0) cell.Contents[0].DataFormat = $"%lu2%pr{decimals}";
                return;
            case JsonValue jv when jv.TryGetValue(out bool b):
                cell.TextString = b ? "true" : "false";
                return;
            default:
                throw new BridgeException("bad_request", $"a cell value must be text, a number or null, not {v.ToJsonString()}");
        }
    }

    // ---------------------------------------------------------------- structure

    /// <summary>insert_rows / delete_rows / insert_columns / delete_columns / merge / unmerge.</summary>
    public static JsonObject ModifyTable(JsonObject a)
    {
        string op = (Args.Str(a, "operation", required: true) ?? "").ToLowerInvariant();
        var doc = JsonUtil.ActiveDocument();
        string tableHandle;
        using (var tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
        {
            var table = TableHandlers.Open(tr, doc, Args.Str(a, "handle"));
            tableHandle = table.Handle.ToString();
            Validate(table, op, a);
            tr.Commit();
        }

        return Write.Run("modify_table", ctx =>
        {
            var t = (Table)Entities.OpenEntity(ctx.Tr, ctx.Db, tableHandle, OpenMode.ForWrite);
            int at = Args.Int(a, "at", 0, 0), count = Args.Int(a, "count", 1, 1, 10000);
            switch (op)
            {
                case "insert_rows":
                    // inherit the format of the row above (or below when inserting at the top)
                    t.InsertRowsAndInherit(at, at > 0 ? at - 1 : 0, count);
                    if (Args.Num(a, "height") is double h)
                        for (int r = at; r < at + count; r++) t.Rows[r].Height = h;
                    break;
                case "delete_rows":
                    t.DeleteRows(at, count);
                    break;
                case "insert_columns":
                    t.InsertColumnsAndInherit(at, at > 0 ? at - 1 : 0, count);
                    if (Args.Num(a, "width") is double w)
                        for (int c = at; c < at + count; c++) t.Columns[c].Width = w;
                    break;
                case "delete_columns":
                    t.DeleteColumns(at, count);
                    break;
                case "merge":
                case "unmerge":
                    var rg = RangeArg(a, t);
                    var range = CellRange.Create(t, rg[0], rg[1], rg[2], rg[3]);
                    if (op == "merge") t.MergeCells(range); else t.UnmergeCells(range);
                    break;
                case "set_column_width":
                    t.Columns[at].Width = Args.Num(a, "width", required: true)!.Value;
                    break;
                case "set_row_height":
                    t.Rows[at].Height = Args.Num(a, "height", required: true)!.Value;
                    break;
            }
            t.RecomputeTableBlock(true);
            return new JsonObject { ["handle"] = tableHandle, ["operation"] = op, ["rows"] = t.Rows.Count, ["columns"] = t.Columns.Count };
        });
    }

    private static void Validate(Table t, string op, JsonObject a)
    {
        int nRows = t.Rows.Count, nCols = t.Columns.Count;
        int at = Args.Int(a, "at", 0, 0), count = Args.Int(a, "count", 1, 1, 10000);
        switch (op)
        {
            case "insert_rows":
                if (at > nRows) throw new BridgeException("bad_request", $"'at' must be 0..{nRows} (= append)");
                break;
            case "insert_columns":
                if (at > nCols) throw new BridgeException("bad_request", $"'at' must be 0..{nCols} (= append)");
                break;
            case "delete_rows":
            case "delete_columns":
                bool rows = op == "delete_rows";
                int n = rows ? nRows : nCols;
                if (at + count > n) throw new BridgeException("bad_request", $"rows/columns {at}..{at + count - 1} are outside 0..{n - 1}");
                if (count >= n) throw new BridgeException("bad_request", "a table must keep at least one row and one column; delete the table instead");
                var range = rows ? CellRange.Create(t, at, 0, at + count - 1, nCols - 1) : CellRange.Create(t, 0, at, nRows - 1, at + count - 1);
                if (rows ? !range.CanDeleteRows : !range.CanDeleteColumns)
                    throw new BridgeException("bad_request", "AutoCAD does not allow deleting these rows/columns (e.g. part of a data link)");
                for (int r = range.TopRow; r <= range.BottomRow; r++)
                    for (int c = range.LeftColumn; c <= range.RightColumn; c++)
                        if (((t.Cells[r, c].State ?? CellStates.None) & LockedStates) != 0)
                            throw new BridgeException("cell_locked", $"cell ({r}, {c}) is locked; unlock it in AutoCAD before deleting. Nothing was changed.", 409);
                break;
            case "merge":
            case "unmerge":
                RangeArg(a, t);
                break;
            case "set_column_width":
                if (at >= nCols) throw new BridgeException("bad_request", $"column {at} is outside 0..{nCols - 1}");
                if (Args.Num(a, "width", required: true) <= 0) throw new BridgeException("bad_request", "'width' must be > 0");
                break;
            case "set_row_height":
                if (at >= nRows) throw new BridgeException("bad_request", $"row {at} is outside 0..{nRows - 1}");
                if (Args.Num(a, "height", required: true) <= 0) throw new BridgeException("bad_request", "'height' must be > 0");
                break;
            default:
                throw new BridgeException("bad_request",
                    "'operation' must be insert_rows, delete_rows, insert_columns, delete_columns, merge, unmerge, set_column_width or set_row_height");
        }
    }

    private static int[] RangeArg(JsonObject a, Table t)
    {
        if (a["range"] is not JsonArray arr || arr.Count != 4)
            throw new BridgeException("bad_request", "'range' must be [top_row, left_column, bottom_row, right_column]");
        int[] r;
        try { r = arr.Select(n => n!.GetValue<int>()).ToArray(); }
        catch { throw new BridgeException("bad_request", "'range' must hold 4 integers"); }
        if (r[0] < 0 || r[1] < 0 || r[2] < r[0] || r[3] < r[1] || r[2] >= t.Rows.Count || r[3] >= t.Columns.Count)
            throw new BridgeException("bad_request", $"'range' is outside the table ({t.Rows.Count} x {t.Columns.Count})");
        return r;
    }

    // ---------------------------------------------------------------- create

    /// <summary>
    /// New table from data: optional title row (merged), the first data row as header (header: true),
    /// column widths (one number or a list), row height, table style, layer.
    /// </summary>
    public static JsonObject CreateTable(JsonObject a)
    {
        if (a["data"] is not JsonArray data || data.Count == 0 || data.Any(r => r is not JsonArray))
            throw new BridgeException("bad_request", "'data' must be a non-empty 2D array [[...], [...]]");
        int nCols = data.Max(r => ((JsonArray)r!).Count);
        if (nCols == 0) throw new BridgeException("bad_request", "'data' has no columns");
        string? title = Args.Str(a, "title");
        bool header = Args.Bool(a, "header", true);
        var pos = Args.Point(a, "position", required: true)!.Value;

        return Write.Run("create_table", ctx =>
        {
            ObjectId styleId = ctx.Db.Tablestyle;
            if (Args.Str(a, "style") is string sn)
            {
                var dict = (DBDictionary)ctx.Tr.GetObject(ctx.Db.TableStyleDictionaryId, OpenMode.ForRead);
                if (!dict.Contains(sn)) throw new BridgeException("not_found", $"table style '{sn}' does not exist (see list_styles)", 404);
                styleId = dict.GetAt(sn);
            }

            int titleRows = title != null ? 1 : 0;
            int nRows = titleRows + data.Count;
            var t = new Table();
            t.SetDatabaseDefaults(ctx.Db);
            t.TableStyle = styleId;
            t.Position = pos;
            t.SetSize(nRows, nCols);
            Props.ApplyCommon(ctx, t, a);
            ObjectId? textStyle = Args.Str(a, "text_style") is string tsn ? StyleHandlers.TextStyleId(ctx, tsn) : null;
            double? textHeight = Args.Num(a, "text_height");
            if (textHeight is <= 0) throw new BridgeException("bad_request", "'text_height' must be > 0");

            double? rowHeight = Args.Num(a, "row_height");
            for (int r = 0; r < nRows; r++)
            {
                t.Rows[r].Style = r < titleRows ? "_TITLE" : r == titleRows && header ? "_HEADER" : "_DATA";
                if (rowHeight is > 0) t.Rows[r].Height = rowHeight.Value;
            }
            var widths = a["column_widths"];
            for (int c = 0; c < nCols; c++)
            {
                double? w = widths switch
                {
                    JsonArray arr when c < arr.Count => arr[c]!.GetValue<double>(),
                    JsonValue v when v.TryGetValue(out double one) => one,
                    _ => null,
                };
                if (w is > 0) t.Columns[c].Width = w.Value;
            }

            t.SuppressRegenerateTable(true);
            if (title != null)
            {
                if (nCols > 1) t.MergeCells(CellRange.Create(t, 0, 0, 0, nCols - 1));
                t.Cells[0, 0].TextString = title;
            }
            for (int i = 0; i < data.Count; i++)
            {
                var row = (JsonArray)data[i]!;
                for (int c = 0; c < row.Count; c++) WriteCell(t.Cells[titleRows + i, c], row[c]?.DeepClone());
            }
            // Overrides for the whole table; row heights grow with the text in GenerateLayout.
            if (textStyle is ObjectId tsId) t.Cells.TextStyleId = tsId;
            if (textHeight is double th) t.Cells.TextHeight = th;
            t.SuppressRegenerateTable(false);
            t.GenerateLayout();
            ctx.Add(ctx.CurrentSpace(), t);
            t.RecomputeTableBlock(true);
            return new JsonObject
            {
                ["handle"] = t.Handle.ToString(),
                ["rows"] = t.Rows.Count,
                ["columns"] = t.Columns.Count,
                ["position"] = JsonUtil.Point(t.Position),
                ["table_style"] = t.TableStyleName,
                ["bounds"] = Entities.Bounds(t),
            };
        });
    }
}
