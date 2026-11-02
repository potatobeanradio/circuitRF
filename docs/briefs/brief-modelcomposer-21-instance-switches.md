# Brief MC-21 — Instance switches: turning pieces of a composed model off

**Series:** `brief-modelcomposer-0-overview.md` (D3, D7, D8, D10) · **Tag:** `R-mc21-<m>` · **Depends on:** MC-9, MC-10, MC-16
**Area:** `src/Core/Devices/Composed/` (`ComposedCoreModel`, `ComposedCoreProgram`, a new `ComposedSwitches`),
`src/Core/Elaboration/Elaborator.cs` (`ComposedFetCell`, `ComposedParams`), the bias-point freeze in `src/Engine`,
`src/Design/Schematic/ComponentTypeRegistry.cs` + `NetExtractor.cs`, `src/Ui/ViewModels/ParameterEditorViewModel.cs`;
`docs/user/src/reference/model-composer.md`, `components.md`, `netlist.md`; `docs/design/model-composer.md`
**Read first:** `ComposedCoreModel`'s class remarks (ports, state nodes, thermal port); `BooleanParameter`;
`Elaborator.ComposedFetCell`; MC-10 R-mc10-1 (trap states) and MC-19 (`Shift` coupling)

---

## 1. Goal

A designer places a `.cmodel` as a `ComposedFet` and simulates it — that works today (palette entry, `File`/`N`/`m`/
`Symbol`, DC/S/HB/`lp`/`lpp`). What they cannot do is **switch one impairment off on one instance** and see what it
does to their circuit: the intrinsic charge, its nonlinearity, gate lag, drain lag, self-heating, DC/RF dispersion.
That is how a designer learns which effect is costing them gain, efficiency or linearity, and how they debug a
circuit that does not behave. Each switch is an ordinary instance parameter, so it can be toggled in the parameter
editor, bound to a variable and swept, and set from `--set` headlessly. **No switch refits anything.** The `.cmodel` is
untouched; the switch changes how this one instance evaluates it.

Per-branch charge linearisation (Linear Cgs / Cdg / Cds) is MC-22. It builds on the bias-point freeze this brief adds.

## 2. Requirements

**R-mc21-1 — The switches.** Six new `ComposedFet` parameters, each read by `BooleanParameter` (so `true/false`,
`on/off`, `yes/no` and `1/0` all work, an expression that evaluates to one of them works, and anything else is refused by
name with the list):

| Parameter | Default | Off / on means |
|---|---|---|
| `Charge` | `true` | `false`: no intrinsic charge at all — `Qg = Qd = 0`, so the core contributes no capacitance. The extrinsic network's capacitors stay. |
| `LinearC` | `false` | `true`: both intrinsic charges are **linearised at the bias point** (R-mc21-3): `q(v) = q(v_Q) + C(v_Q)·(v − v_Q)`, a constant 2×2 capacitance matrix, transcapacitance included. |
| `GateLag` | `true` | `false`: every gate-lag trap state is removed (R-mc21-4). |
| `DrainLag` | `true` | `false`: every drain-lag trap state is removed. |
| `SelfHeating` | `true` | `false`: the core is evaluated at the run's ambient temperature (clamped to the model's range as today) and injects no power into the thermal node. |
| `Dispersion` | `true` | `false`: the drain carries `IdDC` at every frequency and the dispersion node `x` is not minted. |

`Charge=false` wins over `LinearC`. The defaults are the model as fitted, so a netlist that states none of them is
byte-identical to today's and evaluates identically. Add the six names to `ComposedParams` and to the refusal sentence
(`it takes File, N, m, Charge, LinearC, GateLag, DrainLag, SelfHeating and Dispersion`).

**One type owns the parsing and printing**, `ComposedSwitches` in `src/Core/Devices/Composed/` (an immutable record:
`Charge`, `LinearC`, `GateLag`, `DrainLag`, `SelfHeating`, `Dispersion`, plus MC-22's branch flags later; `Default`;
`IsDefault`; `Describe()` → the short summary of R-mc21-7; `Read(overrides, scope)`; `Write()` → the non-default
`key=value` pairs in a fixed order). The elaborator, the schematic extraction, harmonicaRF (MC-23) and `explain` all
call it. The elaborator's `_composedCells` cache key gains the switches. The core instance inside the synthesised cell
carries them, so each core instance is built with them.

**R-mc21-2 — A switch for a part the model does not have is a note, not a refusal.** `GateLag=false` on a model with
no gate-lag state, `SelfHeating=false` on a model with no `Thermal` block, `Dispersion=false` on a model with no
`IdRF`: a note once per run per instance — `X1: 'gan.cmodel' has no gate-lag trap state; GateLag=false changes
nothing.` A designer can then keep one schematic and swap models in it.

**R-mc21-3 — The bias-point freeze (`LinearC`, and MC-22).** "The bias point" is the instance's **quiescent DC
operating point**: the intrinsic `vgs`, `vds` of the circuit's own DC solution with every RF source at zero, the
junction temperature that DC solution carries, and the trap states at their DC values. Intrinsic charge never affects a
DC solution, so finding it is never circular.

- Add a single freeze step in `src/Engine`, for example `BiasFreeze.Ensure(netlist, settings)`. It runs
  `NonlinearDcEngine` once, hands each component that asks for one (a small interface on `ComponentModel`, e.g.
  `IBiasFrozen`) its DC port voltages, and records the DC solution it froze at. If the DC solution is unchanged on a
  later call, it does nothing. `FreezeCount` is a counter for the gates.
- Every engine that reads charge calls it before its first evaluation: S-parameters, HB (single-tone, two-tone, drive
  ramp, **warm-started** solves included), loadpull and pursuit, the parametric-sweep engine at each point, the
  optimizer/yield/DOE engines through those, the Model Composer's validation bench, and harmonicaRF (MC-23). **A test
  scans `src/Engine` and `src/Harmonica` for those engines' entry points** and fails if one does not freeze. Without
  that scan, a missed caller is exactly the silent wrong answer this brief must not produce.
- **An unfrozen linear-mode core refuses to return a charge** (`ComposedEvaluationException` naming the instance and
  saying the bias point was never set). The DC engines read no charge and are exempt, by an explicit flag rather than a
  try/catch.
- The frozen point is set before any parallel evaluation and is never written during a solve. A netlist whose bias
  sources change in place (harmonicaRF's `VdcOverride`, the Idq search) must be re-frozen. The DC-solution comparison
  above is what notices the change, and MC-23's gate holds it.
- With `LinearC=true`, the charge's temperature and trap-state inputs are held at the frozen values too, so the
  capacitance really is constant. The currents keep theirs.

**R-mc21-4 — What "no lag" means.** A removed trap state no longer lags. It takes its DC value at the instantaneous
voltages, `phi_k := TrapDynamics.DcValue(k, vgs, vds, T)`, so that trap no longer distinguishes RF from DC. For
`CaptureEmission` this is exactly `phi1 = vgs`, `phi2 = vds`. Under a `Trained` coupling the current becomes the DC I–V
surface in that state. Under `Shift` (MC-19) that state's shift term is zero. The state's node is **not minted**
(`InternalStateNodeCount` drops), which also takes its unknowns out of HB. Gate-lag vs drain-lag is
`TrapDynamics.CaptureSign`: −1 is gate-lag, +1 drain-lag, so `Custom` models follow the convention MC-10 already
documents. The chain rule for the removed lane goes through `DcValue`'s gradient overload. When a model does not supply
it, use a central difference of `DcValue` and say so in the design note.

**R-mc21-5 — The thermal pin under `SelfHeating=false`.** If a fourth net is bound, it stays connected to the
synthesised `Rth ∥ Cth` and the core simply puts no power into it. Nothing is refused, so a schematic that wires pin 4
keeps working with the switch off.

**R-mc21-6 — Schematic and netlist.** `ComponentTypeRegistry` gains the six rows for `SymbolKind.ComposedFet`:
`ShowOnSchematic` false, each with a one-line description in `ComposedFetParameterDescription`. The parameter editor
shows them as checkboxes, the way it already shows other boolean rows (find how `OpVars`/`IncludeCapacitance` are
presented and match that), in a collapsible **Model switches** group under `File`/`N`. `NetExtractor` writes a switch
only when it is not the default, so every existing `.csch` extracts byte-identically. A switch may hold an expression
(`GateLag=gl`), which the editor shows as text rather than a checkbox.

**R-mc21-7 — A switched-off instance is always visible.** An impairment someone switched off and then forgot about is
exactly the debugging trap this feature exists to remove, so:
- the schematic draws a one-line annotation under the instance whenever its switches are not the default, using
  `ComposedSwitches.Describe()` — e.g. `linear C · no gate lag · isothermal`. Nothing is drawn for the default;
- every run that elaborates a non-default instance puts one note per instance in Messages: `X1: linear C at the bias
  point, no gate lag.`;
- `explain` on a design reports each composed instance's switches (and, for a linear mode, says the charge is frozen
  at the quiescent DC point); `check` validates the spellings.

**R-mc21-8 — Docs** (sources only; the owner runs DocGen). In `model-composer.md`, add a new section **Switching parts
of the model off** `{#switches}`. Give the table above in user terms, what "the bias point" is, what "no gate lag"
does to the I–V, that nothing refits, how to sweep a switch through a variable to compare two runs on one plot, and the
schematic annotation. Link it from the ComposedFet entry in `components.md` and from the Export section. `netlist.md`
lists the keys. The design note records the freeze step, the unfrozen refusal and the lag rule.
Owner rules: no change history in the pages; no vendor names; no "the design's own"; minimal on-screen text.

## 3. Not in this phase
Per-branch linearisation (MC-22). harmonicaRF (MC-23, MC-24). A third trap setting — a state held at its quiescent value
(no capture under drive), as distinct from removed — is **an open question for the owner** (§5), not built.

## 4. Gates
Use `tests/Ui.Tests/ModelComposer/Support/SyntheticGan` as the truth throughout.
- `ComposedSwitchesTests`: parse/print round trip, every `BooleanParameter` spelling, an unknown word refused naming the
  switch, `Write()` empty for the default.
- `LinearChargeModeTests`:
  - S-parameters at the bias point with `LinearC=true` equal `LinearC=false` to `|ΔS|` ≤ 1e-12 (same C matrix by
    construction).
  - At five random voltages the linear core's `q` equals `q(v_Q) + C(v_Q)·Δv` to 1e-12 relative.
  - An HB power sweep differs from the nonlinear one at compression but not at −30 dBm (≤ 0.001 dB). The
    compression-point difference is printed to the test output, not asserted.
  - `Charge=false` S-parameters equal those of the same currents with zero charges (MC-18's typed zeros) to 1e-12.
- `BiasFreezeTests`: an unfrozen linear core refuses by name. A two-point parametric sweep over `Vgs` freezes twice
  (`FreezeCount`). A warm-started HB solve still freezes. The engine-entry-point scan passes.
- `TrapSwitchTests`: with `GateLag=false` and `DrainLag=false` the trap nodes are gone (`InternalStateNodeCount` 0) and
  DC I–V is unchanged to 1e-12. One HB point matches the truth core evaluated with its trap inputs fed the instantaneous
  voltages to 1e-9. `GateLag=false` alone removes `phi1` and keeps `phi2`.
- `SelfHeatingSwitchTests`: DC `Id` at a hot bias with `SelfHeating=false` equals the truth's isothermal `Id` at ambient
  to 1e-9, and a bound pin 4 sits at 0 K rise.
- One netlist holding two instances of one `.cmodel` with different switches honours both (the cell cache key).
- A parametric sweep over a variable bound to `GateLag` produces two different results.
- The schematic extracts byte-identically with defaults. A non-default instance's annotation is drawn: a render
  through `CircuitRF.Render` contains the `Describe()` text.
- Run only these classes, the existing `tests/*/ModelComposer` and `Composed` classes, `ModelComposerDocumentedPageTests`
  and `Firewall.Tests`.

## 5. Open for the owner
- **"No lag" = removed (state follows the voltage) — chosen here.** The other reading is *held at the quiescent
  value*: the trap does not respond to drive, which isolates drive-induced capture (knee walk-out) from the quiescent
  trapping. Both are legitimate debugging views. If both are wanted, the switch becomes a three-way `On | Off | Held`.
- `SelfHeating` and `Dispersion` were added beyond the list asked for, on the same principle. Drop them if unwanted.
