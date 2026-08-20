---
title: Units
slug: reference/units.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Units
lede: Database units, display units, the snap grid, and what circuitRF accepts when you type a value into a field.
---

circuitRF keeps three things apart that other tools tend to conflate: **how a number is stored**, **how
it is shown to you**, and **what grid new geometry lands on**. Keeping them separate is what makes
"change the units" an instant, lossless operation here instead of a destructive one.

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#dbu">The database unit (DBU)</a></li>
<li><a href="#storage-vs-display">Storage versus display</a></li>
<li><a href="#resolution">Changing the DBU resolution is a migration</a></li>
<li><a href="#snap">Layout units and the snap grid</a></li>
<li><a href="#wbond">wBond units</a></li>
<li><a href="#typing">Typing a value into a field</a></li>
<li><a href="#unit-field">A unit is a field, not part of an expression</a></li>
</ol>
</nav>

## The database unit (DBU) {#dbu}

Every layout coordinate in circuitRF is a **64-bit integer** in *database units*. Not a floating-point
millimetre — an integer count.

The reason is exactness. After a rotate, a paste, a boolean operation and a GDSII round-trip, two
vertices that ought to coincide must compare **exactly** equal, or the geometry silently develops
hairline gaps and self-intersections that only show up at the fab. Integers give that guarantee;
doubles do not. It also makes "is this vertex on the grid?" a division with no remainder rather than a
comparison against a tolerance.

**One DBU is one nanometre by default.** The layout property that sets this is `DbuPerMicron`, and its
default is **1000**. That specific number is chosen, not inherited:

```
1 µm  = 1000  DBU     exact
1 mil = 25400 DBU     exact
```

Both are exact integers at 1 nm resolution. A metric-authored MMIC and an imperial-authored PCB are
therefore commensurable in the same database, a cell can be copied from one to the other with an exact
transform, and mil↔µm conversion never produces a fractional coordinate. That single choice removes the
whole family of "the imported board is 0.0003 mil off grid" problems.

At `long` range, ±9.2 × 10<sup>18</sup> DBU is ±9.2 × 10<sup>9</sup> metres of addressable space. There
is no practical ceiling.

## Storage versus display {#storage-vs-display}

<div class="callout warn">
<span class="label">The single most common source of confusion</span>
<p><strong>The display unit is a presentation setting. Changing it moves nothing.</strong> A vertex
stored at 25400 DBU reads <code>25.4</code> when the display unit is µm and <code>1</code> when it is
mil. The stored integer is identical in both cases, and switching back and forth returns the byte-identical
file.</p>
</div>

So changing the display unit is free: every shape stays exactly where it was, stays exactly on whatever
grid it was on, and the change needs no undo entry beyond a view preference. It round-trips perfectly.

The display unit *is* saved in the `.clay` file, purely so a layout reopens in the unit it was authored
in. It carries no other meaning. The available display units are **nm, µm, mm, mil** and **inch**, and
the default comes from the technology — `mil` for the PCB starter technology, `µm` for the MMIC one.

## Changing the DBU resolution is a migration {#resolution}

`DbuPerMicron` is a different matter, and circuitRF treats it as one. It is **not a preference toggle**:

- It may only change by an **exact integer ratio**. A non-integer ratio is rejected outright.
- **Refining** (1 nm → 0.1 nm, ratio 10) is always lossless — every coordinate is multiplied by ten and
  the operation proceeds, logged to the Messages pane.
- **Coarsening** (0.1 nm → 1 nm) is lossy the moment any coordinate is not divisible by the ratio. The
  whole design, including every referenced sub-cell, is pre-scanned first; if any vertex would move, the
  specific offenders are reported to Messages and the change requires confirmation. It is never a silent
  snap.
- The whole thing is one undoable operation.

In practice almost nobody touches it. The default is right for both target markets, and because
refinement is always lossless, starting at 1 nm is safe even for a process that later needs finer
resolution.

## Layout units and the snap grid {#snap}

The **snap grid** is a third, independent number: the pitch that new and edited vertices land on. It is
stored in DBU and it is yours to change at will.

**R5 in one sentence: changing the snap grid never re-snaps existing geometry.** Moving from 1 µm to
0.5 µm does not move a single existing vertex. There is a separate, explicit **Snap selection to grid**
command for when you actually want that, and it reports any shape it could not snap without
self-intersecting.

This is the opposite of the schematic's rule, and it is right for layout. In a schematic an off-grid pin
is a correctness bug — it breaks connectivity. In a layout an off-grid vertex is merely unusual:
imported GDSII, a 45° diagonal and a flattened arc all legitimately produce vertices between grid
points.

| | PCB starter technology | MMIC starter technology |
|---|---|---|
| Display unit | mil | µm |
| Snap grid | 1 mil | 5 nm |
| `DbuPerMicron` | 1000 | 1000 |

The layout toolbar's **Snap** field sets this grid pitch, and offers a ladder of multiples of the
technology's own default rather than a fixed list. **F9** toggles it off and back to the last non-zero
value.

<div class="callout note">
<span class="label">Grid snap and geometry snap are two different things</span>
<p>The <strong>Snap</strong> field is the <em>grid</em>. <strong>Geometry snap</strong> — snapping to a
pin, a corner, a midpoint — is a separate feature with its own toolbar toggle (F3 or <kbd>S</kbd>), and
its capture radius is not a setting at all: it is a fixed <strong>8 device pixels</strong> converted to
DBU at the current zoom, so it stays the same size on screen at every zoom level. See
<a href="layout-editor.html#geometry-snap">Geometry snap</a> in the layout editor chapter.</p>
</div>

## wBond units {#wbond}

Bondwire geometry is stored in **nanometres** — plain nanometres, not DBU. At the default 1000 DBU/µm
the two happen to coincide exactly, which is why the conversion between them is one deliberate,
single-place bridge rather than something spread through the code.

Everything a wire edit does quantises to **one nanometre**. On a 500 µm loop height that is a relative
error of 2 × 10<sup>-6</sup> — physically about a fifth of a millionth of a mil.

Inductance in the wBond panel is always read out in **pH**, fixed, never auto-ranged. That is
deliberate: the panel exists so you can watch a number while dragging, and a unit that switches
mid-drag fakes a 1000× jump.

## Typing a value into a field {#typing}

There are two vocabularies, because there are two different kinds of field.

### Layout dimension fields

A width, a length, a coordinate, a DRC rule value. These accept a bare number — interpreted in the
current display unit — or a number with one of these suffixes, case-insensitively and with any amount
of whitespace:

| Suffix | Means | Example |
|---|---|---|
| *(none)* | the current display unit | `2.5` |
| `nm` | nanometre | `250nm` |
| `u`, `um`, `µm` | micrometre | `50u` |
| `mm` | millimetre | `2.9mm` |
| `mil` | thousandth of an inch | `115mil` |
| `in`, `inch` | inch | `0.25in` |

Leading `+`/`-` and scientific notation (`1.5e3`) are accepted. Anything else is rejected and the field
reverts — it does not silently guess.

### Component parameter fields

A resistance, a capacitance, a frequency. These are evaluated by the
[expression engine](expressions.html), and the unit is a **separate column in the row**, not part of the
number. The unit column accepts these:

| Prefix | Multiplier | | Unit family | Spellings |
|---|---|---|---|---|
| `T` | 10<sup>12</sup> | | Frequency | `Hz` `kHz` `MHz` `GHz` `THz` |
| `G` | 10<sup>9</sup> | | Inductance | `H` `mH` `uH` `nH` `pH` `fH` |
| `M` | 10<sup>6</sup> | | Capacitance | `F` `mF` `uF` `nF` `pF` `fF` |
| `k` | 10<sup>3</sup> | | Resistance | `Ohm` `mOhm` `kOhm` `MOhm` `GOhm` `TOhm` |
| `m` | 10<sup>-3</sup> | | Length | `metre` `nm` `um` `mm` `cm` `mil` `in` `inch` |
| `u` | 10<sup>-6</sup> | | Angle | `deg` `rad` |
| `n` | 10<sup>-9</sup> | | Voltage / current / power | `V` `kV` `mV` `uV` `nV` · `A` `mA` `uA` `nA` · `W` `mW` `uW` `kW` |
| `p` | 10<sup>-12</sup> | | Logarithmic | `dB` `dBm` `dBc` `dBW` |
| `f` | 10<sup>-15</sup> | | | |

The glyphs **Ω** and **µ** are accepted and normalised — `kΩ` is `kOhm`, `µF` is `uF`. A prefix on its
own is also a valid unit, so a row whose value is `2.2` and whose unit is `p` resolves to 2.2 × 10<sup>-12</sup>.

<div class="callout warn">
<span class="label">"m" is milli, not metre</span>
<p>A bare <code>m</code> is the SI prefix <strong>milli</strong>. The metre has its own spelling,
<code>metre</code>, and every other length (<code>mm</code>, <code>um</code>, <code>mil</code>, …)
reduces to it. This is a deliberate decision, not an oversight: re-pointing <code>m</code> at the metre
would silently multiply every hand-authored <code>C=1m</code> in an existing netlist by a thousand, with
nothing anywhere reporting it. The logarithmic units are measurement functions rather than scale
factors, so <code>dBm</code> carries no multiplier.</p>
</div>

## A unit is a field, not part of an expression {#unit-field}

**The expression parser has no unit-suffix production**: `2 GHz` is not an expression, and asking the
parser for one is an error at the `GHz`. What saves you is that nothing asks it to. A VAR row whose unit
column is empty has any trailing unit token lifted out of the expression first, so writing
`RFfreq = 2 GHz` in the schematic VAR editor gives you the variable you meant — the `2` becomes the
expression and `GHz` the unit — and a `.cnl`, which has no unit column at all, has always been read the
same way. The two entry points cannot make identical text mean two different things.

The lift is applied *only* when the text does not already parse and the split makes it parse. That
restraint is the point: because every bare SI prefix is a valid unit, a token-based rule would tear
`2 * f` into `2 *` plus femto and `R * m` into `R *` plus milli — expressions that are legal and common.
Those parse as they stand, so nothing is lifted from them.

<div class="callout warn">
<span class="label">Where it still does not help</span>
<p>The lift needs a <strong>separate token</strong>. <code>2GHz</code>, with no space, is one word to
the splitter and stays a parse error — and because an unresolvable global is skipped during elaboration,
that leaves <strong>no variable at all</strong> rather than a wrong value. The same is true of a bare
<code>60u</code> in a PCell parameter. When in doubt, put the number in the expression and the unit in
the row's unit column, which is unambiguous everywhere.</p>
</div>

<p class="small">See also: <a href="expressions.html">Expressions</a> ·
<a href="layout-editor.html">The Layout Editor</a> · <a href="wbond.html">wBond</a> ·
<a href="components.html">Components</a> · <a href="pcells.html">PCells</a>.</p>
