"""Step 5 checks: creating geometry, text, dimensions, blocks and attribute updates.

Called by smoke_test.py after the step 2-4 checks (block MCP_BLK with attribute NAME exists).
"""

from __future__ import annotations

from typing import Callable


def near(a, b, tol=1e-6) -> bool:
    return a is not None and b is not None and abs(a - b) < tol


def run(base: str, post: Callable, count: Callable, check: Callable) -> None:
    print("Create tools (step 5)")

    s, r = post(base, "create_polyline", points=[[1000, 0], [1010, 0], [1010, 10], [1000, 10]], closed=True,
                bulges=[0, 0, 0, 1], layer="MCP_A")
    check("create_polyline closed with an arc", s == 200 and r.get("type") == "LWPOLYLINE" and r.get("closed") is True
          and len(r.get("vertices", [])) == 4 and r.get("layer") == "MCP_A", str(r)[:300])
    s, e = post(base, "create_polyline", points=[[0, 0]])
    check("polyline with 1 point -> bad_request", s == 400, str(e))
    s, e = post(base, "create_polyline", points=[[0, 0], [1, 1]], bulges=[0])
    check("bulges count mismatch -> bad_request", s == 400, str(e))

    s, r = post(base, "create_circle", center=[1100, 0], radius=7.5, color=3)
    check("create_circle", s == 200 and r.get("radius") == 7.5 and r.get("color") == 3, str(r)[:200])
    s, e = post(base, "create_circle", center=[0, 0], radius=-1)
    check("negative radius -> bad_request", s == 400, str(e))

    s, r = post(base, "create_arc", center=[1200, 0], radius=10, start_angle_deg=0, end_angle_deg=90)
    check("create_arc by angles", s == 200 and near(r.get("end_angle_deg"), 90) and near(r.get("length"), 15.707963268, 1e-6), str(r)[:200])
    s, r = post(base, "create_arc", points=[[1300, 0], [1310, 10], [1320, 0]])
    check("create_arc by 3 points (clockwise input)", s == 200 and near(r.get("radius"), 10)
          and r.get("center") == [1310, 0, 0], str(r)[:300])
    s, e = post(base, "create_arc", points=[[0, 0], [1, 1], [2, 2]])
    check("arc through collinear points -> bad_request", s == 400, str(e))

    s, r = post(base, "create_text", text="Pump room", position=[1000, 50], height=3.5, justify="MiddleCenter", rotation_deg=15)
    check("create_text centered and rotated", s == 200 and r.get("text") == "Pump room" and r.get("justify") == "MiddleCenter"
          and near(r.get("rotation_deg"), 15) and near(r.get("height"), 3.5), str(r)[:300])
    s, e = post(base, "create_text", text="x", position=[0, 0], justify="Somewhere")
    check("bad justify -> bad_request", s == 400, str(e))
    s, e = post(base, "create_text", text="x", position=[0, 0], style="NO_SUCH_STYLE")
    check("unknown text style -> not_found", s == 404, str(e))
    s, r = post(base, "create_mtext", text="Line A\nLine B", position=[1000, 70], width=40, height=2.5, attachment="TopLeft")
    check("create_mtext with 2 paragraphs", s == 200 and "\\P" in (r.get("contents") or "") and r.get("width") == 40, str(r)[:300])
    check("find the new texts by text", count(base, text="Pump r*") == 1 and count(base, text="*Line B*") == 1)

    s, r = post(base, "create_dimension", p1=[1000, 0], p2=[1010, 0], dim_line_point=[1005, -10])
    check("aligned dimension measures 10", s == 200 and near(r.get("measurement"), 10), str(r)[:300])
    s, r = post(base, "create_dimension", kind="linear", angle_deg=90, p1=[1000, 0], p2=[1010, 10], dim_line_point=[990, 5],
                text_override="<> mm")
    check("vertical linear dimension measures 10 with override", s == 200 and near(r.get("measurement"), 10)
          and r.get("text_override") == "<> mm", str(r)[:300])
    s, e = post(base, "create_dimension", kind="angular", p1=[0, 0], p2=[1, 0], dim_line_point=[0, 1])
    check("unsupported dimension kind -> bad_request", s == 400, str(e))

    s, r = post(base, "insert_block", name="MCP_BLK", position=[1000, 200], scale=2, rotation_deg=90, attributes={"name": "P-7"})
    attrs = r.get("attributes", [])
    check("insert_block with attribute value", s == 200 and r.get("block") == "MCP_BLK" and attrs
          and attrs[0].get("value") == "P-7" and near(r.get("rotation_deg"), 90) and r.get("scale") == [2, 2, 2], str(r)[:300])
    b1 = r.get("handle")
    s, r = post(base, "insert_block", name="MCP_BLK", position=[1050, 200])
    check("insert_block default attribute", s == 200 and (r.get("attributes") or [{}])[0].get("value") == "X", str(r)[:200])
    b2 = r.get("handle")
    s, e = post(base, "insert_block", name="MCP_BLK", position=[0, 0], attributes={"POWER": "5"})
    check("unknown tag -> bad_request listing NAME", s == 400 and "NAME" in e.get("error", ""), str(e))
    s, e = post(base, "insert_block", name="NO_SUCH_BLOCK", position=[0, 0])
    check("unknown block -> not_found", s == 404, str(e))
    s, e = post(base, "insert_block", name="MCP_BLK", position=[0, 0], dynamic_properties={"Width": 5})
    check("dynamic props on a static block -> bad_request", s == 400, str(e))

    s, r = post(base, "update_block_attributes", handles=[b1, b2], values={"NAME": "SAME"})
    check("update_block_attributes same value", r.get("blocks") == 2 and r.get("attributes_changed") == 2, str(r))
    s, r = post(base, "update_block_attributes", updates=[{"handle": b1, "values": {"NAME": "A-1"}},
                                                           {"handle": b2, "values": {"NAME": "A-2"}}])
    _, ex = post(base, "extract_block_attributes", blocks="MCP_BLK")
    names = sorted(row[-1] for row in ex.get("rows", []))
    check("update_block_attributes per block", r.get("attributes_changed") == 2 and names == ["A-1", "A-2", "PUMP-1"], str(names))
    s, e = post(base, "update_block_attributes", filter={"blocks": "MCP_BLK"}, values={"NAME": "Z"})
    check("by filter without confirm -> confirm_required", s == 409, str(e))
    s, r = post(base, "update_block_attributes", filter={"blocks": "MCP_BLK"}, values={"NAME": "Z"}, dry_run=True)
    check("dry_run shows 3 blocks", r.get("would_affect") == 3, str(r)[:200])
    s, e = post(base, "update_block_attributes", handles=[b1], values={"NOPE": "1"})
    check("missing tag -> not_found", s == 404, str(e))

    s, u = post(base, "undo")
    _, ex = post(base, "extract_block_attributes", blocks="MCP_BLK")
    names = sorted(row[-1] for row in ex.get("rows", []))
    check("undo reverts the per-block update", s == 200 and names == ["PUMP-1", "SAME", "SAME"], str(names))
