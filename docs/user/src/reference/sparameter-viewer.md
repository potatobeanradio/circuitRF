---
title: S-Parameter Viewer
slug: reference/sparameter-viewer.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > S-Parameter Viewer
lede: A Touchstone file at a glance - what it states, its reflections on a Smith chart, its transmissions on a polar chart, and every term in dB and phase over the file's own frequency range.
keywords: S-Parameter Viewer, Touchstone, s2p, sNp, SNP, SnP, S-parameters, Smith chart, polar chart, magnitude, phase, S11, S21
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#opening">Opening the viewer</a></li>
<li><a href="#file">File information</a></li>
<li><a href="#plots">The four plots</a></li>
<li><a href="#plots-shown">Choosing the plots</a></li>
<li><a href="#terms">Choosing which terms to draw</a></li>
<li><a href="#findings">Findings</a></li>
<li><a href="#saving">Saving it as a Data Display</a></li>
<li><a href="#headless">Running it headless</a></li>
<li><a href="#limits">What it will not do</a></li>
</ol>
</nav>

## Opening the viewer {#opening}

**Tools &rsaquo; S-Parameter Viewer** opens an empty viewer, with or without a workspace, and its
**Open Touchstone file&hellip;** button reads a `.s1p`, `.s2p`, &hellip; `.sNp`. It is a window of its
own, like the Match Designer and the Signal Designer, and it opens centred on the screen that holds
the window you opened it from. Several may be open; asking for a file that is already on show brings
that window forward.

You can also drag a Touchstone file from your file manager onto the viewer. It opens the file in
that window, just as **Open Touchstone file&hellip;** does.

A Touchstone file in the Workspace opens the same way. **Open in S-Parameter Viewer** is on its
right-click menu, and double-clicking the file does it too &mdash; for a file you imported and for a
loose `.sNp` sitting in the workspace folder.

A placed [SnP](components.html#snp) component &mdash; the library's **S1P**, **S2P**, **S3P**, **S4P**
and **SnP** are all this one component &mdash; has three buttons on its **File** row. **Browse&hellip;**
chooses the file, the folder button shows it in your file manager, and the eye button opens the viewer
on it. The eye is available once the file is chosen and is there.

In a viewer opened from the eye button, a Touchstone file dropped on the window also becomes the
component's **File**, and its port count is set from the file. A file that cannot be read changes
nothing. The change is an ordinary schematic edit: **Edit &rsaquo; Undo** in the schematic gives
the component its previous file back, even after the viewer is closed.

A Data Display's dataset list offers the same **View** on any row whose file is a Touchstone file.

## File information {#file}

The left pane reads the file back to you, which is most of the point of opening it. The path under
the file name opens that file in the system file manager.

| | |
|---|---|
| **Ports** | How many the data holds. When the file's name claims a different number, it says so. |
| **Parameters** | S, Y or Z, and how the numbers are written: real and imaginary, magnitude and angle, or dB and angle. |
| **Unit** | The frequency unit on the file's option line. |
| **Points, Start, Stop** | How many frequencies, and the first and the last. |
| **DC point** | Yes when a sample is at 0 Hz. Otherwise No, with a yellow warning, repeated on the status bar. |
| **Spacing** | Uniform, with its step; logarithmic, with its points per decade; or irregular, with its smallest and largest step. |
| **Reference** | The reference impedance, per port when the ports differ. |
| **Noise data** | How many noise rows a two-port carries, or none. |
| **Comments** | How many comment lines the file carries, when it has any. |

The plots follow the file. Nothing is resampled and nothing is interpolated: each curve joins the
file's own points.

## Choosing the plots {#plots-shown}

Four buttons sit above the plots: the Smith chart, the polar chart, magnitude and phase. The window
opens with the Smith chart and magnitude on, and polar and phase off. Each button turns its plot on
or off, and with none of them on the pane is empty.

| On | The pane shows |
|---|---|
| One | That plot, filling the pane. |
| Smith and polar | Side by side. |
| Magnitude and phase | Stacked, magnitude above phase. |
| Two of different shapes | Stacked, the round chart on top and the rectangular plot below. |
| All four | Smith and polar across the top, magnitude and phase across the bottom. |

The polar chart draws the transmissions only — S21, S12 and the other off-diagonal terms — never a reflection.

The names of the traces sit beside each plot. Nothing is written above the curves.

## The four plots {#plots}

**Reflection** is a Smith chart of the reflection at each port &mdash; S11, S22 and so on.

**Transmission** is a polar chart of the off-diagonal terms only &mdash; S21, S12 and the rest.

**Magnitude** is every ticked term in dB against frequency, and **Phase** is the same terms in degrees.
The frequency axis runs from exactly the file's lowest frequency to exactly its highest, rather than
out to the nearest round number, and a logarithmically swept file is drawn on a logarithmic axis. The
phase axis is fixed at &plusmn;180&deg;, which is the whole of what a wrapped phase can be.

The axis unit follows the span. A file written in Hz that runs from 1 to 5 GHz reads in GHz, because
an axis of 1E+09 to 5E+09 says the same thing badly.

Hover the magnitude or phase plot and a vertical line crosses every trace at that frequency, with
each value in the box beside the pointer. On the Smith chart and the polar chart the marker sits on
the nearest sample. A Smith reflection reads `S(1,1) @ 2.4 GHz`, then Γ as a real and imaginary pair
and as a magnitude and angle — `Γ=0.5+j0.1 (0.51 ∠ 11.31°)` — then the impedance at the file's
reference as `Z=Z0*(1+j0) Ω`.
A double-click still drops a marker you can drag and keep, and
**Delete** removes the selected ones. Arrow keys step a kept marker one sample at a time.

A file of Y- or Z-parameters is drawn as S-parameters at the file's own reference, because a Smith
chart of an admittance would not be a reflection. The file pane still says Y or Z, which is what the
file is.

## Choosing which terms to draw {#terms}

Every term is a tick box, and a reflection's name is the heavier one. The four buttons above them:

| | |
|---|---|
| **All** | Every term of the matrix. Past 64 terms it asks first. |
| **Reflections** | S11, S22, &hellip; only. |
| **Default** | What the viewer opened on. |
| **None** | Clears the plots. |

Up to four ports, **Default** is every term. Above that it is the reflections plus each forward
neighbour, S21, S32, S43 and so on, over the first eight ports &mdash; an `.s16p` has 256 terms and a
file can have hundreds of ports, so drawing all of them is a choice rather than a greeting. Past six
terms the list beside a plot is left off, because it would take the width the curves need; each tick
box carries its term's colour.

A file with many ports lists its terms one matrix row at a time, in a list that scrolls on its own.

## Findings {#findings}

Three things about a file are defects, and each is stated as a sentence naming where:

- the file's name claims a port count the data does not hold, so its matrices may have been assembled
  the wrong shape;
- the frequencies are not strictly increasing, and the plots draw them in file order anyway so the
  defect is visible;
- the reference impedance has a resistance that is not positive, so the data cannot be renormalised.

The plots are drawn either way. A file that cannot be read at all shows the reader's own reason and
no plots.

Passivity, reciprocity and causality are not reported here. An amplifier has gain and a circulator is
not reciprocal, and neither is a defect in a file; [`circuitrf check`](cli.html#check) measures all
three and leaves the judgement to you.

## Saving it as a Data Display {#saving}

The viewer keeps nothing of its own. **Save as Data Display** in the header writes
the plots as they stand to a `.cdd` and opens it, so markers, axes and terms can be edited afterwards
in the [Data Display](data-display.html). The file itself is referenced, not copied. The Smith and
polar charts sit across the top and magnitude and phase across the bottom. Each round chart names its
terms beside the chart, or the first few and how many there are when it has more than six, with one
frequency row under it.

**Reload** reads the file again, which is what you want after a measurement or a run has rewritten it.
The terms you had ticked stay ticked.

## Running it headless {#headless}

[`circuitrf snp`](cli.html#snp) prints this pane's lines, and `-o` draws the four plots or writes the
same Data Display the button does.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf snp amp.s2p
<span class="prompt">$ </span>circuitrf snp coupler.s4p --params reflections,S21 -o coupler.svg</code></pre>

## What it will not do {#limits}

It does not check passivity, reciprocity or causality.

It reads the Touchstone files the rest of circuitRF reads. A Touchstone 2 file written with keyword
blocks (`[Version] 2.0`, `[Number of Ports]`, `[Network Data]`) is refused with the reader's own
reason, the same as everywhere else.

It edits nothing. To change a file, change the file; **Reload** picks the change up.
