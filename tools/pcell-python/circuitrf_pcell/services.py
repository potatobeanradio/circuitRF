"""Work a generator asks circuitRF to do for it — the script side of schema §8.

Everywhere else in this package the host asks and the script answers. Here it is the other way
round, for one reason: **layer booleans must have exactly one implementation, and it is not this
one.** circuitRF already clips with Clipper2, over the same int64 database units these functions
speak. A second clipper on this side of the pipe would be two implementations of one rule whose
disagreement is invisible — a result off by a database unit renders perfectly and is wrong. So the
script asks rather than computes.

Nothing here is reachable unless :func:`circuitrf_pcell.run` is serving, which is deliberate: a
generator run outside a host has no circuitRF to ask, and saying so plainly is better than quietly
substituting a different answer.
"""

from __future__ import annotations

import json
from typing import Iterable, Sequence

from .wire import read_frame, write_frame

__all__ = ["Ring", "ClippedPolygon", "clip", "offset", "HostUnavailable", "HostRefused",
           "channel", "set_channel"]

#: A closed ring of integer database-unit coordinates.
Ring = Sequence[tuple[int, int]]


class HostUnavailable(Exception):
    """Asked circuitRF for something while not connected to it."""


class HostRefused(Exception):
    """circuitRF was asked and declined, with a reason."""


class ClippedPolygon:
    """One region of a boolean result: an outer ring and the rings that are holes in it.

    An island sitting inside a hole is a separate :class:`ClippedPolygon` in its own right rather
    than a further nesting level — the same flattening circuitRF applies to its own results, so that
    what a script sees and what the layout stores describe the same thing.
    """

    __slots__ = ("outer", "holes")

    def __init__(self, outer: Ring, holes: Sequence[Ring] = ()):
        self.outer = list(outer)
        self.holes = [list(h) for h in holes]

    def __repr__(self) -> str:
        return f"ClippedPolygon({len(self.outer)} pts, {len(self.holes)} holes)"


class _Channel:
    """The open conversation with circuitRF, borrowed from the run loop.

    It is the SAME pair of pipes the run loop reads and writes; there is no second connection and no
    concurrency. A service call happens strictly inside the handling of a ``generate``, so the host
    is blocked reading, and it answers before anything else can arrive.
    """

    __slots__ = ("_out", "_in")

    def __init__(self, out, inp):
        self._out, self._in = out, inp

    def call(self, body: dict, payload: Sequence[int] = ()) -> tuple[dict, list[int]]:
        write_frame(self._out, json.dumps(body), payload)
        json_text, reply_payload = read_frame(self._in)
        reply = json.loads(json_text)
        if not reply.get("ok", False):
            raise HostRefused(str(reply.get("error") or "circuitRF declined, without saying why."))
        return reply, reply_payload


_channel: "_Channel | None" = None


def channel() -> "_Channel | None":
    return _channel


def set_channel(out, inp) -> None:
    """Installed by :func:`circuitrf_pcell.run`. Not part of a generator's surface."""
    global _channel
    _channel = _Channel(out, inp) if out is not None and inp is not None else None


# ── clip ─────────────────────────────────────────────────────────────────────

_RULES = ("and", "or", "not", "xor")


def clip(rule: str, subject: Iterable[Ring], clip_rings: Iterable[Ring] = ()) -> list[ClippedPolygon]:
    """Ask circuitRF to perform a layer boolean.

    ``rule`` is one of ``and``, ``or``, ``not``, ``xor``. Both operands are sequences of closed
    rings in DATABASE UNITS — the units everything on this wire uses, so no conversion happens here
    and none is hidden.

    Each operand's rings are combined as a plain union regardless of the winding they were given in;
    a ring cannot itself carry a hole. That is the whole input domain a figure list represents, and
    a shape that needs a hole is built out of the result rather than fed in as one.
    """
    if rule not in _RULES:
        raise ValueError(f"Unknown clip rule {rule!r}. It is one of: {', '.join(_RULES)}.")

    conn = _channel
    if conn is None:
        raise HostUnavailable(
            "A layer boolean needs circuitRF to perform it, and this generator is not connected to "
            "one. circuitRF clips with its own library so that script results and layout results "
            "cannot disagree; there is deliberately no fallback that computes a different answer."
        )

    payload: list[int] = []
    subject_counts = _pack(subject, payload)
    clip_counts = _pack(clip_rings, payload)

    reply, coords = conn.call(
        {"op": "clip", "rule": rule, "subject": subject_counts, "clip": clip_counts},
        payload,
    )

    return _unpack_regions(reply, coords)


def offset(delta_dbu: int, rings: Iterable[Ring]) -> list[ClippedPolygon]:
    """Ask circuitRF to grow (positive) or shrink (negative) a region, in DATABASE UNITS.

    Same reasoning as :func:`clip`: circuitRF already offsets with Clipper2, and its editor's own
    Offset command must agree with a generator's grow to the database unit. Two implementations would
    disagree invisibly — a boundary off by one renders perfectly and is wrong.

    A shrink that consumes the region entirely returns an EMPTY list. That is a legitimate answer, not
    a failure.
    """
    conn = _channel
    if conn is None:
        raise HostUnavailable(
            "Growing or shrinking a region needs circuitRF to perform it, and this generator is not "
            "connected to one. circuitRF offsets with its own library so that script results and "
            "layout results cannot disagree; there is deliberately no fallback."
        )

    payload: list[int] = []
    counts = _pack(rings, payload)

    reply, coords = conn.call(
        {"op": "offset", "deltaDbu": int(delta_dbu), "subject": counts}, payload)
    return _unpack_regions(reply, coords)


def _unpack_regions(reply: dict, coords: Sequence[int]) -> list[ClippedPolygon]:
    """Both operations answer with regions-and-their-holes, so both read them the same way."""
    out: list[ClippedPolygon] = []
    at = 0
    for described in reply.get("polygons") or []:
        outer, at = _unpack(coords, at, int(described.get("outer", 0)))
        holes = []
        for count in described.get("holes") or []:
            ring, at = _unpack(coords, at, int(count))
            holes.append(ring)
        out.append(ClippedPolygon(outer, holes))

    if at != len(coords):
        raise HostRefused(
            f"circuitRF described {at} coordinates but sent {len(coords)}. The stream is out of step."
        )
    return out


def _pack(rings: Iterable[Ring], payload: list[int]) -> list[int]:
    counts: list[int] = []
    for ring in rings:
        n = 0
        for x, y in ring:
            payload.append(int(x))
            payload.append(int(y))
            n += 1
        counts.append(n)
    return counts


def _unpack(coords: Sequence[int], at: int, count: int) -> tuple[list[tuple[int, int]], int]:
    end = at + count * 2
    if end > len(coords):
        raise HostRefused("A clip reply ran past the end of its payload. The stream is out of step.")
    return [(coords[i], coords[i + 1]) for i in range(at, end, 2)], end
