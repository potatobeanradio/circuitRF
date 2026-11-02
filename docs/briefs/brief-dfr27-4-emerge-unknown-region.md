# Brief DFR27-4 — EMerge stops on the ten-layer board: "Unknown model region with tag 398"

**Series:** designer feedback round 27 (2026-10-25). · **Tag:** `R-dfr27-4-<n>`
**Area:** the EMerge driver and its problem writer (`src/Design/Em3d/Solvers/EMerge/`, the Python driver it runs),
the geometry it builds (PlanarRings, TouchingConductors, the fragment step). Findings go in `src/Design/RESOLVED.md`.
**Tests:** a reduced fixture in the EMerge driver tests; the full board only by hand.

---

## 0. What happened

The designer ran the delivered TP149 as-built setup (the round-26 workspace's `em/TP149_em_asbuilt.cem`, driven,
6 ports, 0.3–3.5 GHz, order 2, adaptive sweep) on EMerge 3.0.0b3 under Windows. It stopped after about 80 s:
`Exception: Unknown model region with tag 398`. The run's own files are in the round-27 feedback folder:
`problem.json` (format 2, 83 solids, 11 materials, 6 lumped ports, no sheets), `emerge.log`, `status.json`.

Palace meshes the same board (DFR26-1 fixed two Gmsh faults on it: clockwise hole rings emptying slabs, and a face
spanning a plane plus overhanging laser vias left unclaimed). This is the EMerge route's own geometry, which DFR26-1
touched only in part.

## 1. What to do

### R-dfr27-4-1 — Reproduce from the designer's `problem.json`

Run the driver on it here (EMerge is installed). **Use the memory/disk watchdog** (poll every 0.1 s; kill at 3–4 GB
RSS or under 10 GB free disk) — a Gmsh runaway on this machine has filled the disk before. Find which step mints
tag 398 and why the model has no region for it: a fragment that renumbered entities after the region map was taken,
a volume that a boolean split in two, or a region whose material lookup missed.

### R-dfr27-4-2 — Fix it, and say what failed when it cannot be fixed

Fix the cause. Separately, any unknown-tag failure must name the solid (its name from `problem.json`, its layer and
its bounding box), not the tag alone: "region 398" tells a designer nothing.

### R-dfr27-4-3 — A small fixture that fails today

Cut the problem down to the fewest solids that still fail (bisect the solid list), commit THAT as a test fixture (no
customer geometry: rebuild the shape synthetically if the reduced set still resembles the board), and gate on it.

Gate: the designer's `problem.json` meshes and reaches the first frequency solve; the reduced fixture passes in the
routine tier (under ~5 s, or `Category=Benchmark` if it cannot be).
