# Brief DFR28-1 — A via the board edge cuts through stops the 3D mesh

**Series:** designer feedback round 28 (2026-10-26). Closes the open half of R-dfr26-8-4. · **Tag:** `R-dfr28-1-<n>`
**Area:** `src/Design/Layout/Em3d/Em3dLayoutSolids.cs` (the via loop after "Vias: R-em3d3-5d", `_outline`,
`BoardRegions`), `src/Design/Layout/LayoutCrop.cs` (the via/label anchor rule), `src/Design/Em3d/GmshGeoWriter.cs`.
Findings go in `src/Design/RESOLVED.md`.
**Tests:** `tests/Ui.Tests/Layout/CropBoardRound26Tests.cs`, `tests/Ui.Tests/Em3d/` (a `GmshFact`).

---

## 0. What happens

The designer's `TP149_em_asbuilt` cell is a cropped corner of a ten-layer board. Its 3D view's **Capacitance**
setup (Palace, electrostatic) now fails in Gmsh, on his Windows laptop and on this Mac alike:

```
Error : PLC Error:  A segment and a facet intersect at point (125622,119297,360.951)
Error : Invalid boundary mesh (segment-facet intersection) on surface 9742
Warning : No elements in volume 1 2 3 4 5 6 7 8 9 9965 ...
```

Cause, from `model.geo`: a GND through via (pad 0.6 mm, drill 0.3 mm) at **(125.7, 119.2) mm** sits with its centre
**exactly on the cropped board's edge** (the board runs y 114.6…119.2 mm). `LayoutCrop` keeps a via when its anchor
is inside the rectangle, boundary included, so half the barrel — copper tube r = 150 µm and its air fill r = 125 µm —
stands outside the board in the 1 mm air-box padding. The failure point is on the fill cylinder (124.5 µm from the
via's axis) outside the board (y = 119.297 mm).

Measured on a copy of the round-26 TP149 workspace:

| | result |
|---|---|
| as it stands | Gmsh fails, as above, in 30 s |
| that one via deleted | meshes; Palace solves in 1 min 34 s; TP = 4.644 pF, RFin 0.797, RFout 0.255 |

The Oct-24 run of the same setup meshed with no `via/9` in its script at all, so the via entered the 3D problem
after that run. The likely route is 932c2a50 (drill files bound to via entries by named span), which would have
given a previously unspanned via a span. Confirm this; it is the reason the regression appeared without the layout
changing.

The driven `.cem` route survives the same via because its air box has zero padding at that edge, so the box clips
the half-barrel away (`src/Design/RESOLVED.md`, brief-dfr26-8: "the GND via that straddles the edge pushed the box
0.3 mm out").

## 1. What to do

### R-dfr28-1-1 — The 3D build clips a via to the board

In `Em3dLayoutSolids`, a drilled via whose circle is not wholly inside the board region (`_outline` /
`BoardRegions`, when the view has an outline) is built as the extruded intersection of its circle with the board
region, for the copper tube and the air fill both, instead of an `Em3dCylinder`. A via wholly inside is unchanged
(the script must be byte-identical for every existing golden). A via wholly outside is left out, with a note naming
it. One Note per run says how many vias the board edge cut and where the first one is.

The cut face lies in the board's side face. Check that Gmsh fragments it cleanly. If coplanar faces cause their
own trouble, fall back to **R-dfr28-1-1b**: leave the edge-cut via out, with the same note. Stop and report which
of the two you shipped and why; the owner decides between them if neither is clean.

### R-dfr28-1-2 — The crop says it cut a via

`LayoutCrop` keeps its anchor rule (a via is a point that a later edit may move back inside). Its confirmation and
result count the vias whose drill circle crosses the new edge, so the user hears about it at crop time, not from
Gmsh.

### R-dfr28-1-3 — Gate

- A `GmshFact` built from a minimal fixture: a two-layer board, a plane on each layer, and one through via whose
  centre lies on the board edge with 1 mm air-box padding. It fails on today's writer with the PLC error, then
  meshes, and the run's Note names the via.
- On the TP149 copy, the Capacitance setup meshes and solves with the via present. Report C(TP) beside the 4.644 pF
  measured with the via deleted (half a GND via at the edge should move it by well under 1 %).
- `Em3dGeneratorDumpTests` / `PalaceBackendTests` goldens unchanged.

## 2. Not in scope

Sliver cleanup at crop (DFR27-5's open lever), and the openEMS route, which does not mesh this way.
