---
title: Tuning
slug: reference/tuning.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Tuning
lede: A slider on any value in the design, and every Data Display following it as you drag.
keywords: tuning, tune, slider, live, sweep by hand, preset, push, snapshot, ghost, lag, run on release, complex value, magnitude, phase, last tuned, range
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#what">What tuning is for</a></li>
<li><a href="#choosing">Choosing what to tune</a></li>
<li><a href="#rows">A row: the slider, the value and the range</a></li>
<li><a href="#complex">Complex values: tuned by their parts</a></li>
<li><a href="#running">Start, Stop, and when the display is behind</a></li>
<li><a href="#snapshot">Snapshot: before and after on one plot</a></li>
<li><a href="#em">Tuning a schematic simulated by EM</a></li>
<li><a href="#push">Push and Revert</a></li>
<li><a href="#presets">Presets</a></li>
<li><a href="#stored">What is stored, and where</a></li>
</ol>
</nav>

## What tuning is for {#what}

The **Tuning** panel puts a slider on any value you pick: a component parameter, a VAR row, or a
parameter of a sub-circuit instance. Press ▶ and every move of a slider re-simulates the schematic,
with every open Data Display bound to its results redrawing. Nothing in the schematic changes until
you **Push**.

The panel is tabbed behind **Analyses**, and the [Optimizer](optimization.html) is tabbed behind it
(both are also under View ▸ Panels). Like the Analyses panel, it follows the focused schematic: focus
a schematic and its tuned values appear; focus anything else and the panel says *Focus a schematic to
tune it.* A Data Display taking focus while tuning is running keeps the panel, because that is where
you are watching the results.

{{ui: tuning-panel}}

The **Tuning and Optimization** example (File ▸ New ▸ Examples) has three benches already set up for this.

## Choosing what to tune {#choosing}

Three ways in, all of which add the same entry:

- **＋** in the panel opens a search over everything tunable in the schematic. Type part of a name, an
  instance, a parameter or a cell. Tick *Include sub-cells* to search inside the schematic's
  sub-circuits as well. **Double-click** a value to add it and keep the search open for the next one;
  **Enter** adds the selected values and closes it. Close it with **×** beside the search box, or by
  clicking anywhere outside it.
- The **tune toggle** beside a parameter in the Properties panel.
- **Right-click a parameter value on the canvas ▸ Tune.**

**What can be tuned.** A value that is a plain number, with or without a unit — `47`, `47 pF`,
`1.2e-9` — and a complex number written with numbers only ([below](#complex)). **A value that is an
expression is not offered at all**, not even greyed: `W=Wvar*2` has no tune toggle. Tune the VAR it
reads (`Wvar`) instead, and everything that reads it follows.

- **Integers** — a finger count, a number of turns — are offered and snap to whole numbers.
- **A sub-circuit's own parameters are tuned per instance.** If cell `DUT` declares `Rbias`, then
  `X1.Rbias` and `X2.Rbias` are two separate rows. An instance that inherits the cell's default starts
  at the default, and Push adds the override to that instance.
- **A value inside a sub-cell is tuned in every instance of it.** `DUT:R3.R` moves R3 in every placed
  `DUT`, because Push writes the `DUT` schematic and cannot write anything narrower. The row says where
  it lives and how many instances share it: `DUT · ×2`.
- **A VAR that an enabled parametric sweep sweeps** is listed but disabled, with the sweep's name as
  the reason.
- **A value inside a read-only cell** (a kit's cell, or a cell from another workspace) can be tuned and
  optimized. Its row is marked, and Push skips it and says so.

## A row: the slider, the value and the range {#rows}

Each row has a slider, a box holding the current value, the unit, and the range under the slider.

- **Drag the slider**, or **type a value and press Enter** (Esc puts the box back). Click a row, then
  **Up** and **Down** move to the previous and next row, and **Left** and **Right** move the value one
  step; Shift with Left or Right moves it ten steps, and Page Up and Page Down a tenth of the range. The
  keys work wherever the panel has the keyboard, acting on the row you last used. A value box you are
  typing in keeps its own arrow keys until you press Enter.
- **The range** is the minimum and maximum under the slider. Click either one to change it. The first
  time a value is tuned its range is guessed: half to twice the value for a positive number, or 0–1 in
  its own unit for zero. The range is shown in the value's own unit, so pF stays pF.
- **⋮** sets how the slider moves, as one choice with a tick beside it: **Log** (the default when the
  range spans a factor of ten or more and is positive), **Linear**, a **Step** (equal increments), or
  **Standard values** — E6, E12, E24, E48 or E96, or the Smith Chart's preferred-value lists for a
  capacitor, inductor or resistor. On a standard-value row the slider and the arrow keys move from one
  value in the series to the next, and the label under the slider names the series. The same menu
  re-centres the range on the current value, resets the range to its default, reveals the part on the
  canvas, or removes the row.

**A vendor part's row** (`C1.Part`) steps through real parts rather than values: the box holds the part
number and the value is shown beside it. Under the slider are the bounds on the parts' stated value and
how they are chosen, the same kind or a search, and below that the number of candidates and their value
span. **⋮** chooses **Same kind** or **Search the library…**, or clears the bounds. See
[vendor part libraries](pdk-integration.html#vendor-optimize).

**One range per value, for both panels.** The Optimizer's variables are the same entries: a range
changed in either panel is changed in both.

**Digits.** The button at the right of the header sets how many significant digits a tuned value is
written with — 3, 4, 5, 6 (the default) or all of them. The value is simulated and pushed exactly as
shown. The choice is saved with the schematic and applies to the Optimizer panel too.

While a session runs, the canvas draws each value that differs from the schematic in the tuned colour.

**The schematic's value wins when it changes.** A Push from the Optimizer or the Yield panel, or a value
typed into the schematic, moves that row's slider to the new value. **Running the Optimizer on the same
schematic stops the session** without writing its result, because the run's result replaces what the
display shows. The sliders keep their values, and ▶ starts tuning again from them.

## Complex values: tuned by their parts {#complex}

A value written as a complex number made of numbers only — `40+15j`, `50-j10`, `complex(40,15)` or
`polar(42.7,20.6)`, with or without a unit after it — is tuned by its **parts**, never whole. When you
pick one, from ＋, the Properties toggle or the canvas menu, you choose which part:

| Part | Row key | Moves |
|---|---|---|
| Real | `real(Zs)` | the real part, holding the imaginary part |
| Imaginary | `imag(Zs)` | the imaginary part, holding the real part |
| Magnitude | `mag(Zs)` | the magnitude, holding the phase |
| Phase | `phase(Zs)` | the phase **in degrees**, holding the magnitude |

Add as many parts as you like. Real and imaginary together, or magnitude and phase, cover the whole
number. Any other mix also works — real with magnitude, say — and each slider still moves only its
own part, holding its partner in the **same** coordinate system. Every other row of that value then
shows where the number has gone.

**Every part's range always holds, together.** A slider stops where it would take the number outside
the range of any of that value's rows — including a row you are not tuning but have a range for. A
range edit that would leave **no** complex number inside every one of that value's ranges (a typed
bound, Re-centre, Reset, the first activation's guessed range) is **refused**, and the status line
names the other ranges. A phase's default range is ±90° around its value and is always linear; a phase
range wider than 360° is an error.

A complex value that reads a name, such as `4+j*X`, is an expression and is not offered: tune `X`. A
plain `50` is a real number and offers no imaginary part. To tune its imaginary part, write it
`50+0j`.

**Push writes the value whole, in the form it was written.** A value written `polar(50,0) Ohm` comes
back as `polar(42.4,21.3) Ohm`, not as a rectangular number.

## Start, Stop, and when the display is behind {#running}

**Nothing is simulated until ▶.** While tuning runs:

- **Each move re-simulates**, newest value first. If a simulation is still running when you move
  again, it finishes and the newest value runs next; the values in between are skipped. A slow bench
  therefore still shows a result while you drag, rather than nothing until you stop.
- **The dot in the toolbar** is grey when idle, green while the display matches the sliders, and amber
  while it is behind them. The rows whose values are ahead of the display are tinted.
- **⚙** chooses which analyses run for each move — all enabled ones, or only those you tick — and
  turns on **Run on release**, which simulates only when you let go of a slider. When moves start
  taking more than about two seconds, a badge on ⚙ suggests it.
- **Every open Data Display bound to the schematic's results follows**, with a small *Tuning* chip
  while it shows tuned results. No trace needs to be changed for this.
- **Evaluate at**, in the toolbar when the schematic has [corners](yield.html#corners), chooses where
  each move is simulated: *Nominal*, or one corner — its temperature, kit sections and values laid over
  the sliders' values. A statistical corner replays its trial's draws around them. The chip then reads
  *Tuning · hot*, say. **Push** still writes the sliders' values alone, never the corner's.

**■ Stop** ends the session and writes the last result shown as the schematic's results file, marked
as coming from tuned values and which ones. Closing the schematic, Revert and Push also stop it.

## Snapshot: before and after on one plot {#snapshot}

**Snapshot** keeps what every display is currently showing for this schematic as faded copies of its
traces. Keep tuning and the live traces move away from them. **Clear snapshot** removes them.
Snapshots last for the session and are never saved. A table has no ghosts.

## Tuning a schematic simulated by EM {#em}

When an analysis is [simulated by EM of the schematic's layout](em-cells.html), the sliders still drive the
**circuit models**: a move costs milliseconds, not an EM solve.

- **Solve EM here** (the cube button) runs one EM solve at the sliders' current values and draws its traces faded
  under the live ones, where a snapshot would be. A geometry already in the EM cache costs no solve. Move a slider
  and the line under the toolbar says the EM traces are stale. They stay where they were solved.
- **Circuit + EM correction** (the button beside it) adds the last solve's difference between EM and the circuit
  model to the live traces. It is exact at the values that were solved and an estimate elsewhere, and the
  display's chip says *circuit + EM correction (estimate)* while it is on.
- **Where the copper stays connected.** When the session starts, each slider's range is scanned by laying out the
  design at points across it, with the other values where they are; no EM solve is run. Under the slider,
  *connected 3.95 to 6.2* is the stretch around the starting value over which every joint still closes and no two
  parts that should be apart touch (*connected throughout* when that is the whole range). A value outside it turns
  the text orange and the line under the toolbar names the gap or overlap at the sliders' values. The slider is
  never held back: the circuit model still answers there, but no layout can be built at that value.

## Push and Revert {#push}

**Push** writes the tuned values into the schematic as **one undo step per document**: the tuned
schematic for its own values and its instances' values, and each sub-cell's schematic for values that
live in it. A sub-cell that is not open is opened as a tab without taking focus, so its unsaved change
is visible and goes through the ordinary save prompt. Push skips a read-only cell and says so.

**Revert** returns every slider to the schematic's value. Until you Push, the schematic is unchanged.

## Presets {#presets}

A preset is a named set of values stored in the schematic.

- **Lock in** keeps the current value of every tuned row as a new preset, named *Preset 1*,
  *Preset 2* … and ready to rename.
- **Recall** — pick a preset in the **Presets** list — loads its values into the sliders, and into the
  running session if there is one. It does not change the schematic until you Push; **⋮ ▸ Recall and
  Push** does both.
- **A preset outlives edits to the schematic.** A value whose part has since been deleted or renamed
  is skipped. One that is now an expression is skipped. One outside its row's range is loaded and the
  range widened. A recalled value that was not being tuned is turned on. The status line says what
  happened (*Applied 11 of 12 · R7 no longer exists*), and hovering it lists every value.
- **⋮** on a preset renames, duplicates or deletes it, or copies it as a `.cnl` `preset` line.
- **Compare**: tick two presets, or one preset and *Schematic*, for a table of both values and the
  difference — per tuned part for a complex value.

**Last tuned.** Saving or closing the schematic while the sliders differ from it keeps their values as
the preset *Last tuned*, at the top of the list. There is only ever one: it is overwritten each time.
Closing then asks whether to save, as for any other change. Rename it to keep it, and the next save
starts a new *Last tuned*.

## What is stored, and where {#stored}

The tuned values' rows, their ranges, the presets, the goals and the optimizer settings are stored in
the **schematic being tuned** — the test bench's `.csch`, even for values that live in a sub-cell. A
change to any of them marks the schematic changed and can be undone. Extracting a netlist carries them
as `tune` and `preset` lines (`circuitrf reference tuning` lists every key). **The tuned values
themselves are not stored** until you Lock in, Push, or save with *Last tuned*.

<p class="small">See also: <a href="optimization.html">Optimization</a> ·
  <a href="data-display.html">The Data Display</a> · <a href="simulations.html">Simulations</a> ·
  <a href="expressions.html">Expressions</a>.</p>
