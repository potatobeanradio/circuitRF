"""The resolved technology a generator is given — schema §4.2.

**Every length here is in database units, and there are no metres.** ``StackupLayer.thickness`` is
DBU exactly as circuitRF's own ``StackupLayer.ThicknessDbu`` is; SI metres are a derived view on the
C# side for the electrical models, not the stored form.

The consequence worth knowing before writing a generator: the wire is **scale-free**. You can
compute a length from a RATIO — which is what the closed-form microstrip relations are, functions of
``W/h`` — but you cannot compute one from a physical constant, because you are given a conductivity
in S/m and no metre to interpret it with. That is deliberate. A generator that genuinely needs an
absolute physical length is a schema change, not a workaround.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Sequence

from .geometry import Layer


@dataclass(frozen=True)
class LayerInfo:
    """One entry of the technology's layer table."""

    layer: int
    datatype: int
    name: str
    purpose: str | None = None

    @property
    def key(self) -> Layer:
        return Layer(self.layer, self.datatype)


@dataclass(frozen=True)
class StackupLayer:
    """One physical layer of the stack, ordered top to bottom.

    ``epsr`` / ``tand`` / ``mur`` are present on a dielectric; ``sigma`` (S/m) on a conductor. The
    other set is absent rather than defaulted, because a default nobody chose reads exactly like a
    value somebody did.
    """

    kind: str
    name: str
    thickness: int
    epsr: float | None = None
    tand: float | None = None
    mur: float | None = None
    sigma: float | None = None
    is_ground_reference: bool = False
    drawing_layers: tuple[Layer, ...] = ()

    @property
    def is_conductor(self) -> bool:
        return self.kind == "conductor"

    @property
    def is_dielectric(self) -> bool:
        return self.kind == "dielectric"


@dataclass
class Stackup:
    top: str = "open"
    bottom: str = "ground"
    layers: list[StackupLayer] = field(default_factory=list)

    @property
    def conductors(self) -> list[StackupLayer]:
        return [layer for layer in self.layers if layer.is_conductor]

    @property
    def dielectrics(self) -> list[StackupLayer]:
        return [layer for layer in self.layers if layer.is_dielectric]

    def named(self, name: str) -> StackupLayer | None:
        for layer in self.layers:
            if layer.name == name:
                return layer
        return None


class Technology:
    """What a generator is told about the process it is drawing for.

    ``signal_layer`` and ``ground_layer`` are the RESOLVED answer, computed by circuitRF's own
    substrate resolution (plus any per-instance override) before the request was sent. Do not
    re-derive them from the stackup: a second implementation of that rule fails silently, putting
    geometry on a plausible but wrong layer.

    Every field can be absent — a layout with no technology resolved still generates geometry
    (pcell-contract.md §2); only the ELECTRICAL stamp refuses without one. So a generator must have a
    fallback for :attr:`signal_layer` being ``None`` rather than assuming a technology is there.
    """

    def __init__(self, raw: dict[str, Any] | None):
        raw = raw or {}
        layers_raw = raw.get("layers") or {}
        stackup_raw = raw.get("stackup")

        #: Database units per micrometre in the layout being drawn into (wire version 2), or ``None``
        #: when the host did not state one.
        #:
        #: **This is not permission to convert metres, and the distinction matters.** Length
        #: PARAMETERS still arrive already in DBU, converted once by circuitRF's own rounding rule —
        #: there are still no metres in any message. What this is for is a constant the GENERATOR
        #: itself holds: a process dimension read out of a kit's own data, which is a physical length
        #: in micrometres and has no other way of becoming a coordinate. Multiply by it, then round
        #: with :func:`dbu`.
        self.dbu_per_micron: int | None = (
            int(raw["dbuPerMicron"]) if raw.get("dbuPerMicron") else None
        )

        self.signal_layer: Layer | None = Layer.from_json(layers_raw.get("signal"))
        self.ground_layer: Layer | None = Layer.from_json(layers_raw.get("ground"))
        self.layers: list[LayerInfo] = [
            LayerInfo(int(e["layer"]), int(e.get("datatype", 0)), e.get("name", ""), e.get("purpose"))
            for e in layers_raw.get("table", [])
        ]
        self.stackup: Stackup | None = _read_stackup(stackup_raw) if stackup_raw else None

    def layer_named(self, name: str) -> Layer | None:
        """Look a drawing layer up by the name the technology gives it."""
        for entry in self.layers:
            if entry.name == name:
                return entry.key
        return None

    def __repr__(self) -> str:  # pragma: no cover - diagnostics only
        return f"Technology(signal={self.signal_layer}, layers={len(self.layers)})"


def _read_stackup(raw: dict[str, Any]) -> Stackup:
    layers: list[StackupLayer] = []
    for entry in raw.get("layers", []):
        drawing: Sequence[Layer] = tuple(
            Layer(int(d["layer"]), int(d.get("datatype", 0))) for d in entry.get("drawingLayers", [])
        )
        layers.append(
            StackupLayer(
                kind=entry.get("kind", ""),
                name=entry.get("name", ""),
                thickness=int(entry.get("thickness", 0)),
                epsr=entry.get("epsr"),
                tand=entry.get("tand"),
                mur=entry.get("mur"),
                sigma=entry.get("sigma"),
                is_ground_reference=bool(entry.get("isGroundReference", False)),
                drawing_layers=tuple(drawing),
            )
        )
    return Stackup(raw.get("top", "open"), raw.get("bottom", "ground"), layers)
