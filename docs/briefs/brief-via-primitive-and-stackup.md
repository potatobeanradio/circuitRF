# Sonnet Brief — The Via primitive: toolbar tool, stackup completion, and simulation-ready parameters

**Design:** `docs/design/layout-view.md` §3.1 (primitives), §2.4 (`.ctech` and the stackup), §10.4 (substrate
stackup editor), §10.5 (meshing), §8 (Gerber/Excellon), §9A (DRC). **Consumes** all of L0–L4.

`ViaShape` has existed in the model since L0a and has **never had a toolbar tool**. This brief surfaces it,
completes the stackup data it depends on, and adds the parameters that simulation — not just fabrication —
needs.

**Sequencing:** §4 touches Gerber export. If **L4c** has not landed, fold §4 into it rather than editing
twice; if it has, this amends it. Say which in the completion note.

Gate command is plain `dotnet test`.

---

## 1. Why the typed via exists — and why it is not just a fab convenience

For **MMIC**, a via *is* simply geometry on a via layer. That is how mask design works: GDSII has no via
primitive, and the process defines what a shape on the via layer means. `ViaShape` is not needed there.

For **PCB** it is different, and the difference is that a via is **two things at one coordinate**: a copper
pad and a drilled, plated barrel. That pairing is load-bearing for three separate consumers:

- **Fabrication** — a pad flash in Gerber *and* a drill hit in Excellon. Two loose circles have nothing
  keeping them together; move one and the via is silently broken.
- **EM (L9)** — the **pad** governs how current spreads out of the trace, the **barrel diameter** governs
  via inductance (≈ `(µ₀/2π)·h·[ln(4h/d) + 1]`), and fence pitch governs mode suppression. A bare circle can
  express one of pad or barrel, not both.
- **Thermal (beyond L9)** — vias are the dominant vertical heat path in a board, and effective conductivity
  depends on barrel cross-section and **plating thickness**.

**R-via-1. The barrel diameter is a design parameter users sweep, not a fab detail.** At high frequencies a
user will try 0.3 mm against 0.5 mm and re-simulate. Everything below follows from that: it must be
per-via, editable live, and cheap to change.

## 2. Model additions

### 2.1 A via's fill model, and where plating lives

**R-via-2. `StackupKind.Via` records a fill model — `Plated` (with a wall thickness) or `Solid` — not a bare
plating thickness.** A bare thickness field silently assumes every via is a hollow plated barrel, which is a
PCB assumption and not universal.

- **PCB** — `Plated`, wall ~25 µm. A drilled hole with copper on the walls.
- **MMIC backside vias** — in most GaAs/GaN processes the etched via is **conformally plated** with the same
  gold as the backside metal (a few µm) rather than filled, so `Plated` with a thin wall is usually right.
  Processes that genuinely fill their vias use `Solid`.

**The distinction matters unevenly, and that is precisely why it must be carried rather than assumed:**

- **For RF it is largely immaterial.** A 3–5 µm gold wall is many skin depths thick above a few GHz — skin
  depth in gold is well under a micron at 10 GHz — so a plated barrel behaves as a solid conductor and an EM
  solver can treat either as a perfect vertical connection.
- **For thermal it is first-order.** A hollow plated via has a small fraction of the metal cross-section of a
  filled one, and thermal via arrays are sized on exactly that difference.

So the field exists for the thermal work that motivated it, and L9's EM path may reasonably ignore it. Say
so in the code comment, so nobody later "simplifies" it away as unused.

**Either way it is a *process* parameter and belongs on the stackup, not on `ViaShape`.** A fab plates or
fills a whole board to one specification. That is what keeps the owner's requirement intact: nobody
configures fill or wall thickness in order to run a simulation.

Additive and nullable, so existing `.ctech` files load unchanged.

### 2.2 A via layer must say what it connects

**R-via-3. A `StackupKind.Via` entry records which two conductor layers it spans.** `DrawingLayers` already
says *which drawing layer* is the via; it does not say *what the via joins*. Unambiguous on a two-layer
board and undefined on anything thicker — and L9 cannot mesh a z-directed current path without it.

Add the span now even though nothing reads it until L6/L9; the alternative is a model change mid-solver.

### 2.3 `ViaShape` itself

`X`, `Y`, `PadSize`, `DrillSize`, `LandingLayer?` already exist and are the right set. **`DrillSize` is the
barrel diameter** — the EM and thermal parameter, and the Excellon tool selector. Add a **plated** flag only
if the starter technologies need to distinguish plated from non-plated holes; otherwise take it from the
stackup and note the decision.

## 3. Complete the starter technology stackups

**R-via-4. Neither starter technology's stackup defines a via layer**, although `StackupKind.Via` with
`DrawingLayers` has been in the model since L0a. PCB is copper / FR-4 / copper; MMIC is gold / GaAs /
backside ground. Both have via *drawing* layers with no physical entry behind them.

Add them:

- **PCB 2-Layer** — a via entry spanning Top Copper → Bottom Copper, bound to the `Drill` drawing layer,
  `Plated` with a ~25 µm wall, traversing the FR-4.
- **MMIC GaAs** — **two** via entries, because the starter technology already carries the drawing layers for
  a two-metal process (`Metal1`, `Metal2`, `Via`, `Backside Via`) while its stackup defines only one
  conductor:
  - a **backside via** spanning Metal1 → backside ground, bound to `Backside Via`, `Plated` with a few-µm
    gold wall, traversing the 100 µm GaAs;
  - a **Metal1 → Metal2 post**, bound to `Via` — see §3.1.

This is a modest amount of data, and it is what makes "draw a via and simulate it" require **zero**
technology editing when L9 arrives. §10.4's preset-then-tweak stackups are already the answer to "don't make
me configure things"; the presets simply have to be complete enough to deliver on it.

### 3.1 Airbridges need no new concept — they need the stackup to be complete

An MMIC **airbridge** — a metal strap suspended over another conductor to cross it, or to tie the two ground
sides of a CPW together — is not a via and should not be modelled as one. It is:

- a **horizontal conductor on an elevated metal level** (`Metal2`), with
- **air beneath it** (a dielectric layer of εr = 1), landing on
- **posts at each end**, which *are* vias between `Metal1` and `Metal2`.

Every one of those is already expressible. The stackup holds ordered dielectric and conductor layers with
arbitrary εr, and `StackupKind.Via` connects two conductors. So the MMIC starter stackup should read, top to
bottom: **Metal2 / air (εr = 1) / Metal1 / GaAs / backside ground**, with a Metal1→Metal2 via entry for the
posts.

With that in place an airbridge is drawn exactly as an RF designer would expect — a shape on `Metal2`, posts
on `Via` — and a layered-medium solver sees it correctly, because "a conductor at an elevated z over air" is
precisely what a layered Green's function is built to handle. **The air-gap height is a stackup parameter**,
not a per-shape one, consistent with R-via-2's placement of fill and wall thickness.

Nothing here simulates an airbridge. The point is that the data model already accommodates one, and the
starter technology should not be the thing standing in the way.

**When could one actually be simulated? Not before L9 — and it is one of the harder cases.** §10.3's staged
kernel makes this unambiguous:

- **L7** (quasi-static, uniform cross-sections) — an airbridge is a discontinuity by definition. Structurally
  impossible.
- **L8** (full-wave, single dielectric + ground plane) — one signal layer, no vias. Also impossible.
- **L9** (general layered stack, N dielectrics, z-directed current) — **yes.** Multiple horizontal conductor
  levels joined by vertical posts is precisely what "2.5D" names, and it is a standard capability of this
  solver class.

Encouragingly, an elevated conductor needs no special layer treatment: **the air above the substrate is
already the top half-space** of the stratification, so an airbridge is simply a conductor at a non-zero
height within it. The layered Green's function handles a source at arbitrary height by construction.

**The genuinely hard part is meshing, not the Green's function.** An airbridge clears the line beneath it by
only a few microns. The near-field interaction across that gap is strong, and the mesh must resolve the
vertical separation where the bridge overlaps — which means very fine cells exactly where the coupling
matters. Against §10.7's ~5,000-unknown ceiling for a "lightweight" solver, a single airbridge crossing can
consume a large share of the budget. Worth knowing before L9 scopes itself.

**None of that argues for deferring the stackup work.** The reason to do it now is L6: the mesher should be
written against a realistic two-metal-level stackup rather than a single-conductor one, or L9 becomes a
retrofit instead of an extension.

## 4. The Via tool, and geometry drawn on a drill layer

### 4.1 Toolbar

Add a **Via** tool alongside the L1b drawing tools. Single click places a `ViaShape` at the snapped point.

- Pad and drill default from the technology; both are editable in the Properties Inspector (L1j).
- **Render as an annulus** — pad filled in the layer colour with the barrel visible — so pad and drill are
  distinguishable at a glance. A solid disc hides exactly the relationship that matters.
- **Tool prominence follows the technology**: needed where the stackup has a via layer with a drill
  function; on a technology without one it is redundant, since a via there is ordinary geometry (§1).
  Disable with a reason rather than hiding, per R13a.

### 4.2 A circle on a drill layer must not fail silently

Drawing a `Circle` on the Drill layer is the intuitive thing to try, and it is how MMIC genuinely works.

**R-via-5. At Gerber export, non-`Via` geometry on a drill-function layer still emits drill hits, and is
reported.** Something like *"4 circles on Drill will produce unpaired holes — convert to Vias for annular-ring
checking?"*

Refusing to emit would let a design that looks correct ship a board with no holes. Emitting silently would
bake in the pad/drill drift hazard permanently. Reporting is what R13a asks everywhere else: act, or
explain — never quietly do nothing.

**R-via-6. Add a `Convert to Via` command**, so the intuitive path is recoverable. Given a circle on a
drill-function layer, produce a `ViaShape` using the circle's diameter as the barrel and the technology's
default pad — one undoable action, via `ReplaceShapesCommand`. If a concentric pad circle is selected too,
use its diameter as the pad.

### 4.3 GDSII and DXF export — compatible, and lossy in a documented way

Neither format has a via record. **Both are fully compatible with a `ViaShape`; it simply becomes ordinary
geometry.**

| | GDSII | DXF |
|---|---|---|
| Pad | `BOUNDARY` polygon (circle flattened per §3.2 R9e) | **`CIRCLE`** — exact, no flattening |
| Barrel | `BOUNDARY` polygon on the via/drill layer | **`CIRCLE`** on the drill layer |
| Pairing | **lost** | **lost** |
| Drill/tooling data | **lost** | **lost** |

DXF comes off better: L4b's mapping keeps circles as `CIRCLE` entities, so a via exports exactly rather than
flattened — and a circle on a drill layer is the conventional mechanical-drawing representation anyway.
GDSII flattens, which is fine because an MMIC via *is* a polygon on the via layer as far as a mask shop is
concerned.

**R-via-9. A `ViaShape` emits one shape per mapped layer it participates in**, taking the layer assignment
from the `.ctech` interchange mapping (R-L4a-1). Where a part's layer has no mapping for the target format,
that part is skipped and **reported** — never silently dropped.

**This needs pinning before anything is written:** `ViaShape` carries both an inherited `Layer` and a
`LandingLayer?`, and the brief has not said which is the barrel and which is the pad. Define it explicitly
in the model's doc comment — suggest **`Layer` = the via/drill layer (the barrel), `LandingLayer` = the pad's
copper layer** — and make the interchange mapping able to express both. Getting this backwards produces an
export that looks plausible and puts copper where the hole should be.

**The loss is real and belongs in the export report**, alongside curve flattening and hole keyholing:
re-importing either format yields two unrelated shapes, not a via. R-via-6's `Convert to Via` is the recovery
path, and could reasonably be extended later to recognise a concentric pad-and-barrel pair.

**R-via-10. Neither GDSII nor DXF is a manufacturable PCB deliverable, and the export dialog should not
imply otherwise.** Neither carries a drill table, so a board house receiving one has no hole data at all.
**Gerber + Excellon is the PCB deliverable**; GDSII is the MMIC one. A one-line note in the export dialog
when a design contains vias — *"Vias export as geometry only; use Gerber + Excellon for fabrication"* — costs
nothing and prevents a genuinely expensive mistake.

**Do not preserve the pairing via `SREF`/`BLOCK`.** It is technically possible — emit a structure per via
type and reference it — and it would survive a round-trip as hierarchy. But it pollutes the cell namespace
with synthetic structures, and mask shops and DRC decks expect via geometry as flat polygons on the via
layer. Conventional output beats round-trip fidelity here.

## 5. Forward hooks — design for these, do not build them

**R-via-7. Changing a via's drill or pad invalidates the mesh, never the technology.** Barrel diameter is
the swept parameter (R-via-1), so a change must be one Properties edit and one re-run — not a technology
edit, and not a stackup dialog. Nothing here builds the mesher; the point is that L6 must key its
invalidation off shape edits.

**R-via-8. Via arrays must be cheap to re-pitch.** Fences and thermal via arrays are regular grids whose
spacing is itself a design variable. L3a's array instances already cover the placement; this strengthens the
case for L3c's deferred **"Create Array from selection"**, which remains unbuilt — note it, do not build it.

**DRC (L5b):** annular ring = `(PadSize − DrillSize) / 2` is the natural third rule after min-width and
min-spacing, and it is expressible **only** because pad and drill are one object. Leave a comment where
L5b's rule kinds are defined; add no rule now.

## 6. Guardrails

- No EM, no mesher, no thermal — §5 is comments and data-model shape only.
- No DRC rule implementation.
- Do not build "Create Array from selection."
- Do not remove `Circle` as a way to draw on a via layer; MMIC depends on it (§1).
- No `.clay` format change — `ViaShape` already persists. `.ctech` gains fields additively, no version bump.
- Don't touch `src/Core`, `src/Engine`, `RfCore`.

## 7. Gate

1. Builds green; `dotnet test` green; no existing test regresses.
2. **Stackup fields (R-via-2, R-via-3)** — fill model, wall thickness and span round-trip through `.ctech`;
   an existing `.ctech` without them still loads with **no `FormatVersion` change**; `TechValidation` flags a
   via entry whose span names a non-existent conductor layer, and a `Plated` entry with no wall thickness.
3. **Starter stackups (R-via-4, §3.1)** — PCB exposes one via entry; **MMIC exposes two** (backside via and
   a Metal1→Metal2 post) over a **Metal2 / air / Metal1 / GaAs / ground** stack, and both technologies pass
   `TechValidation`. Assert the air layer has εr = 1.
4. **Via tool** — places a `ViaShape` at the snapped point with technology defaults; one undo entry;
   pad and drill are editable in Properties and update the rendering live (L1j's R-L1j-1 path).
5. **Annulus rendering** — a pixel test shows the barrel distinguishable from the pad; both scale correctly
   with zoom.
6. **Tool enablement** — enabled on a technology whose stackup has a via layer, disabled **with a reason**
   on one that has none.
7. **Gerber (R-via-5)** — a `ViaShape` emits a pad flash **and** a drill hit; a bare `Circle` on the drill
   layer emits a drill hit **and** is reported as unpaired; the report is absent when there are none.
8. **Convert to Via (R-via-6)** — a circle on a drill layer converts, using its diameter as the barrel and
   the technology pad; undo restores the circle at its original index.
9. **Excellon tool table** — two vias with the same drill share one tool; differing drills produce two.
10. **GDSII and DXF (§4.3)** — a via exports as pad **and** barrel on their mapped layers: flattened
    `BOUNDARY` polygons in GDSII, exact `CIRCLE` entities in DXF. An unmapped layer is skipped **and
    reported**. Assert the barrel lands on the via/drill layer and the pad on the copper layer, not the
    reverse (R-via-9).
11. **Fabrication warning (R-via-10)** — exporting a design containing vias to GDSII or DXF notes that
    vias carry geometry only; a design with no vias shows no such note.

## 8. On completion

Record in `src/Ui/CLAUDE.md`: **why the typed via exists for PCB and not for MMIC** (§1), and that the
pad/drill pairing serves **EM and thermal as well as fabrication** — the framing correction that motivated
this brief; **R-via-2's** fill model (`Plated` vs `Solid`) rather than a bare plating thickness, its placement
on the stackup rather than the via, and that it is carried **for thermal** even though RF can ignore it above
a few GHz; **§3.1** — that an **airbridge needs no new primitive**, only a complete stackup (Metal2 / air /
Metal1), which is why the MMIC starter gains a second metal level and two via entries; **R-via-3's** span
field and that nothing reads it until L6/L9; and **R-via-5's** emit-and-report decision for bare circles, so
nobody later "fixes" it into a silent refusal.

Note whether §4 landed here or was folded into L4c.
