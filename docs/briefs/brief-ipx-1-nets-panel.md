# Brief IPX-1 — A Nets panel for the layout editor

**Series:** IPC-2581 import, IPX-0 to IPX-4 · **Tag:** `R-ipx1-n`
**Area:** new `src/Ui/Layout/Nets/`, `src/Ui/ViewModels/Dock/`, `src/Ui/Views/`, `src/Render/Renderers/`,
`src/Cli/ExplainQueries.cs`
**Depends on:** nothing. It applies to any layout with named nets.

---

## 0. Why

Layout shapes carry a net name, and `LayoutEditorViewModel.NetNamesOnBoard()` can list them. The only
lists a user can see are inside the Impedance panel and railRF, each serving that tool's own task. A board
imported with its nets gives no general way to see which nets it has or where one of them runs.

## 1. `R-ipx1-1` — the panel

- **`R-ipx1-1a`** A dock tool, `NetsTool`, built the way `ReviewTool` is: it follows the active layout
  document and is registered in `CircuitRfDockFactory`, `DockPanelIds` and `App.axaml`.
- **`R-ipx1-1b`** One row per net: name, shape count, and the layers it reaches, sorted by name in natural
  order so `N2` precedes `N10`. A filter box narrows the list by substring. The list is virtualized: a board
  can have thousands of nets.
- **`R-ipx1-1c`** The counts come from the open cell's shapes. Before writing the panel, establish how a
  net reaches copper inside a placed instance and whether instance copper can be counted without flattening.
  If it cannot be counted cheaply, the panel counts the open cell only and its header says so.
- **`R-ipx1-1d`** With no named nets, the panel shows one line saying this layout has no named nets. It
  does not appear broken or empty without explanation.
- **`R-ipx1-1e`** The list is built once per document change, off the render path. Moving through rows does
  not rescan the shapes.

## 2. `R-ipx1-2` — highlighting

- **`R-ipx1-2a`** Selecting a row outlines that net's copper on every visible layer, in a new theme role
  for both the light and dark variants. It travels as a draw argument, as `RailNetPreview` does: it is the
  state of a list box and not part of the document. It does not change the selection and adds no undo entry.
- **`R-ipx1-2b`** Multi-select highlights several nets, each in the same role. A `Dim other copper` checkbox
  draws everything else at reduced opacity.
- **`R-ipx1-2c`** `Zoom to net` frames the net's extent. `Select shapes` makes its shapes the editor's
  selection through the existing multi-select path, so Properties and Name Net continue to work on them.
- **`R-ipx1-2d`** The buttons and the checkbox sit in a row that is always present. They are disabled when
  no row is selected and never move or appear conditionally.
- **`R-ipx1-2e`** The highlight is overlay state. `render` and every export are unaffected, and a picture
  of a layout with a net selected is byte identical to one without when drawn with no overlay.
- **`R-ipx1-2f`** A net of very many shapes must not cost a repaint proportional to the board on each arrow
  key. Hold this with a counter test, not a timing test.

## 3. `R-ipx1-3` — the same answer headlessly

`explain <path> --nets` prints the panel's rows, and `--json` gives them as a list. Both come from one
function in `src/Design`, called by the panel too, so the two cannot disagree. A document kind with no
layout is a refusal naming the kind, as `--extents` already does. Add the spelling to `ToolCatalog` and to
`docs/design/cli.md`.

## 4. Gates

- One test class for the shared function: the counts on a small layout, natural ordering, and no nets.
- One test: the view model's list matches `explain --nets` for the same layout.
- `tools/UiSnapshot` renders the panel with nets, with a filter, with a row selected and with no nets, plus
  the canvas with a net highlighted, in both themes. Inspect each PNG, compare the button row's bounds across
  the states, and list the pictures in the completion note.
- Check the panel on a Gerber board with its netlist as well as on a `.kicad_pcb`.
- Update `docs/user/src/reference/layout-editor.md` and regenerate that page.
