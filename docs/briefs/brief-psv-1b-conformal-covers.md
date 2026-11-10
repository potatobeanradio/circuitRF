# PSV-1b — a cover that starts on top of the metal

Series: `brief-psv-0-overview.md`. Depends on brief 1 (covers in the planar medium). Independent of
briefs 2 and 3, but it adds a control to the same stackup row as brief 2's `OpenWithLayer`: whichever
lands second puts its control beside the first.

## Why

Every solver fills a conductor band's empty space with a neighbouring dielectric (R-em-4a: the planar
extractor's `BuildMediumStack`, `CrossSectionExtractor.BuildRegions`, and the 3D generator's band
absorption in `Em3dLayoutSolids`, ~line 610). For a cover over the top metal that neighbour is the
cover, so the modelled cover is the metal's thickness plus its own, as one flat slab. That is right for
a solder mask, which flows into the gaps between traces. It is wrong for a conformal coat such as CVD
passivation, which is its own thickness everywhere: on top of the metal, down its sidewalls, and on the
substrate beside it.

Measured with Palace electrostatics (per-unit-length C, PMC end walls) on a 20 µm wide, 3 µm thick
gold line on 100 µm GaAs, under 1 µm of εᵣ 7 nitride:

| Cover model | ε_eff | Z₀ | Cover's effect on ε_eff |
|---|---|---|---|
| none | 7.597 | 75.98 Ω | — |
| true conformal coat, 1 µm everywhere | 7.762 | 75.17 Ω | +2.2 % |
| 1 µm slab on top of the metal, air beside it | 7.760 | 75.18 Ω | +2.1 % |
| absorbed band (today: a 4 µm slab) | 8.117 | 73.51 Ω | +6.8 % |

Today's rule overstates a conformal cover's effect about 3×. A flat slab that starts on top of the
metal, with air beside the metal, matches the true conformal coat to 0.02 %. The floor and sidewall
coating barely matter at these proportions. That model is still flat slabs (D5), so every solver can
represent it.

The planar kernel, which has no metal thickness, measured on the same line: absorbed +6.8 % ε_eff /
−3.2 % Z₀; a 1 µm slab over a 3 µm air band +1.0 % / −0.5 %. Against the conformal truth (+2.2 % /
−1.1 %) the second is the closer of the two on Z₀ (0.6 % error against 2.2 %).

## What to build

### R-psv1b-0 — reconcile the cross-section kernel first

On the absorbed fixture above, the cross-section kernel reads the cover's effect as +14.5 % on ε_eff
(7.357 → 8.421), against +6.8 % from both Palace and the planar kernel. Uncovered, it is 3.2 % below
Palace (7.357 against 7.597). Find out which number is wrong before this brief uses kernel A as a gate:
- Refine kernel A's mesh (`Refined(n)`) and see whether its covered value moves toward 8.117.
- Refine Palace (`Accurate` stopped at 3 adaptive passes) and see whether its values move.
- A 1 µm dielectric layer beside a 3 µm metal edge is the obvious suspect in kernel A: its interface
  segments run into the metal's sidewall.

If kernel A is wrong and the fix is small, fix it here, with its own test. If it is not small, stop and
report. The rest of this brief can still be built and gated on Palace and the planar kernel; say so in
the completion note.

### R-psv1b-1 — the field

Add `StackupLayer.Conformal` (`bool?`, null = false) on the `PresentWithLayer` pattern: additive,
nullable, no `.ctech` `FormatVersion` bump, written only when true, so every existing `.ctech`
round-trips byte for byte.

Meaning: this dielectric starts on top of the conductor directly below it, and that conductor's band
beside its metal is air. It changes only which neighbour fills that one band; nothing else about the
dielectric moves.

`TechValidation`:
- On a non-dielectric entry: a warning that it is ignored.
- On a dielectric with no conductor directly below it: a warning that it has no effect.
- When the conductor below has `SheetAt = Top`: that band is filled from below, not from this entry, so
  `Conformal` has no effect. Warn and name the conductor.

Carry it wherever `PresentWithLayer` is carried today (`grep -rn PresentWithLayer src` lists the sites:
`TechnologyMerge`, `StarterTechnologies`, the importers' round trip). No starter sets it (D4).

### R-psv1b-2 — every solver and every reader of R-em-4a

The rule is the same in each place: where the band would be absorbed into a `Conformal` dielectric
above it, fill it with air instead.
- **Planar**, `PlanarExtractor.BuildMediumStack`: the host lookup for a conductor band. The band
  becomes an εᵣ 1 region with no "taken as free space" note. The free-space trim at the top of the
  stack must not remove it, because it sits below the cover, not at the top of the stack.
- **Cross-section**, `CrossSectionExtractor.BuildRegions`: the cover's region starts at the conductor
  band's top. An air region fills the band. The kernel meshes the real metal, so this is the 3D model
  exactly.
- **3D**, `Em3dLayoutSolids`: the inner-band absorption (`_dielZ[above.Index] = (c.BottomM, …)`) is
  skipped for a `Conformal` dielectric. The band beside the metal is then air, as an outer band already
  is. This reaches the layout's 3D view, a 3D `.cem`, a layout placed in a `.c3d`, and the thermal
  solve through the one builder.
- **Everything else that restates the rule**: find each site rather than trusting this list —
  `TraceCrossSection`, `StackupScene` (the stackup drawing), `ComponentEmExtraction`,
  `PatternedDielectric`, `MmicStack`. `grep -rn "absorb" src/Design src/Render` is a start. A reader
  that disagrees with the solvers is a picture that lies about the run.

The planar run's cover note (brief 1) says when a cover is conformal: the height it reports then starts
at the metal's top.

### R-psv1b-3 — the stackup editor

A **Conformal** checkbox on a dielectric row, with one undo entry. It is enabled only where it acts: a
dielectric directly above a conductor whose sheet is at the band's bottom. Disabled elsewhere, with a
tooltip saying why. The row's height does not change when it is disabled (controls never shift).
Render it with `tools/UiSnapshot`, look at the PNG, and list it in the completion note. Render the
stackup drawing (`StackupScene`) with a conformal cover too: the cover must be drawn starting at the
metal's top.

### R-psv1b-4 — docs

`docs/user/src/reference/mom-engine.md`, the cover paragraph brief 1 wrote: replace the sentence
about a conformal coat reading high with what the setting does, and keep the measured numbers. The
stackup editor's own user page: one row for the checkbox. State what is true; no change history. No
DocGen (brief 4 regenerates).

## Gates

1. **Byte identity.** No starter and no shipped example sets the field: every `.ctech` round-trips
   unchanged, and every planar medium, cross-section region list and 3D solid list is unchanged.
   Brief 1's 13-example medium dump is the instrument for the planar half.
2. **3D reproduces the measurement.** The 20 × 3 µm line above, built from a LAYOUT with the
   technology's own stackup and `Conformal` set: its 3D problem has the nitride solid starting at
   z = metal top, with air beside the metal. Its Palace C is within 0.5 % of the hand-built
   "slab on top" `.c3d` (7.760 ε_eff). Without the field it reproduces the absorbed 8.117 within the
   same 0.5 %.
3. **Planar moves the right way.** On the same line, `Conformal` takes the planar cover effect on Z₀
   from −3.2 % to within 1 percentage point of Palace's conformal −1.1 %, and closer than without it.
   Assert both.
4. **Cross-section**, if R-psv1b-0 left kernel A trustworthy: with `Conformal` its covered ε_eff is
   within its own uncovered disagreement with Palace of 7.762.
5. **A solder mask is untouched.** The RO4350B starter with brief 1's 25 µm mask, field unset: brief
   1's gate 2 numbers do not move.

## Out of scope

- True conformal topography (sidewall and floor coating as their own solids). The measurement above
  says the flat model is within 0.02 % of it at these proportions; a 3D-only topography model is a
  separate question.
- A cover over a conductor whose sheet is at the band's top (MIM-6's lower plates).
