# Brief 33 — the fast model and the return plane

**Series:** [railRF](brief-railrf-0-overview.md) · **Tag:** `R-rail33-n` · **Phase:** defect, measure first, changes numbers
**Area:** `src/Design/Layout/Pdn/PdnCopperClassifier.cs`, `src/Design/Layout/Pdn/PdnGraphExtractor.cs`
(`Extract`'s classification, `GraphBuild.Trace`, `GraphBuild.Pour`, `PourDominatedRefusal`),
`PdnGraphSettings` (`PourCellsAcross`, `MaxPourCells`)
**Depends on:** 31 (the right rail and the right return), 32 (a converged Accurate to gate against)
**Evidence (outside the repo, never copy into it):** the fourth field report's workspace and brief 30's
`h30` harness, held by the owner (README beside them): cfg `planefix-refspread` forces the plane to
Spreading; `REFNET=GND` stands in for brief 31.
**Rule:** the board, its customer and the reporter must not be named anywhere in the repo.

---

## 0. What was measured

The designer's rail, correctly seeded, reference net GND on the inner layer, 30 mA, Release:

| Reading | Drop at the load | vs converged Accurate (~4.0 mV, brief 32) | Time |
|---|---|---|---|
| Fast, plane inferred **Trace** (28.4 squares) — the default | 5.38 mV | **+34 %** | 11 s |
| Fast, plane forced **Spreading** | 4.53 mV | **+13 %** | 12 s |
| Accurate, 6 cells across | 3.93 mV | — | 136 s |

§7's gate is 5 % on trace-dominated paths, and a refusal on pour-dominated ones. **Neither fast
reading meets it, and neither refuses.** (Before brief 31 the Trace reading was −150 MV; that was the
split return, not this.)

**Why the plane classifies as a trace at all.** The classifier's equivalent ribbon comes from area and
perimeter. A 49 × 53 mm plane with 62 rings of antipads has a perimeter so long that it reads as 28.4
squares of ribbon, over the threshold of 10. The same test read the Bottom Copper pour, when it was the
reference, as 205.7 squares. The ribbon test was derived for rail copper that current runs ALONG
(`TraceSquaresThreshold`'s own note). A return plane is the opposite case — current spreads under the
rail — and brief 4's pour refusal deliberately does not look at the reference, so nothing checks it.

## 1. `R-rail33-1` — where the fast model's error is (measure; no fix yet)

- **Split the drop into rail and return**, in Fast (both readings) and in converged Accurate: the
  breakdown rows already carry both sides. The rail itself has three spreading pieces (the top-side 3v3
  and VDD pours, meshed coarsely by `Pour()`), so the +13 % may be the rail's pours, the plane, or both.
  Say which, in millivolts.
- **The coarse pour's pitch.** `PourCellsAcross = 4` meshes a 49 mm plane at ~12 mm. The return current
  enters and leaves through vias and pads millimetres apart; at that pitch the constriction around each
  is not resolved. Measure the plane's return drop against `PourCellsAcross` (4, 8, 16, 32) and against
  local refinement at the attachments only.
- **Check that `Pour()` never joins copper that is not joined.** Brief 30 saw a 2.5 mm² island inside an
  antipad end up in the same netlist component as the plane only when the plane was meshed coarsely. After
  brief 31 that island is no longer reference, but the lookup (`At()` by grid index, `AddLookup` by
  bounds) can register a point inside a hole on the surrounding piece's cell. Show whether it does, on a
  fixture: an island inside an antipad, a coarse cell spanning both.

## 2. `R-rail33-2` — the fix, from what §1 finds

1. **A reference piece is never priced by the trace closed form unless the user forces it.** The ribbon
   test is a statement about rail copper; on the return it has no meaning. Reference pieces classify as
   Spreading, with a reason that says so ("the return plane: current spreads under the rail, so it is
   meshed"). A reference piece forced to trace stays possible and stays drawn as forced.
   Side effect, measured by brief 30: the plane's `MinimumFeatureWidthDbu` then only feeds the reason
   text, and it is ~8 s of the remaining 11.5 s — make the reference piece's reason not need it, or
   compute it once per board content (a cache keyed on the geometry, never on object identity:
   `TechnologyCache` hands back shared instances).
2. **The coarse mesh resolves what the answer depends on.** Refine where current enters and leaves (every
   attachment on the piece — the same idea as the mesh's `RefinementBands`), until Fast agrees with
   converged Accurate on the field board to 5 %. State the cell count it costs; the fast model must stay
   fast (seconds, not the mesh's minutes).
3. **Where it cannot, refuse** — as §7 already does for a pour-dominated rail path — naming the piece
   and saying Accurate answers it. On this board Accurate is minutes at most once brief 32 lands, so a
   refusal is acceptable, and a wrong number with no sentence is not.

## 3. Gates

1. The field board, the designer's own document, briefs 31-32 landed: Fast is within **5 %** of the
   converged Accurate answer, or refuses with a sentence naming the region. Record both numbers.
2. `PdnFastExtractorTests.FastAgreesWithAccurateOnTracesAndRefusesOnAPour` and the rest of
   `PdnFastExtractorTests`, `PdnRefusalCauseTests` still pass. Where a number moves on a fixture whose
   reference used to classify as a trace, say by how much and which side of Accurate it moved to.
3. The shipped Power Rail example's answer: record before and after; a change needs its reason.

## 4. Tests (minimal)

1. A holed return plane whose area and perimeter read as more than 10 squares: classified Spreading, with
   the return-plane reason; forced Trace still honoured.
2. A two-attachment return on a plane (source return and load return 20 mm apart, the rail a simple
   trace above): Fast's return drop within 5 % of Accurate's.
3. §1's island-in-an-antipad fixture, if §1 shows the join happens: the island is not joined.

## 5. On completion

Findings in `src/Design/RESOLVED.md`, never in any `CLAUDE.md`. The designer is waiting for this rail's
number; when 31-33 have landed, record the Fast and Accurate answers on the designer's document there, and tell
the owner so the note to the designer can say railRF answers that rail.
