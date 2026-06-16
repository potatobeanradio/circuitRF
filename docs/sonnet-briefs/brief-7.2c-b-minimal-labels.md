# Sonnet Brief — 7.2c-b: minimal-label display-name policy (§2.7)

**Context.** Second of three 7.2c briefs (after 7.2c-a cube traces; before 7.2c-c rename). Goal: a trace's
**display name** (Y-axis label strip, Rect Y-axis margin label, marker readout) shows the **least** that still
disambiguates it *within its plot* — computed at the **plot level** over all traces sharing the plot, not baked
per-trace. Today only the source-file prefix is conditionally dropped (`ShowFilePrefix` via
`AppSettingsViewModel.EffectiveShowFilePrefix` when >1 source). Generalize that one toggle to all identity
components.

## What exists now (read first)
- `src/Ui/DataDisplay/Models/Trace.cs` — `Description`/`ShortDescription` → `DescriptionFor(includePrefix)`.
  Cube-bound traces (7.2c-a) currently return a stub `"{prefix}{CubeName}"` flagged for this brief. Identity
  components are separate fields: `SourcePath`, `CubeName`, `Slice` (`AxisSlice[]`), `Transform`; plus the
  network path's `MatrixType`/`Row`/`Col`/`Derived`/`YAxis`.
- `src/Ui/DataDisplay/ViewModels/PlotContainerViewModel.cs` → `UpdateLabelStrips()` builds `LabelStripViewModel`s
  per trace and sets `ShowFilePrefix`. This is the **plot-level seam** — the policy is computed here (the
  container sees `plot.Traces`).
- `LabelStripViewModel.CustomLabel` already overrides the trace text (user override path — keep it as the
  highest-priority override).
- Rect Y-axis labels are drawn in `Renderers/AxesRenderer.cs` from the trace + a `showFilePrefix` flag — it must
  consume the same computed names (see step 4).

## Design — a per-plot label computation
Add a small pure helper that, given the traces in a plot, returns each trace's minimal label. Put it where both
the label strips and the renderer can call it (a static method on `Trace` or a new
`Models/TraceLabeler.cs` — your call; headless, no Avalonia).

```csharp
// One label per trace, minimal within this set.
public static IReadOnlyList<string> ComputeMinimalLabels(IReadOnlyList<Trace> traces);
```

**Rules (§2.7):**
1. Build each trace's identity components as discrete tokens (do **not** start from the joined `Description`):
   - **source** = `Path.GetFileNameWithoutExtension(SourcePath)` (null → none)
   - **analysis** = for a results `.npy` (`results/<schematicKey>/<analysisName>.npy`) the file stem already *is*
     the analysis; for 7.2c-b treat **source == analysis** (the file stem). Keep them as separate slots in the
     model so 7.2c+ can split them, but it's fine if they coincide now — just don't emit the same token twice.
   - **quantity** = cube-bound: `CubeName` + the pinned-axis selector(s) that name *which* signal (e.g.
     `V(node=drain)`); network: `dB(S(2,1))`-style from `MatrixType`/`Row`/`Col`/`Derived`/`YAxis` (reuse the
     existing `DescriptionFor` formatting for this token only).
   - **transform** = cube-bound `Transform` when not `None` and not already implied by the quantity token
     (e.g. `dB20` → `dB`); network traces fold the transform into the quantity token as today (`dB(...)`).
2. A component **constant across every trace in the set is dropped.** One source in the plot → no source prefix;
   add a second source → the source token reappears **on all** traces. Same for analysis and any other slot.
3. The label is the join (with `·` between source/analysis and the quantity, matching the plan's examples:
   `S21 (dB)` with one analysis; `SP1·S21` once a second analysis appears) of the **non-dropped** tokens. Keep
   it readable; reuse the existing `..`/`·` separators already in `DescriptionFor` where practical.
4. **User override wins:** if `LabelStripViewModel.CustomLabel`/the plot custom Y label is set, use it verbatim
   (unchanged behavior).
5. **Recompute on add/remove** (and on source add/remove): `UpdateLabelStrips` already runs on
   `PlotStructureChanged` and `OnLibraryEntryCountChanged` — call `ComputeMinimalLabels` there and pass each
   trace's computed label into its `LabelStripViewModel` (new field, e.g. `AutoLabel`), used when `CustomLabel`
   is null.

**Family legend (§2.7) is out of scope** — families are 7.3. Note it and move on.

## Wiring
- `LabelStripViewModel`: add `[ObservableProperty] string? _autoLabel;`. `AxisLabelControl` renders
  `CustomLabel ?? AutoLabel ?? Trace.Description` (keep the existing trace-colour vs theme-colour rule for
  custom). Bump `AppearanceRevision` as today so it re-renders live.
- `UpdateLabelStrips`: compute labels once for `plot.Traces` (or per-axis sets — compute over the **whole plot**
  so dropping is consistent across left+right axes), then assign `AutoLabel` per strip. Drop the ad-hoc
  `ShowFilePrefix` plumbing **or** keep it as the source-token input to the new policy — your choice, but the
  prefix decision must now come from "is the source constant across the plot," not the global setting alone.
  (Honor `AppSettingsViewModel.AlwaysDisplayDataSourcePrefix` as a force-on override.)
- `AxesRenderer` Rect Y-label path: feed it the same computed label (pass the label string in, or have it call
  `ComputeMinimalLabels` for the plot's traces). Marker readout (`GetMarkerValString`) should use the computed
  label too where it currently uses `Description`/`ShortDescription` — minimally, route the same label through.

## Tests (`tests/Ui.Tests`, headless — call `ComputeMinimalLabels` directly)
1. **OneSource_DropsPrefix:** two traces from the same source, different quantity → labels have no source token,
   differ only by quantity.
2. **TwoSources_PrefixReturns:** add a trace from a second source → source token reappears on all.
3. **ConstantQuantityKept:** if all traces share the quantity but differ by source, quantity may be shared but
   each label still disambiguates by source.
4. **CustomOverrideWins:** a strip with `CustomLabel` set is unchanged.
5. **CubeAndNetworkMix:** a cube-bound `V(node=drain) dB` and a network `dB(S(2,1))` in one plot produce distinct
   readable labels.

## Gate
Build 0W/0E; tests green. Manual: one-source plot shows bare quantity labels (`S21 (dB)`); add a trace from a
second `.npy` → both labels gain the source prefix; removing it drops the prefix again; a user-typed custom Y
label still overrides.

## On completion
Note in `src/Ui/CLAUDE.md`: trace display names are computed at the plot level (`ComputeMinimalLabels`) from the
separate identity components — any component constant across the plot's traces is dropped, recomputed on
add/remove; custom labels override; families deferred to 7.3. Next: 7.2c-c (`Snp*→DataSource*` rename).
