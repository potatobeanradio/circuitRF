# Brief MIM-8 — the cross-level fill, at the cell/separation a real MMIC meshes at

**Status: the shipped MMIC technology's own MIM capacitor cannot be EM-simulated on it.** Not
"reads high", not "loses accuracy" — a 1.0838 pF series capacitor extracts as **0.002 pF**, an open
circuit. Measured end to end on a user's design, 2026-09-15, single point at 2.5 GHz:

| run | levels | \|S21\| | series Z | C implied |
|---|---|---|---|---|
| user's `.cem` as found | Metal1, Metal2 | −46.3 dB | 1017 − 20532j Ω | 3.1 fF |
| same, `MIM Metal` added | Metal1, MIM Metal, Metal2 | **−49.5 dB** | 1353 − 29744j Ω | **2.1 fF** |
| the design intent | — | ≈ 0 dB at 2.48 GHz | ≈ 0 + 1j Ω | 1.0838 pF |

The second row is the one that matters: putting the plate in the run made the answer **worse**, and
the structure is a 3.8 nH spiral in series with that capacitor, which should be a series resonance
at 2.48 GHz. The user saw a flat open across the whole band and could not tell why.

**This is not a new discovery — it is MIM-3's own deferral, arriving.** `src/Engine/Mom/RESOLVED.md`
§MIM-3 *Not done, on purpose*: *"A de-embedded reading of the shipped MIM capacitor's own
capacitance … what still binds is MIM-3's MESH condition, and the shipped technology's default mesh
sits outside it (`ValidatedCellOverSeparation`). That is gap 3's business, reported as a note, and no
constant was moved here."* MIM-3 was right to stop there; it had measured a ratio of 12.5 (a 10 µm
plate pair) and 1.46× is a defensible thing to report. **Real artwork is not at 12.5.** The user's
run reports `CELL/SEPARATION = 26.1`, where §MIM-3's own ladder says the extracted capacitance has
the **wrong sign**.

## Why no setting reaches it

The note the run already prints states the arithmetic and is correct:

> That pitch is `min(λ_g/CellsPerWavelength, width/MinCellsAcrossConductor)`, and only the first term
> responds to the frequency knobs — and here it would take Cells per wavelength ≥ 6,396 … At any
> usable setting the second term binds, and it is the metal's own width — so neither frequency knob
> acts here.

On that design the second term is 10.25 µm / 2 = 5.125 µm against a 0.2 µm film. Reaching ratio ≤ 5
needs pitch ≤ 1 µm, i.e. 11+ cells across every 10 µm line and ~1 µm cells over a 512 µm layout —
of order 70,000 unknowns for a two-port spiral, per frequency, alongside four calibration standards.
**The user-facing statement today is therefore "your capacitor is wrong and there is nothing you can
set to fix it", and no note can make that acceptable.** The film thickness is a process constant; the
pitch is set by the narrowest metal on the chip. Every MMIC on this technology is past the bound.

**The connection does not matter and neither does plate size.** `KIT_MIMCAP`'s shunt form is the
series form reflected with a backside via added under the bottom plate — the same two levels, the
same 0.2 µm, the same pitch rule. A larger plate meshes *coarser*, not finer. A shunt capacitor on
this technology fails exactly as the series one does.

## The correction

**The kernel is not the problem and MIM-3 proved that.** `Dcim.FitAtHeights` is flat in the
separation down to 0.05 µm (4.2e-3 at 0.05 µm against 6.4e-3 at 3 µm, ordinary interconnect
spacing). What loses four decades between ratio 1 and 20 is the **cross-level block of the fill** —
`PlanarFill.FillMultiLevel` integrating a peak of width `d` inside a cell of width `h ≫ d`.

That is the textbook near-singular potential integral, and it has known treatments. The brief is
measurement-first in MIM-3's own discipline: pick one, and make the §MIM-3 ladder hold out to
ratio 50.

1. **Peak subtraction (preferred).** A cross-level pair at separation `d` carries a term that is
   static to very high accuracy over one cell: subtract a closed form that is integrable in the
   plane — the two-parallel-rectangles potential integral at separation `d` has one — and integrate
   the smooth remainder with the quadrature already there. O(1) per entry, no ratio dependence, and
   the subtracted term is exact where the error is.
2. **Adaptive subdivision, keyed on `h/d`.** Simpler, and it is the honest fallback if (1) turns out
   to need an integral nobody has in closed form for the layered kernel. Cost is O((h/d)²) per
   cross-level entry, and cross-level entries exist only where two analysis levels **overlap** — the
   plate footprint, not the matrix. Budget it before building it: a 68 × 68 µm plate at 5 µm pitch is
   ~200 cells per level, so ~4e4 cross entries, and even 1,024 sub-samples each is ~4e7 kernel calls.
   If that measures cheap, it is a legitimate answer.

Either way the acting condition is local (`h/d` for that pair), so **a run with no thin film in it
must take the path it takes today**, unchanged.

## Milestones — a table in `HISTORY.md` before the next is started

1. **Reproduce §MIM-3's ladder on today's build**, unchanged, so the before/after share a harness.
   The rungs are cell/separation 1 / 5 / 10 / 20 / 50 at d = 0.2 µm, cross-level block error against
   forced-high quadrature, plus the extracted plate capacitance vs ε₀εᵣA/d at each. `Measure` and
   `Plates` in `tests/Engine.Tests/Mom/MimThinLayerTests.cs` already do this — reuse, do not restate.
2. **Cost the two treatments** before choosing. Fill time and cross-entry count on the real
   `examples/PDK PCells` spiral-plus-capacitor artwork, at its own mesh. The number that decides it
   is the fill's share of one frequency point, not a micro-benchmark of one entry.
3. **Build the chosen one.** Ladder re-run: extracted capacitance within 10 % of ε₀εᵣA/d **to ratio
   50**, and the cross-level block error no longer a function of the ratio in any recognisable way.
4. **The end-to-end gate, which is what this brief is actually for.** The shipped
   `examples/PDK PCells` capacitor, de-embedded, against ε₀εᵣA/d with a stated fringing allowance —
   and the user's structure: 3.8 nH spiral + 1.0838 pF MIM in series must show a series resonance at
   2.48 GHz ± 5 %, not an open. One frequency point is enough for the capacitance; the resonance
   needs a short sweep and is `Category=Benchmark`.
5. **The note is then re-scoped, not deleted.** `PlanarSolve.LevelSeparationNotes` and
   `PlanarLevels.ValidatedCellOverSeparation` state the *measured* range after the fix. If the new
   range is 50 rather than ∞, the note fires past 50 and says so with the new ladder's numbers.

## Must NOT

- Move `ValidatedCellOverSeparation` without re-measuring. The constant is a measurement's name, not
  a tolerance to widen — §MIM-3 says so and it is right.
- Change same-level or single-level fill arithmetic. §MIM-3 measured the same-level block at 3e-6
  and flat across the whole ladder: it is not implicated, and a run with no cross-level overlap must
  come out **bit-identical**. Gate that explicitly, the way MIM-7 gated the module-free stack.
- Loosen `AimAccuracyTests`' 8.7e-7 or any L9 gate to accommodate this.
- Add timing tests. Milestone 2 is a scratch harness in RELEASE, per the repo's own rule.

## If it is not cheap, stop and report — and then these two things are required anyway

Because the present state is worse than a stated limitation; it is an unstated one that ships inside
an example.

- **`examples/PDK PCells/README.md` §"The MIM capacitor" must say it.** It currently explains the
  four storeys of the stackup in detail and never says that an EM run of this cell does not return
  its capacitance. Say what to do instead: EM the passive interconnect, and carry `C` as a lumped
  element in the testbench — which is what `C` being a computed readout on the cell is *for*.
- **The refusal must become a refusal.** A run whose answer for the one structure the user drew is
  known to have the wrong sign should not write an `.s2p` that a schematic then consumes as a model.
  See `brief-em-run-severity-and-check.md` R-emsev-4.
