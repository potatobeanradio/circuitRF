# Brief — harmonicaRF Round 6D: the power-sweep and loadline panels, and a time-domain view

**Read first:** `src/Ui/Harmonica/Renderers/HarmonicaPanelRenderer.cs` — `DrawPowerSweepPanel`,
`DrawEfficiencyAxisOverlay` (and its long doc comment, which records two previous attempts at the bug
in §1), `DrawEfficiencyAxisLabel`, `BuildPowerSweepPlot`, `PinAxisPin`, `BuildLoadlinePlot`,
`DrawLoadlinePanel`. Then `src/Ui/DataDisplay/Renderers/AxesRenderer.cs` (`DrawBorder`, the
`ShowSecondary` branches, `ComputeLabelHitRects`), `src/Ui/DataDisplay/Models/Axes.cs` and
`Plot.cs:383-395`, `src/Ui/Harmonica/HarmonicaFrame.cs` (`PowerSweepPanelData`, `LoadlinePanelData`),
`src/Harmonica/HarmonicaDataSet.cs` (the `Vds_intr_t` / `Ids_intr_t` cubes).

**Depends on R6B §4** — the panel/title fly-menu dispatch in `HarmonicaView.axaml.cs`
(`OnCanvasContextMenuOpening`). Land that first; this brief adds branches to it.

**Do NOT update any `CLAUDE.md`.** `src/Ui/RESOLVED.md` only if warranted — §1 probably warrants it,
since it is the third attempt at the same symptom.

---

## 1. Bug — the right (efficiency) y-axis is rendered twice, in two colours

### 1.1 What is actually happening

`DrawPowerSweepPanel` calls `PlotRenderer.Draw(...)` — which draws the secondary axis line, ticks, tick
numbers and the "Efficiency (%)" label in the shared theme's ordinary axis colour — and then calls
`DrawEfficiencyAxisOverlay`, which **redraws all four on top** in `Harmonica.EfficiencyTrace`.

That is a cover-up by construction, and it has now been "fixed" twice without removing the thing being
covered:

- **R-h9b-9** added the overlay (line, ticks, numbers).
- **R3C §5** found the residual green fringe was `AxesRenderer.DrawBorder`'s antialiased,
  `Square`-capped stroke showing along the edges of a hard-edged `Butt`-capped overlay stroke, and
  matched the paints. Its own note records that route (a) — *not drawing the underlying one* — was
  rejected "because it stays entirely inside this file".
- **R-h9r2-23** then added `DrawEfficiencyAxisLabel` for the same reason on the label.

The owner is reporting it again. **A cover that must match the covered exactly is the wrong design;
stop drawing the underlying one.**

### 1.2 The fix

Draw the shared plot **with the secondary axis chrome suppressed**, then draw the whole secondary axis
once, in `Harmonica.EfficiencyTrace`, from the real plot.

`Axes.ShowSecondary` is the switch: `AxesRenderer` consults it at every place that paints secondary
chrome (lines 105, 146, 152, 620, 626, 780, 802). Two facts make suppressing it safe here, and **you
must verify both by reading, not by assuming**:

- **The efficiency TRACE does not depend on it.** Trace drawing branches on `trace.UseSecondaryAxis`
  and the secondary transform, never on `ShowSecondary` — so the curve still draws.
- **The viewport does not move.** `Plot.SetAxesViewport` reserves a different right margin when
  `ShowSecondary` is false (`Plot.cs:388`) — but `BuildPowerSweepPlot` **pins the viewport explicitly**
  (`plot.Axes.Viewport = PowerSweepShapedViewport()`, R-h9b-11) precisely so that formula cannot move
  it. Confirm nothing else re-derives it.

Shape: build the plot once; take a shallow copy of its `Axes` with `ShowSecondary = false` for the
`PlotRenderer.Draw` call (`Axes` already has a copy path — see `Axes.cs:178`); draw the overlay from
the **original** plot, so `axes.Ticks()`, `WindowSecondary` and `ComputeLabelHitRects` all still see the
real secondary axis. The overlay keeps drawing line, ticks, numbers and label exactly as it does today
— it is now drawing them for the first time rather than over something.

If reading the code shows this cannot be done without changing `AxesRenderer`/`Plot`, then — and only
then — adding an **optional per-axis colour** to `Axes` is permitted as the fallback, with the shared
renderer's default behaviour bit-for-bit unchanged when it is unset. Say which route you took and why.
Do not ship a third generation of "match the cover to the covered".

### 1.3 Gate

A headless render test in `tests/Ui.Tests/Harmonica/HarmonicaPanelTests.cs`: render the power-sweep
panel to an `SKBitmap` and assert that along the right axis line **no pixel carries the ordinary axis
colour** — the exact assertion the previous two fixes could not make, because the covered stroke was
still there underneath. Sample the tick-number band and the rotated label rect too.

---

## 2. Power-sweep X axis needs headroom past the sweep stop

The curve currently ends exactly on the right border, so the compression point sits under the axis line
and cannot be read. `BuildPowerSweepPlot` calls `AutoScale(plot)` (data fit) and then `PinAxisPin`
(which, in a Pin-domain X unit, replaces the X extent with the sweep's *configured* range).

Add right-side headroom after both: extend `plot.Axes.Window`'s right edge by a fraction of the span —
**5 % is the starting value; tune it until the compression cursor is comfortably clear of the border,
and name the constant** (`XHeadroomFraction`, alongside the file's other named fractions).

**Apply the identical extension to `plot.Axes.WindowSecondary`.** `PinAxisPin` already does this and
says why: the two windows must keep the same X mapping or the gain and efficiency curves separate
horizontally. This is the one way to get this section wrong.

Left edge unchanged. Y unchanged.

**Gate:** a test asserting `Window.Right > max(x)` by the expected fraction, and that
`Window`/`WindowSecondary` have identical X extents — for all four `PowerSweepXUnit` values.

---

## 3. Panel titles

- **"Loadline"** above the loadline/DCIV plot.
- **"Power Sweep"** above the power-sweep plot.

`BuildLoadlinePlot` already sets `CustomTitleOn = true, CustomTitle = ""` and `BuildPowerSweepPlot`
does the same — so the title path is wired and set to empty. Setting the strings is most of the work;
`PlotRenderer.ComputeViewport` reserves extra top margin for a non-empty title, which is what makes
room for it. Check that the reserved band does not squeeze the plots at the shipped panel sizes, and
that the pinned `PowerSweepShapedViewport` still lines the two panels up (they deliberately share a
viewport shape — see `PinAxisPin`'s neighbouring comment).

The title is also the **fly-menu hit target** for §4 and for R6E, so keep its rect obtainable from
`AxesRenderer.ComputeLabelHitRects` (a `Title` rect) rather than hand-derived.

---

## 4. Power-sweep title fly menu — `Power Sweep` | `Time Domain`

Right-click the **"Power Sweep" title** → a fly menu with two mutually exclusive, checkable items:
**Power Sweep** (today's plot, the default) and **Time Domain** (§5).

- **Persisted in the `.charm`**, in `CharmAppearance`'s display-toggle block, same nullable-defaulted
  shape as `ShowGridPoints` / `ShowIsoLineLabels` — absent means the built-in default, which is
  Power Sweep.
- The **panel title changes with the selection** (`Power Sweep` ⇄ `Time Domain`), so the title always
  names what is drawn.
- R6E adds `Autoscale` and `Copy` to this same menu — build the menu so items can be appended without
  restructuring.

---

## 5. The Time Domain view

When selected, the power-sweep panel is repurposed. Owner's specification, exactly:

| element | source | axis | colour |
|---|---|---|---|
| Vds(t) | the SAME time-domain Vds the loadline uses | left y | `Harmonica.GainTrace` |
| Ids(t) | the SAME time-domain Ids the loadline uses | right y (secondary) | `Harmonica.Loadline` |

- Left y label: **`Vds (V)`**. Right y label: **`Ids (A)`**.
- X axis: time over one RF cycle. `LoadlinePanelData.LoadlineVds` / `LoadlineIds`
  (`src/Ui/Harmonica/HarmonicaFrame.cs:234`) are exactly the arrays the loadline plots against each
  other, published from `Vds_intr_t` / `Ids_intr_t`, closed over one cycle at
  `Settings.LoadlineSamples` points. Build the time axis as `i / N × 1/f₀` — label it `Time (ns)` (or
  ps, whichever keeps the numbers readable at the shipped f₀ = 2 GHz) and say which you chose. **Do
  not** invent a second evaluation of the loadline: read the same panel data, so the two panels can
  never disagree.
- The **secondary-axis colour work from §1 applies here too** — the right axis must be drawn once, in
  `Harmonica.Loadline` in this mode rather than `EfficiencyTrace`. Make the overlay's colour a
  parameter rather than forking the function.
- The operating-point cursor, the "did not reach compression" note and the X-unit click-to-cycle menu
  belong to the power-sweep mode only. In time-domain mode the X-label context menu must not offer
  Pout/Pin units — check `OnCanvasContextMenuOpening`'s `PowerSweepXUnit` branch and gate it on the
  mode.
- **When the intrinsic plane is not located**, `LoadlineVds`/`LoadlineIds` are published empty (the
  same refusal the glyphs make). Draw the empty panel with a stated note, never zeros.

`PowerSweepValidation.cs` and `HarmonicaPowerSweepAndDcivTests.cs` are where the existing panel's
invariants live — add the time-domain ones beside them: sample count matches `LoadlineSamples`, the
two curves come from the same arrays the loadline panel draws (assert identity, not similarity), and
the axis labels/colours are as specified.

---

## 6. Every plot gets a right-click `Copy`

The owner: *"Make sure each plot has a way for user to right-click and select Copy to copy the plot
rendering to the clipboard. (Do not reinvent the Copy renderer, we've already built that; we just need
the Copy menu wired into the fly menu.)"*

`HarmonicaClipboard.CopyAsync(anchor, vm, panelId)` already renders one panel and hands the bytes to
`PlotExporter.SetClipboardDataAsync` (PDF + SVG + raster + text, transparent background, the Windows
bypass). **Wire the menu; write no rendering code.**

Panels that must offer it, via the R6B §4 dispatch:

- both Smith charts (R6B §4.2 — already specified there),
- the loadline/DCIV panel (which today offers only `DCIV Sweeps…` on right-click — add `Copy` above it),
- the power-sweep panel (in both modes),
- any picked-trace panel that is on screen (§7.7's own panels), if reachable — say so if not.

One helper that takes a resolved `panelId` and builds the `Copy` `MenuItem`, used by every branch. A
failure must land on `HarmonicaViewModel.SolveError` through `RunHook`, like every other menu hook
(R-h9a-13).

---

## 7. Gates

1. `dotnet test tests/Ui.Tests --no-build`, `dotnet test tests/Harmonica.Tests --no-build`,
   `tests/Firewall.Tests` green.
2. §1's pixel test is the one that matters — it is the assertion two previous rounds could not make.
3. Drag cost must not regress. `HarmonicaPowerSweepDragCostTests` and `HarmonicaDragCostTests` exist;
   run them and report. §1 removes a draw rather than adding one, so the expected direction is
   *cheaper*.
4. Owner check: the right axis is one colour; the compression point is clearly inside the plot area;
   both plots are titled; the title menu switches to a time-domain view with the right colours and
   labels and that choice survives save/reopen; right-click ▸ Copy works on every panel.
