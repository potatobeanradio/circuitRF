# Brief 125 — openEMS: a microstrip terminal under a PEC lid

**Series:** [3D EM, eighth series](brief-em3d-112-overview.md) · **Tag:** `R-em3d125-n`
**Written by** brief 115 (§8e), from brief 124's finding and D-115c.
**Area:** measurement first, in `tools/palace-symmetry-spike/oems/` (scratch output under its git-ignored folders); then,
only if the measurement says so, `src/Engine/Em3d/FdtdWavePorts.cs` (`Classify`) and `src/Design/Em3d/OpenEmsRun.cs`
(how a terminal's voltage is formed). Findings in `src/Design/RESOLVED.md`.
**Depends on:** 115 · **Blocks:** nothing

---

## 0. What this brief settles

circuitRF's openEMS wave port classifies a terminal by the PEC faces around it (`FdtdWavePorts.Classify`): a strip with
a PEC face on both sides of its voltage path's axis is a **stripline**, and its voltage is the **mean** of the strip →
ground and strip → lid line integrals (brief 116). That is exact for a stripline, where the two halves are equal by
symmetry. **A microstrip inside a closed housing also has a PEC face above it** (the lid), so it is classified as a
stripline and its voltage is averaged over two paths that are not equal.

Brief 124 measured how unequal (`src/Design/RESOLVED.md` § brief-em3d-124, R-em3d124-1c): on its shielded microstrip
pair (εr 3.5, h 0.508 mm, lid 3 mm above the substrate), up/down = 0.986 at 2 GHz and **0.895 at 6 GHz** on the driven
strip, and 0.65 at −18° on the passive one. Re-assembled with the strip → ground half alone (Palace's voltage path),
openEMS's error against Palace fell from 0.0240 to 0.0089 at 4 GHz. Brief 115's *Coupled Microstrip* example cell is
such a housing, and its README states the difference rather than hiding it. Measured there (2–12 GHz, lid 3 mm above
a 0.508 mm εr 2.2 laminate, strips 1.2 and 2 mm): openEMS against Palace is max |ΔS| 0.009–0.026 up to 8 GHz and
**0.10–0.11 at 10–12 GHz** (|S22| −11.86 dB against Palace's −16.08 dB at 10 GHz); re-assembled from that run's probe
files with the strip → floor half alone it is **0.008–0.027 over the whole band** (`src/Design/RESOLVED.md` §
brief-em3d-115, R-em3d115-8). That run's probe files are in its results folder, not committed: commit them as a fixture
(packed as 124's `probes.npz`) when this brief starts.

The question: **should a microstrip under a lid keep the stripline classification, and if not, what voltage should its
terminal have?**

## 1. Measure first (no product change)

**R-em3d125-1a.** Re-use 124's probe files: `testdata/em3d/terminal/palace-modal/openems/*/probes.npz` hold, per run,
the two voltage halves (`u_dn` strip → ground, `u_up` strip → lid) and the two current planes. `oems/probes.py`
re-assembles S from them with the mean, with `u_dn` alone, or with `u_up` alone. Against Palace's shared-face runs of
the same geometry (`basym-shared-r2e-f{2,4,6}`, `bsym-shared-r2e-f{2,4,6}`, through brief 115's transform), tabulate
max |ΔS| and the per-entry dB / degree differences for all three voltages, at 2, 4 and 6 GHz.

**R-em3d125-1b.** Then vary the lid height in openEMS only (three heights: 124's 3 mm, 1.5 mm and 6 mm above the
substrate), on the symmetric pair, and record how up/down and the three re-assemblies move. A lid far away should leave
the strip → ground voltage and the mean converging; a close lid is where the classification matters most.

**R-em3d125-1c.** One Palace run per lid height on the symmetric pair (Point samples at 2 and 6 GHz), so each openEMS
variant has its own reference. Keep every run under five minutes; coarsen before you lengthen.

## 2. Decide by the numbers

Record the table in `src/Design/RESOLVED.md` and pick one of:
1. **Classify by the ground only**: a strip whose reference is on one side is a microstrip whatever is on the other, and
   its voltage is strip → reference. The lid stays in the field solution; it only stops changing the voltage's path.
2. **Keep the mean** and say so in the run's notes, if (1) is not closer to Palace at every lid height.
3. **Something in between** (for example the reference half alone only when the other face is farther than some
   multiple of the substrate height), only if the measurement shows a clean break.

**If none is closer to Palace than today's by more than openEMS's own grid error on this geometry** (124: 0.0089–0.0103
with the strip → ground voltage), change nothing and close the brief with the table.

## 3. If a change is made

- `FdtdWavePorts.Classify` and the voltage the run forms follow the decision; `explain`'s openEMS wave-port lines say
  which classification each terminal got and why.
- Every existing openEMS golden whose terminals the decision does not touch stays byte for byte. The 3D Wave Ports
  example's *Coupled Microstrip* openEMS run is re-run and its recorded numbers, README and `expected-numbers.json`
  updated; its README paragraph on the lid then says what openEMS does now.
- Gates: the re-assembly of a committed `probes.npz` through the product code reproduces the chosen voltage's S to 1e-9;
  the example's Benchmark gate re-runs it.

## 4. Scope

openEMS only. No Palace change, no change to stripline terminals between two real ground planes (brief 116's Pair is
exact as it is), no new UI.
