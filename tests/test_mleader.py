"""Iteration 2, step 7 checks: create_mleader. Runs after test_styles (multileader style MCP_ML exists).

Called by smoke_test.py in the fresh test drawing.
"""

from __future__ import annotations

from typing import Callable


def near(a, b, tol=1e-6) -> bool:
    return a is not None and b is not None and abs(a - b) < tol


def same_point(p, q, tol=1e-6) -> bool:
    return p is not None and q is not None and all(near(p[i], q[i], tol) for i in range(2))


def run(base: str, post: Callable, count: Callable, check: Callable) -> None:
    print("Multileaders (iteration 2, step 7)")

    s, r = post(base, "create_mleader", points=[[40000, 0], [40800, 900]], text="Pump P-1\n5.5 kW", style="MCP_ML")
    lead = (r.get("leaders") or [[]])[0]
    check("create_mleader to the right", s == 200 and r.get("type") == "MULTILEADER" and r.get("mleader_style") == "MCP_ML"
          and near(r.get("text_height"), 250) and "5.5 kW" in r.get("text", "")
          and same_point(lead[0], [40000, 0]) and same_point(lead[-1], [40800, 900]), str(r)[:500])
    first = r.get("handle")

    s, r = post(base, "create_mleader", points=[[42000, 0], [41300, 900]], text="Wall", style="MCP_ML",
                extra_leaders=[[[42000, -1000], [41300, 900]]])
    leaders = r.get("leaders") or []
    check("create_mleader to the left with 2 leaders", s == 200 and len(leaders) == 2
          and same_point(leaders[0][-1], [41300, 900]) and same_point(leaders[1][0], [42000, -1000])
          and r.get("text_position", [0])[0] < 41300, str(r)[:500])

    s, r = post(base, "create_mleader", points=[[43000, 0], [43500, 500], [44000, 500]], text="Valve",
                text_position=[44300, 800], text_height=100, layer="MCP_NO_LT", color=3)
    lead = (r.get("leaders") or [[]])[0]
    check("create_mleader with 3 points, text_position, text_height, layer, color", s == 200 and len(lead) >= 3
          and near(r.get("text_height"), 100) and same_point(r.get("text_position"), [44300, 800])
          and r.get("layer") == "MCP_NO_LT" and r.get("color") == 3, str(r)[:500])

    check("find_entities MULTILEADER by text", count(base, types="MULTILEADER", text="*P-1*") == 1)

    s, r = post(base, "set_entity_properties", handles=[first], style="Standard")
    _, p = post(base, "get_entity_properties", handles=[first])
    check("set_entity_properties style on a multileader", s == 200
          and (p.get("items") or [{}])[0].get("mleader_style") == "Standard", str(p)[:300])
    s, u = post(base, "undo")
    _, p = post(base, "get_entity_properties", handles=[first])
    check("undo reverts the style change", s == 200 and (p.get("items") or [{}])[0].get("mleader_style") == "MCP_ML", str(p)[:300])

    for name, args, code in [
        ("one point", {"points": [[0, 0]], "text": "x"}, 400),
        ("empty text", {"points": [[0, 0], [1, 1]], "text": ""}, 400),
        ("equal points in a row", {"points": [[0, 0], [0, 0]], "text": "x"}, 400),
        ("unknown style", {"points": [[0, 0], [1, 1]], "text": "x", "style": "NO_SUCH"}, 404),
        ("bad extra_leaders", {"points": [[0, 0], [1, 1]], "text": "x", "extra_leaders": [[[0, 0]]]}, 400),
    ]:
        s, e = post(base, "create_mleader", **args)
        check(f"create_mleader {name} -> {code}", s == code, str(e)[:200])
