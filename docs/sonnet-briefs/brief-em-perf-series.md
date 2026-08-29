# Brief series — Kernel B (planar MoM) speed and memory

**Origin.** A review of the planar solver on 2026-08-28 (owner request: "advanced algorithms for
increased speed and improved memory so the MoM solver can solve bigger problems"). The review found
that what limits problem size is bookkeeping around sound numerics, not the numerics: the dense path
holds ~4× the memory its refusal quotes, the per-frequency LU (not the fill) dominates near the
ceiling and runs on one core, the AIM path re-integrates its singular cores at every frequency, and a
large share of all quadrature is spent on cell pairs that are exact translation copies.

**Each brief below is self-contained and small.** Do them in the order listed unless the dependency
column says otherwise. Every one of them must leave `dotnet test tests/Engine.Tests` green and must
not change any published s-parameter beyond the tolerance its own gates state.

| # | Brief | Kind | Depends on | Expected win |
|---|---|---|---|---|
| P1 | `brief-em-p1-honest-memory-accounting.md` | measure + wording | — | the refusal and the AIM report say what the machine sees |
| P2 | `brief-em-p2-cheap-memory-wins.md` | code, mechanical | P1 | −25% cores, −1 m×m matrix, standards' cores lazy |
| P3 | `brief-em-p3-multilevel-fill-scalability.md` | code | — | multi-level fill scales like the single-level one |
| P4 | `brief-em-p4-vector-block-moment-cache.md` | code | — | ~4× on core build and per-frequency vector remainder |
| P5 | `brief-em-p5-translation-class-memo.md` | code | P4 | 2.5× (hero) … 30× (tapers) on all cell-pair quadrature |
| P6 | `brief-em-p6-aim-frequency-independent-state.md` | code | P4 | AIM per-frequency build ~4× faster; time crossover ≈ N 1,000 |
| P7 | `brief-em-p7-symmetric-inplace-factorisation.md` | code, numerics | P1 | LU 5–10× faster, ~3× less resident at the ceiling |
| P8 | `brief-em-p8-aim-near-radius-floor.md` | measure + one knob | P6 | the over-refinement ladder that failed at N 13,967 |
| P9 | `brief-em-p9-adaptive-sweep-default.md` | decision | — | point count 5–10× on ordinary sweeps |
| P10 | `brief-em-p10-fanout-starvation.md` | measure | — | explains or rescues M2's 1.09–1.15× |
| P11 | `brief-em-p11-accelerated-static-capacitance.md` | code | P6 | the "always dense" calibration refusal goes away |
| P12 | `brief-em-p12-aim-bordered-vias.md` | code, the big one | P6, P11 | vias and multi-level under AIM |

**Conventions that bind every brief here:**

- **Write-ups go to `src/Engine/Mom/RESOLVED.md` (narrative) and `src/Engine/Mom/HISTORY.md`
  (every measured table). Do not add to `src/Engine/Mom/CLAUDE.md`.** If a sentence already in
  `CLAUDE.md` or in `docs/design/mom-engine.md` becomes false because of your work, correct that
  sentence in place with a dated `> Built at Px` note — the pattern the design note already uses —
  and add nothing else.
- **Measure before and after on the same three fixtures**, alone (not alongside other benchmark
  tests — HISTORY records the 2× distortion): the FR-4 hero (`PlanarLineFixtures.Fr4Line(20e-3, 10e9)`,
  N = 552), the 256 mm FR-4 line at 6 GHz (N = 3,731, HISTORY §12's top rung), and the 60 mm
  2.9 → 0.5 mm taper (`PlanarLineFixtures.Taper`, N = 1,891). Report per-frequency fill, factor and
  peak resident bytes (`GC.GetTotalMemory(true)` before/after, plus `Process.PeakWorkingSet64`).
- **Bit-identity is the gate wherever the arithmetic is unchanged; a stated tolerance wherever it
  is not.** Never loosen an existing gate to make a brief pass — say which gate and why in the
  write-up and stop.
- **No new timing tests in the routine tier.** A structural COUNTER (calls, entries, bytes) is the
  routine gate; wall clock goes in `HISTORY.md` and, if a test must carry it, under
  `[Trait("Category", "Benchmark")]`.
- **No native dependencies** without asking (root `CLAUDE.md`). Everything here is managed C#.
