# Sonnet Brief — Snap distance control, and AutoCAD-style geometry snap

Two related features. **§1 is small and independent; §2 is a large feature** whose performance discipline
matters as much as its behaviour.

**Design:** `docs/design/layout-view.md` §1.5 (snap governs future edits only), §6.2 (selection and overlap
cycling), §5.2 (spatial index); `docs/design/pcell-contract.md` R3 (pins). **Consumes L0–L5.**

Gate command is plain `dotnet test`.

---

## 1. A control for the snap distance

`LayoutView.SnapDbu` exists and is seeded from the technology (L0c); nothing in the UI changes it.

**R-snp-1. Add a snap-distance control to the layout metadata bar, beside the display-unit combo.** It edits
**this document's** `SnapDbu`. It does not touch the technology's default — that is the Technology Editor's
job, and L0c's no-re-seed invariant stands.

**R-snp-2. The offered values are technology-relative and unit-suffixed — not a fixed 1 / 5 / 10 list.**

A bare `1 · 5 · 10` is the label-height defect again: one *what*? Mils on a PCB, microns on an MMIC. Derive
the list from the resolved technology's `DefaultSnapDbu` and render each entry through `LayoutUnits.Format`
in the document's display unit, so a PCB reads `1 mil · 5 mil · 10 mil · 25 mil · 50 mil` and an MMIC process
reads its own sensible ladder.

**R-snp-3. Accept a typed value as well as the list.** Any fixed ladder is wrong for somebody. Parse through
`LayoutUnits.TryParse` so `2.5mil` or `0.1u` work, with the same validation treatment every other dimension
field gets.

**Binding: `F9`**, matching AutoCAD's grid snap. (AutoCAD's F8 is ortho; F3 is object snap, which §2 uses.)

---

## 2. Geometry snap

As the cursor approaches a geometry feature, a marker appears. Clicking grabs **that feature point** — even if
the click coordinates miss the shape's own hit-test — and dragging moves the geometry relative to it, snapping
to other features within tolerance. It **overrides grid snap** while active.

### 2.1 Feature types, glyphs, and priority

| Feature | Glyph | Notes |
|---|---|---|
| **Corner / endpoint** | square | vertices of any shape; a `Path`'s ends |
| **Midpoint** | triangle | midpoint of an edge |
| **Centroid** | circle | shape centre |
| **PCell pin** | diamond | position, layer and outward direction all come free from contract R3 |
| **Intersection** | **X** | off by default (R-snp-6) |
| **Nearest point on edge** | **bowtie** | lowest priority — R-snp-5 |

**Pins are included deliberately, and I would rank them above centroid in value** — snapping an MLIN pin to an
MTee pin *is* the microstrip workflow the whole component arc exists for, and R3 already supplies everything
needed. Strike them if unwanted, but they are close to free.

**R-snp-4. Glyphs are screen-space sized and coloured by the *source* layer.** Fixed pixel size so they read
at any zoom; the layer colour so that cross-layer snapping — which is explicitly wanted — tells the user *which*
layer they are about to snap to. Without the colour, cross-layer snapping is a coin flip.

**R-snp-5. Nearest-point-on-edge is strictly lowest priority.** It is available *everywhere* along *every*
edge, so without an explicit order the discrete markers would almost never appear — the cursor is always near
some edge before it is near a corner. Priority, highest first:

> **pin → corner/endpoint → intersection → midpoint → centroid → nearest**

The rule behind the order: the more *intentional* a feature is, the higher it ranks.

### 2.2 Toggles — two, not one per type

**R-snp-6. Exactly two toggles: geometry snap on/off, and include-intersections on/off (default off).** Per-type
toggles are deliberately out of scope — AutoCAD has them and they are a settings thicket nobody visits.

- **Geometry snap:** `F3` (AutoCAD's object snap), plus `s` — **verify `s` is not already a tool shortcut
  before binding it**; single letters usually are, and a mode toggle sharing one is worse than no shortcut.
- Both toggles need a visible state indicator, since a mode the user cannot see is a mode they will fight.

**R-snp-7. Both toggles are live during a drag.** Pressing `F3` mid-drag changes behaviour immediately — and
that means **recomputing on the toggle itself**, not waiting for the next pointer move. A mode change that
appears to do nothing until you jiggle the mouse reads as broken.

### 2.3 Clicking, and the two roles it serves

**R-snp-8. When a marker is showing, the click is consumed by the marker — not by hit-testing.** This is the
core UX claim: the click registers on the feature's shape even if the cursor is outside it. Missing the
geometry by two pixels must not drop the gesture.

**R-snp-9. Coincident features cycle, reusing the existing overlap-cycling mechanism.** When two shapes share a
feature location, successive clicks step through them with the same `n of m` readout selection already uses
(§6.2). Do not build a second cycling mechanism.

**Two distinct roles, both wanted:**

- **Grab point** — "pick up this object *by* this corner." The dragged geometry moves so that point tracks.
- **Target** — "put it *on* that corner." During the drag, other features attract the grab point.

Worth noting these differ from AutoCAD: its object snap is essentially the *target* role, while the grab role
is closer to grip-dragging but from an arbitrary snap point rather than a predefined grip. It is a deliberate
extension, so do not expect AutoCAD's behaviour to answer questions about it.

### 2.4 Precedence against L1d's editing handles

**R-snp-10. Handles win within their hit radius on the selected shape. Geometry snap wins everywhere else.**

No modifier is needed, because the two barely overlap: L1d's vertex/edge/bulge handles appear **only on a
selected shape**, while snap markers are wanted on **unselected** geometry. That is exactly how AutoCAD avoids
the same collision — grips exist only on selected objects; object-snap markers appear while picking a point.

During a drag, geometry snap governs the **destination**, which is the useful half regardless.

**R-snp-11. The modifier *suppresses* snap; it does not enable it.** Once the mode is on the user wants it
acting — the rare need is placing something freely *near* a feature. Charging a keypress for the common case is
backwards.

### 2.5 Candidate scope

- **Visible layers only.** Hidden layers contribute nothing and cost nothing (the L2b query already returns
  candidates that consumers filter).
- **Locked layers ARE snappable.** Locking prevents editing, not referencing.
- **Cross-layer snapping is allowed and wanted** — placing geometry relative to another layer's is a real
  workflow, which is what R-snp-4's colouring exists to make legible.
- Intersections **across layers** are geometrically well-defined as projected crossings and are allowed for
  positioning purposes. Note in the completion that they are projections, not physical intersections.

### 2.6 Performance — the part that decides whether this ships

This runs at **pointer-move rate**, which is the same trap L1i's marquee fell into. Treat it accordingly.

**R-snp-12. Split intrinsic from relational features. Cache the first; compute the second live.**

- **Intrinsic** — corners, midpoints, centroids, pins. Each is a property of *one* shape, so they belong in a
  **per-cell feature index in cell-local coordinates**, cached and keyed exactly as R-L3a-3's geometry cache
  is, and invalidated by the same seam.
- **Intersection is relational** — a property of a *pair*, possibly spanning cells, instances and layers. It
  **cannot** live in that index. Compute it live from the near-cursor candidate set only. A global
  intersection index would be enormous and would invalidate on every edit; this is also why off-by-default
  costs nothing when unused.

**R-snp-13. Transform the query, not the data.** For an instance, inverse-transform the **cursor** into cell
space (one matrix per placement) and look up in that cell's own index — never push a cell's features into world
space. A 50×50 array then costs one index and 2,500 inverse transforms, and the R-tree culls almost all of
those before they happen.

**R-snp-14. Bound candidates with the L2b spatial index.** Work must be O(near the cursor), not O(design).
This is the marquee lesson restated: an O(N) scan per pointer move is unusable well before the pathological
case.

**R-snp-15. Tolerance is in screen pixels, converted per query as `pixels / zoom`.** Never cached, never
`SnapDbu` — the standing rule for every tolerance in this editor.

**R-snp-16. Skip the query entirely when the cursor has not moved a device pixel.** Pointer moves arrive far
faster than the answer can change.

**Counters, not wall-clock, are the gate** — features examined, candidates returned, intersection pairs tested
— following L2a's R-L2a-3.

## 3. Guardrails

- Do not add per-feature-type toggles (R-snp-6).
- Do not build a second cycling mechanism (R-snp-9) or a second tolerance convention (R-snp-15).
- Do not change L1d's handle behaviour; §2.4 is a precedence rule, not a modification.
- Do not change the technology's `DefaultSnapDbu` from §1's control (R-snp-1).
- Do not index intersections (R-snp-12).
- Don't touch `src/Core`, `src/Engine`, `RfCore`.

## 4. Gate

1. Builds green; `dotnet test` green; no existing test regresses.
2. **Snap control (R-snp-1/2/3)** — changing it alters this document's `SnapDbu` and not the technology's; the
   ladder reads in mils on the PCB starter tech and in the MMIC tech's own unit; a typed `2.5mil` is accepted;
   `F9` is bound.
3. **Markers appear** — approaching a corner, midpoint, centroid, pin, and (with intersections enabled) a
   crossing each shows the correct glyph, screen-space sized at several zooms.
4. **Layer colouring (R-snp-4)** — a marker on a bottom-copper feature is drawn in that layer's colour.
5. **Priority (R-snp-5)** — with the cursor near both an edge and a corner, the **corner** marker shows. With
   a pin and a corner coincident, the **pin** wins. Nearest appears only when nothing discrete is in range.
6. **Toggles (R-snp-6/7)** — `F3` toggles the mode and intersections toggle independently; intersections are
   **off** on a fresh document; toggling **mid-drag** changes the live preview **without** requiring a pointer
   move.
7. **Click through the marker (R-snp-8)** — clicking with a marker showing selects and begins dragging the
   marker's shape **even when the click point is outside that shape**. This is the headline behaviour.
8. **Cycling (R-snp-9)** — two shapes sharing a feature location cycle on successive clicks, with the
   existing `n of m` readout.
9. **Both roles** — dragging by a corner keeps that corner under the cursor (grab); releasing near another
   feature lands exactly on it (target).
10. **Handle precedence (R-snp-10/11)** — on a selected shape, a click within a vertex handle's radius edits
    the vertex; just outside it, geometry snap engages. The modifier **suppresses** snap.
11. **Candidate scope (§2.5)** — hidden layers never produce markers; **locked layers do**; cross-layer
    features do.
12. **Counters bound the work (R-snp-13/14/16)** — at 500k shapes with a deep hierarchy, features examined per
    pointer move is **O(near cursor)**, not O(design); a 50×50 array builds **one** feature index; no query
    runs for a sub-pixel cursor move.
13. **Intersections stay unindexed (R-snp-12)** — enabling intersections changes no cached index; disabling
    them removes the pairwise cost entirely (assert the pair-test counter is zero).

## 5. On completion

Record in `src/Ui/CLAUDE.md`: **R-snp-12's intrinsic/relational split** and why intersection cannot be
indexed; **R-snp-13** (transform the cursor, not the geometry) with the array numbers; **R-snp-5's priority
order** and why nearest must be last; **R-snp-10's** phase-based precedence over handles and that it needs no
modifier; **R-snp-8** (the click is consumed by the marker, not by hit-testing) as the feature's central UX
claim; and the measured per-pointer-move cost at the largest test design.
