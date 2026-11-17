"""Builds test content in the ACTIVE AutoCAD drawing through AutoCAD's COM API.

Used by smoke_test.py only after it has checked that the active drawing is a new,
unnamed, empty one, so real drawings are never touched. COM is used on purpose: the
content must not depend on the tools under test.

Requires pywin32 (tests/requirements-dev.txt).
"""

from __future__ import annotations

import time
from typing import Any, Callable

import pythoncom
import win32com.client
from win32com.client import VARIANT

RPC_E_CALL_REJECTED = -2147418111


def retry(fn: Callable[[], Any], attempts: int = 60) -> Any:
    """AutoCAD rejects COM calls while busy; try again for a while."""
    for i in range(attempts):
        try:
            return fn()
        except pythoncom.com_error as e:
            if e.hresult != RPC_E_CALL_REJECTED or i == attempts - 1:
                raise
            time.sleep(0.25)


def pt(x: float, y: float, z: float = 0.0) -> VARIANT:
    return VARIANT(pythoncom.VT_ARRAY | pythoncom.VT_R8, (float(x), float(y), float(z)))


def arr(values: list[float]) -> VARIANT:
    return VARIANT(pythoncom.VT_ARRAY | pythoncom.VT_R8, tuple(float(v) for v in values))


def active_document():
    app = retry(lambda: win32com.client.GetActiveObject("AutoCAD.Application"))
    return retry(lambda: app.ActiveDocument)


def build_step2(doc) -> dict[str, Any]:
    """Layers, texts, circle, polyline, a block with an attribute, an entity on a locked layer.

    Returns what the read tools are expected to report.
    """
    layers = retry(lambda: doc.Layers)
    lay_a = retry(lambda: layers.Add("MCP_A"))
    retry(lambda: setattr(lay_a, "color", 1))
    lay_frozen = retry(lambda: layers.Add("MCP_FROZEN"))
    lay_locked = retry(lambda: layers.Add("MCP_LOCKED"))

    ms = retry(lambda: doc.ModelSpace)
    text = retry(lambda: ms.AddText("Hello MCP", pt(0, 100), 2.5))
    retry(lambda: setattr(text, "Layer", "MCP_A"))
    retry(lambda: ms.AddMText(pt(0, 120), 50, "First line\\PSecond line"))
    circle = retry(lambda: ms.AddCircle(pt(200, 0), 5))
    retry(lambda: setattr(circle, "Layer", "MCP_A"))
    pl = retry(lambda: ms.AddLightWeightPolyline(arr([0, 0, 10, 0, 10, 10, 0, 10])))
    retry(lambda: setattr(pl, "Closed", True))
    retry(lambda: pl.SetBulge(0, 0.5))

    blk = retry(lambda: doc.Blocks.Add(pt(0, 0), "MCP_BLK"))
    retry(lambda: blk.AddCircle(pt(0, 0), 2))
    retry(lambda: blk.AddAttribute(1.0, 0, "Name?", pt(0, 3), "NAME", "X"))
    ref = retry(lambda: ms.InsertBlock(pt(50, 50), "MCP_BLK", 1, 1, 1, 0))
    attrs = retry(lambda: ref.GetAttributes())
    retry(lambda: setattr(attrs[0], "TextString", "PUMP-1"))

    locked_line = retry(lambda: ms.AddLine(pt(300, 0), pt(310, 0)))
    retry(lambda: setattr(locked_line, "Layer", "MCP_LOCKED"))
    retry(lambda: setattr(lay_locked, "Lock", True))
    retry(lambda: setattr(lay_frozen, "Freeze", True))

    return {
        "model_entities": 6,  # text, mtext, circle, polyline, block reference, line
        "block_ref_handle": retry(lambda: ref.Handle),
        "polyline_handle": retry(lambda: pl.Handle),
        "circle_handle": retry(lambda: circle.Handle),
        "locked_line_handle": retry(lambda: locked_line.Handle),
    }


def select_all(doc) -> None:
    """Select everything with grips (the PickFirst set), as a user would."""
    retry(lambda: doc.SendCommand("_.AI_SELALL "))
    time.sleep(1.0)


def clear_selection(doc) -> None:
    retry(lambda: doc.SendCommand("_.AI_DESELECT "))
    time.sleep(0.5)
