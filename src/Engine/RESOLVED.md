# src/Engine — resolved briefs (detail, off the CLAUDE.md growth path)

Mirrors `src/Engine/Loadpull/RESOLVED.md`'s pattern: a completed brief's detail lands here, one `##`
section per brief, sparingly — only for findings that are still true, still surprising, and would cost
someone real time to rediscover. `CLAUDE.md` stays for durable, still-true conventions.

## An unresolvable analysis Tone resolved to 1 GHz, and every later message described that grid (2026-09-02)

Reported against a single-tone HB whose `Tone` named a variable the schematic does not define
(`RFfreq`; the design has `RFfreq1`). The **Analysis card was already correct** — it renders
`f0=unknown: RFfreq` via `AnalysisPreviewHelper`, which does not substitute. The RUN said something
else entirely:

```
'HB3': Commensurability check failed: source 'P2' Freq=1.805E+09 Hz is not on the HB tone grid
{f0=1E+09 Hz, MaxHarm=5} — the source's frequency follows the variable 'RFfreq1' ...
```

Every number in that sentence is real except the one that matters. `HbEngine.Resolve`'s local
`ToneHz` was `try { FreqUnit.ResolveHz(...) } catch { return 1e9; }`, so an `UnresolvedNameException`
became a **1 GHz tone indistinguishable from one the user typed**. The engine then did exactly the
right thing with the wrong input: 1.805 GHz is genuinely off a 1 GHz grid, so the commensurability
check fired, and `SweptToneHint` — which exists for the real swept-tone case — found `RFfreq1` sitting
at that value and appended a confident remedy for a defect that was not present. **The check was not
wrong; it was answering a question about a tone nobody had asked for.**

### The rule the fix encodes

A field the user can leave undefined must **refuse by name**, never substitute. A substituted value
propagates into every downstream message as if it were authored, and the further from the
substitution the symptom appears, the more convincing the wrong explanation gets. `ToneHz` now
throws an `InvalidOperationException` built by `FreqUnit.UnresolvedFieldMessage`, naming the analysis,
the field (`Tone`, or `Tone[2]` on the multi-tone spelling), the expression as typed, and the name
that could not be resolved.

Three engines carried the same `catch { tone = 1e9; }` — `HbEngine`, `LoadpullEngine`,
`LoadpullPursuitEngine` — and all three are fixed together; a loadpull tone is the same field with
the same failure. Only `ExpressionException` (which covers both `UnresolvedNameException` and
`ParseException`) is caught, so a parse error like `Tone="2 GHz"` — the unit is a field of its own,
not part of the expression — is also a refusal rather than a silent 1 GHz.

### Where the refusal surfaces

`SchematicRunService.DescribePlanned` also calls `Resolve`, and it is **already** guarded by a
`try/catch` whose comment says describing can fail the same way running would. So the plan line
degrades to "cannot be described before running (…)" carrying the real sentence, and `Execute` reports
it per-analysis on the normal error path. Nothing needed changing there.

`Resolve`'s sibling `Num()` helper still defaults `MaxHarm`/`Tol`/`MaxIter` on an unresolved name —
the same shape of silence, not touched here, and worth the same treatment if it ever bites.

Gate: `FreqExprUnitTests.Hb_Resolve_UnknownToneVariable_IsRefusedByName` and its multi-tone,
unparseable and loadpull siblings, which assert the name and field appear AND that the message
carries neither a substituted grid nor a blamed source.

## A charge-carrying branch reported no current — HB read w=0 and nothing else (2026-09-01)

Reported against the unified `I` cube: `ds["I"][probe, k]` on a branch whose nonlinear content is
charge came back at the wrong order of magnitude, and the device's own port rows read **exactly
zero**. Reproduced on the smallest circuit that can show it — a 1 V tone through 1 Ω into an
`IProbe` and a constant-`C0` `NonlinearC` — where the closed form is an ordinary series divider,
`I = jωC/(1 + jωRC)` = 0.000986 + j0.031385 A. Before the fix the probe read 9.9996e-13 + j0 A and
both `C1:*` rows read 0.

### The reported figure was a coincidence; the mechanism is not

The report read the probe's value as `C0·V_k` — the linear charge with the `jkω` missing. It is not:
the number is **gmin·V**, and it stays at 1e-12 when `C0` is changed to 5e-12 while `C0·V` would
have moved with it. Both readings are consistent with the same cause, and the gmin one names it —
with **no** nonlinear current injected at that node, the only path the back-solve finds to ground is
the regularization conductance. Chase the invariant, not the first arithmetic that fits.

### One omission, in two places, both of the same shape

A device terminal current is a sum over SDD weighting indices,
`I[p,k] = Σ_w H[w](k·ω₀)·FT{I[p,w](t)}` — `H[0]=1` conduction, `H[1]=jω` charge, `H[w≥2]` the
device's own weight function. `BuildF` has always formed exactly that sum at the interface node.
Two consumers formed only the `w=0` term:

- **`HbNewton.SolveResult.INl`** carried `iNl` alone. Everything downstream treats it as *the*
  nonlinear injection: `HbLinearBackSolver` subtracts it from the RHS to recover IProbe currents and
  linear-only node voltages, `ComputeDevicePortCurrents` back-solves the converged `_c_ref` from it,
  the `INl` cube publishes it, and the loadpull/pursuit FOMs read `INl[drain]`/`INl[gate]` for
  Pout/Pin/Zin. It now carries the total (`TotalInjection`, and its 2-D/N-D twins), assembled at each
  `return` from the same `iNl`/`qNl`/bucket arrays `BuildF` was handed.
- **`ComputeDevicePortCurrents`** FFT'd `res.I[p]` and nothing else. `NonlinearCModel.EvaluateInto`
  sets `i[0] = 0` unconditionally — the whole device is charge — so its port rows were the FFT of a
  zero array. It now FFTs the per-`w` series and sums them with `Model.Weight(w, k·ω₀)`.

**The Newton solve itself was never wrong**, which is why this survived: `V` was right the whole
time (that is why the report's oracles, reading current from node voltages, agreed with physics).
Only what the run *reported* was short a term.

### The seed is the one place w=0 alone is deliberate

`EvaluateNonlinear`'s pass 1 takes a `_c_ref` frozen at the previous iterate's **conduction**
spectrum — its own comment says "(w=0)" — and pass 2 recomputes `_c_ref` from the total. `iNlLast`
is therefore kept as a separate local rather than being repointed at the total, so the solve is
untouched by this change and only the reporting moved.

### What the buffer had to grow

`SolveResult.PortITime` was `[device][port, t]` — one row per port, holding `res.I[p]`. It is now
`PortTermTimes[]` (2-D and N-D get `PortTermTimes2D`/`PortTermTimesNd`), carrying the conduction row,
the charge row, and lazily-allocated rows for any `w≥2` the device actually produced. The HB-P2 M3
fast path is unchanged in kind — the post-solve step is still one FFT per port per weighting index
against a buffer the last Newton pass filled, never a second device sweep. The reuse test's
"a garbage buffer must change the answer" probe now poisons **both** rows: poisoning the conduction
row alone leaves a charge-only device unaffected and the probe goes vacuous.

### Blast radius, checked rather than assumed

Every SDD in `testdata/` declares `I[1,0]`/`I[2,0]` and nothing else, so the charge and bucket terms
are identically zero on all five heroes and no golden moved. The change is visible only on a circuit
that actually carries charge in its nonlinear partition — which is exactly the class that was
reported wrong.

Gate: `tests/Engine.Tests/HarmonicBalance/HbChargeBranchCurrentTests.cs` (5 tests) — the probe row,
the device port row, the linear-only node between them (an `IProbe` is a short, so `V(n_a)` must
equal `V(n_c)`; before the fix it read the undropped source voltage), the same branch written as an
SDD `I[1,1]` charge equation, and the two-tone lattice at both carriers. All five compare against
the closed-form divider, never against another circuitRF path.

---

## Inductor branches are found by interface now, not by type (2026-08-31)

Adding `SRLC` and `PRLC` — see `src/Core/RESOLVED.md` for the models themselves — turned up six
engine-side sites that reached an inductor's Group-2 branch by pattern-matching `InductorModel`:
`SParameterEngine`'s inductance regularization and its SDD control-current resolution,
`HbLinearExtractor`'s regularization and its branch-name map, and both `NonlinearDcEngine`'s and
`HbEngine`'s SDD reference validation. All six now match `IInductiveBranch` instead.

**Why the type check was worse than it looked.** Two of the six are diagnostics, and the other four
decide whether a branch gets regularized at all. A regularization pass that silently skips an
inductive branch produces no message — it produces a singular matrix, reported against whatever row
the factorization happened to fail on, which is usually somewhere else in the circuit. So the
symptom of "forgot to add the new model to the list" is a solve failure that reads as a bug in an
unrelated component. Nothing in the type-check version would have said so.

`TunerModel`'s internal RF choke is still handled separately at both regularization sites, and
deliberately: it stamps its choke inline through `StampInductor` rather than being an inductive model
in its own right, so it has a `ChokeBranchIndex` and not a `LastBranchIndex`. The comment beside it
now says "does not implement IInductiveBranch" rather than "is not an InductorModel", which is the
condition that actually matters.

`HbLinearExtractor`'s branch labels changed with it — they name the component TYPE now
(`SRLC:X1`) rather than always saying `L:`, so a singularity diagnostic about an SRLC does not send
the reader looking for an inductor the schematic never contained.

---

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


## SP-P2 — the MNA keeps its sparsity pattern across frequencies (2026-08-30)

`brief-sp-p2-mna-pattern-cache.md`. `MnaSystem` used to rebuild everything at every frequency: stamps
into a `Dictionary<(int,int),Complex>`, a `CoordinateStorage`, a sort into CSC, then two `HashSet`
scans for structurally zero rows and columns — all of it invariant across ω. It now records the stamp
SEQUENCE on the first pass, builds the CSC and a slot map (`call index → CSC value index`) from it,
and every later pass writes straight into the CSC value array. `Reset` is an `Array.Clear` of the
value and RHS arrays. The structural zero-row/zero-column answer is computed once per pattern; only
the *naming* of a zero row still happens per `Factorize` call, so the diagnostics are unchanged.

### Measured — before/after, and it is bit-identical

Release, single thread, scratch console harness against a `git worktree` of the pre-change tree with
SP-P1's uncommitted work copied in, so the comparison isolates SP-P2. 401 frequency points; the
ladder is series-RLC sections with shunt C, two ports. Median of four runs; the spread was under 2 %
on every row except the whole-run 20/200 ladders, which are short enough to be noisy (±10 %).

Whole `SParameterEngine.Run`, including elaboration, the port solves and the `DataSet` build:

| fixture | before | after | time | alloc |
|---|---|---|---|---|
| ladder 20   | 35.8 µs/pt, 39.5 KB/pt   | 22.2 µs/pt, 27.2 KB/pt   | **1.61×** | 1.45× |
| ladder 200  | 119 µs/pt, 378.9 KB/pt   | 85.9 µs/pt, 261.9 KB/pt  | **1.39×** | 1.45× |
| ladder 2000 | 1,529 µs/pt, 3,736 KB/pt | 1,009 µs/pt, 2,607 KB/pt | **1.51×** | 1.43× |
| Hero 1      | 6.4 µs/pt, 14.0 KB/pt    | 5.7 µs/pt, 9.9 KB/pt     | 1.12×     | 1.41× |

Assembly only — stamp + CSC + structural checks + LU, which is the scope the brief's prototype table
measured (`Size`/`nnz` are this harness's, and the 2000-section ladder lands on the brief's own
Size 4,003 / nnz 12,004):

| fixture | before | after | time | alloc |
|---|---|---|---|---|
| ladder 20 (Size 43, nnz 124)       | 8.2 µs/pt, 37.9 KB/pt     | 5.4 µs/pt, 27.6 KB/pt     | **1.52×** | 1.37× |
| ladder 200 (Size 403, nnz 1,204)   | 74.9 µs/pt, 360.4 KB/pt   | 47.4 µs/pt, 262.1 KB/pt   | **1.58×** | 1.38× |
| ladder 2000 (Size 4,003, nnz 12,004) | 1,387 µs/pt, 3,547 KB/pt | 823 µs/pt, 2,605 KB/pt   | **1.69×** | 1.36× |

Against the brief's prototype (2.35× / 1.51× / 1.62×), the 200- and 2000-node targets are met or
beaten and the 20-node one is not (1.52× against 2.35×). **The allocation columns say why that gap is
not a shortfall in this implementation**: converted to the prototype's units, this harness measures
14.8 → 10.8 MB, 141 → 103 MB and 1,389 → 1,020 MB per 401-point sweep, against the prototype's
15.0 → 10.8, 145 → 105 and 1,429 → 1,042 — the same numbers to within half a percent at every size,
in both trees. What does not line up is the prototype's *absolute times* (its 20-node "before" is
43 µs/pt against 8.2 here, its 2000-node one 786 µs/pt against 1,387), so its 2.35× was measured
against a baseline five times slower than this repo's, and there is no 2.35× available here to find.

**Results are bit-identical, proven end to end.** The harness hashes the whole `S` cube (FNV-1a); all
four fixtures hash the same in both trees (`1218452B9B9A226B` / `A60A96730AA111DE` /
`E9DB3B6673D38F01` / `CD2AE3881F1B4AE8`). No tolerance was loosened, and the new tests assert
`Assert.Equal` on `Complex`, not a tolerance.

### The stamp order is preserved *deliberately*, and that is what makes it bit-identical

The old dictionary summed a cell's repeated stamps in CALL order (`_entries[key] = existing + value`).
Floating-point addition is not associative, so a pattern build that merged duplicates in any other
order would move the last bits of every shared cell — every diagonal, in a ladder. The pattern build
therefore orders the calls with **two stable counting-sort passes** (by row, then by column), never
`Array.Sort`, which is an unstable introsort and would have reordered duplicates silently. `T9` pins
this with `1e16, 1, -1e16, 1` in one cell: call order gives 1, any reversal gives 2.

The same requirement reaches the invalidation path. When a cached pass diverges at call *k*, the
calls already made have to go back into the recording lists — but their individual values are gone,
already summed into the CSC. Rather than keep a per-call value array (a store on every stamp, for a
path that fires rarely), the replay attributes each cell's whole accumulated total to the FIRST call
that reached it and gives the repeats exact `Complex.Zero`. Adding zero to a finite value is exact,
so the rebuilt matrix is bit-identical to the interrupted one rather than merely close.

### Which fixtures rebuilt the pattern, and why

`MnaSystem.PatternBuilds` counts pattern builds and is public for exactly this question — an
invariant sweep builds it **once**, however many points it runs (`T9b`, 25 points). Two things
rebuild it, both anticipated by the brief:

- **ω = 0 with an ideal inductor.** `InductorModel.Stamp` skips its branch diagonal when `jωL + R`
  is exactly zero, so a DC point's sequence is one call shorter. `T9c` walks 1 GHz → 2 GHz → DC → DC
  and reads `PatternBuilds` 1, 1, 2, 2: the DC sequence is itself cacheable, so a grid containing
  0 Hz costs one extra build, not one per point. `T2` runs the three orderings of `[0, f1, f2]`
  through the real engine and requires every point to equal a one-point-per-run solve exactly.
- **The `IfNecessary` regularization retry.** `ApplyRegularization` adds gmin stamps the recorded
  sequence does not have, so the retry mismatches and rebuilds. On a genuinely singular netlist this
  happens **twice per frequency** — the first attempt stops short of the regularized pattern
  (a short pass is a mismatch too, caught by the end-of-pass check in `EnsurePattern`), the retry
  then overshoots it. That path therefore gets no speed-up, which is no worse than before, since it
  rebuilt the dictionary at every point anyway. `T4` pins that the swept `S` still equals the
  point-by-point `S` and that the warning still fires exactly once.

**No model turned out to stamp in a value-dependent order that fact 2 did not anticipate.** Two look
as though they might and do not: `TLineModel.StampUniformLine` clamps `|sinh γl|` to a floor at a
quarter-wave resonance, and `PnToneModel` matches ω against its tone list — both change only the
VALUE handed to an unconditional stamp call, so neither can move the sequence. The complete list of
per-ω sequence branches in `src/Core/Devices` is `InductorModel` and `MatchModel` (the ω = 0
diagonal skip and the `hasC && ω == 0` branch) and `SddModel.StampLinearized`'s zero-admittance and
zero-column `continue`s — the three the brief already named.

### Two things the brief did not ask for, both because the change would otherwise regress

- **`AMD` is discarded only when the sparsity structure actually differs**, not on every
  invalidation. The brief suggests `_amdPerm = null` on invalidate; doing that literally would
  recompute the ordering twice per frequency on the regularization-retry path, where today it is
  computed once for the whole run — a real regression on the very path that already pays the most.
  The gmin retry usually lands on diagonals that are already structurally present, so the rebuilt
  structure is identical and the permutation is still the right one. `BuildPattern` compares the new
  column pointers and row indices against the previous ones and keeps the permutation only when they
  match, which is what the brief's own rationale asks for (recompute when the structure differs).
- **`NonlinearDcEngine` reads the assembly out with `NonZeroEntries()` instead of probing every
  cell.** It filled a dense `_gAug` with `stamped²` `GetEntry` calls, which was O(1) each against a
  dictionary and is O(log nnz_col) against a CSC — size × nnz instead of size + nnz for the readback.
  The dense array starts zeroed, so iterating the nonzeros is exactly equivalent.

### What is left, and it is all the LU

At the 2000-section ladder the whole run now allocates 2,607 KB/pt, of which **2,568 KB/pt (98.6 %)
is `SparseLU.Create`'s own L/U buffers** — measured by running the same assembly loop with
`FindZeroRows()` in place of `Factorize()`, which makes the CSC current without factorizing: the
assembly's own cost is 37.2 KB/pt. (The same measurement on the pre-change tree is not comparable,
because the old `FindZeroRows` scanned the dictionary and never built a CSC at all.) Replacing
`SparseLU.Create` with a fixed-pattern refactorization is §6's explicit non-goal and would need a
sparse LU of our own; it is now the only thing left worth measuring on this path.

`BuildCsc()` returns a COPY, because the internal CSC is live and the next `Reset` zeroes it — but
only when a caller asks for it (`SParameterEngine` never does, and `HbLinearExtractor` calls it once
per ω, where it used to pay for a second full CSC build). The per-port `new Complex[mna.Size]` RHS
allocations in both S-parameter paths are now one reused buffer each, via the new
`MnaSystem.FillRhsWithPortDrive`.

## SP-P3 — the S-parameter sweep runs its frequencies in parallel (2026-08-30)

`brief-sp-p3-frequency-parallelism.md`. `SParameterEngine` gained a second `Run` overload that
splits the frequency grid into contiguous chunks and solves them at once, each on its own elaborated
copy of the testbench. The serial `Run(netlist, …)` is unchanged in behaviour and is still what a
caller holding one netlist gets; the per-netlist setup it always did (ports, singularity namers, the
DC operating point, the SDD control-branch resolution, its own `MnaSystem`) is now factored into a
`Prepared` object built once per worker, and the two solve loops take a half-open `[lo, hi)` range
instead of running the whole grid.

### Measured — Release, 10 cores, workstation GC (the shipped default)

Scratch console harness, median of 5-7 runs, whole `Run` including elaboration and the `DataSet`
build. The ladder is series-RLC sections with shunt C, two ports. "deg" is what the automatic
formula chose.

| fixture | serial | parallel | ratio | deg | gen-0 ser | gen-0 par |
|---|---|---|---|---|---|---|
| Hero 1, 20,001 points        |  60.3 ms |  25.9 ms | **2.33×** | 10 | 38 | 40 |
| 200-node ladder, 2,001 points| 129.1 ms |  46.2 ms | **2.79×** | 10 | 63 | 67 |
| 2000-node ladder, 401 points | 261.2 ms | 144.0 ms | **1.81×** |  6 | 66 | 24 |

Against the brief's 2.8× / 3.1× / 3.0×, the middle row matches and the outer two fall short. **The
brief's own diagnosis is confirmed rather than merely repeated: the limiter is allocation.** The same
harness under `DOTNET_gcServer=1`, nothing else changed:

| fixture | serial | parallel | ratio |
|---|---|---|---|
| Hero 1, 20,001 points         |  56.2 ms | 15.1 ms | **3.73×** |
| 200-node ladder, 2,001 points | 135.1 ms | 35.5 ms | **3.80×** |
| 2000-node ladder, 401 points  | 330.8 ms | 150.7 ms| **2.19×** |

3.80× on the 200-node case is the brief's own number to two figures, and gen-0 collections per run
fall from 63 to 5. **Switching the application's GC mode is not this brief's call** — it is a
process-wide trade that changes memory footprint for every analysis and for the UI — but it is now a
measured lever rather than a guess, and it is where the rest of the scaling is.

Degree sweeps, same harness, showing where each fixture stops paying:

| degree | 200-node ladder | 2000-node ladder |
|---|---|---|
| 2  | 1.84× | 1.68× |
| 3  | 2.41× | — |
| 4  | 2.74× | 1.96× |
| 6  | 2.73× | 2.05× |
| 8  | 2.88× | 1.91× |
| 10 | 2.85× | 2.01× |
| 12 | 2.57× | — |

Past the core count it goes backwards, which is what the `min(ProcessorCount, …)` term is for.

**Bit-identity is proven, not assumed.** The harness hashes the whole `S` cube (FNV-1a over every
`Real`/`Imaginary` bit pattern); serial and parallel hash the same on every fixture, at every degree
in both sweeps, under both GC modes. The tests assert `Assert.Equal` on `Complex` entry by entry, not
a tolerance.

### Elaboration per worker is cheap — until it isn't, and the floor does not see it

Measured elaboration, median of 9: **Hero 1 11 µs, 200-node ladder 346 µs, 2000-node ladder
4,277 µs.** The brief's premise ("elaboration is cheap enough to repeat per thread") holds for the
first two and is where the 2000-node row's 1.81× partly goes: five extra copies at 4.3 ms is ~21 ms
of a 144 ms parallel run, about 15 % of it, spent before the first point is solved.

**The floor counts POINTS, not the cost of a point**, so it cannot see this: 401 points is well over
the 64-per-worker threshold whether a point costs 5 µs or 650 µs. Left as measured rather than
patched with a second heuristic — a cost-aware floor would need a per-netlist estimate the engine
does not have before it elaborates, and the row is still a 1.81× win. It is the thing to look at
first if this path is ever revisited.

### Three departures from the brief, all for a reason

- **The overload takes the caller's netlist AS WELL AS `(lib, tb, baseDirectory)`**, rather than the
  brief's `Run(lib, tb, baseDirectory, …)` which elaborates every copy itself. Every caller already
  holds an elaborated netlist when it computes the frequency grid (`spa.Expand(nl.ResolvedGlobals,
  …)`), and every caller reads that netlist's `Warnings` afterwards — `Cli sparam` prints them twice,
  before and after the run, and `SchematicRunService` drains them into `RunResult.Warnings`. The
  brief's signature would have re-elaborated the primary for nothing and left the caller reading the
  warnings of a netlist the run never touched. So the caller's netlist runs chunk 0 and keeps the
  merged diagnostics, and only the T−1 extras are elaborated and disposed here.
- **Merging warnings "by key, first occurrence winning" was not expressible with the existing API.**
  `ElaboratedNetlist.Warnings` is a `List<string>` and the key set behind `AddWarningOnce` is
  private, so a message alone cannot be de-duplicated against a key that produced it. `AddWarningOnce`
  and `AddNoteOnce` now also record the `(key, message)` pair, and `MergeDiagnosticsFrom` replays a
  copy's keyed entries into another netlist. Only KEYED entries merge, deliberately: an unkeyed
  `AddWarning` comes from elaboration, so every copy produced the same ones the primary already has
  and replaying them would duplicate them.
- **A failing point throws the exception the serial loop would have thrown, not an
  `AggregateException`.** `Parallel.For` wraps whatever escapes a body, which would have turned a
  `SingularMatrixException` — caught by name in the GUI and the CLI — into something neither catches.
  Each chunk instead records its own exception and sets an abort flag the others check per point; the
  lowest faulting CHUNK is the lowest faulting FREQUENCY, because the ranges are contiguous and in
  order, so its exception is re-thrown with `ExceptionDispatchInfo` and the answer does not depend on
  which thread lost the race. A cancellation travels the same road and arrives as the
  `OperationCanceledException` `RunControl.Tick` threw; `T5b` pins that against the serial path
  itself rather than against a remembered type name.

### No model turned out to hold shared static state fact 1 missed

`src/Core/Devices/*.cs` has no mutable static state at all — every `static` there is a pure helper or
prose. `MnaSystem`'s only static is `Col(node)`. So the per-worker netlist really is the whole
thread-safety story for the shipped models.

**The one piece of process-wide shared state on this path is `RfCore.TouchstoneCache`, and SP-P1
had already made it thread-safe** (`ConcurrentDictionary` + `Lazy` at `ExecutionAndPublication`).
That is not incidental: without it, T copies of an SnP-bearing netlist would each parse the
Touchstone file and re-fit its splines. With it they share one parse and one fit, and only the thin
`SnpInterpolator` wrapper — which carries the per-run out-of-range warning — is per model.

`RunControl.Tick` is now called from several threads at once. The counter is `Interlocked` and safe;
the throttle's `Stopwatch.Restart()` is not, but its worst case is a mis-timed interval and therefore
an extra or a skipped progress observation, never a wrong count or an exception. Left alone rather
than serialised: a lock on the report path would cost more than the report.

### Which callers went parallel, and the one measurement behind leaving two serial

On the new overload: `SchematicRunService.RunTypedAnalysis` (the GUI's S-parameter run), `Cli sparam`,
and `ParametricSweepEngine.RunSParam`. The sweep case is safe because the extra copies are elaborated
serially, inside `Run`, while the sweep's variable override is still installed in
`tb.GlobalVariables` — the sweep restores it only after `RunInner` returns — so every chunk runs the
circuit that point is about. A short inner grid falls back to serial on its own.

`TerminationProbe` and `MatchDesignerViewModel.RunResponse` stay on the serial overload, and the
brief's "measure one if you doubt it" was taken up: a 6-element match network over the Match
Designer's default 401 points runs **0.438 ms serial against 0.247 ms at degree 6 — a real 1.77×**.
It is left serial anyway, and that is a judgement rather than an oversight: `RunResponse` is called
per drag frame, so taking six thread-pool workers and five extra elaborations sixty times a second to
save 0.19 ms on a path that is already sub-millisecond spends more than it returns, and it competes
with the UI thread that is waiting for the frame.

### The degree formula, and why the floor is where it is

`PlanDegree(netlist, freqCount, maxDegree)` is public and pure, which is what makes "did this take
the serial path?" answerable without timing anything — it is what the fallback tests assert on.

- `maxDegree == 1` pins serial; `> 1` caps; `0` consults `AnalysisSettings.MaxParallelism`, itself 0
  for automatic, which is `Environment.ProcessorCount`.
- `min(cap, freqCount / 64)`, floored at 1. **64 points per worker** is the brief's own figure and it
  survives contact: Hero 1 costs ~3 µs a point here, so 64 points is ~0.2 ms — the same order as one
  elaboration of a circuit big enough to be worth splitting.
- An `ExternalDeviceModel` anywhere in the netlist returns 1, because its instance is a slot in a
  worker PROCESS (one per kit, not one per thread) and T copies would ask for T times the instances
  and then serialise on its channel anyway. An `SddModel` with `ControlRefs` also returns 1 — nothing
  unsafe, just a small test surface behind `ResolveSParamControlBranches`, kept serial until this
  path has some use behind it.

### Tests

`tests/Engine.Tests/Linear/SParamFrequencyParallelTests.cs`, 23 methods, ~0.2 s, nothing timed.
Seven fixtures (wave path, three coupled inductors, an unbiased and a biased nonlinear device, the
reactive-Z0 legacy path, a floating node that takes the regularization retry at every point, and a
P1Tone port) run serial and at degree 3 over a 301-point grid — 101/100/100, so the split is
exercised on an uneven remainder — and every `S` entry, the `Z0` cube, the warnings list and the
notes list must match exactly. Degrees 2/3/5/8 all land on the same doubles. Separately: the
regularization warning is reported once however many chunks raise it; two independent elaborations of
a biased nonlinear netlist reach the same DC operating point to the last bit (fact 4, asserted rather
than assumed); progress counts every point exactly once and never overruns; a run cancelled after 40
ticks throws `OperationCanceledException` and every chunk stops; and `PlanDegree` is pinned at its
floor, its cap, its serial pin and both fallbacks.

Two test-harness details worth not rediscovering: `Progress<T>` posts to the thread pool, so a
cancellation test that acts on an observation races the run it is observing and will sometimes
measure a completed run — the tests use an inline `IProgress<RunProgress>` that runs on the reporting
thread. And the external-device fallback is asserted through `PlanDegree` against a hand-built
`ExternalDeviceModel` over a trivial in-process fake instance, so no worker process is spawned:
the fallback is decided from the model's TYPE, before any solve, so the fake's numbers only have to
exist.

### Gates

`dotnet test tests/Engine.Tests` 1,433 passed / 1 skipped, `tests/Core.Tests` 1,741 passed,
`tests/Firewall.Tests` 10 passed. `tests/Ui.Tests` 10,185 passed with one failure,
`PCellWorkerHostTests.AScriptThatDiesImmediately_StillReportsWhatItSaidOnTheWayOut` — a Python
subprocess whose traceback arrives on a background stderr reader, on a path this change never
touches; it passes in isolation, which is the signature of a load-dependent test rather than a
regression.

## HB-P1 — the dense solve, the APFT triple product, and the transform cache (2026-08-30)

`docs/sonnet-briefs/brief-hb-p1-dense-solve-and-apft-cost.md`. Three independent costs in the HB
inner loop, all of them implementation rather than formulation: `HbNewton.SolveGaussian` was an
augmented Gauss-Jordan sweep, `HbApft`'s Jacobian triple product a scalar triple loop, and the APFT
itself was rebuilt once per sweep point although it depends on nothing that changes between points.
M4 (two-tone on the lattice) was **not** built — it is owner-gated by the brief's own text.

**Measured per point, Release, Apple M4** (a scratch console harness driving `HbEngine.Run` on the
committed fixtures — not a test; `dotnet test` builds Debug, which inverts managed-vs-native timing):

| fixture | unknowns | before | after | |
|---|---|---|---|---|
| `hero5_6tone.cnl` at order 3 | 756 | 903 ms | **181 ms** | 5.0× |
| `hero5_6tone.cnl` as shipped (order 2) | 172 | 14.1 ms | **7.1 ms** | 2.0× |
| `hero5_3tone.cnl` | 128 | 8.0 ms | **5.1 ms** | 1.6× |
| `hero5.cnl` (two-tone, order 5) | 124 | 21.6 ms | 21.2 ms | — |
| `hero2_convergence.cnl` (single-tone) | 24–32 | 1.0 ms | 1.0 ms | — |

The two-tone and single-tone rows are unchanged on purpose: at 124 unknowns the LU saves ~0.3 ms per
iteration, and those paths spend their time in the FFT box's device evaluation instead (the brief's
own §6 — the two-tone grid evaluates 1,024 samples to solve 62 complex unknowns). **The brief
projected ~8× on the order-3 point; the honest figure is 5.0×**, and the arithmetic below says where
the other 3× went.

### The triple product is bit-identical to the scalar loop it replaced — and that was not luck

The kernel is a 4-row × 4-vector register-blocked GEMM: the 16 accumulators for an output tile live
in vector registers for the whole sample loop, so each output element is written once instead of the
scalar form's read-modify-write on every one of the S samples (862 MB of traffic per D×D block at
6 tones / order 3, for 1.08e8 multiply-adds).

It sums each output element over the samples **in the same ascending order** as the loop it replaced,
so the results agree to the last bit — `HbApftTests` reports relative error `0.00E+000` on both
blocks, not a tolerance. **That is the property to preserve if this is ever touched again**: it means
M2 changed no converged answer anywhere, and it is what lets the column-panel fan-out be safe, since
each output element is still summed by exactly one thread. `OneSharedTransform_GivesBitIdenticalProducts_ToConcurrentCallers`
asserts equality of the raw arrays, deliberately, rather than a tolerance.

Per D×D block at 6 tones / order 3 (D = 378, S = 756): scalar loop **56.5 ms** → 9.9 ms
single-threaded → **4.0 ms** over four column panels.

### The three faster-looking options that were measured and rejected

- **NumFlat's `Mat<double>.Lu()`** — the brief's preferred choice, and the thing design §8 had claimed
  for years was already in use. It is **not blocked**: 39.5 ms at n = 756 against the in-house
  kernel's 29.4 ms, i.e. indistinguishable from an ordinary unblocked right-looking LU. It is also
  *erratic at power-of-two sizes* (n = 256 and n = 512 both collapse to roughly Gauss-Jordan's time —
  reproducible, presumably cache aliasing on a power-of-two row stride), and it is **slower than
  Gauss-Jordan below n ≈ 40**, so adopting it would have made the single-tone path worse. It stays as
  the independent oracle in `HbDenseSolveTests.Lu_AgreesWithNumFlat`, which is the check that would
  catch the two in-house implementations sharing a pivoting mistake.
- **A blocked LU** (panel factorisation + trailing GEMM) — written, measured, bought nothing at these
  sizes (39.6 ms vs the simple form's 39.6 ms at n = 756 before vectorising). The trailing update is
  bound by issue rate, not cache traffic, so there is no traffic to block away. The simpler
  right-looking form ships.
- **Parallelising the LU's rank-1 update** — 29.6 ms → 20.5 ms at n = 756 on eight threads (1.44×),
  and *slower* at every size below 512, because a `Parallel.For` is dispatched per column and n = 756
  means 756 dispatches. Not taken. If the solve is ever the bottleneck again, the fix is a blocked
  factorisation that fans out once per panel, not this.
- **Pre-transposing the weighted analysis rows into a (2D × S) scratch buffer** before the GEMM (the
  brief's option 1) — 4 % faster and needs 4.6 MB per call. Not taken: `_at[s·D + r]` for four
  consecutive `r` is *already contiguous*, so the kernel forms the weighted rows in registers and
  allocates nothing.

### Where the remaining 181 ms is, and why 8× was not reachable

Roughly three Newton iterations, each: **~30 ms** in the LU at n = 756 (the floor for an unblocked
n³/3 at ~7 GFLOP/s on a 128-bit-vector machine), **~24 ms** in `BuildJNd` (three live node pairs ×
two blocks × 4 ms), ~3 ms evaluating the device. The brief's 8× assumed the triple product would
reach 5–10× *and* that the solve would fall by ~3×; the product beat its target (14× with the
fan-out) but the solve is exactly 2.9×, and it is now the largest single item. Closing the rest means
a real blocked/parallel factorisation or a smaller Jacobian — i.e. §16 item 5, which stays deferred.

### The crossover is 8, not 48 — and only one side of it is reachable in production

The brief expected the `Mat<double>` copy to make LU a loss up to n ≈ 48. With no copy, the in-house
kernel is ahead from **n ≈ 8** (measured ratio 1.13 at n = 8, 1.53 at 16, 1.70 at 24, 2.88 at 756),
so `HbNewton.SolveCrossover` is 8. The consequence for the brief's test 3: no analysis this engine
runs is that small — the smallest HB system is 2·N·M and Hero-sized circuits start at dof 24 — so the
Gauss-Jordan branch is exercised only by a synthetic system in
`SolveGaussian_TakesBothBranchesOfTheCrossover_AndTheyAgree`. It is kept rather than deleted so the
small case is not made worse to serve the large one, and because it is the reference the LU is gated
against.

### The product is called once per LIVE node pair — which is 3, not N² = 4

The brief's test 9 expects exactly N² calls per iteration where there were 2·N². It is at most N²: a
node pair whose conductance **and** charge waveforms are both identically zero is skipped entirely
(the AllZero shortcut, now expressed as a null weight argument). On the Hero-2 FET one of the four
pairs is exactly that — the gate current does not depend on the drain voltage — so a real Jacobian
build makes **3** calls. The test asserts `1 ≤ calls ≤ N²` and `calls < 2·N²`, which is the property
that actually matters and does not encode a circuit's topology into a solver test.

### The diagnostic counters are per key and per instance, not process-wide

The brief specifies a static `HbApft.ConstructionCount`. A process-wide counter cannot be asserted
on: xUnit runs test classes concurrently and several of them build transforms, so the count is
perturbed by whatever else is in flight. What ships instead is
`HbApft.ConstructionCountFor(tones, order, oversample)` (per cache key — a test that picks an
oversample no other test uses owns its key outright and can assert an exact count) and an
**instance** `ProductCallCount`. `RunningTheSameAnalysisTwice_ConstructsOneTransform` and
`AnOverCapRequest_ConstructsNoTransform` are exact and race-free because of it.

### Two things found on the way that are NOT this brief's

- **The golden generators rewrite `testdata/` in the source tree on every routine `dotnet test`.**
  `Hero2GoldenGenerator`, `Hero5GoldenGenerator` and `Hero3LoadpullTests` are ordinary `[Fact]`s in
  the default gate, and they walk up from `AppContext.BaseDirectory` to the repo's own `testdata/`
  and overwrite the CSVs. So every full run dirties the working tree, and — since xUnit runs
  `Hero5GateTests` and `Hero5GoldenGenerator` concurrently — whether the golden comparison reads the
  committed data or data the generator just wrote is a race. The comparison is not reliably a gate.
  Verifying "no answer changed" therefore means running the comparators with the generators
  **excluded**, against a clean `testdata/`; done here, 14 tests, all green.
- **The regenerated goldens confirm the change is numerically invisible.** With the generators
  allowed to run, not one field above 1e-12 changed in any Hero-2 or Hero-5 golden. Hero-3's largest
  relative change on a field above 1e-12 is 9.9e-4 — on a value that is itself 1.3e-12, a quadrature
  component at the Newton residual floor. Every physically meaningful number is byte-identical. The
  regenerated files were reverted and are not part of this change.

### M4 — two-tone on the lattice: ON by default (owner decision, 2026-08-30)

`AnalysisSettings.HbTwoToneOnLattice`, **default true**. Built behind the switch and measured first;
the owner then took the default on the numbers below. Clearing the setting routes two tones back to
`HbNewton2D`/`HbFft2D`, which stay in the tree as the independent second implementation
`HbNewtonNdVs2DTests` gates the lattice against.

**Speed: 3.5×.** `hero5.cnl` over the golden's own drive range (−20…−8 dBm, 4 points):
**21.3 ms/point → 6.1 ms/point**. This is the only thing that speeds the two-tone path up — M1–M3
moved it from 21.6 ms to 21.2 ms, because its cost is the device evaluation the FFT grid forces
(1,024 time samples for 62 complex unknowns, against ~250 on the lattice) and none of M1–M3 touches
that.

**Accuracy: the frozen goldens do NOT move.** Worst relative disagreement against the FFT path,
by mixing order, over that same drive range (peak |V| in the second column, so the reader can see
which orders carry signal at all):

| order | peak \|V\| | worst rel. disagreement |
|---|---|---|
| 0 (DC) | 4.8e+1 | **1.5e-16** |
| 1 (carriers) | 6.0e-1 | 1.8e-8 |
| 2 | 6.5e-3 | 1.3e-7 |
| 3 (IM3) | 1.4e-3 | 4.8e-6 |
| 4 | 5.1e-6 | 3.6e-4 |
| 5 (diamond edge) | 2.3e-7 | 8.9e-2 |

The brief predicted "carriers to 1e-6, order-5 edge to ~1e-3". Carriers are two orders better than
that; **the order-5 edge is two orders WORSE — 9 %, not 0.1 %**. That is not a defect: order 5 is the
outer rim of the retained diamond at `MaxMixOrder=5`, so those products are the ones most exposed to
whatever the two paths discarded differently, and the disagreement is a *measurement of how little
either path can be trusted there*. `HbNewtonNdVs2DTests` already pins that it shrinks as the diamond
grows.

**What makes it a non-event for the goldens** is that `Hero5GateTests` ignores bins below
`NoiseFloor = 1e-5` and allows `max(1e-6, |value|·1e-4)`. Orders 4 and 5 peak at 5.1e-6 and 2.3e-7 —
below its floor, never checked. **Verified directly, twice, rather than inferred**: the whole
solution is green under the new default (14,416 tests, 0 failures, `Ui.Tests` and
`Harmonica.Tests` included — both exercise two-tone through the data display and harmonicaRF), and
regenerating the Hero-5 goldens ON the lattice and diffing them against the committed FFT-produced
ones gives a worst per-field disagreement of **3.5e-5 above the gate's own floor**.

**Note the margin, because it is not large: 3.5e-5 against a 1e-4 tolerance is 2.8×.** That is
smaller than the 4.8e-6 the per-order table above suggests, and the difference is not a
contradiction — the gate compares the real and imaginary PARTS separately, each against its own
magnitude, so a small quadrature component of a large phasor carries a much larger relative error
than the phasor does. Anything that later nudges two-tone convergence could put this over. If it
does, the honest fix is to look at whether the bins in question are meaningful at all before
touching either the tolerance or the reference.

**The committed goldens are deliberately left with their FFT provenance.** They were produced on the
rectangular-FFT path, they still pass from the lattice, and leaving them that way makes
`Hero5GateTests` a CROSS-PATH check — a stronger gate than it was when reference and engine shared an
implementation. Regenerating them would silently convert it back into a self-check for nothing.
**This is now a live hazard**, because the golden generators are ordinary `[Fact]`s that rewrite
`testdata/` on every routine `dotnet test` (see the note at the end of §HB-P1): a full run followed
by `git add testdata/` would replace the FFT references with lattice ones and no test would notice.
The generators were run and reverted throughout this work for exactly that reason.

### The routing bug M4's first measurement caught, and why the test leads with an axis name

Routing was first written as "skip `RunTwoTone` when the flag is set", leaving the lattice branch
gated on `ToneFreqsHz.Length >= 3`. A two-tone run then falls past BOTH multi-tone branches into the
**single-tone** solver. It converges cleanly — residual 1.2e-9 — and returns a well-formed DataSet
carrying a `harmonic` axis of length 5 where the caller expects a `mixIndex` axis of length 31.
Nothing throws, nothing warns, and every intermodulation product the analysis exists to compute is
simply absent. **It presented as a 28× speed-up**, which is the only reason it was noticed: the
number was too good, and the comparison against the FFT path then failed to find a `mixIndex` axis.

The branch is now `>= 2` with a comment saying why, and
`HbTwoToneOnLatticeTests.OnTheLattice_TheResultIsStillATwoToneSpectrum_NotASingleToneOne` asserts the
axis before it asserts any number. A timing measurement is a correctness check here: a large
unexplained speed-up in a solver usually means it solved a smaller problem.

*(Unrelated tooling trap met on the way: `sed -i.bak` then `mv foo.cs.bak foo.cs` restores the
ORIGINAL MTIME, so MSBuild's incremental check skips the recompile and the next test run silently
uses the pre-restore DLL. Verify a deliberate flag flip by asserting that a test which SHOULD fail
does fail, rather than trusting the build.)*

## HB-P2 — the linear extractor outlives one solve (2026-08-30)

`docs/sonnet-briefs/brief-hb-p2-extractor-reuse.md`. Three costs, all outside the Newton loop:
`RunSinglePoint` built a fresh `HbLinearExtractor` and refactorized every harmonic on every call
although only the drive changed between Pin steps; the stamp that produced the right-hand side spent
most of its time rebuilding an expression evaluator; and the post-convergence per-port currents were
obtained by evaluating every nonlinear device at every time sample a second time.

**Measured on this box, Release, before and after in the same session** (a scratch console harness
driving the shipping entry points on the committed fixtures — not a test; `dotnet test` builds Debug):

| case | before | after | |
|---|---|---|---|
| Hero 2 warm `RunSinglePoint` | 460.9 µs / 393.0 KB | **174.6 µs / 41.2 KB** | 2.6× / 9.5× |
| Hero 4 warm `RunSinglePoint` | 572.4 µs / 457.0 KB | **237.9 µs / 66.5 KB** | 2.4× / 6.9× |
| Hero 2 warm `Run` | 1230.1 µs / 543.6 KB | **608.8 µs / 63.1 KB** | 2.0× / 8.6× |
| Hero 4 warm `Run` | 799.0 µs / 591.5 KB | **262.3 µs / 92.6 KB** | 3.0× / 6.4× |
| Hero 3 full loadpull, 20 Γ × 32 Pin | 0.24 s / 166.7 MB | **0.19 s / 50.5 MB** | 1.26× / 3.3× |
| warm extractor: `ExtractDC` + `Extract`×K (Hero 2) | 120.5 µs / 143.0 KB | **17.4 µs / 3.9 KB** | 6.9× / 37× |

The loadpull's 1.26× is the honest shape of the win: at 640 solves the extractor was ~80 µs of a
~375 µs solve, and what is left is the Newton loop itself (HB-P1/HB-P3 territory). Its allocation
fell 3.3× regardless, which is what a long grid run actually feels.

### The invalidation protocol the brief specified was not built — the cache validates itself instead

The brief's M1 asks every caller that mutates a termination (`TunerModel.SetHarmonicOverride` and
friends) to call `InvalidateLinear(k·ω₀)` beside it, with a DEBUG-only re-stamp-and-compare as a
"safety net". **That protocol is silently wrong the first time a caller forgets, and Release would
not say so** — and the call sites are not only the two loadpull engines: `SetHarmonicOverride` /
`ClearHarmonicOverride` are also reached from `HarmonicaContext` and from six `Engine.Tests` files
that drive `RunSinglePoint` directly on an engine they hold across mutations.

What ships instead makes the DEBUG net the whole mechanism, in Release too, for free. Every path that
wants a harmonic's factorization has to re-stamp the matrix first — it always did, because the
right-hand side changes with the drive — so `MnaSystem.MatchesCsc` asks the only question that
matters, against the matrix already in hand and with no allocation: **is what I just stamped the
matrix this LU was built from?** Bit equality, structure and values. A `.Equals` mismatch (a NaN
included) refactorizes, which is the safe direction. Cost: O(nnz) compares, ~100 cells on a hero.

Three consequences worth keeping:

- **No caller changed.** `LoadpullEngine`, `LoadpullPursuitEngine` and harmonicaRF gained nothing to
  remember, and every existing test that mutates a tuner between solves on one engine is correct as
  written. `HbLinearExtractor.InvalidateLinear()` / `InvalidateLinear(ω)` exist and are exposed on
  `HbEngine` too, but only as an optimisation and a test hook — correctness never depends on a call.
- **The invalidation is exactly as fine-grained as the physics.** A Γ move at the tuned harmonic
  refactorizes that one harmonic and no other, because only that one's matrix moved. Pinned by
  `HbExtractorReuseTests.TunerImpedanceOverride_IsPickedUpWithNoInvalidationCall_AndRefactorsOneHarmonic`,
  which deliberately makes no invalidation call and asserts both `1` refactorization and an answer
  bit-identical to a fresh engine's.
- **A drive change costs nothing.** `SetSourceDrive` moves the right-hand side only; the matrix
  compares equal and nothing refactorizes. The same test asserts that too.

The engine keeps the extractor per **(netlist, extractor-relevant settings)**, comparing the settings
by VALUE rather than by reference — `Gmin`, both regularization modes, `InductanceRegR`,
`HbConsoleDiagnostics`. Reference equality, which the brief suggested hoisting a copy to preserve,
could never hold: `RunSinglePoint` mints a fresh `AnalysisSettings` whenever the directive's
`MaxIter` differs from the settings default, so the loadpull engine's every call would have missed.

### `ExtractDC` had no cache at all, and was factorizing the same matrix twice per call

Not in the brief, and the larger half of M1 on any circuit whose DC interface needs regularization.
`ExtractDC` never touched `_luCache` — every call built a zero-drive MNA, factorized it for the
Z-columns, then built a *second* MNA with sources active and factorized **that** for `V_oc`. The two
matrices are identical: `ZeroDriveMna` suppresses only `AddSourceValue`/`AddCurrentInjection`, which
land in the right-hand side and not in the matrix. So the whole extraction — DC and every AC
harmonic — now takes ONE stamp with sources active, and the same factorization serves both halves.

The regularized DC factorization is deliberately kept in its own slot (`_dcRegEntry`) rather than in
`_luCache[0]`. `SolveFullNetwork` and `ControlSensitivityRow` have always back-solved at ω = 0
against an **unregularized** matrix, and loadpull forces `InductanceRegularization=Always`, so
sharing one DC entry would have quietly moved every loadpull back-solve (the source-tuner branch
currents behind `Iin`/`Zin`) by the regularization resistance. Two slots, today's numbers.

Hero 2 therefore factorizes **K+2** times, not K+1: its ideal chokes pin the DC interface, so
`IfNecessary` engages and the first `ExtractDC` pays one speculative unregularized pass before the
regularized one. That happens once — the mode is sticky — and the tests assert the count a *single*
solve costs, then that twenty solves cost exactly the same.

### 80% of the linear-partition stamp was rebuilding an expression evaluator

The brief's M2 proposes a `BuildRhsOnly(ω)` that stamps only the RHS-contributing components. It was
not needed, and building it would have cost the self-validating cache above (which needs the full
matrix). Measuring the stamp first said why:

| Hero 2, per stamp | before |
|---|---|
| fresh `MnaSystem` + full stamp + `BuildRhs` | 8.96 µs / 13.22 KB |
| the two `Z_Port` stamps alone | **6.90 µs / 6.21 KB** |

`ZPortModel.EvaluateZ` built a `Scope` **and** an `Evaluator` and re-injected every resolved global —
one `ToString()` per global — on every stamp, at every frequency, on every solve. `ChainModel` has
the identical shape. But `Z(freq)` is a *pure function of freq* for the life of the model: the
expressions, the scope variables and the declared functions are all fixed at construction, and
`ComponentModelFactory` builds the scope dictionary as its own private copy that nothing else holds.
So the memo is exact, not approximate. With it, plus reusing one `MnaSystem` instance per (ω,
regularized?) so SP-P2's sparsity pattern and AMD ordering survive between solves:

| Hero 2, per stamp | before | after |
|---|---|---|
| fresh `MnaSystem` + full stamp + `BuildRhs` | 8.96 µs / 13.22 KB | 3.95 µs / 7.07 KB |
| reused `MnaSystem`, `Reset` + stamp + `BuildRhs` | 6.24 µs / 6.45 KB | **1.40 µs / 0.30 KB** |
| `BuildSourceRhs` × (K+1) | 59.3 µs / 80.8 KB | **9.2 µs / 1.8 KB** |

That also settles M2's "AMD once" without touching `MnaSystem.Factorize`'s signature: the ordering is
per instance, and the instance now persists. It benefits the S-parameter sweep and anything else that
stamps a `Z_Port` or a `Chain`, not just HB.

**This adds no thread-safety constraint that was not already there** — `Stamp` already writes
`PortBranchIndices` on the model, so one instance was never stampable from two threads at once, which
is why `SParameterEngine`'s parallel path elaborates its own netlist per worker.

### M3 — and the one case where the re-evaluation is a different answer, not a slower one

`ComputeDevicePortCurrents` re-evaluated every device at every sample after convergence, to produce
`I:instance:terminal` cubes whose values its own comment says are `INl` re-housed per port. The last
Newton device pass is an evaluation at exactly the converged `V` (the loop returns *before* applying
an update once `‖F‖ < tol`), so keeping its `res.I[p]` in a buffer allocated once per solve makes the
post-solve step an FFT per port. Same for the two-tone and n-tone twins.

**The `cc != null` exemption is not a conservative default; it is required.** With control currents
the post-solve currents are evaluated at the *converged* `_c_ref`, back-solved from the converged
`INl` — which the last Newton pass, one iterate behind on its seed, did not use. That is a different
evaluation of the same device, not a cheaper route to the same one.
`ControlCurrentSdd_IgnoresTheLastPassBuffer_AndReEvaluatesAtTheConvergedCRef` hands the control path
a deliberately wrong buffer and asserts the answer does not move — then hands the *same* wrong buffer
to the `cc == null` path and asserts it does, so the exemption cannot be satisfied vacuously by a
buffer nobody reads.

A buffer whose shape does not match (device count, port count, sample count) falls back to
re-evaluation rather than throwing: a caller passing the wrong one is a caller error, not a data
error, and the wrong answer is the one worth refusing.

### Pre-existing, and NOT caused by this work: the committed self-goldens are stale

Running `Engine.Tests` rewrites ten `testdata/Hero{2,3,5}/*_self_*.csv` and `RLSweep_*.csv` fixtures,
because the golden *generators* are `[Fact]`s. **HEAD does the same** — checked by building the
unmodified HEAD in a second worktree and running the same filter there: the Hero-3 churn is
line-for-line identical (500 / 2358 / 187 / 1218 lines, worst absolute deltas 5.551e-17 / 6.467e-15 /
1.000e-16 / 8.839e-16). Regenerating all fourteen files on both trees and diffing them against each
other gives **byte equality**, so HB-P2 moved no converged number anywhere. The committed copies were
already behind their own generators before this brief; that is someone else's decision to make, and
the files are left as committed here.

---

## HB-P3 — the line search, `DriveStepping`, and warm starts at every tone count (2026-08-30)

`docs/sonnet-briefs/brief-hb-p3-convergence-line-search-and-multitone-warm-start.md`. Design:
`harmonic-balance.md` §11 (rewritten as built), the new §11.2, §11.1, §16 item 4 (settled).

### The numbers

**Release, Apple M4, `testdata/Hero2/hero2_convergence.cnl`, cold `RunSinglePoint` from a DC seed:**

| Pavl | before | after |
|---|---|---|
| 0 dBm | 3 iters, converged | 3 iters, 0 backtracks |
| 10 dBm | 4 iters, converged | 4 iters, 0 backtracks |
| 14 dBm | — | 7 iters, 2 backtracks |
| **16 dBm** | **100 iters, NOT converged, ‖F‖ = 4.64e-1** | **6 iters, 4 backtracks** |
| 18 dBm | — | 7 iters, 5 backtracks |
| **20 dBm** | **100 iters, NOT converged, ‖F‖ = 9.57e-1** | **6 iters, 6 backtracks** |
| 30 dBm | 12 iters, converged | 11 iters, 7 backtracks |

**21-point Pin sweep, 0→30 dBm in 1.5 dB steps, same fixture** (each figure the mean of repeated runs;
before/after measured from a `git worktree` at the pre-brief commit and the working tree, same harness,
same binary layout):

| | before | after |
|---|---|---|
| cold (DC seed each point) | 6.5 ms/pt, 797 Newton iters, **6 non-converged** | **2.07 ms/pt, 132 iters, 0 non-converged** |
| warm (previous-point seed) | 1.14 ms/pt, 85 iters | 1.19 ms/pt, 85 iters |

The warm case pays ~5% for the per-iteration iterate copy the line search needs and saves nothing,
because it was never the case that failed — which is the shape this change was supposed to have.

**Multi-tone Pin sweep, 8 → 22 dBm, 8 points** (`DriveStepping=Never`, so this measures the warm start
alone):

| fixture | nonlinear-DC solves | Newton iterations | ms/point |
|---|---|---|---|
| `hero5.cnl` (2 tones, lattice) | 8 → **1** | 41 → **35** | 7.40 → **6.52** |
| `hero5_3tone.cnl` | 8 → **1** | 64 → **43** | 9.39 → **6.48** |

Against the *pre-brief* engine the three-tone figure is larger still — **36.3 ms/point cold and
40.5 ms/point "warm"** — because that path burned `MaxIter` at 18, 20 and 22 dBm and the seed it was
handed was discarded. Both halves of that (the failure and the ignored seed) are fixed here.

### Where the two-tone/n-tone loops actually failed, and where they did not

The brief expected a two-tone drive level at which the undamped loop fails, to be found and recorded.
**There is none on `hero5.cnl`.** Measured against the pre-brief engine, cold, at 18/20/24/26/28/30/32
dBm: both routes — the mixing lattice (`HbNewtonNd`, the default since 2026-08-30) and the rectangular
`HbNewton2D` — converge at every level, ‖F‖ between 8e-11 and 2.6e-8. With the line search in, the
lattice route refuses exactly one step (at 28 dBm) and the rectangular route refuses none anywhere in
that span.

**The three-tone loop did have one, and it is sharp: `hero5_3tone.cnl` at 17 dBm.** The pre-brief engine
returns non-converged there (‖F‖ = 2.53e-1) and at every level above (0.26 at 18, 0.34 at 20); 16 dBm
converges in 7. With the line search the same cold point converges in 8 iterations with two backtracks.
That level is what `HbLineSearchTests.AThreeToneColdSolve_AtTheLevelTheUndampedLoopFailed_Converges`
pins, and the test says in its own comment that the two-tone half of the brief's expectation was not
reproducible rather than manufacturing a fixture to make it so.

### A converging solve is NOT unconditionally byte-identical — the brief's fact 2 is half true

Fact 2 predicted that with `Lambda = 1` a converging solve "takes exactly the same steps as today, so
goldens do not move." The goldens indeed do not move — `Hero2RegressionTests`, `Hero4Tests` and
`Hero5GateTests` all compare committed CSVs and are untouched — but the stronger claim about *steps* is
false. On the 21-point warm Hero-2 chain, **19 of 21 points take zero backtracks and two do not**: 13.5
dBm (3 backtracks) and 15.0 dBm (1), both on the compression knee, where the full Newton step genuinely
increases ‖F‖ and is refused. Total iterations are 85 either way. So "λ = 1 means nothing changes" is
the right *expectation* and the wrong *invariant*, and a test asserting `Backtracks == 0` everywhere —
which the brief specified — would have been asserting something untrue.

### THE FINDING WITH THE WIDEST BLAST RADIUS: the line search de-fanged the loadpull continuity gate

`LoadpullLadderContinuityTests` had two tests whose fixtures existed to *be wrong*: `hero3_classF.cnl`
at its 2 dB drive step produced 11 energy-violating grid points (several reporting efficiencies in the
thousands of percent), and `hero3_classF_active.cnl` produced a point at PAE = 33,729%. Both opened with
a vacuity assertion — "the fixture is supposed to be WRONG without the guard — if it is not, it has
stopped testing anything". **Both of those assertions now fail, and the guard has nothing left to
remove.** The runaway root is reached by a full Newton step that increases ‖F‖, which is precisely the
step the line search refuses, so it is gone before `DriveLadder`'s continuity criterion is ever
consulted.

Measured over the whole 20-point grid of both fixtures at `PinStep` = 2, 3, 4, 6 and 8 dB, guard on and
guard off: **zero gross violations and zero continuations everywhere**, max PAE 79.7–80.3% and
81.4–82.1%. **There is no step size at which these fixtures still exhibit the defect**, so the vacuity
assertions could not be restored by coarsening them.

The continuity guard is **not** thereby redundant and was left in place: a line search refuses a step
that fails to reduce ‖F‖, and nothing about a monotone descent forbids descending onto a *different*
branch. But it is now **unexercised by these fixtures** — a real loss of coverage, recorded in the
tests' own header rather than papered over. A fixture that jumps branch under the line search would
restore it; none is known.

### Two more tests that were measuring where the Newton happened to stop

- **`LinearBackSolveTests.BackSolver_TighterHbTol_ImprovesCubeAgreement`** compared the default
  `Tol = 1e-6` against `1e-10` and asked for ≥100× better back-solve agreement. It now gets 1.5×, and
  the reason is not a regression: **Newton on `hero2.cnl` is quadratic and its last two iterates are
  ‖F‖ = 2.20e-5 and 1.24e-9 — there is no iterate in between for a tolerance to stop at.** Anything
  from 1e-4 up stops at the first; anything from 1e-5 down reaches the second. So 1e-6 and 1e-10 were
  *the same solve*, and the test had been passing on where the undamped trajectory happened to land.
  The loose end is now pinned at 1e-4, which spans the real step: 6.94e-4 → 1.94e-11, a factor of
  3.6e7.
- The three `LoadpullLadderContinuityTests`/`LinearBackSolveTests` failures were the **only** ones in
  the suite. Every Hero golden, every Jacobian FD oracle and every two-tone/n-tone cross-check passed
  unchanged.

### Design notes worth keeping

- **`Converged`/`Residual` used to be read off `trace.Steps[0]`.** That was the same thing while a point
  was always exactly one Newton solve, and stopped being it the moment `DriveStepping` gained rungs:
  step 0 is then the cold attempt that FAILED, so a point rescued by the ramp would have published
  `Converged = 0` beside a perfectly good spectrum, and under `Always` it would have published the
  small-signal bottom rung's residual for the requested drive's. Reading the LAST step is no better —
  a ramp that runs out of rungs ends on a failing one. Both scalars now come from the solve that
  actually produced the answer (`HbEngine.PointOutcome`).
- **`IDriveScalable` scales the tone and never the DC bias.** Ramping the bias too would walk the
  operating point off the branch the ramp exists to follow — a FET's gate would come up from pinch-off
  at the same time as its drive. Bias ramping is `DcBiasStepping`, a separate mechanism.
- **The ramp's rung offsets are relative and the last one is exactly 0**, not an accumulated sum, for
  the same reason `DriveLadder.ContinueThroughJump` lands its final sub-step exactly: a rung reported at
  `-1.7763568394002505e-15` dB would miss every level-keyed lookup made against it.
- **`HbDriveRamp.Walk` deliberately does not restore the drive**; the caller's `finally` does. Pinned by
  `HbDriveSteppingTests.WalkLeavesTheDriveWhereItWas_AndRestoreIsWhatPutsItBack`, so the engine's
  `finally` cannot be mistaken for decoration and deleted. Injecting a throwing device *model* into an
  elaborated netlist has no seam today, which is why the throw is injected at the ramp's own delegate.
- **A fixture that the line search cannot rescue had to be constructed for M2's gate**, exactly as the
  brief anticipated: capping `HbMaxIter` is what does it. `hero2_convergence.cnl` at 20 dBm converges
  cold in 6 iterations, so `MaxIter = 4` fails cold while every warm-started rung still fits inside 4;
  `MaxIter = 3` fails both, which is the third case worth pinning (the cold result must stand).
- **Two test-facing counters were added**, in the spirit of `HbEngine.LinearFactorizations`:
  `HbNewton.Evaluations` (device evaluations, `[ThreadStatic]`) and `NonlinearDcEngine.Runs` (DC seeds).
  They are what tests 4 and 9–11 of the brief actually read; there was no other observable for either.

### A hazard found on the way, now FIXED: `dotnet test` used to REWRITE the Hero goldens

`Hero2GoldenGenerator.GenerateHero2Golden`, `Hero5GoldenGenerator.GenerateHero5Golden`,
`Hero3LoadpullTests.GenerateHero3Golden` and `Hero3BPursuitTests.GenerateHero3BGolden` are plain
`[Fact]`s that ran and overwrote `testdata/Hero*/…csv` on **every** `dotnet test`. xunit runs classes
in parallel, so a regression test could be comparing against the file its own generator had written
milliseconds earlier, from the very engine under test. **A golden that silently re-freezes itself is
not a golden, and a green unfiltered run was not evidence the frozen answers had held.** It had stood
that way since those generators were written; HB-P3 is only where it was noticed, because a change to
the Newton moved the goldens' noise-floor digits and the suite stayed green either way.

**The fix (`tests/Engine.Tests/Support/GoldenRegen.cs`).** Nothing under `testdata/` is written unless
`CIRCUITRF_REGENERATE_GOLDENS` is set. The generator tests still **run and still assert** — several
carry real physics checks (positive Gt at every grid point, bounded Gmax spread, stop-reason
accounting) that belong in the routine gate — they just write to `TextWriter.Null`. Skipping the tests
outright was the smaller change and was rejected for exactly that reason. Seven write sites were
routed through `GoldenRegen.OpenWriter(path)` (a `TextWriter` that is real or discarding), so a golden
writer's body carries no branch of its own and cannot grow one that forgets the guard.

**One of the eight writers was not a test at all**, and that is the part worth remembering:
`testdata/Hero3B/loadpull_pursuit_output.gam` is written by `LoadpullPursuitEngine` itself, because
`hero3B_at_compression.cnl`'s `OutputGrid=` names a path inside `testdata/`. So *every* test that ran
that pursuit rewrote committed data as a side effect of the engine behaving correctly. Gating the test
would not have caught it. `Hero3BPursuitTests.BuildEngine` now repoints `OutputGridPath` at a scratch
file, which keeps the engine's `.gam` write exercised while leaving the repo alone.

To regenerate deliberately:
`CIRCUITRF_REGENERATE_GOLDENS=1 dotnet test tests/Engine.Tests --filter "FullyQualifiedName~Generate"`,
then read `git diff testdata/` before committing — that diff is the point of the exercise.

**The gate is behavioural, not a convention.** `GoldenFilesStayFrozenTests.EveryGoldenWriter_LeavesTestDataByteIdentical` SHA-256s all 81 files under `testdata/`, invokes every writer directly, and
re-hashes. It fails naming the file when a new ungated writer appears — and the list of writers it
calls is the checklist that says a new one is owed. It skips (rather than inverts) under
`CIRCUITRF_REGENERATE_GOLDENS`, where the files moving is the intent. Cost: ~2 s.

**What the goldens actually did under HB-P3, measured once the generators were controllable.** The
Engine suite was re-run with all four generators filtered out, against the committed goldens:
**1,489 passed, 0 failed.** Then the generators were run alone and the regenerated files compared
numerically against `HEAD`:

| golden | numbers changed | worst absolute Δ |
|---|---|---|
| `Hero2/hero2_self_V_n_gate.csv` | 34 | 1.5e-22 |
| `Hero3/hero3_self_V.csv` | 1,437 | 8.8e-16 |
| `Hero5/hero5_self_V_n_drain.csv` | 213 | 2.0e-8 |
| `Hero5/hero5_self_INl_n_gate.csv` | 224 | 2.1e-17 |

Every difference sits on a near-zero spectral entry — the largest, 2.0e-8 V, is two orders under the
1e-6 convergence tolerance and lands on entries whose own magnitude is ~1e-15 — i.e. the "<1e-5 is
noise" rule the Hero-5 gate already carries. **The committed goldens were restored and are unchanged
in this diff.**

Two other artifacts used to churn on every run and no longer do: `testdata/Hero3/RLSweep_*.csv`
(`Hero3LoadpullTests.RLSweep` is an assertion test that also wrote diagnostics; differences were in the
8th significant digit on the −50 dBm tickle rows) and `testdata/Hero3B/loadpull_pursuit_output.gam`,
whose committed copy is **stale relative to what the engine writes at HEAD** — proven by regenerating
it in a `git worktree` at the pre-brief commit and getting a file byte-identical to the post-brief one.
Neither was attributable to HB-P3, and **the stale `.gam` is deliberately left as committed**: it is
read by nothing, and refreshing it is a decision about that fixture rather than part of this brief.

## HB-P4 — the HB device pass asks for the whole grid at once (2026-08-30)

`docs/sonnet-briefs/brief-hb-p4-sdd-grid-evaluate.md`, engine side.
`src/Engine/HarmonicBalance/HbGridBuffers.cs`, plus the device loops in `HbNewton.RunDevicePass`,
`HbNewton.ComputeDevicePortCurrents`, `HbNewton2D.EvaluateNonlinear2D` and
`HbNewtonNd.EvaluateNonlinearNd`. The Core side (the evaluator itself) is in `src/Core/RESOLVED.md`.

Where a model says its cost is arithmetic (`PrefersGridEvaluate`), the pass transposes the device's
port voltages to `[port][t]`, calls `EvaluateGrid` once, and reads the result back one sample at a
time through `HbGridSampler`. The `EvaluateBatch` path (out-of-process workers, H0) and the scalar
path are unchanged and still chosen first/last respectively.

**Measured, Release, M4, one drive point, best of 5:**

| fixture | shape | scalar | grid | speedup |
|---|---|---|---|---|
| Hero2/hero2.cnl | single tone, K=4, gridN=16 | 0.39 ms | 0.24 ms | **1.65x** |
| Hero2/hero2_convergence.cnl | single tone, K=5, gridN=32 | 0.61 ms | 0.31 ms | **1.95x** |
| Hero4/hero4.cnl | single tone, K=4, gridN=16, 2 devices | 0.95 ms | 0.68 ms | **1.40x** |
| Hero5/hero5.cnl | two tone (APFT, S≈128) | 3.89 ms | 2.69 ms | **1.45x** |
| Hero5/hero5_3tone.cnl | 3 tones, APFT | 3.51 ms | 2.18 ms | **1.61x** |
| Hero5/hero5_6tone.cnl | 6 tones, APFT | 5.17 ms | 3.57 ms | **1.45x** |

### The brief's premise measurements are stale, in TWO ways — the device pass is not 70-85% of a solve

The brief opens with "device evaluation is ~70% of a Newton iteration and 85% of a two-tone one".
Measured on these fixtures now, it is **30-45%**, which is why a 16x device speedup shows up as
1.4-2.0x on the solve. Two separate reasons, both worth knowing before sizing the next HB brief:

1. **HB-P1/P2/P3 landed in between.** The extractor rebuild, the Jacobian and the line search were all
   removed or reduced from the same iteration, so the same device cost is a smaller share of a smaller
   total than when the review was written.
2. **`HbEngine.Run` does not use the 2-D lattice for two tones.** The brief prices Hero 5 at
   "32x32 = 1,024 samples". `HbNewton2D.EvaluateNonlinear2D` is never reached by `Run` — verified by
   instrumenting both device loops and running all six fixtures, which produced zero 2-D calls. The
   multi-tone path is the APFT sampler (`HbNewtonNd`) at **S = 124-172 samples** for every one of
   two, three and six tones. `HbFft2D.GridSizes(5, 5, 1)` does return (32, 32), so the 1,024 figure is
   real for the lattice engine — it just is not what a two-tone HB run executes. Anything sized off
   "the two-tone grid is 1,024 samples" is sized off a path the shipped run does not take.

### `RunSinglePoint` takes the single-tone Newton loop whatever it is handed

The first version of `HbGridEvaluateTests` used `RunSinglePoint` for Hero 5 and reported a 10-entry
V cube for a fixture whose lattice has 31 mixing products — it had silently measured the single-tone
path twice and called it a two-tone bit-identity test. Only `Run` dispatches a multi-tone parameter
set to the 2-D / APFT engines. A multi-tone test written against `RunSinglePoint` passes vacuously.

### The counters have to allow for the nonlinear-DC pre-solve

`NonlinearEvalDiagnostics.ScalarCalls` never reaches zero on the grid path and should not: the DC
pre-solve that seeds the initial guess evaluates one operating point per Newton step (nothing to
batch — brief §8 excludes it) and lands on the same counter. The assertable property is that no HB
DEVICE PASS went sample by sample, i.e. the count is below `gridN`. On Hero 2 that is 4 against a
reference run's 52.

### Allocation

The per-sample device allocation is gone; what still scales with the grid is the pass's OWN buffers
(`iTime`, `qTime`, `dgTime`, `dcTime`, `vTime` and the FFT temporaries), identical on both paths and
untouched by this brief. Measured on Hero 2, one `EvaluateNonlinear`, 16 -> 256 samples: the scalar
path's allocation grows 198,432 B and the grid path's 116,136 B, so **343 B/sample of device garbage
is gone** and the remainder is the pass itself. At the model level the claim is exact —
`SddModel.EvaluateGrid` on a warm `GridResult` allocates **zero** bytes at both 32 and 1,024 samples.

### `HbGridBuffers` is thread-static on purpose

The result buffers must survive from one Newton iteration to the next or the allocation this brief
removes just moves up a level. `EvaluateNonlinear` is a public static entry point with callers in
three engines and in the test suite, so threading a cache parameter through every overload would be a
wide change for a private concern. A device pass is single-threaded — the parallel split lives inside
`SddModel.EvaluateGrid`, over samples — so per-thread state is both sufficient and isolating.

`HbGridSampler` copies a dozen doubles per sample rather than rewriting the three engines'
accumulation loops. Those loops are the KCL port stamp (`PortAdd`/`PortAdd4`, four signed corners per
port pair) and the w>=2 bucket accumulation: the part of the device pass least worth transcribing
three more times, against a copy that is under 1% of what the brief removes.

### The parallel threshold of 256 measured right

Below it the fork/join loses (S=128: 368 ns/sample parallel against 280 serial); at 256 it is a small
win (199 against 265); from 512 up it is 2-3x (S=1024: 102 against 243). `HbNewtonNd`'s 124-172
samples therefore stay serial, which is the correct call for the fixtures as they run today — the
parallel path is there for the larger grids a denser APFT or the lattice engine produces.

## The nonlinear branch row — an equation-defined device that holds a VOLTAGE (2026-09-01)

`docs/sonnet-briefs/brief-spice-behavioural-sources.md` M4. A behavioural voltage source states
`V(a) − V(b) = f(v, i)`, which is a Group-2 relation: it is *between* node voltages, and no
combination of currents expresses it. The device carries a branch-current unknown of its own and a
row in the Newton system that is not a KCL row.

**It is spelled `V[p]` on the SDD, not `F[…]`.** `sdd.md` recorded a free-form implicit equation as
out of scope and `ComponentModelFactory` hard-errored on `F[`. A free-form `F[…] = 0` would need the
engine to work out which unknown each equation belongs to; `V[p]` says which port is constrained,
reads the way `I[p,w]` reads, and is exactly what a behavioural voltage source is. The `F[…]` error
now points at it. A port stating both `V[p]` and a current equation is refused — that is a
contradiction, and one of them would silently win.

### Three facts the brief got wrong about where the seam is, all in the same direction

1. **`NonlinearDcEngine`'s linear stamp pass SKIPS nonlinear models** (`if (ec.Model.Kind !=
   ModelKind.Linear) continue;`). The brief's fact 1 says it "runs for nonlinear models too", which is
   how `Vdc` and `IProbe` come to have branches — but they are linear. So a nonlinear model owning a
   branch needs the pass to be opened for it explicitly: `ComponentModel.BranchEquationCount` is what
   that loop now asks, and it is the only reason that property exists.
2. **The S-parameter engine never calls `Stamp` on a nonlinear device at all** — it calls
   `StampLinearized`. So the branch, the ±1 KCL coupling and the linearised constraint row are all
   stamped there too, and the branch is RE-ALLOCATED rather than reused: branch numbering belongs to
   whichever assembly is being built, which is the same rule the SDD's control-branch indices already
   follow.
3. **Harmonic balance cannot carry it, and "one stamping loop in the HB extractor" understates it by
   a long way.** The HB unknowns are the voltage phasors at the nonlinear-facing nodes — that is the
   formulation, not an implementation choice. A nonlinear branch unknown is neither a node voltage nor
   reducible into the linear network (its own row is nonlinear), so carrying one means bordering the
   Newton system with an extra unknown per constraint per harmonic and threading that through the
   extractor, `BuildJ`, the back-solver, the warm start and both the 2-D and N-D variants. **Not
   built.** `HbEngine`'s constructor refuses such a circuit by name instead — which it must, because
   the device evaluates to zero port current, so an HB run that ignored the constraint would converge
   quickly on a circuit in which the source is simply absent.

### Charge: the collapse WAS built, and it is what makes M4 complete (2026-09-01)

`spice-models.md` §8.8's idiom — a behavioural voltage source driving a linear capacitor — works
exactly at DC and in S-parameters through the general path, with no pattern recognition:
`SddBranchEquationTests.T7` compares the port susceptance against the **analytic derivative of the
charge function** at three biases and holds to 1e-7 relative; `T8` shows the pair is bit-comparable
to the plain capacitor it degenerates to; `T6` pins §8.8's own claim that the interior node is
non-singular at DC with the capacitor open (KCL there forces the branch current to zero and the branch
row fixes the voltage — two equations, two unknowns).

**§13's open question 5 has a better answer than the brief expected.** The collapsed path
(`E` + `C` → one SDD charge equation) is not an optimisation that saves a node and a branch row: it is
the ONLY formulation harmonic balance can carry, because the collapsed device states a charge and HB
has applied `jkω` to charge harmonics since it was written. The algebra is exact and unconditional —
with the `E` from `n+` to `mid` and a linear `K` from `mid` to `n−`, the stored charge is
`K·(v_port − f)` and the capacitor value cancels — and it needs one extra condition beyond "nothing
else on `mid`": **the expression must not sense the constrained pair itself**, since that pair ceases
to exist.

**So it is the default, not an optimisation gated on a measurement.** `SpiceChargePairCollapse`
rewrites the pair at import, unconditionally, whenever the pattern holds exactly. The brief made M4b
conditional on an HB measurement justifying it; that measurement cannot be taken while the general
path refuses HB, and the conditional had the dependency backwards — the collapse is what makes the
measurement possible at all, not what a measurement would justify.

**The two paths are held together entry-by-entry** (`SddBranchEquationTests.M4b1`, three
frequencies): the same physics written as the pair a file writes and as the one charge equation it
becomes, agreeing to 10 decimal places in S₁₁ at a bias where the charge is nonlinear. If they ever
disagree the collapse is wrong, not the general path.

**Mirrored spellings give one formula, which is worth stating because it looks like it should need
two.** Either terminal of the source may be the interior one — the source's minus into the capacitor,
or the capacitor into the source's plus — and both work out to `Q = K·(v_port − f)` over the pair's
two outer terminals. The collapse tries both ends and needs no case analysis beyond that.

**Ground is never an interior node, and neither is a port**, whatever the definition's own element
list says: a port is wired by the call site, and ground is shared by the whole design. Counting
elements alone would collapse a pair whose "interior" node is the ground rail of the entire circuit.

### The HB `I` cube reports a charge branch wrongly, and it predates all of this

Found while writing the charge oracles, and **not caused by this work**: for a series probe feeding a
charge-carrying device, `ds["I"][probe, k]` reports `C0·V_k` — the LINEAR charge, missing both the
`jkω` and the nonlinear part — and the SDD's own rows read exactly zero. A plain `NonlinearC` in the
same circuit produces byte-identical wrong numbers, so it is a pre-existing defect on the reporting
path and not in the solve: the node voltages are exact, and the current derived from them and the
feed resistor matches the analytic Fourier coefficients to four digits.

The oracles therefore read the current as the drop across the feed resistor — node voltages are HB's
own unknowns, so that is the solved answer and nothing else. **The `I` cube defect is unfixed and
deliberately not worked around**; it needs its own look.

### Charge, and what the general path is verified against

### The two HB charge oracles the brief asked for, now that they can be run

`spice-models.md` §9.5.4's gates 2 and 3, which need a large-signal run and so could not exist while
the pair was a branch row:

- **Charge conservation** (`M4b2`) — `∮ i dt = 0` over one period is, in the frequency domain,
  exactly "the DC member of a pure charge's current is zero". Measured at 9e-11 of the fundamental.
  The drive carries a **DC offset** so the question is not vacuous: at 0 V a charge device is an open
  and every formulation passes trivially; at 0.4 V any resistive contamination of the charge term
  would pass a DC current, and nothing else catches that.
- **Harmonic content** (`M4b3`) — the gate that fails if the charge were linearised about the DC
  point rather than evaluated in time and transformed, because a linearisation generates NO second
  harmonic at all and still converges. With `Q(v) = C0·v + a·v²/2` driven at `V₀ + A·cos ωt`, the
  amplitude ratio `|I₂|/|I₁|` is `a·A / 2(C0 + a·V₀)` — **independent of ω and of whatever phasor
  normalisation the engine uses**, which is why the RATIO is what is asserted rather than either
  harmonic. Measured 0.3572 against 0.35714.

**What HB still refuses** is a voltage constraint that is not a charge — a logic or behavioural block
written as one. That is unchanged, and `HbEngine`'s message now names the charge form as the thing to
write instead, because the earlier wording asserted that the importer collapsed such pairs at a point
when nothing did.

### `DBranchC` is on the critical path, as the brief said

`∂g/∂i` with respect to another device's branch current. `SddBranchEquationTests.T4`/`T5` gate it on
the answer AND on the iteration count: the constraint is linear in `_c1`, so with the exact entry
stamped the augmented system is linear and Newton settles in ≤3 steps. A missing entry still
converges — quasi-Newton does — which is why the count is what is asserted.

### A control-current reference names a SIBLING, and did not used to

`C[1]=V_sense` inside a cell means that cell's own `V_sense`. Both engines matched the bare name
against the flattened `InstancePath`, so it resolved at the top level and nowhere else — an imported
subcircuit whose behavioural source reads a sensed current would work as a testbench and fail the
moment the same definition was placed as a cell, which is the ordinary way to use it.
`NonlinearDcEngine.FindControlTarget` tries the owner's own path prefix first, then the name as
written; the S-parameter engine calls the same method, because the two disagreeing means a design's
S-parameters are taken about a bias its own sensed current never reached.

### `VCVS`

The E element's linear half, and a real component rather than an SDD with an affine equation:
53 of the 234 controlled-source lines measured are ideal, and turning each into a nonlinear device
would make a linear macromodel nonlinear — an S-parameter run on it would start needing an operating
point it has no reason to need. Group 2 where `VccsModel` is Group 1, frequency-INDEPENDENT unlike
`VdcModel` (a bias becomes a short away from DC; a transfer holds at every frequency), and
referenceable by `C[n]` for the same reason a `Vdc` is.


### A model that carries its own thermal RC was warned about on every run (2026-09-03)

`ReportThermalNodes`'s ground-reference test is "the reference is zero while the ambient is not",
which catches a thermal network wired to the electrical ground node instead of to a source holding
the ambient — a several-percent error with no symptom. The reference it reads comes out of the LINEAR
network, one solve with the nonlinear devices contributing nothing.

That is exactly how a **self-contained** electrothermal model reads. Some compiled models carry their
own `1/rth` and `ddt(cth·T)` on their thermal node and add `$temperature` to it themselves, so the
node they solve for is the RISE above a thermal ground — correct, and the whole design of the model.
Such a device contributes nothing to the linear stamp (its conductance lives in a Jacobian the linear
system never sees), so its node reads as referenced to zero for exactly the reason it should. The
warning fired on every correctly-modelled part of that kind, on every run — which is what stops the
real warning being read.

`MeasureThermalNodes` already had both halves of the answer; it was collapsing them into one number.
It now also records **which side the path is on**: the device's own conductance is a real path while
the network's is not. `ReportThermalNodes` skips the ground test for such a node and nothing else
changes — a design that wires its own thermal network to ground still has a network path, and is
still reported, which is what keeps the skip from being a blanket.

The elaborator's half needed no change: `PinUnreferencedThermalNodes` already asks the device for a
positive conductance on the node's own diagonal before adding an ambient source, and a self-contained
model supplies one. That was verified rather than assumed — holding such a node at the ambient would
make the model compute ambient + ambient + rise, which is finite, plausible and wrong.

Measured end to end in `Engine.Tests/External/VerilogAConnectedTerminalsTests` against a compiled
model of that shape (`tools/fake-osdi-model`'s `crf_therm`, which needs no kit), and at the fixture
level in `ThermalNodeRobustnessTests` — where the previously-asserted warning is now asserted absent,
with the mis-wired-network case beside it asserting it still fires.

### Harmonic balance and the DC engine agree on an external device at zero drive (2026-09-03)

Every static check on an external device compares the model's outputs with each other — a Jacobian
against a finite difference of its own currents, a capacitance against one of its own charges — and
all of them pass on a device whose `i` and `q` have been swapped, because both halves are then
self-consistently wrong. The two engines are not self-consistent with each other: DC reads only `i`
and `dg`, HB carries `q` and `dc` through its own frequency-domain assembly.
`ExternalDeviceHbDcConsistencyTests` pins the two together at zero drive, with a driven case beside
it so the comparison cannot be of a circuit the drive never reached. The two land 3.7e-5 V apart on a
3.56 V node, which is the two solvers' own residual tolerances (a residual of e amperes is e × R
volts) and four orders of magnitude clear of what a current/charge mix-up costs.

## PM3 — an operating-point variable is a WAVEFORM in harmonic balance (2026-09-03)

`project-brief-physics-model-opvar-readback.md`. The provider seam is in `src/Core/RESOLVED.md`.

### The whole feature is a question of position in time, not of arithmetic

A compact model writes its op-var storage during a **load**. So a read describes whichever bias was
evaluated last, and a read taken one call too late returns a perfectly plausible number belonging to a
different operating point. Nothing objects: the residual is fine, the plot is smooth, and under a
parametric sweep it is an off-by-one along the sweep axis.

That is why `NonlinearDcEngine` collects inside the solve's own scope
(`CollectOperatingPointVars(x)`, called from `SolveDirect`/`SolveRamped` at the converged `x`) rather
than in `DcResultPacker`, and why the evaluation at that bias is **deliberate rather than inherited**.
The last thing a solve does happens to leave most devices at `x` already — but "happens to" is not a
property anything holds shut.

Only a CONVERGED point collects. Under `IfNecessary` a failed direct attempt is routinely followed by
the ramp, so collecting there would spend a round trip per device on an answer about to be discarded —
and a read-back for a bias the solve never reached is worse than none.

### D1 was answered "per sample in HB", which the brief itself listed as a phase of its own

The owner chose option 3 over the recommended DC-only. It is the right shape and it is not a comment:
a transconductance at 30 dBm swings between pinch-off and full channel once per RF cycle, and no
single number describes it. `HbOpVars` captures per sample and transforms to the analysis' own
spectral axis — `HbFft.Forward` at one tone, `HbNewton2D.ForwardConv2D` at two, `HbApft.Analyze` at
T — so `OP` is `[opvar, spectral]` **Complex**, exactly like `V` and `INl`. Harmonic 0 is the cycle
average, which is what a designer means by "gm at this drive".

**It is a second evaluation, not a hook in the Newton loop, and that is deliberate.** The Newton pass
does not know which iteration is its last, so capturing there means asking the provider for op-vars on
every iteration and discarding all but one set — the cost paid per iteration instead of per point. One
pass at the converged voltages is one round trip per device per HB point and is unambiguously at the
answer. (`ComputeDevicePortCurrents`'s own `useLastPass` shortcut is not available here for the same
reason it exists: it reuses a buffer, and there is no op-var buffer to reuse.)

### ONE cube on a labelled axis, never one cube per quantity

`OP` + `__OpVars`, matching `I` on `branch`. A physics-based model declares tens of quantities, so a
handful of devices is hundreds of names; as separate cubes that is a `DataSet` nobody can navigate and
a picker with no structure to group by. Labels are `"<InstancePath>.<opVarName>"` and are sorted
**ordinally**, so a picker's rows do not move between two runs of the same design.

### `DC1.OP(...)` joined the V/INl/I accessor branch rather than getting one of its own

It IS that shape: one labelled axis, optionally a spectral axis, optionally sweep axes in front. The
only change in `Evaluator.EvalQualifiedAccessor` is a third `axisName` arm (`"opvar"`), which also
gets the existing refuse-by-name-with-the-available-list behaviour for free. **Qualified only** (D2,
the owner's choice): there is no unqualified `OP(...)`, so a testbench running two analyses never has
to be guessed about.

### Values are the model's own, per device, UNMULTIPLIED

A device multiplier scales what the netlist stamps, not what the model computed. A `gm` read back
from a part placed with `m=4` is one finger's, because that is the number the model wrote and
circuitRF has no basis for deciding which of a model's own quantities are extensive.
