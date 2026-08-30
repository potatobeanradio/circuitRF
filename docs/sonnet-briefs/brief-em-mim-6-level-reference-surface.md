# Brief MIM-6 — the level reference surface: a 0.2 µm plate gap must not solve as 3.2 µm

**Problem — MIM-2's FINDING 1, verbatim from `src/Ui/RESOLVED.md`.** `PlanarExtractor` places every
conductor level's zero-thickness sheet at the BOTTOM of its stackup band and absorbs the band's own
z range into the dielectric ABOVE it. Both rules are right for everything that came before — they
are what makes a microstrip's height come out as the substrate thickness. Between two capacitor
plates they are wrong: the lower plate's full metal thickness lands inside the gap, so the shipped
MIM technology's levels extract at z = 100 / 103.2 / 106 µm and the solver sees a 3.2 µm plate
separation where the process states 0.2 µm — 16× — with the whole 3.2 µm carrying the capacitor
dielectric's εᵣ. Not fixable by authoring: `TechValidation` requires positive thickness on every
band, and Metal1's sheet is pinned by the microstrip case.

**The shape.** A conductor entry learns which SURFACE of its band its sheet sits on, and the
absorption direction follows the choice — that pairing is what keeps every sheet on an interface of
the medium by construction, which `PlanarProblem.CanSolve` requires:

- `StackupLayer` gains an optional per-Conductor field (say `SheetAt`: `Bottom` / `Top`) — additive,
  nullable, **no `.ctech` FormatVersion bump**, meaningless on non-Conductor entries; exactly the
  `Fill`/`SpanFromLayer` pattern already in `TechModel.cs`.
- **`Bottom` (and unset) is today's behaviour, bit-identical**: sheet at the band's bottom, band
  absorbed into the dielectric above.
- **`Top`**: sheet at the band's top, band absorbed into the dielectric BELOW. On the MIM starter,
  `Metal1 = Top` puts the levels at 103 / 103.2 / 106 µm: the plate gap is the 0.2 µm dielectric
  alone, and the region under Metal1 is GaAs extended to 103 µm.
- The extractor's notes already print every level's z; they now name the surface it sits on too.
- The `.ctech` editor's conductor rows gain the selector (the `IsGroundReference` checkbox pattern
  in `StackupLayerRowViewModel`); `TechnologyMerge` and persistence round-trip it.
- The shipped MIM technology (`.ctech` AND `StarterTechnologies.MmicGaAsMim()`, held together by the
  existing field-by-field test) sets `Metal1 = Top`. **The plain starter is untouched.**

**Two consequences to state, not hide.** (1) On the MIM technology a Metal1 microstrip's EM
substrate becomes 103 µm of GaAs instead of 100 — a deliberate ~3% height shift bought against a 16×
capacitance error; `MimCapacitorTests.AMetal1Microstrip…` asserts 100 µm on both technologies today
and is re-pointed deliberately, with the number in the write-up. (2) `SubstrateResolver` (the
closed-form microstrip path) sums dielectric thicknesses and is DELIBERATELY untouched — decide with
a measurement whether the ≤ one-metal-thickness discrepancy between its height and the EM
extractor's is worth teaching it the field, and record the decision either way rather than silently
diverging the two.

Read first: `src/Design/Layout/Em/PlanarExtractor.cs` (`BuildStack`, `BuildMediumStack`, the level-z
assignment); `src/Design/Layout/TechModel.cs`; `src/Ui/Layout/StackupLayerRowViewModel.cs`;
`tests/Ui.Tests/Em/MimCapacitorTests.cs` (the z assertions this re-points); `src/Ui/RESOLVED.md`
§MIM-2 (finding 1, and the finding-2 RETRACTION — the accuracy question this brief deliberately does
NOT answer).

## Milestones

1. The field, persistence round-trip, merge, and the editor selector; `Bottom`/unset provably
   changes nothing (an extraction-level bit-identity assertion over the existing fixtures).
2. The extractor honors `Top`: level z, absorption direction, region table, notes. Gate: on the MIM
   starter the extracted levels are 103 / 103.2 / 106 µm, the between-plates region is 0.2 µm of
   εᵣ 6.8, and the region below Metal1 is GaAs to 103 µm.
3. The shipped MIM technology opts in; `MimCapacitorTests`' z and region assertions re-pointed; the
   airbridge-post and substrate-resolution assertions re-measured and their new numbers recorded.
4. The user docs' MIM accuracy callout (`stackup.md` #mim-accuracy) loses its separation caveat and
   says what the run now models — leaving the raw-solve/port-discontinuity caveat, which is MIM-4's.

## Must NOT

- Change any default: an unset field is today's extraction, bit-identical, on every technology.
- Touch `src/Engine` (levels' `ZM` is already arbitrary there) or kernel A's cross-section path
  (it models real thickness and has no sheet to place).
- Claim a capacitance ACCURACY — a 0.2 µm gap against micron cells is exactly MIM-3's unmeasured
  regime. This brief fixes geometry; MIM-3 measures whether the numerics survive it, and **must run
  its physics tier only after this lands**, or it validates the wrong regime.

## Gates

Milestones 1–3 as tests; `dotnet test tests/Ui.Tests` and `tests/Firewall.Tests` green;
`tests/Engine.Tests` untouched. Write-up in `src/Design/RESOLVED.md` (extractor) and
`src/Ui/RESOLVED.md` (editor/starter); `docs/design/mom-engine.md` §10.12's finding-1 paragraph
corrected in place with a `> Built at MIM-6` note.
