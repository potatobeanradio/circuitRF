# Brief DFR27-5 — 3D runs on an 8 GB laptop: an openEMS grid that fits, a time estimate, notes as they happen

**Series:** designer feedback round 27 (2026-10-25). · **Tag:** `R-dfr27-5-<n>`
**Area:** the openEMS gridder (`FdtdGrid`, `OpenEmsGridSettings`, the thirds rule and the `MinCell` merge),
`src/Design/Em3d/Solvers/OpenEms/Em3dRunService.OpenEms.cs`, the run log (`RunContext.Log`) and how the GUI posts it,
the pre-run size check (the Palace memory dialog), and the Palace launch settings. Findings go in
`src/Design/RESOLVED.md`.
**Tests:** counter tests only — grid line counts, smallest cell, estimate arithmetic. No timing tests.

---

## 0. What happened on the designer's machine (Windows, 7.9 GB, Linux subsystem 3.8 GB)

The round-26 TP149 as-built setup, unchanged:

- **Palace** — the size check said about 6.9 GB, 183 % of the Linux subsystem. Run anyway, it was killed (exit 137,
  signal 9) during the reduced-order model's first pass. The estimate was right; there was nothing else to try.
- **EMerge** — DFR27-4.
- **openEMS** — after 10 minutes it was still on port 1 of 6 at `0 / 46,988,910 steps`, 2.4 GB in use with
  swapping. On Cancel, the run's notes (solver version, each port's placement) all arrived at once, AFTER the line
  saying the cancel writes nothing.

`circuitrf explain` on the same setup with `Solver3D: OpenEms` shows why openEMS could never finish:

| | |
|---|---|
| grid | 430 × 535 × 107 = **24.6 M cells**, about 2.9 GB |
| smallest cell | **0.97 µm**, on y |
| time step | 2.67 fs (Courant) |
| steps | about 4.7 M per run (the run's cap is 10× that: 46,988,910) |
| runs | 6, one per port |

At ~100 M cell-updates/s that is about two weeks a port. The 0.97 µm cell is not a feature of the board: it is set by
a Top Copper ground edge at y = 119.0898 mm and a Bottom Copper ground edge at y = 119.2 mm — two DIFFERENT layers whose
edges land about 10 µm apart near the crop line — each wanting a thirds-rule pair. Fifteen more pairs fell inside
the 1 µm `MinCell` and were merged. Sub-micron to tens-of-micron offsets between imported Gerber layers are normal;
at 2.4 GHz they are electrically meaningless.

## 1. What to do

### R-dfr27-5-1 — Edges on different layers that nearly coincide share a line

Two edges within a merge distance that is a fraction of the LOCAL cell they would sit in (not the fixed 1 µm
`MinCell`) become one line, wherever they come from. Edges of the same conductor keep today's behaviour. The merge is
reported as today (`explain` lists each). State the rule in `docs/user` alongside the other grid settings.

Gate (counters): on the round-26 crop, the smallest cell is at least 10 µm and the step count falls by at least 5×,
and the explain listing still places every port on a line within 10 µm of where it is drawn.

### R-dfr27-5-2 — An openEMS run says how long before it starts

The pre-run check that asks about Palace's memory also asks about openEMS's TIME: cells × steps × ports over a
throughput (a conservative constant per core, or a short calibration on this machine), shown in hours or days, with
**Run anyway / Cancel**. Above a day it also names the largest contributor (the smallest cell and what set it) so the
designer knows what to change. The CLI prints the same estimate and runs (it has no dialog), as `em` does for memory.

### R-dfr27-5-3 — Notes as they happen

A run's notes (solver, ports, grid merges, warnings) post to Messages when they are known, not when the run ends. A
Cancel posts nothing after its own line except the stopped line.

### R-dfr27-5-4 — A solver that has gone quiet says so

If openEMS reports no step for 2 minutes, post one Info line: what it is probably doing (building its operator for
N M cells) and that Stop keeps nothing yet. Check that its progress lines reach circuitRF on Windows at all (a
buffered child stdout looks identical). Existing rule: no silent stalls.

### R-dfr27-5-5 — When Palace will not fit, offer something that will

When the estimate exceeds the memory available, the dialog offers what would fit, each with its new estimate: first-
order elements, fewer MPI processes (measure how Palace's memory varies with the process count on this board — keep
the run short), and a smaller crop. If none fits, say so plainly rather than offering Run anyway as the only button.
