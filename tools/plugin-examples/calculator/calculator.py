"""An L-network match between two resistances: a panel plugin that answers every change at once.

It speaks the plugin frame on stdin/stdout by itself - no helper package:

    [uint32 jsonLen][uint32 binLen][json bytes][binLen bytes of int64], little-endian

The arithmetic. Call the smaller resistance R_lo and the larger R_hi. One L-section raises R_lo to R_hi with a loaded
Q of sqrt(R_hi / R_lo - 1). The series element sits on the R_lo side with reactance Q * R_lo; the shunt element sits
across the R_hi side with reactance R_hi / Q. For a low-pass response the series element is an inductor and the shunt
a capacitor; for a high-pass response they swap. Every value arrives and leaves in base SI.
"""
import json
import math
import os
import struct
import sys

MAX_Q = 50.0


def read_exact(n):
    buf = b""
    while len(buf) < n:
        chunk = sys.stdin.buffer.read(n - len(buf))
        if not chunk:
            return None
        buf += chunk
    return buf


def read_message():
    head = read_exact(8)
    if head is None:
        return None
    json_len, bin_len = struct.unpack("<II", head)
    body = read_exact(json_len)
    if bin_len:
        read_exact(bin_len)
    return json.loads(body.decode("utf-8"))


def send(message):
    data = json.dumps(message).encode("utf-8")
    sys.stdout.buffer.write(struct.pack("<II", len(data), 0) + data)
    sys.stdout.buffer.flush()


def design(params):
    """The two elements, or the reason there are none."""
    f0 = params["f0"]["value"]
    rs = params["rs"]["value"]
    rl = params["rl"]["value"]
    low_pass = params["style"] == "Low-pass"
    if abs(rs - rl) <= 1e-9 * max(rs, rl):
        return None, "warning", "Source and load are equal, so no match is needed."
    r_lo, r_hi = min(rs, rl), max(rs, rl)
    q = math.sqrt(r_hi / r_lo - 1.0)
    if q > MAX_Q:
        return None, "error", "A ratio of %.0f : 1 needs a loaded Q of %.0f, more than one L-section can hold; use two sections." % (r_hi / r_lo, q)
    w = 2.0 * math.pi * f0
    xs, xp = q * r_lo, r_hi / q
    if low_pass:
        series = {"kind": "L", "value": xs / w, "unit": "H"}
        shunt = {"kind": "C", "value": 1.0 / (w * xp), "unit": "F"}
    else:
        series = {"kind": "C", "value": 1.0 / (w * xs), "unit": "F"}
        shunt = {"kind": "L", "value": xp / w, "unit": "H"}
    return {"q": q, "series": series, "shunt": shunt, "r_lo": r_lo, "r_hi": r_hi, "source_low": rs < rl, "f0": f0}, None, None


def ohms(r):
    return ("%g k\u03a9" % (r / 1e3)) if r >= 1e3 else ("%g \u03a9" % r)


def hertz(f):
    for scale, prefix in ((1e9, "G"), (1e6, "M"), (1e3, "k")):
        if f >= scale:
            return "%.3g %sHz" % (f / scale, prefix)
    return "%.3g Hz" % f


def reply(msg):
    d, severity, why = design(msg["params"])
    if d is None:
        return {"type": "reply", "id": msg["id"],
                "readouts": {"series": "—", "shunt": "—", "q": "—", "summary": ""},
                "messages": [{"severity": severity, "text": why}],
                "enabled": {"commit": False}}
    s, p = d["series"], d["shunt"]
    name = {"L": "inductor", "C": "capacitor"}
    text = ("A shunt %s across the %s side, then a series %s toward the %s side. "
            "The match is exact at the design frequency; its bandwidth is roughly f0 / Q = %s."
            % (name[p["kind"]], ohms(d["r_hi"]), name[s["kind"]], ohms(d["r_lo"]), hertz(d["f0"] / d["q"])))
    messages = []
    if d["q"] > 10:
        messages.append({"severity": "warning", "text": "A loaded Q of %.1f is a narrow match; element tolerance will move it." % d["q"]})
    messages.append({"severity": "info", "text": "Series %s %s, shunt %s %s." % (s["kind"], "on the source side" if d["source_low"] else "on the load side",
                                                                              p["kind"], "across the load" if d["source_low"] else "across the source")})
    return {"type": "reply", "id": msg["id"],
            "readouts": {"series": {"value": s["value"], "unit": s["unit"]},
                         "shunt": {"value": p["value"], "unit": p["unit"]},
                         "q": d["q"], "summary": text},
            "messages": messages, "enabled": {"commit": True}}


def netlist(params):
    d, _, why = design(params)
    if d is None:
        return None, why
    rs, rl = params["rs"]["value"], params["rl"]["value"]
    s, p = d["series"], d["shunt"]
    # The series element sits on the low-resistance side; the shunt element across the high one.
    lo, hi = ("src", "load") if d["source_low"] else ("load", "src")
    lines = [
        "Port:P1 src  0 Num=1 Z=%r Ohm" % rs,
        "Port:P2 load 0 Num=2 Z=%r Ohm" % rl,
        "%s:X1 %s %s %s=%r" % (s["kind"], lo, hi, s["kind"], s["value"]),
        "%s:X2 %s 0 %s=%r" % (p["kind"], hi, p["kind"], p["value"]),
        "analysis SP1 type=sparam start=%r stop=%r npts=101" % (d["f0"] * 0.5, d["f0"] * 1.5),
    ]
    return "\n".join(lines) + "\n", None


def main():
    staging = None
    while True:
        msg = read_message()
        if msg is None or msg.get("type") == "bye":
            return
        kind = msg.get("type")
        if kind == "hello":
            staging = msg["staging"]
            send({"type": "hello", "apiVersion": 1})
        elif kind in ("update", "action"):
            send(reply(msg))
        elif kind == "commit":
            text, why = netlist(msg["params"])
            if text is None:
                send({"type": "placed", "id": msg["id"], "cells": [], "messages": [{"severity": "error", "text": why}]})
                continue
            with open(os.path.join(staging, "l_match.cnl"), "w", encoding="utf-8") as f:
                f.write(text)
            send({"type": "placed", "id": msg["id"], "cells": [{"name": "l_match", "files": ["l_match.cnl"]}]})
        # "cancel" needs nothing: every answer here is immediate.


if __name__ == "__main__":
    main()
