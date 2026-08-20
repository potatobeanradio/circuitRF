#!/usr/bin/env python3
"""A second reference PCell generator: a rectangular spiral inductor.

Where ``mlin.py`` is the smallest cell that is still a real one, this is the smallest cell that is
still *interesting*: the artwork is a function of four numbers that interact, so it is the example
that shows what a parameterised cell buys you over a stored one. Sweep ``Turns`` and the geometry is
rebuilt; sweep ``Width`` and every segment moves.

**It is written from the contract, exactly as MLIN is** (``pcell-contract.md`` R3/R4):

* Pin 1 sits at the cell origin, and the cell's principal axis runs along +X.
* Pin 1 is the outer terminal, entering from the left at 180 degrees.
* Pin 2 is the inner terminal, and it is left facing +Y — an inner terminal has to be reached by an
  air bridge or an underpass on another layer, which is the caller's business and not this cell's.

**The geometry is deliberately a chain of rectangles rather than one polygon.** A spiral drawn as a
single closed outline needs a mitre rule at every corner, and getting that rule wrong is the classic
way a spiral's inductance comes out plausible and wrong. Overlapping rectangles on one layer union
exactly (integer coordinates, ``layout-view.md`` §1.1) and every corner is square by construction.

Run it exactly like ``mlin.py``:  it is a generator script, so circuitRF drives it over a pipe.
Point a ``pcell-generators.json`` at whichever of the two you want, or at a module importing both.
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
    dbu,
    generator,
    run,
)

#: Used only when no technology resolves — a layout with none still generates geometry.
FALLBACK_LAYER = Layer(1, 0)


@generator(
    "SPIRAL",
    [
        Parameter.length("Width"),    # conductor width
        Parameter.length("Space"),    # gap between adjacent turns
        Parameter.length("Inner"),    # inner opening, across the flats
        Parameter.integer("Turns"),
    ],
)
def spiral(params, tech):
    w = params.length("Width", 10_000)      # 10 um
    s = params.length("Space", 10_000)
    inner = params.length("Inner", 100_000)
    turns = params.integer("Turns", 3)

    if turns < 1:
        # Raising is how a generator REFUSES. Returning an empty Result would be a silently empty
        # cell, which looks like it worked.
        raise ValueError(f"a spiral needs at least one turn; got {turns}")
    if w <= 0 or s < 0:
        raise ValueError(f"width must be positive and space non-negative; got W={w}, S={s}")

    layer = tech.signal_layer or FALLBACK_LAYER

    pitch = w + s                 # centre-to-centre spacing of adjacent turns
    half = dbu(w / 2)             # dbu(), never round(): round() is banker's in Python
    half_inner = dbu(inner / 2)

    shapes = []

    # Walk the spiral inward, one turn at a time, as four segments per turn. `r` is the distance
    # from the centre to the CENTRE-LINE of the current turn's conductor.
    for t in range(turns):
        r = half_inner + half + (turns - 1 - t) * pitch
        r_next = r - pitch        # the next turn inward, where this turn's last segment lands

        # Top edge, running left to right.
        shapes.append(Rect(layer, -r - half, r - half, r + half, r + half))
        # Right edge, running down.
        shapes.append(Rect(layer, r - half, -r - half, r + half, r + half))
        # Bottom edge, running right to left.
        shapes.append(Rect(layer, -r - half, -r - half, r + half, -r + half))
        # Left edge, running up — stopped one pitch short so it meets the next turn inward, or,
        # on the innermost turn, so it reaches the inner terminal.
        top = r_next + half if t < turns - 1 else half
        shapes.append(Rect(layer, -r - half, -r + half, -r + half, top))

        # The step inward that joins this turn's left edge to the next turn's top edge.
        if t < turns - 1:
            shapes.append(Rect(layer, -r - half, r_next - half, -r_next + half, r_next + half))

    outer = half_inner + half + (turns - 1) * pitch

    return Result(
        shapes=shapes,
        pins=[
            # Outer terminal: the left end of the outermost top edge, facing out to the left.
            Pin("1", -outer - half, outer, layer, w, 180.0),
            # Inner terminal: the top of the innermost left edge, facing up. It is reached by an
            # air bridge or an underpass, which belongs to whatever places this cell.
            Pin("2", -half_inner - half, half, layer, w, 90.0),
        ],
        diagnostics=(
            [f"{turns} turns at a {pitch} DBU pitch leaves an inner opening of {inner} DBU"]
            if inner < 2 * pitch
            else []
        ),
    )


if __name__ == "__main__":
    run()
