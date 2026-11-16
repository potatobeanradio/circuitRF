# Brief PCH-10b — a receive array on the fast path that is not stepped, and fast ray couplings

**Series:** PCH-0 … PCH-12 · **Tag:** `R-pch10b-<n>` · **Depends on:** PCH-8b, PCH-10 · **Design note:** §5.4, §8.2
**Area:** `src/Engine/Envelope/EnvelopeChannel.cs`, `src/Engine/Envelope/EnvelopeDropReuse.cs`, `src/Engine/Array/PhasedArrayFastPath.cs`,
`PropagationChannelModel` (`.Ends.cs`), `src/Engine/RESOLVED.md`.

## The gap

PCH-10 solves a transmit side once across drops and replays its waves into the receive side alone; the receive side is still
stepped in every drop. Two things keep a drop of an array link expensive:

1. **A receive array on the fast path is stepped element by element.** PCH-8b's receive collapse puts each ray on the feed as
   one Norton source, exact, but the array's own circuit (every element and its channel cell) stays in the envelope run. Note
   §8.2 says a receive side that meets the fast-path conditions is a linear filter of its incident field and need not be
   stepped at all. Measured (src/Engine/RESOLVED.md, PCH-10): one drop's 1 µs envelope run into an array with linear channel
   cells costs 12 ms a step at 64 elements and 584 ms at 256, about N^2.8, and did not finish in 300 s at 1024 (5.3 GB). At
   1024 and 4096 elements a Monte Carlo over drops is impractical, linear cells or not.
2. **Building the per-ray element couplings is slow.** An instance end through a clustered delay line spends nearly all of
   its setup in `PropagationChannelModel.InstanceCouplings`: each element's pattern is evaluated from the EM record toward
   each ray, per product. A 2 × 2 array through CDL-C (~460 rays) took 19 s before the first step, against 0.15 s with one
   ray; at 64 × 64 elements with a 460-ray drop this dominates every drop.

## Requirements

**R-pch10b-1 — The receive array replaced by its feed.** When a receive array meets the fast-path conditions and the channel
is its only drive, an envelope run replaces the array by a one-port at its feed: the reflection Γ_F(f) the closed array
presents across the run's band (the fast path's closure, fitted or sampled as the envelope's frequency-defined one-ports
are), driven by the collapse's Norton source per ray. The array's elements and channel cells are not stamped. Applied only
where it equals the stepped array (gate 1); the run says whether it was taken and why not.

**R-pch10b-2 — Couplings once per pattern class.** The element field toward a ray is evaluated once per pattern class
(isolated, or each embedded class) and per ray, and the position phase applied per element, rather than the pattern per
element. Element patterns over a drop's ray directions are evaluated together where the record's interpolation allows.

**R-pch10b-3 — Cost recorded.** A drop's cost at 64, 1024 and 4096 receive elements, linear cells, before and after, in
`src/Engine/RESOLVED.md` (no timing test).

## Gates

1. **Equal.** A 16 × 16 receive array with identical linear cells through two rays and through CDL-C: the feed's envelope with
   the array replaced equals the stepped array's to the envelope's own floor (PCH-8b gate 1's), and the run's other cubes
   are unchanged.
2. **Counted.** The replaced run stamps none of the array's elements (a counter), and a reusing drop sweep of a 64 × 64
   receive array solves the receive side as a one-port.
3. **Couplings.** `InstanceCouplings` at 64 elements through CDL-C equals the per-element evaluation to 1e-12 and evaluates
   each pattern class once per ray (a counter).

## Not here

Nonlinear receive arrays (they must be stepped); beam tracking within a drop.

## Completion note (2026-11-15)

- R-pch10b-1 built: `EnvelopeChannel` replaces a fast-path receive array, its feed and its cells by a one-port presenting Γ_F at the feed
  (`PhasedArrayFastPath.FeedReflection`), for a noiseless run whose array transmits on no channel; equal to the stepped array to 4.2e-10
  (two rays) and 9.8e-11 (CDL-C). Gate 2's sweep runs at 32 × 32 in the routine tier (64 × 64 is 43 s).
- R-pch10b-2 was already met (one pattern evaluation per class and direction); gate 3 counts it. The slow setup the gap named was not the
  couplings but three pre-stepping element checks inverting the array's and the channel's stamps densely; they now skip both.
- R-pch10b-3: 64 / 256 / 1024 / 4096 linear elements 0.5 / 0.9 / 1.2 / 5.4 s a drop (from 6 s / 288 s / over 300 s / not run); nonlinear
  64 / 256 elements 3.7 / 56 s, 1024 over 300 s. `src/Engine/RESOLVED.md`.
