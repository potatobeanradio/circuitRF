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
