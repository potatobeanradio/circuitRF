"""circuitRF's own implementation of the PCell API that open process kits are written against.

**Why this exists.** A kit's parametric device cells are Python written against an abstract API —
``from cni.dlo import *`` — not against any particular layout tool. Measured: 33 device
generators, and *not one of them* imports the tool's own module. Only the kit's registration glue
does, and circuitRF replaces that anyway.

So supplying this API ourselves means the library's own cells run, unmodified, with **nothing for the
user to install** beyond a Python interpreter. That is the whole point: the alternative is asking
every user to install two more third-party components, one of them GPL, before their kit's devices
will draw.

**This is a re-implementation from the observable API surface, not a port.** Nothing here is derived
from any GPL implementation of the same interface — the shape of the API is dictated by the kit's own
Apache-licensed cells, which is what was read.

**Units.** The kit's cells compute in MICROMETRES throughout, in floating point. circuitRF's
coordinates are integer database units. The conversion happens once, at the boundary in
:mod:`cni.dlo`, using the resolution the host states (wire version 2) — never inside a cell, and
never twice.
"""

from . import dlo, geo, tech

__all__ = ["dlo", "geo", "tech"]
