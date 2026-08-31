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
shape against its actual buffer length names the culprit in one pass.
