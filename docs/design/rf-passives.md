# circuitRF — RF passives: one plan per topology

**Status:** six parts — MWPD, the Wilkinson divider; MBLC and MRRC, the branch-line and rat-race hybrids; MCLC, MIDC
and MCBL, the coupled-line coupler, interdigitated coupler and balun · **Date:** 2026-11-16

Dividers, couplers and baluns on microstrip, each one built-in part that is defined once and read the same way by the
schematic, the layout, LVS and EM. Stage 1 built the Wilkinson divider and the shared pieces; the hybrids and the
coupled-line parts were added on the same pieces, one subsection each in §3.

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
an MLIN (`PlanLine`), an MCLIN or a set of interdigitated strips (`PlanCoupledLine`) — with the plan node each of its
pins joins and a pose (where its pin 1 sits, a turn in quarters). Nothing in a plan is a new kind of line: it is the
vocabulary circuitRF already has, arranged. Every dimension is held on a 2 nm grid
(`PassivePlan.OnGrid`), so halves are whole database units and two pieces that meet in the plan meet to the last unit
in the artwork.

**One model for every plan.** `MicrostripPlanModel` stamps each section through that part's own model
(`MicrostripTeeModel`, `MicrostripCurveModel`, `MicrostripLineModel`, `MicrostripCoupledLineModel`, with their own
validity warnings) into a `LocalAdmittanceBlock` on the plan's nodes and Kron-reduces the internal nodes away — the way
MTEE already stamps the arms it draws (`JunctionArms`).
The block is shared with MTEE for that reason. A topology therefore writes no stamping code at all.

**One drawing path from the same plan.** `PassivePlanPCells.Draw` draws each section through that part's own generator
(`MTeePCell`, `MCurvePCell`, `MlinPCell`, `CoupledLinePCells`), turned and moved to its pose, and takes the passive's
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

### 3.1 MWPD, the equal-split Wilkinson divider

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
| `K`, `N` | reserved | Split ratio and section count, Stage 2. |
| W, arm W, L, ring radius, isolation R | readout | Derived by `WilkinsonPlan.Build` on the substrate. |

Refusals: a non-positive spec, an impedance no width reaches on the substrate, and a quarter wave too short to make a
ring (high F or a wide Gap).

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

## 4. What a new topology costs

1. `src/Core/Devices/Microstrip/<X>Plan.cs` — the pure plan (and a section kind, with one case in
   `MicrostripPlanModel.Build` and one in `PassivePlanPCells.Draw`, if it needs a part the plans do not use yet).
2. `ComponentModelFactory` — the type name, `_parameterizedTypes` and one dispatch line to `MicrostripPlanModel`.
3. `SchematicModel.SymbolKind`; `ComponentTypeRegistry` (entry, terminals text, `EngineReference`, parameter
   descriptions, `ModelValidity`, `HasNoiseSwitch`, `DefaultParameters`, token); `EditableSchematic.SymbolPortDefs`;
   `BuiltInSymbols` (glyph); `MicrostripSubstrateInjection` (kind set); `SymbolArtworkGenerator`.
4. `PassivePlanPCells.Generate<X>` and its `PCellRegistry` row; the generator row in `Lvs/DeviceType`.
5. Tests: the plan against its closed form, the composite against its parts by hand, a schematic-to-layout fixture; the
   records in `LibraryCatalogTests` and `PCellGeneratorVersioningTests`.
6. Docs: a subsection under Dividers and couplers in `components.md`, and the example's README table.

## 5. Known limits

- **EM cannot cut the isolation resistor out as a component port.** Both component-port builders refuse a part whose two
  pads lie on one connected piece of copper (`EmPlanarComponentPorts.Construct`, and the 3D builder's "land on one
  conductor"), and the ring joins R2 to R3. The resistor is left out of the run with a warning, so an EM result is the
  divider without it. By symmetry an input drive puts no current through R2–R3, so S11, S21 and S31 are unaffected; S22
  and S23 are compared against the circuit model with the resistor removed.
- **MTEE's closed forms are calibrated at εr 9.9** and say so on any other substrate; MCURVE's effective-radius rule is
  stated for radii of 3W to 7W, and a low-frequency ring is larger. Both warnings reach Messages through the part.
- **The MWPD generator's version follows MTEE's and MCURVE's.** It draws through them, so a change that bumps either
  generator's version changes MWPD's artwork too and must bump MWPD's.

## 6. Questions for the maintainer

1. **Specs as parameters** (Z0, F, later K, N, C), as MKLOPF does, against the "geometry is the input" rule MLIN keeps.
   The readout shows the geometry; nothing derived is stored.
2. **Part names.** MWPD here (microstrip Wilkinson power divider, the literature's abbreviation); the later stages need
   names that are circuitRF's own rather than a commercial catalogue's.
3. **Lumped parts outside the component, on pins**, rather than inside the PCell (which would need sub-instances in the
   PCell contract).
4. **`Gap` equal to the resistor's pin span**, as a stored layout choice with the 0402's span as its default.
5. **About eight new kinds in the closed registry**, all under Microstrip today; a "Dividers and couplers" palette
   category may read better once there are several.
6. **Component ports across a loop.** Should the component-port builders accept a part whose pads are joined only through
   distributed copper elsewhere (a Wilkinson's resistor, a balun's), with the pads themselves not touching?

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
