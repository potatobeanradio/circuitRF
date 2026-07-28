# Sonnet Brief — Layout + interchange fixes from owner testing

Ten items from a testing pass. **Items 1 and 6 have specific diagnoses below — read those before
investigating.** Items 5 and 7 are correctness bugs with a shared shape. The rest are contained.

Gate command is plain `dotnet test`.

---

## 1. "Group into Cell" renders the instance as if XOR'd (the cell's own view is correct)

### Diagnosis

`LayoutRenderer` merges shapes into one batched `SKPath` (`batch.AddPath(outline)`, ~line 409) and relies on
Skia's default **Winding** fill rule. The code already knows this matters: the comment at ~line 643 explains
that **holes are wound opposite to their outer ring** so Winding cuts them out.

**Outer rings are never normalized to a consistent direction.** A polygon's vertex order is whatever the user
drew, a boolean produced, or an importer emitted — so two overlapping *outer* contours with opposite winding
**cancel** under Winding fill. That is the XOR appearance.

It shows on the instance and not in the cell's own view because those are two different render paths: the
cell's own view draws **per shape** (each `DrawPath` composites independently — R8a's darkening), while an
instance draws the **batched, cached** path from R-L3a-3. Same geometry, different fill accounting.

**R-fix-1. Normalize winding when building any batched path: every outer ring one direction, every hole the
opposite.** Compute the signed area per contour and reverse as needed.

**This is not instance-specific.** The same batched path serves L2c's LOD and merge tier, so the identical
cancellation must be reachable on ordinary geometry at low zoom or high shape counts. Fix it in the batch
builder, and test both paths.

## 2. Copy/paste of an instance into another design cannot resolve the cell

`LayoutInstance.CellRef` is documented as *"relative path to the referenced cell folder"* — relative to the
containing `.clay`. A pasted fragment carries that string verbatim, so it is resolved against the **wrong
base directory** in the destination.

L1f's fragment already carries its source `DbuPerMicron` and `LayerDef`s precisely so it is self-describing
(R-L1f-1). It needs the same treatment here.

**R-fix-2. The fragment carries each referenced cell's identity in a base-independent form** — its
workspace-relative path, plus the absolute path as a fallback — and **paste recomputes `CellRef` relative to
the destination `.clay`.** Where the cell is not present in the destination workspace, report it clearly by
name; the instance still pastes and renders as R-L3a-1's placeholder, so the user can see what is missing
rather than losing the geometry.

## 3. Properties Inspector goes stale after clicking the project tree

Clicking the tree makes it the active dockable. Selecting geometry afterwards changes the layout's
*selection* but nothing tells the Properties tool that the layout is the subject again — hence the workaround
of switching tabs and back, which does re-activate the document.

**R-fix-3. Interacting with a layout canvas makes its document the active dockable.** The canvas already
calls `Focus()` for keyboard input, but that is control focus, not Dock activation — they are different
things and only the latter drives the Properties tool.

Fix it at the activation seam rather than by having the Properties tool listen to every open document's
selection: the latter would show properties for a document the user is not looking at. Check whether the
schematic and symbol canvases already do this — if they do, this is another case of the layout canvas
diverging from an established pattern, and the fix is to converge.

## 4. GDSII export: no dialog when nothing changes

R-L4a-3 requires the export dialog to state what will be converted. **When the count of every conversion is
zero, show nothing and export.** A dialog that says "nothing will change" trains users to dismiss dialogs
without reading, which defeats the purpose of the ones that matter.

## 5. Exports must use the in-memory design, not the last saved file

**All three exporters — GDSII, DXF, Gerber — currently export what is on disk.** A user who draws something
and exports without saving gets a file that does not match what they are looking at. That is a serious
correctness bug: the export silently disagrees with the screen.

**R-fix-4. Export takes the live `LayoutView` from the open document.** Never a path re-read from disk when
the document is open. Where an export is invoked with no document open (e.g. from the project tree), loading
from disk is correct — but if the document *is* open, memory always wins, dirty or not.

**Audit every export entry point** — menu, context menu, project tree, and anything L4c adds — and make them
all route through the same accessor. This is the kind of bug that gets fixed on one path and left on three.

## 6. Unexpected "Sample Label" text in GDSII and DXF output

### Leading hypothesis — check this before hunting for a metadata leak

Both writers emit text **only** from a `LabelShape` (`DxfWriter.WriteText` is reached solely from
`case LabelShape`, and the GDSII writer mirrors it). Neither writes a user name, author field, or path into
any annotation. So the overwhelmingly likely explanation is that **the layout genuinely contains a label with
that text that is invisible in circuitRF.**

That is consistent with the label-height defect fixed earlier: a label authored at the old hardcoded 5 µm
default is roughly 1/4000 of a PCB-scale viewport — invisible on screen, including its caret. **KLayout
renders GDSII `TEXT` at a fixed screen size regardless of zoom**, so the same label is plainly readable
there. A label typed while testing the label tool and never seen would behave exactly this way, and would
appear in both formats because it is real model data.

**Diagnostic, in order:**
1. Search the `.clay` for a `LabelShape` whose text matches. If found, this is explained — delete it and
   confirm the export is clean.
2. Only if not found, grep the writers and their callers for `Environment.UserName`, machine or account
   metadata, and any hardcoded annotation.

**R-fix-5. Whatever the cause, add a guard**: report the count of `TEXT`/label records written in the export
summary. Text a user did not knowingly place is exactly the thing an export report should surface.

**Also worth fixing regardless**: a label too small to see is a trap. Consider a minimum on-screen render
size for labels, as the label brief already specified for the *in-progress* ghost — the same argument applies
to committed labels.

## 7. "Import GDSII Library…" reports success but nothing appears

Per L4a, import **creates real cells** through `CellFolder` machinery — a library becomes N circuitRF cells.
It does not import into the focused layout. If nothing appears, one of: the cells are not actually created;
they are created outside the workspace; or the project tree is not refreshed.

**R-fix-6. Determine which, then fix it and make the outcome legible.** The success message must name what
happened and where:

> *Imported 12 cells from `mydesign.gds` into `<workspace>`: `TOP`, `VIA_ARRAY`, `PAD`, … (9 more).
> Top-level cell: `TOP`.*

Cell count, destination, the names (truncated sensibly), and **which cell is top-level** — that last one is
what the user actually wants to open. Offer to open it, or open it automatically.

Also confirm the project tree refreshes without a manual reload.

## 8. File menu reorganization

- **File → Import →** `Data…` (renamed from "Import Data…") and `GDSII…` (renamed from "Import GDSII
  Library…").
- **File → Export →** `Data`, `GDSII`, `DXF`, `Gerber`.
- **The three layout export items are disabled unless a layout document is active**, with a reason on hover
  per R13a — *"Requires an active layout document."*
- **New Layout** and **New Technology** move under **New Cell**.
- **A separator above Open Workspace.**

Keep the underlying commands unchanged — this is menu structure only. Mirror the changes in the macOS native
menu as well as the in-window menu, and watch the `$parent[Window]` binding gotcha already documented in
`src/Ui/CLAUDE.md`.

## 9. Acknowledgments dialog: add Clipper2

Add **Clipper2** — <https://github.com/AngusJohnson/Clipper2>, Boost Software License — to the in-app
Acknowledgments dialog. It is already in `README.md`; the dialog was missed.

While there, check the dialog against the README's list so the two agree, and note any other drift.

---

## Guardrails

- Fix only these ten items. No refactoring of the render pipeline beyond R-fix-1's winding normalization.
- Do not change the entity mappings, the bulge identity, `SPLINE` export, or array handling.
- Item 6 is a **diagnosis first** — do not add metadata-stripping code before confirming the cause.
- Don't touch `src/Core`, `src/Engine`, `RfCore`.

## Gate

1. Builds green; `dotnet test` green; no existing test regresses.
2. **Winding (R-fix-1)** — a cell containing two overlapping same-layer polygons with **opposite** vertex
   order renders identically whether drawn directly or as an instance. Off-screen pixel comparison; the
   overlap must be filled, not cancelled. Add the same assertion for the LOD/merge path.
3. **Paste across designs (R-fix-2)** — an instance copied from a layout in one folder and pasted into
   another resolves its cell; pasting into a workspace lacking the cell reports it by name and renders the
   placeholder rather than dropping the instance.
4. **Properties activation (R-fix-3)** — click the project tree, then click layout geometry: the inspector
   shows that geometry with no tab switching.
5. **Silent clean export (item 4)** — a design with no curves, holes or bitmaps exports to GDSII with **no**
   dialog; one with any of them still shows it.
6. **In-memory export (R-fix-4)** — draw a shape, do **not** save, export to each of GDSII, DXF and Gerber,
   and assert the shape is present in all three. Repeat from every export entry point.
7. **Label accounting (R-fix-5)** — the export summary reports the number of text records written.
8. **GDSII import (R-fix-6)** — importing a multi-structure library creates the cells, refreshes the tree,
   and reports count, destination, names and the top-level cell.
9. **Menus (item 8)** — the new structure exists in both menu surfaces; the three export items are disabled
   with a reason when no layout is active and enabled when one is.
10. **Acknowledgments (item 9)** — Clipper2 appears in the dialog and the dialog matches the README.

## On completion

Record in `src/Ui/CLAUDE.md`: **R-fix-1's winding normalization and that the batched path serves both
instances and the LOD/merge tier** (so the bug was never instance-specific); **R-fix-2's base-independent
cell reference** in the clipboard fragment; **R-fix-4 — exports read memory, never disk, when a document is
open** — and list the entry points audited; and the **actual cause of item 6**, whichever it turned out to be.
