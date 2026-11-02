# Brief 6 — reduction: the same collapse on both netlists

**Series:** [LVS](brief-lvs-0-overview.md) · **Tag:** `R-lvs6-n` · **Design note:** [`lvs.md`](../design/lvs.md) §6.3
**Area:** `src/Design/Layout/Lvs/LvsReduce.cs` (new)
**Depends on:** 4 · **Blocks:** 7, 10

---

## 0. What this brief delivers

One function that collapses parallel and series groups, run on **both** netlists before matching,
so a fingered FET and a schematic's single symbol are the same circuit.

```
LvsNetlist (layout)   ─┐
                       ├─► LvsReduce.Apply ─► reduced pair ─► brief 7
LvsNetlist (schematic) ─┘        (one function, both sides)
```

**This reverses the design note's first draft.** It proposed no automatic reduction; the owner's
answer was *use what is common and expected by users*, and what every production LVS does is
reduce by default. A designer whose eight-finger FET reports as seven extra devices stops running
the tool.

---

## 1. `R-lvs6-1` — one function, both sides, before matching

**`R-lvs6-1a`** `LvsReduce.Apply(LvsNetlist) → LvsNetlist` plus a `ReductionLog`. Called twice per
run, on each side, with **identical arguments**. Never on one side "to make it look like" the
other — that is note R-lvs-2 applied to this pass, and a reducer that can tell the sides apart
will eventually treat them differently.

**`R-lvs6-1b`** Run to a **fixed point**: a series merge can expose a parallel one and the reverse.
Iterate until nothing changes, with the iteration count in the log. A cap is not needed — every
pass strictly reduces the device count — but assert monotonic decrease so an infinite loop is a
test failure rather than a hang.

**`R-lvs6-1c` Deterministic.** Groups are formed in a canonical order (device path, ordinal) so the
same input gives the same merged identities every time and on every platform.

---

## 2. `R-lvs6-2` — what is reduced (note R-lvs-35)

| Collapse | Applies to | Condition | Value arithmetic |
|---|---|---|---|
| **Parallel, 2-terminal** | `R`, `C`, `L` | same `DeviceType`, same unordered net pair | `C` sums; `R` and `L` sum as reciprocals |
| **Parallel, multi-terminal** | any type | **every** terminal on the same net as its counterpart's, port for port | multiplicity recorded; values summed where the dimension is extensive |
| **Series, 2-terminal** | `R`, `C`, `L` | the shared node has **degree exactly 2** and is not observable (§3) | `R` and `L` sum; `C` sums as reciprocals |

**`R-lvs6-2a` The multi-terminal parallel rule is port-for-port, not set-wise.** Two FETs whose
drain and source are swapped relative to each other are **not** parallel; they are antiparallel and
that is a different circuit. Comparing terminal *sets* would merge them.

**`R-lvs6-2b`** A merged device keeps a **group** of contributing device paths, in canonical order.
That group is what brief 8 un-reduces (§5).

**`R-lvs6-2c`** Multiplicity is carried, not discarded: a merge of four is `Multiplicity = 4` and
brief 10 compares it against the schematic's own `Nf`/`M` parameter.

---

## 3. `R-lvs6-3` — the degree-2 test is the whole safety argument

**`R-lvs6-3a`** A node qualifies for series reduction only when **all** hold:
- exactly two terminals attach to it, across the whole netlist;
- it is not a cell boundary net;
- it carries no net label;
- **no `measure` line names it**;
- it is not net `"0"`.

**`R-lvs6-3b`** If nothing else reaches the node, the two parts are electrically indistinguishable
from one. That is the entire justification and every clause above is a way the node could be
reachable after all.

**`R-lvs6-3c` The `measure` clause is the one that will be forgotten.** Reducing away a node a
measurement references produces a circuit that still parses, still runs, and silently answers a
different question. The measurement list comes from the `TestBench` and is passed in; the layout
side gets the **same** list, so both sides refuse the same collapse and the two stay in step.

**`R-lvs6-3d`** Net `"0"` is excluded structurally: ground has more than two terminals in every
real design, but excluding it explicitly costs nothing and removes a class of degenerate fixture.

---

## 4. `R-lvs6-4` — what is never reduced (note R-lvs-36)

**`R-lvs6-4a` Distributed and behavioural elements.** The microstrip family, `SnP`, `SDD`, the
tuner family, `Port`, `Term`, the wBond. Two series `MLIN`s are one line **only if their `Z0`s
agree**, and a comparison tool must not perform that arithmetic on the user's behalf. Collapsing an
`SnP` is not even definable.

**`R-lvs6-4b` Series anything other than lumped `R`/`C`/`L`.** Two FETs in series are a cascode.

**`R-lvs6-4c` Across an observable node** — §3.

**`R-lvs6-4d` Across a type boundary.** An `R` in series with an `L` stays two devices. The merged
value would have no dimension.

**`R-lvs6-4e` The exclusion is by `DeviceKind`, listed once**, not a predicate scattered through
the merge rules. A new device kind is excluded by default and must be added deliberately; the
alternative is a kind that becomes reducible because nobody thought about it.

---

## 5. `R-lvs6-5` — everything is reported, and every finding un-reduces

**`R-lvs6-5a`** The run summary carries `lvs.reduce.parallel` and `lvs.reduce.series` at **info**,
with counts by device type and by side. *"Layout: 8 parallel groups (32 → 8 devices). Schematic: 0."*
An asymmetry in those counts is often the first clue to what is actually wrong.

**`R-lvs6-5b`** Any finding involving a merged group names the **individual** devices and their
designators, from `R-lvs6-2b`'s group. A report that can only say *"the merged group at net 14"* is
one a user cannot act on, and this is what makes reduction safe to have on by default: nothing is
hidden, it is only counted differently.

**`R-lvs6-5c`** `--no-reduce` turns the whole pass off, and the run summary **always states which
mode produced it**, in both modes. A result whose reduction mode is not on its face is a result two
people can read differently.

**`R-lvs6-5d` Jumpers.** A part whose `PartKind` declares it a shorting link collapses to a net
merge — **on both sides, and only when both sides have it.** Reported as `lvs.reduce.jumper` at
info either way, because a jumper present in one document and absent from the other is exactly the
thing a designer wants told.

---

## 6. Gate

`tests/Ui.Tests/Lvs/ReductionTests.cs`.

1. **Four identical resistors on one net pair reduce to one**, value = R/4, `Multiplicity = 4`,
   group naming all four paths (`R-lvs6-2`, `R-lvs6-2b`).
2. **Two series resistors through a bare node reduce to one**, value = R1+R2; **two series
   capacitors** reduce reciprocally (`R-lvs6-2`).
3. **The five non-reducible node conditions, one test each**: three terminals; a boundary net; a
   labelled net; a net a `measure` line names; net `"0"` (`R-lvs6-3a`). The `measure` case is the
   one to write first.
4. **Antiparallel multi-terminal devices do not merge** — two FETs with D and S swapped
   (`R-lvs6-2a`).
5. **Every excluded kind stays put**: series `MLIN`+`MLIN`, series `SnP`+`SnP`, `R`+`L`
   (`R-lvs6-4`).
6. **A new `DeviceKind` is excluded by default** — add one in the test and assert it is not
   reduced (`R-lvs6-4e`).
7. **Fixed point.** A ladder that needs three passes (parallel exposes series exposes parallel)
   converges, the log says three, and device count decreases monotonically (`R-lvs6-1b`).
8. **Determinism.** Ten runs over a symmetric fixture give identical merged identities and
   identical order (`R-lvs6-1c`).
9. **Both sides get the same treatment.** Feed the *identical* netlist in as both sides; the two
   reduced results are equal object for object (`R-lvs6-1a`).
10. **`--no-reduce` changes the count and not the verdict.** The correct board of brief 5 passes in
    both modes, with different device counts and the same zero findings (`R-lvs6-5c`). **This is
    the most important test in the brief**: reduction must change how a correct design is counted
    and never whether it passes.
11. **A finding names the individuals.** Break one of four parallel resistors and assert the
    finding names that resistor's designator, not the group (`R-lvs6-5b`).
12. **A jumper on one side only is reported** and does not silently collapse (`R-lvs6-5d`).

## 7. Scope

- **No value judgement.** Reduction computes merged values; deciding whether they agree is brief 10.
- **No topology-changing transform** beyond the table: no star-delta, no node elimination, no
  device-type rewriting.
- **No configuration.** The table is the rule. `--no-reduce` is the only switch, and it is all or
  nothing.
