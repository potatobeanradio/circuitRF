# Brief 30 — where the minutes go

**Series:** [railRF](brief-railrf-0-overview.md) · **Tag:** `R-rail30-n` · **Phase:** investigation, measure first
**Area:** `src/Design/Layout/Pdn/PdnGraphExtractor.cs` (`Extract`, `GraphBuild.Build`, `Trace`,
`Pour`), `src/Design/Layout/Pdn/PdnCopperClassifier.cs`, `src/Design/Layout/Pdn/PdnMeshExtractor.cs`
(`MinimumFeatureWidthDbu`, `ResolveReferenceCopper`), `src/Design/Layout/Extraction/Regions.cs`,
`src/Design/RailRf/RailArtwork.cs` (`PadsFor`), `src/Design/RailRf/RailDcRun.cs`,
`src/Design/RailRf/RailPlaneRun.cs`
**Depends on:** brief 29 (commit `8803b248`) · **Blocks:** nothing
**Evidence (outside the repo, never copy into it):** the field-report-4 workspace and its `.crail`,
held by the owner. Rewrite the `.crail`'s absolute `ArtworkCellRef` relative, and regenerate
`.generated-cells` with `GeneratedCellsLifecycle.RegenerateAll` from the scratch harness first, or
every part reads as unresolved.
**Rule:** the board, its customer and the reporter must not be named anywhere in the repo.

---

## 0. What is already known

Brief 29 made a DISCONNECTED rail refuse in ~10 s. A CONNECTED rail on the same two-sided board still
takes minutes, and the designer runs a Debug build on a slower machine: they reported ~10 minutes
where this Mac measured ~5. Measured with a scratch harness (both builds agree to within a few
percent, because the cost is in Clipper, which ships optimised):

| Phase (fast model, DC) | Bottom Copper as reference | Inner plane as reference |
|---|---|---|
| pads (`RailArtwork.PadsFor`) | 7–8 s | 7.3 s |
| walk + conductors + reference extent | 2.6 s | **190 s** |
| sorting (classification) | **148–154 s** | **173 s** |
| measuring (per-piece rasters and pour meshes) | **146 s** | **168 s** |
| solve (`LinearDcEngine`) | — (refused) | 0.1 s |

Two further measurements point at the reference plane rather than the rail:

- **Classification timed per layer:** the rail's own copper classifies in **0.08 s**. Bottom Copper —
  71 pieces, 67,295 vertices, 99 rings — takes **159 s**.
- **The largest Bottom Copper piece (≈49 × 53 mm) was classified `Trace`, not `Spreading`.** The
  classifier works out an equivalent ribbon from area and perimeter. A plane full of antipads has a
  huge perimeter, so it can come out as "many squares" and so as a trace. A reference piece read as
  trace goes through `Trace()`: it is rasterised at the width rule's pitch, capped at
  `MaxRasterCells` = 4,000,000, then thinned.

Nothing below the phase level has been measured. **Treat every lead in §2 as a hypothesis.**

## 1. `R-rail30-1` — the breakdown (measure; no code change yet)

Owner rules: scratch harness, not a benchmark test; **Debug AND Release**; no new timing tests.

- **Inside each slow phase, per piece and per call:** `PdnCopperClassifier.Classify` split into
  `DrcRegions.Components`, area/perimeter, `MinimumFeatureWidthDbu` (how many bisection steps, what
  each `InflatePaths` pair costs at 67k vertices) and `PortsOn` (how many `Regions.Contains` calls,
  and what each costs on the plane). `GraphBuild.Build` per piece: which class, which path
  (`Trace`/`Pour`), raster cell count, whether it hit the ceiling, thinning time, `CellAreas` time.
- **The 190 s walk stage with the inner plane attached:** split `Regions.Walk` (and
  `DrcConnectivity` inside it), `ResolveConductors`, `ResolveReferenceCopper` under `AsImported`, and
  brief 29's own `PdnRailConnectivity.Refusal`. It is new code on that path and must be shown not
  to be the cost.
- **The paths nobody has timed:** Accuracy (`PdnMeshExtractor`) on the same rail; the frequency
  sweep and the plane modes (`RailPlaneRun`, which calls the mesh extractor more than once); and
  what the WINDOW re-runs after an edit (`NotifyArtworkChanged`, the debounced pad re-read from
  brief 28). A user waits for the whole of that chain, not only the DC run.
- **Report** the per-phase and per-piece tables, Debug and Release, in the RESOLVED entry.

## 2. `R-rail30-2` — the leads, each to be confirmed or refuted by §1

1. **Is the reference plane being priced as a trace?** If §1 confirms it, that is a CORRECTNESS
   question before it is a speed one. A plane thinned to a skeleton and priced by the closed form
   along it is OPTIMISTIC, and brief 4's pour refusal deliberately does not look at the reference.
   Check it against Accuracy on the same rail (§2.9 rule 4). Say which number moves and by how much
   before anyone decides whether the classifier should treat holed pieces, or reference pieces,
   differently. **Do not change the classification rule in this brief.** Bring the measurement to
   the owner.
2. **The same reference work repeated on every run.** The reference copper, its classification and
   its raster/mesh depend on the artwork, the technology, the reference layer, the extent and the
   graph settings. They do not depend on the rail's anchors or loads. Measure how much of a run is
   reference-only work. Then say whether caching it (keyed on content, not on object identity —
   `TechnologyCache` hands back SHARED instances) would pay across rails, reruns and the window's
   re-runs after an edit.
3. **`MinimumFeatureWidthDbu` on a 67k-vertex pour.** It runs a bisection with two `InflatePaths` per
   step, and it runs in both the classifier and `Trace()`. Is it most of the 159 s? Could it run on
   the piece it actually sizes rather than on the whole union?
4. **Point-in-copper tests done as Clipper boolean ops.** `Regions.Contains` intersects a 2-DBU
   square with the WHOLE path set, once per point, and `PortsOn` calls it for every attachment inside
   a piece's bounds. Count the calls on the plane.
5. **`ResolveReferenceCopper` under `AsImported`.** It takes the whole ~1,830 mm² pour. Measure it
   against what the rail's footprint needs. Changing the default extent changes the answer, so that
   is the owner's call, not this brief's.
6. **`PadsFor`'s 7 s**, now the largest cost ahead of the extraction on a refused rail. What inside
   it: the turned-parts reading from field report 4, or the flatten?

## 3. `R-rail30-3` — what may be fixed here, and what may not

- **A change that leaves the netlist IDENTICAL may be made in this brief.** That covers caching,
  faster membership tests, and not repeating work. Gate it by comparing the extracted netlist before
  and after (element count, every element's value, `Ports`, `NodeCells`) on the existing
  `PdnFastExtractorTests` / `PdnRefusalCauseTests` fixtures and on the field-report board in the
  harness. Hold the win with a COUNTER — e.g. how many times the plane was classified across two
  rails — never a wall-clock threshold.
- **Anything that changes a number comes back to the owner first, with its measured effect.** That
  includes the classification rule, raster pitch or ceilings, the pour cell count, and the default
  reference extent. The fast-vs-accurate gate (§7: agree within 5% on trace-dominated paths, refuse
  on pour-dominated ones) must still hold.
- **Cancellation granularity must not regress.** `RunControl.TickStage` inside the per-piece loop is
  what makes Stop answer within one piece. A cached or batched path needs a check at the same
  granularity.

## 4. Tests (minimal)

One test per claim, counters not timers, and run only the classes touched plus
`PdnFastExtractorTests` and `PdnRefusalCauseTests` (`--filter FullyQualifiedName~…`). Not the full
suite, and not all of `Ui.Tests`.

## 5. On completion

Findings, and both tables, go in `src/Design/RESOLVED.md`, never in any `CLAUDE.md`. End with a
ranked list: each remaining lead, its measured share of the wait, whether it changes a number, and
the expected saving. That list is the next brief's input.
