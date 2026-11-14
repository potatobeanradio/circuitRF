# Brief PAA-2 — the array engine, and the record's port positions

**Series:** PAA-0 … PAA-10 · **Tag:** `R-paa2-<n>` · **Depends on:** PAA-1 · **Design note:** §2, §3, §6.1, §6.2, §10
**Area:** new `src/Engine/Array/`; `src/Design/Em3d/` beside `PortFarFieldSet`; `src/Design/Layout/Em/EmResultRecord.cs`;
`src/Engine/RESOLVED.md`, `src/Design/RESOLVED.md`.

## The gap

The co-simulation can sum the patterns of ports that were solved; it cannot form the pattern of an element that was not.
Nothing computes an array factor on a two-dimensional lattice, re-references an element's pattern to its own position,
extends a sub-array's coupling to a larger array, or forms each element's active reflection. And a result record does not
say where its ports are, so a sub-array's ports cannot be placed on a lattice without the user saying so.

## Requirements

**R-paa2-1 — Lattice.** `ArrayLattice` in `src/Engine/Array`: rows, columns, pitch (metres), `rect` or `tri`, the
positions from the array's centre, the element numbering of D6, and an `Off` set. Pure: no record, no units.

**R-paa2-2 — The pattern sum.** `ArrayPattern.Sum(lattice, weights, elementPattern(s), grid, f)` returning Eθ and Eφ on the
grid, with the row and column phase tables of note §6.1 precomputed per (lattice, grid, f) and kept, so a call with new
weights does not evaluate an exponential. A separable weighting takes the O(D·(R+C)) path when it is detected; the
general sum otherwise. A `tri` lattice is two interleaved rectangular sums. Up to 64×64; the phase tables for a grid
are built for that grid only and released with it. The element pattern is one for every element,
or one per position class (if PAA-1 kept classes), chosen per element by its position.

**R-paa2-2a — Resolution (note §6.2).** Three evaluations, never one:

- **overview** — the record's grid, for the whole-sphere pattern and the `farfield` cubes, labelled with its step;
- **local** — the two principal cuts through the peak and a patch around the peak and around each sidelobe a metric
  reads, at a step no coarser than a tenth of the current beamwidth (estimated from the lattice, pitch, f and scan
  before the sum, then confirmed from the cut);
- **(u,v)** — the 2-D FFT of the lattice weights zero-padded to at least 8× the array in each direction (a `tri` lattice
  on its own skewed grid), sampled onto the visible circle.

At every point off the record's grid the element pattern is interpolated from it (bilinear in θ and φ, Eθ and Eφ
separately, magnitude and unwrapped phase) and the array factor is evaluated exactly. The overview grid is never used to
compute a metric.

**R-paa2-3 — Re-referencing.** An embedded pattern taken from port c of a record is multiplied by `exp(−j·k·û·r_c)` with
r_c that port's position, so it is referred to its own element (note §2.2). Done once per (record, port, frequency) and
cached with `PortFarFieldSet`'s arrays.

**R-paa2-4 — Terminated embedded patterns from any record.** `EmbeddedPatterns.Of(PortFarFieldSet, ports, f)` returns
the pattern of each requested port with every other port terminated in Z0. On a terminated (3D) record that is the stored
pattern; on a planar record (others shorted) it is formed through `PortFarFieldSet.Weights` from the waves of the
circuit "port k driven, every other port in Z0", which is `FieldCosimulation`'s own path. No second formula.

**R-paa2-5 — Coupling extension.** `ArrayCoupling.Extend(sSub, subLattice, lattice)`: each offset (Δrow, Δcol) of the
array takes S from the pair of the sub-array nearest its centre with that offset; an offset the sub-array does not hold
is zero; the diagonal is the centre element's S_cc (or its class's). Returns the matrix **in sparse form** (at most
(2R−1)·(2C−1) non-zeros per row for an R×C sub-array, ~330,000 entries at 4096 elements from a 5×5) and the measured
offset range, which the run's notes state. `Coupling=off` keeps the diagonal only. Nothing in the array path forms a
dense N×N matrix except at `exact`, where N is the record's port count.

**R-paa2-6 — Active reflection and power.** `ArrayWaves.Active(S, a)`: **b** = S·**a** (a sparse product), Γ_act per element (note eq. 3),
accepted power, and from `ArrayPattern` the radiated power and the power balance (note §2.4). An element with aₙ = 0
(switched off) has no Γ_act and reports none.

**R-paa2-7 — Metrics.** Beyond what `Em3dRadiation.Publish` already forms from a pattern (gain, directivity, realized
gain, polarization): beamwidth in the two principal planes through the peak, first sidelobe level, pointing error against
a requested (θ₀,φ₀), scan loss against boresight, and the grating-lobe margin (how far a grating lobe is from entering
real space at the requested scan, from the pitch and f alone). Each a function of the pattern and lattice, no circuit,
and each computed from R-paa2-2a's local evaluation, never from the overview grid.

**R-paa2-8 — Port positions in the record.** A new run writes each port's reference point in the layout's coordinates
(metres) into its `.cemr` (`EmResultRecord`), for Planar and 3D alike, from the same port geometry the run solved. An
older record reads as "positions not recorded" and is not rejected. `EmResultRecord`'s field-comparison and adoption
paths treat the new field as optional.

**R-paa2-9 — The lattice of a record.** `RecordLattice.Of(record)` fits the recorded port positions to a rect or tri
lattice (rows, columns, pitch, numbering) within a small tolerance, or explains why they are not one (a missing port,
two pitches, a rotation). With no positions recorded it takes `RecordRows/RecordCols/RecordDx/RecordDy` from the caller
and says that the numbering is assumed row-major.

## Gates

1. **The L2 identity.** On PAA-1's E5 fixture with the steering weights, `ArrayPattern` with each port's own embedded
   pattern (no array factor approximation) equals `FieldCosimulation.PointOf` to PAA-1's identity tolerance (≈1e-9).
2. **The array factor.** For isotropic elements on a 1×N line it equals the Chain Budget Steering bench's closed form to
   1e-12; on 32×32 and 64×64 rect lattices the row/column path equals the direct sum to 1e-12; a `tri` lattice equals its
   direct sum; the (u,v) FFT equals the direct sum at the same (u,v) points to 1e-12.
3. **L1 against the reference.** From the E5 fixture, the predicted E6 pattern and Γ_act are within PAA-1's tolerances of
   E6's own L2.
4. **Coupling extension.** Extending E5 to 5×5 returns E5's own S for every offset it holds from the centre pair; a 9×9
   has zeros beyond ±4 and says so.
5. **Positions.** A new planar run of a 2×2 fixture of short stubs (one frequency, under ~5 s) records four positions that `RecordLattice.Of` reads as 2×2 at the
   drawn pitch; an older fixture without them reads as "not recorded".
6. **No exponential per call.** A counter on the phase-table cache: a second `Sum` with new weights builds no table.
7. **Resolution at size.** On a 64×64 array of E5's centre pattern steered to 0° and 45°: beamwidth, first sidelobe
   level and pointing error from the local evaluation are within 0.02°, 0.05 dB and 0.02° of a direct sum on a uniform
   0.05° grid with the element pattern sampled exactly; the same metrics read off the 1° overview grid are shown in the
   completion note to miss, which is why they are not read there. The interpolated element pattern's error against an
   exact sample is recorded.
8. **Sparse at size.** Extending E5 to 64×64 stores at most 81 non-zeros per row; `Active` on it allocates no N×N array
   (an allocation counter, not a time).

## Not here

The component (PAA-3), units and expressions (PAA-3/4), any UI.
