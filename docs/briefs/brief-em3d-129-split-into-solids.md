# Brief 129 — Split into Solids: an existing multi-solid Step object becomes a group of pieces

**Series:** [3D EM, ninth series](brief-em3d-126-overview.md) · **Tag:** `R-em3d129-n`
**Area:** `src/Design/ThreeD/Step/` (a new `StepSplit.cs` beside `StepImport.cs`, reusing `SameFace`, `References`,
`StepTree`), `src/Design/ThreeD/C3dValidation.cs` (or wherever `check`'s kernel-backed findings are made),
`src/Ui/ThreeD/C3dEditorViewModel.TreeMenu.cs` and `C3dEditorViewModel.Step.cs` (the command and its one undo entry),
`src/Cli/` (`check`, `explain`), `docs/user/src/reference/drawing-in-3d.md`. Findings in `src/Design/RESOLVED.md`.
**Depends on:** 127 · **Blocks:** nothing (130 uses it for documents imported before 128)

---

## 0. What this brief settles

A document imported before 128 holds a multi-solid product as **one** Step object with no `Solid`. The owner's
package is one: a compound of seven solids with one material. This brief gives that object a command that turns it
into **what 128's import would have produced**: one Step object per solid, gathered in a group, with every reference
to the old object's faces moved to the piece that owns the face.

The old object stays legal (overview D8). Nothing splits unless asked.

## 1. `R-em3d129-1` — `check` and `explain` name it

**R-em3d129-1a.** A Step object with no `Solid` whose part has more than one solid gets one finding, from the read
`check` already performs for Step objects:
- **WARNING** when its solids' colours (127's per-solid colour) differ: `"'<name>' is <n> solids of <file>
  part <p> in one object, so they share one material; their colours differ. Split into Solids gives each its own."`
- **NOTE** otherwise, with the same text minus the colour clause.

No finding for a product of one solid.

**R-em3d129-1b.** `explain` on the object reports its solid count and each solid's colour, face count and volume.
That is how an agent, which edits by writing the document (CLAUDE.md: no per-primitive edit verbs), learns what to
write. It needs no new verb. An agent that writes the pieces by hand writes `Solid = 1..n`, and 127 builds them.
**Moving face references by hand is the agent's own work.** `check` reports any reference that no longer lands.

## 2. `R-em3d129-2` — the split, in `src/Design`

**R-em3d129-2a.** `StepSplit.Plan(doc, c3dPath, objectName, kernel)` returns either a refusal or a plan:
- **the pieces:** one `C3dStep` per closed solid, with `File`, `Part`, `Hash`, `Unit` and `SourcePath` copied, and
  `Solid = k`. Each also copies the old object's `Placement`, `Material`, `Role`, `Appearance`, `Transparency`,
  `Hidden` and `Model`. The placement composes on top of the file's location exactly as it did for the whole, so
  **nothing moves**. Each piece is named per overview D5.
- **the group:** the pieces' `Group` is the old object's `Group` path plus one new level, named **after the old
  object**. A group is not an object's namespace, so the name is free once the object is gone.
  - Say the package `pkg` sat in group `board`: the pieces land in `board/pkg`.
  - The tree then shows one row called `pkg` where `pkg` was, and a click still takes the whole package.
- **the re-points:** for every reference `References(doc, obj)` finds on `obj/face<n>`, the one piece and face that
  coincides (`SameFace` against each piece's faces, read with `StepTree(... solid: k)`), giving `<piece>/face<m>`.

**R-em3d129-2b.** It refuses rather than guesses. Each refusal names the object and the reason:
- The object is not top-level (a boolean's operand, a fillet's or chamfer's target): `"'<name>' is the <role> of
  '<op>'; split it before it is combined, or split '<op>''s result"`. An operation's operand has no group and no
  tree row of its own, so it has nothing to split into.
- A non-closed solid in the part: list it. The split creates the closed ones only after **asking**. In the GUI that is
  a confirmation naming what is dropped; on the CLI it is n/a (§1b). Dropping geometry silently is not allowed.
- A face reference whose face matches zero or several pieces' faces: the reference is named, as `PlanReload` names it.
- A piece's proposed name already used.

**R-em3d129-2c.** References the walker must cover: ports, face boundaries, face images, thermal blocks, probes and
field-plot faces. In short, everything that spells `<object>/face<n>`. Also anything naming the **object** itself (a
`Solid` probe, a thermal source by solid, a setup's object list). An object-level reference to the whole package has
**no single piece to move to**, so it is refused with the reference named. *Alternative, if refusing proves common:*
let it name the group. That is a model change for every such reference kind; record it, do not do it here.

**First audit `StepImport.References`** against every `<object>/face` spelling in `C3dDocument` and the setup
records. It was written for reload, so it may know only what reload needed. Extend it **in place**, so reload gains
the same coverage, and record what was missing in `RESOLVED.md`.

**R-em3d129-2d.** `StepSplit.Apply(plan, doc)` replaces the object, **at its index**, with the pieces in `Solid`
order, so construction order is kept. It then rewrites the references. One function, called by the GUI and by any
future headless path.

## 3. `R-em3d129-3` — the command

**R-em3d129-3a.** **Split into Solids** appears in the tree's right-click menu and in the editor's 3D menu
(**3D ▸ STEP ▸ Split into Solids**, beside *Reload from Source*). It is enabled for exactly one selected top-level
Step object with no `Solid` whose part has more than one solid. The disabled tooltip says which condition failed.
It needs the kernel, so it is disabled with the kernel's reason when the kernel is absent (R-em3d63-3c's pattern).

**R-em3d129-3b.** The plan runs off the UI thread, like reload. A refusal goes to the Messages panel with every reason
at once, not just the first. Success is **one undo entry** (`Split <name> into <n> solids`), and the selection is the
new group as one unit.

**R-em3d129-3c.** The old copy of the STEP file stays: the pieces name the same `File` and `Hash`. No file is written.

## 4. Gates

Fixtures: 127's `two-solids-one-product.step`, plus a `.c3d` holding it as **one** object with no `Solid`, a material,
a placement offset, a port on a face of the second solid, and a face boundary on the first.

In `tests/Ui.Tests/ThreeD/StepSplitTests.cs`:
1. `check` reports the WARNING (the two solids' colours differ). `explain` reports two solids with their colours.
2. The split writes two Step objects at the old index with `Solid` 1 and 2. Both keep the material and placement
   offset, both sit in a group named after the old object, and **the scene's vertex extents are unchanged** (compare
   the built scene before and after).
3. The port and the face boundary land on the right piece's coinciding face, by geometry.
4. Refusals, each named: the object as a boolean's blank, and a reference to the whole object.
5. One undo restores the document byte for byte.
6. The command's enablement for a single-solid Step object, a split piece, and two selected objects (all disabled,
   each with its reason).

Run only `StepSplitTests`, `StepSolidTests`, and the `check`/`explain` classes the finding touches.

## 5. Docs

In `drawing-in-3d.md`, a short *Split into Solids* entry: when to use it (a model imported as one object that is
really several pieces), what it keeps (placement, material, every face reference it can move), and what it refuses.
Present tense.
