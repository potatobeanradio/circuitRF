# PMS-2 accuracy references — A1 and A2

Today's tensor-grid planar solve of the two accuracy blocks of `brief-pms-2-measure-mesh-drivers.md`. They are the
references a new mesh representation (stacked regions, the local grid) is measured against, in |ΔS|.

## The structures

`SyntheticMmicChip.AccuracyBlock(regionVia)` in `tests/Engine.Tests/Mom/Support/SyntheticMmicChip.cs`, on that file's
two-level GaAs medium (100 µm GaAs εr 12.9, 3 µm εr 2.7 dielectric; Metal1 on the interface, Metal2 on top, gold).
A 250 µm block:

- a 20 µm Metal2 line across it at y = 125 µm — ports 1 (left edge) and 2 (right edge), on Metal2;
- a 20 µm Metal1 line from the bottom edge at x = 210 µm (port 3, on Metal1) up to an 80 µm octagonal Metal1 pad
  carrying a 60 µm octagonal backside via;
- four posts, 6 µm cuts on 14 µm pads on both levels: two on the Metal2 line, two on the Metal1 line;
- **A1 only:** a 120 µm square at (60…180, 60…180) µm where Metal2 overlaps Metal1 and is joined to it by one via
  footprint of the same size, on the Metal2 line.

A2 is A1 without the region (its metal and its footprint).

## How they were solved

`new PlanarKernel().Solve(chip.ToProblem(), mesh, chip.Ports(), [1, 5, 10 GHz], solve)` in Release:

- mesh: `PlanarMeshSettings.Default with { MinCellsAcrossConductor = 2, MemoryLimitBytes = 16e9 }` — 2 across rather
  than the default 4 so the dense solve fits in memory and time (4 across meshes A1 at 22,005 unknowns before feed
  leads);
- solve: `PlanarSolveSettings.Default` with `Fill.EdgePortReturn = Ground` (the product's default edge port; the
  engine's series-gap default drops 1 GHz here as below its de-embedding floor). Dense, de-embedded, conductor loss on.
- The solver grows a uniform feed lead on every port (the block is shorter than the 300 µm the calibration standard
  replaces) and peels it off again: A1 meshes at **11,655 unknowns** (5,467 cells, 1,188 vertical), A2 at
  **7,308** (3,664 cells, 328 vertical), each with six calibration standards.

Files: `A1.s3p`, `A2.s3p` — S, real/imaginary, 50 Ω, ports in the order above.
`Pms2MeshDriverTests.TheAccuracyReferences_DescribeTheFixtures` checks the blocks still mesh to those counts at these
settings, so a change to the fixture or the mesher that would invalidate the references fails there.
