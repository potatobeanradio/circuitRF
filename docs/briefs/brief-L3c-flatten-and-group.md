# Sonnet Brief — Phase L3c: flatten and group-into-cell

**Design:** `docs/design/layout-view.md` §7 (hierarchy), §6.1 (Flatten Hierarchy vs Flatten to Polygon),
§3.1a (holes), §2.4 (technology scope). **Consumes L3a, its follow-ups, and L3b.**

**Scope is L3c: the two operations that convert between hierarchy and flat geometry.** This closes Phase L3.

**Test loop** (root `CLAUDE.md` §"Fast test loop"), two commands — this SDK rejects multiple project paths in
one invocation:
```
dotnet test tests/Ui.Tests --filter "Category!=Nightly" --no-build
dotnet test tests/Firewall.Tests --no-build
```
The documented gate still applies to completion.

---

## 1. Naming, first

§6.1 already warns that **"Flatten" alone will be misread**, because two unrelated operations share the word:

- **Flatten to Polygon…** (L1e/L1h) — a *curve* becomes a polygon.
- **Flatten Hierarchy** (this brief) — an *instance* becomes geometry.

Label them distinctly everywhere they appear, and never put them adjacent in the same menu group.

## 2. Flatten Hierarchy — one level

Replace an instance with copies of its sub-cell's contents, transformed into the parent's coordinates.

- **One level means one level** — and **an array is a level.** GDSII agrees: `AREF`, `SREF` and `BOUNDARY`
  are three distinct records, so an arrayed instance genuinely sits one rung above a plain one.

**R-L3c-1. Flatten Hierarchy removes exactly one rung. On an array, that yields N plain instances — not
geometry.** A 50×50 array flattens to 2,500 instances of the same cell; flattening *those* is a second,
deliberate action that yields shapes.

This is not an extra step bolted on for safety — it is what "one level" already meant, applied consistently.
It also earns four things:

- **The dangerous jump disappears from the default path.** Exploding an array adds 2,500 *instances*, which
  under R-L3a-3's instance caching is still **one** geometry build and 2,500 matrix draws — essentially the
  cost the array already had. The 50,000-shape outcome now requires the user to select those instances and
  ask again, with the count in front of them.
- **Partial flattening becomes possible**: explode, then flatten only the placements you need to modify and
  leave the rest as instances. That is a real workflow and it is unreachable in a one-shot flatten.
- The sub-cell's own instances still become instances of the parent, unchanged.
- **Flatten All Levels (§3) still goes all the way in one action**, so the one-shot path remains for users
  who genuinely want geometry.

**R-L3c-1a. The command states its outcome before acting**, since the same menu item now does two different
things depending on selection: *"Flatten Hierarchy → 2,500 instances"* for an array, *"→ 20 shapes"* for a
plain instance. Put it in the enablement tooltip (R13a already gives that a home) and in the confirmation.
Confirm above a modest threshold in **either** unit — shapes because of what L2c measured, instances because
2,500 objects is still a large selection to land on someone unannounced.

A separate **Explode Array** entry, enabled only for arrays, is worth adding for discoverability — but it
must route through the *same* command as Flatten Hierarchy so the two cannot diverge.

**Noted, not in scope:** the natural inverse, *Create Array from selection*, would make the operation
round-trippable. Worth a follow-up brief; do not build it here.

When a flatten does produce geometry, bitmaps, labels, holes and curved primitives all come across as
themselves — flatten changes *ownership*, not shape type.

### 2.1 The transform walk — extend the shared one, don't write a fourth

R-L1h-6 established a single coordinate traversal so that `TryChangeResolution`, paste rescale and Scale
cannot disagree about hole rings, cubic control points, path widths, via drills, label heights or
`FlattenTolDbu`. **Flatten is the fourth consumer**, and it needs a *general affine* transform (translate +
90° rotation + mirror + magnification), not just the scale the walk handles today.

**R-L3c-2. Generalize the existing walk to an affine transform rather than adding a parallel traversal.**
A fourth independent walk is how one of them ends up forgetting hole rings.

Two carried-over rules apply unchanged:
- **Arc bulges are dimensionless and must not be scaled** — but they *do* flip sign under a mirror. Assert
  both.
- **Non-uniform scaling promotes arcs to cubics** (R-L1h-7). Instance `Mag` is uniform, so arcs survive
  flatten as arcs — but a mirror combined with a rotation must still land on exact integer DBU, which 90°
  rotations and mirrors do.
- Non-integer `Mag` rounds to DBU without snapping, per L1h's rule. Flatten is therefore not perfectly
  reversible at `Mag ≠ 1`; say so in the confirmation.

### 2.2 Cross-technology flatten

A sub-cell resolves relative to the parent's directory and may reference a **different `.ctech`**. Its
shapes' `LayerKey`s then mean something different in the parent.

**R-L3c-3. When the sub-cell's technology differs from the parent's, flatten runs L1g's
`LayoutLayerMapping` and its shared dialog.** This is exactly the problem L1g solved — and the trap it
exposed applies here verbatim: both starter technologies use keys `(1,0)`–`(8,0)`, so a naive flatten would
silently land Drill geometry on Substrate with nothing missing and no warning. Reuse the component; do not
write a second reconciliation.

Per R-L1g-2, confirmation is required only when some row is uncertain — a same-technology flatten stays
silent and frictionless, which is the overwhelmingly common case.

**Nets** carry across as-is. Hierarchical net naming to avoid parent/child collisions is an LVS concern
(§9A.3) and is explicitly **not** in scope; note the collision risk in the completion write-up.

## 3. Flatten Hierarchy — all levels

Recursive, honouring R-L3a-2's depth cap.

**R-L3c-4. Compute the full resulting shape count *before* mutating anything**, and put it in the
confirmation. A deep hierarchy with arrays at two levels multiplies, and the number can be millions. Refuse
outright above a hard ceiling rather than producing a layout the editor cannot open — and name the ceiling in
the message.

A broken or unresolvable instance anywhere in the tree is **left in place as an instance** and reported,
rather than silently dropped. Partial success with a clear report beats an all-or-nothing failure on a large
design.

## 4. Group into cell

The inverse: a selection becomes a new cell, replaced in place by one instance of it.

**R-L3c-5. The geometry must not visually move.** This is the invariant the whole operation is judged on.
Pick the new cell's origin (suggest the selection's bbox minimum — predictable and easy to reason about) and
place the instance so every shape lands exactly where it was. Assert pixel-identical rendering before and
after.

- **Selection** may contain shapes *and* instances (R-fix-2's mixed selection). Instances move into the new
  cell as instances — grouping does not flatten.
- **Cell creation** goes through `CellFolder.CreateCellFolder` so the result is a real cell with a proper
  folder, not a bare `.clay`. Prompt for the name; reject collisions with the existing message.
- **Inherit `TechRef` and `DbuPerMicron` verbatim** from the parent. Same technology means §2.2's
  reconciliation never fires for a freshly grouped cell, and identical resolution means no rescale. Both are
  the reason to inherit rather than default.
- **Cycles are impossible** — the new cell is empty of references to the parent — but run R-L3a-2's check
  anyway rather than special-casing.

**R-L3c-6. Undo removes the instance and restores the shapes, but does NOT delete the created cell folder.**
State this in the confirmation and in a Messages note after undo. Deleting a folder on undo is genuinely
unsafe: the user may already have opened or edited it, another layout may have instantiated it, and file
deletion is not something an undo stack should be doing. An orphaned cell is a harmless, visible, manually
removable artifact; a deleted one the user had started working in is not. Re-doing the group should reuse the
existing cell rather than creating a second one.

## 5. Scope guardrails

- No net renaming or hierarchical net naming (LVS, §9A.3).
- No changes to instance rendering, the instance cache, or L3b's invalidation — flatten and group both go
  through the normal mutation path and let the existing machinery react.
- No changes to `Flatten to Polygon…` beyond making the two names unambiguous.
- No auto-grouping heuristics, no "find repeated geometry and make a cell" — this is user-invoked only.
- Don't touch `src/Core`, `src/Engine`, `RfCore`.

## 6. Gate (acceptance)

1. Builds green (`TreatWarningsAsErrors=true`); documented gate green; no existing test regresses.
2. **Flatten one level is visually identity** — for each of the 8 rotation/mirror combinations and for
   `Mag` of 1 and 2, the flattened result renders **pixel-identically** to the instance it replaced.
3. **One level stops at one level** — a two-deep hierarchy flattened once leaves the inner instances intact
   as instances.
4. **Arrays explode, they do not vaporize (R-L3c-1)** — flattening a 5×5 array yields **25 plain instances**
   at the right positions, each rendering identically to its former placement (pixel-identical overall).
   Flattening those 25 yields the geometry. Assert `PathsConstructed` after the explode is still
   **O(sub-cell shapes)**, confirming instance caching still holds — the explode must not quietly become 25
   independent builds. The outcome preview (R-L3c-1a) reports "25 instances" then "N shapes".
5. **The shared walk (R-L3c-2)** — flattening a fixture containing a polygon **with holes**, a `Curve` with
   **cubic** edges, a `Path` (width), a `Via` (pad and drill), a `Label` (height) and a curved shape with
   `FlattenTolDbu` transforms every one of those fields. Arc bulges are **unchanged** under rotation and
   **sign-flipped** under mirror.
6. **Cross-technology (R-L3c-3)** — flattening an instance whose sub-cell uses the other starter technology
   **requires confirmation** and does not silently remap Drill onto Substrate. Same-technology flatten raises
   no dialog.
7. **Flatten all levels** — a three-deep hierarchy fully flattens; the pre-computed count matches the actual
   result; an unresolvable instance survives as an instance and is reported; the hard ceiling refuses with
   its number named.
8. **Group does not move geometry (R-L3c-5)** — pixel-identical render before and after, for a selection
   containing shapes, a polygon with holes, and an instance.
9. **Group creates a real cell** — the folder is well-formed, appears in the project tree, opens as a layout,
   and inherits the parent's `TechRef` and `DbuPerMicron`.
10. **Group undo (R-L3c-6)** — shapes are restored at their original indices, the instance is gone, the cell
    folder **remains**, and a Messages note says so. Redo reuses that cell rather than creating a second.
11. **Round-trip** — group a selection then flatten the resulting instance; the geometry is byte-identical to
    the original (`LayoutPersistence.Serialize` equality).

## 7. On completion

1. Add a "Phase L3c — COMPLETE" entry at the top of `src/Ui/CLAUDE.md`. Call out: **R-L3c-2** (the shared
   walk generalized to affine, now four consumers, and the bulge sign-flip under mirror); **R-L3c-3**
   (cross-technology flatten reuses L1g, and why the identical `(1,0)`–`(8,0)` key ranges make it
   load-bearing); **R-L3c-5** and **R-L3c-6** with the reasoning for not deleting the cell on undo; and the
   deferred hierarchical-net-naming gap.
2. State whether **Phase L3 is complete**, and re-run the instance/array benchmark so L2d can be scoped
   against hierarchy-shaped shape counts, as the L3a brief asked.
