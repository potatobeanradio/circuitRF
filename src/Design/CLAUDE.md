# `src/Design` — the design-layer document artifacts

Standing instructions for `CircuitRF.Design`. Created 2026-08-26 by
`docs/sonnet-briefs/brief-cli-em-verb.md`, which carved it out of `CircuitRF.Ui` so `src/Cli` could
run an EM setup. Detail of that move is in `src/Ui/Layout/Em/RESOLVED.md`.

## What this project is

**The files a layout and an EM setup are stored in, and the code that reads them.** Concretely:

| Folder | Holds |
|---|---|
| `Layout/` | the layout model + `.clay` reader, geometry/units/angles, flatten, booleans, the spatial index, the cell-layout resolver, and the technology model + `.ctech` reader/validator |
| `Layout/Em/` | the `.cem` model + reader, the cross-section and planar extractors, port extraction, `EmRunService`, `EmSetupResolver`, SnP provenance |
| `Layout/Drc/` | only what the `.clay`/`.ctech` FORMAT names — the waiver record and the layer-expression parser. The DRC engine is not here |
| `Layout/PCells/` | only `PCellValue` and how it serialises. No generators, no handle solver, no Python |
| `Cells/` | the `.ccell` cell-folder format and the atomic write behind every save |
| `Workspace/` | the `.cws` reader and R-fgn-3's ancestor-workspace walk |
| `Theming/` | `Rgba`, because a technology's layers carry colours |
| `Results/` | where a run's grouped `.npy` lands and what it is called |

## What must NOT come in here

**Anything that draws, docks, undoes, or observes a canvas.** This project is on the far side of the
UI firewall and `tests/Firewall.Tests` asserts it references no Avalonia, transitively. That
assertion is not decoration: every file in here sat next to Avalonia until the day it moved, so the
gate is what keeps `circuitrf em` buildable at the later date when someone reaches for a `Dispatcher`
in the layout reader.

Things that deliberately stayed in `src/Ui` and should not follow: `LayoutEditorViewModel` and its
sixteen partials, `DrcEngine` and the wBond assembly rules, all seven PCell generators and
`GeneratedCellStore`, `EmSetupEditorViewModel`, `EmBackAnnotation`, `TechEditorViewModel`, the
technology importers, and `AppPreferences`. Flattening a placed generated cell reads the `.clay` the
generator already wrote, which is why a headless EM run needs no generator and no Python.

## Two rules that are easy to break by accident

**Diagnostics are RETURNED, never posted.** `TechnologyResolver`, `EmSetupResolver` and
`ResultsWriter` all hand back what they have to say and let the caller decide — the GUI raises
Messages, the CLI writes to stderr. A parameter of type `IMessageSink` in here is the shape of the
mistake.

**A preference is an ARGUMENT.** `EmRunService.Run` takes its core cap rather than reading
`AppPreferencesIo`, because headless there is no preferences file and no user to have set one. The
same applies to anything else the GUI stores per-machine.

## Who consumes it

`src/Ui` and `src/Cli`, one way only. `src/Ui/GlobalUsings.cs` lists every namespace that moved here,
in one place, rather than in ~300 `using` lines — that file is the map when a type seems to appear
from nowhere in `src/Ui`.

## Gate

Plain `dotnet test` at the repo root. `tests/Firewall.Tests` is the one that must never be skipped
when touching this project's references; `tests/Ui.Tests` holds every layout, technology and EM test.
`tests/Ui.Tests/Em/EmCliVerbTests.cs` is the end-to-end proof that a headless run and a Simulate
write the same bytes to the same path. It launches the real CLI and still runs in well under a
second, so it is in the routine gate rather than the `Benchmark` tier — see its own header for why
it launches the BUILT `CircuitRF.Cli.dll` rather than `dotnet run`.
