"""The smallest circuitRF plugin: one parameter, and a cell to place.

It speaks the plugin frame on stdin/stdout by itself - no helper package - so it shows the whole contract:

    [uint32 jsonLen][uint32 binLen][json bytes][binLen bytes of int64], little-endian

stdout carries frames and nothing else; anything to log goes to stderr.
"""
import json
import os
import struct
import sys


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


NETLIST = """Port:P1 in  0 Num=1 Z=50 Ohm
Port:P2 out 0 Num=2 Z=50 Ohm
R:R1 in  out R={r!r} Ohm
R:R2 out 0   R={r!r} Ohm
analysis SP1 type=sparam start=1 stop=6 npts=11 Unit=GHz
"""


def resistor(name, x, y, ohms):
    return {
        "InstanceName": name, "Symbol": "Resistor", "X": x, "Y": y, "Rotation": "R0", "MirrorX": False,
        "Disable": "None",
        "Parameters": [{"Name": "R", "Expression": repr(ohms), "Unit": "Ohm", "ShowOnSchematic": True,
                        "Dimension": "Resistance"}],
    }


def schematic(ohms):
    return {
        "FormatVersion": 2, "CellName": "divider", "GridSize": 100, "GridSnap": True,
        "Components": [resistor("R1", 0, 0, ohms), resistor("R2", 400, 0, ohms)],
        "Wires": [], "Dots": [], "NetLabels": [], "Analyses": [], "Sweeps": [], "CanvasObjects": [],
    }


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
        elif kind == "update":
            r = msg["params"]["r"]["value"]
            send({"type": "reply", "id": msg["id"], "readouts": {"r": r},
                  "messages": [{"severity": "info", "text": "Two %g Ohm resistors." % r}]})
        elif kind == "commit":
            r = msg["params"]["r"]["value"]
            with open(os.path.join(staging, "divider.cnl"), "w", encoding="utf-8") as f:
                f.write(NETLIST.format(r=r))
            with open(os.path.join(staging, "divider.csch"), "w", encoding="utf-8") as f:
                json.dump(schematic(r), f, indent=2)
            send({"type": "placed", "id": msg["id"],
                  "cells": [{"name": "divider", "files": ["divider.cnl", "divider.csch"]}]})
        # "cancel" needs nothing: every answer here is immediate.


if __name__ == "__main__":
    main()
