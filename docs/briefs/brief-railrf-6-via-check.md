# Brief 6 — the via current check: the worst via, not the average

**Series:** [railRF](brief-railrf-0-overview.md) · **Tag:** `R-rail6-n`
**Area:** `src/Design/Layout/Pdn/`, `src/Design/RailRf/` · **Depends on:** 5 · **Blocks:** nothing
**Design note:** [`railrf.md`](../design/railrf.md) §2.4 ("The via check"), §4.2, Q-17, Q-22

---

## 0. What this brief delivers

A flag on a layer transition carrying more current than its vias can take, with a **stated basis** and its
provenance on every row.

| Type | File | What it is |
|---|---|---|
| `PdnViaCurrentLimit` | `src/Design/Layout/Pdn/PdnViaCurrentLimit.cs` | The limit, computed from the barrel's own annulus, its span and a rise budget. |
| `PdnViaCheck` | `src/Design/Layout/Pdn/PdnViaCheck.cs` | Per-transition: the current in **each** via, the worst one, the limit, the count that would clear it. |
| `PdnViaFlag` | same | One flagged transition, with the basis that produced it. |

> **This is a rule with a stated basis. It is not a thermal model** (§2.7). Neither is current density,
> which §2.4 and Q-7 both put explicitly at step 2 and which this brief does not build.

---

## 1. `R-rail6-1` — the worst via, because vias do not share equally

§2.4:

> railRF knows the current in **each** via, not the average — vias in parallel do not share equally, and
> the one nearest the load routinely carries several times its share.

The mesh already knows this: brief 3 stamps each barrel as its own element on its own nodes, with no
special case for a group. What this brief does is **read the per-element currents brief 5 solved** and
group them by transition.

The flag is on the transition whose **worst** via exceeds the limit, and it names *the count that would
clear it.* A flag saying "this transition is over" is a complaint; a flag saying "six vias, worst carries
0.62 A against a 0.45 A limit, ten would clear it" is an instruction.

**Averaging is the defect this whole design exists to avoid**, and it is one line away at every step —
`group.Sum(i) / group.Count` reads entirely naturally and is wrong. Say so in the code.

---

## 2. `R-rail6-2` — the limit is COMPUTED, and the table is the default it opens on

This is the substance of Q-17's answer and §4.2 spends a page on why. Review's table:

| Drill | Current at roughly 10 °C rise |
|---|---|
| 0.2–0.3 mm, a signal via | 0.3–0.5 A |
| 0.4–0.5 mm | 0.7–1.0 A |
| 0.6–0.8 mm | 1.0–1.5 A |
| 1.0–1.2 mm | 1.5–2.5 A |

with a 0.3 mm drill **at 20 µm of plating** quoted separately at 0.8–1.0 A.

> **That separate figure is the most useful part of the answer, because it disagrees with the table's own
> 0.3 mm row by about a factor of two** — and the one term that differs between them is the **plating
> thickness**, which the table does not state and which is the only thing setting the barrel's conducting
> cross-section. A 0.3 mm hole plated to 20 µm has roughly twice the copper annulus of the same hole
> plated to 10 µm, and roughly twice the current. **A table indexed on drill size alone cannot express
> that.**

So:

- **railRF does not ship the table as the rule.** It computes the limit from the barrel's own annulus, its
  span and a **rise budget that is a setting**.
- **The table is what that setting opens on**, and it is drawn as a **sanity band** beside each flagged
  transition — so a computed limit that falls wildly outside the band is visible as such.
- The figures are **general engineering guidance rather than a standard held internally**, and the code
  says so where it defines them.

### `R-rail6-3` — the plating thickness, and Q-22

The plating thickness is the term the table hides and it is worth a factor of two. It comes from:

1. **The stackup's via entry** where one states it (`StackupLayer`'s wall thickness — the field brief 3's
   `PdnViaModel` already reads for barrel resistance). This is the good case and it costs nothing.
2. Otherwise **typed, and shown on the report**. Never defaulted silently.

Q-22 is open: nothing we have is known to state it. §4.2's own closing sentence is the thing to keep
visible — *"which is honest but means the one number the flag turns on is the one number nobody checked."*
So the flag prints the thickness it used, every time, and a typed one is marked as typed.

### `R-rail6-4` — every flag says which basis produced it

`Computed` (from geometry) or `Table` (drill-size fallback, where the geometry is unresolvable), for the
same reason every other number in this document carries its provenance. Two flags with different bases
side by side must not read identically.

---

## 3. `R-rail6-5` — a blind or buried span that cannot be resolved is REPORTED

Brief 3 `R-rail3-10`: v1 assumes through vias, reads a span declaration where one exists, and **reports
rather than assumes** when it meets spans it cannot resolve. That report reaches the via check too — a
transition whose span is unresolved gets **no flag and a note**, because a limit computed from an assumed
span is a number with no basis at all.

A missing flag and a note is honest. A flag computed from a guessed 1.6 mm span is not.

---

## 4. Tests — `tests/Ui.Tests/RailRf/PdnViaCheckTests.cs`

§7 gives both halves and the second is the point:

- **Symmetric group, against the closed form.** *n* identical vias equidistant from the source and the
  load split the current equally: each carries I/n, to under 1 %. This is a parallel-resistance
  calculation and needs no simulator.
- **Asymmetric group, against the mesh.** The same *n* vias with the load off to one side — **the point is
  precisely that the split is not equal.** Assert the ordering (nearest carries most), assert the worst is
  above I/n by a stated factor, and assert the flag fires on the worst rather than on the mean. The
  negative that makes it a real test: **replace the per-via read with the average and assert the test goes
  red.**
- **`R-rail6-2`**: at 0.3 mm and 20 µm plating the computed limit lands in 0.8–1.0 A, and at 0.3 mm and
  10 µm it lands near half that. Both of the note's own numbers, in one test, because the *disagreement*
  between them is the finding.
- **`R-rail6-2` the band**: every computed limit for a drill in the table's range is reported with the
  band, and a computed limit outside its band is reported as outside rather than clamped.
- **`R-rail6-3`**: a stackup stating a wall thickness is used and marked `Computed` from it; one stating
  none takes the setting and is marked typed. Neither path produces a silent default.
- **`R-rail6-1` the count**: a transition flagged at six vias reports the count that clears it, and
  re-solving with that count clears the flag. That round trip is what makes the number an instruction
  rather than an estimate.
- **`R-rail6-5`**: an unresolved span produces a note and no flag.

---

## 5. Scope

- **No current density field, no hot-spot map.** Explicitly step 2 (Q-7, §2.4). Review asked for it and
  agreed it comes after the flag.
- **No temperature anywhere else.** The rise budget is a setting on this check and nothing else in railRF
  reads it.
- **No thermal model.** §2.7.
- **No UI.** Brief 7 shows the flag count in the results panel and brief 8 draws the flagged transitions
  on the board. This brief produces `PdnViaFlag`s and draws nothing.

**On completion:** record findings in `src/Design/RESOLVED.md`. Never in a CLAUDE.md.
