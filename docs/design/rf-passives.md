# circuitRF — RF passives: one plan per topology

**Status:** six parts — MWPD, the Wilkinson divider, with its unequal-split (`K`) and multi-section (`N`) variants; MBLC
and MRRC, the branch-line and rat-race hybrids; MCLC, MIDC and MCBL, the coupled-line coupler, interdigitated coupler
and balun; MWPD's and MRRC's rings curved or cornered (`Corner`); sites with an optional `Footprint` (§3.5) and a route
helper (§3.6) · **Date:** 2026-11-16

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
(`PlanCoupledLine`) — with the plan node each of its pins joins and a pose (where its pin 1 sits, a turn in quarters). Nothing in a plan is a new kind of line: it is the
vocabulary circuitRF already has, arranged. Every dimension is held on a 2 nm grid
(`PassivePlan.OnGrid`), so halves are whole database units and two pieces that meet in the plan meet to the last unit
in the artwork.

**One model for every plan.** `MicrostripPlanModel` stamps each section through that part's own model
(`MicrostripTeeModel`, `MicrostripCurveModel`, `MicrostripLineModel`, `MicrostripCoupledLineModel`,
`MicrostripMiterBendModel`, `MicrostripBendModel`, with their own validity warnings) into a `LocalAdmittanceBlock` on the plan's nodes and Kron-reduces the internal nodes away — the way
MTEE already stamps the arms it draws (`JunctionArms`).
The block is shared with MTEE for that reason. A topology therefore writes no stamping code at all.

**One drawing path from the same plan.** `PassivePlanPCells.Draw` draws each section through that part's own generator
(`MTeePCell`, `MCurvePCell`, `MlinPCell`, `CoupledLinePCells`, `MMiterPCell`, `MBendPCell`), turned and moved to its pose, and takes the passive's
pins from those parts' own pins. So the model and the layout are the same pieces by construction, not by two
implementations agreeing.

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
Each such place is a plan's *site* (§3.5), which the readout lists and Place Site Parts fills.

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
| `N` | yes, 1 | Sections, 1 to 3; the pins are 3 + 2·N (§3.1). |
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

**Multi-section, `N` = 2, 3** (Cohn). The even-mode half is a chain of quarter waves from 2·Z0 to Z0, made equal-ripple
over the band f1 = 2F/(1 + Band) … f2 = 2F·Band/(1 + Band) and symmetric (z_k·z_(N+1−k) = 2), so one unknown, z_1, is
found by bisection where the reflection vanishes at the first Chebyshev zero, cos θ = cos θm·cos(π/(2N)), θm = π/(1 + Band).
The odd-mode half is a short at the input, the same sections, and half of each resistor to ground at every junction; the
resistors make that half's admittance exactly Z0⁻¹ at the even mode's own zeros (θ1 and, by the lines' symmetry,
π − θ1; for N = 3 also π/2), N real equations in N conductances solved by Newton's method from all ones. Matched in both
modes there, every port is matched and the outputs isolated. The tests hold the N = 2 solve to an independent closed
form (the two-section transformer's z_1 = √(A + √(A² + 2)), A = 1/(2·tan²θ1), and the two resistors from the odd-mode
match), and the N = 2 and 3 solutions on ideal TLIN lines to |S| < 1e-9 at every zero. The ring is the section next to
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
  `WilkinsonPlan.BroadbandSections` bisects and runs a 3 × 3 Newton. It is still pure and fast enough for the readout.
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

**MBLC, the 3 dB branch-line (90°) hybrid**, one or two sections. Pins `1`–`2` and `4`–`3` are the two through lines;
driven at 1, 2 is through, 3 coupled and 4 isolated. Sections: an MTEE at each junction (four, or six for two sections),
a `PlanLine` for each series and shunt arm, and a `Z0` `PlanLine` feed to each pin, in line with the through line. One
section: series arms Z0/√2, shunt arms Z0. Two sections: series arms Z0 and shunt arms (1 + √2)·Z0, √2·Z0, (1 + √2)·Z0.
The synthesis method is Levy and Lind's and Muraguchi, Yukitake and Naito's (§7); these values come from its even/odd
conditions, in normalised admittances (match and isolation b = 2at²/(1 + a²), 3 dB b/t² = 1/√2, with t = 1), and the
circuit model checks them (`HybridSParamTests`: matched, isolated and 3 dB at F). Every arm is λg/4 at F between the
reference planes of the tees it joins.

**MRRC, the rat-race (180°) hybrid.** Pin `1` is Σ, `4` is Δ, `2` and `3` the outputs. Sections: four MTEEs on a ring
of √2·Z0, `PlanLine` runs between them and two 180° `PlanCurve`s; each pin is the tee's branch, one W0 long. The pins
sit in ring order 2, 1, 3, 4, spaced λg/4, λg/4, λg/4 and 3λg/4 between reference planes.

| Parameter | Part | Stored | Meaning |
|---|---|---|---|
| `Z0` | both | yes, Ω | Port impedance; the arms' impedances follow from it. |
| `F` | both | yes, Hz | Centre frequency: every arm or spacing is λg/4 (or 3λg/4) here. |
| `N` | MBLC | yes | Sections, 1 or 2. |
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

Refusals: `device.branchline.*` (a non-positive spec, `N` other than 1 or 2, an unreachable width, an arm too short for
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

### 3.5 Sites, and an optional `Footprint`

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

### 3.6 The route helper

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

## 4. What a new topology costs

1. `src/Core/Devices/Microstrip/<X>Plan.cs` — the pure plan (and a section kind, with one case in
   `MicrostripPlanModel.Build` and one in `PassivePlanPCells.Draw`, if it needs a part the plans do not use yet).
   `PlanBend` (MMITER and MBEND, for `Corner`) cost exactly one model case and one drawer case; because MBEND stamps in
   branch (Z) form, the shared local block also learned to take branch rows, once, for any such part
   (`LocalAdmittanceBlock`).
2. `ComponentModelFactory` — the type name, `_parameterizedTypes` and one dispatch line to `MicrostripPlanModel`.
3. `SchematicModel.SymbolKind`; `ComponentTypeRegistry` (entry, terminals text, `EngineReference`, parameter
   descriptions, `ModelValidity`, `HasNoiseSwitch`, `DefaultParameters`, token); `EditableSchematic.SymbolPortDefs`;
   `BuiltInSymbols` (glyph); `MicrostripSubstrateInjection` (kind set); `SymbolArtworkGenerator`.
4. `PassivePlanPCells.Generate<X>` and its `PCellRegistry` row; the generator row in `Lvs/DeviceType`; the generator
   in `GroundPourPlanner`'s line set. A part whose pins follow a parameter also needs its count in
   `EditableSchematic.PortCount`, `SymbolPortDefs.For` and `BuiltInSymbols.Primitives` (§3.1).
5. Tests: the plan against its closed form, the composite against its parts by hand, a schematic-to-layout fixture; the
   records in `LibraryCatalogTests` and `PCellGeneratorVersioningTests`.
6. Its sites (§3.5): the plan's `Sites`; a kind with sites in `SchematicViewModel.HasSites`, and one whose site span is a
   stored length in `MicrostripSubstrateInjection.HasSiteFootprint`. A folded line is `PlanRoute` (§3.6).
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
- **`N` must be a plain number**, since it sets the pins. The catalogue (`circuitrf reference`) lists MWPD at its
  five terminals and says in the terminals note how `N` adds pairs.
- **The MWPD generator's version follows MTEE's and MCURVE's**, and with corners MMITER's, MBEND's and MLIN's (MRRC's
  likewise). It draws through them, so a change that bumps one of those generators' versions changes the part's artwork
  too and must bump the part's.

## 6. Possible enhancements

1. **Coupling between side-by-side lines.** The multi-section divider's line pairs run about `Gap` apart and are
   stamped as two uncoupled lines (§5); EM sees the coupling and the circuit does not. A `PlanCoupledLine` per pair would
   close that, with no new model or drawer code.
2. **Phase compensation for MCBL.** Microstrip's odd mode is faster than its even mode, so the outputs sit about 170°
   apart rather than 180°. A first section a few percent longer, solved in the plan, would restore 180°.
3. **An EM cell for MIDC.** The Lange is checked in the circuit, Update Layout and LVS only: Planar cannot put a jumper
   across a finger. A bond-wire jumper in Planar, or drawn wires in 3D, would let the example check it as it checks the
   others.
4. **Loop ports on cut cell instances.** A part on drawn pads whose copper closes a loop has its port (§5); a cut cell
   instance whose pins the copper joins is still refused, since its pins are ground-referenced with no gap between two
   of them.
5. **A "Dividers and couplers" palette category.** Six new kinds under Microstrip make that list long; a category of
   their own would make them easier to find.
6. **Smooth circuit curves in the example's plots.** Each bench sweeps only the EM runs' frequencies, so every marker is
   a solved point and the circuit line is coarse; a `.cdd` that also reads the cell's own fine sweep would draw it
   smoothly.

7. **Bond wires and air bridges from Place Site Parts.** A jumper site (MIDC's) is left with a note, since a wire part
   needs its own design; placing one would fill the last kind of site by hand.
8. **Die parts sized from the site's value.** On an MMIC or RFIC stack Place Site Parts puts an ideal R, L or C with no
   land; a TFR or MIMCAP sized for the value from the process would make a die design complete in one step.
9. **A `Footprint` path relative to the schematic in a `.cnl`.** A written netlist resolves a cell path against its own
   folder; one written elsewhere keeps the span extraction resolved but warns, since its folder holds no such cell.

## 7. References

> E. J. Wilkinson, "An N-Way Hybrid Power Divider," *IRE Transactions on Microwave Theory and Techniques*, vol. 8,
> no. 1, pp. 116–118, January 1960. DOI [10.1109/TMTT.1960.1124668](https://doi.org/10.1109/TMTT.1960.1124668).

> D. M. Pozar, *Microwave Engineering*, 4th ed., Wiley, 2012: §7.3, the Wilkinson power divider; §7.5 and §7.8, the
> quadrature and 180° hybrids; §7.6 and §7.7, coupled-line directional couplers and the Lange coupler. A book, with no
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

> M. Kirschning and R. H. Jansen, "Accurate model for effective dielectric constant of microstrip with validity up to
> millimetre-wave frequencies," *Electronics Letters*, vol. 18, no. 6, pp. 272–273, 1982.
> DOI [10.1049/el:19820186](https://doi.org/10.1049/el:19820186). The dispersion λg/4 is measured in.

> M. Kirschning and R. H. Jansen, "Accurate Wide-Range Design Equations for the Frequency-Dependent Characteristic of
> Parallel Coupled Microstrip Lines," *IEEE Transactions on Microwave Theory and Techniques*, vol. 32, no. 1, pp. 83–90,
> January 1984. DOI [10.1109/TMTT.1984.1132616](https://doi.org/10.1109/TMTT.1984.1132616); corrections, vol. 33, no. 3,
> p. 288, 1985, DOI [10.1109/TMTT.1985.1133005](https://doi.org/10.1109/TMTT.1985.1133005). The modes the coupled-line
> synthesis inverts.

> J. Lange, "Interdigitated Stripline Quadrature Hybrid," *IEEE Transactions on Microwave Theory and Techniques*,
> vol. 17, no. 12, pp. 1150–1151, December 1969. DOI [10.1109/TMTT.1969.1127115](https://doi.org/10.1109/TMTT.1969.1127115).

> W. P. Ou, "Design Equations for an Interdigitated Directional Coupler," *IEEE Transactions on Microwave Theory and
> Techniques*, vol. 23, no. 2, pp. 253–255, February 1975.
> DOI [10.1109/TMTT.1975.1128534](https://doi.org/10.1109/TMTT.1975.1128534).

> N. Marchand, "Transmission-Line Conversion Transformers," *Electronics*, vol. 17, no. 12, pp. 142–145, December 1944.
> A trade magazine, with no DOI: [a scan of the issue](https://www.worldradiohistory.com/Archive-Electronics/40s/Electronics-1944-12.pdf).

> K. S. Ang and I. D. Robertson, "Analysis and Design of Impedance-Transforming Planar Marchand Baluns," *IEEE
> Transactions on Microwave Theory and Techniques*, vol. 49, no. 2, pp. 402–406, February 2001.
> DOI [10.1109/22.903108](https://doi.org/10.1109/22.903108).
