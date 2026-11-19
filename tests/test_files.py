"""Iteration 3 checks: documents and files (stage 4). Runs last: it opens and closes other drawings
and returns to the test drawing at the end.

Every file is created in a fresh temporary folder; AutoCAD's templates are only read (copied from).
"""

from __future__ import annotations

import os
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
