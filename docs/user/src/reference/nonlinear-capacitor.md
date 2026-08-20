---
title: The Nonlinear Capacitor & the C–V Editor
slug: reference/nonlinear-capacitor.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Nonlinear Capacitor
lede: A capacitor whose value depends on the voltage across it — a varactor/varicap, or the junction capacitance of a device. circuitRF models C(V) as a polynomial (Taylor) series, and the C–V Editor fits that polynomial to a curve for you.
---

<nav class="toc">
    <h2>On this page</h2>
    <ol>
      <li><a href="#model">The Taylor-series C(V) model</a></li>
      <li><a href="#params">Coefficients (C0, C1, …)</a></li>
      <li><a href="#editor">The C–V Editor</a></li>
      <li><a href="#apply">The Apply step (important)</a></li>
    </ol>
  </nav>

## The Taylor-series C(V) model {#model}

<figure class="symbol"><span class="frame">
    <img class="sym-light" src="../assets/symbols/nonlinear-c.svg" alt="Nonlinear capacitor symbol">
    <img class="sym-dark"  src="../assets/symbols/nonlinear-c-dark.svg" alt="">
  </span><figcaption>Nonlinear capacitor (NonlinearC)</figcaption></figure>

The capacitance is a **power series in the port voltage V** — a Taylor-series expansion about V =
0:

```text
C(V) = C0 + C1·V + C2·V² + C3·V³ + …
```

The coefficients `C0, C1, C2, …` are the model's parameters. `C0` is the zero-bias capacitance;
the higher terms bend the curve. A polynomial is used because it's smooth and differentiable
everywhere, which the harmonic-balance solver needs (it works with the charge, the integral of
C(V)·dV, and its derivatives). You can enter the coefficients by hand, or — far easier — fit them
to a measured or modeled C–V curve with the C–V Editor.

## Coefficients (C0, C1, …) {#params}

<table class="param-table"><thead><tr><th>Name</th><th>Default</th><th>Unit</th><th>Meaning</th></tr></thead>
    <tbody>
      <tr><td>C0</td><td>1</td><td>pF</td><td>Constant term — the zero-bias capacitance (shown).</td></tr>
      <tr><td>C1, C2, C3, …</td><td>(none)</td><td>—</td><td>Higher-order coefficients. Add them by hand, or let the C–V Editor generate them.</td></tr>
    </tbody>
    <caption>A NonlinearC with only <code>C0</code> behaves as an ordinary linear capacitor.</caption>
  </table>

## The C–V Editor {#editor}

Rather than guessing polynomial coefficients, open the **C–V Editor** (from the NonlinearC's
parameter editor — *Edit C–V…*) and give it the curve you want to model:

<ol class="steps">
    <li>Enter the <strong>C–V table</strong> — pairs of (voltage, capacitance) points describing the device's
      behavior across the bias range of interest.</li>
    <li>Choose a <strong>fit order</strong> — the highest polynomial degree to use (higher order follows a
      wigglier curve but can overfit; pick the lowest order that tracks the data well).</li>
    <li>Review the <strong>preview</strong> — the editor overlays the fitted polynomial on your points so you
      can see the quality of the fit.</li>
  </ol>

{{ui: cv-editor}}

## The Apply step (important) {#apply}

<div class="callout warn">
    <span class="label">Press Apply to generate the coefficients</span>
    <p>Editing the C–V table or the fit order does <strong>not</strong> change the component on its own. You
    must press <strong>Apply</strong> — that's the step that <em>fits the polynomial and writes the
    <code>C0, C1, C2, …</code> coefficients back onto the component</em>. Until you Apply, the component still
    carries its previous coefficients. So the workflow is always: edit the table / order → check the preview →
    <strong>Apply</strong>. The Apply is a single undoable change.</p>
  </div>

After Apply, the generated coefficients are ordinary parameters on the NonlinearC — visible and
editable in the parameter editor, saved with the schematic, and used by the simulator. Re-open the
C–V Editor any time to refit.

---

<p class="small">See also: <a href="components.html#nonlinearc">Components › NonlinearC</a> ·
  <a href="components.html#sdd">SDD</a> (equation-defined nonlinear devices) ·
  <a href="simulations.html#harmonic-balance">Harmonic Balance</a>.</p>
