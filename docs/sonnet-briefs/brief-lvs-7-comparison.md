# Brief 7 — the comparison: anchors, colour refinement, divergence

**Series:** [LVS](brief-lvs-0-overview.md) · **Tag:** `R-lvs7-n` · **Design note:** [`lvs.md`](../design/lvs.md) §6.2
**Area:** `src/Design/Layout/Lvs/LvsCompare.cs` (new), `LvsRun.cs` (new)
**Depends on:** 3, 4, 6 · **Blocks:** 8, 9, 10, 11

---

## 0. What this brief delivers

Two `LvsNetlist`s in, a **correspondence** out, plus the smallest set of places it fails. This is
the brief that first says yes or no.

```
anchors ──► initial colours ──► refine to fixed point ──► classes
                                                            │
                          size 1 both sides ──► matched     │
                          size n both sides ──► automorphism, paired deterministically
                          present one side  ──► DIVERGENCE, reported at the point of divergence
```

---

## 1. `R-lvs7-1` — the algorithm, named, because it must terminate and be fast

**`R-lvs7-1a`** Iterative partition refinement — the colour-refinement (Weisfeiler–Leman) fixed
point over the bipartite device/net graph, with automorphism tie-breaking. Near-linear, no
backtracking search, and that is what makes thousands of components sub-second.

**`R-lvs7-1b`** It is **not** a subgraph-isomorphism solver and must never grow into one. If
refinement cannot separate two classes, they are genuinely symmetric and §4 handles it; adding
search would turn a bounded cost into an unbounded one on exactly the designs that are largest.

---

## 2. `R-lvs7-2` — anchors are hypotheses, not answers

**`R-lvs7-2a`** Anchors come from: tier-0 `SchematicId`, tier-1 designator match, and any net name
unambiguous on **both** sides — a cell boundary port, or a stamped `Net` that equals a schematic
net label.

**`R-lvs7-2b` Every anchor is verified by the refinement, never trusted.** An anchor the refinement
contradicts is itself a finding, `lvs.anchor.contradicted`, and it is usually **the single most
useful line in the whole report**: it names a mis-wired part by the designer's own name for it,
rather than reporting two anonymous unmatched objects.

**`R-lvs7-2c`** A contradicted anchor is **dropped and the refinement re-run once** without it.
Keeping it would propagate one wrong pairing through every neighbour's colour and turn one fault
into a cascade — which is how a report goes from six findings to four hundred.

**`R-lvs7-2d`** An anchor naming a component that does not exist on the other side is
`lvs.device.dangling-schematic-id` (brief 3 `R-lvs3-3d`) and is not an anchor.

**`R-lvs7-2e`** With **no** anchors at all — every name changed, or a hand-drawn board with no
designators — the comparison still runs on structure alone (tier 2) and says so:
`lvs.match.structural-only` at info. That is the case that proves the algorithm rather than the
naming.

---

## 3. `R-lvs7-3` — colours

**`R-lvs7-3a` Initial device colour** = hash(canonical `DeviceType`, terminal count, parameter
**class**, anchor id or none). Parameter *class* — the set of declared parameter names and their
unit dimensions — not their values: values are brief 10's business and folding them in here would
make a 1 % value difference a topology mismatch.

**`R-lvs7-3b` Initial net colour** = hash(terminal count, anchor id or none, is-net-`"0"`).

**`R-lvs7-3c` Refinement recolours from the sorted multiset of neighbour colours AND the terminal
position each neighbour attaches through.** The terminal index is not optional: a resistor is
symmetric but a FET is not, and dropping the index matches a drain to a source. This is the single
most likely implementation error in the brief.

**`R-lvs7-3d`** Iterate to a fixed point. Cap at the graph diameter and report
`lvs.compare.refinement-capped` if the cap binds — it should never bind, and if it does the graph
or the hashing is wrong.

**`R-lvs7-3e` Counting sort over the previous iteration's dense colour ids**, never a comparison
sort over strings. This is what keeps the pass near-linear (note R-lvs-49).

---

## 4. `R-lvs7-4` — automorphisms are ordinary and must be said out loud

**`R-lvs7-4a`** Classes of size *n* > 1 with identical colours on both sides are *n* genuinely
interchangeable objects — paralleled fingers that survived reduction, a symmetric attenuator's two
shunt legs, four identical decoupling caps on one rail.

**`R-lvs7-4b`** Pair them by a deterministic tie-break: provenance order — instance path, then
designator, then ordinal. Same input, same pairing, every run, every platform.

**`R-lvs7-4c` Report `lvs.match.by-symmetry` at info, naming the group.** The pairing is
**arbitrary**, so a later finding naming "C7" may mean the part the designer calls C9, and a user
who does not know that will chase the wrong part. This is not a nicety.

**`R-lvs7-4d`** A class of size *n* on one side and *m* ≠ *n* on the other is a divergence, not an
automorphism, and the finding reports **both counts**.

---

## 5. `R-lvs7-5` — report at the point of divergence

**`R-lvs7-5a`** When refinement separates the two sides, report the **smallest colour class that
differs**, with the path from the nearest anchor to it. *"Everything matched except 412 devices"*
is not a report; *"the gate of M3 reaches VDD, and the schematic says it reaches n17"* is.

**`R-lvs7-5b`** Findings are **capped and summarised**, on the change-report convention already in
the repo: a per-object cap with a trailing count, and run-level lines outside the cap. One
mis-wired bus should not produce a thousand lines.

**`R-lvs7-5c`** The correspondence itself is returned, not only the failures. Brief 12's
cross-probing needs it, and a user who wants to know *what did match* is asking a reasonable
question.

**`R-lvs7-5d`** Type mismatch is reported **as such** (brief 4 `R-lvs4-5e`): *"R1 is a resistor in
the schematic and a capacitor in the layout"*, one line, not two unmatched-device lines.

---

## 6. `R-lvs7-6` — determinism, and `LvsRun` as the one door

**`R-lvs7-6a`** Same two documents → same result, same order, on every platform. All hashing over
ordered, culture-invariant strings. **No `GetHashCode`**, no dictionary enumeration order, no
parallelism whose completion order can reach the answer.

**`R-lvs7-6b`** `LvsRun.Run(...)` is the single entry point: read both sides, reduce both, compare,
assemble the result. The CLI calls it, the panel calls it, every non-unit test calls it. There is
no second path into the comparison, and a source scan holds that.

**`R-lvs7-6c`** Progress and cancellation ride `RunHost`'s `RunControl`, as `em` and `render` do,
so a large comparison can be stopped. A cancelled run returns nothing and writes nothing.

---

## 7. Gate

`tests/Ui.Tests/Lvs/ComparisonTests.cs`, plus the brief-5 gates that switch on here.

1. **The correct board compares with zero findings above info** (brief 5 `R-lvs5-1d`).
2. **Each of the six faults, singly**, produces its expected finding by id and names the right
   objects; then **all six together** produce six findings and not sixty (brief 5 `R-lvs5-2`).
3. **The symmetric shunt pair** reports `lvs.match.by-symmetry`, and ten runs give the identical
   pairing and order (`R-lvs7-4c`, `R-lvs7-6a`).
4. **Terminal position matters.** A FET with drain and source swapped in the layout is a finding.
   **Write this test first** — it is the one `R-lvs7-3c` exists for, and an implementation that
   drops the terminal index passes everything else.
5. **A contradicted anchor is reported and dropped.** R2 renamed to R9 in the layout but wired
   correctly: `lvs.anchor.contradicted`, then a clean match. Assert the finding count is **1**, not
   a cascade (`R-lvs7-2b`, `c`).
6. **No anchors at all.** Strip every designator and `SchematicId` from the correct board: it still
   matches, structurally, and reports `lvs.match.structural-only` (`R-lvs7-2e`).
7. **Parameter values do not affect topology.** Change R3's value only: the topology matches and the
   only finding is brief 10's (`R-lvs7-3a`).
8. **Type mismatch is one line** (`R-lvs7-5d`).
9. **Unequal symmetric classes report both counts** — three parallel caps against four
   (`R-lvs7-4d`).
10. **Refinement never caps** on any fixture in the repo (`R-lvs7-3d`).
11. **A counter gate on the sort**: colour-refinement work grows near-linearly as a fixture is
    scaled 10×, asserted as an operation counter, never a wall clock (overview §1i).
12. **`LvsRun` is the only door** — source scan for any other caller of `LvsCompare`
    (`R-lvs7-6b`).
13. **Cancellation writes nothing and returns nothing** (`R-lvs7-6c`).

## 8. Scope

- **No finding formatting, no markers, no short paths.** Brief 8 — this brief emits typed
  divergences and brief 8 turns them into the report.
- **No hierarchy.** Brief 9; this brief's flat answer is that brief's oracle.
- **No properties.** Brief 10.
- **No search.** `R-lvs7-1b`.
