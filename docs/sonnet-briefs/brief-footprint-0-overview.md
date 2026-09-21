# Brief — SMT footprint artwork: one picker, one concept, every component — the series

**Status:** unstarted, briefs 1-5 · **Date:** 2026-09-20
**Area:** `src/Design/Layout/Footprints/`, `src/Design/Layout/PCells/`, `src/Design/Schematic/`,
`src/Core/Elaboration/`, `src/Ui/Layout/`, `src/Ui/ViewModels/`, `src/Ui/Views/ParameterEditor/`,
`examples/Power Rail/`
**Requirement tag for the series:** `R-fp<n>-<m>`, scoped per brief (`R-fp1-5` is brief 1's fifth)
**Raised by:** a first-time designer's pass over railRF and the Power Rail example, 2026-09-20 —
he asked for "a library category of component with art work, like 0402 R / C / L", because drawing
a land pattern by hand to place a part is a mess

---

## 0. The short answer

A user who has an S2P of a capacitor, or an `SRLC`, or an ordinary `C`, can today simulate it and
cannot lay it out: **Update Layout from Schematic writes nothing for any of them**, because
`SchematicToLayoutGenerator.ResolveComponentLayout` resolves artwork from a kit reference, a
`CellRef` or a registered PCell generator, and an ordinary two-terminal part has none of the three.
So a design ends up as two schematics — one that models and one that lays out — which is exactly the
split v2's LVS exists to close and exactly the split this series exists to prevent.

The whole feature is **one instance parameter and one branch**:

```
Footprint = "smt:0402@N"        on the component, in the .csch, like any other parameter
                ↓
ResolveComponentLayout          one new branch, AHEAD of the kit / CellRef / PCell chain
                ↓
a LayoutView with two pads      generated, or an imported cell, or a user's own .clay
```

Everything else in the series is that idea's consequences: where the built-in patterns come from,
what the picker offers, what happens when the pad count and the port count disagree, and what the
shipped example looks like once its parts are real.

### The one rule the series is built on

> **A footprint IS a layout view of a cell. There is no second artifact kind.**

`ComponentImport` already writes exactly that — a cell folder holding a symbol plus one or more land
patterns as sibling `.clay` views (`BuiltLayout.Variant`, R-PL1-25's density suffix) with a
`.ccell` naming the primary. Nothing in this series changes it, and nothing in this series invents a
parallel registry beside it. **An imported part appears in the footprint picker because it is a cell
with a layout view**, with no extra step, no second import and no re-export.

The corollary, gated by a comment-stripped source scan in brief 4: **nothing in this series writes a
second land-pattern format, a second footprint index, or a second import.**

---

## 1. Six things that are not obvious, resolved here once

These came out of reading the code before writing the briefs. Each is restated where it bites.

### 1a. The PCell contract is already below the firewall; only the generators are not

`src/Design/Layout/PCells/` holds `PCellContract.cs` (`PCellResult`, `PCellPin`, `PCellGenerator`,
`PCellHandle`), `PCellValue`, `PCellUnits` and `SubstrateResolver`. What lives in `src/Ui` is the
six microstrip *generators* and `PCellRegistry`.

So the land-pattern generator goes in **`src/Design/Layout/Footprints/`** and costs nothing
architecturally: it draws nothing, it produces a `LayoutView`, and `src/Cli` can therefore generate a
footprint with no display — which `circuitrf netlist`, `render` and `convert` all need. `src/Ui`
registers it with `PCellRegistry` through the resolver seam that already exists for kit generators.
Same move the DRC engine made on 2026-09-05.

### 1b. A shipped `.clay` per case size cannot work, and would fail silently

A `.clay` carries absolute `LayerKey`s. The shipped technologies disagree about every one of them:

| technology | Top Copper | Silk Top | Soldermask Top |
|---|---|---|---|
| `pcb-2layer_FR-4_70mil_1oz` | (1,0) | **(5,0)** | (3,0) |
| `pcb-4layer_FR-4_62mil_1oz` | (1,0) | **(7,0)** | (5,0) |
| `examples/Power Rail/tech/pcb-4layer-1p6mm` | (1,0) | **none at all** | none at all |
| `mmic-GaAs_2LM_100um` | Metal1 | none | none |

A shipped 0402 `.clay` dropped into the four-layer technology would put its silkscreen on layer 5,
which is Soldermask Top, and nothing would say so. **Generated, resolving layers by ROLE** — the
existing `Purpose` field and `Interchange.PcbLayerName` (`F.Cu`, `F.Mask`, `F.SilkS`, `Edge.Cuts`) —
is the only form that survives contact with more than one technology, and it is the only form that
can **refuse by name** on a technology with no top copper.

### 1c. `Footprint` must never reach the numeric layer

The invariant is standing: *the numeric layer sees only fully-resolved parameter values*. `Footprint`
is artwork and is not a value — `smt:0402@N` is not an expression, and `Elaborator` would try to
evaluate it.

There is an existing per-family answer (`Elaborator._snpStringParams`, `src/Core/Elaboration/Elaborator.cs:1901`,
which stores `File`/`InterpMode`/`PinConfig`/… raw), and it is the **wrong** one to copy: a universal
artwork parameter added to a per-family list is a parameter that leaks the first time somebody puts
it on a family nobody thought of. It is **dropped before parameter resolution**, once, for every
component kind — brief 2 R-fp2-6.

`SchematicToLayoutGenerator.NonPCellParamNames` (`src/Ui/Layout/SchematicToLayoutGenerator.cs:67`)
must exclude it too, or it is handed to a PCell generator as a dimension.

### 1d. The BOM and the part library already have a footprint column

`BomFile` recognises `footprint`, `package`, `pattern`, `land pattern`, `fp`, `decal`, `pkg`, `case`
and `footprint name` (`src/Design/Layout/Interchange/BomFile.cs:144`), and `PartLibraryRow.Footprint`
is a `string?` that exists today (`src/Design/RailRf/PartLibrary.cs:80`).

Those are **inputs to the same picker**, not a second vocabulary. Brief 4 maps a BOM's spelling onto
a case code where it can, and reports where it cannot, rather than quietly inventing a third naming
scheme for the same twelve parts.

### 1e. The imperial/metric collision is a real, silent 2.4x error

`0201` imperial is 0.6 x 0.3 mm. `0201` **metric** is 0.25 x 0.125 mm, which is imperial `008004` —
and both codes are in the shipped list. A BOM that writes `0201` meaning metric, read by a picker
that means imperial, yields a part 2.4x too big **with nothing to notice**: it places, it renders, it
exports, and the first sign of trouble is a board.

So: every entry in every list, combobox row, tooltip and refusal reads its metric twin and its
millimetres —

```
0402   (metric 1005)   1.00 x 0.50 mm
```

— and a BOM token that is ambiguous between the two schemes is **reported, never guessed**
(R-fp4-8). This is the same class as the Excellon suppression refusal `convert` already makes.

### 1f. Pad count against port count is the contract, and it must refuse

An `S4P` pointing at an 0402 is a design error. Placed silently it produces artwork with two pads
for a four-port model, which LVS in v2 would then flag as a mystery.

The picker offers only footprints whose pad count equals the component's port count, and a stored
`Footprint` that disagrees is **reported on Update Layout and not placed** (R-fp3-5). The one that
will bite: **an `SnP` with `RefNode` set has one more port than its file has** —
`EditableComponent.GetEffectiveSnpPortDefs` generates the reference pin, so a 2-port S2P with
`RefNode` is three ports and does not fit a two-pad chip land.

---

## 2. What each brief delivers

| | Brief | Delivers |
|---|---|---|
| 1 | [land-pattern generator](brief-footprint-1-land-pattern-generator.md) | `src/Design/Layout/Footprints/` — the case table, IPC-7351 density variants, layers by role, the refusals |
| 2 | [the instance parameter](brief-footprint-2-instance-parameter.md) | `Footprint` on `EditableComponent`, the schematic combobox, the third label, and the elaborator's blindness to it |
| 3 | [Update Layout](brief-footprint-3-update-layout.md) | the one branch in `ResolveComponentLayout`; the pad-count contract; the layout editor's picker and its place-by-hand gesture |
| 4 | [one picker](brief-footprint-4-picker-and-import.md) | built-ins + workspace cells + Custom in one list; the Component Import reconciliation; the BOM column |
| 4b | [designators](brief-footprint-4b-designators.md) | the refdes a placement owns, drawn on silk, movable and resettable; three interchange sites stop discarding it |
| 5 | [the example](brief-footprint-5-power-rail-example.md) | Power Rail rebuilt on real footprints — silkscreen, refdes, re-spaced, every number re-measured |

Briefs 1-2 are independent. 3 depends on 1 and 2. 4 depends on 3. **4b depends on 3 and 4, and
blocks 5** — brief 5's R-fp5-2c is written as though a per-placement designator already exists, and
at HEAD nothing in the model or the renderer provides one. 5 depends on 4b and on
[railRF brief 23](brief-railrf-23-mount-and-unmount.md).

---

## 3. What this series is NOT

- **Not a footprint editor.** A land pattern is a `.clay` and the layout editor already edits those.
  Custom means "point at one", never "draw one here".
- **Not a parts database.** The picker lists case sizes and the cells that are already in the
  workspace. It does not know part numbers, stock, or a manufacturer's recommended pattern.
- **Not LVS.** The `SchematicId` an instance already carries is what v2's LVS will match on, and
  brief 3 keeps writing it. Nothing here checks connectivity.
- **Not a change to Component Import.** See §0's rule. If a brief in this series finds itself editing
  `src/Design/Layout/ComponentImport.cs` for anything but a call site, it has taken a wrong turn.

## 4. Vendor references

The report that raised this series came with a third-party evaluation board's schematic and BOM,
naming a board vendor and several component manufacturers. **None of that enters the repo** — not as
a fixture, not as an example part number, not as a comment, and not as a list of names to filter.
Every part in every example and every test stays synthetic. Grep before any commit.
