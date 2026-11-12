# Brief series CXP — the coaxial (annular) lumped port, and a connector-fed loop sensor example

**Goal.** A coaxial connector in a 3D view can be fed where a real cable feeds it — across the ring of dielectric
between its pin and its shell — on Palace, EMerge and openEMS, and an example workspace shows that port feeding a
connector soldered to a printed loop sensor.

## What the port is

A **coaxial lumped port** is a lumped port whose sheet is an **annulus**: the flat ring of a coax's cross-section
between the inner conductor (radius *a*) and the outer one (radius *b*). Where a rectangular lumped port imposes a
field of one direction across its sheet, this one imposes a **radial** field that falls as 1/r,

    E(r) = V / (r · ln(b/a)) · r̂,

which is exactly the transverse field of the coax's TEM mode. Its current flows radially through the ring, so the
ring's sheet impedance that terminates the port in Z0 is Zs = 2π·Z0 / ln(b/a). The port's voltage is the line
integral of E from the inner conductor to the outer one along any radius, and every radius gives the same answer.

Palace states this port directly: a `LumpedPort` whose `Direction` is `"+R"` or `"-R"`. Palace finds the centre and
both radii from the boundary elements themselves (a coplanar annulus, the centre of its bounding ball, the outer
radius half the ball's diameter, the inner radius the nearest point to the centre), so **the annulus may face any
direction** — checked against Palace 0.18.1's `CoaxialElementData`.

## Why it matters for connectors

A coax connector today is fed one of two ways, and neither is what a newcomer reaches for:

- **A rectangular gap port** across a short gap between the bore's floor and the pin's end (the 3D Connector
  example). It forces one field direction across the gap, so the gap's capacitance is part of every answer.
- **A coax wave port** on the air box's face (the 3D Wave Ports example's Launch). It is exact, but the coax must
  run to the air box's face, and a 2D mode is solved for the face.

The annular port is fed in the coax's own field and does not need the air box's face, so it sits at a connector's back
face wherever the connector is. It assumes the TEM mode, which is the only mode a connector carries in its band.

## What circuitRF has today

The NUMERIC layer already carries it: `Em3dPort.Annulus` (`Em3dAnnulus(InnerRadiusM, OuterRadiusM, Outward)`),
written by `PalaceConfigWriter.WriteDirection` as `+R`/`-R`, meshed by `GmshGeoWriter.EmitAnnulus`, drawn by
`Scene3DBuilder`, lowered for DC by `Em3dDcLowering`, and refused by name by `EmergeLoweringRules`. But:

1. **Nothing a user writes creates one.** No `.c3d` key, no tool and no menu makes an annular port; only tests
   construct `Em3dAnnulus`.
2. **Only a z-facing annulus is accepted** (`Em3dProblem` validation, `EmitAnnulus`, `Scene3DBuilder`). An edge-launch
   connector's axis lies in the board's plane, so its annulus faces x or y.
3. **EMerge refuses it**, and **openEMS refuses it** with the wrong reason (its direction "is not along an axis").

## The briefs, in order

| Brief | What | Depends on |
|---|---|---|
| `brief-cxp-1-c3d-annular-port.md` | The `.c3d` key, the resolver (inner/outer conductors inferred), lowering to `Em3dPort.Annulus` in any of the three planes, Palace end to end, `check`/`explain`, reference docs | — |
| `brief-cxp-2-annular-port-editor.md` | The editor: Make Port ▸ Coaxial on a ring face, the Port tool's coaxial shape, the properties panel, the overlay | 1 |
| `brief-cxp-3-emerge-annular-port.md` | EMerge: a radial lumped port in `circuitrf_emerge.py` | 1 |
| `brief-cxp-4-openems-annular-port.md` | openEMS: a radially weighted source, a ring termination and radial probes | 1 |
| `brief-cxp-5-loop-sensor-example.md` | The example workspace: a printed split-ring loop sensor fed through a real-size edge-launch SMA (built with the 3D Connector's operations), with the paper it follows cited in its README; user docs | 1, 2; 3 and 4 for its EMerge/openEMS setups |

Briefs 3 and 4 are independent of each other and of 2.

## Decisions already made

- **D1 — One port kind, not a new kind.** An annular port is a LUMPED port with a ring for its sheet. It has a Z0, a
  polarity, `Model`, `Flip` and a reference plane like every lumped port. It is not a wave port and does not solve a
  mode.
- **D2 — Polarity is the coax's.** The conductor the INNER circle touches is positive and the one the OUTER circle
  touches is negative, unless `Positive`/`Negative` state otherwise; `Flip` swaps them, as it does on every port.
  `Em3dAnnulus.Outward` is true exactly when the negative conductor is the inner one (Palace's `+R`).
- **D3 — Any of the three planes.** XY, YZ and XZ, centred anywhere. Not a turned or tilted plane, and never on a
  cylinder or a quad (those stay refused by name, as `Em3dProblem` already refuses a turned annulus).
- **D4 — Every solver runs it, or refuses by name.** No solver writes an annulus as a rectangle. Where a solver can
  state it only under a condition (openEMS: not on a PEC wall; EMerge: wherever brief 3's gate shows it holds), the
  refusal names the condition and the alternative.
- **D5 — The coax wave port stays.** It remains the exact port for a coax that ends on the air box's face; the docs say
  when to use which. Nothing about it changes.
- **D6 — No vendor or tool names.** The paper brief 5 follows is cited in that example's README only (the owner asked
  for it). The paper's own simulation tool is not named anywhere, nor is any website the question came from.

## Gates, for the whole series

1. **Nothing shipped moves.** Every shipped example's `.c3d` reads, resolves and lowers to byte-identical solver input
   on every path this series touches; no shipped file states an annulus today.
2. **A matched coax line is matched, on every solver that runs it.** One fixture, used by briefs 1, 3 and 4: a straight
   50 Ω coax (the 3D Connector's 0.4 mm pin in a 1.34 mm PTFE bore, εr 2.1 — small, so it meshes quickly; the example's
   real-size SMA is not needed to test the port), 20 mm long, an annular port at each end,
   solved at three frequencies (1, 5, 10 GHz). It must show |S11| below the tolerance each brief states, |S21| within
   its tolerance of the conductor-loss closed form, and ∠S21 within its tolerance of −β·ℓ with β = 2πf√εr/c.
3. **Orientation does not matter.** The same fixture along x, y and z gives the same S-parameters on Palace, to
   ±0.01 dB and ±0.1°.
4. **The example's numbers are recorded and held.** Brief 5's README quotes only numbers its runs produced, and a
   `Category=Benchmark` regression test holds them as `Em3dConnectorExampleTests` holds the 3D Connector's.

## Rules that apply throughout

- **Tests:** one per claim, named classes only. Never the full suite and never all of `Ui.Tests`. No new timing test.
  The solver fixtures above are `Category=Benchmark` if they cross ~5 s; their wiring (the written `.geo`, Palace JSON,
  EMerge script, openEMS XML) is checked in the routine tier, with no solver.
- **EM runs are short and specific.** The coax fixture is three frequencies. No whole-board run, nothing over a few
  minutes, and a memory watchdog on every ad-hoc run.
- **UI changes are rendered headlessly and looked at** (`tools/UiSnapshot`), and each picture is listed in the
  brief's completion note.
- **Findings go to the nearest `RESOLVED.md`**, never `CLAUDE.md`. Docs are regenerated once, at the end of the
  series (brief 5).
