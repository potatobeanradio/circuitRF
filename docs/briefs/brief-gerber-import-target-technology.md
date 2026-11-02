# Brief — Gerber import into an existing technology

**Tag:** `R-gt-<n>` · **Standalone** (no series) · **Depends on:** nothing unmerged
**Area:** `src/Design/Layout/Interchange/GerberImport.cs` (+ `GerberImportEntry.cs`),
`src/Ui/Views/Dialogs/LayerMappingDialog.axaml[.cs]`, `src/Ui/ViewModels/WorkspaceViewModel.cs` (the Import Gerber
command), `src/Cli/LayoutConvert.cs` + `src/Cli/Serve/ToolCatalog.cs` (the `convert` flag),
`src/Ui/Diagnostics/FigureCatalog.cs` + a fixture (the figures), `docs/user/src/reference/layout-editor.md` and
`docs/user/src/reference/cli.md` (sources only)

---

## 1. Goal

Let a Gerber import use a technology the workspace already has, which no import can do today. The `.clay` then
references that technology, and the import writes **no `.ctech` of its own**.

Two costs of the current behaviour drive this. The first one matters most.

1. **The stackup the import invents looks real.** With no job file, the import's own `.ctech` gets a generic FR-4
   substrate: 35 µm outer and 18 µm inner copper, εr 4.4, and the dielectrics sharing 1.778 mm evenly. On a 6-layer
   board that puts 355.6 µm under Top Copper where the real build (`pcb-6layer_FR-4_63mil_1oz`) has 220 µm. A
   microstrip on that layer then solves at the wrong impedance, and the run succeeds. The import says so once, in
   its summary, and nothing repeats it.
2. **The workspace fills with technologies nobody uses.** A user who imports many boards against one fab stackup
   gets one stray `<board>.ctech` per import, each with a wrong stackup. In the Project tree they look like real
   technologies.

**Change Technology…** after the import does work. This brief does not remove it. The point is that the right
answer should be available at the moment the user already has the question in front of them.

## 2. What exists (verified 2026-10-09)

- **`GerberImport.Import(files, parentDir, importName, destTech, …)`** already takes a destination technology.
  - It uses that technology for layer IDENTITY: rung 2's `GerberSuffix` and the name lookups in
    `GerberLayerIdentity.cs`.
  - It uses it for the mapping rows (`LayoutLayerMapping.Propose`, `GerberImport.cs` ~l.670), and as a DONOR of
    layer keys, names and colours (~l.1811, ~l.2087).
  - It **never** uses that technology's stackup. `BuildTechnology` (~l.1019) builds the stackup from the job file
    or the FR-4 fill.
  - It writes `<parentDir>/<importName>/<importName>.ctech`, and the `.clay`'s `TechRef` points at it (~l.1246).
  - The workspace technology is asserted unmodified (R-L4g-8; `src/Ui/RESOLVED.md` §3 of the L4g notes).
- **The GUI command** (`WorkspaceViewModel.cs` ~l.5347) passes the workspace's default technology as `destTech`.
  It **always** shows the shared `LayerMappingDialog` for Gerber (`alwaysAsk: true`), so that dialog is already a
  step in every Gerber import.
- **`convert`** takes `--tech <path>` and passes it as `destTech`, but still mints a `.ctech` of its own
  (`LayoutConvert.ImportGerber`). Every other importer goes through `MintTechnology`, which clones `destTech` and
  **keeps its stackup** ("never replace a stackup that is already there"). Gerber is the one importer that discards
  the destination stackup.
- **railRF passes `destTech: null` on purpose** (`src/Ui/RESOLVED.md`, "destTech is null on railRF's own board
  import"). That path must behave exactly as it does now.
- **Choosing a technology already has a pattern.** `ChangeTechnologyDialog`'s second constructor lists
  `WorkspaceTechnologyChoices.Enumerate(...)` (every `.ctech` anywhere in the workspace) and then the catalog's
  technologies. A catalog choice is copied into `tech/` the way New Workspace copies one (`WorkspaceCreate`, with
  its material libraries). Reuse both. Do not write a second enumeration or a second copy.
- **Name matching already works for the motivating case.** A scratch harness (2026-10-09) imported a synthetic
  6-layer set against the shipped `pcb-6layer_FR-4_63mil_1oz` in two naming styles: X2 `%TF.FileFunction`
  attributes, and extension-only (`.GTL`, `.G1`–`.G4`, `.GBL`, …). In both, **13 of 13 artwork files landed on the
  technology's existing layers by name, and no layer was added.** Mapping is not the missing part. Only the stackup
  and the stray file are.

## 3. Decisions (the owner may overturn any of these before work starts)

- **D1 — Where the choice lives: a Technology row at the top of the Layer Mapping dialog, Gerber imports only.**
  Every Gerber import already passes through this dialog, and the choice changes what the table proposes. Putting
  both on one screen lets the user see the result of the choice. No new dialog and no new step. The GDSII, DXF,
  board, retarget and paste callers of `LayerMappingDialog` do not show the row.
- **D2 — The choices.** In order:
  - **New technology from the files**, which is today's behaviour;
  - every `.ctech` in the workspace (`WorkspaceTechnologyChoices`);
  - the catalog's technologies, labelled as `ChangeTechnologyDialog` labels them ("built in, copied into tech/").
    Choosing one copies it into `tech/` when Continue is pressed, not before. If `tech/<id>.ctech` already exists
    with identical bytes, the existing file is used and nothing is written.
- **D3 — The default selection.** Pre-select the workspace's default technology when its conductor count equals
  the set's copper count. Otherwise pre-select **New technology from the files**. railRF (`destTech: null`) and any
  caller with no workspace get **New**, with the row hidden.
- **D4 — A choice whose conductor count differs from the set's copper count is listed but disabled.** Its item
  text carries the count, e.g. `pcb-4layer_FR-4_62mil_1oz — 4 copper`, so the reason is visible without prose.
  Six copper files against a 4-layer technology means the user picked the wrong technology. Do not offer it as a
  guess.
- **D5 — Using an existing technology never modifies it.** No layer is added, no stackup entry is changed and no
  `GerberSuffix` is written. The file's bytes are identical before and after, and a test asserts it. This is
  R-L4g-8's rule, which still holds: the import references the shared technology and does not mutate it.
- **D6 — The CLI default is unchanged (New).** Headless there is no workspace whose default could be pre-selected.
  Adopting an existing technology is an explicit flag (R-gt-7).
- **D7 — The chosen technology wins over a job file.** If the set has a `.gbrjob` whose stackup disagrees, the
  import warns and names the differences, but the user's explicit choice wins.

## 4. Requirements

**R-gt-1 — The target, as a parameter of the one import.** Add an optional target to `GerberImport.Import` and to
`GerberImportEntry.Run` / `RunFolder`, for example
`GerberTechnologyTarget { New, Use(absolutePath) }`; the exact shape is the implementer's call. Omitted, it means
**New**: every existing caller, railRF included, behaves exactly as now, and the existing Gerber tests are the proof.
Under **Use**:
- identity, rows and donation run against the chosen technology;
- **no `.ctech` is written**;
- the `.clay`'s `TechRef` is the stored relative path from the layout folder to the chosen file
  (`RefPath.ToStored`, as l.1246 does now);
- the returned `ImportResult.TechPath` / `.Technology` are the chosen file and its loaded model.

**R-gt-2 — Re-proposing without re-reading.** Changing the dialog's Technology choice re-proposes the rows against
the new technology live. That requires running the import's post-read stage (identity → source layers → `Propose`)
again for a different technology **without reading the files a second time**. Read time dominates on a real board:
~1.8 s Release on a 20-layer set. Factor the import so that stage can run again. Expose it to the dialog through the
mapping callback, for example a request object carrying the rows, the candidate list and a
`Repropose(Technology?)` function, with the rows plus the chosen target coming back. The dialog's other callers keep
their current signature.

**R-gt-3 — Copper must agree with the chosen stackup, or the import refuses.** Under Use, each of these refuses and
creates nothing, with one sentence naming the files and layers involved:
- the number of copper files differs from the technology's conductor count. D4 normally prevents this; the check is
  there for the CLI and for callers with no dialog;
- a copper file is mapped to a drawing layer that no `StackupKind.Conductor` entry binds;
- two copper files are mapped to one conductor;
- a copper file's resolved rank (job file, X2 or heuristic order) differs from the stackup rank of the conductor it
  lands on. Example: the file ranked 2nd landing on the 4th conductor.

**R-gt-4 — Nothing is added to the chosen technology.** Under Use the Action column does not offer **Add to
technology**. An artwork row with no counterpart in the technology defaults to **Keep unknown**, and may be mapped to
an existing layer. The summary names every layer kept unknown. A copper row can never be kept unknown, because
R-gt-3 refuses first.

**R-gt-5 — Drill data.** Under Use, each drill file must map to a layer bound by a `StackupKind.Via` entry of the
chosen technology, so rebuilt vias carry that entry's span. If none binds it, the import refuses and names the
drill file. Turning every via into a plain circle without saying so is the silent-loss class this import exists
to avoid. Reconciling blind or buried spans declared by the files against the technology's spans is §5's.

**R-gt-6 — The summary.** Under Use:
- the stackup paragraph and the FR-4 "NOT STATED BY ANY FILE" paragraph are **not** emitted, since nothing was
  filled in;
- the closing sentence "This import wrote its own technology, board.ctech, …" is replaced by one sentence naming
  the technology used and its path;
- a job-file disagreement (D7) is one warning that lists the differing entries through `StackupComparison`.

Under New, the summary is unchanged.

**R-gt-7 — `convert` and MCP.** Add `--into-tech <path.ctech>` for a Gerber source with a `.clay` target. It means
Use: import against that technology, reference it, and write none. Given together with `--tech`, it is refused as a
pair, not ordered. Given with any other source format, it is refused by format: those importers already keep the
destination's stackup through `MintTechnology`, so the flag has nothing to do there. R-gt-3/5's refusals exit 1
with the import's own sentence. Add the matching option to the `convert` row in `ToolCatalog.cs`. The CLI test
compares the `.clay` it writes **byte for byte** against the in-process `GerberImport.Import` with the same target,
which is the pattern every other convert gate follows.

**R-gt-8 — The dialog.** In `LayerMappingDialog`, Gerber only:
- **Layout:** a **Technology:** label and combo on one row above the table, left-aligned with the table's edge.
- **Combo contents:** D2's list, in D2's order, with the D4 entries disabled.
- **Default:** D3.
- **Behaviour on change:** the rows re-propose (R-gt-2), and the summary line on the bottom row updates.
- **What this adds and what it does not:** a tooltip on the combo that says what Use means ("The layout uses this
  technology; no new technology is written") — and **no** explanatory text under the combo. Panel notes are kept
  out of this UI on purpose.
- **Result:** the chosen target travels back through the result. Continue with a catalog choice performs D2's
  copy, then the import.

The dialog's declared size stays 1000×520 unless the visual check (R-gt-10) shows that it must change.

**R-gt-9 — User docs (sources only).** In `layout-editor.md` §Gerber ▸ Import:
- one paragraph on the Technology row, covering what Use does, the copper-count rule and the default;
- the existing "What you get … its own new `.ctech`" paragraph scoped to the New choice;
- the new figure, cited with `{{ui: gerber-import-technology}}`.

In `cli.md`, add `--into-tech` to `convert`. **Do not run DocGen.** The owner regenerates at the end of the series.

**R-gt-10 — Visual check of the dialog, through the figure catalog.** This is a gate, not a nicety.
- **Fixture.** Add `DocLayoutFixtures.GerberImportTechnology` (or a new `DocGerberFixtures`, if that reads better
  beside the others). It builds the `LayerMappingDialog` exactly as the Import Gerber command builds it:
  - a workspace holding `pcb-6layer_FR-4_63mil_1oz` and `pcb-4layer_FR-4_62mil_1oz` in `tech/`, with the 6-layer
    one as the default;
  - a synthetic 6-copper set like the 2026-10-09 harness (14 files: six copper, two mask, two paste, two silk,
    outline, drill), imported up to the mapping callback;
  - the Technology combo on the 6-layer technology.

  Build it with the real dialog and the real proposal code, never a mock-up.
- **Two catalog rows:**
  - `gerber-import-technology`: the dialog as it opens. Size **1000×486**, the declared 1000×520 less the synthetic
    title bar, as the `analyses-setup` row derives its size. Chrome `WindowFrame.Titled("Import Gerber — Layer
    Mapping")`. Its caption says what the row does, in the catalog's voice.
  - `gerber-import-technology-choices`: the same dialog with the Technology combo **open** (`MustContainPopup:
    true`), showing the New row, both workspace technologies (the 4-layer one disabled, with its count), and the
    catalog rows.
- **The check itself.**
  1. Capture both rows in both variants into the **scratchpad**, not `docs/user`, by calling
     `UiArtworkGenerator.RenderScene(row.Build(), row.Width, row.Height, variant, path, row.Chrome,
     row.MustContainPopup)`. This is the exact call `tools/DocGen/Pipeline/DocGenRun.cs` makes, so the pixels are
     the ones DocGen will commit. A throwaway test or a scratch harness is fine; neither is committed.
  2. Rasterise each SVG to PNG with **Svg.Skia, registering every `docs/user/assets/fonts/*.ttf` first**. Without
     the fonts, Skia falls back to a serif and the figure looks wrong when it is not. **Do not use `qlmanage`**: it
     forces a square canvas and crops a wide figure, which reads as clipping that is not there.
     `Picture.CullRect` gives the true extent.
  3. **Look at all four PNGs** and confirm each item below. Fix and recapture until every one holds:
     - the **Map to** column is fully visible at 1000 px, with no horizontal scroll needed (the historical defect
       recorded in the dialog's XAML comment);
     - the Technology row's left edge lines up with the table's, and its combo shows the 6-layer technology's full
       name **untruncated**;
     - the disabled 4-layer entry reads as disabled in **both** light and dark, and its count is legible;
     - the popup really rendered (the catalog fails a `MustContainPopup` row that drew nothing, but check it by eye
       too), and it is not clipped by the dialog's bottom edge;
     - all 14 rows are visible, or the table scrolls cleanly with no row cut through its text;
     - no prose block was added under the combo or the table;
     - dark-variant text and combo borders are legible against the background.
  4. Put the four final PNGs in the completion report, so the owner sees what was checked.
- **What happens to the test suite.** `DocsFactoryTests.EveryCapturedFigureExistsInBothVariantsAndDrawsSomething`
  will be red for the two new ids until the owner's end-of-series DocGen run writes their SVGs. This is expected.
  Name it in the report, and do not commit generated SVGs to make it green.

## 5. Not in this phase

- Changing the chosen technology in any way, including adding layers or `GerberSuffix` entries. D5 forbids it.
- Reconciling blind or buried via spans declared by a job file or drill-file attributes against the technology's
  via spans (R-gt-5 only requires a binding).
- A Technology choice for the GDSII, DXF or board importers. Their mint path already keeps the destination
  stackup.
- Any change to Change Technology…, to railRF's import, or to the New-mode output.

## 6. Gates (minimal tests; run only these classes)

`Ui.Tests` holds the Design-level import tests, so run `dotnet test tests/Ui.Tests --no-build --filter` with these
classes:

- **New: `GerberImportTargetTechnologyTests`** (Design level, no dialog). One test per claim:
  - **Use against the shipped 6-layer technology.** The 14-file synthetic set lands every shape on that
    technology's keys, writes no `.ctech` anywhere under the import folder, and gives a `.clay` `TechRef` that
    resolves to the chosen file. The chosen file's bytes are unchanged.
  - **Copper-count mismatch.** A 6-copper set into the 4-layer technology refuses, and the import folder does not
    exist afterwards.
  - **Rank mismatch.** A set whose X2 ranks contradict the mapped conductors refuses (R-gt-3, last bullet).
  - **Drill not bound.** A technology with no Via entry for the drill layer refuses (R-gt-5).
  - **Re-propose.** Run against technology A, then re-proposed against B, the rows equal a fresh run against B, and
    the files are read once (count the reads).
- **New: `LayerMappingDialogTechnologyTests`** (headless view model):
  - the row is absent for non-Gerber callers;
  - D3's default holds in both cases;
  - a D4 entry is disabled and carries its count;
  - Use offers no Add-to-technology action;
  - a catalog choice copies into `tech/` only when Continue is pressed, and reuses an identical existing copy.
- **New, in `ConvertCliVerbTests`:** `--into-tech` gives a `.clay` byte-identical to the in-process
  import; `--into-tech` together with `--tech` is refused; `--into-tech` on a DXF source is refused by format.
- **Existing classes, run unchanged to prove New mode did not move:** `GerberImportTests`, `GerberRoundTripTests`,
  `GerberImportEntryTests`, `LayerMappingDialogSourceTests`.
- **R-gt-10's visual check**, with its four PNGs in the report.

**Findings go in `src/Design/RESOLVED.md`** (the import) and **`src/Ui/RESOLVED.md`** (the dialog). They never go
in a CLAUDE.md.
