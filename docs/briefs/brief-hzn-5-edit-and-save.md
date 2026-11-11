# HZN-5 — edit a `.hzn` design and save it back

Series: `brief-hzn-0-overview.md`. Needs HZN-3 and HZN-4.

## Why

After brief 3, a `.hzn` design opens read-only. Editing it in circuitRF, and saving it where Hertzon will open it
again, is the point of the series (owner decision).

## What to build

### R-hzn5-1 — the document source becomes writable

Brief 3's source accepts Save. **Save As** writes a native `.c3d` (brief 6's path). Undo and redo work as for any
`.c3d`; they act on the in-memory document and never on the file.

### R-hzn5-2 — rewrite only what changed

On save, walk brief 2's provenance map:

- **An unchanged `.c3d` item** leaves its `.hzn` origin untouched: the same bytes, through brief 1.
- **A changed item** is rewritten through brief 4's writer, in place, keeping the original object's name, colour,
  transparency, group, visibility and any keys not modelled.
  - A history whose shape can be kept is edited **step by step**. A changed box size changes `XSize` in the existing
    `CreateBox` step and nothing else, and an expression stays an expression.
  - A changed structure is rewritten as a new history only when no step-level edit expresses it. Examples: a boolean
    added, or a tool removed.
- **A new item** is appended as a new object. A deleted item removes its object.
  - A boundary or excitation left pointing at nothing is removed with it, and listed in the save note.
  - Hertzon-only content that names the deleted object (a report, a field plot, an optimization goal) is **not**
    edited. The save note lists each one, so the user knows what Hertzon will flag.
- **A suppressed step** is written back exactly as it was read.
- **Project variables** go back under their own names at project level (brief 2 R-hzn2-3).
- **Ports and face boundaries:** a moved or resized port rewrites its sheet object's creation step. A face boundary on
  a changed object gets a new `sig` from the kernel's build of the saved geometry; the index is kept only if it still
  names that face.

### R-hzn5-3 — when the mapping is lost, refuse

Brief 5 must refuse to save, with the reason, rather than rewrite the whole design, when:
- an edit cannot be traced to an origin
- an origin it would rewrite has a step brief 4 cannot write
- a face reference cannot be re-resolved

The user keeps the in-memory document and can Save As `.c3d`.

### R-hzn5-4 — concurrent change

- Save goes through brief 1's change-on-disk check. When the file has changed, the dialog offers:
  - **Reload**, discarding the in-memory edits, which are named first
  - **Save As .c3d**
  - **Cancel**
- **There is no merge.**
- Brief 1's backup applies per the owner's answer to Q1.

## Gates

- `HznSaveTests`, one per claim:
  - save with no edit is byte-identical
  - an edited box size changes exactly one property string
  - an expression edited stays an expression
  - an added object appends one object
  - a deleted object removes it and its port, and lists the report that names it
  - a suppressed step survives
  - project variables round-trip
- A save of an edited design reads back through brief 2 to the edited `C3dDocument`, and passes `hertzon-cli
  describe` (skipped without an install).
- A file changed on disk is refused, and the dialog is rendered (UiSnapshot).
- An untraceable edit refuses the save, and the in-memory document survives.
