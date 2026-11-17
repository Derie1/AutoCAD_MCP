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
from typing import Any

from mcp.server import MCPServer
from mcp.server.mcpserver.exceptions import ToolError

SERVER_VERSION = "0.1.1"
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


def main() -> None:
    mcp.run("stdio")


if __name__ == "__main__":
    main()
