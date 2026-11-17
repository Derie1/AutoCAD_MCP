"""Step 6 checks: writing cells, changing table structure, creating tables.

Called by smoke_test.py after the step 3 fixture (main 5 x 3 table with a locked cell (3,0),
a merged title row, a formula in (4,1) and a block in (4,2); a small 2 x 2 table).
"""

from __future__ import annotations

from typing import Callable


def run(base: str, post: Callable, count: Callable, check: Callable) -> None:
    print("Table writing (step 6)")
    _, f = post(base, "find_entities", types="TABLE", text="*kW*")
    t = (f.get("items") or [{}])[0].get("handle")
    _, f = post(base, "find_entities", types="TABLE")
    small = next((i["handle"] for i in f.get("items", []) if i["handle"] != t), None)

    def table(**kw):
        return post(base, "get_table", handle=t, **kw)[1]

    # ---- cells
    s, r = post(base, "set_table_cells", handle=t, cells=[{"row": 2, "column": 1, "value": "6.5"}])
    check("set one cell", s == 200 and r.get("written") == 1 and table()["cells"][2][1] == "6.5", str(r))
    s, r = post(base, "set_table_cells", handle=t, start=[2, 1], values=[[1.5, 3], [2.5, 4]])
    v = table(rows=[2, 3], columns=[1, 2], format="value")["cells"]
    check("write a 2x2 block of numbers", s == 200 and v == [[1.5, 3], [2.5, 4]], str(v))
    s, r = post(base, "set_table_cells", handle=t, cells=[{"row": 4, "column": 1, "value": "=1+2"}])
    tb = table()
    check("formula =1+2 -> 3", s == 200 and tb["cells"][4][1] == "3"
          and any("1+2" in (x.get("formula") or "") for x in tb.get("formulas", [])), str(tb.get("formulas")))
    s, r = post(base, "set_table_cells", handle=t, cells=[{"row": 2, "column": 0, "value": None}])
    check("null clears a cell", s == 200 and table()["cells"][2][0] is None, str(r))

    before = table()["cells"][2][2]
    s, e = post(base, "set_table_cells", handle=t, cells=[{"row": 2, "column": 2, "value": "X"}, {"row": 3, "column": 0, "value": "Y"}])
    check("locked cell -> cell_locked, nothing written", s == 409 and e.get("code") == "cell_locked"
          and "(3, 0)" in e.get("error", "") and table()["cells"][2][2] == before, str(e))
    s, e = post(base, "set_table_cells", handle=t, cells=[{"row": 0, "column": 1, "value": "x"}])
    check("inside a merged range -> bad_request", s == 400 and "merged" in e.get("error", ""), str(e))
    s, e = post(base, "set_table_cells", handle=t, cells=[{"row": 4, "column": 2, "value": "x"}])
    check("block cell -> bad_request", s == 400 and "block" in e.get("error", ""), str(e))
    s, e = post(base, "set_table_cells", handle=t, start=[5, 0], values=[["P-3"]])
    check("past the end without add_rows -> bad_request", s == 400, str(e))
    s, r = post(base, "set_table_cells", handle=t, start=[5, 0], values=[["P-3", "1.1", "1"], ["P-4", "2.2", "2"]], add_rows=True)
    tb = table()
    check("add_rows appends 2 rows", r.get("rows_added") == 2 and tb["rows"] == 7 and tb["cells"][6] == ["P-4", "2.2", "2"], str(r))
    s, e = post(base, "set_table_cells", handle=t, cells=[{"row": 1, "column": 9, "value": "x"}])
    check("column outside -> bad_request", s == 400, str(e))
    s, e = post(base, "set_table_cells", values=[["x"]], start=[1, 1])
    check("no handle with several tables -> bad_request", s == 400, str(e))
    post(base, "set_selection", handles=[small])
    s, r = post(base, "set_table_cells", values=[["selected"]], start=[1, 1])
    _, sm = post(base, "get_table", handle=small)
    check("no handle uses the selected table", s == 200 and sm["cells"][1][1] == "selected", str(r))
    post(base, "set_selection", handles=[])

    # ---- structure
    s, r = post(base, "modify_table", handle=t, operation="insert_rows", at=2, count=1)
    check("insert_rows", s == 200 and r.get("rows") == 8 and table()["cells"][3][1] == "1.5", str(r))
    s, r = post(base, "modify_table", handle=t, operation="delete_rows", at=2, count=1)
    check("delete_rows", s == 200 and r.get("rows") == 7 and table()["cells"][2][1] == "1.5", str(r))
    s, e = post(base, "modify_table", handle=t, operation="delete_rows", at=3, count=1)
    check("delete a row with a locked cell -> cell_locked", s == 409 and e.get("code") == "cell_locked", str(e))
    s, r = post(base, "modify_table", handle=t, operation="insert_columns", at=3, width=25)
    check("insert_columns at the end", s == 200 and r.get("columns") == 4 and table()["column_widths"][3] == 25, str(r))
    s, r = post(base, "modify_table", handle=t, operation="delete_columns", at=3)
    check("delete_columns", s == 200 and r.get("columns") == 3, str(r))
    s, r = post(base, "modify_table", handle=t, operation="merge", range=[5, 0, 5, 1])
    check("merge cells", s == 200 and [5, 0, 5, 1] in table()["merged"], str(table()["merged"]))
    s, r = post(base, "modify_table", handle=t, operation="unmerge", range=[5, 0, 5, 1])
    check("unmerge cells", s == 200 and [5, 0, 5, 1] not in table()["merged"], str(table()["merged"]))
    s, r = post(base, "modify_table", handle=t, operation="set_column_width", at=0, width=55)
    check("set_column_width", s == 200 and table()["column_widths"][0] == 55, str(r))
    s, e = post(base, "modify_table", handle=t, operation="delete_rows", at=0, count=7)
    check("deleting every row -> bad_request", s == 400, str(e))
    s, e = post(base, "modify_table", handle=t, operation="shuffle")
    check("unknown operation -> bad_request", s == 400, str(e))

    # ---- create
    tables_before = count(base, types="TABLE")
    s, r = post(base, "create_table", position=[0, -200], title="Pumps", column_widths=[30, 20],
                data=[["Tag", "kW"], ["P-1", 5.5], ["P-2", "=2*3"]])
    h = r.get("handle")
    _, nt = post(base, "get_table", handle=h or "0")
    check("create_table 4 x 2 with title", s == 200 and r.get("rows") == 4 and r.get("columns") == 2, str(r))
    check("new table content", nt.get("cells", [[None]])[0][0] == "Pumps" and nt["cells"][1] == ["Tag", "kW"]
          and nt["cells"][3][1] == "6" and nt.get("row_styles", [])[:3] == ["_TITLE", "_HEADER", "_DATA"]
          and [0, 0, 0, 1] in nt.get("merged", []) and nt.get("column_widths") == [30, 20], str(nt)[:400])
    s, e = post(base, "create_table", position=[0, 0], data=[])
    check("create_table without data -> bad_request", s == 400, str(e))
    s, e = post(base, "create_table", position=[0, 0], data=[["a"]], style="NO_SUCH_STYLE")
    check("create_table unknown style -> not_found", s == 404, str(e))
    s, u = post(base, "undo")
    check("undo removes the new table", s == 200 and count(base, types="TABLE") == tables_before, str(u))
