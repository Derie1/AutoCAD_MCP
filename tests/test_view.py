"""Iteration 2, step 1 checks: zoom and set_current_layout.

Called by smoke_test.py in the fresh test drawing after the other write checks.
"""

from __future__ import annotations

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
