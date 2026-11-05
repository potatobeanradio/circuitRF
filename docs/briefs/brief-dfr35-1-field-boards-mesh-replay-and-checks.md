# Brief DFR35-1 — results on the current field boards, a mesh replay, and a geometry check before meshing

**Series:** designer feedback round 35. · **Tag:** `R-dfr35-1-<n>`
**Area:** `src/Design/Layout/Em3d/`, `src/Design/Em3d/` (the `.geo` writer and Gmsh failure reporting), `src/Cli/` (`em`),
`src/Ui/ThreeD/` (the air box a 3D view draws with no setup), a new program under `tools/`.
Findings go in `src/Design/RESOLVED.md`. Field-test boards stay outside the repo: no board, result or board-specific name
is committed, including in tests and notes.

## 0. Why

Imported boards keep failing at the mesh, the one step a dry run does not reach. The last fixes each closed one cause: "overlapping
facets" has now had two different causes (a 0.1 µm jog at a board edge, then a zero-width spike in a pour), and neither was
reported before Gmsh ran. Every setup from earlier field rounds can be meshed again in about 15 minutes, and doing it once found
a failure in 5 s on a setup that passes its dry run. So this brief does three things: gets S-parameters on the three boards of
the current round, makes the replay routine, and has the dry run report bad geometry by name.

Already fixed in round 35 and not to be redone: the spike (`Em3dOutlineCleanup`), the planar return refusal in 3D, drill files
into a technology, a slot hiding H, the padding box's E notation, the frequency count in the sweep row.

**Owner decisions.** The antenna air box rule stays as it is: `DefaultPadding`, `ByHeightApplies` and the H rule do not change.
A launch that cannot return is handled by a small change to the port or geometry, not by a new coaxial port type.

## 1. Results on the current boards

Work on copies; the field-test originals are never written. Results go to the owner beside the originals, with a table of what
was changed on each board, so the same edit can be made in the GUI on the board's own machine. Run `--dry-run` first. Stop and
report rather than run on if one board passes **60 minutes** or the run's own estimate exceeds this machine's memory (owner's
rules on long EM runs; use the memory and disk watchdog, targeting the real pid).

**R-dfr35-1-1 — Board A, a 4-layer cropped RF trace, two ports.** It failed at the mesh before round 35 and meshes now. Solve it
and return the `.s2p`. It reports one ill-shaped tet; say whether Palace accepted the mesh.

**R-dfr35-1-2 — Board B, the same 4-layer board's other RF path, two ports.** Meshes to about 100,000 tetrahedra and an estimate
near 12.4 GB at the Standard preset. A field run reached "model change 1.4e-3 (done below 1.0e-4)" on port 1 after 1.5 hours and
was stopped, so the question is whether it was slow or stuck. Read `palace.log` for the time and iterations per solve, and whether
the low end of the sweep costs more than the top. Then solve at Draft (estimate about 4.7 GB), and report the |ΔS| between Draft
and any Standard points reached. Report the numbers; do not tune the presets here.

**R-dfr35-1-3 — Board C, a 2-layer antenna board with SMA launches, four ports.** Port 1's line ends inside a 5 mm opening on
both copper layers, so no sheet has metal to end on, down or across.
- *The modification:* trim the line so its end sits about 0.25 mm past the opening's edge, over the bottom plane, move the port
  label to the new end, clear the return stated on the label and set Return to Automatic. Checked by dry run: 0 errors, a vertical
  sheet 2,002 µm wide and 1,465 µm tall, returning through the bottom copper. Report the trim's length as the shift of port 1's
  reference plane, and the sheet's series inductance as a known limit of that port.
- *The sweep:* the setup asks for a pattern on the default 0.5–8 GHz sweep, which pads 94.7 mm a face and estimates 147.8 GB. Under
  the antenna rule a sweep round the band with a stated pattern frequency pads 30.6 mm and estimates 20.9 GB on the whole board.
  Ask the owner for the band if it is not on the board's documents.
- *The crop:* if that is still over this machine's memory, set a solve region round the antenna and its feed, and say what the
  crop leaves out.

**R-dfr35-1-4 — The refusal says how far.** When a line's end is in an opening, the refusal states the distance along the line to
where metal starts under it ("the plane starts 2.36 mm along the line"), since that is the edit that fixes it. Same for a coplanar
return whose gap is not straight at the end. Gate: `PortReturnOpeningTests`.

## 2. A mesh-only run and a replay

**R-dfr35-1-5 — `em --mesh-only`.** Builds the problem, meshes it, reports what the mesh gave (elements, warnings, Gmsh's own
errors with the objects named) and stops before any solver starts. Exit 0 on a mesh, 1 on a refusal or mesher failure, 130 on
cancellation. It uses the run's own stage folder and the same script, so it fails exactly where a run would. Gate: a process-level
test in `EmCliVerbTests`' style against the small synthetic boards in `BoardEdgeSliverTests`.

**R-dfr35-1-6 — `tools/MeshReplay`.** Takes a folder as an argument, finds every `.cem` under it, copies each workspace to a
temporary directory, and runs `em --mesh-only` with a per-setup time limit and memory ceiling. It prints one row per setup: meshed
(elements, warnings), refused (first sentence), failed (Gmsh's words), timed out. Given last run's output it prints only what
changed. It holds no data, is not in `circuitrf.slnx`, and is not a test: nothing in the repo depends on the folder existing.
Add it to `BUILDING.md`'s release steps as something run before a release, and run it over the field-test material for this
brief's report. Refusals that are a correct answer to a setup's state (a port outside its solve region) are not failures; the
difference shows as "changed since last run".

## 3. The dry run reports bad geometry

**R-dfr35-1-7 — `Em3dGeometryCheck`.** A check over the outlines a run will mesh, after Robust meshing, reporting what is known to
stop Gmsh: an outline that reverses on itself within a tolerance; a slit or a sliver narrower than the tolerance; two faces of the
same or different objects nearly coincident (the 0.0127° case in `BoardEdgeSliverTests`); an edge far below the local element size
where it meets another object. Each finding names the object, the layer and the layout coordinates. It runs in `em --dry-run`,
`check` and the setup panel, as a warning, since a mesh may still succeed. It must stay cheap on a 3,000-shape board: use a spatial
index, and measure with a scratch harness, not a new timing test.
*Gate:* each class has a small synthetic board that fails in Gmsh with the check off, and the check names it first. Record how many
of the failures found by R-dfr35-1-6 it would have named, and which it misses.

**R-dfr35-1-8 — A mesh failure says where.** `GmshGeoWriter.NameSurfaces` names the objects but not the place, and two faces of one
pour read as "between X and X". Add the failing surfaces' position in layout coordinates, and a Messages action that selects that
point in the layout. Measure the cost of writing surface boxes before choosing where they come from: `faces.txt` is opened once per
group for a reason.

## 4. What else remains

**R-dfr35-1-9 — A dielectric lost in the fragment.** A setup meshes at one crop and fails at a slightly different one:
`Skipping unknown volume 1 in physical volume 1`, then `Unknown model region with tag 1` at the first `Boundary{ Volume{s0[]}; }`
of the faces table, in about 5 s. The dielectric still exists after its `BooleanDifference` and before `BooleanFragments`, so `s0[]`
is stale after the fragment: find why this volume's tag did not survive when the others did. Reproduce it on a synthetic board, fix
it in the generator, and make sure the faces table, which is diagnostic, cannot be what stops a run.

**R-dfr35-1-10 — A 3D view with no setup follows the antenna rule.** With no active setup, `C3dEditorViewModel.ResolveRecords` sizes
the box from `new EmSetup { … }`, whose `RadiationPattern` is false. So a layout whose `.cem` asks for a pattern is drawn with the
non-radiating rule: on a 2-layer antenna board H is the whole 1.52 mm, sides 20 × H capped at the largest extent (18.16 mm), floor
10 × H (15.22 mm), top 100 % of the height (1.594 mm), against 18.16 mm on every face in the `.cem`'s own run. The panel showed a box
no run of that design would solve in. Where exactly one `.cem` names the placed layout, the view's no-setup box takes that setup's
problem type, sweep and pattern; otherwise it says it is the box a new setup would start with. Gate: a 3D view over a layout whose
`.cem` has a pattern gets that `.cem`'s padding on every face, and the rule itself is unchanged (`AirBoxDefaultsDfr34Tests`).
Check by UiSnapshot of the air box rows, with the rendered PNG inspected.

**R-dfr35-1-11 — A `.c3d`'s dry run on a board this size.** `em x.c3d --dry-run` on a 3,200-shape layout did not finish in 170 s,
against 3 s for the `.cem` on the same layout, and `explain` did not finish in 5 minutes. The same elaboration may be what makes a
3D view slow to open. Find where the time goes and report it; fix it here if it is one place.

**R-dfr35-1-12 — The element estimate.** A radiating setup was estimated at 13,720 elements and meshed to 93,360 (6.8×); a warning
then named a memory figure from the mesh. An estimate that low sends a user into a large run. Find what it leaves out and report the
ratio across the replay's setups; change it only if one term explains it.

**R-dfr35-1-13 — The Recycle Bin on Windows.** `SystemTrash` now retries access denied (code 120) and explains it, untested on
Windows. On a Windows machine, remove a 3D view with its document open, with the workspace watchers running, and inside a synced
folder if one is available. Say which of them holds the file. If the watchers do, release them around the move.

## Gates

1. Boards A, B and C: a Touchstone file for each, or a stated reason with the numbers behind it; the table of edits for each board.
2. `em --mesh-only` and `tools/MeshReplay` run over the field-test material, with the before and after tables in the report to the
   owner, not in the repo.
3. Each `Em3dGeometryCheck` class: a synthetic board, failing in Gmsh, named first by the dry run.
4. R-9's synthetic board fails before the fix and meshes after it.
5. Targeted test classes only: `BoardEdgeSliverTests`, `RobustMeshingTests`, `PortReturnOpeningTests`, `AirBoxDefaultsDfr34Tests`,
   `Em3dGeneratorDumpTests`, `PalaceBackendTests`, `EmCliVerbTests`, plus each class this brief adds. No new timing test.
