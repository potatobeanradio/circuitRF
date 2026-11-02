# Brief 41 — the 3D view and the `.c3d` document

**Series:** [3D EM, third series](brief-em3d-40-overview.md) · **Tag:** `R-em3d41-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §6.3 (Tier B), §6.4 (naming);
[`workspace-and-project-tree.md`](../design/workspace-and-project-tree.md) §1.2, §2, §4;
[`layout-view.md`](../design/layout-view.md) §1 (units), §7 (hierarchy)
**Area:** `src/Design/Cells/`, `src/Design/ThreeD/` (new), every reference walker in `src/Design`,
`src/Ui` and `src/Cli`, the Project Tree, `src/Cli/{DocumentKinds,Check,Find,Authoring,Reference}.cs`
**Depends on:** — · **Blocks:** everything else in the series
**Owner decisions:** D1 (name and extension), D4 (integer DBU plus placement), D10 (lazy `3d/`)

---

## 0. What this brief delivers

A cell can have a **3D view**. The view is a `.c3d` file in the cell's `3d/` sub-folder, with primacy
exactly as the other three views have it.

- The format is readable, written, round-trips byte for byte, and is described by a reference page.
- The Project Tree shows it.
- **New ▸ 3D View** makes an empty one.
- Rename Cell and Remove Cell count and rewrite `.c3d` references.
- `check`, `find` and `new cell --views 3d` know it.

**Nothing is drawn or solved yet.** Brief 42 elaborates; brief 43 opens a window. This brief only makes the
document real, so both of those stand on a format that will not move.

---

## 1. `R-em3d41-1` — the fourth view type

**`R-em3d41-1a`** `ViewType.ThreeD`, sub-folder `3d`, extension `.c3d` (`CellFolder.SubFolderName`,
`ViewExtension`). Primacy is the five-branch rule, unchanged. `.ccell` gains a primary-3D entry beside the
other three, written only when set, so every existing `.ccell` is byte-identical.

**`R-em3d41-1b` The sub-folder is created when the first 3D view is made (D10)**, not by
`CreateCellFolder`. Every existing cell lacks it, so every reader must treat an absent `3d/` as
`NoView`. `ResolvePrimary` already does. **Nothing may create `3d/` as a side effect of reading.**

**`R-em3d41-1c` Every `switch` on `ViewType` gets a `ThreeD` arm, or a comment saying why it has none.**
There are ~265 uses in ~51 files. Most are exhaustive switches that throw on an unknown value, so a missed
one crashes rather than misbehaves. Find them with `grep -rn "ViewType\." src`, not from memory. A test
enumerates `ViewType` through the functions that must accept every member (`SubFolderName`,
`ViewExtension`, `ResolvePrimary`, the Project Tree's grouping, `CellViewFileValidator`), so a fifth view
type later fails loudly in one place.

**`R-em3d41-1d` Attachments.** A `.c3d` has none in this series. The attachment rule (§1.2.1 — same
sub-folder, same stem) applies unchanged if one arrives.

## 2. `R-em3d41-2` — the format

JSON, as `.clay` and `.cem`. `FormatVersion: 1`. Human-readable, stable key order, omit-at-default.
**Every coordinate is an integer in DBU.** A point is `[x, y, z]`, and a point on a drawing plane is
`[u, v]`.

```jsonc
{
  "FormatVersion": 1,
  "DbuPerMicron": 1000,                 // as .clay; default 1000 (1 nm)
  "DisplayUnit": "Mil",                 // LayoutUnit: Nm | Um | Mm | Mil | Inch
  "SnapDbu": 25400,                     // the drawing grid; never re-snaps geometry (layout-view §1.5)
  "TechRef": "../../tech/package.ctech",// resolved as a .clay's is; materials come from here
  "Objects": [
    { "$type": "Box",      "Name": "base", "Material": "Alumina",
      "Min": [0,0,0], "Size": [5080000,5080000,254000] },   // a corner and a size, never two corners
    { "$type": "Prism",    "Name": "lead1", "Material": "Gold",
      "Plane": "XY", "Offset": 254000,
      "Outline": [[..],[..]], "Holes": [],
      "Height": 17780,                  // along the plane's normal; negative is allowed
      "Shear": [0,0] },                 // [du,dv] of the top against the bottom; omitted when zero
    { "$type": "Cylinder", "Name": "via1", "Material": "Gold",
      "Base": [x,y,z], "Axis": "Z", "Length": 254000, "Radius": 76200 },  // any other axis: Placement
    { "$type": "Sheet",    "Name": "trace", "Material": "Gold",
      "Plane": "XY", "Offset": 254000,
      "Rect": { "Min": [u,v], "Size": [du,dv] },   // a rectangle; or "Outline"/"Holes" for a polygon
      "ThicknessUm": 5 },
    { "$type": "Polyline", "Name": "path1", "Plane": "XZ", "Offset": 0,
      "Points": [[u,v],..], "Closed": false },  // construction only — never solved (§2e)
    { "$type": "Polyhedron", "Name": "lid", "Material": "Lid alloy",
      "Vertices": [[x,y,z],..],
      "Faces": [ { "Name": "f0", "Outer": [0,1,2,3], "Holes": [] }, .. ] }
  ],
  "Instances": [
    { "Name": "U1", "CellRef": "../MMIC",   // as a .clay instance's CellRef, ws:// included
      "View": "Layout",                     // "ThreeD" (default) or "Layout"; brief 48 swaps it
      "Placement": { "Origin": [x,y,z] },
      "Array": { "Counts": [4,1,1], "Pitch": [2540000,0,0] } }   // optional; brief 42
  ],
  "Variables": [],                      // brief 51: VARs, as a .csch carries them
  "Ports": [],                          // brief 49
  "FaceBoundaries": [],                 // brief 49
  "Setups": []                          // brief 42: EmSetup objects, the .cem schema minus LayoutRef
}
```

**`R-em3d41-2-dims` Dimensions are stored as sizes, not as second corners.** A box is a corner and a
`Size`, a prism has a `Height`, a cylinder a `Length` and a `Radius`, a rectangle a `Min` and a `Size`.
The reason is brief 51: a width typed as `w` must have a **field to live in**. With two corners, "the
width is `w`" could only be written as `Max.x = Min.x + w`, an expression the user never typed, and
moving the box would have to rewrite it. A `Size` component is `w` and nothing else. A `Size` component
is positive; the writer normalises a negative one by moving `Min`. A prism's `Height` keeps its sign,
because it says which way the prism was pulled from its plane.

**`R-em3d41-2a` Every object has:**
- a **`Name`**, unique in the document. It is validated as `NameValidator` validates cell names, with
  `airbox` reserved (the air box's faces are `airbox/xmin` …);
- a **`Material`** naming a material in the document's technology. The role comes from the material,
  overridable by `Role` (Conductor, Dielectric, Air). Brief 42 states the inference;
- an optional **`Placement`**: `{ "Origin": [x,y,z], "Rotate": [{"Axis":"Z","Deg":90}, ..],
  "MirrorX": true }`, identity by default and omitted then. The rotations apply in list order, after the
  mirror, then the translation — layout's mirror-then-rotate order, extended by a list;
- an optional **`Hidden`**: this is **document** state, like a layer's visibility in `.ctech`. The view's
  camera is not document state and lives in the workspace's window state, as brief 28 put it.

**`R-em3d41-2b` Order is construction order.** The list order is `Em3dSolid.Order`: where two solids
overlap, the later one wins the volume. That is the one overlap rule both backends already honour. The
writer keeps the order and never sorts.

**`R-em3d41-2c` Face names (overview §1e).** Faces are named, never indexed:
- **Box:** `xmin xmax ymin ymax zmin zmax`, in its own frame;
- **Prism:** `bottom`, `top`, and `side<k>`, where k is the outline edge from vertex k to k+1. A hole's
  side faces are `hole<h>.side<k>`;
- **Cylinder:** `bottom`, `top`, `side`;
- **Polyhedron:** each face's `Name`, stored.

A polyhedron's face names are unique within the object. A folded face's pieces are `<name>.<i>` (brief 47).

**`R-em3d41-2d` Room for what comes later.** The reader rejects an unknown `$type` **by name**, as a
refusal and not a crash. A boolean object (overview §1b) and an expression-valued field (brief 51) are
therefore additive and need no format version change. For brief 51: a numeric field is today an integer,
and a string there is refused with *"expressions arrive in a later version"*. The writer never emits one.
The `Variables` key is read and written from this brief (an empty list is omitted) so that a document
written after brief 51 does not lose its VARs if opened by a build from before it.

**`R-em3d41-2e` A polyline is construction geometry.** It is drawn and snapped to, and it is the input to
Extrude (brief 45). It is **never** in the solved problem. It has no material. Its tree entry says
*construction*.

**`R-em3d41-2f` Validation on read, all problems not the first** (R-em3d3-1c's rule):
- duplicate names;
- a prism or sheet outline with fewer than three distinct points;
- a polyhedron that is not closed. Every edge must be used by exactly two faces, in opposite directions,
  and a failure names the edge's two vertices;
- a face that is not planar within **1 DBU**;
- an unknown material, as a **warning**, because materials resolve late (brief 42);
- a zero-volume box, prism or cylinder.

## 3. `R-em3d41-3` — the model and persistence

**`R-em3d41-3a`** `C3dDocument` is the mutable working model, framework-free: `EmSetup` and `LayoutModel`
are the pattern. `C3dPersistence` reads and writes it. Write through `AtomicFile`, as every other document
does.

**`R-em3d41-3b` Round trip.** Read then write is byte-identical, on fixtures that use every `$type`, a
placement of each kind, a hole, an empty document, and a mil document.

**`R-em3d41-3c` `C3dPlacement`** composes and inverts exactly. A composition of 90° rotations and mirrors
gives an integer matrix. Test that composing the 24 rotations of a cube with and without the mirror gives
exactly 48 distinct integer matrices.

## 4. `R-em3d41-4` — the workspace sees it

- **Project Tree:** the 3D group sits under the cell after Layout, with the same primary marking and the
  same *Make Primary* on a cell with several.
- **New ▸ 3D View** in the cell's context menu creates `3d/` if absent and writes an empty `.c3d`:
  - `DbuPerMicron` from the cell's primary `.clay` if it has one, else `LayoutUnits.DefaultDbuPerMicron`;
  - `DisplayUnit` from the cell's primary `.clay` if it has one, else the technology's
    `DefaultDisplayUnit`, else `Um` (owner decision D4, overview §1d);
  - `SnapDbu` from the technology's `DefaultSnapDbu`;
  - `TechRef` as a new layout's would be.

  A test covers each branch of the display-unit rule: cell with a `.clay` in mil under a µm technology →
  mil; no `.clay`, mm technology → mm; neither → µm.
- **Rename Cell / Remove Cell:** `CellUsageScanner` counts and rewrites `.c3d` instance references. *Easy to forget; breaks
  designs when omitted* — `layout-view.md` §7 said it about `.clay`.
  The instance record is defined here (§2) so that the walkers can read it. What it *means* is brief 42's.
- **Every other walker that follows cell references** learns `.c3d`, or says in a comment why not:
  - `WorkspaceRenameRepoint`;
  - `WorkspaceArchiveWriter`'s repointing;
  - TM2's moved-cell forwarding (`.cmoves`);
  - `CrossWorkspaceCellCopy`;
  - the revision-control snapshot's file set.

  Find them with `grep -rln "\.clay" src`, and treat each hit as a question to answer, not a line to copy.

## 5. `R-em3d41-5` — the CLI and the reference page

- `DocumentKinds.Classify` knows `.c3d` **by content**: the `FormatVersion` + `Objects` shape. A
  motion-capture file with the same extension is named as foreign, not reported as a broken `.c3d`
  (overview §1c).
- `check <x.c3d>` reports R-em3d41-2f's findings through a validator the GUI also uses. There is no
  CLI-only rule (the `check` rule in `cli.md`).
- `find` lists 3D views.
- `new cell <ws> <name> --views 3d` (alone, or with others) creates it through the same function the
  GUI's *New ▸ 3D View* calls. There is no second copy (the `Authoring.cs` rule).
- **The reference page** for `.c3d` is generated the way the `.clay` and `.cem` pages are
  (`DocumentSchema`, `Reference.cs`). It covers every field, the face-name table, the placement order and
  the refusals. Headless authoring *is* this page (em-3d.md §4.6). The MCP server's `reference` tool
  serves it with no change of its own.

## 6. Gate

`tests/Ui.Tests/ThreeD/` (headless), `tests/Firewall.Tests`.

1. **Round trip** on every fixture (§3b), byte for byte.
2. **Every `ViewType` accepted** by the §1c functions (the enumeration test).
3. **Absent `3d/`** everywhere: a workspace from before this brief opens, scans, renames and archives with
   no `3d/` created and no warning.
4. **Validation lists all problems.** A fixture with five defects reports five.
5. **Rename Cell** rewrites a `.c3d` instance reference. **Remove Cell** counts it.
6. **Placement:** 48 distinct integer matrices; inverse ∘ placement = identity exactly.
7. **`new cell --views 3d`**, run as a process, gives the same bytes as the in-process call.
8. **Classify:** a `.c3d` from another program is named foreign.
9. **Firewall:** `src/Design/ThreeD` references no UI framework.

## 7. Owner check

Create a 3D view on a cell. Check that it shows in the tree, that *Make Primary* works with two, and that
renaming the cell keeps it. The tree has no picture to check beyond this.

## 8. Scope

- No elaboration and no window.
- Instances are read, written and followed by the reference walkers. What they elaborate to is brief 42's.
- No expressions (brief 51).
