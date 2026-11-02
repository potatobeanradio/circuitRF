# Brief DFR31-0 — designer feedback round 31: overview and triage

**Series:** designer feedback round 31 (2026-10-29). · **Tag:** `R-dfr31-<brief>-<n>`
**Source:** a field test on a Gerber-imported 10-layer board. A full Palace driven solve ran end to end (6 ports, 101
points, 38 min, Draft). The next step was the round-26 electrostatic test-pad capacitance study, repeated on that
board through the GUI alone, and that is where the gaps below showed. The field-test workspaces, logs, crash report
and Gmsh log are kept outside the repo.

Briefs in this round:

| Brief | What | Size |
|---|---|---|
| DFR31-1 | Electrostatic on a board with no nets: connected bodies, a terminal picker, net names accepted, floating metal | large: the blocker |
| DFR31-2 | Two shipped examples for extracting capacitance (a simple one and a test pad on a multilayer board) plus a user-doc page | medium |
| DFR31-3 | Field plots: show the cut plane in the 3D view, and say why the Solution list is empty | small/medium |
| DFR31-4 | Create SnP Schematic after a successful EM run | medium |
| DFR31-5 | Small fixes: the impedance overlay outlives its panel, huge port labels, the 3D view hanging after a lost GPU context, a Messages log file, plainer progress text | small, independent items |
| DFR31-6 | Gmsh "Invalid boundary mesh (overlapping facets)" on a cropped imported board | investigation |

## Fixed directly this round (not briefed)

**The Archive Workspace dialog archived results that had been unticked.** With every box unticked, the zip was still
486 MB. The footer agreed: "1.3 GB … 151 item(s) ticked", under a tree with every box empty. The tree
builds rows lazily. The Results heading opens expanded, and its sub-headings (Data Displays, Touchstone, Analysis,
Other) stay collapsed. Unticking Results pushed its state into each sub-heading with the "from parent" flag set, and
the flag skipped the code that updates a collapsed heading's unbuilt rows. Every file under those headings stayed
selected, including roughly 900 MB of Palace field files under Other. Fix: `ArchiveTreeNode.ApplyToChildren` now
updates a collapsed group's unbuilt options, recursing through nested unbuilt groups. Test:
`ArchiveTreeAndMenuTests.UntickingAnOpenGroup_ReachesTheRowsOfItsCollapsedSubGroups`.

## Behaviour the documentation should state more plainly (covered by DFR31-2's app note)

1. **Which model an EM run uses.** Running a `.cem` builds its own 3D model from the LAYOUT, every time. With
   Solver3D = Palace that is a full 3D FEM solve, not planar MoM. A `.c3d` 3D view is a separate document. Its
   embedded setup (copied from the `.cem` when the view is created) runs on the 3D view's own content. The view
   instances the layout by reference, so a saved layout edit reaches both, and the 3D view never needs regenerating
   after a layout edit.
2. **The cut plane can be moved after the solve.** A field plot's axis and offset are properties of the plot, not of
   the solve. The solve saves the whole field, and the plot reads a plane out of it.
3. **Static terminals need no potentials set.** Each terminal is driven at 1 V in turn, with the others at 0 V. The
   capacitance matrix and the per-terminal field solutions both come from that.

## Rules for every brief in this round

- Do not quote the designer or the owner, in the repo or in commits. Paraphrase.
- The field-test board is private. Nothing from it (layout, net names, part designators, coordinates) goes into the repo, a test
  fixture or an example. Reproduce each defect on synthetic geometry.
- Findings go in the relevant `RESOLVED.md`, never `CLAUDE.md`.
- One test per claim. Run only the test classes the change can reach, never the full suite.
