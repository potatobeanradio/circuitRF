# Brief SAR-13 — Acceptance, example workspaces, docs

**Series:** `brief-sar-0-overview.md` (§4, L1) · **Tag:** `R-sar13-<m>`
**Depends on:** all
**Area:** `examples/` (two new workspaces, `examples.json`), `docs/user/` sources, `docs/design/sar.md`,
`src/**/RESOLVED.md`

---

## 1. Goal

Measure the series against overview §4, ship two worked examples, and document the feature for its two audiences.
After this phase the owner decides whether `sar` has been tested enough to merge into `main` (overview header).

## 2. Requirements

**R-sar13-1 — The numbers.** `docs/design/sar.md` closes with one table: every §4 item, its tolerance, the measured
value, and pass or miss. Each miss has its cause. The table also lists every owner decision (O1–O6) and its outcome.

**R-sar13-2 — Example: MRI loop coil.** `examples/SAR MRI Loop Coil/`:
- the 7 T loop and head phantom `.c3d` with capacitors as ports;
- a schematic with the tuning capacitors, the match and the feed;
- a TestBench with `fields`, B1 and SAR goals, and a saved optimizer setting.

Its run settings are the fast ones. Where an analysis setting was traded for speed, the example's README says which
setting, what it was, and what it costs in accuracy (the repository's standing practice for examples).

**R-sar13-3 — Example: phone SAR.** `examples/SAR Phone Dipole/`: the system-check dipole against the flat phantom at
two bands, using the band-sweep helper, with the reference-value table.

**R-sar13-4 — User pages** (doc sources only; DocGen runs at the end of the series as the owner does it):
- a SAR and B1 page: setting up, units and normalisation, the B1 convention, reading the results;
- a co-simulation page: per-port fields, the schematic side, tuning and optimizing on fields;
- tissue materials and phantoms;
- voxel and mesh import;
- the phone workflow.

Each page says once, plainly, that circuitRF's SAR is for **research and engineering use** and is not a compliance
tool (L1), and nowhere repeats it.

**R-sar13-5 — RESOLVED.md.** Findings with their causes go in the RESOLVED.md beside the code they concern (CLAUDE.md
rule), never in a CLAUDE.md: the openEMS HDF5 layout table, the averaging choices, the grid comparisons, and the ring-down numbers.

**R-sar13-6 — The vendor-name scan.** Before the owner commits, grep the branch's whole diff for commercial vendor,
product and model-family names (CLAUDE.md §Commercial Vendor References) and report what was removed.

## 3. Gates

The classes every phase added, run once by class (never the full suite unasked), plus `tests/Firewall.Tests` and
`VersionSingleSourceTests`. The opt-in `Benchmark` reruns once, for the record.
