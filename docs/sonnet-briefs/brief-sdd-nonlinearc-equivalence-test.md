# Brief #1 (SDD weighting): NonlinearC ≡ 1-port SDD `I[1,1]` equivalence test

Design ref: `docs/design/sdd.md` §5. **No production-code changes** — this test validates the *existing*
charge/`w=1` path before the weighting-function generalization is built on top of it. It proves that a
nonlinear capacitor written as an SDD `I[1,1]=Q(V)` (the `H[1]=jω` charge weighting) produces the same result
as the dedicated `NonlinearC` device, in both the S-parameter (small-signal) and HB (large-signal) engines.
It's the regression anchor for the whole SDD-weighting feature.

If anything here fails, it's a real bug in the current `I[p,1]`→charge path (factory bind, `StampLinearized`,
or HB `jω·Q` assembly) — surface it, don't paper over it.

Build **0W/0E**; tests green. One new file:
`tests/Engine.Tests/HarmonicBalance/SddNonlinearCEquivalenceTests.cs` (both facts; namespace
`CircuitRF.Engine.Tests.HarmonicBalance`).

---

## The shared device — a quadratic nonlinear capacitor

A clean, exact (no fit residual) non-trivial C(V), so both devices get identical coefficients:

```
C(V) = 10e-12 − 1.5e-12·V + 0.1e-12·V²      (F, V in volts)
Q(V) = ∫₀ⱽ C dv = 10e-12·V − 0.75e-12·V² + (0.1e-12/3)·V³
```

- **NonlinearC** form: `NonlinearC:C1  n1 0  C0=10e-12 C1=-1.5e-12 C2=0.1e-12`
  (its `Evaluate` returns `Q` in the charge slot and `C(V)` in `dc` — brief #2.)
- **SDD** form (1 port = 2 nets; `I[1,1]` = charge, weight 1): write `Q(_v1)` with explicit multiplication to
  avoid any `^`-operator assumption:
  ```
  SDD:X1  n1 0  I[1,1]=10e-12*_v1 - 0.75e-12*_v1*_v1 + (0.1e-12/3)*_v1*_v1*_v1
  ```
  (`I[1,1]` binds to `chargeAst[0]`; `dQ/d_v1 = C(V)` falls out of the dual-AD — no `dg`, all `dc`.)

Both are pure-charge 1-ports → the engines must produce identical results. Put the coefficient literals in
**both** strings from the same `const` fragments if convenient, so they can't drift.

---

## Fact 1 — S-parameter equivalence (small-signal, no HB)

Mirror `tests/Engine.Tests/Linear/NonlinearCSParamTests.cs` exactly (its `Run` helper:
`CnlReader().Read` → `Elaborator(lib).Elaborate(tb)` → `SParameterEngine.Run(nl, freqs)`; read
`S(ds,r,c,fi) = (Complex)ds["S"][fi,r,c]`).

Two 1-port testbenches, shunt device to ground, no DC source (auto-bias → 0 V):
```
Port:P1  n1 0  Num=1  Z=50 Ohm
NonlinearC:C1  n1 0  C0=10e-12 C1=-1.5e-12 C2=0.1e-12
```
vs
```
Port:P1  n1 0  Num=1  Z=50 Ohm
SDD:X1   n1 0  I[1,1]=10e-12*_v1 - 0.75e-12*_v1*_v1 + (0.1e-12/3)*_v1*_v1*_v1
```

Run both over `double[] freqs = [1e9, 2e9, 5e9, 10e9]`. **Assert `S11` matches between the two at every
frequency**, `(sSdd - sNlc).Magnitude < 1e-9`. (At 0 bias both linearize to `jω·C(0)=jω·10e-12`, so this is
near-exact — it confirms the SDD flows through the same auto-DC-bias → `StampLinearized` seam NonlinearC uses,
and that the `w=1` charge derivative `dc` is stamped as `jω·C`.) Optionally also assert both equal a linear
`C:C1 n1 0 C=10e-12` reference as a third leg.

(Note: 0-bias S-param only exercises `C0`. The C1/C2 curvature is exercised by Fact 2. If you want it in the
linear engine too, add an optional biased variant: feed `n1` from `Vdc:VB nb 0 Vdc=2` through `R:Rf n1 nb
R=1e6` — the cap blocks DC so `n1` sits at 2 V, and both devices then linearize to `jω·C(2 V)=jω·7.4e-12`.
Optional; Fact 2 is the primary nonlinear check.)

---

## Fact 2 — HB equivalence (large-signal charge nonlinearity)

Mirror `tests/Engine.Tests/HarmonicBalance/SddSingleIndexHbTests.cs` for the harness (P1Tone drive,
`analysis ... type=hb`, run, read `ds["V"]`, node-label lookup, harmonic axis). A **single HB point** is enough
— use a 1-value parametric sweep as that test does, or the `NoSweepHbTests.cs` single-point pattern, whichever
matches the existing style.

Drive the cap with a tone large enough to swing across the C(V) curve so harmonics are clearly above the noise
floor. Two testbenches, identical except the device:
```
P1Tone:P1  n1 0  Pavl=15 dBm  Z=50 Ohm  Freq=1e9  Phase=0 deg
NonlinearC:C1  n1 0  C0=10e-12 C1=-1.5e-12 C2=0.1e-12
analysis HB1 type=hb Tone=1e9 MaxHarm=5 Tol=1e-6
```
vs the same with
```
SDD:X1  n1 0  I[1,1]=10e-12*_v1 - 0.75e-12*_v1*_v1 + (0.1e-12/3)*_v1*_v1*_v1
```
(1 GHz chosen so the cap impedance is high enough to develop voltage across it; bump `Pavl` if the harmonics
come out too small. The equivalence holds at any level — pick one that makes 2nd/3rd harmonics clearly
non-trivial.)

Read `V` at node `n1` for both. **Assert, per harmonic `k = 0…MaxHarm`,
`|V_sdd[n1,k] − V_nlc[n1,k]| < tol`** with `tol = max(1e-9, 1e-6·|V_nlc[n1,k]|)` (loosen only if FP-ordering
between NonlinearC's coefficient formula and the SDD's AST eval needs it — the physics is identical, so it
should be tight). **Plus a sanity assertion that the test actually exercises nonlinearity**: at least the 2nd
and 3rd harmonics `|V_nlc[n1,2]|`, `|V_nlc[n1,3]|` are above ~`1e-7` (so a degenerate all-zero match can't pass
silently). Optionally compare the `I` cube too (the port-current spectra) the same way.

Use `ITestOutputHelper` to print the per-harmonic magnitudes for both devices (handy when tuning the drive).

---

## Gate
Build 0W/0E; both facts green. This locks the charge/`w=1` foundation: the next brief (the `NonlinearResult`
+ engine generalization for arbitrary `H[w]`) must keep these two passing byte-for-byte, since `w=0/1` stays
the fast path.
