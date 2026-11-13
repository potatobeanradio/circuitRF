# RfCore — resolved findings (detail, off the CLAUDE.md growth path)

Same pattern as the other `RESOLVED.md` files in this repo: a completed investigation's detail
lands here, and `CLAUDE.md` stays for durable, still-true conventions only.

## An `IndexOutOfRangeException` in `DataCube.GatherComplex` is a MALFORMED CUBE, never a bad slice argument (2026-08-31)

**Reported:** a crash report (1.0.0-beta.6, Windows) from adding a trace after toggling a trace
card's matrix type S → Z and back:

```
System.IndexOutOfRangeException: Index was outside the bounds of the array.
   at RfCore.Data.DataCube.GatherComplex(...)
   at RfCore.Data.DataCube.GatherComplex(...)
   at RfCore.Data.DataCube.Slice(Object[] args)
   at RfCore.Data.DataCube.get_Item(Object[] args)
   at CircuitRF.Ui.DataDisplay.ViewModels.PlotInspectorViewModel.SetCubeDataFrom(...)
   at ...PlotInspectorViewModel.AddTrace()
```

### The slice arguments cannot produce this, and that is provable

The instinct on this stack is "a stale pin index survived the S → Z rewrite and indexed past the
new cube's port axis". **It cannot**, on two independent grounds:

- **The caller clamps.** `PlotInspectorViewModel.SetCubeDataFrom` builds every pin as
  `Math.Clamp(found?.Index ?? 0, 0, Math.Max(0, cube.Axes[d].Length - 1))` against the cube it is
  about to slice — including in the family path (`ResolveFamily`).
- **`Slice` re-validates, and throws a DIFFERENT exception.** An out-of-range pin throws
  `ArgumentOutOfRangeException` from `Slice`'s own guard; an out-of-range `Range` throws
  `ArgumentOutOfRangeException` from `Range.GetOffsetAndLength`. Neither is
  `IndexOutOfRangeException`, and neither is thrown from inside `GatherComplex`.

Fuzzed to be sure rather than argued: 200,000 random well-formed cubes (rank 1–4, axis lengths 0–3)
sliced with every mix of `Range.All`, narrowed ranges and clamped pins produced **zero**
`IndexOutOfRangeException`.

**So the only way that line throws is a cube whose backing buffer is SHORTER than its axes claim** —
`Axes` says `[freq 201, i 2, j 2]` while the `Complex[]` holds fewer than 804 entries. The gather
then walks off the end, and the stack names only the READER, several operations downstream of
whatever actually built the bad cube.

### Reading the frame count: it tells you nothing about rank

`GatherComplex` recurses once per dimension, so the tempting inference is "2 frames ⇒ rank 1".
**That is wrong for any optimized build.** Measured directly:

| build | rank 1 | rank 2 | rank 3 | rank 4 |
|---|---|---|---|---|
| tier-0 (a test that runs a handful of times, Release included) | 2 | 3 | 4 | 5 |
| fully optimized (`DOTNET_TieredCompilation=0`, i.e. what the shipped app runs once warm) | 2 | 2 | 2 | 2 |

The JIT collapses the recursive tail calls once the method is promoted, so a real crash report shows
**2 frames at every rank**. Do not size the cube from the stack — and do not measure this in a
one-shot test without disabling tiering, or the numbers will disagree with the field for reasons
that have nothing to do with the bug.

(The gather stopped being recursive in round 6 below, partly for this reason. The table stays because
it is how every report up to and including 1.0.0-beta.9 has to be read.)

### The guard

Every `DataCube` constructor that takes external data already validated shape-vs-data. The two
PRIVATE buffer-adopting constructors — the ones `Slice`, the element-wise transforms, the arithmetic
operators, `PrependAxis`, `Reduce` and `Scalar` build their results through — did not. They now call
the same `ValidateSize`, whose message additionally names the shape
(`freq[3] x i[2]`) so a crash report identifies WHICH cube is malformed.

Every internal caller derives its buffer length from the axes it passes, so the guard only ever
fires on a genuine bug — and when it does, the throw lands on the code that made the cube instead of
on an unrelated read much later. Verified by adding the check and running the full suite: nothing in
the repo currently builds a malformed cube (`RfCore.Tests` 370, `Ui.Tests` 10,364, `Engine.Tests` 1,544, full
solution green).

Held by `DataCubeTests.ShapeMismatch_InAdoptingConstructor_ThrowsAndNamesTheShape` and
`EveryInternalBufferAdoptingPath_BuildsAShapeConsistentCube`.

### Follow-up (2026-09-01): the same crash, on a build that PROVABLY contains the guard above — so this theory is refuted

A second report arrived, same stack, from **1.0.0-beta.7** — the release built *after* the guard
landed. That changes the conclusion, and the evidence is direct rather than inferred:

- The released `circuitRF.exe` (win-x64, single-file, self-contained — the `.msi` installs the same
  one binary, so there is no loose `RfCore.dll` for a stale copy to shadow) contains the string
  literals `" x "`, `"scalar"` and `"does not match axes shape "` adjacent in its string heap.
  Those three exist together **only** in the rewritten `ValidateSize`, so that build is at or after
  the guard commit, and the private buffer-adopting constructors in it call it.
- Therefore a cube whose buffer is short of its axes would have thrown `ArgumentException` **naming
  the shape**, at construction. The user got `IndexOutOfRangeException` out of the gather instead.

**A malformed cube can no longer explain this crash.** Nor can the slice arguments, re-confirmed on
.NET 10: every out-of-range form — an over-long `Range`, a `Range` past the axis, a pin at or beyond
the axis length, a negative pin — throws `ArgumentOutOfRangeException`, from `Slice`'s own guard or
from `Range.GetOffsetAndLength`. Never `IndexOutOfRangeException`, never from inside `GatherComplex`.

The reporter's own scenario does not reproduce it either. Driven end to end against the real
artifacts (the reporter's `.cnl` and `.s1p`, a **1-port** run, 101 points, 0.1–3 GHz, exported to
`.npy`, reloaded through `DataSourceLibraryViewModel` and added to successive Smith plots through
`PlotInspectorViewModel.AddTrace`): clean. The cubes are `S: freq[101] x i[1] x j[1]` and
`Z0: port[1]`, both shape-consistent. Clean under `da-DK` as well (the reporter's locale — a decimal
comma changes nothing on this path), and clean across 12,000 randomized Data Display operations on
that source: add/remove trace, S/Z/Y matrix toggles, signal reselection, Z0-override toggles, plot
type changes, and reopening the inspector.

### Follow-up (2026-09-02): the instrumented report clears `DataCube` entirely

Three more trails, from 1.0.0-beta.8 — the release carrying both the constructor guard and
`Slice`'s own `RequireShapeConsistent`. Every one names a cube of `freq[601] x i[1] x j[1]`,
Complex, sliced `[freq:KeepAsX, i:0, j:0]`, and **`RequireShapeConsistent` did not fire**. That
closes this file's part of the question:

- a short buffer would now throw `InvalidOperationException` naming the shape, from the read;
- an out-of-range slice argument throws `ArgumentOutOfRangeException`, from `Slice`'s own guard.

Neither happened, so the throw is not in `DataCube`. The hunt moves to the caller — see
`src/Ui/DataDisplay/RESOLVED.md` for what the caller's own instrumentation now records, and for the
two unguarded reads found there.

**One unguarded read in this project, found while looking and fixed:** `DataSetBuilder.ToSnp` read
`z0Cube.ComplexValues[0]` with no length check, thirty lines below a `ClassifyZ0` that explicitly
treats a zero-length reference array as a legitimate shape. An empty `Z0` cube now falls back to
50 Ω, the same way an absent one already did. Held by
`DataSetBuilderZ0Tests.ToSnp_EmptyZ0Cube_Fallback50Ohm_RatherThanThrowing`.

### The read side now refuses it too, and names it

`Slice` repeats the constructor's arithmetic against the cube's own state before gathering
(`RequireShapeConsistent`). It costs one multiply per slice and it cannot fire for a cube built
through any constructor — which is exactly why it is worth having: if the field keeps reporting this,
the next report says `Malformed cube: axes freq[101] x i[1] x j[1] claim 101 elements, buffer holds
N` instead of a bare index error on a stack that names only the reader. Held by
`DataCubeTests.MalformedCube_IsRefusedByTheRead_NotByAnIndexOutOfRange`, which has to corrupt a cube
past its constructor by reflection to reach the check at all.

The Data Display no longer dies on it either — see `src/Ui/DataDisplay/RESOLVED.md`, "A trace that
cannot be resolved says so".

### Follow-up (2026-09-02, round 5): the stack arrives, and it names the gather after all

The instrumented build (1.0.0-beta.9) reports
`at RfCore.Data.DataCube.GatherComplex` -> `Slice` -> `get_Item` ->
`PlotInspectorViewModel.SetCubeDataFromCore`. The read is where the throw is; the previous
follow-up's conclusion that it is elsewhere in `SetCubeDataFromCore` is withdrawn.

The state it names is `freq[101] x i[2] x j[2]` Complex, sliced `(All, 0, 0)`, `override=off` so no
renormalization runs, no transform on one trace and `dB20` on another, no versus, no family, no
markers. **On that state the gather cannot overflow**, and that is now measured rather than argued:
a scratch console against real RfCore slices the exact cube 18,000,000 times in Release with every
pin combination and never fails, and a full run-shaped `DataSet` (S + Z0, then Z/Y materialized the
way the Data Display does) slices every element clean while printing a group inventory identical to
the crash note's.

#### The leading-stride check was not the whole check

`RequireShapeConsistent` compared `_strides[0] * Axes[0].Length` against the buffer. That is a real
element-COUNT check — `_strides[0]` is the product of the trailing axes — but only of the count. It
could not see a cube whose INNER strides disagree with its own axes: the count still matches, every
diagnostic still prints a healthy shape, and the gather walks a layout nothing reports. It now
recomputes the strides the axes imply and holds the stored ones to them, naming both vectors when
they differ.

Two more places where that class of inconsistency could have originated are closed with it. Every
constructor read the caller's `Axis[]` TWICE — once for `Axes`, once for `ComputeStrides` — and now
snapshots it once; and `Slice`'s gather is wrapped so an `IndexOutOfRangeException` is rethrown with
the closed-form maxima:

    Gather walked off its buffer on cube freq[101] x i[2] x j[2] (strides [4,2,1]): max source
    index 400 of 404, max destination index 100 of 101. BOTH INDICES ARE IN RANGE: this read
    cannot have gone out of bounds from this state, so the fault is not in the cube's shape or
    the slice.

That last sentence is the point. Every shape-based explanation for this report has now been spent,
so the next trail has to be able to say that the arithmetic was fine — otherwise the fifth round of
analysis starts by re-deriving it. Zero cost until an exception is thrown. Held by
`DataCubeShapeIntegrityTests` (7), which reaches the private helper by reflection because no
constructor can produce the state any more.

### Still open

**This relabels the crash; it does not explain the reported one.** The originating malformed cube
was not found, and the UI sequence did not reproduce: 144 combinations of the reported steps
(1/2/3/4-port and swept runs × Rect/Smith/Polar/Table × Z0 override on/off × three S→Z→…→S orders ×
delete-the-trace-and-re-add) all ran clean against synthetic grouped runs. Two facts narrow where to
look next:

- The report's `--- trail ---` section is **empty**, and a simulate always writes breadcrumbs
  (`SchematicRunService`, `WorkspaceViewModel`). The session lived 63 seconds. **So no analysis ran
  in it** — the Data Display was reading a `.npy` written by an earlier session, with its trace
  state restored from the `.cdd`. The defect is far more likely to be in that specific file than in
  the toggle logic the report describes.
- No code path in the repo constructs a shape-inconsistent cube under test, so the next place to
  look is file-shaped input (a `.npy`/Touchstone/loadpull artifact) rather than a computation.

The offending artifact was requested from the owner; when it arrives, dumping every cube's declared
shape against its actual buffer length names the culprit in one pass. **Still not received** — the
second report's workspace folder carries the `.cnl`, the `.cdd` and the technology, but no `.npy`,
which is the one file the Data Display actually reads.

### Follow-up (2026-09-03, round 6): the state-based explanations are spent, and the stale-file lead is dead

Two more trails, both 1.0.0-beta.9 with the round-5 instrumentation in it (`4c8dd48` + `2dfa4a3`).
Every failure names `freq[601] x i[1] x j[1]` Complex, strides `[1,1,1]`, sliced `(All, 0, 0)`,
`override=off`, no versus, no family, no markers — and the new probe fields say `buf=601 expect=601
same=yes`, so the cube the gather saw **is** `ds["SP1.S"]` and its backing `Complex[]` is exactly the
axes product. `RequireShapeConsistent` ran immediately before the gather and passed.

**The two things that changed:**

- **The stack settles where the throw is.** Three frames, `GatherComplex` -> `GatherComplex` ->
  `Slice`, stopping at `Slice` because that is where the wrapper catches. Per the frame-count table
  above, two gather frames is what a promoted JIT gives at *any* rank, so this is the terminal write
  `buf[dstFlat] = _complexData![srcFlat]`. The 2026-09-02 follow-up's conclusion that the throw was
  elsewhere in `SetCubeDataFromCore` stays withdrawn.
- **The stale-`.npy` lead is refuted, and it was this file's only remaining one.** The second trail
  carries a complete run — `begin 'SP1'` / `end 'SP1'` at 07.34.56.101–.170 — and the first resolve
  failure lands 76 ms later, on the file that run had just written. The same session had also failed
  at 07.34.38.891, before the run, reading the pre-existing file. So the fault is deterministic for
  this data, is not a corrupt legacy artifact, and survives a rewrite by the current binary. The
  earlier report's empty trail meant only that no analysis had run in *that* session.

Comparing `GatherWalkedOff`'s closed form against the gather term by term — pin branch, range branch,
and the `CountSurvivingBefore`/`DstStride` source-dim-to-destination-dim mapping — they are the same
arithmetic. On `(All, 0, 0)` over `601 x 1 x 1` with strides `[1,1,1]` both indices run `0..600`
against 601-element arrays. **An in-range index, on a sealed type whose every field is `readonly`,
threw `IndexOutOfRangeException`.** That is no longer a shape, slice or stale-state bug: all three are
now positively excluded rather than merely unreproduced. It is also not shape-specific — the reports
across rounds are `freq[101] x i[1] x j[1]`, `freq[101] x i[2] x j[2]` and `freq[601] x i[1] x j[1]`.

#### The gather is no longer recursive, and the note now reports the faulting index

Two changes, in that order of importance.

**`GatherComplex`/`GatherReal` plan the walk once and run it flat** (`BuildGatherPlan` + an odometer
over the surviving axes). Three reasons, and only the third is about this bug:

- **The destination needs no strides at all.** It is dense row-major in the order the surviving axes
  appear, which is the order the non-pinned source dimensions appear, so the walk carries a single
  running `dst`. `CountSurvivingBefore` is gone with it.
- **The common shape is now a block copy.** One surviving axis with unit source step — a swept
  quantity with everything else pinned, which is nearly every Data Display trace — is one
  `Array.Copy` with one bounds check, instead of N recursive calls with three. Any contiguous inner
  run inside a larger walk gets the same treatment.
- **It removes the self-tail-recursion the JIT collapses.** That collapse is why five rounds of
  reports carried a stack that could not be read for rank, and why the faulting index was never
  recoverable from one. If the report survives an `Array.Copy` of an in-range range, the cause is not
  in this method — and that is a conclusion the recursive form could not support.

A zero-length axis has to be *said* now: the recursive form got the empty result for free from a
for-loop that ran no times, whereas a plan whose inner run is non-empty under an empty outer axis
would block-copy into a zero-length buffer. Held by
`AnAxisOfLengthZero_GathersNothing_RatherThanCopyingARun`.

`Slice`'s wrapper also catches `ArgumentException` alongside `IndexOutOfRangeException`, because the
same walk-vs-buffer disagreement now surfaces out of `Array.Copy` as an argument fault. Nothing else
inside the try can raise either — the slice arguments are validated above it.

**The note carries two new fields, both computed only on the failing path:**

- **`checked walk:`** — the identical walk with every index pair compared against the live buffer
  lengths and no array touched, reporting the FIRST offending pair and its position, or that all N
  pairs are in range with the observed maxima. This is the fact the closed form cannot give: a
  maximum that is in range says the read should not have thrown, not which index did.
- **`replay:`** — the identical gather run again into a fresh buffer. A repeat failure keeps the
  fault deterministic and hands over the index; a **success** is the more informative outcome,
  because the cube, the slice and the code are unchanged between the two attempts, so it excludes
  every explanation that is a property of any of them.

On the exact state the two trails name, the message is now:

    Gather walked off its buffer on cube freq[601] x i[1] x j[1] (strides [1,1,1]): max source
    index 600 of 601, max destination index 600 of 601. BOTH INDICES ARE IN RANGE: ... checked
    walk: every one of 601 index pairs is in range (src<=600 of 601, dst<=600 of 601). replay:
    SUCCEEDED on a fresh buffer — the identical read is NOT reproducible from this state, so the
    fault is not a property of the cube or the slice.

**Held by `DataCubeShapeIntegrityTests`** (now 13): the two diagnostic tests assert both new clauses
in both directions — an in-range walk that reports every pair clean and a replay that succeeds, and a
genuine 200-point overrun of a 404-element buffer that names `walk position [101] (pair 102 of 200)`
and replays into the same exception. The walk itself is gated by
`TheGather_MatchesAnIndependentIndexOracle`, six shapes (rank 1 to 4, including `601 x 1 x 1` and a
degenerate middle axis) crossed with whole-axis / first / last / interior-narrowed arguments per
dimension, checked against index arithmetic **the test derives itself from the axis lengths** rather
than from the cube's strides — agreeing with our own old arithmetic would prove nothing here.
Verified to catch a regression rather than assumed: an off-by-one in the odometer carry fails 4 of
the 6 shapes, and dropping the zero-length-axis guard fails the empty-axis test.

**What is still open, precisely.** The read is now provably sound and self-reporting; nothing in
`DataCube` explains the field report, and no further printing of the cube's state can. The next
report either names the faulting pair — in which case the walk and the buffer genuinely disagree and
the plan says where — or says the replay succeeded, which localises the fault outside this code
(environment or codegen on that machine) and is the point at which asking the reporter to run with
`DOTNET_TieredCompilation=0` becomes worthwhile. The artifact request stands and is now cheap to
satisfy: the trail names the workspace folder, and a run rewrites `Sparam1.npy` in it, so the
reporter can produce the file on demand.

### Follow-up (2026-09-04, round 7): the first trail that does not fail — and the last untouched layer names itself

One trail from 1.0.0-beta.10, from the round-3 reporter's own machine (16 cores, 64 GB, Windows
x64, packaged Release, a period-separated locale). **Nothing in it failed.** No `=== FATAL ===`
block, no `trace resolve FAILED:` line, no gather message of any kind, across 57 breadcrumbs and
roughly four and a half minutes of Data Display work. It is a copy of the live `session-*.running`
file rather than a promoted report, which is itself the finding: the session was still alive when it
was taken.

**This is a non-reproduction, not a fix.** The bug was never on-demand — round 4 records a failure
33 s after launch with no analysis in the session at all — so one clean run is a strong negative and
no more. What makes it worth recording is that the session did exercise the recipe rather than
merely avoid it; the gesture breadcrumbs are what let anyone say so, and the Data Display half of
this round is in `src/Ui/DataDisplay/RESOLVED.md`.

**There is now a mechanism by which it could genuinely be gone, and it was not built as a fix.**
Round 6's rewrite of `GatherComplex`/`GatherReal` from self-tail-recursion to a planned flat walk
shipped in this build. It was done for readable stacks and for the `Array.Copy` fast path, and the
round-6 note says outright that no state-based explanation was left. But it replaced the exact
instruction sequence the beta.9 stack named, at a site where the index arithmetic was proven in range
and the fault appeared only on x64 Release on one machine. If the fault lived in the generated code
rather than in the cube, that rewrite ends it accidentally — and an accidental fix is
indistinguishable from a non-reproduction until the next report or the reporter's `.npy` arrives.

**The one new fact, and it is the layer this file said was left.** Round 6 closed with "the replay
succeeded" localising the fault outside `DataCube`, to environment or codegen on that machine. The
execution-environment header added for exactly that purpose (`CrashReporter.AppendExecutionEnvironment`)
reports on this run:

    debugger    : no
    gc          : workstation, Interactive
    profiler    : none
    codegen env : all default
    modules     : 27 loaded, from elsewhere: PGHook.dll

Four of those five clear the field: no attached debugger, no CLR profiler rewriting IL, no codegen
environment variable set, stock GC. The fifth does not. `PGHook.dll` is a native module loaded from
neither Windows nor the application directory — an injected API-hooking library, the shape that
endpoint-protection and monitoring agents use. **No report before beta.10 could have shown it**, and
it is the only remaining candidate for an in-range index that faults: a hooking layer is the one
thing in the list that alters the code that actually runs.

It is a lead, not a diagnosis. It is *not* evidence the module is at fault — nothing correlates it
with a failure, because this session had none. What it does is give the next report a variable to
compare against, and it is the reason the environment header stays.

**Unchanged, and still the shortest route:** the reporter's `Sparam1.npy`. The trail names the
workspace folder and every run rewrites the file in it, so it can be produced on demand. With the
file this is a repro that runs here instead of a wait for the next trail.

---

# Passive readouts and Touchstone health (2026-09-06)

`PassiveMetrics` and `TouchstoneHealth`, added so a vendor's part file can be read as the part —
ESR, C_eff, L_eff, Q, self-resonance — and so an `.sNp` can be checked before it is trusted. Three
findings here were arrived at by measurement after a first answer that was wrong, and each one is a
trap that would otherwise be re-entered.

## 1. The 1-port misread of a shunt-through file is exactly `Z ∥ Z0`

The feature exists because nothing in a Touchstone file records the fixture. The obvious
justification — *"at a few milliohms against 50 Ω, S11 is pinned at −1 and carries no information"* —
is the **numerical** argument, about a network analyser's directivity floor. It is true, and it is
not the systematic error, and writing it as if it were sent the first test looking in the wrong
place.

Working the algebra: for a shunt DUT, `S11 = −Z0/(2Z+Z0)`, so the 1-port relation returns

    Z0·(1+S11)/(1−S11)  =  Z ∥ Z0

exactly. The consequences are the opposite of intuition:

- The misread is **most accurate at the self-resonance**, where `|Z| ≪ Z0` — the one place a careless
  check would look, and the reason the first version of `FixtureMatters_…` failed with the two
  values agreeing to four digits.
- It **saturates at Z0** everywhere else. A bulk decoupling capacitor is under an ohm across most of
  its band and survives the mistake at the 1 % level; a 10 pF part, an inductor or a bead comes back
  as ≈ 50 Ω, and its capacitance with it.

**So the test fixture has to be a part that actually reaches saturation.** The second attempt used
the same 100 nF part as everything else and failed on its own premise assertion: 100 nF at 2.25 MHz
is 0.7 Ω of reactance, not 707 Ω. A test built around the obvious part would have measured a 1 %
error and concluded the fixture barely matters. `PassiveMetricsTests` now pins the parallel form over
the whole sweep and demonstrates saturation on a 10 pF part.

## 2. The causality pre-cursor test: subtract the REAL asymptote, never the complex one

The measurement transforms each S element to an impulse response and reports the fraction of energy
landing before t = 0. The raw form flags causal files: a shunt capacitor measured **28.9 %** and a
plain through **10.9 %**, because the sweep stops at f_max where the response is still large, and
zero-padding above it is a step whose sinc ringing is symmetric about t = 0.

Subtracting a constant across frequency removes a scaled `δ(t)`, which is causal and therefore cannot
change the verdict in principle. **Only the real part may be subtracted.** Subtracting a complex
constant `c` removes `c_re·δ(t)` *plus a Hilbert kernel* `c_im/(πt)`, which is spread over all time
including negative time — it injects the very thing being measured. That was tried first and made
every case worse (the capacitor went 0.289 → 0.317), which is how the asymmetry was found.

With `Re(H(f_max))` subtracted per element: shunt capacitor 4.9 %, plain through 0 %, RC low-pass
0.06 %, delay line 4.9 %; a right-half-plane pole 98 %, a delay run backwards 56 %.

## 3. A high pre-cursor ratio has two causes and the measurement cannot separate them

**This is why the finding is worded as a measurement with two readings and never as "not causal".**

A ferrite bead with a 95 MHz impedance corner, sampled on a 25 MHz uniform grid to 10 GHz, is
perfectly causal and measures **37 %** — the same side of the line as a delay running backwards.
Refining the step to 1 MHz takes the same network to **0.02 %**. The cause is not aliasing of a
resonance (refining the capacitor's grid did *not* help, which ruled that out) but the sweep failing
to resolve the response's own low-frequency behaviour. A uniform grid fine enough to resolve a
hundred-megahertz corner up to ten gigahertz needs tens of thousands of points, which nobody ships.

Both readings matter to anyone using the file in the time domain, so the finding is worth making —
but claiming acausality would be a claim the number does not support. The threshold (25 %) is
empirical and `TouchstoneHealthTests` asserts a **2× margin on each side** rather than only the side
of the line, so narrowing the separation fails loudly instead of quietly.

## 4. Two smaller notes

- **The test runs only on a uniform grid reaching DC in one step**, and says so otherwise. Most
  vendor passive files are log-swept, so *"not evaluated"* is the common answer and is correct:
  resampling a log sweep needs an interpolation that is itself a low-pass and would manufacture the
  smoothness being tested for.
- **`SNP` cannot be constructed empty** — both constructors refuse — so `TouchstoneHealth.Analyze`'s
  zero-point branch is defensive and deliberately ungated. A test for it would have to defeat SNP's
  own guard to build the input.


## AUT-8 R-aut8-3 — a Touchstone header that said GHz over Hz data (2026-09-07)

`SNP.FreqUnit` defaults to `GHz` and nothing sets it on an SNP produced by an analysis, so a sweep
that ran in the Hz decade wrote a file whose option line said `# GHz` above a first column reading
`5E-10`. That file is wrong whichever end you believe, with nothing in it to say which.

**The fix is a stated/unstated distinction, not a new default.** `FreqUnitIsStated` is set by the
property setter, so the Touchstone reader marks it simply by assigning the unit it parsed off the
option line; an SNP nobody has set it on stays unstated and `TouchstoneIO.WriteFile` picks the unit
from the data (`UnitForData` — the largest unit in which the highest frequency still reads as at
least 1). A file that was READ therefore keeps its own declared unit and a read-write round trip is
byte-identical, which is what stops this touching the byte-for-byte export gates.

**The copy paths need the stated-ness carried with the value.** `CopyMetadataFrom` and `RefreshFrom`
assign through the private field and copy the flag, because `FreqUnit = source.FreqUnit` would turn a
unit that was only the type's default into a declaration — and a converted network would go straight
back to announcing GHz over Hz data.


## AUT-9 — units on values, per-port Z0, and asking a result for part of itself (2026-09-07)

Three changes in this project, all reporting rather than computation.

### `DataCube.Unit`, and why it is a fallback rather than the whole answer

The axes have carried a unit since they were written; the values never did. One loadpull-pursuit
result carried `Efficiency` at 65.84 and `MXE_Eff` at 0.7087 at the same operating point — the cube in
percent, the scalar as a fraction — with nothing anywhere saying which.

Annotating every cube every engine makes would be a large change for a value that is, for most names,
already determined. So there are two layers and the split is deliberate:

- **`RfCore.Export.ResultUnits`** is a name-keyed vocabulary covering the standard result names
  (`Pout_dBm`, `Gt_dB`, `Z0`, `S`, `V`, `I`, the pursuit's scalars, the flags and counts). It answers
  for a cube whose producer said nothing, which is nearly all of them.
- **`DataCube.Unit`** is for the case a name cannot answer, and there is a real one:
  `LoadpullPostProcessor.Enrich` scales `PAE` from a fraction to a percentage **under its own name**,
  so two cubes called `PAE` mean different things and only the producer knows which. `Efficiency` does
  not need it — `Enrich` renames `DE` on the way — which is exactly why the name-keyed table gets
  most of the vocabulary for free.

**A stated unit survives the file.** `NpyWriter` writes a `"unit"` key beside `"kind"` and `NpyReader`
reads it; `MatWriter` writes a sibling `unit` dataset for the same reason its own directory's
`CLAUDE.md` gives — the two writers serialize one logical payload and must track each other. Both
write the key **only when the unit is non-empty**, so a file whose cubes say nothing about their units
is byte-identical to what these writers produced before, and every committed fixture still reads.

**`unknown` is a value, not a hole.** A designer's `measure Foo = dB(S(2,1))` has whatever unit that
expression has, and circuitRF does not evaluate units through the expression engine. Saying `unknown`
tells a caller not to guess; an empty string tells it nothing, which is the state this replaced.

### `SNP.Z0PerPort` — carried, never applied

`ToSnp` flattened a non-uniform per-port Z0 to port 1's value and discarded the rest, so a matrix
generalized w.r.t. [50, 12] was written and read back as though every port were 50 Ω. The per-port
values now travel on the SNP and through the Touchstone header note in both directions.

**Renormalizing to the single declared reference was implemented first and reverted.** It makes the
file self-consistent and it destroys the quantity the ports were declared to ask about: on a
50-to-10 transformer a matched `|S21|` of ~0 dB becomes the -2.55 dB a uniform 50 Ω measurement of the
same part reads. `Engine.Tests`' `MatchStampTests.ACnlContainingAMatch_RunsHeadlessUnderCliSparam` is
what caught it, and it is worth knowing that gate exists: it is the only place in the tree that runs a
deliberately non-uniform two-port end to end through the CLI. Detail in `src/Cli/RESOLVED.md`.

### `DataSetNarrowing` — the unit rule that makes `--at freq=2GHz` unambiguous

**The axis's unit is stripped before the SI prefix**, and that ordering is the whole of it: on a metre
axis `5mm` is five millimetres and `5m` is five metres, where reading a trailing `m` as milli always
would silently divide a length by a thousand. A bare number is taken as already being in the axis's
base unit, so `2e9` and `2GHz` are the same request on a `Hz` axis.

Two smaller decisions:

- **`--at` keeps the axis at length 1 rather than collapsing it.** Collapsing would take the answer's
  own location out of the document, and "which point did I actually get" is the question `nearest` has
  to answer. `--interp` rewrites the axis value to the one asked for; `nearest` leaves the grid point
  it landed on.
- **A cube that does not HAVE the named axis is left whole.** A `Z0` cube has no frequency axis, and
  narrowing by frequency is not a claim about it. An axis **no** cube has is a refusal — that is a
  caller's typo, and answering it with everything would be answering a different question.

Interpolation is linear on real and imaginary parts for a complex cube, which is what a linear
interpolation of a complex quantity is. Nothing interpolates magnitude and phase separately: around a
wrap that is a different and wrong answer.

## WSP-1 — `Stability/WspReduction.cs`, the single-probe reduction (2026-09-08)

The first file of `src/RfCore/Stability/`: pure functions over `Complex` values that turn one
probe's 2×2 block of the `wsp` matrix into the reduced two-port (`[Y]` Eq. 44, `[Z]` Eq. 48) and the
six default outputs. Two things to know before extending it (WSP-2 does):

- **Eq. 40 and Eq. 47 are wrong as printed and are not implemented** (overview typo register T-1, T-2).
  `y12`/`y21` of Eq. 40 carry the `|P|` term with the wrong sign; Eq. 47's `z22` has a wrong first
  factor. Eq. 44 and Eq. 48 are the forms, and gate (e) holds `[Y]·[Z] = I` to 1e-11 on a network
  with real feedback.
- **`ZG`/`ZL` ship in the Z form, `−A/B` and `(1 + A)/B`**, not the `|Y|` form of Eq. 65/66: one fewer
  cancellation, exact algebraically, and the two agree to ~1e-15 on Hero 1. The `|Y|` forms are kept
  as `ZGFromY`/`ZLFromY` for tests only. **No epsilon is added to any denominator** — `H0 = 0` or
  `Y0 = 0` returns NaN with `Degenerate` set, and the engine warns once per probe. The document's own
  `wsp_yop`/`wsp__zop` add `1e-15`; circuitRF does not (overview D-7).

## WSP-2 — `Stability/`, the single-probe derived metrics (2026-09-08)

Six more files beside `WspReduction.cs`, plus the cube glue in `src/Core/Expressions/Evaluator.Wsp.cs`
(the expression engine's own file — it computes nothing, it maps). The design note
`docs/design/stability-wsprobe.md` §5 is the reference; what follows is what was *found* while
building it, none of which is in the brief.

### The brief's gate (f) claimed something the algebra does not give — and the fixture had to be
### built to make the claim true

The brief's even-mode gate reads "the even-mode impedance at either drain probe equals the value
found by simulating one half with the common load doubled". That is only true for a particular
placement of the *stimulus* probe, and the reason is worth writing down because it decides what
`wsp_impedance` means:

Off the diagonal the ratio is `V_j / iS_j` at the response probe, and **which side of that probe is
source-free decides what the ratio is**:

- Drive on the response probe's **L side** → its G side is source-free, KCL gives
  `v_Gj/Z_Gj + iS_j = 0`, and the ratio is `−Z_Gj` — with **no dependence on the drive at all**.
- Drive reaching it through its **G side**, with an active device on the L side → the ratio is the
  effective impedance that device works into, i.e. a genuine load line.

So a naive fixture — two drain probes in a combiner, the stimulus in one of the branches — measures
the *other* device's output resistance, negated, and never a load line. `combiner_even_mode.cnl`
therefore puts a third probe on the **common input node**: driving that excites both halves in
phase, both devices are live, and either drain reads `RM + 2·RL = 55 Ω` exactly, independent of gm,
of the device output resistance and of frequency. `WspNodalFunctionTests.F_…` asserts both readings
on the same circuit — 55 Ω from the common probe and −1000 Ω from the other branch's probe — because
the contrast is the finding.

**Eq. 37's shunt form and App. C's series form are the SAME number off the diagonal**, exactly. The
brief presents them as two forms that "both cancel the common stimulus"; they cancel it to the same
value, because the ratio is a property of the response probe and the excitation, not of which
generator produced it. They differ only on the diagonal, where the series form is `−ZG` and the
shunt form is `1/YL`. The `stimulus` argument is therefore there for the diagonal case and for
fidelity to the document — not because the two disagree where the function is normally used.

### The circulator direction mapping was a prediction; it is a measurement now

The brief predicted `Direction="CW"` ↔ `"REV"` and `"CCW"` ↔ `"UNI"`. Replacing `hero1_probed.cnl`'s
`WSProbe:P1` with circuitRF's own `Circulator` (ports 1 and 2 in the same node, port 3 brought out as
a fifth analysis port) and comparing `S55` against `wsp_loopgain`: **CW matches REV to 4.7e-15,
against 0.18 the other way; CCW matches UNI to 4.6e-15.** Two independent implementations — a
transcribed `ȳ` formula and a stamped 3×3 S-block — agreeing to machine precision is about as strong
as a gate gets, and it pins Eq. 97/99, the `ȳ = Z0·y` normalisation, the probe's G/L orientation and
the series-source sign all at once.

### `wsp_yparam`'s output shape is a capability the document does not mention

It returns `{…, freq, i, j}` with `i`/`j` valued 1 and 2 and unit `"port"` — byte-for-byte the shape
an `S` cube has. Converted to S at a real reference, **Rollett K, μ, μ′, |Δ|, MAG/MSG and the
stability circles of the reduced two-port at a probe** all come from `NetworkMetrics` with nothing
new written, once WSP-4 exposes a probe's reduced network as a Data Display source. That is why the
shape is asserted in the gate rather than left to chance.

### Three shapes, and one of them has to be allowed to be empty

`wsp_unstable_freq_kurokawa` returns a `{n}` cube of frequencies which is **empty** when no crossing
was sampled. That survives the whole path — `MeasurementEvaluator`, the DataSet, the `.npy` export
and `DataSetImporter` — and is asserted end to end, because the alternative the document takes
(returning a zero) makes "no crossing" indistinguishable from "a crossing at DC". An empty result
means *no crossing was sampled*, not *the circuit is stable*: a sweep coarser than the resonance can
step over one.

`GainDEFs` returns four NAMED numbers per frequency, which in this result model is a labelled axis
and nothing else — `{…, freq, gaindef}` with labels `GT_dB`, `GP_dB`, `GA_dB`, `Gmax_dB`, picked
with `at(...)`. There is no other honest spelling for it in an expression language whose values are
scalars and cubes.

### Two traps in the test fixtures, both of which produce a passing test of nothing

- **A probe with a dangling one-port on its G side has `ZG = 1/YG` exactly**, however much feedback
  the rest of the circuit carries (the design note's §2.4 — ground is not a path). The first
  `derived_metrics.cnl` had exactly that at `P1`, so the Eq. 79 assertion measured 5e-16 and would
  have "passed" any looser test while proving nothing. One capacitor from `a1` to `a2` closes a path
  around both probes that runs through neither, and the discrepancy becomes 45%.
- **`GP` and `GA` are not `|S21|²` at `ΓS = ΓL = 0`.** Only `GT` is. Each of the other two still
  divides by the mismatch its own definition leaves un-terminated (`Γin = S11`, `Γout = S22`), so the
  matched-limit gate is `|S21|²/(1 − |S11|²)` and `|S21|²/(1 − |S22|²)`.

### The inverse of the reduction, which is what makes the identity gate a real test

A random `[Y]` is turned back into the four transfer functions `A, B, C, D` the probe would have
measured by reading Eq. 44 backwards:

```
C = H0 = 1/(y11 + y12 + y21 + y22)     (which is Eq. 93's 1/H0 = YG + YL)
A = −C·(y12 + y22)      D = C·(y21 + y22)      B = |Y|·C   (which is Eq. 69's 1/Y0 = ZG + ZL)
```

`WspReduction.YParam` of that quad returns the original `[Y]`, and **that round trip is asserted
first** in `WspNodalTests.A_…`: without it every identity after it would be checking the fixture
against itself. The two parenthesised remarks are not decoration — they are why the closed form
exists at all, and they are the cheapest sanity check on it.

### `wsp_zo_renorm_s` IS the power-wave renormalisation, so it does not transcribe E.10

E.10's `S' = F·(I − conj(Zr)·Y)·(I + Zr·Y)⁻¹·F⁻¹` with `F = diag(1/(2√Re Z))` is Kurokawa's
power-wave form with `Z = Y⁻¹` substituted and the `Y⁻¹` cancelled. The shipped path is therefore
`RFNetwork.SToS` — the repository's one complex-reference renormalisation, the Z0-override path's —
and the transcription lives in the test as the oracle and nowhere else. They agree to 1e-11 on 300
random two-ports with random complex references.

### `wsp_nZ` and `wsp_nY` are names chosen here, not the document's

The brief gives the two normalised driving-point loci as formulas (`nZ`, `nY`) and no function name,
because they are circuitRF's own rather than the reference document's. The registered spellings are
`wsp_nZ(wsp, idx)` and `wsp_nY(wsp, idx)` — the brief's own symbols, under the `wsp_` prefix every
probe function carries so a designer finds them beside the rest. They are the only two names in the
whole library that a reader of the document will not recognise, which is exactly why their
doc-comments and their Data Display descriptions have to keep saying **"not the published margin"**.

### The normalised loci give the same crossings, not the same doubles

The brief's gate (i) asks that `wsp_unstable_freq_kurokawa` "return identical frequencies" on `nZ`
and on `Y0`. The invariance is real and exact — dividing by a positive real cannot move a zero of
`Im(g)` — but bit equality is unattainable and the reason is structural: the search **interpolates
linearly between two samples**, and the normaliser `|ZG| + |ZL|` is not constant across that
interval, so the two straight lines cross zero at very slightly different places. Measured on both
of the document's resonators: **12 Hz on a 10 MHz grid, 1.19e-6 of a sweep step**, four orders below
anything the sweep resolves. The gate asserts the same COUNT and agreement to 1e-4 of a step, and
prints the drift; asserting bit equality would be asserting a property of the interpolator rather
than of the loci.

## WSP-3 — `Stability/`, probe pairs, bifurcation, Ohtomo, the reduced matrices and the envelope (2026-09-08)

Six files beside the WSP-2 set (`WspMatrix`, `WspPair`, `WspBifurcation`, `WspOhtomo`, `WspGlobal`,
`WspEnvelope`), the cube glue in `src/Core/Expressions/Evaluator.WspGlobal.cs`, and one engine
addition (`__WspTermZ`). The design note `docs/design/stability-wsprobe.md` §6–§8 is the reference;
what follows is what was *found* while building it.

### The document's bifurcation code returns TRANSPOSES, and its two forms disagree about which side is "active"

Future readers of the document will rediscover this, so it is written down once. Its §6 matrix
equations (Eq. 163–168, 175–176) are written for a symmetric `Y`; for a non-reciprocal network the
products its code forms — `−inverse(VV)·VI` and the three siblings — are the **transpose** of the
subnetwork's true Y or Z, because `wsp` is stimulus-major (rows are the stimulus probe) and a
network matrix is response-major. And its Y-form puts the G-side network in `Y` (`wsp_YA`) while its
Z-form puts the **L-side** network in `Z` (`wsp_ZA`) — so `wsp_YA` and `wsp_ZA` describe opposite
subnetworks, and `wsp_YA = inverse(wsp_ZF)`, not `inverse(wsp_ZA)`. Every use the document makes of
these matrices is transpose-invariant (determinants, principal minors, a diagonal cofactor), so its
results stand; a designer reading `y21` of a block from them would be reading `y12`. circuitRF's
primitive is `wsp_bifurcate(wsp, form, side)` with the transpose applied, and the document's four
names are aliases whose doc-comments state their side. The gate that catches both a lost transpose and
a swapped side is `Y form of a side == inverse(Z form of the same side)` on a NON-reciprocal fixture —
a reciprocal one hides both.

### The rank-1 update at the LOAD probe needs two corrections the brief's formula does not have

The brief states the Sherman–Morrison update for a shunt admittance at a probe's **G** node and says
to apply it "likewise" at the load probe, whose Term faces its **L** terminal. The two terminals are
one node, so every node voltage and every other probe's current respond identically to an injection
at either — but two entries do not: under the probe's own series stimulus the L-node voltage is
`vP + vS` (one more than `wsp(2S−1, 2S)`), and a current injected at the L node does not flow through
the probe branch (the probe's own `iS` response is one less than `wsp(2S, 2S−1)`). Without the two
`δ`s the load probe's own row and column come out wrong and nothing else does — which is exactly the
kind of error a test that reads only `H0'` would never see. Gate (e) compares **all 36 entries**
against a re-run with the Terms changed: 4.6e-14 worst relative error.

### The engine records each probe's neighbouring Term, so the precondition compares against a declared number

`wsp_terminate` refuses (`wsprobe.envelope-probe-not-at-termination`) unless the source probe's `ZG`
(or the load probe's `ZL`) equals the declared `Z` of a top-level `Term`/`Port` shunting that
terminal to ground, to 1e-6 at every frequency. The engine now writes `__WspTermZ` `{probe, side}`
(NaN where there is no such Term) beside `__WspProbes`. The evaluator finds it through the analysis
that OWNS the wsp cube — `MeasurementContext.TryFindWspOwner`, reference equality on the cube object
`SP1.wsp` hands out — because a function argument is a cube, not an analysis name. A sliced cube
(`at(SP1.wsp, …)`) is a new object; the envelope functions refuse it and say what to pass, rather
than skipping the check. Probe LABELS on the `i`/`j` axes of every multi-probe result come from the
same lookup, and fall back to idx numbers when it fails.

### Three brief premises the arithmetic overrode

- **"A resistor from an inner node to ground" does not raise the pair residual** (4.7e-16). Ground is
  not a coupling path (the WSP-1 finding again): a shunt at an inner node is part of the inner block's
  own `y11`, and both stimulus sets agree on it. What the residual detects is a path from the region
  between the probes to the rest of the network AROUND them — a resistor from inner node `a` to outer
  node `d` gives 0.37. The gate asserts both, because the distinction is the point of the diagnostic.
- **At `|Γ| = 0.8` the whole load-pull circle is stable** on the series resonator with `R1 = −5 Ω`:
  the smallest `Re ZS` on that circle is `50·(1 − 0.64)/(1 + 0.8)² = 5.56 Ω`, above the 5 Ω the
  negative resistance can overcome. The gate uses `|Γ| = 0.9` (min 2.63 Ω), and the instability arc is
  then exactly where `Re ZS(θ) < 5 Ω`. And the reported frequency tracks `f0` only at `θ = 180°`
  where `Im ZS = 0`; elsewhere the load reactance detunes the resonator by up to a gigahertz and what
  it tracks, to four digits, is the closed form `X(ω) = −Im ZS(θ)`.
- **Terms at `1e9 Ω` do not give an S → Y that agrees to 1e-8**, and subtracting the Term's `1e-9 S`
  (the brief's suggestion) is not why. `S → Y` at a reference impedance yields the network's own
  admittance — the reference is the generator's, not part of the network — so nothing is subtracted.
  What fails is conditioning: with `Y ~ 0.1 S` and `Z0 = 1e9`, the port waves sit at `1 + S ≈ 1e-8`
  and the conversion loses eight digits (measured 1.5e-8, on the entries the 3 nH inductor joins, in
  a run with NO regularisation warning). At `1e6 Ω` the two agree to 1e-8.

### `wsp_block_design` is `wsp_zo_renorm_s` up to a known port phase, not equal to it

E.8 absorbs the parallel capacitance of each side into the network and renormalises to the real
parallel resistance; `wsp_zo_renorm_s` renormalises to the complex `Z = R ∥ 1/jωC` with power waves.
Writing `Z_r = 1/(G + jB)`: `V + Z_r·I = (V + R·I'')/(1 + jBR)` with `I'' = I + jBV` the current into
the augmented network, and `Re Z_r = R/(1 + B²R²)`, so the two incident waves differ by `e^{−jφ}`,
`φ = atan(BR)`, and the reflected by `e^{+jφ}`. Hence `S_rc = D*·S_zo·D*`, `D = diag(e^{jφ_k})`:
equal magnitudes, phases that differ per port. The brief's "equals to 1e-12" holds for the magnitudes;
the gate holds the full identity with the factor put back.

### Two derivations of the same loop gain make `1 − {9} == {13}` a test rather than a tautology

`wsp_block_calc`'s `{9}` (the synthetic-FET return difference `FB = |Y + Yf| / |Yo + Yf|`, Eq. 159)
and `{10}` (the same with the feedback block passivated, `yf12 → yf21`) are computed as determinant
ratios, while `{13}` and `{14}` are Eq. 148 and Eq. 149 transcribed literally; `{11}`/`{12}` are
`1 − {15}`, `1 − {16}` by definition. On random blocks and on the real circuit the two derivations
agree to 1e-10. On `two_block.cnl` `LGf` is identically zero (reciprocal feedback, `yf12 = yf21`, is
unchanged by its own passivation), which is why those comparisons use a scale floor of 1.

### The `1/H0'` side of the envelope reports crossings of its own under a complex load

On the series resonator with a Term, the document's own §4.10 says the zero masks the pole in
`1/H0` — true at the nominal real load. Under a complex pulled load the `1/H0'` locus crosses the
negative real axis clockwise at frequencies of its own (θ = 130°: 0.6037 GHz beside `1/Y0'`'s
0.5906), and at one load only `1/H0'` fires because `1/Y0'`'s crossing sits at the sweep's edge.
`wsp_loadpull_unstable` reports both sides separately (`unstable_H0`, `unstable_Y0`) and the union
(`unstable`), as the document's method takes the union; the gate asserts the `1/Y0'` side against the
closed form and prints the other.

### What the expression language could not say, and what was chosen

- A measurement is ONE cube, so a function the document returns two things from returns a labelled
  axis: `wsp_yparam2` is the document's own `YP(1..8)` on `k`, with a fourth argument
  (`"inner"`/`"feedback"`) for one block as a 2-port; `wsp_loadpull` puts `H0env`/`Y0env` on `env`;
  `wsp_loadpull_unstable` puts the counts and the frequencies on `item`, NaN-padded.
- There is no list literal, so a probe list is an integer or a quoted comma-separated string of idx
  numbers or labels, and a Γ grid is a scalar, a cube, or `"|Γ|:count[@start]"`. Both are documented
  on the wrapper file.
- The pair residual `wsp_yparam2_residual` and the side-explicit `wsp_bifurcate` are the only names
  here the document does not have; both carry the `wsp_` prefix so a reader finds them beside the rest.

## WSP-9 — the stability margin (2026-09-08)

`src/RfCore/Stability/WspMargin.cs`: the four bounded proxies of [M] §II and the two margins
`SM_Y0`/`SM_H0` (M-Eq. 9/10), plus `WspEnvelope.LoadpullMargin`/`LoadpullNdf` — [E]'s envelope, as
post-processing over WSP-3's rank-1 re-termination. Design note: `docs/design/stability-wsprobe.md`
§9. Findings, in the order they cost time:

### The paper's third case must be tested FIRST, and the boundary cases are conventions

[M] prints `rY`'s `Re ZG + Re ZL ≤ 0 ⇒ 0` case last, after the two magnitude branches. Taken in that
order it is unreachable: at `Re ZG = 5`, `Re ZL = −10` the magnitude branch answers 0.25 where
Kurokawa's real-part condition holds and the margin must be 0. The sum is tested first (`ProxyReal`),
and the gate asserts both values so the ordering cannot be "simplified" back.

Three more the paper leaves open, all conventions and all gated (typo register T-18): equal
magnitudes agree on both branches so `≥` is not a choice; **both parts exactly zero returns 0.5**,
because the function is genuinely discontinuous at the origin (the limit is 1 along
`Im ZL = Im ZG → 0`, 0 along `Im ZL = −Im ZG → 0`, 0.5 along either axis) and 0.5 is what one purely
resistive side gives; and NaN propagates, so a degenerate node reads NaN and never a 0 that would be
read as an instability.

### The both-parts-zero convention makes a resistive side read a FLAT margin — twice

The consequence of that convention is bigger than it looks, and it surfaced twice.

- **WSP-1's own resonator cannot be the margin's fixture.** There the probe sits at the Term, so
  `Im ZL = 0` at every frequency, `iY ≡ 0.5`, and `SM_Y0` is **flat at 0.375** for `R1 = −5 Ω` — no
  resonance at all. `testdata/wsprobe/margin_split_resonator{,_neg20}.cnl` splits the reactance
  across the probe instead, and the engine then matches the closed form to 2.5e-16 over 2001 points.
- **The same thing happens at two points of every Γ circle.** On the margin envelope over
  `|Γ| = 0.9` the pulled termination is purely resistive at `θ = 0°` and `θ = 180°`, so `SMenv`
  floors at exactly `0.25` = −12.04 dB there instead of collapsing — one grid point of 24 on the
  arc the closed form says is unstable. The brief's "below −40 dB on the arc" holds on the other 23,
  and the gate exempts the resistive point by TESTING `Im ZS ≈ 0` rather than by widening a
  tolerance.

### "Below −40 dB" is a claim about a SWEEP, not about a circuit

On `series_resonator_term.cnl`'s own 251-point grid the shallowest reactive arc point reads
−33.5 dB, because the notch there is narrower than the 10 MHz step and the sampled minimum misses
the bottom. At 2001 points the same point reads −58.7 dB. The gate asserts −30 dB on the shipped
grid, prints both numbers, and re-runs refined to pin the cause as resolution rather than
arithmetic — the same sampling caveat `WspKurokawa.UnstableFrequencies` already carries.

### [E]'s reduction, rebuilt from the circuit rather than transcribed

E-Eq. 1–4 build a core 4-port from the `wsp` entries; the paper's own matrix layout is not held
here, so the oracle was derived: with the suspect probe's branch OPEN the circuit is a 4-port (source
node, load node, suspect-G, suspect-L), four stimuli the `wsp` matrix already carries drive it, and
stacking the port voltages and currents gives `I_mᵀ = Y·V_mᵀ`, hence **`Y = (V_m⁻¹ I_m)ᵀ`** — which
is E-Eq. 4 including its transpose, arrived at independently. The two bookkeeping corrections are
the ones WSP-3 §6 already needed (`vL = vP + vS` under the probe's own series stimulus; the branch
current leaves port 3 and enters port 4). Measured against the rank-1 `wsp_terminate`: **2.7e-15**
relative on all six quantities. **E-Eq. 11 as printed is off by 1.17 relative** (T-16 held by a test,
not a note), and the un-transposed matrix differs from the core `Y` by 0.086 (D-9).

### Two functions, not one, wherever the answers differ in rank or kind

`wsp_loadpull_margin` returns the frequency-resolved margins; `wsp_loadpull_margin_env` returns
`SMenv` — the minimum over frequency, the number [E] Fig. 6–9 plot against phase. `wsp_loadpull_ndf`
returns a Complex locus; `wsp_loadpull_ndf_enc` returns a Real encirclement count. One call returns
one cube, and a cube has one rank and one kind, so the brief's "returns A, B and C" is two names
each — the split `wsp_loadpull_unstable` already is from `wsp_loadpull`.

### The retirement of D-12's placeholders

Removed: `wsp_nZ`, `wsp_nY`, `WspNodal.NormalizedLocusSeries`/`NormalizedLocusShunt`,
`WspNodal.StabilityMargin`'s refusal, `MarginNotTranscribedKey`/`Message`, their allowlist line,
their tests, and the docs rows. `wsp_stability_margin` answers with `min(SM_Y0, SM_H0)`. A
comment-stripped source scan of `src/` plus a plain scan of `docs/design/` is the gate — which is
why the design note's own §9.8 describes the retirement without printing the spellings, and this is
the one place they are still written down.

## The encirclement count is over a HALF contour, and two consumers disagree about it (2026-09-08)

`WspEnvelope.LoadpullNdf`'s `Encirclements` field and `SParameterEngine`'s `NDF_poles` cube both call
themselves encirclement counts of the same NDF, and **they differ by exactly two**.

The argument principle counts turns around the CLOSED Nyquist contour, `ω` from −∞ to +∞. A sweep
runs `ω ≥ 0`, and Platzker's property 4 (`NDF(−ω) = conj NDF(ω)`, §8 p. 112) makes the missing arm
turn through the same angle: with `φ(ω) = arg NDF(ω)`, the negative arm runs `−φ(∞) → −φ(0)` and
contributes `φ(∞) − φ(0)`, the same as the positive one. So

```
right-half-plane poles = 2 × (the swept locus's own net turn)
```

`NDF_poles` (WSP-6) carries the doubled count, referred to the sweep's first sample. `Encirclements`
(WSP-9) carries the swept locus's own turn and is not referred to the first sample. A single REAL
right-half-plane pole is half a turn: `NDF_poles` reads 1, `Encirclements` reads 0 or 1 depending on
where the locus started. A conjugate PAIR — what an oscillator has — is a whole turn: 2 against 1.

**Which is right is not a matter of taste.** WSP-6's gate (a) has a closed form with a single real
right-half-plane pole, and only the doubled count reads 1 there.

**It was corrected and reverted.** Doubling `Encirclements` flips WSP-9's own gate (i) at one grid
point of the Ohtomo fixture, where the REDUCED NDF over a probe set that provably cannot see the odd
mode wanders about half a turn without ever going round — a number that is not a count at all, and
one the `≥ 1` threshold then catches. Changing a shipped WSP-9 output on the strength of a fixture
whose reduced NDF is documented as incomplete is an owner decision, not WSP-6's to make.

What holds meanwhile, and is asserted at 288 grid points by WSP-6's gate (k): the two agree about
**whether** a point is unstable — which is all R-wsp9-5's `≥ 1` threshold reads — and
`NDF_poles == 2 × Encirclements` wherever both are clean.

## Review round after WSP-9 — two findings in the stability library (2026-09-08)

**The margin's "SM_Y0 = 0 iff Kurokawa" has an exception, and it is convention (c).** `WspMargin`'s
summary listed `SM_Y0 = 0 ⇔ Re(ZG + ZL) ≤ 0 and Im ZL = −Im ZG` as a gated property. It is not one:
when `Im ZG` and `Im ZL` are BOTH exactly zero the reactances cancel — Kurokawa's second condition
holds — and `Proxy` returns the conventional 0.5 of §2.1c, so `SM_Y0` is 0.25. The test only ever
checked the case with non-zero opposed reactances.

It is not an academic corner. A probe facing a purely resistive termination has `Im ZL ≡ 0` at EVERY
frequency, and `Proxy(g, 0) = ½(1 + 0/g) = 0.5` whatever the other side does — so the margin on that
stimulus is flat at exactly the −12.04 dB floor and carries no information at all. Run
`testdata/wsprobe/series_resonator.cnl`, which is the document's own Fig. 31 negative-resistance
oscillator: `SM_Y0` reads −12.04 dB at all 251 points, the minimum lands at an arbitrary 1.7 GHz
rather than at the 1.5915 GHz crossing, and the paper's −15 dB rule never fires on a circuit that
certainly oscillates. This is [M]'s formula behaving as written, not a transcription error, and it is
already why brief-wsprobe-9 §3's own fixture splits the reactance ACROSS the probe rather than reusing
WSP-1's resonator (the fixture's header comment says so) and why the user docs carry "a probe against
a purely resistive termination reads the resonance on the other side only". What was missing was the
statement of it beside the property it contradicts, and a gate. Both are there now.

**`LoadpullNdf` materialised the whole grid of re-terminated matrices.** It walked the grid twice —
once over the active sweep keeping every `wsp'`, once over the passivated one consuming them — which
is `ns·nl·nf` matrices of `2N × 2N` alive at once: ~130 MB on a 21×21 Γ grid over 501 frequencies with
three probes, and ~350 MB with five, for a quantity consumed one grid point at a time. `OverGrid` is
now a one-sweep wrapper over `OverGrids`, which walks any number of sweeps in lockstep and hands the
visitor all their re-terminated matrices together; `LoadpullNdf` makes one walk over both. Each sweep
keeps its OWN starting admittance, which is not an approximation and never was: the update removes
whatever that matrix's probe sees on that side and installs `yS`, so both land on the same absolute
termination whatever they started from — which is also why the precondition being checked against the
active cube only is sound.

## Two-port noise blocks, and the fixture read off the data (field report, 2026-09-28)

**A measured amplifier `.s2p` carrying a noise-parameter block was refused outright.** Touchstone 1.x
marks the noise block only by a frequency that does not advance past the last network frequency; the
reader had no notion of it, so the first noise row (5 numbers) was folded into the next 9-number
network block and the file failed with "Token overflow: got 10 tokens, expected 9 for 2-port. Check
that the file extension matches the data." — advice that was wrong, since the extension was right.
`TouchstoneIO.Read` now switches to noise mode at a two-port block boundary on a **five-number row
whose frequency does not advance** (or after a Touchstone 2 `[Noise Data]` line), and keeps the rows
in `SNP.NoiseParameters` (freq, NFmin dB, Γopt, rn) — kept rather than skipped so the file is not
silently narrowed; nothing consumes them yet and the writer does not emit them. **The "exactly five"
is load-bearing:** without it, a 4-port stream read with no port count (whose continuation lines also
"go backwards") read as one 2-port point plus noise instead of failing —
`TouchstonePortInferenceTests.Read_FourPortStream_WithoutKnownPorts_Fails` caught it.
`RefreshFrom` copies the list, per its own every-field rule.

**`PassiveMetrics.InferExtraction(SNP)`**: a lumped two-terminal impedance leaves its fixture in the
S-matrix as an identity independent of its value — series-thru S11 + S21 = 1, shunt-thru
S21 − S11 = 1. The median residual over the sweep is compared; an answer is given only when one is
below 0.1 and ten times below the other, else null (a one-port, a non-S file, an amplifier). On the
reporter's four vendor `_series.s2p` part files the residuals were ~4e-16 against ~2. The enum's
doc comment used to say the fixture is "not about the data" and cannot be inferred; it now points
here. Gate: `tests/RfCore.Tests/TouchstoneNoiseBlockTests.cs`.

## openEMS's HDF5 files through PureHDF: the layout, and the three things a reader gets wrong (brief-sar-4, recorded by brief-sar-13, 2026-10-18)

`src/RfCore/Hdf5/` reads openEMS 0.37.0's raw SAR dump, its frequency-domain field dumps and `sar_calc`'s result, and writes
CSXCAD's `DiscMaterial` voxel files, all through the PureHDF 2.1.2 the `.mat` export already shipped (overview O1: no HDF5
code of circuitRF's own, no new dependency). The layout was taken with `h5dump` and h5py from real files, then read back
with PureHDF row by row:

| | Raw SAR dump (DumpType 29) | FD field dump (10, 11) | `sar_calc` result | `DiscMaterial` |
|---|---|---|---|---|
| superblock | 2 | 2 | 2 | 0 (symbol-table groups) |
| layout | contiguous | contiguous | contiguous | `/DiscData` chunked, deflate 9 |
| data | `/CellData/{Conductivity,Density,Volume}` float32 (nx, ny, nz); `/FieldData/FD/f<k>` (3, nx, ny, nz), compound {r, i} float32 | `f<k>` the same compound | `f<k>` float32 (nx, ny, nz) | `/DiscData` **uint8 (nz, ny, nx)**; `/mesh/{x,y,z}` float32 |
| `d_order` | `XYZ` (cells), `NXYZ` (field) | `NXYZ` | `XYZ` | none |
| frequency | float64 | float64 | **float32** | — |

- **`dump_type` is the field, not the DumpType.** An E dump (DumpType 10) says `dump_type = 0`, an H dump (11) says 1; that it
  is frequency-domain is the `/FieldData/FD` group. Only the raw dump says 29.
- **Mesh lines are always metres.** `mesh_scaling` beside them is the setup's drawing unit (1e-3 for mm) and is never to be
  applied. The raw dump's `/Mesh` lines are cell **centres** (`dual_mesh = 1`), with widths in `/CellWidth`.
- **The DiscMaterial is x-fastest with no `d_order`.** CSXCAD indexes its flat buffer ix + iy·Nx + iz·Nx·Ny, so the
  dataset's dimensions are (nz, ny, nx); every other openEMS file is C order over (x, y, z), circuitRF's own.
  `OpenEmsArrayOrder.ToCsxcadOrder` is the one permutation, and CSXCAD itself is the gate: a transposed 3 × 2 × 2 file gives
  the wrong tissue to 10 of its 12 voxels. The legacy `ZYX` order (`--legacyHDF5Dumps`) is refused, naming `d_order`.
- **The index is one byte**, so one DiscMaterial holds at most 255 tissues besides air; SAR-11 refuses more, naming the limit.
- **`sar_calc`'s exit status is inverted** (it returns its success bool: 1 is success). Judge it by its output file.

Gates: `tests/Ui.Tests/Sar/OpenEmsHdf5Tests.cs`, `DiscMaterialWriterTests.cs`. Detail: `docs/design/sar.md` §6.

## `MatReader`: MAT v5 padding is relative, and the repo had no v5 writer to test against (brief-modelcomposer-3, 2026-10-19)

- **Padding after a data element is relative to the element, not to the file.** A compressed (`miCOMPRESSED`) element
  is not padded at all, so every element after one starts at whatever offset the zlib stream ended on. Aligning the
  next tag to an absolute 8-byte boundary — the obvious reading of "elements are 8-byte aligned" — reads garbage as
  the second variable's tag and refuses the file as truncated. The reader pads by `(size + 7) & ~7` from the
  element's body; inside a matrix the sub-elements are relative to the matrix, which is the same rule.
- **`MatWriter` writes v7.3 (HDF5) only.** The brief's gate asked for "a v5 file written by `MatWriter`"; there is no
  such writer, so `MatReaderTests` builds its v5 file byte by byte from the published layout (column-major, one
  compressed element, a 1 × 1 struct) and compares it with a v7.3 file from `MatWriter`.
- **Order convention:** everything comes back C order over MATLAB's logical shape. A v7.3 dataset carrying
  `MATLAB_class` has its HDF5 dimensions reversed and is transposed like v5; a dataset without one (`MatWriter`'s,
  h5py's) is taken as stored. Without that split one of the two writers' 3 × 4 matrices reads back as 4 × 3.

## The Z-plane auto-view box cut off the MXP it was built around (2026-10-20)

`LoadpullSurface.RecommendedBox` rounds a Z-plane box to a grid of a fifth of its width with `Math.Round`, which moves
each edge to the NEAREST grid line — inward by up to half a step. On the Model Composer's GaN loadpull the MXP sat at
about 36 Ω, the box built around it started at 30.8 Ω (VSWR 1.3), and rounding moved that to 39 Ω: the contour fill,
and the Rect plot autoscaled to it, ended past the MXP, which drew half off the plot's edge. Edges now round outward
and are clipped to the measured extent again (`RoundOutward`). Gate: `LoadpullSurfaceTests.RecommendedBox_ZPlaneRounding_NeverCutsInward`.
A `.cdd` saved before the fix keeps its stored window; deleting it lets the next run re-create the display.
## SignalCache: a shared generation must not take a caller's cancellation token (2026-10-21)

`SignalCache.GetAsync` hands every caller asking for one signal the SAME generation task, and it was
started with the FIRST caller's token. The Signal Designer cancels a preview whenever a newer one starts
(and its own auto-preview is the first caller), so under load the second, live preview awaited a task
the first had cancelled and came back empty — and a simulation waiting on the same cached signal would
have failed the same way. The generation now runs uncancellable; each caller stops WAITING with
`Task.WaitAsync(ct)`. Only the first caller receives progress reports; later callers just wait.

## Signal EVM: what a generated record reads, and why it is not exactly zero (2026-10-22)

`SignalEvm.Measure` demodulates a carrier's own native record (FFT right after each cyclic prefix, where
the WOLA ramps do not reach) against the QAM points the generator drew, regenerated from the seed through
the generator's own `DrawRow`/`BinOf` so the two cannot drift. Unshaped and unclipped, the record
demodulates back to its symbols to < 1e-6 %, which is the check that the timing and bin mapping are
right. With the default -80 dBc shaping it reads a small, real EVM (0.17 % LTE 20, 0.02 % NR 20 at
1024-point 30 kHz): trimming each symbol's out-of-band skirts across the whole record leaks a little
back in-band. Clipping is what moves EVM in practice (NR 20 64QAM clipped to 8 dB: ~1.7 %).
One complex gain is fitted over the record, with no per-subcarrier equaliser, so the number is the
distortion the source itself adds.

## Clipping now meets its target at 4x oversampling (2026-10-22)

`OfdmGenerator.Clip` clipped each carrier at its native rate. For a 20 MHz carrier that is 30.72 MS/s, only ~1.5x
the occupied band, so the true peaks fall between samples. A clip to 8 dB read 7.9 dB natively and 9.2 dB peak
(8.7 dB at 0.01 %) at 122.88 MS/s, which is what the Designer previews and the amplifier sees. It now clips the
record resampled up 4x (exact, periodic and band-limited) and filters the clipping noise to the occupied band. It
iterates the peak at 4x to within 0.05 dB, restarting each pass from the original with the threshold moved by the
miss, and keeps the closest result. It then resamples back down, which is exact because the result lies inside the
native band. Result: peak at 4x within 0.1 dB of target for LTE and NR at 7, 8 and 9 dB (S6 theory).
`GeneratorVersion` is now `crf-signals-2`, so cached clipped signals regenerate. Unclipped signals are unchanged.

The old S6 test measured PAPR on the native samples, the same place the clip works, so it could not see this.
Clipping stays per carrier: a multi-carrier composite still exceeds the target, because the carriers add.

### Then: peak cancellation, and the target as a ceiling (same day)

Clip-and-filter kept only the in-band share of each cut, so it cancelled only part of each peak's excess. It had
to clip lower and more often to reach the target, and every extra cut became in-band error. `Clip` is now peak
cancellation: each pass subtracts the occupied-band kernel (peak 1) scaled to exactly each local maximum's excess.
It touches only the peaks, and every correction is in-band, so ACLR does not move. At the same PAPR, EVM drops by
10-23 %. NR 20 64QAM: 9 dB 1.17 -> 1.04 %, 8 dB 2.44 -> 2.07 %, 7 dB 4.86 -> 3.96 %. The target is now a CEILING: the
4x peak finishes at or under it (all cases 0.013-0.02 dB under), and with it the 0.01 % PAPR. A +/-0.05 dB window had
let the peak end at 8.04 dB, and because peak cancellation stacks many peaks at the threshold, the 0.01 % figure
followed it over. Generator `crf-signals-3`.

### Then: the target judged at 8x, the work done at a power-of-two length (same day)

A 4x peak still misses the peaks of a wide carrier. NR 100 clipped to 9 dB at 4x read 9.17 dB at the Designer's 8x
display rate (983.04 MS/s), so the user would have seen the target missed. The outer loop now steers the threshold on
the 8x peak (`PaprOversample`); the cancellation passes stay at about 4x (`ClipOversample`). Doing the passes at 8x as
well took NR 100 to two minutes per clip. The passes also now run at the next power of two at or above 4x: an LTE/NR
record is 2^k x 15 samples, and Bluestein FFTs at that length were most of the cost. Periodic band-limited resampling
is exact at any length. NR 20 clips in ~3 s (was ~15 s at 4x), NR 100 in ~18 s. Every case lands at or under the
target at 8x and at the display rate, peak and 0.01 % (NR 100 9 dB: 8.99 / 8.82 dB). EVM rises slightly because the
stricter judgement clips a little deeper (NR 20 64QAM 8 dB: 2.07 -> 2.21 %). Generator `crf-signals-4`; S6 gains an
NR 100 case and checks the display rate.

### Carrier frequencies take units (2026-10-22)

`BW`, `SCS` and `Off` in a carrier description accept `Hz`, `kHz`, `MHz` or `GHz` (exact case, since `m` is milli
everywhere else); a bare number is still Hz. The prefix is applied by re-reading the literal with its exponent, so
`20MHz`, `20000kHz` and `20e6` parse to the same double. The cache key is therefore unchanged, and the generated
records are identical (S10). `FormatCarrier` writes BW and Off in MHz and SCS in kHz, but only when the text
reads back as the same double; otherwise it writes plain Hz.

### Custom I/Q: four FILE-carrier defects the Load Custom work exposed (2026-10-22)

A FILE carrier existed before the Signal Designer could load one as the whole signal, and four things were wrong with
it. Each was silent:

- **Its bandwidth was 20 MHz whatever the file held.** Nothing set `CarrierSpec.BandwidthHz` for a file, and the
  envelope rate, the preview's span, `RateFor`'s refusal and the ACLR bands all read it. The bandwidth now comes from
  `# channel_bw_hz`, or is measured: twice the furthest 99 % edge, over 0.9. `CarrierRecord.Spec` carries it, and the
  cache stores it, since a cache read rebuilds the record from the netlist's spec.
- **A relative `File=` resolved against the process's working directory.** The elaborator passed carrier strings
  through verbatim. It now maps the `File=` field alone through the Touchstone rule (`SignalSpec.MapFilePath`, so
  hand-written fields survive), and the Designer stores and resolves the path with `SnpPathPolicy`.
- **An edited file was served from the cache**, because the key covered the path and not the content. The key now
  includes each file's length and write time.
- **An envelope run refused any record that 1.92 MHz × 2^k does not divide**, and an arbitrary capture's N / fs is
  usually such a record. `SignalRate.AtLeast` falls back to the file's own rate × 2^k, which always divides it. All
  three engine sites use it.

Clipping now applies to a file carrier as well (`OfdmGenerator.Clip` works at any length). `Fs=` gives a headerless
file its rate. Generator `crf-signals-5`.

### DBPSK and CSS carriers: the rate floor, and C-mode meter-to-other is not Gaussian (2026-10-26)

- **`SignalRate.AtLeast` started every search at 1.92 MHz**, the LTE / NR ladder's base, and a ladder rate that divides
  the record always won. A 100 b/s DBPSK uplink needs ~3 kHz and its 10.22 s record divides 1.92 MHz exactly, so an
  envelope run would have stepped 20 million times per record — 600 × its own native rate. A need below the ladder's
  base now climbs from the narrowband carriers' own rates first (FSK, DBPSK, CSS, FILE); above it nothing changed.
  `SuggestedCompositeRateHz` takes the same exception for the Designer's Auto rate.
- **The Designer's `RateFor` let a standard rate through for a seconds-long record**, and `LengthAt`'s `int` overflows
  past 2^31 samples (10 s at 1966 MS/s is 2 × 10^10), so the refusal it was given has to test the size before calling
  it. Over 2^25 samples is refused, naming Auto and Native rate.
- **Wireless M-Bus C mode's meter-to-other link (868.95 MHz, 100 kchip/s, ±45 kHz) is plain 2-FSK.** Only the
  other-to-meter link (869.525 MHz, 50 kchip/s, ±25 kHz) is GFSK, BT 0.5. The C1 preset had the Gaussian filter on.
- **DBPSK with hard phase flips spreads 99 % of its power over ±9 Rb**, far from the ~100 Hz a UNB uplink occupies; a
  raised-cosine pulse two bits long keeps 99 % within ±0.7 Rb, and putting its peak ON a sample makes each bit edge a
  sample too, so a reversal is exactly zero and two equal bits exactly flat. A record whose data period holds an odd
  number of 0s ends phase-reversed, so it is generated twice over (PN9: 1,022 bits).
- **A chirp symbol `s` starts `s / 2^SF` of the way up the sweep**, i.e. the base up-chirp rotated EARLIER by `s` chips;
  rotated later it dechirps as `2^SF − s`. And at more than one sample per chip the FFT bin of the dechirped tone is
  already the symbol (bin spacing fs / N = BW / 2^SF); scaling it by 2^SF / N again is wrong.

## Binary simulation datasets (`.ds`): what the reader keys on, and the traps (2026-10-31)

`BinaryDatasetReader` / `BinaryDatasetWriter` (`src/RfCore/Export`). The container is described field by field in
the reader's header; these are the parts that are easy to get wrong:

- **Recognised by structure, never by its header text.** The first 13 bytes carry the writing program's own text and
  are not read; the signature is CR LF 0x1A at 13, `0xCEFAEDFE` at 0x10 and `DCBA` at 0x20. The writer puts
  `circuitRF` there.
- **The directory's third field is pages per page-list entry**, 1 in container version 3 and 8 for version 4's value
  objects. Reading it as a count of 1 truncates every value object of a version-4 file to its first page, with no
  error.
- **The key table is matched to objects by LINK id, not by order of appearance.** Key n (from 1) names the object
  whose LINK page carries id n + 1. When one block's keys are interleaved with another's, matching by key order
  reads the wrong values with no error.
- **Strings resolve through the pool's own index** (25-byte entries), not by splitting the pool on NULs. A pool
  can also hold text values (port names), so counting strings goes wrong as soon as there is one.
- **Only a full grid is read.** Counts come from the sweep records and must agree at every level, and every outer
  point must sweep the same inner values; otherwise the file is refused, naming the variable. Dividing the stored
  value count by the outer count would accept an uneven sweep and lay it out wrong without a word.
- **A voltage source's current is recorded flowing into the source**, i.e. out of the device. The array importer's
  map therefore takes a leading `-` (`-DC1.VD.i=id`) to read an array negated, and the drain-current sign flag
  names that spelling for an array import.
- **Writing a `DataSet`** (`BinaryDatasetWriter.FromDataSet`, `ExportFormat.Ds`): a `Z0 [port]` has no sweep axis,
  and written on its own it became a separate one-point block, so it read back as a different group (`SP1_2.Z0`).
  It is written as `PortZ[p]` at every point of the S sweep instead, beside S in S's block.

## SnP reciprocity: the tolerance that ties S(i,j) to S(j,i) (brief-sens-2, 2026-11-01)

`RFNetwork.Reciprocity(SNP)` is the worst |S(i,j) − S(j,i)| over a file's own points and every pair, in absolute
LINEAR terms (a −40 dB entry 3 dB apart differs by 0.003). `SnpPerturbation.ReciprocalTolerance` is the line
`Reciprocal=auto` ties at. It was fixed at **0.01** after measuring all 166 Touchstone files under `examples/` and
`testdata/` (3D-EM results from Palace, EMerge and the FDTD reference runs among them):

| Group | Files | Worst \|S(i,j) − S(j,i)\| |
|---|---|---|
| 1-ports, and 2/4/5/6-ports written symmetric (spirals, test pad, pads, hero 1B, the gap-capacitor DC row) | 63 | exactly 0 |
| 3D-EM and port-calibration results, passive | 26 | 6.4e-11 … 1.9e-4 (EMerge cavity), 6.2e-4 … 1.6e-3 (FDTD coupled pairs) |
| the worst passive file: a Palace coupled-microstrip 4-port | 1 | **5.5e-3** (S41, 2.2 dB on a weak entry) |
| the least non-reciprocal ACTIVE file: a pinched-off FET bias point (Vgs −1.6 V) | 1 | **0.117** |
| the shipped amplifiers (`potentially_unstable_amp.s2p`, `fet_bias.s2p`), hero 1's golden 4-port | 6 | 4.99 … 10.5 |

0.01 sits 1.8× above the worst passive file and 11.7× below the least non-reciprocal active one. Two things worth
knowing: a passive 3D-EM result is reciprocal only to its solver's accuracy (the coupled-microstrip's 5.5e-3 is on a
−30 dB entry), so a tolerance much tighter than ~1e-2 would untie real passive structures; and a FET held in
pinch-off is not reciprocal at all (S21 ≠ S12 by 6 dB), so "passive at this bias" does not imply tied.

## The network inverse and TRL: what the algebra allows (brief-de-1, 2026-11-07)

`NetworkInverse` and `TrlCalibration` are the math under the Deembed component and the `trl` analysis. Three findings:

- **The inverse of a network often has no S-matrix, even when the network is ordinary.** `F⁻¹`'s cascade matrix, in F's
  own blocks, is `[[S12⁻¹, −S12⁻¹·S11], [S22·S12⁻¹, S21 − S22·S12⁻¹·S11]]` (block inversion of T_F, whose Schur complement is
  exactly S12), so it needs only S12 invertible. Its S-matrix also needs `S21 − S22·S12⁻¹·S11` invertible, and a series
  100 Ω in a 50 Ω system fails that (det S = 0): the series −100 Ω that undoes it shorts the two reference terminations into
  each other. Anything that STAMPS an inverse must use the cascade form. `TryInvert` (the S form) is for files and plots.
- **TRL is written in the eigenvectors of `Ml·Mt⁻¹`, not in ratios of their entries.** The usual ratio form divides by
  the fixture's S22, which a perfectly matched fixture does not have. Writing A = [u w]·diag(p, q) and B = A⁻¹·Mt leaves
  only p and q: the Reflect fixes p/q, and A's reciprocity fixes p·q. The left eigenvectors for B are never needed.
- **Two signs are free per frequency, and both must be tracked across frequency**: which eigenvalue is e^(−γℓ), and the
  sign of S21_A's square root. The part de-embedded with the fixtures depends on neither (only products), so dropping the
  tracking looks harmless. It is not: untracked, fixture A's S21 flipped sign at 7 GHz on the test bench, and above 180° of
  line every answer was wrong (`TrlCalibrationTests.WithoutContinuityTracking_…`). A fixture file whose S21 flips between two
  samples interpolates through zero, and the Deembed reads its file by interpolation. Tracked, both fixtures come back to
  1e-10 through 333° of line.

## TRL's usable band at its exact edges (brief-de-5, 2026-11-07)

A Line cut for exactly the 8:1 band reads 20° and 160° at its edges only to rounding, which can fall either side from one
runtime to the next and turn the band's end points into a warning. `TrlCalibration` counts a point within 1e-6° of an edge
(`UsableEdgeSlackDeg`) as inside. `DataCube.Mag` on a real cube is now its absolute value (`src/Core/RESOLVED.md`).

## Two-port noise math, and the Touchstone noise block written back (2026-11-09)

`Data/NoiseMetrics.cs` is the noise math the S-parameter noise analysis builds on: noise parameters ↔ the chain
correlation C_A, the noise-wave correlation Cs → C_Y → C_A → parameters, an independent "straight from Cs" path
(Wedge & Rutledge 1992), F(Γs), constant-F circles and a realizability check. `TwoPortNoise(Fmin, GammaOpt, Rn, R)`
carries the reference its Γopt is on.

- **Conventions.** Spectral densities are single-sided and absolute (a matched load's outgoing wave is kT W/Hz, a
  resistor's voltage 4kTR V²/Hz). F is referred to T0 = 290 K with k = 1.380649e-23 J/K exact; RfCore cannot reach
  `Core`'s `Temperature.Boltzmann`, so the constant is repeated once. Noise waves are Kurokawa power waves with every
  port in its own noiseless Z0. C_A puts v in series and i in shunt at the input, so
  F(Ys) = 1 + (C11|Ys|² + 2Re(Ys·C12) + C22)/(4kT0·Re Ys), which fixes `T = [[0, −1/Y21], [1, −Y11/Y21]]` for
  C_A = T·C_Y·Tᴴ with port currents I = Y·V + i. Γ is always on a REAL R: the option line's R for a file, Re Z0 of port 1
  for a run.
- **`RFNetwork.SToY` is not the power-wave Y for a complex Z0.** It is the √Z0 form, which agrees with power waves only
  for a real reference (the gap `TraceResolve.ResolveNetworkParamCube` already documents). C_Y needs the power-wave Y,
  so `NoiseMetrics` renormalises S to Re Z0 with the power-wave `SToS` first and converts there. With plain `SToY` the
  Bosma check (a passive network at T, Cs = kT(I − S·Sᴴ), must give C_Y = 2kT(Y + Yᴴ)) fails for complex Z0 and passes
  for real Z0, so a real-Z0 test alone would not have caught it.
- **The textbook direct forms all checked out**, two rewritten. Γopt = η2(1 − √(1 − 1/|η2|²)) divides by
  ζ = c22·S11 − c12·S21, which is zero exactly when Γopt is; rationalised it is Γopt = 2ζ*/(h + √(h² − 4|ζ|²)),
  h = c22 + η1, with no special case. Rn is not in that set; it is the limit of F·(1 − |Γs|²) at Γs = −1,
  Rn = z0·(c22 + η1 + 2Re ζ)/(4kT0|S21|²), which needs no division by |Γopt|² (the route through F at Γs = 0 does).
- **Realizability** is det C_A ≥ 0, and det C_A = (4kT0)²·a(2·Rn·Gopt − a) with a = (Fmin − 1)/2, so the check is
  0 ≤ Fmin − 1 ≤ 4·Rn·Gopt. A C_A with C11 = 0 (a noiseless two-port) has no finite optimum and reads Fmin = 1, Rn = 0.
- **Touchstone.** `TouchstoneIO.Write` now emits the 1.x noise rows (freq, NFmin dB, |Γopt|, ∠Γopt°, rn) after a
  two-port's data, and `DataSetBuilder.ToSnp` fills `SNP.NoiseParameters` from cubes named `NFmin` (dB), `Sopt` and
  `Rn` (Ω), each [freq], in the S cube's group. Nothing is re-referenced on write: the rows are stored against Re Z0, and
  the option line's R is Re Z0 in both write modes (strict 1.1 renormalises the S data to exactly that). The reader is
  1.x-only, so rn is always taken as Rn/R. The writer adds no header comment of its own, so a read → write → read keeps
  the file's own noise comments without doubling them.

Gates: `tests/RfCore.Tests/NoiseMetricsTests.cs` (Pozar 4th ed. Ex. 12.5, the C_A round trip at R = 50 and 25, Bosma
with real and complex Z0, the two paths against each other, F from a complex-Z0 source) and
`TouchstoneNoiseBlockTests.cs` (write → read at R = 50 and 25, strict mode, the cubes through `ToSnp`).

## EVM of a received record: SignalEvm.Demodulate / Evaluate (brief NZ-8, 2026-11-09)

The Signal Designer's EVM demodulates a carrier's own native record. An envelope run hands over a probe's envelope at
the envelope rate, possibly delayed, possibly a part of the record. `Demodulate` brings it back to the native grid and
`Evaluate` scores it with the Designer's own gain fit (`Score`, shared with `Measure`).

- **Whole records are resampled exactly; a part of one is not.** A window of whole records is periodic, so cutting its
  spectrum at the native Nyquist is `Fft.ResamplePeriodic` and the native samples come back exactly (E6: 1e-9 pp).
  Any other window wraps, and a brick wall would ring as 1/t across the symbols, so it rolls off as cos² from the
  channel edge (the record's own content ends there) to the native Nyquist, and symbols within a few samples of either
  end are left out. That roll-off is a receive filter, and cutting the skirts of a record whose skirts reach the native
  Nyquist (an unshaped one) costs 0.017 pp; a shaped record (the -80 dBc default) reads within 0.01 pp (E7).
- **No per-symbol FFT at the envelope rate.** A DFT over the useful part at R× the native rate is orthogonal for
  subcarriers but not for the band-limited interpolation between native samples, which carries the next symbol's edge
  into the window; demodulating the native samples is what the Designer does.
- **The delay is fitted against the record the source played, not the ideal points.** H = Σ_s Y·conj(D) per subcarrier
  is a pure phase line for a pure delay, and for a matched pad it is a constant — exactly zero delay, checked as such
  (`IsFlatPhase`) so the result is the Designer's. Fitting against the ideal points would let the delay soak up a
  share of the clipping error and read below the Designer's EVM. The peak of H's 8x zero-padded transform, refined by
  golden section, finds a 37 ns delay to 1e-12 s (E6). A delayed clipped record reads 0.002 pp differently: each FFT
  window then reaches into the cyclic prefix, which clipping has made not quite a copy of the symbol's end.
- **The envelope rate need not be a multiple of the carrier's.** p/q in whole numbers (q ≤ 4096) is enough: a 15 MHz LTE
  carrier at 23.04 MHz under a 122.88 MHz envelope is 16/3.

## DBPSK, FSK and CSS quality: SignalQuality (brief NZ-8 follow-up, 2026-11-09)

The owner asked why only LTE / NR had an EVM: the other generated carriers carry known data too. A QAM EVM fits only
DBPSK, so each is measured as its receiver reads it (`SignalQuality.cs`, tests `SignalQualityTests`).

- **One measurement filter on both sides.** The received window and the sent record pass through the same cos² roll-off
  from the channel edge (DBPSK Rb, FSK Dev + Rb, CSS BW/2) to 1.5x it, so the measurement bandwidth is the channel's
  and the reference carries the same filtering: a pad reads zero (1e-13 % through an envelope run). Filtering only the
  received side cut the DBPSK pulse's sidelobes beyond its 2 Rb main lobe and left an error floor on a perfect copy.
- **DBPSK** is sampled at the bit centres (native sample 32k + 16), where the generator's two-bit cos² pulse peaks and
  its neighbours are exactly zero; EVM is `SignalEvm.Score` against the filtered sent value normalised to unit RMS.
  EVM² × SNR = 0.95 through the engine with noise.
- **FSK** reads the frequency from the samples either side of each bit centre, `arg(x[c+1] conj(x[c-1]))`, on both
  sides, and fits f_rx = a f_sent + b: deviation a·Dev, offset b, FSK error the RMS residual over the NOMINAL deviation.
  It is not 1/√SNR: noise moves the phase's derivative more than the amplitude (23.5 dB SNR read 11.2 %).
- **CSS** is per chirp: one complex gain of the received chirp against the sent one, the error the energy left over the
  energy fitted. With |s| = 1 that is the energy outside the symbol's FFT bin after de-chirping, which is what a
  receiver reads; one gain per chirp because it reads each symbol on its own. Chirp starts: 10 up-chirps from 0, then
  the payload 2.25 chirps after the sync (the delimiter is skipped).
- **The delay** is fitted from H = X conj(S) over the passband, by `SignalEvm.Delay`. Its flat-phase test now compares
  against the largest element of H: a bin near a null of DBPSK's spectrum carried round-off at any phase and turned an
  exact zero delay into a 1e-15 s fit.

## A display reads a CLI result made ungrouped (issue 66, 2026-11-13)

Every CLI run verb's `-o` writes one analysis's cubes in the default group; Simulate groups them under the analysis's
name. A `.cdd` made from a Simulate result asks for `ENV1.Pavg`, and a CLI file had no `ENV1` group, so every trace was
empty. `DataSet.BindsAnalysisNamesToFlatCubes`, set by the display's loaders (`PlotSourceFile`, the Data Display
library), resolves `Group.Cube` as the bare `Cube` when the DataSet has no analysis group of its own and no cube literally
named `Group.Cube`. It is opt-in so nothing else that resolves specs (measurements, `read`, the engines) changes, and a
grouped file never binds another analysis's name. Gate: `FlatCliResultBindingTests`.
