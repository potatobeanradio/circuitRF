# Sonnet Brief E1 — unify branch currents into a single labeled `I` cube (engine + accessor)

Foundation for the V/I-symmetric trace card (option A). Make **`I` a single cube with a labeled
`branch` axis**, exactly mirroring `V`'s `node` axis: all branches live in one cube; `__ProbeBranches`
marks the IProbe subset (the labeled branches) the way `__LabeledNodes` marks user-named nodes. This
**replaces** the separate `I:<probe>` / `I:<instance>:<terminal>` cubes from Briefs 1–2 and folds the
measurement `I(...)` accessor into the existing `V`/`INl` node-accessor path (deleting the special-case
branch accessor). Net: less code, full symmetry.

Two-tone IProbe currents remain deferred (no back-solver) — two-tone packs only the device-port branches.

Scope: `src/Engine/DcResultPacker.cs`, `src/Engine/HarmonicBalance/HbEngine.cs`
(`BuildSingleToneDataSet`, `BuildTwoToneDataSet`), `src/Core/Expressions/Evaluator.cs`
(`EvalQualifiedAccessor` + delete `EvalBranchCurrentAccessor`), `RfCore/src/Data/DataSet.cs` (`I()` helper),
plus tests + a docs note. Build 0W/0E (`TreatWarningsAsErrors=true`); tests green.

Read first: the listed files, and the existing `V` cube + `node` axis + `__LabeledNodes` shape in each
packer — the `I` cube must mirror it.

## Target cube shape (mirror of V)
- **`I`** cube. DC: `[branch]` Real. HB single-tone: `[branch, harmonic]` Complex. HB two-tone:
  `[branch, mixIndex]` Complex. Branch axis: name `"branch"`, `Labels` = branch names
  (IProbe names like `Ids`; device-port keys like `M1:d`), values `0..B-1`. (Mirrors `node` axis.)
- **`__ProbeBranches`** (unchanged name): axis `"probe"`, `Labels` = the IProbe branch names — the
  *labeled subset* surfaced by default. (Mirrors `__LabeledNodes`.) Omitted when there are no IProbes.
- Device-port branches are present in `I` but **absent from `__ProbeBranches`** → they are the
  "unlabeled" branches the eye/Show-all reveals (mirrors unlabeled nodes).

## 1. DC packer (`DcResultPacker.Pack`)
Replace the per-probe scalar loop:
```csharp
foreach (var (probeName, current) in dc.ProbeCurrents)
    ds.Add("I:" + probeName, DataCube.Scalar(current));
```
with a single `[branch]` cube (keep `__ProbeBranches`, now derived from the same names):
```csharp
if (dc.ProbeCurrents.Count > 0)
{
    var bNames = dc.ProbeCurrents.Keys.ToArray();              // stable order
    var bVals  = Enumerable.Range(0, bNames.Length).Select(i => (double)i).ToArray();
    var branchAxis = new Axis("branch", bVals, "A", bNames);
    var iVals = bNames.Select(n => dc.ProbeCurrents[n]).ToArray();   // Real, aligned to bNames
    ds.Add("I", new DataCube([branchAxis], iVals));

    var pIdx = Enumerable.Range(0, bNames.Length).Select(i => (double)i).ToArray();
    ds.Add("__ProbeBranches", new DataCube([new Axis("probe", pIdx, "", bNames)], new double[bNames.Length]));
}
```
(DC has only IProbe branches, so every branch is labeled — like a fully-labeled `V`.)

## 2. HB single-tone (`BuildSingleToneDataSet`)
Delete both current loops — the device-port `foreach (… portCurrents) ds.Add("I:" + branchKey, …)` AND
the probe `foreach (… probeCurrents) ds.Add("I:" + probeName, …)` block (including its inline
`__ProbeBranches`). Replace with one combined `I` cube, **probes first** (labeled) then device ports
(unlabeled), and a single `__ProbeBranches` for the probe subset:
```csharp
// Unified I cube [branch, harmonic]: IProbe branches (labeled) + device-port branches (unlabeled).
var brNames = new List<string>();
var brSpecs = new List<Complex[]>();

string[] probeLabels = Array.Empty<string>();
if (probeCurrents is { Count: > 0 })
{
    probeLabels = probeCurrents.Keys.ToArray();
    foreach (var name in probeLabels) { brNames.Add(name); brSpecs.Add(probeCurrents[name]); }
}
foreach (var (key, specList) in portCurrents)
{
    if (specList.Count == 0) continue;
    brNames.Add(key); brSpecs.Add(specList[0]);
}

if (brNames.Count > 0)
{
    int B = brNames.Count;
    var bVals = Enumerable.Range(0, B).Select(i => (double)i).ToArray();
    var branchAxis = new Axis("branch", bVals, "", brNames.ToArray());   // unit "" like HB node axis
    var iData = new Complex[B * K1];
    for (int b = 0; b < B; b++)
    {
        var spec = brSpecs[b];
        for (int k = 0; k < K1; k++) iData[b * K1 + k] = k < spec.Length ? spec[k] : Complex.Zero;
    }
    ds.Add("I", new DataCube([branchAxis, harmAxis], iData));

    if (probeLabels.Length > 0)
    {
        var pIdx = Enumerable.Range(0, probeLabels.Length).Select(i => (double)i).ToArray();
        ds.Add("__ProbeBranches",
            new DataCube([new Axis("probe", pIdx, "", probeLabels)], new double[probeLabels.Length]));
    }
}
```
`V`/`INl`/`__LabeledNodes` unchanged. (`INl` stays node-indexed — it is not a branch current.)

## 3. HB two-tone (`BuildTwoToneDataSet`)
Replace the device-port `foreach (… portCurrentsByBranch) ds.Add("I:" + branchKey, …)` with a single
`[branch, mixIndex]` cube over the device-port branches; **no** `__ProbeBranches` (no probe currents in
two-tone). Keep the existing `TODO(two-tone IProbe currents)` note.
```csharp
var brNames = new List<string>();
var brSpecs = new List<Complex[]>();
foreach (var (key, specList) in portCurrentsByBranch)
{
    if (specList.Count == 0) continue;
    brNames.Add(key); brSpecs.Add(specList[0]);
}
if (brNames.Count > 0)
{
    int B = brNames.Count;
    var bVals = Enumerable.Range(0, B).Select(i => (double)i).ToArray();
    var branchAxis = new Axis("branch", bVals, "", brNames.ToArray());
    var iData = new Complex[B * M];
    for (int b = 0; b < B; b++)
    {
        var spec = brSpecs[b];
        for (int m = 0; m < M; m++) iData[b * M + m] = m < spec.Length ? spec[m] : Complex.Zero;
    }
    ds.Add("I", new DataCube([branchAxis, mixAxis], iData));
}
```

## 4. Measurement accessor (`Evaluator.EvalQualifiedAccessor`) — fold `I` into the `V`/`INl` path
The `V`/`INl` accessor already pins a labeled axis (`"node"`), prepends sweep axes, treats the
harmonic/mixIndex axis as last, and (V only) falls back to the back-solver. `I` is identical with axis
`"branch"` and no back-solver fallback. So:

- **Delete** the early dispatch line:
  ```csharp
  if (accessorName == "I")
      return EvalBranchCurrentAccessor(ds, cl.Args, scope, cl.Name);
  ```
- **Widen** the node block guard and parameterize the axis name + the back-solver fallback:
  ```csharp
  if (accessorName is "V" or "INl" or "I")
  {
      var cube = ds[accessorName];
      string axisName = accessorName == "I" ? "branch" : "node";
      var nameVal = EvalExpr(cl.Args[0], scope);
      string label = nameVal.Kind == ValueKind.String ? nameVal.AsString() : nameVal.ToString();

      int axisIdx = -1;
      for (int a = 0; a < cube.Rank; a++)
          if (cube.Axes[a].Name == axisName) { axisIdx = a; break; }
      if (axisIdx < 0) axisIdx = 0;

      var labels = cube.Axes[axisIdx].Labels;
      int idx = labels is null ? -1
          : Array.FindIndex(labels, s => s.Equals(label, StringComparison.OrdinalIgnoreCase));

      // V-only: linear-interior node → back-solver.
      if (idx < 0 && accessorName == "V" && _ctx.TryGetBackSolver(analysisName, out var bs))
          return EvalVFromBackSolver(bs!, cube, cl.Args, scope, cl.Name, label);

      if (idx < 0)
          throw new ExpressionException(
              $"{cl.Name}: {(axisName == "branch" ? "branch" : "node")} '{label}' not found. " +
              $"Available: [{string.Join(", ", labels ?? [])}]");

      // (identical slice-build as the existing V/INl block, using axisIdx/idx)
      …
  }
  ```
  (Reuse the existing slice-building tail verbatim, with `nodeAxisIdx`→`axisIdx`, `nodeIdx`→`idx`.)
- **Delete** the `EvalBranchCurrentAccessor` method entirely.

Result: `HB1.I("Ids")`, `DC1.I("Ids")`, and `HB1.I("M1:d")` all resolve through the same code as
`V("drain")`; `HB1.I` (no args) returns the whole `I` cube via the existing zero-arg path.

## 5. `DataSet.I()` helper (`RfCore/src/Data/DataSet.cs`)
Fix the axis name (currently `"node"`):
```csharp
public DataCube I(string branchName, params object[] remainingArgs) =>
    NodeTrace("I", "branch", branchName, remainingArgs);
```

## Tests
Engine (`tests/Engine.Tests`):
1. **Dc_I_Cube_Branch:** DC with two IProbes → one `I` cube rank-1 `[branch]`, `Labels` = probe names,
   values aligned; `__ProbeBranches` lists both; no `I:<probe>` cubes exist.
2. **Hb_I_Cube_BranchHarmonic:** single-tone HB with an IProbe + an SDD device → `I` is `[branch, harmonic]`;
   branch `Labels` contain the probe name AND the device-port key; `__ProbeBranches` lists only the probe.
3. **Hb_I_k0_MatchesDc:** `I` at `harmonic=0` for the probe branch equals the DC `I` value for that probe
   (Brief-1 parity, preserved).
4. **TwoTone_I_NoProbe:** two-tone HB → `I` is `[branch, mixIndex]` over device-port branches; no
   `__ProbeBranches`.
5. **No_Legacy_I_Cubes:** neither packer emits any `I:` -prefixed cube.

Accessor (`tests/Core.Tests` or wherever measurement-accessor tests live):
6. **I_Accessor_PinsBranch:** `HB1.I("Ids")` returns the harmonic spectrum for that branch (mirror of
   `HB1.V("drain")`); `HB1.I("Ids", 1)` returns the fundamental; `HB1.I` returns the whole cube.
7. **I_Accessor_UnknownBranch_Throws:** `HB1.I("nope")` throws with an "Available: […]" branch list.

Update any existing tests that referenced `I:<probe>` / `I:<inst>:<term>` cubes to the new `I` cube.

## Gate (manual)
Single-tone HB with IProbe `Ids` + an SDD device. `PDC = DC1.I("Ids") * DC1.V("Vds")` still resolves.
In Data Display, `HB1.I` now appears as one cube with a `branch` axis (pin/sweep a branch via the generic
axis-role editor) — the polished V/I item-combo UX comes in the follow-up display brief.

## On completion
- `src/Engine/HarmonicBalance/CLAUDE.md` + `src/Engine/CLAUDE.md`: branch currents are one `I` cube with a
  labeled `branch` axis (IProbe branches labeled via `__ProbeBranches`; device-port branches unlabeled),
  mirroring `V`/`node`/`__LabeledNodes`. Two-tone packs device-port branches only (IProbe two-tone still
  pending a mixing-lattice back-solver).
- `docs/design/measurements.md`: `I(name, …)` pins the `branch` axis of the single `I` cube — the exact
  mirror of `V(name, …)`; note the two-tone IProbe gap.
- Flag for the follow-up display brief: the landed Brief-2 separate-cube branch filter and the
  `brief-picker-cascade-layout.md` (4c) item handling are superseded by the unified-cube model.
