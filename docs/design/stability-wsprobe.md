# The WSProbe — bidirectional impedances and the `wsp` matrix

Design note for the WSProbe series (`docs/sonnet-briefs/brief-wsprobe-0-overview.md` and its seven
implementation briefs). **WSP-1 created this note with §1–§4 (the probe, the `wsp` matrix, the
DataSet layout, the typo-register entries the engine half depends on).** Later briefs append their
sections: WSP-2 the single-probe derived metrics, WSP-3 probe pairs and the envelope, WSP-5
harmonic balance, WSP-6 NDF.

**Reference, cited by equation throughout:** T. A. Winslow, *General Circuit Analysis Using The
WSProbe*, January 29, 2023 (public technical report). Notation is the document's, verbatim — `wsp`,
`H0`, `Y0`, `ZG`, `ZL`, `YG`, `YL`, `LG`, `F`, `idx`, the Appendix E.13 letters `A B C D`. Nothing is
renamed to something more circuitRF-flavoured; a designer who has read the document must find every
quantity circuitRF reports under the name the document gave it.

---

## 1. What the probe is

A `WSProbe` is a **two-terminal series element with an orientation** — terminals `G` (generator
side) and `L` (load side), Fig. 15/16 — placed *in* a node so that it splits the node into a G-side
and an L-side terminal, exactly as an `IProbe` does. Electrically it is a **0 V short**: it perturbs
nothing (§4, p. 33, "completely nonperturbative … requiring only a single analysis sweep"). In the
code it is `WSProbeModel`, which shares its whole stamp with `IProbeModel` through
`SeriesProbeModelBase`: one branch unknown, the constraint `V(nG) − V(nL) = 0`, and the KCL coupling
with the branch current flowing G → L. Every engine site that used to match an `IProbeModel` — the
DC packer's `I:<label>` cubes, the harmonic-balance `I` cube and `__ProbeBranches`, the SDD
control-current resolvers — matches the base, so a WSProbe reports `I:<label>` in DC and HB exactly
as an IProbe does (gate R-wsp1-14(m)). A probe that is not transparent to the analyses it does not
serve is a probe that perturbs.

Netlist spelling: `WSProbe:<label> nG nL` — first net G, second net L, the same first/second rule
`IProbe np nm` uses. No parameters in v1 (a `Z0` for the synthetic circulator of Eq. 96–99 is a
function argument, not a probe property). The instance name is the document's "Label"; a probe in a
sub-cell is `X1.GATE` (§4.3, "at any depth of schematic hierarchy").

**`idx`** (Eq. 50–52) is assigned at elaboration, in **flattened netlist order, 1-based**, and is a
property of `ElaboratedNetlist` (`WspProbes`, a list of `(ComponentIndex, Label, Idx)`) rather than
of the model, because it depends on the other probes. It is reported in the run's `__WspProbes` cube
and in `explain --analysis`, so it is never guessed.

Its value is not the element but the matrix the analysis produces from it.

## 2. The `wsp` matrix

For each probe `i` the engine applies, mathematically, two vanishingly small auxiliary generators
(Fig. 16): a **series voltage** `vS` in the probe branch (− at G, + at L, so `v_L − v_G = vS`) and a
**shunt current** `iP` injected into the G-side node; and reads at *every* probe `j` two responses:
the branch current `iS` (flowing G → L through the probe) and the G-side node voltage `vP`.
Normalised, these are the four transfer functions per probe pair (Eq. 33), stored **1-based, rows =
stimulus probe, columns = response probe** (Eq. 32–34):

```
wsp(2i−1, 2j−1) = iS_j / vS_i     series current  response to series voltage   (Y0 when i = j, Eq. 52/133)
wsp(2i−1, 2j  ) = vP_j / vS_i     shunt  voltage  response to series voltage   (Eq. 135)
wsp(2i  , 2j−1) = iS_j / iP_i     series current  response to shunt current    (Eq. 136)
wsp(2i  , 2j  ) = vP_j / iP_i     shunt  voltage  response to shunt current    (H0 when i = j, Eq. 50/131)
```

With `N` probes this is `2N × 2N`. Everything else in the document — the reduced two-port at the
probe (Eq. 44/48), the bidirectional impedances (Eq. 65–68), every loop gain, the synthetic
circulator, probe-pair blocks, bifurcation, Ohtomo, the NDF reductions, the envelope — is
**post-processing of `wsp`**. That is the architecture: one engine addition (compute `wsp`), one
library of pure functions over it (`src/RfCore/Stability/`), and their surfacing in measurements,
the Data Display and the CLI.

### 2.1 How the engine computes it (`SParameterEngine.SolveProbes`)

Inside the per-frequency loop of **both** the wave path and the legacy path, after that frequency's
S-parameter solves and **against the same factorisation**: with `N` probes the extra work is `2N`
back-substitutions and no factorisation, and the extra solves stamp nothing, so the `MnaSystem`
pattern cache is untouched. The mapping of the document's conventions onto circuitRF's (overview
§3), fixed here once because a sign flip in any of them produces plausible wrong answers:

| Document (Fig. 16) | circuitRF |
|---|---|
| Probe terminals `G` then `L` | `WSProbe:<label> nG nL` — first net is G, second is L |
| `iS` flows G → L through the probe | Engine branch-current convention: *first node → second node*. `iS` **is** the branch current unknown, unchanged in sign |
| `vS` has − at G, + at L: `v_L − v_G = vS` | The 0 V constraint row is `V(nG) − V(nL) = value`; the series injection is `b[br] = −1` for unit `vS` |
| `iP` injected **into** the G-side node | `b[nG − 1] = +1` — the engine's current-source convention already injects into its first node |
| `vP` = voltage of the G-side node to ground | `x[nG − 1]` of the solve; a grounded G node reads 0 |
| `i1` into port 1 of the reduced two-port (G side): `i1 = iP − iS`; `i2 = iS` | Consequences the reduction (Eq. 38, 45) already encodes; nothing to stamp |

The branch index is read off the model *after* the stamp at each frequency, because the wave and
legacy assemblies number branches differently (the wave path skips port branches) and a precomputed
index would be the wrong one on one of them.

**The series-source sign is the one place a wrong guess survives every symmetric test.** With `−1`, a
zero-feedback cascade gives `vP/vS = −ZG/(ZG + ZL)` (Eq. 67, 69); with `+1` it gives the negative and
`ZG` comes out negated. `WSProbeTests.A_ZeroFeedbackCascade_…` holds it to 1e-12.

**The network the probes see is the terminated one** (§4.2: "All external reference terminations
(Zo) and any DC supplies and all bypassing are contained within the reduced Y matrix"). On the wave
path that is exactly the assembly already on the stack — every `Port`/`Term` stamps its `1/Z0`,
independent sources are off, nonlinear devices are linearised at the DC operating point. On the
legacy path (some port with `Re(Z0) ≤ 0`) a port is a 0 V driven branch, which is *not* a
termination; the probe solves there use a second assembly on its own `MnaSystem` — the same stamp
sequence (so every model's branch index and every SDD's resolved control branch are the ones that
assembly has too) plus one diagonal entry `−Z0` per port branch, which turns `V(n0) − V(n1) = 0` into
`V(n0) − V(n1) − Z0·I = 0`: the port terminated in its own `Z0` with no drive. A port with `Z0 = 0`
exactly cannot be terminated and is a refusal naming the port (`wsprobe.port-short`), never a
large-conductance stand-in.

**Port-less analyses are allowed when a probe is present.** The document's own fixtures (Fig. 31,
34) have no ports. The run emits the `wsp` cubes with no `S` and no `Z0` cube; nothing downstream
assumes `S` exists (`sparam` refuses a Touchstone of such a run by name, and says which spellings
carry the result).

The regularisation retry applies to the probe solves too — they use the retried factorisation, and
the `sparam-regularization` warning is emitted once as today. A probe whose G node is floating
produces a singular matrix and the existing zero-row diagnostic names the node.

### 2.2 The six default outputs (`WspReduction`, `src/RfCore/Stability/WspReduction.cs`)

Writing `A = vP/vS = wsp(2i−1, 2i)`, `B = iS/vS = wsp(2i−1, 2i−1) = Y0`, `C = vP/iP = wsp(2i, 2i) =
H0`, `D = iS/iP = wsp(2i, 2i−1)` (the Appendix E.13 letters):

```
|P| = B·C − A·D                                                 (Eq. 42)
α = |P|,  β = |P| + A,  γ = |P| − D,  δ = |P| + A − D + 1        (Eq. 43)
[Y] = (1/H0)·[[ δ, −β ], [ −γ, α ]]                             (Eq. 44)   — never Eq. 40 as printed (T-1)
[Z] = (1/Y0)·[[ α,  β ], [  γ, δ ]]                             (Eq. 48)   — never Eq. 47 as printed (T-2)
H0 = C  (Eq. 50)      Y0 = B  (Eq. 52)
LG = −(y12 + y21)/(y11 + y22) = (z12 + z21)/(z11 + z22)         (Eq. 58/92, 60)   bilateral (Tian) loop gain
F  = 1 − LG                                                     (Eq. 57, p. 33)
ZG = z11 − z12 = −A/B                                           (Eq. 65, 67)
ZL = z22 − z21 = (1 + A)/B                                      (Eq. 66, 68)
```

`ZG` and `ZL` ship through the **Z form** (`−A/B`, `(1 + A)/B`): one fewer cancellation than the `|Y|`
form of Eq. 65/66, and algebraically exact; the `|Y|` form is kept as a test oracle only
(`ZGFromY`/`ZLFromY`). The reduced two-port has **port 1 = the G-side terminal, port 2 = the L-side
terminal**; `ZG` looks into port 1 (the generator direction), `ZL` into port 2 (the load direction)
(Eq. 64–66). In the zero-feedback limit `ZG = 1/y11` and `ZL = 1/y22` (Eq. 89).

**Guards (overview D-7).** `C = 0` (`H0 = 0`, an exact short from the G node to ground) makes `[Y]`
undefined; `B = 0` (`Y0 = 0`, an exact open in the probe branch) makes `[Z]` undefined. The library
returns NaN for the affected outputs at that frequency and the engine adds one run warning per
probe (`wsprobe.degenerate-node`) naming the probe and the first offending frequency. **No epsilon is
ever added to a denominator** — the document's own `wsp_yop`/`wsp__zop` add `1e-15`; circuitRF does
not.

These are computed by the engine at run time *through the same library functions every derived
metric calls* (overview D-2), so a run's `ZG:GATE` cube and a trace card's `wsp_ZG` of the same probe
are one implementation.

### 2.3 What a loop gain is, and is not

The two driving-point functions `H0` and `Y0` carry the full network determinant in their
denominators (Eq. 28–30) and are the document's *primary* stability metric (§4.9–4.10): plot `1/H0`
and `1/Y0` on a polar chart and look for Kurokawa's start-up signature — a clockwise crossing of the
negative real axis (Eq. 107/108). **Both must be checked**, because a zero can mask the pole in one
of them but never in both: the series resonator (Fig. 31) shows it in `1/Y0` only, the parallel
resonator (Fig. 34) in `1/H0` only (§4.10, Eq. 109–128; both are gates in `WSProbeTests`). Every
loop gain — including the default `LG` and `F = 1 − LG` — is **incomplete** (§4.4, p. 50: "not
fundamental circuit quantities and are therefore not rigorous stability measurements"). They are
diagnostic, and useful, and the user docs must carry the caveat with the weight the document gives it.

### 2.4 Where feedback around a probe comes from

A probe has feedback around it — `y12 ≠ 0`, `ZG ≠ 1/YG` (Eq. 79) — only when some path joins its
two sides *other than through the probe itself*. Ground does not count: two one-ports that share only
the reference have `z12 = 0`. A probe placed between a series element and a node whose only other
connection is a termination to ground (Hero 1's `L1`–`a1` wire, for instance) has a dangling one-port
on its G side and `ZG = 1/YG` exactly, however much feedback the rest of the network carries.
Algebraically `ZG·YG = 1 ⟺ z12·(z12 + z21 − z11 − z22) = 0`, i.e. `z12 = 0` or `LG = 1`. The gate
fixture (`testdata/wsprobe/hero1_probed.cnl`) puts the probes at the two-port's ports, where `C5`
plus the two-port closes a loop around each.

## 3. DataSet layout and accessors

Per run (per sweep point when swept), all Complex:

| Cube | Axes | Content |
|---|---|---|
| `wsp` | `{freq, row, col}` | `row`/`col` are 1-based integer values `1 … 2N` (no unit); `wsp[f, r, c]` is the document's `wsp(r, c)` with no index arithmetic between |
| `H0:<label>`, `Y0:<label>`, `ZG:<label>`, `ZL:<label>`, `LG:<label>`, `F:<label>` | `{freq}` | one set per probe, the `:` spelling of `I:IP1`; `H0`/`ZG`/`ZL` in Ω, `Y0` in S |
| `__WspProbes` | `{probe}` (labels = the probe labels, in idx order); Real values = `idx` | metadata; excluded from pickers like every `__` cube |

Under `ParametricSweepEngine` the sweep axis is prepended (`wsp[Vgg, freq, row, col]`,
`ZG:P1[Vgg, freq]`) and `__WspProbes` passes through unstacked, by the existing `StackSweepAxis`
rules (gate R-wsp1-14(h)).

Measurement accessors (`Evaluator.EvalQualifiedAccessor`), alongside `HB1.V(...)`:

```
SP1.wsp                 → the {freq,row,col} cube (or {sweep…,freq,row,col})
SP1.wsp(3, 13)          → one element traced over freq: the document's wsp(3,13) (Eq. 36), matched
                          by axis VALUE — never positional, since a positional slice would pin freq
SP1.idx("GATE")         → Real scalar, the probe's idx (from __WspProbes)
SP1.H0("GATE")  SP1.Y0("GATE")  SP1.ZG("GATE")  SP1.ZL("GATE")  SP1.LG("GATE")  SP1.F("GATE")
                        → the per-probe cubes; trailing sweep axes kept, as V(...) keeps them
```

An unknown label is an error that lists the probes present. `measure` lines using these evaluate
identically in the GUI and under `circuitrf sparam`, which runs the TestBench's measurements through
the one `MeasurementEvaluator`.

CLI: `circuitrf sparam` prints one line per probe after the S summary
(`WSProbe GATE idx=1  H0(f_lo)=… ZG(f_lo)=…`) and `--json` carries `wsprobes: [{label, idx}]`;
`-o out.npy`/`.mat`/`.txt` carry every cube; a Touchstone of a port-less run is a refusal naming
those spellings. `explain --analysis` lists each probe with its `idx` and both terminal nets, and
says `S-parameters: none (no ports)` when that is the case. `check` warns `wsprobe.shorted` when a
probe's two nets are the same net — the headless twin of the GUI's series-probe insertion cut.

## 4. Typo-register entries this half depends on

From the overview's §5; **implement the corrected form, never the printed one.**

- **T-1 — Eq. 40, `y12` and `y21`.** Both carry the `|P|` term with the wrong sign as printed. The
  correct values are `y12 = (AD − BC − A)/C` and `y21 = (AD − BC + D)/C`, which is what Eq. 44 and
  the Appendix E.13 code give. **Use Eq. 44.**
- **T-2 — Eq. 47, `z22`.** The first factor of its numerator is printed `iS/vP · vP/iP`; it is
  `iS/vS · vP/iP`. **Use Eq. 48.**
- **T-3 — Eq. 70.** `1/Y0 = z11 + z22 − z21 − y12` should read `− z12`. (`α + δ − β − γ = 1`.)
- **T-5 — Eq. 86/88.** The document contradicts itself on the asymmetric pure-feedback case
  (`CG = (1 + G2/G1)C` vs `(1 + G1/G2)C`). Only the symmetric case `G1 = G2`, `CG = CL = 2C`
  (Eq. 87) is a gate; the asymmetric case is not used.

One premise of the implementation brief that the physics overrode (recorded in
`src/Engine/RESOLVED.md`): the parallel resonator gate cannot use the series fixture's values. A
parallel negative resistance starts up only when its conductance exceeds the load's (`|R1| < RS`);
with `R1 = −20 Ω` against `RS = 10 Ω` the circuit is stable and the Kurokawa signature is absent.
The fixture uses `R1 = −5 Ω`.

---

## 5. Single-probe derived metrics (WSP-2)

Everything the document derives from **one** probe's 2×2 block of `wsp`, plus the small utility
functions of its appendix. The owner's instruction: these are **derived metrics**, never "scripts".
They keep the document's names and argument order exactly, so a designer who has read it can type
`wsp_yparam(SP1.wsp, SP1.idx("GATE"))` and get Eq. 44.

Probe *pairs* and the multi-probe functions (`wsp_yparam2`, `wsp_block_calc`, bifurcation, Ohtomo,
`wsp_ymatrix`, the envelope) are WSP-3's.

### 5.1 Where they live

`src/RfCore/Stability/`, framework-free, namespace `RfCore.Stability`, pure functions over
`Complex`. **The scalar core is the tested unit** (`tests/RfCore.Tests/Stability/WspNodalTests.cs`);
the cube-facing wrappers are a map over the frequency axis and any prepended sweep axes, and they
live in `src/Core/Expressions/Evaluator.Wsp.cs` — the expression engine's own file, which computes
nothing (overview D-6).

| File | Contents |
|---|---|
| `WspReduction.cs` | (WSP-1) `wsp_yparam`, `wsp_zparam`, the six defaults, `ZG`/`ZL`/`YG`/`YL` |
| `WspNodal.cs` | the document's names for the bidirectional immittances, the open-port immittances, all eight `wsp_loopgain` kinds and their S-parameter oracles, `wsp_nodal_gamma`, the normalised driving-point loci, the margin slot |
| `WspKurokawa.cs` | `wsp_unstable_freq_kurokawa`, `encirculations`/`enc` |
| `WspTransfer.cs` | `wsp_impedance`, `wsp_gain` (App. C, D) |
| `ImmittanceModels.cs` | `wsp_zsrc`, `wsp_zprc`, `z_to_*`, `y_to_*` |
| `GainDefinitions.cs` | `GainDEFs`, `_dB` |
| `WspRenorm.cs` | `wsp_rc_renorm_s`, `wsp_zo_renorm_s` |

`WspNodal` does not re-implement `ZG`, `ZL`, `YG` or `YL`; it names them and forwards to
`WspReduction`, which is what the engine computes a run's `ZG:<label>` cubes with. That is why
`wsp_ZG(SP1.wsp, SP1.idx("P1"))` and the run's own `ZG:P1` are **bit-identical** — one
implementation, two callers, held by test.

### 5.2 The built-ins, and what each returns

Every one is a built-in of `Evaluator` with the document's exact name and arity. Arguments: a `wsp`
cube (`{…, freq, row, col}`), a probe index (`SP1.idx("GATE")` or an integer literal), a 2×2 network
(`{…, freq, i, j}` — the same shape the `S` cube has, so `NetworkMetrics` and the Data Display's
network-parameter paths accept it), or a scalar-per-frequency cube (`{…, freq}`).

| Function | Equation | Returns |
|---|---|---|
| `wsp_yparam(wsp, idx)` | Eq. 44 | `{…, freq, i, j}` S |
| `wsp_zparam(wsp, idx)` | Eq. 48 | `{…, freq, i, j}` Ω |
| `wsp_H0(wsp, idx)`, `wsp_Y0(wsp, idx)` | Eq. 50/132, 52/134 | `{…, freq}` Ω, S |
| `wsp_ZG`, `wsp_ZL` | Eq. 67, 68 (E.14) | `{…, freq}` Ω |
| `wsp_YG`, `wsp_YL` | Eq. 76 (E.14) | `{…, freq}` S |
| `wsp_zop`, `wsp_yop` | Eq. 89, 90 (E.16) | `{…, freq}` Ω, S |
| `wsp_loopgain(Y, kind [, Z0])` | Eq. 92, 97, 99–104 | `{…, freq}` |
| `wsp_nodal_gamma(wsp, idx)` | E.7 | `{…, freq}` |
| `wsp_rY`, `wsp_iY`, `wsp_rH`, `wsp_iH` | M-rY … M-iH (§9) | `{…, freq}` Real, `[0, 1]` |
| `wsp_SM_Y0(wsp, idx)`, `wsp_SM_H0(wsp, idx)` | M-Eq. 9, 10 (§9) | `{…, freq}` Real, `[0, 1]` |
| `wsp_stability_margin(wsp, idx)` | `min(SM_Y0, SM_H0)` (§9) | `{…, freq}` Real |
| `wsp_sm_z(ZG, ZL)`, `wsp_sm_y(YG, YL)` | M-Eq. 9, 10 over any pair (§9) | same shape as its arguments, Real |
| `wsp_unstable_freq_kurokawa(T)` | Eq. 107/108 (E.12) | `{n}` Hz, possibly empty |
| `encirculations(SP)`, `enc(SP)` | E.3 | `{…, freq}` Real, the running count |
| `_dB(M)` | E.2 | `10·log10\|M\|` |
| `wsp_impedance(wsp, i, j [, stimulus])` | Eq. 196/197 (App. C), Eq. 37 | `{…, freq}` Ω |
| `wsp_gain(wsp, S, G, D)` | App. D code (T-14) | `{…, freq}` dB |
| `GainDEFs(GamS, SM, GamL)` | Eq. 203–208 (E.1) | `{…, freq, gaindef}` dB, labels `GT_dB`, `GP_dB`, `GA_dB`, `Gmax_dB` |
| `wsp_zsrc(R, C [, f])`, `wsp_zprc(R, C [, f])` | E.4 | `{…, freq}` Ω |
| `z_to_pr/pc/pl/sr/sc/sl`, `y_to_…` | E.11 | `{…, freq}` Ω / **F** / **H** |
| `wsp_zo_renorm_s(ZG, SP, ZL [, f])` | E.10 | `{…, freq, i, j}` |
| `wsp_rc_renorm_s(RG, CG, SP, RL, CL [, f])` | E.10 | `{…, freq, i, j}` |

Kind strings for `wsp_loopgain` are the document's: `BI` (bilateral/Tian), `UNI` (forward
circulator; **`FOR` is an accepted alias**, p. 68), `REV`, `HST`, `MB`, `MBR`, `GFT`, `GFTR`. `Z0`
defaults to 50 Ω and is read only by the two circulator kinds. `wsp_loopgain(…, "BI")` is the same
function the run's `LG:<label>` cube comes from.

The document's `wsp__yparam` and `wsp__zop` (double underscore, E.13/E.16) are its own redundant
re-implementations of its own built-ins; circuitRF has one implementation and registers the
single-underscore names only.

**Free, and the document does not mention it:** `wsp_yparam` returns a cube shaped exactly like an
`S` cube, so once WSP-4 exposes a probe's reduced network as a Data Display source, **Rollett K, μ,
μ′, |Δ|, MAG/MSG and the stability circles of the reduced two-port at a probe** come from the
`NetworkMetrics` code already shipped, with nothing new written.

### 5.3 ZG is not 1/YG, and that is the point

`ZG`/`ZL` are the impedances the two sides present under **series-voltage** stimulation; `YG`/`YL`
are the admittances they present under **shunt-current** stimulation. They agree only when there is
no feedback across the probe, `y12 = y21 = 0` (Eq. 79–82; §4.5, p. 56–57). Reading one as the
reciprocal of the other is the single most common misreading of the probe, so the doc-comments say
so. Only the sums are fundamental: `ZG + ZL = 1/Y0` (Eq. 69), `YG + YL = 1/H0` (Eq. 77).

`Zop`/`Yop` are Ochoa's open-loop port immittances (§4.7): the part of `1/H0` and `1/Y0` that
survives when the bilateral feedback is removed, and therefore the part that carries an instability
*between* two otherwise-unconnected blocks — Kurokawa's own case, where every loop gain reads zero
(p. 62–63). `1/H0 = (1 − LG)/Zop`, `1/Y0 = (1 − LG)/Yop`, `Zop/H0 = Yop/Y0`, `YG + YL = |Y|(ZG + ZL)`
(Eq. 93–95) are all gates on 3,000 random two-ports.

The document's note that `YG`/`YL` are "NOT outputs of the WSProbes" (E.14) describes its own
implementation, not the quantity. Here they are first-class functions and Data Display items.

### 5.4 The two circulator loop gains are verified through an independent path

Eq. 97 and Eq. 99 are implemented in the printed `ȳ = Z0·y` form. Their S-parameter equivalents
`LGR = S21 + S11·S22/(1 − S12)` and `LGF = S12 + S11·S22/(1 − S21)` (Eq. 96, 98, with
`S = (I − ȳ)(I + ȳ)⁻¹`) agree to 2e-15 on random two-ports and are kept as the test oracle only.

The document says the two are "numerically identical" to what an ideal circulator's third port would
reflect (p. 67). **That is now a test through a completely independent path**
(`WspNodalFunctionTests.C_…`): `hero1_probed.cnl`'s `WSProbe:P1` is replaced by circuitRF's own
`Circulator` model, ports 1 and 2 in the same node and port 3 brought out as a fifth analysis port,
and `S55` of that five-port is compared against `wsp_loopgain` of the probed run. **Measured:
`Direction="CW"` matches `"REV"` and `Direction="CCW"` matches `"UNI"`, to 4.7e-15 — against 0.18
the other way round.** The mapping was a prediction of the brief; it is a measurement now.

### 5.5 Kurokawa's start-up search, and why the step direction is the whole test

`wsp_unstable_freq_kurokawa(T)` forms `g(ω) = 1/T(ω)` on the sampled sweep and reports every
frequency where `Re(g) ≤ 0`, `Im(g) = 0` and `∂Im(g)/∂ω > 0` (Eq. 107 for `H0`, Eq. 108 for `Y0`).
Concretely: for consecutive samples with `Im(g_k) < 0 ≤ Im(g_{k+1})` — a **clockwise** crossing of
the negative real axis, decreasing argument through `π` — the zero of `Im(g)` is interpolated
linearly in `ω`, `Re(g)` is interpolated there, and the frequency is reported when that `Re(g) ≤ 0`.
A counter-clockwise crossing is the steady-state side of a *stable* resonance and is **not**
reported; the two stable versions of the document's own resonators (`R1` made positive) return empty
from both searches, which is the gate.

**Both `H0` and `Y0` must be checked** (§4.9–4.10): the series resonator shows the signature in
`1/Y0` only, the parallel resonator in `1/H0` only, and the gate measures exactly that — 1.59155 GHz
in one, nothing in the other, on a 10 MHz grid.

An empty result is an **empty cube**, not a zero (the document returns "a zero in case there are no
frequencies", E.12). It means "no crossing was sampled", not "the circuit is stable": **a sweep
coarser than the resonance can step over a crossing entirely**, the same caveat the group-delay page
carries for phase unwrapping. Swept data is refused with the document's own note ("does not work
with multi-index swept data", E.12), naming `at(...)` as the way to pin the sweep first — a list of
frequencies has no shape once there is more than one sweep point.

`encirculations`/`enc` is `−unwrap(phase(SP))/360`, returned as the running count over the sweep;
the net count is the last value rounded. The minus makes a **clockwise** encirclement positive,
which is the sign NDF and the Nyquist argument want (§8).

### 5.6 Deliberate deviations from the document

- **Base SI, not pF/nH.** The document's `y_to_pc` returns picofarads (`1e12·…`) and `y_to_pl`
  nanohenries. circuitRF returns **farads and henries**. Every trace and every derived metric here
  is base SI, and a scale factor hidden inside a function is exactly the class of defect that once
  produced a run at 2 Hz that looked entirely normal. The names are unchanged.
- **No epsilon in a denominator.** The document's `wsp_yop`/`wsp__zop` (E.16) add `1e-15`, and its
  `wsp_zsrc` (E.4) adds `1e-9` to the frequency to survive DC. circuitRF adds neither: a vanishing
  denominator returns NaN, which is what an exact short, an exact open, or a series capacitor at DC
  actually is (overview D-7).
- **A negative reference resistance is refused, not absorbed.** E.10 writes `abs(real(RG))` under
  its square root and carries on; circuitRF refuses with `wsprobe.renorm-negative-reference` — a
  reference impedance with `Re ≤ 0` is a wrong input, not a case, and the power-wave definition
  divides by `√Re(Z0)`.
- **`wsp_zo_renorm_s` calls the repository's own complex-reference renormalisation** (`RFNetwork.SToS`,
  the Z0-override path's) rather than transcribing E.10's matrix expression. The transcription lives
  in the test, as the oracle, and nowhere else.

### 5.7 Typo-register entries this half depends on

- **T-6 — Eq. 100 sign.** The single-probe Hurst loop gain is printed `LG_H = −y21·y12/(y11·y22)`
  while the two-block Hurst form (Eq. 141/150) has no minus. The minus follows the convention of
  Eq. 53 (`T ≡ −Vp/Vx`, `LG = −T`); it was not independently re-derived. **Implemented as printed**,
  and the doc-comment says so.
- **T-14 — Eq. 199 and Eq. 201.** As printed, both factors of `wsp_gain`'s ratio use the **drain**
  index — which would make the gain identically 0 dB. The App. D code
  (`GT = 10*log(mag(real(…D…)/real(…G…)))`) is unambiguous: drain in the numerator, gate in the
  denominator, and Eq. 198/200 agree. **The code is implemented**, and the gate asserts the answer
  is not 0 dB for exactly this reason. The document's `log` is `log10`.

### 5.8 What `wsp_impedance` actually measures, and why the stimulus probe matters

Off the diagonal the ratio is `V_j / iS_j` at the response probe. Whether that is the **load line**
or a **one-port impedance** is decided by where the stimulus is, and the document's phrase "common
stimulus" is doing real work:

- With the drive on the response probe's **L side**, its G side is source-free and the ratio is
  `−Z_G` — the negated impedance looking the generator way, with no dependence on the drive at all.
- With a **common** drive that makes both halves of a symmetric structure live, the response probe's
  L side contains an active device and the ratio is the effective impedance that device works into
  — the even-mode load line.

`combiner_even_mode.cnl` measures both on one circuit: driven from the probe that splits the common
input node, either drain reads `RM + 2·RL = 55 Ω` exactly (independent of gm, of the device output
resistance and of frequency), matching `combiner_half.cnl` — one branch with the common load doubled,
the classic even-mode reduction. Driven from the *other branch's* probe instead, the same function
reads `−1000 Ω`, that device's own output resistance negated.

**Eq. 37's shunt form and App. C's series form are the same number off the diagonal**, exactly — both
ratios cancel the same common stimulus at the same response probe. They differ only on the diagonal,
where the series form is `−ZG` and the shunt form is `1/YL`. The `stimulus` argument therefore exists
for the diagonal case and for fidelity to the document, not because the two disagree where the
function is normally used.

---

## 6. Multi-probe functions (WSP-3)

Everything the document derives from **two or more** probes: the probe pair (§5.1–5.4, E.5, E.6,
E.8, E.9), network bifurcation (§6), Ohtomo's global loop gains (§7), the reduced admittance
matrix and the probe-based NDF (§8), and the stability envelope (§9). Same home as WSP-2
(`src/RfCore/Stability/`), same registration discipline (`src/Core/Expressions/Evaluator.WspGlobal.cs`
computes nothing; it maps the scalar core over the cube's leading axes), same rule that every
function's doc-comment cites the document by equation.

| File | Contents |
|---|---|
| `WspMatrix.cs` | the four `N×N` blocks `VV`, `VI`, `IV`, `II` over a probe subset (§1 of the brief), and the small dense algebra the rest needs — products, an explicit partial-pivot LU, the determinant **in log form** |
| `WspPair.cs` | `wsp_yparam2` (+ its shunt-stimulus twin and the residual), `wsp_block_calc`, `wsp_block_breakout`, `wsp_fb_breakout`, `wsp_block_design`, `wsp_fb_design` |
| `WspBifurcation.cs` | `wsp_bifurcate(wsp, form, side [, probes])` and the four document aliases |
| `WspOhtomo.cs` | `wsp_loopgain_ohtomo`, `wsp_unstable_freq_loopgain` |
| `WspGlobal.cs` | `wsp_ymatrix`, `wsp_ndf` |
| `WspEnvelope.cs` | the rank-1 update, `wsp_terminate`, `wsp_loadpull`, `wsp_loadpull_unstable` |

### 6.1 Notation

`wsp` is stimulus-major. Writing the four `N×N` blocks, each indexed `[stimulus i, response j]`:

```
VV[i,j] = wsp(2i−1, 2j)      voltage at j  per series voltage at i        (Eq. 164)
VI[i,j] = wsp(2i−1, 2j−1)    current at j  per series voltage at i        (Eq. 166)
IV[i,j] = wsp(2i,   2j)      voltage at j  per shunt current at i         (Eq. 174)
II[i,j] = wsp(2i,   2j−1)    current at j  per shunt current at i         (Eq. 172)
```

A probe subset is the corresponding sub-blocks. **Sides:** for probe `i` the **G side** is the
network at node `vP_i` (current into it `−iS_i` under a series stimulus, `δ_ii·iP − iS_i` under a
shunt one) and the **L side** is the network at `vP_i + vS_i` (current into it `+iS_i`).

### 6.2 The two argument spellings the single-probe file did not need

The expression language has no list literal, so a **probe list** is an integer (one probe) or a
quoted string of comma-separated idx numbers or probe labels — `"1,3"`, `"GATE,DRAIN"` — and
defaults to every probe in idx order. Labels resolve through the `__WspProbes` metadata of the
analysis that produced the cube, which the evaluator finds by **reference equality** on the `wsp`
cube (`MeasurementContext.TryFindWspOwner`): `SP1.wsp` hands out the DataSet's own object. A sliced
or derived cube is a new object, so labels fall back to numbers and the envelope functions — which
need that same lookup for their precondition — refuse and say to pass the analysis' own cube.

A **Γ grid** is a single reflection coefficient, a cube of them, or the common case as a string
`"|Γ|:count"` (`"0.8:24"` — 24 points on the `|Γ| = 0.8` circle from `θ = 0°`, `"0.8:24@15"` to
start at 15°).

### 6.3 What each returns

| Function | Equation | Returns |
|---|---|---|
| `wsp_yparam2(wsp, idx1, idx2)` | Eq. 137–139 | `{…, freq, k}`, `k = 1…8` labelled `y11 … yf22` — the document's own `YP(1..8)` |
| `wsp_yparam2(wsp, idx1, idx2, "inner"\|"feedback")` | | that block as a 2-port `{…, freq, i, j}` |
| `wsp_yparam2_residual(wsp, idx1, idx2)` | — (circuitRF's) | `{…, freq}` Real |
| `wsp_block_calc(wsp, idx1, idx2 [, Z0])` | Eq. 142–151 | `{…, freq, k}`, `k = 1…16` labelled `s11 … LGM` |
| `wsp_block_breakout`, `wsp_fb_breakout(wsp, idx1, idx2 [, Z0])` | E.5, E.6 | 2-port |
| `wsp_block_design`, `wsp_fb_design(wsp, idx1, idx2 [, Z0 [, freq]])` | E.8, E.9 | 2-port |
| `wsp_bifurcate(wsp, form, side [, probes])` | Eq. 167/168/175/176 | `{…, freq, i, j}`, port axes valued by idx and labelled by probe |
| `wsp_YA`, `wsp_YF`, `wsp_ZA`, `wsp_ZF(wsp [, probes])` | §6 | same, with the document's sides (§7 below) |
| `wsp_loopgain_ohtomo(wsp, probes [, active = "G", Z0 = 50])` | Eq. 177–180 | `{…, freq, node}` |
| `wsp_unstable_freq_loopgain(G)` | p. 110 | `{n}` Hz, possibly empty |
| `wsp_ymatrix(wsp [, probes])` | Eq. 184–185 | `{…, freq, i, j}` S |
| `wsp_ndf(wsp_active, wsp_passive [, probes])` | Eq. 186 | `{…, freq}` |
| `wsp_terminate(wsp, idxS, YS, idxL, YL [, YSo, YLo])` | §9 | the re-terminated `wsp`, same shape |
| `wsp_loadpull(wsp, idxS, idxL, idx, gammaS, gammaL [, Z0])` | §9 | `{…, gS, gL, freq, env}`, `env` = `H0env`, `Y0env` |
| `wsp_loadpull_unstable(…)` | §9 + Eq. 107/108 | Real `{…, gS, gL, item}`: `unstable`, `unstable_H0`, `unstable_Y0`, `f1 … fK` |

A function the document returns two things from returns **one labelled axis** here, for the same
reason `GainDEFs` does: a measurement is one cube. `wsp_yparam2` is the document's own eight-vector;
the fourth argument is the way to get one block as a network. An index of 0 on either side of the
envelope leaves that side unpulled (its grid axis is one row labelled `unpulled`).

### 6.4 The probe pair

Two probes in the GEN → LOAD orientation of Fig. 40: the inner block `[Y]` between probe 1's **L**
terminal and probe 2's **G** terminal, the feedback block `[Yf]` between probe 1's **G** and probe 2's
**L**. From Kirchhoff on that configuration with the two series stimuli:

```
A = [[ VV11 + 1, VV12 ], [ VV21, VV22 ]]        B = [[ VV11, VV12 ], [ VV21, VV22 + 1 ]]
A·[y11; y12]   = [ VI11;  VI21 ]                A·[y21; y22]   = [ −VI12; −VI22 ]
B·[yf11; yf12] = [ −VI11; −VI21 ]               B·[yf21; yf22] = [  VI12;  VI22 ]
```

The `+1` on `A(1,1)` and on `B(2,2)` is the orientation, not a typo. Each unknown pair is a **row**,
so the true matrices come out with no transpose; on `two_block.cnl` `y21 = +gm` sits at (2,1) and
`(1,2)` is 0.

**The residual** (`wsp_yparam2_residual`, not in the document) solves the same two blocks from the
two **shunt** stimuli — the independent second set of equations with the same side bookkeeping —
and reports `max|Y_series − Y_shunt|` relative. Round-off (5e-16) when the pair brackets a two-port;
0.37 when a resistor joins the inner region to the outside around the probes. **A shunt from an
inner node to ground does not raise it** (4.7e-16): ground is not a coupling path, and such an
element is simply part of the inner block's own `y11`. The brief's suggested test was that shunt; the
gate uses the bypass and asserts the shunt small, because the distinction is the finding.

`wsp_block_calc`'s `{9}` and `{10}` are computed as the **determinant ratios** of §5.4 — `FB = |Y +
Yf| / |Yo + Yf|` with the inner block's controlled source zeroed (`y21 → y12`), and the same with the
feedback block passivated (`yf12 → yf21`, typo register T-7) — while `{13}` and `{14}` are Eq. 148
and Eq. 149 transcribed literally. `1 − {9} == {13}` and `1 − {10} == {14}` are therefore
identities between two derivations, held to 1e-10 on random blocks and on the real circuit, rather
than definitions. `{16}` is Eq. 151's right-hand side (T-8).

**E.8/E.9 and `wsp_zo_renorm_s` are not the same number.** `wsp_block_design` absorbs the parallel
capacitance of each side into the network and renormalises to the real parallel resistance;
`wsp_zo_renorm_s` renormalises to the complex `Z = R ∥ 1/jωC` with power waves. Their waves differ
per port by `e^{∓jφ}`, `φ = atan(ωCR)`, so `S_rc = D*·S_zo·D*` with `D = diag(e^{jφ})`: **equal
magnitudes, phases that differ by a known port factor**, held to 1e-12 with the factor put back. The
brief's "equals to 1e-12" is true of the magnitudes only.

## 7. Bifurcation and sides

With `N` probes all oriented the same way the network splits into the G-side and the L-side
subnetwork, and each is an `N`-port recoverable from `wsp`. From the bookkeeping of §6.1, stacking
the stimuli as columns:

```
Y-form (series stimuli):   Y_G = −( VV⁻¹ · VI )ᵀ            Y_L = ( (VV + I)⁻¹ · VI )ᵀ
Z-form (shunt stimuli):    Z_L =  ( II⁻¹ · IV )ᵀ            Z_G = ( (I − II)⁻¹ · IV )ᵀ     (T-12/T-13)
```

**The trap (overview T-9, T-15).** The document's §6 code computes the bracketed products **without
the transpose**, and its Y-form and Z-form put the "active" network on **opposite sides** of the
probes. For a non-reciprocal network the document's `wsp_YA` is therefore `Y_Gᵀ`, and its `wsp_ZA`
is `Z_Lᵀ` — a different subnetwork from `wsp_YA`'s. Every use the document makes of these matrices
(determinants, principal minors, a diagonal cofactor) is transpose-invariant, so its results stand; a
designer reading `y21` of a block is not. circuitRF's primitive is **side-explicit**,
`wsp_bifurcate(wsp, "Y"|"Z", "G"|"L")`, returns the true matrix, and registers the document's names
as aliases with the document's sides:

| Document name | = | side |
|---|---|---|
| `wsp_YA` | `wsp_bifurcate(wsp, "Y", "G")` | G |
| `wsp_YF` | `wsp_bifurcate(wsp, "Y", "L")` | L |
| `wsp_ZA` | `wsp_bifurcate(wsp, "Z", "L")` | **L** |
| `wsp_ZF` | `wsp_bifurcate(wsp, "Z", "G")` | **G** |

The gate that catches both a lost transpose and a swapped side is **same side, both forms, must
agree**: `Y form of G == inverse(Z form of G)` and likewise L, on a non-reciprocal fixture
(`three_probe.cnl`: three VCCS couplings one way on the G side, `Y_G[2,1] = gm`, `Y_G[1,2] = 0`),
held to 1e-10 at every frequency, and on random non-reciprocal synthetic pairs. The one-probe case
reduces to WSP-2 (`Y_G = YG`, `Y_L = YL`, `Z_G = ZG`, `Z_L = ZL`).

**A subnetwork is well defined only when every listed probe has it on the same side** (§6, p. 100).
Nothing can check that from `wsp`; two probes whose named sides meet give a singular system or a
matrix describing nothing. Two probes whose same-side terminals share a node give a singular
`Y` for that side (two ports on one node have no admittance matrix), which is why the three-probe
fixture puts each probe on its own node.

**`wsp_ymatrix` is different in kind from a bifurcation**: `Z = IVᵀ` (Eq. 184; the transpose is
overview D-9) is the impedance matrix of the **whole** network at the probe nodes under shunt
stimuli with the probes closed, so it contains both sides of every probe — on the three-probe
fixture it is `Y_G + Y_L` exactly, both sides in parallel at the same nodes. It is the matrix the NDF
wants and the matrix the envelope modifies. Gate (c) also compares it against the engine's own
`S → Y` with Terms at the same nodes: **at `Z = 1e9 Ω` the two disagree at 1.5e-8**, not because of
the Term's `1e-9 S` (nothing is subtracted; `S → Y` at a reference yields the network's own
admittance) but because the port waves then sit at `1 + S ≈ 1e-8` and lose eight digits. At `1e6 Ω`
they agree to 1e-8 with the same conversion.

**Ohtomo** (§7): `M = SP·SA − I` from the two sides as scattering matrices at `Z0`, and
`G_i = 1 + |M_{N−i+1}| / |M_{N−i}|` with `M_{N−i+1}` the trailing principal submatrix on rows and
columns `i … N` (Eq. 180 corrected, T-10; `M_0 ≡ 1`). Telescoping gives `Π(G_i − 1) = det(M)`, and
since `det(M)` is the Nyquist determinant of the closed loop of travelling waves, **the sum of the
encirclements of `+1` by the `G_i` equals the encirclements of the origin by `det(M)`** whatever the
probe order or the choice of active side — measured on the three-probe fixture over 801 points:
0.04047 turns forward, 0.04047 reversed, 0.04047 for `det(M)`, with the individual `G_i` different
in the two orders. `N = 1` gives `ΓP·ΓA`, Jackson's index, and on the series resonator
`wsp_unstable_freq_loopgain(G_1)` reports 1.59155 GHz for `R1 = −20 Ω` and nothing for `+20 Ω`.
The oscillation test on a loop gain is `|G| ≥ 1` with `∠G = 0` crossed **clockwise** — `Im(G)` from
positive to negative — the mirror of the Kurokawa search's rule around the critical point `+1`.
Ohtomo assumes each subnetwork is stable on its own (§7, p. 108: one side purely active with no
terminations that could form a loop, the other purely passive); nothing here can check that, and the
doc-comment says so.

`wsp_ndf` takes the determinant of `Z = IVᵀ` from an ordinary run and from a passivated one in
**log form** — the sum of the logs of the LU pivots plus `π` per row swap — and exponentiates only
the ratio, so a 30-probe matrix at 1,000 frequencies neither overflows nor underflows. It is the
probe-based route to NDF and the cross-check for WSP-6's native one.

## 8. Envelope by rank-1 update

The document's §9 reads the starting source and load terminations from probes placed at them, swaps
them for pulled ones in the 3×3 reduced `Y` and recomputes `H0` at a suspect node through a
cofactor (Eq. 187–191, with Eq. 191's denominator garbled as printed — T-11). The same physics gives
more with less algebra: **adding a shunt admittance `ΔY` at a probe node is a rank-1 update of the
whole `wsp`**, because every response to an injection there is already in the matrix. With
`h = wsp(2S, 2S) = H0_S`, for every row `r` and column `c`:

```
G node:  wsp'(r, c) = wsp(r, c) − wsp(r, 2S) · ΔY · wsp(2S, c) / (1 + ΔY·h)                    (Sherman–Morrison)
L node:  wsp'(r, c) = wsp(r, c) − [wsp(r, 2S) + δ(r, 2S−1)] · ΔY · [wsp(2S, c) − δ(c, 2S−1)] / (1 + ΔY·h)
```

**The L-node form is the brief's G-node statement plus two corrections the physics requires**, and
it is needed because the load probe faces its Term with **L**. Under the probe's own series stimulus
(row `2S−1`) the L-node voltage is `vP + vS`, one more than `wsp(2S−1, 2S)`; and a current injected
at the L node does not flow through the probe, so the probe's own branch-current response (column
`2S−1`) is one less than the G-node injection's. Every other entry is identical, because the two
terminals are one node. Applied once for the source probe (`ΔY_S = YS − YSo`, G node) and once, on
the result, for the load probe (`ΔY_L = YL − YLo`, L node), this yields the **complete `wsp` of the
re-terminated circuit** — every `H0'`, `Y0'`, `ZG'`, `ZL'`, loop gain and Ohtomo gain — with no
cofactor bookkeeping and no assumption about which node is "suspect". Gate (e) compares it against a
**re-run with the Terms changed** (`ΓS = 0.5∠60°`, `ΓL = 0.3∠−120°`) on a two-stage amplifier with
global feedback: **4.6e-14 worst relative entry error over all 36 entries**, the load probe's own row
and column included, and Eq. 191's cofactor form of `H03'` (T-11) equals `wsp'(6, 6)` to 1e-10.

`YSo`/`YLo` default to `1/ZG` of the source probe and `1/ZL` of the load probe (§9: "either known
or determined using the bidirectional impedance calculations provided directly from the source and
load WSProbes"). **Precondition, checked** (`wsprobe.envelope-probe-not-at-termination`): the source
probe must sit directly at its termination with G facing it, the load probe with L facing it, so
that the termination is a pure shunt at that node and the bidirectional impedance on that side
equals the `Term`'s declared `Z` to `1e-6` relative at every frequency. The engine records each
probe's neighbouring top-level `Term`/`Port` — one shunting the G node to ground, one shunting the L
node — in a **`__WspTermZ` `{probe, side}` metadata cube** (NaN where there is none), which passes
through a sweep unstacked like `__WspProbes`. A probe with nothing at that node refuses; a probe
whose `ZG` differs from the declared `Z` refuses naming both numbers — which is what feedback across
the probe (§9, p. 119) or a series element between the probe and its Term produces, and is the
intended outcome.

`wsp_loadpull` runs the update over two Γ grids and reads `H0'` and `Y0'` at the suspect probe —
both, because §4.10's pole masking applies under mismatch as much as at nominal.
`wsp_loadpull_unstable` runs WSP-2's Kurokawa search on `1/H0'` and `1/Y0'` at every grid point.
On the series resonator with a Term at 10 Ω and `R1 = −5 Ω` (stable at 10 Ω): a load-pull over
`|Γ| = 0.9` finds exactly the arc on which `Re ZS(θ) < 5 Ω` unstable on the `1/Y0'` side, at the
frequency where `X(ω) = −Im ZS(θ)` — Eq. 109 with `RS → ZS(θ)` — to four digits, and nothing on the
rest of the circle. Two things the brief stated that the arithmetic overrides: at `|Γ| = 0.8` the
**whole circle is stable** (min `Re ZS` is 5.56 Ω at `Γ = −0.8`, above the 5 Ω the negative
resistance can overcome), and the reported frequency tracks `f0` only where `Im ZS ≈ 0`; elsewhere
the load reactance detunes the resonator by up to a gigahertz and the closed form is what it tracks.
The `1/H0'` side also reports crossings at some loads, at frequencies of its own (at `θ = 130°`,
0.6037 GHz beside `1/Y0'`'s 0.5906): the union is what the document's method takes, and the gate
prints that side rather than asserting it.

---

## 9. The stability margin (WSP-9)

The 2023 document's driving-point functions are the rigorous nodal stability metric, and they have
**units**. Their absolute trajectory in the complex plane is set by the node's impedance level — two
FETs of very different periphery tuned to the same Rollett `K` have loci differing by orders of
magnitude — so `1/H0` on a polar chart gives a binary answer, a Kurokawa crossing or not, and no
sense of *how close*. The published margin is the way out.

> **[M]** T. A. Winslow, "A Novel Stability Margin for Transfer Functions," *Proc. 19th European
> Microwave Integrated Circuits Conference (EuMIC)*, Paris, Sept. 2024, pp. 291–294,
> DOI 10.23919/EuMIC61603.2024.10732614. Its equations are cited `(M-Eq. n)` and its four unnumbered
> displays `(M-rY)`, `(M-iY)`, `(M-rH)`, `(M-iH)`.
>
> **[E]** T. A. Winslow, "Stability Envelope Using Nodal Transfer Functions," *Proc. 20th EuMIC*,
> Utrecht, Sept. 2025, pp. 254–257, DOI 10.23919/EuMIC65284.2025.11233915. Cited `(E-Eq. n)`.

### 9.1 The definitions

Both driving-point functions are sums of bidirectional immittances — `1/H0 = YG + YL` (M-Eq. 1),
`1/Y0 = ZG + ZL` (M-Eq. 2) — and Kurokawa's condition on each (M-Eq. 7, 8) is a statement about the
**relative** size of the two halves: the real parts cancelling, the imaginary parts cancelling.
Normalising each half against the other gives four bounded, unitless proxies:

```
rY = 0                                   if  Re ZG + Re ZL ≤ 0          (M-rY, third case — FIRST)
   = ½ (1 + Re ZL / Re ZG)               if |Re ZG| ≥ |Re ZL|
   = ½ (1 + Re ZG / Re ZL)               otherwise

iY = ½ (1 + Im ZL / Im ZG)               if |Im ZG| ≥ |Im ZL|           (M-iY)
   = ½ (1 + Im ZG / Im ZL)               otherwise

rH, iH:  the same two functions over Re YG, Re YL and Im YG, Im YL      (M-rH, M-iH)

SM_Y0 = ½ (rY + iY)                                                     (M-Eq. 9)
SM_H0 = ½ (rH + iH)                                                     (M-Eq. 10)
```

Each proxy is in `[0, 1]`, so both margins are; the paper reads them in dB. `SM_Y0` is the margin on
the **series** stimulus and `SM_H0` on the **shunt** one, and **both are required** ([M] §IV): a
series-resonant instability is seen by one and a parallel-resonant one by the other, and "in rare
exceptional circuits" one of them fails to detect. That is §4.10's pole masking in margin form, and
§9.3 shows it on a circuit with one loop in it. `wsp_stability_margin` is their elementwise minimum.

The library is `src/RfCore/Stability/WspMargin.cs`, called by the engine (the `SM_Y0:<label>` and
`SM_H0:<label>` cubes) and by the built-ins through the same `WspMargin.Of`, so a run's cube and a
trace card's function of the same probe are bit-identical (overview D-2).

### 9.2 Conventions [M] leaves open — decided here, each held by a gate

These are typo-register entry **T-18**. Every one of them is a convention, not a transcription.

- **(a) The `≤ 0` case takes precedence.** [M] lists it third. With `Re ZG = 5`, `Re ZL = −10` the
  magnitude branch alone gives `0.25`; the sum is `−5`, Kurokawa's real-part condition holds, and
  the margin **must** be 0 there. The sum is tested first.
- **(b) Equal magnitudes.** Both magnitude branches give the same value, so `≥` on the first branch
  changes nothing.
- **(c) Both parts exactly zero** is **0.5**. The function is genuinely discontinuous at the origin
  (the limit is 1 along `Im ZL = Im ZG → 0`, 0 along `Im ZL = −Im ZG → 0`, 0.5 along either axis), so
  any value is a convention; 0.5 is what one purely resistive side gives and is continuous with it.
- **(d) NaN propagates.** A degenerate node (`wsprobe.degenerate-node`) gets a NaN margin, never a 0
  that reads as an instability.
- **(e) dB is `20·log10`** (overview D-16). The margin is a unitless ratio bounded by 1 and the Data
  Display applies `20·log10` to every unitless magnitude, so the ordinary `dB(...)` is the one to
  use and no `_dB` variant is added. [M] §IV's rule of thumb — investigate any sudden decrease below
  **−15 dB** — is **0.178** linear under this convention (it would be 0.032 under `10·log10`, and
  both numbers are printed so no reader is misled silently).
- **(f) Kurokawa's third condition is not in the margin.** `∂Im/∂ω > 0` is what separates a start-up
  from a benign crossing; the margin measures distance to the first two only.
  `wsp_unstable_freq_kurokawa` stays the *detector* and the margin is the *distance*, and every
  place one is reported the other is beside it.

### 9.3 What the numbers mean

Properties the definition guarantees, all gated:

- **Unitless in the sense that matters:** scaling `ZG` and `ZL` by the same positive real leaves
  `SM_Y0` unchanged. That is the cross-node comparability [M] §I wants.
- **`SM_Y0 = 1` iff `ZL = ZG` with `Re > 0`.** A **conjugate match is not the top of the scale**:
  `ZL = conj(ZG)` gives `rY = 1`, `iY = 0`, `SM_Y0 = 0.5 = −6.02 dB`. A designer who expects a
  matched node to read 0 dB will read −6 dB as a problem, so this sentence belongs in the user docs.
- **The −12 dB floor.** If `Re ZG > 0` and `Re ZL > 0` then `rY ∈ (0.5, 1]` and `SM_Y0 ≥ 0.25`
  (−12.04 dB). Contrapositive, and the interpretive rule: **a margin below −12 dB certifies that one
  side of the node presents negative resistance at that frequency.**
- **`SM_Y0 = 0` iff `Re(ZG + ZL) ≤ 0` and `Im ZL = −Im ZG`** — the two static Kurokawa conditions on
  `1/Y0`. At a frequency the search reports, `rY` is 0 exactly and `iY → 0` as the grid refines.
- `SM_Y0` and `SM_H0` are **not** functions of each other, even with zero feedback.

**The fixture that shows a resonance splits the reactance across the probe.** WSP-1's own resonator
puts the probe at the Term, so `ZG` is purely real, `Im ZG = 0`, and `iY ≡ 0.5` by (c): the margin is
**flat** (0.375 for `R1 = −5 Ω`) and shows no resonance at all. That is the definition, not a defect
— the proxy normalises one reactance *by the other*, and a resistive side has none to offer, so **a
probe against a purely resistive termination reads the resonance on the other side's margin only**.
`testdata/wsprobe/margin_split_resonator.cnl` splits it:

```
Term RS = 10 Ω ── L1 = 1 nH ──[ WSProbe, G left ]── C1 = 10 pF ── R1 ── ground
ZG = RS + jωL1      ZL = R1 − j/(ωC1)      f0 = 1/(2π√(L1C1)) = 1.5915 GHz
```

On its 0.5–3 GHz, 2001-point grid (measured; the engine matches the closed form to 2.5e-16):

| `R1` | `rY` | `SM_Y0` min | `SM_H0` min | `Re(YG + YL) ≤ 0` |
|---|---|---|---|---|
| −5 Ω (stable at 10 Ω) | 0.25 | 0.12509 (−18.06 dB) at 1.5913 GHz | 0.10175 (−19.85 dB) at 1.7337 GHz | 1.7337–3.0 GHz |
| −20 Ω (unstable) | 0 | 9.4e-5 (−80.53 dB) at 1.5913 GHz | 0.15852 (−16.00 dB) at 1.8600 GHz | 1.8613–3.0 GHz |

Three things to read off. The `SM_Y0` notch sits at `f0` and its depth is set by `rY`, the
negative-resistance ratio; at −20 Ω it is `½·iY` exactly and reaches the grid's own resolution of
zero. `SM_H0`'s minimum is at a **different** frequency — the one where `Re(YG + YL)` changes sign,
although the impedance sum is positive everywhere for −5 Ω — which is the pole masking, visible on
one loop. And the **stable** −5 Ω case already sits below [M]'s −15 dB rule at `f0`: a node one
negative-resistance step from oscillating *has* little margin, and the threshold message fires on it
by design.

### 9.4 What the engine and the verbs report

- **Two more default cubes per probe**, beside the six of §2.2: `SM_Y0:<label>` and `SM_H0:<label>`,
  Real over `{freq}`, from `WspMargin.Of` on the same `WspProbeQuad`. `SP1.SM_Y0("GATE")` resolves
  like `SP1.H0("GATE")`, and a parametric sweep stacks them as it stacks `H0`.
- **The run summary** (`circuitrf sparam`, and the GUI's own line) appends each margin's minimum over
  the sweep and its frequency, in dB. `--json`'s `wsprobes[]` rows gain `smY0Min`, `smY0MinHz`,
  `smH0Min`, `smH0MinHz` — **linear**, because dB is a display convention and a document carries the
  number.
- **`MarginThreshold=<dB>` on the analysis line** (default **−15**, [M]'s own rule;
  `MarginThreshold=none` disables) produces one **Info** note per probe whose `min(SM_Y0, SM_H0)`
  falls below it, `wsprobe.margin-below-threshold:<label>`. It is a note and not a warning because
  the −5 Ω resonator above is stable and fires it: the message is "look here", not "this is wrong".
  `explain --analysis` lists the effective value beside the probe list, since the default is not
  written in the document; `check` says nothing new, because the margin is a run result and not a
  document property.

### 9.5 The envelope: margin and NDF under mismatch, with no re-simulation

[E]'s thesis is that the margin swept over source and load VSWR is a stability envelope that tracks
the NDF and is more informative than it, because the NDF is binary and the margin is a distance.
WSP-3's rank-1 re-termination already gives the **complete** `wsp'` of the mismatched circuit, so
both halves of that comparison are post-processing:

| Function | Returns |
|---|---|
| `wsp_loadpull_margin(wsp, idxS, idxL, idx, gammaS, gammaL [, Z0])` | Real `{…, gS, gL, freq, env}`, `env` = `SM_Y0env`, `SM_H0env`, `SM` |
| `wsp_loadpull_margin_env(…)` | Real `{…, gS, gL, item}` — `SMenv`, `SMenvHz`, `SM_Y0min`, `SM_Y0minHz`, `SM_H0min`, `SM_H0minHz` |
| `wsp_loadpull_ndf(wsp, wsp_passive, idxS, idxL, probes, gammaS, gammaL [, Z0])` | Complex `{…, gS, gL, freq}` |
| `wsp_loadpull_ndf_enc(…)` | Real `{…, gS, gL}`, the net clockwise encirclement count |

**Each pair is two functions rather than one for a reason that is not style.** One call returns one
cube; a cube is single-kind and has one rank. `SMenv` — the one number per termination [E]'s Fig. 6–9
plot against phase — is a *minimum over frequency*, so it cannot share a cube with the
frequency-resolved margins; and an NDF locus is Complex while its encirclement count is Real. This
is the same split `wsp_loadpull_unstable` already is from `wsp_loadpull`.

The immittances the margin is taken of are `Z^R_G`, `Z^R_L`, `Y^R_G`, `Y^R_L` of E-Eq. 11/12, **with
T-16's correction**: E-Eq. 11 as printed swaps the G and L numerators (the same swap as T-4), and the
gate asserts the printed form disagrees by O(1) rather than leaving that as a note.

`wsp_loadpull_ndf` applies the same terminations to the active and the passivated matrix and takes
the determinant ratio — exact, because both are the exact `wsp` of the re-terminated network.
[E] re-ran a full NDF sweep per grid point; circuitRF does not have to. **The document's own caveat
applies** (p. 112–113): this is the *reduced* NDF over the probed nodes, complete only if the probe
set covers every node that can hide a pole. §9.7 shows what that costs when it is not.

### 9.6 [E]'s reduction is the independent oracle for the rank-1 update

E-Eq. 1–12 are implemented **in the test project only**, as a second derivation of WSP-3 §6's result
from the other side. With the suspect probe's branch OPEN the circuit is a 4-port — port 1 the source
probe's node, port 2 the load probe's node, ports 3 and 4 the suspect probe's two terminals — and
four stimuli the `wsp` matrix already carries drive it. Stacking the port voltages and currents as
`V_m` and `I_m` gives `I_mᵀ = Y·V_mᵀ`, hence `Y = (V_m⁻¹ I_m)ᵀ` (E-Eq. 4) — **the transpose is
required**, which is overview D-9 arriving independently. E-Eq. 5 swaps the terminations on the
diagonal exactly as Eq. 191 does, E-Eq. 6–8 reduce onto the suspect probe's two terminals (the two
terminated ports carry no external current, so [E]'s `d_ij/D` cofactor spelling is the Schur
complement), and E-Eq. 9–12 read the six quantities off the resulting 2×2 `R`, which is the reduced
**admittance** two-port at the probe.

Measured on the non-reciprocal two-stage amplifier at `ΓS = 0.5∠60°`, `ΓL = 0.3∠−120°`: the two
routes agree to **2.7e-15** relative on all six quantities; **E-Eq. 11 as printed is off by 1.17
relative** (T-16); and the un-transposed `V_m⁻¹ I_m` differs from the core `Y` by 0.086 (D-9).

### 9.7 What the envelope can and cannot see — Ohtomo's Type-A

`testdata/wsprobe/ohtomo_type_a.cnl` is the two-device parallel amplifier of M. Ohtomo, *IEEE Trans.
MTT* vol. 41 no. 6/7, 1993 — the topology [E] sweeps — redrawn with circuitRF's own element values
(overview D-15), probed S / G / L, with the balancing resistor `Rb` across the two gates. Measured:

- At nominal 50 Ω with `Rb = 30 Ω`, the Kurokawa search finds nothing at the suspect probe, the
  reduced NDF makes no encirclement, and `SM_Y0` bottoms out at −13.30 dB at 6.92 GHz.
- **`Rb` is load-bearing.** Raising it to 100 Ω — weakening the odd-mode damping and changing nothing
  else — makes the search report a start-up at **6.090 GHz**, and `SM_Y0` reads **−53.1 dB** there
  on the fixture's own 991-point grid (−80.5 dB at 1,981 points: the notch is narrower than the step,
  the same sampling caveat §9.3 carries).
  That is §9.2(f)'s two halves, the detector and the distance, on one circuit.
- **The reduced NDF over the S/G/L probe set reads zero encirclements at that start-up**, and adding
  a fourth probe on the *other* gate — nothing else changed — makes the same NDF read 2. The odd mode
  is differential across the two gates and only one carried a probe; this is the document's p. 112–113
  caveat, demonstrated rather than quoted.
- **No termination at `ρ = 0.9` on either side reaches it**, and the margin envelope moves by only
  ~20 dB across the circle. That is not a defect in the envelope: **the odd mode sees both ports as
  virtual grounds, so a source/load stability envelope is structurally blind to it** — which is
  Ohtomo's own thesis and the reason a Type-A amplifier needs `Rb` rather than a better match. An
  instability a VSWR sweep can expose has to live in a port-coupled path.

The line values of [E] Fig. 4 and the eleven FET element values of [E] Fig. 5 are not held here, so
this circuit is the same *topology* with different elements and its numbers were never going to be
[E]'s. The ρ ladder is measured and printed by the gate rather than asserted.

### 9.8 What was retired

Overview D-12 promised that the published margin would replace circuitRF's own normalised
driving-point loci the day it arrived. It has. The two placeholder built-ins, their library
functions, the margin's own refusal and its diagnostic key are **gone**, along with their tests and
the docs rows that described them. Two normalised stability quantities beside each other — one
Winslow's, one ours — is exactly the confusion the notation rule of §1 exists to prevent, and the
loci carried nothing the `1/H0`, `1/Y0` polar traces and the margin do not carry between them.

**A source scan of `src/` and `docs/design/` for the retired spellings is a gate**, which is why
this section does not print them; `src/RfCore/RESOLVED.md` names them once, for anyone reading a
diff that still contains them.

---

## 10. Large-signal small-signal solve (WSP-5)

Everything above computes `wsp` from an **S-parameter** analysis, which linearises the nonlinear
devices at their DC operating point. That answers whether the design is stable when nothing is
driving it. It cannot answer the question a power-amplifier designer actually asks — *is it stable at
the drive level it ships at* — and it cannot see a **parametric** instability at all, because that one
lives at `ω0/2` and an unpumped circuit has no `ω0`.

The 2023 document says the probe serves harmonic balance as it serves a linear analysis (§4.2, p. 43:
"Linear analysis, harmonic balance, or AC analysis can all be accommodated"; §7, p. 111: Ohtomo's
loop gains "extended into the nonlinear regime using Harmonic Balance"). What that takes is one new
engine capability, and **every derived metric of §5–§9 then applies to the resulting cube with no
change at all**, because they map over the leading axes and neither knows nor cares which analysis
produced the matrix.

> **Method references.** S. A. Maas, *Nonlinear Microwave and RF Circuits*, 2nd ed., ch. 3 (the
> conversion matrix); A. Suarez, *Analysis and Design of Autonomous Microwave Circuits* (2009),
> ch. 1–2 (large-signal stability as a small-signal perturbation of the periodic steady state) —
> the document's own [11], [24], [25].

### 10.1 What is computed

The circuit is driven hard by its HB tones. At the converged operating point the nonlinear devices
are **periodically time-varying** conductances and capacitances, and the probe injects a vanishingly
small series voltage or shunt current at a frequency `ω_ss` that is in general **not** on the HB grid.
Because the linearisation is time-varying, a stimulus at `ω_ss` produces a response at every sideband
`ω_k = ω_ss + k·ω0`, `k = −K_ss … K_ss`; the `wsp` entries are the responses **at `ω_ss` itself** —
the `k = 0` sideband — which is the large-signal counterpart of the S-parameter `wsp`.

The directive spells the sweep like the S-parameter one and honours its unit rules
(`cli.md` §10A.3):

```
analysis HB1 type=hb Tone=RFfreq MaxHarm=7 \
     SSStart=0.1 SSStop=10 SSNpts=991 SSUnit=GHz  [SSStep=…] [SSLog=true] [SSMaxHarm=K_ss] \
     [MarginThreshold=<dB>|none]
```

`SSUnit` applies to start, stop and step alike — the one-unit-for-the-whole-sweep rule, because a
bare coefficient read as base SI is how a sweep once ran at 2 Hz and looked entirely normal.
**Absent `SSStart`/`SSStop` means no small-signal solve at all**, and the run is byte-identical to one
from before this existed; a WSProbe with no `SS*` keys is not an error — the probe is transparent
(§1) and the run says once that it carried no transfer functions.

### 10.2 The conversion matrix

`N_int` interface nodes (the nonlinear-facing nodes), `K_ss ≤ K` sidebands, unknowns the interface
voltages at every sideband, `n_c = N_int·(2K_ss + 1)` complex:

```
J_ss[(n,k), (m,l)] = δ_kl · Y_NN(ω_k)[n,m]
                   + G⁽²⁾[n,m, k−l]
                   + j·ω_k · C⁽²⁾[n,m, k−l]
                   + Σ_w H[w](ω_k) · Dw⁽²⁾[n,m, k−l]
```

No half-amplitude weights, no real-split, no DC special cases: at `ω_ss ≠ 0` every sideband is an
ordinary complex unknown. `src/Engine/HarmonicBalance/HbSmallSignal.cs`.

**The two-sided coefficient rule is a frozen convention.** `HbFft` and `HbApft` report FULL-amplitude
one-sided phasors — the DC bin divided by `N`, an AC bin by `N/2` — so an AC bin is *twice* the
two-sided Fourier coefficient of the real waveform the conversion matrix is written in:

```
G⁽²⁾[0] = G[0]        G⁽²⁾[k] = G[k]/2  (k > 0)        G⁽²⁾[−k] = conj(G[k])/2
```

and likewise `C` and every `w ≥ 2` bucket. Getting the factor of two wrong is **invisible at low
drive** and doubles every mixing term at high drive. It is also why the spectra are the solve's own
arrays rather than a re-evaluation: after a drive ramp, re-evaluating the devices would linearise at
whichever iterate happened to be left in `V`, not at the one the run is reporting.

`Y_NN(ω_k)` comes from the linear extractor at `ω_k`; for `ω_k < 0` it is the **complex conjugate** of
`|ω_k|`'s, because the linear network is real in the time domain — a theorem, not an approximation —
and no model in circuitRF is ever evaluated at a negative frequency
(`src/Engine/HarmonicBalance/CLAUDE.md`). `ω_k = 0` exactly takes the DC formulation.

### 10.3 The probe injections

Per probe and per injection — a unit `vS` in its branch (`−` at G, `+` at L) and a unit `iP` into its
G node, §3's exact stamps, with every independent source OFF because this is a perturbation of an
already-solved operating point:

1. the linear partition at `ω_ss` with the injection alone gives the open-circuit interface voltages,
   and `I_src = −Y_NN(ω_ss)·V_oc` is the Norton excitation the conversion system takes at its `k = 0`
   block and nowhere else;
2. `J_ss·V = −RHS` for the interface voltages at every sideband;
3. `I_nl,0 = −(Y_NN(ω_ss)·V[·,0] + I_src)`, the balance `Y·V + I_src + I_nl = 0` read at `k = 0`;
4. the linear partition again with the injection **and** `I_nl,0` at the interface, read at every
   probe as `iS_j = x[br_j]` and `vP_j = x[nG_j − 1]`.

The factorisation at `ω_ss` and the dense factorisation of `J_ss` are shared by all `2N` right-hand
sides of that probe frequency. Per probe frequency, structurally: `2K_ss + 2` linear-partition
extractions (one per sideband plus the handle the back-solves share — factorisations *or* cache
hits), one dense factorisation, `2N` dense solves, `4N` sparse back-solves. WSP-8 lowers those; this
path is its oracle, so it stays available.

### 10.4 The cubes

Per operating point: `wsp {ssfreq, row, col}` and the eight per-probe defaults over `{ssfreq}` —
§2's six through `WspReduction` and §9's `SM_Y0`/`SM_H0` through `WspMargin`, the same library calls
the S-parameter path makes (`src/Engine/WspCubePacker.cs` is one implementation serving both, so the
two analyses cannot disagree about a cube name, a unit, the NaN policy or a diagnostic's wording).
Under a drive sweep `ParametricSweepEngine` stacks them to `{Pin, ssfreq, row, col}` exactly as it
stacks `V` and `S`.

**The `ssfreq` axis carries frequencies in Hz, not indices** — it is a genuine frequency axis, not a
harmonic-order axis, and `HbSpectrum` is not involved. `HB1.wsp`, `HB1.idx("GATE")`, `HB1.H0("GATE")`
resolve exactly as `SP1.*` do.

### 10.5 Where a large-signal instability shows up

- **At low drive the HB `wsp` tends to the S-parameter `wsp`** linearised at the DC operating point.
  The two analyses answer the same question at the two ends of the drive sweep, and that limit is a
  gate to 1e-6 relative.
- **A right-half-plane pole** of the linearised periodic system is seen at `ω_ss` near its imaginary
  part, with Kurokawa's start-up signature on `1/H0(ω_ss)` and `1/Y0(ω_ss)` (Eq. 107/108, Fig. 30) —
  the same `wsp_unstable_freq_kurokawa`, on a different cube.
- **A parametric (sub-harmonic) instability** is seen at `ω_ss ≈ ω0/2`, and at its images
  `ω0/2 + kω0`. **This is the case no linear analysis can see**, and the reason to run the sweep
  across the drive rather than at one drive level.
- **The steady-state condition `1/H0 = 0`** (Fig. 30, p. 70) is reached only by an autonomous solution
  the HB analysis was not asked for. The `wsp` of a *converged, non-oscillating* HB solution answers
  whether **that** solution is stable — not what the circuit would become if it is not.
- **The margin under drive.** [M]'s own amplifier lost its margin in the *small-signal* simulation,
  which §9's linear `SM_Y0`/`SM_H0` already catch. The drive-swept fan of `SM_Y0(ssfreq)` is the
  large-signal extension: a margin that collapses only above some `Pin` is a drive-dependent
  instability, and the parametric case reads as a notch at `ω0/2` that is absent at low drive.
  `MarginThreshold` is applied at each operating point.

  **The threshold NOTE, however, does not survive a sweep, and the number does.**
  `ParametricSweepEngine` re-elaborates per point and disposes each point's netlist, and it has never
  propagated a per-point engine diagnostic out of the loop — an HB non-convergence warning inside a
  sweep vanishes the same way, and has since long before this. So a single-point run prints each
  probe's minimum and its frequency, and a drive-swept run does not print anything: what it produces
  is the `SM_Y0`/`SM_H0` cubes over `{Pin, ssfreq}` plus `__WspMarginThreshold`, which is what the
  Data Display draws the fan and its threshold line from. Reading the fan is the intended workflow
  either way; the sentence is the thing that is missing, not the answer.

### 10.6 The one frequency this analysis refuses to answer

`ω_ss` **commensurate with the fundamental at order 2** — `2·f_ss / f0` an exact integer, so
`f_ss = 0`, `f0/2`, `f0`, `3f0/2`, … — is reported as NaN with one warning, never as a plausible
number.

There the sideband family `{ω_ss + kω0}` and its negation are the **same set of frequencies**, so the
responses at `+ω_ss` and `−ω_ss` are conjugates of each other rather than independent. The
formulation solves one family with the stimulus at that family's own zero sideband, which is the
whole stimulus only while the two families are disjoint; at a half-multiple of the fundamental it is
half of it, and the answer would be wrong in a way that is not wrong one grid step away. `ω_ss = 0`
is the `n = 0` case of the same rule — and it is the case `HbNewton.BuildJ` handles by folding the
`±k` unknowns into a one-sided real-split form, which is why **the HB Jacobian is this analysis at
`ω_ss = 0`** and why that folding is the gate that pins the whole convention (R-wsp5-9(b); measured
agreement is exact, 0 relative deviation on every entry).

The consequence to state plainly, because it is a property of the method and not a defect: **a
parametric instability at `ω0/2` is found by the grid points either side of it.** The pole pair
approaching the imaginary axis at `ω0/2` shows Kurokawa's crossing at the samples that bracket it,
which is what the sweep is for; a grid that lands on `ω0/2` exactly loses that one sample and nothing
else. An odd point count over the same span usually avoids it, and the warning says so.

The lattice form of the same test asks whether `2·ω_ss` is a retained mixing frequency.

### 10.7 Two-tone, and what is refused

With `T ≥ 2` tones the sidebands are `ω_ss + k₁ω₁ + … + k_Tω_T` over the retained mixing lattice —
each half-space representative and its negation, DC once, `2M − 1` of them — and the device spectra
are looked up at the difference of two mixing vectors, which reaches order `2·MaxMixOrder` exactly as
`k − i` reaches `2K` single-tone. `SSMaxHarm` truncates the sideband diamond there rather than a
scalar harmonic.

**Getting those spectra costs one extra device pass per operating point, and cannot not.** The T-tone
Newton path never forms a derivative spectrum at all: `HbApft.AccumulateTripleProducts` consumes the
derivative waveforms as raw time samples, which is exactly what makes it tone-count-general. Those
samples live on the order-`O` torus and cannot resolve an order-`2O` coefficient, so the converged
operating point is re-synthesised onto the wider torus — an exact embedding, since the lattice
enumerates by ascending total order — and the devices are evaluated there once.

v1 supports **one and two tones**. `T ≥ 3` is refused by name, with the count of retained products
the conversion matrix would have needed, because the APFT path's conversion blocks are a separate
piece of work. The rectangular-FFT two-tone path (`HbTwoToneOnLattice = false`) is likewise not
served and says so; the lattice path is the default.

`J_ss` is dense, so it costs `16·n_c²` bytes and is factored once per probe frequency. Above
`n_c = 2000` the analysis refuses by name and states which of `SSMaxHarm`, `MaxHarm` and
`MaxMixOrder` binds — the alternative is a run that allocates gigabytes and is killed with nothing
said.

### 10.8 Loadpull, and the envelope instead

`LoadpullEngine` and `LoadpullPursuitEngine` own their own HB loops and do **not** run the
small-signal solve: at every termination of a grid it would multiply the run by the `ssfreq` count.
A probed loadpull is not refused — it says once where the small-signal sweep lives, and points at
§8's rank-1 re-termination, which is the document's own replacement for a stability loadpull and
gives a large-signal stability envelope for no HB solves at all beyond the nominal one. It works on
an HB `wsp` exactly as on a linear one.

### 10.9 Gates

`tests/Engine.Tests/HarmonicBalance/WSProbeHbTests.cs`. The two that carry the weight are independent
of each other: the fold onto `HbNewton.BuildJ` at `ω_ss = 0`, and a **two-tone HB oracle** — the same
operating point driven with a small second tone at the probe's own terminals, read at the `(0, 1)`
mixing product, through a code path that shares no linearisation with the conversion matrix. Where
the series source goes in that oracle is a derivation and not a choice: only a source on the probe's
**L** side, first net facing the load, reproduces both `iS` **and** `vP` of the probe's own branch
injection, because the probe's own source sits between the two terminals and `vP` is read at one of
them. The oracle's own accuracy is bounded from both sides — an absolute floor of ≈7e-10 A from the
APFT's conditioning below, the tickle's smallness against the pump above — which is what sets that
gate's tolerance rather than a wish.

Gate (e) is the parametric case: a pumped varactor divider whose tank sits at `f0/2`. Below the pump
threshold there is no Kurokawa crossing anywhere in `[0.3 f0, 0.7 f0]`; above it there is one within a
grid step of `f0/2`. The threshold is not asserted from a textbook formula — it is confirmed through
the same two-tone path, by the growth of the pump's own **image** of a tickle, which runs from 18% of
the direct response to 95% of it over the pump range in which the crossing appears. (The direct
response is measurably the wrong observable there: the tank's resonance moves with the pump's average
capacitance and pulls the tickle off resonance, hiding the parametric gain underneath.)

---

## 11. The normalized determinant function (WSP-6)

`NDF = Δ / Δ0 = |Y| / |Y_passive|` (Eq. 181; Bode Eq. 16), where `Y` is the network's admittance
matrix with every termination, bias network and bypass included and every independent source off,
and `Y_passive` is the same matrix with **every dependent source, negative resistance and non-Foster
element rendered passive** (§8, p. 111). `Δ0` then has no right-half-plane zeros by construction, so
by the argument principle the clockwise encirclements of the origin by `NDF(jω)` count the network's
right-half-plane poles (§8, p. 112).

**circuitRF can build `Δ0` and the document's designer usually cannot.** §8 (p. 113) and §5.4
(pp. 95–99): the passive determinant "requires having precise access to the transconductance
elements in all active devices", which a black-box vendor model withholds. Every built-in active
model in `src/Core/Devices` is circuitRF's own, and each knows its controlled sources exactly.

The knob is on the S-parameter directive:

```
analysis SP1 type=sparam start=1 stop=100000 npts=2001 log Unit=MHz  NDF=yes  [PassiveVars="NDFgm"] [PassiveParams="X1.gmscale"]
```

`circuitrf sparam` prints `NDF: N right-half-plane pole(s)` and the property findings; `--json`
carries them under `ndf`; **`explain --analysis` lists the passivation each instance will use**,
which is how to see a refusal coming without running.

### 11.1 The determinant ratio without determinants

`ΔM = M − M0` holds only the dependent-source entries, and every one of them lives in a **control
column** — a controlled current source writes into the columns of its sensing pair, a controlled
voltage source into the columns of its control pair, a linearised FET's `gm` into the gate columns.
With `c₁ … c_r` those columns, `U = ΔM[:, c]` (`n × r`) and `E = [e_{c₁} … e_{c_r}]`:

```
ΔM     = U·Eᵀ
det(M) = det(M0 + U·Eᵀ) = det(M0)·det(I_r + Eᵀ·M0⁻¹·U)          (matrix determinant lemma)
NDF    = det(I_r + Eᵀ·M0⁻¹·U)
```

which is `r` sparse solves against the **passive** factorisation, the `r` rows at `c` of the result,
plus `I_r`, and one dense `r × r` determinant. `r` is the number of control columns in the whole
circuit — two or three per transistor, not the size of the network — so there is no large
determinant, no overflow, no underflow and no tiny ratio of two huge numbers, which are exactly the
numerical troubles the document reports for the NDF (pp. 95, 112). **`M` itself is never factored**;
the S-parameter solves of the same run use their own factorisation of it as before.

`I_r + Eᵀ M0⁻¹ U` is Bode's **return-difference matrix** of the dependent sources, and its LU pivots
taken in **device order** are Struble's sequential return differences `F_i = 1 + T_i` (Eq. 17) —
which is why `NdfCalculator.DeltaColumns` takes a device-ordered column preference rather than
sorting ascending. It is a free cross-check, not a feature.

**MNA versus nodal.** The document writes `|Y|` for the nodal matrix; circuitRF's `M` carries branch
rows as well. `M` and `M0` have identical branch blocks — a controlled voltage source passivates to a
*zero-gain* source, which is still a 0 V branch, and a source that is off, an `IProbe` or a `WSProbe`
merges two rows in both alike — so eliminating the branches multiplies both determinants by the same
factor and the ratio is the nodal one. `NdfTests` gate (e) asserts it against explicit determinants
of both assemblies, taken by a different route.

**Which assembly.** The TERMINATED one, which is the network Eq. 181 is about. On the wave path that
is the assembly the S-parameter solve already built; on the legacy path it is the second, terminated
assembly the WSProbes already needed. The passive assembly is linearised at the **active** circuit's
operating point: `Δ` and `Δ0` are two determinants of ONE network, so linearising the passive one
about its own (different) bias would make the ratio a comparison of two circuits rather than Bode's
return difference.

**Cost, as counters** (owner rule, 2026-08-23): per frequency, one extra assembly, one extra
factorisation (of `M0`) and `r` extra back-substitutions. Frequency-parallel exactly as SP-P3 —
chunks write `NDF[f]` by index, and each worker gets its own passive netlist.

### 11.2 Reading a count off half a contour — the factor of two

The argument principle counts turns around the **closed** Nyquist contour, `ω` from −∞ to +∞. A
sweep runs `ω ≥ 0`, and Platzker's property 4 (`NDF(−ω) = conj NDF(ω)`) says the missing half turns
through the same angle: writing `φ(ω) = arg NDF(ω)`, the negative-frequency arm runs from `−φ(∞)` to
`−φ(0)` and contributes `φ(∞) − φ(0)`, the same as the positive arm. So

```
right-half-plane poles = 2 × (the swept locus's own net clockwise turn)
```

and **`NDF_enc` carries that doubled count**, referred to the sweep's first sample so it starts at
zero. A single REAL right-half-plane pole is half a turn of the swept locus and reads 1 (the analytic
stage of gate (a) is exactly that); a conjugate PAIR — what an oscillator has — is a whole turn and
reads 2. The reference document's Fig. 37 calls a locus whose phase "passes through π" one
encirclement, which is the same statement about the swept half.

> **`WspEnvelope.LoadpullNdf`'s `Encirclements` field does NOT carry the factor of two.** It is the
> swept locus's own turn count, as WSP-9 shipped it, so it is exactly half `NDF_poles`. The two agree
> about *whether* a point is unstable, which is all R-wsp9-5's threshold reads, and gate (k) asserts
> both the verdict and the factor. Reconciling them is an owner decision — see
> `src/RfCore/RESOLVED.md`.

### 11.3 The passivation contract

`ComponentModel` carries:

```csharp
public enum Activity { Passive, ActiveExact, ActiveUserScaled, BlackBox }
public virtual Activity Activity => Activity.Passive;
public virtual Activity ActivityFor(ElaboratedComponent c, IReadOnlyList<double> freqsHz) => Activity;
public virtual void StampPassive(IMnaContext mna, ElaboratedComponent c, double omega) => Stamp(...);
public virtual void StampLinearizedPassive(IMnaContext mna, ElaboratedComponent c, double omega, in PortVoltages bias);
public virtual IReadOnlyList<(int P, int Q)> ControlledConductances => [];
```

**`Activity` is answered by TYPE and conservatively.** A model whose activity depends on DATA it has
not read answers `BlackBox` and refines it in `ActivityFor`, where the component and the run's
frequency grid are available — default-deny, so a model that never looks is refused rather than
assumed harmless. An `ActiveExact` model **must** override `StampPassive`/`StampLinearizedPassive` or
name its `ControlledConductances`; a reflection test over every `ComponentModel` subclass asserts it,
**and asserts that every subclass appears in the table below** — so the next active device cannot be
added without deciding its passivation.

**The passivated branch count must not change.** The two assemblies are subtracted entry by entry, so
a model that allocates a different number of branch unknowns when passivated shifts every later row
and column. A controlled voltage source passivates to a *zero-gain* source (a short), never to no
branch at all.

| Model(s) | Activity | Passivation |
|---|---|---|
| `C`, `L`, `SRLC`, `PRLC`, `SRL`, `SRC`, `SLC`, `PRL`, `PRC`, `PLC`, `Bead`, `Mutual`, `TLIN`, the microstrips, `Short`, `IProbe`, `WSProbe`, `Port`, `Term`, `wBond`, `Match` | Passive | as-is — the ordinary stamp already IS the passive stamp |
| `Vdc`, `V_1Tone`/`V_nTone`, `I_1Tone`/`I_nTone`, `P1Tone`, `PnTone`, `Tuner` | Passive | sources are off in this assembly already; `P1Tone` and `Tuner` stamp their impedances |
| `Atten`, `Switch`, `Circulator`, `Coupler`, `Balun`, `Filter`, `Duplexer` | Passive | as-is. **The circulator is non-reciprocal and passive** — Platzker zeroes dependent sources, not non-reciprocity, and a `σ_max` of 1 can no more hold a right-half-plane pole than a length of line can |
| `Diode`, `NonlinearC`, `SemiC` | Passive | a two-terminal nonlinearity linearises to a positive conductance or capacitance at any bias |
| `R` | ActiveExact | `R → \|R\|` (§8 p. 111's "negative resistances … rendered passive"). The TYPE-level answer is unconditional and the EFFECT is not: for a positive resistor the two stamps are identical entry for entry and it contributes no column at all |
| `Z_Port` | ActiveExact | `Re Z → \|Re Z\|` on the diagonal, where a driving-point negative resistance lives; the off-diagonal transfer terms are the block's own reciprocity and are untouched |
| `VCCS` | ActiveExact | `G → 0` — no stamp, and it allocates no branch, so the numbering is unchanged |
| `VCVS` | ActiveExact | `E → 0`; **the branch and its constraint row stay** (a zero-gain controlled voltage source is a short) |
| `Amp` (system) | ActiveExact | forward gain → 0. What is left is what the other three entries already say — matched terminations and the reverse isolation, kept. With compression on it is nonlinear and the same term is `∂I_out/∂V_in` |
| `Mixer` (system) | ActiveExact | **every off-diagonal** of its linearised block → 0: the two conversion terms and the LO-to-RF leak alike. The brief names the conversion gain; the leak is a dependent source by the same argument, and passivating MORE is the safe direction |
| `FET_*`, `PFET_*` | ActiveExact | `gm = ∂I_d/∂V_gs → 0`. `gds`, the gate diode's conductance and both gate capacitances stay at bias; this family's `dc` is already symmetric, so there is no transcapacitance to remove |
| `JFET_*` | ActiveExact | `∂I_ds/∂V_gs → 0`; `gds` and both gate junctions stay |
| `BJT_*` | ActiveExact | the transport current source `I_ct → 0` in both directions, **and** the base-resistance modulation `∂I_rb/∂V_be`, `∂I_rb/∂V_bc` where `Rb` is modelled — a dependent source by the same argument even though §8 names only `I_ct`. Every junction conductance and charge is kept; the Early effect's `∂Q_be/∂V_bc` is removed as a transcapacitance |
| `MOS*` | ActiveExact | `∂I_ds/∂V_gs → 0` and `∂I_ds/∂V_bs → 0`; `∂I_ds/∂V_ds` and both bulk junctions stay. Meyer's gate charge is genuinely non-reciprocal here and its antisymmetric part goes |
| `VDMOS_*` | ActiveExact | `∂I_ds/∂V_gs → 0`; `gds`, the body diode and both gate capacitances stay |
| `IGBT_*` | ActiveExact | the channel's `gm` and the wide-base bipolar's `α·g_e` transport source → 0 |
| `SDD` | ActiveUserScaled | a global named in `PassiveVars=`, re-elaborated at 0 (§11.4) |
| `VerilogA`, `ExtDevice` | ActiveUserScaled | an instance parameter the model exposes, named in `PassiveParams=` (`X1.gmscale`). With no entry reaching it the run is refused, which is the black-box outcome stated as a remediable one |
| `SnP` | data-dependent | **Passive** when `σ_max(S) ≤ 1 + 1e-6` at every sampled frequency **of the file**; otherwise **BlackBox** — "an S-parameter block with gain hides its dependent sources". The FILE's own grid, not the run's: interpolating between two passive points cannot manufacture gain, and a block that is an amplifier at 12 GHz is not made passive by sweeping to 6 |
| `Chain` | data-dependent | the same test over the block's own evaluated ABCD, at every frequency the run will visit — a `Chain` has no grid of its own |

**Two deviations from brief-wsprobe-6 §3's table, both stated rather than silent.** `wBond` is
grouped there with the `SnP`-backed blocks; in this repository it stamps a physically-derived R/L/M
impedance reduction and reads no data file, so it is Passive by construction. And the `Mixer`'s LO
leak is passivated alongside its conversion terms, for the reason the table gives.

**The transcapacitance rule, stated once.** A two-terminal `C(V)` between the port-`p` pair and the
port-`q` pair contributes `dc[p,q] = dc[q,p]`: it is reciprocal, and it is the "capacitances
evaluated at bias" the document says to keep. A charge at `p` that responds to `V_q` *without* a
matching response of `Q_q` to `V_p` is not a capacitor — it is a controlled source, and it is exactly
the half of `dc` that survives `dc − dcᵀ`. Removing it is what makes `Y + Yᴴ ⪰ 0` attainable at every
ω: the Hermitian part of `Dg + jω·Dc` is `(Dg + Dgᵀ) + jω(Dc − Dcᵀ)`, whose imaginary half grows
without bound while a passive block's does not.

### 11.4 The user-scaled route, and the shape that works

`PassiveVars` is applied exactly as `--set var=expr` is — before elaboration — producing a second
`ElaboratedNetlist` for the passive assembly. Both must produce the same node map, the same component
order and the same branch order, and the engine asserts all three: a `PassiveVars` global that also
sizes a component out of existence, or that a conditional branches on, changes the topology and is
refused by name rather than subtracted. A name that is not a global, or that no device reads, is a
refusal too — **a scaling variable that scales nothing is the classic silent failure of a hand-built
NDF.**

**The SHAPE of the SDD equation matters.** `I[2,0] = NDFgm*Ids(_v1,_v2)` is the wrong shape: it
scales the whole drain current, output conductance included, so `Δ0` would be a *different circuit*
rather than the same one with its controlled source removed. What is frozen is the **controlling
voltage**:

```
I[2,0] = Ids(NDFgm*_v1 + (1 − NDFgm)*Vgs0, _v2)
```

At `NDFgm = 0` the drain current no longer responds to the gate voltage at all while staying
evaluated at the same bias — `∂I_d/∂V_gs → 0` with `∂I_d/∂V_ds`, every junction conductance and every
capacitance kept, which is exactly what a built-in FET's `ActiveExact` passivation does.
`testdata/ndf/hero2_sdd_stage.cnl` is the worked example.

`PassiveParams` names a **top-level** instance (`X1.gmscale`). A dotted path into a sub-cell is a
refusal, not a silent no-op: adding an override there would mean editing a shared `Cell` and would
change every other instance of it in the same run.

### 11.5 The five properties, checked on the engine's own output

Properties 1 (the NDF has zeros only) and 4 (`NDF(−ω) = conj NDF(ω)`) hold by construction — the
denominators of `Δ` and `Δ0` are the same branch-elimination factor and cancel, and a real-valued
netlist gives a conjugate-symmetric `M(jω)` — so there is nothing for a check to catch. The other
three are run diagnostics:

- **`ndf.no-asymptote`** — `|NDF(f_max) − 1| > 0.05` *and* the locus is still closing on 1 (its
  distance from 1 has shrunk by more than three between a decade lower and the top). Extend the sweep
  upward.
- **`ndf.constant-asymptote`** — the same distance, but the locus is **not** closing. The limit is
  then simply not 1, and it is quoted (extrapolated by Richardson elimination of the `1/ω` tail).
  **A constant factor is what passivating a NEGATIVE RESISTANCE does:** `R → |R|` changes an element
  VALUE, and an element value can be a *factor* of the network determinant where a dependent source
  is only ever a *term* in it. The series resonator of gate (b) reads exactly −1 for that reason.
  The pole count is unaffected — a constant turns through no angle.
- **`ndf.dc-imaginary`** — `|Im NDF(f_min)| / |NDF(f_min)| > 0.05`. Extend the sweep downward.
- **`ndf.counterclockwise`** — the running count falls back a **whole encirclement** below its own
  maximum. Property 2 forbids a counter-clockwise *encirclement*, not a counter-clockwise stretch of
  phase: a locus may wander back and forth as long as it does not go round, and the Ohtomo amplifier
  of gate (k) backtracks by 0.74 of one with a perfectly passive `Δ0`.

**The passivity guard (R-wsp6-5).** For every `ActiveExact`/`ActiveUserScaled` device that has a
port-admittance form, the passivated block `Y_dev(ω)` is checked for `Y + Yᴴ ⪰ 0` at every frequency;
a failure is `ndf.passivation-not-passive`, naming the device and the frequency, and the NDF is still
emitted with the count declared unreliable. The minimum eigenvalue is read off an SVD by shifting:
`H = Y + Yᴴ` is Hermitian, so `H + σ_max(H)·I` is positive semidefinite and its singular values ARE
its eigenvalues, giving `λ_min(H) = σ_min(H + σ_max(H)·I) − σ_max(H)`. This is the check §8 p. 113
wishes for ("great care must be taken when constructing the NDF"), and it earns its keep: Hero 2's
SDD FET at its own quiescent bias has a slightly NEGATIVE output conductance — the model's `_v2*th`
drain-induced-barrier-lowering term — so its passivated block fails by 4.7e-4 of its own scale, and
the run says so.

Blocks with no port-admittance form are covered elsewhere rather than skipped in silence: `SnP` and
`Chain` are measured for `σ_max ≤ 1` at setup, and `VCCS → 0`, `VCVS →` short and `R → |R|` are
passive by construction. `Z_Port` overrides the hook (`Y = Z_passive⁻¹`), because taking `|Re Z|` on
the diagonal says nothing about the off-diagonal transfer terms.

### 11.6 Large-signal NDF — deliberately not built

`det(J_ss) / det(J_ss,passive)` over WSP-5's conversion matrix is the natural extension and is not in
scope here. The passivated device spectra it needs are exactly the ones §11.3 defines, applied per
harmonic: the conversion matrix's blocks are built from the same `Dg`/`Dc` this contract masks, so
the passivation carries over unchanged and what is missing is only the plumbing.

### 11.7 Gates

`tests/Engine.Tests/Linear/NdfTests.cs`; fixtures under `testdata/ndf/`.

- **(a)** an analytic single-loop stage — a `VCCS` driving `R_L ∥ C_L` with `R_f` back to the control
  node — against `NDF = 1 + Gf·gm / [(Gs+Gf)(GL+Gf+jωCL) − Gf²]` at 201 log-spaced points, to 1e-12
  relative, and the pole count either side of the closed form's own `g_crit`. **This is what pins the
  factor of two of §11.2**: the stage has a single real right-half-plane pole and reads exactly 1.
  (The inequality runs the other way from the brief's, because a positive `G` sinks current from the
  output node and positive feedback through `Rf` therefore takes a negative `gm`. The closed form is
  computed in the test rather than quoted.)
- **(b)** the document's two resonators (Fig. 31 and Fig. 34), each unstable and stable, reading 2
  and 0; and the phase passing through π within 5 % of 1.5915 GHz on both — asserted on the locus
  referred to its own asymptote, so that the series fixture's −1 constant does not rotate the
  crossing off the axis being tested.
- **(c)** Hero 2's SDD FET at Hero 2's bias, passivated through `PassiveVars`: properties 3, 5 and 2,
  and a zero count; then the same device in a Meissner oscillator reading 2, cross-checked by the
  reduced NDF over its own probes.
- **(d)** the refusals: an `SnP` with `σ_max = 5.1`, named, and the same circuit with a passive pad
  proceeding; an SDD with nothing passivating it; a `PassiveVars` name that is not a global, and one
  that is a global no device reads.
- **(e)** the lemma against explicit LU determinants of both assemblies, by a different route, 1e-10.
- **(f)** the probe route (Eq. 186) over a probe at every non-ground node, against the native NDF,
  1e-9 — which is also what `wsp_passive` exists for.
- **(g)** the guard has teeth: for the FET, BJT, MOSFET and JFET families at three bias points and
  three frequencies, the passivated block passes `Y + Yᴴ ⪰ 0` and the ACTIVE block fails it. Each
  family's bias vector is written in **its own port coordinates** — a vector written for one family
  and handed to another is a device biased OFF, which would pass for the wrong reason.
- **(h)** the contract is complete, by reflection over every `ComponentModel` subclass.
- **(i)** K is not enough: a two-port whose terminal S-parameters are a 6 dB pad — `K > 1`,
  `|Δ| < 1` across the band — wrapped around an internal loop that oscillates. The NDF reads 2. And
  the demonstration is kept from being circular: the stable and unstable versions' terminal
  S-parameters agree to 1e-4, so the two-port metrics were not simply given different data.
- **(j)** a no-knob run is byte-identical, and the counters are §11.1's: `2n` factorisations against
  `n`, and `r` extra back-substitutions.
- **(k)** `Category=Benchmark`, ~20 s. WSP-9's Ohtomo Type-A at `ρ = 0.9` over a 12×12 grid — 144
  real re-runs per balancing resistance — against `wsp_loadpull_ndf`. Both the verdict and the factor
  of two hold at all 288 comparisons. At `Rb = 30 Ω`, 25 of 144 terminations are unstable and every
  one has `SMenv < −30 dB`; two more have a collapsed margin and no encirclement, which is [E]'s own
  point, printed rather than asserted. At `Rb = 100 Ω` all 144 are unstable, because the odd mode is
  differential and no source or load termination reaches it. The fixture carries a probe on **both**
  gates: the envelope's NDF is the *reduced* one over the probe set, and WSP-9's own gate (i) shows a
  set covering one gate cannot see that mode.

---

## 12. Where it is documented (WSP-7)

The user-facing half is **one chapter**, `docs/user/src/reference/wsprobe.html`, sitting directly
after Derived Metrics in the Simulate section of the reading order. Everything in §1–§11 above has a
section of it, and every section here names the one that carries it — so a change to the engine has
a page to update rather than a search to do.

| This note | The chapter | Figure |
|---|---|---|
| §1 the probe, §2 the `wsp` matrix | §1 *What the WSProbe is*, §3 *What it computes* | `{{symbol: wsprobe}}` + an authored redrawing of the two injections |
| §1 placement, the netlist spelling, `idx` | §2 *Placing it* | — |
| §2.2 the six defaults, the reduced two-port | §3 (table) and its callout | — |
| §2.3 what a loop gain is and is not, §5.3 `Zop`/`Yop` | §4 *Reading the results* | — |
| §2.4 where feedback comes from, §5.3 `ZG ≠ 1/YG` | §4 (the warning callout) | — |
| §5.5 Kurokawa's search, the both-must-be-checked rule | §4 | `wsprobe-resonator-polar` |
| §5.2 the single-probe catalogue, §5.6 base SI | §5 *The derived metrics* | — |
| §9 the stability margin, §9.2 the conventions, §9.3 what the numbers mean | §4a *The stability margin* | `wsprobe-margin-resonator` |
| §9.4 what the engine reports, `MarginThreshold` | §4a (the threshold message) | — |
| §6.4 the probe pair, the residual | §6 *Probe pairs: a block in situ* | — |
| §7 bifurcation and sides, Ohtomo | §7 *Global stability* | — |
| §8 the envelope, §9.5 margin and NDF over it, §9.7 what it cannot see | §8 *The stability envelope* | `wsprobe-envelope-card`, `wsprobe-margin-envelope-ohtomo` |
| §10 the large-signal small-signal solve, §10.5–10.7 | §9 *Under harmonic balance* | `wsprobe-hb-fan` |
| §11 the NDF, §11.3 the passivation contract, §11.4 the SDD shape | §10 *NDF* | `wsprobe-ndf-k` |
| §3 the accessors and the CLI | §11 *From the command line* | — |
| every caveat, in one list | §12 *Caveats* | — |
| the history the whole thing sits in | Appendix A *Stability, from the beginning* | three authored diagrams |

Two rules the chapter is under, both gated by `tests/Ui.Tests/Docs/WsProbeDocsTests.cs`:

- **Every equation it cites is a row of the equation register** (overview §4). The register is the
  list of equations somebody re-derived or checked numerically; a citation outside it is a claim
  with no provenance.
- **The two printed equations the register marks as wrong (T-16, T-17) appear only in their
  corrected form**, and the page says so rather than leaving a reader to discover it against a
  non-reciprocal network.

The example designs the figures are drawn from are the committed `testdata/` netlists the gates above
already run — `series_resonator.cnl`, `parallel_resonator.cnl`, `margin_split_resonator.cnl`,
`two_stage_terms.cnl`, `hb_varactor_divider.cnl`, `ohtomo_type_a_ndf.cnl` and
`hidden_pole_two_port.cnl` — read rather than copied (`src/Ui/Diagnostics/Fixtures/DocWsProbeFixtures.cs`).

---

## 13. Performance under harmonic balance (WSP-8)

§10 is the formulation; this section is what it costs and what was done about it. **It changes no
answer** — the implementation §10 describes is kept as `HbSmallSignal.SolveProbesStraightforward`
and is the oracle the shipped path is measured against, at 1.3e-15 relative.

*(brief-wsprobe-8 asked for this as §12; §12 was already taken by WSP-7's documentation map.)*

### 13.1 Where the time went

For one operating point, `M` tickle frequencies, `N` probes, `N_int` interface nodes and sideband
order `K_ss`, §10's straightforward path performs per tickle frequency:

| work | count | kind |
|---|---|---|
| `Y_NN(ω_ss + kω0)`, `k = −K_ss … K_ss` | `2K_ss + 1` sparse factorisations, `N_int` solves each | sparse, size `n` |
| the linear partition at `ω_ss` | 1 factorisation, `4N` back-solves | sparse, size `n` |
| `J_ss` and its factorisation | 1 dense LU of size `n_c = N_int(2K_ss + 1)` | dense |
| the `2N` injections | `2N` dense back-substitutions | dense |

A drive sweep of `P` points multiplies every row by `P`. **Every sparse row is of a matrix that does
not depend on the drive**: the linear partition is the same at every drive level, and what genuinely
changes with the operating point — the two-sided device spectra of §10.2 — enters only the dense
conversion matrix. The ratio, roughly `P·(2K_ss + 1)`, is the whole of the difference between this
analysis and an S-parameter sweep of the same circuit.

### 13.2 Rows of `M⁻¹`, and the whole per-frequency block at once

At `ω_ss` the readings the document wants are `2N` entries of the solution vector — a branch current
and a G-node voltage per probe. Writing `R` for that `2N × n` selection of rows and `b_p` for one
injection,

```
r = R·M⁻¹·b_p + R·M⁻¹·B_int·(−I_nl,0)  =  W[p, ·] + T·(−I_nl,0)
```

and `R·M⁻¹` is `2N` **rows** of `M⁻¹`, each one transposed solve. `b_p` has a single nonzero, so
every entry of `W` (`2N × 2N`) is one lookup; `T` (`2N × N_int`) is the same rows read at the
interface nodes. The `N_int` interface rows give `Z_NN` — hence `Y_NN` — *and* every injection's
open-circuit interface voltage, hence `I_src = −Y_NN·V_oc` for all `2N` at once.

**One factorisation and `N_int + 2N` transposed solves per frequency**, against `1 + 2N` forward
solves plus `N_int` Z-column solves *per operating point*. All of it is small and dense: `Y_NN` is
`N_int²`, `W` is `4N²`, `T` and `I_src` are `2N·N_int` each. The factorisation itself is deliberately
**not** kept — that is the memory the design refuses to spend, and it is why a frequency reached
first as a sideband and later as a tickle point has to be factored twice unless the tickle points are
visited first (they are).

**`SolveTranspose` is Hermitian.** CSparse solves `Mᴴ y = b`; the row of `M⁻¹` is `conj(y)`. This is
measured, not assumed — a real matrix and a real right-hand side cannot tell `Mᵀ` from `Mᴴ` — and
omitting the conjugation is a sign error on the imaginary part alone, which leaves every magnitude
right.

### 13.3 What is left per operating point

```
J_ss   ← the cached Y_NN(ω_k) for every k, and this point's G⁽²⁾, C⁽²⁾, Dw⁽²⁾
LU(J_ss)                                        one dense factorisation
for each of the 2N injections p:
    V      = J_ss⁻¹·(−I_src[·, p] in the k = 0 block)
    I_nl,0 = −(Y_NN(ω_ss)·V[·,0] + I_src[·, p])
    r[p, ·]= W[p, ·] + T·(−I_nl,0)
```

**Dense only.** `P·M` dense LUs of size `n_c`, and that is the honest floor for this formulation: on
a 32-interface-node fixture at `K_ss = 7` it is 96 % of the remaining time. The counters assert the
rest is gone.

### 13.4 Reuse across the sweep, and how it is made safe

The cache lives on the **drive sweep**, not on the extractor: `ParametricSweepEngine` re-elaborates
the netlist and builds a fresh `HbEngine` — and therefore a fresh `HbLinearExtractor` — at every
point, so an extractor-owned cache would be discarded exactly when it was about to pay for itself.
Nothing in it holds a netlist, a component or a factorisation.

Reuse is **verified, never declared**. Each entry carries the values of the matrix it was computed
from; the next use re-stamps that frequency and compares them bit for bit, exactly as the extractor
validates its own factorisations. A swept linear element or a loadpull tuner override changes the
matrix and the entry is recomputed; nothing else can go stale and no caller has to remember to
invalidate. The cost is one stamp and one `O(nnz)` comparison per frequency per operating point —
no factorisation and no solve — and on a small linear partition that is the dominant residual cost,
which is why a *second* visit to a frequency inside one sweep is trusted without re-stamping (the
Newton solve is finished before the first tickle frequency is touched, so nothing can have changed).

**One `MnaSystem` for the whole sweep.** The extractor's own cache is keyed per omega, which is right
for the handful of harmonics a Newton solve visits and wrong for thousands of one-shot frequencies:
it would build the sparsity pattern and the AMD ordering once per frequency and hold every assembled
matrix for the life of the run. The small-signal sweep stamps every frequency into one system, and
`PatternBuilds` for the whole drive sweep is 1.

### 13.5 Sideband coincidence, and the grid nobody moves

`Y_NN` is keyed by frequency, so when the tickle step divides `f0` the sidebands of one point **are**
the sidebands of others: a grid of `M` points at step `f0/m` visits `M + 2K_ss·m` distinct sideband
frequencies instead of `M(2K_ss + 1)`. Folding `ω < 0` onto `|ω|` — `Y(−ω) = conj(Y(ω))` is a
theorem — collapses it further.

**The grid is never altered to achieve this.** A frequency the user wrote is a frequency the engine
uses; the run reports the census (visits, signed distinct, folded distinct) so a designer can choose
an aligned step, and the user page says so in one paragraph.

A frequency is keyed — and **computed at** — its value rounded to 1e-14 relative. Rounding the
computation frequency and not merely the key is what makes an entry a function of its value alone:
`ω_ss + k·ω0` reached from different `(ω_ss, k)` pairs differs in its last bits, and computing at
whichever spelling arrived first would make the answer depend on the ORDER of visits — which is
precisely what chunking the grid across workers changes.

### 13.6 Parallelism

The tickle grid is embarrassingly parallel: contiguous chunks, each worker with its own elaborated
netlist copy, its own extractor and its own cache slice, each writing its slice of `wsp` by index.
`MaxParallelism` governs it as it governs the S-parameter sweep, and the operating point's two-sided
spectra are read-only and shared. Measured 3.0× at degree 4.

**Not across operating points.** The drive sweep warm-starts each HB point from the previous one and
that ordering is the convergence story (`DriveLadder`, `HbDriveRamp`). Overlapping point `i`'s
small-signal work with point `i + 1`'s Newton solve is a later refinement.

Three things pin the sweep serial regardless of the setting: no `Library` to elaborate a per-worker
copy from (the copy is the whole thread-safety story — a model writes state during `Stamp`); a
netlist that may not be elaborated twice (an external device is a slot in a worker *process*; a
control-referencing SDD resolves per netlist); and a **lattice** sideband family, whose
difference-index memo is written on first use.

### 13.7 Memory, and giving up in order

`AnalysisSettings.WspCacheBudgetMB` (default 512). The projection is made once, from the first
stamp's `nnz`, so every operating point behaves identically. Over budget the per-tickle-point blocks
(`W`, `T`, `I_src`) are dropped first and recomputed per operating point — one factorisation and
`N_int + 2N` transposed solves each, still far below §10's path; over budget again the sideband
`Y_NN` entries go too. Either fallback is stated **once**, with the sizes, because a run that
silently got slower is a run nobody can explain.

The certificate is counted in the projection: `16·nnz` bytes per entry, against `16·N_int²` for the
`Y_NN` it guards. On a large design the certificates are the larger half.

### 13.8 `SSMaxHarm`

`K_ss < K` cuts the sideband factorisations linearly and `n_c` — hence the dense LU — cubically. It
is not a cheap knob. Measured on one device driven into compression, `1/H0` over 0.41-3.89 GHz
against `K_ss = 7`:

| `K_ss` | 0 | 1 | 2 | 3 | 5 |
|---|---|---|---|---|---|
| median error | 205 % | 53 % | 59 % | 36 % | 7.1 % |
| worst error | 235 % | 259 % | 95 % | 62 % | 26.9 % |

The convergence is not monotone, and it is not supposed to be: truncation removes mixing paths
rather than terms of a series, so removing an odd number of them can move the answer further than
removing an even number. **The default stays at `K`.** The low-drive limit needs only `K_ss = 0` and
the `f0/2` parametric case needs `K_ss ≥ 1`, but a hard-driven stage needs the lot.
