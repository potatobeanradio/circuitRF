# ENV-FI — Envelope: a floating island behind the nonlinear devices must not amplify the Newton residual

## Why

Harmonic balance showed hundreds of kilovolts on a floating source and the ferrite-bead internals of a bridge
rectifier, while transient on the same circuit was correct. The cause and the HB fix are in `src/Engine/RESOLVED.md`
§"HB back-solve: a floating island behind the nonlinear devices read hundreds of kilovolts" (commit `028b6cdfa`):

- With the devices removed, the source and the bead internals reach ground only through gmin, so that island's common
  mode is ~1e11 Ω.
- Every non-interface node was rebuilt by injecting the solve's device currents `INl`. Those differ from what the linear
  partition carries at the converged interface voltages by the Newton residual `F` (~1e-7 A, inside tolerance), and the
  island multiplied `F` into kilovolts.
- HB now injects `HbEngine.LinearSideInjection = −(I_src + Y_NN·V)`. That differs from `INl` by exactly `F`, so an
  ordinary circuit is unchanged, and the back-solve reproduces the returned interface voltages to round-off (2e-16).

The same circuit run in circuit envelope, with the 50 Hz mains on a slow envelope and an 869 MHz carrier, is the
natural next analysis. Both envelope engines rebuild the linear state the same way HB used to, and they do it on
**every step**, not only once. The mechanism has already been seen there once: issue 66 (`RESOLVED.md` around line
4544) showed a seam error of 20.6 on a net that only devices touch. The fix, `DeviceOnlyInterfaceIndexOf`, reads such a
node from `V` instead of the state, which covers device-only nodes and nothing else. A node behind a bead is not device-only.

## Where the envelope engines inject `INl`

| Site | `EnvelopeEngine.cs` (one fundamental) | `EnvelopeEngineMt.cs` (mixing lattice) |
|---|---|---|
| HB start point: full state from `SolveFullNetwork(…, sol.INl, …)` | ~line 1006 | ~line 619 (conjugated for a negative product) |
| Every step: `x = x_oc − Σ_j Zcol_j · (Inj_j + jkω·q_j)` | ~line 616, inside `Advance` | ~line 439 (conjugated when `pr.Neg`) |
| Step-split sub-steps (`Split`) | reach the same `Advance` | same |
| Recorded node read from `V` when only devices touch it | `DeviceOnlyInterfaceIndexOf`, line 474 | line 303 |

## Requirements

**R-fi-1 — Reproduce before changing anything.** Build a generic test circuit with no vendor model: a floating source
feeding a four-diode bridge through four bead networks. Each bead is R1 163.4 Ω ‖ L1 442.5 nH, then C 0.2828 pF across
to an inner node, R2 61.04 Ω ‖ L2 998.4 nH, R3 62.71 Ω ‖ L3 45.98 nH, then R4 35 mΩ in series.
`HbFloatingIslandBackSolveTests` has it written out. Drive it with a `MOD_Source` on a carrier the envelope accepts,
starting from `examples/Envelope`'s simplest bench. Run it on both engines (a second tone sends a run to
`EnvelopeEngineMt`). Record two numbers per engine:

- **A:** |V(source node) − V(diode node it feeds)| at the start point and at the last step.
- **B:** the same figure read from the HB start point alone.

If neither engine shows the error, report that with the numbers and stop. Do not build R-fi-2 against a symptom that
was never measured.

**R-fi-2 — Inject the linear side's own current, at both sites, in both engines.**
- **Start point:** pass `HbEngine.LinearSideInjection(V, yNN, iSrc)` in place of `sol.INl`. It is `internal static` in
  the same assembly, so `Envelope/` can already call it. Keep the existing conjugation for a negative product.
- **Each step:** the state update injects the total `F` balances. `BuildF`/`BuildFNd` form
  `F = iSrc + yEff·V + inj + jkω·qNl` (k > 0). The replacement for `Inj + jkω·q` is therefore
  `−(iSrc[k] + yEff[k]·V[:,k])`, made from the step's own `iSrc` and `yEff`, the stage's `Y`, which already carries
  α·C. Do not recompute either one.
- Keep the DC bin real, as the current update does, and keep the `pr.Neg` conjugation in the lattice engine.
- The accepted `V` is what Newton returned, so this needs no extra device evaluation. The cost is one N×N
  multiply per product per step, which is negligible next to the step's device evaluations.

**R-fi-3 — The state carries history, so state the change in trajectory.** `x` feeds the next step's
`−C(b1·x(n−1) + b2·x(n−2))`. Replacing `Inj` with `Inj − F` therefore changes every later step by a residual-sized
amount, so measure the change on every existing envelope test before deciding anything:

- An assertion that moves by no more than the run's tolerance may have its bound restated. Never widen a bound
  just to make a test pass.
- An assertion that moves by more is a finding. Report it before changing the test.

**R-fi-4 — `DeviceOnlyInterfaceIndexOf` after the fix.** With the state reproducing `V` at every interface node, a
device-only node's state value and its `V` agree to round-off, so the special case should no longer change any result.
Measure that on `EnvelopeDeviceOnlyNetTests` with the special case bypassed:

- If they agree, delete the special case and its two call sites, and say so in `RESOLVED.md`.
- If they do not, keep it and record why.

**R-fi-5 — Gate.** Add one test per engine on R-fi-1's circuit. Assert that the source node follows the diode node it
feeds, at the start point and at the last step, within a millivolt-scale bound justified by the bead's impedance. Each
test must fail without R-fi-2. Show that failure once, the way `HbFloatingIslandBackSolveTests` did (−7959 V against
3.2 V), and record the number in `RESOLVED.md`.

## Out of scope

- The HB paths (done in `028b6cdfa`).
- The SDD control-current back-solve inside the HB Newton loop (`HbNewton` ~lines 1105 and 2003). It reads a branch
  current and has the same exposure only when that branch sits in a floating island. Note it in `RESOLVED.md` if
  R-fi-1's circuit can reach it, and do not change it here.
- Transient. It solves the whole MNA with no partition, so it has no island.

## Tests to run

Run only `tests/Engine.Tests`, filtered with `FullyQualifiedName~Envelope|FullyQualifiedName~PropagationChannel|FullyQualifiedName~DropReuse`.
Run the full suite only if the owner asks.

## Findings

Record them in `src/Engine/RESOLVED.md`, under a section that follows the HB one, never in a `CLAUDE.md`.
