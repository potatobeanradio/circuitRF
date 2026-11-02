# Brief 128 — STEP import: one object per solid, gathered in one group

**Series:** [3D EM, ninth series](brief-em3d-126-overview.md) · **Tag:** `R-em3d128-n`
**Area:** `src/Design/ThreeD/Step/StepImport.cs` (`StepImportPart`, `Read`, `Apply`, `Import`, `Notes`, *Map all of this
colour*), `src/Design/ThreeD/C3dGroups.cs` (read only, plus a name helper if one is missing),
`src/Ui/ThreeD/StepImportDialogViewModel.cs` and its view, `src/Ui/ThreeD/C3dEditorViewModel.Step.cs` (the commit and its
selection), `src/Cli/LayoutConvert.cs` (`convert x.step`), the MCP `import`/`convert` tool text,
`docs/user/src/reference/drawing-in-3d.md`. Findings in `src/Design/RESOLVED.md` and `src/Ui/ThreeD/RESOLVED.md`.
**Depends on:** 127 · **Blocks:** nothing

---

## 0. What this brief settles

**File ▸ Import ▸ STEP…** and `circuitrf convert x.step` bring a multi-solid product in as **one Step object per
solid**, every object of the import **gathered in one group**. The user sees one row in the tree, the package. A
click in the view picks up the whole package. Expanding the row lists the pieces, and each piece takes its own
material.

**Nothing about groups is built here.** `C3dGroups` and the editor already give a group everything the owner asked
for (overview §0): a click in the view takes the whole group; the tree shows one group row with its members; Move,
Rotate, Mirror, Duplicate, Array, Align, Delete, Hide and material act on the group. **Reaching and changing one
member also already exists:** click the member's row in the tree, or, with the cursor over it in the view, press B to step through what lies under
the cursor (`Viewer3DViewModel.Selection.cs`).
Then give it a material from the row's right-click **Material** submenu (`C3dEditorViewModel.TreeMenu.cs`) or the
Inspector. Setting a material on the *group's* row sets every member (`C3dEditorViewModel.Groups.cs`). This brief
only makes the import produce a group. §5's gate proves the existing member and group paths on an imported
package, so a regression there is caught where the owner will use it.

## 1. `R-em3d128-1` — the plan: one row per solid

**R-em3d128-1a.** `StepImportPart` stays one row per **importable object**. A product of one solid is one row, as
today, with `Solid = null`. That keeps every existing file, test and document identical. A product of several solids
is **one row per solid**, each carrying:
- `Path` (the product's occurrence path);
- `Solid` (k);
- the solid's colour, *mixed* flag, face count, triangle estimate, closed/why and healing from 127;
- a proposed `Name` (overview D5);
- a material from `AutoMatch` on **the solid's** name and colour. A *mixed* solid has no colour to match.

**R-em3d128-1b.** The table shows a product of several solids as a **header row**, with its name and *n solids*, and
its solids indented beneath it. The header row has:
- a check box that checks or unchecks every solid under it;
- a material combo that sets every solid under it to *Chosen*.

*Map all of this colour* works across every solid of the file, so the leads of a package, which share one colour, get
copper in one action. A non-closed solid is listed, unchecked and disabled with its reason (R-em3d68-4b, applied
per solid). The rest of its product imports.

**R-em3d128-1c.** The units line, the healing notes and the triangle total are unchanged in meaning. The total sums
the checked solids.

## 2. `R-em3d128-2` — the group

**R-em3d128-2a.** When `Apply` creates **more than one** object, every object it creates gets `Group` set to the
import's group (overview rule 2). The group is named after the file's stem (overview D4), made legal by the same rule
`C3dGroups` applies to a typed group name, and unique among the document's groups (`<stem>_2`, ...). **One object
created: no group,** exactly as today.

**R-em3d128-2b.** In a file of several products, a product of several solids is a **nested** group under the file's
group (`<file>/<product>`), named as the product's row would have been. A product of one solid sits directly in the
file's group.

**R-em3d128-2c.** The dialog shows the group name above the table in an editable field, pre-filled per 2a. An empty
field means *no group*, an explicit opt-out for someone who wants loose objects. A name colliding with an existing
group is refused inline with the same text grouping uses.

**R-em3d128-2d.** After the commit, the editor's selection is **the group, taken whole** (one unit, per
`C3dGroups.Units`). That replaces brief 68's *select every imported part*, which said the same thing before groups
existed. Undo removes the objects and therefore the group (a group is its members' paths, so nothing else is kept).

## 3. `R-em3d128-3` — `convert x.step` and the MCP

**R-em3d128-3a.** `circuitrf convert x.step out/` lists and creates exactly what the dialog would with its defaults:
one object per closed solid, in one group named per 2a.
- `--group <name>` overrides the group name. `--group ""` means no group.
- `--part <path>` (repeatable) keeps its meaning and selects every solid of that product.
- `--part <path>#<k>` selects one solid. **This is a CLI spelling only**; the document still writes `Part` and
  `Solid` separately (overview D3).
- `--list-parts` prints one line per solid with its colour, match and name, like the dialog's table.

**R-em3d128-3b.** The JSON result lists each created object with `part`, `solid`, `name`, `material`, `match`, and
the `group`. The MCP tool's description gains one sentence: a product of several solids imports as one object per
solid, gathered in a group.

## 4. `R-em3d128-4` — notes

`Notes` reports, in addition to brief 68's notes:
- `"'<product>' is <n> solids; each is its own object in group '<group>'."`, once per such product;
- the solids with no material by name, as today for parts;
- each *mixed* solid: `"'<name>' has faces of several colours, so it was not matched by colour."`

No note for the ordinary single-solid product.

## 5. Gates

Fixtures: 127's `two-solids-one-product.step`, plus `two-products.step` (hand-written: one product of two solids, one
of one solid).

In `tests/Ui.Tests/ThreeD/StepImportSolidsTests.cs`:
1. **Plan.** `two-solids-one-product` reads as two rows with `Solid` 1 and 2, the right colours, and *mixed* on the
   second. `two-products` reads as three rows, two under a header.
2. **Group.** `Apply` on `two-solids-one-product` writes two Step objects with `Solid` 1 and 2 and both `Group` = the
   file stem. On `two-products` it writes the nested path for the two-solid product and the bare file group for the
   other. A single-solid file (an existing brief-68 fixture) writes one object with **no** `Group`, byte for byte as
   before.
3. **Opt-out and collision.** An empty group name writes no `Group`. A name already used by a group is refused.
4. **Materials.** *Map all of this colour* sets both solids that share a colour. The header row's combo sets every
   solid under it to *Chosen*. A *mixed* solid is *Unmatched*.
5. **CLI.** `convert two-solids-one-product.step` produces the same document as `Apply` with the dialog's defaults,
   **byte for byte**. `--part 1#2` imports one object with no group. `--group ""` imports two with no group.

In `tests/Ui.Tests/ThreeD/` (the existing group test class, or a new `StepImportGroupUxTests.cs`), the paths that
already exist, proved on an imported package:
6. After the commit, the selection is the group as one unit.
7. A view pick on any piece selects the whole group. Move moves every piece by the same step.
8. **Selecting one member by its tree row and setting its material changes that member only.** The group row's
   material changes every member. Each is one undo entry.

Run only the classes named here plus `StepImportTests`.

## 6. Docs

In `drawing-in-3d.md`'s STEP import section: a product of several solids imports as one object per solid, gathered in
a group named after the file. To give one piece its own material, expand the group in the tree and use the piece's
row. In the view, pressing B steps through what lies under the cursor, which reaches a single piece. Then add a paragraph on the table's header rows. Present
tense, no history (no *now* / *used to*).
