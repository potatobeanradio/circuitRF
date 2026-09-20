# Brief 21 — two numbers with one name, and a legend nobody can read

**Series:** [railRF](brief-railrf-0-overview.md) · **Tag:** `R-rail21-n` · **Phase:** corrective
**Area:** `src/Design/Layout/Pdn/PdnPlaneModes.cs`, `src/Ui/RailRf/RailRfViewModel.Plane.cs`,
`src/Ui/RailRf/RailLayoutOverlay.cs`, `src/Render/Renderers/RailMapRenderer.cs`,
`src/Engine/Pdn/PdnBreakdown.cs`, `src/Ui/Views/RailRf/RailRfWindow.axaml`
**Depends on:** 1-18 · **Blocks:** nothing
**Found by:** a first-time designer's pass, 2026-09-20

---

## 0. What this brief delivers

Three reports, and the first two are the same failure: **one window prints two different quantities
under one name, and the difference is four orders of magnitude.**

| | Defect | Reported as |
|---|---|---|
| `R-rail21-1` | the board's \|Z\| readout and the plot's Z(1,1) marker are different quantities, identically labelled | *"why does one say 23 mOhm and the other 480 Ohm? at the same frequency"* |
| `R-rail21-2` | the drop headline and the breakdown's own total are different sums | *"48.3 mV or 50.1 mV total drop?"* |
| `R-rail21-3` | the map legend's text is too small to read | *"text is not readable in the scale, unless using heavy zoom"* |

**Nothing here is an arithmetic bug.** Every number in the window is correct. The failures are all
naming and presentation, which is what makes them worth a brief: three correct answers presented so
that a competent reader concluded the tool was broken.

---

## 1. `R-rail21-1` — the plane pair and the rail are not the same \|Z\|

### What the two numbers actually are

The board map is `PdnPlaneModes.Of`'s `ImpedanceMap` — **the plane pair alone**: its copper, its
shape, its stackup, driven from one observation port. The code says so explicitly, in a note it
already emits:

> *"These modes and this map are the PLANE PAIR's own — its copper, its shape and its stackup. The
> decoupling parts, the sources and the loads hanging on it are not in either of them; their answer
> is the \|Z\| curve."* — `src/Design/Layout/Pdn/PdnPlaneModes.cs`

The plot's curve is `PdnSweep`'s — **the decoupled rail**: thirteen capacitors, their ESR, their
mounting loops, the source's R and L. At 50 MHz a bare plane pair between two points is hundreds of
ohms; the same rail with its decoupling is tens of milliohms. **465 Ω and 23 mΩ are both right.**

### Why the window made that unreadable

The map's caption reads

```
|Z| at 50 MHz from U1.VDD
```

and its readout reads

```
465.401 Ω at 50 MHz from U1.VDD — the 6.058 GHz node is at 0.12 of its own peak here
```

Neither says *plane pair*. The plot's marker reads `Z(1,1) Mag = 0.02342`. A reader sees two
impedances, one board, one frequency, one port name, and a factor of 20,000. The correct sentence
exists — **in `Notes`**, which is a list on a different tab that nobody reads while looking at a
picture.

### `R-rail21-1a` — the map says what it is, everywhere it says anything

`ImpedanceMapAt` (`RailRfViewModel.Plane.cs:107`), the hover readout in
`RailLayoutOverlay.ImpedanceReadoutAt`, the `\|Z\|` tab's own tooltip and every exported picture's
caption name the quantity: **the plane pair's own \|Z\|, with no parts on it.** One phrasing, defined
once, used by all of them — two spellings of one distinction is how they come to disagree.

### `R-rail21-1b` — the note moves onto the picture

The `Notes` sentence quoted above is promoted from the notes list to the map panel itself, beside
the colour bar. It is the single most important thing a reader of that picture needs to know and it
is currently the hardest thing in the window to find.

### `R-rail21-1c` — print both numbers together

Where a frequency sweep has been run, the map's readout **also states the rail's own \|Z\| at the map
frequency**, named. Two numbers, two names, one line:

```
plane pair alone   465.4 Ω
this rail          23.4 mΩ     (the parts, at 50 MHz)
```

That is the answer to the designer's question, delivered at the moment he asked it rather than in a
document. It costs one interpolation into a curve that is already computed.

### `R-rail21-1d` — the plot marker is named too

`Z(1,1) Mag` is a cube name, not an answer. The marker and the axis say which \|Z\| this is, in the
same vocabulary R-rail21-1a defines.

### `R-rail21-1e` — this is NOT a physics change

Explicit, because the temptation is real: nobody is to "fix" the map by adding the parts to it. The
map is of the plane pair on purpose, it is the only view of the cavity there is, and adding lumped
parts to a distributed cavity solve would produce a third quantity with no name at all.

---

## 2. `R-rail21-2` — two totals, both called the drop

### What is wrong

The Drop card reports the port's own drop — `48.368 mV below the source`. The Breakdown card lists
rows and computes each row's share against a **different** total:

```csharp
total += drop;                                   // over EVERY group on the rail
...
rows[i] with { ShareOfTotal = rows[i].DropV / total }
```
— `src/Engine/Pdn/PdnBreakdown.cs`

The comment there states the intent, and the intent is right:

> *"Shares are computed against the sum of the rows themselves, so they add to one exactly whatever
> the board is — a table whose percentages did not add up would be read as a missing row, which is
> the one reading this table must never invite."*

But the sum of the rows is **not** the drop at the port. It is the sum over every group carrying
current anywhere on the rail — which includes the return, and on a board with more than one loaded
port includes copper on a path the selected port never sees. The two numbers differ, both are
labelled in millivolts, and nothing says they are answering different questions.

### `R-rail21-2a` — the breakdown states its own total, by name

The card prints its total explicitly, and the total is **named for what it is** — the sum of every
group's drop on this rail — rather than left to be inferred from the percentages.

### `R-rail21-2b` — and says why it differs, when it does

Where the breakdown's total and the selected port's drop differ by more than display rounding, one
sentence says which is which and what the difference is made of. Where they agree — the ordinary
single-load board — nothing is printed, because a reconciliation note about two numbers that match
is noise.

### `R-rail21-2c` — establish the arithmetic before writing the sentence

The measured difference on the shipped example needs pinning down before anything is worded:
the README's published breakdown sums to ~49.3 mV against a 48.368 mV port drop, and the designer
saw 50.1. **Work out exactly which groups account for the gap** — the reference return is the
obvious candidate, since it is in the rows and is not in the source-to-port path — and write the
sentence from the finding, not from a guess. Record it in `src/Design/RESOLVED.md`.

---

## 3. `R-rail21-3` — the legend is unreadable

### What is wrong

`R-rail18-4` fixed the map legend's three labels **overlapping** on a small canvas — *"a box in DBU
with text in points"*. It did not fix their SIZE. In the designer's screenshot the colour bar's
caption is a grey smear; the numbers at its ends are barely a pixel tall.

### `R-rail21-3a` — a floor, in screen points

Legend text is drawn at the window's own minimum readable size and does not scale with the canvas.
A legend that shrinks with its picture is a legend that stops being one.

### `R-rail21-3b` — if it does not fit, drop text rather than shrink it

At a canvas size where the full caption cannot be drawn at the floor size, the caption is dropped
and the end labels kept — and at a size where even those will not fit, the legend is not drawn at
all. **Never scaled below the floor.** An unreadable legend is strictly worse than no legend: it
occupies the space where the answer would go and looks like a rendering fault.

### `R-rail21-3c` — the exported picture too

`RailGraphicExport` draws the same legend. The floor is in the renderer
(`src/Render/Renderers/RailMapRenderer.cs`), not in the window, so the export gets it by
construction rather than by a second fix.

---

## 4. Gate

`tests/Ui.Tests/RailRf/ImpedanceNamingTests.cs`,
`tests/Engine.Tests/Pdn/BreakdownTotalTests.cs`,
`tests/Ui.Tests/RailRf/MapLegendTests.cs`.

1. **The map caption names the plane pair.** Every surface in R-rail21-1a carries the phrase, and
   all of them get it from one constant. **Fails at HEAD.**
2. **The note is on the panel.** Present in the map panel's bound text, not only in `Notes`.
3. **Both numbers appear together.** With a sweep run, the readout states both, and the rail figure
   matches an interpolation of the curve at the map frequency to within the interpolation's own
   tolerance (R-rail21-1c).
4. **The map is still the plane pair's.** A regression guard for R-rail21-1e: the map's values are
   unchanged by this brief, bit for bit.
5. **A breakdown whose total differs prints the reconciliation; one that agrees does not.** Two
   synthetic boards, one of each (R-rail21-2b).
6. **The shares still sum to one.** `PdnBreakdown`'s own invariant, unchanged.
7. **The legend has a floor.** Render the map at 200 px, 400 px and 1200 px wide; assert the text
   size is identical at all three and at or above the floor (R-rail21-3a).
8. **It drops rather than shrinks.** At 120 px the caption is absent and no text is below the floor
   (R-rail21-3b).
9. **The export matches.** The exported picture's legend text metrics equal the on-screen one's at
   the same size (R-rail21-3c).

## 5. Scope

- **No physics change.** R-rail21-1e.
- **No change to how shares are computed.** R-rail21-2's fix is a label and a sentence; the
  arithmetic in `PdnBreakdown.Rank` is correct and its invariant is deliberate.
- **No new export format.** The legend fix reaches the export through the renderer.
