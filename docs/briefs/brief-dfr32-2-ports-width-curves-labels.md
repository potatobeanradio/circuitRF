# Brief DFR32-2 — ports: width on a wide conductor, ports on a curved edge, labels that stay readable

**Series:** designer feedback round 32 (overview: `brief-dfr32-0-overview.md`). · **Tag:** `R-dfr32-2-<n>`

Three port problems from one field test. All three come from ports placed where a part's pads sit, on a board drawn
from a picture, rather than at the end of a clean 50 Ω line.

## 1. An edge port on a wide conductor takes the whole edge

**The gap.** An edge port's sheet is as wide as the conductor edge it sits on (`Em3dGenerator`: `PieceAt(...)`
returns `(edgeAt, lo, hi)` and the sheet spans `lo..hi`). On a line end that is the line's width, which is right. To
model a shunt part's ground terminal, the designer put ports on the edges of ground pours. One became a sheet
1,429 µm wide. Another became 4,902 µm wide, which is the pour's whole edge clipped to the solve region. When the
`.c3d` was made from the layout, that sheet crossed a via and the port was refused ("P6's bottom edge touches both …
GND and via/71"). The refusal was correct about the sheet it was given. The sheet itself was not what anyone meant,
and nothing showed its extent in the layout before the run. The designer's workaround was to draw small copper bumps
where each part's pad meets the pour, so every port sits on a short edge.

**R-dfr32-2-1 — the layout editor draws each port's resolved sheet extent**, as a short bar along the edge in the
port's colour, from the same function the run uses. A port whose sheet would span more than its neighbourhood is
then visible before anything runs.

**R-dfr32-2-2 — a port on a long edge gets a bounded width.** *Decision D1 for the owner:*
- **(a) recommended:** a port label may state a `Width` (additive, nullable, so existing `.clay` files round-trip
  byte for byte). When none is stated and the edge under the port is longer than 4 × H (H = the dielectric height
  to the return plane), the sheet is centred on the port's point and 2 × H wide, clipped to the edge. The run posts a
  note saying so and naming the stated width as the way to change it. The Properties panel shows the width as a
  length.
- (b) Refuse a port on an edge longer than 4 × H until a `Width` is stated.

The `.c3d` creation path that bakes a layout port into a 3D port rectangle uses the same function, so a newly made
`.c3d` gets the bounded sheet too.

## 2. A port on a curved edge multiplies the mesh

**The gap.** On a balun with small round pads joined by a thin trace, a port placed where the trace meets a round pad
sat on the arc. Palace's memory estimate went up 10 to 100 times. Cutting the trace so the port sat on a straight face
brought it back to normal. The likely cause is that a flat sheet at `edgeAt` meets an arc's polyline in slivers of
metal, or gaps a fraction of a micrometre wide, which Gmsh then resolves.

**R-dfr32-2-3 — measure, then fix.** On a synthetic round pad (polygonised as the importer does) with a thin trace,
place the port on the arc, then at the trace's straight end. Record tetrahedra and the smallest element for each. Put
the numbers in `src/Design/RESOLVED.md`. Then make the conductor flush with the sheet: clip the conductor's outline at
the sheet's plane across the sheet's width, so the sheet meets a straight face. Post a note saying the pad was cut
flat there and by how much. **Go/No-go:** the arc case's tetrahedron count within 1.5× of the straight case. If
clipping is not enough, stop and report what the mesh shows.

## 3. Port labels pile up and are hard to read

**The gap.** Round 31 capped a new port label at twice its line's width. On this board twelve ports sit within about
2 mm of each other, 100–300 µm apart, and each label is stored 200–250 µm tall at the port's own point. The labels
overlap into an unreadable heap. The owner suggested a fixed size, and the designer agreed, asking also that labels
not be stacked on top of each other.

**R-dfr32-2-4 — a port's name is drawn at a fixed screen size**, the size the layout's other annotations use, beside
the port's arrow on its outward side, at every zoom. The stored label `Height` is unchanged and still governs what an
export (GDSII, DXF) writes. A fixed screen size is an annotation, as a schematic pin name is.

**R-dfr32-2-5 — labels do not overlap.** When two port names' boxes overlap on screen, the later one moves outward
along its port's direction until it is clear, with a thin leader line back to the port when it has moved more than its
own height. The rule is deterministic: the same layout at the same zoom always places the labels the same way, so a
picture does not shimmer while panning. `render` uses the same placement, so a headless picture matches the canvas.

## Gates

- One test per claim: the resolved extent drawn equals the run's sheet; a long-edge port's bounded width and its note
  (and the stated `Width` winning); the arc case's tetrahedron count (counter, not timing); label size constant across
  two zooms; two overlapping labels separated, and the placement identical on a second render.
- UiSnapshot PNGs of a dense port cluster before and after, looked at and listed in the note.
- `src/Render` changes: run the layout render tests and `RenderCliVerbTests`.
- Docs: the port section of `layout-editor.md` and `em-3d.md`.
