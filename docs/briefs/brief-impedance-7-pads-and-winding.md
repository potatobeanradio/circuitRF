# Brief 7 — a trace into a pad, and copper that cancels itself

**Series:** [Impedance review](brief-impedance-0-overview.md) · **Tag:** `R-imp7-n` · **Phase:** defect fix
**Area:** `src/Design/Layout/LayoutClipper.cs` (`ToClipperPaths`, `RingsToClipperPaths`),
`src/Design/Layout/LayoutFlattener.cs` (`Flatten`, `WithHoles`), `src/Design/Layout/Em/TraceImpedanceAnalysis.cs`
(finding traces: pieces, chains, pours, pads), `src/Design/Layout/Em/TraceCopper.cs`
**Depends on:** 6 · **Blocks:** nothing
**Rule:** the boards, their vendors and the reporter must not be named anywhere in the repo.

---

## 0. What was found

Brief 6's §3 investigation built synthetic boards to hunt the "no capacitance" refusal and turned up two
defects that are not that one. Both are recorded in `src/Design/RESOLVED.md` under brief 6. Neither was
reported by a user; both sit under the most ordinary thing on a board — a trace ending on a pad.

Stackup for every case below: Top copper 35 µm, 500 µm of εr 4.4, a solid plane (35 µm) drawn as copper
under the whole board; DBU 1000 per µm.

## 1. `R-imp7-1` — a trace that runs into a pad is not found as a trace

**Repro.** `Rect(Top, −6000, −475, 0, 475)` (a 950 µm trace, 6 mm long) plus `Rect(Top, 0, −1500, 3000, 1500)`
(a 3 mm square pad at its end), plane under both. `TraceImpedanceAnalysis.Analyze(… Layers = [Top])`
reports **zero traces**. The same trace with no pad is one trace at 49.1 Ω. A via land at the same end
(a `ViaShape`) keeps the trace; a pad overlapping the trace's end by 1 mm loses it too. The probe on the
same board prices the trace at 49.14 Ω everywhere more than a width from the pad, so the copper and the
cross-section are fine — it is the analysis's trace FINDING that drops it.

**Do first: find which rule drops it, and say so in RESOLVED.** Candidates, in the order to check:
the island's classification as a pour (the pad makes the island's largest width 3 mm — under the
5 mm ceiling `WidthRange` computes here, so check what else the pour test reads, e.g. area or hole
count); the pad-at-a-trace's-end rule (`APadAtATracesEnd_IsNotPartOfTheTrace` passes with a SMALLER
pad — find the size at which the trace disappears); the chain's minimum aspect. Print the island's
classification and the chain list for this board from a scratch test before changing anything.

**Fix rule.** A pad at a trace's end ends the trace; it never removes it. The trace is reported with
its own length (to where the copper widens), and the pad is neither a trace nor a pour. A genuine pour
with a trace running into it keeps whatever brief 2's survey and the pour rule already say about it —
do not widen this into "pours are traces".

**Gate.** On the repro board the analysis reports one trace on Top, length 6 mm ± one station, Z₀
within 1 % of the probe's at x = −3 mm; the pad is not a second trace. `APadAtATracesEnd_IsNotPartOfTheTrace`
still passes.

## 2. `R-imp7-2` — a polygon wound the "wrong" way cancels copper it overlaps

**Cause (read, not yet measured).** `LayoutFlattener.Flatten` returns a `PolygonShape`'s or `CurveShape`'s
rings in the order and winding they were stored (`WithHoles` concatenates them as given), and a
`RectShape` as `X1,Y1 → X2,Y1 → X2,Y2 → X1,Y2`, whose winding flips when `X1 > X2` or `Y1 > Y2`.
`LayoutClipper.ToClipperPaths` hands those straight to Clipper2 under `LayoutClipper.Rule = NonZero`. So:

- two overlapping shapes wound opposite ways sum to winding 0 in the overlap — **the overlap is not
  copper**;
- a hole wound the SAME way as its outer ring sums to 2 — **the hole is filled**.

**Observed.** A 3 mm pad drawn as a `PolygonShape` overlapping a trace's last 200 µm gave the probe a
different answer at 507 of 804 points depending only on the pad's vertex order.

**Blast radius.** `ToClipperPaths` is the one conversion every consumer uses: DRC (`DrcRegions.Expand`),
the layout booleans, the interchange exporters, railRF's copper pieces, the EM extractors and the
impedance tools. An editor-drawn polygon, a pasted or imported `PolygonShape`, and a `RectShape` with
swapped corners can all carry either winding. `FromClipperTree` output (every Gerber import) is already
consistently oriented — which is why imported boards mostly look right, and why this has been invisible.

**Fix rule: orientation is normalised in ONE place, `ToClipperPaths` / `RingsToClipperPaths`** — every
shape's FIRST ring (the outer) positive, every further ring of the same shape (a hole) negative
(`Clipper.IsPositive`, reverse where needed). Never in a consumer, never per call site. A `PathShape`'s
outline comes out of `InflatePaths` and is already oriented; leave it. Do not change `FillRule`: NonZero
is what makes overlapping shapes of one net union, and EvenOdd would punch holes where they overlap.

**Measure before and after.** Before changing it, write the failing unit test at the `LayoutClipper`
level (two overlapping rects, one reversed, unioned: the union's area must equal the geometric union's).
Then check the change is a no-op for every correctly-wound input: the existing DRC, boolean, export and
impedance test classes must pass unchanged, and the committed export fixtures must stay byte-identical
(`ConvertCliVerbTests`, `GdsiiExport`/`GerberExport` byte-identity gates). If any output changes,
stop and report which, because that would mean some file on disk depended on the old winding.

**Gates.**
1. The `LayoutClipper` union test above: reversed overlap unions to the full area; a same-wound hole
   is a hole.
2. The probe on the repro board (pad reversed vs not) gives identical answers at every point.
3. A DRC width check on a reversed-winding polygon reports what it reports on the same polygon wound
   the other way.

## 3. Tests (minimal)

One per gate. Run only the classes they sit in plus the byte-identity export gates (§2) —
`--filter FullyQualifiedName~…`, never the whole of `Ui.Tests`.

## 4. On completion

`src/Design/RESOLVED.md` (what dropped the trace in §1; whether §2 changed any existing output), never
any `CLAUDE.md`. Update the impedance user doc's "What counts as a trace" only if §1 changes what it says.
