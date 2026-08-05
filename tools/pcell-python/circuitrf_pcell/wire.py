"""Frame codec — the script side of docs/design/pcell-wire-schema.md §2.

    [ uint32 jsonLen ][ uint32 binLen ][ jsonLen bytes UTF-8 ][ binLen bytes of int64 ]

All little-endian. ``binLen`` is a BYTE count, not an element count.

This is deliberately an INDEPENDENT implementation of the format, not a port of circuitRF's own
codec: a second copy of one implementation agreeing with itself proves nothing about the format,
which is the whole reason the package is written against the specification instead.
"""

from __future__ import annotations

import struct
from typing import BinaryIO, Sequence

_HEADER = struct.Struct("<II")

#: Refuses a frame claiming more than this. Beyond any plausible cell, an announced length is a
#: desynchronised stream — and believing it means allocating gigabytes on a corrupt number instead
#: of reporting the desync.
MAX_FRAME_BYTES = 256 * 1024 * 1024


class WireError(Exception):
    """A malformed frame, a desynchronised stream, or a message this build cannot read."""


def read_exactly(stream: BinaryIO, count: int) -> bytes:
    """Read exactly ``count`` bytes, or raise.

    A partial read on a pipe is normal and must be LOOPED — treating one as end-of-stream produces
    frames that decode as garbage only under load. This is the single subtlest thing in the codec,
    and it is why ``stream.read(n)`` is not called directly anywhere else in this package.
    """
    chunks: list[bytes] = []
    remaining = count
    while remaining > 0:
        chunk = stream.read(remaining)
        if not chunk:
            raise WireError(
                f"The stream ended after {count - remaining} of {count} expected bytes."
            )
        chunks.append(chunk)
        remaining -= len(chunk)
    return b"".join(chunks)


def read_frame(stream: BinaryIO) -> tuple[str, list[int]]:
    """Read one frame. Returns ``(json_text, payload)`` with the payload as int64 values."""
    header = stream.read(_HEADER.size)
    if not header:
        raise EOFError("stream closed")
    if len(header) < _HEADER.size:
        header += read_exactly(stream, _HEADER.size - len(header))

    json_len, bin_len = _HEADER.unpack(header)

    if json_len == 0:
        raise WireError("A frame arrived with no control-plane object; the stream is not in step.")
    if json_len > MAX_FRAME_BYTES or bin_len > MAX_FRAME_BYTES:
        raise WireError(
            f"A frame announced an implausible size ({json_len} + {bin_len} bytes). "
            "The stream is out of step — this is a desynchronisation, not a large cell."
        )
    if bin_len % 8 != 0:
        raise WireError(
            f"A frame announced {bin_len} payload bytes, which is not a whole number of coordinates. "
            "The stream is out of step."
        )

    json_text = read_exactly(stream, json_len).decode("utf-8")
    payload: list[int] = []
    if bin_len:
        raw = read_exactly(stream, bin_len)
        payload = list(struct.unpack(f"<{bin_len // 8}q", raw))

    return json_text, payload


def write_frame(stream: BinaryIO, json_text: str, payload: Sequence[int] = ()) -> None:
    """Write one frame and flush.

    Flushing is not optional: the host is blocked reading our reply, so a buffered frame is a
    deadlock rather than a delay.
    """
    if not json_text:
        raise WireError("Every frame must carry a control-plane object.")

    json_bytes = json_text.encode("utf-8")
    body = struct.pack(f"<{len(payload)}q", *payload) if payload else b""

    if len(json_bytes) > MAX_FRAME_BYTES or len(body) > MAX_FRAME_BYTES:
        raise WireError("Refusing to send a frame beyond any plausible cell.")

    stream.write(_HEADER.pack(len(json_bytes), len(body)))
    stream.write(json_bytes)
    if body:
        stream.write(body)
    stream.flush()
