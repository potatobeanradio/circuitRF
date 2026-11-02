# Brief SI-5 — Entry points: the menu, the dialog, the drops, the CLI and the MCP tool

**Series:** `brief-schimp-0-overview.md` (D1, D9, D10) · **Tag:** `R-si5-<m>`
**Depends on:** SI-1 and at least one reader (SI-2 or SI-3); SI-4's format joins automatically when it lands.
**Area:** `src/Ui/Views/WorkspaceWindow.axaml` (both the `NativeMenu` and the `MenuItem` Import menus),
`src/Ui/Views/Shared/TornOffFileMenuView.axaml`, `src/Ui/ViewModels/WorkspaceViewModel.cs` (beside
`ImportComponent`), new `src/Ui/Views/Dialogs/ImportSchematicDialog.axaml(.cs)` + view model,
`src/Ui/Controls/SchematicCanvas.cs` (a new drop handler), `src/Ui/Views/ProjectTree/ProjectTreeView.axaml.cs`
(`OnFileDrop`), `src/Cli/Authoring.cs` + `CliEntry.cs` + `CliDiagnostics.cs` (the `import schematic` noun),
`src/Cli/Serve/ToolCatalog.cs` (the MCP `import` tool), `docs/design/cli.md`.

---

## 1. Goal

Make SI-1's two functions reachable from every place a user or an agent would look, with **no import logic outside
`src/Design`** (overview D1).

## 2. Requirements

**R-si5-1 — File ▸ Import ▸ Schematic…** in every Import menu (window menu, native menu, torn-off File menu), between
Data… and Image. Enabled when a workspace is open (as Import Component). File filter: "Schematic designs
(*.dsn; *.edf; *.edn; *.edif; *.kicad_sch)" plus All files — **the filter names extensions only** (overview D7). The
tooltip names what it does, not the source tools.

**R-si5-2 — The dialog.** Source path (pre-filled from the picker), the detected format and its version, target
folder (the tree's selected folder, else the workspace root) and cell name (file stem, validated live by
`NameValidator`), the pages/sheets found, a **parts table** (reference, source symbol, value, "becomes": native kind
or symbol cell — SI-1 R-si1-3; a row may be overridden to symbol cell), a **ports list** (connectors, global and
hierarchical ports, each with "make a circuitRF Port", default unchecked — overview D9), the code page (EDIF/binary
only, default 1252), and a **preview** drawn by the very `NetlistSchematic.Build` result the import will write. The
connectivity result (D8) shows under the preview before Import is pressed. Import writes; Cancel writes nothing.
Afterwards the new cell's schematic opens.

**R-si5-3 — Drop on an open `.csch` (overview D10).** A new handler pair on `SchematicCanvas`
(`OnSchematicFileDragOver/Drop`), registered after the parts-table pair. Drag-over accepts by extension (cheap);
drop classifies by content (`SchematicFileClassifier`) and calls `SchematicImport.BuildInto` at the drop point (world
coordinates), off the UI thread with Esc to cancel (the `BeginPaste` pattern), then executes the returned edit as
**one undo step**; symbol cells are written beside the document's cell (D4). No dialog: defaults throughout (every
part per the map, no ports); one Messages line — "Built *file* into *schematic*: n parts (k native, m symbol cells,
j reused), connectivity matched" — with **Details…** opening the report. A file that classifies as nothing is one
Messages error, nothing placed. A drop onto an unsaved schematic writes its symbol cells only after the user saves —
or is refused with that reason; choose the simpler and record it.
- **Image files are untouched**: the existing `OnImageFileDrop` places a bitmap exactly as today. Gate: dropping a
  `.png` still yields one bitmap and zero components; dropping a `.kicad_sch` yields components and no bitmap.
- The drop handlers' ordering and `e.Handled` discipline match the existing ones (`DropTakesKeyboardFocusTests`
  extends to the new handler).

**R-si5-4 — Drop on the project tree.** A schematic file dropped on a workspace or folder row opens R-si5-2's dialog
targeting that folder. Other file kinds keep their current behaviour.

**R-si5-5 — `circuitrf import schematic <file> --into <workspace-or-dir> [--cell <name>] [--port <ref-or-net>]…
[--symbol-cell <ref>]… [--codepage <n>] [--list] [--json]`.** A noun on `import`, not a new verb (the `new`/`import`
rule). `--list` prints pages, parts and their mapping and writes nothing. Every default is the dialog's; anything the
dialog would ask that has no default is a refusal naming its flag. Exit 0 on success; **exit 1 when the connectivity
check is not `Matched`** (the cell is still written, and the report says so — a build machine must not treat a
mismatch as success). The paths created are the result, on stdout and in `--json`. Comment-stripped source scan:
`src/Cli` and the view models contain no reader or build logic.

**R-si5-6 — MCP.** The `import` tool gains `what=schematic` with the same parameters; its description says it
creates a cell and reports the connectivity check. `ToolCatalog` tests extended.

**R-si5-7 — `cli.md`** gets a section; the CLI help lists the noun; `CLAUDE.md` is NOT edited (findings to
`src/Ui/RESOLVED.md` / `src/Cli/RESOLVED.md`).

## 3. Gates (targeted)
- The CLI as a process vs the in-process `SchematicImport.Import` on one committed fixture: byte-identical `.csch`
  except the provenance date (the `em`/`render` gate pattern).
- Headless view tests: the dialog's preview equals the imported cell's drawing; the two drop tests above; undo after a
  drop restores the document exactly.
- The source scan.
