# HZN-4 — export a `.c3d` to a new `.hzn`, or into an existing one

Series: `brief-hzn-0-overview.md`. Needs HZN-1 and HZN-2 (its tables, read the other way).

## Why

A structure drawn in circuitRF cannot be handed to Hertzon. This brief writes one, either as a project of its own or
as a new design added to a project that already exists.

## What to build

### R-hzn4-1 — `C3dToHzn.Write(C3dDocument, target) → HznDesign`

The inverse of brief 2's tables:

- Each `.c3d` object becomes a Hertzon object with a history: a creation step, then one step per wrapping operation,
  innermost first. A `C3dBoolean`'s tools become objects of their own, with `consumed_by` set.
- `Placement` becomes `Move`/`Rotate`/`Mirror` steps.
- Variables are written as design variables (overview D9), and expressions as expressions, in Hertzon's spelling
  (inline units).
- A DBU literal is written in the design's units, with enough digits to read back to the same DBU.
- Ports become rectangle sheets plus a Lumped or Wave Port on the sheet object, with an `IntegrationLine` between
  `Negative` and `Positive` when they are stated. (Built from the negative conductor to the positive one, because every
  line in Hertzon's examples runs ground to signal; `src/Design/RESOLVED.md` §HZN-4.)
- Face boundaries are written by object where they cover the whole object. Otherwise they are written by `{obj, face,
  sig}`, with the index and `sig` taken from the kernel's build of the written object, so Hertzon finds the face (gate
  2 proves it).
- The document's materials are written into the project's `materials` (D9's reuse and conflict rule), and setups are
  written from the `.c3d` setups.
- **Refused, listed (overview D3), where Hertzon has no equivalent:**
  - phantoms
  - voxel bodies
  - meshes
  - heat sources
  - probes
  - contact resistances
  - effective blocks
  - a cylindrical port
  - a thermal, electrostatic or magnetostatic setup
- Anything brief 2 refuses to read is never written, so every export reads back through brief 2.

### R-hzn4-2 — flattening (overview D8)

- **Instances** are written as ordinary objects named `<instance>/<object>` (nested instances: `<a>/<b>/<object>`),
  with the placement applied.
- **Geometry the `.c3d` builds from a layout** is written as built, with the current variable values.
- Both are listed as notes ("flattened: 3 instances, 41 objects; built from layout: U1").

### R-hzn4-3 — two targets

- **A new `.hzn`** holds one design named after the cell (or `--design-name`), project `name` = the file name, and
  `format`/`version` 2.
- **Into an existing `.hzn`**, through brief 1's `HznFile`, so every byte of the existing file is kept:
  - The design is appended.
  - A name collision is asked again in the GUI and refused naming `--design-name` on the CLI (D9).
  - The change-on-disk check applies.

### R-hzn4-4 — the GUI and the CLI

- **GUI:** the 3D editor's File ▸ Export ▸ Hertzon Project…. The dialog offers a new file or an existing one, with the
  design name; the picker follows the glyph rule (… for one file).
- **CLI:** `convert <c3d|cell> -o out.hzn [--design-name N]` writes a new file. `convert <c3d> --into existing.hzn
  [--design-name N]` adds a design. The format is inferred from the extension, and by content for `--into`. The
  `convert` gate's pair table gains the rows.

## Gates

- `C3dToHznTests`:
  - every brief-2 mapping row, written and read back through brief 2, gives an equal `C3dDocument`
  - the refusal fixture lists every item
  - the flatten notes are exact
- **Into an existing file:** every byte outside the appended design is unchanged, a name collision is refused, and a
  conflicting material is refused with its properties listed.
- **Hertzon reads it** (series gate 2): every written fixture passes `hertzon-cli describe`, with the expected ports
  and variables. Skipped without an install.
- The CLI as a process matches the in-process call byte for byte.
