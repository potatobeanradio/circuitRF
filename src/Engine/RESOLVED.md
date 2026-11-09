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
`GammaOpt` (on Re Z0 of port 1), each [freq], through `RfCore.Data.NoiseMetrics`. Off, nothing is entered: the same
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
  missing. A coupled group with members at different temperatures is taken at the hottest (marked `ponytail:`).
- **Temperature** is `Temperature.ResolveDeviceC` per part (Temp, then Dtemp, then the ambient — 300 K by default, so
  a design stating nothing is not at T0 = 290 K).
- **Noiseless.** Ports and independent sources by definition. Nonlinear devices, active or black-box linear parts
  (a VCCS, an op-amp, a negative resistor), external devices and S-parameter blocks have no noise model here and are
  named in one `sparam-noise-noiseless` warning.

Gates: `tests/Engine.Tests/Linear/SParamNoiseTests.cs` against closed forms (a matched pad at 290 K and 400 K, Bosma's
`kT(I − SSᴴ)` entry by entry on a lossy reactive network, coupled lossy inductors and an ideal transformer) and
`testdata/noise/refs.json` (`gen_noise_refs.py`: numpy nodal and ngspice 47 `.sp … donoise` at 290 K — mixed `Temp=`,
a Mutual, a VCCS amplifier with 75/50 Ω ports: Cs within 2e-13); serial = parallel bit for bit; a parametric sweep stacks the cubes;
`circuitrf sparam … -o x.s2p` writes the noise block.
