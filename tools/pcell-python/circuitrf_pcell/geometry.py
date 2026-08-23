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


LINEAR = "linear"
ANGULAR = "angular"

#: How eagerly circuitRF should redraw this cell's artwork while a grip is dragged.
#:
#: ``AUTO`` (the default) lets circuitRF time your generator once and decide. ``DEFERRED`` says
#: "don't bother trying" — the pre-drag artwork stays put, the grip and the numeric readout follow
#: the cursor, and the cell is regenerated once when the drag ends.
#:
#: **Declare DEFERRED when you already know your cell is expensive** — hundreds of shapes, or many
#: boolean round trips per generate. AUTO reaches the same conclusion, but only after spending one
#: full regeneration to find out. Being wrong costs the user a live preview they could have had,
#: never a wrong answer: the committed value is identical either way.
AUTO = "auto"
DEFERRED = "deferred"

# A Handle's optional `quantity` reuses the SAME vocabulary a parameter's own dimension already uses
# (circuitrf_pcell.LENGTH / ANGLE, defined in host.py — imported from there rather than redefined
# here, so the two can never drift into meaning different strings).


@dataclass(frozen=True)
class CrossAxis:
    """The parameter a :class:`Handle` drives when dragged ACROSS its own axis.

    Use it when one point on your cell genuinely means two things at once — the far end of a taper is
    "how long" along its axis and "how far off centre" across it::

        Handle("L", anchor=(0, 0), at=(l, offset), axis=0, cross=CrossAxis("Offset"))

    Travel along the axis and travel across it are independent scalars, so circuitRF splits the drag
    between them without guessing. Both commit together as one edit.
    """

    parameter: str
    label: str | None = None
    min: float | None = None
    max: float | None = None
    #: ``circuitrf_pcell.LENGTH``, ``ANGLE``, or None. See :attr:`Handle.quantity`.
    quantity: str | None = None


@dataclass(frozen=True)
class Handle:
    """One draggable grip on this cell's artwork — the user drags it, a parameter follows.

    Declaring one is optional and costs two facts you can already see in your own drawing code::

        Handle("L", anchor=(0, 0), at=(l, 0), axis=0)              # the far end IS the length
        Handle("W", anchor=(l // 2, 0), at=(l // 2, w // 2), axis=90)   # top edge of a centred trace

    ``anchor`` is the fixed point the grip measures FROM, ``at`` is where the grip is right now for
    these parameter values, and ``axis`` is the direction it travels in degrees (0 = +X, 90 = +Y).

    **You never state how much the parameter changes per unit of travel.** circuitRF measures that by
    regenerating your cell with the parameter perturbed and seeing where you put the grip. That is
    why nothing here mentions a unit or a scale factor, why the same declaration works whether the
    parameter is a length or a turn count, and why a cell whose geometry is *not* linear in its
    parameter needs no special treatment.

    Two rules worth knowing before you declare one:

    * **The grip must actually move when the parameter changes**, in the direction ``axis`` names.
      That is the whole declaration; if it does not, circuitRF reports the handle as undraggable
      rather than guessing.
    * **Several handles may name one parameter.** A centred width declares a grip on each edge; both
      drive the same value and both move when either is dragged. Nothing special is needed.

    ``min``/``max`` are optional bounds in the parameter's own units (on this side, that means
    database units for a length, like everything else). They are a convenience — a generator that
    clamps internally needs neither, because circuitRF redraws the grip wherever your cell actually
    put it.
    """

    parameter: str
    anchor: tuple[int, int]
    at: tuple[int, int]
    axis: float = 0.0
    kind: str = LINEAR
    label: str | None = None
    min: float | None = None
    max: float | None = None
    #: An optional second parameter, driven by dragging across ``axis``. See :class:`CrossAxis`.
    cross: "CrossAxis | None" = None
    #: Hold this grip's ``anchor`` still on screen while the grip is dragged.
    #:
    #: Your cell cannot move its own origin — pin 1 is always at (0, 0) — so without this, dragging
    #: the LEFT edge of a trace grows it to the right. circuitRF instead moves the whole placed cell
    #: so the anchor keeps the position it had, which is what "drag this end, keep the other end
    #: still" means. Declare the OPPOSITE edge as ``anchor`` and set this, and both hold.
    #:
    #: A no-op when the anchor does not move for that parameter, so it is safe to set on every grip
    #: of a set rather than only the ones that need it.
    keep_anchor_fixed: bool = False
    #: ``circuitrf_pcell.LENGTH``, ``ANGLE``, or None (the default).
    #:
    #: Say what the parameter IS, if you can, so circuitRF can print a readout a person can read
    #: ("W = 12 mil", not "W = 12000") and can round a length onto the layout's own snap grid — a user
    #: who set 1 mil snapping expects the committed width to land on a whole mil. Saying nothing is
    #: not a defect: the readout falls back to the raw number and the grid is not applied, which is
    #: how every grip behaved before this existed.
    quantity: str | None = None

    def to_json(self, payload: list[int]) -> dict[str, Any]:
        # The four coordinates ride in the binary payload like every other coordinate (schema §2), so
        # there is nowhere a fractional one could be written. min/max are parameter VALUES, not
        # coordinates, so they stay in the JSON — a fractional bound is legitimate.
        at_index = len(payload)
        payload.extend(
            [coord(self.anchor[0]), coord(self.anchor[1]), coord(self.at[0]), coord(self.at[1])]
        )
        body: dict[str, Any] = {
            "parameter": self.parameter,
            "kind": self.kind,
            "span": {"at": at_index, "count": 4},
            "axisDeg": float(self.axis),
        }
        if self.keep_anchor_fixed:
            body["keepAnchorFixed"] = True
        if self.quantity is not None:
            body["quantity"] = self.quantity
        if self.label is not None:
            body["label"] = self.label
        if self.min is not None:
            body["min"] = self.min
        if self.max is not None:
            body["max"] = self.max
        if self.cross is not None:
            body["crossParameter"] = self.cross.parameter
            if self.cross.label is not None:
                body["crossLabel"] = self.cross.label
            if self.cross.min is not None:
                body["crossMin"] = self.cross.min
            if self.cross.max is not None:
                body["crossMax"] = self.cross.max
            if self.cross.quantity is not None:
                body["crossQuantity"] = self.cross.quantity
        return body


@dataclass
class Result:
    """What a generator returns: geometry, pins, and anything it wants to say about them.

    ``diagnostics`` is **not an error channel** — it is for a generator that DID produce geometry
    and has a caveat about it. To refuse outright, raise; the host reports that naming the cell.

    ``handles`` is optional. Declaring none — which is what every generator written before handles
    existed does — simply means the cell is edited through its parameter list, exactly as before.
    """

    shapes: list[Shape] = field(default_factory=list)
    pins: list[Pin] = field(default_factory=list)
    diagnostics: list[str] = field(default_factory=list)
    handles: list[Handle] = field(default_factory=list)
    #: ``AUTO`` or ``DEFERRED`` — see the module constants. Only meaningful alongside ``handles``.
    preview: str = AUTO

    #: The parameters this run treated as OUTPUTS, mapped to what it derived them to — a capacitance
    #: a cell works out from its own w and l, a resistance from its own geometry. A value of ``None``
    #: names the parameter as an output WITHOUT stating a value, which is the honest answer when
    #: nothing in the cell computes it.
    #:
    #: **A report here is the only way such a value can be current.** An output is not read, so
    #: whatever the design has stored for it is whatever it was stored with; with no report the host
    #: can only show that stale number, and it will keep showing it while the geometry that
    #: determines it changes underneath. Naming the parameter at least stops circuitRF offering an
    #: edit box that cannot do anything; naming it AND its value makes the list track the artwork.
    computed: dict[str, Any] = field(default_factory=dict)
