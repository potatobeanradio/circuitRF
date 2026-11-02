# Brief 2 — the review run: roles, findings, settings, `circuitrf review`

**Series:** [Layout Review](brief-review-0-overview.md) · **Tag:** `R-rev2-n` · **Phase:** model + CLI
**Area:** new `src/Design/Layout/Review/` (`LayoutReview.cs`, `ReviewLayers.cs`, `ReviewModel.cs`,
`ReviewSettings.cs`, `ReviewAcceptance.cs`, `ReviewBoard.cs`), `src/Design/Layout/LayoutModel.cs`, `LayoutPersistence.cs`,
new `src/Cli/Review.cs`, `src/Cli/CliEntry.cs`, `src/Cli/Serve/ToolCatalog.cs`
**Depends on:** 1 · **Blocks:** 3, 4, 5, 6

---

## 0. What this brief builds

The frame every check plugs into, with no check in it yet beyond one trivial one used to prove the frame (R-rev2-9).
After this brief, `circuitrf review board.clay` reads the board once, resolves its layer roles, runs the enabled checks,
and prints grouped findings, and the same result is reachable from MCP.

## 1. Requirements

**R-rev2-1 — Read once.** `LayoutReview.Run(LayoutSource source, ReviewSettings settings, RunControl? control)` takes
the `TraceImpedanceAnalysis.LayoutSource` that `LoadLayout` already produces (view, resolved technology, flattened
shapes). It does not re-read the file or re-flatten. It builds, ONCE per run and shared by every check:
- per-layer unioned regions, the way `DrcEngine.Run` builds `layerRegions` (factor that loop out of `DrcEngine` into an
  internal helper both call; do not copy it);
- the connectivity partition via `ConnectivityCache.Get`, with `GroundReach`;
- a `DrcRegionEval` over both (brief 1's partition argument).
A check that needs none of these does not trigger their construction (lazy, built on first request).

**R-rev2-2 — Layer roles: `ReviewLayers`.** Resolved once from the technology and handed to every check:
| Role | Found by |
|---|---|
| copper layers, top to bottom, with ADJACENCY (consecutive `StackupKind.Conductor` rows) | the stackup |
| top/bottom outer copper, soldermask, paste | `Footprints.SurfaceRoles` (`Top`, `Bottom`) |
| drill layers and what each spans | stackup `Via` rows (`SpanFromLayer`/`SpanToLayer`) |
| board outline | `Em3dGenerator.BoardOutlineLayers` |
| ground reference | `StackupLayer.IsGroundReference`, through `GroundReach` |
A role the technology lacks is null, and every check that needs it reports *not run* (R-rev2-5). The review never asks for a
layer by number.

**R-rev2-3 — Finding model.** `ReviewFinding(CheckId, Layer?, Severity, Sentence, MeasuredText?, MarkerRings, Marker,
Key)`, `Layer` a `LayerKey?` (a finding between two layers, like `stacked-traces`, names the upper one and says both in
the sentence), `Severity` ∈ {Warning, Note}, `MarkerRings`/`Marker` as `DrcViolation`'s (DBU, world coordinates).
`Key` is derived from the check id, the layer and the marker's exact bounding box, on `DrcViolation.Key`'s terms.
`Sentence` is a plain sentence a reviewer reads ("GND via 1.4 mm from this pad; the review asks for 0.75 mm") with
lengths in the layout's display unit, rounded as the rest of the UI rounds them (never twelve digits).

**R-rev2-4 — Result.** `LayoutReviewResult(Checks, Findings, TechnologyName, Diagnostics, Cancelled)` where `Checks` is one
row per check: id, title, ran / not run / cancelled, the not-run reason, finding count, accepted count. Findings are
ordered by check (the table order of the overview), then layer in stackup order, then marker position, so two runs over
unchanged artwork produce identical output.

**R-rev2-5 — Not run is a Note, by name.** A check whose role is missing contributes no findings, its row says *not run*
and why, and the result carries ONE Note naming the fix ("No ground reference is flagged in this technology's stackup —
flag the ground plane in the Technology Editor"). A check that throws is caught, reported as *not run* with the
exception's message, and does not lose the other checks' findings.

**R-rev2-6 — Settings: `ReviewSettings`.** Which checks are enabled and each check's thresholds, every threshold a
length in DBU (or a plain number where it is a count or a density). Each check declares its own thresholds (id, title,
unit kind, default) so the panel and the CLI list them from one table; briefs 3–5 add their rows. Defaults are stated in
SI and converted to DBU through the layout's `DbuPerMicron`. Saved on `LayoutView` as `Review`, beside `ImpedanceReview`,
on its terms: absent means "the defaults", only thresholds that DIFFER from the default are written, and an unknown check
id or threshold id in a file is kept on read and written back (a newer build's settings survive an older build's save).

**R-rev2-7 — Accepted findings: `ReviewAcceptance`.** `Key`, `CheckId`, `Reason`, `AcceptedUtc`, `LayerName`, `Summary`,
saved on `LayoutView` as `ReviewAcceptances`, beside `DrcWaivers` and on `TraceImpedanceAcceptance`'s terms. A finding
whose key matches is reported ACCEPTED with its reason and does not count; an acceptance matching nothing is kept and
listed as stale, never silently dropped. These are NOT DRC waivers and not Impedance acceptances; each list belongs to
its own run (brief 5 says how impedance acceptances are honoured).

**R-rev2-8 — Progress and cancellation.** Per check: "Reviewing: ground-via-distance (3 of 9)" on `RunControl`. A
cancelled run returns what finished, marks the rest *cancelled*, and says so.

**R-rev2-9 — The frame's own proof: the board region and `edge-copper`.** So the frame is gated before any real check
exists, brief 2 builds the board region and its edge band (brief 4 R-rev4-2 / R-rev4-3 define them; they live in
`ReviewBoard.cs` from this brief on) and ships the simplest check in full: copper on any copper layer inside the
`pullback` band (default 0.25 mm), marker = the copper inside the band. Brief 4 owns the check's sentence and its tests
beyond this brief's.

**R-rev2-10 — `circuitrf review <path>`.** In `src/Cli/Review.cs`, on `src/Cli/Authoring.cs`' terms: argument
parsing, refusals and reporting only, every finding out of `LayoutReview.Run`.
- Input: a `.clay`, a cell folder or a workspace + `--cell`, inferred as `lvs` infers it.
- `--checks id,id` / `--skip id,id`; `--set id.threshold=value` with an SI unit (a bare number for a length is a
  refusal, as `render --window` refuses one); `--list-checks` prints every check with its thresholds, defaults and the
  layout's saved values, and runs nothing.
- The layout's saved `Review` applies by default, each `--set` overriding it for this run only; `--defaults` ignores the
  saved values. Saved acceptances apply unless `--ignore-accepted`.
- Output: per check a header line with its count (ACCEPTED counted separately), then findings grouped by layer;
  `--all` lists every finding, otherwise at most 20 per check per layer with "… and N more". `--json` emits the full
  result. Progress on stderr.
- Exit: 0 when it ran (findings are advice); 1 on a refusal, or with `--severity warning` when any unaccepted
  Warning remains; 130 on cancellation. Never 2.
- Read-only: it writes nothing except `-o` (brief 7).
- Register it in `CliEntry.Run`'s switch so the installed app answers it.

**R-rev2-11 — MCP.** A `review` tool in `ToolCatalog` with the CLI's arguments, returning the `--json` result.

## 2. Tests

- `tests/Ui.Tests/Review/ReviewRunTests.cs`: roles resolve on a shipped 4-layer technology AND on a technology whose
  layer numbers are different (an imported-style table built in code) to the same roles; a technology without an outline
  layer reports `edge-copper` *not run* with the sentence; a seeded copper rectangle 0.1 mm inside the outline gives one
  finding and at 0.3 mm none; acceptance by key, and a stale acceptance listed; settings round-trip through the `.clay`
  writing only non-default thresholds, and an unknown threshold id survives a save.
- `tests/Ui.Tests/Review/ReviewCliVerbTests.cs`: the verb run as a PROCESS against `LayoutReview.Run` in process, the
  `--json` outputs identical; a bare-number `--set` refused; exit codes 0 / 1 (`--severity warning`).
Run only these two classes.

## 3. By hand on board C (reported in the completion note, not a test)

`circuitrf review <board C>` with only `edge-copper`: wall time, peak memory, count. This is the baseline briefs 3 and 4
add to. Board C's technology flags Inner 1 as its ground reference and has an outline layer, so nothing should report
*not run*.

## 4. Not in this brief

The other checks (3, 4, 5), the panel (6), the PDF (7).
