# Nonlinear components in the linear simulators

Status: design (approved decisions baked in). Owner: design notes for the NonlinearC feature and the
general "nonlinear device in a small-signal engine" mechanism. Companion to `nonlinear-dc.md`,
`harmonic-balance.md`. Implementation lands via Sonnet briefs after this doc.

## 1. Purpose & scope

circuitRF's nonlinear engines (NonlinearDC, HB) already consume a device's full nonlinear description.
The *linear* small-signal engines (S-parameter today; linear-AC/noise later) do not — a nonlinear device
currently contributes nothing to their MNA and is electrically invisible. This doc defines:

1. How a nonlinear device produces a correct **small-signal linear stamp** for the linear engines, by
   linearizing around a DC operating point.
2. How the **DC operating point** is obtained automatically (run `NonlinearDcEngine` before the
   small-signal sweep) so the linearization is physically correct, not zero-bias.
3. The first concrete nonlinear device exercising the charge path: **NonlinearC**, a 1-D polynomial
   nonlinear capacitor.
4. How the same mechanism generalizes to **SDD** (nonlinear R and/or C) and any future nonlinear model.

Non-goals: large-signal accuracy in the linear engines (that's HB's job); multi-tone small-signal;
noise. Those reuse the same linearization seam when they arrive.

## 2. Background — the existing nonlinear contract

`ComponentModel` (src/Core/ComponentModel.cs) partitions every device as exactly one of
`ModelKind.Linear` or `ModelKind.Nonlinear` and exposes two contribution paths:

- `Stamp(IMnaContext, ElaboratedComponent, omega)` — the *linear* contribution (admittance/branch
  stamps). Linear devices implement it; nonlinear devices currently no-op it (`SddModel.Stamp` is `{ }`).
- `Evaluate(in PortVoltages) → NonlinearResult{ I, Q, Dg, Dc }` — the *nonlinear* contribution:
  per-port current `I`, per-port **charge `Q`**, and the Jacobians `Dg = ∂I/∂V` and **`Dc = ∂Q/∂V`**.

Port-voltage convention (established by `NonlinearDcEngine`): ports are consecutive node pairs in
`ec.Nodes` — port `p` spans `nodes[2p]` (+) and `nodes[2p+1]` (−), and `v[p] = V(nodes[2p]) −
V(nodes[2p+1])`. A 2-terminal device is a single port (`nodes[0]`,`nodes[1]`).

Key observation: the charge path (`Q`, `Dc`) is fully plumbed through the contract and through HB, but has
never been exercised by a real device — SDD authors have not written charge equations, and no built-in
device fills `Q`/`Dc`. NonlinearC will be the first.

Who calls what today:
- **NonlinearDC** (`NonlinearDcEngine`): builds the constant linear part from `Kind==Linear` devices via
  `Stamp(ω=0)`; for each `Kind==Nonlinear` device it computes port voltages, calls `Evaluate`, and stamps
  `I` into the residual and `Dg` into the Jacobian. `Q`/`Dc` are dropped (jω=0 at DC).
- **HB**: linear partition from `Kind==Linear` stamps; nonlinear partition from `Evaluate` (uses all of
  `I,Q,Dg,Dc`).
- **S-parameter** (`SParameterEngine`): stamps *every* non-port component via `Stamp(mna, ec, ω)`
  regardless of `Kind`. Because nonlinear `Stamp` is a no-op, nonlinear devices vanish. **This is the gap.**

## 3. The design — small-signal linearization at the DC operating point

A nonlinear device's small-signal admittance at a bias operating point V₀ is, per port pair (p,q):

  Y[p,q](ω) = Dg[p,q](V₀) + jω · Dc[p,q](V₀)

i.e. exactly the Jacobians the device already returns from `Evaluate(V₀)`. For a pure capacitor Dg=0 and
Y = jω·C(V₀); for a resistive SDD Dc=0 and Y = G(V₀); a device with both contributes both. So the linear
engines need only: (a) the bias V₀ at each device's terminals, and (b) a routine that evaluates the
device there and stamps the Y block.

### 3.1 The linearized-stamp seam

Add one virtual to `ComponentModel`:

```csharp
/// <summary>
/// Small-signal linear contribution of a nonlinear device, linearized at the bias operating
/// point. Stamps Y[p,q] = Dg[p,q] + jω·Dc[p,q] (from Evaluate(bias)) as an N-port admittance
/// block. Linear engines call this for Kind==Nonlinear devices instead of Stamp().
/// </summary>
public virtual void StampLinearized(IMnaContext mna, ElaboratedComponent c, double omega, in PortVoltages bias)
{
    var r = Evaluate(bias);
    int P = PortCount;
    for (int p = 0; p < P; p++)
    {
        int np = c.Nodes.Length > 2*p   ? c.Nodes[2*p]   : 0;
        int nm = c.Nodes.Length > 2*p+1 ? c.Nodes[2*p+1] : 0;
        for (int q = 0; q < P; q++)
        {
            int qp = c.Nodes.Length > 2*q   ? c.Nodes[2*q]   : 0;
            int qm = c.Nodes.Length > 2*q+1 ? c.Nodes[2*q+1] : 0;
            var y = new Complex(r.Dg[p,q], omega * r.Dc[p,q]);
            if (y == Complex.Zero) continue;
            mna.AddBlockAdmittance(np, qp,  y);
            mna.AddBlockAdmittance(np, qm, -y);
            mna.AddBlockAdmittance(nm, qp, -y);
            mna.AddBlockAdmittance(nm, qm,  y);
        }
    }
}
```

This default lives on the base class and works for *every* nonlinear device — NonlinearC and SDD do not
override it. `AddBlockAdmittance` already exists on `IMnaContext` and drops node-0 entries, so ground-
referenced ports are handled. For a 2-terminal cap this reduces to `AddAdmittance(n0, n1, jω·C(V₀))` —
identical in form to the linear capacitor stamp, with C evaluated at bias.

### 3.2 The operating point — automatic DC bias (Decision 2)

Per the approved decision, the bias is **not** zero and **not** a per-instance parameter: it is the true
DC operating point of the whole circuit. Before the frequency sweep, the linear engine runs
`NonlinearDcEngine.Run(netlist, settings)` once and uses `DcResult.NodeVoltages` to bias every nonlinear
device. The user expects S-parameters of, say, a varactor to reflect its actual DC bias — this delivers
that.

`DcResult.NodeVoltages` is indexed `[node−1]` for circuit node `node` (node 0 = ground = 0 V). The engine
forms each device's bias `PortVoltages` with the same node-pair convention used inside the DC engine:
`bias[p] = NodeV(nodes[2p]) − NodeV(nodes[2p+1])`.

**Source-free / no-DC-bias circuits (the common S-param case).** A typical S-parameter testbench has only
ports (inert at DC — the DC engine skips Port/Term) plus passives and the nonlinear device(s), i.e. no
independent DC source. This does **not** fail. With the default `ConductanceRegularization = IfNecessary`,
the DC engine adds `Gmin` (1e-12 S) to every node, so the source-free system is non-singular and the
cold-start `x = 0` is already the exact solution (caps are opens at DC, nonlinear-cap `I(0)=0`) — the
solve converges at iteration 0 to **0 V at every net**, and each NonlinearC linearizes at C(0)=C0. (If the
user has explicitly set `ConductanceRegularization = Never`, the source-free matrix is singular and the DC
solve reports non-convergence; the §3.5 fallback then also yields a 0 V bias.) When the resolved operating
point is all-zero, the engine posts an **informational** Messages entry — e.g. "No DC bias present;
nonlinear components linearized at 0 V" — so the user understands the linearization point. This is
informational, not a warning or error: zero-bias small-signal characterization is a legitimate, common
request.

### 3.3 S-parameter integration flow

`SParameterEngine.Run` gains a pre-pass and a per-component branch:

1. **Detect** nonlinear devices: `bool hasNonlinear = netlist.Components.Any(c => c.Model.Kind ==
   ModelKind.Nonlinear);`
2. If `hasNonlinear`: run `var dc = NonlinearDcEngine.Run(netlist, settings);`. Build a `double NodeV(int
   node1based)` over `dc.NodeVoltages`. If `!dc.Converged`, see §3.5.
   If `!hasNonlinear`: skip the DC solve entirely — **zero behavior change for purely linear circuits.**
3. In `StampAll`, the existing loop already calls `ec.Model.Stamp(mna, ec, omega)` for non-port, non-
   mutual components. Split it: for `Kind==Nonlinear` devices call `StampLinearized(mna, ec, omega,
   bias[ec])` instead; linear devices keep calling `Stamp`. Ports (Port/Term/P1Tone) and mutuals are
   unaffected. (Nonlinear devices are never ports, so the buried/skip logic for ports doesn't touch them.)
4. The wave-path/legacy-path choice is unchanged — both call `StampAll`, so both get the linearized
   nonlinear stamps automatically.

**Rule: the DC engine is never invoked for a purely linear S-parameter run.** When the netlist contains no
`Kind==Nonlinear` device, `SParameterEngine` does not construct or call `NonlinearDcEngine` at all — no
operating-point solve, no Gmin pre-pass, no behavior change. Purely-linear S-parameter results stay
byte-identical to today's. The DC solve is gated strictly on the presence of at least one nonlinear device.

Note the partition stays clean: HB and DC select nonlinear devices by `Kind` and use `Evaluate`; they
**never** call `StampLinearized` or the nonlinear `Stamp`, so there's no double-count. Only the linear-
only engines call `StampLinearized`.

### 3.4 Frequency-sweep efficiency

The bias is frequency-independent, so `Evaluate(bias)` (hence `Dg`, `Dc`) is the same at every frequency
point. Recommended: evaluate each nonlinear device once at the DC bias, cache its `Dg`/`Dc` (and node
indices), and at each frequency stamp `Dg + jω·Dc` from the cache. v1 may simply call `StampLinearized`
(which re-`Evaluate`s) per frequency for simplicity; the cache is a drop-in optimization and worth doing
if the sweep is large or `Evaluate` is expensive (SDD AST eval). Either way the DC solve runs exactly
once per S-parameter run.

### 3.5 Zero-bias and DC non-convergence

Two distinct situations, both non-fatal:

- **Zero bias (normal).** No DC source, or the operating point genuinely solves to ~0 V (see §3.2). This
  is a converged result; nonlinear devices linearize at 0 V (C0, etc.). The engine posts an informational
  Messages entry noting the 0 V operating point. Not a warning.
- **Non-convergence (degenerate).** The circuit has nonlinear devices and the DC solve fails to converge
  (e.g. `ConductanceRegularization = Never` on a floating circuit, or a genuinely hard bias point that
  exhausts ramping/halving). The linearization point is undefined. Policy (v1, approved): **emit a
  prominent `netlist.AddWarningOnce` and fall back to zero-bias linearization** (V₀ = 0 → C0, G(0)), so the
  run still produces a clearly-flagged result rather than silently-wrong S-parameters or a hard crash. This
  matches circuitRF's research-tool "warn and continue" philosophy. (A strict hard-`throw` mode can be
  added behind a setting later if validation use cases want it.)

## 4. NonlinearC — the 1-D polynomial nonlinear capacitor

### 4.1 Model

`NonlinearCModel : ComponentModel`, `PortCount = 1` (a single differential terminal pair = 2 nets, per the
`Nodes[2p],Nodes[2p+1]` convention — a p-port nonlinear device uses 2p nets), `Kind = Nonlinear`.
Capacitance is a function of its own terminal voltage only (1-D):

  C(V) = Σ_{k=0}^{n} Cₖ · Vᵏ           (small-signal capacitance, F)
  Q(V) = ∫₀ⱽ C(u) du = Σ_{k=0}^{n} Cₖ · V^(k+1)/(k+1)     (charge, with Q(0)=0)

`Evaluate(v)` with Vd = v[0] (the single port voltage = V(n+) − V(n−)):
- I  = [0]        (no conduction)
- Q  = [Q(Vd)]
- Dg = [[0]]
- Dc = [[C(Vd)]]

The per-terminal +/− current/charge distribution is performed by the engine's 4-corner stamp (the DC
engine's nonlinear loop and `StampLinearized`), not by the device — so the device returns single-port
(1×1) Jacobians. Evaluate via Horner for both C and Q.

In NonlinearDC: I=0, Dg=0 → contributes nothing (a capacitor is an open at DC) — correct. In HB: `Q`/`Dc`
drive the charge balance — this is the path being exercised for the first time. In S-param: linearized to
jω·C(Vd_bias) via §3.1.

### 4.2 Parameters & coefficient computation (Decision 1)

Two user entry methods; **coefficients are always the canonical, stored simulation input.**

- **Method 1 (direct):** the user sets `C0, C1, …, Cn` as instance parameters (Real). The model reads
  consecutive `Ck` keys starting at `C0`, stopping at the first absent index (absent ⇒ 0), so the order is
  arbitrary. `C0` defaults to 0 if entirely unset (degenerate zero-cap, warn-once optional).
- **Method 2 (CV data, editor-only):** the user enters bias/capacitance arrays and a fit order in a
  schematic-editor CV editor. On **Apply**, the editor fits `C(V)` to a polynomial of the requested order
  (least-squares, highest-power-last ordering — note NumPy's `polyfit` returns highest-power-first) and
  **writes the resulting `C0…Cn` into the instance parameters**. On **Close**, nothing is computed or
  applied. The raw CV arrays + order **persist in the `.csch`** (see §4.4) so a later session can reopen,
  edit, and re-Apply. The user is responsible for re-clicking Apply after changing CV data — coefficients
  are not auto-recomputed.

The fit routine lives in **Core** (`PolynomialFit.Fit(double[] v, double[] c, int order) → double[]`,
Vandermonde least-squares via NumFlat or normal equations), called by the editor. Core-located keeps it
unit-testable and reusable; UI→Core is within the firewall. The engine never fits — it only ever reads
coefficients. (Rationale: `Value` is scalar-only — `{Real,Complex,Bool,String,Cube,All}` — so CV arrays
are not naturally storable as instance params anyway; coefficients are plain Real params that drop into
the existing system.)

Optional linearization note: because the bias now comes from the auto DC solve (§3.2), NonlinearC needs
**no** `Vbias` parameter.

### 4.3 Symbol & palette

The NonlinearC symbol = the linear capacitor symbol **plus** the three short diagonal slashes drawn
through it that conventionally denote a nonlinear/variable element (the same convention used for varactors
/ nonlinear parts). Add a palette entry in the standard library next to C. Symbol geometry mirrors the
linear C primitive with the three added strokes; reuse the C body and pin layout so it drops into the
existing symbol/render pipeline (`SymbolKind` gains a `NonlinearC` member; renderer + palette glyph +
`ComponentModelFactory` mapping follow the existing capacitor wiring).

### 4.4 Persistence

Coefficients persist as normal instance parameters in the `.csch`. The Method-2 CV arrays + fit order
persist as additional, engine-ignored fields on the instance (e.g. a `String`-encoded `cv_data` param, or
a small dedicated record on the schematic instance — pick whichever the `.csch` instance schema supports
most cleanly; a `String` param is the least-invasive). Alpha no-migration rule: new defaulted/nullable
fields require no format bump.

## 5. How SDD slots in (generalization)

SDD is already `Kind=Nonlinear` with `Evaluate` returning `I,Q,Dg,Dc` from user expressions (current AST
+ charge AST). With the §3 seam, SDD automatically gains correct small-signal behavior in S-parameter:
its `StampLinearized` (the inherited default) stamps `Dg + jω·Dc` at the DC bias — no SDD-specific code.
A resistive SDD contributes `G(V₀)`; an SDD with charge equations contributes `jω·C(V₀)`; both, both. This
is the second motivation the owner named, and it falls out of the same mechanism for free. (SDD's existing
no-op `Stamp` stays a no-op — linear engines call `StampLinearized`, not `Stamp`, for nonlinear devices.)

## 6. Engine touch-points summary

- `src/Core/ComponentModel.cs`: add `StampLinearized(... in PortVoltages bias)` default.
- `src/Core/Devices/NonlinearCModel.cs` (new): the model above.
- `src/Core/Devices/ComponentModelFactory.cs`: map the new component type.
- `src/Core/Expressions/PolynomialFit.cs` (new): the CV fit (called by the editor).
- `src/Engine/SParameterEngine.cs`: run DC once when nonlinear devices present; in `StampAll`, route
  `Kind==Nonlinear` devices to `StampLinearized(bias)`; build bias from `DcResult.NodeVoltages`.
- UI: `SymbolKind.NonlinearC`, symbol primitive (C + three slashes), palette entry, CV editor mode
  (Apply/Close semantics), `.csch` CV persistence.

## 7. Open items / future

- HB could reuse `NonlinearDcEngine` for its initial DC guess if it doesn't already — out of scope here.
- Multi-port bias convention assumes consecutive node pairs (matches `NonlinearDcEngine`); revisit if/when
  a nonlinear device needs non-pair port topology.
- Linear-AC and noise engines reuse §3 unchanged.
- DC-failure policy (§3.5): resolved — warn-and-fallback-to-zero-bias (a strict hard-throw mode may be
  added behind a setting later).

## 8. Testing

- `PolynomialFit`: recovers known coefficients from sampled C(V); matches the numpy `polyfit(...)[::-1]`
  reference within tolerance on the example varactor CV (v=[0..5], c=[10p..1.8p], order 3).
- NonlinearC `Evaluate`: C(Vd) and Q(Vd) match the analytic polynomial/integral at several Vd; Dc symmetry
  (`Dc[0,0]=Dc[1,1]=−Dc[0,1]=−Dc[1,0]=C(Vd)`); I=0, Dg=0.
- S-param linearization: a NonlinearC with constant C (C1..=0) gives S-parameters identical to a linear C
  of the same value at all frequencies (regression anchor). A bias-dependent NonlinearC across two DC bias
  conditions yields the two expected C(V₀) values (drive the bias with a DC source + verify the small-
  signal C matches C(V₀_solved)). Purely-linear netlists are byte-identical before/after (no DC pre-pass).
- SDD in S-param: a resistive SDD linearizes to G(V₀); a charge SDD to jω·C(V₀).
