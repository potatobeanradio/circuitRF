---
title: The Filter Designer
slug: reference/filter-designer.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Filter Designer
lede: An LC ladder from a filter specification, drawn on the Data Display's own plots.
keywords: filter, ladder, bandstop, Butterworth, Chebyshev, Gaussian, preferred values
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#what">What it is</a></li>
<li><a href="#spec">The specification</a></li>
<li><a href="#mask">The plot</a></li>
<li><a href="#values">Exact, standard, library</a></li>
<li><a href="#place">Where the result goes</a></li>
<li><a href="#headless">The same thing from the command line</a></li>
</ol>
</nav>

## What it is {#what}

**Filter Designer** (Tools ▸ Filter Designer) synthesises a doubly terminated LC ladder from a
prototype and shows the response on the same plots a Data Display uses. Open it on a placed
[Filter](components.html#filter) from that component's properties, and **Apply to Filter** writes
the specification back onto the ideal block.

The ideal Filter and the ladder answer different questions. The block is a rational scattering
matrix, so its two port impedances are independent and either may be complex. A ladder cannot do
that: its load is the ratio the family produces (1 for Butterworth, a particular ratio for an
even-order Chebyshev). When the two disagree, the window says what the ladder's load is and Apply
keeps the impedances you typed on the block.

It is direct synthesis. There is no optimiser in the window. A mask the snapped ladder misses is
the existing optimizer's problem, on an E-series or on a part.

## The specification {#spec}

| Control | What it sets |
|---|---|
| Source, Load | The two resistances, in ohms. |
| First element | Series or parallel. Parallel starts the ladder on a shunt arm. |
| Order | The **prototype** order. A bandpass or bandstop replaces each element by a resonator, so an all-pole order 3 is six elements and a tank becomes four. |
| Response | Above the plots. Maximally Flat, Chebyshev, Inverse Chebyshev, Elliptic, Bessel-Thomson, Gaussian. |
| Form | Lowpass, highpass, bandpass, bandstop. |
| Ap, As | Passband and stopband figures, in dB. Chebyshev and elliptic read Ap as the ripple. Inverse Chebyshev and elliptic read As as the stopband floor. The others ignore them for the synthesis and the mask still draws them. |
| Edges | Two for lowpass and highpass (Fp and Fs), four for bandpass (Fs1, Fp1, Fp2, Fs2) and bandstop (Fp1, Fs1, Fs2, Fp2). The unit sits on the row. |

The cutoff the ladder is built at is Fp for a lowpass or highpass, the passband edges for a
bandpass, and the stopband edges for a bandstop. Fs on a lowpass or highpass is the mask: where
you want the stopband floor, not a second cutoff.

Gaussian is scaled so the cutoff is the −3.01 dB point at every order. Bessel is chosen for its
group delay, which is the second plot.

Inverse Chebyshev and elliptic of an odd order are ladders of tanks: a finite transmission zero is
a parallel LC in the through path or a series LC to ground. An even order of either family keeps a
finite floor at infinite frequency, and a ladder cannot, so the window keeps the ideal curve and
says so.

## The plot {#mask}

The two plots sit side by side across the top, with the response and **dB / division** on the row
above them. Both are Data Display plots. The magnitude plot carries the ideal |S21|, the ladder's
|S21|, and the ladder's |S11| on the right axis. The phase plot is the ladder's phase and its group
delay, in the same nanoseconds the rest of the application uses. **dB / division** is the
magnitude plot's vertical grid. The ladder is drawn underneath.

**Design** and **Redraw** both rebuild the ladder and the curves. The synthesis is closed form, so
there is no separate long step to wait for.

Design information reports the order, the ladder's load, and the minimum insertion loss in the
passband — 0 for a lossless ladder that touches 0 dB, and the passband's worst-case best
transmission after a snap.

## Exact, standard, library {#values}

The realization table has the theoretical value and the value that is written.

| Choice | What each element becomes |
|---|---|
| Exact | The continued-fraction value, in henries or farads. |
| E6, E12, E24, E96 | The nearest IEC 60063 preferred number, nearest by ratio — the same snap the Smith chart and the optimizer use. |
| A library series | **Load library series** indexes the workspace's vendor libraries. Pick one inductor series and one capacitor series. Each element becomes the nearest stated value in that series, and the row names the part. |

A workspace with no vendor library leaves the series lists empty and says so. It does not fall
back to an E-series. The power-integrity part library is a different drawer and is not read here.

## Where the result goes {#place}

**Apply to Filter**, when the window was opened on a placed Filter, writes Response, Form, Order,
the cutoff, Ap, As and the two impedances onto that instance. The snap is not applied to the
block, because the block has no elements.

**Save netlist…** writes the ladder as a `.cnl`: two terminations and the inductors and capacitors
between them, at the values the table shows. That file is what `sparam` simulates, and it is the
same netlist the plot was drawn from.

## The same thing from the command line {#headless}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf filter --form bandstop --response Butterworth --order 3 --f1 1GHz --f2 2GHz --series e24 -o filter.cnl</code></pre>

The verb calls the same synthesis the window calls. The options are in [CLI › filter](cli.html#filter).
