# Brief IM-10 — A schematic picture made into a schematic

**Series:** `brief-img-0-overview.md` (D4, D5, D10, D11, D12, D13, D14, D16) · **Tag:** `R-im10-<m>`
**Depends on:** IM-8, IM-9, IM-5
**Area:** `src/Design/Schematic/Recognition/` (`SchematicImageRecognition.cs`, `SchematicImageEmit.cs`),
`src/Design/Schematic/NetlistSchematic.cs` (exact placement hints), `src/Design/Layout/Recognition/PartsTable*.cs`
(picture rows), `src/Ui/Recognition/` (the schematic-picture mode), design note §11, `docs/user/src`

---

## 1. Goal

Turn what IM-7 … IM-9 read into a `TestBench`, draw it **the way the picture draws it**, lay the picture under it, and
light up the dialog's schematic-picture mode.

## 2. Requirements

**R-im10-1 — Entry points.** `SchematicImageRecognition.Circuit(ImageSource, options, partsCsv?) → result` (no write;
the CLI's read-only default and the dialog's preview) and `.Run(…, target)` (writes), mirroring
`ArtworkRecognition.Circuit`/`Run`.

**R-im10-2 — The circuit.** Nets from the wire graph: connected wire components (crossings per D11), joined by equal
net labels and equal supply names. Each recognised symbol becomes its component with its pins on those nets; kind `?`
becomes C (AS rule) with the `?` kept in the parts table; cut-out devices give one Port per wired pin (AS D9, numbered
after the drawing's own ports). Ground symbols join node 0; a supply net with no source is left as a named net and
listed (AS D9's open-pin rule). Transmission-line symbols become **TLIN** with Z and E from their line set, or **TLIN
physical form** with L and the technology's εeff when the set gives a length; unread parameters become variables like
any other value. Every unread value is a global variable with a `tune` entry, `<Refdes>_<Param>`, at AS D10's
transparent initial value.

**R-im10-3 — Ports (D13).** In priority: port and terminal symbols; connector symbols; net or port names that are port
words; dangling wire ends at the drawing's left and right extremes (left numbered first, top to bottom). With none, the
schematic is written **without** its S-parameter analysis and the report says *no port found: add Port components to
simulate*. With ports, AS D15's analysis.

**R-im10-4 — The drawing as drawn (D12, owner-decided).** `NetlistSchematic.Build` gains **exact placement hints**: per instance a
grid position and a rotation/mirror, and per net an optional list of orthogonal wire routes. The picture frame maps to
the schematic so the median recognised pin span equals the symbols' own; positions snap to the schematic grid; wire
routes are the picture's wire segments, snapped, re-joined to the snapped pins with at most one dog-leg each. An
instance whose snapped pins would collide with another's falls back to the existing automatic placement for that
instance only, and is reported. **No hint, no change**: `Build` without hints is byte-identical to today (AS-6's own
rule for its ordering hints). *Arrange: As drawn / Tidy* chooses whether hints are passed at all.

**R-im10-5 — The underlay (D5).** The picture as a locked `EditableBitmap` at 35 % behind the drawing, at the frame of
R-im10-4, so each symbol sits on its picture. Not written with *Tidy* (it would not line up) — the report says so — or
when the setting is off.

**R-im10-6 — Targets (D14, owner-decided).** A new cell (default name from the file name), or — started on a placed schematic
bitmap — **this schematic, over the picture**: the components, wires and variables added through the schematic
editor's own commands as **one** undo step, at the frame the placed bitmap defines (its rect sets the scale and the
offset; no second underlay is added), selected afterwards. Variables that already exist in that schematic with the
same name get a suffix and are reported.

**R-im10-7 — The parts table for a picture.** Rows from recognised symbols: Refdes, Kind, Connection, Value, Model,
Confidence, and X/Y as **pixel** coordinates. `PartsTableCsv` writes the unit on the coordinate columns' header only
when it is not the layout one, so every existing CSV and every AS gate is byte-identical; a CSV with pixel coordinates is
refused for an artwork source and the other way round. The CSV overlay rules (rename by X/Y, Kind/Value/Model edits)
apply unchanged.

**R-im10-8 — Correcting connections.** In the dialog's overlay, a crossing can be clicked to flip *connected / not
connected*, and a dangling end can be dragged onto a pin to connect it. Both are held as **overrides** (like table edits,
re-applied after a re-run) and carried to the CLI as `--connect`/`--disconnect` pixel points (IM-11), so the format
stays the contract. Nothing else is editable on the picture — the schematic editor is where a drawing is fixed.

**R-im10-9 — The dialog's schematic-picture mode.** IM-5's *Schematic* kind now reads: the overlay of R-im5-3's
schematic classes, the parts table with picture rows, *Arrange* and the crossing rule in the options row, Learn These
Glyphs live for picture rows. Create calls `SchematicImageRecognition.Run`.

**R-im10-10 — Provenance and the report.** The `ImageSource` block (IM-2) with the reading options and overrides; one
report: IM-7's, IM-8's, IM-9's and the emit's (nets, ports and how found, variables created, placement fallbacks).

**R-im10-11 — User page.** The IM-5 page gains *Schematic pictures*: what is recognised (D9), conventions (D11), values
as variables, *As drawn* and *Tidy*, correcting a crossing, and the Update Layout from Schematic hand-off. Edit sources
only; **do not run DocGen**.

## 3. Not in this phase
Device models for cut-out parts (AS D18); any layout output (D3).

## 4. Gates (minimal tests, run only these classes)
- `SchematicImageRecognitionTests` — an in-memory picture of a two-port L-C-L ladder with ports, values and a supply
  stub gives the same netlist (kinds, connectivity, values) as the `.cnl` it was drawn from; with values smudged, the
  same netlist with three variables and three `tune` entries; with the ports removed, no analysis and the report line.
- `NetlistSchematicHintsTests` — `Build` without hints is byte-identical to before; with exact hints each instance is at
  its snapped position and rotation; a colliding hint falls back for that instance only.
- `PartsTableCsvTests` (existing) — one case: a pixel-coordinate CSV round-trips and is refused for an artwork source;
  an artwork CSV is byte-identical to before.
- `SchematicImageOverridesTests` — a flipped crossing survives a re-run and reaches the entry point as `--disconnect`'s
  point.
