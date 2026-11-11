"""The conversation with circuitRF: hello, the requests, cancel, progress, host calls and bye.

A plugin registers a function per request and calls :meth:`Plugin.run`::

    plugin = Plugin()

    @plugin.update
    def update(p, ctx):
        return Reply(readouts={"z0": 50.2})

    plugin.run()

Everything here follows docs/user/src/reference/writing-plugins.md; nothing in it is specific to one plugin.
"""

from __future__ import annotations

import json
import os
import queue
import sys
import threading
import traceback
from decimal import ROUND_HALF_UP, Decimal
from typing import Any, BinaryIO, Callable, Optional

from . import frame

#: The newest message set this package speaks.
API_VERSION = 1

#: circuitRF's default database units per micrometre, used until a technology parameter names its own.
DEFAULT_DBU_PER_MICRON = 1000


class Cancelled(Exception):
    """Raised by :meth:`Context.check` (and by a host call) once circuitRF has cancelled the request being answered.

    Let it propagate: the package answers the cancelled request for you, and circuitRF drops that answer.
    """


class HostError(Exception):
    """circuitRF answered a host call with an error. The message is circuitRF's own sentence."""


# ── values the handlers return ─────────────────────────────────────────────────


def error(text: str) -> dict:
    """A message for the panel's messages list."""
    return {"severity": "error", "text": text}


def warning(text: str) -> dict:
    return {"severity": "warning", "text": text}


def info(text: str) -> dict:
    return {"severity": "info", "text": text}


class Reply:
    """The answer to an ``update``, an ``action`` or an ``event``.

    - ``readouts``: keyed by ``readout`` parameter. A number is base SI in the parameter's unit, ``{"value", "unit"}``
      names its own unit, a string is shown as written, and a :class:`~circuitrf_plugin.views.Table` fills an
      ``as: table`` readout.
    - ``views``: keyed by view name. A view left out keeps what it showed; ``None`` clears it.
    - ``messages``: :func:`error`, :func:`warning`, :func:`info` dicts, or plain strings (read as info).
    - ``enabled``: ``{name: False}`` greys a parameter or a button (named by its action). Complete each time.
    - ``set``: ``{param: value}`` changes parameters as typing them would, with no further update.
    """

    def __init__(self, readouts: Optional[dict] = None, views: Optional[dict] = None,
                 messages: Optional[list] = None, enabled: Optional[dict] = None, set: Optional[dict] = None):
        self.readouts = readouts or {}
        self.views = views or {}
        self.messages = list(messages or [])
        self.enabled = enabled or {}
        self.set = set or {}

    def to_message(self, request_id: int) -> tuple[dict, list[int]]:
        payload: list[int] = []
        body: dict[str, Any] = {"type": "reply", "id": request_id}
        if self.readouts:
            body["readouts"] = {k: _json_of(v, payload) for k, v in self.readouts.items()}
        if self.views:
            body["views"] = {k: (None if v is None else _json_of(v, payload)) for k, v in self.views.items()}
        body["messages"] = [_message(m) for m in self.messages]
        if self.enabled:
            body["enabled"] = dict(self.enabled)
        if self.set:
            body["set"] = dict(self.set)
        return body, payload


class Placed:
    """The answer to ``commit``: the cells to place, each ``{"name", "files", "layout"?}``, and any messages.

    ``files`` are paths relative to the staging folder (stage them with :meth:`Context.stage`). ``layout`` names a
    ``layout`` view of the last reply instead of a staged ``.clay``: circuitRF writes that view as the cell's layout.
    A commit handler may return a plain list of cells instead.
    """

    def __init__(self, cells: list, messages: Optional[list] = None):
        self.cells = list(cells)
        self.messages = list(messages or [])


def _message(m) -> dict:
    if isinstance(m, str):
        return info(m)
    if isinstance(m, dict) and "text" in m:
        return {"severity": m.get("severity", "info"), "text": str(m["text"])}
    raise TypeError("A message is error(text), warning(text), info(text) or a string, not %r." % (m,))


def _json_of(value, payload: list[int]):
    """A view or readout as the JSON it crosses as; a layout puts its coordinates on the frame's payload."""
    to_json = getattr(value, "to_json", None)
    if callable(to_json):
        return to_json(payload)
    return value


# ── parameters ──────────────────────────────────────────────────────────────────


class Params(dict):
    """The parameters circuitRF sent, as it sent them, with one convenience.

    ``p["f0"]`` is the object circuitRF delivered (``{"value": 2.4e9, "unit": "Hz", "text": "2.4 GHz"}`` for a
    quantity). ``p.f0`` is a quantity's ``value`` in base SI, or any other kind's value as delivered.
    """

    def __getattr__(self, name: str):
        try:
            v = self[name]
        except KeyError:
            raise AttributeError("circuitRF sent no parameter called %r (it sent %s)." % (name, ", ".join(self) or "none"))
        if isinstance(v, dict) and "value" in v and "unit" in v:
            return v["value"]
        return v


# ── the context a handler is given ──────────────────────────────────────────────


class Context:
    """What a handler can ask for: the session's facts, the staging folder, progress, cancel, and host calls."""

    def __init__(self, plugin: "Plugin", hello: dict):
        self._plugin = plugin
        self.hello = hello
        self.api_version = API_VERSION
        self.staging: str = hello.get("staging") or os.getcwd()
        self.workspace: Optional[str] = hello.get("workspace")
        self.platform: Optional[str] = hello.get("platform")
        self.mode: str = hello.get("mode") or "interactive"
        self.context = hello.get("context")
        self.request_id: Optional[int] = None
        self.dbu_per_micron: int = DEFAULT_DBU_PER_MICRON

    @property
    def headless(self) -> bool:
        """True under ``circuitRF plugin run``: no window will ever show this reply."""
        return self.mode == "headless"

    # staging

    def path(self, name: str) -> str:
        """The absolute path of ``name`` in the staging folder. A name that leaves the folder is refused."""
        root = os.path.realpath(self.staging)
        full = os.path.realpath(os.path.join(root, name))
        if full != root and not full.startswith(root + os.sep):
            raise ValueError("%r is outside the staging folder; a plugin writes only there." % name)
        return full

    def stage(self, name: str, content) -> str:
        """Write ``content`` (text, written as UTF-8 with ``\\n`` line ends, or bytes) to ``name`` in the staging
        folder and return ``name``, ready for a ``files`` list or a view."""
        full = self.path(name)
        os.makedirs(os.path.dirname(full), exist_ok=True)
        if isinstance(content, str):
            content = content.encode("utf-8")
        with open(full, "wb") as f:
            f.write(content)
        return name

    # geometry

    def dbu(self, metres: float) -> int:
        """Metres as database units, rounded ONCE, half away from zero, as circuitRF rounds a length it is typed.

        Round each coordinate here and nowhere else: a sum of rounded values drifts, a rounded sum does not.
        """
        if isinstance(metres, bool) or not isinstance(metres, (int, float, Decimal)):
            raise TypeError("dbu() takes a length in metres, not %r." % (metres,))
        exact = Decimal(repr(metres)) * Decimal(10) ** 6 * self.dbu_per_micron
        return int(exact.quantize(Decimal(1), rounding=ROUND_HALF_UP))

    # progress and cancel

    def progress(self, fraction: Optional[float] = None, text: Optional[str] = None) -> None:
        """Move the panel's busy bar. Also a cancellation point: it raises :class:`Cancelled` once cancelled."""
        self.check()
        msg: dict[str, Any] = {"type": "progress", "id": self.request_id}
        if fraction is not None:
            msg["fraction"] = float(fraction)
        if text is not None:
            msg["text"] = str(text)
        self._plugin._send(msg)

    def check(self) -> None:
        """Raise :class:`Cancelled` if circuitRF has cancelled the request being answered. Call it in long loops."""
        self._plugin._drain()
        if self.request_id in self._plugin._cancelled:
            raise Cancelled()

    # host calls (writing-plugins.md, "Host calls")

    def simulate(self, cnl: str, analysis: Optional[str] = None) -> dict:
        """Run a staged ``.cnl`` with circuitRF's own engine: ``{result, touchstone?, cubes, messages}``."""
        args: dict[str, Any] = {"cnl": cnl}
        if analysis:
            args["analysis"] = analysis
        return self.host("simulate", **args)

    def resolve(self, what: str, name=None, ref: Optional[str] = None, from_: Optional[str] = None) -> dict:
        """A technology, substrate or cell as a parameter of that kind delivers it, or a reference's report."""
        args: dict[str, Any] = {"what": what}
        if name is not None:
            args["name"] = name
        if ref is not None:
            args["ref"] = ref
        if from_ is not None:
            args["from"] = from_
        return self.host("resolve", **args)["resolved"]

    def evaluate(self, expr: str, unit: Optional[str] = None, path: Optional[str] = None) -> dict:
        """An expression evaluated by circuitRF's expression engine: ``{value, unit}``."""
        args: dict[str, Any] = {"expr": expr}
        if unit is not None:
            args["unit"] = unit
        if path is not None:
            args["path"] = path
        return self.host("evaluate", **args)

    def line_model(self, line: str, substrate, width: float, freq, gap: Optional[float] = None) -> dict:
        """circuitRF's model of a line of ``width`` metres on ``substrate`` at ``freq`` hertz (a number or a list):
        ``{z0: [...], eeff: [...], alphaDbPerM: [...], messages}``, one entry per frequency."""
        args: dict[str, Any] = {"line": line, "substrate": substrate, "width": float(width),
                                "freq": [float(f) for f in freq] if isinstance(freq, (list, tuple)) else float(freq)}
        if gap is not None:
            args["gap"] = float(gap)
        return self.host("lineModel", **args)

    def host(self, call: str, **args) -> dict:
        """Any host call by name. Returns circuitRF's answer, or raises :class:`HostError` with its sentence."""
        if self.request_id is None:
            raise RuntimeError("A host call is made while answering a request, from inside a handler.")
        self._plugin._send(dict({"type": "host", "id": self.request_id, "call": call}, **args))
        answer = self._plugin._await_host_reply(self.request_id)
        if self.request_id in self._plugin._cancelled:
            raise Cancelled()
        if "error" in answer:
            raise HostError(str(answer["error"]))
        return answer


# ── the plugin ──────────────────────────────────────────────────────────────────


class Plugin:
    """Register handlers with the decorators, then call :meth:`run`."""

    def __init__(self, api_version: int = API_VERSION):
        self.api_version = api_version
        self._update: Optional[Callable] = None
        self._actions: dict[str, Callable] = {}
        self._event: Optional[Callable] = None
        self._commit: Optional[Callable] = None
        self._hello: Optional[Callable] = None
        self._out: Optional[BinaryIO] = None
        self._inbox: "queue.Queue[Optional[tuple[dict, list[int]]]]" = queue.Queue()
        self._waiting: list[tuple[dict, list[int]]] = []
        self._cancelled: set = set()
        self._ended = False
        self._send_lock = threading.Lock()

    # decorators

    def update(self, fn: Callable) -> Callable:
        """``fn(p, ctx) -> Reply`` - every parameter change. Also answers an event or action with no handler."""
        self._update = fn
        return fn

    def action(self, name: str) -> Callable:
        """``@plugin.action("simulate")``: ``fn(p, ctx) -> Reply`` for the button whose action is ``name``."""
        def register(fn: Callable) -> Callable:
            self._actions[name] = fn
            return fn
        return register

    def event(self, fn: Callable) -> Callable:
        """``fn(p, ctx, event) -> Reply`` - a dragged marker or handle, or a click on an image. ``p`` already holds the
        new value; ``event`` is the message (``view``, ``kind`` and its fields)."""
        self._event = fn
        return fn

    def commit(self, fn: Callable) -> Callable:
        """``fn(p, ctx) -> Placed | list`` - Place in workspace, and ``circuitRF plugin run --place``."""
        self._commit = fn
        return fn

    def on_hello(self, fn: Callable) -> Callable:
        """``fn(ctx)`` once, after hello - for work every request shares."""
        self._hello = fn
        return fn

    # the loop

    def run(self, stdin: Optional[BinaryIO] = None, stdout: Optional[BinaryIO] = None) -> None:
        """Answer circuitRF until it says ``bye`` or closes the pipe.

        stdout carries frames and nothing else, so ``print`` (and anything else writing to ``sys.stdout``) is sent to
        stderr - the plugin's log - before any handler runs.
        """
        self._out = stdout if stdout is not None else _claim_stdout()
        source = stdin if stdin is not None else sys.stdin.buffer
        threading.Thread(target=self._read, args=(source,), name="circuitrf-plugin-reader", daemon=True).start()

        hello = self._next()
        if hello is None:
            return
        msg, _ = hello
        if msg.get("type") != "hello":
            _log("circuitRF opened with %r, not hello." % msg.get("type"))
            return
        offered = msg.get("apiVersion", self.api_version)
        self._send({"type": "hello", "apiVersion": min(self.api_version, offered) if isinstance(offered, int) else self.api_version})
        ctx = Context(self, msg)
        if self._hello is not None:
            self._hello(ctx)

        while True:
            nxt = self._next()
            if nxt is None:
                return
            msg, payload = nxt
            kind = msg.get("type")
            if kind == "bye":
                return
            if kind == "cancel":
                continue                 # nothing of that id is running: it was answered already, or answered below
            if kind in ("update", "action", "event", "commit"):
                self._answer(ctx, msg)
            else:
                _log("Ignoring a %r message this plugin does not handle." % kind)

    def _answer(self, ctx: Context, msg: dict) -> None:
        rid = msg.get("id")
        kind = msg["type"]
        ctx.request_id = rid
        p = Params(msg.get("params") or {})
        ctx.dbu_per_micron = _dbu_per_micron(p)
        try:
            if rid in self._cancelled:
                raise Cancelled()
            if kind == "commit":
                if self._commit is None:
                    raise RuntimeError("This plugin places nothing: it has no @plugin.commit.")
                placed = self._commit(p, ctx)
                if not isinstance(placed, Placed):
                    placed = Placed(placed or [])
                body: dict[str, Any] = {"type": "placed", "id": rid, "cells": placed.cells}
                if placed.messages:
                    body["messages"] = [_message(m) for m in placed.messages]
                self._send(body)
                return
            handler = self._update
            args: tuple = (p, ctx)
            if kind == "action" and msg.get("name") in self._actions:
                handler = self._actions[msg["name"]]
            elif kind == "event" and self._event is not None:
                handler, args = self._event, (p, ctx, msg)
            if handler is None:
                raise RuntimeError("This plugin has no @plugin.update.")
            reply = handler(*args)
            if not isinstance(reply, Reply):
                reply = Reply(**reply) if isinstance(reply, dict) else Reply()
            body, payload = reply.to_message(rid)
            self._send(body, payload)
        except Cancelled:
            # circuitRF drops the answer to a cancelled request, but waits for one before it calls the plugin settled.
            self._send({"type": "reply", "id": rid, "messages": []})
        except Exception as e:  # a plugin's own bug: reported in the panel, and the session carries on
            _log(traceback.format_exc())
            text = "%s: %s" % (type(e).__name__, e) if not isinstance(e, HostError) else "circuitRF: %s" % e
            if kind == "commit":
                self._send({"type": "placed", "id": rid, "cells": [], "messages": [error(text)]})
            else:
                self._send({"type": "reply", "id": rid, "messages": [error(text)]})
        finally:
            self._cancelled.discard(rid)
            ctx.request_id = None

    # frames in

    def _read(self, source: BinaryIO) -> None:
        try:
            while True:
                text, payload = frame.read_frame(source)
                self._inbox.put((json.loads(text), payload))
        except EOFError:
            pass
        except Exception as e:  # a broken stream: the loop ends, and circuitRF sees the process exit
            _log("The stream from circuitRF broke: %s" % e)
        self._inbox.put(None)

    def _take(self, block: bool) -> Optional[tuple[dict, list[int]]]:
        """One message off the pipe, noting a cancel as it passes (so a running handler can see it)."""
        if self._ended:
            return None
        try:
            item = self._inbox.get(block=block)
        except queue.Empty:
            raise
        if item is None:
            self._ended = True
            return None
        msg = item[0]
        if msg.get("type") == "cancel":
            self._cancelled.add(msg.get("id"))
        return item

    def _drain(self) -> None:
        """Move everything already received to the waiting list, so a cancel is seen without blocking."""
        while True:
            try:
                item = self._take(block=False)
            except queue.Empty:
                return
            if item is None:
                return
            self._waiting.append(item)

    def _next(self) -> Optional[tuple[dict, list[int]]]:
        """The next message to act on. A request already cancelled before it started is answered and skipped."""
        while True:
            if self._waiting:
                item = self._waiting.pop(0)
            else:
                item = self._take(block=True)
                if item is None:
                    return None
            msg = item[0]
            if msg.get("type") in ("update", "action", "event", "commit") and msg.get("id") in self._cancelled:
                self._cancelled.discard(msg.get("id"))
                self._send({"type": "reply", "id": msg.get("id"), "messages": []})
                continue
            return item

    def _await_host_reply(self, rid) -> dict:
        """Wait for circuitRF's hostReply to request ``rid``; anything else that arrives meanwhile waits its turn."""
        while True:
            item = self._take(block=True)
            if item is None:
                raise Cancelled()        # circuitRF has gone; nothing will answer
            msg = item[0]
            if msg.get("type") == "hostReply" and msg.get("id") == rid:
                return msg
            if msg.get("type") == "bye":
                self._ended = True
                raise Cancelled()
            self._waiting.append(item)

    # frames out

    def _send(self, message: dict, payload=()) -> None:
        with self._send_lock:
            frame.write_frame(self._out, json.dumps(message, allow_nan=False), payload)


def _dbu_per_micron(p: Params) -> int:
    """A technology parameter's dbuPerMicron, when one was sent."""
    for v in p.values():
        if isinstance(v, dict) and isinstance(v.get("dbuPerMicron"), int):
            return v["dbuPerMicron"]
    return DEFAULT_DBU_PER_MICRON


def _claim_stdout() -> BinaryIO:
    """Keep the real stdout for frames and point everything else that writes to it at stderr.

    At the descriptor level as well as ``sys.stdout``, so a C extension or a subprocess that inherits descriptor 1
    cannot write into the middle of a frame either.
    """
    sys.stdout.flush()
    try:
        wire = os.fdopen(os.dup(1), "wb", buffering=0)
        os.dup2(2, 1)
    except (OSError, ValueError, AttributeError):
        wire = sys.stdout.buffer
    sys.stdout = sys.stderr
    return wire


def _log(text: str) -> None:
    print(text, file=sys.stderr, flush=True)
