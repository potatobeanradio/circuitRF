# circuitRF — RF passives: one plan per topology

**Status:** MWPD, the Wilkinson divider, with its unequal-split (`K`) and multi-section (`N`, to seven) variants; MGPD,
MBPD and MTPD, the Gysel, Bagley polygon and tee dividers; MBLC (to four sections) and MRRC, the branch-line and
rat-race hybrids; MCLC, MIDC and MCBL, the coupled-line coupler, interdigitated coupler and balun; the rings curved or
cornered (`Corner`); the gap, taper, cross and open-end sections (§1); MRADIAL, the radial stub; MCLINN and the N-line coupled
section; sites with an optional `Footprint`, and a route helper; MCRC, every coupled-resonator filter as one part of
stored geometry (§3.10); MLAD, the microstrip ladder, an
ordered list of sections (§3.11) · **Date:** 2026-11-16

Dividers, couplers and baluns on microstrip, each one built-in part that is defined once and read the same way by the
schematic, the layout, LVS and EM. Each part has its own subsection in §3.

## 1. The idea

**Every part is a plan.** A part is a short list of standard sections — straight lines, tees, curves and coupled lines —
joined at numbered nodes and placed in the part's frame. One model stamper (`MicrostripPlanModel`) simulates any plan,
and one PCell drawer (`PassivePlanPCells.Draw`) draws any plan, each section through the existing part's own model and
its own generator. A new topology is therefore only a new plan function: it writes no model and no drawing code, its
circuit and its copper cannot disagree, and LVS and EM read the specs it was planned from. That is what makes the set
one standard, generic design rather than six one-off parts. The rest of this section is how each piece works.

**A plan made of sections.** Each topology has one pure function in Core, `src/Core/Devices/Microstrip/<X>Plan.cs`, that
takes the part's specs and the substrate and returns a `PassivePlan`: the part's pin names, and a list of sections. A
section is an EXISTING microstrip part placed in the passive's frame — an MTEE (`PlanTee`), an MCURVE (`PlanCurve`),
an MLIN (`PlanLine`), an MMITER or MBEND corner (`PlanBend`), an MCLIN or a set of interdigitated strips
(`PlanCoupledLine`), N coupled strips (`PlanCoupledStrips`, §3.6), an MGAP (`PlanGap`), an MTAPER (`PlanTaper`), an MCROSS (`PlanCross`) — with the plan node each of its pins joins and a pose (where its pin 1 sits, a turn in quarters; a cross's centre, its own origin). Nothing in a plan is a new kind of line: it is the
vocabulary circuitRF already has, arranged. The one term that is not a part of its own is a line's open end
(`PlanOpen`), below. Every dimension is held on a 2 nm grid
(`PassivePlan.OnGrid`), so halves are whole database units and two pieces that meet in the plan meet to the last unit
in the artwork.

**One model for every plan.** `MicrostripPlanModel` stamps each section through that part's own model
(`MicrostripTeeModel`, `MicrostripCurveModel`, `MicrostripLineModel`, `MicrostripCoupledLineModel`,
`MicrostripMiterBendModel`, `MicrostripBendModel`, `MicrostripGapModel`, `MicrostripTaperModel`, `MicrostripCrossModel`, with their own validity
warnings; an open end is `MicrostripOpenEnd`'s capacitance on MLIN's or MCLIN's own line) into a `LocalAdmittanceBlock` on the plan's nodes and Kron-reduces the internal nodes away — the way
MTEE already stamps the arms it draws (`JunctionArms`).
The block is shared with MTEE for that reason. A topology therefore writes no stamping code at all.

**One drawing path from the same plan.** `PassivePlanPCells.Draw` draws each section through that part's own generator
(`MTeePCell`, `MCurvePCell`, `MlinPCell`, `CoupledLinePCells`, `MMiterPCell`, `MBendPCell`, `MGapPCell`, `MTaperPCell`,
`MCrossPCell`; an
open end draws nothing), turned and moved to its pose, and takes the passive's
pins from those parts' own pins. So the model and the layout are the same pieces by construction, not by two
implementations agreeing.

**Gaps, tapers and open ends.** Three sections the filters are built from: a ladder of series lines, gaps, tapers and
shunt stubs, coupled-resonator chains and coupled arrays. Each takes any widths and any pose, and numbers its nodes as
the others do. Tests: `PlanSectionsSParamTests` (each in a small plan is its parts wired by hand to 1e-9, the open end
as a capacitor; the stub below), `PlanSectionsPCellTests` (each draws its own generator's artwork) and
`MicrostripOpenEndTests` (the closed form against an independent solve; a gap's refusal and warning).

- **`PlanGap`, a series gap.** Two line ends of width W, S apart: `MicrostripGapModel` (Cg across the gap, Cp to ground
  at each end) and `MGapPCell`, which draws no copper, only the two ends, so the lines either side end exactly S apart.
  S ≤ 0 is MGAP's own refusal, a ratio outside its fit MGAP's own warning. Its two nodes are separate copper, so a plan's
  DC islands stop at it. End-coupled resonators and DC blocks are lines and gaps.
- **`PlanTaper`, a linear taper.** W1 to W2 over L: `MicrostripTaperModel`'s cascade of MLIN sections and
  `MTaperPCell`'s trapezoid.
- **`PlanOpen`, an open end.** A line end with nothing attached holds charge in the field fringing past it, and without
  it every stub and resonator in a plan would resonate high. No term for it existed. MGAP's shunt capacitance levels off
  past S/W = 1, but as a clamp of its even-mode fit rather than an open end: 8.9 fF at W = h = 508 µm on εr 3.66, where
  the gap's own electrostatic reference (`testdata/microstrip-gap`) puts 13.4 fF in one open end, and only for W/h 0.5
  to 2. So `MicrostripOpenEnd` is Kirschning, Jansen and Koster's end extension Δl
  ([10.1049/el:19810088](https://doi.org/10.1049/el:19810088), §7), a closed form in W/h, εr and the static εeff, stated
  for 0.01 ≤ W/h ≤ 100 and εr ≤ 128 (12.85 fF in that case). Δl is geometry and is evaluated once; the end stamps as the
  capacitance that length of line holds, C = Δl·√εeff/(c·Z0), at MLIN's dispersive Z0 and εeff at each frequency. On one
  strip's end node that C goes to ground. On the two end nodes of a `PlanCoupledLine` pair, given S, each mode takes the
  same Δl form at its own static εeff, and its own C from MCLIN's Kirschning–Jansen modes: Ce to ground at each end and
  (Co − Ce)/2 between them, so the pair driven alike sees Ce and driven opposite Co. On 20 mil RO4350B a 50 Ω end is
  0.217 mm, and it moves a quarter-wave stub's null from 2.4253 to 2.3969 GHz, where the bare stub 0.217 mm longer has
  it, to within 0.1 % of the shift. A stub's open end has a near-zero self admittance at resonance and the local block
  does not pivot, so a plan numbers it first of its internal nodes, as MCBL does.

**Specs are the stored parameters; geometry is a readout.** A part stores what a designer specifies — Z0 and F, and where
the part has them N (sections), `Coupling` or `ZL` — plus the few layout choices no spec decides (`Gap`). Widths, lengths and radii
are derived on the technology's substrate, every time, by the plan. The Properties editor shows them
(`ParameterEditorViewModel.DividerReadout.cs`, which calls the plan itself), and they are never stored, so a design moved
to another board re-derives its lines rather than keeping the wrong ones. This is MKLOPF's shape (Z1/Z2 stored, widths
derived), not MLIN's (W stored).

**Lumped parts stay ordinary parts on pins.** A topology that needs a resistor, a capacitor or a via has a pin pair for
it and the part is placed on those pins in the schematic, with its own land pattern. It is then a real device to LVS, the
BOM, yield and the layout: a Wilkinson's isolation resistor is a resistor of 2·Z0 on `R2`–`R3`, and Update Layout from
Schematic lays its 0402 across the ring's gap because `Gap` is the land pattern's pin span. A PCell has no sub-instances
(`PCellResult`), so a resistor inside the part would need a contract change and would hide the part from all of these.
Each such place is a plan's *site* (§3.7), which the readout lists and Place Site Parts fills.

## 2. Why a standard pattern and not one-off parts

- **Consistency.** Every topology has the same parameter style, the same readout, the same pins-for-lumped-parts rule
  and the same refusals (`device.<topology>.*`), so a user who has placed one has learnt them all.
- **Less code per part.** A new topology is a plan function and registration rows (§4). The stamping, drawing, pin
  handling and readout plumbing exist once.
- **LVS and EM read the same parameters.** The generated cell carries the specs it was drawn from, so LVS compares
  Z0, F and Gap against the schematic (an F mismatch is one `lvs.property.mismatch`), and the EM solves the copper the
  model describes.
- **Tested against hand-built parts.** Because a plan IS existing parts, each composite has an exact oracle: the same
  sections wired by hand as explicit MTEE/MCURVE instances must give the same S-matrix to 1e-9
  (`WilkinsonSParamTests.TheDivider_IsItsSectionsWiredByHand`). Its numbers are checked against the closed form
  (`WilkinsonPlanTests`), its layout through Update Layout and LVS (`testdata/schematic-to-layout/F8-wilkinson-divider`),
  and its board against Planar MoM, EMerge and Palace (`examples/Dividers and Couplers`).

## 3. The parts

### 3.1 MWPD, the Wilkinson divider

Pins `1` (input), `2`, `3` (outputs), `R2`, `R3` (the resistor's ends). The plan: an input tee whose branch is pin 1, two
arms of √2·Z0 (a straight run on the tee, then a 180° MCURVE) coming back face to face across `Gap`, and an output tee at
each arm's end whose branch leaves outward to pin 2 or 3; the output tee's inner end is R2 or R3. Widths are MLIN's at F
(its dispersive Z0); each arm is λg/4 at F in MLIN's dispersive εeff, measured between the junctions' reference planes,
which fixes the curve radius. Each port pin sits one 50 Ω width of line beyond its junction.

| Parameter | Stored | Meaning |
|---|---|---|
| `Z0` | yes, Ω | Port impedance. Arms √2·Z0, isolation resistor 2·Z0. |
| `F` | yes, Hz | Centre frequency: each arm is λg/4 here. |
| `Gap` | yes, m | The isolation resistor's pin span; the ring closes across it. 1.35 mm is an 0402 at nominal density. |
| `SignalLayer`, `GroundReference` | yes | The usual layer choices; empty follows the technology. |
| `K` | yes, 1 | Split ratio, P3/P2 = K² (§3.1). |
| `N` | yes, 1 | Sections, 1 to 7; the pins are 3 + 2·N (§3.1). |
| `Band` | yes, 2 | f2/f1 of the multi-section design, centred on F. |
| `Corner` | yes, a name | `Curve` (the default), or `Miter` / `Square`: the ring a rectangle of MMITER or MBEND corners (§3.4). |
| W, arm W, L, ring radius, isolation R | readout | Derived by `WilkinsonPlan.Build` on the substrate; for a variant, every line and resistor. |

Refusals: a non-positive spec, an impedance no width reaches on the substrate, and a quarter wave too short to make a
ring (high F or a wide Gap).

#### The variants: the same part, the same pieces

Both variants are MWPD with a parameter, not new parts. At `K` = 1, `N` = 1 the plan is the equal split's to the last bit
(`WilkinsonPlanTests.TheEqualSplit_IsUnchangedBitForBit` fingerprints every number of it, taken before the variants
existed), so no placed divider moves.

**What they reuse unchanged.** Every variant is still MTEE and MCURVE sections: no new section kind, so
`MicrostripPlanModel` and `PassivePlanPCells.Draw` did not change. A transformer is an output tee's branch; a further
section is a tee's through arm; a junction with no metal past the through line is MTEE's straight-line case. The
composite-against-its-parts test is the same function over any plan. LVS reads `K`, `N` and `Band` off the artwork like
`Z0` and `F` (a `K` or `Band` mismatch is one `lvs.property.mismatch`). The readout prints the plan's own list of lines and
resistors.

**Unequal split, `K`** (Parad and Moynihan). Arms of Z0·√(K(1 + K²)) to port 2 and Z0·√((1 + K²)/K³) to port 3 leave the
arms' ends at K·Z0 and Z0/K, so they sit at equal voltage while carrying powers in the ratio K²; a λg/4 transformer of
Z0·√K and Z0/√K on each output tee's branch brings them back to Z0, and the resistor is Z0·(K + 1/K). The arms' εeff
differ, so their quarter waves differ in length. The plan keeps one curve radius (so R2 and R3 still face each other
across `Gap`) and takes the difference up in the input tee's two through lengths, which puts the gap's centre a little
off the input's axis. At K = 1 the transformers are Z0 lines and are left out, which is what keeps the equal split
bit-exact; the cost is a 90° step in the outputs' phase between K = 1 and a K just off it.

**Multi-section, `N` = 2 to 7** (Cohn). The even-mode half is a chain of quarter waves from 2·Z0 to Z0, made equal-ripple
over the band f1 = 2F/(1 + Band) … f2 = 2F·Band/(1 + Band) and symmetric (z_k·z_(N+1−k) = 2), so one unknown, z_1, is
found by bisection where the reflection vanishes at the first Chebyshev zero, cos θ = cos θm·cos(π/(2N)), θm = π/(1 + Band).
The odd-mode half is a short at the input, the same sections, and half of each resistor to ground at every junction; the
resistors make that half's admittance exactly Z0⁻¹ at the even mode's own zeros (θ1 and, by the lines' symmetry,
π − θ1; for N = 3 also π/2), N real equations in N conductances solved by Newton's method from all ones. Matched in both
modes there, every port is matched and the outputs isolated. The tests hold the N = 2 solve to an independent closed
form (the two-section transformer's z_1 = √(A + √(A² + 2)), A = 1/(2·tan²θ1), and the two resistors from the odd-mode
match), and the N = 2 to 7 solutions on ideal TLIN lines to |S| < 1e-9 at every zero. Above three sections the
bisection has more than one unknown, so `WilkinsonPlan.ChebyshevSections` solves the same two conditions for any N: the
⌊N/2⌋ free impedances by Gauss–Newton on the reflection at the Chebyshev zeros below π/2 (with the symmetry each zero is
one real condition, and having all of them makes the transformer the equal-ripple one), and the N conductances by
Newton's method. A plain start fails for four or more sections in narrow bands, where the zeros crowd together near π/2,
so both are continued from f2/f1 = 10, where they converge from evenly spaced impedances and resistors of 4, to the band
asked for in 5 % steps, each step starting from the last one's answer. N = 2 and 3 keep the bisection, so their numbers
and fingerprints stay bit for bit; the general solve gives them to eleven places. Below f2/f1 = 1.1 the odd mode's
equations are nearly dependent (seven sections lose the solution at 1.05), so four or more sections there are refused
(`device.wilkinson.sections-unsolved`). The ring is the section next to
the input; each further section is a pair of straight lines run on from the ring's output tees, parallel either side of
the gap's centre line, and every junction is a tee whose branch reaches the gap's edge. Resistors are numbered from the
outputs, as the paper numbers them: resistor 1 on R2–R3, resistor k on `R2_k`–`R3_k`.

**What they needed beyond the pattern.**

- **Per-arm planning.** The equal split plans one arm and mirrors it; an unequal split plans each arm and balances them.
- **A pin count that follows a parameter.** MWPD's symbol is variadic on its PIN count, 3 + 2·N, the way the Combiner's
  follows its N: `EditableSchematic.PortCount` reads a plain-number `N`, and `SymbolPortDefs.For` and
  `BuiltInSymbols.Primitives` answer any other count, the one-argument default included, with the five-pin divider, so
  every caller that does not know about N keeps getting what it always got. `GeneratedCellStore` checked a generated
  cell's pins against the symbol's default list, which refused the seven-pin cell; it now asks the symbol at the
  generated pin count, which a fixed symbol ignores. `InstanceNetContract` states the plan's pin count, so a short
  `.cnl` line is refused.
- **A small solver inside a plan.** The multi-section impedances and resistors have no closed form for N = 3, so
  `WilkinsonPlan.BroadbandSections` bisects and runs a 3 × 3 Newton, and above three a continued Newton of up to 7 × 7
  over about fifty band steps. It is still pure and well under a millisecond, fast enough for the readout.
- **The ground pour counts the part.** `GroundPourPlanner` covered only the line generators, so a divider beyond its
  lines (the second stage of a tree) was left off the plane.

**Why one part with `N` rather than a separate kind per section count.** Separate kinds keep a fixed pin list each, but
cost every registration row in §4 twice more and two more palette tiles for one topology. The variadic pins are a few
lines in three places that already handle the Combiner, the pin-contract fix applies to any generator, and the
catalogue and the `.cnl` contract keep one token. The cost: `N` cannot be an expression (an expression is the five-pin
divider in the symbol), and the catalogue states the one-section terminals, with the terminals note saying how `N` adds
pairs.

**N-way: not built, on purpose.** Wilkinson's N-way divider joins N resistors at one star point. Counting the board's
edge as a node (every output's line must reach its port), the input, the star point and the edge each connect to every
output: for three or more outputs that contains K3,3, which has no planar drawing, so any layout needs a crossing (air
bridge, second layer, or a resistor over a line). The two-way case is K3,2 and planar. The practical answer is a tree of
two-way dividers (the example's `FourWay` cell is three MWPDs) and, for three equal ways, an unequal MWPD with K² = 2
followed by an equal one.

**EM.** The example's dividers run on a coarse recipe: Planar with the edge mesh and the local grid off, EMerge and
Palace on their Draft presets with no mesh refinement, eleven log-spaced points from F/2 to 2F. It holds |S21| to the
model within 0.04 dB at F. The 3D isolation null is shallow and low mostly because of the mesh: at twice the tetrahedra
both nulls move up towards F and deepen, still short of the model's. The unequal split's 0.2 mm arm needs Planar at 2
cells across a conductor, and its 3D runs send port 2 0.5 to 0.9 dB too much, which EMerge's Standard preset moves by
only 0.09 dB; the cause is open. The studies are in `src/Design/RESOLVED.md`.

### 3.2 MBLC and MRRC, the branch-line and rat-race hybrids

Both are plans of the shared sections and nothing else, so they stamp through `MicrostripPlanModel` and draw through
`PassivePlanPCells.Draw` with no model or drawing code of their own: `BranchLinePlan` and `RatRacePlan` are the whole of
each topology, and the PCell generators are three-line wrappers.

**MBLC, the 3 dB branch-line (90°) hybrid**, one to four sections. Pins `1`–`2` and `4`–`3` are the two through lines;
driven at 1, 2 is through, 3 coupled and 4 isolated. Sections: an MTEE at each junction (four, or six for two sections),
a `PlanLine` for each series and shunt arm, and a `Z0` `PlanLine` feed to each pin, in line with the through line. One
section: series arms Z0/√2, shunt arms Z0. Two sections: series arms Z0 and shunt arms (1 + √2)·Z0, √2·Z0, (1 + √2)·Z0.
The synthesis method is Levy and Lind's and Muraguchi, Yukitake and Naito's (§7); these values come from its even/odd
conditions, in normalised admittances (match and isolation b = 2at²/(1 + a²), 3 dB b/t² = 1/√2, with t = 1), and the
circuit model checks them (`HybridSParamTests`: matched, isolated and 3 dB at F). Every arm is λg/4 at F between the
reference planes of the tees it joins.

Three and four sections keep the series arms at Z0, as two do, and solve the shunt arms from the same conditions
(`BranchLinePlan.ShuntAdmittances`). At F each half is a chain of quarter waves with an eighth-wave stub of j·y at every
junction; its A = D, it is matched when B = C, and the split is 3 dB in quadrature when |B| = 1/√2. Three sections
(y1, y2, y2, y1) have a closed form: B = j(y2² − 1) gives y2 = √(1 − 1/√2), and B = C then y1 = √2·y2·(√(1 + 1/√2) − 1),
shunt arms of 4.26·Z0 and 1.85·Z0. Four (y1, y2, y3, y2, y1) solve both conditions in closed form given y3 —
y2 = (1 − √(1 − y3/√2))/y3 and a quadratic in 1 − y1·y2 — and y3 is set where the even half's reflection is also flat at
F (ρ = Γ/(jT), real on the real axis, with ρ' = 0), by bisection on a central difference: shunt arms of 7.40·Z0, 2.55·Z0
and 1.99·Z0. A maximally flat design that frees the series arms as well (computed for comparison, not built) needs
higher outer arms still, 5.3·Z0 and 10.6·Z0 for three and four sections, for a few per cent more band, so the series arms
stay at Z0. Both keep port 3 a quarter period behind port 2.

The outer arms are the cost. 213 Ω in a 50 Ω system is 1.3 µm wide on 20 mil RO4350B, which the line model accepts
and no board draws, and its conductor loss takes 1 dB of the split; on 60 mil PTFE it is 70 µm, and there the
three-section coupler is matched and isolated to −43 dB at F. Four sections' 370 Ω is outside the line model and
refused by name; the tests take a 35 Ω system on the PTFE board, 259 Ω and 11 µm.

**MRRC, the rat-race (180°) hybrid.** Pin `1` is Σ, `4` is Δ, `2` and `3` the outputs. Sections: four MTEEs on a ring
of √2·Z0, `PlanLine` runs between them and two 180° `PlanCurve`s; each pin is the tee's branch, one W0 long. The pins
sit in ring order 2, 1, 3, 4, spaced λg/4, λg/4, λg/4 and 3λg/4 between reference planes.

| Parameter | Part | Stored | Meaning |
|---|---|---|---|
| `Z0` | both | yes, Ω | Port impedance; the arms' impedances follow from it. |
| `F` | both | yes, Hz | Centre frequency: every arm or spacing is λg/4 (or 3λg/4) here. |
| `N` | MBLC | yes | Sections, 1 to 4. |
| `Corner` | MRRC | yes | `Curve`, `Miter` or `Square`, as MWPD's (§3.4). |
| `SignalLayer`, `GroundReference` | both | yes | As for MWPD. |
| W and L of each arm; the ring's width, λg/4 and bend radius | — | readout | `BranchLinePlan.Build`, `RatRacePlan.Build`. |

**What the hybrids needed beyond the shared pieces, and why.**

- **`PlanLine`, a straight MLIN section.** A branch-line's arms and feeds are straight lines between junctions, and so
  are the rat-race's runs. It is one record in `PassivePlan.cs`, one case in `MicrostripPlanModel.Build` (the MLIN model)
  and one in `PassivePlanPCells.Draw` (`MlinPCell`), the cost §4 states for a new section kind.
- **A stub in each tee's branch.** MTEE simulates a tee whose branch ends at the through line's edge as a straight line
  with a pin on it, with no junction. Each branch-line tee therefore carries one W0 of its shunt arm, and the arm's
  `PlanLine` starts there. `BranchLinePlan.Layout` takes the dimensions directly, stub included, so the one-section plan
  on the `C2-branch-line` fixture's numbers with no stub is that hand-drawn network to 1e-9.
- **One span for the shunt arms.** Both through lines are straight, so a two-section coupler's three shunt arms share one
  span although their εeff differ; each takes the mean of their quarter waves (about ±1.5 % on 20 mil RO4350B).
- **A ring of quarter turns.** A section's pose turns only in quarters, so the rat-race's ports cannot sit 60° apart on a
  circle. The ring is two straight runs and two half-turns, three tees on one run and one on the other; closing it fixes
  the half-turns, π·(R − 0.2·W) = λg/4 − W0.
- **Two shared fixes.** `InstanceNetContract` now states a planned passive's net count (its plan's pins), which MWPD also
  lacked. The ground pour counts the planned passives' generators: without them it covered only the lines placed beside
  the part, which left the rat-race's half-turns off the plane.

Refusals: `device.branchline.*` (a non-positive spec, `N` other than 1 to 4, an unreachable width, an arm too short for
its tees' stubs) and `device.ratrace.*` (a non-positive spec, an unreachable width, a ring too small for F). Tests:
`HybridPlanTests` (closed forms, reference planes, every plan node one point, refusals), `HybridSParamTests` (each part
equals its sections wired by hand to 1e-9; the C2 fixture; the split, match, isolation and phase at F; DC), the
`F9-branch-line-hybrid` and `F10-rat-race-hybrid` schematic-to-layout fixtures, and an LVS clean run plus one spec
mismatch for each. The generators' versions follow MTEE's, MLIN's and MCURVE's, as MWPD's do.

**EM.** Palace refines the hybrids' mesh twice (`AdaptiveMaxIterations` 2): on the Draft mesh alone it read the
branch-line's |S21| 1.2 dB low. Refined, the 3D solvers still read the branch-line's through port 0.33 to 0.40 dB below
the model, and a series reactance per port in the model, standing in for the 3D lumped sheets, moves the split the
other way; the cause is open.

For the maintainer: the names MBLC and MRRC (microstrip branch-line and rat-race coupler) are circuitRF's own.

### 3.3 MCLC, MIDC and MCBL, the coupled-line parts

The coupled-line coupler (MCLC), the four-finger interdigitated coupler (MIDC) and the coupled-line balun (MCBL) are
three more plans on the same pieces. What they added to the shared structure, and why:

- **One more section kind.** `PlanCoupledLine` is `W`, `S`, `L` and a finger count. With two fingers it is an MCLIN,
  nodes in MCLIN's own order; with more, alternate strips are the two conductors of an interdigitated section, nodes
  listed strip by strip. It is one case in `MicrostripPlanModel.Build` and one in `PassivePlanPCells.Draw` (an MCLIN
  through `CoupledLinePCells`, each finger through `MlinPCell`); the balun's joining line is the hybrids' `PlanLine`. So
  the three parts wrote no stamping and no drawing code of their own.
- **One shared synthesis.** `CoupledLineSynthesis` turns a coupling and Z0 into Ze and Zo (Ze·Zo = Z0²), and Ze, Zo and
  F into `W` and `S` by inverting MCLIN's own Kirschning–Jansen modes at F — two nested bisections over the model's
  published range, so a pair outside it is a coded refusal rather than an extrapolation. The length is λg/4 in the mean
  of the two modes' εeff. All three plans call it; they differ only in which Ze and Zo they ask for: the coupler's from
  its coupling, the interdigitated coupler's adjacent pair from Ou's equations, the balun's from its match condition.
- **DC by copper island.** The Wilkinson's pins are one piece of copper; a coupler's two strips are not. The model now
  joins each section's strip ends and declares a tree of DC shorts per island, so a coupler passes DC along each strip
  and not across.

| Part | Stored | Derived (readout) | Pins |
|---|---|---|---|
| MCLC | `Coupling` dB (10), `Z0`, `F` | `W`, `S`, `L`, modal εeff, Ze, Zo | 1 in, 2 through, 3 coupled, 4 isolated |
| MIDC | `Coupling` dB (4), `Z0`, `F` | finger `W`, `S`, `L`; the pair's and the coupler's Ze, Zo | 1-4 as MCLC; `J1`-`J4` jumper landings |
| MCBL | `Z0`, `ZL` per output (200), `F`, `Gap` | `W`, `S`, `L` per section, Ze, Zo | 1 in, 2 and 3 out, `G2`, `G3` to ground |

**What the topologies needed beyond the shared pieces, and why.**

- **Finger jumpers on pins (MIDC).** The inner fingers are separate copper: on a board they are joined across the
  finger between by bond wires, which are parts with their own model, assembly and LVS. So the inner fingers' ends are
  pins `J1`-`J4`, and a jumper goes from pin k to Jk, as the Wilkinson's resistor goes on R2-R3. The model then has to
  give those pins something to stand on: Ou's equations describe the coupler as two conductors, so each finger stamps
  its own share of each mode's admittance (an outer finger the adjacent pair's own, an inner one its screened share),
  paired with its mirror finger in the other conductor. With ideal jumpers the shares add to Ou's pair to 1e-9; a real
  jumper carries its finger's current, and a missing one leaves a solvable, weaker coupler.
- **Grounds on pins (MCBL).** The coupled strips' outer ends go to ground through vias, which LVS, the BOM and EM must
  see, so they are pins `G2` and `G3` for an ordinary VIAGND each. Update Layout places each via beside its pin.
- **A layout choice the specs do not decide (MCBL's `Gap`).** The two sections are joined by a line, and the outputs face
  each other across it; its length is stored, like the Wilkinson's `Gap`, and the plan models it.
- **Kron order.** The balun's open end has a near-zero self admittance at F (a quarter-wave null), and the block
  eliminates without pivoting, so the plan numbers it to be eliminated last.

**What the board builds** (20 mil RO4350B, 2.4 GHz, a 75-100 µm gap rule). MCLC: 10 dB needs 115 µm, 8 dB 65 µm.
MIDC: 3 dB needs 138 µm fingers 69 µm apart, too tight; 4 dB needs 105 µm, so the part opens there. MCBL: 50 Ω on each
output needs −4.8 dB sections, under the model's S/h = 0.1, and is refused; the match fixes 1/Zo − 1/Ze =
√(2/(ZL·Z0)) whatever the impedance level, and 200 Ω on each output (−9.5 dB) needs 102 µm.

**Limits.** In microstrip the odd mode outruns the even mode: the coupler's directivity is about 11 dB at 10 dB coupling
(under 5 dB at 20 dB), and the balun's outputs are about 16° short of 180° (exactly 180° as TEM pairs). Ou's equations are
TEM, nearest-neighbour, with the pair's εeff. Ideal `Short` jumpers close a loop of DC shorts with the fingers, which the
DC solve cannot take (two ideal lines in parallel are the same case); a jumper with any resistance solves.

**EM.** The example's coupler and balun run on the same three solvers as the Wilkinson, but not on its coarse recipe: a
0.1 mm gap needs Planar's edge mesh (−10.72 dB coupling against the model's −10.49; −11.96 with it off) and refined 3D
meshes (EMerge Standard with three adaptive passes, Palace Draft with three refinement passes, both still about 1 dB
stronger than the model; their Draft presets read 3 to 5 dB too strong). Planar refuses the balun: its two ground vias
are 0.71 λ apart at the band top, past the via kernel's validated 0.1 λ. MIDC has no example cell yet: Planar cannot
cross a finger with a jumper, and the 3D view needs its bond wires drawn by hand.

### 3.4 Corners: MWPD and MRRC as rectangles

`Corner` = `Miter` or `Square` replaces each 180° MCURVE of a ring with two 90° `PlanBend`s and a straight `PlanLine`
between them: an MMITER at its own default chamfer (the Douville-James optimum for W/h, capped where its table ends) or
an MBEND with no chamfer. Every spacing still counts every piece. A corner's electrical length is its insertion phase
at F between two lines of the arm's impedance, from the corner's own model stamped as a one-section plan
(`PlanSection.LineLength`), as a length of that line; the straight runs are shortened by it, so the plan stays a pure
function of the specs and the substrate. MWPD: the input tee's line, 2·rise + across and two corners make λg/4. The
rectangle keeps the curved ring's height, the upper run across at the half-turn's top (from the curved plan of the
same specs), because the plan models no coupling between the arms: a rectangle as low as the tees allow would run
the two λg/4 arms side by side, about Gap + 2·W0 apart, where the curved ring keeps them a diameter apart. The run across
takes what is left, about (π − 2)·R less the corners; the arm needing more line rises and falls by half the
difference on top, so the gap stays on the input's axis for any K. MRRC: each end's two corners and side make λg/4 − W0. Every K and N has this geometry, so no
combination is refused; a ring with no straight run left is `ring-too-small`. A corner's pins are where its generator
puts them (`PlanBend.Arm`, rounded to 1 nm as both generators round), so pieces meet to the unit.

At `Curve` both plans are unchanged bit for bit (`WilkinsonPlanTests.TheVariants_AtCurvedCorners_AreUnchangedBitForBit`,
`HybridPlanTests.TheRatRace_AtCurvedCorners_IsUnchangedBitForBit`, taken before the corners existed). `Corner` is a name,
as a filter's `Response` is: the elaborator keeps it as text, Update Layout hands it to the generator as text, and LVS
compares it as a string (a mismatch is one `lvs.property.mismatch`). On the example board at 2.4 GHz a mitred corner
stands for 0.567 mm of the 70.7 Ω arm; with it taken out, the mitred divider's |S11| and |S23| at F are −52 and −36 dB
(curved: −48, −36; square corners, whose own reflection limits the match: −29, −34), and left in they would be −30
and −26 dB. Tests: the cornered plans
against their quarter waves and joined (`WilkinsonPlanTests`, `HybridPlanTests`), each equal to its MMITER, MBEND, MLIN
and MTEE sections wired by hand to 1e-9 and divided, summed and isolated at F (`WilkinsonSParamTests`,
`HybridSParamTests`), and `testdata/schematic-to-layout/F8d-wilkinson-mitred` through Update Layout and LVS.

### 3.5 Radial stub (MRADIAL), an element and a section

The ladder filters' radial and butterfly stubs and a bias tee's RF ground need a stub circuitRF did not have, so MRADIAL is
a new element rather than a plan: a sector of angle α whose narrow end is the feed's width `W`, open at `Ro` from its
apex, on one pin, the shunt node; `Sides` = 2 is a butterfly, two sectors back to back on that node. It is registered as
MGAP is (factory, net contract, registry, symbol, `MRadialPCell`, LVS kind, ground pour), and `PlanRadialStub` is a
section kind with one case in `MicrostripPlanModel.Build` (the element's model) and one in `PassivePlanPCells.Draw` (its
generator), so a plan can hang it on a tee's branch.

**The method.** Between the sector and the plane the dominant wave is cylindrical, and per unit radius the line's
series impedance is jωμh/(α·r) and its shunt admittance jωε·α·r/h. Open at the arc, the input impedance at the
narrow end's radius r_i = W/(2·sin(α/2)) is March's closed form,
Z = j·(η·h/(α·r_i))·[J₀(k·r_i)·Y₁(k·r_o) − Y₀(k·r_i)·J₁(k·r_o)] / [J₁(k·r_i)·Y₁(k·r_o) − Y₁(k·r_i)·J₁(k·r_o)], which
for a thin sector far from its apex is an open stub's −j·Z·cot(k·L). Fringing enters as an effective permittivity, MLIN's
(dispersion included) for a line as wide as the chord at the mean radius, (r_i + r_o)·sin(α/2), as March takes it, and
an effective radius, the arc moved out by that line's open-end length (Kirschning, Jansen and Koster). Loss is cheap: the
radial line's conductor loss per unit length and its loss tangent do not depend on r, so they are constant fractions δc,
δd of the series and shunt terms, and the equation keeps its Bessel form with k = β·√((1 − jδc)(1 − jδd)) and the level
scaled by √((1 − jδc)/(1 − jδd)); δc and δd are the equivalent line's own attenuations over β/2. The Bessel functions
are the Planar MoM engine's complex-argument ones, moved to RfCore so Core reaches the same code.

**Its limits.** One radial wave: a feed at the middle excites the sector's next symmetric mode once the arc is about a
guided wavelength, and the instance says so. MLIN's range is reported for the equivalent line. The impedance level is
the sector's arc width, as March's is, with no fringing along the straight edges: on 20 mil RO4350B at W = 1.1 mm,
Ro = 8 mm and 60° the equivalent line's own impedance is about a fifth below that level at the mean radius. That sets
the stub's slope near resonance rather than where it falls. The feed junction is not modelled, and a butterfly's two
sectors do not see each other.

**Tests.** No numerical example of either paper is reproduced here; the closed form is held instead to its own
equations, integrated by Runge-Kutta with loss, to 1e-7. The shunt stub's |S21| minimum falls at the closed form's zero
(3.86 GHz on the board above, two thirds of a straight quarter wave past the narrow end); a butterfly is two stubs to
1e-9; a `PlanRadialStub` on a `PlanTee` is MTEE and MRADIAL wired by hand to 1e-9 (`RadialStubTests`). The drawn
outline's flattened area is the sector less the triangle behind its narrow end, and a plan's stub lands edge to edge on
the tee's branch (`MRadialPCellTests`, which also places the defaults on the GaAs die). `F14-radial-stub` runs a sector
and a butterfly through Update Layout and LVS, and an `Angle` changed on the schematic is one `lvs.property.mismatch`.

**Possible enhancements.**

1. **The straight edges' fringing as an apex shift.** A wide strip's fringing widens it by about a constant, so moving
   the apex back by (W_eff − W_eq)/α, with W_eff the equivalent line's planar-waveguide width, carries it in the same
   Bessel form. Why: the level sets the stub's admittance slope, which is a stub filter's bandwidth, and it reads about
   20 % high on the board above.
2. **The planar-circuit model as a reference.** Giannini, Sorrentino and Vrba's mode expansion carries the sector's
   higher modes and its edges. Why: it would check wide angles and long arcs, where the single-mode closed form warns,
   without an EM run.
3. **An EM cell.** A coarse Planar MoM run of `F14-radial-stub`'s stubs. Why: the closed form is checked against its own
   equations and the layout against LVS, but not yet against a field solve.
4. **Grips for `Ro` and `Angle`.** Why: the other microstrip cells have them, and tuning a stub by dragging its arc is
   the usual gesture; the cell draws none yet.

### 3.6 The N-line coupled section (`PlanCoupledStrips`, MCLINN)

A strip of an interdigital or combline filter, or of any array of coupled lines, couples to both neighbours at once and
more weakly to the strips beyond them. A cascade of two-line MCLIN sections cannot say that, and Ou's equations
(MIDC) keep only neighbours. The N-line section is the one piece that does: N strips, each of its own width `W[k]`,
with a gap of its own `S[k]` below each but the last, all of length `L`, on the part's substrate.

**The method** (Tripathi and Lee, [10.1109/22.20041](https://doi.org/10.1109/22.20041); Paul,
[10.1109/9780470547212](https://doi.org/10.1109/9780470547212)). `MicrostripMultiLineModel` builds the cross-section as
MLIN's: the strips at H to H + T over a ground plane, the substrate below them, air above. `QuasiStaticSolver2D`, which
the broadside pairs already use, solves it twice: with the dielectric (the Maxwell C) and without it (C₀), and
L = μ₀ε₀·C₀⁻¹. Loss: R is MLIN's Wheeler form per strip, R_kk = 2·Rs·Kr/W_k, which gives a symmetric pair's modes
exactly MCLIN's conductor loss; G = ω·tanδ·εr/(εr − 1)·(C − C₀), MLIN's filling-factor form in matrix form (exactly MLIN's
per mode, and ω·tanδ·C in a homogeneous dielectric). The 2N-port is the modal one. The lossless modes of L·C give Tv and
Ti, each mode is an ordinary line of Zc = √(Zm/Ym) and γ = √(Zm·Ym), and the short-circuit admittance is
Σ_m Ti[:,m]·y_m·Ti[:,m]ᵀ/e_m, with y_m the mode's coth and csch terms (`CLineModel.Modal`, shared with CLIN's).

**One copy of the modal math.** The MoM quasi-static kernel already had it (`ModalDecomposition`, `RlgcToSparams`),
in Engine, which Core cannot reference. It moved to RfCore as `MulticonductorLine`: the lossless eigenproblem and its
ordering, the `Ti` normalisation, the per-frequency modal Z and Y with their coupling residual, and the block sum, which
gives the open-circuit Z over Tv and the short-circuit Y over Ti. Engine's classes keep their public shape and call it,
and the kernel's tests pass unchanged.

**Solved once per geometry.** The solve runs at the first stamp, not per frequency, and is cached process-wide by the
exact widths, gaps and substrate, as `BroadsideCoupledPair` caches its pairs. It takes about half a second in the debug
build for three strips on a board.

**Its limits, and what it says.** It is quasi-static, so there is no dispersion: each mode keeps its static εeff and
Zc at every frequency. Past a cross-section of a tenth of the wavelength in the dielectric, the instance says so
(`BroadsideCoupledPair.ReportQuasiStatic`, the broadside pairs' check, now shared). The solver's stated accuracy holds:
C within about 0.5 % for strips with thickness, Z up to about 1.2 % low for zero-thickness ones, which it also says.
Loss rides perturbatively on the lossless modes (Route A, with its residual), and the per-strip R leaves out current
crowding between close strips. The solver's grid is not mirror-exact, so a symmetric set of strips is symmetric to about
1e-3 in its modes and its S-parameters. Against MCLIN on 20 mil RO4350B at 1 GHz, with W = 1.1 mm and S = 0.2 and
0.5 mm, Ze and Zo agree within 0.4 % and 1.2 %, the modal εeff within 0.4 %, and a 45 mm pair's S-parameters within 0.008
(`MicrostripMultiLineTests`).

**As a plan section.** `PlanCoupledStrips` carries `W` and `S` as lists; its nodes are `PlanCoupledLine`'s, each strip's
near end and then its far end, top strip first. The model's pins are every near end and then every far end, so strip
k's nodes 2k − 1 and 2k are its pins k and N + k (`ModelNodes`). `MicrostripPlanModel` stamps it through the N-line model
and counts each strip as its own copper island; `PassivePlanPCells.Draw` draws one MLIN per strip, strip k's centre
below strip k − 1's by their half widths and the gap between them, which for equal strips is exactly the old
k·(W + S). `PlanCoupledLine` is unchanged: two fingers are MCLIN and an even set is Ou's interdigitated section, so the
six parts above stamp and draw bit for bit as before. A plan asks for the N-line model by placing a `PlanCoupledStrips`,
for any N and any widths, two strips included.

**As a part, MCLINN.** The same model is a built-in element: N (3), W, S, L, with `W[k]` and `S[k]` rows for a strip or a
gap of its own, as the Combiner's `IL[k]` are. Its 2N pins follow N (`EditableSchematic.PortCount`,
`SymbolPortDefs.For`, `BuiltInSymbols.Primitives`, `InstanceNetContract`'s `N` rule), the near ends down the left and
the far ends down the right. Its layout cell is a one-section plan through the same drawer, and LVS compares its
parameters, the indexed ones included (`F15-coupled-lines`: a changed `W[2]` is one `lvs.property.mismatch`). Tests:
`MicrostripMultiLineTests` (MCLIN at N = 2; three equal strips reciprocal, end-to-end and mirror symmetric, passive, with
the odd, even and middle modes; a section with an internal node equal to MCLINN and MLIN wired by hand to 1e-9),
`CoupledStripsPartTests` (registration, pins, artwork, the cross-section on the RFIC stack).

**Which parts use it.** MCLINN, and through `PlanCoupledStrips` the parts still to come: the coupled-line array
(interdigital, combline, pseudo-interdigital and multi-line couplers). It is also what MIDC's non-adjacent fingers and
MWPD's side-by-side section pairs need (§5, §6); neither uses it yet, which keeps them as they were.

**Possible enhancements.**

1. **Dispersion.** A per-mode frequency correction, or a full-wave cross-section solve, would track the modes' εeff as
   it rises with f·h, because a long resonator's quasi-static phase runs a few percent fast at the top of a board's
   range.
2. **Loss from the solved field.** The solver already returns ∮σ² over every conductor, and the broadside pairs' loss is
   built on it. The full R matrix from it would count the current crowding on facing edges, which the per-strip
   Wheeler R misses and which matters most in narrow-gap filters.
3. **Coupled open ends.** An open-ended array (interdigital, combline) needs each strip end's fringing, and the end of a
   coupled strip differs from MLIN's. The open-end Δl generalised to N strips would set the resonators' lengths right.
4. **An option on MIDC and MWPD to model every coupling.** Placing their fingers or section pairs as one
   `PlanCoupledStrips` would close §5's uncoupled-pairs limit and give the Lange its non-adjacent coupling. It belongs
   in an option, so that their defaults stay as they are.
5. **A "+" for `W[k]` and `S[k]`.** The rows are typed by hand today, as the Combiner's are. An indexed group in the
   Properties editor (`UserParamTemplate`) would add a strip's width and gap in one click.
6. **A mirror-symmetric grid.** Grading the solver's grid out from the cross-section's centre would make a symmetric set
   of strips symmetric to round-off, so that a test, or an even/odd readout, could ask for exact symmetry.

### 3.7 Sites, and an optional `Footprint`

**The rule.** Every lumped element of a planned passive is a *site*: a pin pair (series) or a pin to ground (shunt),
filled by an ordinary part. On a board that part is an SMT R, L or C; on a die a TFR, MIMCAP or SPIRAL; a ground is a
VIAGND, a crossover a bond wire or an AIRBRIDGE. The parts already worked this way by hand (MWPD's R2/R3, MIDC's J1-J4,
MCBL's G2/G3); a plan now says so. `PassivePlan.Sites` lists each `PlanSite`: its role (R, L, C, via, jumper, or the load the
part drives), its pins, series or shunt, the span between the pins where the plan sets it, and the value the design wants
there. Sites are metadata, not copper: no section reads them, so every existing plan is unchanged bit for bit (the
fingerprints in `WilkinsonPlanTests` and `HybridPlanTests` still pass). What the three parts declare:

| Part | Sites |
|---|---|
| MWPD | an R site per resistor, R2-R3 and R2_k-R3_k, `Gap` apart, of that resistor's value (2·Z0, Z0·(K + 1/K), or Cohn's) |
| MIDC | a jumper from each pin k to Jk, two finger pitches, 2·(W + S), across |
| MCBL | a via on G2 and on G3; the balanced load across 2-3, `Gap` apart, of 2·ZL |
| MLAD | a via on each shorted stub's G pin, and each `site` of its list, a series one `Gap` apart (§3.11) |

The Properties readout lists them under the part's numbers.

**`Footprint`.** `Gap` stays a length and stays the default. MWPD and MCBL, whose site span is that parameter, also take
an optional `Footprint` in the R/C parts' own spelling, `smt:<case>[@density]` or a cell path, chosen with the R/C parts'
own picker (two-pad lands only). When it is set the site span is that land's: where a line meets each pad's outer edge
(`PadJoint`), so for a built-in land its outer span as `ChipLandPatternGenerator` draws it, IPC's Z (1.35 mm for an 0402
at N, the default `Gap`), and for a cell path the distance between its two pins' joints. A footprint that does not resolve
leaves `Gap` and says why. A new part whose site span is a parameter joins `MicrostripSubstrateInjection.HasSiteFootprint`.

**Where it is resolved, and why there.** In the Design layer, at the seam where the substrate is injected
(`MicrostripSubstrateInjection.SiteSpanMeters`, `ApplySiteFootprint`), so every Core plan still takes a plain length.
Every consumer of `Gap` passes through it:

- the schematic's extraction (`NetExtractor`): the run, `circuitrf netlist`, and LVS's schematic side, which reads the
  extracted netlist's values;
- a hand-written `.cnl` (`CnlTechnologyBinding`), against the netlist's own folder and technology;
- Update Layout's PCell parameters (`SchematicLayoutPlanner.ResolveComponentLayout`), so the generated cell, its stored
  parameters and LVS's layout side carry the span;
- EM, which solves that layout's copper.

`Footprint` is consumed there, as a layer choice is: the line carries the span as `Gap`, so a netlist written from a
schematic runs the same from any folder and is not resolved twice. Moving `SmtCaseTable` down to Core was the other route;
it was not needed, since the seam reaches all four, and a cell path needs the file system, which Core never touches.

**Misfit.** The part on a site must have the passive's footprint when one is set. The geometry is Update Layout's existing
bridge report (`ChainPlacer.BridgeFit`): an 0402 on a divider stating `smt:0603` is centred between the ring's ends, an
0603's 2.4 mm apart, and reported as a part bridging them whose pads span 1.35 mm. LVS adds one property check on the drawing,
`lvs.property.site-footprint`: a part on a series site of a passive that states a `Footprint`, with another footprint or
none, is one error naming the passive, the site and both footprints.

**Place Site Parts** (the schematic context menu and a button under the readout). `PassiveSites` reads each passive's
plan from its elaborated model, so a site's value is the run's, finds each site's nets and the parts already across them,
and `PlaceParts` adds a part on each empty one: a resistor, inductor or capacitor of the site's value, with the passive's
`Footprint`, else a built-in land whose span is the site's (an 0402 for MWPD's default `Gap`), else the technology's R/C
default; a VIAGND on a via site. A series part whose pins are a part's span apart in line, with no other pin between,
lands on them as a resistor dropped on MWPD's R2-R3 does; otherwise it stands beside them, outward, with a wire to each.
A shunt part stands on its pin, pointing away from the passive. Jumpers and the load are left, each with a note. The
whole is one undo step (`PlaceSitePartsCommand`). Tests: `PlanSiteTests` (each plan's sites), `PassiveSiteTests` (the
span, the extraction and a `.cnl`, the F8 divider with `smt:0603` through Update Layout and LVS, the misfit, Place Site
Parts on MWPD and MCBL with undo, the readout).

### 3.8 The route helper

`PlanRoute.Build` lays a run of width W that is θ long at F from a start pose, as existing sections, for parts that need
a line folded to fit (a compact rat-race, a phase shifter's reference line, a meander part):

- `Straight`: one `PlanLine`, the same record a plan would write by hand;
- `Corner`: two equal legs and one 90° corner of the given `PlanCorner` (an MCURVE of `Radius`, 3W when none is given; an
  MMITER at its Douville-James chamfer; an MBEND);
- `Meander`: k + 1 equal parallel legs joined by k turns back, stacked `Pitch` apart towards `Side`, each turn the other
  way, so an odd k ends heading back; a turn is a half-turn MCURVE of radius Pitch/2, or two corners and the line across.

It returns the sections, the next free plan node, the end pose and the copper's extent (centre lines widened by W/2).
**The length holds θ through the corners**: the legs share what is left of θ in MLIN's dispersive εeff at F after each
corner's own insertion phase at F, as a length of the line (`PlanSection.LineLength`, the cornered rings' rule, now on
every two-pin section). Every dimension is on the 2 nm grid. **Close turns**: legs under 3W apart edge to edge are a
warning, `device.route.runs-coupled`, since they are stamped as separate lines; the N-line coupled section will take
that case. Refusals: `device.route.spec-not-positive`, `device.route.pitch-too-small`, `device.route.too-short`.
Tests (`PlanRouteTests`, `PassiveSiteTests`): θ = 90° meanders and corners of every corner kind hold S21 at −90° within
0.5° in the plan model, on a board and on a GaAs die; every node is one point; a straight run is one `PlanLine`, field
for field; the drawn copper's ends and extent are the route's.

### 3.9 MGPD, MBPD and MTPD: the Gysel, Bagley polygon and tee dividers

Three more dividers, each a plan of the shared sections and nothing else, so they stamp through `MicrostripPlanModel` and
draw through `PassivePlanPCells.Draw` with no model or drawing code of their own.

| Part | Stored | Derived (readout) | Pins |
|---|---|---|---|
| MGPD | `Z0`, `F`, `Corner` | the three lines' W and λg/4, the ring's turns, R = Z0 | 1 in, 2 and 3 out, `R2` and `R3` the isolation nodes |
| MBPD | `Z0`, `F`, `N` (odd, 3 to 9), `Corner` | the input arms' and sides' W and λg/4, the ring's turns | 1 in, 2 … N + 1 out, the count following `N` |
| MTPD | `Z0`, `F`, `K` | each transformer's W and λg/4 | 1 in, 2 and 3 out |

**MGPD, the Gysel divider** (Gysel 1975). The ring runs 1, 2, R2, R3, 3: quarter waves of √2·Z0 from the input to each
output, quarter waves of Z2 from each output to an isolation node, and a half wave of Z3 between the nodes, each node to
ground through an ordinary resistor on its pin. The numbers come from the even/odd analysis about the line through 1 and
the half wave's middle. Even: the middle is an open, so each isolation node, a quarter wave away, is a short and its
resistor carries nothing, and the isolation arm, a quarter wave further, is an open at the output; the output sees its
input arm alone, which matches 2·Z0 to Z0 at √2·Z0. Odd: 1 and the middle are grounds, the input arm and the half line
are shorted quarter waves, opens at the output and at the node, and the output sees Z2²/R, Z0 when R = Z2²/Z0. Z2 is
free; Z0 makes R = Z0, and makes the Z0 line each resistor lands on matched by it. Z3 does not enter at F; on ideal
lines a lower one widens the band (|S11|, |S22| and |S23| under −20 dB over ±9 % of F with Z0, ±11 % with Z0/√2, ±12 %
with Z0/2), and the plan takes Z0/√2, whose width stays within about twice the port line's. On the example board the
divider is matched and isolated to −50 dB at F and under −20 dB from 2.11 to 2.65 GHz, with any `Corner`.

**MBPD, the Bagley polygon** (Sakagami et al. 2007; Bagley's own design is unpublished). The outputs are the corners of a
polygon of half-wave sides of Z0; the input feeds the middle of the closing side, a quarter wave from the first and last
corners. A half wave repeats its voltage inverted, so every corner has one amplitude and neighbours are opposite, and N
must be odd for the two ways round to agree. Equal voltages into equal loads divide the power equally; the corners'
currents sum to N·V/(2·Z0) at the first and last corners, which a quarter wave of Z_a turns into Z_a²·N/(4·Z0) at the
input, Z0 for Z_a = 2·Z0/√N. The sides' impedance does not enter at F; Z0 gives the widest input match at three ways
(±17 % on ideal lines, against ±13 % at Z0/√2 and ±15 % at √2·Z0). Only the input is matched.

**MTPD, the tee divider** (Pozar §7.2, a textbook, no DOI). One MTEE whose through arms are quarter-wave transformers of
Z0·√(1 + K²) to 2 and Z0·√(1 + K²)/K to 3, so the junction sees Z0·(1 + K²) and Z0·(1 + K²)/K² in parallel, Z0, split as
K². No isolation and only the input matched: there is no resistor.

**What they needed beyond the shared pieces, and why.**

- **One ring builder for both rings (`RacetrackRing`).** The Gysel's ring and the Bagley's polygon are both the
  rat-race's racetrack: tees on two straight runs and a turn at each end. The builder takes each run's stops (a tee with
  its branch pin, or a line), carries the shorter run's difference as a line at each of its ends, makes each turn and
  those lines the span it must have between the tees it joins, and turns on half-turns (π·(R − 0.2·W)) or on two corners
  at their own electrical length (`PlanBend.LineLength`) and a side, as MRRC's. MRRC itself is left as it is, so its
  plan stays bit for bit; it is the same geometry.
- **Where the Bagley's corners go.** For N = 2m + 1 the input and its nearest ⌊(m + 1)/2⌋ corners each side sit on the
  upper run and the rest on the lower, which leaves the runs within about a half wave of each other for every N; the side
  between the two runs goes round the turn.
- **Grounded resistors on pins (MGPD).** Each isolation node is a pin for a resistor to a ground via, as MCBL's G2/G3
  are for its vias, so LVS, the BOM and EM see each resistor; Update Layout places the resistor on its pin and the via
  beside its far pad (`F16-gysel-divider`). The two pins sit 600 apart on the symbol, so no resistor dropped there spans
  both.
- **A second variadic pin count (MBPD).** N + 1 pins, as MWPD's 3 + 2·N: `EditableSchematic.PortCount` reads a plain odd
  `N`, and `SymbolPortDefs.For` and `BuiltInSymbols.Primitives` answer any other count with the three-way divider
  (`BagleyPlan.WaysForPinCount`).

Refusals: `device.gysel.*`, `device.bagley.*` (also an even `N`, or one outside 3 to 9) and `device.teedivider.*`: a
non-positive spec, an unreachable width, a ring too small for F. Tests: `DividerPlanTests` (the closed forms; each
spacing walked round the ring between reference planes; every piece joined; refusals), `DividerSParamTests` (each part
equal to its sections wired by hand to 1e-9; at F the Gysel matched and isolated and split 3 dB in phase, the Bagley a
third to each output in alternate phase, the tee split as K²), `DividersOnMmicTests` (each plans, draws and divides on
the GaAs MMIC technology), the `F14`–`F16` schematic-to-layout fixtures and an LVS clean run plus one spec mismatch each.
The generators' versions follow MTEE's, MLIN's and MCURVE's (and MMITER's and MBEND's for the rings' corners).

For the maintainer: the names MGPD, MBPD and MTPD (microstrip Gysel, Bagley polygon and tee power divider) are
circuitRF's own.

### 3.10 MCRC, the coupled-resonator filters

One part for every coupled-resonator microstrip filter, planned by `CoupledResonatorPlan` from the shared sections and
nothing else. It differs from the parts above in one way, on purpose: it **stores physical geometry**, not specs. A filter's
widths, lengths and gaps are a design, every number chosen with the others, and the design method depends on the shape
(J-inverters, coupling coefficients, closed forms); a filter synthesis will be written later and fill in exactly these
parameters. Until then a user, a script or an optimizer states them. `CoupledResonatorSpec.Read` is the one reader,
used by the factory and the PCell alike.

| Parameter | Stored | Meaning |
|---|---|---|
| `N` | yes | Resonators, 1 to 20. Ring: 1. Cross: 4. |
| `W`, `W1` … `WN` | yes, m | Each resonator's width (its end sections' for a SIR), and the feed strip's beside it. `Wk` overrides `W`. |
| `L`, `L1` … `LN` | yes, m | Each resonator's centre-line length, open end to open end, its corners counted along the centre line (2·arm each, `PlanBend.Arm`). A loop's is 4·side − `Gap`; a ring's its circumference. `Lk` overrides `L`. |
| `S1` … `S(N+1)` | yes, m | Bandpass: `Sk` between resonator k and the resonator or feed before it; `S(N+1)` the output feed's. Tapped: `S2` … `SN` only. Bandstop: `Sk` resonator k to the through line. Spurline: `Sk` spur k's slot. |
| `Shape` | yes, a name | `Straight`, `ZigZag`, `Hairpin`, `OpenLoop`, `Ring`. |
| `Profile` | yes, a name | `Uniform`, or `SIR`: centre `Wc` wide over `Uc`·L (Straight, ZigZag, Hairpin bandpass). |
| `Feed` | yes, a name | `Coupled`, or `Tapped` at `Tap` from the end resonators' midpoints (bandpass). |
| `Form` | yes, a name | `Bandpass`; `Bandstop` (ZigZag or Hairpin); `Spurline` (Straight). |
| `Arrangement` | yes, a name | Open loops: `Inline` or `Cross`. |
| `Sa`, `Gap`, `Sx`, `Tap`, `Wc`, `Uc` | yes | Hairpin arm gap; loop opening; cross-coupling gap (loops 1-4); tap distance; SIR centre width and share. |
| `SignalLayer`, `GroundReference` | yes | As for MWPD. |

**The plans.** *Chain* (Straight, ZigZag, Hairpin): each resonator is walked from one open end to the other — an end
section, the centre (a straight run, one corner, or a U of two corners and the run across `Sa`), the other end section —
and the next resonator's first end section is placed W + S beside this one's second, on the side away from the fold, so
every pair is aligned by construction and every fold turns away from the strip it last coupled to. ZigZag turns right,
then left; Hairpin starts ∪ and alternates. A SIR's end sections are W wide and couple; its centre is `Wc` over `Uc`·L,
and the SIR hairpins come out staggered so their wide ends face. A shorter neighbour sets a pair's length; the longer end
runs on as a plain line. *Loops*: each square is walked counter-clockwise from its opening (an MGAP of `Gap`); two loops
couple wherever a straight side of one lies W + S beside a straight side of the other, over their overlap, which handles
loops of different sizes and the cross square's offset rows alike. *Ring*: two 180° MCURVEs, each feed a W-long line and
an MGAP. *Bandstop*: the through line is the pairs' other strips, back to back, so neighbouring resonators sit a coupled
length (about λg/4) apart, alternately above and below. *Spurline*: per spur, `S` of line 2·W + S wide, then the through
strip and the spur as one MCLIN pair joined at its near end, the spur's far end open, the through strip `S` on.

**What is coupled.** Each strip couples to the one neighbour it is placed against, as an MCLIN pair: the coupling Cohn,
Cristal and Frankel (without the arms' self-coupling) and Hong and Lancaster design with. Left out of the circuit, and
present in an EM run of the artwork: a hairpin's own two arms; strips further over (the resonator two along, the cross
square's diagonal); the overhang of a longer neighbour; the corners' coupling; a coupled end's even/odd end difference
where the pair ends at a loop opening (the MGAP takes it); the spurline's slot end. `Builder.Couple` is the one place
pairs are made. The N-strip coupled section (a W per strip, an S per gap) replaces it there: unequal neighbours (refused
today, `device.mcrc.unequal-pair`) become one asymmetric pair, and the strips beside a pair can join it as one section.

**Open ends.** Every free end is a `PlanOpen`, and a loop's two ends at its opening an MGAP's; all of them are numbered
first of the internal nodes, after the pins, so the unpivoted block eliminates them last.

**What it needed beyond the pattern, and why.**

- **Names in the model and the layout.** `Shape`, `Profile`, `Feed`, `Form` and `Arrangement` reach the factory and the
  generator as text, as `Corner` does: one clause in the elaborator, one in the layout planner. LVS compares them as strings.
- **No substrate rule.** `MicrostripSubstrateInjection` leaves MCRC's widths and lengths alone on every technology: they
  are one design, which only a synthesis can carry to another substrate.
- **Inline loops opened alternately.** Loops opened on the same side couple through sides whose points carry opposite
  voltages and equal currents (the facing sides span the same distances from the two loops' ends, s and L − s), so their
  electric and magnetic coupling cancel exactly for TEM lines and nearly in microstrip: two such loops in the model did
  not reach a coupling coefficient of 0.04 even 0.1·h apart. Opened alternately top and bottom the two add (Hong and
  Lancaster's mixed coupling), and 0.8 mm gives it.
- **A ring's pins on copper.** An MGAP draws no copper, so a pin on its outer end had none under it and LVS refused it
  (`lvs.pin.no-copper`); each feed is a line one W long.
- **The spurline's notch.** With the through strip and the spur joined at the near end and the spur open at the far end,
  the pair's transfer admittance is 2/(j(Ze·sin θe − Zo·tan θo·cos θe)), which vanishes where θo = π/2: the notch is where
  the spur is a quarter wave in the odd mode. The model's notch is 0.24 % from that closed form with the spur's open end.

**What synthesis must produce, for each shape.** All of it as the stored parameters above; nothing is derived inside the
part.

| Shape / form | Synthesis produces | Notes for the synthesis |
|---|---|---|
| Straight | `N`, `W`, `L` (or `Lk`), `S1` … `S(N+1)`; tapped: `S2` … `SN` and `Tap` | Each coupled section is an end section, (L − centre)/2 long. Cohn's J-inverters give each section's Ze, Zo; with one W the gap alone sets (Ze − Zo)/(Ze + Zo) = JZ0/(1 + (JZ0)²). L is two sections less the two open ends' Δl (`MicrostripOpenEnd`). Per-section widths need the N-strip section. |
| ZigZag | as Straight | L adds each corner's excess, 2·arm − `PlanBend.LineLength`. |
| Hairpin | as Straight, plus `Sa` | The U is W + `Sa` + 2·arm of centre line: L adds two corners' excess. `Sa` wide enough that the arms' own coupling (not modelled) is small, or the synthesis accounts for it (Cristal and Frankel). |
| SIR (any of the three) | plus `Wc`, `Uc` | The impedance step and the length share set the resonance and the first spurious (Makimoto and Yamashita). `Uc`·L must hold the fold. |
| OpenLoop, Inline | `N`, `W`, `Lk` per loop, `Gap`, `S2` … `SN`, and `S1`, `S(N+1)` or `Tap` | Coupling coefficients from split frequencies, Qe from a loaded loop. Each loop is sized for its surroundings: a coupled side lowers a loop's resonance (by 3 % at 0.8 mm on 20 mil RO4350B), so the middle loops come out smaller. |
| OpenLoop, Cross | as Inline with `N` = 4, plus `Sx` | M12 = M34 (mixed), M23 (magnetic, S3), M14 (electric, `Sx`) of opposite sign: a zero either side of the band. |
| Ring | `W`, `L`, `S1`, `S2` | L one guided wavelength at f0; the gaps set the external Q. |
| Bandstop | `N`, `W`, `L`/`Lk`, `S1` … `SN`, `Sa` for Hairpin | Each resonator notches where it is λg/2 (corners' excess added); the gaps set each notch's coupling. Spacing is fixed: back to back. |
| Spurline | `N`, `W`, `L`/`Lk`, `S1` … `SN` | The spur is a quarter wave in the odd mode at the notch, less its open end. The line is 2·W + S wide. |

Refusals: `device.mcrc.*` — `n-invalid`, `spec-not-positive`, `gap-missing`, `name-unknown`, `form-shape`, `form-feed`,
`profile-shape`, `ring`, `cross-n`, `sir-ratio`, `sir-centre`, `unequal-pair`, `tap-off-line`, `too-short`, `no-overlap`.
Tests: `CoupledResonatorPlanTests` (every node one place, every copper island clear of the others, open ends first, pin 1
at the origin, each refusal's code), `CoupledResonatorSParamTests` (a textbook design per shape within 2 % of its centre
and 15 % of its ripple bandwidth — Cohn as Straight, ZigZag and Hairpin, Hong and Lancaster for inline loops; the cross
square's two zeros; the ring, bandstop and spurline within 2 % of their closed forms; every shape and form its sections
wired by hand to 1e-9), `CoupledResonatorGaAsTests` (a hairpin on the shipped GaAs and Si processes), and the
`F14`-`F18` schematic-to-layout fixtures, one per shape, each LVS clean with one stored value changed giving exactly one
`lvs.property.mismatch`.

#### Possible enhancements

1. **The N-strip coupled section in `Builder.Couple`.** It would lift the refusal of unequal neighbours, which a W per
   resonator needs, and couple a hairpin's own arms and the strips two over, which Cristal and Frankel's hairpin design
   and a tightly folded open-loop filter both depend on.
2. **A through line of its own width for the bandstop.** Today each through-line stretch is as wide as the resonator
   beside it, because an MCLIN pair has one width; a 50 Ω line with resonators of another impedance is the usual design,
   and the asymmetric pair from (1) gives it.
3. **Spacing parameters for the bandstop and the spurline.** Resonators sit back to back, about λg/4 apart, and spurs 2·S
   apart; optimum bandstop designs and cascaded spurlines choose the spacing, so a synthesis would want to state it.
4. **The spurline's slot end.** The spur's open end faces the full-width line across the slot's short leg, a gap the
   model leaves out; an MGAP there would follow narrow slots, where it pulls the notch down.
5. **A readout of each resonator's frequency and each coupling.** The part shows geometry only; the resonators' own
   frequencies, the coupling coefficients and Qe, computed from the plan as the tests compute them, would let a user check
   a hand-made design before running it.
6. **More cross-coupled arrangements.** Trisections and cascaded quadruplets of open loops are the same loops in other
   places; the coupling search already finds facing sides wherever loops sit.

### 3.11 MLAD, the microstrip ladder

MLAD stores **geometry** rather than specs, as MCRC does: one through line from pin 1 to pin 2 built from an
ordered list of sections. A filter synthesis, written later, will produce exactly that list. The list alone covers the
stepped-impedance and stub lowpass filters (the elliptic kind included), radial-stub and butterfly lowpass filters,
shorted-stub bandpass and highpass filters, quarter-wave stub bandstop filters, end-coupled bandpass filters, DC blocks,
single- and double-stub matchers, multi-section quarter-wave transformers, and semi-lumped and lumped LC ladders.
`LadderPlan` is the whole of it in Core: the list, its text and the plan.

| Parameter | Stored | Meaning |
|---|---|---|
| `Sections` | yes, text | The list (below). |
| `Z0` | yes, Ω | The ports' lines: their width, Z0's at F, is the width a gap or a stub at either end sits on. |
| `F` | yes, Hz | Where the readout states each section, and where the ports' lines' width is found. |
| `Gap` | yes, m | Every series site's span (1.35 mm, an 0402's land); a `Footprint` sets it from its land (§3.7). |
| `SignalLayer`, `GroundReference` | yes | As for MWPD. |
| each section's Z and θ at F; a gap's Cg and Cp; a radial end's reactance | readout | `LadderPlan.Build(...).AtF`. |
| each site's part, value and span | readout | `PassivePlan.Sites`, as every planned passive lists them. |

**The list, and why it is one text parameter.** circuitRF's list-valued parameters are indexed numbers: the Combiner's
`IL[k]` and `Phase[k]`, a tone source's `Freq[k]`. They suit a list of numbers of one kind. A ladder's sections are of
several kinds with different fields, a name among them (`end`, `side`), and inserting one in the middle would renumber
every row after it. So `Sections` is one text parameter with a small grammar, parsed in Core (`LadderPlan.Parse`, and
`LadderPlan.Format` writes it back):

```
Sections := item { ";" item }            empty items (a trailing ;) are skipped
item     := kind "(" field { "," field } ")"
field    := name "=" value                any order; names and words in any case; whitespace ignored
line(W, L)   gap(S)   taper(W1, W2, L)   stub(W, L, end=open|short, side=up|down|both)
stub(W, L, end=radial|butterfly, Ro, angle, side)   site(role=R|L|C, value, type=series|shunt, side=up|down)
```

A length is a number in metres or a number with a length unit glued on (`mm`, `um`, `nm`, `cm`, `mil`, `in`); `m` alone
is milli in circuitRF and is refused. `end` and `side` default to `open` and `up`. A wrong item is a coded refusal naming
the section's number and text: `device.ladder.empty`, `.syntax`, `.unknown-kind`, `.unknown-field`, `.missing-field`,
`.bad-value` and `.not-positive`, and for the part `.spec-not-positive` and `.width-unreachable`. The elaborator keeps
`Sections` as text (as a ring's `Corner`), Update Layout hands it to the generator as text, the netlist extractor quotes
it, and LVS compares it as a string, so a changed list is one `lvs.property.mismatch` naming `Sections`.

**New kinds without breaking stored values.** A kind is a word and a field a name, so a kind added later reads no
stored list differently: an unknown word is refused today and is a section tomorrow, and a field added with a default
leaves every list that does not state it as it was. Radial and butterfly ends and lumped sites arrived this way: two
new `end` words with two new fields that only they take (`Ro`, `angle`, 60° unless stated), and a new kind, `site`. A
site's value is a number with its role's unit or a bare SI prefix (`3.3pF`, `12nH`, `4.7kOhm`, `2.2p`). A stub that is
not radial refuses `Ro` and `angle`, and a series site refuses `side`, so no list reads one way and is drawn another.
Every list written before them parses, builds and draws as it did: `LadderPlanTests`, `LadderSParamTests` and the F24
fixture are unchanged.

**The plan.** Every section is an existing part, with no new model or drawer code beyond one section kind:

- `line` is a `PlanLine`, `gap` a `PlanGap` at the width there, and `taper` a `PlanTaper`. The width there starts as the
  ports' lines' (W0) and is each line's W or each taper's W2 after it.
- A one-sided `stub` is a `PlanTee` whose through arms end at the stub's edges (no line in them) and whose branch is the
  whole stub, `L` past the wider through arm's edge: MTEE's own reference planes, so the junction is MTEE's and `L` is
  the line beyond it. Its through arms take the widths either side. MTEE's branch leaves along its own −y, so an `up`
  stub's tee is turned over.
- A two-sided `stub` is a `PlanCross`, the new section: one case in `MicrostripPlanModel.Build`
  (`MicrostripCrossModel`) and one in `PassivePlanPCells.Draw` (`MCrossPCell`), posed at its centre, MCROSS's own
  origin. MCROSS reads a pair of arms ending at the other pair's edges as a straight line with pins on its sides, a rule
  for a cross drawn alone, which would drop the junction. So the cross's through arms reach 1 µm past the stubs
  (`LadderPlan.CrossThroughPast`): enough for the junction, and 0.01° at 10 GHz.
- An open end is a `PlanOpen`. A shorted end is a pin, `G1`, `G2`, … in list order, up before down, for an ordinary
  VIAGND, as MCBL's grounds are, declared as a via site.
- A radial or butterfly end is a `PlanRadialStub` (MRADIAL's section, §3.5) on the stub's end node, `W` wide at its
  narrow end, so the stub is a feed of `W` and `L` and then the sector. A sector fans along its own +x, so it is turned
  to fan away from the line; a butterfly's two fan along its own ±x with the feed from −y, so it is turned to take the
  feed from the line, its sectors either side of the feed's end and parallel to the line. A feed must reach far enough
  for them to clear the line; nothing checks it (below).
- A series `site` is no section at all: the copper stops `Gap` short, and its two ends are pins for the part across them.
  A shunt `site` is a one-sided junction like a stub's whose branch is a square pad of the width there, its end a pin for
  the part to ground. Where a series site has no copper beside it (at either end, after a gap, or beside another series
  site), the plan adds a pad there, a `PlanLine` square of the width there, so the part always lands on copper and an LC
  ladder of sites alone is a row of pads. Each site is declared in `PassivePlan.Sites` with its role, kind, pins, span
  and value, which the readout lists and Place Site Parts fills (§3.7); a `Footprint` sets the span, MLAD having joined
  `MicrostripSubstrateInjection.HasSiteFootprint`.
- Nodes: the pins (1, 2, G…), then the stub ends that are not pins (open and radial), numbered first of the internal
  nodes because a quarter-wave stub's end has a near-zero self admittance at resonance and the local block does not
  pivot, then the rest in list order. The pins
  sit exactly at the first section's start and the last one's end. No line is added, so a transformer or a matcher ends
  on its own load. Every dimension is held on the 2 nm grid.

**What it needed beyond the pattern.** Its pins follow the list: `EditableSchematic.PortCount` counts the G pins
(`LadderPlan.Grounds`, 0 for a list that does not parse), and `SymbolPortDefs.For` and `BuiltInSymbols.Primitives` draw
G1… along the bottom, 200 apart. The symbol is drawn from the pin count alone, as every variadic symbol here is, so a
site's pins cannot have names of their own: every pin after 1 and 2 is a G pin, in list order, a via's or a part's, and a
list with no site keeps exactly the pins it had. G pins 200 apart are closer than a lumped part's 400, which Place Site
Parts used to leave to the user; it now stands such a part beside its pins with a wire to each, as it does for pins
farther apart than a part. Unlike MWPD's, the ladder's symbol differs at two and three pins, so the catalogue
measures it as variadic: `ComponentTypeRegistry.PortCountParameter` names `Sections`, and the netlist reader does not
infer `Sections` from a line's net count. A text parameter needed its path through the elaborator, Update Layout and
the netlist extractor, as above.

**What a synthesis must produce.** The list is physical, so a synthesis step's output is the list itself. Widths come
from `LadderPlan.Width(Z, F, substrate)`, MLIN's impedance at F on the 2 nm grid. Lengths come from each line's own
εeff at the design frequency. Series lines run between junction edges and stubs from the through line's edge, and an
open stub is written short by its end extension (`MicrostripOpenEnd.Extension`). For each kind of design:

- **Stepped impedance.** For each prototype value g: a shunt C as a low-Z line of βl = g·Zlow/R0, a series L as a high-Z
  line of βl = g·R0/Zhigh, at the cutoff. The steps between them are not in the model.
- **Richards and Kuroda.** Every element is λ/8 at the cutoff (Richards, Ω = tan βl). A shunt C becomes `stub(end=open)`
  of Z = 1/C (normalised), and a shunt L `stub(end=short)` of Z = L. A series L is a series stub, which microstrip cannot
  draw, so unit elements (`line`, also λ/8) are added at the ends and Kuroda's identities move them through: a unit
  element Z1 then a series stub Z2 equals a shunt stub Z1(Z1 + Z2)/Z2 then a unit element Z1 + Z2. A low-impedance stub
  may be split into two arms, `side=both`, each of twice the impedance.
- **Elliptic kind.** Stepped impedance, with each shunt branch's zero as an open stub λ/4 at that zero, of the
  impedance that gives the branch's admittance at the cutoff.
- **Quarter-wave stub filters** (Matthaei). Stubs λ/4 at the centre joined by λ/4 lines: shorted for a bandpass, or
  made wide for a highpass, and open for a bandstop.
- **J-inverter gaps** (Cohn). Each inverter J gives a series susceptance b = J/(1 − J²) (normalised), and a `gap` whose
  MGAP Cg is b/(ω0·Z0). MGAP's Cg falls monotonically with S, so a bisection finds S. Each resonator is
  π − ½(atan 2bk + atan 2bk+1) long, less atan(ω0·Cp·Z0) for each end gap's Cp.
- **Matchers and transformers.** Single and double stubs as `stub` and `line`, the load on pin 2 at the last section's
  end, and λ/4 transformers as `line`s.
- **Radial and butterfly stubs.** A radial stub in place of a λ/4 open stub, its `Ro` and `angle` from MRADIAL's own
  closed form (§3.5): `stub(end=radial)` with a short feed.
- **Lumped and semi-lumped.** A prototype element that stays lumped is a `site` of its value: a series L or C a series
  site, a shunt one a shunt site. Mixed with lines and stubs it is a semi-lumped ladder; alone, a lumped one.

The closed forms place each stub at a point, while the model includes each junction's reactance (MTEE, MCROSS). A
synthesis that must land within a percent compensates for it, or optimises on the model.

**What the tests hold** (each design from its closed form, on 20 mil RO4350B unless stated). `LadderPlanTests`: the
list reads back what it writes, any case and spacing and length unit read, each refusal names its section, the pins
follow the shorted stubs with the open ends first, and every section meets its neighbours with pin 2 at the ladder's
length. `LadderSParamTests`:

- every kind against its MLIN, MGAP, MTAPER, MTEE and MCROSS by hand, to 1e-9;
- the stepped-impedance lowpass's −3 dB point at the g-values' cutoff, −4.6 %;
- the Kuroda stub lowpass's, −2.1 %;
- the elliptic kind's zero, +2.9 %;
- the shorted-stub bandpass's centre (Matthaei), −1.7 %, and the highpass;
- the bandstop's notch, +0.6 %;
- the end-coupled bandpass's centre (Cohn, 25 mil alumina at 10 GHz), +0.04 %;
- the DC block;
- |S11| at F of −52 dB for the single-stub matcher, −39 dB for the double-stub matcher and −78 dB for the binomial
  transformer.

`LadderStackTests`: the same quarter-wave stub on the GaAs, Si and RO4350B stacks simulates and draws. Also
`ALaddersPins_FollowItsShortedStubs`, `LadderReadoutTests`, `PlanSectionsPCellTests` (the cross is MCROSS's own
artwork), and `testdata/schematic-to-layout/F24-microstrip-ladder` with its via on G1, LVS clean and a changed list
one mismatch.

For the radial ends and the sites: `LadderSitesTests` covers the new kinds reading back what they write, their defaults
and spellings, each refusal naming its section, the G pins and sites in list order, the radial sections turned away from
the line, and an LC ladder of sites alone as a row of pads that meet. `LadderSitesSParamTests` holds:

- a radial-stub lowpass and a butterfly lowpass, each equal to its MLIN, MTEE, MCROSS and MRADIAL by hand to 1e-9, and
  each passing low and stopping high;
- the third-order lumped Butterworth lowpass of sites alone, filled with ideal parts of the values its sites state, with
  its −3 dB point at 200 MHz −1.2 %.

`LadderSiteTests` covers drawing the radial ends and the pads, the readout with and without a `Footprint`, Place Site
Parts filling the site, and `testdata/schematic-to-layout/F25-ladder-site`: a series capacitor's site that an 0402
spans, LVS clean, and a changed list one mismatch.

**Limits.**

- **The step between widths is not modelled.** There is no MSTEP: upstream left it out on purpose. A stepped-impedance
  design is its lines cascaded, and the step capacitance is a known limit.
- **The junctions are MTEE's and MCROSS's fits,** calibrated at εr 9.9. A stub wider than the line it stands on moves a
  zero: +2.9 % for the elliptic example, by MTEE's negative branch inductance.
- **Nothing checks the layout.** Coupling between neighbouring stubs is not in the model, two stubs on one side closer
  than their widths overlap without a refusal, and nor does a radial sector that reaches back over the line or a
  neighbour.
- **A site's pads are copper the lumped design does not count.** They are in the model: the shunt pad's own inductance,
  in series with the part, lowers a 1 GHz lumped lowpass's cutoff about 5 % on 20 mil RO4350B, and a 200 MHz one's about
  1 %.
- **One span for every series site.** `Gap`, or the `Footprint`, sets them all, so an L and a C of different cases
  cannot sit in one ladder at their own lands.
- **The geometry is stored,** so a ladder moved to another stack keeps its widths. That is the point of a ladder, and
  the readout shows what they come to there.

**Possible enhancements.**

1. **Expressions in the list.** A list holds numbers, so an optimiser cannot tune one section's length. Expressions in
   fields, or one tunable per field, would let a synthesis hand its result to the optimiser for the last per cent the
   junctions take.
2. **A step section, if a step model is ever added.** It would be one model case and one drawer case, and the
   stepped-impedance cutoff would include the steps.
3. **Junction-aware lengths.** The closed forms place stubs at points. A helper that measured each junction's own phase,
   as `PlanBend.LineLength` does for a corner, would let a synthesis correct its stubs exactly.
4. **Coupled neighbours.** Stubs or lines closer than about three widths couple. A `PlanCoupledLine` between them would
   put that in the model with no new model code.
5. **A stub overlap refusal.** Two stubs on one side closer than their widths overlap; the plan could refuse that, as it
   refuses a ring too small for its F.
6. **Meandered stubs.** A low-frequency stub is long and straight; the route helper (S4) could fold it, at the cost of
   the corner corrections it already carries.
7. **A span per site.** A `gap=` field on a series site, or a footprint per site, would let parts of different cases sit
   at their own lands, which a lumped ladder of mixed L and C sizes needs. Today every series site spans the part's
   `Gap`.
8. **Pads the design can count.** The readout could state each site pad's own L and C at F. A lumped synthesis could
   then take them out of its values, rather than leaving the cutoff a few per cent low at higher frequencies.
9. **Names for a site's pins.** A symbol drawn from the list rather than from its pin count could name a site's pins by
   site and draw a series site's two 400 apart, so a part dropped on them lands with no wire. That needs every caller of
   `SymbolPortDefs.For` to ask the instance, which is wider than this part.

## 4. What a new topology costs

1. `src/Core/Devices/Microstrip/<X>Plan.cs` — the pure plan (and a section kind, with one case in
   `MicrostripPlanModel.Build` and one in `PassivePlanPCells.Draw`, if it needs a part the plans do not use yet).
   `PlanBend` (MMITER and MBEND, for `Corner`) cost exactly one model case and one drawer case; because MBEND stamps in
   branch (Z) form, the shared local block also learned to take branch rows, once, for any such part
   (`LocalAdmittanceBlock`). `PlanGap`, `PlanTaper` and `PlanCross` cost the same, and `PlanOpen` that plus its closed form
   (`MicrostripOpenEnd`); a gap and an open end also join no copper, one line in the model's DC islands.
2. `ComponentModelFactory` — the type name, `_parameterizedTypes` and one dispatch line to `MicrostripPlanModel`.
3. `SchematicModel.SymbolKind`; `ComponentTypeRegistry` (entry, terminals text, `EngineReference`, parameter
   descriptions, `ModelValidity`, `HasNoiseSwitch`, `DefaultParameters`, token); `EditableSchematic.SymbolPortDefs`;
   `BuiltInSymbols` (glyph); `MicrostripSubstrateInjection` (kind set); `SymbolArtworkGenerator`.
4. `PassivePlanPCells.Generate<X>` and its `PCellRegistry` row; the generator row in `Lvs/DeviceType`; the generator
   in `GroundPourPlanner`'s line set. A part whose pins follow a parameter also needs its count in
   `EditableSchematic.PortCount`, `SymbolPortDefs.For` and `BuiltInSymbols.Primitives` (§3.1), and, where two and three
   pins draw differently, `ComponentTypeRegistry.PortCountParameter` (§3.11). A ring of tees on two
   runs is `RacetrackRing.Build` (§3.9) and costs nothing more.
5. Tests: the plan against its closed form, the composite against its parts by hand, a schematic-to-layout fixture; the
   records in `LibraryCatalogTests` and `PCellGeneratorVersioningTests`.
6. Its sites (§3.7): the plan's `Sites`; a kind with sites in `SchematicViewModel.HasSites`, and one whose site span is a
   stored length in `MicrostripSubstrateInjection.HasSiteFootprint`. A folded line is `PlanRoute` (§3.8).
7. Docs: a subsection under Dividers and couplers in `components.md`, and the example's README table.

## 5. Known limits

- **The isolation resistor's component port closes a loop.** The ring joins R2 to R3, so the resistor's two pads are one
  piece of copper. Both component-port builders build a port across such a part as across two conductors, when air lies
  between the pads (`EmPlanarComponentPorts.Construct`; `C3dComponentPorts.Construct`, whose sheet's ends are then
  inferred as a loop's), and warn that the 0 Hz row reads the loop (`em.component-port.across-loop`). So an EM run has the
  resistor's port, and the co-simulation netlist closes it with the 100 Ω. A cut cell instance whose pins the copper joins
  is still refused: its pins are ground-referenced ports, with no gap between two pins to tell a loop from a short.
- **MTEE's closed forms are calibrated at εr 9.9** and say so on any other substrate; MCURVE's effective-radius rule is
  stated for radii of 3W to 7W, and a low-frequency ring is larger. Both warnings reach Messages through the part.
- **The multi-section pair is two uncoupled lines in the model.** The lines run about `Gap` apart; their coupling is
  in EM and not in the circuit. On the example board Planar and EMerge follow the model's isolation to a few dB.
- **`N` must be a plain number** for MWPD and MBPD, since it sets the pins. The catalogue (`circuitrf reference`) lists
  MWPD at its five terminals and MBPD at its four, and says in the terminals note how `N` adds pins.
- **A multi-section branch-line's outer arms are high.** Three sections need 4.26·Z0 and four 7.40·Z0; in a 50 Ω system
  the first is a line no board draws on 20 mil RO4350B and the second is outside the line model (§3.2).
- **The Bagley and the tee match only their input.** Their outputs see each other: the Bagley's three-way outputs are
  at −3.7 dB return loss and −9.6 dB coupling to each other on the example board, the tee's at −6 dB.
- **The MWPD generator's version follows MTEE's and MCURVE's**, and with corners MMITER's, MBEND's and MLIN's (MRRC's
  likewise). It draws through them, so a change that bumps one of those generators' versions changes the part's artwork
  too and must bump the part's.

## 6. Possible enhancements

1. **Coupling between side-by-side lines.** The multi-section divider's line pairs run about `Gap` apart and are
   stamped as two uncoupled lines (§5); EM sees the coupling and the circuit does not. A `PlanCoupledStrips` per pair
   (§3.6) would close that, with no new model or drawer code.
2. **Phase compensation for MCBL.** Microstrip's odd mode is faster than its even mode, so the outputs sit about 170°
   apart rather than 180°. A first section a few percent longer, solved in the plan, would restore 180°.
3. **An EM cell for MIDC.** The Lange is checked in the circuit, Update Layout and LVS only: Planar cannot put a jumper
   across a finger. A bond-wire jumper in Planar, or drawn wires in 3D, would let the example check it as it checks the
   others.
4. **Loop ports on cut cell instances.** A part on drawn pads whose copper closes a loop has its port (§5); a cut cell
   instance whose pins the copper joins is still refused, since its pins are ground-referenced with no gap between two
   of them.
5. **A "Dividers and couplers" palette category.** Nine new kinds under Microstrip make that list long; a category of
   their own would make them easier to find.
6. **Coupled ends' own model.** A coupled pair's open ends take the single line's Δl form once per mode (§1), which
   is not a fit to coupled ends. Kirschning, Jansen and Koster's coupled parallel-gap model (§7) was made for them, and
   the edge-coupled filters' resonator lengths, which these ends set, would follow it more closely.
7. **An unequal split for MGPD.** The nodal analysis at F gives one directly: input arms of Z0·√(1 + K²) and
   Z0·√(1 + K²)/K, isolation arms in the ratio K, and the two resistors in parallel K²·Z2²/(Z0·(1 + K²)), the half wave
   tying them; the literature has closed forms for it, but none could be checked against its paper here, and the free
   choices (Z2, how R splits) change the band. With a checked source it is one more spec, as MWPD's `K` is.
8. **A band for MBLC.** Four sections at a flat match need 7.40·Z0 outer arms. The one free value trades flatness for
   lower arms and more band: with the middle arm at 2.86·Z0 instead of 1.99·Z0 the outer arms fall to 4.4·Z0 and, on
   ideal lines, |S11| and |S41| stay under −20 dB over ±35 % of F, against ±27 % when flat. Stated as a band, as MWPD's
   `Band` is, it would bring four sections onto the boards that build three.
9. **A Bagley with outputs in phase.** Alternate outputs leave half a period apart; a half-wave line on every other
   output, planned in, would bring them into phase for an array feed, at the cost of length.
10. **The Gysel's N-way form.** Gysel's paper divides N ways with each output's isolation line meeting at one floating
   node; like Wilkinson's N-way it does not lie flat for three or more outputs (§3.1), and would need a crossing part.
11. **Smooth circuit curves in the example's plots.** Each bench sweeps only the EM runs' frequencies, so every marker is
   a solved point and the circuit line is coarse; a `.cdd` that also reads the cell's own fine sweep would draw it
   smoothly.

12. **Bond wires and air bridges from Place Site Parts.** A jumper site (MIDC's) is left with a note, since a wire part
   needs its own design; placing one would fill the last kind of site by hand.
13. **Die parts sized from the site's value.** On an MMIC or RFIC stack Place Site Parts puts an ideal R, L or C with no
   land; a TFR or MIMCAP sized for the value from the process would make a die design complete in one step.
14. **A `Footprint` path relative to the schematic in a `.cnl`.** A written netlist resolves a cell path against its own
   folder; one written elsewhere keeps the span extraction resolved but warns, since its folder holds no such cell.

## 7. References

> E. J. Wilkinson, "An N-Way Hybrid Power Divider," *IRE Transactions on Microwave Theory and Techniques*, vol. 8,
> no. 1, pp. 116–118, January 1960. DOI [10.1109/TMTT.1960.1124668](https://doi.org/10.1109/TMTT.1960.1124668).

> U. H. Gysel, "A New N-Way Power Divider/Combiner Suitable for High-Power Applications," *MTT-S International Microwave
> Symposium Digest*, vol. 75, pp. 116–118, 1975. DOI [10.1109/MWSYM.1975.1123301](https://doi.org/10.1109/MWSYM.1975.1123301).

> I. Sakagami, T. Wuren, M. Fujii and M. Tahara, "Compact Multi-Way Power Dividers Similar to the Bagley Polygon,"
> *2007 IEEE/MTT-S International Microwave Symposium*, pp. 419–422, June 2007.
> DOI [10.1109/MWSYM.2007.380477](https://doi.org/10.1109/MWSYM.2007.380477). The polygon divider's analysis; G. Bagley's
> original design is unpublished.

> D. M. Pozar, *Microwave Engineering*, 4th ed., Wiley, 2012: §7.2, the lossless T-junction divider; §7.3, the
> Wilkinson power divider; §7.5 and §7.8, the quadrature and 180° hybrids; §7.6 and §7.7, coupled-line directional couplers and the Lange coupler. A book, with no
> DOI: [the publisher's page](https://www.wiley.com/en-us/Microwave+Engineering%2C+4th+Edition-p-9780470631553).

> J. Reed and G. J. Wheeler, "A Method of Analysis of Symmetrical Four-Port Networks," *IRE Transactions on Microwave
> Theory and Techniques*, vol. 4, no. 4, pp. 246–252, October 1956.
> DOI [10.1109/TMTT.1956.1125071](https://doi.org/10.1109/TMTT.1956.1125071). The even- and odd-mode method.

> R. Levy and L. F. Lind, "Synthesis of Symmetrical Branch-Guide Directional Couplers," *IEEE Transactions on Microwave
> Theory and Techniques*, vol. 16, no. 2, pp. 80–89, February 1968.
> DOI [10.1109/TMTT.1968.1126612](https://doi.org/10.1109/TMTT.1968.1126612).

> M. Muraguchi, T. Yukitake and Y. Naito, "Optimum Design of 3-dB Branch-Line Couplers Using Microstrip Lines," *IEEE
> Transactions on Microwave Theory and Techniques*, vol. 31, no. 8, pp. 674–678, August 1983.
> DOI [10.1109/TMTT.1983.1131568](https://doi.org/10.1109/TMTT.1983.1131568). With Levy and Lind, the synthesis of
> multi-section branch-line couplers that MBLC's two-section design follows; its arm impedances are derived in §3.2 from
> the even/odd conditions and checked by the circuit model, not taken from a table in either paper.

> E. Hammerstad and Ø. Jensen, "Accurate Models for Microstrip Computer-Aided Design," *1980 IEEE MTT-S International
> Microwave Symposium Digest*, pp. 407–409. DOI [10.1109/MWSYM.1980.1124303](https://doi.org/10.1109/MWSYM.1980.1124303).
> The static line model the widths are synthesised on.

> L. I. Parad and R. L. Moynihan, "Split-Tee Power Divider," *IEEE Transactions on Microwave Theory and Techniques*,
> vol. 13, no. 1, pp. 91–95, January 1965. DOI [10.1109/TMTT.1965.1125934](https://doi.org/10.1109/TMTT.1965.1125934).
> The unequal split.

> S. B. Cohn, "A Class of Broadband Three-Port TEM-Mode Hybrids," *IEEE Transactions on Microwave Theory and
> Techniques*, vol. 16, no. 2, pp. 110–116, February 1968.
> DOI [10.1109/TMTT.1968.1126617](https://doi.org/10.1109/TMTT.1968.1126617). The multi-section divider.

> R. J. P. Douville and D. S. James, "Experimental Study of Symmetric Microstrip Bends and Their Compensation," *IEEE
> Transactions on Microwave Theory and Techniques*, vol. 26, no. 3, pp. 175–182, March 1978.
> DOI [10.1109/TMTT.1978.1129340](https://doi.org/10.1109/TMTT.1978.1129340). The mitred corner's chamfer.

> M. Kirschning, R. H. Jansen and N. H. L. Koster, "Measurement and Computer-Aided Modeling of Microstrip
> Discontinuities by an Improved Resonator Method," *1983 IEEE MTT-S International Microwave Symposium Digest*,
> pp. 495–497. DOI [10.1109/MWSYM.1983.1130959](https://doi.org/10.1109/MWSYM.1983.1130959). The square corner's model.

> M. Kirschning, R. H. Jansen and N. H. L. Koster, "Accurate model for open end effect of microstrip lines,"
> *Electronics Letters*, vol. 17, no. 3, pp. 123–125, February 1981.
> DOI [10.1049/el:19810088](https://doi.org/10.1049/el:19810088). The open end's extension (`PlanOpen`).

> M. Kirschning, R. H. Jansen and N. H. L. Koster, "Coupled microstrip parallel-gap model for improved filter and
> coupler design," *Electronics Letters*, vol. 19, no. 10, pp. 377–379, May 1983.
> DOI [10.1049/el:19830261](https://doi.org/10.1049/el:19830261). Coupled open ends and gaps (§6).

> M. Kirschning and R. H. Jansen, "Accurate model for effective dielectric constant of microstrip with validity up to
> millimetre-wave frequencies," *Electronics Letters*, vol. 18, no. 6, pp. 272–273, 1982.
> DOI [10.1049/el:19820186](https://doi.org/10.1049/el:19820186). The dispersion λg/4 is measured in.

> M. Kirschning and R. H. Jansen, "Accurate Wide-Range Design Equations for the Frequency-Dependent Characteristic of
> Parallel Coupled Microstrip Lines," *IEEE Transactions on Microwave Theory and Techniques*, vol. 32, no. 1, pp. 83–90,
> January 1984. DOI [10.1109/TMTT.1984.1132616](https://doi.org/10.1109/TMTT.1984.1132616); corrections, vol. 33, no. 3,
> p. 288, 1985, DOI [10.1109/TMTT.1985.1133005](https://doi.org/10.1109/TMTT.1985.1133005). The modes the coupled-line
> synthesis inverts.

> S. B. Cohn, "Parallel-Coupled Transmission-Line-Resonator Filters," *IRE Transactions on Microwave Theory and
> Techniques*, vol. 6, no. 2, pp. 223–231, April 1958. DOI [10.1109/TMTT.1958.1124542](https://doi.org/10.1109/TMTT.1958.1124542).
> MCRC's Straight, ZigZag and Hairpin test designs.

> E. G. Cristal and S. Frankel, "Hairpin-Line and Hybrid Hairpin-Line/Half-Wave Parallel-Coupled-Line Filters," *IEEE
> Transactions on Microwave Theory and Techniques*, vol. 20, no. 11, pp. 719–728, November 1972.
> DOI [10.1109/TMTT.1972.1127860](https://doi.org/10.1109/TMTT.1972.1127860).

> M. Makimoto and S. Yamashita, "Bandpass Filters Using Parallel Coupled Stripline Stepped Impedance Resonators," *IEEE
> Transactions on Microwave Theory and Techniques*, vol. 28, no. 12, pp. 1413–1417, December 1980.
> DOI [10.1109/TMTT.1980.1130258](https://doi.org/10.1109/TMTT.1980.1130258). The SIR profile.

> J.-S. Hong and M. J. Lancaster, "Couplings of Microstrip Square Open-Loop Resonators for Cross-Coupled Planar
> Microwave Filters," *IEEE Transactions on Microwave Theory and Techniques*, vol. 44, no. 11, pp. 2099–2109, 1996.
> DOI [10.1109/22.543968](https://doi.org/10.1109/22.543968). The open loops, their couplings and the cross-coupled square.

> R. N. Bates, "Design of Microstrip Spur-Line Band-Stop Filters," *IEE Journal on Microwaves, Optics and Acoustics*,
> vol. 1, no. 6, p. 209, 1977. DOI [10.1049/ij-moa.1977.0029](https://doi.org/10.1049/ij-moa.1977.0029).

> J.-S. Hong and M. J. Lancaster, *Microstrip Filters for RF/Microwave Applications*, Wiley, 2001.
> DOI [10.1002/0471221619](https://doi.org/10.1002/0471221619). Ring resonators, bandstop filters and the
> coupling-coefficient design method.

> J. Lange, "Interdigitated Stripline Quadrature Hybrid," *IEEE Transactions on Microwave Theory and Techniques*,
> vol. 17, no. 12, pp. 1150–1151, December 1969. DOI [10.1109/TMTT.1969.1127115](https://doi.org/10.1109/TMTT.1969.1127115).

> W. P. Ou, "Design Equations for an Interdigitated Directional Coupler," *IEEE Transactions on Microwave Theory and
> Techniques*, vol. 23, no. 2, pp. 253–255, February 1975.
> DOI [10.1109/TMTT.1975.1128534](https://doi.org/10.1109/TMTT.1975.1128534).

> V. K. Tripathi and H. Lee, "Spectral-Domain Computation of Characteristic Impedances and Multiport Parameters of
> Multiple Coupled Microstrip Lines," *IEEE Transactions on Microwave Theory and Techniques*, vol. 37, no. 1,
> pp. 215–221, January 1989. DOI [10.1109/22.20041](https://doi.org/10.1109/22.20041). The N-line section's multiport
> parameters from its modes.

> C. R. Paul, *Analysis of Multiconductor Transmission Lines*, 2nd ed., Wiley-IEEE Press, 2007.
> DOI [10.1109/9780470547212](https://doi.org/10.1109/9780470547212). The modal decomposition the N-line section and
> the MoM quasi-static kernel share.

> N. Marchand, "Transmission-Line Conversion Transformers," *Electronics*, vol. 17, no. 12, pp. 142–145, December 1944.
> A trade magazine, with no DOI: [a scan of the issue](https://www.worldradiohistory.com/Archive-Electronics/40s/Electronics-1944-12.pdf).

> S. L. March, "Analyzing Lossy Radial-Line Stubs," *IEEE Transactions on Microwave Theory and Techniques*, vol. 33,
> no. 3, pp. 269–271, March 1985. DOI [10.1109/TMTT.1985.1132997](https://doi.org/10.1109/TMTT.1985.1132997). The
> radial stub's closed form.

> F. Giannini, R. Sorrentino and J. Vrba, "Planar Circuit Analysis of Microstrip Radial Stub," *IEEE Transactions on
> Microwave Theory and Techniques*, vol. 32, no. 12, pp. 1652–1655, December 1984.
> DOI [10.1109/TMTT.1984.1132907](https://doi.org/10.1109/TMTT.1984.1132907).

> M. Kirschning, R. H. Jansen and N. H. L. Koster, "Accurate model for open end effect of microstrip lines,"
> *Electronics Letters*, vol. 17, no. 3, pp. 123–125, February 1981.
> DOI [10.1049/el:19810088](https://doi.org/10.1049/el:19810088). The radial stub's effective radius.

> K. S. Ang and I. D. Robertson, "Analysis and Design of Impedance-Transforming Planar Marchand Baluns," *IEEE
> Transactions on Microwave Theory and Techniques*, vol. 49, no. 2, pp. 402–406, February 2001.
> DOI [10.1109/22.903108](https://doi.org/10.1109/22.903108).
