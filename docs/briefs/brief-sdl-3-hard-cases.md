# Brief SDL-3 — Connected placement: the remaining hard cases

**Series:** [Schematic-driven layout](brief-sdl-0-overview-and-groundwork.md) · **Tag:** `R-sdl3-n`
**Area:** `src/Design/Layout/SchematicSync/` (`JointGraph`, `ChainPlacer`, new `PlacementOverlaps.cs`)
**Depends on:** SDL-1 (SDL-2 for the overlap check's footprint cases). **Gate designs:** H1-H7.

---

## 0. What this adds

SDL-1 and SDL-2 cover the common shapes. This brief makes the placer behave predictably on the rest of
what a microstrip or MMIC schematic contains, so a design that falls outside the common shapes still
gives a correct partial result and an exact report rather than a silent mess.

## 1. Cases and rules

| Case | Rule |
|---|---|
| **Width step** (two MLINs of different W, no junction part) | Joined centre to centre (SDL-1 §3.4). Nothing to report: the widths are the schematic's. An offset step (one edge aligned) is out of scope. |
| **Taper** (MTAPER) | An ordinary two-pin part; its pins carry their own widths. No special case; H4 proves it. |
| **Coupled lines** (MCLIN, MBCLIN) | Four pins; each joined pin is a joint, open ends are not. A staircase of coupled sections is a tree and needs no special case; H2 proves the staircase comes out at the coupled lines' own offsets. MBCLIN's two lines are on different layers, so D1's same-layer rule applies per pin. |
| **Layer change** (VIA between two metals) | The VIA's pins are on two layers; each joins the same-layer pin on its net. A net whose two pins are on different layers with no via is not a joint and is reported with both layer names. |
| **Three or more line ends on one net, no junction part** | Not joined. Reported: *"Net N joins three line ends (ML1.2, ML2.1, ML5.1); a junction part (MTEE, MCROSS) is needed to place it."* No tee is inserted (S2). |
| **Long any-angle chains** | SDL-1 D3's exact poses. H6: 20 MCURVEs at 17° end within 1 DBU of the exact double result. A test also proves that rounding per link would have failed H6, so the rule cannot be dropped silently. |
| **Ground and multi-pin ground nets** | Never joints. Ground is the pour's and the vias' job (`GroundArtwork`). CPWG and GSGPAD ground pins are excluded by net, not by part type. |
| **Hierarchical sub-cells with a layout** | A part like any other: its pins come from `CellPins.Resolve`. No special case. |
| **Arrays** (`Rows`/`Cols` > 1) | Not chained. Row-placed and reported once. |
| **A part reached only through a part with no layout** (an ideal R) | The two sides are separate chains, packed separately. Report: *"ML3 and ML4 are joined through R1, which has no layout; they are placed apart."* |

## 2. `R-sdl3-1` — overlaps of unconnected copper

`PlacementOverlaps.Find(plan, target)` runs after placement, through the layout's spatial index (no
pairwise scan over every shape). Copper of two instances on different nets that overlap or touch is a
`Warning`: *"ML7 overlaps MT2 (nets n4, n9): the schematic's lengths make these cross."* Copper the user
drew or placed without a schematic link counts too (SDL-4 relies on this). Nothing moves (S1).

## 3. `R-sdl3-2` — the report as a whole

The run's report groups lines by kind (loop gaps, unjoinable nets, overlaps, footprint notes) so a
large design's output is readable. Each line names the schematic ids involved; clicking a line in
Messages selects those parts in the layout, using the cross-probe LVS already uses (`LvsCrossProbe`).

## 4. Tests

H1-H7 through `SdlChecker`. H5 expects exactly the three-ends line. H7 expects exactly one overlap
warning naming both parts. Plus one test per table row that has a rule but no design (arrays; a part
reached through an ideal R).

## 5. Docs

`layout-view.md` §9.1: the table. User page: the report kinds and what to do about each.

## 6. Gate

As SDL-1. Findings in `src/Design/RESOLVED.md`.
