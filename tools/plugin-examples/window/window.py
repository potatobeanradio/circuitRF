"""A `window` plugin with no real window: after hello it stages a .cnl, sends `placed` under an id of its own, waits for
circuitRF's placedReply, and exits. Started with `stay`, it stays until circuitRF says `bye` instead.

A real window plugin draws its own window here and sends `placed`, `host` and `open` whenever its user asks.

It speaks the plugin frame on stdin/stdout by itself - no helper package:

    [uint32 jsonLen][uint32 binLen][json bytes][binLen bytes of int64], little-endian
"""
import json
import os
import struct
import sys

NETLIST = """Port:P1 in 0 Num=1 Z=50 Ohm
Port:P2 out 0 Num=2 Z=50 Ohm
R:R1 in out R=8.56 Ohm
R:R2 out 0 R=292 Ohm
analysis SP1 type=sparam start=1 stop=6 npts=11 Unit=GHz
"""


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


def main():
    stay = "stay" in sys.argv[1:]
    hello = read_message()
    if hello is None or hello.get("type") != "hello":
        return
    send({"type": "hello", "apiVersion": 1})
    with open(os.path.join(hello["staging"], "attenuator.cnl"), "w", encoding="utf-8") as f:
        f.write(NETLIST)
    # The id is this plugin's own; circuitRF answers placedReply with the same one.
    send({"type": "placed", "id": 1, "cells": [{"name": "attenuator", "files": ["attenuator.cnl"]}]})
    while True:
        msg = read_message()
        if msg is None or msg.get("type") == "bye":
            return
        if msg.get("type") == "placedReply" and msg.get("id") == 1:
            print("placed: %s" % json.dumps(msg), file=sys.stderr)
            if not stay:
                return


if __name__ == "__main__":
    main()
