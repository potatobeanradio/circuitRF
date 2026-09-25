# Case B through circuitRF's planar MoM — refused

**The planar MoM cannot state case B, and says so three ways.** Its ground reference is the laterally
infinite boundary its Green's function terminates on, and it needs one beneath every conductor it
solves; case B has signal on both sides of its only plane. The brief expected this case to be solvable
by the L9 multi-level machinery; F0 refutes that (`docs/design/em-3d-f0-findings.md` Q1).

The three setups, each committed here and each run with `circuitrf em <file.cem>` from the repository's
own CLI (built from source on 2026-09-24):

1. `ws/via/em/via.cem` — the case as drawn: the middle plane is the ground reference and the return.
2. `ws/via/em/via-both-levels.cem` — the same, analysing the top and bottom copper levels together.
3. `ws-meshed-plane/via/em/via.cem` — the plane as ordinary meshed copper (not a ground reference),
   all three levels analysed, open top and bottom.

Output verbatim (paths shortened to the workspace; every run exits 1):

```
=== circuitrf em ws/via/em/via.cem
[circuitRF] workspace: ws/.cws
[circuitRF] layout: ws/via/layout/via.clay
[circuitRF] technology: ws/tech/f0-case-b.ctech
warning: 1 signal conductor layer(s) carry artwork but are NOT in this EM setup's analysis levels ('Top Copper'): 'Bottom Copper'. Their shapes are not meshed and contribute nothing to the answer. Add them to the setup's level list if they are part of the structure.
warning: 1 via(s) join 'Top Copper' to 'Bottom Copper' without touching 'Ground Plane'. These are signal vias, not stitches: they are NOT modelled, so the structure continues where these s-parameters stop. Solve the other side as its own run and join them there, or analyse a region these vias do not leave.
note: Analysis is set to "Full-wave planar" explicitly, and Automatic never overrides that.
note: Every port returns through 'Ground Plane' at 270 µm, because THIS EM SETUP names it as the return plane — R-em-4 would otherwise have chosen 'Ground Plane' at 270 µm. The signal level sits at 470 µm. That plane is the negative terminal of every port in this run and is not selectable per port; it is modelled as laterally infinite. Clear this setup's return plane to go back to the automatic choice.
note: The return plane's own metal is in this solve: 'Ground Plane' is a laterally infinite conductor of σ = 5.8E+07 S/m and 35 µm thickness, entered as a surface impedance on the boundary the Green's function terminates on. It is still not meshed and adds no unknowns. On an ordinary microstrip the plane is of order a fifth to a quarter of the total conductor loss, so a run with it and a run without it differ by a real amount in α and in the published |S₂₁|.
note: 4 overlapping conductor shape(s) were merged into 2 before meshing — copper that overlaps on one level is one conductor (a placed part's pad over a drawn or imported pad is the usual case), and a port's feed is measured on the merged outline.
note: 2 label/bitmap shape(s) ignored — annotation is not artwork.
note: 1 shape(s) are on a ground-designated conductor layer and none of them is meshed. 1 of them are on 'Ground Plane', THIS run's return plane, and their outline is read and carried so the run can report how large the real plane is in wavelengths — see the ground-plane note beside the results. It is still NOT MESHED: the plane in the analysis is the laterally infinite boundary the Green's function terminates on, carrying that conductor's own metal, and reading the outline changed no matrix entry and no published number.
note: 0 via(s) on this entry stitch to 'Ground Plane'; 1 pass through a void in it and are ignored. The entry spans 'Bottom Copper' on the far side of the plane, so each via was classified from the plane's own artwork — copper under it, or a clearance — not from the technology, which needs no change.
note: Port 1 ('1') at (-5000, 0 µm) was taken to be on the conductor's low-x (left) end (the port's own direction), driving current in the +x direction, at 50 Ω.
Refused: Port 2 ('2') at (5000, 0 µm) is not on any conductor the EM setup's signal layer. Move it onto the metal, or check that the artwork it names is on a layer bound to a signal conductor in the technology's stackup.
exit code 1
=== circuitrf em ws/via/em/via-both-levels.cem
[circuitRF] workspace: ws/.cws
[circuitRF] layout: ws/via/layout/via.clay
[circuitRF] technology: ws/tech/f0-case-b.ctech
note: Analysis is set to "Full-wave planar" explicitly, and that analysis refused the geometry.
Refused: This EM setup names 'Ground Plane' as its return plane, but its top surface is at 270 µm, which is NOT below the lowest analysis level 'Bottom Copper' at 0 µm. A return plane must lie BENEATH the conductor it feeds, or there is no dielectric slab between them to solve on. Name a conductor below 0 µm, or restrict this setup's analysis levels to conductors above 'Ground Plane'.
exit code 1
=== circuitrf em ws-meshed-plane/via/em/via.cem
[circuitRF] workspace: ws-meshed-plane/.cws
warning: Stackup has no conductor marked as a ground reference (Stackup tab) — microstrip components cannot resolve a ground plane.
[circuitRF] layout: ws-meshed-plane/via/layout/via.clay
[circuitRF] technology: ws-meshed-plane/tech/f0-case-b.ctech
note: Analysis is set to "Full-wave planar" explicitly, and that analysis refused the geometry.
Refused: Technology 'F0 case B (via transition)' has no ground plane below the lowest analysis level 'Bottom Copper' — no conductor is marked as a ground reference and Stackup.Bottom is Open. Two separate things are missing, and only the first is about the Green's function:
(1) The SPECTRUM. An open bottom half-space DENSER than the one above puts a second branch point inside the half-plane DCIM's sampling path runs into, and DCIM fits a sum of exponentials, which cannot carry a cut — measured at 59× the free-space kernel on G_q and 2.3e+4× on G_A. The dielectric under this level reads εᵣ = 1; that is refused whenever it exceeds the medium above. An equal-or-lighter half-space is fittable and is NOT what blocks this.
(2) The DE-EMBEDDING, which is what actually blocks it here. The published s-parameters are referenced to the line's own Z_c = γ/(jωC_pul); C_pul is differenced from an electrostatic IMAGE SERIES over a grounded slab, and the calibration standard's end run is measured in substrate heights. Neither quantity exists without a ground plane. Mark the return-path conductor as a ground reference in the technology editor, or set Stackup.Bottom = Ground.
exit code 1
```
