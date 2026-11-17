"""Add or remove the "autocad" MCP server in the Claude Desktop config.

Called by INSTALL.BAT:
    python register_claude.py add    --config <json> --python <python.exe> --server <acad_mcp_server.py>
    python register_claude.py remove --config <json>

Only the "autocad" entry is touched; other servers (navisworks, Revit, ...) and
settings are kept. A backup <config>.bak-autocad is written before the first change.
Exit codes: 0 = done (or already registered), 1 = error, 3 = remove: no entry to remove.
"""

from __future__ import annotations

import argparse
import json
import shutil
import sys
from pathlib import Path

NAME = "autocad"
NOTHING_TO_REMOVE = 3


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("action", choices=["add", "remove"])
    ap.add_argument("--config", required=True)
    ap.add_argument("--python")
    ap.add_argument("--server")
    a = ap.parse_args()

    path = Path(a.config)
    if path.exists():
        try:
            cfg = json.loads(path.read_text(encoding="utf-8") or "{}")
        except ValueError as e:
            print(f"cannot read {path}: {e}")
            return 1
    else:
        if a.action == "remove":
            return NOTHING_TO_REMOVE
        cfg = {}

    servers = cfg.setdefault("mcpServers", {})
    if a.action == "add":
        if not a.python or not a.server:
            print("--python and --server are required for add")
            return 1
        entry = {"command": a.python, "args": [a.server]}
        if servers.get(NAME) == entry:
            return 0
        servers[NAME] = entry
    else:
        if NAME not in servers:
            return NOTHING_TO_REMOVE
        del servers[NAME]

    path.parent.mkdir(parents=True, exist_ok=True)
    backup = path.with_name(path.name + ".bak-autocad")
    if path.exists() and not backup.exists():
        shutil.copy2(path, backup)
    path.write_text(json.dumps(cfg, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main())
