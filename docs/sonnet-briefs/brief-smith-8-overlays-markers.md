# Brief 8 — overlays and markers: the reference material under the work

**Series:** [Smith Chart](brief-smith-0-overview.md) · **Tag:** `R-smith8-n` · **Phase:** P2
**Area:** `src/Ui/Smith/` · **Depends on:** 7 · **Blocks:** nothing
**Design note:** [`smith-chart.md`](../design/smith-chart.md) §5.7, §4.5

---

## 0. What this brief delivers

The ability to put other data on the chart — a Touchstone file, a cube in an open `DataSet` — and markers
with VSWR circles on top of it. **None of it is needed to match an impedance**, which is why it is P2; all
of it is what makes the match checkable against the part it is matching.

### `R-smith8-1` — the trace machinery is not rebuilt

Every overlay is an ordinary Data Display `Trace` on the `Plot` brief 5 built. Its quantity is picked
through the **existing** machinery: a raw S-parameter (`S11`, `S22`, …), a virtual Z or Y, or a
`DerivedParameters` mode. `SourceStabilityCircle` and `LoadStabilityCircle` are the two the specification
asks for by name and they already exist — `TraceResolve` computes them and the renderer draws them.

`CubeTraceSpecParser` parses the trace spec; `TraceRowViewModel` is the worked example of a row that picks
one. Do not write a second resolver.

---

## 1. `R-smith8-2` — two sources, and one of them is referenced rather than copied

- **A Touchstone file**, by a path **relative to the document** — the `.cdd` convention, and the one that
  survives an archived or moved workspace. The archive round's repointing work is what made relative
  references actually resolve; read `src/Ui/Archive/` before assuming a bare path works.
- **A cube in an open `DataSet`**, referenced the way a Data Display trace card references one.

**This is the opposite choice from the generator** (brief 1 `R-smith1-7`), and deliberately: an overlay is
reference material the user is comparing against, so a reference is right and a stale copy would be wrong.
The generator is part of the design, so a copy is right and a broken path would be fatal.

A reference that does not resolve is a **row marked unresolved with the path in its tooltip** — the
document still opens, the rest of the chart still draws, and nothing is silently missing.

### `R-smith8-3` — everything renormalizes to Z₀_chart on the way in

The chart has one reference impedance (§3.4 of the note). A Touchstone file referenced to 75 Ω is
renormalized to it through `RfCore`'s own path. **A trace that is not renormalized is a curve in the wrong
place that looks entirely plausible**, which is why this is a requirement and not an option.

### `R-smith8-4` — overlays are reference material, not part of the cascade

- They carry their own colour and style, and they may carry markers.
- They are **excluded from the chart's autoscale** unless the row says otherwise — a stability circle can
  be enormous, and one unlucky overlay should not reframe the work.
- They are **not** in the node array, not in the trajectory set, and not in the network strip.
- They **are** in the picture brief 7 copies, because they are in the `Plot`.

---

## 2. `R-smith8-5` — markers and their VSWR circles

Markers are the Data Display's own `Marker` objects on this `Plot`. That means placement, drag, hit-test,
the info box, the context menu, the editor and persistence all come for free — including `VswrEnabled` and
`VswrValue`, whose circle is `LoadpullSurface.VswrLocus` in the Γ plane and whose drag inverse is
`HarmonicaVswrHandle.VswrThroughEx`.

Brief 5 already set `NextMarkerIndexProvider`, `FindMarkerInfoBoxVmProvider` and `SelectedMarkersProvider`
and gave the info boxes somewhere to be drawn. This brief surfaces the commands.

**`R-smith8-6` — the correction worth repeating, because this tool invites the same mistake:**

> A constant-VSWR circle about a marker is **not** centred on that marker unless the marker is at Γ = 0.

`vswr-locus-gamma-plane.md` derives it, `HarmonicaVswrHandle`'s header records that it was got wrong once
by reading "the matched point" as "wherever the marker is", and a centre at (0.3, −0.2) with VSWR 3 has
its true centre at (0.23, −0.16) — a difference far larger than any grab tolerance. **Call `VswrLocus`;
do not construct a circle.**

Marker persistence rides brief 1's `Markers` block, which stores the Data Display `Marker` shape verbatim
— not a second marker model.

---

## 3. The gate

`tests/Ui.Tests/Smith/SmithOverlayTests.cs` — one test per claim:

1. **A 75 Ω Touchstone overlay lands where 50 Ω says it should** (`R-smith8-3`) — the renormalization, with
   a hand-computed point. This is the one that catches a plausible-looking wrong curve.
2. **Stability circles resolve** through the existing derived path, and a two-port fixture produces the
   published centres and radii.
3. **An unresolvable reference marks its row and does not stop the document opening** (`R-smith8-2`).
4. **A relative path survives a moved document**: write, move the pair, reopen, resolve.
5. **Overlays are excluded from autoscale** by default, and included when the row says so.
6. **A VSWR circle about an off-centre marker is `VswrLocus`'s circle**, not one centred on the marker
   (`R-smith8-6`) — assert the centre, which is where the wrong version differs visibly.
7. **Markers round-trip through `.csmith`** in the Data Display's own shape.

---

## 4. What this brief must NOT do

- **No second trace resolver, no second marker model, no second stability computation.**
- **No overlay in the network strip.** Overlays are chart-only; they are not elements.
- **No embedding of Touchstone content in the `.csmith`.** A reference is the decision (`R-smith8-2`).
- **No change to `LoadpullSurface` or `HarmonicaVswrHandle`.** Both are correct; call them.

---

## 5. On completion

Findings to `src/Ui/RESOLVED.md`. **Never a `CLAUDE.md`.**
