# Brief 11 — the parts over frequency: ESR, derating, and the word *indicative*

**Series:** [railRF](brief-railrf-0-overview.md) · **Tag:** `R-rail11-n` · **Phase:** P1
**Area:** `src/Design/RailRf/` · **Depends on:** 2 · **Blocks:** 12
**Design note:** [`railrf.md`](../design/railrf.md) §2.2 ("The parts"), §9, Q-11, Q-12, Q-15

---

## 0. What this brief delivers

The arithmetic that turns a library row or a vendor file into an element the mesh can carry over frequency —
and the **marking** that keeps a defaulted number from reading like a measured one.

| Type | File | What it is |
|---|---|---|
| `RailPartModel` | `src/Design/RailRf/RailPartModel.cs` | The resolved element: C, L, ESR, each with its provenance. |
| `RailPartResolver` | `src/Design/RailRf/RailPartResolver.cs` | Library row → model, with the file overriding the row. |
| `RailDerating` | `src/Design/RailRf/RailDerating.cs` | Capacitance at the rail voltage, from a bias curve. |
| `RailEsrDefaults` | `src/Design/RailRf/RailEsrDefaults.cs` | Dissipation factor per dielectric class, and where it also serves the board's tan δ. |

Brief 2 built the **representation** of all of this. Brief 11 computes it.

---

## 1. `R-rail11-1` — four ways a part can be modelled, and the file wins

§2.2, and Q-11 closed it as *both*:

| Form | What it gives | Notes |
|---|---|---|
| **A library row** — part number, C, f₀ | C exactly; **L derived** as `1/((2πf₀)²C)` | The common case. **Carries no ESR.** |
| **A Touchstone file** — the vendor's own `.sNp` | C, L **and a real ESR**, all the part's own | **Recommended**, and already built — the shunt-through relation is in `RfCore.Data.PassiveMetrics`. |
| **An R-L-C triple** | all three, typed | |
| **A SPICE/equivalent-circuit subcircuit** | placed as an ordinary circuitRF subcircuit | |

**The file overrides the row**, and the parts table reports which won (brief 2 `R-rail2-11`, brief 7
`R-rail7-9`).

### `R-rail11-2` — the Touchstone path is already built and must not be rebuilt

`PassiveMetrics.Impedance(full, z0, mode, portA, portB)` and `PassiveMetrics.SelfResonance(freqs, z)` already
read a part's impedance through the shunt-through relation, and `TouchstoneHealth` already says whether a
vendor file is trusted and why. `SnpModel` already stamps one into a netlist. **This brief writes none of
that** — it calls them, and it records that the ESR came out `Measured`.

---

## 2. `R-rail11-3` — ESR: the class default is the BASIS, not a stopgap

Q-15 is unusually blunt and its wording is the requirement:

> **there is no per-part ESR figure to be had, so a dissipation-factor default per dielectric class *is* the
> basis rather than a stopgap**, and every peak height computed from one is marked **indicative** wherever it
> appears.

Why it matters, from §2.2: *"With C and f₀ alone, railRF can place a resonance but not size it"* — **ESR sets
the depth of the minimum and the height of every anti-resonance peak.**

So:

- A dissipation factor per dielectric class, stated as a table with its own comment saying it is general
  engineering guidance rather than a standard held internally (the same wording brief 6's via table carries).
- `ESR = DF / (2π·f·C)`, evaluated at the frequency in question.
- **A part with no dielectric class gets no default** (brief 2 `R-rail2-5`: the parse leaves it null and it
  stays null). It is counted and reported, not given X7R's number.

### `R-rail11-4` — *indicative* is carried to five places, and §9 says why

§9:

> **a mask margin in dB computed from an indicative peak looks exactly as authoritative as a real one.** Every
> such margin is marked wherever it appears — part row, plot, table, export provenance — and **that marking is
> now load-bearing rather than temporary.**

Five places, and the flag has to survive the whole chain to reach them: the **part row** (brief 7), the
**plot** (brief 12), the **anti-resonance table** (brief 12), the **mask-margin readout** (brief 12), and the
**provenance of every export** (brief 10).

A flag that is computed here and dropped at the `DataSet` boundary reaches none of them. Carry it on the
result, not in a log line.

### `R-rail11-5` — the same per-class figure serves the board dielectric

Q-15's second half: where the stackup states no tan δ of its own, the same per-class dissipation factor serves
the board dielectric, **and is flagged the same way.** That matters more than it sounds: §2.2 names tan δ as
one of the two stackup numbers most often wrong, and *"it sets how sharp the cavity resonances are, which is
the difference between a 6 dB bump and a 20 dB one."*

### `R-rail11-6` — the headline count

§9: *"the parts table's count of **how many parts are modelled from a file** is a headline number and not a
detail."* Expose it as a first-class query on the resolver, beside the bias-curve coverage count from brief 2
`R-rail2-10`. Brief 7 puts both on the status strip.

---

## 3. `R-rail11-7` — derating is CORRECTED, and coverage is the residual risk

Q-12 closed it as *correct it*:

> The rail's voltage is known, and capacitance-versus-bias curves are available. So the part library carries a
> bias curve per part number where one has been obtained, **railRF applies it at the rail voltage**, and the
> parts table shows **marked and derated side by side.** Where there is no curve it warns and uses the marked
> value, and says so on the result.

§9 names the size of the error and it is not small:

> a 10 µF 0402 X5R can be **under 2 µF** at its rated voltage. A PDN answer computed with the marked value is
> wrong by a factor of several in exactly the band the bulk capacitors own, and it is wrong **optimistically**.

And the residual risk, which is this brief's real job:

> a library that is **only partly** populated with curves produces a result that is **partly derated**, and
> that is **worse than either extreme unless it is visible.** Every part row shows marked, derated and which
> it used, and the result carries a count of parts with no curve.

So `RailDerating.Apply` returns a triple — marked, derated, and which was used — never a single number. A
function returning one number is a function whose caller cannot show the other two.

---

## 4. `R-rail11-8` — mounting inductance: typed in P1, computed in P2a

§6: P1 is **artwork optional** — mounting inductances may be typed. Brief 13 computes them from the actual via
geometry.

So `RailPartModel` carries `MountingInductanceH` with its own provenance (`Typed` | `ComputedFromGeometry`),
and P1 fills it from the document. That is not a placeholder: a typed mounting inductance is a legitimate
input for a board whose artwork has not arrived, and §2.2 lets a user override a computed one anyway.

The typical range is worth carrying in the field's doc comment because it is the sanity check a user has:
**0.3–1.5 nH**, dominating above roughly **50 MHz**, *"and it is the thing your form factor change actually
altered."*

---

## 5. `R-rail11-9` — the source model, and what it does not claim

Q-5 closed the source as *assume a battery*:

- **R-L is the v1 source model**, with **R swept across cell life** — ohms to hundreds of ohms.
- A published output-impedance curve is accepted **as a Touchstone file like any other part**, where one
  exists.
- Where the source is a converter modelled as R-L, **railRF states that the answer near the loop crossover is
  optimistic** rather than quietly producing a monotonic curve that misses the peak.

That last clause is a sentence on the result, not a log line, and it is the same shape as `R-rail11-4`'s
*indicative* marking: the honest statement has to travel with the number.

---

## 6. Tests — `tests/Ui.Tests/RailRf/RailPartModelTests.cs`

- **`R-rail11-1`**: the note's two rows — 1 µF at 5.31 MHz → 898 pH; 33 nF at 39.1 MHz → 502 pH. (Brief 2
  tests the reader's derivation; this tests that the *model* carries it.)
- **`R-rail11-2`**: a part with an attached `.s2p` resolves C, L and ESR from the file, and reports
  `Measured`. The row's own C is not used.
- **`R-rail11-3`**: a class-default ESR at a stated DF and C matches `DF/(2πfC)`. A part with **no class**
  gets **no ESR** and is counted.
- **`R-rail11-4`**: the indicative flag survives from the part model to the result object — asserted at the
  `DataSet` boundary, which is where it would be dropped.
- **`R-rail11-7`**: a 10 µF X5R with a bias curve at its rail voltage derates, and the result carries
  **marked, derated and which was used** — all three, asserted separately. A part with no curve uses marked,
  warns, and increments the count.
- **`R-rail11-5`**: a stackup with no tan δ takes the class figure and the result is flagged.
- **`R-rail11-6`**: over a mixed library, the file-modelled count and the no-curve count are both correct.
- **`R-rail11-9`**: the life sweep produces one result per R; a converter-as-R-L result carries the
  optimistic-near-crossover statement.

Per the standing rule on minimal tests: **one test per claim.** Do not table-drive every dielectric class —
two rows straddle the behaviour and the rest is the same arithmetic.

---

## 7. Scope

- **No sweep, no mask, no ranking.** Brief 12.
- **No computed mounting inductance.** Brief 13.
- **No new `ComponentModel`.** `CapacitorModel`, `SeriesRlcModel`, `SnpModel` and `BeadModel` cover all four
  forms.
- **No cost data and no minimum-cost search.** Q-4: a later feature, not v1.

**On completion:** record findings in `src/Design/RESOLVED.md`. Never in a CLAUDE.md.
