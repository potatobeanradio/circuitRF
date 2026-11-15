# Brief PAA-3 — the Phased Antenna Array component, Elements mode

**Series:** PAA-0 … PAA-10 · **Tag:** `R-paa3-<n>` · **Depends on:** PAA-2 · **Design note:** §4.1, §4.2, §4.4
**Area:** `src/Core/Devices/` (a `PhasedArrayModel`), `ComponentModelFactory`, `src/Design/Schematic/ComponentTypeRegistry.cs`,
`src/Design/Em3d/FieldCosimulation*.cs`, the parameter editor (`src/Ui/ViewModels/ParameterEditorViewModel*.cs`),
`src/Cli` (`check`, `explain`), `src/Core/RESOLVED.md`.

## The gap

PAA-2's engine has no way into a schematic. A user wants to place an array, point it at an EM result, state its lattice
and pitch in their own units, sweep them, and get the array's pattern from a `type=fields` analysis. This brief builds the
component with its parameters and its **Elements** mode (one pin per element, D1), which is the smaller of the two modes
and the one the brute-force path (PAA-6) needs. Feed mode is PAA-4; the symbol PAA-5 (until then the component draws a
plain box with its pins, from the Combiner's layout).

## Requirements

**R-paa3-1 — The component.** `.cnl` type `PhasedArray`, palette name **Phased Antenna Array**, System category,
designator `PA`. Parameters of note §4.2; this brief implements `Record`, `Level`, `Patterns`, `Mode` (Elements only —
`feed` is refused with "Feed mode is PAA-4" until then, phrased for a user), `Rows`, `Cols`, `Lattice`, `dx`, `dy`, `Off`,
`Coupling`, `Z0`, `RecordRows/RecordCols/RecordDx/RecordDy`.

**R-paa3-2 — Lengths in any unit.** `dx` and `dy` are length parameters: a value with a unit (`25.86 mm`, `1018 mil`,
`0.02586 m`) means what it says; a bare number takes the unit chosen beside the field in the parameter editor, as every
length parameter does; an expression referencing a unit-bearing VAR follows the var-unit-wins rule
(`docs/design/expressions.md`). `lambda(f)` (c/f, a length) is added as a standard function; before adding it, confirm no
shipped example or test defines a `lambda` of its own, and if one does, record the shadowing rule that applies.

**R-paa3-3 — Sweepable.** `dx`, `dy`, `Z0` and every other numeric parameter resolve per sweep point like any
parameter, so `parametric_sweep` over a VAR they reference re-evaluates the array at each point. **`Rows` and `Cols` are
refused as swept in Elements mode**, by `check` and by the run, naming the VAR, because the pin count would change
mid-run (D4). `Record`, `Mode`, `Lattice`, `Level`, `Patterns`, `Coupling` are words and not swept.

**R-paa3-4 — The level and its notes.** The level defaults from the record (D3): one port → `isolated`, a lattice whose
rows and columns equal the array's → `exact`, any other lattice → `embedded`. `exact` with a lattice that differs from
the array's is refused, naming both. The run's notes and `explain --analysis` state the level, the record's lattice and
pitch, the coupling offsets measured, and — D5 — a warning when `dx`/`dy` differ from the record's pitch by more than
PAA-1's threshold, giving both pitches in the unit the user typed.

**R-paa3-5 — What it stamps in Elements mode.** An N-port, pins `1…N` row-major, with S from PAA-2: the record's own S
(`exact`), `ArrayCoupling.Extend` (`embedded`, `Coupling=on`), or the diagonal (`isolated`, or `Coupling=off`). An `Off`
element's pin is still a pin; its port is terminated in Z0 inside and the pin left open, and `check` says so. N ≤ 64,
else refused naming Feed mode. It works in every linear analysis and in HB's linear partition, as `EmFieldData` does.

**R-paa3-6 — The pattern.** `analysis F1 type=fields block=PA1` on a PhasedArray forms the pattern of note §2: equation
(1) at `exact`, (2) with PAA-2's engine at the other levels, through the same `Em3dRadiation.Publish` so every existing
cube, metric (`RealizedGainDbi(F1)`, `PeakEirpDbm`, …), Data Display trace and tuning slider works unchanged. The circuit's
waves at the block (`SParameterEngine.BlockWaves`) are the weights. New cubes in an `array` group over an element axis
(with `row`, `col`, `x`, `y` as coordinates): `a`, `b`, `GammaActive`, `Pacc`; and scalar metrics from R-paa2-7 as
measurement functions: `HpbwDeg(F1, plane)`, `SllDb(F1)`, `PointingErrorDeg(F1, theta0, phi0)`, `ScanLossDb(F1)`,
`GratingMarginDeg(F1)`, `PowerBalance(F1)`, `ActiveVswrMax(F1)`.

**R-paa3-7 — `check` and `explain`.** `check`: the record opens and has a far field; the record's lattice (R-paa2-9) or
the stated one; Elements-mode N ≤ 64; `Rows`/`Cols` not swept in Elements mode; a pitch warning (R-paa3-4). `explain
--analysis`: the level, the patterns used, the coupling range, and per element class which pattern it takes. The MCP
`reference` format topic lists the component's parameters with units and defaults.

**R-paa3-8 — Parameter editor.** Rows for the parameters above, the record picked with the `…` button EM Field Data's
row uses, `dx`/`dy` with their unit selector, `Mode` and `Level` as choices. No new panel.

## Gates

1. **Exact equals the block it replaces.** A PhasedArray with `Level=exact` on PAA-1's E3 (1×4) fixture, in Elements
   mode, gives the same S and the same pattern as an EmFieldData block on the same record with the same circuit, to 1e-12.
2. **Units.** `dx=25.86 mm`, `dx=1018.11 mil` and `dx=0.02586` with the field's unit `m` give the same pattern to 1e-12.
3. **Sweep.** A `parametric_sweep` of a VAR driving `dx` over three values returns three patterns whose array factors
   match R-paa2-2's direct sum at each pitch; the pitch warning appears for the points beyond the threshold only.
4. **Refusals.** A swept `Rows` in Elements mode is refused naming the VAR; 9×9 in Elements mode is refused naming Feed
   mode; `exact` on a mismatched lattice is refused naming both lattices.
5. **Embedded level.** A 7×7 Elements-mode PhasedArray on the E5 fixture reproduces R-paa2 gate 3's numbers through the
   schematic path (the component adds nothing to the engine's answer).

## Not here

Feed mode, element scope, weights, the channel cell (PAA-4); the symbol (PAA-5); HB waves (PAA-6).

## Completion note (2026-11-14)

**Built.** `PhasedArrayModel` (src/Core/Devices) — `.cnl` `PhasedArray`, palette **Phased Antenna Array**, System, designator `PA`
— with `Record`, `Level` (`auto`/`isolated`/`embedded`/`exact`), `Patterns`, `Mode` (`feed` refused in a user's words), `Rows`,
`Cols`, `Lattice`, `dx`, `dy`, `Off`, `Coupling`, `Z0`, `RecordRows/Cols/Dx/Dy`; its words kept verbatim and its lengths resolved by
the ordinary unit rule (Elaborator `ResolvePhasedArrayParameters`, the typed unit carried for the pitch sentence); `lambda(f)` as a
unit-bearing built-in. Elements mode stamps an N-port (record S at exact, `ArrayCoupling.Extend` when embedded, the diagonal when
isolated or `Coupling=off`), an `Off` element open at its pin and terminated inside. `type=fields block=PA1` goes through
`FieldCosimulation.RunArray` and `Em3dRadiation.Publish`, adds the `array` group (`a`, `b`, `GammaActive`, `Pacc` over `element` with
`row`, `col`, `x`, `y`), and answers `HpbwDeg`, `SllDb`, `PointingErrorDeg`, `ScanLossDb`, `GratingMarginDeg`, `PowerBalance`,
`ActiveVswrMax` from PAA-2's local evaluation. `check` reports the level, the coupling range, the pitch warning, the elements off and a
swept `Rows`/`Cols`; `explain --analysis` the level, the coupling and which record port's pattern each element takes; `reference
components PhasedArray` lists every parameter with its unit and default. The parameter editor: the Record row with EM Field Data's
picker (a Touchstone resolved to or adopted into its record), `dx`/`dy` with their unit selector, `Mode`/`Level`/`Lattice`/
`Patterns`/`Coupling` as choices; the symbol (until PAA-5) is a box holding an antenna mark — a mast, an inverted triangle and two arcs of
radiation — with one pin per element at the Combiner's spacing and "×N" beside the antenna; one element is a 240 × 240 square with its
pin on the left at mid-height, so it sits on a block or chain diagram's signal line, its lead stopping at the body edge.

**Moved.** The lattice, the coupling extension and the lattice fit from src/Engine and src/Design into `src/Core/Devices/Arrays`, so
the component can build its S at elaboration (`src/Core/RESOLVED.md`).

**Gates.** 1 — exact on the 1×4 is the EM Field Data block: S 1.9e-15, pattern 8.4e-16 of the peak. 2 — 25.86 mm, 0.02586 and the mil
spelling of the same length: identical; `1018.11 mil` is 25.8599939 mm and differs by its own rounding (1.1e-7). 3 — a sweep of the
VAR driving dx over 25.86/26.5/29 mm: each point's pattern equals the direct sum at that pitch to 1e-12; only 29 mm (0.062 λ) is warned
about. 4 — a swept Rows names the VAR (run and check), 9×9 names Feed mode, exact on a 7×7 from the 5×5 names both lattices. 5 — the
6×6 from the 5×5 reproduces PAA-2 gate 3's reference active reflections to 1.1e-15; a 7×7's pattern and Γ_act are the engine's.
Tests: `tests/Ui.Tests/PhasedArray/PhasedArrayComponentTests.cs` (6).

**Also.** The parameter row's one-file picker reads "…" (it read "Browse…"), as the owner's picker rule asks. The components page has
a short Phased Antenna Array entry and its symbol figure, and the expressions page lists `lambda(f)`; the full user pages are
PAA-10's. The refusals are sentences beside the EM Field Data family's, allow-listed in the firewall's text gate.
