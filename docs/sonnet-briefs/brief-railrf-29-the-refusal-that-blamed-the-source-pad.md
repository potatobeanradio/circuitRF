# Brief 29 — the refusal that blamed the source pad, after ten minutes

**Series:** [railRF](brief-railrf-0-overview.md) · **Tag:** `R-rail29-n` · **Phase:** defect, measure first
**Area:** `src/Design/Layout/Pdn/PdnGraphExtractor.cs` (the "reaches … only through … spreading"
refusal, around line 1330), `src/Design/RailRf/RailDcRun.cs`, `src/Cli/Rail.cs`
**Depends on:** field report 4 (commit `0cbe6a48`) · **Blocks:** nothing
**Evidence (outside the repo, never copy into it):** the reported workspace and its `.crail`, held by
the owner. The `.crail`'s `ArtworkCellRef` is an absolute path on the reporter's machine; rewrite it
relative for a local run. The layout's footprints live in `.generated-cells`, which is not shipped:
regenerate with `GeneratedCellsLifecycle.RegenerateAll` from a scratch harness first, or every part
reads as unresolved.
**Rule:** the board, its customer and the reporter must not be named anywhere in the repo.
**Outcome:** `src/Design/RESOLVED.md`, 2026-09-22 — including a second connectivity case this brief
did not anticipate (a rail routed on its own reference layer).

---

## 0. What happened

The designer's rail has a coordinate source at (33552.295, 39118.668) µm on the 3v3 connector pad,
a 30 mA coordinate load at (46577.448, 62727.16) µm, and Bottom Copper as its reference. Headless
(`circuitrf rail <crail>`, Debug build) it ran about **ten minutes** in the fast model, then refused:

> Rail '…' reaches (46577.448, 62727.16) µm only through a 1569.72 µm × 1998.782 µm region on layer
> 11/0 …, which the fast model read as spreading rather than as a trace. … What was measured: 2.61 mm²
> of copper with a 5.8 mm perimeter …

That region is the **source's own landing pad**. In the window the user saw "solving…" for the whole
time, then no number, and an empty parts table.

## 1. `R-rail29-1` — the refusal names the wrong cause (fix)

Read the predicate. The refusal fires when a load's nodes are not in the source's union-find root
after joining every NON-spreading node. It then names the LARGEST spreading region on the whole rail
(`worst = classification.Where(Spreading).OrderByDescending(area)`), whether or not that region lies
between the source and the load.

On this board the source and the load are on **two separate islands**: the source on 3v3 (~35 mm²),
the load on VDD (~33 mm²). Only the jumper JP1 joins them, and the rail carries no series part for
it. Measured in field report 4's scratch harness: seeding both anchors gives 2 islands, 68 mm². No
classification of any region could have connected them, and the sentence sent the user to force a
connector pad to "trace", which would not have helped.

**First, verify** with the harness: re-run the island walk from the two anchors and confirm they are
galvanically separate even with every piece counted as trace.

- **Ask the question that separates the two cases before blaming a class.** If the load's nodes are
  not reachable from the source even with spreading nodes INCLUDED in the union, the answer is
  disconnection. Say it: the source and the load are on separate copper, N islands, and nothing
  joins them at DC until a series part says what does. Name the parts whose pads sit on both islands
  (JP1 here, found from `request.Pads`) as the likely bridge, and name the gesture that adds a series
  part. `Regions.Walk` already writes the "N galvanically separate regions" diagnostic; use its
  wording, not a new one.
- **Only when the union WITH spreading nodes connects them** is spreading the cause. Then name a
  region that is actually ON the path (one whose removal disconnects them), not the largest one.
- **The source's and loads' own landing pieces are never the thing to blame.** Current enters and
  leaves there by definition. Decide whether they should be exempt from the spreading refusal
  altogether (priced as spreading via the mesh, or as a contact), and write the reason down.

## 2. `R-rail29-2` — where the ten minutes went (measure, then decide)

Owner rule: measure before fixing, scratch harness rather than a benchmark test, and **both Debug and
Release** (the owner runs Debug; one `.clay` read was 8.3× slower in Debug). Round 3 left this
question open: "where his solve actually spends its time".

- Instrument the fast run's phases on this workspace: resolve, flatten, pads, partition,
  classification, raster, reference extent (the Bottom Copper reference is a ~1,830 mm² pour, meshed
  in full under `AsImported`), graph solve, and the refusal check.
- **The refusal is a connectivity question and must be answered BEFORE any pricing.** If the ten
  minutes were spent pricing a rail that was then refused on connectivity, move the check to the
  front so a disconnected rail refuses in seconds. That alone would have told this user the answer
  ten minutes sooner.
- Report the per-phase table (Debug and Release) in the RESOLVED entry. Fix only what the table
  points at.

## 3. Tests (minimal)

1. Two islands, source on one and load on the other, a two-pin part bridging them that is not a
   series part: the refusal says disconnected, names that part, and does not mention "spreading".
2. The same rail with the bridge added as a series part: no refusal.
3. A genuine spreading-only path (a narrow region that IS the only link): the refusal names THAT
   region, not a larger unrelated pour or the source pad.
4. The connectivity refusal is reached without the pricing stage running (assert with a counter,
   not a timer).

Run only the new test class and `PdnFastExtractorTests`. Not the full suite.

## 4. On completion

Findings go in `src/Design/RESOLVED.md`, never in any `CLAUDE.md`. Tell the designer the rail needs
JP1 as a series part, or a load anchored on the 3v3 side.
