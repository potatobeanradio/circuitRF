# Brief DFR26-7 — A test-point pad component that carries its own EM port

**Series:** designer feedback round 26 (2026-10-24), see DFR26-1 for the list. · **Tag:** `R-dfr26-7-<n>`
**Depends on:** DFR26-1 (the pad-to-plane 3D port and the port-level rule). Do not start before it lands.
**Area:** new `src/Design/Layout/PCells/TestPadPCell.cs` beside `ViaPCell.cs`; `PCellRegistry.cs`;
`src/Design/Schematic/ComponentTypeRegistry.cs` (parameters, symbol); a new `src/Core/Devices/TestPadModel.cs` +
`ComponentModelFactory.cs`; `src/Design/Layout/PCells/GroundPourPlanner.cs` (clearances); `GeneratedCellStore.cs`
(~166, how a generated cell's pins become labels); the EM flatten step that collects port labels
(`EmPortExtraction`, `Em3dGenerator.BuildPorts`); `src/Design/Layout/Em/TraceImpedanceAnalysis*.cs` and the impedance
panel. Findings go in `src/Design/RESOLVED.md` and `src/Core/RESOLVED.md`.
**Tests:** new `tests/Ui.Tests/Layout/TestPadRound26Tests.cs`, `tests/Core.Tests` for the circuit model.

---

## 0. The gap

A production test point on an RF line — a round pad, often on the far side of the board, reached through a via —
loads the line with the pad's capacitance to the nearest plane and the via's inductance. On the designer's ten-layer
board a 1.5 mm bottom-side pad 71 µm from a solid plane, fed through a 1.7 mm via, made a series resonance in the
2.4 GHz band and cost 6 dB. The fixture sets the pad size, so the fix is a cut-out in the planes under the pad, and
the question is how large, on which layers. Their hand estimate (parallel plate plus fringing,
`C ≈ ε₀εᵣπr²/h + ε₀εᵣ·2πr·ln(1 + 2r/h)`) says L9 and L8 both have to be cleared, but they need an EM answer before
spending a board spin.

They asked for a component: a round pad, its diameter and distance to ground as parameters, fed from one of four
sides, with a second port at the pad centre that is either left open (test point not in use) or continues to a
connector. The owner wants it to **create its own port in the 3D EM view** when the centre is used.

## 1. What to do

### R-dfr26-7-1 — The component, `TESTPAD` ("Test Point Pad")

Parameters (lengths SI, shown in mm like the other PCB components):

| Parameter | Meaning | Default |
|---|---|---|
| `PadLayer` | conductor the pad is on | the stackup's bottom signal conductor |
| `D` | pad diameter | 1.5 mm |
| `Feed` | `N` / `E` / `S` / `W` / `Via` — where pin 1 joins | `Via` |
| `FeedW` | width of the feed trace stub when `Feed` is a side | the technology's 50 Ω width on `PadLayer` |
| `ViaFrom` | conductor at the via's other end, when `Feed = Via` | the stackup's top signal conductor |
| `Drill` | via drill | the technology's via drill |
| `ClearLayers` | conductors whose plane gets a round cut-out under the pad, comma-separated | empty |
| `ClearD` | cut-out diameter on those layers | `D` + 0.5 mm |
| `Centre` | `Open` or `Port` | `Open` |
| `PortNum` | the EM port number when `Centre = Port` | next free |

**Pins:** pin 1 is the feed — at the via on `ViaFrom` when `Feed = Via`, else at the end of a short feed stub on the
named side. Pin 2 is the pad centre on `PadLayer`. Both are ordinary pins for the schematic and the ratsnest.

**Artwork** (built like `ViaPCell`): the pad, the via drill and its pad on `ViaFrom` when via-fed, the feed stub when
side-fed. No pads on the layers the via passes (the via component's rule). **The cut-outs are not drawn as shapes**:
they are declared, the way a `VIA`'s `Antipad` is, and `GroundPourPlanner` cuts `ClearD` out of each plane in
`ClearLayers` that it pours — so a schematic-generated layout gets them for free.

### R-dfr26-7-2 — Cut-outs on a board whose planes are imported artwork

On an imported board the planes are drawn polygons, not pours, and nothing cuts them. A context-menu command on a
placed `TESTPAD`, **Cut Clearances into Planes**, cuts a `ClearD` circle out of every drawn shape on each layer in
`ClearLayers` at the pad, in one undo entry, using the existing Cut Out boolean. It reports what it cut per layer.
Changing `ClearD` afterwards and running it again re-cuts with the new size (the old hole is a subset; say in RESOLVED
how a smaller re-cut is handled — probably "undo first", stated in the message).

### R-dfr26-7-3 — A component-declared EM port

Today only port labels in the TOP-LEVEL layout become EM ports (`Em3dGenerator`, `anyPortLabel` on `source.View.Shapes`);
a generated cell's pin labels never do, which is right for every MLIN end. Add the opt-in:

- With `Centre = Port`, the generated cell carries one extra label at the pad centre, marked as an **EM port** (a new
  flag on the label, written and read by `LayoutPersistence`; absent means no), kind internal-to-ground, number
  `PortNum`.
- The EM flatten step collects EM-port labels from instances, transformed to top-level coordinates, alongside the
  top-level port labels. In 3D each becomes DFR26-1's pad-to-plane port; in the planar solver the existing internal
  port to ground.
- A number used twice (a top-level label and a component, or two components) is a refusal naming both.
- `Centre = Open` adds nothing; the pad is just metal.

This is the mechanism, not a test-point special case: write it so a connector footprint could set the same flag later.

### R-dfr26-7-4 — The circuit model

`TestPadModel` (two pins plus ground): series via inductance from pin 1 to the pad node, the pad's shunt capacitance at
the pad node, pin 2 at the pad node. Closed forms:

- Pad capacitance to the nearest plane NOT in `ClearLayers` that lies on the pad's own side of the board: the
  designer's formula, with `h` the summed dielectric thickness to that plane and the layered dielectrics combined as
  series capacitors.
- Via inductance over its length; the via barrel's capacitance to each plane it passes through a clearance (coaxial
  form with the clearance as the outer radius) added at its point along the via.
- A side-fed pad has no via: the series element is zero length.

Name the sources of every closed form in the file's header, as the microstrip models do, and post one note per
instance that the values are a closed-form estimate and the EM setup is the reference.

### R-dfr26-7-5 — The impedance tool reports a pad hanging off a line

When the impedance tool's selection reaches a round pad joined to the line (directly or through a via), report it as a
shunt element: its capacitance to its reference plane and, through a via, the via's inductance and the frequency they
resonate at. **One implementation** of the formulas, shared with `TestPadModel` — the tool calls it, it does not copy
it.

## 2. Tests

1. Artwork: a via-fed `TESTPAD` on a ten-layer stackup has the pad on `PadLayer`, the drill on the via span, nothing on
   the layers between; side-fed has a stub on the named side.
2. `GroundPourPlanner` cuts `ClearD` out of each plane in `ClearLayers` and out of no other.
3. Cut Clearances into Planes cuts a circle from a drawn plane on each listed layer, one undo entry.
4. `Centre = Port`: the EM flatten step sees port `PortNum` at the pad centre on `PadLayer`; `Open` sees none; a
   duplicate number is refused.
5. Circuit model: pad C for a 1.5 mm pad over 71 µm of εᵣ 4.4 agrees with the designer's formula to 1e-12 (it is the
   same formula); clearing the next plane moves `h` to the next one; the open-ended stub resonates where
   `1/(2π√(LC))` puts it.
6. The impedance tool on a line with a pad off it reports the same C and L as the model.

Targeted classes only. **The EM check** is a local measurement, not a committed test: build a cell with a 50 Ω line on
Top Copper, a via-fed `TESTPAD` to a bottom pad, ports at both line ends, on the DFR26-3 ten-layer technology; run 3D
with `ClearLayers` empty, `Inner 8`, and `Inner 8, Inner 7` at a coarse mesh, and record S21 at 2.44 GHz against the
circuit model's for each in RESOLVED. Keep each run short and under the EM watchdog.

## 3. Text

`docs/user/src/reference/components.md` (TESTPAD: parameters, pins, the cut-outs, `Centre = Port`),
`em-3d.md` (a component can carry an EM port), `layout-editor.md` (Cut Clearances into Planes). Regenerate those
pages only.

## 4. Out of scope

- Other component kinds carrying EM ports (connector footprints): the flag is built so they can, but none is added
  here.
- Non-round pads.
- A shipped example workspace (the owner may ask for one afterwards).
