"""Smoke test for the AutoCAD MCP bridge.

Needs AutoCAD 2025-2027 running with a drawing open and MCPSTART typed.

    .venv\\Scripts\\python.exe tests\\smoke_test.py            # all checks
    .venv\\Scripts\\python.exe tests\\smoke_test.py --port 8766

Checks:
  1. HTTP: /health, every route, unknown route, bad JSON.
  2. Concurrency: 100 parallel get_document_info calls must all succeed.
  3. Write tools (create, undo): ONLY in a new, unnamed, empty drawing (Drawing1.dwg with
     0 objects); skipped otherwise, so real drawings are never touched. Everything the
     test creates is undone at the end.
  4. MCP: spawn "dotnet bin\\Release\\Server\\acad_mcp_server.dll" over stdio, list tools, call the
     read-only ones, check that bridge errors and arguments pass through.
"""

from __future__ import annotations

import argparse
import asyncio
import json
import os
import sys
import time
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SERVER = ROOT / "bin" / "Release" / "Server" / "acad_mcp_server.dll"  # built by Build-Package.ps1

passed: list[str] = []
failed: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    (passed if cond else failed).append(name)
    print(f"  [{'PASS' if cond else 'FAIL'}] {name}" + (f" - {detail}" if detail and not cond else ""))


def http(base: str, method: str, route: str, body: bytes | None = None) -> tuple[int, dict]:
    req = urllib.request.Request(f"{base}/{route}", data=body, method=method)
    req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return r.status, json.loads(r.read())
    except urllib.error.HTTPError as e:
        return e.code, json.loads(e.read())


def test_http(base: str) -> None:
    print("HTTP")
    status, h = http(base, "GET", "health")
    check("health ok", status == 200 and h.get("ok") is True, str(h))
    check("health product=AutoCAD", h.get("product") == "AutoCAD", str(h))
    check("health reports whether commands are allowed",
          isinstance(h.get("commands_allowed"), bool)
          and h.get("commands_source") in ("off", "config", "environment", "session")
          and h.get("commands_allowed") == (h.get("commands_source") != "off"), str(h))
    check("ribbon panel on the Home tab", h.get("ribbon") == "Home tab", str(h.get("ribbon")))
    print(f"    {h.get('host_product')} {h.get('acad_build')}, build {h.get('build_series')}, "
          f".NET {h.get('dotnet')}, {h.get('routes')} routes")

    status, d = http(base, "POST", "get_document_info", b"{}")
    check("get_document_info ok", status == 200 and d.get("ok") is True, str(d))
    for key in ("name", "insunits", "current_layer", "counts", "open_documents"):
        check(f"get_document_info has {key}", key in d, str(d))
    print(f"    {d.get('name')}: {d.get('counts')}")

    status, e = http(base, "POST", "no_such_tool", b"{}")
    check("unknown route -> 404 unknown_tool", status == 404 and e.get("code") == "unknown_tool", str(e))

    status, e = http(base, "POST", "get_document_info", b"{not json")
    check("bad JSON -> 400 bad_request", status == 400 and e.get("code") == "bad_request", str(e))


def test_concurrency(base: str, n: int = 100) -> None:
    print(f"Concurrency ({n} parallel get_document_info)")
    t0 = time.perf_counter()
    with ThreadPoolExecutor(max_workers=32) as pool:
        results = list(pool.map(lambda _: http(base, "POST", "get_document_info", b"{}"), range(n)))
    dt = time.perf_counter() - t0
    ok = sum(1 for s, r in results if s == 200 and r.get("ok"))
    check(f"{n}/{n} succeeded", ok == n, f"{ok}/{n}")
    print(f"    {dt:.2f} s total, {dt / n * 1000:.0f} ms per call")
    status, h = http(base, "GET", "health")
    check("AutoCAD still answers after the burst", status == 200 and h.get("ok") is True)


READ_ONLY_TOOLS = {"health_check", "get_document_info", "find_entities", "get_selection", "list_documents",
                   "list_layers", "list_blocks", "list_layouts", "list_xrefs", "list_styles",
                   "extract_block_attributes"}


def post(base: str, route: str, /, **args) -> tuple[int, dict]:
    return http(base, "POST", route, json.dumps(args).encode("utf-8"))


def count(base: str, /, **filters) -> int:
    _, r = post(base, "find_entities", **filters)
    return r.get("total", -1)


def is_fresh(base: str) -> bool:
    """True only for a new, unnamed, empty drawing: the only place the test may change anything."""
    _, d = post(base, "get_document_info")
    if d.get("is_saved_to_file") or d.get("counts", {}).get("model_space_entities") != 0:
        print(f"    SKIPPED: active drawing {d.get('name')} is not a new empty drawing")
        return False
    return True


def test_write(base: str) -> None:
    print("Write tools (step 1: find_entities, create_line, undo)")

    check("find_entities on empty drawing -> 0", count(base) == 0)
    s, a = post(base, "create_line", start=[0, 0], end=[100, 0])
    check("create_line ok", s == 200 and a.get("type") == "LINE" and a.get("length") == 100, str(a))
    s, b = post(base, "create_line", start=[0, 50, 0], end=[30, 90], layer="0", color=1)
    check("create_line with layer + color", s == 200 and b.get("color") == 1 and b.get("layer") == "0", str(b))
    check("find_entities types=LINE -> 2", count(base, types="LINE") == 2)
    check("find_entities window inside -> 1",
          count(base, window={"min": [-1, -1], "max": [101, 1]}) == 1)
    check("find_entities window crossing -> 2",
          count(base, window={"min": [-1, -1], "max": [10, 60], "mode": "crossing"}) == 2)
    check("find_entities layers wildcard", count(base, layers="0*") == 2)
    check("find_entities types=TABLE -> 0", count(base, types="TABLE") == 0)
    s, p = post(base, "find_entities", limit=1)
    check("paging: limit 1 of 2", p.get("count") == 1 and p.get("has_more") is True, str(p))
    items = post(base, "find_entities")[1].get("items", [])
    check("handles are returned", all(i.get("handle") for i in items) and len(items) == 2)

    s, e = post(base, "create_line", start=[1, 1], end=[1, 1])
    check("create_line same points -> bad_request", s == 400 and e.get("code") == "bad_request", str(e))
    s, e = post(base, "create_line", start=[0, 0], end=[1, 1], layer="NO_SUCH_LAYER")
    check("create_line unknown layer -> not_found", s == 404, str(e))
    s, e = post(base, "find_entities", window={"min": [0, 0], "max": [1, 1], "mode": "around"})
    check("find_entities bad window mode -> bad_request", s == 400, str(e))
    check("failed calls left nothing behind", count(base) == 2)

    s, e = post(base, "undo", steps=5)
    check("undo more than recorded -> bad_request", s == 400, str(e))
    s, u = post(base, "undo")
    check("undo ok", s == 200 and u.get("undone") == 1, str(u))
    check("one undo reverts exactly one call -> 1 line left", count(base) == 1)
    s, u = post(base, "undo")
    check("second undo -> 0 lines", s == 200 and count(base) == 0, str(u))
    s, e = post(base, "undo")
    check("undo with nothing recorded -> nothing_to_undo", s == 409 and e.get("code") == "nothing_to_undo", str(e))


def by_name(items: list[dict], name: str) -> dict:
    return next((i for i in items if i.get("name") == name), {})


def test_read(base: str) -> None:
    print("Read tools (step 2) on a COM-built fixture")
    import fixture
    doc = fixture.active_document()
    fx = fixture.build_step2(doc)

    _, r = post(base, "list_documents")
    check("list_documents: one active", sum(1 for x in r.get("documents", []) if x.get("active")) == 1, str(r))

    _, r = post(base, "list_layers")
    layers = r.get("layers", [])
    a = by_name(layers, "MCP_A")
    check("list_layers: MCP_A red with 2 entities", a.get("color") == 1 and a.get("entities") == 2, str(a))
    check("list_layers: MCP_FROZEN frozen", by_name(layers, "MCP_FROZEN").get("frozen") is True)
    lk = by_name(layers, "MCP_LOCKED")
    check("list_layers: MCP_LOCKED locked, 1 entity", lk.get("locked") is True and lk.get("entities") == 1, str(lk))
    check("list_layers: layer 0 current", by_name(layers, "0").get("current") is True)
    _, r = post(base, "list_layers", name="MCP_*", with_counts=False)
    check("list_layers name filter -> 3", r.get("count") == 3, str(r))

    _, r = post(base, "list_blocks")
    b = by_name(r.get("blocks", []), "MCP_BLK")
    attrs = b.get("attributes", [])
    check("list_blocks: MCP_BLK, 1 reference, attribute NAME",
          b.get("references") == 1 and attrs and attrs[0].get("tag") == "NAME" and attrs[0].get("default") == "X", str(b))

    _, r = post(base, "list_layouts")
    lays = r.get("layouts", [])
    check("list_layouts: Model + 2 layouts, Model current",
          len(lays) == 3 and lays[0].get("model") and lays[0].get("current"), str(r)[:300])
    _, r = post(base, "list_xrefs")
    check("list_xrefs: none", r.get("count") == 0, str(r))

    _, r = post(base, "list_styles")
    check("list_styles: all five kinds", set(r) >= {"text", "dimension", "table", "multileader", "linetype"}, str(list(r)))
    check("list_styles: a current text style", any(s.get("current") for s in r.get("text", [])), str(r.get("text")))
    s, r = post(base, "list_styles", kinds=["text"])
    check("list_styles kinds=text only", "text" in r and "table" not in r, str(list(r)))
    s, e = post(base, "list_styles", kinds=["fonts"])
    check("list_styles bad kind -> bad_request", s == 400, str(e))

    _, r = post(base, "get_system_variable", names=["INSUNITS", "clayer", "LIMMAX", "NO_SUCH_VAR"])
    v = r.get("values", {})
    check("get_system_variable values", isinstance(v.get("INSUNITS"), int) and v.get("CLAYER") == "0"
          and isinstance(v.get("LIMMAX"), list) and "error" in v.get("NO_SUCH_VAR", {}), str(v))

    check("find text=Hello* -> 1", count(base, text="Hello*") == 1)
    check("find text=*second* (MTEXT) -> 1", count(base, text="*second*") == 1)
    check("find text=PUMP-1 (attribute) -> 1", count(base, text="PUMP-1") == 1)
    check("find blocks=MCP_* -> 1", count(base, blocks="MCP_*") == 1)
    check("find types=TEXT_ANY -> 2", count(base, types="TEXT_ANY") == 2)
    check("find space=model -> 6", count(base, space="model") == fx["model_entities"])
    check("find space=paper -> 0", count(base, space="paper") == 0)
    s, e = post(base, "find_entities", space="NoSuchLayout")
    check("find unknown layout -> not_found", s == 404, str(e))

    _, r = post(base, "get_entity_properties",
                handles=[fx["circle_handle"], fx["polyline_handle"], fx["block_ref_handle"], "FFFFFF", "xyz"])
    items = r.get("items", [])
    c, pl, br, missing, bad = (items + [{}] * 5)[:5]
    check("props: circle r=5 at [200,0,0]", c.get("radius") == 5 and c.get("center") == [200, 0, 0], str(c))
    check("props: polyline closed, 4 vertices, bulge 0.5",
          pl.get("closed") is True and len(pl.get("vertices", [])) == 4 and pl["vertices"][0].get("bulge") == 0.5, str(pl))
    battrs = br.get("attributes", [])
    check("props: block ref MCP_BLK with NAME=PUMP-1",
          br.get("block") == "MCP_BLK" and battrs and battrs[0].get("value") == "PUMP-1", str(br))
    check("props: unknown handle -> item error not_found", missing.get("code") == "not_found", str(missing))
    check("props: bad handle -> item error bad_request", bad.get("code") == "bad_request", str(bad))

    # Selection goes through the bridge, not COM SendCommand: after a COM SendCommand AutoCAD can
    # hold back the undo tool's command for good (PLAN section 10).
    post(base, "set_selection", handles=[])
    _, r = post(base, "get_selection")
    check("get_selection: nothing selected -> 0", r.get("total") == 0, str(r))
    _, every = post(base, "find_entities", space="model", limit=500)
    post(base, "set_selection", handles=[i["handle"] for i in every.get("items", [])
                                         if i.get("layer") not in ("MCP_FROZEN", "MCP_LOCKED")])
    _, r = post(base, "get_selection")
    check("get_selection after select all -> >= 5", r.get("total", 0) >= 5, str(r)[:300])
    post(base, "set_selection", handles=[])


def test_tables(base: str) -> None:
    print("Tables and attributes (step 3) on a COM-built fixture")
    import fixture
    doc = fixture.active_document()
    fx = fixture.build_step3(doc)
    h = fx["table_handle"]

    check("find types=TABLE -> 2", count(base, types="TABLE") == 2)
    check("find text=*kW* finds the table", count(base, types="TABLE", text="*kW*") == 1)

    s, r = post(base, "get_table", handle=h)
    cells = r.get("cells", [])
    check("get_table ok, 5 x 3", s == 200 and r.get("rows") == 5 and r.get("columns") == 3 and len(cells) == 5, str(r)[:400])
    if len(cells) < 5:
        return
    check("title in [0][0], merged cells null", cells[0][0] == "Equipment list" and cells[0][1] is None and cells[0][2] is None, str(cells[0]))
    check("merged range [0,0,0,2]", [0, 0, 0, 2] in r.get("merged", []), str(r.get("merged")))
    check("header row", cells[1] == ["Tag", "Power, kW", "Qty"], str(cells[1]))
    check("plain text drops MTEXT codes", cells[2][0] == "P-1", str(cells[2]))
    check("row styles title/header/data", r.get("row_styles", [])[:3] == ["_TITLE", "_HEADER", "_DATA"], str(r.get("row_styles")))
    f = r.get("formulas", [])
    check("formula =2+3 with value 5", f and f[0].get("row") == 4 and f[0].get("column") == 1
          and "2+3" in (f[0].get("formula") or "") and "5" in str(f[0].get("value")), str(f))
    lk = r.get("locked_cells", [])
    check("locked cell (3,0)", any(x.get("row") == 3 and x.get("column") == 0 for x in lk), str(lk))
    bc = r.get("block_cells", [])
    check("block cell (4,2) MCP_BLK", any(x.get("row") == 4 and x.get("column") == 2 and x.get("block") == "MCP_BLK" for x in bc), str(bc))

    _, raw = post(base, "get_table", handle=h, rows=[2, 2], columns=[0, 0], format="raw")
    raw_text = (raw.get("cells") or [[""]])[0][0] or ""
    check("raw format keeps codes", raw_text.startswith("{" + chr(92) + "f"), raw_text)
    _, val = post(base, "get_table", handle=h, rows=[2, 3], columns=[1, 2], format="value")
    check("range read 2x2", val.get("range") == {"rows": [2, 3], "columns": [1, 2]} and len(val.get("cells", [])) == 2, str(val)[:300])
    _, det = post(base, "get_table", handle=h, rows=[0, 4], columns=[0, 2], details=True)
    dc = det.get("cells", [[{}] * 3] * 5)
    check("details: merged_into and locked", dc[0][1].get("merged_into") == [0, 0] and dc[3][0].get("locked") is True, str(dc[:1])[:300])
    s, e = post(base, "get_table", handle=h, rows=[3, 9])
    check("rows out of range -> bad_request", s == 400, str(e))
    s, e = post(base, "get_table", handle=fx["block_handle"] if "block_handle" in fx else "1")
    check("not a table -> error", s in (400, 404), str(e))
    s, e = post(base, "get_table")
    check("no handle with 2 tables -> bad_request", s == 400 and "2 tables" in e.get("error", ""), str(e))

    _, ex = post(base, "extract_block_attributes")
    cols = ex.get("columns", [])
    rows = ex.get("rows", [])
    check("extract_block_attributes: NAME column, PUMP-1",
          "NAME" in cols and rows and rows[0][cols.index("NAME")] == "PUMP-1" and rows[0][1] == "MCP_BLK", str(ex)[:300])
    _, ex = post(base, "extract_block_attributes", blocks="NO_SUCH*")
    check("extract_block_attributes: no match -> 0", ex.get("total") == 0, str(ex))


async def test_mcp(port: int) -> None:
    from mcp.client.session import ClientSession
    from mcp.client.stdio import StdioServerParameters, stdio_client

    print(f"MCP (stdio): dotnet {SERVER.name}")
    params = StdioServerParameters(
        command="dotnet", args=[str(SERVER)], env={**os.environ, "ACAD_MCP_PORT": str(port)}
    )
    async with stdio_client(params) as (read, write):
        async with ClientSession(read, write) as session:
            init = await session.initialize()
            info = getattr(init, "server_info", None) or getattr(init, "serverInfo", None)
            check("server identifies as autocad", getattr(info, "name", None) == "autocad", str(info))
            tools = {t.name for t in (await session.list_tools()).tools}
            check("57 tools listed", len(tools) == 57 and {"health_check", "get_table", "capture_view"} <= tools, str(len(tools)))
            for name in sorted(tools & READ_ONLY_TOOLS):
                res = await session.call_tool(name, {})
                check(f"call {name}", not getattr(res, "is_error", False) and not getattr(res, "isError", False),
                      str(res)[:300])
            res = await session.call_tool("get_table", {"handle": "1"})
            text = res.content[0].text if res.content else ""
            check("bridge errors reach the model", (getattr(res, "is_error", False) or getattr(res, "isError", False))
                  and "Error executing tool get_table" in text and "[" in text, text[:200])
            res = await session.call_tool("find_entities", {"types": "LINE", "limit": 1})
            check("arguments are passed through", '"total"' in (res.content[0].text if res.content else ""), str(res)[:200])
            res = await session.call_tool("capture_view", {"mode": "extents", "size": 300})
            kinds = [c.type for c in res.content]
            img = next((c for c in res.content if c.type == "image"), None)
            check("capture_view returns image content + text", kinds == ["image", "text"] and img is not None
                  and getattr(img, "mime_type", getattr(img, "mimeType", None)) == "image/png" and img.data.startswith("iVBOR")
                  and '"image_width":300' in res.content[1].text and '"data"' not in res.content[1].text, str(kinds))


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=int(os.environ.get("ACAD_MCP_PORT", "8766")))
    args = ap.parse_args()
    base = f"http://localhost:{args.port}"
    try:
        test_http(base)
    except urllib.error.URLError as e:
        print(f"Bridge not reachable at {base}: {e}. Type MCPSTART in AutoCAD.")
        return 2
    test_concurrency(base)
    if is_fresh(base):
        test_write(base)
        test_read(base)
        test_tables(base)
        import test_edit
        test_edit.run(base, post, count, check)
        import test_draw
        test_draw.run(base, post, count, check)
        import test_tablewrite
        test_tablewrite.run(base, post, count, check)
        import test_fixes
        test_fixes.run(base, post, count, check)
        import test_dynamic
        test_dynamic.run(base, post, count, check)
        import test_dims
        test_dims.run(base, post, count, check)
        import test_styles
        test_styles.run(base, post, count, check)
        import test_mleader
        test_mleader.run(base, post, count, check)
        import test_view
        test_view.run(base, post, count, check)
        import test_commands
        test_commands.run(base, post, count, check, http)
        import test_files
        test_files.run(base, post, count, check)
    asyncio.run(test_mcp(args.port))
    print(f"\n{len(passed)}/{len(passed) + len(failed)} passed")
    return 0 if not failed else 1


if __name__ == "__main__":
    sys.exit(main())
