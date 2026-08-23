"""The run loop: declare generators, answer ``describe`` / ``generate`` / ``shutdown``.

A cell author writes generators and calls :func:`run`. Everything below the ``@generator``
decorator is this package's problem.
"""

from __future__ import annotations

import json
import sys
import traceback
from dataclasses import dataclass
from typing import Any, BinaryIO, Callable, Sequence

from .geometry import AUTO, Handle, Pin, Result, Shape
from .tech import Technology
from .values import Parameters, encode
from .services import set_channel
from .wire import read_frame, write_frame

#: Wire version 7 added the EDITOR HINTS a parameter declaration may carry — ``label``, ``choices``,
#: ``minimum``/``maximum``, ``computed`` — plus ``computed``/``computedValues`` on a generate reply.
#: None of it changes what a generator receives; all of it changes what circuitRF's parameter dialog
#: can put on screen. A vendor kit already states most of it (a CDF ``ChoiceConstraint`` is a
#: dropdown, a two-valued Yes/No one is a checkbox) and circuitRF used to discard every bit of it,
#: rendering fourteen distinct kinds of parameter as one identical free-text box.
#:
#: Wire version 6 (2026-08-06) added optional ``handles`` to the generate reply: draggable parameter
#: grips, so a placed cell can be edited by dragging its artwork rather than only by typing numbers.
#: Purely additive — a generator that declares none behaves exactly as before — but the bump is still
#: required, because ``describe`` compares versions for equality and refuses rather than negotiating.
#: ``CONTRACT_VERSION`` is untouched: a generator's signature has not changed, only what it may
#: optionally include in its result.
#:
#: Wire version 5 (2026-08-04) added an ``offset`` service op — grow/shrink, asked of the host for the
#: same reason the booleans are: it is Clipper2 offset, which circuitRF already owns.
#:
#: Wire version 4 (2026-08-04) added a declared DEFAULT to each parameter, so circuitRF can PLACE a
#: generator's cell without being told its parameters — a placed cell is then editable rather than
#: frozen at whatever the script fell back to. Declared, never inferred: a default the host guessed
#: would be a value the generator never sanctioned.
#:
#: Wire version 3 (2026-08-04) let a frame travel the OTHER WAY: a generator may ask circuitRF to
#: perform a layer boolean mid-generate rather than implementing a second clipper here. Nothing
#: already on the wire changed shape — see ``circuitrf_pcell.services`` and schema §8.
#:
#: Wire version 2 added ``dbuPerMicron`` to the generate request, reachable as
#: :attr:`Technology.dbu_per_micron`. It rides on the technology object rather than becoming a third
#: argument to every generator: a new parameter would be a CONTRACT change and would break every
#: generator ever written, while an extra attribute breaks none. The two versions move independently
#: for exactly this reason — see ``docs/design/pcell-wire-schema.md`` §7.
WIRE_VERSION = 7
CONTRACT_VERSION = 2

#: Dimensions the wire understands. Deliberately three: length and angle are the only ones that
#: change how a value crosses (length -> DBU, angle -> degrees), and declaring anything else would
#: ask for a unit-aware conversion the wire does not perform.
LENGTH = "length"
ANGLE = "angle"
NONE = "none"


@dataclass(frozen=True)
class Parameter:
    """One parameter a generator declares.

    **The dimension is not documentation — it decides what arrives.** circuitRF converts a
    ``length`` from SI metres to database units before sending it, using its own single rounding
    rule. A length you forget to declare arrives unconverted and your geometry is off by nine orders
    of magnitude; a non-length you declare as one is silently scaled. This is the one declaration
    worth checking twice.
    """

    name: str
    kind: str = "real"
    dimension: str = NONE
    #: What this parameter is when circuitRF sends nothing for it. ``None`` states none.
    #:
    #: **A length default is stated in DATABASE UNITS**, like every other length on this wire — the
    #: host does not convert it, because there is nothing to convert from (schema §1: no metres).
    default: Any = None

    #: What to CALL this parameter on screen, when its name is not what a human would call it. The
    #: name stays the identifier; this is only ever displayed.
    label: str | None = None

    #: The values this parameter may take. States that the editor is a CHOICE — a dropdown, or a
    #: checkbox when there are exactly two and they read as a yes/no pair. Empty/``None`` states
    #: nothing and leaves the parameter free-form, which is what every generator written before
    #: version 7 says by omission.
    #:
    #: **Advisory, not enforced.** The host offers these and nothing more, but a value that arrives
    #: from an older design or a hand-edited file is still passed through — a generator that needs a
    #: choice to be binding validates it itself, because it is the only side that knows what an
    #: out-of-range value should do.
    choices: tuple[Any, ...] | None = None

    #: Bounds for a numeric parameter, either or both. Advisory in exactly the same sense.
    minimum: float | None = None
    maximum: float | None = None

    #: This parameter is an OUTPUT: the generator derives it and never reads it. circuitRF renders
    #: it as selectable text rather than an edit box, because typing into it cannot do anything.
    #:
    #: A generator that computes such a value should also report it per-run — see
    #: :attr:`Result.computed` — otherwise the host can only show the last value it was given.
    computed: bool = False

    @staticmethod
    def length(name: str, default: Any = None) -> "Parameter":
        """A width, a length, a radius — arrives in DATABASE UNITS, already converted."""
        return Parameter(name, "real", LENGTH, default)

    @staticmethod
    def angle(name: str, default: Any = None) -> "Parameter":
        """Arrives in DEGREES, not radians."""
        return Parameter(name, "real", ANGLE, default)

    @staticmethod
    def real(name: str, default: Any = None) -> "Parameter":
        """A ratio, an impedance in ohms — anything continuous that is not a length."""
        return Parameter(name, "real", NONE, default)

    @staticmethod
    def integer(name: str, default: Any = None) -> "Parameter":
        """A count: fingers, turns, segments."""
        return Parameter(name, "int", NONE, default)

    @staticmethod
    def flag(name: str, default: Any = None) -> "Parameter":
        return Parameter(name, "bool", NONE, default)

    @staticmethod
    def text(name: str, default: Any = None) -> "Parameter":
        """A model name, a mode word."""
        return Parameter(name, "string", NONE, default)

    def to_json(self) -> dict[str, Any]:
        body: dict[str, Any] = {"name": self.name, "kind": self.kind, "dimension": self.dimension}
        if self.default is not None:
            # Encoded exactly as any other value crosses (§3), so a default and an actual value are
            # the same bytes — a second encoding here could disagree with the one that matters.
            body["default"] = encode(self.default)
        if self.label:
            body["label"] = str(self.label)
        if self.choices:
            # Through the SAME encoder as a value, for the same reason the default is: a choice is
            # compared against the parameter's current value, and two encodings that can disagree
            # would make a legitimate choice fail to match the value it is identical to.
            body["choices"] = [encode(c) for c in self.choices]
        if self.minimum is not None:
            body["minimum"] = float(self.minimum)
        if self.maximum is not None:
            body["maximum"] = float(self.maximum)
        if self.computed:
            body["computed"] = True
        return body


GeneratorFn = Callable[[Parameters, Technology], Result]


class Registry:
    """The generators one script offers. Usually there is one module-level instance; see
    :func:`generator` and :func:`run`."""

    def __init__(self) -> None:
        self._generators: dict[str, tuple[Sequence[Parameter], GeneratorFn]] = {}

    def add(self, generator_id: str, parameters: Sequence[Parameter], fn: GeneratorFn) -> None:
        if generator_id in self._generators:
            raise ValueError(
                f"'{generator_id}' is declared twice. Two generators under one id is not a "
                "preference the host can resolve — the second would silently win."
            )
        self._generators[generator_id] = (tuple(parameters), fn)

    def ids(self) -> list[str]:
        return list(self._generators)

    def declaration(self) -> list[dict[str, Any]]:
        return [
            {"id": gid, "parameters": [p.to_json() for p in params]}
            for gid, (params, _) in self._generators.items()
        ]

    def declared_defaults(self, generator_id: str) -> dict[str, Any]:
        """What one generator's parameters fall back to when the host sends nothing for them."""
        entry = self._generators.get(generator_id)
        if entry is None:
            return {}
        return {p.name: p.default for p in entry[0] if p.default is not None}

    def decorate(self, generator_id: str, wrap: Callable[[GeneratorFn], GeneratorFn]) -> None:
        """Replace one registered generator with ``wrap(original)``, keeping its declaration.

        For adding behaviour AROUND a generator somebody else registered — which is the situation a
        vendor kit puts you in, since its cells are discovered from its own package and you never
        write the registration call. See :func:`reports_computed`.
        """
        entry = self._generators.get(generator_id)
        if entry is None:
            known = ", ".join(sorted(self._generators)) or "(none)"
            raise KeyError(f"No generator '{generator_id}' to decorate. Registered: {known}.")
        params, fn = entry
        self._generators[generator_id] = (params, wrap(fn))

    def invoke(self, generator_id: str, params: Parameters, tech: Technology) -> Result:
        entry = self._generators.get(generator_id)
        if entry is None:
            known = ", ".join(sorted(self._generators)) or "(none)"
            raise KeyError(f"No generator '{generator_id}' in this script. It offers: {known}.")
        return entry[1](params, tech)


#: The registry :func:`generator` and :func:`run` use when none is supplied.
default_registry = Registry()


def reports_computed(generator_id: str,
                     compute: Callable[[Parameters, Technology], dict[str, Any]],
                     registry: "Registry | None" = None) -> None:
    """Have an already-registered generator also report what it DERIVES — see :attr:`Result.computed`.

    **Why this is a hook and not something the host can work out.** circuitRF can tell that a
    parameter is an output — a generator says so, or a kit's cell demonstrably never reads it — and
    that alone is enough to stop offering an edit box for it. What it cannot do is produce the
    *number*. A derived value is a function only the cell knows, and where the cell does not compute
    it either (a vendor library whose dialog callback did the arithmetic, leaving the layout code
    reading only w and l) there is nothing anywhere to read. Reading an attribute back off the cell
    is not a substitute: one open kit sets ``self.w`` to the micrometre value of a parameter whose own
    text is ``'6.99u'``, so an attribute that matches a parameter's name need not hold its value.

    ``compute`` is handed the same parameters the generator got and returns ``{name: value}``. It is
    called AFTER the generator, and only its own failure is swallowed — a derived readout is worth
    less than the geometry, and must never be what stops a cell being placed.

    Nothing here declares the parameter to BE an output; that is a separate statement
    (``Parameter.computed``, or the host measuring it). A value reported for a parameter nobody named
    as derived is dropped by the host.
    """
    reg = registry or default_registry
    # Captured at decoration time: the declaration does not change afterwards, and reading it per
    # generate would put a dictionary rebuild on the hot path for a value that cannot have moved.
    defaults = reg.declared_defaults(generator_id)

    def wrap(fn: GeneratorFn) -> GeneratorFn:
        def generate(params: Parameters, tech: Technology) -> Result:
            result = fn(params, tech)
            try:
                # WITH the declared defaults applied. The generator itself passes a fallback to every
                # accessor and so never sees the gap; a calculator standing outside the cell has no
                # way to know what each parameter should fall back to, and would read 0 for every
                # parameter the host happened to leave at its default.
                derived = compute(params.with_defaults(defaults), tech)
            except Exception as exc:  # noqa: BLE001 - see the docstring
                print(f"{generator_id}: could not compute its derived values: {exc!r}",
                      file=sys.stderr)
                return result
            for name, value in (derived or {}).items():
                # A generator that already stated a VALUE knows more about it than a bolt-on
                # calculator does and keeps it. A ``None`` is not a value — it is the claim "this is
                # derived and I cannot tell you to what", which is exactly the gap this fills, so it
                # is replaced. (Plain setdefault gets this wrong: the host measures a kit's outputs
                # by running its cells, so every name this is called for is ALREADY present as None
                # by the time the wrapper sees it, and every value would be silently dropped.)
                if result.computed.get(str(name)) is None:
                    result.computed[str(name)] = value
            return result
        return generate

    reg.decorate(generator_id, wrap)


def generator(generator_id: str, parameters: Sequence[Parameter] = (),
              registry: Registry | None = None):
    """Declare a generator.

        @generator("SPIRAL", [Parameter.length("W"), Parameter.integer("Turns")])
        def spiral(params, tech):
            ...
            return Result(shapes=[...], pins=[...])
    """

    def decorate(fn: GeneratorFn) -> GeneratorFn:
        (registry or default_registry).add(generator_id, parameters, fn)
        return fn

    return decorate


# ── Serving ──────────────────────────────────────────────────────────────────


def _encode_result(result: Result) -> tuple[str, list[int]]:
    payload: list[int] = []
    body: dict[str, Any] = {
        "ok": True,
        "shapes": [s.to_json(payload) for s in _as_shapes(result.shapes)],
        "pins": [
            {
                "name": p.name,
                "x": p.x,
                "y": p.y,
                "layer": p.layer.to_json(),
                "width": p.width,
                "outwardDeg": float(p.outward_deg),
            }
            for p in result.pins
        ],
    }
    if result.diagnostics:
        body["diagnostics"] = list(result.diagnostics)
    if result.handles:
        body["handles"] = [h.to_json(payload) for h in _as_handles(result.handles)]
    if result.preview != AUTO:
        body["preview"] = result.preview
    if result.computed:
        # The NAMES and the VALUES are separate on purpose. A generator may know that a parameter is
        # derived without being able to state what it derived to (nothing in the cell computes it —
        # only the vendor's dialog ever did), and that is still worth saying: circuitRF stops
        # offering an edit box for something typing into cannot change. ``None`` is exactly that
        # claim, so it names the parameter and contributes no value.
        body["computed"] = [str(k) for k in result.computed]
        values = {str(k): encode(v) for k, v in result.computed.items() if v is not None}
        if values:
            body["computedValues"] = values
    return json.dumps(body), payload


def _as_shapes(shapes: Sequence[Any]) -> Sequence[Shape]:
    for s in shapes:
        if not isinstance(s, Shape):
            raise TypeError(
                f"A generator returned {type(s).__name__} where a shape was expected. Use the "
                "constructors in circuitrf_pcell (Rect, Polygon, Path, ...)."
            )
    return shapes


def _as_handles(handles: Sequence[Any]) -> Sequence[Handle]:
    for h in handles:
        if not isinstance(h, Handle):
            raise TypeError(
                f"A generator returned {type(h).__name__} where a Handle was expected. Use "
                "circuitrf_pcell.Handle(parameter, anchor=..., at=..., axis=...)."
            )
    return handles


def _error(message: str) -> str:
    return json.dumps({"ok": False, "error": message})


def _describe(registry: Registry, request: dict[str, Any]) -> str:
    host_wire = int(request.get("wireVersion", 0))
    if host_wire != WIRE_VERSION:
        # Refused, never negotiated: negotiation means several code paths of which the rare ones are
        # wrong. Both numbers are named so the message says exactly what to update.
        return _error(
            f"This script speaks PCell wire version {WIRE_VERSION}; circuitRF asked for "
            f"{host_wire}. One of the two needs updating."
        )
    return json.dumps(
        {
            "ok": True,
            "wireVersion": WIRE_VERSION,
            "contractVersion": CONTRACT_VERSION,
            "generators": registry.declaration(),
        }
    )


def serve_one(registry: Registry, json_text: str) -> tuple[str, list[int]] | None:
    """Handle one request. Returns the reply, or ``None`` for ``shutdown``.

    Split out from :func:`run` so the whole dispatch is testable without a pipe.
    """
    try:
        request = json.loads(json_text)
    except json.JSONDecodeError as exc:
        return _error(f"circuitRF sent malformed JSON: {exc}"), []

    op = request.get("op")

    if op == "shutdown":
        return None
    if op == "describe":
        return _describe(registry, request), []
    if op != "generate":
        return _error(f"Unknown command '{op}'."), []

    try:
        params = Parameters(request.get("parameters") or {})
        tech = Technology(request)
        result = registry.invoke(str(request.get("generatorId", "")), params, tech)
        if not isinstance(result, Result):
            raise TypeError(
                f"A generator returned {type(result).__name__}; it must return a Result."
            )
        return _encode_result(result)
    except Exception as exc:  # noqa: BLE001 - every failure is reported, never propagated
        # The traceback is included deliberately. A generator is somebody's own script and this is
        # the only view they get of it failing; a bare message would leave them guessing which line.
        detail = "".join(traceback.format_exception(type(exc), exc, exc.__traceback__)).strip()
        return _error(f"{type(exc).__name__}: {exc}\n{detail}"), []


def run(registry: Registry | None = None,
        stdin: BinaryIO | None = None, stdout: BinaryIO | None = None) -> None:
    """Serve requests until ``shutdown`` or end of stream.

    **stdout is the wire and must carry nothing else.** A stray ``print()`` in a generator lands in
    the middle of a frame and desynchronises the stream — which presents as circuitRF reporting a
    malformed reply, nowhere near the print. Write to ``sys.stderr`` instead; circuitRF surfaces it.
    """
    registry = registry or default_registry
    inp = stdin if stdin is not None else sys.stdin.buffer
    out = stdout if stdout is not None else sys.stdout.buffer

    # The same two pipes, lent to :mod:`circuitrf_pcell.services` so a generator can ask circuitRF
    # for work mid-generate. There is no second connection: a service call happens strictly inside
    # the handling of one request, while the host is blocked reading our reply.
    set_channel(out, inp)

    while True:
        try:
            json_text, _ = read_frame(inp)
        except EOFError:
            return

        reply = serve_one(registry, json_text)
        if reply is None:
            return
        write_frame(out, reply[0], reply[1])
