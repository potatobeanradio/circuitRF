# PWL-4 — The wall port in the editors: one right-click, one table column

Series overview: `brief-pwl-0-overview.md`. Needs brief 3.

## Why

A port kind nobody can find is a port kind nobody uses. Every 3D port today is made the same way: right-click the face
it belongs on and choose *Make Port ▸ Lumped*, *Wave* or *Coaxial*. Every port is configured the same way too: in the
Solver group's port table, where a row sets Z0, Kind and Return. The wall port joins both places and adds no new one.

## The user's path

**In a 3D view, one step:** right-click the air box's face where the lines end, a line's end face lying on it, or a
sheet drawn on it, and choose ***Make Port ▸ Wall***. Nothing is asked.

**In a layout's 3D setup, two steps:** place the port label as today, then pick **Wall** in its row's Kind.

**To compare kinds:** a port on an air-box face switches between **Wave** and **Wall** with a **Kind** combobox, in
Properties in a 3D view and in the port table in a `.cem`, because both kinds state the same plane, offset and
rectangle. A sheet port that bridges two conductors stays Lumped.

## Requirements

- **R-pwl4-1 — *Make Port ▸ Wall*** (`C3dEditorViewModel.Simulate.cs`, beside `MakeCoaxialPortItem` in
  `C3dEditorViewModel.CoaxialPorts.cs` and the Wave item). It writes one wall port per signal conductor meeting that
  face, each with a rectangle round its line's end, numbered with the next free numbers from left to right along the
  face (the order *Make Port ▸ Wave* gives terminals), in one undo entry. A conductor that already has a port on that face is
  skipped, and the status line says so. The menu offers it only where brief 3's resolver would accept it, and otherwise
  shows it disabled with the resolver's own refusal as its tip: the existing rule that the menu offers only the kind
  that would resolve, and the other's tip says why.
- **R-pwl4-2 — The setup is not edited.** Making a wall port changes no setup: the face is PEC for every setup that
  runs the port, by brief 3's R-pwl3-1. The air box overlay draws that face as PEC whenever a wall port is on it, and
  its tip says why.
- **R-pwl4-3 — A Kind combobox in Properties** (`C3dPropertiesViewModel.Ports.cs`). A 3D view's port has no Kind
  control today: its kind is fixed by the gesture that made it. Add a **Kind** combobox to the port's Properties,
  offering **Wave** and **Wall** for a port on an air-box face (they state the same plane, offset and rectangle, so
  switching keeps the port's number, name, Z0 and Offset). A sheet port bridging two conductors shows Lumped,
  disabled, with the tip saying why it cannot become either. Rules:
  - **The whole face switches together**, in one undo entry, because one face carries wave ports or wall ports, not
    both (brief 3 R-pwl3-5). The combobox's tip says so before the change, and the status line names every port that
    switched.
  - **A wave port with terminals becomes one wall port per terminal** (same numbers, same conductors), and a face of
    wall ports becomes one wave port with a terminal per wall port, so a round trip returns the file it started from.
    A routine test holds the round trip byte for byte.
  - **An item that would not resolve is disabled, with the resolver's refusal as its tip** (a face with a line that
    is not uniform over the end run cannot become Wall; a face whose lines need the wave port's terminals on a solver
    that refuses them says so), the rule the *Make Port* menu already follows.
  - The same switch is on the port's right-click menu in the view and the Object Tree, as **Kind ▸ Wave / Wall**, for a
    user who never opens Properties.

  Properties also shows, read-only, the port's calibration group (*Wall xmin · group 1 of 2: P1, P2*), the gap and the
  end run.
- **R-pwl4-4 — The overlay.** A wall port is drawn as a lumped port is (the two-colour checkerboard, number and flat
  arrow) over exactly its GAP, from the face to where the line will end, with the arrow pointing from the wall to the
  line; and its reference plane as the dashed line the wave port uses, at the face or moved by Offset. Ports of one
  group are joined by a thin bracket on the face, labelled with the group. Anything in front hides it, as for every
  port.
- **R-pwl4-5 — The object tree** lists wall ports under Ports like every port, with the group in the row's tip.
- **R-pwl4-6 — The port table's existing Kind combobox** (`EmSetupEditorView.axaml`,
  `EmSetupEditorViewModel.Port3DKindChoices`, today Lumped and Wave) gains **Wall**. A Wall row enables Z0 and
  Offset, hides Return (it has none, as a wave port has none) and disables Width and Height (wave-only). The Offset
  cell's enablement and tip become "wave or wall", not wave only. The kind tip (`Port3DKindTip`) adds one sentence for
  Wall. Changing one row to Wall changes every row on the same face of the layout, with the same tip and status line
  as R-pwl4-3. A row whose port cannot be a wall port shows the refusal under the row, as a wave row does.
- **R-pwl4-6b — The port table in a 3D view's Analysis Setup.** *Simulate ▸ Setup Analyses…* opens the `.cem`
  panel without its port table today (`EmSetupEditorViewModel.IsPortKindSetup` is false when `IsEmbedded`), because a
  `.c3d` port carries its own kind. Show the table there too, listing **only the ports on an air-box face**, with
  columns Port, Name and **Kind**, a combobox of **As drawn (Wave)** or **As drawn (Wall)**, then **Wave** and **Wall**.
  "As drawn" writes nothing; a choice writes the setup's `Ports3D` row (brief 3). Z0 and Offset stay on the port
  itself and are shown read-only with the tip *set in the port's Properties*, so each value is edited in exactly one
  place. Choosing a kind on one row changes every row on that face, as in R-pwl4-3. A 3D view with no port on an
  air-box face shows the table's header with *No ports on the air box's faces*, greyed, so the panel's layout does
  not move between views.
- **R-pwl4-6c — The view shows the active setup's kind.** The overlay draws each face port as the ACTIVE setup runs
  it (R-pwl4-4's wall drawing or the wave port's own). When the setup overrides the port, the port's Properties Kind
  shows the port's own kind with a note under it, *Runs as Wall in setup 'Wall · Palace'*, and the Object Tree row's
  tip says the same, so the view never shows one kind while the run uses another without saying so.
- **R-pwl4-7 — The setup switch.** **Calibrate wall ports**, a checkbox in the Solver group under the port table,
  always present so nothing moves (greyed with the tip *no wall ports in this setup* when there are none). Off writes
  `"CalibrateWallPorts": false`.
- **R-pwl4-8 — The results.** After a run, the diagnostics group's γ, ε_eff and Z_c per mode are pickable in the Data
  Display like any cube, under names a reader understands (*Wall xmin · group 1 · ε_eff (mode 1)*).

## Gates

- UiSnapshot pictures, looked at and listed in the completion note: the menu on an air-box face with two lines; the
  same face after *Make Port ▸ Wall* (two numbered gaps, the group bracket, the reference planes); Properties of one;
  the port table with a Wall row and the switch; the disabled menu item with its tip on a face whose line stops short;
  the Properties Kind combobox open on a wave port, and the same face after switching it to Wall; a 3D view's Analysis
  Setup with its port table and a face set to Wall, and the view with that setup active (wall overlay, the Properties
  note *Runs as Wall in setup …*); the same view with a setup that runs it as drawn.
- Routine tests for R-pwl4-6b: choosing Wall writes the setup's `Ports3D` rows and leaves the `.c3d`'s ports
  byte-identical; choosing "As drawn" removes them.
- One routine test per requirement that changes the document (R-pwl4-1, R-pwl4-3, R-pwl4-7), asserting the written
  `.c3d`/`.cem` bytes.

## Docs

`docs/user/src/reference/drawing-in-3d.md` §Simulating: *Make Port ▸ Wall*, beside the coaxial port paragraph, linking
to EM Setup ▸ Wall ports.

## Completion

`src/Ui/RESOLVED.md` §PWL-4, with the picture list.
