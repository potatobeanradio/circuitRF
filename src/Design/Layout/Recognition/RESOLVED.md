# `src/Design/Layout/Recognition/` — findings

Create Schematic from Artwork — `docs/design/artwork-to-schematic.md`, `docs/sonnet-briefs/brief-artsch-0-overview.md`.

---

## AS-3 — the board graph

### The trace review's pour rule cannot be read literally at the piece level

The brief asks that "small island" mean *not a pour by the trace review's own pour rule*, so there is one definition.
That rule (`TraceImpedanceAnalysis.FindLayer`) answers a different question — **which islands hold traces worth
reviewing** — and two of its clauses give the wrong answer for ground:

- **An island with no trace strips in it is never a pour** (`pieceArea <= 0 → continue`). A solid plane has no pair of
  facing edges within the widest-trace limit, so it has no strips, and the literal rule would read the board's plane
  as *not* ground copper.
- **An island carrying four vias or more is a pour.** A shunt part's ground pad with six vias is exactly that, so the
  literal rule would make the pad ground copper, its vias stitching, and the VIAGNDs the overview exists to keep would
  vanish — the brief's own gate (six vias → four VIAGND, two counted) cannot pass under it.

So recognition keeps the rule's **numbers and its pad rule** and reads them per piece (`BoardCopper`, `BoardGround`):
ground copper is the chosen piece, or copper **wider than any trace** (it survives erosion by half of
`TraceImpedanceAnalysis.WidestTraceDbu` — the review's own `WidthRange`, exposed for this, not re-derived), or copper
carrying `PourViaCount` vias that is **not pad-sized** by the review's `MinAspect` rule. The via count still catches a
narrow ground strip with a row of vias; it no longer catches a pad. The width limit and both constants are the
review's, so the two cannot drift apart.

### The partition records only barrels that met two conductors

`ConnectivityPartition.Barrels` keeps a barrel that touched two pieces or more — what the union-find needed. A via
landing on one pad and an undrawn reference plane touched **one** drawn conductor, so it is not there. `BoardCopper`
locates those through the partition's own point lookup (`CopperPieces.IndexAt` on each spanned conductor), so the
pad still knows its via; that lookup is not a second connectivity model — it decides no join.
`CopperPieces` now carries `Barrels` (read through a new `DrcConnectivity.Partition`, the same cached answer every
other form reads), so the partition is computed once: `RecognitionCountersTests` holds it at one extraction for a
200-via board.

### An any-layer point lookup answers with the plane

`CopperPieces.IndexAt(x, y, null)` returns the lowest-numbered piece under the point, and under a line on Top that is
often the Bottom plane. `BoardGraph.IslandAt` without a layer therefore searches each layer an island is on, and
`BoardCopper.ConductorAt` searches the stated layer first and then the conductors in stackup order.

### A scope is a second partition, and ground still comes from the first

Clipping the copper and partitioning it again is what makes "copper outside the scope is not read" true — an island
that leaves the selection and re-enters is two islands, as it must be. Ground is never re-read from the clipped copper:
each clipped piece takes the class of the whole-board piece under one of its own vertices, so a pour the selection cuts
in two stays one ground. A crossing is found where the **original** copper continues in a thin band just outside the
boundary, so a clipped edge is a cut and a piece that merely stops inside is not.

### A connector designator needs a digit after the letter

R-as3-6 says a connector's designator *starts* with J, P or X. Read literally, `PS1` (a power supply) and `PWR1` are
connectors. `PortDiscovery.IsConnectorRefdes` requires the letter to be followed by a digit.

---

## AS-4 — parts and the parts table

### The land-pattern references are generated on a technology of their own

`ChipLandPatternGenerator` refuses a technology whose top conductor does not sit directly on a dielectric — a board
with a via row between Top and its core in the stackup list reads as "not a laminate surface" and gets no lands. So the
references are generated once on a minimal two-row technology (front copper on a core, a soldermask by purpose) inside
`LandPatternMatch.References`, and the test boards' own land helper strips the via row for the same reason. The board
under recognition is never asked to be generator-friendly.

### A 30 % scaling is not a guaranteed miss

The case table is dense. An 0603 grown by 30 % lies within 20 % of the two-pad crystal's least-density land
(`XTAL3216@L`, RMS 0.12), an 0805 shrunk by 30 % fits `0603@L`, and an 0402 shrunk by 30 % fits `0201@M`. Those are
correct readings of geometry no table could tell apart, which is why the runner-up note exists. The gate scales an 0805
up, which fits nothing.

### "In a row" had to be strict

The first rule (a like pad on the pair's line within 0.5–1.5 pitches) rejected two chip parts placed end to end at
about one pitch, which is an ordinary layout. A package row is pads of one size at one gap, so the rule is now: same
size within 10 %, on the line, and the gap to the nearer end equal to the pair's own gap within 10 %.

### `PartModelResolution` is taken

railRF's `PartModelResolution` record (one part number in one `.crlib`) already lives in `CircuitRF.Design.RailRf`, and
recognition calls it. The brief's file name is kept; the class is `PartModelResolver`, so a file importing both
namespaces compiles.

### `NF` is a not-fitted marker only in capitals

`BomTablePaste`'s do-not-populate words do not include `NF`, and adding it there would make a pasted table's unit
column (`nF`) read as not fitted — its comparison lower-cases. The bill-of-materials check accepts the exact capital
`NF` beside the shared recogniser instead.

### A placement row lands by the body box railRF draws

A Gerber-only board has no pads by designator, so `PdnAttachments` finds none for a placement row; `RailPartMarks.For`
still returns the body box (the row's point ± `PadReachDbu`), and that box is what decides which land pattern the row
names — the same box the railRF window marks the part with.

---

## AS-5 — traces to line elements

### The review drops a short line between two parts unless something selects it

A chain shorter than `MinAspect` (4) widths is kept by the trace review only where a selector chooses it — a wide line
cut into sections by series parts is two to four widths long between them. Recognition runs the review with one region
round all the copper, so every chain is chosen and `SelectedMinAspect` (2) applies. AS-4's part reading had its own lazy
run with no scope; it now shares this one, which is what holds the review at one run per recognition.

### A taper is a gap, not a piece

The piece finder pairs edges anti-parallel within 2°; a linear ramp's edges are tilted by atan((W2−W1)/2L) — 26.6° for
the gate's 1 mm ramp from 0.5 to 1.5 mm — so the ramp has no piece and the chain joins straight across it. The join is
therefore what an MTAPER is read from. The gate's ramp is exactly 2·W of its narrow end, so the brief's "more than 2·W"
is read as "at least 2·W" (with 1 µm of slack); anything shorter is a step.

### A wide end piece is trimmed as a land

The review's end trim takes a chain's last piece off as a pad when it is 1.2× wider than its neighbour and shorter than
four of its own widths. A taper's wide side therefore has to run on for at least four widths, or it is read as a pad and
the trace ends at the ramp.

### A sliver between two collinear pieces reads as a junction

Both ends of a piece shorter than its neighbours' join distance (1.25 widths) meet both neighbours, so each end has two
candidates and all three are junction ends; the sliver is then dropped as a chain shorter than two widths. Its length is
not lost — each neighbour runs to the junction's centre — but it is two lines. Recognition merges a two-arm junction of one
type and width class back into one line, and counts the sliver as absorbed.

### A pad standing on a line does not end the trace

A shunt part whose pad sits on a line (AS-4's board: C2 on R1's output line) leaves the line's edges unbroken, so the trace
runs straight through the pad and the part's terminal is in the middle of it. Attaching that terminal to the nearest
trace END put the part at the open end of the line, 6 mm away. Recognition now splits a line at any terminal, via or
port within a width of its centre line and more than a width from either end ("taps").

### A chamfer is invisible in the pieces

A mitred corner's 45° edge has no anti-parallel partner, and for any chamfer up to a full width the outer edge still runs
past the inner corner, so the pieces of a mitred and a square corner are identical. The chamfer is measured in the review
(`TraceCorner.CutLeg`) by walking out from the corner point along the outward bisector to the copper's edge: a leg m brings
that edge m·cos(θ/2) nearer than the sharp corner's (W/2)/cos(θ/2).

### The square corner's pieces already end at the reference planes

For a right-angle bend the inner edges end W/2 short of the corner point on each side, and for a T the through pieces end
at the branch's edges and the branch at the through line's — exactly the MBEND's corner square and the MTEE's arm planes.
Recognition still measures every end against the corner point or the junction centre explicitly, because at any other
angle the pieces end somewhere else (a 45° bend's inner corner is 0.21·W from the corner point, not 0.5·W).

## AS-6 — emit, the target cell, the per-schematic technology (brief-artsch-6, 2026-10-08)

### The extracted `.cnl` names its technology relative to the WORKSPACE ROOT, not to the `.csch`

A schematic's `TechRef` is relative to the `.csch`, but the text a schematic extracts to is read back against
`SchematicCircuit.ReferenceBaseOf` — the schematic's workspace root, else its own folder — which is also where Simulate
writes `netlist.cnl` and what every other relative reference in that text (a Touchstone file) resolves against. So the
`technology` statement is relative to that base (`SchematicTechnology.NetlistRef`); a `.csch`-relative spelling would
resolve against the wrong folder whenever the schematic is not at the root. Simulate's `WriteNetlist` restates it for
where the file actually lands (`SchematicTechnology.Rebase`) — another workspace's root, or the scratch folder with no
workspace open. A first version wrote the absolute path; the owner requires a relative one. Recognition's own `.cnl`
(`RecognitionCircuit.CnlText`) is relative to where it is going.

### A layer name is written bare when it can be

`SignalLayer="Top"` reads correctly in a `.cnl` (the binding unquotes it), but the drawing carries the expression through
as text and a schematic's extractor does NOT unquote — so a drawn MLIN would have asked for a layer called `"Top"`, quotes
and all. The emit writes the name bare and quotes only a name with a space or a quote in it.

### A TechRef that does not resolve never falls back to the workspace default

`SchematicTechnology` answers no technology and an error, and the extraction lists it among its conflicts; `check`
promotes it to `check.schematic.technology-unresolved` (an error) and drops the duplicate warning. For a `.cnl` the
binding refuses the netlist outright, which `check` reports as unreadable. Falling back would compute every recognised
line on the workspace default's substrate, which is the failure D20 exists to remove.

### The divergence report compares stackups too (`StackupComparison`)

`TechnologyDivergenceReport` asked only `ExternalWorkspaceGate.CompareTechnologies`, which compares the two technologies'
LAYER TABLES — the placement gate's question, what a layout view means, and it leaves the stackup out on purpose. Two
technologies with the same layers and different substrates (1.6 mm of FR-4 against 0.5 mm of εr 3.5, AS-6's own gate)
compared equal, so a schematic and its layout on those two said nothing. Found writing `TechnologyDivergenceReportTests`;
the owner ruled the stackup must be compared. `StackupComparison.Difference` (`src/Design/Layout`) compares the conductor
and dielectric entries in order — kind, thickness, εr, tanδ, μr, conductivity, ground designation — and the two
boundaries, not names or drawing layers. The report asks it only for a schematic holding a stackup-bound line, since a
footprint resolves layers alone; the placement gate is unchanged.

### The checkpoint before a headless write moved below the firewall

`Optimize.CheckpointBefore` (`opt --save-preset`, the `yield` writes) is now `WorkspaceCheckpoints.BeforeWrite` in
`src/Design/Revision`, with the CLI's helper delegating to it, because recognition's replace needs the same floor and the
GUI command will call it from `src/Ui`. A replace takes it as a `SavePoint` with the intent "Create Schematic from
Artwork"; a workspace with no history records nothing and the replace goes ahead.

## AS-7 — the `recognize` verb and the MCP tool

### A layer name with a space was drawn into the schematic WITH its .cnl quotes

`RecognitionEmit.AddLayer` quotes a layer name holding a space (`SignalLayer="Top Copper (1 oz)"`) because the `.cnl`
line would otherwise split there, and `NetlistSchematic.Build` copied every override expression verbatim — so the drawn
`.csch` stored `"Top Copper (1 oz)"` quotes and all. A schematic stores layer names BARE (the extraction reads them raw
and quotes them as it writes the `.cnl`), so the substrate binding looked for a conductor named with the quotes, warned,
and bound the default layer. On the shipped LVS example that was 17 warnings from `check` on a freshly recognised
schematic; the `.cnl` written by `-o` was clean. Invisible to every AS-3…AS-6 gate because the synthetic technology's
layers are `Top`/`Bottom`. Fixed in `NetlistSchematic.Build` (it unquotes `SignalLayer`, `GroundReference` and the via
layer parameters), which also fixes `netlist --to-schematic` on any `.cnl` with such a name; gate
`NetlistToSchematicTests.AQuotedLayerName_IsStoredBare`.

### `ArtworkRecognition.Circuit` — recognise and emit, write nothing

The verb's `-o` path and its read-only default need the circuit without a target; `Run` always writes. `Circuit` is
`Run`'s first half (the emit-omissions finding included) and `Run` now calls the same private `Emit`, so the `.cnl` the
two paths write is one function's output. `RecognitionTarget.NewCellDir` is where a new cell goes, shared by `Run` and the
verb's `--replace` question rather than worked out twice.

## AS-8 — the GUI command

### The dialog's edits travel as CSV text, not as a second table model

The dialog re-runs recognition on every option change and rebuilds its rows from the result, so an edit stored on a row
would be lost. It is held instead as the override a parts CSV would carry and handed over as
`RecognitionInput.PartsCsvText`, laid over by the same `PartsTableCsv.Read` the CLI's `--parts` uses. One trap: on an
edited row every unedited cell must be written as the table already has it, because an EMPTY Value cell is the CSV's
"clear the value" — writing only the edited column would silently clear every other edited row's value.

### A placement file's origin need comes from the file, read with no origin

Reading with the chosen origin reports `OriginEvidence.Chosen`, which reads as "no origin needed" — the choice would
disappear the moment it was made. The need is decided from a read with no origin, then the file is re-read with the
choice.

### The sweep composition moved below the firewall

`RecognitionSweep` (`Parse`, `Basis`, `Compose`) is what `--start/--stop/--npts` did inside `src/Cli/Recognize.cs`;
the dialog's Frequency fields compose the same way, so a field left at the basis is unstated on both surfaces. The verb's
source-scan allow list names the two types.

### No `EmRunServiceTests` existed

The brief named it as existing; the run-level refusal tests live in `PortClearanceRefusalTests` and
`EmCeilingRefusalTests` (the latter tests the mesher, not the run). The new class drives `EmRunService.Run` on the
committed `testdata/portcal` fixtures — `separated-pair` meshed past the ceiling (refused before any fill, ~0.3 s) and
`offset-pair`'s port-clearance refusal as the "other" case.

## AS-10 — designators from the silkscreen

Measured on a field Gerber board (a chamfered plotter font, 1,820 silkscreen strokes, ~0.7 s to read). Design in
`docs/design/artwork-to-schematic.md` §9.

### The averaged Chamfer distance let a subset glyph win

A 3 lies wholly on the same font's 8, so the 3→8 direction is 0 and the average halves the 8's missing left side. With
a taught 3 every 8 on the board read as 3. The distance is now the LARGER of the two one-way means (modified Hausdorff).
It also lifted the built-in reading: the round-topped 3s read with no teaching at all.

### A taught glyph must match near-exactly, or it reads the whole font

A board's own font plotted the same character identically (0.000 to the taught glyph) while its other characters sat
at 0.017–0.04 — nearer than the Hershey template of the right character, because style dominates the distance. One
taught 9 at the ordinary 0.1 read every chamfered 8 and 3 as 9. `GlyphTemplates.TaughtReach` = 0.01.

### Words read as designators until three rules held them

`TO`/`NO` upside down → T0/N0, `ST` → S7, `22` → Z2, `RD` → R0. Fixed by refusing a leading zero, never reading a
letter where a digit fits better, reading a digit where a letter fits better only after a known prefix, and leaving
word-letter prefixes (`S`, `Z`, `E`, `H`, `M`, `W`) out of `KnownPrefixes`. Rows of ticks and hatching read as
`IIII…` and are dropped when three quarters bars, unless they are a known-prefix designator (`C11`).

### CAD stroke fonts differ from Hershey in exactly the digits that matter

A flagless 1 (read as `I`, so `C12` came out `CI2`), a serifed 1 (read upside down as `T`, so `R1` came out `T5` at
180°), a round-topped 3. All three are variants assembled from Hershey strokes (`hershey-variants.txt`, same licence),
not copied from any CAD font. `I` and the flagless 1 are identical templates — `StrokeGlyphs.Alike` keeps them from
being each other's runner-up, and position in the designator tells them apart.

### The `Uncertain` count is for designator-shaped lines only

Counting every low-margin glyph listed `PURPOSE`, `ONLY` and every `O`/`0`. It is now the glyphs that failed only the
margin in a reading that is otherwise a designator under a known prefix. Such a line is attached (not named) to the
unnamed part beside it so the user can correct that part and learn the glyph.

### The parts CSV renames by position

The parts table was keyed by designator and the designator was not editable, so a correction had nowhere to go. A row
whose designator the board lacks, at exactly the X/Y of a silkscreen-read or generated part, renames it; the dialog
holds edits under `PartRow.BoardRefdes` (the designator the board gave) and writes X/Y. The rename treats the row's
old kind text and old default variable as no edit, or a GUI CSV (which writes every column of an edited row) would
put them back.

### AS-4 pairs adjacent vertical caps' pads sideways on that board

Two 0805-ish caps side by side, vertical, came out as two "parts" each made of one pad from each cap (centres between
the caps). Not an AS-10 defect — the silkscreen reading then names those phantom parts by the reach rule — but worth
knowing when a field board names a part oddly. Only 4 two-pad parts were found on that board against ~25 designators.

## AS-9 — the round trip, the field boards, the example (brief-artsch-9, 2026-10-08)

### A shunt part's pad wider than its line was merged away

The round trip's L1 (0402, pad ~0.6 mm across) stands on a 450 µm line. The trace review reads that pad as a JUNCTION
of two arms (the line either side), and `LineRecognition` merges a two-arm junction back into one line as a sliver —
so the line came out 4.5 mm whole and L1 attached to its end at C1 instead of its middle. The tap rule in
`LineSegmentation` never saw it: the terminal lies in no piece. A terminal (part, via or port) between the two arms'
ends, within the wider arm's width of the line joining them, now stops the merge: the arms stay two lines, each run to
the terminal (`TapBetween`). The first attempt put the check in `LineSegmentation`'s straight-join walk, which is the
wrong place — the review had already split the trace there — and changed nothing.

### Generated bends and tees are longer than their models

`MBendPCell`/`MTeePCell` draw 2.5·W arms the lumped models do not carry (their reference planes are W/2 from the corner
or centre). Lines placed pin-to-pin on those arms make a layout up to 2·W longer per end than its schematic, and the
recognition, reading the copper, is then "wrong" by exactly that. The round trip ends each line at the reference plane,
over the arm. Not changed: it is the generator's artwork, and recognising what is drawn is correct.

### A defaulted via plating empties the companions' BOM

A VIAGND on a stackup with no via `WallThicknessDbu` adds "A default is used … Plating = 25 µm" to
`NetExtractor.Extract`'s `Conflicts`; `PdnLayoutNets.Of` treats ANY conflict as a naming conflict and drops the whole
schematic, so `BoardCompanions` writes a BOM with no values and pads with no nets. The round trip's technology states the
wall. Left open — the fix is in what counts as a conflict, outside this series.

### The README makes the field-board folder exist

Committing `testdata/artwork-boards/README.md` means the folder is on every clone, so a `FixtureFact` gated on the
folder no longer skips — the four earlier field tests would have failed `Assert.NotEmpty` on a fresh clone. They are
gated on `testdata/artwork-boards/*/expected.json` now (`FieldBoards.Gate`; `FixturePaths` resolves one `*` segment).

## Designer report, round 15 — two field boards (2026-10-09)

### A bill of materials had to be a railRF one

`BomFile` (railRF's reader) refuses a header with no part-number, quantity or description column, so a spreadsheet
headed Component,Type,…,Value,Package was refused outright, and so was circuitRF's own netlist of the drawn schematic.
`RecognitionBom` is now what the BOM… button and `recognize --bom` read: a `.csch` or a netlist (by extension, or by a
first statement shaped `Type:Name …`, since the designer's was a `.txt`) as the circuit it is; else `BomFile`; else, on a
refusal, `BomTablePaste` — which reads each cell by what it looks like, so a unit in the next column works. A file that
is not UTF-8 is read as Latin-1: Windows-1252's `µ` (0xB5) otherwise decodes to U+FFFD and `0.1`,`µF` loses its unit.

### The designator prefix outranked the bill of materials

The BOM was consulted for the kind only when nothing else gave one, and `FB` → L always did, so a bead the BOM listed
as `R 0.01 Ohm` stayed an L with its value refused as the wrong dimension. Order is now: placed part, then a kind the
BOM states, then the prefix; overriding the prefix leaves a note. `ReadTypeWord` reads a single letter when it is the
WHOLE cell — circuitRF's own BOM writes `C`/`L`/`R` there. A description that is a bead word takes its kind from the
value's dimension.

### The case was read and never written

`RecognitionEmit` emitted no `Footprint`, so the recognised schematic's parts had none, the BOM export had none, and
Update Layout from Schematic had nothing to place. Every part with a case now carries `smt:<case>@N`.

### Lines reaching nothing

Traces from the pins of an unmodelled part (the board's QFN) came out as line elements whose every node was their own:
five floating elements in the drawn schematic. A group of lines and vias joined through non-ground nodes that holds no
port and no part (nothing with a fixed name) is dropped before the walk, and reported as `StrayLines` with anchors.

### A large pad on a narrow line did not split it

FB2's pad is wider than the 1.2 mm supply rail, so the review split the rail there and `TapBetween` was to keep the
two arms apart at FB2's terminal — but a terminal is the PAD's centre, 1.67 mm off the rail, and the reach was one
width. The arms merged into one 22 mm line and FB2 joined FB1's node. Both tap reaches (`TapBetween` and
`LineSegmentation`'s mid-piece tap) are now a line end's attach reach, `2·W + 1 mm`. A synthetic board did not
reproduce it — three geometries passed with and without the fix — so the gate is field board `board-c`'s new
`apart` key in `expected.json` (README updated), which fails without the change.

## Designer report, round 16 (2026-10-09)

### "No copper on any conductor" until the workspace was archived

Recognition reads the layout AND its technology from disk (`RecognitionInput.FromFile`, a fresh `TechnologyCache`), as
the CLI does. The canvas draws the Technology editor's LIVE technology, so a stackup built on an imported board's
`.ctech` and not yet saved was on screen and not on disk — the disk stackup had no conductors and the refusal was true
of the file. Archive Workspace prompts to save everything, which is why it "fixed" it. Create Schematic from Artwork now
asks first when the layout or the open technology it resolves is dirty (`SaveWhatRecognitionReads`: Save / Don't Save
reads disk / Cancel). Reading the live technology instead was not done: the dialog would then disagree with
`circuitrf recognize` on the same files.

### A net the layout names is the circuit's net name

Islands already carried `NetName` (from `CopperPieces`); the emitter numbered every net `n<k>` regardless. Every node
key (line node, port, part terminal, via end) now records its island, and after the union a net whose island is named
takes that name — first in walk order bare, the rest `_2`, `_3` — ahead of a port's `p<n>`. A root seeing two names
takes the ordinal-least, deterministically (a Short part can join two named islands).

### Synced back into a new layout: no coplanar lines, and a grid

Two separate causes. There was no `CPWG` (or `SLIN`) generator, so those components resolved "no layout view" and were
skipped; `CpwgPCell` draws the strip and a side ground of width `Wg` (default `W`) each side, and `SLIN` reuses
`MlinPCell` on its `SignalLayer`. And new instances were placed on the grid; a `FromArtwork` component now goes to its
anchor (lines by pin 1 and direction, parts centred). **Part orientation could not come from the schematic**: the
symbol's rotation composed with `PinAlignment` turned every 2-pad part 90° from the board on field board `board-c`, because the
drawing's rotation is a drawing decision. The emitter now records `PadAxisDeg` (pad 1 → pad 2) in `ArtworkMeasured`
and the generator turns the cell's pin 1 → pin 2 axis onto it. Checked by rendering `board-c` and its round trip in
one window: lines, part centres and orientations (including C5's diagonal) agree; the recognizer's lumped junctions
leave short gaps between a part pad and the next line start, which is the model, not placement.

### New stackup entries were air

`NewStackupLayer` gave every kind a 1 µm thickness and the model defaults (εr 1, tanδ 0, σ 0). A conductor or
dielectric now copies the last entry of its kind, or takes `SubstrateDefaults` (FR-4, 35 µm copper) when it is the
first — the same named generic board an artwork-only import completes a stackup with.

### A short line from a port to itself

A 0.5 mm stub at a port label came out as `CPWG P3 P3`. Not port merging (`PortMergeMicrons` merges PORTS only): the
end-attachment pass gave each end of a trace its nearest attachment within `2·W + 1 mm` independently, and with nothing
at the far end the nearest was the port at the near end, 0.5 mm back along the line itself. One attachment now never
takes both ends of a trace: the nearer end keeps it, the other takes its next nearest or is loose (an open end,
reported as one). On the designer's board that changed this one element and the open-end count, nothing else. The
end reach was already this size before round 15 widened the TAP reaches to match it.

## Designer report, round 17 (2026-10-10)

### A trace one rounding step short of its pad

The P3 stub, after the round-16 fix, read `CPWG L=0.5 mm n3 p3` with `n3` open and `L1.2` open beside it. The copper
LOOKS continuous: L1's pad, a taper, the 0.2 mm trace. It is not: the pad rectangle ends at x = 13 512.343 µm and the
taper polygon starts at 13 512.800 — a 0.375 mm (metric) pad centred on an inch grid against a vertex rounded onto the
file's 0.1 mil grid. 0.46 µm, so the partition made them two pieces, two islands, and an attachment must share the
line's island. The same board had a second one, 0.75 µm, between the 7.2 mm output line and the RFout launch, which
had left P2 disconnected from the amplifier without anyone noticing.

`LayerRegions.JoinHairlineGaps` closes gaps under 1 µm (grow by half, shrink back) in the ELECTRICAL reading only, and
returns the region unchanged — not an equivalent region — when it joins nothing, so no board drawn in the app moves a
vertex. Outer rings before and after are the join count (a close never separates). It is in `LayerRegions` because that
union is what the partition, railRF's mesh and LVS all start from; the trace review unions its own band copper and
calls the same function, or the joined island would still be two RUNS whose facing ends never meet (that was the
synthetic test's first failure: the far run took P1 as its open end's attachment). Reported as
`HairlineGapsJoined`. Not done as a recognition-only tolerance on attachment reach: LVS and DRC would then still call
the pad and the trace two nets.

Still not modelled: the ~0.4 mm taper between L1's pad and the 0.2 mm trace. The recognised CPWG is the 0.5 mm of
uniform trace; the taper is read as part of the land.

## Create Layout from Image, IM-3 (2026-10-10)

Two traps found while tracing a two-layer picture drawn with a translucent top layer (detail in
`docs/design/image-to-circuit.md` §6.4):

- **A colour junction leaves a blip on a straight edge.** Where a bottom layer's corner lands on a top layer's edge,
  that pixel mixes three or four colours, and the two-colour mix model gives it a wrong coverage. The top layer's edge
  came back with a 0.37 px excursion splitting it into two edges snapped a hair apart, so the rectangle was a
  seven-vertex polygon. Removing vertices near the line through their neighbours did nothing, because the neighbours
  were not on one line. `ImageTrace.Deblip` merges the two parallel snapped edges onto their common line instead.
- **Rounding each vertex to DBU breaks a 45° edge.** x and y rounded separately put the two ends of a 45° jog a DBU off
  45°. `ImageTrace.Quantise` rounds each edge's invariant once (y, x, or x ∓ y) and solves each vertex from its two
  edges.

The overlap test is compositing, not mixing: a colour on the segment between two layer colours is the wrong test,
because the translucent top layer's observed colour already carries the background (`P + β(Q − W)`).
