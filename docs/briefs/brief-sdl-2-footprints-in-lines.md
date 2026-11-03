# Brief SDL-2 — Footprints in lines

**Series:** [Schematic-driven layout](brief-sdl-0-overview-and-groundwork.md) · **Tag:** `R-sdl2-n`
**Area:** `src/Design/Layout/SchematicSync/` (new `PadJoint.cs`, `ShuntAttach.cs`; `JointGraph`,
`ChainPlacer`), new `src/Design/Layout/PadFacing.cs`, `src/Design/Layout/ComponentImport.cs`,
`src/Design/Layout/Interchange/PcbReader.cs`, `ComponentLibraryXmlReader.cs`, `ComponentArtwork.cs`,
the VIAGND partner placement moved in SDL-0
**Depends on:** SDL-1. **Gate designs:** F1-F7.

---

## 0. What this adds

Real RF schematics mix distributed lines with packaged parts: a DC-blocking capacitor in a 50 Ω line, a
shunt capacitor where two lines meet, resistors in an attenuator, a resistor bridging a Wilkinson's
arms. After this brief, a footprint joins a chain like a line does: its pads meet the line ends the
schematic connects them to.

Two things stand in the way:

- **Where on a pad a line ends** is not defined. A footprint's pin sits at the pad centre; a line's pin
  sits mid-way across its end edge. Joining centre to end puts half the pad under the line and makes the
  layout's line shorter than the schematic's L by half a pad. D-joint (SDL-0 §1) decides it: the line
  ends on the middle of the pad's outer edge.
- **Which way a pad faces** is unreliable. `ComponentImport` corrects each pad's outward direction to
  point away from the part's centre (`ComponentImport.cs:310`, `FacingOf`). Footprints read straight
  from a board or library file keep the pad's long AXIS only, so the sign can be backwards
  (`PcbReader.cs:1173`, `ComponentLibraryXmlReader.cs:361`, `ComponentArtwork.cs:236`). A chain built on
  a backwards pad puts the part on the wrong side of the line.

## 1. Decisions

- **D1 — One facing rule, used everywhere.** `PadFacing.Effective(cellPins)` returns each pin's outward
  direction: the stored direction's axis, with the sign that points away from the centroid of all of the
  part's pads. A part whose pads all sit at the centroid (a single pad) keeps the stored direction, and
  that is reported `Info` once per part. The three readers above call the same function, so a newly read
  file stores the right sign; placement also applies it, because files already written keep the old one.
- **D2 — The pad's joint point is its outer-edge midpoint.** Found from the pad's copper shape on the
  pin's layer that contains the pin point: the pin point plus the outward direction times half the pad's
  extent along it. Computed once per footprint cell and cached on the cell, like `CellPins`. A pin with
  no copper under it joins at its own point and is reported.
- **D3 — A series part is a link in the chain.** A two-pad footprint between two line nets joins both
  through D2's points; the solved rotation makes pad 1 face the incoming line.
- **D4 — A shunt part attaches side-on, at most one per side.** A net holding two line pins plus one or
  more footprint pads: the two lines join each other (an ordinary joint). Each pad then attaches to the
  side of the joint: the part is turned so that pad faces the line, its outer edge touching the line's
  edge on the joint's centre line. The first pad goes on the left of the direction of travel (from the
  lower-numbered port), the second on the right. A third or later pad on the same joint is not placed by
  the chain; it is row-placed and reported. The part's other pad, if grounded, gets the existing VIAGND
  placement beside it.
- **D5 — A line that ends at a pad which also has a shunt part.** A net with one line end and two or more
  pads: the line joins the pad whose part continues the chain (its other pad is on a net that leads to
  more distributed parts). The other pads attach side-on to that line's end per D4. If no pad continues
  the chain, the lowest schematic id is joined.
- **D6 — A bridging part closes a loop; it is never forced (S1).** When both of a footprint's nets are
  already placed by the chain (the Wilkinson resistor), the part is turned and placed so its pads face
  the two line ends, centred between them. If its pad spacing does not match the gap within D4 of SDL-1,
  the misfit is a loop-gap warning naming the part, the required spacing and the actual spacing.
- **D7 — A three-or-more-pad part chains by its first joint.** Its first joint (by schematic order) is a
  tree edge; its other joints are closing joints, checked and reported as any loop is. Grounded pads take
  the VIAGND placement.
- **D8 — Sides must match.** A pad on the bottom side (BSP placement) cannot join a top-side line
  without a via; that joint is not made, and is reported.

## 2. `R-sdl2-1` — `PadFacing`

New file, `src/Design/Layout/PadFacing.cs`, holding D1's rule. `ComponentImport.FacingOf` becomes a call
to it rather than a second copy. Tests: an 0603 read through each of the three readers and through
`ComponentImport` gives identical effective directions; a footprint with a deliberately reversed stored
direction is corrected; a single-pad footprint is reported.

## 3. `R-sdl2-2` — joints through pads

`JointGraph` gains a pin kind (line pin, pad pin) and D2's joint point. `ChainPlacer` uses the joint
point, not the pin point, for pads. D3, D6 and D7 follow from the SDL-1 tree and closing-joint rules with
that one substitution, and need no special case in the placer.

## 4. `R-sdl2-3` — side-on attachment

`ShuntAttach.Place(joint, pads)` implements D4 and D5 after the chain is placed. It never moves the
chain. Overlap between two side-on parts, or between a side-on part and another chain part, is
reported (SDL-3's overlap check reports it if that brief has landed; until then this brief checks
against the parts at the same joint only).

## 5. Tests

- F1-F7 through `SdlChecker`.
- F6 gives the same copper as F1: the board-file footprint with an axis-only direction lands the same
  way round as the imported one.
- F4 with the resistor's pad spacing 0.2 mm short of the arm gap gives exactly one bridging warning with
  both spacings.
- F2 with three shunt parts on one joint: two placed side-on, the third row-placed and reported.
- D-joint: in F1, the distance between the two line pins that meet the capacitor equals the
  capacitor's outer-edge-to-outer-edge pad span, within 1 DBU.
- Visual check: F1, F2, F4 rendered with `tools/UiSnapshot`; PNGs listed in the completion note.

## 6. Docs

`layout-view.md` §9.1: D1-D8. User page: footprints in lines, where the line ends on a pad, side-on
shunt parts, what a bridging warning means.

## 7. Gate

As SDL-1, plus the tests of the three readers whose pad direction changed
(`--filter "FullyQualifiedName~PcbImport|FullyQualifiedName~ComponentLibrary|FullyQualifiedName~ComponentImport"`).
Findings in `src/Design/RESOLVED.md`.
