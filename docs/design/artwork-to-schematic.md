# Create Schematic from Artwork

**Series:** `docs/sonnet-briefs/brief-artsch-0-overview.md` (decisions D1–D20) · **Code:**
`src/Design/Layout/Recognition/` · **Builds on:** the trace impedance review (`src/Design/Layout/Em/TraceImpedanceAnalysis*.cs`),
the shared copper partition (`src/Design/Layout/Extraction/CopperPieces.cs`, `src/Design/Layout/Drc/DrcConnectivity.cs`),
the board interchange readers (`src/Design/Layout/Interchange/`), railRF's part discovery, the footprint work, the drawn
netlist (`src/Design/Schematic/NetlistSchematic.cs`) and the microstrip family's stackup binding.

This note records the decisions and the pipeline, then one section per phase: the board graph (§4), parts (§5),
lines (§6), the circuit and its target (§7), the dialog (§8), the silkscreen (§9), Swap Line Type (§10), the line
models the series added (§11), the command line (§12), and the acceptance gate, field fixtures and example (§13).
§14 is the known limits. AS-3 wrote it; each phase added its section.

---

## 1. What it is for

A user imported a production board's Gerbers, put a port at every part, ran the planar MoM engine and was refused at
about two million unknowns. The board was simple: about ten SMT parts joined by 50 Ω lines and a top pour stitched to
the bottom plane by hundreds of vias. Nothing on it needed an EM run.

The command turns flattened artwork — an imported Gerber, a flattened GDSII, a `.kicad_pcb` read back as copper — into a
**schematic of native circuitRF components** that simulates in seconds: parts as R, L, C (or an SnP), traces as MLIN and
its discontinuities, grounded coplanar waveguide and stripline as CPWG and SLIN, anything else as TLIN in its physical
form, the stitching vias dropped and the vias that matter kept as VIA / VIAGND. It is a best attempt that the user then
checks and cleans up — a light model between "one impedance number" and brute-force EM, good to about 2 GHz on an
ordinary board, enough to predict a matching network and to compare a datasheet's target against a measurement.

## 2. The pipeline (D3)

```
artwork (.clay + .ctech [+ .cem] [+ placement/BOM])
   │
   ├─ AS-3  board graph   — ground net, via classes, signal islands, ports, scope
   ├─ AS-4  parts         — evidence → parts table (editable; CSV); AS-10 adds the silkscreen as evidence
   ├─ AS-5  lines         — TraceImpedanceAnalysis chains → line elements (CPWG / SLIN / TLIN from AS-1, AS-2)
   │
   └─ AS-6  emit          — TestBench (+ globals, tune entries, sparam analysis)
                             └─ NetlistSchematic.Build → .csch in the target cell

surfaces: AS-7 `circuitrf recognize` + MCP `recognize` · AS-8 Design ▸ Create ▸ Schematic from Artwork…
afterwards: AS-11 Swap Line Type · gate: AS-9 round trip
```

`ArtworkRecognition.Recognize(RecognitionInput, RunControl?)` is the one entry point. It is pure: it reads what it is
handed, posts nothing, writes nothing, and returns a `RecognitionResult` — the board graph, later stages' outputs, and a
`RecognitionReport`.

## 3. The locked decisions, and why

| | Decision | Why |
|---|---|---|
| **D1** | Recognition produces a netlist (a `TestBench`); the drawing already exists (`NetlistSchematic.Build`). The `.cnl` text is a first-class output. | No second schematic writer. The `.cnl` is what a test compares and an agent reads. |
| **D2** | Everything that decides anything lives in `src/Design/Layout/Recognition/`, below the firewall; `src/Ui` holds the dialog, the parts-table view model and the cross-probe only. | A rule that lives only in the dialog is a rule the CLI does not apply. AS-7's gate is byte identity between the two. |
| **D3** | The pipeline above; the trace review is reused, not re-derived. Where it lacks something, the missing record is added to its output. | Its pieces, chains, junctions, end kinds, per-station Z0/εeff and configuration strings are exactly what line recognition needs; walking the copper twice is two answers. |
| **D4** | Target: a new cell (default `<artwork cell>_model`, made with `CellCreate`), or the artwork cell only when it has no schematic view. A re-run replaces only a schematic this command wrote, after a confirmation and a history checkpoint. | A hand-made schematic is never overwritten; the checkpoint is the revision-control floor. |
| **D5** | Every created component carries `FromArtwork=true`; Update Layout from Schematic skips it, Update Schematic from Layout does not duplicate it. The command never writes the layout. | Without it, a schematic written into the artwork cell would have its MLINs generated as copper on top of the imported Gerber. |
| **D6** | Ground: a named ground net (`GND`, `AGND`, `0`); else the piece with the largest copper on the stackup's reference conductor, and everything joined to it. A separate pour is a signal island and is reported. The user can name another. | The evidence order a designer would use; an override because every heuristic has a board it misreads. |
| **D7** | Vias: ground–ground dropped; signal–signal across layers a VIA; a ground pad that is its own island a VIAGND per via, nearest four; signal–ground a VIAGND. `vias=ground` makes every VIAGND a GND. | Hundreds of stitching vias are the thing that refused the MoM run; the few that carry current to ground are real inductance. |
| **D8** | Ports, in priority: the `.cem`'s, layout pins and port labels, a multi-pin part's pads on a line, an edge launch or connector, a scope crossing. All 50 Ω. | The user's own ports first; the rest are the places a matching network is measured from. |
| **D9** | A part with more than two pads is cut out; its pads on lines become ports, on ground join ground, the rest are left open and listed. | The schematic is the networks around the device as the device sees them — what matching work needs. The device comes in afterwards as an SnP or through Import Component. |
| **D10** | Parts evidence: placed footprints, placement + BOM, land-pattern match, silkscreen (AS-10). Unknown values become global variables `<Refdes>_<Param>` with tune entries and transparent initial values. The parts table is a CSV. | Every unknown part is a knob; the first simulation shows the lines alone. The format is the contract for a user and an agent alike. |
| **D11** | Scope: the whole layout, the selection, or `--region` with SI units. A line crossing the boundary becomes a port. | The designer's "clip a section" case, without any EM. |
| **D12** | The schematic carries a provenance block; each component its artwork anchor. | The anchor is the cross-probe; the provenance marks a schematic as this command's to replace. |
| **D13** | Line element by the majority configuration of its stations: MLIN, CPWG, SLIN, else TLIN in its physical form. The coplanar reading is the user's to choose; measured gaps are always recorded. | A trace is easily misread as GCPW when a pour comes close; recording the gaps makes it one swap away. |
| **D14** | MLIN regions get MBEND / MTEE / MCROSS / MTAPER; CPWG / SLIN / TLIN regions do not. Short pieces merge into neighbours; no line is dropped for being short. Coupling is reported, not modelled. | A short thin line is an inductor; coupled-line models are not in this series. |
| **D15** | The testbench carries an S-parameter analysis: the `.cem`'s range, else 100 MHz – 6 GHz, 201 points. | Enough to see a match; editable. |
| **D16** | Every guess and omission is a finding with a count. Refused only for no technology, no copper in scope, or no port. | A best attempt is the product; hiding what it guessed would make it untrustworthy. |
| **D17** | Committed fixtures are synthetic; field boards are git-ignored `FixtureFact` data. | Third-party artwork is never committed. |
| **D18** | Non-goals: coupled lines, step / open / gap models, case parasitics, multi-pin device models, MKLOPF, writing the layout. | Scope. |
| **D19** | Swap Line Type: MLIN ↔ CPWG ↔ SLIN ↔ TLIN in place, W and L held, Z0 free. | The misread-line remedy, useful on any schematic. |
| **D20** | A `.csch` may name its own technology (`TechRef`; `.cnl` `technology "<path>"`), resolved before the workspace default. | An imported board's cell carries its own `.ctech`; without this an MLIN would be computed on the wrong substrate, silently. |

## 4. AS-3 — the board graph

`src/Design/Layout/Recognition/`: `ArtworkRecognition.cs` (entry point, `RecognitionInput`, `RecognitionResult`),
`BoardGraph.cs`, `GroundReading.cs`, `ViaClassification.cs`, `PortDiscovery.cs`, `RecognitionScope.cs`,
`RecognitionReport.cs`, `RecognitionOptions.cs`.

### 4.1 Inputs and the partition (R-as3-1, R-as3-2)

`RecognitionInput.FromFile(clay)` reads through `TraceImpedanceAnalysis.LoadLayout` — the walk the trace review and
`circuitrf impedance` already use: the view, its technology resolved as the editor resolves it, the placed cells
flattened as a DRC run flattens them. Its EM setup is the `.cem` given, else the first in its workspace that analyses
it (`EmSetupResolver.FindSetupsForLayout`).

Islands are built over `CopperPieces.Build` — **the one partition** DRC, railRF and LVS share — and a via is classified
by the pieces its barrel touched, read off the partition's own record (`ConnectivityPartition.Barrels`, now carried on
`CopperPieces`). No barrel is tested a second time and there is no second connectivity model. The whole board is
partitioned **once**; a scope adds exactly one more partition, of the clipped copper.

The graph reads that partition at the **piece** level, not the net level. A shunt part's ground pad tied to the plane
by six vias is on the ground *net*, and so is a shorted stub; neither is ground *copper*, and the vias between them and
the plane are the elements the circuit needs.

### 4.2 Ground (R-as3-3, D6)

The ground choice, in order: `RecognitionOptions.GroundAt` (a point — on no copper it is a refusal naming the point);
`GroundNet` (a name the artwork does not state is a refusal listing those it does); a net the artwork names `GND`,
`AGND` or `0`; the largest piece on the stackup's ground-reference conductor; the reference itself where the stackup
declares it and the artwork draws nothing (every net a barrel carried down to it is ground); and, where no reference
is flagged, the largest copper on the board. The report states which.

Every conductor piece on the ground net is then either **ground copper** or not:

- **ground copper** (a pour or a plane) — the chosen piece; or a piece **wider than any trace** somewhere (it survives
  erosion by half the widest trace the review reads on its layer, `TraceImpedanceAnalysis.WidestTraceDbu` — its own
  `WidthRange`); or a piece carrying `PourViaCount` (4) vias or more that is **not pad-sized**;
- **a candidate ground pad** — pad-sized by the review's own pad rule: shorter than `MinAspect` (4) of its widths and
  no larger than the widest trace either way;
- otherwise copper on the ground net through a via: a shorted line.

A pour **not** on the ground net is a **separate pour**: a signal island, reported by name and location.

### 4.3 Vias (R-as3-4, D7)

An **island** is a set of non-ground conductor pieces joined through the barrels between them. A via's ends are the
pieces its barrel met, plus the undrawn reference when its span passes one:

| ends | class | element |
|---|---|---|
| ground copper only | `Stitching` | none — dropped and counted |
| islands only | `SignalTransition` | `VIA` |
| ground copper and a ground-pad island | `PadGround` | `VIAGND`, the nearest `MaxGroundViasPerPad` (4) to the pad's centre; the rest none, counted |
| ground copper and any other island | `SignalShort` | `VIAGND` |
| one conductor or none | `JoinsNothing` | none — counted |

A **ground-pad island** is an island whose every piece is a candidate ground pad and which has at least one via to
ground copper. A part's ground pad that sits **on** a pour is part of the pour's piece, so its vias are stitching and
the part is a plain GND; nothing needs a rule for it. Under `vias=ground` every `VIAGND` is `GND`. Several VIAGNDs on
one pad are reported once: their mutual inductance is not modelled. Each via keeps its drill, pad, span and position for
AS-6's `ViaSubstrateInjection`.

### 4.4 Signal islands (R-as3-5)

An island is a **signal island** — one node of the circuit before AS-5 subdivides it — when it is not a ground pad and
it carries a port, a pad of a two-pad placed part, a kept via, a line (any piece that is not pad-sized), or a separate
pour. Anything else is **copper read as nothing** (a test point, a logo, a fiducial): listed and left out, but kept in
the graph as `IslandKind.Nothing` so AS-4, finding a part's land on it, can promote it. A multi-pin part's pad does not
make its island a node by itself (D9): a bias pad on a pad of its own is left open.

### 4.5 Ports (R-as3-6, D8, D9)

`PortDiscovery`, in priority order:

1. the `.cem`'s ports — a layout's port labels, numbered by `EmPortExtraction.NumberPorts`, with the setup's Z0;
2. without a `.cem`, the same labels as port labels; then layout pins;
3. pads of a placed part with more than two pads that land on a signal island (`PlacedPins.Of`, artwork only). Placement
   rows and land-pattern clusters are AS-4's reading;
4. a signal island reaching the board outline within `EdgeReachMicrons` (500 µm) — the importer's `Outline` layer where
   it drew one, else the copper's extent — one port per stretch of copper in the edge strip, at the copper's own edge; a
   separate pour is not an edge launch. And the pads of a connector footprint — a placed part or a placement row whose
   designator is J, P or X **followed by a digit** (so `PS1`, a power supply, is not one) — on a signal island;
5. a crossing of the scope's boundary, centred on the cut, named `X1`, `X2`, ….

A connector's pad is the launch of its island: an edge port on an island a connector pad lands on is dropped (AS-12 —
an edge SMA's centre pin and the line's edge are one port, often more than 1 mm apart). A part or connector pad, or an edge, on
ground-tied copper is no port: an island a via joins straight to ground whose own shapes name a ground net (the chosen
ground's name or `GND`/`AGND`/`0`) and no other — a shell tab, a ground strip at the edge. Shape names, because the
partition names nets galvanically and the via has already joined the island's net to the plane's. Unnamed copper tied
by a via keeps its ports (an RF line with a shorted stub); a board tool names such a line as ground, so there it loses
them — the known limit. A multi-pin part's ports take their numbers in its pin
order within the places they hold. A port within `PortMergeMicrons` (1 mm) of a higher-priority port on the same island is that port. Ports from labels
keep the number they state; every other port takes the lowest free number, pins first, then left to right and top to
bottom. Names are `P<n>`, a label's own text where it is not a bare number, a pin's name, `<Refdes>.<pin>` for a part's
pad, or `X<k>`. A port label, pin or connector that lands on no signal copper is reported, not a port. No port in scope
is the refusal *"Nothing in scope reaches a port — add port labels, a .cem, or widen the selection."*

### 4.6 Scope (R-as3-7, D11)

`RecognitionScope` is `Whole`, `Rectangle(...)` or `Polygons(...)` (the selection's outline), DBU. Copper is clipped to
it with `LayoutClipper`; a via or a label is kept where its centre is inside. **Ground is read from the whole board**:
each clipped piece takes the class of the whole-board piece under it, so a pour the selection cuts in two is still one
ground. A crossing is where the original copper continues past the boundary — looked for in a band starting two
microns outside it, because a selection's outline is the selected copper's own edge and the board's copper differs
from it by fractions of a micron (chords of a flattened arc, the hairline close moving a curved edge); read on the
boundary itself, every such sliver was a port. When the scope cuts a piece of ground copper the report says so.

A `Polygons` scope may carry **layers** — the GUI's are the selected shapes' and parts' own. Clipped copper on any
other electrical layer is kept only where a via joins it to copper on those layers (`ScopeOtherLayersLeftOut` reports
the rest); non-electrical artwork is kept whatever its layer. A rectangle carries none and reads every layer.

### 4.7 The report (R-as3-8, D16)

`RecognitionReport` is a list of `RecognitionFinding(Class, Count, Sentence, Anchors)` — the GUI expands a class and
cross-probes its anchors, the CLI prints one line per class. AS-3's classes: ground chosen, stitching vias dropped,
ground vias kept (and capped, and their coupling not modelled), signal vias kept, vias joining nothing, separate pours,
copper read as nothing, conflicting net names, ports by source, ports on no signal copper, and the scope cutting ground.

### 4.8 Gates

`tests/Ui.Tests/Recognition/`: `BoardGraphGroundTests`, `ViaClassificationTests`, `PortDiscoveryTests`,
`RecognitionCountersTests` (the 200-via board reads its partition once — a counter on the `ConnectivityCache`), and
`ArtworkRecognitionFieldTests` (`FixtureFact` over the git-ignored `testdata/artwork-boards/<board>/`, each with a
hand-written `expected.json`: `{ "clay": "<path to the .clay>", "ports": <count> }`).

---

## 5. AS-4 — parts and the parts table

`PartReading.Read`, called by `ArtworkRecognition.Recognize` after the board graph is built; the result carries a
`PartsTable` (`RecognitionResult.Parts`). Files: `PartReading.cs`, `LandPatternMatch.cs`, `PartEvidence.cs`,
`PartsTable.cs`, `PartsTableCsv.cs`, `PartModelResolution.cs` (class `PartModelResolver` — railRF already has a record
named `PartModelResolution`, the one-library resolution this calls).

### 5.1 Evidence (R-as4-1, D10)

Strongest first, and every field of a row records which source gave it (`Evidence`: `refdes=placement;kind=refdes;
case=land;value=bom;pn=bom;model=file`):

1. **Placed instances** — `PlacedPins` (railRF's and LVS's walk) gives each designated part's pads on their own land
   layer; `LayoutPartKind.Of` the declared kind; the land-pattern cell's name the case (`smt-<case>@<density>_<hash>`
   parsed exactly, else any unambiguous token through `FootprintTokens`).
2. **Placement + bill of materials** — passed in already read, refusals intact: a refused placement or BOM refuses the
   recognition with the reader's own sentence, so an unstated origin is never guessed. A placement row lands where
   `RailPartMarks.For` puts the part's body (the row's point ± `PadReachDbu`), on the nearest unnamed land pattern whose
   centre or a pad is inside it. A row naming a placed instance only adds its case. BOM rows are taken by designator;
   several rows with different part numbers or values give neither, with a note.
3. **Land patterns** (`LandPatternMatch`) on pads no instance claims. Pad outlines come from the side's **paste**
   openings, else its **solder-mask** openings (by board-format alias, purpose, then a name carrying the side), each a
   union of the layer's shapes kept where it is at least 82 % of its bounding box and has copper of that side under its
   centre. A side with neither reads **copper**: pad-shaped pieces by the trace review's own pad rule, and the copper
   ahead of every trace end the review names `"pad"` (an axis-aligned window as long and wide as the largest land). The
   reference lands and mask openings are **generated** — `ChipLandPatternGenerator` draws every case at every density
   once on a minimal board technology — never tabulated. A pair matches a case when both pads' along/across extents and
   the gap are each within 20 %, at 0° or 90°; smallest RMS error wins; a different case within 5 points is the
   runner-up, named in the row's notes. Candidates resolve best fit first; a pad is in one part at most. A third pad of
   the same size continuing the pair's line with the pair's own gap (±10 %) makes the pair two pins of a package row.
2½. **Declared pads** (AS-12, `DeclaredPads.cs`) — copper shapes whose `Pin` carries a Gerber `%TO.P` (`C1,1`,
   `J1,1,In`; a bare pin takes the shape's `Component`). Grouped by refdes, one pad per pin on the pin's topmost
   copper layer, shapes of one pin that touch merged and ones that do not kept as separate pads (a connector's ground
   tabs all declare one pin). Pads are in natural pin order, so `SeriesTerminals` gives pin 1 first. A declared part
   named by an instance is the instance's; one standing on a differently named instance's pad is dropped and
   reported (`DeclaredRefdesDisagrees`). A placement row over declared pads of another name renames them — placement
   is the stronger source for the designator — with the same finding. A land pattern whose two outlines are one
   declared two-pad part's pads gives it its case and makes no part; one touching any declared pad makes none. The
   declared pads join the instance pads for signal-island marking and port discovery; a declared pin name names a
   connector's port (`J1.In` when two connectors share it). It is not an `IPartEvidenceSource`: that seam names
   parts the reading already found, and declared pads form them. The declared NETS are a check only
   (`DeclaredPads.CrossCheck`): ground copper and every island a via joins straight to ground is one node; a node with
   two nets is `DeclaredNetsShorted`, a net on two nodes `DeclaredNetsOpen`, else one `DeclaredNetsAgree` line.
3½. **Body outlines over a gap** (`PartReading.BodyOutlineParts`) — for a board drawn with no lands and no mask, where
   a series part bridges a gap cut in a line and a shunt part a line's edge and a pour. A body is a closed outline on a
   silkscreen or assembly layer (one shape, a closed stroke, or a drawing's lines of no width that chain into a rectangle,
   as `DxfCopperLoops` chains copper; a stroked legend's open strokes are letters and never chain), at least 82 % of its
   box, at least 1.2 times as long as it is wide and no longer than a land pattern spans. Each half along its length takes
   the copper piece of its side with the most area under it, which must cover a quarter of the half; two different
   pieces make a two-terminal part, a terminal at the middle of the copper under each half; an outline with a third piece
   under an end or its middle as an end sits on copper (a quarter of a half) is a part of more terminals and is said instead
   (`BodyOutlinesOverManyPieces`) — a pour's sliver along its long edges is not. An outline
   whose box meets a
   part another rung found, or holds its pads, makes none. Below land patterns, so the pads it reads are only where the
   drawing says the part is: confidence `medium`, `Evidence` `refdes=silk` once named. Reported as
   `PartsFromBodyOutlines`.
4. **The silkscreen** (AS-10, §9), then any further `IPartEvidenceSource`: each source's `PartClaim`s are given to the
   unnamed parts one to one by `RefdesAssociation` — never to a part a stronger source named, never a designator a
   stronger source already gave.
5. **Nothing**: generated designators `C_A1`, `C_A2`, … top to bottom, left to right.

### 5.2 Kind, value, connection, model (R-as4-2 … R-as4-6)

- **Kind**: designator prefix (`R`; `L`/`FB`/`FL`; `C`; `J`/`P`/`X` → Connector; a two-pad `D`/`Q`/`U`/`IC`/`Y`/`SW`/`TP`
  is `Ignore` with a note, more pads `MultiPin`), then the placed `PartKind`, then the BOM description's type word
  (`BomTablePaste.ReadTypeWord`, shared). More than two pads is `MultiPin` unless a connector. A BOM not-fitted marker
  (`BomTablePaste.IsNotFittedMarker`, plus a capital `NF`) is `Open`; a jumper case or an R whose value is 0 Ω is `Short`.
- **Value**: the BOM value column, else its description, through `BomTablePaste.TryReadValue` (SI prefixes, the
  letter-as-decimal `4R7` / `10n0`), in base SI. A value of another dimension than the kind is a note and a
  `PartValueWrongDimension` finding, and is not used.
- **Unknown value** → `Variable = <Refdes>_<Param>` on every modelled R/L/C/unknown row without a value or a measured
  model; `PartRow.TransparentValue` is its starting value (series C 100 pF, L 0.1 nH, R 0 Ω; shunt C 0.01 pF, L 1 µH,
  R 1 MΩ).
- **Connection**: the two terminals are paired by `RailPartDiscovery.SeriesTerminals`, and each is looked up in the
  board graph — an island, ground copper (a body piece, or a `PadGround` island) or no copper. One end on ground is
  `Shunt` (terminals ordered signal end first); both on ground `Shorted`; both on one island `Bridged`; otherwise
  `Series`; an end on no copper `Unplaced`. Shorted, bridged and unplaced parts are left out (`PartRow.IsModelled`).
  A modelled part's island that AS-3 read as `Nothing` is promoted to `Signal`.
- **Model**: a BOM part number whose row in any workspace `.crlib` attaches a Touchstone file, else a workspace `.sNp`
  whose name begins with it (shortest name wins, the others noted). Dot-folders are not searched. A file with other
  than two ports is not used and the row says so.

### 5.3 The table and its CSV (R-as4-7, R-as4-8)

Columns `Refdes, Kind, Connection, Case, Value, Variable, Model, ModelFile, PartNumber, X, Y, Evidence, Confidence,
Notes`, UTF-8, comma-delimited, natural designator order, LF line ends. Values are written `4.7 Ohm`, `100 pF`,
`1 uH` — the spelling the BOM value reader reads back. `X`/`Y` are in the layout's display unit with the unit stated.
`ModelFile` is relative to the workspace root (the layout's folder when there is none). Confidence: `high` for a placed
instance or a BOM row on a part the placement landed; `low` for a land pattern alone of unknown kind; `medium` otherwise.

Reading back is an overlay on a fresh recognition: `Kind`, `Value`, `Variable`, `Model`, `ModelFile` are applied (a
value clears the variable; a kind change keeps a value only within the same dimension; an SnP file is checked for two
ports); `Connection`, `Case`, `X`, `Y`, `Evidence`, `Confidence` and `PartNumber` are measured and ignored, with a note
where they differ; `Notes` is ignored. An unknown column is a refusal naming it; a designator the board does not have
is reported and ignored — unless the row's `X` and `Y` are exactly those of a part whose designator was read off the
silkscreen or generated, which it then RENAMES (AS-10 R-as10-5): those two designators are recognition's guesses. The
renamed row's evidence is `refdes=user`, `PartRow.RenamedFrom` keeps the board's designator, its kind follows the new
prefix where the kind came from the old designator or was unknown, and a default variable follows the new name. A
designator from a placed footprint, a placement file or a BOM is the board's own and is never renamed. Write then read
is the identity.

### 5.4 The report (R-as4-9)

Parts by source (instances, placement, silkscreen, land pattern alone, body outlines over a gap), unknown kinds, unknown values (the variables),
values in the wrong dimension, SnP models, land-pattern ambiguities, shorted / bridged / off-copper parts left out,
multi-pin parts and connectors cut out (with their part numbers, for Import Component), BOM designators and placement
rows not found on the board, sides with no mask or paste layer, and a parts table's notes and unknown designators.

### 5.5 Gates

`LandPatternMatchTests`, `PartReadingTests`, `PartModelResolutionTests`, `PartsTableCsvTests`, and
`PartReadingFieldTests` (`FixtureFact`; the field board's `expected.json` gains `"parts"`, `"series"`, `"shunt"`).

---

## 6. AS-5 — traces to line elements

`LineRecognition.Recognize`, called by `ArtworkRecognition.Recognize` after the parts; the result carries a
`LineRecognitionResult` (`RecognitionResult.Lines`): the elements and the nodes between them. Files: `LineRecognition.cs`
(the orchestration and the report), `LineSegmentation.cs` (one trace → its elements), `LineJunctions.cs`,
`LineTypeChoice.cs` (D13 and the coplanar option, and `LineBinder` — the stackup asked whether a component binds),
`LineElement.cs` (the output records).

### 6.1 The reader (R-as5-1)

**One** trace review per recognition, shared with AS-4's part reading (`RecognitionResult.ReviewRuns` holds it at one).
It is run with a scope of one region round all the copper in scope: a chain a selector chooses is a trace from
`SelectedMinAspect` (2) widths rather than `MinAspect` (4), so a short line between two parts is a line and not a pad.
For recognition only (`TraceImpedanceOptions.KeepLongEndSections`, set by `ReviewOf` and nowhere else) the review's end
trim takes an end piece off a chain as its land, when it is wider than the piece it joins, only below the same 2 widths:
a 1.7 mm by 5.6 mm section from a pad on a 1.1 mm line is a line, not the line's land. A review, scoped or not, trims at 4.
Its target, tolerance and findings are not read.

What recognition needed and the review did not return was **added to its output records**, computed where the review
already has the geometry:

- `TraceStation.H` — the height the classifier's coplanar threshold is measured against;
- `TraceRun.Corners` (`TraceCorner`) — every join between consecutive pieces that turns by `CornerMinDeg` (15°) or more:
  the point the centre lines meet, the signed turn, the width, and the chamfer measured by walking out along the outward
  bisector to the copper's edge (a chamfer edge pairs with nothing, so the pieces of a mitred and a square corner are the
  same);
- `TraceLayerResult.Junctions` (`TraceJunction`) with `TraceRun.StartJunction` / `EndJunction` — each junction's member
  traces and its centre, the least-squares meeting point of every member piece's centre line, read in the chaining step
  while every member piece is still known.

### 6.2 Line type (R-as5-2, D13)

Each cut is read again from what the review returns — the references either side, both side gaps and H — so the user's
choice applies without touching the review:

| References | Reading |
|---|---|
| below only | MLIN; CPWG under **Auto** when both gaps ≤ `CoplanarGapFactor`·H (default 3, the review's own), under **Gcpw** when both gaps are measured at all; never under **Microstrip** |
| both sides | SLIN; TLIN ("stripline with coplanar ground") when a side gap is within the factor |
| above only | TLIN ("… (reference above)") |
| none | TLIN ("coplanar waveguide (no ground plane)") |

An MLIN, CPWG or SLIN the stackup cannot bind — `SubstrateResolver.ResolveElectrical` with the trace's conductor and its
**measured** reference, or `ResolveStripline` — is a TLIN naming the resolver's reason. A cut with no Z0 is *unsolved*.
A run of one reading shorter than max(2·W, 0.5 mm) takes its neighbours' (both, where they agree; else the longer). A
CPWG whose mean gaps differ by more than 1.5× is a TLIN. Every MLIN and CPWG records the measured gaps, length-weighted,
whatever was chosen (D12, D19).

### 6.3 Segmentation (R-as5-3 … R-as5-5)

One walk along each trace's pieces and joins:

- **Straight.** Pieces of one reading and one width class (`MergeToleranceMicrons`) are one line. A piece shorter than
  max(W, 100 µm) between straight joins takes its longer neighbour's width and reading; its length stays in the line.
- **A straight join** is a jog (one line), a **step** (the lines abut at the join's middle), or — between two microstrip
  pieces of different width — an **MTAPER** (W1, W2, L = the join) when the join is at least 2·W of its narrow end. The
  piece finder sees no piece in a linear ramp (its edges are not parallel), so a taper *is* a join.
- **A corner** in a microstrip region is an **MBEND**: `Angle` the unsigned turn, `Miter` the nearest of the model's
  options (none, 0.5·W, `MicrostripDiscontinuities.MiterCutLength(W, h)`) to the measured chamfer. A 90° corner made as
  two 45° corners is two MBENDs with the diagonal between them an MLIN. Anywhere else a corner is centre-line length.
- **Reference planes.** An MBEND owns its corner square: each adjoining line stops W/2 short of the corner point (so the
  two lines and W make the centre line). A trace's end at a **pad** stops at the pad's edge — where the review's end trim
  already put it. At a **via** it stops at the land's edge. At a **junction** see §6.4. A line left with no length is
  absorbed: its two nodes become one, and it is counted.
- **Taps.** A part's terminal, a via or a port the trace runs *through* — within a width of its centre line, more than a
  width from either end — splits the line there: a shunt part's pad standing on a line is a node in it, not at its end.
  A tap within half a micron of an existing split is at that split, whichever side of it rounding put it, so the walk's
  direction (which a mirror image reverses) never moves it; and a second tap at a point another has taken is that tap's
  node — two shunt parts either side of a line at one point are one node.

### 6.4 Junctions (R-as5-4, R-as5-5)

Three arms that are all MLIN are an **MTEE**: the two most nearly collinear arms are the through line, the branch is on
the right of travel from pin 1 to pin 2 (the model's through along +X, branch along −Y). Four are an **MCROSS**, pins
counter-clockwise from the arm nearest +X. The arm lengths follow the models' own reference planes, which are where their
artwork stops: `MTeePCell` admits a through arm no shorter than W3/2 and a branch no shorter than max(W1, W2)/2, and
`MicrostripTeeModel`'s star network carries "no reference-plane shift beyond what the star itself represents";
`MCrossPCell`'s arms stop half the crossing arms' width out. So each arm runs from the trace's end to the centre, less
half the crossing arm's width. More than four arms, or any arm not microstrip, is a **plain node**: every arm runs to the
centre and the report says so.

A junction of **two** arms is how the review reads a sliver between two collinear pieces (both of its ends meet both
neighbours); the sliver is dropped as a pad-length chain, so the two arms of one type and width class are merged back into
one line and the sliver is counted as absorbed. **Unless a part's terminal, a via or a port lies between the two arms'
ends** (within the wider arm's width of the line joining them): that is a shunt part's pad standing on a line and wider
than it, which the review also reads as two arms meeting. The arms then stay two lines and meet at the terminal, as a
tap on one piece does (§6.3) — the round trip's shunt 0402 on a 450 µm line found it (AS-9).

### 6.5 Ends and nodes (R-as5-6)

Nodes are named for what they are: `R1.2` (a part's terminal, AS-4's row and terminal), `V4.1` (a kept via's end on one
island), `P1` (a port), `J2` / `J2.3` (a plain junction / an MTEE or MCROSS arm), `T5_2` (between two elements of trace
T5), `O1` (an open end). A trace's end attaches to the nearest part terminal, port or via on its island within
2·W + 1 mm (a via within its land's radius plus a width); else to another loose end within two widths on the island
(a gentle bend the review did not join); else it is an **open end** — an open line, no open-end model, listed. **A pad is
one node**: an island's copper that no trace piece covers (each piece's strip taken away, a micron wider each side) is pad
copper, and in each connected region of it every line end the review names a `"pad"` end (half a width past it) and every
part terminal, via end and port is joined by an ideal short, reported as `PadsJoinTheirMembers` with the pad's size. A
junction's centre is no pad: its arms end on it as `"junction"`. Pad copper is the island less what the review
returned, so a trace it did not read (an arc) is in it too: each region is cut to its cores — eroded by half of
`PadWidthStep` times the widest line ending in it (else the widest trace on the layer), so that all copper no wider than
the lines by the review's pad-to-trace step goes, and grown back by as much — and a member on no core, or on another
core than the first, is still joined but reported as `CopperJoinedWithoutLine` (with no core left at all, a member
farther than a line end reaches from the first is). Modelling
a pad as a wide line or a junction is a later step. A part terminal, via or port no line reaches joins the nearest line end on its island, or the island's other
attachments.
`LineRecognitionResult.NodeOf(name)` gives the node any of those names ended up as.

### 6.6 Parameters (R-as5-7)

SI, named as the components name them; the substrate is never written (injected at extraction from `SignalLayer` and the
measured `GroundReference` each element carries):

- MLIN `W`, `L`; MBEND `W`, `Angle`, `Miter`; MTEE `W1`–`W3`; MCROSS `W1`–`W4`; MTAPER `W1`, `W2`, `L`;
- CPWG `W`, `L`, `G` (the mean of the two mean gaps); SLIN `W`, `L`;
- TLIN `Z`, `Eeff` (length-weighted over the solved cuts), `L`, `F` (D15's top frequency: the option, else the `.cem`'s
  stop where it is a plain number, else 6 GHz), `Ad` = (π·f/c₀)·(Er/√Eeff)·((Eeff−1)/(Er−1))·tanδ and
  `Ac` = Rs/(Z0·W), both in dB/m. **`Ac` is an estimate**: the strip's own surface resistance over its width, with no
  current crowding, no ground-return loss and no roughness. Er, tanδ and σ are the stackup's between the trace and its
  reference. Every element also carries its drawn `Width`, so a TLIN swapped to another type (D19) keeps W.
- A segment with no solved cut is a TLIN at the nearest solved segment's Z and εeff on the same trace (else the layer's),
  marked unsolved.

### 6.7 Coupled pairs and the report (R-as5-8, R-as5-9)

Two straight segments on one layer, parallel within 2°, edges within 3 mean widths, overlapping for more than λ/20 at the
top frequency (λ from their mean εeff) are one *coupled, modelled uncoupled* finding naming both. Nothing in the circuit
changes. The report adds: elements by type; TLIN fallbacks by reason; segments read as CPWG and as MLIN under the coplanar
reading in force (and how many MLINs had ground both sides); junctions over four arms; bends, junctions and steps outside
microstrip (one finding); open ends; slivers and lines absorbed; coupled pairs; unsolved segments.

### 6.7½ The circuit against the copper

Recognition checks its own result before the emit drops stray lines (`CircuitCopperCheck`): every part terminal, port
and kept via end on one signal island is joined to the others there by copper, so the circuit must join them too through
its lines, junctions and VIAs — direct current only, no part crossed. An island the circuit splits is
`CircuitSplitsCopper` and a node joining two islands `CircuitJoinsAcrossCopper`, each naming both places; agreement is
one `CircuitAgreesWithCopper` line, said every time with what was checked, unless a join across copper no line models
stands in the report. A part terminal, port or via end no line
reached that joins a line end farther than a line end reaches (§6.5) is `CopperJoinedWithoutLine`: copper between them
that no line models — a trace the review did not hand over — shorted. Tests treat all three as errors.

### 6.8 Gates

`LineSegmentationTests`, `LineJunctionTests`, `LineTypeChoiceTests`, `CircuitCopperCheckTests`, `LineRecognitionCountersTests` (a 40 mm line solves
one cut; the review runs once) and `LineRecognitionFieldTests` (`FixtureFact`; the field board's `expected.json` gains
`"lines": { "MLIN": [min, max], … }`).

---

## 7. AS-6 — the circuit, the drawing, the target cell

`ArtworkRecognition.Run(input, target, options)` is the one entry point the GUI command and the CLI verb call: recognise,
emit, draw, write. Files: `RecognitionEmit.cs` (the circuit), `RecognitionProvenance.cs` (the component fields and the
provenance block), `RecognitionTarget.cs` (the target and `Run`); in `src/Design/Schematic`, `SchematicTechnology.cs`
(the per-schematic technology) and `ArtworkProvenance.cs`.

### 7.1 The per-schematic technology (R-as6-1, D20)

A `.csch` may carry `TechRef` (relative to the `.csch`, the `.clay`'s spelling) and a `.cnl` a `technology "<path>"`
statement (relative to the `.cnl`; top level, once). `SchematicTechnology.Resolve` takes the document's own reference
first and the workspace default only without one — the order a layout uses — and every stackup injection is handed what
it returns: `NetExtractor` (MLIN family, CPWG, SLIN, VIA/VIAGND, MMIC passives), `CnlTechnologyBinding`, and the
parameter editor's readouts. A reference that does not resolve is an error naming the path, never a fall back to the
workspace default: `check` reports `check.schematic.technology-unresolved`, and a netlist naming a missing file is refused
by the binding. A schematic with a `TechRef` writes the statement into the text it extracts to, relative to the base that text is read
back against (its workspace root, else its own folder — where Simulate writes `netlist.cnl`; Simulate restates it when the
file lands elsewhere);
`NetlistSchematic.Build` turns a netlist's statement back into the drawing's `TechRef`. `TechnologyDivergenceReport`
compares the layout's technology with the schematic's RESOLVED one, and for a schematic with a stackup-bound line compares
the two STACKUPS as well as the layer tables (`StackupComparison`); `explain` reports a schematic's and a netlist's
technology as a `technology` walk step. Without a reference, everything is byte-identical to before.

### 7.2 The circuit (R-as6-2)

`RecognitionEmit.Build` turns the result into a `TestBench`:

- **Instances**: ports as AS-3 named them, parts by designator, lines `TL…`, bends `B…`, tees `TEE…`, crosses `X…`, tapers
  `TP…`, CPWG `CP…`, SLIN `SL…`, TLIN fallbacks `TF…`, vias `V…` / `VG…` — numbered in the order a breadth-first walk
  from port 1 meets them. A designator that collides with a series name keeps it and the series skips it.
- **Nets**: a port's net is its name in lower case, ground `0`, every other `n<k>` in the walk's order. A `Short` part and
  a via under `vias=ground` merge their nodes (with ground, for the via); `Open` and `Ignore` parts are nothing.
- **Lines** carry W, L (in mm), the bend's angle and miter, a TLIN's Z, Eeff, F and losses, and `SignalLayer` /
  `GroundReference` — never a substrate value. **Vias** carry Drill, Pad and `FromLayer` / `ToLayer` (the conductor of
  each end's island within the via's span); `GroundLayer` is left to the injection's default.
- **Parts**: R/L/C with the value, or with the variable's name; an SnP two-port with its file (port 2 on ground for a
  shunt part). Each variable is a global at its transparent start with a `tune` entry: tune on, optimize off, ×0.1…×10,
  or 0…10 Ω / 0…1 nH for the zero-ish series R and L.
- **SP1**: the `.cem`'s sweep, else 100 MHz – 6 GHz, 201 points; `--start/--stop/--npts` through `RecognitionEmitOptions`.
- **The technology statement** names the artwork's `.ctech`.

`RecognitionCircuit.CnlText(dir)` is the `.cnl` with the technology and every model file relative to where it is going.

### 7.3 The drawing (R-as6-3)

`NetlistSchematic.Build(lib, tb, dir, hints)` draws it. With hints (each instance's artwork point, y up), the hangers of
one main-line net are ordered by their projection on the artwork's travel there (read from the path elements either side),
and a two-pin hanger whose copper lies on the left of that travel is drawn above the line. Hints that agree with the
default drawing change no byte of it; no hints is the default code path.

#### 7.3.1 "As on the board" (brief-dfr22-2)

**Arrange** (`RecognitionEmitOptions.Arrange`, `--arrange board|tidy`) picks the drawing; the circuit is the same.
**Tidy** is the drawing above, unchanged byte for byte. **As on the board**, the default — and the default of Update
Schematic from Layout's artwork fill, which only ever draws into an EMPTY schematic, so no part already drawn moves —
builds an `ExactPlacement` (`NetlistSchematic.BoardPlacement`) from `RecognitionCircuit.BoardHints` (each instance's
anchor centre, y up, and its direction: a part's `PadAxisDeg`, a line's trace direction at its centre) and draws it with
`PlaceExact`, the schematic-picture path. No third placement algorithm:

- **Scale.** One per drawing: the least that puts every pair of glyphs (labels included, `Full` boxes, centred on their
  board points) two grid cells apart along x or y, capped so the sheet stays under 40,000 units — a via beside its pad
  would otherwise scale it without limit. Framed on the hinted instances, so a selection-scoped recognition is placed
  relative to the selection. y is flipped.
- **Empty board is squeezed out** (designer round 24). One close pair sets that scale, so every long line and bare
  stretch of board grew with it and the sheet ran to the cap. Each axis is then compacted in board order: an item goes
  as far back as it can — no further back than the one before it, four grid cells clear of every earlier item it would
  overlap across the axis, and, for an earlier item it shares a net with, at least two cells and as much of their scaled
  board offset as six cells allows (a part beside a junction stays beside it, not level with what it hangs from). The
  first x and y passes part only pairs the board has apart along that axis (unless twice as far apart the other way), so
  a part straight below another stays below it; a last y pass against the compacted x parts every pair that would
  overlap, which keeps every pair of glyphs apart, so only straightening can still send a part to the fallback.
  Measured on the field board the report came from (a 247-part region and the 202-part whole board): area 15 % smaller
  and unchanged; nets joined by label instead of wired 27 → 7 and 29 → 6; parts sent to the fallback 15 → 0 and 9 → 0.
  The width is set by rows of parts whose value labels sit side by side. Two cells for both constants halves the area,
  but joins more nets by label and loops the bias chain's wire round its decoupling capacitor
  (`ArtworkBoardArrangementTests`).
- **Orientation.** A two-pin instance with a direction is turned to it to the nearest quarter turn; anything else (a
  port, a via, a tee, a bend) is turned — and mirrored where that scores better — so each signal pin points at the board
  position of what its net joins.
- **Straightening.** A scaled board leaves pins a cell or two off, and the router reaches a pin only along its lead:
  pins facing each other on one net are lined up (each instance moved once per axis, in netlist order), and a pin whose
  lead points at a pin of its net less than two cells away (four, when the two face each other off to one side and
  meet by a jog) has its instance moved back.
- **Wiring** is `SchematicAutoRouter`'s (`ExactPlacement.AutoRoute`): a board's copper runs are not schematic wires, so
  the picture path's `WireExact`, which wires along given routes, has nothing to follow. **Grounds** sit on their pins as
  in Tidy. **Collisions** fall back per instance to the automatic rows below the drawing (`PlacementFallbacks`), and the
  report counts them (`BoardPlacementFallbacks`).

**The wire loop (R-dfr22-2-3) came from `Place`, not the router.** A chain hanging off the main line — line, line, port
to ground — is placed by the "everything else" rows, whose cost made a shift sideways cheaper than a row further down,
so the second link landed BESIDE the first with its upper pin barely below the pin it joins; the router, which must
reach that pin along its lead, then came at it from above round a U. Two changes in `Place`: a series link that
continues a chain is placed before what merely hangs from it, and a two-pin part joining a net that so far ends on one
pin goes straight below it (a sideways shift costs ten rows); an upright part drawn beside its net's pins keeps its
upper pin at least four cells below that net's top terminal. Tidy drawings without such a chain are unchanged.

**Labels (R-dfr22-2-4).** Every label `Build` writes is anchored to a wire of its own net. Where no stretch of the net's
wiring is clear of everything else, the label used to go on a terminal unanchored, and after an edit floated off; it now
goes on the middle of the net's longest run. A net with no wire (unrouted) keeps its label on its terminal. Extraction
reads an anchored label's net from its OWNER WIRE (`NetExtractor.FindLabelNetKey`), not from where it is drawn: a label
drawn a little off its wire used to name whatever its point landed on, or nothing. That is also what lets a label be
dragged off its wire (R-dfr22-2-5, `SchematicViewModel.NetLabelDrag.cs`, `MoveNetLabelCommand`): dropped on a wire of
its own net it re-anchors there; on another net's wire it snaps back; clear of every wire it keeps its anchor and
takes the drop as its offset, at most 100 units (one connection-grid cell) from it.

### 7.4 The component fields and the provenance (R-as6-4, R-as6-5)

`EditableComponent.FromArtwork`, `ArtworkAnchor` (DBU points) and `ArtworkMeasured` (line Z0, Eeff, side gaps; a TLIN's
W; a part's `PadAxisDeg`, pad 1 toward pad 2) are persisted in the `.csch`, written only when set, and never reach the elaborator. They are laid on the drawing by
instance name after `Build`; the drawing's own ground symbols get none. The schematic's `ArtworkSource` block records the
source `.clay` (relative), the scope and its rings, the options, the parts CSV's SHA-256, the version and the time.

### 7.5 The target (R-as6-6) and the L5 commands (R-as6-7)

`NewCell(name)` creates the cell beside the artwork's with `CellCreate.Create` (schematic only; an existing name or one
`NameValidator` rejects is a refusal). `ArtworkCell` writes the artwork cell's primary schematic, offered only while it
has no schematic view or its primary drawing is empty (`RecognitionTarget.IsEmptyDrawing` — written over in place, under
its own name; designer feedback round 21). `ArtworkRecognition.Draw` is the same recognise-emit-draw with no write, for
Update Schematic from Layout on artwork with nothing placed: it puts the drawing into the open, empty schematic as one
undoable edit (`FillEmptySchematicCommand`). A placed instance with a `SchematicId` takes its kind and value from the
linked component in the cell's schematic (evidence `instance`); an expression that does not read as a number is a note. `Replace(cell)` rewrites a primary schematic that carries `ArtworkSource`, after
`WorkspaceCheckpoints.BeforeWrite` (a `SavePoint`, intent "Create Schematic from Artwork"); one without it is refused —
*"<cell>'s schematic was not created from artwork; choose a new cell"*. The `.clay` is never written.

Update Layout from Schematic skips a `FromArtwork` component before resolution (no add, update, orphan report or ground)
and says *"N components model existing artwork — not generated"* — into the provenance layout only. Into any other
layout it generates them, and a new one is placed from its anchor: a straight line (MLIN, CPWG, SLIN, MTAPER) with pin 1
on its first anchor point, turned toward the second; anything else centred on its anchor, a part turned so its pin 1 →
pin 2 axis is `PadAxisDeg`. Anchors are scaled from the source layout's DBU. Components with no anchor go in the usual
rows, below the board.

A net is named from the copper when its island carries a stated name (a shape's `Net`): every node on the island takes
it, the first in walk order bare and the rest `_2`, `_3`, …, ahead of a port's own `p<n>`. Update Schematic from Layout leaves a placement whose
`SchematicId` or `RefDes` names a `FromArtwork` component alone and says *"C6 is modelled from artwork — unchanged"*.

### 7.6 Gates

`SchematicTechRefTests`, `TechnologyDivergenceReportTests`, `RecognitionEmitTests` (the board checks with 0 errors and
simulates), `NetlistSchematicHintTests`, `RecognitionTargetTests` (the `.clay` byte-identical throughout; the checkpoint
a counter), `FromArtworkSyncTests` — all in `tests/Ui.Tests/Recognition/`.

## 8. AS-8 — Design ▸ Create ▸ Schematic from Artwork…

The GUI command is a shell round the same three calls the verb makes, behind `IArtworkRecognitionRunner`
(`src/Ui/Recognition/`): `RecognitionInput.FromFile` on open, `ArtworkRecognition.Circuit` for every preview, and
`ArtworkRecognition.Run` on Create. The interface exists only so a test can record what the dialog hands across.

### 8.1 The dialog's state is the CLI's options (R-as8-2)

Each control fills a field the verb fills from a flag — `RecognitionOptions` (vias, coplanar reading and factor,
`GroundAt` from the layout pick, `TopFrequencyHz` from a stated stop), `RecognitionScope` (the selection's outline as
polygons, or whole), the target, and the sweep. The sweep's composition moved out of `src/Cli/Recognize.cs` into
`RecognitionSweep` so both surfaces state a field-by-field override of the same basis; a field left at the basis is
unstated, exactly as an absent flag is. A placement file's origin need is read from the FILE (read with no origin), so
the three-way choice stays on screen once made; nothing is pre-selected and recognition waits for it.

### 8.2 Table edits are a CSV overlay (R-as8-2, R-as8-3)

`RecognitionInput.PartsCsvText` carries the dialog's held edits as the parts CSV they would be; the recognition lays it
over the board's table with `PartsTableCsv.Read` — the reader `--parts` uses — so a re-run after an option change keeps
every edit without the dialog owning any table logic. Only edited rows are written, and on them the untouched cells are
written as the table has them (an empty Value would clear a value — the CSV's own rule). The provenance hashes the text.
The Value cell's red border is `PartsTableCsv.TryReadValue`, the CSV reader's own value rule.

### 8.3 Cross-probe (R-as8-3, R-as8-5)

`ArtworkCrossProbe` resolves; the layout editor's `ArtworkProbe` overlay draws, through the DRC/LVS finding-marker
routine. A part is marked as `RailPartMarks` marks one (pads plus the placement body where one is stated); a line's
anchor is drawn as a ring that runs out and back along the centre line. Show in Artwork resolves the provenance's
`Layout` against the schematic's folder. Messages gained expandable rows (`IMessageSink.PostItems`, default: the text
alone), whose items act on a double-click.

### 8.3.1 Create's progress, its Cancel, and the preview it reuses (brief-dfr22-2 R-7, R-8)

Create runs under a `RunControl` like the preview: the same progress bar (`IsBusy`) and stage text, a Cancel on the
bar's right-click and on the dialog's Cancel (which stops a Create in flight rather than closing), and nothing written
when cancelled — `ArtworkRecognition.Run` checks the token after drawing and before the write. When the input Create
would recognise equals the last completed preview's (`RecognitionInput` value equality; `RecognitionScope` compares its
rings point for point) and the `.clay` has not been written since, Create hands that `RecognitionResult` over
(`RecognitionRunOptions.Recognised`) and only emits and draws. The emit's and drawing's report lines are `Set`, not
added, so a reused result does not say them twice.

### 8.4 The MoM refusal (R-as8-6)

`EmRunService.MeshCeilingRefusal` appends `WithoutEmPointer` to the unknown-ceiling refusal only.

### 8.5 Gates

`CreateSchematicFromArtworkViewModelTests`, `ArtworkCrossProbeTests` (`tests/Ui.Tests/Recognition/`) and
`EmRunServiceTests` (`tests/Ui.Tests/Em/`).

## 9. AS-10 — designators from the silkscreen

`src/Design/Layout/Recognition/Silkscreen/`. Evidence source 4 (D10): on a board with no placed footprints and no
placement or BOM file, the designator printed beside a part is the only statement of what it is. Values are never read
from silkscreen.

### 9.1 Strokes, not pictures (R-as10-1, R-as10-2)

A Gerber legend's text is drawn by a stroke font, and the import keeps every pen stroke as a path with a centre line.
Those centre lines are the input — nothing is rasterised, nothing trained, nothing native. `SilkscreenText.Layers`
finds the silkscreen layers (the `F.SilkS`/`B.SilkS` alias, a silkscreen or legend purpose, else a name saying silk,
legend or overlay); each layer is read on its own and a bottom layer prefers a mirrored reading. A filled shape on a
silkscreen layer is not read and is counted (`SilkscreenFilledNotRead`) — text drawn as filled outlines needs a
different reader.

**Typed text is read as itself.** A drawing exported as DXF carries its designators as `TEXT`/`MTEXT`, which the import
keeps as labels with the exact characters. `TypedSilkscreenEvidence` reads each label on a silkscreen layer whose text
(MTEXT's codes and toggles and TEXT's `%%u`/`%%o`/`%%k` stripped, upper-cased) passes the designator rule of §9.2 as a
line with no glyphs, its box from the anchor, the height, 0.6 of a height per character and the rotation, and as the same
`PartClaim`s a picture's legend makes through `IPartEvidenceSource` — read by `PartReading` itself, not through that seam,
because it is given first and reported whatever else is found. Then the stroked reading names what they leave. A label only names a
part another rung found; with no glyphs, correcting it teaches nothing (`SilkscreenText.Lesson`).

`StrokeGlyphs`: strokes whose centre lines touch (within 0.3 of the pen width) or cross are one **unit**. A unit is
TALL in a family — reading along x (0°/180°) or along y (90°/270°) — when its extent across the line is at least
1/1.6 of its extent along it. Tall units of one height (±35 %) overlapping 70 % across the line, at most 0.6 cap heights
apart along it, are one **line**; a short unit (`-`, `_`) in the line's band joins it. Each line is offered whole and
split at its widest gap (recursively), so two designators printed side by side read as two. A line is read in the four
frames of its family (rotation and mirror); the frame with the least total distance wins. Candidate lines are taken
best first — a designator, then more glyphs, then a closer fit — one line per unit. Strokes no line takes are
outlines, logos and marks (`SilkscreenStrokesExcluded`); a line three quarters of whose glyphs are bars (`I`, `1`, `-`,
`_`) is a row of ticks or a hatch unless it is a designator with a known prefix.

### 9.2 Matching (R-as10-3)

A glyph is normalised to its LINE — cap height 1, the baseline at 0, its own box centred on x = 0 — so a `-` keeps its
height and an `_` its place. Its centre lines are resampled to 48 points and compared with each template by a
symmetric Chamfer distance in its **modified-Hausdorff** form: the mean distance from each one's samples to the other's
segments, the LARGER of the two directions. The larger, not the average: on a field board a taught 3 lay wholly on the
same font's 8, and the average halved the 8's missing side until every 8 read as the 3.

Templates (`GlyphTemplates`): Hershey Roman Simplex's `A–Z 0–9 - _ +` (`resources/silkscreen-glyphs/`, public domain,
its licence and required acknowledgements in `LICENSE-hershey.txt` beside it), plus three variants assembled from
Hershey strokes because CAD plotter fonts commonly draw them so — a flagless `1`, Hershey's serif-face `1` with its base
serif (without it, that `1` read upside down as a `T`), and a round-topped `3` — plus whatever the user taught.

A line is a designator when it reads `^[A-Z]{1,3}[0-9]{1,4}$` with each glyph matched WITHIN ITS CLASS (a letter
position against letters, a digit against digits — so `O`/`0` and `I`/`1` are told apart by position), within 0.1 cap
heights and clear of its in-class runner-up by a margin of 0.2 (1 − best/second). Measured on field boards, three
further rules keep words from reading as designators: a letter is never read where a digit fits better (`22` as Z2);
a digit is read where a letter fits better only after a known prefix (`ST` as S7; `CIO` → C10 is fine); the first digit
is not 0 (`TO`, `NO` as T0, N0). A known prefix also decides between splits (`C`+`12` over `CI`+`2`). `I` and a flagless
`1` are drawn alike and are never each other's runner-up.

### 9.3 Association (R-as10-4)

`RefdesAssociation.Assign`: distance from the label's centre to each unnamed part's BODY box (the union of its two pad
outlines); a pair is in reach within three body diagonals (and a claim's own `ReachDbu` where positive). Claims and
parts are clustered through reachable pairs and each cluster is solved by the Hungarian method (the O(n³) potentials
form, rectangular), so a label printed nearer a neighbour's body than its own still goes to its own when the
neighbour's label is there to take the neighbour. A label in reach of no part, or losing the assignment, is listed
(`SilkscreenRefdesNotAssociated`). A designator a stronger source already gave is no claim.

A line that would be a designator but for an uncertain glyph names nothing, but is attached to the part it stands
beside (the same assignment), with a note — so correcting that part's generated designator can teach the glyph.

### 9.4 Teaching (R-as10-5)

The dialog's Refdes cell is editable on a row whose designator was read off the silkscreen or generated; the edit is a
CSV overlay like any other (§5.3's rename rule, the dialog writes `X`/`Y`). `SilkscreenText.Lesson(row)`: each glyph of
the row's silkscreen line that the corrected designator says is another character — only when the counts agree.
**Learn These Glyphs** stores them through `GlyphTemplates.Learn` in `<UserStateDirectory>/silkscreen-glyphs/taught.json`
(never the workspace), and every later recognition — GUI, CLI, MCP — matches against `GlyphTemplates.ForUser()`.

**A taught template counts only within `TaughtReach` (0.01 cap heights).** It is the board's own font, plotted by the
same instructions every time: on the field board the same character matched it at 0.000 while the font's other
characters stood 0.017 and more away. Taken at the ordinary 0.1, one taught chamfered 9 read the chamfered 8s and 3s
as 9s, because a glyph resembles its own font's other characters more than another font's right one.

### 9.5 The report (R-as10-6)

`SilkscreenTextRead` (lines read, how many designators), `PartsFromSilkscreen` (named), `SilkscreenRefdesNotAssociated`,
`SilkscreenGlyphsUncertain` (glyphs of designator-shaped lines under a known prefix that failed only the margin),
`SilkscreenStrokesExcluded`, `SilkscreenFilledNotRead`, and `SilkscreenLabelsRead` (designators typed as labels, and how
many other labels there are). The strokes are read only when some part is still unnamed after placed footprints, the
placement file and the typed labels; the typed labels are reported whatever was found, so a board with no part says that
its designators were there.

### 9.6 Gates

`StrokeGlyphTests`, `RefdesAssociationTests`, `TypedSilkscreenTests`, `SilkscreenFieldTests` (`FixtureFact`; expected.json gains
`"silkscreen": [{refdes, x, y}]` in mm and `"silkscreenFraction"`) in `tests/Ui.Tests/Recognition/Silkscreen/`, and
`PartsTableCsvTests.ARowAtAGuessedPartsPlaceRenamesItButNeverABoardDesignator`.

## 10. AS-11 — Swap Line Type

`LineTypeSwap.Swap(component, toType, context)` in `src/Design/Schematic` is the whole operation: it returns a NEW
component (the input is untouched) or a refusal. `LineTypeSwapContext` carries the schematic's technology, F (the
bench's top S-parameter frequency, `LineTypeSwap.BenchTopFrequencyHz` — the same F the parameter editor's Z0
readout uses, which now calls it) and a gap the user was asked for.

### 10.1 Where each parameter comes from (R-as11-2)

In order: the source's own row of that name, verbatim → what an earlier swap set aside → what was measured off the
artwork (`ArtworkMeasured` `GapLeft`/`GapRight` mean for G, a recognised TLIN's `W`) → derived. Derived means: a CPWG
gap asked for (headless: a refusal naming G, `NeedsGap`); a TLIN's Z, Eeff, Ac, Ad computed from the source line at
F; a width synthesised from a TLIN's Z at F; an electrical-form TLIN's E converted to L at its own F with its εeff
(the new line's at W when it has none). The line model is `LineCalculator.ModelAt` — the elaborated instance the line
calculator reports — on the substrate the extraction injects, so the swap, the calculator and a run read one model.
Synthesis is `PlanarLineSynthesis.SynthesizeWidth` over that model's Z0 at F.

A SLIN the stackup cannot carry is `PlanarLineSubstrateInjection`'s own refusal, before anything is built.

### 10.2 Nothing is lost (R-as11-3)

A row the target has no place for is set aside WHOLE in `EditableComponent.SwapRemembered` (expression, unit, shown),
persisted as `SwapRemembered` in the `.csch`. It is set aside only when the swap straight back would not derive the
same text anyway — decided by running that inverse swap — so a swap and its inverse are the identity and memory does
not pile up. A TLIN's own rows are tied to the W/L/G rows beside them (`TLIN@W` …); a swap back after the geometry
was edited computes instead of restoring a stale Z, and says so.

### 10.3 The GUI (R-as11-4, R-as11-5)

Context menu ▸ **Swap Line Type** (`SchematicView`), the parameter editor's type combo
(`ParameterEditorViewModel.LineSwap.cs`) — both call `SchematicViewModel.SwapLineTypeAsync`. All swaps land as one
`SwapLineTypeCommand`, which rewrites each component IN PLACE (type, rows, label offsets, what was set aside), so its
Id, its place in the component list, the selection and an open parameter editor still point at it. A refused line is
a Messages warning; MBEND/MTEE/MCROSS/MTAPER sharing a net with a line swapped to anything but MLIN get one note each
(`LineTypeSwap.DiscontinuityNotes`).

### 10.4 Gates

`LineTypeSwapTests`, `SwapLineTypeCommandTests` in `tests/Ui.Tests/Schematic/`.

## 11. AS-1 and AS-2 — the line models the recognition needed

A trace that is neither microstrip nor fits a model was the reason for both. The models themselves are documented with
the other planar lines, not here: `docs/design/planar-line-models.md` and `docs/design/linear-engine.md`.

- **CPWG** (`src/Core/Devices/CoplanarLineModel.cs`, `Planar/GroundedCoplanar.cs`): grounded coplanar waveguide, bound
  to the stackup as MLIN is (`W`, `L`, `G`). The quasi-static form takes the branch with the larger air capacitance of
  the coplanar and microstrip readings, so it tends to MLIN exactly as G grows; references (a field solve and
  independently-coded formulas) are in `testdata/planar-lines/`.
- **SLIN** (`StriplineModel.cs`, `Planar/Stripline.cs`): symmetric or offset stripline between two ground-reference
  planes, its heights from the stackup (`SubstrateResolver.ResolveStripline`). A conductor without a reference plane on
  each side is a refusal naming the missing plane — a stripline with one plane is a microstrip, and answering it as one
  would be a silent MLIN.
- **TLIN, physical form** (AS-2): `L` + `Eeff` (θ = 2π·f·L·√Eeff/c₀) with `Ac`/`Ad` in dB/m at `F`, beside the angle
  form. It is the general fallback (D13): every line recognition cannot model is a TLIN at the solved cross-section's Z0
  and εeff. A mixture of the two forms is a `ParameterRefusalException`, reported with the instance path.

## 12. AS-7 — the command line and the MCP tool

`circuitrf recognize <layout|cell|workspace>` (`src/Cli/Recognize.cs`) and the MCP `recognize` tool are a command line
onto `ArtworkRecognition` and nothing else — the verb's full description is `docs/design/cli.md` §26. Read-only by
default (the report and the parts CSV on stdout); `-o` writes the `.cnl`, `--into new:<name>|artwork [--replace]` the
schematic, `--parts-out` / `--parts` the parts CSV round trip; `--placement` / `--bom` the companions, read with their
own refusals; `--region` in SI units. `ArtworkRecognition.Circuit` is recognise + emit with no write. `explain` gained
the `artwork source` walk step. Its gate is byte identity with the in-process `ArtworkRecognition.Run`, and a source scan
holds `src/Cli` to naming nothing of the recognition but its entry point, options, result and `PartsTableCsv`
(`RecognizeCliVerbTests`).

## 13. AS-9 — the acceptance gate, the field boards, the example

### 13.1 The round trip (R-as9-1, R-as9-2)

`tests/Ui.Tests/Recognition/ArtworkRoundTripTests.cs`, its design and steps in `ArtworkRoundTripBoards.cs`. A schematic
whose answer is known — two ports, an MLIN path with a 90° MBEND and an MTEE to an open stub, a series 0402 C, a shunt
0402 L standing on the line to a VIAGND pad, a series 0603 R — goes out exactly as a fabricator would receive it and
comes back recognised, every step through the function the GUI's own command calls:

1. drawn from its netlist (`NetlistSchematic.Build`), footprints on the three parts;
2. **Update Layout from Schematic** (`SchematicToLayoutGenerator.Run`), the parts arranged as a designer would — each
   line ending at its part's pad edge, at half a width from a bend's corner or a tee's centre, or at a shunt part's pad
   centre — a ground plane drawn, and Update Layout again (the VIAGND placed beside its pad);
3. `GerberExport` (Gerber + Excellon) and `BoardCompanions` (placement + BOM);
4. `GerberImportEntry.RunFolder` into a fresh workspace, the stackup the import guessed replaced by the fabricator's;
5. `circuitrf recognize` as a process, `--placement --bom --into new:…`, then again without the companions.

**What is compared, and the tolerances.** Topology: element counts by type; each part's designator, kind, value and
connection; and every element touching the same elements, and ground, as its counterpart. Geometry: every line's W
within 1 µm (the Gerber is written at 1 nm and read back exactly), every L within max(1 %, 10 µm) (a length runs
between reference planes the recognition places, from copper kept to the nanometre). Response, both simulated
0.1–3 GHz: |ΔS| ≤ 0.02 on every entry at every point, small entries judged by absolute error, and S21's phase within
±3°. The two circuits share every model; what can still differ is what the geometry tolerance admits (10 µm of line
is under 0.1° at 3 GHz) and what neither models — the pads (the lines meet the parts at the pad edges, where the
designer drew them to) and a step for a pad wider than its line (D18). **Measured: identical** — every length to the
micrometre and |ΔS| = 0. Without the companions the parts come back as three parts of unknown kind, each value a
variable with its tune entry, the topology and geometry unchanged.

R-as9-2 draws the two lines no generator draws (D18) — a GCPW line with side grounds and two via fences on the
two-layer technology, and a stripline on the inner layer of a four-layer one — through the same export and import.
The GCPW line is `CPWG` with W, G and L in tolerance and all 38 fence vias dropped as stitching; under
`--coplanar microstrip` it is `MLIN` carrying both measured gaps. The stripline is `SLIN` with W and L, its H1/H2 from
the technology (800 / 200 µm).

**Found by the round trip, and what it means for anyone repeating it.**

- *A shunt part's pad wider than its line* was read as a two-arm junction and merged away, the part attached to the
  line's end instead of its middle. Fixed in `LineRecognition` (§6.4).
- *A bend's and a tee's generated artwork are longer than their models.* `MBendPCell` and `MTeePCell` draw arms of
  2.5·W (`PCellGeometryHelpers.StubLengthFactor`) that the lumped models do not carry — the models' reference planes
  are half a width from the corner or centre. A layout made by placing lines pin-to-pin on those arms is up to 2·W
  longer per end than its schematic. The gate's designer ends each line at the reference plane, running over the arm;
  a layout that does not is still recognised faithfully — the recognition reads the copper, which is the point.
- *A Gerber set carries no stackup*: the import writes its own technology with an FR-4 guess and says so. The
  designer types in the fabricator's on the Stackup tab; without it every line is computed on the wrong substrate.
- *`BoardCompanions` lists every placed instance*, lines included, in the placement and BOM; recognition reports the
  rows it could not place and carries on.
- *A VIAGND whose stackup states no plating wall* puts a "default used" note into `NetExtractor.Extract`'s
  `Conflicts`, and `PdnLayoutNets.Of` treats any conflict as a naming conflict — so the BOM the companions write
  carries **no values** and the pads no nets. The gate's technology states the wall thickness; the classification is
  outside this series and is left for the owner.

### 13.2 The field boards (R-as9-3, D17)

`testdata/artwork-boards/<board>/`, git-ignored folder by folder (`/testdata/artwork-boards/*/`); the folder's
`README.md` is committed and is the schema. Because the README makes the folder exist on every clone, every field test
is gated on a board being there — `testdata/artwork-boards/*/expected.json`, which `FixturePaths` resolves with one
`*` segment — not on the folder. `ArtworkFieldBoardTests` is a `FixtureTheory`, one case per board: it recognises with
no refusal, its `.cnl` passes `check` with no error, and its counts are within `expected.json`'s (ports, parts,
series/shunt exact; line types as ranges). The earlier phases' field tests read the same file.

### 13.3 The example (R-as9-4)

`examples/Artwork to Schematic/`: the round trip's board, authored in circuitRF and written to `fab/` as Gerber +
Excellon + placement + BOM, then imported as the cell **Board** — a real flattened board, as a user's would be. The
placement and BOM list only the soldered parts, and L1's BOM row is deleted so the dialog shows a variable to tune.
**Board design**, the schematic it was drawn from, ships beside it to compare against: tuned to 8.2 nH, L1 brings the
recognised schematic's S11 at 2 GHz from −7.7 dB to −15.9 dB against the design's −15.8 dB. The folder is rebuilt by
`tests/Ui.Tests/Examples/ArtworkToSchematicExampleAuthoring.cs` with `CRF_AUTHOR_ARTWORK=1`.

## 14. Known limits

- **Not modelled** (D18): coupled lines (reported, modelled uncoupled), step, gap and open-end discontinuities, case
  parasitics and the pads themselves, devices with more than two pins (cut out, D9), Klopfenstein and other curved
  tapers (MTAPER or stepped lines). CPWG, SLIN and TLIN regions get no bend or step models (D14). A junction of
  grounded coplanar arms (or coplanar and microstrip mixed) is the microstrip `MTEE`/`MCROSS` with the arms' widths —
  the nearest model, reported as such (brief-dfr22-2 R-dfr22-2-6, the owner's choice); a coplanar tee model with its
  own externally generated reference would be a series of its own. SLIN and TLIN junctions stay plain nodes.
- **The stackup is the user's.** A recognition is only as right as the technology it reads; an imported Gerber set's
  is a guess until corrected.
- **Ground is a heuristic** (D6) with an override; a split ground is a separate pour and a signal island.
- **Placement origins are never guessed**: a placement file that does not state its origin waits for the user.
- **Silkscreen** reads stroked text only; designators, never values.
- **Good to about 2 GHz on an ordinary board** (§1): beyond that the unmodelled pads, steps and coupling grow, and an
  EM run of the region that matters is the next step — a scope selection makes that region a circuit of its own first.

