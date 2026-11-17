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

SERVER_VERSION = "0.1.2"
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
    offset: Annotated[int, Field(ge=0, description="Skip this many results (paging).")] = 0,
    limit: Annotated[int, Field(ge=1, le=2000, description="Maximum results to return (default 200).")] = 200,
) -> dict[str, Any]:
    """Find entities in the active drawing by type, layer, block name, area and space.

    All filters combine with AND. Returns {total, offset, count, has_more, items}; each item has
    handle, type, layer, color, linetype, lineweight, space (Model / layout name), block (for
    block references) and bounds {min, max}. Use the handle to address the entity in other tools.
    Read-only.
    """
    return await call("find_entities", types=types, layers=layers, blocks=blocks, window=window,
                      space=space, offset=offset, limit=limit)


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


def main() -> None:
    mcp.run("stdio")


if __name__ == "__main__":
    main()
