"""Iteration 4 checks: run_command (and later eval_lisp). Called by smoke_test.py in the fresh test drawing.

With commands allowed (the default test session, AcadSession.ps1) the tools are exercised; in a session
started with -NoCommands only the refusal is checked.
"""

from __future__ import annotations

import glob
import os
import subprocess
import time
from typing import Callable


def run(base: str, post: Callable, count: Callable, check: Callable, http: Callable) -> None:
    print("Commands (iteration 4)")
    _, h = http(base, "GET", "health")
    if not h.get("commands_allowed"):
        s, r = post(base, "run_command", command="_.REGEN")
        check("run_command while not allowed -> 403 commands_disabled", s == 403 and r.get("code") == "commands_disabled"
              and "MCPCOMMANDS" in r.get("error", ""), str(r)[:300])
        s, r = post(base, "eval_lisp", expression="(+ 1 2)")
        check("eval_lisp while not allowed -> 403 commands_disabled", s == 403 and r.get("code") == "commands_disabled", str(r)[:300])
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

    _lisp(base, post, count, check)
    _after_com_send_command(base, post, count, check)


def _lisp(base: str, post: Callable, count: Callable, check: Callable) -> None:
    """Step 3: eval_lisp."""
    def ev(expr: str):
        return post(base, "eval_lisp", expression=expr)

    s, r = ev("(+ 1 2)")
    check("eval_lisp number", s == 200 and r.get("value") == 3 and r.get("lisp") == "3", str(r)[:300])
    s, r = ev("(list 1 2.5 \"x\" (list 3 4) '(5 . 6) nil T)")
    check("eval_lisp list with nested list, dotted pair, nil, T", s == 200
          and r.get("value") == [1, 2.5, "x", [3, 4], [5, 6], None, True], str(r)[:300])
    s, r = ev("(getvar \"CLAYER\")")
    check("eval_lisp getvar", s == 200 and isinstance(r.get("value"), str), str(r)[:300])
    s, r = ev('(princ "hello") (princ "\nworld") 42')
    check("eval_lisp several forms, printed output", s == 200 and r.get("value") == 42
          and r.get("output") == "hello\nworld", str(r)[:300])
    s, r = ev("(/ 1 0)")
    check("eval_lisp LISP error comes back as error", s == 200 and r.get("error") == "divide by zero"
          and r.get("completed") is False, str(r)[:300])

    s, r = ev('(entmakex (list (cons 0 "CIRCLE") (cons 10 (list 72000 0 0)) (cons 40 5.0) (cons 8 "0")))')
    handle = (r.get("value") or {}).get("entity")
    _, p = post(base, "get_entity_properties", handles=[handle or "0"])
    check("eval_lisp entmakex returns the entity handle", s == 200 and handle
          and (p.get("items") or [{}])[0].get("type") == "CIRCLE", str(r)[:300])
    s, r = ev(f'(cdr (assoc 40 (entget (handent "{handle}"))))')
    check("eval_lisp entget by handle", s == 200 and r.get("value") == 5, str(r)[:300])
    s, r = ev('(ssget "_X" (list (cons 0 "CIRCLE") (cons 10 (list 72000 0 0))))')
    check("eval_lisp selection set", s == 200 and (r.get("value") or {}).get("selection_set", 0) >= 1, str(r)[:300])
    s, u = post(base, "undo")
    s2, u2 = post(base, "undo")
    s3, u3 = post(base, "undo")
    _, p = post(base, "get_entity_properties", handles=[handle])
    check("undo reverts eval_lisp calls one by one", s == s2 == s3 == 200 and "error" in (p.get("items") or [{}])[0], str(p)[:300])

    for name, expr, code in [
        ("unclosed form", "(+ 1 2", 400),
        ("extra ')'", '(princ "x"))', 400),
        ("unclosed string", '(strcat "abc)', 400),
        ("command", '(command "_.LINE" (list 0 0) (list 1 1) "")', 400),
        ("command-s", '(command-s "_.REGEN")', 400),
        ("getpoint", '(getpoint "Pick: ")', 400),
        ("interactive ssget", "(ssget)", 400),
        ("empty", "  ", 400),
    ]:
        s, r = ev(expr)
        check(f"eval_lisp {name} -> {code}", s == code, str(r)[:200])
    s, r = ev('(strcat "(command " "x") ; (getpoint)')
    check("eval_lisp: command words inside strings and comments are fine", s == 200 and r.get("value") == "(command x", str(r)[:300])


def _after_com_send_command(base: str, post: Callable, count: Callable, check: Callable) -> None:
    """A command sent to a drawing through COM SendCommand (another program) makes AutoCAD stop starting
    command-context requests in that drawing (PLAN section 10); the bridge re-activates the drawing.
    Runs in a drawing of its own: re-activation needs a second open drawing."""
    _, info = post(base, "get_document_info")
    home = info.get("name")
    s, d = post(base, "new_document")
    if s != 200:
        check("new drawing for the COM SendCommand check", False, str(d)[:200])
        return
    ps = ("$a=[Runtime.InteropServices.Marshal]::GetActiveObject('AutoCAD.Application');"
          "$a.ActiveDocument.SendCommand('_.REGEN ');[void][Runtime.InteropServices.Marshal]::ReleaseComObject($a)")
    try:
        sent = subprocess.run(["powershell", "-NoProfile", "-Command", ps], capture_output=True, text=True, timeout=60)
        check("COM SendCommand to the new drawing", sent.returncode == 0, sent.stderr[:200])
        time.sleep(1)
        post(base, "create_line", start=[0, 0], end=[100, 0])
        t = time.time()
        s, u = post(base, "undo")
        check("undo after COM SendCommand works (no hang)", s == 200 and count(base, types="LINE") == 0
              and time.time() - t < 10, f"{time.time() - t:.1f}s {u}")
        s, r = post(base, "run_command", command="_.REGEN")
        check("run_command after COM SendCommand works", s == 200 and r.get("completed") is True, str(r)[:200])
        _, now = post(base, "get_document_info")
        check("the drawing stays active after re-activation", now.get("name") == d.get("name"), str(now.get("name")))
    finally:
        post(base, "close_document", discard_changes=True)
        if home:
            post(base, "set_active_document", name=home)
