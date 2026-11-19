"""Iteration 2, step 4 checks: dynamic block properties of existing references (set_entity_properties)
and on insert (insert_block). Uses the "Door - Metric" block from AutoCAD's sample drawings.

Called by smoke_test.py in the fresh test drawing.
"""

from __future__ import annotations

from typing import Callable

BLOCK = "Door - Metric"


def dyn(post: Callable, base: str, handle: str) -> dict:
    _, p = post(base, "get_entity_properties", handles=[handle])
    item = (p.get("items") or [{}])[0]
    return {d["name"]: d["value"] for d in item.get("dynamic_properties", [])}


def run(base: str, post: Callable, count: Callable, check: Callable) -> None:
    print("Dynamic blocks (iteration 2, step 4)")
    import fixture

    if not fixture.import_dynamic_block(fixture.active_document(), BLOCK):
        print("    SKIPPED: AutoCAD sample 'Architectural - Metric.dwg' is not installed")
        return

    s, d1 = post(base, "insert_block", name=BLOCK, position=[9000, 0], dynamic_properties={"Door Size": 600})
    check("insert_block with dynamic_properties", s == 200 and dyn(post, base, d1.get("handle"))["Door Size"] == 600, str(d1)[:300])
    h = d1["handle"]
    _, d2 = post(base, "insert_block", name=BLOCK, position=[11000, 0])
    line = post(base, "create_line", start=[9000, -500], end=[9500, -500])[1]["handle"]

    s, r = post(base, "set_entity_properties", handles=[h],
                dynamic_properties={"door size": 900, "Opening Angle": "Open 90º", "Hinge": 1}, color=3)
    now = dyn(post, base, h)
    check("set_entity_properties dynamic_properties on an existing reference", s == 200 and r.get("changed") == 1
          and now.get("Door Size") == 900 and now.get("Opening Angle") == "Open 90º" and now.get("Hinge") == 1, f"{r} {now}")
    check("result reports the new values", (r.get("dynamic_blocks") or [{}])[0].get("dynamic_properties", {}).get("Door Size") == 900, str(r))
    _, p = post(base, "get_entity_properties", handles=[h])
    check("same handle, color applied too", (p.get("items") or [{}])[0].get("color") == 3, str(p)[:200])

    s, e = post(base, "set_entity_properties", handles=[h], dynamic_properties={"Door Size": 850})
    check("value not in the list -> bad_request with allowed values", s == 400 and "allowed" in e.get("error", ""), str(e))
    s, e = post(base, "set_entity_properties", handles=[h], dynamic_properties={"Height": 2100})
    check("unknown dynamic property -> bad_request", s == 400 and "Door Size" in e.get("error", ""), str(e))
    s, e = post(base, "set_entity_properties", handles=[h, line], dynamic_properties={"Door Size": 1000})
    check("a non-dynamic object among targets -> bad_request", s == 400 and line in e.get("error", ""), str(e))
    check("failed calls changed nothing", dyn(post, base, h).get("Door Size") == 900)

    s, r = post(base, "set_entity_properties", filter={"blocks": BLOCK}, dynamic_properties={"Wall Thickness": 150}, dry_run=True)
    check("dry_run by block name finds both doors", r.get("would_affect") == 2, str(r)[:200])
    s, r = post(base, "set_entity_properties", filter={"blocks": BLOCK}, dynamic_properties={"Wall Thickness": 150}, confirm=True)
    check("bulk change of both doors", s == 200 and r.get("changed") == 2
          and dyn(post, base, d2["handle"]).get("Wall Thickness") == 150 and dyn(post, base, h).get("Wall Thickness") == 150, str(r)[:300])

    s, u = post(base, "undo")
    check("undo reverts the bulk dynamic change", s == 200 and dyn(post, base, d2["handle"]).get("Wall Thickness") == 100
          and dyn(post, base, h).get("Wall Thickness") == 100 and dyn(post, base, h).get("Door Size") == 900, str(u))
