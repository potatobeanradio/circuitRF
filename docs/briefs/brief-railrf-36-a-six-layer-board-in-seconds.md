# Brief 36 — a six-layer board in seconds

**Series:** [railRF](brief-railrf-0-overview.md) · **Tag:** `R-rail36-n` · **Phase:** defect, netlist must not change
**Area:** `src/Design/Layout/Drc/DrcConnectivity.cs` (`Extract`, `FirstTouching`),
`src/Design/Layout/Extraction/Regions.cs` (every `DrcConnectivity.Extract` call),
`src/Design/Layout/Extraction/CopperPieces.cs`, `src/Design/Layout/Pdn/PdnRailConnectivity.cs` (`Walk`),
`src/Design/RailRf/RailArtwork.cs` (`PadsFor`), `src/Ui/RailRf/RailRfViewModel.Open.cs`,
`src/Ui/RailRf/RailRfViewModel.PadRead.cs`, `src/Ui/RailRf/RailRfViewModel.Parts.cs` (`RebuildParts`)
**Depends on:** 30, 35 · **Blocks:** 37's gates (they need the board to open)
**Evidence (outside the repo, never copy into it):** the round-9 field-report workspace — a six-layer
board, its Gerber-imported `.clay`, `.ctech`, `.crail` and `.crlib` — held by the owner.
**Rule:** the board, its customer and the reporter must not be named anywhere in the repo.

---

## 0. What was reported, and what is measured

On the six-layer board the window's crash trail reads `pads read — 4 pad(s), 607484 ms` (a second
board: 497 s). Four pads, ten minutes: the cost is not per pad, it is the board. While it runs, a click
on the board, the Turn button or a rotate "thinks forever" — the designer's words were that it did not
respond — and a turned-part notice for a part he had already rotated stayed up, because the re-read that
would clear it was the same ten minutes.

Measured headlessly on this Mac, Release, `circuitrf rail` on the same `.crail`: **3 min 45 s** before
a refusal (the refusal itself is brief 37's). Three `dotnet-stack` samples 30 s apart are identical:

```
Clipper64.Execute ← DrcConnectivity.FirstTouching ← DrcConnectivity.Extract
  ← PdnRailConnectivity.Walk ← PdnGraphExtractor.Extract ← RailDcRun.Run
```

`FirstTouching` inflates each via barrel and runs a full `Clipper.BooleanOp(Intersection)` against
every candidate piece whose BOUNDING BOX meets it. A plane's bounding box is the board, so every via on
a six-layer board is intersected with every plane's whole path set — tens of thousands of vertices,
hundreds of antipad holes — once per via per layer. Round 5 found the same lookup (and
`PieceIndex.IndexAt`'s `Regions.Contains` fallback) costing 7–9 s on a two-layer board; this is that,
at six layers. `Regions.cs` also calls `DrcConnectivity.Extract` from three separate entry points, so
one open and one run may extract the same board's connectivity more than once.

## 1. `R-rail36-1` — measure before changing (scratch harness, Debug AND Release)

Break the open and the fast DC run into phases on the field-report board: flatten, `DrcRegions.Components`,
`DrcConnectivity.Extract` (and how many times per open / per run), `FirstTouching` calls and the
vertex count of each piece it clips, `PieceIndex.IndexAt` fallbacks, `PadsFor`, the turned-part read.
Report the table in the RESOLVED entry. No timing tests (owner rule); hold every win with a COUNTER.

## 2. `R-rail36-2` — the via-to-copper test stops being a polygon boolean

A via barrel is small and a plane is huge, so the question "does this barrel touch this piece" is a
LOCAL one. Answer it locally, with the same answer:

- Per piece, a segment index (a uniform grid over its edges, like the wire-clearance grid WB-D uses)
  built once per extraction.
- Barrel against piece: the barrel's centre inside the piece (even-odd ray cast against the indexed
  edges), OR any piece edge within the barrel's grown radius. Only when a `meet` polygon is needed
  (`PieceJoin`, R-lvs2-4d) is it computed — and then by clipping the piece to the barrel's grown
  box first (`RectClip`), never the whole piece.
- Keep `TouchDilationDbu` exactly; an edge exactly at the dilation boundary must decide the same way as
  today. Gate that case explicitly.

## 3. `R-rail36-3` — one connectivity per artwork, not one per question

Cache the extracted pieces and joins keyed on CONTENT (artwork signature + technology content + the
settings that reach it), never on object identity — `TechnologyCache` hands back shared instances.
Every `Regions` entry point and the pad read share it. Counter: two rails and a re-run on the same board
extract connectivity once.

## 4. `R-rail36-4` — nothing slow on the UI thread

The crash trail's `t12!ui` lines put the open's pad read on the UI thread. Move the open's reading onto
the same off-thread path the debounced re-read already uses (`BeginPadRead`/`ReadCopperOffThread`),
with the window showing that it is reading and every board gesture that needs the reading disabled or
queued rather than blocking. Turn and rotate while a read is pending must not start a second full read
behind the first — supersede it.

## 5. `R-rail36-5` — the `RebuildParts` race

`SeriesChainTests.TheFirstRunSweepsOnThePartitionMeasuredOffTheArtwork` fails under load (1 in ~3 in a
filtered run, passes alone) with `IndexOutOfRangeException` from `ObservableCollection.Insert` inside
`RebuildParts.Done` — two threads rebuilding `Parts` at once. Find the second caller (a completion
posted inline when there is no dispatcher is the likely one), make every rebuild run on the UI thread,
and keep the test in the routine gate. This is a lead for the "click and it thinks forever" report too.

## 6. Gates

1. Netlists IDENTICAL before and after on `PdnFastExtractorTests`, `PdnRefusalCauseTests`, the shipped
   Power Rail example and the field-report board (brief 30's `fx` runner pattern).
2. The field-report board opens, and its rail reaches brief 37's answer, in seconds, not minutes —
   reported as a measured number in RESOLVED, held by a counter (clip calls, extractions), not a timer.
3. LVS's joins (`PieceJoin`, the `meet` polygons) unchanged on the LVS fixtures.

## 7. Tests (minimal)

One per claim: the touch decision at the dilation boundary; one extraction for two rails (counter); a
barrel against a holed plane decided without clipping the whole plane (counter of vertices clipped).
Run only the classes touched plus `PdnFastExtractorTests`, `PdnRefusalCauseTests` and the LVS
connectivity tests (`--filter FullyQualifiedName~…`).

## 8. On completion

Findings and the phase table in `src/Design/RESOLVED.md` (the window half in `src/Ui/RESOLVED.md`),
never in any `CLAUDE.md`.
