# Brief 35 — series parts from the window: a gesture, a library model, and more than one

**Series:** [railRF](brief-railrf-0-overview.md) · **Tag:** `R-rail35-n` · **Phase:** feature, model change
**Area:** `src/Design/RailRf/RailSpec.cs` (`SeriesElement`, `Refusal`), `RailPart.cs`,
`RailSeriesModel.cs`, `RailSeriesPartition.cs`, `RailPartResolver.cs`, `PdnSweep.cs`, `RailDcRun.cs`,
`RailPartDiscovery.cs`, `PartLibrary.cs`; `src/Ui/RailRf/RailPartRowViewModel.cs`,
`RailRfViewModel.Parts.cs`, `src/Ui/Views/RailRf/RailRfWindow.Parts.cs`, `RailRfWindow.axaml`
**Depends on:** 25, 31, 34 · **Blocks:** nothing
**Found by:** field report 5, 2026-09-23 — the designer's rail runs through a ferrite bead and a
resistor standing in for a load switch's on-resistance.
**Rule:** the board, its customer and the reporter must not be named anywhere in the repo.

---

## 0. What is missing after brief 25

Brief 25 built the model: `RailPart.Connection = Series`, a `RailSeriesModel` (an R-L or a measured
Touchstone curve, `RailSourceModel`'s shape reused), a partition of the rail read off the artwork, a
second frequency-model node, the DC breakdown row. Three things stop a designer using it:

1. **No gesture reaches it.** Nothing in `src/Ui` ever sets `RailPartConnection.Series`. A part added
   with the parts pane's **+** is a shunt row (`RailRfViewModel.Parts.cs`, `AddParts`), and the series
   fields — `SeriesResistanceOhms`, `SeriesInductanceHenries`, `DcResistanceOhms`, `TouchstoneRef` — are
   editable only by writing the `.crail`. `RailPartDiscovery` tells the user to *"add it as a series
   element"*, a gesture that does not exist. In the field report the designer added the bead and the
   resistor with **+**, got two shunt rows, typed their numbers into the part library instead, and
   (before a fix in this round) the bead was then solved as a 1 µF decoupling capacitor.
2. **The library cannot describe one.** The part library is keyed by part number and holds a
   CAPACITOR's model. Round 5 added the class `Other` ("not a capacitor", `PartLibraryRow.OtherClass`)
   so a bead is no longer counted as lacking a bias curve, but nothing reads an `Other` row as a series
   model. The same bead on four rails is four hand-typed rows today.
3. **One series element per rail** (R-rail25-2c). The designer's rail has two in a chain — the bead,
   then the switch — and brief 25's suggested workaround ("put it on a rail of its own") cannot
   express a chain: the load is behind both.

## 1. `R-rail35-1` — the window makes a part series, and edits it

- **`R-rail35-1a`** The parts pane's context menu gains **Make series element** / **Make decoupling
  (shunt)** on the selected rows, one undoable edit each, through the document's own command stack.
  A part `RailPartDiscovery` reported as spanning the rail offers it directly, and its sentence names
  the gesture instead of a `.crail` field.
- **`R-rail35-1b`** A series row edits its own model in the row: DCR, and EITHER an R-L OR a
  Touchstone file — `RailPart.Refusal` already refuses both at once, so the editor offers the choice,
  not three independent boxes. Units required on every field (round 5's rule: a bare number is
  refused where its scale is a guess; ohms may be bare).
- **`R-rail35-1c`** The capacitance column of a series row keeps brief 25's R-rail25-4a spelling (the
  element's impedance, never a capacitance). Its model-source column says where each number came from
  — the row, the library (R-rail35-2) or a file.

## 2. `R-rail35-2` — a library row classed `Other` is a series element's model

- **`R-rail35-2a`** For a row classed `Other`: **ESR is read as the DCR**, and **Model file** (a
  two-port Touchstone, series-thru, as supplier tools publish for beads) is its measured impedance —
  through the same `RailMeasuredPart` arithmetic `RailSeriesModel` already uses for `TouchstoneRef`.
  State in `PartLibraryRow`'s docs which of its fields an `Other` row uses and which it ignores.
- **`R-rail35-2b`** Precedence, per field: the rail row's own stated value, then the library row. A
  row that states nothing inherits everything, which is the "same bead on four rails is one edit"
  case. The model-source column says which won, per the R-rail2-11 rule.
- **`R-rail35-2c`** **No impedance-at-one-frequency field.** A datasheet's "220 Ω at 100 MHz" does not
  determine an R-L — the split between R and ωL is unknown, and any split chosen is a guess that
  prints as a model. The route to a bead's impedance is its Touchstone curve; without one it is R-L
  with brief 25's R-rail25-1c sentence, from values the user states.
- **`R-rail35-2d`** A shunt part whose library row is `Other` is refused at Run, naming the part:
  *not a capacitor, and not marked series* — the exact state the field report's rows were in.

## 3. `R-rail35-3` — more than one series element: sections form a tree

The frequency model is already NODAL: brief 25 has an upstream and a downstream node and stamps the
series element's admittance between them (`PdnSweep`, `NodeOf`). Generalise from two nodes to K.

- **`R-rail35-3a`** Cut the rail at EVERY series element's pads and walk (`RailSeriesPartition`'s
  existing walk): each galvanic region is a SECTION, each series element an edge between two sections.
  Parts, loads, sources and observation ports land in a section by their own pads, as today.
- **`R-rail35-3b`** The section graph must be a **tree rooted at the source's section** — a chain is
  the common case, a branch (one bead feeding two sub-rails) is fine. Then every series element's DC
  current is the sum of the loads beyond it, and every DCR is a breakdown row. **A cycle is refused**
  by name (two series paths between the same sections make the DC current split a copper question the
  lumped model cannot answer). **A bridged element** — both pads in one section — is refused as today
  (R-rail25-2b), now per element. **A section with no path to a source** is refused.
- **`R-rail35-3c`** With no artwork (R-rail25-2d), each part and load states its section by naming the
  series element it sits behind (or none); the typed route supports a chain only.
- **`R-rail35-3d`** The copper map shades each section (R-rail25-4c) — K sections, not two.
- **`R-rail35-3e`** Unmounting any series element opens the rail and is refused (R-rail25-3c), per
  element.

## 4. Gates

1. **A closed-form oracle, by hand**: source R-L → series R1 → section with C1 → series R2 → section
   with C2 and the load. Analytic Z(f) at every port, and the three ports differ. Not built from any
   railRF path.
2. **Every existing document is unchanged**: the shipped Power Rail example and brief 25's fixtures,
   whole `DataSet` bit for bit, before and after.
3. The DC drop through a two-element chain equals the hand sum (load current × each DCR, plus the
   copper).
4. The field report's shape, synthesised (no real part numbers): a bead row classed `Other` with a
   stated ESR and a Touchstone file, a resistor row with a stated ESR; both marked series from the
   window; the rail solves, both DCRs are breakdown rows, and the bead's R-rail25-1c sentence is ABSENT
   (it has a measured curve).

## 5. Tests (minimal — one per claim)

1. Make series / make shunt from the pane is one undo step each and round-trips the `.crail`.
2. An `Other` library row supplies DCR and Touchstone to a series row that states neither; a row value
   overrides it; the model-source column names the winner.
3. A shunt row whose library row is `Other` is refused naming the part.
4. Two series elements in a chain partition into three sections off a synthetic board.
5. A cycle is refused by name; a bridged second element is refused by name.

## 6. Scope

- No bias-dependent ferrite model (R-rail25-1c stands).
- No impedance-at-one-frequency field (R-rail35-2c).
- No cycles in the section graph.

## 7. On completion

Findings in `src/Design/RESOLVED.md` (the window half in `src/Ui/RESOLVED.md`), never in any
`CLAUDE.md`. Update the railRF reference's part-library section (`docs/user/src/reference/railrf.md`),
which currently says an `Other` row is "modelled by the ESR on its row" — true of its DC only until
this lands.
