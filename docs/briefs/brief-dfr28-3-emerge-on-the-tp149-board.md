# Brief DFR28-3 — EMerge on the TP149 board: a NaN element, a port in the absorbing face, and a fair comparison

**Series:** designer feedback round 28 (2026-10-26). Follows DFR28-1. · **Tag:** `R-dfr28-3-<n>`
**Area:** `src/Design/Em3d/Solvers/EMerge/circuitrf_emerge.py` (the driver: the direct solve, `Heard`, `adaptive_sweep`),
`src/Design/Em3d/Solvers/EMerge/Em3dRunService.Emerge.cs` (the S = −I refusal), `src/Design/Layout/Em3d/Em3dGenerator.cs`
(air box and ports), `src/Design/Layout/Em3d/Em3dLayoutSolids.cs`. Findings go in `src/Design/RESOLVED.md`.
**Tests:** `tests/Ui.Tests/Em3d/` (`EmergeRunTests`, `EmergeGpuTests`, `ViaOnTheBoardEdgeTests`), an `EmergeFact` where a
real solve is needed.

---

## 0. What happened

The owner asked for the designer's driven setup (`em/TP149_em_asbuilt.cem`: six lumped ports on three nets, 0.3–3.5 GHz,
65 points, zero air-box padding on x and y, 4 mm on z) to be run on EMerge at the Standard preset and compared with the
Palace result. Runs were made on copies of the round-26 TP149 workspace with DFR28-1's build (the board-edge via clipped
to the board) on a 16 GB machine. Every run below except the last Palace one also made the air box follow the clipped via
(R-dfr28-3-0a, since reverted: see Finding A).

| run | result |
|---|---|
| EMerge Standard, device auto | meshed in 80 s: 67,712 tets, 515,532 unknowns (order 2), 6.2 GB peak. The GPU solve said "Matrix is so singular that it is broken on single precision" at every frequency; the adaptive sweep chased noise round 350 MHz until the stall watch switched it to the CPU at sample 17 (about 5 min lost). The CPU then converged in 8 samples, error 2.97e-4. |
| EMerge Standard, device cpu | the same answer in 1 min 57 s. |
| the same, YMax padding 300 µm | **S = −I at every frequency**, refused by round 28's check. Not memory: 2 min, 6 GB. |

**Against Palace Draft** (`results/TP149_em_asbuilt.palace.s6p`, written before DFR28-1, so its box face sat 0.15–0.3 mm
beyond the board edge where the unclipped via pushed it):

| ports (net) | max \|ΔS\| | |
|---|---|---|
| 1–2 (RFin) | 0.017 | agree; S21 within 0.05 dB |
| 3–4 (TP149) | 0.12 | S43 at 3.5 GHz −15.5 dB (Palace) vs −25.7 dB (EMerge); Palace Draft is order 1, EMerge order 2 |
| 5–6 (RFout) | 0.29 | EMerge S65 is **−3 dB, flat**, Palace ≈ 0 dB |

**Palace Draft on that same new geometry** (box face at the board edge; 3 min 40 s, 145,048 tets, 6.8 GB): S65 is
**−2.15 dB, flat**, where its own earlier run read ≈ 0 dB; every other entry moved by at most 0.014. So Finding A is the
geometry, not EMerge. Against it, EMerge's max |ΔS| is 0.018 on RFin, 0.13 on TP149 (S43 at 3.5 GHz −14.9 vs −25.7 dB)
and 0.075 on RFout (−2.15 vs −2.9 dB, both wrong).

### Finding A — a lumped port lying in an absorbing air-box face

Port 6 is the RFout line's end at the cropped board edge, y = 119.2 mm, normal (0, −1, 0). Making the air box follow the
clipped via (R-0a) put the zero-padded YMax face exactly at y = 119.2 mm, so the port's sheet lies in the absorbing face. A
flat −2 to −3 dB through-line is that port sharing its power with the boundary; Palace and EMerge both show it. Without
R-0a the edge via's whole drill circle still bounds the content and pushes the face 150 µm out, and that accident is what
keeps port 6 clear. R-0a was reverted for this reason: it lands with R-3, not before.

### Finding B — one NaN tetrahedron, a "converged" solve, and S = −I

With the YMax face 300 µm out, the run returned S = −I. Probed by hand in the run folder (the driver with
`SolverAASDS.solve` wrapped):

- the assembled matrix holds **400 non-finite entries in 20 columns**, and the right-hand side none. Twenty is one
  second-order tetrahedron's unknowns: **one element is degenerate** and its NaN reaches every unknown;
- the fast sparse solver returns x = NaN and reports `{'converged': True, 'residual': 0.0}`; EMerge ignores that report
  anyway (`x[:, i], _ = self.aasds.solve(...)`). Every factorisation it offers (LU, threshold-pivoted LU, symmetric and
  not, Bunch–Kaufman LDLᵀ) gives NaN, which is expected with NaN in A;
- the S-parameters that come out are exactly −I, which round 28's `NoFieldFrequencies` refuses, with a sentence that
  says the cause is "most often its sparse solver running out of memory". Here it is not.

**The designer's round-28 −I run is very likely this, not memory.** His geometry had the box face pushed beyond the
board by the unclipped edge via: the same shape as the 300 µm run. The round-28 response says memory.

### Finding C — the GPU says its factorisation is unusable, and solves with it

EMerge logs "Matrix is so singular…" when SPARTA's own `factor.quality.usable` is false, then solves with that factor.
The stall watch (EMW-3) caught it only after 8 samples without a new best.

## 1. What is already done (uncommitted at the time of writing)

- **R-dfr28-3-0a (reverted, do with R-3)** — the content bounds should use the clipped part of an edge-cut drill
  (`Em3dLayoutSolids.Extent`: `ClipDisc` instead of `ViaFootprint` when `!OnBoard`), so a zero-padded box ends at the board
  edge instead of 150 µm past it, where nothing is built. Tried and measured (§0), then reverted, because on its own it puts
  port 6 in the absorbing face. Add it back with R-3, and assert the box's YMax in `ViaOnTheBoardEdgeTests` (4 mm for the
  fixture's board edge at 3 mm plus its 1 mm of padding).
- **R-dfr28-3-0b** — Finding C: the driver switches a GPU adaptive sweep to the CPU at the first "broken on single
  precision" line (`GPU_UNUSABLE`, `Heard.unusable`), the stall report carries `"unusable": true`, the note and the
  progress row say why, and a non-adaptive GPU run that hears it gets a warning. Read-back test:
  `EmergeGpuTests.AGpuSweepStoppedForAnUnusableFactorisation_IsReadBackWithItsReason`. Not yet seen on a real run:
  confirm on the TP149 copy (device auto) that the switch happens at the first frequency.

## 2. What to do

### R-dfr28-3-1 — A non-finite solve is a failure, said at once and named

In the driver, wrap the direct solve (every solver EMerge may call, not just the fast sparse one): before solving, if the
matrix has a non-finite entry, stop the run with a refusal; after solving, if x is not finite, the same. Do it at the first
frequency, not after the sweep. The sentence names the cause (a degenerate mesh element) and **where it is**: map the bad
columns back to their element(s) through EMerge's mesh (edge/face DoF → tet), and give the element's centroid in mm and
the solids it lies in or between. Exit as a refusal (`Refused`), with the message circuitRF reports.

Keep `NoFieldFrequencies` as the last line of defence, but correct its sentence: −I is what any silent solver failure
leaves, and memory is one cause among others. Do not name a cause it cannot know.

Gate: a driver-level test that injects NaN into a solve (a fake solver, or a matrix with one bad entry) and reads the
refusal back; plus, if a small fixture reproduces the degenerate element, an `EmergeFact` on it.

### R-dfr28-3-2 — Find and remove the degenerate element

With R-1's location, find what in the geometry makes it. Suspects, from the 300 µm run: the region beside the board edge
at y = 119.2 mm, where the clipped via's cut face, pads and planes ending on the edge, and the thin air strip meet; and
features at the micrometre scale ("the smallest conductor feature (2.93 µm)", from `explain`). EMerge also warns that its
global scaling is experimental. Fix it where it starts (the geometry circuitRF writes, or the mesh options circuitRF
passes, such as a minimum size or Gmsh's optimiser), and show the padded run meshing and solving. If no clean fix exists,
stop and report what the element is and what would remove it; the owner decides.

### R-dfr28-3-3 — A lumped port must not lie in an absorbing face (and R-0a with it)

In `Em3dGenerator`, find any lumped port whose sheet is coplanar with an air-box face that is not PEC (to the same
tolerance the generator already uses for coplanarity) and lies within that face's extent. Then either pad that face
outward by a margin that keeps the port in the interior, with a Note saying so, or refuse with a sentence that names the
port and the face and says to pad it. Measure both on TP149 and recommend one; the owner decides. Check what each solver
does with a port in that position, Palace and openEMS included, before writing the sentence. The padded option depends on
R-2: until the degenerate element is gone, padding YMax by 300 µm is exactly the run that returned −I.

### R-dfr28-3-4 — The comparison, on one geometry

With R-1…R-3 in, run the TP149 driven setup on the same geometry with EMerge Standard (CPU) and Palace, and report max
|ΔS| per port pair and S21/S43/S65 at five frequencies, as §0 does. Palace Draft is order 1. Run Palace Standard (order 2)
too if its size estimate fits this machine (the circuitRF memory ceiling stops it otherwise). The 0.12 on the TP149 net
needs an order-2 Palace result to settle. Keep each run short: one setup, no field saves.

### R-dfr28-3-5 — Round 28's response

The response written for the designer attributes his EMerge −I to memory. Once R-1 can name the cause, re-run his Draft
geometry (the pre-DFR28-1 build, or a box padded past the board edge) and correct the response if it is the NaN element.

## 3. Not in scope

The GPU solver's precision itself (EMerge's), and the Draft/Standard presets' sizing. On this board the two meshes
differ by under 5 % (64,673 vs 67,712 tets), because the geometry, not the wavelength, sets the element size.
