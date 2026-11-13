# Brief ICP-3 — The same cut on a 3D `.cem` and in a `.c3d` setup

**Series:** ICP-0 … ICP-9 · **Tag:** `R-icp3-<n>` · **Depends on:** ICP-1, ICP-2
**Area:** `src/Design/Layout/Em3d/Em3dLayoutSolids.cs` (`Prepare`, around line 291), `src/Design/ThreeD/C3dComponentPorts.cs`,
`C3dProblemAssembly.cs`, `Em3dSetupProblem.cs`, `C3dHierarchy.cs`, `src/Design/Layout/Em3d/EdgePortSheet.cs`,
`EmCoSimNetlist.PortsOf` (3D overload).

## The gap

A 3D `.cem` with component ports is built through an in-memory 3D view (`Em3dSetupProblem.Of`, LCP-5), and a `.c3d`
setup builds them in `C3dComponentPorts` against each layout instance's frame. Both read parts from `EmPartPads.Project`.
`Em3dLayoutSolids.Prepare` takes `EmGeometry.ForSetup` only for the instance carrying a region and `EmGeometry.Flatten`
otherwise, so a cut made by ICP-2 would reach one 3D path and not the other.

## Requirements

**R-icp3-1 — One cut for every 3D path.** The instance paths to leave out, and the cut faces, come from ICP-2's
`EmCellCutout` in the layout's own frame and are carried through the instance frame (`C3dInstanceFrame`) like
`EmPartPads.Project`'s `world` transform. `Em3dLayoutSolids.Prepare` applies the cut on both branches, so a `.c3d` placing
a layout and a 3D `.cem` on that layout leave out the same metal and vias. A 3D view with no cut is byte-identical.

**R-icp3-2 — The port.** Each cut face gets the lumped sheet a drawn edge port gets on 3D (`EdgePortSheet`), from the face
to the port's return, as wide as the face (`EdgePortSheetRule` whole edge). No landing rectangle and no new port kind. The
return is the one an edge port on that layer already resolves to; a layer with none is refused by `<path>.<pin>` with the
edge port's own wording. A 3D view needs drawn ground (LCP-5); that rule is unchanged.

**R-icp3-3 — Same verdicts as Planar.** Every R-icp2-3 refusal reads the same on 3D, from the same function. What 3D adds:
a face whose sheet would cross other metal on the way to the return.

**R-icp3-4 — `.c3d` entries.** A `.c3d` setup's `ComponentPorts` takes the ICP-1 entries against its layout instances,
with the layout instance's name as the first path segment when the view places more than one layout (`Board/X1/Q3`); with
one, the path starts inside it. New 3D View (LCP-6) copies the entries as it copies the rest.

**R-icp3-5 — Names and numbering** are ICP-2's, from the same function; the Touchstone header and `.cemr` agree with a
Planar run of the same `.cem`.

## Measure, by hand, once

EMerge (not Palace), the ICP-2 `KIT_MLIN` fixture cut and closed with an ideal line, time-capped, against the whole line on
EMerge. Record |ΔS| and note the sheet's height (the substrate) against the wavelength. This is ICP-0 § Risks 3: written into
`src/Design/RESOLVED.md` and the docs, never a gate.

## Gates

No solve. One test per claim:
1. The assembled 3D problem of a cut has no solid from the instance (Pin mode) or inside the box (Cell mode), and one
   lumped sheet per connected pin at the cut face, of the face's width.
2. A `.c3d` placing the layout and the 3D `.cem` on it assemble the same solids and sheets for the same entry.
3. An R-icp2-3 refusal reads identically from a Planar and a 3D setup.
4. A 3D view with no cut assembles byte-identically.

## Not here

Pin references (ICP-4), `_cosim` (ICP-5, which reads `PortsOf` for both), UI.
