"""Iteration 4 checks: run_command (and later eval_lisp). Called by smoke_test.py in the fresh test drawing.

With commands allowed (the default test session, AcadSession.ps1) the tools are exercised; in a session
started with -NoCommands only the refusal is checked.
"""

from __future__ import annotations

import glob
import os
import time
from typing import Callable


def run(base: str, post: Callable, count: Callable, check: Callable, http: Callable) -> None:
    print("Commands (iteration 4)")
    _, h = http(base, "GET", "health")
    if not h.get("commands_allowed"):
        s, r = post(base, "run_command", command="_.REGEN")
        check("run_command while not allowed -> 403 commands_disabled", s == 403 and r.get("code") == "commands_disabled"
              and "MCPCOMMANDS" in r.get("error", ""), str(r)[:300])
        return

    _, before = post(base, "get_system_variable", names=["FILEDIA", "CMDDIA", "LOGFILEMODE", "LOGFILEPATH"])
    log_dir = (before.get("values") or {}).get("LOGFILEPATH") or ""
    logs_before = set(glob.glob(os.path.join(log_dir, "*.log"))) if os.path.isdir(log_dir) else set()
    lines0 = count(base, types="LINE")
    s, r = post(base, "run_command", command="_.LINE", inputs=[[70000, 0], [70100, 0], [70100, 50], ""])
    check("run_command LINE with all answers", s == 200 and r.get("completed") is True
          and "Specify first point" in r.get("command_line", "") and count(base, types="LINE") == lines0 + 2, str(r)[:400])

    s, r = post(base, "run_command", command="_.CIRCLE", inputs=[[70000, 0], 30])
    check("run_command CIRCLE center + radius", s == 200 and r.get("completed") is True
          and count(base, types="CIRCLE", window={"min": [69900, -100], "max": [70100, 100]}) == 1, str(r)[:300])

    s, r = post(base, "run_command", command="_.LINE", inputs=[[71000, 0], [71010, 10]])
    check("run_command that still waits is cancelled with its prompt", s == 200 and r.get("completed") is False
          and r.get("cancelled_waiting_for_input") is True and "next point" in (r.get("last_prompt") or "")
          and r.get("still_active") is None, str(r)[:400])

    s, u = post(base, "undo")
    s2, u2 = post(base, "undo")
    check("undo reverts each run_command call as one step", s == 200 and s2 == 200
          and count(base, types="CIRCLE", window={"min": [69900, -100], "max": [70100, 100]}) == 0, str(u2)[:200])
    s, u = post(base, "undo")
    check("undo reverts a multi-segment LINE in one step", s == 200 and count(base, types="LINE") == lines0, str(u)[:200])

    s, r = post(base, "run_command", command="-LAYER", inputs=["_M", "CMD_LAYER", "_C", "3", "", "_S", "0", ""])
    _, lay = post(base, "list_layers", name="CMD_LAYER")
    check("run_command -LAYER with keywords", s == 200 and r.get("completed") is True
          and (lay.get("layers") or [{}])[0].get("color") == 3, str(r)[:300])

    s, r = post(base, "run_command", command="_.NOSUCHCMD")
    check("run_command unknown command -> error", s == 200 and r.get("completed") is False and "unknown" in (r.get("error") or ""), str(r)[:300])

    _, after = post(base, "get_system_variable", names=["FILEDIA", "CMDDIA", "LOGFILEMODE", "LOGFILEPATH"])
    check("run_command restores FILEDIA, CMDDIA, LOGFILEMODE and LOGFILEPATH", before.get("values") == after.get("values"),
          f"{before.get('values')} -> {after.get('values')}")
    if os.path.isdir(log_dir):
        time.sleep(1)
        post(base, "get_document_info")  # AutoCAD idle again: late clean-up of the log runs
        left = set(glob.glob(os.path.join(log_dir, "*.log"))) - logs_before
        check("run_command leaves no command-line log files behind", not left, str(sorted(left)))

    for name, args, code in [
        ("forbidden QUIT", {"command": "_QUIT"}, 403),
        ("forbidden NETLOAD", {"command": "NETLOAD"}, 403),
        ("command with spaces", {"command": "_.LINE 0,0 1,1"}, 400),
        ("bad input", {"command": "_.LINE", "inputs": [{"x": 1}]}, 400),
        ("inputs not a list", {"command": "_.LINE", "inputs": "0,0"}, 400),
    ]:
        s, r = post(base, "run_command", **args)
        check(f"run_command {name} -> {code}", s == code, str(r)[:200])
