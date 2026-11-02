# Brief — wBond wire temperature from a DC or large-signal run

**Tag:** `R-wbt-n` · **Written:** 2026-10-05 · **Series:** standalone (builds on the wBond σ(T) work, `02527f84`).
**Area:** `src/WBond` (new `Thermal/` folder, `Materials.cs`), `src/Core/Devices/WBondModel.cs` +
`ComponentModelFactory.CreateWBondModel`, `src/Engine/HarmonicBalance/HbEngine.cs` (single-, two- and multi-tone dataset
builders), `src/Engine/{NonlinearDcEngine,DcResultPacker}.cs`, `src/Engine/Loadpull/{LoadpullEngine,LoadpullPursuitEngine}.cs`, `src/Design/Schematic/{ComponentTypeRegistry,
WBondPlacement}.cs`, `src/Ui/ViewModels/{ParameterEditorViewModel.WBond,ParameterRowViewModel}.cs`,
`src/Ui/Views/ParameterEditor/ParameterEditorView.axaml`, `docs/user/src/reference/wbond.md`, `docs/design/wbond.md`,
`docs/design/cli.md`, `examples/Thermal Output Wires/`.
**Depends on:** nothing unlanded. **Blocks:** nothing.

## Why

A designer running a power amplifier wants to know how hot the output bond wires get at each drive level. The only way to
find out today is to run the 3D thermal model (`Thermal Output Wires` ▸ `FromHB`). That takes an EM run, Gmsh and a mesh.
This brief adds a quick check to the schematic. When a wBond instance's temperature is **solved** rather than fixed, every
DC, harmonic-balance, loadpull and loadpull-pursuit run reports each wire array's **maximum wire temperature**. The temperature
is computed from the DC and harmonic currents that run found in the wires, by the same conductive-balance method the 3D
thermal run uses, applied to each wire as a one-dimensional conductor between two fixed end temperatures. A DC run has only
the DC current, and that heats the wires too. The temperature is a cube in the run's `DataSet`, so it shows up in the Data
Display, in `.npy`/`.mat`/`.txt` exports and in every parametric sweep (a `Pin` sweep in particular, or a bias sweep for DC).

Some arrays carry no current at all: a wire behind a DC block in a DC run, or an unused array. That must be an ordinary,
exact answer (D8), never a failure, a warning or noise.

## 0. What already exists — read this first

| Piece | Where |
|---|---|
| Instance `Temp` (°C) overrides the design's `OperatingTempC`; σ(T) table, clamped at its ends with a Messages warning | `ComponentModelFactory.CreateWBondModel` ~2602; `WireMaterial.SigmaAt`, `ConductivityClampNotes` |
| Default wire temperature 125 °C everywhere | `WireMaterials.DefaultOperatingTempC`; `ComponentTypeRegistry.WBondDefaultTempC` |
| Wire metals from `generic-materials.cmat`, or a workspace `.cmat` named by `MaterialLibrary`. **The `.cmat` records already carry `ThermalK` and `ThermalKVsTemp`**, but `WireMaterial` does not read them yet | `src/WBond/Materials.cs` ~32, ~200; `src/Design/resources/technologies/generic-materials.cmat` |
| One branch-current unknown per array; `ArrayBranchIndices` set at each `Stamp` | `WBondModel` |
| `Z_arr(f)` at the stamp temperature; the inductance-only reduction and its per-array current shares | `WBondModel.ArrayImpedance`, `.InductanceOnly()`, `ArrayReduction.CurrentShares` |
| How an array's RF current divides among its wires (inductive and frequency-independent, including the circulating current in the other arrays) | `src/WBond/ArrayShare.cs` |
| R′_ac(f, σ) by the exact Bessel solution, with its σ-slope | `InternalImpedance.ResistanceWithSigmaSlope` |
| Single-tone HB: lazy back-solver giving **every linear branch current**, and the precedent of reading a linear model's branch rows into the `I` cube | `HbLinearBackSolver`; `HbEngine` ~720 (`HbPinCurrents` for `SnpModel`) |
| Loadpull's per-point `SinglePointResult` carries that same back-solver | `HbEngine.SinglePointResult.BackSolver` |
| Two-/multi-tone HB: full node spectra for every user-facing node (`Vfull`) | `HbEngine` ~1064, ~1348 |
| DC: `DcResult` exposes only IProbe branch currents (`ExtractProbeCurrents` reads them out of the solution vector `x`). `DcResultPacker.Pack` is the **single** packer that the standalone run, the swept run (`ParametricSweepEngine.RunDc`), the CLI `dc` verb and `RailDcRun` all call | `NonlinearDcEngine` ~64–135, ~810; `DcResultPacker` |
| Parametric sweeps stack each point's `DataSet` (so a per-point cube gains the sweep axis for free) | `ParametricSweepEngine` |
| **Conductive balance**: Newton on temperature and potential, step halving, stall rule, "not physical" states (σ ≤ 0, above a table's top), continuation that brackets runaway | `src/Thermal/Nonlinear/{ConductiveBalance,Continuation}.cs`; `ElectricalConductivity.Beyond` |
| A wire's RF heat q′ = Σ ½\|Iₙ\|²R′_ac(fₙ, σ(T)) (peak phasors) | `ThermalWire.RfHeat` |
| The Inspector's wBond checkbox pattern (`IncludeCapacitance`) and the generic expression rows `Temp` stays on | `ParameterEditorViewModel.WBond.cs` ~60–180; `ParameterEditorView.axaml` ~421 |
| A row's greyed state (`ExpressionEnabled`), currently only "fixed for the life of the component" | `ParameterRowViewModel` ~116–140 |
| Six 1 mil gold wires, ready-made | `examples/Thermal Output Wires/Pads/layout/Pads.wBond` |

**Why the 3D solver is not reused directly:** `ElectrothermalProblem` requires a `ThermalProblem` (a tetrahedral mesh) and
ties wire ends to pad contact patches. A schematic wBond has no mesh. Its wires are solved as 1D chains between two stated
temperatures, using the same *method* (R-wbt-3) and checked against the 3D code (G3). `src/Engine` does not reference
`src/Thermal`, and this brief does not add that reference.

## Decisions (owner may overturn any; each has a recommendation already applied below)

- **D1 — One-way, not self-consistent.** The circuit is solved first. The wire temperatures are computed from its converged
  currents, and nothing goes back into the circuit. This is the rule the 3D `FromHB` path already follows, and it is what
  keeps the feature quick. **In solved mode the stamp uses the HIGHER of `TempStart` and `TempEnd`** (owner, 2026-10-05,
  for S-parameters). It is the conservative choice: a wire is never cooler than its hotter end, so its loss is never
  understated. **The same rule applies to every analysis**, HB, loadpull and DC included, not only S-parameters. The
  factory builds one model whatever the analysis, and one rule keeps an HB run's small-signal limit in agreement with the
  S-parameters of the same schematic. It is stated on the `Temp` row's description and in the user doc. A
  self-consistent loop (re-stamp at the solved temperature and re-run HB until it stops moving) is a possible follow-up
  brief and is **not** part of this one.
- **D2 — The thermal model is conduction along the wire only.** Each wire is held at `TempStart` at its input end and
  `TempEnd` at its output end. No heat leaves through its sides (no mould, no air, no radiation). That makes the result an
  **upper bound**: a real wire in a mould compound runs cooler. The user doc says so and points to the 3D thermal run for
  the mould. With uniform heating along the wire and adiabatic sides, a wire's shape does not matter: only its **3D arc
  length**, diameter and metal do. Use the same length the wBond resistance is computed from (summed over the wire's
  `WireMesh` filaments), so the thermal and electrical models describe the same wire.
- **D3 — Time-averaged heat.** A wire's thermal time constant (ms) is many orders longer than an RF period, so the heat is
  the time average: q′ = I_dc²·R′_dc(σ(T)) + Σ_f ½|I_f|²·R′_ac(f, σ(T)) over every non-DC frequency the run carries. That
  means harmonics for single-tone, and every mixing product for two- and multi-tone. Peak phasors, matching HB's
  full-amplitude convention. CW only: no pulsed duty.
- **D4 — Parameters.** `FixedTemp` (boolean; the Inspector shows it as the checkbox on the `Temp` row), `TempStart` (°C,
  default **125**), `TempEnd` (°C, default **85**). "Start" is the array's **input** pin end, "End" its **output** pin end,
  in the pin order the symbol already has. `Temp`, `TempStart` and `TempEnd` stay **expression** rows, so `TempStart = tdie`
  can be typed and swept. **A newly placed wBond is in solved mode** (`FixedTemp=false`). **An instance with no `FixedTemp`
  (every schematic saved before this) is in fixed mode**, so its answer does not change (G7).
- **D5 — Output.** One real cube, **`WireTemp`**, unit `°C`, on an axis `wire array` whose labels are
  `<instance path>:<array name>`. That is the `{path}:{n}` spelling `HbPinCurrents` already uses in the `I` cube. The value
  is the maximum over the array's wires of each wire's maximum along its length. **A fixed-mode instance reports its `Temp`
  for each of its arrays**, so the user can see the temperature was fixed. A metadata cube `__WireTempState` on the same
  axis holds 0 = fixed, 1 = solved, 2 = no steady state, 3 = the circuit itself did not converge at this point. For states
  2 and 3, `WireTemp` is **NaN**. State 3 adds no warning of its own, because the circuit's non-convergence is already
  reported. For state 2 and the run warns once per
  instance and array, naming the drive point and the last converged one (R-wbt-4). Both cubes are present whenever the
  netlist holds a wBond, and absent otherwise, so every other run's dataset is byte-identical.
- **D6 — Where the code lives.** The 1D solver and the per-wire current split live in `src/WBond/Thermal/`, which already
  holds the wire physics: shares, Bessel R′, σ(T). No new project reference. The engine calls it after convergence, which
  is not the Newton hot path.
- **D7 — Scope.** DC (standalone, swept, and the CLI `dc` verb), HB (single-, two- and multi-tone), loadpull, loadpull
  pursuit, and any `parametric_sweep` that wraps them (owner, 2026-10-05, added DC). **Not S-parameters.** They stamp at
  max(`TempStart`, `TempEnd`) in solved mode, per D1, and report no wire temperature: a small-signal run has no current
  amplitude to heat anything.
- **D8 — No current is an ordinary answer.** An array with no current gets the exact conduction profile, linear from
  `TempStart` to `TempEnd`, so its `WireTemp` is max(`TempStart`, `TempEnd`) with state 1 (solved), no warning and no NaN.
  Examples: a DC run with a DC block in series, an array the circuit leaves open, or a zero-drive sweep point. The same
  holds for a current that is not exactly zero but is round-off (1e-15 A through a DC block, say). The answer must
  be continuous as the current goes to zero, so there is **no threshold** that switches between two code paths with
  different answers (R-wbt-3f).

## 1. `R-wbt-1` — material: thermal conductivity on `WireMaterial`

- **a.** `WireMaterial` gains `ThermalK` (W/(m·K), nullable) and `ThermalKVsTemp` (`IReadOnlyList<KPoint>?`), read from the
  same `.cmat` records and keys the 3D thermal run reads (`ThermalK`, `ThermalKVsTemp` rows of `TempC`/`Value`). This
  applies to both the shipped library and a workspace `.cmat` named by `MaterialLibrary`. Interpolation and end behaviour
  match the σ table's: linear inside the table, held at the bottom row below it. **Above the top row, the state is not
  physical**, which is the meaning `ElectricalConductivity.Beyond` gives the σ table (gold's ends at 1,027 °C).
- **b.** A `.wBond` file's own embedded materials keep round-tripping. A material stated with no `ThermalK` is legal
  everywhere **except** in solved mode, where it is a refusal: *"wBond 'WB1' solves its wire temperature, and its wire
  material 'X' states no thermal conductivity. State ThermalK for 'X' in the Materials editor, or check Temp to fix the
  temperature."*
- **c.** The `New Material…` dialog's created record gains a `ThermalK` field. Default: the shipped metal it was copied
  from, or blank.

## 2. `R-wbt-2` — the component: parameters, the stamp temperature, back-compatibility

- **a.** `ComponentTypeRegistry` (wBond list): add `FixedTemp` (declared default `"false"`), `TempStart`
  (`WBondDefaultTempC`, which is 125) and `TempEnd` (`"85"`, a named constant beside `WBondDefaultTempC`). Add °C units in
  the `Temp`/`Tnom`/`Dtemp` unit rule. Add descriptions in `WBondParameterDescription`. **Rewrite `Temp`'s description** so
  it covers both modes and D1's stamp at the higher boundary temperature.
- **b.** `WBondPlacement` writes `FixedTemp=false`, `TempStart=125` and `TempEnd=85` on a new placement. **A `.csch`
  loaded from disk is not back-filled**: prove that no load, netlist or elaboration path merges registry defaults into a
  stored instance (G7). If one does, stop and report it rather than work around it.
- **c.** `CreateWBondModel`: parse `FixedTemp` with `BooleanParameter.Parse(..., whenAbsent: true)`. In solved mode set
  `design.OperatingTempC = Math.Max(TempStart, TempEnd)` **before** `ConductivityClampNotes()` (so a clamp warning names the
  temperature actually stamped). Hand `WBondModel` a new
  `WireThermalSpec(bool Solved, double FixedC, double StartC, double EndC)`.
- **d.** `WBondModel` exposes that spec, plus what the solver needs per wire: array index, arc length, diameter and metal.
  **Build that list once per model**, not per sweep point.

## 3. `R-wbt-3` — the solver: one-dimensional conductive balance

New `src/WBond/Thermal/WireConductiveBalance.cs`. Input: one array's wires (length, diameter, `WireMaterial`), the two end
temperatures, the array's DC current, and each wire's harmonic currents (peak magnitudes per frequency, already split, see
R-wbt-4). Output: each wire's temperature profile, its maximum, a converged/runaway state, Newton steps, and a note.

- **a. Discretisation.** Second-order line elements along the arc length, the same element `ElectrothermalSystem` uses for a
  wire, enough that halving the element size changes the maximum by < 0.01 K (G1 measures it; pick the count from that
  measurement, not a guess). Dirichlet at both ends.
- **b. DC sharing is part of the solve.** An array's wires are electrically in parallel between the same two nodes, so they
  carry a common voltage `V`, and the circuit's array current `I_dc` splits as `I_i = V / R_i(T_i)` with
  `R_i = ∫ ds / (σ(T)·A)`. Unknowns: every interior temperature, plus `V`. Recommended: Newton on each wire's tridiagonal
  system with `V` as a bordering unknown (block elimination, O(n) per wire). A dense solve over the whole array is not
  acceptable. Arrays do not share DC current with each other (the DC reduction is diagonal by array), so each array is
  solved on its own.
- **c. Harmonic heat** uses each wire's fixed harmonic currents and `R′_ac(f, σ(T))` from
  `InternalImpedance.ResistanceWithSigmaSlope`, with its σ-slope in the Jacobian, exactly as `ThermalWire.RfHeat` does.
- **d. The method is `ConductiveBalance`'s**, and the code says so in its header. That means: a full Jacobian (k(T) and
  σ(T) slopes); step halving up to ten times while the scaled residual does not fall or the state is not physical (σ ≤ 0,
  or a temperature above either table's top row, or above `ConductiveBalance.Absurd`); the stall rule (three damped steps
  that do not halve the residual end the solve as not converged); and the same convergence test (temperature update below
  tolerance × span, floored at `ThermalSolver.SpanFloorK`, and the residuals down by 1e-8 of their reference or to their
  round-off floor). Copy the constants by reference, not by value, so the two cannot drift.
- **e. Continuation and runaway** follow `Continuation.cs`. Start from the previous sweep point's state when there is one.
  Otherwise, cold-start from the **linear profile between the two ends**, which is the exact zero-current solution, never
  from a uniform temperature.
  If a point fails, bisect a drive scale λ ∈ (λ_last converged, 1] that multiplies every current. If λ = 1 is never
  reached, the point has **no steady state**: report state 2 and the largest λ that converged, as drive context for the
  warning. Never report a temperature from an unconverged state.

- **f. Zero and near-zero current (D8).** With the cold start of 3e, an array with no current has a starting residual of
  zero. That is `ConductiveBalance`'s "already at the answer" case (`atStart`): converged in 0 steps. Port that guard
  and its span floor exactly. Without them, three things go wrong:
  - With `TempStart == TempEnd` the span is 0, and "update ≤ tol × span" can never hold.
  - A round-off current takes a step "down" from round-off, which is noise.
  - The bordering unknown `V` is 0 with `I_dc` = 0. Its Jacobian entry Σ 1/R_i is positive, so it is not singular, but
    prove that in G13 rather than assume it.

  The heat is I², so a negative DC current (flow from output to input) heats exactly as a positive one does. A harmonic
  with zero current asks for no R′_ac, as `ThermalWire.RfHeat` already skips it, because its frequency may be unset.
  Arrays of one instance are independent (3b), so a dead array never affects its live neighbour. In HB, a DC-blocked
  array still carries circulating harmonic current through the shares (R-wbt-4b), so "no DC" is not the same as
  "no heat".

## 4. `R-wbt-4` — the engine: currents in, `WireTemp` out

New `src/WBond/Thermal/WBondWireTemperature.cs` (the orchestration, so it can be tested with no engine), called from
`src/Engine` after convergence of each point.

- **a0. DC.** `DcResult` gains `WBondArrayCurrents` (instance path → one real current per array), read out of `x` at
  `ArrayBranchIndices[k]` in the same place, and the same way, that `ExtractProbeCurrents` reads an IProbe's row. Fill it on
  both of `NonlinearDcEngine`'s return paths (~935 and ~968). The thermal solve gets the DC current only: no harmonics.
  A swept DC run warm-starts the thermal solve from the previous point, as HB does.
- **a. Array current spectra.** For single-tone HB and for each loadpull or pursuit point, read the branch rows
  `ArrayBranchIndices[k]` from the back-solver at every harmonic, exactly as the `HbPinCurrents` block reads an `SnpModel`'s
  rows. For two- and multi-tone HB, which have no back-solver, use `I = Z_arr(f)⁻¹ · (V_in − V_out)` at each mixing
  frequency from `Vfull` (the stamp's own equation, at the stamp temperature). Factor that once per frequency per instance.
  With capacitance on, the branch current is the series current between the end shunts: the wires' current. Say this in
  the code comment.
- **b. Per-wire harmonic split.** Wire `i` at frequency `f` carries `Σ_k share[k][i] · I_k(f)` as a **complex** sum over
  every array `k` of the instance (share from `InductanceOnly().CurrentShares`, the same numbers `ArrayShare.For` gives;
  G4 compares them). This differs from the 3D run, which adds magnitudes because it has no phases. Here the phases are
  known, so the sum is exact, and the code comment says why the two paths differ.
- **c.** Fixed-mode instances skip the solve and report `Temp`. Solved-mode instances run R-wbt-3 per array.
- **d. Datasets.** Add `WireTemp` and `__WireTempState` (D5) in **`DcResultPacker.Pack`**. It is the single packer, so
  the standalone, swept and CLI DC runs get identical cubes. `RailDcRun` has no wBond and gets neither cube. Then add them
  in the single-tone, two-tone and multi-tone builders of
  `HbEngine`, and as `[grid, pin, wire array]` cubes in `LoadpullEngine.BuildLoadpullDataSet`. In the pursuit dataset, use
  the layout its `Pout` already has, plus the array axis. `ParametricSweepEngine` stacks them unchanged. Confirm that its
  ragged-grid padding treats `WireTemp` as data and `__WireTempState` as metadata.
- **e. Warnings** go through the existing `AddWarning` channel to the Messages panel. Each instance and array warns once
  per run, not once per sweep point. Example: *"wBond 'WB1' array 'out': no steady wire temperature above Pin = 27.5 dBm
  (thermal runaway); the last converged point, Pin = 27.0 dBm, reached 912 °C. WireTemp is NaN past it."* The same
  sentence names the swept variable for a DC sweep, or says "at the operating point" for a single DC run.
- **f. Cost.** Measure the added time per point on a 6-wire, 2-array design with a scratch harness (not a Benchmark
  test). Report it in `src/WBond/RESOLVED.md`. Expected: well under a millisecond, against an HB point's tens of ms.

## 5. `R-wbt-5` — the Inspector

- **a.** The `Temp` row gains a leading **checkbox** bound to `FixedTemp`. The label stays "Temp", so checking Temp fixes
  the temperature, which is what the label suggests. `TempStart` and `TempEnd` rows sit directly under it.
- **b.** **Checked:** the `Temp` box is enabled and the two boundary boxes are **disabled** (greyed). **Unchecked:** the
  reverse. This needs a third reason for `ExpressionEnabled` to be false, a **mode-dependent** one. Add it as its own flag
  (`SetDisabledByMode`), kept apart from `_structurallyFixed` and `_readOnlyRequested`, so a refresh cannot clear it and it
  cannot clear them. Update the doc comment there: it currently says greyed means "fixed for the life of the component",
  which stops being the only case.
- **c.** All three stay expression rows. A `VAR` reference in a disabled box is kept and committed unchanged. Toggling the
  checkbox never rewrites the values.
- **d.** `FixedTemp` joins `IsWBondPanelParameter` (it has a real control). `Temp`, `TempStart` and `TempEnd` do **not**
  (see the existing comment on why `Temp` must stay a generic row).
- **e.** Toggling is one undoable `SetParametersCommand`, like `IncludeCapacitance`.

## 6. `R-wbt-6` — documentation

- **a. `docs/user/src/reference/wbond.md`:** In the Parameters table, update `Temp` (the checkbox, both modes, the stamp at
  the higher of `TempStart` and `TempEnd` in solved mode, in every analysis) and add `TempStart` and `TempEnd`. Add a section **"Wire temperature from a DC or large-signal run"
  `{#wire-temperature}`** that states:
  - the two modes;
  - that **wire temperature is an output** of every DC, harmonic-balance, loadpull and loadpull-pursuit run of a design with a
    wBond, viewable in the Data Display as `WireTemp` (one trace per array; plot it against the swept `Pin`);
  - that a fixed-temperature wBond still reports its `Temp`, as a reminder that it was fixed;
  - that only each array's hottest wire is reported, at its hottest point;
  - what heats the wires: DC plus every harmonic or mixing product, each at its own skin-effect resistance, with σ(T) and
    k(T) following the temperature;
  - why the defaults are 125 °C (die end) and 85 °C (package-lead end): typical of a PA's output wire, and different from
    each other to show that the two ends are independent;
  - **what it does not model**: no heat leaves through the sides, so it is an upper bound; the 3D thermal run is where a
    mould is modelled (link `thermal.html#electrothermal`); and the circuit is not re-solved at the solved temperature (D1).
    Its resistance is taken at the hotter of the two ends, in S-parameters and every other analysis;
  - that a wire carrying no current (behind a DC block, say) reads the hotter of its two end temperatures, because with no
    heat its temperature runs straight from one end to the other;
  - that runaway is an answer: NaN plus a Messages warning.
  Keywords: `wire temperature, bond wire temperature, WireTemp, TempStart, TempEnd, FixedTemp`.
- **b. `docs/user/src/reference/thermal.md` `#electrothermal`:** one sentence pointing to the quick check above.
- **c.** `docs/user/src/reference/components.md`, if it lists wBond parameters: the same three rows.
- **d. `docs/design/wbond.md`:** a new §5.6 *Wire temperature* covering D1–D7, the formulation, and why the share sums
  phasors where the 3D run sums magnitudes. Add D1–D7 to §14 as decided.
- **e. `docs/design/cli.md`:** on the `dc`/`hb`/`lp`/`lpp` rows, note that `-o` carries `WireTemp` when the netlist has a wBond.
- **f.** Doc sources only. **No DocGen run** (the owner regenerates at the end of a series).

## 7. `R-wbt-7` — example

Add a test bench to `examples/Thermal Output Wires/`: **`Amplifier Wires`** (schematic). It is the `Amplifier` bench's
drive (same FET, bias, choke, load and `Pin` sweep 10 → 28 dBm), with a **wBond component of `Pads.wBond`** in place of
the `SnP` block, in solved mode at the defaults 125 °C and 85 °C. It needs no EM run and no Gmsh. Quote its numbers in the
README (time to run, `WireTemp` at three drive levels) next to `FromHB`'s, with one sentence on why the 1D estimate runs
hotter: no mould. Add its numbers to `expected-numbers.json`, gated like the others. Register nothing new in
`examples.json` unless the example's cell list needs it.

## 8. Gates

New tests go in `tests/WBond.Tests` unless stated otherwise. Write one test per claim, and run only those classes (plus the
existing wBond, loadpull and HB classes the change reaches), never the full suite.

- **G1 — analytic, constant σ and k.** One wire, DC plus one harmonic, with ends at 125 and 85 °C. The profile is
  `T = Ta + (Tb−Ta)s/L + q′s(L−s)/(2kA)`, with q′ from D3. The maximum must match within 0.01 K. Also record the
  element-count convergence that chose R-wbt-3a's count.
- **G2 — analytic runaway.** Linear resistivity (`SigmaVsTemp` null, so σ = σ₂₀/(1+α₂₀(T−20))) and constant k. The profile
  is `u = 1 + α(T−20)`, `u″ + β²u = 0` with `β² = I²α/(σ₂₀kA²)`, and steady states end at **βL = π**:
  `I_c = (π/L)·A·√(σ₂₀k/α)`. Converges at 0.99·I_c and matches the closed-form maximum. Reports state 2 at 1.01·I_c.
- **G3 — cross-check against `src/Thermal`.** One gold wire (tables on), DC plus a harmonic, with both ends fixed. Compare
  against `ConductiveBalance` on the smallest problem `tests/Thermal.Tests` already builds that has a wire between two
  fixed-temperature contacts and no side coupling: within 0.1 K. **If no such mesh-free or tiny fixture exists, skip this
  gate and say so in the report.** Do not build Gmsh into a routine test.
- **G4 — sharing.** (i) Two wires of different length in one array, constant σ: the DC split is `R₂/(R₁+R₂)`. (ii) Two
  arrays: the per-wire harmonic currents equal the `ArrayShare.For(design)` columns combined by complex phasor sums, to
  1e-12.
- **G5 — engine, single-tone** (`Engine.Tests`): a small netlist (tone source → wBond → load) where HB's `WireTemp` equals
  `WBondWireTemperature` called directly on the `I`-cube-equivalent branch currents. A fixed-mode instance reports `Temp`
  exactly with `__WireTempState` = 0. A netlist with no wBond has neither cube.
- **G6 — engine, sweep and multi-tone:** a `Pin` sweep gives `WireTemp` the sweep axis, non-decreasing in `Pin`. The `.npy`
  export of that dataset holds `WireTemp` (read it back). A two-tone run's `WireTemp` uses the mixing products, against
  a hand computation of q′.
- **G6b — DC** (`Engine.Tests`): a DC current source through a solved wBond gives the analytic `WireTemp` of G1 with no
  harmonic term. `DcResultPacker` gives identical cubes for a standalone run and a one-point sweep. A bias sweep gives
  `WireTemp` the sweep axis. The CLI `dc --json` carries the cube.
- **G7 — back-compatibility, bit for bit:** an instance with no `FixedTemp` stamps exactly what it stamped before (an
  S-parameter comparison on an existing wBond test design, with no tolerance). A `.csch` saved before this loads with no
  `FixedTemp` written (R-wbt-2b).
- **G7b — the stamp temperature in solved mode:** with `TempStart=125` and `TempEnd=85`, the S-parameters are bit-identical
  to fixed mode at `Temp=125`. With the two swapped, they are still identical (the rule is max, not "start").
- **G13 — no current (D8), one test per claim:**
  - Exactly zero current: `WireTemp` = max(ends) exactly, state 1, 0 Newton steps, no warning.
  - Zero current with `TempStart == TempEnd`: the same, at that temperature.
  - A 1e-15 A current: within 1e-9 K of the zero-current answer, with no warning.
  - Current → 0 is continuous: 1e-6 A against the G1 closed form.
  - A negative DC current equals the positive one bit for bit.
  - A two-array instance with one array DC-blocked in a DC run (a series capacitor in a netlist): the blocked array reads
    max(ends) and the other is unaffected.
  - The same netlist in HB: the blocked array's `WireTemp` comes from its circulating harmonic current alone.
- **G8 — loadpull and pursuit:** `WireTemp` is `[grid, pin, wire array]` and finite at converged points.
- **G9 — refusal:** solved mode with a metal that has no `ThermalK` refuses with R-wbt-1b's sentence. The same instance in
  fixed mode runs.
- **G10 — Inspector** (`Ui.Tests`, view-model level): a new placement writes the three parameters; toggling greys exactly
  the right rows; a `VAR` in a disabled box survives two toggles unchanged; one undo restores the previous state.
- **G11 — example:** `Amplifier Wires`' expected numbers. If it costs over ~5 s, tag it `Benchmark`, like the other
  example gates.

## 9. Done means

- Every gate above passes. Report which test classes were run and their counts.
- `src/WBond/RESOLVED.md` gains a section with the measured per-point cost, the chosen element count and anything that
  surprised (never `CLAUDE.md`).
- The vendor-name grep is clean. Nothing is committed unless the owner asks.
