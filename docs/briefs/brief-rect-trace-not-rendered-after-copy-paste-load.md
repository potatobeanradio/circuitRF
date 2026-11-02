# Brief — Rect trace not rendered after Copy/Paste and after .cdd load (regression)

**Severity:** high (visible data loss on a core workflow). **Scope:** Rect plots only; Smith/Polar unaffected.
**Symptom:** After Copy→Paste of a plot, the pasted Rect plot shows axes/grid but **no trace**. Same when a
`.cdd` is first loaded into memory. Smith/Polar plots paste/load fine. (We fixed a version of this before; it
regressed.)

---

## Root cause (verified on disk)

The single mechanism is in `Plot.RestoreAxesFromConfig` combined with Rect's autoscale fallback.

**1. `RestoreAxesFromConfig` always re-autoscales, discarding the restored window.**
`<repo>/src/Ui/DataDisplay/Models/Plot.cs`:
```csharp
public void RestoreAxesFromConfig(bool autoscaleX, bool autoscaleY, bool autoscaleRightY, bool autoscaleMag,
                                  Rect window, Rect windowSecondary)
{
    _autoscaleX = autoscaleX; _autoscaleY = autoscaleY;
    _autoscaleRightY = autoscaleRightY; _autoscaleMag = autoscaleMag;
    Axes.Window = window;  Axes.WindowSecondary = windowSecondary;
    Autoscale();           // ← if autoscale flags are true (the default), this OVERWRITES the window just set
    Axes.WindowState = Axes.Window;  Axes.WindowSecondaryState = Axes.WindowSecondary;
}
```
With default autoscale flags on, the saved window is immediately replaced by a fresh `Autoscale()` →
`AutoscaleCore`, which frames the window from `t.PathBoundingRect()` over the traces.

**2. Rect autoscale collapses to a tiny default box when no trace has points yet; Smith self-frames.**
In `AutoscaleCore` (same file): if no trace produced a bounding box (`!primarySet`), the window falls back to
`defaultWindow`. For **Rect** (`!SupportsComplex`) that default is `new Rect(0, 0, 2, 2)`. For **Smith/Polar**
the complex branch forces the **unit circle** (`AutoscaleEnforceUnityMinimum` → `new Rect(-1,-1,2,2)` and
`SquareCentredOnOrigin`) regardless of points. So:
- Smith/Polar: always gets a sane window even with zero points → trace appears once points exist.
- Rect: gets the `0..2 × 0..2` box. Real data (freq in GHz on X, dB/linear on Y) lies far outside `0..2`, so
  the trace renders **off-screen** → "not rendered". This is exactly the Rect-only asymmetry the user sees.

**3. Why points are missing at autoscale time during paste/load.**
In `<repo>/src/Ui/DataDisplay/ViewModels/DataDisplayViewModel.cs`,
`LoadPlotContainerConfigAsync` builds each trace and resolves its data **inline** in the trace loop
(`trace.BuildPath(...)` for network; `PlotInspectorViewModel.TrySetCubeData(...)` for cube-bound), then AFTER
the loop calls `RestoreAxesFromConfig(savedAxes...)`. But `TrySetCubeData` only produces points when it can
resolve the cube's `DataSet` from the library (`entry?.Data`). During **paste** (`configDir:""`, source not
yet in library) and **first .cdd load** (entries still loading), `entry.Data` can be null at that instant, so
`TrySetCubeData` clears `Points` and returns. Network traces can hit the analogous gap when the SNP isn't
resolved yet. With no points, the post-loop `RestoreAxesFromConfig`→`Autoscale` produces the Rect default box,
and **nothing re-autoscales after the data later resolves** — so the Rect window stays wrong and the trace
stays off-screen. (Smith masks this because its window is always the unit circle.)

**Net:** the saved-and-correct Rect window is thrown away by an unconditional re-autoscale that runs before the
trace data is reliably present, and Rect's empty-autoscale fallback is a tiny origin box rather than a
data-framing window.

---

## Fix

Two changes; the first is the core fix, the second hardens the Rect fallback. Prefer doing both.

### Fix A — honor the restored window; don't auto-clobber it on restore [CORE]
`RestoreAxesFromConfig` must NOT discard the explicitly-saved window. The saved config already carries the
exact window the user had; restoring it should set it, not recompute it. Change the method so it only
autoscales an axis when there is **no** valid saved window for it (or, more simply, set the windows and skip the
unconditional `Autoscale()`):

```csharp
public void RestoreAxesFromConfig(bool autoscaleX, bool autoscaleY, bool autoscaleRightY, bool autoscaleMag,
                                  Rect window, Rect windowSecondary)
{
    _autoscaleX = autoscaleX; _autoscaleY = autoscaleY;
    _autoscaleRightY = autoscaleRightY; _autoscaleMag = autoscaleMag;

    Axes.Window          = window;
    Axes.WindowSecondary = windowSecondary;

    // Only (re)autoscale an axis whose saved window is missing/degenerate — never clobber a valid
    // saved window. A valid window has positive width AND height. This preserves the user's exact
    // saved view and prevents the empty-points Rect autoscale from scrolling the trace off-screen
    // when data resolves after restore.
    bool windowValid          = window.Width > 0 && window.Height > 0;
    bool windowSecondaryValid = windowSecondary.Width > 0 && windowSecondary.Height > 0;

    if (SupportsComplex)
    {
        if (!windowValid && _autoscaleMag) RunAutoscaleBoth();   // or Autoscale() — complex self-frames anyway
    }
    else
    {
        if (!windowValid)          { if (_autoscaleX) RunAutoscaleX(); if (_autoscaleY) RunAutoscaleY(); }
        if (!windowSecondaryValid) { if (_autoscaleRightY) RunAutoscaleRightY(); }
    }

    Axes.WindowState          = Axes.Window;
    Axes.WindowSecondaryState = Axes.WindowSecondary;
}
```
> Implementation note: `RunAutoscale("x"/"y"/"rightY"/"both")` is the existing private entry (`RunAutoscale`
> calls `AutoscaleCore`). Use those strings rather than inventing new methods — e.g.
> `if (_autoscaleX) RunAutoscale("x");`. Keep the public `Autoscale()` untouched for other callers.

This alone fixes the reported bug: paste/load saves a valid Rect window, so it is honored as-is and the trace
is framed correctly the moment its points exist (the renderer reads the window each frame).

### Fix B — Rect empty-autoscale fallback should not be the origin box [HARDENING]
Independently, make the Rect "no points yet" autoscale fall back to a window that will still show data once it
arrives, OR — cleaner — ensure a re-autoscale happens after the data resolves. Two acceptable options
(pick one; B2 is lower-risk and matches the "we fixed it before" pattern):

- **B1 (fallback window):** in `AutoscaleCore`, when `!primarySet` on a Rect plot, prefer leaving the existing
  window unchanged if it is valid, rather than overwriting with `new Rect(0,0,2,2)`. (Guard: only overwrite a
  degenerate/zero window.)
- **B2 (re-autoscale after data resolves) — RECOMMENDED:** after `LoadPlotContainerConfigAsync` finishes a
  container whose traces are now resolved, call `plot.Autoscale(force:true)` IF the saved window was absent/
  degenerate OR if any trace's points were empty at restore time. The natural place: the post-load library
  refresh path already re-resolves cube/network data — ensure it ends with a `plot.Autoscale()` for plots whose
  axes are in autoscale mode. (See `PlotInspectorViewModel.OnLibraryChanged` / `RebuildAndNotify`, which already
  call `_plot.Autoscale()` after re-resolving data — confirm the pasted/loaded container's inspector actually
  receives a library-changed/rebuild pass once its source finishes loading; if it does, Fix A is sufficient and
  B is belt-and-suspenders.)

> Why both: Fix A stops the active clobber (the proven bug). Fix B ensures that even a plot saved WITHOUT a
> valid window (older config, or a freshly authored plot) still frames its data once it loads. If you only do
> one, do Fix A.

---

## Verification (owner-run)

1. **Copy/Paste Rect:** open a `.cdd` (or build a Rect plot with a network or cube trace that renders), Copy the
   plot, Paste. The pasted Rect plot shows the trace immediately, framed identically to the source. Repeat for a
   cube-bound Rect trace (loadpull/HB result) and a network S-param Rect trace.
2. **First .cdd load:** load a `.cdd` containing Rect plots from cold. All Rect traces render framed correctly,
   not off-screen.
3. **Smith/Polar unaffected:** paste/load Smith and Polar plots — still correct (regression guard).
4. **Saved zoomed window survives:** zoom/pan a Rect plot to a non-autoscale window, Copy/Paste — the pasted
   plot preserves the saved window (Fix A must not re-autoscale a valid saved window).
5. **Autoscale toggle still works:** toggling AutoscaleX/Y on a restored plot still reframes to data.

---

## Constraints / gotchas
- Do NOT change the meaning of the public `Autoscale()` — other callers rely on it. Scope the change to
  `RestoreAxesFromConfig` (+ optional `AutoscaleCore` Rect fallback).
- A "valid" saved window = positive Width AND Height. Treat zero/negative as "autoscale this axis".
- Complex (Smith/Polar) already self-frames to the unit circle; the change must not alter that (it shouldn't,
  since complex autoscale is unconditional inside `AutoscaleCore`).
- `Axes.WindowState`/`WindowSecondaryState` must still be synced at the end (used by pan/zoom reset).
- TreatWarningsAsErrors: no unused locals; nullable property reads → locals.

## Tests
- Owner-verified per the steps above (the failure is a UI/timing render issue; hard to unit-test fully).
- If a unit test is cheap: construct a Rect `Plot`, add a trace with known points, call
  `RestoreAxesFromConfig(autoscaleX:true,...; window:<valid saved window>)`, assert `Axes.Window` equals the
  saved window (NOT a recomputed autoscale box). Then call with a degenerate window and assert it autoscales to
  frame the points.
