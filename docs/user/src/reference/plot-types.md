---
title: Plot Types
slug: reference/plot-types.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Plot types
lede: The Data Display offers four plot types. Which one you can use depends on whether the data is real (scalar) or complex — that's the single most important rule below.
---

<div class="callout note">
    <span class="label">Scalar vs. complex — the rule</span>
    <p><strong>Rectangular</strong> shows <em>scalar</em> (real) data only. <strong>Smith</strong> and
    <strong>Polar</strong> show <em>complex</em> data only. <strong>Table</strong> is the exception — it shows
    <em>both complex and scalar data together</em> in one grid. So a complex quantity like <code>S(1,1)</code>
    goes on a Smith/Polar/Table; a real quantity like Pout in dBm goes on a Rectangular/Table; and a Table can
    hold both at once.</p>
  </div>

<nav class="toc">
    <h2>On this page</h2>
    <ol>
      <li><a href="#rectangular">Rectangular</a></li>
      <li><a href="#smith">Smith</a></li>
      <li><a href="#polar">Polar</a></li>
      <li><a href="#table">Table</a></li>
      <li><a href="#traces">Adding traces &amp; transforms</a></li>
    </ol>
  </nav>

## Rectangular {#rectangular}

The familiar Cartesian X–Y plot. **Scalar data only.** A complex signal must be reduced to a real
quantity first via a per-trace transform — magnitude in dB (`dB20`/`dB10`), phase in degrees, real
part, imaginary part, magnitude. This is the plot for gain vs. frequency, Pout vs. drive,
efficiency vs. load, harmonic levels, and most swept results.

{{ui: plot-rectangular-data}}

## Smith {#smith}

The Smith chart maps impedance/reflection onto the unit disk. **Complex data only** — it plots the
complex value directly (no transform needed). Use it for reflection coefficients (`S(1,1)`),
input/output impedance, and matching-network design. Loadpull contours are drawn on the Smith
chart's Γ-plane.

{{ui: plot-smith-data}}

## Polar {#polar}

A magnitude-and-angle plot on concentric circles. **Complex data only.** Use it when you care
about a complex quantity's phase and magnitude but don't want the Smith chart's impedance grid —
e.g. viewing a reflection or transmission coefficient as a vector.

{{ui: plot-polar-data}}

## Table {#table}

The numbers in a grid. **Table is special: it shows complex *and* scalar data simultaneously** in
the same table — something no other plot type does. A complex column displays in a chosen complex
format (magnitude/angle, real/imag, or dB/angle), and a scalar column shows its real value, side
by side. It's ideal for reading exact values, building a results summary, and copying data out.
(The loadpull *summary table* is a specialized Table view.)

{{ui: plot-table-data}}

## Adding traces & transforms {#traces}

Each plot holds one or more **traces**, configured in the trace card:

- **Data source** — which run/file the trace reads from. Measured files overlay simulated ones on
  the same axes.

- **Signal** — the quantity: an S-parameter (`S(2,1)`), a node voltage, a branch current, a
  measurement, or a cube slice.

- **Transform** (Rectangular/Table) — how a complex value becomes scalar: `dB20`, `dB10`, `Mag`,
  `Phase`, `Real`, `Imag`. Not needed on Smith/Polar (they take the complex value directly).

- **Complex format** (Table) — magnitude/angle, real/imag, or dB/angle for complex columns.

- **Markers** — drop interactive markers to read values; on a loadpull contour, markers read the
  surface, and the max-power / max-efficiency optima are flagged.

---

<p class="small">See also: <a href="simulations.html">Simulations</a> (what each analysis produces) ·
  <a href="components.html">Components</a>.</p>
