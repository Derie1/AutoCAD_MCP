"""AutoCAD MCP server (stdio).

Translates MCP tool calls into JSON HTTP requests to the AcadMcpBridge plugin
running inside AutoCAD (http://localhost:8766/ by default). It knows nothing
about the AutoCAD API, and nothing about the Navisworks MCP bridge: both
servers can run side by side.

Environment:
  ACAD_MCP_PORT     bridge port (default 8766; must match config.json of the plugin)
  ACAD_MCP_TOKEN    optional token, sent as X-Bridge-Token
  ACAD_MCP_TIMEOUT  HTTP timeout in seconds (default 60)

Requires the `mcp` package 2.x (see requirements.txt).
"""

from __future__ import annotations

import asyncio
import json
import os
import urllib.error
import urllib.request
from typing import Annotated, Any

from mcp.server import MCPServer
from pydantic import Field
from mcp.server.mcpserver.exceptions import ToolError

SERVER_VERSION = "0.1.5"
PORT = int(os.environ.get("ACAD_MCP_PORT", "8766"))
TOKEN = os.environ.get("ACAD_MCP_TOKEN", "")
TIMEOUT = float(os.environ.get("ACAD_MCP_TIMEOUT", "60"))
BASE_URL = f"http://localhost:{PORT}"

NOT_RUNNING = (
    f"The AutoCAD MCP bridge is not reachable at {BASE_URL}. "
    "Start AutoCAD 2025-2027 and type MCPSTART in its command line."
)


class BridgeError(ToolError):
    """An error reported by the bridge or the transport.

    Subclasses ToolError: in mcp 2.x only ToolError messages reach the model;
    any other exception is reported as a bare "Error executing tool ...".
    """


def _http(method: str, route: str, args: dict[str, Any] | None = None) -> dict[str, Any]:
    data = None if args is None else json.dumps(args).encode("utf-8")
    req = urllib.request.Request(f"{BASE_URL}/{route}", data=data, method=method)
    req.add_header("Content-Type", "application/json; charset=utf-8")
    if TOKEN:
        req.add_header("X-Bridge-Token", TOKEN)
    try:
        with urllib.request.urlopen(req, timeout=TIMEOUT) as resp:
            body = resp.read()
    except urllib.error.HTTPError as e:  # bridge answered with an error status
        body = e.read()
    except (urllib.error.URLError, ConnectionError, TimeoutError) as e:
        raise BridgeError(f"{NOT_RUNNING} ({e})") from None
    try:
        result = json.loads(body.decode("utf-8"))
    except ValueError:
        raise BridgeError(f"Port {PORT} answered with something that is not the AutoCAD bridge.") from None
    if not isinstance(result, dict):
        raise BridgeError(f"Unexpected answer from port {PORT}: {result!r}")
    return result


_verified = False


def _check_product() -> None:
    """Make sure the port belongs to AutoCAD (not Navisworks or anything else)."""
    global _verified
    if _verified:
        return
    health = _http("GET", "health")
    if health.get("product") != "AutoCAD":
        raise BridgeError(
            f"Port {PORT} is served by {health.get('product') or 'an unknown program'}, not by the "
            "AutoCAD bridge. Set ACAD_MCP_PORT and the plugin's config.json port to a free port."
        )
    _verified = True


def _call_sync(route: str, args: dict[str, Any]) -> dict[str, Any]:
    _check_product()
    result = _http("POST", route, {k: v for k, v in args.items() if v is not None})
    if not result.pop("ok", False):
        code = result.get("code", "error")
        raise BridgeError(f"[{code}] {result.get('error', 'unknown error')}")
    return result


async def call(route: str, **args: Any) -> dict[str, Any]:
    return await asyncio.to_thread(_call_sync, route, args)


mcp = MCPServer(
    name="autocad",
    version=SERVER_VERSION,
    instructions=(
        "Tools that control a running AutoCAD 2025-2027 through the AcadMcpBridge plugin. "
        "If a tool says the bridge is not reachable, ask the user to type MCPSTART in AutoCAD. "
        "Coordinates are [x, y, z] in drawing units (WCS); see get_document_info for the units."
    ),
)


@mcp.tool()
async def health_check() -> dict[str, Any]:
    """Check that AutoCAD is running with the MCP bridge started.

    Returns plugin version, AutoCAD product and build, .NET version, port and
    number of available routes. Answers even while AutoCAD is busy.
    """
    result = await asyncio.to_thread(_http, "GET", "health")
    result.pop("ok", None)
    result["server_version"] = SERVER_VERSION
    return result


@mcp.tool()
async def get_document_info() -> dict[str, Any]:
    """Summary of the active drawing in AutoCAD.

    Returns file name and path, unsaved changes, read-only flag, DWG version,
    units (INSUNITS, metric/imperial), drawing extents, current layer, space and
    layout, counts of model-space entities / layers / block definitions /
    paper layouts, the command in progress (if any) and the number of open drawings.
    """
    return await call("get_document_info")


# ---------------------------------------------------------------- shared parameter types

Point = Annotated[list[float], Field(min_length=2, max_length=3, description="[x, y] or [x, y, z] in WCS drawing units")]
Color = Annotated[
    str | int | None,
    Field(description='"ByLayer", "ByBlock", an ACI index 1-255 or "#RRGGBB". Default: the current color (usually ByLayer).'),
]
Layer = Annotated[str | None, Field(description="Existing layer name. Default: the current layer.")]
NameList = str | list[str] | None


# ---------------------------------------------------------------- query

@mcp.tool()
async def find_entities(
    types: Annotated[NameList, Field(description=(
        'DXF type names, e.g. "LINE", ["LWPOLYLINE", "INSERT"]. Aliases: "TABLE" (AutoCAD tables, ACAD_TABLE), '
        '"BLOCK" (block references, INSERT), "POLYLINE" (LWPOLYLINE + POLYLINE), "TEXT_ANY" (TEXT + MTEXT).'))] = None,
    layers: Annotated[NameList, Field(description="Layer names; * and ? wildcards allowed.")] = None,
    blocks: Annotated[NameList, Field(description="Block names for block references (dynamic blocks by definition name); wildcards allowed.")] = None,
    window: Annotated[dict[str, Any] | None, Field(description=(
        'Area in XY: {"min": [x, y], "max": [x, y], "mode": "inside" | "crossing"}. '
        '"inside" (default) = entirely inside, "crossing" = touches the area.'))] = None,
    space: Annotated[str, Field(description='"all" (default), "model", "paper" (all layouts) or a layout name.')] = "all",
    text: Annotated[NameList, Field(description=(
        "Text pattern, * and ? wildcards, case-insensitive; use *word* to search for a substring. Matches TEXT/MTEXT "
        "content, block attribute values, dimension text overrides and AutoCAD table cells; other types never match."))] = None,
    offset: Annotated[int, Field(ge=0, description="Skip this many results (paging).")] = 0,
    limit: Annotated[int, Field(ge=1, le=2000, description="Maximum results to return (default 200).")] = 200,
) -> dict[str, Any]:
    """Find entities in the active drawing by type, layer, block name, area, space and text.

    All filters combine with AND. Returns {total, offset, count, has_more, items}; each item has
    handle, type, layer, color, linetype, lineweight, space (Model / layout name), block (for
    block references) and bounds {min, max}. Use the handle to address the entity in other tools.
    Read-only.
    """
    return await call("find_entities", types=types, layers=layers, blocks=blocks, window=window,
                      space=space, text=text, offset=offset, limit=limit)


@mcp.tool()
async def get_entity_properties(
    handles: Annotated[list[str], Field(min_length=1, max_length=100, description="Entity handles from find_entities or get_selection.")],
    include_xdata: Annotated[bool, Field(description="Also return extended data (XData).")] = False,
) -> dict[str, Any]:
    """Full properties of entities by handle.

    Common: handle, type, layer, color, linetype, lineweight, space, bounds, visible, transparency.
    Plus per type: LINE start/end/length/angle; CIRCLE center/radius/area; ARC angles/length;
    LWPOLYLINE vertices (with bulges, widths), closed, length, area; TEXT/MTEXT text, position,
    height, rotation, style (MTEXT also raw "contents" with formatting codes); INSERT block,
    position, rotation, scale, attributes [{tag, value}], dynamic block properties; DIMENSION
    measurement and text override; HATCH pattern/area; ACAD_TABLE size and style (cell contents:
    get_table). An unknown handle gives an item with "error" instead of failing the whole call.
    """
    return await call("get_entity_properties", handles=handles, include_xdata=include_xdata)


@mcp.tool()
async def get_selection(limit: Annotated[int, Field(ge=1, le=2000)] = 200) -> dict[str, Any]:
    """Objects the user has selected in AutoCAD right now (selected before calling, with grips shown).

    Returns {total, count, has_more, items} with the same entity summary as find_entities.
    Use it when the user says "this", "these objects", "the selected table".
    """
    return await call("get_selection", limit=limit)


@mcp.tool()
async def get_system_variable(
    names: Annotated[list[str], Field(min_length=1, description='System variable names, e.g. ["INSUNITS", "LTSCALE", "CLAYER"].')],
) -> dict[str, Any]:
    """Read AutoCAD system variables. Returns {values: {NAME: value}}; unknown names get {"error": ...}."""
    return await call("get_system_variable", names=names)


# ---------------------------------------------------------------- catalog (read-only)

@mcp.tool()
async def list_documents() -> dict[str, Any]:
    """Drawings open in AutoCAD: name, path (null if never saved), active, read_only,
    has_unsaved_changes (known only for the active drawing). All other tools work on the active drawing."""
    return await call("list_documents")


@mcp.tool()
async def list_layers(
    name: Annotated[str | None, Field(description="Layer name filter with * and ? wildcards.")] = None,
    with_counts: Annotated[bool, Field(description="Count entities per layer (model space and layouts).")] = True,
) -> dict[str, Any]:
    """Layers of the active drawing: name, color, linetype, lineweight, on, frozen, locked,
    plottable, current, from_xref, description and the number of entities on each."""
    return await call("list_layers", name=name, with_counts=with_counts)


@mcp.tool()
async def list_blocks(
    name: Annotated[str | None, Field(description="Block name filter with * and ? wildcards.")] = None,
    include_anonymous: bool = False,
    include_xrefs: bool = False,
) -> dict[str, Any]:
    """Block definitions: name, dynamic, references (number of insertions, dynamic blocks included),
    entities, attribute definitions [{tag, prompt, default, constant, invisible}], origin, units."""
    return await call("list_blocks", name=name, include_anonymous=include_anonymous, include_xrefs=include_xrefs)


@mcp.tool()
async def list_layouts() -> dict[str, Any]:
    """Model and paper layouts in tab order: name, current, entities, viewports, plot device,
    paper size, plot style table and rotation."""
    return await call("list_layouts")


@mcp.tool()
async def list_xrefs() -> dict[str, Any]:
    """External references: name, saved path, status (Resolved, Unloaded, FileNotFound, ...),
    overlay or attachment, nested, number of references."""
    return await call("list_xrefs")


@mcp.tool()
async def list_styles(
    kinds: Annotated[list[str] | None, Field(description=(
        'Which styles: any of "text", "dimension", "table", "multileader", "linetype". Default: all.'))] = None,
) -> dict[str, Any]:
    """Named styles of the drawing with the current one marked. Text styles include font,
    height, width factor and oblique angle; dimension styles text height, arrow size, scale."""
    return await call("list_styles", kinds=kinds)


# ---------------------------------------------------------------- tables and attributes

Range = Annotated[list[int] | None, Field(min_length=1, max_length=2, description="[first, last], 0-based, inclusive.")]


@mcp.tool()
async def get_table(
    handle: Annotated[str | None, Field(description=(
        "Handle of the AutoCAD table (find_entities types=TABLE). Omit to use the one table the user has "
        "selected, or the only table in the drawing."))] = None,
    rows: Range = None,
    columns: Range = None,
    format: Annotated[str, Field(description=(
        '"plain" (default): displayed text without formatting codes; "raw": text with MTEXT formatting codes; '
        '"value": typed values (numbers stay numbers).'))] = "plain",
    details: Annotated[bool, Field(description="Return each cell as an object {value, formula, locked, linked, merged_into, data_type}.")] = False,
) -> dict[str, Any]:
    """Read the contents of an AutoCAD table (ACAD_TABLE).

    Returns rows/columns count, range read, cells as a 2D array [row][column] (0-based; row 0 is
    usually the title, row 1 the header; see row_styles: _TITLE/_HEADER/_DATA), row_heights,
    column_widths, merged ranges [top, left, bottom, right] (only the top-left cell of a merged
    range has the value, the others are null), formulas [{row, column, formula, value}],
    locked_cells (content locked or read-only, e.g. linked to Excel: they cannot be written),
    block_cells (block name and attribute values), data_links and, if the user has selected
    cells inside the table, selected_cells. Tables over 5000 cells are returned in parts:
    "truncated" is true and "hint" says which rows to read next.
    """
    return await call("get_table", handle=handle, rows=rows, columns=columns, format=format, details=details)


@mcp.tool()
async def extract_block_attributes(
    blocks: Annotated[NameList, Field(description="Block names, wildcards allowed. Default: all blocks with attributes.")] = None,
    tags: Annotated[list[str] | None, Field(description="Only these attribute tags (columns). Default: all tags found.")] = None,
    layers: Annotated[NameList, Field(description="Layer filter, wildcards allowed.")] = None,
    space: Annotated[str, Field(description='"all" (default), "model", "paper" or a layout name.')] = "all",
    window: Annotated[dict[str, Any] | None, Field(description='{"min": [x, y], "max": [x, y], "mode": "inside" | "crossing"}')] = None,
    offset: Annotated[int, Field(ge=0)] = 0,
    limit: Annotated[int, Field(ge=1, le=5000)] = 500,
) -> dict[str, Any]:
    """Attribute values of block references as a table, e.g. for a schedule or an Excel export.

    Returns {total, offset, count, has_more, columns, rows}: columns are handle, block, space,
    layer, position and then one column per attribute tag; rows hold the values (null when a
    block has no such tag). Read-only.
    """
    return await call("extract_block_attributes", blocks=blocks, tags=tags, layers=layers, space=space,
                      window=window, offset=offset, limit=limit)


# ---------------------------------------------------------------- draw

@mcp.tool()
async def create_line(start: Point, end: Point, layer: Layer = None, color: Color = None) -> dict[str, Any]:
    """Draw a line from start to end in the current space (model space or the active layout).

    Returns the new entity (handle, type, layer, color, bounds) and its length.
    The call is one step for the undo tool.
    """
    return await call("create_line", start=start, end=end, layer=layer, color=color)


# ---------------------------------------------------------------- edit

@mcp.tool()
async def undo(steps: Annotated[int, Field(ge=1, le=100, description="How many MCP changes to revert.")] = 1) -> dict[str, Any]:
    """Revert the last change(s) made through these tools, using AutoCAD's UNDO.

    Each changing tool call is exactly one undo step. Refuses if the user has run any command in
    AutoCAD since the last MCP change, so the user's own work is never undone; in that case ask
    the user to use UNDO in AutoCAD. Returns {undone, remaining_mcp_changes}.
    """
    return await call("undo", steps=steps)


Handles = Annotated[list[str] | None, Field(description="Entity handles to act on (from find_entities / get_selection). Use this OR filter.")]
Filter = Annotated[dict[str, Any] | None, Field(description=(
    "Select the objects by a filter instead of handles: the find_entities keys (types, layers, blocks, window, "
    "space, text). A filter needs confirm: true; run with dry_run: true first to see what it hits."))]
DryRun = Annotated[bool, Field(description="Change nothing; report how many and which objects would be affected.")]
Confirm = Annotated[bool, Field(description="Required when objects are chosen by filter. Ask the user before bulk changes.")]

@mcp.tool()
async def set_entity_properties(
    handles: Handles = None,
    filter: Filter = None,
    layer: Annotated[str | None, Field(description="Move to this existing layer.")] = None,
    color: Color = None,
    linetype: Annotated[str | None, Field(description='Linetype name, "ByLayer" or "ByBlock"; loaded from acadiso.lin/acad.lin if missing.')] = None,
    lineweight: Annotated[str | float | None, Field(description='"ByLayer", "ByBlock", "Default" or millimetres, e.g. 0.25.')] = None,
    linetype_scale: Annotated[float | None, Field(gt=0)] = None,
    transparency: Annotated[str | float | None, Field(description='"ByLayer", "ByBlock" or percent 0-90.')] = None,
    visible: bool | None = None,
    dry_run: DryRun = False,
    confirm: Confirm = False,
) -> dict[str, Any]:
    """Change layer, color, linetype, lineweight, linetype scale, transparency or visibility of objects.

    Objects are chosen by handles or by filter. Objects on locked layers make the whole call fail
    (nothing changes); unlock the layer first. One call = one step for the undo tool.
    """
    return await call("set_entity_properties", handles=handles, filter=filter, layer=layer, color=color,
                      linetype=linetype, lineweight=lineweight, linetype_scale=linetype_scale,
                      transparency=transparency, visible=visible, dry_run=dry_run, confirm=confirm)



@mcp.tool()
async def transform_entities(
    operation: Annotated[str, Field(description='"move", "copy", "rotate", "scale" or "mirror".')],
    handles: Handles = None,
    filter: Filter = None,
    vector: Annotated[list[float] | None, Field(description="move/copy: displacement [dx, dy] or [dx, dy, dz] (or give from + to).")] = None,
    from_point: Annotated[list[float] | None, Field(description="move/copy: base point (with to).")] = None,
    to: Annotated[list[float] | None, Field(description="move/copy: target point.")] = None,
    count: Annotated[int, Field(ge=1, le=1000, description="copy: number of copies, each one more displacement further (an array).")] = 1,
    base: Annotated[list[float] | None, Field(description="rotate/scale: base point.")] = None,
    angle_deg: Annotated[float | None, Field(description="rotate: angle in degrees, counter-clockwise.")] = None,
    factor: Annotated[float | None, Field(gt=0, description="scale: factor.")] = None,
    p1: Annotated[list[float] | None, Field(description="mirror: first point of the mirror line.")] = None,
    p2: Annotated[list[float] | None, Field(description="mirror: second point of the mirror line.")] = None,
    keep_original: Annotated[bool, Field(description="mirror: keep the originals (default true).")] = True,
    dry_run: DryRun = False,
    confirm: Confirm = False,
) -> dict[str, Any]:
    """Move, copy (also as a linear array), rotate, scale or mirror objects.

    Returns {operation, affected} and, for copy and mirror with keep_original, created_handles.

    Objects are chosen by handles or by filter. Objects on locked layers make the whole call fail
    (nothing changes); unlock the layer first. One call = one step for the undo tool.
    """
    return await call("transform_entities", operation=operation, handles=handles, filter=filter, vector=vector,
                      **{"from": from_point}, to=to, count=count, base=base, angle_deg=angle_deg, factor=factor,
                      p1=p1, p2=p2, keep_original=keep_original, dry_run=dry_run, confirm=confirm)



@mcp.tool()
async def delete_entities(handles: Handles = None, filter: Filter = None,
                          dry_run: DryRun = False, confirm: Confirm = False) -> dict[str, Any]:
    """Delete objects. Returns {deleted}. Ask the user before deleting objects they did not ask about.

    Objects are chosen by handles or by filter. Objects on locked layers make the whole call fail
    (nothing changes); unlock the layer first. One call = one step for the undo tool.
    """
    return await call("delete_entities", handles=handles, filter=filter, dry_run=dry_run, confirm=confirm)



@mcp.tool()
async def set_selection(
    handles: Annotated[list[str], Field(description="Objects to select with grips in AutoCAD; [] clears the selection.")],
) -> dict[str, Any]:
    """Select objects in AutoCAD for the user (shows grips), e.g. to point at what was found.
    Does not change the drawing."""
    return await call("set_selection", handles=handles)


@mcp.tool()
async def set_system_variable(
    name: str,
    value: Annotated[str | float | int | list[float], Field(description="New value; type must fit the variable.")],
) -> dict[str, Any]:
    """Set an AutoCAD system variable. Returns {name, old, new}. Read-only variables are refused.
    Counts as one undo step."""
    return await call("set_system_variable", name=name, value=value)


# ---------------------------------------------------------------- layers

LayerColor = Annotated[str | int | None, Field(description='ACI index 1-255 or "#RRGGBB".')]


@mcp.tool()
async def create_layer(
    name: str,
    color: LayerColor = None,
    linetype: Annotated[str | None, Field(description="Linetype name; loaded from acadiso.lin/acad.lin if missing.")] = None,
    lineweight: Annotated[str | float | None, Field(description='"Default" or millimetres, e.g. 0.35.')] = None,
    plottable: bool | None = None,
    description: str | None = None,
    current: Annotated[bool, Field(description="Make it the current layer.")] = False,
) -> dict[str, Any]:
    """Create a new layer. Fails with already_exists if the name is taken (then use set_layer_state)."""
    return await call("create_layer", name=name, color=color, linetype=linetype, lineweight=lineweight,
                      plottable=plottable, description=description, current=current)


@mcp.tool()
async def set_layer_state(
    names: Annotated[list[str], Field(description="Layer names, * and ? wildcards allowed.")],
    on: bool | None = None,
    frozen: bool | None = None,
    locked: bool | None = None,
    plottable: bool | None = None,
    current: Annotated[bool, Field(description="Make the (single) layer current.")] = False,
    color: LayerColor = None,
    linetype: str | None = None,
    lineweight: Annotated[str | float | None, Field(description='"Default" or millimetres.')] = None,
    description: str | None = None,
    new_name: Annotated[str | None, Field(description="Rename (exactly one layer, no wildcards).")] = None,
) -> dict[str, Any]:
    """Turn layers on/off, freeze/thaw, lock/unlock, set plottable, color, linetype, lineweight,
    description, make current or rename. Only the given settings change. The current layer
    cannot be frozen. Returns the changed layers."""
    return await call("set_layer_state", names=names, on=on, frozen=frozen, locked=locked, plottable=plottable,
                      current=current or None, color=color, linetype=linetype, lineweight=lineweight,
                      description=description, new_name=new_name)


def main() -> None:
    mcp.run("stdio")


if __name__ == "__main__":
    main()
