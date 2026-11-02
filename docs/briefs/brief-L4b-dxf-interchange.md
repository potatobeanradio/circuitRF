# Sonnet Brief — Phase L4b: DXF import and export

**Design:** `docs/design/layout-view.md` §8 (interchange, R15), §3.2 (curved primitives and the edge-list
model), §3.1a (holes), §3.1b R10e (bitmaps never exported), §2.4 (interchange mappings in `.ctech`).
**Consumes L4a's shared interchange layer** — the neutral model, the `.ctech` mappings (R-L4a-1) and L1g's
reconciliation dialog (R-L4a-2) already exist. Reuse them; do not fork.

**Owner additions beyond §8's original scope:**
1. **Import is first-class**, not a "documented subset" afterthought.
2. **Export curves to their DXF equivalents** wherever possible, rather than flattening.
3. **Preserve arrays** wherever possible.

All three are achievable to a degree GDSII could not manage — DXF is the one target format richer than our
model in the places that matter.

**Gate command is plain `dotnet test`** (fast by default since `brief-test-default-fast.md`).

---

## 1. The mappings are unusually direct — exploit that

### 1.1 Bulge is already our representation

**`LayoutEdge.Bulge` and DXF's `LWPOLYLINE` bulge (group code 42) are the same quantity: tan(sweep/4).**
Not merely convertible — identical. An arc edge exports by **copying the number**, and imports the same way.

This is the single most valuable fact in this brief. It means:
- Arc-bearing `Curve` and `Polygon` outlines → `LWPOLYLINE` with per-vertex bulges, exactly.
- `RoundedRect` → `LWPOLYLINE` whose four corner bulges are `tan(22.5°) ≈ 0.41421356` — the same constant
  already used in §4's `.clay` example.
- **Round-trip is lossless** for every arc.

**R-L4b-1. Arc edges must never be flattened on DXF export.** If a DXF export produces chords where the
model has arcs, something has gone wrong — this is not a tolerance decision.

### 1.2 The rest of the curve mapping

| Ours | DXF | Fidelity |
|---|---|---|
| `Circle` | `CIRCLE` | Exact |
| `RoundedRect` | `LWPOLYLINE` + corner bulges | Exact |
| `Arc` edge | `LWPOLYLINE` bulge | Exact (§1.1) |
| `Cubic` edge | `SPLINE` | **Exact** — a cubic Bézier *is* a degree-3 non-rational B-spline with knots `[0,0,0,0,1,1,1,1]` and the control points unchanged |
| `Path` (centerline + width) | `LWPOLYLINE` with constant width (43), bulges intact | Near-exact; see below |
| `Polygon`/`Curve` with holes | `HATCH` with multiple boundary paths + island detection | Native |
| `Label` | `TEXT` | Metadata, as in GDSII |
| `Bitmap` | omitted | §3.1b R10e |

**This closes an open decision.** §13's DXF-`SPLINE` question was deliberately deferred to L4 to be settled
"against what real downstream consumers actually accept." Settle it now: **emit `SPLINE`, because the
conversion is exact**, and provide a **flatten-to-polyline export option** for consumers that choke on
splines. Report which was used.

**`Path` caveat**: DXF polyline width is a rendering width, not a stroked outline, so **end-cap style
(flush / round / square / extended) does not survive**. Either accept that and report it, or offer an option
to export paths as their outline polygon. Recommend keeping the parametric polyline as the default —
editability is why `Path` exists — with the outline as an option.

### 1.3 Arrays are native

**`INSERT` carries column count (70), row count (71), column spacing (44) and row spacing (45).** A
`LayoutInstance` with `Rows`/`Cols`/`PitchX`/`PitchY` maps to **one** `INSERT`. Arrays are preserved, not
expanded.

**R-L4b-2. Mirror is a negative scale, not a flag.** `INSERT` has no mirror bit; `MirrorX` must become
`xscale = -1`. This is the DXF analogue of L4a's `STRANS` trap — get it wrong and mirrored blocks land
plausibly and incorrectly. Rotation is group 50 (degrees, counter-clockwise); `Mag` is the uniform scale.

`BLOCK`/`ENDBLK` define; nested blocks give hierarchy. Route imported hierarchy through R-L3a-2's existing
load-time cycle check.

## 2. Import — a first-class reader

§8 called import "the hard direction" and it still is: dozens of producers, and files that are valid but
strange. The goal is not to read everything; it is to **read a documented set faithfully and report
everything else per-entity**, so a user always knows what did not come through.

**Supported set**: `LWPOLYLINE`, `POLYLINE` (old-style 2D), `LINE`, `ARC`, `CIRCLE`, `ELLIPSE`, `SPLINE`,
`SOLID`, `HATCH`, `TEXT`/`MTEXT`, `INSERT` (including its array fields), `BLOCK`/`ENDBLK`.

**R-L4b-3. Bulges are load-bearing on import.** §8 already warns that dropping them "silently turns arcs
into chords." A polyline's bulge array must be read and mapped straight onto `LayoutEdge.Bulge`.

**SPLINE import**: degree-3 non-rational converts to `Cubic` edges **exactly**. Higher degree, or rational
(weighted) splines, must be approximated — flatten those at the layout's tolerance and **report the entity
handle and the reason**, so the user can go back to the source if it matters.

**ELLIPSE**: our model has no ellipse. Convert to four `Cubic` edges — accurate to roughly 0.02% of the
radius, which is far below any plausible manufacturing tolerance. Report it as an approximation anyway.

**R-L4b-4. Units come from `$INSUNITS`, and an absent or `0` value must not be guessed silently.** The
header values that matter: 1 = inches, 2 = feet, 4 = mm, 5 = cm, 6 = m, 13 = µm. When unset, **ask** —
a drawing interpreted at 1000× the intended scale is the worst possible silent failure, and unlike a
mis-mapped layer it is not visually obvious in a zoom-to-fit. Default the prompt to mm, and report whatever
was used.

If the source resolution is finer than the layout's DBU, warn with the affected-coordinate count and offer
refinement, exactly as L4a does.

**Layers**: DXF layers are **named strings**, which is the ideal input for L1g's `LayoutLayerMapping` —
its name-first matching was written for precisely this. Unmapped layers auto-create with generated names
and are reported (§8's cross-cutting rule).

**Out of scope, stated so it is not assumed**: DWG (a different, proprietary format), binary DXF (support
ASCII; report and refuse binary clearly), dimensions, leaders, xrefs, paper-space layouts, and 3D entities.
Report each skipped entity by type with a count, and by handle where the count is small.

## 2A. The exported file must open with the design on screen

Owner request, and a real usability problem: a DXF opened in AutoCAD frequently shows empty space because
the stored view has nothing to do with where the geometry is. The user then has to Zoom Extents before they
can see anything — and if they don't know to, they conclude the export failed.

DXF has explicit machinery for this. Do **both** layers below, because they fail independently.

### 2A.1 Correct extents — the portable half

`$EXTMIN` / `$EXTMAX` in `HEADER` are the drawing extents, and `$LIMMIN` / `$LIMMAX` the limits. **Many
viewers zoom-to-extents on open regardless of any stored view**, so getting these right is the part that
helps in the widest range of tools — and it is what makes a user's own Zoom Extents land correctly even when
the initial view is ignored.

**R-L4b-5. Extents describe what was actually written, not what the layout contains.** Bitmaps are omitted
(§3.1b R10e), so they must not contribute — zooming to include an invisible object is worse than not zooming
at all. Instances and arrays **do** contribute their full transformed extent, which L3a's instance bbox
already computes; reuse it rather than re-deriving.

### 2A.2 The stored view — what AutoCAD actually opens with

The active viewport is the `VPORT` table entry named `*ACTIVE`: view centre (group codes 12/22), view height
(40) and aspect ratio (41). Header `$VIEWCTR` and `$VIEWSIZE` mirror it.

**Verify the exact group codes against the public specification before writing** — this is a small table with
unforgiving semantics, and a wrong code here produces a file that loads but frames nothing.

**Note the likely prerequisite:** minimal DXF writers often emit only a `LAYER` table. If the exporter has
no `TABLES`/`VPORT` section yet, one has to be added — budget for that rather than discovering it late.

### 2A.3 Two modes

**Fit to extents (default).** Centre on the geometry bbox; set the view height to frame it with a margin
(~10%).

**R-L4b-6. Fitting must account for aspect ratio, and err toward showing too much.** The viewer's window
aspect is unknown at export time. Use `height = max(bboxH, bboxW / aspect) × margin`, taking `aspect` from the
circuitRF canvas when it is available — it is a decent proxy for the user's screen — and a conservative
default otherwise. A view slightly too large is a non-event; one slightly too small clips the design and
recreates the problem this section exists to solve.

**Match the current circuitRF view (option).** Centre and height taken from the live `LayoutViewport`,
converted to drawing units. This is the mode that makes the DXF open looking like what the user was just
working on, which is often more useful than fit-to-extents when exporting a detail of a large board.

Expose it as a simple choice in the export dialog — *Fit to extents* / *Match current view* — defaulting to
fit, and remember the choice for the session.

### 2A.4 Guards

- **An empty layout has no extents.** A zero or negative view height is invalid and can make a file
  unopenable. Emit a sensible default view and omit or neutralise the extent variables; do not write zeros.
- **A single point or a zero-height/zero-width design** (one horizontal line) hits the same problem in one
  axis — clamp to a minimum span before computing the view.
- Extents are in drawing units and must use the **same scaling as the entities**, derived from the same
  `$INSUNITS` decision (R-L4b-4). A mismatch here is the same class of silent 1000× error.
- Honest limitation for the completion note: **some viewers ignore the stored view entirely** and apply their
  own policy. Correct extents (§2A.1) is the part that degrades gracefully; the stored view is a bonus where
  it is honoured.

## 3. Scope guardrails

- No Gerber or Excellon (L4c). No DRC (L5b), no mesh/EM (L6+).
- No changes to the geometry model, the flattener, `LayoutLayerMapping`, or L4a's neutral layer beyond
  wiring DXF into it. If DXF needs something the neutral model cannot express, **report it** — that is the
  signal L4a's abstraction was GDSII-shaped and needs widening, and it is worth knowing before L4c.
- Do not add a DXF library dependency — write from the public specification.
- Don't touch `src/Core`, `src/Engine`, `RfCore`.

## 4. Gate (acceptance)

1. Builds green (`TreatWarningsAsErrors=true`); `dotnet test` green; no existing test regresses.
2. **Bulge identity (§1.1)** — export a shape with a 90° arc edge and assert the emitted bulge is
   `tan(22.5°)` to full precision; import it back and assert `LayoutEdge.Bulge` is **bit-identical** to the
   original. No arc anywhere in a DXF export becomes a chord (R-L4b-1).
3. **Curve round-trip** — a `Circle`, a `RoundedRect`, an arc-bearing `Curve`, a cubic-bearing `Curve` and a
   curved `Path` all survive export→import as the **same primitive types** with geometry equal within
   tolerance. The cubic case must be exact, not approximate.
4. **Arrays survive (§1.3)** — a 5×5 array exports as **one** `INSERT` with the right counts and spacings,
   and re-imports as **one** `LayoutInstance` with `Rows=Cols=5`, not 25 placements.
5. **Mirror (R-L4b-2)** — all 8 rotation/mirror combinations round-trip to the same rendered result
   (off-screen pixel comparison). This is the test that catches mirror-as-flag.
6. **Holes** — a polygon with two holes exports as a `HATCH` with island boundaries and re-imports with both
   holes intact, satisfying §3.1a R10b (each hole inside the outer ring, non-intersecting).
7. **Hierarchy** — nested blocks import as nested cells and instances; a crafted cyclic file is caught by
   R-L3a-2's load-time check without throwing.
8. **Units (R-L4b-4)** — `$INSUNITS = 4` imports as mm; `13` as µm; **absent prompts** rather than assuming;
   the chosen interpretation is reported. A finer-than-DBU source warns with a count.
9. **SPLINE fallback** — a degree-5 or rational spline imports flattened, with the entity handle and reason
   reported; a degree-3 non-rational one imports as exact cubics.
10. **Unsupported entities** are reported by type with counts and nothing is silently dropped; a file of
    entirely unsupported content imports as an empty cell **with a clear report**, not an error.
11. **Third-party check** — export a design containing arcs, a spline, an array and a hole; open it in an
    independent CAD viewer (name and version recorded) and confirm curves render as curves and the array as
    an array. As with L4a's KLayout gate, this is the only check that catches "correct by our own reader's
    standards."
12. **Real-world import** — import at least one DXF produced by a *different* tool and report what came
    through and what did not. A reader tested only against its own writer is not tested.
13. **The design is on screen on open (§2A)** — export a design placed far from the origin (say centred at
    x = 500 mm) and confirm in the third-party viewer that geometry is **visible without a manual Zoom
    Extents**. Assert `$EXTMIN`/`$EXTMAX` equal the written geometry's bbox, and that a bitmap in the layout
    does **not** widen them (R-L4b-5).
14. **Both modes** — fit-to-extents frames the whole design with margin at several aspect ratios, including
    a design far wider than it is tall (R-L4b-6); match-current-view reproduces the circuitRF viewport's
    centre and height in drawing units.
15. **Degenerate cases (§2A.4)** — an empty layout, a single point, and a single horizontal line each export
    to a file that opens cleanly with a sane view, with no zero or negative view height.

## 5. On completion

Add a "Phase L4b — COMPLETE" entry at the top of `src/Ui/CLAUDE.md`. Call out: **the bulge identity** and
that arcs are therefore lossless in both directions; that **`SPLINE` export was chosen over flattening
because the cubic→degree-3-B-spline conversion is exact**, closing §13's deferred question, and that a
flatten option exists; the **mirror-as-negative-scale** rule; the `$INSUNITS` prompt-rather-than-guess
decision; **§2A's extents and stored view**, including which group codes were used, whether a `VPORT` table
had to be added, and which viewers honoured the stored view versus applying their own; the documented
supported entity set and what is reported; and **whether L4a's neutral model needed widening for DXF** — that
answer tells L4c how much scaffolding it can rely on.
