# CXP-2 — the annular port in the 3D editor

Read `brief-cxp-0-overview.md` and `brief-cxp-1-c3d-annular-port.md` first.

## Why

Brief 1 makes the port a key in the file. The editor offers no way to make one, so a connector is still fed through a
rectangle unless the user writes JSON.

## Requirements

**R-cxp2-1 — Make Port ▸ Coaxial.** Right-clicking a flat face that is a RING or a DISC with a hole — the end face of
a connector's dielectric, whose inner edge is the pin and whose outer edge is the bore — offers **Make Port ▸
Coaxial** beside *Lumped* and *Wave* (`C3dEditorViewModel.Simulate.cs`, `MakePortItem`). The face's circles give the
centre and both radii; when the face's solid was built from VARs (the bore is `bore_d`, the pin `pin_d`), the port's
radii are written as those expressions, so a VAR edit moves the port with the geometry. The item is offered only when
the port it would add resolves (brief 1's resolver), with the reason in its tooltip otherwise, as *Lumped* and *Wave*
already are. A face that is a full disc with no hole (the pin's end face) does not offer it.

**R-cxp2-2 — The Port tool.** The Port tool (Shift+A P) gains a **Coaxial** shape: click the centre, drag out the
inner radius, then the outer one, on the current drawing plane, with the snap and the typed-value entry the other
shapes have. Escape cancels a half-drawn ring.

**R-cxp2-3 — The properties panel.** An annular port's row shows its centre, inner and outer radius (each an editable
expression), the inferred or stated conductors, Z0, `Flip` and `Model`, and — read-only, as a computed value — the
ring's TEM impedance from brief 1's `explain` note, labelled just "estimated" per the panel rule for computed readouts.
Converting a port between rectangle and ring is not offered; delete and remake.

**R-cxp2-4 — The overlay.** The ring draws in the port colour in its plane, with four short radial arrows from the
negative conductor to the positive one (the polarity), and the port's label. Selection, hover, Move and copy/paste
treat it as any port; Move moves the centre.

## Gates

1. **Make Port** on the 3D Connector's bore end face, in a copy whose bore runs through the housing's back, makes one
   annular port bound to `pin_d/2` and `bore_d/2`, pin positive; editing `bore_d` moves the ring.
2. **The tool** draws a ring in each of the three planes and the result is identical to the JSON brief 1's gate 1 reads.
3. **Pictures** (UiSnapshot, looked at): the context menu with *Coaxial*, the ring on the connector with its arrows, and
   the properties row. Listed in the completion note.

## Out of scope

Solvers (briefs 3, 4), and any automatic port placement on a connector imported from STEP beyond what Make Port on its
face does.

## Completion note (2026-11-12)

Built as specified, plus the ring's checkerboard (cells radial and round, four filled arrows from − to +) to match a
rectangular lumped port's, asked for while this brief was being built. The draw tool is its own kind, **Coaxial Port**
(Shift+A X, toolbar toggle beside Port, Draw menus beside Port), rather than a shape switch on Port: each Draw tool here is one
gesture. Moving a ring drops its centre's expressions and keeps its radii's. Expand/Contract Edges refuses a ring.

R-cxp2-1 said a pin's end face does not offer Coaxial; it does, when the pin's end lies inside its bore (the ring is then the
face's circle out to the bore), which is the face a user clicks with the housing hidden. The pin's tip out in the air does not.

Gates: `CoaxialPortEditorTests` (6 — Make Port on the 3D Wave Ports Launch's bore face bound to `pin_d/2`, `bore_d/2` and
`axis_z`, the menu offering Coaxial there and on the pin's end face but not its tip; on the 3D Connector Launch's pin end face
inside the bore, and its bore's front face but not its floor; `bore_d` 1.34 → 1.5 mm moving the ring to 0.75 mm; the tool
in each plane; the Inspector fields), and `MetalPrecedenceTests.ACoaxialPort_IsACheckerboardOverExactlyItsRing_…`.

Pictures (UiSnapshot, looked at, on a scratch copy of 3D Wave Ports whose P1 is a ring): the ring face-on from the front and
from behind the connector, the whole launch, and the Properties panel (Centre y/z, Inner/Outer radius as expressions, Ring,
Between, "Ring Z 50.02 Ω, estimated"). The Make Port submenu could not be pictured headless (`src/Ui/RESOLVED.md`); the test
reads it instead.
