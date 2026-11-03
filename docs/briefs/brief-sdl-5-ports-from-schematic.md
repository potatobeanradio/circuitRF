# Brief SDL-5 — Schematic ports become EM ports

**Series:** [Schematic-driven layout](brief-sdl-0-overview-and-groundwork.md) · **Tag:** `R-sdl5-n`
**Area:** `src/Design/Layout/SchematicSync/` (new `PortPlacement.cs`), the planner,
`src/Design/Layout/LayoutPortDirection.cs`, new `src/Design/Layout/Em/EmSetupCreate.cs` (extracted from
`WorkspaceViewModel.NewEmSetup`, `src/Ui/ViewModels/WorkspaceViewModel.cs:7481`),
`src/Design/Layout/Em/EmSetupModel.cs`
**Depends on:** SDL-1 and SDL-4 (ports are linked parts and follow its hand-edit rules). **Gate designs:** P1-P5.

---

## 0. What this adds

A schematic's Port components say where the circuit is driven and in what order (`Num`, `Z`). After an
Update Layout from Schematic, the layout has no EM ports: each has to be drawn with the Port tool, by
hand, at the right line end, with the right number, and redrawn if the line moves. This brief places
them: each schematic Port becomes a port label at the pin it connects to, numbered as the schematic
numbers it. Optionally, a `.cem` setup for the layout is created or brought up to date with them.

## 1. Decisions

- **D1 — A port goes where its net meets exactly one layout pin.** Its label is the port label the Port
  tool places today: text = `Num`, `PortDirection` pointing into the metal (the pin's outward direction
  + 180°, per `LayoutPortDirection`'s convention), on the pin's layer, at the pin point (a line's end
  midpoint) or, for a footprint pad, at SDL-2's outer-edge joint point. Kind is left null so it is
  inferred from the artwork exactly as a hand-placed port's is (`LayoutPortDirection.KindOf`), which
  makes a port at a line's open end an edge port.
- **D2 — A port the placer cannot put is reported, not guessed.** A Port whose net has two or more
  layout pins (it sits on a junction), none, or a pin on a ground net; and a Port between two non-ground
  nets (differential). Each is a report line asking for the port to be placed by hand.
- **D3 — A port label is a linked part.** It carries the schematic id of its Port component and has a
  `GeneratedPlacement` record, so SDL-4's rules apply unchanged: a label sitting on its pin moves with
  that pin when a length change propagates (SDL-4 D2), so the port stays on the line end; a label the
  user pulled off its pin is not reached by propagation and stays where it was put (SDL-4 D6); a
  deleted one is not re-added; Re-snap puts it back on its pin. `Num` changed in the schematic changes the
  label's text (an edit to the label's text in the layout is a hand edit, D-conflict applies).
- **D4 — Numbers are checked where they already are.** A duplicate number is refused by
  `EmComponentPorts` today; the placer adds nothing of its own, and its report quotes that refusal.
- **D5 — Reference impedance lives in the `.cem`, as it does today** (`EmSetup.PortZ0s`). The label
  carries no Z0. §3's setup step writes the schematic's `Z` there.

## 2. `R-sdl5-1` — placing ports

`PortPlacement.Plan(extraction, placedPins)` runs inside the planner after chains and side-on parts are
placed, so a port lands on the pin's final position. Ports join SDL-0's checker: a design's expected
ports are part of its `expect.txt`.

## 3. `R-sdl5-2` — the `.cem` setup

- **`R-sdl5-2a`** Extract the creation half of `WorkspaceViewModel.NewEmSetup` into
  `src/Design/Layout/Em/EmSetupCreate.cs` (`src/Design/CLAUDE.md`: a make-an-artifact operation belongs
  there with a shell wrapper in `src/Ui`). The GUI's New EM Setup calls it; a source scan proves the
  view model kept no second copy.
- **`R-sdl5-2b`** Update Layout from Schematic gains one option, **Create or update EM setup**, default
  OFF (writing a new file is not something a re-run should start doing unasked).
  - **No setup references this layout:** create one through `EmSetupCreate`, named after the cell, with
    planar defaults; `PortZ0s` from each Port's `Z`; frequency plan from the schematic's first S-parameter
    analysis if it has one (start, stop, points), else the setup's defaults.
  - **One or more setups reference it:** for each, add a `PortZ0s` entry for a new port number and
    report a number no longer present. Never delete a port's settings, never change a frequency plan or
    any other setting. A changed `Z` updates `PortZ0s` only if the setup still holds the value this
    command wrote last (stored as a provenance note in the setup); otherwise the setup's value is kept
    and reported.

## 4. Scenarios

| | Design | Expected |
|---|---|---|
| P1 | C2 | Four edge ports, numbered 1-4 at the launch ends, directions into the metal |
| P2 | F1 | Two ports at the line ends, one at a footprint pad's outer edge (a variant with the port on the pad) |
| P3 | C3 with a Port on a tee's junction net | Reported; no label |
| P4 | C2, change `Num` 3→5 in the schematic, re-run | Text becomes 5; `.cem` gains slot 5, reports 3 absent |
| P5 | C1, lengthen the middle MLIN | Port 2's label moves with its line end by the difference |

Plus: `EmSetupCreate` byte identity with the GUI path; a setup the user edited is left alone (R-sdl5-2b);
the created setup for P1 runs a dry run (`EmRunService` with nothing solved) without refusal.

## 5. Docs

`layout-view.md` §9.1: D1-D5. User page: ports from the schematic; the option; which ports are left to
place by hand. `docs/user/src/reference/settings.md` if the option is persisted.

## 6. Gate

As SDL-1, plus `--filter "FullyQualifiedName~Em.EmSetup|FullyQualifiedName~EmComponentPorts"`. UI: the
option rendered with `tools/UiSnapshot`, looked at, PNG listed. Findings in `src/Design/RESOLVED.md`.
