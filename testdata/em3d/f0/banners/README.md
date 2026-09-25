# Version banners, as F0 recorded them

The exact text each validated program prints when asked what it is — the fixtures
`tests/Ui.Tests/Em3d/SolverDiscoveryTests.cs` parses (brief-em3d-6 R-em3d6-2c) and its fake executables
print. Nothing here was written by circuitRF.

| File | Program and question | Where it was recorded |
|---|---|---|
| `palace-version.txt` | Palace 0.18.1, `palace --version` | `docs/design/em-3d-f0-findings.md` Q9, verbatim |
| `gmsh-version.txt` | Gmsh 4.15.2 (Homebrew), `gmsh --version` | `docs/design/em-3d-f0-findings.md` §6, verbatim |
| `openems-banner.txt` | openEMS 0.37.0-rc3, the banner every run and `openEMS --help` print | the first 13 lines of `../B-via/openems/dw25-lossy/run.log`, byte for byte |

openEMS has no version flag: `--version` is an unknown option and aborts (Q11). `--help` prints the same
banner and exits 0 (checked on the F0 install, 2026-09-25).
