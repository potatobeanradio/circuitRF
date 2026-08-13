# Brief — harmonicaRF Round 2A: the drag loop, the contours, and the markers

**Read first:** `docs/design/harmonicarf.md` (**§4.2**, **§6.4–6.8**, **§7.2**), then `src/Harmonica/CLAUDE.md`
and `src/Ui/CLAUDE.md`'s **H6**, **H7**, **R1B** and **R1C** entries.

**Round 2 is two briefs and they are independent.** **2A** (this one) is the frame loop: what a drag is
allowed to re-solve, what must stay on screen while it does, the default marker set, marker z-order, and
the per-marker context menu. **2B** is the chrome: the macOS menu bar (docked, and the dark-mode crash),
the Smith titles, the DCIV defaults, the inline editor, the power-sweep range, Set Z0, and the readout
strip.

**§1 is the whole point of this brief and the other three sections are small.** Do §1 first, and read
§1.1 before writing any code — the diagnosis is already done and one of the two findings changes what
"fix it" means.

---

## 0. What already exists

| you need | it is here |
|---|---|
| the drag gesture | `HarmonicaGesture` (`src/Ui/Harmonica/HarmonicaPointer.cs`) — `Apply` is where a marker drag becomes a frame request |
| the frame ladder | `FrameScheduler` (`src/Harmonica/FrameScheduler.cs`) — `NextPlan(dragging)` → `FramePlan { Quality, Rings, Spokes, RasterResolution, SkipContours }` |
| the frame builder | `HarmonicaSolver.Solve` (`src/Ui/Harmonica/HarmonicaSolver.cs`) — the ONE place a `HarmonicaFrame` is built |
| what a frame carries | `HarmonicaFrame` / `SmithPanelData` (`src/Ui/Harmonica/HarmonicaFrame.cs`) — `Contours`, `GridPoints`, `Optimum`, `Markers` |
| the frame request | `HarmonicaViewModel.RequestScheduledFrame(dragging)` / `RequestFrame(options)` / `PublishFrame` |
| the Γ grid | `ContourGrid` (`src/Harmonica/ContourGrid.cs`) — **`Build(..., reuseUnchanged:)` and its `_reusableAgainst` key are load-bearing here; see §1.1** |
| the markers | `HarmonicaMarker` (`HarmonicaFrame.cs`) — `Side`, `Band`, `Name`, `Gamma`, `GammaIntrinsic` |
| marker create/remove | `HarmonicaViewModel.AddMarkerBand` / `RemoveMarkerBand` — R-h7-2: the ONLY way to create one |
| marker draw | `HarmonicaPanelRenderer.DrawMarkers` (`src/Ui/Harmonica/Renderers/`) |
| the hit test | `HarmonicaHitTest.Resolve` — three z-ordered passes (markers, glyphs, grid points) |
| the canvas right-click | `HarmonicaCanvas.ContextMenuTarget` + `HarmonicaView.OnCanvasContextMenuOpening` — ONE `ContextMenu`, rebuilt on `Opening` |
| the Data Display's own marker menu | `MarkerInfoBoxView.PopulateMarkerMenu` (`src/Ui/Views/DataDisplay/`) — VSWR toggle, Snap to Point, Remove |
| VSWR rendering | `MarkerRenderer.DrawVswrLocus` + `PlotRenderer.VswrAvailableFor` (`src/Ui/DataDisplay/Renderers/`) |
| the set-a-termination dialog | `HarmonicaSetTerminationDialog` (`src/Ui/Views/Dialogs/`) — already commits through `SetMarkerGamma`/`SetMarkerImpedance` |

---

## 1. The drag must stop sweeping the grid

Three owner reports, one mechanism.

> **owner (1):** *"Dragging the L1 marker on a Smith Chart set to the Fundamental Load Plane forces a full
> loadpull simulation (all grid points are run). This is unnecessary and wasteful — the contours do not
> need to change in this case. Only the loadline and power sweep plots need to update live. This same
> logic should be used for an L2 drag move when the Smith Charts are 2f0 load plane. The rule is: don't
> run loadpull at all when the user drags a marker termination on a Smith Chart whose plane matches the
> termination."*

> **owner (2):** *"Live contour generation is too computationally intensive to get a high frame rate.
> Let's deactivate it and remove all messages that say 'Contours frozen while dragging' or similar."*

> **owner (3):** *"The isolines and grid glyphs disappear when the user starts dragging a marker
> termination. We don't want renderings disappearing or flashing. Keep the isoline rendering constant
> during a loadpull calculation, then update the isoline renderings (for all Smith Charts) at the same
> time. The grid glyph should move live while the user drags it."*

### 1.1 — two diagnoses, both already done, and the first one is a theorem

**Finding A — report (3) is a real defect and its cause is one line.** `HarmonicaSolver.Solve` builds
`SmithPanelData smithP = new() { Title = …, Subtitle = …, Markers = markers }` and only fills `Contours`,
`GridPoints` and `Optimum` **inside** `if (!opt.SkipContours)`. So a frozen (or any grid-less) frame
publishes a panel with **empty** contour and grid-point lists, and the renderer faithfully draws nothing.
`FrameScheduler`'s own doc comment says a `FrozenContours` frame means *"the previous frame's are
ghosted"* — **nothing ghosts them.** They vanish. That is exactly the reported disappear-on-drag.

**Finding B — report (1) is not an optimisation, it is provably a no-op, and this codebase already
knows it.** `ContourGrid.Build`'s single-point reuse guard (`_reusableAgainst`, H7's R-h7-12) keys on the
structural key, the bias, the drive, the compression window, the side and band being swept, **and every
OTHER band's termination** — and its own note states why the swept band is excluded: *"it is overwritten
per point and says nothing about what a held point was solved at."* Moving the termination of the band
the grid sweeps therefore changes **no grid point's answer**. The contours, the grid samples, MXP/MXE's
positions, and (since `SolveAtOptimum` substitutes the optimum's Γ into that same swept band)
MXP/MXE's solved FOMs are all bit-identically unchanged.

**State this in the completion note**, because it is the difference between "we skip a solve to be fast"
and "we skip a solve because it cannot change the answer".

**One consequence worth stating up front:** §6.5's plane and harmonic selectors are DOCUMENT-wide (both
Smith charts sweep one grid, differing only in metric — `HarmonicaSolver.Options.GridSide`'s own doc
comment records why per-chart was rejected on cost). So "the chart whose plane matches" is not a
per-chart test: the test is `marker.Side == GridSide && marker.Band == GridHarmonic`, and it holds for
both charts at once. Do not add a per-chart plane to make the owner's wording literal.

### R-h9r2-1 — a frame carries its predecessor's contour layer forward

Give `HarmonicaSolver.Solve` the previous frame (or the previous two `SmithPanelData`s) and, when the
plan does not sweep a grid, **copy `Contours`, `Levels`, `GridPoints` and `Optimum` from it** instead of
leaving them empty. This one change fixes report (3) for every grid-less frame, whatever produced it —
the ladder's own `FrozenContours` rung, §1.2's always-frozen drag, and §1.3's skip-entirely case — rather
than three separate carry-forward paths.

Three things to get right:

- **The carried data must be tagged with what it was solved for.** Carrying a Load-plane contour set into
  a frame the user has since switched to the Source plane would draw a confident wrong picture. Carry
  only when the grid's own identity (`GridSide`, `GridHarmonic`, and whatever else `ContourGrid` keys on)
  matches; otherwise publish empty and let the next full frame fill it. **Say which fields you compared.**
- **Carry the whole layer or none of it.** Contours without their grid points (or an optimum whose
  surface is gone) is a half-updated picture, which is the thing report (3) is about.
- **`SmithPanelData` is a record.** The natural shape is `previous with { Title = …, Subtitle = …,
  Markers = markers }` — the titles and markers are LIVE on every frame (they describe settings and the
  drag, not the grid) and must not be carried stale.

### R-h9r2-2 — a drag never sweeps the grid, and the ladder stops talking about contours

Report (2), taken literally: while a marker, glyph or grid-point drag is in progress, the plan is always
`SkipContours`. Implement it where the plan is *chosen*, not by making the scheduler lie about its own
measurements — the cheapest correct place is `HarmonicaViewModel.OptionsFor(plan)`/`RequestScheduledFrame`
forcing `SkipContours = true` when `dragging` is true. Tier A (the Pin drive-up feeding the loadline and
power sweep) still runs on every frame — that is D4 and it does not change.

Then **delete the contour-quality status strings**: `FrameScheduler.StatusMessage`'s
`CoarseRaster` / `CoarseGrid` / `FrozenContours` cases (`"Contours at reduced resolution while
dragging…"`, `"Contours on the coarse grid while dragging…"`, `"Contours frozen while dragging; they
update on release."`) all go. **Do not delete the tier-A message** — `TierAHealthy`'s own "tier A alone
cannot hold the target" report is D4's honesty valve and is about a genuinely different thing.

**The ladder itself stays.** It still measures, still degrades, still recovers — it simply no longer has
a contour rung to announce, because contours are not computed during a drag at all. If that leaves a
rung with nothing to do, say so in the completion note rather than quietly deleting the enum member;
`FrameQuality` is read by `HarmonicaFrame.Quality`, by the message line's idle summary, and by
`HarmonicaSolver`'s own `Quality == FrameQuality.Full` gate on solving the optima.

### R-h9r2-3 — on RELEASE, skip the grid too when the dragged band is the swept band

This is report (1)'s own rule, and Finding B is why it is safe. In `HarmonicaGesture.Apply`'s
`ExtrinsicMarker` branch (and the equivalent release path), when
`marker.Side == vm.GridSide && marker.Band == vm.GridHarmonic`, request a **tier-A-only** frame even on
release. The published frame carries the previous contour layer forward (R-h9r2-1) and its loadline,
power sweep and readouts are fresh.

- **The intrinsic-glyph drag is the same case** — `DragIntrinsicGlyph` moves the same band's extrinsic
  termination through the inverse solve, so the same test applies to `Grab.Marker`.
- **A marker on any OTHER band still re-solves on release**, exactly as today. That is not the case
  Finding B covers: a held band's termination is part of `_reusableAgainst`, so moving it genuinely
  invalidates every grid point.
- **Pin this as a counter test, not a timing test.** `HarmonicaViewModel.LastSolveCount` and
  `HarmonicaSolver.LastGridPointsReused` already exist; a full-grid frame on this document is ~280 HB
  solves and a tier-A-only frame is ~10 (H4–H5's own measured figures). The gate is that dragging L1 with
  the grid on the 1f₀ Load plane and releasing costs the tier-A count, and that the published contour
  polylines are **reference-equal or value-equal** to the pre-drag ones.

### R-h9r2-4 — a grid-point drag moves its own glyph live and re-solves that point on release

Report (3)'s last clause. `DragGridPoint` already re-solves only the dragged sample
(`ReuseUnchangedGridPoints: true` — H7 measured 3 solves / 3.3 ms against a 272-solve full rebuild), but
under R-h9r2-2 a dragging frame now skips the grid entirely, which would freeze the dragged glyph too.

**The dragged glyph must still follow the cursor.** Two ways: publish the moved Γ into the carried
`GridPoints` list for display only, or let the grid-point drag be the one drag that still runs its own
single-point solve. **Pick one, say which, and say why** — the first is cheaper and matches "the grid
glyph should move live"; the second additionally keeps the dragged point's own contour contribution
honest, at 3 solves a frame. Either way the CONTOURS stay frozen until release.

---

## 2. The default terminations for S2, L2 and L3

> **owner:** *"The default S2, L2, L3 marker terminations for a new harmonicaRF document should be
> Z = 1e-6."*

R1B's `HarmonicaViewModel` constructor currently seeds S2 = 30 − j15 Ω, L2 = 15 + j20 Ω, L3 = 10 − j10 Ω
(R-h9b-14 chose "sensible starting impedances rather than the unmarked near-short"). The owner has
overruled that: a harmonic termination defaults to a **short**, which is the conventional starting point
for harmonic tuning and is what makes the fundamental behaviour readable before the user has tuned
anything.

- `new Complex(1e-6, 0)` for all three, not 0 — the Γ ⇄ Z conversion and the closure both have a term
  that a hard zero degenerates. Use the same non-zero epsilon everywhere rather than three spellings.
- **S1 and L1 keep their own defaults** (25 Ω and 80 + j10 Ω). The owner named S2/L2/L3 only.
- The marker set itself is unchanged: five markers, created through `AddMarkerBand` (R-h7-2), in the same
  order `RebuildMarkersFromTerminations` would produce.
- **A loaded `.charm` is unaffected** — R-h9b-14's own rule. Pin it again: loading a file whose bands
  carry other impedances must not be re-seeded.

---

## 3. Marker z-order, and the one you are touching comes to the top

> **owner:** *"The default rendering z-order is L1, L2, L3, S1, S2, where L1 is rendered highest. If the
> user clicks on the L2 marker, it moves to the top of the z-order rendering. This allows the user to see
> the termination they are interacting with."*

`DrawMarkers` currently iterates `d.Markers` in list order, which is `RebuildMarkersFromTerminations`'
order (source before load, ascending band) — the OPPOSITE of what the owner wants, since a later draw
paints on top.

### R-h9r2-5 — one comparer, two consumers, and the hit test must agree

- **The default order is a rank, not a list position.** L1 > L2 > L3 > S1 > S2 (highest first). Express it
  as one small comparer/rank function on `(Side, Band)` and use it in exactly one place per consumer.
- **The renderer draws LOWEST rank first** so the highest ends up on top.
- **`HarmonicaHitTest.Resolve`'s first pass must use the SAME order.** It currently takes the nearest
  marker within the grab radius, which for two overlapping markers is a coin flip that can disagree with
  what is drawn on top — grabbing the thing the user cannot see is the exact failure the z-ordered passes
  exist to prevent. Prefer the topmost marker among those within the radius; fall back to nearest only to
  break a tie at equal rank.
- **The "interacted with" promotion is SESSION state, not document state.** One nullable
  `HarmonicaMarker? TopmostMarker` on `HarmonicaViewModel`, set on a successful marker grab
  (`HarmonicaGesture.PointerDown`, when `Grab.Kind == ExtrinsicMarker`), consulted by both the renderer
  and the hit test as an override on the rank. **Never write it to `.charm`** — a rendering z-order the
  user nudged by clicking is not a property of the design, and persisting it would make two documents
  that describe the same circuit compare unequal.
- **A click that grabs an INTRINSIC glyph promotes its marker too.** The owner's rule is about "the
  termination they are interacting with", and a glyph drag is interacting with that band.

---

## 4. A context menu on every marker

> **owner:** *"Add a context menu to each harmonicaRF marker so the user can perform many different
> operations. List the termination for the marker in Gamma (Real/Imag) format, Gamma (mag/phase), and
> Z (real/Imag) format as a menu, each with their own sub-menu 'Set…' that gives a small dialog for the
> user to adjust the termination location using the format of its parent menu as the input units/method.
> Add a VSWR menu that enables a VSWR circle rendering on the Smith chart (and the VSWR interactions).
> We have already built this for loadpull markers in the Data Display so reuse this for harmonicaRF
> markers. Same with Snap to Grid, which is to snap the harmonicaRF marker onto Grid points. The last
> menu in the context menu is a separator followed below it by a 'Remove L<x>' menu that removes the
> marker termination rendering and sets the termination internally to Z = 1e-6. <x> is the termination
> number. Note that L1 can never be removed, so it is disabled for the L1 marker context menu."*

### R-h9r2-6 — the right-click resolves a marker through the SAME hit test a drag uses

`HarmonicaCanvas` already records `ContextMenuTarget` on right-click and
`HarmonicaView.OnCanvasContextMenuOpening` already builds a fresh item list per opening (R1B's own
L1-fix pattern — one `ContextMenu` instance, rebuilt on `Opening`; do not add a second `ContextMenu`).
Add a marker branch to that handler, resolved with `HarmonicaHitTest.Resolve` — the same call, the same
radius, the same z-order — so "the marker you can right-click" is always "the marker dragging would
grab". Resolve markers **before** the existing panel-scoped branches (power-sweep X label, DCIV), since a
marker sits inside a Smith panel and the panel-level items must not shadow it.

### R-h9r2-7 — three format rows, each with its own Set… — and the reuse is the DIALOG, not a new one

The three rows READ the current termination in that format and their `Set…` sub-item opens a small dialog
whose input is interpreted in that same format.

- `HarmonicaReadoutFormatting` already formats Γ (real/imag and mag/angle) and Z, and already parses them
  back (`TryParse`) — R1C built it for the readout strip. Use it. **Do not write a second formatter**, or
  the marker menu and the readout strip will disagree about how a number is spelled.
- `HarmonicaSetTerminationDialog` already exists, already takes a marker name + Γ + Z₀, and already
  commits through `SetMarkerGamma`/`SetMarkerImpedance`. **Reuse it**, widened with which format it should
  open in / accept, rather than adding three near-identical dialogs.
- **Every write goes through `SetMarkerGamma` or `SetMarkerImpedance`** — never a third path. That is
  R-h9c-8's own rule for the readout editor and it applies unchanged here.
- Whether the reading rows are themselves clickable (i.e. whether the row and its `Set…` do the same
  thing) is your call — **say which you built.**

### R-h9r2-8 — VSWR: reuse the IDEA and the maths, not the type

**Check before you build.** The Data Display's VSWR is `Marker.VswrEnabled` / `Marker.VswrValue`,
gated by `PlotRenderer.VswrAvailableFor` and drawn by `MarkerRenderer.DrawVswrLocus`. Those hang off the
Data Display's own `Marker`/`Trace`/`Plot` types, which harmonicaRF does not use — its markers are
`HarmonicaMarker` and its panels are drawn by `HarmonicaPanelRenderer`. So:

- **`DrawVswrLocus`'s own geometry is the reusable part.** If it can be called with a centre, a VSWR value
  and a transform without dragging a `Trace` in, call it. If it cannot, extract the locus computation into
  something both can call rather than copying it — a VSWR circle drawn two ways that disagree at the
  fourth decimal is exactly the kind of divergence this codebase's own notes keep warning about.
  **Report which you did.**
- `HarmonicaMarker` gains `VswrEnabled` / `VswrValue` (default 2.0, matching the Data Display's own
  default). These ARE document state — persist them in the `.charm` marker block, additive, **no
  `FormatVersion` bump**.
- The circle is drawn on the Smith panel through `HarmonicaPanelRenderer`'s own `GammaToCanvas`
  (R-h9b-1/4's transform pair), never `PlotRenderer.BuildTransforms` directly — that is the bug R1B's own
  diagnosis found and it is one careless call away from returning.
- **"and the VSWR interactions"** is the loosest part of the ask. The Data Display's own interaction is
  the menu toggle plus a numeric field in the marker editor. Build that much; if you build more, say
  what and why.

### R-h9r2-9 — Snap to Grid means snap to a Γ GRID POINT

Not the Smith chart's constant-R/X arcs, and not a rectangular lattice: the owner says *"snap the
harmonicaRF marker onto Grid points"*, and the grid points are `Frame.SmithPower.GridPoints` — the Γ
samples the contour grid swept. A toggle per marker; while on, a drag lands on the nearest grid sample
rather than the raw cursor Γ.

- The Data Display's own "Snap to Point" is the shape to mirror (a checkbox-icon menu item that flips a
  per-marker flag and immediately re-resolves the marker's position), not the implementation — it snaps
  to a contour sample, which is a different set of points.
- **Snap must apply where the drag COMMITS, not only where it is drawn**, or the marker will show snapped
  and the solve will use the raw value.
- **A snap with no grid is a no-op, not an error.** `SkipContours` frames and a document that has never
  solved a grid both leave `GridPoints` empty; the toggle stays available and simply does nothing until
  there is something to snap to. Say so in the tooltip rather than disabling it, since the list fills in
  a moment.

### R-h9r2-10 — Remove, and why L1 cannot be removed

Last item, after a separator. `Remove L2` (the label carries the marker's own `Name`) removes the marker
and sets that band's termination to Z = 1e-6 — the same short §2 makes the default, so "removed" and
"never touched" are the same state internally.

- `RemoveMarkerBand` already exists and already refuses band 1 (§4.2: S1 and L1 are ALWAYS present).
  **The menu item is DISABLED with a stated reason for band 1**, never hidden — R13a's own rule, and a
  missing item reads as a bug where a greyed one with a tooltip reads as a rule.
- **S1 is equally unremovable** and the owner's note names only L1. Disable both; say so.
- Setting the termination to the short is a separate call from removing the marker
  (`SetMarkerImpedance` then `RemoveMarkerBand`, or the reverse) — do it in the order that leaves one
  frame request, not two, and make it one undo-equivalent action from the user's point of view.

---

## 5. Scope guardrails

- **No chrome work** — the macOS menu bar, the Smith titles, the DCIV defaults, the inline editor, the
  power-sweep range, Set Z0, the readout strip and the format flyout are **2B**.
- **Do not make §6.5's plane/harmonic selectors per-chart.** `HarmonicaSolver.Options.GridSide`'s own
  comment records why; §1's rule works on the document-wide value.
- **Do not weaken `ContourGrid._reusableAgainst`.** §1.1's Finding B relies on it being exactly as
  conservative as it is. If a band the grid does NOT sweep changes, the grid must still rebuild.
- **Do not persist the z-order promotion** (§3) or the drag's own transient state to `.charm`.
- **No `.charm` `FormatVersion` bump** — the VSWR fields and the snap flag are additive-with-a-default,
  like every other marker/appearance field before them.
- **Never widen `PlotRenderer` / `AxesRenderer` / the Data Display's `Marker`** for a harmonicaRF need.
  The `AnnulusHeadroom` precedent is the rule.
- `src/Core`, `src/Engine`, `RfCore` untouched.

---

## 6. Gates

1. **Build + `dotnet test` green** — `tests/Ui.Tests` and `tests/Harmonica.Tests` while working, full
   solution at the end.
2. **Dragging L1 while the grid sweeps the 1f₀ Load plane sweeps NO grid points**, on every frame of the
   drag AND on release — counter-gated on `LastSolveCount`, with the measured before/after figures in the
   completion note. The published contours are unchanged across the whole gesture.
3. **Dragging L2 while the grid sweeps 2f₀ Load behaves identically**; dragging L2 while the grid sweeps
   **1f₀** Load still re-solves the grid on release.
4. **The isolines and grid glyphs never empty during a drag.** A frozen frame's `SmithPanelData` carries
   the previous frame's `Contours` / `GridPoints` / `Optimum`, and both Smith charts update together on
   the next full frame.
5. **A carried contour layer is refused when the swept plane or band has changed** — the panel publishes
   empty rather than a stale picture, with the compared fields named.
6. **No "Contours frozen while dragging" (or coarse-raster/coarse-grid) message can be produced**, from
   any rung. Tier A's own health message still can.
7. **A grid-point drag moves that glyph live** and re-solves that one point, with the chosen approach
   (§R-h9r2-4) named.
8. **A new document opens with S2/L2/L3 at Z = 1e-6** and S1/L1 unchanged; a loaded `.charm` is
   untouched.
9. **Markers render L1 above L2 above L3 above S1 above S2**, and clicking S2 brings it to the front —
   with the hit test agreeing about which marker is on top when two overlap.
10. **Right-clicking a marker opens its own menu**: three format rows each with `Set…`, a VSWR toggle
    that draws a circle on the chart, Snap to Grid, a separator, and `Remove L2` — with `Remove` disabled
    and explained on S1 and L1.
11. **A Set… dialog commits through `SetMarkerGamma`/`SetMarkerImpedance`** and the readout strip agrees
    with the marker menu about how the same number is spelled.
12. **VSWR and the snap flag survive a `.charm` round trip**; a `.charm` written before them still opens.

**Interactive verification is required** for the drag feel, the marker context menu and the VSWR circle —
no visual driver here, matching every prior harmonicaRF phase. List the exact gestures in the completion
note under "please confirm on your end".
