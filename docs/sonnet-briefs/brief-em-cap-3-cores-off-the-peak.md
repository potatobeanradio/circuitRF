# Brief CAP-3 — the cached cores off the peak of a frequency point

Series: `brief-em-cap-0-overview.md`. **Changes no answer.** Takes the cores' share of
`PlanarSystem.ResidentBytes` from a quoted ~6·N² bytes to near nothing during the factorisation, so
the memory a dense point needs is essentially the lower-triangle matrix alone (~8·N² after CAP-2).

## The gap

The frequency-independent geometric cores (`PlanarFillCores`, built once per sweep by
`PlanarFill.BuildCores*` and reused by every frequency's fill) stay resident through the
factorisation, which needs none of them. Two separate problems:

1. **The quote is far above the allocation.** `PlanarSystem.CoreBytes` is an a-priori figure for the
   old triangle layout (~6·N² bytes). The production `PlanarCoreLayout.Classes` holds a 4-byte
   translation-class index per ordered band pair (≈ 0.6·m², m ≈ N/1.95 cells) plus 112 bytes per
   class — measured 25 MB against a quoted 83 at N = 3,731. The ceiling is computed from the quote,
   so the over-quote alone costs capacity.
2. **What IS allocated is held across the factorisation** for no use, and grows as m².

## What to do

1. **Quote what will be allocated.** The class count is a property of the artwork, so the honest
   figure is known only once the classes are built — but the index size (4 bytes × ordered band
   pairs) is known from the mesh alone, before anything is filled. Split `CoreBytes` into that exact
   index term plus a bounded class term, and keep a stated worst case only for the class term.
   The ceiling, the refusal and `PlanarMemoryCheck` all follow from the one function.
2. **Take the m² index off the factorisation's peak.** Pick on measurement, in this order of
   preference, and record why:
   - **(a) No stored index:** derive a pair's translation class at fill time from the pair's
     relative offset and shapes through a lookup keyed on that geometry (memory O(classes), not
     O(m²)). Best if the per-pair lookup does not slow the fill by more than ~10 %.
   - **(b) Release before factoring, rebuild before the next fill:** exact, costs one index rebuild
     per frequency point. Acceptable if the rebuild is small against the factor time at large N
     (it is O(m²) against the factor's O(N³)).
   - **(c) Narrower index** (2 bytes when the class count fits): halves it, keeps it resident.
     Only if (a) and (b) both fail their speed bar.
   The class TABLE itself (112 bytes × classes) and the O(N) vectors (`VMoment`) stay.
3. **The AIM path and de-embedding standards share the cores** (`PlanarSolveContext`,
   `PlanarEntryCores`, the standards' own lazy cores). Whatever lifetime change is made must leave
   them correct; the AIM path is not a capacity concern here but must not regress.
4. **The fill's own transient peak** (the m×m scalar-potential matrix `PlanarFill.ScalarPotentialMatrix`,
   released before the factorisation) must stay below the factorisation's peak at every N — check it
   with CAP-2's halved matrix, since halving the matrix makes the fill's transient relatively larger.
   If it now governs, bound it in panels the way `PlanarFillMerged` already does.

## Gates

- **Bit-identical** S-parameters on the §10.7 hero, a multi-level via mesh, a merged mesh and a
  de-embedded run (the cores' values do not change, only their lifetime or lookup).
- **Allocation:** `CoreBytes`' new figure is ≥ what `PlanarFillCores` reports allocated on all seven
  fixtures P5 measured, and within 2× of it.
- **Resident at the factorisation:** measured, one point at N ≈ 15,000, recorded beside the new
  `ResidentBytes`; with CAP-2 in, the total is within 10 % of 8·N² + vectors.
- **Speed (scratch harness, recorded):** fill per point not worse than +10 % at N = 11,210.
- After CAP-2 and CAP-3, record the machine-derived ceiling on a 16 GB machine (the arithmetic, from
  `CeilingFor` at 16e9 bytes) and on the machine the measurements ran on.

Targeted tests: `tests/Engine.Tests/Mom/PlanarMemoryAccountingTests.cs`, `PlanarFillCostTests.cs`,
`PlanarFillTests.cs`, `PlanarBudgetTests.cs`, and the AIM and de-embedding classes the gate runs
through. Run only those.
