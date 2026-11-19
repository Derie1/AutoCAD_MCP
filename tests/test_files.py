"""Iteration 3 checks: documents and files (stage 4). Runs last: it opens and closes other drawings
and returns to the test drawing at the end.

Every file is created in a fresh temporary folder; AutoCAD's templates are only read (copied from).
"""

from __future__ import annotations

import os
import re
import shutil
import tempfile
from typing import Callable


def run(base: str, post: Callable, count: Callable, check: Callable) -> None:
    print("Documents and files (iteration 3)")
    tmp = tempfile.mkdtemp(prefix="acad_mcp_test_")
    try:
        _run(base, post, count, check, tmp)
    finally:
        # Close whatever is still open from the temporary folder, then go back to the test drawing.
        _, docs = post(base, "list_documents")
        for d in docs.get("documents", []):
            if (d.get("path") or "").lower().startswith(os.path.realpath(tmp).lower()):
                post(base, "close_document", path=d["path"], discard_changes=True)
        shutil.rmtree(tmp, ignore_errors=True)


def _run(base: str, post: Callable, count: Callable, check: Callable, tmp: str) -> None:
    _, home = post(base, "get_document_info")
    home_name = home.get("name")

    s, new = post(base, "new_document")
    template = new.get("template") or ""
    check("new_document (default template)", s == 200 and new.get("active") is True and new.get("path") is None
          and template.lower().endswith(".dwt") and os.path.isfile(template), str(new)[:300])
    _, info = post(base, "get_document_info")
    check("new drawing is active", info.get("name") == new.get("name") and info.get("name") != home_name, str(info)[:200])
    s, r = post(base, "close_document")
    check("close_document unchanged new drawing", s == 200 and r.get("closed") == new.get("name")
          and r.get("discarded_changes") is False, str(r)[:300])

    s, r = post(base, "new_document", template="no_such_template")
    check("new_document unknown template -> 404 with the list", s == 404 and ".dwt" in r.get("error", ""), str(r)[:200])

    a = os.path.join(tmp, "A.dwg")
    b = os.path.join(tmp, "B.dwg")
    shutil.copy(template, a)
    shutil.copy(template, b)

    s, r = post(base, "open_document", path=a)
    check("open_document", s == 200 and r.get("name") == "A.dwg" and r.get("active") is True
          and r.get("already_open") is False and r.get("read_only") is False, str(r)[:300])
    s, line = post(base, "create_line", start=[0, 0], end=[10, 0])
    check("draw in the opened drawing", s == 200, str(line)[:200])
    s, r = post(base, "open_document", path=a)
    check("open_document again -> already_open", s == 200 and r.get("already_open") is True
          and r.get("has_unsaved_changes") is True, str(r)[:300])

    s, r = post(base, "open_document", path=b, read_only=True)
    check("open_document read_only", s == 200 and r.get("read_only") is True, str(r)[:300])
    s, r = post(base, "create_line", start=[0, 0], end=[10, 0])
    check("read-only drawing refuses changes", s == 409 and r.get("code") == "read_only", str(r)[:200])

    s, r = post(base, "list_documents")
    by_name = {d["name"]: d for d in r.get("documents", [])}
    check("list_documents shows unsaved changes of inactive drawings",
          by_name.get("A.dwg", {}).get("has_unsaved_changes") is True and by_name.get("B.dwg", {}).get("active") is True,
          str(r)[:400])

    s, r = post(base, "set_active_document", name="A")
    check("set_active_document by name without extension", s == 200 and r.get("name") == "A.dwg" and r.get("active") is True, str(r)[:200])
    check("tools work on the newly active drawing", count(base, types="LINE") == 1)

    s, r = post(base, "close_document", name="A.dwg")
    check("close_document with unsaved changes -> 409", s == 409 and r.get("code") == "unsaved_changes", str(r)[:200])
    s, r = post(base, "close_document", name="A.dwg", save=True, discard_changes=True)
    check("close_document save + discard -> 400", s == 400, str(r)[:200])
    s, r = post(base, "close_document", path=a, discard_changes=True)
    check("close_document discard_changes", s == 200 and r.get("discarded_changes") is True and r.get("closed") == "A.dwg", str(r)[:200])
    s, r = post(base, "close_document", name="B")
    check("close_document read-only drawing", s == 200 and r.get("closed") == "B.dwg", str(r)[:200])

    held = os.path.join(tmp, "held.dwg")
    shutil.copy(template, held)
    with open(held, "r+b"):
        s, r = post(base, "open_document", path=held)
    check("open_document file in use -> 409", s == 409 and r.get("code") == "file_in_use", str(r)[:200])

    for name, args, code in [
        ("relative path", {"path": "A.dwg"}, 400),
        ("missing file", {"path": os.path.join(tmp, "nope.dwg")}, 404),
        ("wrong extension", {"path": os.path.join(tmp, "x.txt")}, 400),
    ]:
        s, r = post(base, "open_document", **args)
        check(f"open_document {name} -> {code}", s == code, str(r)[:200])
    s, r = post(base, "set_active_document", name="nope")
    check("set_active_document unknown -> 404", s == 404, str(r)[:200])
    s, r = post(base, "set_active_document")
    check("set_active_document without name -> 400", s == 400, str(r)[:200])

    _save(base, post, count, check, tmp)
    _plot(base, post, count, check, tmp)
    _xrefs(base, post, count, check, tmp)
    _maintenance(base, post, count, check, tmp)
    _import(base, post, count, check, tmp)

    s, r = post(base, "set_active_document", name=home_name)
    check("back to the test drawing", s == 200 and r.get("name") == home_name, str(r)[:200])
    s, line = post(base, "create_line", start=[-500, -500], end=[-400, -500])
    s, u = post(base, "undo")
    _, p = post(base, "get_entity_properties", handles=[line.get("handle")])
    check("undo works after switching drawings", s == 200 and "error" in (p.get("items") or [{}])[0], str(u)[:200])


def _dwg_version(path: str) -> bytes:
    with open(path, "rb") as f:
        return f.read(6)


def _save(base: str, post: Callable, count: Callable, check: Callable, tmp: str) -> None:
    """Step 2: save_document, save_as."""
    s, new = post(base, "new_document")
    s, r = post(base, "save_document")
    check("save_document of a never-saved drawing -> 400", s == 400 and "save_as" in r.get("error", ""), str(r)[:200])
    _, l1 = post(base, "create_line", start=[0, 0], end=[100, 0])

    s1 = os.path.join(tmp, "S.dwg")
    s, r = post(base, "save_as", path=s1)
    check("save_as DWG (default format 2018)", s == 200 and r.get("name") == "S.dwg" and r.get("has_unsaved_changes") is False
          and r.get("previous_path") is None and os.path.isfile(s1) and _dwg_version(s1) == b"AC1032", str(r)[:300])

    _, l2 = post(base, "create_line", start=[0, 0], end=[0, 100])
    s, r = post(base, "save_document")
    check("save_document", s == 200 and r.get("has_unsaved_changes") is False and r.get("saved_to", "").endswith("S.dwg"), str(r)[:300])
    s, u = post(base, "undo")
    _, p = post(base, "get_entity_properties", handles=[l2.get("handle"), l1.get("handle")])
    items = p.get("items") or [{}, {}]
    check("undo after a save reverts the last change only", s == 200 and "error" in items[0] and "error" not in items[1], str(p)[:300])

    old = os.path.join(tmp, "old.dwg")
    s, r = post(base, "save_as", path=old, format="2004")
    check("save_as DWG 2004", s == 200 and r.get("format") == "DWG 2004" and _dwg_version(old) == b"AC1018"
          and r.get("previous_path", "").endswith("S.dwg"), str(r)[:300])
    dxf = os.path.join(tmp, "x.dxf")
    s, r = post(base, "save_as", path=dxf, format="2013")
    check("save_as DXF 2013", s == 200 and r.get("name") == "x.dxf" and os.path.isfile(dxf), str(r)[:300])

    # AutoCAD keeps the previous file of a renamed drawing open (S.dwg here), so overwrite another one.
    s, r = post(base, "save_as", path=s1)
    check("save_as onto an existing file -> 409", s == 409 and r.get("code") in ("file_exists", "file_in_use"), str(r)[:200])
    other = os.path.join(tmp, "other.dwg")
    shutil.copy(old, other)
    s, r = post(base, "save_as", path=other)
    check("save_as onto an existing file -> file_exists", s == 409 and r.get("code") == "file_exists", str(r)[:200])
    s, r = post(base, "save_as", path=other, overwrite=True)
    check("save_as overwrite", s == 200 and r.get("name") == "other.dwg" and _dwg_version(other) == b"AC1032", str(r)[:200])

    for name, args, code in [
        ("unknown format", {"path": os.path.join(tmp, "y.dwg"), "format": "2027"}, 400),
        ("wrong extension", {"path": os.path.join(tmp, "y.pdf")}, 400),
        ("missing folder", {"path": os.path.join(tmp, "no_folder", "y.dwg")}, 404),
        ("relative path", {"path": "y.dwg"}, 400),
    ]:
        s, r = post(base, "save_as", **args)
        check(f"save_as {name} -> {code}", s == code, str(r)[:200])

    s, r = post(base, "open_document", path=old, read_only=True)
    s, r = post(base, "save_document")
    check("save_document of a read-only drawing -> 409", s == 409 and r.get("code") == "read_only", str(r)[:200])
    s, r = post(base, "save_as", path=os.path.join(tmp, "copy.dwg"))
    check("save_as copies a read-only drawing", s == 200 and r.get("name") == "copy.dwg", str(r)[:200])
    s, r = post(base, "close_document")
    s, r = post(base, "set_active_document", name="other")
    _, l3 = post(base, "create_line", start=[0, 0], end=[5, 5])
    s, r = post(base, "close_document", save=True)
    check("close_document save", s == 200 and r.get("saved") is True, str(r)[:200])


def _pdf(path: str) -> tuple[int, list[tuple[float, float]]]:
    """Page count and page sizes (points) of a PDF written by AutoCAD."""
    if not os.path.isfile(path):
        return 0, []
    b = open(path, "rb").read()
    pages = len(re.findall(rb"/Type\s*/Page[^s]", b))
    boxes = [tuple(float(v) for v in m.split()[2:4]) for m in re.findall(rb"/MediaBox\s*\[([^\]]*)\]", b)]
    return pages, boxes


def _plot(base: str, post: Callable, count: Callable, check: Callable, tmp: str) -> None:
    """Step 3: plot_to_pdf."""
    post(base, "new_document", template="acadiso")
    post(base, "create_polyline", points=[[0, 0], [4000, 0], [4000, 3000], [0, 3000]], closed=True)
    _, txt = post(base, "create_text", text="PDF TEST", position=[500, 2500], height=200)

    f = os.path.join(tmp, "model.pdf")
    s, r = post(base, "plot_to_pdf", path=f, paper="A4")
    pages, boxes = _pdf(f)
    sheet = (r.get("sheets") or [{}])[0]
    check("plot_to_pdf current layout (Model, extents, fit)", s == 200 and pages == 1 and sheet.get("layout") == "Model"
          and sheet.get("area") == "extents" and sheet.get("scale") == "fit", str(r)[:300])
    check("wide area prints on a landscape page", boxes and boxes[0][0] > boxes[0][1], str(boxes))

    f = os.path.join(tmp, "all.pdf")
    s, r = post(base, "plot_to_pdf", path=f, layouts="*")
    pages, _ = _pdf(f)
    check("plot_to_pdf all paper layouts into one PDF", s == 200 and pages == 2
          and [x.get("layout") for x in r.get("sheets", [])] == ["Layout1", "Layout2"], str(r)[:300])
    _, lay = post(base, "list_layouts")
    check("current layout is restored after plotting", [x["name"] for x in lay.get("layouts", []) if x.get("current")] == ["Model"], str(lay)[:200])

    sub = os.path.join(tmp, "sheets")
    os.mkdir(sub)
    s, r = post(base, "plot_to_pdf", path=sub, layouts=["Model", "Layout1"], separate_files=True)
    names = sorted(os.listdir(sub))
    check("plot_to_pdf separate files", s == 200 and len(r.get("files", [])) == 2 and len(names) == 2
          and all(n.endswith(("-Model.pdf", "-Layout1.pdf")) for n in names), str(names))

    f = os.path.join(tmp, "a1.pdf")
    s, r = post(base, "plot_to_pdf", path=f, layouts="Model", paper="A1", scale=10)
    sheet = (r.get("sheets") or [{}])[0]
    check("plot_to_pdf paper A1, scale 1:10", s == 200 and "A1" in sheet.get("paper", "") and sheet.get("scale") == "1:10", str(r)[:300])
    f = os.path.join(tmp, "win.pdf")
    s, r = post(base, "plot_to_pdf", path=f, layouts="Model", p1=[0, 0], p2=[1000, 2000], plot_style="monochrome")
    pages, boxes = _pdf(f)
    check("plot_to_pdf window, plot style, tall area on a portrait page", s == 200 and pages == 1
          and (r.get("sheets") or [{}])[0].get("area") == "window" and boxes and boxes[0][0] < boxes[0][1], str(r)[:300])

    _, line = post(base, "create_line", start=[0, 0], end=[10, 10])
    s, r = post(base, "plot_to_pdf", path=f, layouts="Model")
    check("plot_to_pdf onto an existing file -> 409", s == 409 and r.get("code") == "file_exists", str(r)[:200])
    s, r = post(base, "plot_to_pdf", path=f, layouts="Model", overwrite=True)
    check("plot_to_pdf overwrite", s == 200, str(r)[:200])
    s, u = post(base, "undo")
    _, p = post(base, "get_entity_properties", handles=[line.get("handle"), txt.get("handle")])
    items = p.get("items") or [{}, {}]
    check("undo after plotting reverts the last change only", s == 200 and "error" in items[0] and "error" not in items[1], str(p)[:300])

    for name, args, code in [
        ("unknown layout", {"path": os.path.join(tmp, "x.pdf"), "layouts": "Nope"}, 404),
        ("unknown paper", {"path": os.path.join(tmp, "x.pdf"), "paper": "Z9"}, 404),
        ("unknown plot style", {"path": os.path.join(tmp, "x.pdf"), "plot_style": "nope.ctb"}, 404),
        ("scale on a paper layout", {"path": os.path.join(tmp, "x.pdf"), "layouts": "Layout1", "scale": 5}, 400),
        ("wrong extension", {"path": os.path.join(tmp, "x.dwg")}, 400),
        ("separate_files with a missing folder", {"path": os.path.join(tmp, "nope"), "separate_files": True}, 404),
    ]:
        s, r = post(base, "plot_to_pdf", **args)
        check(f"plot_to_pdf {name} -> {code}", s == code, str(r)[:200])
    post(base, "close_document", discard_changes=True)


def _xrefs(base: str, post: Callable, count: Callable, check: Callable, tmp: str) -> None:
    """Step 4: attach_xref, manage_xrefs."""
    def xrefs() -> dict:
        _, r = post(base, "list_xrefs")
        return {x["name"]: x for x in r.get("xrefs", [])}

    def layers() -> set:
        _, r = post(base, "list_layers", with_counts=False)
        return {x["name"] for x in r.get("layers", [])}

    os.mkdir(os.path.join(tmp, "x"))
    src = os.path.join(tmp, "x", "Part.dwg")
    post(base, "new_document", template="acadiso")
    post(base, "create_layer", name="PART", color=1)
    post(base, "create_circle", center=[0, 0], radius=500, layer="PART")
    post(base, "save_as", path=src)
    post(base, "close_document")

    post(base, "new_document", template="acadiso")
    s, r = post(base, "attach_xref", path=src)
    check("attach_xref in an unsaved drawing stores the full path", s == 200 and r.get("status") == "Resolved"
          and os.path.normcase(r.get("path", "")) == os.path.normcase(os.path.realpath(src)), str(r)[:300])
    s, u = post(base, "undo")
    check("undo removes the attached xref", s == 200 and xrefs() == {}, str(xrefs())[:200])

    host = os.path.join(tmp, "Host.dwg")
    post(base, "save_as", path=host)
    s, r = post(base, "attach_xref", path=src, position=[1000, 0], scale=2, rotation_deg=30)
    check("attach_xref relative path, position, scale, rotation", s == 200 and r.get("path") == r".\x\Part.dwg"
          and r.get("scale") == 2 and r.get("handle"), str(r)[:300])
    _, p = post(base, "get_entity_properties", handles=[r.get("handle")])
    item = (p.get("items") or [{}])[0]
    check("the xref reference is in the drawing", item.get("type") == "INSERT" and "Part|PART" in layers(), str(item)[:300])
    s, r = post(base, "attach_xref", path=src)
    check("attach_xref with a name in use -> 409", s == 409 and r.get("code") == "name_in_use", str(r)[:200])
    s, r = post(base, "attach_xref", path=src, name="PartOv", overlay=True, path_type="full")
    check("attach_xref overlay with full path", s == 200 and r.get("overlay") is True and os.path.isabs(r.get("path", "")), str(r)[:300])

    s, r = post(base, "manage_xrefs", action="unload", names="Part")
    check("manage_xrefs unload", s == 200 and xrefs().get("Part", {}).get("status") == "Unloaded", str(r)[:300])
    s, r = post(base, "manage_xrefs", action="bind", names="Part")
    check("bind an unloaded xref -> 409", s == 409 and r.get("code") == "not_loaded", str(r)[:200])
    s, r = post(base, "manage_xrefs", action="reload", names="*")
    check("manage_xrefs reload all", s == 200 and all(x.get("status") == "Resolved" for x in xrefs().values()), str(r)[:300])

    copy_path = os.path.join(tmp, "x", "Part2.dwg")
    shutil.copy(src, copy_path)
    s, r = post(base, "manage_xrefs", action="repath", names="PartOv", path=copy_path)
    check("manage_xrefs repath", s == 200 and xrefs().get("PartOv", {}).get("path", "").endswith("Part2.dwg"), str(r)[:300])

    s, r = post(base, "manage_xrefs", action="bind", names="Part")
    check("manage_xrefs bind (layers Part$0$PART)", s == 200 and "Part" not in xrefs() and "Part$0$PART" in layers(), str(r)[:300])
    s, u = post(base, "undo")
    check("undo reverts the bind", s == 200 and "Part" in xrefs() and "Part$0$PART" not in layers(), str(xrefs())[:200])
    s, r = post(base, "manage_xrefs", action="insert", names="PartOv")
    check("manage_xrefs insert (layers merged)", s == 200 and "PartOv" not in xrefs() and "PART" in layers(), str(r)[:300])
    s, r = post(base, "manage_xrefs", action="detach", names="Part")
    check("manage_xrefs detach", s == 200 and xrefs() == {}, str(r)[:300])

    for name, args, code in [
        ("unknown name", {"action": "reload", "names": "Nope"}, 404),
        ("unknown action", {"action": "explode", "names": "*"}, 400),
        ("without names", {"action": "reload"}, 400),
    ]:
        s, r = post(base, "manage_xrefs", **args)
        check(f"manage_xrefs {name} -> {code}", s == code, str(r)[:200])
    for name, args, code in [
        ("missing file", {"path": os.path.join(tmp, "nope.dwg")}, 404),
        ("itself", {"path": host}, 400),
        ("not a dwg", {"path": os.path.join(tmp, "x.pdf")}, 400),
    ]:
        s, r = post(base, "attach_xref", **args)
        check(f"attach_xref {name} -> {code}", s == code, str(r)[:200])
    post(base, "close_document", discard_changes=True)


def _maintenance(base: str, post: Callable, count: Callable, check: Callable, tmp: str) -> None:
    """Step 5: purge, audit. Uses x/Part.dwg from the xref checks."""
    def layers() -> set:
        _, r = post(base, "list_layers", with_counts=False)
        return {x["name"] for x in r.get("layers", [])}

    post(base, "new_document", template="acadiso")
    post(base, "save_as", path=os.path.join(tmp, "Clean.dwg"))
    _, ref = post(base, "attach_xref", path=os.path.join(tmp, "x", "Part.dwg"))
    post(base, "manage_xrefs", action="insert", names="Part")   # block Part + layer PART
    post(base, "delete_entities", handles=[ref.get("handle")])  # now both unused, the layer only via the block
    post(base, "create_layer", name="TMP_A")
    post(base, "create_layer", name="TMP_B")
    post(base, "create_layer", name="KEEP")
    post(base, "create_line", start=[0, 0], end=[1, 1], layer="KEEP")
    post(base, "set_text_style", name="TMP_TEXT", font="Arial")

    s, r = post(base, "purge")
    would = r.get("would_remove", {})
    check("purge dry run (default) lists nested objects", s == 200 and r.get("dry_run") is True and r.get("passes") == 2
          and {"PART", "TMP_A", "TMP_B"} <= set(would.get("layers", [])) and "KEEP" not in would.get("layers", [])
          and "0" not in would.get("layers", []) and would.get("blocks") == ["Part"] and "TMP_TEXT" in would.get("text_styles", []),
          str(r)[:400])
    check("purge dry run changes nothing", {"PART", "TMP_A", "TMP_B"} <= layers())

    s, r = post(base, "purge", kinds=["layers"], names="TMP_*", dry_run=False)
    check("purge layers by mask", s == 200 and r.get("removed", {}).get("layers") == ["TMP_A", "TMP_B"]
          and "TMP_A" not in layers() and "PART" in layers(), str(r)[:300])
    s, u = post(base, "undo")
    check("undo restores purged layers", s == 200 and {"TMP_A", "TMP_B"} <= layers(), str(u)[:200])
    s, r = post(base, "purge", dry_run=False)
    check("purge everything unused", s == 200 and r.get("total", 0) >= 5 and "PART" not in layers() and "KEEP" in layers(), str(r)[:300])
    s, r = post(base, "purge", dry_run=False)
    check("second purge finds nothing", s == 200 and r.get("total") == 0, str(r)[:200])
    s, r = post(base, "purge", kinds=["nope"])
    check("purge unknown kind -> 400", s == 400, str(r)[:200])

    _, line = post(base, "create_line", start=[0, 0], end=[5, 0])
    s, r = post(base, "audit")
    check("audit of a saved drawing reports the counts", s == 200 and r.get("errors_found") == 0
          and "AUDIT REPORT" in (r.get("report") or "") and not os.path.exists(os.path.join(tmp, "Clean.adt")), str(r)[:300])
    s, r = post(base, "audit", fix=True)
    check("audit fix", s == 200 and r.get("errors_fixed") == 0, str(r)[:300])
    s, u = post(base, "undo")
    _, p = post(base, "get_entity_properties", handles=[line.get("handle")])
    check("undo after audit reverts the last change", s == 200 and "error" in (p.get("items") or [{}])[0], str(p)[:200])
    post(base, "close_document", discard_changes=True)

    post(base, "new_document")
    s, r = post(base, "audit")
    check("audit of an unsaved drawing runs without a report", s == 200 and r.get("report") is None and r.get("note"), str(r)[:300])
    post(base, "close_document", discard_changes=True)


def _import(base: str, post: Callable, count: Callable, check: Callable, tmp: str) -> None:
    """Step 6: import_from_drawing. Uses x/Part.dwg from the xref checks."""
    def layer(name: str):
        _, r = post(base, "list_layers", with_counts=False)
        return next(((x["color"], x["linetype"]) for x in r.get("layers", []) if x["name"] == name), None)

    std = os.path.join(tmp, "Std.dwg")
    post(base, "new_document", template="acadiso")
    post(base, "create_layer", name="E-POWER", color=1, linetype="DASHED")
    post(base, "create_layer", name="E-LIGHT", color=2)
    post(base, "set_text_style", name="ST_ISO", font="isocpeur")
    post(base, "set_dim_style", name="D50", text_style="ST_ISO")
    _, ref = post(base, "attach_xref", path=os.path.join(tmp, "x", "Part.dwg"))
    post(base, "manage_xrefs", action="insert", names="Part")
    post(base, "save_as", path=std)
    post(base, "close_document")

    post(base, "new_document", template="acadiso")
    post(base, "create_layer", name="E-POWER", color=5)
    s, r = post(base, "import_from_drawing", path=std, kinds=["layers"], names="E-*")
    check("import_from_drawing layers, existing skipped", s == 200 and r.get("imported", {}).get("layers") == ["E-LIGHT"]
          and r.get("skipped_existing", {}).get("layers") == ["E-POWER"] and layer("E-POWER") == (5, "Continuous")
          and layer("E-LIGHT") == (2, "Continuous"), str(r)[:400])
    s, r = post(base, "import_from_drawing", path=std, kinds=["layers"], names="E-POWER", on_conflict="replace")
    check("import_from_drawing replace brings the linetype along", s == 200 and r.get("replaced", {}).get("layers") == ["E-POWER"]
          and "DASHED" in r.get("also_added", {}).get("linetypes", []) and layer("E-POWER") == (1, "DASHED"), str(r)[:400])
    s, u = post(base, "undo")
    check("undo reverts the import", s == 200 and layer("E-POWER") == (5, "Continuous"), str(layer("E-POWER")))

    s, r = post(base, "import_from_drawing", path=std, kinds=["dim_styles", "blocks"])
    check("import_from_drawing dimension style and block", s == 200 and r.get("imported", {}).get("dim_styles") == ["D50"]
          and r.get("imported", {}).get("blocks") == ["Part"] and "ST_ISO" in r.get("also_added", {}).get("text_styles", [])
          and "PART" in r.get("also_added", {}).get("layers", []), str(r)[:400])
    s, ins = post(base, "insert_block", name="Part", position=[0, 0])
    check("an imported block can be inserted", s == 200, str(ins)[:200])

    for name, args, code in [
        ("nothing matches", {"path": std, "kinds": ["layers"], "names": "NOPE*"}, 404),
        ("unknown kind", {"path": std, "kinds": ["bogus"]}, 400),
        ("without kinds", {"path": std}, 400),
        ("missing file", {"path": os.path.join(tmp, "none.dwg"), "kinds": ["layers"]}, 404),
        ("bad on_conflict", {"path": std, "kinds": ["layers"], "on_conflict": "merge"}, 400),
    ]:
        s, r = post(base, "import_from_drawing", **args)
        check(f"import_from_drawing {name} -> {code}", s == code, str(r)[:200])
    post(base, "close_document", discard_changes=True)
