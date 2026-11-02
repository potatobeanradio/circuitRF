# Brief — AN-02: Importing a Gerber file set, and turning it into a schematic

**Tag:** `R-an02-<n>` · **Standalone** · **Depends on:** `aadebd8b` (Gerber import into an existing technology, R-gt)
**Area:** `docs/user/src/app-notes/` (new page), `docs/user/src/_nav.txt`, `docs/user/src/reference/layout-editor.md`
(sources only), `src/Ui/Diagnostics/FigureCatalog.cs` + `src/Ui/Diagnostics/Fixtures/DocGerberFixtures.cs` (the
figures). **No product code changes**, except the one R-an02-6 allows.

---

## 1. Goal

`layout-editor.md` is 1,241 lines long. Its **Gerber ▸ Import** subsection (about 85 lines from
`#### Import — **File ▸ Import ▸ Gerber…**` to `#### What a round trip does and does not preserve`) is a workflow,
not a reference entry. It explains the scope prompt, identification, drill data, vias, the Technology row, the
stackup guess and what to check afterwards. That reads better as a worked application note, with pictures of each
step on a real board.

AN-02 takes that material, walks it through the shipped **Artwork to Schematic** example board, and continues past the
import into **Design ▸ Create Schematic from Artwork…**, which is what most people import a board for. The reference
page keeps a short summary that links to the note.

The note has **many figures**: the dialogs as they open, the layout at each stage, the stackup, and the schematic
that comes out. It **links to the Reference pages** for every control, format and option it uses, instead of
re-explaining them.

## 2. What exists (verify each before relying on it)

- **The app-note section.** `docs/user/src/app-notes/index.md` and `an01-ports-and-coupling.md`. AN-01 is the model
  for front matter (`title`, `slug`, `doc-kind: Application Note`, `breadcrumb`, `lede`, `keywords`), the in-page
  `<nav class="toc">`, `{#anchors}` on headings, `{{ui: <id>}}` figures, `<pre>` command blocks, `callout note`
  blocks and a closing checklist. The index makes two promises AN-02 must keep. **Every number came out of a run, and
  the note says which run.** **A note links to its Reference chapter rather than re-explaining it.**
- **`_nav.txt`** lists `app-notes/an01-ports-and-coupling.html` under the app-notes section. A page not listed there
  fails the docs run by name.
- **The walkthrough board.** `examples/Artwork to Schematic/` contains:
  - `fab/Board/`: `Board.GTL`, `Board.GBL`, `Board.GTS`, `Board.drl` and `Board.gbrjob`. The files carry X2
    attributes. The job file has **no** `MaterialStackup`.
  - `fab/Board.pos` and `fab/Board-bom.csv`. The BOM deliberately has no row for L1.
  - `Board/` is the board as imported, with its own `Board.ctech`. `Board design/` is the schematic it was drawn from.
  - `tech/board.ctech` (named `round-trip`) is the workspace technology: two conductors, a 254 µm core and a PTH via.
  - Its `README.md` walks the Create Schematic from Artwork half with quoted numbers: S11 ≈ −7.7 dB at 2 GHz before
    tuning, −15.8 dB for Board design, and L1 = 8.2 nH. The authoring code is
    `tests/Ui.Tests/Examples/ArtworkToSchematicExampleAuthoring.cs`.
  - **Check this discrepancy:** the README's last paragraph says the set was imported with **File ▸ Import ▸
    Board**. Read the authoring test. If the import was a Gerber import, record that in the completion report. Do not
    edit the example (§5).
- **Figures already in the catalog that this note reuses** (do not duplicate them):
  - `gerber-import-technology` and `gerber-import-technology-choices`, the Technology row on a synthetic six-copper
    set. Both are cited only by `layout-editor.md` today.
  - `artwork-to-schematic-dialog`, Create Schematic from Artwork on this same example with its placement and BOM given.
  - `tuning-panel`, `data-display` and the stackup cross-section fixtures in `DocStackupFixtures`, as patterns to
    follow, not as figures to reuse unless they show this board.
- **Dialogs a Gerber import can raise.** These are all real Windows, so a fixture lifts out their content:
  - `GerberImportScopeDialog(GerberImportEntry.FolderSurvey)`
  - `GerberDrillFormatPromptDialog(...)`
  - `GerberArchiveOfferDialog`
  - `LayerMappingDialog`, including its Gerber constructor
  - `ChangeTechnologyDialog`
- **Figure conventions** (`src/Ui/Diagnostics/CLAUDE.md` and `FigureCatalog`'s header):
  - Every row states its size. A dialog figure is the declared size less the synthetic title bar.
  - A window figure is 1100×700.
  - A figure never prints a machine path or a measured time.
  - Two consecutive runs must produce identical output.
  - `DocRecognitionFixtures` shows how to give companion paths **relative** to the example so no absolute path is
    printed.
  - `DocGerberFixtures` shows how to lift a dialog with its margin kept: the generator sizes the lifted body to the
    full figure, so a bare margin is lost.
  - `DocGerberFixtures.OpenTechnologyCombo` shows how to open a combo popup so the catalog's popup check passes.
- **The Reference pages AN-02 links to.** Confirm each anchor exists before citing it:
  - `reference/layout-editor.html`: `#interchange`, `#gerber`, `#technology`, and the round-trip table.
  - `reference/artwork-to-schematic.html`: `#open`, `#target`, `#scope`, `#options`, `#companions`, `#parts`,
    `#silkscreen`, `#recognised`, `#coplanar`, `#ic`, `#report`, `#probe`, `#fromartwork`, `#headless`.
  - `reference/stackup.html`: `#where`, `#anatomy`, `#cross-section` and `#tab`.
  - `reference/cli.html`: `#convert` and `#convert-tech`, plus the `recognize` verb's section.
  - `reference/settings.html`: the coalesce-raster-fill preference.
  - `reference/railrf.html`: board import with netlist companions.
  - `reference/components.html`: MLIN, TLIN, CPWG, VIAGND.
  - `reference/vias.html`, `reference/workspace.html` (the Messages panel), and the tuning reference.

## 3. Decisions (the owner may overturn any of these before work starts)

- **D1 — AN-02 becomes the home of the import walkthrough.** The *Import* subsection of `layout-editor.md` §Gerber
  moves into the note. What stays in the reference:
  - the `#gerber` anchor;
  - Export;
  - the round-trip table;
  - a summary of at most eight lines. It covers what an import produces, that identification is by content, and that
    the Technology row chooses New or an existing technology. It ends with a link to AN-02.

  The two `gerber-import-technology*` figures move with the text. Nothing in GDSII, DXF, OASIS or Board moves.
- **D2 — One board throughout, the shipped example.** Every walkthrough figure and every quoted number comes from
  `examples/Artwork to Schematic`, copied to a temporary folder by the fixture. Use a synthetic set only for a
  situation the example cannot show:
  - the scope prompt needs a single file picked from the folder, which the example can show;
  - the drill-format prompt needs a headerless drill file;
  - a mismatched copper count;
  - an unidentified file and the stackup question.

  Label each synthetic figure in its caption as a made-up set.
- **D3 — Both technology paths are shown.**
  - *New technology from the files*: what it guesses (FR-4, 1.778 mm), where the summary says so, and how to correct
    it on the Stackup tab.
  - *Use the workspace's technology* (`tech/board.ctech`, two conductors, which matches the set): the layout
    references it and nothing is guessed.

  The note says which one to choose when: use an existing technology when the fabricator's stackup is already in one.
- **D4 — Create Schematic from Artwork is a section of the note, not a copy of its reference page.** It follows the
  example README's five steps (recognise, review the parts table, create, simulate, tune the unknown part) with a
  figure per step. Every option links to `artwork-to-schematic.html` and is not re-explained. The reference page
  gains one sentence linking to AN-02 from its *A worked example* section, and nothing else in it changes.
- **D5 — Title and slug.** "AN-02 — Importing a Gerber file set, and turning it into a schematic",
  `app-notes/an02-gerber-import.html`, listed in `_nav.txt` directly after AN-01.

## 4. Requirements

**R-an02-1 — The page.** `docs/user/src/app-notes/an02-gerber-import.md`, in AN-01's shape. Sections, in order, each
with an anchor and in the `<nav class="toc">`:
1. **The board** (`#board`). What the example is, what is in `fab/` (a table of files and what each one is), and the
   one-command way to get it: Tools ▸ Examples ▸ Artwork to Schematic.
2. **Pointing at the files** (`#pick`). Folder versus single file and the scope prompt. Identification is by content,
   and skipped files are named.
3. **Which layer is which** (`#identify`). The five rungs: job file, X2, `GerberSuffix`, heuristic, dialog. Say what
   this board's files declare and which rung settled each one, as the import summary reports it.
4. **The layer-mapping dialog and the Technology row** (`#mapping`).
   - New versus Use, the copper-count rule, and the default.
   - The refusals (count, binding, rank, drill).
   - The stackup question for an unidentified file.
   - The two moved `gerber-import-technology*` figures plus this board's own dialog.
5. **Drill data and vias** (`#drill`). The inference, when the prompt appears, and vias rebuilt where a hit meets a
   flash. Give this board's via count from the run.
6. **What you get** (`#result`). The flat cell, the folder, the `.ctech` (New) or the reference (Use), and the
   import summary in Messages.
7. **Check the stackup before you trust a number** (`#stackup`). Under New: the guess, the summary paragraph that names
   it, and the Stackup tab with this board's fabricator values (254 µm, εr 3.66, from the example README). Under Use:
   the job-file disagreement warning. Link to `stackup.html`.
8. **From artwork to a schematic** (`#schematic`). D4's walkthrough: recognise, the parts table (BOM, placement, the
   unknown L1 that becomes `L1_L`), create, simulate, tune to 8.2 nH, Show in Artwork, Swap Line Type. Explain why
   this path is the answer for a whole board: an EM run of a whole board is refused at millions of unknowns, and the
   recognised schematic solves in seconds. Link to `mom-engine.html` for the refusal.
9. **The same thing from the command line** (`#cli`). `circuitrf convert … --to clay` (with `--into-tech`) and
   `circuitrf recognize …`, each run for real on the example with the output quoted. Link to `cli.html`.
10. **Checklist** (`#checklist`). Five to eight lines, in AN-01's closing style.

**R-an02-2 — Links to the Reference.** Every control, dialog, file format, component and verb the note names links,
on first mention in each section, to its Reference page and anchor (§2's list). The note states what happened on this
board and why it matters. The reference states what every option does. Where the note would otherwise repeat a
reference table, it links instead. A self-check in the completion report lists every `reference/…html#…` the note
cites and confirms each anchor exists in its source, by `grep`, since DocGen is not run.

**R-an02-3 — Figures, many of them, all from the real controls.** Add catalog rows prefixed `an02-`. Each one is built
from the real dialog, editor or panel, never a mock-up, at the sizes §2's conventions give. The minimum set:

| id | What it shows | Board |
|---|---|---|
| `an02-scope-prompt` | The scope question after picking `Board.GTL` alone | example |
| `an02-layer-mapping` | The mapping dialog for this set, Technology row on the default D3 picks | example |
| `an02-layer-mapping-unidentified` | A row asking the stackup question (*in the stackup as*), its combo open | synthetic |
| `an02-drill-format` | The drill-format prompt with its evidence | synthetic (headerless drill) |
| `an02-import-summary` | The Messages panel after the import, the summary block visible | example |
| `an02-imported-layout` | The layout editor with the imported board, all layers | example |
| `an02-imported-copper` | The same, copper and vias only, zoomed on the tee and stub | example |
| `an02-stackup-guessed` | The Stackup tab of the technology a New import wrote (the FR-4 guess) | example |
| `an02-stackup-corrected` | The cross-section with the fabricator's values | example |
| `an02-parts-row-selected` | The layout with one parts-table row's pads marked | example |
| `an02-recognised-schematic` | The schematic editor showing `Board_model` | example |
| `an02-show-in-artwork` | The layout with a recognised line's copper marked | example |
| `an02-s11` | S11 of `Board_model` against `Board design`, before and after tuning | example |

Reuse `artwork-to-schematic-dialog`, `gerber-import-technology` and `gerber-import-technology-choices` as they are.

- **Shrinking the set:** a figure in the table may be dropped only when the control cannot be built headlessly. Name
  each dropped figure in the report with the reason.
- **Adding to the set:** add a figure wherever a paragraph describes something on screen that no figure shows.
- **Captions:** each states what the figure shows, in the catalog's voice.
- **Placement:** in the text, each `{{ui: …}}` follows the paragraph it illustrates.

**R-an02-4 — Fixtures.** Extend `DocGerberFixtures`, or add `DocAppNoteGerberFixtures` if that reads better.
- Copy the example into a temporary folder with `ExampleWorkspaces.ResolveRoot()`, and delete it in `Cleanup`.
- Run the **real** import (`GerberImportEntry` / `GerberImport.Import`), the real recognition
  (`ArtworkRecognitionRunner`) and a real S-parameter run for `an02-s11`. If that run makes the capture slow, use a
  `Static` row as the catalog's header explains.
- **No absolute or temporary path may appear in any figure.** This includes Messages text. R-gt's closing sentence
  for a Use import prints the full path of the technology (see R-an02-6).
- Two consecutive captures must be identical.

**R-an02-5 — Every number from a run.** Every count, dB value, thickness and inductance the note quotes is read off the
fixture's own run, or off a CLI run you perform and quote. The report lists each quoted number and where it came
from. If a number disagrees with the example README (−7.7 dB, −15.8 dB, 8.2 nH), say so in the report and use the
measured value. Do not edit the README.

**R-an02-6 — The one product change allowed.** If the Use-import closing sentence's full path would appear in
`an02-import-summary`, change that sentence to print the path relative to the import's parent folder: the workspace
in the GUI, the output folder in `convert`. Make the change in `GerberImport` only, and update any test that asserts
the sentence. Do not crop a figure to hide a path.

**R-an02-7 — The reference page after the move.** In `layout-editor.md` §Gerber:
- *Export* and *What a round trip does and does not preserve* stay word for word.
- *Import* becomes D1's summary, with a link to `../app-notes/an02-gerber-import.html`.
- `grep` `docs/user/src` for every link into the moved text (`layout-editor.html#gerber` and any anchor inside the
  moved block) and repoint any that no longer lands on what it describes.
- `file-formats.md` and `cli.md` link `#interchange`, which stays. Check them anyway.

**R-an02-8 — Voice.** Follow the docs rules already held in memory and the owner's standing feedback:
- no change history ("now", "used to", "was updated");
- no commercial vendor or product names, `.kicad_pcb` excepted;
- no prose a reader already knows;
- never the phrase "the design's own".

Plain, specific, second person, as in AN-01.

**R-an02-9 — Visual check, as R-gt-10.**
- Capture every new row in both variants into the scratchpad with
  `UiArtworkGenerator.RenderScene(row.Build(), row.Width, row.Height, variant, path, row.Chrome, row.MustContainPopup)`.
- Rasterise with Svg.Skia, registering every `docs/user/assets/fonts/*.ttf` first. Before loading, rewrite
  `fill-opacity` to `opacity` on `<text>`: Svg.Skia ignores the former, so disabled text otherwise looks enabled.
  Never use `qlmanage`.
- Look at every PNG and confirm each of these:
  - nothing is clipped at a figure edge, and no dialog's Continue or Close button is cut;
  - no table row is cut through its text;
  - popups rendered and are not clipped;
  - no path, time or machine-specific text appears;
  - dark-variant text is legible.
- Fix and recapture until each holds. Put every final PNG in the completion report.

## 5. Not in this phase

- Running DocGen, or committing generated SVGs or HTML. The owner regenerates at the end.
  `DocsFactoryTests.EveryCapturedFigureExistsInBothVariantsAndDrawsSomething` is red for every new id until then.
  That is expected; name it in the report.
- Rewriting `artwork-to-schematic.md` beyond D4's one sentence.
- Moving any other interchange format out of `layout-editor.md`.
- Changing the example workspace, its README or its fab files.
- Any product change other than R-an02-6.

## 6. Gates

- `dotnet build` of `src/Ui` with no new warnings in the fixture files.
- No new tests unless R-an02-6 changes a sentence a test asserts. If it does, run only that test class plus
  `GerberImportTargetTechnologyTests`.
- R-an02-2's anchor self-check, R-an02-5's number provenance list and R-an02-9's PNGs, all in the completion report.
- Findings go in `src/Ui/Diagnostics/RESOLVED.md` (fixtures and capture) and, if R-an02-6 is used,
  `src/Design/RESOLVED.md`. They never go in a CLAUDE.md.
