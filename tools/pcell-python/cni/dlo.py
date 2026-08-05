"""The API a kit's device cells are written against, implemented onto circuitRF's own PCell package.

Everything a cell touches is here, because a cell says ``from cni.dlo import *``. The names and their
shapes were taken from the kit's own Apache-licensed cells — what they construct, what they call, and
what they read back.

**Two rules run through the whole module.**

*Microns in, database units out, converted once.* Every coordinate a cell computes is a floating-point
micrometre. Nothing is rounded until :func:`_emit`, which multiplies by the resolution the host stated
and rounds with circuitRF's own rule. A cell never sees a database unit and never rounds anything, so
there is exactly one rounding rule — the same argument that keeps metres off the wire.

*Constructing a shape registers it.* ``Rect(Layer('MIM'), box)`` draws; the return value is only there
so a cell can ask the shape its own extent afterwards. That is the kit's convention, not ours, and it
is why generation runs inside a context.
"""

from __future__ import annotations

import math
from abc import abstractmethod
from typing import Any, Iterable, Sequence

import circuitrf_pcell as crf

__all__ = [
    "Box", "ChoiceConstraint", "Direction", "DloGen", "Donut", "Ellipse", "Font", "GapStyle",
    "Grouping", "Layer", "Location", "Net", "Orientation", "Path", "PathStyle", "PCellWrapper",
    "Point", "PointList", "Polygon", "RangeConstraint", "Rect", "ShapeFilter", "SignalType",
    "Snap", "StdVia", "Term", "TermType", "Text", "Tech", "Transform", "Via", "ViaParam",
    "Numeric", "octagon", "ulist", "append", "cadr", "caddr", "cadddr",
    "strToOrient", "strToAlignt", "Transform",
    "UnsupportedByCircuitRF", "current_context", "abstractmethod",
]


class UnsupportedByCircuitRF(NotImplementedError):
    """A part of the API circuitRF does not implement yet.

    Raised BY NAME rather than approximated. A cell that silently drew something slightly different
    from what its author wrote would be a wrong answer that renders — the conformance harness counts
    these instead, so the gap is a number rather than a surprise.
    """


# ── geometry primitives (micrometres, floating point — the kit's own units) ───────────────────


class Point:
    __slots__ = ("x", "y")

    def __init__(self, x: float, y: float = 0.0):
        if isinstance(x, Point):
            self.x, self.y = x.x, x.y
        else:
            self.x, self.y = float(x), float(y)

    def getX(self) -> float: return self.x
    def getY(self) -> float: return self.y

    def __iter__(self): return iter((self.x, self.y))
    def __eq__(self, o): return isinstance(o, Point) and (self.x, self.y) == (o.x, o.y)
    def __hash__(self): return hash((self.x, self.y))
    def __add__(self, o): return Point(self.x + o.x, self.y + o.y)
    def __sub__(self, o): return Point(self.x - o.x, self.y - o.y)
    def __repr__(self): return f"Point({self.x}, {self.y})"


class PointList(list):
    """A list of :class:`Point`, accepting the several spellings the kit's cells use."""

    def __init__(self, points: Iterable[Any] = ()):
        out = []
        for p in points or ():
            if isinstance(p, Point):
                out.append(p)
            elif isinstance(p, (tuple, list)) and len(p) == 2:
                out.append(Point(p[0], p[1]))
            else:
                raise UnsupportedByCircuitRF(f"PointList cannot take {type(p).__name__}")
        super().__init__(out)

    def compress(self) -> "PointList":
        """Drop consecutive duplicates — what the kit's path builders call between steps.

        Only EXACT repeats are removed. Collapsing near-coincident points as well would quietly move
        geometry, and the kit is already working on its own grid."""
        out: list[Point] = []
        for p in self:
            if not out or out[-1] != p:
                out.append(p)
        return PointList(out)


class Box:
    """An axis-aligned rectangle. Constructible from four numbers or from two corners."""

    __slots__ = ("left", "bottom", "right", "top")

    def __init__(self, *args):
        if len(args) == 4:
            x1, y1, x2, y2 = (float(a) for a in args)
        elif len(args) == 2 and all(isinstance(a, Point) for a in args):
            x1, y1, x2, y2 = args[0].x, args[0].y, args[1].x, args[1].y
        elif len(args) == 1 and isinstance(args[0], Box):
            b = args[0]
            x1, y1, x2, y2 = b.left, b.bottom, b.right, b.top
        else:
            raise UnsupportedByCircuitRF(f"Box{args!r}")
        # Normalised on construction: a cell may hand the corners in either order, and every
        # consumer below assumes left <= right.
        self.left, self.right = min(x1, x2), max(x1, x2)
        self.bottom, self.top = min(y1, y2), max(y1, y2)

    # The kit reads these under several spellings.
    def getBBox(self, *_): return self
    @property
    def bbox(self): return self
    def lowerLeft(self): return Point(self.left, self.bottom)
    def upperRight(self): return Point(self.right, self.top)
    def getWidth(self): return self.right - self.left
    def getHeight(self): return self.top - self.bottom
    def getCenter(self): return Point((self.left + self.right) / 2, (self.bottom + self.top) / 2)

    def expand(self, d: float) -> "Box":
        return Box(self.left - d, self.bottom - d, self.right + d, self.top + d)

    def fix(self) -> "Box":
        """Return the box in canonical form — what the kit calls before using a box's edges.

        Canonicalising means ordering the corners, which :meth:`__init__` already does on every box,
        so this is the identity here. It is NOT a grid snap: a box has no access to the technology,
        the kit states a grid resolution of zero for this process, and rounding coordinates at an
        arbitrary point in the middle of a cell's arithmetic would move geometry the cell had already
        computed exactly."""
        return self

    def __repr__(self): return f"Box({self.left}, {self.bottom}, {self.right}, {self.top})"


class Layer:
    """A process layer, named the way the kit names it: a layer plus an optional purpose.

    Resolved against circuitRF's technology only at emit time — a cell may name a layer the current
    technology does not define, and that has to be reported as a missing layer rather than crash
    halfway through generation.
    """

    __slots__ = ("name", "purpose")

    def __init__(self, name: str, purpose: str = "drawing"):
        self.name = str(name)
        self.purpose = str(purpose) if purpose else "drawing"

    @property
    def full(self) -> str: return f"{self.name}.{self.purpose}"

    def __eq__(self, o): return isinstance(o, Layer) and (self.name, self.purpose) == (o.name, o.purpose)
    def __hash__(self): return hash((self.name, self.purpose))
    def __repr__(self): return f"Layer({self.name!r}, {self.purpose!r})"


# ── shapes: constructing one DRAWS it ────────────────────────────────────────────────────────


class _Figure:
    """Base for anything a cell draws. Registers itself with the cell being generated."""

    def __init__(self, layer: Layer):
        self.layer = layer
        self._net: str | None = None
        self._pin: Any = None
        ctx = current_context()
        if ctx is not None:
            ctx.figures.append(self)

    def getBBox(self, *_) -> Box: raise NotImplementedError
    @property
    def bbox(self) -> Box: return self.getBBox()

    def __getattr__(self, name: str):
        """Unknown attributes read as ``None`` rather than raising.

        **This exists for one measured behaviour, not as general permissiveness.** A kit ANNOTATES
        figures with its own tags — ``fig.col = True`` on the pieces of a collector — and then reads
        that tag back across EVERY figure it has drawn (``[g.add(f) for f in self.getShapes() if
        f.col]``). The figures it never tagged have no such attribute, so a strict model raises on a
        line the kit considers ordinary.

        Only reached when normal lookup has already failed, so every real method and field is
        unaffected. The cost is stated rather than hidden: a genuine typo now reads as ``None``
        instead of raising at the point of the mistake. That is the trade this layer exists to make —
        it hosts somebody else's code, and code that ran elsewhere must not fail here on a convention
        circuitRF simply did not share.
        """
        if name.startswith("__"):
            # Never answer a dunder lookup: claiming __iter__/__len__/__deepcopy__ would advertise
            # capabilities this object does not have, and those failures land far from the cause.
            raise AttributeError(name)
        return _UnsetTag(type(self).__name__, name)

    def getNet(self): return self._net
    def setNet(self, net): self._net = net
    def getPin(self): return self._pin
    def setPin(self, pin): self._pin = pin

    def destroy(self):
        ctx = current_context()
        if ctx is not None and self in ctx.figures:
            ctx.figures.remove(self)

    def transform(self, t: "Transform", *_a, **_k) -> "_Figure":
        """Move and/or reorient this figure IN PLACE, and return it.

        In place is the kit's own convention — it draws a piece, then reorients it, and expects the
        already-registered figure to have moved. Returning ``self`` as well lets a call site chain.
        """
        raise UnsupportedByCircuitRF(f"transform on {type(self).__name__}")

    def moveBy(self, dx: float, dy: float = 0.0, *_a, **_k) -> "_Figure":
        """Translate in place. Expressed as a transform so a move and a reorientation can never
        disagree about what moving a figure means."""
        if isinstance(dx, Point):
            dx, dy = dx.x, dx.y
        return self.transform(Transform(dx, dy, "R0"))

    #: The kit spells the same operation several ways.
    def move(self, *a, **k):      return self.moveBy(*a, **k)
    def translate(self, *a, **k): return self.moveBy(*a, **k)

    # The boolean operations a kit uses on figure groups. Delegated to cni.geo, which asks circuitRF
    # to perform them rather than implementing a second clipper here. Imported inside the methods
    # because geo builds figures and so imports this module.
    def fgOr(self, other=None, *_a, **_k):
        from .geo import fgOr as _op
        return _op(self, other)

    def fgAnd(self, other=None, *_a, **_k):
        from .geo import fgAnd as _op
        return _op(self, other)

    def fgNot(self, other=None, *_a, **_k):
        from .geo import fgNot as _op
        return _op(self, other)

    def fgXor(self, other=None, *_a, **_k):
        from .geo import fgXor as _op
        return _op(self, other)

    def fgSize(self, *a, **k):
        from .geo import fgSize as _op, _size_args
        size, layer = _size_args(*a, **k)
        return _op(self, size, layer)


class Rect(_Figure):
    def __init__(self, layer: Layer, box: Box, *_):
        super().__init__(layer)
        self.box = box if isinstance(box, Box) else Box(box)

    def getBBox(self, *_): return self.box

    def clone(self) -> "Rect":
        """A second rectangle just like this one. It DRAWS, like any other construction — the kit
        clones a shape in order to have another one, not to hold an unattached copy."""
        return Rect(self.layer, Box(self.box))

    def transform(self, t: "Transform", *_a, **_k) -> "Rect":
        # Every orientation in Transform's table maps the axes onto the axes, so a rectangle stays a
        # rectangle — transforming its two opposite corners and re-normalising is exact. Box's own
        # constructor sorts them, so a corner pair that comes back swapped is already handled.
        x1, y1 = t.apply(self.box.left, self.box.bottom)
        x2, y2 = t.apply(self.box.right, self.box.top)
        self.box = Box(x1, y1, x2, y2)
        return self


class Polygon(_Figure):
    def __init__(self, layer: Layer, points: Iterable[Any], *_, holes: Iterable[Any] | None = None):
        super().__init__(layer)
        self.points = PointList(points)
        #: Inner rings. A kit never constructs one directly — a boolean does, and circuitRF's own
        #: geometry model carries holes natively (§3.1a), so the region survives as the region asked
        #: for rather than a filled approximation of it.
        self.holes: list[PointList] = [PointList(h) for h in (holes or ())]

    def getBBox(self, *_):
        xs = [p.x for p in self.points]; ys = [p.y for p in self.points]
        return Box(min(xs), min(ys), max(xs), max(ys))

    def clone(self) -> "Polygon":
        """A second polygon just like this one — and, like every construction here, it DRAWS."""
        return Polygon(self.layer, list(self.points),
                       holes=[list(h) for h in getattr(self, "holes", ())])

    def transform(self, t: "Transform", *_a, **_k) -> "Polygon":
        self.points = PointList([Point(*t.apply(p.x, p.y)) for p in self.points])
        self.holes = [PointList([Point(*t.apply(p.x, p.y)) for p in ring])
                      for ring in getattr(self, "holes", ())]
        return self


class Path(_Figure):
    """A trace: a centreline plus a width.

    **Note the argument ORDER: the width comes before the points.** The kit writes
    ``Path(layer, width, pointList, style)`` — a width is a scalar and a point list is a sequence, so
    getting this backwards fails at the point list with "'float' object is not iterable", nowhere near
    the call. Accepted in either order for exactly that reason: whichever argument is a number is the
    width, whichever is a sequence is the centreline.
    """

    def __init__(self, layer: Layer, width: Any = 0.0, points: Iterable[Any] | None = None,
                 *_a, **_k):
        super().__init__(layer)
        if points is None or isinstance(width, (list, tuple, PointList)):
            width, points = points, width       # (layer, points, width) — the other spelling
        self.points = PointList(points or ())
        self.width = float(width or 0.0)

    def getBBox(self, *_):
        xs = [p.x for p in self.points]; ys = [p.y for p in self.points]
        h = self.width / 2.0
        return Box(min(xs) - h, min(ys) - h, max(xs) + h, max(ys) + h)


class Ellipse(_Figure):
    def __init__(self, layer: Layer, box: Box, *_):
        super().__init__(layer)
        self.box = box if isinstance(box, Box) else Box(box)

    def getBBox(self, *_): return self.box


class Text(_Figure):
    """A label. Note the argument ORDER: the text comes before the position.

    Getting this the intuitive way round costs nothing visible — the position argument receives a
    string, ``Point(*text)`` unpacks its characters, and the error names an argument count that is
    really the label's length. Taken from the kit's own call sites, not assumed."""

    def __init__(self, layer: Layer, text: str, origin: Point, height: float = 1.0, *_a, **_k):
        super().__init__(layer)
        self.text = str(text)
        self.origin = origin if isinstance(origin, Point) else Point(*origin)
        self.height = float(height)
        self.alignment: Any = None
        self.orientation: Any = None

    # Carried, not acted on. circuitRF's own label has no alignment or orientation of its own, so
    # recording them keeps a cell's intent visible without pretending to honour it.
    def transform(self, t: "Transform", *_a, **_k) -> "Text":
        self.origin = Point(*t.apply(self.origin.x, self.origin.y))
        return self

    def setAlignment(self, alignment): self.alignment = alignment
    def setOrientation(self, orientation): self.orientation = orientation
    def setFont(self, font): self.font = font
    def setDrafting(self, drafting): self.drafting = drafting

    def getBBox(self, *_): return Box(self.origin.x, self.origin.y, self.origin.x, self.origin.y)


class Donut(_Figure):
    def __init__(self, *_a, **_k): raise UnsupportedByCircuitRF("Donut")


class Via(_Figure):
    def __init__(self, *_a, **_k): raise UnsupportedByCircuitRF("Via")


class StdVia(_Figure):
    def __init__(self, *_a, **_k): raise UnsupportedByCircuitRF("StdVia")


# ── enumerations and small value types ───────────────────────────────────────────────────────


class _Enum:
    """The kit reads these as bare attributes; only their identity matters to it."""
    def __init_subclass__(cls, **kw):
        super().__init_subclass__(**kw)
        for n in getattr(cls, "_members", ()):
            setattr(cls, n, f"{cls.__name__}.{n}")


class Font(_Enum):
    _members = ("ROMAN", "STICK", "SWEDISH", "MILSPEC", "EURO", "EURO_STYLE", "GOTHIC", "MATH",
                "SCRIPT", "FIXED", "HERSHEY", "SWISS")


class Direction(_Enum):
    _members = ("NORTH", "SOUTH", "EAST", "WEST", "NORTH_SOUTH", "EAST_WEST")


class Orientation(_Enum):
    _members = ("R0", "R90", "R180", "R270", "MX", "MY", "MXR90", "MYR90")


class Location(_Enum):
    _members = ("CENTER_CENTER", "CENTER_LEFT", "CENTER_RIGHT", "LOWER_CENTER",
                "LOWER_LEFT", "LOWER_RIGHT", "UPPER_CENTER", "UPPER_LEFT", "UPPER_RIGHT")


class TermType(_Enum):
    _members = ("INPUT", "OUTPUT", "INPUT_OUTPUT", "SWITCH", "JUMPER", "UNUSED", "TRISTATE")


class SignalType(_Enum):
    _members = ("SIGNAL", "POWER", "GROUND", "CLOCK", "TIEOFF", "TIEHI", "TIELO", "ANALOG")


class GapStyle(_Enum):
    _members = ("TRUNCATE", "EXTEND", "ROUND")


class PathStyle(_Enum):
    _members = ("TRUNCATE", "EXTEND", "ROUND", "VARIABLE", "OCTAGON")


class Snap(_Enum):
    _members = ("ROUND", "CEILING", "FLOOR")


class Transform:
    def __init__(self, offset: Point | None = None, orient: Any = None, mag: float = 1.0):
        self.offset = offset or Point(0, 0)
        self.orient = orient
        self.mag = float(mag)


class ShapeFilter:
    def __init__(self, *_a, **_k): pass


class Net:
    def __init__(self, name: str = "", sigType: Any = None):
        self.name, self.sigType = str(name), sigType


class Term:
    def __init__(self, name: str = "", termType: Any = None):
        self.name, self.termType = str(name), termType


class ViaParam:
    def __init__(self, *_a, **_k): raise UnsupportedByCircuitRF("ViaParam")


class Transform:
    """A translation plus an orientation, applied to a figure in place.

    The kit spells an orientation as a STRING (``strToOrient`` is the identity here), so both a
    string and an :data:`Orientation` member are accepted and normalised the same way.
    """

    #: Orientation → (a, b, c, d) of the linear part [[a, b], [c, d]], applied before the offset.
    _LINEAR = {
        "R0":    (1, 0, 0, 1),
        "R90":   (0, -1, 1, 0),
        "R180":  (-1, 0, 0, -1),
        "R270":  (0, 1, -1, 0),
        "MX":    (1, 0, 0, -1),     # mirror about the X axis
        "MY":    (-1, 0, 0, 1),     # mirror about the Y axis
        "MXR90": (0, 1, 1, 0),
        "MYR90": (0, -1, -1, 0),
    }

    def __init__(self, dx: float = 0.0, dy: float = 0.0, orient: Any = "R0", *_a, **_k):
        self.dx, self.dy = float(dx), float(dy)
        name = getattr(orient, "name", None) or str(orient)
        key = name.rsplit(".", 1)[-1].upper()
        if key not in Transform._LINEAR:
            raise UnsupportedByCircuitRF(f"orientation {orient!r}")
        self.orient = key

    def apply(self, x: float, y: float) -> tuple[float, float]:
        a, b, c, d = Transform._LINEAR[self.orient]
        return (a * x + b * y + self.dx, c * x + d * y + self.dy)


class _UnsetTag:
    """What an unread annotation reads as: FALSY, but named if anyone calls it.

    A plain ``None`` would do for the tag case (``if f.col``) and does the wrong thing for the other
    one: a kit reaching for a METHOD this layer has not implemented would get
    ``'NoneType' object is not callable`` — true, and useless. Calling this says which type and which
    name, which is the difference between a diagnosable gap and a puzzle.
    """

    __slots__ = ("_owner", "_name")

    def __init__(self, owner: str, name: str):
        self._owner, self._name = owner, name

    def __bool__(self) -> bool: return False
    def __len__(self) -> int: return 0
    def __iter__(self): return iter(())
    def __eq__(self, other): return other is None or other is False or isinstance(other, _UnsetTag)
    def __hash__(self): return hash((self._owner, self._name))
    def __repr__(self) -> str: return f"<unset {self._owner}.{self._name}>"

    def __call__(self, *_a, **_k):
        raise UnsupportedByCircuitRF(
            f"{self._owner}.{self._name}() is not implemented by circuitRF's kit compatibility layer.")


class _PinHandle:
    """What :meth:`DloGen.addPin` hands back.

    circuitRF does not need the artwork-to-terminal association the kit builds with it: a pin's
    position and extent come from the declared box, and its connecting width and outward direction
    from the drawn artwork itself (``PinInference``). So this records the name and accepts the rest —
    a doorway, not an emulation.
    """

    __slots__ = ("name", "shapes")

    def __init__(self, name: str):
        self.name = name
        self.shapes: list[Any] = []

    def addShape(self, shape: Any) -> Any:
        self.shapes.append(shape)
        return shape

    def getName(self) -> str: return self.name
    def __repr__(self) -> str: return f"<pin {self.name}>"


class Grouping:
    """A named bag of figures. The kit uses it to move or delete several shapes together."""

    def __init__(self, name: str = "", figures: Sequence[_Figure] = ()):
        self.name = str(name)
        self.figures = list(figures)

    def add(self, fig): self.figures.append(fig)

    def getBBox(self, *_):
        boxes = [f.getBBox() for f in self.figures]
        if not boxes:
            raise UnsupportedByCircuitRF("Grouping.getBBox on an empty group")
        return Box(min(b.left for b in boxes), min(b.bottom for b in boxes),
                   max(b.right for b in boxes), max(b.top for b in boxes))

    def destroy(self):
        for f in list(self.figures): f.destroy()
        self.figures.clear()

    def ungroup(self): self.figures.clear()

    def transform(self, t, *_a, **_k):
        for f in self.figures: f.transform(t)
        return self

    def moveBy(self, dx, dy=0.0, *_a, **_k):
        for f in self.figures: f.moveBy(dx, dy)
        return self


# ── parameters ───────────────────────────────────────────────────────────────────────────────


class ChoiceConstraint:
    def __init__(self, choices: Sequence[Any]): self.choices = list(choices)


class RangeConstraint:
    def __init__(self, low=None, high=None, *_a, **_k): self.low, self.high = low, high


def Numeric(value: Any) -> float:
    """The kit's own coercion of a parameter value to a number.

    Accepts the engineering-notation strings a parameter may carry (``'1.5u'``), because a cell does
    ``Numeric(params['w']) * 1e6`` and the value may have arrived either way.
    """
    if isinstance(value, (int, float)): return float(value)
    text = str(value).strip()
    if not text: return 0.0
    try:
        return float(text)
    except ValueError:
        pass
    suffixes = {"a": 1e-18, "f": 1e-15, "p": 1e-12, "n": 1e-9, "u": 1e-6, "µ": 1e-6,
                "m": 1e-3, "k": 1e3, "K": 1e3, "M": 1e6, "G": 1e9, "T": 1e12}
    head, tail = text[:-1], text[-1]
    if tail in suffixes:
        try:
            return float(head) * suffixes[tail]
        except ValueError:
            pass
    raise ValueError(f"Cannot read {value!r} as a number")


class _ParamSpecs:
    """What a cell's ``defineParamSpecs`` is handed. Callable, and carries the technology."""

    def __init__(self, technology: "TechImpl | None" = None):
        # The kit's registration glue instantiates every cell with NO arguments, so the technology
        # has to be reachable without being passed — it comes from the registry the kit populated at
        # import time. An explicit one still wins, which is what generation uses.
        self.tech = technology if technology is not None else Tech.get()
        self.specs: list[dict[str, Any]] = []

    def __call__(self, name: str, default: Any = None, label: str = "", constraint: Any = None, *_a):
        self.specs.append({"name": str(name), "default": default,
                           "label": label or str(name), "constraint": constraint})


# ── the cell itself ──────────────────────────────────────────────────────────────────────────


class _Context:
    """Everything one generation run accumulates. One per :meth:`DloGen.generate` call."""

    def __init__(self, technology: crf.Technology | None, dbu_per_micron: int,
                 kit_layers: dict[str, tuple[int, int]] | None = None):
        self.figures: list[_Figure] = []
        self.pins: list[tuple[str, Box, Layer]] = []
        self.technology = technology
        self.dbu_per_micron = dbu_per_micron
        # The kit's OWN name-to-stream-number map, when it publishes one. Preferred over circuitRF's
        # technology because it is the kit stating its own layer numbers — no name matching to get
        # wrong, and it works even for a layer the current technology has never heard of.
        self.kit_layers = kit_layers or {}
        self.missing_layers: set[str] = set()


_context: _Context | None = None


def current_context() -> _Context | None:
    return _context


class TechImpl:
    """The base a kit subclasses to describe its own process.

    **The kit supplies this, not circuitRF** — its subclass reads its own JSON, states its own layer
    numbers and its own database unit. That is the right way round: the process data is the kit's, and
    a second copy of it here would be a second thing to keep in step.

    Note the subclass may not call ``super().__init__()``, so nothing here may require it.
    """

    def name(self) -> str: return "unnamed"
    def getTechParams(self) -> dict[str, Any]: return getattr(self, "_techParams", {})
    def getGridResolution(self) -> float: return 0.0
    def stream_layers(self) -> dict[str, tuple[int, int]]: return getattr(self, "_layers", {})

    @property
    def dataBaseUnits(self) -> float: return getattr(self, "_dataBaseUnits", 0.001)


class Tech(TechImpl):
    """The registry a kit registers its process with, and that cells look it up through."""

    _registered: "TechImpl | None" = None

    def __init__(self, params: dict[str, Any] | None = None, grid: float = 0.001):
        self._techParams = dict(params or {})
        self._grid = float(grid)

    # -- what the kit's own module calls at import time ----------------------

    @classmethod
    def register(cls, implementation: TechImpl) -> TechImpl:
        Tech._registered = implementation
        return implementation

    @classmethod
    def get(cls, *_a, **_k) -> TechImpl:
        if Tech._registered is None:
            Tech._registered = Tech()
        return Tech._registered

    # -- defaults, used only when no kit registered one ----------------------

    def getTechParams(self) -> dict[str, Any]: return self._techParams
    def getGridResolution(self) -> float: return self._grid
    def name(self) -> str: return "circuitRF"


class PCellWrapper:
    """The kit wraps a generator for registration. circuitRF registers them itself, so this only
    has to carry the class through without doing anything to it."""

    def __init__(self, generator, *_a, **_k):
        self.generator = generator


class DloGen:
    """Base class for a kit's parametric cell.

    A cell overrides ``defineParamSpecs(specs)`` (a classmethod), ``setupParams(params)`` and
    ``genLayout()``. :meth:`generate` drives those three in order and converts what they drew.
    """

    def __init__(self, technology: "TechImpl | None" = None):
        # The kit's registration glue instantiates every cell with NO arguments, so the technology
        # has to be reachable without being passed — it comes from the registry the kit populated at
        # import time. An explicit one still wins, which is what generation uses.
        self.tech = technology if technology is not None else Tech.get()
        self.techparams = self.tech.getTechParams()
        self.grid = self.tech.getGridResolution()
        self.pins: list[tuple[str, Box, Layer]] = []
        #: Free-form per-instance bag. Several devices stash their own state here; the kit's API
        #: provides it and cells assume it exists.
        self.props: dict[str, Any] = {}

    # -- what a cell calls ---------------------------------------------------

    def getShapes(self) -> list["_Figure"]:
        """Every figure drawn so far in THIS generation. A kit re-reads its own drawing to group or
        combine pieces of it, so this is the cell's live figure list, not a snapshot of a past run."""
        ctx = current_context()
        return list(ctx.figures) if ctx is not None else []

    def addPin(self, pinName: str, termName: str, box: Box, layer: Layer, *_a, **_k) -> "_PinHandle":
        ctx = current_context()
        entry = (str(pinName or termName), box if isinstance(box, Box) else Box(box), layer)
        self.pins.append(entry)
        if ctx is not None:
            ctx.pins.append(entry)
        # A HANDLE, not the tuple: the kit keeps talking to a pin after declaring it — most often
        # pin.addShape(fig), tying artwork to the terminal. Returning the internal tuple made that a
        # crash on an ordinary line.
        return _PinHandle(entry[0])

    def addTerm(self, name: str, termType: Any = None, *_a, **_k) -> Term:
        return Term(name, termType)

    # -- the lifecycle -------------------------------------------------------

    @classmethod
    def defineParamSpecs(cls, specs):  # pragma: no cover - overridden by every real cell
        pass

    def setupParams(self, params):     # pragma: no cover - overridden by every real cell
        pass

    def genLayout(self):               # pragma: no cover - overridden by every real cell
        pass

    @classmethod
    def declared_parameters(cls, technology: "Tech") -> list[dict[str, Any]]:
        specs = _ParamSpecs(technology)
        cls.defineParamSpecs(specs)
        return specs.specs

    @classmethod
    def generate(cls, technology: "Tech", params: dict[str, Any],
                 crf_tech: crf.Technology | None, dbu_per_micron: int) -> crf.Result:
        """Run one cell and hand back circuitRF geometry.

        The context is global for the duration because the kit's shape constructors register
        themselves — that is the kit's convention, and a cell holds no reference it could be threaded
        through. It is unwound in ``finally`` so a cell that raises cannot leak its half-drawn
        geometry into the next generation.
        """
        global _context
        kit_layers = {}
        try:
            kit_layers = dict(technology.stream_layers() or {})
        except Exception:                                  # noqa: BLE001 - optional, never fatal
            kit_layers = {}

        previous, _context = _context, _Context(crf_tech, dbu_per_micron, kit_layers)
        try:
            cell = cls(technology)
            cell.setupParams(params)
            cell.genLayout()
            return _emit(_context)
        finally:
            _context = previous


# ── converting what was drawn into circuitRF geometry ────────────────────────────────────────


def _emit(ctx: _Context) -> crf.Result:
    """Micrometres to database units, once, at the boundary.

    A missing layer is collected and reported as a diagnostic rather than raised: a cell that draws on
    twelve layers of which one is absent from the current technology has still drawn eleven correctly,
    and losing all of them would hide which one was actually missing.
    """
    shapes: list[Any] = []
    for fig in ctx.figures:
        layer = _resolve(fig.layer, ctx)
        if layer is None:
            continue
        shapes.extend(_convert(fig, layer, ctx))

    pins: list[crf.Pin] = []
    for name, box, layer_spec in ctx.pins:
        layer = _resolve(layer_spec, ctx)
        if layer is None:
            continue
        # Width and outward direction are NOT derived here. The kit states neither, and circuitRF
        # recovers both from the drawn artwork with a rule that was measured against real devices
        # (PinInference) — a second, weaker guess made here would quietly disagree with it.
        cx, cy = (box.left + box.right) / 2, (box.bottom + box.top) / 2
        pins.append(crf.Pin(name, _dbu(cx, ctx), _dbu(cy, ctx), layer,
                            _dbu(box.getHeight(), ctx) or 1, 0.0))

    diagnostics = []
    if ctx.missing_layers:
        diagnostics.append(
            "The technology does not define these layers, so nothing was drawn on them: "
            + ", ".join(sorted(ctx.missing_layers)) + ".")

    return crf.Result(shapes=shapes, pins=pins, diagnostics=diagnostics)


def _resolve(layer: Layer, ctx: _Context) -> crf.Layer | None:
    """A cell's layer name to a stream number.

    The kit's own map is asked first — it is the kit stating its own numbers, so there is no name
    matching to get wrong and it covers layers the current technology has never heard of. circuitRF's
    technology is the fallback, matched on the full spelling first because an imported process layer
    keeps its purpose in its name (``Metal1.drawing``), which is what makes that lookup unambiguous.
    """
    for key in (layer.full, f"{layer.name}_{layer.purpose}", layer.name):
        entry = ctx.kit_layers.get(key)
        if entry is not None:
            return crf.Layer(int(entry[0]), int(entry[1]))

    tech = ctx.technology
    if tech is not None:
        found = tech.layer_named(layer.full) or tech.layer_named(layer.name)
        if found is not None:
            return found

    ctx.missing_layers.add(layer.full)
    return None


def _dbu(microns: float, ctx: _Context) -> int:
    return crf.dbu(microns * ctx.dbu_per_micron)


def _convert(fig: _Figure, layer: crf.Layer, ctx: _Context) -> list[Any]:
    if isinstance(fig, (Rect, Ellipse)):
        b = fig.getBBox()
        # An ellipse is emitted as its bounding rectangle only when it is actually a circle; anything
        # else would be a different shape drawn confidently, so it is refused instead.
        if isinstance(fig, Ellipse):
            raise UnsupportedByCircuitRF("Ellipse")
        return [crf.Rect(layer, _dbu(b.left, ctx), _dbu(b.bottom, ctx),
                         _dbu(b.right, ctx), _dbu(b.top, ctx))]

    if isinstance(fig, Polygon):
        xy: list[int] = []
        for p in fig.points:
            xy += [_dbu(p.x, ctx), _dbu(p.y, ctx)]
        holes: list[list[int]] = []
        for ring in getattr(fig, "holes", ()):
            h: list[int] = []
            for p in ring:
                h += [_dbu(p.x, ctx), _dbu(p.y, ctx)]
            holes.append(h)
        return [crf.Polygon(layer, xy, holes=holes or None)]

    if isinstance(fig, Path):
        xy: list[int] = []
        for p in fig.points:
            xy += [_dbu(p.x, ctx), _dbu(p.y, ctx)]
        return [crf.Path(layer, xy, _dbu(fig.width, ctx))]

    if isinstance(fig, Text):
        return [crf.Label(layer, _dbu(fig.origin.x, ctx), _dbu(fig.origin.y, ctx),
                          fig.text, _dbu(fig.height, ctx))]

    raise UnsupportedByCircuitRF(type(fig).__name__)


# ── small helpers the kit's cells reach for ──────────────────────────────────────────────────


def octagon(box: Box, chamfer: float) -> PointList:
    """The eight corners of a chamfered rectangle, anticlockwise from the lower left."""
    c = float(chamfer)
    return PointList([
        (box.left + c, box.bottom), (box.right - c, box.bottom),
        (box.right, box.bottom + c), (box.right, box.top - c),
        (box.right - c, box.top), (box.left + c, box.top),
        (box.left, box.top - c), (box.left, box.bottom + c),
    ])


def strToOrient(text): return str(text)
def strToAlignt(text): return str(text)
class ulist(list):
    """A list the kit builds as ``ulist[Rect]()`` — a GENERIC TYPE, not a function.

    The element type is decoration in Python and is discarded; what matters is that the subscript
    works at all, since a plain function raises "not subscriptable" and takes the whole cell with it.
    Varargs construction is accepted too, because the kit uses both spellings."""

    def __init__(self, *items):
        if len(items) == 1 and isinstance(items[0], (list, tuple)):
            super().__init__(items[0])
        else:
            super().__init__(items)

    def __class_getitem__(cls, _item): return cls
def append(a, b): return list(a) + list(b)
def cadr(seq): return seq[1]
def caddr(seq): return seq[2]
def cadddr(seq): return seq[3]

sqrt, cos, sin, acos = math.sqrt, math.cos, math.sin, math.acos
