# Sonnet Brief — HB IProbe branch currents (full spectrum) + probe provenance

Goal: make HB surface **IProbe branch currents** the way DC already does, so a user who places an
IProbe gets its current as a plottable cube and `I("<probe>")` resolves in measurement expressions for
HB (not just DC). Keep the existing device-port current machinery untouched ("keep the system") — this
**adds** IProbe currents and provenance; it removes nothing.

Read first: `docs/design/measurements.md`, `src/Engine/HarmonicBalance/CLAUDE.md`, and the existing
`DcResultPacker` (the DC precedent). Build 0 warnings/0 errors (`TreatWarningsAsErrors=true`); tests green.

## Background (verified on disk)

- **DC** (`DcResultPacker.Pack`) emits one `I:<probeName>` **scalar** cube per IProbe, from
  `dc.ProbeCurrents`. All of DC's `I:*` cubes are IProbe currents.
- **HB** (`HbEngine.BuildSingleToneDataSet` / `BuildTwoToneDataSet`) emits `I:<instance>:<terminal>`
  cubes from `ComputeDevicePortCurrents` — **device SDD-port currents**, keyed instance:terminal. HB emits
  **no IProbe current** today (an IProbe is `ModelKind.Linear`, so it never enters the nonlinear
  device-port set).
- `IProbeModel` (`src/Core/Devices/IProbeModel.cs`) stamps a 0 V branch and records
  **`LastBranchIndex`** (its MNA branch slot) — the comment already says "Used by HbLinearBackSolver."
- `HbLinearBackSolver.GetSolution(k, sweepIdx)` returns the full MNA solution vector `x`:
  `x[0..NonGroundCount-1]` = node voltages, **`x[NonGroundCount + branchIdx]` = branch currents**
  ("IProbe, inductors, etc."). It is **single-tone only** — `HbRunResult` carries no back-solver for
  two-tone runs (see the two-tone scope note below).

So a single-tone IProbe current spectrum is a direct read: for each IProbe, for each harmonic k, take
`GetSolution(k, sweepIdx)[NonGroundCount + LastBranchIndex]`.

## 1. Compute single-tone IProbe currents (`HbEngine.Run`, single-tone path)

In `Run(HbAnalysisParams p)`, after the `backSolver` is constructed and before/at the
`BuildSingleToneDataSet(...)` call, build a probe-current map:

```csharp
// IProbe branch currents (full spectrum) via the linear back-solver.
// Each IProbe's branch slot is IProbeModel.LastBranchIndex; the back-solver solution
// vector holds branch currents at NonGroundCount + branchIdx.
var probeCurrents = new Dictionary<string, Complex[]>(StringComparer.Ordinal);
int nonGround = extractor.NonGroundCount;            // back-solver x layout: [nodes | branches]
foreach (var ec in _netlist.Components)
{
    if (ec.Model is not IProbeModel ip || ip.LastBranchIndex < 0) continue;
    string probeName = ec.InstancePath;              // MATCH DcResultPacker's key (see note)
    var spec = new Complex[K + 1];
    for (int k = 0; k <= K; k++)
    {
        var x   = backSolver.GetSolution(k, 0);      // single-tone: sweepIdx 0
        int row = nonGround + ip.LastBranchIndex;
        spec[k] = row < x.Length ? x[row] : Complex.Zero;
    }
    probeCurrents[probeName] = spec;
}
```
Then pass `probeCurrents` into `BuildSingleToneDataSet`.

**VERIFY two things against the code before trusting the index:**
1. **Branch-slot offset.** Confirm `x[NonGroundCount + LastBranchIndex]` is the right element — i.e. that
   `LastBranchIndex` is the *branch-local* index (0-based within the branch section) and
   `SolveFullNetwork` lays branches after the `NonGroundCount` node rows in that order. If `AddBranch()`
   returns an absolute MNA row instead, use `x[LastBranchIndex]` directly. Check `MnaSystem.AddBranch` /
   `SolveFullNetwork`.
2. **Probe-name key.** Read how `NonlinearDcEngine` keys `dc.ProbeCurrents` (instance name vs
   `InstancePath`) and use the **same** key here, so the same probe is `I:<name>` in both DC and HB and
   `I("<name>")` resolves identically.

## 2. Pack `I:<probe>` cubes + `__ProbeBranches` provenance (`BuildSingleToneDataSet`)

Add a parameter `Dictionary<string, Complex[]> probeCurrents` to `BuildSingleToneDataSet`. After the
existing device-port `I:` loop (keep it), add the probe cubes over the **same `harmAxis`** and the
provenance side-cube:

```csharp
foreach (var (probeName, spec) in probeCurrents)
{
    var iData = new Complex[K1];
    for (int k = 0; k < K1 && k < spec.Length; k++) iData[k] = spec[k];
    ds.Add("I:" + probeName, new DataCube([harmAxis], iData));
}

// Provenance: which I:* cubes are IProbes (the current analogue of __LabeledNodes).
if (probeCurrents.Count > 0)
{
    var names = probeCurrents.Keys.ToArray();
    var idx   = Enumerable.Range(0, names.Length).Select(i => (double)i).ToArray();
    ds.Add("__ProbeBranches", new DataCube(
        [new Axis("probe", idx, "", names)], new double[names.Length]));
}
```
`I:<probe>` and the device-port `I:<instance>:<terminal>` cubes coexist (different keys, no collision).
`__ProbeBranches` is `__`-prefixed, so `StackSweepAxis` passes it through sweep-invariantly and the
display already skips it as a plottable signal.

## 3. DC provenance parity (`DcResultPacker.Pack`)

DC already emits `I:<probe>` cubes; just add the same provenance so the display filter (next brief) treats
DC and HB identically:
```csharp
if (dc.ProbeCurrents.Count > 0)
{
    var names = dc.ProbeCurrents.Select(p => p.Key /* same key used for "I:" + ... */).ToArray();
    var idx   = Enumerable.Range(0, names.Length).Select(i => (double)i).ToArray();
    ds.Add("__ProbeBranches", new DataCube(
        [new Axis("probe", idx, "", names)], new double[names.Length]));
}
```
(Match the exact key DcResultPacker already uses in `ds.Add("I:" + probeName, …)`.)

## Two-tone scope — OPEN DECISION (do not implement blindly)

`RunTwoTone` builds no back-solver, so there is **no path to a two-tone IProbe current spectrum** without
new engine work (a 2-D / mixing-lattice back-solver). **Default for this brief: single-tone only.** In
`BuildTwoToneDataSet`, do **not** add probe cubes; leave a `// TODO(two-tone IProbe currents): needs a
mixing-lattice back-solver` and no `__ProbeBranches`. (If the owner decides two-tone is in scope now, it
is a separate, larger stage — a 2-D back-solver — not part of this brief.)

## Tests — `tests/Engine.Tests` (headless)

1. **Hb_IProbe_CurrentCube_Present:** single-tone HB on a small netlist with one IProbe in a known branch
   → `I:<probe>` cube exists, rank-1 over `harmonic`, length K+1.
2. **Hb_IProbe_DcComponent_MatchesDc:** the k=0 entry of the HB `I:<probe>` cube equals (within tol) the
   DC operating-point probe current from `NonlinearDcEngine` for the same netlist (the HB internal DC
   solve and the standalone DC must agree at DC).
3. **Hb_IProbe_Provenance:** `__ProbeBranches` lists the probe name(s); device-port `I:<inst>:<term>` cubes
   are NOT listed.
4. **Hb_DevicePortCurrents_StillEmitted:** the existing `I:<instance>:<terminal>` cubes are unchanged
   (regression guard — "keep the system").
5. **Dc_ProbeBranches_Provenance:** `DcResultPacker` output now carries `__ProbeBranches` with the probe
   name(s); `I:<probe>` cubes unchanged.

## Gate (manual)
Single-tone HB with an IProbe (say `Ids`) + a measurement `Ig = mag(HB1.I("Ids"))` → run → the
`I:Ids` cube plots vs harmonic, and `HB1.I("Ids")` resolves in a measurement (parity with DC). Device-port
currents still present (advanced/typed access).

## On completion
Note in `src/Engine/HarmonicBalance/CLAUDE.md`: HB now emits `I:<probe>` cubes (full single-tone spectrum)
from the linear back-solver's branch-current rows, alongside the existing device-port `I:<inst>:<term>`
cubes; `__ProbeBranches` marks the IProbe set for the Data Display filter. Two-tone IProbe currents remain
a gap pending a mixing-lattice back-solver.
