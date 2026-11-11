# HZN-1 — the `.hzn` file model: read and write without losing anything

Series: `brief-hzn-0-overview.md`. No dependencies. No UI.

## Why

Every later brief reads a `.hzn`, and briefs 4 and 5 write one. Writing back a file circuitRF only partly understands
is safe only if the parts it does not understand come back unchanged. Those parts are reports, optimizations, field
plots, circuit designs, and keys added by a newer Hertzon. This brief makes that guarantee in one place, so no later
brief has to think about it.

## What to build

### R-hzn1-1 — `HznFile`: a lossless JSON model

`src/Design/Interchange/Hzn/HznFile.cs`:

- **Read** a `.hzn` into a tree that keeps every key, every key order, every number's original text and every string
  exactly.
  - `System.Text.Json`'s `JsonNode` keeps order but normalises numbers on write. Either keep the raw token text for
    numbers, or keep the original bytes of each untouched subtree.
  - The test is that write(read(f)) == f, byte for byte, including whitespace and indentation, for every fixture.
- **Typed views over that tree, not a second copy:** `HznProject` (name, variables, materials, designs) and
  `HznDesign`.
  - `HznDesign` exposes `Name`, `Kind` (`ThreeD` or `Circuit`), `Units`, `Variables`, `Objects`, `Boundaries`,
    `Excitations`, `Setups` and `CoordSystems`.
  - `HznObject` exposes `Name`, `Material`, `ConsumedBy`, `Group`, `Visible`, `Model`, `SolveInside` and `History`.
  - `HznStep` exposes `Kind`, `Props` and `Suppressed`.
  - A view reads from and writes to the tree. Anything it does not expose stays in the tree untouched.
- **Write**, preserving the original formatting of every subtree that was not changed. A changed subtree is written in
  the style the file already uses: indentation width and key order of its siblings.

### R-hzn1-2 — the version gate

- `format` must be `hertzon-project`; anything else is refused as "not a Hertzon project".
- `version` 2 is read. A higher version is refused by name ("written by a newer Hertzon, format version 3; circuitRF
  reads version 2"). Version 1 is refused too, naming the remedy: open and save it once in Hertzon.
- A write keeps the version it read. circuitRF never raises it.

### R-hzn1-3 — change on disk

`HznFile` records the file's length, modification time and a SHA-256 of its bytes when it reads. Before any write,
`HznFile.Save` compares them against the file on disk. If they differ, it refuses with "changed on disk since it was
opened (Hertzon may have saved it)". It then offers no merge; a reload is the caller's decision (brief 5). The write is
atomic: a temp file in the same folder, then a replace.

### R-hzn1-4 — the backup (owner question Q1)

Build the mechanism behind a flag; the default waits for the owner's answer. When it is on, the first `Save` of an
`HznFile` instance in a process copies the original bytes to `<name>.hzn.bak` first.

### R-hzn1-5 — fixtures

`tests/Ui.Tests/Hzn/Fixtures/`: small files written by hand, in the shape the overview describes.

- one 3D design with a box and a lumped port
- two designs in one project
- a circuit design next to a 3D design
- unknown keys at every level
- unusual number spellings (`1e-3`, `0.50`, `-0`)
- non-ASCII names

None is copied from Hertzon (overview D2).

## Gates

- `HznFileTests`: byte-identical round trip for every fixture, and for every `.hzn` under `CIRCUITRF_HERTZON_HOME`
  (Skipped without it).
- A typed edit (rename a variable's value) changes exactly that string, and the rest of the file is byte-identical.
- The version gate refuses `format` ≠ `hertzon-project`, version 1 and version 3, each with its sentence.
- A file changed between read and save is refused, and the file on disk is left untouched.
- `tests/Firewall.Tests` covers `src/Design/Interchange/Hzn/`.

Record in `src/Design/RESOLVED.md` §HZN: which JSON strategy preserved the bytes, and why the alternatives did not.
