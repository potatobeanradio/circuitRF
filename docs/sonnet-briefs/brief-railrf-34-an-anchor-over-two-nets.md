# Brief 34 — an anchor that stands over two nets

**Series:** [railRF](brief-railrf-0-overview.md) · **Tag:** `R-rail34-n` · **Phase:** defect, changes numbers
**Area:** `src/Design/RailRf/RailPortAnchor.cs`, `src/Design/RailRf/RailDocumentIo.cs`,
`src/Design/Layout/Pdn/PdnAttachments.cs` (`Resolve`), `src/Design/Layout/Extraction/Regions.cs`
(`Walk`'s `extraRailSeeds`), `src/Ui/RailRf/RailRfViewModel.Board.cs` (`PlaceSource`, `PlaceLoad`),
`src/Ui/RailRf/RailRfViewModel.Import.cs` (`PickRailAt`), `src/Ui/RailRf/RailAnchorEntry.cs`
**Depends on:** 31 · **Blocks:** nothing
**Rule:** the board, its customer and the reporter must not be named anywhere in the repo.

---

## 0. What is left after brief 31

Brief 31 stops a rail seed claiming the RETURN net. The same mechanism still merges any two other
nets: `Regions.Walk` seeds every anchor — a bare coordinate, and also a refdes anchor, which
`PdnAttachments.Resolve` turns into a bare coordinate — on every copper layer except the reference.
A VDD pad on Top with a 3v3 pour under it on Bottom therefore makes one rail of two supplies, and the
answer is plausible and wrong. The field report's board happens not to have this (the only other copper
under its load is ground), but the mechanism is the one brief 30 measured, and field report 4 already
fixed its twin for NET POINTS (`PdnNetPoint.Layer`, "a pad seeded the plane under it").

## 1. `R-rail34-1` — a pad anchor seeds its land

A refdes/pin anchor resolves to the pad's coordinate AND its land layer, from the same place
`PdnNetPoint.Layer` gets it (`PlacedPinOrigin`), and `Walk` seeds only that layer. A through-hole pad
reaches every layer through its own barrel, which the connectivity walk already follows.

## 2. `R-rail34-2` — a coordinate anchor says which copper, or is refused

- `RailPortAnchor` gains an optional `Layer`. `.crail` reads and writes it; a document without it
  reads as today (the `.crail` format's eight registration points, brief 1 — check each).
- The window records it: a right-click placement and the pour-click route (`PickRailAt`) take the
  topmost copper under the click among the layers the board view is SHOWING (brief 20's per-layer
  visibility), not the topmost in the stackup. Hiding a layer is how a user says "not that one".
- A coordinate with no layer, standing on copper of MORE THAN ONE galvanic net off the reference layer
  (after brief 31's return exclusion), is refused before anything is priced. The refusal names each
  candidate (layer, net name where one is known, area) and the `.crail` spelling that answers it; the
  window offers the choice as one click per candidate.
- One galvanic net under it (the ordinary case: a pad and the vias under it are one net) seeds as today.

## 3. Gates

1. Every existing fixture, the shipped Power Rail example and the field-report board (the designer's
   document, whose load stands over VDD on Top and ground on Bottom): netlists identical before and
   after (brief 30's `fx` runner pattern). Ground is excluded by 31, so nothing there is ambiguous.
2. The two-supply fixture below refuses without a layer and prices only VDD with one.

## 4. Tests (minimal)

1. VDD pad on Top over a 3v3 pour on Bottom, reference an inner plane: no layer → refused, naming both;
   `Layer = Top` → the rail's copper holds no 3v3 piece.
2. A refdes anchor on that pad seeds its land only, with no layer stated.
3. `.crail` round trip with and without `Layer`.

## 5. On completion

Findings in `src/Design/RESOLVED.md` (the window half in `src/Ui/RESOLVED.md`), never in any `CLAUDE.md`.
