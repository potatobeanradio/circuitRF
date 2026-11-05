# Brief IPX-4 — IPC-2581 import: where it appears, and real-file acceptance

**Series:** IPC-2581 import, IPX-0 to IPX-4 · **Tag:** `R-ipx4-n`
**Area:** new `src/Design/Layout/Interchange/Ipc2581Import.cs`, `src/Cli/LayoutConvert.cs`, `src/Cli/DocumentKinds.cs`,
`src/Cli/Serve/ToolCatalog.cs`, `src/Ui/ViewModels/WorkspaceViewModel.cs`, `src/Ui/Views/WorkspaceWindow.axaml`,
`src/Ui/Views/Shared/TornOffFileMenuView.axaml`, `docs/design/cli.md`, `docs/user/src/reference/`
**Depends on:** IPX-0, IPX-2 and IPX-3. The panel from IPX-1 is what shows the result.

---

## 1. `R-ipx4-1` — one function

`Ipc2581Import.Import(path, parentDir, boardName, destTech, dbuPerMicron, stepName, …)` reads the file with
`Ipc2581Reader` and calls `PcbImport.ImportBoard`, returning its `ImportResult`. It takes a path or an unconsumed stream,
never a decoded string. The GUI and the CLI both call it, and a comment-stripped source scan holds that neither keeps a
second copy, as `AuthoringCliVerbTests` does.

## 2. `R-ipx4-2` — the GUI

- **`R-ipx4-2a`** `File ▸ Import ▸ IPC-2581…` appears in the three places the Board row appears: the native menu, the window
  menu and the torn-off menu. The command follows `ImportBoardAsync`, including `ImportFolder.Create`, the mapping dialog with
  its technology choice, and the Raster-fill preference.
- **`R-ipx4-2b`** The picker offers `*.xml` and `*.cvg`, plus All Files. A file whose root is not `IPC-2581` is refused with the
  reader's sentence.
- **`R-ipx4-2c`** Several steps: a small chooser listing them, because a file with a panel step and a board step is a question
  only the user can answer. One step asks nothing.
- **`R-ipx4-2d`** A large file reads with progress and can be cancelled, on the terms the Gerber import has. A cancelled import
  leaves nothing behind.
- **`R-ipx4-2e`** When it finishes, the board cell opens, the Messages panel holds the report, and the Nets panel is available
  and lists the board's nets.
- **`R-ipx4-2f`** The report's first lines are the stackup as a small table: each layer's name, kind, thickness, εr and tanδ, or
  conductivity, with stated and defaulted values marked. No stackup display is written: the Technology editor is the view, and
  this brief confirms it shows the result correctly, including inner layers and via entries.

## 3. `R-ipx4-3` — the CLI and the agent tools

- **`R-ipx4-3a`** `convert` gains a source format `ipc2581`, spelled that way in `--from`. It is detected by content through a
  new `Ipc2581Reader.LooksLikeIpc2581(path)`, which reads only as far as the root element, and `DetectSource` calls it before the
  Gerber classifier. `.cvg` also detects by extension.
- **`R-ipx4-3b`** A target of `ipc2581` is a refusal stating that it is import only.
- **`R-ipx4-3c`** `--step <name>` chooses the step, and more than one with none stated is a refusal listing them. `--list-steps`
  prints them with their layer and net counts and writes nothing. `--into-tech`, `--crop` and `--no-coalesce` work as on a board
  source.
- **`R-ipx4-3d`** `check` names an IPC-2581 file as such rather than calling it unreadable XML, through
  `DocumentKinds.Classify`.
- **`R-ipx4-3e`** Add the format to `Usage`, `ToolCatalog`, `docs/design/cli.md`, and the `convert` and `import` agent tools.
  `ConvertCliVerbTests` gains the source row against every target; it runs on one small fixture so it stays cheap.
- **`R-ipx4-3f`** The CLI's result is byte identical to the in-process `Ipc2581Import.Import` for a fixture, as the other import
  gates hold.

## 4. `R-ipx4-4` — documentation

Add the format to `docs/user/src/reference/file-formats.md`, `cli.md` and `layout-editor.md`: what is imported, what is reported
as skipped (placement as instances, step-and-repeat, backdrill, zones beyond the one chosen), and how a value is decided when a
file states none. Regenerate the edited pages. Add the format to the `src/Design` entry in the `RESOLVED.md` for any significant
finding, in terms of what the format is, with no reference to the files used.

## 5. `R-ipx4-5` — real-file acceptance (not committed)

The hand-written fixtures encode this series' own reading of the format, so they cannot catch a misreading of it. This run can.
It uses a scratch harness outside the repo. The files, their names and the measurements stay out of the repo, its comments and
its commit messages; the results go to the owner in chat.

Several of the standard's published test cases ship the same board as IPC-2581, as Gerber plus drill, and as an IPC-D-356 netlist,
and circuitRF already reads the last two. So the two readers can be compared on the same board:

- **`R-ipx4-5a` Copper.** For each copper layer, the area of the XOR between the IPC-2581 import and the Gerber import, against that
  layer's copper area. Target: at most 0.5%. Any excess is explained by cause before the layer passes. A mirrored or wrongly scaled
  board fails this immediately, and it is the only check of negative-plane handling on real boards.
- **`R-ipx4-5b` Nets.** For each pad in the IPC-D-356 netlist, the net at that point in the import, after normalising its spelling. Report
  the match rate and list the mismatches. Target: every mismatch is explained.
- **`R-ipx4-5c` Holes.** Hole count and diameter per drill tool against the drill files; via spans against the blind and buried drill
  files where a board has them.
- **`R-ipx4-5d` Stackup.** Each file's stated layers, thicknesses and values, read by hand, against the import.
- **`R-ipx4-5e` Size.** The largest test case, about 244 MB of XML: read it end to end under a memory watchdog. Record peak working set
  and time. If it exceeds `EntityHardCeiling`, confirm the refusal reads correctly. Do not add a timing test.
- **`R-ipx4-5f` Revision coverage.** The published test cases predate Rev B. Run any later file the owner can supply through all of the
  above that apply, and state plainly in the completion note which revisions were tested on real files and which only on fixtures.
- **`R-ipx4-5g` A picture of each.** `render` the imported board's copper, and look at it alongside the Gerber import's.

Before committing, grep the series' diff for vendor and product names. Exporters write such names into `SoftwarePackage`, `Enterprise`
and part fields, and they must not reach fixtures, messages or tests.

## 6. Gates

- The source scan, the byte-identity test and the `ConvertCliVerbTests` row.
- `tools/UiSnapshot` pictures: the step chooser, the import report, and the Technology editor showing an imported stackup. Inspect each
  and list them in the completion note.
- §5 reported in full, including what failed.
- Run those classes only.
