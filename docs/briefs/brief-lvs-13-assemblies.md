# Brief 13 — assemblies: two technologies, and the wBond

**Series:** [LVS](brief-lvs-0-overview.md) · **Tag:** `R-lvs13-n` · **Design note:** [`lvs.md`](../design/lvs.md) §4.6, §4.8
**Area:** `src/Design/Layout/Lvs/AssemblyRead.cs` (new), `src/Design/Schematic/WBondPlacement.cs`,
`src/WBond/`, `src/Design/Layout/Extraction/`
**Depends on:** 9 · **Blocks:** 15

---

## 0. What this brief delivers

A die on a board, a die wired to a package lead, two dies wired to each other — compared. This is
the case with no precedent in either the DRC reading or the railRF one, and it is the reason the
unit of LVS is an **assembly** rather than a cell.

```
package/board .clay          ← the extraction root
  ├─ instance: die A (.clay, its OWN .ctech)      ─► sub-cell, boundary = its bond pads
  ├─ instance: die B                              ─► sub-cell
  └─ Assembly.wBond  (beside the root .clay)      ─► wires; feet land on A's, B's and the root's copper
```

---

## 1. `R-lvs13-1` — an assembly is brief 9 with a wBond in it

**`R-lvs13-1a`** The root is the cell that **places** the dies. Its instances are the dies; its own
shapes are the package or leadframe copper; the `.wBond` beside its `.clay` holds the wires.

**`R-lvs13-1b`** Each die is a sub-cell under brief 9 and its **bond pads are its boundary pins**.
Nothing new is required for that half — it is §4.5 with a wBond in it.

**`R-lvs13-1c`** A die's pad that no wire reaches and that lands on no parent copper is an open,
reported on the parent's terms, naming the die and the pad.

---

## 2. `R-lvs13-2` — copper never crosses a technology boundary by layer coincidence

**`R-lvs13-2a`** Two technologies' `Metal1`s are not the same metal, and a collision between an
MMIC's layer 1 and a board's layer 1 is a coincidence of integers. **Never** joined by layer
number.

**`R-lvs13-2b`** Across a boundary, connection is **only** through: a declared boundary pin of the
sub-cell landing on parent copper; a via whose stackup entry spans the boundary; or a bondwire
foot. There is no fourth route.

**`R-lvs13-2c`** A layer reconciliation left pending at the boundary makes that sub-cell
**unextractable** — the same refusal `LayoutDesignFlatten` already gives, reported, never guessed
at. A guess here silently merges or silently separates two dies' nets.

**`R-lvs13-2d`** DBU resolutions differ between technologies. Every cross-boundary coordinate goes
through the existing conversion; a hand-written scale factor anywhere in this brief is a defect.

---

## 3. `R-lvs13-3` — a wire foot resolves like any other pin

**`R-lvs13-3a`** A `Wire`'s endpoints are `Point3` in **nanometres**; the layout is DBU. Convert,
then point-in-piece, exactly as brief 3 locates a terminal.

**`R-lvs13-3b` The nm↔DBU bridge fails SILENTLY at the 1000 DBU/µm default** — the two numbers
coincide, so a wrong conversion is invisible on every default-configured design. This is a
recorded scar (WB-C), not a hypothetical. **The conversion is named at the call site and the test
fixture uses a non-default DBU resolution and coordinates that are not round micron values.**

**`R-lvs13-3c`** A foot lands on the root's copper, on a die's copper, or on nothing. On nothing
is `lvs.wbond.foot-on-nothing`, error, naming the array, the wire and the coordinate.

**`R-lvs13-3d`** A foot landing on a **die's** copper resolves through that die's own partition and
then through the boundary, because a bond pad is a boundary pin: the wire reaches whatever net the
pad reaches inside the die.

---

## 4. `R-lvs13-4` — arrays match by name, never by index

**`R-lvs13-4a`** A wBond component's ports are `2M` (+ reference), ordered by array; its terminals
are named from the **array names** (brief 4 `R-lvs4-6c`). Array *k* runs from its input node to its
output node.

**`R-lvs13-4b` Matched by NAME.** `WBondPlacement.DriftBetween` exists for exactly the failure an
index match would produce: reorder the array list and every pin keeps its position while its name
moves, so the wires now connect to different arrays.

**`R-lvs13-4c`** LVS **consumes** that drift report rather than re-deriving it, and a drifted wBond
is `lvs.wbond.array-drift`, error, reported **before any net is compared** — comparing nets against
a drifted array list produces findings that are all real and all about the wrong thing.

**`R-lvs13-4d`** An array with no wires is not an error: it is an ordinary mid-design state. Its
two terminals are simply open, and the open finding says which array.

---

## 5. `R-lvs13-5` — which wires, and saying so

**`R-lvs13-5a`** A placed wBond has two possible wire sources — **Carried** (the `Design`
payload) and **Linked** (the cell's `.wBond`) — chosen per instance by its `Source` parameter.

**`R-lvs13-5b` LVS reads the one the ENGINE would read**, and the report says which. Verifying
wires the engine will not simulate verifies a design nobody runs.

**`R-lvs13-5c`** A Carried instance whose payload has drifted from its cell's `.wBond` is
`lvs.wbond.payload-drift`, **warning**, naming *"Update Schematic from wBond Layout"* as the
remedy. §9.6 calls that state normal and recoverable; the rule is that it must never be **quiet**,
not that it must be prevented.

**`R-lvs13-5d`** The capacitors a wBond stamps (§5.2/WB19c) are **not devices**. They are part of
the component's own model, they appear at no schematic symbol, and emitting them would make every
wBond report `M` extra unmatched capacitors. Stated here because it is exactly the kind of thing
that looks like an omission.

---

## 6. `R-lvs13-6` — the reference conductor

**`R-lvs13-6a`** §5.4's reference conductor is not optional in the model and is not optional here:
the wBond's reference terminal resolves like any other, and an unresolved one is an open.

**`R-lvs13-6b`** On an assembly the ground reference frequently lives in the **package**, not in
either die — which is precisely brief 3 `R-lvs3-6d`'s "a die with no backside metal, grounded only
through bondwires to a package, is a real and correct design". That die reports
`lvs.ground.no-reference-conductor` at warning **and still compares correctly**, because its
ground arrives through a wire. This is the case that rule was written for and it is a gate here.

---

## 7. Gate

`tests/Ui.Tests/Lvs/AssemblyTests.cs`.

1. **One die on a board, two technologies, wires to the package.** End to end, clean, with the die
   extracted once and stitched at its pads.
2. **A wire foot's net is correct under a NON-DEFAULT DBU resolution** and at coordinates that are
   not round microns (`R-lvs13-3b`). **Write this one first**; at 1000 DBU/µm a wrong conversion
   passes.
3. **A foot on nothing is reported** naming array, wire and coordinate (`R-lvs13-3c`).
4. **A foot on a die's pad reaches the die's internal net** (`R-lvs13-3d`).
5. **Reordering the array list is caught by name before any net comparison**, and the finding
   count is 1 rather than a cascade (`R-lvs13-4c`).
6. **Carried and Linked give different answers on a drifted instance**, LVS reads the engine's
   choice, and the report names which (`R-lvs13-5b`, `c`).
7. **A wBond's stamped capacitors are not devices** — device count equals the schematic's
   (`R-lvs13-5d`).
8. **No layer-coincidence connection.** A die whose layer 1 collides with the board's layer 1, with
   overlapping geometry and no pin, no via and no wire: the two are **separate nets**
   (`R-lvs13-2a`). This is the test that catches the whole class.
9. **A pending layer reconciliation refuses that sub-cell** and says so (`R-lvs13-2c`).
10. **A die with no ground reference, grounded through wires, compares correctly** with the warning
    present (`R-lvs13-6b`).
11. **An array with no wires is two opens, not an error** (`R-lvs13-4d`).

## 8. Scope

- **No new wBond file format, no new parameter.** `Source`, `Arrays`, `Design`, `File` and
  `DriftBetween` already exist.
- **No wire-to-wire geometry.** That is assembly DRC's and it already exists.
- **No inductance, no capacitance, no physics.** LVS never prices anything.
- **No three-technology case** beyond what falls out: the rules are per boundary and compose.
