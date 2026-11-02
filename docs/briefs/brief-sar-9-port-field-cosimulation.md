# Brief SAR-9 — Capacitors as ports: per-port fields recombined by the circuit (co-simulation)

**Series:** `brief-sar-0-overview.md` (§1, O6) · **Tag:** `R-sar9-<m>`
**Depends on:** SAR-8
**Area:** `src/Design/Em3d/` (per-port field runs and storage), `src/Engine/Em3d/Sar/` (recombination), the schematic
S-parameter block (`src/Core` component and its elaboration), a new analysis output in the schematic's TestBench,
the 3D view and Data Display (`src/Ui`), the CLI, `docs/design/sar.md` §11

---

## 1. Goal

The method coil designers use, built in.
1. Every capacitor gap and feed of a coil becomes a port. The EM problem then has no resonance of its own, and no
   long ring-down.
2. openEMS runs **once per port** and saves each port's E and H in the lossy region.
3. A schematic holds the coil's S-parameter block with its real capacitors, matching network and feed.
4. Solving the schematic gives every port's incident and reflected waves.
5. B1 and SAR follow by superposition, in seconds, **with no new EM run** when a capacitor changes.

## 2. Requirements

**R-sar9-1 — Per-port field runs.** A `.c3d` setup with `Sar.PerPortFields` set runs one openEMS excitation per
port, with the others terminated in their Z0. That is what the S-matrix already needs, so it is the same set of runs.
Each run keeps its E (raw SAR dump: E, σ, ρ, volume) and H in the cropped region. Storage: one float32 file per port,
per frequency, per field, beside the `.sNp`, read **memory-mapped**. The S-parameter file's provenance records the
field files, so they move and stale together. The run reports their total size before it starts (overview §7).

**R-sar9-2 — Normalised per incident wave.** Each port's fields are stored per unit incident wave a_k = 1 √W at that
port, with every other port matched, as brief-em3d-100 refers fields to the incident wave. The total field for any
excitation is then E = Σ_k a_k·E_k (and the same for H). Here a_k is the incident wave the **circuit** delivers to the
port, computed in the port's own Z0.

**R-sar9-3 — The schematic side (O6).** The `.c3d`'s `.sNp` is placed in a schematic as the existing S-parameter
block. The schematic adds the tuning capacitors across the cap ports, the matching network and the feed source. A
TestBench analysis output `fields` names the block instance and an SAR frequency. The circuit's port waves at that
block's ports become the weights a_k. Result: a **recombined field set** with the same quantities SAR-6 produces (local
and averaged SAR, B1±, scalars), normalised to the source's available or accepted power. They are viewed in the 3D
view of the `.c3d`, which opens with the recombined result selected, and plotted in the Data Display.

**R-sar9-4 — Recombination cost.** The recombination is a weighted sum over N port files, streamed:
- local SAR and B1 maps are O(N × cells);
- averaged SAR reruns SAR-5's averaging on the summed field.

A **fast path** computes B1 and local SAR only, on a clip plane or a region, for interactive tuning (SAR-10), and full
10 g averaging on demand. A counter proves no openEMS process starts on a capacitor change.

**R-sar9-5 — Consistency checks the run reports.**
- Σ over ports of the power each delivers equals the absorbed plus radiated plus conductor-loss power of the
  recombined field, within a stated tolerance.
- A port whose fields are missing or stale (older than its `.sNp`) is a refusal naming it.

**R-sar9-6 — The validations.**
- **The loop coil with ports for capacitors** (4 ports) and the schematic's 5.4 pF capacitors must reproduce SAR-8's
  lumped-capacitor run (a) to overview §4: B1+ within 2 %, psSAR10g within 3 %.
- **Superposition.** SAR-8b's birdcage two-run combination, plus a two-port case where a
  simultaneous two-port run must equal the weighted sum of two single-port runs.

Both committed as circuitRF output and compared on fixed data.

## 3. Not in this phase

Goals, tuning, optimizer, yield (SAR-10); Q-matrices.

## 4. Gates (minimal)

- `FieldRecombinationTests`: synthetic per-port fields with known weights give the hand-computed sum, SAR and B1.
- `CosimLoopCoilTests`: R-sar9-6's comparison on fixed data.
- `CosimNoEmRunTests`: a capacitor change recomputes the fields with zero openEMS launches (counter).
- A stale field file is refused.
