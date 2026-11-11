---
title: Writing a Plugin
slug: reference/writing-plugins.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Writing a Plugin
lede: Everything a plugin author needs - the manifest, the panel, every message, view and host call - complete enough to hand to an AI assistant and get back a plugin that installs and runs.
keywords: plugin, writing a plugin, plugin author, plugin.json, manifest, frame, panel, window plugin, action plugin, host call, lineModel, simulate, Python, C#, NativeAOT, Avalonia, staging folder, Place in workspace, vetted list, plugins/index.json
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#what">What you are writing</a></li>
<li><a href="#smallest">The smallest plugin</a></li>
<li><a href="#manifest">The manifest, <code>plugin.json</code></a></li>
<li><a href="#params">Parameters</a></li>
<li><a href="#panel">The panel: a layout tree</a></li>
<li><a href="#units">Units: base SI, and DBU for shapes</a></li>
<li><a href="#conversation">The conversation</a></li>
<li><a href="#views">Views</a></li>
<li><a href="#host">Host calls: work you ask circuitRF to do</a></li>
<li><a href="#placing">Placing: what <code>commit</code> stages</a></li>
<li><a href="#window">Window plugins</a></li>
<li><a href="#helper">The Python helper, <code>circuitrf_plugin</code></a></li>
<li><a href="#stub">A walk through the stub plugin</a></li>
<li><a href="#compiled">Writing a plugin in C# (or any compiled language)</a></li>
<li><a href="#rules">The rules circuitRF enforces</a></li>
<li><a href="#testing">Testing a plugin</a></li>
<li><a href="#vetted">Getting a plugin on the list</a></li>
<li><a href="#which">Plugin, PCell, or pull request?</a></li>
</ol>
</nav>

This page is the contract. It is written for an engineer who will often hand it to an AI assistant along with one of
the example plugins, so every table here is complete: if a field is not on this page, circuitRF does not read it.
[Using Plugins](plugins.html) covers installing and running them.

## What you are writing {#what}

A plugin is **a separate program** in a folder with a `plugin.json` beside it. circuitRF starts the program, talks to
it over its standard input and output, and never loads its code. That gives you three things for free: your plugin
cannot crash circuitRF, it can be written in any language under any licence, and it does not break when circuitRF's
own code changes — only the messages on this page are the contract.

**circuitRF draws; your plugin describes.** You return numbers, a plot description, shapes in database units, a
netlist. circuitRF draws them with the same renderers, colours, zoom and markers as the rest of the application. Your
plugin writes no drawing code and no mouse code.

There are three kinds:

| `kind` | What the user sees | Use it for |
|---|---|---|
| `panel` | A tool window circuitRF builds from your manifest: your parameters, readouts, buttons and views, recalculated as the user types | Almost everything: synthesis, calculators, generators |
| `action` | A parameter dialog, then one run that places a cell | A generator with no preview |
| `window` | Your own program's window. circuitRF draws nothing and only listens | An existing desktop tool, an instrument front panel, anything the panel vocabulary cannot express |

Every plugin ends the same way: it writes documents into a **staging folder** circuitRF gives it, and circuitRF
**places** them in the workspace — validated, behind a restore point, with the Replace / New name question. A plugin
never writes into the workspace itself.

## The smallest plugin {#smallest}

A folder with two files. `plugin.json`:

```json
{
  "apiVersion": 1,
  "id": "example.quarter-wave",
  "name": "Quarter-Wave Transformer",
  "version": "0.1.0",
  "kind": "panel",
  "run": { "python": { "entry": "qw.py", "requiresPython": ">=3.8" } },
  "params": [
    { "name": "zl", "label": "Load", "kind": "quantity", "unit": "Ohm", "default": "100 Ohm", "min": "1 Ohm" },
    { "name": "zt", "label": "Line impedance", "kind": "readout", "unit": "Ohm" }
  ]
}
```

`qw.py`, using the helper package (copy `tools/plugin-python/circuitrf_plugin/` from the circuitRF repository into the
folder beside it — it is standard library only):

```python
import math
from circuitrf_plugin import Plugin, Reply

plugin = Plugin()

@plugin.update
def update(p, ctx):
    return Reply(readouts={"zt": math.sqrt(50.0 * p.zl)})   # p.zl is the load in ohms, base SI

plugin.run()
```

Link the folder (Settings ▸ Plugins ▸ **Link Folder…**), allow it, and open it from **Tools ▸ Plugins**. With no `ui`
in the manifest you get the default panel: the parameters in a column, then one tab per view your first reply
contains. The same plugin runs headless with `circuitRF plugin run example.quarter-wave --set zl="75 Ohm" -o out`.

## The manifest, `plugin.json` {#manifest}

| Field | Required | Meaning |
|---|---|---|
| `apiVersion` | yes | The message set you wrote against: `1`. A whole number |
| `id` | yes | Unique, reverse-DNS-like: lower-case letters, digits, `.` and `-` only (`yourname.coupled-filter`). The key for permission, the list, the CLI and every result |
| `name` | yes | What the menu shows |
| `version` | yes | Your version, e.g. `0.3.0`. An update is a new version |
| `kind` | yes | `panel`, `action` or `window` |
| `run` | yes | How to start your program, per platform (below) |
| `summary` | | One sentence: the menu tooltip and the list |
| `author`, `license`, `homepage` | | Shown in the permission prompt and Settings |
| `minCircuitRF` | | The oldest circuitRF it needs, e.g. `1.8.0` |
| `menu` | | The submenu of Tools ▸ Plugins it appears under (`Filters`, `Calculators`) |
| `contexts` | | Also offer it from a right-click: any of `cell`, `layout`, `result`, `technology` |
| `params` | | The parameters (next section). Required for a `window` plugin's headless form |
| `ui` | | The panel's layout tree, for `kind: panel`. Leave it out for the default panel |
| `timeouts` | | `{"update": s, "action": s, "commit": s}` in seconds, to replace the deadlines |

An unknown optional field is ignored, never refused, so a manifest written for a later circuitRF still reads.

**`run`** has one entry per platform, keyed by .NET runtime identifier, or one `python` entry that runs everywhere:

| Key | Entry | Started as |
|---|---|---|
| `python` | `{"entry": "my_plugin.py", "requiresPython": ">=3.10"}` | `<python> -u <entry>`, with `PYTHONIOENCODING=utf-8`, on the Python circuitRF finds for kit scripts (the first `python3` on the `PATH`) |
| `win-x64`, `win-arm64`, `win-x86` | `{"exe": "bin/win-x64/tool.exe"}` | The program itself |
| `osx-x64`, `osx-arm64` | `{"exe": "bin/osx-arm64/tool"}` | The program itself |
| `linux-x64`, `linux-arm64` | `{"exe": "bin/linux-x64/tool"}` | The program itself |

Paths are relative to the plugin folder and must stay inside it. The working directory is the plugin folder. A platform
with no entry lists the plugin dimmed with the reason. A Python plugin may ship `requirements.txt` beside the manifest;
circuitRF installs it at install time (see [Using Plugins](plugins.html#python)).

## Parameters {#params}

Parameters are declared once, separately from where the panel shows them, because the command line and MCP need them
with no panel. Each is `{"name", "kind", …}`; a `name` is an identifier (`[A-Za-z_][A-Za-z0-9_]*`) and unique.

| `kind` | Fields | The user gives | Your plugin receives |
|---|---|---|---|
| `quantity` | `unit` (required), `default`, `min`, `max`, `chars` | A value with an SI prefix (`2.4 GHz`, `2.4G`, `2.4e9`) or an expression | `{"value": 2.4e9, "unit": "Hz", "text": "2.4 GHz"}` — `value` in base SI |
| `int` | `default`, `min`, `max`, `chars` | A whole number | The number |
| `real` | `default`, `min`, `max`, `chars` | A number | The number |
| `bool` | `default` | A check box | `true` / `false` |
| `string` | `default`, `chars` | Text | The text |
| `choice` | `options` (required), `default` | A picker | The chosen option's text |
| `file` | `extensions` (e.g. `[".s2p"]`), `chars` | A file picker | The absolute path |
| `substrate` | | A picker over the workspace technologies' conductors | The resolved substrate (below) |
| `technology` | | A picker over the workspace's technologies | `{id, path, dbuPerMicron, layers: [{name, layer, datatype, purpose}], stackup}` — `stackup` as the substrate's, below |
| `cell` | | A picker over the workspace's cells | `{path, name, views}` |
| `result` | | A picker over the workspace's Touchstone files and run `.npy` | `{path}`, plus `ports`, `fmin`, `fmax` for a Touchstone file |
| `readout` | `unit` (optional) | Nothing: your reply fills it | Never sent |

Every parameter takes an optional `label` (what the panel shows; the name stays the identifier).

- **Units** are circuitRF's unit names: `Hz`, `Ohm`, `F`, `H`, `S`, `V`, `A`, `W`, `dB`, `dBm`, `deg`, `metre` and the
  rest of [Units](units.html). Declare the **base** unit: `Hz`, not `GHz` — the user may still type `GHz`. A length is
  `metre`, never `m`, because `m` is milli.
- A `quantity`'s `default`, `min` and `max` are text with a unit (`"2.4 GHz"`); an `int`'s or `real`'s are numbers.
  A value outside `min`/`max` is refused by circuitRF before your plugin is asked.
- A `quantity` may be an expression in the workspace's variables (`2*fc`); you receive the evaluated number. A bare
  number takes the declared unit: `200` for an `Ohm` quantity is 200 Ω.
- A `readout` with no `unit` is shown as a plain number.
- **A picker starts on a choice.** A `choice`, `substrate`, `technology`, `cell` or `result` whose default is not one of
  its choices starts on its first choice, so the panel never shows a value it is not sending.
- **`chars`** is a size hint: about how many characters the box should hold (`4` for an order, `12` for a frequency,
  `40` for a path). circuitRF turns it into a width from its theme's font and clamps it to 2–80.

**The substrate object** — what a `substrate` parameter delivers, and what `resolve substrate` returns:

| Field | Meaning |
|---|---|
| `technology` | The `.ctech` path, absolute — `resolve technology` takes it as `name` for the layer numbers |
| `dbuPerMicron` | The technology's database units per µm, for scaling your shapes |
| `signal`, `ground` | The signal conductor's and the ground reference's names in the stackup |
| `h` | Dielectric height between them, metres |
| `er`, `tanD` | Relative permittivity and loss tangent |
| `t`, `sigma`, `roughness` | Conductor thickness (m), conductivity (S/m), RMS roughness (m) |
| `stripline` | `true` when there is a ground above as well |
| `stackup` | Every stackup entry, top to bottom: `{kind, name, thickness}` with `thickness` in metres and `kind` one of `conductor`, `dielectric` or `via`; `er`, `tanD`, `mur` on a dielectric; `sigma`, `isGround`, `roughness` on a conductor; and on any entry drawn on layers, `layers` — the drawing layer names. Skip a kind you do not use: more may be added |

Shapes in a layout view name **drawing layers** (`Top Copper`), which are not the stackup conductor's name
(`Top Copper (1 oz)`): take the layer from the conductor's `layers`.

## The panel: a layout tree {#panel}

`ui` is `{"title", "size": [width, height], "layout": <node>}`. A node is an object naming one container or one leaf.

```json
"ui": {
  "title": "Single-Stub Match",
  "size": [1180, 760],
  "layout": { "split": "horizontal", "sizes": [1, 3], "children": [
    { "column": [
      { "group": "Load", "children": [ { "param": "f0" }, { "param": "rl" } ] },
      { "readout": "summary", "as": "text" },
      { "messages": true },
      { "button": "Place in workspace", "action": "commit" }
    ]},
    { "tabs": [
      { "title": "Response", "view": "response", "kind": "plot" },
      { "title": "Layout",   "view": "artwork",  "kind": "layout" }
    ]}
  ]}
}
```

**Containers**

| Node | Meaning |
|---|---|
| `{"row": [...]}` / `{"column": [...]}` | Children side by side / stacked |
| `{"group": "Title", "children": [...]}` | A titled box |
| `{"tabs": [{"title": "…", …a leaf or a container}]}` | Tabs; each tab is a node with a `title` added |
| `{"split": "horizontal" \| "vertical", "sizes": [1, 3], "children": [...]}` | A resizable split; one size per child, as proportions |

**Leaves**

| Node | Draws |
|---|---|
| `{"param": "f0"}` | The parameter's label and control. `"label"` and `"chars"` override the declaration's |
| `{"readout": "lengths", "as": "value" \| "table" \| "text"}` | A `readout` parameter your reply fills; `as` defaults to `value`. `"label"` overrides |
| `{"button": "Label", "action": "name"}` | Sends `action {name}`. `"commit"` is the reserved placement action |
| `{"messages": true}` | Your messages for the current parameters |
| `{"view": "name", "kind": "plot" \| "smith" \| "layout" \| "schematic" \| "image" \| "table"}` | A view your reply fills |
| `{"note": "text"}` | Fixed text, one or two lines |

A view name is placed once. An element or view kind this circuitRF does not know is drawn as an empty frame and noted,
not refused. **circuitRF holds the panel's rules, so you cannot break them:** controls never shift (a widget you
disable is greyed, never hidden; an empty view draws an empty frame with the reason), colours and fonts are the theme's,
and the panel remembers its size and split positions per plugin.

## Units: base SI, and DBU for shapes {#units}

**Physical values cross in base SI**: hertz, ohms, metres, farads, seconds. A `quantity` arrives as base SI, a readout
number is read as base SI in its parameter's unit, a table cell is base SI in its column's unit, a plot axis is base SI.
You never format a number for display — circuitRF does, in the user's units — and you never parse `"2.4 GHz"`.

**Layout geometry crosses as integer database units (DBU)**, as int64 in the frame's binary part: 1 DBU is
1/`dbuPerMicron` µm, and `dbuPerMicron` is the technology's (1000 today, so 1 DBU = 1 nm). Convert each coordinate
once, rounding half away from zero (`round(metres · 1e6 · dbuPerMicron)`), and do arithmetic in DBU after that.
The reason is the same as for circuitRF's own layout: a fractional coordinate cannot be expressed, so it can never be
silently rounded by whoever reads it, and a sum of rounded values cannot drift.

## The conversation {#conversation}

### The frame

Every message, both ways, is one **frame** on stdin (circuitRF → you) or stdout (you → circuitRF):

```
[ uint32 jsonLen ][ uint32 binLen ][ jsonLen bytes of UTF-8 JSON ][ binLen bytes of int64 ]
```

Both lengths and every int64 are **little-endian**. `binLen` counts **bytes** (a multiple of 8). The binary part is
empty except in a reply carrying a layout view. [Writing a plugin in C#](#compiled) shows two frames byte by byte.

- **stdout is frames and nothing else.** One stray print corrupts the stream and circuitRF ends the session. Send
  logging to **stderr**: it is your log. circuitRF keeps the last 200 lines and shows them under the panel's messages
  when the plugin fails; `plugin run` prints the last five when the plugin fails or replies with an error.
- **A pipe returns partial reads.** Loop until you have the whole header, then the whole JSON, then the whole payload.
  A reader that treats a short read as the end works in testing and fails under load.
- **Flush after every frame.** circuitRF is waiting for it.
- **End of input is the end.** When stdin closes between frames, exit.

### Messages

Every message is one JSON object with a `"type"` naming it and its fields beside it. Requests from circuitRF carry an
integer `id`, counted up from 1 per session; your answer copies it. A message of a type the other side does not know is
a protocol error, because the version was agreed in `hello`.

**circuitRF → plugin**

| Message | When | You answer |
|---|---|---|
| `hello {id, apiVersion, circuitRF, platform, staging, workspace, context, mode}` | Once, first | `hello {apiVersion}` — the version you will speak (≤ the one offered); it needs no `id` |
| `update {id, params}` | The panel opens, and after any parameter change (debounced 150 ms) | `reply` |
| `action {id, name, params}` | A button | `reply` |
| `event {id, view, kind, …, params}` | A bound marker or handle is dragged, an image is clicked | `reply` |
| `commit {id, params}` | **Place in workspace**, or `plugin run --place` | `placed` |
| `cancel {id}` | A newer request supersedes `id`, or the user pressed Stop | Nothing required — see below |
| `bye` | The panel closes, or circuitRF exits | Exit within 2 s, or you are killed |
| `hostReply {id, …}` | The answer to your `host` call | — |
| `placedReply`, `openReply` | The answers to a window plugin's `placed` and `open` | — |

`hello`'s fields: `platform` is this machine's runtime identifier; `staging` is your staging folder (absolute); `workspace`
is the workspace folder, or null; `context` is where you were opened from — `{workspace, cell | layout | technology |
result, path}` when it was a right-click; `mode` is `interactive` (a window) or `headless` (`circuitRF plugin run`).

**plugin → circuitRF**

| Message | Fields |
|---|---|
| `reply` | `id`, and any of: `readouts` `{name: value}`, `views` `{name: view or null}`, `messages` `[{severity, text}]`, `enabled` `{name: false}`, `set` `{param: value}` |
| `placed` | `id`, `cells: [{name, files, layout?}]`, optional `messages` |
| `progress` | `id`, `fraction` (0–1, optional), `text` (optional) — before your reply, shown on the panel's busy bar |
| `host` | `id` (the request you are answering), `call`, and its arguments — before your reply ([Host calls](#host)) |

**`params`** in `update`, `action`, `event` and `commit` is an object keyed by parameter name, each value as
[Parameters](#params) says it arrives:

```json
{ "type": "update", "id": 4,
  "params": { "f0": { "value": 2.4e9, "unit": "Hz", "text": "2.4 GHz" }, "order": 5, "substrate": { "technology": "…", "signal": "Top Copper (1 oz)", "h": 7.62e-4, "…": "…" } } }
```

**What a reply's fields mean.**

- `readouts`: keyed by `readout` parameter. A number is base SI, shown with an SI prefix in that parameter's `unit`;
  `{"value", "unit"}` names its own unit; a string is shown as written; a table readout takes a [table](#table).
- `views`: keyed by view name. A view left out keeps what it showed; `null` clears it.
- `messages`: `severity` is `error`, `warning` or `info`.
- `enabled`: names parameters and buttons (a button by its `action`); a name left out is enabled. It is complete each
  time, not a change.
- `set`: changes parameters' values as typing them would, without another `update` for them.

**Events.** `kind: "marker"` carries `marker` (its index in the plot's `markers`) and `x` (base SI); `kind: "handle"`
carries `param` and `value` (base SI, solved by circuitRF); `kind: "click"` carries `x` and `y` in the image's own pixel
coordinates. A marker or handle changes its parameter: `params` already holds the new value, and the ordinary `update`
follows your reply. **If you have nothing particular to do for an event, answer it exactly as you answer `update`.**

**Deadlines.** An `update` has 30 s; an `action` or `commit` 10 min (`timeouts` replaces them). Past it the panel says
the plugin is not responding and offers Stop. Put anything slow behind a button (`action`), not in `update`.

**Cancel.** circuitRF sends `cancel {id}` when a newer request supersedes one, and ignores whatever you then answer to
it, so a slow plugin never draws an old answer over a new one. You may ignore `cancel` and still be correct; honouring
it only saves time. After the user presses **Stop**, a plugin that has answered nothing it was told to cancel within 2 s
is restarted — so **answer every request, even a cancelled one** (an empty `reply {id}` is enough).

**One process per panel.** It lives until the panel closes. If it exits unexpectedly the panel says so, keeps the last
views and starts it again on the next change, with a fresh `hello`.

**Be a function of your parameters.** The same parameters must give the same reply and the same staged bytes. circuitRF
relies on it: the panel and `plugin run` must agree, and a test can call your plugin with no display.

## Views {#views}

### `plot` and `smith` {#plot}

```json
"response": {
  "data": {
    "axes":  { "freq": { "unit": "Hz", "values": [2.0e9, 2.01e9] } },
    "cubes": { "S11": { "axes": ["freq"], "kind": "complex", "re": [0.1, 0.09], "im": [-0.2, -0.19] } }
  },
  "plot": {
    "y": { "label": "dB", "min": -40, "max": 0 },
    "traces":  [ { "cube": "S11", "y": "db20", "label": "S11" } ],
    "markers": [ { "trace": "S11", "bind": "f0" } ],
    "lines":   [ { "y": -20, "side": "above", "xStart": 2.3e9, "xEnd": 2.5e9 } ]
  }
}
```

- **`data`** is named axes (`unit`, `values`) and named cubes over them: `kind: "complex"` with `re` and `im`, or
  `kind: "real"` with `values`. A large result is a staged file instead: `"data": {"file": "response.s2p"}` (a
  Touchstone file or `.npy` in your staging folder).
- **`plot`** fields: `title`; `x`, `y`, `y2` (each `{label, min, max}` — `x` in base SI, `y` in the plot's own units);
  `freqUnit` (`GHz`…); `legend`; `traces`; `markers`; `lines`. Any other field is refused by name.
- **A trace** is `cube` plus any of `y` (`db20`, `db10`, `mag`, `phase`, `real`, `imag`, `conj`, `none`), `i` and `j`
  (1-based port numbers into an S-matrix cube), `label`, and `axis` (`left` or `right`). These are `circuitRF plot
  --trace`'s fields. **`"y": "db"` is refused**: say `db20` for a voltage ratio or `db10` for a power.
- **A marker** sits on `trace`, at `x` (base SI, or a parameter's name) or **bound** to a `quantity` parameter with
  `"bind": "f0"` — the user drags it and the parameter follows, as if typed. The bound parameter's unit must be the
  plot's x unit. A marker sits on a sample of its trace, so put round values on your axis if you want it to land on
  round values.
- **A line** is a limit area: `y`, `side` (`above` hatches above it: the curve must stay below; `below` the reverse),
  optional `xStart`/`xEnd` in base SI.
- **`smith`** is the same object on a Smith chart: cubes are reflection coefficients or impedances; a bound marker is
  bound to a frequency parameter.

The user can zoom, add markers, export, and **Open in Data Display**, which writes your data and a `.cdd` into the
staging folder and opens it as an ordinary document. None of that is your plugin's business, and you are not told.

### `table` {#table}

```json
"dims": { "columns": ["Section", "Width", "Length"], "units": ["", "metre", "metre"],
          "rows": [["Feed", 1.61e-3, 3e-3], ["Stub", 1.61e-3, 29.0e-3]] }
```

Numbers in base SI; circuitRF formats each column in its unit. `""` is a plain number or text.

### `layout` {#layout}

```json
"artwork": {
  "technology": "self",
  "shapes":  [ { "kind": "rect", "layer": "Top Copper", "xy": { "at": 0, "count": 4 } } ],
  "handles": [ { "parameter": "trim", "kind": "linear", "span": { "at": 4, "count": 4 }, "axisDeg": 90, "label": "Stub length" } ],
  "ports":   [ { "name": "1", "x": 0, "y": 0, "layer": "Top Copper", "width": 1610000, "outwardDeg": 180 } ]
}
```

**Every shape and handle coordinate is a span into the frame's int64 payload**: `{"at": i, "count": n}`, in int64
elements, `n` even (x, y pairs). A port's `x`, `y` and `width`, and lengths that are not coordinates (a path width, a
radius), are JSON integers in DBU, refused with a fractional part.

`technology` is `"self"` (the technology your `technology` or `substrate` parameter names) or a `.ctech` path relative to
the workspace. A `layer` is a drawing layer's **name** (`"Top Copper"`) or `{"layer": 1, "datatype": 0}`. A layer the
technology does not have is drawn on a fallback colour and named in the messages.

| `kind` | `xy` holds | Other fields |
|---|---|---|
| `rect` | 4 values: x1, y1, x2, y2 | — |
| `poly` | The outline, ≥ 3 points | `holes`: a list of spans |
| `path` | The centreline, ≥ 2 points | `width` (DBU), `end`: `flush`, `round`, `square` or `extended` |
| `rrect` | 4 values: x1, y1, x2, y2 | `cornerRadius` (DBU) |
| `circle` | 2 values: the centre | `radius` (DBU) |
| `via` | 2 values: the centre | `padSize`, `drillSize` (DBU), optional `landingLayer` |
| `label` | 2 values: the anchor | `text`, `height` (DBU), `rotation` (`R0`, `R90`, `R180`, `R270`) |
| `curve` | The outline | `edges` (one per vertex: `{"kind": "line"}` or `{"kind": "arc", "bulge": tan(sweep/4)}`), `holes` |

Every shape also takes an optional `net`.

**Handles** are grips the user drags to change a `quantity` parameter. `span` is exactly 4 values: **anchor x, anchor y,
grip x, grip y**. `kind` is `linear` (a length along `axisDeg`, measured from the anchor) or `angular` (an angle about
the anchor). Optional: `label`, `min`, `max` (parameter values, base SI — not coordinates). circuitRF solves the new value
itself and sends an `event {kind: "handle", param, value}`: your plugin never maps a mouse position to a length. A
handle on any other parameter kind is drawn and not draggable.

**Ports** are drawn as layout port labels and become real ports when the layout is placed: `name` (the port number as
text), `x`, `y`, `layer`, `width` (DBU), and `outwardDeg` — the direction pointing **out of** the conductor (180 for a
line's left end, 0 for its right).

The preview is read-only artwork with rulers, pan, zoom, hover position and shift-drag measurement.

### `schematic`

```json
"circuit": { "cnl": "circuit.cnl" }
```

A `.cnl` you wrote into the staging folder ([The netlist format](netlist.html)), drawn by circuitRF read-only.

### `image`

```json
"chart": { "file": "groupdelay.svg", "clickable": true }
```

A PNG or SVG in the staging folder, for a picture circuitRF has no view kind for. With `clickable`, a click sends
`event {view, kind: "click", x, y}` in the image's own pixel coordinates (an SVG's user units). No hover, drag or zoom:
this is the escape hatch, not a drawing API.

## Host calls: work you ask circuitRF to do {#host}

While answering a request, send `host {id, call, …}` with **that request's id**, then read until `hostReply` with the
same id arrives — its fields beside `type` and `id`, as the table's last column lists them (other messages, such as a `cancel`, may arrive first: keep them for afterwards). Several host calls may
precede one reply, one at a time. A failed call is `hostReply {id, error: "<sentence>"}` and the session carries on.
Results land in your staging folder, never in the workspace.

| `call` | Arguments | Returns |
|---|---|---|
| `lineModel` | `line`: `microstrip`, `stripline` or `cpwg`; `substrate`: the object a `substrate` parameter delivered, or `"conductor : technology path"`; `width` (m); `gap` (m, for `cpwg`); `freq`: Hz, a number or an array | `{z0: [...], eeff: [...], alphaDbPerM: [...], messages}`, real numbers, one entry per frequency — circuitRF's own model of that line. `messages` is `[{severity, text}]`, as in a reply: pass them on |
| `simulate` | `cnl`: a staged `.cnl`; `analysis`: its analysis name (optional when the file has one) | `{result: <staged .npy>, touchstone: <staged .sNp, for a plain S-parameter run>, cubes: [names], messages}` |
| `resolve` | `what`: `technology`, `substrate`, `cell` or `reference`; `name` (a technology path; a substrate as `"conductor : technology path"`, the path absolute or relative to the workspace; a cell folder); for a reference, `ref` and `from` | `{resolved}`: the same object a parameter of that kind delivers. A `technology` with no `name` is the workspace's default |
| `evaluate` | `expr`; optional `unit` (a base unit); optional `path` (a document whose variables are in scope) | `{value, unit}` |

- `line`, not `type`: every message's `type` is the message's own.
- `cpw` is refused: circuitRF's coplanar line is `cpwg`, with a ground plane below.
- **`simulate` refuses EM, loadpull and pursuit**: a plugin update must not start a run measured in minutes. It is given
  nine tenths of its request's deadline; a slow sweep belongs behind a button.
- Use them. `lineModel` and `simulate` make your plugin agree with what the user gets after placement, instead of
  computing a private approximation that disagrees with circuitRF's own models.

## Placing: what `commit` stages {#placing}

On **Place in workspace** (or `plugin run --place`) circuitRF sends `commit`. Write your documents into the staging
folder and answer `placed {id, cells: [{name, files, layout?}]}`:

- `name` is the suggested cell name (any text without `< > : " / \ | ? *` or control characters). `files` are paths relative to the staging folder; each extension has one home in
  the cell, and a cell has at most one file of each:

  | Extension | Placed in | Checked by |
  |---|---|---|
  | `.cnl` | `schematic/` | It must read and elaborate |
  | `.csch` | `schematic/` | circuitRF's schematic reader |
  | `.csym` | `symbol/` | circuitRF's symbol reader |
  | `.clay` | `layout/` | circuitRF's layout reader |
  | `.c3d` | `3d/` | circuitRF's 3D view reader |
  | `.cem` | `em/` | It must read, and its layout must resolve |

- **`layout`: name a `layout` view instead of staging a `.clay`.** `"layout": "artwork"` has circuitRF write the
  artwork view of your last reply, ports as port labels, as the cell's `layout/artwork.clay` — what was drawn is what
  is placed, and you never write `.clay` JSON. A cell has one layout: a staged `.clay` *and* `layout` is refused.
- **A `.cem` may name that view as the file it will be beside it:** `"LayoutRef": "artwork.clay"` in a staged `.cem`
  is re-pointed at the placed layout. A `.cem`'s ports are the layout's port labels, so a placed cell opens ready for
  an EM run.

**To fail a placement**, answer `placed` with no cells and an `error` message saying why; nothing is placed and the
message is shown. The placed cell is a folder named after the cell holding a `.ccell` and its view folders.
circuitRF validates every file, asks for the name (Replace / New name when it exists), takes a restore point named
after your plugin and version, and places the cell. Any failure places nothing and lists every reason.

### The netlist, in brief {#cnl}

A `.cnl` is one part per line: `Type:Name node node … Param=value …`. Node `0` is ground; any other node is a name
you choose. A value carries its unit (`50 Ohm`, `3 mm`, `1.5 pF`); a length is written in `mm` or `um`, since `m` is
milli. A line starting with `;` is a comment. Stage a netlist with its `analysis` line, so the placed cell runs as it is. The parts a
plugin most often needs:

| Line | Is |
|---|---|
| `Port:P1 in 0 Num=1 Z=50 Ohm` | Port 1 between `in` and ground, 50 Ω reference |
| `R:R1 a b R=100 Ohm`, `L:L1 a b L=2 nH`, `C:C1 a b C=1 pF` | A resistor, inductor, capacitor |
| `TLIN:TL1 a b Z=50 Ohm L=12.3 mm Eeff=2.87` | An ideal transmission line of impedance `Z`, physical length `L` and effective permittivity `Eeff` |
| `fc = 2.4 GHz` | A variable other values can use |
| `analysis SP1 type=sparam start=1 stop=6 npts=51 Unit=GHz` | An S-parameter sweep (`start`/`stop` in `Unit`) |

[The netlist format](netlist.html) has every part and analysis; `circuitRF reference` prints them all.

### A `.clay`, in brief {#clay}

A panel plugin never writes one: it names its layout view in `placed`. A **window** plugin has no view to name, so it
stages a `.clay` (the Python helper's `clay_text` writes one from a `Layout`). A line with a port at each end:

```json
{
  "FormatVersion": 1,
  "DbuPerMicron": 1000,
  "Shapes": [
    { "$type": "Rect", "X1": 0, "Y1": -805000, "X2": 18439000, "Y2": 805000, "Layer": { "Layer": 1, "Datatype": 0 } },
    { "$type": "Label", "X": 0, "Y": 0, "Text": "1", "Height": 805000, "Rotation": "R0",
      "IsPort": true, "PortDirection": "R0", "PortLayer": { "Layer": 1, "Datatype": 0 }, "Layer": { "Layer": 1, "Datatype": 0 } },
    { "$type": "Label", "X": 18439000, "Y": 0, "Text": "2", "Height": 805000, "Rotation": "R0",
      "IsPort": true, "PortDirection": "R180", "PortLayer": { "Layer": 1, "Datatype": 0 }, "Layer": { "Layer": 1, "Datatype": 0 } }
  ],
  "Instances": []
}
```

- Coordinates are integer DBU at `DbuPerMicron`. A layer is `{Layer, Datatype}` **numbers**, from the technology's
  `layers` (`resolve technology` gives them by name).
- Shapes: `Rect` (`X1, Y1, X2, Y2`), `Poly` (`Xy`: a flat `[x0, y0, x1, y1, …]`, optional `Holes`: a list of such),
  `Path` (`Xy`, `Width`, `End`: `Flush`, `Round`, `Square` or `Extended`), `Label` (`X, Y, Text, Height, Rotation`).
- **A port is a `Label` with `IsPort: true`**: its text is the port number, its `PortLayer` the conductor's layer, and
  **`PortDirection` points INTO the conductor** — `R0` at a line's left end, `R180` at its right — the opposite of a
  layout view's `outwardDeg`. The port's width is the conductor's; `Height` is only the label's text size.
- **`TechRef`** names the technology relative to the placed file, `<workspace>/<cell>/layout/` — so a technology at
  `<workspace>/tech/board.ctech` is `"../../tech/board.ctech"` from any cell. Leave it out for the workspace's default.
- `check` notes that port labels "carry no manufacturable area": that is expected of every port label.

## Window plugins {#window}

A `window` plugin gets the same `hello` and then circuitRF draws nothing and only listens. Whenever **your** user asks,
send, with an `id` of your own (count up from 1):

| You send | circuitRF answers |
|---|---|
| `placed {id, cells}` — placed exactly as above | `placedReply {id, cells: [the names they were placed under]}` or `placedReply {id, error}` |
| `open {id, path}` — a document in your staging folder or the workspace | `openReply {id}` or `openReply {id, error}` |
| `host {id, call, …}` | `hostReply`, as above, with no deadline |

Your ids are your own: count up from 1, one per message, and match each answer by it; they never collide with
circuitRF's. Between `hello` and `bye` circuitRF sends a window plugin nothing but these answers. `placed` cannot
name a layout view (a window has no reply for it to come from): stage a `.clay`. A window gets no parameters: to read
a value the user typed with a unit, ask `evaluate` with `unit` (`{"expr": "2.4G", "unit": "Hz"}` → 2.4e9).

There is one instance per plugin: choosing it again while it runs says it is already running (circuitRF cannot raise
another program's window). When circuitRF exits it sends `bye`, waits 2 s, then kills. A window plugin that exits by
itself has been closed by its user.

**A headless form.** If the manifest declares `params` and your program answers `update` (readouts) and `commit`
(placed) when `hello`'s `mode` is `headless` — opening no window — then `circuitRF plugin run` and MCP can run it.
Without one, `plugin run` refuses it, saying it "has no headless form". The headless `update` reply is a panel's reply:
readouts, messages, and views too, which `-o` writes as files. Make both forms call the same code, and stage a `.clay`
in both, so they stage the same bytes for the same parameters.

## The Python helper, `circuitrf_plugin` {#helper}

`tools/plugin-python/circuitrf_plugin/` in the circuitRF repository: copy the folder into your plugin. It depends on
nothing but the standard library (Python 3.8+), handles the frame, `hello`, cancel, progress, host calls and `bye`, and
**redirects `print` to stderr** (at the descriptor, so a library writing to stdout cannot corrupt a frame either).

```python
from circuitrf_plugin import Plugin, Reply, Placed, Plot, Smith, Layout, Table, Schematic, Netlist, error, warning, info

plugin = Plugin()

@plugin.update                      # every parameter change; also answers an event or action with no handler
def update(p, ctx): ...             # -> Reply

@plugin.action("simulate")          # a button whose action is "simulate"
def simulate(p, ctx): ...           # -> Reply

@plugin.event                       # optional: a dragged marker or handle, or an image click
def event(p, ctx, ev): ...          # ev is the message: ev["view"], ev["kind"], ...

@plugin.commit                      # Place in workspace
def commit(p, ctx): ...             # -> Placed(cells, messages) or a list of cells

plugin.run()
```

| | |
|---|---|
| `p["f0"]` | The parameter as delivered (`{"value", "unit", "text"}` for a quantity) |
| `p.f0` | A quantity's `value` in base SI; any other kind's value as delivered |
| `ctx.staging`, `ctx.workspace`, `ctx.mode`, `ctx.headless`, `ctx.context`, `ctx.hello` | From `hello` |
| `ctx.stage(name, text_or_bytes)` | Writes a file in the staging folder (UTF-8, `\n` line ends); refuses a name outside it; returns the name |
| `ctx.path(name)` | The absolute path of a staged file |
| `ctx.dbu(metres)` | Metres to DBU, rounded once, half away from zero, at the `dbuPerMicron` of the request's `technology` or `substrate` parameter (1000 otherwise; set `ctx.dbu_per_micron` to override) |
| `ctx.progress(fraction, text)` | The busy bar; also a cancellation point |
| `ctx.check()` | Raises `Cancelled` once the request is cancelled — call it in long loops. Let it propagate: the helper answers the cancelled request |
| `ctx.line_model(line, substrate, width, freq, gap=None)` | `lineModel` |
| `ctx.simulate(cnl, analysis=None)`, `ctx.resolve(what, name=None, ref=None, from_=None)`, `ctx.evaluate(expr, unit=None, path=None)` | The other host calls; a failure raises `HostError` with circuitRF's sentence |
| `Reply(readouts, views, messages, enabled, set)` | A reply; a message is `error(t)`, `warning(t)`, `info(t)` or a plain string |
| `Plot(title, x, y, y2, freq_unit, legend)` / `Smith(…)` | All optional (`y={"label": "dB", "min": -40, "max": 0}`). `.axis(name, unit, values)`, `.cube(name, axes, values)` (complex values become re/im), `.data_file(staged)`, `.share_data(other)`, `.trace(cube, y=…, label=…)`, `.marker(trace, bind=… or x=…, draggable=…)`, `.line(y, side, x_start, x_end)` |
| `Table(columns, units, rows)` | `.row(*cells)` |
| `Layout(technology="self")` | `.rect`, `.polygon`, `.path`, `.circle`, `.via`, `.label`, `.port(name, x, y, layer, width, outward_deg)`, `.handle(parameter, anchor, grip, axis_deg, …)` — **every coordinate an `int`; a float is refused** with a message naming `ctx.dbu` |
| `Schematic(cnl)`, `Image(file, clickable)` | The other views |
| `Netlist()` | `.part(kind, name, *nodes, **params)`, `.analysis(name, **keys)`, `.line(text)`, `.text()` — a `.cnl` with no hand-written lines |
| `clay_text(layout, technology, tech_ref=None)` | A `.clay` from a `Layout`, for a `window` or `action` plugin with no view to name |

A handler that raises is reported in the panel as an error message, and the session carries on.

## A walk through the stub plugin {#stub}

`tools/plugin-examples/stub/` is the reference plugin: a single open stub matching a load to 50 Ω on a microstrip
substrate. Its arithmetic is textbook, so every number in it can be checked by hand, and it uses every view kind. It
speaks the frame itself rather than through the helper, so it shows the whole contract in one file.

**`plugin.json`.** Six parameters — `f0`, `rl` and `xl` (quantities in `Hz` and `Ohm`), `substrate`, `trim` (a length in
`metre`, the stub's handle) and `summary` (a text readout). The `ui` is a horizontal split: the inputs, the readout, the
messages and two buttons (**Simulate in circuitRF** sends `action simulate`; **Place in workspace** is `commit`) on the
left; five tabs on the right — a response plot, a Smith chart, a dimensions table, the layout and the schematic.

**`stub.py`**, top to bottom:

1. **The frame**: `read_exact` loops over partial reads; `read_frame` reads one message; `send` writes one and flushes.
   `WAITING` keeps messages that arrive while a host call is answered, so `read_message` returns them in order.
2. **`host()`** sends a host call carrying the request's id and reads until its `hostReply`.
3. **The line**: `solve_width` asks `lineModel` for the impedance of a guessed width (the closed-form microstrip
   equations give the first guess) and runs a secant search on ln(width) to 50 Ω, then rounds the width to a whole DBU
   and asks once more for the effective permittivity *at that width*. So the line it draws is circuitRF's 50 Ω line.
4. **The match**: `design()` turns the load into a line length `d` and a stub length `l` (the formulas are in the file's
   header), plus the trim.
5. **The views**: `sweep()` puts the response on a 1-2-5 step so a dragged marker lands on round frequencies; `data()`
   is the S11 cube; `artwork()` returns the shapes and the payload — two rectangles, a `linear` handle on `trim` along
   90°, and a port at each end of the line; `netlist()` is the `.cnl`.
6. **`reply()`** answers `update`, `event` and `action` alike: every view, a one-line summary, and `enabled` greying
   Place when the trim makes the stub negative.
7. **`simulate()`** is the button: `simulate` on the staged `circuit.cnl`, whose S11 is drawn beside the closed form's.
8. **`commit`** stages `stub.cnl`, `stub.csch` (the same circuit drawn: `schematic()` lays the parts out on the grid)
   and `stub.cem`, and answers `placed` with `"layout": "artwork"` — so the cell gets the artwork as `layout/artwork.clay`
   and the `.cem` naming it, and opens ready for an EM run.

## Writing a plugin in C# (or any compiled language) {#compiled}

Nothing about a plugin is Python-specific. A compiled program needs the frame, the messages on this page, and a `run`
entry per platform. `tools/plugin-examples/csharp-window/` is a complete one: a C# program with its own Avalonia window,
published with NativeAOT to one native program per platform, with a headless form. It is the template for an author
who already has a .NET desktop tool and wants it to place cells instead of exporting interchange files.

### The frame, byte by byte

The answer to `hello` — `{"type":"hello","apiVersion":1}`, 31 bytes of JSON, no payload:

```
00000000: 1f 00 00 00  00 00 00 00  7b 22 74 79 70 65 22 3a   ........{"type":
00000010: 22 68 65 6c  6c 6f 22 2c  22 61 70 69 56 65 72 73   "hello","apiVers
00000020: 69 6f 6e 22  3a 31 7d                               ion":1}
```

`1f 00 00 00` is jsonLen = 31, little-endian; `00 00 00 00` is binLen = 0. A frame with a payload —
`{"type":"reply","id":2}` (23 bytes) carrying the two int64 values 1000 and −500:

```
00000000: 17 00 00 00  10 00 00 00  7b 22 74 79 70 65 22 3a   jsonLen 23, binLen 16
00000010: 22 72 65 70  6c 79 22 2c  22 69 64 22 3a 32 7d e8   ..."id":2}  then 1000 = e8 03 00 00 00 00 00 00
00000020: 03 00 00 00  00 00 00 0c  fe ff ff ff ff ff ff      -500 = 0c fe ff ff ff ff ff ff
```

The JSON is UTF-8; non-ASCII text such as `Ω` may be written as UTF-8 or escaped (`Ω`).

### `run`, per platform

A compiled plugin ships one program per runtime identifier it supports, each in its own folder, and names them in
`run`:

```json
"run": {
  "win-x64":   { "exe": "bin/win-x64/line-section.exe" },
  "win-arm64": { "exe": "bin/win-arm64/line-section.exe" },
  "osx-arm64": { "exe": "bin/osx-arm64/line-section" },
  "osx-x64":   { "exe": "bin/osx-x64/line-section" },
  "linux-x64": { "exe": "bin/linux-x64/line-section" }
}
```

A platform you leave out lists the plugin dimmed there, saying why. A native library your program loads (a UI
toolkit's, say) sits beside it in the same folder.

### `window` or `panel`?

**A panel is the default choice even in C#**: circuitRF draws it, it looks like the rest of the application, and it
runs headless for free. Your program is then a loop that answers `update` and `commit`. Choose **`window`** when the tool
already has a window you want to keep, or needs interaction the panel vocabulary cannot express (a live tuner, a drawing
editor, an instrument front panel). A window plugin should also have a **headless form** ([above](#window)), or it
cannot run from the command line or an agent.

### NativeAOT's two traps

NativeAOT compiles your program to one native executable with no .NET runtime to install, which is what a plugin
wants. Two things go wrong silently:

1. **Reflection-based JSON is trimmed away.** `JsonSerializer.Serialize(obj)` without type information compiles, runs
   under `dotnet run`, and throws at run time in the published program — on the first message. Use **System.Text.Json
   source generation**: declare your message types on a `JsonSerializerContext` and pass its type info to every call.
   (Reading with `JsonDocument` and writing with `Utf8JsonWriter` is AOT-safe too, and simpler for passing an object
   such as the substrate straight through.) Set `IsAotCompatible` in the project so the build warns about anything
   else that would be trimmed.
2. **NativeAOT cannot cross operating systems.** It links with the platform's own native toolchain (Xcode's
   command-line tools on macOS; clang on Linux; Visual Studio's C++ workload on Windows), so each operating system's
   programs are published **on that operating system**. Cross-architecture on the same OS (arm64 → x64 on macOS) works.
   A `run` entry with no program behind it fails for whoever installs it, not for you: publish every RID you list.

### A walk through `csharp-window/`

It makes a microstrip line section of a given impedance and electrical length. Every file stands on its own:

| File | What it holds |
|---|---|
| `Frame.cs` | The frame codec, BCL only, written to be copied into another program unchanged: little-endian header and payload, partial reads looped, a clean end of stream between frames returned as null, and a stream ending inside a frame an error |
| `Protocol.cs` | The messages as classes on a source-generated `JsonSerializerContext` (`PluginJson`); `ReadHello`; one background reader thread; `AskAsync`, which sends a message with an id and completes when circuitRF's answer with that id arrives; `ResolveAsync` and `LineModelAsync` |
| `LineSection.cs` | The deterministic part: the parameters and circuitRF's line model in, the three documents out. A secant search on ln(width) finds the impedance, the width is rounded to a whole DBU, and length = (degrees/360) · c / (f · √εeff). Both the window and the headless form call it, so they stage identical bytes |
| `MainWindow.cs` | An Avalonia window built in code (no XAML): a conductor picker, frequency, impedance and electrical length, **Place in workspace** (`placed`), **Open in circuitRF** (`open` for the placed `.clay`) and a status line. Every message is awaited, so the UI thread never waits on the pipe |
| `Program.cs` | `Main` keeps stdout for frames and points `Console.Out` at stderr **before anything else runs** (the C# version of the helper's print redirect), reads `hello`, and opens the window or runs the headless form by `mode` |
| `plugin.json` | `kind: window`, the parameters of the headless form (including `substrate`) and its readouts, and a `run` entry per RID |
| `publish.sh`, `publish.ps1` | One `dotnet publish -c Release -r <rid> -p:PublishAot=true -o bin/<rid>` per RID; each refuses a RID of another operating system rather than skipping it |
| `LineSection.csproj` | `net10.0`, `IsAotCompatible`, `InvariantGlobalization`, and Avalonia. It references nothing in the circuitRF repository; empty `Directory.Build.props`/`.targets` beside it stop MSBuild reading the repository's own |

The window asks `resolve` (no name) for the workspace's default technology and offers its signal conductors. As the
user types, it asks `lineModel` for each width the search tries. **Place in workspace** stages `line.clay` (the
rectangle, and a port label at each end facing into the line), `line.cnl` (an ideal line of circuitRF's modelled
impedance and εeff between two ports) and `line.cem` (naming `line.clay`, both ports at the line's impedance), then
sends `placed`. Headless, the same code answers `update` with the width, length, εeff and modelled impedance as
readouts, and `commit` with `placed`.

**To make it yours:** rename the project, `AssemblyName`, `id` and the `run` paths; replace `LineSection.cs` with your
tool's computation (keep it a function of its inputs); replace `MainWindow.cs`'s controls with your own window; keep
`Frame.cs` and the message classes as they are, adding a class to `PluginJson` for each new message you send.

## The rules circuitRF enforces {#rules}

- **Write only into the staging folder.** A file named outside it, in `placed` or a view, is refused. Placement is the
  only way into the workspace.
- **stdout carries frames only.** Anything else ends the session with a protocol error.
- **Answer every request**, within its deadline, with the id it carried, by the type expected (`reply`, or `placed` for
  `commit`). An answer to an id you were never sent is a protocol error.
- **Exit within 2 s of `bye`**, or the process and everything it started is killed.
- **Be deterministic**: the same parameters, the same reply and the same staged bytes.
- **No network access unless a parameter or button says so**, and no background process that outlives yours.

A plugin that crashes, hangs, writes garbage or never answers leaves circuitRF responsive; the panel says what happened
and shows your stderr.

## Testing a plugin {#testing}

- **Link Folder…** (Settings ▸ Plugins) runs your folder in place and re-reads the manifest on every start: edit, close
  the panel, open it again. Allow it once.
- **Keep staging** (a check box on an action plugin's dialog) keeps the staging folder after the run and names it in
  Messages, so you can read what your plugin wrote.
- **`circuitRF check plugin.json`** (or the folder) reports every manifest error with the field it is in, including the
  panel's layout tree.
- **`circuitRF plugin describe <id>`** shows the manifest as circuitRF read it, and how it will run on this machine.
- **`circuitRF plugin run <id> --set name=value … --workspace <ws> -o <dir>`** sends `hello` (`mode: headless`) and one
  `update`, and writes every view as a file: a plot as its data and a `.cdd`, a layout as a `.clay`, a schematic as its
  `.cnl`, a table as CSV, the readouts as `readouts.json` (also printed). Your messages go to stderr, with the last five
  lines of your own stderr when you fail or reply with an error.
  - **`--workspace`** is the workspace parameters resolve against. A `substrate`, `technology`, `cell` or `result`
    parameter needs one.
  - **`--set`** takes what the panel's box takes: a quantity with or without a prefix or unit, a choice's option, and a
    substrate as `"Top Copper (1 oz) : tech/board.ctech"` or just its conductor's name when that names one. A
    parameter you leave out starts where the panel starts it. An unknown name or a value out of range is refused,
    listing what is allowed.
  - **`--place <workspace>`** then sends `commit` and places what you stage — the same placement the panel's button
    does. `--cell <name>` names the cell; a name that is taken is refused unless you add `--replace` or
    `--new-name <name>`.
  - Exit code 0; 1 for a refusal, an error message from your plugin, or a failure; 130 when cancelled.
  - Your stderr is not shown when a run succeeds. To look at what circuitRF sent you while developing, put it in a
    readout: it is printed and written to `readouts.json`.
- `CRF_PLUGIN_PATH` (folders separated by `:` on macOS and Linux, `;` on Windows) links plugin folders for one run of
  circuitRF, without touching Settings.

The command line cannot grant permission: allow the plugin once in the window, and `plugin run` uses that permission.

## Getting a plugin on the list {#vetted}

circuitRF's Settings ▸ Plugins lists the plugins in `plugins/index.json` in the circuitRF repository. **A version is
vetted, not a plugin**: each release goes on the list by its own pull request.

1. **Release it** from your repository with a tag (`v0.3.0`) and a zip asset holding the plugin folder — `plugin.json`
   at the top of the zip, or in one folder at its top. Leave out your build folders (`bin/`, `obj/` of a project kept
   inside the plugin folder): ship only what `run` names and what it loads. One zip for every platform is named in the list as `any`; or one
   zip per runtime identifier. Build each zip with a tool that keeps Unix permissions (`zip -r` on macOS or Linux), or a
   native program arrives without its execute bit and cannot start.
2. **Take its SHA-256**: `shasum -a 256 plugin-0.3.0.zip` (macOS, Linux) or `Get-FileHash plugin-0.3.0.zip` (Windows).
3. **Open a pull request** to circuitRF adding your version to `plugins/index.json`:

```json
{
  "id": "yourname.coupled-filter",
  "name": "Coupled-Line Filter",
  "summary": "Edge-coupled bandpass filters on a microstrip substrate.",
  "repo": "https://github.com/yourname/crf-coupled-filter",
  "versions": [
    { "version": "0.3.0", "tag": "v0.3.0", "minCircuitRF": "1.8.0", "apiVersion": 1,
      "assets": { "any": { "url": "https://github.com/yourname/crf-coupled-filter/releases/download/v0.3.0/plugin-0.3.0.zip",
                           "sha256": "…" } },
      "reviewed": "" }
  ]
}
```

Asset URLs must be on `github.com`. circuitRF checks the download against `sha256` before unpacking anything.

**What the review checks**:
- the release asset is built from the tagged source, and the source is readable;
- it writes only into its staging folder;
- it reaches the network only when a parameter or button says so;
- it starts no background process that outlives it;
- it names no commercial EDA tool or design kit, in code, manifest or text;
- its licence is stated.

Being on the list means **that version** was reviewed. It is not an endorsement of the author or of later versions.

## Plugin, PCell, or pull request? {#which}

| If the tool… | It is a… |
|---|---|
| is one parameterised layout cell that should stay live in a layout | **PCell** ([PCells](pcells.html)) |
| is a device model | **external device or Verilog-A** ([Verilog-A](veriloga.html)) |
| synthesises, calculates, imports or reports, and its author wants to own it | **plugin** |
| is broadly useful, maintained, and fits circuitRF's standards | **pull request**, often after it proved itself as a plugin |
