# CXP-5 — example workspace: a connector feeding a printed loop sensor

Read `brief-cxp-0-overview.md` first. Needs briefs 1 and 2; its EMerge and openEMS setups need briefs 3 and 4.

## Why

The commonest question about simulating a small printed antenna is what to do with its connector: a port across the
loop's two open ends is quick, but the board is measured through a coaxial connector soldered to it, and the two do not
always agree. No example shows a connector feeding an antenna, and none shows how to feed a connector so its own
parasitics are in the answer and the port's are not.

The design followed is the split-ring loop sensor of a published, open-access paper, chosen because it states its
dimensions, its band (about 1 GHz, swept 0.75–1.25 GHz in measurement) and a simulated |S11| to compare with:

> W. Cui, J. Wang, C. Ding, P. Van Cappellen, E. A. Ho, C. L. Ren, "A functionalized microwave biosensor for rapid,
> reagent-free detection of E. coli in water samples," *Biosensors and Bioelectronics* 278 (2025) 117334.
> https://doi.org/10.1016/j.bios.2025.117334 (open access, CC BY-NC-ND 4.0)

The README cites it in those words. Its figures and text are NOT reproduced (the licence forbids derivatives, and the
example draws its own). Its simulation tool is not named (D6).

## The design, as the paper gives it and as assumed

Stated in the paper (its Fig. 2): a board **25 × 25 mm**; an **outer ring of radius R2 = 8.5 mm**, fed at its bottom
through **two parallel arms l2 = 3 mm apart** running to the board's edge; an **inner, split ring of radius
R1 = 6.5 mm** with a **capacitive gap l1 = 1 mm**, centred under a sample reservoir in a PDMS layer bonded on top; an
end-launch coaxial connector soldered at the arms; resonance designed **near 1 GHz**, simulated |S11| about **−14 dB**
at its dip.

Not stated, so assumed and written as VARs with a sentence each in the README: the board's laminate and thickness
(FR-4, εr 4.4, tan δ 0.02, 1.6 mm), the copper (35 µm), the trace width (1 mm, read from the figure's proportions),
whether R1 and R2 are to the traces' centre lines (assume so), how l2 is measured (assume between the arms' centre lines),
and the PDMS layer's thickness, permittivity and the reservoir's size.

**No ground plane**: the loop is fed between its two arms, the pin on one and the connector's shell on the other.

**Do not tune to land on 1 GHz.** Build to the paper's numbers and the stated assumptions, run, and report where the
resonance falls. If it is far from 1 GHz, find which assumption moves it (one short run each, a few frequencies around
the dip) and state that in the README with its numbers. Change an assumption only with a reason the paper supports.

## Cells

| Cell | View | What it is |
|---|---|---|
| **Sensor** | layout, mm | The 25 × 25 mm board: the two rings, the gap, the two arms to the edge |
| **SMA Flange** | 3D view, mm | The SMA's flange and its four ground legs, drawn here; the source of `sma-flange.step` |
| **SMA** | 3D view, mm | A real-size edge-launch SMA jack on its own: body with its PTFE bore, flange from STEP, pin with a filleted tip, a coaxial port at its back face. Placed by the two cells below |
| **Ideal Port** | 3D view, mm | *Sensor* placed, and a rectangular lumped port joining the two arms' ends at the board's edge: the quick model |
| **With Connector** | 3D view, mm | *Sensor* placed, and *SMA* placed at the board's edge. **P1 is the coaxial port** at the SMA's back face, made with Make Port ▸ Coaxial |

**The SMA is real size.** Take its interface dimensions from the SMA interface standard (MIL-STD-348): a 1.27 mm pin in
a PTFE bore of about 4.1 mm, a 1/4-36 threaded body (modelled as a plain 6.35 mm cylinder: threads are outside the
field) and an edge-launch flange whose legs straddle a 1.6 mm board, two above it and two below. State each dimension as
a VAR with its source in the README, and the coax's impedance by the closed form η₀/(2π√εr)·ln(D/d) with the
technology's PTFE εr. If that is more than 1 Ω from 50 Ω, say so and why (the PTFE's εr, the bore's tolerance) rather
than adjusting a dimension to reach 50.

**Fitted to the paper's board.** The pin rests on one arm, sunk half the copper's thickness (the 3D Connector's rule), so
`axis_z` = board thickness + copper/2 + pin radius. The 1.6 mm board is the gap a standard edge-launch SMA's legs are
made for. One top leg lands on the other arm 3 mm away (the paper's l2), standing for the shell's solder joint; the
other three legs touch nothing, as on the real board with no ground plane, and are modelled anyway. If a standard
leg position does not land on the arm, say so and move the leg, not the arm.

**Built as the 3D Connector's connector is built**: Box and Cylinder, Boolean ▸ Subtract with Keep tools for the bore,
the flange exported to STEP and imported back, Boolean ▸ Unite, Fillet on the pin's tip. The README names that example
for the gestures rather than repeating them. Copy the 3D Connector's technology entries (PTFE, connector alloy) into this
workspace's own `.ctech`.

A real-size SMA is also kinder to the solvers than the 3D Connector's 0.4 mm pin: its smallest feature is three times
larger, so each solver's mesh or grid near the pin is coarser. Report the mesh size or cell count each run used.

## Setups and the questions the README answers

Each cell gets a **Palace** setup over **0.4–1.6 GHz** (the paper's plotted range), with enough points to place the dip
to 1 MHz (an adaptive sweep if the setup offers it), and an **EMerge** setup if brief 3 shipped. An **openEMS** setup
ships only if a dry run says it finishes in minutes; a 1 GHz resonance resolved on 0.1 mm cells needs a long ring-down,
and if it does not, the README says so with the dry run's numbers. Every shipped run finishes in a few minutes on the
reference machine (Apple M4, 16 GB).

1. **Where is the resonance, and how deep?** Against the paper's ≈1 GHz and ≈ −14 dB, with the assumptions stated.
2. **Does the connector move it?** *Ideal Port* against *With Connector*: the resonance's shift in MHz and the dip's
   depth. Say what causes the difference (the connector's length of line, the tab, the pin's step onto the arm), from
   the numbers, not by assertion.
3. **Why a coaxial port.** One extra run of *With Connector* fed by a rectangular gap port at the bore's floor, as the
   3D Connector feeds it, with the difference it makes at the dip.
4. **Does the connector alone behave?** *SMA* on its own, the pin's board end open: one short run whose |S11| near 1 GHz
   says how much of the connector is a 50 Ω line and how much the launch's step. It sets up question 2.
5. **What a sample does** (if the reservoir is modelled): the shift when the reservoir holds water against when it is
   empty — the sensing mechanism the paper measures. One VAR switches it.

## Also in this brief

- `examples/examples.json` row; `ExampleWorkspacesTests` passes.
- `expected-numbers.json` and a `Category=Benchmark` regression test holding every shipped run's |S11| at every
  frequency, as `Em3dConnectorExampleTests` does, with a routine-tier test that the README quotes only recorded values.
- **The 3D Connector and 3D Wave Ports READMEs** say "a 3D view's ports are rectangles" and explain the gap port by
  it. That is no longer true: restate those paragraphs as they now stand (the gap is kept there for its comparison,
  and the coaxial port exists), with no change-history wording.
- **User docs:** *EM Setup ▸ Ports* gains *Coaxial ports* (what the ring imposes, its polarity, its Z0, which solvers
  run it and where, and when the coax wave port is still the better choice); *The 3D Editor* gains Make Port ▸ Coaxial
  and the Port tool's ring. Regenerate the changed pages with DocGen once, at the end of the series.
- MCP `reference examples` lists the example with its one-line summary.

## Gates

1. Every number in the README is one a run printed, and the regression test holds them.
2. `check` on the workspace reports no error; any warning is explained in the README.
3. Pictures (UiSnapshot / `render`, looked at): the board, the connector on the board with the ring port visible, and
   one |S11| plot of *Ideal Port* against *With Connector*.
