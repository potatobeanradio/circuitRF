# Brief — Gate 6 Round 1 / C: spectral (stem) InfoBox — freq/harmonic rows + value refresh

**Status:** Ready to implement (includes one instrument-first step — read it before coding)
**Scope:** Two spectral-marker InfoBox bugs: (1) the row content/labels are wrong; (2) the reported value goes stale when the marker is dragged to a new harmonic. Single-tone harmonic stems only (`mixIndex`/two-tone is noted but out of scope — no test case yet).
**Depends on:** Gate 4 (stem markers) landed. Independent of Briefs A/B/D.

## Confirmed facts (from the owner + the HB design doc — do not re-derive)
- The plot is **single-tone**: cube X-axis name is `"harmonic"` (`Trace.IsHarmonicStem` true).
- `CubeXValues` holds **harmonic indices** `0, 1, 2, 3, …` (integer-valued; may be stored as `double`). NOT frequencies.
- A stem marker stores its harmonic X-value in `marker.PositionStatic.X` (Gate 4). For the harmonic axis `xScale == 1`, so `Points[i].X == CubeXValues[i]`.
- Current stem InfoBox lines (in `Trace.BuildMarkerBoxLines`, stem branch): `MarkerString`, then `GetStemOrderString(m)` = `$"harmonic={m.PositionStatic.X:G4}"`, then `GetStemValString(m, …)`.
- Physical frequency of harmonic `k` is `k · f0`, where `f0` is the analysis fundamental tone. **Whether the trace can currently reach `f0` is unknown — Step 0 settles it.**

## UI build gate
UI builds with `TreatWarningsAsErrors=true`.

---

## Step 0 — INSTRUMENT FIRST (do this before writing the fix)

We need ground truth on two things before coding: (a) what frequency source is reachable for the `freq=` row, and (b) why the value row goes stale. Add temporary `Console.WriteLine` (visible in the `dotnet run` terminal — NOT `Debug.WriteLine`), run, drag a stem marker across two harmonics, and read the terminal.

In `Trace.GetStemValString` (or wherever you compute the stem value), temporarily log:
```csharp
Console.WriteLine($"[STEM] PosX={m.PositionStatic.X} idx={idx} " +
    $"xUnit={CubeXUnit ?? "<null>"} xName={CubeXAxisName} " +
    $"val={val} xvals0..2=[{(CubeXValues!=null&&CubeXValues.Count>0?CubeXValues[0]:double.NaN)}," +
    $"{(CubeXValues!=null&&CubeXValues.Count>1?CubeXValues[1]:double.NaN)}," +
    $"{(CubeXValues!=null&&CubeXValues.Count>2?CubeXValues[2]:double.NaN)}]");
```
Also check whether the trace/cube exposes a fundamental frequency or a sibling `freq` coordinate. Inspect (read, don't guess):
- `Trace`'s cube fields and any `f0`/tone/`freq` the owner stamps when binding a harmonic cube (look in `PlotInspectorViewModel.TrySetCubeData` and how the `"harmonic"` axis is set up — is there a companion `freq` axis on the source `DataCube`, or a tone frequency available to the trace?).
- The source `DataSet`/`DataCube` for a `freq` axis or scalar.

**Report the terminal output and the freq-source finding to the owner if the `freq=` source is not obvious**, then proceed using whichever of the branches below matches reality. The two non-freq fixes (harmonic row + value refresh) do NOT depend on Step 0 and can be done regardless.

---

## Fix 1 — Row content + labels

Target InfoBox rows for a single-tone stem marker (top → bottom):
```
m1                       (MarkerString, bold)
freq=<freq> <Units>      (physical frequency of this harmonic)
harmonic=<index>         (integer harmonic order, no decimal)
<desc>=<value>           (the stem's value, unchanged)
```

### 1a. Harmonic row (certain — `PositionStatic.X` is the index)
Replace `GetStemOrderString`:
```csharp
/// <summary>Integer harmonic order the stem marker sits on (no decimal). Single-tone.
/// Two-tone (mixIndex) would format the (k1,k2) pair — not supported yet.</summary>
public string GetStemOrderString(Marker m)
    => $"harmonic={(int)Math.Round(m.PositionStatic.X)}";
```
(`PositionStatic.X` is an integer harmonic index; round-and-cast guards the double storage. Output e.g. `harmonic=2`.)

### 1b. Freq row (depends on Step 0)
Add a `GetStemFreqString(Marker m)` that returns `freq=<freq> <Units>`. Implement the branch Step 0 confirmed:

- **Branch A — a sibling `freq` coordinate exists on the cube/trace** (preferred): look up the frequency at the marker's harmonic index `idx` and format with the frequency unit. Use the existing freq-formatting helper the InfoBox/freq-string path uses (`m.FreqString` formats `m.Freq` with `m.FreqUnits`; reuse that formatter on the looked-up Hz value). Pseudocode:
  ```csharp
  double fHz = /* sibling freq[idx] in Hz */;
  // format with the plot's FreqUnit like FreqString does:
  double scaled = fHz * m.FreqUnits.Scale();
  return $"freq={scaled:G6} {m.FreqUnits.Description()}";
  ```
- **Branch B — only `f0` (fundamental tone) is reachable**: `fHz = harmonicIndex * f0`, then format as above.
- **Branch C — neither is reachable from the trace**: the trace cannot currently produce a physical frequency. **Do not fabricate one.** In that case, report back to the owner that the harmonic stem trace needs `f0` (or a `freq` axis) plumbed from the HB cube before the `freq=` row can be correct, and ship Fix 1a (harmonic row) + Fix 2 (value refresh) only, leaving the freq row out. (We'll add a tiny follow-up brief to plumb `f0` once we know where it lives.)

Wire whichever branch applies into the stem branch of `BuildMarkerBoxLines`:
```csharp
if (IsHarmonicStem)
{
    var lines = new List<(string, bool)> { (m.MarkerString, true) };
    var fline = GetStemFreqString(m);          // null/empty in Branch C
    if (!string.IsNullOrEmpty(fline)) lines.Add((fline, false));
    lines.Add((GetStemOrderString(m), false));
    lines.Add((GetStemValString(m, showFilePrefix), false));
    return lines;
}
```

**Multitone note (no code):** for a future `mixIndex` (two-tone) trace the harmonic row becomes `harmonic=<k1,k2>` (the tone pair) — leave a `// TODO multitone (mixIndex): format (k1,k2) pair` comment but do not implement; there's no test case.

## Fix 2 — Value goes stale on harmonic drag

**Symptom:** dragging the stem marker to a new harmonic updates its position/glyph but the reported `<desc>=<value>` row keeps the old value.

**Diagnosis path (use the Step 0 log):** confirm whether, mid-drag, `GetStemValString`'s computed `idx` actually changes with `PositionStatic.X`. Two candidate causes — the log tells you which:

1. **Refresh not firing / measured-size cache stale.** The stem drag goes through `MoveMarkerToCanvasPoint` → `marker.PositionStatic = (HarmonicX, 0)` → `OnPointerMoved` fires `MarkerMoved` → container `OnContainerMarkerMoved` → `vm.OnMarkerMoved()` → `RefreshSize()` + redraw. If the value row is stale but the *harmonic* row updates, the box IS redrawing and the bug is in the value lookup (cause 2). If **nothing** updates until release, the refresh isn't firing during drag — verify `MarkerMoved` is raised on every move (it is in the stem branch of `OnPointerMoved`) and that `OnContainerMarkerMoved` calls `vm.OnMarkerMoved()` for this container.

2. **Value lookup index doesn't track PositionStatic.X.** In `GetStemValString`, the index is found by matching `CubeXValues[i]` to `m.PositionStatic.X`. Confirm this match recomputes each call (it does — it's not cached) and that `FormatCubeCell(idx, …)` reads the live cube. If `PositionStatic.X` is updated to the new harmonic but `idx` still resolves to the old one, the match logic is off (e.g. comparing against `Points` index vs cube index). The fix is to make `idx` the position in `CubeXValues` whose value equals `PositionStatic.X`:
   ```csharp
   int idx = 0; double bestD = double.PositiveInfinity;
   for (int i = 0; i < xs.Count; i++)
   {
       double d = Math.Abs(xs[i] - m.PositionStatic.X);
       if (d < bestD) { bestD = d; idx = i; }
   }
   ```
   (This is already the intended logic from Gate 4 — verify it's intact and that `FormatCubeCell(idx, …)` is what feeds the row.)

**Most likely:** the harmonic row (Fix 1a) and value row both read `PositionStatic.X` the same way, so once Fix 1 is in and the box redraws on drag, both update together. If after Fix 1 the value still lags, it's cause 1 (refresh) — chase `OnContainerMarkerMoved`.

**Remove all Step-0 instrumentation before finishing.**

## Out of scope
- Two-tone (`mixIndex`) formatting.
- Editor (Brief D), context menu (Brief B), persistence/crash/delete (Brief A).

## Acceptance / verification
1. Build green.
2. Stem marker InfoBox shows, in order: `m1`, `freq=<freq> <Units>` (or omitted if Branch C), `harmonic=<int>`, `<desc>=<value>`.
3. The harmonic row is an integer (`harmonic=2`, never `harmonic=2.000`).
4. Drag the marker across harmonics → **both** the harmonic row and the value row update live on every hop (not just on release).
5. No instrumentation left in the build.

## Report back
- State which freq branch (A sibling-freq / B f0 / C none) applied, and if C, where `f0`/`freq` would need to come from.
- Confirm the value row updates live on drag and name the cause if a refresh fix was needed (refresh-not-firing vs index-mismatch).
- Paste the one-line Step-0 log for a harmonic-2 marker so we have a record of the cube X values.
