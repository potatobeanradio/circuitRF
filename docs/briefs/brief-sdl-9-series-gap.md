# Brief SDL-9 — A series gap part (MGAP)

**Series:** [Schematic-driven layout](brief-sdl-0-overview-and-groundwork.md) · **Tag:** `R-sdl9-n`
**Area:** new `src/Core/Devices/MicrostripGapModel.cs` and `src/Core/Devices/Microstrip/MicrostripGap.cs`,
the component-model factory, new `src/Design/Layout/PCells/MGapPCell.cs`, `PCellRegistry`, the component
catalogue and palette, `testdata/` (reference data)
**Depends on:** SDL-1 (placement). Independent of SDL-4 to SDL-8; SDL-7's gap tests use it.

---

## 0. What this adds

A gap in the metal that matters to the design has to be in the schematic, or nothing can place it and
nothing can optimise it. Coupled lines carry their spacing S. A gap between two line ends — a
gap-coupled resonator filter, a DC break, an end-coupled feed — has no part to express it: there is no
gap model among the microstrip discontinuities and no gap generator among the PCells. This brief adds
one, so an end-to-end gap is a part with a parameter `S` that placement keeps exact and SDL-7 can
optimise.

## 1. Decisions

- **D1 — MGAP is a two-port part between two line ends:** parameters `W` (the line width) and `S` (the
  gap), with the substrate taken as MLIN takes it. Its circuit model is the usual equivalent for a gap
  in a microstrip line: a series capacitance across the gap and a shunt capacitance at each end to
  ground, from closed-form expressions valid over a stated range of `S/h` and `W/h`. Outside that range
  the model warns through `IReportsWarnings`, as the other discontinuities do (`MicrostripValidity`).
  Derive and write the expressions in our own words; no citations in the repo.
- **D2 — The PCell draws no copper.** Its layout is two pins facing each other across `S`, each of width
  `W`, on the line's layer. SDL-1's chain then places the line on each side so its end sits on a pin:
  the gap is exactly `S` by construction, with no special case in the placer. Its two pins are on
  different nets, so LVS and SDL-7's connectivity signature see a designed gap, not a broken joint.
- **D3 — `S > 0` is the part's own rule.** `S ≤ 0` is a model error at elaboration (the two lines would
  touch, which is not a gap). The optimiser's infeasible check (SDL-7 D9) sees the same thing through
  the signature, without asking the model.
- **D4 — Validation against independently generated reference data.** CLAUDE.md's rule: a numerical
  addition needs a `testdata/` regression test with externally generated references. Produce the
  reference S-parameters with an EM solver independent of the closed-form model (one of the 3D solvers
  circuitRF drives, or the planar solver, each on a meshed gap), over a grid of `S` and `W` on the
  shared test substrate, commit them as fixed data, and gate the model against them within a stated
  tolerance on |S21| and phase. State where the closed form departs from the EM reference instead of
  tuning the expressions to hide it.

## 2. Work

- **`R-sdl9-1`** Model and factory registration; catalogue entry and symbol; parameter defaults.
- **`R-sdl9-2`** `MGapPCell` (D2) and registration; pins with `OutwardDirectionDeg` facing across the gap.
- **`R-sdl9-3`** Reference data and the regression test (D4).
- **`R-sdl9-4`** Add design `C8 gap-coupled filter` to the SDL test-design set: three half-wave
  resonators separated by two MGAPs; LVS clean, gaps exactly `S` in the layout.

## 3. Docs

User reference page for MGAP in the microstrip parts section: parameters, validity range, what the layout
draws (pins only). `layout-view.md` §9.1: D2.

## 4. Gate

`dotnet test tests/Core.Tests --no-build`, `dotnet test tests/Ui.Tests --no-build --filter "FullyQualifiedName~SchematicSync|FullyQualifiedName~PCell"`,
`tests/Firewall.Tests`. Findings in `src/Core/RESOLVED.md` (model) and `src/Design/RESOLVED.md` (PCell).
