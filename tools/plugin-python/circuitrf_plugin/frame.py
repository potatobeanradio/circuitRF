"""The frame - the plugin side of docs/design/plugins.md section 6.1, which is the PCell wire's frame:

    [ uint32 jsonLen ][ uint32 binLen ][ jsonLen bytes UTF-8 ][ binLen bytes of int64 ]

All little-endian. ``binLen`` is a BYTE count, not an element count.

A copy of ``tools/pcell-python/circuitrf_pcell/wire.py``, not an import of it: this package is copied into a plugin
on its own, so it may depend on nothing else. The tests in ``tests/test_frame.py`` are that file's claims, made again.
"""

from __future__ import annotations

import struct
from typing import BinaryIO, Sequence

_HEADER = struct.Struct("<II")

#: Refuses a frame claiming more than this. Beyond any plausible reply, an announced length is a
#: desynchronised stream — and believing it means allocating gigabytes on a corrupt number instead
#: of reporting the desync.
MAX_FRAME_BYTES = 256 * 1024 * 1024


class FrameError(Exception):
    """A malformed frame or a desynchronised stream."""


def read_exactly(stream: BinaryIO, count: int) -> bytes:
    """Read exactly ``count`` bytes, or raise.

    A partial read on a pipe is normal and must be LOOPED — treating one as end-of-stream produces
    frames that decode as garbage only under load. This is the single subtlest thing in the codec.
    """
    chunks: list[bytes] = []
    remaining = count
    while remaining > 0:
        chunk = stream.read(remaining)
        if not chunk:
            raise FrameError(
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
        raise FrameError("A frame arrived with no JSON object; the stream is not in step.")
    if json_len > MAX_FRAME_BYTES or bin_len > MAX_FRAME_BYTES:
        raise FrameError(
            f"A frame announced an implausible size ({json_len} + {bin_len} bytes). "
            "The stream is out of step — this is a desynchronisation, not a large reply."
        )
    if bin_len % 8 != 0:
        raise FrameError(
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

    Flushing is not optional: circuitRF is waiting for the reply, so a buffered frame is a
    deadlock rather than a delay.
    """
    if not json_text:
        raise FrameError("Every frame must carry a JSON object.")

    json_bytes = json_text.encode("utf-8")
    body = struct.pack(f"<{len(payload)}q", *payload) if payload else b""

    if len(json_bytes) > MAX_FRAME_BYTES or len(body) > MAX_FRAME_BYTES:
        raise FrameError("Refusing to send a frame beyond any plausible reply.")

    stream.write(_HEADER.pack(len(json_bytes), len(body)))
    stream.write(json_bytes)
    if body:
        stream.write(body)
    stream.flush()
