# Brief MC-1 — The `.cmodel` format and the `ComposedFet` device (equation constituents)

**Series:** `brief-modelcomposer-0-overview.md` (D1, D3, D4, D8, D9, D12, D14) · **Tag:** `R-mc1-<m>` · **Depends on:** —
**Area:** a new `src/Core/Devices/Composed/`; `src/Core/Devices/ComponentModelFactory.cs`;
`src/Core/Elaboration/Elaborator.cs`; `src/Cli/DocumentKinds.cs` (classification only); a new
`docs/design/model-composer.md`; `docs/user/src/reference/file-formats.md`
**Read first:** [P1] §6.2.5 (properties of well-defined constitutive relations), §6.2.9 (linearisation, eqs.
6.13–6.17), §6.3.1 (eqs. 6.19–6.21); `docs/design/sdd.md` §1–§4; `ExternalDeviceModel` and
`Elaborator.BuildExternalDeviceNodes` (internal nodes that HB can see)

---

## 1. Goal

Before there is any composer, a `.cmodel` file can be written by hand (or by a test) and placed as a `ComposedFet`.
It then simulates correctly in DC, S-parameters and HB, scales with finger count, and honours the temperature
rule. Constituents are **equations** only in this phase; tables and ANNs arrive in MC-2.

## 2. Requirements

**R-mc1-1 — The container.** A `.cmodel` is a ZIP holding `model.json` and `arrays/<name>.npy`. `model.json`
fields:
- `FormatVersion` (1) and `Kind` (`"Fet"`); `Terminals` `["G","D","S"]`.
- `BasePeriphery` `{ Fingers: N0, UnitWidth: "<value unit>" }`.
- `Temperature`: `{ Mode: "Single", T0 }` or `{ Mode: "Range", Tmin, Tmax }`, in °C.
- `Network` `{ Netlist: "<.cnl text of the six-pin network, values resolved>", Scaling: { "<variable>":
  "<rule>" } }`.
- `Core.Constituents`: `Id`, `Ig`, `Qg`, `Qd` (optional `IdRF`), each `{ Method, Inputs, …payload }`.
- `Extrapolation` (MC-2), `Thermal` (MC-9) and `Traps` (MC-10) — absent until those phases.
- `Symbol`: a library symbol name, or a `.csym` path relative to the `.cmodel`.
- `Provenance` `{ Created, CircuitRfVersion, Sources: [{ Path, Sha256 }], SettingsDigest }`.

The **writer is deterministic**: fixed entry order, fixed ZIP timestamps, and `.npy` through RfCore's `NpyWriter`.
The same model writes the same bytes.

**R-mc1-2 — The validator.** The reader refuses with a sentence that **names the field**. An unknown `Method`,
scaling rule or trap model name is refused **by name**. A `FormatVersion` newer than the reader's says so. A
network that does not have exactly the six pins of D5 is refused. `check` classifies `.cmodel` in
`DocumentKinds` and calls **this** validator, with no logic of its own (the CLAUDE.md rule for `check`).

**R-mc1-3 — Equation constituents.**
- An expression in the one expression engine. Variables: `vgs` and `vds` (intrinsic, V); `T` (°C) only when
  `Temperature.Mode` is `Range`; `phi1`/`phi2` only with traps (MC-10).
- Named parameters are constants written into the constituent.
- Derivatives come from the engine's dual-number AD, as the SDD's do.
- Units: currents in A, charges in C. `Ig` is two expressions, `IgGs` and `IgGd` (branch currents of [P1] Fig.
  6.30), so the symmetry option (MC-2) has the branches it needs.
- An equation that is NaN or infinite at a sample is a named evaluation failure, never a silent zero.

**R-mc1-4 — The component.** `ComposedFet` is registered in `ComponentModelFactory`.
- Nets: `g d s`, and optionally `t` (MC-9; in this phase a fourth net is refused).
- Parameters: `File`, `N`. `File` resolves relative to the referencing document, exactly as `SnpModel`'s `File`
  does, with the same named refusals: missing, a folder, unreadable.
- `N` must be a positive integer; a non-integer is a refusal stating that `N` is a finger count. It defaults to
  `N0`.
- Loaded models are cached by full path, size and modification time — the `TouchstoneCache` pattern — so 100
  instances read the file once (a counter, not a timing).

**R-mc1-5 — Elaboration as a cell (D3).**
- For each distinct (`.cmodel`, `N`), the elaborator builds an **in-memory cell** with ports `g d s`:
  - the network's components, with **numeric values already scaled** by their rules (R-mc1-7);
  - its internal net names kept, pins 4/5/6 named `gi`/`di`/`si`;
  - one `ComposedCore` instance on `gi di si`.
- It flattens through the existing hierarchy path, so `V(X1.gi)` and `X1.rg` resolve exactly as in any
  hierarchy, and the netlist and the Data Display see ordinary components.
- The synthesised cell never appears in the workspace, the palette or a saved file.
- Whether this is a synthesised `Cell` handed to `FlattenCell` or an equivalent is the implementer's choice; the
  observable contract is the paths, the probes and the results.

**R-mc1-6 — `ComposedCore`.**
- `Kind` Nonlinear; intrinsic ports gate–source and drain–source on `gi di si`.
- `Evaluate` returns `i = (Ig, Id)` and `q = (Qg, Qd)` with their Jacobians `dg`, `dc`, all multiplied by `N/N0`.
  `Ig` is `IgGs + IgGd` at the gate, and `IgGd` enters the drain current with its sign.
- Implement `EvaluateInto` (`HasEvaluateInto`), so HB grids evaluate without allocation.
- **The internal-node seam** used by MC-7, MC-9 and MC-10 is defined here: the core declares how many internal
  state nodes it has. They are minted as ground-referenced nodes with ordinary global rows, as
  `BuildExternalDeviceNodes` does. They are never eliminated locally, because their harmonic content matters in HB.
  MC-1 declares none.

**R-mc1-7 — Scaling (D9).**
- `fixed`, `perFinger` and `perFingerInverse` act on network variables; the core multiplies by `N/N0`.
- The scaled values are what the synthesised cell holds, so `explain`/the netlist show the numbers simulated.
- `M` is untouched and composes with `N` (`M=2 N=8` is two 8-finger devices).

**R-mc1-8 — Temperature rule (D8).**
- `Single`: there is no `T` input, and one warning per run when `temp` ≠ `T0`, naming both.
- `Range`: `T = temp` (until MC-9 adds self-heating), clamped to `[Tmin, Tmax]`, with one warning per run when it
  clamps.
- Warnings go through the netlist's existing warning channel.

**R-mc1-9 — The symbol.**
- Placing a `ComposedFet` copies the model's `Symbol` into the instance; the Inspector changes it per instance.
- A symbol that cannot be resolved falls back to the standard 3-terminal FET symbol, with one warning naming the
  missing symbol.
- The symbol's pins 1/2/3 bind to `g d s` by number.

**R-mc1-10 — Documents.**
- Write `docs/design/model-composer.md` from the overview, with the `.cmodel` schema as built.
- Add `.cmodel` (and a placeholder for `.cmc`) to `docs/user/src/reference/file-formats.md`.
- Add `ComposedFet` to `docs/user/src/reference/components.md`.

## 3. Not in this phase
Tables, ANNs, extrapolation and symmetry (MC-2); thermal and trap nodes (MC-9, MC-10); dispersion (MC-7);
anything that *creates* a `.cmodel` from data.

## 4. Gates (minimal tests, run only these classes)
- `ComposedFetEquivalenceTests` — the reference is an **SDD**. An equation `.cmodel` (a Curtice-style tanh `Id`, a
  diode `IgGs`, non-separable `Qg`/`Qd` with a transcapacitance) inside the GaN network equals an SDD with the same
  expressions inside the same network written as a `.cnl`:
  - DC node voltages to 1e-12 relative;
  - S-parameters at three biases to 1e-9;
  - HB fundamental and harmonics at one drive to 1e-9.
- `CmodelFormatTests` — write → read → write is byte-identical; three refusals name their field; a newer
  `FormatVersion` is refused; an unknown rule name is refused by name.
- `PeripheryScalingTests` — `N = 2·N0` equals a hand-scaled netlist (core doubled, `perFinger` doubled,
  `perFingerInverse` halved, `fixed` unchanged); a non-integer `N` is refused.
- `ComposedFetProbeTests` — `V(X1.gi)` and the path `X1.rg` resolve; 100 instances load the file once (counter).
- `ComposedFetTemperatureTests` — `Single` warns once when `temp` ≠ `T0`; `Range` clamps and warns once.
- `CheckCmodelTests` — `check` on a broken `.cmodel` reports the validator's sentence and exits 1.
