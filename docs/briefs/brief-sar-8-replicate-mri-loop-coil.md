# Brief SAR-8 — `MRI_Loop_Coil` (and the birdcage), rebuilt in circuitRF (the coil gate)

**Series:** `brief-sar-0-overview.md` (§4, L4, L8, O5) · **Tag:** `R-sar8-<m>`
**Depends on:** SAR-6
**Area:** `testdata/sar/circuitrf/loop/` and `.../birdcage/` (new), `tests/Ui.Tests/Sar/`, `docs/design/sar.md` §10

---

## 1. Goal

Prove B1, local SAR and lumped tuning on openEMS's 7 T loop-coil example, the way SAR-7 proves psSAR. **It is a
gate, on SAR-7's terms.**

## 2. Requirements

**R-sar8-1 — The `.c3d`.** From the tutorial's stated parameters:
- **loop:** 80 mm (z) × 60 mm (y), 5 mm strips in the plane x = −130 mm, with gaps of a third of the strip width;
- **capacitors:** three 5.4 pF `C3dLumpedElement`s at the midpoints of the two sides and the top;
- **feed:** a 2.5 Ω lumped port in the bottom gap;
- **phantom:** the three-ellipsoid head with the script's semi-axes and 298 MHz tissue values;
- **setup:** `MUR` on all faces; Gaussian f0 = 298 MHz, fc = 300 MHz; `EndCriteria 1e-4`; `CellConstantMaterial` on;
  SAR at 298 MHz with B1 on and B0 along +z.

The phantom is circuitRF's own `C3dPhantom`, not the bundled `.h5` (L3). Before the run, compare it **voxel by voxel**
against the reference's dumped material grid at the same lines; a mismatch is resolved first.

**R-sar8-2 — Two runs**, as SAR-7: (a) on the reference's XML grid lines; (b) on circuitRF's own 2 mm grid in the
phantom. Outputs are committed under `testdata/sar/circuitrf/loop/`.

**R-sar8-3 — Comparison.** `LoopCoilReplicationTests` on fixed data. Run (a) is held to overview §4:
- S11 minimum frequency and depth;
- B1+ per √W at the phantom centre, and the correlation of the B1+ and local-SAR planes;
- peak local SAR per W.

It also compares Yin across the band. Run (b) is reported, with its differences explained as in SAR-7 R-sar7-5.

**R-sar8-4 — Ring-down, measured.** Record the run's time steps to the end criterion and compare them with the
reference's. It is a high-Q structure: if circuitRF's run stops early on its port-decay rule (em-3d.md §3), say
whether that changed the result. This number is SAR-9's motivation.

**R-sar8-5 — The birdcage (O5).** SAR-1 has its reference. Rebuild the 8-rung, 2.6 pF low-pass birdcage in its
320 mm bore with the cylindrical body phantom. Do the quadrature feed (1 at rung 1, −j at rung 3) as **two single-port
runs combined** with those weights. That is circuitRF's first superposition, compared with the reference's single run
with both ports excited. Same comparison form, with B1+ homogeneity (coefficient of variation in the phantom's central
slice) added.

## 3. Not in this phase

Capacitors as ports (SAR-9).

## 4. Gates

`LoopCoilReplicationTests`, plus `BirdcageReplicationTests` (fixed data, routine). `Benchmark` reruns are
opt-in.
