"""Smoke test for the AutoCAD MCP bridge.

Needs AutoCAD 2025-2027 running with a drawing open and MCPSTART typed.

    .venv\\Scripts\\python.exe tests\\smoke_test.py            # all checks
    .venv\\Scripts\\python.exe tests\\smoke_test.py --port 8766

Checks:
  1. HTTP: /health, every route, unknown route, bad JSON.
  2. Concurrency: 100 parallel get_document_info calls must all succeed.
  3. MCP: spawn server/acad_mcp_server.py over stdio, list tools, call each.
Read-only: nothing in the drawing is changed.
"""

from __future__ import annotations

import argparse
import asyncio
import json
import os
import sys
import time
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SERVER = ROOT / "server" / "acad_mcp_server.py"

passed: list[str] = []
failed: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    (passed if cond else failed).append(name)
    print(f"  [{'PASS' if cond else 'FAIL'}] {name}" + (f" - {detail}" if detail and not cond else ""))


def http(base: str, method: str, route: str, body: bytes | None = None) -> tuple[int, dict]:
    req = urllib.request.Request(f"{base}/{route}", data=body, method=method)
    req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return r.status, json.loads(r.read())
    except urllib.error.HTTPError as e:
        return e.code, json.loads(e.read())


def test_http(base: str) -> None:
    print("HTTP")
    status, h = http(base, "GET", "health")
    check("health ok", status == 200 and h.get("ok") is True, str(h))
    check("health product=AutoCAD", h.get("product") == "AutoCAD", str(h))
    print(f"    {h.get('host_product')} {h.get('acad_build')}, build {h.get('build_series')}, "
          f".NET {h.get('dotnet')}, {h.get('routes')} routes")

    status, d = http(base, "POST", "get_document_info", b"{}")
    check("get_document_info ok", status == 200 and d.get("ok") is True, str(d))
    for key in ("name", "insunits", "current_layer", "counts", "open_documents"):
        check(f"get_document_info has {key}", key in d, str(d))
    print(f"    {d.get('name')}: {d.get('counts')}")

    status, e = http(base, "POST", "no_such_tool", b"{}")
    check("unknown route -> 404 unknown_tool", status == 404 and e.get("code") == "unknown_tool", str(e))

    status, e = http(base, "POST", "get_document_info", b"{not json")
    check("bad JSON -> 400 bad_request", status == 400 and e.get("code") == "bad_request", str(e))


def test_concurrency(base: str, n: int = 100) -> None:
    print(f"Concurrency ({n} parallel get_document_info)")
    t0 = time.perf_counter()
    with ThreadPoolExecutor(max_workers=32) as pool:
        results = list(pool.map(lambda _: http(base, "POST", "get_document_info", b"{}"), range(n)))
    dt = time.perf_counter() - t0
    ok = sum(1 for s, r in results if s == 200 and r.get("ok"))
    check(f"{n}/{n} succeeded", ok == n, f"{ok}/{n}")
    print(f"    {dt:.2f} s total, {dt / n * 1000:.0f} ms per call")
    status, h = http(base, "GET", "health")
    check("AutoCAD still answers after the burst", status == 200 and h.get("ok") is True)


async def test_mcp(port: int) -> None:
    from mcp.client.session import ClientSession
    from mcp.client.stdio import StdioServerParameters, stdio_client

    print("MCP (stdio)")
    params = StdioServerParameters(
        command=sys.executable, args=[str(SERVER)], env={**os.environ, "ACAD_MCP_PORT": str(port)}
    )
    async with stdio_client(params) as (read, write):
        async with ClientSession(read, write) as session:
            await session.initialize()
            tools = {t.name for t in (await session.list_tools()).tools}
            check("tools listed", {"health_check", "get_document_info"} <= tools, str(tools))
            for name in sorted(tools):
                res = await session.call_tool(name, {})
                check(f"call {name}", not getattr(res, "is_error", False) and not getattr(res, "isError", False),
                      str(res)[:300])


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=int(os.environ.get("ACAD_MCP_PORT", "8766")))
    args = ap.parse_args()
    base = f"http://localhost:{args.port}"
    try:
        test_http(base)
    except urllib.error.URLError as e:
        print(f"Bridge not reachable at {base}: {e}. Type MCPSTART in AutoCAD.")
        return 2
    test_concurrency(base)
    asyncio.run(test_mcp(args.port))
    print(f"\n{len(passed)}/{len(passed) + len(failed)} passed")
    return 0 if not failed else 1


if __name__ == "__main__":
    sys.exit(main())
