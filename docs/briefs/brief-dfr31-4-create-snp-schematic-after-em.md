# Brief DFR31-4 — Create SnP Schematic after a successful EM run

**Series:** designer feedback round 31 (2026-10-29). · **Tag:** `R-dfr31-4-<n>`
**Area:** `src/Design/Layout/Em/EmSetupModel.cs` + `EmSetupPersistence.cs` (a new setup flag), the EM run completion
path (`EmRunService` and the 3D run service), `src/Design` cell creation (`CellCreate`) and schematic writing,
`src/Ui/Views/Layout/EmSetupEditorView.axaml`.
Findings go in `src/Design/RESOLVED.md`.
**Tests:** `tests/Ui.Tests/Em/`.

---

## 0. Why

The first thing done with a finished multi-port driven run is to build the schematic that uses it: an SnP block
reading the run's Touchstone, TermG on every port, then the parts being studied (in the field test, a shunt capacitor
and a 0 Ω link). That is the same few minutes of placement after every run. The run should offer to create that
start: a check box on the EM setup, and a cell and schematic with the ports and the analysis already set up, TermG on
every port, and the TermGs turned to face outward so a many-port result is easy to wire.

## 1. Requirements

**R-dfr31-4-1 — Setup flag `CreateSnpSchematic`** (default off), shown on the setup panel as **Create SnP schematic
when the run finishes**. Stored in the `.cem` and in an embedded `.c3d` setup alike. A planar run and a 3D run both
honour it.

**R-dfr31-4-2 — What is created**, through the same `CellCreate` the New Cell command calls (no second copy of
creation logic: the `Authoring.cs` rule). The cell is named `<setup>_sim`. If that cell exists, it is never
overwritten. The schematic is re-pointed only when its SnP block still names the same results file, and otherwise
the run says the cell was left alone. Contents:
- an S`N`P instance whose File is the run's Touchstone, workspace-relative;
- one TermG per port, `Num` = port number, Z = the setup's port impedance;
- each TermG placed on the side of the SnP block its port faces in the layout (the port label's direction), rotated
  to point away from the block;
- for N > 4, ports spread evenly along each side;
- an S-parameter analysis over the setup's sweep;
- a Data Display with dB(S) of the through paths and the input matches.

**R-dfr31-4-3 — On a re-run**, the existing `_sim` cell is not touched. Its SnP already reads the same file path,
which the run overwrites (the predictable output path is by design).

**R-dfr31-4-4 — Messages line:** "Created cell `<name>`: an S6P of this run with 6 TermG, ready to simulate" with a
link that opens the schematic.

## 2. Gates

1. A 2-port planar run with the flag on creates the cell, and simulating that schematic reproduces the Touchstone's S21
   to 1e-9.
2. A 6-port run places the TermGs on the sides the ports face. Render with `tools/UiSnapshot` and look before
   reporting.
3. A second run neither duplicates nor overwrites the cell.
