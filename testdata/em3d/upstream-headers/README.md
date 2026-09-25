# Upstream copyright header lines — the GPL boundary's scan fixtures

`tests/Firewall.Tests/SolverBoundaryTests.cs` (brief-em3d-6 R-em3d6-6a) fails if any file under `src/`
or `tools/` carries one of these lines, because a file carrying one is upstream Gmsh, openEMS or CSXCAD
source — GPL (LGPL for CSXCAD), and circuitRF is MIT and ingests none (em-3d.md §12).

Each `.txt` holds the identifying lines of a header, verbatim, taken from the upstream source F0
installed (2026-09-25): `gmsh.txt` from Gmsh 4.15.2's installed `gmsh.h`, `openems.txt` from openEMS
0.37.0-rc3's `openems.cpp`, `csxcad.txt` from CSXCAD `dcdb62b`'s `CSXCAD_Global.h`. Only the lines that
name the project or its copyright holder are kept — never the licence text itself. The scan generalises
the year run in a `Copyright` line, since each upstream file states its own years.
