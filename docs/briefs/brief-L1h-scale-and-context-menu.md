# Sonnet Brief — Phase L1h: Scale, and fixing the shape context menu

**Design:** `docs/design/layout-view.md` §6.1 (edit operations), §3.2 R9d (Flatten to Polygon), §1.1 (integer
DBU), §1.5 R5 (snap governs future edits only), §3.2 R9a (the edge-list vocabulary). **Consumes** L1a–L1g.
**Runs before L2.**

Two owner-reported items. The second is not only a labelling problem — two of the commands genuinely
overlap, and the fix is to remove the overlap rather than explain it.

---

## 1. The context menu is confusing, and partly redundant

### 1.1 What the three commands actually do today

| Command | Behaviour | On a `Circle` | On a `Rect` |
|---|---|---|---|
| **Merge** | Union, grouped per layer (L1e §3) | union of one shape = itself → **nothing** | union of one shape = itself → **nothing** |
| **Flatten to Polygon** | Curved primitive → `PolygonShape` at the resolved tolerance | works | **nothing** (already straight-edged) |
| **Flatten to Polygon…** | Same, after a tolerance prompt with a live vertex count | works | **nothing** |

So on a single `Rect`, all three are no-ops, and on a single `Circle`, one is. The owner's report is exactly
right, and greying them out is necessary — but not sufficient, because **Merge and Union are the same
command wearing two names.**

### 1.2 Remove Merge; make Union layer-aware

L1e defined **Union (OR)** as "all selected shapes merged", landing on the primary operand's layer, and
**Merge** as "union restricted to shapes sharing a layer, applied per layer". For a single-layer selection
these are identical, which is the overwhelmingly common case. Two commands differing only in a subtlety
nobody reads a tooltip for is worse than one command that does the obviously right thing.

**R-L1h-1. There is one union command, named Union, and it groups by layer.** Shapes on different layers are
unioned within their own layers and stay there. `Merge` is deleted.

This also fixes a latent surprise in the old Union: unioning a top-copper shape with a silkscreen shape and
getting a single top-copper shape is not what anyone wants. Cross-layer combination, when genuinely
intended, is **Move to Layer** followed by **Union** — two explicit steps rather than one silent one.

### 1.3 Collapse the two Flatten entries into one — but fix a prerequisite first

#### 1.3.0 Prerequisite: `Circle` and `RoundedRect` have no tolerance field at all

The owner reported not being able to find the flatten tolerance in the properties panel. That report is
correct, and it exposes a real gap rather than a discoverability problem.

§3.2 **R9b** says *"Every curved primitive carries a flatten tolerance."* The implementation carries it on
only two of the four:

| Shape | `FlattenTolDbu` in `LayoutModel.cs` | Shown in the properties panel |
|---|---|---|
| `CurveShape` | yes | yes |
| `PathShape` | yes | yes |
| **`CircleShape`** | **no** | **no** |
| **`RoundedRectShape`** | **no** | **no** |

`LayoutShapePropertiesViewModel` gates the field on `_selected.All(s => s is CurveShape or PathShape)`, so
selecting a circle — the exact case in the report — shows nothing, because there is nothing to show. This
traces back to L0a, whose primitive table listed `FlattenTolDbu` only on the two edge-list types; R9b was
already the stated rule and the table simply did not match it.

**Fix, before anything else in §1.3:**

1. Add `public long? FlattenTolDbu { get; set; }` to `CircleShape` and `RoundedRectShape`, with the same
   `null` = inherit-from-technology semantics and the same doc comment. **Additive and nullable, so no
   `FormatVersion` bump** — the same pattern as `Holes`.
2. Widen the panel predicate to
   `_selected.All(s => s is CurveShape or PathShape or CircleShape or RoundedRectShape)`, and extend the
   getter/setter switch in `CommitFlattenTolText` to cover all four.
3. Include both new fields in R-L1h-6's shared coordinate walk — a tolerance is a length and must scale.

#### 1.3.1 Then collapse the menu entries — the prompt is the survivor

The `Command` / `Command…` convention (defaults vs. options) is real but not universally read, and two
adjacent near-identical entries is noise. **Decided by the owner: keep the prompt, drop the silent one.**

**R-L1h-2. There is exactly one entry, "Flatten to Polygon…", and it always prompts.** Flattening is
irreversible except by undo and its resolution is the whole point of the operation, so the user should see
and confirm the tolerance they are getting rather than infer it. The no-dialog variant is removed.

The dialog:

- **Pre-fills from the shape's resolved `FlattenTolDbu`** — the shape's own value if set, otherwise the
  technology default, labelled so the user can see which it is (*"1 µm (from technology)"*).
- Shows the **live resulting vertex count** as the tolerance is typed — per shape for a single selection,
  and a total plus per-shape breakdown for a multi-selection.
- Names what will be **skipped**: *"3 of 5 selected shapes will be flattened; 2 have no curvature."*
- Tolerance parses through `LayoutUnits.TryParse`, so `1u`, `0.5 mil`, `250nm` all work.

**R-L1h-2a. The dialog does NOT write its value back to the shapes it flattens.** Worth stating explicitly,
because the opposite seems reasonable at first: `FlattenTolDbu` is also what **GDSII export** reads (§3.2
R9e), so keeping the two in sync sounds important. It isn't, because *every shape the dialog touches stops
being curved* — a flattened circle is a `PolygonShape` with no tolerance and nothing left to flatten. Writing
the value onto a shape that is about to be replaced accomplishes nothing.

The sync that actually matters runs the other way, and pre-fill already provides it:

**R-L1h-2b. The dialog pre-fills from the shape's own `FlattenTolDbu` if set, otherwise the technology
default, and labels which one it used.** So a tolerance set in the properties panel is the tolerance the
dialog offers, and the tolerance export would have used is the tolerance the user is shown. The two surfaces
agree by reading the same source rather than by writing to each other.

The properties-panel field from §1.3.0 remains the home of the persistent value — it governs GDSII export for
every curved shape the user never manually flattens, which is the normal case.

Optional nicety, take it or leave it: remember the last tolerance entered **for the session** and use it when
a shape has no explicit value of its own, so repeated flattens do not require re-typing. Shape value still
wins when present.

**Flatten All Curves** (layer / whole layout) uses the same dialog, prompting once and applying the entered
value to every affected shape.

Menu label: **Flatten to Polygon…**
Tooltip: *"Replace curved shapes with polygons at a tolerance you choose. Undo to revert."*

### 1.4 The general rule the owner is reaching for

**R-L1h-3. A command is either disabled with a stated reason, or it does something. Never a silent no-op.**

Two halves, and both matter:

- **Disable, don't hide.** Context-menu items keep their positions so muscle memory works; a missing item
  reads as a bug, a greyed one reads as a state.
- **Say why.** A disabled item with no explanation is only marginally better than a no-op. Every disabled
  command carries a tooltip naming the condition — *"Select 2 or more shapes on the same layer"*, *"No
  curved shapes in selection"*.

And the companion case: a command that is legitimately enabled but turns out to change nothing — Union of two
shapes that do not touch, Repair on a shape that is already clean — **reports through Messages** rather than
appearing to fail. Enabled-and-silent is the failure mode being fixed here; do not reintroduce it one layer
up.

### 1.5 Enablement table — implement exactly this

Drive it from one place (a single `CanExecute` evaluator over the current selection) so the menu, the
toolbar and any keyboard binding always agree.

| Command | Enabled when | Disabled tooltip |
|---|---|---|
| Cut / Copy / Delete / Duplicate | ≥ 1 shape selected | "Select at least one shape" |
| Paste / Paste in Place | clipboard holds a valid layout fragment | "Clipboard has no layout geometry" |
| **Union** | ≥ 2 selected shapes share a layer | "Select 2 or more shapes on the same layer" |
| Intersect / XOR | ≥ 2 selected shapes share a layer | as above |
| Difference | ≥ 2 selected shapes share a layer | as above (first-selected is the primary — state that in the *enabled* tooltip) |
| Size / Offset | ≥ 1 | "Select at least one shape" |
| **Scale** | ≥ 1 | "Select at least one shape" |
| **Flatten to Polygon** | ≥ 1 selected shape has curvature — `Circle`, `RoundedRect`, or a `Curve`/`Path` with a non-`Line` edge | "No curved shapes in selection" |
| Repair Self-Intersection | ≥ 1 selected shape is flagged self-intersecting | "No self-intersecting shapes in selection" |
| Move to Layer / Set Net | ≥ 1 | "Select at least one shape" |
| Align | ≥ 2 | "Select 2 or more shapes" |
| Distribute | ≥ 3 | "Select 3 or more shapes" |
| Convert Edge to Line / Arc / Cubic | the context menu was opened **on an edge** | n/a — edge menu only |
| Insert / Remove Vertex | opened on an edge / on a vertex, and removal leaves ≥ 3 (closed) or ≥ 2 (`Path`) | "A closed shape needs at least 3 vertices" |

**Audit every existing context-menu command against this table**, not just the three named in the report.

---

## 2. Scale

### 2.1 One operation, two ways to drive it

Both are wanted; they share a single `ScaleShapesCommand` and one set of semantics.

**Numeric — context menu → "Scale…"**

- **Factor** and **Target size** in one dialog, linked live: typing `1.5` updates the target dimensions,
  typing `2.9mm` into the width updates the factor. Half the time the user's real intent is *"make this
  2.9 mm wide"*, and forcing them to compute a ratio for that is the kind of friction §10.10 budgets
  against. Dimension fields parse through `LayoutUnits.TryParse`.
- **Uniform** by default, with an unlock for separate X/Y.
- **Anchor**: selection bbox centre (default), any of the 8 bbox points, or the layout origin.
- Live preview of the resulting bbox in display units.

**Mouse — bbox scale handles**

**R-L1h-4. Corner handles scale uniformly; side handles stretch one axis.** No modifier decides which. This
sidesteps the Shift-constrains convention fight entirely, makes non-uniform scaling a deliberate act rather
than an accidental one, and is self-describing: a corner moves in two dimensions, a side moves in one.

- Anchor is the **opposite** corner or side; **Alt** anchors the bbox centre instead.
- **Live readout** during the drag: factor plus resulting size, in display units.
- **Typed override mid-drag**, exactly as L1b's rect W/H commit works — type a factor or a size and it
  commits at that exact value.
- Escape cancels with nothing pushed.

**R-L1h-5. Handle conflict resolution.** L1d gives a *single* selected shape vertex/edge/bulge handles, and
gives a multi-selection none.

| Selection | Handles shown |
|---|---|
| One shape | vertex / edge / bulge / control-point handles (L1d) — **unchanged** |
| Two or more shapes | **bbox scale handles** — new, and this is where multi-selection previously had nothing |
| One shape, Scale mode active | bbox scale handles temporarily replace the L1d handles; Escape returns |

Scale mode is entered from the context menu or a toolbar toggle. This adds capability to multi-selection
without disturbing any existing single-shape gesture.

### 2.2 Semantics — what scales, and the two traps

**R-L1h-6. Reuse one coordinate walk.** `LayoutScaling.TryChangeResolution` already traverses every
coordinate in a layout; paste rescaling (L1f) traverses the same set; Scale is a third. **Extract the
traversal once** and have all three call it. The failure mode this prevents is real and has already been
flagged twice in this project: one of the three forgets hole rings, or cubic control points, or
`FlattenTolDbu`, and the omission only shows up on an unusual shape months later.

The full set: outer vertices · **hole rings** · **cubic control points** · circle radius · rounded-rect
corner radius · path width · via pad and drill · label position and height · `FlattenTolDbu`.

Arc **bulge** values are dimensionless ratios and must **not** be scaled under a uniform scale — scaling
them silently changes the arc's curvature relative to its chord. Assert this.

**Trap 1 — non-uniform scale and arcs.** A circular arc scaled non-uniformly is an **ellipse**, which the
edge-list model cannot represent: it has `Line`, `Arc` (circular) and `Cubic` only.

**R-L1h-7. Under a non-uniform scale, arc edges are converted to cubic edges first, then transformed.**
Cubic Béziers are closed under affine transformation and circular arcs are not, so this is exact rather than
approximate. A `Circle` under non-uniform scale becomes a `Curve` with cubic edges — the same shape of
promotion as L1d's R-L1d-3, so follow that precedent. Report the conversion once per operation through
Messages. Under a **uniform** scale, arcs stay arcs and a `Circle` stays a `Circle`.

**Trap 2 — rounding.** Scaling by an arbitrary factor produces non-integer DBU. Round to the nearest DBU;
**do not snap to the snap grid.** Off-grid vertices are legitimate (§1.5 R5), and snapping a scaled result
would deform it — the same mistake R-L1c-3 avoids for move. Note that scaling is therefore not exactly
reversible: scaling by 3 then by 1/3 may differ by a DBU. That is correct and unavoidable with integer
storage; say so in the completion note rather than trying to be clever about it.

**Guards.** A factor of 0 or negative is rejected with a message — mirroring is a separate operation and is
not in scope here. A scale that would collapse a shape below one DBU is rejected, naming the shape.

**Undo**: one `ScaleShapesCommand` per operation (numeric or one mouse drag), restoring every original at its
original index.

---

## Scope guardrails (do NOT do in L1h)

- No Mirror, no Rotate, no Array — Scale only. (Rotate raises the same arc-vs-ellipse question and deserves
  its own pass.)
- No spatial index, caching or LOD (L2). No DRC (L5b), no interchange (L4), no instances (L3).
- No changes to L1d's single-shape handles beyond R-L1h-5's mode switch.
- Do not add a snap-to-grid step to Scale.
- Don't touch `src/Core`, `src/Engine`, or `RfCore`.

## Gate (acceptance)

1. Builds green (`TreatWarningsAsErrors=true`); `dotnet test` green; no existing test regresses.
2. **`Merge` is gone**; `Union` groups by layer — a selection spanning two layers unions within each and
   produces one shape per layer, both on their original layers.
3. **Enablement (R-L1h-3)** — a headless test over the §1.5 table: for each command and a representative
   selection, `CanExecute` matches the table exactly. Specifically: `Union` disabled on a single `Rect`;
   `Flatten to Polygon` disabled on a `Rect` and enabled on a `Circle`; `Repair` disabled on a clean shape.
4. **Every disabled command has a non-empty reason string**, asserted by iterating the command set — no
   silent greying.
5. **No silent no-ops** — an enabled `Union` of two non-overlapping same-layer shapes posts a Messages note.
6. **One Flatten entry, "Flatten to Polygon…", which always prompts** — the no-dialog variant is gone. The
   dialog pre-fills from the resolved tolerance (labelled when inherited), shows a live vertex count that
   matches what the command produces, and names how many selected shapes will be skipped.
6b. **Pre-fill chain (R-L1h-2b)** — a shape with an explicit `FlattenTolDbu` opens the dialog at that value,
   labelled as the shape's own; a shape without one opens at the technology default, labelled as inherited.
   Assert the dialog does **not** modify `FlattenTolDbu` on any shape (R-L1h-2a) — including the mixed case
   where some selected shapes are skipped.
6a. **The tolerance field is reachable on all four curved types (§1.3.0)** — selecting a `Circle`, a
   `RoundedRect`, a `Curve` or a `Path` shows the field; a mixed selection of any of the four shows it and
   applies to all; selecting a `Rect` does not. A blank field shows the inherited technology value as
   placeholder text. Assert `CircleShape.FlattenTolDbu` and `RoundedRectShape.FlattenTolDbu` round-trip
   through `.clay` with **no `FormatVersion` change**, and that an existing file without them still loads.
7. **Scale, numeric** — factor and target-size fields stay consistent in both directions; a factor of 2 on a
   1 mm square yields exactly 2 mm; each of the 9 anchors positions the result correctly.
8. **Scale, mouse** — a corner drag scales uniformly and a side drag scales one axis (R-L1h-4); Alt anchors
   the centre; a typed override mid-drag commits exactly; Escape pushes nothing. Drive at least one of these
   from **screen-pixel** coordinates through the canvas conversion (the standing screen→world rule).
9. **Handle modes (R-L1h-5)** — one shape shows L1d handles; two shapes show bbox scale handles; Scale mode
   on a single shape swaps them and Escape restores.
10. **Everything scales (R-L1h-6)** — a fixture exercising every field in the list, including a polygon
    **with a hole**, a `Curve` with a **cubic** edge, a `Path` (width scales), a `Via` (pad and drill), and a
    `Label` (height). Assert **bulge values are unchanged** under uniform scale.
11. **The shared traversal is actually shared** — assert `TryChangeResolution`, paste rescale and Scale all
    route through the same walk (e.g. a fixture with holes and cubics gives consistent results through all
    three paths).
12. **Non-uniform scale converts arcs (R-L1h-7)** — a `Circle` scaled 2×1 becomes a `Curve` with cubic
    edges whose flattened outline matches the analytic ellipse within tolerance, and a Messages note is
    posted. Under uniform scale it stays a `Circle`.
13. **Rounding** — a scale by 1.37 leaves every coordinate an integer DBU and **not** snapped to the snap
    grid; a factor of 0, a negative factor, and a collapse-below-one-DBU are each rejected with a message.
14. **One undo entry** per scale, restoring originals at their original indices.

## On completion

1. Add a "Phase L1h — COMPLETE" entry at the top of `src/Ui/CLAUDE.md`. Call out explicitly: **why `Merge`
   was removed rather than fixed** and that `Union` is now per-layer, **the one-Flatten-entry decision** and
   that tolerance lives in the properties panel with a live vertex count, **R-L1h-3** as a standing rule for
   every future command, **R-L1h-4/5** (corner = uniform, side = one axis; and the three handle modes),
   **R-L1h-6's shared coordinate walk** and the exact field list it must cover, **R-L1h-7** (non-uniform
   scale promotes arcs to cubics, because cubics are closed under affine transforms and arcs are not), and
   that **scaling is not exactly reversible** under integer storage.
2. Update `docs/design/layout-view.md` §6.1's operation list to match what now exists.
3. Report back before **L2 — performance** is briefed.
