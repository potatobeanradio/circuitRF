"""The kit's own process data, read as plain JSON.

A kit ships its ~900 process constants — layer enclosures, minimum widths, via sizes — in a JSON file
beside its cells. That is data, not code, and reading it needs nothing installed. This is the single
biggest reason the whole compatibility layer is possible.
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

from .dlo import Tech, TechImpl

__all__ = ["Tech", "TechImpl", "load_tech"]


def load_tech(path: str | Path) -> Tech:
    """Read a kit's process-parameter file.

    Accepts the two shapes seen in the wild: the parameters at the top level, or under a
    ``techParams`` key alongside a layer table. A grid resolution stated by the kit is used —
    the kit is the authority on its own manufacturing grid, and guessing one would put every
    rounded coordinate slightly off.
    """
    raw: Any = json.loads(Path(path).read_text(encoding="utf-8"))
    params = raw.get("techParams", raw) if isinstance(raw, dict) else {}
    grid = params.get("grid", 0.001)
    try:
        grid = float(grid)
    except (TypeError, ValueError):
        grid = 0.001
    return Tech(params, grid)
