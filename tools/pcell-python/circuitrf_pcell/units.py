"""Database units, and the one rounding rule.

**There are no metres anywhere in this package, and that is the point** (schema §1). Every length a
generator receives is already in database units, converted host-side by circuitRF's own
``PCellUnits.MetresToDbu``. The resolution is deliberately never sent, so a script *cannot* do its
own metre conversion — there is nothing to do it with. That is what keeps R7's single rounding rule
true across the process boundary.

What a script does need is a rule for rounding its own arithmetic, and this module owns it.
"""

from __future__ import annotations

import math


def dbu(value: float | int) -> int:
    """Round a computed length to a whole database unit, half away from zero.

    **Use this rather than ``round()`` or ``int()``.** Python's built-in ``round`` is banker's
    rounding — ``round(0.5)`` is ``0`` and ``round(1.5)`` is ``2`` — which disagrees with
    circuitRF's own rule at exactly the midpoints where two adjacent shapes decide whether they
    abut. ``int()`` truncates, which is a different disagreement in the same place.

    Half-away-from-zero is the rule ``LayoutUnits.ToDbu`` already applies on the C# side, so a
    coordinate computed here lands where a coordinate computed there would.
    """
    if isinstance(value, int):
        return value
    if not math.isfinite(value):
        raise ValueError(f"A coordinate must be finite; got {value!r}.")
    return int(math.floor(value + 0.5)) if value >= 0 else int(math.ceil(value - 0.5))


def coord(value: float | int) -> int:
    """Validate one coordinate on its way onto the wire.

    Accepts an ``int``, or a ``float`` that is exactly integral (which is unambiguous). **Refuses a
    fractional float by name**, pointing at :func:`dbu` — because silently rounding it here would be
    this package inventing a rounding rule the author never saw, which is the failure schema §1
    exists to prevent. Being made to write ``dbu(w / 2)`` is the point, not an inconvenience.
    """
    if isinstance(value, bool):
        # bool is a subclass of int in Python; a flag is not a coordinate.
        raise TypeError("A coordinate must be a number, not a bool.")
    if isinstance(value, int):
        return value
    if isinstance(value, float):
        if not math.isfinite(value):
            raise ValueError(f"A coordinate must be finite; got {value!r}.")
        if value.is_integer():
            return int(value)
        raise ValueError(
            f"{value!r} is not a whole database unit. Round it explicitly with dbu(...) — this "
            "package will not choose a rounding rule on your behalf."
        )
    raise TypeError(f"A coordinate must be a number; got {type(value).__name__}.")
