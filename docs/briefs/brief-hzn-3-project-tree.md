# HZN-3 — `.hzn` in the Project Tree; a design opens in the 3D editor

Series: `brief-hzn-0-overview.md`. Needs HZN-2.

## Why

A `.hzn` file today is an unrecognised file in the tree. It shows no designs and opens nothing.

## What to build

### R-hzn3-1 — the node

- **Inside the workspace folder**, a `.hzn` is found by the existing scan by extension
  (`docs/design/workspace-and-project-tree.md` §3). It renders with its own icon and its project `name` as a tooltip.
- **Outside the workspace**, the user adds it as a **Known File** (§5), from Add Known File and by dropping it on the
  tree. It renders under Known Files like any other. A path that cannot be resolved is the existing unresolved Known
  File state.
- **Expanding the node lists its designs** in file order: a 3D icon for a 3D design, the schematic icon for a circuit
  design.
  - The list comes from `HznFile` (brief 1), read lazily on first expand and cached against the file's modification
    time.
  - A file that fails the version gate shows one child row carrying the refusal sentence, and nothing else.

### R-hzn3-2 — double-click opens a 3D design

- Double-clicking a 3D design runs `HznToC3d.Translate` (brief 2).
- **When it translates**, the design opens in the 3D editor as an ordinary `.c3d` document:
  - the tab title is `<design> — <file>.hzn`
  - every view, panel, measurement and render that works on a `.c3d` works on it
- The editor reaches the document through a **document source**: one seam in the 3D editor's load/save path, so a
  `.c3d` on disk and a design held in a `.hzn` are two implementations of it.
  - Saving is disabled until brief 5.
  - Until then the source reports itself read-only, and the editor shows the read-only state it already has.
- **When it is refused**, nothing opens. The refusal appears exactly as an unopenable document's does today, with every
  item listed and grouped (overview D3). Find the existing pattern; do not add a second one.
- **A circuit design** is shown but does not open. Double-clicking it says that circuit designs in `.hzn` files are not
  opened by circuitRF.

### R-hzn3-3 — the context menu

On the `.hzn` node:
- Open in Hertzon (brief 8, disabled until then, saying what enables it)
- Reveal in Finder / Explorer
- Remove from Known Files (Known Files only)

On a 3D design: Open, Convert to .c3d… (brief 6), Run in Hertzon (brief 8) and Simulate (brief 7). Items whose brief
has not landed are disabled and say so.

## Gates

- `ProjectTreeHznTests`:
  - a `.hzn` in a user folder and one added as a Known File both expand to their designs in file order
  - a version-3 file shows its refusal row
- `HznDocumentSourceTests`: opening a fixture design gives a `C3dDocument` equal to `HznToC3d.Translate`'s, and the
  source reports read-only.
- A refused design opens nothing and lists every item.
- UiSnapshot PNGs, listed in the completion note:
  - the expanded node, inside and outside the workspace
  - the refusal
  - an opened design in the 3D editor
  - the context menus
