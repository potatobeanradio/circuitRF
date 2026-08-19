# Plot Versus (`Y vs X`) — Design

Status: implemented (2026-08-19). Audience: circuitRF developers, plus the user-documentation pass
(§8 is the cheat-sheet).

A trace's X axis has always been the cube's own swept axis. **Plot versus** lets a trace name its
own X data instead: `Gain vs Pout`. The motivating case is PA design — a sweep is authored over
input power because that is what the simulator can drive, but the design is read against **output**
power, because the transmit chain is budgeted backwards from the antenna. Gain, PAE, efficiency and
AM-PM all want Pout on the bottom axis.

Reads with: `trace-card.md` (the card this appears on and the slice shorthand it extends),
`family-curves.md` (the family rules it inherits), `data-display.md` (plot types, Table columns),
`expressions.md` (the expression grammar each side is written in).

Primary source files:
`src/Ui/DataDisplay/VersusSpec.cs` (the separator), `VersusResolver.cs` (X-side resolution + gates),
`ViewModels/PlotInspectorViewModel.cs` (`SetCubeDataFrom`, `ResolveFamily`),
`ViewModels/TraceRowViewModel.Versus.cs` + `XAxisPinRowViewModel.cs` (the card row),
`Models/Trace.cs` (`XSpec`/`XSourcePath`, `FamilyCurve.RawX`), `Models/Plot.cs` (`XLabelFor`,
`XLabelsDiffer`), `Renderers/AxesRenderer.cs` (per-trace X label rows),
`Renderers/TableRenderer.cs` (index-paired columns).

---

## 1. Syntax

```
<y-expression>  vs  <x-expression>
```

`vs` — or `versus`, case-insensitive — is a **lowest-precedence infix separator**, split off before
any cube-name scanning, at **top level only**: never inside `[ ]`, `( )`, or a quoted label. At most
one per trace.

Both sides are ordinary trace specs (single-cube shorthand or a multi-cube expression), so neither
grammar changed to accommodate this:

| Spec | Reads as |
|---|---|
| `Gain vs Pout` | gain versus output power |
| `PAE vs Pout` | PAE versus output power |
| `dB20(HB1.V[:, "Vout", 1]) vs Pout` | fundamental output voltage in dB, versus Pout |
| `Gain vs dB10(Pout_W)` | the X side takes transforms like any other spec |
| `Gain[:, ~] vs Pout` | a family — see §3 |
| `Gain vs measured::Pout` | X from a **different loaded file** — see §4 |
| `Gain` | unchanged: X is the cube's swept axis |

**Degenerate case, documented rather than defended:** a cube literally named `vs` must be written
bracketed (`vs[:]`) to be read as data rather than as the separator.

## 2. Where the X side lives, and why it is not just expression text

The X side is its **own field** on the trace (`Trace.XSpec`), not folded into `Trace.Expression`:

```
Trace.CubeName / Slice / Transform   ← the Y side: unchanged identity
Trace.XSpec                          ← the X side, parsed by the SAME parsers
Trace.XSourcePath                    ← optional: the X side's own data source
```

This is the load-bearing decision. A multi-cube `Expression` sets `CubeName`/`Slice` to null, which
blanks the card's axis-role editor (`trace-card.md` §3). Had `Gain vs Pout` gone down that path,
**every versus trace would have lost its axis editor** — the opposite of the goal. Holding the X
side separately means versus is an *X-source attribute*, and the Y side keeps its identity: the
group/item combos, the axis-role rows, the family machinery, and the pinned-axis labels all work
exactly as they do for any other trace.

`Trace.Expression` still carries the whole `Y vs X` text (it is what the spec box shows and what a
label or Table header reads); the resolver splits it and evaluates only the Y half.

## 3. Families — one X per curve

The family case is `Gain[:, ~] vs Pout` — Gain and Pout both swept over `[Pin, RFfreq]`, one curve
per RFfreq. **Each curve's X data genuinely differs** (Pout at 2.0 GHz is not Pout at 2.4 GHz), so
the shared-X model every other family uses does not apply:

- `FamilyCurve.RawX` holds the per-curve X array; `BuildFamilyPath` uses `fc.RawX ?? _cubeXValues`,
  so every ordinary family is byte-for-byte unchanged (`RawX` is null there).
- The trace-level `_cubeXValues` becomes curve 0's X — an anchor for anything that still reads one
  array — and the marker readout deliberately reads the **marked curve's** own X instead (§6).

**Role congruence.** The two sides must mark the **same axis name** as `~` and the same axis as `:`.
A bare X side (no brackets) **inherits** the Y side's roles by axis name — X, family, and pinned
index alike — which is why `Gain[:, ~] vs Pout` means what it reads as and is the form the card
emits. A bracketed X side is taken exactly as typed and is checked for congruence:

> `'Pout[:, 0]': both sides must iterate the same family axis ('RFfreq').`

An X cube with axes the Y side does not have (picking `HB1.V` as X still needs a node and a
harmonic) gets those axes pinned — by the card's Fix rows (§5), or at index 0 when inherited.

## 4. Cross-source X

`XSourcePath` names the X side's own dataset; null means "the same source as Y" (and keeps following
a sentinel-bound trace when the toolbar's datasource changes). Measured Pout against simulated Gain
is one trace.

- **Typed as `alias::Cube`.** The alias is the same one the label strips and the source combo show.
  It is resolved to a path at commit and **never stored** — an alias is renamable, a path is not.
  `Trace.XSourceAlias` re-attaches it for display only, so the spec box and Table header still say
  where X came from.
- **Persisted** like a source alias key: relative to the results root when it lives there, else
  absolute. It always names a **concrete file** — never the `Selected` sentinel, because an X side
  belongs to one dataset, not to whichever source happens to be selected.
- An alias that matches no loaded dataset is reported by the **resolver**, not at the point of
  typing: every edit is followed by a resolve that would otherwise replace the card's message.

## 5. The card

```
 Group  [ Measurements ▾ ]  Item [ Gain ▾ ]                    →R
 ──────────────────────────────────────────────────────────────────
 Pin     [ X ] [Fam] [Fix]
 RFfreq  [ X ] [Fam] [Fix]   ▾ 2.40 GHz
 ──────────────────────────────────────────────────────────────────
 ☑ vs X:  [ measured ▾ ]
          [ HB1 ▾ ] [ V ▾ ]  [ Mag ▾ ]
          Shares the axes above: Pin = X, RFfreq = family
          node       Fix  ▾ Vout      ← the Y side has no node axis
          harmonic   Fix  ▾ 2
 ──────────────────────────────────────────────────────────────────
 [ None ▾ ]  Gain[:, ~] vs mag(HB1.V[~, :, "Vout", 2])
```

- Ticking **vs X** is **one choice** in the common case: which quantity is X. The swept axis and the
  family are inherited by name.
- **A shared axis gets no row here — it gets a sentence.** `Shares the axes above: Pin = X, RFfreq =
  family` (and `RFfreq = fixed at 2.40 GHz` when pinned, naming the value, not an index). The role and
  the pinned value of a shared axis belong to the trace and are already edited by the axis rows a few
  lines above; a second copy here is two controls on one piece of state.
  **This took three tries and the first two were both controls.** Omitting the shared axes entirely read
  as *"the family button is missing from this card"*; adding them back disabled read as *"they're just
  text, I can't click them"*; making them live made them duplicates — *"does it make sense to have Pin as
  X, Fam and Fix as well? I am confused with the vs X area"* (owner, all three, 2026-08-19). The need was
  never a control. It was **knowing** that the family and the swept axis apply to the X side too, which
  is one sentence.
- **Rows appear only for the X quantity's OWN axes** — the ones the Y side does not have (picking
  `HB1.V` as X still needs a node and a harmonic). Those can only ever be pinned, since a family needs
  the same axis on both sides, so each shows a value picker and nothing else.
- **The X spec is re-derived whenever the Y side's roles change.** An explicit X spec otherwise keeps
  the roles it was written with, so pressing Fam would leave `Gain[:, ~]` beside `…[:, 0, …]` — which
  the resolver then correctly refuses. The Y-side flush regenerates it before composing the text.
- **The X side has its OWN transform combo**, and it must: the X axis has to be real, an ordinary X
  quantity (`HB1.V`) is complex, and the card's other transform combo transforms **Y**. Reaching for
  that one moves the wrong half of the trace (also owner-reported). The X transform is written into
  the X spec as a function call — `mag(HB1.V[…])` — which is exactly the form the spec parser reads
  back, so a typed spec and a picked one land in the same place and no new persisted field exists.
  Picking a complex X quantity **defaults the X transform to Mag** rather than leaving the trace in
  an error state, and `None`/`Conj` are not offered as usable options for a complex X. **Changing the
  X quantity re-derives its transform from the new quantity alone** — real → `None`, complex → `Mag` —
  the same convention the Y side's signal switch follows; carrying the old one over left `mag` sitting
  on a real quantity purely because the previous one had been complex.
- The default X quantity is deliberately **not** the Y quantity — Gain vs Gain is never what was meant —
  and it prefers a **real sibling in the Y quantity's own group** (`Gain` → `Pout_dBm`). "First cube in
  the file that isn't Y" lands on a raw complex HB voltage on a real run, so the feature would open on an
  X that could not be plotted without a transform.
- The **source** combo appears only once a second dataset is loaded, mirroring the Y side's own
  Source selector, so a single-dataset display is untouched by this feature.
- The X picker lists **quantities** only: no network metrics, no V/I placeholders, no S(i,j) element
  explosion. Anything more exotic is typed into the spec box.
- The card emits a **bare** X spec whenever the Y side answers for every axis (that is the form that
  inherits, and the form that survives a re-run whose sweep changed length), and the explicit
  bracketed form only when the X cube has axes of its own. Explicit pins are re-read from the spec
  on every rebuild, so a `.cdd` load cannot silently rewrite a saved pin back to index 0. The X
  transform is read from the **spec** first and the combo only as a fallback, so typing a spec
  without one genuinely clears it.

## 6. What the rest of the plot does with it

**X-axis label.** `Plot.XLabelFor(trace)` returns the X spec text for a versus trace. When the traces
on a Rect plot do not share one X quantity (`Plot.XLabelsDiffer`), the renderer draws **one X label
row per trace, in the trace's own colour** — the mirror of how Y labels have always been drawn — and
the plot box shrinks to make room. With a shared X it keeps the single centred label. The Y labels'
`" dimension mismatch"` suffix is suppressed on exactly those plots: the rows already say it, and on
a `Gain vs Pout` beside `Gain vs Pin` plot the difference is deliberate.

**There is no unit on a versus X, and there cannot be one from the data.** `DataCube` carries units
on **axes only** — cube *values* have no unit anywhere in the model. So the label reads `Pout`, not
`Pout (dBm)`, exactly as every Y label already reads `Gain`. Custom X label covers the user who wants
units. (While fixing this, `AxesLimitsViewModel.XUnitLabel` — which hardcoded the frequency unit for
every Rect plot, so a Pin sweep's limits already said "(GHz)" — was re-pointed at
`Plot.XAxisUnitLabel`.)

**Table.** A versus column **pairs by row index** (`TableColumn.PairByIndex`), and keeps sweep order.
An ordinary X column is a sorted, de-duplicated sweep axis whose cells are found by matching the X
value; a versus X is the *values* of another quantity — it can be non-monotonic (Pout folds back past
compression) and can repeat, so sorting would reorder the pairing and a repeat would collapse two
distinct sweep points into one row. Adjacent-dedup additionally requires the same pairing rule.

A versus **family** on a Table emits an **(X, Y) column pair per curve**
(`Pout @ RFfreq = 2 GHz | Gain @ RFfreq = 2 GHz | …`), because the curves do not share an X column.

**Markers.** The readout builds its X row from the trace's X name/values, so a versus trace reads
`Pout=18.0` rather than `Pin=5`. On a versus family it reads the **marked curve's** own X. *Known
nuance:* a Table marker resolves its row by nearest X value, so on data with a genuinely repeated X
value it may report the first of the two rows.

## 7. Gates (what is refused, and what it says)

| Condition | Message |
|---|---|
| Point counts differ | `'Gain vs Pout': X has 21 point(s), Y has 41 — both sides must slice the same swept axis.` |
| X side complex | `X side 'Vout' is complex — the X axis must be real. Wrap it in mag(), real(), dB20(), …` |
| Smith / Polar | `'vs' is available on Rect and Table plots only.` |
| Y side is a scalar | `'vs Pout' needs a swept Y — this selection is a single value.` |
| Two separators | `Only one 'vs' is allowed — a trace has one X axis.` |
| Empty side | `'vs' needs an expression on each side (e.g. "Gain vs Pout").` |
| Family axes disagree | `'Pout[:, 0]': both sides must iterate the same family axis ('RFfreq').` |
| Unmatched `alias::` | `No loaded data source named 'measured'.` |

A refused trace reports under the spec box, carries `<invalid>` on its label, and **renders nothing** —
and that label marker is the part that must not be skipped: the label is built from the trace's
*authoring* state (cube + pins + transform), which stays perfectly well-formed when the *resolve*
fails, so without an explicit marker the trace just disappears from the plot with a label that looks
right (owner-reported for a complex X). `TraceLabeler.BuildCubeQuantity` appends it for **any**
unresolved cube binding — a failed versus X, a cube missing from a re-run, a bad typed spec —
it never draws a partial or mis-paired curve. The point-count gate is what makes a cross-source X safe:
two files whose sweeps disagree can never be silently paired.

## 8. Interface reference (for user documentation)

- **Plot against another quantity:** tick **vs X** on the trace card and pick the quantity — e.g.
  Gain against **Pout**. Everything else about the trace stays as it is.
- **If the X quantity is complex** (a voltage, an S-parameter), use the **X transform** combo in the
  vs row — `Mag`, `dB20`, `Real`, … The transform combo further down the card is the **Y** one; the X
  axis has its own because it has to be real. Picking a complex X sets `Mag` for you.
- **The vs row states what it shares with the trace** ("Shares the axes above: Pin = X, RFfreq =
  family"). Change the swept axis or the family in the ordinary axis rows above — the X side follows.
  The only rows inside the vs area are for axes the X quantity has and the Y side does not (a node, a
  harmonic), and those can only be fixed to one value.
- **Type it directly:** `Gain vs Pout` in the spec box. `versus` also works.
- **Families:** make the Y side a family as usual (**Fam** on RFfreq); the X side follows it — you get
  one Gain-vs-Pout curve per frequency.
- **X from another file:** pick it in the vs row's source combo (or type `measured::Pout`).
- **Table:** the same trace gives you a Pout column beside its Gain column, in sweep order; a family
  gives you one such pair per curve.
- **Markers** read the X quantity by name (`Pout=…`), and the plot's bottom label says what is
  actually plotted — one label row per trace when two traces use different X quantities.

## 9. Persistence

`TraceConfig.XSpec` + `TraceConfig.XSourcePath`. Both null for a non-versus trace, so every `.cdd`
written before this feature loads unchanged.
