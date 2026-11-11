# HZN-9 — a `.hzn` design placed as an instance in a `.c3d`

Series: `brief-hzn-0-overview.md`. Needs HZN-2.

## Why

A structure kept in a `.hzn` — a connector, a launch, a radiator — belongs inside a larger circuitRF assembly. Placing
it by reference keeps the `.hzn` as the one copy, so an edit there reaches every assembly that uses it.

## What to build

### R-hzn9-1 — the reference

- `C3dInstance` gains a way to name a design inside a `.hzn`. The file path is spelled as `CellRef` is spelled today:
  relative to the `.c3d`'s folder, or `ws://`. A new key `Design` holds the design name, and `View` gets a value
  `Hertzon`.
- Every walker that follows a `CellRef` must handle this one, or refuse it by name. That includes `check`, `find`,
  `explain --ref`, revision-control pins and the copy and move of a cell. List them in the completion note.
- **Resolution:** `HznToC3d.Translate` (brief 2) gives the placed geometry. A design that would be refused when opened
  is refused as an instance, with brief 2's list, naming the instance.
- **Parameters:** the instance may override the design's variables (`Params`, as for any `.c3d` instance), each
  evaluated in the parent's scope (`expressions.md` §9). An override naming a variable the design does not have is
  refused.
- **Materials:** the placed design's materials are matched by name against the parent's technology, with
  brief 6's rule: identical → shared; missing or conflicting → a refusal listing them. Placing never
  writes the technology.
- **Ports in the placed design** are offered the way a placed cell's are today (the component-ports rules). Nothing new
  is invented here.
- **Caching:** the translation is cached against the `.hzn`'s modification time and hash. An edit to the `.hzn` (in
  Hertzon or by brief 5) shows the next time the parent elaborates.

### R-hzn9-2 — placing it

- **Drag a 3D design** from the Project Tree onto an open `.c3d`, or use Place ▸ Instance… with the picker listing
  `.hzn` designs beside cells.
- **The placed instance draws like any instance.** Double-clicking it opens the design (brief 3).
- **Export to `.hzn`** (brief 4) flattens it like any instance (overview D8).

## Gates

- `HznInstanceTests`:
  - a placed fixture design elaborates to the same solids as brief 2's document, moved by the placement
  - an override takes effect
  - an unknown override is refused
  - a refused design is refused as an instance, with the list
  - a changed `.hzn` is picked up on the next elaboration
  - a material conflict is refused
- `check` on the parent `.c3d` follows the reference, and reports a missing file or design as a broken reference.
- UiSnapshot: the placed instance in the 3D view, and the picker listing `.hzn` designs.
