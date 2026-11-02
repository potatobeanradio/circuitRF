# Sonnet Brief — WSP-1: the WSProbe component and the `wsp` matrix in the S-parameter engine

**Read `brief-wsprobe-0-overview.md` first** — §3 (conventions) and §5 (typo register) are binding
here. Reference: T. A. Winslow, *General Circuit Analysis Using The WSProbe* (2023), §4.1 (the `wsp`
matrix, Eq. 31–36), §4.3 (Eq. 50–52), §5 (Eq. 129–136), and the closed-form fixtures of §4.10
(Eq. 109–128).

**Scope.** After this brief, a `.cnl` (or a schematic, through `netlist`) containing
`WSProbe:GATE n1 n2` lines runs under the S-parameter analysis and the run's DataSet carries the
document's `wsp` matrix and each probe's six default outputs, headless and in the GUI alike. No
symbol, no palette tile, no Data Display work (WSP-4), no functions beyond the six defaults (WSP-2),
no harmonic balance (WSP-5).

**Guardrail.** `src/Engine/SParameterEngine.cs` is the only engine file that changes behaviour, and
only when a probe is present. A netlist with no `WSProbe` is byte-identical before and after
(R-wsp1-12). `src/Core`, `src/RfCore`, `src/Cli` gain additive code only.

---

## 1. The component: `WSProbeModel`

**R-wsp1-1.** Add `src/Core/Devices/WSProbeModel.cs`, registered in `ComponentModelFactory` as
`"WSProbe"`. Netlist syntax:

```
WSProbe:<label>  nG  nL
```

Two nets: the first is the **G** (generator-side) terminal, the second the **L** (load-side)
terminal — Fig. 15/16's orientation, and the same first/second rule `IProbe np nm` already uses.
`TerminalNames = ["G", "L"]`. `Kind = Linear`. No parameters in v1 (a `Z0` for the synthetic
circulator of WSP-2 is a *function* argument there, not a probe property).

**Electrically it is an `IProbeModel`.** `Stamp` allocates one branch, writes the 0 V constraint
`V(nG) − V(nL) = 0` and the KCL coupling with the branch current flowing **G → L** — identical to
`IProbeModel.Stamp`, and for the same reason: the document's `iS` (Fig. 16) *is* that branch current
in the engine's own sign convention (overview §3). Do it by lifting `IProbeModel`'s body into a shared
base (`SeriesProbeModelBase`, with `LastBranchIndex`) that both derive from; `IProbeModel` stays
`sealed` in behaviour and name.

**Everywhere the engine matches `IProbeModel`, it must accept the WSProbe as well:** the DC packer's
`I:<label>` scalar cubes (`DcResultPacker`), the HB back-solved `I` cube and `__ProbeBranches`
(`HbEngine`, lines matching `is not IProbeModel ip`), the frequency-parallel exclusion list if any, and
the SDD control-current reference resolver (`ValidateSinglePortBranchHb` and its DC/S-parameter
siblings) — a WSProbe is a legitimate `C[n]=` reference, since it is a 0 V ammeter. Grep
`IProbeModel` and `is IProbeModel`; every hit is a site. **A WSProbe therefore reports `I:<label>` in
DC and HB exactly as an IProbe does**, and that is a gate (R-wsp1-13), because a probe that is not
transparent to the analyses it does not serve is a probe that perturbs.

**R-wsp1-2. `idx`.** `ElaboratedNetlist` gains `IReadOnlyList<WspProbeEntry> WspProbes` — `(int
ComponentIndex, string Label, int Idx)` — populated by the `Elaborator` in **flattened netlist order,
1-based**, `Label` = the component's `InstancePath` (so a probe in a sub-cell is `X1.GATE`, per §4.3).
`idx` is a property of the elaborated netlist, not of the model, because it depends on the other
probes. Two probes with the same full path cannot exist (the elaborator already refuses duplicates).

---

## 2. The `wsp` extraction in `SParameterEngine`

**R-wsp1-3. Where it runs.** Inside the per-frequency loop of **both** paths (`RunWavePath`,
`RunLegacyPath`), after the S-parameter solves of that frequency and **with the same factorisation**.
With `N` probes, the extra work per frequency is `2N` back-substitutions and no factorisation; the
`MnaSystem` pattern cache is untouched because the extra solves stamp nothing.

**R-wsp1-4. The network the probes see is the terminated one.** The document is explicit (§4.2): "All
external reference terminations (Zo) and any DC supplies and all bypassing are contained within the
reduced Y matrix." On the wave path that is exactly the assembly already on the stack — every
`Port`/`Term` stamps its `1/Z0` conductance, independent sources are off, nonlinear devices are
linearised at the DC operating point through `StampLinearized`. **On the legacy path** (some port
with `Re(Z0) ≤ 0`), the ports are 0 V driven branches, which is *not* a termination; for the probe
solves build a second assembly at that frequency with every port stamped as the admittance `1/Z0`
between its nodes and no drive, factor it, and use that. A port with `Z0 = 0` exactly cannot be
terminated and is a refusal naming the port (`wsprobe.port-short`) — never a large-conductance stand-in.

**R-wsp1-5. The two injections and the four readings.** For each probe `i` (branch `br_i`, G-side
node `nG_i`), two right-hand sides against the factored terminated matrix:

```
series:  b = 0;  AddSourceValue(br_i, −1)         // unit vS with − at G, + at L  (v_L − v_G = vS)
shunt:   b = 0;  AddCurrentInjection(nG_i, +1)    // unit iP into the G-side node
```

and for every probe `j` read `iS_j = x[br_j]` and `vP_j = x[nG_j − 1]` (a grounded G node reads 0).
Fill, 1-based (Eq. 33):

```
wsp(2i−1, 2j−1) = iS_j   from the series solve of i      // Y0_ij   (Eq. 133)
wsp(2i−1, 2j  ) = vP_j   from the series solve of i      //         (Eq. 135)
wsp(2i  , 2j−1) = iS_j   from the shunt  solve of i      //         (Eq. 136)
wsp(2i  , 2j  ) = vP_j   from the shunt  solve of i      // H0_ij   (Eq. 131)
```

The sign of the series source value is the one place a wrong guess survives every symmetric
test: with `−1`, a zero-feedback cascade gives `vP/vS = −ZG/(ZG + ZL)` (from Eq. 67 and 69); with
`+1` it gives the negative, and `ZG` comes out negated. R-wsp1-14(a) holds it.

**R-wsp1-6. Port-less analyses are allowed when a probe is present.** `SParameterEngine.Prepare`
currently throws when there is no `Port`/`Term`/`P1Tone`. The document's own fixtures (Fig. 31, 34)
have no ports. When `WspProbes.Count > 0` and there are no ports, run the frequency loop for the
probe solves only and emit the `wsp` cubes with **no `S` cube** (and no `Z0` cube). `BuildDataSet`
must not assume `S` exists downstream: `DataSetBuilder.FromSnp` is not called; the `wsp` cubes are added
to an otherwise-empty DataSet. `explain --analysis` reports "S-parameters: none (no ports); WSProbe
outputs: N probes".

**R-wsp1-7. The regularisation retry applies to the probe solves too.** They use the retried
factorisation when the first one was singular; the `sparam-regularization` warning is emitted once as
today. A probe whose G node is floating produces a singular matrix and the existing zero-row
diagnostic names the node; no probe-specific message is needed.

---

## 3. The six default outputs

**R-wsp1-8. Minimal library.** Create `src/RfCore/Stability/WspReduction.cs` — pure functions over
`Complex` values, no cubes, no engine types, each doc-commented with its equation. WSP-2 extends this
file's namespace; nothing here is thrown away. Writing `A = vP/vS = wsp(2i−1,2i)`,
`B = iS/vS = wsp(2i−1,2i−1) = Y0`, `C = vP/iP = wsp(2i,2i) = H0`, `D = iS/iP = wsp(2i,2i−1)` (the
Appendix E.13 letters):

```
|P| = B·C − A·D                                            (Eq. 42)
α = |P|,  β = |P| + A,  γ = |P| − D,  δ = |P| + A − D + 1   (Eq. 43)

[Y] = (1/H0) · [[ δ, −β ],       i.e.  y11 = δ/C   y12 = −β/C          (Eq. 44)
                [ −γ, α ]]              y21 = −γ/C   y22 =  α/C

[Z] = (1/Y0) · [[ α,  β ],       i.e.  z11 = α/B   z12 = β/B           (Eq. 48)
                [ γ,  δ ]]              z21 = γ/B   z22 = δ/B
```

**Never Eq. 40 or Eq. 47 as printed** (typo register T-1, T-2). Then

```
H0  = C                                                     (Eq. 50)
Y0  = B                                                     (Eq. 52)
LG  = −(y12 + y21)/(y11 + y22)                              (Eq. 58/92)   bilateral (Tian) loop gain
F   = 1 − LG                                                (Eq. 57, p. 33: "F = 1 − LG")
ZG  = (y12 + y22)/(y11·y22 − y21·y12) = z11 − z12 = −(1/Y0)·A            (Eq. 65, 67)
ZL  = (y21 + y11)/(y11·y22 − y21·y12) = z22 − z21 =  (1/Y0)·(1 + A)      (Eq. 66, 68)
```

Implement `ZG`, `ZL` through the **Z form** (`−A/B`, `(1+A)/B`): it has one fewer cancellation than the
`|Y|` form and is exact algebraically (verified: the two agree to 2e-16 on random two-ports). Keep the
`|Y|` form as a test oracle only.

**R-wsp1-9. Guards.** `C = 0` (an exact short across the G node to ground, so `H0 = 0`) makes
`[Y]` undefined; `B = 0` (`Y0 = 0`, an exact open in the probe branch) makes `[Z]` undefined. The
library returns `NaN` for the affected outputs at that frequency and the engine adds one run warning
per probe (`wsprobe.degenerate-node`) naming the probe and the first offending frequency. **No
epsilon is added to a denominator** (overview D-7).

**R-wsp1-10. DataSet layout.** Per run (per sweep point when swept), all Complex:

| Cube | Axes | Content |
|---|---|---|
| `wsp` | `{freq, row, col}` | `row`/`col` are 1-based integer labels `1 … 2N`; `wsp[f, r, c]` is the document's `wsp(r, c)` |
| `H0:<label>`, `Y0:<label>`, `ZG:<label>`, `ZL:<label>`, `LG:<label>`, `F:<label>` | `{freq}` | one set per probe, the `:` spelling of `I:IP1` |
| `__WspProbes` | `{probe}` (labels = the probe labels, in idx order), Real values = `idx` | metadata; excluded from pickers like every `__` cube |

`row`/`col` carry no unit. Under `ParametricSweepEngine` the sweep axis is prepended
(`wsp[Vgg, freq, row, col]`) and `__WspProbes` passes through unstacked, both by the existing
`StackSweepAxis` rules — assert, do not assume (R-wsp1-14(h)).

**R-wsp1-11. Measurement accessors.** In `Evaluator`/`MeasurementContext`, alongside `HB1.V(...)`:

```
SP1.wsp                      → the {freq,row,col} cube (or {sweep…,freq,row,col})
SP1.idx("GATE")              → Real scalar, the probe's idx (from __WspProbes)
SP1.H0("GATE")   SP1.Y0("GATE")   SP1.ZG("GATE")   SP1.ZL("GATE")   SP1.LG("GATE")   SP1.F("GATE")
                             → the per-probe cubes; trailing sweep axes kept, as V(...) keeps them
SP1.wsp(3, 13)               → one element traced over freq (the document's wsp(3,13), Eq. 36)
```

An unknown label is an error that lists the probes present. Bracket notation works as it does for any
cube (`SP1.wsp[:, 3, 13]`). `measure` lines using these evaluate identically in the GUI and under
`circuitrf sparam` (the evaluator is shared; add one test on each side).

---

## 4. CLI and `explain`

**R-wsp1-12(a).** `circuitrf sparam` prints, after the S summary, one line per probe:
`WSProbe GATE idx=1  H0(f_lo)=… ZG(f_lo)=…` (the CLI's existing formatting of complex values), and
`--json` carries `wsprobes: [{label, idx}]`. `-o out.npy` carries every cube (nothing to do — the
exporter writes the DataSet); `-o out.s2p` writes S only and, when there is no S, refuses naming the
`.npy`/`.mat` spellings. `plot --trace cube=wsp,row=2,col=2,y=polar` is WSP-4's; leave `plot` alone
here.

**R-wsp1-12(b).** `explain --analysis` lists the probes with their `idx` and both terminal nets;
`check` warns (`wsprobe.shorted`) when a probe's two nets are the same net — the GUI's own
`SeriesProbeInsertion` cut is what prevents that for an IProbe, and the check is the headless twin.

---

## 5. Design note

**R-wsp1-13.** Create `docs/design/stability-wsprobe.md` with §1 (what the probe is; citation), §2
(the `wsp` matrix, indexing, the four injections and the sign conventions of overview §3 — verbatim),
§3 (DataSet layout and accessors), §4 (the typo register entries used here). Later briefs append their
sections. Cite the document by equation throughout.

---

## 6. Gates

All in `tests/Engine.Tests/Linear/WSProbeTests.cs` unless stated; fixtures under
`testdata/wsprobe/`, hand-written `.cnl`, every value chosen so the closed form is exact.

**R-wsp1-14(a) — zero-feedback cascade, the sign anchor.**
`Term1 (50 Ω) — L1 — [probe] — C2 ∥ R2 — ground`, no path around the probe. Closed form:

```
ZG = 50 + jωL1                 ZL = 1/(jωC2 + 1/R2)
Y0 = 1/(ZG + ZL)               H0 = 1/(1/ZG + 1/ZL)
vP/vS = −ZG/(ZG + ZL)          iS/iP = (1/ZL)/(1/ZG + 1/ZL)          (Eq. 67, 76)
LG = 0   F = 1                 y12 = y21 = 0   (Eq. 89: ZG = 1/y11, ZL = 1/y22)
```

Assert all four `wsp` elements and the six outputs to 1e-12 relative at 50 frequencies. **A wrong
series-source sign fails `vP/vS` and `ZG` here and nowhere symmetric.**

**(b) — the series unstable resonator (Fig. 31, Eq. 109–118).** Node `N` split by the probe: G side
`R1 + L1 + C1` in series to ground with `R1 < 0`; L side `RS` to ground. `L1 = 1 nH`, `C1 = 10 pF`
(so `f0 = 1/(2π√(L1C1)) = 1.5915 GHz`), `RS = 10 Ω`, `R1 = −20 Ω`. No ports (R-wsp1-6). With `s = jω`:

```
Y0 = sC1 / (C1L1 s² + C1(R1 + RS) s + 1)                                   (Eq. 109)
H0 = (RS C1 L1 s² + C1 R1 RS s + RS) / (C1L1 s² + C1(R1 + RS) s + 1)       (Eq. 110)
```

Assert both to 1e-10 relative over 0.5–3 GHz. Then the signature: `1/Y0` crosses the negative real
axis clockwise at `f0` (Re < 0 there, Im changing sign with `∂Im/∂ω > 0`, Eq. 108) and `1/H0`
does **not** (§4.10, p. 76–77: pole masking). The Kurokawa search itself is WSP-2's; here assert the
three conditions directly on the sampled locus.

**(c) — the parallel unstable resonator (Fig. 34, Eq. 119–128).** G side `R1 ∥ L1 ∥ C1` to ground,
L side `RS` to ground, same values. Closed form:

```
H0 = s L1 R1 RS / (s² C1 L1 R1 RS + s L1 (R1 + RS) + R1 RS)                (Eq. 119)
Y0 = (R1 + s L1 + s² R1 L1 C1) / (s² C1 L1 R1 RS + s L1 (R1 + RS) + R1 RS)  (Eq. 120)
```

and the mirror signature: `1/H0` shows it at `f0`, `1/Y0` does not (p. 78).

**(d) — pure feedback (Fig. 25, Eq. 85–87, symmetric case only, typo register T-5).** Probe from `A`
to `B`; `C` from `A` to `B`; `Term` of `Z = 1e9 Ω` at each of `A` and `B` (so `G1 = G2 = 1e-9 S`).
Assert `ZG = ZL = 1/(G1 + 2·jωC)` to 1e-9 relative, i.e. `CG = CL = 2C`, and `ZG + ZL = 1/Y0`.

**(e) — identities on a network with feedback around the probe.** Hero 1 with `WSProbe:P1` inserted
between `L1` and `a1` and `WSProbe:P2` between `a2` and `L2` (the `C5` and `C3` paths make `y12 ≠ 0`).
At every frequency, for each probe, to 1e-11 relative:

```
[Y]·[Z] = I                                    (Eq. 49)
ZG + ZL = 1/Y0                                 (Eq. 69)
YG + YL = 1/H0        with YG = y11+y12, YL = y22+y21                     (Eq. 76–77)
1/H0 = (1 − LG)/Zop,  1/Y0 = (1 − LG)/Yop,  Zop = 1/(y11+y22),  Yop = 1/(z11+z22)   (Eq. 93)
Zop/H0 = Yop/Y0                                (Eq. 94)
YG + YL = |Y|·(ZG + ZL)                        (Eq. 95)
LG from [Y] equals LG from [Z]                 (Eq. 60)
ZG ≠ 1/YG  (assert the difference EXCEEDS 1e-6 relative somewhere — Eq. 79 must be visible)
```

**(f) — non-perturbation.** Hero 1's `S` with those two probes equals Hero 1's `S` without them to
1e-12 absolute, and the golden `< 1e-6` gate of `Hero1Tests` still passes on the probed netlist.

**(g) — cross terms and orientation.** On (e): `wsp(2, 4)` (voltage at P2 per current into P1) equals
`wsp(4, 2)` only if the network is reciprocal; with the non-reciprocal `.s2p` in the path they differ
— assert the difference is non-zero, then replace the `.s2p` by its reciprocal part (an `SnP` of
`(S + Sᵀ)/2` written to a temp file) and assert equality. A stimulus-major/response-major mix-up
passes the second half and fails the first.

**(h) — sweep stacking.** Wrap (e)'s analysis in a two-point parametric sweep of a global the network
reads (e.g. `R3`): `wsp` has axes `[R3, freq, row, col]`, `ZG:P1` has `[R3, freq]`, `__WspProbes`
has `[probe]` only and equals the unswept one.

**(i) — frequency-parallel identity.** `SParameterEngine.Run(...maxDegreeOfParallelism: 4)` on (e)
with 256 points gives a `wsp` bit-identical to the serial run (the pattern `SParamFrequencyParallelTests`
uses).

**(j) — a biased nonlinear device.** The `NonlinearSParamTests` T3 pattern: an SDD whose conductance
depends on bias, a probe at its port; `wsp` equals that of the hand-linearised circuit (a `VCCS`/`R`
network with the DC-point derivatives) to 1e-9.

**(k) — no-probe runs are untouched.** Hero 1, Hero 1B and `WavePortTests` fixtures produce
byte-identical `S` (compare the serialised `.npy` bytes, not just values), and `PatternBuilds == 1`
still holds.

**(l) — counters, not timings** (owner rule). On (e) with 2 probes and 100 frequencies: exactly 100
factorisations and exactly `100 × (N_ports + 2·2)` back-substitutions — expose the count on
`MnaSystem` or the engine's prepared state for the test.

**(m) — transparency to DC and HB.** A WSProbe in `hero2.cnl`'s drain wire: the HB `V` cube is
byte-identical to the unprobed run, and `I:<label>` equals the `I:IP1` an `IProbe` in the same place
reports. Same for the DC packer.

**(n) — measurement accessors.** A `measure` line `ZGg = SP1.ZG("P1")` and `h = SP1.wsp(2, 2)`
evaluate through `MeasurementEvaluator` and through `circuitrf sparam` to the same cubes.

## On completion

Findings to `src/Engine/RESOLVED.md` (engine) and `src/RfCore/RESOLVED.md` (library) — **never a
`CLAUDE.md`**. Update `docs/design/linear-engine.md` §9 with one paragraph pointing at
`stability-wsprobe.md`, and `docs/design/cli.md` §2's verb table for the `sparam` additions. User docs
are WSP-7's.
