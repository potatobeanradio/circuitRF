# Brief DFR28-4 — 3D setups lose the Return plane box: every lumped port finds its return in the artwork

**Series:** designer feedback round 28 (2026-10-26). Follows DFR28-3. · **Tag:** `R-dfr28-4-<n>`
**Area:** `src/Design/Layout/Em3d/Em3dGenerator.cs` (`BuildPorts`: the line-end port's return, ~l. 405–480, and the
pad-to-plane walk, ~l. 560–700), `src/Design/Layout/Em/PlanarExtractor.cs` (`InferredReturnPlane`, shared with the planar
kernel — leave its behaviour alone), `src/Ui/Layout/Em/EmSetupEditorViewModel.Palace.cs` (`ShowReturnPlane`,
`ShowConductorsGroup`, the 3D port table), `src/Ui/Views/Layout/EmSetupEditorView.axaml`, `src/Design/Layout/Em/EmSetupModel.cs`
(`EmPort3D`) and `EmSetupPersistence.cs` (`CemPort3D`), `docs/user/src/reference/em-setup.md` and `em-3d.md`.
Findings go in `src/Design/RESOLVED.md`.
**Tests:** `tests/Ui.Tests/Em3d/Em3dGeneratorTests.cs` (its `GroundStackupLayerName` fixture at ~l. 443),
`tests/Ui.Tests/Em/ConformalBoundaryCellsUiTests.cs` (the editor's visibility tests), and a new file for the walk.
`tests/Ui.Tests/Em/ReturnPlaneOverrideTests.cs`, `FlippedStackReturnPlaneTests.cs` and `GroundViaThroughPlaneTests.cs` are
the PLANAR kernel's and must not change.

---

## 0. Why

The owner does not want a Return plane setting on any 3D setup: the return of a port is something circuitRF should work
out from the layout, out of sight. After DFR28-3's follow-up the box is already hidden for a `.c3d`'s setups (drawn ports
name their own two conductors) and for electrostatic setups (no ports). It remains on a layout-driven (`.cem`) 3D setup
that is driven, eigenmode or magnetostatic, as the Conductors group's only 3D content.

The box is an **override**. Blank — every shipped example, every new setup — the generator infers the return. The
inference is good for one kind of port and weak for the other:

- **A pad-to-plane port (a TESTPAD with `Centre = Port`, or any pad port) already reads the artwork.** It walks up and down
  from the pad, layer by layer, and takes the first ground-reference layer with metal across the sheet's footprint; metal
  on a non-reference layer in the way is a refusal that names it. The setting is consulted only when it is filled in.
- **A port label at a line's end does not.** `PlanarExtractor.InferredReturnPlane` picks the nearest layer the
  TECHNOLOGY flags as a ground reference, below the line and else above, from the stackup alone. Only then does the
  generator look for a piece on that layer at the line's end (`PieceContaining`, with a tolerance of a tenth of the line's
  width for a plane that stops short). So it never considers a nearer plane that is not flagged, it can choose a flagged
  plane with no metal under the port and then refuse, and a technology that flags no conductor is refused outright with a
  sentence telling the user to name a return plane — the one case that sends anyone to the box.

No shipped example names a return plane in a 3D setup; one generator test does.

## 1. What to do

### R-dfr28-4-1 — A line-end port's return is found by walking the artwork

Replace the line-end port's choice of return LAYER with the walk the pad-to-plane port uses, over the sheet's own
footprint (the line's end edge: `edgeAt`, from `lo` to `hi`):

- walk down and up from the line's layer; on each side the candidate is the first layer with metal across the
  footprint, where "across" keeps today's tolerance (a plane ending within a tenth of the line's width short of the end
  still counts, and the sheet moves in, with today's note);
- **prefer a ground-reference layer**: a flagged layer with metal under the port wins over a nearer unflagged one,
  unless the unflagged metal lies BETWEEN them in the walk, which blocks it as the pad walk already blocks;
- of the two sides, take the nearer, as the pad walk does; the PEC floor (when the box has one) counts as the plane it
  stands for, as today;
- the run's note names the plane and the side, in the form the pad-to-plane note already uses ("a vertical lumped sheet
  … from 'Top Copper' down to 'Inner 1' …"), so what was chosen is never hidden.

Share the walk between the two port kinds rather than writing a second one. Keep `InferredReturnPlane` as it is: the
planar kernel reads it.

**The cropped board edge is the case to get right.** A port at a line that ends on a cropped board's edge (TP149's ports 1
and 6) has planes whose own edge lies exactly on the line's end. A coverage test that treats a polygon's boundary as
outside will find no metal there and refuse a port that works today. Measure it on TP149 (the scratch copy of the round-26
workspace, or a fixture with planes cut at the line's end) before and after; every TP149 port must take the plane it takes
now.

### R-dfr28-4-2 — A technology that flags no ground reference

Today it is refused. With the walk, the nearest metal under the port is a return whether flagged or not. **Owner
decision D1:** take the nearest metal (recommended: it is what the layout shows, and the note says which), or prefer metal
on a net named like ground (GND, AGND, VSS…) — which is a naming rule and is not recommended. With no metal under the
port on either side and no PEC floor, refuse, naming the layers looked at on each side and what each had at the port.

### R-dfr28-4-3 — A setup that already names a plane

A `.cem` may carry `GroundStackupLayerName` from the time it was editable. **Owner decision D2:** keep honouring it, and
say so in the run's note ("as this setup names") — recommended, because a result must not move silently — or ignore it
for 3D. Either way it is never written by a 3D setup again and never shown. `explain` reports which applied.

### R-dfr28-4-4 — The box and the group go for every 3D setup

`ShowReturnPlane` becomes `IsPlanarAnalysis && !Is3DSetup`; `ShowConductorsGroup` then hides the group for every 3D setup,
since the signal conductor and the analysis levels are already planar-only. Update the visibility test, the EM Setup page
("What a 3D run does read stays: …") and the 3D page, and regenerate those two pages.

### R-dfr28-4-5 — A per-port override: the port table's Return column

For the port whose return is not the walk's choice — a stripline whose port must reference the plane above, a port on a
board with several reference planes — the override is **per port**, because one setup-wide box cannot give different
ports different returns. It lives in the setup's existing per-port 3D settings (`Ports3D`: `EmPort3D` / `CemPort3D`,
beside `Kind`, `Width`, `Height` and `OffsetUm`) and in the 3D port table the Solver group already shows, not in the
layout: the table is where a 3D port's other settings are, it is shown only for 3D setups, and the layout format does not
change.

- **In the file** it is one more key on a port's entry in the `.cem`'s port section, `Ports3D`:

  ```json
  "Ports3D": [
    { "Port": 3, "Return": "Inner 1" },
    { "Port": 5, "Kind": "Wave" }
  ]
  ```

  A port with no entry, or an entry with no `Return`, is Automatic.
- **The field:** `Return` — a conductor layer's stackup name. **Omitted means Automatic** (R-1's walk), never written at
  its default, and part of `EmPort3D.IsDefault`, so every existing `.cem` keeps its bytes.
- **The column:** a combo per lumped port, *Automatic* first, then the technology's conductor layers, a ground-reference
  layer marked as the old Return plane list marks it. A wave port's cell is empty: its two conductors come from the
  air-box face.
- **What it does:** the walk goes to THAT layer, on whichever side it is. It must have metal under the port's footprint
  (with R-1's tolerance); metal on another layer between is a refusal naming it, as the pad walk already refuses for a
  named plane. A layer with no metal under the port is a refusal naming the layer and the port. The note says the return
  was stated ("as port 3's Return states").
- **Both port kinds:** a line-end port and a pad-to-plane port (TESTPAD `Centre = Port`, or any pad port) honour it the same
  way, since both now go through one walk.
- **Against a setup-level name (D2):** a port's own Return wins over a setup's legacy `GroundStackupLayerName`.
- **Reported:** `explain` lists each port's return and whether it was walked or stated; `check` refuses an unknown layer
  name, naming the port.

### R-dfr28-4-6 — Look at the panel, rendered headlessly

The GUI cannot be started from a shell here, so render the real EM Setup panel with `tools/UiSnapshot`, which hosts
`EmSetupEditorView` on a `.cem` and also prints every visible named check box's state and tooltip:

```
dotnet build tools/UiSnapshot
dotnet run --project tools/UiSnapshot --no-build -- --cem <setup.cem> -o <out.png> [--size 900x1200]
```

Open each PNG and look at it; a passing test does not show a crowded table. Render at least:

- a **driven Palace** setup and a **driven EMerge** setup (R-4): no Conductors group; R-5's Return column in the port
  table, its combos wide enough for a ten-layer stackup's longest layer name (`Top Copper (1 oz)`) without clipping, and
  the table still fitting the panel at 900 DIPs wide;
- a setup with a **wave port** beside lumped ports: the wave port's Return cell empty;
- a **planar** setup: the Conductors group and its Return plane box still there, unchanged;
- an **electrostatic** setup: no port table Return column, no Conductors group.

A ten-layer `.cem` to start from: copy one of the TP149 setups (the round-26 workspace) and set `Solver3D`, `Problem3D`
and `Ports3D` as each case needs. Keep the PNGs in the scratchpad; mention in the report anything that looked wrong and
was fixed.

## 2. Gates

1. **Same answers where nothing changes.** Every 3D generator test and the generator dumps (`Em3dGeneratorDumpTests`,
   `testdata/em3d/generator-dumps/`) reproduce unchanged, except where a dump's port had no metal under it — list each
   such change and why.
2. **A stripline** between a plane below and a plane above, both flagged: the port takes the nearer and the note says
   which. Equal distances: below, as today.
3. **A nearer unflagged plane** below the line and a flagged one under it: the flagged one is taken; the unflagged metal,
   when it lies across the footprint between them, is a refusal naming it (the pad walk's rule).
4. **No ground flags** (D1): the nearest metal is taken, with the note.
5. **No metal under the port** on either side: refused, naming each layer looked at.
6. **The cropped edge:** planes ending exactly on the line's end are found (TP149's port 6 shape).
7. **A named plane** (D2) is honoured with its note, and the editor shows no Return plane for any 3D setup.
8. **The per-port Return** (R-5): a stripline port stated to the plane above takes it where the walk takes the one below;
   a stated layer with no metal under the port, and one with other metal in between, are refused by name; a
   pad-to-plane port honours it; a `.cem` without the field reloads and re-serialises byte-identically; the port
   table shows the column for lumped ports only.
9. **The panel looks right** in R-6's headless renders, each one opened and inspected.

Run only the classes the change reaches (the generator, the new walk tests, the editor visibility tests, the dumps).

## 3. Not in scope

The planar kernel's return plane (its box stays, and `InferredReturnPlane` keeps its rule); a `.c3d`'s drawn ports;
wave ports, whose two conductors come from the air-box face; a return stated on the layout's port label (the setup's
port table is the one place it is set).
