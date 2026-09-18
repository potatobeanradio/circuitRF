# Brief 16 — A/B: what the layout did to the PDN, and which parts can come off

**Series:** [railRF](brief-railrf-0-overview.md) · **Tag:** `R-rail16-n` · **Phase:** P3
**Area:** `src/Engine/Pdn/`, `src/Design/RailRf/`, `src/Ui/RailRf/` · **Depends on:** 12 (13 enriches it)
**Design note:** [`railrf.md`](../design/railrf.md) §2.5, §2.1 Q3, §2.6, §7, Q-19

---

## 0. What this brief delivers

The workflow that motivated the whole tool. §2.5: *"This is Q3 and it is the workflow that motivated the
whole tool."*

| Type | File | What it is |
|---|---|---|
| `RailComparison` | `src/Design/RailRf/RailComparison.cs` | Two documents, matched, with everything it could not match named. |
| `PdnDelta` | `src/Engine/Pdn/PdnDelta.cs` | Δ\|Z\| in dB versus frequency, and where it moved most. |
| `RailComparisonReport` | `src/Design/RailRf/RailComparisonReport.cs` | The short report, drawn by brief 9's one render function. |

### `R-rail16-1` — what this is FOR, and rev 3 guessed it wrong

Q-19 closed it, and the correction changes the shape of the feature:

> The comparison weighs what the *layout* did to the PDN and, through that, **which parts could come off the
> board** — **it is not a load-current study.** The DC drop analysis is a layout-review step of its own, done
> once the topology is settled, and review keeps the two apart.

So: **the impedance half is the point, the DC half is supporting evidence**, and the DC-only early version
rev 3 proposed *"is not worth building."* That is why this lands with P2a rather than ahead of it.

---

## 1. `R-rail16-2` — matching: by name, by refdes, by part number — and never by proximity

§2.5:

> railRF matches the two by **net name** where it can and by **refdes** for the parts, falling back to **part
> number** for the models. **Where it cannot match something, it says so and asks — it does not pair things by
> proximity or by guessing.**

Three rules, in order, and a fourth that is a prohibition:

1. Nets by **net name**.
2. Parts by **refdes**.
3. Models by **part number**, where a refdes did not match.
4. **Nothing by proximity, by value, by footprint or by any geometric similarity.** A part that matches
   nothing is reported as unmatched and the user resolves it.

### `R-rail16-3` — sources and loads pair by refdes and pin, and this is why brief 1 anchored them there

§2.5:

> **Sources and loads pair by refdes and pin** for the same reason §2.2 anchors them there: **pairing by
> coordinate is exactly what a re-layout breaks, and a comparison whose ports moved is a comparison of
> nothing.**

This is the single reason brief 1 `R-rail1-1` made `RailPortAnchor` a refdes and a pin with a coordinate as
the fallback. A comparison where either side uses the coordinate fallback for a port is a comparison that
**says so on the report** — not a refusal, because sometimes it is all there is, but never silent.

---

## 2. `R-rail16-4` — the six outputs, in the order §2.5 puts them

| # | Output | Note |
|---|---|---|
| 1 | **Both impedance curves on one plot**, with the target mask and the aggressor lines | *The reference passes; does yours?* |
| 2 | **A delta trace** — Δ\|Z\| in dB versus frequency | with the frequencies where it moved most called out |
| 3 | **A per-part comparison table**: the same capacitor's mounting inductance on both boards | *0.4 nH on the reference and 1.1 nH on yours because its return via moved 4 mm* — needs brief 13 |
| 4 | **Both removal rankings** | **the output Q-19's answer puts first** |
| 5 | **Both DC breakdowns**, element by element | supporting evidence — *though on a compact redesign it is often where the first surprise is* |
| 6 | **Both mode lists** | where brief 15 has run |

### `R-rail16-5` — the removal rankings are read as a PAIR, and that is a new finding shape

§2.5's fourth bullet is the one that is not simply "the same table twice":

> a part that **earns its place on the reference and earns nothing on yours** has been **shadowed by the
> re-layout**, and a part that **earns nothing on both** can come off **both boards.**

Two different findings out of one pairwise comparison, and both are actionable. The report states them as
findings, by refdes, rather than leaving the reader to diff two tables.

---

## 3. `R-rail16-6` — the output is a REPORT, not just a plot

§2.5's closing paragraph is the specification for it:

> *these three parts got worse mounting, this trace section costs 61 mV more than the reference's, and the net
> effect is a 6 dB mask violation at 7.1 MHz — on the converter's fundamental — that the reference did not
> have.*

Three sentences: what changed, what it cost, and what it broke — **with the coincidence check's own answer
inside the third.** A report that lists numbers without saying which of them is the finding has not done the
job the tool exists for.

It is drawn by **brief 9's one render function**, which is also what `Report ▸` and the headless report call.
§11.7: *"there is one route from an overlay to a page and not two."*

### `R-rail16-7` — the DC half reads as supporting evidence and still carries its own surprise

§2.5: *"the same schematic, 40 mm of extra 0.3 mm trace, and 60 mV that were not in the budget."* Present it
below the impedance answer, not above it — Q-19's ordering — but do not shrink it, because §2.8's correction
is that the copper is second only to the aged battery on these boards.

---

## 4. `R-rail16-8` — comparing two different outlines on equal terms

Brief 1 `R-rail1-6`'s **Infinite** reference extent exists partly for this: §2.2 says it is *"useful as an
upper bound and for comparing two different outlines on equal terms."* A comparison where the two sides used
**different** reference extents is meaningless and must be refused, naming both — the one place in this brief
where a mismatch is a refusal rather than a report, because there is no honest way to present the difference.

---

## 5. Tests — `tests/Ui.Tests/RailRf/RailComparisonTests.cs`

### The gate from §7 — the answer is CONSTRUCTED, not solved for

> **The A/B report** against **two synthetic boards differing in exactly one known way**, where the correct
> answer is **constructed rather than solved for**.

That is the design of the whole test file. Build pairs:

| Pair differs in | Expected finding |
|---|---|
| one trace's width | one DC breakdown row moves, by the closed-form amount; the impedance barely moves |
| one part's return-via distance | that part's mounting inductance moves; the Δ trace moves at that part's own band |
| one part deleted | that part's removal-ranking row is absent; the delta shows its band |
| a part shadowed by a re-layout | `R-rail16-5`'s first finding fires, by refdes |
| nothing at all | **the delta is identically zero and the report says the boards are equivalent** |

That last row is the one that catches the whole class of defect where the comparison is doing something — a
match that silently pairs the wrong things produces a non-zero delta on identical inputs.

### The rest

- **`R-rail16-2`**: a part present on one board only is reported **unmatched**. The negative: place an
  unmatched part 0.2 mm from a matched one on the other board and assert it is **still unmatched** — proving
  nothing pairs by proximity.
- **`R-rail16-3`**: a port anchored by coordinate on either side is reported as such.
- **`R-rail16-5`**: both findings, each on a board pair constructed to produce exactly one of them.
- **`R-rail16-8`**: mismatched reference extents refuse, naming both.
- **`R-rail16-6`**: the report renders through brief 9's function, and a source scan finds no second render
  path.

---

## 6. Scope

- **No optimisation, no suggestion.** The comparison reports; it does not propose a placement.
- **No three-way comparison.** Two designs. A third is a different feature and the note does not ask for one.
- **No cross-rail comparison.** Rail `+1V8` against rail `+1V8`. Comparing two different rails is not a thing
  this answers.
- **No new plot type.** Both curves and the delta are traces on a `PlotControl` in rectangular mode.

**On completion:** record findings in `src/Design/RESOLVED.md` and `src/Ui/RESOLVED.md`. Never in a CLAUDE.md.
