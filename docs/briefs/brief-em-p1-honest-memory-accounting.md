# Brief P1 — honest memory accounting for the planar solver

**Problem.** `SurfaceMesher.GuardCeiling`, `PlanarSystem.GuardCeiling` and `PlanarFill.GuardCeiling`
all quote `16·N²` bytes ("381 MB at the ceiling"). The machine sees roughly four times that.
Measured 2026-08-28 (scratch program, NumFlat 1.3.0): a 3,000×3,000 complex matrix is 137 MB and
`.Lu()` **added 530 MB** after a full GC. Reflection shows why: `LuDecompositionComplex` holds `l` and
`u` as two separate full `Mat<Complex>`, and `PlanarSystem` keeps `Matrix` alive beside them
(`src/Engine/Mom/PlanarSystem.cs:106`). Add the cached cores (+51% of a matrix) and the transient
m×m `P` (`PlanarFill.cs:664`). `CLAUDE.md` §7 already records "381 MB quoted against ~607 MB real"
as an open item; 607 was itself an underestimate.

The AIM report has the same class of defect: `PlanarAimReport.ApproximateBytes`
(`src/Engine/Mom/PlanarAim.cs:206`) omits the `SparseLU` fill-in (reported as
`PreconditionerNonZeros`, never added), CSparse's own CSC copy, and `_nearExact`
(`PlanarAim.cs:410`), which is only read by `FactorNear` and two diagnostics.

**This brief measures and reports. It changes no arithmetic.** P2 and P7 act on what it finds.

Read first: `src/Engine/Mom/CLAUDE.md` §7 (open items) and §8; `PlanarSystem.cs`; the three
`GuardCeiling` bodies; `PlanarAimReport`.

## Milestones

1. **Measure the resident peak of one dense frequency point** at N = 552, 1,980 and 4,933 (the
   hero, the 80 mm line, the ceiling), split into: matrix, L, U, cores, `P`, everything else.
   `GC.GetTotalMemory(true)` at each phase boundary plus `Process.PeakWorkingSet64` at the end. Record
   the table in `HISTORY.md`. Confirm or refute the 4× from the scratch measurement.
2. **Measure the same for one AIM frequency point** at N = 3,731 and at the 12,000 ceiling, split
   into near arrays, CSC copy, `SparseLU` factors, FFT buffers, stencils. Compare with
   `ApproximateBytes`.
3. **Make `ApproximateBytes` honest**: add the factor's non-zeros (`SparseLU` exposes them) and the
   CSC copy; rename it if it is no longer "approximate". Free `_nearExact` after `FactorNear` unless a
   diagnostic is registered (`NearExactAt` can read the CSC instead). Gate: the reported bytes are
   within 20% of the measured resident delta at N = 3,731.
4. **Make the three refusals quote the resident peak**, derived from ONE function
   (`PlanarSystem.ResidentBytes(n, cellCount)`), so the three cannot drift. The wording must say
   what the number is ("resident at the peak of one frequency point: matrix + factors + cached
   cores"). `tests/Ui.Tests/Em/EmCeilingRefusalTests.cs` asserts on the wording — update its
   expected sentence, do not stop asserting.
5. Close `CLAUDE.md` §7's "381 vs 607 MB — owner's call" item: replace that sentence with the
   measured number and a pointer to `HISTORY.md`.

## Must NOT

- Move `UnknownCeiling` or `AcceleratedUnknownCeiling`. That decision belongs to P7 (dense) and P8
  (accelerated), once the memory is what it will be.
- Change `PlanarSystem`'s factorisation. P7.

## Gates

- The `HISTORY.md` tables from milestones 1 and 2.
- `ApproximateBytes` within 20% of measured (a routine test at N ≈ 500 comparing the report to the
  GC delta is acceptable — it is a counter comparison, not a timing).
- `EmCeilingRefusalTests` green with the new sentence.
- Write-up in `RESOLVED.md`; `docs/design/mom-engine.md` §10.7's table gains a `> Built at P1`
  note giving the resident peak per row.
