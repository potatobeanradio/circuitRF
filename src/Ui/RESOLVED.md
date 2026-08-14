# src/Ui — resolved briefs (detail, off the CLAUDE.md growth path)

`src/Ui/CLAUDE.md` reached 21,417 lines as an append-only phase log and had to be archived to
`src/Ui/HISTORY.md`. Going forward, a completed brief's detail lands here instead — one `##` section
per brief, sparingly, only for findings that are still true, still surprising, and would cost someone
real time to rediscover. `CLAUDE.md` stays for durable, still-true conventions only. Mirrors
`src/Ui/DataDisplay/RESOLVED.md`'s own pattern.

## A batch of owner follow-ups: marker clamp, Contour Harmonic, a settings dialog, silent hooks (2026-08-13)

**`HarmonicaViewModel.SetMarkerGamma`'s own clamp was redundant with — and stricter than —
`HarmonicaDataSet.ImpedanceOf`'s already-correct handling of the SAME edge case.** The owner asked
for markers to be draggable outside the unit circle (negative Z, an active termination); the clamp
(`if (mag > 0.999) gamma = gamma/mag*0.999`) silently forbade ANY `|Γ| > 0.999`, forever. But
`ImpedanceOf`, one call downstream, already nudges only the true singularity (Γ = 1 exactly, where
`1−Γ` is the pole) and its own doc comment already says "`|Γ| > 1` is left alone, because an active
termination is a legitimate thing... to land on" — so the fix was deleting the redundant guard in
`SetMarkerGamma`, not narrowing it. **Lesson worth keeping: when a caller pre-clamps "to be safe"
before handing a value to a callee that already has its own, correct handling of the dangerous case,
check the callee before assuming the caller's guard is load-bearing** — this one had been silently
overriding a design decision made lower in the stack the whole time.

**Contour Harmonic was three hardcoded XAML items (f₀/2f₀/3f₀) on EACH menu surface, on a document
whose K is a live setting.** `SetGridHarmonicCommand` itself was already K-aware (validates
`k <= Terminations.HarmonicCount`) — only the ITEM LIST was frozen at 3, so K=5 had no menu path to
the bands it actually has. Fixed by mirroring the Markers menu's own `SourceBands`/`LoadBands`
pattern exactly (`HarmonicaMenuViewModel.ContourHarmonics`, an `ObservableCollection` rebuilt to K's
own length, triggered by the SAME `Markers.CollectionChanged` event the band checkboxes already used
— K only ever moves through `RetargetTerminations`, which always touches `Markers`, so no new
"K changed" signal was needed). Both surfaces (in-window `ItemsSource`, NativeMenu's own
code-behind `Fill`) share the pattern the band checkboxes already established; a new test
(`DisplayMenu_ListsTheSameItems_OnBothSurfaces`) checks SUBMENU parity specifically, since the
existing menu-parity test only ever compared top-level headers and would not have caught either
surface drifting alone.

**The SAME silent-guard bug R-h9c-10 diagnosed and fixed once (`ShowSetDutAsync`) was still sitting,
unfixed, in two sibling hooks in the identical file — `ShowPreferencesAsync` (the owner's own "Edit ▸
Settings does nothing" report — there is no menu item literally named "Settings"; it's Preferences…)
and `ShowSetZ0Async` (found alongside it, same shape, not yet reported).** `if (_doc is null ||
TopLevel.GetTopLevel(this) is not Window owner) return;` — a bare early return throws nothing, so
`RunHook`'s own exception-reporting fix (R-h9a-13) cannot help with it; the failure is silent by
construction, not by an exception slipping past a handler. **Worth stating plainly: R-h9c-10's own
note ("every OTHER dialog-opening hook in this file shares the identical guard shape... fixed because
it is the one under report") was accurate and specific — the SAME class of bug was always going to
resurface in the next sibling hook someone happened to exercise, and it did, twice.** Both are now
fixed the identical way (`Vm is not { } h → return`, then a NAMED `SolveError` + `Refresh()` on a
missing `TopLevel`) — any FUTURE dialog-opening hook copy-pasted from one of these now copies the
reporting shape too, not the silent one.

**A new per-document dialog (`HarmonicaAdvancedSettingsDialog`) for the four inputs the strip no
longer renders** (loadline pts / FFT× / charge / M — owner: "remove... from the display... set via a
menu item AND a settings in a separate dialog"). `HarmonicaInputs.Build` is UNCHANGED and still
returns all four — only `ReadoutStripView.SetInputs` stopped rendering them
(`HiddenFromStripKeys`, alongside the pre-existing `SettingsColumnKeys` split) — so the dialog reads
and writes through the exact same `HarmonicaViewModel.ApplyInput`/`HarmonicaInputs` keys the strip
row used to, per `HarmonicaSetZ0Dialog`'s own established "second surface, never a second write path"
rule. Four independent fields, each its own key — unlike `HarmonicaPowerSweepDialog`'s combined
Start/Stop/Step, there is no cross-field relationship to validate together here.

**Owner: "Idq should display in mA, not A; convert to A when searching for the proper Vgs."**
`BiasSpec.Idq` itself stays amps (the unit `SolveVgsForIdq` and every other solver-side consumer
expect) — the mA/A boundary is exactly ONE place, `HarmonicaInputs.Build`/`Apply`'s own Idq rows.
**Owner, same conversation: "keep Idq to 1 decimal place, Vgs to 3 — the inline editor should still
show the full value."** This needed a real DISPLAY-vs-EDIT split that did not exist before:
`HarmonicaInput.EditText` (falls back to `Text` when absent — every other input has no separate
rounding) is what an inline editor now seeds from, while `Text` is what the row shows at rest.
`ReadoutStripView`'s `SettingsRowState` gained `EditSeedText`, refreshed every
`UpdateSettingsColumnRow` call alongside the existing placeholder bookkeeping, so a double-click
reads the CURRENT full-precision value live rather than closing over a build-time one — the identical
staleness concern that already justified reading `value.Text`/`state.IsPlaceholder` live instead of
capturing `input` in R3C's own Settings-column closure.

## The strip's columns, Smith titles, and the efficiency axis fringe (brief-harmonicarf-r3c, 2026-08-13)

**The antialias/cap mismatch behind the two-colour axis line, and it will recur.** The power-sweep
plot's right axis showed a green fringe under the red efficiency-axis overlay because
`HarmonicaPanelRenderer.DrawEfficiencyAxisOverlay`'s cover stroke (`linePaint`/`tickPaint`) was drawn
`IsAntialias = false` with the default `Butt` cap, over `AxesRenderer.StrokePaint`'s antialiased,
`Square`-capped stroke of the identical nominal width. An antialiased stroke covers a wider pixel
footprint than a hard-edged one of the same width, and a `Square` cap extends half a stroke-width past
each endpoint where `Butt` does not — so the underlying border was always going to show as a border
around the cover, on every side and past both ends, regardless of colour choice. **The general lesson:
when one renderer paints over another's stroke to recolour it (rather than to add a new one), the
cover's `SKPaint` must match the covered one's shape field-for-field — width and colour are not
enough.** Fixed by matching `AxesRenderer.StrokePaint` exactly (`IsAntialias = true`, `StrokeCap =
Square`) rather than by widening `AxesRenderer` itself, per the standing "never widen `PlotRenderer`/
`AxesRenderer` for a harmonicaRF need" rule.

**Two owner-reported follow-ups on the inline editor itself, both found after the first pass landed —
worth keeping because they will recur wherever this codebase floats an editor over live content.**

- **Escape was silently eaten by `WorkspaceWindow`'s own `<KeyBinding Gesture="Escape"
  Command="{Binding DisarmPlacementCommand}"/>`.** A docked document sits inside that window, and a
  `KeyBinding` gesture is resolved BEFORE ordinary tunnel/bubble routing ever reaches the focused
  control — so the editor's own `box.KeyDown` Escape branch never ran. This is not a new failure mode:
  `SchematicView.OnViewKeyDownTunnel` documents hitting the IDENTICAL problem for its own inline
  editor, and the fix is the same shape — a `Tunnel`-routed `KeyDownEvent` handler registered with
  `handledEventsToo: true` (the only way to still see a key the KeyBinding already marked `Handled`),
  intercepting Escape for whichever editor currently has focus. **Any future inline editor hosted
  inside `WorkspaceWindow` needs this same handler — Escape does not work there by default.**
- **A spliced-in editor widens its own row, and a `StackPanel` column sizes to its widest row.** The
  original R-h9c-8 scheme removed the value control and inserted the `TextBox` in its place — so the
  box's `MinWidth` (70px) became that ROW's width the moment it opened, and every column laid out
  after it in `Columns` (a horizontal `StackPanel`) visibly shifted right. Fixed by floating the box in
  a new transparent `Canvas` (`EditorOverlay`, layered on top of the content in a shared `Panel`) at
  the original control's translated position, while the original control merely goes `Opacity = 0`
  (which reserves its layout slot; removing it would not). **The general lesson: an editor that needs
  to be WIDER than its cell must never become a literal member of that cell's layout container — float
  it in an overlay that shares the container's coordinate space instead.** A useful side effect: since
  `EditorOverlay` is untouched by `SetItems`'s per-frame `.Clear()` of the Source/Load columns, an open
  Source/Load editor now survives a published-frame refresh better than it did before this change,
  even though that specific hazard (previous bullet) was not itself the target here.
- **A third follow-up, same session: the flat `MinWidth = 70` this bullet's own fix carried over
  (unused once nothing else in the row constrained it, but still oversized for a short value like
  "-1.5") was itself owner-reported.** Replaced with `ReadoutStripView.CalcInlineEditWidth(text,
  fontSize)` — the IDENTICAL formula `SchematicView.CalcInlineEditWidth` already uses for its own
  inline editor (average per-char width for IBM Plex Sans, floored at two characters) — set on open and
  recomputed on every `TextChanged`, so the box genuinely grows and shrinks live as the user types
  rather than being sized once. Growing to the right falls out for free from the overlay shape above:
  the box's `Canvas.Left` is set once at open time and never touched again, so widening only moves the
  RIGHT edge.

**The title-band padding was NEVER the real cause of "the title renders too high above the chart" —
and two prior fixes (R-h9r2-13, then this brief's own §4) both tuned it anyway, because nobody had
measured the actual gap.** The 3rd owner report of the identical complaint prompted actually measuring
it against the shipped code rather than adjusting the same few-pixel constant a third time: on a
representative panel the gap between the title band and the VISIBLE Smith circle was **~63px, ~11% of
the chart's own height** — two orders of magnitude bigger than `TitleBottomPaddingFraction`'s few
pixels, which is exactly why tuning it twice never visibly helped. **The real cause was
`HarmonicaPanelRenderer.AnnulusHeadroom`**, R-h45-4's panel-wide 20% shrink (`k=1/(1+0.25)`,
`IntrinsicGlyphScale.DefaultMargin`) that reserves room around the ENTIRE Smith circle so a marker for
a device whose intrinsic Γ is legitimately outside the unit circle (§4.5 consequence 2 — ordinary, not
an error) is never clipped at the panel edge. That shrink is applied UNIFORMLY on all four sides via a
scale about the panel's own centre — so half of the freed-up space sits above the circle where the
title already lives, and half below where nothing does; neither prior fix touched it because both
were reasoning about the title band in isolation from what the chart itself does within `chartSize`.
Presented with the actual trade-off (a real, deliberately-built, but never empirically-measured-against
real device data safety margin, vs. a visibly tight chart), the owner chose to **remove the margin
entirely** (`AnnulusHeadroom = 0`, AskUserQuestion, 2026-08-13) and explicitly accepted that a
sufficiently far-out intrinsic glyph can be clipped at the panel edge again — the exact failure mode
R-h45-4 was built to prevent. `IntrinsicGlyphScale.DefaultMargin` itself is untouched (0.25) — it
governs the compression CURVE (how a glyph's position reads), a distinct question from whether the
panel shrinks to make room for it, and the request was about the panel, not the curve. **General
lesson worth keeping: when a repeated visual complaint survives a plausible-looking fix twice, measure
the actual pixel gap against the shipped renderer before touching the same constant a third time** —
the fix that finally worked took five minutes once the real number was in hand; the two before it
spent that same five minutes each on the wrong knob.

**The strip-rebuild-destroys-an-open-editor hazard, and how it was closed for the new Settings
column.** `ReadoutStripView.SetItems` (Source/Load/MXP/MXE) and `SetInputs` (the input half) both run
on every published frame and both used to handle this differently: `SetItems` clears and rebuilds its
four columns unconditionally (safe only because none of THOSE rows survive a rebuild anyway — an open
editor there gets destroyed and reopened as a stale row every published frame, a pre-existing gap this
brief did not touch), while `SetInputs`'s original always-live-`TextBox` scheme used a shape signature
plus per-row `UpdateInPlace` specifically so a solve landing mid-keystroke could not stomp the caret.
R3C's new Settings column (double-click-to-edit, like Source/Load) needed the SAME discipline
`SetInputs` already had, extended to cover "a row is mid-edit" rather than just "a TextBox has focus":
the column is built ONCE (its shape — the same 7 keys, in the same order, every time — never changes,
since `HarmonicaInputs.Build` always emits them) and every later call only WRITES into the existing
rows, skipping a row's value slot entirely while its own `SettingsRowState.IsEditing` is true. The
decision itself (`ReadoutStripView.SettingsRowMayBeOverwritten(bool isEditing)`) is a bare pure
predicate for exactly this reason — Ui.Tests cannot construct a live Avalonia control to prove a real
`TextBox` survives a refresh, but the boolean logic gating it is fully testable without one.

**The title band's render/hit-test coupling** (`HarmonicaPanelRenderer.TitleBandHeight`/
`GammaToCanvas`/`CanvasToGamma`) needed nothing new here beyond what R1B already documented — the 85%
size factor and the bottom-padding constant both flow through the same `TitleBandHeight` both
directions already call, so the coupling that fixed R1B's render-vs-hit-test bug could not be
reopened by construction. One thing worth stating that the existing comments do not: **the 7.0pt
floor is deliberately NOT scaled by the new 0.85× factor** — a panel small enough to hit the floor is
already at the smallest legible size, and shrinking the floor itself would only make an
already-clamped title harder to read for no space saved.

**A real, pre-existing gap found while surfacing the "solved Vgs" R3C §3 asked for, worth flagging
here because a future maintainer touching bias/Idq will otherwise assume the opposite.** The removed
readout-half "Vgs" row used to show the literal text `"(from Idq)"` whenever the bias was
current-driven — never an actual number. Searching the whole repo for how `Bias.Idq` is consumed
confirms why: `HarmonicaContext.Apply` substitutes a bare `model.Bias.Vgs ?? 0.0` whenever `Vgs` is
null, and nothing anywhere runs the "1-D secant on the DC solve" the tooltips and doc comments
describe. `Idq` is round-tripped and persisted (`.charm`, `CharmIo`) but never actually drives a
solve. R3C §3 preserves the informational text (now the Vgs Settings input's own `Placeholder`) rather
than inventing a number — implementing the secant itself is solver work and out of this brief's scope
(§6's guardrails).

## §1.4 — the drag frame's render cost, not the solve, is most of what the owner saw (brief-harmonicarf-r3b, 2026-08-13)

**The solve is no longer the story.** After §1's evaluator work, a mid-drag L1-marker frame's SOLVE
side (tier-A 46-solve sweep + dataset + loadline) measures **7.3 ms** — down from the brief's own
~33 ms baseline. What was never measured before is the REST of the frame, and it turns out to be the
larger half.

**Measured** (`HarmonicaDragFrameBreakdownTests`, `Category=Benchmark`, real solver + real
`HarmonicaPanelRenderer` SkiaSharp draw calls, a REAL carried-forward contour layer — the drag starts
from an already-solved 37-point grid, exactly as §1's own carry-forward rule keeps its polylines on
screen frozen through every drag frame, which a from-empty measurement would have understated):

| stage | 1x (1600×1000) | 2x / Retina (3200×2000 px) |
|---|---|---|
| solve (tier A + dataset + loadline) | 7.3 ms | 7.3 ms |
| **render** (2 Smith panels w/ 30 carried polylines + loadline + power sweep) | **11.5 ms** | **21.2 ms** |
| SolvePool.Submit → Completed round trip | ~0.0 ms | ~0.0 ms |
| **measured total** | **18.9 ms (53 fps upper bound)** | **28.5 ms (35 fps upper bound)** |

**The render is real and was previously invisible** — `HarmonicaRenderBudgetTests`' own R4 note said
the readout strip "costs a layout pass, not a frame of this number," which was correct but left the
CANVAS render itself unmeasured for an actual drag-shaped frame (a carried contour layer, not an
empty grid). It roughly **doubles from 1x to 2x**, which matters directly: a Retina/HiDPI display
(the ordinary case on macOS, one of this repo's three target platforms) pays close to the WHOLE
60 fps frame budget on the render alone, before the solve, the readout strip, or anything Avalonia
itself does are even added.

**Per-panel breakdown, the four panels drawn in isolation at their own real placement size** (not the
whole canvas — an earlier pass of this measurement drew each at full-canvas size, overstating every
panel; fixed to each panel's own sub-rect: Smith 800×600, loadline/power-sweep 640×500, matching
`RenderAt`'s own layout):

| panel | @1x | @2x |
|---|---|---|
| SmithPower | 2.40 ms | 7.01 ms |
| SmithEfficiency | 2.24 ms | 6.76 ms |
| Loadline | 1.13 ms | 1.42 ms |
| PowerSweep | 0.25 ms | 0.42 ms |

**Neither the loadline nor the power-sweep panel is the bottleneck** — combined they are 1.4 ms @1x /
1.8 ms @2x, a small fraction of the total. **The two Smith charts dominate**, at roughly 4–17× the
cost of the other two panels each, and scale far worse with device pixel count (nearly 3× from 1x to
2x, against loadline's ~1.3× and power-sweep's ~1.7×) — consistent with them being the panels that
draw the grid-point dots (37), markers, glyphs, contour polylines AND the Smith-chart chrome (circles,
grid lines, title rows) all at once, where the other two panels draw a handful of simple curves.

**Frozen contour DATA is not the same as frozen contour PIXELS — worth stating precisely, since it is
easy to mis-hear "carried forward" as "free."** R-h9r2-1's freeze means the 30 iso-line polylines are
not re-solved/re-fit/re-rastered during a drag, and that is genuinely true and unchanged. But
`HarmonicaPanelRenderer.DrawContours` is immediate-mode Skia with, by its own doc comment, "no
geometry cache" — it re-issues every `DrawPath` call from scratch on every repaint, and the panel DOES
repaint every drag frame (the marker glyph and power-sweep curve are live, which triggers
`InvalidateVisual` on the whole canvas). **Measured, isolated** (re-rendering the same frame with
`Contours` cleared): the 30 frozen polylines cost **1.0 ms @1x / 1.4 ms @2x** of the render total above
— real, but a small (~7–9%) share. The render cost is dominated by everything else on the panel (37
grid-point dots, markers/glyphs, Smith-chart chrome, the loadline and power-sweep curves), not by the
contours specifically. Caching the frozen layer as a pre-rendered picture/bitmap and compositing it
was considered as a follow-up but not built — the measured payoff (≤1.4 ms) does not justify it on
its own; it would only be worth doing as part of a broader render-caching pass across the whole panel.

**What could not be measured, and why, named explicitly rather than left implicit:**
- **The §7.5 readout-strip rebuild** (`ReadoutStripView.SetItems` — real Avalonia
  `StackPanel`/`TextBlock` construction, ~37 items → ~70–110 controls for this fixture, every
  frame). `Ui.Tests` may not call Avalonia runtime APIs (a hard project rule — SkiaSharp canvas
  drawing is not one of those, which is why the render above IS measurable), so this cannot be
  benchmarked headlessly. **`ReadoutStripView.LastSetItemsMs` (new)** self-times the call; reading it
  during the interactive check below is how this gets a real number.
- **The Avalonia compositor/dispatcher round trip** (the worker-to-UI-thread `Dispatcher.Post`,
  `InvalidateVisual`, and whatever layout/compositing Avalonia itself does around the raw canvas
  draw) — structurally unmeasurable outside a live `Application`/`Window`, for the same reason.

**The honest accounting:** measured solve+render+pool is 18.9–28.5 ms depending on device scale,
against the owner's ~90 ms (~11 fps) observation. The gap (~60–70 ms) is therefore concentrated in
exactly the two unmeasurable stages above, not spread thin across many small costs — which is a
useful, falsifiable claim for the interactive check to confirm or refute (read `LastSetItemsMs` and
compare a real drag's actual fps against the 35–53 fps upper bound this file computes from the
measurable stages alone).

## §4 — the render backdrop cache, and the pixel-mismatch bug that guarded it (brief-harmonicarf-r4, 2026-08-13)

`HarmonicaBackdropCache` (new, `src/Ui/Harmonica/Renderers/`) rasterises a Smith panel's Layer A
(chrome + frozen contour polylines + optimum cross) and Layer B (grid-point dots) once into offscreen
`SKSurface`s and blits them back — one instance per panel, owned by `HarmonicaCanvas`, never static.
`HarmonicaPanelRenderer.DrawSmithPanel` falls back to its original, byte-identical uncached draw when
no cache is supplied (export, Copy Plot, a one-off render).

**§4.5's own correctness gate — cache-on vs cache-off must be pixel-identical — did not hold on the
first cut, and the reason was subtle enough to be worth recording precisely.** `HarmonicaBackdropCacheTests`
caught it directly (`CacheOnVsOff_ArePixelIdentical_ForAStaticScene` et al.), initially failing with
~5% of pixels differing by up to 199 levels/channel — nothing like ordinary antialiasing rounding.
Root-caused to **three independent, compounding effects**, fixed in order:

1. **AA sub-pixel phase mismatch (the dominant one, ~9500 px).** An offscreen raster's own pixel grid
   always starts at phase 0 at its local origin. The live canvas, by contrast, places chart-local
   (0,0) at whatever FRACTIONAL device pixel its accumulated transform happens to land on — `ChartBox`'s
   margin/title-band arithmetic is essentially never pixel-integral. Rasterising Layer A/B at phase 0
   and blitting onto that fractional position forces Skia to resample the whole image, reprocessing
   every antialiased edge in the backdrop differently from the uncached vector draw. **Fixed** by
   reading `canvas.TotalMatrix` at the point of render, baking that exact matrix into the offscreen
   surface (`SetMatrix`, not a bare `Scale(deviceScale)`) shifted by only the INTEGER part of where
   local (0,0) lands (`floorX`/`floorY` — an integer translate cannot change AA phase), then blitting
   that integer shift back in raw device space (`canvas.SetMatrix(Identity)`, bypassing whatever CTM
   was active) — an integer-aligned, same-size copy needs no resampling at all. General on purpose
   (matrix-derived, not `deviceScale`-arithmetic-derived): verified to hold under an outer 2x HiDPI
   scale composed with a fractional outer translate too
   (`CacheOnVsOff_ArePixelIdentical_At2xWithAnOuterFractionalTransform`), not just the test harness's
   simplest identity-CTM case.
2. **Fractional destRect size (~300 px on its own).** `chartSize` (a `double`) fed a `Ceiling`d integer
   pixel size for the raster but the blit `destRect` used the un-ceiling'd fractional `chartSize`
   directly — a tiny (`pixelSize/deviceScale`)⁄`chartSize` rescale on every blit. Folded away by the
   same fix: `destRect` is now the raster's own integer extent, never `chartSize`.
3. **Double alpha-blend rounding through a transparent offscreen background (~28 px, ≤2 levels/channel
   — real, not merely theoretical).** Layer A clears to `SKColors.Transparent`, so every antialiased
   edge is 8-bit-rounded once when rasterised and AGAIN when composited onto the live canvas — two
   roundings where the uncached draw does one. **Fixed for Layer A** by clearing to the panel's real
   (opaque) background color instead: every edge blends against it exactly once, matching the uncached
   draw, and the blit degenerates to an exact copy (opaque source, no blend math needed). **Layer B
   (the grid-point dots) can't take the same fix** — it's sparse, so it can't be pre-filled with a
   uniform opaque background without occluding Layer A underneath it. Instead Layer B is **fused**
   directly onto a COPY of Layer A's already-opaque pixels in one compositing pass
   (`HarmonicaBackdropCache.GetOrRenderFusedWithLayerB`) rather than blitted as its own second
   translucent layer — exactly one rounding step per pixel, the same as the uncached path drawing dots
   directly over the already-rendered chrome. `LayerBRebuilds` still counts only when Layer B's OWN key
   (grid points/theme/chartSize/matrix/pixel size) changes, not when a recompose is forced by Layer A
   changing underneath it (`ChangingContours_RebuildsLayerA_NotLayerB` pins this distinction) — an F16
   offscreen color format was tried first as a precision fix and made things WORSE (6365 px, likely an
   implicit linear-light blend Skia applies for F16 targets), which is why the fused-compositing
   approach was built instead of chasing more bits.

**After all three: 0/176,400 differing pixels, cache-on vs cache-off, including at 2x with an outer
fractional transform.** All 15 `HarmonicaBackdropCacheTests` (bit-exact identity, and one test per
invalidation-key field — contours, levels, optimum, title/subtitle, grid points, panel rect, device
pixel scale, theme, `ShowGridPoints` toggle, iso-line labels, the R-h9r2-1 carried-list-reference
case) pass.

**Per-panel render cost, warm steady state of a marker drag** (`HarmonicaDragFrameBreakdownTests`,
`Category=Benchmark`, same 37-point carried-forward fixture §1.4 used, best of 9, measured alone),
directly against §1.4's own "before" table:

| panel | @1x before | @1x after (cache warm) | @2x before | @2x after (cache warm) |
|---|---|---|---|---|
| SmithPower | 3.30 ms | **0.16 ms** | 10.06 ms | **0.53 ms** |
| SmithEfficiency | 3.03 ms | **0.16 ms** | 9.84 ms | **0.53 ms** |

(The §1.4 "before" figures quoted above are re-measured on today's tree, not the original 2.40/2.24/
7.01/6.76 ms figures — this tree carries 37 Γ points/39 polylines against §1.4's 37/30, and §1/§3's
already-landed convergence fixes changed the exact grid, so the two are close but not identical; both
are reported as measured rather than reconciled, per this repo's own measurement-honesty convention.)

**Far better than §4.2's own "roughly halved, 3–4 ms @2x" prediction, and worth explaining rather than
just believing.** The prediction priced a naive two-separate-translucent-layer blit against "order 1–2
ms" for a raw 7.7 MB RGBA CPU copy. What's actually being blitted after the fused-compositing fix is
ONE opaque, axis-aligned, integer-pixel-aligned image — a case Skia's raster backend copies near
memcpy-speed rather than through general blend math, and the fusion means there is only ONE blit per
frame (not two) plus a handful of cheap live draws (marker glyphs, the reachable-region wash). The
speedup (≈20×, not ≈2×) reflects that the STATIC content (grid + 30–39 polylines + dots) was the
overwhelming majority of the original render cost, and a warm cache now pays for essentially none of
it every frame — consistent with, not contradicting, §4.1's own diagnosis that the cacheable share was
"most of each [panel], not 1.4 ms across both."

**§4.6 — `ReadoutStripView.LastSetItemsMs` was not read this pass.** It requires a live interactive
Avalonia session (real `StackPanel`/`TextBlock` construction — `Ui.Tests` may not call Avalonia runtime
APIs, per §1.4's own note above), which this session had no way to drive. Per the brief: not fixed
here regardless (out of scope), and the number is still worth reading in the owner's own interactive
check — §1.4's own estimate (~60–70 ms of the observed ~90 ms sitting in the strip rebuild + Avalonia
round trip) is now the DOMINANT term by a wider margin than before, since §4 just cut the render side
from ~7–10 ms/panel to ~0.2–0.5 ms/panel.

**A pre-existing, unrelated test failure was found and fixed while running the full suite as this
brief's own gate.** `HarmonicaPanelTests.Tier8_AGridWithAHole_DrawsNoContourAndNoFillInsideTheExcludedDisc`'s
own fixture (`BuildGridWithADeliberateHole`, `maxGamma: 0.85`) started failing its own precondition
(`Assert.InRange(grid.HoleCount, 1, …)`, actual 0) — not from anything in this brief, but from §3's
already-landed `PinSearch.Run` bracket fix (`src/Harmonica/RESOLVED.md`'s own §3 entry), which closed
most of the bracket-stage holes this smaller 31-point fixture used to rely on for "a few holes."
Scanned `maxGamma` 0.85–0.98 in 0.02 steps (deterministic — no RNG in this solve path): 0.90 reproduces
2/31 holes reliably; the test now uses that instead of 0.85, with a comment recording why.

## §5 — the drag-size FPS asymmetry: measured, not guessed, and it is real but small (brief-harmonicarf-r4, 2026-08-13)

The owner's own diagnosis (§5.1) named the mechanism exactly: `PinSearch.Sweep`'s `priorLevelSpectra`
(R-h9r2-19's "lever 1" — the previous FRAME's converged spectrum, tried first at every Pin level)
is a near-perfect seed on a small drag move and can be an actively misleading one on a large move that
lands the termination in a different HB solution basin, since the solution surface across the
termination plane is not smooth. Measured directly rather than assumed
(`tests/Harmonica.Tests/DragSeedPolicyTests.cs`, `Category=Benchmark`, Hero 2's GaN HEMT under
25 Ω/80+j10 Ω — the same fixture §1/§3 already use, chosen because the shipped default's own
unmarked-band terminations don't compress at all — shipped `PinMaxDbm=50`, §1's early stop already
landed, best-of-5 per frame after one discarded warm-up run):

| policy | small jump (\|ΔΓ\|≈0.004) | tangential control (\|ΔΓ\|≈0.13, const \|Γ\|=0.5) | large jump (\|ΔΓ\|≈0.99) |
|---|---|---|---|
| A — today (always reuse) | **9.23 ms** | **10.72 ms** | 13.76 ms |
| B — owner's (never reuse) | 12.10 ms | 12.01 ms | **11.94 ms** |
| C — hedged (below) | 9.22 ms | — | **11.88 ms** |

**Policy B does not win outright — measured, not assumed away.** The brief's own decision rule was
"if B's small-drag time is within noise of A, delete lever 1 and take B." It is not: B is ~24% SLOWER
than A at |ΔΓ| ≈ 0.004, a small, reproducible, above-noise gap (stable across repeated runs), and the
tangential control shows the same thing at a genuinely large per-frame Γ MOVEMENT (0.13) that never
approaches a harder region — so this is not the "large is also hard" confound §5.3 warned about; lever 1
is genuinely still winning there. So Policy B is not adopted outright.

**The threshold was found by scanning the crossover, not picked**
(`AvsB_CrossoverPoint_WhereLever1StopsHelping`, same fixture, single jump from a converged base point at
each size): A wins clearly through |ΔΓ| ≈ 0.15, ties through ~0.20–0.25, and B starts winning from
~0.30. `HarmonicaSolver.LeverOneDeltaGammaThreshold = 0.20` sits just past where A stops winning
outright — Policy C, the hedge, is what shipped: lever 1 is read only when the LARGEST single-band Γ
move since the previous frame (a freshly-marked band counts as infinite) is under this threshold.
Wired in `HarmonicaSolver.Solve` (new fields `_lastTerminationGammas`, `Lever1DisabledCount` — a
counter, not a stopwatch, gated by `HarmonicaSeedPolicyTests`), not in `PinSearch.Sweep` itself, which
is unchanged and still does exactly what its own doc comment says.

**Gradual, with one real cliff, not a clean either/or.** `PolicyA_FrameTimeVsJumpSize_GradualOrCliff`:
frame time rises smoothly from 9.7 ms (|ΔΓ|=0.01) to 11.9 ms (|ΔΓ|=0.20), then SPIKES to 18.4 ms at
|ΔΓ|=0.30 (only 103 Newton iterations there — fewer than the 118 at 0.20 — so the extra ~6.5 ms is not
"more iterations," it is one or more rungs' Newton solve taking an internal continuation-stepping
detour, §5.3's own predicted cliff mechanism), then drops back to 12.8/12.0 ms at 0.45/0.60. The cliff
is narrow and Γ-position-dependent rather than a clean function of |ΔΓ| alone — worth knowing, not
worth chasing further this pass.

**A large-jump drag frame's own factor over a small one, stated rather than asserted against a
target:** under the SHIPPED policy (C), 11.88 ms / 9.22 ms ≈ **1.29×**. Nothing like the owner's
subjective "roughly two thirds unaccounted for" 11 fps experience — which is exactly what §4's own
combined reading (below) explains.

**§5.4 — the no-op frame, independent of the policy work, landed too.** A mid-drag marker frame whose
Γ has not moved (quantised to `HarmonicaViewModel.DragNoOpGammaTolerance = 1e-4`, an order of magnitude
under both a Smith glyph's own on-screen resolution and every readout's decimal precision) past the
last frame ACTUALLY submitted to the pool never reaches `SolvePool.Submit` at all — `RequestFrameOnMarkerRelease`
returns `-1` (matching `DragGridPoint`'s own sentinel) and increments `NoOpDragFrameSkipCount`, a
counter. **Release is never skipped by this**, even when it lands within tolerance of the last mid-drag
frame — a real, full-quality solve always runs on release, matching `DragGridPoint`'s own "mid-drag is
free, release is real" shape. Gated on counters, not a stopwatch, exactly as the brief asked
(`HarmonicaDragTests.MidDragMarkerFrame_WithinToleranceOfLastSubmitted_IsSkipped_GatedOnACounterNotAStopwatch`,
`.MarkerReleaseAlwaysSolves_EvenWithinTheNoOpTolerance`).

**§4 and §5 measured together, as the brief's own §5.5 asked.** With Layer A/B's cache warm (§4:
~0.16–0.53 ms per Smith panel, down from ~3–10 ms), the render's contribution to a drag frame is now a
small fraction of the SOLVE side above (9–14 ms) rather than comparable to or larger than it — so the
solve, and specifically the seed-policy asymmetry this section measures, is now the dominant and
VISIBLE cost in a drag frame, confirming §5.5's own prediction ("the asymmetry will be more visible
after §4 than before it, not less") rather than needing a separate render-included re-measurement:
render is close enough to zero now that solve-only numbers above already stand in for total frame time
to within the ~1–3 ms `HarmonicaDragFrameBreakdownTests` measured for the non-Smith panels.

**Not chased further, named rather than silently dropped:** the ~60–70 ms `ReadoutStripView.LastSetItemsMs`
gap from §1.4/§4.6 is unmeasured in this headless environment and is now, by a wide margin, the largest
unaccounted-for piece of the owner's original ~90 ms/11 fps observation — bigger than everything §4 and
§5 together move.

## A grid-point drag was costing the whole tier-A power sweep (brief-harmonicarf-r3b §2, 2026-08-13)

**A gesture that changes no circuit state was costing 46 HB solves.** `HarmonicaViewModel.
DragGridPoint(dragging: true)` routed every mid-drag frame through `RequestFrame`, whose
`OptionsFor(..., dragging: true)` sets `SkipContours = true` — but `SkipContours` only ever skips the
CONTOUR GRID build; `HarmonicaSolver.Solve` runs tier A's whole `PinSearch.Sweep` ladder
unconditionally, every frame, at terminations a grid-point drag never touches at all (the dragged Γ
is a sample the grid sweeps LATER, not a termination anything solves against). R-h9r2-4 chose the
"splice the moved point into the carried `GridPoints` list, display only" shape precisely so this
gesture would be cheap, then routed it through the full frame pump anyway.

**Fix:** a mid-drag grid-point frame no longer calls `RequestFrame`/touches `_pool` at all. It splices
the moved Γ into the CURRENTLY PUBLISHED `Frame.SmithPower`/`SmithEfficiency` grid-point lists
directly (the existing `ApplyGridPointOverride` helper, already built for exactly this splice) and
sets `Frame` — an `[ObservableProperty]`, so the assignment itself raises `RedrawRequested` via
`OnFrameChanged`. Same no-re-solve shape as `SetMarkerVswr`/`ToggleMarkerVswrEnabled`, applied to a
grid point instead of a marker overlay. `CustomGrid` stays untouched mid-drag (unchanged from
before — only committed on release), and release (`dragging: false`) is unchanged: it still commits
into `CustomGrid` and requests a real frame with `ReuseUnchangedGridPoints = true`.

**Gated on a counter** (`HarmonicaGridPointDragTests.
MidDragGridPointFrame_CostsZeroHbSolves_GatedOnACounterNotAStopwatch`): five simulated pointer-move
events during a drag leave `SolvePool.StartedCount` and `HarmonicaSolver.LastSolveCount` unchanged,
while the glyph's own Γ visibly tracks the last move — and release still submits a real solve. All
6563 `Ui.Tests` pass.

## macOS native menu: docked focus and the crash (brief-harmonicarf-r3a, 2026-08-13)

The macOS "menu not shown when docked" bug and the "crashed switching apps / opening Settings" crash
were ONE bug, not two, and R2B's own diagnosis of the crash ("a genuine Avalonia.Native race this
view cannot see into") was wrong — the mechanism is fully knowable from Avalonia 12.0.3's own source
(`src/Avalonia.Native/AvaloniaNativeMenuExporter.cs`, `IAvnMenu.cs`).

**The standing invariant, from here on: on macOS, a window's `NativeMenu` instance is chosen ONCE
and never changes for that window's whole lifetime. To change what the menu bar shows, mutate that
instance's `Items` — never call `NativeMenu.SetMenu` on a window a second time with a different
instance.** Four facts pin this down:

1. **One `AvaloniaNativeMenuExporter` per `TopLevel`, created once, never torn down.** Every
   `NativeMenu.SetMenu(window, x)` for that window routes to the SAME exporter, for the window's
   whole life.
2. **The exporter binds to the FIRST `NativeMenu` instance it is ever given, permanently.**
   `__MicroComIAvnMenuProxy.Initialize` is called only on that first bind. Its own `Update`:
   ```csharp
   internal void Update(IAvaloniaNativeFactory factory, NativeMenu menu)
   {
       if (menu != ManagedMenu)
           throw new ArgumentException("The menu being updated does not match.", nameof(menu));
   ```
   A second, different instance handed to the same window throws — synchronously, on the calling
   thread, out of `NativeMenu.SetMenu` itself.
3. **`SetMenu(window, null)` is not a clear — it substitutes a brand-new empty `NativeMenu`**
   (`_menu = menu ?? new NativeMenu();`), so calling it on a window that already holds a real menu
   ALSO throws, for the same reason (the throwaway empty menu is not `ManagedMenu` either) — R2B's
   own "defensive clear" was therefore a poisoning step, not a safety step, and is now gone.
4. **`_menu` is assigned BEFORE the throw, and a later dispatcher-queued reset re-reads it.** Any
   `NativeMenuItem` added to or removed from the exporter's *original* menu calls `QueueReset()` →
   `Dispatcher.UIThread.Post(DoLayoutReset, ...)`. That queued call re-runs `SetMenu` with the now
   *poisoned* `_menu` and throws again — on the dispatcher, where no call-site `try`/`catch` can
   reach it. This is the exact owner-reported crash: a menu-item mutation (rebuilding the Window menu
   on `Activated`, or opening Settings) some time AFTER the poisoning attach is what actually brings
   the process down, which is why the failure looked delayed/intermittent rather than immediate.

**The fix (`HarmonicaMenuView.RecomputeAttachment`, split into `AttachToWindowOutright` +
inject/withdraw):** a torn-off document or the standalone binary still owns its hosting window
outright via `NativeMenu.SetMenu` (that window has never had a menu, so this is always the FIRST
bind and always succeeds). A **docked** document never calls `NativeMenu.SetMenu` on the
`WorkspaceWindow` at all — that window's exporter is already permanently bound to circuitRF's own
app-menu instance (`WorkspaceWindow.AttachNativeMenuAtApplicationScope`, at startup). Instead, on
docked focus, the document's own top-level items (Markers / Display / Grid — not File/Edit/Help,
which circuitRF's bar already shows) are appended to that SAME instance's `Items`
(`HarmonicaAppMenuInjector.Inject`), and removed again — by reference, never by header match — on
blur (`.Withdraw`).

**The item-`Parent` validator forces a THIRD rendering, not a copy.** `NativeMenu`'s list validator
throws `InvalidOperationException` for any item that already has a `Parent` — so the injected items
must be freshly-built `NativeMenuItem`s from `HarmonicaMenuViewModel`'s own collections/commands
(`HarmonicaAppMenuInjector`), never `_ownMenu`'s own children. This mirrors the view's existing
"TWO SURFACES, HAND-MIRRORED" shape (the in-window `Menu` and the standalone `NativeMenu` are already
two independent renderings of one source) — the injected set is simply a third.

**`WorkspaceViewModel.TryWireWindowFocusTracking`'s Harmonica/WBond exclusion already closed the
§2.3 ordering trap**, before this brief: `AttachSharedNativeMenuIfMacOS` is gated on
`doc is not HarmonicaDocument and not WBondDocument`, so a torn-off harmonicaRF/wBond window can
never receive circuitRF's shared app-menu instance regardless of activation order (each owns its own
per-window attach). This makes the invariant type-based rather than order-dependent — verified, and
now pinned by a dedicated test, rather than left as "today's ordering happens to favour it."

**`Dispatcher.UIThread.UnhandledException` (`App.WireNativeMenuDispatcherBackstop`) is a floor, not
the fix** — it exists only because a queued `DoLayoutReset` throw is, structurally, unreachable by
any call-site `try`/`catch`. It matches ONLY `ArgumentException("...menu being updated does not
match...")` whose stack contains `Avalonia.Native`; a blanket handler was rejected on purpose.
