#!/usr/bin/env python3
"""Self-test for the circuitrf_pcell package — no circuitRF, no .NET, no pytest.

Run it:  python3 tools/pcell-python/verify.py

This checks the package against the SPECIFICATION (docs/design/pcell-wire-schema.md). The C# side
has its own tests that drive this package as a real subprocess; the two together are what say the
format is right, because each was written from the spec rather than from the other.
"""

from __future__ import annotations

import io
import json
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from circuitrf_pcell import (  # noqa: E402
    Edge,
    WIRE_VERSION,
    Layer,
    Parameter,
    Parameters,
    Path as PathShape,
    Pin,
    Polygon,
    Rect,
    Registry,
    Result,
    Technology,
    coord,
    dbu,
    read_frame,
    serve_one,
    write_frame,
)

_failures: list[str] = []


def check(condition: bool, what: str) -> None:
    if condition:
        print(f"  ok   {what}")
    else:
        print(f"  FAIL {what}")
        _failures.append(what)


def raises(fn, what: str) -> None:
    try:
        fn()
    except Exception:  # noqa: BLE001
        print(f"  ok   {what}")
        return
    print(f"  FAIL {what} (no exception)")
    _failures.append(what)


# ── Rounding ─────────────────────────────────────────────────────────────────

print("rounding")
# Half away from zero, NOT Python's banker's rounding. round(0.5) is 0 and round(2.5) is 2 in
# Python; both would disagree with circuitRF at exactly the midpoints where shapes decide to abut.
check(dbu(0.5) == 1, "dbu(0.5) == 1 (round() would give 0)")
check(dbu(1.5) == 2, "dbu(1.5) == 2")
check(dbu(2.5) == 3, "dbu(2.5) == 3 (round() would give 2)")
check(dbu(-0.5) == -1, "dbu(-0.5) == -1 (away from zero)")
check(dbu(-2.5) == -3, "dbu(-2.5) == -3")
check(dbu(7) == 7, "dbu passes an int through")

print("coordinates")
check(coord(5) == 5, "an int is a coordinate")
check(coord(5.0) == 5, "an integral float is unambiguous and accepted")
raises(lambda: coord(5.5), "a fractional float is refused, naming dbu()")
raises(lambda: coord(True), "a bool is not a coordinate")

# ── Values ───────────────────────────────────────────────────────────────────

print("values")
params = Parameters({"W": 300000, "Turns": {"int": 4}, "Guard": True, "Model": "nch_lvt"})
check(params.length("W") == 300000, "a length arrives in DBU")
check(params.integer("Turns") == 4, "a tagged int decodes as an int")
check(params.flag("Guard") is True, "a bool decodes as a bool, not the integer 1")
check(params.text("Model") == "nch_lvt", "a string decodes as a string")
check(params.length("Absent", 42) == 42, "a missing name falls back to the default")
check(params.real("Model", 1.5) == 1.5, "a string is never coerced to a number")

# ── Frames ───────────────────────────────────────────────────────────────────

print("frames")
buf = io.BytesIO()
write_frame(buf, '{"op":"x"}', [1, -2, 3])
buf.seek(0)
text, payload = read_frame(buf)
check(text == '{"op":"x"}' and payload == [1, -2, 3], "a frame round-trips, negatives included")


class Dribble(io.RawIOBase):
    """One byte per read — what a pipe under load looks like."""

    def __init__(self, data: bytes):
        self._data, self._pos = data, 0

    def read(self, size=-1):  # noqa: D102
        if self._pos >= len(self._data):
            return b""
        out = self._data[self._pos : self._pos + 1]
        self._pos += 1
        return out


big = list(range(4096))
buf = io.BytesIO()
write_frame(buf, '{"op":"x"}', big)
text, payload = read_frame(Dribble(buf.getvalue()))
check(payload == big, "a payload delivered one byte at a time still decodes whole")

# ── Geometry ─────────────────────────────────────────────────────────────────

print("geometry")
payload = []
body = Rect(Layer(1, 0), 0, -150000, 2000000, 150000).to_json(payload)
check(payload == [0, -150000, 2000000, 150000], "rect coordinates go to the payload")
check(body["xy"] == {"at": 0, "count": 4}, "and the JSON carries only a span")
# The span's own "at"/"count" are of course numbers; what must be absent is any coordinate VALUE.
text = json.dumps(body)
check(
    all(str(v) not in text for v in (-150000, 2000000, 150000)),
    "no coordinate value appears in the JSON",
)

payload = []
poly = Polygon(Layer(1, 0), [0, 0, 10, 0, 10, 10], holes=[[2, 2, 4, 2, 4, 4]]).to_json(payload)
check(len(poly["holes"]) == 1 and payload[-6:] == [2, 2, 4, 2, 4, 4], "holes append after the ring")

payload = []
path = PathShape(
    Layer(1, 0), [0, 0, 10, 0], width=5, end="round",
    edges=[Edge.arc(0.41421356237309515), Edge.cubic(1, 2, 3, 4)],
).to_json(payload)
check(path["edges"][0]["bulge"] == 0.41421356237309515, "an arc crosses as a bulge, not as points")
check(path["edges"][1]["control"] == {"at": 4, "count": 4}, "a cubic's controls are a span")

raises(lambda: Polygon(Layer(1, 0), [0, 0]).to_json([]), "a one-point ring is refused")
raises(lambda: Rect(Layer(1, 0), 0, 0, 1.5, 1).to_json([]), "a fractional rect corner is refused")

# ── Dispatch ─────────────────────────────────────────────────────────────────

print("dispatch")
registry = Registry()


@ (lambda fn: registry.add("T", [Parameter.length("W")], fn) or fn)
def _t(params, tech):
    w = params.length("W")
    layer = tech.signal_layer or Layer(1, 0)
    return Result(shapes=[Rect(layer, 0, 0, w, w)], pins=[Pin("1", 0, 0, layer, w, 180.0)])


reply, _ = serve_one(registry, json.dumps({"op": "describe", "wireVersion": WIRE_VERSION}))
described = json.loads(reply)
check(described["ok"] and described["wireVersion"] == WIRE_VERSION, "describe answers with its version")
check(
    described["generators"][0]["parameters"][0]["dimension"] == "length",
    "describe declares each parameter's dimension — what lets the host convert",
)

reply, _ = serve_one(registry, json.dumps({"op": "describe", "wireVersion": 99}))
check(not json.loads(reply)["ok"], "a wire-version mismatch is refused, not negotiated")
check("99" in json.loads(reply)["error"], "and the refusal names both versions")

reply, payload = serve_one(
    registry,
    json.dumps({"op": "generate", "generatorId": "T", "parameters": {"W": 400},
                "layers": {"signal": {"layer": 3, "datatype": 0}}}),
)
generated = json.loads(reply)
check(generated["ok"] and payload == [0, 0, 400, 400], "generate returns geometry in the payload")
check(generated["shapes"][0]["layer"] == {"layer": 3, "datatype": 0}, "the resolved layer is used")

reply, _ = serve_one(registry, json.dumps({"op": "generate", "generatorId": "NOPE"}))
failed = json.loads(reply)
check(not failed["ok"] and "NOPE" in failed["error"], "an unknown generator is refused by name")

check(serve_one(registry, json.dumps({"op": "shutdown"})) is None, "shutdown ends the loop")


@ (lambda fn: registry.add("BOOM", [], fn) or fn)
def _boom(params, tech):
    raise ValueError("turns must be at least 1")


reply, _ = serve_one(registry, json.dumps({"op": "generate", "generatorId": "BOOM"}))
crashed = json.loads(reply)
check(not crashed["ok"], "an exception becomes a refusal, never a crashed process")
check("turns must be at least 1" in crashed["error"], "and carries the generator's own message")
check("Traceback" in crashed["error"], "with a traceback — this is the author's only view of it")

# ── Technology ───────────────────────────────────────────────────────────────

print("technology")
tech = Technology({
    "layers": {"signal": {"layer": 1, "datatype": 0},
               "table": [{"layer": 1, "datatype": 0, "name": "Metal1"}]},
    "stackup": {"top": "open", "bottom": "ground", "layers": [
        {"kind": "conductor", "name": "Metal1", "thickness": 35000, "sigma": 5.8e7},
        {"kind": "dielectric", "name": "FR-4", "thickness": 1600000, "epsr": 4.4, "tand": 0.02},
    ]},
})
check(tech.signal_layer == Layer(1, 0), "the resolved signal layer is given, not derived")
check(tech.layer_named("Metal1") == Layer(1, 0), "a layer can be looked up by name")
check(tech.stackup.conductors[0].thickness == 35000, "a stackup thickness is DBU, not metres")
check(tech.stackup.dielectrics[0].epsr == 4.4, "a dielectric carries epsr")
check(tech.stackup.dielectrics[0].sigma is None, "and no conductivity — absent, not defaulted")
check(Technology(None).signal_layer is None, "no technology still yields a usable object")

# Wire version 2: a generator carrying a process constant of its own (a dimension in micrometres out
# of a kit's data) needs the resolution to turn it into a coordinate. Length PARAMETERS are still
# converted by the host — this is only for constants the script itself holds.
check(Technology({"dbuPerMicron": 1000}).dbu_per_micron == 1000,
      "the resolution of the target layout reaches the generator")
check(Technology(None).dbu_per_micron is None,
      "and is absent rather than defaulted when the host states none")
check(dbu(0.42 * (Technology({"dbuPerMicron": 1000}).dbu_per_micron or 0)) == 420,
      "so a 0.42 um process constant becomes 420 DBU")

# ── The example, as a real subprocess ────────────────────────────────────────

print("example generator, over a pipe")
example = Path(__file__).resolve().parent / "example" / "mlin.py"
proc = subprocess.Popen([sys.executable, str(example)],
                        stdin=subprocess.PIPE, stdout=subprocess.PIPE)
try:
    write_frame(proc.stdin, json.dumps({"op": "describe", "wireVersion": WIRE_VERSION}))
    text, _ = read_frame(proc.stdout)
    ids = {g["id"] for g in json.loads(text)["generators"]}
    check(ids == {"MLIN", "VIAARRAY"}, "the example declares both its generators")

    write_frame(proc.stdin, json.dumps({
        "op": "generate", "generatorId": "MLIN",
        "parameters": {"W": 300000, "L": 2000000},
        "layers": {"signal": {"layer": 1, "datatype": 0}},
    }))
    text, payload = read_frame(proc.stdout)
    result = json.loads(text)
    check(payload == [0, -150000, 2000000, 150000], "MLIN's rect matches the contract exactly")
    check(len(result["pins"]) == 2, "and it returns two pins")
    check(result["pins"][0]["outwardDeg"] == 180.0, "pin 1 faces outward at 180 degrees")

    write_frame(proc.stdin, json.dumps({"op": "shutdown"}))
    check(proc.wait(timeout=10) == 0, "shutdown exits cleanly")
finally:
    if proc.poll() is None:
        proc.kill()

print()
if _failures:
    print(f"{len(_failures)} FAILED:")
    for f in _failures:
        print(f"  - {f}")
    sys.exit(1)
print("all checks passed")
