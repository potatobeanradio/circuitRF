# Brief 1 — three additions to the DRC region engine

**Series:** [Layout Review](brief-review-0-overview.md) · **Tag:** `R-rev1-n` · **Phase:** engine
**Area:** `src/Design/Layout/Drc/DrcLayerExpr.cs`, `DrcLayerExprParser.cs`, `DrcRegionEval.cs`, `DrcEngine.cs`,
`src/Design/Layout/TechModel.cs` (`DrcRuleKind`)
**Depends on:** — · **Blocks:** 2, 3, 4

---

## 0. Why

Every review check in briefs 3 and 4 reduces to "select a region, then mark every polygon left in it". The region
evaluator can already do most of the selecting (`and`, `not`, `sized`, `interacting`, `with_area`, …), and the review
builds its regions as `DrcLayerExpr` trees in code rather than with a second set of Clipper2 recipes. Three things are
missing, and all three are useful to an ordinary DRC deck as well, so they go in the engine rather than in the review:

| Missing | Needed by | Today |
|---|---|---|
| a rule kind that marks every polygon of a region | DRC decks; via-in-pad as a production rule | only by abusing `MinArea` with a huge value, whose message ("area below …") is wrong |
| "within distance *d*", optionally counting how many | ground via near pad; shared ground via; edge stitching | `interacting(sized(…))`, but `Sized` is MITER on purpose (its own doc comment), which overstates a diagonal distance by up to the miter limit |
| net awareness in a selection | ground pads, ground vias, different-net stacked traces | nets exist (`DrcConnectivity`) but only spacing and antenna rules read them |

Every addition is **additive and inert unless used**: an existing `.ctech` evaluates byte-identically, and the existing
DRC tests pass unmodified.

## 1. Requirements

**R-rev1-1 — `DrcRuleKind.MustBeEmpty`.** One region (`RegionA`, or `Layer`). Every polygon of it is a violation, its
marker that polygon. No value; `ValueDbu` is ignored and the Technology Editor shows no value field for it. The
violation's sentence is the rule's own `Name`, followed by nothing measured (`MeasuredText` null). Severity as stated.
Append it to the END of the enum (the `.ctech` stores the name, but tests and the panel sort by value).

**R-rev1-2 — `near(a, b, d[, min, max])`.** New `DrcLayerExpr.Near(A, B, DistanceDbu, MinCount, MaxCount)`, a
whole-polygon selection on `Select`'s terms: keep each polygon of A for which the number of distinct polygons of B whose
closest approach to it is ≤ *d* lies in [min, max]. With no count, the range is [1, ∞) — "within *d* of some B".
`near(a, b, d, 0, 0)` is "no B within *d*".
- Distance is the true Euclidean closest approach (ROUND), not a miter offset. Implement it as A grown by *d* with
  `JoinType.Round` tested against B with the existing one-DBU touch dilation, and the count as the number of B
  components that test hits. State in the record's comment why this one rounds and `Sized` does not.
- Use the B side's bounding boxes to reject pairs before any clip; board C has thousands of drills and pads.
  The gate is a COUNTER of clips performed (R-rev1-5), not a timing.

**R-rev1-3 — `interacting(a, b, min, max)`.** The existing `interacting` gains an optional count of distinct polygons of
B touched, same semantics as R-rev1-2 with *d* = 0. With no count it is exactly today's `interacting`. `not_interacting`
takes no count (it is `interacting(a, b, 0, 0)`; do not add a second spelling).

**R-rev1-4 — `same_net(a, b)` and `on_ground(a)`.** Net-aware whole-polygon selections:
- `same_net(a, b)`: keep each polygon of A that shares a net with some polygon of B.
- `on_ground(a)`: keep each polygon of A on a net `GroundReach.Nets` contains.
- **A derived polygon's nets** are the nets of every connectivity piece (`DrcNetPiece`) it overlaps or touches. A
  polygon touching no piece has no net and is never kept. A polygon touching pieces of two nets is on both. Say this in
  the record's comment; it is the rule a reviewer reasons about.
- The partition is `ConnectivityCache.Get(layerRegions, tech)`, the one the run already builds. **`DrcRegionEval` gains an
  optional partition argument**; without one, a net selection evaluates to empty AND is reported once as a diagnostic
  ("… needs connectivity, which this run did not build"), never silently.
- A technology that flags no ground reference: `on_ground` is empty with the diagnostic that `GroundReach.None` already
  implies. Nothing infers a ground net.
- A shape's own `Net` string (a native or `.kicad_pcb` layout carries names) is NOT a second source of truth here: the
  partition already carries it where it exists. Do not add a name match.

**R-rev1-5 — Text form and round-trip.** Every addition has its parser spelling and `Format` output, and round-trips
exactly (the existing round-trip tests are the pattern). `ReferencedLayers()` covers both operands. `DrcRegionEval`
caches the new nodes like the rest. Add `near` and `interacting`'s count to the cost counters the evaluator already keeps
(`EvaluatedNodeCount`), plus a clip counter for `near`.

**R-rev1-6 — Nothing else moves.** No change to the rule-deck importer (§9A.4a): a deck construct it reports as
unsupported stays reported, even where one of these could now express it. That is a separate decision, left for later.

## 2. Tests (one per claim)

Beside the existing ones — `tests/Ui.Tests/DrcLayerExprTests.cs` (expressions, round-trips) and
`tests/Ui.Tests/DrcAreaDensityNetTests.cs` (net-aware rules):

1. `MustBeEmpty` on a two-rectangle region gives two violations whose markers are the rectangles; on an empty region,
   none.
2. `near` keeps a square whose corner is 0.95·*d* diagonally from a disc, and drops it at 1.05·*d* (the case a miter
   offset gets wrong).
3. `near(a, b, d, 2, )` keeps a disc with two squares within *d* and drops one with a single square.
4. `interacting` with no count evaluates to exactly the same region as before (compare `Paths64`).
5. `same_net` keeps a pad joined by a via to a piece of B on another layer and drops an unjoined pad.
6. `on_ground` with no ground reference flagged is empty and the diagnostic is reported once.
7. Round-trip parse/format for each new spelling.
8. An existing shipped `.ctech` evaluated before and after: identical violations (one technology is enough).

Run only those two classes plus any existing DRC class whose file you touched, e.g.
`dotnet test tests/Ui.Tests --no-build --filter "FullyQualifiedName~DrcLayerExprTests|FullyQualifiedName~DrcAreaDensityNetTests"`.

## 3. Not in this brief

The review itself, any `.ctech` content change, and any change to the width, spacing or density checks.
