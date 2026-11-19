"""Iteration 2, step 1 checks: zoom and set_current_layout.

Called by smoke_test.py in the fresh test drawing after the other write checks.
"""

from __future__ import annotations

import base64
import os
import struct
import tempfile
from typing import Callable


def near(a, b, tol=1e-6) -> bool:
    return a is not None and b is not None and abs(a - b) < tol


def run(base: str, post: Callable, count: Callable, check: Callable) -> None:
    print("View tools (iteration 2, step 1)")

    s, r = post(base, "set_current_layout", name="model")
    check("set_current_layout Model", s == 200 and r.get("layout") == "Model" and r.get("space") == "model", str(r))

    s, r = post(base, "zoom")
    z = r.get("zoomed_to", {})
    check("zoom extents (default mode)", s == 200 and r.get("mode") == "extents" and r.get("objects", 0) > 0
          and r.get("view_width", 0) >= z.get("max", [0])[0] - z.get("min", [0])[0], str(r)[:300])

    s, r = post(base, "zoom", p1=[100, 100], p2=[0, 0], padding=0)
    check("zoom window: centered, whole box visible", s == 200 and r.get("mode") == "window"
          and near(r["view_center"][0], 50) and near(r["view_center"][1], 50)
          and r["view_width"] >= 100 - 1e-6 and r["view_height"] >= 100 - 1e-6
          and (near(r["view_width"], 100) or near(r["view_height"], 100)), str(r)[:300])

    s, c = post(base, "create_circle", center=[5000, 5000], radius=10)
    s, r = post(base, "zoom", handles=[c["handle"]], padding=0)
    check("zoom to an entity by handle", s == 200 and r.get("objects") == 1 and near(r["view_center"][0], 5000)
          and near(r["view_center"][1], 5000) and near(min(r["view_width"], r["view_height"]), 20), str(r)[:300])

    s, r = post(base, "zoom", filter={"types": "CIRCLE"}, padding=0.5)
    check("zoom to entities by filter (no confirm needed)", s == 200 and r.get("objects", 0) >= 1, str(r)[:300])

    s, e = post(base, "zoom", mode="spiral")
    check("zoom bad mode -> bad_request", s == 400, str(e))
    s, e = post(base, "zoom", padding=2)
    check("zoom bad padding -> bad_request", s == 400, str(e))
    s, e = post(base, "zoom", mode="window", p1=[0, 0])
    check("zoom window without p2 -> bad_request", s == 400, str(e))
    s, e = post(base, "zoom", filter={"types": "NO_SUCH_TYPE"})
    check("zoom to nothing -> not_found", s == 404, str(e))

    # The view does not take part in the undo tool: undo after a zoom reverts the drawing change.
    before = count(base)
    post(base, "create_line", start=[5000, 5000], end=[5010, 5010])
    post(base, "zoom")
    s, u = post(base, "undo")
    check("undo after zoom reverts the change, not the zoom", s == 200 and count(base) == before, str(u))

    # Layouts
    _, layouts = post(base, "list_layouts")
    paper = next((l["name"] for l in layouts.get("layouts", []) if l.get("name") != "Model"), None)
    check("drawing has a paper layout", paper is not None, str(layouts)[:200])
    if paper is None:
        return
    s, r = post(base, "set_current_layout", name=paper.upper())
    check("set_current_layout paper (case-insensitive)", s == 200 and r.get("layout") == paper
          and r.get("previous_layout") == "Model" and r.get("space") in ("paper", "model_in_viewport"), str(r))
    s, pl = post(base, "create_line", start=[0, 0], end=[50, 50])
    check("line created on the paper layout", s == 200, str(pl)[:200])
    s, r = post(base, "zoom", handles=[pl["handle"]])
    check("zoom to a paper entity on its layout", s == 200 and r.get("objects") == 1, str(r)[:300])

    s, r = post(base, "set_current_layout", name="Model")
    check("back to Model", s == 200 and r.get("layout") == "Model" and r.get("previous_layout") == paper, str(r))
    s, e = post(base, "zoom", handles=[pl["handle"]])
    check("zoom to an entity of another layout -> other_space", s == 409 and e.get("code") == "other_space", str(e))
    s, u = post(base, "undo")
    check("undo of the paper line from Model", s == 200, str(u))
    s, r = post(base, "get_entity_properties", handle=pl["handle"])
    check("paper line is gone after undo", s == 404 or r.get("items", [{}])[0].get("code") == "not_found", str(r)[:200])
    _, d = post(base, "get_document_info")
    check("layout after undo reported", d.get("current_layout") in ("Model", paper), str(d.get("current_layout")))

    s, e = post(base, "set_current_layout", name="NoSuchLayout")
    check("unknown layout -> not_found", s == 404, str(e))
    post(base, "set_current_layout", name="Model")

    run_capture(base, post, check, paper)


def png_size(b64: str) -> tuple[int, int]:
    raw = base64.b64decode(b64)
    if raw[:8] != b"\x89PNG\r\n\x1a\n":
        return (0, 0)
    return struct.unpack(">II", raw[16:24])


def run_capture(base: str, post: Callable, check: Callable, paper: str) -> None:
    print("capture_view (iteration 2, step 2)")
    _, before = post(base, "get_system_variable", names=["VIEWCTR", "VIEWSIZE"])

    s, r = post(base, "capture_view")
    img = r.get("image", {})
    w, h = png_size(img.get("data", ""))
    check("capture current view: PNG, long side 1500", s == 200 and r.get("mode") == "current" and img.get("mime_type") == "image/png"
          and max(w, h) == 1500 and (w, h) == (r.get("image_width"), r.get("image_height")), str({k: v for k, v in r.items() if k != "image"}))

    s, r = post(base, "capture_view", mode="extents", size=400, background="light")
    w, h = png_size(r.get("image", {}).get("data", ""))
    check("capture extents, size 400, light", s == 200 and r.get("objects", 0) > 0 and max(w, h) == 400
          and r.get("drawing_units_per_pixel", 0) > 0, str({k: v for k, v in r.items() if k != "image"}))

    s, r = post(base, "capture_view", p1=[0, 0], p2=[200, 100], padding=0, size=200)
    w, h = png_size(r.get("image", {}).get("data", ""))
    check("capture window 2:1 -> 200 x 100 px", s == 200 and r.get("mode") == "window" and (w, h) == (200, 100)
          and abs(r.get("drawing_units_per_pixel", 0) - 1) < 1e-6, str({k: v for k, v in r.items() if k != "image"}))

    _, circles = post(base, "find_entities", types="CIRCLE", space="model", limit=1)
    s, r = post(base, "capture_view", handles=[circles["items"][0]["handle"]], size=300)
    check("capture entities by handle", s == 200 and r.get("mode") == "entities" and r.get("objects") == 1, str(r)[:200])

    path = os.path.join(tempfile.gettempdir(), "acadmcp_capture_test.png")
    if os.path.exists(path):
        os.remove(path)
    s, r = post(base, "capture_view", mode="extents", size=200, save_path=path)
    check("capture saved to file", s == 200 and r.get("saved_to") == path and os.path.getsize(path) > 100, str(r.get("saved_to")))
    os.remove(path)

    for bad, args in [("size too big", {"size": 5000}), ("bad background", {"background": "pink"}),
                      ("relative save_path", {"save_path": "x.png"}), ("save_path not png", {"save_path": "C:\\x.jpg"}),
                      ("bad mode", {"mode": "spiral"})]:
        s, e = post(base, "capture_view", **args)
        check(f"capture {bad} -> bad_request", s == 400, str(e))
    s, e = post(base, "capture_view", save_path="C:\\no_such_folder_mcp\\x.png")
    check("capture save_path in missing folder -> not_found", s == 404, str(e))

    _, after = post(base, "get_system_variable", names=["VIEWCTR", "VIEWSIZE"])
    check("capture does not move the user's view", before.get("values") == after.get("values"), f"{before} {after}")

    post(base, "set_current_layout", name=paper)
    s, r = post(base, "capture_view", mode="extents", size=500)
    check("capture a paper layout", s == 200 and r.get("space") == "paper" and r.get("image_width", 0) > 0,
          str({k: v for k, v in r.items() if k != "image"}))
    post(base, "set_current_layout", name="Model")
