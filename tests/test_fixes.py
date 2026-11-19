"""Iteration 2, step 3 checks: create_layer without linetype; ByLayer defaults and linetype/lineweight
on the create tools.

Called by smoke_test.py in the fresh test drawing (block MCP_BLK exists).
"""

from __future__ import annotations

from typing import Callable


def run(base: str, post: Callable, count: Callable, check: Callable) -> None:
    print("Create defaults (iteration 2, step 3)")

    s, r = post(base, "create_layer", name="MCP_NO_LT", color=8, lineweight=0.5)
    check("create_layer without linetype -> Continuous", s == 200 and r.get("linetype") == "Continuous", str(r))
    _, layers = post(base, "list_layers", name="MCP_NO_LT")
    lay = (layers.get("layers") or [{}])[0]
    check("list_layers shows its linetype", lay.get("linetype") == "Continuous", str(layers)[:300])

    # Current color / linetype / lineweight set to something else: new objects still get ByLayer.
    _, old = post(base, "get_system_variable", names=["CECOLOR", "CELTYPE", "CELWEIGHT"])
    old = old.get("values", {})
    post(base, "set_system_variable", name="CECOLOR", value="1")
    post(base, "set_system_variable", name="CELTYPE", value="Continuous")
    post(base, "set_system_variable", name="CELWEIGHT", value=50)
    try:
        s, r = post(base, "create_line", start=[7000, 0], end=[7010, 0])
        check("create_line ignores CECOLOR/CELTYPE/CELWEIGHT -> ByLayer", s == 200 and r.get("color") == "ByLayer"
              and r.get("linetype") == "ByLayer" and r.get("lineweight") == "ByLayer", str(r)[:300])
        s, r = post(base, "insert_block", name="MCP_BLK", position=[7000, 50], attributes={"NAME": "LT"})
        check("insert_block -> ByLayer", s == 200 and r.get("color") == "ByLayer" and r.get("linetype") == "ByLayer"
              and r.get("lineweight") == "ByLayer", str(r)[:300])
        s, r = post(base, "create_table", position=[7000, 100], data=[["a", "b"], ["1", "2"]])
        _, p = post(base, "get_entity_properties", handles=[r.get("handle")])
        p = (p.get("items") or [{}])[0]
        check("create_table -> ByLayer", s == 200 and p.get("color") == "ByLayer" and p.get("lineweight") == "ByLayer", str(p)[:300])
    finally:
        for name in ("CECOLOR", "CELTYPE", "CELWEIGHT"):
            if name in old:
                post(base, "set_system_variable", name=name, value=old[name])

    s, r = post(base, "create_line", start=[7000, 10], end=[7010, 10], linetype="DASHED", lineweight=0.35, color=2)
    check("create_line with linetype, lineweight, color", s == 200 and r.get("linetype") == "DASHED"
          and r.get("lineweight") == "LineWeight035" and r.get("color") == 2, str(r)[:300])
    s, r = post(base, "create_circle", center=[7050, 10], radius=3, linetype="ByBlock", lineweight="ByBlock", color="ByBlock")
    check("create_circle ByBlock properties", s == 200 and r.get("linetype") == "ByBlock"
          and r.get("lineweight") == "ByBlock" and r.get("color") == "ByBlock", str(r)[:300])
    s, r = post(base, "insert_block", name="MCP_BLK", position=[7000, 70], linetype="DASHED", lineweight=0.25, layer="MCP_NO_LT")
    check("insert_block with linetype, lineweight, layer", s == 200 and r.get("linetype") == "DASHED"
          and r.get("lineweight") == "LineWeight025" and r.get("layer") == "MCP_NO_LT", str(r)[:300])
    s, r = post(base, "create_text", text="T", position=[7000, 90], height=2, lineweight=0.18)
    check("create_text with lineweight", s == 200 and r.get("lineweight") == "LineWeight018", str(r)[:300])
    s, r = post(base, "create_table", position=[7100, 100], data=[["x"]], color=5, layer="MCP_NO_LT")
    _, p = post(base, "get_entity_properties", handles=[r.get("handle")])
    p = (p.get("items") or [{}])[0]
    check("create_table with color and layer", s == 200 and p.get("color") == 5 and p.get("layer") == "MCP_NO_LT", str(p)[:300])

    s, e = post(base, "create_line", start=[0, 0], end=[1, 0], lineweight=0.33)
    check("bad lineweight -> bad_request", s == 400, str(e))
    s, e = post(base, "create_line", start=[0, 0], end=[1, 0], linetype="NO_SUCH_LT")
    check("unknown linetype -> not_found", s == 404, str(e))
