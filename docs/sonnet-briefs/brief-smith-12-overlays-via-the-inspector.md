# Brief 12 — overlays move to the Plot Properties inspector

**Series:** [Smith Chart](brief-smith-0-overview.md) · **Tag:** `R-smith12-n` · **Phase:** post-ship
**Area:** `src/Ui/Smith/`, `src/Ui/DataDisplay/`, `src/Render/DataDisplay/`, `src/Design/Smith/`
**Depends on:** 8 (which it replaces), 11 · **Blocks:** nothing
**Design note:** [`smith-chart.md`](../design/smith-chart.md) §5.7, §7 — **both of which this brief rewrites**

---

## 0. What this brief delivers

**The Overlays panel is deleted.** Reference data goes onto this chart the way it goes onto a Smith chart
in a Data Display: pick a source, open **Plot Properties…**, press **Add**, and edit the trace card.

**Owner instruction, 2026-09-19:** remove the Overlays panel; overlay S-parameter data sources are added
through the Plot Properties inspector, **the same way they are added to a Smith chart on a Data Display.**

That last clause is the whole specification and it is worth reading as a constraint rather than as a
comparison: **nothing in this brief invents an overlay UI.** The trace card already picks a matrix element,
a virtual Z or Y, a derived mode, a colour, a line style, a Z₀ and its override. Brief 8 built a second,
smaller version of that in a side panel — seven properties where the card has thirty — and the panel is
what goes.

**It is not a UI move.** Brief 5 set `Plot.IsFixedReadout` on this chart with a reason that is still true:

> *every trace here is rebuilt from the design on each edit, so a trace added in the inspector would be
> gone by the next keystroke and one removed would be back.*

Allowing the Add button is therefore the smallest part of the work. The rest is making a trace the user
authored **survive the rebuild** — which is the markers' problem (`HarvestMarkers`), solved again for
traces, and it forces a `.csmith` format change.

---

## 1. `R-smith12-1` — the flag has to be split, because it means two things

`Plot.IsFixedReadout` gates three behaviours through `PlotInspectorViewModel` and `TraceRowViewModel`:

| gated | wanted here |
|---|---|
| `CanEditTraceSet` — the **Add** button and the per-card trash | **yes, on** |
| `CanChangePlotType` — the plot-type picker | **no. It is a Smith chart and stays one** |
| the card's own remove button | **yes, on** — it is the same idea as the trash |

So `IsFixedReadout` keeps its meaning for the plot TYPE, and a second flag — `Plot.AllowUserTraces`,
default false — opens the trace set. `CanEditTraceSet` becomes `!IsFixedReadout || AllowUserTraces`;
`CanChangePlotType` is untouched. railRF's `ImpedancePlot` and the Match Designer's two response plots set
only `IsFixedReadout` and must be unaffected — **assert that**, because they are the two places where a
user-added trace would be silently discarded on the next solve.

---

## 2. `R-smith12-2` — Add Trace must not clone a trajectory

`PlotInspectorViewModel.AddTrace()` opens with:

```csharp
if (_plot.Traces.Count > 0)
{
    var src = _plot.Traces.Last();
    trace = new Trace(src, incrementColorBy: 1, includeMarkers: false);
```

On a Data Display that is right — the commonest Add is "another one like the last". **On this chart the
last trace is always one of the tool's own**: a cube trace whose `CubeName` and `Expression` are an element
name (`"L2"`, `"band"`, `"Zgen"`) and whose points were pushed in by `SetGamma`. Cloning it produces a
trace bound to a cube that exists nowhere, drawing a frozen copy of a curve that will not track the design.
It looks like a trace. It is not one.

**A plot with `AllowUserTraces` seeds from the library**, i.e. takes the `_library.SelectedEntry` branch
that already exists below, and falls back to the clone only when the plot has a user trace to clone. The
tool's own traces are recognisable by `Trace.ExcludeFromAxisLabels`, which brief round three set on
everything `SmithPlotBuilder.CubeTrace` produces and left clear on overlays — **use that, do not add a
second marker of the same fact.**

---

## 3. `R-smith12-3` — the window needs a data source to add

`WorkspaceViewModel.WireSmithChartSources` already gives this document's `DataSourceLibraryViewModel` the
three providers a Data Display gets (`ResultsRootProvider`, `KnownTouchstoneProvider`,
`KnownLoadpullProvider`) and the same `IPlotDataSources` seam. What it has never had is **the toolbar
combo**, and `AddTrace` seeds from `SelectedEntry` — so without one the Add button adds nothing and says
nothing about why.

Add the Data Display's own combo, bound exactly as `DataDisplayView.axaml` binds it
(`AvailableDataSources` / `SelectedDataSourceItem`, tooltip on `SelectedDataSourceAbs`). Put it **in the
chart pane's top strip beside the Q button**, not in the generator column: it belongs to the chart.

**A scratch `.csmith` with no workspace open has no providers**, and that has to keep working — it is what
makes this a real document rather than a workspace feature. Wire `AddSourceFileRequested` to the same
picker `OverlayFileChooser` used, so a file can be loaded with nothing else open. Keep the relative-path
convention: **a reference, never a copy** (brief 8 `R-smith8-2` — an overlay is reference material, and
this is the opposite choice from the generator's `.s1p` import, deliberately).

---

## 4. `R-smith12-4` — a user trace has to survive the rebuild, and that is a format change

`SmithPlotBuilder.Fill` clears `plot.Traces` and refills from the design on **every** committed edit. A
trace the user added in the inspector is gone by the next keystroke unless the document is told about it.
This is `HarvestMarkers`' contract for traces, and it is the reason brief 5 closed the trace set in the
first place.

### 4a. What has to be stored

`SmithOverlayRef` — source, quantity, derived, renormalize, visible, autoscale, `#rrggbb`, dashed —
**cannot hold what a trace card authors.** The card also owns line width and type, marker glyph, size and
colour, the Z₀ complex value and its override, matrix format, precision, column widths, cube name and
slice, an expression, a *plot-versus* X spec, and the trace's own markers. Storing seven of thirty and
silently dropping the rest is worse than the panel was.

**Store the Data Display's own `TraceConfig`**, which is exactly the thirty. This is `SmithMarker`'s
precedent (§7 of the note): a `.csmith` already writes markers in `MarkerConfig`'s shape, field for field,
so *"the bytes a `.csmith` puts on disk for a marker are the bytes a `.cdd` puts on disk for the same
marker."* Overlays join them.

### 4b. The firewall makes the obvious version wrong

`SmithMarker` is a hand-written mirror of `MarkerConfig` in `src/Design`, because `MarkerConfig` lives in
`src/Render` and `src/Design` is below it. **Do not mirror `TraceConfig` the same way.** It pulls in
`TracePropertiesConfig`, `MarkerConfig`, `AxisSliceConfig`, `WspTraceConfig`, `ContourTraceConfig` and
`SummaryColumnConfig`, and it is a live type that grows whenever the Data Display gains a per-trace
setting — WSProbe, pattern mirroring, the dBm reference override and *plot versus* are all recent
additions to it. A mirror would fall out of step silently, and the symptom would be a setting that
survives in a `.cdd` and vanishes from a `.csmith`.

**`SmithDesign.Overlays` becomes `List<JsonElement>` — the trace configs, opaque to `src/Design`.** The
`.csmith` still contains ordinary readable JSON; what it no longer does is give that JSON a type in a
project that must not know about traces. `src/Ui` serializes and deserializes it with the **same**
`JsonSerializerOptions` the `.cdd` writer uses, so one spelling.

**Check it under `SmithDesignIo.SerializeUnvalidated` specifically**, not only under `Serialize`: that is
called on every committed edit to build the undo snapshot, and a round trip that loses a field there loses
it on the next undo rather than on the next save. `SmithMarker`'s own header records the NaN that took the
document down by exactly that route.

### 4c. Migration

Existing `SmithOverlayRef` rows **migrate on read** and the old block is dropped on write. Every field maps
onto a `TraceConfig` (`Source` → `SourcePath`, `Quantity` → `MatrixType`+`Row`+`Col`, `Derived` →
`Derived`, `Renormalize` → `Z0Override`, `ColorHex`/`Dashed` → `Properties`). Nothing shipped carries an
overlay, so this is cheap insurance rather than a feature — and the alternative is a user's own `.csmith`
quietly losing its reference data.

---

## 5. `R-smith12-5` — restoring one trace, without a second trace loader

`PlotConfigLoader.LoadPlot` is the only code that turns a `TraceConfig` into a `Trace`, and it is welded to
a `PlotContainerConfig` and to building a whole new `Plot`. **Extract the per-trace body** into

```csharp
public static Trace? LoadTrace(TraceConfig cfg, PlotType plotType, FreqUnit freqUnit,
                               IPlotDataSources sources)
```

and leave `LoadPlot` as the loop that calls it. **Pure extraction — no behaviour change.** The existing
`.cdd` suite is the gate; if any of it moves, the extraction is wrong.

The writer already exists and is already in `src/Ui`: `DataDisplayViewModel.BuildTraceConfig(trace,
configDir, library)`. **Do not write a second one.**

### 5a. Reuse the trace INSTANCE across a rebuild

The obvious implementation re-resolves every overlay from its config on each `Fill`. Do not: the inspector's
trace cards, its selection and the trace's markers all hold the `Trace` **object**, and replacing it leaves
every one of them pointing at a discarded copy. (This is not hypothetical — round three's own marker test
held a stale `Trace` across one rebuild and removed a marker from nothing.)

So the view model **caches the resolved `Trace` per overlay and re-adds the same instance**, re-resolving
only when that overlay's config or the chart's Z₀ changed. `Fill` keeps its `overlays` parameter and its
signature; what changes is where the list comes from.

### 5b. Harvest

`PlotInspectorViewModel.PlotStructureChanged` fires on add, remove and reorder. Subscribe, and write back:
every trace on the plot **without** `ExcludeFromAxisLabels` is a user trace, and its `BuildTraceConfig` is
the document's overlay list. An add or a remove is **one undo entry**; anything else rides along on the
next save — `HarvestMarkers`' split, for `HarvestMarkers`' reason (`R-smith4-3`: an entry per pointer move
is the Match Designer's "eight edits took fourteen undos" by a slower route).

### 5c. The marker key must stay stable

Markers are stored against a trace's **label** (`SmithTraceKey`), because an index moves when an element is
deleted. `SmithOverlayResolver.Label` produced that label today. Whatever replaces it must produce the same
string for the same overlay across a rebuild, **and across a reorder** — otherwise a marker taken on an
overlay lands on the first curve instead, which brief 8 chose deliberately as the visible failure and is
not what anyone wants here.

---

## 6. `R-smith12-6` — the two properties the card does not have

Brief 8 gave an overlay row two toggles that no trace card has. Both must land somewhere or they are lost.

- **Autoscale.** `Trace.ExcludeFromAutoscale` exists, is set only in code, is absent from `TraceConfig`,
  and has no card UI. §5.7's reason for it is specific: *a stability circle can be enormous, and one
  unlucky overlay should not reframe the work.* **Add it to `TraceConfig`** (absent = false = every `.cdd`
  written to date, unchanged) **and give the trace card the checkbox.** A new overlay on this chart seeds
  with it **on**; the user can turn it off. This is the one place the brief deliberately touches the Data
  Display's own card, and the alternative is losing either the protection or the control.
- **Visible.** It goes, with no replacement. `SmithPlotBuilder`'s own note says `TraceProperties.Enabled`
  is read by nothing and a hidden trace would still sit in the trace list, the legend and the Add Marker
  menu. On a Data Display you delete the trace. Here too — the card's trash is the affordance.

---

## 7. `R-smith12-7` — three things that must not regress

- **Renormalization to Z₀_chart is a requirement, not an option** (`R-smith8-3`). `Fill` already forces
  `t.Z0 = chartZ0` on every trace that has not set `Z0OverrideEnabled`, so it survives by construction —
  but a trace the card creates must seed with the chart's Z₀ and the override ON. *A 75 Ω part drawn on a
  50 Ω chart without it is a curve in the wrong place that looks entirely plausible.*
- **`ExcludeFromAxisLabels` stays clear on an overlay.** Round three set it on everything the tool derives
  so that only the user's own data names an axis. A restored overlay that inherits it loses the very label
  the user added it to read.
- **The CLI draws the same picture.** `src/Cli/Smith.cs` resolves `design.Overlays` through
  `SmithOverlayResolver` and hands the result to `Fill`; that path changes with the format and must go
  through `PlotConfigLoader.LoadTrace` like the window's. `SmithCliVerbTests` compares the verb's SVG
  against the in-process render **byte for byte**, so a divergence fails there rather than looking fine.

---

## 8. The gate

Extend `tests/Ui.Tests/Smith/SmithOverlayTests.cs` — **one test per claim**, and only the claims whose
failure would be silent.

1. **A trace added to the chart survives a component edit** (`R-smith12-4`). Add, harvest, change an
   element value, rebuild; the trace is still on the plot with its colour and its markers. *This is the
   whole feature; on the old code it fails on the second line.*
2. **…and it is the SAME `Trace` instance** (`R-smith12-5a`), because the inspector's cards hold it.
3. **A `.csmith` round-trips a trace card's full state** — including at least one field `SmithOverlayRef`
   could not hold (line width, marker glyph, a cube slice) — **through `SerializeUnvalidated`**, which is
   the undo path.
4. **A `SmithOverlayRef`-era `.csmith` opens and draws the same curve** (`R-smith12-4c`), asserted against
   a Γ the old resolver produced.
5. **A 75 Ω overlay lands where 50 Ω says it should** — brief 8's renormalization test, re-pointed at the
   new path. *The one that catches a plausible-looking wrong curve.*
6. **`Add` seeds from the library, not from the last trace** (`R-smith12-2`): on a chart whose last trace
   is `"band"`, the added trace is bound to the selected source.
7. **A plot with `IsFixedReadout` and no `AllowUserTraces` still refuses Add** (`R-smith12-1`) — railRF's
   impedance plot, named.
8. **`PlotConfigLoader.LoadPlot` is unchanged in behaviour** — the existing `.cdd` suite, run as-is.
9. **The overlay's axis label is still drawn and the tool's traces' are not** (`R-smith12-7`).
10. **`SmithCliVerbTests`' byte-identity gate still passes** on a document carrying an overlay.

A **source scan** proving the panel is gone: no `SmithOverlayRowViewModel`, no `AddOverlayCommand`, no
`OverlayFileChooser`, no overlay `ListBox` in `SmithChartView.axaml`. A panel left in place beside the new
path is two authors of one list.

---

## 9. What this brief must NOT do

- **No second trace resolver, no second trace writer, no second overlay UI.** `LoadTrace` and
  `BuildTraceConfig` are the two, and both already exist or are an extraction of something that does.
- **No mirror of `TraceConfig` in `src/Design`.** §4b — it is a growing type and the mirror would fail
  silently.
- **No plot-type change on this chart.** `IsFixedReadout` keeps that job; `AllowUserTraces` is only the
  trace set.
- **No overlay in the network strip, and none in the cascade.** Unchanged from brief 8 `R-smith8-4`.
- **No embedding of Touchstone content in the `.csmith`.** A reference is still the decision.
- **No change to how markers are stored**, beyond keeping their trace key stable (§5c).

---

## 10. On completion

Findings to `src/Ui/RESOLVED.md` and `src/Render/RESOLVED.md`. **Never a `CLAUDE.md`.**

Rewrite **§5.7** of `docs/design/smith-chart.md` — it currently describes a panel with rows — and **§7**'s
`Overlays` block, and add the round to **§9.3**. `docs/user/src/reference/smith-chart.md` describes adding
an overlay and must describe the inspector instead; the chapter's figures need a DocGen run, which is
already owed from round three.
