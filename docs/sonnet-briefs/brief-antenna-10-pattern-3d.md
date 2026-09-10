# Brief — ANT-10: the 3D pattern viewer

**Series:** `brief-antenna-0-overview.md` §4. **Depends on:** ANT-4 (and reads better after ANT-7).
**Blocks:** nothing.

**Its own brief on owner instruction, 2026-09-10.** It is the only item in the series that adds a new
interaction model, and it is the most UI-expensive thing here.

---

## 0. Two things to be clear about before starting

**There is no 3D machinery anywhere in this repository.** `grep -rl "Matrix4x4\|Vector3\b"` over
`src/Render` and `src/Ui` returns nothing. Camera, projection, depth ordering, and a rotate/zoom
gesture are all new. That is not a reason not to build it — it is a reason to scope it as a new
capability rather than as another plot type's worth of work.

**It is the least informative item per unit of effort in the series**, and that should be said rather
than discovered. The principal-plane cuts of ANT-7 tell an engineer more about an antenna than a 3D
lobe does. What the 3D view is genuinely better at is (a) seeing that a pattern is *not* what you
assumed — a squint, an unexpected lobe, a mode that is not the one you designed for — and (b) being
the picture that goes in a report. Both are real; neither is a substitute for the cuts.

## 1. What it is

A surface r(θ, φ) = the pattern in dB above a floor, drawn over the upper hemisphere, coloured by the
same value, rotatable.

Concretely: triangulate the (θ, φ) grid ANT-4 already produces, transform, sort by depth, fill. A
painter's algorithm over a convex-ish star-shaped surface is adequate and is a few hundred lines; a
z-buffer is not needed and a GPU is certainly not.

**Everything is drawn with Skia, in `src/Render`, below the firewall.** That is not incidental: it is
what lets `Cli render` and `Cli plot` produce the same picture headlessly, which is the rule
`src/Render` was created to enforce (RND-1) and the reason the CLI's `render` verb owns no rendering
of its own.

## 2. M1 — where it lives

**Recommendation: a new `PlotType`**, so it inherits the plot container, the trace resolution, the
export path, the theme and the label strip, rather than becoming a second document kind with its own
copy of all of them.

**The risk to check first, and to report before building on it:** `PlotType` today means *2D axes*
throughout — `PlotTypeExtensions.IsRect`/`IsComplex`, `AxesRenderer`, `PlotCanvasGeometry`,
`PlotLabelStrips`, the readouts, the exporter. A fourth kind that has no x axis, no y axis and no
`Window` in the existing sense may fit cleanly or may require an `IsPlanar`-style predicate threaded
through a dozen call sites.

**Spend the first hour finding out which, and report it.** If it is the second, a separate document
kind is the better answer and this brief should say so rather than force the fit. Do not decide from
this brief; decide from the code.

## 3. M2 — the view

- **Rotate** (drag), **zoom** (wheel), and a **reset**. Nothing else.
- **Named standard views** — broadside, and the two principal planes edge-on — because "which way am I
  looking" is the question a 3D picture always raises, and a button answers it better than a gesture.
- **Axes drawn in the scene**: x, y, z with the layout's own orientation, so the lobe can be related to
  the artwork. A pattern with no axes is a shape.
- **The ground plane drawn as a disc at θ = 90°**, which is simultaneously the horizon and the visible
  statement that nothing is modelled below it (see §4).
- **No animation, no lighting model, no perspective unless it is measurably clearer than orthographic.**
  Orthographic is easier to read for a pattern and easier to get right.

## 4. M3 — the same statement ANT-7 makes, in 3D

ANT-4's θ axis stops at 90°, so the surface is a hemisphere and there is nothing below it. In 2D that
reads as a half-disc and needs a note; **in 3D a hemisphere floating above a plane reads as a
complete, very good antenna** unless the view says otherwise.

- The ground disc at θ = 90° is the visual half.
- The note is the other half, and — exactly as in ANT-7 — **it is built from the cube's own axis
  range, not written as a constant**, so ANT-11 extending θ to 180° changes it automatically and the
  surface simply closes underneath.

## 5. M4 — colour

Reuse whatever colour-ramp decision the dB map in ANT-8 and the contour renderer already make. Do not
introduce a third ramp.

- The scale must be **shown with its floor and its reference**, as everywhere else in this series.
- **Both themes**, like every other plot.

## 6. Gates

- The `PlotType`-vs-new-document question of §2, **answered from the code and reported**, before the
  surface is built.
- A **known analytic pattern** renders correctly — a half-wave dipole's doughnut and an isotropic
  hemisphere are both checkable by eye and by a rendered-geometry assertion (peak direction, null
  directions, symmetry). Assert the geometry, not the pixels.
- Depth ordering is correct at a rotation that puts a lobe behind the origin — the case a painter's
  algorithm can get wrong.
- **The same picture from `Cli render`/`plot` as from the app**, byte for byte, which is the standing
  gate for anything drawn in `src/Render` and the whole reason it lives there.
- The hemisphere note is derived from the axis range (hand it a 180° cube and check the surface
  closes).
- Both themes; phone-width is not applicable, but the plot must survive a small pane.
- Frame cost at a realistic grid (1° × 1° is 16,200 triangles) **measured once and reported** — no new
  timing test, per the standing rule. If it is not comfortably interactive, decimate the grid for
  interaction and draw the full one on release, and say so.
- `RESOLVED.md` write-up wherever it lands.

## 7. Must NOT

- **Do not pull in a 3D library or a native dependency.** Cross-platform risk, and adding a native
  dependency is an ask-first item in the root `CLAUDE.md`. Skia and `System.Numerics` are enough.
- **Do not put it above the firewall.** It must draw headlessly.
- **Do not introduce a third colour ramp.**
- **Do not add lighting or perspective for looks.**
- **Do not let it become the default pattern view.** The cuts are.
- **Do not force a `PlotType` fit if §2 says otherwise.**

## 8. Reading order

`src/Render/DataDisplay/Models/Plot.cs` (`PlotType`, and how much of the container assumes 2D) ·
`src/Render/DataDisplay/Renderers/AxesRenderer.cs` (what a plot kind is expected to provide) ·
`src/Render/CLAUDE.md` and `src/Render/RESOLVED.md` (what may and may not live below the firewall, and
the two silent fallbacks that had to be fixed to get there) · `docs/design/cli.md` §14 (`render`, and
why it owns no rendering) · ANT-4 §3-4 (the cube, and the hemisphere).
