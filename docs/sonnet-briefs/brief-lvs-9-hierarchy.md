# Brief 9 — hierarchy: extract each cell once, stitch at the boundary

**Series:** [LVS](brief-lvs-0-overview.md) · **Tag:** `R-lvs9-n` · **Design note:** [`lvs.md`](../design/lvs.md) §4.5, §7
**Area:** `src/Design/Layout/Lvs/LayoutRead.cs`, `LvsCompare.cs`, `src/Design/Layout/Extraction/PlacedPins.cs`
**Depends on:** 7, 8 · **Blocks:** 13

---

## 0. What this brief delivers

A 3,000-part board becomes 40 cell extractions and one board-level one, instead of 3,000. **The
performance is a side effect; the reason is correctness** — flattening destroys the instance
identity the whole comparison is built on, and a report that cannot name `U3/M1` is a report about
a design nobody drew.

```
for each DISTINCT resolved cell:   extract once, cache by content key
at each level:                     boundary pin -> parent net (transform, then point-in-piece)
compare:                           cell against cell, board against board
```

---

## 1. `R-lvs9-1` — extract each cell once

**`R-lvs9-1a`** A cell's netlist is extracted in **its own coordinate frame**, with its
`LayoutPin` list as its boundary.

**`R-lvs9-1b` Cache key:** a content hash of (cell directory, primary `.clay` mtime, resolved
technology identity, resolved PCell parameters). This is the key shape `CellLayoutResolver` and
`GeneratedCellStore.BuildCellName` already use — **reused, not re-derived**, because a cache key
that differs from the one the rest of the application uses is a cache that is stale in exactly the
cases the others are not.

**`R-lvs9-1c`** Per run, in memory. Nothing is written (R-aut4-6 remains this feature's rule).

**`R-lvs9-1d`** Two placements of one cell with **different resolved parameters** are two cache
entries, correctly: a PCell is its parameters.

---

## 2. `R-lvs9-2` — stitching

**`R-lvs9-2a`** A sub-cell's boundary pin is transformed into the parent's frame by
`LayoutInstanceTransform.TransformPoint(pin.X, pin.Y, inst, r, col)` — the one function — and then
located in the parent's partition by point-in-piece **on the pin's own layer**, exactly as brief 3
locates a device's terminal.

**`R-lvs9-2b`** A sub-cell instance becomes **one `LvsDevice`** whose terminals are its boundary
pins. A *hierarchical* comparison stops there and compares it against the schematic's own cell
instance. A *flattening* comparison substitutes the sub-cell's netlist in.

**`R-lvs9-2c`** Which of the two happens per cell: hierarchical by default; flattened where the
schematic has no corresponding cell instance (a layout module the drawing does not have as a
cell), where `--flatten-cell <name>` says so, or where `R-lvs9-3` forces it.

**`R-lvs9-2d`** Net identity across the boundary comes **only** from the pin landing on parent
copper. A sub-cell's internal net names never reach the parent, and the parent's never reach in.

---

## 3. `R-lvs9-3` — undeclared contact is reported, never absorbed

**`R-lvs9-3a`** A sub-cell whose copper touches the parent's copper anywhere **other than at a
declared boundary pin** breaks the hierarchical reading. This is the classic hierarchical-LVS
failure and the only honest responses are to report it or to flatten that one cell.

**`R-lvs9-3b`** `lvs.hierarchy.undeclared-contact`, **error**, naming the cell, the instance, the
layer and the coordinate, with a marker on the contact.

**`R-lvs9-3c` Silently absorbing it would mean the hierarchy says one thing and the copper
another** — the whole class of defect this tool exists to find, reintroduced by the tool itself.

**`R-lvs9-3d`** The escape hatch is explicit and per cell: `--flatten-cell <name>` on the verb, and
a `FlattenForLvs` flag in the cell's `.ccell` for a cell that is permanently like this (a shield
frame, a module whose ground is genuinely continuous with the board's). Using it is **reported**
at info, so a design that quietly flattens everything is visible.

**`R-lvs9-3e` Detection is cheap and must stay so.** Union the sub-cell's contributed copper
against the parent's per layer; any component of the intersection not containing a declared pin is
a contact. One extra boolean per instance per layer, on geometry already unioned.

---

## 4. `R-lvs9-4` — the flat fallback is the oracle

**`R-lvs9-4a`** `--flat` extracts through brief 3's tagged flatten and compares one graph. It is
always available and always correct.

**`R-lvs9-4b` A hierarchical run and a flat run over the same documents produce the SAME findings,
in the same order.** That is the gate on the whole brief, and it is why brief 7 was built flat
first. Where they differ, the hierarchical reading is wrong until proven otherwise.

**`R-lvs9-4c`** The exception is exactly `R-lvs9-3`: a design with undeclared contact has no
hierarchical reading, so the flat run answers and the hierarchical run reports. Those fixtures are
excluded from the equivalence gate **by name**, not by a blanket tolerance.

---

## 5. `R-lvs9-5` — counters, not clocks

**`R-lvs9-5a`** The structural properties, both counters:
- **extractions == distinct (cell, parameter-set) pairs**, not placements;
- **point-in-piece queries == pins**, each answered through brief 2's index.

**`R-lvs9-5a2` Union per layer ONCE, over one cell's flat geometry** (note R-lvs-48) — not per
placement, and not per shape pair. `LayerRegions.Build` already unions once per call; what this
brief guarantees is that it is **called** once per distinct cell rather than once per placement,
which is `R-lvs9-1`'s cache doing its job. It is the third counter.

**`R-lvs9-5b`** `WireSweepCounters` is the in-repo precedent for the shape — *"counting the
netlist's elements is the structural property that makes this fast, and it is what the tests
assert, never a wall-clock time"*.

**`R-lvs9-5c` No new timing test.** If a timed measurement is ever wanted it is
`Category=Benchmark` and it is a separate decision.

**`R-lvs9-5d`** The ceiling: above `MaxDevices` (default, the existing 500,000 shape ceiling
reused rather than re-derived) the run **refuses and says what it would have needed** —
`DrcEngine`'s bargain. A pathological design costs a message, not a hang.

---

## 6. `R-lvs9-6` — the schematic side is already hierarchical

**`R-lvs9-6a`** `NetExtractor` produces `TestBench` + `Library` and does not flatten; the
`Elaborator` is what flattens. Brief 4 already reads the design model, so the schematic side of
this brief is: compare cell against cell, using the `Library`'s own cells.

**`R-lvs9-6b`** A layout sub-cell with **no** corresponding schematic cell is flattened into the
parent (`R-lvs9-2c`) and reported at info. It is a common and legitimate state — a layout-only
module — and refusing it would make hierarchy useless on real boards.

**`R-lvs9-6c`** The reverse — a schematic cell instance with no layout sub-cell — is an ordinary
unmatched device (`lvs.device.unmatched-layout`'s mirror), not a hierarchy finding.

---

## 7. Gate

`tests/Ui.Tests/Lvs/HierarchyTests.cs`.

1. **Flat and hierarchical agree** on every fixture in the repo — findings, ids, objects and order
   (`R-lvs9-4b`). **This is the brief.**
2. **One extraction per distinct cell**, asserted as a counter: a board with 40 placements of one
   cell extracts it **once** (`R-lvs9-5a`).
3. **Two parameter sets of one PCell are two extractions** (`R-lvs9-1d`).
4. **Boundary stitching under transform.** A sub-cell placed at 90°, at 217° and mirrored: its
   boundary pins land on the parent nets hand arithmetic says they do, with **pins off both axes**
   (the WB-C trap).
5. **Undeclared contact is reported**, naming cell, instance, layer and coordinate, with a marker
   (`R-lvs9-3b`); and the same design under `--flatten-cell` compares clean and reports the
   flatten at info (`R-lvs9-3d`).
6. **A layout-only module flattens and is reported**; a schematic-only cell instance is an
   unmatched device (`R-lvs9-6b`, `c`).
7. **Queries per pin is exactly one**, and **layer unions equal distinct cells**, both counters,
   on a **generated** scale fixture — built in the test, never committed as artwork, so it can be
   scaled without adding megabytes to the repo (`R-lvs9-5a`, `R-lvs9-5a2`; note §10 validation 4).
8. **Over the device ceiling: refusal naming the number** (`R-lvs9-5d`).
9. **The cache key matches the application's.** A `.clay` touched on disk invalidates the entry; a
   technology change invalidates it; a parameter change invalidates it (`R-lvs9-1b`).
10. **Nothing is written** during a hierarchical run over a read-only tree (`R-lvs9-1c`).

## 8. Scope

- **One technology per run.** Brief 13.
- **No wBond.** Brief 13.
- **No per-cell caching across runs, no cache file.** In memory, per run (`R-lvs9-1c`).
- **No change to brief 3's flat reading**, which is this brief's oracle and must stay honest.
