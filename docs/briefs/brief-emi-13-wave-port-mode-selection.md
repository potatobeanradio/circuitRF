# Brief EMI-13 — which mode an EMerge wave port carries: the line's own mode by its effective index, and a degenerate pair split

**Series:** EMI · **Tag:** `R-emi13-<n>` · **Depends on:** EMW-2 (single-terminal wave ports), EMW-3 (terminal groups), HZN-11 and HZN-20
(hollow guides of one and two modes), the EMerge 3.0.10 re-pin (`src/Design/RESOLVED.md`, "EMerge 3.0.10") · **Area:**
`src/Design/Em3d/Solvers/EMerge/circuitrf_emerge.py` (wave-port construction, `hollow_mode_signs`), the lowering
(`EmergeScriptWriter`, `EmergeLoweringRules`), `src/Design/Em3d/Em3dModePorts.cs`, the hollow-guide reading of `.hzn` files, kernel A's
cross-section solve, `explain`, `docs/user/src/reference/em-setup.md`, `src/Design/RESOLVED.md`.

## The gap

**A wave port takes the mode with the largest β², which is not always the line's.** EMerge's `WavePort` fixes its modes at the sweep's top
frequency as the candidates with the largest β² (3.0.8 made this one procedure for every port, and kept the rule), and circuitRF's
single-terminal wave port is that `WavePort` with `modetype="TEM"`. Two cases are already recorded where the largest is the wrong one:

- **Ground metal on the top layer at the port's face.** Between that ground and the bottom plane runs a parallel-plate mode with
  β² = εr·k₀², above a microstrip's or a grounded coplanar line's quasi-TEM mode (ε_eff < εr). On Palace, whose wave port chooses the same
  way, the port took it: 10.5 Ω with wide top grounds, S21 ≈ −40 dB on a through line (`src/Design/RESOLVED.md`, the coplanar return's
  entry). A `.c3d` drawn from a real board has such grounds at most faces.
- **A line over conducting silicon at low frequency.** ε_eff falls from 53 to 7.6 over 1–40 GHz there, and a wave port took the wrong
  mode at 5 GHz: a uniform line read S11 = −0.76 dB (`src/Design/Em3d/RESOLVED.md`, PWL-2 Q5).

EMerge 3.0.2 added `target_neff` to `ModalPort`: the first modal analysis searches around it and orders the candidates by their distance to
it. `WavePort` has no such argument; its candidate order is `_candidate_key`, −Re β². circuitRF already knows the index to aim at: kernel A
solves the face's cross-section quasi-statically (the wall ports' C′, the planar solver's silicon path), and a terminal group already
passes `presolveNeff` to its first mode solve.

**A guide whose two lowest modes are degenerate is refused, though EMerge can now split them.** HZN-11 refuses a square guide, and HZN-20
refuses two modes whose cutoffs agree within a part in 10⁴: an eigen solve returns any combination of a degenerate pair, so which field each
port mode carries was undefined. EMerge 3.0.8 rotates a degenerate TE or TM pair onto the vectors given to `align_modes()`, and fixed
`align_modes()` on a `WavePort` (it failed with "'numpy.ndarray' object has no attribute 'x'"). A multi-mode hollow guide runs on EMerge
only (`Em3dModePorts`), so this is EMerge's to lift, and each mode already has its polarity line, which is the vector to align it with.

## Requirements

**R-emi13-1 — The line's mode, aimed at.** For a single-terminal wave port on a face that carries conductors, the lowering computes the
line's effective index at the sweep's top from kernel A's cross-section of that face and hands it to the driver; the driver makes the mode
closest to it the port's mode. Decide how, with a spike on the two recorded cases, and record why:
- a `WavePort` whose `_candidate_key` is the distance to the target (as `ModalPort`'s is with `target_neff`), so the port keeps
  `WavePort`'s absorption of evanescent modes and its tracking; the driver already subclasses `LumpedPort` the same way; or
- a `ModalPort(target_neff=…)`, which supports the argument as API but holds propagating modes only.

Hollow guides keep the largest-β² rule (they have no conductor to give a quasi-TEM index). A terminal group keeps `presolveNeff`, and
the two use one computation if they can.

**R-emi13-2 — Say what was taken.** When the mode taken differs from the target by more than a stated fraction (start at 10 %, and say
what the cases measure), the run warns naming both indices and the mode's type. `explain` shows the target and where it came from.
A face where kernel A cannot solve (no conductor, a material it does not model) keeps today's rule and says so.

**R-emi13-3 — A degenerate pair split.** On EMerge, a hollow port of two modes calls `align_modes()` with each mode's polarity-line
direction, in mode order, before its modes are fixed. The square-guide and equal-cutoff refusals are lifted for EMerge setups and stay for
any solver that cannot do this (say which). `hollow_mode_signs`' check stays: each mode must still be largest on its own line, and is still
signed by it. If the `.hzn` reading can describe a round guide's TE11 pair, it is covered too; if not, say so.

**R-emi13-4 — Docs and census.** `em-setup.md`'s wave-port section (how a port's mode is chosen, the warning, square guides on EMerge),
the Hertzon compatibility census if a refused file now converts, and the RESOLVED entry with the spike's numbers.

## Gates

1. **Top grounds.** A grounded coplanar through line with top-layer ground at both faces, on EMerge: the port's mode impedance within 2 % of
   kernel A's, |S21| within 0.1 dB of a uniform line's loss and |S11| below −30 dB; at HEAD the same model takes the parallel-plate mode
   (record its numbers). Category=Benchmark if over ~5 s, skipped without EMerge.
2. **Silicon.** The PWL-2 Q5 line at 5 GHz: S11 of the uniform line below −30 dB where HEAD reads −0.76 dB, or, if the target cannot be
   held at that frequency, the warning of R-emi13-2 and the reason recorded.
3. **A square guide.** An empty square guide of two modes, through: each port's mode carries a y- or x-polarised wave into the same mode at
   the far port (|cross terms| ≤ 1e-3) and |S21| of each mode follows e^(−jβL) to 1e-3. The two-mode guide of HZN-20 is unchanged.
4. Every existing EMerge wave-port test (EMW, HZN, wall ports) passes unchanged; a test that moves names the reason in the RESOLVED entry.

## Out of scope

Choosing modes on Palace or openEMS (Palace's own port chooses as EMerge's does, and its fix is another brief), modes of more than two
per hollow face, and asking the user for a target index (it is computed, never typed).
