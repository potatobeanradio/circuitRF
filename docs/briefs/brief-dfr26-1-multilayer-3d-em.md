# Brief DFR26-1 — A ten-layer board in 3D EM: port levels, a pad-to-plane port, ground references after import

**Series:** designer feedback round 26 (2026-10-24) — DFR26-1 multilayer 3D EM, DFR26-2 cropping a board, DFR26-3
technology files, DFR26-4 layout ratsnest, DFR26-5 schematic editing and the MTEE, DFR26-6 plots and tuning,
DFR26-7 the test-point component. · **Tag:** `R-dfr26-1-<n>`
**Do this one first.** The designer is sizing ground cut-outs under a production test point before a board respin,
and every item below stands between them and that simulation.
**Area:** `src/Design/Layout/Em/EmPortExtraction.cs` (the level rules ~376-420), `src/Design/Layout/Em3d/Em3dGenerator.cs`
(`BuildPorts`, the internal-port refusal ~376), `src/Design/Layout/Em3d/Em3dLayoutSolids.cs` (ground bands, floor),
`src/Design/Layout/Interchange/GerberImport.cs` (the imported copy of the technology), `src/Ui/Viewer3D/Viewer3DViewModel.cs`
(~489, "Nothing to show"), `src/Design/Layout/Em/PlanarExtractor.cs` (~937). Findings go in `src/Design/RESOLVED.md`.
**Tests:** new `tests/Ui.Tests/Em/MultilayerPortRound26Tests.cs`; the existing port-extraction and 3D-generator classes
named in §3.

---

## 0. The gap

The designer imported a ten-layer board from Gerber, cropped it down to a 12 mm RF path, and tried to run it in 3D.
The path is: an RF trace on Top Copper, two series capacitors, and between them a through via (layer 1 to 10) down to
a 1.5 mm test-point pad on Bottom Copper. Inner 8 (L9) is a solid ground 71 µm above that pad. Cutting the trace to
the test point on the real board gave back 6 dB, so the pad's ~1.7 pF to L9 plus the via's ~1.5 nH is the cause. They
need an EM answer for how large a cut-out in L9 and L8 under the pad brings the pad down by ~1 pF.

They hit five walls, in this order:

1. **An edge port on Top Copper was refused** with "Port 2 … sits on metal on 8 of this EM setup's 9 conductor levels
   ('Bottom Copper', 'Inner 8', … 'Inner 2')". The port's committed layer is Top Copper (`PortLayer` = layer 1 in the
   `.clay`). Top Copper is *not* in that list — the inner ground pours are. **Cause, read from the code:** the rule
   that trusts a port's own layer (`EmPortExtraction.cs`, the block after `NearestPolygon`) only fires when that level
   is among the levels that CONTAIN the point (`containing.Contains(own)`). An edge port sits on its trace's boundary,
   so no Top Copper polygon contains it; the edge rule below it only fires when NO level contains the point
   (`containing.Count == 0`). With pours underneath, neither fires and the multi-level refusal runs. **Every edge port
   over a plane on a multilayer board is refused** — the 2026-09-26 two-layer fix covered only the "inside the pad"
   case.
2. **A port in the middle of a round pad** (the MCX connector's centre pin; the test point "in use") is not possible in
   3D: placing it as an internal port gets "A 3D setup builds lumped EDGE ports in this version".
3. **Meshing failed:** "The signal conductor 'Bottom Copper' sits at or below the ground plane …". The imported
   technology's stackup has `Bottom = Ground` while Bottom Copper carries signal artwork (the test pad itself). It
   came from the technology the import was mapped onto, and the import copied it unchanged.
4. **Only Inner 1 was a ground reference.** Inner 8 — the plane the test pad couples to — and the other planes were
   ordinary signal levels, which is also why wall 1 listed eight of them.
5. **Show 3D showed nothing** ("Nothing to show: Port 2 …"), so there was no picture to work out what was wrong from.

## 1. Reproduction

The owner has the designer's Gerber set and four workspaces (the board at successive stages of cropping: the
uncropped import, a cropped one, a further simplified one, and the last one with a `.cem` and a `.c3d`). Ask for them;
**do not commit them or name them anywhere** — tests use synthetic fixtures (§3).

With the last workspace: open the layout, open its EM setup, press Show 3D → "Nothing to show"; switch the setup to
the planar solver → the nine-level refusal; mesh → the Bottom Copper refusal.

## 2. What to do

### R-dfr26-1-1 — A port's own level wins at an edge, whatever is underneath

In `EmPortExtraction`, when the port commits a level (`OwnLevel` is not null) and the label is ON that level's metal —
inside a polygon of that level, or on its boundary within the same tolerance the edge rule already uses — that is the
port's level, regardless of how many other levels contain the point. Keep the multi-level refusal for a port that
commits no level (a bare label on a marker layer), which is what it exists for. One rule, written once, used by both
the planar and the 3D path (`Em3dGenerator` calls the same `Extract`).

### R-dfr26-1-2 — A pad-to-plane port in 3D

The owner's decision: **a vertical lumped port from the pad to the nearest reference plane.** In a 3D setup, an
internal-to-ground port (`PlanarPortKind` internal, not delta-gap, not conductor-referenced) becomes:

- **Terminal 1:** the pad metal at the label point, on the port's own level.
- **Return:** the nearest ground-reference conductor, on either side, that has metal directly under (or over) the label
  point. Planes with an opening there (a cut-out under the pad) are passed through, not landed on — that is the case
  this exists for. The setup's named return plane, if it names one, wins, as it does for edge ports.
- **The sheet:** vertical, from the pad's face to the return plane's face, centred on the label point. Width: the
  edge-port rule's width for that pad (state it in RESOLVED). **When a via barrel occupies the label point** (the test
  point's through via), place the sheet tangent to the barrel on the side away from the feed trace, and add a note
  saying where it went and why. A sheet that would cross other metal on its way is a refusal naming the level crossed.
- No return plane with metal under the point → refusal naming the planes passed and saying none has metal there.

The internal **delta-gap** port stays refused in 3D; update that refusal so it no longer says internal ports in
general are unsupported.

### R-dfr26-1-3 — Gerber import marks plane layers as ground references, and says so

Owner decision: **guess, and tell the user what was marked.** On the IMPORTED copy of the technology (the cell's own
`.ctech`, never the shipped or user-library source):

- A conductor whose copper covers at least half the board outline's area (the copper bounding box when there is no
  outline) is marked `IsGroundReference`. Marks the source technology already carries are kept.
- One message lists every conductor marked, with its coverage, and says how to undo it ("Technology editor ▸
  Stackup"). A power plane is an AC return at RF, so a power plane marked this way is right for EM; the message does
  not need to argue it.
- A drawn ground plane is still meshed as metal with its holes in 3D (`Em3dLayoutSolids`, "A DRAWN plane is metal with
  holes in it") — confirm that with a test, because the cut-outs are the whole point of the designer's study.

### R-dfr26-1-4 — The stackup's outer boundary follows the artwork

On the same imported copy: when the bottom conductor carries artwork and is not a ground reference (after
R-dfr26-1-3), set `Stackup.Bottom = Open`; the same for the top. One message names what changed. Then find the
"Technology … has 1 issue (Stackup 1)" message the designer's import raised, say in RESOLVED what it was, and make
sure an import of a ten-layer board onto the DFR26-3 ten-layer technology raises none.

### R-dfr26-1-5 — Show 3D draws the board when a port is refused

`Em3dGenerator` already has a `portsOptional` path that keeps the geometry and holds the port refusal aside. Find why
the viewer's Show 3D did not take it (the refusal may come from a step before that flag is read), make the viewer
draw every solid with the bad port missing, and show the port refusal as the viewer's banner rather than replacing
the scene. Solving still refuses.

### R-dfr26-1-6 — Make "New 3D View from Layout" findable from the layout

Today it is only on the Project tree's cell context menu. Add the same command to the layout editor's own menu (where
the EM setup commands are) and to the EM setup editor next to Show 3D. One command, three entry points.

## 3. Tests

New `MultilayerPortRound26Tests`, synthetic geometry only, one test per claim:

1. A Top Copper edge port at a trace end, with solid pours on two inner levels under the point, resolves to Top Copper
   (planar extraction and 3D generation both).
2. A label with NO committed level over the same three levels is still refused with the multi-level message.
3. A pad-to-plane port on a bottom pad, with an opening in the nearer plane under it, builds a sheet ending on the
   farther plane; with no opening it ends on the nearer one.
4. The same port with a via barrel at the label point builds the sheet tangent to the barrel and posts the note.
5. A Gerber import of a synthetic four-layer set (two full planes, two signal layers) marks the two planes, posts the
   message listing them, and sets `Bottom = Open` when the bottom layer is signal.
6. The 3D generator with a refused port and `portsOptional` returns the solids and the refusal.

Also run the existing `EmPortExtraction*`, `Em3dGenerator*` and `GerberImport*` classes. Targeted classes only — not
the full suite, not all of `Ui.Tests`.

**On the designer's workspace (local, not committed):** generate the 3D problem and the mesh for the cropped board
with P1 at the trace edge and P2 as a pad-to-plane port at the connector pad. Report the mesh size and the time. Run
a solve only if the setup's own estimate is a few minutes, under the EM memory/disk watchdog; otherwise report that
it meshes and stop.

## 4. Text

`docs/user/src/reference/em-3d.md` (ports: the pad-to-plane port and when it is used),
`docs/user/src/app-notes/an02-gerber-import.md` (what the import marks as ground and the boundary it sets). Edit the
sources and regenerate those pages only (`--page`, no figures). No change history in the pages.

## 5. Out of scope

- A delta-gap internal port in 3D.
- A coaxial or wave port at a pad.
- The planar solver on nine levels. Record, in RESOLVED, what it says on the cropped board after this brief; do not
  change it here.
