# Brief VL-17 — Export to Library from Model Composer

**Series:** [Vendor libraries](brief-vendorlib-0-overview.md) · **Tag:** `R-vl17-m`
**Area:** Model Composer's window (`src/Ui/Views/ModelComposer/ModelComposerWindow.axaml`), a new
`src/Design/VendorLibraries/ModelLibrary/ModelCellFromModel.cs`, `ModelLibraryExport` (brief 10), the Export to
Library dialog, `circuitrf export part`, `docs/user/src/reference/model-composer.md`
**Depends on:** 10 (Export to Library), 13 (tolerances and corners), 15 and 16 (protected models).
**Rule:** overview §0's naming rule holds unchanged. Fixtures are synthetic (`TSTFET100`, `TSTCAP10`).

---

## 0. What this adds

A model made in Model Composer reaches a library today only through a schematic: the maker places the exported
`.cmodel` (or the passive cell) in a cell of their own, draws its pins, saves it, and runs **Export to Library…** from
the schematic editor. The schematic is pure ceremony — a FET with three pins wired to three Pins — and it is where a
pin gets misnumbered or a thermal pin forgotten.

Model Composer gains the schematic editor's **Export to Library…** button, and exports the model it holds directly.

## 1. Decisions

- **D1 — The same button, the same dialog, the same function.** The toolbar button is the schematic editor's
  (`ExportToLibraryBtn`: `PackageVariantClosedPlus`, `SelectionBtn` class, greyed with the reason as its tooltip),
  and it opens `ExportToLibraryDialog` on an `ExportToLibraryViewModel`. Export runs `ModelLibraryExport.Plan` and
  `Export` unchanged. Nothing about exporting is re-implemented in Model Composer.
- **D2 — The cell is synthesised, not drawn by the maker.** `ModelCellFromModel.Write(modelDocument, folder)` (in
  `src/Design`) writes a cell folder that places the model, and the export runs on that folder. The folder is
  temporary — `<document folder>/.export-<part>/` — and is deleted after the dialog closes; it is never offered as a
  cell of the workspace. Because the export copies the cell and extracts the copy, the library part is byte-for-byte
  what a hand-drawn cell would have produced.
- **D3 — What the synthesised cell holds.**
  - *Active (a `.cmodel`):* one `ComposedFET` with `File` pointing at the exported `.cmodel` (the Export step's own
    path; refused with the Export step's sentence when the model has not been exported or is stale), Pins `g`, `d`,
    `s` in that order, and `t` as pin 4 when the model self-heats. The cell's parameters are `N` (default the model's
    N0) and `Temp` (blank), forwarded to the FET, so a placed part takes a finger count and a temperature.
  - *Passive:* the cell the Passive Export step already writes (`PassiveExportSettings.Cell`), used as it is.
- **D4 — Defaults.** Part number: the `.cmodel`'s file stem (active) or the passive cell's name. Kind: Active / Passive
  as the document is. Symbol: the FET glyph for an active model (pins bind by name), the box otherwise. Frequency:
  the measured data's range when the document has one. Every one of these is still the dialog's to change.
- **D5 — Compile netlist works unchanged.** An active model's synthesised cell is "only a ComposedFET", so brief 16's
  R-vl16-1c path applies: the protected part is the FET's own standalone module. A table constituent is refused with
  the writer's own sentence, as brief 16 already does.
- **D6 — Greyed, with a reason.** The button is greyed (reason as tooltip) when: the document is unsaved; an active
  model has not been exported to a `.cmodel`, or the `.cmodel` is older than the document; a passive document has no
  Export step settings. It never silently exports something stale.

## 2. Requirements

- **`R-vl17-1` The synthesiser** (`src/Design`): `ModelCellFromModel.Write` per D3, deterministic (same document, same
  bytes), with a `.ccell`, a `.csch` drawn through `NetlistSchematic` from a one-line netlist plus Pins (as brief 16's
  wrapper is drawn), and no `.csym` (the dialog's symbol choice decides). It refuses rather than guesses: no exported
  model, a stale one, a passive document with no cell.
- **`R-vl17-2` The button** in `ModelComposerWindow`'s toolbar, per D1 and D6; on success the Messages lines are
  the schematic editor's (`WorkspaceViewModel.ExportToLibraryAsync`'s), so the function that posts them is shared,
  not copied.
- **`R-vl17-3` The CLI:** `circuitrf export part <file.cmodel|file.cmc> --library …` synthesises the cell the same
  way. A `.cmc` uses its Export step's `.cmodel`. Every other flag is `export part`'s.
- **`R-vl17-4` Cleanup:** the temporary cell is deleted whether the export succeeds, is refused or is cancelled. A
  leftover `.export-*` folder from a crash is deleted the next time the button runs.
- **`R-vl17-5` The page:** `model-composer.md`'s Export section gains "Publishing to a library", with the toolbar
  button, what the part's pins and parameters are, and a link to Protected Models for Compile netlist.

## 3. Tests (minimal)

1. An active ANN model's synthesised cell exports, and the library part's DC and S-parameters equal a hand-drawn
   cell's export of the same `.cmodel` (the existing self-check comparison).
2. A self-heating model gets pin `t` as pin 4; a model that does not gets three pins.
3. Each D6 condition greys the button with its sentence; a stale `.cmodel` is refused by the CLI with the same one.
4. The temporary folder is gone after a success, a refusal and a cancel.
5. Compile netlist from Model Composer gives the FET's standalone module (brief 16 test 1, through this path).

## 4. On completion

As brief 6 §9. The toolbar with the button, greyed and live, gets a `tools/UiSnapshot` render that has been looked at.
