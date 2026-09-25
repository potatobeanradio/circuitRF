# Brief 10 — running both: one setup, two solvers, and the difference

**Series:** [3D EM, first series](brief-em3d-0-overview.md) · **Tag:** `R-em3d10-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §4.4 (what agreement proves), §4.5, §4.3, §12 (disagreement read as a bug)
**Area:** `src/Design/Em3d/Em3dRunService.cs`, `src/Design/Em3d/Em3dComparison.cs` (new),
`src/Cli/CliEntry.cs` (`RunEm`)
**Depends on:** 7, 9 · **Blocks:** —

---

## 0. What this brief delivers

`Solver3D: Both` (or `em --solver both`) runs Palace and then openEMS on **the same generated
problem**. It writes each result where brief 7 §5 puts it, plus a **comparison `DataSet`** that the
Data Display and `plot` read like any other result.

Most of the value is in what the comparison **says** beside the numbers. Two solvers that share
nothing numerically will disagree by something (§4.4). If circuitRF gives a user a few tenths of a dB
with no explanation, the user distrusts both (§12).

---

## 1. `R-em3d10-1` — one problem, generated once

**`R-em3d10-1a`** The problem is generated **once** and handed to both backends. Generating it
twice could not differ today, but running it once is what makes "same geometry" true by
construction rather than by coincidence. A counter asserts one `Em3dGenerator.Generate` per run.

**`R-em3d10-1b` It is a cross-check, not a reference** (§4.4). A bug in the generator appears in
both results and agreement cannot catch it. The comparison's header says so in one sentence, and
nothing in the UI or the docs calls agreement "validated".

**`R-em3d10-1c`** The two runs are **sequential**, Palace first, because each already uses every
core. The progress line names which one is running.

---

## 2. `R-em3d10-2` — the comparison `DataSet`

**`R-em3d10-2a` Same frequency grid, exactly, or no comparison.** Both backends evaluate at the
setup's requested frequencies: Palace at its sweep points, and openEMS by direct DFT at those
frequencies (brief 9 §4b). The comparison asserts the two frequency vectors are **equal** and
**refuses** if not. It never interpolates one onto the other, because interpolation error would
be reported as solver disagreement.

**`R-em3d10-2b` Cubes**, per S-parameter (i, j): `S_palace`, `S_openems` (complex); `dMag_dB` =
20·log10|S_p| − 20·log10|S_o|; `dPhase_deg`, wrapped to (−180, 180]; and `dVec` = |S_p − S_o|,
which is the one that stays meaningful where |S| is small and a dB difference explodes. Single-kind
cubes, per the invariant.

**`R-em3d10-2c` Written as** `<key>.compare_em.npy`, alongside the two results, through the same
`.npy` writer the EM runs use. `circuitrf plot` draws it with no `.cdd` to author (gate 5).

**`R-em3d10-2d` The reference plane is stated.** Both results are referred to the ports' reference
planes (brief 3 §2c). The comparison records the planes, all zero-shift for Tier A lumped ports. A
comparison is only fair when both results are referred to the same plane (§4.4).

---

## 3. `R-em3d10-3` — what the comparison says

Emitted as notes on the run and carried in the dataset's metadata. Each comes from a fact the run
already knows; none is generic boilerplate.

**`R-em3d10-3a` §4.3's expectation for this geometry**, from brief 5's guidance function (the same
function, not a copy), in the form: *"bond wires present: FEM is expected to be the more accurate
here"*.

**`R-em3d10-3b` Every known systematic difference that applies to this run**, from the two backends'
own notes:
- dielectric loss fitted at f_c in FDTD, against constant tanδ in FEM (brief 9 §2d). The expected
  |S21| difference **grows away from f_c** on a lossy substrate, and the note says so;
- solid conductors written as PEC in FDTD (brief 9 §2d), with their names;
- wires below the FDTD grid resolution, modelled as thin conductors (brief 9 §2c), with their names;
- an FDTD run that did not reach its end criterion (brief 9 §3d). This is a warning, and it is the
  first thing to fix before reading any difference;
- lumped ports: both are lumped, so their parasitics are similar but not identical. State it once.

**`R-em3d10-3c` A summary line**: the largest |dMag_dB| and |dPhase| over the band for S21 (or the
first transmission term) and S11, and the frequency where each occurs. This is the line a user
reads first.

---

## 4. `R-em3d10-4` — failure of one side

**`R-em3d10-4a`** If one solver refuses or fails, **the other still runs and its result is
written**. The comparison is not written, and the run exits **1**, because something asked for was
not obtained. The report names which succeeded and where its result is. Throwing away a
half-hour FEM run because openEMS was not installed would be the worst outcome.

**`R-em3d10-4b` Refusals before work.** Both solvers' discovery, version and capability checks run
**before either starts**. If Palace is fine and openEMS is missing, the user learns that in the
first second, not after the FEM run. The refusal says the user can run the one that is available
with `--solver palace`.

**`R-em3d10-4c`** Cancellation stops whichever is running, writes nothing for it, keeps an
already-finished result from the other, and exits 130.

---

## 5. `R-em3d10-5` — `-o` with two results

`-o base` gives the base path. The results are `base.palace.sNp` and `base.openems.sNp`, and the
comparison is `base.compare_em.npy`. All three are listed on stdout and in `--json`. The
single-solver meaning of `-o` (brief 7 §5c) is unchanged.

---

## 6. Gate

`tests/Ui.Tests/Em3d/RunBothTests.cs` and `tests/Engine.Tests/Em3d/Em3dComparisonTests.cs`.

1. **Comparison arithmetic, synthetic**: two hand-built S sets. dMag, dPhase wrapping at ±180, and
   dVec checked against values computed in the test.
2. **Grid mismatch refuses**: two sets whose frequencies differ by 1 Hz at one point.
3. **One generation**: counter reads 1 for a both-run.
4. **One side fails**: a fake openEMS executable (a script that exits 1 with a message) leaves the
   Palace result written, no comparison, exit 1, and the message quoted verbatim. *(Palace.)*
5. **Plottable**: `circuitrf plot` on `compare_em.npy` with `--trace cube=dMag_dB,i=2,j=1` writes
   an SVG.
6. **Notes apply only when true**: a lossless-substrate setup carries no dielectric-loss note; a
   lossy one does.
7. **F0 case B through both**, generated by circuitRF: the summary line's maxima fall within the
   spread F0's Q1 measured between the two hand-built runs, cited. *(Palace + openEMS; Benchmark.)*
8. **Refusal before work**: openEMS missing means no Palace process started (counter), and the
   message names `--solver palace`.

## 7. Scope

- **No viewer comparison of fields.** F2 (§8.5, "either field or their difference").
- **No automatic "which one to trust" verdict.** The notes explain; the user decides.
- **No comparison against planar MoM or kernel W** in this dataset. That is a useful follow-up with
  its own reference-plane questions, and it needs its own brief.
