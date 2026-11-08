"""Layer boolean operations — performed by circuitRF, not here.

**A second polygon clipper is the mistake this file exists to avoid.** circuitRF already does layer
booleans with Clipper2, tested against its own geometry. Implementing them again in Python would put
two implementations of one rule on either side of a process boundary — which is exactly the reasoning
that keeps metres off the wire, and the failure mode here is worse: a boolean result off by a database
unit produces geometry that renders perfectly and is wrong.

So these four functions convert the figures to rings, ask the host, and rebuild the answer as ordinary
figures. There is one clipper, and a generator's booleans agree with circuitRF's own by construction.

Measured: ten device cells reach for these — including the base class of the four RF
MOSFETs — so this is the difference between a static cell and a parametric one.
"""

from __future__ import annotations

from typing import Any, Iterable

import circuitrf_pcell as crf

from .dlo import (
    Box,
    Grouping,
    Layer,
    Point,
    Polygon,
    Rect,
    UnsupportedByCircuitRF,
    current_context,
)

__all__ = ["fgAnd", "fgOr", "fgNot", "fgXor", "fgSize", "FigureGroup"]


class FigureGroup(list):
    """What a boolean returns: the resulting figures, usable as a list or as one figure.

    A kit treats a boolean's result both ways — iterating it, and calling figure methods on it as a
    whole — so this is a ``list`` that also answers the small figure surface those call sites use.
    """

    def getBBox(self, *_a) -> Box:
        if not self:
            return Box(0, 0, 0, 0)
        boxes = [f.getBBox() for f in self]
        return Box(
            min(b.left for b in boxes), min(b.bottom for b in boxes),
            max(b.right for b in boxes), max(b.top for b in boxes),
        )

    @property
    def bbox(self) -> Box:
        return self.getBBox()

    def setLayer(self, layer: Layer) -> "FigureGroup":
        for f in self:
            f.layer = layer
        return self

    def getLayer(self):
        return self[0].layer if self else None

    def transform(self, tf, *_a, **_k) -> "FigureGroup":
        for f in self: f.transform(tf)
        return self

    def moveBy(self, dx, dy=0.0, *_a, **_k) -> "FigureGroup":
        for f in self: f.moveBy(dx, dy)
        return self

    def getComp(self, index: int):
        """One region by index. The kit reaches for a specific piece of a boolean's result."""
        return self[index]

    def getComps(self) -> list:
        """The individual regions. A kit checks how many a boolean produced before deciding what to
        do with it — a union that stayed in two pieces means something different from one that merged."""
        return list(self)

    def setNet(self, net):
        for f in self:
            f.setNet(net)

    def destroy(self) -> None:
        for f in self:
            f.destroy()
        self.clear()

    def fgOr(self, other=None, *_a, **_k):  return fgOr(self, other)
    def fgAnd(self, other=None, *_a, **_k): return fgAnd(self, other)
    def fgNot(self, other=None, *_a, **_k): return fgNot(self, other)
    def fgXor(self, other=None, *_a, **_k): return fgXor(self, other)
    def fgSize(self, *a, **k):              return fgSize(self, *_size_args(*a, **k))


def _size_args(*a, **k) -> tuple:
    """Normalise the kit's own ``fgSize`` call shape.

    A kit writes ``fig.fgSize(ShapeFilter(), size, layerId, grid)`` — a filter it uses to select
    within a group, then the amount, then the target layer, then a snap grid. This layer holds one
    figure at a time (the filter has nothing to select from) and works in exact integer database units
    (a snap grid is a rounding refinement it does not need), so both are accepted and dropped rather
    than refused: a call that ran elsewhere must not fail here on arguments that mean nothing to us.
    """
    args = [x for x in a if not isinstance(x, (bool,))]
    size = next((x for x in args if isinstance(x, (int, float))), k.get("size", 0))
    layer = next((x for x in args if isinstance(x, (Layer, str))), k.get("layer"))
    return (size, layer)


# ── figures ⇄ rings ──────────────────────────────────────────────────────────


def _as_figures(operand: Any) -> list:
    """Accept the several shapes a kit passes a boolean operand in."""
    if operand is None:
        return []
    if isinstance(operand, Grouping):
        return _as_figures(operand.figures)
    if isinstance(operand, (list, tuple, set, FigureGroup)):
        out: list = []
        for item in operand:
            out.extend(_as_figures(item))
        return out
    return [operand]


def _rings_of(fig: Any, dbu_per_micron: int) -> list[list[tuple[int, int]]]:
    """One figure's closed rings, in DATABASE UNITS.

    Converted here, once, with the same rule :func:`circuitrf_pcell.dbu` applies everywhere else —
    the wire speaks database units and nothing downstream re-scales.
    """
    def d(microns: float) -> int:
        return crf.dbu(microns * dbu_per_micron)

    if isinstance(fig, Rect):
        b = fig.box
        return [[(d(b.left), d(b.bottom)), (d(b.right), d(b.bottom)),
                 (d(b.right), d(b.top)), (d(b.left), d(b.top))]]

    if isinstance(fig, Polygon):
        outer = [(d(p.x), d(p.y)) for p in fig.points]
        holes = [[(d(p.x), d(p.y)) for p in h] for h in getattr(fig, "holes", ())]
        return _hole_free(outer, holes) if holes else [outer]

    raise UnsupportedByCircuitRF(
        f"layer boolean on {type(fig).__name__}: only rectangles and polygons are closed regions "
        "with an unambiguous outline. A path's outline depends on its end style, so it is widened "
        "into a polygon before it can take part in a boolean."
    )


def _hole_free(outer: list[tuple[int, int]], holes: list[list[tuple[int, int]]]) -> list[list[tuple[int, int]]]:
    """A region with holes as hole-free rings whose union is that region, cut by the host.

    The host reads an operand as a union of rings and turns every ring into a solid, so a hole cannot
    go in as a ring of its own: it would be filled. Cut at each hole vertex's y, a horizontal slab of
    the region cannot enclose a hole — any hole it meets spans it from cut to cut — so each slab is
    the outline minus the holes and minus what lies above and below it, asked of the host like any
    other boolean.
    """
    xs = [x for x, _ in outer]
    ys = [y for _, y in outer]
    left, right, bottom, top = min(xs) - 1, max(xs) + 1, min(ys) - 1, max(ys) + 1

    def band(lo: int, hi: int) -> list[tuple[int, int]]:
        return [(left, lo), (right, lo), (right, hi), (left, hi)]

    cuts = [bottom] + sorted({y for h in holes for _, y in h}) + [top]
    rings = []
    for lo, hi in zip(cuts, cuts[1:]):
        for piece in crf.clip("not", [outer], holes + [band(bottom - 1, lo), band(hi, top + 1)]):
            if piece.holes:                                 # cannot happen; never fill one silently
                raise UnsupportedByCircuitRF("layer boolean on a region whose holes could not be cut out")
            rings.append(piece.outer)
    return rings


def _clip(rule: str, subject: Any, clip: Any) -> FigureGroup:
    ctx = current_context()
    if ctx is None:
        raise UnsupportedByCircuitRF(
            f"layer boolean: {rule} was called outside a cell generation. Booleans build a cell's "
            "geometry and only mean something while one is being built."
        )

    subject_figs = _as_figures(subject)
    clip_figs = _as_figures(clip)
    if not subject_figs:
        return FigureGroup()

    layer = subject_figs[0].layer
    net = subject_figs[0].getNet()

    subject_rings = [r for f in subject_figs for r in _rings_of(f, ctx.dbu_per_micron)]
    clip_rings = [r for f in clip_figs for r in _rings_of(f, ctx.dbu_per_micron)]

    polygons = crf.clip(rule, subject_rings, clip_rings)

    # The operands stay drawn, as the API defines it: a kit deletes the ones it does not want and
    # keeps the rest, so consuming them here deleted artwork the kit kept.

    scale = 1.0 / ctx.dbu_per_micron
    result = FigureGroup()
    for poly in polygons:
        pts = [Point(x * scale, y * scale) for x, y in poly.outer]
        # Holes are carried, not dropped: subtracting a via pad from a pour is the single most common
        # layout boolean there is, and circuitRF's own model represents the result exactly (§3.1a).
        rings = [[Point(x * scale, y * scale) for x, y in h] for h in poly.holes]
        fig = Polygon(layer, pts, holes=rings)
        if net is not None:
            fig.setNet(net)
        result.append(fig)

    return result


# ── the four operations ──────────────────────────────────────────────────────


def fgAnd(subject: Any, clip: Any = None, *_a, **_k) -> FigureGroup:
    """The region common to both operands."""
    return _clip("and", subject, clip)


def fgOr(subject: Any, clip: Any = None, *_a, **_k) -> FigureGroup:
    """The union of both operands. With one operand, merges it into as few regions as possible."""
    return _clip("or", subject, clip)


def fgNot(subject: Any, clip: Any = None, *_a, **_k) -> FigureGroup:
    """The first operand with the second removed from it. Order matters."""
    return _clip("not", subject, clip)


def fgXor(subject: Any, clip: Any = None, *_a, **_k) -> FigureGroup:
    """The region covered by exactly one of the two operands."""
    return _clip("xor", subject, clip)


def fgSize(subject: Any, size: float, layer: Layer | str | None = None, *_a, **_k) -> FigureGroup:
    """Grow (positive ``size``) or shrink (negative) a figure, in MICRONS.

    **The result is a NEW figure and the original is left alone,** as with the booleans. This is how a
    kit derives one layer from another — a well grown out of the diffusion it must enclose — and both
    pieces are real artwork that has to survive.

    ``layer`` is where the result is drawn; omitted, it stays on the subject's own layer. That
    re-layering is the whole point of the derive idiom, so it is an argument rather than a later step.
    """
    ctx = current_context()
    if ctx is None:
        raise UnsupportedByCircuitRF(
            "fgSize was called outside a cell generation; it builds a cell's geometry and only means "
            "something while one is being built."
        )

    figures = _as_figures(subject)
    if not figures:
        return FigureGroup()
    if size < 0 and any(getattr(f, "holes", None) for f in figures):
        # A region with holes goes in as hole-free pieces (_hole_free). Growing the pieces grows the
        # region; shrinking them opens a gap along every cut. Refused by name rather than drawn wrong.
        raise UnsupportedByCircuitRF("fgSize shrinking a polygon with holes")

    rings = [r for f in figures for r in _rings_of(f, ctx.dbu_per_micron)]
    delta = crf.dbu(size * ctx.dbu_per_micron)

    target = figures[0].layer if layer is None else (layer if isinstance(layer, Layer) else Layer(str(layer)))
    net = figures[0].getNet()

    scale = 1.0 / ctx.dbu_per_micron
    result = FigureGroup()
    for poly in crf.offset(delta, rings):
        pts = [Point(x * scale, y * scale) for x, y in poly.outer]
        holes = [[Point(x * scale, y * scale) for x, y in h] for h in poly.holes]
        fig = Polygon(target, pts, holes=holes)
        if net is not None:
            fig.setNet(net)
        result.append(fig)

    return result
