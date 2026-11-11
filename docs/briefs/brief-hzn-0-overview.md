# Brief series HZN — Hertzon `.hzn` projects: open, edit, convert, place and simulate

**Goal.** A Hertzon project (`.hzn`) is a first-class document in circuitRF:

- It appears in the Project Tree, from inside the workspace or from anywhere else as a Known File.
- It expands to show its designs.
- Double-clicking a 3D design opens it in the 3D editor as a `.c3d` document, held in memory.
- Edits save back into the `.hzn`.
- A design converts to a native `.c3d`, and a `.c3d` exports to a new `.hzn` or into an existing one.
- A `.hzn` design can be placed as an instance inside a `.c3d`.
- A `.hzn` design runs either on circuitRF's own EMerge driver or in Hertzon itself, through `hertzon-cli`.

**Why.** Hertzon is an open-source 3D full-wave FEM suite whose solver core is EMerge, which circuitRF already drives.
Today a 3D design made in either tool cannot reach the other:
- **Nothing in circuitRF reads or writes `.hzn`**, so a structure drawn in Hertzon is redrawn by hand, and so is one
  going the other way.
- **circuitRF users have one 3D front end.** Hertzon offers a second one, supported independently, with its own
  adaptive meshing, field post-processing and optimizers. Reaching it needs nothing more than its file format and its
  command line.
- **A design kept in a `.hzn` cannot take part in a circuitRF assembly**, so a structure that belongs inside a larger
  `.c3d` has to be rebuilt inside it.

**There is deliberately no schematic block for a `.hzn` design** (owner decision). A `.hzn` design reaches a circuit
the way any 3D result does: through the Touchstone file its run writes.

## The format, as far as it is known

A `.hzn` file is UTF-8 JSON. Everything below was established from Hertzon's documentation, its example files and
black-box runs of an installed Hertzon (0.3.19):

- **The top level holds:**
  - `format` (`"hertzon-project"`) and `version` (`2`; Hertzon converts version 1 files when it opens them)
  - `name`
  - project `variables`, which `hertzon-cli` reports with a leading `$`
  - project `materials`
  - `designs`
- **A design** has a `name` and an optional `kind`. `"circuit"` is a schematic with `components` and `wires`; anything
  else is a 3D design. A 3D design holds `units`, `settings`, `variables`, `objects`, `boundaries`, `excitations`,
  `mesh_ops`, `setups` (each with `sweeps`), `coord_systems` and `working_cs`. It also holds post-processing that
  circuitRF does not model: `reports`, `field_plots`, `spheres`, `field_exprs`, `field_funcs`, `optimetrics`,
  `parametrics` and `notes`.
- **An object** is a named **modelling history**: an ordered list of `{kind, props, suppressed}` steps. The first step
  creates a shape (`CreateBox`, `CreateRectangle`, `CreatePolyline`, …), and later steps change it (`Subtract`,
  `Unite`, `Chamfer`, `SweepAlongVector`, `Move`, …). A boolean names its tool objects. A consumed tool records the
  consumer in `consumed_by`. Every property is a string expression; a position is three comma-separated expressions,
  units are written inline (`1.524mm`, `2.45GHz`), and a bare number is in the design's units.
- **Boundaries and excitations** apply to whole `objects`, or to `faces`, `edges` and vertices by index. A face is
  given as `{obj, face, sig}`, where `sig` appears to be the face's centroid, normal and area (in metres), recorded so
  the face can be found again.
- **No written specification exists yet.** It has been requested in Hertzon's Discussion #406. Until it is published,
  each brief records every property meaning it relies on, and the experiment that established it, in
  `src/Design/RESOLVED.md` §HZN.

## The briefs, in order

| Brief | What | Depends on |
|---|---|---|
| `brief-hzn-1-file-model.md` | Read and write a `.hzn` without losing anything; version gate; change-on-disk detection. No UI | — |
| `brief-hzn-2-design-to-c3d.md` | A 3D design → an in-memory `C3dDocument`: shapes, operations, variables, materials, ports, boundaries, setups. The full refusal list. `check`/`explain`/`find` read `.hzn` | 1 |
| `brief-hzn-3-project-tree.md` | The `.hzn` node, Known Files, designs as children, double-click opens one in the 3D editor | 2 |
| `brief-hzn-4-c3d-to-hzn.md` | `.c3d` → a new `.hzn`, or a new design in an existing `.hzn`; `convert` | 1, 2 |
| `brief-hzn-5-edit-and-save.md` | Edit an opened `.hzn` design and save it back, rewriting only what changed | 3, 4 |
| `brief-hzn-6-convert-hzn-to-c3d.md` | A `.hzn` design → a native `.c3d` in a cell; File menu and `convert` | 2 |
| `brief-hzn-7-run-on-emerge.md` | Run an opened `.hzn` design on circuitRF's EMerge driver, its setups translated | 2, 3 |
| `brief-hzn-8-run-in-hertzon.md` | Run in Hertzon: find `hertzon-cli`, `describe`, `solve`, progress, results | 3 |
| `brief-hzn-9-instance.md` | A `.hzn` design placed as an instance in a `.c3d` | 2 |

Brief 6 can run beside 3–5. Brief 7 and brief 8 are independent of each other, and so is brief 9 once brief 2 is in.

## Decisions already made

- **D1 — Clean room.** Hertzon is GPL-2.0-or-later, and circuitRF is MIT. **No code is ported, translated or copied
  from Hertzon's source**, and no brief reads `hertzon/` to learn what a property means. The reader and writer are
  written from the files themselves, Hertzon's documentation, Discussion #406 once it is answered, and black-box
  experiments with an installed Hertzon (`hertzon-cli describe`, `solve`, the rendered model). Running Hertzon as a
  separate program is fine; that is how brief 8 works.
- **D2 — No Hertzon file is committed.** Test fixtures are small `.hzn` files written for the purpose, in
  `tests/Ui.Tests/Hzn/Fixtures/` or built in code. A test that needs Hertzon's own examples or an installed
  Hertzon reads its location from `CIRCUITRF_HERTZON_HOME`, and **reports Skipped with a reason** when that is unset,
  as the RfCore fixture tests do.
- **D3 — A design circuitRF cannot represent is refused, never approximated.** The refusal lists **every** unsupported
  item at once, grouped by kind, with the objects that use each:
  - a creation step
  - an operation
  - a reference by face or vertex index that cannot be resolved
  - a boundary or excitation kind
  - a material
  - an expression function
  Nothing opens half a design.
- **D4 — Nothing circuitRF does not model is lost.** Everything the translation does not read is kept: other designs,
  reports, optimizations, field plots, mesh operations, settings and keys it does not recognise. It is written back
  exactly as it was read (brief 1). A save rewrites only what was edited (brief 5).
- **D5 — Expressions stay expressions.** A dimension written `Lp+50mm` opens as that expression and saves as that
  expression. A literal opens in DBU only when no expression is involved, and an untouched literal saves as its
  original text, so opening and saving changes no byte that was not edited.
- **D6 — Materials come from the `.hzn`.** While a design is open, its project's materials are the document's own
  materials. They save back into the `.hzn`, and the workspace technology is never written. A material named but not
  defined in the project comes from Hertzon's built-in library. Brief 2 resolves a small table of those circuitRF can
  state exactly; any other is refused by name (D3).
- **D7 — Two run paths, both offered** (owner decision): circuitRF's EMerge driver (brief 7), and Hertzon itself
  through `hertzon-cli` (brief 8). They will not agree bit for bit, because they mesh differently and run different
  EMerge builds. Agreement within a stated tolerance on the same design is a gate, not an assumption (brief 7).
- **D8 — No hierarchy is written into `.hzn` until Hertzon has one.** An export flattens instances into ordinary
  objects named `<instance>/<object>`, and geometry that a `.c3d` builds from a layout is written as built, with the
  current values. Both are reported as notes listing what was flattened (owner decision).
- **D9 — Adding a design to an existing `.hzn` never overwrites anything.**
  - A design name already in the file is asked again in the GUI, and refused naming `--design-name` on the CLI.
  - Our variables are written at design level, never as project variables.
  - A material with the same name and identical properties is reused. One with the same name and different
    properties is a refusal listing each conflict.
- **D10 — The code lives in `src/Design/Interchange/Hzn/`** (reading, writing and translating; nothing there draws) and
  `src/Ui` (the tree node, the editor's document source, dialogs). `tests/Firewall.Tests` holds the first. The CLI
  verbs gain the format through their existing seams (`convert`, `check`, `explain`, `find`, `em`) and hold no logic
  of their own.

## Open questions for the owner

- **Q1 — A `.hzn` outside any workspace has no revision history**, so saving into it from circuitRF is the only copy.
  Recommendation: the first save in a session keeps the file as it was beside it, as `<name>.hzn.bak`, overwriting the
  previous backup.
- **Q2 — Run a native `.c3d` in Hertzon?** Brief 4's export makes this cheap: export to a scratch `.hzn`, then run
  brief 8. Recommendation: yes, as a later addition to brief 8, once the export round trip is proven.
- **Q3 — Hertzon's built-in library materials.** Brief 2 states a few exactly (`pec`, `vacuum`, `air`, `copper`). The
  rest need their values from Hertzon. Recommendation: ask whether `hertzon-cli describe` could list the materials a
  design uses, with their properties. Any such request is drafted and approved before it is posted.

## Risk, stated before the series starts

- **Brief 2 is the hard one: medium to high.** Hertzon objects are histories whose operations can refer to faces,
  edges and vertices by index, and circuitRF never stores an index (`docs/design/em-3d.md` §6.4). Index references are
  resolved by geometry, matching each `sig` against the faces the geometry kernel builds, and need the OpenCASCADE
  worker for anything past a primitive. Brief 2's own gate decides how much of Hertzon's example set opens. A low
  number is a finding to report, not a reason to approximate (D3).
- **Brief 5: medium.** Rewriting only what changed needs a reliable record of which `.hzn` object each `.c3d` item came
  from, through edits that rename, regroup and delete. The rule when a mapping is lost is to refuse the save and say
  why, never to rewrite the whole design.
- **The format is not ours and will change.** Every brief reads `version` and refuses a newer one by name. A change
  in Hertzon that breaks a brief's assumptions shows up as a failing test against the files under
  `CIRCUITRF_HERTZON_HOME`, not as a silent misreading.
- **Briefs 1, 3, 4, 6, 8, 9: low to medium.** Each has a working precedent: `convert`'s import/export seam, Known
  Files, the `.c3d` writer, solver discovery, and `.c3d` instances.

## Gates, for the whole series

1. **Round trip, byte for byte.** Opening and saving with no edit writes back the same bytes for every fixture, and for
   every file under `CIRCUITRF_HERTZON_HOME` that opens.
2. **Hertzon reads what we write.** Every `.hzn` that brief 4 or brief 5 writes passes `hertzon-cli describe` and
   lists the expected designs, ports and variables. Where the installed Hertzon offers a geometry check, it passes
   that too. Skipped without an install (D2).
3. **The refusal is complete.** A fixture holding one of every unsupported item is refused with every item listed,
   and nothing opens.
4. **Two solvers, one answer.** Brief 7's run and brief 8's run of the same small design agree within the tolerance
   brief 7 measures and records.

## Rules that apply throughout

- **Tests:** one per claim, named classes only. Never all of `Ui.Tests`, never the full suite. No new
  `Category=Benchmark` test and no timing assertion. No EM solve in a routine test. A test that runs Hertzon or EMerge
  is skipped without an install, and is kept to seconds.
- **Every UI piece is rendered with `tools/UiSnapshot` and looked at**, and the PNGs are listed in the completion note.
- **No commercial tool or PDK names**, and no names of companies or products that appear in Hertzon's own example
  files or part libraries. Hertzon and EMerge are named because the feature is about them.
- **Clean room (D1)** in code, comments, tests and `RESOLVED.md`: describe what a property means, never how Hertzon's
  code handles it.
- Findings go in `src/Design/RESOLVED.md` §HZN (or the relevant sibling), never `CLAUDE.md`.
