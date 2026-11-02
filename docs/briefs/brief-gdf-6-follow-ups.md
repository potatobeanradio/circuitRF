# Brief 6 — follow-ups: kits loaded on demand, one import path, two small gaps

**Series:** [gdsfactory kits](brief-gdf-0-overview.md) · **Tag:** `R-gdf6-n` · **Phase:** build
**Area:** `src/Design/Schematic/` (`PdkKitRegistry`, `PdkWorkspaceImport`, `NetExtractor.RouteCardDevice`),
`src/Cli/` (`WorkspaceKits`, `CircuitSource`, `ImportPdk`, `GeneratedCells`), `src/Ui/ViewModels/WorkspaceViewModel.cs`
(the Import PDK command only), `src/Ui/Schematic/KitArchive.cs`, `docs/design/cli.md`, `docs/design/pdk-import.md`
**Depends on:** 1–5, and the post-series review fixes (2026-10-30)
**Rule:** overview §0. No kit, foundry, process name, device name or model name in the repo. Fixtures are synthetic.

---

## 0. Why

A review of briefs 1–5 fixed what it found, except for four items that each need more than a local change.
Three are defects; one is an inconvenience.

1. **Every headless run in a workspace loads every kit that workspace references**, whether the design uses
   any of them or not. `WorkspaceKits.EnsureLoadedFor` calls `PdkWorkspaceImport.LoadReferenced`, which reads
   and installs every `.cws` kit reference before the extraction starts. On the largest kit measured in
   brief 1 that is about 7–8 s added to every `circuitrf sparam`, `dc`, `netlist` or `check` of a design that
   uses none of its parts. It cannot simply be skipped: a top schematic with no kit part can reach kit parts
   through its child cells, and only the extraction finds out.
2. **File ▸ Import PDK and `circuitrf import pdk` run two different sequences.** The CLI calls
   `PdkWorkspaceImport.Import`; the GUI calls `Read`, `Install` and `Commit` itself, with progress stages in
   between. They agree today only because both were checked by hand. The byte-identity gate compares the CLI
   against `Import()`, not against what the GUI does, so a change to either side diverges silently, which is
   the failure root `CLAUDE.md` describes for a verb that re-implements a view model's operation. Two
   concrete differences already exist:
   - **A folder that contains a gdsfactory kit's source tree anywhere inside it** is routed to the
     gdsfactory import (`GdsfactoryKitDiscovery.FindSourceTree`), in both the GUI and the CLI, and the
     ordinary import of that folder (its symbols, model libraries, corners) is **skipped with nothing said**.
     An upstream kit tree that also carries its gdsfactory package is exactly this shape.
   - **An archive** is unpacked and imported by the GUI (`KitArchive`); the CLI refuses it with "unpack an
     archive first".
3. **The "compile them" advice is given for any card type with no built-in**, whether the kit ships a
   Verilog-A source for it or not (`NetExtractor.RouteCardDevice`). R-gdf1b-3 keeps that advice only for a
   type the kit actually ships source for. The extraction cannot tell today: the import report's list of
   Verilog-A sources never reaches `PdkKitRegistry`.
4. **`--python` names one interpreter for every gdsfactory kit in the run** (`GeneratedCells.PythonOverride`).
   A design using two kits installed in two environments cannot be run headlessly without first recording a
   per-user choice at the keyboard, which a build machine has no keyboard for.

## 1. Decisions (defaults stand unless the owner changes them before the build starts)

- **D1 — load on demand at the registry, not in the extractor.** A kit is loaded the first time anything asks
  `PdkKitRegistry` for one of its parts. That one seam serves `.csch` extraction, a `.cnl` that names a kit
  part, and anything added later, and it keeps the "only the extraction knows" fact where it already lives.
  *Rejected:* scanning the design's cells for kit references before extraction. That is a second walk of
  the hierarchy that must agree with the extraction's, which is the divergence item 2 is about.
- **D2 — a folder holding both a kit tree and a gdsfactory package imports both**, in that order: the
  ordinary import, then the gdsfactory import of the package it contains, and the report says that two
  things were imported and from where. *Rejected:* asking which one. Both are the kit, and a headless run
  cannot ask.
- **D3 — `--python` takes an optional module**: `--python <interp>` keeps today's meaning (every module),
  `--python <module>=<interp>` names one, and the flag repeats. A module-specific entry beats the bare one.
  `=` cannot occur in a module name, so the two forms are unambiguous.
- **D4 — the CLI accepts an archive**, through the same `KitArchive` code the GUI uses (CLI verbs are not
  restricted by the UI). It is moved below the firewall to `src/Design` if it still references no UI type.

## 2. Requirements

### `R-gdf6-1`: kits load when a part of theirs is first asked for

- `PdkKitRegistry` gains a per-workspace **loader**: given a kit's provider name, load that one kit from the
  workspace's `.cws` reference (the work `LoadReferenced` does today, for one reference) and register it.
  The registry calls it on a miss for a provider the workspace references and has not yet loaded. Called at
  most once per provider per process, under the registry's own lock. A load that fails is remembered as
  failed, so the miss is not retried for every instance.
- `WorkspaceKits.EnsureLoadedFor` installs the loader instead of loading everything. Its warnings
  (`KitNotLoaded`) are raised by the loader at the moment a kit is actually needed, so a run that uses no
  kit says nothing about kits.
- The device-worker resolver a kit brings (`DeviceWorkerProviderResolver`) is registered by the same load.
- **The GUI keeps loading at workspace open.** The palette needs every kit's parts whether or not a design
  uses them. This requirement is headless only, and says so in `WorkspaceKits`' header.
- `check` and `explain` follow from the same seam, with no change of their own.

**Gate.** A counting seam on the one-kit load. A workspace referencing two kits:
- a design using neither loads **0**;
- a design whose *child cell* places a part of one loads **exactly that one**;
- a `.cnl` naming a part of the other loads **exactly that one**;
- a referenced kit whose folder is missing is reported only by a run that needs it.

Assert counts, never timings.

### `R-gdf6-2`: one Import PDK, called by both

- `PdkWorkspaceImport.Import` takes an optional progress callback (stage name, indeterminate), and the GUI's
  Import PDK command calls it, from a worker thread, instead of `Read`/`Install`/`Commit`. Whatever the GUI
  does after an import, such as opening the report dialog, offering the technology, or adding the kit to the
  palette, is done on the result `Import` returns. What `Import` itself writes is the same for both callers.
- Folder routing moves inside the shared function as well, so it is decided once (D2). The result carries
  both parts when a folder held both, and each caller reports each part.
- The CLI accepts an archive through `KitArchive` (D4), with the unpack location reported on stdout and in
  `--json` as a created path.
- `ImportedPdks`, the palette refresh and the report dialog stay in the view model; they consume the result,
  they do not compute it.

**Gate.**
- The existing byte-identity test is extended: the `.cws` and every declaration written by the GUI's command
  path, driven headlessly, equal those written by `circuitrf import pdk` on the same synthetic kit.
- A comment-stripped source scan proves `WorkspaceViewModel` no longer calls `PdkWorkspaceImport.Read`,
  `.Install` or `.Commit` directly, which is the `AuthoringCliVerbTests` pattern.
- A synthetic kit tree that also contains a synthetic gdsfactory package imports both from the CLI. The
  gated tier B variant runs the gdsfactory half; the routine tier asserts that the ordinary half is no longer
  skipped.
- An archive of the synthetic kit imports from the CLI and writes the same `.cws` as the unpacked folder,
  apart from the recorded location.

**Look at it.** Render the import report for the two-part folder with `tools/UiSnapshot` and check that it
says both things happened. Light and dark.

### `R-gdf6-3`: compile advice only where the kit ships the source

- The kit's Verilog-A source list (module names, from the import report) is carried into
  `PdkKitRegistry` with the kit's parts.
- `RouteCardDevice`'s no-built-in refusal says one of two things:
  - **The kit ships a source implementing this type**: today's sentence, naming that source file.
  - **It does not**: "no compiled model, no source in the kit and no built-in for '<type>'". There is no advice
    to compile anything.

**Gate:** one synthetic kit with a `.va` declaring the module and one without; each gets its own sentence.

### `R-gdf6-4`: `--python` per module

- D3's syntax, parsed in `GeneratedCells.TakeTrustFlags`. `InterpreterFor(module)` returns the module's entry,
  else the bare one, else the recorded per-user choice.
- `--python <module>=<interp>` naming a module no kit in the run uses is a **warning**, not a refusal. A
  script that passes a flag for a kit it sometimes does not use must not fail.
- `docs/design/cli.md` and the gdsfactory user page document both forms.

**Gate:** two synthetic modules, two interpreters by path. Each kit's worker is launched with its own
interpreter (the launch seam `GdsfactoryAdapterTests` already uses). A bare `--python` still applies to both.

### `R-gdf6-5`: a kit name taken from a folder is checked too

`PdkWorkspaceImport.KitNameProblem` refuses `--name` values that would split a part reference. A name derived
from the folder (no `--name`) is not checked, and a folder can be called anything. Apply the same check to
the derived name. On a problem, refuse and name `--name` as the way to give one. In the GUI, the report says
so and nothing is recorded.

**Gate:** a folder named with a `|` is refused from the CLI, and the refusal mentions `--name`.

### `R-gdf6-6`: a kit cell must not depend on what the worker built before it

Found by re-running the acceptance matrix after the review fixes (2026-10-30). The review's harness fix
builds each reference in a clean child process. That exposed failures which had been there all along, hidden
because the old harness built its reference in a process it had already used:

- **gdsfactory keeps every cell in one process-wide layout, by name**, and circuitRF's worker is one
  long-lived process that also builds every cell at its defaults while describing the kit. Two
  consequences, both reproduced in a plain gdsfactory session with no circuitRF involved:
  - **A cell built a second time with other parameters raises.** The error is "Cellname <x> already exists
    in the layout" and comes from a fixed-name helper cell. It cost kit D 5 sets and the generic kit 3. In
    the application this is a designer changing a parameter on such a cell. Deleting the named cell from
    the layout and building again succeeds.
  - **A fixed-GDS cell comes back with no ports** when another cell has already imported a GDS file holding
    a cell of the same name. The import reuses the existing cell and skips the kit's own add-ports step. It
    cost kit C 2 standard cells: their geometry is identical but both pins are missing. Clearing
    gdsfactory's cache and layout does **not** restore them. Only a process that never built the other cell
    does.
- **Decide (D5):**
  - (a) In `generate`, on "Cellname … already exists", delete the named cells and retry once, saying so in
    the result's diagnostics.
  - (b) Describe the kit in a forked child, so the worker starts generating from a clean process. Windows
    has no fork; there, describe stays in-process and the residual risk is stated.
  - (c) Generate each cell in a fresh child as well, at the cost of the worker's warm start.
  - Recommended: (a) and (b). (c) only if the matrix still shows order-dependent pins after both.
- The harness gains a column that separates "differs only after other cells were built in the same process"
  from a genuine difference, so the attribution is honest whichever option is chosen.

**Gate:** re-run kits C, D and the generic kit; 0 attributed to circuitRF.

**Also open from the same run.** Kit B, at its defaults, has 4 cells whose geometry on one layer differs from
gdsfactory's by just over the XOR tolerance (for example 2632 against 2308 DBU²). Its earlier full run never
compared these cells, because its worker died, so it is not known whether this is new. Investigate before
deciding whether the tolerance or the conversion is at fault.

## 3. Testing

One test per claim; run only the classes touched (`ImportPdkCliVerbTests`, `KitCardDeviceRoutingTests`,
`GdsfactoryAdapterTests`, the new loading test class). Tier B (`CRF_GDSFACTORY_PYTHON`) only for the
gdsfactory half of R-gdf6-2. Never the full suite.

## 4. Out of scope

- **A bundled pure-Python package with no metadata** cannot be told apart from circuitRF's own
  `pcell-python` sources by a rule that names no package. CliSmoke's scan documents this limit; it stays a
  limit.
- Restarting a kit worker that dies mid-run (brief 5's open kit-B item) is its own question.
