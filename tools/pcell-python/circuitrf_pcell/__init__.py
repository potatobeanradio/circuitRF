"""circuitRF PCell generators, in Python.

A generated cell is a function of its parameters and its technology, and nothing else. Write one,
declare its parameters, and call :func:`run`::

    from circuitrf_pcell import Parameter, Pin, Rect, Result, generator, run

    @generator("MLIN", [Parameter.length("W"), Parameter.length("L")])
    def mlin(params, tech):
        w = params.length("W")          # already in database units
        l = params.length("L")
        layer = tech.signal_layer
        return Result(
            shapes=[Rect(layer, 0, -w // 2, l, w // 2)],
            pins=[Pin("1", 0, 0, layer, w, 180.0), Pin("2", l, 0, layer, w, 0.0)],
        )

    run()

Three things are worth reading before writing a real cell:

* **There are no metres.** Every length arrives in database units, already converted by circuitRF
  with its own single rounding rule. Round your OWN arithmetic with :func:`dbu`, never with
  ``round()`` (banker's rounding) or ``int()`` (truncation).
* **A pin carries width and outward direction.** A connection is an edge, not a point. They are
  required arguments because a cell written without them cannot be abutted and would need revisiting.
* **A generator must be deterministic given its declared inputs** — no clock, no ambient state, no
  randomness, no set-iteration order. Two users on different machines must get identical geometry;
  when they do not, what they see is a design that changed by itself.

See ``docs/design/pcell-wire-schema.md`` and ``docs/design/pcell-contract.md``.
"""

from .geometry import (
    Circle,
    Curve,
    Edge,
    Label,
    Layer,
    Path,
    Pin,
    Polygon,
    Rect,
    Result,
    RoundedRect,
    Shape,
    Via,
)
from .host import (
    ANGLE,
    CONTRACT_VERSION,
    LENGTH,
    NONE,
    WIRE_VERSION,
    Parameter,
    Registry,
    default_registry,
    generator,
    run,
    serve_one,
)
from .services import ClippedPolygon, HostRefused, HostUnavailable, clip, offset
from .tech import LayerInfo, Stackup, StackupLayer, Technology
from .units import coord, dbu
from .values import Parameters
from .wire import WireError, read_frame, write_frame

__all__ = [
    "clip",
    "offset",
    "ClippedPolygon",
    "HostUnavailable",
    "HostRefused",
    "ANGLE",
    "CONTRACT_VERSION",
    "Circle",
    "Curve",
    "Edge",
    "LENGTH",
    "Label",
    "Layer",
    "LayerInfo",
    "NONE",
    "Parameter",
    "Parameters",
    "Path",
    "Pin",
    "Polygon",
    "Rect",
    "Registry",
    "Result",
    "RoundedRect",
    "Shape",
    "Stackup",
    "StackupLayer",
    "Technology",
    "Via",
    "WIRE_VERSION",
    "WireError",
    "coord",
    "dbu",
    "default_registry",
    "generator",
    "read_frame",
    "run",
    "serve_one",
    "write_frame",
]
