# Brief — ANT-7: pattern plots — the dB polar mode, and the cuts

**Series:** `brief-antenna-0-overview.md` §4. **Depends on:** ANT-5. **Blocks:** ANT-12.

Presentation of ANT-4 and ANT-5 in the Data Display. Most of this already works; one thing does not,
and it is the one an antenna engineer will look for first.

---

## 1. What already works, and should be confirmed rather than built

- **Rectangular pattern cuts work today with no changes at all.** `PlotType.Rect`, θ on x, dBi on y.
- **Pinning the extra axes is already expressible.** A trace carries an `AxisSlice[]`
  (`TraceResolve` — an int pins, a Range keeps, missing entries default to pin-index-0), so
  "pin freq, pin φ, sweep θ" needs no new mechanism. The loadpull contour is the existing precedent
  for a trace reaching into a rank > 2 cube.
- **The polar plot's radial axis is already general.** `AxesRenderer.DrawPolarGrid` frames on
  `rMax = max(|Window.Left|, |Window.Right|)` and lays rings by `PolarRings(rMax)`; it is **not**
  locked to |Γ| ≤ 1. A linear-magnitude pattern renders on it today.

The first task is to **verify each of those three on a real `"farfield"` cube and report it**, because
the brief's plan for §2 depends on them being true.

## 2. M1 — a dB radial mode on the polar plot

`PolarRings` places rings at the nearest 1/2/5 decade to `rMax / 5` — a **linear** lattice. An antenna
pattern is read in dB with a floor: rings at 0, −10, −20, −30 dBi, the outer ring at a chosen
reference, and everything below the floor collapsed to the centre rather than clipped away.

This is the only genuinely missing piece and it is contained: a radial *mode* on the polar plot, with

- a **floor** (default −40 dB relative to the outer ring, and the floor is a control, because a
  high-directivity pattern and a near-isotropic one want different ones);
- an **outer-ring reference** that is either the peak (normalised, the usual default) or an absolute
  dBi value — and **the plot must say which**, because a normalised pattern and an absolute one look
  identical and mean very different things;
- rings labelled in dB, at 10 dB steps by default;
- values below the floor drawn **at** the floor, not omitted — a gap in a pattern trace reads as a
  null in the antenna, and a real null and a clipped value must not look the same.

**Do not implement this as a derived complex expression.** It is tempting: a pattern can be pushed
through the existing polar plot as z = 10^((dB − ref)/20)·e^{jθ} and it will draw. But then the rings
are linear and mislabelled, the floor is invisible, and the plot has no idea it is showing a pattern.
The mode is the honest route.

## 3. M2 — the trace spellings

Whatever the Data Display gains, the CLI `plot` verb gains the same, because `plot` builds the
document `render --data` consumes and hands it to the same composer — that is the existing contract
and this must not become a second plotting path.

Two spellings worth having, both shorthand over the general slice mechanism:

- **A cut**: pin freq and φ, sweep θ. This is the E-plane / H-plane plot, and it is what most people
  actually want.
- **A pattern at a frequency**: pin freq, keep θ and φ — the input to a contour or, in ANT-10, to the
  3D surface.

**An integer on a `port` axis is a 1-based port number, not an index.** The same trap the CLI `plot`
verb already records for `i`/`j` on an S cube, and for the same reason: converting it silently draws
the wrong port.

## 4. M3 — what the plot must state

An antenna pattern carries more context than an S-parameter trace and losing it makes the picture
unfalsifiable. On the plot or in its label strip:

- **normalised or absolute**, and the reference value;
- **which cut** (the pinned φ, and its name if ANT-5 derived one);
- **which port is driven**;
- the frequency;
- and — because ANT-4's θ axis stops at 90° — **that the lower hemisphere is not modelled**, not as a
  warning but as a statement of what is drawn. A polar plot occupying a half-disc will otherwise be
  read as a rendering bug.

The last one is the visible half of the staged front-to-back refusal. **When ANT-11 extends θ to 180°,
this note changes with it** — it should be built from the cube's own axis range, not written as a
constant.

## 5. Gates

- Each of §1's three claims, confirmed on a real `"farfield"` cube.
- The dB mode's ring lattice, floor behaviour and label text, on: a high-directivity pattern, a
  near-isotropic one, and one containing a deep null. The null case is the one that distinguishes
  "drawn at the floor" from "clipped".
- Normalised and absolute both render and both say which they are.
- A cut trace and a full-pattern trace resolve from both the Data Display and `Cli plot`, and the
  CLI's output is the same document the Data Display builds.
- The port axis takes a 1-based port number.
- The hemisphere note is derived from the axis, not hard-coded — assert it by handing the renderer a
  cube whose θ axis reaches 180° and checking the note changes.
- **Theme**: both dark and light, like every other plot.
- `RESOLVED.md` write-up in `src/Render/DataDisplay` (or `src/Ui/DataDisplay`, wherever the mode
  lands). No `CLAUDE.md` change unless a default moves.

## 6. Must NOT

- **Do not build a second plotting path.** The CLI and the Data Display share the composer; a pattern
  plot that only one of them can draw is the thing `plot`'s own design exists to prevent.
- **Do not fake the dB mode with a derived complex expression.** §2.
- **Do not clip below the floor.** §2.
- **Do not draw an unlabelled normalised pattern.** A 0 dB peak with no reference is not a result.
- **Do not hard-code the half-disc.** §4.
- **Do not put the 3D surface here.** ANT-10 is its own brief.

## 7. Reading order

`src/Render/DataDisplay/Renderers/AxesRenderer.cs` — `DrawPolarGrid`, `PolarRings`, and the ring
comment around line 238 · `src/Render/DataDisplay/TraceResolve.cs` (`AxisSlice`, the pin/range
semantics around lines 200-280 and 480-510) · `src/Render/DataDisplay/ContourResolve.cs` (the existing
precedent for a trace reaching into a rank > 2 cube) · `docs/design/trace-card.md` ·
`docs/design/cli.md` §16 (`plot`, and the 1-based-port trap) · ANT-4 §3-4 (the cube and its axes).
