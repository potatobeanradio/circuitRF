# Sonnet Brief — WSP-2: the single-probe derived metrics (the reference document's function library)

**Read `brief-wsprobe-0-overview.md` first**; depends on WSP-1's `wsp` cube and `WspReduction.cs`.
Reference: T. A. Winslow, *General Circuit Analysis Using The WSProbe* (2023), §4.2–4.9 and Appendix
C, D, E.1–E.4, E.7, E.10–E.16.

**Scope.** Every quantity the document derives from **one** probe's 2×2 block of `wsp`, plus the
small utility functions of its appendix, implemented once as pure functions in
`src/RfCore/Stability/` and exposed (a) to `measure` lines through the expression engine and (b) to
the Data Display through WSP-4. The owner's instruction: these are **derived metrics**, never
"scripts"; they keep the document's names and argument order exactly, so a designer who knows the
document can type `wsp_yparam(SP1.wsp, SP1.idx("GATE"))` and get Eq. 44.

Probe *pairs* and multi-probe functions (`wsp_yparam2`, `wsp_block_calc`, bifurcation, Ohtomo,
`wsp_ymatrix`, the envelope) are WSP-3.

---

## 1. Where they live and how they are called

**R-wsp2-1.** Files under `src/RfCore/Stability/` (framework-free, `RfCore` namespace):

| File | Contents |
|---|---|
| `WspReduction.cs` | (WSP-1) `wsp_yparam`, `wsp_zparam`, the six defaults |
| `WspNodal.cs` | bidirectional immittances, open-port immittances, all eight `wsp_loopgain` kinds, `wsp_nodal_gamma`, the normalised driving-point loci, the margin slot |
| `WspKurokawa.cs` | `wsp_unstable_freq_kurokawa`, `encirculations`/`enc` |
| `WspTransfer.cs` | `wsp_impedance`, `wsp_gain` (App. C, D) |
| `ImmittanceModels.cs` | `z_to_pr/pc/pl/sr/sc/sl`, `y_to_pr/pc/pl/sr/sc/sl`, `wsp_zsrc`, `wsp_zprc` |
| `GainDefinitions.cs` | `GainDEFs`, `_dB` |
| `WspRenorm.cs` | `wsp_rc_renorm_s`, `wsp_zo_renorm_s` |

Each public function: one doc-comment line citing the equation and appendix section; scalar
(per-frequency) core taking `Complex` values or a `Complex[,]`, and a cube-facing overload that maps
over the frequency axis (and any prepended sweep axes) of a `wsp` cube. **The scalar core is the
tested unit**; the cube overload is a `map`.

**R-wsp2-2. Expression-engine registration.** Each function is a built-in of `Evaluator` (the
`case "…"` table beside `conj`, `mag`, `dB`) with the document's exact name and arity. Arguments:

- `wsp` — a `{…, freq, row, col}` cube (`SP1.wsp`, or `HB1.wsp` under WSP-5);
- `idx` — a Real scalar (`SP1.idx("GATE")`) or an integer literal;
- a 2×2 network — a `{…, freq, i, j}` cube with `i`, `j` ∈ {1, 2} (what `wsp_yparam` returns, and the
  same shape the `S` cube already has, so `stoy`-style helpers and the Data Display's network-parameter
  paths accept it);
- a scalar-per-frequency quantity — a `{…, freq}` cube.

Return shapes are the natural ones: `wsp_yparam` → `{…, freq, i, j}`; `wsp_ZG` → `{…, freq}`;
`wsp_unstable_freq_kurokawa` → `{n}` (a list of frequencies, possibly empty) or, with sweep axes,
refused with the document's own note (App. E.12: "does not work with multi-index swept data") naming
`at(...)` as the way to pin the sweep first.

**R-wsp2-3. Names are the document's, spelled exactly:** `wsp_yparam`, `wsp_zparam`, `wsp_H0`,
`wsp_Y0`, `wsp_ZG`, `wsp_ZL`, `wsp_YG`, `wsp_YL`, `wsp_zop`, `wsp_yop`, `wsp_loopgain`,
`wsp_nodal_gamma`, `wsp_unstable_freq_kurokawa`, `encirculations`, `enc`, `_dB`, `wsp_zsrc`,
`wsp_zprc`, `wsp_impedance`, `wsp_gain`, `GainDEFs`, `wsp_rc_renorm_s`, `wsp_zo_renorm_s`,
`z_to_pr`, `z_to_pc`, `z_to_pl`, `z_to_sr`, `z_to_sc`, `z_to_sl`, `y_to_pr`, `y_to_pc`, `y_to_pl`,
`y_to_sr`, `y_to_sc`, `y_to_sl`. The document's `wsp__yparam` and `wsp__zop` (double underscore,
E.13/E.16) are its *own* redundant re-implementations of built-ins; circuitRF has one implementation
and registers the single-underscore names only. Leading-underscore `_dB` is a legal identifier in
the expression tokenizer or the tokenizer is extended so it is.

---

## 2. The functions, with their equations

Throughout, `A = vP/vS`, `B = iS/vS = Y0`, `C = vP/iP = H0`, `D = iS/iP` are the four elements of
probe `i`'s diagonal block (`wsp(2i−1,2i)`, `wsp(2i−1,2i−1)`, `wsp(2i,2i)`, `wsp(2i,2i−1)`), and
`[Y]`, `[Z]` are WSP-1's Eq. 44/48 reductions. **Every denominator that can vanish returns NaN at
that frequency and raises a per-run diagnostic** (overview D-7); the document's `+ 1e-15` in
`wsp_yop`/`wsp__zop` (E.16) is not reproduced.

### 2.1 Driving-point immittances (E.15) and bidirectional immittances (E.14)

```
wsp_H0(wsp, idx)  = wsp(2·idx, 2·idx)                          (Eq. 50, 132)
wsp_Y0(wsp, idx)  = wsp(2·idx−1, 2·idx−1)                      (Eq. 52, 134)

wsp_ZG(wsp, idx)  = z11 − z12  = −(1/Y0)·(vP/vS)               (Eq. 67; E.14 wsp_ZG = ZP(1,1) − ZP(1,2))
wsp_ZL(wsp, idx)  = z22 − z21  =  (1/Y0)·(1 + vP/vS)           (Eq. 68; E.14 wsp_ZL = ZP(2,2) − ZP(2,1))
wsp_YG(wsp, idx)  = y11 + y12  =  (1/H0)·(1 − iS/iP)           (Eq. 76; E.14 wsp_YG = YP(1,1) + YP(1,2))
wsp_YL(wsp, idx)  = y22 + y21  =  (1/H0)·(iS/iP)               (Eq. 76; E.14 wsp_YL = YP(2,2) + YP(2,1))
```

`ZG`/`ZL` are the impedances under **series-voltage** stimulation, `YG`/`YL` the admittances under
**shunt-current** stimulation, and `ZG ≠ 1/YG` whenever there is feedback across the probe (Eq. 79–80;
§4.5, p. 56–57). The doc-comment says so, because it is the single most common misreading of the
probe. Only the sums are fundamental: `ZG + ZL = 1/Y0` (Eq. 69), `YG + YL = 1/H0` (Eq. 77).

The document's note that `YG`/`YL` are "NOT outputs of the WSProbes" (E.14) describes its own
implementation; here they are first-class functions and Data Display items (a public re-implementation
of the 2018 probe found them useful enough to add — overview §1.1).

### 2.2 Open-port immittances (E.16)

```
wsp_zop(wsp, idx) = 1/(y11 + y22)      = H0 |_{y12=y21=0}       (Eq. 89)
wsp_yop(wsp, idx) = 1/(z11 + z22)      = Y0 |_{z12=z21=0}       (Eq. 90)
```

These are Ochoa's "open-loop" port immittances (§4.7): the part of `1/H0` and `1/Y0` that survives when
the bilateral feedback is removed, and therefore the part that carries an instability *between* two
otherwise-unconnected blocks (Kurokawa's own case, where every loop gain reads zero — p. 62–63). The
identities

```
1/H0 = (1 − LG)/Zop = YG + YL           1/Y0 = (1 − LG)/Yop = ZG + ZL           (Eq. 93)
Zop/H0 = Yop/Y0                                                                    (Eq. 94)
YG + YL = |Y|·(ZG + ZL)                                                            (Eq. 95)
```

are gates (R-wsp2-14(a)).

### 2.3 Loop gains — `wsp_loopgain(Y, kind [, Z0])`

Takes the reduced two-port `Y` from `wsp_yparam` (the document's calling pattern, p. 68) and a kind
string; `Z0` (default 50 Ω) is used only by the two circulator kinds. Kind strings are the document's;
`"FOR"` is accepted as an alias of `"UNI"` (p. 68: the author "had hoped to" have it called that).

```
"BI"    LG     = −(y12 + y21)/(y11 + y22)                                   (Eq. 92; Tian bilateral)
"UNI"   LGF    = [1 − ȳ22 − ȳ12(ȳ21 + 2) + ȳ11(ȳ22 − 1)] /
                 [1 + ȳ22 − ȳ21(ȳ12 − 2) + ȳ11(ȳ22 + 1)]                     (Eq. 99; forward circulator)
"REV"   LGR    = [1 − ȳ22 − ȳ21(ȳ12 + 2) + ȳ11(ȳ22 − 1)] /
                 [1 + ȳ22 − ȳ12(ȳ21 − 2) + ȳ11(ȳ22 + 1)]                     (Eq. 97; reverse circulator)
"HST"   LG_H   = −y21·y12/(y11·y22)                                          (Eq. 100; Hurst, as printed — T-6)
"MB"    LG_MF  = −(y21 − y12)/(y11 + y22 + 2·y12)                            (Eq. 101; Middlebrook forward)
"MBR"   LG_MR  = −(y12 − y21)/(y11 + y22 + 2·y21)                            (Eq. 102; Middlebrook reverse)
"GFT"   LG_MGF = −y21/(y11 + y22 + y12)                                      (Eq. 103; GFT forward)
"GFTR"  LG_MGR = −y12/(y11 + y22 + y21)                                      (Eq. 104; GFT reverse)
```

where `ȳ = Z0·y` is the normalised admittance. Eq. 97 and 99 were **verified numerically** against
their S-parameter forms

```
LGR = S21 + S11·S22/(1 − S12)          LGF = S12 + S11·S22/(1 − S21)          (Eq. 96, 98)
```

with `S = (I − ȳ)(I + ȳ)⁻¹` on random two-ports (2e-15). Implement the `ȳ` forms as printed; keep the
`S` forms as the test oracle. The S forms are exactly the reflection coefficient a circulator's third
port would see, which is what R-wsp2-14(c) uses.

The bilateral loop gain is what WSP-1 already emits as `LG:<label>`; `wsp_loopgain(…, "BI")` must
equal it bit for bit (same function, called twice).

### 2.4 The nodal conjugate reflection coefficient (E.7)

```
wsp_nodal_gamma(wsp, idx) = (ZG − conj(ZL)) / (ZG + conj(ZL))                (E.7, return line)
```

"a measure of how well the node is conjugately matched … plotting on a dB scale can give you a good
sense of how well a node is gain matched" (E.7). Its magnitude in dB is the natural Data Display
default for it (WSP-4).

### 2.5 Kurokawa's start-up signature (E.12, §4.9)

`wsp_unstable_freq_kurokawa(T)` takes a driving-point function `T` (`H0` **or** `Y0`; the document
says check both, §4.9–4.10), forms `g(ω) = 1/T(ω)` on the sampled sweep, and returns every frequency
at which the three conditions hold (Eq. 107 for `H0`, Eq. 108 for `Y0`, E.12's restatement):

```
Re(g) ≤ 0
Im(g) = 0
∂Im(g)/∂ω > 0
```

Concretely: for consecutive samples `k, k+1` with `Im(g_k) < 0 ≤ Im(g_{k+1})` (the sign change that
is a *clockwise* crossing of the negative real axis — decreasing argument through `π`, from the lower
half-plane to the upper), interpolate the zero of `Im(g)` linearly in `ω`, interpolate `Re(g)` at that
`ω`, and report the frequency when that `Re(g) ≤ 0`. A sample with `Im(g_k) = 0` exactly counts as the
crossing point itself. Return the frequencies ascending; an empty result is an empty cube, not `0`
(the document returns "a zero in case there are no frequencies", E.12 — an empty list is the honest
spelling and is what the Data Display readout shows as "none"). The step direction is the entire
content of the test: a counter-clockwise crossing (`Im` from positive to negative) is the steady-state
side of a *stable* resonance and must **not** be reported.

Document the sampling caveat the way the group-delay page already does for unwrapping: a sweep coarser
than the resonance can miss a crossing entirely; report nothing rather than guess.

### 2.6 Encirclements (E.3)

```
encirculations(SP) = enc(SP) = −unwrap(phase(SP))/360        (E.3; phase in degrees)
```

Returned as a cube over frequency (the running count); the **net count** is the value at the last
frequency, rounded to the nearest integer, and the Data Display shows both (WSP-4). `unwrap` is the
same one the group-delay metric uses. Sign: the document's minus makes a *clockwise* encirclement
count positive, which is the sign NDF and the Nyquist argument want (§8: "the NDF … only encirculate
the origin in a clockwise direction").

### 2.7 Remote even-mode impedance and gain (App. C, D)

```
wsp_impedance(wsp, idx1, idx2) = wsp(2·idx1−1, 2·idx2) / wsp(2·idx1−1, 2·idx2−1)     (Eq. 196/197, App. C code)
      = (V_{idx2}/V_S,idx1) / (I_{idx2}/V_S,idx1)  — the L-direction impedance at probe idx2 under a
        common series stimulus at probe idx1 (the even-mode load line, §4.1 Eq. 37 and App. C)

wsp_gain(wsp, idxS, idxG, idxD) = 10·log10 | Re( wsp(2S−1, 2D) · conj(wsp(2S−1, 2D−1)) )
                                          / Re( wsp(2S−1, 2G) · conj(wsp(2S−1, 2G−1)) ) |   (App. D code)
```

**Typo register T-14 (this brief):** Eq. 199 and 201 as printed use the drain index in *both*
numerator and denominator factors of the ratio; the App. D code (`GT = 10*log(mag(real(...D...)/
real(...G...)))`) is unambiguous — numerator at the drain probe, denominator at the gate probe — and
Eq. 198/200 say the same. Implement the code. The document's `log` is `log10`.

Note Eq. 37 uses the **shunt** stimulus (`wsp(2i, 2j)/wsp(2i, 2j−1)`) while App. C's code uses the
**series** stimulus (`wsp(2i−1, 2j)/wsp(2i−1, 2j−1)`); both cancel the common stimulus and both are
even-mode load lines. Implement App. C (the function the document ships) and expose Eq. 37's shunt
form as an optional third argument `stimulus="series"|"shunt"`, default `"series"`.

### 2.8 Immittance models (E.4, E.11) — **base SI, not pF/nH**

```
wsp_zsrc(R, C, freq) = R + 1/(jωC)                     (E.4)      ω = 2π·freq
wsp_zprc(R, C, freq) = 1/(1/R + jωC)                   (E.4)
y_to_pr(Y) = 1/Re(Y)         y_to_pc(Y, f) = Im(Y)/ω           y_to_pl(Y, f) = −1/(Im(Y)·ω)     (E.11)
y_to_sr(Y) = Re(1/Y)         y_to_sc(Y, f) = −1/(Im(1/Y)·ω)    y_to_sl(Y, f) = Im(1/Y)/ω        (E.11)
z_to_pr(Z) = y_to_pr(1/Z)    z_to_pc, z_to_pl, z_to_sr, z_to_sc, z_to_sl likewise (the document's
                             built-ins, defined by the E.11 duals)
```

**Deliberate deviation, units only:** the document's `y_to_pc` returns picofarads (`1e12·…`) and
`y_to_pl` nanohenries (`1e9·…`). circuitRF returns **farads and henries** — the derived-metrics page's
standing rule is base SI on every trace, and a `1e12` inside a function is exactly the kind of hidden
scale the sweep-unit incident (`sweep-unit-scale-and-mark`) was about. Say so in the doc-comment and
in the user docs; the names are unchanged. E.4's `freq + 1e-9` guard against `f = 0` is not
reproduced: `wsp_zsrc` at `f = 0` is `NaN` (an open), like `Ceff` above resonance on the passive
readouts.

`freq` may be omitted in a `measure` line; the cube's own `freq` axis is used (the document's
`indep(R,1)` default).

### 2.9 Gain definitions (E.1)

```
GainDEFs(GamS, SM, GamL) → { GT_dB, GP_dB, GA_dB, Gmax_dB }
Γin  = S11 + S12·S21·ΓL/(1 − S22·ΓL)                                          (Eq. 203)
Γout = S22 + S12·S21·ΓS/(1 − S11·ΓS)                                          (Eq. 204)
GP = |S21|² · (1 − |ΓL|²) / [ (1 − |Γin|²)  · |1 − S22·ΓL|² ]                 (Eq. 205)
GA = |S21|² · (1 − |ΓS|²) / [ (1 − |Γout|²) · |1 − S11·ΓS|² ]                 (Eq. 206)
GT = |S21|² · (1 − |ΓS|²) · (1 − |ΓL|²) / [ |1 − Γin·ΓS|² · |1 − S22·ΓL|² ]   (Eq. 207)
Gmax = MAG/MSG                                                                (Eq. 208)
_dB(M) = 10·log10|M|                                                          (E.2)
```

`Gmax` calls `RFNetwork`'s existing MAG/MSG (the one implementation the derived-metrics page
documents). `GamS`/`GamL` are the probe's `ZG`/`ZL` converted to Γ against the network's reference
(E.1's own recommendation); `SM` is a 2-port cube from `wsp_block_calc` (WSP-3) or any `S` cube.

### 2.10 Renormalisation (E.10)

```
wsp_zo_renorm_s(ZG, SP, ZL, freq):  Y = stoy(SP, 50);  Zr = diag(ZG, ZL);  F = diag(1/(2√Re ZG), 1/(2√Re ZL))
                                    S' = F · (I − conj(Zr)·Y) · (I + Zr·Y)⁻¹ · F⁻¹
wsp_rc_renorm_s(RG, CG, SP, RL, CL, freq):  as above with Y ← Y + diag(jωCG, jωCL) and Zr = diag(RG, RL)
```

The first is the power-wave renormalisation to complex references (Kurokawa), which `RfCore` already
implements for the `Z0 override` path — **call that**, do not transcribe; assert equality with the
transcribed E.10 matrix expression in a test. The second absorbs the parallel capacitances into the
network and renormalises to the real parts, "so the S-parameters are normalized to the real port
resistance and not a complex impedance" (E.10) — implement as the two steps it is. The document's
`abs(real(RG))` under the square root silently accepts a negative `RG`; circuitRF refuses
(`wsprobe.renorm-negative-reference`) — a negative reference resistance is a wrong input, not a case.

### 2.11 The normalised driving-point loci and the margin slot (overview D-12)

```
circuitRF normalized driving-point locus (series):  nZ = (ZG + ZL)/(|ZG| + |ZL|)     = (1/Y0)/(|ZG|+|ZL|)
circuitRF normalized driving-point locus (shunt):   nY = (YG + YL)/(|YG| + |YL|)     = (1/H0)/(|YG|+|YL|)
```

Both are unitless with `|n| ≤ 1`; Kurokawa's three conditions are invariant under division by a
positive real, so `wsp_unstable_freq_kurokawa` reports the same frequencies on `nZ` as on `Y0`, and on
`nY` as on `H0` — **a gate**, and the reason these are safe to offer beside the raw loci. They are
named as circuitRF's, cite this brief and the EuMIC-2024 abstract, and carry the sentence "not the
published margin" in their doc-comment and Data Display description.

`wsp_stability_margin(wsp, idx)` is **registered and refused** with the diagnostic
`wsprobe.margin-not-transcribed`: *"The stability margin of T. A. Winslow, 'A Novel Stability Margin
for Transfer Functions', EuMIC 2024 (DOI 10.23919/EuMIC61603.2024.10732614) has not been transcribed
into circuitRF; its definition is not in the public abstract."* The moment the owner supplies the
paper, the body replaces the refusal and the D-12 loci become its inputs.

> **Superseded 2026-09-08 (after this brief was built and committed).** The paper arrived; overview
> D-12 is rewritten and **`brief-wsprobe-9-stability-margin.md` implements the margin** from it —
> `rY, iY, rH, iH`, `SM_Y0`, `SM_H0` as functions of the `ZG`/`ZL`/`YG`/`YL` this brief built. WSP-9
> also **retires** `wsp_nZ`, `wsp_nY` and the refusal above (its §7), so gate (i)'s normalised-locus
> and refusal cases go with them. Nothing else in this brief changes.

---

## 3. What the reduced two-port makes possible for free

`wsp_yparam` returns a `{freq, i, j}` 2-port cube. Converted to S at a real reference it is exactly
the shape the Data Display's existing derived metrics take: **Rollett K, μ, μ′, |Δ|, MAG/MSG and the
stability circles of the reduced two-port at a probe** come from the code already shipped, once WSP-4
exposes the probe's reduced network as a source. This brief only has to make sure the cube is shaped
so that `NetworkMetrics` accepts it (an `S` cube plus a `Z0` cube in a group). Record the fact in the
design note; it is a real capability the document does not mention.

---

## 4. Gates

`tests/RfCore.Tests/Stability/WspNodalTests.cs` for the scalar cores; `tests/Engine.Tests/...` for the
end-to-end lines.

**R-wsp2-14(a) — the identities, on random two-ports.** Build `A, B, C, D` from a random `[Y]` by
solving Fig. 16's equations (the test helper of WSP-1), then assert to 1e-12 every identity of §2.1–2.2
and Eq. 49, 60, 69, 77, 93, 94, 95; and `wsp_loopgain(Y,"BI")` = `LG` of WSP-1's defaults. 1,000
random draws, including some with `|y12·y21| ≫ |y11·y22|` (heavy feedback) and some with
`y12 = y21 = 0` (then `ZG = 1/YG` must hold, Eq. 81/82, and every loop gain but the circulators is 0).

**(b) — circulator forms.** Eq. 97 vs Eq. 96 and Eq. 99 vs Eq. 98 on random two-ports at several
`Z0`; and at `y12 = y21 = 0`, `LGR = LGF = S11·S22 = ΓG·ΓL` (Jackson's stability index, §3.7, App. B).

**(c) — the synthetic circulator against a real one.** On the WSP-1 fixture (e) at a probe: build the
same netlist with circuitRF's own `Circulator` model inserted at the probe's node — its ports 1 and 2
in the node, port 3 to a `Term` of the same `Z0` and no other port — and assert that the one-port `S11`
of that netlist equals `wsp_loopgain(Y, "REV", Z0)` (for `CW`) and `"UNI"` (for `CCW`, or ports 1/2
swapped) to 1e-9 at every frequency. The document says the two are "numerically identical" (p. 67);
this makes it a test, through an independent path.

**(d) — Kurokawa search on the resonators.** On WSP-1's fixtures (b) and (c) at 1 MHz steps:
`wsp_unstable_freq_kurokawa(Y0)` on the series resonator returns one frequency within half a step of
1.5915 GHz and `wsp_unstable_freq_kurokawa(H0)` returns empty; the parallel resonator the reverse. Then
flip `R1` positive (stable): both return empty on both fixtures — the counter-clockwise crossing of the
now-stable resonance must not be reported.

**(e) — encirclements.** Synthetic loci `e^{−jkθ}`, `θ` over `[0, 2π)`, `k = 0, ±1, ±2`: `enc` ends at
`k` (clockwise positive). A locus that touches but does not cross the origin ends at 0.

**(f) — `wsp_impedance` and `wsp_gain`.** A two-stage fixture with a symmetric two-way combiner: the
even-mode impedance at either drain probe equals the value found by simulating one half with the
common load doubled (the classic even-mode reduction), to 1e-9. `wsp_gain` vs Eq. 202/205 computed
from the block's own S-parameters and the probes' `ZG`/`ZL` (the document: "yields the same results",
p. 135) to 1e-9 dB.

**(g) — immittance models.** Round-trips: `wsp_zprc(y_to_pr(Y), y_to_pc(Y,f), f) == 1/Y`;
`wsp_zsrc(y_to_sr(Y), y_to_sc(Y,f), f) == 1/Y` for `Im(1/Y) < 0`; farads and henries, asserted on a
100 pF / 2 nH case against the numbers, not the document's scaled ones.

**(h) — renormalisation.** `wsp_zo_renorm_s` equals `RfCore`'s existing complex-reference
renormalisation and the transcribed E.10 expression; `wsp_rc_renorm_s(RG, 0, SP, RL, 0)` equals
`wsp_zo_renorm_s(RG, SP, RL)`; a negative `RG` is refused.

**(i) — normalised loci.** `wsp_unstable_freq_kurokawa` on `nZ` and on `Y0` return identical
frequencies on (d); `|nZ| ≤ 1` everywhere; `wsp_stability_margin` raises the documented refusal.

**(j) — end to end.** A `.cnl` with `measure` lines using each function evaluates identically through
the GUI path (`MeasurementEvaluator`) and `circuitrf sparam`; cube shapes as R-wsp2-2 states; a
misspelled probe label lists the probes.

## On completion

Findings to `src/RfCore/RESOLVED.md`; **never a `CLAUDE.md`**. Append §5 "Single-probe derived
metrics" to `docs/design/stability-wsprobe.md` (the table of §1 here plus T-14), and add the new
built-ins to `docs/design/expressions.md`'s function list and to `docs/design/measurements.md`. User
docs are WSP-7's.
