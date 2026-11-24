---
title: The LC Filter Designer
slug: reference/lc-filter-designer.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > LC Filter Designer
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

**LC Filter Designer** (Tools ▸ LC Filter Designer) synthesises a doubly terminated LC ladder from a
prototype and shows the response on the same plots a Data Display uses. To open it on a placed
[Filter](components.html#filter), right-click the Filter and choose **Synthesise…**, or press
**Synthesise…** in its properties. Both open the same window, and opening it a second time on the
same Filter brings that window forward. **Apply to Filter** writes the design back onto the ideal
block, and **Place filter** puts the ladder itself on the schematic.

The window's actions are on its toolbar: **Design** (the word), then, as icons with their names in
the tooltips, **Apply to Filter** (only when opened on a Filter), **Place filter**, **Place component**
and **Replace this Filter** (likewise). Place filter carries the ladder glyph this designer has in the
Tools menu; Place component carries the part glyph of a Touchstone block, the one the S-Parameter
Viewer uses. To write the ladder as a netlist, use `circuitrf filter lc … -o filter.cnl`.

{{ui: lc-filter-designer-toolbar}}

The ideal Filter and the ladder answer different questions. The block is a rational scattering
matrix, so its two port impedances are independent and either may be complex. A ladder cannot do
that: its load is the ratio the family produces (1 for Butterworth, a particular ratio for an
even-order Chebyshev). When the two disagree, the window says what the ladder's load is and Apply
keeps the impedances you typed on the block.

It is direct synthesis, and **the window never hands back a filter outside its own mask.** The
order formula only says where to start: the window checks each candidate's response against the
mask, band by band out to a decade past the outer edges, and takes the first that stays inside it.
When the edges as typed miss (a Chebyshev built at a 0.1 dB ripple against an Ap of 3 dB, say), it
moves the cutoff into the slack Ap leaves before trying the next order. With an E-series or a
vendor library it checks the ladder at its snapped values too, so a value a few percent off cannot
push an edge out. A design that still misses, at an order you typed, is drawn with the miss said in
the status line, and cannot be applied or placed.

## The specification {#spec}

| Control | What it sets |
|---|---|
| Source, Load | The two resistances, in ohms. |
| First element | Series or parallel. Parallel starts the ladder on a shunt arm. |
| Order | The **prototype** order. A bandpass or bandstop replaces each element by a resonator, so an all-pole order 3 is six elements and a tank becomes four. |
| Response | In Shape. Maximally Flat, Chebyshev, Inverse Chebyshev, Elliptic, Bessel-Thomson, Gaussian. |
| Type | In Shape, for Elliptic only. **Auto** (a at odd order, c at even order), **a** (classical), **b** (even order, unequal terminations), **c** (even order, equal terminations). |
| Form | Lowpass, highpass, bandpass, bandstop. |
| Order | **Minimum** is the smallest prototype order whose ladder meets the mask (Ap, As and the edges), checked rather than taken from the order formula. **Specify** types N, and a design that misses the mask at that order cannot be applied or placed. Bessel and Gaussian have no magnitude order formula, so they stay on Specify. For an elliptic the minimum is for the type actually built: an even order is type c (or b), whose stopband edge sits a little above type a's, so the window checks it against Fs and steps up when it misses. An even-order inverse Chebyshev, or an elliptic of type a, steps to the next odd (those even orders have no LC ladder). |
| Ap, Ripple, As | Passband attenuation, synthesis ripple and stopband attenuation, in dB. Ripple is enabled only for Chebyshev and elliptic, the two families that have one; the others build at a fixed ripple. Ripple may not exceed Ap. Inverse Chebyshev and elliptic read As as the stopband floor; at a shallow As their ladder can need a negative element, so the window then builds them deeper than As (and says so) at edges that still meet the mask's As. Hover the labels for the same wording. |
| Edges | Two for lowpass and highpass (Fp and Fs), four for bandpass (Fs1, Fp1, Fp2, Fs2) and bandstop (Fp1, Fs1, Fs2, Fp2). Hover a label for what that edge is. Each edge has its own prefix (Hz, kHz, MHz, GHz) beside it; changing one rescales that box only, so the frequency it states is unchanged. The edges start in MHz. The plot's axis reads in the passband edge's prefix. |

The cutoff the ladder is built at is Fp for a lowpass or highpass, the passband edges for a
bandpass, and Fp1 and Fp2 for a bandstop — the passband edges either side of the rejected band.
Inverse Chebyshev is the one exception: its edge is where As is first met, so it is built at Fs
(Fs1 and Fs2 on a band form). The other edges are the mask: where you want the stopband floor, not
a second cutoff. Those are the edges as typed; when the mask leaves room (Ap above the Ripple, a
Butterworth asked for less than 3.01 dB at Fp, or a stopband built deeper than As), the cutoff
moves off them so the response sits inside the mask.

The plot draws that mask by role. Passband edges and the −Ap limit are blue, stopband edges and
the −As limit are red, and each limit spans only its own band, with the side a trace must not
enter lightly tinted. A specification the window refuses (an edge on the wrong side of another)
still draws its mask, so the mistake is visible.

Gaussian is scaled so the cutoff is the −3.01 dB point at every order. Bessel is chosen for its
group delay, which is the second plot.

Inverse Chebyshev and elliptic are ladders of tanks: a finite transmission zero is a parallel LC in
the through path or a series LC to ground. Elliptic ladders reach any practical order — the
reference set runs to order 31 — and every one the window shows has been checked against the
ideal response before it is shown. An even-order elliptic of type a keeps a finite floor at
infinite frequency, which a ladder cannot reach; types **b** and **c** move that zero to infinity
and are ladders at every even order, b with the load ratio its DC mismatch implies and c with
equal terminations. **Auto** builds c at an even order.

A handful of specifications have no ladder with every element positive: a very narrow transition
at high order with a shallow stopband, and an inverse Chebyshev whose stopband floor is shallow for
its order. The window says so in words and keeps the ideal curve; a deeper stopband, a wider
transition or a lower order is the remedy. A ladder is never shown that does not reproduce the
response.

## The plot {#mask}

The two plots sit side by side across the top. Both are Data Display plots. The magnitude plot
carries the ideal |S21|, the ladder's |S21|, and the ladder's |S11| on the right axis. The phase
plot is the ladder's phase and its group delay, in the same nanoseconds the rest of the application
uses. The ladder is drawn underneath, arm by arm: a series LC to ground is one column; a parallel
LC in the through path is its capacitor on the line with the inductor on a short rail above; a
bandpass or bandstop series arm with a transmission zero is a box of three parallel branches — the
series resonator on top, its inductor on the line, its capacitor below; and a parallel resonator to
ground is two adjacent legs. Every label sits beside its own symbol. **Copy** puts the same drawing
on the clipboard, and pasted into a schematic it extracts to exactly the ladder in the table.
Resizing the window re-fits the ladder, so it stays centred and whole in its pane. Exporting or
copying the magnitude plot keeps the mask lines and their labels, as drawn on screen.

**Design** on the toolbar rebuilds the ladder and the curves. Every edit redesigns after a
short pause, off the window's thread, so typing an order of 25 does not freeze the window; the
status line says *Designing…* until the new ladder is drawn. A design whose specification has not
changed (a new frequency or impedance level on the same response) is reused, not resynthesised.

Design information reports the order, the ladder's load, and the minimum insertion loss in the
passband — 0 for a lossless ladder that touches 0 dB, and the passband's worst-case best
transmission after a snap.

## Exact, standard, library {#values}

The realization table has the theoretical value and the value that is written.

| Choice | What each element becomes |
|---|---|
| Exact | The continued-fraction value, in henries or farads. |
| E6, E12, E24, E96 | The nearest IEC 60063 preferred number, nearest by ratio — the same snap the Smith chart and the optimizer use. |
| Vendor library | Real parts from the workspace's vendor libraries. Choosing it indexes the libraries and shows an **Inductors** and a **Capacitors** series list, which are hidden for every other choice. Each element becomes the nearest stated value in its series, and the row names the part; the table's Series and Part lists pick a different part for one element. |

A workspace with no vendor library shows no series lists and says so, and every value stays exact.
It does not fall back to an E-series. An element whose kind has no series chosen keeps its exact
value. The power-integrity part library is a different drawer and is not read here.

With Vendor library chosen, **Place filter** and **Replace this Filter** place the library parts
themselves: each element is the vendor part the table names, exactly as if it had been placed from
the palette, with its case size's footprint on a board technology.

## Where the result goes {#place}

**Apply to Filter**, when the window was opened on a placed Filter, writes the whole specification
onto that instance: Response, Form, Order, the cutoff, Ripple, As, the elliptic type and the two
impedances, so the ideal block and the ladder are the same response. Astop is the floor the design
was built at; when that is deeper than the mask's As, `AsMask` keeps the mask's. It also writes the window's
own settings, which the block keeps for the Designer and does not simulate: Ap, every mask edge
(`Fp`, `Fs` or `Fp1`, `Fs1`, `Fs2`, `Fp2`), Minimum or Specify for the order, the first element, the
value series and the library series and parts. **Synthesise…** on that Filter later reopens the
window exactly as it was applied, with nothing derived again.

**What the block simulates follows Values.** With Exact, the Filter is the ideal transfer function,
which is the exact ladder's own response. With an E-series or Vendor library, a Filter that carries a
design simulates as its ladder: the snapped values, or the vendor library parts its properties name,
so the run uses what the properties say. A part the workspace's libraries no longer hold is reported
in Messages, and the block then simulates as the ideal filter. With **Use Synthesis** unticked the
block is always the ideal transfer function, since its fields may no longer match the design.

**A Filter that carries a design is locked.** Its properties and its parameter dialog show the same
fields the window does, in the window's order, but greyed, and its values cannot be edited on the
schematic either: every change goes through **Synthesise…** and Apply, so the block never drifts from
the design behind it. `IL` stays editable, because the window does not set it. A Filter placed from
the palette carries no design; it shows only the block's own fields and is edited directly.

**Use Synthesis**, beside **Synthesise…** in the Filter's properties, is ticked while the design holds
the block. Untick it to make the block a plain Filter again: every field is editable, so a variable or
a sweep can drive it, and the design is kept, so **Synthesise…** still reopens on it. Ticking it opens
the window, and **Apply to Filter** holds the block to the design again.

**Place filter** puts the realised ladder, at the values the table shows, on the schematic. It has no
terminations: each end is a short wire with a net label, `IN` on port 1 and `OUT` on port 2 (`IN2` and
`OUT2` onward when the schematic already has those names), ready to be wired into the design. The
ladder follows the pointer on the connection grid until you click, exactly as a paste does, and
Escape drops it. **Place component** places one Filter block carrying the specification and the
settings, written by the same function Apply uses.

Both go into the schematic the window was opened on. A window opened from Tools ▸ LC Filter Designer
places into the schematic you last worked in, and both buttons are disabled when there is no
schematic or no design; the tooltip says which.

When the window was opened on a Filter, the **Replace this Filter** toolbar button, while lit, turns
Place filter into a replacement. The block is removed, its two pins keep their nets under labels
(the net's own name when it already has one), and the ladder is placed below where the block was,
with its ends on the same names. It is one undo step, and the ladder arrives selected so you can
move it.

To get the ladder as a netlist, use `circuitrf filter lc … -o filter.cnl`: two terminations and the
inductors and capacitors between them. That file is what `sparam` simulates, and it is the same
netlist the plot was drawn from.

## The same thing from the command line {#headless}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf filter lc --form bandstop --response Butterworth --order 3 --f1 1GHz --f2 2GHz --series e24 -o filter.cnl</code></pre>

The verb calls the same synthesis the window calls. The options are in [CLI › filter lc](cli.html#filter-lc).
