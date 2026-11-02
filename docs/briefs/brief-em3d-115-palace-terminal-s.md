# Brief 115 — Palace: terminal ports on a shared face, and the example that shows them

**Series:** [3D EM, eighth series](brief-em3d-112-overview.md) · **Tag:** `R-em3d115-n`
**Rewritten 2026-10-06** from [brief 124](brief-em3d-124-palace-modal-transform-spike.md)'s go (option 1, the derived
modal transform alone). The owner chose it over [brief 119](brief-em3d-119-palace-magnetic-wall-spike.md)'s route B
(the mirror-symmetry half-model), which is not built.
**Area:** `src/Engine/Em3d/` (a new `ModalTerminalTransform`: Palace's modal S, `V_wp`, Z_PV and kₙ → terminal S),
`src/Design/Em3d/GmshGeoWriter.cs` (one port face per terminal group), `src/Design/Em3d/PalaceConfigWriter.cs` (N entries
on that face), `src/Design/Em3d/PalaceRun.cs` (readers: `V_wp`, kₙ per frequency), `src/Design/Em3d/Em3dRunService.cs`
(`TerminalPortRefusal`, the Palace read path), `src/Cli/` (`em`'s report, `explain`), `docs/user/src/` (`em-setup.md`,
`em-3d.md`, `cli.md`, the new-user guide §14), **`examples/3D Wave Ports/`** (§8)
**Depends on:** 114, 124 · **Blocks:** nothing

---

## 0. What this brief delivers

A multi-terminal wave port runs on Palace, symmetric or not. Every terminal's mode goes on ONE port face per end: N
`WavePort` entries on one attribute, `Mode` k on entry k, the first `Active`. circuitRF then converts Palace's modal S
to terminal S, one port per terminal at its own Z0, in the same `.sNp` an openEMS run writes. The 3D Wave Ports example
then shows it on both solvers, on its existing symmetric stripline Pair and on a new asymmetric microstrip pair that no
symmetry route could run.

**Read first, all of it:** `src/Design/RESOLVED.md` § "Palace modal transform on a shared face — brief-em3d-124". It
holds the transform, every choice behind it, and the numbers this brief's gates hold to. Its harness is
`tools/palace-symmetry-spike/` (`modal.py` is the transform in 120 lines of numpy; port it, do not reinvent it). Its
fixtures are `testdata/em3d/terminal/palace-modal/` with a README naming each run. Also read § brief-em3d-113 (the
original derivation, R-em3d113-1d/e/g) and § brief-em3d-119 for its meshing lesson: the strip-edge refinement is what
makes the bar.

**What 124 measured, in one paragraph.** At equal mesh density the transform equals route B to max |ΔS| 0.0003 on the
air stripline pair, and 0.0003 / 0.0007 on a microstrip pair. An asymmetric air pair (10 % and 3 % wider) is within
0.0025–0.0031 of the exact multiconductor line, the same error the symmetric pair has on that mesh. An asymmetric
microstrip lands at openEMS's own error, and what the asymmetry does agrees with openEMS to 0.002–0.005. With a loss
tangent, what the loss does agrees with openEMS to 0.0004 at the one frequency where openEMS's loss is exact. It costs
3.5–4× route B's wall clock and about 2× its memory.

## 1. Owner decisions, with the default this brief builds

| | Decision | Default |
|---|---|---|
| D-115a | Terminals per face | **Two.** Three or more is refused by name, pointing to openEMS. 124: on a face whose modes are degenerate, the Gram fit is exactly determined at three terminals (no check left) and under-determined at four; a non-degenerate face would work but was not measured |
| D-115b | A terminal port with a nonzero reference-plane shift | **Refused on Palace**, by name, pointing to openEMS (which takes one Offset per face). The transform needs `Offset` 0 (113: `V_wp` ignores it); a modal de-embedding by each kₙ is possible but unmeasured |
| D-115c | openEMS's lidded microstrip | **Unchanged here.** 124 found that a PEC face above a microstrip makes circuitRF's openEMS port treat it as a stripline and average the strip → ground and strip → lid voltages (they differ by 10 % at 6 GHz in 124's box). The new example cell's README states the difference; §8e writes a follow-up brief to measure it |

## 2. `R-em3d115-2` — lowering a terminal group to Palace

**`R-em3d115-2a` One face per group.** `GmshGeoWriter` today makes one physical surface per `Em3dPort`. A terminal group
(`Em3dPort.FaceGroup`) shares one rectangle (114 validates that), so it becomes **one** surface and one attribute,
claimed once. Ungrouped ports are untouched: every existing `.geo` golden stays byte for byte (gate 7).

**`R-em3d115-2b` The entries.** `PalaceConfigWriter` writes, per group, one `WavePort` entry per terminal, all on the
group's attribute, in terminal-number order:
- `Index` = the terminal's port number; `Mode` = 1, 2, … in that order; `"Active": true` on the first, `false` on the
  rest; **`MaxSize` equal on every entry of the face** (113: only one shared eigen-solve guarantees independent modes);
  use `max(2·N, N + 15)` for the face's N, the largest default Palace would pick for any of them;
- `Offset` 0 always (D-115b refuses the rest);
- `Excitation` = the port number, as today; every entry is excited;
- `VoltagePath` = the terminal's own path, signal → ground (114 builds it).

Ungrouped wave ports are written exactly as today.

**`R-em3d115-2c` The second-mode check** (`PalaceRun.SecondModes`) sets every entry to mode 2. On a terminal face, mode 2
is a terminal's. Ask the face's mode **N + 1** instead, once per face, and word the note so that it is about the mode
past the terminals' own.

**`R-em3d115-2d` Refusals.** `TerminalPortRefusal` lifts its Palace refusal for a two-terminal group and keeps it,
reworded, for D-115a/b: *"Port 'Left' has three terminals; Palace runs terminal ports with two terminals per face in
this version. Set the setup's solver to openEMS."* `Both` then runs both solvers on such a problem, and the skip note
stays for what Palace still refuses.

## 3. `R-em3d115-3` — the frequency sweep: measure first

124 solved single frequencies only (Point samples). circuitRF writes a Palace sweep as an adaptive one (`Linear` with
`AdaptiveTol`), and Palace's adaptive path (PROM) may not evaluate the port modes at every output frequency (PR
[#903](https://github.com/awslabs/palace/pull/903)). kₙ is written only to the log (`Port k, mode m: kₙ = …`,
`PalaceRun.ParseWaveMode`). Before building §4, run 124's `basym-shared-r2e` geometry (harness, not product code)
twice: once as an adaptive 2–6 GHz sweep of 9 points, and once as Point samples at those 9 frequencies. Record in
`RESOLVED.md`:
- whether `port-S.csv`, `port-V.csv` and `port-Z.csv` carry every output frequency in the adaptive run;
- which frequencies' kₙ lines appear in the adaptive run's log;
- the transform's terminal S from both runs at each frequency (max |ΔS|), using kₙ interpolated where the log lacks it
  (interpolate ε_eff,m = (Re k_m·c/ω)² linearly in f between logged frequencies, which 124's 2/4/6 GHz values support:
  K varied 1.0793 → 1.0823 → 1.0863).

**Decide by the numbers.** If the adaptive terminal S matches Point to ≤ 0.001, use the adaptive sweep with interpolated
kₙ. If it does not, write Point samples for a problem with a terminal group on Palace and say so in the run's notes (it
costs one full solve per frequency per terminal). **If neither fits gate 9's time bound, stop and report to the owner.**

## 4. `R-em3d115-4` — the transform (numeric layer, pure)

`ModalTerminalTransform.Solve(...)`: arrays in, arrays out, no file. Per frequency, with the run's modal S (all ports),
the `V_wp` matrix (port × excitation), each entry's Z_PV and kₙ, and the groups (each face's entries, the Active one
first):

```
P = 1 + S_m                      M = V · P⁻¹                T_I = M⁻ᴴ
G = 1, except per degenerate face (below): real symmetric, unit diagonal, g from |(M Gᵀ)_ii|² = Z_PV[i]
C = G⁻ᵀ · P                      K = diag(Re k_active / Re k_m), per face
I = T_I · (2 − K C)              U = V
S_t = (U − Z₀ I)(U + Z₀ I)⁻¹     then renormalise to each terminal's own Z0
```

- **Degenerate face:** every entry's Re kₙ within 1e-3 of the Active entry's (the log prints four figures; 124's air
  faces agree to all four, its microstrip faces differ by 7–8 %). Only there is G fitted, by least squares on the relative
  residual from several starts. **Never fit a complex g** (124: it lands on a wrong root with zero residual). Elsewhere
  G = 1 (124: fitting it on a non-degenerate face moves S by ≤ 5.5e-4 and is not physics).
- **K uses real parts**, never the complex kₙ (124: Palace builds the Robin term and every mode's n×H from Re kₙ; the
  complex form misses what loss does by 0.0085).
- An ungrouped wave port in the same run is a one-entry face: G = 1, K = 1, and the transform reduces to its Z_PV
  renormalisation. Prove that in gate 3, and keep `RenormaliseWavePorts` for runs with no group.
- A new reader `PalaceRun.ReadWavePortV` reads `Re{V_wp[i][j]}` / `Im{…}` by column NAME (one excitation drops the second
  index, as `ReadPortV`'s comment records for `V`).

## 5. `R-em3d115-5` — what every run checks and reports

In `em`'s output, the `.sNp` provenance header and the run's notes:

| Check | Outcome |
|---|---|
| The route | **Note**, always: *"Port 'Left': terminals P1, P2 as modes 1 and 2 of one Palace wave port, converted to terminal S (degenerate face: Gram fit g = −0.501, residual 4e-6)."* |
| Gram fit residual (degenerate faces) | **Reported**; above **1e-3** a **warning**: the fit has no root consistent with Palace's Z_PV |
| |T_V,ii|² against Z_PV after the fit | Reported in the diagnostics; a mismatch above 1e-3 is a **warning** naming the port (a changed power convention, e.g. PR #937, shows here) |
| M off-block (a terminal reading another face's modes) | Above **1e-2** of the largest entry, a **warning** |
| Per-port power |S_jj|² + Σ|S_ij|², and σ_min, σ_max | **Reported**. On a problem with no loss mechanism (perfect conductors, lossless dielectrics, no absorbing face), any of them more than **0.002** from 1 is a **warning**. σ_max alone is not the check: 124 saw σ_max 1.0000 with σ_min 0.805 when a correction was missing |
| Reciprocity | Reported only (124: blind to every failure it saw) |

The diagnostics `.npy` adds, per run and frequency: the modal S, `V_wp`, Z_PV, kₙ, G and K
(`<key>.palace_modal_S`, …), so a disputed result can be re-derived without re-running.

## 6. `R-em3d115-6` — explain, fields, UI

- **`explain`** on a `.c3d` with a Palace setup and a terminal group prints the §5 route line: which entry is Active,
  each terminal's Mode, the equal MaxSize, and whether the face will be judged degenerate (after a run; before one,
  "decided from the run's kₙ"). Or it prints the refusal sentence.
- **Field plots (D8):** a step of such a run is an excitation of one MODE, not one terminal. Its *Solution* label says so
  (`Left mode 2`). **No terminal-drive superposition** in this brief.
- **No new editor UI.** Nothing is declared that 114 does not already declare.

## 7. `R-em3d115-7` — user docs (sources only; overview §2a)

Edit `docs/user/src/` only. Do **not** run DocGen; the owner does that.
- `em-setup.md` **#wave-port-terminals** gains *On Palace*: two terminals per face, any cross-section, symmetric or
  not; Palace solves the face's modes and circuitRF converts them to terminal S. One paragraph on what it costs (one
  excitation per terminal, the whole problem meshed). What is refused (three terminals, a shifted reference plane) and
  why. The warnings of §5 and what to do about each.
- `em-3d.md`: the diagnostics keys, the field-plot *Solution* labels.
- `cli.md`: `em`'s report lines and `explain`'s line.
- Every page that says terminal ports run "on openEMS only" (the overview's §2a list) is corrected in the same change.

## 8. `R-em3d115-8` — the example: `examples/3D Wave Ports/`

The example shows terminal ports on both solvers. The rule from 117: **every number in it comes from a run, never from
this brief.** The two cells:

**`R-em3d115-8a` Pair gains a Palace setup.** Pair is the symmetric PTFE stripline pair (a degenerate face, so the
Gram fit runs). Its Palace setup uses the existing mesh controls: the Palace preset, plus mesh regions (`C3dMeshRegion`)
along the four strip edges, standing in for 119's strip-edge size field. Sweep and points as §3 decided. The bar
against Cohn's coupled line (57.511 / 43.100 Ω, already in `expected-numbers.json`): **0.05 dB / 0.5° on every entry
above −30 dB, |ΔS| ≤ 0.006 below, per-port power and σ within 0.002 of 1.** If the existing controls cannot reach it
inside gate 9's time bound, stop and report. Do not add a mesh control in this brief.

**`R-em3d115-8b` A new cell, *Coupled Microstrip*:** the case no symmetry route can run. Two microstrip lines of
DIFFERENT widths on the example technology's PTFE-glass laminate, in a closed housing (PEC sides and lid, as 124's
shielded box; 119 measured absorbing faces near a microstrip pair loading its even mode). A two-terminal wave port per
end, `Reference` the ground. Two setups, *Palace* and *openEMS*, at the same frequencies. Size it so both runs stay
inside gate 9's bound, and choose widths and gap for an asymmetry the S-parameters show plainly (S11 ≠ S33 by more than
1 dB somewhere in the band). Its README section explains, in one paragraph each and with no history:
- what Palace does: one port face per end, two modes on it, converted to one port per strip;
- why the two solvers' numbers differ where they do: the lid makes openEMS average two voltage paths (D-115c), and
  openEMS fits the laminate's loss tangent at the band centre only (`CsxcadWriter`'s note);
- the two runs side by side at three frequencies (|S11|, |S33|, thru, near-end, far-end).

**`R-em3d115-8c` Numbers and test.** `expected-numbers.json` gains both cells' Palace runs and the new cell's openEMS
run: recorded |S|, tolerance, wall clock, memory, and the per-port power. `tests/Ui.Tests/Examples/Em3dWavePortsExampleTests.cs`'s
routine gates cover the new cell (closed forms from its VARs where there are any, the README/guide text checks), and
its gate 4 (`Category=Benchmark`) re-runs every shipped run and holds each recorded |S| to its tolerance.

**`R-em3d115-8d` Guides.** The README's "Why Pair has no Palace setup" paragraph goes. "Runs on openEMS only" becomes
true statements about both solvers. The new-user guide's §14 (`#wave-ports-3d`) and `em-setup.md`'s example pointer
follow. Tools ▸ Examples needs nothing: the workspace is already listed.

**`R-em3d115-8e` The follow-up brief.** Write `docs/sonnet-briefs/brief-em3d-<next free number>-openems-lidded-microstrip.md`
and add it to the overview's §2 table. It measures circuitRF's openEMS port on a microstrip under a PEC lid against
Palace (124's `testdata/em3d/terminal/palace-modal/openems/*/probes.npz` already hold the down and up halves), and
decides whether the classification or the voltage should change. Do not change openEMS in this brief.

## 9. Gate

`tests/Engine.Tests/Em3d/ModalTerminalTransformTests.cs`, `tests/Ui.Tests/Em3d/PalaceTerminalPortTests.cs`, and the
example's test file. One test per claim; run the classes you touch, not the suites.

1. **Replay A** *(124's fixtures, through `PalaceRun`'s readers and the transform)*. `a-shared-r2e0.02` gives route B's
   result on the same mesh (`a-half{PMC,PEC}-r2e0.02`) to max |ΔS| 0.0005, and g = −0.501 / +0.153 to 1e-3.
   `a-shared-r2-default` and 113-a's `../palace/pair-a-shared-face` (two different mode mixtures) give terminal S equal
   to 0.0005.
2. **Replay asymmetric.** `asym10-shared-r2e0.01` against the exact multiconductor line from 124's 2D capacitance matrix
   (commit the 2 × 2 Zc as test data; do not port `fv2d.py`): max |ΔS| ≤ 0.003.
3. **Replay B, lossless and lossy.** `basym-shared-r2e-f{2,4,6}`: per-port power within 0.002 of 1, and K =
   1.0793 / 1.0823 / 1.0863. `basymloss-shared-r2e-f4` minus `basym-shared-r2e-f4` against openEMS's lossy minus lossless
   (`palace-modal/openems/*/PairB.s4p` at 4 GHz): max |ΔS| ≤ 0.001. A one-entry face reduces to today's
   `RenormaliseWavePorts` to 1e-12.
4. **The wrong forms fail** (so the gates can see what they guard): G = 1 on `a-shared-r2e0.02` leaves per-port power
   off 1 by more than 0.01; K = 1 on `basym-shared-r2e-f6` leaves σ_min below 0.9; complex K on the lossy differential
   exceeds 0.005.
5. **Lowering golden.** The Pair with a Palace setup: one port surface per end in the `.geo`, four entries in the config
   (Mode 1/2, Active true/false, equal MaxSize, Offset 0, paths). Golden committed.
6. **Refusals.** Three terminals; a shifted reference plane; both name openEMS. `Both` on a two-terminal problem runs
   both solvers.
7. **Byte identity.** Every existing Palace golden (`.geo` and config) is unchanged.
8. **Checks.** Doctored inputs: a Gram residual of 1e-2 warns; a lossless problem whose result loses 0.5 % warns; a
   passive, lossless pair warns nothing.
9. **Real runs** *(`Category=Benchmark`)*: §8's three shipped runs to their bars. **Each Palace run under 10 minutes
   on the reference machine** (124 measured 2.5–3 minutes per frequency for four excitations at 1.57 M unknowns, so the
   sweep decision in §3 is what makes this possible). Record settings, wall clock and memory in the README and in
   `expected-numbers.json`.

## 10. Owner check

- Run Pair on Palace and on openEMS; plot S21/S31/S41 from both over Cohn's line.
- Run Coupled Microstrip on both solvers; read the route note and the power lines; compare S11 with S33.

## 11. Scope

- Two terminals per face, wave ports only, Offset 0. No N > 2, no reference-plane shift on Palace, no openEMS change
  (§8e writes that brief), no mixed-mode export (D13), no terminal-drive field plots (D8).
- Route B and route A are not built. 119's harness and fixtures stay as the reference the transform was checked
  against.
- **When Palace moves version** (`SolverDiscovery`'s validated list), re-check what the transform depends on: 124's
  "What the transform depends on in Palace" list (#960, #937, #886, #996/#1019, #328). Gates 1–4 are the re-check: they
  replay the transform on recorded files, so re-run 124's harness on the new Palace and replay its new output too.
