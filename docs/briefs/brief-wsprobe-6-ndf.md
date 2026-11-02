# Sonnet Brief — WSP-6: the Normalized Determinant Function, native to the engine

**Read `brief-wsprobe-0-overview.md` first** (D-11). Independent of WSP-1–5 except for the
probe-based cross-check (§5), which needs WSP-3's `wsp_ndf`. Reference: T. A. Winslow, *General
Circuit Analysis Using The WSProbe* (2023), §3.5 (Bode's return difference, Eq. 14–16), §3.6
(Eq. 17), §8 (Eq. 181–186 and the five Platzker properties, p. 112), §5.4 (why a black-box device
blocks NDF, p. 95–99); A. Platzker and W. Struble, "Rigorous Determination of the Stability of Linear
N-Node Circuits from Network Determinants and the Appropriate Role of the Stability Factor K of
Their Reduced Two-Ports," INMMC Workshop, 1994 ([7]); W. Struble and A. Platzker, "A Rigorous Yet
Simple Method For Determining Stability of Linear N-Port Networks," GaAs IC Symposium, 1993 ([19]).

**Scope.** An `NDF=yes` knob on the S-parameter analysis that makes the engine emit the NDF over the
sweep, computed from its own assembly with every built-in dependent source zeroed exactly, user
devices passivated through a named variable, and black boxes refused by name. Encirclement count and
the Platzker property checks ride along. Large-signal NDF (over the conversion matrix) is noted as
the follow-on and not built here.

---

## 1. What is computed

```
NDF(ω) = Δ / Δ0 = |Y(ω)| / |Y_passive(ω)|                                      (Eq. 181; Bode Eq. 16)
```

`Y` is the network's admittance matrix with every termination, bias network and bypass included and
every independent source off; `Y_passive` is the same matrix with **every dependent source, negative
resistance and non-Foster element rendered passive** (§8, p. 111). `Δ0` has no right-half-plane
zeros by construction, so by the argument principle the number of **clockwise** encirclements of the
origin by `NDF(jω)`, `ω` from 0 to ∞, is the number of right-half-plane poles of the network (§8,
p. 112). The five properties Platzker gives (p. 112) are checks the engine performs on its own output:

1. `NDF` has zeros only (the denominators of `Δ` and `Δ0` cancel);
2. it encircles the origin clockwise only;
3. `NDF → 1` as `ω → ∞`;
4. `NDF(−ω) = conj(NDF(ω))` — positive frequencies suffice;
5. `Im(NDF) → 0` as `ω → 0`.

**Why circuitRF can do this and the document's designer usually cannot.** §8 (p. 113) and §5.4
(p. 95): the passive determinant "requires having precise access to the transconductance elements in
all active devices," which black-box vendor models withhold. Every built-in active model in
`src/Core/Devices` is circuitRF's own; each knows its controlled sources exactly.

---

## 2. The determinant ratio without determinants

**R-wsp6-1.** Assemble the same terminated MNA the S-parameter engine solves (wave-path stamping:
ports as `1/Z0`, sources off, nonlinear devices linearised at the DC operating point) twice per
frequency: `M` (active) and `M0` (passive, §3). Their difference `ΔM = M − M0` is **sparse and
low-rank** — it holds only the dependent-source entries, and its nonzero columns are the control
columns of those sources (a `VCCS` contributes two columns; a linearised FET two or three). With
`c₁ … c_r` the nonzero columns, `U = ΔM[:, c]` (`n × r`) and `E = [e_{c₁} … e_{c_r}]` (`n × r`):

```
ΔM = U·Eᵀ
det(M) = det(M0 + U·Eᵀ) = det(M0) · det(I_r + Eᵀ·M0⁻¹·U)          (matrix determinant lemma)
NDF    = det(I_r + Eᵀ·M0⁻¹·U)
```

i.e. `r` sparse solves against the **passive** factorisation (`M0⁻¹·U`), the `r` rows at `c` of the
result, plus `I_r`, and one dense `r × r` determinant. `r` is the number of control columns in the
whole circuit — two per transistor, not the size of the network — so there is no large determinant,
no overflow, no underflow and no loss of the tiny ratio between two huge numbers, which are exactly
the numerical troubles the document reports for NDF (p. 95, 112). The `r × r` matrix `I_r + Eᵀ M0⁻¹ U`
is Bode's **return-difference matrix** of the dependent sources; its determinant is the NDF and its
LU pivots, taken in device order, are Struble's sequential return differences `F_i = 1 + T_i` of
Eq. 17 — record that in the design note (it is a free cross-check, §5), do not build a feature on it.

**MNA versus nodal.** The document writes `|Y|` for the nodal matrix; circuitRF's `M` carries branch
rows as well. Because `M` and `M0` have identical branch blocks (`B`, `C`, `D` in the Schur
partition), eliminating the branches multiplies both determinants by the same factor and the ratio
is the nodal one. A zero-volt branch (a source that is off, an `IProbe`, a `WSProbe`) merges two rows
in both alike. So `det(M)/det(M0)` **is** `|Y|/|Y_passive|`; assert it (R-wsp6-9(e)).

**R-wsp6-2. Output.** Cubes `NDF {freq}` (Complex), `NDF_enc {freq}` (Real, WSP-2's `enc` running
count) and the scalar `NDF_poles` (rounded final count). Property checks as run diagnostics:
`ndf.no-asymptote` when `|NDF(f_max) − 1| > 0.05` ("extend the sweep upward; NDF must tend to 1"),
`ndf.dc-imaginary` when `|Im NDF(f_min)|/|NDF(f_min)| > 0.05`, `ndf.counterclockwise` when the
running count ever decreases by more than the sampling can explain (a counter-clockwise encirclement
means `Δ0` was not passive — the passivation failed; see R-wsp6-5). A sweep that starts at 0 Hz uses
the DC formulation for that point.

The directive:

```
analysis SP1 type=sparam start=1 stop=100000 npts=2001 log Unit=MHz  NDF=yes  [PassiveVars="NDFgm"] [PassiveParams="X1.gmscale"]
```

`circuitrf sparam` prints `NDF: N right-half-plane pole(s)` and the property findings; `--json`
carries them; `explain --analysis` lists the passivation each instance will use (§3), which is the
way to see a refusal coming without running.

---

## 3. The passivation contract

**R-wsp6-3.** `ComponentModel` gains

```
public enum Activity { Passive, ActiveExact, ActiveUserScaled, BlackBox }
public virtual Activity Activity => Activity.Passive;
public virtual void StampPassive(IMnaContext mna, ElaboratedComponent c, double omega)            => Stamp(mna, c, omega);
public virtual void StampLinearizedPassive(IMnaContext mna, ElaboratedComponent c, double omega, in PortVoltages bias)
                                                                                                  => StampLinearized(mna, c, omega, bias);
```

with the rule, **checked at NDF setup**: a model whose `Activity` is `ActiveExact` **must** override
the relevant `StampPassive`/`StampLinearizedPassive` (a reflection test enumerates every
`ComponentModel` subclass and asserts it — R-wsp6-9(h)); a `Passive` model's default is correct by
definition; `ActiveUserScaled` needs a `PassiveVars`/`PassiveParams` entry that reaches it or the run
refuses; `BlackBox` refuses. The refusal (`ndf.cannot-passivate`) lists every offending instance with
its reason and stops before the frequency loop.

| Model(s) | Activity | Passivation (what `StampPassive` stamps) |
|---|---|---|
| `R`, `C`, `L`, `SRLC`, `PRLC`, `Bead`, `Mutual`, `TLIN`, microstrips, `Short`, `IProbe`, `WSProbe`, `Port`/`Term` | Passive | as-is; **except** `R` with `R < 0` and `Z_Port` with `Re Z < 0` at some ω, which are ActiveExact and passivate by `Re → |Re|` (the document's "negative resistances … rendered passive", p. 111) |
| `Vdc`, `V_1Tone`/`V_nTone`, `I_1Tone`/`I_nTone`, `P1Tone`, `Tuner` | Passive | sources are off in this assembly already; `P1Tone`/`Tuner` stamp their impedances |
| `VCCS` | ActiveExact | `G → 0` (no stamp) |
| `VCVS` | ActiveExact | `E → 0` (a 0 V branch: short) |
| `Amp` (system) | ActiveExact | forward gain → 0: matched terminations at both ports, isolation kept |
| `Mixer` (system) | ActiveExact | conversion gain → 0 |
| `Atten`, `Switch`, `Circulator`, `Coupler`, `Balun`, `Filter`, `Duplexer` | Passive | as-is (the circulator is non-reciprocal and passive — Platzker zeroes dependent sources, not non-reciprocity) |
| `FET_*`, `PFET_*`, `JFET_*` | ActiveExact | in the linearised block: `∂I_d/∂V_gs → 0` (gm); keep `∂I_d/∂V_ds` (gds), every diode conductance, and the capacitances evaluated at bias — each two-terminal `C(V)` is symmetric; a transcapacitance (`∂Q_p/∂V_q`, `p ≠ q`, not matched by `∂Q_q/∂V_p`) is a controlled source and is zeroed |
| `BJT_*` | ActiveExact | transport current source `I_ct → 0` (both directions); junction conductances and charges kept |
| `MOS*`, `VDMOS_*`, `IGBT_*` | ActiveExact | `∂I_ds/∂V_gs → 0` and `∂I_ds/∂V_bs → 0`; `∂I_ds/∂V_ds` kept |
| `Diode`, `NonlinearC`, `SemiC` | Passive | a two-terminal nonlinearity linearises to a positive conductance/capacitance at any bias |
| `SDD` | ActiveUserScaled | the user multiplies each controlled-source term by a global (`I[2,0] = NDFgm*Ids(_v1,_v2) + …` is the wrong shape — the *dependence on the other port* is the source: write `Ids(NDFgm*_v1 + (1−NDFgm)*Vgs0, _v2)` or, more simply, keep the gm term separate, as the Hero 2 SDD does not — WSP-7's example shows the pattern) and names it in `PassiveVars`; the passive assembly re-elaborates with those globals at 0. An SDD with no `PassiveVars` reaching it is refused |
| `VerilogA`, `ExtDevice` | ActiveUserScaled | only through an instance parameter the model exposes, named in `PassiveParams` (`X1.gmscale=0`); otherwise BlackBox |
| `SnP`, `Chain`, `wBond` (`SnP`-backed) | data-dependent | **Passive** when `σ_max(S) ≤ 1 + 1e-6` at every sampled frequency of the file (the passivity metric the derived-metrics page already computes); otherwise **BlackBox** — "an S-parameter block with gain hides its dependent sources" |
| `Match` | Passive | it synthesises passive networks |

**R-wsp6-4. Re-elaboration for the user-scaled route.** `PassiveVars` is applied exactly as
`--set var=expr` is (before elaboration), producing a second `ElaboratedNetlist` for the passive
assembly; both netlists must produce the same node map and branch order (assert), because `ΔM` is
formed by subtracting the two assemblies element-wise. A `PassiveVars` name that is not a global, or
that no device reads, is a refusal (the classic silent failure of NDF: a scaling variable that scales
nothing).

**R-wsp6-5. The passivity guard.** After passivation, for every `ActiveExact`/`ActiveUserScaled`
device, form its passivated linearised block `Y_dev(ω)` at each frequency and check
`Y_dev + Y_devᴴ ⪰ 0` (Hermitian part positive semidefinite, tolerance 1e-12·‖Y_dev‖). A failure is
`ndf.passivation-not-passive` naming the device and frequency; the NDF is still emitted (the count
is then unreliable and the message says so). This is the check the document wishes for (p. 113: "So,
great care must be taken when constructing the NDF").

---

## 4. Cost

Per frequency: two assemblies (both from the cached stamp pattern), one factorisation of `M0`, `r`
sparse solves, one `r × r` determinant. `M` itself is **never factored** — only its difference from
`M0` is used. The S-parameter solves of the same run use their own factorisation of `M` as today.
Frequency-parallel exactly as SP-P3 (chunks write `NDF[f]` by index). Counters, not timings.

---

## 5. Cross-checks and the probe route

- **Full determinants (test only).** `det(M)` and `det(M0)` as sums of `log` of LU pivots with the
  permutation parities, on small fixtures, must reproduce the lemma's NDF to 1e-10 (R-wsp6-9(e)).
- **Probe route (Eq. 186).** When `NDF=yes` and WSProbes are present, the engine also emits
  `wsp_passive` (WSP-1's probe solves on `M0`), and WSP-3's `wsp_ndf(wsp, wsp_passive, probes)` over
  a probe at **every** non-ground node must equal the native NDF (the reduced determinant over all
  nodes is the full one) — R-wsp6-9(f). Over a subset of nodes it is the *reduced* NDF the document
  describes (p. 112–113), correct only if the omitted nodes hide no pole; say so.
- **Struble's sequential form (Eq. 17).** The product of the return-difference matrix's LU pivots in
  device order equals its determinant trivially; a test that this reproduces `Π(1 + T_i)` with `T_i`
  computed by zeroing devices 1..i−1 explicitly (the definition) is the cross-check that the column
  bookkeeping of R-wsp6-1 identifies devices correctly.

**Large-signal NDF** — `det(J_ss)/det(J_ss,passive)` over WSP-5's conversion matrix — is the natural
extension and is deliberately not in scope; note it in the design note with the observation that the
passivated device spectra are the ones R-wsp6-3 defines, applied per harmonic.

---

## 6. Gates

`tests/Engine.Tests/Linear/NdfTests.cs`; fixtures under `testdata/ndf/`.

**R-wsp6-9(a) — an analytic single-loop stage.** `VCCS` (`gm`) driving `R_L ∥ C_L`, with `R_f` from
output back to the control node, source `R_s` at the control node. Nodal 2×2 `Y` by hand; `Y_passive`
= the same with `gm = 0`; `NDF = |Y|/|Y_passive|` in closed form (a bilinear function of `jω`). Assert
the engine's NDF equals it to 1e-12 at 200 log-spaced points; the RHP-pole count (0 for `gm < g_crit`,
1 above, where `g_crit` follows from the closed form) equals `NDF_poles`.

**(b) — the resonators.** WSP-1's series and parallel resonators with `R1` realised as a `VCCS`
across its own control (`G = −1/|R1|`, a negative conductance that passivates to 0): `NDF_poles = 1`
for the unstable value, 0 with `G` positive; the document's Fig. 37 (both encircle once, at 1.592 GHz)
reproduced numerically — the phase of `NDF` passes through `π` near 1.5915 GHz.

**(c) — Platzker's properties on a real device.** Hero 2's SDD with a `NDFgm` variable added to its
`I[2,0]` equation (the fixture is a copy under `testdata/ndf/`, the original is untouched) at its bias:
`NDF(100 GHz) ≈ 1`, `Im NDF(1 kHz) ≈ 0`, no counter-clockwise step, and `NDF_poles = 0` for the
Hero 2 terminations; then with a series inductance in the gate lead sized to make the stage
oscillate (verified independently by the K/|Δ| of its S-parameters going potentially unstable **and**
by a Kurokawa signature on a WSProbe's `1/Y0`), `NDF_poles = 1`.

**(d) — refusals.** Hero 1 (`potentially_unstable_amp.s2p`, `σ_max > 1`): `ndf.cannot-passivate`
naming `X1` with the reason; the same run with the `.s2p` replaced by a passive file proceeds. An SDD
with no `PassiveVars`: refused naming it. `PassiveVars="nothing"`: refused as scaling nothing.

**(e) — the lemma against full determinants** on (a) and (b), 1e-10.

**(f) — the probe route.** (a) with a WSProbe at every non-ground node and `NDF=yes`:
`wsp_ndf(SP1.wsp, SP1.wsp_passive, all)` equals `SP1.NDF` to 1e-9.

**(g) — the passivity guard has teeth.** For each built-in active family at three bias points: the
passivated block passes `Y + Yᴴ ⪰ 0`; the *active* block of a biased FET/BJT/MOS **fails** it at some
frequency (so the check distinguishes them); a deliberately wrong passivation (test double that keeps
gm) triggers `ndf.passivation-not-passive`.

**(h) — the contract is complete.** Reflection over every `ComponentModel` subclass: `Activity ==
ActiveExact ⇒ StampPassive` or `StampLinearizedPassive` is overridden; and the table of §3 is
reproduced by the test (a model that appears in the codebase and not in the table fails the test, so
the next device cannot be added without deciding its passivation).

**(i) — K is not enough.** A fixture the document's §3.2 describes (Platzker's examples, [6]): two
stages with a hidden right-half-plane pole whose two-port `K > 1` and `|Δ| < 1` across the band, while
`NDF_poles = 1`. The derived-metrics page's μ/K say stable; the NDF says unstable; the user docs
(WSP-7) use this fixture to make the point.

**(j) — no-knob runs untouched;** counters: one factorisation and `r` solves per frequency.

**(k) — the envelope agrees with a re-run** ([E] Fig. 6/7 as a test; WSP-9 R-wsp9-6). On WSP-9's
Ohtomo Type-A fixture at `ρ = 0.9`, a **coarse** grid (30° steps on both sides, 144 runs — the fine
grid is WSP-9's post-processing job, not this gate's): the native `NDF_poles` of a re-run with the
Terms set to each `(ΓS, ΓL)` equals `wsp_loadpull_ndf`'s `NDFenc` at every grid point, and the
`wsp_passive` this brief emits (§5) is what that function takes. Every point with `NDF_poles ≥ 1` has
`SMenv < −30 dB` (WSP-9 R-wsp9-5); the converse is **printed, not asserted** — [E]'s own point is that
the margin collapses before NDF encircles.

## On completion

Findings to `src/Engine/RESOLVED.md` and `src/Core/RESOLVED.md` (the passivation table is standing
knowledge and belongs in the design note, not a `CLAUDE.md`). Append **§11** "NDF" to
`docs/design/stability-wsprobe.md` (§9 is WSP-9's, §10 WSP-5's) with the table of §3 and the lemma of §2; add `Activity` to
`docs/design/data-model.md` §5 ("How to add a component type" must say every new model declares
it).
