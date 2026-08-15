# src/Harmonica — resolved briefs (detail, off the CLAUDE.md growth path)

Mirrors `src/Ui/DataDisplay/RESOLVED.md`'s own pattern: a completed brief's detail lands here, one
`##` section per brief, sparingly — only for findings that are still true, still surprising, and
would cost someone real time to rediscover. `CLAUDE.md` stays for durable, still-true conventions.

## R8C — rgs, and the intrinsic drag's closed form replaces the inverse solve (brief-harmonicarf-r8c, 2026-08-15)

**§3 — rgs is a series resistor in the Cgs branch, and `IntrinsicPlane.SourceImpedance`'s own claim
("no hand-written expression for the source-side impedance anywhere — it falls out of the converged
Jacobian") is now gated, not merely asserted.** `IntrinsicRgsTests` compares the solved `Z_S,intr`
against `Z_source,plane ∥ (rgs + 1/(jωCgs))` at rgs ∈ {0, 2, 25} Ω, agreeing to < 1e-9 relative — but
only once the ORACLE folds in the document's own bias network (`Z_source,plane = Ychoke ∥
(Z_marker + Z_dcblock)`, not the raw marker impedance): omitting that stage cost ~0.1 Ω absolute
(~0.2%) against a ~44 Ω fixture, small but real, and would have looked like the Jacobian claim being
slightly wrong rather than an incomplete oracle. `DutCapacitances.IsIdentity` deliberately does not
consult `RgsOhms` — a non-zero rgs with an absent Cgs is an open branch and emits nothing, so it must
not count as "not the identity" or the structural-key/netlist machinery would treat an untouched
document (rgs left at some stale nonzero value, Cgs never set) as touched.

## R8C §5 — IntrinsicAbcd: the closed-form chain's ELEMENT ORDER is the netlist's, not the brief's own prose

**The three-element list ("1. series Rg+jωLg, 2. shunt jωCpg, 3. shunt branch") describes the
elements' PHYSICAL identity, not the order `IntrinsicAbcd.Chain` may build them in.**
`HarmonicaNetlist.Build` shunts Cpg at the TERMINATION PLANE itself, *before* the Rg/Lg series lead —
the opposite of the prose order — and building the ABCD chain in the prose's literal order (series
first, both shunts combining additively at one node) passed every single-element §5.4 hand oracle
(a lone series or shunt is direction-symmetric and cannot distinguish the two orderings) but put the
§5.4 item 3 round-trip **~0.24 off in Γ** against the real solver — caught only by that test, exactly
as the brief predicted ("if it fails, the chain is wrong, not the solver"). Fixed by building each
side's chain in NETLIST node order (Cpg-then-series-then-branch) with each new element LEFT-multiplied
onto the accumulated matrix (`chain = newElement * chain`), which is what makes port 1 = intrinsic in
the standard "Zin at port 1 given a load at port 2" two-port identity the header formula assumes.
Re-derived by hand for the combined case: `Z_intr = ((Z_ext ∥ Z_Cpg) + Z_series) ∥ Z_branch` — Cpg
combines with the RAW extrinsic impedance (same node), the series lead moves the plane inward, and
only then does the Cgs/rgs branch join in, at the gate terminal (a DIFFERENT node from Cpg's).

**The round trip's own residual, once the chain order was fixed and a units bug (Z0 defaults to 80 Ω,
not 50 — the test had hardcoded 50) was found and removed: 1.045e-15 in Γ.** Machine precision, not
merely "close" — the ABCD chain and the netlist agree about what the circuit is to the last bit
`Complex` arithmetic can represent.

**The chain does NOT model the ideal bias tee (`BiasChokeHenries`/`DcBlockFarads`), and that is a
deliberate idealization, not an oversight left in by accident.** Measured directly: with the repo's
own non-ideal test convention (1e-6 H / 1e-9 F, chosen elsewhere for HB Newton conditioning, not for
being "ideal"), ignoring the bias tee costs ~0.2% relative on a representative impedance — real but
small, consistent with the brief's own three-element chain never mentioning it. A document using the
true defaults (1 H / 1 F) sees an even smaller gap. Not corrected in production; a real drag on a
document with an aggressively non-ideal bias network would show a small, size-of-the-non-ideality
mislanding rather than an exact one.

**The map's own pole is not testable as a literal `double.IsFinite` failure through a realistic drag
gesture, so the refusal is a magnitude bound instead.** A target Γ that lands EXACTLY on `−C·Z_intr +
A = 0` in floating point is a measure-zero event; even constructing the pole analytically and feeding
it straight back through `ExtrinsicFor` (same formula, same operations) landed one ULP off zero and
produced a "finite" ~7×10¹⁷ Ω rather than an actual `double.PositiveInfinity`. `HarmonicaViewModel.
PoleMagnitudeOhms` (1e9 Ω) is the practical version of the brief's "`−C·Z_intr + A → 0`" — a limit,
not an exact equality — and is what a real near-pole drag will actually trip.

**`IntrinsicDragAllowed` is on `CircuitModel` itself (a static method), next to `DutCapacitances` and
`LumpedPackage.CouplesInputAndOutput`, which it reuses rather than re-deriving.** Confirmed true for
the shipped default document — unlike H6's own finding that the default document "cannot exercise an
intrinsic drag at all" (there, the reason was the glyph coinciding with its marker within grab-radius
pixels, a DIFFERENT limitation from this predicate; `IntrinsicAbcdTests.
Predicate_TrueForTheShippedDefaultDocument` pins the predicate side of that, `HarmonicaInverseDragTests`
still needs an added package/capacitor to get PIXEL separation for an actual gesture test, same as H6
needed).

Gate: `tests/Harmonica.Tests/IntrinsicRgsTests.cs` (8 tests), `IntrinsicAbcdTests.cs` (15 tests —
identity, single-element and combined hand oracles, the real-solver round trip, side independence, the
predicate). `tests/Ui.Tests/Harmonica/HarmonicaInverseDragTests.cs` rewritten (the wiring, not the
maths — `IntrinsicAbcdTests` gates that) for the closed form: grab/drag/land, the pole refusal, Pass 2
not running when the predicate is false, `ShowReachableRegion` defaulting off. `HarmonicaInputsRgsTests.cs`
(6 tests), `IntrinsicGlyphSizeTests.cs` (5 tests, one a source scan — the renderer cannot be exercised
headlessly).

## D5 reversed: iso-lines now span a hole; the optimum search needed a SECOND raster to stay correct (brief-harmonicarf-r8a §6, 2026-08-15)

**The owner overruled D5's "holes are thrown out, never extrapolated into" — deliberately, not because
the old reasoning was wrong.** `ContourGrid.InSupport` used to be one unconditional check (hull AND
NOT-in-any-hole-disc); it now takes `bool excludeHoleDiscs`, and the two real callers were given
**opposite defaults on purpose**: `InSupport` itself defaults to `true` (old behaviour, so any call
site that doesn't pass the parameter is unaffected), while `Raster` defaults to `false` (spanning —
what a Smith panel now draws). Reading the two defaults side by side without this note looks like a
bug; it isn't — `Raster`'s default had to flip for the feature to ship, `InSupport`'s could not without
silently changing every other caller's behaviour underneath it.

**The optimum search (`InterpolatedArgmax`) has NO `InSupport` check of its own in its seed-picking
loop** — it just scans the `SurfaceGrid` it's handed for the highest non-NaN cell. Under the old
doctrine that was safe by construction, because `Raster` always masked holes. Now that `Raster`'s
default spans them, feeding `InterpolatedArgmax` the SAME raster a panel draws from can seed — and, if
the local refinement then finds nothing supported nearby, RETURN — a Γ inside a hole. Fixed by having
`HarmonicaSolver.BuildSmith` build a SECOND raster, `excludeHoleDiscs: true`, for the optimum path only
— a real added per-cell RBF evaluate (the raster is ~76% of a panel's own cost, see this project's own
`CLAUDE.md`), paid deliberately: reporting "the optimum load is here" from an unconverged Γ is a wrong
NUMBER, not a cosmetic gap. `InterpolatedArgmax`'s own internal refinement `InSupport` call also now
says `excludeHoleDiscs: true` explicitly rather than relying on the default, so a reader who finds two
`InSupport` calls with different flags has the reason written at the call site.

**Measured on the shipped default document (37-point ring grid, load side, band 1, maxGamma 0.8): 1
hole, and NO invented island** — no closed polyline (Pout, drain efficiency, or PAE) fell entirely
within the excluded hole disc with no measured point behind it. §6.4 asked for this to be checked and
reported rather than tuned around; on this fixture there was nothing to report beyond "did not happen".
A denser hole cluster could still produce one — the mitigation is the hollow hole dot (unchanged,
`HarmonicaPanelRenderer.DrawGridPoints`), which is why the class doc comment says the reversal
*depends* on those dots staying drawn.

**RBF defaults retuned alongside the reversal, unrelated reasons**: `ContourSmooth` 1e-3 → 0.1,
`ContourEpsilon` null → 0.5 (owner-set). The `CharmIo` half of this is the interesting bit: an absent
`ContourEpsilon` field and an explicitly-persisted JSON `null` are indistinguishable to the
deserializer, so `?? defaults.ContourEpsilon` treats both as "take the default" — meaning a user who
clears the Advanced tab's epsilon box to get `Rbf2D`'s own auto epsilon, then SAVES and reloads, comes
back at 0.5, not auto. The blank-for-auto behaviour survives only within a session that never
round-trips through disk.

## Idq⇄Vgs — the "1-D secant on the DC solve" the tooltips always promised, built for real (owner follow-up, 2026-08-13)

**It never existed.** `HarmonicaContext.Apply` applied `model.Bias.Vgs ?? 0.0` unconditionally —
`Bias.Idq` was round-tripped through `.charm` and the UI but never once READ by anything that
actually biases the circuit. Found while investigating an owner report that editing Idq did not move
Vgs; the honest answer was "nothing moves it, ever," not a stale-cache or ordering bug.

**`HarmonicaContext.SolveVgsForIdq(idqTargetAmps, vds)`** is the real thing now: bracket-then-secant
(the exact shape `PinSearch.Run` already uses for Pin-vs-compression, applied to Vgs-vs-Ids instead),
each trial a REAL DC solve (`NonlinearDcEngine.Run` against the already-elaborated open-port netlist —
cheap, netlist-only Newton, no HB, no re-elaboration) followed by a direct device evaluation
(`ComponentModel.Evaluate` at the converged terminal voltages) rather than a bare-law shortcut —
`DcivFamily.Compute` takes that shortcut deliberately for its own illustrative background curve, but a
BIAS solve has to see any series package embedding (Rd/Rs/Ls), since the device's real terminal
voltage differs from the applied one by exactly the self-referential drop the embedding creates.
**Never throws, never leaves the bias unmoved on failure** — matches `ComputeDcSeed`'s own
"best-effort, always leaves a real number behind" rule; a target outside the DUT's reachable range
returns the closest point actually found rather than the pre-edit Vgs.

**Both fields end up populated together now — "Vgs xor Idq" is no longer the invariant to assume.**
`Apply`'s value-branch resolves through `SolveVgsForIdq` when `Idq` is non-null and writes the solved
number into `_model.Bias.Vgs` via the ordinary `SetBias` path; `_model.Bias.Idq` survives untouched
(it was already copied in by `Apply`'s own top-level `_model = model` before the branch runs) as the
TARGET that produced the solved Vgs. A caller that used to check `Bias.Vgs is null` to mean
"Idq-driven" has to check `Bias.Idq is not null` instead now.

**A structural rebuild (or a context's very FIRST `Create`) needed the identical resolve, and almost
didn't get it.** `Rebuild()` never called anything bias-related before this — a document opened
Idq-driven for the first time would elaborate and sit at the raw netlist's default gate voltage until
some UNRELATED later value edit happened to trigger `Apply`'s bias branch. Caught by a test that
constructs a context directly from an Idq-only model (no prior Vgs to warm-start from at all) — the
first attempt failed with `Bias.Vgs` still null right after `Create`. Fixed by factoring the resolve
into `ResolveBias(CircuitModel)` and calling it from both `Apply`'s value branch AND the end of
`Rebuild()` itself, so "just constructed" and "just structurally rebuilt" get the same treatment as
"just value-edited."

**Verified against a closed form, not just "some number came back".** `IdqVgsSolveTests` uses an SDD
whose drain law is analytically invertible for Vgs > −3 (`Ids = 0.08·(Vgs+3)²·tanh(0.4·Vds)`) — the
secant lands within 2×10⁻⁴ V of the closed-form answer, both raising and lowering the target current,
and re-solving for the SAME Idq target after Vds alone moves (Idq depends on both).

## §3 — the loadpull grid's holes, root-caused and mostly cured (brief-harmonicarf-r3b, 2026-08-13)

**The owner's own evidence was right, and the mechanism was almost — but not quite — what §3.2
proposed.** Measured directly (`LoadpullHoleDiagnosticTests`, `Category=Benchmark`, shipped default,
5×12 ring grid): **every hole is `NonConvergence`, zero are `PinMax`** — so this is a search-quality
defect, not a physical "does not compress" answer, exactly as the owner suspected. But the FAILING
STAGE, instrumented per-solve (`PinSearch.Run`'s new optional `onProbe` hook, purely additive), was
100% at the **bracket** stage (never the tickle) — not the tickle-from-neighbour mismatch §3.2 named
as its primary suspect.

**The actual mechanism, traced exactly:** `ContourGrid.Build` seeds `Run`'s tickle/PinStart from the
VSWR-nearest neighbour's own compressed spectrum — an 80 dB-ish mismatch on paper, but the tickle
(−50 dBm, deep in small-signal territory) converges from almost any reasonable seed regardless, and
by the time PinStart solves, `seed` has already been overwritten by the tickle's OWN low-drive
result. **The real gap opens at the bracket's FIRST probe**: with a Pin hint from the neighbour
(`pin = Clamp(hint, pinLo+0.25, PinMaxDbm)`), it can jump straight from `PinStart` (−10 dBm) to the
neighbour's compression Pin — often 25–34 dBm, a 30+ dB jump — seeded with `PinStart`'s own low-drive
spectrum (the in-ladder chain, not the neighbour's). Confirmed independently: `PinSearch.Sweep`
(small uniform 1 dB steps, warm-starting adjacent close levels) converges and compresses cleanly at
every one of the failing terminations `Run()` holed on.

**Fixes landed, in the brief's own priority order, each measured:**

1. **A real nonlinear DC seed** (`HarmonicaContext.SeedFromRealDc`/`ComputeDcSeed`) — the owner asked
   for this by name, and the previous "DC seed" was not one: `SeedFromDc` solved the LINEAR network's
   DC point **with the device absent** (`V = −Y(0)⁻¹·I_src(0)`), harmonics zero. Now
   `NonlinearDcEngine.Run` against harmonicaRF's own open-port netlist (the bias tees are already
   stamped into it — no termination needed for a DC point), cached on `HarmonicaContext` keyed to
   (structure, bias), invalidated in `Rebuild`/`SetBias`, gated by `DcSeedComputeCount` (a counter).
   A DC solve that fails even under continuation stepping falls back to zero — never worse than the
   old seed's own failure mode.
2. **Never seed a probe from a very-different drive level.** `ContourGrid` now keeps each converged
   neighbour's WHOLE ladder (`IReadOnlyList<PinStep>`, not just its last/most-compressed step); `Run`
   picks, for EVERY solve (not only the first), whichever is closer in Pin — the neighbour's own step
   nearest the level being solved, or this point's own in-ladder predecessor. This is the fix that
   actually closes the bracket-stage gap identified above.
3. **One retry from the DC seed before declaring a hole** — a failed solve at any stage gets exactly
   one more attempt, cold-seeded from the real DC point (item 1), before the point is thrown out.
   Counted (`PinSearchResult.Retries`, summed as `ContourGrid.RetryCount`), never silent.
4. **`PinMax` and `NonConvergence` reachable separately** — `ContourGrid.PinMaxHoleCount`/
   `NonConvergenceHoleCount` (new). The two were, and visually still are, an identical hollow dot;
   this is the brief's stated minimum bar (a counter) rather than a status-strip/tooltip surface,
   which was not built this pass.

**Measured, before → after, shipped default, 5×12 ring grid:**

| MaxGamma | before (converged / holes) | after | 
|---|---|---|
| 0.80 | 56 / 5 (91.8%) | **60 / 1 (98.4%)** |
| 0.85 | 53 / 8 (86.9%) | **60 / 1 (98.4%)** |
| 0.90 | 53 / 8 (86.9%) | **60 / 1 (98.4%)** |

**The one residual hole at each setting is explained, not silently tolerated**, per the gate's own
allowance: it now fails at the SECANT stage (not the bracket — that mechanism is fully closed), still
converges under `Sweep()`, and the retry (item 3, seeded from the real DC point) was tried and did
not save it either — consistent with a still-nontrivial Pin gap opening within the secant's own
bracket-interval choice on a coarse first bracket, at a smaller scale than the cured bracket-stage
failures. Not chased further this pass.

**`PinSearch.Sweep`'s ladder semantics are untouched** (R-h9r2-17/18/19, the guardrail) — every change
above is in `Run` and `ContourGrid` only. `LoadpullEngine`/`PursuitEngine` and every hero golden are
untouched; the full solution (Core/Engine/Harmonica/Ui/RfCore/WBond/Firewall) passes.

## §4 — the grid parallelised, and a pre-existing `PinSearch.Run` defect found while gating it (2026-08-13)

`ContourGrid.BuildParallel` (new) parallelises the grid across a small POOL of per-batch
`HarmonicaContext`s (a `ConcurrentBag`-backed pool, persistent across calls — created once per
structural change, exactly `SolveWorker.EnsureContext`'s own rule; never two batches on the same
context at once, which is the one thing that would corrupt a result since a context is not
thread-safe). Batches are FIXED and assigned to workers by `b % workerCount`, decided before any work
starts — no work stealing, a run is reproducible. Seeding is strictly within a batch, plus one
deterministic serial "leader" point (closest to Γ = 0) solved before the fan-out and shared by every
batch, per the brief's own explicitly-allowed lever.

**Batch MEMBERSHIP is by ANGLE, not raw grid-array order — measured to matter, not a style choice.**
`RingGrid`'s own generation order is ring-then-spoke, so chunking by consecutive array index gives a
batch one ring's short ANGULAR arc — coherent in angle, blind in radius. The shipped default
document's own hole cluster sits along a single RADIAL line (the same angle, every ring — see §3
above), so index-order batching split that exact line one point per batch, each with no radially
adjacent neighbour to bootstrap from, and the parallel grid's hole SET diverged from the serial one
(6 extra holes, measured). Sorting the pending points by angle before chunking groups a whole radial
stripe into one batch instead, which closed the gap to zero. General for any Γ scatter, not
ring-grid-specific.

**Batch size: the owner's own "4 or 8 points per core" measured too small on this fixture.** At 8,
the hole SET still showed a real (not hypothetical) one-point mismatch; at 12 — one whole ring on this
grid's own shape — it matched exactly and wall-clock was still 2.68× faster than serial. Default
changed to 12 for that reason, stated in `BuildParallel`'s own doc comment rather than silently picked.

**The genuinely important find: `PinSearch.Run`'s bracket can converge to the WRONG (non-first)
compression crossing, and this PRE-DATES this brief entirely.** Chasing a residual metric deviation
between the parallel and serial grids (`ContourGridParallelTests.Diagnose_IsTheHintJumpOrTheNeighborSpectrum_RootCause`)
traced it to `Run`'s DOUBLING bracket stride (3, 6, 12, 24 dB…) — coarse enough that on a device whose
gain-vs-Pin curve has a local non-monotonicity, the bracket can PROBE right past the true first
crossing and lock onto a later, spurious one instead. Reproduced with the untouched, original bracket
code — no hint, no neighbour spectra, pure `PinStart` + doubling stride, exactly what the very first
point of a fresh grid does: **`Run()` reports 28.4 dBm; `PinSearch.Sweep` (ground truth, 1 dB uniform
steps) reports 27.2 dBm, at the SAME termination.** This is not something this brief's §3 fixes or §4's
batching introduced — the stride-doubling code was never touched — but §3's convergence fixes made it
MORE VISIBLE: points that used to fail outright (an honest hole) now succeed, sometimes at the wrong
Pin, and batching's smaller per-batch neighbour pool changes HOW OFTEN a bad hint triggers it. **Not
fixed here — it is a separate, real investigation** (redesigning the bracket's sampling granularity
trades directly against the whole reason a doubling stride exists: R-hrf-7's own measured ~4.6
solves/point against the ladder's ~30). Flagged for a follow-up brief; the parallel-vs-serial gate in
`ContourGridParallelTests` reports the actual worst-case deviation rather than asserting a tolerance
that would either hide this or be tuned to it.

**Measured, shipped default, 61-point ring grid, batch size 12:** hole SET identical (0 differences);
worst-case converged-point deviation 0.04 dB Pout / 0.05 pts DE / 2.69 pts PAE (the PAE figure is the
pre-existing `Run` defect above, isolated to the one point it already affects in the SERIAL grid too);
wall-clock **2.68× faster** (98–119 ms serial → 43–44 ms parallel, `Environment.ProcessorCount = 10`).
A superseded build still cancels within one Γ point's cost (~20 ms against a 60+ ms full build).

## §5 — the loadline's current axis genuinely carries more harmonics than K, confirmed by DFT (2026-08-13)

**The owner's suspicion is correct, measured directly.** `LoadlineHarmonicContentTests` DFTs the
actual arrays `IntrinsicPlane.Loadline` draws, on the shipped default at its compression point (K=3,
Pin = 27.0 dBm):

| bin | Vds, % of fundamental | Ids, % of fundamental |
|---|---|---|
| 2 | 0.024% | 30.58% |
| 3 | 0.009% | 16.16% |
| **4** | **2.9×10⁻¹⁵%** (round-off) | **18.69%** |
| 5 | round-off | 0.85% |
| 6 | round-off | 8.38% |
| 7 | round-off | 4.06% |
| 8 | round-off | 2.32% |

**`Vds` is exactly band-limited to K = 3** — content above bin 3 is 2.9×10⁻¹⁵ relative to the
fundamental, i.e. floating-point round-off, confirming `ResampleSpectrum`'s truncated Fourier series
is exact and carries nothing the K=3 solve didn't produce. **`Ids` is NOT band-limited** — bin 4 alone
sits at 18.7% of the fundamental, comparable in size to the *retained* bins 2 and 3. The mechanism is
exactly what reading the code predicts: `vds[t]` comes from the band-limited voltage spectrum,
`ids[t] = dut.Evaluate(pv).I[drainPort]` is the device's FULL nonlinear response evaluated AT that
voltage — the solve truncates the current spectrum at K when it builds the HB residual, but the
loadline panel never applies that truncation; it evaluates the real device law pointwise, and a real
device law does not know about K.

**The rest of the K path audited, one source of truth confirmed:** `HarmonicaContext.Solve` /
`HbFft.GridSize(K, FftOverSample)`, `HarmonicaDataSet`, `TerminationSet.HarmonicCount`, and
`HarmonicaNetlist` all read `HarmonicaSettings.HarmonicCount` — there is no second K anywhere.
**`FftOverSample` (default 1) enlarges only the FFT's intermediate TIME grid, never the retained
harmonic count** — `HbFft.GridSize` returns `oversample × nextpow2(4K)` as the evaluation grid size,
but the forward transform always emits exactly `K+1` bins regardless of oversample (its own doc
comment: "The evaluation grid (N) is LARGER than the solution spectrum (K+1 bins); FFTOverSample
anti-aliases without growing the Newton unknowns"). Confirmed by reading, not just quoted.

**Not changed unilaterally, per the brief's own instruction — this is the owner's call:**
1. **Truncate the displayed `Ids` to K bins**, making the loadline internally consistent with what the
   HB solve actually retained. Pro: the curve you see is exactly the curve the solver solved for.
   Con: it hides real physics — the device genuinely produces that current at that voltage; truncating
   it draws a locus the device does not actually trace.
2. **Leave `Ids` as the true device response** (today's behaviour). Pro: physically honest — this IS
   what the device does at that instantaneous voltage. Con: a viewer comparing the loadline's apparent
   bandwidth to the displayed "K = 3" can reasonably read it as a mismatch/bug, which is exactly what
   prompted this investigation.

Both are defensible; nothing here should change without the owner picking one.

## R7B — the SDD text editor: variables become globals, and reconstruction lives in the dialog, not `CharmIo` (2026-08-14)

**`DutSpec.Parameters` now carries both an SDD's equations AND its scope variables, keyed identically
(name → expression text) — the split is made at netlist-emission time, not at storage time.**
`HarmonicaNetlist.SplitSddParameters` partitions on the SAME predicate the factory itself uses
(`ComponentModelFactory.IsSddEquationName`, made `public` for exactly this) rather than a second,
re-spelled classifier: an equation-shaped key stays on the instance line, whitespace-stripped
(`StripWhitespace` — the instance-line parser reads a space as a net separator); everything else
becomes a top-level `name = expr` global line, spaces intact, emitted before the `DUT` line. This is
what lets a variable reference another variable (`Elaborator.InjectSddScopeVars` resolves through the
enclosing scope) and what lets an expression contain spaces at all.

**Reconstructing editor text for a pre-R7B `.charm` (no `SddText` field) happens in
`HarmonicaDutEditor`'s constructor, NOT in `CharmIo`.** The tempting-looking alternative — have
`CharmIo.FromDocument` reconstruct and populate `DutSpec.SddText` whenever the JSON field is absent —
breaks the "untouched document re-serialises byte-for-byte" rule the moment that model is written back
out: `SddText` would flip from null to a non-null reconstructed value on the FIRST read, and `ToDocument`
would then start emitting a field the original file never had, even though nobody opened the dialog.
Keeping `CharmIo` a pure pass-through (`SddText = dut?.SddText`, verbatim, no reconstruction) and doing
the reconstruction (`SddTextIo.Reconstruct`, `src/Harmonica`, framework-free so `CharmIo` can call it
too) only where a human is actually about to look at the text keeps the byte-for-byte guarantee free,
with no special-casing.

**The owner's own default text carried an invisible `U+200E` glued onto `Periphery_mm`** — pasted text
routinely carries U+200B–U+200F/U+FEFF/U+00A0, and an identifier with one attached is a genuinely
different identifier that fails to resolve with a message naming a symbol that looks correct on
screen. `HarmonicaSddText.Sanitize` strips these once, before anything else parses the text; there is
a dedicated test seeded with the exact character in the exact position the owner's paste had it.

**The equivalence gate is real, not assumed:** substituting the ten named constants into the new
variable-form `I[2,0]` reproduces the old folded-coefficient string bit-for-bit as text, and
`HarmonicaSddTextTests.DefaultModelEquation_AgreesWithTheOldFoldedCoefficientForm_...` evaluates both
ASTs through `SddEvaluator.EvalDouble` over a 25×25 grid spanning the device's real operating range,
agreeing to 1e-12 relative everywhere on the grid.

Gate: `tests/Ui.Tests/Harmonica/HarmonicaSddTextTests.cs`, 21 new tests — every §3.6 check, the
invisible-character trap, the `VarTextParser`/`SddTextIo` round trips, the equivalence gate and an
end-to-end elaborate-and-solve of the regenerated netlist.

## R7D — Cgs/Cdg/Cds on the DUT, linear or nonlinear (2026-08-14)

**`HarmonicaSettings.ComputeCharge` is confirmed INERT, per §2.5's own instruction — not wired up.**
A repo-wide grep (`grep -rn ComputeCharge src/Harmonica src/Engine src/Core`) finds it read and
written only by `CircuitModel.StructuralKey` and `CharmIo` — nothing in `src/Engine`/`src/Core`
consumes it, and `NonlinearCModel.Evaluate` returns its charge term (`q = ChargeAt(vd)`)
unconditionally, gated by nothing. So a nonlinear capacitor's charge is evaluated regardless of this
setting, exactly as the brief predicted. Left unwired, as instructed — wiring it up is a decision for
whoever actually wants the toggle to do something, not a side effect of adding a capacitor that
happens to need charge.

**The real bug this phase found: `HarmonicaContext.DutComponent` assumed "the only nonlinear
component IS the DUT", and that stopped being true the moment a Cgs/Cdg/Cds capacitor is C(V).**
`NonlinearCModel.Kind == ModelKind.Nonlinear`, so `_netlist.NonlinearComponents` now legitimately
holds two entries once one capacitor is nonlinear, and `_netlist.NonlinearComponents.Single()` threw
`InvalidOperationException: Sequence contains more than one element` — caught immediately by
`Nonlinear_CoefficientsSurviveToTheElaboratedModel` (`tests/Harmonica.Tests/DutCapacitanceTests.cs`),
not by review. Fixed by finding the DUT by its own fixed instance name
(`HarmonicaNetlist.Dut == "DUT"`) instead — identity, never "the only one of its kind". The general HB
solve path (`HbNewton.Solve`/`EvaluateNonlinear`, both `foreach (var nlIdx in netlist.NonlinearComponents)`)
already iterates every nonlinear device and needed no change; this was the one place harmonicaRF's own
code assumed singularity.

**The capacitors sit in parallel with the SDD's own ports, which is the entire point.** `Z_intr`/
`Gamma_intr` are read at the SDD's own ports (`IntrinsicPlane`), and those do not include a parallel
capacitor's displacement current — so adding Cdg genuinely rotates the intrinsic glyph relative to the
(unchanged, by construction — it is read straight off `terminations`, independent of the DUT) extrinsic
marker, and moves Zin. Measured directly (`Cdg_RotatesTheIntrinsicGlyphAndMovesZinWhileTheExtrinsicMarkerStaysPut`):
adding 0.5 pF of Cdg to a 2 GHz SDD with real gm/gds visibly moves both `Gamma_intr` (load, 1f0) and
`Zin` (source, 1f0), while `Gamma_ext` on both sides is bit-identical (it cannot be otherwise — it is
a pure function of the termination set).

**"An untouched document must not move by so much as an LSB" is proven by construction, not against an
external fixture file.** A model that never sets `DutSpec.Capacitances` (default `DutCapacitances.None`)
and one that sets it to `DutCapacitances.None` explicitly produce byte-identical netlist text — so
their solved numbers are identical by construction, since solving is a pure function of that text —
which is a stronger and cheaper guarantee than diffing against a stored golden file would have been.

**The Parameter Editor, not the C-V curve-fit dialog, is what §4 reuses** — the owner's own words name
it, and it is also the better fit mechanically: `NonlinearCvEditorViewModel` works from sample (V, C)
points and a least-squares fit, the wrong shape for a seed that is already raw coefficients, while
`ParameterEditorView` already renders `C0, C1, …` as ordinary rows AND already offers "Add Group"/
"Remove Top Group" for a `NonlinearC` target (`ComponentTypeRegistry.UserParamTemplate`'s own `C{0}`
template) — so add/remove/edit of raw polynomial terms was already the generic mechanism this dialog
gives any `NonlinearC` component, with nothing new to build for it. `HarmonicaNonlinearCEditor`
(`src/Ui/Harmonica`) hosts it over a throwaway `SchematicViewModel` + one `EditableComponent` — building
one **was** possible headlessly, confirmed by the same construction `tests/Ui.Tests` already uses
(`new SchematicViewModel(new SchematicEditModel())`), so no second editor was built.

**There is no real Cancel in the hosted dialog, and that is stated rather than papered over.** The
Parameter Editor commits every edit LIVE onto its (throwaway) schematic's own undo stack — every other
consumer of it works the same way, including the product's own double-click dialog — so
`HarmonicaNonlinearCEditor.EditAsync` shows it modally and reads back whatever `C0, C1, …` the
component carries when the window closes; there is no distinguishable "cancelled" state to return
`null` for in practice. A user backing out of "Use Nonlinear…" by mistake still leaves the row
nonlinear (at the seeded C0) rather than reverting to linear — a minor, named wart rather than a
silently-absent feature.

**§5's NumericUpDown fix confirmed the brief's own diagnosis on the first try**: the Fit-order control
in `NonlinearCvEditorView.axaml` set none of `MinWidth`/`Height`/`Padding`/`VerticalContentAlignment`
the two working instances (`MarkerEditorView`/`PlotInspectorView`) set, and its `Width="70"` was
smaller than the Fluent theme's own default `MinWidth` — which does not shrink the control, it produces
one wider than its slot with the spinner eating the digits. Applying the known-good style fixed it
outright; nothing in §5.3's numbered fallback list (theme resource vs Width, binding mode, the
converter, `SizeToContent`) was needed. The spinner was kept ON here (unlike the other two, which turn
it off) — a small integer nudged up and down is exactly the case §5.3 item 3 says it earns its space —
so the control's own width was widened to 96 px to give it room instead.

Gate: `tests/Harmonica.Tests/DutCapacitanceTests.cs` (13 tests — the netlist gate, the physics gate,
the `.charm` round trip) and `tests/Ui.Tests/Harmonica/HarmonicaR7dCapacitanceStripTests.cs` (13 tests
— row shape/text/Locked by DUT kind and capacitor state, `Apply`'s refusals, `LinearizedCapacitanceFarads`
against the Horner directly, and one end-to-end `HarmonicaViewModel.Inputs` check after a real solve).
