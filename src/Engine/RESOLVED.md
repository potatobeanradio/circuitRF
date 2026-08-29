# src/Engine — resolved briefs (detail, off the CLAUDE.md growth path)

Mirrors `src/Engine/Loadpull/RESOLVED.md`'s pattern: a completed brief's detail lands here, one `##`
section per brief, sparingly — only for findings that are still true, still surprising, and would cost
someone real time to rediscover. `CLAUDE.md` stays for durable, still-true conventions.

## Match MN-4 — probing the external network (2026-08-19)

`docs/design/match.md` §10, `src/Engine/Match/TerminationProbe.cs`. Looks outward from one pin of a
placed `Match`, measures the network's impedance over the design band, fits the four two-element
termination models by closed-form least squares, and ranks them by mean |ΔΓ|.

### The conjugate belongs on the FIT, not on the measured data — the brief's own step is self-defeating

MN-4 §1 step 8 says "if `conjugate`, use `Z*(f)`" and then fit and rank; MN-4 §7 and match.md §5.4 both
say the answer for a measured parallel R‖C must be a **parallel R‖L**. **Those two cannot both hold, and
the measurement says which one gives way.** Z* of a parallel R‖C is `R/(1 - jQ)` — which is exactly a
parallel R with a **negative** capacitance, and that model fits Z* to 1.3e-16 while being unbuildable.
Ranking the four models against Z* therefore hands the answer to whichever *physical* model happens to
follow a curve nothing physical produces, and on §5.4's own fixture (200 Ω ‖ 0.125 pF, 3.3–5 GHz) that is
a **series R+L** at |ΔΓ| = 0.0236, beating parallel R‖L at 0.0431. A series end arm is a different ladder
parity, so following the brief literally would deliver the opposite of what §5.4 exists for.

What ships: the fit always describes the network **as measured**, and the conjugate is applied to the
chosen fit — same R, same topology, reactance kind flipped, value through match.md §5.1's own identity
`C_eq = 1/(w0^2 L)`. That is **exact at band centre, and exact in the only sense the synthesis uses**:
the synthesis reads a termination solely through `Termination.CeqAt`/`QAt` at ω₀, and both are
bit-identical to the measured ones after the flip. Every displayed residual stays a statement about the
real network. `TerminationProbeTests.FittingTheConjugatedDataWouldPickTheWrongTopology` pins the route
not taken, so this cannot be quietly "simplified" back.

### The Γ-vs-impedance metric: §10.2 is right, but the brief's evidence for it does not exist

MN-4 §7 asks the discrimination test to "assert an impedance-domain metric would have got at least one
of these wrong". **It could not be evidenced, and the reason is structural rather than a gap in the
fixtures.** A load that IS one of the four models is reproduced by that model to ~1e-14, so no metric can
misrank it. A 1,680-point sweep (R = 100 Ω..900 Ω, C = 0.05..0.5 pF, series bond-wire L = 0..1 nH, access
R = 0..50 Ω — the region where the parallel resistance is actually *visible*, i.e. where "genuinely
parallel" has a determinable meaning) put mean |ΔΓ| and mean |ΔZ| on different models **2 times out of
1,680**, both marginal. Every larger divergence found was on a network where `R >> 1/wC`, where the
resistance is invisible and the true topology is genuinely ambiguous — calling either metric "wrong"
there would be reading a preference as a fact.

The demonstrable half — and the one that matters — is that **a lower impedance-domain residual does not
mean a better match**: on 20 Ω of access resistance in front of a 20 kΩ ‖ 0.05 pF output over 1–10 GHz,
ranking by |ΔZ| picks a model whose Γ error is **3.1×** the Γ-ranked one's (0.0153 vs 0.00492). Γ is the
quantity the synthesis exists to null, so ranking in it is ranking in the thing the user is designing
against. Both facts are tests:
`TheGammaMetricAgreesWithTheImpedanceMetricWhereverTheTopologyIsDeterminable` and
`WhereTheMetricsDisagree_TheImpedancePickIsAMateriallyWorseMatch`.

### A DC failure has to be refused HERE — `SParameterEngine`'s own behaviour is the opposite, correctly

`SParameterEngine.Run` catches a non-converged DC solve, warns `sparam-dc-nonconverged`, and linearizes
at 0 V. That is the right default for an ordinary analysis and exactly wrong for a measurement someone is
about to design a matching network against, so the probe runs `NonlinearDcEngine.Run` itself first
whenever the netlist holds a nonlinear model and **refuses with the iteration count and the final ‖F‖**
rather than reporting an impedance from an operating point that does not exist. The extra DC solve costs
milliseconds on a single FET and buys the distinction.

### The probe port must take the next FREE `Num`, and every existing `Term` stays in the circuit

§1 step 3 says "attach a `Term` (`Num = 1`, …)". A bench that already has a `Term:Num=1` — an input
termination, which is the ordinary case — would then hold two ports claiming port 1 and the engine's
`ports.Sort` would resolve which is which by list order. The probe elaborates the stripped bench **once
before adding its own port**, reads the top-level `Port`/`Term`/`P1Tone` numbers the way
`CollectPortsAndBranchLabels` does (a `Num` can be an expression; only the elaborator resolves one), and
takes the smallest free number — so it is 1 whenever 1 is free, and never a collision otherwise. Its own
S-matrix index is then the count of ports numbered below it.

**Existing ports are kept, deliberately.** An input `Term` is part of the external network; deleting it
would leave that node open and measure a different circuit.
`AnExistingPortIsKeptAndTheProbeTakesTheNextFreeNumber` builds 150 Ω in series with a 50 Ω `Term` and
requires the probe to report 200 Ω.

### Small things worth not rediscovering

- **`Ω` is not a unit the elaborator knows.** A `ParameterAssignment("Z", "50", "Ω")` on the probe's own
  `Term` fails elaboration with `Unknown unit 'Ω'`, from a code path three layers from anything the user
  did. The probe passes no unit at all.
- **`Term` is inert at DC** (`NonlinearDcEngine` skips `PortModel`/`TermModel`), so attaching the probe
  port cannot move the operating point it is measuring at. That is what makes step 3 and step 4
  compatible.
- The `TestBench` copy shares its `Instance` objects — they are immutable — and deliberately drops the
  bench's `Measurement`s, several of whose absolute paths lead into the instance just deleted.

## The commensurability error named the source; the fix is on the ANALYSIS (2026-08-19)

**Reported as:** a frequency sweep would not run — `Commensurability check failed: source 'P1'
Freq=3E+09 Hz is not on the HB tone grid {f0=2E+09 Hz, MaxHarm=3}` at a sweep point, blocking work in
the Data Display.

**Not an engine defect.** The owner's netlist had

```
analysis HB1 type=hb Tone="2" ToneUnit=GHz ...
P1Tone:P1 ... Freq=RFfreq GHz
analysis HB1_sweep_RFfreq type=parametric_sweep Var=RFfreq Start=2 Stop=4 Step=1
```

The **source** follows the swept variable and the **analysis's own Tone is a literal**, so the HB grid
stays where it started and every point past the first is legitimately off-grid. `Tone` has always
accepted an expression (`HbEngine.Resolve` → `FreqUnit.ResolveHz`, which also gets var-unit-wins right),
so `Tone="RFfreq"` is the entire fix — verified end to end through `Cli hb` on the owner's netlist.

**What WAS wrong is the message.** It named the source, which is the half that is right, and therefore
sent the reader to the one place with nothing to fix. `HbEngine.SweptToneHint` now searches
`_netlist.ResolvedGlobals` for a variable whose current value equals the off-grid tone — accepting the
unit-less spelling too (`Freq=RFfreq GHz` applies the unit at the use SITE, so the global holds 3, not
3e9) — and appends the variable's name and the fix. Test:
`P1ToneTests.T7b_OffGridSourceFollowingASweptVariable_NamesTheVariableAndTheFix`. Fuller write-up in
`src/Ui/DataDisplay/RESOLVED.md` (it surfaced during the plot-versus work).

## A parametric sweep's unit: SCALE and MARK must come from the same place (2026-08-18)

**Reported as:** a Loadpull Pursuit with a frequency parametric sweep over `RFfreq` ran at the wrong
frequency. **Pinned by the user's own artifact rather than by reading the source** —
`circuitRF_demo/results/anotherLP.npy` carries `__Freq = 2.0`, i.e. the pursuit solved at **2 Hz** for
a design meaning 2 GHz. That number is what identified the mechanism, because only one path produces
exactly 2.0 rather than 2e9 or the loadpull engines' 1e9 fallback.

### The defect

`ParametricSweepEngine.Run`'s re-injection does **two** things per sweep point:

1. multiplies the values into base SI, and
2. attaches the scale-1 base symbol (`GHz`→`Hz`) so `Elaborator` calls `MarkGlobalHasUnit`, which
   puts the variable in `GlobalsWithExplicitUnit` → `FreqUnit.ResolveHz` fires **var-unit-wins** →
   the use site's own unit (`ToneUnit=GHz`) is *not* applied a second time.

The effective unit was `Spec.Unit`, **else the swept VAR's declared unit** — but that fallback fed
only step 2. `SweepSpec` stores raw coefficients and only `ParametricSweepAnalysis`'s spec ctor scales
them, from `Spec.Unit`. So with `Spec.Unit == ""` and `RFfreq` declared `GHz`, the values stayed
`2, 2.5, 3` while the mark asserted they were already base SI. Var-unit-wins then suppressed the GHz
that would have rescued them. The result axis was even *labelled* `"Hz"` on values of 2 — the two
halves of one decision disagreeing, in the same method, four lines apart.

### Why brief-sweep-range-units did not already cover it

That brief fixed the **UI** (`SweepAxisRowViewModel.EffectiveUnit` bakes the inherited unit into
`Spec.Unit` at build time) and concluded Part B needed **"no behavioral change"** in the engine. True
for a UI-authored spec, which arrives with `Spec.Unit` filled in and its values already scaled. False
for a `.cnl`-authored sweep with no `Unit=`, and for any spec written by an editor build predating the
brief — both still arrive with `Spec.Unit == ""`. The reported schematic was the second kind.

**Fix:** an empty `Spec.Unit` now inherits the VAR's unit for the SCALE as well as the mark, which is
the rule the editor has always applied (owner decision 3: "the unit defaults to the swept VAR's
declared unit"). Scaling the materialized points is exactly equivalent to scaling Start/Stop before
expansion, Linear and Log alike. `Values=` list sweeps are excluded: those are base-unit by definition.

### The trap worth remembering

**A test asserted the bug as intended behaviour, with a stated rationale.**
`SweptLengthUnitTests.AUnitlessSpecOverAUnitBearingGlobal_StillLandsInMetres` pinned 10 mil → **10
metres** on the reasoning *"the point of the property is that the re-attach adds nothing, not that a
unit-less spec magically acquires one."* That sentence describes the re-attach correctly and the
sweep wrongly — the re-attach does not merely "add nothing", it MARKS. The 4,000× length error and
the 10⁹ frequency error are one defect wearing two units. A test's prose is not evidence that the two
halves of a mechanism agree; only checking them against each other is.

## Multi-tone HB (3–6 tones): the FFT route was measured out, not designed out (2026-08-19)

**What shipped:** HB now takes 3 to 6 excitation tones. `T = 2` is untouched — it still runs
`MixingGrid`/`HbFft2D`/`HbNewton2D` verbatim. `T ≥ 3` runs a new path
(`MixingLattice`/`HbApft`/`HbNewtonNd`) producing an identically-shaped `DataSet`. Detail in
`docs/design/harmonic-balance.md` §6.4–§6.6; only the findings are here.

**The design doc's own plan for n-tone does not work, and the arithmetic is the reason.** §6.4 had
recorded n-tone as "a dimensionality refactor lifting the 2-tone FFT to a separable `N₁×…×N_T` real
FFT — moderate, mechanical". That grid is `nextpow2(4·order)^T` samples:

| tones | order-3 grid | one Newton iteration's arrays |
|---|---|---|
| 4 | 16⁴ = 65,536 | ~7 MB |
| 5 | 16⁵ = 1,048,576 | ~117 MB |
| 6 | 16⁶ = **16,777,216** | **~1.9 GB** |

(~14 arrays per iteration: `v`/`i`/`q` per interface node, `dg`/`dc` per node pair.) So the
"mechanical" route cannot reach the tone count that was asked for, and it is also nearly all waste:
it computes a box to retain a diamond. The APFT needs **1,512 samples** for that same 6-tone
order-3 case. Worth keeping visible because the refactor reads plausible right up until someone
writes down `16⁶`.

**The Jacobian stopped needing the `4·order` grid at all, which is the non-obvious half.** Under the
APFT the residual's nonlinear term is literally `i_nl = A·i(Γ·V)`, so its exact derivative is the
triple product `A·diag(dg)·Γ` — no difference/sum-frequency spectrum lookups. The `4·order` per-axis
rule existed *precisely* to give the two-tone convolution its `2·MaxMixOrder` reach (§5.2); with the
triple product there is nothing left for it to reach. Two substitutions, one consequence.

**"The two formulations disagree" was truncation, and the test asserts the trend instead of a
tolerance.** Running the same two-tone problem through both paths, the carriers agree to ~4e-8 but
IM3 differed by 0.46% at `MaxMixOrder=3`. Not a defect: the FFT aliases everything above the diamond
back onto it by periodic wrap while the APFT least-squares-projects it, so the product on the diamond
EDGE is the most exposed and each path discards it differently. Measured across orders:

```
IM3 (2,-1) relative disagreement:  order 3: 5.3e-3   order 4: 2.9e-5   order 5: 3.1e-7
```

`HbNewtonNdVs2DTests` therefore asserts the **convergence** (≥10× per order, ≥1000× over 3→5), which
a genuine formulation error would fail and a loose tolerance would have absorbed. Oversampling the
APFT barely moves it (5.3e-3 → 3.0e-3 → 4.5e-4 at oversample 2/4/8) — confirming diamond truncation,
not sample-set error, as the cause.

**The 600-product ceiling is measured, not guessed.** Hero-5 six-tone, single point, dense Jacobian:
order 2 (43 products) **0.4 s**; order 3 (189 products) **4.3 s**; order 4 (**645** products) was
still running at **>17 minutes** of 100%-CPU when it was killed. 645 is just over the cap, and that
gap — seconds to tens of minutes across one order step — is what the cap is placed in. The refusal is
at setup time and names the largest order that fits, because building the lattice + APFT for an
over-cap request would itself allocate hundreds of MB before failing.

**`EffectiveSettings` silently drops any `AnalysisSettings` field not listed in it** — it is a
hand-written field-by-field copy taken whenever the directive's `MaxIter` differs from the settings
default. It had been dropping `HbConsoleDiagnostics`, so `--diag` went quiet for any netlist whose
`MaxIter=` was non-default: the flag appeared to work on some netlists and not others with nothing to
indicate why. Fixed, and the missing fields (`HbSweepWarmStart` and the three new multi-tone ones)
added. Anything added to `AnalysisSettings` that HB reads must be added there too.

**Equal tone spacing puts distinct products on one frequency, and that is fine.** At
1.99/2.00/2.01 GHz — the ordinary three-carrier stimulus — `(1,-1,0)` and `(0,1,-1)` both sit at
−10 MHz. They remain independent unknowns because each tone owns its own phase axis, so the torus
basis functions stay orthogonal regardless of what the frequencies do; the solve is not singular.
`hero5_3tone.cnl` is the fixture, and `HbMultiToneRunTests` asserts both that they share a frequency
and that they are not the same unknown. The spectrum plot shows two stems at one x; they are not
summed.

**One lattice class serves every tone count because the T ≥ 3 enumeration rule REPRODUCES the locked
two-tone order.** `MixingGrid`'s "k₁ descending, then k₂ descending within the upper half-plane" is
exactly lexicographic-descending under the half-space rule ("first nonzero component positive"), so
`MixingLattice(2, O)` matches `MixingGrid(O)` element for element (pinned, orders 0–6). That is what
let the generalization avoid renumbering an index map the measurement library and every existing
two-tone cube already depend on.

## Test-time cut: 61 more `Engine.Tests` methods tagged `Category=Benchmark` (2026-08-28)

Owner's rule: no long test in the routine `dotnet test`. Tagged mechanically from one full-run TRX
— every method over ~5 s, 61 of them summing 1,695 of the run's 1,889 test-seconds, almost all MoM
(`PlanarStaticLimitTests`, `PlanarDeembedTests`, `CalibrationStandardSelectionTests`, `ViaBasisTests`,
`LayeredDcimTests`, `PlanarSolveProgressTests`, …) plus two `Hero3BPursuitTests`. Routine
`Engine.Tests` went from ~3.5–6 min to **19 s** (1,291 tests). Caveat, recorded rather than hidden:
that TRX was taken while `Ui.Tests` ran concurrently, so some durations near the threshold are
inflated (the run read 6 m 12 s against the ~3.5 min it takes alone); a few tagged tests may be 3–4 s
alone. They are all still run by `dotnet test --settings circuitrf.benchmark.runsettings`, and the
counts in the root notes ("122 test methods repo-wide") are now stale by ~176.

