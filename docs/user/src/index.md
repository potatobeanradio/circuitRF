---
title: Documentation
slug: index.html
doc-kind: Documentation
lede: A lightweight, cross-platform RF circuit simulator — DC, S-parameters, harmonic balance, and loadpull.
---

<div class="card-grid">
<a class="card" href="quick-start/index.html">
<h3>Quick Start →</h3>
<p>For engineers who already know circuit simulators. Run a simulation and see a plot in a couple of
pages. Start here if you have used SPICE or a vendor RF tool.</p>
</a>
<a class="card" href="new-user-guide/index.html">
<h3>New User's Guide →</h3>
<p>Never used a circuit simulator? What a library, a cell and a simulation are — explained from the
ground up, with worked examples.</p>
</a>
<a class="card" href="reference/index.html">
<h3>Reference Guide →</h3>
<p>The complete technical reference. Every analysis, every component, the layout and EM engines, the
file formats and the two bundled tools.</p>
</a>
</div>

{{search: hero}}

## What is circuitRF?

circuitRF studies the frequency response and nonlinear behaviour of RF circuits — from a handful of
components to hierarchical, multi-port designs. It is an **RF simulator, not a SPICE simulator**:
the analyses and the workflow are built around the RF/microwave problem, the file formats are
human-readable, and loadpull is a first-class experiment. Results are stored as NumPy `.npy`
datasets, so they drop straight into Python or MATLAB.

Alongside the circuit engines it carries a planar
[method-of-moments EM solver](reference/mom-engine.html) for layout, a 3D
[bondwire kernel](reference/wbond.html), and
[harmonicaRF](reference/harmonicarf.html) — an interactive harmonic load-pull bench for a single
device.

## Everything in this documentation

Every page below is in one reading order. Start anywhere and follow **Next** at the foot of each
page to reach the end without coming back here.

{{toc: site}}

<div class="callout note">
<span class="label">Alpha</span>
<p>circuitRF is in active development. These docs track the current build and details may shift
between releases. Every figure in them is a vector capture of the running application, regenerated
from the live interface rather than drawn by hand — so a picture here is what the build does.</p>
</div>
