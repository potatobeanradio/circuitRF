#!/usr/bin/env python3
"""A reference PCell generator: a straight microstrip line, plus a via array.

**MLIN here is written from the CONTRACT, not transcribed from circuitRF's own MlinPCell.cs**, and
that is the whole reason it is worth having. The gate it feeds compares this against the built-in
and asserts byte-identical geometry — a comparison that proves something only because the two were
arrived at independently. A port of the C# would just be one implementation agreeing with itself,
which is the same rule ``tools/DeviceWorkerExample`` already follows for the device path.

The contract MLIN is written against (pcell-contract.md R3/R4, §3 of the microstrip brief):

* Pin 1 sits at the cell origin and the line runs along +X.
* The line is ``W`` wide, centred on the X axis, and ``L`` long.
* Pin 1 faces outward at 180 degrees, pin 2 at 0.

VIAARRAY exists to exercise the parts of the vocabulary MLIN does not reach — a count parameter, a
flag, a text parameter, vias, and the diagnostics channel.
"""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from circuitrf_pcell import (  # noqa: E402
    Layer,
    Parameter,
    Pin,
    Rect,
    Result,
    Via,
    dbu,
    generator,
    run,
)

#: Used only when no technology resolves — a layout with none still generates geometry
#: (pcell-contract.md §2); only the electrical stamp refuses without one.
FALLBACK_LAYER = Layer(1, 0)


@generator("MLIN", [Parameter.length("W"), Parameter.length("L")])
def mlin(params, tech):
    w = params.length("W")
    length = params.length("L")
    layer = tech.signal_layer or FALLBACK_LAYER

    # Integer halving, matching how a database-unit width straddles the axis: the C# side computes
    # w / 2 in integer arithmetic too, so an odd width lands the same way on both.
    half = w // 2

    return Result(
        shapes=[Rect(layer, 0, -half, length, half)],
        pins=[
            Pin("1", 0, 0, layer, w, 180.0),
            Pin("2", length, 0, layer, w, 0.0),
        ],
    )


@generator(
    "VIAARRAY",
    [
        Parameter.integer("Rows"),
        Parameter.integer("Cols"),
        Parameter.length("Pitch"),
        Parameter.length("Pad"),
        Parameter.length("Drill"),
        Parameter.flag("Staggered"),
        Parameter.text("Note"),
    ],
)
def via_array(params, tech):
    rows = params.integer("Rows", 1)
    cols = params.integer("Cols", 1)
    pitch = params.length("Pitch", 100_000)
    pad = params.length("Pad", 50_000)
    drill = params.length("Drill", 25_000)
    staggered = params.flag("Staggered")
    note = params.text("Note")

    if rows < 1 or cols < 1:
        # Raising is how a generator REFUSES; the host reports it naming the cell. Returning an
        # empty result would be a silently empty cell, which looks like it worked.
        raise ValueError(f"a via array needs at least one row and column; got {rows}x{cols}")

    barrel = tech.layer_named("Drill") or Layer(7, 0)
    landing = tech.signal_layer or FALLBACK_LAYER

    shapes = []
    for r in range(rows):
        for c in range(cols):
            # dbu(), never round() — Python's round() is banker's and disagrees with circuitRF at
            # exactly the midpoints where two shapes decide whether they abut.
            x = c * pitch + (dbu(pitch / 2) if staggered and r % 2 else 0)
            shapes.append(Via(barrel, x, r * pitch, pad, drill, landing_layer=landing))

    diagnostics = []
    if pad <= drill:
        diagnostics.append(
            f"pad ({pad}) is not larger than drill ({drill}) — the annular ring is zero or negative"
        )
    if note:
        diagnostics.append(note)

    return Result(shapes=shapes, pins=[], diagnostics=diagnostics)


if __name__ == "__main__":
    run()
