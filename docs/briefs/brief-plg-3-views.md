# Brief PLG-3 — the plugin views: plots, Smith charts, tables, layouts, schematics, images

Series: `brief-plg-0-overview.md`. Design: `docs/design/plugins.md` §5. Depends on PLG-2.

**Why.** A plugin panel that shows only numbers cannot show what a design tool's user judges by: a response with
markers, a match on a Smith chart, the artwork, the circuit. Each of these already has a renderer in circuitRF, but
each renderer is fed by a document inside an editor. This brief feeds them from a plugin's reply, without a second
copy of any of them.

## What to build

Every view model below is parsed in `src/Design/Plugins/PluginViews.cs` (below the firewall; brief 5 writes them as
files) and drawn in `src/Ui/Plugins/Views/`. Each view, given `null`, clears; absent, keeps what it showed; given
something it cannot read, shows an empty frame with the reason and adds the reason to `messages`.

### 1. `plot` and `smith` — the Data Display, fed a `DataSet`

- **Data:** inline (`axes` + `cubes`, `plugins.md` §5.1) built into a `DataSet` of single-kind cubes, or `file` — a
  `.sNp` or `.npy` in the staging folder, read with the readers the Data Display already uses (`ResultDocument`).
  The inline form refuses a cube whose length does not match its axes, naming the cube.
- **Plot spec:** the `.cdd` model's own fields (`DataDisplayConfig`: axes, traces, markers, limit lines), read by the
  same code `PlotConfigLoader` uses. `cube=`/`y=` mean what they mean in `circuitRF plot --trace`; reuse that
  parser, do not write a second one.
- **Drawn by the Data Display's existing plot control**, embedded, with its zoom, marker placement, readouts and
  export. Add **Open in Data Display**, which writes the data and a `.cdd` into the staging folder and opens it as
  an ordinary document (placement moves neither).
- **Bound markers** (`bind`): drawn with the marker style, at the parameter's value. Dragging sends
  `event {view, kind: "marker", marker: index, x}`; the controller sets the parameter and the ordinary
  `update` follows. A marker bound to anything but a `quantity` whose unit equals the x axis's is refused when the
  view is read, naming the marker.
- **Markers the user adds** are the user's, kept across updates of the same view, and never sent to the plugin.

### 2. `table`

`columns`, `units`, `rows` (`plugins.md` §5.2). Numbers are formatted in the workspace's display units with the
Data Display table's formatter; copy-to-clipboard as tab-separated text. Also used by `readout` `as: table`.

### 3. `layout` — the PCell reply, drawn by `LayoutRenderer`

- **Shapes:** the PCell wire's generate reply and shape vocabulary (`pcell-wire-schema.md` §4.3–4.4), decoded by
  the existing codec from the frame's int64 payload into the layout model a PCell's generated cell becomes.
- **Technology:** `"technology": "self"` means the one in the plugin's `technology`/`substrate` parameter; else a
  technology id. Drawn with `LayoutRenderer` in that technology's colours, with rulers, pan, zoom, fit, and the
  layer visibility list; a layer the technology lacks uses the fallback palette and is named once in `messages`.
- **Handles:** the wire's editor hints (§4.5, version 7) — `linear` and `angular`, each bound to a parameter. Drag
  uses the PCell handle solver from `src/Ui` (it stays there); release sends `event {view, kind: "handle", param,
  value}` with the solved value, which sets the parameter. While dragging, the preview shows the handle's own
  ghost only — the plugin is asked on release, not per mouse move.
- **Ports:** drawn as layout port labels; on placement they are written into the `.clay` as port labels (SDL-5's
  form), so a `.cem` the plugin stages can name them.
- **Read-only.** No selection-to-edit, no move, no delete; hover and measure work.

### 4. `schematic` — a staged `.cnl`, drawn

- `netlist --toSchematic`'s drawing lives in `src/Cli/NetlistToSchematic.cs`. **Move it to `src/Design`**
  (R-aut-2: a capability only an adapter has is not a capability), leave the verb as its caller, and keep its
  output byte-identical — its existing tests are the gate for the move.
- The view runs it on the staged `.cnl` and draws the result with `SchematicRenderer`, read-only, pan and zoom.

### 5. `image`

A staged PNG or SVG (SVG through the same loader the doc figures use), letterboxed, fit to the frame. With
`clickable`, a click sends `event {view, kind: "click", x, y}` in the image's pixel coordinates. No drag, no hover.

## Gates

1. **Inline data:** an inline S21 builds a complex cube equal, element for element, to the same data read from the
   `.s2p` the example also stages.
2. **One parser:** a source scan finds no second `cube=`/`y=` parser and no second `.cdd` field reader under
   `src/Ui/Plugins` or `src/Design/Plugins`.
3. **Bound marker:** dragging marker 0 to 2.35 GHz in the headless harness sends exactly one `event` and one
   `update` with `f0 = 2.35e9`, and the marker is drawn there after the reply.
4. **Layout:** the staged shapes, placed, give a `.clay` whose geometry equals the preview's, shape for shape.
   A handle drag sends the solved value; the PCell handle solver is the one called (no copy).
5. **Schematic move:** `netlist --toSchematic` output is byte-identical before and after the move, on its existing
   cases.
6. **UI:** UiSnapshot PNGs of `tools/plugin-examples/stub/` (brief 7's example: response plot with a bound marker,
   Smith chart, table, layout with a handle, schematic) and of the `image` view, light and dark. Looked at and
   listed.

## Not in this brief

3D views, field plots and EM results inside a panel. A plugin that wants an EM answer stages a `.cem` and the user
runs it after placement; a `simulate` of an EM setup is not a host service (brief 4).
