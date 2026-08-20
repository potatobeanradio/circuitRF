---
title: circuitRF Reference Guide
slug: reference/index.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference Guide
lede: The complete technical reference. Plain and direct — look things up here.
---

The Reference Guide is every chapter below. They are in one reading order, and every page carries
**Previous** and **Next** links at its foot, so the whole guide can be read straight through as well
as dipped into.

{{toc: section:Core concepts}}

{{toc: section:Design}}

{{toc: section:Simulate}}

{{toc: section:Layout & EM}}

{{toc: section:Tools}}

## Where to start

<div class="callout note">
<span class="label">If you are new to circuitRF</span>
<p>Read <a href="units.html">Units</a> and <a href="grid.html">Grid &amp; Connectivity</a> first.
Between them they cover the two conventions that are circuitRF's own rather than the industry's —
how a coordinate is stored versus displayed, and what actually decides that two things are
connected. Almost every early surprise is one of those two.</p>
</div>

<div class="callout note">
<span class="label">Three chapters that are easy to miss</span>
<p><b><a href="match.html">The Match Component</a></b> synthesises a bandpass matching network that
absorbs both terminations — closed-form, no optimiser. <b><a href="harmonicarf.html">harmonicaRF</a></b>
runs harmonic load-pull on one device while you drag a marker. <b><a href="wbond.html">wBond</a></b>
models bondwire arrays as geometry rather than as a number you looked up. Each is a tool in its own
right rather than a component with a dialog.</p>
</div>

<!-- =====================================================================
     FOR DEVELOPERS — in-app Help buttons deep-link into these pages.
     Anchor scheme (a stable contract, checked by the generator and by
     tests/Ui.Tests/DocsFactoryTests):
       Component Parameter Editor "Help"  ->  reference/components.html#<symbolkind-lowercase>
                                              e.g. components.html#resistor , components.html#sdd
                                              (the #sdd section links on to the full sdd.html chapter)
       Analyses "Help"                    ->  reference/simulations.html#<analysis>
                                              e.g. simulations.html#harmonic-balance , #s-parameters
       Plot-type Help                     ->  reference/plot-types.html#<type>
                                              (#rectangular #smith #polar #table)
       Nonlinear-C / C-V Editor Help      ->  reference/nonlinear-capacitor.html
       EM Setup panel Help                ->  reference/em-setup.html
     The list the application can emit lives in src/Ui/Diagnostics/DocAnchors.cs, and the generator
     fails if any of it does not resolve. Do not hand-maintain the correspondence.
     ===================================================================== -->
