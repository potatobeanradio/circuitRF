# Brief 6 — a part dropped into a layout is a part, not a piece of copper

**Series:** [SMT footprints](brief-footprint-0-overview.md) · **Tag:** `R-fp6-n` · **Phase:** P3
**Area:** `src/Design/Layout/LayoutModel.cs`,
`src/Design/Layout/Footprints/{FootprintDefaults,FootprintLabel}.cs`,
`src/Design/Schematic/{ComponentTypeRegistry,SchematicEditModel}.cs`,
`src/Ui/Layout/{LayoutEditorViewModel.PaletteDrag,LayoutEditorViewModel.Designators,LayoutToSchematicGenerator}.cs`,
`src/Ui/Controls/{LayoutCanvas,SchematicCanvas}.cs`, `src/Ui/ViewModels/SchematicViewModel.cs`
**Depends on:** [2](brief-footprint-2-instance-parameter.md),
[3](brief-footprint-3-update-layout.md), [4b](brief-footprint-4b-designators.md)
**Raised by:** the owner, 2026-09-21 — whether a user could drag a component from the Library
palette into a `.clay` to start a PCB layout-first, and reach the schematic later through Update
Schematic from Layout

---

## 0. What this brief delivers

**A component dragged from the Library palette into a layout becomes a placed part with a name, and
Update Schematic from Layout turns it into the component it always was.**

Three things, in dependency order, each useful on its own:

| | |
|---|---|
| `R-fp6-1` | the reverse direction **says** what it skipped — it still creates nothing for a bare land pattern |
| `R-fp6-2` | the palette drop places a **part**: shared land-pattern artwork, plus an identity on the placement |
| `R-fp6-3` | Update Schematic from Layout creates the component, at the name the board already draws |
| `R-fp6-4` | one designator pool across a cell's two primary views, so that name cannot already be taken |

`R-fp6-4` is listed last and is a **prerequisite of `R-fp6-3`**, not a follow-on. §5 says why:
without it, back-annotation can be forced to rename a part that is already silkscreened onto copper.

### The one rule this brief is built on

> **A land pattern is artwork. A part is a land pattern plus an identity, and the identity lives on
> the PLACEMENT — never on the shared cell, and never inferred from a string the user can retype.**

This is brief 4b's rule (*"the designator belongs to the placement, not to the cell"*) applied one
level further: what the part IS belongs to the placement too, for exactly the same reason.

---

## 1. What is already here — the gap is narrower than it looks

**1a. Palette drag into a layout already works.** `LayoutCanvas.cs:1045` is a complete drop target
with a live ghost, and `LayoutEditorViewModel.PaletteDrag.cs` is its view model. It accepts anything
with a registered PCell generator — every microstrip built-in, every cell a kit contributes.
`CanDropPaletteComponent` refuses `R`/`L`/`C` for one reason only: `HasPCellGenerator`
(`SchematicToLayoutGenerator.cs:488`) finds no generator id for them, so the cursor says no before
release. **This brief adds a branch to an existing gesture; it does not add a gesture.**

**1b. The default footprint is already decided.** `FootprintDefaults.For` gives the nine discrete RLC
kinds `smt:0201@N` on a board technology and `null` everywhere else, and its own comment states why
`SRLC`/`PRLC` are excluded — a three-element branch is a network someone builds, not a part someone
buys. That list is this brief's droppable set, unchanged and un-copied.

**1c. Placing a land pattern by hand already ships.** The Footprint tool —
`LayoutEditorView.axaml.cs:1565` → `FootprintPickerDialog` → `BeginFootprintPlacement`. It starts
from a *case size*. That is the whole difference: it produces copper in the shape of a part, and
nothing that is a part.

**1d. It produces copper with no name, and that is correct.** `SeedDesignator`
(`LayoutEditorViewModel.Designators.cs:355`) seeds from `FootprintLabel.PrefixOfCell`, which reads a
`Reference` parameter out of the cell's `.ccell`. A generated land-pattern cell declares none —
`GeneratedCellStore` calls `CellFolder.CreateCellFolder` with no parameters — so a hand-placed 0402
gets `RefDes = null`, deliberately: R-fp3-6c, *an instance corresponding to no schematic component
must not be given a fabricated identity*. **So a board authored entirely with the Footprint tool has
no designators at all until the user types each one**, which is the second reason this brief is
worth building, independent of back-annotation.

**1e. The identity cannot live on the cell.** Update Layout from Schematic places R1 as an instance
of the **bare** land-pattern cell (`SchematicToLayoutGenerator.cs:782-795`). A resistor and a capacitor
at 0402 share one generated cell, correctly — the artwork is identical, and the store is
content-addressed on generator + parameters + technology. The schematic↔artwork link is carried
entirely by `LayoutInstance.SchematicId`. A layout-first part has no schematic component for that
field to point at yet, which leaves the **instance** as the only honest home for what the part is.

**1f. The reverse direction skips it in silence.** `LayoutToSchematicGenerator.cs:171`:

```csharp
if (!builtIn && kitRef is null)
    continue;   // a foreign generator no part claims — nothing to name it after
```

`builtIn` is a six-entry table of microstrip kinds; `kitRef` is a kit part reference. `smt:0402@N`
is neither, so it `continue`s **with no report line**. The forward direction does not behave this
way: a component with no artwork is reported, `"{comp.InstanceName} ({label}): {reason} — skipped."`
(`SchematicToLayoutGenerator.cs:183`).

---

## 2. `R-fp6-1` — the reverse direction says what it skipped, and still creates nothing

**`R-fp6-1a`** A resolved instance whose cell carries a `PCellOrigin` that names no component —
neither a built-in microstrip kind nor a kit part nor (after `R-fp6-2`) a part kind of its own —
**remains skipped**. Nothing is created for it. A land pattern placed by the Footprint tool is
artwork the user drew, exactly as a drawn polygon is, and Update Schematic from Layout has never
invented a component for a polygon.

**`R-fp6-1b`** It is **reported**, once per run, as one aggregate `Info` line naming the count and
the remedy: *N placements are land patterns with no part behind them, so no components were created
for them; drop a component from the Library palette to place a part that has one.* Aggregate rather
than per-instance because a hand-authored board can hold a hundred of them and a hundred identical
lines is a report nobody reads.

**`R-fp6-1c`** It is `Info`, not `Warning`. Nothing is wrong: the user placed artwork and got
artwork. The line exists because "nothing happened and nothing was said" is indistinguishable from a
broken command, which is the failure the forward direction's own skip report already exists to
prevent.

---

## 3. `R-fp6-2` — the palette drop places a part

**`R-fp6-2a` The droppable set is `FootprintDefaults.IsDiscreteRlc`, called, not restated.** Nine
kinds. A second list here would be a second list to keep in step with the first, and this series'
overview already gates against exactly that shape of duplication.

**`R-fp6-2b` The footprint is `FootprintDefaults.For(kind, Technology)`** — the same function the
schematic placement path calls (`SchematicViewModel.cs:3473`), so a resistor placed in the schematic
and a resistor dropped in the layout land on the same case size on the same board. `null` from that
function is a **refusal**, not a fallback: on a non-board technology there is no land pattern to
give, `CanDropPaletteComponent` answers no, and the cursor says so before release. An MMIC die
design does not silently sprout chip resistors.

**`R-fp6-2c` The cell is the shared land-pattern cell, unchanged.** The drop resolves through
`ResolvePCellCellRef` exactly as today — same `GeneratedCellStore`, same content addressing, same
cell folder a schematic-driven placement of the same case size would reuse. **Nothing in this brief
mints a per-component cell.** Two 0402 resistors and an 0402 capacitor are three instances of one
cell, which is what makes re-pointing, DRC, flatten and every export continue to work with no
changes at all.

**`R-fp6-2d` The instance records its kind — one new field.**

```csharp
/// <summary>The component this placement IS, for an instance that corresponds to no schematic
/// component yet — the SymbolKind's own name. Null on every instance that has a SchematicId (the
/// schematic knows) and on every land pattern placed by the Footprint tool (nothing knows).</summary>
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public string? PartKind { get; set; }
```

Nullable, omitted when null, **no `FormatVersion` bump** — the additive convention `RotDeg`,
`CellInterfaceHash` and `RefDes` already follow, so every `.clay` written before this re-serializes
byte for byte.

Stored as the `SymbolKind` **name**, not the enum's number, and an unrecognised value is treated as
absent rather than as an error — a file written by a later version stays readable, and a part this
version cannot name degrades to the R-fp6-1a case instead of failing to open a board.

**`R-fp6-2e` Why a field and not the `RefDes` prefix.** Within the nine the prefixes happen to be
distinct (`R`, `L`, `C`, `SRL`, `SRC`, `SLC`, `PRL`, `PRC`, `PLC`), so parsing them back would work —
right up until a user renames `R1` to `Rin`, which is an ordinary thing to do to a board and which
`CommitSelectedInstanceRefDes` permits. The prefix is how the **name** is seeded. It is not the
record of what the part is, because it is a string the user owns and this would be the one place
that quietly depended on them not editing it.

**`R-fp6-2f` The designator is seeded by the existing machinery**, with the prefix supplied by the
drop rather than by the cell: `ComponentTypeRegistry.InstancePrefix(kind)` → `SeedDesignator`, which
already picks the lowest free number and already scans `DisplayRefDes` so it sees linked and
hand-placed instances alike. `SeedDesignator`'s own contract is unchanged; §5 widens what it counts
as taken.

**`R-fp6-2g` The drag ghost is the land pattern**, through the existing `_paletteDragGeometryCache`
keyed on the resolved generator id. A dropped component and a dropped footprint of the same case
show the same ghost because they are the same artwork.

**`R-fp6-2h` R and M turn it on the cursor**, because the instance-placement gesture already does
(`LayoutEditorViewModel.Instances.cs:337`) and this places through it. Nothing new.

---

## 4. `R-fp6-3` — Update Schematic from Layout creates the component

**`R-fp6-3a`** An instance with a `PartKind` and no `SchematicId` creates a schematic component of
that kind, **named by its own `RefDes`** — not by `ClaimName`. The board already draws that name on
silkscreen; a back-annotation that renumbers it produces a schematic that disagrees with copper the
user is looking at. If the name is not free, that is `R-fp6-4`'s job to have prevented, and if it
happens anyway it is reported and the instance is left alone rather than renamed.

**`R-fp6-3b`** The component takes `ComponentTypeRegistry.DefaultParameters(kind, 0)` — the same call
the existing PCell branch makes (`LayoutToSchematicGenerator.cs:205`). A layout-first resistor
arrives at the registry default and the user sets its value in the schematic. **Nothing in this brief
puts a component VALUE on a layout instance**; see §7.

**`R-fp6-3c`** It is given `Footprint = <the instance's own land pattern>`, resolved from the cell's
`PCellOrigin.GeneratorId`. Without it the very next Update Layout from Schematic would report the
brand-new component as having no artwork — while its artwork sits on the board.

**`R-fp6-3d` Linking transfers the name; it does not copy it.** On creation the instance takes
`SchematicId = <name>` and its `RefDes` is **cleared to null**. R-fp4b-1a is explicit that an
instance with a `SchematicId` stores no `RefDes` — two fields with one meaning drift — and
`DisplayRefDes` then draws the same string from the schematic side, so nothing on the board changes
appearance at the moment of linking. `PartKind` is cleared with it, for the same reason: the
schematic now knows.

**`R-fp6-3e`** The created component is placed on the existing 8-column grid (`GridCols`,
`GridPitchSchematic`) and a `SchematicPCellSnapshots` entry is written, exactly as the PCell branch
already does. A land pattern has no parameters, so the snapshot is empty and the push-back loop is a
no-op — stated because an empty snapshot looks like an omission.

**`R-fp6-3f` No wiring, unchanged and stated plainly.** R-L5-19 stands: this command places and
updates components, and draws no wires. A layout-first board back-annotates to a schematic of
correctly named, correctly footprinted, **unconnected** parts. That is a BOM round trip, not yet a
design flow, and the user must be told so in the change report rather than discovering it: one
closing line naming the count of components created and stating that no nets were derived.

Deriving nets from copper is real and is **not** this brief — `PdnCopperPieces` and the Name Net
gesture (`LayoutEditorViewModel.Nets.cs`) are the machinery, and
[brief-authored-board-2](brief-authored-board-2-net-identity.md) is where that decision already
lives, under its own rule that a board circuitRF drew already states everything the `.ipc` would
state.

---

## 5. `R-fp6-4` — one designator pool across a cell's two primary views

**This is a correctness fix, not a nicety.** `DisplayRefDes` prefers `SchematicId` over `RefDes`
(`LayoutModel.cs:875`): the design's stated position is that R1 in the layout **is** R1 in the
schematic. Two independent name pools contradict that, and both collisions are reachable at HEAD:

- The schematic holds R1, not yet pushed. A hand placement in the layout sees no R1 among the
  layout's instances, takes R1, and Update Layout from Schematic later places the real R1 beside it.
- The layout holds a hand-placed R1. A schematic placement scans only `schematic.Components`, takes
  R1, and Update Layout from Schematic puts a second R1 on the board.

**`R-fp6-4a` The pool is the union**, over the cell's **primary** schematic view and **primary**
layout view: every `EditableComponent.InstanceName`, plus every `LayoutInstance.DisplayRefDes`
(which already covers linked and hand-placed instances both).

**`R-fp6-4b` Both choosers ask it.** `SchematicEditModel.NextAvailableName` and
`FootprintLabel.SeedDesignator` each see one document today. Neither grows a copy of the other's
scan: they take the taken-set as a parameter, and one new function assembles it.

**`R-fp6-4c` Where the sibling comes from.** When it is open, free:
`LayoutSessionRegistry`/`SchematicSessionRegistry` are mirrors of each other, keyed on normalized
path, both owned by `WorkspaceViewModel`. When it is closed, this adds a file read to a path that
touches no disk today — so it is read once per placement session and cached, invalidated on save and
on external change. Placing twenty parts must not pay for it twenty times. **Measure it in Release**:
`.clay` read cost is one of the places the Debug build is misleading by a large factor.

**`R-fp6-4d` Report, never renumber.** R-fp4b-8d already fixed this policy and
`FootprintLabel.DuplicateReports` already implements the after-the-fact half. Choosing a free name at
placement is the cheaper half of the same rule; nothing here renames anything that already exists.

**`R-fp6-4e` Primary views only**, as scoped. A non-primary layout view is a variant land pattern and
is not on the board — and an instance draws its cell's primary view regardless, which
`ResolveFootprintPath` already refuses to let a user work around
(`SchematicToLayoutGenerator.cs:876`).

**`R-fp6-4f` A cell with only one of the two views loses nothing.** The absent side contributes an
empty set and both choosers behave exactly as they do now, which is what keeps this from being a
change to every schematic that has no layout.

---

## 6. Gate

`tests/Ui.Tests/Footprints/LayoutFirstPartTests.cs` — new, beside the series' others.

1. **The cursor says no at HEAD and yes after.** `CanDropPaletteComponent(SymbolKind.Resistor, 2)` on
   a board technology: false before, true after (R-fp6-2a). On a non-board technology it stays false
   (R-fp6-2b).
2. **A dropped resistor is named.** Drop two resistors and a capacitor: `R1`, `R2`, `C1`, each with
   `PartKind` set and `SchematicId` null (R-fp6-2d, R-fp6-2f).
3. **One cell, three instances.** Those three resolve to **two** cell folders — one per case size,
   not one per component — and the resistor's folder is the same one a schematic-driven Update Layout
   produces for the same case (R-fp6-2c).
4. **The Footprint tool is unchanged.** A land pattern placed by hand still gets `RefDes = null` and
   no `PartKind` (R-fp6-1a, guarding 1d).
5. **Skipped, and said.** Update Schematic from Layout over a layout holding three hand-placed land
   patterns: zero components created, exactly one `Info` line, and it names the count (R-fp6-1b/1c).
   **This fails at HEAD** on the line count — write it first and watch it go red.
6. **Created at its own name.** The three dropped parts back-annotate to components named `R1`, `R2`,
   `C1`, of the right kinds, each carrying `Footprint` (R-fp6-3a, 3c).
7. **Linking clears the placement's copy.** After that run each instance has `SchematicId` set and
   `RefDes`/`PartKind` null, and `DisplayRefDes` is unchanged from before the run (R-fp6-3d).
8. **Idempotent.** Running it a second time creates nothing and reports everything unchanged.
9. **No wires, and it says so.** The generated schematic has zero wires and the report's closing line
   states it (R-fp6-3f).
10. **Cross-view collision, both directions.** (a) schematic holds `R1`, layout is empty → a dropped
    resistor takes `R2`; (b) layout holds a hand-named `R1`, schematic is empty → a schematic
    placement takes `R2` (R-fp6-4a/4b).
11. **A non-primary view does not contribute.** A sibling non-primary `.clay` full of `R1`…`R9` does
    not push a schematic placement past `R1` (R-fp6-4e).
12. **One-view cells are untouched.** A cell with a schematic and no layout produces exactly the name
    sequence it does at HEAD (R-fp6-4f).
13. **Additive.** A `.clay` written before this brief loads and re-saves **byte for byte**, and its
    `FormatVersion` is unchanged (R-fp6-2d).
14. **The sibling is read once.** Placing twenty parts with the sibling document closed performs one
    read of it, asserted by a counter, not by a clock (per the standing rule against timing tests).

---

## 7. Scope

- **No net derivation from copper.** §4 R-fp6-3f. It is the step that turns this into layout-driven
  design and it belongs to [brief-authored-board-2](brief-authored-board-2-net-identity.md).
- **No component VALUE on a layout instance.** A dropped resistor arrives at the registry default and
  is given its value in the schematic. A value on the placement would be a second place a component's
  parameter lives, which is the drift this series' own rules keep refusing.
- **No widening beyond the discrete RLC nine.** Sources, ports, SnP blocks, Match networks and every
  microstrip kind are unaffected — the microstrip kinds already drop today through their own
  generators and keep doing exactly that.
- **No auto-annotation.** Nothing here renumbers or reconciles existing designators. R-fp4b-8d's
  rule stands, and §5 only chooses a free name for the part being placed right now. Brief 4b's own
  scope deferred this; `R-fp6-4` is the narrowest part of it and takes nothing else.
- **No per-primitive edit verb in the CLI.** Placement is a gesture. The format remains the way to
  author a document headlessly.
- **No change to the Footprint tool, the picker, or `ComponentImport`.**
- **No LVS.** An instance with a `PartKind` and no component is a layout-first part awaiting
  back-annotation, not an unmatched one. v2 owns that question.

---

## 8. On completion

Findings, traps and anything learned that outlives the change go in `src/Ui/RESOLVED.md` (and
`src/Design/RESOLVED.md` for the model field) — **never in a `CLAUDE.md`**.
