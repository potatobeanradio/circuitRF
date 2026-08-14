# src/Harmonica — resolved briefs (detail, off the CLAUDE.md growth path)

Mirrors `src/Ui/DataDisplay/RESOLVED.md`'s own pattern: a completed brief's detail lands here, one
`##` section per brief, sparingly — only for findings that are still true, still surprising, and
would cost someone real time to rediscover. `CLAUDE.md` stays for durable, still-true conventions.

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
