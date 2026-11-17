"""Step 4 checks (layers, properties, transform, delete, selection, system variables, undo).

Called by smoke_test.py after the step 2/3 fixture is built in a new empty drawing.
"""

from __future__ import annotations

from typing import Callable


def run(base: str, post: Callable, count: Callable, check: Callable) -> None:
    print("Edit tools (step 4)")

    # ---- layers
    s, r = post(base, "create_layer", name="MCP_NEW", color=3, linetype="DASHED", lineweight=0.35, description="test")
    check("create_layer with color, linetype, lineweight", s == 200 and r.get("color") == 3 and r.get("linetype") == "DASHED"
          and r.get("lineweight") == "LineWeight035", str(r))
    s, e = post(base, "create_layer", name="MCP_NEW")
    check("create_layer twice -> already_exists", s == 409 and e.get("code") == "already_exists", str(e))
    s, e = post(base, "create_layer", name="bad<name")
    check("create_layer invalid name -> bad_request", s == 400, str(e))
    s, r = post(base, "set_layer_state", names=["MCP_NEW"], locked=True, on=False)
    lay = (r.get("layers") or [{}])[0]
    check("set_layer_state lock + off", lay.get("locked") is True and lay.get("on") is False, str(r))
    s, r = post(base, "set_layer_state", names=["MCP_NEW"], new_name="MCP_NEW2", locked=False, on=True)
    check("set_layer_state rename", s == 200 and (r.get("layers") or [{}])[0].get("name") == "MCP_NEW2", str(r))
    s, e = post(base, "set_layer_state", names=["0"], frozen=True)
    check("freeze current layer -> bad_request", s == 400, str(e))
    s, e = post(base, "set_layer_state", names=["MCP_FROZEN"], current=True)
    check("make frozen layer current -> bad_request", s == 400, str(e))
    s, e = post(base, "set_layer_state", names=["NO_SUCH_*"], on=True)
    check("set_layer_state no match -> not_found", s == 404, str(e))

    # ---- properties
    _, line = post(base, "create_line", start=[500, 0], end=[510, 0])
    h = line.get("handle")
    s, r = post(base, "set_entity_properties", handles=[h], color=2, lineweight=0.5, layer="MCP_NEW2", transparency=50)
    _, p = post(base, "get_entity_properties", handles=[h])
    p = (p.get("items") or [{}])[0]
    check("set_entity_properties by handle", s == 200 and p.get("color") == 2 and p.get("layer") == "MCP_NEW2"
          and p.get("lineweight") == "LineWeight050", str(p))
    s, e = post(base, "set_entity_properties", filter={"layers": "MCP_A"}, color=4)
    check("filter without confirm -> confirm_required", s == 409 and e.get("code") == "confirm_required", str(e))
    s, r = post(base, "set_entity_properties", filter={"layers": "MCP_A"}, color=4, dry_run=True)
    check("dry_run reports 2 objects", r.get("would_affect") == 2 and r.get("dry_run") is True, str(r)[:200])
    s, r = post(base, "set_entity_properties", filter={"layers": "MCP_A"}, color=4, confirm=True)
    check("filter with confirm -> changed 2", r.get("changed") == 2, str(r))
    _, locked = post(base, "find_entities", layers="MCP_LOCKED")
    lh = [i["handle"] for i in locked.get("items", [])]
    s, e = post(base, "set_entity_properties", handles=lh, color=5)
    check("object on locked layer -> locked_layer", s == 409 and e.get("code") == "locked_layer", str(e))
    s, e = post(base, "set_entity_properties", handles=[h])
    check("nothing to change -> bad_request", s == 400, str(e))

    # ---- transform
    s, r = post(base, "transform_entities", operation="move", handles=[h], vector=[0, 10])
    _, p = post(base, "get_entity_properties", handles=[h])
    check("move by vector", (p.get("items") or [{}])[0].get("start") == [500, 10, 0], str(p)[:200])
    s, r = post(base, "transform_entities", operation="copy", handles=[h], vector=[0, 5], count=3)
    copies = r.get("created_handles", [])
    check("copy x3 (array)", len(copies) == 3, str(r))
    _, p = post(base, "get_entity_properties", handles=copies[-1:] or ["0"])
    check("third copy 15 units up", (p.get("items") or [{}])[0].get("start") == [500, 25, 0], str(p)[:200])
    post(base, "transform_entities", operation="rotate", handles=[h], base=[500, 10], angle_deg=90)
    _, p = post(base, "get_entity_properties", handles=[h])
    end = (p.get("items") or [{}])[0].get("end", [0, 0])
    check("rotate 90 deg", abs(end[0] - 500) < 1e-6 and abs(end[1] - 20) < 1e-6, str(end))
    post(base, "transform_entities", operation="scale", handles=[h], base=[500, 10], factor=2)
    _, p = post(base, "get_entity_properties", handles=[h])
    check("scale x2 -> length 20", abs((p.get("items") or [{}])[0].get("length", 0) - 20) < 1e-6, str(p)[:200])
    s, r = post(base, "transform_entities", operation="mirror", handles=[h], p1=[0, 0], p2=[0, 1])
    check("mirror keeps original, creates 1", len(r.get("created_handles", [])) == 1, str(r))
    s, e = post(base, "transform_entities", operation="spin", handles=[h])
    check("unknown operation -> bad_request", s == 400, str(e))

    # ---- delete + undo
    before = count(base)
    s, r = post(base, "delete_entities", handles=copies)
    check("delete 3 copies", r.get("deleted") == 3 and count(base) == before - 3, str(r))
    s, e = post(base, "delete_entities", filter={"types": "LINE"})
    check("delete by filter without confirm -> refused", s == 409 and count(base) == before - 3, str(e))
    s, u = post(base, "undo")
    check("undo brings the 3 copies back", s == 200 and count(base) == before, str(u))

    # ---- selection
    post(base, "set_selection", handles=[h])
    _, sel = post(base, "get_selection")
    check("set_selection -> get_selection 1", sel.get("total") == 1 and sel["items"][0]["handle"] == h, str(sel)[:200])
    post(base, "set_selection", handles=[])
    _, sel = post(base, "get_selection")
    check("set_selection [] clears", sel.get("total") == 0, str(sel))

    # ---- system variables
    _, v = post(base, "get_system_variable", names=["LTSCALE"])
    old = v.get("values", {}).get("LTSCALE")
    s, r = post(base, "set_system_variable", name="LTSCALE", value=2.5)
    check("set_system_variable LTSCALE", s == 200 and r.get("new") == 2.5 and r.get("old") == old, str(r))
    post(base, "set_system_variable", name="LTSCALE", value=old)
    s, e = post(base, "set_system_variable", name="DWGNAME", value="x.dwg")
    check("read-only variable -> bad_request", s == 400, str(e))
    s, e = post(base, "set_system_variable", name="NO_SUCH_VAR", value=1)
    check("unknown variable -> not_found", s == 404, str(e))
