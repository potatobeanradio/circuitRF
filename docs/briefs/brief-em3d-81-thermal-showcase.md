# Brief 81 — the thermal showcase: three example workspaces

**Series:** [3D thermal, fifth series](brief-em3d-71-thermal-overview.md) · **Tag:** `R-em3d81-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §9; overview §0, §2A, gate 4
**Area:** `examples/Thermal Die to Heatsink/`, `examples/Thermal Channel vs Surface/`,
`examples/Thermal Output Wires/` (all new), `examples/examples.json`,
`docs/user/src/reference/` (a new `thermal.md`; `drawing-in-3d.md`, `file-formats.md`, `cli.md`,
`em-solvers.md`), the figure catalogue, `tests/`
**Depends on:** 72–80 · **Blocks:** — (the series' last brief)

---

## 0. What this brief delivers

The three scenarios the owner described, each as an example a newcomer opens from *Tools ▸ Examples*, with its
numbers measured once and quoted everywhere, and the user page that teaches thermal through them. Brief 70's
pattern throughout: one `expected-numbers.json` per example, README and user page quoting its **text**, and a
test holding the three together.

**Every material, part and process is generic** — no device maker, laminate product, mould-compound product or
package name, anywhere (CLAUDE.md, *Commercial Vendor References*). Materials come from the shipped
`generic-materials.cmat` (brief 73).

---

## 1. `R-em3d81-1` — *Thermal Die to Heatsink* (scenario 1)

A GaN-on-SiC die with a multi-finger FET drawn as one heat-source sheet over its finger area, sintered-silver
attach to a copper–molybdenum flange, the flange soldered to a two-layer board with a grid of plated vias, the
board on a thermal interface material on a heatsink face fixed at a VAR `Ths`.

- **Setups:** `Steady` (a power sweep of `Pdiss`); `ViasAsBlock` (the same with the via field's effective block
  enabled — brief 76).
- **Probes:** the die's top (Max), the flange's bottom face (Avg — the package/board interface the owner wants
  to de-embed), a via barrel (Max), the heatsink interface (Avg).
- **Measures:** `Rth_jc = (Tmax(die_top) - Tavg(flange_bot)) / Pdiss`, `Rth_ja`, `T_interface`.
- The README states what "case temperature" means here (the flange bottom's **average**) and that a lab's
  definition may differ (a centre-point thermocouple), and shows the `Max` alternative's number beside it.

## 2. `R-em3d81-2` — *Thermal Channel vs Surface* (scenario 2)

One finger's cross-section extruded along its width: SiC substrate, GaN epitaxy with the GaN/SiC interface
resistance from the technology, a passivation layer, the gate, a **field plate** over the channel, source and
drain metal. The heat source is a narrow sheet at the drain-side gate edge. A mesh region round it (brief 72 Q7's
sizes). A symmetry plane at the finger's centre line (brief 76).

- **Probes:** the channel (Max), an **IR spot** on the top surface above the channel (a `Spot`, diameter a VAR
  `ir_spot`, default a few micrometres), a `Line` from the channel up through the field plate to the surface.
- **Measures:** `dT_ch_ir = Tmax(channel) - T(ir_spot)`, the owner's offset; `Rth_ch`.
- **A second cell**, *Eight Fingers*, repeats the finger at its pitch for the Rth matrix, Z_th and a pulse setup
  (brief 80), so the centre-versus-edge finger difference and a 10 % duty radar pulse are shown.
- The README explains **why** the offset exists (the field plate hides the channel; the IR spot averages) and
  shows the offset against `ir_spot` at two values — the setting that moves the answer most.

## 3. `R-em3d81-3` — *Thermal Output Wires* (scenario 3)

A die pad, a row of gold wedge–wedge output wires (as a `.wBond` array reached through the layout **and**, in a
second cell, the same row drawn as `.c3d` wires — brief 78's identity, shown), a package lead, the whole in a
generic mould compound whose glass-transition temperature is the wires' probe `LimitC`.

- **Setups:** `DcSweep` (a DC current sweep past runaway — brief 77); `RfHarmonics` (DC plus two harmonics, one
  entered Peak and one RMS — brief 78); `FromHB` (the power sweep of a small schematic in the workspace whose
  output network instances this view's S-parameter result — brief 79).
- **The schematic** is minimal and generic: a nonlinear FET model already shipped in circuitRF, a bias network,
  the 3D view's S-parameter block as its output interconnect, a load. It exists to drive currents, not to be a
  good amplifier, and the README says so.
- **Results quoted:** the hottest wire and its temperature at the top of each sweep, the runaway current, and
  the `LimitC` sentence (*"w1[k] reaches … at Pin ≈ …"*).
- **Conductive balance A/B**: `DcSweep` run with both switches, with `KOfT` off, and with `SigmaOfT` off; the three
  hottest-wire temperatures at one current in `expected-numbers.json` — **what k(T) actually contributes**, the
  owner's question.
- **The outer-EM question** (overview §4): one extra number — the per-wire RF share recomputed with each wire's
  resistance at its solved temperature (wBond's `ImpedanceReduction` with R included) against the inductive share
  used — so the page states, measured, how much an outer EM loop would have changed.

## 4. `R-em3d81-4` — numbers, presets and what was traded away

- `expected-numbers.json` per example: every quoted value, the mesh sizes, element and unknown counts, solver
  path, iteration counts, run time and peak memory on the reference machine, the Gmsh version.
- **Every setting chosen for speed has its alternative and expected numbers in the README**: the mesh one step
  finer (brief 74's convergence check, run once and quoted), P1 vs P2 where it matters, the explicit via field
  against the block.
- **External checks labelled as external**: the scenario-3 DC case against the closed form of brief 72 (W1) for
  one isolated wire, and — when brief 72 has it — the Shah paper's comparison, cited by author and title.

## 5. `R-em3d81-5` — the user pages (doc sources only)

**DocGen is not run by this brief** — the owner regenerates `docs/user` at the end of the series.

- **`reference/thermal.md`** (new): what a thermal setup is; sources, probes, mesh regions, boundaries, contacts;
  electrothermal and **conductive balance** (the name, what Newton balances, the two switches, runaway as an
  answer); wires as 1D elements and why their area is the round one; harmonic currents and Peak vs RMS; the HB
  link; the Rth matrix, Z_th, the Foster fit and pulses; Plot Temperature and the colour range; what is not
  modelled (radiation, RF loss outside wires, the loop back into HB).
- `drawing-in-3d.md`: the three tools and the thermal context menu. `file-formats.md`: the new `.c3d` lists,
  the `Thermal` section, `ThermalInterfaces`. `cli.md`: `em` runs a thermal setup. `em-solvers.md`: a paragraph
  pointing at `thermal.md` and stating that thermal needs Gmsh.
- Figures through the existing catalogue, headless (the renderers the app draws with).

## 6. Gates

1. **Examples open and run**: each registered, each `check`s clean, each setup runs headlessly through `em` and
   reproduces `expected-numbers.json` within its stated tolerance — `Category=Benchmark` for anything over ~5 s.
2. **Numbers agree**: README, user page and `expected-numbers.json` quote identical text (brief 70's test pattern).
3. **Vendor scan**: the three example folders and the new doc sources contain none of the repository's banned
   names.
4. **No personal paths** in any example file.

## 7. Owner check list (Debug build) — the series walk-through

Overview `R-em3d71-4`, steps 1–6, on these three examples, with no terminal at any step. The owner records
anything that did not feel immediate.

## 8. Scope

- **No new capability.** Anything an example needs that a brief did not build is a finding for the owner, not a
  quiet addition.
- Findings in `examples/RESOLVED.md`; never `CLAUDE.md`.
