# Brief 7.5f — Summary-table polish: harmonic guard + empty-state (src/Ui)

**Phase:** 7.5 (loadpull summary table) — final slice. **Layer:** `src/Ui`. **Depends on:** 7.5a–7.5e (all
landed). **Design:** `circuitRF/docs/design/loadpull-summary-table.md` §2.6 (title), decision 3 (harmonic
deferral).

**Read this first — scope is deliberately small.** Two of the three nominal "polish" items are already done or
not-actionable:

1. **Title compression formatting — ALREADY COMPLETE.** `TableRenderer.SummaryTitle` /
   `FormatCompressionToken` (verified on disk) already produce "Max P-3dB Power Load" / "...Efficiency Load",
   honor `CustomTitle`, trim trailing zeros ("3", "1.5", "0.5"), and right-align. **No work needed.** Do not
   re-touch the title unless a specific defect is found.

2. **Harmonic-loadpull guard/warn — minimal, presence-based (the one real item).** The importer today produces
   NO harmonic structure (`LoadpullFomDialect` maps fundamental quantities only; `SplReader`/`LpcwaveReader`
   build single-`GammaLoad`/`ZLoad` cubes over `{[freq,]gridPoint,pinStep}`). So there is nothing concrete to
   detect yet. The guard must therefore be a **documented, presence-based no-op** that lights up IF a future
   importer ever emits a harmonic-load cube — not speculative parsing of a structure that doesn't exist.

3. **Empty-state nicety (small, real).** A summary Table with columns but no resolvable data (e.g. source not
   yet loaded → `SummaryFreqs` null/empty) renders header-only. That's already correct/non-crashing (verified
   in 7.5c), but a one-line "no data" affordance is a reasonable courtesy. Optional — include only if trivial.

If, after reading the above, you conclude 7.5f is effectively a no-op, **that is an acceptable outcome** — say
so and stop. Don't manufacture changes.

---

## Item 2 — harmonic guard (the actual deliverable)

### 2a. Detection contract (documented convention, presence-gated)
Define the harmonic-load naming convention the guard keys on, so it's a real check now and lights up later:
a harmonic-indexed load termination cube would be named `GammaLoad{n}` or `ZLoad{n}` for harmonic n≥2 (e.g.
`GammaLoad2`, `ZLoad2`), parallel to the fundamental `GammaLoad`/`ZLoad`. This matches the existing
fundamental naming and is the natural extension; document it as the convention a future harmonic importer
should follow.

Add a detector in `PlotInspectorViewModel` (it already holds the dataset and runs `RebuildSummary`):
```csharp
/// <summary>
/// True when the dataset carries harmonic-indexed load-termination cubes (GammaLoad2/ZLoad2/…),
/// which the summary table does NOT use — it targets the fundamental (1f0) only (design decision 3).
/// Presence-gated: returns false for every dataset the current importer produces (fundamental-only).
/// </summary>
private static bool HasHarmonicLoadCubes(DataSet ds)
{
    foreach (var g in ds.Groups)
        foreach (var name in ds.CubesIn(g).Keys)
        {
            // GammaLoad2, ZLoad2, GammaLoad3, … — fundamental GammaLoad/ZLoad have no trailing digit.
            if ((name.StartsWith("GammaLoad", StringComparison.Ordinal)
              || name.StartsWith("ZLoad",     StringComparison.Ordinal))
                && name.Length > 0 && char.IsDigit(name[^1]))
                return true;
        }
    return false;
}
```

### 2b. Warn once, non-fatal
In `RebuildSummary`, after the surface is built and before/after computing cells, emit a one-time warning when
harmonics are present. The summary still computes (fundamental only) — the warning is informational, not a
block. Use the existing app warning seam (RfCore's `RFNetwork.Warn` is RfCore-only; the UI uses its own
Messages/notification path — **confirm how the UI surfaces non-fatal warnings**; e.g. a `Messages` collection or
a toast. If there's an established UI warning seam used elsewhere in this VM or its siblings, use it. If there
is none, log via the same mechanism contour fit-warnings surface, or skip the user-facing toast and just guard
silently — do NOT invent a new notification system for this).

```csharp
// in RebuildSummary, after `var ds = ...` is in hand:
if (HasHarmonicLoadCubes(ds) && !_harmonicWarned)
{
    _harmonicWarned = true;
    // Surface via the established UI warning seam if one exists; otherwise this is a silent guard.
    // Message: "Summary table uses the fundamental (1f0) load only; harmonic terminations are ignored."
}
```
Add `private bool _harmonicWarned;` and reset it to false whenever the source entry changes (so a new dataset
re-warns once). Simplest reset point: at the top of `RebuildSummary` when the resolved entry's FilePath differs
from a stashed `_harmonicWarnedForPath` — or just reset in `OnLibraryChanged`. Keep it simple; a single
per-session warning is acceptable if path-tracking is fiddly.

> **Judgment call for the implementer:** if the UI has no clean non-fatal-warning seam, do NOT build one for
> this. A silent presence-gated guard (the `HasHarmonicLoadCubes` method, wired but only logging) satisfies the
> design's "optionally warn." The design says "use the fundamental and **optionally** warn" — optional is the
> operative word. The detector existing + documented is the durable value; the toast is gravy.

---

## Item 3 — empty-state (optional, include only if trivial)

If you want the courtesy: when `IsSummaryTable(plot)` is true but `plot.SummaryFreqs` is null/empty, the
renderer could draw a faint centered "No loadpull data" line below the header. This is a ~6-line addition to
`TableRenderer` (a guarded draw at the end of `Draw`, mirroring `DrawSummaryTitle`). **Skip it** unless the owner
asks — the header-only render is already correct and non-crashing, and an empty summary table is a transient
state (source resolves → cells populate).

---

## Constraints / gotchas
- Do NOT touch `SummaryTitle`/`FormatCompressionToken` — already correct.
- The harmonic guard must be a true no-op for every dataset the current importer produces (fundamental-only).
  Verify: `HasHarmonicLoadCubes` returns false for a normal loadpull import (cubes are `GammaLoad`/`ZLoad` with
  no trailing digit).
- Don't invent a notification system. Use an existing UI warning seam or guard silently.
- RfCore firewall unaffected (this is all `src/Ui`); the detector reads cube names, no engine change.
- TreatWarningsAsErrors: no unused fields (if `_harmonicWarned` is added, it must be read), nullable locals.

## Tests / verification (owner-run)
1. **Fundamental dataset (the only kind today):** `HasHarmonicLoadCubes` returns false; no warning; summary
   computes normally. This is the critical regression check — the guard must be invisible for real data.
2. **Synthetic harmonic dataset:** if you hand-add a `GammaLoad2` cube to a test DataSet, the guard detects it
   and (if a warning seam is wired) warns once; the summary still computes from the fundamental.
3. **Title regression:** confirm "Max P-3dB Power Load" etc. still render (unchanged from 7.5c).
4. **Phase 7.5 end-to-end:** add columns / auto-fill / toggle optimum / change compression / save+reload all
   still work (no regression from this slice).

---

## Phase 7.5 completion note
With 7.5f, the loadpull summary table is feature-complete: engine accessors (7.5a), importer derived fields
(7.5g), model + persistence (7.5b), renderer (7.5c), header controls + card trimming + live RebuildSummary
(7.5d), auto-fill (7.5e), and this guard/polish (7.5f). Remaining future work (out of scope for 7.5): a proper
harmonic-loadpull import + per-harmonic summary, if/when harmonic datasets arrive.
