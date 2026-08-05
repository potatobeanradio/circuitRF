"""Shapes and pins — what a generator returns. Schema §4.3/§4.4.

Every coordinate goes into the binary payload and none into the JSON, so a fractional coordinate is
structurally unrepresentable: :func:`circuitrf_pcell.units.coord` refuses one by name on the way in.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Sequence

from .units import coord


@dataclass(frozen=True)
class Layer:
    """A drawing layer — the ``(layer, datatype)`` pair circuitRF keys geometry on."""

    layer: int
    datatype: int = 0

    def to_json(self) -> dict[str, int]:
        return {"layer": self.layer, "datatype": self.datatype}

    @staticmethod
    def from_json(raw: Any) -> "Layer | None":
        if not raw:
            return None
        return Layer(int(raw["layer"]), int(raw.get("datatype", 0)))


@dataclass(frozen=True)
class Pin:
    """A connection point.

    **Width and outward direction are not optional and not decoration.** A microstrip connection is
    an EDGE, not a point, and a bend needs to know which way its arm faces — a pin without them
    cannot be abutted, and every cell written without them would need revisiting. They are required
    arguments for exactly that reason.

    ``outward_deg`` is a continuous angle in degrees (0 = +X, 90 = +Y), not a four-way enum: layout
    supports any-angle geometry, so a bend's pin can legitimately face 37.5 degrees.
    """

    name: str
    x: int
    y: int
    layer: Layer
    width: int
    outward_deg: float


class Shape:
    """Base for everything a generator can emit. Not instantiated directly."""

    kind: str = ""

    def __init__(self, layer: Layer, net: str | None = None):
        self.layer = layer
        self.net = net

    def _emit(self, payload: list[int]) -> dict[str, Any]:  # pragma: no cover - overridden
        raise NotImplementedError

    def to_json(self, payload: list[int]) -> dict[str, Any]:
        body = {"kind": self.kind, "layer": self.layer.to_json()}
        if self.net is not None:
            body["net"] = self.net
        body.update(self._emit(payload))
        return body


def _span(payload: list[int], values: Sequence[float | int]) -> dict[str, int]:
    """Append coordinates to the payload and return the span addressing them."""
    at = len(payload)
    payload.extend(coord(v) for v in values)
    return {"at": at, "count": len(payload) - at}


def _ring(payload: list[int], xy: Sequence[float | int]) -> dict[str, int]:
    if len(xy) < 4 or len(xy) % 2 != 0:
        raise ValueError(
            f"A vertex run needs an even number of coordinates and at least two points; got {len(xy)}."
        )
    return _span(payload, xy)


@dataclass
class Edge:
    """One edge of an edge list, describing the edge LEAVING vertex *i*.

    A curve crosses as a curve: an arc carries the signed ``tan(sweep/4)`` bulge circuitRF stores,
    and a cubic carries its two control points. **Do not flatten your own curves** — that bakes a
    tolerance into the geometry, and flattening is a rendering decision made at screen resolution.
    """

    kind: str = "line"
    bulge: float = 0.0
    control: tuple[int, int, int, int] | None = None

    @staticmethod
    def line() -> "Edge":
        return Edge("line")

    @staticmethod
    def arc(bulge: float) -> "Edge":
        return Edge("arc", bulge=bulge)

    @staticmethod
    def cubic(c1x: int, c1y: int, c2x: int, c2y: int) -> "Edge":
        return Edge("cubic", control=(c1x, c1y, c2x, c2y))

    def to_json(self, payload: list[int]) -> dict[str, Any]:
        if self.kind == "arc":
            return {"kind": "arc", "bulge": float(self.bulge)}
        if self.kind == "cubic":
            if self.control is None:
                raise ValueError("A cubic edge needs its two control points.")
            return {"kind": "cubic", "control": _span(payload, self.control)}
        return {"kind": "line"}


def _edges_json(edges: Sequence[Edge] | None, payload: list[int]) -> list[dict[str, Any]] | None:
    return [e.to_json(payload) for e in edges] if edges else None


class Rect(Shape):
    kind = "rect"

    def __init__(self, layer: Layer, x1: int, y1: int, x2: int, y2: int, net: str | None = None):
        super().__init__(layer, net)
        self.x1, self.y1, self.x2, self.y2 = x1, y1, x2, y2

    def _emit(self, payload: list[int]) -> dict[str, Any]:
        return {"xy": _span(payload, (self.x1, self.y1, self.x2, self.y2))}


class RoundedRect(Shape):
    kind = "rrect"

    def __init__(self, layer: Layer, x1: int, y1: int, x2: int, y2: int,
                 corner_radius: int, flatten_tol: int | None = None, net: str | None = None):
        super().__init__(layer, net)
        self.x1, self.y1, self.x2, self.y2 = x1, y1, x2, y2
        self.corner_radius = corner_radius
        self.flatten_tol = flatten_tol

    def _emit(self, payload: list[int]) -> dict[str, Any]:
        body: dict[str, Any] = {
            "xy": _span(payload, (self.x1, self.y1, self.x2, self.y2)),
            "cornerRadius": coord(self.corner_radius),
        }
        if self.flatten_tol is not None:
            body["flattenTol"] = coord(self.flatten_tol)
        return body


class Circle(Shape):
    kind = "circle"

    def __init__(self, layer: Layer, cx: int, cy: int, radius: int,
                 flatten_tol: int | None = None, net: str | None = None):
        super().__init__(layer, net)
        self.cx, self.cy, self.radius = cx, cy, radius
        self.flatten_tol = flatten_tol

    def _emit(self, payload: list[int]) -> dict[str, Any]:
        body: dict[str, Any] = {
            "xy": _span(payload, (self.cx, self.cy)),
            "radius": coord(self.radius),
        }
        if self.flatten_tol is not None:
            body["flattenTol"] = coord(self.flatten_tol)
        return body


class Polygon(Shape):
    """A filled region with straight edges, implicitly closed.

    ``holes`` are inner rings. Each must lie inside the outer ring and intersect neither it nor
    another hole — circuitRF validates this on the way in rather than trusting it.
    """

    kind = "poly"

    def __init__(self, layer: Layer, xy: Sequence[int],
                 holes: Sequence[Sequence[int]] | None = None, net: str | None = None):
        super().__init__(layer, net)
        self.xy = xy
        self.holes = holes

    def _emit(self, payload: list[int]) -> dict[str, Any]:
        body: dict[str, Any] = {"xy": _ring(payload, self.xy)}
        if self.holes:
            body["holes"] = [_ring(payload, h) for h in self.holes]
        return body


class Curve(Shape):
    """A filled region whose edges may be lines, arcs or cubics."""

    kind = "curve"

    def __init__(self, layer: Layer, xy: Sequence[int], edges: Sequence[Edge] | None = None,
                 holes: Sequence[Sequence[int]] | None = None,
                 flatten_tol: int | None = None, net: str | None = None):
        super().__init__(layer, net)
        self.xy, self.edges, self.holes = xy, edges, holes
        self.flatten_tol = flatten_tol

    def _emit(self, payload: list[int]) -> dict[str, Any]:
        body: dict[str, Any] = {"xy": _ring(payload, self.xy)}
        if self.holes:
            body["holes"] = [_ring(payload, h) for h in self.holes]
        edges = _edges_json(self.edges, payload)
        if edges:
            body["edges"] = edges
        if self.flatten_tol is not None:
            body["flattenTol"] = coord(self.flatten_tol)
        return body


class Path(Shape):
    """An open centreline with a width — a trace.

    ``end`` is one of ``flush`` / ``round`` / ``square`` / ``extended``.
    """

    kind = "path"

    def __init__(self, layer: Layer, xy: Sequence[int], width: int, end: str = "flush",
                 edges: Sequence[Edge] | None = None, flatten_tol: int | None = None,
                 net: str | None = None):
        super().__init__(layer, net)
        self.xy, self.width, self.end, self.edges = xy, width, end, edges
        self.flatten_tol = flatten_tol

    def _emit(self, payload: list[int]) -> dict[str, Any]:
        body: dict[str, Any] = {
            "xy": _ring(payload, self.xy),
            "width": coord(self.width),
            "end": self.end,
        }
        edges = _edges_json(self.edges, payload)
        if edges:
            body["edges"] = edges
        if self.flatten_tol is not None:
            body["flattenTol"] = coord(self.flatten_tol)
        return body


class Via(Shape):
    """A pad and a drilled barrel at one coordinate.

    ``layer`` is the BARREL's own via layer; ``landing_layer`` is the pad's copper layer. Getting
    these the wrong way round produces artwork that looks plausible and puts copper where the hole
    should be.
    """

    kind = "via"

    def __init__(self, layer: Layer, x: int, y: int, pad_size: int, drill_size: int,
                 landing_layer: Layer | None = None, net: str | None = None):
        super().__init__(layer, net)
        self.x, self.y = x, y
        self.pad_size, self.drill_size = pad_size, drill_size
        self.landing_layer = landing_layer

    def _emit(self, payload: list[int]) -> dict[str, Any]:
        body: dict[str, Any] = {
            "xy": _span(payload, (self.x, self.y)),
            "padSize": coord(self.pad_size),
            "drillSize": coord(self.drill_size),
        }
        if self.landing_layer is not None:
            body["landingLayer"] = self.landing_layer.to_json()
        return body


class Label(Shape):
    """Text. ``rotation`` is one of ``r0`` / ``r90`` / ``r180`` / ``r270``."""

    kind = "label"

    def __init__(self, layer: Layer, x: int, y: int, text: str, height: int,
                 rotation: str = "r0", is_port: bool = False, net: str | None = None):
        super().__init__(layer, net)
        self.x, self.y, self.text, self.height = x, y, text, height
        self.rotation, self.is_port = rotation, is_port

    def _emit(self, payload: list[int]) -> dict[str, Any]:
        return {
            "xy": _span(payload, (self.x, self.y)),
            "text": self.text,
            "height": coord(self.height),
            "rotation": self.rotation,
            "isPort": self.is_port,
        }


@dataclass
class Result:
    """What a generator returns: geometry, pins, and anything it wants to say about them.

    ``diagnostics`` is **not an error channel** — it is for a generator that DID produce geometry
    and has a caveat about it. To refuse outright, raise; the host reports that naming the cell.
    """

    shapes: list[Shape] = field(default_factory=list)
    pins: list[Pin] = field(default_factory=list)
    diagnostics: list[str] = field(default_factory=list)
