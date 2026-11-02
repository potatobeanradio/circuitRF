# Sonnet Brief — Phase L1e: Clipper2 geometry operations and Flatten to Polygon

**Design:** `docs/design/layout-view.md` **§3.1a (holes — read this first)**, §6.1 (booleans, offsets, the
one `ToClipperPaths` helper), §3.2 R9d/R9e (Flatten to Polygon, and the operations that flatten implicitly),
§3.4 R10a (net propagation through booleans), §3.1 (the primitive set). **Consumes L1c** (`LayoutFlattener`,
`LayoutHitTest`, selection) and **L1d** (`ReplaceShapeCommand`, handles, self-intersection flagging).

**Scope is L1e ONLY: geometric operations on existing shapes.** The clipboard — cut/copy/paste across cells
with DBU rescale and layer reconciliation — is **L1f**, the last brief of Phase L1.

---

## 0. Decided: polygons and curves carry explicit holes

**Read this section before writing any code. It changes the L0a model, and it is settled — not a question.**

Boolean operations produce results the current model cannot represent. `PolygonShape` and `CurveShape` are a
single closed ring. Subtracting a via pad from a ground pour — *the* most common PCB layout operation —
produces a polygon **with a hole**, and there is nowhere to put it.

**circuitRF supports holes explicitly.** Not keyholing in the database, not refusing the operation. The
reasoning, so it survives:

- Holes are not an edge case here; they are the common case for pours, keepouts and clearances.
- Three of the four consumers already handle multi-contour geometry **natively**: `SKPath` renders it with
  one fill type, point-in-polygon hit-testing is the same ray cast, and Clipper2 works in `PolyTree` form
  already. The flattener needs to return N rings instead of one, which its signature
  (`IReadOnlyList<long[]>`) was already written to allow.
- Keyholing in the database would be **lossy and irreversible**: once a slit is cut, the hole is gone as a
  distinct entity, later booleans behave differently, and L6's mesher would mesh a degenerate zero-width
  channel.
- Only **GDSII** genuinely cannot express holes. That is a *format* limitation, handled at the export
  boundary — exactly like curve flattening (§3.2 R9e). One format's restriction must not deform the
  database, for the same reason §1 refuses to let the display unit into storage.

**Implementation** — additive and backward-compatible, so **no `FormatVersion` bump**:

```csharp
// on PolygonShape and CurveShape
public List<long[]>? Holes { get; set; }   // inner rings; null/absent = no holes
```

Absent means no holes, so every existing `.clay` loads unchanged. Update: `LayoutFlattener` (return outer
ring first, then holes), `LayoutGeometry.BboxOf` (holes never extend the bbox — assert it), `LayoutHitTest`
(a point inside a hole is **not** a hit), `LayoutRenderer` (add hole contours to the same `SKPath`; Skia's
`Winding` fill needs holes wound **opposite** to the outer ring, so normalize winding on construction), and
`LayoutScaling` (scale hole coordinates too — the same "easy to miss" list as cubic control points).

**R-L1e-0 (= §3.1a R10b in the design doc — keep the two in step). A hole must lie inside its outer ring and
must not intersect it or another hole.** Clipper2's
`PolyTree64` output already satisfies this; enforce it on any other construction path (paste, import,
hand-edited file) by running the rings through a `Union` rather than trusting them. A hole that escapes its
outer ring renders and hit-tests as nonsense, and the failure is confusing rather than obvious.

### The GDSII boundary (forward requirement — do NOT build now)

**L4's GDSII writer keyholes on export**, since `BOUNDARY` has no hole concept: cut a zero-width slit from
each inner ring to the outer ring, emitting one self-touching contour. This is standard practice and is what
every GDSII writer does. It applies **only** to the exported bytes — the database keeps its holes, and
re-importing that GDSII yields a keyholed polygon rather than the original, which is inherent to the format
and must be stated in the export dialog's per-format fidelity note (§8) alongside curve flattening.

DXF (multiple polylines / `HATCH`) and Gerber (region polarity, `LPD`/`LPC`) both express holes natively and
need no such treatment.

Add a `// L4: GDSII has no holes — keyhole at export, database keeps them` comment where the writer will
live, and record the requirement in the L1e completion note so L4's brief inherits it.

---

## 1. Clipper2

Add the `Clipper2Lib` package reference to `src/Ui/CircuitRF.Ui.csproj`. The README acknowledgment is
already in place. Managed C#, Boost licence — no native dependency, so it clears the root `CLAUDE.md` bar.

**Our DBU integers go straight in.** Clipper2's `Path64` is `long`-based, which is exactly §1.1's storage
type — no scaling, no float conversion, no precision loss anywhere in the pipeline. This is the payoff for
the integer-database decision and it is worth a comment saying so, because the tempting "scale to a working
integer grid" step that other clipping libraries need is simply absent here. Coordinate magnitudes (≤ ~10⁹)
sit far inside Clipper2's safe range.

**Fill rule: `FillRule.NonZero`** everywhere, stated once and not varied per call site. It is what makes
self-intersection repair (§4) produce the outer region rather than a checkerboard.

## 2. `LayoutClipper.ToClipperPaths` — the single conversion point

§6.1 requires **one** helper that booleans, offsets, DRC (L5b), the mesher (L6), and export (L4) all call,
so the flattening tolerance is never chosen twice with two different answers.

```csharp
public static Paths64 ToClipperPaths(LayoutShape shape, long tolDbu);
public static IReadOnlyList<LayoutShape> FromClipperTree(PolyTree64 tree, LayerKey layer, string? net);
```

- Built on L1c's `LayoutFlattener`; it must **not** re-implement curve flattening.
- Tolerance comes from L1c's single resolver (shape → technology → constant). No new resolution logic.
- **`PathShape` gets its geometry outline here**, via Clipper2 `InflatePaths` on the flattened centerline at
  `Width/2` with the join/cap matching its `End` style.

**R-L1e-1. This is NOT the display outline, and the two must stay separate.** `LayoutRenderer.BuildPathOutline`
keeps using the Skia stroker plus `Simplify` (see `brief-L1-fix-path-seams-and-live-tech.md`), because Skia
tessellates curves adaptively at the current zoom while Clipper2 works on flattened geometry — routing
display through Clipper2 would make every curved trace visibly polygonal. Two outlines, two purposes.
Do not "unify" them; there is a doc comment on the renderer method saying so.

## 3. Operations

All on the current selection, all one undo entry, all through a new
**`ReplaceShapesCommand(removed: IReadOnlyList<(int Index, LayoutShape Before)>, added: IReadOnlyList<LayoutShape>)`**.
Inserting at the **lowest removed index** keeps z-order predictable, and undo restores every original at its
original index (L1b's rule, extended to the N→M case).

| Operation | Semantics |
|---|---|
| **Union (OR)** | All selected shapes merged. |
| **Intersect (AND)** | Common region of all selected. |
| **Difference (NOT)** | First-selected minus the rest. Selection order matters — surface it in the status bar. |
| **XOR** | Symmetric difference. |
| **Size / Offset** | Signed `InflatePaths` on each selected shape independently. Dimension field, unit-suffixed. Negative offsets can annihilate a shape — that is legal; delete it and report. |
| **Merge** | Union restricted to shapes sharing a layer, applied per layer. Convenient for a multi-layer selection. |

**Result attributes:**
- **Layer** — the primary operand's layer (first-selected; for NOT, the shape being subtracted from). Operands
  on different layers are allowed; the result lands on the primary's layer and the operation reports it.
- **Net (§3.4 R10a)** — propagate when every operand shares a net; **clear it and report** when they differ.
  Do not pick one arbitrarily.
- Curved operands are flattened (§3.2 R9e) — **warn once per session**, not once per operation.

**Empty results** are legal: an intersection with no overlap removes the operands and reports. It must not
throw and must not silently leave the originals in place.

## 4. Self-intersection repair

L1d flags self-intersection on release without repairing. Add **"Repair Self-Intersection"** to the shape
context menu, enabled only when the shape is actually flagged: a Clipper2 `Union` of the single shape against
nothing, with `NonZero`, which resolves crossings into a clean simple result (possibly several pieces, or one
with holes — §0 covers both).

## 5. Flatten to Polygon (§3.2 R9d)

- **Context menu → "Flatten to Polygon"** on any curved primitive (`Curve`, `Circle`, `RoundedRect`, or an
  arc/cubic-bearing `Path`). Replaces it in place with the polygon export would have produced, as **one**
  undoable action, via `ReplaceShapeCommand`.
- **"Flatten to Polygon…"** (ellipsis) opens a small tolerance prompt showing the **resulting vertex count
  live** as the tolerance changes.
- Applies to a multi-selection, **silently skipping** shapes with nothing to flatten.
- **"Flatten All Curves"** on a layer, and on the whole layout, for pre-export cleanup.
- A `PathShape` with curved edges flattens its **centerline edges to line edges** — it stays a `PathShape`
  with its width and end style intact. It does **not** become a polygon outline; that conversion is a
  different (and lossy) operation, and users flattening a trace expect to keep editing its width.
- After flattening there is no un-flatten beyond undo. Say so in the tooltip.

---

## Scope guardrails (do NOT do in L1e)

- **No clipboard** — no cut, copy or paste (L1f).
- No DRC execution (L5b), no meshing (L6), no GDSII/DXF/Gerber (L4) — only the forward comment in §0.
- No new drawing tools, no changes to L1d's handles beyond what holes require.
- No spatial index, no caching, no LOD (L2). No instances (L3).
- Do not route rendering through Clipper2 (R-L1e-1).
- Don't touch `src/Core`, `src/Engine`, `RfCore`, or the symbol editor.

## Gate (acceptance)

1. Builds green (`TreatWarningsAsErrors=true`); `dotnet test` green; no existing test regresses.
2. **Holes round-trip** — a polygon with a hole serializes, reloads and compares equal; an existing
   hole-free `.clay` still loads with **no** `FormatVersion` change.
3. **Holes behave everywhere** — bbox is unaffected by holes; a point inside a hole is **not** a hit;
   `LayoutScaling` scales hole coordinates; a rendered donut has an actual visible hole (off-screen pixel
   test: centre pixel is background, ring pixel is layer-coloured).
4. **The canonical case**: a rectangle minus a fully-interior circle yields **one** shape with **one** hole —
   not two shapes, not a keyholed ring.
5. **Every boolean** produces the expected geometry for overlapping, disjoint and fully-contained pairs,
   including the disjoint-intersection case that yields nothing.
6. **Multiple disjoint results** — a difference that splits a shape in two yields two shapes, both at
   sensible indices, and undo restores the single original at its original index.
7. **Net propagation (§3.4 R10a)** — same-net operands keep the net; differing nets clear it and report.
8. **Offset** — positive grows, negative shrinks, and an over-shrink annihilates the shape with a report
   rather than an exception or a zero-area ghost.
9. **Determinism** — the same boolean on the same inputs, run repeatedly and after a serialize/reload cycle,
   yields byte-identical results. L1c pinned this for the flattener; it must hold end-to-end.
10. **Display outline unchanged (R-L1e-1)** — the L1-fix seam test still passes, and a curved `PathShape`
    still renders with adaptive curves at high zoom rather than visible facets.
11. **Flatten to Polygon** — a circle becomes a polygon within tolerance; the vertex-count preview matches
    what the command produces; a multi-selection skips non-curved shapes without error; a curved `Path`
    stays a `PathShape` with its width and end style intact.
12. **Repair** — a deliberately self-intersecting polygon repairs to a clean result, and the L1d flag clears.
13. **One undo entry per operation**, restoring all operands at their original indices
    (`LayoutPersistence.Serialize` equality).

## On completion

1. Add a "Phase L1e — COMPLETE" entry at the top of `src/Ui/CLAUDE.md`. Call out explicitly: **the hole
   decision and its reasoning** (including that GDSII keyholes at export, not in the database), that
   **DBU integers feed Clipper2 with no scaling** because both are `long`, **R-L1e-1's display-vs-geometry
   outline split**, the boolean layer/net attribute rules, and the test file names.
2. Report back before L1f (cross-cell cut/copy/paste: `.clay` fragment format, DBU rescale on paste, and
   layer reconciliation against the destination technology) is briefed.
