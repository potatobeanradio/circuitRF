# Sonnet Brief — WSP-3: probe pairs, network bifurcation, Ohtomo, the reduced matrices for NDF, and the stability envelope

**Read `brief-wsprobe-0-overview.md` first**; depends on WSP-1 (the `wsp` cube) and WSP-2 (the
reduction and utility functions). Reference: T. A. Winslow, *General Circuit Analysis Using The
WSProbe* (2023), §5.1–5.4 (Eq. 137–160), §6 (Eq. 161–176 and its four functions), §7 (Eq. 177–180),
§8 (Eq. 181–186), §9 (Eq. 187–191), Appendix E.5, E.6, E.8, E.9.

**Scope.** Everything the document derives from **two or more** probes. Same home as WSP-2
(`src/RfCore/Stability/`), same registration rules, same naming discipline. The stability envelope
gets a general formulation here that the document's 3×3 special case falls out of; the NDF *function*
over probe matrices is defined here and its passive input comes from WSP-6.

**The trap this brief exists to defuse.** The document's multi-probe algebra is written for a
symmetric world: its matrix equations (Eq. 163–168, 175–176) and the code in §6 return the
**transpose** of the subnetwork's Y or Z for a non-reciprocal network, and its Y-form and Z-form put
the "active" network on **opposite sides** of the probes (overview T-9). Every use the document makes
of those matrices — determinants, principal minors, a diagonal cofactor — is transpose-invariant, so
its results are right; a designer reading `y21` of a block is not. circuitRF returns the true matrices
and says which side they are, and the gates use a non-reciprocal fixture so a lost transpose cannot
hide (§2 and §3 below).

---

## 1. Notation for many probes

`wsp` is stimulus-major: `wsp(2i−1, 2j) = vP_j/vS_i` (series stimulus at `i`, voltage at `j`) and so
on (overview §2). Write the four `N×N` blocks, each indexed `[stimulus i, response j]`:

```
VV[i,j] = wsp(2i−1, 2j)      voltage at j  per series voltage at i        (Eq. 164: A, "odd rows, even cols")
VI[i,j] = wsp(2i−1, 2j−1)    current at j  per series voltage at i        (Eq. 166: C)
IV[i,j] = wsp(2i,   2j)      voltage at j  per shunt current at i         (Eq. 174)
II[i,j] = wsp(2i,   2j−1)    current at j  per shunt current at i         (Eq. 172)
```

The document's §6 code extracts exactly these (`wsp(1::2::N, 2::2::N)` etc.). A probe subset is the
corresponding sub-blocks; every function below takes an optional probe list (the document's Ohtomo
and `wsp_ymatrix` already do) and the whole set by default.

**Sides.** For probe `i` the **G side** is the network at node `vP_i` (port voltage `vP_i`, current
into it `−iS_i` under a series stimulus and `δ_ii·iP − iS_i` under a shunt one) and the **L side** is
the network at `vP_i + vS_i` (current into it `+iS_i`). A subnetwork is well defined only when every
listed probe has it on the same side, which is the document's "all oriented in the same direction
(this is a requirement of the math)" (§6, p. 100).

---

## 2. The probe pair — `wsp_yparam2` and `wsp_block_calc`

### 2.1 `wsp_yparam2(wsp, idx1, idx2)` (Eq. 137–139, §5.1)

Two probes in the GEN → LOAD orientation of Fig. 40: the **inner block `[Y]`** sits between probe 1's
**L** terminal and probe 2's **G** terminal; the **feedback block `[Yf]`** is everything else, between
probe 1's **G** terminal and probe 2's **L** terminal. Re-derived from Kirchhoff on that configuration
(series stimuli only, one at each probe):

```
inner   : ports (1: probe-1 L side, 2: probe-2 G side)
          I_a = y11·V_a + y12·V_b     with V_a = vP1 + vS1,  V_b = vP2,  I_a = +iS1,  I_b = −iS2
feedback: ports (1: probe-1 G side, 2: probe-2 L side)
          I_c = yf11·V_c + yf12·V_d   with V_c = vP1,  V_d = vP2 + vS2,  I_c = −iS1,  I_d = +iS2

A = [[ VV[1,1] + 1,  VV[1,2] ],       B = [[ VV[1,1],  VV[1,2]     ],          (Eq. 138, 139)
     [ VV[2,1],      VV[2,2] ]]            [ VV[2,1],  VV[2,2] + 1 ]]

A · [y11; y12]   = [ VI[1,1];  VI[2,1] ]        A · [y21; y22]   = [ −VI[1,2]; −VI[2,2] ]     (Eq. 137, rows 1–4)
B · [yf11; yf12] = [ −VI[1,1]; −VI[2,1] ]       B · [yf21; yf22] = [  VI[1,2];  VI[2,2] ]     (Eq. 137, rows 5–8)
```

The `+1` lands on `A(1,1)` and on `B(2,2)`, not symmetrically — that asymmetry *is* the orientation
(probe 1's series source is inside the inner block's port-1 voltage; probe 2's is inside the feedback
block's port-2 voltage). It is not a typo. Each unknown pair is a **row** of `[Y]`/`[Yf]`, so this
form returns the true matrices with no transpose. Returns two 2-port cubes `{…, freq, i, j}` (the
document's `YP(1..4)` and `YP(5..8)`, p. 94).

**Consistency diagnostic.** The shunt stimuli give a second, independent set of four equations per
block (`IV`, `II` with the same side bookkeeping). Compute both and report `max|Y_series − Y_shunt|`
relative as `wsp_yparam2_residual`; on an exact linear solve it is at round-off, and a large value
means the two probes do not bracket a two-port with everything else outside (a third connection into
the region between them). The document has no such check; it is cheap and it is the first thing a
designer wants to know when a block's parameters look wrong.

### 2.2 `wsp_block_calc(wsp, idx1, idx2 [, Z0 = 50])` (Eq. 142–151, §5.2–5.4)

Returns the document's list of 16 as a `{…, freq, k}` cube with `k = 1 … 16`, flattened exactly as
the document indexes it (`FET2(1) … FET2(16)`, p. 87, 91, 100):

```
{1..4}   s11, s12, s21, s22   of the inner block at Z0            (Eq. 142)  S = (I − Z0·Y)(I + Z0·Y)⁻¹
{5..8}   sf11, sf12, sf21, sf22 of the feedback block at Z0       (Eq. 143)
{9}      F = 1 − LGa                                              (Eq. 144)
{10}     F = 1 − LGf                                              (Eq. 145)
{11}     F = 1 − LGH                                              (Eq. 146)
{12}     F = 1 − LGM                                              (Eq. 147)
{13}     LGa = [ (yf21+y21)(yf12+y12) − (yf21+y12)(yf12+y12) ] /
               [ (yf11+y11)(yf22+y22) − (yf21+y12)(yf12+y12) ]     (Eq. 148 = Eq. 160, LGB)
{14}     LGf = [ (yf21+y21)(yf12+y12) − (yf21+y21)(yf21+y12) ] /
               [ (yf11+y11)(yf22+y22) − (yf21+y21)(yf21+y12) ]     (Eq. 149, as printed — T-7)
{15}     LGH = (yf21+y21)(yf12+y12) / [ (yf11+y11)(yf22+y22) ]     (Eq. 150 = Eq. 141, Hurst)
{16}     LGM = yf12·y21 / [ (y11+yf11)(y22+yf22) − y12·y21 − yf12·yf21 ]   (Eq. 151 = Eq. 155 RHS — T-8)
```

`LGa`/`{9}` are the synthetic-FET return difference of §5.4: `FB = |Y + Yf| / |Yo + Yf|` (Eq. 159)
with the inner block's controlled source `y21 − y12` zeroed (`y21 → y12`, Fig. 50/51), and
`LGB = 1 − FB` (Eq. 156/160). Implement `{13}` literally as Eq. 148 and assert `1 − {9} == {13}`.

**Winslow's own warning goes in the doc-comment** (p. 89): the Hurst form "is most meaningful when
the probe pair is closest to and capture a single dependent source — such as with a transistor. I
don't recommend using a complex network between the probe pair."

### 2.3 The appendix helpers (E.5, E.6, E.8, E.9)

```
wsp_block_breakout(wsp, idx1, idx2)  = {1..4} of wsp_block_calc as a 2-port cube          (E.5)
wsp_fb_breakout(wsp, idx1, idx2)     = {5..8}                                              (E.6)
wsp_block_design(wsp, idx1, idx2)    = wsp_rc_renorm_s(z_to_pr(ZG₁), z_to_pc(ZG₁), block S,
                                                        z_to_pr(ZL₂), z_to_pc(ZL₂))            (E.8)
wsp_fb_design(wsp, idx1, idx2)       = wsp_rc_renorm_s(z_to_pr(ZL₁), z_to_pc(ZL₁), feedback S,
                                                        z_to_pr(ZG₂), z_to_pc(ZG₂))            (E.9)
```

where `ZG₁` is probe 1's `ZG` (looking away from the inner block into the source side) and `ZL₂`
probe 2's `ZL`; for the feedback block the roles flip (E.9 uses `ZLL` of probe 1 and `ZGG` of probe
2). These are the in-situ renormalised S-parameters — the block "as if it were broken out and
isolated and terminated with your target loadlines" (p. 88). Any RC pair may be replaced by the
user's own (`wsp_rc_renorm_s` is public), which is the design use the document describes.

---

## 3. Network bifurcation — `wsp_YA`, `wsp_YF`, `wsp_ZA`, `wsp_ZF` (§6)

With `N` probes all oriented the same way, the network splits into the G-side subnetwork and the
L-side subnetwork. From the Kirchhoff bookkeeping of §1 (one series stimulus per probe for the Y form,
one shunt stimulus per probe for the Z form), stacking the stimuli as columns:

```
Y-form (series stimuli):
   Y_G · VVᵀ       = −VIᵀ      ⇒  Y_G = −( VV⁻¹ · VI )ᵀ                      (true form of Eq. 168)
   Y_L · (VV + I)ᵀ =  VIᵀ      ⇒  Y_L =  ( (VV + I)⁻¹ · VI )ᵀ                (true form of Eq. 167)
Z-form (shunt stimuli):
   Z_L · IIᵀ       =  IVᵀ      ⇒  Z_L =  ( II⁻¹ · IV )ᵀ                      (true form of Eq. 175)
   Z_G · (I − II)ᵀ =  IVᵀ      ⇒  Z_G =  ( (I − II)⁻¹ · IV )ᵀ                (true form of Eq. 176; T-12/T-13)
```

The document's §6 functions compute the bracketed products **without the transpose** and label them
`YA = Y`, `YF = Yf` (Y-form: active = G side) and `ZA = Z`, `ZF = Zf` (Z-form: active = L side). So:

**R-wsp3-1.** Provide the side-explicit primitive
`wsp_bifurcate(wsp, form = "Y"|"Z", side = "G"|"L" [, probes])` returning the **true** matrix of the
named side, and register the document's four names as aliases with the document's sides:

| Document name | = | side |
|---|---|---|
| `wsp_YA(wsp [, probes])` | `wsp_bifurcate(wsp, "Y", "G")` | G |
| `wsp_YF(wsp [, probes])` | `wsp_bifurcate(wsp, "Y", "L")` | L |
| `wsp_ZA(wsp [, probes])` | `wsp_bifurcate(wsp, "Z", "L")` | **L** |
| `wsp_ZF(wsp [, probes])` | `wsp_bifurcate(wsp, "Z", "G")` | **G** |

Each alias's doc-comment states its side and that the document's `A`/`F` labels do not agree between
the two forms. Returned as `{…, freq, i, j}` cubes with `i`, `j` labelled by probe label in list
order.

**R-wsp3-2. Same side, both forms, must agree:** `wsp_bifurcate(…,"Y","G") == inverse(wsp_bifurcate
(…,"Z","G"))` and likewise for L, at every frequency, on a **non-reciprocal** fixture. This is the
gate that catches both a lost transpose and a swapped side (R-wsp3-9(b)).

The one-probe case reduces to WSP-2: `Y_G` (1×1) `= YG`, `Y_L = YL`, `Z_L = ZL`, `Z_G = ZG`.

---

## 4. Ohtomo's global loop gains — `wsp_loopgain_ohtomo(wsp, probes [, active = "G", Z0 = 50])` (§7)

Bifurcate along the listed probes; the `active` side is `SA`, the other `SP`:

```
S_X = (I − Z0·Y_X)(I + Z0·Y_X)⁻¹      for X ∈ {A, P}, Y_X from §3 (true matrices)
M   = SP · SA − I                                                            (Eq. 178)
G_i = 1 + |M_{N−i+1}| / |M_{N−i}|,   i = 1 … N,   M_0 ≡ 1                    (Eq. 179)
M_{N−i+1} = the trailing principal submatrix of M on rows and columns i … N   (Eq. 180, corrected — T-10)
```

so `G_N = 1 + M_{N,N}`, `G_{N−1} = 1 + det(M[N−1..N, N−1..N]) / M_{N,N}`, … , `G_1 = 1 + det(M)/det(M
[2..N, 2..N])`. Return `{…, freq, node}` with `node` labelled by probe label. Sanity: `N = 1` gives
`G_1 = SP·SA = ΓP·ΓA`, Jackson's stability index.

**Why the bookkeeping cannot be wrong if one identity holds:** telescoping,
`Π_{i=1..N} (G_i − 1) = det(M)`. Assert it at every frequency (R-wsp3-9(d)). Because `det(M) = det(SP·
SA − I)` is the Nyquist determinant of the closed loop of travelling waves, the sum of the encirclements
of `+1` by the `G_i` equals the encirclements of the origin by `det(M)`, which is why Ohtomo's method is
global. The choice of `active` side and the ordering of the probe list change the individual `G_i`
(they are the sequential loop gains with the earlier loops closed) but not that sum — document it, and
gate the sum's invariance.

The oscillation test on a loop gain is `|G_i| ≥ 1` with `∠G_i = 0` crossed **clockwise** (p. 110) —
the positive real axis, decreasing argument, so `Im` going from positive to negative. Provide
`wsp_unstable_freq_loopgain(G)` beside WSP-2's Kurokawa search (same interpolation rules, the critical
point `+1` instead of the origin's negative axis) and use it for `LG`, the circulator loop gains and
`G_i` alike.

**Prerequisite the function cannot check, stated in its doc-comment and the user docs:** Ohtomo's
method assumes each subnetwork is stable on its own (no right-half-plane poles); the document's
recommendation is one side purely active (devices with no terminations that could form a loop) and
the other purely passive (§7, p. 108).

---

## 5. The reduced admittance matrix — `wsp_ymatrix(wsp, probes)` (§8, Eq. 182–185)

The driving-point and transfer impedances among the probe **G nodes** under shunt stimuli, with the
probes closed (they are shorts), form the impedance matrix of the whole network seen at those nodes:

```
Z[j, k] = V_j / I_k = wsp(2k, 2j) = IV[k, j]       ⇒   Z = IVᵀ          (Eq. 184; transpose — D-9)
Y = Z⁻¹                                                                   (Eq. 185)
```

Returned as `{…, freq, i, j}`. This is the matrix NDF wants (Eq. 181 with the reduced Y, or Eq. 186
without the inverse) and the matrix the stability envelope (§6 below) modifies. It contains
**both** sides of every probe — unlike §3's bifurcation — because a shunt injection at a closed probe
excites the whole node.

`wsp_ndf(wsp_active, wsp_passive, probes) = det(Z_passive) / det(Z)` (Eq. 186) takes two `wsp`
cubes, one from an ordinary run and one from a passivated run (WSP-6's analysis knob), and returns the
NDF over frequency. It is the probe-based route to NDF and the cross-check for WSP-6's native one; the
determinant is taken in log form (sum of `log` of LU pivots) so a 30-probe matrix at 1,000 frequencies
neither overflows nor underflows.

---

## 6. The stability envelope — `wsp_terminate`, `wsp_loadpull`, `wsp_loadpull_unstable` (§9)

### 6.1 The general mechanism: a termination change is a rank-1 update of `wsp`

The document's method (§9): read the starting source and load terminations from probes placed at
them, then swap them for pulled ones in the reduced matrix and recompute `H0` at a suspect node —
"instant source and load VSWR stability analysis capability without having to rerun any new
simulations" (p. 120). Its Eq. 187–191 does this through the 3×3 reduced `Y` and a cofactor. The same
physics gives more with less algebra: **adding a shunt admittance `ΔY` at the G node of probe `S`** is
equivalent to injecting `−ΔY·v_S'` there, and every response is already in `wsp`:

```
h = wsp(2S, 2S) = H0_S
for every row r (any stimulus) and column c (any response):
    wsp'(r, c) = wsp(r, c) − wsp(r, 2S) · ΔY · wsp(2S, c) / (1 + ΔY · h)         (Sherman–Morrison)
```

Applied once for the source probe (`ΔY_S = YS − YSo`) and once, on the result, for the load probe
(`ΔY_L = YL − YLo`), this yields the **complete `wsp` of the re-terminated circuit** — `H0'` and `Y0'`
at every probe, `ZG'`, `ZL'`, every loop gain, Ohtomo, all of it — with no cofactor bookkeeping and
no assumption about which node is "suspect". `H03'` of Eq. 191 is the `(2·3, 2·3)` entry of the result
(R-wsp3-9(e) asserts it).

```
wsp_terminate(wsp, idxS, YS, idxL, YL [, YSo, YLo])   →  the re-terminated wsp cube
```

`YSo`/`YLo` default to `1/ZG` of probe `idxS` and `1/ZL` of probe `idxL` — the starting terminations
as the probes see them (§9: "either known or determined using the bidirectional impedance calculations
provided directly from the source and load WSProbes"). **Precondition, checked:** the source probe must
sit directly at the termination with `G` facing it, so that the termination is a pure shunt at its G
node and `ZG` equals the `Term`'s declared `Z` — the engine records each probe's neighbouring `Term`
(if any) in `__WspProbes`, and `wsp_terminate` refuses (`wsprobe.envelope-probe-not-at-termination`)
when `|ZG − Z_term| > 1e-6·|Z_term|` at any frequency, naming the probe. Likewise the load probe with
`L` facing its `Term`. A probe with feedback across it (§9, p. 119: "feedback across the WSProbe
terminals can make the calculation of the source and load looking bidirectional impedances take on
values that strongly depend on the feedback") cannot isolate a termination and fails that check, which
is the intended outcome.

### 6.2 `wsp_loadpull(wsp, idxS, idxL, idx, gammaS, gammaL [, Z0 = 50])`

`gammaS`, `gammaL` are grids of reflection coefficients (a constant-|Γ| circle is the common case:
`|Γ|·e^{jθ}`, `θ` stepped). For each pair: `YS = 1/ZS`, `ZS = Z0·(1 + ΓS)/(1 − ΓS)`, same for the
load; `wsp_terminate`; read `H0'` and `Y0'` at probe `idx`. Returns two cubes `{…, gS, gL, freq}`
(`H0env`, `Y0env`) with the Γ grids as axes (values carried as labels so the Data Display can lay them
on a polar chart per `θ`). Both immittances, because §4.10's pole masking applies under mismatch as
much as at nominal (the 2025 envelope paper's abstract names both).

### 6.3 `wsp_loadpull_unstable(...)`

Same arguments; runs WSP-2's Kurokawa search on `1/H0'` and `1/Y0'` at every grid point and returns
the set of `(ΓS, ΓL, frequency, which)` that meet the start-up conditions — a `{gS, gL}` Real cube
`unstable` (count of unstable frequencies) plus a list cube of the frequencies. This is the
"stability envelope": the terminations a circuit can be presented with before an internal node shows
Kurokawa's signature. The envelope over `|Γ|` at fixed `θ` steps is what WSP-4 draws.

> **Added 2026-09-08, after this brief was built.** T. A. Winslow's 2025 EuMIC paper ([E] in the
> overview §1.2) reaches the same re-terminated `H0'`/`Y0'`/`ZG'`/`ZL'`/`YG'`/`YL'` by an explicit
> 4-port reduction (E-Eq. 1–12) rather than a rank-1 update. **WSP-9 implements that reduction in the
> test project only, as an independent oracle for `wsp_terminate`** (its gate (f)), and adds the
> stability margin and an NDF over this envelope (`wsp_loadpull_margin`, `wsp_loadpull_ndf`) — the
> paper's own comparison, with no re-simulation. E-Eq. 11 as printed has its G/L numerators swapped
> (T-16); nothing here depends on it.

---

## 7. Gates

`tests/RfCore.Tests/Stability/WspGlobalTests.cs` for the algebra on synthetic `wsp` matrices built from
known subnetworks; `tests/Engine.Tests/Linear/WSProbeGlobalTests.cs` for the end-to-end lines.

**R-wsp3-9(a) — pair reduction on a constructed two-block circuit.** Build a `.cnl` whose inner
block is a `VCCS` (`G = gm`) with shunt `R`/`C` at each port (so `y21 = gm ≠ y12 = 0`: non-reciprocal
and known) and whose feedback block is a series `R`–`C` between the outer nodes plus the terminations;
probes at both block ports, GEN → LOAD. `wsp_yparam2` returns `[Y]` and `[Yf]` equal to the
hand-written matrices to 1e-10; `y21 = +gm` sits at `(2,1)`, not `(1,2)` (a transpose would move it);
`wsp_yparam2_residual < 1e-10`. Then add a third connection into the inner region (a resistor from an
inner node to ground) and assert the residual is large (`> 1e-3`).

**(b) — bifurcation sides and transposes.** Same fixture with the VCCS on the G side of both probes:
`wsp_bifurcate("Y","G")[2,1] = gm` and `[1,2] = 0`; `wsp_bifurcate("Y","G") == inverse(wsp_bifurcate
("Z","G"))` and the L pair likewise, to 1e-10; the document's alias names resolve to the sides the
table in §3 states.

**(c) — `wsp_ymatrix` against the engine.** Its `Y` at the probe nodes equals the admittance matrix
obtained by running an S-parameter analysis with `Term`s (Z = 1e9 Ω, so effectively open) at those
same nodes and converting `S → Y` — after subtracting the 1e-9 S the terms add — to 1e-8; on the
non-reciprocal fixture, so the `IVᵀ` transpose is exercised.

**(d) — Ohtomo.** `Π(G_i − 1) = det(M)` at every frequency on (b)'s fixture with 3 probes; `N = 1`
gives `ΓP·ΓA`; reversing the probe list changes individual `G_i` but not `Σ enc(G_i − 1)`; on WSP-1's
series resonator with the loop split at the probe (G side = `R1 + L1 + C1`, L side = `RS`),
`wsp_unstable_freq_loopgain(G_1)` reports 1.5915 GHz for `R1 = −20 Ω` and nothing for `R1 = +20 Ω`.

**(e) — the envelope against brute force.** On a two-stage amplifier fixture (WSP-7's example, or
Hero 1 with `Term`s at 50 Ω): pick `ΓS = 0.5∠60°`, `ΓL = 0.3∠−120°`; compute `wsp_terminate` from the
50 Ω run, and separately **re-run the S-parameter analysis with the `Term` impedances changed** to
`ZS`, `ZL`; the two `wsp` cubes agree to 1e-9 relative in every entry. Then assert `H03'` from
Eq. 191's cofactor form (the corrected 3×3, T-11) equals `wsp'(2·3, 2·3)` to 1e-10. Then move the
source probe one series element away from its `Term` and assert the refusal.

**(f) — `wsp_loadpull_unstable`.** On the series resonator fixture with `RS` replaced by a `Term` at
`ZSo = 10 Ω` and `R1 = −5 Ω` (stable at 10 Ω, since `R1 + RS > 0`): a load-pull over `|Γ| = 0.8` finds
the arc of `θ` for which `Re(ZS(θ)) < 5 Ω` unstable and the rest stable, matching the closed form of
Eq. 109 with `RS → ZS(θ)`; the reported frequency tracks `1/(2π√(L1C1))` within a step.

**(g) — `wsp_block_calc` self-consistency.** `1 − {9} == {13}` … `1 − {12} == {16}`; `{1..4}` equals
`S` of `[Y]` from (a) at `Z0`; in the no-reverse-transfer limit (`y12 = yf21 = 0`) `LGM == LGH`.

**(h) — helpers.** `wsp_block_breakout` = `{1..4}`; `wsp_block_design` with the probes' own RC models
equals `wsp_zo_renorm_s` with `ZG₁`, `ZL₂` to 1e-12 when both are purely R‖C at that frequency.

## On completion

Findings to `src/RfCore/RESOLVED.md` — in particular the transpose finding of §3, which future readers
of the document will otherwise rediscover; **never a `CLAUDE.md`**. Append §6 "Multi-probe functions",
§7 "Bifurcation and sides", §8 "Envelope by rank-1 update" to `docs/design/stability-wsprobe.md`.
