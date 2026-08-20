---
title: harmonicaRF
slug: reference/harmonicarf.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > harmonicaRF
lede: Interactive harmonic load-pull on a single device, at the speed of a mouse drag.
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#what">What it is</a></li>
<li><a href="#active-loadpull">Active load-pull, before you touch the bench</a></li>
<li><a href="#circuit">The circuit it solves</a></li>
<li><a href="#dut">Supported DUT models</a></li>
<li><a href="#ui">The interface</a></li>
<li><a href="#interaction">Interaction: dragging and editing</a></li>
<li><a href="#presets">Preset terminations — B, J, J*, F and F⁻¹</a></li>
<li><a href="#menus">The menus</a></li>
<li><a href="#charm">The .charm document, and getting results out</a></li>
</ol>
</nav>

## What it is {#what}

harmonicaRF answers one question, continuously, while your mouse is moving:

> *If I terminate this device like **this** at f₀, 2f₀, 3f₀ … on both the source and the load side,
> what is the current generator actually doing, and what does it cost me in power and efficiency?*

Every other tool in this space is a post-process: set up a sweep, wait, read a static contour.
harmonicaRF runs harmonic balance **during the drag**, so the relationship between a termination and the
loadline is felt rather than inferred. The second thing that makes it different is that **the intrinsic
plane is the primary view** — wherever it is meaningful you are shown the voltage and current *of the
current generator*, not of the package terminals, because that is the plane a designer reasons in when
inventing a termination strategy.

**What it is not**, stated plainly so you reach for the right tool:

- **Not a circuit simulator.** There is exactly one DUT, two termination planes and an optional linear
  embedding. Anything more structured is a circuitRF schematic.
- **Not a replacement for the batch [load-pull analyses](simulations.html#loadpull).** Those stay the
  high-point-count, reference-grade path. harmonicaRF trades grid density for interactivity.
- **Not two-tone.** Single tone, single frequency.

It opens from circuitRF's **Tools** menu, works with or without a workspace open, and also ships as a
standalone binary for people who want the instrument and nothing else.

## Active load-pull, before you touch the bench {#active-loadpull}

Used deliberately, harmonicaRF is an **on-wafer active load-pull simulator**.

An active load-pull bench synthesises the reflection it presents by injecting a signal back at the DUT,
which is what makes harmonic terminations reachable at all on-wafer. It is also a bench on which it is
entirely possible to present a device a termination that destroys it — or to walk probes across a wafer
for hours collecting points that were never going to be interesting.

The practical argument for exploring first, in a tool that runs at frame rate:

- **Decide which terminations are worth measuring** before committing measurement time to them. A drag
  across the Γ plane costs seconds; the same exploration on a bench costs a session.
- **See where the loadline is going before the device does.** The loadline plot is live and it is drawn
  against the device's own DCIV family, so a termination that drives the device into knee or breakdown
  territory is visible as geometry, not as smoke.
- **Bracket the harmonic terminations first.** Which of 2f₀ and 3f₀ actually moves efficiency on *this*
  device is a question you can answer in a minute here and then verify at three points on the bench,
  instead of gridding blind.
- **The device and the probes survive the exploration**, because none of it happened on the wafer.

None of this replaces the measurement. It decides what the measurement should be.

## The circuit it solves {#circuit}

One signal path, fixed:

```
                       ┌─────────── embedding stack ──────────┐
  SourceTuner ─ s2p_in ─┐                                   ┌─ s2p_out ─ LoadTuner
                        ├─ s4p / s6p (1,2 outer · 3,4/5,6 DUT) ┤
                        └─ lumped package ─ DUT ─ package ────┘
```

Every element of the embedding is optional and any combination is legal. **The cascade order is fixed,
outside in: s2p → s4p/s6p → lumped → DUT.**

- **`s2p_in` / `s2p_out`** — a Touchstone two-port at either external port. Port 1 faces the tuner,
  port 2 faces inward.
- **`s4p` / `s6p`** — one block embedding the whole DUT. Ports 1,2 face outward; ports 3,4 (or 3,4,5,6
  for a 3-port DUT) face the DUT.
- **The lumped package** — a fixed-topology extrinsic network with editable values: series `Rg, Lg` /
  `Rd, Ld` / `Rs, Ls`, and shunt `Cpg, Cpd, Cgd_ext`. Any value may be zero. It is deliberately not an
  arbitrary sub-network — an arbitrary network is what circuitRF is for.

**Terminations are per harmonic band.** A marker on a Smith chart *is* a band's termination: `S1`/`L1`
are the source and load fundamentals and are always present; `S2`/`L2`, `S3`/`L3` … are added and
removed from the Markers menu. **A band with no marker is terminated in a near-short (1e-6 Ω)**, which
is worth knowing before you conclude that the third harmonic does nothing on your device. DC is not a
marker: bias is an ideal choke and DC block, so band 0 is a hard short to the supply.

**Markers are linked across charts** — a marker belongs to the circuit, not to a plot, so moving `L2` on
the power chart moves it on the efficiency chart in the same frame.

## Supported DUT models {#dut}

**The source is always grounded.** A two-port device is used as it is; a three-port device has its source
port grounded — so there are only ever two termination planes and two marker families. (The source is
grounded at the *package* plane, which is why a shared source lead `Rs`/`Ls` shows up in the source-side
intrinsic impedance.)

| DUT | What it is |
|---|---|
| **Native FET** | Any of the five large-signal models — [Angelov, Curtice, Curtice cubic, Materka, Statz](components.html#fets). Choose the law, then edit the model's own parameters in the strip. |
| **SDD** | Drain-current (and gate-current) equations you type, in the standard [expression language](expressions.html). Two-port (`_v1` = Vgs, `_v2` = Vds) or three-port, which adds the source terminal against ground as `_v3`. Optional `Cgs`, `Cdg`, `Cds` across the device's own terminals, each either linear or a polynomial C(V). |
| **External model** | A compiled Verilog-A `.osdi`, or a part from a vendor kit through the device worker — see [PDK integration](pdk-integration.html). You must name which internal node is the intrinsic gate and drain and which pin is the source: nothing can guess that, so until it is answered the intrinsic glyphs and the loadline stay **empty** rather than plausibly wrong. |
| **Diode** | A two-terminal built-in, for teaching and for the degenerate cases. |

Load one with **File ▸ Set DUT…**, or drag a part in from the circuitRF Library palette when a workspace
is open. **Refresh DUT** re-reads a model that changed on disk.

The DUT is embedded in the same `s2p`/`s4p`/`s6p`/lumped stack described above, so a bare die and a
packaged part are the same document with a different embedding.

## The interface {#ui}

{{ui: harmonica-instrument}}

Four panels and a strip:

- **Two Smith charts, side by side** — power on the left, efficiency on the right, both as **unfilled
  iso-lines** over the Γ plane. Each chart has its own plane (load or source) and its own harmonic, so
  you can put 2f₀ next to f₀ and watch both.
  - **Iso-lines fade by level, not by position.** The highest-level contour — the one bounding the
    best region — is fully opaque wherever it lands, and lower levels fade out. The top contour is the
    answer; the rest is context.
  - **Iso-line labels are off by default.** A dense unfilled contour set reads better without them;
    turn them on from the Display menu when you need the numbers.
  - **Grid points are visible and draggable.** A point that could not be solved (unreachable at the
    requested compression) is drawn hollow — a hole, not a missing point.
  - The efficiency chart plots **DE or PAE**, per chart, drain efficiency by default.
- **The loadline plot** — the device's DCIV family with the time-domain loadline superimposed, live
  during a drag. **One plane toggle moves both curves together** between intrinsic and extrinsic, so the
  two can never be misleadingly superimposed, and a persistent indicator on the panel says which plane
  you are looking at.
- **The power sweep** — gain on the left axis, efficiency on the right, against output power. The
  **X-axis unit cycles when you click it**: Pout (dBm) → Pout (W) → available Pin (dBm) → available Pin
  (W). It carries the **operating-point cursor**, the drive level at which the intrinsic glyphs, the
  loadline and the readouts are evaluated — drag it, or snap it to compression.
- **The readout strip**, below.

{{ui: harmonica-readout-strip}}

The strip is deliberately dense: small text, no section titles, no decoration. Every element has a
tooltip, and **all of it is selectable text**, so any readout can be copied straight out.

- **Left**: the settings — bias (`Vgs`, or `Idq`, which solves `Vgs` for you), `Vds`, frequency, HB
  order, the compression level, `Z0`, and **every parameter the loaded model declares**, so periphery or
  finger count appears exactly when the model actually has it.
- **Middle**: the operating point at the cursor — Pout, gain, DE, PAE, Pdc, `Zin`, AM/PM — and the
  **MXP** and **MXE** summaries from the grid, each carrying **its own `Zin`**, because `Zin` moves with
  the load on a non-unilateral device and one number for both would be a lie.
- **Right**: each marker's Γ and Z, and the **Fourier coefficients of Ids and Vds** per harmonic, in
  both planes. Idq and the *dynamic mean* Id under drive are both shown: they differ, and the difference
  is worth seeing.

## Interaction: dragging and editing {#interaction}

<div class="callout note">
<span class="label">The two gestures that matter</span>
<p><b>Markers are dragged.</b> Click a marker on either Smith chart and drag it: the terminations, the
contours behind it, the loadline and every readout update while your hand is moving.</p>
<p><b>Configuration is double-clicked.</b> Double-click any settings value in the readout strip to edit
it in place. Enter commits, Escape reverts.</p>
</div>

More precisely:

- **Dragging a marker** re-solves the circuit each frame. The contour layer is carried forward frozen
  during the drag and re-rastered when you let go, so the frame rate stays in your hand rather than in
  the grid.
- **Dragging a grid point** moves that Γ sample. The grid is yours to shape: presets are 3 × 12, 5 × 12
  and 7 × 16 rings × spokes, and a whole grid can be imported or exported as a `.gam` file — which is
  also the route for a hand-authored or a measured grid.
- **Dragging an intrinsic glyph** runs the **inverse solve**: you state what you want the current
  generator to see and the tool works out the terminal termination that produces it. It is available
  only when the inversion is exact — an SDD DUT, linear capacitances, no `Cdg` feedback, and a package
  that does not couple the input and output loops. When it is not available the tool says which of those
  conditions failed instead of silently approximating.
- **Right-click a marker** for its context menu: set it as an impedance (R + jX) or as a gamma (polar or
  rectangular), toggle normalisation, snap it to MXP or MXE, copy Γ, copy Z, or remove it.
- **Double-click** in the readout strip edits a setting in place — bias, Vds, frequency, HB order,
  compression, Z0, and any model parameter. A nonlinear capacitance row is edited from its own
  right-click menu instead, because it carries a polynomial rather than a value.

## Preset terminations — B, J, J* and F, F⁻¹ {#presets}

**Markers ▸ Preset Terminations** writes a whole class of load terminations in one gesture:

| Preset | Shortcut | What it writes at the intrinsic plane |
|---|---|---|
| **Class B** | `Ctrl+B` / `⌘B` | f₀ at `Z0`; every harmonic above it a near-short. |
| **Class J** | `Ctrl+J` / `⌘J` | f₀ at `Z0·(1 − j0.5)`; 2f₀ at `Z0·j·3π·0.5/8` (reactive); 3f₀ and above a near-short. |
| **Class J\*** | `Ctrl+Shift+J` / `⌘⇧J` | The complex conjugate of Class J, band by band — same magnitudes, opposite reactance sign. |
| **Class F** | `Ctrl+F` / `⌘F` | f₀ at `2·Z0/√3`; **even** harmonics shorted, **odd** harmonics open. |
| **Class F⁻¹** | `Ctrl+Shift+F` / `⌘⇧F` | f₀ at `(√2/2)·Z0 / (½ − 8/9π²)`; **even** harmonics open, **odd** harmonics shorted — the inverse arrangement. |

Three things a user needs to know about them:

1. **They are intrinsic targets, and `Z0` means R_opt.** The presets are written against the document's
   own `Z0`, which is what makes them meaningful — set `Z0` to the device's optimum resistance
   (**Display ▸ Set Z0…**) before applying one, or you are asking for a Class-F termination around the
   wrong load. The tool transforms them out to the terminal plane for you.
2. **A "short" is `Z0/100` and an "open" is `Z0·100`,** not a mathematical zero and infinity. At
   Z0 = 50 Ω that is 0.5 Ω and 5 kΩ — |Γ| = 0.980 either way, which is a short and an open for every
   practical purpose, while leaving the solver a well-scaled problem. An eleven-orders-of-magnitude
   termination makes the contour raster around that band degenerate; this does not.
3. **A preset writes only the bands you have markers for.** Add `L2` and `L3` first if you want a
   Class-F arrangement to have anywhere to put its harmonics. Bands with no marker stay at the
   unmarked near-short.

When to reach for which: **B** is the reference case and the sanity check. **J / J\*** buy you Class-B
efficiency over a wider bandwidth by trading fundamental reactance against a reactive second harmonic —
useful when a real matching network cannot hold a short at 2f₀ anyway. **F** and **F⁻¹** are the
harmonic-tuned high-efficiency arrangements; which of the two suits a device depends on whether its
current or its voltage waveform is the one you can shape, and comparing them here — two keystrokes
apart — is exactly the comparison this tool exists to make cheap.

The termination values come from:

> Sharma, T. (2018). *Modelling and Design Methodology of Higher-Efficiency Harmonic Tuned Power
> Amplifiers for 5G Applications* (Doctoral thesis, University of Calgary).
> <https://prism.ucalgary.ca/handle/1880/106695>

## The menus {#menus}

harmonicaRF documents carry their own menu set — notably **no Simulate menu**, because it is always
simulating. On macOS these appear in the system menu bar; elsewhere they are in the window.

| Menu | What is in it |
|---|---|
| **File** | New · Open `.charm` · Save · Save As · **Set DUT…** · Refresh DUT · Import/Export `.gam` · Export Data · **Export Testbench…** · Close |
| **Edit** | Undo · Redo · Settings… |
| **Markers** | Source Bands · Load Bands (add or remove a band's marker) · **Preset Terminations** · Add Load Marker (`Ctrl+A`) · Add Source Marker |
| **Display** | Contour plane (load/source) · contour harmonic · efficiency metric (DE/PAE) · loadline plane (intrinsic/extrinsic) · contour levels (5/10/20) · iso-line labels · grid points · Power Sweep… · **Set Z0…** |
| **Grid** | Grid preset (3 × 12, 5 × 12, 7 × 16) · Reset grid · Import/Export `.gam` |
| **Help** | This page. |

## The .charm document, and getting results out {#charm}

A harmonicaRF document saves as **[`.charm`](file-formats.html)** — the DUT, the embedding, the bias, the
terminations and markers, the grid, the settings, and your panel layout. A DUT can be **embedded** in the
document or **referenced**, so a `.charm` is either self-contained or tracks a model that keeps changing,
whichever you need.

Three ways out:

- **Export Data** publishes harmonicaRF's own `DataSet` — every quantity it solved, in the same form
  every circuitRF analysis produces, so it opens in the [Data Display](data-display.html) and exports to
  `.npy`/`.mat` like any other run. That is also why **Edit Display**'s trace picker can plot *anything*
  harmonicaRF solved, not a fixed list.
- **Export Testbench…** writes a runnable `.cnl` that reproduces the current state through the ordinary
  HB path, so any finding here is checkable by the reference engine. (It writes a `P1Tone` source and a
  no-tone `PnTone` load rather than a Tuner pair, because those are the components the HB engine gives a
  harmonic-band ruler to.)
- **Copy termination set** puts a `Tuner` pair on the clipboard, ready to paste into a schematic where
  the load-pull engine drives it.

**Edit Display** unlocks the panel layout: add, move, resize and delete plots and readouts, change text
size and alignment, and add a trace of anything in the published `DataSet`. Lock it again and the layout
is yours; it is saved in the `.charm`.
