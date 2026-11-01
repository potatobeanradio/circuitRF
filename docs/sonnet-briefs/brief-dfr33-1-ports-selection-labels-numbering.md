# Brief DFR33-1 — ports: selecting around them, names in a dense cluster, and a missing port number

**Series:** designer feedback round 33. · **Tag:** `R-dfr33-1-<n>`

Three port problems from one field test: a 4-layer LNA evaluation board, 30.48 mm square, with 13–14 edge ports.
Nine of them sit along one 50 Ω line within about 10 mm, and four of those within 1.5 mm, at the part's pads.

Read first: `LayoutEditorViewModel.BuildPickStack` (ports rank above artwork, and a second press at the same point
cycles to what lies underneath — this already exists and is not to be changed), `LayoutEditorViewModel.Layers.cs`
(per-view Visible / Selectable, `_hiddenLayers` / `_lockedLayers`, stored with the view), and
`src/Render/Renderers/LayoutRenderer.PortNames.cs` (round 32's fixed-size, non-overlapping port names).

## 1. Shapes cannot be selected without the ports on them

**The gap.** A port label lives on the conductor layer it drives (`PortLayer`, here Top Copper). Its pick region is
larger than its text so that it can be grabbed, and ports rank first in the pick stack. That is right for moving a
port. It is wrong when a user wants several shapes for a boolean operation: a marquee picks up every port inside it,
and each shift-click on a short line between two ports lands on a port first. Booleans skip port labels silently, so
nothing breaks, but the selection is hard to build and hard to read. Turning off Top Copper's Selectable box removes the
traces as well. There is no way to set ports aside on their own.

**R-dfr33-1-1 — a Ports row in the Layers panel, with its own Visible and Selectable boxes**, above the layer rows. It
governs every port label in the view, whatever its layer:
- **Selectable off:** no port enters the pick stack, a marquee, Select All or a crossing selection. The Port tool still
  places ports, and a port placed while the row is off is selected as it is placed (the same rule as drawing on a
  locked layer, if one exists — follow it; otherwise this one).
- **Visible off:** port labels, their arrows, names and resolved-width bars are not drawn, and are not selectable.
- Stored per view exactly where `_hiddenLayers` / `_lockedLayers` are stored, so it survives reopening and does not
  travel in the `.clay` or the `.ctech`. `HasLayerOverrides` and the panel's reset count it.
- The status line says "Ports not selectable" while Selectable is off, so a user who forgot does not think the editor
  is broken.

`render` takes no new flag for this: a headless picture already chooses its layers, and ports there stay as they are.

## 2. Port names in a dense cluster still crowd at some zooms

**The gap.** Round 32 made a port's name a fixed screen size and moves a name outward until it clears the others,
with a leader once it has moved more than its own height. That works at close zoom. Zoomed out on this board, four
names 150–700 µm apart are each pushed out along a leader, and nine along one line become a row of leaders and text
longer than the feature they label (`P14-P1 P9 P7 P5 P4 P6 P8 P13`, with `P5·P4` and `P7-·-P6` touching).

**R-dfr33-1-2 — a name that cannot be placed near its port is shortened, then left out.** In placement order:
1. the full name beside the port, as now;
2. moved outward up to **3** of its own heights, with the leader as now;
3. if still not clear: the port **number** alone (`4` for `P4`) in the same place, tried again with step 2;
4. if still not clear: no name. The port's arrow stays, and hovering it shows the name (the layout's existing hover
   tooltip, if it has one; otherwise a tooltip on the canvas for the port under the pointer).

A name left out at one zoom comes back when zooming in makes room. Placement stays deterministic (same layout, same
zoom, same result), and `render` draws through the same pass, so a picture and the canvas agree. *Decision D1 for the
owner:* (a) **recommended** as above; (b) skip step 3 and go straight from a leader to hover.

## 3. A missing port number is not mentioned

**The gap.** Between two snapshots of the workspace, P2 disappeared from the layout. The remaining ports are numbered
1, 3, 4 … 14. `check`, `explain` and the 3D run's notes list each port and say nothing about the gap. A Touchstone file
has no port numbers, only positions, so a run over 13 ports writes 13 columns, and whatever circuitRF does with port 3
in that case decides whether the file's second port is P3 or a missing P2. A schematic's SnP block then connects by
position. Round 27 (`brief-dfr27-2`) made a gapped schematic result refuse a Touchstone export for exactly this
reason. Layout EM ports have no such guard.

**R-dfr33-1-3 — establish what happens first.** On a two-line synthetic layout with ports 1, 3 and 4 (no 2), run a
planar setup and a Palace setup (Draft, one frequency point), and record for each: the port count in the `.sNp`, which
label each column is, what `EmSnpSchematic` (Create SnP Schematic) names each pin, and what the `.npy` metadata says.
Put the table in `src/Design/RESOLVED.md` before changing anything.

**R-dfr33-1-4 — one line wherever ports are listed** (`check`, `explain`, the run's notes, the EM Setup editor's port
table): *"Port numbers skip 2: the 13 ports are 1, 3–14, and the Touchstone file's ports are in that order (its
port 2 is P3)."* — worded to match what R-3 found. *Decision D2 for the owner:* (a) **recommended:** a warning, and the
run proceeds, because a designer removing a port mid-design is ordinary and the file stays self-consistent with the
note; (b) refuse the run until the numbers are contiguous, matching round 27's export refusal.

**R-dfr33-1-5 — a port outside the solve region is named.** In the same field test, the drawn solve region ended at
x = 30,405.8 µm and the board's right-hand port sat at x = 30,454.6 µm. Check what a planar run does with a port label
outside its solve region. If it is dropped, the run's notes name it and say why ("P2 lies outside the solve region and
is not in this run"). If this is already said, record where and do nothing.

## Gates

- One test per claim: ports out of a marquee and Select All with Selectable off, and still placed by the Port tool;
  ports not drawn with Visible off; the setting restored on reopen; the dense-cluster name falls back to the number and
  then to nothing, and comes back on zooming in (counter assertions on the placement list, no timing); the gap line
  from `check` and from a run's notes; a port outside the solve region named.
- UiSnapshot PNGs: the Layers panel with the Ports row; a dense port cluster at three zooms before and after. Look at
  each and list them in the completion note.
- `src/Render` changes: run the layout render tests and `RenderCliVerbTests`.
- Targeted test classes only. Do not run the full suite or all of `Ui.Tests`.
- Docs: the port and Layers-panel sections of `docs/user/src/reference/layout-editor.md`, and the port section of
  `docs/user/src/reference/em-3d.md`. Do not run DocGen; the owner regenerates at the end of the series.
- Findings in `src/Ui/RESOLVED.md` (and `src/Design/RESOLVED.md` for R-3/R-4), never in a `CLAUDE.md`.
