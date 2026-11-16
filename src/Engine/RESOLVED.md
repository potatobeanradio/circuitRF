# src/Engine — resolved briefs (detail, off the CLAUDE.md growth path)

Mirrors `src/Engine/Loadpull/RESOLVED.md`'s pattern: a completed brief's detail lands here, one `##`
section per brief, sparingly — only for findings that are still true, still surprising, and would cost
someone real time to rediscover. `CLAUDE.md` stays for durable, still-true conventions.


## `LinearDcEngine` — the ω = 0 linear solve, and why it is not `NonlinearDcEngine` (2026-09-18)

Added for railRF brief 5; the full record is in `src/Design/RESOLVED.md` under that brief, and the
part that belongs here is the reason a second DC entry point exists at all.

`NonlinearDcEngine` materialises its augmented system as a **dense `double[n,n]`** before assembling a
CSC out of it. That is what makes `MeasureThermalNodes` and `ResidualPerUnknown` cheap to write and it
is entirely reasonable for a circuit; it is unusable for a resistive mesh, whose extractor ceiling is
400,000 nodes — 1.28 TB of that array, and 800 MB at a modest 10,000. Newton is the other half: such a
netlist is LINEAR, so there is one assembly, one factorisation and one back-substitution, and the
iteration, the source stepping and the convergence trace have nothing to do.

So `LinearDcEngine` is that engine's **linear pass with the dense array removed** — it implements no
matrix, no ordering and no factorisation, and keeps every convention that pass sets: Port and Term
inert at DC, gmin on every voltage row, mutual inductance stamped after everything else, and a branch
current flowing from its element's FIRST node to its SECOND. It refuses a netlist carrying a nonlinear
model rather than solving one incorrectly, and it returns the whole solution vector — **branch
currents included, because an ideal source's current is a solution unknown and cannot be derived from
node voltages**, and it is exactly the number "which source carried what share" is made of.

`LinearDcSolution.AsDcResult()` hands the answer back in `NonlinearDcEngine.DcResult`'s shape so
`DcResultPacker` packs it unchanged: an ω = 0 linear run yields the same `DataSet` every other DC run
yields, and nothing downstream learns a second result type.

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
That level is what `HbLineSearchTests.AThreeToneColdSolve_WhereTheLineSearchBacktracks_Converges`
pinned until issue 65 removed the excess drive (it now pins 24 and 26 dBm, where the line search still backtracks), and the test says in its own comment that the two-tone half of the brief's expectation was not
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

## A throwaway stamp pass was warning about a frequency nobody swept (2026-09-06)

Every S-parameter run of a netlist containing an ideal line printed:

```
[circuitRF] TLIN:TL1: sin(θ)≈0 at f=0.159155 Hz (θ=1.43239E-08°, a quarter-wave
open/short resonance); clamping |sinh γl| to a floor and proceeding.
```

on a sweep of 1–10 GHz. 0.159155 Hz is 1/2π — i.e. ω = 1 rad/s.

### The pass reads topology, but Stamp does more than build a matrix

`SParameterEngine.CollectPortsAndBranchLabels` exists to capture port branch indices and a
branch→component-name map. That is topology, which is invariant across ω, so it stamped at a
placeholder ω=1 into a temp `MnaSystem` it then threw away. The matrix genuinely did not matter — but
a model also **warns** from inside `Stamp`, and a warning names the frequency it was handed. A
`TLIN`'s electrical length is proportional to frequency, so at 0.159 Hz θ≈0 for any sane reference
frequency and the degeneracy check fired unconditionally.

### The louder symptom was the quieter bug

The false message was cosmetic. The real damage is that `TLineModel`'s warning is latched **once per
instance** (`_warnedDegenerate`), so the phantom report CONSUMED the latch before the sweep began —
and a genuine resonance at a real swept frequency was then never reported at all. Verified directly:
after the fix, a 1–10 GHz sweep of a 90°-at-1 GHz line now reports `sin(θ)≈0 at f=1E+10 Hz (θ=900°)`,
which is a true half-wave degeneracy at the last sweep point and which the old code never printed.

### The fix, and why it is not a new mechanism

The pass now stamps at `freqsHz[0]` (falling back to 1.0 on an empty grid) — which is exactly the form
the sibling throwaway pass in `ResolveSParamControlBranches`, a few hundred lines down the same file,
was already using. A warning raised by the preliminary pass is now one the per-frequency loop would
raise anyway, and `AddWarningOnce` dedups it.

S-parameters are byte-identical across the change, checked on a written Touchstone.

**Considered and not done:** routing `TLineModel` through `IReportsWarnings` instead of
`Console.Error`. It is the better channel and eleven models under `src/Core/Devices` still write
directly, but that is a sweep of its own and would not have fixed this — a warning on the right
channel naming 0.159 Hz is still wrong.

Gate: `tests/Engine.Tests/Devices/TLineResonanceWarningTests.cs`.


## AUT-8 R-aut8-4 — the VCCS net-count refusal moved from the engine to elaboration (2026-09-07)

`CurrentSourceTests.Vccs_WithThreeNets_IsRefusedAndNamesTheInstance` used to elaborate a three-net
VCCS successfully and assert that `NonlinearDcEngine.Run` refused it. The refusal is now raised by
`Elaborator` — same substance, and richer (it names the type, the instance, the count given and the
count expected) — so the test asserts at elaboration instead.

**Why the move is the point rather than a side effect.** `circuitrf check` runs no analysis by design
(AUT-4 R-aut4-1), so a refusal only the engine could raise was one `check` could never make. A
mis-wired instance line was therefore invisible to every headless caller until it ran — and for the
parts whose expansions are guarded on an exact net count (`Tuner`, the FET/BJT/MOS families) it was
invisible after it ran too, because a short line skips the expansion in silence and simulates to
completion.

## WSP-1 — the WSProbe and the `wsp` matrix in the S-parameter engine (2026-09-08)

`brief-wsprobe-1-probe-and-wsp-matrix.md`, built in one pass; design in
`docs/design/stability-wsprobe.md`. Every gate (a)–(n) is in `tests/Engine.Tests/Linear/WSProbeTests.cs`
and the CLI half in `tests/Ui.Tests/Cli/WsProbeCliTests.cs`. Three things the brief stated that the
physics or the code overrode, and two observations worth the next reader's time:

- **The brief's Hero 1 probe placement has NO feedback around either probe.** It put P1 between `L1`
  and `a1` and P2 between `a2` and `L2`, claiming "the C5 and C3 paths make y12 ≠ 0". They do not: a
  probe has feedback around it only when a path joins its two sides other than through the probe, and
  ground does not count. With P1 there the G side is `L1` + `Term1` to ground — a dangling one-port —
  and `ZG = 1/YG` held to 1e-15 at every frequency, which is what gate (e)'s last assertion caught
  (`ZG·YG = 1 ⟺ z12·(z12 + z21 − z11 − z22) = 0`). The fixture puts the probes at the two-port's
  ports instead, where `C5` plus the two-port closes a loop around each, and the difference is then 2.1×.
- **The parallel resonator cannot use the series fixture's values.** The brief says "same values"
  (`R1 = −20 Ω`, `RS = 10 Ω`) for Fig. 34. A parallel negative resistance starts up only when its
  conductance exceeds the load's: `Re(1/H0) = 1/R1 + 1/RS = −0.05 + 0.1 > 0`, so with those values
  the circuit is stable and the Kurokawa signature the gate asserts is absent. `R1 = −5 Ω` in the
  fixture. Eq. 119/120 hold for either; only the signature depends on it.
- **The counters gate needed a counter that did not exist.** `MnaSystem.Factorizations` (incremented
  on the success path of `Factorize`) and `SParameterEngine.RunWithStats` (factorisations,
  back-substitutions, pattern builds) were added for R-wsp1-14(l); `Run` calls `RunWithStats` and
  discards the stats, so nothing about the ordinary entry point changed.
- **"No-probe runs are byte-identical" was measured across builds, once, by hand** — a worktree of the
  pre-change commit, `sparam` on Hero 1 and Hero 1B to `.npy` and `.mat` from both, `cmp`: identical in
  all four. What the test holds is the structure that makes it true: an unprobed run's DataSet is
  exactly `{S, Z0}`, it does one back-substitution per port per frequency and one pattern build. There is
  no committed `.npy` golden and there should not be one.
- **A negative-R notice printed once per elaborated COPY under the frequency-parallel path — fixed.**
  The port-less resonator fixture has 251 points, so `PlanDegree` elaborates three copies, and
  `ResistorModel`/`ParallelRlcModel` wrote their `R < 0` and `R = 0` notices to `Console.Error`
  directly (with a per-instance `_warned` flag) — three identical lines on the terminal, and NOTHING
  in `ElaboratedNetlist.Warnings`, so the Messages pane and `--json` never saw them at all. Both models
  now implement `IReportsWarnings` and queue the notice keyed per instance path; the engine drains it
  after the stamp and the SP-P3 merge dedupes the copies (`ModelNoticeRoutingTests`). The two
  `SanityTests` that captured the console read the warnings list now. Ten more direct console writes
  remain in the microstrip, TLINE and mutual-inductance models; same shape, same fix, not done here.
- **On the legacy path the terminated assembly is the SAME stamp sequence plus one diagonal per port.**
  Skipping the ports (as the wave path does) would shift every later branch index and break an SDD's
  resolved control branch; adding `−Z0` to each port branch's diagonal keeps the numbering and turns the
  0 V drive into a termination. A port with `Z0 = 0` exactly is a refusal (`wsprobe.port-short`), never
  a large conductance.

## WSP-9 — the margin cubes, the threshold, and what a load-pull envelope cannot see (2026-09-08)

- **Two more default cubes per probe**, `SM_Y0:<label>` and `SM_H0:<label>`, computed by
  `WspMargin.Of` on the SAME `WspProbeQuad` the six existing defaults come from — one implementation,
  so a run's cube and the built-in over the run's `wsp` cube are bit-identical (overview D-2, gated).
- **`MarginThreshold=` had to reach the engine through `AnalysisSettings`**, because
  `SParameterEngine.Run` takes a netlist and never sees the `SParameterAnalysis`. Three call sites map
  it in (the CLI's `sparam`, `SchematicRunService`, `ParametricSweepEngine.RunSParam`); the sweep does
  it PER POINT, since the threshold belongs to the inner analysis and not to whatever settings the
  sweep was started with. `AnalysisSettings` is a plain class with ~25 init-only properties, so the
  copy is an explicit `WithMarginThreshold` over `MemberwiseClone` rather than a `with` expression —
  making it a record to gain `with` would change its equality from reference to structural for every
  existing holder, which is a far larger change than the one knob being set.
- **The diagnostic is an Info NOTE, not a warning** (`AddNoteOnce`,
  `wsprobe.margin-below-threshold:<label>`). The −5 Ω split resonator is stable and fires it by
  design: a node one negative-resistance step from oscillating genuinely has little margin, so the
  message is "look here". A probe whose margin is NaN throughout (a degenerate node) never fires it —
  that probe already has its own diagnostic.
- **`MarginThreshold` had to be carried by three formats, not one.** `CnlReader`/`CnlWriter` (the
  standing check of `src/Core/CLAUDE.md` — a field the writer cannot say is silently absent from
  every GUI run, which writes a netlist and reads it back), the `.csch` DTO in
  `src/Design/Schematic/AnalysisSerialization.cs`, and the multi-segment merge in `CnlReader`, which
  REBUILDS the analysis: a second `type=sparam` line for the same name would otherwise reset the key
  to its default in silence. The default is not written out, so a document that never mentioned it
  round-trips byte-identically.

### A source/load stability envelope is structurally blind to an odd-mode instability

Measured on `testdata/wsprobe/ohtomo_type_a.cnl`, a redrawn Ohtomo Type-A (two devices in parallel,
balancing resistor `Rb` across the gates). At `Rb = 30 Ω` it is stable at 50 Ω; at `Rb = 100 Ω` the
odd mode starts up at 6.090 GHz and `SM_Y0` at the gate probe reads **−53.1 dB** there on the
fixture's own 991-point grid (−80.5 dB at 1,981 points — the notch is narrower than the step). **No
termination at `ρ = 0.9` on either side reaches it** — the odd mode sees both ports as virtual
grounds, which is Ohtomo's own thesis and the reason such an amplifier needs `Rb` rather than a
better match. An instability a VSWR sweep can expose has to live in a port-coupled path, so a
margin envelope that finds nothing is not evidence a circuit is stable.

The same fixture also demonstrates the document's p. 112–113 caveat rather than quoting it: the
reduced NDF over the S/G/L probe set reads **zero** encirclements at that start-up, and adding a
fourth probe on the *other* gate — nothing else changed — makes the same NDF read **2**. The odd mode
is differential across the two gates and only one carried a probe.

## The WSProbe under harmonic balance — WSP-5, the conversion-matrix small-signal solve (2026-09-08)

Findings from building `HbSmallSignal`, the `SS*` sweep on the `hb` directive, and the gates in
`tests/Engine.Tests/HarmonicBalance/WSProbeHbTests.cs`. Design note:
`docs/design/stability-wsprobe.md` §10; the frozen conventions are in
`src/Engine/HarmonicBalance/CLAUDE.md`.

### One probe frequency has no answer, and it is exactly the one the feature is about

`ω_ss` **commensurate with the fundamental at order 2** — `2·f_ss/f0` an exact integer, so `0`,
`f0/2`, `f0`, `3f0/2`, … — is the case the formulation cannot answer, and `f0/2` is precisely where a
parametric instability lives.

The reason is structural rather than numerical. The unknowns are one sideband family
`{ω_ss + kω0}`, and solving that family alone with the stimulus at its own zero sideband is complete
only while the family and its negation are **disjoint**. They coincide exactly when `2ω_ss` is a
multiple of `ω0`: there the response at `−ω_ss` is the conjugate of a retained unknown rather than a
separate one, and half of a real stimulus is silently dropped. Verified by construction — the map
`k ↦ −k−1` at `ω_ss = ω0/2` sends the sideband set into itself, and the injection's own conjugate
lands on sideband `−1`, which the right-hand side leaves empty.

The engine returns NaN across the `wsp` row there, with one warning naming the remedy. It does **not**
nudge the frequency, because a nudged sweep point silently reports a different circuit than the one
asked for, and it does not refuse the run, because a plausible round-numbers grid (`0.5 .. 3 GHz` step
`0.1` at `f0 = 2 GHz`) hits three of them.

**Consequence to know before reading a result:** a parametric instability at `f0/2` is found by the
grid points **either side** of it, where the pole pair approaching the imaginary axis shows Kurokawa's
crossing. Gate (e) measures this — 40 points over `[0.3 f0, 0.7 f0]` with none landing on `f0/2` finds
the crossing to within one 20.5 MHz step. A folded (real-split) formulation at exactly `f0/2` would be
the right answer there and is not built; `HbNewton.BuildJ` is that folding at `ω_ss = 0` and nothing
generalises it to a half-integer sideband yet.

### The Jacobian comparison is exact, not merely within tolerance

R-wsp5-9(b) folds `J_ss` at `ω_ss = 0` back onto `HbNewton.BuildJ`'s real-split matrix and the two
agree with **0 relative deviation on every entry** of a 20×20 Jacobian — not 1e-12, zero. That is
worth knowing because it makes the gate a genuine tripwire: any future change to `ConversionWeight`,
to `SafeGet`'s conjugation, or to the `jkω₀` charge rotation breaks it immediately rather than
drifting under a tolerance.

The fold itself is short but every factor in it is load-bearing: the residual scales by `2` on an AC
row (`F_k = 2·R_k`), the unknown by `½` on an AC column (`δV[k] = ΔV_k/2`), and the Maas §7.3 DC rules
**fall out** of those two scalings rather than being applied on top.

### `wsprobe.hb-device-no-spectra` is unreachable and was not built

R-wsp5-4 asks for a refusal naming an external device that cannot report spectra. There is no such
device: `HbNewton.BuildJ` needs `dg`/`dc` from every nonlinear device on every iteration, so a device
that could not supply them would have failed the Newton solve long before any small-signal solve —
and `ExternalDeviceModel` returns `Conductance`/`Capacitance` unconditionally because the worker
protocol requires them. A refusal that cannot fire is a claim the code does not make.

### The two-tone path has no derivative spectrum at all, so the lattice form costs a device pass

`HbNewtonNd` builds its Jacobian from `HbApft.AccumulateTripleProducts`, which consumes the derivative
waveforms as **raw time samples** — that is exactly what makes it tone-count-general, and it means
there is no `G[n,m,k]` to reuse. The conversion matrix needs coefficients at the DIFFERENCE of two
sidebands, which reaches order `2·MaxMixOrder`, and the order-`O` torus cannot resolve one.

So `HbSmallSignal.LatticeSpectra` re-synthesises the converged operating point onto an order-`2O`
torus and evaluates the devices there once per operating point. The embedding is exact —
`MixingLattice` enumerates by ascending total order, so every representative of the narrow lattice is
one of the wide lattice — but it is **looked up rather than assumed to be the identity**, because that
ordering is a locked contract about one lattice and not a promise about the pair.

`J_ss` is dense at `N_int·(2M − 1)` square, which is what makes a ceiling necessary: `MaxMixOrder = 15`
at two tones would ask for a 3,722² complex matrix (221 MB) factored once per probe frequency. The
refusal at `n_c = 2000` names which of `SSMaxHarm`, `MaxHarm` and `MaxMixOrder` binds.

### The two-tone oracle's own accuracy is bounded from both sides, and the bound is what sets the gate

Gate (d) drives the same operating point with a small second tone at the probe's terminals and reads
the `(0, 1)` mixing product — a path sharing no linearisation with the conversion matrix. Its accuracy
was measured over five decades of tickle amplitude and does **not** improve monotonically:

| tickle | series (V) injection | shunt (A) injection |
|---|---|---|
| 1e-2  | (non-convergent) | 4.4e0 |
| 3e-3  | 1.1e-5 | 1.3e-2 |
| 1e-3  | 2.9e-5 | 1.3e-3 |
| 3e-4  | 9.5e-5 | 1.2e-4 |
| 1e-4  | 2.9e-4 | 1.4e-5 |
| 3e-5  | 9.6e-4 | 1.8e-5 |

Two mechanisms, opposite in amplitude: an **absolute floor of ≈7e-10 A** on a mixing-product current
(the APFT's least-squares conditioning against a ‖F‖ = 1e-14 residual) makes a smaller tickle worse,
and the tickle's own size against the pump makes a larger one worse. The two optima differ by a
factor of fifty because this probe's `iS/iP` is fifty times its `iS/vS` — the shunt injection is a
CURRENT compared against ~10 mA of pump current, the series one a VOLTAGE compared against ~0.17 V.
`HbApftOversample = 3` halves the floor. **A single "small enough" amplitude does not exist**; the gate
uses 3 mV and 0.1 mA and asserts 5e-5.

The floor is also additive, not relative, so a cross term forty times smaller than its row's largest
entry is resolved forty times worse **in relative terms**. The gate therefore scales each comparison
against the largest entry of the same kind in that row. Scaling every entry against itself would
demand of the smallest one an accuracy the oracle has nowhere.

### Where the oracle's series source goes is a derivation, and two of the three placements are wrong

The probe's own series source sits BETWEEN its terminals, so `vP = v_G` is set by the generator branch
alone: `iS = vS/(Z_G + Z_L)` and `vP/vS = −Z_G/(Z_G + Z_L)`. An external source between the generator
network and the probe's G node reproduces `iS` but gives `vP/vS = +Z_L/(Z_G + Z_L)` — a different
number, because the measurement node then sits on the far side of the inserted EMF. Only a source on
the probe's **L** side with its first net facing the load (`V(load) − V(nL) = A`) reproduces both.

The first attempt used the G-side placement and disagreed with the conversion matrix by O(1) with the
`iS/vP` RATIO within 0.1% — which is the tell: a placement error preserves the ratio and moves the
absolute values, so a gate on `Y0` alone would have passed it.

### A pumped varactor divider's direct response is the wrong observable for its own threshold

Gate (e) needed the parametric threshold confirmed independently of the Kurokawa signature.
The obvious observable — the two-tone response at the tickle frequency — is measurably useless
(171 → 182 → 181 → 166 Ω over a 1 → 5 V pump sweep, non-monotone): the tank's resonance moves as the
pump's average capacitance shifts, pulling the tickle off resonance and hiding the parametric gain
underneath.

The pump's own **image** of the tickle — the `(1, −1)` product at `f0 − f_tickle` — has no competing
effect, since it exists only because the pump converts. It runs 31 → 101 → 136 → 158 Ω over the same
sweep, and the honest statement of approaching a **degenerate** threshold is the ratio: signal and
idler grow equal, 18% of the direct response at 1 V and 95% at 5 V, over exactly the pump range in
which the Kurokawa crossing appears (none at 3 V, one at 6 V).

### Two shared implementations came out of this, both to stop a second copy drifting

- **`src/Engine/WspCubePacker.cs`** — the `wsp` cube, the eight per-probe defaults, the three `__`
  metadata cubes, the degenerate-node warning and the margin-threshold note, written once for both
  the S-parameter `freq` axis and the HB `ssfreq` axis. Overview D-2 makes the derived metrics one
  implementation; the second half of that promise is that the two ANALYSES agree with each other, and
  a cube name or a NaN policy that existed twice would show up as an HB result the Data Display draws
  differently from the S-parameter result of the same design. `SParameterEngine`'s own `TermAt` now
  delegates to `WspCubePacker.TermZAt` for the same reason.
- **`HbLinearExtractor.LinearPartitionAt`** — the factored linear partition as a handle. Every call to
  `Extract` or `SolveFullNetwork` re-stamps the whole network to compare it against the cache, and the
  small-signal solve performs `4N` back-substitutions against ONE matrix per probe frequency; routing
  those through the existing entry points would have stamped the network `4N` times for a matrix that
  cannot have changed in between.

**A Tuner is deliberately NOT a termination in `__WspTermZ`.** Its impedance is set per harmonic BAND
against the run's own tone ruler, so it has no single declared `Z` that `ZG` could be required to equal
at every point of a small-signal sweep — which is exactly the precondition `wsp_terminate` checks.
Reporting its tone-band value would let the envelope re-terminate a shunt whose real value is a
different number at every `ssfreq`. A `Term`, `Port` or `P1Tone` shunting a probe terminal is
frequency-flat and does qualify, which is the common case in an HB testbench.

### hero2 is a poor small-signal fixture and the brief's own gates use a new one

`hero2.cnl`'s per-harmonic `Z_Port` bands collapse to near-shorts (1e-6 Ω) everywhere except the
fundamental, so a `wsp` computed at any other probe frequency compares two numbers near zero on both
sides of the gate — a comparison that passes for the wrong reason. `testdata/wsprobe/hb_pumped_two_port.cnl`
is frequency-flat by construction (plain R, L, C) and carries a time-varying conductance AND
capacitance with feedback in both, so the conversion matrix's `G`, `C`, sideband and node-pair indices
are all exercised rather than only its diagonal. Its unprobed twin is the transparency gate.

The transparency and byte-identity gates are also NOT what the brief's words suggest: a WSProbe SPLITS
a node, so a probed netlist genuinely has more nodes than an unprobed one and its `V` cube has more
rows — the two cannot be byte-identical in shape. What is asserted instead is the pair of claims that
matter: the same PROBED netlist with and without `SS*` keys is byte-identical in `V`, `I` and `INl`
(the small-signal solve does not perturb the Newton solve), and the probe's two terminals carry the
same spectrum as each other and as the merged node of the unprobed netlist.

### A per-point engine diagnostic does not survive a parametric sweep, and never has

`ParametricSweepEngine` re-elaborates the netlist **per point** and disposes it (a correctness
requirement — an external provider's device lives in a worker process and re-elaborating is the only
thing that hands the instances back), and it never copies that netlist's `Warnings`/`Notes` anywhere.
`SchematicRunService` drains the OUTER netlist, which no point ever wrote to.

So R-wsp5-1's `MarginThreshold` note and §4's "the threshold diagnostic names the drive level as well
as the frequency" cannot be delivered through the note channel under a drive sweep — which is exactly
the case the drive-swept margin fan is for. An HB non-convergence warning inside a sweep has always
vanished the same way; this is not new and not specific to the WSProbe.

What IS delivered is the number: `SM_Y0`/`SM_H0` stack over `{Pin, ssfreq}` and `__WspMarginThreshold`
rides through unstacked, so the Data Display draws the fan and the line it was judged against. Adding
the drive level to the note's text would have been inert, so it was not added. Propagating per-point
diagnostics out of the sweep would be a real improvement and a broad behaviour change for every
analysis — out of this brief's scope, and it belongs to whoever next touches that engine.

## WSP-6 — the normalized determinant function, native to the engine (2026-09-08)

The design note (`docs/design/stability-wsprobe.md` §11) carries the contract, the passivation table
and the derivations. What follows is only what cost time to find and is still surprising.

### Reading a count off half a contour: the factor of two, and where it is NOT applied

The argument principle counts turns around the CLOSED Nyquist contour; a sweep runs `ω ≥ 0`. Property
4 (`NDF(−ω) = conj NDF(ω)`) makes the missing arm turn through exactly the same angle — writing
`φ(ω) = arg NDF(ω)`, the negative arm runs `−φ(∞) → −φ(0)` and contributes `φ(∞) − φ(0)`, the same as
the positive one — so **right-half-plane poles = 2 × (the swept locus's own net turn)**, referred to
the sweep's first sample.

This is not a convention that could have gone either way, and gate (a) is what settles it: the
analytic single-loop stage has a single REAL right-half-plane pole, and under the doubling it reads
exactly 1. Counting the swept locus's turns literally would make it read one half. A conjugate PAIR —
what an oscillator actually has — reads 2, which is why the document's own two resonators read 2 and
not the 1 the brief expected.

**`WspEnvelope.LoadpullNdf`'s `Encirclements` field carries the UNDOUBLED count**, as WSP-9 shipped
it, and is not referred to the sweep's first sample either. Correcting it was tried and reverted: it
flips WSP-9's own gate (i) at one grid point of the Ohtomo fixture, where the REDUCED NDF over an
incomplete probe set wanders by about half a turn without ever going round — a number that is not a
count in the first place. The two agree about *whether* a point is unstable, which is all R-wsp9-5's
threshold reads, and WSP-6's gate (k) asserts both the verdict and the factor of two at 288 grid
points. **Reconciling them is an owner decision**, recorded in `src/RfCore/RESOLVED.md` too.

### Passivating a NEGATIVE RESISTANCE puts a constant factor in the NDF, and it is not a bug

A dependent source contributes ADDITIVELY to the network determinant, so removing it leaves every
constant factor alone and the ratio tends to exactly 1. `R → |R|` does not: it changes an element
VALUE, and an element value can be a *factor* of the determinant. The document's series resonator is
the clean example — its determinant is `g·g_S·[s²LC + 1 + sC(R1 + RS)]`, with `R1` appearing both in
the prefactor and in the bracket, so `Δ/Δ0 = (g/g0)·(bracket ratio) = −1 × (bracket ratio)` for all ω.

Consequences, all real:

- The NDF tends to −1 rather than 1. **The pole count is unaffected** — a constant turns through no
  angle — and the count was measured correct (2) with the locus sitting on the negative real axis.
- Platzker's property 3 is stated for a `Δ0` that differs from `Δ` by removed dependent sources
  alone, and a plain `|NDF(f_max) − 1| > tol` check therefore fires on a correct answer, telling the
  user to extend a sweep that is already long enough. The check now asks whether the locus is still
  CLOSING on 1 (its distance from 1, against the same distance a decade lower) before deciding which
  of two messages to give — `ndf.no-asymptote` or `ndf.constant-asymptote`, the second quoting the
  extrapolated limit.
- A Richardson extrapolation of the `1/ω` tail was tried first as the discriminator and abandoned:
  the residual is `−a/(ω₀ω₁)` from the `1/ω²` term, and on a five-decade sweep whose resonance sits
  three decades up, the second (validating) extrapolation lands ON the resonance rather than in the
  tail. It survives only as the number the message quotes.
- There is no passivation of a negative resistor that avoids this. Decomposing it into `+|g|` plus a
  VCCS of `−2|g|` leaves the same total conductance in `Δ`'s prefactor, so the same −1.

### A VCCS across its own control is a conductance, and passivating a SERIES one opens the branch

`G → 0` removes the element. That is right for a SHUNT negative conductance (the parallel resonator)
and wrong for a SERIES one: the branch becomes an open, two nodes float, and `Δ0` is a different
topology. It shows up as the NDF diverging linearly with ω instead of tending to a constant — which
is Platzker's property 3 catching a bad `Δ0` exactly as it is meant to, and is why
`testdata/ndf/series_resonator_negr.cnl` uses a plain negative resistor and only the parallel fixture
uses the VCCS the brief describes.

### Property 2 forbids a counter-clockwise ENCIRCLEMENT, not a counter-clockwise stretch of phase

The first threshold was half a turn, on the reasoning that the unwrap already assumes better than
half a turn per step. That fires on the Ohtomo two-device amplifier, which backtracks by 0.74 of an
encirclement near the top of a seven-decade sweep with a `Δ0` that is provably passive — every
element of it is an ordinary R, L or C once the two transconductances are zeroed. A locus is free to
wander as long as it does not go round; the threshold is a whole encirclement.

### The passivity guard earns its keep on the first real device it met

Hero 2's SDD FET at its own quiescent bias has a slightly NEGATIVE output conductance — the model's
`_v2*th` drain-induced-barrier-lowering term makes increasing `Vds` raise the effective threshold —
so the PASSIVATED block fails `Y + Yᴴ ⪰ 0` by 4.7e-4 of its own scale, at every bias point tried. The
run reports it and still emits the NDF, with the count declared unreliable. That is the honest answer
and it is the check §8 p. 113 asks for.

The minimum eigenvalue is read off an SVD by shifting: `H = Y + Yᴴ` is Hermitian, so its singular
values are the magnitudes of its eigenvalues and cannot tell a negative one from a positive one, but
`H + σ_max(H)·I` is positive semidefinite and ITS singular values ARE its eigenvalues. Hence
`λ_min(H) = σ_min(H + σ_max(H)·I) − σ_max(H)`, at the cost of one extra SVD of a matrix at most a
handful of ports wide. NumFlat has no Hermitian eigendecomposition; this needs none.

### Hero 2's own testbench cannot be used for a stability analysis, and the reason is instructive

Its per-harmonic `Z_Port` terminations are `1e-6 Ω` outside the tone bands — a dead short at both
ports — so neither node can develop a voltage and the stage is unconditionally stable: its NDF is 1
to eight decimal places at every frequency, whatever the transconductance is. `testdata/ndf/
hero2_sdd_stage.cnl` therefore keeps the DEVICE and its bias and gives it ordinary 50 Ω terminations.

Even then it will not oscillate, and no lead inductance changes that: Hero 2's SDD is a pure
transconductance with **no charge storage at all**, so `∂I_g/∂V_d = 0` and the stage is strictly
unilateral. A unilateral stage has no loop to close. Adding `Cgd` gives it one, but through a 50 Ω
load that path is inverting and therefore stabilising; source degeneration needs a `Cgs` the model
also lacks. What does oscillate is the classical MEISSNER arrangement — a tuned tank at the drain, a
feedback winding at the gate, mutual inductance between them, each coil doubling as its own DC feed —
whose start-up threshold is one parameter and sits between `k = 0.6` and `0.7`.

**Kurokawa's start-up signature is absent at both probed nodes of that oscillator**, while the NDF
counts two right-half-plane poles and the reduced NDF over the same probes agrees with it. That is
§4.4 (p. 50) the other way round: a node-local test is not a fundamental circuit quantity and is "not
a rigorous stability measurement". A Meissner oscillator's negative resistance lives in the
transformer loop, not at either terminal. It is printed by the gate, not asserted away — it is
exactly the situation WSP-6 exists for.

### Two traps in the fixtures themselves

**A variable that declares a unit makes the site unit be skipped** (var-unit-wins), so
`Mk = 9 nH` with `M=Mk nH` is 9 nH — and an override written as `9e-9` with no unit is read AS nH and
lands nine orders of magnitude out. The oscillator fixture states `Mk` in henries with no unit for
that reason. This is the same trap the sweep-scale note records.

**A WSProbe hung off a large resistor reports that resistor.** The probe must sit IN a node,
splitting it, with real circuit on both sides; only then is `1/Y0 = ZG + ZL` the loop impedance the
start-up condition is about.

## WSP-8 — the WSProbe's harmonic-balance sweep with no sparse work per operating point (2026-09-08)

brief-wsprobe-8. WSP-5's straightforward path factors the linear partition `2·K_ss + 2` times per
probe frequency **per operating point**, and every one of those factorisations is of a matrix that
does not depend on the drive. The sparse work moves into a cache keyed by frequency
(`HbSmallSignalCache`), the per-operating-point work becomes dense-only, and the tickle grid
parallelises. **No answer changes**: the shipped path agrees with WSP-5's, which is kept as
`HbSmallSignal.SolveProbesStraightforward` and is the oracle, to **1.3e-15 relative** — three orders
inside the brief's own 1e-12 gate — and WSP-5's independent two-tone oracle still passes against it.

### What the cache holds, and the one trap in getting it

Per frequency: `Y_NN` (`N_int²`), and at a probe frequency also `W` (`2N × 2N`, the reading of each
injection with the interface open), `T` (`2N × N_int`, the reading per unit interface current) and
`I_src` (`N_int × 2N`). One factorisation and `N_int + 2N` **transposed** solves fill all of it,
replacing `4N` forward solves plus `N_int` Z-column solves per operating point.

**`SparseLU.SolveTranspose` in CSparse solves `Mᴴ y = b`, not `Mᵀ y = b`.** Measured directly on a
3×3 non-symmetric complex matrix, because a real test matrix cannot tell the two apart — and neither
can a real-vector right-hand side, which is what every obvious probe uses. The row of `M⁻¹` this
formulation wants is therefore `conj(y)`. Omitting the conjugation is a sign error on the imaginary
part alone: every magnitude stays exactly right, so a Smith-chart plot of `wsp` looks entirely
plausible and only the phase is mirrored.

### Two premises of the brief that were false

**"`ParametricSweepEngine` must hand the same extractor to every operating point (it does for `hb`
today; assert)" — it does not.** The sweep re-elaborates the netlist and constructs a fresh
`HbEngine`, and therefore a fresh `HbLinearExtractor`, at every point (`ParametricSweepEngine.Run`'s
`using var netlist = …Elaborate(tb)` is a correctness requirement — an external device's instances
live in a worker process and are only released by disposal). So an extractor-owned cache would be
thrown away exactly when it was about to pay for itself. **The cache lives on the sweep instead** and
is threaded down as an optional `HbEngine` constructor argument; it holds no netlist, no component
and no factorisation, so the re-elaboration between points costs it nothing, and the entries are
valid for any netlist that stamps the same matrix.

**Gate (c)'s "`HbLinearExtractor.Factorizations` equals the number of distinct sideband
frequencies" cannot hold as written**, for two independent reasons. That counter also counts the HB
Newton solve's own per-harmonic factorisations, which the small-signal sweep has nothing to do with;
and the brief's arithmetic (`M + 2·K_ss·m`, 3,791 for its own example) counts **signed** sidebands,
while `Y(−ω) = conj(Y(ω))` is a theorem and one entry serves `+ω` and `−ω` both. The census
therefore reports three numbers — visits, signed distinct, folded distinct — and the folded one is
the factorisation count. On the measured fixtures the fold is worth 1.5-1.6× on its own.

### The certificate is not free, and it is what caps the speed-up on a small circuit

R-wsp8-4's rule is that reuse is *verified*, never declared: each entry carries the VALUES of the
matrix it was computed from and the next use re-stamps and compares them bit for bit, exactly as
`HbLinearExtractor` validates its own factorisations. That costs one stamp and one `O(nnz)`
comparison per frequency per operating point — no factorisation and no solve, but not nothing. On a
40-nonzero fixture a stamp costs about what a factorisation costs, so removing 195,324
factorisations while adding 211,601 stamps nets very little; the win is real only because the sparse
SOLVES go too. **A factorisation is superlinear in `n` and a stamp is linear, so the ratio improves
with the size of the linear partition** — which is the regime the brief is about, and is why the
300-section-ladder row below is the honest headline rather than the 16-device one.

Two things cut the stamp count without weakening the guarantee:

- **A validation epoch per (worker, operating point).** Nothing mutates the netlist during a
  small-signal sweep — the Newton solve is finished before the first probe frequency is touched — so
  a second visit to a frequency *inside one sweep* cannot find a different matrix than the first
  visit did. An entry validated in the current epoch is trusted without a stamp. The epoch is opened
  by `HbSmallSignal.SolveProbeRange` itself, which is the one function that runs a sweep on a slice,
  so there is nothing for a caller to remember. Measured: 211,601 → 58,999 stamps on the 401-point
  fixture, 3.6× → 5.7× overall.
- **The probe frequencies are visited before any sideband-only entry** (`HbSmallSignal.Precompute`).
  A probe frequency is also a sideband of other probe frequencies precisely when the grid step
  divides `f0` — the alignment R-wsp8-5 exists to reward — and a sideband-only entry holds `Y_NN`
  and nothing else, so reaching one later as a probe frequency means factoring it a second time for
  `W` and `T`. The LU is deliberately not kept; that is the memory the whole design refuses to spend.

### The frequency a shared sideband is computed at must be a function of its VALUE

The first version keyed entries by a rounded frequency but computed each entry at whichever spelling
of `ω_ss + k·ω0` arrived first. Two consequences, one fatal and one merely expensive:

- **Expensive:** the certificate then compared a matrix stamped at spelling A against one stamped at
  spelling B, which differ in their last bits, so *every* shared sideband missed. The cache reported
  1,439 entries and 90,596 factorisations at the same time — the tell that a cache is storing and
  never reading.
- **Fatal to R-wsp8-9(g):** which spelling arrives first depends on the ORDER of visits, and chunking
  the grid across workers changes that order. `wsp` then agreed with the serial run at one degree and
  differed by ~2e-15 relative at another. It reproduced under full-suite load and passed in
  isolation, which is exactly the shape of a bug people call flaky and it was not.

`HbSmallSignalCache.Canonical` rounds the frequency to the key's own precision before it is used, so
the entry is a function of the value alone and every worker at every degree computes the same one.
**1e-14 relative is the floor, not a preference**: the rounding is done on the mantissa and the
rounded integer must stay below `2^53`. Tightening it from 1e-13 to 1e-14 costs 5.8% more
factorisations (1,439 → 1,522 on the 401-point fixture) and buys determinism; the frequency
perturbation it introduces is what moved the agreement with the straightforward path from 4.7e-16 to
1.3e-15.

### Measured (Release, scratch harness, M4-class laptop, `MaxParallelism = 1` unless stated)

Fixtures are generated cascades of the WSP-5 two-port SDD, one `WSProbe` on each side of each
device, so `N_int` = probes = 2 × stages. **The brief's own table names a "16-FET amplifier (fixture
from WSP-7)" — no such fixture exists in the repository**, so the 16-stage row below is a generated
stand-in and the sizes are smaller than the brief's (they were chosen so the whole table runs in
under a minute; the per-point costs extrapolate).

| Circuit | `N_int` | `K_ss` | probes | `M` | drive points | straightforward (s) | this brief (s) | speed-up | sparse factorisations |
|---|---|---|---|---|---|---|---|---|---|
| 1 device | 2 | 5 | 2 | 401 | 41 | 1.14 | 0.20 | **5.6×** | 195,324 → 1,522 |
| 4 stages | 8 | 7 | 8 | 201 | 11 | 1.62 | 0.98 | 1.7× | 35,376 → 992 |
| 16 stages | 32 | 7 | 32 | 51 | 3 | 2.56 | 2.15 | 1.2× | 2,448 → 472 |
| 1 device + 300-section linear ladder | 2 | 5 | 2 | 201 | 11 | 8.11 | 0.45 | **18.0×** | 26,532 → 769 |

**Where the remaining time goes, which is the number the next person needs.** Per operating point,
after the first: the 16-stage row spends 0.69 s of its 0.72 s in the DENSE work — 51 factorisations
of a 480 × 480 `J_ss` plus 64 back-substitutions each — and 1,416/3 = 472 stamps. That is the honest
floor for this formulation, and the brief says so: `P·M` dense LUs of size `N_int(2K_ss+1)`, genuinely
per operating point because the device spectra are. The 1-device and ladder rows are the opposite
regime, where the dense matrix is 22 × 22 and everything was sparse.

**Parallelism** (16 stages, `K_ss = 7`, `M = 145`, whole `HbEngine.Run` including the HB solve):

| `MaxParallelism` | 1 | 2 | 4 | 8 |
|---|---|---|---|---|
| wall clock | 2.13 s | 1.15 s | 0.70 s | 0.70 s |

Degree caps at 4 because `MinSsPointsPerWorker` is 32 and the grid is 145 points; 8 and 4 are the
same run. `ProcessorCount` was 10.

Three things pin the sweep to the serial path regardless of `MaxParallelism`, and each is a real
constraint rather than caution: no `Library` was supplied to `HbEngine` (there is then nothing to
elaborate a per-worker copy FROM, and the copy is the whole thread-safety story — a model writes
state during `Stamp`); a netlist that may not be elaborated twice (`SParameterEngine`'s own
`CanElaborateInParallel`, shared rather than copied); and a **lattice** sideband family, whose
difference-index memo and scratch buffer are written on first use.

### `SSMaxHarm` — measured, and it is not a cheap knob

`1/H0` at the `DRAIN` probe over 0.41-3.89 GHz, one device driven into compression, against
`K_ss = 7`:

| `K_ss` | 0 | 1 | 2 | 3 | 5 |
|---|---|---|---|---|---|
| median error | 205 % | 53 % | 59 % | 36 % | **7.1 %** |
| worst error | 235 % | 259 % | 95 % | 62 % | **26.9 %** |

The convergence is not monotone (`K_ss = 1` is worse than `K_ss = 2` at the worst point) because
truncating the sideband set is not a series expansion of the answer — it removes mixing paths, and
removing an odd number of them can move the result further than removing an even number. **At a
hard-driven operating point even `K_ss = K − 2` is a 7 % answer**, which is why the default stays at
`K` and why the brief's suggested "measure it, do not guess" was worth doing: the guess would have
been "a couple of harmonics down is free".

### Budget

`AnalysisSettings.WspCacheBudgetMB`, default 512. The projection is made once, from the first stamp's
`nnz`, so every operating point behaves identically; over budget the probe blocks are dropped first
and the sideband `Y_NN` entries second, and either fallback is stated once with the sizes
(`wsprobe.hb-cache-over-budget`). **The brief's §4 arithmetic omits the certificates, and on a large
design they are the larger half** — `16·nnz` bytes per entry against `16·N_int²` for the `Y_NN` it
guards. They are counted in the projection for that reason.

## An edge port resolved to the wrong edge, and drove past the end of the one it named (2026-09-09)

Owner report on a connector-cutout board: a port placed on the wall of a NOTCH in the top-copper
polygon. Two independent defects in `PlanarPorts`, both invisible on a uniform feed and both exposed
by that one shape.

**1. The march inward read only the port's TRANSVERSE coordinate.** It started at the mesh's own
boundary on the named side and stopped at the first metal it met. That is correct while the port's
row crosses ONE run of metal — every uniform feed, and every fixture in `PlanarPortTests` — which is
why it stood so long. Measured on the reported board, both ports resolved to
`plane = 109.6192 mm, width = 1.800 mm`: **they drove the same edge**, 5.7 mm from where one of them
actually was. Nothing refused and nothing warned — a complete, plausible two-port answer for a
structure nobody drew. A row through a slot, a gap, or two separate conductors on one layer is the
same shape of error.

The port's LONGITUDINAL coordinate disambiguates it and costs one extra loop. The run CONTAINING it
wins; failing that the nearest, which is what preserves the documented allowance that a label "may
legitimately sit just off the end face it names" — a label beyond the metal still lands on the run it
is beyond, exactly as the old march did.

**2. The transverse run then walked past the end of the edge.** `HasBasis` asked only "is there a
rooftop straddling the plane here", and at a notch that stays true well past the wall, because above
the wall the conductor turns and keeps going. Driving those rooftops injects current into the MIDDLE
of unbroken metal — a delta gap, not an edge feed — so the structure solved was not the one drawn,
and the layout editor's marker and the excitation disagreed about the port's own width.

**An END is where the metal STOPS**, and the test is one lookup: the cell just OUTSIDE the face must
be empty. On a uniform feed it is empty across the whole width, so every pre-existing port resolves
cell for cell as before. It can never fail at the seed, because `outer` is by construction the
outermost metal column of the seed's own run.

It narrows the METAL walk by the same test deliberately, rather than letting the difference fall into
`UndrivenMetalM`. That field means "metal at the plane a conformal cell declined to pair", and it
carries that explanation in words; metal that never ended at this face is not undriven, it is not
this port's cross-section at all.

Sequence on the reporting board: `109.6192 mm / 1.800 mm` -> `103.9245 mm / 1.213 mm` ->
`103.9245 mm / 0.853 mm`, which is the wall. `src/Render/RESOLVED.md` carries the other half — the
layout editor was measuring the same port three different wrong ways at the same time, and the two
halves agreeing is the point.

Gate: `tests/Engine.Tests/Mom/PlanarPortOnANotchTests.cs`. `PlanarFillTests.Grid` gained an optional
metal mask so a fixture can have a row whose metal stops somewhere other than the mesh's own edge;
every caller before it passes null and gets the mesh it always did.

## The published sweep now says which of its points were SOLVED (2026-09-11)

`PlanarSolveResult` has carried `SolvedPointCount` and `SolvedFrequencies` since L9e, and the run
report has said "N solved by the full-wave kernel and the rest modelled from those" since then. None
of that reached the DataSet, and the DataSet is the only thing that leaves the process — so anything
reading `results/<key>.npy` saw one frequency axis with two kinds of number on it and no way to tell
them apart. An owner who stopped a run after six points got a hundred published points and a plot
that marked all hundred (see `src/Render/RESOLVED.md` for the half that lives there).

`PlanarKernel.BuildDataSet` now emits `planar.PointSolved` through
`RfCore.Data.SampleProvenance` — rank 1, on the sweep's own frequency axis, 1 = the full-wave kernel
produced this matrix, 0 = it came out of the interpolant. Unconditional, so a non-adaptive run says
"all of them" rather than saying nothing.

**A point the resonance search ADDED reads 1 here and 1 in `PointAddedBySearch` beside it**: it was
solved, and it was not asked for. The two cubes answer different questions and neither implies the
other, which is why the solved list handed to the mask is `SolvedFrequencies` ∪ `AddedFrequencies`.

**Matched by VALUE, not by index.** The published list and the solved list are two orderings of
frequencies out of the same array, and the index correspondence between them stops being true the
moment the search splices a found point in.

Gate: `tests/Engine.Tests/Mom/SolvedPointCubeTests.cs` — routine tier (~3 s on the coarse FR-4 line):
the flags match `SolvedFrequencies` point for point on a sweep that exercises both kinds, a plain
sweep is all ones, and the cube sits on the sweep's own axis.

## The DC point was published as MODELLED, and it had been solved all along (2026-09-16)

Owner report: with adaptive frequency sampling on, a sweep that asked for DC did not appear to
simulate it — and the same question was asked about the top of the sweep.

**Nothing was being skipped. The mask was wrong about it.** LF1 takes 0 Hz off the front of the grid
before any full-wave machinery reads it and answers it with `PlanarDcSolve` — a conduction network,
exact for the meshed structure — and LF2 does the same for the points below the fit's floor. Both are
spliced back on at the end. But `solvedList`, which `PlanarKernel.BuildDataSet` turns into
`planar.PointSolved`, is the ADAPTIVE path's set of solved GRID POSITIONS, and it is indexed into the
grid those points had already been removed from. So `PointSolved` read 0 at 0 Hz: *"this matrix came
out of the interpolant through the points that were solved"* — of the one row in the file that
nothing is interpolated into, and the row a user takes an HB or s-parameter run's bias path from. A
reader who checked the flag before trusting the file was told the exact opposite of the truth.

It is only the adaptive path. With sampling off `solvedList` is empty, which is the convention
`SampleProvenance.BuildSolvedCube` reads as "every published point was solved", so DC read 1 — which
is why this survived: **the flag was right in every configuration except the one it exists for.**

The fix is at the splice, not at the seeding: `solvedList` is also the interpolant's NODE list
(`nodeF`) until the sweep is published, so adding 0 Hz to it any earlier would make DC a node of a
spline over a grid it is not on. The LF1/LF2 frequencies go in where their points do, and only when
the list is non-empty.

**The sub-floor points go in for the same reason and it is the same reason.** They carry that
conduction solve's answer at the user's own frequency; no interpolant touches them either. What they
*are* is `ConductionSubstitutionNote`'s sentence — resistance only, no reactance — and the mask
answers a narrower question than that one: computed, or modelled from the ones that were.

**The top and bottom of the sweep were never at risk**, and that is structural rather than lucky:
`PlanarAdaptiveSweep.SeedIndices` is both endpoints plus interior indices, and refinement only
BISECTS between solved points, so the first and last frequencies of the full-wave grid are always
among the first solved. `AdaptiveSweepTests.T0_4` pins it on the seed function; the new test pins it
on what a run publishes.

Two reporting consequences, both fixed with it:

- the adaptive note counts over the FULL-WAVE grid, so a 17-point sweep starting at DC said "9 of 16
  were solved" and left the seventeenth to be read as modelled. It now names the LF1/LF2 points and
  says they were never candidates for modelling.
- `WorkspaceViewModel.EmRunSummary` said "N solved by the full-wave kernel"; the DC point is not the
  full-wave kernel's, so the phrase is now just "N solved". Its `>= requestedPoints` comparison also
  only becomes reachable for a sweep starting at DC once DC is in the count.

Gate: `SolvedPointCubeTests.ThePointsTheConductionSolveAnswered_AndBothEndsOfTheGrid_ArePublishedAsSolved`
(routine tier, ~2 s on the coarse FR-4 line) — 0 Hz, a 1 kHz sub-floor point and both ends of the
full-wave grid read 1, with interior points still modelled so the assertion is evidence of something.


## Every engine diagnostic raised inside a parametric sweep was discarded (2026-09-17)

Found while wiring the System-block passivity report to the Messages panel: it appeared on an
unswept `.cnl` and on nothing the System Design example ships, because every bench there is swept.

**`ParametricSweepEngine.Run` elaborates a fresh netlist per point and disposes it.** That is a
correctness requirement, not hygiene — the re-elaboration is how a swept variable reaches the
circuit at all, and the `using` is what returns an external provider's device instances (a 201 × 101
sweep otherwise asks a worker for 20,502 instances of the 4,096 it holds). But the netlist every
engine writes its warnings and notes to is that per-point one, and the caller is left holding the
netlist it elaborated in order to PICK the analysis — which nothing ever stamped.

Both CLI run verbs (`PrintWarnings(nl)`) and `SchematicRunService` (`DrainWarnings(nl)`) read that
one. So a swept run reported **no engine diagnostics at all**: not `hb-dc-nonconverge`, not
`sparam-regularization`, not `dc-worst-unsettled`, not a microstrip outside its validity range, not
a block that is not passive. An unswept run of the same circuit reported all of them, which is the
shape that keeps this invisible — the bench nobody sweeps behaves.

Fixed with an optional `diagnosticsInto` parameter and `MergeDiagnosticsFrom` per point, before the
netlist goes out of scope. `MergeDiagnosticsFrom` already dedups on the same keys `AddWarningOnce`
does, so a condition every point raises is reported once rather than once per point — which is why
the parameter takes a netlist rather than a list. A **nested** sweep passes the current point's own
netlist down, so a message raised three levels deep arrives once at the top.

Null keeps the old behaviour, which is what every test caller wants; the three production call sites
(both CLI run verbs, `SchematicRunService`) pass their own.

Gate: `tests/Engine.Tests/Parametric/SweepDiagnosticsReachTheCallerTests.cs`. Its fixture raises
`sparam-zero-bias`, which only the S-parameter engine can produce — a passivity report would not do,
because that one IS raised at elaboration and the caller would hold it either way, so the test would
pass against the bug.


## The HB linear extractor drained no model warnings (2026-09-17)

Found in the same pass. `IReportsWarnings`' own remarks name `HbLinearExtractor` as one of the three
call sites that must drain a model after stamping it, and it did not — `StampInto` stamped every
linear component and collected nothing. Neither did `SParameterEngine`'s NONLINEAR arm, which
`continue`s past the drain that follows the linear one.

So in a harmonic-balance run, a microstrip line outside its validity range reported nothing, and a
System block whose S is not passive queued a message that was never collected. Both are one line
each; the HB one also means every `IReportsWarnings` model is heard in HB for the first time.


## railRF brief 12 — the mask, the peaks, the coincidences and the ranking (2026-09-18)

`brief-railrf-12-impedance.md`. Four files under `src/Engine/Pdn/` — `PdnMask`,
`PdnAntiResonance`, `PdnCoincidence`, `PdnRemovalRanking` — on `PdnBreakdown`'s own terms: numeric
arrays in, rows out, no `Technology`, no `LayoutView`, no `RailDocument`, no matrix.

### 1. Q-9's 7.1 MHz is a first-order figure and the circuit does not agree with it

The design note's own sanity check (§8.2 Q-9) quotes the bulk-against-ceramic anti-resonance at
**7.1 MHz**, which is `1/(2π√(L_mount · C_ceramic))` over the bulk's 5 nH mounting loop and the
0402's 100 nF. That treats the ceramic as a pure capacitor. At an anti-resonance the two branches'
reactances CANCEL, so the ceramic's own inductance adds to the bulk's and the answer is
`1/(2π√((L_bulk + L_ceramic) · C_ceramic))` = **6.50 MHz, 8.7 % below**.

Q-9's other three numbers — 104 kHz, 5.3 MHz, 16 MHz — are exact and come straight out of the part
models. The fourth is the only one that needed a circuit, and it is the only one that moved. The
gate is against the circuit's closed form, not the note's: a test that reproduced 7.1 MHz would be
a test of an approximation railRF is not entitled to make.

### 2. An inductive branch has a NEGATIVE imaginary admittance, and that is the whole attribution

`Y = 1/(R + jX)`, so `Im Y` carries the opposite sign to the reactance. The attribution of
R-rail12-5 is nothing more than summing each bank's admittance at the peak and taking the largest
on each side of zero — but the sign is exactly the kind of thing that produces a table naming the
two contributors the wrong way round while looking entirely reasonable, so it is stated on
`PdnBranchAdmittance.Admittance` rather than left in the reader's head.

Two guards go with it. A branch whose susceptance is under `ContributorFloor` (1e-3 of the largest
single contribution) is on NEITHER side — that is what "a branch that is neither is not named"
means numerically, and without it a source's series resistance or a part far off resonance gets
named as a contributor because its rounding happened to fall one way. And a peak with no
contributor on one side reports **"not attributed"** rather than half a label: "L(C3–C9) against
nothing" reads as a finding about C3–C9 when it is a finding about the method's limits.

### 3. A mask is judged in LOG-LOG, is not judged outside its own band, and reports excursions

Three decisions, each of which has a plausible wrong alternative:

- **Interpolated in log f and log ohms.** A PDN mask is drawn and quoted on log-log axes, so
  "flat to 1 MHz then rising" is a straight line THERE. A linear interpolation between two points a
  decade apart sits up to 30 % off the ceiling the author believes they drew, in the direction that
  passes a design that does not meet it.
- **Outside the stated band nothing is judged, and that is not a pass.** `LimitAt` returns null and
  `UnjudgedPoints` counts it. A sweep running a decade past the mask's last point would otherwise
  report a clean pass over frequencies the target never spoke about. For the same reason a
  TRANSIENT target's mask stops at the knee `0.35/t_rise` rather than at the sweep's top.
- **One row per contiguous EXCURSION, at its worst point** — not one per sample. A single peak
  crossing the ceiling covers tens of grid points, and the row count would otherwise move with the
  sweep's point density, which is a setting rather than a property of the design.

### 4. The removal ranking takes a DELEGATE, and there is no sensitivity path at all

§2.4 states the reason and the arithmetic backs it: removing branch `p` gives `Z' = Z/(1 − Z·Y_p)`
exactly, while a first-order step from `∂Z/∂Y_p = −Z²` gives `Z' ≈ Z(1 + Z·Y_p)`. The two agree
only while `|Z·Y_p| ≪ 1`, and at an anti-resonance `ΣY` is the near-cancellation of two large
susceptances so `|Z·Y_p|` is of order one — the approximation has left its neighbourhood, not
merely lost precision. On the committed fixture the two differ by more than 3 dB.

So `PdnRemovalRanking.Rank` takes `Func<int, double>` and there is no fast option beside it,
because an option is a thing a future caller will reach for.

`DisplayResolutionDb` is **0.05**, set by the column's own one-decimal format rather than chosen:
§2.6 reads this table as "five parts show 0.0 dB", so anything that rounds to 0.0 dB IS 0.0 dB, and
a part flagged redundant while its row read 0.1 would be a table arguing with itself. A removal
that IMPROVES the margin (a peak moving away from where the mask is tightest) is clamped to zero
growth rather than reported negative — the column would then read as advice to delete the part,
which this table has not earned: it has judged one rail against one mask and knows nothing about
why the part is there.

### 5. The coincidence window's default is the capacitor tolerance, not a round number

`PdnCoincidence.DefaultFraction` is **0.10**. A part's resonance moves as `1/√C`, so the ±20 %
tolerance an ordinary ceramic is bought to moves f₀ by about ∓10 % on its own — before Q-12's bias
derating, before the mounting loop varies with placement, and before the converter's own switching
frequency moves with load and temperature. A peak drawn 8 % away from a line on THIS model lands on
it for some fraction of the parts actually fitted, which is the failure the check exists to catch;
a tighter window would report only the coincidences that had already happened.

Rows are ordered by **mask margin first and peak height only where there is no mask**: a near-miss
on a peak violating its target by 6 dB is a bigger problem than a direct hit on one that passes by
10, and ordering by separation puts them the other way round. A peak may legitimately appear on
more than one row — a peak on both a converter harmonic and a crystal fundamental is excited by
both, and collapsing the two would drop the name of one, which is the whole content of the row.


## railRF brief 15 — the cavity modes, and what the eigenproblem actually costs (2026-09-18)

`PdnModeSolver` and `PdnFieldMap` are §4.5's generalised eigenproblem and §2.4's impedance map. Four
things came out of building them that would cost someone real time to rediscover.

**The generalised problem reduces exactly, and it has to.** §4.5's system is `K v = ω²·C v` with `K`
the graph Laplacian weighted by `1/L` and `C` the diagonal of the cells' own capacitances. `C` is
diagonal and positive, so `A = C^(−1/2)·K·C^(−1/2)` turns it into a standard symmetric problem with no
approximation anywhere — and that is not tidiness. **LAPACK's generalised driver requires the first
matrix to be positive DEFINITE and `K` is only semi-definite**: its null space is one vector per
connected piece of the plane pair, which is the ω = 0 "mode" of a capacitor nothing is driving. A
generalised call would fail, or worse succeed on a matrix it had read only the upper triangle of —
`ModalDecomposition`'s own header records NumFlat's Hermitian-only behaviour for the complex case and
the same caution applies here. The zero modes are then skipped by COUNTING the connected pieces with a
union-find rather than by thresholding the eigenvalues, which is exact and says what it is doing.

**The cost, measured on the design note's own board (R-rail15-5).** The solve is dense and cubic. On a
uniform plane pair, single-threaded through NumFlat/OpenBLAS, Debug build:

| cells | wall clock | managed heap delta |
|---|---|---|
| 800 (32 × 25) | 0.66 s | 19 MB |
| 1,575 (45 × 35) | 4.38 s | 74 MB |
| **3,200 (64 × 50)** — §4.1's own 90 × 70 mm at 1.4 mm | **35.9 s** | **298 MB** |

Eight times the time for twice the cells, which is n³ exactly — and that ratio is also the evidence
that the LAPACK call dominates rather than the managed assembly loop, which is O(edges) and would have
scaled as 4×. **So the Debug/Release question does not arise here**: the work is native.

§3's argument ("the difference between a minute and a day") survives, and "cheap" does not: **36 s is
not the milliseconds one sweep point costs**, and that is why brief 15 puts the plane run on its own
button rather than in the Fast edit loop, and why `PdnModeOptions.MaxCells` is 4,000 — a little over
the note's own board, about 70 s at the top of it, and a refusal that names the CELL SIZE as the knob.
A shift-and-invert Lanczos would be far cheaper and was not built: its convergence knobs are wrong in
ways that look like physics, since a mode that has not converged is a mode at the wrong frequency and
nothing about the number says so. That trade is worth revisiting only with a gate that would catch it.

**The eigenvector's sign is arbitrary and the map is not.** An eigensolver may legitimately return
`v` on one run and `−v` on the next; drawn on a cold-to-hot ramp those are different pictures, and
brief 9's clipboard gate and brief 17's figures both require one result to render as one set of bytes.
`PdnMode.Field` therefore normalises explicitly — largest magnitude to exactly `+1`, lowest index on a
tie — rather than leaving it to LAPACK.

**A port's reading is the PEAK over its cells, never the mean.** §4.3 ties every cell under a load's
pads into one port. A mode with a null through the middle of a BGA's power field averages to nearly
nothing over it while being at full amplitude on both halves — the optimistic direction, and exactly
the answer §9 says nobody investigates.

**Acceptance, measured.** `tests/Engine.Tests/Pdn/PdnModeTests.cs` runs §7's rectangle: 60 × 42 mm on
εr 4.3, first six modes against `f_mn`. Worst error **3.32 % at Δ = 6 mm, 0.84 % at 3 mm, 0.37 % at
2 mm** — falling by four on each halving, which is the unit-cell ladder's own `(kΔ)²/24` dispersion and
nothing else. §7 asks for better than 2 % with monotone convergence; both halves hold, and the coarsest
rung is deliberately outside the tolerance so the ladder is measuring something.

---

## railRF review round 2 — two findings in the peak table's own text (2026-09-18)

Review of briefs 9-16. Both are in what a user READS, which is where §2.4 puts the value of this
whole table.

### 1. `FindPeaks`' doc comment described the wrong flank

The summary said *"standing at least `prominenceDb` above the LOWER of its two flanking minima"* and
the code measures against `Math.Max(Flank(-1), Flank(+1))` — the HIGHER one, which is topographic
prominence's own key col and is correct. The comment is the thing that was wrong, and it is the kind
of wrong that gets "fixed": measuring against the lower minimum would report a 0.2 dB ripple on the
shoulder of a real 20 dB peak as a 20 dB anti-resonance of its own, because the walk down its outer
flank runs to the floor of the band. That is exactly the shoulder `DefaultProminenceDb` exists to
suppress, so the comment now says which flank and why.

### 2. `PdnCoincidenceRow.Describe` spelled every harmonic past the third with a bare "th"

`21th`, `22th`, `31th`. §2.4 calls this sentence *the sentence the tool exists to produce* and a
converter's declared harmonic count runs into the twenties on these boards, so the suffix is computed
now (`second`, `third`, then `4th` … with the 11-13 exception).

### Reported, not changed: `PdnFieldMap` builds and factorises a matrix

The series overview's scope rule reads *"nothing under `src/Design/Layout/Pdn/` or `src/Engine/Pdn/`
builds a matrix, factorises anything, or owns a result type"*, and `PdnFieldMap.Impedance` builds an
`MnaSystem`, stamps the cavity into it and calls `Factorize`/`Solve`. Brief 3's comment-stripped scan
(`PdnMeshExtractorTests.NothingUnderPdnSolvesAnything`) gates only the `src/Design` half, so nothing
caught it.

It is defensible and is probably what should have happened: brief 14 §6 says in as many words *"no
new solver — CSparse's complex LU is what `SParameterEngine` already uses"*, `MnaSystem` is this
repo's one assembler rather than a second one, and an N-cell impedance field genuinely cannot be
read back out of an `ElaboratedNetlist` sweep. But the overview's sentence and the code disagree, and
one of the two should move — either the scan extends to `src/Engine/Pdn/` and this is refactored, or
the overview's rule is narrowed to the `src/Design` half it was really about. An owner call.

## `freq` in a measurement — the run's frequency axis, as a cube (2026-09-22)

Reported as a bug: `measure myCalc = 1/2/pi/freq` over an S-parameter analysis failed with
*"Unresolved name 'freq' in scope 'measurements'"*. It was accurate — nothing had ever injected it —
and `docs/design/measurements.md` recorded the consequence as a known limitation ("nothing yields the
`freq` vector — so a measurement that needs ω cannot be written"). It reads as a bug because `freq`
IS a reserved keyword the engine injects everywhere else a frequency is in play (`Z_Port`, `ChainModel`,
an SDD equation), so a user meeting it in one place reasonably expects it in the other.

`MeasurementEvaluator` now injects it into the globals scope alongside the swept variables, **as a 1-D
cube rather than a scalar, and for the same reason**: a measurement over a swept analysis is a curve,
and `DataCube`'s broadcast aligns operands by axis NAME and VALUES, so a `freq` carrying the analysis's
own axis composes with `SP1.S(2,1)` and lands one element per point. A scalar would have been the wrong
shape and — worse — would have silently collapsed a per-frequency quantity to one number.

**The grid is taken from the RESULTS, not from the analysis declaration.** Same argument as the swept
axis above it in that method: the declaration says what was asked for, the cubes say what was produced,
and a segmented or adaptively-refined sweep differs between the two.

**Two analyses on different frequency grids inject NOTHING, and the error says so.** Picking either one
produces a cube of entirely plausible numbers computed against the wrong frequencies, which nothing
downstream can detect. A DC-only run says the other half (no analysis produced a frequency axis) —
without those two sentences the caller sees only the bare unresolved name and concludes the feature does
not exist, which is exactly the report this fix came from.

Scoped to `freq` deliberately. `ssfreq`, `harmonic` and `mixIndex` are NOT injected: a spectral axis is
an index set, not a frequency the measurement can divide by, and `ssfreq` has no reserved-name
protection, so a user's global could already carry that spelling.

Gate: `tests/Engine.Tests/Measurements/FrequencyInMeasurementTests.cs` (2 tests — alignment against an
S cube, and the ambiguous-grid refusal). Both entry points are covered by the one change: the GUI's
`SchematicRunService` and `circuitrf sparam`/`hb` call the same evaluator.

### The same name on a trace card (`src/Render/DataDisplay/TraceExpression.cs`)

Reported the same day and the same root: a trace-card expression dividing by `freq` failed with
*"Unresolved name 'freq' in scope 'te'"*. Fixed alongside, because two spellings of one idea that
disagree about whether `freq` exists is worse than neither having it.

**It is a SCALAR here, not a cube, and that is not an inconsistency.** `TraceExpression` evaluates the
expression once per X-sample with each cube slice injected as that sample's own value; binding `freq`
to `xAxis.Values[i]` is the identical arithmetic the measurement's cube-valued `freq` performs
element-wise, so `mag(...)/(2*pi*freq)` is the same curve written the same way in both places.

**Bound only when the X axis IS a frequency** (`freq` or `ssfreq`). A trace whose X is `Pin` or a Γ
sweep has no single frequency the sample stands at, and binding the sweep coordinate there would be a
divide by a number in dBm that produces a perfectly plausible curve. `ssfreq` IS bound, unlike in the
measurement scope, because here it is the trace's X axis in Hz rather than a name competing with a
user's own global.

## brief-em3d-8 — the FDTD grid generator (`Em3d/FdtdGrid.cs`, 2026-09-25)

`FdtdGrid.Build(Em3dProblem, OpenEmsGridSettings)` writes openEMS's three line arrays and the reasons
behind them. Per axis: collect the required lines, merge the near-coincident ones (every merge reported),
then fill. Gate: `tests/Engine.Tests/Em3d/FdtdGridTests.cs`, 10 tests, ~0.1 s together.

**The fill is exact and provably graded, and the proof is what shaped it.** Each required line gets a
size s; an interval must start and end with cells within √r of its end sizes, so two cells meeting at a
line differ by at most r. For n cells the largest partition is `min(H, A·r^k, B·r^(n−k+1))` and the
smallest `max(A·r^−(k−1), B·r^−(n−k))` (A, B = the end sizes over √r); each is graded because it is a
min/max of graded sequences, so their log-linear blend is graded for every blend factor, and the factor
is bisected to hit the length. Three things that were not obvious:
- **Short intervals have gaps.** With equal end sizes s, one cell covers lengths [0.88, 1.14]·s, two
  cover [1.75, 2.28]·s, three [2.43, 3.76]·s — an interval of 1.4·s has NO partition. The sizes must
  be repaired. Lowering BOTH ends to a uniform fill (the first attempt) cascades between short
  intervals: case B's smallest cell came out 12.5 µm. Lowering only the LARGER end, down a ladder of
  r^(1/8) steps, and falling back to uniform only when that fails, gives 23.5 µm on the same geometry.
- **At the cap, the largest partition equals the length only to rounding**, and stepping to one more
  cell can make the interval infeasible (two cells of s/√r exceed a length of s). "Reaches" is taken to
  1e-12 relative.
- **The smallest profile underflows.** It decays geometrically into the middle of a long interval
  (1.3^−3000 is 0), which as plain numbers makes zero-width cells. It is computed in logs.

**The thirds rule's local cell is clamped by the metal and by SEPARATE metal only.** The brief says the
local cell is the target size at the edge; unclamped, a 100 µm strip under a 480 µm target puts its two
inside lines across each other. The cell is at most 3/5 of the metal's width (the three cells between
the two outside lines are then equal) and 3/7 of the gap to separate metal in the same layer (two facing
edges' outside lines then leave a middle cell equal to the rest). **Metal whose extent meets the edge's
own shape is not a neighbour** — counting a via pad fused to the line's end, or the antipad hole in the
plane below, clamped the whole line's cell to 9 µm because of one pad vertex 12 µm off the edge. Below
three MinCells the rule steps aside and the edge gets its own line, so a 10 nm drawing gap is MERGED
and reported rather than hidden inside a thirds pair.

**openEMS's own numbers, confirmed from F0's committed log** (`testdata/em3d/f0/B-via/openems/dw25-lossy/run.log`):
its cell count is the product of the three LINE counts ("83x75x32 --> 199200 FDTD cells"), which is
also the denominator of `Em3dSizeEstimate.OpenEmsBytesPerCell`; and its Gaussian pulse is 9/(π·f_c)
long (2.86481e-10 s at f_c = 10 GHz). `RingDownPulses` = 6 comes from F0 too: case A reached −50 dB at
6.8 pulse lengths, and case B's bounded run stopped at 7.0.

**Case B, generated-shape, default settings:** 69 × 85 × 50 = 293,250 cells, smallest 23.5 µm on y
(bounded by the via barrel's extremes and the port extents), Δt ≈ 6.4e-14 s. F0's hand grid was
83 × 75 × 32 = 199,200 at 25 µm with a 400 µm cap and ratio 1.4; the defaults here are a 392 µm cap
(λ/20 at 20 GHz in εr 3.66) and 1.3, which accounts for most of the difference.

**Deviations from the brief's letter, each deliberate:** the `.cem` field is `MinCellUm`, not
`MinCell`, because every `.cem` length is micrometres (`PaddingUm`). Gate 5 asserts the x- and z-lines
over the strip; a far pad's own y-edges run across the whole grid (a tensor grid has no other kind of
line), so y cannot be unchanged by construction. Gate 10 asserts a via feature (barrel, pad or
antipad) among the smallest cell's features, which the result reports for EVERY interval tying the
smallest size — the barrel and the port extents tie at 23.5 µm, and naming whichever rounding put
first would name half of the answer.

## brief-em3d-49 — face boundaries in the neutral problem (2026-09-26)

`Em3dFaceBoundary(Object, Face, Kind, Material?)`, `Em3dProblem.FaceBoundaries` (default empty), `Em3dFaceGeometry`
(a primitive's own face names and each named face's planar pieces), `Em3dFaceSheets.Apply`.

- **The face is the PRIMITIVE's name** (a box's world xmin…zmax, an extrusion's bottom/top/side<k>, a polyhedron's
  `Em3dFace.Name` — split pieces share it and all are taken). A cylinder's side is refused: no box query recovers a
  curved face and no flat sheet covers it.
- **Validate refuses**: a conductor's face, a sheet, Absorbing/PMC/Symmetry (both backends state those only on the
  outer boundary), a missing or curved face, a Conductive face with no conducting material, a face stated twice.
- **The FDTD side is one transform, applied inside both `FdtdGrid.Build` and `CsxcadWriter.Write`**: each piece becomes
  a sheet coincident with its face at priority max(order)+1 — PEC as σ = ∞ (which the writer already states as
  `Metal`), Conductive at three skin depths of its metal at the top frequency, because a CSXCAD conducting sheet takes
  a thickness and a face boundary is the outside of a thick conductor. Every caller of the grid (viewer overlay,
  `check`, `explain`) sees the same lines without knowing. No face boundary → the same instance, same bytes.

## brief-em3d-65 — `Em3dFidelity`, and a kernel solid's grid lines (2026-09-27)

`Em3dFidelity.For(problem, solver, grid, palaceSmallestSizeM)` → `Em3dFidelityFinding` rows; `FdtdGrid`'s kernel
lines (`KernelLines`). Gates: `tests/Engine.Tests/Em3d/Em3dFidelityTests.cs` (5 and 7).

- **The staircase cell is measured across the axes a face BENDS across, not all three.** The brief's rule (the
  largest of the three axes' cells inside the face's box) counted a fillet's LENGTH axis, whose cells decide
  nothing about it — on an ordinary grid, coarse along the edge, that is a "will not represent" warning on a fillet
  the grid resolves. The axes are those a triangle normal of the face has a component along (> 1e-3): a cylinder's
  facets contain its generators, so its axis drops out exactly; a sphere or torus keeps all three.
- **A chamfer's width is its smaller leg** — the face's box extent on the axes it slopes across — which is the
  chamfer distance a user typed for a symmetric one, not the face's own width (√2 larger).
- **One row per solid, per rule.** The worst feature (warnings before notes, then the smallest ratio) is named, and
  "It is the worst of N rounded features" says how many others there are; the ≥ 4-cell note is already a summary.
  The sentences carry the face in parentheses, which the brief's examples left out, so a row can be found.
- **`PalaceCurvatureElements` lives here** and `GmshGeoWriter.CurvatureElements` reads it: the mesh-size note and the
  script cannot state two numbers.
- **Kernel grid lines**: every planar face normal to the axis (a `MetalExtreme` for a conductor, as the brief says —
  not `MetalEdge`), the solid's extremes, and each other face's extremes (a bore's, a chamfer's). The thirds rule
  runs at a straight edge parallel to another axis only when the edge is CONVEX — the face lying on the line faces
  away from the metal its neighbour runs back over (read from the neighbour's box and the face's winding); a concave
  edge keeps only its face line, as `bordersMetal` does for an outline. The local cell is `MaxCellAt` and 3/5 of the
  neighbour face's extent across the edge; the gap clamp to separate metal is not applied to a kernel solid.

## brief-em3d-79 — pin currents of an S-parameter block in HB (2026-09-28)

**They were not in the `I` cube, and now are only on request.** HB's `I` rows are IProbes (back-solved) and the terminals of
NONLINEAR devices (`ComputeDevicePortCurrents`); an `SnP` is linear and never appeared. `AnalysisSettings.HbPinCurrents` (empty
by default) names instances whose pins a single-tone run appends as `<path>:<pin>` — read from the back-solver's solution at
`SnpModel.PortBranchIndices[p]`, the branch the block stamps per pin (current INTO the pin from the circuit), exactly as an
IProbe's branch is read. Nothing else asks, so no other result moved: of `Engine.Tests`' HarmonicBalance, Hero and
golden-file tests, 417 of 418 passed unmodified; the one failure was a load-dependent `CnlReader` race
(`HbDirectiveTests.Hero5_ParsesNumFreqs2_AndToneExprs` — a concurrent collection write while parsing), which passes alone. Multi-tone runs do not honour the list.

**HB phasors are PEAK.** `HbFft`'s frozen convention (X[k] = raw[k]/(N/2)) gives a cosine of amplitude A the phasor magnitude A;
`ThermalCircuitLinkTests.Gate1` pins it by inverting a pin's spectrum on a fine grid and reading the waveform's maximum. A
purely linear circuit (no nonlinear device, N = 0 interface nodes) runs and converges in HB, which is what that gate uses.

## A record that prints itself forever: `EmPoint` and `PlanarProblem` (2026-09-30)

The Ui.Tests host died of a **stack overflow** under a full run (`--blame-crash` gave the frames:
`EmPoint.PrintMembers → EmPoint.ToString → …`). A C# record's generated `ToString` prints every public
property, and `EmPoint.LeftNormal`/`RightNormal` are `EmPoint`s — so printing a point printed its normal,
which printed its normal, without end. Nothing needs to be wrong for it to fire: an assertion message, a
log line, or xUnit formatting a theory argument is enough, and it takes the whole process down, hiding
every result after it. `EmPoint` now has its own `PrintMembers` (X and Y only, the same format).

A scan of every record in `src/` for a public instance property of the record's own type found one more:
`PlanarProblem.PerfectGround`, which builds a new problem each time it is read. It is now the method
`WithPerfectGround()`, which a record does not print. **A computed property returning the record's own type
is this trap; make it a method.**

## A Term is its Z at DC (2026-10-05, owner decision; overturns Brief H's "inert in DC")

A wBond test bench drove a DC current source through the wires into a Term and nothing else. Both DC engines skipped
Port/Term outright, so the current had no return: the nodes ran off to ~1e10 V on gmin, most sweep points did not
converge, and the ones that "converged" carried a third of the current through gmin. The owner ruled that a Term must
load the circuit outside S-parameters. `PortModel.DcResistance` (since folded into `TerminationAdmittance`) — Re(Z), 50 Ω when unstated, null for Re(Z) ≤ 0 or a Term inside a sub-cell (which the
elaborator already says is ignored) — is stamped by `NonlinearDcEngine` and `LinearDcEngine` as a conductance to the
reference node; the 0 V drive branch is still never stamped there.

- **What it reaches:** every DC run with a top-level Term, and the DC pre-pass of an S-parameter run of a nonlinear
  circuit (the bias point). No hero and no shipped example has a Term on a DC path; two tests did, both with a Term
  DC-coupled to a biased node and relying on it being open (`BjtIntegrationTests.B4` — collector pulled to 0.14 V and
  saturated; `SddBranchEquationTests.T7` — bias divider pulled to ~0 V). Each now has the DC block a bench would have.
  `TermScopingTests`' Brief H gate now asserts the loading, and that a buried Term stays out.
- **HB too (same day, owner decision).** `HbLinearExtractor.StampInto` — the one place every HB, loadpull and harmonicaRF
  solve stamps its linear network — loads a top-level Term with 1/Z at every harmonic and mixing product and 1/Re(Z) at
  k = 0 (`PortModel.TerminationAdmittance`, which both DC engines read at ω = 0). So the HB k = 0 equation and its
  `NonlinearDcEngine` seed agree again, and an HB small-signal limit matches the S-parameter wave path's 1/Z0 port load.
  Every test class with a Term in an HB, loadpull or harmonicaRF run passed unchanged (24 classes across Engine,
  Harmonica, Core and Ui); no hero or shipped example has a Term in one. Gate:
  `TermScopingTests.HarmonicBalance_ATermIsItsZ_AtEveryHarmonic_AndRealAtDc` (Z = 50+25j: V₁ = I·Z, V₀ = Idc·Re Z).

- **Not on railRF's PDN mesh (2026-10-07).** The extractor puts a `Port` at every load as an OBSERVATION point —
  "it states no current and draws none"; the load current is its own `I_1Tone` beside it. Loading those with Re(Z)
  hung 50 Ω from every load pin to its reference: ~72 mA of phantom current on a 3.6 V rail, so every DC drop,
  source share and the Power Rail example's published figures moved (eleven Ui.Tests, none run at the time).
  `LinearDcEngine.Run` takes `portsTerminate` (default true, the owner's rule); `RailDcRun` passes false. Its only
  caller is railRF, but the default stays the circuit rule so a future circuit caller inherits the decision above.

## openEMS grid: a thirds pair broken by grading in a narrow gap (brief-em3d-117, 2026-10-06; fixed by brief-em3d-122)

Found building the 3D Wave Ports example's stripline pair; reported, not patched (brief 117 §8). Two strips facing across
a gap S each get a thirds pair (a line 2h/3 outside the edge, one h/3 inside, nothing between). The two outside lines
leave a middle cell of S − 4h/3. The 3/7-of-the-gap clamp above engages only when h > 3S/7; just below it the middle
cell is a hair WIDER than the largest cell, so `Fill` halves it, and the halves sit beside the thirds pairs' h-wide cells
at a ratio near 2, past `GradingRatio` 1.3. Grading then inserts a line INSIDE each thirds pair, a fraction of a cell
from the edge, and the edge singularity the rule exists to place is gridded as if there were no rule.

Measured on PTFE stripline, b 2 mm, W 1.6 mm, S 0.3 mm, 90 cells per wavelength at 18 GHz (h = 127.7 µm): middle cell
129.8 µm → lines at ±64.9 and 0, and grading lines at −122.3 and 121.8 µm between the pairs (−64.9 | −192.6, 64.9 | 192.6).
The odd mode, whose field is in the gap, ran slow (ε_eff 2.25–2.48 against 2.1; in air 1.11–1.15 against 1.00), so the
far-end coupling of a homogeneous line read −12.6 dB at 18 GHz against Cohn's −75 dB, the thru 15° late, max |ΔS| 0.26.
The same cell with S 0.4 mm (middle cell 229.7 µm, halves 114.9 µm, ratio 1.11, no insertion) lands within 0.0027 of
Cohn everywhere from 2 to 18 GHz. Nothing warns: the grid summary only names the 56.9 µm smallest cell.

What a fix has to decide: either let the clamp also cover the band where S − 4h/3 falls between h and about 2h/1.3 (so the
middle cell is one cell), or forbid grading from splitting a thirds pair and grade outward from it instead. Either moves
every grid with two edges facing across a narrow gap, so the openEMS goldens would be re-checked. Brief written: `docs/sonnet-briefs/brief-em3d-122-thirds-pair-grading.md`.

### brief-em3d-122 — measured, then repaired on violation (2026-10-06)

**The check (R-1.1).** `FdtdGrid.ThirdsViolations(FdtdAxisGrid)` pairs each edge's `ThirdsInside` and `ThirdsOutside`
sources by (feature, edge coordinate, metal side) from the MERGED required lines and returns the pairs with a grid line
strictly between. It skips a pair with another REQUIRED line between its two: that line is a feature's (see below), every
cell size keeps it there, and no refinement can clear it. What it finds is a line nothing asked for — one the fill made.

**The bands (R-1.2, R-1.4)** — 100 µm cap, MinCell 10 µm, ratio 1.3, 0.01 steps. The brief predicted only the facing gap,
at S/h ≈ (2.33, 2.87), (3.33, 3.64), (4.33, 4.41). Measured, the fill split pairs in four geometries, not one:

| geometry | split at | the interval beside the pair, in cells of h |
|---|---|---|
| two sheets facing across S | S/h 2.34–2.97, 3.34–3.65 | S − 4h/3: 1.01–1.64, 2.01–2.31 |
| one strip of width W (its own two edges) | W/h 1.67–2.31, 2.67–2.98 | W − 2h/3: 1.00–1.64, 2.00–2.31 |
| an edge D from a PEC box face | D/h 0.70–1.54, 1.67–2.31, 2.67–2.98 | D − 2h/3: 0.03–0.87, 1.00–1.64, 2.00–2.31 |
| an edge D from a dielectric's face | identical to the box face | |

So the first band runs to 1.64 cells, not the brief's 1/0.77 = 1.54 (`Plan` wants a cell within √r of its END size, not
r), there is no third band, and a single edge breaks too — against a box or material face for EVERY D below about 1.5
cells, and on a strip's own interior. The 3/5 width clamp makes a strip's interior exactly one cell at W = 5h/3; just
above it the interior halves, exactly as the 3/7 gap clamp's middle does.

**The repo (R-1.3)**, every grid traced while the named classes ran:

- `FdtdGridTests`: 67 builds; **35 had 142 split pairs**, nearly all in gate 2's fifty seeded layouts (random grading
  ratios 1.15–1.5), plus one pair in each of two microstrip fixtures (one of them gate 5's strip-and-pad).
- The openEMS Ui classes (`OpenEmsBackendTests`, `OpenEmsWavePortTests`, `OpenEmsCylindricalGridTests`,
  `Em3dWavePortsExampleTests`, `Em3dConnectorExampleTests`): 21 builds; **one** had split pairs — 
  `OpenEmsWavePortTests.Gate5_ACurrentBoxTouchingTheOtherStrip` (S 0.1 mm, 60 cells per wavelength), its two strips' own
  interiors. A refusal test; it still refuses. **Every golden and every shipped example grid (3D Connector, 3D Wave Ports
  Launch and Pair) had none**, so all are built by the unchanged first pass, byte for byte.

**A second kind, NOT repaired — for the owner.** Far more pairs have a REQUIRED line between their two lines than a fill
line: 15 of the 21 Ui grids have one, among them the 3D Connector's and the Board fixture's, and case B's every pair. The
cause is a port extent or a material face lying ON the metal edge — every lumped port drawn across a strip's full width
puts its fixed extent lines on the strip's two edges, and a board's dielectric ends where its copper does. The edge then
gets three lines, the middle one on the edge, which is the grid the thirds rule exists to avoid. Refinement cannot clear
it (the line stays on the edge at any cell); dropping the pair for an edge line, or moving a port extent off the edge,
are both design decisions, and neither is in this brief.

**The repair (R-2, D1-D3 as defaulted).** `Build` runs the check after `Fill`; a grid with no split pair returns from the
first pass, exactly as before. Otherwise `RepairThirds` lowers the split edges' local cells in a per-axis table that
`CollectRequired` reads, and the axis is built again, at most 64 passes, then throws (D2). For each split pair it picks
the side whose cell INSIDE the pair came out smaller (the interval that forced it), finds what bounds that interval —
another edge's thirds line (that edge is the anchor) or any other required line (the line is) — and sets
h′ = D/(k + f₁ + f₂): D from the edge to the anchor, k the interval's cells at today's h rounded up, f the thirds each end
takes (⅔ beside an outside line, ⅓ an inside one, 0 a plain line). The interval is then exactly k cells of h′; a facing
edge gets the same h′, and for two facing edges that is the brief's S/(k + 4/3). When the interval is already whole (the
split graded in from a smaller cell farther off), h is lowered by √ratio and the next pass makes it whole; an edge whose
cell falls below three MinCells loses its pair for an edge line, so the loop ends. The lowered cell and the feature
that set it go on both thirds sources (`LocalCellM`, `LocalCellSetBy`), so `explain`'s smallest-cell sentence names the
facing strip, the box face or the material face. No warning (D3).

**Results.** `FdtdGridTests` gate 11 (the three sweeps above, 0 split pairs, graded, max cell held) and gate 12 (brief
117's §0 section: h′ = 90 µm, lines −180, −90, 0, 90, 180 µm, set by the facing strip), and gate 2's fifty layouts all clean.
`OpenEmsWavePortTests.Gate10` (Benchmark, 60 s): the S 0.3 mm PTFE pair against Cohn (59.80 / 41.89 Ω), max |ΔS| 0.0083
over 2–18 GHz (was 0.26), far-end at 18 GHz −58.6 dB (was −12.6; ideal −75.5), every thru/near/far within tolerance.
`explain` on it: 209 × 283 × 17 = 1,005,499 cells, smallest 90 µm on y, set by the four gap thirds lines.
The examples' Benchmark gates were not re-run: their grids took the unchanged first pass (no split pair, traced above).

### brief-em3d-123 R-1 — a required line inside a thirds pair, measured (2026-10-06; no code changed)

**The kinds (R-1.1)**, traced over 122's R-1.3 set plus `CaseB()` (each distinct pair counted once). The brief's three
sources are all there, and three it did not list:

| kind inside the pair | pairs | where |
|---|---|---|
| a port extent ON the edge | 71 | every fixture with a lumped port across a strip's full width (microstrip, stripline gate 7, case B, the goldens) |
| a material face ON the edge | 51 | case B's laminate (x = ±5 mm, y = ±2.5 mm), the Board fixture's laminate at x = 5.08 mm, gate 2's layouts |
| a kernel solid's own face ON the edge | 10 | the 3D Connector housing, every pair on it |
| **another pair's line** (two pairs overlapping) | 89 | the housing's z-edges at −1500 and −774.5 µm (725 µm apart, h ≈ 833 µm), and gate 2's seeded layouts |
| **another solid's extreme or edge, off the edge** | 29 | case B's line pairs (h 240 µm at y = ±200 µm hold the barrel's extreme at ±150 and the pads' at ±300); the Launch's board line at y = ±787 µm holds the bore's extreme at ±670 |
| **the same conductor's curved extreme, off the edge** | 8 | the via golden's merged copper: the line pair at ±200 µm holds its own pad's extreme at ±299.757 |

Also off the edge: material faces (14) and a port extent (2). Of the 21 openEMS Ui grids, 15 have such a pair, as 122 found.

**Accuracy (R-1.2).** Three variants, each a scratch switch: V0 today; V1 drop the pair, keep the edge line; V2 keep the pair
and do not force a port extent, material face or kernel own face inside it. **V2 needs the WRITER to snap a lumped port
explicitly.** openEMS snaps a `LumpedElement` box and the probes to the nearest line (`Operator::SnapBox2Mesh`, nearest by
dual line), but **the excitation does not snap**: `Operator_Ext_Excitation` takes every E edge whose Yee position lies
geometrically inside the box. A port's own plane (zero width along the line) is inside the strip-end edge's x-pair, so
with no line there no edge is excited and the solve refuses ("the port matrix is singular"). Snapped to the nearest line,
which is always the pair's inside line (h/3 against 2h/3), the result is the same as openEMS's own snap where that works
(52.474 against 52.478 Ω). Z₀ comes from the two-line eigenvectors, (Z₊ − Z₋)/2, and ε_eff from the eigenvalue (10 and 30 mm).
**Z₀ is not identifiable from two lines alone** (an ideal transformer in the error boxes commutes with the line), so this
assumes each port's error box tends to identity at low frequency; the spread between the 10 and 30 mm lines' own
√(B/C) at 1 GHz, about 1 %, is taken as the extraction's uncertainty.

The default grid puts 2-3 cells through the stripline's 1 mm stack and ONE cell through the microstrip's substrate, and that
error dominates everything, so the z cell was also capped by a scratch knob (25 µm; 50 µm agrees to 0.1 Ω) with y and x at
the shipped 20 cells per wavelength. Errors against the closed form (Cohn t = 0 for the stripline, 51.18 Ω and εr; Hammerstad–Jensen
t = 0, 30.21 Ω, ±0.2 %, and Kirschning–Jansen ε_eff(f), ±0.6 %, for the microstrip). The strips are 1 µm sheets.

| case | variant | Z₀ error | ε_eff error | cells (10 mm) | smallest, Δt | run (30 mm) |
|---|---|---|---|---|---|---|
| (a) stripline, z resolved, 1-4 GHz | V0 | −6.0 to −7.3 % | 0.4-0.9 % | 26,650 | 25 µm z, 82 fs | 17 s |
| | V1 | −30.6 to −32.2 % | 0.4-0.9 % | 10,250 | 25 µm z, 83 fs | 8 s |
| | **V2** | **+1.3 to +2.5 %** | −0.1-1.0 % | 14,350 | 25 µm z, 83 fs | 17 s |
| (a) stripline, shipped grid | V0 | −18.6 to −19.9 % | 0.6-0.9 % | 1,950 | 141 µm y, 452 fs | 1 s |
| | V1 | −33.9 to −35.5 % | 0.4-0.8 % | 750 | 500 µm z, 1394 fs | 1 s |
| | **V2** | **−7.1 to −8.4 %** | 0.3-0.8 % | 1,050 | 480 µm y, 1144 fs | 1 s |
| (b) microstrip 600/254 µm εr 9.8, z resolved, 1-10 GHz | V0 | −4.3 to −6.2 % | +1.1 to +1.5 % | 216,600 | 21 µm z, 69 fs | 122 s |
| | V1 | −16.6 to −17.5 % | +4.2 to +5.1 % | 139,200 | 21 µm z, 71 fs | 111 s |
| | **V2** | **+1.3 to +2.5 %** | +0.8 to +1.7 % | 134,400 | 21 µm z, 71 fs | 86 s |
| (b) microstrip, shipped grid | V0 | −12.5 to −13.8 % | +3.3 to +3.7 % | 32,490 | 106 µm y, 264 fs | 17 s |
| | V1 | −20.2 to −21.1 % | +5.2 to +6.1 % | 20,880 | 228 µm z, 532 fs | 9 s |
| | **V2** | **−2.4 to −3.5 %** | +2.4 to +2.8 % | 20,160 | 228 µm z, 576 fs | 8 s |

So in (a) and (b) **V2 beats V0 by 3.5-11 points of Z₀**, far past both closed forms' accuracy and the extraction's ~1 %, with
fewer cells and a time step 2.2-2.5× longer. ε_eff barely discriminates (a TEM line's is εr on any grid). **V1 is the worst
everywhere**: an edge line with no pair leaves a 600-800 µm strip one or two cells wide. That contradicts D4's default
(V1 for a line that cannot move), so a fix should leave such a pair as it is (V0), not drop it. At 240 cells per wavelength the
stripline's three variants are one grid: the cell (210 µm) is below three MinCells (MinCell is a tenth of the 800 µm strip), so
the thirds rule steps aside on its own.

**(c) the 3D Connector's Launch, openEMS (fillet), against the recorded Palace runs.** |S11| at 2/6/10/14/18 GHz:

| run | 2 | 6 | 10 | 14 | 18 GHz | cells | time |
|---|---|---|---|---|---|---|---|
| Palace, Draft (recorded) | −30.06 | −19.45 | −16.71 | −19.02 | −40.75 | | |
| Palace, element order 2 (README) | | | −18.55 | | −31.95 | | |
| V0 | −33.55 | −25.06 | −18.23 | −16.74 | −23.53 | 510,291 | 102 s |
| V1 | −28.16 | −20.23 | −15.51 | −15.43 | −17.25 | 172,584 | 53 s |
| V2 (V1 where a line cannot move) | −28.13 | −20.23 | −15.97 | −15.81 | −17.23 | 211,680 | 59 s |
| V2, leaving a pair with an unmovable line as it is | −26.05 | −20.95 | −17.13 | −16.07 | −17.92 | 356,004 | 80 s |

|S21| at 10 GHz: V0 −0.118, V2 −0.167 dB, Palace −0.246 (Palace's includes conductor loss; openEMS's metals are PEC). Snapping
moves the answer toward Palace at 2-10 GHz and away from it at 14-18 GHz, and Palace's own |S11| moves 1.8 dB at 10 GHz and
8.8 dB at 18 GHz between element orders 1 and 2, so **(c) does not discriminate**. It does show that a fix moves this example's
recorded openEMS numbers by up to 6 dB (D5), and that the board line's pair in the Launch holds the bore's extreme, so D4's
V1 would grid the launch's own microstrip the worst way measured above.

Harness: a scratch xUnit class driving `EmRunService.Run` on `PalaceBackendTests.Stripline` and a microstrip of the same form,
with `CRF_V123` / `CRF_ZMAX123` switches in `FdtdGrid` and a nearest-line snap of the lumped port in `CsxcadWriter`; reverted.

### brief-em3d-123 R-2 — the fix: a port's width, a material face and a kernel solid's own face snap (2026-10-06)

Owner decisions after R-1: V2 for port extents, material faces and a kernel solid's own face on the edge; a line that cannot
move leaves its pair as it is (D4 reversed — V1 measured worst); the 3D Connector's openEMS numbers re-recorded (D5).

- **`FdtdGrid.SnapIntoThirdsPairs`**, between `CollectRequired` and `Merge` in the Cartesian `Build` only (the cylindrical
  grid's axial `CollectRequired` call is untouched): a raw line strictly inside a thirds pair whose every source is a port's
  extent ACROSS its width, a material face, or the pair's own conductor's `MetalExtreme` exactly on the edge (D3 — only a
  kernel solid makes one) is dropped. Another line inside the same pair (an air-box face, a sheet plane, a feed line,
  another solid's edge or extreme, another pair's line) stays, and so does the pair. A dropped port extent or material face
  comes back as an `FdtdSnap` on `FdtdGridResult.Snaps` with the line it lands on (the nearest; a snap that lands where
  another line already is is not reported), and both the run's notes and `explain` print it ("snapped:").
- **Only a port's WIDTH snaps** (`FdtdGrid.PortWidthAxis`: neither its sheet's normal nor its drive axis; null for a wave or
  coaxial port). R-1's V2 also snapped the port's own plane, which sits on the strip END's edge and inside that edge's
  x-pair; the two-length method cancels it, so R-1 could not see it, but the microstrip golden's port 1 moved from x = 0 to
  x = 261 µm — the reference plane a third of a cell into the line. The plane and the two ends it drives between stay lines.
- **The writer snaps the width explicitly** (`CsxcadWriter`, `FdtdGrid.SnapToLine`): openEMS snaps a `LumpedElement` box
  and its probes to the nearest line, but its excitation takes every E edge geometrically inside the box, so element and
  excitation would otherwise span different edges wherever the nearest line fell outside the drawn box. A coordinate already
  on a line (to the merge's coincidence tolerance) is written as drawn, so a port with nothing to snap is byte-identical.
- **A snapped edge the 122 repair would take below the rule's floor keeps its line** (`keepLines` in `Build`). With nothing
  inside the pair, 122's repair now works on every snapped edge, and on gate 1b's tee (a 600 µm strip with a tab edge 200 µm
  in from one side, MinCell 60 µm so the floor is 180 µm) it needs h′ = 150 µm to make the interior whole: the pair would be
  dropped for an edge line, which is V1. Such an edge is put back exactly as it was before features snapped (its feature's
  line, its unlowered pair) and is never snapped again in that build.

**Gates.** `FdtdGridTests.Gate1_…_PortsOnLinesOrSnappedToTheThirdsLine` (the rule restated: the strip's edges carry only
their pairs, each port extent across x lands on the inside line, ±(W/2 − h/3), and `SnapToLine` names the same line);
`Gate10` (case B: no snappable line inside a pair, no split pair, snaps of both kinds — its line pairs at ±200 µm keep the
via's barrel and pad extremes, which cannot move). `OpenEmsThirdsAccuracyTests` (Benchmark, 10 s and 59 s): the 800 µm
stripline in 1 mm of εr 2.2 and the 600/254 µm microstrip on εr 9.8 against Cohn and Hammerstad–Jensen/Kirschning–Jansen,
each dielectric drawn as 50 µm / 25.4 µm layers of one material with `MinCellUm` 10 so z is resolved by required lines and x
and y are the shipped grid. Measured: stripline Z₀ +1.2 to +2.5 %, ε_eff ≤ 0.9 %; microstrip Z₀ +1.4 to +3.4 %, ε_eff +1.1 to
+2.3 %. **Both fail with the snap disabled** (stripline −7.6 %, microstrip −6.2 %), so they hold the fix.
**Goldens** (`testdata/em3d/openems-goldens`): microstrip — each port's y extent ±550 → ±330 µm (the 1.1 mm strip's inside
lines, h = 660 µm), 137 → 128 x lines, 124 → 116 y lines, 392,220 → 386,640 steps; via — y ±200 → ±183.4 µm, 86 → 76 x lines,
101 → 75 y lines, **1,409,220 → 380,480 steps** (the edge's middle line set the time step).

**The examples (D5).** Their openEMS grids lost the line each lumped port's extent put on the board line's edge. That line,
with its pair either side, made a three-line cluster of 55-64 µm cells at y = ±787 µm, and those cells graded into the
bore and pin: **the resolution round the connector's pin was a side effect of the defect.** With the pair whole, the pin is
one 200 µm cell across on y, at 20 and at 30 cells per wavelength alike (the cells there are set by the pin's own lines
and the grading, not by the wavelength).
- **3D Connector** (setups unchanged, the shipped default grid on purpose): openEMS 510,291 → 360,360 cells (no fillet
  449,748 → 326,040; at 30 cells per wavelength 692,300 → 355,971), each run ~105 → ~80 s. |S11| with the fillet at
  2/6/10/14/18 GHz: −33.72/−25.05/−18.23/−16.74/−23.48 → −26.07/−20.67/−17.25/−16.29/−19.03 dB; |S21| at 10 GHz −0.118 →
  −0.133 dB. Palace's runs reproduced to 0.0005 dB. `check` gains a second warning, the bore staircased with about 3
  cells, and the fillet's cell is 200 µm (was 110.688). Palace (order 1 −40.75, order 2 −31.95 dB at 18 GHz) cannot say
  which openEMS answer is better (R-1 (c)); the README and the EM Solvers page say what moved and why.
- **3D Wave Ports, Launch on openEMS: the run was REFUSED** after the fix — its coax face's current probe needs one cell
  of clearance from the housing round the pin, and with the cluster gone it had 0.87 of one (0.46 at 25-40 cells per
  wavelength, where the grid near the pin is coarser still — more cells per wavelength does not help here). The setup now
  states `GradingRatio` 1.15 (1.2 falls short, 0.99 cells): 937,440 cells (was 660,192), 2 min 17 s, |S11| 10/18 GHz
  −19.36/−21.38 dB, |S21| 18 GHz −0.452 dB, the line measured 54.59 Ω ε_eff 3.08 (was 56.72 Ω, 3.17; the coax is
  50.02 Ω), no bore warning, fillet cell 143 µm. Re-run twice, reproducing to 0.002 dB. At 1.15 openEMS also takes the
  1.34 mm square port the README used to say it refuses. Pair and Coupled Microstrip: same cell counts and no snap, so
  their grids are unchanged and their recorded runs (and the 15-minute Palace one) were not re-run.

**Owner check (§5).** `explain` on the 3D Connector's Launch, setup openEMS — before: 59 × 93 × 93 = 510,291 cells, smallest
15.632 µm on z (the top copper's face and port 1's extent), Δt about 4.9e-14 s. After: 63 × 65 × 88 = 360,360 cells, the same
smallest cell and feature, Δt about 4.7e-14 s, and four "snapped:" lines — port 2's y extents at ±787.4 µm written on
the lines at ±758.05 µm (29.35 µm in; the board line's pair, local cell 88 µm after 122's repair) and the laminate's faces
at y = ±2.54 mm. The microstrip with a full-width lumped port: `FdtdGridTests.Gate1` and `OpenEmsThirdsAccuracyTests`
print it — on the 800 µm stripline, "'port/1' port extent at y = −400 µm lies inside the thirds pair of 'Strip/1' edge at
y = −400 µm, so it is not a grid line; the port is written on the line at y = −240 µm, 160 µm from where it is drawn."

**Not this brief's** (seen in the scoped run, in code it does not touch): `PowerRailExampleTests` (two: the README's
"Fast drop" 47.656 against 65.07 produced) and `ExampleWorkspacesTests.EveryDesignDocumentAnExampleShipsIsAViewOfACell`
('Thermal: Output Wires' ships a .csch outside a cell's schematic/ folder).

**openEMS is not repeatable to the 3D Connector's 0.05 dB tolerance**, before this brief or after it. Gate 6 run twice on
identical code read the fillet run's |S11| at 18 GHz 0.055 dB apart and |S22| at 2 GHz 0.046 dB apart (R-1's unchanged V0
run had already landed 0.047 dB from the old record); the two runs of each port stopped differently — one on the ports'
decay, one on openEMS's energy criterion — which moves the last few hundredths of a dB. The connector's openEMS rows are
therefore recorded as the MEAN of the two runs, each run within 0.028 dB of it. The tolerance was left as it was; if it
fails by a few hundredths again, this is why, and it says nothing about the grid.

## Bayesian optimization models the raw cost, not a log warp of it (brief-tuneopt-8, 2026-10-07)

A log warp, ln(cost − lowest + δ), is the usual advice for a least-squares cost that spans decades on
its way to zero. Measured on Branin, a 2-D Rosenbrock and an L-section match (40 evaluations, six seeds
each) at δ = 1e-3, 1e-2 and 1e-1 of the spread to the median, it was worse than the raw standardized
cost on all three — on Branin seed 5 it stalled 2.3 above the optimum. The warp turns the best point
into an outlier far below the rest, the GP fits a narrow well there, and expected improvement stops
exploring. `Bayesian` therefore has no `warp` option; the class remark records why.

**A related limit seen while gating Auto:** Levenberg–Marquardt on the L-section stalls at
|S11| = 1.2e-6 against a `le 1e-6` goal — its 1e-6-of-the-box difference step is too coarse for a
log-scaled 50:1 range at that depth. Not Auto's defect; the Auto gate uses 1e-5.

## SAR engine: cube-averaged SAR with summed-volume tables, and sar_calc as a cross-check (brief-sar-5, 2026-10-18)

- **A cube's mass is O(1) whatever its size.** The partial-cell fractions are separable (a cell's fraction is the
  product of three axis fractions, 1 except at each axis's two end cells), so the cube's sums are a weighted
  combination of at most 4 × 4 × 4 entries of one 3D prefix table of (ρV, V, background V, power):
  `−Ws·P[S] + (Ws−1)·P[S+1] + (1−We)·P[E] + We·P[E+1]` per axis. Searching for the cube of mass m is then a 1D root
  search on the half-width; a secant on the mass's CUBE ROOT is nearly exact in homogeneous tissue, about 4 evaluations
  per search. 10.05 M cells, 1 g and 10 g: 1.6 s and 1.25 GB on the 10-core developer machine (`docs/design/sar.md` §7.4).
- **"Largest containing valid average" is a painter's pass, not a per-cube loop.** Valid cubes in falling order of
  value claim unclaimed cells through a per-row skip list; only cubes holding a candidate (a prefix count) are visited.
  Looping over each valid cube's cells, as the straightforward description reads, is O(cells × cube volume).
- **Agreement with openEMS's sar_calc on the dipole's raw dump is exact in structure**: the same peak value and cell,
  the same valid/used/unused/air counts, every cell within 7.8e-7. So the validity rules in `sar.md` §7.2 are sar_calc's
  as it behaves, including two that are easy to miss: a cube must touch tissue on all SIX faces (not just stay under
  10 % background), and a face-grown cube that reaches the grid's edge is clipped and still accepted.
- **sar_calc's own choice points that circuitRF does NOT copy** (`sar.md` §7.3): its iterative size search can call a
  cube invalid at a smaller iterate (identical on convex tissue); a sub-cell cube's fraction is applied twice; SIMPLE
  accepts any iterate within 5 % of the mass. circuitRF finds the exact mass.
- **E is reduced to ½σ|E|² as each slab arrives** (`SarGridBuilder`), 4 bytes a cell instead of 28; background cells'
  dissipation is reported as `UncountedPowerW`, never silently dropped.
- **sar_calc is found only beside the openEMS discovery chose** (`SolverDiscovery.SarCalcBeside`), judged by its output
  file (its exit status is inverted), absent = a note, > 0.5 % peak difference = a warning (`OpenEmsSarCalc`). The
  `Sar.CrossCheck` setup key arrives with SAR-6's `Sar` group.
Gate: `tests/Ui.Tests/Sar/SarEngineTests.cs`. Design note: `docs/design/sar.md` §7.

## SAR averaging: every choice the procedure's description leaves open, and local SAR measured against sar_calc (brief-sar-13, 2026-10-18)

Recorded at the series' end so the choices sit beside the engine (`src/Engine/Em3d/Sar/`). `sar_calc` 0.37.0 is the
behaviour matched; its C++ was read to learn it, never copied, and IEC/IEEE 62704-1 was not consulted (it is purchased).

| Point | `sar_calc` | circuitRF | Effect |
|---|---|---|---|
| the cube's size | iterates to 1e-6 in mass | secant on the mass's cube root, bracketed, to 1e-9 | values agree to ≤ 1e-6 |
| a smaller iterate with an all-air face | invalid before the mass is reached | only the final cube is judged | identical on convex tissue (identical cube counts on the dipole); a non-convex body could differ at a few surface cells |
| a cube narrower than a cell | that cell's fraction applied twice | once (2h/w) | never reached at 1 g or 10 g |
| air and face tests | by volume with partial cells; < 10 %; face layer includes partial cells | the same | — |
| face-grown cubes at the grid's edge | clipped, accepted if they reach the mass | the same | the random phantom depends on it |
| the 5 % volume rule | volume with background, against the smallest | the same | — |
| `SIMPLE`'s mass | the first iterate within 5 % | the exact mass | `Simple` can differ by that window; the cross-check runs `SIMPLE` for it |
| a region | none | restricts every reported number; averaging always uses all tissue | head-only SAR is a region, not a code path |
| `IEEE_C95_3` | offered | not offered | — |

**Local SAR, measured directly at the series' end:** `sar_calc --method SIMPLE --mass 0` on circuitRF's own loop-coil raw dump
(SAR-8 run (a), 105 × 87 × 107 cells) against the local-SAR volume circuitRF wrote from the same dump: every one of 463,363
tissue cells within **3.3e-7** relative (float32), the same cells tissue on both sides, against overview §4's 1e-5. Until then
it was held only by the analytic block and implied by the identical 10 g averages. Not made a test: it needs both the local
dump (35 MB) and `sar_calc`, and the 10 g comparison already gates the same arithmetic.

## ANN training: bit-identical weights need a tanh of our own (brief-modelcomposer-6, 2026-10-19)

Design note: `docs/design/model-composer.md` §8. Findings worth keeping:

- **`Math.Tanh` and `Math.Exp` are not cross-platform deterministic.** .NET forwards them to the operating
  system's C library, and macOS, glibc and Windows round them differently in the last place. Thousands of
  L-BFGS iterations turn one ulp into different weights, so R-mc6-6's "same seed, same weights on every platform"
  would have been false while every single-machine test passed. `Modeling/Ann/DeterministicMath` builds `exp`
  (Cody–Waite reduction, degree-13 Taylor, `Math.ScaleB`) and `tanh` (a Taylor `expm1` below |z| = 0.5) from
  `+ − × ÷` alone, which IEEE fixes exactly. The runtime `AnnEvaluator` keeps `Math.Tanh`: only TRAINING has to
  reproduce bits. The same applies to initialisation — Glorot-UNIFORM, because `SplitMix64.NextGaussian` goes
  through `Math.Log` and `Math.Cos`.
- **Fixed-block summation makes the parallel mode free of its own reduction.** The loss and gradient are summed
  per block of 64 points and the blocks added in order whether they ran on one thread or several, so the parallel
  mode returns the serial weights; it is still documented only to 1e-12, as the brief asks.
- **A loss-term normaliser must be ONE constant per term.** Dividing each derivative direction by its own scale
  (`outputScale·inputScale_j`) looks like harmless conditioning but changes the relative weight of the directions,
  and for non-conservative targets that moves the least-squares compromise. The derivative term divides by the
  RMS of all its targets instead.

**Review (2026-10-20), three fixes:**
- **A rejected curvature pair corrupted L-BFGS's oldest pair once the memory was full.** The step pair was written
  straight into `S[head]`/`Y[head]` and only then tested; with `stored == m`, `head` is the OLDEST pair still in
  use, so a rejected pair left that slot holding new `s, y` with the old `ρ`. It is now built in its own buffers and
  swapped in only when kept.
- **A line search still descending after its last doubling gave up**, though every step it tried met the
  sufficient decrease; it now takes the longest one.
- **Unmeasured derivative components (weight 0) counted as data.** Their placeholder targets entered the
  derivative term's RMS normaliser and the output scale, and they counted toward the one-in-ten rule's targets,
  which could hide its warning. All three now count only measured components, as the reported error already did.

## Model Composer MC-8: a constant value target says nothing about the output's size (2026-10-19)

`AnnTrainer` scaled the output by the value targets' range, falling back to `max(|offset|, 1)` when they were all one
number. The drift charge (MC-8) trains on derivatives plus 21 value targets of exactly zero (`Qdrift = 0` along
`Id = 0`): the output scale became 1 C for a charge of ~1e-14 C, and the network would have had to hold its answer
in weights of ~1e-14. Values that are all one number now take the scale from the derivatives, as a derivative-only
problem always did, and the offset from that one value. Problems whose values vary are unchanged bit for bit.

## Model Composer MC-12: vector fitting without an eigensolver (2026-10-19)

- **The relocated poles are the zeros of `σ(z) = 1 + Σ r_k/(z − a_k)`, found by an Aberth iteration** on
  `p(z) = σ(z)·Π(z − a_k)`, not as the eigenvalues of `A − b·c̃ᵀ` (the repository has no general eigensolver; `Mom/Dcim.cs`
  says why). The obvious `p′/p = σ′/σ + Σ 1/(z − a_k)` cancels two terms of size `1/|z − a_i|` next to pole `a_i` — which
  is where every root of a converging fit sits — so near pole `a_i` the pair is combined analytically into
  `(1 + ρ + ρ′(z − a_i)) / ((z − a_i)(1 + ρ) + r_i)`. Poles of a known rational function come back to 1e-8.
- **The fast formulation is the default, not an option.** The full stacked relocation system grows as
  (responses × poles)²; triangularising each response's rows alone and stacking only σ's rows of R is the same solution.
  Measured on a non-rational 2-port that climbs to order 12 for both Y and Z: 8.9 s → 0.7 s. Iterations default to 15:
  exact data converges in a handful, and data that is not rational never meets the pole tolerance at all.
- **An order the samples do not determine crashed `FitByError`** (review, 2026-10-20). A relocation's least squares
  for one response has `2·nf` real rows and `2n + c` unknowns (`c` the constant and proportional terms), and an
  under-determined one read past the end of R — an `IndexOutOfRangeException`, which the order loop's
  `ArithmeticException` catch does not take. Noisy data never meets the 1e-3 tolerance and walks to order 12, so an
  11-point block ended its analysis there. `VectorFit.MaxOrder(nf)` caps the search and `Fit` refuses beyond it.

- **A sweep over a composed-FET switch could not stack** (brief-modelcomposer-21, 2026-10-20). `DrainLag=dl` swept
  over `1,0` changes the core's port count — a removed trap state mints no node — and HB's `I` cube carries one branch
  per device port, so the two points' branch axes were 6 and 4 long and `DataCube.PrependAxis` refused the stack. The V
  and INl node axes were unaffected (minted state nodes are `__`-prefixed and excluded). `DataSet.StackSweepAxis` now
  aligns cubes that differ only along LABELLED axes onto the union of their labels, NaN where a point has no such entry
  (`DataCube.AlignLabelledAxes`); cubes that already agree are passed through untouched, and a differing numeric axis or
  a repeated label is still refused, so every sweep that stacked before stacks bit-identically.
- **`BiasFreeze` is free unless a model asks for it.** It scans the netlist for an `IBiasFrozen` that needs a bias
  point before solving anything, so the extra DC solve it adds to each HB entry exists only for a design with a
  `LinearC=true` instance. Loadpull calls `RunSinglePoint` per termination and drive step, so such a design pays one DC
  solve per point there; nothing caches it, because a bias changed in place (harmonicaRF's `VdcOverride`) is exactly
  what must re-freeze and only a fresh DC solve can see it.

## Model Composer MC-25: harmonic balance with a trap node and a thermal node (2026-10-20)

The example's GanPowerSweep bench (8-finger SyntheticGan, 2 GHz, Pin −4 to 22 dBm) failed seven rungs (14, 16 to 21 dBm)
with no switch set, and took 13 s on each. `HbConvergenceTrace.IterRecord` now records, per iteration, the interface node
whose rows carry the largest share of ‖F‖² (`WorstNode`, `WorstShare`) and the node the accepted step moved furthest
(`StepNode`, `StepSize`). The single-tone non-convergence warning names that node.

**The record, one cold point at 18 dBm, the ampere norm.** Interface: `gi di si`, `t` (ΔT in K, its row in W, `Rth` 12.5
K/W, `Cth` 0.8 µF), `x0` (the drain-lag state `phi2` in V, on the trap model's 1 mF node).
- Entry: `x0` holds 100 % of ‖F‖ = 1.3e5. That is the cold seed, not physics: every harmonic of every interface node
  starts at 1e-3 + 1e-3j, and on the trap node that is `kω·1 mF × 1.4 mV` ≈ 1.8e4 A per harmonic.
- Iteration 1 accepts a full step (λ = 1), because it clears that one row: ‖F‖ 1.24e5 → 1.06e3. The same step takes the
  gate from 0.27 V to 4.3 V of swing (the answer has 3.3 V) and the gate row from 0.09 A to 206 A; `phi2` moves 7.6 V.
- From iteration 2 the thermal row holds 84 to 94 % of ‖F‖ for the 98 iterations left. The overdriven waveform
  dissipates ~1000 W against the 0.17 W of the answer. Newton asks for ΔT steps of 10² to 10³ K, the search accepts 1/256
  of them, and ΔT wanders between −7 and +41 K. The `phi2` row barely moves; the norm being judged is the thermal row's.

**The hypotheses.** (1) Row scaling: confirmed, and it is the cause. One row in units of its own carried the whole norm
twice, first the trap row (whose clearing let a ruinous circuit step through) and then the thermal row (in watts, where
an ordinary node error is ~0.1 A). (2) Stiffness: real but secondary. The capture exponential makes the trap row the
largest entry by orders of magnitude, but `phi2` and `t` did not need limiting once the norm weighed rows fairly.
(3) Warm start: contributing under the ampere norm, not the cause. A cold start failed too. In the sweep, rungs 12 and 13
failed their warm-started solve and converged only through the drive ramp, though cold they converge in 16 and 19
iterations. Under the scaled norm a warm-started rung takes 4 to 7 iterations against ~7 cold.

**The fix: the line search judges `‖W·F‖`, W = 1/|Y_self| per (node, harmonic)** (`HbNewton.LineSearchWeights`),
whenever a device's state node is on the interface (`HbNewton.HasStateRow`, from `ComponentModel.StatePorts`, which
`ComposedCoreModel` answers with its dispersion/trap nodes and its thermal node). The weight is the magnitude of the
row pair's own 2×2 block, not the diagonal entry alone: a state node's harmonic rows are almost all susceptance, the
trap node's 1 mF is ~1.3e7 S at 2 GHz, and all of it sits off the diagonal. W is held for the whole line search, so the
Newton step stays a descent direction and only the accepted λ changes. Convergence is still `‖F‖ < tol` in amperes.
All three Newton loops take it (single-tone, 2D, lattice N-tone).
- Cold, every rung −4 to 22 dBm converges in at most 24 iterations (Pin 18: 16 iterations, 0.06 s, from 100 iterations
  and 12.7 s). The sweep through `CircuitEvaluation` converges at all 27 rungs in 0.55 s, from 102.8 s with 7 failed.
  Two tones at 14, 16 and 18 dBm total: 22 to 26 iterations, where the ampere norm stopped at 100.
- **Measured and not chosen.** Per-row infinity-norm scaling converged the fitted model but broke `DrainLag=false`,
  which had converged. Diagonal-entry scaling converged everything but missed the capacitive part. Accepting a trial only
  when both the ampere and the scaled test pass gave the same paths as scaling alone. Seeding the state nodes' harmonics
  at zero (a state cannot follow RF) made the ampere norm fail at EVERY rung, from −4 dBm up: without the seed artefact,
  each circuit step that widens the drain swing grows the stiff capture row, and the search crawled at λ ≤ 1/128.
- **No converged answer moves.** A netlist with no state node never builds W, so its solve is unchanged bit for bit;
  every hero gate is such a netlist. With one, a point that converged under the ampere norm lands on the same spectrum.
  Driven to the round-off floor under both norms, they agree to ≤ 5e-12 relative to the spectrum's largest entry. At the
  default tol = 1e-6 the two paths stop at different points inside the tolerance ball, up to 3e-8 relative apart
  (`DrainLag=false` at 18 dBm, whose old answer stopped at ‖F‖ = 2.2e-7). The ball is wider than 1e-9 there: the
  thermal row's 1e-6 W of tolerance is 1.25e-5 K of ΔT. Gate: `TrapThermalHbConvergenceTests`, with
  `HbNewton.UnscaledLineSearch` (thread-affine, test-only) as the old norm.
- The floor itself is between 1e-9 and 1e-8 A on this bench: voltage round-off on the trap node's 1.3e7 S of harmonic
  susceptance. Tol 1e-8 is not reachable there.

**A sweep's convergence is visible** (R-mc25-4). `CircuitEvaluation` derives a parametric sweep's
`AnalysisConvergence` from the stacked `Converged` cube: false when any point failed, with `Detail` (and a run warning)
naming the points by their sweep values; true when all converged; null only when the inner analysis carries no such cube.
It was always null, and all three readers (`OptimizationRun`, the CLI's `Fields`, the example gate) take null as
converged. An optimizer evaluation over a sweep with a failed rung is now refused as `EvaluationFailed`, as a single
non-converged HB already was. The CLI run verbs were already right: they read the cube and exit 2.

**Where a sweep rung's time goes** (R-mc25-5, Release, the 18-rung −4 to 13 dBm sweep MC-22's gate ran). Elaboration
costs 0.2 ms per rung (85 ms for the first, the model load) and `BiasFreeze` 0.3 ms with a `Linear*` switch (0 without).
The rest is the HB solve. Before: rungs −4 to 11 took 11 to 22 warm iterations (23 to 63 ms), and rungs 12 and 13
failed warm and walked the drive ramp (35 to 42 steps, 1,350 to 1,820 iterations), which was 96 % of the 14 s. With a
`Linear*` switch the same iterations cost ~6× each, so the sweep was 130 s. After: 4 to 7 iterations per rung, 0.13 s
(0.58 s with a switch); MC-22's five-sweep gate at −4 to 22 dBm runs in 16 s in Debug, where it took 9 min to 13 dBm. Not
fixed: a `Linear*` instance's evaluation costs ~6 ms per iteration against ~1 ms. That is `BranchLinearisation`
integrating along the line from the bias point at every sample. Measurements were not in the timed path (the gate runs
without them).

## Loadpull: a first rung the tickle cannot reach (Model Composer review 4)

The loadpull engine's continuation re-walked a failed rung from the previous REAL rung only, so the first rung after the
tickle had nothing to continue from and the load stopped there. On the Model Composer example's GaN at 2 GHz, behind the
report's conjugately matched Class B source (16.6 + j179 Ω, Gt ≈ 19 dB), every load stopped on its first rung at −10 dBm
(‖F‖ stalled at 5.9e-3 after 100 iterations) while the same loads walked cleanly to compression from −30 dBm: the 40 dB
jump from the −50 dBm tickle is not one Newton solve on that stage. The failed first rung now continues from the tickle
through `DriveLadder.ContinueThroughJump`. Never for a vacuous-tolerance refusal (`SinglePointResult.ToleranceVacuous`):
smaller steps "converge" against the same meaningless tolerance and were reported as compression
(`VacuousToleranceAndCompressionCredibilityTests` caught it). Continuity is still never judged against the tickle.
Gate: `ComposedFetLoadpullTests.AFirstRungTheTickleCannotReach_IsContinuedFromIt_AndDrivesToCompression`.

## The DC engine dropped a model's refusal (brief-modelcomposer-26, 2026-10-21)

`TryBuildResidualAndJacobian` takes an `ExternalDeviceException` as an ordinary outcome of trying a bias and shortens the
step, which is right. Nothing kept the exception's text, though, so when the solve then failed, the report named only
residuals. The refusal itself was very often the reason: a compiled model's `$fatal` naming the quantity that was not
finite, attached by the worker channel as `Worker output:`. The engine now keeps the last refusal (`_lastRefusal`), and
the worst-unsettled report ends with `The last point a model refused: …`. A solve that converges is unaffected.

## …and said nothing when a model refused every point (brief-modelcomposer-27, 2026-10-21)

The report above is built from the residual at the last iterate. When a model refuses EVERY point — a compiled
Verilog-A module whose own `@(initial_step)` raises `$fatal`, as an exported composed FET does for a linear-charge switch
with no bias point — there is no residual to build it from, and `ReportWorstResiduals` returned on the empty vector. The
run ended unconverged with the model's sentence nowhere: on a self-heating model the only line left was a thermal-node
warning that the refused evaluations had themselves provoked. An empty residual with a refusal on record now reports
`DC did not converge: a model refused every point it was asked to evaluate. The last refusal: …`, through the same
deferral the retry already uses. Gate: `VerilogALinearChargeTests.AnUnsetBiasPoint_IsRefusedWithTheSentence`.
## Envelope chord Newton: a stalled CHORD step is not converged (2026-10-21)

`EnvelopeEngine`/`EnvelopeEngineMt` reuse one dense LU across steps (chord Newton) and rebuild it
when the residual contracts by less than 4x. The acceptance test also took a step whose residual had
merely stopped halving while still up to 1000x above `Tol` — and it ran BEFORE the rebuild check, so a
stale Jacobian's slow contraction was accepted instead of curing it. On a two-fundamental 2C run
(order 7, native step) that left about a dozen isolated single-sample glitches in 12,000 steps: up to
21 % of IM3's peak at one step, invisible in every integrated metric (≤ 0.004 dB) and obvious only on
a zoomed waveform against an independent reference. A stall is now accepted only after an update made
with a FRESH Jacobian (a genuine round-off floor), at ≤ 100x `Tol`. Tightening `Tol` 100x after the fix
changes nothing, which is how the remaining sub-percent difference on the weak products was shown not
to be Newton's.

## Envelope PSD: appending each product's span made a stitched spectrum (2026-10-21)

With two fundamentals, the PSD was each lattice product's envelope spectrum (fk ± fs/2) appended in
order. Whenever fs exceeds the product spacing, neighbouring spans overlap. At standard accuracy two
NR20 carriers 400 MHz apart run at fs = 491.52 MHz, so C1's span reaches 2.046 GHz and C2's starts at
1.954 GHz. The frequency axis therefore ran backwards at every seam: two skirts crossed, plus a closing
line across the overlap. The 1x validation runs (122.88 MHz spans) never overlapped, which is why it
went unseen. The spans are now power-summed onto ONE ascending grid over their union
(`EnvelopeDataSet.CompositeGrid`; the cross-spectrum between products is neglected, which only matters
in the far skirts). Single-fundamental runs are unchanged: one span, the same bins.

## Envelope spectra: resolution follows the carriers, not the envelope rate (2026-10-21)

The Welch segment was a fixed 8192 bins, so the bin width scaled with the envelope rate. A single
envelope spanning widely spaced carriers (`Envelope=single`) runs 8-16x faster than a carrier's own
envelope. Its bins grew to 0.5-2 MHz, and Hann leakage at a 20 MHz channel edge read ACPR1 up to
7 dB high. A finer step looked WORSE, which is what gave it away. The segment is now 8192 bins at the
carrier groups' own rate (`EnvelopeEngineMt.Plan` needRate). For one fundamental and for the lattice
that is the same 8192 as before, so no existing result moves.

## Envelope=single: a product lives in envelope k = a+b, not round(F/f0) (2026-10-21)

A single envelope over two carriers at f0 -/+ s reads each IMD product from a harmonic envelope.
`round(F/f0)` chose that envelope, but IM5 at f0 -/+ 5s is exactly 0.5 f0 and 1.5 f0 when s = f0/10.
Banker's rounding then sent IM5L to the baseband envelope and IM5U to the second harmonic's. The
figures read were wrong, and they moved with the step, which is what exposed them. The fast-time
transform freezes the envelopes over a carrier period, so a product a f1 + b f2 is generated in envelope
k = a + b. That is now the rule.

A centre-shift test then separated method from bug. The lattice IMD is identical to 0.01 dB with Fc
at 2.0 or 1.8 GHz. The single envelope moves 0.3 dB on IM3U and 1-4 dB on IM5 between the two
centres, and also between fine and 32x oversampling. Far products are not converged in a single
envelope, so the lattice stays the reference. The same pattern showed in the other simulator's
composite route.

The lattice PSD previously drew only the six labelled products. It now draws every product with
|k1+k2| <= 1, so the even-order baseband products (for example (-3,3) at 1.2 GHz) that the run
simulates are also on the plot.

## Envelope: two crashes found while adding the export test (2026-10-21)

- **Single-carrier runs fell into the two-fundamental branch of `EnvelopeDataSet.Build`.** An `else`
  attached to the wrong `if` when the single-envelope baseband span was added, so every ordinary
  one-carrier run (and a sweep of one) dereferenced the absent mixing lattice. The two-carrier runs that
  change was tested on never took that path, and there was no single-carrier envelope test.
- **A purely resistive network crashed the tangent check.** With C = 0 the combined matrix carries no
  `Values` array, and `EnvelopeEngine.RelDiff` iterated it.

Both are now covered by `EnvelopeExportCliTests.ASingleCarrierRun_ExportsTheCliLayout_LikeHb`:
a CW source into 50 ohm, run as a real CLI process, delivering 0 dBm.

## Envelope: follow-up fixes after merge review (2026-10-22)

- **A swept envelope's later segments lost the reference base.** `ParametricSweepEngine`'s envelope case
  elaborated segments 2..S with no `BaseDirectory`, so a relative SnP or `.cmodel` path resolved in segment 1
  and was "not found" in the rest. A `.cnl` hid it, because `CnlReader` anchors its `File=` paths when it reads
  the file; a schematic's stay relative. Gate: `SweptEnvelopeReferenceBaseTests`, which fails with the fix
  removed.
- **Segment netlists were never disposed.** `EnvelopeRunner.Run` now disposes every netlist it elaborated
  (the first stays the caller's). A compiled model's worker instances otherwise outlived the run, one per
  segment per sweep point.
- **AM-AM/AM-PM and DE_pct are emitted at every point.** They were added only when more than one power bin
  held data (and DE only when Pdc > 0), so a sweep whose points differed (a CW drive fills one bin) could not
  be stacked and the whole sweep was refused. The AM-AM grid is fixed, so the shape never changes; an empty
  bin, or a DE with no DC power, is NaN.

## Envelope: S-parameter blocks as a rational fit, and which element the tangent fails (2026-10-22)

brief-env-snp-rational. Code: `src/Engine/Envelope/EnvelopeSnpFit.cs` (fit, realization, run-scoped
substitution), `EnvelopeTangentCheck.cs` (per-element measure and warning). Gate:
`tests/Engine.Tests/Envelope/EnvelopeSnpTests.cs`.

**What the tangent cost.** MOD_Source (NR 20 MHz) → 2-port SnP → 50 Ω, against `y = IFFT(X·S21)/2` from the
source's own record. Fixture (a), a 5th-order 0.5 dB Chebyshev bandpass (ripple band ±11 MHz), at `fine`:
channel power **1.98 dB** low, ACPR1 off by up to 0.75 dB, ACPR2 by 1.9 dB, output NMSE **−0.6 dB**;
`__EnvTangentError` read 8e14. Fixture (b), the same with an ideal 1 ns line on port 2: 2.02 dB, NMSE −0.55 dB.
Fitted: 0.0011 dB and NMSE −66.9 dB (a), 0.0012 dB and −66.6 dB (b), at 16×.

**The gate runs at 16×, because 8× is not converged against an exact reference.** With the fit exact (order
10, error 8e-15) the residual at `fine` was 0.074 dB on ACPR1 and NMSE −54.8 dB on both fixtures, which is
Gear-2's own frequency warping at the channel edge (Δω·h ≈ 0.06 rad), not the model: 16× gives 0.003 dB and
−66.9 dB. The comparison reference is computed by overlap-save over a 4 µs lead (the filter's slowest pole
decays in 130 ns): the whole 0.5 ms periodic record at 2 GS/s cost 6 s per test for the same y.

**Fit defaults, measured on fixture (b) at 16×.** Tolerance 1e-2: order 14, 0.016 dB, NMSE −51 dB. 1e-3:
order 16 (error 1.9e-4), 0.0012 dB, NMSE −66.6 dB, σmax 1.0002. 1e-4: order 17, the same accuracy but
**σmax 1.53** (a non-passive bump below the file). 1e-5: order 18, passive. 1e-3 sits at the discretization
floor, so it is the default; max order 60. No setup key was needed. `Proportional = false` because S is bounded.
**Non-rational files often fit non-passively outside their range**: mismatched-line files at 1e-3 gave σmax
1.003–2.4, always between DC and the file's lower edge. That is reported (and warns above 1.001), not enforced.

**DC and beyond the file.** The run stamps the fit everywhere, the DC solve and HB start included, so the run
starts at its own equilibrium (gate 3 holds a CW start to 3.4e-6 V on 0.9 V). The interpolator and the fit do
part there and the note says by how much: fixture (a) 0.057 at DC, 0.053 at 2 GHz, 0.061 at 3 GHz (clamped
end values against the true filter); fixture (b) 0.66 at DC.

**The realization's scaling matters at k = 0.** The two-stamp C recovery at k = 0 differences stamps at
dlt = 2π rad/s and 2dlt. In complex-pole form a state row's entry `(jω − a)` would lose ~1e-7 of C to
cancellation (ω_p ~ 6e9 against dlt); the real 2×2 block keeps `jω` and `−σ` in different parts, so nothing
cancels. Rows are scaled by 1/|a_p| so every entry is O(1) at RF.

**The per-element tangent measure took three tries, each forced by a measurement.**
- *Raw stamp entries* (`max|A_edge − tangent| / max|A|`) read exactly 1 for the Chebyshev file whatever its
  bandwidth: its Z stamp is singular at f0 (S21 = 1 there, so det(I − S) = 0). A line's Y stamp is likewise
  singular wherever sin θ = 0 at the band edge (E = 1530° read 1593 % and moved ACPR1 by 0.1 dB).
- *The terminated response* (every node of the element to ground through 50 Ω, compare node impedances) is
  bounded, but at one band-edge point it saturates near 0.35–0.44 while ACPR keeps growing.
- *Absolute error, RMS across the band* orders the line scan but not the filter scan: a 22 MHz filter with a
  moderate absolute error moved ACPR1 4.2 dB, because its skirt is deep in the adjacent channel and ACPR reads
  the RELATIVE error there.
- Shipped: **relative** error of each terminated impedance (floored at −60 dB), RMS over 16 offsets out to the
  signal's own extent (±need/2: carriers plus their ACPR2 bands), worst over products. The baseband envelope is
  not checked: a line's Y stamp is clamped at DC.

**The threshold, 0.5.** Mismatched 25 Ω line, §1 topology, tangent vs its own fitted Touchstone at the same
step: E = 810/990/1170/1350/1530/1890/2250/2970° → error 0.20/0.30/0.41/0.50/0.56/0.64/0.67/0.71 → ΔACPR1
0.028/0.042/0.057/0.076/0.098/0.145/0.203/0.329 dB. The §1 Chebyshev under the tangent, ripple bandwidth
22/44/88/132/176/264/352 MHz → error 74.5/45.6/1.59/1.08/1.12/0.75/0.93 → ΔACPR1 4.24/1.52/0.24/0.08/0.04/0.01/0.41
dB (the 352 MHz filter moves channel power 0.97 dB through passband ripple). No single value separates the two
scans; every case at or above 0.1 dB is above 0.6, so 0.5 warns on all of them, with some false alarms.

**System Filter and Duplexer had the same defect, worse.** Their S(ω) is a prototype's rational response
stamped as `b = S(ω)·a`. Under the tangent a passive Chebyshev bandpass read **Gt = +7.1 dB** (its own
Touchstone gave −0.09 dB) and a duplexer **diverged to NaN**. They are now fitted from samples of their own
stamp, as a node-referenced S in 50 Ω (no Core accessor needed), and match S21 from `SParameterEngine` to
0.001 dB. Two settings were measured: a 1,903-point grid cost the 3-port duplexer 20 s (order 20); 161–243
points (a coarse anchor plus 41 per product band) give the same answer in ~2 s. Their tolerance is 1e-9, not
1e-3: the sampled S is exactly rational, and at 1e-3 the duplexer stopped at order 14 of 20 with σmax 1.0012.
Constant-S blocks (Atten, Amp with or without IP3, Switch, Circulator, Coupler, Balun) were already exact.

**Nothing else moved.** SHA-256 over every cube, before and after: a lumped envelope run with a diode, an HB
run with an SnP and an S-parameter sweep of one — all three identical. The substitution swaps
`ElaboratedNetlist.Components` entries for clones and puts the originals back when the run ends.

## Envelope: a line in the baseband product had no pivot (2026-10-22)

`MOD_Source → TLIN (Z = 25 Ω, E = 90/270/450°) → 50 Ω` failed in `EnvelopeEngine.Prepare` with CSparse's "No pivot
element found" (pre-existing; no SnP involved). The k = 0 product takes C from stamps at dlt = 2π rad/s and 2·dlt.
A line's Y stamp goes as 1/(jωL) there (L = τ·Z0), so the recovered slope is ~1/(dlt²·L) ≈ 2e6, and α·C ≈ 1.5e15 at
the standard step: the 0.02 S terminations sit below that matrix's round-off and the pivot vanished. E ≥ 810° survived
only because 9× the L put the round-off just under 0.04 S. Its DC equilibrium was wrong too: A0 = a1 − j·dlt·C is
not the line's DC stamp.

Fix (`EnvelopeBaseband.cs`): an element whose own 1 Hz / 2 Hz tangent is not exact (terminated relative error above
1e-6; an affine stamp's round-off, extrapolated from 1 Hz to 50 MHz, is estimated at ~5e-9, and the lumped fingerprint confirms none is held) is wrapped and HELD at its DC stamp for
the baseband product only — exact at DC, and the stamp the DC solve and HB start already use. Lumped elements, sources,
inductive branches and fitted SnPs are never held, so a lumped run is bit-identical (fingerprint unchanged). Every
length now runs and passes the line's closed-form gain (−1.93820 dB) to 1e-5 dB; a 1 V bias through a 90° line into
10 Ω draws 0.1 W to 2e-12. The cost is the line's frequency dependence across the baseband, measured by the same
terminated measure and warned above `EnvelopeTangentCheck.WarnAbove`: a 90° line at 1 GHz (4.5° at 50 MHz) is not
named; 810° reads 54 % and 2970° 147 %, and are. Gate: `tests/Engine.Tests/Envelope/EnvelopeBasebandTests.cs`.

## Envelope: a composed FET stopped the run at a signal peak; one failed point lost the sweep (2026-10-22)

The Model Composer example's GaN FET (traps and self-heating on) in an NR20 envelope bench ran to 12 dBm average and
stopped at 16 and 20 dBm (`fine`), and at 10 dBm on `standard`, with "envelope step N did not converge". A residual
trace of the failing step showed the same defect MC-25 found in HB: the envelope Newton took every update whole, and on
the composed core the trap-state row (`core_x0`) then the thermal row (`X1.t`) went to 1e13 … 1e28 and never came
back. Switching off traps, self-heating, dispersion or charge did not move the failing step, so it was the update, not
one part of the model.

Fix (`EnvelopeNewton.cs`, used by both engines): every update takes `HbNewton.Backtrack` on the scaled norm
(`LineSearchWeights` when a device has a state row). A full step that reduces the residual is accepted on the first
trial, so a step that converged before takes the same iterates (8 dBm: every printed figure unchanged). A rejected CHORD
step is not halved: its direction came from an older Jacobian, so the Jacobian is rebuilt at the iterate first. That
alone carried 16 and 20 dBm on `fine`; 20 dBm on `standard` still lost one step, so a step that fails is retaken as
2, 4, 8 or 16 backward-Euler sub-steps, the drive linear between its samples, landing on the step's own time so the
Gear-2 history at h stays valid. 20 dBm: `standard` with one split step and `fine` agree to 0.0003 dB in Pout.

`ParametricSweepEngine` let one failed envelope point throw away the whole sweep, with a message naming no sweep
value. An envelope point that fails is now NaN with a warning naming its value (`Pin = 16 dBm: …`) and the
finished points are kept; only a sweep in which no point completes throws, listing every point. The engines' notes
used to be dropped inside a sweep; they are now kept, prefixed with the point. The `env` CLI's sweep path passes
`diagnosticsInto` and prints them, and a segment's failure surfaces as its own message rather than
"One or more errors occurred". Gate: `tests/Engine.Tests/Envelope/EnvelopeConvergenceTests.cs`, where each of the
first two tests fails with its own fix switched off.

## Envelope sweep points ran one at a time (2026-10-22)

The Envelope example's README said a swept envelope's points run in parallel; they never did. `ParametricSweepEngine`
takes points one at a time for every inner analysis, and the only parallelism was inside a point (time segments, at
least 20 µs each). A record under 40 µs is one segment, so a 7-point 10 µs sweep of the composed FET ran on one of
ten cores (118 s).

Envelope points now run side by side (`RunEnvelopePoints`), up to one per core, each with at most
`cores / concurrent` segments when `Segments` is auto. The testbench holds ONE point's value at a time, so
`EnvelopeRunner.Run` is split into `Prepare` (every segment's netlist elaborated with the value set, on the calling
thread) and `Execute` (simulation only, touching no testbench). At most `concurrent` points are prepared and
unfinished; each point disposes its models when it ends, and warnings are merged afterwards in sweep order.
`CRF_ENV_SERIAL` makes it one point at a time again. The same 7-point sweep: 42 s, every figure unchanged. The
example's 5 × 200 µs sweep: 11.0 s → 7.9 s at 2 segments per point instead of 10, the table unchanged to the printed
digit. Gate: `tests/Engine.Tests/Parametric/SweptEnvelopeConcurrentPointsTests.cs` (each point bit for bit against
its own run alone, and a concurrency counter rather than a timing).

## Envelope segments ignored the circuit's memory (brief ENV-SEG, 2026-10-23)

`Segments=auto` warmed every segment up for a constant 5 µs; the schema said the warm-up came from the network's
slowest pole, which no code computed. On the composed GaN example (drain-lag emission 1 ms) the 10 stitched segments
restarted the trap state at every seam and reported a seam error of 125: RF figures within 0.06 dB of sequential, the
state trajectory wrong.

**What auto does now.** Only when both `Segments` and `Warmup` are auto and today's rule would split the run, Prepare
asks the circuit for its slowest time constant τ (`EnvelopeMemory`): the largest a model declares
(`ComponentModel.SlowestTimeConstant`; `ComposedCoreModel` reports each kept trap state's emission time, the
dispersion τ and each thermal pole's `R·C` with the instance's own `Rth`/`Cth`) or the linear network's slowest pole.
The warm-up becomes `max(5 µs, 5τ)` and the existing segment rule (none shorter than 4× the warm-up or 20 µs) is
applied to it. A run that cannot hold two segments runs as one and posts a note naming the part and τ. Both
quantities explicit, either one explicit, or a run that is one segment anyway: the plan is exactly today's and nothing
is computed. So every short-memory and every explicit run plans, steps and stitches as before; only the seam metric's
value differs (below).

**The pole estimate** is the largest |eigenvalue| of G⁻¹C of the baseband product: the same two stamps (1 Hz, 2 Hz,
lines held at DC) the engine uses for k = 0, power iteration on `(G + σC)⁻¹C` with σ = 1e-3/Stop so a gmin-only node or
an inductor across an ideal source still factors, |μ| from two successive gains so a dominant complex pair still gives
a magnitude. It runs on a netlist of its own (one extra elaboration, disposed), so the run's own netlists are never
stamped by it. **Cost: 0.84 ms on the composed-FET fixture** (0.2 ms elaboration besides), against ~33 s for a 20 µs
run of it (a 2 µs run measured 3.35 s), so it is kept. It also finds the fixture's thermal pole (25 °C/W × 0.4 µJ/°C
= 10 µs at node `X1.t`) independently of the core's declaration. Limits: devices are open in the linear partition, so a
node with a capacitor whose only DC path runs through a device reads as a gmin-limited (very slow) pole and the run
goes to one segment, which is slower but not wrong; and for a high-Q resonance 1/|s| understates the ring-down time
(Q/π cycles), which a 5 µs floor covers at RF but not for a resonance near the baseband band.

**Fixture plan (§3, 10 cores):** before, 10 segments of 20 µs with 5 µs warm-up; after, 1 segment, warm-up reported
5 ms, warning "Envelope ran as one segment: X1's drain-lag state remembers 1 ms. Set Segments in the Envelope analysis
setup to run in parallel.", seam error 0 by construction. Both messages are one line naming the setup field that
answers them; they reach the Messages pane as warnings through the runner's note callback (`AddWarningOnce`). With `DrainLag=false GateLag=false Cth=1e-8` (thermal 0.25 µs) the plan is today's.

**Seam metric.** Each probe's worst harmonic-1 mismatch on the overlap divided by that probe's RMS there (a probe with
almost no harmonic 1 by 1e-6 of the largest probe's RMS), worst over probes, still `__EnvSeamError`. The value at each
step was the normaliser before, so one sample near zero (or a trap/thermal probe) set it. The warning ignores probes
on a model's state ports (`StatePorts`, plus `__` nodes) and names the worst other probe in the note.

**Threshold, measured** on the test's bias-feed circuit (an SDD whose conductance follows a node charged by the
rectified drive through 1 kΩ ∥ 2 nF, τ = 2 µs; LTE 5 MHz QPSK, 400 µs, draft), `Segments=4` at warm-ups 0.25-20 µs
against a settled sequential run (`Segments=1 Warmup=50e-6`):

| seam error | ΔPout_avg | ΔACLR1 | ΔACLR2 (at −102 dBc) |
|---|---|---|---|
| 0.11-0.12 | ≤ 0.009 dB | 0.37-0.60 dB | 33 dB |
| 0.072 | 0.0005 dB | 0.12 dB | 29 dB |
| 0.0185 | 0.0004 dB | 0.027 dB | 17 dB |
| 0.005-0.013 | ≤ 0.0007 dB | ≤ 0.005 dB | 7-14 dB |
| 0.001 | 0.00002 dB | 0.0016 dB | 0.8 dB |
| ~1e-4 | ~0 | ≤ 0.0014 dB | 0.1-0.2 dB |

Pout moves 0.01 dB and ACLR1 0.1 dB near a seam error of 0.07, so the warning is at **0.01**. A band 100 dB down is a
different matter: a seam's step splatters broadband, so ACLR2 at −102 dBc moves by dB at 1e-3 and by 0.1 dB only at
~1e-4, which is the overlap's own disagreement with a fully settled warm-up (the HB start versus the propagated state),
so a run read that deep wants one segment. The seam error is not monotonic in the warm-up: it samples the signal at
three seams. Also measured: the cold `Segments=1` run (no warm-up, 5 µs skipped) is 0.005 dB from the settled one in
Pout, its own start transient.

**Parareal** (segments re-run from the previous one's end state) would keep the speed with long memory; not worth a
brief yet. The cases that need it are composed-FET runs of a few hundred µs with traps on, which a single segment
runs at 3.8× the wall clock of the split.

Gate: `tests/Engine.Tests/Envelope/EnvelopeSegmentMemoryTests.cs` (plan only for the composed FET; the bias-feed run
is ~0.1 s). The brief's test 2 asked for a *linear* bias feed: a linear circuit cannot show a seam, since its DC does
not follow the envelope and its harmonics do not couple, so the test uses the smallest nonlinear one.

## Envelope: a line in series behind a DC block diverged at any length (brief ENV-TLIN, 2026-10-23)

A small PA's envelope run (`TLIN` between the output DC block and the 50 Ω load, MaxHarm 5, standard) failed at step
22-26 (~50 ns) for every length (3-300 mm), loss, form and drive (−10 and 14 dBm), and ran with the line replaced by
1 mΩ.

**What was measured (§2a, in order).**
1. Harmonic balance on the same circuit (P1Tone at 2.14 GHz, MaxHarm 5) converges at −10 and 14 dBm. Not upstream.
2. Bisection on the reproduction: MaxHarm 1 fails at the same step; draft runs, fine fails (step 64); the line on the
   input side fails (step 58); 10 kΩ from n10 to ground fails; removing `Cout` (line straight from Vd) runs. Harmonics
   and drive are not involved.
3. The 1 Hz evaluation is `EnvelopeBaseband.Apply`'s classification probe (`BasebandErrors` stamps the unwrapped line
   at 1 Hz and 2 Hz, where a 3 mm line's θ is 3.6e-9° and clamps). It never reached the run: the run's k = 0 product
   stamped the HELD line, at ω = 0, which is the same clamp — and that clamped value was the baseband stamp.
4. Per step, the baseband state (k = 0) grew ×4.76 a step from step 8 (|x0| 96 → 3.8e13 by step 25), in n10 = Vout
   with Vd dragged along; its imaginary part grew from 1e-12 at the same rate. Harmonics 1-5 stayed bounded.

**Cause.** A lossless line's admittance has a pole at ω = 0 (Y ≈ 1/(jω·Z0·τ), its series inductance), so its ω = 0
stamp is `StampUniformLine`'s resonance clamp: |sinh γl| floored at 1e-9, Y = ∓j/(Z0·1e-9) ≈ ∓j·2e7 S — a purely
IMAGINARY admittance, which no DC stamp can be. The baseband product held the line at exactly that. Every lumped
element's k = 0 product is real by construction (A0 = a1 − j·dlt·C cancels jωC exactly), and the engine projects the
k = 0 interface currents and injections to their real part on that assumption. A complex baseband matrix behind a real
projection is not passive (j·B maps the real part of the state into the imaginary part and back), and Gear-2 grew it.
Removing `Cout` changes the topology enough that the mode did not form; a larger (draft) step damped it. The plain DC
solve takes the real part of the same stamp, so it saw the line as OPEN, while the HB start (complex `ExtractDC`) saw a
j·2e7 S "short": three different lines in one run. Checked: making the held stamp real (same magnitude) alone made the
reproduction run, with Pout_avg −1.1897 dBm, equal to the 1 mΩ circuit's.

**Fix.** `IDcShortedTerminals` (Core) states what a line is at DC — its terminal pairs shorted — on TLIN (both forms),
MLIN, SLIN, CPWG, CLIN, MCLIN, BCLIN and MBCLIN (each is lossless at 0 Hz: every loss term vanishes there).
`BasebandHeldModel` holds such a line as a branch with V_a = V_b, exactly as an inductor is at DC, rather than at its
clamp. The branch is stamped in every product (I = 0 when not holding), so every product and the HB start have the
same unknowns; the replacement carries no multiplier (a branch cannot be multiplied; the original's `Stamp` applies
it). Both engines hold the whole k = 0 product (its RHS too), the start's DC solve, its k = 0 extraction and its k = 0
back-solve, so the DC solve, the HB start and the stepping see one line. A line is no longer classified by its 1 Hz
tangent (never exact, and stamping it there only reaches the clamp — the clamp message no longer prints in an envelope
run); its held error is measured against the short. A non-line held element (Z(freq), an SnP the fit could not take)
stamps exactly as before in every one of those places. Not changed: TLineModel's stamp, so DC, S-parameter, HB and
loadpull runs, and every envelope run without a line, are identical by construction.

**Results.** Every row of the brief's table runs at 1 µs, as do MLIN (20 mm), CPWG (20 mm), CLIN (90°), MCLIN (20 mm),
MBCLIN (20 mm), SLIN and BCLIN (5 mm). The reproduction at 20 µs, L = 3 mm against `R:Rz n10 Vout R=0.001 Ohm`:
Pout_avg −2.0424 / −2.0424 dBm, Gt 7.67 / 7.67 dB, ACPR1 −67.11 / −68.00 dBc on both, ACPR2 −102.19 / −102.80 against
−102.23 / −102.79 dBc. At 14 dBm, 1 µs, the TLIN tangent error is 0.002 % (3 mm), 12.8 % (300 mm), 55.4 % (1 m),
75.3 % (2 m), 94.8 % (3 m), so 300 mm is correctly NOT named (the threshold is 50 %) and posts no warning at all. 3 mm
posts none. 1 m posts, verbatim:
- `envelope: 'TL3' (64.1 %) is held at its DC value across the ±50 MHz baseband envelope, off by that much (RMS) across it; the bias-path and baseband results through it are approximate`
- `envelope: 'TL3' (55.4 %) is approximated by a straight line in frequency around each harmonic, off by that much (RMS) across the ±50 MHz signal band; results across the band are approximate`

The coupled lines post the held warning at 94-100 % even when short: the short drops the A-B coupling, which is small
but compared relatively above the measure's −60 dB floor. That is the measure working as designed, not this defect.

**Found, not fixed — a lossy line at a harmonic resonance (a different defect).** SLIN 20 mm (−10 and 14 dBm) and BCLIN
20 mm still fail, and so does a lightly lossy TLIN placed at a 5th-harmonic resonance (L = 42.03 mm = 3λ/2 at
10.7 GHz, Ac = Ad = 2 dB/m at 2 GHz: step 18); the lossless 42.03 mm line runs, and so do heavily lossy ones
(Ac = Ad = 20). Here the baseband stays exactly real and harmonic 5 grows ×7.4 a step. Near θ = nπ,
Y11 ≈ 1/(Z0(αl + jδ)), so dB/dδ = −1/(Z0·α²l²) < 0 at the resonance: the tangent's C is a negative capacitance
(anomalous dispersion; Foster's theorem keeps dB/dω > 0 only for a lossless element), G + (jkω0 + α)C gets a
right-half-plane pole, and Gear-2 grows it. A rational fit of the line closes it, and is its own brief (ENV-FIT); nothing
is done here.

**The DC analysis treated an ideal line as OPEN (fixed).** `NonlinearDcEngine` takes the real part of every stamp, and
a lossless line's ω = 0 stamp is purely imaginary, so `Vdc 1 V → TLIN 3 mm → 50 Ω` solved to 0 V at the load — in a DC
analysis and in the DC pre-pass of every S-parameter, HB and loadpull run with a line in its bias path (an S-parameter
run of a nonlinear circuit linearised about that wrong point). Both DC engines now stamp a line's declared shorts
(`DcLineShorts`, from `IDcShortedTerminals`) instead of its clamp: 1 V at the load, through TLIN, MLIN and CLIN's
strip A, with no current in strip B. `LinearDcEngine` solved complex and saw the clamp as a 2e7 S reactance, a short
to ~1e-9; it is exact now. No hero, example or HB fixture has a line, so no gated result moved. Gate:
`tests/Engine.Tests/Devices/DcLineShortTests.cs`.

Gate: `tests/Engine.Tests/Envelope/EnvelopeLineInSeriesTests.cs`.

## Envelope: what a rational fit of a line costs (brief ENV-FIT §1b, 2026-10-23) — stopped at the gate

Measured before building anything, through the System Filter path as it stands (`EnvelopeTangentCheck.NodeS` samples,
every node terminated in 50 Ω; `VectorFit.FitByError`, tolerance 1e-3, `MaxOrder` 60, `Proportional = false`), on the
reference bench's span: f0 = 2.14 GHz, MaxHarm 5, NR 20 MHz (rate 122.88 MHz), so the top of the run is 10.76 GHz and
each product's band ±61.4 MHz. Lines are TLIN with `Eeff=1`. σmax(S_fit) is over 2,001 points from DC to the top.
"Anchor" is the grid the System Filter uses (101 points DC→top plus 41 across DC and each product's band, 321 samples);
"bands" is option (ii) (DC and the bands only, 226 samples). Time is the whole `FitByError` call, which tries orders
1, 2, … until one meets the tolerance; "one fit" is a single `Fit` at the order reached.

| element | grid | order | rel. error | σmax (dense) | σmax (on samples) | time | one fit |
|---|---|---|---|---|---|---|---|
| 50 Ω, 3 mm | anchor | 2 | 9.1e-6 | 1.00000 | | 0.03 s | |
| 50 Ω, 30 mm | anchor | 5 | 8.2e-5 | 1.00006 | | 0.05 s | |
| 50 Ω, 100 mm | anchor | 11 | 7.4e-5 | 1.00005 | | 0.34 s | |
| **50 Ω, 300 mm** | anchor | 27 | 1.4e-4 | 1.00013 | 1.00012 | **3.7 s** | 0.39 s |
| 50 Ω, 1000 mm | anchor | 41 (best) | **0.47** | 2.31 | | 38 s | |
| 25 Ω, 3 mm | anchor | 2 | 7.5e-4 | 1.00094 | | 0.00 s | |
| 25 Ω, 30 mm | anchor | 8 | 1.2e-4 | 1.00031 | | 0.13 s | |
| 25 Ω, 100 mm | anchor | 19 | 1.6e-4 | 1.00042 | | 1.4 s | |
| **25 Ω, 300 mm** | anchor | 48 | 4.1e-4 | 1.00163 | 1.00157 | **19.4 s** | 1.19 s |
| 25 Ω, 1000 mm | anchor | 51 (best) | **0.46** | 3.54 | | 35 s | |
| **50 Ω, 300 mm, `Ac=20 Ad=20 F=2 GHz`** | anchor | 58 (best) | **0.018** | 1.0142 | 0.99993 | **48 s** | 2.32 s |
| Z(freq) parallel resonator, Q 20 at 2.14 GHz | anchor | 2 | 1.5e-15 | 1.00000 | 1 | 0.01 s | |
| 50 Ω, 300 mm | bands | 14 | 6.0e-4 | 1.037 | 1.00086 | 0.47 s | 0.08 s |
| 25 Ω, 300 mm | bands | 25 | 4.7e-4 | 1.047 | 1.0011 | 2.3 s | 0.25 s |
| 50 Ω, 1000 mm | bands | 24 | 4.0e-4 | 1.086 | | 2.0 s | |
| 25 Ω, 100 mm | bands | 16 | 8.8e-4 | **1.177** | | 0.65 s | |
| 25 Ω, 1000 mm | bands | 39 | 9.5e-4 | **8.16** | | 8.3 s | |
| 50 Ω, 300 mm lossy | bands | 58 | 7.2e-4 | **259.7** | 1.0000 | 35 s | 1.90 s |

The Z(freq) expression was `Z[1,1]=50*freq*2.14e9/(freq*2.14e9 + j*20*(freq*freq - 2.14e9*2.14e9))` (a Z_Port in series).

**The §1b.2 gate fails, so nothing was built.** The 300 mm line meets the tolerance at order ≤ 60 (27 at 50 Ω, 48 at
25 Ω), but in 3.7 s and 19.4 s, not under ~2 s; the lossy 300 mm line misses the tolerance at every order (0.018 at
order 58, 48 s). What the numbers say about each option:
- **Most of the time is the order search, not the fit.** One fit at the order reached is 0.39 s (50 Ω) and 1.19 s
  (25 Ω); `FitByError` spends the rest fitting every lower order first. A search that starts near the answer would
  bring the 50 Ω line under 2 s with no change to the fit itself (2·τ·f_top ≈ 22 against the 27 reached). It would not
  reliably bring the 25 Ω line there: its mismatch resonances took order 48, far from that estimate, and each fit near
  it costs ~1 s. That is a change to the fit's search, so it is the owner's to choose.
- **(i) a higher order cap** does not help the lossy line: its error stops falling well inside 60. The skin-effect
  term (√f) has a branch point at DC, which no low-order rational function follows; the misfit is near DC.
- **(ii) bands only** is fast and meets the tolerance up to 1000 mm, and is passive on its own samples, but between the
  bands the fit is unconstrained: σmax 1.04-8.2 on the lossless lines and 260 on the lossy one. The stepping does read
  the realization outside the fitted bands (Gear-2 at `standard` resolves ±245 MHz around each harmonic, against
  ±61 MHz fitted), so a fit passive only on its samples is not known to be safe to step.
- **(iii) a delay realization** is the only option the table does not rule out for long or lossy lines.
- **1000 mm on the anchor grid** fails for a reason of its own: the anchor's 107 MHz spacing is 128° of the line's
  phase, so the fit cannot follow it between samples.
- A Z(freq) block that is rational in `freq` (as a resonator expression is) fits exactly at its own order.

### Option (iii), measured before building (2026-10-23)

Same span (f0 2.14 GHz, MaxHarm 5, signal extent ±50 MHz, standard step h = 2.03 ns, Nyquist ±245.8 MHz).

**The line loss models are not causal, so the loss residual cannot be fitted globally.** In a delay realization the line
is its Zc plus the wave from the other end delayed by τ and filtered by the residual H̃(f) = e^{−γl}·e^{jωτ}. Every
line model here gives γl = αl(f) + jβl(f) with an attenuation that has no matching phase (TLIN: αl ∝ Ac·√f + Ad·f,
βl = ωτ exactly; MLIN: conductor and dielectric α with a geometric-only εeff(f)). A real, frequency-dependent |H̃|
with no phase is an impulse response symmetric about t = 0, which no causal (stable rational) function follows across
DC to 10.8 GHz. Fitting H̃ alone (one response, 401 points DC→top plus the bands, 1e-3, order ≤ 60), with τ reduced by
Δ = 0, 2, 5, 10, 20, 50 ps to let the fit absorb some delay, met the tolerance in no lossy case: TLIN 300 mm
Ac = Ad = 20 → 0.20-0.28 relative error at order 51-58 (|H̃_fit| up to 1.54, not passive); Ac = Ad = 2 → 7.7e-3 to
1.6e-2; Ad = 20 only → 0.098-0.18; Ac = 20 only → 0.064-0.10; 42 mm Ac = Ad = 2 → 9.8e-4 at Δ = 50 ps only; 1000 mm
Ac = Ad = 20 → 0.35-0.38; MLIN FR4 300 mm (W 1.5, H 0.8 mm, Er 4.4, TanD 0.02) → 0.022-0.039. Each took ~55 s. The same
non-causality is the likely reason the lossy line failed the whole-line fit above.

**What works: exact delay plus the residual to first order around each harmonic.** The run reads the line only across
each product's band, where H̃ and Zc are smooth (the delay, which is what makes a line hard, is taken out). Per product
k, H̃ ≈ H̃(kf0) + H̃′(kf0)·Δf and Zc likewise. At the baseband the envelope is real, so H̃ can only be a real constant
there, and it is held at H̃(0) = 1 to keep DC exact: the baseband column is the lossless delay against the lossy line.
(A first draft of this table fitted a complex slope at the baseband too, which no real envelope can step; its baseband
column read 4-30× smaller.) max|ΔS| (50 Ω, worst over S11 and S21) across ±50 MHz against the exact line, beside today's first-order
treatment of the whole line (its Y stamp's value and slope; at the baseband, the short it is held at):

| line | today, k = 1…5 | today, baseband | delay + residual, k = 1…5 | delay + residual, baseband |
|---|---|---|---|---|
| TLIN 50 Ω 300 mm lossless | 0.054-0.40 | 0.31 | ≤ 1e-13 (exact) | exact |
| TLIN 25 Ω 300 mm lossless | 0.038-0.26 | 0.38 | exact | exact |
| TLIN 25 Ω 1000 mm lossless | 0.80-1.1 | 1.0 | exact | exact |
| TLIN 50 Ω 300 mm Ac = Ad = 20 | 2.5e-4 to 1.2e-2 | 0.32 | 1.1e-6 to 8.9e-5 | 0.12 |
| TLIN 25 Ω 300 mm Ac = Ad = 20 | 2.2e-4 to 1.1e-2 | 0.37 | 1.0e-6 to 8.0e-5 | 0.13 |
| TLIN 50 Ω 300 mm Ac = Ad = 2 | 0.028-0.097 | 0.31 | 1.6e-6 to 7.1e-6 | 1.3e-2 |
| TLIN 50 Ω 42 mm Ac = Ad = 2 (ENV-TLIN's resonance case) | 1.0e-3 to 1.1e-2 | 0.044 | ≤ 7.4e-7 | 1.8e-3 |
| TLIN 50 Ω 1000 mm Ac = Ad = 20 | ≤ 4.3e-3 | 0.88 | ≤ 3.1e-5 | 0.34 |
| MLIN FR4 300 mm | 0.061-0.83 | 0.56 | 2.8e-5 to 9.8e-5 | 1.3e-2 |

Passive in every case: σmax of the realized S ≤ 1 across the whole ±245.8 MHz the step resolves around each product
(1.00000 for the lossless lines, below 1 for the lossy ones). These numbers leave out the one remaining error, below.

**The delay is usually shorter than a step**, so it needs a fractional-delay interpolation (300 mm at εeff 1 is 1 ns;
h = 2.03 ns at standard). The delayed value is then partly the CURRENT step's, which puts a term in the stepped matrix
(linear, fixed per run, so still one factorization per product). Causal Lagrange on steps n, n−1, …, max error across
±50 MHz against e^{−jΔωτ}, and max |L| up to Nyquist:

| accuracy (h) | τ | linear | quadratic | cubic |
|---|---|---|---|---|
| standard (2.03 ns) | 0.1 ns | 9.4e-3 | 3.9e-3 | 1.8e-3, max |L| 1.004 |
| standard | 1 ns | 5.1e-2 | 1.6e-2 | 6.3e-3, max |L| 1.086 |
| standard | 3.3 ns | 4.8e-2 | 1.4e-2 | 5.2e-3, max |L| 1.147 |
| standard | 10 ns | 1.6e-2 | 3.6e-3 | 1.2e-3, max |L| 1.098 |
| fine (1.02 ns) | 0.3 ns | 1.1e-2 | 1.9e-3 | 4.1e-4, max |L| 1.036 |
| fine | 3.3 ns | 9.4e-3 | 1.8e-3 | 3.8e-4, max |L| 1.028 |
| draft (8.14 ns) | 1 ns | 0.30 | 0.37 | 0.50 |

Linear and quadratic never exceed |L| = 1 (passive); cubic does, so it is out. Quadratic at standard is 0.4-1.6 % at the
band edge, of the order of Gear-2's own error at that step (the SnP gate above needed 16x to reach 0.003 dB), and 5-25× better than today's treatment of a lossless 300 mm
line. At draft the signal band reaches 2.6 rad per step and no interpolation is accurate; draft is already documented as
coarse.

### Built (2026-10-24)

Code: `src/Engine/Envelope/EnvelopeLineDelay.cs` (new: `DelayLineModel` and the run-scoped substitution, history and
interpolation), `src/Core/Devices/IUniformLine.cs` (new: Zc and γl of TLIN, MLIN, SLIN, CPWG, whose stamps now read
through it with the same arithmetic), and the gate in `EnvelopeSnpFit.cs`. Gate: `tests/Engine.Tests/Envelope/EnvelopeLineDelayTests.cs`.

**How a line is stepped.** Each end is `V − Zc·I` = the other end's wave `V + Zc·I` from τ earlier, rotated by e^(−jω_kτ)
and filtered by the residual to first order (the slope as a time derivative of the delayed wave). τ is the line's group
delay at each product, so the residual has no phase slope; at the baseband it is the low-frequency delay and the residual
is 1. The delayed wave is quadratic Lagrange on the step grid; when τ < h the current step's weight is a term of the
stepped matrix (`Prepare`'s `delayNow`), fixed per run, so there is still one factorization per product. A split step
uses linear interpolation (its own spacing). With no product set the model stamps the line's exact steady state, so the
DC solve, the HB start and every extraction see the line itself, and at ω = 0 the Branin rows are its DC short: no
resonance clamp anywhere, and lines are no longer wrapped by `EnvelopeBaseband`. The DC solve numbers the line's branches
differently from the run's system, so the run copies the branch rows right after stamping each product (`Bind`) rather
than reading the model's last stamp later. A residual whose |h0 + h1·Δf| exceeds 1 anywhere up to the step's Nyquist is
stepped with h1 = 0 and the note says where; no case run here needed it.

**Accuracy, in the tests** (closed forms; steps chosen to divide 20 ns so the tone windows are whole periods — at
0.32 ns, 62.5 steps per period leaked 1.8e-3 into |S21|, which first looked like an engine error):
two CW carriers at f0 ± 50 MHz through a 25 Ω, 300 mm TLIN, step 0.4 ns (τ = 2.5 steps, interpolation's worst case):
−0.6673 / −1.7318 dB against −0.6675 / −1.7316 dB; the first-order treatment gave −0.5197 dB at −50 MHz (measured before
the change). The 100 MHz beat of those carriers through a square law and the same line in a bias feed: −0.7749 dB against
−0.7721 dB; held shorted it read 0.0000 dB. The interpolation error in |ΔS21| for that line: 1.3e-4 at h = 0.4 ns,
8.8e-4 at 0.8 ns, 2.9e-7 at 0.2 ns (τ = 5.003 steps, nearly whole). A Z(freq) parallel resonator (Q 20 at f0), one
carrier at +50 MHz: −2.2371 dB against −2.2385 dB (fitted at order 2); under the first-order treatment that run went to NaN.

**The reference bench** (ENV-TLIN's PA, 14 dBm, `TLIN Z=50 Ohm L=300 mm`, 20 µs): Pout_avg 24.0125 dBm, ACPR1
−28.09 / −28.98 dBc, ACPR2 −55.35 / −54.36 dBc; first order gave 24.0172 dBm, −28.06 / −28.94 dBc, −55.18 / −54.19 dBc.
The line is matched to the 50 Ω load, so it is nearly a pure delay at the PA and the two differ by 0.005-0.17 dB. It posts
`TLIN 'TL3': stepped as its delay (1.0007 ns) with its loss to first order around each harmonic; 0 % (RMS) from the line
across the ±50 MHz signal band` and no warning.

**ENV-TLIN's lossy-resonance divergence is gone for lines**: TLIN 42.03 mm Ac = Ad = 2 (3λ/2 at 5·f0) and SLIN 20 mm,
which failed, run at −10 and 14 dBm. BCLIN 20 mm still fails (step 16): a coupled line is still first order.

**Nothing else moved.** SHA-256 over every cube (wall time excluded), before the change and after it: an all-lumped PA run
(1 µs, and 2 µs with automatic segments), the same PA through an SnP (a 2 nH / 1 pF lowpass file) and a two-fundamental
lattice run — all four identical. HB, S-parameter, DC and loadpull do not reach this code; the four line models' `Stamp`
now calls `LineAt`, the same arithmetic in the same order. A System Filter or Duplexer with a multiplier m ≠ 1 was swapped
for its fit with the multiplier kept, but its fit is of the element's own stamp (m applied) and the realization allocates
branches, which a multiplier refuses: such a block could not run an envelope. The fit now carries none; m = 1 is unchanged.

**The fit gate, and what it found.** The brief asked for every linear element whose tangent error is above round-off. A
survey of the linear primitives (defaults or a typical FR4 geometry, products at k·2.14 GHz, ±50 MHz):
- at round-off (≤ 5e-13): R, C, L, PRLC, Bead, TFR, Short, SemiC, Transformer, Atten, VCCS, VCVS, WSProbe, IProbe;
- 1e-7 to 1e-6: MIMCAP 9.4e-8, AIRBRIDGE 4.7e-7;
- 1e-5 to 1e-4: MBEND 1.5e-5, MMITER 1.2e-5, MTEE 1.3e-5, MCROSS 3.2e-5, VIA 2.1e-5, VIAGND 6.1e-5, SPIRAL 5.4e-5,
  OSPIRAL 5.1e-5, SRLC 1.8e-4 (its series C makes the branch non-affine);
- above 1e-3: MKLOPF 5.4e-3, the Z(freq) resonator, and the lines (TLIN 8.5e-3 at its defaults, MLIN 2.6e-3 and SLIN 5.2e-3
  at 20 mm, CPWG 9.3e-4 at 10 mm — stepped as delays instead), CLIN 1.4, MCLIN 1.9e-2, MBCLIN 1.8e-3, BCLIN 3.6e-4;
- MTAPER 6.8e-4, MCURVE 9.1e-4.
Fitting at "above round-off" cost a default SPIRAL **21 s** (order 44) for an element first order already holds to 5e-5,
so the gate is **above the fit's own tolerance, 1e-3**: a fit accurate to 1e-3 cannot improve on a tangent that is already
closer. That fits MKLOPF (order 10, 0.3 s) and Z(freq) blocks, and leaves the rest as they were. SRLC is an inductive
branch and is not measured (as `EnvelopeBaseband` already skipped it).

**Coupled lines are not fitted, by decision pending the owner.** Through the fit path a 20 mm MCLIN fitted in 3.2 s
(order 15), but a 300 mm MCLIN was refused after **132 s** (0.17 at order 50) and a CLIN 90° was refused for σmax 1.0012
in 1 s; the refusal costs every order of the walk. They keep their first-order treatment and the baseband short, as
before. Options: a modal delay realization (a symmetric pair decouples into even and odd lines with the fixed [1 1; 1 −1]
transform; an asymmetric one needs the general modal path), or a bounded order search in the fit. A Z(freq) block that is
not rational in freq (a pure 20 ns delay expression) was refused after 38 s by the same walk.

### Reviewed after building (2026-10-24)

- **A generic fit's refusal is now written "not within tolerance and passive"** (`EnvelopeSnpFit.FitSystemBlock`), so a
  NaN relative error or σmax refuses the fit. Before, `NaN > Tolerance` was false and such a fit would have been stepped.
  Non-finite samples are already dropped before fitting, so no case run here reached it; it is the one way a fit could
  pass the gate that §1c forbids.
- **The refusal path had no test** (§3.5). `EnvelopeLineDelayTests.AZFreqBlockWhoseFitIsNotPassive_IsRefused_AndNamed`
  is the resonator with its sign turned: it fits exactly at order 2 but σmax(S) = 3, so it is refused, stays its own
  model, and is named with the reason and the first-order wording.
- **Zc is held at its value at each product**, not taken to first order as the "measured before building" table did. For
  TLIN, SLIN and the angle form it is constant anyway; for MLIN and CPWG its dispersion across a product's band is in the
  per-line error the note reports (`Error` compares against the exact Zc(f)), so nothing about it goes unreported.
- **The segment warm-up does not count a line's delay as memory** (`EnvelopeMemory` holds lines at DC). It does not need
  to: the warm-up is at least 5 µs, and a line would have to be ~1.5 km long to delay that much.

## Envelope: a pulsed-CW source, and what is measured inside its pulses (brief ENV-PCW, 2026-10-24)

`PCW` is a fifth carrier standard (`src/RfCore/Signals/PulsedCw.cs`); `EnvelopePulse` measures it. What was not
obvious:

- **The pulse fields are nullable on `CarrierSpec`, and that is load-bearing.** `CacheKey()` hashes the spec's JSON,
  and System.Text.Json writes every public property — `Label` included. A defaulted `double` pulse field would have
  appeared in every LTE/NR/CW/FILE carrier's JSON and moved every existing cache key; null with `WhenWritingNull`
  leaves them byte-identical (checked: samples, cache keys, labels and netlist text of all four, before and after).
  `GeneratorVersion` was deliberately NOT bumped for the same reason.
- **Unit power on the TOP, not unit mean.** `SignalComposer` normalises every other carrier to unit mean power; a pCW
  record is left at exactly 1 on the flat top, so `VsPeak` (built from `Pavg`) makes `Pavg` the pulse-top power with no
  code in the source or the engine, and harmonic balance (which plays `Pavg` as CW) sees the top. `Pin_avg_dBm`
  computed from the drive stays the record average.
- **A pulse skips no settle.** A cold run is measured after `min(5 µs, Stop/10)`; on a pulse that drops off-time and
  biased `Pin_avg_dBm` by +0.11 dB on a 200 µs record (a 0.498 power duty read as 0.511). A pulse starts at quiescent in its
  off-time and has nothing to settle, so the skip is zero for it.
- **The PRI test must be absolute.** `SignalRate.Divides` judges "whole samples" relative to the count; doubling the rate
  until a PRI divides would then accept any PRI once the count is large enough (a 100.5 µs PRI, which no
  1.92 MHz × 2ᵏ rate divides, passed within sixteen doublings). The native
  rate search uses an absolute 1e-6 sample and at most 2⁸ above the edge's own need.
- **The engine interpolates the pulse, it does not sample it.** The envelope step is 4× (standard) the native rate, so
  `CompositeWindow`'s Kaiser sinc reconstructs the drive between native samples. Within ±24 native samples of an edge
  that leaves sub-1e-4 ripple; the off-time and top away from the edges are exact. The droop window (10-90 % of the top)
  is far clear of it.
- **The memory note costs one extra elaboration**, and only on a pCW run: `Prepare` measures the slowest memory once
  (lazily) and both the note and the segment warm-up read that one answer.
- **`DE_pct` on a pulse is the SUPPLY's, and a storage capacitor shifts it in time.** During a pulse most of the drain's
  energy comes out of the storage capacitor, and the supply repays it over the next few R·C after the fall. A run
  that stops inside that recharge never counts the last repayment: one 100 µs pulse stopped 4 µs after its fall read
  `DE_pct` 126 % (470 µF through 0.2 Ω). Nothing in the engine can tell stored charge from delivered energy, so the
  doc states the rule instead: end `Stop` after the last pulse's recharge.
- **Pulse cubes and the zero settle answer the same question the same way.** The skip used to look at the first
  netlist's first `MOD_Source`, and the cubes at the merged result's sources; both now go through `EnvelopePulse.Of`.

## Harmonic balance: a source directly on a nonlinear port was not honoured (2026-10-24)

An ideal voltage source with nothing between it and a nonlinear device's port pins that interface node at every
harmonic: its row and column of `Z_NN` are exactly zero. `ExtractDC`'s inductance regularization cures only the DC case
of an ideal CHOKE through a source; with the source on the port nothing changed, and the per-harmonic `Extract` had no
handling at all. `InvertNN`'s Gauss-Jordan skipped the singular row, leaving that node with no equation the device
current could balance against. Nothing failed: the solve converged, and the node behaved as if about 1 Ω stood between
it and its source. Found when an ideal 0 V source on an SDD's thermal port let the junction sit at 22.7 K (DC: 0), and a
0.5 V source on an SDD diode read 0.49975 V.

- **Fix:** `HbLinearExtractor.RegularizePinned` puts `InductanceRegR` (1 µΩ) on a pinned diagonal before every
  inversion — the same R → 0 limit the inductive pass converges to — and `HbSmallSignalCache` calls it too, so the
  stability probe's `Y_NN` stays the Newton's. A port that is not pinned, and a grounded interface node, are untouched,
  so every other circuit is bit-identical (the HB, envelope and loadpull classes, 523 tests, unchanged).
- Under `InductanceRegularization=Never` the DC path still throws first, as it did; the AC path regularizes regardless,
  because it never had a refusal and was silently wrong. Gate: `HbPinnedPortTests`.
- **When building an isothermal SDD bench by hand, hold the thermal node with a small resistor or an ideal source
  alike now** — before this fix only the resistor gave the right answer in harmonic balance.
- **A pinned node now sets a residual floor.** Its row carries Y = 1/1 µΩ = 1e6 S, so round-off in V alone (~1e-16 ×
  a volt or so) leaves ~1e-10 A on it: a `Tol=` at or below that is unreachable, and Newton stalls at its last
  line-search halving instead of converging. `ExternalDeviceHbDcConsistencyTests` (an ideal `V_1Tone` on the gate) sat at
  1.05e-10 against `Tol=1e-10` and was moved to 1e-9. That this fix is what turned it red is inferred from the code, not bisected.

## Envelope: power through every IProbe, and `Zmeas` retired (brief ENV-MEAS-1, 2026-10-28)

Every power an envelope run reported was |V|² / (2 · `Zmeas`) at a probe node: right only at a resistive load equal to
`Zmeas`, with no warning otherwise, and a complex `Zmeas` silently kept its real part. Both engines already solved every
IProbe's branch current at every step; nothing read it.

- **What is recorded.** Every `IProbeModel` (a WSProbe shares the stamp and is excluded), at every kept step: its branch
  current from `Xm1[LastBranchIndex]` (the absolute MNA row, read after the products are stamped, like the supply rows)
  and its first node's voltage from the same vector. On the lattice a negative-frequency product is conjugated exactly as
  the probe voltages are. The DC entry is kept real, as `V`'s is. `EnvelopeEngine.IProbesOf` is the one selection.
- **Why the power is averaged per step, not from averages.** ½·Re(Vₖ·Iₖ*) at a step is the exact power that product
  carries (the cross terms between products average out over a carrier period), so ⟨½·Re(V·I*)⟩ is the average power;
  the product of the averages is not, and at k = 0 it would miss V₀ following the modulation behind a supply
  resistance. The window is the built-in scalars' own (`t0`, after a cold run's settle skip), so at a true 50 Ω load
  `Pprobe_fund_W` reads `Pout_avg_dBm` to 1e-6 dB.
- **A reactive load's probe power is not exactly the resistor's power under modulation.** Into R + L the envelope power
  carries ¼·L·d|I|²/dt, which averages to the change in stored energy over the window, not to zero. The reactive gate
  therefore runs CW, where the envelope is stationary from its harmonic-balance start; under NR the difference is that
  stored-energy change over the record, and it is real power that did flow through the probe.
- **`V` and `PSD` keep exactly the `Probes` nodes.** An IProbe's node voltage goes only into the power cubes; appending it
  to `V` would have grown the examples' node axes.
- **`Zmeas` → `EnvelopeDataSet.MeasureOhms` (50).** `Plan.Zmeas` and `EnvelopeParams.MeasureZ` are gone;
  `EnvelopeAnalysis.ZmeasExpr` stays as the carrier of an old file's value, so `Resolve` can evaluate it in the run's own
  scope (it may name a global) and `Prepare` posts one warning on the first netlist under one key — a sweep's merge
  dedups it, so N points warn once. The reader, the schema entry (now DEPRECATED, like HB's `Sweep=`) and the `.csch` DTO
  still accept it; neither writer emits it.
- **A `.csch` loses a non-50 `Zmeas` before the run can warn.** A schematic reaches the elaborator through `CnlWriter`'s
  text (`SchematicCircuit.RoundTrip`), and the writer no longer says `Zmeas`, so such a schematic runs at 50 Ω with no
  warning. Only a `.cnl` that states `Zmeas` warns. The envelope card no longer shows the field, so the next save drops
  the key either way.

**Gate 1, the examples did not move.** A scratch harness ran `NR20_ACPR`, `NR20_ACPR_vs_Pin`,
`NR20_2C_400MHz_single` (Stop 20 µs) and `PulsedPA_pCW` (Stop 130 µs, one whole pulse) through `CircuitEvaluation` before
and after, comparing every cube's axes and values as raw bits. Run to run, before the change, all four were identical
(determinism checked first). After it: 29, 29, 30 and 28 pre-existing cubes **bit-identical**, none moved or missing;
each run gained exactly `I`, `Pprobe_W`, `Pprobe_fund_W`, `Pprobe_t_W` and `__ProbeBranches` (all four examples already
place IProbes). Gates 2-9: `EnvelopeProbePowerTests`, plus `EnvelopeModeRoundTripTests` for the writers.

## Harmonic balance: the same probe-power cubes as envelope (brief ENV-MEAS-2 R-envm2-3, 2026-10-28)

All three HB dataset builders (single-tone, the rectangular two-tone, the lattice) emit `Pprobe_W[branch,
harmonic|mixIndex]` and `Pprobe_fund_W[branch]` beside `I` and `__ProbeBranches`, so `HB1.P("Iout")` in a measure line
means what `ENV1.P("Iout")` means.

- **The voltage is the V cube's own row**, not a fresh back-solve: the converged Newton spectrum at an interface node,
  the linear back-solve elsewhere, zero for a probe whose first node is ground (`ProbeFirstNodeVoltages`). An interface
  node's back-solved voltage differs from its Newton value by the solve residual, and a power that disagreed with
  ½·Re(V·I*) taken from the cubes by hand would look like a bug.
- **The fundamental band** is harmonic 1, or each tone's first-order product: `(1,0)` and `(0,1)` on two tones, the unit
  vectors on the lattice. Index 0 of the spectral axis is DC on all three, so `P("IDC", 0)` is V₀·I₀ everywhere.
- **The probe set is HB's own** (every `SeriesProbeModelBase`, the set already in `__ProbeBranches`), so a WSProbe gets a
  power row too, as it already gets a current row. Envelope matches `IProbeModel` only; the two differ only on a bench
  that places a WSProbe in an HB run, where the extra row is harmless.
- **Nothing else moved**: the builders only read V and I, so every pre-existing cube is unchanged. One visible
  consequence: the data display's auto-seed ranks a plottable cube whose `branch` labels are exactly the placed probes
  above raw outputs, so an unswept HB run with IProbes and no measurements now opens on `Pprobe_W` (harmonic as X)
  rather than `V`, as envelope has done since ENV-MEAS-1. Left as is: it is the existing "a placed probe outranks a raw
  output" rule doing what it says.

Gates: `ProbePowerAccessorTests` (`OnHarmonicBalance_PMeansWhatItMeansOnEnvelope` agrees with the envelope run to
0.01 dB on one CW bench; `OnATwoToneHb_TheFundamentalBandIsTheTwoCarriers`).


## Envelope: `Probes` retired; labeled nets record voltage, IProbes measure power (brief ENV-MEAS-3, 2026-10-28)

A run records every labeled net (`NodeMap.LabeledNames`, HB's own set, ascending node index; a `.cnl` with none records
its MOD_Source nodes) as `V` and `VSD[node, freq]` in dBV/Hz, and every IProbe's delivered power on a `branch` axis:
`PSD` (½·Re of `SignalMeasure.CrossWelch` of the probe's V and I, the same spans, grid and baseband rule as `VSD`),
`ACLR`, `IMD`, `Pavg_dBm`, `PAPR_dB`/`CCDF` of p(t) = ½·Re(V₁·I₁*), `Gt_dB`, `DE_pct`, `NMSE_dB`, `Delay_s`, AM-AM/AM-PM
with `AMPM_min`/`_max`, the cloud (`Pin_inst_t_dBm`, `Gt_t_dB`, `PM_t_deg`, every m-th aligned sample, at most 20,000)
and the pulse cubes, now `[branch, …]`. `Pout_avg_dBm` and every 50 Ω assumption are gone (`EnvelopeDataSet.MeasureOhms`
removed); `Pin_avg_dBm` and `Pdc_W` stay scalars. A probe whose fundamental-band power is below 10⁻⁶ of the available
drive is NaN in the output rows. The seam error now also covers every IProbe current. `Probes=` is read as before and
its nodes join the recorded set, with one note (`EnvelopeRunner.Plan.ProbesNote`); a run with no IProbe notes once that
no power was measured. D1-D4 taken as recommended; the cloud cap held (`src/Ui/RESOLVED.md`, same brief).

- **`Welch` is now `CrossWelch(x, x)`'s real part**, bit for bit: the complex accumulator's real part is
  re·re − im·(−im), the same two products the old `|X|²` summed. Gate 3a on the real examples confirms: old `PSD` at
  every node equals the new `VSD` + 13.0103 dB to 6e-14 dB.
- **The examples' input spectra are expected to move, and do.** At `Iin` (the gate network) NR20_ACPR's channel power
  reads 14.01 dBm where the old `Vin` read 24.36 dBm into an assumed 50 Ω, and its ACPR1/ACPR2 are blank: the
  adjacent-channel distortion there flows from the device toward the source, a negative delivered density, which the
  brief plots as NaN. PAPR moves 9.45 → 9.32 dB. NR20_2C: `Iin` Pch 10.50 / 8.37 dBm (old `Vin` 9.29 / 8.39), PAPR
  9.09 → 9.65 dB.
- **Gate 1 (scratch harness, Stop 20 µs; pCW 150 µs, one pulse), output figures old → new at `Iout`:** NR20_ACPR Pout
  36.9853 → 36.9842 dBm (−0.0011 dB: the 100 pF block's −j0.8 Ω in series with 50 Ω, the delivered power being exact),
  Gt the same, DE 36.9523 → 36.9430 % (0.009 pp), NMSE and AM-PM bit-identical, AM-AM bins ≤ 0.0013 dB (min/max ≤ 0.004),
  ACLR ≤ 0.0011 dB, PAPR 2e-7 dB. NR20_ACPR_vs_Pin: the same at every Pin (DE ≤ 0.0105 pp). PulsedPA_pCW: every pulse
  figure, Pout, Gt and DE within 1e-9 (the new `Iout` feeds the load directly).
- **NR20_2C_400MHz_single had its `Iout` at the drain (`Vd`), ahead of the 0.3166 pF + 20 nH output resonator**, not at
  the load as the brief assumed of all four. There, Pout, Gt, DE and ACLR matched (the network is lossless), but NMSE
  moved 1.8 dB, AM-AM up to 6 dB, AM-PM 57°, PAPR 1.1 dB and IMD 4.5 dB: those are read from the probe node's voltage and
  the instantaneous V·I, and at the drain both carry the resonator's stored-energy exchange. The probe was moved in front
  of `RL` (first node `Vout`, the load on a new net); every figure then matches the old build to 1e-9. A probe meant for
  output figures belongs at the load, which `simulations.md#envelope-iprobe-power` now says.
- **Gate 5 as stated is not a property of the min/max cubes.** On the pulsed-radar GaN biased through ideal sources
  (no linear memory), LTE 5 MHz, 20 µs, `AMAM_max − AMAM_min` was 0.08-0.15 dB in the linear bins and 0.45-0.58 dB in
  compression **with every memory switch off**, and no larger with them on (0.11-0.55 dB). The spread is mostly the
  gain's own change across a 0.5 dB bin (about 1 dB per dB in compression). A slope-free measure, each cloud sample's
  distance from the AM-AM curve interpolated at its own drive, was 0.032 dB / 0.33° off and 0.038 dB / 0.33° on: this
  device's drain lag captures quasi-statically, and 20 µs shows no hysteresis to speak of. What is gated instead is
  exact: the min/max cubes are the extremes of the cloud's own samples in each bin
  (`EnvelopeProbesRetiredTests.TheCloud_BinnedOnThePinGrid_IsAmAmAndAmPm_WithTheirSpread`). A memory figure that
  subtracts the bin's slope is a follow-up for the owner to decide on.
- **`.csch` drops `EnvProbes` on the way to the run**, as it dropped `Zmeas`: a schematic reaches the elaborator through
  `CnlWriter`, which no longer writes it. Only a `.cnl` keeps its `Probes` nodes; a schematic labels nets instead.
- **Delivered power through a lossless network equals the load's in the mean and per frequency, but not per sample.**
  That is why AM-AM is exact only where V and I are read at the load itself, and why gate 1 needed the probe moved.

Gates: `EnvelopeProbesRetiredTests` (2-8), `EnvelopeExampleDisplaysTests` (9, in `tests/Ui.Tests`). The envelope tests
that read the retired cubes place an IProbe in front of their load and read `Pavg_dBm`/`Gt_dB` there.

## Envelope: wBond checked against the line/SnP treatment; baseband hold rule; WireTemp (2026-10-29)

**A wBond needs no fit and no delay.** Its stamp is R(f) + jωL(f) per array plus fixed capacitances, a quasi-static
lumped model. Measured by the envelope's own per-element measure (k = 1..5 at 2.14 GHz, ±30 MHz): 2.5e-8 to 2.2e-7 for
1, 4 and 2×20 wires and a 400 mil wire, against a fit tolerance of 1e-3. A PA with the wBond at the drain output or as
the drain feed's only inductance matched R+L+C at its 2.14 GHz values to ≤ 0.0008 dB Pavg and ≤ 0.002 dB ACPR1, 1 and
4 segments alike. At DC it is its series resistance (no open-line defect).

**The baseband hold took the worse approximation.** `EnvelopeBaseband` held every element whose baseband tangent error
exceeded 1e-6 (`ExactBelow`) at its DC stamp, without comparing the two. A wBond's skin effect puts its tangent at
6e-6 to 8.4e-4 and its DC stamp (inductance gone) at 0.25 % to 2.2 %, so it was held at an error 400× larger. It is now
held only when the hold is the closer of the two. Lines and coupled lines are unaffected (they take the shorts path);
an element whose baseband slope blows up, the reason the hold exists, has a tangent error far above its hold. The
measured effect on the PA was under 0.002 dB either way. Envelope tests 83/83, swept-envelope 2/2.

**WireTemp from an envelope run.** The method is in `docs/design/wbond.md` §5.6. Two things were found building it:
- **A 2 µs window of a 20 MHz NR carrier delivered 0.21 dB below the source's Pavg** (39.785 vs 39.993 dBm), which put
  the wire 0.11 K low on a 3 K rise against HB at the same available power. The gate compares against HB driven at the
  power the envelope delivered; then the two agree to 0.011 K on a 12.5 K rise.
- **Dropping the cross-array terms** (each array's own ⟨|I|²⟩ only) read 96.28 °C against HB's 97.50 °C for two coupled
  arrays in parallel, so the gate catches it.

## MC-30 — Verilog-A convergence parity, and a worker that exits mid-run (2026-10-30)

**Reproduced on scratch copies of the GanVerilogA bench, one instance per bench** (a joint DC hides which side failed),
the same model as `gan_4x125.cmodel` (ComposedFET) and as the exported `gan_4x125.va`:

| Bench | ComposedFET | Verilog-A, before | after the seed fix | after the line-search judge too |
|---|---|---|---|---|
| DC, terminals through chokes, Vgs −5…0.75 × Vds 0…20 (504 points) | 504/504 | 504/504 | 504/504 | 504/504 |
| DC, 1 kΩ gate and 25 Ω load line, Vgs −5…0.75 × Vdd 0…40 (504 points) | 504/504 | 504/504 | 504/504 | 504/504 |
| The example's own DC sweep and HB drive sweep, both instances | all | all | all | all |
| GanLoadpull's 49-load grid to 3 dB compression: loads reaching it | 49/49 | **1/49** | 45/49 | **49/49** |
| the same, Pin rungs converged | 1,025/1,025 | 21/69 | 1,018/1,022 | 1,025/1,025 |
| the grid's wall clock (Debug) | 10.5 s | — | 32 min 46 s | 13.7 s |

**DC was never the problem** on these benches; the brief's gate (the DC sweep converges at every point on both sides)
held before any change. **The loadpull was.** Every load after the first stopped at the −50 dBm tickle, Newton stuck
at ‖F‖ 0.3–0.7. The same load run alone (cold start) converged, so the failure was the seed: `LoadpullEngine.Run`
stored each load's LAST converged rung — the compressed state — as the neighbour seed, and the next load's tickle,
50+ dB below it, started from there. The native model survives that start because its network and thermal network are
linear elements outside the Newton loop; the exported module carries the network, the thermal node and the trap node
inside the device (nine internal nodes), so the whole drive-up has to be walked back in one solve. **The candidates the
brief listed were checked and are not it**: the native core has no junction limiting either, both sides scale the trap
node by the same `TrapDynamics.NodeCapacitance` (1e-3) with identical capture/emission equations, and an OSDI model's
Jacobian is the compiler's exact derivative. **Fix:** the neighbour seed is the load's FIRST converged rung (the tickle,
or PinStart without one) — the drive the next load starts at. The native side's printed results are unchanged to the
shown precision (its `.npy` moves in the last bits); the loadpull, Hero 3 and parametric-sweep tests are 146/146.

**The 4 near-compression losses, and the Verilog-A grid's 32 min 46 s, had one cause, now fixed.** A scratch harness
ran grid point 1 alone (cold, 21 rungs, Debug), wrapping the provider to time and count every evaluation:

| One load | Newton iterations | HB evaluations | per iteration | worker time | wall |
|---|---|---|---|---|---|
| ComposedFET (unchanged by the fix) | 131 | 248 | 1.9 | — | 0.27 s |
| Verilog-A, before | 845 | 22,124 | 26.2 | 5.2 s (26 %) | 19.6 s |
| Verilog-A, after | 99 | 107 | 1.1 | 0.03 s | 0.33 s |

Batching was not it (one worker call per HB evaluation, 32 samples, ~8 µs per sample), and neither was partition size
(an evaluation costs about the same on both sides): the NUMBER of evaluations was 89×, the line search rejecting step
after step. MC-25's self-admittance-scaled line-search norm switched on only for a device's DECLARED `StatePorts`;
`ExternalDeviceModel` declares none, so the exported module's trap node (1 mF, ≈1.3e7 S at 2 GHz) was judged in raw
amperes and swamped every KCL row. **Forcing the scaled norm on was not enough**: it fixed the loadpull but stalled 10
of `VerilogALinearChargeTests`' HB points at 1e-8…1e-11 A against their 1e-12 tolerance (a weakly connected internal
row's large weight makes its round-off the scaled norm's floor). **Accepting either norm for EVERY state row was not
right either**: it lost `EnvelopeConvergenceTests.AStepTheLineSearchCannotTake_IsRetakenInSubSteps`' native start point.
So the fix is three-way, `HbNewton.JudgeFor` → `LineSearchJudge`: **Plain** with no state rows, **Scaled** (MC-25,
unchanged) for declared state rows, and **ScaledOrPlain** — a trial passes when it decreases either norm, relative to
the iterate (`LineSearchNorm`) — when an external device's free internal node is on the interface, since what such a
node carries cannot be known. Every HB loop (single-tone, 2-D, N-tone) and both envelope engines take it. A netlist
with no external device solves exactly as before (the ComposedFET's counts above are identical). `StatePorts` is NOT
widened: harmonicaRF reads `HasStateRow` to relax its tolerance to `StateNodeTol` and the envelope reads `StatePorts`
to filter its seam warning, and neither should change for a compiled model.

**After: the 49-load grid reaches compression at 49/49 loads, 1,025/1,025 rungs, in 13.7 s** (the ComposedFET grid
10.5 s), and the two placements agree to the log's 0.01 dB print resolution in Pout over 976 common rungs. Gate:
`VerilogALineSearchTests` (one load of that bench on the shipped model: Verilog-A at most twice the ComposedFET's
evaluations — 107 against 248; with the judge reverted it is 22,124 and fails). Suites run: Engine HB / loadpull /
envelope / external / Hero / sweep 724, Harmonica 270, Core external 313, Ui Verilog-A / external / trap 202.

**A worker that exits mid-run (R-mc30-6).** `DeviceWorkerChannel` now throws `DeviceWorkerExitedException` (a subtype of
`ExternalDeviceException`, which is no longer sealed) whenever the process has gone, carrying its exit code (new
`IDeviceWorkerTransport.ExitCode`, null for a stream transport) and its last error line; `ExternalDeviceModel.Guarded`
keeps the type when it prefixes the instance name. `NonlinearDcEngine.TryBuildResidualAndJacobian` rethrows it rather
than treating it as a refusal — every smaller step would fail the same way against a dead pipe. `ParametricSweepEngine`
catches it per point: the attempt is reported (`Sweep 'SW1' at Vg = 2: the device worker (…) exited with code 3:
<last line> The point failed; the worker was restarted and the point run again.`), the re-elaboration restarts the
worker (the registry already replaces a dead provider it owns), and the point runs once more; a second exit at the same
point stops the run with that sentence. The brief's "marked failed … and the run continues" is read as the failed
ATTEMPT being reported and the point retried, since a NaN point in an I–V family is worse than either a real value or a
clear stop, and only a retry gives "a second exit at the same point" a meaning. Loadpull's own grid loop and envelope
are not covered. Gate: `WorkerExitMidRunTests`, with `tools/DeviceWorkerExample --exit-on-eval n` /
`--exit-when-vgs-above v`.

## S-parameter noise: passive thermal noise and the noise waves (2026-11-09)

`Noise=yes` on a `sparam` directive (`AnalysisSettings.Noise`) adds `Cs` [freq, i, j] (W/Hz, the outgoing noise waves'
correlation with every port in its own noiseless Z0) and, for a two-port, `NF`, `NFmin` (dB, T0 = 290 K), `Rn` (Ω) and
`Sopt` (on Re Z0 of port 1), each [freq], through `RfCore.Data.NoiseMetrics`. Off, nothing is entered: the same
cubes, S bit for bit and the same `RunStats`. On, S is still bit-identical and each frequency costs N more
back-substitutions and no factorisation. `SParameterNoise.cs` holds all of it.

- **The noise waves come from the engine's own wave extraction.** With no drive, port k's current is `−V_k/Z0_k`, so
  the Kurokawa `b_k = (V_k − Z0_k*·I_k)/(2√Re Z0_k)` the S loop reads is `(√Re Z0_k/Z0_k)·V_k`. The port-voltage row of
  `M⁻¹` is one transposed solve per port on the SAME `SparseLU` the S columns used; `SolveTranspose` is Hermitian, so the
  row is `conj(y)` (the HB small-signal cache's finding, reused). `Cs = D·Σ H·C·Hᴴ·Dᴴ`. A dropped conjugate leaves the
  diagonals right and mirrors the off-diagonals' phase; `lossy_lc`'s complex off-diagonals catch it. The legacy path
  (some Re Z0 ≤ 0) terminates nothing and is refused in `Prepare`, before the DC pre-pass (`NoiseDiagnostics`).
- **Passive parts: Bosma/Twiss on the recorded stamp.** Each part is recorded (`EnvelopeTangentCheck.Recorded`), every
  node tied to ground through 50 Ω, and `Z_t` taken from the whole recorded system, so branches are eliminated. The
  part's open-circuit noise is the terminated network's `2kT(Z_t + Z_tᴴ)` less the terminations' `(4kT/R)·Z_t·Z_tᴴ`, and
  its node currents `C_j = Z_t⁺·C_V·Z_t⁺ᴴ`. Equal to `2kT(Y + Yᴴ)` where Y exists (a resistor, R‖C, R‖L‖C, a lossy
  inductor and a lossy TLIN agree with the Schur complement of their own stamp to ≤ 5e-15). An ideal tie (a short, an ideal transformer, a 0 V branch) makes `Z_t` singular; the SVD
  pseudo-inverse (cut 1e-10 of σ_max) drops exactly the injections that drive no node voltage, which the surrounding
  network cannot see either, so it stays exact (a resistor and a short recorded together, seen through a terminated
  network). Lossless parts come out at round-off with no special case. Ceilings: a resistor below ~1 nΩ reads as a tie,
  and one far above 50 Ω loses about log10(R/50) digits to the subtraction (a few 1e-9 relative at 1 GΩ, where its noise
  no longer matters).
- **Record before the assembly.** A stamp writes the model's branch index (`LastBranchIndex`, an SnP's port branches);
  recording one into a scratch numbering after `StampAll` would leave the regularisation and the probes reading the
  scratch index. The parts are recorded first at each frequency, and `StampAll` writes the real indices back.
- **A Mutual and its inductors are one recording**, inductors first (`MutualInductanceModel.Coupled` joins them; chains
  of couplings union). Recorded apart, each inductor's loss would be reduced through its own branch with the coupling
  missing. A coupled group with members at different temperatures is taken at the hottest.
- **Temperature** is `Temperature.ResolveDeviceC` per part (Temp, then Dtemp, then the ambient — 300 K by default, so
  a design stating nothing is not at T0 = 290 K).
- **Noiseless.** Ports and independent sources by definition. Nonlinear devices, active or black-box linear parts
  (a VCCS, an op-amp, a negative resistor), external devices that declare no noise sources and S-parameter blocks with
  gain and no noise data have no noise model here and are named in one `sparam-noise-noiseless` warning (the next two
  sections).

Gates: `tests/Engine.Tests/Linear/SParamNoiseTests.cs` against closed forms (a matched pad at 290 K and 400 K, Bosma's
`kT(I − SSᴴ)` entry by entry on a lossy reactive network, coupled lossy inductors and an ideal transformer) and
`testdata/noise/refs.json` (`gen_noise_refs.py`: numpy nodal and ngspice 47 `.sp … donoise` at 290 K — mixed `Temp=`,
a Mutual, a VCCS amplifier with 75/50 Ω ports: Cs within 2e-13); serial = parallel bit for bit; a parametric sweep stacks the cubes;
`circuitrf sparam … -o x.s2p` writes the noise block.

## S-parameter noise: an S-parameter block's own noise data (2026-11-09)

A two-port SnP whose Touchstone file has a noise block is noisy by that data, passive or not
(`SParameterNoise.DataFile`); a passive SnP without one is thermal through Bosma like any passive part, since
`SnpModel.ActivityFor` measures σ_max(S) on the file; an SnP with gain and no noise data, and every Deembed, stay
noiseless and named.

- **The data path.** `SnpModel.NoiseData` turns the rows into `TwoPortNoise` (Fmin as a factor, Rn = rn·R, Γopt and Rn
  on the option line's R = Re Z0; the reader is 1.x-only, so rn is always normalised). `NoiseAt` interpolates linearly
  in Fmin, Re Γopt, Im Γopt and Rn, as `u·a + t·b` so a sweep point on a row returns the row exactly, and holds the end
  rows outside. The engine forms `C_A = ChainCorrelation(p)` and `C_Y = T⁻¹·C_A·T⁻ᴴ` (`AdmittanceFromChain`) with Y the
  admittance of the S the block stamps (`SnpModel.SAt`, now also what `Stamp` uses, offsets included) at the file's R —
  a real R, so `RFNetwork.SToY` is the power-wave Y here. The densities are absolute at T0 and do not move with the
  ambient; the multiplicity scales C_Y (m independent copies). Re-referencing is free: C_Y is reference-independent, so a
  25 Ω file between 50 Ω ports reports Γopt on 50 Ω with nothing converted.
- **A floating reference.** Port k's current enters at `Nodes[k]` and leaves at `ReferenceNode`, so the 2x2 C_Y goes onto
  three nodes through `[[1,0],[0,1],[−1,−1]]`, ground rows dropped (`AcrossPorts`). The gate makes it observable: a
  passive file whose noise rows are its own thermal noise at T0, on a reference reaching ground through 20 Ω, the bench
  at T0, must give Bosma's `kT0(I − SSᴴ)` for the whole network (9e-15); dropping the reference row fails it.
- **A Deembed states no noise.** The inverse of a noisy fixture would need the fixture's noise taken away, which no
  current source can do, so it is named as noiseless even on a file with a noise block.
- **Two warnings, once per instance, both decided in `Prepare`** so a frequency-parallel run reports what a serial one
  does: `sparam.noise.data-band` (the sweep leaves the rows' band) and `sparam.noise.data-unphysical` (some row fails
  `0 ≤ Fmin − 1 ≤ 4·Rn·Gopt`; the data is used as given). Realizability is checked with a 1e-3 tolerance on the factor:
  measured NFmin rounded to 0.01 dB misses the bound by up to ~1e-3.

Gates: `SParamNoiseTests` — refs.json's `friis_matched` (the Friis closed form), `friis_mismatched` (scikit-rf `connect`),
`touchstone_r50`/`_r25`/`_r25_in_50` (the file's values, Γopt re-referenced) and `pozar_ex12_5`, all within 1e-14 dB and
3e-15 in Cs; Pozar's 2 dB circle from the reported cubes against scikit-rf's `nf_circle` (1e-9) and the textbook (1e-2);
a passive SnP with no noise data against Bosma on a floating reference; linear interpolation and the held ends; the
unphysical-data warning; the noiseless naming of an SnP with gain and of a Deembed; and `circuitrf sparam … -o x.s2p`
writing back ampA.s2p's noise rows.

## S-parameter noise: an external device's own noise sources (2026-11-09)

An OSDI device is noisy by the sources its model declares (the worker's `noise` command, `tools/osdi-worker/README.md`
Noise). `SParameterNoise.DeviceSources` reads them once per instance in `Prepare`, for every sweep frequency, at the DC
operating point the run linearises the device about (`SParameterEngine.BuildBias`, now internal), so the noise and the
small-signal stamp describe the same bias. A failed read fails the run: noise that was asked for and not produced is not
a warning.

- **Mapping.** Source s between device nodes (a, b), density p, is a current noise source: `p·[[1, −1], [−1, 1]]` on
  circuit nodes `(Nodes[2a], Nodes[2b])`, or p alone on `Nodes[2a]` when b is −1 (ground) or the circuit node is 0. Internal
  nodes are ordinary MNA unknowns, so they map the same way; a pair collapsed onto one node cancels. All sources of one
  device are summed into one `NoiseCurrents` (independent sources add).
- **Multiplicity is applied here.** The worker passes no `$mfactor` and returns one device's densities; m independent
  copies in parallel carry m times the density, as their stamp carries m times the admittance.
- **The lookup is by ω.** The contributor is called with the ω `RunRange` computes, `2π·f`, and the table is keyed by
  the same expression, so it is an equality of the same double.
- **A device that declares no sources is named in the `sparam-noise-noiseless` warning.** The descriptor cannot tell a
  model that states it has no noise from a provider that cannot report any (the other two worker kinds never speak the
  command, so their descriptors are empty too). Naming it is right for both: the user learns the device adds nothing,
  and a model with no `white_noise` loses nothing by being named.

Gate: `tests/Engine.Tests/External/OsdiNoiseTests.cs` (the worker and `fake_osdi`'s `crf_noise`, skipped when not built):
refs.json's `osdi_crf_noise` points (g 1 and 4 mS, Vdc 0.5 and 1 V, T 300 and 350 K), NF to 4e-15 dB and every Cs entry
to 2e-15 against the closed form; with kf > 0 the flicker source is `kf·|g·v|/f`; m = 2 equals one device of 2g; a
model with no sources (`crf_rc`) contributes nothing and is named.


## S-parameter noise: the cubes named as Qucs and ngspice name them, and the linear and temperature forms (2026-11-09)

A two-port noise run now adds `NF`, `Fn`, `NFmin`, `Fmin`, `Te`, `Tmin`, `Rn`, `Sopt` and `Zopt` beside `Cs`
(`SParameterNoise.AddCubes`). `GammaOpt` became `Sopt`, ngspice's `SOpt` and Qucs's `Sopt`; `DataSetBuilder.ToSnp` reads
the Touchstone noise block from `NFmin`, `Sopt` and `Rn`.

- **The linear noise factor is `Fn`, not Qucs's `F`.** `SP1.F("probe")` is already the WSProbe's return difference
  (`WspCubePacker` writes `F:<label>` into the same analysis group), and `EvalQualifiedAccessor` sends every
  one-argument `SP1.F(…)` there, so a cube named `F` could not be indexed that way and `SP1.F` would mean two things.
- `Fn` and `Te` come from the same `NoiseFactorAtZ0` double as `NF`, not back from dB; `Fmin` and `Tmin` from `FromWaves`'
  Fmin. T0 is 290 K, so a matched pad at T gives Te = T·(L − 1) whatever T is.
- `Zopt = R·(1 + Sopt)/(1 − Sopt)` on R = Re Z0 of port 1, the reference Sopt is on.
- Still two-port only: an N-port gets `Cs`, and its per-port figures come from `nf`/`te` with a port.

Gates: `SParamNoiseTests` — `MatchedPad_MeetsTheClosedForm` (Fn, Te = Tmin = T(L − 1) at 290 and 400 K),
`LinearAndTemperatureCubes_AndZopt_FollowFromTheNoiseParameters` (the three ngspice benches; Zopt against their SOpt),
and the three-port splitters of refs.json (`ThreePort_NfAndTeToEachOutputPort_MatchTheReference`).

## S-parameter noise: each part's share, Noise=no on a part, Temp on an SnP, a resistive Z_Port (2026-11-09)

Brief NZ-1. Design note: `docs/design/sparameter-noise.md` §1.1.

- **The shares are the very terms `Cs` already summed.** `SParameterNoise.Waves` keeps each entry's `D·H·C·Hᴴ·Dᴴ`
  as it adds it, so Σ NCs equals `Cs` bit for bit (measured 0 on all four gate benches) and Σ NFc equals F − 1 to
  ~4e-16 relative: the only difference is the division by kT0·|S21|² done per share rather than once. The source order
  is unchanged, so `Cs` and every PR #50 cube are the same bits as before.
- **One classification for the run and for `explain`.** `SParameterNoise.Classify` decides every part's model once;
  `Sources` builds the noise from it and `Table` reports it. A part noiseless by definition is its own entry and is
  never grouped, so the order of the noisy entries — and with it the summation order of `Cs` — is what it was.
- **`Noise=` is taken off the instance at elaboration, not passed as a parameter.** An external device's provider
  rejects a parameter name its model does not declare, so leaving it in the dictionary would have refused every
  OSDI part with the switch. A kit netlist's dialect boolean arrives quoted (`Noise="no"`) and is read the same way.
- **`Z_Port`'s activity is now per instance.** Its type-level answer stays `ActiveExact`; `ActivityFor` measures σ_max
  of its S at 50 Ω over the run's frequencies, which is ≤ 1 exactly when Re Z ⪰ 0. NDF reads the same answer, and for
  a passive Z_Port its passivated stamp is its ordinary one, so nothing NDF computes moves.
- **No noisy part at all** (every resistor of a pad switched off) gives `Cs` exactly zero and NF exactly 0 dB, and no
  part axis: `NCs`, `NFc` and `NFpct` are not written rather than written with a zero-length axis.

Gates: `tests/Engine.Tests/Linear/NoiseContributionTests.cs` (sums on mixed temperatures, a coupled group, a VCCS and
the Friis cascade; the π pad's closed-form split at 290 K and 400 K; Noise=no on every and on one resistor; Te =
T(L − 1) of a passive .s2p at 20 K and 400 K through its Temp=; a resistive Z_Port equal to the resistor; the table),
`OsdiNoiseTests.Table_NamesTheDeviceSources_AndNoiseNoQuietsTheDevice`, `tests/Ui.Tests/Cli/NoiseExplainCliTests.cs`
(the explain rows and the check INFO, as a process) and `tests/Ui.Tests/PartNoiseSwitchTests.cs` (the editor's box).

## Harmonic-balance noise: the adjoint on the conversion matrix, and two things it showed (2026-11-09)

Brief NZ-5; design note `docs/design/hb-noise.md`. Noise runs on `HbSmallSignal.BuildJss` with one transposed sparse solve
at the output, one transposed dense solve of `J_ss`, and one transposed sparse solve per sideband (`HbNoise.Add`).

- **An S-parameter NF is only as converged as its DC pre-pass.** `NonlinearDcEngine` declares convergence at
  ‖F‖ < 1e-6 A (`NonlinearAbsTol`). On a BJT biased through 150 kΩ that leaves the base ~2.5e-7 V short of the converged
  point, which is 1e-5 of gm and 9e-6 dB of NF — ten times the HB-vs-S-parameter gate's 1e-6 dB. Nothing is wrong in either
  noise path: with the reference's DC pre-pass at 1e-14 A the two agree to 4e-10 dB. A comparison of an S-parameter
  result against anything at that precision has to tighten the reference's DC tolerance; the default was not changed,
  since that would move every nonlinear S-parameter result.
- **A cyclostationary source cannot be attributed to a sideband.** The share of output noise per input sideband splits a
  stationary source exactly (its sidebands are uncorrelated), but a device switched by its own waveform has large
  cross-sideband terms that cancel: a diode pumped on and off by the LO showed ±20 % of the output noise per sideband and
  2 % net. `NFsb` therefore carries the stationary sources only and sums to 100 % less the devices' share, which `NFc`
  carries.
- **A one-tone small-signal limit floors above Tol = 1e-12.** The LNA bench reaches ‖F‖ ≈ 1.5e-10 and stops at MaxIter;
  1e-9 converges and is already below what the gate resolves.

Gates: `tests/Engine.Tests/HarmonicBalance/HbNoiseTests.cs`, `HbNoiseExternalTests.cs`,
`OsdiNoiseTests.NoiseBatch_EqualsNoise_StateByState`; the dialog, `tests/Ui.Tests/HbSmallSignalDialogTests.cs`.

## Envelope noise: thermal noise and a phase-noise mask on any source (2026-11-09)

Brief NZ-7. `Noise=yes`, `Seed=` and `SeedPerPoint=` on `type=env`; `PhaseNoise=` (and `PhaseNoiseFloor=`) on P1Tone,
PnTone, the tone sources and MOD_Source. Code: `Envelope/EnvelopeNoise.cs` (injection), `RfCore/Signals/NoiseDraw.cs`
(the draws), `RfCore/Signals/PhaseNoiseMask.cs` (the mask and its phase record). Gate: `EnvelopeNoiseTests`.

- **Scaling.** A part's single-sided correlation C at a band's centre becomes, per step, `sqrt(2/h)·L·z` (C = L·Lᴴ,
  E|z|² = 1) on a product at f > 0 — a complex envelope of two-sided density 2C, which is a real signal of single-sided
  density C. The baseband product is real: `sqrt(1/(2h))·L·g` with Re C = L·Lᵀ, read at fs/4, the middle of 0..fs/2.
  Measured: a matched pad at T0 reads k·T0 to 0.3 % (standard error 0.8 %), an Amp with NF=3 and 20 dB reads
  k·T0·F·G to 0.1 %, and the run's own PSD cube agrees within 0.03 dB.
- **The parts' noise is built before the run replaces anything** — before a line becomes its delay and an S-parameter
  block its rational fit — so each comes from the part's own stamp, through `SParameterNoise.Sources` (the one seam).
  Devices with their own sources are claimed out of it and named in one warning ("noise not modelled in envelope").
- **Sources and ports stay noiseless by default**, as in the S-parameter noise analysis. `Noise=yes` on a P1Tone,
  PnTone or MOD_Source (the parameter dialog's Noise box, unticked by default on these) makes its impedance thermal in
  an envelope run: 4kT·Re(1/Z) across its terminals at the source's `Temp`, else the ambient, with a P1Tone's or
  PnTone's Z[n] for the band, n = round-half-up(f / fc), as they assign it. Injected across (ext, ref) rather than
  (ext, drive): the ideal drive source ties the two, so the current is the same. The S-parameter, HB and loadpull
  noise analyses keep it noiseless — their NF already counts the source termination at T0 — and `check` says so as
  INFO. Measured: a 50 Ω MOD_Source or P1Tone at T0 into the matched pad reads 1.007 and 0.998 k·T0; without the key
  the pad's own 0.901 (expected 0.9). Gate 1 still builds its source from a tiny Z and an explicit resistor.
- **Every draw is keyed by (seed, part path, product, ABSOLUTE sample).** Two segments with a warm-up draw what one run
  draws, and on a memoryless circuit the stitched record is bit-identical to the sequential one from sample 1 on.
  Sample 0 differs: a cold run records its noiseless harmonic-balance start there, where the split run is already a
  warm-up into its record.
- **The draws are platform-independent bits.** The Gaussian (a radius from one uniform, an angle from another) is written with its own logarithm, sine and cosine
  (polynomials on +, −, ×, ÷ and the IEEE-exact square root), so no platform maths library is in the path; a test pins
  six draws' bit patterns. The phase record goes through the managed FFT, whose twiddles do use the maths library, so the
  phase is as reproducible across platforms as the rest of an envelope run, not bit-pinned.
- **A CW source's phase noise** is its static RHS split out per product (the RHS less the RHS at DriveScale 0) and added
  back each step as (exp(j·n·φ) − 1) times its own solution, n = |f| / the source's lowest tone frequency. φ = 0 adds
  exactly nothing. The harmonic-balance start is at φ = 0 while step 1 reads φ(i0+1), so a circuit with memory sees a
  small phase step at the start; the settle skip / warm-up covers it, and a memoryless one sees none.
- **Lattice products at a negative frequency** are stepped in the frame of |w| (the factorization is shared), so their
  noise is C at |f|, not its conjugate.
- **Gate results.** The mask comes back over 16 seeds to 0.41 dB worst bin (mean −0.005 dB) from 20 kHz to 0.4·fs;
  reciprocal mixing through an ideal multiplier reads −53.53 dBc in a 18.75 kHz channel 198.75 kHz from the blocker,
  against −53.52 dBc closed form. With no key, a CLI run of a bench with an SDD, a CW P1Tone and two segments wrote the
  same bytes before and after the change, but for the 7-byte run-time scalar that differs between two runs anyway.
- **Trap for anyone writing a CW envelope bench:** a `CW` carrier with no `BW=` takes the 20 MHz default, so the run
  steps at the 20 MHz rate (122.88 MHz times the oversampling). The first version of these tests took ~10 minutes at a
  million steps per run; stating `BW=0.2MHz` brings a 2 ms record to 3,840 draft steps.

## Envelope EVM, its split into distortion and noise, the SNR and the constellation (2026-11-09)

Brief NZ-8. EVM_pct / EVM_dB / EVMpeak_pct / MER_dB [branch, carrier], Const [branch, carrier, symbol] and ConstRef
[carrier, symbol] at every IProbe for every LTE / NR carrier; with `EvmSplit=yes` (and noise or a mask) EVMdist_pct,
EVMnoise_pct and SNR_dB. Code: `Envelope/EnvelopeDataSet.AddEvm`, the second run in `EnvelopeRunner.Execute`, the
demodulator in `RfCore/Signals/SignalEvm` (`Demodulate`, `Evaluate`, `SignalToNoise`; its own entry in
`src/RfCore/RESOLVED.md`). Gates: `EnvelopeSignalQualityTests`, `SignalEvmTests.E6-E8`, `SignalQualityPresetTests`.

- **Gate 1 is 1e-5 pp, not the brief's 1e-6.** The pad reads the Signal Designer's EVM to 3.4e-6 pp on a 5 % clipped
  record and 9.4e-6 pp on a 0.25 % shaped one. The residual does not scale with EVM: the demodulated points differ from
  the native record's by 6.9e-8 rms at 1x and 4x oversampling alike, which is the drive itself — `CompositeWindow`'s
  windowed-sinc interpolation of the record to the envelope rate — and it moves EVM by up to that × 100 pp through its
  correlation with the error vector. The demodulator is exact: the same record resampled exactly
  (`Fft.ResamplePeriodic`) reads `SignalEvm.Measure` to 1e-9 pp (`SignalEvmTests.E6`). Tightening the drive's
  interpolation would change every envelope result's bits, so it was not done.
- **EvmSplit's noiseless run takes a Newton update every step (`EnvelopeParams.MinNewton = 1`).** The envelope Newton
  accepts its quadratic predictor whenever the residual is already under `Tol` (1e-6 A by default). The noisy run never
  meets that — the noise is not predictable — so it is solved to well inside Tol; the noiseless one accepts the
  predictor on about a third of the steps. On an amplifier at -37 dBm output (probe current ~9e-5 A, so Tol is ~1 % of
  it) that left the noiseless run 1-2 % off: it read 3.463 % "distortion" and NMSE -32.4 dB where the amplifier,
  far below compression, passes the clipped record on at 3.228 %, and the split came out as no noise at all. With one
  update forced the split reads 3.228 % and 0.914 % at the default Tol, the same as at Tol=1e-12. The first run is left
  exactly as asked. The same looseness was in every noiseless envelope run of a small signal; fixed below (the run tolerance).
- **The CLI `env` verb dropped `Noise`, `Seed` and `SeedPerPoint`** (since NZ-7): it rebuilds the analysis to apply its
  overrides and copied every field but those, so `Noise=yes` ran noiseless headlessly with nothing said. It copies them
  now, with `EvmSplit` (`EnvelopeExportCliTests.TheNoiseKeys_ReachTheRun`).
- **The quiet run reuses the run's netlists**, after the first, with the same steps and segments. Fresh runs of each
  half read the same numbers as the split, so nothing carries over between them.
- **DBPSK, FSK and CSS are measured too** (the owner's follow-up): DBPSK into the EVM cubes and the constellation,
  FSK into FSKerr_pct / FreqDev_Hz / FreqOffset_Hz with FreqPts / FreqRef per bit, CSS into CSSerr_pct, each with its
  own Dist / Noise split and the shared SNR_dB (`SignalQuality`, its entry in `src/RfCore/RESOLVED.md`). Each family is
  written only when one of its carriers is there, NaN for the others. Gates:
  `EnvelopeSignalQualityTests.TheIotCarriers_ReadZeroThroughAPad_AndTheirNoiseAsTheSnrSays`.
- **No carrier with symbols, no cubes**: the gate-5 rule. A run with one writes NaN for any other carrier and says once
  which and why (CW, pulse, imported I/Q).

## The envelope's tolerance follows its signal (2026-11-09)

Both found while building NZ-8, both one cause: `Tol` is absolute (1e-6 A at the nonlinear interface).

- **A small signal was solved to about a percent of itself.** The step's predictor is accepted whenever the residual is
  already under Tol, which at a -37 dBm output is ~1 % of the current. A noiseless amplifier far below compression read
  NMSE -32.4 dB, ACPR -44 dB and EVM 3.463 % where the clipped record alone is 3.228 %: spectral regrowth that was
  solver error. Now the run uses `EnvelopeEngine.RunTolerance`: Tol, or 1e-4 of the RF excitation at the record's RMS
  drive when that is smaller (both engines, the start and every step). The same bench reads NMSE -112 dB, ACPR -87 dB,
  EVM 3.228 %. **A run whose excitation is 10 mA or more keeps Tol exactly**: the NR20 ACPR and the 400 MHz two-carrier
  examples are bit-identical to before, but for the run-time scalar. The RMS, not the drive at t = 0, because that may be
  zero.
- **The harmonic-balance start was refused when the drive at t = 0 was below Tol.** `HbNewton.Solve` refuses, at
  iteration 0, a solve whose RF excitation the tolerance cannot constrain (`IsToleranceVacuous`) — right for a drive-up
  ladder, which would otherwise report its previous rung's spectrum; wrong for an envelope start, which IS the drive at
  one instant. A DBPSK record starts on a phase reversal, where the drive is zero (to round-off), and every rung of the
  start's drive ramp was zero too: "the harmonic-balance start point did not converge" through any nonlinear block. Its
  seed is now the start, to within the run tolerance every step is held to. With the run tolerance, an FSK bench at
  -75 dBm from a 1 mΩ source, refused for the same reason, starts on an ordinary solve. The lattice engine's start
  (`HbNewtonNd`) makes no such refusal and only takes the run tolerance.
- `EvmSplit`'s `MinNewton = 1` stays: the noiseless run is then never less converged than the noisy one, whatever the
  tolerance. `explain --analysis` lists the carriers measured and what EvmSplit costs;
  `check` reports EvmSplit with nothing to split as INFO.
- **The phase-noise closed form** (gate 3): EVM² = Σ S_φ(f_m)/T · (1 − f_m/B) over the record's bins, B the occupied
  bandwidth — the share of each subcarrier's spread that lands inside the channel. Over four seeds it reads 3.954 %
  against 3.968 %.

## Brief NZ-9 — the oscillator analysis (`OscEngine`, `OscProbeModel`)

- **The HB engine is untouched.** The auxiliary generator is the OscProbe's own stamp, armed by `OscEngine` with (A, ω, G):
  a Norton source plus G at ω only, idle (open in shunt, a 0 V branch in series) everywhere else. The extractor sees an
  ordinary linear part whose right-hand side changes with A, so its cached factorisations are reused across amplitude
  steps and refactored only when f moves. No file under `HarmonicBalance/` that existed before was edited, so every driven
  HB result is the same bytes (gate 4 by construction).
- **The residual is the circuit's large-signal immittance at the probe, per unit amplitude** — `G(A − V)/V` in shunt,
  `−V/(A + G·V)` in series — not the generator's current. It does not depend on G (G sets only how well posed each driven
  solve is), and at A → 0 it is the small-signal immittance, which the start-up condition says is not zero. That is what
  keeps the trivial DC solution out by construction. G is the start-up immittance's slope scale plus twice its negative
  real part, so it outweighs the negative resistance at every amplitude below the oscillation.
- **A 4K-point grid aliases a cubic at MaxHarm = 1.** With FFTOverSample=1 the grid is 4 points, and cos³ sampled at four
  points IS cos: the third harmonic folds onto the fundamental, and the cubic's describing function reads g3·A³ instead
  of (3/4)·g3·A³ — a 15 % amplitude error that converges cleanly. Gate 1 runs FFTOverSample=4 and matches the closed form
  to 4e-10 (frequency to 2e-16). This is HB's own grid rule, not the oscillator's; the oscillator takes FFTOverSample for it.
- **The inner HB tolerance follows the generator**: min(Tol, 1e-9 × the generator's current). A solve that stops short of
  that but under the analysis's own Tol is accepted, since that is the answer HB itself gives there.
- **How far an oscillation sits from its start-up frequency depends on the loop gain, and can be large.** The gate-2
  Colpitts at a loop gain near 24 (RL = 2 kΩ, 1.7 mA) converged 21 % below its 159 MHz start-up: the transistor saturates
  and is off most of the cycle, so the tank falls to L with C1 in series with C2. At a loop gain near 3 it lands 6.8 %
  below (130.89 MHz against 140.41), above the transistor-off resonance (129.09 MHz), from Fguess ±20 % alike. That
  bracket — start-up frequency above, transistor-off resonance below — is the stated pull.

## Brief NZ-10 — oscillator phase noise (`OscPhaseNoise`)

Design note: `docs/design/hb-noise.md` §10. Findings:

- **HB noise centred on its output frequency cannot see an oscillator's phase mode at low MaxHarm.** NZ-5 builds the
  sidebands `f_out + k·f0`, k = −K…K. Read at `f0 + f_m` with K = 1 that set is `f_m`, `f0 + f_m`, `2f0 + f_m`: the
  carrier's other sideband `f0 − f_m` (k = −2) is missing, the upper and lower sidebands cannot couple, and the noise of
  the held oscillation reads 6 dB low with no error anywhere. Phase noise therefore uses a baseband frame — offset `f_m`,
  sidebands `f_m + k·f0` — which holds both carrier sidebands at any K ≥ 1, and reads the two through two adjoints of the
  same matrix (`HbNoise.Adjoint`, now shared, at sideband ±1 instead of 0). The far-out gate compares against NZ-5 at
  MaxHarm = 3, where both sets hold both sidebands; it agrees to 0.002 dB.
- **The outer residual is not the circuit's immittance, and the difference matters for one number only.** NZ-9's residual
  is the generator's current over the probe's voltage, at a fixed generator amplitude A, so `∂R/∂ln A` carries a factor
  `G/(K + G)` (G the generator's conductance, K the circuit's amplitude stiffness) that the circuit's own immittance does
  not. For the frequency's sensitivity this is harmless: the bordered solve `−M⁻¹·δR` uses R's own derivatives
  consistently, and a hand derivation that took M to be the circuit's immittance gave a wrong amplitude sensitivity by
  exactly that factor. For the amplitude's relaxation rate, which places the join, the circuit's immittance is the one
  meant, so it is recovered from the same four solves by dividing out the probe level's own response to A.
- **The probe's generator must not be a noise source.** Armed, the OscProbe stamps a conductance at `f0` exactly, which
  the thermal classification would give 4kT·G of noise. It is claimed and dropped, and once the static sensitivity is taken the
  probe is disarmed, so a conversion matrix at an offset inside the generator's 1e-9·f0 window (only reachable when there
  is no relaxation rate to join at) is still the autonomous circuit's.
- **A static adjoint at `ω_ss = 0` is well posed in two-sided form.** The ±k sideband unknowns are conjugates there, but the
  complex matrix is the complexification of the real Jacobian, so it is invertible exactly when the held circuit's
  Jacobian is; a slow real current has `u_{−k} = conj u_k`, which turns `Re(conj μ·δR)` into one complex row per sideband.

## Brief NZ-11 — an LO in an envelope run, a receiver read after its mixer, and a Mixer's NF (2026-11-09)

Three gaps the Receiver Noise example's EVM bench found, none visible until a signal went through a mixer.

**A P1Tone LO drove nothing, silently.** An envelope run made fundamentals only from MOD_Source carriers. A P1Tone
drives a product only where the product's frequency equals its `Freq` (`P1ToneModel.Stamp`), so a 2.1 GHz LO beside a
2.45 GHz signal, a single envelope on 2.45 GHz and its harmonics, was a bare 50 Ω load: no conversion, no warning. In a
two-fundamental run every P1Tone was driven at `tones[0]` whatever its own `Freq` said. Now
`EnvelopeEngineMt.Plan(sources, tones, …)` places each P1Tone: on a frequency the carriers already reach (a harmonic of
the one envelope, or a lattice product of two within MaxMixOrder) it adds nothing; anywhere else it is a fundamental of
its own, with no carrier and no signal rate; inside 3× a carrier's bandwidth but off its frequency it is refused
(`envelope.tone.inside-carrier`) — its envelope would overlap the carrier's; past two fundamentals it is refused
(`envelope.tone.too-many-fundamentals`). `Envelope=single` keeps one fundamental and warns about a P1Tone off its
harmonics (`envelope.tone.off-single-envelope`). Every P1Tone is driven at its own `Freq`. A run whose P1Tones all sit
on the carrier's frequency, which is every existing test and example, plans exactly as before.

**A carrier after a mixer was measured at its own product.** EVM, Pavg, Gt, ACLR and PAPR read each carrier at
`CarrierMix[c]`, its tone's fundamental, so the IF probe was `noRf` and blank. `EnvelopeDataSet.CarrierReads` now picks,
per probe and carrier, among the carrier's own product and every product reaching it through an LO tone (a fundamental
with no carrier, or one CW carrier), the one delivering the most power, from the per-product power `ProbePower` already
computes; every per-carrier probe measurement reads that one choice. A product stores the envelope at its signed
frequency Σ k·f, holding the carrier where its tone's k is +1 and its conjugate where −1; `Window`'s own conjugation of
negative-frequency products then decides whether the physical spectrum is mirrored, and a mirrored ACLR swaps lower and
upper. With no LO tone every carrier is at its own product and `Pprobe_fund_W` is summed over the same products in the
same order as before. Gate: `EnvelopeNoiseTests.AfterAMixer_TheCarrierIsMeasuredAtTheIf`, low-side and high-side LO —
EVM and power through an ideal 0 dB mixer equal the RF probe's (0.3570 % both; read unmirrored, a mirrored 64QAM IF
would be near 100 %).

Without an IF filter an ideal mixer's sum and difference products carry equal power, so "the most power" is a tie;
both hold the carrier un-mirrored and measure the same. A receiver bench should filter its IF anyway.

**A Mixer's NF was missing from envelope noise.** `EnvelopeNoise.Thermal` claimed only device rows, so a Mixer fell
through as `Noiseless` and was named in the warning, while HB noise (NZ-5) used its NF. It now takes the same
`HbNoise.MixerSource` (an equivalent source at the RF port sized to F_DSB) in every band. Gate:
`EnvelopeNoiseTests.MixerNf_IsTheNoiseInAnEnvelopeRun` — NF 20 → 30 dB moves the IF SNR by 10.079 dB, the closed form
10·log10((F_DSB,30 − 1)/(F_DSB,20 − 1)) to three decimals.

## NZ series review (2026-11-09)

**Close-in phase noise read the baseband at DC.** `OscPhaseNoise.Sensitivities` built the held circuit's conversion
matrix at ω_ss = 0, where every capacitor is open, so a bias or tuning RC whose corner lay below the offset passed its
noise whole: a 100 kΩ tuning resistor behind 100 nF (16 Hz) read the same at 100 kHz as behind 1 pF, ~4 dB high, and the
join to the conversion matrix disagreed by 4 dB (the warning blamed Tol and MaxHarm). The sideband-0 rows and the
partition the adjoint back-solves there are now taken at f_m, the carrier's sidebands stay static, and the residual's
envelope is read from two adjoints (the probe's +ω0 and −ω0 sidebands), which reduces to the static form at f_m = 0
(identical to the last digit with nothing slow at baseband). The receiver example's VCO moved +2.9 dB at 100 kHz: its base
node (R1‖R2, Cb = 1 nF, 68 kHz) no longer looks open to Q1's correlated noise above the corner, and its AN, which jumped
13.7 dB at the join, is continuous. AN is now join-checked too. Gate: `OscPhaseNoiseTests.CloseIn_BiasNoiseIsFilteredByItsRc_AboveTheCorner`
(−20.0 dB/decade below a 370 Hz corner, −40.0 above).

**The envelope's relative tolerance had no floor.** `RunTolerance` took min(Tol, 1e-4 × excitation); behind ideal chokes
the 1 µΩ regularisation makes a ~3e6 A Norton current per 3 V, and the residual cannot fall below a machine epsilon of it
(~7e-10 A), so any signal under ~−75 dBm into a biased transistor failed to start. The floor is one epsilon of the
largest Norton current (measured converging at under 0.3 of it), and the run warns (`envelope.tol.roundoff`) once the
floor holds the solve above 1 % of the excitation: EVM 0.363 % at −90 dBm, 0.373 % at −100, 0.71 % at −110 with the warning.

**Smaller ones.** A FET with Ri and neither Td nor Kf had a noiseless drain and no warning (now named, "(its drain)");
`Noise=no` on a ComposedFET was refused (it now quiets the core and every network element); coupled inductors inside a
composed FET took the ambient, not the device temperature; a flicker source at 0 Hz NaN'd the point silently (warned);
NFpct is NaN, not round-off over round-off, when F − 1 is below 1e-12; HB noise on an unconverged solve warns; a NoiseIn/
NoiseOut naming no port or Tuner is refused before the first grid point (and by `check`/`explain`); `PnStart="1 mHz"`
read as a megahertz; a stated `Aguess` that does not solve at start-up falls back to the ramp.

**A swept oscillator stopped at its first refused point.** A point that does not start, or leaves its search span, is
now NaN (`Converged` 0) with a warning naming its value, as a swept envelope's failed point is, and the curve keeps its
other points; every point refused still stops the run. Gate: `OscillatorAnalysisTests.Sweep_APointThatDoesNotOscillate_IsAGap_NotTheEnd`.

**Z_Port's passivity is the noise analysis's question only.** NZ-1 measured it in `ActivityFor`, which the NDF
passivation also reads, so a run without `Noise=yes` reported a passive Z_Port differently. It is now
`ZPortModel.IsPassiveOver`, asked by `SParameterNoise.IsThermal`; `ActivityFor` and the passivation note are the
type-level ones again. `OscProbeModel` also joins the NDF passivation table (idle: a short or an open), which NZ-9 left out.

`explain --analysis` lists the noisy parts of an HB or loadpull `Noise=yes` run as it does an S-parameter run's,
classified at the frequencies the run reads (the small-signal sweep; the noise read frequency), and counts a Mixer's NF.

**Found, not changed.** MOSFET channel thermal noise is on by default (GammaN = 2/3), as NZ-4's table says, though its
gate 5 lists only diode/BJT shot and series-R thermal.

## The oscillator start-up search freezes the bias point (suite review, 2026-11-10)

- `OscEngine.Startup` linearises every device at its DC point (`StampLinearized`) and never called `BiasFreeze`, so an
  oscillator built on a composed FET with `LinearC=true` would have met the unfrozen core's refusal. It freezes at the DC
  solution it has just solved, as `SParameterEngine` does. Gate: `BiasFreezeTests.EveryEngineEntryPointThatReadsCharge_Freezes`.

## A global derived from a swept one, read by a measurement (2026-11-12)

- **Root cause.** `MeasurementEvaluator` bound every global to its value in `ElaboratedNetlist.ResolvedGlobals`. Those
  values come from the one elaboration the run started with. Only the swept variables were then overridden with their
  axis cubes. Each sweep point re-elaborates with its own value (`ParametricSweepEngine` swaps the `Variable` in
  `TestBench.GlobalVariables`), so the circuit always followed the sweep: a source set to `y = 2*x` was at `2*x` at
  every point, nested sweeps included. Only the measurement read the nominal `y`.
- **Fix.** `ResolveDerived` finds the globals that depend on a swept one, through other globals and through user
  functions' bodies (`UserFunctionInliner`). It takes each global's references once and walks outward from the swept
  names over who reads them. Each derived global is then **evaluated at each point of the sweeps it depends on,
  exactly as that point's elaboration evaluated it**:
  - the elaborator's global scope: every global with its own expression and unit, and the ambient `temp` when the
    design declares none;
  - the point's values bound by `ParametricSweepEngine.PointVariable`, so units and var-unit-wins are that point's;
  - plain numbers, with an evaluator of its own that has the user functions and the trial's draws.

  The values are stacked into a cube over those sweeps, in the results' axis order. A point that fails leaves the global
  unbound. A measurement reading it fails and names the point, through the `unavailable` map that the `freq` hint now
  shares; it never reads the nominal value.
- **Why per point, not cube arithmetic.** The first version bound the swept variables to their axis cubes and resolved
  the derived globals once, as cubes. That is not what the circuit computes:
  - a conditional, `max`, `limit(x, lo, hi)` and `polar` refuse a cube;
  - `imag(log(50 + j*x))` read 0, because `log` of a cube is ln|z|, while the circuit had atan(x/50);
  - the axis order followed the expression (`z*x` gave `{z, x}`).

  Evaluated per point, the measurement and the circuit agree by construction.
- **Not covered.** A loadpull swept over its tone frequency stacks a `freq` axis instead of the variable's. That variable,
  and anything derived from it, stays a nominal scalar in a measurement, as before.

Gates: `tests/Engine.Tests/Measurements/DerivedVariableMeasurementTests.cs`.

## An envelope EVM floor near 1 %: the draft step against a carrier off the envelope's centre (2026-11-12)

The RF Chain Budget's Superhet Demo read 1.55 % EVM from −56 to −41 dBm, where the budget's SNR predicts 0.34 % to
0.06 %, split into two floors near 1 %: a distortion part of 1.23 %, put down to the LO's phase noise, and a noise part
stopping at 40 dB of SNR, put down to the envelope measurement. Neither was what it was said to be.

**The measurement is exact.** Source → matched pad → load reads the source's own EVM: NR 64-QAM 0.353 % (its spectral
shaping; 0.017 % with `Shaping=off`), DBPSK 0.002 %, the same with the carrier 4.5 MHz off the envelope's centre, and
with `Noise=yes` and EvmSplit 1/√SNR up to 98 dB of SNR.

**The distortion part was the step.** EvmSplit's noiseless run has no noise and no phase-noise mask, so its 1.23 % could
not be the LO. Bisected on the demo at −40.87 dBm, noiseless: the NR source alone through the chain 0.406 %; with the
DBPSK source beside it, even at −200 dBm, 1.225 %. The second carrier, 9 MHz up, makes the group's envelope centre their
midpoint, so the NR carrier sits 4.5 MHz below it and its band reaches 6.75 MHz, which the draft step (64 MS/s) takes at
0.66 rad. Gear-2 steps an envelope frequency f as jω(1 + θ²/3 − 7θ⁴/60) + θ⁴/(4h), θ = 2πfh. The damping σ = θ⁴/(4h)
dominates: through a filter's group delay τ it scales the gain by e^(−στ), and since σ grows as f⁴ that is a tilt across
a carrier off the centre; the warp's f³ phase is the smaller part. Neither is a delay or a gain, so the demodulator's
fit cannot take them out. On the bench below at the old step, with the NR carrier 4.5 MHz off, the closed form gives
1.02 % with both, 0.95 % from the damping alone and 0.35 % from the warp alone. The smallest reproduction is a 60 MHz
third-order Butterworth bandpass between 50 Ω terminations: the NR carrier 4.5 MHz off the centre reads 1.108 % against
0.379 % centred, an excess of 1.05 %, where the closed form through that filter, delay and gain fitted, gives 1.06 %.
With the source's Fc moved 4.5 MHz up instead, so that only the envelope's centre moves, the carrier reads 1.066 %,
0.379 % and 0.343 % at h, h/2 and h/4 (`standard`): excesses of 1.01 %, 0.16 % and ~0 over the last, against 1.02 %,
0.17 % and 0.03 % from the closed form. DBPSK 9 MHz off the centre read 0.829 % (centred 0.006 %).

The signal rate, 2(|offset| + 2.5 BW) per carrier, holds an off-centre carrier's spectrum, and the presets multiplied
it, so an off-centre carrier's band edge got fewer steps a cycle than a centred one's. `EnvelopeEngine.Oversample`
raises the preset until every carrier's band edge, |offset| + BW/2, gets 10 steps a cycle, what a centred carrier gets
at draft (the rate is at least five bandwidths). A run whose carriers are all centred keeps its step bit for bit, `fine`
never changes, and `standard` changes only for a carrier more than 7.5 bandwidths off the centre; the most it raises any
run is 5x. The runner's `StepSeconds` and both engines call it, the lattice plan carrying each group's band edge, and a
raised step is said in one note and in the oversampling the CLI and the run's summary print. The demo's step halves (one
point took 2.0 to 2.3 times as long); its distortion part reads 0.39 %, nearly all the source's own;
the filter bench's off-centre NR carrier 0.390 % and DBPSK 0.036 %. The Canceller bench, whose 20 MHz interference
carrier sits 20 MHz off its centre, now reads its closed-form depth to 0.64 % (0.09 dB at 49.7 dB deep) instead of
1.34 % (0.67 dB there). Gates: `EnvelopeSignalQualityTests.ACarrierOffTheEnvelopesCentre_ReadsCloseToItsCentredEvm` and
`ARaisedStep_IsPrintedAndNoted`.

**The rule bounds the step at the band edge, not the EVM.** The demo and the gate are clean because the ceiling
overshoots there (ceil(1.14) = 2 gives 17.6 steps a cycle). What is left grows with the offset and with the filter's
delay. The NR carrier centred in the bandpass above, only the envelope's centre moved: at draft 0.388 % centred, 0.691 %
3.5 MHz off (10.2 steps a cycle, so the rule does not act), 1.172 % at 15 MHz and 1.506 % at 22 MHz; at `standard`
0.602 % at 15 MHz and 0.894 % at 18 MHz, where `fine` reads 0.36 %. The closed form matches every point to ±0.01 %. A
fixed count of 20 would cost up to 7x at draft and still suit no filter in general, so the count stays 10 and the
reference says to read EVM through a filter at `standard` or `fine`, or with Fc on the carrier. Stronger fixes, none
taken here: centring the envelope on a single-carrier group, a less dissipative integrator, or a count set from the
circuit's delay.

**The noise part is the LO's phase noise.** The noisy run carries the LO's mask, the noiseless one does not. The mask's
closed form, EVM² = Σ 2·10^(L(f)/10)/T·(1 − f/B) over the record's bins, is −38.5 dBc, 1.18 %; one seed reads 0.96 %.
Without the mask the demo's SNR at −40.87 dBm is 63.8 dB against the budget's 64.0 dB. The Canceller bench's 41 dB,
taken for the same floor, is its own thermal SNR at −60 dBm: it reads 61 dB at −40 dBm.

**Found, not changed.** At draft a centred carrier still reads Gear-2's error through a narrow filter (0.379 % against
0.343 % at `standard` on the bandpass above); the presets keep their meaning for it. A single envelope's IMD products
far from Fc, which move with the centre (above), meet the same error; the rule resolves the carriers, not their
products.

## Weighted terms (`w ≥ 2`) in the multi-tone solvers and both envelope engines (HB-TERMS, 2026-11-13)

A device's `NonlinearResult.Terms` — a current whose spectrum the device's own `H[w](ω)` multiplies before it joins the
node — were honoured by the single-tone `HbNewton` only. Two producers exist: an SDD with `I[p,w]`, `w ≥ 2`, and a
system block with PIM on whose S is complex (the 90° hybrid), whose `Im(Y)` and `Im(N)` ride in `H[2] = j·sign(ω)`.
For the ideal hybrid `Re(Y) = 0`, so a solver that drops the bucket sees four open circuits. Nothing refused or warned.

**Where each path lost it, and the three different shapes of the same bug:**

- `HbNewton2D` and `HbNewtonNd` never read `Terms`: residual, Jacobian, the reported total injection (and so the
  linear back-solve) and the device terminal currents all lacked the bucket. Two tones, 100/110 MHz at 30 dBm into
  the hybrid: the through port at −289 dBm and the source node at 36.02 dBm (open-circuit doubling), against 26.99 dBm
  and 30.00 dBm single-tone.
- `EnvelopeEngineMt` reaches devices only through `HbNewtonNd.EvaluateNonlinearNd`, so it inherited the drop — same
  numbers.
- `EnvelopeEngine` (single envelope) **did** hand the buckets to `HbNewton.BuildF`, so Newton balanced the interface
  correctly — and then updated the linear state, which every probe, IProbe and the next step's history read, from an
  injection without them. A correct solve reported the hybrid as open: through port −125 dBm. Handing a term to the
  residual is not enough when a second consumer reads the injection; both envelope engines now add the bucket to the
  step's injection itself and pass it separately only to the Jacobian.
- `HbSmallSignal.LatticeSidebands` declared `Buckets => 0` on the grounds that `HbNewtonNd` had none either. The
  premise went stale with this fix; it now carries the bucket spectra from `LatticeSpectra` (the WSProbe sweep and HB
  noise around a two-tone point).

**`H[w]` at a negative mixing frequency is `conj(H[w](|ω|))`** (`HbNewton.WeightAt`), never a model call at a negative
argument — the rule `Y_NN` and `HbSmallSignal.ToneSidebands.BucketWeight` already follow. Single tone never meets a
negative frequency, which is why the single-tone code calls `Weight` directly.

**Measured agreement with single-tone HB** (`MultiToneWeightedTermsTests`, tones on multiples of 10 MHz so every product
is a harmonic): two tones 5e-9 on the lattice and 4e-12 on the FFT path; envelopes 2e-6 at the carriers and 7e-6 at the
products (one drive-level offset at the first and third power: the envelope's own tolerance); three tones at
`MaxMixOrder = 3` up to 7e-4, which is the fifth-order content the lattice drops and the reference keeps on the same
frequencies (`3f1 − 2f2 = 2f2 − f3`). An SDD under a low-pass `H[2]` at 0 dBm: 6e-5, again truncation.

**The FD Jacobian oracle's relative metric is floored here.** At mixing order 2 it reads 1.7e-5 (lattice) / 1.5e-5
(FFT path) for the hybrid on near-zero entries, and 2.1e-4 / 2.1e-5 for an in-phase coupler that has no bucket at all;
the worst ABSOLUTE error against the Jacobian's scale (4e-10–6e-10) is the meaningful number, since a missing or
misrotated bucket is an error of the scale itself.

**Now visible, not new:** the quadrature hybrid's PIM product also leaves the isolated port, about 3 dB under the
through port. Single-tone HB has always shown the same; it is the overlay's memoryless projection of the incident
wave for a complex `T` (`src/Core/RESOLVED.md`), not the solver.

**Blast radius, checked.** The only shipped examples with a weighted-term candidate in a multi-tone run are the three
System Design transmitters (`TxDirectConversion`, `TxFetFinal`, `TxSuperhet`), whose PIM sits on a circulator with a
real S — no bucket. Their HB results are bit-identical before and after. No example has an SDD with `w ≥ 2` or a PIM
coupler.

## Envelope input power on the lattice, and a Stop too short to measure (issue 66, 2026-11-13)

- **Pin_avg 3.01 dB high for two carriers of one source.** `EnvelopeEngineMt` summed one source's drive over its tone
  groups into one phasor before recording it, and `EnvelopeDataSet` averaged `|Σ|²`. The groups are different lattice
  products (100 and 110 MHz), so their powers add: two equal CW carriers read `|2a|²` against `2|a|²`, and Gt read
  −3.01 dB through a wire. `Drive` now has one row per (source, tone group), `s·T + t`, each with its own source's
  `DriveRe`, and Pin_avg sums each row's mean `|d|²/(8 Re Z)`. The single envelope keeps one row per source, so a
  one-source run is unchanged. AM-AM (single envelope only) still reads the rows summed, on the first source's Z.
- **A Stop shorter than 15 steps crashed** with an `IndexOutOfRangeException`: `t0 = Min(nt − 16, skip)` went negative
  and `Window` read before the record. The runner now refuses it before solving, naming the step and the shortest Stop
  that measures (`EnvelopeDataSet.MinimumSamples`, 16 samples), and `t0` is clamped at 0.

Gates: `EnvelopeShortStopAndInputPowerTests`.

## A net only nonlinear devices touch was recorded with Newton's residual on it (issue 66, 2026-11-13)

- **Cause.** Both envelopes recorded a labelled net from the linear state after the step's update,
  `x = x_oc − Z·injection`. Where only device ports hold a node (two `Amp` outputs feeding a third's input), the
  driving-point impedance there is huge, so the injection's residual, within `Tol` in amps, became a large voltage
  error. Nothing downstream read it; the seam check compared it between segments: EnvSeamError 20.6 and a "segments
  disagree" note, against 0.0025 unlabelled, every output identical.
- **Fix.** A recorded node that no linear element touches is read from the step's accepted Newton unknown `V`
  (`EnvelopeEngine.DeviceOnlyInterfaceIndexOf`, both engines). With `n2` labelled the seam error is 0.0025, the
  unlabelled value. `V` was not taken at every interface node: where a linear element also holds the node, `V` and the
  linear state differ by about 1e-5 relative, and the IProbe's voltage there comes from the linear state, so a node
  record and a probe record of one net would disagree (the AM-AM gates in `EnvelopeProbesRetiredTests` saw it). Every
  run with no device-only labelled net is unchanged bit for bit.

Gate: `EnvelopeDeviceOnlyNetTests` (2 s; 36.4 against 0.002 before the fix).

## Multi-tone HB: a source drove every product on its frequency; harmonically related tones (issue 65, 2026-11-13)

- **The defect.** Both multi-tone paths extracted each product's Norton source at the product's frequency, so every
  product landing on a source's frequency was driven by it: with tones 100 and 300 MHz, `(3,0)` and `(0,1)` both carried
  the 300 MHz source, `(2,−1)` and `(1,0)` the 100 MHz one; at 100/200 MHz `(2,−1)` sits on 0 Hz and took the bias. The
  lattice and the FFT path then disagreed by tens of percent on IM3 and by 1e3 on higher bins, and an odd PIM read
  even-order products. Equally spaced tones had it too: in `hero5_3tone.cnl` (1.99/2.00/2.01 GHz) `(0,2,−1)` and
  `(−1,2,0)` sit on the outer tones and were driven, which is why the undamped loop failed from 17 dBm there; with the fix
  every level to 34 dBm converges and the line search first refuses a step at 24 dBm (`HbLineSearchTests` repointed).
- **Fix 1.** `HbEngine.SourceDrivenProducts`: each frequency's excitation goes to its lowest-order product only, in
  extraction and in the linear back-solve. Coinciding products stay independent unknowns, as designed (§6.5).
- **Fix 2.** Tones that are low harmonics of their common fundamental (each within `MaxHarm` of it) run as single-tone
  HB on it and are read onto the mixing indices, with the warning `hb.tones.commensurate`. Exact for such a set, where
  the products add coherently. A first version took every coincidence there; closely spaced tones then became
  single-tone runs at hundreds of harmonics (2.00/2.01/2.02 GHz on 10 MHz) and five multi-tone tests hung or broke, so
  the owner narrowed it.
- **The single-tone run must keep the lattice's termination bands.** A source's or load tuner's `Z[k]`/`G[k]` band is
  `round(f / f_c)`, and the lattice measures it from the MEAN of the tones; the first version let the single-tone run use
  its own fundamental, so at 100/300 MHz the 300 MHz tone sat in band 3 instead of band 2 (and |Vs|, which is computed
  from that impedance, moved with it). With `Z[2] = 10 Ω` the input read 0.316 V at 300 MHz and 0.236 V at 300.001 MHz.
  `RunCommensurate` now hands the single-tone run the multi-tone ruler and the multi-tone default drive (a P1Tone with
  no `Freq` drives at the first tone). A source with one flat `Z` never noticed, which is why the first gate passed.
- **Seed chaining.** A commensurate point returns its single-tone interface seed, marked in a weak table so the next
  commensurate point warm-starts from it and a lattice point never reads it as mixing products when its length happens
  to equal M. Without it a swept commensurate run cold-started every point.
- **Which sets take the single-tone run** is keyed on `MaxHarm`: every tone at most `MaxHarm` times the common
  fundamental. The lattice itself never reads `MaxHarm` (its only knob is `MaxMixOrder`), so this is the one place it
  matters on a multi-tone analysis; the user page says so. A sweep across tone frequency therefore mixes locked points
  (exactly commensurate) with independent ones, which is the physics of an exact ratio rather than a defect.
- **Loadpull settings (§3 of the issue):** measured in `src/Engine/Loadpull/RESOLVED.md`.

Gates: `CommensurateTonesTests`, `LoadpullSettingsReachTests`, `HbLineSearchTests`.

## brief-paa-2 — the array engine, `src/Engine/Array` (2026-11-14)

- **The namespace is `CircuitRF.Engine.AntennaArray`, not `…Array`.** A namespace named `Array` under `CircuitRF.Engine` would
  capture every `Array.Empty` / `Array.Copy` written in any `CircuitRF.Engine.*` file (name lookup reaches the enclosing namespace's
  members before `System`), and the same for a test namespace under `CircuitRF.Engine.Tests`. The folder keeps the brief's name;
  the tests live in `tests/Engine.Tests/AntennaArray`.
- **Phase tables, not exponentials.** `exp(j·k·(x·u + y·v))` is an x factor times a y factor; a triangular lattice's half-pitch row
  shift folds into the row table (`Y[d,r] = exp(j·k·(y_r·v + s_r·u))`), so one table pair serves both lattice kinds and the sum is
  exact, not a re-gridding. The tables are kept per direction set (`ConditionalWeakTable`), so they die with the set; `TablesBuilt`
  counts builds and a second sum with new weights adds none (gate 6). A separable weighting is detected by a rank-one check at
  1e-13 of the largest weight — a steer written as one exponential per element differs from its own row × column product by the
  rounding of a ~100 rad phase, ~2e-14, so 1e-14 rejected the case it exists for.
- **Measured against independent sums** (`ArrayEngineTests`): the 1×8 line against the Steering bench's closed form, and the
  32×32 / 64×64 row-column path, general, separable and with elements off, the triangular lattice and the (u,v) FFT (rectangular,
  triangular, one row) against one-exponential-per-element direct sums — every one inside 1e-12 of Σ|w|.
- **The (u,v) map's bins are aliased by construction**, period λ/dx in u: that IS the grating-lobe structure, so no unwrapping is
  done. A triangular lattice is its even-row and odd-row sub-lattices (row pitch 2·dy) on the same bins, the odd rows' shift an
  exact phase per bin; a sub-lattice a row short is referred to where its missing row would be, which `RowY` extends linearly.
- **The local evaluation** (`ArrayBeam`): step = min(estimate, measured)/10 with the estimate 0.886·λ/L of the LARGEST side (the
  narrowest beam the lattice can make, so the step is never too coarse before the cut confirms it); a tangent-plane patch climbs to
  the peak and re-centres when the maximum lands on its edge; the two cuts are great circles through the peak along θ̂ and φ̂; each
  sidelobe is re-sampled ten times finer, and every peak is read by a three-point parabola. Gate 7 (64 × 64 of E5's centre pattern):
  against a 0.05° direct sum, beamwidths within 0.0010°, sidelobes within 0.001 dB, pointing within 0.002°, at 0° and 45°.
- **Coupling extension** reproduces PAA-1's `extend_S` selection exactly, ties included (first pair in row-major order), because
  gate 3 holds the engine to that harness's numbers. On a triangular lattice an odd row offset is keyed with the start row's parity.
  E5 extended to 64 × 64: at most 81 non-zeros a row; `ArrayWaves.Active` on it allocates O(N), against 268 MB for a dense N×N.
- **Moved (brief-paa-3):** `ArrayLattice`, `ArrayCoupling` and `ArraySparseMatrix` now live in `src/Core/Devices/Arrays`, because the
  PhasedArray component builds its S at elaboration; the pattern sum, directions, element interpolation, beam metrics, (u,v) map and
  waves stay here. `src/Core/RESOLVED.md` § brief-paa-3.

## brief-paa-4 — the beam's noise, and a boresight beam's cut planes (2026-11-14)

- **`SParameterEngine.BlockFunctionalNoise`** is the noise of y = Σ v_k·a_k over a block's incident waves — what an array radiates in
  one direction — one transposed solve per functional on the terminated network's factorisation. **The block's own noise must be
  taken out of its waves**: its Bosma noise is a Norton current across its pins and part of the block as a terminal sees it, so the
  incident wave is (V + Z0·(I − i_n))/(2√Re Z0). Without the i_n term a lossy antenna's own noise (received from its surroundings,
  emitted at its port) read as noise it radiates: 0.098 dB on a noiseless chain, 0.006 dB behind a 12 dB channel, constant at the
  element node. With it the beam noise figure is the Friis cascade to 1e-15 dB on one element and 7e-8 dB on a column of four.
- **The noise figure at the peak is not always the cascade's**: a uniform split radiates its isolation noise wherever the weights
  are not phased to the peak. The patch element's peak sits 0.16° off boresight in its H-plane, so a 1×4 row reads 6e-6 dB above
  Friis and a 4×1 column 7e-8 dB. That is the beam's, not an error; gate 6 uses the column.
- **`ArrayBeam` cut a boresight beam along whatever azimuth its peak landed on** (`Cuts`): the climb leaves a peak at θ ≈ 0.002°
  with an arbitrary φ, and the "elevation" plane through it can be a diagonal, where a separable taper's sidelobes are far below the
  principal planes' — a 32×32 `taylor(-30,4)` read `SllDb` = −45 dB, then −32.4 dB depending on where the overview's 5° grid
  started the search. A peak within one cut step of boresight is now cut along the requested scan's azimuth, or φ = 0 when none was
  requested, made tangent at the peak. PAA-2's 64×64 reference test cuts its own reference the same way at boresight (it used the
  peak's own azimuth too, and agreed only by the same accident).

## brief-paa-7 — an array's own noise, and receiving through one-way channels (2026-11-14)

- **An antenna's noise is a WAVE source, and it reaches every engine as node currents.** b = S·a + c with c's correlation from the sky
  (`ArrayNoise`, note §5.2) cannot be a Norton current at the pins at size: j = (2/√Z0)·(I + S)⁻¹·c is dense, and I + S is singular for a
  uniform N-way split (tᵀt = 1). Instead each element (and each port of a Feed-mode beamformer) gets a source node holding a 1 S conductance,
  whose voltage enters its wave row with −2 (the row is 2b − 2S·a), so the row reads b − S·a = V_src and a noise current j on that node IS
  the noise wave. S-parameter, HB, envelope and phase noise all read node currents already; nothing in them changed. The incident waves of
  R-paa7-1 use the same node (`ArrayIncidentWaveModel`, a `ToneSourceModelBase`, so every engine treats it as an I_nTone).
- **`NoiseCurrents.Structured`**: the S-parameter noise asks for an array's correlation sparse (`SParameterNoise.Sources(…, structured: true)`)
  and forms each port pair's share as one quadratic form (`ArrayCorrelation.Form`); every other consumer gets it dense, as before. A
  4096-element array's whole noise analysis forms no N×N (`ArrayCorrelation.DenseCopies`).
- **The beam functional's own-noise correction is for a NORTON current at the pins only** (`BlockFunctionalNoise`): its pin map now covers the
  block's pins and not its source nodes, so an array's wave-source noise is read through its terminal currents whole, with nothing taken out.
- **Receive weights, not the transmit pattern.** A receive channel of one-way amplifiers passes nothing from the port to the elements, so a
  fields analysis's transmit waves say nothing about reception. `SParameterEngine.PortReceiveWeights` is one transposed solve at the receive
  port giving Tₙ = ∂b_port/∂c_n on the source nodes; the receive pattern is the array sum with weights T, and the receive metrics
  (`FieldCosimulation.ArrayFieldsMetrics.EvaluateReceive`) are all formed from T, C and the noise analysis's Cs. `SnrDb` equals the power HB
  delivers through such a channel over Cs to 1e-9 dB.
- **A sky integral on the record's grid is aliased past a few wavelengths of offset** — see `src/Core/RESOLVED.md`, § brief-paa-7.

## brief-paa-8 — the identical-channel fast path, and what a slider move costs (2026-11-14)

**Measured on** an Apple M4 (10 cores, 16 GB), .NET 10, one evaluation of a Feed-mode array on E5 (5×5 record, 5.9 GHz, extended
coupling), `Phase=steer(theta0,0,f0)`, one Port at F; the channel is an Amplifier (20 dB), an Attenuator (3 dB) and a shunt 0.3 pF at the
element side, so a wave does return down it. Medians of 2-3 runs; the harness is a scratch console program (not a test, not committed),
which times `Elaborator.Elaborate`, the circuit solve (`SParameterEngine.BlockWaves` before, `PhasedArrayFastPath.TryWaves` after),
`FieldCosimulation.Run` whole (local output, as a drag asks), the closure's factor and back-substitution, `ArrayPattern.Sum` on a 1° and
a 2° upper hemisphere (first sum on a new direction set, and a second with new weights on the same set), `ArrayBeam.Evaluate` (cuts and
peak patch) and `ArrayUv.Map`. Milliseconds.

| N | coupling | channel | elaborate | circuit before → after | fields run before → after | factor (once) | back-sub |
|---|---|---|---|---|---|---|---|
| 64 | on | yes | 1.1 | 3.2 → 0.1 | 6.9 → 3.0 | 1.5 | 0.03 |
| 256 | on | yes | 2.1–5.5 | 19.6 → 0.5 | 42.9 → 2.0 | 5.6 | 0.06 |
| 1024 | on | no | 6.4 | 184.8 → 0.8 | 359.3 → 2.0 | — | — |
| 1024 | on | yes | 11.7–15 | 210.0 → 8.7 | 415.1 → 2.6 | 35.0 | 0.21 |
| 1024 | off | yes | 10–14 | 5.3 → 0.3 | 17.3 → 1.4 | — | — |
| 4096 | on | no | 27–59 | 2080.6 → 2.9 | 3710.6 → 16.3 | — | — |
| 4096 | on | yes | 56–79 | 2332.5 → 6.0 | 4387.0 → 8.2 | 344.3 | 1.52 |
| 4096 | off | no | 25 | 9.1 → 2.5 | 23.8 → 5.6 | — | — |
| 4096 | off | yes | 53–63 | 32.5 → 1.7 | 67.9 → 3.2 | — | — |

Release. Debug is the same to within noise for the circuit (CSparse is a release package either way): 4096 coupled with a channel
2522 ms → 7.5 ms, fields 4833 → 17 ms, factor 360 ms, back-substitution 1.69 ms. The "before" channel had no shunt capacitor; the full solve
with it, timed after (fast path off), is 2324 ms at 4096 and 201 ms at 1024 — the same.

The pattern pieces did not move, being the same code (Release; Debug in brackets), 4096 elements: overview on a new 1° hemisphere 56-73 ms
(175), on the same set with new weights 7-24 ms (48), at 2° 16-20 ms (46) and 1.8-7.9 ms (13); the local evaluation 10-14 ms (31-56); the
(u,v) map 9-11 ms (31). At 1024: 3.6-7.8 ms (25) for the kept 1° overview, 5-8 ms local, 2.2-2.7 ms (u,v).

- **The beamformer isolates, which is the whole fast path.** Its S is [[0, tᵀ],[t, 0]]: no path reaches another, so a wave reflected up
  channel k leaves at F and the element side closes on its own, α = (I − S22·S)⁻¹·S21·t·a_F, β = S·α, Γ_F = Σ t_k(S11·t_k + S12·β_k).
  With no channel S22 = 0 and α = t·a_F with no solve, coupled or not; with Coupling=off S is diagonal and the closure is too; only an
  identical, reflecting channel on a coupled array factorises anything. The circuit outside the array (the Port and whatever feeds F) is
  solved by the ordinary engine with the closed array at F as a one-port of reflection Γ_F (`ArrayFeedLoadModel`), so the drive's
  reflection, the circuit's S and the wave into F are the engine's own; the element waves follow by scaling α̂, β̂ by a_F.
- **The ordering: minimum degree on A + Aᵀ.** At 4096 (309,136 non-zeros, ±4 rows and columns of offsets): natural row-major 2,050,816 in
  L+U, 396 ms; MinimumDegreeAtPlusA 1,693,644, 337 ms, 1.52 ms a solve; MinimumDegreeAtA 2,135,714, 543 ms. At 1024: 250,240 / 227,792 /
  256,174. The fill is 5.4× the matrix; the coupling's pattern is symmetric (reciprocal record), which is what A + Aᵀ assumes.
- **Off keeps the factor.** The kept factor is of I − S22·S with every element's channel in place; an element switched off (an identical
  channel is omitted, the element terminated in Z0) replaces its row by the identity's, a rank-|Off| change applied by Woodbury against the
  kept factor: |Off| back-substitutions and a small dense solve, kept per Off set. Past 256 elements off the closure is factorised for the
  set instead. Gate 2's third case (three off) agrees with the full solve to 1.1e-13 in Γ_act.
- **What counts as "only the weights moved" is two hashes of resolved values**: the array's parameters other than PhaseBits, AttenStep and
  Off (the weights and the receive waves are never in them — the elaborator evaluates those per element) with the record file's stamp; and
  one channel instance's components — types, paths and node names with the channel's own prefix taken off, multiplicities and resolved
  values. A VAR that moves only theta0 changes neither; one that moves the channel's attenuator changes the second.
- **Every fields evaluation of an array solved the whole circuit twice**: once for the block's waves and again for the circuit's S cube
  (`AddCircuitS`). The fast path gives both from the outer circuit; the full path still pays both.
- **`ArrayDirections.OnGrid` built a new direction set per call**, and the phase tables and sampled element patterns are kept against the
  set object, so every evaluation rebuilt the tables for the record's grid (and the beam's overview, and the power balance, each rebuilt
  them again). One set per grid object now (`ConditionalWeakTable`); since a set now lives as long as its grid, its table list keeps the
  last 8 geometries and frequencies (a dx sweep would otherwise grow it without bound).
- **After the fast path the largest cost of a move at 4096 is elaboration** (55-60 ms Release with a channel: 4096 element scopes, the
  extended coupling rebuilt by `PhasedArrayModel.Create`, 4096 channel flattenings), then the pattern pieces. Not attempted here; it is the
  next limit for a 4096-element slider. R-paa8-5's coarse overview during a drag is therefore not done: at 4096 the overview with kept
  tables is 7-24 ms against elaboration's ~56, so a 2° overview would not move the frame.
- **The estimate (R-paa8-4)** scales each element's waves of the last full solve by w_new/w_old and holds everything else (active
  reflections, the drive's reflection, the circuit's S). It is used only for a drag (local output), only when nothing but the weights
  changed since that solve (a hash of every component but the beamformer), and not when an element that carried nothing now carries a
  weight. On a 4×4 array with a per-element channel and coupling it is 3% off the solve at a 15° move; the release solves.

## brief-paa-9 — transient direction sets, and the (u,v) transform (2026-11-14)

- **`ArrayPattern.SumOnce` / `FactorOnce`** sum over a direction set whose phase tables are built for the call and dropped: never
  kept against the set and never counted in `TablesBuilt`. `ArrayBeam.Evaluate` uses them for its search patches, cuts and sidelobe
  refinements. Each of those sets was used once anyway, so its kept tables were dead weight until the set was collected, and every
  readout counted dozens of "builds" that a drag gate (PAA-9 gate 6) could not tell from a real cache miss. Results are unchanged
  (same `ArrayPhaseTables`, same sum).
- **`ArrayBeam.PeakPower`** runs the same two climbs as `Evaluate` and stops before the cuts. It is the scan-loss reference, at a
  fraction of the cost.
- **`ArrayUv.Map` transforms only the rows that hold weights along u** (the padding rows transform to zero), and runs both passes
  in parallel. It is the same transform: `TheUvFft_IsTheDirectSum_AtItsPoints` passes unchanged. At 64 × 64, padded to 512 × 512,
  the first pass does 64 rows instead of 512.
- **`ArrayCut.Field`** carries the field a cut was read from, so a view splits it into co- and cross-polar without summing again.

## brief-pch-4 — the ideal Delay block is stepped as its delay in an envelope run (2026-11-15)

**The Delay block was fitted, not delayed.** `DelayModel` is an ideal S-block, so an envelope run sent it through
`EnvelopeSnpFit`: a rational fit of e^(−jωT), which turns 360° every 1/T Hz. One splitter–Delay–Atten–combiner circuit
took **35 s** for a 0.5 ms LTE record against 0.4 s without the Delay, and its response was only approximate. A
matched lossless delay is exactly a lossless line with Zc = Z0 and γl = j·2πf·T, which `EnvelopeLineDelay` already
steps exactly (the method of characteristics). `DelayModel` now implements `IUniformLine`. `IUniformLine.Ends` names
a line's two ends among its nets: by default its first two, and for the four-net Delay block port 1's and port 2's +
nets. The substitution happens only when both − nets are ground, Z0 is real and T > 0; otherwise the block keeps its
S-parameter stamp. The same run now takes **0.3 s**, and the genie equalizer takes a 1 µs echo out to the run's own
floor.

**The run's floor is 8e-4 %, not 1e-6 %.** Through a pad, an unshaped LTE record read 7.5e-4 % with genie and
8.2e-4 % with one gain. With the echo it read 1.55e-3 %, unchanged at `Tol=1e-12`, so this is the envelope's playback
of the record and not the solver's tolerance. `EnvelopeEqualizerTests` therefore holds the genie through the echo to
1e-3 % of the no-channel run (from 48 % with one gain), not the brief's 1e-6 %. **Shaping must be off for that
comparison.** The shaping filter runs after the cyclic prefix is inserted, so a prefix is then not quite a copy of its
symbol's end, and an echo inside it reads something the source did not play (0.195 % against 0.253 %).

## brief-pch-6 — the Propagation Channel in the envelope (EnvelopeChannel, 2026-11-15)

- **Explicit, one way, and on the right-hand side only.** For the run the channel stamps its two ends alone (S21 left out), and
  each ray's delayed transmit wave w = (v + Z0·i)/√Re Z0 enters the receive row's right-hand side. The simulated delay is one step
  plus the ray's excess delay, so the quadratic Lagrange nodes are always solved steps, sub-steps included. Unlike a line, there
  is no current-step share in the matrix. The transmit and receive sides stay block-diagonal: gate 2 (receive side alone,
  driven by the recorded transmit wave) agrees with the joint run to exactly 0, and gate 5 (changed receive side) leaves the
  transmit side bit for bit.
- **The step's latency is a pure envelope delay.** The rotation is e^(−jω_k·τ) with τ the excess delay only, so a constant
  envelope reads H(f_k, t), which the harmonic-balance start stamps frozen, and the history before t = 0 is that steady state.
  Measurement skips whatever of h + τ_max the warm-up does not hold (`SettleSkip`). Parallel segments warm up for at least
  twice that.
- **The free-space slope across a product's band had to be carried.** The amplitude λ/(4π·d) goes as 1/f. Held at the product's
  frequency, it read 5e-4 off H(f) across ±0.7 MHz at 2 GHz. It is now carried to first order, (1 − Δf/f_k), with the Δf as
  the Lagrange derivative of the delayed wave, as `EnvelopeLineDelay` carries a loss slope. The second order is 1e-7 there.
- **Quadratic Lagrange needs a fine step for 1e-6.** Its error at the band edge is about d(1−d)(2−d)/6·(ωh)³. At the automatic
  step that is ~1e-4 of the delayed ray. Gate 4 steps at 32 per signal sample and reads 8.7e-7 against H(f), the delay 3.37
  steps.
- **Two existing passes would have eaten the channel.** `EnvelopeSnpFit`'s generic non-affine test and `EnvelopeTangentCheck` both
  stamp every element at the steady state, where the channel's frozen H is not affine in ω. The fit would have replaced it with a
  rational approximation, and the check would have warned about it. Both skip `PropagationChannelModel` now.
- **Envelope noise skipped device sources** (with a warning that their noise is not modelled in an envelope run). The channel's
  receive-pin noise is stationary thermal, so it is let through.
- **HB needs a tight tolerance for a 1e-9 dB power gate.** At the default `Tol` a P1Tone's matched voltage is 5e-7 off (4e-6 dB).
  Gate 1 solves to 1e-14 A, and the channel's own ratio V(rx)/V(tx) is Friis to 1e-11 either way.
- **What a run reports.** `PropagationChannelReport.Add` writes `link:<instance>:PathLoss_dB`, `Loss_dB[term]`, `Delay0_s`,
  `Doppler0_Hz`, `Reference_Hz`, `RayDelay_s`, `RayDoppler_Hz` and `RayGain[ray]` into the S-parameter, HB (all three paths) and
  envelope results. HB also notes once that it holds the channel with no Doppler shift.

## brief-pch-7 — fading in the envelope, and the genie through a fading channel (2026-11-15)

**The genie no longer takes the fitted offset out before dividing by the channel's H(f, t).** `EnvelopeDataSet.Read` ran
`RemoveOffset` (a phase, frequency and drift fit over the symbols) and then divided by H, which already holds the
channel's Doppler and fading phase. So the channel's phase was removed twice: a fading tap's random phase walk was
partly "fitted" as an offset. Since PCH-6 this applied to a single Doppler ray as well, but no gate exercised the genie
through a channel. With a channel response the genie now equalizes the symbols as they came. The offset is still
measured and reported, and `none` and `pilot` are unchanged.

**Gate 6 against an independent prediction, to 0.3 %.** NR 10 MHz at 30 kHz through flat Rayleigh at f_D = 300, 600 and
1200 Hz (f_D/Δf_sc = 0.01, 0.02, 0.04) reads 0.950 %, 1.627 % and 4.097 % EVM. `testdata/propagation/ici_reference.py`
predicts 0.951 %, 1.622 % and 4.099 %. The script writes the generator again from its definition and computes each
symbol's error by Parseval, (var_W(h) + |mean_W(h) − h_c|²)/|h_c|² over the useful part. It reads none of circuitRF's
demodulator, equalizer or engine. The prediction depends on the realization, not only on f_D/Δf_sc. A genie error is
ICI/h, and E[1/|h|²] diverges for Rayleigh, so an ensemble formula cannot be met within 10 % by one drop. The test
therefore reads each measured symbol's middle from the receiver and averages the script's E(t) there. The script picks
the first seed whose |h| stays above 0.4 over the window (seed 1). The window is not a whole record, so no symbol wraps
it. A wrapped symbol joins the end of the window to its start, which a time-varying channel makes a discontinuity.

**Cost.** Each fading ray adds 64 sinusoids per step per product. A 24-tap TDL-C through 1 ms of NR 10 MHz (58,000
steps at 16.3 ns) ran in 1.36 s wall. The three gate-6 runs take 2 s together.

**What a run reports** (`PropagationChannelReport`): `RmsDelaySpread_s`, `CoherenceBandwidth_Hz`, `fD_Hz`, `Tc_s` (the
first lag where the power-weighted |Σ p_i R_i(τ)| falls to one half, infinite when it never does),
`PowerDelayProfile_dB[delay]`, `DopplerSpectrum_dB[doppler]` (the drop's lines in 32 bins over ±1.25 f_D), and
`TapGain[ray, time]` (256 instants over the record, or TimeOffset alone). An envelope run adds `H[time, offset]` (64 × 33
over the widest carrier's band), `fDxTrecord`, `fDoverSCS` and a regime note. The Signal quality preset draws the
first channel's power-delay profile and Doppler spectrum under its table, points only, with the Doppler axis in Hz
(a plot defaults to GHz).

## brief-pch-8 — instance ends in the envelope, and what was not built (2026-11-15)

**The envelope steps each ray from the transmit elements' waves.** `EnvelopeChannel.ICh` keeps a ring of every transmit
element's wave per product. Per ray it forms the departure field from the waves one step plus its excess delay earlier,
using quadratic Lagrange on solved steps with PCH-6's first-order slope term, turns it by the ray's fading and Doppler,
and adds ½·(j)·F_rxᵀ·y on each receive element's source row (or the pin's wave row). The cost is K·(5·N_t + 2·N_r)
multiply-adds per step per product. It is counted (`EnvelopeChannel.LastInstanceCost`) and stated in the run's note.
Through two directional rays into a 4 × 4 array, the envelope settles on the harmonic-balance feed/transmit voltage
ratio to 9e-9 at Tol 1e-12. The residual follows the Newton tolerance (6e-8 at draft accuracy), so it is the solve's,
not the coupling's.

**Gate 2 is held by counting, not by solving.** A 64 × 64 array to a 64 × 64 one through CDL-A (460 rays) stamps 920
unknowns and 4.35 M entries. The bound is 2K·(2N_t + N_r) = 11.3 M; rays from behind an aperture meet a pattern of zero,
since the record covers θ ≤ 90°. N_r × N_t would be 16.8 M. The channel's longest coefficient array is 4096. Nothing
solved the link; the arrays' own 4096-element solves are PAA-8's.

**Gates 5 and 6 against an independent sum, to the sixth decimal.** A boresight beam on a 16 × 16 isolated-element array
loses 27.752902 dB in one CDL-C drop (480 rays) against the line of sight. Two transmitters at 5.9 and 5.95 GHz into one
array give an SIR of 37.391671 dB, and the signal is unchanged to 1e-9 dB with the interferer present.
`testdata/propagation/array_link_reference.py` reproduces both from the rays the channel realized: each element's
wave, the array factor and the beamformer's sum. The rays are committed and the test checks them, so the script reads
exactly what the run used. The element's pattern along each ray is circuitRF's own reading of the record (PAA's,
gated there), passed to the script as data.

**Not built:**
- **R-pch8-4, the receive collapse, and gate 3.** Forming the beamformed output from Σ_r AF_rx·… instead of element by element
  needs an engine path that replaces a fast-path receive array's channel cells and elements by one source at its feed.
  Today every receive element is stamped.
- **R-pch8-9's reported per-channel received power and SINR at the feed.** Several channels into one array add, which gate 6
  measures, and a run note names the channels sharing a receive end. The cubes naming each channel's feed power and the
  SINR need each channel's share of the solution, which the report does not see.
- **An EM Field Data block as an end** is refused (see src/Core/RESOLVED.md).

## brief-pch-8b — the receive collapse, each channel's share at the feed, Field Data ends (2026-11-15)

**The collapse keeps the array's own stamp and moves only the channel's injection.** A Feed-mode receive array that
meets PAA-8's fast-path conditions is linear between its elements' emitted waves and its feed. So the channel does not
inject on each element's source row. It adds one Norton current at the feed, J = 2·b_m/(√Z0·(1 + Γ_F)), where b_m =
Σ_n G_n·c_n is what the elements' waves deliver into a matched load at the feed. The array's stamp, which does not
depend on the field, still supplies Γ_F to the rest of the circuit, so the equivalence is exact.
`PhasedArrayFastPath.Receive` forms G = S12_ch·M⁻¹·t from the fast path's own closure matrix M = I − Γ_ch·D·S (it was
M⁻ᵀ·t, with Mᵀ factorized explicitly, until the PCH review below: the two agree only with every element on). Γ_F is formed as `TryWaves` forms it. Through CDL-C (480 rays) into a 16 × 16 array with identical
mismatched channels and coupling on, S21 matches the element-by-element stamp to 2.5e-15. Harmonic balance matches to
2.7e-11, its floor on this bench. A 4 × 4 envelope run matches to 2.6e-11.

**G and Γ_F are kept, the feed's node never is.** Caching the whole collapse per (array, resolved values, channel, frequency)
injected at a node index from an earlier netlist, where the same array's feed has another number. The feed was 24 dB
low, exactly 10·log10(256), until the node came from the current netlist.

**The envelope still steps the receive array.** With the collapse, each ray is projected on one node (the counters say 1
instead of N). But the array's channel cells and elements are still in the envelope's circuit. An envelope run of the
16 × 16 array with its 256 channel cells takes over 8 minutes whatever the rays. Taking such an array out of the
envelope entirely (note §8.2's "not stepped at all") needs the envelope to stand the closed array at its feed, as the
fast path's outer circuit does for S-parameters. It is not built.

**Each channel's share** comes from its own ray unknowns in the harmonic-balance solution
(`HbLinearBackSolver.GetSolution`), mapped to the matched-load feed wave by the coefficients the collapse stamped.
`link:<channel>:FeedPower_dBm` equals the run with only that channel to 1e-9 dB. `link:<array>:SIR_dB[channel]` equals
array_link_reference.py's to six decimals. It is a signal-to-interference ratio: the noise term R-pch8b-3 asks for is
not in it, because the noise is the S-parameter noise analysis's (SystemTempK), which the HB run does not hold. An array
off the fast path reports NaN and says why. Multi-tone HB does not report shares.

**EM Field Data ends** couple onto the block's own rows in the form the block stamped at that frequency
(`SnpModel.StampsAsWaves`): 2b − 2S·a = 2c, or v − Z·i = 2√Z0·(I − S)⁻¹·c. The block's stamp is unchanged.
A one-port block on e1 receives what a 1 × 1 array on the same record does to 1.0e-12 (the (I − S)⁻¹ path), and
transmits identically. Envelope runs refuse a Field Data end.


## brief-trn-1 — the transient engine on lumped and nonlinear models (2026-11-15)

`src/Engine/Transient/` (`TransientEngine`, `TransientElements`, `TransientRunner`, `TransientTypes`); design in
`docs/design/transient.md` §4. Gates: `tests/Engine.Tests/Transient/TransientEngineTests.cs`, `TransientElementsTests.cs`,
`tests/Ui.Tests/Transient/TransientCliTests.cs`.

- **The two-stamp recovery is one class now, `AffineStamp`.** It was written out three times (the envelope's single-fundamental
  products, its multi-fundamental lattice in `EnvelopeEngineMt`, and `EnvelopeMemory`'s pole estimate); all three call it, with the
  same arithmetic in the same order, and the 126 envelope tests pass unchanged. The transient engine takes its baseband pair, real.
- **Which elements run is measured, not listed.** `TransientElements.AffineError` stamps each linear element alone at 1 and 2 Hz,
  recovers G and C, and compares G + jωC with the element's true stamp from 1 kHz to 100 GHz by the envelope's terminated measure
  (`EnvelopeTangentCheck.TerminatedDiff`, made internal for it). Lumped models come out at round-off. **SRLC, and an `L` with `C=`,
  are refused**: a capacitor in series on one inductor branch stamps 1/(jωC) on that branch's own row, which no real G and C
  reproduce; it needs a state of its own, which TRN-4's realization gives. A Port or Term is measured through
  `PortModel.TerminationAdmittance`, as the linear partition stamps it.
- **TLIN prints its own 1 Hz degeneracy line to stderr** (`TLineModel` writes `Console.Error` directly rather than queueing through
  `IReportsWarnings`), so the probe at 1 Hz makes it appear beside the transient's refusal of the line. The envelope's own probes
  trigger the same line. Models that queue their warnings have the probe's drained and dropped.
- **Sources need no per-model work either.** b(t) is Re Σ_f B_f e^(j2πft) over the frequency set of every tone source (0 included),
  each B_f the linear partition's own right-hand side at that frequency (`BuildSourceRhs`). A tone is harmonic balance's phasor, so a
  V_1Tone with Phase=0 starts at its peak and the operating point is solved there (the RC test checks 1.5·10/11 V, not 0.5·10/11).
- **The DC engine has no limiter or damping to call** (it takes full Newton steps, λ = 1, backing off only on a model's refusal), so
  R-trn1-6's "limiting and damping are NonlinearDcEngine's" has nothing to reuse. The transient's defences are the step itself: a
  failed Newton halves h, and the opening backward-Euler steps after every corner. None of the gate circuits needed more.
- **LTE tolerance is relative to each row's running peak, not its instantaneous charge.** On a 200-section lossless LC ladder the deep
  sections' inductor rows have tiny currents and an absolute flux tolerance of L·ITol ≈ 1e-22 Wb at 0.1 nH, which round-off-level
  ripple exceeds; the step was pinned at a few hundred femtoseconds. Against max |Φ_i| over the accepted steps, a zero start of that
  amplifier went from 60,828 to 14,128 steps. The absolute term is kept.
- **A step so small that t + h rounds back to t would be accepted forever**: the floor is max(MaxStep/2^40, 1e-13·Stop).
- **Newton's last evaluation is reused** for the step's charges and error estimate (each device keeps its last result and port
  voltages), so an accepted step costs (Newton iterations) evaluations rather than two more; `Tran.DeviceEvaluations` counts them.
- **gear2's damping is ~1e-3 of a damped ring's own decay** at the steps the default tolerance picks, below what a peak fit resolves,
  so R-trn1 gate 2's second half is an undamped LC: trap holds 0.9999 of the amplitude over 20 cycles, gear2 0.900 at the default
  tolerance — about 0.5 % a cycle, which is what Method=gear2 costs an oscillation at RelTol 1e-3 (0.9855 before the tolerance was made
  relative to each row's peak, when the steps were smaller).
- **Both Newton paths give identical waveforms (1.2e-13 V on the rectifier)** because they converge to the same tolerance on the same
  steps; the brief's "1e-9 relative" holds with margin. The full path factors once per Newton iteration (42,498 on the rectifier against
  26 for the interface path).

### The interface/full threshold (R-trn1-6)

Measured with a scratch harness (each circuit run on both paths, same steps; Debug build, one core), after `J = I + Zp·D` was
made to use D's sparsity (it holds entries only between a device's own ports — a plain triple loop made the ring of 101 take 16.6 s
on the interface path):

| Circuit | Interface ports | Steps | Interface path | Full path |
|---|---|---|---|---|
| One Curtice FET, 2 × 200-section LC ladders (≈1,200 unknowns) | 2 | 3,649 | 0.23 s | 1.35 s |
| Bridge rectifier, 4 diodes | 4 | 4,006 | 0.03 s | 0.04 s |
| Ring of 15 CMOS inverters (MOS level 1) | 16 | 2,041 | 0.22 s | 0.23 s |
| Ring of 51 | 52 | 2,025 | 0.80 s | 0.56 s |
| Ring of 101 | 102 | 2,023 | 2.14 s | 1.04 s |

`TransientEngine.InterfaceThreshold` is 32: the interface path wins wherever a few devices sit in a large linear network (the RF
case), the two are even near 16 ports, and above ~32 the dense solve (N³/3 per Newton iteration) loses to one sparse factorization of
the whole Jacobian. The full path factors once per Newton iteration; its symbolic analysis is not yet reused between iterations, which
is the first thing to try if the threshold matters.

### A worker-hosted device (R-trn1-13)

`tools/DeviceWorkerExample`'s square-law FET in a common-source stage (a 1 MHz tone on the gate, 20 µs, 2,006 accepted steps), against
MOS level 1 with the same law in the same circuit: **2.99 evaluations per accepted step** for both (Newton's 2.46 per step plus the
operating point and the opening steps; the charge and error estimate reuse Newton's last evaluation), **36 µs per evaluation through the
worker against 1.5 µs built in** — 0.22 s against 0.01 s for the run. Each evaluation is one round trip: transient cannot batch over time
as harmonic balance batches over samples. Batching one Newton iteration's devices into one round trip is the remedy if a design with
many worker devices needs it, and it is the worker protocol's to add (note §13.6).

The example worker models saturation only (no triode region): a drain resistor large enough to push it below 0 V has no solution at
all, which the transient reports as its starting state not converging, naming the drain net. That is the model, not the engine.

## brief-pch-9 — picking the beam (BeamSelection, 2026-11-15)

**Where the selection runs.** At the start of a run, in the engine, because the carrier is known there and not at elaboration:
`SParameterEngine.Prepare` (the band's middle frequency, as the channel report is read), the three HB carrier blocks, `EnvelopeEngine` /
`EnvelopeEngineMt` (over the signal's band, before the noise is formed from the stamps), and `PhasedArrayFastPath.TryWaves` (before it reads
the weights). The elaborator leaves an `ArraySteering` on every Feed-mode array: its `steer()` call (`SteerCall`, the reading the tool's drag
uses) and a `Weigh(θ, φ)` that is `FeedWeights` on the instance with the call's two arguments replaced by literal degrees. The chosen entry's
weights are copied into the array's own beamformer (`PhasedArrayFeedModel.TakeWeights`), so a beam costs no elaboration (gate 4: 1
elaboration for a scored 64 × 64 codebook and its HB run).

- **A VAR argument is replaced in the array's own field, not set.** Two ends steered by one VAR would move together, and anything else
  reading it would move with them. The tool's Apply is what writes the VAR.
- **Selected once per run, keyed on the beamformers too.** A re-weighed netlist (PAA-8b) shares the channel model with the kept one but has
  new beamformers; a cache on frequency and time alone returned the old table and left the new feeds with the authored weights.
- **The receive collapse's cache key had no weights in it.** `G = S12·M⁻ᵀ·t` and `Γ_F` carry the beamformer's `t`, and `ArrayKey` leaves out
  what only moves the weights, so a re-weighed receive array (a selected beam, or a tuned steer VAR — the latter a PCH-8b defect) was handed
  the previous beam's collapse. `PhasedArrayFeedModel.WeightsKey()` is in the key now.
- **A fields analysis on a channel's transmit array crashed** (`MnaSystem.StableOrderByColThenRow`, index out of range): the fast path
  solves the circuit outside the array with the array closed at F, and the channel was in that circuit, stamping rows against element
  branches the system never allocated. The channel only reads the transmit array's waves, so it is left out of that solve; a channel that
  receives on the array puts rays on its elements, so that array is solved whole. Both predate PCH-9; the tool on a channel end needed them.
- **A literal angle inside an expression is glued: `30deg`.** `30 deg` is a number followed by a name and the parser refuses it (and a space
  splits a netlist line). PAA-9's literal rewrite wrote the spaced form; `PhasedArraySteering.Rewritten` and `SteerCall.Rewritten` glue it.
- **Unstated BeamSelect with nothing steerable is plain manual, said nowhere.** Every PCH-8 bench's arrays have no `steer()`; a note on
  each would be noise. Stated, an end that cannot be steered refuses with the reason and names `manual`.

**The codebook.** One beamwidth is the DFT spacing Δu = λ/(Cols·dx), Δv = λ/(Rows·dy) — a beam's peak to its first null — so a grid
neighbour, diagonals included, is "within one beamwidth" (Chebyshev distance ≤ 1 in units of the spacing) and rank 2 skips it. On the gate's
two-cluster channel the second-strongest pair overall is such a neighbour (rank 1's receive beam with the diagonal transmit neighbour),
stronger than the other cluster's pair (rank 2, −54.55 dB against rank 1's −42.20 dB).

**Alternating.** Started from the beams nearest the strongest rays, up to 8 distinct starting pairs. With 4 starts from the 4 strongest rays
(often one cluster's) it missed the exhaustive best on one of the three CDL-C drops of gate 6 by 0.10 dB; with 8 distinct starts it finds it
on all three, scoring 1,469 / 886 / 1,327 of 22,201 pairs (16 × 16 both ends).

**The bound** is phase-only and unconstrained, alternating dominant eigenvectors from the best pair; it is never below the best codebook pair
(it is taken as the larger). Through CDL-C at 16 × 16 the codebook costs 7-8 dB against it — the bound combines several clusters coherently,
which one steered beam cannot; through a single ray the cost is the grid's straddle loss (≈3 dB on 8 × 8 with the ray between entries).

**Cost** (Debug build, `explain --analysis` on a CDL-C bench, both ends 64 × 64, default codebooks of 2,501 entries each, alternating, 44,938 of
6.26 M pairs scored; the same bench with a 64-entry codebook per end takes 4 s, which is the elaboration and the realization):

| | Total | Peak RSS |
|---|---|---|
| Every entry weighed through `FeedWeights` and held | 51 s | 1.76 GB |
| Entries let go once their factors are taken | 53 s | 317 MB |
| Closed-form weighing for scoring (`ArraySteering.Fast`) | 14 s | 303 MB |
| … and the rays' phase tables built once per end, not per entry | **9 s** | 279 MB |
| The same with `PhaseBits=4` (the quantised weights are not separable: K·N per entry) | 17 s | 283 MB |

The choice is the same in every row. **The closed form** applies only when the field is exactly the bare call (Phase `steer(θ0, φ0, f0)`, Delay
`steerDelay(θ0, φ0)`) and f0 reads no element name; it starts from one exact weighing (each element's amplitude, sign kept, and the other
field's values) and forms the phase in closed form, quantised as `FeedWeights` quantises. Against the exact weighing at four directions on 16 × 16
it is within 4e-15 (continuous; Taylor taper with PhaseBits=5 and AttenStep=0.5; steerDelay with a Phase offset), no quantisation boundary
crossed. It is used for SCORING only: what a run applies, and what a kept selection is validated by, is always weighed through `FeedWeights`,
so a phase sitting exactly on a quantisation boundary could move a candidate's score by that rounding and never the run. Any other field (an
offset beside the call, a 5-argument form) is scored through the exact weighing, as before. **The phase tables**: `FactorOnce` rebuilt the
rays' K·(R + C) exponentials for every entry, ~300 M sin/cos over 5,002 entries; `ArrayPattern.TransientTables` builds them once per end.

**Selections kept across netlists.** An envelope run's segments and a sweep's points elaborate the same channel and arrays again, each with
models of its own, so each searched again. A selection is kept (the last 8; a table can hold 10⁶ pairs) under a key of the channel's instance,
resolved parameters and the files they name (with write times), each end's instance, resolved parameters, record and elaborated weights, the
reference frequency, TimeOffset and the band. A hit is taken only after the chosen entries are weighed again exactly and match, so a key that
misses something costs a search, never a wrong beam. Concurrent selections of one key (parallel segments start together) wait for the first
and take its choice (gate: four netlists in parallel, one search, three hits).

**Progress and Stop.** The selection starts deep inside every engine, none of which is handed the run's `RunControl` the same way, so
`CircuitEvaluation.ExecuteCore` makes it ambient for the run (`BeamSelection.Use`, an `AsyncLocal`, so it flows into tasks the run starts).
The selection reports on the control's STAGE counter — "Beam of CH1: weighing the codebooks n / N beam(s)", then "scoring every pair n / A"
or "alternating search n / 8 start(s)", then "the phase-only bound" — and restores the analysis's own stage after. Stop is answered between
codebook entries (~9 ms each at 64 × 64, Debug) and between search steps, as `OperationCanceledException`, which every run already treats as
cancelled. Before this a 64 × 64 selection sat for ~50 s with the run's bar unmoved and Stop ignored.

**A beam drag on an auto-selected array re-ran the whole search at every step.** Each drag step re-weighs the netlist, so the arrays have
new beamformers and the once-per-run cache (keyed on them) missed. Every candidate replaces θ0 and φ0, so a tuned steer VAR changes no
candidate: when each steered end's chosen entry weighs exactly as before, the choice is taken again (one weighing per end, `Reused`
counts it, no pairs scored). Anything that does move a candidate — a taper VAR, PhaseBits — changes that weighing and the search runs.

**Counters are per thread** (`Elaborator.Elaborations`, `ArraySteering.Weighings`, `PhasedArrayFeedModel.WeightsTaken`,
`ArraySteering.FastWeighings`, `BeamSelection.PairsScored`, `BeamSelection.Reused`): a run is synchronous on its caller's thread, and a
process-wide count moved under other tests elaborating in parallel. `BeamSelection.Selections` and `CacheHits` are process-wide, since parallel
segments select on several threads; only a channel with a steered array end moves them, and those tests share one collection.

## brief-trn-2 — sources and initial conditions in the transient engine (2026-11-15)

**Each waveform source and each shaped tone source gets its own right-hand-side column, by difference.** The aggregate
per-frequency phasor columns are built with every waveform source stamping 0 at DC (`StampOverride = 0`) and every shaped
tone source at `DriveScale = 0`; a source's own column is then the right-hand side with it alone switched on, less the
aggregate — exact, b being linear in each source. Nothing about any model's stamp is repeated in the engine. A shaped tone
(`Delay`, `Ramp`, `Damping` non-zero) adds env(t)·Re{b e^{jω(t − Delay)}} at each step, env = 0 up to Delay, a raised cosine
over Ramp, times e^{−Damping (t − Delay)}; its DC offset stays in the aggregate (not delayed). Breakpoints: every
waveform's corners, and a shaped tone's Delay and Delay + Ramp. The automatic step bound also counts an SFFM's
carrier + (|ModIndex| + 1)·Signal.

**IC= is held in the operating-point solve, then released.** A capacitor's by `HoldConductance` across it (a Norton
source of G·V, both nodes or one); an inductor's by `HoldResistance` = 1e9 Ω on its branch row, which then reads
V(a) − V(b) + K i = K I, so the current departs from I by the branch voltage over K. `InitialState=zero` writes them into
the starting state directly. Gate 5: an RC from IC=1 follows e^(−t/τ) within 7.6e-7 (RelTol=1e-6), an RL from IC=1m within 1e-8.

**An SDD reading `time` invalidates its cached evaluation when the clock moves.** The engine caches each device's last
evaluation by its port voltages; with `time` in the equation the same voltages at another time are a different answer, so
`SetClock` clears that cache for those devices. The clock is set to 0 for the starting state (NonlinearDcEngine's seed
included) and to t + h before each step, and cleared in `Run`'s finally.

**A P1Tone/PnTone with a harmonic termination of its own is refused** (`HasHarmonicTerminations`: any band whose Z differs
from Z[1]) before the affine check, with a sentence naming Z[k]; TRN-4 realizes it.

Gates (`tests/Engine.Tests/Transient/TransientSourcesTests`): a pulse train's 16 corners are accepted steps and the output
equals the definition to 1.0e-14; PWL File= equals Points= bit for bit and a repeated time is refused naming line 3;
Ramp=10/f follows the raised cosine to 1.6e-14; a damped delayed sine equals A·e^(−θ(t−td))·sin(2πf(t−td)+φ) to 1.5e-14;
HB and S-parameter benches with a pulse equal Vdc at its first level bit for bit, and HB is bit-identical with
Delay/Ramp/Damping set; V_Expr equals V_Exp exactly; `time` outside a condition in HB is refused naming it.

## brief-trn-3 — soft breakpoints and the first hard-switching measurement (2026-11-15)

**A soft window limits the step without landing.** `ITransientDevice.SoftWindow` gives a control port and a voltage window; before
each step `SoftRung` extrapolates that port's voltage along its slope over the segment's last two accepted steps, and when the
step from the current rung would reach into the window, moves down the ladder until h ≤ window / (TransitionPoints·|slope|). Only
the ladder rung changes, so the step still factors from the kept rung set and nothing is landed on. A segment's first step has no
slope and no limit, which costs nothing because a segment opens six rungs down. Measured: a 1 V window crossed in 50 ns with
MaxStep = 50 ns takes 8 accepted steps (7 inside), where the unlimited ladder would take one or two.

**A state change between steps clears the device's cached evaluation** (`AcceptStep` returns true): its i(v) is a different
function afterwards, and the cache is keyed on port voltages only.

**R-trn3-6 — a buck converter, the first hard-switching circuit measured.** 12 V in, VSwitch (Ron 0.1 Ω, Roff 1 MΩ, Von 3 V,
Voff 2 V) driven by a 0-5 V 1 MHz pulse (10 ns edges, 490 ns width), a diode (Is 1e-14) from ground, 10 µH, 1 µF, 5 Ω, 200 µs
(200 cycles) from InitialState=zero, Step 10 ns, Debug. It runs without failure: 33,226 accepted steps, 4 rejected, 84,695 Newton
iterations (2.5 per step), 812 factorizations, 231,794 device evaluations, 801 breakpoints landed, 14 method-rung pairs, the
interface path; 0.21 s. The settled output (mean of the last 20 cycles) is 5.5175 V with 40 mV ripple, against the duty-cycle
estimate D(Vin − I·Ron) − (1 − D)·Vd = 5.5269 V: −0.17 %.
Gate 3 (HB against transient): a tone-driven switch's current agrees on its first five harmonics to 1e-4 dB.

## brief-trn-4 — lines and frequency-defined elements in transient (2026-11-15)

**Where it lives.** `src/Engine/Transient/TransientRealization.cs` replaces, for one run, every element the transient
engine cannot stamp as fixed G and C (and refuses the rest by name); `TransientLines.cs` steps lossless lines;
`src/Engine/RationalPassivity.cs` checks and enforces passivity of a rational S matrix; `src/Engine/DelayInterpolation.cs`
is the envelope's quadratic Lagrange moved down unchanged (`EnvelopeLineDelay` and `EnvelopeChannel` call it; the envelope
classes and their fixed files pass unchanged). The fit band is DC to f_max = 1/(2 MaxStep), MaxStep found from the sources
before anything is stamped.

### R-trn4-3 stopped and reported: no shipped lossy line is causal within 1e-3

The design note's method (the residual e^(−γl)·e^(jωτ) given the minimum phase of its magnitude by the cepstrum, τ the
high-frequency delay, the residual fitted rationally, Zc held) was measured on the shipped microstrip, stripline and
coplanar models at three geometries each, with a scratch harness evaluating `IUniformLine.LineAt` directly: |ΔS21| in
50 Ω between that realization and the model, DC to f_max.

| model, geometry | loss at 20 GHz | Δφ at band edge (20 GHz) | residual fit | max \|ΔS21\| 20 GHz (best τ) | max \|ΔS21\| 5 GHz |
|---|---|---|---|---|---|
| MLIN 0.254 mm Er 3.66, L 10 mm | 0.23 dB | −0.045 rad | order 4, 8.5e-5 | 2.3e-2 | 2.6e-3 |
| MLIN FR4 0.8 mm, tanD 0.02, L 25 mm | 1.73 dB | −0.55 rad | order 5, 6.5e-5 | 0.22 | 2.1e-2 |
| MLIN GaAs 100 µm, L 2 mm | 0.23 dB | −0.021 rad | order 5, 3.7e-5 | 9.7e-3 | 2.7e-3 |
| SLIN b 0.5 mm Er 3.5, L 10 mm | 0.20 dB | −0.040 rad | order 4, 6.7e-5 | 4.4e-3 | 1.7e-3 |
| SLIN FR4 b 1.0 mm, L 25 mm | 2.07 dB | −0.58 rad | order 5, 8.3e-5 | 4.0e-2 | 1.3e-2 |
| SLIN LTCC Er 7.8, L 3 mm | 0.13 dB | −0.018 rad | order 4, 4.8e-5 | 2.8e-3 | 1.3e-3 |
| CPW 0.254 mm Er 3.66, L 10 mm | 0.17 dB | −0.040 rad | order 4, 6.2e-5 | 1.2e-2 | 1.5e-3 |
| CPW FR4, L 25 mm | 1.44 dB | −0.52 rad | order 5, 7.5e-5 | 0.14 | 1.1e-2 |
| CPW GaAs, L 2 mm | 0.22 dB | −0.024 rad | order 3, 2.6e-5 | 5.2e-3 | 3.9e-3 |
| MLIN, no loss (dispersion only) | 0 | 0 | order 1 | 1.9e-2 | 7.7e-4 |

"Best τ" lowers τ below the high-frequency delay by the least-squares best amount (still causal); with τ the
high-frequency delay itself the 20 GHz errors are 1.5-2.4× larger. The fit is never the problem (≤ 1e-4); the causality
correction is. The loss models attenuate with no matching phase, and the minimum phase of that attenuation is a real
phase lag that the frequency-domain model does not have — so the realization is "wrong" by exactly the model's
non-causality, and that is 1e-3 to 0.2. Dispersion (ε_eff(f)) is a phase with no magnitude, so a minimum-phase residual
drops it entirely: the lossless microstrip alone misses by 1.9e-2 at 20 GHz. Zc held constant costs ≤ 9e-4 (S21 depends
on the mismatch squared).

**So, as the brief directs, lossy and dispersive lines are refused in transient by name**, the sentence naming the
measured |ΔS| of the line as a lossless delay and the analyses that support it. What runs:
- a line whose delay alone reproduces its own model within 1e-3 (|ΔS11|, |ΔS21| in 50 Ω, DC to f_max) is stepped as that
  delay — every ideal TLIN, a lossless physical TLIN, a lossless stripline, the Delay block, and a low-loss line at a
  low enough f_max;
- a lossy line with f_max·τ ≤ 2 is offered to the ordinary wideband fit of its own stamp (R-trn4-4, causal by
  construction, its error measured against the model) and runs if that meets 1e-3.

### Lines (R-trn4-1/2)

Rows `v − Zc i − W_other(t − τ) = 0` are real and constant (G). τ ≥ h: the delayed wave is read from accepted steps by
the quadratic through three steps that do not straddle a corner (a breakpoint landing), else linearly. τ < h: linear
between the last step and the unknown one, the unknown's share (h − τ)/h a term of the stepped matrix — stages are then
keyed (alpha, h), and only then, so a run with no short line keeps TRN-1's factorization counts. The operating point
couples each line fully (a lossless line is a short at DC). Breakpoints τ and 2τ after every source corner (the start
included), capped at 200,000. CLIN runs by its even/odd modes (Zc = Ze/2, Zo/2, delays E/(2πF), Eo/(2πF)). Gate 1's
reflection matches the closed form to 4.4e-11 V and gate 2's NEXT/FEXT to 5.3e-12 V.

### Wideband fits (R-trn4-4)

- **The fit loop** walks orders (to 60) until the relative RMS error over the data's own points meets 1e-3 (1e-9 for a
  System Filter/Duplexer, exactly rational), and **stops when eight orders have not halved the error** — a delay or a
  non-causal response would otherwise walk every order (the envelope's 132 s MCLIN). An order with a pole of damping
  −Re(a)/|a| < 1e-6 is not taken: see the terminations below.
- **Passivity** is checked on a uniform grid ten times denser than the samples (≥ 2,001 points) to f_max, a log tail to
  100·f_max, every pole's frequency and s → ∞. Enforcement moves residues and D (poles kept) by the least-norm change of
  the RESPONSE over the samples (the basis Gram matrix as the weight) that brings σmax to 1 − 1e-6 at the constraints, and
  repeats. **Constraining only local peaks failed on a plateau**: a file with |S21| 2 % too large across the passband
  ended at σmax 1.031 after 40 iterations (worse than 1.020) and took 20 s; constraining the peak plus 24 points spread
  across each violating band converges in a few iterations (σmax 0.99999983, change 3.1e-3) in well under a second. The
  2×2 singular value is in closed form (power iteration was most of those 20 s).
- **The committed band-pass** (`testdata/transient/bandpass_1ghz.s2p`, from `bandpass_1ghz.py`, independent of
  circuitRF) fits at order 6, error 2.8e-7, σmax 1.0000009 made passive by a 2e-6 change. Driven by a 0.4 ns pulse, the
  output's transform against the file's S21 across 0.8-1.2 GHz is set by the INTEGRATION, not the fit: 4.1e-3 at
  RelTol 1e-5 (2,816 steps), 2.3e-4 at 1e-7 (10,302), 1.1e-4 at 1e-9 (45,656). Gate 4 therefore bounds it by the fit's
  error plus 5e-4 at RelTol 1e-7, not by the fit's error alone. Gate 5 (the enforced active copy, 10⁴ × its slowest pole's
  3.5 ns, 697,000 steps, ~1 s) decays to 2.5e-314 V from a 2.8 mV peak.

### Tone source terminations (R-trn4-6): placed poles, not a fit

A P1Tone/PnTone keeps its drive (`DriveOnly`) and its band-mapped impedance is realized beside it, between the external
and drive nodes. **Vector fitting cannot do this**: sampled at the harmonics alone it put lossless poles exactly on the
samples (Re a ~ −1e-32 at 1.8, 2.0 and 2.2 GHz — a "fit" that reads −0.667 at 2 GHz to round-off and rings forever, so
the run saw 50 Ω there); sampled flat across each band it is a staircase (error 0.26); a smooth real-valued blend between
harmonics is zero-phase, which no causal function is (error 0.55). Only the harmonics are read in a periodic steady
state, so the poles are placed — one pair of damping 0.2 on each harmonic — and the residues and constant solved so Γ
takes every harmonic's value exactly (2N + 1 real equations and unknowns), then made passive like any fit. A 2 GHz current
then sees 50 Ω ∥ 10 Ω to 7e-5.

### EM cells (R-trn4-5)

Nothing new: `model=em` already builds the EM bench for any analysis kind and dispatches a tran through
`RunTypedAnalysis`; the EM instance arrives as an SnP on the cached Touchstone and is fitted like any file
(`tests/Ui.Tests/Transient/TransientEmCellTests`).

### Refusals (R-trn4-7)

Unchanged by-kind list (Mixer, PhasedArray, PropagationChannel, Tuner, WSProbe, OscProbe, EmFieldData, MOD_Source), each
naming its analysis; added: lossy/dispersive single lines (above), MCLIN/BCLIN/MBCLIN by name, a Deembed (it would have to
advance what a network delays), and any element whose fit misses its tolerance, the fit's error and order in the sentence.
The sentence is now "is not supported in transient" (it was "not yet supported").

## brief-trn-4b — causal loss in the line models, and lossy lines in transient (2026-11-15)

**The fix was in the models.** TRN-4 refused every lossy line because the shipped losses carry no phase. `CausalLoss`
(`src/Core/Devices/Planar/CausalLoss.cs`) gives them theirs, carried as `MicrostripLineParameters.LossPhaseRadPerM`
beside the lossless β, so Z0 and ε_eff (what the line calculator reports) do not move:
- conductor: Δβ_c = α_c(f) − α_c(1 Hz) — the skin effect's internal reactance equals its resistance. **The DC floor
  matters**: with Δβ_c = α_c, a thin-metal line (GaAs CPW, 3 µm gold; LTCC stripline, 10 µm), whose loss levels off at
  the sheet resistance below the skin-depth transition, was itself non-causal (a constant loss with a constant reactance)
  and missed by 2e-2 at 5 GHz; without the floor's reactance it fits within 6e-5.
- dielectric: Δβ_d = −(2/π)·α_d·ln(f/f_ref), the mid-band wideband Debye relation (permittivity falling by
  (2/π)·tanD·εr per e-fold), pinned at f_ref = 1 GHz for MLIN/SLIN/CPWG (`CausalLoss.DielectricReferenceHz`) and at `F`
  for TLIN. MTAPER and MKLOPF sections take the same phase.

**How far S21 moved** (50 Ω, |ΔS21| and the added phase), measured on the shipped code:

| line | 0.1 GHz | 1 GHz | 10 GHz | 20 GHz |
|---|---|---|---|---|
| MLIN 0.254 mm Er 3.66, 10 mm | 1.1e-3 (0.06°) | 3.1e-3 (0.18°) | 1.2e-3 (0.07°) | 8.6e-3 (−0.5°) |
| MLIN FR4 0.8 mm tanD 0.02, 25 mm | 2.1e-3 (0.12°) | 2.7e-3 (0.16°) | 0.11 (−7°) | 0.28 (−20°) |
| MLIN GaAs, 2 mm | 1.8e-3 (0.1°) | 5.6e-3 (0.33°) | 1.7e-2 (1°) | 2.3e-2 (1.4°) |
| SLIN FR4 b 1 mm, 25 mm | 2.4e-3 (0.14°) | 3.6e-3 (0.21°) | 0.13 (−8.5°) | 0.31 (−23°) |
| CPWG FR4, 25 mm | 1.8e-3 (0.1°) | 3.2e-3 (0.19°) | 8.7e-2 (−5.4°) | 0.23 (−15°) |
| any lossless line | 0 | 0 | 0 | 0 |

Near f_ref a design moves by tenths of a degree (the conductor's internal inductance); a long lossy FR4 line far above it
moves by degrees, which is the permittivity a flat loss tangent of 0.02 implies (4 % lower by 20 GHz). |S21| in dB does not
move (≤ 0.002 dB: the loss is unchanged, only its phase is new). Expressed as electrical length, which is what moves a
resonator built from the line: FR4 (tanD 0.02) −0.8 % at 5 GHz, −1.3 % at 10 GHz, −1.7 % at 20 GHz (a 10 GHz stub resonance
about 126 MHz higher); the low-loss board lines within ±0.2 % above 1 GHz; narrow thin-metal MMIC microstrip (70 µm wide,
3 µm gold) +4.7 % at 1 GHz and +1.4 % at 10 GHz, from the internal inductance of a conductor that thin and narrow (the √f
conductor law MLIN uses does not level off at the thickness, so this follows the loss it already reported). Tests re-baselined:
`TLineModelLossTests.PhysicalForm_WithAcAndAd_MatchesTheClosedForm` and
`MklopfPerformanceAndMessagesTests.Gate2_…` (their closed forms gain the phase, written out, not called);
`TrlAnalysisTests.PointsOutsideTheUsableBand_…` (its TLIN standard, exactly 20° at 2 GHz, is 0.12° shorter there, so the
2 GHz point joins the unusable run: "(1-2 GHz)").

**The transient realization** (`TransientRealization.FitLossyLine`): τ = max(0, τ_min − 1.5/f_max), the residual
H̃ = e^(−γl)·e^(j2πfτ) vector-fitted DIRECTLY (a minimum-phase residual drops dispersion, a phase with no magnitude),
the order the lowest whose realization meets 3e-4 |ΔS21| on a 2,060-point check grid, among fits with |H̃| ≤ 1 on
RationalPassivity's grid (one barely above 1 is scaled down). Its states are extra unknowns at each end (`TransientLineModel`),
the delayed wave entering the main row with weight −D and each state row with −1 (−2 for a pair's first).
Findings, each measured:
- **τ at the smallest phase delay fits badly**: the residual is then barely causal, and 40 poles miss by 1.4e-2 on 0.8 mm
  FR4 at 20 GHz; leaving rotation in the fit (τ × 0.9, 0.6, 0.4 → 5.6e-3, 1.1e-3, 4.5e-4 at order 40) is what works;
  1.5 periods of f_max is the rule taken.
- **Choose the order by the realization's own |ΔS21| on a dense grid**, not the fit's error on its samples: the latter
  over-fits between samples near DC (order 58 reading 1e-4 on its samples was 5.3e-3 off between them).
- **General passivity enforcement wrecks a line residual**: a spurious narrow peak (|H̃| 1.29 at 3.1 GHz on LTCC, 1.09 at
  4.5 MHz on the board MLIN) drove the least-change enforcement to |ΔS21| = 2 and to divergence (σmax 764). Rejecting an
  order that rises above 1 + 3e-4 and scaling a marginal one is safe and cheap.
- **The order loop needs a stall stop** (eight orders without halving the error): a line whose best was order 11 at 3.4e-4,
  just above the 3e-4 target, walked all 40 orders in 8.8 s; with the stop every line fits in 0.04-0.7 s.
- At f_max 5 and 20 GHz every line of the nine geometries, and both TLIN forms, is realized within |ΔS21| 1e-3 (largest
  9.9e-4, CPW GaAs at 20 GHz). At 50 GHz the 0.8 mm FR4 microstrip (7.9e-3) and coplanar (2.0e-3) lines still miss and
  are refused by name. |ΔS11| is set by the held Zc of the dispersive models (2.5e-2 on 0.8 mm FR4 at 20 GHz, ≤ 1.3e-3
  on the others); a warning names a line above 1e-2.

**Gate 3's two traps.** (1) A pulse's spectral images fold back into the band on a coarse output grid: at 5 ps the
transform of a line realized within 2.6e-4 read 1.4e-3 off at 4 GHz (a magnitude droop ∝ f², the triangle pulse near its
5 GHz null); a lossless line hides this because e^(−jωτ) repeats exactly across the images. 1 ps and 0.1 ns edges fix it.
(2) What remained, 9.1e-4 at 4 GHz on a line realized within 7e-5 there, was a magnitude deficit that did not move with
RelTol (1e-7 or 1e-9) or Stop (10 or 40 ns) but fell 6× with MaxStep 50 → 20 ps: **the backward-Euler step that opens
every segment, at MaxStep/64, was never error-checked** (`ErrorRatio` returns 0 with one point of history), so its damping,
∝ (ω·MaxStep/64)², was outside the tolerance — and every line adds delayed-corner breakpoints, so more such steps.
**Fixed**: `TransientEngine.FirstStepRatio` checks that step by step doubling — the same interval in two backward-Euler
half steps, the full step's error twice the charge difference (C·h² against 2·C(h/2)²) — and rejects it down the ladder
like any other step. The half-step stage is the trapezoidal stage of the same rung, so it rarely costs a factorization.
Gate 3 went from 9.1e-4 to 1.1e-4 (the same at RelTol 1e-7 and 1e-9: what is left is the fit's own error) and is held at
3e-4. Cost on TRN-1/TRN-3's benches: the RC ring 7 → 8 factorizations for its 4,007 steps; the buck 33,226 → 33,252
accepted steps, 4 → 6 rejected, 812 → 820 factorizations, Vout unchanged at 5.5175 V. The second backward-Euler step was
always checked (two points of history).

## Reuse across drops: one transmit solve per distinct transmit side (brief PCH-10 R-pch10-4, 2026-11-15)

`EnvelopeDropReuse` + `ParametricSweepEngine.RunEnvelopePoints`. The sides are `PropagationChannelGraph`'s islands: receive = every
island a channel receives on and everything downstream; transmit = the rest. A part with no island (a supply) takes the side of every
part its nets reach, and **a supply both sides reach refuses the reuse** (its current would be half one run's), said in a note. The key
hashes the transmit parts (type, path, nodes, resolved values), each transmit array's `WeightsKey()` after the drop's beam selection
(`BeamSelection.Apply` run first on the point's netlist; the engine's own call then hits its cache), the transmit end each channel
presents (pin node, TxZ, TxPattern) and the run's own `EnvelopeParams` (Seed, Noise, Stop …). The first drop of a key runs jointly and
records every channel's transmit waves per segment (an instance end per transmit element: `ChannelRecording.Elements`); a later drop of
that key runs on the receive side alone (every transmit part removed but the MOD_Sources, which carry the signal and time base; every
transmit net held by 1 Ω, `Noise=off`), takes the recording run's segmentation (`EnvelopeRunner.PrepareLike`, so each segment replays
its own segment's recording on the same step grid), and gets the transmit side's node voltages, IProbes, supplies, wires and source
drives back from the recording run's record by name before `EnvelopeDataSet.Build`, so every measurement is the joint run's.

**The harmonic-balance start is the one place the replay is not free.** The joint start solves both sides with the coupling frozen; a
receive side alone would start with nothing incident, and one with memory (a filter) would start elsewhere and differ until it settled.
So a driven channel's start stamps the recording's first sample as a known source on its receive rows (`SteadyDrive`: H(f_k)·w at a pin,
each ray's projection at an array) in place of the coupling — the value the joint start couples in.

Gates: Engine `DropReuseTests` — 50 drops of a fading channel, nonlinear PA, series-LC receive side: every cube of every drop
**bit-identical** with the reuse and without (worst difference 0), counters (1 transmit solve, 49 replays). Ui `DropReuseArrayTests` — an
8 × 8 steered transmit array turned 15° every third drop (`TxAz=15*floor(drop/3)`): 4 distinct beams, 4 transmit solves, 5 replays,
bit-identical.

**A test-side trap worth knowing:** `DataCube.ComplexValues`/`RealValues` copy on every read; indexing them inside a loop made the
first version of the gate's comparison take 60 s against 0.3 s for both sweeps. Read the array once.

**What a drop costs (R-pch10-6)**, Debug, one drop's envelope run into a receive array with channel cells (CW, 1 µs record, 493 steps,
one line-of-sight ray; the transmit side is a pin, so this is the receive side's cost, the part reuse cannot remove). As first measured,
and after PCH-10b (below):

| Receive array | Linear cells, as first measured | Linear cells, after PCH-10b | Nonlinear cells (amplifier), after PCH-10b's checks |
|---|---|---|---|
| 64 | 6.0 s | 0.5 s | 3.7 s |
| 256 | 288 s | 0.9 s | 56 s |
| 1024 | not finished in 300 s (5.3 GB) | 1.2 s | not finished in 300 s |
| 4096 | not run | 5.4 s (2 GB) | not run |

**The first measurement's cost was not the stepping.** It was three per-element checks the envelope makes before it steps
(`EnvelopeTangentCheck.Measure`, `EnvelopeSnpFit.NotAffine`, `EnvelopeBaseband.Apply`), each stamping one element alone and inverting the
result densely per offset: the array is one element with a port per element, and an instance-end channel's stamp carries two ray
unknowns per ray — so a 2 × 2 transmit array through CDL-C (~460 rays) spent 19 s inverting ~1000 × 1000 matrices for checks whose
answer changed nothing (an earlier version of this entry blamed `InstanceCouplings`; it takes 10-20 ms). See the PCH-10b entry below.
A nonlinear receive array is still stepped element by element and its step cost grows faster than N (the harmonic-balance interface is
every element's device): a Monte Carlo over many drops into 1024 or more nonlinear elements remains impractical, and the docs say so.

## A receive array replaced by its feed, and the checks that cost minutes (brief PCH-10b, 2026-11-15)

**Three pre-stepping checks skip the channel and the arrays.** `EnvelopeTangentCheck.Measure` (the tangent warning), `EnvelopeSnpFit.NotAffine`
(whether to fit an element) and `EnvelopeBaseband.Apply` (whether to hold an element at DC) each stamp one element alone and invert it
densely per offset. A Phased Antenna Array is one element with a port per element: at 256 elements the three took 287 of a 288 s run, at
1024 they did not finish in 300 s, and for the arrays measured they concluded nothing (no fit, no hold, no warning). An array's coupling
is its record's, linear in f between record frequencies, so it is affine across an envelope band; it keeps its value and slope at each
harmonic. The channel was already skipped by two of them and not by the baseband check, where an instance end's stamp alone carries two
ray unknowns per ray (19 s for CDL-C's ~460 rays into a 2 × 2 array; 0.3 s after).

**The receive array replaced (R-pch10b-1).** When the receive collapse is taken at every product, the run has no noise (the cells' own noise
is part of the array) and the array transmits on no channel, `EnvelopeChannel` takes the array, its feed and its channel cells out of the
run's netlist (`ElaboratedNetlist.Prune(…, dispose: false)`), holds the array's other nets by 1 Ω, and puts a one-port at the feed that
presents Γ_F(f) (`PhasedArrayFastPath.FeedReflection`: one factorization of M, not also Mᵀ; cached). The rays already land on the feed as
Norton sources; the channel's harmonic-balance start reads the collapse from an untouched copy of the netlist
(`PropagationChannelModel.CollapseNetlist`); the run's Dispose puts everything back. The one-port solves Γ_F exactly at each product and at
the product ± AffineStamp's dlt (the three frequencies the step's affine stamp is built from) and is the line through them anywhere else
near the product (the tangent check's band edge): three factorizations a product. The first version solved Γ_F with both factorizations at
every frequency asked: 66 s a drop at 4096 elements, against 5.4 s now.

**Equal to the stepped array.** Both are first order around each harmonic (the stepped array affine per element, the one-port tangent to
the exact Γ_F), so they agree to first order and could differ at second order in the offset; on the gates' 16 × 16 array they agree to the
envelope's floor: every cube to 4.2e-10 through two rays and 9.8e-11 through CDL-C. Two cubes needed care in the comparison, not in the
code: a PSD in dB has bins at −400 dB (round-off, compared only within 150 dB of the peak), and a CW's AM-PM is 8e-10° (held to 1e-9°
absolute). Gates: Ui `ReceiveArrayFeedTests` — equality through rays and CDL-C with 258 parts replaced, a noisy run keeps the array, a
reusing drop sweep into 32 × 32 replaces 1026 parts with the transmit pin solved once (64 × 64 does the same in 43 s, too long for the
routine tier), and the couplings evaluate each pattern class once per ray (`PatternEvaluations`: 480 rays → 481, the extra one the line
of sight the pin end's polarization is matched along; R-pch10b-2 was already so, the per-direction cache in `ArrayField.Toward`).

## An envelope probe past a Propagation Channel read as carrying no RF (brief PCH-11, 2026-11-15)

`EnvelopeDataSet` blanks every output row of a probe whose fundamental-band power is below `NoRfFraction` (10⁻⁶, −60 dB) of the
available drive, meant for a supply or bias probe. A receiver at the end of a link sits 60 to 140 dB below the drive, so a Link
bench drop in a 10 dB fade (−61 dBm out of a 2 dBm drive) came back with no EVM, BER, Gt or ACLR at all while the drop beside it,
at −52 dBm, read 9 %; and a sweep over the two refused to stack (`SINR_dB` one element against 7 × 1272). `EnvelopeRunner.PathGain`
now passes `Build` the product of each pin-end channel's |H|² at the carrier, at most 1 each, and the threshold is 10⁻⁶ of the drive
times that. A circuit with no channel is unchanged to the bit.

## A drawn rain's term label grew a drop sweep's result as N² (brief PCH-11, 2026-11-15)

`AddedLosses` names a drawn rain's term with the drop's own time percentage ("P.618 rain at 0.0321 % of the time (drawn), …"),
and `PropagationChannelReport` used term names as the `Loss_dB` cube's `term` labels. Stacked over a drop sweep, every drop's
label was a term of its own: 10,000 drops of a geostationary link wrote `link:CH1:Loss_dB` as 10,000 × 10,001 (807 MB, 19 s);
30,000 reached 5.7 GB before it was stopped. The report now labels a drawn rain `P.530 rain (drawn)` or `P.618 rain (drawn),
clouds and scintillation` (`TermLabel`); the percentage is `RainTime_pct`. 10,000 drops: 6 MB, 2.3 s; the Link bench's own
100,000: 64 MB, 14.6 s. The term's name in the model's notes is unchanged.

## brief TRN-7 — ngspice as a transient solver (2026-11-15)

**The runner lives in `src/Engine/Transient/NgspiceRunner.cs` and knows no discovery.** The program comes from
`NgspiceRunner.Locator`, which `src/Design`'s `NgspiceLocator` fills from `SolverDiscovery.Ngspice` as a module initializer, read
at the moment of each run, so an install that has just finished or a path just set is used with no restart. `Override` replaces
it for one async flow (a test's stub). No locator, or one that finds nothing, is the refusal `NotFound`; there is no native
fallback anywhere in the path.

**The scratch folder is `results/ngspice/<analysis>-<time>-<pid>/` beside the design** (`AnalysisSettings.OutputDirectory`, set by
`CircuitEvaluation.RunFiles` for a transient and for a sweep that wraps one), holding the deck, `names.txt`, `ngspice.log` and
`out.raw`. Without a design folder it is the system temp folder.

**Resampling is the native engine's own `Interpolate`** (made `internal static`, as are `OutputGrid` and `RecordedNodes`), so
the two solvers' results differ only by what each solved. `OP.V` is ngspice's first point. The native counters other than
`Tran.AcceptedSteps` (ngspice's point count) are removed from an ngspice result rather than reported as zeros.

**`ngspice -b -n -r out.raw deck.cir`.** `-n` keeps a user's `.spiceinit` out of the run. ngspice writes "Note: No compatibility
mode selected!" to stdout on every run; only stderr is relayed as notes, prefixed `ngspice <version>:`, and the whole output is in
`ngspice.log`. Cancelling polls `RunControl` every 50 ms and kills the process tree; a `ProcessExit` hook kills it if circuitRF
exits first.

**`.options INTERP` is broken in ngspice 47** — its rawfile repeats the header (variable list and `Values:`) inside the data and
states one point fewer than the grid has; binary output is then unreadable garbage. Nothing in circuitRF uses INTERP. The
references in `testdata/transient/ngspice/` are put on the grid with `linearize` in a control block the generator adds.

**Measured against ngspice 47, worst |native − ngspice| over the grid as a fraction of the swing:** RC 2.5e-6, RLC 3.4e-5,
rectifier 1.0e-4, buck 1.2e-3, lossless line 1.7e-2 (ngspice's points straddle the delayed edge's corners and `linearize` joins
them straight), MOS ring 1.3e-4 over 8 periods (first point skipped: circuitRF reports the zero state at t = 0 under
InitialState=zero, ngspice's first `uic` point already has the sources applied). Live RC with RelTol 1e-5: ngspice 3.0e-6 V from
the closed form and 1.3e-6 V from the native run.

## brief TRN-8 — transient-assisted harmonic balance and oscillator start-up (2026-11-15)

- **Every BJT failed in the native transient at t = 0.** The interface Newton (`TransientNewtonPath.Interface`, the `Auto`
  choice up to 32 interface ports) condenses the linear partition G + αC onto the interface, and a BJT's internal collector,
  base and emitter nodes (joined to its terminals by its own ohmic ports, so present with the default parasitics) carry
  nothing in the linear partition but gmin. The condensed impedance is a teraohm and Newton never converges, even for a
  resistor-biased transistor with no drive ("the step fell below 5E-22 s at t = 0"). TRN-1..7's references hold no BJT. `Auto`
  now takes the full path when an interface node has no conductance above 1000·gmin and no capacitance
  (`TransientEngine.LinearlyTouched`). A forced `NewtonPath=Interface` still fails there.
- **Harmonic balance's line search converges hard-driven stages from DC.** A class-E stage on `VSwitch` converges from DC in
  3 iterations at any switch steepness: a switch whose control is an ideal source is a linear time-varying conductance, so the
  problem is linear in the unknowns. A built-in FET law driven to 42 dBm still converges from DC (30-38 iterations); only with
  a gate diode and a body diode does it take 29 at 30 dBm. Gate 1 therefore states an iteration budget (`MaxIter=20`) as
  `HbDriveSteppingTests` does: the DC start fails within it, the seed converges in 8, the drive ladder in 82 over 11 rungs, and
  the seeded and drive-stepped solutions agree to 7e-12 of each node's largest harmonic.
- **Fast bias trades seed accuracy for settling time.** Scaled to a reactance ratio of 20 (just over the threshold), a coupling
  capacitor's reactance is 5 % of the impedance it sees and the seed is a few per cent off at the fundamental: on the class-E
  bench 26 Newton iterations from it, against 12 with no scaling. At 50 the biased-PA gate (10 µH, 1 µF at 2 GHz) settles in
  121 periods with 5 iterations from it (20: 50 periods, 7 iterations); without scaling it is still 5 % apart after 200.
  `HbTransientSeed.ScaledRatio = 50`.
- **Consecutive-period agreement alone passes a drifting bias.** A 1 µF block charging through 50 Ω changes by microvolts per
  carrier period and passes a 1e-3 per-period test long before it has settled. The settle test extrapolates: the last change
  over 1 − ρ, ρ the least-squares ratio of the last two period-to-period difference waveforms (`SettleMonitor`).
- **A parametric divider for the divider gate.** `q = c2 v²` alone has a capacitance that goes negative below −CT/(2 c2), where
  the transient stops (the charge has a turning point); a cosine pump switched on at t = 0 rings the tank there within a
  nanosecond. The gate's divider starts the pump at zero (`Phase=-90`) and limits the half-frequency with a cubic charge term
  (`c3 > c2²/(3 CT)` keeps the capacitance positive), not a cubic loss, which also loads the pump. From DC, harmonic balance on
  the half-frequency grid stays on the undivided solution (|V1| 6e-12): the odd harmonics' residual is zero there and Newton
  never leaves it. From the seed, |V1| = 3.6224 V; a 100 ns native transient gives 3.6171 V.
- **The shipped VCO without Fguess** (Receiver Noise ▸ 7 VCO Phase Noise): the seed settles in 12 cycles with Cb scaled ×0.004,
  0.03 % off in frequency and 3 % in amplitude, and the search finishes in 3 Newton steps (6 from Fguess) at the same
  oscillation to 7e-12 in frequency and 3e-10 in amplitude.
- **An idle OscProbe is no longer refused in transient**: it is a short or an open, as in every analysis but the oscillator's,
  and the oscillator's seed runs with it in place.

## brief PCH-12 — findings from building the three Propagation Channel examples

- **A channel into an array crashed every envelope run with `Noise=yes`** (IndexOutOfRange in `EnvelopeNoise.Add`). Such a
  channel declares no noise source (it has no receive pin to be thermal at `RxTant`) and reports itself `Passive`, so
  `SParameterNoise` classed it thermal on its stamp: noise injected on both arrays' element nodes, the ray unknowns and the
  absent pin's placeholder node (1073741822), which ran past the right-hand side. It is now noiseless by definition
  (`IsNoiselessByDefinition`, `PropagationChannelModel { RxPin: < 0 }`): the channel adds no noise and the array's is its
  elements' own (PAA-7), as the design note says.
- **An envelope run's cost with many nonlinear devices is quadratic in the interface nodes.** `HbNewton.RunDevicePass`
  allocates and FFTs dense `[N, N, grid]` Jacobian arrays over every interface node for every device pass, though each device
  touches only its own ports. 256 nonlinear element amplifiers (N ≈ 512) cost ~120 ms a step: one drop of two 60 kHz NR
  symbols (1,250 steps) ran over ten minutes; with the elements linear and one nonlinear pre-driver, ~4.5 s a drop of four
  symbols. Not changed here (it is HB's assembly too); the *mmWave Array Link* example keeps its element amplifiers linear and
  says so. A sparse per-device assembly is the fix.
- **A `.cnl` Amp with no `IP3` is nonlinear** (the 40 dBm default); `IP3=200` is exactly linear. A "linear" receive cell
  written without it kept a receive array off the fast path.
- **Reuse across drops does not apply with `Noise=yes`**: each drop solved the transmit side again in all three examples
  (stated in their READMEs and the user page).
- `link:<ch>:Drift0_Hzps` is written beside `Doppler0_Hz`: the common Doppler's rate the channel already applied and named only
  in its note (−991 Hz/s for the LEO pass).

## brief TRN-9 — fast bias scales only beyond a ratio of 50, which three descriptions did not say

`HbTransientSeed.FastBiasPlan` scales a choke or block only when its reactance ratio exceeds `ScaledRatio` (50); one between
`ChokeRatio` (10) and 50 is left alone, since scaling it to 50 would slow it. The file header still said "the ratio is 20",
`ScaledRatio`'s summary "twice ChokeRatio", the `SeedFastBias` key's reference summary "to 20x", and the Simulations page "more
than 10 times … scaled to 50 times". All four now say 50 and why. No behaviour changed. Checked on the class-E example: its
100 nH drain choke is left alone, a 10 µH one is scaled ×0.0149 and named.

## The SDD example's bias loop: two DC defects in HB and DC, and one open convergence problem

Found building *Symbolically Defined Device (SDD)*'s bias-loop bench (an SDD that reads the drain supply's current through
`C[1]` and feeds the FET's gate through a choke).

- **HB decoupled two interface nodes an ideal inductor joins.** At DC they are one node, so Z_NN(0) is rank-deficient with
  no zero diagonal; `HbLinearExtractor.ExtractDC` recognised only a zero diagonal (a node pinned by a source) as singular and
  inverted the rest, and the Gauss-Jordan pass left the two nodes decoupled: −2 V and 0 V where DC has both at −2 V, a
  silent wrong answer. `RankDeficientAt` now finds the rank deficiency and takes the inductance-regularization path. When the
  pair has no DC path to ground but gmin, Z_NN(0) is ~1e12 Ω and the 1 µΩ is lost in double precision, so the matrix stays
  singular; `ForcedDc` then takes Y_NN(0) and I_src(0) with every interface node held by an ideal source (Y ~1e6 S between
  the two). Used only when Z_NN(0) is still rank-deficient after regularization; every other circuit is untouched (1,007
  HB/DC/SDD/loadpull/hero tests unchanged). Gate: `HarmonicBalance/HbInterfaceShortTests` (both cases against DC).
- **DC left a weighted term's control-current derivative out of its Jacobian.** `NonlinearDcEngine` stamped a `w ≥ 2` term's
  value and voltage derivatives but not `JacCtrl` (∂I[p,w]/∂_cn), so the loop written on a low-pass weight (`H[2]` = 1 at DC)
  did not converge where the same loop on `w = 0` did. Gate: `SddWeightedControlDcTests`.
- **Closed-loop HB of that loop did not converge from a cold start** (loop gain 50 or more), with or without a bleed resistor
  on the bias node, though continuation from a small loop gain found the solution and DC converged. **Resolved** by
  brief-hb-control-loop-cold-start: the line search, not the Jacobian (next section).
- A DC solve with an SDD drain current independent of Vds (a pure current source behind a resistor) and a control-current loop
  did not move from its zero start in 1,500 iterations; with a Vds dependence it converges. Recorded, not investigated.

## A control-current loop's HB from a cold start: the line search vetoed it, the Jacobian was right (brief-hb-control-loop-cold-start)

- **The FD oracle found no Jacobian error.** At the DC seed and at the warm-started solution of the SDD example's bias loop
  (Kloop 200, 10 dBm) the oracle's relative metric read 4e-2 and 2.6e-2, every worst entry in the bias node's DC row against
  the drain's harmonics. Swept over its step, that error grows as 1/ε (3e-3 at ε = 1e-5, 4e-2 at 1e-6, 3.6e-1 at 1e-7): it is
  roundoff. The row holds the ~1e6 S `ForcedDc` puts between `vg` and `g`, so its F values are millions of amperes and a central
  difference resolves its 0.2-sized entries to ~1e-3 at best. Against the largest entry of its own row
  (`JacobianComparisonResult.MaxRowScaledError`, added) the analytic Jacobian agrees to 4e-7 at the seed and 5e-7 at the
  solution; with `J_cc` switched off the same metric reads 1.0. The relative metric's floor (1e-7 of the whole matrix's largest
  entry) cannot see this: the floor is 0.1 here, and the entries sit just above it.
- **The cause was the line search's norm.** The bias node is held through Rc = 1 mΩ and the loop multiplies the drain current
  into it by Kloop, so the node's row of F reads thousands of amperes per volt of error. The first Newton step from the DC
  seed raises the RF swing, the swing raises the average drain current by rectification, and that row's residual rises by
  hundreds of amperes; judged by ‖F‖₂ in amperes the line search cut λ to 1/128–1/256 and held it there for 100 iterations.
  At zero drive the same seed converges in two full steps at any loop gain, which is what pointed away from the Jacobian.
  Judged by the self-admittance-scaled norm (MC-25's), Rc = 1 mΩ at 10 dBm and 200 V/A converges in 7 iterations; Rc = 1 Ω
  took 25 under the plain norm and 6 under the scaled one.
- **The fix: a stalled control-current solve is solved again judging either norm** (`HbNewton.Solve`, `ScaledOrPlain`).
  Only for a solve with control currents, and only once the usual one stalls: 5 line searches running that each cut λ to
  1/32 or less. Waiting for the usual solve to fail first cost 1 to 1.3 s a point on the example's drive sweep, every point from
  10 to 20 dBm warm-started or not (its 100 iterations each evaluated 9 trials); a stall test on fully rejected searches alone
  missed 10 dBm at 50 V/A, which crawled at λ 1/64 to 1/128 and never stalled. When the retry fails too, the usual solve runs
  again from the same start with nothing cut short. Circuits without control currents are untouched by construction; a
  control-current circuit is changed only if its usual solve crawls for 5 iterations running (at 30 dBm the bias loop did, and
  converged anyway in 45 to 80; it now converges in 17 to 21 to the same answer).
- **Cold against warm:** Kloop 50, 200 and 1,000 at 10, 20 and 30 dBm all converge from cold in 10 to 21 iterations and equal
  the solution reached by raising Kloop from 0 to within 2.1e-5 V (Kloop 1,000 at 20 dBm; 4e-12 to 2.5e-7 V elsewhere).
- **R-hbcl-3, a failed point is NaN.** An HB point inside a parametric sweep that did not converge keeps `Converged` 0, its
  `Residual`, its tone frequencies and its seed cubes; every other cube is NaN (`ParametricSweepEngine.UnconvergedHbPoint`), so a
  measurement reads NaN there. The loadpull engine leaves a failed step's spectra out and its readers select converged steps
  by its `Converged` cube; an HB sweep's measurements read the spectra directly, so the spectra carry it. One existing test read
  numbers at such a point (`HbLinearNodeTests.T5_Sweep_FullNodes`, a point refused as a vacuous tolerance: 1e-4 A against an
  RF drive of 1.6e-11 A through a 1 H choke, returning its DC seed); it now checks the converged point and that the refused one
  reads NaN.
- **The example's bias bench** gained its HB drive sweep (10 to 30 dBm into bench 5's maximum-power load, loop open and
  closed): 22 points, 1.2 s from the CLI, every one converged; 7.3 s before the stall test.

## TRN series review (2026-11-16) — what the review of TRN-1…9 found in the engine and fixed

- **gear2 reused nothing and under-estimated its error after a rejection.** The step ratio rho = h/h2 was rebuilt from
  accumulated times, a few ulps off the ladder's power of two, so a0 (the stage cache's key) missed and factored afresh every
  few steps; rho is now snapped to its power of two. The corrector's error constant was 2/9 at every ratio, which holds only for
  equal steps: (1+rho)^2/(6 rho (1+2 rho)) is 0.375 at rho = 0.5 and 0.694 at 0.25, exactly the steps that follow a rejection.
- **A switch with Hyst could change state at t = 0+ with nothing changing.** The operating point is solved with the switch off
  (its window shifted Hyst/2); BeginTransient then read the UNSHIFTED u >= 0.5, a third rule. It now applies AcceptStep's rule
  under the operating point's window, and every transient device's cached evaluation is cleared after BeginTransient.
- **Transient-assisted HB:** a fallback seed the transient cannot run (an element it does not step, a failed operating point)
  keeps the DC start's best-available result with a note instead of failing the run (and, under a sweep, the sweep); the
  Fguess-free start-up ranked crossings by |W| AT the crossing, where every one scores -1, so the first won — it now uses the
  bracket's own samples; a series OscProbe's monitor compared its loop current in amperes with 1 % of the kick in volts, so a
  low-current oscillator was refused as never starting — the kick is carried over through |Z| at the time scale, floored at
  50 ohm.
- **SaveSteps under a parametric sweep** failed the whole sweep after its last point (each point accepts a different number of
  steps, so Steps.* cannot stack); dropped there with a note.
- **Step longer than the record, negative Start** are refused by Resolve (and therefore by check, which now runs Resolve:
  check.tran.keys); they gave a two-point grid, or were clamped, silently.
- **ngspice:** a rawfile ending short of Stop is refused rather than padded flat to Stop.
- **Lines and fits:** an SnP with no point in (0, f_max] is refused (an order-1 fit matched a line extrapolated from two
  points outside the band and was accepted); a tone termination past 128 bands is refused (a 10 MHz IF beside a 2 GHz LO was
  2,000 placed resonators and an O(K^4) passivity step with nothing checking Stop).
- **Envelope baseband line delay** (a TRN-4b regression): the baseband took the phase delay at 1 kHz, where the causal
  conductor loss's phase over omega grows as f^-1/2 — a lossy microstrip's baseband delay read 3-4x long. It is taken across
  the step's band now; a lossless line is unchanged.
- **Left as found, for a decision:** HbEngine takes no RunControl, so a transient seed cannot be stopped; an oscillator sweep
  re-runs the seed at every point; the step-floor stop discards the run and names no net; OP.V under ngspice with Start > 0 is
  the state at Start.

## PCH review (2026-11-16) — the envelope channel, drops, the receive collapse, beam selection

- **The receive collapse was wrong with elements Off.** The element side closes as a = Γ_ch·D·b, so (I − Γ_ch·S·D)·b = c and
  G = S12·(I − Γ_ch·D·Sᵀ)⁻¹·t: for the reciprocal array, M⁻¹·t, not M⁻ᵀ·t. With D = I they agree, which is why the 8b gates held.
  Gate: `ChannelArrayEndTests.Gate8b1_SParameters_WithElementsOff_…` (fails on the old form). The collapse key now carries the
  Off set (ArrayKey leaves it out), and both collapse caches clear past 512 entries.
- **Drop reuse replayed a relay's second hop.** Every channel was recorded and every recording replayed, so a channel transmitting
  from the receive side heard the recording drop's relay output. `Partition.DrivenChannels` limits the replay to channels whose
  transmit end is on the transmit side. A tone on the transmit side (an upconverter's LO) now refuses the reuse (the pruned run
  lost the second fundamental); a faulted recording entry is dropped so the next drop records afresh rather than every drop of
  that key failing; and a drop whose channel delay outlasts the recording's warm-up (`EnvelopeRunner.WarmCovers`) solves jointly.
- **A second channel into a replaced receive array lost its contribution.** The replacement now happens only when one channel
  receives on the array; nor when its channel cells hold a line already swapped to its stepped delay form.
- **An EM Field Data end crashed the envelope and `explain`** (`TxArray`/`RxArray` were a tuple with a null array): they are
  null for a non-array end now, and the envelope's refusal comes before anything reads them.
- **The channel report evaluated tap gains and H(f, t) at TimeOffset + t, without the run's Start** (the record's axis is
  0-based; the channel stepped from Start). `EnvelopeSpan.OffsetS` carries it.
- **Beam bound:** a non-steered end kept unit-modulus phases, losing its taper; it keeps its weights now. **The kept beam's key**
  timestamps a `path#port` pattern by its path.
- **Drop reuse is exact only with a linear receive side, and now refuses a nonlinear one.** The joint step's Newton and
  predictor acceptance are judged over every unknown, so the recorded transmit waves carry the recording drop's own receive
  iterations. Measured over 50 drops with an LNA (IP3 −30 dBm) after the channel: V to 2e-5 and I to 2e-4 of their peaks, AM-PM
  and Delay of a CW not at all. Each drop is then solved jointly and the run note names the part. Gate
  `DropReuseTests.ANonlinearReceiveSide_IsSolvedJointly`.
- **The genie through a channel now takes out what the channel does not hold.** It divided by the channel's H(f, t) and took out
  no offset at all (taking the whole fit out would remove the channel's Doppler twice), so a transmitter's or an LO's own offset
  stayed in. The residual is fitted on the symbols with H divided out (`SignalEqualizer.WithoutChannel`) and removed first; CFO_Hz
  is still the whole offset as received.
- **Left as found:** recordings are kept for every key for the whole sweep. `CollapsedElements`, `ReplacedParts`, `LastInstanceCost`
  are process-wide counters, so the 8b envelope test can read another class's run under load (`PatternEvaluations`, counted on
  the caller's thread, is per thread now, which settled `ReceiveArrayFeedTests.Gate3_…`).

## brief MTR-1…3 — a MOD_Source in native transient, kept at baseband (2026-11-16)

- **The drive** is a column of its own at Fc, taken by difference like a shaped tone. At each step it is multiplied by
  g(t)·b(RecordStart + t)·e^(jωt), with b read from a table that `ModulatedSignal.CompositeWindow` builds once (32 × the
  record's widest excursion from Fc, cubic between points). On the gate bench (LTE 5 MHz at 1 GHz into 50 Ω, explicit
  MaxStep = Step/5), the load voltage at RF rate equals ½·Re{Vs·b·e^(jωt)} to 1.5e-6 of Vs at RecordStart 0 and at 1 µs.
- **Interpolated output points are not good enough for downconversion.** At the automatic step (20 a period), the
  engine's quadratic interpolation between accepted steps is about 1e-3 of the carrier, and it lands in the envelopes.
  Baseband storage therefore steps on a whole subdivision of its RF grid (4(K+1) points a carrier period, at least 20
  steps a period of the highest frequency), so every grid point is an accepted step. With that, the harmonic-1 envelope
  is ½·Vs·b to 8e-6 of Vs and harmonics 0, 2 and 3 are below 8e-6.
- **A few µs of OFDM is not the record's unit mean.** Delivered power into the matched load was −2.05 dBm against
  Pavg = 0 dBm over a 3 µs window, and it equals 10·log10(mean |b|²) over that window. A test comparing a short window's
  power with Pavg is wrong, not the run.
- **EVM needs whole symbols** (about 70 µs for LTE). At the stepping above that is several million steps, so EVM is not
  in the routine gate. The envelope's own code reads it, and a window too short for a symbol is reported by that code.
- **The Transient preset** now takes only a real `V` cube. A baseband result's `V` is the envelope's complex
  [time, node, harmonic], and the envelope's displays read it.
- **The HB and oscillator seeds** set PlayModulation = false: a MOD_Source there is its CW tone at Fc, the steady state
  harmonic balance solves for.

## The transient seed answers Stop (2026-11-16)

`HbEngine` takes an optional `RunControl` (constructor), and `OscEngine.Run` takes one too. Both are used for
cancellation only and both hand it to the transient seed (`HbTransientSeed.Run`, `OscTransientSeed.Run`). The seed's
transient engine checks it every 256 accepted steps.
- CircuitEvaluation's HB and oscillator cases, HbCircuitRun and the parametric sweep's HB and oscillator points pass the
  run's control.
- The UI already had the control: the toolbar Stop and the Messages live row's Cancel are one `RunCancellation`.
- Until now that control was honoured only at a sweep-point boundary, so a single HB point waited out a 200-period seed.
- The Newton solve and the drive ramp are still not interrupted. `src/Engine/CLAUDE.md`'s "HbEngine … take no control"
  now means "checks it only inside its transient seed".
- Gate: `TransientSeedTests.AStoppedRun_EndsTheSeed`.

## The step floor keeps what the run did (2026-11-16)

A step that fell below `MaxStep / 2^40` threw, and every accepted step went with it. Design note §4.3 always said the run
stops at that time with what it has.
- **What the run keeps now:** the output up to that time, plus `Tran.StoppedAt_s`.
- **The warning** names the last failure. That is the Newton failure if there was one, kept over a later rejection that
  had no failure of its own (the old code overwrote it with ""). Otherwise it is the net and devices with the largest
  local error, now tracked by both error estimates, or "the solution was not finite" (the linear path used to report a NaN
  as a tolerance problem).
- **Convergence:** CircuitEvaluation reports such a run as not converged.
- **Still refused:** a run with no output point yet, and the HB and oscillator seeds (`KeepWhatWasDone = false`), since a
  seed cut short is no steady state.
- **Gate:** `TransientEngineTests.AtTheStepFloor_TheRunStops_KeepingWhatItHad`. A drive that turns non-finite after 0.5 µs
  stops the run at 5e-7 s with its output up to there.
