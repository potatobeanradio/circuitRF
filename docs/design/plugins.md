# circuitRF — plugins: tools other people write, that circuitRF draws

**Status:** proposed · **Date:** 2026-11-09 · **Briefs:** `docs/briefs/brief-plg-0-overview.md` and PLG-1…7 ·
**Reads with:** `automation-architecture.md` (capabilities → adapters → clients), `pcell-wire-schema.md` (frames,
DBU on the wire, editor hints, host services), `pcell-parameter-handles.md`, `pdk-external-devices.md` §4 (the device
worker's frames), `data-display.md` (the `.cdd` model), `cli.md`, `auto-update.md` (signed downloads, install layouts)

---

## 0. What this is

A **plugin** is a tool somebody else writes and maintains, in its own repository, that a circuitRF user can install
and remove without rebuilding circuitRF. A filter synthesiser is the first one anyone has asked for (GitHub
discussion #51). It is not the only shape: an impedance calculator, a coupler or balun generator, a spiral inductor
generator, a datasheet-to-model fitter, a lab-instrument importer, or a report writer are all the same kind of
thing.

What they share decides the design. Each one:
- takes **inputs** — numbers with units, choices, a substrate, a cell, a result file;
- shows the user **something to judge** — a response plot, a Smith chart, a layout drawing, a schematic, a table;
- reacts when the user **changes an input or drags something** in those views;
- and in the end **writes circuitRF documents** — a `.clay`, a `.csch`/`.cnl`, a `.cem`, an `.sNp`.

**What a plugin is NOT.** It is not code loaded into circuitRF. It is not a way to change how circuitRF's own
editors, engines or file formats behave. It does not draw: it describes, and circuitRF draws.

---

## 1. The decisions everything else follows from

**P-1. A plugin is a separate program, never loaded into circuitRF's process.**
- **Crash isolation.** A stack overflow or a native-code crash in a loaded assembly ends the whole application,
  and no .NET load context can contain either. A separate process that dies is a message in a panel.
- **No version coupling.** A plugin compiled against circuitRF's internal classes, or against Avalonia, breaks on
  most releases. A plugin that speaks a versioned message format does not.
- **No UI framework across the boundary.** circuitRF can be re-skinned by replacing `src/Ui`
  (`ui-architecture.md`). A plugin that shipped XAML or a control would tie every plugin to this skin.
- **Licensing stays clean.** circuitRF is MIT. A plugin under any licence, GPL included, talks to it over a pipe
  and is never linked into it.
- **Any language.** A Python script, a self-contained .NET program, Rust, C. Nothing has to be compiled against
  circuitRF.

**P-2. circuitRF draws every view, from the documents and data a plugin returns.** A plot is the Data Display's
`.cdd` model plus data. A layout is shapes in the PCell wire's vocabulary, drawn by `LayoutRenderer`. A schematic is
a `.cnl`, drawn the way `netlist --toSchematic` draws one. The plugin author writes no drawing code and no mouse
code; the user gets zoom, markers, dark mode and export that work as they do everywhere else.

**P-3. The panel is declared, not programmed.** The manifest holds a layout tree of a small, fixed widget
vocabulary (§4). There is no scripting language and no arbitrary canvas. A tool that does not fit the vocabulary
uses its own window (§8) or belongs in circuitRF itself.

**P-4. A plugin is a function of its parameters.** The same parameters give the same reply. That is the PCell
contract's determinism rule (`pcell-contract.md` R5) for the same reasons: circuitRF may cache, a test can call
the plugin with no display, and the CLI and MCP get the same answer the window shows.

**P-5. circuitRF writes the workspace; a plugin never does.** A plugin writes into a **staging folder** circuitRF
gives it for the session. On **Place in workspace**, circuitRF validates what is there (`CellViewFileValidator`,
`check`'s validators), takes a history checkpoint (`GitCheckpoint`) and moves it in through the same functions New
Cell and Import use (`CellCreate`, `ComponentImport`). So a plugin cannot leave a half-written cell, cannot
overwrite a cell without the Replace / New name question, and every placement can be undone from History.

**P-6. Permission is per user and never travels.** As with kit scripts (`PCellTrustStore`), whether a plugin may run
is recorded in this installation's preferences, keyed by the plugin's installed directory, and never in a `.cws`. An
installed plugin is asked about once, before it first runs. Nothing about a plugin runs before that: listing reads
the manifest only.

**P-7. Every plugin with a declared panel is also a headless capability.** `circuitRF plugin run <id> --set
f0="2.4 GHz" … -o <dir>` and the MCP `plugin` tool call the same host code the window calls
(`automation-architecture.md` R-aut-2). A panel-only feature is a defect.

**P-8. The vocabulary is a public contract, so it starts small.** Every widget, view kind, event and host service
is something circuitRF supports from then on. Each one is added because a real plugin needs it, and the manifest's
`apiVersion` says which set a plugin was written against.

---

## 2. Where it lives

| Part | Project | Why |
|---|---|---|
| Manifest model + reader, discovery, process host, frames, messages, staging, placement, trust store model, index reader, installer | `src/Design/Plugins/` | Below the firewall: the CLI and MCP need all of it |
| The panel: widgets, views, the plugin window, Tools ▸ Plugins, the trust prompt, Settings ▸ Plugins | `src/Ui/Plugins/` | Draws |
| `plugin` verb | `src/Cli/Plugin.cs` | Argument parsing and reporting only, on `Authoring.cs`' terms |
| `plugin` MCP tool | `src/Cli/Serve/` | Adapter only |
| Python helper package | `tools/plugin-python/circuitrf_plugin/` | Like `tools/pcell-python`; references nothing in the repo |
| Example plugins used by the tests | `tools/plugin-examples/` | Independent implementations of the contract |
| The vetted list | `plugins/index.json` at the repo root | Data, reviewed by PR |

`.cws`, `.clay`, `.csch` and every other document format are unchanged by this design.

---

## 3. The manifest — `plugin.json`

A plugin is a folder holding `plugin.json` and whatever the program needs.

```json
{
  "apiVersion": 1,
  "id": "example.coupled-line-filter",
  "name": "Coupled-Line Filter",
  "version": "0.3.0",
  "summary": "Edge-coupled bandpass filters on a microstrip substrate.",
  "author": "Example Author",
  "license": "MIT",
  "homepage": "https://github.com/example/crf-coupled-line-filter",
  "minCircuitRF": "1.8.0",

  "run": {
    "python": { "entry": "filter_plugin.py", "requiresPython": ">=3.10" },
    "win-x64":   { "exe": "bin/win-x64/filter.exe" },
    "osx-arm64": { "exe": "bin/osx-arm64/filter" }
  },

  "kind": "panel",
  "menu": "Filters",
  "params": [ … ],
  "ui": { … }
}
```

- **`id`** is reverse-DNS-like and unique. It is the key everywhere: trust, the index, the CLI, results.
- **`run`** names one way to start the program per platform (`win-x64`, `win-arm64`, `win-x86`, `osx-x64`,
  `osx-arm64`, `linux-x64`, `linux-arm64`), or `python`, which runs on every platform through the interpreter
  `PythonInterpreterDiscovery` finds (§9.3). A platform with no entry lists the plugin dimmed, with the reason.
- **`kind`** is `panel` (circuitRF draws the panel from `ui`), `action` (a parameter dialog, then one run — no
  panel), or `window` (the plugin opens its own window, §8).
- **`menu`** groups plugins under Tools ▸ Plugins. `contexts` (optional) also offers the plugin from a right-click:
  `["cell", "layout", "result", "technology"]`.

### 3.1 Parameters

Parameters are declared once, separately from where they appear, because the CLI and MCP need them with no panel.

```json
"params": [
  { "name": "type",    "kind": "choice",   "options": ["Chebyshev", "Butterworth"], "default": "Chebyshev" },
  { "name": "order",   "kind": "int",      "min": 1, "max": 9, "default": 5 },
  { "name": "f0",      "kind": "quantity", "unit": "Hz", "default": "2.4 GHz", "min": "1 MHz" },
  { "name": "bw",      "kind": "quantity", "unit": "Hz", "default": "200 MHz" },
  { "name": "ripple",  "kind": "quantity", "unit": "dB", "default": "0.1 dB" },
  { "name": "substrate","kind": "substrate" },
  { "name": "lengths", "kind": "readout" }
]
```

| `kind` | What the user gives | What the plugin receives |
|---|---|---|
| `quantity` | A value with an SI prefix, or an expression in the workspace's variables (`2*fc`) | `{"value": 2.4e9, "unit": "Hz", "text": "2.4 GHz"}` — base SI, evaluated by circuitRF's one expression engine |
| `int`, `real`, `bool`, `string`, `choice` | The obvious control | The value |
| `file` | A picker (`…`), with `extensions` | An absolute path |
| `substrate` | A picker over the workspace technology's dielectric/conductor stackups | The resolved stackup: layer names, thicknesses, εr, tan δ, conductivity, roughness — as `SubstrateResolver` resolves it |
| `technology` | A picker over the workspace's technologies | The technology id, its `dbuPerMicron`, its layers and stackup |
| `cell` | A picker over the workspace's cells | The cell folder path and its views |
| `result` | A picker over results (`.sNp`, run `.npy`) | The path, and for a Touchstone file its port count and band |
| `readout` | Nothing — the plugin fills it | Not sent |

Names are identifiers. Units are circuitRF's unit names. A `quantity` with a `min`/`max` is refused by circuitRF
before the plugin is asked.

**Lengths are physical here, and geometry is not.** A plugin computes physics, so a design length arrives and is
returned in metres. The **shapes** a plugin returns for a layout view (§5.3) are int64 DBU, with `dbuPerMicron`
from the technology parameter — the PCell wire's version-2 rule unchanged: the plugin scales its own numbers, and a
fractional coordinate cannot be expressed.

---

## 4. The panel — a layout tree

`ui.layout` is a tree. Containers hold children; leaves place a parameter, a readout, a button or a view.

```json
"ui": {
  "title": "Coupled-Line Filter",
  "size": [1200, 780],
  "layout": { "split": "horizontal", "sizes": [1, 3], "children": [
    { "column": [
      { "group": "Specification", "children": [
        { "param": "type" }, { "param": "order" }, { "param": "f0" }, { "param": "bw" }, { "param": "ripple" }
      ]},
      { "group": "Substrate", "children": [ { "param": "substrate" } ] },
      { "readout": "lengths", "as": "table" },
      { "messages": true },
      { "button": "Place in workspace", "action": "commit" }
    ]},
    { "tabs": [
      { "title": "Response",  "view": "response",  "kind": "plot" },
      { "title": "Smith",     "view": "match",     "kind": "smith" },
      { "title": "Layout",    "view": "artwork",   "kind": "layout" },
      { "title": "Schematic", "view": "circuit",   "kind": "schematic" }
    ]}
  ]}
}
```

### 4.1 Containers

| Node | Meaning |
|---|---|
| `{"row": [...]}` / `{"column": [...]}` | Children side by side / stacked |
| `{"group": "Title", "children": [...]}` | A titled box, like the analysis editor's groups |
| `{"tabs": [{"title", …leaf or container}]}` | Tabs |
| `{"split": "horizontal"\|"vertical", "sizes": [..], "children": [..]}` | A resizable split; sizes are proportions |

### 4.2 Leaves

| Node | Draws |
|---|---|
| `{"param": "f0"}` | The parameter's label and control, from its declaration. `"label"` overrides the label |
| `{"readout": "lengths", "as": "value"\|"table"\|"text"}` | A value the plugin fills in each reply |
| `{"button": "Label", "action": "name"}` | Sends `action`; `"commit"` is the reserved placement action (§6) |
| `{"messages": true}` | The plugin's errors, warnings and notes for the current parameters |
| `{"view": "name", "kind": "plot"\|"smith"\|"layout"\|"schematic"\|"image"\|"table"}` | A view the plugin fills (§5) |
| `{"note": "text"}` | Fixed text, one or two lines |

**Rules circuitRF holds, so a plugin cannot break them:**
- Controls never shift. A widget that the plugin disables (`"enabled": false` in a reply) is greyed, never hidden,
  and a view with nothing to show draws an empty frame with the reason.
- A parameter whose valid values can be listed is a picker, never a text box.
- Colours, fonts and spacing are circuitRF's theme. A plugin names no colour except a trace's (§5.1), and that
  is optional.
- The panel is a top-level tool window, as Match Designer and railRF are, and remembers its size and split
  positions per plugin.

A manifest with no `ui` and `kind: panel` gets a default panel: the parameters in one column, then one tab per
view the plugin's first reply contains.

---

## 5. Views — what the plugin returns, and what circuitRF draws

Each reply carries a `views` object, keyed by view name. A view absent from a reply keeps what it showed; a view
set to `null` is cleared.

### 5.1 `plot` and `smith` — the Data Display, given data

```json
"response": {
  "data": {
    "axes": { "freq": { "unit": "Hz", "values": [2.0e9, 2.01e9, …] } },
    "cubes": {
      "S21": { "axes": ["freq"], "kind": "complex", "re": […], "im": […] },
      "S11": { "axes": ["freq"], "kind": "complex", "re": […], "im": […] }
    }
  },
  "plot": {
    "y": { "label": "dB", "min": -60, "max": 0 },
    "traces": [ { "cube": "S21", "y": "db" }, { "cube": "S11", "y": "db" } ],
    "markers": [
      { "trace": "S21", "x": "f0", "draggable": true, "bind": "f0" },
      { "trace": "S21", "x": 2.3e9 }
    ],
    "lines": [ { "y": -20, "label": "spec" } ]
  }
}
```

- **The data is a `DataSet`**, in the shape `ResultDocument` already reads: named axes, named single-kind cubes.
  A large result is a file instead — `"data": {"file": "response.s2p"}` or `.npy`, a path inside the staging
  folder.
- **The `plot` object is the `.cdd` model's own fields** (`DataDisplayConfig`: axes, traces, markers, limit
  lines), with `cube=`/`y=` meaning what they mean in `circuitRF plot --trace`. Anything the Data Display reads is
  available; nothing new is invented for plugins.
- **The user can do everything the Data Display allows** — zoom, add markers, read values, export the picture or
  the data, open it as a real Data Display. Those are circuitRF's, not the plugin's, and the plugin is not told.
- **A bound marker** (`"bind": "f0"`) sits at the parameter's value. Dragging it sends an `event` (§6) and changes
  the parameter, exactly as typing a new value would. A marker can be bound only to a `quantity` parameter whose
  unit is the plot's x unit.
- `smith` is the same object on a Smith chart; its cubes are reflection coefficients or impedances, as the Smith
  plot type already accepts. A bound marker on a Smith chart is bound to a frequency parameter.

### 5.2 `table`

```json
"lengths": { "columns": ["Section", "W", "S", "L"], "units": ["", "m", "m", "m"], "rows": [["1", 6.1e-4, 2.0e-4, 1.87e-2], …] }
```

circuitRF formats each column in the workspace's display units, so the plugin never formats a number for display.
A `readout` with `"as": "table"` takes the same object.

### 5.3 `layout` — shapes in the PCell vocabulary

```json
"artwork": {
  "technology": "self",
  "shapes": "payload",
  "layers": ["TopCopper", "Ground"],
  "handles": [
    { "kind": "linear", "param": "L3", "from": [x0, y0], "to": [x1, y1], "shape": 7 }
  ],
  "ports": [ { "name": "P1", "at": [x, y], "layer": "TopCopper", "width": 1200 } ]
}
```

- **Shapes are the PCell wire's reply** (`pcell-wire-schema.md` §4.3–4.4): rectangles, polygons, paths and
  labels on technology layers by name, coordinates as int64 DBU in the frame's binary payload. circuitRF
  draws them with `LayoutRenderer` in the technology's colours, with rulers, pan, zoom and the layer panel's
  visibility. A layer the technology does not have is drawn on the fallback palette and named in the messages.
- **Handles are the PCell wire's editor hints** (§4.5, version 7): `linear` and `angular` handles bound to a
  parameter. Dragging one sends an `event` with the new parameter value, computed by circuitRF's existing handle
  solver, so the plugin never maps a mouse position to a length.
- **Ports** are drawn as layout port labels and become real ports on placement (§6).
- The preview is **read-only artwork**, like a PCell instance. Editing it by hand is what placement is for.

### 5.4 `schematic` — a `.cnl`

```json
"circuit": { "cnl": "circuit.cnl" }
```

The plugin writes a `.cnl` into the staging folder; circuitRF draws it as `netlist --toSchematic` draws one. This
needs that drawing code moved out of `src/Cli` into `src/Design` (PLG-3) — by R-aut-2 it should already be there.

### 5.5 `image` — the escape hatch

```json
"chart": { "file": "groupdelay.svg", "clickable": true }
```

A PNG or SVG the plugin drew itself, for a picture circuitRF has no view kind for. With `clickable`, a click sends
`event {view, kind: "click", x, y}` in the image's own pixel coordinates. This is the only way a plugin draws, and
it is deliberately limited: no hover, no drag, no zoom beyond the picture's.

---

## 6. The conversation

### 6.1 Frames

The frame is the PCell wire's (`pcell-wire-schema.md` §2), so `tools/pcell-python` and `tools/plugin-python` share
a codec and the int64 geometry payload comes for free:

```
[ uint32 jsonLen ][ uint32 binLen ][ jsonLen bytes UTF-8 ][ binLen bytes of int64 ]
```

On stdin and stdout. **stderr is the plugin's log**: circuitRF keeps the last 200 lines, shows them under the
panel's messages when the plugin fails, and writes them to the run log. A plugin that prints to stdout outside a
frame is a protocol error (the Python helper redirects `print` for exactly this reason).

### 6.2 Messages

Every request has an `id`. Every reply names the `id` it answers.

| circuitRF → plugin | When | Plugin replies |
|---|---|---|
| `hello {apiVersion, circuitRF, platform, staging, workspace, context}` | Once, after start | `hello {apiVersion}` — the version it will speak |
| `update {id, params}` | The panel opens, and after any parameter change (debounced 150 ms) | `reply {id, readouts, views, messages, enabled}` |
| `action {id, name, params}` | A button | `reply` |
| `event {id, view, kind, …, params}` | A bound marker or handle is dragged, an image is clicked | `reply`, optionally with `set: {param: value}` |
| `commit {id, params}` | **Place in workspace** | `placed {id, cells: [{name, files}]}` — the documents in the staging folder |
| `cancel {id}` | A newer request supersedes `id`, or the user pressed Stop | Nothing required |
| `bye` | The panel closes | The process exits within 2 s, or is killed |

| plugin → circuitRF, before its reply | Meaning |
|---|---|
| `progress {id, fraction, text}` | Shown on the panel's busy bar |
| `host {id, call, …}` | A host service (§7); circuitRF answers `hostReply {id, …}` |

- **Stale replies are dropped.** circuitRF sends `cancel` for a superseded request and ignores its reply, so a slow
  plugin never draws an old answer over a new one.
- **A plugin may ignore `cancel`** and still be correct; honouring it only saves time.
- **Deadlines.** An `update` gets 30 s, an `action` or `commit` 10 min (`"timeouts"` in the manifest overrides).
  Past it the panel says the plugin is not responding and offers Stop. The window never blocks on a plugin.
- **One process per open panel.** It lives until the panel closes. If it exits unexpectedly the panel says so,
  keeps the last views, and restarts it on the next change.
- **`context`** in `hello` says where the plugin was opened from: the workspace, and the selected cell, layout
  selection or result when it came from a right-click.

### 6.3 Placement

`commit` asks the plugin for its documents. circuitRF then:
1. validates every file with the validator its kind already has, and refuses with the plugin's messages and its
   own if any fails;
2. asks for a cell name, with Replace / New name when it exists (the Create now dialog's question);
3. takes a history checkpoint with the plugin's name and version as its intent;
4. moves the files in through `CellCreate` / `ComponentImport`, and refreshes the Project Tree.

A `.clay` from a plugin may carry its layout `ports` as layout port labels, and a `.cem` may reference them, so
a placed filter opens ready to simulate.

---

## 7. Host services — work a plugin asks circuitRF to do

A plugin is allowed to ask; it is never handed circuitRF's internals.

| `call` | Does | Returns |
|---|---|---|
| `simulate` | Runs an analysis line on a `.cnl` the plugin wrote (`type=sparam …`), in-process, as `circuitRF sparam` does | The result's path (`.npy`/`.sNp`) and its cube names |
| `resolve` | Resolves a technology, a substrate, a cell's views, or a reference, as `explain` does | The resolved object |
| `evaluate` | Evaluates an expression in the workspace scope | `{value, unit}` |
| `lineModel` | A microstrip / stripline / CPW line's Z0 and εeff from circuitRF's own planar models | `{z0, eeff, loss}` per frequency |

`simulate` is what lets a synthesis plugin show the response of the circuit it designed using circuitRF's own
engine and models, rather than a second, private approximation that disagrees with what the user will get after
placement. `lineModel` is the same reason applied to a single line.

A host call answers in the same process the panel runs in and is cancelled with its request. A plugin's
`simulate` that would take minutes is the plugin's responsibility to avoid in an `update`: put it behind an
`action`.

---

## 8. `window` plugins — the plugin draws its own window

`kind: window` is for a tool that needs more than the vocabulary: a live tuner, a drawing editor, an instrument
front panel. circuitRF starts the program with the same `hello` (workspace, context, staging folder) and then
only listens:
- `placed {cells}` places, exactly as §6.3;
- `host` calls are answered as §7;
- `open {path}` opens a document in circuitRF.

circuitRF draws nothing for it and cannot make it look native. It has no headless form unless it also declares
`params` and answers `commit` without a window.

---

## 9. Installing

### 9.1 Where plugins live

Per user, beside the preferences: `<app data>/circuitRF/plugins/<id>/<version>/`. One version of a plugin is active
at a time; the previous one is kept until the next update succeeds, so an update that fails to start can be rolled
back from the Plugins tab.

### 9.2 Three ways in

| From | How | Trusted by |
|---|---|---|
| **The vetted list** | Settings ▸ Plugins lists `plugins/index.json` (§10); Install downloads the release asset named there and checks its SHA-256 | The review that put that version on the list, and the user's one-time permission |
| **A GitHub address** | Install from address… takes a repository URL and a tag; circuitRF downloads that tag's release asset, or the source archive for a Python plugin | The user alone; the dialog says the plugin is not on the list |
| **A folder** | Install from folder… copies it; Link folder… runs it in place, for a plugin under development, and re-reads the manifest on every start | The user alone |

No address is fetched without the user asking. circuitRF holds no credential: a private repository is out of
scope for the first version.

A downloaded executable is written by circuitRF itself, so macOS quarantine and Windows' mark of the web are not
set; this is the same reason the updater's payload is not blocked (`auto-update.md` §4.1). That makes circuitRF's
own permission prompt the only gate, which is why it exists.

### 9.3 Python plugins

A Python plugin declares `requiresPython` and may ship `requirements.txt`. At install circuitRF creates a virtual
environment for that plugin version under its folder, with the interpreter `PythonInterpreterDiscovery` finds (the
one Settings already lets the user choose for kits), and installs the requirements into it. That is the one step
that reaches the network for a Python plugin, and the install dialog says so. A plugin with no requirements needs
no network at install.

### 9.4 Permission

Before a plugin first runs, circuitRF shows the prompt kit scripts get (`PCellTrustDialog`'s shape): name, version,
author, where it came from, whether it is on the vetted list, what it will run (the executable or the interpreter
and entry). **A plugin runs with the user's own permissions**: the prompt says that, and so does the user doc.
Nothing about a separate process is a sandbox. An update to a new version asks again unless the new version is on
the vetted list and the old one was.

`CRF_NO_PLUGINS=1` and a `no-plugins` file beside the install turn plugins off entirely, with the same precedence
`ExternalWorkerPolicy` gives device workers, so a managed installation can forbid them.

---

## 10. The vetted list

`plugins/index.json` in this repository:

```json
{
  "schema": 1,
  "plugins": [
    {
      "id": "example.coupled-line-filter",
      "name": "Coupled-Line Filter",
      "summary": "Edge-coupled bandpass filters on a microstrip substrate.",
      "repo": "https://github.com/example/crf-coupled-line-filter",
      "versions": [
        { "version": "0.3.0", "tag": "v0.3.0", "minCircuitRF": "1.8.0", "apiVersion": 1,
          "assets": { "any": { "url": "https://github.com/…/plugin-0.3.0.zip", "sha256": "…" } },
          "reviewed": "2026-11-20" }
      ]
    }
  ]
}
```

- **A version is vetted, not a plugin.** A new release goes on the list by a PR that adds its entry, and is
  reviewed like any other change.
- **What a review checks** is in `docs/user/src/reference/plugins.md`'s "Getting a plugin on the list" section: the
  source matches the tag; it writes only into its staging folder; it reaches the network only when a parameter or
  button says so; it starts no background process; it names no commercial EDA tool or PDK; its licence is stated.
- circuitRF reads the list from the release it was built from, and refreshes it from the repository's `main` when
  the user presses Refresh on the Plugins tab, under the same "nothing reaches a network without being asked"
  rule as updates. The list read from `main` must be signed by the key the updater already verifies
  (`tools/ReleaseSigner`), so a compromised mirror cannot add an entry.

---

## 11. The CLI and MCP

```
circuitRF plugin list [--json]
circuitRF plugin describe <id> [--json]                     # parameters, views, version, where it came from
circuitRF plugin run <id> --set name=value … -o <dir>        # one update, writes views + readouts as files
circuitRF plugin run <id> --set … --place <workspace> [--cell name] [--replace]
circuitRF plugin install <folder|address> [--tag t] / uninstall <id>
```

- `run` sends `hello`, one `update` and, with `--place`, one `commit`. Views are written as files into `-o`: a
  plot as its data plus a `.cdd`, a layout as a `.clay`, a schematic as its `.cnl`, readouts as `readouts.json`.
- A plugin not yet permitted is a refusal naming `--allow`, which records the permission exactly as the prompt does.
  `CRF_NO_PLUGINS` beats it.
- The MCP `plugin` tool has the same actions and the same refusals.

---

## 12. Versioning

- `apiVersion` is an integer. circuitRF supports a range; the `hello` exchange picks the plugin's.
- A plugin built for a newer `apiVersion` than this circuitRF supports is listed dimmed with "needs circuitRF
  x.y".
- A new widget, view kind, message field or host call is a new `apiVersion` when an older circuitRF would
  misread it, and only then. An unknown optional field is ignored, never refused.
- The frame and every message format carry the PCell wire's rule: nothing is renamed, only added.

---

## 13. What is deliberately absent

- **Loading a plugin's code into circuitRF.** P-1.
- **Plugin-supplied controls, XAML, styles or colours.** P-2, P-3.
- **A drawing API.** The `image` view is the whole of it.
- **Plugins that change circuitRF's own behaviour** — a new component model, a new analysis, a new file format, a
  menu in another editor. A component model already has its route (`pdk-external-devices.md`, Verilog-A); a
  layout generator already has its route (PCells, `pcell-contract.md`). A plugin may *produce* a cell that uses
  either.
- **Plugins writing into a workspace.** P-5.
- **Permission stored in a workspace.** P-6.
- **Private repositories, credentials, and a hosted plugin store.** The list is a file in this repository.

---

## 14. Plugin, PCell, or pull request?

| If the tool… | It is a… |
|---|---|
| is one parameterised layout cell that should stay live in a layout | **PCell** (`pcell-contract.md`) |
| is a device model | **external device / Verilog-A** (`pdk-external-devices.md`) |
| synthesises, calculates, imports or reports, and its author wants to own it | **plugin** |
| is broadly useful, maintained, and fits circuitRF's standards | **pull request**, often after it proved itself as a plugin |
