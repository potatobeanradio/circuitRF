# Brief 10 — properties: values, tolerances, and where the tolerances come from

**Series:** [LVS](brief-lvs-0-overview.md) · **Tag:** `R-lvs10-n` · **Design note:** [`lvs.md`](../design/lvs.md) §6.4
**Area:** `src/Design/Layout/Lvs/LvsProperties.cs` (new), `src/Design/Layout/TechModel.cs`,
`examples/LVS/`
**Depends on:** 5, 6, 8 · **Blocks:** 15

---

## 0. What this brief delivers

Matched devices are checked for agreement about their values, within a tolerance that was
**measured off a real design rather than chosen** — *"I don't have default property tolerances,
you'll have to create a design to test it. We can tweak it later"* (owner, 2026-09-21).

---

## 1. `R-lvs10-1` — after topology, never before

**`R-lvs10-1a`** The property pass runs on the correspondence brief 7 produced. Comparing
parameters of devices that may not correspond produces noise proportional to the size of the
design, and the noise buries the topology finding that caused it.

**`R-lvs10-1b`** It compares the **reduced** devices (brief 6), because that is what corresponds.
A merged group's value is the summed one and the finding names the parts it was summed from.

**`R-lvs10-1c`** A device with no topology match gets no property findings at all. One fault, one
finding.

---

## 2. `R-lvs10-2` — compared only where both sides claim a value

**`R-lvs10-2a`** The layout side claims a value when the device is a **PCell**
(`PCellOrigin.Parameters` — resolved SI values) or when brief 14's recognition measured one. A
land-pattern instance claims **nothing** about resistance: the land pattern is shared by every
0402 on the board, so a value stored on it would be wrong for all but one.

**`R-lvs10-2b`** Where only one side claims, there is nothing to compare and it is **not a
finding**: `lvs.property.layout-silent` at **info**, said **once per device type**, not once per
device. Four hundred 0402s must not produce four hundred lines saying the obvious.

**`R-lvs10-2c`** Where neither claims, silence.

**`R-lvs10-2d`** A parameter present on one side and absent on the other **for a device type that
declares it** is different from `R-lvs10-2b` and is `lvs.property.missing` at warning — the
schematic asks for a `W` the layout's PCell does not carry, which means the two are not the same
generator.

---

## 3. `R-lvs10-3` — tolerance is per unit dimension, and it is printed

**`R-lvs10-3a`** Keyed on `UnitDimension`, which already exists on `CcellParameter`. One table,
in one place.

**`R-lvs10-3b`** Overridable per technology, in the `.ctech` beside `DrcRules`, because a PCB 1 %
part and an MMIC thin-film resistor are not held to the same number.

**`R-lvs10-3c` Every finding prints both values AND the tolerance that was applied.** Never the
word "mismatch" alone. *"R3: schematic 100 Ω, layout 150 Ω, tolerance 1 %"* is actionable; a
wrong default is then visible rather than latent, which is what makes `R-lvs10-5` safe.

**`R-lvs10-3d`** Integers, enumerations, model names and strings are **exact**. There is no
tolerance on a model name and a near-miss there is a different model.

**`R-lvs10-3e`** Comparison is **relative** for extensive quantities and **absolute in DBU** for
geometric ones — one DBU on a length, because DBU is the database's own resolution and a
sub-DBU difference cannot exist.

---

## 4. `R-lvs10-4` — derived parameters are compared as derived

**`R-lvs10-4a`** `PCellOrigin.IsComputed` already marks the parameters a generator **derives from
its own geometry** rather than reads — a MIM cap's C from its own w and l, a thin-film resistor's
R from its own dimensions.

**`R-lvs10-4b`** Such a parameter **cannot disagree with its own geometry**. It can disagree with
the *schematic's* request, and that is a real design error worth naming precisely:

> *C2: the layout's geometry gives 1.82 pF; the schematic asks for 2.0 pF.*

not *"C2 value mismatch"*. The two sentences send the designer to different places — one to the
artwork, one to the drawing.

**`R-lvs10-4c`** `PCellOrigin.IsUnread` marks a parameter the generator never read, so **nothing
about the geometry depends on it**. A difference there is `lvs.property.unread-differs` at
**info**: the parameter is still the user's to set (a model name, a multiplier) and the artwork is
not wrong.

---

## 5. `R-lvs10-5` — merged multiplicity

**`R-lvs10-5a`** Four fingers in the layout against a schematic device declaring `Nf=3` (or `M=3`)
is a **property** finding — `lvs.property.multiplicity` — not a topology mismatch. The merge is
what made the comparison possible; judging it is here.

**`R-lvs10-5b`** Where the schematic declares no multiplicity parameter at all,
four-against-one is `lvs.reduce.multiplicity-unstated` at **warning**, naming the four devices.
That is a real and common under-specification and the designer should know.

**`R-lvs10-5c`** Multiplicity is an integer and therefore exact (`R-lvs10-3d`).

---

## 6. `R-lvs10-6` — the tolerances are MEASURED, and the derivation is recorded

This is the deliverable the owner asked for. The numbers come out of `examples/LVS/Bias tee/`
(brief 5 `R-lvs5-3c`), which is the only fixture with parameters on the layout side.

**`R-lvs10-6a` Procedure**, run once and written down:
1. Extract the cell. For each PCell device take the schematic's requested value and the layout's
   resolved value.
2. Record the **observed spread** per unit dimension across the whole correct design.
3. Set each default to a round number **comfortably above** the observed spread of a correct
   design and **comfortably below** the smallest fault anyone would want caught.
4. Record **both bounds** as well as the chosen number.

**`R-lvs10-6b` Where the observed spread is zero, the tolerance is zero.** A derived parameter
computed by the same generator on both sides agrees exactly, and a non-zero tolerance there hides
a whole class of error for no benefit.

**`R-lvs10-6c` The numbers and both bounds go in the completion note**, not only in the code. A
tolerance whose derivation is not written down is a magic number by the next release, and nobody
will dare change it because nobody will know what it was protecting.

**`R-lvs10-6d` They are provisional and the note says so.** One table, overridable per technology,
printed on every finding. Tweaking later is a one-line change with a test that says what the change
costs.

**`R-lvs10-6e` No tolerance is invented before it is measured.** If a unit dimension has no
representative in the fixture, its default is **exact** and a finding says the tolerance was
never established — which is honest, and is what makes the gap visible enough to close.

---

## 7. Gate

`tests/Ui.Tests/Lvs/PropertyTests.cs`, plus brief 5 gate 10.

1. **The derivation is reproducible.** A test recomputes `R-lvs10-6a`'s spread over the correct
   MMIC cell and asserts each shipped default sits **between the two recorded bounds**. A later
   PCell change that widens the spread past its tolerance **fails here, loudly**, instead of
   silently making LVS pass a real error (`R-lvs10-6c`).
2. **Brief 5's fault F6** — R3 re-pointed from 100 R to 150 R — produces `lvs.property.mismatch`
   naming **both values and the tolerance** (`R-lvs10-3c`).
3. **A value inside tolerance is silent**, and one just outside is a finding. Two rows straddling
   the boundary, not a ladder.
4. **A board with no layout-side values** produces `lvs.property.layout-silent` **once per device
   type**, not once per device — assert the count on a fixture with 40 identical parts
   (`R-lvs10-2b`).
5. **A derived parameter reports the derived sentence**, not the generic one (`R-lvs10-4b`).
   Assert on the diagnostic **id**, which differs.
6. **An unread parameter differing is info** (`R-lvs10-4c`).
7. **Multiplicity:** four merged against `Nf=4` is silent; against `Nf=3` is
   `lvs.property.multiplicity`; against a device declaring none is
   `lvs.reduce.multiplicity-unstated` naming all four (`R-lvs10-5`).
8. **A model name differing by one character is a finding** — no tolerance on strings
   (`R-lvs10-3d`).
9. **A geometric parameter differing by one DBU is a finding; by zero is not** (`R-lvs10-3e`).
10. **A technology override changes the verdict** and the printed tolerance (`R-lvs10-3b`).
11. **No property findings on an unmatched device** (`R-lvs10-1c`).
12. **A unit dimension with no fixture representative defaults to exact** and says so
    (`R-lvs10-6e`).

## 8. Scope

- **No new parameter model.** `CcellParameter`, `UnitDimension` and `PCellOrigin` already carry
  everything this needs.
- **No unit conversion of its own.** Values are resolved SI on both sides by the time they arrive.
- **No tolerance on topology.** A property difference never suppresses or creates a topology
  finding, and the reverse.
- **No per-parameter tolerance.** Per unit dimension, per technology. A per-parameter table is a
  configuration surface with no evidence behind it yet.
