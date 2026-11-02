# Layout Review — series overview: help for a person reviewing a board

**Series:** Layout Review · **Tag:** `R-rev{n}-m` · **Briefs:** 1–7
**Area:** `src/Design/Layout/Drc/` (brief 1 only), a new `src/Design/Layout/Review/`, `src/Design/Layout/LayoutModel.cs`,
`LayoutPersistence.cs`, `src/Cli/Review.cs` (new), `src/Cli/Serve/ToolCatalog.cs`, `src/Render/Renderers/` (brief 7),
`src/Ui/Layout/Review/` and `src/Ui/ViewModels/Dock/` (brief 6)
**Found by:** owner request, 2026-10-27. An experienced designer reviewing layout artwork looks for a familiar list of
things, and circuitRF can point at the candidates so the review starts from a short list rather than a blank board.
**Rule:** the large board used as this series' gate is "board C" (brief-impedance-8). It, its vendor and the people who
sent it must not be named anywhere in the repo, and no path to it may appear in a test, a comment or a RESOLVED.md.

---

## 0. What this is, and what it is not

**A review is not DRC.** DRC is the machine answering "does this artwork meet the process's rules": pass or fail, and
the rules come from the technology. A review is a PERSON looking at the board, who can run DRC as one of their tools.
This series gives that person a second tool: a set of built-in checks, each of which marks the places on the board a
reviewer would want to look at, with a sentence saying why. A review finding is never an error. The reviewer reads it
and either changes the artwork or accepts it with a reason.

The owner's decisions (2026-10-27):

- **The review is a separate run, beside DRC, not rules added to the shipped `.ctech` files.** DRC is unchanged
  except for brief 1's additions, which are additive and inert unless used.
- **The checks are built into circuitRF, with editable thresholds.** There is no user-authored review rule and no
  rule file. A new check is added in code, by us, later.
- **Current handling in a trace is out of scope.** The artwork carries no current. railRF already computes current
  density on real board shapes, and that is where the question belongs. The review does not grow a power-trace-width
  check.

**Why not rules in the `.ctech`.** A `.ctech` rule names layers by number. Board C's technology is its own Gerber import:
it carries no DRC rules at all, and its layer numbers (top copper 1/0, top mask 3/0, drill 7/0, outline 8/0) differ from
every shipped technology's (the shipped 4-layer board has its mask on 5/0 and its outline on 11/0). Rules added to the
shipped files would never reach the boards reviews are mostly run on. **The review therefore finds every layer it needs
by ROLE**: copper by stackup order, soldermask and paste per side through `Footprints.SurfaceRoles`, drill layers from the
stackup's via rows, the board outline through `Em3dGenerator.BoardOutlineLayers`, and ground through
`StackupLayer.IsGroundReference`. One review runs unchanged on a shipped technology, an imported one and a hand-built
one.

## 1. What was measured before the series was written (2026-10-27)

`circuitrf check` on board C (Release build), against a scratch copy of its technology carrying four warning rules on top
copper:

| | |
|---|---|
| Wall time / peak memory | **100 s / 1.3 GB** |
| drill ∩ copper ∩ mask opening (via in pad) | **69** — the kind of finding a review is for |
| top copper width < 100 µm | **35,881** — almost all of it noise |
| top copper spacing < 100 µm | 88, many at 0 µm |
| top copper to outline < 250 µm | none reported; whether the outline was actually measured was not confirmed |

Two rules for the whole series follow from this:

- **Findings are grouped and counted, never only listed.** 36,000 rows are worthless to a person. Every surface shows
  each check's count first and the findings grouped by layer under it, and clusters dense markers the way
  brief-impedance-9's Summary does.
- **The board is read once, and the run says how far it has got.** Every check shares one flattened, unioned copy of the
  board and one connectivity partition (`ConnectivityCache`). The run reports progress per check and can be
  cancelled, on `RunControl`'s terms, like `impedance`.

## 2. The checks

| Check id | What it marks | Brief |
|---|---|---|
| `via-in-pad` | a drill inside an SMD pad's mask opening (production does not want them); thermal pads and through-hole pins exempt | 3 |
| `ground-via-distance` | a ground pad with no ground via within *D* | 3 |
| `shared-ground-via` | a ground via serving two or more ground pads | 3 |
| `thermal-vias` | a thermal pad (a large exposed ground pad, e.g. under a PA) with too few vias per mm² | 3 |
| `stacked-traces` | two traces on adjacent copper layers, different nets, running on top of each other for longer than *L* | 4 |
| `edge-copper` | copper closer to the board edge than the pullback | 4 |
| `edge-trace` | a trace closer to the board edge than *E* | 4 |
| `edge-stitching` | a stretch of board edge with no ground pour, or a gap between ground stitching vias longer than *P* | 4 |
| `impedance` | the saved Impedance review's FAIL and WARN traces, read in rather than re-solved | 5 |

## 3. The briefs

| # | Brief | What it adds | Depends on |
|---|---|---|---|
| 1 | [Engine additions](brief-review-1-engine-additions.md) | `MustBeEmpty` rule kind; `near` with a count; a count on `interacting`; `same_net` / `on_ground` | — |
| 2 | [The review run](brief-review-2-review-run.md) | layer roles, the finding model, settings + acceptances in the `.clay`, `circuitrf review`, the MCP tool | 1 |
| 3 | [Pads and vias](brief-review-3-pads-and-vias.md) | the four pad/via checks | 2 |
| 4 | [Copper and the board edge](brief-review-4-copper-and-edge.md) | stacked traces and the three edge checks | 2 |
| 5 | [Impedance in the review](brief-review-5-impedance.md) | the saved Impedance review's verdicts as review findings | 2 |
| 6 | [The Review panel](brief-review-6-review-panel.md) | dockable panel, thresholds, results, markers on the canvas, accept with a reason | 2–5 |
| 7 | [The review report](brief-review-7-report.md) | a PDF a reviewer can circulate, from the panel and `review -o` | 6 |

Build in order. Briefs 3, 4 and 5 are independent of each other once brief 2 is in. Brief 2 ships the CLI so that
every check brief is gated headlessly on a small synthetic layout AND by hand on board C before any UI exists.

## 4. Rules that hold across the series

- **One run.** The panel, the PDF, `circuitrf review` and the MCP tool all read the same `LayoutReviewResult` from the
  same `LayoutReview.Run`. Nothing in `src/Ui` computes a finding.
- **No second classifier.** A trace is what `TraceImpedanceAnalysis`'s trace finder says is a trace; a net is what
  `DrcConnectivity` says is a net; a pad's copper is what `PlacedPins` and the mask opening say it is. A check that
  needs one of these calls the existing code. It does not grow its own.
- **A check that cannot run says so, by name.** No soldermask layer, no outline, no ground reference flagged in the
  stackup: the check reports *not run* with the one sentence that fixes it (e.g. "flag the ground plane in the
  Technology Editor"), and the run carries on with the others. **Nothing is guessed:** there is no "the largest net is
  probably ground".
- **Thresholds are lengths with units**, shown in the layout's display unit and stored in DBU with the layout. A
  threshold never silently takes a different value on a different board.
- **Severity is Warning, or Note for "not run".** A review never produces an Error.
- **Accepted findings are kept, listed and counted**, on the terms of `DrcWaivers` and the Impedance review's
  acceptances: a statement about this artwork, saved in the `.clay`, keyed by place, and gone when the place changes.
- **Gates are counters and outcomes, never wall clock.** A timing is measured by hand on board C and reported in the
  brief's completion note. No new `Category=Benchmark` timing test.
- **Tests are minimal and synthetic.** Each check gets one small hand-built layout per claim (fires / stays silent /
  exempt case), built in code. Board C is not committed and no test names it.

## 5. Not in this series

- Current handling, ampacity and IR drop (railRF).
- User-authored review rules or a review rule file.
- Running DRC from inside the review. They stay two runs, side by side, so each says what it found.
- Live (as-you-draw) review.
- Any change to the trace finder or the cross-section solver (the Impedance series owns both).

## 6. On completion (every brief)

Findings in `src/Design/RESOLVED.md` (the UI half in `src/Ui/RESOLVED.md`, the CLI half in `src/Cli/RESOLVED.md`), never
in any `CLAUDE.md`. Update `docs/user/src/reference/layout-editor.md` (a new "Layout Review" section),
`docs/user/src/reference/cli.md` (the `review` row and section), `docs/design/cli.md` (§ for the verb) and
`docs/user/src/reference/file-formats.md` (the `.clay` keys). Do not run DocGen; the owner regenerates at the end of the
series. Run only the test classes the brief touches, never the full suite or all of `Ui.Tests`.
