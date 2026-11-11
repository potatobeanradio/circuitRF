"""circuitrf_plugin's own tests. Run from tools/plugin-python:

    python3 -m unittest discover -s tests

Each test is one claim the package makes; none needs circuitRF.
"""

import io
import json
import os
import struct
import subprocess
import sys
import textwrap
import threading
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, ROOT)

from circuitrf_plugin import (Cancelled, Layout, Netlist, Placed, Plot, Plugin, Reply, Table, clay_text,  # noqa: E402
                              read_frame, write_frame)
from circuitrf_plugin.frame import FrameError  # noqa: E402
from circuitrf_plugin.plugin import Context  # noqa: E402


class Trickle(io.RawIOBase):
    """A stream that hands back one byte per read, as a busy pipe can."""

    def __init__(self, data: bytes):
        self._data = data
        self._at = 0

    def readable(self):
        return True

    def read(self, n=-1):
        if self._at >= len(self._data):
            return b""
        self._at += 1
        return self._data[self._at - 1:self._at]


class FrameTests(unittest.TestCase):
    def test_a_frame_round_trips_its_json_and_its_int64_payload(self):
        buf = io.BytesIO()
        write_frame(buf, json.dumps({"type": "reply", "text": "Ω µm"}, ensure_ascii=False), [0, -1, 2 ** 62])
        raw = buf.getvalue()
        json_len, bin_len = struct.unpack("<II", raw[:8])
        self.assertEqual(24, bin_len)
        self.assertEqual(len(raw), 8 + json_len + bin_len)
        text, payload = read_frame(io.BytesIO(raw))
        self.assertEqual({"type": "reply", "text": "Ω µm"}, json.loads(text))
        self.assertEqual([0, -1, 2 ** 62], payload)

    def test_partial_reads_are_looped_not_taken_for_the_end(self):
        buf = io.BytesIO()
        write_frame(buf, '{"type":"update","id":7}', [5, 6])
        write_frame(buf, '{"type":"bye"}')
        stream = Trickle(buf.getvalue())
        self.assertEqual(('{"type":"update","id":7}', [5, 6]), read_frame(stream))
        self.assertEqual(('{"type":"bye"}', []), read_frame(stream))
        with self.assertRaises(EOFError):
            read_frame(stream)

    def test_a_stream_ending_inside_a_frame_is_an_error_not_an_end(self):
        buf = io.BytesIO()
        write_frame(buf, '{"type":"update"}', [1])
        with self.assertRaises(FrameError):
            read_frame(io.BytesIO(buf.getvalue()[:-3]))

    def test_a_payload_that_is_not_whole_int64s_is_refused(self):
        raw = struct.pack("<II", 2, 7) + b"{}" + b"\0" * 7
        with self.assertRaises(FrameError):
            read_frame(io.BytesIO(raw))


class Session:
    """A Plugin.run on a thread, with pipes standing in for circuitRF's end."""

    def __init__(self, plugin: Plugin):
        to_plugin_r, self._to_plugin_w = os.pipe()
        self._from_plugin_r, from_plugin_w = os.pipe()
        self.inp = os.fdopen(self._to_plugin_w, "wb", buffering=0)
        self.out = os.fdopen(self._from_plugin_r, "rb", buffering=0)
        plugin_in = os.fdopen(to_plugin_r, "rb", buffering=0)
        plugin_out = os.fdopen(from_plugin_w, "wb", buffering=0)
        self.thread = threading.Thread(target=lambda: (plugin.run(plugin_in, plugin_out), plugin_out.close(), plugin_in.close()),
                                       daemon=True)
        self.thread.start()

    def send(self, msg, payload=()):
        write_frame(self.inp, json.dumps(msg), payload)

    def receive(self):
        text, payload = read_frame(self.out)
        return json.loads(text), payload

    def hello(self, staging="/tmp", mode="interactive"):
        self.send({"type": "hello", "id": 1, "apiVersion": 1, "staging": staging, "mode": mode})
        return self.receive()[0]

    def close(self):
        self.send({"type": "bye"})
        self.thread.join(5)
        self.inp.close()
        self.out.close()


class ConversationTests(unittest.TestCase):
    def test_hello_is_answered_with_the_version_and_an_update_with_its_reply(self):
        plugin = Plugin()

        @plugin.update
        def update(p, ctx):
            return Reply(readouts={"double": 2 * p.f0}, messages=["note"])

        s = Session(plugin)
        self.assertEqual({"type": "hello", "apiVersion": 1}, s.hello())
        s.send({"type": "update", "id": 2, "params": {"f0": {"value": 1e9, "unit": "Hz", "text": "1 GHz"}}})
        reply, _ = s.receive()
        self.assertEqual("reply", reply["type"])
        self.assertEqual(2, reply["id"])
        self.assertEqual(2e9, reply["readouts"]["double"])
        self.assertEqual([{"severity": "info", "text": "note"}], reply["messages"])
        s.close()

    def test_cancel_raises_Cancelled_at_the_next_check_and_the_request_is_still_answered(self):
        plugin = Plugin()
        started = threading.Event()

        @plugin.update
        def update(p, ctx):
            if p.n == 1:
                ctx.progress(0.0, "working")
                while True:
                    ctx.check()
            return Reply(readouts={"n": p.n})

        s = Session(plugin)
        s.hello()
        s.send({"type": "update", "id": 2, "params": {"n": 1}})
        progress, _ = s.receive()
        self.assertEqual({"type": "progress", "id": 2, "fraction": 0.0, "text": "working"}, progress)
        s.send({"type": "cancel", "id": 2})
        cancelled, _ = s.receive()
        self.assertEqual({"type": "reply", "id": 2, "messages": []}, cancelled)   # dropped by circuitRF, but settles it
        s.send({"type": "update", "id": 3, "params": {"n": 2}})
        self.assertEqual(2, s.receive()[0]["readouts"]["n"])
        s.close()

    def test_a_host_call_carries_the_request_id_and_returns_circuitRFs_answer(self):
        plugin = Plugin()

        @plugin.update
        def update(p, ctx):
            m = ctx.line_model("microstrip", "Top Copper : tech/a.ctech", 1.5e-3, [1e9])
            return Reply(readouts={"z0": m["z0"][0]})

        s = Session(plugin)
        s.hello()
        s.send({"type": "update", "id": 4, "params": {}})
        call, _ = s.receive()
        self.assertEqual({"type": "host", "id": 4, "call": "lineModel", "line": "microstrip",
                          "substrate": "Top Copper : tech/a.ctech", "width": 1.5e-3, "freq": [1e9]}, call)
        s.send({"type": "hostReply", "id": 4, "z0": [50.5], "eeff": [2.9], "alphaDbPerM": [0.1], "messages": []})
        self.assertEqual(50.5, s.receive()[0]["readouts"]["z0"])
        s.close()

    def test_a_host_error_is_reported_as_a_message_and_the_session_carries_on(self):
        plugin = Plugin()

        @plugin.update
        def update(p, ctx):
            ctx.evaluate("nope")
            return Reply()

        s = Session(plugin)
        s.hello()
        s.send({"type": "update", "id": 2, "params": {}})
        s.receive()
        s.send({"type": "hostReply", "id": 2, "error": "nope is not defined."})
        reply, _ = s.receive()
        self.assertEqual([{"severity": "error", "text": "circuitRF: nope is not defined."}], reply["messages"])
        s.close()

    def test_commit_answers_placed_and_a_layout_views_coordinates_ride_the_payload(self):
        plugin = Plugin()

        @plugin.update
        def update(p, ctx):
            art = Layout().rect("Top Copper", 0, -5, 100, 5).port("1", 0, 0, "Top Copper", 10, 180)
            art.handle("L", anchor=(0, 0), grip=(100, 0), axis_deg=0)
            return Reply(views={"artwork": art, "dims": Table(["L"], ["metre"], [[1e-3]])})

        @plugin.commit
        def commit(p, ctx):
            return Placed([{"name": "line", "files": ["line.cnl"], "layout": "artwork"}], ["placed one"])

        s = Session(plugin)
        s.hello()
        s.send({"type": "update", "id": 2, "params": {}})
        reply, payload = s.receive()
        self.assertEqual([0, -5, 100, 5, 0, 0, 100, 0], payload)
        self.assertEqual({"at": 0, "count": 4}, reply["views"]["artwork"]["shapes"][0]["xy"])
        self.assertEqual({"at": 4, "count": 4}, reply["views"]["artwork"]["handles"][0]["span"])
        s.send({"type": "commit", "id": 3, "params": {}})
        placed, _ = s.receive()
        self.assertEqual({"type": "placed", "id": 3, "cells": [{"name": "line", "files": ["line.cnl"], "layout": "artwork"}],
                          "messages": [{"severity": "info", "text": "placed one"}]}, placed)
        s.close()

    def test_a_handler_that_raises_is_an_error_message_not_a_dead_plugin(self):
        plugin = Plugin()

        @plugin.update
        def update(p, ctx):
            return Reply(readouts={"x": 1 / p.d})

        s = Session(plugin)
        s.hello()
        s.send({"type": "update", "id": 2, "params": {"d": 0}})
        self.assertIn("ZeroDivisionError", s.receive()[0]["messages"][0]["text"])
        s.send({"type": "update", "id": 3, "params": {"d": 4}})
        self.assertEqual(0.25, s.receive()[0]["readouts"]["x"])
        s.close()


class StrayPrintTests(unittest.TestCase):
    def test_print_in_a_handler_goes_to_stderr_and_never_into_a_frame(self):
        script = textwrap.dedent("""
            import os, sys
            sys.path.insert(0, %r)
            from circuitrf_plugin import Plugin, Reply
            plugin = Plugin()

            @plugin.update
            def update(p, ctx):
                print("a stray print")
                os.write(1, b"a stray write to descriptor 1\\n")
                return Reply(readouts={"ok": 1})

            plugin.run()
        """ % ROOT)
        buf = io.BytesIO()
        write_frame(buf, json.dumps({"type": "hello", "id": 1, "apiVersion": 1, "staging": ".", "mode": "headless"}))
        write_frame(buf, json.dumps({"type": "update", "id": 2, "params": {}}))
        write_frame(buf, json.dumps({"type": "bye"}))
        run = subprocess.run([sys.executable, "-I", "-c", script], input=buf.getvalue(), capture_output=True, timeout=30)
        out = io.BytesIO(run.stdout)
        self.assertEqual("hello", json.loads(read_frame(out)[0])["type"])
        self.assertEqual({"ok": 1}, json.loads(read_frame(out)[0])["readouts"])
        with self.assertRaises(EOFError):
            read_frame(out)
        self.assertIn(b"a stray print", run.stderr)
        self.assertIn(b"a stray write to descriptor 1", run.stderr)


class GeometryTests(unittest.TestCase):
    def test_a_float_coordinate_is_refused_and_says_how_to_convert(self):
        art = Layout()
        with self.assertRaises(TypeError) as e:
            art.rect("Top Copper", 0, 0, 1.5, 10)
        self.assertIn("ctx.dbu(metres)", str(e.exception))
        with self.assertRaises(TypeError):
            art.path("Top Copper", [(0, 0), (10, 0)], 2.0)
        with self.assertRaises(TypeError):
            art.port("1", True, 0, "Top Copper", 10, 180)

    def test_dbu_rounds_once_half_away_from_zero(self):
        ctx = Context(Plugin(), {"staging": "."})
        self.assertEqual(1_500_000, ctx.dbu(1.5e-3))           # 1000 DBU per micrometre
        self.assertEqual(3, ctx.dbu(2.5e-9))
        self.assertEqual(-3, ctx.dbu(-2.5e-9))
        self.assertEqual(2, ctx.dbu(1.5e-9))
        with self.assertRaises(TypeError):
            ctx.dbu("1 mm")

    def test_stage_refuses_a_name_outside_the_staging_folder(self):
        ctx = Context(Plugin(), {"staging": HERE})
        with self.assertRaises(ValueError):
            ctx.stage("../escape.cnl", "x")


class DocumentTests(unittest.TestCase):
    def test_a_netlist_is_one_line_per_part_in_order(self):
        net = Netlist().part("Port", "P1", "in", "0", Num=1, Z="50 Ohm").part("R", "R1", "in", "0", R=49.9)
        net.analysis("SP1", type="sparam", start=1, stop=2, npts=11, Unit="GHz")
        self.assertEqual("Port:P1 in 0 Num=1 Z=50 Ohm\nR:R1 in 0 R=49.9\n"
                         "analysis SP1 type=sparam start=1 stop=2 npts=11 Unit=GHz\n", net.text())

    def test_a_clay_names_layer_numbers_and_writes_ports_as_port_labels_facing_in(self):
        tech = {"dbuPerMicron": 1000, "layers": [{"name": "Top Copper", "layer": 1, "datatype": 0}]}
        art = Layout().rect("Top Copper", 0, -500, 9000, 500).port("1", 0, 0, "Top Copper", 1000, 180)
        doc = json.loads(clay_text(art, tech))
        rect, label = doc["Shapes"]
        self.assertEqual({"Layer": 1, "Datatype": 0}, rect["Layer"])
        self.assertTrue(label["IsPort"])
        self.assertEqual("R0", label["PortDirection"])            # outward 180 degrees: it faces +x, into the line
        self.assertEqual(1000, label["Height"])
        with self.assertRaises(ValueError):
            clay_text(Layout().rect("Bottom Copper", 0, 0, 1, 1), tech)


if __name__ == "__main__":
    unittest.main()
