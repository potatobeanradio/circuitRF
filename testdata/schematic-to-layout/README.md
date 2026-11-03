# Schematic-to-layout test designs

The designs every Update Layout from Schematic behaviour is gated on (`docs/briefs/brief-sdl-0-overview-and-groundwork.md`
§4). `tests/Ui.Tests/SchematicSync/SdlChecker.cs` plans each design onto an empty layout, applies the plan to a copy and
checks LVS, open nets, overlaps and the exact set of report lines.

- `designs.txt` lists the designs and the brief that makes each one pass. A brief adds its own.
- `microstrip-0508.ctech` is the one technology every design is placed on: two copper layers on a 0.508 mm substrate.
- Each design is a cell folder: `schematic/<name>.csch` and `expect.txt`. A footprint that is not generated from a case
  size is a cell folder of its own under the design's `parts/`, with a `.ccell` declaring its terminals.
- A folder not listed in `designs.txt` is a variant a unit test reads (`F2b-three-shunts`), not a design the checker gates.

`expect.txt`, one entry per line, `#` for a comment:

| Line | Meaning |
|---|---|
| `<Info\|Warning> <schematic id> <text fragment>` | One report line the run must produce. `-` is the id of a run-level line. A component with no layout view reads as a `Warning` under its own id. Every line the run produces must be listed, and nothing more. |
| `joinable <net>` | A net the placement must join: no ratsnest line may remain on it. |
| `lvs <finding id>` | An LVS error the design is expected to have. Any other LVS error fails it. |
| `overlap` | Copper joining two different nets is expected. Without it, any such contact fails the design. |
