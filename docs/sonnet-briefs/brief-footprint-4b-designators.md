# Brief 4b — the designator a placement owns, and the label nothing has ever drawn

**Series:** [SMT footprints](brief-footprint-0-overview.md) · **Tag:** `R-fp4b-n` · **Phase:** P2
**Area:** `src/Design/Layout/LayoutModel.cs`, `src/Design/Layout/LayoutFlatten.cs`,
`src/Design/Layout/Footprints/`, `src/Design/Layout/Interchange/{PcbReader,PcbExport,PcbWriter}.cs`,
`src/Render/Renderers/LayoutRenderer*.cs`, `src/Render/Layout/LayoutHitTest.cs`,
`src/Ui/Layout/LayoutEditorViewModel.Instances.cs`, `src/Ui/Commands/Layout/`
**Depends on:** [3](brief-footprint-3-update-layout.md), [4](brief-footprint-4-picker-and-import.md) ·
**Blocks:** [5](brief-footprint-5-power-rail-example.md)
**Raised by:** the same first-time designer's pass, 2026-09-20 — he asked whether a component's
instance name could be drawn on the silkscreen, and whether he could move it

---

## 0. What this brief delivers

**A placed instance draws its reference designator on silkscreen, and the user can move it.**

This is not a new feature bolted onto the series. It is the thing brief 5 already assumes:
R-fp5-2c reads *"Each instance carries a refdes drawn on Silk Top — that is the 'component layer'
the designer asked for"*, and at HEAD there is no such thing, in the model or the renderer. Brief 5
cannot be built on top of nothing, and building its example on thirteen hand-placed free-text
objects would mean regenerating that example a second time when this lands. So it runs first.

It is also the last piece of the designer's own question that brief 5 R-fp5-2e flags as currently
unanswerable — *"is C10 shorted in your example?"* — which nobody can answer from a picture of
anonymous copper.

### The one rule this brief is built on

> **The designator belongs to the PLACEMENT, not to the cell. The text is derived; only its
> placement is stored.**

---

## 1. Three places in the repo already name this hole

None of this is speculative. Each of these is a committed comment explaining a workaround for the
field this brief adds, and each of them stops being a workaround when it lands.

**1a. Import drops it on purpose.** `PcbReader.cs:91-99` — *"An `fp_text` is the placement's
reference designator and value — R3, 10k — not the library part's artwork. Importing it into the
CELL would bake one placement's designator into the shared cell and mint a separate cell per
placement."* That reasoning is exactly right and is this brief's premise. `ReferenceOf(node)`
(`:1007`) already reads the designator, in both epoch spellings, and uses it only to name the cell
folder readably.

**1b. Export writes null on purpose.** `PcbExport.cs:108-111` — *"No reference designator:
circuitRF's layout model has no such field, and inventing R1/C2 names would put fabrication-facing
identifiers in a file the user did not author."*

**1c. A part library already carries the prefix.** `ComponentPlxReader.cs:79` reads `refDesPrefix`
into `Metadata["Reference"]`. Read, stored, and used by nothing.

**And the data is already on the instance.** `SchematicToLayoutGenerator.cs:164` sets
`SchematicId = comp.InstanceName`. **Every schematic-generated layout instance has carried its
designator since L5.** Nothing has ever drawn it.

---

## 2. `R-fp4b-1` — the text is derived, never stored

**`R-fp4b-1a`** For an instance with a `SchematicId`, the designator **IS** the `SchematicId`. It is
not copied into a second field. Two fields with one meaning drift, and this repo has paid for that
once already — `LayoutModel.cs:756` cites the version-number scar for exactly this reason. Derived
means a rename in the schematic arrives through Update Layout with no migration, no second write
path, and no possibility of a board that disagrees with the drawing about what a part is called.

**`R-fp4b-1b`** An instance placed by hand carries no `SchematicId` (R-fp3-6c, and that stays true).
It gets `LayoutInstance.RefDes`, a nullable string it owns.

**`R-fp4b-1c`** One accessor, in the shape `RotationDegrees`/`Rot`/`RotDeg` already established
(`LayoutModel.cs:760`): `DisplayRefDes => SchematicId ?? RefDes`. Nothing outside persistence reads
the two fields directly, and nothing writes `RefDes` on an instance that has a `SchematicId`.

**`R-fp4b-1d`** **It is a designator, not free text.** A user who wants arbitrary text on silk uses
the Label tool that already exists (`LayoutEditorViewModel.cs:3521`). Letting the drawn string
diverge from the instance's identity would produce a board whose silkscreen lies, which is the one
outcome a designator exists to prevent.

**`R-fp4b-1e`** Editing a designator in the layout **does not write back to the schematic** — the
rule R-fp3-6d already states, for the same reason. In practice this only arises for `RefDes`,
since an instance with a `SchematicId` has no editable text at all.

---

## 3. `R-fp4b-2` — the placement is stored, and its default is null

Four nullable fields on `LayoutInstance`, each omitted from the file entirely when null — the
additive convention `RotDeg`, `CellInterfaceHash` and `PortDirection` already follow, so **no
`FormatVersion` bump and every existing `.clay` re-serializes byte-for-byte**:

| field | null means |
|---|---|
| `RefDes` | use `SchematicId` (R-fp4b-1) |
| `LabelDx` / `LabelDy` | **auto** — derived from the resolved cell's own extent, every time |
| `LabelRotDeg` | follow the placement, normalized readable (R-fp4b-3) |
| `LabelHeight` | the default height (R-fp4b-2d) |
| `ShowRefDes` | shown (R-fp4b-6) |

**`R-fp4b-2a`** **Null offset means AUTO, and auto is recomputed, not frozen.** The default position
is derived from the resolved cell's silkscreen/courtyard extent — centred above the body, clear of
it by the silk clearance `ChipLandPatternGenerator` already uses (`SilkClearanceMm`, `:46`).

This is the load-bearing decision in the brief. Writing the computed default into the file at
placement time would freeze it, so re-pointing a part from 0402 to 0805 (R-fp3-4a, a supported
gesture) would leave its designator sitting inside the bigger body — correct when written, wrong
afterwards, and wrong silently. This is the same class as R-fp2-3d's "a default that follows the
technology around is a design that changes when you open it somewhere else", and it is why the
commercial tools call the two states *autoposition* and *manual* rather than storing one number.

**`R-fp4b-2b`** A stored offset is **relative to the instance's own origin, in the instance's placed
frame** — so a designator moves and rotates with its part, which is what a user who dragged it there
meant. It is in the PARENT's DBU, because that is the frame the drag happened in.

**`R-fp4b-2c`** The offset is a single function, `FootprintLabel.AutoOffset(cellView, roles)`, with
its own test — never a literal at a call site, because the renderer, the hit-test, the flatten and
the reset command all need the same answer and three of them agreeing by coincidence is not the
same as one of them being right.

**`R-fp4b-2d`** **The default height is a fixed board-wide constant (0.8 mm), not a fraction of the
part.** A board carrying 0402s and a `7343-31` would otherwise print designators an order of
magnitude apart in size, and the smaller ones would be unreadable — which defeats the entire
purpose. Every tool that ships a default ships a fixed one.

---

## 4. `R-fp4b-3` — a designator is never drawn upside down, and never mirrored

**`R-fp4b-3a`** The drawn angle follows the placement and is then normalized into `(-90, 90]`. Rotate
a resistor 180 degrees and its designator stays readable — this is universal across every tool that
draws one, and it is the behaviour a user will assume without being told.

**`R-fp4b-3b`** A `MirrorX` instance draws its designator **un-mirrored**, at the mirrored anchor. No
tool draws mirror-reversed designator text, because the string is there to be read.

**`R-fp4b-3c` — bottom-side placement is a NON-GOAL, stated rather than half-built.** circuitRF has
no notion of board side: `LayoutInstance` has `MirrorX` and nothing else, and mirroring does **not**
flip layers inside circuitRF — `PcbLayerNaming.FlipSide` (`:175`) is called only by `PcbWriter` at
write time (`:846`, `:1045`). A mirrored part's copper still sits on the front copper layer key until
it is written out. Real bottom-side support is a `Side` on the instance plus role resolution that
flips `F.*` to `B.*`, and it reaches DRC, EM stackup orientation and every export. **It is a separate
brief and this one must not start it.** What this brief owes it is R-fp4b-3b, which is already the
right answer on either side.

---

## 5. `R-fp4b-4` — it is artwork, not chrome

**`R-fp4b-4a`** The designator is emitted as a `LabelShape` on the **silkscreen role**, resolved
through `LandPatternLayers` exactly as the body outline is. Not an editor overlay. A screen that
disagrees with the Gerber about what is on the board is the defect this rule exists to prevent.

**`R-fp4b-4b`** It is emitted by `LayoutFlatten`/`LayoutDesignFlatten` — **one place**, which is what
makes Gerber, DXF, GDSII and `.kicad_pcb` export all correct at once, and what keeps the EM path
indifferent (silk is not a conductor; `EmGeometry.Flatten` already ignores it by layer). Confirm
that indifference with a test rather than assuming it.

**`R-fp4b-4c`** **A technology with no silkscreen role draws no designator**, and says so through the
same diagnostic channel `LandPatternLayers.Missing` already uses. It is **not relocated** to
soldermask or to the board outline — R-fp1-3a's rule, unchanged and for the same reason. (The Power
Rail technology has no silk today; brief 5 R-fp5-1 adds it, which is why the two are sequenced this
way.)

**`R-fp4b-4d`** No assembly-layer second copy. Commercial tools conventionally carry a designator on
both silkscreen and an assembly/fab layer; circuitRF ships one. Noted here so the omission is a
decision rather than an oversight.

---

## 6. `R-fp4b-5` — the renderer: a deferred pass, not the instance compile

**This is the bulk of the work, and the reason is structural.**

`LayoutRenderer.Instances.cs:437` skips `LabelShape` inside a placed instance outright — a documented
L3a gap (`src/Render/RESOLVED.md:2293`). It is not an oversight: the instance path compiles a
sub-cell **once** into a reusable per-layer aggregate `SKPath` in cell-local space (that file's
header, R-L3a-3), and per-placement text cannot be baked into a shared path.

Which forces the right design anyway: **the designator is drawn by the parent, because it is the
parent's data.**

**`R-fp4b-5a`** Copy the port-glyph pass, which exists for exactly this shape. `DrawLayer` collects
into a per-frame list (`DeferredPort`, `LayoutRenderer.cs:3349`); `DrawPortGlyphs` (`:3373`) paints
after every layer, every instance and every overlay, and before the transient interaction chrome.
A `DeferredDesignator` list beside it, painted in the same window, is the whole mechanism.

**`R-fp4b-5b`** The instance compile stays geometry-only. Nothing in this brief puts text into a
compiled cell, and the two skips (`Instances.cs:437`, `LayoutRenderDetail.cs:380`) stay exactly as
they are.

**`R-fp4b-5c`** It participates in LOD like any other text: below a legibility floor in device
pixels it is dropped, not shrunk. `DrawBrokenInstancePlaceholder` (`:1489`) already makes this
judgement for its own label and states the reasoning — reuse the shape, not the constant.

**`R-fp4b-5d`** `DocumentExtents` already measures labels (`:75`, `:165`), so `render --fit` grows to
include designators. That is correct. Check for a moved figure golden before reporting done.

---

## 7. `R-fp4b-6` — moving it, and getting it back

**`R-fp4b-6a`** Drag. The schematic already has this gesture end to end — `Tool.MoveLabels`,
`MoveLabelsCommand` (`src/Ui/Commands/Schematic/MoveLabelsCommand.cs`, an old-offsets/new-offsets
snapshot pair), `ResetLabelOffsets` (`SchematicViewModel.cs:649`). Mirror its shape in the layout
editor so a user who has learned one has learned both, and so the undo entry is one command like
every other instance edit.

**`R-fp4b-6b`** **Reset to auto is mandatory, not a nicety.** It clears `LabelDx`/`LabelDy`/
`LabelRotDeg` back to null — i.e. back to *derived*, not back to a remembered number. A dragged
label with no way home is a trap, and it is the first thing a user hits after dragging one by
accident.

**`R-fp4b-6c`** Hit-testing the designator makes it a third selectable kind beside
`ValidSelectedIndices` and `SelectedInstanceIndices` (`LayoutEditorViewModel.Instances.cs:57`).
**This is the fiddliest part of the brief** and the closest precedent is the PCell handle
(`LayoutHandleHitTest.cs`, `LayoutEditorViewModel.PCellHandles.cs`) — a draggable sub-object attached
to an instance, already carrying the "hit the instance or hit its handle" disambiguation this needs.
`LayoutHitTest.LabelHitBbox` (`:426`) gives the bbox.

**`R-fp4b-6d`** Per-instance visibility is `ShowRefDes`, on the selected-instance surface beside
rotation, mirror, mag and array (`:477`). Turning it off for a crowded corner of a board is a
multi-select edit, which that surface already supports — **there is no new global toggle**, because a
view-only switch that makes the screen disagree with the export is precisely R-fp4b-4a's defect. The
silk layer's own visibility already hides designators on screen along with the body outlines.

**`R-fp4b-6e`** The header string `LayoutShapePropertiesViewModel.cs:2410` already renders
(`cell · SchematicId`) gains nothing and changes nothing. It is the same fact shown in the panel
rather than on the board, and the two must keep agreeing.

---

## 8. `R-fp4b-7` — shown by default, and what that costs

> **Open decision — confirm before building.** Recommended: **shown**.

**`R-fp4b-7a`** `ShowRefDes` null means shown. The whole report is that a designer could not tell
which part was which; a designator behind a switch nobody finds does not answer it.

**`R-fp4b-7b`** **The cost, stated plainly:** an existing layout whose instances were placed by
Update Layout has carried a `SchematicId` since L5, so opening it after this lands draws designators
that were not there before, and **exporting it produces a Gerber with silkscreen text it did not have
yesterday.** The `.clay` itself is byte-identical — no field is written — so what changed is the
picture, not the document.

**`R-fp4b-7c`** Two things make that acceptable rather than silent: the text is drawn prominently, so
it cannot be exported without having been seen; and one per-instance toggle reverses it. If the owner
prefers the other default, the only change is this requirement's sense — nothing else in the brief
moves. **The test in §10.9 asserts whichever way it is decided, explicitly, so the default is a
stated fact rather than an emergent one.**

---

## 9. `R-fp4b-8` — what stops being thrown away

Three call sites from §1, closed. **None of this touches `ComponentImport`** (series overview §3's
rule): these are the interchange readers and writers on either side of it.

**`R-fp4b-8a`** `PcbReader` carries `ReferenceOf(node)` into the placement instead of only naming a
folder with it, and reads the `fp_text`'s own position and angle into the per-instance offset — so a
real board round-trips with its designators where their author put them. The cell still gets no
text, and `FootprintTextSkipReason` still applies to the VALUE text (10k), which is a parameter
circuitRF holds elsewhere. Rewrite that comment to say what is now carried and what is still
dropped; a comment that describes a workaround that no longer exists is worse than none.

**`R-fp4b-8b`** `PcbExport` passes the real designator into `PcbFootprintPlacement.Reference`, which
`PcbWriter:685` already writes as `(property "Reference" …)`. Delete the "circuitRF's layout model
has no such field" comment — it stops being true in this brief.

**`R-fp4b-8c`** `ComponentPlxReader`'s `refDesPrefix` seeds the designator of a hand-placed instance
of that part: prefix plus the lowest free number among the layout's own instances. `C` gives `C1`,
`C2`. A part with no prefix gets no designator, not an invented one — R-fp3-6c's principle, that an
instance corresponding to no schematic component must not be given a fabricated identity.
`ComponentLibraryXmlReader.cs:252` currently skips `<text>` wholesale and may do the same, or stay as
it is; say which and why.

**`R-fp4b-8d`** A designator that duplicates another in the same layout is **reported, never
renumbered**. Two parts called C3 is a real condition a user needs told about; silently renaming one
of them is how a board stops matching its BOM.

---

## 10. Gate

`tests/Ui.Tests/Footprints/FootprintDesignatorTests.cs` — new, beside the series' other four.

1. **It draws at all.** A layout with one Update-Layout-placed 0402 renders text matching its
   `SchematicId` on the technology's silk layer. **This fails at HEAD** — `Instances.cs:437` drops
   it — write it first and watch it go red (R-fp4b-5a).
2. **Derived, not stored.** Rename the schematic component, re-run Update Layout, and the drawn
   designator follows with nothing written to `RefDes` (R-fp4b-1a).
3. **Auto is recomputed.** Place an 0402, re-point it to `smt:0805@N` (R-fp3-4a), and the designator
   clears the larger body — the frozen-default regression (R-fp4b-2a).
4. **Readable at every angle.** Placements at 0/90/180/270 and at 37 degrees, mirrored and not: the
   drawn angle is in `(-90, 90]` and no glyph transform has a negative determinant (R-fp4b-3).
5. **One place, four exports.** The same layout through Gerber, DXF, GDSII and `.kicad_pcb`: each
   carries the designator, on that format's silk layer, from the one flatten (R-fp4b-4b). The board
   export carries it as `(property "Reference" …)`, not as flattened outlines (R-fp4b-8b).
6. **EM does not see it.** An EM extraction of a layout with and without designators produces an
   identical `EmProblem` (R-fp4b-4b).
7. **No silk, no designator, and a sentence.** On a technology with no silkscreen role: nothing
   drawn, nothing relocated, one diagnostic naming the technology (R-fp4b-4c).
8. **Additive.** A `.clay` written before this brief loads and re-saves **byte-for-byte**, and its
   `FormatVersion` is unchanged (R-fp4b-2).
9. **The default is what §8 decided**, asserted explicitly on an instance that sets nothing
   (R-fp4b-7c).
10. **Drag, undo, reset.** One drag is one undo entry; undo restores the previous offset; Reset
    clears to null and the designator returns to the derived position — not to the pre-drag one
    (R-fp4b-6a, R-fp4b-6b).
11. **Board round trip.** A `.kicad_pcb` with three placements at hand-moved designator positions
    imports, and each instance's offset matches the file's (R-fp4b-8a).
12. **A duplicate is reported.** Two instances with the same designator: one report line, both
    still drawn as authored (R-fp4b-8d).

## 11. Scope

- **No bottom-side placement.** R-fp4b-3c — named, deferred, not started.
- **No value/comment text.** A designator only. The second string every board tool draws beside it
  is a parameter circuitRF holds in the schematic, and putting a second derived label on silk is a
  decision for whoever wants it, with its own reasons.
- **No assembly-layer copy.** R-fp4b-4d.
- **No free text on an instance.** R-fp4b-1d — the Label tool already exists.
- **No auto-annotation.** Nothing here invents, renumbers or reconciles designators. `SchematicId`
  is what the schematic called it, and that is the whole source (R-fp4b-8d).
- **No change to `ComponentImport`.** Series overview §3.
