# Brief 69 — STEP export

**Series:** [3D EM, fourth series](brief-em3d-60-overview.md) · **Tag:** `R-em3d69-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §6.3 (Tier A and Tier B), §6.3a (precedence); overview D8
(what is written), D9 (`convert`)
**Area:** `src/Design/ThreeD/Step/StepExport.cs` (new — the one function the GUI and `convert` call),
`tools/geometry-worker` (the `write-step` request), `src/Design/Layout/Em3d/Em3dLayoutSolids.cs` (called, not
changed), `src/Ui/ThreeD/` + `src/Ui/Layout/` (the Export STEP dialog), `src/Ui/Views/WorkspaceWindow.axaml` +
`src/Ui/Views/Shared/TornOffFileMenuView.axaml` (File ▸ Export ▸ STEP…), `src/Cli/LayoutConvert.cs`,
`docs/design/cli.md`, `tests/`
**Depends on:** 64 (elaboration to `Em3dProblem` with kernel solids), 63 · **Blocks:** 70; 68's round-trip gate

---

## 0. What this brief delivers

A mechanical colleague needs the RF model — a board with its connector, a package with its lid — in the CAD
tool they use. When this brief is done:

- **File ▸ Export ▸ STEP…** (and **3D ▸ Export STEP…**) writes the active `.c3d`, or the active **layout's 3D
  form**, as one STEP file.
- What is written is **the elaborated model** (D8): the solids the solver gets, named by their instance paths
  (`U1/pad_in`), coloured by their materials, in the document's units — so the file a colleague opens is the
  geometry that was simulated, not a second rendering of the document.
- **Flattened by default**; **As assembly** keeps each placed cell as a STEP sub-assembly, instanced, so a
  package holding sixteen identical dies writes the die once.
- **Headless:** `circuitrf convert <x.c3d | x.clay | cell> -o out.step` through the same function (D9).

The worker writes the file (OCCT's STEP writer with names and colours). `StepExport` decides what is in it.

---

## 1. `R-em3d69-1` — what is in the file

**`R-em3d69-1a` The source is `Em3dProblem`, after elaboration.** For a `.c3d`, `C3dElaborator` (brief 42, 64);
for a `.clay`, `Em3dLayoutSolids.From` — the Tier A "solids of a layout" path the generator and the `.c3d`
elaborator already share (brief 40 §1i). Nothing here re-derives geometry. Every primitive kind the problem can
state (`Em3dBox`, `Em3dExtrudedPolygon`, `Em3dCylinder`, `Em3dSweep`, `Em3dSphere`, `Em3dTruncatedSphere`,
`Em3dPolyhedron`, `Em3dShapeSolid`) is built into a B-rep by the worker from its resolved numbers.

**`R-em3d69-1b` Precedence is applied — the file holds disjoint solids.** Where a metal overlaps a dielectric, the
dielectric loses the metal's volume (`Em3dPrecedence`, em-3d.md §6.3a), exactly as the `.geo` script's cut order
does. A via then appears as a hole through the substrate with the via inside it, which is physically true, and
mass properties in the receiving tool are right. **As drawn (overlapping)** is an option for the colleague who
wants design intent; it is off by default and the file's header description says which was written.

**`R-em3d69-1c` Sheets** (zero-thickness metal, ports excluded) are written as **faces** — STEP shell-based
surface models — named and coloured like solids. Option **Thicken sheets** writes each as a thin solid of the
thickness the problem states for it (its metal's thickness), for tools that drop surfaces. Default off: a surface
is what the solver gets.

**`R-em3d69-1d` Excluded, always:** ports (a port is an excitation, not geometry), face boundaries, setups and
construction polylines. Objects with **no material** are excluded, because they are not in the problem (em-3d.md
§6.4), and the export note counts them. A **hidden** object is **written**: it is still in the problem, and
hiding is a view state.

**`R-em3d69-1e` The air box: an option, off by default.** It is a solver construct, not a part; a colleague
checking clearances to a housing sometimes wants it, so **Include air box** writes it as one solid named
`airbox`, uncoloured. Decided here rather than left open: excluded by default, one tick to include.

**`R-em3d69-1f` A layout's dielectric slabs are bounded** exactly as a layout instance's are inside a `.c3d`
(brief 40 §1i): by the board outline, else by the drawn geometry's bounding box. The note says which, as the
elaboration note already does. An infinite slab cannot be written.

## 2. `R-em3d69-2` — names, colours, structure

**`R-em3d69-2a` Names.** Each STEP product is named by the solid's problem name — the instance path included —
so a face the colleague asks about can be found in circuitRF by name.

**`R-em3d69-2b` Colours** from each material's `Color` in the technology (and its `.cmat` libraries). A material
with no colour writes no colour; the receiving tool's default is honest. Dielectrics are written with the same
alpha the 3D view gives them where the schema can carry it.

**`R-em3d69-2c` Flattened (default):** one assembly, one product per solid, no nesting. **As assembly:** the
top document is the root, each `C3dInstance` a sub-assembly instanced with its placement, each instanced child
written once. Arrays write one instance per element. A layout instance's solids form one sub-assembly named by
the layout cell. Precedence (§1b) is applied **within** each sub-assembly and not across them in this mode —
across-instance cuts would make each instance a different shape, which is not an assembly; the dialog says so
beside the option.

## 3. `R-em3d69-3` — units and schema

**`R-em3d69-3a` Units follow the document's display unit, mapped to the two units every receiving tool reads:**
nm, µm and mm write **millimetres**; mil and inch write **inches**. The numbers change, the geometry does not,
and a receiving tool that does not know a micron or a mil — several do not — still opens the file. A `.clay`
exports in its own display unit by the same map.

**`R-em3d69-3b` Schema: AP214 by default.** AP214 carries everything this export writes — geometry, product
names, assembly structure and colours — and is the schema the widest range of tools read. AP242 adds
product-manufacturing information circuitRF does not have. `--schema ap242` and a combo in the dialog write it
for the colleague whose tool prefers it.

**`R-em3d69-3c` The header carries no user and no path.** `FILE_NAME`'s author and organisation are empty; the
name field is the output's **file name**, not its path; the originating system is `circuitRF <VERSION>` (from
the repo-root `VERSION` file through `AppVersion`, never a second literal). A STEP file travels — to a
colleague, into a supplier's tracker — and must not carry a login name or a home directory.

## 4. `R-em3d69-4` — the dialog and the menus

**`R-em3d69-4a`** A save dialog, then one small options dialog: *Flattened / As assembly*, *Precedence applied
/ As drawn*, *Thicken sheets*, *Include air box*, *Schema*. The options are remembered per session. The summary
line states solid, face and sheet counts before writing.

**`R-em3d69-4b` Menus:** *File ▸ Export ▸ STEP…* in all three places the File menu lives (the native menu, the
in-window menu, and the torn-off File menu, which must stay identical — `TornOffFileMenuView.axaml`'s own rule),
enabled when a `.c3d` or a `.clay` is active, with the Design menu's tooltip pattern (*"Requires an active 3D or
layout document"*). With the kernel absent it is disabled with the capability's tooltip (overview §1d).

**`R-em3d69-4c`** Export runs on `RunControl`: progress, **Cancel**, and a cancelled export **writes nothing**
(a temporary file renamed into place on success, the rule `render` follows).

**`R-em3d69-4d`** An export of a `.c3d` holding a disabled boolean or fillet writes what elaboration gives —
the operands, or the unrounded edge (overview §1f). The note says an operation was disabled.

## 5. `R-em3d69-5` — headless: `convert … -o out.step` (D9)

**`R-em3d69-5a`** `LayoutConvert`'s target formats gain `step` (`.step`, `.stp`). Legal sources: `.c3d`, `.clay`,
a cell folder (its primary 3D view, else its primary layout — `CellFolder.ResolvePrimary`; a cell with both is a
refusal **listing the two views** and naming `--view 3d|layout`, the rule `render` follows for a cell of several
views), and every layout interchange source (GDSII, DXF, Gerber, board), which is imported into a scratch cell as
today and then exported — so `convert board.kicad_pcb -o board.step` works in one line.

**`R-em3d69-5b` The verb calls `StepExport.Export` and nothing else**; flags mirror the dialog: `--assembly`,
`--as-drawn`, `--thicken-sheets`, `--include-airbox`, `--schema ap214|ap242`. An unknown flag is a refusal
(cli.md §3.3).

**`R-em3d69-5c` Refusals, each naming its remedy:**
- a `.clay` whose technology has no stackup for a drawn layer — `Em3dLayoutSolids.From`'s own refusal,
  verbatim, not reworded;
- a `.clay` with no resolvable technology — refused, naming `--tech`; never an empty technology here, because an
  export with no stackup has no z at all (the opposite of brief 68 §3d, where an empty technology is honest);
- a `.c3d` whose every object has no material (*"nothing to export"*);
- the kernel absent — the capability's sentence, exit 1;
- an interchange source whose own import refuses — that refusal, unchanged.

**`R-em3d69-5d`** stdout is the written path; `--json` records it as an output of kind `step`. `docs/design/cli.md`
gains the pair in the `convert` section.

## 6. Gates

`tests/Ui.Tests/ThreeD/StepExportTests.cs`, `tests/Ui.Tests/ThreeD/StepConvertCliTests.cs`. Worker-dependent
tests are `[KernelFact]`.

1. **CLI = in-process, byte for byte, bar one field.** `convert x.c3d -o x.step` run **as a process** (the
   `Engine.Tests` pattern cli.md §8.6 records: the DLL exec'd directly, both pipes drained) writes the same bytes
   as `StepExport.Export` in-process — every line except `FILE_NAME`'s time-stamp field, which the file carries
   by design, exactly as `em`'s gate exempts the provenance write-timestamp. Also for a `.clay` and for a cell
   folder. **If OCCT's writer proves not to be byte-deterministic** in entity order across processes, that is a
   finding for `src/Design/RESOLVED.md`, and the gate falls back to comparing the canonical re-read (gate 2's
   quantities) — stated in the test's comment, not hidden.
2. **Round trip.** Export then import (brief 68) gives the **same solid count, the same names, and each solid's
   volume within the kernel's tolerance** (relative 1e-6), for: a flattened `.c3d` with a boolean and a fillet;
   the same *As assembly*; the 3D Package example's package cell; a layout.
3. **Precedence:** a dielectric box drawn after a metal box that it swallows exports with the metal's volume
   removed from the dielectric; *As drawn* exports both whole.
4. **Exclusions:** ports, polylines and material-less objects are absent; the air box appears only with the
   option; a hidden object is present.
5. **Units:** a mil document exports in inches, a µm one in millimetres; the re-imported extents agree in DBU.
6. **Header privacy:** the file contains neither the machine's user name nor any directory of the output path.
7. **Cancel** leaves no file at the target.
8. **No logic in the verb:** a comment-stripped source scan finds no call into the worker, and no STEP text, in
   `src/Cli`.

## 7. Owner check (Debug build)

1. Open the 3D Package example; *File ▸ Export ▸ STEP…*, defaults; open the file in any STEP viewer; check the
   names and colours.
2. Export again *As assembly*; check the die appears once, instanced.
3. Open a board layout; *Export STEP…*; check the substrate is bounded by the outline.
4. Hand one file to someone who uses a mechanical CAD tool and ask whether it opened cleanly.

Pixels were not seen by the agent that built this brief; the completion note says so.

## 8. Scope

- **Geometry only.** No schematic, no results, no fields, no product-manufacturing information.
- **No new verb** — `convert` gains a format (D9).
- **One writer:** the worker's. No managed STEP text is generated.
- **No vendor names** in fixtures, tests, notes or docs, including the names of the tools the file is meant for.
- Findings go in `src/Design/RESOLVED.md` and `src/Cli/RESOLVED.md`, never `CLAUDE.md`.
