"""Kinded parameter values — schema §3.

The JSON encoding is the same one circuitRF's own ``.clay`` files use, deliberately: one encoding
for the file and the wire means a value cannot mean two things in the two places.

    Real   -> a bare number      0.05
    Bool   -> true / false
    String -> a bare string      "nch_lvt"
    Int    -> {"int": 4}

Int is tagged because JSON has a single number token, and Int and Real are different inputs to the
content hash that names a generated cell — an Int written bare would come back a Real and resolve to
a different cell.
"""

from __future__ import annotations

from typing import Any, Mapping


def decode(raw: Any) -> Any:
    """Decode one wire value to a Python value.

    **``bool`` is checked before ``int``, and that ordering is load-bearing**: in Python ``bool`` is
    a subclass of ``int``, so ``isinstance(True, int)`` is ``True``. Checked the other way round,
    every flag would arrive as the integer 1 and every ``if guard:`` would still pass — a bug that
    only shows when someone writes ``guard == 1`` and it works, or ``guard is True`` and it does not.
    """
    if isinstance(raw, bool):
        return raw
    if isinstance(raw, (int, float)):
        return raw
    if isinstance(raw, str):
        return raw
    if isinstance(raw, Mapping) and "int" in raw:
        return int(raw["int"])
    raise ValueError(f"Unrecognised parameter value {raw!r}.")


def encode(value: Any) -> Any:
    """Encode one Python value for the wire. See :func:`decode` for the bool-before-int rule."""
    if isinstance(value, bool):
        return value
    if isinstance(value, int):
        return {"int": value}
    if isinstance(value, float):
        return value
    if isinstance(value, str):
        return value
    raise TypeError(f"Cannot put {type(value).__name__} on the wire as a parameter value.")


class Parameters:
    """The resolved parameters for one ``generate`` call.

    Every accessor names the kind it expects, so a generator states what it believes a parameter is
    at the point it reads it. A missing name falls back to the supplied default rather than raising —
    a cell that gains a parameter should keep working against a host that has not been told about it
    yet.
    """

    def __init__(self, raw: Mapping[str, Any]):
        self._values = {name: decode(v) for name, v in raw.items()}
        #: Every name any accessor was ASKED for during this generation, whether or not the
        #: parameter existed. What a generator reads is the only honest statement of what it uses:
        #: a declaration says what a cell offers, this says what it acted on. See
        #: ``cni.bridge`` for the one inference built on it.
        self.read: set[str] = set()

    def __contains__(self, name: str) -> bool:
        # NOT recorded as a read. A cell that merely asks whether a parameter is present has not
        # used its value, and counting it would make every "if 'x' in params" look like a use.
        return name in self._values

    def __repr__(self) -> str:  # pragma: no cover - diagnostics only
        return f"Parameters({self._values!r})"

    def with_defaults(self, defaults: Mapping[str, Any]) -> "Parameters":
        """This set, with anything the host did not send filled in from ``defaults``.

        The host sends the values it holds and no others, so a parameter left at its declared default
        may simply be absent. A generator never notices — it passes its own fallback to each
        accessor — but anything reading the parameters WITHOUT knowing what each one should fall back
        to (a derived-value calculator, say) sees a zero where the cell sees a width. The defaults
        are already declared; this applies them.

        Values are taken as-is, not re-decoded: a declaration's defaults are Python values on this
        side of the wire, having never been encoded.
        """
        merged = Parameters({})
        merged._values = {**dict(defaults), **self._values}
        return merged

    def length(self, name: str, default: int = 0) -> int:
        """A length, **already in database units** — never metres (schema §1)."""
        self.read.add(name)
        value = self._values.get(name)
        if value is None or isinstance(value, str):
            return default
        return int(value)

    def real(self, name: str, default: float = 0.0) -> float:
        """A continuous quantity — an angle in degrees, a ratio, an impedance in ohms."""
        self.read.add(name)
        value = self._values.get(name)
        if value is None or isinstance(value, str):
            return default
        return float(value)

    def integer(self, name: str, default: int = 0) -> int:
        """A count or an index."""
        self.read.add(name)
        value = self._values.get(name)
        if value is None or isinstance(value, str):
            return default
        return int(value)

    def flag(self, name: str, default: bool = False) -> bool:
        self.read.add(name)
        value = self._values.get(name)
        if value is None:
            return default
        if isinstance(value, str):
            return default
        return bool(value)

    def text(self, name: str, default: str = "") -> str:
        """A model name, a mode word — anything the cell selects by name rather than by number."""
        self.read.add(name)
        value = self._values.get(name)
        return value if isinstance(value, str) else default
