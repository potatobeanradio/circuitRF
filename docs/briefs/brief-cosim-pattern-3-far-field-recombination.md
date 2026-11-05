# Brief CP-3 — Fields gives a far field and the circuit's S

**Series:** `brief-cosim-pattern-0-overview.md` · **Tag:** `R-cp3-<n>` · **Depends on:** CP-2
**Area:** new `src/Design/Em3d/PortFarFieldSet.cs`, `src/Design/Em3d/FieldCosimulation.cs`, `src/Design/Em3d/Em3dRadiation.cs`,
`src/Design/Circuit/CircuitEvaluation.cs`, `src/Core/Design/Analysis.cs`, `src/Design/VendorLibraries/VendorPartRange.cs`, `src/Cli/Fields.cs`,
`src/Cli/Serve/ToolCatalog.cs`, `src/Ui/DataDisplay/ViewModels/TraceRowViewModel.cs`, `tests/Ui.Tests/Sar/`. Findings go in `src/Design/RESOLVED.md`.

## 0. Why

A Fields analysis refuses a block with no SAR field set, so it cannot use the per-port far field every driven run with **Radiation
pattern** stores. This brief makes it use them, and proves the result physically before any further UI depends on it.

**Do not rebuild:** `SParameterEngine.BlockWaves` (the circuit's V and I at the block), `FieldCosimulation.Weigh` (the waves),
`Em3dRadiation.Publish` (every metric), `NpyReader`, and the directive, its editor and its serialization.

## 1. Requirements

**R-cp3-1 — `PortFarFieldSet.Open(record)`.** Reads the `.npy` the record names and returns the grid, frequencies, port numbers,
`farfield.Etheta`, `farfield.Ephi`, `S`, `Z0`, the reference input power and the solver's name, or a sentence refusing them. A
refusal names the file and the remedy, for an `.npy` with no `farfield` group or ports whose Z0 differ (`cosimulation.md` already
requires one Z0). Cache the read per path, length and write time: CP-4's sliders evaluate many times, and a read costs far more
than the sum.

**R-cp3-2 — Dispatch in `FieldCosimulation.Run`.**

| The record has | Result |
|---|---|
| A field set, no far field | Today's result, plus R-cp3-6 |
| A far field, no field set | The pattern; no volumes, no tissue checks |
| Both | Both, in the same analysis |
| Neither | A refusal naming both settings |

SAR wording stays only on paths about SAR. `average` and `norm` do not apply to pattern cubes: gain is per watt accepted and realized
gain includes mismatch, as for a 3D run. Say so in the run's first note when `norm=available` is set.

**R-cp3-3 — The sum.** Overview §3, at each frequency. Reuse `Weigh`'s wave formula; do not write a second one. A `drive` list gives one
`port` entry, the first drive's, as the SAR path does. A block with S offsets is refused as it is today. Hold the grid the solver wrote,
including an openEMS hemisphere: `Publish`'s `hemisphere` comes from the source's θ axis.

**R-cp3-4 — `freq=all`.** One frequency stays the usual form. `all` takes every frequency the run stored, which gain against frequency
needs. It is valid only for a far field; a SAR set's refusal says so. Teach every reader of `FreqExpr`: `FieldCosimulation`,
`VendorPartRange`, and the summary in `CircuitEvaluation`. A frequency between points is refused, listing them.

**R-cp3-5 — Names.** `CircuitEvaluation` groups only `r.Data.Cubes`, the default group, so cubes `FarFieldStage.Publish` puts in a `farfield`
group would be **dropped silently**. Publish into the analysis's default group under the keys `farfield.U` and so on, which reads
`F1.farfield.U` in the run's result and leaves `StackSweepAxis` working (CP-5). **First**, confirm a dot in a cube's name survives
`NpyWriter`/`NpyReader` and the trace-spec parser, and report before changing the spelling if it does not. Then make
`TraceRowViewModel.IsFarFieldCube` and the `plot` verb's pattern shorthand accept the name under an analysis's group, so a pattern
defaults to dB and `cut=` works.

**R-cp3-6 — The circuit's S.** Every Fields analysis also publishes `S` and `Z0` of the circuit, over its circuit ports, at its own
frequencies, on `SParameterEngine`'s path and in its cube shape, so `F1.S` and `dB(F1.S(1,1))` work as `SP1`'s do. The drive does not
change S. The existing `Gamma` cube keeps its meaning (the driven excitation's reflection).

**R-cp3-7 — The checks**, each a note, and a warning when it fails:
- delivered power against the block's S, to `PowerTolerance`, as the SAR path does;
- radiated over delivered at most `1 + EfficiencyTolerance`;
- a note when any `|1 + S_kk|` is small enough that the port's stored pattern has lost precision, naming the port and frequency.

**R-cp3-8 — Headless.** `circuitrf fields` and MCP's `run analysis=fields` run it with no new flags. Update both tools' text.

## 2. Gates

Keep them few; one test per claim; run only these classes. **Report the measured numbers in `RESOLVED.md`. If a gate misses its limit,
stop and report; do not widen the limit.**

**Routine, on small synthetic data built in code (a coarse grid, no solver):**
- **G1 identity.** Every block port on a circuit Port of its own Z0, drive k: the result equals the stored pattern k, to 1e-9.
- **G2 algebra.** Three ports, hand-made S and patterns, parts wired across two. Compare with `a = (I − ΓS)⁻¹·a_source` written in the
  test. Cover L, C, SRLC, PRLC, and an `.s2p` wired 1-to-node, 2-to-ground against the impedance it presents.
- **G3 S and refusals.** `F1.S` equals `SP1.S` at the same frequency, and one line each for the refusals and the SAR-only row of
  R-cp3-2.

**Physical, measured once on a small board with a scratch harness.** The problem is an antenna with a feed and two gap ports that all
carry current; one gap port's `Reference` is reversed so polarity is exercised. Keep each run short. Use one frequency so no
adaptive-sweep tolerance enters. Limits apply over directions within 20 dB of the peak:

| Gate | Compare | Limit |
|---|---|---|
| **G4 resistive, Palace** | 50 Ω run recombined with a 200 Ω load, against a run with that port's Z0 set to 200 | peak gain 0.05 dB; pattern 0.2 dB; \|ΔS11\| 1e-3 |
| **G5 reactive, Palace** | L and C recombined, against a scratch copy of the run's `config.json` with `L` or `C` on those `LumpedPort`s and no `Excitation` | same limits |
| **G6 high Q** | G5's values moved onto resonance, at Draft and Standard | report; no limit |
| **G7 other solvers** | G4 on EMerge and openEMS | same limits, or that solver is refused by name until it passes |

G5's config edit is for the measurement only. Nothing in the product writes it.
