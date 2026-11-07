# Brief series LCP — component ports on a layout: Planar and 3D, from a `.cem`

**Goal.** A `.cem` can name component ports, the same way a `.c3d` setup does today: tick the parts, press Check, run. On
the Planar solver each one is an internal delta gap across a strip joining the part's two pads. On a 3D solver it is the lumped
gap port `C3dComponentPorts` already builds. Users call both **component ports**; the docs say plainly that on Planar they
are gap ports.

**Why a series.** A component port exists only in a `.c3d` setup. `Em3dGenerator.Generate` refuses `ComponentPorts` on a
layout (`ComponentPortsOnLayoutRefusal`), `ShowComponentPorts` is false outside a `.c3d`, `em --component-ports` refuses a
layout's `.cem`, and a Planar run has no port across a part at all: its documentation says a real gap between two pads
cannot be a port, so a part's site has to be redrawn as continuous metal. Co-simulating a board on Planar therefore means
editing its artwork, and a planar `_cosim` cell does not exist.

## The briefs, in order

| Brief | What | Depends on |
|---|---|---|
| `brief-lcp-1-planar-strip-ports.md` | The construction: part pads without a `.c3d`, the strip and its gap, verified, solved on Planar | — |
| `brief-lcp-2-cli.md` | `em`, `explain`, `check`, the MCP tools and the format reference accept a layout's `.cem` | 1 |
| `brief-lcp-3-cem-editor.md` | The Component ports section in the `.cem` editor, with per-part Z0, width and reference | 1 |
| `brief-lcp-4-layout-marks.md` | Read-only marks on the layout canvas from the open `.cem`, following the part | 1, 3 |
| `brief-lcp-5-3d-from-cem.md` | A 3D `.cem` on a layout builds the same ports a `.c3d` does | 1 |
| `brief-lcp-6-new-3d-view.md` | New 3D View carries each `.cem`'s component ports; the two are separate afterwards | 5 |
| `brief-lcp-7-cosimulation.md` | The `_cosim` netlist and cell from a Planar run and a 3D `.cem` | 1, 5 |
| `brief-lcp-8-docs.md` | Where each kind of port is stored, and every page that says otherwise today | all |

## Decisions already made

- **D1 — Component ports live in the setup, never in the `.clay`.** The list is `EmSetup.ComponentPorts`, already part of the
  `.cem` format. Nothing the strip adds is written to the layout. Two `.cem` files on one layout may name different parts.
- **D2 — A component port is not moved by hand.** Its position comes from the part's pads, so it follows the part. Z0, width
  and reference pad are what a user sets, as in a `.c3d` today. Anything unusual remains a drawn port.
- **D3 — The Planar construction is a strip with a delta gap.** A real slot cannot be a port (§ Measured). Same idea as a 3D
  landing rectangle: it exists in the solved problem only.
- **D4 — New 3D View carries component ports from every route.** Each `.cem` naming the layout that has component ports brings
  them into the new `.c3d`, whichever command made it. A copy made once: after that the `.cem` and the `.c3d` are independent,
  and different lists are normal.
- **D5 — The word stays "component port"** in every panel, message and tool. Only the docs and the `explain` walk say
  "delta gap".

## Measured before this series was written

A scratch 18 × 9 mm board on the shipped `pcb-2layer_RO4350B_20mil_1oz` stack: a 1100 µm line through two series parts (C1, C2;
800 µm gap, the trace between them connected to nothing else) with a shunt part at each end (R1, R2; 800 µm gap to a pad with
a via to the plane). Ports on all four parts. Solved twice: `.c3d` component ports on Palace, and on Planar a copy of the
layout with a strip and an `InternalDeltaGap` label drawn across each gap. No code was changed. The Palace figures are its
second refinement pass, because every Palace run hit its time limit before finishing the third; pass 1 to pass 2 moved S by
at most 0.0096.

**What this settles — the construction works:**

| Claim | Result |
|---|---|
| Polarity matches 3D, stating current from pad 1 to pad 2 across the gap | All four ports agree with no sign change |
| Trace sections joined to the rest only through ports cause no low-frequency trouble | At 100 MHz the four ports form one series loop, theory \|S\| = 0.5 everywhere. Planar 0.4996-0.5000; 3D the same to within 0.0025 |
| A strip with a gap, shorted in the circuit, equals solid metal | Planar max \|ΔS\| 0.0000 at every point, 1-10 GHz. 3D sheet, same test: ≤ 0.029, ≤ 0.04 dB, ≤ 0.3° |
| Planar mesh is not where differences come from | 20 → 40 cells per wavelength moves S by ≤ 0.006 at 10 GHz |

**What it does not settle — Planar and 3D differ, and it is not the strip:**

| GHz | Full board, raw max \|ΔS\| | Series parts only, no vias: max \|ΔS\|, S21 phase | C1, C2 solid, ports on R1, R2 only: max \|ΔS\|, S21 phase |
|---|---|---|---|
| 1 | 0.022 | 0.010, 1.0° | 0.035, 2.0° |
| 2.5 | 0.050 | 0.029, 2.3° | 0.081, 4.6° |
| 4 | 0.072 | 0.064, 4.4° | 0.122, 6.7° |
| 10 | 0.112 | 0.166, 9.0° | 0.216, 10.9° |

It is there with no vias and no shunt parts, and there with the series parts as solid metal. With C1 = C2 = 1 pF closed in the
circuit, Planar and 3D read |S41| −11.44 and −12.00 dB at 1 GHz, −4.15 and −4.51 dB at 2.5 GHz, −0.46 and −1.67 dB at 10 GHz. A
sharp resonance magnifies it: 10 nH parts read −15.5 and −9.7 dB at the 4 GHz notch.

**Power loss is the larger part of it.** On the solid-series board at 1 GHz, 1 − Σ|S|² reads 0.0034 on Planar and 0.0151 on 3D.
A hand estimate from skin loss and the dielectric's loss tangent over the 10 mm line gives about 0.004.

**Outside this series, and open.** Whether that difference belongs to the Planar solver, the 3D setup or a default in either is
its own investigation: a plain line with edge ports on both would start it. **No brief here may gate Planar against 3D to a
tolerance,** and the docs must not claim they match.

**Two things the run also found.** Planar refuses a board whose vias are more than 0.1 λ apart (`DirectVerticalKernel` lifts it;
10.3 mm at 10 GHz here), and a board with ground vias will reach this often: LCP-1 keeps that refusal as it stands. And the
Mesh budget note read 614 cells at both 20 and 40 cells per wavelength with `Auto` off, while the note beside it showed the
pitch halving and the answer changed. Not looked into; not part of this series.

## Simulation budget, for every brief

Runs stay short. These are limits, not targets.

| Run | Cost measured here | Rule |
|---|---|---|
| Planar, two series parts, no vias, 6 points | **1.7 s** | The routine gate. Build fixtures on this |
| Planar with ground vias and `DirectVerticalKernel`, 6-11 points | 57-87 s | Only where a via is the point, and by hand, not in a test. Otherwise keep vias close, or the sweep low enough that the refusal does not fire |
| Palace on this small board | over 8 min with 2 ports, over 25 min with 4; none finished its third pass | **No Palace in any test or brief gate.** Assert the assembled problem instead |

- No new `Category=Benchmark` test and no timing assertion. One test per claim.
- Run the named test classes only, never all of `Ui.Tests`.
- If a hand comparison on Palace is ever needed, cap the wall clock, narrow the sweep, and set the pass limit inside the setup's
  `Palace` object: `AdaptiveMaxIterations` at the top level of a setup is silently ignored, which is why two of the runs above
  ran three passes.
- Nothing here is run on a real board.

## Rules that apply throughout

- **No second construction.** `check`, `explain`, the panel's Check and the run all call one builder, so a verdict means the same
  thing everywhere (the `C3dProblemAssembly.Assemble` rule).
- **A part that cannot be built is left out with the reason**, and the others run; the run refuses only when no port is left. This
  is what a `.c3d` does today.
- **Existing results do not move.** A setup with no `ComponentPorts` produces byte-identical output on every path this series
  touches.
