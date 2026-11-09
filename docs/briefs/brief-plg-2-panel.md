# Brief PLG-2 — the plugin panel: layout tree, widgets, and the update loop

Series: `brief-plg-0-overview.md`. Design: `docs/design/plugins.md` §4, §6.2. Depends on PLG-1.

**Why.** After PLG-1 a plugin can take parameters and place a result, but the user sees nothing until it is placed.
A synthesis or calculation tool is used by changing an input and judging the answer, many times, before placing
anything. That needs a panel that stays open, updates as inputs change, and stays responsive when the plugin is
slow or stops.

## What to build

### 1. The layout tree — `src/Design/Plugins/PluginLayout.cs`

- Parse `ui.layout` into a tree of containers (`row`, `column`, `group`, `tabs`, `split`) and leaves (`param`,
  `readout`, `button`, `messages`, `view`, `note`) exactly as `plugins.md` §4.1–4.2.
- Validation returns diagnostics: a `param` leaf naming no declared parameter, a parameter placed twice, a
  `readout` naming a parameter not of kind `readout`, two views with one name, an unknown `kind` (listed as an
  empty frame saying "needs a newer circuitRF", never a refusal of the whole panel), a `split` whose `sizes` count
  differs from its children.
- **No `ui`:** the default panel — parameters in one column, `messages`, a Place in workspace button, then one tab
  per view in the first reply (`plugins.md` §4).
- This file is below the firewall because brief 5 lists a plugin's views from it.

### 2. The panel — `src/Ui/Plugins/PluginWindow`

- A top-level tool window, like Match Designer and railRF: title from `ui.title`, size from `ui.size`, both then
  remembered per plugin `id` with the split positions and the selected tab.
- Each tree node maps to one Avalonia container; a `group` is styled as the analysis editor's groups are. A
  `param` leaf reuses the control PLG-1's parameter dialog built for its kind, so the dialog and the panel cannot
  differ.
- **`readout`:** `value` is a label formatted in display units; `table` uses brief 3's table view; `text` is
  selectable, wrapped, monospace off.
- **`messages`:** errors, warnings and notes from the last reply, with the theme's severity glyphs, plus a line for
  a host-side failure ("The plugin stopped. Change a value to restart it." / "No answer in 30 s." with Stop).
- **`button`:** sends `action`; `commit` runs placement (PLG-1 §7). Disabled while a commit is running.
- **`enabled`** in a reply greys named parameters and buttons. **Nothing is ever hidden** and no control moves.
- A busy bar along the top edge, always reserved, shows `progress` text and fraction, and a Stop button that sends
  `cancel` and, if no reply arrives within 2 s, restarts the process.

### 3. The update loop — `PluginPanelController` (below the firewall)

- Opening the panel sends `hello` then `update` with the defaults (or the last parameters used, remembered per
  plugin per workspace — in the user's preferences, not the `.cws`).
- **A change debounces 150 ms**, then sends `update`. A newer request sends `cancel` for the one in flight and its
  reply is dropped on arrival. Only the reply to the newest request is ever applied.
- **Validation before sending:** a `quantity` that does not evaluate, or is outside `min`/`max`, is marked on its
  control and not sent; the last good reply stays shown.
- **A reply is applied atomically** — readouts, views, messages and `enabled` together, on the UI thread, from a
  model the controller built off it.
- `set: {param: value}` in a reply updates those controls without sending another `update` for them.
- **A crashed process** keeps the last reply shown, says so in `messages`, and the next change restarts it with
  `hello` + `update`.
- Closing the window sends `bye`.
- The controller is UI-free and takes a session interface, so the loop is tested without a window or a process.

## Gates

1. **Layout:** every validation refusal has a test; the default panel is built for a manifest with no `ui`.
2. **Supersede:** with a fake session that answers request 1 after request 2, only reply 2 is applied; request 1
   got a `cancel`.
3. **Debounce:** five changes within 150 ms send one `update` with the final values.
4. **Never blocks:** a fake session that never answers leaves the controller's UI-facing calls returning at once;
   the timeout message appears; Stop restarts.
5. **Crash and restart:** with `tools/plugin-examples/broken` in exit-after-first-reply mode, the first reply stays
   shown, the message says the plugin stopped, the next change starts a new process.
6. **Controls never shift:** a reply that disables every control leaves every control's bounds unchanged
   (measured in the headless harness).
7. **UI:** UiSnapshot PNGs of `tools/plugin-examples/calculator/` (a `panel` plugin: a few `quantity` inputs, a
   `value` readout, a `text` readout, messages) idle, busy, with an error, and after the plugin stopped; light and
   dark. Looked at and listed.

## Not in this brief

Views (`plot`, `smith`, `layout`, `schematic`, `image`, `table`) are brief 3; until then a `view` leaf draws an
empty frame saying "view not available yet" and the loop simply stores the reply's `views` for it.
