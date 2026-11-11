# circuitrf_plugin — the Python helper for circuitRF plugins

Write a circuitRF plugin in Python without writing the frame, the conversation or the document formats. Copy the
`circuitrf_plugin/` folder into your plugin beside your script; it depends on nothing but the standard library
(Python 3.8+). It is not published to a package index.

```python
from circuitrf_plugin import Plugin, Reply, Plot, Layout, Table

plugin = Plugin()

@plugin.update
def update(p, ctx):
    view = Plot().axis("freq", "Hz", freqs).cube("S11", ["freq"], s11).trace("S11", y="db20")
    return Reply(readouts={"z0": 50.2}, views={"response": view})

@plugin.commit
def commit(p, ctx):
    ctx.stage("stub.cnl", netlist_text)
    return [{"name": "stub", "files": ["stub.cnl"], "layout": "artwork"}]

plugin.run()
```

It handles the frame (partial reads, little-endian lengths, the int64 payload), `hello`, `cancel` (`ctx.check()` raises
`Cancelled`), `progress`, `bye` and the host calls (`ctx.simulate`, `ctx.resolve`, `ctx.evaluate`, `ctx.line_model`). It
points `print` — and anything else writing to descriptor 1 — at stderr before any handler runs, so a stray print cannot
corrupt the stream. Layout builders take integer database units and refuse a float, with `ctx.dbu(metres)` to convert a
length once. `Netlist` writes a `.cnl` from a list of parts and `clay_text` a `.clay` from shapes.

The full contract, and the helper's API, are in the user guide's
[Writing a Plugin](../../docs/user/src/reference/writing-plugins.md).

## Tests

```sh
python3 -m unittest discover -s tests
```

Frame round trip, partial reads, a stream ending inside a frame, cancel, host calls, a stray print, float coordinates
refused. circuitRF's `tests/Ui.Tests/Plugins/PluginFrameGoldenTests.cs` runs them in its routine gate, and also decodes
the C# example's golden frames with this package's reader.
