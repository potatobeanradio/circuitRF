# Gmsh versions circuitRF has tested (brief-em3d-135)

`SolverDiscovery.Gmsh`'s list is of **tested** versions, not a pin: any other version runs, with a warning that
names this list. A version goes on the list only on a measured pass recorded here. Palace and openEMS keep their
exact-version refusal; nothing here applies to them.

## The pass rule (R-em3d135-2c)

Per version, per script, meshed exactly as a run meshes (`gmsh model.geo -3 -o model.msh` in the script's own
directory, by `run.sh`):

1. Gmsh exits 0.
2. **`entities.txt` is byte-identical to 4.15.2's** (the Homebrew build circuitRF was validated with, OCC 7.9.3),
   committed beside each script in `corpus/`. This is the table `GmshGeoWriter.CheckEntities` reads before any
   solve, and where a changed `BooleanFragments` or bounding-box selection would show. The B-rep/STEP probes print
   their own lines instead; their reference is `q4/`.
3. `CheckEntities` passes on it (`SolverDiscoveryTests.Gate9` replays every committed table).
4. The element count is recorded, **not gated**: meshes legitimately differ between versions. A difference over
   10 % would be flagged for review; none was.

A version passes when every script passes. A failing version stays off the list, and the rule is not loosened to
admit one.

## The corpus

- `corpus/golden--*`: the four `testdata/em3d/palace-goldens` scripts (driven microstrip, via, electrostatic,
  magnetostatic).
- `corpus/palace--*` and `corpus/thermal--*`: every Palace and thermal setup of every example `.c3d`, written by
  the writer a run uses (`C3dProblemAssembly.Assemble` then `GmshGeoWriter.Write`, or `ThermalLowerings.Build`),
  with its `groups.json` and the kernel `.brep` files the two Launch scripts import. openEMS-only setups write no
  Gmsh script. Output's RfHarmonics thermal setup wrote the same bytes as its DcSweep and is not repeated.
- `../f4b/q4/*.geo`: the kernel-import (B-rep and STEP) probes, in place.
- `../../thermal/**/mesh/*.geo`: the thermal references' scripts, in place. Their Python generators wrote them, not
  circuitRF, so they carry no entity table: exit code and element count only.

64 scripts in all. A version takes about 2.5 minutes on an M4.

    ./run.sh /path/to/gmsh /tmp/out        # exit 0 = every script passed; /tmp/out/summary.tsv per script

## Results

**The corpus was rewritten on 2026-11-06 for the build-once script (brief-em3d-140). Two builds have run it, and both passed
64 of 64: the reference, 4.15.2 with OCC 7.9.3, and 4.15.2 with OCC 7.8.1, the upstream macOS arm64 wheel (largest element
difference +3.8 %).** The rows below measured the scripts before that change. It adds `Geometry.OCCExportOnlyVisible`,
`FileExists`, `Delete Model`, and a `.brep` written by `Save` and read back by `ShapeFromFile`. Run `run.sh` on the other versions
before relying on them. The one table that changed is `palace--WavePorts_Launch--Palace`, whose `all_surfaces` is 42
against 44: two sheet pieces that bound no volume are no longer saved, and neither belonged to a group.

Measured 2026-10-15. *Max Δ elements* is the largest element-count difference from 4.15.2 over the 64 scripts.

| Version | OCC | Build | Scripts passed | Max Δ elements | Result |
|---|---|---|---|---|---|
| 4.8.4 | 7.6.3 | Debian 12 package, arm64 | 2 of 64 | -99.2% | FAIL |
| 4.11.0 | 7.6.1 | upstream's macOS arm64 archive | 64 of 64 | +5.6% | pass |
| 4.11.1 | 7.6.1 | upstream's macOS arm64 archive | 64 of 64 | +5.6% | pass |
| 4.12.0 | 7.7.2 | upstream's macOS arm64 archive | 64 of 64 | +5.6% | pass |
| 4.12.1 | 7.7.2 | upstream's macOS arm64 archive | 64 of 64 | +5.6% | pass |
| 4.12.1 | 7.6.3 | Ubuntu 24.04 package, arm64 | 64 of 64 | +5.3% | pass |
| 4.12.2 | 7.7.2 | upstream's macOS arm64 archive | 64 of 64 | +5.6% | pass |
| 4.13.0 | 7.7.2 | upstream's macOS arm64 archive | 64 of 64 | +5.6% | pass |
| 4.13.1 | 7.7.2 | upstream's macOS arm64 archive | 64 of 64 | +5.6% | pass |
| 4.13.1 | 7.8.1 | Debian 13 package, arm64 | 64 of 64 | +5.3% | pass |
| 4.14.0 | 7.8.1 | upstream's macOS arm64 archive | 64 of 64 | +5.6% | pass |
| 4.14.1 | 7.8.1 | upstream's macOS arm64 archive | 64 of 64 | +5.6% | pass |
| 4.15.0 | 7.8.1 | upstream's macOS arm64 archive | 64 of 64 | +5.6% | pass |
| 4.15.1 | 7.8.1 | upstream's macOS arm64 archive | 64 of 64 | +3.8% | pass |
| 4.15.2 | 7.8.1 | upstream's macOS arm64 archive | 64 of 64 | +3.8% | pass |
| 4.15.2 | 7.9.3 | Homebrew bottle, macOS arm64 (**the reference**) | 64 of 64 | +0.0% | pass |
| 4.15.2 | 7.6.3 | circuitRF's Linux arm64 recipe, Ubuntu 24.04 | 64 of 64 | +3.9% | pass |
| 4.15.2 | 7.6.3 | circuitRF's Linux arm64 recipe, Debian 12 | 64 of 64 | +3.9% | pass |

**Debian 12's Gmsh 4.8.4 fails**, and that is what a Debian 12 user sees: a run proceeds with the untested-version
warning and then stops with Gmsh's own error. First error lines: `Unknown variable 'newcl'` (the next free curve-loop
tag, which both writers use for every polygon outline: every Palace script and five of the six thermal ones),
`Unknown field type "Constant"` (One Finger's thermal script, its mesh-region field) and
`Unknown option 'Sampling' in field 1 of type 'DistanceField'` (the thermal references). The B-rep probes exit 0,
but the part's bounding box prints `0.000123463` where 4.15.2 prints `0`, and a tight-box face query then finds a
different count. The two scripts that pass are thermal-reference probes with no mesh-size field.

**Not measured:** Gmsh 4.10.5 and older on macOS (upstream's macOS archives before 4.11 are x86 only and did not run
on this arm64 machine); a Gmsh linking OCCT 8.x (the kernel's own OCCT 8.0.1 build in `~/.circuitRF-build` is
trimmed to the toolkits the geometry worker needs and lacks the IGES and XDE toolkits Gmsh's CMake requires, so it
was not cheap to have). OCCT 7.6.1, 7.6.3, 7.7.2, 7.8.1 and 7.9.3 all gave the same entity tables.
