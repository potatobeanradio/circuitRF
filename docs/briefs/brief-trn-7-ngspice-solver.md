# Brief TRN-7 — ngspice as a transient solver

**Series:** TRN-0 … TRN-9 · **Tag:** `R-trn7-<n>` · **Depends on:** TRN-6 · **Design note:** §2 D2, D8, D10, D11, §9.1,
§9.3, §9.4, §12.3, §12.4
**Area:** `src/Design/Em3d/SolverDiscovery.cs`, `src/Design/Em3d/Install` (recipes, installer), `src/RfCore/Spice/RawFileReader.cs`
(new), `src/Engine/Transient` (the ngspice runner), Settings ▸ Solvers, the tran analysis card, `src/Cli` (`tran`,
`solver`), the MCP `run` and `solver` tools, `testdata/transient/`, `src/Design/RESOLVED.md`, `src/RfCore/RESOLVED.md`.

## The gap

A bench can be written as a deck (TRN-6) but nothing runs it. There is no way to find, install, run or read back
ngspice, so `Solver=ngspice` is still refused, and the native engine has no external references to be measured against.

## Requirements

**R-trn7-1 — Discovery.** `SolverTool.Ngspice` in the existing enum, appended so no stored value changes, with note
§9.1's search order (Settings, `CIRCUITRF_NGSPICE`, PATH, default directories, conda environments, circuitRF's own
install) and the version from `ngspice -v`.

**R-trn7-2 — Recipes**, one pinned upstream release, checksummed, on the existing schema:
- Windows x64: the official archive, download and extract. Windows arm64: an arm64 archive if upstream publishes one,
  otherwise the x64 archive with the Settings row saying it runs under emulation. Tested on an arm64 Windows machine.
- macOS (arm64, x64) and Linux (x64, arm64): micromamba's static binary downloaded and checksummed, then the conda-forge
  `ngspice` package installed into circuitRF's own environment. Confirm the package exists for each of the four before
  writing its recipe; a platform without it gets a `ByHand` message instead.
- No source build (D10).

**R-trn7-3 — Settings ▸ Solvers.** A **Circuit** group above the EM group with the ngspice row: where it was found, its
version, Install…, and the uninstall the other rows have. UiSnapshot PNG, looked at.

**R-trn7-4 — The run.** With `Solver=ngspice`: TRN-6's deck and map written to a scratch folder under the run's output
directory; ngspice run in batch mode with a binary rawfile output on the run's `RunControl`, the process killed on
cancel (exit 130 from the CLI); ngspice's messages relayed verbatim, prefixed with its name and version.

**R-trn7-5 — `RawFileReader`** in `src/RfCore`: binary and ASCII rawfiles, real and complex vectors (complex for later
analyses, tested now), the header's variable list and flags. Vectors renamed through the map and resampled onto the
output grid with the native engine's interpolation (D11).

**R-trn7-6 — The result.** The same cubes as a native run, `Ngspice.Version` and the deck's path in the provenance;
with `SpiceFit=yes`, one note per fitted element; with `SpiceLines=rlgc`, one note per line giving its fit frequency and largest |ΔS|. A failed run (non-zero exit, no rawfile, a missing vector) is a
refusal carrying ngspice's last message.

**R-trn7-7 — Disabled when missing; refused, never a fallback.** The tran card's ngspice choice is enabled only while
discovery finds a usable ngspice (installed, its path present, `ngspice -v` answering), showing the discovered version;
otherwise it is disabled (greyed) with a tooltip naming Settings ▸ Solvers. It follows discovery live: it enables when
an install finishes (`SolverInstallRunner.Finished`) or a path is set in Settings, and disables when an uninstall
completes, with no restart. A bench that already says `Solver=ngspice` while none is discovered shows that value,
disabled, with a note beneath naming Settings ▸ Solvers (its space reserved, so no control moves), and a run from the
GUI, the CLI or MCP is refused with that sentence and `circuitrf solver install ngspice`; it never runs native.

**R-trn7-8 — CLI and MCP.** `circuitrf tran --solver ngspice`; `circuitrf solver install ngspice`; MCP `solver` lists
ngspice and `run analysis=tran` honours `Solver`.

**R-trn7-9 — References for the native engine.** Decks for TRN-1–TRN-4's analytic and nonlinear gate circuits (RC, RLC,
rectifier, buck, lossless line, a MOS level-1 inverter ring) run once by ngspice, their output-grid waveforms committed
under `testdata/transient/` with each deck, the generating script and the ngspice version. A native-versus-reference
test per circuit, with its tolerance stated and justified in the test.

## Gates

1. **Rawfile.** A committed binary and an ASCII rawfile of the same run read to identical cubes.
2. **Live run.** (Skips with a reason when ngspice is not discovered.) The RC bench with `Solver=ngspice` equals the
   closed form within ngspice's tolerance and equals the native run within the stated tolerance.
3. **Cancel.** A long ngspice run cancelled through `RunControl` leaves no process behind and writes no result.
4. **Disabled and refused.** With discovery stubbed to find nothing: the card's ngspice choice is disabled with the
   tooltip, a bench saying `Solver=ngspice` shows the note, and its run refuses with the sentence naming Settings and
   the install verb; it never runs native. With discovery stubbed to find ngspice, the choice enables without
   re-opening the card. UiSnapshot PNGs of both states, looked at.
5. **References.** Each native-versus-reference test passes without ngspice installed.
6. **Recipe schema.** Every new recipe parses and checksums every download (the existing recipe tests).

## Not here

ngspice for any analysis but transient; linking `libngspice`; building ngspice from source.
