"""Writers for the two documents a plugin most often stages, so it never hand-writes either format.

- :class:`Netlist` writes a ``.cnl`` from a list of parts.
- :func:`clay_text` writes a ``.clay`` from a :class:`~circuitrf_plugin.views.Layout`. A ``panel`` plugin rarely needs
  it - naming its layout view in ``placed`` (``"layout": "artwork"``) has circuitRF write the ``.clay`` itself - but a
  ``window`` or ``action`` plugin has no view to name.
"""

from __future__ import annotations

import json
from typing import Optional

from .views import Layout


def _value(v) -> str:
    if isinstance(v, bool):
        return "true" if v else "false"
    if isinstance(v, float):
        return "%.12g" % v
    return str(v)


class Netlist:
    """A ``.cnl``, one line per part, in the order added::

        net = Netlist()
        net.part("Port", "P1", "in", "0", Num=1, Z="50 Ohm")
        net.part("TLIN", "TL1", "in", "out", Z="50 Ohm", L="12.3 mm", Eeff=2.9)
        net.part("Port", "P2", "out", "0", Num=2, Z="50 Ohm")
        net.analysis("SP1", type="sparam", start=1, stop=6, npts=51, Unit="GHz")
        ctx.stage("line.cnl", net.text())

    A value is written as given: a string keeps its unit (``"50 Ohm"``), a float is written to 12 significant digits
    in base units. Node ``0`` is ground. A comment line starts with ``;``.
    """

    def __init__(self):
        self.lines: list[str] = []

    def part(self, kind: str, name: str, *nodes: str, **params) -> "Netlist":
        words = ["%s:%s" % (kind, name)] + [str(n) for n in nodes]
        words += ["%s=%s" % (k, _value(v)) for k, v in params.items()]
        self.lines.append(" ".join(words))
        return self

    def analysis(self, name: str, **keys) -> "Netlist":
        """An analysis line: ``type`` and its keys, as ``circuitRF reference analyses`` lists them."""
        self.lines.append(" ".join(["analysis", name] + ["%s=%s" % (k, _value(v)) for k, v in keys.items()]))
        return self

    def line(self, text: str) -> "Netlist":
        """Any other line, as written: a variable, or a comment, which starts with ``;``."""
        self.lines.append(text)
        return self

    def text(self) -> str:
        return "\n".join(self.lines) + "\n"


_ENDS = {"flush": "Flush", "round": "Round", "square": "Square", "extended": "Extended"}


def clay_text(layout: Layout, technology: dict, tech_ref: Optional[str] = None) -> str:
    """``layout`` as a ``.clay`` document, its ports as port labels.

    ``technology`` is what a ``technology`` parameter (or ``ctx.resolve("technology")``) delivered: its ``layers``
    turn layer names into layer numbers and its ``dbuPerMicron`` is the document's. ``tech_ref`` names the technology
    relative to the placed ``layout`` folder; leave it out for the workspace's default technology.

    Rectangles, polygons, paths and labels are written; any other shape is refused by name.
    """
    names = {l["name"]: {"Layer": l["layer"], "Datatype": l["datatype"]} for l in technology.get("layers", [])}

    def layer(value):
        if isinstance(value, str):
            if value not in names:
                raise ValueError("The technology has no layer called %r." % value)
            return names[value]
        return {"Layer": value["layer"], "Datatype": value["datatype"]}

    shapes = []
    for fields, xy, holes in layout.shapes:
        kind = fields["kind"]
        if kind == "rect":
            shapes.append({"$type": "Rect", "X1": xy[0], "Y1": xy[1], "X2": xy[2], "Y2": xy[3], "Layer": layer(fields["layer"])})
        elif kind == "poly":
            s = {"$type": "Poly", "Xy": xy, "Layer": layer(fields["layer"])}
            if holes:
                s["Holes"] = holes
            shapes.append(s)
        elif kind == "path":
            shapes.append({"$type": "Path", "Xy": xy, "Width": fields["width"], "End": _ENDS[fields.get("end", "flush")],
                           "Layer": layer(fields["layer"])})
        elif kind == "label":
            shapes.append({"$type": "Label", "X": xy[0], "Y": xy[1], "Text": fields["text"], "Height": fields["height"],
                           "Rotation": fields.get("rotation", "R0"), "Layer": layer(fields["layer"])})
        else:
            raise ValueError("clay_text writes rect, poly, path and label shapes, not %r." % kind)

    dbu_per_micron = technology.get("dbuPerMicron", 1000)
    for p in layout.ports:
        # A port label faces INTO its conductor: the opposite of the port's outward direction, to the nearest 90°.
        inward = ("R0", "R90", "R180", "R270")[int(round((p["outwardDeg"] + 180.0) / 90.0)) % 4]
        shapes.append({"$type": "Label", "X": p["x"], "Y": p["y"], "Text": p["name"],
                       "Height": max(p["width"] // 2, dbu_per_micron), "Rotation": "R0", "IsPort": True,
                       "PortDirection": inward, "PortLayer": layer(p["layer"]), "Layer": layer(p["layer"])})

    doc = {"FormatVersion": 1, "DbuPerMicron": dbu_per_micron, "Shapes": shapes, "Instances": []}
    if tech_ref:
        doc["TechRef"] = tech_ref
    return json.dumps(doc, indent=2) + "\n"
