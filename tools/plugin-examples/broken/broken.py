"""A plugin that breaks the contract in one way, chosen by its first argument.

Each mode is something a real plugin could do by mistake; circuitRF must survive every one of them, stay
responsive, say what happened and leave no process behind.

    exit       exits at once, before answering anything
    hang       answers nothing, and starts a child that would outlive it if only the parent were killed
    text       prints an ordinary line on stdout, where only frames belong
    malformed  sends a well-framed message whose body is not JSON
    wrongid    answers an update with an id it was never sent
    outside    places a file it wrote outside its staging folder
    stderr     writes 10 MB to stderr, then behaves
    exit-after-first-reply
               answers the first update, then exits - a plugin that crashes between two changes
"""
import json
import os
import struct
import subprocess
import sys
import time


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


def send_bytes(data):
    sys.stdout.buffer.write(struct.pack("<II", len(data), 0) + data)
    sys.stdout.buffer.flush()


def send(message):
    send_bytes(json.dumps(message).encode("utf-8"))


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else "exit"
    sys.stderr.write("broken plugin, mode %s\n" % mode)
    sys.stderr.flush()

    if mode == "exit":
        sys.exit(3)

    if mode == "hang":
        child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(600)"])
        sys.stderr.write("child %d\n" % child.pid)
        sys.stderr.flush()
        time.sleep(600)
        return

    if mode == "text":
        sys.stdout.write("debug: the plugin is starting up and printing to the wrong stream\n")
        sys.stdout.flush()
        time.sleep(600)
        return

    if mode == "malformed":
        send_bytes(b"this is not json {")
        time.sleep(600)
        return

    if mode == "stderr":
        line = "x" * 99 + "\n"
        for _ in range(10 * 1024 * 1024 // 100):
            sys.stderr.write(line)
        sys.stderr.flush()

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
            send({"type": "reply", "id": msg["id"] + (1000 if mode == "wrongid" else 0), "readouts": {},
                  "messages": [{"severity": "info", "text": "answered n=%s" % msg["params"].get("n")}]})
            if mode == "exit-after-first-reply":
                sys.stderr.write("exiting after the first reply\n")
                sys.stderr.flush()
                sys.exit(4)
        elif kind == "commit":
            outside = os.path.join(os.path.dirname(staging), "escaped.cnl")
            with open(outside, "w", encoding="utf-8") as f:
                f.write("R:R1 a 0 R=50 Ohm\n")
            send({"type": "placed", "id": msg["id"], "cells": [{"name": "escaped", "files": ["../escaped.cnl"]}]})


if __name__ == "__main__":
    main()
