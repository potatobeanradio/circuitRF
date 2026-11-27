# Brief PCD-12 — Filter synthesis on the PCell Designer

**Series:** [PCell Designer](brief-pcd-0-overview-and-contract.md) · **Tag:** `R-pcd12-n`
**Area:** `src/Core/Synthesis/Filters/` (`LowpassPrototype`), `src/Core/Systems/FilterLadder.cs` and
`src/Core/Match/MatchSynthesis.cs` (R1 only, bit-identical), `src/Design/Filters/FilterSynthesisService.cs`, new
`src/Design/PCellDesigner/PCellFilter.cs`, `src/Design/Cells/CellPersistence.cs` (one additive field),
`src/Ui/Filters/FilterDesignerViewModel.cs` and `src/Ui/Views/Filters/FilterDesignerWindow.axaml(.cs)`, new
`src/Ui/PCellDesigner/FilterMetricsPanel.axaml(.cs)`, `src/Cli/FilterVerb.cs`, `docs/user/src/reference/filter-designer.md`,
`docs/user/src/reference/cli.md` (the `filter` lines), `docs/design/filter-synthesis.md`
**Depends on:** PCD-0, PCD-5 (`PCellDesign.Create`, `PCellDesignCheck`, `PCellPackage`), PCD-4 (`CellNetlistMerge`),
PCD-6 (`PCellDesignResponse`, the designer's Save as the pattern); the Filter Designer (filter-synthesis.md §6, §6c).

---

## 0. What this adds

The Filter Designer realises a filter as one built-in part and its site parts; until now that realisation could only be
placed into a schematic as fixed values. This lane makes it a **designed cell**: the realised parts, their site parts and
two Ports, the synthesis dimensions as the cell's parameters, so the PCell Designer, `circuitrf pcell` and every bench
treat a synthesised filter as any other designed cell. The cell keeps its specification, so its metrics are read back
from its own circuit response or an EM result, and a hairpin or open-loop cell can be polished on its parameters. On the
way, the lowpass prototype's g-values get one home where that changes no result.

## 1. Requirements

- **`R-pcd12-1` — one lowpass prototype source, where nothing moves.** The three Chebyshev g-value recurrences
  (`LowpassPrototype.GValues`, `FilterLadder.ChebyshevG`, `MatchSynthesis.RippleG`) are routed through one
  implementation only where every existing result stays bit-identical. The LC Filter Designer's numbers do not change.
- **`R-pcd12-2` — Make PCell from a realised filter.** The Filter Designer's **Make PCell…** and
  `circuitrf filter … --realiser R --pcell <name>` turn the selected realisation into a designed cell through one Design
  function, `PCellFilter.Make`: `PCellDesign.Create`, the cell's schematic merged from the realisation's circuit by
  `CellNetlistMerge`, and the designer's Save (parameters, `PCellDesign`, `Terminals`, symbol, `.csch`, `.clay` at the
  defaults). The cell holds the realised part, its site parts and Ports 1 and 2. Each synthesis dimension is a cell
  parameter, bound into the part as an expression, with a range. `PCellDesignCheck` passes and `PCellPackage.Export`
  writes the `.cpcell`.
- **`R-pcd12-3` — filter metrics for a designed cell.** The cell keeps its specification in `CcellPCellDesign.Filter`
  (additive, `WhenWritingNull`). `PCellFilter.Metrics` runs `FilterExtraction` (through
  `FilterSynthesisService.Extract`) on the cell's circuit response (`PCellDesignResponse.Run`) or on any S-parameters
  (an EM result), returning the extracted filter with each check's pass or fail. `FilterMetricsPanel`, a self-contained
  Avalonia `UserControl`, shows them with the Filter Designer's own rows. The designer window is not edited: PCD-10/11
  place the control.
- **`R-pcd12-4` — polish for a designed filter cell.** `PCellFilter.Polish`: the realisation read back from the cell at
  its parameters' defaults, `FilterPolish.Run` on it, the polished dimensions written back as the defaults, and the cell
  saved again. Hairpins and open loops in a row (what `FilterPolish.Refusal` passes); any other cell is refused with that
  sentence.

## 2. Decisions

- **D1 — R1: one home for the handbook recurrence, the exact one stays apart.** `FilterLadder.ChebyshevG` and
  `MatchSynthesis.RippleG` are the same text (the handbook's 17.37 for 40/ln 10, `sin·sin`, the angle as `(2k−1)·θ`).
  That body moves verbatim to `LowpassPrototype.TableChebyshevG`, and both call it: bit-identical by construction.
  `FilterLadder.ButterworthG` and `GValues`' Butterworth case are the same arithmetic too (`(2k−1)` is exact either as
  an int or as `2.0·k − 1.0`), so both call `LowpassPrototype.ButterworthG`. `GValues`' Chebyshev case is **not** merged:
  it takes 40/ln 10 exactly (filter-synthesis.md §1: the ladder is equiripple to rounding), squares with `Math.Pow` and
  forms the angle as `(2k−1)·π/(2N)`. One body for both would move either the LC Filter Designer's values (the
  constant, about 1e-5 relative) or every realiser's (the last bits), which the rule forbids; so the two Chebyshev forms
  stay, side by side in `LowpassPrototype`, each saying why.
- **D2 — One function, two entry points.** `PCellFilter.Make(workspaceRoot, cellName, spec, realisation, realiser)` is
  what the window and the verb call; the verb holds no logic of its own. The technology is the workspace's, resolved as
  the designer resolves a new cell's.
- **D3 — The schematic is the Filter Designer's bench without its sweep and substrate.** `FilterSynthesisService.Circuit`
  is the old `Cnl` body before its analysis line (and `Cnl` is now `Circuit` plus that line, the same bytes). The cell
  takes it with no substrate stated, so the part and its site parts take the workspace technology's substrate and layers
  as any designed cell's parts do, and `CellNetlistMerge.Merge` draws it into the new cell.
- **D4 — Which values become parameters.** Every row of the realisation holding one positive number with a length unit
  (`12.3mm`, `0.5 mm`): MCRC's W, L1…LN, S1…S(N+1), Tap, Sa, Gap, Wc; MCLA's W[k], S[k], L. The parameter is named as
  the row with its brackets dropped (`W[2]` → `W2`), its default the number in the row's unit, its range
  ±`FilterTuning.GeometryRange` (20 %), the band the optimizer hand-off already tunes over. Counts (N), words (Shape,
  Feed), MLAD's `Sections` text, frequencies and the site parts' values stay literal: they are not dimensions.
- **D5 — Save as the designer saves.** `PCellFilter` writes what the designer's Save writes (the parameters,
  `PCellDesign` with `Sweep` and `Filter`, `LayoutGenerator`, `EmCopper`, `Terminals`, `NumPorts`, the symbol from
  `AutoSymbolGenerator`, the `.csch`, the `.clay` from `DesignedCellLayout.At` at the defaults). The designer's Save is in
  the window's view model, which the CLI cannot reach; moving it to a Design function both call is PCD-6's to make.
- **D6 — The sweep.** `CcellPCellDesign.Sweep` is the Filter Designer's bench sweep (`FilterSynthesisService.Frequencies`,
  401 points), so the designer's live response shows the band and the metrics have their samples.
- **D7 — The stored spec.** `CcellFilter`: `Response`, `Band`, `Order`, `F1Hz`, `F2Hz`, `RippleDb`, `StopAttenuationDb`,
  `Z0`, `TransmissionZerosHz` (omitted when none) and `Realiser`. Its own class, not `FilterSpec`, whose computed
  properties would be written too. The designer's Save keeps it (`PCellDesign ??=`).
- **D8 — The panel binds one value.** `FilterMetricsPanel.Metrics` (a `PCellFilterMetrics?`) is all it takes; the rows
  are `FilterMetricRow.SpecRows` and `MatrixRows`, which the Filter Designer now builds its own tables with too.
- **D9 — The window's new cell name.** Make PCell… names the cell `FilterPCell<k>`, the next free, and opens the PCell
  Designer on it; renaming is the project tree's (PCD-6 D3). The verb takes the name given.

## 3. Tests

`tests/Ui.Tests/PCellDesigner/Pcd12*.cs`, one per requirement, on the `microstrip-0508` scratch workspace
(`Pcd5Workspace`), with a three-pole tapped hairpin: R1's delegates against the old bodies, bit for bit; Make PCell's
parameters, bindings, check and export, and the verb's cell; the metrics of the cell's circuit response and the panel's
rows; one polish pass. The existing `FilterDesigner*`, `FilterExtraction*`, `FilterPolish*`, synthesis, `FilterLadder`
and LC Filter Designer tests stay green.

## 4. Possible enhancements

- **The site parts' values as parameters.** A lumped ladder's L and C are its design; as parameters they would make an
  MLAD cell tunable from a bench. They are not dimensions, and a value range wants the part's tolerance, not 20 %.
- **MLAD's sections as parameters.** Its lengths and widths live inside the `Sections` text, which no expression
  reaches; the part would need indexed rows first.
- **Calibrate from EM for a designed cell.** `FilterPolish.Run` takes an EM response already; feeding it the cell's own
  Run EM would correct the cell against its layout, at one EM run per pass.
- **A Filter Designer reopened on a designed filter cell.** The stored spec is enough to open it with the spec filled in
  and the cell's dimensions as the realisation.
