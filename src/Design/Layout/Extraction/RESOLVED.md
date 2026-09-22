# `src/Design/Layout/Extraction/` — findings

One reading of copper, two readers. This namespace is railRF's extraction, promoted so LVS can call
it — `brief-lvs-2-shared-extraction.md`, `docs/design/lvs.md` §3, `docs/design/railrf.md`.

**The governing fact for anyone touching this code: railRF's answers are a GATE.** The promotion
changed nothing about them, and it was only allowed to land because it changed nothing about them.
The whole of `tests/Ui.Tests/RailRf/` and `PowerRailExampleTests` — which parses the shipped
`examples/Power Rail/README.md`'s own numbers, committed *before* the change, and compares them
against a live run — pass unmodified: 830 tests. Two consumers of one extraction is the point; two
extractions that agree today is the failure this file exists to prevent.

---

## The precedence inversion: why `PinNaming` is an enum and not four nullable delegates

**This is the trap the whole namespace was built around, and its failure mode is total silence.**

`PdnLayoutNets`' governing rule — the one railRF has always had — is *net(pad) = the schematic's own
binding, else the net stated on the copper*. That is exactly right for railRF, which wants the best
available answer about a board.

It is **catastrophic for LVS**. LVS asks the artwork what the artwork says, gets the *schematic's*
answer back, and every net on every design matches. No exception, no warning, no finding: a tool
that passes everything. That is worse than a tool that fails everything, because nothing about the
output looks wrong — the report is clean, the counts agree, and the design ships.

So the mode is `PinNaming`, it is an **enum**, and it is **required** at every call site.

**"Pass null for the schematic delegates" would have worked too, and was rejected.**
Null-means-artwork-only is already a supported shape here (R-ab1-4d), which is precisely how a later
caller half-supplies the schematic by accident and re-enters the trap without ever deciding to.
Making the caller **write the word** is the entire mechanism; the type system cannot help with a
defect whose symptom is a green result.

`PinNaming.ArtworkOnly` with a schematic-facing delegate supplied is an `ArgumentException`
(R-lvs2-2c), not a parameter quietly ignored — a parameter that is silently ignored in one mode is a
parameter somebody will supply and believe in. It is a programmer's error no document can produce,
so it lives on `UserFacingTextGateTests`' allowlist rather than becoming a coded diagnostic with
nothing for the Messages window to group.

---

## What the promotion cost, and what it preserved

### What moved, and what kept its railRF name

Moved to `CircuitRF.Design.Layout.Extraction`: `CopperPieces`, `LayerRegions.Build`,
`PlacedPins.Of`, `PlacedPin`/`PinSource`, `Regions.Walk`/`.Contains`, `Conductors.Of`.

**`PdnConductor` stayed in `Layout/Pdn`** with its sheet resistance — railRF prices copper and LVS
never prices anything — and gained `AsConductor`, a projection onto the neutral `Conductor` that
`Conductors.Of` enumerates.

**`PdnNetPoint`, `PdnRegion` and `PdnRailRegionSet` came across with `Regions.Walk` KEEPING THEIR
NAMES.** R-lvs2-1's rename table names the types the two readers share and says nothing about these
three, which are railRF's own vocabulary for a rail and its return. R-lvs2-1d forbids an alias, so
renaming them would have been sixty call sites of churn for no reader's benefit.

**`DrcConnectivity` stayed `internal`** and grew an *overload* rather than a third `Extract`: the
DRC does not ask for the joins and pays nothing for them, because locating one costs a Clipper
intersection the two-argument form never performs. Two overloads differing only in their
out-parameter type make `out var` ambiguous, and `out var` is how every existing caller is written —
which is why the ground-reach form is a differently-*named* method (`ExtractWithGround`).

### Two collisions the neutral names created

`LayoutEditorViewModel.CopperPieces()` — a method named after the type it now returns — became
`PartitionedCopper()`, and a test helper named `Regions()` became `Walked()`. Both are the ordinary
cost of giving a promoted type the neutral name; neither is worth an alias.

`RailCliVerbTests`' source scan listed `"PdnRailRegions"` as a forbidden token in `src/Cli/Rail.cs`.
`"Regions"` alone would match `layerRegions`, so the token is now `"Regions.Walk"` — the thing the
scan was actually about.

### `ExtractWithGround` reports the ground reach and unions NOTHING

It is additive in `PieceJoin`'s sense: the same walk, keeping a set of integers the other forms
discard. **It does not merge the grounded pieces.** Two backside vias reaching the same undrawn
metal are one net to LVS and must not become one net to the DRC's net-aware rules or to railRF's
island count, which are answers about *drawn* copper. `LayoutRead`'s `NetTable` does the merge on
its own account.

### `PlacedPins.Of` gained a scope and an origin side channel, and LVS needed both

- **`PlacementScope.EveryPlacement`.** railRF skips a placement with no designator (R-ab1-1b: a pad
  keyed on a fabricated designator is worse than no pad, because an anchor would resolve to it).
  LVS keys a device on its **path**, and R-lvs3-3a's last clause exists precisely for a placement
  carrying no designator, no part kind and no schematic id — which is what makes a user-authored PDK
  need no registration. Nothing is fabricated: the pad comes back with a null `Refdes`, which
  `PlacedPin` has always been able to say.
- **`PlacedPinOrigin`**, filled in lockstep with the returned pads: the instance, the resolved cell
  folder, the pin key and the **layer** (a pin lands on the piece under it on its own layer,
  R-ab2-2d). A **list**, not a dictionary keyed on the pad, because two cells of one array can put
  two identical pads in two places and a dictionary would keep one.

`PlacedPins.PinKeyOf` is now the **one** spelling of a layout pin's key, and `TerminalMap` calls it.
Two spellings would have matched nothing at all — which reads as every device being open on a board
that is perfectly connected.

---

## `JoinKind.SameLayerTouch` cannot be produced by today's walk, and that is not a defect

`DrcConnectivity` receives per-layer **unioned** geometry and splits it with `DrcRegions.Components`,
so two pieces of metal meeting on one drawing layer are already one component before the union-find
sees them: every edge the walk retains bridges a via barrel to a conductor on another layer. A via
drawing layer that happened to coincide with a conductor's would not change that, because the union
would then make them one component too.

So the kind is classified **by measuring the two pieces' layers**, not by which loop produced the
union. The unreachable half is still written because brief 9's boundary stitching unions on its own
account, and a classification keyed on provenance would be wrong there and right nowhere.

---

## The broad phase, and the three things that are easy to get wrong in it

### Candidates must be tested in ASCENDING piece order

`PieceAt` with a null layer can be covered by pieces on several layers, and the linear scan it
replaced returned the **first** of them in list order. A grid bucket plus a board-spanning list is
two ascending sequences, so `PieceIndex.PieceAt` **merges** them rather than concatenating.
Concatenating changes which piece answers an any-layer query, which is not a performance change at
all. Held by 2,000 randomised pieces and 10,000 queries, index against a scan written out in the
test.

### A pour goes on ONE list, not into every cell

A board-wide pour's bbox covers every cell, so smearing it across the grid is a quarter of a million
insertions for one shape. A piece spanning more than 64 cells goes on a "spans the board" list every
query tests exactly instead. That is fine **because there are a handful of pours**, and it is stated
at the class header because it stops being fine the moment somebody reaches for the same grid over
*shapes*. The gate puts the pour **last** on purpose: at index 0 it would answer every query on its
first exact test and the assertion would prove nothing.

### The cell size is the MEDIAN piece bbox, and the mean would have been wrong

One board-wide pour drags a mean up by orders of magnitude and coarsens the grid into uselessness on
exactly the boards that need it. R-lvs2-3d's "not configured" is the other half: a knob here is a
knob nobody can set correctly.

---

## Winding, and the rotated rect that silently disconnects a board

Two findings that belong here because `LayerRegions.Build` is where they land, even though one was
fixed upstream.

**A rotated `RectShape` came out of the flatten with its corners swapped.** `RectShape` and
`RoundedRectShape` both declare *"Normalized so X1&lt;X2, Y1&lt;Y2"* as their contract;
`LayoutCoordinateWalk.Transform` transformed the two corners and left it at that, so a 90°
placement, a mirror or a negative scale mapped lower-left to upper-right and broke it.

It is **not cosmetic**, because of where the rect becomes a ring. The readers that defend against a
reversed rect do it with `Math.Min`/`Math.Max` on a *bounding box*. The one that does not is
`LayoutFlattener.Flatten`, which emits `X1,Y1 X2,Y1 X2,Y2 X1,Y2` verbatim — so a reversed rect
becomes a **clockwise** ring. `LayoutClipper.Rule` is `FillRule.NonZero`, so that ring carries
winding −1 and **cancels against correctly-wound copper it overlaps**: the union punches a hole
exactly where a rotated pad meets the trace it is soldered to, and the two come back as separate
nets. Nothing throws. A 90°-placed footprint simply stops being connected — in the DRC's partition,
in railRF's islands, and in LVS's. Fixed at source in `LayoutCoordinateWalk.Transform`, the one
mutator flatten, rotate and scale share.

**The same cancellation is reachable two more ways**, and the fix for those is here:
`LayerRegions.Build.FaceOneWay` turns each shape's rings so the outer one winds positively before
they are unioned. A mirrored or negatively-scaled placement reverses every ring its sub-cell
contributes, because a reflection reverses orientation — and **the polygon tool follows the user's
clicks, so a polygon drawn clockwise is clockwise.** Neither is exotic.

**Deliberately NOT in `LayoutClipper.ToClipperPaths`**, which is the other candidate and the funnel
every export shares: reversing a ring changes the **order** its vertices are written in, and the
interchange gates compare exported files byte for byte. Orientation matters only when rings from
*different* shapes are unioned, and `LayerRegions.Build` is the one place in this repository where
that happens to copper.
