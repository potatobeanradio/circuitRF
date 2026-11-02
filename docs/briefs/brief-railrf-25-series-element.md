# Brief 25 — a series element on the rail: the ferrite that has nowhere to live

**Series:** [railRF](brief-railrf-0-overview.md) · **Tag:** `R-rail25-n` · **Phase:** feature, model change
**Area:** `src/Design/RailRf/RailPart.cs`, `RailSpec.cs`, `RailPartResolver.cs`, `RailDocumentIo.cs`,
`src/Design/RailRf/PdnSweep.cs`, `RailDcRun.cs`, `src/Design/Layout/Pdn/PdnRailRegions.cs`,
`src/Ui/RailRf/RailPartRowViewModel.cs`, `RailRfViewModel.Parts.cs`
**Depends on:** 1-18 · **Blocks:** [footprint 5](brief-footprint-5-power-rail-example.md) R-fp5-4
**Found by:** a first-time designer's pass, 2026-09-20 — *"to emulate for example a ferrite bead…
series. the ferrite is to kill the unwanted frequency content"*

---

## 0. What this brief delivers, and why it is the largest in the set

Every other brief in this round is a window-level fix. **This one changes the model.**

The designer asked for a series resistor on the rail, to stand in for a ferrite bead, and then for
the richer version: *"you can also split the path in 2 similar loads with a few caps, one using a
ferrite bead and the other with an ideal resistor instead."*

railRF cannot represent either. Two reasons, both structural:

1. **`RailPart` models shunt decoupling only** — capacitance, ESR, ESL, mounting loop
   (`src/Design/RailRf/RailPart.cs`). There is no series row.
2. **The rail is ONE NODE in the frequency model.** `PdnSweep`'s own header says so: *"the rail is
   ONE node here and every part, every source and every load hangs off it… every observation port
   reads the SAME curve."* A series element needs a before and an after, and there is no before and
   after.

The README already tells the truth about this and will need rewriting when this lands:

> *"The series FET and the ferrite are the source's R and L, not rows of their own. In the lumped
> model the source is the whole branch feeding the rail node, and this is where a series part
> lives."*

That is a fair v1 answer for a part at the head of the rail. It is no answer at all for a ferrite
in the middle of one, which is where ferrites go.

---

## 1. `R-rail25-1` — a series element is a part with two rail-side terminals

### `R-rail25-1a` — `RailPart.Connection`

`Shunt` (the default, every existing row) or `Series`. Persisted; absent reads as `Shunt`, so every
existing `.crail` is unchanged.

### `R-rail25-1b` — its model is an impedance over frequency, and the machinery exists

A ferrite is `R(f) + jωL(f)`, a resistor is `R`, and a published curve is a Touchstone file.
**`RailSourceModel` already models exactly this** — `RailSourceBasis.Rl` or `Measured`, with
`ImpedanceAt(double)` returning a `Complex` and NaN outside a measured file's band rather than
clamping (`src/Design/RailRf/RailSourceModel.cs:70`).

So a series part's model is that shape, reached through the same `RailMeasuredPart` arithmetic.
**Not a second impedance-over-frequency type**, and not a new Touchstone path.

### `R-rail25-1c` — the honest sentence travels with it

A ferrite is the part in the whole document a lumped R-L most misrepresents: its impedance is
strongly bias-dependent and its datasheet curve is at zero DC bias. An R-L-modelled ferrite at
350 mA is optimistic by a large factor and the curve looks entirely ordinary — **the same failure
`RailSourceBasis`'s own header describes for a converter near loop crossover**, and it gets the
same treatment: a sentence on the result, not a log line. Where a measured curve is supplied, the
sentence goes away.

---

## 2. `R-rail25-2` — the rail gains a second node, and the artwork says where the boundary is

### The problem

A series element partitions the rail. Everything upstream of it sees one impedance; everything
downstream sees another. Which parts and which loads are on which side is a **fact about the
board**, and asking a user to type it for thirteen capacitors would be both tedious and wrong the
first time somebody moves a part.

### `R-rail25-2a` — partition by the walk that already exists

`PdnRailRegions.Walk` walks everything galvanically connected to a seed, through vias and across
layers. Cut the rail at the series element's two pads and walk from each: **the rail falls into two
galvanically separate regions**, and every part, load and observation port lands in one of them by
where its own pads are.

That is a measurement off the artwork, in the same currency as the mounting inductances Q2 already
reads off it. No typing, and it follows a re-layout automatically.

### `R-rail25-2b` — a cut that does not separate is refused

If cutting at the element's pads leaves one region, the element is **bridged** — there is copper
around it — and it is not in series with anything. Refuse with that sentence, naming the element,
rather than modelling a series part that the board shorts out. This is a real and common layout
error and it is exactly the kind of thing railRF exists to find.

### `R-rail25-2c` — more than one series element is refused in v1

Two series elements make three or more sections and a topology that may not be a chain. **v1 is one
series element per rail, and a second is refused by name** with what to do (put it on a rail of its
own). A refusal is a scope boundary a user can see; a wrong answer is not.

Note that this still satisfies the designer's richer scenario, which is **two rails** — one through
a ferrite, one through an ideal R — not two elements on one rail.

### `R-rail25-2d` — with no artwork, it is typed

§6 makes P1 artwork-optional and a rail may have none. There, the partition is stated: each part
and load row says which side it is on, defaulting to downstream, which is where decoupling goes.
The same two-state field; a different way of filling it in.

---

## 3. `R-rail25-3` — the DC answer

A series element carries the load current, so unlike a decoupling capacitor **it appears in the
drop**.

**`R-rail25-3a`** Its DC resistance is a row in the breakdown, ranked with everything else. On the
shipped example a ferrite's DCR would sit near the top — the README's own arithmetic has the
source's 60 mΩ at 42 % of the drop, and a ferrite is a good part of that 60 mΩ today.

**`R-rail25-3b`** An element with no stated DCR is **not zero**. It is unstated, and the drop answer
says the rail's DC total is a lower bound rather than printing a number that quietly omits it.
`RailPart`'s existing treatment of a null mounting inductance is the precedent: *null is honest
rather than zero*.

**`R-rail25-3c`** Unmounting one — [brief 23](brief-railrf-23-mount-and-unmount.md) — **opens the
rail**. That is refused with the reason, not solved as an open circuit. R-rail23-1e names this case
and defers to here.

---

## 4. `R-rail25-4` — what the reader is shown

**`R-rail25-4a`** The parts table marks series rows distinctly. A series 1 Ω and a shunt 1 Ω do
opposite things and a table that spells them the same is a table that will be misread.

**`R-rail25-4b`** The impedance curve is per observation port again, because with a series element
the ports genuinely differ. `PdnSweep`'s note — *"Every observation port on this rail reads the same
curve. That is P1's lumped model rather than a defect"* — is **conditional from now on**: true
without a series element, false with one, and printing it in the second case would be a false
statement about the model.

**`R-rail25-4c`** The board shades the two sections differently on the copper map, so *which side of
the ferrite am I on* is answerable by looking. That is the same question the class map already
answers for trace-versus-mesh, through the same overlay.

---

## 5. Gate

`tests/Design.Tests/RailRf/SeriesElementTests.cs`,
`tests/Ui.Tests/RailRf/SeriesElementWindowTests.cs`.

1. **A closed-form oracle first.** Source R-L, one series R, two shunt caps on the downstream side,
   analytic Z(f) at both ports — and the two differ, by the series R, at every frequency. **Build
   the oracle by hand and not from another railRF path.** This is the test that says the second
   node exists at all.
2. **The partition comes off the artwork.** A synthetic board with a series pad pair and parts on
   both sides: cutting at the element separates them exactly as placed (R-rail25-2a).
3. **A bridged element is refused.** Add copper around it; the walk yields one region and the run
   refuses by name (R-rail25-2b).
4. **A second series element is refused by name** (R-rail25-2c).
5. **The typed route works with no artwork** (R-rail25-2d).
6. **It appears in the drop.** A series DCR is a breakdown row, ranked with the rest
   (R-rail25-3a).
7. **An unstated DCR is not zero.** The drop answer states it is a lower bound (R-rail25-3b).
8. **Unmounting a series element is refused** (R-rail25-3c).
9. **The same-curve note is conditional.** Present without a series element, absent with one
   (R-rail25-4b). **This one will be missed** — it is a sentence that is correct today and becomes
   a lie, and nothing fails when a lie is printed.
10. **Every existing document is unchanged.** Run the shipped example before and after this brief
    and compare the whole `DataSet` bit for bit. A model change that moves an existing answer has
    done something it was not asked to.

## 6. Scope

- **One series element per rail.** R-rail25-2c.
- **No bias-dependent ferrite model.** R-L or a measured curve, with the honest sentence
  (R-rail25-1c). A bias-dependent model needs bias-swept data nobody will have.
- **No new impedance type.** R-rail25-1b — `RailSourceModel`'s shape, reused.
- **No third section.** If the topology needs one, it needs two rails.
