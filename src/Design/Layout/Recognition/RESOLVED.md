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

## Create Schematic from Image, IM-4 (2026-10-10)

The traced layout goes through the artwork recognition unchanged; four things a picture does not state had to be
answered on the way in, each recorded in `docs/design/image-to-circuit.md` §7:

- **A technology declares layers a picture never draws, and the recognition reads them from the technology.** The
  reference plane (answered as AS-3's undrawn reference), the solder mask — with a mask layer declared, AS-4 reads land
  patterns ONLY from mask openings, so a picture with no mask drawn found no part at all — and the board outline (with
  none, the copper's extent is the edge, and a line lying along it got an edge port in its middle). `PictureTechnology`
  hands the recognition a copy that states only what the picture draws, plus the traced frame as the outline.
- **A part standing on its line is visible only in the mask.** L1 of the AS-9 round-trip board has its first pad centred
  on the trace; in copper it is a pad inside a line. Read from copper alone — a picture, or the artwork without its mask
  — it is not found, and its ground via is a stray. Not fixed here (it is AS-4's copper reading); the IM-4 gate compares
  like with like.
- **One drill hole is below the colour clustering's noise floor.** IM-1's elbow stopped at two colours and the hole was
  folded into the copper; `ColourClusters` now adds a far, flat-carried colour of its own (§4.2 step 5).
- **A skeleton cuts sharp corners**, enough to split "C12" at the matcher's letter-gap limit; `RasterSilkscreenEvidence`
  restores them before matching (§7.3).


## Layout from a board photo with no corners picked (2026-10-11)

A bare two-layer board photographed under a warm lamp, scaled with Line impedance, came in with its laminate on the top
copper and its traces on the bottom, and a 50 Ω line 100 µm wide on 20 mil RO4350B (about 1.06 mm is right). Two causes:

- **The area rule is a drawing's rule.** Auto maps colours to conductors by area, top first; in a photo the largest
  colour is the laminate. Flat share does not tell such a photo from a drawing when it is cropped into a paper's figure
  (0.77: the paper is flat and the photo is smoothed), but exact-colour repetition does — the 256 most common colours
  cover 0.42 of the photo against ≥ 0.94 of every drawing in the corpus. A photograph is now clustered white-balanced
  (the laminate goes from chroma 24 to 2, the copper stays at 20–27) and its copper-hued colours map to the top copper.
- **The impedance scale took an unsolved width.** On a layer with no return conductor the line calculator has no model
  and its cross-section refuses, but the row still carries the width the search started from (`hGuess`, 100 µm), and
  `ImageScale.FromImpedance` took `CrossSection.WidthM` without looking at its `Z0`. It now refuses with the reason.

The dialog also opened every picture in Auto, even from Create Layout from Image, and Auto read this photo as a plot
after a long read. A dialog opened by a command that names its kind now starts there for every picture handed to it and
never classifies until Auto is chosen (`ImageKindResult.Stated`, `IsStated` — a forced kind with no reading behind it).

**A region dragged after the scale undid the scale.** The region clips each layer's coverage, and the impedance scale
measured the picked line on that clipped coverage — so a second region leaving the line out refused the scale and traced
nothing (the dialog looked as if it kept the first region), and a region whose edge crossed the line narrowed it.
`Prepared.WholeCoverage` gives the scale the picture's own coverage. Delete on the dialog's canvas now clears the region.

**A replace left an open tab on the old layout.** After Create replaced a cell's layout (or schematic), the window only
activated the tab already open on it, which held the model it had read. The written files now go through the reload a
history restore uses (`ReloadChangedDocuments`): the tab is closed, its edit session discarded, and it is reopened in
place.

## AS-12 — the netlist a Gerber set declares (2026-10-11)

Field board: a two-layer LNA's Gerbers with `%TO.P`/`%TO.N` on every pad, eighteen parts. Three things only the real
board showed:

- **One pin, several pads.** An edge-launch connector's four ground tabs all declare pin 2. Keying pads by (refdes, pin)
  merged them into one box whose centre sat on the signal pad, so the connector read as "bridged" and the leftover tabs
  became made-up land-pattern parts (`C_A1`… and a silkscreen-named `N1`). Shapes of one pin are one pad only where they
  touch; a through-hole pin's other-layer copies are dropped by taking the pin's topmost layer.
- **A ground pad joined by a trace and a via is not on ground copper.** C4's GND pad runs a short trace to its own via;
  that island is a signal island shorted to ground, not a `PadGround` one, so the net check called GND open. Ground
  copper and every island a via joins straight to ground are one node for the check.
- **An edge SMA gave two ports, and its shell tab a third.** The centre pin's connector port and the line's edge port
  were more than the 1 mm merge radius apart, and one ground tab sat on a ground-shorted island read as signal. A
  connector port now drops the edge port on its island, and neither a pad nor an edge on ground-tied copper is a port
  (that also removed `P6`, an edge port on a GND strip, which the board got with or without the attributes).
  **Ground-tied needs the shapes' own net names, not the partition's.** The first try used "joined to ground by a via"
  alone, then the island's partition net name — both are galvanic, so an unnamed RF line with a shorted stub read as
  ground and lost its edge ports (`PortDiscoveryTests.AnEdgeOnGroundTiedCopperIsNoPort` holds both halves).

## Update Schematic from Layout on imported artwork (designer feedback round 21, 2026-10-18)

Two field boards, both Gerber imports. On the first (copper and four port labels, no placed parts) Update Schematic
from Layout did nothing and said nothing; on the second (eight footprints placed over the imported pads) it updated
the eight parts and modelled none of the traces or vias between them.

- **Root cause, both:** the command walks `LayoutInstance`s only (R-L5-19, "no wiring"). The recognition that
  reads copper as lines lives behind Create ▸ Schematic from Artwork, which the user never reached for — the update
  was the obvious command. Running `recognize` on the first board gives exactly the two expected chains
  (P1→CPWG 7.96 mm→CPWG 0.86 mm→P2, and the mirror P3→P4).
- **A trap that compounded it:** the no-op update CREATES an empty `.csch` in the artwork cell, and from then on the
  artwork cell was no longer offered as a recognition target ("already has a schematic view"). An empty drawing now
  counts as none (`RecognitionTarget.IsEmptyDrawing`) and is written over under its own name.
- **Fix:** with nothing placed and an empty schematic, the update draws the recognised circuit into the open schematic
  as one undoable edit — `ArtworkRecognition.Draw` (Run's recognise-emit-draw, no write) + `FillEmptySchematicCommand`.
  A schematic with anything in it is never changed: splicing lines into a hand-drawn drawing would have to guess
  which wire each trace replaces, and the part on the far end (a chip modelled as an SnP) has no placement at all.
  There, the update reports the unmodelled copper and names Create ▸ Schematic from Artwork.
- **Placed parts were read with kind and value unknown** on the second board (every one a capacitor variable) —
  `LayoutPartKind.Of` is null by design on an instance the schematic owns ("the schematic knows"), and the reading
  never asked the schematic. `PartReading` now reads the cell's primary schematic (else the sibling) by `SchematicId`.
  The editor stores `70k` with unit `Ω`, which the bill-of-materials reader refuses with the space; the expression
  and unit are tried together, run together, then the expression alone.
- **Slow:** the second board's recognition takes ~54 s wall (285 s CPU) — 1,589 filled silkscreen shapes and 2,900
  copper shapes. Not addressed; it is why the update does not run a recognition just to list trace lengths.

## Shorts, unlisted footprints and generated lines (designer feedback round 22, 2026-10-20)

Three findings on the eval board and the stub-match test layout the designer sent.

- **A 0 Ω part vanished from the schematic.** `RecognitionEmit` handled `PartKind.Short` by joining the two nets and
  emitting nothing, so the part was not in the drawing and not in a later Update Layout either. It is now a 0 Ω
  resistor (`R=0 Ohm`) listed in `RecognitionCircuit.Shorted`, and `RecognitionProvenance.Apply` sets the drawn
  component's `Disable = Short` — so the schematic simulates it as the short it is, with no near-short warning. The
  `.cnl` written by `recognize -o` carries `R=0 Ohm`, which the resistor model stamps as `Gmax` with a warning;
  `.cnl` has no disable state to say more.
- **A land pattern the bill of materials does not list was modelled.** With a BOM given, a two-pad pattern with no
  BOM row and no placed-part kind is now `Open` ("read as not fitted"). A part whose kind came from a placed instance
  is left alone. Trap found while writing it: `PartEvidenceSource`'s default is `Instance`, so
  `GetValueOrDefault(PartField.Kind)` on a generated part read as a placed one.
- **Generated lines and tees were read as parts.** A layout made by Update Layout from Schematic places MLIN/MTEE
  cells with designators; `PlacedPadsOf` turned their pins into parts — the MLINs "bridged" by their own copper,
  the MTEE cut out with its stub. An instance whose cell's `PCellSnapshot.GeneratorId` is in
  `PCellRegistry.KnownGeneratorIds` (the built-in structures — footprints come from the land-pattern resolver and
  are not in that set) is now artwork only.
- **The stub layout's lines were 57× too long** — not recognition's fault: see the E-at-F entry in
  `src/Ui/RESOLVED.md`. Recognition then saw three lines that did not meet.

## The Artwork to Schematic example lost its unknown L1 to the not-fitted rule (2026-10-20)

Round 22's rule (a BOM lists what is fitted, so a two-pad pattern it has no row for is Open) silently removed L1 from
the shipped example: its BOM left L1 out on purpose to make it the unknown part, so `Board_model` came out with no L1,
no `L1_L` and an empty Tuning panel, and AN-02's tuning walkthrough could not be followed. Nothing failed but the AN-02
doc fixture. Owner decision: keep the rule and fix the example. L1 now has a BOM row with an EMPTY value
(`L1,,smt:0402,L`), which reads as fitted with an unknown value. `ArtworkToSchematicExampleAuthoring` writes it that
way. A side effect visible in the `recognize` table: every part's kind now comes from the BOM's description
(`kind=bom`), not its designator.

## Round holes in the copper as vias; via sizes rounded (designer feedback round 29, 2026-10-27)

A datasheet's test-board drawing draws its holes in the background's colour, so the layer map has no Drill row and
every via came out as a hole in the pour, with nothing on the drill layer. `ImageTrace.Build` now, when no drill
colour was read, takes each hole in a copper region that is round (`RoundHole`: bounds aspect ≤ 1.3, area ≥ 0.6 of
its box, so a drawing's rounded-square hole counts) and between 0.1 and 1 mm across as a via: the via is written on
the barrel layer, and the hole is left out of the polygon, so the copper is whole under it. A wider round hole (a
mounting hole's clearance, 2.6 mm on the field picture) stays an opening, and the report says so. On the field
picture: 71 vias, 2 openings. The hole-vias join `ImageTraceResult.Drills`, so the dialog's overlay draws them as
drills. Drill and pad are rounded in the technology's unit (`Tidy`): 1 mil (½ mil below 4 mil), 0.05 mm (0.01 mm below
0.2 mm), 1 µm otherwise. The measured hole includes the drawing's black outline ring, so the drill reads a little
large.

## Corrections by hand before Create (brief DFR29-1, 2026-10-27)

`ImageTraceInput.Corrections` holds `ImageCorrection` records in the traced picture's pixels: `Via`, `Hole` (not
plated), `NotVia`, `Copper`, `Clear`, `Outline`. The dialog's hand tools and `convert`'s `--via`/`--hole`/`--not-via`/
`--copper`/`--clear`/`--outline` build the same records, and a process-run test compares the two `.clay` files byte
for byte. `Build` applies them after the colour trace. Clear runs before Copper. Only the regions whose bounds meet a
correction go through Clipper (EvenOdd, so a region's holes and its islands survive). A layer no correction touches
is the list as read, so a trace with no corrections writes the same bytes as before. The last point correction on a
hole wins; an earlier one on the same hole is reported as answered by it. One that lands on nothing gets a one-sentence
refusal in `ImageTraceResult.Corrections`, and the report names it.

- **A non-plated hole keeps its copper opening**, as R-dfr29-1-1's table says. A pour with one hole left open and one
  made non-plated therefore has two holes, not the one gate 1's wording counts.
- **D2: a technology with no non-plated via layer is copied, never changed.** The copy gains `Non-plated hole`
  (`Plated = false`, spanning the first to the last conductor), and Write saves it as `<cell>/<cell>.ctech`, referenced
  by the layout. The copy is made only when a `Hole` correction exists, and it is written only when a hole was actually
  placed on it. Into an open layout, which cannot carry its own technology, this is a refusal.
- **A copied technology's material-library references must be rebased.** They are relative to the original `.ctech`.
  Saved into the cell folder, the copy named `generic-materials.cmat` beside itself, where there is none, and failed
  to load (`MaterialLibraryException`). Write rewrites each reference that resolves to a file into one relative to the
  copy (`MaterialLibraries.RelativeReference`).
- **The outline is a closed `PathShape`, one picture pixel wide.** That is the shape Em3d and Crop to Rectangle read
  along its centre line. Board corners… / `--outline corners` use `BoardPhotoReading.Rectangle()`, the rectangle in
  rectified pixels, through `ImageTrace.WithPhotoOutline`, which adds it only when no outline is drawn.
- **R-dfr29-1-4's stackup form was built and then withdrawn** at the owner's decision: the picture is read with the
  chosen technology as it is, and no flag states a stackup. The owner wants the picker to offer every existing
  technology instead, for its layers (see `src/Ui/RESOLVED.md`).

## Create Layout from Image, round 2 (brief DFR30-1, 2026-10-28)

**What got worse from 1.5.6 to 1.5.7 was a dark hole rim made into copper discs.** A datasheet board drawing draws each
hole white inside a dark rim, and the automatic map reads the rim's colour as a second copper layer. Each rim is then a
ring region, one round hole in it. Round 29's rule (a round hole in copper becomes a via, the copper written whole under
it) applied to the ring too, so 1.5.7 wrote a solid copper disc on the rim's layer under every via. The pour itself read
the same in both versions, region for region. The drill was also read at the rim's outer edge (the pour's hole), so on
a synthetic board with 0.3, 0.6 and 1.0 mm rimmed holes the drills came out one to three rounding steps large, and the
1.0 mm holes were both an opening in the pour and a via from the rim's own hole.

- **`Rings` (ImageTrace.cs) finds every region that is one ring round one round hole.** A ring whose outer edge fills a
  round hole of another copper layer, concentric and within 1.5 px or 15 % of its radius, is that hole's RIM. The via is
  made once, from the pour's hole, drilled on the rim's centre line (half-way between the rim's outer and inner radii,
  each the radius of its area), and the rim is consumed. Any other ring is the hole's PAD and becomes the via's pad, as a
  drill circle's ring already did. Neither is made whole. Measured on the synthetic board, the three sizes now read
  exactly at 20 µm a pixel and within one rounding step at 40 µm.
- **The ring's outer edge is tested with `RoundHole`, not `CircleOf`.** On a compressed picture at a coarse scale most
  rims fail `CircleOf`'s residual, and only a few of them were found.
- **The 1 mm via/opening cut-off compares the ROUNDED drill.** A 1.0 mm hole measured a few microns over was an
  opening. Stated as a designer states it, it is a 1 mm via.
- **A "round hole" wider than 10 mm is not a hole** (`MaxHoleMetres`). `RoundHole` accepts a rounded square, so the
  board inside a dark outline drawn round its edge passed as one and was drawn as a board-sized hole circle. It is now
  left as the copper's hole as read.

**A via's stated drill is a LENGTH (`ImageCorrection.DrillMetres`), not pixels.** The brief named `DrillPx`. A drill
chosen as 0.3 mm has to stay 0.3 mm when the scale is changed, while the point it sits on moves with the picture. The
CLI's `--via x,y,0.6mm` and the dialog's Drill picker build the same record. `DrillPx` had no other reader.

**`Clear` can name a conductor** (`--clear "…=<conductor>"`), and then clears that conductor alone. Remove ▸ Copper read
on writes one covering the whole picture, and Clear runs before Copper, so a fill drawn on that conductor afterwards is
all that is written there. The overlay draws no box for it: a box round the whole picture says nothing.

**A line drawing is said to be one (`ImageTraceResult.LineArt`, finding `ImageLineArt`); the reading is unchanged.**
The brief's test ("most of the copper colour in thin closed outlines, little of what they enclose read") failed on a
real picture. Anti-aliasing splits the black core of each drawn line from its grey edge into two colours, so no outline
closes and nothing is enclosed. The test now uses the strokes alone. The copper read covers under 20 % of its own
bounding box, and its mean width (twice its area over its edge length) is no more than 3 px, or 1 % of that box's
narrower side.

## Painting copper into a line drawing: Paint Fill / `--paint-fill` (2026-10-28)

Renamed from Fill / `--fill` by brief-dfr30-3 R-dfr30-3-5: to a PCB designer a fill is a copper pour.

`ImageCorrectionKind.PaintFill` (`ImageTraceFill.cs`) is a paint bucket. It floods the clicked pixel's colour cluster
(`ColourClusterSet.Label`, 4-connected) and adds the near half of the line drawn round that area. A line pixel within
`FillEdgePx` belongs to the area when the area is nearer it than the clicked colour beyond the line is. A pixel exactly
in the middle counts half, so the contour crosses it half way. When every tie went to the area, each drawn hole
shrank by about half a pixel, and a 0.4 mm drawn via read 15 mil instead of 16. The area is unioned onto the conductor
like a Copper fill, after the Clears. Its round holes are then read as any round hole in copper, which makes the vias.
A flood reaching all four picture edges is the background and is refused (`FillBackgroundRefusal`). It is a correction,
not an edit to the picture's pixels, so a pasted picture with no file is painted the same way, and the CLI's `--fill`
builds the same record. On a picture with one board, a few clicks give the pours and pads with the drawn vias as vias.
A click on a letter inside a pad paints the letter: the pad's white is what is meant.

**The copper corrections act before the scale (owner, 2026-10-28).** On a line drawing the scale has to come from
**Line impedance…** clicked on a line painted in first. The impedance scale read the colour under the click, which is
the white inside a drawn line and no layer. And Fill was applied only in `Build`, which needs a scale. So `Trace` now
runs `EditCopper` (Clear, Copper, Fill) straight after `Prepare`, and `Build` takes those outcomes. The line-drawing
test is still made on the layers as read. `Prepared.Painted` names the layers a correction changed. `FromImpedance`
measures a click inside painted copper on that copper with `PaintedWidth`, from the row and column runs through the
outline geometry. It was first measured on a whole-pixel raster, and that lost the half pixel at each edge: a line
drawn 12 px between its centre lines read 11 px, an 8 % scale error.

## A round hole must be round (brief-dfr30-3 R-dfr30-3-2, 2026-10-28)

A datasheet's board drawn as a closed outline is a line region whose hole is the whole board. `RoundHole` took any hole
up to 1.3:1 filling 60 % of its box, so a near-square board passed. The 10 mm cap that DFR30-1 added is a length, so it
held only at the true scale. At 8 µm a pixel a 17 mm datasheet board was 4 mm across, kept as an opening, and drawn by the
overlay as the circle of its area: a black circle round the board that nothing explained.

The tests are now of shape alone. Circularity (4π·area / perimeter²) and box fill were measured on traced holes:

| Shape | Circularity | Box fill |
|---|---|---|
| Circles 0.3–1.0 mm, 10–32.5 µm a pixel (solid, rimmed, drawn as lines) | 0.93–0.997 | 0.74–0.87 |
| A datasheet line drawing's hand-drawn vias, 10–14 px | 0.75–0.96 | 0.66–0.89 |
| Square, any size | 0.785–0.813 | 0.97–1.00 |
| Square rounded by 10 % of its side | 0.83–0.87 | 0.98–0.99 |
| Square rounded by 20 % of its side | 0.88–0.92 | 0.96–0.97 |
| Square rounded by 30 % of its side | 0.94–0.96 | 0.91–0.93 |

A hole is round when box fill ≤ 0.9 (`MaxHoleBoxFill`), with the 1.3:1 aspect and 0.6 fill floor it already had. No
circle measured above 0.89, and no rounded square up to 30 % below 0.91.

**Circularity was tried as well and withdrawn the same day.** The first version also required circularity ≥ 0.85. A
larger copy of the same datasheet drawing, pasted at 1936 px, traces its hand-drawn holes as ragged pixel staircases,
and a staircase's perimeter is long for its area: 156 holes 15–40 px across scored 0.21–0.85 (median 0.77). Every one
was rejected, so the automatic reading of that drawing found no vias at all, where the previous commit had found 99. A
staircase disc scores about 0.62, below a square's 0.79, so no circularity threshold separates the two on traced
outlines. Box fill does not depend on the perimeter and still keeps out the square board and a rounded pad. Without
circularity the same picture gives 80 vias. `AStaircaseCircle_IsARoundHole` holds it.

Independent of the scale, a hole wider than half the picture's shorter side is no drilled hole (`MaxHolePictureShare`).
It catches a round board drawn as an outline, which the shape tests admit. `MaxHoleMetres` (10 mm) is kept: a 12 mm
round clearance in a 60 mm board's picture is round and a fifth of the picture, and is still no drill.

An opening is drawn as its own outline (`ImageTracedHole.Outline`), never as the circle of its area. A drill circle
has no outline and is still drawn as its circle. Openings the report did not lead the user to expect are named with
the way out (`ImageTrace.HoleWayOut`), and so are round holes in copper with no drill drawn in them when the picture has
a drill colour. These were opened silently before.

Seen while testing, not changed: a Paint Fill clicked on a solid pour takes up to `FillEdgePx` (4 px) of every hole
in it, because it treats each hole's edge as a drawn line. A 0.3 mm hole drawn 12 px across at 25 µm a pixel was lost.
Paint Fill is for line drawings, and on those it is right.

## A line drawing made simple: no copper from its lines, the board outline bounds the reading, its size beside the scale (2026-10-28)

The owner traced a pasted screenshot of the datasheet's line-drawn test board and got a layout that looked nothing like
it. Three things went wrong, and only the first was a defect:

- **The round-hole tightening of the same day dropped every via** (see the section above: circularity was withdrawn).
- **The scale was 4× too small with nothing to show it.** Line impedance… was taken on the RF line with a 10-layer
  1.7 mm technology, where a 50 Ω Top Copper line is 102 µm wide over its thin prepreg. The drawing's board is 0.2 mm
  FR-4, where it is 0.4 mm. The 24 px line was measured correctly, so the board came out 4.45 mm across instead of 17.
  Every via that got the technology's 0.6 mm pad on a 0.1 mm drill read as a large dark disc.
- **Every line, letter and the parts table were copper.** A clean board needed Remove ▾ on each layer before any Paint
  Fill, and nothing said so in the order it had to be done.

What changed:

- **A line drawing reads no copper from its lines.** `IsLineArt` is now decided before any correction, and when it
  holds, the copper layers read are emptied before `EditCopper`. Paint Fill floods the colour clusters, not the regions
  read, so it is unaffected. The round holes inside a painted area still become vias. `LineArtNote` says to click each
  copper area with Paint Fill.
- **Only the copper inside the board outline drawn by hand is read** (`InsideOutline`, after the copper corrections).
  The last outline drawn counts, as it does for the outline written.
- **`ImageTraceResult.SizeLine`** gives the board's size at the scale: the outline drawn, else one read from the
  picture, else the crop (`Scope`), else the whole picture (*Board 17 mm × 17 mm*, *Crop …*, *Picture …*). It is
  appended to the scale's report sentence and shown in the dialog's Scale row. At a scale 4× too small it reads *Board
  4.45 mm × 4.44 mm*.

On a screenshot of a datasheet's line-drawn 17 mm test board at 16.3 µm a pixel, a crop to the board, three Paint Fill clicks (both pours and the RF
line) and the outline give two pours, the line and 70 vias, with the parts table and notes left out.

**Silkscreen is off by default, and a via's pad is never a disc six holes wide** (owner, the same day). The owner's
next attempt used the same 4.26 µm-a-pixel Line impedance scale and showed two more defaults working against a user:

- Auto mapped the drawing's grey anti-aliased edges (#818181) to Silkscreen. Its 2,696 traced edge shapes drew a second
  outline round every line. A colour that `IsSilkscreen` recognises is now mapped to **Ignore**. It is still kept out of
  the copper, and silkscreen is read only when the user sets a row to it. The owner's reasons: most pictures are read
  for their RF traces and top copper, silkscreen read from a picture is seldom right, and something that is seldom
  right should be asked for, not given.
- A via with no pad drawn took the technology's 0.6 mm pad whatever its drill, so a 0.1 mm drill (a scale 4× too small)
  drew as a dark disc six holes across. `PadFor` now takes the technology's pad only up to twice the drill, else twice
  the drill. At the true scale, 0.35–0.45 mm drills keep the 0.6 mm pad.
- The board's size is now also the canvas prompt whenever a scale is set and no tool is armed, with Two points…, Board
  corners… and Line impedance… beside it (`SizeCheckPrompt`). The same size in the side pane alone was not seen.

## A board drawn as DXF outlines, recognised (2026-11-12)

A two-layer RF board drawn as a DXF of outlines — copper, drill marks, a silkscreen body rectangle and a typed designator
for every part, no lands and no mask — imported with a `--layer-map` and recognised. Each finding below left the circuit
wrong with no hand correction possible from the report alone. The fixture is synthetic
(`testdata/interchange/dxf-board/make_boards.py`, six spellings of one board).

- **Typed designators are read** (R-as10-2, R-as10-4, R-as10-6, R-as4-1 (4), R-as4-9). `SilkscreenText` reads stroked shapes
  only and skipped every `LabelShape`, so a DXF's `TEXT`/`MTEXT` designators — exact characters — named nothing, and with
  no other evidence the report said nothing about the silkscreen at all. `TypedSilkscreenEvidence` makes each label whose
  text passes the designator rule a `SilkTextLine` with no glyphs and a claim through `IPartEvidenceSource`, the seam the
  picture reader (`RasterSilkscreenEvidence`) already used; its claims go first, then the strokes name what is left.
  MTEXT's inline codes are stripped first (`{\fArial|b0;C2}` is C2). Typed text only NAMES parts another rung found
  (`PartReading`'s association, unchanged). The strokes are read only while some part is unnamed, so the typed count is
  reported regardless (`SilkscreenLabelsRead`): with no part found it is what says the designators were there. A typed
  line has no glyphs, which `Lesson` already answers with nothing to learn. Gate: `TypedSilkscreenTests`.
