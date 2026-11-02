# Brief — 3D EM, ninth series: a STEP part's solids as objects, grouped as one

**Status:** Briefed, not built · **Date:** 2026-10-09 · **Decisions:** D1–D2 settled by the owner; D3–D9 open, each
with a recommended default (§3)
**Design note:** [`docs/design/em-3d.md`](../design/em-3d.md) (the `.c3d` object model);
[brief 68](brief-em3d-68-step-import.md) (STEP import) and [brief 69](brief-em3d-69-step-export.md) (STEP export)
own every rule this series does not change.
**Previous briefs:** 1–125. This series is numbered **126–130**.
**Area:** `tools/geometry-worker/geometry_worker.cpp` (the STEP reader and the `step` tree node),
`src/Design/ThreeD/` (`C3dDocument`'s `C3dStep`, `C3dGroups`, `C3dValidation`, `C3dElaborator`), `src/Design/ThreeD/Step/StepImport.cs`,
`src/Design/ThreeD/Occ/` (`GeometryKernel`, `GeometryKernelTree`), `src/Ui/ThreeD/` (the import dialog, the tree menu, the
editor's STEP commands), `src/Cli/` (`convert`, `check`, `explain`, `DocumentSchema`), `docs/user/src/reference/drawing-in-3d.md`
**Requirement tag:** `R-em3d<n>-<m>` as before.

---

## 0. The short answer

The owner imported a STEP model of a power-transistor package: copper leads and a plastic overmold body. It arrived
as **one object** in the tree. So the leads and the body could not have different materials, and no piece could be
selected or edited on its own.

The owner asked for two things:

1. **Break an imported STEP object into its pieces**, so each piece can have its own material and be edited.
2. **Keep the import convenient**: after import the pieces should already be separate objects, *gathered as one
   thing* in the tree. A click in the view picks up the whole package, and each piece is reachable under it.

### Why it arrived as one object

Brief 68 settled that **a `Step` object is one solid part** (R-em3d68-1a/-1b). One name, one material, one
placement and one face namespace is what lets a STEP part be a boolean operand, a fillet target and a port's face.
**The code implements "one part" as one STEP *product***, and assumes a product is one solid. That assumption is wrong
often enough to matter. A package model is commonly exported as **one product holding several solids**. The owner's
file has one `PRODUCT` and **seven `MANIFOLD_SOLID_BREP`s**, which the worker's reader holds as one compound shape.
`BuildStep` builds the compound as one object, and `ClosedSolid` accepts it.

Colours fail the same way. `ReadStep` keeps **one colour per product**: the first it finds, from the product's label
or its shape. The file carried 17 colours over its solids and faces, so material-by-colour (R-em3d68-3a) had a
single colour to match against, where there should have been one per solid.

**So this series finishes brief 68's own decision rather than reversing it.** A Step object stays one solid. A
product of several solids becomes several Step objects, and the import gathers them in a **group** (the existing
`C3dGroups` model) so the package still acts as one thing.

### What groups already do (no new UX is invented here)

`C3dGroups` and `C3dEditorViewModel.Groups.cs` already provide the following:
- **A click in the view takes the whole group** (Object mode). Move, Rotate, Mirror, Duplicate, Array, Align,
  Delete, Hide, material and boolean all act on the selection, so they act on the group.
- **The tree shows the group as one row with its members under it.** Clicking a member's row reaches that member alone.
- A group has no placement of its own. It is organisation only: nothing reaches a solver.
- Grouping, ungrouping and renaming are each one undo entry.

So "the pieces as one circuitRF object" is a group the import creates. The pieces are its members.

## 1. The series

| Brief | What | Depends on |
|---|---|---|
| [127](brief-em3d-127-step-solid-addressing.md) | **Address one solid inside a part.** `C3dStep.Solid`; the worker builds the k-th solid; per-solid colour; per-solid closed check; reload matches solids by geometry | — |
| [128](brief-em3d-128-step-import-solids-grouped.md) | **Import: one object per solid, gathered in one group.** The dialog's table, `convert`, material by colour per solid, the group named after the file | 127 |
| [129](brief-em3d-129-split-into-solids.md) | **Split into Solids** on an existing multi-solid Step object, with every reference re-pointed by geometry; `check` names a multi-solid object | 127 |
| [130](brief-em3d-130-editing-imported-solids.md) | **Editing an imported piece.** A decision brief: replace with a fitted primitive, convert a flat-faced solid to a polyhedron, or B-rep face editing | 127 (129 for the owner's file) |

128 and 129 are independent of each other once 127 lands. 130 is written as a decision brief: it states the options
and their cost, and builds nothing until the owner picks.

## 2. The rules this series adds

> **1. A Step object is one solid, now in fact.** `Part` names the product; `Solid` (1-based, the reader's
> topological order) names one solid within it. No `Solid` means the whole part, which is legal for a product of one
> solid and **named by `check`** for a product of several (129).

> **2. An import that creates more than one object gathers them in one group.** The group is named after the file
> (D4). A product of one solid is one object, as before. An import that creates a single object creates no group.

> **3. Every piece keeps every rule of brief 68.** Each piece has its own name, material, placement, `face<n>`
> namespace, appearance and transparency. Each is a boolean operand, a fillet target and a port's face. Each is
> exported as its own part (brief 69 is unchanged: it already writes one part per object).

> **4. Nothing fuzzier than brief 68.** Material by name, then by exact colour, then none. A solid with several face
> colours has no colour for matching (D6). A material is never inferred from shape, size or position.

> **5. References move by geometry, never by index** (R-em3d68-5c's rule, reused). Splitting an object or reloading a
> revised file re-points each face reference to the one face that coincides. Zero or several matches is a refusal
> naming the reference.

## 3. Decisions

**D1 (owner, settled).** Breaking a STEP model into its solids is wanted, both at import and for a model already
imported.

**D2 (owner, settled).** After import the pieces are separate objects **gathered as one** in the object tree. This is
`C3dGroups`, created by the import.

**D3 (open). How a piece is addressed.** *Recommended:* a new optional `Solid` integer on `C3dStep`, 1-based, in the
order `TopExp_Explorer(shape, TopAbs_SOLID)` visits the part's healed shape. It is stable for identical bytes, which
is the same guarantee `Part` and `face<n>` already rest on (both are meaningful for the recorded `Hash`). *Rejected:*
spelling it into `Part` (`1#3`), which would make every existing `Part` reader parse a second grammar.

**D4 (open). What the group is called.** *Recommended:* after the **file** (its stem, made a legal group name and
unique among groups). That is the name of the thing the user picked. Product names in CAD exports are often generic
(`part`, `product`, `Body`). In a file of several multi-solid products, each product is a **nested** group under the
file's group, named as brief 68 names a part today (product name, else `<stem>_<i>`).

**D5 (open). What each piece is called.** *Recommended:* the solid's own name when the file gives one and it is legal
and unused. Otherwise `<group>_<k>`, with k the `Solid` index. Object names are document-wide, since a group is not a
namespace (`C3dGroups` header). Every name stays editable in the dialog's Name column, as today.

**D6 (open). A solid's colour.** *Recommended:*
1. the solid's own colour (XCAF, on the solid's sub-shape);
2. else the colour every one of its faces shares;
3. else the part's colour when the solid's faces carry none;
4. else **none**: the table shows *mixed* and matching treats it as having no colour.

A marking face on a body would make it *mixed*. That is the honest answer, and the user maps the row by hand.
*Rejected:* the colour covering most area, which is a guess.

**D7 (open). Format compatibility.** A build before 127 reads `Solid` as an unread key. `C3dValidation` reports it,
and the elaborator builds the **whole part** for every piece, so seven overlapping copies of the package appear.
*Recommended:* accept this. The unread-key diagnostic already names it, and circuitRF is pre-1.0 with no
cross-version promise for `.c3d`. *Alternative:* bump `C3dPersistence.CurrentFormatVersion` to 2 when a document
holds a `Solid`. That makes an older build refuse the file outright, at the cost of the first version bump of the
format.

**D8 (open). The existing multi-solid object.** *Recommended:* stays legal and builds as today. `check` names it as a
WARNING when its solids carry different colours (they are probably different materials) and as a NOTE otherwise. The
tree menu offers **Split into Solids** (129). *Rejected:* splitting silently on open, because a document must not
change because it was read.

**D9 (settled 2026-10-09: (b) then (a), as recommended; built). Which editing to build (brief 130).** *Recommended:* 130 option **(b)**, *Replace with Box/Prism*, then
**(a)**, *Convert to Polyhedron*. Option (c), editing a curved STEP face directly, is a spike first. 130 sets out the
costs.

## 4. Guardrails for every brief in the series

- **No third-party STEP file enters the repository** (brief 68 §11). Fixtures are hand-written minimal AP214 text, or
  written by circuitRF's own `write-step` and post-edited by a test helper. The owner's package file is neither
  committed nor named in any brief, test, comment or RESOLVED entry.
- **The worker is native code.** A change to `geometry_worker.cpp` is rebuilt with
  `tools/geometry-worker/ensure-built.sh` (`.cmd` on Windows). The kernel tests are `[KernelFact]` and skip with a
  reason where the worker is not built. Do not build `src/Ui` while the worker copy is in progress (the exit-134
  copy race).
- **Test scope:** only the classes named in each brief's gate, with `--no-build` after one build of the projects
  touched. Never the full suite.
- **Docs:** edit `docs/user/src/reference/drawing-in-3d.md` (and any how-to it links) only. DocGen is run by the owner
  at the end of the series.
- Findings go in `src/Design/RESOLVED.md` (model, import, worker) or `src/Ui/ThreeD/RESOLVED.md` (dialog, tree,
  commands), never in `CLAUDE.md`.
