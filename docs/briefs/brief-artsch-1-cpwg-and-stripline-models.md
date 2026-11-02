# Brief AS-1 — CPWG and SLIN: grounded coplanar waveguide and stripline components

**Series:** `brief-artsch-0-overview.md` (D13, D17, D18) · **Tag:** `R-as1-<m>`
**Depends on:** — (independent of every other phase)
**Area:** `src/Core/Devices/` (new `CoplanarLineModel`, `StriplineModel`, shared closed forms under
`src/Core/Devices/Planar/` or beside `Microstrip/`), `src/Core/Devices/ComponentModelFactory.cs`,
`src/Design/Schematic/` (`SchematicModel.cs` `SymbolKind`, `BuiltInSymbols.cs`, `ComponentTypeRegistry.cs`,
`LibraryCatalog.cs`, `MicrostripSubstrateInjection.cs` or a sibling `PlanarLineSubstrateInjection`),
`src/Design/Layout/Em/LineCalculator.cs`, `src/Design/Layout/Lvs/DeviceType.cs` (if it enumerates line kinds),
`src/Ui/Diagnostics/SymbolArtworkGenerator.cs`, `docs/design/microstrip-models.md` (a new section, or a sibling
`docs/design/planar-line-models.md`), `testdata/planar-lines/`

---

## 1. Goal

Two new circuit components, as first-class as MLIN: **`CPWG`** (conductor-backed / grounded coplanar waveguide)
and **`SLIN`** (stripline, centred or offset). Both bind to a technology's stackup exactly as MLIN does, both carry
conductor and dielectric loss, and both are validated against references produced outside circuitRF. AS-5 maps the
trace review's `grounded coplanar waveguide` and `stripline` to them; a user can also place them by hand.

## 2. Requirements

**R-as1-1 — CPWG.** Two-port, ground-referenced (the MLIN/TLIN stamp shape, `StampUniformLine`). Parameters, SI
after the elaborator's unit scale, names in MLIN's spelling: `W` (centre strip), `G` (gap to the coplanar ground,
both sides), `L`, `H` (substrate to the backing plane), `T`, `Er`, `TanD`, `Sigma`, `Roughness`. Model:
- quasi-static **conformal mapping for conductor-backed CPW** (the Ghione–Naldi form: the two partial capacitances
  K(k)/K(k′) for the coplanar part, K(k₃)/K(k₃′) with k₃ = tanh(πa/2h)/tanh(πb/2h) for the backing plane), the
  coplanar grounds taken as wide;
- a **finite-thickness correction** to W and G (the published effective-width form for CPW);
- **frequency dispersion** of εeff (a published CPW dispersion formula; cite it);
- **conductor loss** (a published CPW conductor-loss form valid for t ≪ skin-depth-limited cases and t ≫ δ, with
  `Roughness` applied as MLIN applies it) and **dielectric loss** from the filling factor.
- K(k)/K(k′) by the **arithmetic–geometric mean** (exact to machine precision), not a piecewise approximation.
- Validity: report through the same reporter shape `MicrostripValidityReporter` uses (W/H, G/H, εr, t/W ranges the
  chosen formulas state). Out of range is a warning, never a refusal.

**R-as1-2 — SLIN.** Two-port. Parameters: `W`, `L`, `H1` (dielectric to the plane above), `H2` (to the plane
below), `T`, `Er`, `TanD`, `Sigma`, `Roughness`. `H1 = H2` is the centred line. Model:
- centred line: the exact zero-thickness K(k)/K(k′) form with **a finite-thickness formula** (Wheeler's 1978 form
  or Cohn's, cite which);
- offset line: the **parallel-combination** form (each half as a centred line of spacing 2·Hᵢ + T, combined), the
  textbook approximation for an offset stripline — cite it, and state its accuracy range in the validity report;
- TEM: εeff = Er (no dispersion);
- conductor loss by the incremental-inductance rule; dielectric loss tanδ-only.

**R-as1-3 — Stackup binding.** A schematic instance on a technology gets its substrate injected at extraction,
exactly as MLIN does (`MicrostripSubstrateInjection.BuildOverrides` is the model):
- **CPWG**: from the signal layer and the nearest reference conductor below (or above, if that is the only one) —
  `H`, `T`, `Er`, `TanD`, `Sigma`. `G` is per instance; the technology has no gap.
- **SLIN**: the nearest reference conductors above **and** below the signal layer — `H1`, `H2` as the dielectric
  thickness between them, `T`, `Sigma`. When the layers between the two planes have **different** permittivities,
  `Er` (and `TanD`) is the **thickness-weighted mean**, and a warning names the layers and the spread when it
  exceeds 10 %. A layer with only one reference is a refusal on the instance naming the missing plane — never a
  silent MLIN.
- The instance's layer is chosen the way MLIN's is (its existing layer parameter and default), so the two families
  behave alike in the parameter editor.

**R-as1-4 — Symbols and registry.** New `SymbolKind`s, built-in symbols drawn in the MLIN family's style (a CPWG
symbol shows the two side grounds; a SLIN symbol shows the two planes), registry entries, palette rows in the
transmission-line group, `reference components CPWG|SLIN` entries over MCP (the `nets` field = 2), and the
parameter editor's length-unit handling (`ApplyTechnologyLengthUnit`). The Lvs `DeviceType` and any enumerations of
"line kinds" that MLIN belongs to include them where the enumeration's meaning covers them (audit every
`SymbolKind.Mlin` site and decide each; record the decisions in `RESOLVED.md`).

**R-as1-5 — Line Calculator.** `LineCalculator` today answers a coplanar line from the cross-section alone ("no
circuit component models a coplanar line"). It now **analyses and synthesises** CPWG and SLIN through their
models (as it does MLIN through `MicrostripLineModel.LineParameters`), and keeps the cross-section result beside
it, so a user sees model and field solve side by side. The sentence about no model goes.

**R-as1-6 — References (the repo's validation rule).**
- **Implementation references:** for each model, a table of (geometry → Z0, εeff, α at three frequencies) computed
  by an **independent implementation** of the cited formulas, written outside `src/` (a short script under
  `testdata/planar-lines/`, committed with its output and a provenance header), agreeing with the C# to **1e-6
  relative**. This proves the code is the formula.
- **Physics references:** for each model, at least six geometries spanning thin/wide strips, low/high εr and (SLIN)
  centred and offset, with Z0 and εeff from **published tables or worked examples** in the open literature (cite
  each) and/or an **open-source field solver** run outside the repo (its input and output committed as data, its
  name and version in the provenance header). Agreement within the **formula's stated accuracy** (typically 1–3 %
  on Z0) — the tolerance stated per row with its source. This proves the formula is the physics.
- Cross-check, not reference: the trace review's own cross-section solve (`TraceCrossSection.Solve`) on the same
  geometries, within 3 % on Z0, recorded in the test's output. It is ours, so it cannot be the reference; it is
  what AS-5's choice between the model and TLIN will lean on, so the two must agree.
- No numbers from a commercial tool, and no commercial tool named.

**R-as1-8 — The impedance readout.** The parameter editor's MLIN impedance readout
(`ParameterEditorViewModel.MlinImpedance.cs`) is extended to CPWG and SLIN: each shows its computed Z0 and εeff at
the bench's top frequency from its own model, so a swap (AS-11) shows at once what the new type makes of the same
geometry.

**R-as1-7 — Design note.** A section in the line-models design note: formulas, citations, validity ranges, loss
treatment, the stackup binding rules, and what is deliberately not modelled (finite coplanar ground width, the
lid, CPWG / SLIN discontinuities).

## 3. Not in this phase
Discontinuity models for either family; coupled CPW or stripline; PCells (L5's Update Layout from Schematic
reports "no layout view — skipped" for both, as for any part without one); ungrounded CPW (TLIN fallback, D13).

## 4. Gates (minimal tests, run only these classes)
- `CpwgModelTests` — the implementation-reference table at 1e-6; the physics-reference rows at their stated
  tolerances; a lossless instance is reciprocal and passive; `G → ∞` and large `H` tend to the right limits (the
  conductor-backed line's microstrip and CPW limits respectively, within the stated tolerance).
- `SlinModelTests` — the same two tables; `H1 = H2` and the offset form agree at zero offset; swapping `H1`/`H2`
  changes nothing.
- `PlanarLineInjectionTests` — a four-layer synthetic technology: CPWG on the top layer binds the plane below;
  SLIN on an inner layer binds both planes and warns on a 4.2 / 3.5 εr stack with the weighted value; SLIN on a
  layer with one plane is the refusal.
- `LineCalculatorPlanarTests` — a CPWG and a SLIN analyse to the model's numbers with the cross-section beside
  them; synthesis returns a W that analyses back to the target within the synthesis tolerance.
- `NetlistToSchematicTests` (existing class in `tests/Ui.Tests/Cli/`, one new case) — a `.cnl` with one CPWG and one SLIN draws and round-trips.
