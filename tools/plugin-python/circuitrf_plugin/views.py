"""The views a reply carries - builders for the JSON writing-plugins.md describes, so a plugin never spells it by hand.

Every value is base SI (hertz, metres, ohms) except layout geometry, which is integer database units (DBU): convert
lengths with ``ctx.dbu(metres)``. A float coordinate is refused here, before it could reach circuitRF.
"""

from __future__ import annotations

import operator
from typing import Any, Iterable, Optional, Sequence


# ── plot and smith ──────────────────────────────────────────────────────────────


class Plot:
    """A ``plot`` view: data (axes and cubes, or a staged file) plus what to draw of it.

    ::

        view = Plot(y={"label": "dB", "min": -40, "max": 0})
        view.axis("freq", "Hz", freqs)
        view.cube("S11", ["freq"], s11)              # complex values become re/im
        view.trace("S11", y="db20", label="S11")
        view.marker("S11", bind="f0")                # a marker the user drags to change f0
        view.line(-20, side="above", x_start=2.3e9, x_end=2.5e9)
    """

    def __init__(self, title: Optional[str] = None, x: Optional[dict] = None, y: Optional[dict] = None,
                 y2: Optional[dict] = None, freq_unit: Optional[str] = None, legend: Optional[str] = None):
        self._axes: dict[str, Any] = {}
        self._cubes: dict[str, Any] = {}
        self._file: Optional[str] = None
        self._plot: dict[str, Any] = {}
        for key, value in (("title", title), ("x", x), ("y", y), ("y2", y2), ("freqUnit", freq_unit), ("legend", legend)):
            if value is not None:
                self._plot[key] = value
        self._traces: list[dict] = []
        self._markers: list[dict] = []
        self._lines: list[dict] = []

    def axis(self, name: str, unit: str, values: Iterable[float]) -> "Plot":
        self._axes[name] = {"unit": unit, "values": [float(v) for v in values]}
        return self

    def cube(self, name: str, axes: Sequence[str], values: Iterable) -> "Plot":
        """A named cube over ``axes``. Complex values cross as ``re``/``im``; real ones as ``values``."""
        values = list(values)
        if any(isinstance(v, complex) for v in values):
            self._cubes[name] = {"axes": list(axes), "kind": "complex",
                                 "re": [complex(v).real for v in values], "im": [complex(v).imag for v in values]}
        else:
            self._cubes[name] = {"axes": list(axes), "kind": "real", "values": [float(v) for v in values]}
        return self

    def data_file(self, staged: str) -> "Plot":
        """The data as a staged ``.sNp`` or ``.npy`` instead of axes and cubes - for a large result."""
        self._file = staged
        return self

    def share_data(self, other: "Plot") -> "Plot":
        """Draw ``other``'s data too (a Smith chart of the same cubes, say)."""
        self._axes, self._cubes, self._file = other._axes, other._cubes, other._file
        return self

    def trace(self, cube: str, **fields) -> "Plot":
        """A trace: ``cube`` plus any of ``y`` (``db20``, ``db10``, ``mag``, ``phase``, ``real``, ``imag``, ``conj``,
        ``none``), ``i``, ``j`` (1-based port numbers), ``label``, ``axis`` (``left`` or ``right``) - the fields
        ``circuitRF plot --trace`` takes. ``y="db"`` is refused: say db20 or db10."""
        self._traces.append(dict({"cube": cube}, **fields))
        return self

    def marker(self, trace: str, bind: Optional[str] = None, x=None, draggable: Optional[bool] = None) -> "Plot":
        """A marker on ``trace``: ``bind`` a quantity parameter to drag it, or sit it at ``x`` (base SI, or a
        parameter's name)."""
        m: dict[str, Any] = {"trace": trace}
        if bind is not None:
            m["bind"] = bind
        if x is not None:
            m["x"] = x
        if draggable is not None:
            m["draggable"] = draggable
        self._markers.append(m)
        return self

    def line(self, y: float, side: str = "above", x_start: Optional[float] = None, x_end: Optional[float] = None) -> "Plot":
        """A limit line at ``y`` (the plot's own units), hatched on ``side``; ``x_start``/``x_end`` in base SI."""
        ln: dict[str, Any] = {"y": float(y), "side": side}
        if x_start is not None:
            ln["xStart"] = float(x_start)
        if x_end is not None:
            ln["xEnd"] = float(x_end)
        self._lines.append(ln)
        return self

    def to_json(self, payload: list[int]) -> dict:
        data: dict[str, Any] = {"file": self._file} if self._file else {"axes": self._axes, "cubes": self._cubes}
        plot = dict(self._plot)
        plot["traces"] = self._traces
        if self._markers:
            plot["markers"] = self._markers
        if self._lines:
            plot["lines"] = self._lines
        return {"data": data, "plot": plot}


class Smith(Plot):
    """A ``smith`` view: the same object as :class:`Plot`, drawn on a Smith chart. Its cubes are reflection
    coefficients or impedances; a bound marker is bound to a frequency parameter."""


# ── table ───────────────────────────────────────────────────────────────────────


class Table:
    """A ``table`` view, or an ``as: table`` readout. Numbers in base SI; circuitRF formats them in each column's
    ``units`` entry (``""`` for a plain number or text)."""

    def __init__(self, columns: Sequence[str], units: Optional[Sequence[str]] = None, rows: Optional[list] = None):
        self.columns = list(columns)
        self.units = list(units) if units is not None else [""] * len(self.columns)
        self.rows = [list(r) for r in (rows or [])]

    def row(self, *cells) -> "Table":
        self.rows.append(list(cells))
        return self

    def to_json(self, payload: list[int]) -> dict:
        return {"columns": self.columns, "units": self.units, "rows": self.rows}


# ── schematic and image ─────────────────────────────────────────────────────────


class Schematic:
    """A ``schematic`` view: a staged ``.cnl``, drawn by circuitRF."""

    def __init__(self, cnl: str):
        self.cnl = cnl

    def to_json(self, payload: list[int]) -> dict:
        return {"cnl": self.cnl}


class Image:
    """An ``image`` view: a staged PNG or SVG. ``clickable`` sends an event with the click's pixel position."""

    def __init__(self, file: str, clickable: bool = False):
        self.file = file
        self.clickable = clickable

    def to_json(self, payload: list[int]) -> dict:
        return {"file": self.file, "clickable": self.clickable} if self.clickable else {"file": self.file}


# ── layout ──────────────────────────────────────────────────────────────────────


def coord(v, what: str = "a coordinate") -> int:
    """An integer DBU value, or a TypeError naming what was wrong. ``True`` and ``2.0`` are both refused."""
    if isinstance(v, bool):
        raise TypeError("%s must be an integer number of DBU, not a bool." % what.capitalize())
    try:
        return operator.index(v)
    except TypeError:
        raise TypeError("%s must be an integer number of DBU, not %r: convert a length with ctx.dbu(metres)."
                        % (what.capitalize(), v)) from None


class Layout:
    """A ``layout`` view: shapes, handles and ports in integer DBU, on technology layers named as the technology names
    them (a substrate's ``stackup`` entries list their drawing ``layers``).

    ::

        art = Layout()
        art.rect("Top Copper", 0, -w // 2, length, w // 2)
        art.port("1", 0, 0, "Top Copper", w, outward_deg=180)
        art.handle("L", anchor=(0, 0), grip=(length, 0), axis_deg=0, label="Length")

    ``technology`` is ``"self"`` (the one the plugin's technology or substrate parameter names) or a ``.ctech`` path
    relative to the workspace.
    """

    def __init__(self, technology: str = "self"):
        self.technology = technology
        self.shapes: list[tuple[dict, list[int], list[list[int]]]] = []
        self.handles: list[tuple[dict, list[int]]] = []
        self.ports: list[dict] = []

    @staticmethod
    def _layer(layer):
        if isinstance(layer, str):
            return layer
        if isinstance(layer, (tuple, list)) and len(layer) == 2:
            return {"layer": coord(layer[0], "a layer number"), "datatype": coord(layer[1], "a datatype")}
        if isinstance(layer, dict):
            return layer
        raise TypeError("A layer is a technology layer's name or (layer, datatype), not %r." % (layer,))

    @staticmethod
    def _points(points, what: str) -> list[int]:
        flat: list[int] = []
        for pt in points:
            if len(pt) != 2:
                raise ValueError("Each point of %s is (x, y)." % what)
            flat += [coord(pt[0]), coord(pt[1])]
        return flat

    def _add(self, fields: dict, xy: list[int], holes: Optional[list[list[int]]] = None) -> "Layout":
        self.shapes.append((fields, xy, holes or []))
        return self

    def rect(self, layer, x1, y1, x2, y2, net: Optional[str] = None) -> "Layout":
        return self._add(_net({"kind": "rect", "layer": self._layer(layer)}, net), [coord(x1), coord(y1), coord(x2), coord(y2)])

    def polygon(self, layer, points, holes=None, net: Optional[str] = None) -> "Layout":
        if len(points) < 3:
            raise ValueError("A polygon has at least three points.")
        return self._add(_net({"kind": "poly", "layer": self._layer(layer)}, net), self._points(points, "a polygon"),
                         [self._points(h, "a hole") for h in (holes or [])])

    def path(self, layer, points, width, end: str = "flush", net: Optional[str] = None) -> "Layout":
        """A centreline ``path`` of ``width`` DBU; ``end`` is ``flush``, ``round``, ``square`` or ``extended``."""
        if len(points) < 2:
            raise ValueError("A path has at least two points.")
        return self._add(_net({"kind": "path", "layer": self._layer(layer), "width": coord(width, "a path width"),
                               "end": end}, net), self._points(points, "a path"))

    def circle(self, layer, cx, cy, radius, net: Optional[str] = None) -> "Layout":
        return self._add(_net({"kind": "circle", "layer": self._layer(layer), "radius": coord(radius, "a radius")}, net),
                         [coord(cx), coord(cy)])

    def via(self, layer, x, y, pad, drill, landing=None) -> "Layout":
        fields = {"kind": "via", "layer": self._layer(layer), "padSize": coord(pad, "a pad size"),
                  "drillSize": coord(drill, "a drill size")}
        if landing is not None:
            fields["landingLayer"] = self._layer(landing)
        return self._add(fields, [coord(x), coord(y)])

    def label(self, layer, text: str, x, y, height, rotation: str = "R0") -> "Layout":
        return self._add({"kind": "label", "layer": self._layer(layer), "text": text, "height": coord(height, "a height"),
                          "rotation": rotation}, [coord(x), coord(y)])

    def port(self, name: str, x, y, layer, width, outward_deg: float) -> "Layout":
        """A port: drawn as a layout port label, and a real port once placed. ``outward_deg`` points out of the
        conductor (180 for a line's left end, 0 for its right)."""
        self.ports.append({"name": str(name), "x": coord(x), "y": coord(y), "layer": self._layer(layer),
                           "width": coord(width, "a port width"), "outwardDeg": float(outward_deg)})
        return self

    def handle(self, parameter: str, anchor, grip, axis_deg: float, kind: str = "linear", label: Optional[str] = None,
               min: Optional[float] = None, max: Optional[float] = None) -> "Layout":
        """A grip at ``grip`` the user drags to change ``parameter`` (a quantity). ``anchor`` is the point a ``linear``
        handle measures from along ``axis_deg``, or an ``angular`` one turns about. ``min``/``max`` are parameter
        values (base SI), not coordinates."""
        h: dict[str, Any] = {"parameter": parameter, "kind": kind, "axisDeg": float(axis_deg)}
        if label is not None:
            h["label"] = label
        if min is not None:
            h["min"] = float(min)
        if max is not None:
            h["max"] = float(max)
        self.handles.append((h, [coord(anchor[0]), coord(anchor[1]), coord(grip[0]), coord(grip[1])]))
        return self

    def to_json(self, payload: list[int]) -> dict:
        def span(values: list[int]) -> dict:
            at = len(payload)
            payload.extend(values)
            return {"at": at, "count": len(values)}

        shapes = []
        for fields, xy, holes in self.shapes:
            s = dict(fields)
            s["xy"] = span(xy)
            if holes:
                s["holes"] = [span(h) for h in holes]
            shapes.append(s)
        handles = []
        for fields, values in self.handles:
            h = dict(fields)
            h["span"] = span(values)
            handles.append(h)
        body: dict[str, Any] = {"technology": self.technology, "shapes": shapes}
        if handles:
            body["handles"] = handles
        if self.ports:
            body["ports"] = list(self.ports)
        return body


def _net(fields: dict, net: Optional[str]) -> dict:
    if net is not None:
        fields["net"] = net
    return fields
