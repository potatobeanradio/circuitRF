"""circuitrf_plugin - write a circuitRF plugin in Python without writing the frame, the conversation or the formats.

Copy this folder into your plugin, beside your script. It depends on nothing but the standard library (Python 3.8+).

::

    from circuitrf_plugin import Plugin, Reply, Plot, Layout, Table

    plugin = Plugin()

    @plugin.update
    def update(p, ctx):
        return Reply(readouts={"z0": 50.2}, views={"response": Plot(...), "artwork": Layout(...)})

    @plugin.commit
    def commit(p, ctx):
        ctx.stage("stub.cnl", netlist_text)
        return [{"name": "stub", "files": ["stub.cnl"], "layout": "artwork"}]

    plugin.run()

The contract this implements is docs/user/src/reference/writing-plugins.md.
"""

from .documents import Netlist, clay_text
from .frame import FrameError, read_frame, write_frame
from .plugin import (API_VERSION, Cancelled, Context, HostError, Params, Placed, Plugin, Reply, error, info,
                     warning)
from .views import Image, Layout, Plot, Schematic, Smith, Table, coord

__all__ = [
    "API_VERSION", "Cancelled", "Context", "FrameError", "HostError", "Image", "Layout", "Netlist", "Params", "Placed",
    "Plot", "Plugin", "Reply", "Schematic", "Smith", "Table", "clay_text", "coord", "error", "info", "read_frame",
    "warning", "write_frame",
]
