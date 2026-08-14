# Brief — harmonicaRF Round 6B: the Smith charts — VSWR, added grid points, and the fly menus

**Read first:** `src/Ui/Harmonica/HarmonicaVswrHandle.cs` (its header comment especially — the Möbius
finding it records is load-bearing for everything in §1), `src/Ui/Harmonica/HarmonicaPointer.cs`
(`HarmonicaHitTest.Resolve`, `HarmonicaGrabKind`, the drag apply at ~line 429),
`src/Ui/Views/Harmonica/HarmonicaView.axaml.cs` (`OnCanvasContextMenuOpening`, `BuildMarkerMenu`),
`src/Ui/Harmonica/Renderers/HarmonicaPanelRenderer.cs` (`DrawVswrLocus`, `DrawOptima`,
`DrawGridPoints`, `TitleBandHeight`/`DrawTitleRows`), and — as the model to copy in §1 —
`src/Ui/DataDisplay/Controls/PlotControl.cs` (`HitTestVswrLocus`, `_vswrReadoutActive`,
`_vswrReadoutPt`) with `src/Ui/DataDisplay/Renderers/PlotRenderer.cs` (the `VswrReadout` record and
its draw block, ~line 303).

**Order:** this brief owns the *shared* panel/title fly-menu dispatch that R6D and R6E both extend.
Land §4 before those briefs touch the same file.

**Do NOT update any `CLAUDE.md`.** Write to `src/Ui/RESOLVED.md` only if something here is genuinely
worth the next person's time.

---

## 1. VSWR circle — no gripper, drag anywhere, unclamped, with a live readout

### 1.1 Remove the gripper; grab the locus itself

Today `HarmonicaHitTest.Resolve` (`HarmonicaPointer.cs:~189-201`) hit-tests a single point —
`HarmonicaVswrHandle.HandleGamma`, the θ = 0 sample — within
`VswrHandleGrabRadiusDevicePixels = 9.0`, and `HarmonicaPanelRenderer.DrawVswrLocus` draws a handle
glyph there. **Both go.** The user grabs the circle anywhere on its circumference.

The Data Display already does exactly this and is the implementation to copy —
`PlotControl.HitTestVswrLocus` samples `LoadpullSurface.VswrLocus` at its default resolution and
measures point-to-segment distance against every edge of the resulting polyline, with a grab radius
scaled off the panel's short side. Do the same here, on harmonicaRF's own transform (raw Γ →
canvas, `GammaToCanvas`, never `IntrinsicGlyphScale` — `HarmonicaVswrHandle`'s header says why).
Keep the grab tolerance in device pixels and keep the existing `HarmonicaGrabKind.VswrHandle` name so
the drag path, the z-order rules and the context-menu branch in `OnCanvasContextMenuOpening` all keep
working; only what the hit test *matches* changes.

**Z-order matters:** a marker sits at the centre of its own circle and other markers may sit on top of
the locus. The existing resolve order (markers first, then VSWR, per `OnCanvasContextMenuOpening`'s own
comment) stays — a click that lands on both a marker and a locus is the marker's.

### 1.2 Unclamp the value

Owner ruling: **no clamping — the user may drag the circle outside the Smith chart if they want.**
`HarmonicaVswrHandle.MinVswr = 1.001` / `MaxVswr = 199.0` and `VswrOf`'s `Math.Clamp(rho, 0, 0.99)`
currently saturate the drag, and `HarmonicaViewModel.SetMarkerVswr` re-clamps by round-tripping through
ρ. Remove the clamping from the drag path.

Keep exactly one floor, for a reason that is geometric rather than a policy: **VSWR ≥ 1**. Below 1 the
ρ = (VSWR−1)/(VSWR+1) relation goes negative and the circle is not a circle. Use a floor just above 1
(the existing `MinVswr` constant is fine for this) and say in the code comment that this is the only
remaining restriction and why. `VswrThrough`'s bisection bracket must widen with the ceiling — an
unbounded upper end needs a bracket that actually contains the answer, so pick a large finite ceiling
for the *search* (document the number) rather than pretending the bisection is unbounded.

This is a display annotation over an already-solved termination: `SetMarkerVswr`'s "no re-solve, just
`RedrawRequested` + `DirtyChanged`" contract is correct and stays.

### 1.3 The live readout glyph

While dragging, draw the current value near the pointer, in **`Harmonica.ReadoutText`**. Copy the Data
Display's shape exactly:

- the control records the pointer position and an "active" flag on press/move, clears both on release
  (`PlotControl.cs:837/1019/1110`);
- the renderer draws it **unclipped, last**, offset from the pointer (`+10, −10` there) so it is never
  cut off at the panel edge (`PlotRenderer.cs:303-311`);
- text is `VSWR: <value>`. Data Display uses `F4`; harmonicaRF's own marker menu uses `0.##`.
  **Use one format for both this readout and §2's menu header** — pick `0.##` (the harmonicaRF
  convention) and use it in both places, so the number the user drags to is the number the menu then
  shows.

Font: the same `SkiaFonts.PlexRegular` at a size scaled off the panel's short side, matching the Data
Display's `0.0224 × min(W,H)`. The colour is the one difference from the Data Display, which hardcodes
black — use `theme.ReadoutText`.

### 1.4 Tests

`tests/Ui.Tests/Harmonica/HarmonicaVswrHandleTests.cs` exists and pins the old handle behaviour —
rework it rather than deleting it. What must be pinned now:

- a point ON the locus (any θ, not just 0) at grab tolerance resolves to `VswrHandleKind`;
- a point well off the locus does not;
- a drag to a Γ **outside the unit circle** produces the VSWR whose locus passes through it, with no
  saturation — the direct test of §1.2 (assert the resulting locus actually passes through the drag
  point, which is the invariant, rather than asserting a magic number);
- the readout text formats identically to the menu header (one shared formatter, asserted).

---

## 2. The marker context menu

`BuildMarkerMenu` in `src/Ui/Views/Harmonica/HarmonicaView.axaml.cs:~880`. Current order: three format
rows (each with its own "Set…"), separator, VSWR toggle, Snap to Grid, separator, Remove.

### 2.1 `VSWR: <val>` with a `Set…` submenu

The header becomes **`VSWR: 2.5`** (value in the shared `0.##` format from §1.3) — it stays a checkbox
item that toggles the circle, and it gains **one submenu item, `Set…`**, opening a small numeric dialog.

Validation, robustly:

- accept any finite number **≥ 1** (a negative number is not a VSWR — the owner's note "negative
  values are ok" applies to *impedance/Γ* entry elsewhere, not here; **values less than 1 are not
  allowed** is the operative rule and 1 is the mathematical floor, so refuse < 1 and refuse
  non-finite);
- reject-and-keep-the-text on bad input with a stated reason, never a silent substitution — the
  `HarmonicaSetZ0Dialog` is the closest existing precedent for shape and size; reuse it rather than
  inventing a fourth dialog idiom;
- OK commits through `HarmonicaViewModel.SetMarkerVswr`, the same call the drag uses.

Setting a value also **enables** the circle if it was off — typing a number and seeing nothing happen
is the failure mode to avoid. State that in the code.

### 2.2 `Add Point` — directly under `Snap to Grid`

Adds a Γ point to the loadpull grid **at the marker's own Γ**.

The machinery exists: `HarmonicaSolver.Options.GammaGrid` is "an explicit Γ scatter, superseding
`Rings`/`Spokes`" (R-h7-11), installed by `HarmonicaViewModel.SetGammaGrid` and already used by
`Import .gam…`. So an added point is a Γ appended to the document's effective scatter, not a new
concept.

**Owner rulings on lifetime:**

- added points **persist in the `.charm`** and survive a re-solve;
- they are **additive on top of the current ring/spoke preset** — adding a point to a 3 × 12 grid gives
  3 × 12 + 1 solved points, it does not throw the preset away;
- **`Grid ▸ Reset Grid` clears them, and so does changing `Grid ▸ Grid Preset`** — the preset must
  always describe exactly what is on screen. Say this in the menu's own tooltip.

Because the preset and the added points must now compose, `SetGammaGrid`'s current "replace the whole
scatter" contract is not enough on its own. The cleanest shape: keep the preset (rings/spokes) as the
*base* and carry a separate `AddedPoints` list on the view model; the solver's `GammaGrid` is
`RingGrid(rings, spokes, maxGamma)` concatenated with the added points, and an imported `.gam` still
replaces the base outright. Whatever you choose, one place must answer "what Γ points is this document
solving", and both the glyph renderer and the solver must read that same place — a second source for
this drifts immediately.

After adding:

1. **The grid glyphs update** — `SmithPanelData.GridPoints` is what `DrawGridPoints` renders, and it is
   built from `grid.Points` in `HarmonicaSolver` (`GridPoints = [.. grid.Points.Select(…)]`), so a
   correct re-solve updates them for free. If you find yourself splicing a point into `GridPoints` for
   display, you have taken the display-only shortcut `MoveGridPointForDisplay` uses for a *drag* — that
   is correct for a drag preview and wrong here, because this point has no solved result yet.
2. **The loadpull re-runs for the new point and the contours/MXP/MXE update.** A full re-solve of every
   point is the honest fallback; solving only the new point and merging it into the existing set is
   better if `ContourGrid.Build`'s structure allows it. Either is acceptable — say which you did and
   what it costs. Note that adding a point invalidates `ContourGrid`'s factorization cache by
   construction (the node SET moved — see its `_factor`/`_factorMask` note), so no special handling is
   needed there.
3. A point that does not compress comes back a **hole**, drawn hollow, exactly like any other. Do not
   special-case an added point's failure.

### 2.3 `Add Points to VSWR` — directly under `Add Point`

Adds **12 uniformly spaced points along this marker's VSWR circle** to the grid, through the same path
as §2.2. Uniform in θ on the locus: `LoadpullSurface.VswrLocus(center, vswr, SurfacePlane.Gamma, z0,
nPoints: 12)` gives exactly that, and it is the same call the renderer and `HarmonicaVswrHandle`
already use, so the points land visibly *on* the drawn circle.

**Disabled (greyed) when the marker's VSWR circle is off** (`marker.VswrEnabled == false`), with a
tooltip saying so — the same "disabled with a stated reason" shape `Remove` already uses on band 1.

Since §1.2 unclamps the VSWR, some of those 12 points can land outside the unit circle. That is a
legitimate active termination and must not be filtered out silently; if the Pin search cannot converge
there it comes back a hole, which is the honest answer.

### 2.4 What is removed

Nothing in this menu. (`Cursor Snap to Compression` is a **Display**-menu removal and belongs to R6A
§4 — do not remove it from here, it was never here.)

---

## 3. Remove the MXP / MXE glyphs from the Smith charts

`HarmonicaPanelRenderer.DrawOptima` (~line 672) draws one cross at `d.Optimum?.Gamma`. **Remove the
glyph rendering** — deferred to v2.

Remove only the drawing. **`SmithPanelData.Optimum` stays populated**, because the MXP/MXE readout
columns (`HarmonicaSolver.AddMxColumn`) read exactly that record and R6C keeps those columns. The
backdrop cache's `LayerAKey` includes `Optimum` (`HarmonicaPanelRenderer.cs:392`) — once the cross is
not drawn, `Optimum` no longer belongs in that key, and leaving it there means the whole cached layer
is thrown away every time the optimum moves during a drag. **Take it out of the key and say so**;
that is a small drag-cost win, not a regression, but it must be a deliberate edit rather than a
leftover.

`tests/Ui.Tests/Harmonica/HarmonicaPanelTests.cs` almost certainly asserts the cross is drawn. Invert
those assertions rather than deleting them: the point worth pinning now is that the optimum is still
*computed and reported* while nothing is drawn for it.

---

## 4. Fly menus on the Smith charts

This section establishes the pattern R6D and R6E extend to the other panels. Build it so a second
panel's menu is a few lines, not a copy.

### 4.1 The dispatch

`OnCanvasContextMenuOpening` in `HarmonicaView.axaml.cs` is already the single right-click entry point:
the canvas only *records* the target (`HarmonicaCanvas.ContextMenuTarget`), and this handler rebuilds
the menu fresh each open — keep that shape exactly (it is the L1 fix pattern, and it is why a stale
menu can never appear for a click that landed elsewhere).

Add two branches, resolved in this order after the existing marker branch:

1. **Smith panel TITLE band** — `HarmonicaPanelRenderer.TitleBandHeight(size)` already computes the
   band's height, and `HarmonicaHitTest.ToPanel` converts a canvas point to panel-local coordinates.
   A click with local `Y < TitleBandHeight(size)` is a title click. Use the existing accessor; do not
   hand-derive the band geometry (the same rule the power-sweep X-label branch already follows by
   calling `AxesRenderer.ComputeLabelHitRects`).
2. **Smith panel BODY** — anywhere else in either Smith panel.

### 4.2 Body menu (both Smith charts)

| item | behaviour |
|---|---|
| **Copy** | copies THIS panel to the clipboard. `HarmonicaClipboard.CopyAsync(Canvas, h, panelId)` already does exactly this and is what `Edit ▸ Copy Plot` calls — pass the resolved `panelId` instead of `Canvas.PanelUnderPointer()`. **Do not touch `PlotExporter` or write a second exporter.** |
| **Show Grid Points** | checkbox, toggles `HarmonicaViewModel.ShowGridPoints` — the same command `Display ▸ Grid Points` uses (`ToggleShowGridPointsCommand`). It is one document-wide flag, so it affects both Smith charts; that is what the owner asked for. |

### 4.3 Title menu

On **both** Smith charts:

- **Contour Plane** → Load / Source — `SetGridSideCommand`, parameters `"Load"` / `"Source"`;
- **Contour Harmonic** → built from `HarmonicaMenuViewModel.ContourHarmonics`, which already tracks K
  (do not hardcode f₀/2f₀/3f₀ — that was an owner-reported bug once already, see the
  `RebuildNativeBandMenus` comment in `HarmonicaMenuView.axaml`).

On the **efficiency** chart only, additionally:

- **Efficiency Metric** → Drain Efficiency / PAE — `SetEfficiencyMetricCommand`, parameters
  `"DE"` / `"PAE"`.

These are **additional** ways to reach the same commands: `Display ▸ Contour Plane`, `Contour Harmonic`
and `Efficiency Metric` all stay exactly where they are. Bind to the same commands — never a parallel
implementation — and show the current selection as a checked item so the menu doubles as a readout.

### 4.4 Tests

`tests/Ui.Tests/Harmonica/HarmonicaMenuAndInputTests.cs` is the home for menu-shape tests. What is worth
pinning:

- title-band vs body resolution at a boundary point (one pixel above and one below `TitleBandHeight`);
- the power chart's title menu has no Efficiency Metric item, the efficiency chart's does;
- every fly-menu item resolves to the SAME `ICommand` instance as its `Display`-menu counterpart —
  that is the assertion that stops the two drifting apart.

---

## 5. Gates

1. `dotnet test tests/Ui.Tests` and `dotnet test tests/Harmonica.Tests` green; `tests/Firewall.Tests`
   green.
2. Owner check: drag a VSWR circle by its circumference from any angle, past the edge of the chart,
   with the value readable next to the cursor throughout; `VSWR: <val> ▸ Set…` accepts 3.7 and refuses
   0.5 with a message; `Add Point` on a marker puts a dot exactly under that marker and the contours
   move; `Add Points to VSWR` puts 12 dots on the circle and is greyed out when the circle is off;
   right-click on a Smith chart offers Copy and Show Grid Points; right-click on its title offers the
   contour menus.
3. Report the cost of §2.2's re-solve (added point → visible contour update) — the owner is sensitive
   to anything that stalls a drag, and this runs on a click rather than a drag, but the number is worth
   having.
