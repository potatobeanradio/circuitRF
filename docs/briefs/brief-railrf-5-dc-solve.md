# Brief 5 — the DC solve: the drop, the breakdown, and the rail chain

**Series:** [railRF](brief-railrf-0-overview.md) · **Tag:** `R-rail5-n`
**Area:** `src/Design/RailRf/`, `src/Engine/Pdn/` · **Depends on:** 3, 4 · **Blocks:** 6, 10, 12, 16
**Design note:** [`railrf.md`](../design/railrf.md) §2.4 ("At DC"), §2.8, §4.4, §7

---

## 0. What this brief delivers

The answer P0 exists for. §6: *"If only one phase is ever built it should be P0 … P0 answers a question
these designs have, on artwork that always exists, with an oracle that is arithmetic rather than
opinion."*

| Type | File | What it is |
|---|---|---|
| `RailDcRun` | `src/Design/RailRf/RailDcRun.cs` | Runs the rail set in `RailOrder`'s order, one extraction and one solve per rail. |
| `RailDcResult` | `src/Design/RailRf/RailDcResult.cs` | Node voltages, branch currents, the per-port drop, and the provenance. |
| `PdnBreakdown` | `src/Engine/Pdn/PdnBreakdown.cs` | §2.4's ranked per-element table. Arithmetic over a solved netlist; no domain types. |

### `R-rail5-1` — the solve is `MnaSystem`, and nothing here is new

§4.4: assemble one sparse MNA system and solve via CSparse's LU — **the same numerical layer every other
circuitRF analysis uses.** At ω = 0 the system is real, symmetric and positive-definite and the same code
path solves it far faster.

So `RailDcRun` builds the `ElaboratedNetlist` (brief 3 or 4), hands it to the existing machinery, and
reads the answer. It writes no assembly code, no solver, and no result type of its own beyond the
domain-shaped `RailDcResult` — the numeric result is a `DataSet` like every other analysis's.

---

## 1. `R-rail5-2` — the drop map is DATA here and a picture in brief 8

§2.4:

> **The drop map.** The artwork itself, coloured by node voltage on a cold-to-hot scale, from the source
> to the load. **This is the headline of the DC mode and it is a picture, not a table**: where the colour
> changes fastest is where the drop is, and a designer reads that in a second.

This brief produces the **field**: a voltage per node, and `PdnNetlist.NodeCells` already maps node → cell.
Brief 8 draws it and brief 8 owns the colour scale. What brief 5 owns is that the field is complete — a
node with no voltage is a hole in the picture, and a hole in a heat map reads as a value rather than as an
absence.

A cursor reads out the absolute voltage anywhere on the net (brief 8), which means the field has to be
**interpolable between cells** rather than only sampled at them. State the interpolation here, once, so
the window and the headless report cannot differ about it.

---

## 2. `R-rail5-3` — the ranked breakdown, and why a total is not the output

§2.4:

> *"This 42 mm run of 0.3 mm inner-layer copper is 38 % of your drop"* is a finding; *"the drop is
> 180 mV"* is not.

One row per element on the path — the source, each series part, each trace section, each via group — with
its resistance, its current, its drop, and its share of the total. **Ranked.**

```csharp
public sealed record PdnBreakdownRow(
    string Label,        // from PdnNetlist.Origins: "42 mm of 0.3 mm inner copper, L3", "Q1 protection FET"
    double ResistanceOhms,
    double CurrentA,
    double DropV,
    double ShareOfTotal);
```

### `R-rail5-4` — sections are AGGREGATED to what a designer can act on

A mesh path is thousands of cell edges. A breakdown listing thousands of rows is a breakdown nobody reads.
So rows are aggregated by **origin**: contiguous cells belonging to one trace section on one layer become
one row, a via group becomes one row, a part is one row.

`PdnNetlist.Origins` is what makes this possible and is the reason brief 3 carries it. **An aggregation
that loses which layer a section was on has lost the finding** — §2.6's worked example turns on exactly
that: *"70 mm of 0.2 mm copper **on L3** is 347 mΩ and 42 mV on its own."*

### `R-rail5-5` — the copper is near the TOP of that table, not the bottom

This is the correction rev 3 of the note exists for, and it is worth carrying into the implementation's
own comment because rev 2's assumption is the intuitive one:

> **The copper is not a rounding error on these boards; it is second only to the aged battery and it is
> comparable with the series semiconductor.**

Sheet resistance is **0.49 mΩ/square at 1 oz and 0.99 mΩ/square at 0.5 oz**, and a 50 mm run of 0.3 mm
0.5 oz copper is 167 squares — **~165 mΩ**, half a 350 mΩ protection FET. Nothing in the ranking may
special-case copper as a small term, and no display may round it away.

---

## 3. `R-rail5-6` — more than one source, gated by superposition

§7, and it is the cheapest gate in this brief:

> A resistive mesh is linear, so a two-source DC solve is **exactly** the sum of the two one-source
> solves. That is arithmetic rather than opinion, it needs no external data, and it catches the whole
> class of defect where a second source is stamped once, twice or at the wrong node.

The test: a rail with two sources, each with its own R. Solve with both. Solve with source A alone and
source B alone. **Node-for-node, the two one-source solves sum to the two-source solve** to machine
precision — not to a tolerance, because this is linearity and not physics.

§9 says why it matters: *"a second source stamped at the wrong node produces a plausible number, not an
error."*

And §2.2 says what the answer buys: *"the geometry is what decides how they share, which is the answer a
hand calculation cannot give and is a large part of why this is a board tool rather than a spreadsheet."*
So the result reports **the share each source carried**, as a first-class number.

---

## 4. `R-rail5-7` — the rail chain, in dependency order

§2.2:

> railRF solves the rails in that dependency order so **the regulator's input voltage is the upstream
> answer rather than a nominal.**

`RailOrder.Resolve` (brief 1) gives the order. `RailDcRun` walks it:

1. Solve the input rail. Its result gives the voltage at the regulator's input pin field.
2. That voltage — **not the nominal** — is what the output rail's source starts from.
3. Where the regulator states a `MinimumInputVoltageV` (brief 1 `R-rail1-5`), compare and report.

### The gate — §7, and it is a number written down in advance

> **The rail chain, against a hand-solved two-rail ladder.** Input rail with a known drop, a regulator
> drawing a known current, an output rail with its own loads: the input voltage the second solve starts
> from is a number written down in advance. The gate is that it is the **upstream answer** and not the
> nominal.

Two assertions, and the second is the one that catches the real defect: change the input rail's copper so
its drop changes, and assert the **output** rail's answer changes. A chain that reads the nominal passes
the first assertion and fails this one.

### `R-rail5-8` — the negative half: a cycle is REFUSED, never iterated

§7: *"the negative half of it is that a cycle in the order is refused rather than iterated."* §9 says why
this is load-bearing rather than a limitation:

> Solving them *together* … is a different model, it needs exactly the data §8.2 records as frequently
> impossible to obtain, and **it would be entered by accident the first time someone asked for a cycle in
> the order to be supported.**

So `RailDcRun` refuses on a cycle, naming the two rails and the refdes that closes it. There is no
iteration count, no relaxation, and no partial answer.

### `R-rail5-9` — what the chain buys, and what it does not

§2.2 is explicit that v1 does **not** extend the chain over frequency:

> Carrying ripple from an input rail to an output one needs the regulator's PSRR and its output impedance
> — the data §8.2 records as frequently impossible to obtain. So each rail's Z(f) is solved against its
> own source model, and where that source is a regulator whose curves were not supplied, **railRF says the
> sub-megahertz answer is optimistic rather than producing a curve that looks complete.**

At DC the chain buys the answer that matters most on a battery design: **whether the drop on the input
rail has taken the regulator below the input voltage it needs.** Report it as a finding with both numbers
in it, and report *nothing* where no minimum was stated — with the rails that had none listed, so the
absence is visible rather than read as a pass.

---

## 5. `R-rail5-10` — an observation port appears in the report AS observed

Brief 1 `R-rail1-7`: a load with no current contributes nothing to the DC solve. §2.2: *"the DC report
lists it as observed rather than omitting it."*

Omitting it is the failure: a user who added a port and sees no row for it concludes the port did not take
effect, and the two states — *not added* and *added with no current* — must not look the same.

---

## 6. `R-rail5-11` — everything at 20 °C, said once, on the report

§2.4, and it is a scope statement as much as a setting:

> Review is running this as a room-temperature selection tool; the design is measured over temperature in
> the lab regardless. So railRF computes at 20 °C, says so on the report, and **does not pretend to be a
> thermal tool.** (Copper is +0.39 %/K: at 85 °C the same trace is ~25 % worse, which is worth one line on
> the report and nothing more.)

One line on the report. Not a temperature setting, not a sweep, not a derating. The +0.39 %/K figure and
the 85 °C example are the line's own content.

---

## 7. Tests — `tests/Ui.Tests/RailRf/PdnDcSolveTests.cs`

- **`R-rail5-6` superposition.** Two sources, node-for-node, to machine precision. Then the negative:
  stamp the second source at the wrong node and assert the sum no longer matches — proving the gate has
  teeth.
- **`R-rail5-7` the ladder.** The hand-solved two-rail case, with both assertions. The number the output
  rail starts from is written into the test as a literal, computed by hand, with the arithmetic in a
  comment.
- **`R-rail5-8`** a cycle refuses, and the refusal names both rails.
- **`R-rail5-3`/`R-rail5-4`**: on the note's own §2.6 reference board, the breakdown's rows sum to the
  total drop exactly, the shares sum to 1, and the copper rows name their **layer**.
- **`R-rail5-5`**: a board built to §2.8's table — a 350 mΩ FET and a 165 mΩ inner run — ranks the FET
  first and the copper second, and the copper's share is over 30 %.
- **`R-rail5-10`**: three loads, one currentless — three rows, one of them marked observed, and two
  current injections.
- **`R-rail5-2`**: every node in `NodeCells` has a voltage. No holes.
- **Fast vs Accurate**, end to end: brief 4's trace-dominated board solved both ways agrees to 5 % on the
  **drop**, not merely on the extraction. That is §7's gate seen through the solve, which is where a user
  meets it.

---

## 8. Scope

- **No via current limit.** Brief 6 — it needs the per-via currents this brief produces, and it is a
  separate question with a separate basis.
- **No frequency.** Briefs 12-14.
- **No picture.** Brief 8 draws the drop map; this brief produces the field.
- **No CLI.** Brief 10.
- **No A/B.** Brief 16.
- **No thermal anything.** §6 above is the whole of it: one line on the report.

**On completion:** record findings in `src/Design/RESOLVED.md`. Never in a CLAUDE.md.
