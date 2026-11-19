"""Iteration 2, step 6 checks: set_text_style, set_dim_style, set_table_style, set_mleader_style;
style in set_entity_properties; text_style / text_height in create_table.

Called by smoke_test.py in the fresh test drawing.
"""

from __future__ import annotations

from typing import Callable


def near(a, b, tol=1e-6) -> bool:
    return a is not None and b is not None and abs(a - b) < tol


def props(post: Callable, base: str, handle: str) -> dict:
    _, p = post(base, "get_entity_properties", handles=[handle])
    return (p.get("items") or [{}])[0]


def run(base: str, post: Callable, count: Callable, check: Callable) -> None:
    print("Styles (iteration 2, step 6)")

    # ---- text styles
    s, r = post(base, "set_text_style", name="MCP_ISO", font="ISOCPEUR", height=0)
    check("set_text_style creates a TrueType style", s == 200 and r.get("created") is True and r.get("font") == "ISOCPEUR"
          and r.get("truetype") is True and near(r.get("height"), 0), str(r))
    s, r = post(base, "set_text_style", name="MCP_ISO", width_factor=0.8, oblique_deg=15, current=True)
    check("set_text_style changes an existing style", s == 200 and r.get("created") is False and near(r.get("width_factor"), 0.8)
          and near(r.get("oblique_deg"), 15) and r.get("current") is True and r.get("font") == "ISOCPEUR", str(r))
    s, r = post(base, "set_text_style", name="MCP_SHX", font="txt.shx", height=2.5)
    check("set_text_style with an SHX font", s == 200 and r.get("font") == "txt.shx" and r.get("truetype") is False, str(r))
    s, e = post(base, "set_text_style", name="MCP_NOFONT")
    check("new text style without font -> bad_request", s == 400, str(e))
    s, e = post(base, "set_text_style", name="bad<name", font="Arial")
    check("invalid style name -> bad_request", s == 400, str(e))
    post(base, "set_text_style", name="Standard", current=True)

    # ---- dimension styles
    s, r = post(base, "set_dim_style", name="MCP_DIM", based_on="Standard", text_style="MCP_ISO", text_height=250,
                arrow="_ARCHTICK", arrow_size=150, ext_offset=50, ext_extend=100, text_gap=40, decimals=0,
                text_vertical="above", fit="best", current=True)
    check("set_dim_style creates and makes current", s == 200 and r.get("created") is True and r.get("text_style") == "MCP_ISO"
          and near(r.get("text_height"), 250) and "ARCHTICK" in str(r.get("arrow")).upper() and r.get("decimals") == 0
          and r.get("text_vertical") == "above" and r.get("current") is True, str(r))
    s, d = post(base, "create_dimension", p1=[30000, 0], p2=[33000, 0], dim_line_point=[31500, 800])
    check("new dimension follows the current MCP_DIM", s == 200 and d.get("dim_style") == "MCP_DIM" and near(d.get("text_height"), 250), str(d)[:300])
    s, r = post(base, "set_dim_style", name="MCP_DIM", text_height=300)
    check("changing the style changes its dimensions", s == 200 and near(props(post, base, d["handle"]).get("text_height"), 300), str(r))
    s, e = post(base, "set_dim_style", name="MCP_DIM", based_on="Standard")
    check("based_on on an existing style -> bad_request", s == 400, str(e))
    s, e = post(base, "set_dim_style", name="MCP_DIM2", text_style="NO_SUCH")
    check("unknown text style -> not_found", s == 404, str(e))
    post(base, "set_dim_style", name="Standard", current=True)

    # ---- table styles
    s, r = post(base, "set_table_style", name="MCP_TBL", text_style="MCP_ISO",
                text_height={"title": 350, "header": 250, "data": 200}, horizontal_margin=60, vertical_margin=40)
    th = r.get("text_height", {})
    check("set_table_style per-row heights", s == 200 and r.get("created") is True and near(th.get("title"), 350)
          and near(th.get("header"), 250) and near(th.get("data"), 200) and r.get("text_style", {}).get("data") == "MCP_ISO", str(r))
    s, r = post(base, "set_table_style", name="MCP_TBL", text_height=180)
    check("set_table_style one height for all rows", s == 200 and all(near(v, 180) for v in r.get("text_height", {}).values()), str(r))
    s, e = post(base, "set_table_style", name="MCP_TBL", text_height={"footer": 1})
    check("bad row key -> bad_request", s == 400, str(e))

    s, t1 = post(base, "create_table", position=[30000, -1000], data=[["Room", "Area"], ["Kitchen", "12.5"]],
                 text_style="MCP_ISO", text_height=250, column_widths=[3000, 2000])
    p1 = props(post, base, t1.get("handle"))
    height = p1.get("bounds", {}).get("max", [0, 0])[1] - p1.get("bounds", {}).get("min", [0, 0])[1]
    check("create_table text_height makes rows tall enough", s == 200 and height > 2 * 250, f"{height} {str(t1)[:200]}")
    s, e = post(base, "create_table", position=[0, 0], data=[["x"]], text_height=0)
    check("create_table bad text_height -> bad_request", s == 400, str(e))

    # ---- multileader styles
    s, r = post(base, "set_mleader_style", name="MCP_ML", text_style="MCP_ISO", text_height=250, arrow="_DOT",
                arrow_size=100, landing_gap=50, dogleg_length=300, current=True)
    check("set_mleader_style", s == 200 and r.get("created") is True and r.get("text_style") == "MCP_ISO"
          and near(r.get("text_height"), 250) and "DOT" in str(r.get("arrow")).upper() and near(r.get("dogleg_length"), 300)
          and r.get("current") is True, str(r))
    post(base, "set_mleader_style", name="Standard", current=True)

    # ---- style in set_entity_properties
    _, ln = post(base, "create_line", start=[30000, 4000], end=[30100, 4000])
    _, txt = post(base, "create_text", text="Living room", position=[30000, 2000], height=250)
    s, r = post(base, "set_entity_properties", handles=[txt["handle"]], style="MCP_ISO")
    check("text gets a text style", s == 200 and props(post, base, txt["handle"]).get("style") == "MCP_ISO", str(r))

    _, d2 = post(base, "create_dimension", p1=[30000, 3000], p2=[31000, 3000], dim_line_point=[30500, 3500], text_height=99)
    s, r = post(base, "set_entity_properties", handles=[d2["handle"]], style="MCP_DIM")
    p2 = props(post, base, d2["handle"])
    check("dimension gets a style, its overrides dropped", s == 200 and p2.get("dim_style") == "MCP_DIM"
          and near(p2.get("text_height"), 300), str(p2)[:300])

    s, r = post(base, "set_entity_properties", handles=[t1["handle"]], style="MCP_TBL")
    # (the text then takes the style's height; checked visually - rows keep their height as a minimum)
    check("table gets a table style", s == 200 and props(post, base, t1["handle"]).get("table_style") == "MCP_TBL", str(r))

    s, e = post(base, "set_entity_properties", handles=[txt["handle"]], style="MCP_DIM")
    check("text with a dimension style name -> not_found", s == 404, str(e))
    s, e = post(base, "set_entity_properties", handles=[txt["handle"], ln["handle"]], style="MCP_ISO")
    check("style on a line -> bad_request, nothing changed", s == 400 and ln["handle"] in e.get("error", ""), str(e))

    s, u = post(base, "undo")
    check("undo reverts the table style change", s == 200 and props(post, base, t1["handle"]).get("table_style") != "MCP_TBL", str(u))
