# Brief 114 — the terminal wave port: document, problem, editor

**Series:** [3D EM, eighth series](brief-em3d-112-overview.md) · **Tag:** `R-em3d114-n`
**Area:** `src/Design/ThreeD/C3dDocument.cs` (`C3dPort`, a new `C3dTerminal`), `C3dPersistence`, `src/Design/ThreeD/C3dPorts.cs`
(inference), `C3dProblemAssembly`, `src/Engine/Em3d/Em3dProblem.cs` (`Em3dPort.FaceGroup`, validation), `src/Design/Em3d/Em3dPortMap.cs`,
`src/Ui/ThreeD/C3dEditorViewModel.Simulate.cs` (*Make Port ▸ Wave*, the port rows, the overlay), `src/Render/Scene3D/Scene3DBuilder.cs`
(drawing a terminal's voltage path), `src/Cli/` (`check`, `explain`, `DocumentSchema`), `docs/user/src/reference/` (`drawing-in-3d.md`, `em-setup.md`, `cli.md`)
**Depends on:** 113-a · **Blocks:** 116's multi-terminal half
**Changed by D14 (owner, 2026-10-05):** terminal ports run on openEMS only. Palace refuses them by name, so this brief
no longer carries anything that existed only for Palace's shared face (`Mode`, the Gmsh face claim).

---

## 0. What this brief delivers

A `.c3d` wave port that is **one port with N terminals**: N signal conductors and one reference, all meeting the port's
rectangle on an air-box face. Everything up to the solver understands it. It can be drawn, saved, reopened, inferred,
checked, explained and shown in the editor. **No solver runs it yet.** openEMS refuses it until brief 116 lands; Palace
refuses it for this series (overview D14), with a sentence pointing to openEMS.

Overview rule 3 governs throughout: **a one-terminal wave port, and every lumped port, is byte-identical to today**, in the
file, the problem, the Gmsh script and the Palace configuration.

---

## 1. `R-em3d114-1` — the file

**`R-em3d114-1a`** `C3dPort` gains (overview D3, as settled):

```jsonc
{
  "Name": "Left", "Kind": "Wave", "Plane": "YZ", "Offset": 0, "Rect": { … },
  "Reference": "gnd",                                   // optional; inferred when absent (1c)
  "Terminals": [
    { "Number": 1, "Name": "P1", "Conductor": "strip_a", "Z0": "50" },
    { "Number": 2, "Name": "P2", "Conductor": "strip_b", "Z0": "50",
      "VoltagePath": { "From": [..], "To": [..] } }    // optional; inferred as brief 23 infers it
  ]
}
```

- A terminal also takes `Flip` (as a port does today).
- When `Terminals` is present, the port-level `Number`, `Z0`, `Positive`, `Negative`, `VoltagePath` and `Flip` are **not
  written**, and a file stating both `Terminals` and any of them is refused with a sentence naming the conflict. The
  port's `Name` stays: it labels the port (`Left`), and each terminal's `Name` labels its result port (`P1`).
- `Terminals` with **one** entry is refused: write the ordinary single-terminal port. That keeps one spelling per meaning.
- `Kind: Lumped` with `Terminals` is refused: a lumped sheet has one gap.
- `Model: false` (brief 93) applies to the **whole port** (overview D5).

**`R-em3d114-1b` Numbers.** Every terminal number is unique across **all** of the document's port numbers, terminals and
ordinary ports together. A clash is refused, naming both. Brief 93's renumbering treats each terminal as one port.

**`R-em3d114-1c` The reference, inferred.** Among the conductors meeting the region, using `C3dPorts.Negative`'s existing
evidence, generalised from two conductors to N:

1. exactly one is in the ground set (an air-box PEC face counts, as today) → that one;
2. otherwise the one with the largest surface;
3. a tie → refused, asking for `Reference`.

The reason is kept and shown by `explain` and the overlay, as the two-conductor case does today.

**`R-em3d114-1d` Terminals are never invented by the reader** (overview D4). A wave port with no `Terminals` whose region
meets three or more conductors stays refused. The sentence changes from "a wave port's mode runs between two conductors"
to one naming the conductors found, the inferred reference, and the two fixes: add `Terminals`, or use *Make Port ▸ Wave*
on that face.

**`R-em3d114-1e`** Each terminal's conductor must meet the region (the existing "feet" test) and must not be the reference.
Two terminals naming one conductor are refused. Each terminal's voltage path is inferred exactly as the single-terminal
case infers it today (from the reference's foot to the terminal's), or stated.

---

## 2. `R-em3d114-2` — the problem layer

**`R-em3d114-2a`** Each terminal lowers to its own `Em3dPort` with `Kind = Wave`. That makes overview rule 1 hold by
construction: the problem's `Ports` list is the result's ports. The new init-only properties, both null/default on every
port that exists today:

- `FaceGroup` (`int?`) — shared by a port's terminals; null for a one-terminal port. openEMS's lowering (116) uses it to
  give the group one feed extension.

(An earlier draft also added `Mode`, 1…N within the group, for Palace's shared-face modes. Brief 113-a found that route
unusable and D14 keeps terminal ports off Palace, so it is not added. Brief 119 decides what Palace would need, if
anything.)

**`R-em3d114-2b` `Em3dProblem.Validate`** gains the group rules:

- Every port in a group has the same rectangle, face and `NegativeObject`.
- Each has a distinct `PositiveObject`, and every one has a `VoltagePath`.
- Every port in the group is `Wave`.

**`R-em3d114-2c`** *(removed by D14)*. `GmshGeoWriter` needs no change: Palace refuses a group before Gmsh runs.

**`R-em3d114-2d` `Em3dPortMap`** records, for each result port, its terminal: `result port 2 is the 3D view's port 'Left',
terminal 'P2' (strip_b)`. The diagnostics cube carries the document number as today.

**`R-em3d114-2e` Refusals.** `Em3dRunService` refuses a problem with any `FaceGroup`:
- on **Palace**, for this series (D14): "Port 'Left' has two terminals; terminal wave ports run on openEMS only in this
  version. Set the setup's solver to openEMS." This sentence stays when 116 lands;
- on **openEMS**, until brief 116 lands: "Port 'Left' has two terminals; terminal wave ports are not yet built for
  openEMS." Brief 116 removes it.

Both are **before Gmsh**, as every capability refusal is.

---

## 3. `R-em3d114-3` — the editor

- **`R-em3d114-3a` *Make Port ▸ Wave*** on an air-box face region that meets N+1 conductors:
  - writes the port with the inferred `Reference` and N `Terminals`;
  - numbers the terminals with the next free numbers, in a stable order (by the conductor's foot position along the
    rectangle's long axis, then by name);
  - names them `P<n>`.

  The status line says how many terminals were made and which conductor is the reference. One undo step.
- **`R-em3d114-3b` The port rows**: a multi-terminal port is one row (its tick hides the whole port, as D5 implies) with a
  child row per terminal showing number, name, conductor and Z0, each editable as a port row's are today.
- **`R-em3d114-3c` The overlay** draws each terminal's voltage path as an arrow from the reference to its conductor, with its
  number at the head, in the port colour. The refusal outline and text behave as today.
- **`R-em3d114-3d` The context menu** on a multi-terminal port offers *Set Reference…* (conductors meeting the region) and,
  per terminal, *Flip*.
- **`R-em3d114-3e` The *Make Port ▸ Wave* tip** (`C3dEditorViewModel.Simulate.cs` ~732) adds one clause: a face met by more
  than two conductors makes one terminal per conductor besides the reference.

## 4. `R-em3d114-4` — `check` and `explain`

- `check` reports every 1b–1e refusal with the port and terminal named, and every 2b violation (which can only arise from
  hand-written files).
- `explain` prints, per multi-terminal port: the face, the reference and why it was chosen, and per terminal its number,
  conductor, Z0 and voltage path (end points in the display unit), plus the line "terminal S: each terminal is a port of
  the result".
- `DocumentSchema` (the `reference` topic the MCP server serves) documents `Reference` and `Terminals`.

## 4a. `R-em3d114-5` — user docs (sources only; overview §2a)

Edit `docs/user/src/` only. Do **not** run DocGen or regenerate `docs/user`; the owner does that.

- `drawing-in-3d.md` **#simulate** (the port paragraph, ~1110): a wave port on a face met by several conductors is one
  port with a terminal per conductor. Cover what the reference is and how it is chosen, and the arrows the overlay draws.
  In **#reference** (the port entries, ~1503–1511): *Make Port ▸ Wave*'s terminals, the terminal rows, *Set Reference…*,
  and per-terminal *Flip*.
- `em-setup.md` **#wave-ports**: a new subsection, *Several conductors on one face* {#wave-port-terminals}. It covers what
  a terminal is, that each terminal is a port of the result (numbering, with the pair as the example), the `Reference` and
  `Terminals` keys with a short JSON block, and that a `.cem` cannot state one (overview D2). **Which solver runs it is
  left to 116** to write. Until it lands, the subsection says nothing about running, rather than stating a refusal that
  is about to be lifted.
- `cli.md`: `explain`'s terminal lines (4).

## 5. Gate

`tests/Ui.Tests/ThreeD/TerminalWavePortTests.cs` (and the existing port suites, unchanged). No solver.

1. **Round trip.** A two-terminal port writes and re-reads to the same object; its JSON has no port-level `Number`/`Z0`.
2. **Inference.** Stripline pair (two strips, two ground planes joined by side walls into one conductor): the reference is
   the ground, the paths run from it to each strip. Three conductors and no ground-set member: the largest is the
   reference. A tie is refused, asking for `Reference`.
3. **Refusals.** Each of: both `Terminals` and `Positive`; one terminal; a lumped port with terminals; a terminal on the
   reference; two terminals on one conductor; a terminal number clashing with another port; three conductors and no
   `Terminals` (the new sentence, naming the fix).
4. **Problem.** The lowering has two `Em3dPort`s sharing `FaceGroup`, the same rectangle and negative object;
   `Validate` refuses each 2b violation built by hand.
5. **Byte identity.** Every existing `.c3d` example and fixture re-saves unchanged. Every existing Palace and Gmsh golden
   (`testdata/em3d/palace-goldens`) is regenerated byte-identical.
6. **Make Port.** On the pair's face the command writes one port, two terminals, numbers 1 and 2 (or the next free), and
   one undo step removes it.
7. **Refused to run.** A two-terminal setup on Palace and on openEMS is refused before Gmsh with 2e's sentences (Gmsh
   invoked 0 times).
8. **`explain`** prints 4's lines for the fixture.

## 6. Owner check

- Draw the stripline pair, *Make Port ▸ Wave* on each end face, and see two numbered arrows per face.

## 7. Scope

- No solver output. No `.cem` terminal ports (overview D2).
- No change to a lumped port or a one-terminal wave port, in any file (rule 3).
