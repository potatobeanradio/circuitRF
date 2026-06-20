# Brief #1: Nonlinear-device small-signal seam + DC-biased S-parameter

Design ref: `docs/design/nonlinear-in-linear-engines.md` (read it first — this brief implements §3). This is
the foundation brief of a 4-part feature; NonlinearC itself lands in brief #2. After this brief, any
`Kind==Nonlinear` device becomes electrically visible in S-parameter, linearized at the auto-solved DC
operating point. Testable immediately with a 1-port resistive SDD — no NonlinearC needed yet.

Stack/rules: .NET 10, C# 14, Core/Engine reference NO Avalonia (CI-enforced). Build must end **0W/0E**.
Add the gate tests below; report total test count. Newest-first changelog entry in the Engine/Core
CLAUDE.md after landing. No `.cnl`/format changes; no persistence changes.

Two files change: `src/Core/ComponentModel.cs` (add one virtual) and `src/Engine/SParameterEngine.cs`
(DC pre-pass + route nonlinear devices to the new virtual).

---

## Part A — `ComponentModel.StampLinearized` (Core)

Add a virtual to `ComponentModel` (src/Core/ComponentModel.cs). It linearizes a nonlinear device at a bias
operating point and stamps the small-signal admittance block `Y[p,q] = Dg[p,q] + jω·Dc[p,q]` (both from
`Evaluate(bias)`). The port→node convention is **identical** to `NonlinearDcEngine`'s nonlinear loop: port
`p` spans `Nodes[2p]` (+) and `Nodes[2p+1]` (−).

Add `using System.Numerics;` to the file (it currently has only `using CircuitRF.Core.Elaboration;`).

```csharp
/// <summary>
/// Small-signal linear contribution of a nonlinear device, linearized at the supplied bias
/// operating point. Stamps Y[p,q] = Dg[p,q] + jω·Dc[p,q] (from Evaluate(bias)) as an N-port
/// admittance block, using the same port→node-pair convention as NonlinearDcEngine
/// (port p spans Nodes[2p],Nodes[2p+1]). Linear-only engines (S-parameter, future linear-AC)
/// call this for Kind==Nonlinear devices instead of Stamp(); HB/DC never call it.
/// Base default suits every nonlinear device (NonlinearC, SDD); override only for special cases.
/// </summary>
public virtual void StampLinearized(IMnaContext mna, ElaboratedComponent c, double omega, in PortVoltages bias)
{
    var r = Evaluate(bias);
    int P = PortCount;
    for (int p = 0; p < P; p++)
    {
        int np = c.Nodes.Length > 2 * p     ? c.Nodes[2 * p]     : 0;
        int nm = c.Nodes.Length > 2 * p + 1 ? c.Nodes[2 * p + 1] : 0;
        for (int q = 0; q < P; q++)
        {
            int qp = c.Nodes.Length > 2 * q     ? c.Nodes[2 * q]     : 0;
            int qm = c.Nodes.Length > 2 * q + 1 ? c.Nodes[2 * q + 1] : 0;
            var y = new Complex(r.Dg[p, q], omega * r.Dc[p, q]);
            if (y == Complex.Zero) continue;
            mna.AddBlockAdmittance(np, qp,  y);
            mna.AddBlockAdmittance(np, qm, -y);
            mna.AddBlockAdmittance(nm, qp, -y);
            mna.AddBlockAdmittance(nm, qm,  y);
        }
    }
}
```

Notes:
- `IMnaContext.AddBlockAdmittance(rowNode, colNode, y)` already exists and drops node-0 entries, so
  ground-referenced ports are handled. For a 1-port device this reduces to the familiar 2-terminal
  admittance stamp (n0,n0)+Y,(n0,n1)−Y,(n1,n0)−Y,(n1,n1)+Y == `AddAdmittance(n0,n1,Y)`.
- The base `Evaluate` throws for non-nonlinear models, but `StampLinearized` is only ever called on
  `Kind==Nonlinear` devices (the engine guards), which override `Evaluate`. Do not call it on linear
  devices.
- `Dg`/`Dc` are `double[,]`; `Evaluate` returns full P×P Jacobians (off-diagonal transadmittances are
  honored for multi-port devices like SDD).

---

## Part B — DC-biased S-parameter (`src/Engine/SParameterEngine.cs`)

### B1. Run the DC operating point once, when (and only when) nonlinear devices are present

In `Run(...)`, after `CollectPortsAndBranchLabels` / the namer setup and **before** the
`if (allPortsResistive) RunWavePath(...) else RunLegacyPath(...)` dispatch, insert:

```csharp
// ── Nonlinear devices → solve the DC operating point once and linearize there (design §3.2) ──
// RULE: purely-linear S-parameter runs never touch the DC engine (zero behavior change).
double[]? dcNodeVoltages = null;
bool hasNonlinear = netlist.Components.Any(c => c.Model.Kind == ModelKind.Nonlinear);
if (hasNonlinear)
{
    NonlinearDcEngine.DcResult? dc = null;
    try { dc = NonlinearDcEngine.Run(netlist, settings); }
    catch (NonlinearDcNotConvergedException) { dc = null; }  // DcBiasStepping=Never path throws

    if (dc is { Converged: true })
    {
        dcNodeVoltages = dc.NodeVoltages;
        const double ZeroBiasTol = 1e-9;
        if (dc.NodeVoltages.All(v => Math.Abs(v) < ZeroBiasTol))
            netlist.AddWarningOnce("sparam-zero-bias",
                "No DC bias present; nonlinear components linearized at the 0 V operating point.");
    }
    else
    {
        // Non-convergence (degenerate) → warn + fall back to zero-bias linearization (design §3.5).
        string detail = dc is null ? "(no result)" : $"residual {dc.FinalResidual:G3} after {dc.Iterations} iters";
        netlist.AddWarningOnce("sparam-dc-nonconverged",
            $"DC operating-point solve did not converge ({detail}); nonlinear components linearized at " +
            "0 V. S-parameters may be inaccurate.");
        dcNodeVoltages = null;  // null ⇒ BuildBias yields 0 V
    }
}
```

Then thread `dcNodeVoltages` into both path methods:

```csharp
if (allPortsResistive)
    RunWavePath(netlist, freqsHz, settings, ports, N, mna, freqCount, sMatrices,
        nodeNamer, branchNamer, canRetry, dcNodeVoltages);
else
    RunLegacyPath(netlist, freqsHz, settings, ports, N, z0PerPort, mna, freqCount, sMatrices,
        nodeNamer, branchNamer, canRetry, dcNodeVoltages);
```

`AddWarningOnce` is the existing message channel (used right below for regularization). If the netlist/UI
has a distinct **informational** severity (the Messages panel) prefer it for the `sparam-zero-bias` note —
it's informational, not a warning. If only `AddWarningOnce` exists, keep the "No DC bias present…" wording
(reads as a note, not an error).

### B2. Thread the bias into both path methods and their `StampAll` calls

Add a trailing `double[]? dcNodeVoltages` parameter to **`RunWavePath`** and **`RunLegacyPath`**. In each,
pass it to **every** `StampAll(...)` call (each path calls `StampAll` twice — the initial stamp and the
regularization-retry stamp):

- Wave path: `StampAll(mna, netlist, omega, skipPorts: true, dcNodeVoltages);` (both call sites).
- Legacy path: `StampAll(mna, netlist, omega, dcNodeVoltages: dcNodeVoltages);` (both call sites).

### B3. Route nonlinear devices through `StampLinearized` in `StampAll`

Add a trailing param and one branch. Current `StampAll` ends each iteration with
`ec.Model.Stamp(mna, ec, omega);`. Change the signature and insert the nonlinear branch immediately before
that line:

```csharp
private static void StampAll(
    MnaSystem         mna,
    ElaboratedNetlist netlist,
    double            omega,
    bool              skipPorts        = false,
    double[]?         dcNodeVoltages   = null)
{
    mna.Reset();
    foreach (var ec in netlist.Components)
    {
        if (ec.Model is MutualInductanceModel) continue;

        if (ec.Model is P1ToneModel p1)   // (existing P1Tone block — unchanged)
        {
            bool buried = ec.InstancePath.Contains('.');
            p1.StampSParamDriveTie(mna, ec);
            if (!buried && !skipPorts) p1.StampAsSParamPort(mna, ec);
            continue;
        }

        if (IsSParamPort(ec.Model) && ec.InstancePath.Contains('.')) continue;
        if (skipPorts && IsSParamPort(ec.Model)) continue;

        // Nonlinear devices: small-signal linearization at the DC operating point (design §3).
        // Never IsSParamPort, so they fall through the port skips above to here.
        if (ec.Model.Kind == ModelKind.Nonlinear)
        {
            ec.Model.StampLinearized(mna, ec, omega, BuildBias(ec, dcNodeVoltages));
            continue;
        }

        ec.Model.Stamp(mna, ec, omega);
    }
    foreach (var ec in netlist.Components)
        if (ec.Model is MutualInductanceModel)
            ec.Model.Stamp(mna, ec, omega);
}
```

Add the two small helpers (private static, anywhere in the class):

```csharp
/// <summary>Builds a device's bias PortVoltages from the DC node-voltage solution, using the same
/// port→node-pair convention as NonlinearDcEngine (port p = Nodes[2p] − Nodes[2p+1]).
/// Null dcNodeVoltages ⇒ all-zero bias (purely-linear run never reaches here; DC-fail fallback).</summary>
private static PortVoltages BuildBias(ElaboratedComponent ec, double[]? dcNodeVoltages)
{
    int P = ec.Model.PortCount;
    var v = new double[P];
    for (int p = 0; p < P; p++)
    {
        int np = ec.Nodes.Length > 2 * p     ? ec.Nodes[2 * p]     : 0;
        int nm = ec.Nodes.Length > 2 * p + 1 ? ec.Nodes[2 * p + 1] : 0;
        v[p] = NodeV(dcNodeVoltages, np) - NodeV(dcNodeVoltages, nm);
    }
    return new PortVoltages(v);
}

/// <summary>DC voltage of 1-based circuit node (0 = ground = 0 V).</summary>
private static double NodeV(double[]? dc, int node1based)
    => (dc is null || node1based <= 0 || node1based - 1 >= dc.Length) ? 0.0 : dc[node1based - 1];
```

`StampLinearized` is on `IMnaContext`'s consumer side via `mna` (the `MnaSystem` passed in implements
`IMnaContext`). `PortVoltages`/`NonlinearResult`/`ModelKind` are all in `CircuitRF.Core` (already imported).

### B4. Why the DC pre-pass is safe for the existing port types

- `Term`/`Port` are skipped inside `NonlinearDcEngine` (inert at DC) — unchanged.
- `P1Tone` is `Kind==Linear` (verified), so the DC engine stamps it via `Stamp(ω=0)`, which in S-param
  mode (`_fc==0`, the default — `SetToneContext` is an HB-only call) stamps only its Z-port between its
  external nodes; its internal `__drv` node is tied off by `gmin`. No drive, no `Evaluate`, no throw.
- Source-free testbenches (the common case: ports + passives + nonlinear device, no DC source) solve to
  0 V at every net via `gmin` and converge at iteration 0 → `sparam-zero-bias` note → C0/G0 linearization.
  (Confirmed against `NonlinearDcEngine`/`AnalysisSettings`: `ConductanceRegularization` defaults to
  `IfNecessary`, and the DC engine adds `Gmin` whenever the mode isn't `Never`.)

---

## Tests (engine-level; no UI)

Place in the Engine test project alongside the existing S-param / nonlinear-DC tests. Use a **1-port
resistive SDD** (2 nodes, `Kind==Nonlinear`, a single current expression, no charge equation) as the
nonlinear fixture so the seam is exercised before NonlinearC exists. Confirm the SDD construction path you
use matches how SDD instances are elaborated (1-port ⇒ `TerminalNames=["1"]`, `Nodes=[n0,n1]`).

1. **Purely-linear unchanged (regression guard).** A linear-only netlist (R/L/C + Term ports) produces
   byte-identical S-parameters before/after this change, and the DC engine is **not** invoked. Assert via
   a spy/flag or by confirming no `sparam-zero-bias`/`sparam-dc-nonconverged` message is emitted and the
   S-matrix matches a captured baseline.
2. **Resistive SDD becomes visible, linearized at 0 V.** A 1-port SDD with `I = g0·V` (g0 = 1/75 S)
   shunting a 2-port Term–Term line, no DC source ⇒ DC solves to 0 V ⇒ S-parameters equal those of a
   linear 75 Ω resistor in the same spot, at all frequencies. Also assert the `sparam-zero-bias` note is
   emitted.
3. **Bias-dependent linearization.** A 1-port SDD with `I = g0·V + g1·V²` (so G(V)=g0+2·g1·V) plus a DC
   source biasing its node to a known V₀ ⇒ the small-signal conductance seen in S-parameters equals
   g0+2·g1·V₀ (compare to a linear resistor of 1/(g0+2·g1·V₀)). Verifies the operating point actually
   feeds the linearization.
4. **DC non-convergence fallback.** Force a non-converging DC (e.g. `ConductanceRegularization=Never` on a
   floating nonlinear-only sub-net, or `DcBiasStepping=Never` on a stiff case) ⇒ run still completes,
   emits `sparam-dc-nonconverged`, and linearizes at 0 V (no throw out of `Run`).

(NonlinearC's "constant-C == linear-C" regression anchor lives in brief #2, once the model exists.)

---

## Out of scope (later briefs)
- `NonlinearCModel` + `PolynomialFit` (brief #2).
- Symbol/palette/factory (brief #3); CV editor (brief #4).
- Per-frequency caching of `Evaluate(bias)` (design §3.4 optimization) — fine to defer; correctness first.
  If trivial to add here, cache each nonlinear device's `(Dg,Dc,node indices)` once after the DC solve and
  stamp `Dg + jω·Dc` per frequency; otherwise leave `StampLinearized` re-`Evaluate`-ing per frequency.
