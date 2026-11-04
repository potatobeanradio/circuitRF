# Brief DFR34-4 — electrostatic setup: ground, the conductor list, vias between nets, and the result

**Series:** designer feedback round 34. · **Tag:** `R-dfr34-4-<n>`

A capacitance extraction on a 10-layer board (four terminals: RF in, RF out, a capacitor pair, a 3.3 V plane) ran,
but each step took more questions than it should have. Round 34 already added a Pick for the Ground box, made the
marker read a terminal's name, and made the ground-terminal refusal point to `C_mutual`. This brief is the rest.

Read first: `EmSetupEditorViewModel.Static.cs`, `EmSetupEditorView.axaml` (terminal rows, Ground row),
`Em3dBodyList` / `Em3dBodies`, `Em3dConductorNames.Resolve`, `Em3dGenerator` (ground set ~1297, `ViaNetRefusal`),
`Em3dLayoutSolids` (a via's net, ~865), `Em3dStaticResult.Build`, and `docs/user/src/app-notes/an03-extracting-capacitance.md`
(§ The Maxwell matrix and the circuit matrix).

## 1. Ground is one name

**The gap.** `Ground3D` is one token, and naming it replaces the default ground (ground-reference layers and the metal
touching them). A plane on a layer the technology does not mark as ground cannot be added to the ground without
losing the others. The only workaround is editing the stackup.

**R-dfr34-4-1 — Ground takes several**, comma-separated like a terminal's conductors, with "+ default ground" as a
first entry meaning the ground-reference layers. Pick adds to the list. The resolver, `C3dProblemAssembly`,
`C3dComponentPorts` and the CLI accept the list.

## 2. The conductor list is hard to read and to choose from

**The gap.** Each entry is "name — every layer it touches, vias included — N objects" on one line in a 220 px column,
so the part that tells two entries apart is clipped. Entries are in document order. Ground bodies are offered as
terminal choices, which only leads to a refusal. Selection commits on every selection change. A Pick in the 3D view
on a 10-layer board usually lands on dielectric ("is not a conductor").

**R-dfr34-4-2** — two-line items (name; then layer range, "Top–Bottom, 11 vias", and size), sorted by net then size;
ground bodies marked "(ground)" and not offered to a terminal; bodies already in a terminal marked with its name;
commit on Enter or click only. **R-dfr34-4-3** — an armed Pick passes through dielectric and air to the first
conductor along the ray.

## 3. A via on a different net from the copper it passes

**The gap.** A static run refused: "the via at (126.6, 117.5) mm is on net 'GND' and passes through copper of net
'N7120220' on every layer". In 2D the user saw N7120220, most likely because a click there selects the pad, not the
drill. A via's stored net wins over its landing copper (`shape.Net ?? ViaLandingNets`).

**R-dfr34-4-4** — the refusal names the drill shape (layer and index) and offers a one-click "Give the via its copper's
net". The layout editor's hover on a drill under a pad names both nets when they differ. *Decision D1 for the owner:*
in a static solve, should a via whose stated net disagrees with **all** of its landing copper simply take the copper's
net with a note? Recommend yes: the via is one piece of metal with that copper, so the stated net cannot be what is
built.

## 4. The result

**R-dfr34-4-5** — a 1-D `C_ground` cube (each terminal's capacitance to ground, the diagonal of `C_mutual`) beside
`C` and `C_mutual`. A new capacitance plot defaults to `C_mutual`. The run's completion note links to AN-03's
Maxwell-matrix section, which explains why `C`'s off-diagonals are negative. `C_mutual` is derived from `C` whenever
the solver's own mutual file was not read.

## Gates

- Ground = "+ default ground, body:…/Inner 5/2" grounds both and solves on a fixture board (R-1).
- UiSnapshot of the conductor drop-down on the round-34 board, inspected: every entry distinguishable (R-2).
- A via with a stated net that disagrees with its copper: the refusal and the one-click fix (R-4).
- `C_ground` equals the row sums of `C` to 1e-12 on the Capacitance Extraction example (R-5).
