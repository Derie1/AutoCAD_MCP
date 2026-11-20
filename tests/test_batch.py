"""batch_run / batch_status through the MCP server (stdio). No AutoCAD window is needed.

    .venv\\Scripts\\python.exe tests\\test_batch.py
    .venv\\Scripts\\python.exe tests\\test_batch.py --years 2026

Spawns "dotnet bin\\Release\\Server\\acad_mcp_server.dll" (Build-Package.ps1); the server takes the
plugin next to it (bin\\Release\\R25|R26). Works on copies of AutoCAD's sample "Architectural - Metric.dwg"
in %TEMP%\\acadmcp_batch_mcp (removed at the end).
"""
import argparse
import asyncio
import json
import os
import shutil
import sys
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SERVER = ROOT / "bin" / "Release" / "Server" / "acad_mcp_server.dll"
WORK = Path(tempfile.gettempdir()) / "acadmcp_batch_mcp"

passed: list[str] = []
failed: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    (passed if cond else failed).append(name)
    print(f"  [{'PASS' if cond else 'FAIL'}] {name}" + ("" if cond else f"  -- {detail}"))


def sample(year: str) -> Path:
    return Path(rf"C:\Program Files\Autodesk\AutoCAD {year}\Sample\en-us\Dynamic Blocks\Architectural - Metric.dwg")


def log_paths() -> dict[str, str]:
    """LogFilePath of every AutoCAD profile of this user (the batch must not change any)."""
    import winreg
    found: dict[str, str] = {}

    def walk(key, path: str) -> None:
        try:
            found[path] = winreg.QueryValueEx(key, "LogFilePath")[0]
        except OSError:
            pass
        i = 0
        while True:
            try:
                name = winreg.EnumKey(key, i)
            except OSError:
                break
            i += 1
            try:
                with winreg.OpenKey(key, name) as sub:
                    walk(sub, path + "\\" + name)
            except OSError:
                pass

    for release in ("R25.0", "R25.1", "R26.0"):
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, rf"Software\Autodesk\AutoCAD\{release}") as k:
                walk(k, release)
        except OSError:
            pass
    return found


async def call(session, tool: str, args: dict) -> tuple[bool, dict | str]:
    res = await session.call_tool(tool, args)
    text = res.content[0].text if res.content else ""
    is_error = bool(getattr(res, "is_error", False) or getattr(res, "isError", False))
    if is_error:
        return False, text
    return True, json.loads(text)


def prepare(year: str) -> Path:
    if WORK.exists():
        shutil.rmtree(WORK)
    (WORK / "sub").mkdir(parents=True)
    for name in ("a.dwg", "b.dwg", "c.dwg"):
        shutil.copy(sample(year), WORK / name)
    shutil.copy(sample(year), WORK / "sub" / "d.dwg")
    (WORK / "broken.dwg").write_bytes(b"this is not a drawing")
    return WORK


async def run_year(session, year: str) -> None:
    print(f"== AutoCAD {year}")
    folder = prepare(year)
    read_steps = [
        {"tool": "get_document_info"},
        {"tool": "list_layers", "args": {"with_counts": True}},
        {"tool": "audit"},
        {"tool": "purge", "args": {"dry_run": True}},
        {"tool": "delete_entities", "args": {"filter": {"types": ["INSERT"]}, "dry_run": True}},
    ]

    t = time.time()
    ok, r = await call(session, "batch_run", {"folder": str(folder), "pattern": "?.dwg", "steps": read_steps,
                                              "autocad_version": year})
    check("read batch: done", ok and r["status"] == "done", str(r)[:300])
    if ok:
        check("read batch: 3 files ok", r["total_files"] == 3 and r["ok_files"] == 3, json.dumps(r)[:300])
        check("read batch: the asked AutoCAD", r["autocad_version"] == year, str(r.get("autocad_version")))
        f0 = r["files"][0]
        check("read batch: per-step results", len(f0["steps"]) == 5 and all(s["ok"] for s in f0["steps"])
              and f0["steps"][1]["result"].get("layers"), json.dumps(f0)[:300])
        check("read batch: files in list order", [Path(f["file"]).name for f in r["files"]] == ["a.dwg", "b.dwg", "c.dwg"],
              str([f["file"] for f in r["files"]]))
        print(f"    3 files: {time.time() - t:.1f} s, parallel {r['parallel']}")
    check("read batch: drawings untouched",
          all((folder / n).stat().st_size == sample(year).stat().st_size for n in ("a.dwg", "b.dwg", "c.dwg"))
          and not list(folder.glob("*.bak")), str(list(folder.iterdir())))

    # recursive + a broken file + files list; a failed step stops that file unless continue_on_error
    ok, r = await call(session, "batch_run", {"folder": str(folder), "recursive": True, "autocad_version": year,
                                              "steps": [{"tool": "get_table"}, {"tool": "list_layouts"}]})
    if ok:
        names = sorted(Path(f["file"]).name for f in r["files"])
        check("recursive: 5 files incl. sub\\d.dwg and broken.dwg", names == ["a.dwg", "b.dwg", "broken.dwg", "c.dwg", "d.dwg"], str(names))
        broken = next(f for f in r["files"] if f["file"].endswith("broken.dwg"))
        check("broken file: failed with a reason", not broken["ok"] and broken.get("error"), json.dumps(broken)[:300])
        a = next(f for f in r["files"] if f["file"].endswith("a.dwg"))
        check("failed step stops the file", not a["ok"] and len(a["steps"]) == 1 and a["steps"][0]["code"] == "not_found",
              json.dumps(a)[:300])
    else:
        check("recursive batch ran", False, str(r)[:300])
    ok, r = await call(session, "batch_run", {"files": [str(folder / "a.dwg")], "continue_on_error": True,
                                              "autocad_version": year,
                                              "steps": [{"tool": "get_table"}, {"tool": "list_layouts"}]})
    check("continue_on_error: next step runs", ok and len(r["files"][0]["steps"]) == 2 and r["files"][0]["steps"][1]["ok"],
          str(r)[:300])

    # long batch: wait_seconds 0 -> job_id, then batch_status until done
    ok, r = await call(session, "batch_run", {"folder": str(folder), "pattern": "?.dwg", "wait_seconds": 0,
                                              "steps": [{"tool": "list_blocks"}], "autocad_version": year})
    check("wait_seconds 0: running with a job_id", ok and r["status"] == "running" and r["job_id"], str(r)[:300])
    if ok:
        job = r["job_id"]
        for _ in range(120):
            ok2, s = await call(session, "batch_status", {"job_id": job})
            if not ok2 or s["status"] != "running":
                break
            await asyncio.sleep(1)
        check("batch_status: done, 3 ok", ok2 and s["status"] == "done" and s["ok_files"] == 3, str(s)[:300])
        ok3, lst = await call(session, "batch_status", {})
        check("batch_status without job_id lists the batch", ok3 and any(j["job_id"] == job for j in lst["jobs"]), str(lst)[:300])

    # cancel
    ok, r = await call(session, "batch_run", {"folder": str(folder), "pattern": "?.dwg", "wait_seconds": 0,
                                              "parallel": 1, "steps": [{"tool": "list_layers"}], "autocad_version": year})
    if ok:
        ok2, s = await call(session, "batch_status", {"job_id": r["job_id"], "cancel": True})
        check("cancel: cancelled, files skipped", ok2 and s["status"] == "cancelled" and s["skipped_files"] + s["failed_files"] >= 1,
              str(s)[:400])
    else:
        check("cancel batch started", False, str(r)[:300])


def bridge(route: str, **args) -> dict | None:
    """The running AutoCAD's bridge, or None when it is not running (that check is skipped)."""
    import urllib.request
    try:
        req = urllib.request.Request(f"http://localhost:8766/{route}", data=json.dumps(args).encode(),
                                     headers={"Content-Type": "application/json"})
        with urllib.request.urlopen(req, timeout=60) as r:
            return json.loads(r.read())
    except Exception:
        return None


async def open_in_autocad(session, year: str) -> None:
    """A drawing open in the running AutoCAD (locked) is still read by the batch (/readonly)."""
    health = bridge("health")
    if not health or health.get("product") != "AutoCAD":
        print("== open in AutoCAD: SKIPPED (AutoCAD with the bridge is not running)")
        return
    print("== a drawing open in AutoCAD")
    path = WORK / "open.dwg"
    shutil.copy(sample(year), path)
    opened = bridge("open_document", path=str(path))
    try:
        check("open the drawing in AutoCAD", bool(opened and opened.get("ok")), str(opened)[:200])
        ok, r = await call(session, "batch_run", {"files": [str(path)], "steps": [{"tool": "list_layers"}]})
        check("batch reads a drawing open in AutoCAD", ok and r["ok_files"] == 1, str(r)[:300])
    finally:
        bridge("close_document", path=str(path), discard_changes=True)


def header(path: Path) -> bytes:
    return path.read_bytes()[:6]


async def writes(session, year: str) -> None:
    """Changing steps: copies to an output folder, in place with .bak, skipped files, no save after a failed step."""
    print(f"== changing drawings (AutoCAD {year})")
    folder = prepare(year)
    (folder / "broken.dwg").unlink()
    version = header(sample(year))
    out = WORK.parent / "acadmcp_batch_mcp_out"
    shutil.rmtree(out, ignore_errors=True)
    change = [{"tool": "create_layer", "args": {"name": "MCP_BATCH", "color": 3}},
              {"tool": "create_line", "args": {"start": [0, 0], "end": [10, 10], "layer": "MCP_BATCH"}}]
    try:
        ok, r = await call(session, "batch_run", {"folder": str(folder), "recursive": True, "steps": change,
                                                  "save": "copy", "output_folder": str(out), "autocad_version": year})
        check("copy: 4 drawings changed and saved", ok and r["saved_files"] == 4 and r["changed_files"] == 4
              and r["save"] == "copy", str(r)[:400])
        check("copy: subfolder kept", (out / "sub" / "d.dwg").exists(), str(list(out.rglob("*"))))
        check("copy: same DWG version", all(header(f) == version for f in out.rglob("*.dwg")), str(version))
        check("copy: originals unchanged, no .bak or .dwl",
              all((folder / n).stat().st_size == sample(year).stat().st_size for n in ("a.dwg", "b.dwg", "c.dwg"))
              and not list(folder.rglob("*.bak")) and not list(folder.rglob("*.dwl*")), str(list(folder.rglob("*"))))
        ok, r = await call(session, "batch_run", {"folder": str(out), "recursive": True, "autocad_version": year,
                                                  "steps": [{"tool": "find_entities", "args": {"layers": ["MCP_BATCH"]}}]})
        check("copy: the saved drawings hold the change", ok and r["ok_files"] == 4
              and all(f["steps"][0]["result"]["total"] == 1 for f in r["files"]), str(r)[:300])

        ok, text = await call(session, "batch_run", {"folder": str(folder), "steps": change, "save": "copy",
                                                     "output_folder": str(out), "autocad_version": year})
        check("copy: existing files refused without overwrite", not ok and "overwrite" in str(text), str(text)[:200])
        ok, r = await call(session, "batch_run", {"files": [str(folder / "a.dwg")], "save": "copy", "output_folder": str(out),
                                                  "overwrite": True, "autocad_version": year,
                                                  "steps": [{"tool": "create_circle", "args": {"center": [0, 0], "radius": 3}}]})
        check("copy: overwrite replaces", ok and r["saved_files"] == 1, str(r)[:300])

        # a failed step: nothing saved (without continue_on_error)
        ok, r = await call(session, "batch_run", {"files": [str(folder / "b.dwg")], "save": "in_place", "confirm": True,
                                                  "autocad_version": year,
                                                  "steps": [change[1], {"tool": "get_table"}]})
        f = r["files"][0] if ok else {}
        check("failed step: not saved", ok and not f.get("ok") and not f.get("saved_to")
              and (folder / "b.dwg").stat().st_size == sample(year).stat().st_size and not (folder / "b.bak").exists(),
              str(r)[:300])

        # in place with .bak; a read-only file and a locked file are skipped
        ro = folder / "c.dwg"
        os.chmod(ro, 0o444)
        import win32file
        handle = win32file.CreateFile(str(folder / "sub" / "d.dwg"), win32file.GENERIC_READ, 0, None,
                                      win32file.OPEN_EXISTING, 0, None)
        try:
            ok, r = await call(session, "batch_run", {"folder": str(folder), "recursive": True, "save": "in_place",
                                                      "confirm": True, "steps": change, "autocad_version": year})
        finally:
            handle.Close()
            os.chmod(ro, 0o666)
        if ok:
            by = {Path(f["file"]).name: f for f in r["files"]}
            check("in place: a.dwg saved with .bak", by["a.dwg"].get("saved_to") and by["a.dwg"].get("backup")
                  and (folder / "a.bak").exists() and header(folder / "a.dwg") == version, json.dumps(by["a.dwg"])[:300])
            check("in place: read-only file skipped", "read-only" in str(by["c.dwg"].get("skipped")), json.dumps(by["c.dwg"])[:200])
            check("in place: file in use skipped", "in use" in str(by["d.dwg"].get("skipped")), json.dumps(by["d.dwg"])[:200])
            check("in place: no .dwl left", not list(folder.rglob("*.dwl*")), str(list(folder.rglob("*.dwl*"))))
        else:
            check("in place batch ran", False, str(r)[:300])
    finally:
        shutil.rmtree(out, ignore_errors=True)


async def plots(session, year: str) -> None:
    """plot_to_pdf in a batch: path is a folder, one PDF per drawing (subfolders kept) or per layout."""
    print(f"== printing to PDF (AutoCAD {year})")
    folder = prepare(year)
    (folder / "broken.dwg").unlink()
    pdfs = WORK.parent / "acadmcp_batch_mcp_pdf"
    shutil.rmtree(pdfs, ignore_errors=True)
    try:
        ok, r = await call(session, "batch_run", {"folder": str(folder), "recursive": True, "autocad_version": year,
                                                  "steps": [{"tool": "plot_to_pdf", "args": {"path": str(pdfs), "layouts": "*"}}]})
        names = sorted(str(f.relative_to(pdfs)) for f in pdfs.rglob("*.pdf"))
        check("plot: one PDF per drawing, subfolder kept", ok and r["ok_files"] == 4 and r["save"] == "none (read-only)"
              and names == ["a.pdf", "b.pdf", "c.pdf", "sub\\d.pdf"], f"{names} {str(r)[:300]}")
        check("plot: PDFs are not empty", all(f.stat().st_size > 1000 for f in pdfs.rglob("*.pdf")), str(names))
        check("plot: drawings untouched", not list(folder.rglob("*.bak")) and not list(folder.rglob("*.dwl*")), "")

        ok, text = await call(session, "batch_run", {"folder": str(folder), "autocad_version": year,
                                                     "steps": [{"tool": "plot_to_pdf", "args": {"path": str(pdfs)}}]})
        check("plot: existing PDFs refused without overwrite", not ok and "overwrite" in str(text), str(text)[:200])
        ok, r = await call(session, "batch_run", {"files": [str(folder / "a.dwg")], "autocad_version": year,
                                                  "steps": [{"tool": "plot_to_pdf", "args": {"path": str(pdfs), "overwrite": True}}]})
        check("plot: overwrite replaces", ok and r["ok_files"] == 1, str(r)[:300])

        ok, r = await call(session, "batch_run", {"files": [str(folder / "b.dwg")], "autocad_version": year,
                                                  "steps": [{"tool": "plot_to_pdf", "args": {"path": str(pdfs / "sep"),
                                                            "layouts": "*", "separate_files": True}}]})
        sep = sorted(f.name for f in (pdfs / "sep").glob("*.pdf")) if (pdfs / "sep").exists() else []
        check("plot: separate_files -> a PDF per layout", ok and r["ok_files"] == 1 and sep == ["b-Layout1.pdf", "b-Layout2.pdf"],
              f"{sep} {str(r)[:300]}")

        ok, text = await call(session, "batch_run", {"folder": str(folder), "autocad_version": year,
                                                     "steps": [{"tool": "plot_to_pdf", "args": {"path": str(pdfs / "x.pdf")}}]})
        check("plot: a .pdf path is refused (folder expected)", not ok and "FOLDER" in str(text), str(text)[:200])
    finally:
        shutil.rmtree(pdfs, ignore_errors=True)


async def refusals(session) -> None:
    print("== refusals (no console starts)")
    folder = WORK if WORK.exists() else Path(tempfile.gettempdir())
    cases = [
        ("change refused", {"folder": str(folder), "steps": [{"tool": "create_line", "args": {"start": [0, 0], "end": [1, 1]}}]},
         "where to save"),
        ("purge without dry_run refused", {"folder": str(folder), "steps": [{"tool": "purge", "args": {"dry_run": False}}]},
         "where to save"),
        ("audit fix refused", {"folder": str(folder), "steps": [{"tool": "audit", "args": {"fix": True}}]}, "where to save"),
        ("in_place needs confirm", {"folder": str(folder), "save": "in_place",
                                    "steps": [{"tool": "create_layer", "args": {"name": "X"}}]}, "confirm: true"),
        ("copy needs output_folder", {"folder": str(folder), "save": "copy",
                                      "steps": [{"tool": "create_layer", "args": {"name": "X"}}]}, "output_folder"),
        ("copy into the drawings' own folder", {"folder": str(folder), "save": "copy", "output_folder": str(folder),
                                                "steps": [{"tool": "create_layer", "args": {"name": "X"}}]}, "own folder"),
        ("bad save value", {"folder": str(folder), "save": "somewhere",
                            "steps": [{"tool": "create_layer", "args": {"name": "X"}}]}, "must be"),
        ("zoom refused", {"folder": str(folder), "steps": [{"tool": "zoom"}]}, "cannot run in a batch"),
        ("run_command refused", {"folder": str(folder), "steps": [{"tool": "run_command", "args": {"command": "REGEN"}}]},
         "cannot run in a batch"),
        ("unknown tool", {"folder": str(folder), "steps": [{"tool": "no_such"}]}, "no such tool"),
        ("no steps", {"folder": str(folder), "steps": []}, "non-empty"),
        ("no files given", {"steps": [{"tool": "list_layers"}]}, "folder"),
        ("missing folder", {"folder": r"C:\no\such\folder", "steps": [{"tool": "list_layers"}]}, "does not exist"),
        ("no DWG found", {"folder": str(folder), "pattern": "*.nothing.dwg", "steps": [{"tool": "list_layers"}]}, "no DWG"),
        ("bad AutoCAD version", {"folder": str(folder), "autocad_version": "2019", "steps": [{"tool": "list_layers"}]},
         "2025, 2026 or 2027"),
    ]
    for name, args, expect in cases:
        ok, text = await call(session, "batch_run", args)
        check(name, not ok and expect in str(text), str(text)[:200])
    ok, text = await call(session, "batch_status", {"job_id": "nope"})
    check("unknown job_id", not ok and "no batch job" in str(text), str(text)[:200])


async def main_async(years: list[str]) -> None:
    from mcp.client.session import ClientSession
    from mcp.client.stdio import StdioServerParameters, stdio_client

    params = StdioServerParameters(command="dotnet", args=[str(SERVER)], env=dict(os.environ))
    async with stdio_client(params) as (read, write):
        async with ClientSession(read, write) as session:
            await session.initialize()
            tools = {t.name for t in (await session.list_tools()).tools}
            check("batch_run and batch_status listed", {"batch_run", "batch_status"} <= tools, str(sorted(tools))[:200])
            logs_before = log_paths()
            for year in years:
                if not sample(year).exists():
                    print(f"== AutoCAD {year}: SKIPPED (not installed)")
                    continue
                await run_year(session, year)
                await writes(session, year)
                await plots(session, year)
            if WORK.exists():
                await open_in_autocad(session, next(y for y in reversed(years) if sample(y).exists()))
            await refusals(session)
            after = log_paths()
            check("users' LogFilePath unchanged (consoles run /isolate)", after == logs_before,
                  str({k: (logs_before.get(k), v) for k, v in after.items() if logs_before.get(k) != v})[:300])


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--years", nargs="*", default=["2026", "2027"])
    args = ap.parse_args()
    try:
        asyncio.run(main_async(args.years))
    finally:
        shutil.rmtree(WORK, ignore_errors=True)
    print(f"\n{len(passed)}/{len(passed) + len(failed)} passed")
    return 0 if not failed else 1


if __name__ == "__main__":
    sys.exit(main())
