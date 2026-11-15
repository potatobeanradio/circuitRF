# Brief PCH-9 — picking the beam

**Series:** PCH-0 … PCH-12 · **Tag:** `R-pch9-<n>` · **Depends on:** PCH-8 · **Design note:** §6
**Area:** `src/Engine/Array/` (new `BeamSelection.cs`), `PropagationChannelModel`, `PhasedArrayReweigh`, the `steer()`
override, `src/Ui/PhasedArray`, `src/Render/PhasedArray`, `src/Cli` (`explain`), `src/Engine/RESOLVED.md`,
`src/Ui/RESOLVED.md`.

## The gap

Through a multipath channel the best beam is rarely the one pointed at the other end, and a second-best beam on another
path is what a link falls back to when the first is blocked. Nothing scores beams against a channel, and nothing lets a
user see the channel's paths beside an array's beams and pick one.

## Requirements

**R-pch9-1 — Codebooks.** Per array end, a list of steering directions. Default: a (u, v) grid at one beamwidth spacing
within the array's scan limit, steered through the array's own `steer()` and quantised by its `PhaseBits`.
`Codebook="file.csv"` gives directions.

**R-pch9-2 — Scoring.** Note eq. (2): the wideband effective channel through both beams, at the drop's channel at
`TimeOffset`, mean power over frequency samples across the signal band; array factors by `ArrayPattern.FactorOnce` over
the ray directions. `BeamSearch=exhaustive|alternating`, alternating by default past 10⁶ pairs.

**R-pch9-3 — `BeamSelect=auto`** (default when an end is an array whose weights are a `steer()`): the strongest pair.

**R-pch9-4 — The user's choice.** `BeamSelect=rank` with `BeamRank=n`: the n-th strongest distinct pair (a pair within
one beamwidth of a higher-ranked pair at both ends does not count). `BeamSelect=index` with `TxBeam=`/`RxBeam=`: a
codebook index or a direction `(θ, φ)` per end. `BeamSelect=manual`: the arrays' authored weights untouched.

**R-pch9-5 — Applied in memory.** The selected direction sets the `steer()` call's VARs or replaces its literals for the
run only, by PAA-9's write rule, through `PhasedArrayReweigh`, so a beam change does not re-elaborate. `auto`, `rank` and
`index` on weights that are not a `steer()` refuse with the reason and name `manual`.

**R-pch9-6 — The beam table.** Every scored (or visited) pair with its metric and rank, the pair chosen and why, and the
unconstrained bound (the dominant eigenvector of the channel's spatial covariance, phase-only) with the codebook's loss
against it. In the result and in `explain --analysis`. The notes say the selection used the linear pattern model.

**R-pch9-7 — The tool.** When the Phased Array tool's array is a channel end: the (u, v) map draws the drop's rays
(departure on a transmit array, arrival on a receive array) sized by power, and the codebook beams; a **Beams** list of
ranked pairs; clicking a pair or a ray writes `BeamSelect=index` with `TxBeam`/`RxBeam` on the channel, one undo step;
**Apply to array** writes the direction into the array's `steer()` VARs and sets `BeamSelect=manual`, one undo step; a
drop slider. Square glyph buttons; controls do not shift. UiSnapshot PNGs, looked at.

## Gates

1. **One ray.** A single-ray channel picks the codebook entry nearest the ray at both ends.
2. **Rank.** A two-cluster channel: rank 1 is the stronger cluster, rank 2 the other — not a neighbouring beam of rank 1.
3. **Index and manual.** `index` with a direction uses that direction; `manual` leaves every weight bit-identical to a run
   with no channel.
4. **No elaboration.** Scoring 64 × 64 pairs and running the chosen one makes 1 elaboration and the re-weighs the
   counters predict.
5. **Refusal.** `auto` on an array with explicit per-element phases refuses naming `manual`.
6. **Alternating finds the exhaustive best** on three committed CDL drops at 16×16, and the counter shows fewer pairs scored.
7. **Tool.** A click on rank 2 writes `TxBeam`/`RxBeam` as one undo step; undo restores; Apply writes the steer VARs.

## Not here

Beam tracking within a drop; codebooks of arbitrary weights; multiple streams.
