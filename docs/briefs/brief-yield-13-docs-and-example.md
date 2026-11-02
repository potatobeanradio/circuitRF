# Brief YA-13 — The Yield example, the user pages, the design note's final state

**Series:** `brief-yield-0-overview.md` · **Tag:** `R-ya13-<m>` · **Depends on:** all
**Area:** `examples/Yield/` (new), `examples/examples.json`, `docs/user/src/reference/` (new `yield.md`, edits to
`optimization.md`, `tuning.md`, `cli.md`, `ai-agents.md`, the Data Display page, `pdk-authoring.md`),
`docs/user/src/_nav.txt`, `docs/design/yield.md`, `docs/design/tuning-optimization.md` §19, `docs/PRD.md`

---

## 1. Goal

A user who opens **Tools ▸ Examples ▸ Yield** sees the whole workflow work on real benches, and the pages explain it
in the order a designer meets it. The PRD records yield as delivered.

## 2. Requirements

**R-ya13-1 — The example workspace** (the six-example pattern: a folder plus an `examples.json` entry):
- **Bandpass yield** — the Optimization example's minimax bandpass with ±2 % Gaussian L and C (truncated at 3σ),
  a correlated pair of capacitors from one lot, two yield goals loosened from the optimizer goals (`use=yield`) and a
  tighter `use=opt` copy; the saved `.cdd` is the one-click yield display. It shows the intended loop: centre,
  loosen, estimate.
- **Amplifier corners** — a small amplifier bench with three temperatures × two supply values generated as corners,
  one goal failing at the hot/low-supply corner, and a statistical corner saved from a Monte Carlo run, then the
  optimizer run across corners.
- **Centering** — a simple divider bench whose start yield is ~70 %; Centering brings it above 95 % with the
  verification printed.
Each bench's analysis settings are cheap (seconds per run at the default trial counts); where a setting was traded
for speed, the bench's description says which and what the full setting would be.

**R-ya13-2 — The user pages.** A new **Yield** page in the Simulate section of `_nav.txt`, after Optimization:
tolerances and distributions (with the percent-vs-absolute rule), correlation, specs as goals and `use=`, Monte Carlo
vs yield, reading the yield and its interval, auto-stop, did-not-evaluate trials, the plots (histogram, CDF/quantile,
yield sensitivity, families by pass/fail, envelopes, scatter, contributions) with when to use each, corners and
statistical corners, optimizing across corners, centering and the surrogate, kit statistics (process/mismatch, how to
select a kit's statistical section). Edit `cli.md` (the `yield` verb), `ai-agents.md` (the walk-through),
`optimization.md` (`corners=` and `use=`), `tuning.md` (Evaluate at), the Data Display page (the new styles and menus),
`pdk-authoring.md` (how a kit's statistical sections are found and used). **No change-history phrasing** in any page.
**Do not run DocGen** — the owner regenerates at the end of the series.

**R-ya13-3 — The design note.** `docs/design/yield.md` gets its "decisions as built" table and the "what's left"
list (high-sigma, the other dialect's statistics blocks, and design of experiments while YA-14 is unbuilt — each
with the seam it would use).
`tuning-optimization.md` §19 marks every seam taken and by which phase. `PRD.md` moves yield from "planned" to
delivered in §2 and §5.1, with the acceptance gates that hold it.

**R-ya13-4 — Example gate.** One test per bench runs its yield/corner/centering at the example's own settings with a
fixed seed and asserts the outcome the page describes (a yield inside a stated interval, the named failing corner, the
verified centering gain) — the `OptimizationExampleTests` pattern. These are the series' acceptance tests; tag one
`Category=Benchmark` only if measured above ~5 s.

## 3. Gates (minimal; run only these classes)
- `YieldExampleTests` — R-ya13-4.
- `ExampleWorkspacesTests` (existing) — gains the new entry: listed and opens.
