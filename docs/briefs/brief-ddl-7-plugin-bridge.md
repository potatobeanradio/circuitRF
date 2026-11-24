# Brief DDL-7 — the plugin bridge: display variables, declared panels, instruments

**Status: not scheduled.** This brief records the seam so DDL-1, DDL-5 and DDL-6 keep it open. It is to be scoped
again when a real plugin needs it (`plugins.md` P-8: the vocabulary grows because a real plugin needs it).

**Series:** DDL-0 … DDL-8 · **Tag:** `R-ddl7-<n>` · **Depends on:** DDL-1, DDL-5, DDL-6, the PLG series
**Area:** `docs/design/plugins.md` (§4, §6, §7, §8), `src/Design/Plugins/`, `src/Render/DataDisplay/Links/`,
`src/Ui/DataDisplay/`.

## The direction

The owner's aim is a Data Display that works as a front panel: controls that drive plots now; custom panels that
plugin authors supply later; and, through a plugin, instruments. circuitRF's plugin design already fixes how that
can happen without loading anyone's code (`plugins.md` P-1–P-3). A plugin is a separate process. It talks JSON frames.
It declares panels from a fixed vocabulary, and anything beyond the vocabulary draws its own window (§8, which
already names "an instrument front panel").

## What DDL-1, DDL-5 and DDL-6 must keep true (the seam)

- **Display variables are addressable by name and id, below the firewall.** A `.cdd`'s link sources (markers, controls,
  `selected`) are listed, read and set through one API in `src/Render/DataDisplay/Links/` (`DisplayVariables`: list,
  get, set, subscribe). The window uses it, and so does `render` (`--marker`, `--control`). Nothing about a variable
  is reachable only through a view model. A plugin session in `src/Design/Plugins` can then be given that API, and
  never the view model.
- **Setting a variable is the same act whoever does it.** A value set by a plugin goes through the same command the
  canvas uses: an undo step, a dirty mark, live follow. There is no second write path.
- **Controls are serialisable declarations.** A control's whole definition is its `DisplayControl` record (DDL-5
  R-ddl5-1). A plugin can therefore declare one in a manifest as JSON, with nothing compiled.

## What a later brief would add (sketch, for scoping then)

1. **A display-bound plugin.** A plugin with `kind: display` is attached to one open `.cdd` tab, from the tab's
   menu. It receives `hello` with the tab's variables, and `event {kind: "variable", name, value}` when one changes
   (debounced as `update` is, 150 ms). In its reply it may return `set: {name: value}`, as a marker event already can
   (`plugins.md` §6.2).
2. **Declared display panels.** The manifest's `ui` gains the DDL-5/6 leaves (`slider`, `knob`, `button`, `readout`),
   each a `DisplayControl` declaration, laid out as a group on the canvas. They are drawn by `ControlRenderer`, so
   they look, export and render like the user's own. They arrive locked (DDL-5 R-ddl5-9), because the plugin laid them
   out, and the user may unlock them. This stays inside P-3 (a fixed vocabulary) and P-2 (no
   plugin-supplied drawing).
3. **Live data in.** A plugin writes `.npy` or Touchstone files into its staging folder and announces them
   (`dataSource {path}`). The display adds each as a data source and reloads it when the plugin announces a new
   version: the existing reload path (`DataSourceLibraryViewModel.ReloadChangedAsync`). An instrument plugin then shows
   a sweep in ordinary plots, with markers and links, and writes nothing into the workspace (P-5).
4. **Instrument control** is the plugin's own business: instrument I/O libraries, sockets and drivers live in its process. circuitRF ships no
   driver and holds no instrument credential (as it holds no git credential). A plugin that controls an instrument is
   asked about once, like any plugin (P-6). Its manifest says it reaches the network or hardware, and the trust prompt
   says so.

## Open questions for that brief

- Whether a display plugin's `set` is subject to the user's own undo (proposed: yes, one step per reply), and whether a
  plugin can see controls it did not declare (proposed: only the variables the user names in the attach dialog).
- Rate: an instrument streaming at 10 Hz into a plot. The reload path coalesces, but this would be measured before any
  promise is made.
- MCP: `plugins.md` keeps MCP a consumer of plugins (PLG-5). An agent setting display variables would go through
  `render --marker/--control` (headless) rather than a live window.

## Not here (for this series)

Everything above beyond the seam.
