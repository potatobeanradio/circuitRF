# Brief PCD-9 — Showcase cells, and `Model="short"`

**Series:** [PCell Designer](brief-pcd-0-overview-and-contract.md) · **Tag:** `R-pcd9-n`
**Area:** `examples/PCell Designer/` (`Wilkinson3`, `Spurline`, `DualModePatch`, `Slotline` and their benches),
`examples/examples.json`, `src/Design/Layout/PCells/DrawnMetal.cs`, `src/Core/Devices/MShapeModel.cs`,
`src/Design/Layout/Em/ParametricEm/EmModelSwitch.cs`, `src/Design/PCellDesigner/PCellDesignCheck.cs`,
`tests/Ui.Tests/PCellDesigner/Pcd9*.cs`, `docs/user/src/reference/pcell-designer.md` (Examples),
`docs/design/pcell-designer.md`
**Depends on:** PCD-2 to PCD-8.

---

## 0. What this adds

Four designed cells that show what the designer can make beyond PCD-8's acceptance cell, each authored through the
window's own functions and saved by its Save. To join resistors at one drawn pad, `MSHAPE` gains a third model.

## 1. Requirements

- **`R-pcd9-1` — `Model="short"`.** Every pin of the `MSHAPE` is one circuit node: an ideal junction or star pad. It
  counts as a circuit model (`WithoutCircuitModel` does not name it), and the design check accepts it, quoted as
  `MSHAPE` writes its words.
- **`R-pcd9-2` — 3-way Wilkinson, 2.45 GHz.** Three √3·Z0 = 86.6 Ω quarter-wave arms, and three Z0 = 50 Ω isolation
  resistors meeting on a drawn star pad (`Model="short"`). Check and LVS are clean, and the circuit simulation shows the
  three-way split, the match and the isolation.
- **`R-pcd9-3` — spurline bandstop.** A 50 Ω line with an L-shaped slit, drawn as one `MSHAPE`, with a Stretch binding on
  the slit length. EM-only (`Model="none"`). A closed-form estimate of the notch, a Planar sweep, and an `em=direct` run
  that moves the notch to its target.
- **`R-pcd9-4` — dual-mode square-patch bandpass.** A square patch with a corner-cut perturbation and two orthogonal
  feeds coupled across offset-placed gaps. EM-only, with a Planar sweep showing the two split modes.
- **`R-pcd9-5` — slotline combiner.** The slot is drawn ground copper: two `MSHAPE`s on the ground layer, on net 0, since
  a polygon has no hole. Microstrip feeds cross it. Planar cannot solve a slotted ground (its ground is the unbroken plane
  of the Green's function), so the bench is solved through a 3D view of the layout on EMerge.
- **`R-pcd9-6` — each example:** a bench placing the cell, a `.cdd` the application renders, a README section, its
  `examples.json` row, `circuitrf check` with no error, LVS clean, and a scoped test. No results are committed.

## 2. Results

| Cell | Result |
|---|---|
| `Wilkinson3` (circuit, 2.45 GHz) | S21/S31/S41 = −4.81/−4.95/−5.01 dB, S11 −32.1 dB, isolation −30.0/−47.4/−34.7 dB |
| `Spurline` (Planar) | Estimate 5.45 GHz, swept 5.39 GHz; `em=direct`, 8 solves, slit 9.31 mm, notch 4.97 GHz for a 5 GHz target |
| `DualModePatch` (Planar) | Modes at 5.10 and 5.32 GHz either side of the 5.22 GHz estimate; under-coupled, S21 peaks at −8.2 dB |
| `Slotline` (EMerge, draft mesh) | At 5 GHz, S21 −5.72 dB and S31 −5.87 dB, 179° apart |

## 3. Decisions

- **D1.** A planar 3-way Wilkinson with a star of resistors cannot keep all three outputs outside the star (K2,3), so one
  resistor is a 1206 whose body bridges the middle arm. A 0.3 mm line keeps R2's pad off bend BR2's copper.
- **D2.** Offsets and flips are set on the schematic, because the text view carries no placement. The text is then
  applied again, which redraws the wires and keeps every part's orientation and offset.
- **D3.** The design check joins a footprint at its pad's outer edge, where the planner joins it, not only at its pin.

## 4. Possible enhancements

- **A coupling parameter on `DualModePatch`, tuned by `em=direct`.** At its saved gap the patch is under-coupled; an
  optimisation would bring S21 up to a usable pass band.
- **A finer slotline solve.** The draft mesh reads about 2.7 dB below an ideal 3 dB split; a standard mesh would show how
  much is the mesh and how much the transitions.
