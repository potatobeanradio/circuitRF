"""Sends one host call, written as JSON in the `call` parameter, and shows circuitRF's hostReply.

On every update or action the parameter's JSON object becomes the fields of a `host` message for that request; the
reply's `answer` readout is the hostReply circuitRF sent back, and `last` is the one before it - which is how a call that
was cancelled with its request is seen afterwards. An empty `call` sends nothing.

It speaks the plugin frame on stdin/stdout by itself - no helper package:

    [uint32 jsonLen][uint32 binLen][json bytes][binLen bytes of int64], little-endian
"""
import json
import struct
import sys

queued = []


def read_exact(n):
    buf = b""
    while len(buf) < n:
        chunk = sys.stdin.buffer.read(n - len(buf))
        if not chunk:
            return None
        buf += chunk
    return buf


def read_message():
    if queued:
        return queued.pop(0)
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


def text_of(value):
    """A string parameter arrives as its text, or as {"value": text} from a panel."""
    return value.get("value", "") if isinstance(value, dict) else (value or "")


def host_call(request_id, fields):
    """Sends `host` for the request and waits for its hostReply. Anything else that arrives meanwhile - a newer
    update, a cancel - is kept for afterwards, in order."""
    send(dict(fields, type="host", id=request_id))
    held = []
    while True:
        msg = read_message()
        if msg is None:
            sys.exit(0)
        if msg.get("type") == "hostReply" and msg.get("id") == request_id:
            queued.extend(held)
            return msg
        held.append(msg)


def main():
    last = None
    while True:
        msg = read_message()
        if msg is None or msg.get("type") == "bye":
            return
        kind = msg.get("type")
        if kind == "hello":
            send({"type": "hello", "apiVersion": 1})
        elif kind in ("update", "action", "event"):
            text = text_of(msg.get("params", {}).get("call")).strip()
            answer = None
            messages = []
            if text:
                try:
                    fields = json.loads(text)
                except ValueError as e:
                    fields = None
                    messages.append({"severity": "error", "text": "The host call is not JSON: %s" % e})
                if isinstance(fields, dict):
                    answer = host_call(msg["id"], fields)
                    if "error" in answer:
                        messages.append({"severity": "warning", "text": answer["error"]})
            readouts = {"answer": json.dumps(answer) if answer else "", "last": json.dumps(last) if last else ""}
            if answer:
                last = answer
            send({"type": "reply", "id": msg["id"], "readouts": readouts, "messages": messages})
        # "cancel" needs nothing here: a cancelled call is answered by circuitRF, and this reply is then dropped.


if __name__ == "__main__":
    main()
