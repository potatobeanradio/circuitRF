"""A single open shunt stub that matches a load to 50 ohms: the reference panel plugin.

It speaks the plugin frame on stdin/stdout by itself - no helper package:

    [uint32 jsonLen][uint32 binLen][json bytes][binLen bytes of int64], little-endian

and it returns every view kind a panel draws from data: a response plot with a marker bound to the frequency, a Smith
chart, a dimensions table, the artwork as PCell-wire shapes with a handle on the stub, and the circuit as a .cnl.

The arithmetic is the textbook single-stub match on ideal lines. From the load, a line of length d turns the load's
admittance to 1/Z0 + jB; an open stub of length l across that point adds -jB. With t = tan(beta d):

    t = (X + sqrt(R ((Z0 - R)^2 + X^2) / Z0)) / (R - Z0)        (t = -X / 2 Z0 when R = Z0)
    B = (R^2 t - (Z0 - X t)(X + Z0 t)) / (Z0 (R^2 + (X + Z0 t)^2))
    l = atan(-B Z0) / beta, folded into [0, lambda/2)

The microstrip width and effective permittivity come from the closed-form microstrip equations. Every value arrives and
leaves in base SI; geometry crosses as int64 database units, 1000 per micrometre.

The "Simulate in circuitRF" button asks circuitRF to run the staged circuit with its own engine (the `simulate` host
call) and draws that S11 beside the closed form's, so the two can be compared before anything is placed.
"""
import json
import math
import os
import struct
import sys

C0 = 299792458.0
Z0 = 50.0
DBU_PER_M = 1000 * 1e6
FEED = 3e-3          # the feed from P1 to the stub junction, metres
POINTS = 201


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


def send(message, payload=()):
    data = json.dumps(message).encode("utf-8")
    blob = struct.pack("<%dq" % len(payload), *payload) if payload else b""
    sys.stdout.buffer.write(struct.pack("<II", len(data), len(blob)) + data + blob)
    sys.stdout.buffer.flush()


# ── microstrip, closed form ─────────────────────────────────────────────────

def eeff_of(w_h, er):
    f = (1 + 12 / w_h) ** -0.5 + (0.04 * (1 - w_h) ** 2 if w_h < 1 else 0.0)
    return (er + 1) / 2 + (er - 1) / 2 * f


def width_for(z0, er, h):
    """The strip width for z0 on a substrate of er and height h."""
    a = z0 / 60 * math.sqrt((er + 1) / 2) + (er - 1) / (er + 1) * (0.23 + 0.11 / er)
    w_h = 8 * math.exp(a) / (math.exp(2 * a) - 2)
    if w_h > 2:
        b = 377 * math.pi / (2 * z0 * math.sqrt(er))
        w_h = 2 / math.pi * (b - 1 - math.log(2 * b - 1) + (er - 1) / (2 * er) * (math.log(b - 1) + 0.39 - 0.61 / er))
    return w_h * h


# ── the match ───────────────────────────────────────────────────────────────

def design(p):
    f0, r, x = p["f0"]["value"], p["rl"]["value"], p["xl"]["value"]
    sub = p["substrate"]
    er, h = sub["er"], sub["h"]
    w = width_for(Z0, er, h)
    eeff = eeff_of(w / h, er)
    lam = C0 / (f0 * math.sqrt(eeff))
    if abs(r - Z0) < 1e-9:
        t = -x / (2 * Z0)
    else:
        t = (x + math.sqrt(r * ((Z0 - r) ** 2 + x * x) / Z0)) / (r - Z0)
    d = math.atan(t) / (2 * math.pi) * lam
    if d < 0:
        d += lam / 2
    b = (r * r * t - (Z0 - x * t) * (x + Z0 * t)) / (Z0 * (r * r + (x + Z0 * t) ** 2))
    l = math.atan(-b * Z0) / (2 * math.pi) * lam
    if l < 0:
        l += lam / 2
    return {"f0": f0, "r": r, "x": x, "w": w, "eeff": eeff, "lam": lam, "d": d, "l": l,
            "stub": l + p["trim"]["value"], "signal": signal_layer(sub)}


def signal_layer(sub):
    for entry in sub.get("stackup", []):
        if entry.get("name") == sub.get("signal") and entry.get("layers"):
            return entry["layers"][0]
    return sub.get("signal", "Top Copper")


def gamma(m, f):
    """S11 seen from P1: the feed, then the stub junction, then the line d to the load."""
    beta = 2 * math.pi * f * math.sqrt(m["eeff"]) / C0
    zl = complex(m["r"], m["x"] * f / m["f0"] if m["x"] >= 0 else m["x"] * m["f0"] / f)   # an L or a C load
    tan_d = math.tan(beta * m["d"])
    z = Z0 * (zl + 1j * Z0 * tan_d) / (Z0 + 1j * zl * tan_d)
    y = 1 / z + 1j * math.tan(beta * m["stub"]) / Z0
    z = 1 / y
    tan_f = math.tan(beta * FEED)
    z = Z0 * (z + 1j * Z0 * tan_f) / (Z0 + 1j * z * tan_f)
    return (z - Z0) / (z + Z0)


def mm(v):
    return "%.4g mm" % (v * 1e3)


# ── the views ───────────────────────────────────────────────────────────────

def sweep(f0):
    """Half to one and a half times f0 on a round step (1, 2 or 5 times a power of ten), at least POINTS samples, so a
    marker dragged along the curve lands on round frequencies. Integer multiples of the step are exact in a double."""
    raw = f0 / (POINTS - 1)
    decade = 10 ** math.floor(math.log10(raw))
    step = max(k * decade for k in (1, 2, 5) if k * decade <= raw)
    first, last = math.ceil(f0 * 0.5 / step), math.floor(f0 * 1.5 / step)
    return [k * step for k in range(first, last + 1)]


def data(m):
    freqs = sweep(m["f0"])
    g = [gamma(m, f) for f in freqs]
    return {"axes": {"freq": {"unit": "Hz", "values": freqs}},
            "cubes": {"S11": {"axes": ["freq"], "kind": "complex", "re": [v.real for v in g], "im": [v.imag for v in g]}}}


def artwork(m):
    """The strips as PCell-wire rectangles: the feed and the line along x, the stub up from the junction."""
    dbu = lambda v: int(round(v * DBU_PER_M))
    w, jx, end = dbu(m["w"]), dbu(FEED), dbu(FEED + m["d"])
    top = w // 2 + dbu(m["l"])
    payload, shapes = [], []

    def span(values):
        at = len(payload)
        payload.extend(values)
        return {"at": at, "count": len(values)}

    shapes.append({"kind": "rect", "layer": m["signal"], "xy": span([0, -w // 2, end, w // 2])})
    shapes.append({"kind": "rect", "layer": m["signal"], "xy": span([jx - w // 2, w // 2, jx + w // 2, w // 2 + dbu(m["stub"])])})
    handles = [{"parameter": "trim", "kind": "linear", "label": "Stub length", "axisDeg": 90.0,
                "span": span([jx, top, jx, w // 2 + dbu(m["stub"])]), "min": -m["l"]}]
    ports = [{"name": "1", "x": 0, "y": 0, "layer": m["signal"], "width": w, "outwardDeg": 180.0},
             {"name": "2", "x": end, "y": 0, "layer": m["signal"], "width": w, "outwardDeg": 0.0}]
    return {"technology": "self", "shapes": shapes, "ports": ports, "handles": handles}, payload


def netlist(m):
    """The circuit as a .cnl, every value written in a unit a reader would choose."""
    eeff = "%.6g" % m["eeff"]
    line = lambda name, a, b, length: "TLIN:%s %s %s Z=50 Ohm L=%.6g mm Eeff=%s" % (name, a, b, length * 1e3, eeff)
    lines = ["Port:P1 in 0 Num=1 Z=50 Ohm", line("TF", "in", "j", FEED), line("TS", "j", "open", m["stub"]), line("TD", "j", "load", m["d"])]
    w0 = 2 * math.pi * m["f0"]
    if m["x"] > 0:
        lines += ["R:RL load x R=%.6g Ohm" % m["r"], "L:XL x 0 L=%.6g nH" % (m["x"] / w0 * 1e9)]
    elif m["x"] < 0:
        lines += ["R:RL load x R=%.6g Ohm" % m["r"], "C:XL x 0 C=%.6g pF" % (-1 / (w0 * m["x"]) * 1e12)]
    else:
        lines += ["R:RL load 0 R=%.6g Ohm" % m["r"]]
    f = sweep(m["f0"])
    lines.append("analysis SP1 type=sparam start=%.6g stop=%.6g npts=%d Unit=GHz" % (f[0] * 1e-9, f[-1] * 1e-9, len(f)))
    return "\n".join(lines) + "\n"


def read_s1p(path):
    """A one-port Touchstone file's S11 by frequency in hertz: RI, MA or DB, any frequency unit."""
    scale = {"hz": 1.0, "khz": 1e3, "mhz": 1e6, "ghz": 1e9}
    unit, fmt, freqs, values = 1e9, "ma", [], []
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.split("!")[0].strip()
            if not line:
                continue
            if line.startswith("#"):
                words = line[1:].lower().split()
                for w in words:
                    if w in scale:
                        unit = scale[w]
                    elif w in ("ri", "ma", "db"):
                        fmt = w
                continue
            a, b, c = (float(x) for x in line.split()[:3])
            if fmt == "ri":
                v = complex(b, c)
            else:
                mag = 10 ** (b / 20) if fmt == "db" else b
                v = mag * complex(math.cos(math.radians(c)), math.sin(math.radians(c)))
            freqs.append(a * unit)
            values.append(v)
    return freqs, values


def simulate(msg, staging, body):
    """The `simulate` host call: circuitRF runs circuit.cnl, and its S11 joins the response plot as a second trace."""
    send({"type": "host", "id": msg["id"], "call": "simulate", "cnl": "circuit.cnl"})
    while True:
        answer = read_message()
        if answer is None:
            sys.exit(0)
        if answer.get("type") == "hostReply" and answer.get("id") == msg["id"]:
            break
    if "error" in answer:
        body["messages"].append({"severity": "error", "text": "circuitRF could not simulate the circuit: " + answer["error"]})
        return
    freqs, values = read_s1p(answer["touchstone"])
    response = body["views"]["response"]
    response["data"] = json.loads(json.dumps(response["data"]))      # its own copy: the Smith chart shares the data
    response["data"]["axes"]["freq_circuitRF"] = {"unit": "Hz", "values": freqs}
    response["data"]["cubes"]["S11_circuitRF"] = {
        "axes": ["freq_circuitRF"], "kind": "complex", "re": [v.real for v in values], "im": [v.imag for v in values]}
    response["plot"]["traces"].append({"cube": "S11_circuitRF", "y": "db20", "label": "S11, circuitRF"})
    body["messages"].append({"severity": "info", "text": "circuitRF simulated the circuit: " + os.path.basename(answer["touchstone"])})


def reply(msg, staging):
    p = msg["params"]
    m = design(p)
    if m["stub"] <= 0:
        return {"type": "reply", "id": msg["id"], "readouts": {"summary": ""},
                "messages": [{"severity": "error", "text": "The trim makes the stub %s long; it must be longer than 0." % mm(m["stub"])}],
                "enabled": {"commit": False}}, ()
    with open(os.path.join(staging, "circuit.cnl"), "w", encoding="utf-8") as f:
        f.write(netlist(m))
    layout, payload = artwork(m)
    s11 = abs(gamma(m, m["f0"]))
    ds = data(m)
    views = {
        "response": {"data": ds, "plot": {
            "y": {"label": "dB", "min": -40, "max": 0},
            "traces": [{"cube": "S11", "y": "db20", "label": "S11"}],
            "markers": [{"trace": "S11", "bind": "f0", "draggable": True}],
            "lines": [{"y": -20, "side": "above", "label": "spec", "xStart": m["f0"] * 0.95, "xEnd": m["f0"] * 1.05}]}},
        "match": {"data": ds, "plot": {
            "traces": [{"cube": "S11", "label": "S11"}],
            "markers": [{"trace": "S11", "bind": "f0"}]}},
        "dims": {"columns": ["Section", "Width", "Length"], "units": ["", "metre", "metre"],
                 "rows": [["Feed", m["w"], FEED], ["Line to load", m["w"], m["d"]], ["Open stub", m["w"], m["stub"]]]},
        "artwork": layout,
        "circuit": {"cnl": "circuit.cnl"},
    }
    summary = ("A %s open stub %s from the load, on a %s wide line with an effective permittivity of %.3g. "
               "|S11| at the design frequency is %.2g." % (mm(m["stub"]), mm(m["d"]), mm(m["w"]), m["eeff"], s11))
    messages = []
    if abs(p["trim"]["value"]) > 0:
        messages.append({"severity": "info", "text": "The stub is trimmed %s from the exact match." % mm(p["trim"]["value"])})
    return {"type": "reply", "id": msg["id"], "readouts": {"summary": summary}, "views": views,
            "messages": messages, "enabled": {"commit": True}}, payload


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
        elif kind in ("update", "action", "event"):
            # An event is a dragged marker or handle; its parameter is already in params, so it is answered as an update.
            body, payload = reply(msg, staging)
            if kind == "action" and msg.get("name") == "simulate" and "views" in body:
                simulate(msg, staging, body)
            send(body, payload)
        elif kind == "commit":
            m = design(msg["params"])
            with open(os.path.join(staging, "stub.cnl"), "w", encoding="utf-8") as f:
                f.write(netlist(m))
            # The layout is the artwork view itself: circuitRF writes it, ports and all, as the cell's .clay.
            send({"type": "placed", "id": msg["id"], "cells": [{"name": "stub", "files": ["stub.cnl"], "layout": "artwork"}]})
        # "cancel" needs nothing: every answer here is immediate.


if __name__ == "__main__":
    main()
