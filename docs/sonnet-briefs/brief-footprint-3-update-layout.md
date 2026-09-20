# Brief 3 — Update Layout places footprints, and the layout editor can place one by hand

**Series:** [SMT footprints](brief-footprint-0-overview.md) · **Tag:** `R-fp3-n` · **Phase:** P2
**Area:** `src/Ui/Layout/SchematicToLayoutGenerator.cs`, `src/Ui/Layout/LayoutEditorViewModel.Instances.cs`,
`src/Ui/Views/Layout/LayoutEditorView.axaml*`, `src/Ui/ViewModels/WorkspaceViewModel.SchematicToLayout.cs`
**Depends on:** [1](brief-footprint-1-land-pattern-generator.md), [2](brief-footprint-2-instance-parameter.md) · **Blocks:** briefs 4-5

---

## 0. What this brief delivers

The thing the designer actually asked for: **Update Layout from Schematic writes artwork for an
S2P, an SRLC and an ordinary capacitor**, because each of them now states a footprint.

Plus the other half he asked for in the same breath — *"if you don't have a netlist, you can always
place the parts by hand that way, on top of the same pads at that layer"* — which is a
place-a-footprint gesture in the layout editor that needs no schematic at all.

---

## 1. `R-fp3-1` — one branch, ahead of the chain

`SchematicToLayoutGenerator.ResolveComponentLayout` (`src/Ui/Layout/SchematicToLayoutGenerator.cs:505`)
resolves in a fixed order today: kit reference, then `CellRef`, then a registered PCell generator,
then null ("an ordinary electrical-only component").

A stated `Footprint` is consulted **first**, before all of them.

**`R-fp3-1a`** First and not last, because a stated footprint is an explicit choice a user made and
the others are inferences. The one case this reorders in practice is a component that has BOTH a
`CellRef` and a `Footprint` — which R-fp2-3c says never gets one by default, so it can only arise by
a deliberate act, and honouring the deliberate act is right.

**`R-fp3-1b`** `smt:` resolves through `ChipLandPatternGenerator` into the content-addressed
`GeneratedCellStore`, exactly as a microstrip PCell does. One generated cell per (case, density,
technology) triple. Nothing new is invented for storage.

**`R-fp3-1c`** A path resolves through `ExternalCellRef.ResolveCellDir` and
`CellFolder.ResolvePrimary`, exactly as `CellRef` does at line 561 — same refusals, same sentences.
A `.clay` path that names a file rather than a cell folder is accepted directly, because Custom
means "this file".

**`R-fp3-1d`** None resolves to null and falls through to the existing chain unchanged. A component
with no footprint behaves today exactly as it does now.

---

## 2. `R-fp3-2` — the technology the pattern is generated against

`RunLayoutUpdate` already hands the generator `layoutVm.Technology` — the LAYOUT's resolution, its
own `TechRef` first and the workspace default only as a fallback. The footprint generator takes the
same one.

**`R-fp3-2a`** This matters more here than for microstrip, and the existing
`ReportTechnologyDivergence` (`WorkspaceViewModel.SchematicToLayout.cs:44`) does not cover it: that
check fires only when the schematic holds a microstrip component. A schematic of thirteen
capacitors on one technology, laid into a layout on another, is silent today and would place its
lands on the wrong layers.

**`R-fp3-2b`** So the divergence report gains a second trigger: **any component carrying a
`Footprint`**. Reported, not refused — same rule the existing check states, for the same reason.

---

## 3. `R-fp3-3` — placement, and why it is now measurable

`PlaceNewInstances` measures the cells it places and spaces them by half the largest
(`GridPitchDbu` is the fallback for a reference that resolves to no geometry — the 2026-09-15 report
about three kit cells 20 mm apart).

**`R-fp3-3a`** A generated land pattern HAS geometry, so it measures, and thirteen 0402s land on a
sensible pitch rather than at 10 mm centres. Nothing to change — this is a note that the existing
fix already covers the new case, and a test that asserts it rather than assuming it.

**`R-fp3-3b`** `LayoutInstance.SchematicId` keeps being written, unchanged. That is v2 LVS's match
key and this brief must not break it.

---

## 4. `R-fp3-4` — a re-pointed footprint updates in place

Changing a component's `Footprint` and re-running Update Layout must move the existing instance to
the new cell, not add a second one.

**`R-fp3-4a`** The existing `cellRefChanged` path (line 201) already does exactly this for a
`CellRef` that moved, and reports it. A footprint change is the same event and takes the same path
and the same report line.

**`R-fp3-4b`** Setting a footprint to **None** on a component that already has an instance
**removes** the instance, and says so in the change report. Leaving orphaned artwork behind would
mean the layout says a part is there and the schematic says it is not — which is the divergence
this whole series exists to close.

---

## 5. `R-fp3-5` — the pad-count contract

The overview's §1f. **Pad count must equal port count, and a mismatch is reported and NOT placed.**

**`R-fp3-5a`** Port count is the component's own, as the schematic uses it —
`EditableComponent.PortCount`, and for an `SnP` that means `GetEffectiveSnpPortDefs().Length`,
because `RefNode` adds a pin. **A 2-port S2P with `RefNode` set is three ports** and does not fit a
two-pad chip land. This is the one that will bite; it gets its own test row.

**`R-fp3-5b`** Pad count is the resolved layout view's pin count — `LayoutView`'s pins for an
imported cell, `PCellResult.Pins` for a generated one.

**`R-fp3-5c`** The refusal joins `NoLayoutWarnings`, which is already reported unconditionally and
uncapped (`SchematicToLayoutGenerator.cs:38`). The sentence names both numbers and the component:
*"C7 states footprint 0402, which has 2 pads, and the component has 4 ports. Nothing was placed."*

**`R-fp3-5d`** Reported and skipped, never placed-and-flagged. Artwork on the board with the wrong
pad count is artwork somebody routes to.

---

## 6. `R-fp3-6` — the layout editor's own picker

Two gestures, and they are different.

**`R-fp3-6a` — re-point a selected instance.** A `Footprint` combobox on the selected-instance
surface (`LayoutEditorViewModel.Instances.cs:477`, beside rotation, mirror, mag and array), which
re-points the instance's `CellRef` through the existing `ReplaceSelectedInstance` path — so it is
one undoable command, like every other instance edit. Same row content as brief 2's combobox
(R-fp2-4b's metric twin included).

**`R-fp3-6b` — place one by hand.** A footprint entry in the layout editor's placement surface, so a
user with no netlist can drop an 0402 onto existing pads. This is the designer's own words and it is
not the same as R-fp3-6a: there is nothing selected to re-point.

**`R-fp3-6c`** An instance placed by hand carries **no `SchematicId`**, and that is correct: it
corresponds to no schematic component. Nothing in this series may invent one for it, and v2's LVS
will report it as unmatched, which is the truth.

**`R-fp3-6d`** Re-pointing an instance that DOES carry a `SchematicId` **does not write back to the
schematic.** The layout and the schematic diverge, which is what Update Layout exists to reconcile
and what the change report exists to say. A silent write-back would make an artwork edit change a
simulation.

---

## 7. Gate

`tests/Ui.Tests/Footprints/UpdateLayoutFootprintTests.cs` — new.

1. **The three the designer asked for.** A schematic holding an `S2P` with `Footprint = smt:0402@N`,
   an `SRLC` with `smt:0603@N` and a `C` with the 0201 default. Update Layout places three
   instances, each resolving to a cell with two pads on the technology's top copper. **This test
   fails at HEAD with three `NoLayoutWarnings` and zero instances** — write it first and watch it
   go red.
2. **The S4P case.** `S4P` with `Footprint = smt:0402@N` places nothing and reports both counts
   (R-fp3-5c).
3. **`RefNode` counts.** `S2P` with `RefNode` true and `smt:0402@N` places nothing and reports
   3 against 2 (R-fp3-5a).
4. **Re-point updates in place.** Run, change the footprint, re-run: one instance, new cell,
   reported once. Not two instances (R-fp3-4a).
5. **None removes.** Run, set None, re-run: zero instances, reported (R-fp3-4b).
6. **Placement is measured.** Thirteen 0402s land within a bounding box proportional to the pattern,
   not at 10 mm centres (R-fp3-3a).
7. **`SchematicId` survives.** Every placed instance carries it; a hand-placed one carries none
   (R-fp3-3b, R-fp3-6c).
8. **Divergence is reported.** A schematic of capacitors, a layout with its own `TechRef`, and the
   report names both technologies (R-fp3-2b).
9. **One undo.** A re-point through the layout editor's combobox is a single undo entry
   (R-fp3-6a).

## 8. Scope

- **No routing, no ratsnest.** R-L5h-3 removed persisted ratsnest geometry deliberately; nothing
  here brings it back.
- **No write-back to the schematic.** R-fp3-6d.
- **No footprint in the layout palette's own library tree** — brief 4 decides what the picker
  offers; this brief only needs the gesture to exist.
