"""Iteration 2, step 5 checks: create_dimension with the current DIM* overrides and its own appearance
arguments; modify_dimension.

Called by smoke_test.py in the fresh test drawing.
"""

from __future__ import annotations

from typing import Callable


def near(a, b, tol=1e-6) -> bool:
    return a is not None and b is not None and abs(a - b) < tol


def run(base: str, post: Callable, count: Callable, check: Callable) -> None:
    print("Dimensions (iteration 2, step 5)")

    # The current DIM* overrides (as set by set_system_variable) reach the new dimension.
    names = ["DIMTXT", "DIMASZ", "DIMBLK", "DIMDEC"]
    _, old = post(base, "get_system_variable", names=names)
    old = old.get("values", {})
    try:
        post(base, "set_system_variable", name="DIMTXT", value=250)
        post(base, "set_system_variable", name="DIMASZ", value=150)
        post(base, "set_system_variable", name="DIMBLK", value="_ARCHTICK")
        post(base, "set_system_variable", name="DIMDEC", value=0)
        s, r = post(base, "create_dimension", p1=[20000, 0], p2=[23000, 0], dim_line_point=[21500, 600], kind="linear")
        check("create_dimension takes DIMTXT/DIMASZ/DIMBLK/DIMDEC overrides", s == 200 and near(r.get("text_height"), 250)
              and near(r.get("arrow_size"), 150) and "ARCHTICK" in str(r.get("arrow")).upper() and r.get("decimals") == 0
              and near(r.get("measurement"), 3000), str(r)[:400])
        s, r2 = post(base, "create_dimension", p1=[20000, 0], p2=[23000, 0], dim_line_point=[21500, -600], style="Standard")
        check("explicit style: no sysvar overrides", s == 200 and not near(r2.get("text_height"), 250), str(r2)[:300])
    finally:
        for n in names:
            if n in old:
                v = old[n]
                post(base, "set_system_variable", name=n, value="." if n == "DIMBLK" and v == "" else v)

    s, r = post(base, "create_dimension", p1=[20000, 1000], p2=[20120, 1000], dim_line_point=[20060, 1500],
                text_height=100, arrow="_OBLIQUE", dim_scale=2, fit="text_inside", decimals=1)
    check("create_dimension with own appearance", s == 200 and near(r.get("text_height"), 100) and near(r.get("dim_scale"), 2)
          and "OBLIQUE" in str(r.get("arrow")).upper() and r.get("fit") == "text_inside" and r.get("decimals") == 1, str(r)[:400])
    small = r.get("handle")

    s, r = post(base, "create_dimension", p1=[20000, 2000], p2=[20300, 2000], dim_line_point=[20150, 2400],
                text_height=50, text_position=[20600, 2600])
    check("create_dimension with text_position", s == 200 and r.get("text_moved") is True
          and near(r["text_position"][0], 20600, 1e-3) and near(r["text_position"][1], 2600, 1e-3), str(r)[:400])

    # modify_dimension
    s, r = post(base, "modify_dimension", handles=[small], text_override="L=<>", text_height=300)
    m = (r.get("dimensions") or [{}])[0]
    check("modify_dimension text and height", s == 200 and r.get("changed") == 1 and m.get("text_override") == "L=<>"
          and near(m.get("text_height"), 300) and near(m.get("measurement"), 120) and near(m.get("dim_scale"), 2), str(r)[:400])
    s, r = post(base, "modify_dimension", handles=[small], text_position=[20060, 1800], text_leader=True)
    m = (r.get("dimensions") or [{}])[0]
    check("modify_dimension text_position", s == 200 and m.get("text_moved") is True and near(m["text_position"][1], 1800, 1e-3), str(r)[:300])
    s, r = post(base, "modify_dimension", handles=[small], reset_overrides=True)
    m = (r.get("dimensions") or [{}])[0]
    check("reset_overrides drops overrides", s == 200 and not near(m.get("text_height"), 300) and near(m.get("dim_scale"), 1)
          and m.get("text_override") == "L=<>", str(r)[:300])

    s, r = post(base, "modify_dimension", filter={"types": "DIMENSION", "window": {"min": [19000, -1000], "max": [24000, 3000]}},
                dim_scale=1.5, dry_run=True)
    check("modify_dimension dry_run by filter", r.get("would_affect") == 4, str(r)[:200])
    s, r = post(base, "modify_dimension", filter={"types": "DIMENSION", "window": {"min": [19000, -1000], "max": [24000, 3000]}},
                dim_scale=1.5, confirm=True)
    check("modify_dimension bulk", s == 200 and r.get("changed") == 4
          and all(near(x.get("dim_scale"), 1.5) for x in r.get("dimensions", [])), str(r)[:300])
    s, u = post(base, "undo")
    _, p = post(base, "get_entity_properties", handles=[small])
    check("undo reverts the bulk change", s == 200 and near((p.get("items") or [{}])[0].get("dim_scale"), 1), str(p)[:300])

    _, line = post(base, "create_line", start=[19000, 0], end=[19001, 0])
    for name, args, code in [
        ("not a dimension", {"handles": [small, line["handle"]], "dim_scale": 2}, 400),
        ("text_position for 2 dimensions", {"handles": [small, r2["handle"]], "text_position": [0, 0]}, 400),
        ("unknown arrow", {"handles": [small], "arrow": "_NO_SUCH_ARROW"}, 400),
        ("bad fit", {"handles": [small], "fit": "squeeze"}, 400),
        ("nothing to change", {"handles": [small]}, 400),
        ("unknown style", {"handles": [small], "style": "NO_SUCH_STYLE"}, 404),
        ("bad text_height", {"handles": [small], "text_height": 0}, 400),
    ]:
        s, e = post(base, "modify_dimension", **args)
        check(f"modify_dimension {name} -> {code}", s == code, str(e)[:200])
