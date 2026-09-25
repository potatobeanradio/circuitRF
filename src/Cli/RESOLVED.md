# src/Cli — resolved findings

Findings worth keeping from work on the command-line driver. The design note is
`docs/design/cli.md`; this file records what turned out to be true while building against it, not
what the design says.

---

## AUT-13 — the installed `circuitRF` executable is the CLI (2026-09-24)

`docs/design/cli.md` §20 has the design; these are the things that turned out to be true.

**`src/Ui` → `src/Cli` publishes, and is still the wrong reference.** Measured: no NETSDK1150 at all
(`-r` flows the RID to the referenced exe), but the self-contained publish tree gains a
framework-dependent `CircuitRF.Cli` apphost, `CircuitRF.Cli.deps.json` and
`CircuitRF.Cli.runtimeconfig.json` — dead files in every installer. Hence `src/Cli.Verbs`, which
compiles this folder's sources in place. **Its `Compile` glob must exclude `../Cli/obj/**`:** that
holds the EXE's generated `AssemblyInfo.cs`, and including it gives the library a second set of
assembly attributes. And every `InternalsVisibleTo("CircuitRF.Cli")` elsewhere had to follow the code
— only `CircuitRF.Render` had one, and the build names the members it hides.

**`IsVerb` comes from `Run`'s own switch.** The switch expression became `Dispatch(string)`, returning
the verb's function or null; `Run` calls it and `IsVerb` asks it. Two lists would drift, and the
installed executable's "command line or document?" decision is the one place a drift is silent.

**The application's seven module initializers run before its `Main`**, so the installed CLI gets them
and `CircuitRF.Cli.dll` does not. Measured harmless: `check --json` over all ten shipped examples and
`lvs --json` over three are byte-identical through both front doors. The PCell generator seam can
change an answer only for a PCell cell written before pins were persisted, and there the installed CLI
gives the GUI's answer. A future initializer that does more than install a seam — reads a file, starts
a thread — runs on every CLI call too; `InstalledCliTests`' byte-identity gate is what would notice.

**A running `serve` after a macOS update: measured, and it degrades rather than dies.** With the
bundle exchanged under it (`renamex_np RENAME_SWAP`, as the updater does), `check` still answered and
`run` / `history` failed with *Could not load file or assembly 'NumFlat'* / *'System.Diagnostics.
Process'* — each returned as an ordinary tool error, and the server kept going. That half-working state
is what `InstallationGuard` replaces with one JSON-RPC error (−32001) and an exit. **The measurement is
easy to get wrong:** the first run swapped in the pre-change build of the same RID, and EVERYTHING
still worked — the two bundles differ only by one assembly near the end, so every assembly before it
sits at the same offset and the swap is invisible. The x64 build (laid out differently throughout)
is what showed the failure. Deleting the tree instead (the Linux reclaim shape) is caught the same way.

**`--version` is plain text, and first.** It is taken before `JsonRun.TakeFlags`, so `--version
--json` still prints only the version: a caller asking which build answered has not chosen a protocol
yet. The version string is `JsonRun.Version()` — the one reader of `InformationalVersion` in this
assembly besides `McpServer`'s `serverInfo`, which reads the same attribute.

**The installed form wants the verb FIRST.** `circuitrf --json check .` works under `dotnet run` and
opens the GUI when installed, because `IsVerb` looks at `args[0]`. Nothing documented put a flag
first, and `TakeFlags` already assumed the verb leads (`smith`'s `--at`); the user guide now says so.

---

## `check` learned the terminal map, and a `.ccell` became a path it accepts (2026-09-21)

`brief-lvs-1-terminal-map.md` R-lvs1-4. `Check.cs` gained a row in §10.2's validator table and
**nothing else**: `CheckCell` calls `TerminalMap.ValidateCell` and forwards whatever comes back. Every
`check.terminals.*` id, severity and sentence is authored in `src/Design/Layout/TerminalDiagnostics.cs`,
below the firewall, so the cell Properties panel reports the identical ones — R-aut4-2's rule, which is
the reason the family is not in `CliDiagnostics` beside its neighbours.

**Loading a cell's two primary views moved out of the verb too.** The first version had a private
`ReadPrimary` here; `TerminalMap.PrimaryViewsOf` is where it belongs, because the panel and (from
brief 3) LVS resolve the same two files and three copies of "which `.csym` and which `.clay`" is the
shape that drifts.

**A bare `.ccell` is redirected to its folder in `Run`, not in `DocumentKinds.Classify`.** Classifying
the file as `DocumentKind.Cell` would be honest — the file IS the cell's declaration — but `render`,
`explain` and `find` all take a cell as a DIRECTORY and would then be handed a file. One verb wanted
it, so one verb does it.

---


## AUT-12 — layer colour, framing on a subset, and two shapes that did not compose (2026-09-08)

`brief-automation-12-render-ergonomics.md`. Four places the artwork half of the automation surface —
otherwise the strongest part of it — made a caller do arithmetic or edit a document to get an ordinary
picture. **None of the four was a bug**: every one produced a correct answer in a form the next verb
could not take, which is a failure class with no error, no warning and no wrong result. It is written
up as `automation-architecture.md` §6B, beside §6A's "silence is the defect".

### The renderer ignores a layer colour's alpha, so an eight-digit override had to move something else

`LayerDef` carries a `Color` (RGBA) and a `FillOpacity` (0-1), and it is easy to assume the first is
the alpha. It is not: `LayoutRenderer` builds its `SKColor` as `new SKColor(def.Color.R, def.Color.G,
def.Color.B)` at **all four** of its call sites (`LayoutRenderer.cs:977`, `:1135`,
`Instances.cs:961`, `Snap.cs:87`) and takes the alpha from `FillOpacity` — `LayerFillPaint.Create`
computes `def.FillOpacity * 255` and `DrawLayer` computes the stroke's alpha the same way.
`Color.A` reaches nothing.

*Side effect worth knowing about:* putting a colour in the layer report fixed a name in the same row.
On a layout with NO technology every layer is a fallback-palette layer, and that branch of
`LayerReport` had been spelling the row's name `kv.Key.ToString()` — and `LayerKey` is a plain record
struct, so that is `LayerKey { Layer = 7, Datatype = 0 }`. Building the row from
`FallbackPalette.For(key)` gives both the colour the picture was actually drawn in and the `L7/0` name
`explain --layers` and `--layers` already use.

So `--layer-colors "Top Copper=#ff000080"` had to be defined as setting the layer's **fill opacity**,
not the colour's alpha. Writing the alpha into `Rgba.A` and stopping would have produced an override
that parses, reports itself as applied, and changes not one pixel — the exact shape of failure this
whole series is about, arrived at from the opposite direction. `RenderLayerJson` now reports each
layer's `color` and `fillOpacity` **as drawn** for the same reason: a colour change is the one thing a
caller receiving only a picture cannot verify from the numbers beside it.

### One clone carrying both the visibility and the colour, not two chained ones

`TechnologyLayerSelection.WithVisibility` already existed for `--layers`, and the obvious way to add a
colour override was a second pass over its output. It works, and it is wrong in a way that would not
have shown up for a while: the appearance predicate would then read a **copy** rather than the
technology's own `LayerDef`, so "as before, but brighter" would quietly mean "as the previous pass
left it". `WithLayers` takes both predicates and both read the original; `WithVisibility` forwards to
it, so there is still exactly one reflective copy.

The cached-technology trap is unchanged and now has a second gate: three renders in ONE process —
reference, coloured, plain — with the third required byte-identical to the first. It is the same trap
`--layers` has (`TechnologyCache` hands back a shared instance) and re-asserting it per flag is
cheap, because it is invisible across two processes by construction.

### `--fit` already framed only what is drawn; nobody had said so, and it is the wrong knob anyway

The brief offered two answers and the truth needed both. `DocumentExtents.LayoutBox` gates on
`LayerDef.Visible` and `DrawLayout` measures with the **clone** the drawing is taken through, so
`--hide-layers` had always shrunk the fit as well as the picture. Specifying and testing it was one
test.

But it does not answer the case that produced the requirement. An imported board's drill-map
fabrication drawing sits far outside the board; hiding it fixes the framing and **removes the
content**, which is a different picture from the one that was wanted. `--fit-layers` frames on some
layers and draws all of them, through a second clone taken from the first.

**The gate for it needed a fixture nobody would guess.** Comparing `shapesDrawn` between
`--fit-layers` and an unfiltered render fails: the narrowed frame culls the far outlier, so the two
counts differ for a reason that has nothing to do with the flag. `shapesDrawn` is a per-frame counter
and is viewport-dependent by construction. The fixture therefore puts **two** shapes on the outlier
layer — one 100 mm away and one inside the board — and the assertion is against `--hide-layers` at
the identical viewport, where the in-board shape is the whole difference between the two flags.

### `explain --extents` and `render --window` were each right and could not be composed

`--extents` reports base SI with the unit and the scale named (R-rnd3-8, and the 2 Hz bug is why).
`--window` refuses a bare number, because DBU, micrometres and millimetres are three plausible
pictures six orders of magnitude apart (R-rnd2-4). Both rules are correct and together they meant the
one verb that says WHERE the content is emitted exactly what the other verb rejects.

**The fix is a `window` STRING beside the numbers, not a change to the numbers.** A report is read by
more than one kind of caller, and replacing a measurement with a command line would trade one
half-answer for another.

Three things about the spelling that are not obvious:

- **It cannot be metres.** `LayoutUnits.TryParse` — which is what `render` parses a coordinate with —
  reads `nm`, `u`/`um`/`µm`, `mm`, `mil` and `in`/`inch`, and has **no spelling for a bare metre at
  all**. Emitting the numeric field's own unit would have produced a string that reads perfectly
  plausibly and is refused. It is spelled in the document's DISPLAY unit instead.
- **The suffix table is not the display table.** `LayoutUnits.Suffix` already existed and gives
  micrometres as `µm`, which the parser does accept. A coordinate emitted to be pasted travels through
  an argument list, a JSON document and somebody's shell first, so `AsciiSuffix` is a second table with
  one row different, and both live beside the parser rather than in either verb.
- **The decimal count is derived.** One DBU is `1000 / (nm-per-unit × dbu-per-micron)` of the display
  unit; `Spell` uses that many places plus one, so the formatter's nearest-value rounding and
  `ToDbu`'s away-from-zero rounding cannot land on opposite sides of a boundary. `Format`'s default
  four places silently quantises a nanometre-resolution layout written in millimetres to 10 nm — a
  window off by a hair, which is invisible in the picture. The round-trip gate uses coordinates that
  are round in no unit at all, because a value landing on whole micrometres round-trips through a
  truncating formatter as happily as through a correct one.

The vacuity guard is the defect itself, kept as a test: the numeric fields spelled straight out of the
document are still refused by `--window` on a layout. Without it, a `window` field that happened to be
unit-bearing by accident would look like a fix.

**And the hand conversion was not merely tedious — it was lossy, which nobody had noticed.** Writing
the user-docs transcript by converting the printed metres gave `3767.19um`; the real value is
`3767.188um`. The console prints coordinates at `G6`, so a hand conversion inherits six significant
figures and a 20 mm board loses the last two nanometres of its box. `DocumentedWalkthroughTests` —
which executes the chapter and compares the real transcript — caught it immediately, which is exactly
the argument for the feature: **the number a caller could see was never the number the verb wanted.**

### `convert -o out/board.clay`: refused, rather than collapsed to one file

A `clay` target is a directory because the import IS the conversion — the readers write one cell
folder per structure plus a technology beside them, which is why `--to clay` simply stops after the
import. `-o out/board.clay` therefore produced a *directory* called `out/board.clay` with the real
`.clay` two levels inside it.

Writing the `.clay` where asked was the other option in the brief and is worse: an import that
produced a **hierarchy** has no single file to collapse to, so the rule would work for the flat case
and discard the other silently — which is the failure mode the refusal exists to remove, reintroduced
one level down. The `.clay` extension is also how a caller SAYS clay, so the inference is kept and the
SHAPE is refused, with the directory spelling (the same path minus the extension) in the message so
acting on it is an edit of one token. An existing directory passes whatever it is called; an existing
FILE is its own refusal, because nothing in `convert` overwrites.

### Noticed while working here, deliberately NOT changed: `--layers` has a dead alias

`Render.ApplyLayerSelection` builds its name map with two entries per layer:

```csharp
known[l.Name] = l.Key;
known[l.Key.ToString()] = l.Key;   // a caller that has only the numeric key from an import
```

`LayerKey` is a plain `readonly record struct`, so the second key is the literal string
`LayerKey { Layer = 1, Datatype = 0 }` — nothing a caller would ever type, and the comment's stated
intent is unreachable. It costs nothing and misleads nobody in practice, because the generated
definitions carry `L1/0` as their **Name** and that is the spelling both `explain --layers` and
`--layers` already use.

Left alone on purpose: making `1/0` work is a widening of the accepted input with no requirement
behind it, and this brief's four changes are each answering an observed defect. Recorded so the next
person to read that line does not assume it works.

### `StrMap`: an MCP option kind that is about shape, not confinement

`layerColors` is a map, and the two ways to carry one over the existing kinds are both worse. An
array of `"name=#rrggbb"` strings makes the client assemble the `=`, which it will eventually assemble
wrong. A comma-joined `StrList` splits on a character that can legitimately appear in the KEY — a
layer name is chosen by a technology author, not by us — and the two halves then resolve to nothing.
`StrMap` declares `{"type":"object","additionalProperties":{"type":"string"}}` and emits the flag
repeated, one `key=value` per entry. The CLI accepts both spellings, so a person at a shell still
writes one quoted list.

---


## AUT-11 — `netlist`, `plot`, `find`, and a `create` that makes its own parent (2026-09-08)

`brief-automation-11-missing-verbs.md`. Four capabilities a client reached for and did not find.
Additive: nothing existing changed behaviour except the two noted below, both of which replaced a
wrong answer with a right one.

### The automation surface could not simulate a design anyone had drawn

**R-aut11-1, and it is the largest single gap the series found.** `run` on a `.csch` failed with
`Error: Cell '"FormatVersion"' not found in libraries (referenced by '')` — it had handed the JSON
document to `CnlReader`, which parsed it as netlist text and reported its first key as a missing cell
name. There was no `extract` verb and no `netlist` verb, and `check` and `explain` both accepted a
`.csch` happily, so the surface read as though a run should too.

The consequence was not merely inconvenience: **the surface could author a design, validate it,
explain it and draw it, and could not run it.** Every finding in AUT-8 and AUT-9 was reached only
because a testbench had to be reconstructed from scratch instead of started from a known-good
extraction.

Two halves landed together and neither is complete alone: every run verb now reads its input through
`CircuitSource.ReadRunInput`, which takes a `.cnl` as itself and a `.csch` through the extraction, and
`circuitrf netlist` writes that extraction out.

**The file the verb writes is the same BYTES a run consumes, not merely an equivalent netlist.** Both
go through `CircuitSource.CnlTextOf`. That is why the provenance comment is a constant rather than a
timestamp or the verb's name: two extractions of one schematic have to be comparable, and the gate
compares them. `MissingVerbsCliTests` also scans the whole of `src/Cli` for a second
`CnlWriter.Write` — **and the scan has to be on the WRITER, not on `NetExtractor.Extract`.** `check`
legitimately calls the extractor on its own account, because extraction is where a naming conflict
between two labels on one physical net is reported and nothing else in the tree reports it; it then
goes through `CircuitSource` for the netlist half like everyone else. What must not exist twice is
the write, because that is what decides the bytes.

**It is also the reference answer a client checks its own authoring against.** AUT-7 §3's false defect
report — a working `TunerModel` written up as broken — came from a one-net instance line. One look at
a known-good extraction would have shown two nets on that line and the whole detour would not have
happened.

### `plot`: the trap was the port number, and it is silent

**R-aut11-2.** The verb builds a `DataDisplayConfig` and hands it to `RenderDataDisplay.Draw`, which
`render`'s own `.cdd` half calls — one plotting path, gated by rendering the `--write-cdd` document
with `render` and comparing byte for byte in all three formats.

**On an `i` or `j` axis the shorthand's integer token is a 1-based PORT NUMBER, not a 0-based index**
(`SliceTokenParser.Parse` — `S[:,2,1]` is S21, which is what makes that spelling readable). The first
implementation here mapped a port number to an array index and emitted that, which the parser then
interpreted as a port number again. It was caught by a refusal (`Port 0 out of range`) only because
port 1 maps to index 0; **for `i=2,j=1` it would have drawn S12 and said nothing**, and on a
reciprocal part S12 and S21 are the same curve, so it would have been invisible in the obvious test.
The gate asserts the slice indices in the written `.cdd` rather than only that a picture appeared.

**Two smaller ones worth keeping:**

- **`TracePropertiesConfig.LineColorIndex` is an index into `TraceProperties.LineColorOrder`, whose
  first entry is 12 (red), not into the colour table directly.** Using the trace's ordinal gave the
  first trace colour 0 — black — which is invisible on the dark variant and produces a correct-looking
  picture on the light one.
- **A PDF's metadata Title is taken from the OUTPUT path** (`PlotDocumentWriter.PdfTitleFor`, matching
  the application's own Export). Two byte-comparison outputs must therefore share a file NAME and
  differ by directory; `a.pdf` versus `b.pdf` differ in that one field and nowhere else, which is the
  sort of difference that gets excluded from a gate rather than understood.

**One refusal is made here rather than forwarded.** `CubeTraceSpecParser`'s answer for a bare name it
does not recognise is `Missing '['` — correct from where it stands, and useless to a caller who
mistyped a cube or is looking at the wrong run. `plot` extracts the bare cube name from the spec
first and refuses with the list of cubes the file holds. Every other syntax error is the parser's own
sentence, forwarded unchanged, because it is the same one the trace card shows for the same text.

### `find`: a bounded walk has to say when it stopped

**R-aut11-3.** There was no way to ask the surface what exists, so locating a workspace holding a
particular device meant searching the filesystem outside the MCP entirely — which a client that has
the server and nothing else cannot do at all.

**A listing that quietly stopped short is the one failure this verb must not have**: a caller reads a
short answer as "the workspace is not here" and goes elsewhere. So `truncated` is in the document with
a warning beside it, and only a directory that still had children when the bound was reached counts —
a leaf reached exactly at the limit did not stop short.

**A directory symbolic link is where a bounded walk stops being bounded and a confined one stops being
confined**, so one is never followed. That is also what makes "a path outside the root never appears"
checkable: the gate plants a link to an outside workspace and asserts it is absent.

**A cell whose analyses could not be read reports `analyses` as ABSENT, not empty.** "Declares none"
and "could not be read" are different answers, and reporting the second as the first tells a caller a
runnable cell is not runnable.

### `create` refused what it could simply have done

**R-aut11-4.** Creating a workspace under a path whose parent did not exist failed with
`No such directory`. The refusal was defensible and it was discovered by hitting it — and a client
with no file tools of its own had nowhere to go from there. Creating an intermediate directory is not
the destructive act that refusal was guarding: nothing is overwritten, an existing workspace is still
refused, and the creation is reported as an info diagnostic so a mistyped path is visible rather than
silently materialised.

The other half is the server's `instructions`, which now say plainly that **no tool here writes a file
of the client's text** — the client supplies its own file writing, and what these tools write is what
they produce. That was true before and was also discovered by hitting it.

---

## AUT-10 — the generated reference (2026-09-08)

`brief-automation-10-generated-reference.md`. Five requirements, all additive; nothing existing
changed behaviour except two refusals that used to be silent acceptances.

### The catalogue was publishing a symbol's pin count under the heading `nets`

**R-aut10-2, and it is the origin of the whole series' worst finding.** `reference components Tuner`
said `"ports": {"count": 1}` under a line labelled `nets:`. A `.cnl` instance line for a Tuner binds
TWO nets — the DUT node and the reference node — because the reference terminal is implicit on the
glyph. A client with no schematic editor and no source tree wrote the one-net line the catalogue
described, got a bench whose bias tee delivered nothing, and reported `TunerModel` as defective with
a minimal reproduction case. The model is correct.

The fix is a second field, `CatalogEntry.Nets`, from `InstanceNetContract.ForToken` — the same table
the elaborator refuses a wrong count with, so the catalogue and the reader are one statement. The
rendering labels the two differently (`nets:` and `terminals:`) because they are two facts.

**It is MEASURED, not written down.** `ForToken` constructs the type at three port counts and asks
`Expected` of each: three equal answers is a fixed count, an affine progression is a rule with its
multiplier and intercept read off the measurement, anything else is reported as the three numbers it
is. Three points and not two, because two cannot tell an affine rule from a coincidence — and the
Switch is the case that proves it useful, at `2N + 2` rather than the `2N` two points would have
suggested if the intercept had been assumed away.

**Constructing a model here is not the thing AUT-6 forbade.** R-aut6-9's rule is that a port count
must not be read off a model built from invented parameter values. `InstanceNetContract.MinimalParameters`
supplies only what a type REFUSES TO CONSTRUCT without — a Tuner's `Z[1]`, a Mutual's two inductor
names, a Match's empty design — and the three-point probe is what establishes that none of them moved
the answer. That is a different claim from reading a number off a guess.

### The `Term` note was the defect, not a disclaimer of it

The catalogue's note for a symbol-less type ended "…and nothing below the UI firewall states how many
nets its instance line takes." Accurate, and exactly the problem: the reader knew, and nothing asked
it. Removing the gap removed the sentence.

### The wrong-count refusal has two spellings, and a gate must accept both

The AUT-10 gate writes an instance line per registered type and elaborates it, one net short and one
net long as well. Two different refusals fire: `InstanceNetContract.Refusal` (AUT-8) and the older
per-family `Elaborator.ValidatePortPairNetCount`, which runs FIRST for the ideal system blocks and
says the same thing in its own words (`Amp 'A1': expected 4 nets (in+, in−, out+, out−); got 3.`).
A gate that insisted on one sentence failed on a correct refusal. Both are the reader rejecting the
count.

**And a gate that supplies no parameters is blind.** The first version wrote bare lines and reported
Tuner, Match, Amp, Chain, Coupler and nine others as "accepted at the wrong count" — because model
CONSTRUCTION failed first and the net check never ran. `NetlistContractTests`' own
`EveryRegisteredPrimitiveEitherStatesANetCountOrIsANamedException` had the same blindness from a
private minimal-parameter table that covered three types: **Tuner, the type the entire series exists
for, was silently skipped by the test meant to hold the table exhaustive.** Both now use
`InstanceNetContract.MinimalParameters`, and the test asserts it actually reaches those three.

### The two format topics are generated by reflection, and that was the only affordable answer

R-aut10-3 and R-aut10-4 ask for pages describing the `.cdd` and `.ctech` formats. Both are
`System.Text.Json` serialisations of a DTO tree — `DataDisplayConfig` is ~60 fields over 10 types plus
20 enums. A hand-written description of that is a second copy of the reader that goes stale silently,
which is the failure the whole surface exists to remove, so `src/Cli/DocumentSchema.cs` walks the
type instead and reads every default off a freshly-constructed instance.

**Each carries an authored preamble and a WORKING example.** The generated half cannot say what a
field is for, and it cannot say which four fields matter. Writing those examples caught two things the
field list alone would not have: `PlotType` has no member called `Rectangular` (it is `Rect`), and a
network-bound trace's `Row`/`Col` are ZERO-based, so S21 is `Row 1, Col 0`. Both were wrong in the
first draft of the preamble and both were caught by rendering the example. The gate extracts the
example from the served page and parses it.

### `read` accepted a `.cdd` and did not say so; it also read a `.wasm` as text

**R-aut10-5.** The schema listed `.cws .csch .csym .clay .ctech .cem .cnl`; the verb has always taken
a `.cdd` too, and an out-of-process client found that by trying it. A schema that under-promises costs
a caller what one that over-promises does, because a client believes it either way.

The audit of the same list found the other direction as well: `DocumentKind.AssemblyRules` (`.wasm`)
fell through to the text path, so `File.ReadAllText` on a compiled module produced whatever the bytes
decoded to and handed it back as a document. It is a refusal now (`read.file.binary`), naming what the
file is.

### The server's `instructions` carry a worked example that was actually run

The six-step sequence in `McpServer.Initialize` — create, write a `.cnl`, `check`, `run`, `read` — was
executed end to end before it was written down, and the first draft of the `.cnl` in it was WRONG in
a way worth recording. `R:R1 in mid 96 Ohm` — the value written positionally, which is how a
resistor reads in several other netlist languages — is refused: nets stop at the first `name=value`,
so with none on the line `96` and `Ohm` are read as two more nets and AUT-8's check reports a
four-net resistor. The value needs its key (`R=96 Ohm`). **The refusal named the line, both counts and
where nets stop**, which is exactly the difference between a ten-second fix and a guess — it is the
first time in this series the new refusals caught the author of the surface rather than a client.

---


## Post-RND-5 review — the layers a technology does not define (2026-09-07)

A read-through of the whole `brief-render-0-overview.md` series against the code. One defect family,
found by asking the question the series' own gates could not: **what happens on a document that draws
on a layer key its technology does not declare?**

That state is ordinary — it is what an import produces (`layout-view.md` §2.4) — and
`LayoutRenderer` handles it: an undeclared key resolves through `FallbackPalette.For`, whose
`Visible` is `true`, and the shape is PAINTED. `explain --layers` already reported such rows,
deliberately and with a comment saying why. `render` did not know they existed, and that was wrong in
three places at once:

| | Was | Now |
|---|---|---|
| `render --json`'s `layers[]` | enumerated `tech.Layers` only, so the row was absent | the technology's table, then the generated rows — the same set `explain --layers` prints |
| `--layers L99/0` | refused, "the resolved technology defines no layer called…", **naming `explain --layers`** — the verb that had just listed it | accepted, and the picture is that layer alone |
| `--layers "Top Copper"` | drew Top Copper **and** L99/0 | draws Top Copper |

**The third is the one that matters**, and it is R-rnd2-7's own failure mode with the sign reversed.
That rule refuses a misspelling because "a picture with that layer missing is indistinguishable from a
layer that is genuinely empty"; here a caller asked for ONE layer, got TWO, and the extra one is
indistinguishable from a layer it forgot it had asked for. Verified before and after on a fixture
drawing on 1/0 and 99/0 against a technology declaring eight layers and not 99/0: `shapesDrawn` was 2
with `--layers "Top Copper"` and the reported `extents` still spanned the second rectangle.

**Why the existing gates could not see it.** RND-3's gate 4 compares `explain --layers`' counts
against `render --json`'s dictionary-for-dictionary — exactly the right assertion — but its fixture's
technology declares every layer the fixture draws on, so the two sets were equal for a reason that had
nothing to do with the code. The gate is now also run on a document with an undeclared key
(`Layers_ThatTheTechnologyDoesNotDefine_AreReportedByBothVerbs`), with the generated name asserted
present as a vacuity guard, and RND-2 gained
`ALayerTheTechnologyDoesNotDefine_IsReportedAndSelectableAndExcludable`.

**The fix is one idea in one place.** `TechnologyLayerSelection.WithVisibility` takes the generated
definitions as `extraLayers` and the verb hands it `FallbackPalette.For(key)` for every counted key
the technology does not declare — the palette's OWN definition, so with nothing selected the drawing
is byte-for-byte what it was, and `Visible` becomes a field there is somewhere to write. A selection
had nowhere to write it before, which is the whole of the bug.

### Three flags a data display read and dropped

`CddInapplicable` refuses an option that describes a drawing rather than ignoring it, and its own
header gives the reason: a caller that passed a flag and got a full picture back has no way to learn
the flag did nothing. Three were being read into `Options` and never reaching
`RenderDataDisplay.Request`:

- **`--theme`.** A display draws on `RenderTheme.Light`/`Dark`, which are not `.ccolor` themes at all
  (that file's own `TODO 7.x` records that they are not wired to one). It is its own refusal —
  `render.cdd.theme-not-applicable` — because the remedy is different from the others': `--variant`.
  A test asserts the remedy actually changes the picture, so the refusal cannot be satisfied by a verb
  with no colour control.
- **`--margin`.** A fraction of an extent, and a display's page has no extent for it to be a fraction
  of.
- **`--tab` together with `--all-tabs`.** Two answers to "which tab", with `--tab` silently losing —
  `render.tab.conflict` now, on R-rnd2-3's "refused together rather than ordered" terms.

### One thing found in `src/Render` on the way, and it is older than this series

`LayoutRenderDetail.CanAffordOutlines` built a set of VISIBLE layer keys and skipped any shape whose
key was not in it — so a shape on a layer the technology does not declare was not counted, while
`LayoutRenderer.Draw` draws it. The budget therefore undercounted worst on exactly the documents where
undeclared keys are ordinary (an import), and answered "outlines are affordable" about a frame they
were not affordable for. It is `HiddenLayers` now: skip what the technology declares and HIDES, count
everything else. Detail in `src/Render/RESOLVED.md`.

### The series' own byte-identity gates were flaky under full-suite load

Three failures in a full `dotnet test`, all green in isolation, all the shared-static hazard RND-4
recorded — and the reason is that RND-4's fix was applied to one half of the problem.
`SkiaFontsTypefaceCollection` serialized the three Data Display classes; RND-2's schematic, symbol and
layout gates were in `LayoutTextOutlineTypefaceCollection`. **Two collections is two groups xUnit runs
in PARALLEL**, so `ScalarCubeTests`' Helvetica window still fell across
`RenderCliVerbTests.RenderingASchematicAsAProcess_WritesTheBytesTheRendererWrites`, which draws in
this process and compares against a CLI process that has no override to read. Five more classes set
`SkiaFonts.TestOverrideTypeface` and were in NO collection at all.

The two names now resolve to one collection, and the five loose setters joined it — except
`ComponentPreviewTests`, which is in `CellStatGlobalsCollection` and cannot be in two. Its two tests
set the override to `SKTypeface.Default` for a reason R-rnd1-4 removed (the embedded faces would not
load without an Avalonia host), so they simply stopped writing the static. **That is the direction the
rest of this should go**: RND-1 recorded that deleting the override entirely is the real end state,
and every class still setting it to `SKTypeface.Default` is carrying a workaround for a limitation
that no longer exists.

**What the membership rule has to say, and did not:** a class belongs in that collection if it sets
either typeface static **or compares rendered TEXT bytes against another process**. The second half is
the one that is easy to miss, because such a class looks like it touches no global at all — which is
precisely how RND-2's gates ended up outside it.

---


## RND-5 — the protocol surface, and the user-docs chapter (2026-09-07)

`brief-render-5-mcp-and-user-docs.md`. `render` as a tool, RND-3's three questions as arguments on the
`explain` tool, and `docs/user/src/reference/cli.md` rewritten around both. No new behaviour on either
side — a discoverability brief that changes what a verb does has stopped being one.

### The brief's tool count was two short, and the count is load-bearing

R-rnd5-1 says "eight tools, up from seven", and `cli.md` §11.3 said seven too. **`tools/list` was
already advertising nine**: `ToolCatalog` holds `run`, `check`, `explain`, `create`, `import`, `read`,
`history` and `reference`, and `McpServer` appends `HistoryBatch`'s `batch` beside them. RC-5 and RC-9
added the last two and neither updated the number. `render` makes **ten**.

That is not pedantry about a sentence: §11.3's whole argument is that the count IS the cost, so a
design note that undercounts by 22% is arguing from the wrong number. §11.3's table and the
`TheServer_StartsAdvertisesAndShutsDownCleanly` assertion now list all ten, in order.

### `explain` gained FIVE arguments, not three, and R-aut-13 is why

R-rnd5-1 asks for `--cells`, `--layers` and `--extents`. The verb also reads `--all` and `--view`, and
both are the arguments that **answer these three questions' own refusals**: `--extents` on a cell
folder holding two views is refused naming `--view`, and `--cells` hides generated cells unless
`--all` says otherwise. A client handed a refusal naming a flag it cannot pass is exactly the
asymmetry R-aut-13 forbids, in the direction that is hardest to notice — the capability is there, the
refusal is correct, and the caller is stuck.

`EveryAdvertisedArgument_IsAFlagTheVerbActuallyReads` could never have caught it: it checks that every
ADVERTISED argument is a flag the verb reads, not that every flag the verb reads is advertised. The
gate now also asserts the five rows exist by their CLI spelling.

### The image attachment is the one argument that is not a flag, and it needed a place to live

R-rnd5-4 asks for an explicit tool argument, default off. There is no CLI spelling of it and there
must not be — a command line writes the file and the person opens it; a protocol client may have no
way to read the path it was handed. So it is a property of the ENVELOPE, not of the render.

`ToolSpec.Adapter` is a separate list rather than a flavour of `ToolOption`, so the invariant stays
checkable **by reading**: everything in `Modes` becomes argv, and the only things that do not are
there. `ToArgv` type-checks them through the same `Emit` with a null `argv`, so a client that wrote
`"attachImage": "yes"` is refused by name rather than handed a picture-less result with nothing said.
The parity gate exempts it by name and **counts the exemptions**, so one that quietly grew would be a
flag the verb does not read arriving by the other door.

### The document had to stay byte-identical, so nothing about the attachment is in it

The attachment and its cap refusal are ADDITIONAL content blocks. Block 0 is still exactly what
`circuitrf render --json` writes, which is what keeps §11.7's parity gate meaning what it means — and
putting the cap's diagnostic into the document instead would have been the adapter authoring a
diagnostic the CLI never writes, which is the thing §11.1 exists to prevent.

`isError` stays the verb's own exit code, too. A render that succeeded and was too large to attach is
not a failed run.

### The cap is 4 MiB, and it comes from RND-2's measured table

The brief asks whether the number is right "from the measured sizes RND-2 §7.4 produced rather than
from a guess". From that table, at the default 1600x1200 page on a real six-layer board:

| | `--detail full` | `--detail screen` |
|---|---|---|
| `.png` | 1.4 MB | 1.0 MB |
| `.pdf` | 9.6 MB | 2.6 MB |
| `.svg` | 23.5 MB | 6.2 MB |

4 MiB admits **every PNG that board can produce** — which is the format an agent wants when it wants
to LOOK at something — and every schematic and symbol in any format, while refusing exactly the four
cases where a caller genuinely should have narrowed: a whole dense board as vector geometry. Base64
adds a third on top, which is why the cap is on the FILE rather than on the frame. The gate renders a
250,000-vertex polygon and measures **8,339,447 bytes** of SVG, so it is a real render past a real cap
rather than a stubbed size.

### A PDF is attached as a PDF, which is not an `image` content block

R-rnd5-4's third constraint forbids transcoding, and MCP's `image` content means an image. So `.png`
and `.svg` go as `image` with their own MIME type and `.pdf` goes as an embedded `resource` with a
base64 `blob` — one rule, two envelopes, and the gate decodes both against the file on disk.

### Two option kinds had to exist, and the second is a confinement hole that was already open

`PathRepeat` is a repeated flag whose items are FILES (`--data a.npy --data b.npy`); `StrRepeat`
repeats and `Path` confines, and neither did both.

`PathOrName` is `--theme`, and it is the more interesting one. It takes a theme NAME resolved through
a chain of directories the server root has nothing to do with, OR a `.ccolor` file the caller points
at. `OptKind.Path` would turn `dark` into `<root>/dark`, which resolves to no theme at all — the
capability would be unreachable. `OptKind.Str` would let a client name any path on the machine and
learn from the refusal whether it exists, and `ColorThemeIo.LoadFile` would open it. So the value is
confined **when it names a file** — an extension, a directory separator or a root — which is
`Render.ResolveTheme`'s own split one step earlier, and each end now names the other.

**This is reported rather than absorbed**, per the brief's last bullet: a flag that has to be
explained by describing where its value is resolved is a flag whose shape is arguable. If `--theme`
were ever split into `--theme <name>` and `--theme-file <path>`, this kind would go with it.

### The chapter: what could not be explained without an implementation detail

The brief asks for these. Two:

- **`--detail`'s pixel budget.** `--detail 0.5` is not the tolerance you get: `LayoutRenderDetail`
  buckets by octave, so the number a caller asks for and the `toleranceDbu` reported back routinely
  differ. The chapter says so and points at the `--json` field, which is the only honest thing to do,
  but "pass a number, get a different number" is a flag explaining itself by describing its
  implementation. The three named modes need no such paragraph.
- **`--window` on a `.cdd`.** The refusal is right and its sentence is good, but the reason a caller
  reaches for it — "I want a crop of this plot" — has no answer at all: a display's plots carry their
  own axis windows, which are in the document and not on the command line. The chapter states the
  refusal and does not pretend there is a workaround.

Neither is this brief's to fix.

### The chapter's own worked example is a TEST, and it reads the chapter

Gate B.4.3 asks for the worked example executed end to end from an empty directory.
`tests/Ui.Tests/Render/DocumentedWalkthroughTests.cs` parses the commands, the `.clay` fence and the
expected transcript **out of `cli.md` itself** and runs them with the child's working directory set to
an empty temp folder. A test that re-typed the sequence would prove that something someone once wrote
still works and say nothing about the PAGE, which is the artefact a reader runs.

Three kinds of documented line are excluded from the comparison and each for a stated reason: a line
carrying an absolute path (the chapter anonymizes those to the SHAPE of a path), the chapter's own `…`
elision, and the one number that belongs to the machine — an encoded file's LENGTH. The render's
counters are compared in full, because §13.6 says they are deterministic and machine-independent by
construction, which is what makes them worth printing in a manual.

The second test in that file is gate B.4.2: every option the two verbs' own USAGE text prints appears
somewhere in the chapter. All 30 did.

### `tools/DocGen` did not build, and it is not in `circuitrf.slnx`

Found on the way to §B.3, not looked for. RND-1 moved `ColorVariant` to `CircuitRF.Render` and
`SvgFontNormalizer` out of `CircuitRF.Ui.Diagnostics`, and DocGen names both by their old namespaces.
**Nothing caught it**: DocGen is deliberately outside the solution (its own `.csproj` says so, for the
same reason `tools/IconGen` is), so a plain `dotnet build` and a plain `dotnet test` never compile it.

Three files, six lines, no behaviour. But the shape is worth keeping: **the docs generator can be
broken by a refactor for as long as nobody regenerates the docs**, and the only thing that would catch
it earlier is a build that compiles it. `tools/DocGen/check-docs-current.sh` exists; whether CI runs
it is a separate question this brief did not answer.

### The DocGen diff, classified

Three runs of the same tree (§B.3's own instruction, plus one more after a table fix):

- **Pages genuinely changed: 1** — `reference/cli.html`, 462 lines added and 7 removed, which is this
  brief's own chapter. `assets/js/search-index.js` changed by one line, being one line.
- **Figures genuinely changed: 0.**
- **Figure churn: 2, both the known rotation family** (`docgen-nondeterministic-figure`).
  `analysis-editor-hb-dark.svg` differed between run 1 and run 2 of the SAME tree
  (`matrix(0.9975 0.07055 …)` against `translate(113 365)`), and run 3 produced a third value
  (`matrix(0.8581 0.5134 …)`); `em-setup-loaded-dark.svg` joined it on run 3, flipping between
  `translate` and a rotation matrix. Both were restored, along with the two pages that inline them
  (`schematic-editor.html`, `em-setup.html`).
- **Id churn: 0.** No `cl_`/`img_`/`gr_`/`fp_` id appears anywhere in the retained diff.

### A pre-existing rendering defect in the docs' markdown tables, avoided rather than fixed

A `\|` inside a code span in a markdown table renders as the literal two characters, not as a pipe —
`derived-metrics.html` has shipped `<code>\|Z\|</code>` for as long as that page has existed. The new
tables were written to avoid the escape (`--variant` | "`light` or `dark`") rather than to add four
more instances of it. Reported, not fixed: it is the docs pipeline's, not this chapter's.

---


## RND-3 — `explain --cells`, `--layers`, `--extents` (2026-09-07)

`brief-render-3-query-surface.md`. The three questions a caller has to be able to ask before `render`
is usable, as options on `explain` rather than three new verbs. Everything below is what turned out to
be true while building it.

### `--extents` and `--fit` genuinely share one function, and making that true moved code

R-rnd3-9 asked whether the two share a function or merely agree today. Before this brief they merely
agreed — `src/Cli/Render.cs` held its own `LayoutExtents`, `SchematicExtents`, `SymbolBodyBox`,
`SymbolPinMarksSolved`, `FitZoom`, `LayerVisibility` and `WorldRect`, ~200 lines of measurement inside
a CLI verb. All of it is now **`src/Render/DocumentExtents.cs`**, below the firewall, called by both;
`Render.cs` keeps the viewport arithmetic and none of the measurement. The gate compares the two verbs'
`--json` numbers **as raw text**, so a difference of one ulp fails rather than passing an epsilon test.

**It hands back two boxes, and that distinction is the finding.** Some of what a frame paints is
measured in PIXELS at render time and has no world extent until a page size is chosen — a symbol pin's
name (a size with a floor) and a `RulerSizeMode.Fixed` ruler's readout (n screen points, so its world
width depends on the scale, which depends on the bounds). A zoom-independent verb cannot report those,
which is R-rnd3-10's own instruction for the symbol case. So:

- `LayoutBox` / `SymbolBox` — the geometry. **What both verbs now report as `extents`.**
- `LayoutFitBox` / `SymbolFitBox` — that box plus the room those marks need at this page. **What a fit
  is framed on**, and still exactly what RND-2 framed on.

This CHANGED `render --json`'s reported `extents` for a symbol, and for a layout carrying a Fixed
ruler: RND-2 reported the solved box there. Nothing gated it, and the new value is the better one —
"how big is the document" and "how big a page does it need" are two questions, and the second is
already answered by `viewport`. The gate asserts a fitted symbol page is genuinely WIDER than the
reported geometry, so the two boxes agreeing could never be mistaken for there being only one.

### `render`'s instance base directory was one level too shallow, silently

Found while moving the extents measurement, not looked for. `DrawLayout` computed
`Path.GetDirectoryName(Path.GetDirectoryName(full))` — the CELL folder — where the editor's own
`InstanceBaseDir` (which the canvas passes as `LayoutRenderOptions.BaseDir`) is the directory the
`.clay` itself lives in, the `layout/` sub-folder. An instance's `CellRef` is written by
`Path.GetRelativePath` FROM that directory, so every reference resolved one level too shallow and the
instance drew as a broken-reference placeholder rather than as its content — with nothing reported,
because an unresolved reference is an ordinary state.

RND-2's own gate could not catch it: its fixture has no instances, and the in-process call it compares
against copies the same arithmetic. It is now `CellHierarchy.BaseDirOfDocument`, named beside
`LayoutBaseDirOf`, whose remarks already described the trap from the other end.

### Gate 4 cannot be run against `counters.shapesDrawn`, and the field it IS run against changed

§5's gate 4 asks that `--layers`' per-layer `shapes` equal what `render --layers <that layer> --json`
reports as `shapesDrawn`. Measured on a hierarchical fixture — a top cell drawing one rectangle and
placing a 2x3 array of a cell drawing one more — `explain` says 7 and `shapesDrawn` says **1**. That is
not a disagreement to fix: `LayoutRenderResult.ShapesDrawn` counts the top-level shapes a frame issued
a draw call for, and an instance's interior is accounted in `InstancesDrawn` instead. The two count
different things by design.

The field that answers the same question is `render --json`'s `layers[].shapes` — and **that one was
top-level-only too**, from a private `foreach (var s in view.Shapes)` counter in
`ApplyLayerSelection`. So the two verbs would have reported 1 and 7 for the same layer of the same
document. Both now go through **`CellHierarchy.ShapeCountsByLayer`**, added for this, and the gate
compares them dictionary against dictionary: a disagreement means R-rnd3-6's "do not write a second
walk" was violated. `RenderLayerJson.Shapes` widened to `long` on the way, because a via field placed
in an array crosses `int` sooner than one expects.

**The count is visibility-INDEPENDENT**, deliberately, and that is not a contradiction of R-rnd3-6's
"what would be RENDERED". `RenderLayerJson.Shapes`' own remarks already say the field is "shapes on
that layer in the document, drawn or not — so an empty layer and an excluded one are two different
answers", and `visible` beside it is the other half. R-rnd3-6's sentence is about HIERARCHY: its own
continuation is "a layer used only inside a placed sub-cell is used".

`ShapeCountsByLayer` is not `OccupiedLayerKeys` with a counter, and the difference is worth stating: a
UNION dedupes on the cell folder because a cell reached twice contributes the same keys twice, and a
COUNT must not, because a cell placed twice draws its shapes twice. It multiplies by `Rows*Cols`,
dedupes on nothing but the DFS path, and carries a budget — a generated via field is a six-figure shape
count per placement, and a walk that quietly stopped counting would hand back a floor a caller would
read as a total. Exceeding it reports `explain.layers.count-truncated` and sets `truncated`.

### `WorkspaceScanner` was NOT moved below the firewall, and the rule it enforces was

R-rnd3-4 says `--cells` uses the workspace's own scanner so a folder the GUI hides is hidden here too.
Taking that literally means moving `WorkspaceScanner` (~950 lines) plus `ProjectTreeNode`/`NodeKind`
plus their `GeneratedCellStore` dependency out of `src/Ui` — a closure far larger than the sentence
predicts, for a listing that needs none of the tree's referenced-library, Known-Files or carried-subtree
machinery. `brief-render-0-overview.md` §4.1 says to report a closure bigger than measured rather than
absorb it, so:

- The **rule** moved: `src/Design/Workspace/ReservedFolders.cs` holds `.generated-cells` and `.git`
  and the two predicates over them. `WorkspaceScanner.IsReservedTreeDir` and
  `GeneratedCellStore.ReservedFolderName`/`IsUnderGeneratedCellsFolder` now delegate to it, so there is
  exactly one place either name is written down and the GUI and the CLI cannot drift.
- The **enumeration** is `CellLookup`, which already existed and whose own header says it is the answer
  `explain --cells` gives — it is what `render --cell` resolves through, so a cell this lists is a cell
  that verb can draw. A second enumeration would have been free to disagree with the one that draws.

`--all` lifts the generated-cells exclusion only; `.git` is never walked whatever it says, because it
is a history and nothing in it is a cell.

### There are no shipped example workspaces, so gate 1 builds its own

§5's gate 1 asks for the comparison "on the shipped example workspaces". The repo ships none — nothing
under `testdata/` carries a `.cws`, and every workspace in the test suite is built by the test that
uses it. The gate is therefore run on a fixture holding six cells whose primacy resolves five different
ways, one of them a folder deep and one under the reserved folder, with a **vacuity guard** on both
sides: an empty set satisfies both set assertions and proves nothing.

Its answer to the brief's "report any cell state that is not clean": there is nothing to report,
because there is nothing to look at. That is a fact about the repo rather than a clean bill of health.

**The comparison is against `--cells --all`, not the default**, and that is the honest direction:
`check`'s folder walk descends into `.generated-cells` — it is a folder like any other to a validator,
and a generated cell holding a defect is a defect — while `--cells` hides it. Comparing against the
default would assert the exclusion twice and the agreement not at all.

### `unit` is `design-units`, not `null`, and it is a deliberate departure

R-rnd3-8 asks for `unit: null` on a schematic or a symbol, "said to be dimensionless". RND-2 already
ships `"unit": "design-units"` in `render --json` for the same coordinates, with a test pinning it. Two
spellings of one fact across two verbs is precisely the drift this series exists to prevent, and `null`
is the LESS explicit of the two — it says the field was not filled in, where the string says the
coordinates are dimensionless. The gate compares the two verbs' `unit` field for equality, so they
cannot part company later.

### What the layer report cannot say, and a caller would want

The brief asks for the gaps. Three, all real:

- **A via spanning two levels is reported on one.** `ViaShape` carries `Layer` (the barrel) and
  `LandingLayer` (the pad), deliberately two fields — and `LayoutRenderer` draws the whole annulus on
  the barrel layer, in that layer's colour, never reading `LandingLayer` at all. So `shapes` counts a
  via once, on its barrel layer, and a caller asking "is the landing layer empty" is told yes about a
  layer a via field's pads are notionally on. `CellHierarchy.OccupiedLayerKeys` unions both keys and is
  right to, because it answers a different question; the count matches the renderer because R-rnd3-6
  says it must, and the two now differ on purpose with a comment saying so at both ends.
- **A layer distinguished only by its purpose is reported twice.** `LayerDef`'s identity is its
  `LayerKey` — the (layer, datatype) pair — and `Purpose` is a free-text field beside it. A technology
  that declares two `LayerDef`s with one key and two purposes gets two rows carrying the SAME shape
  count, in both this verb and `render`'s own layer report, because the count is keyed on the pair.
  Nothing validates against it and nothing this brief added could.
- **A layer the fallback palette invented is now reported, and marked.** A key the document draws on
  that the technology does not define is common after an import (`layout-view.md` §2.4) and it RENDERS,
  on the generated palette. Omitting it would report a document as drawing on layers it does not and
  hide the ones it does, so those rows are appended with `FallbackPalette.For`'s deterministic colour
  and its `L<n>/<d>` name. What a caller cannot tell from a row alone is which kind it is looking at —
  the tell is `technology`, and where nothing resolved at all `resolvedBy` names the palette outright
  (R-rnd3-7).

### `perLayer` covers the document's own shapes, and says so

An instance's extent comes back from `CellHierarchy.InstanceBbox` as ONE box for the whole placement.
Splitting it per layer needs a second walk, free to disagree with the first about what it measured, for
an answer that is genuinely about "where is my metal in this file". So `perLayer` is the top-level
shapes only, documented in the field rather than left for a caller to discover from a number that does
not add up.

---


## RND-2 — `circuitrf render` (2026-09-07)

`brief-render-2-render-verb.md`. One verb over a `.csch`, a `.csym` and a `.clay`, as `.svg`, `.pdf` or
`.png`. `src/Cli/Render.cs` draws nothing: it parses arguments, computes a viewport, refuses, and
reports, and every pixel comes out of the `CircuitRF.Render` the application draws each frame with.
Gate file `tests/Ui.Tests/Render/RenderCliVerbTests.cs`.

### Byte identity came out BETTER than RND-1 predicted, and the reason is worth knowing

RND-1 recorded that Skia's SVG device numbers its `clipPath` elements from a counter it does not reset
per canvas, and that this made in-process SVG byte comparison unavailable for the LAYOUT (`cl_3` vs
`cl_4`). **Across processes it did not bite at all**: the layout, the schematic and the symbol all came
back byte-identical, raw, and so did the PDF — no id normalisation, no date stripper, no exclusion of
any kind. Each process starts its own counter, and the first layout SVG a process draws lands on the
same number on both sides.

**That is a load-dependent property, not a guarantee**, and the gate is written accordingly: it
compares raw first and normalises Skia's ids only where the bytes actually differ, reporting when it
does. Other test classes in the same process emit SVGs and advance the shared counter, so a full-suite
run can legitimately push the in-process side off by one. An exclusion that stops being needed stops
being applied; one that is needed is named.

### `--detail full` needed two LOD knobs to grow the "off" branch the other six already had

R-rnd2-6 quotes `LayoutRenderOptions`' own contract — "a NEGATIVE value disables the tier outright,
which is how an export pins exact vector geometry" — and it was true of six knobs and **not** of
`LodPixelThreshold` or `MergeShapeCountThreshold`, which read `> 0 ? value : default`. A caller asking
for those tiers to be off got the DEFAULT instead: silently, and in the one direction where the mistake
produces a plausible picture of *less* geometry than the document holds. `LayoutRenderer` now has
`EffectiveLodPixelThreshold` / `EffectiveMergeShapeCountThreshold`, which answer `-∞` / `int.MaxValue`
for a negative. Nothing passed a negative before this verb, so no existing caller changed behaviour.

### `verticesEmitted`, and what it deliberately does not count

R-rnd2-6's whole claim is that `--detail` changes how much geometry comes out, and no existing counter
could be asserted against it: `ShapesDrawn` is unchanged (the same shapes are drawn) and
`PathsConstructed` is unchanged (the same paths are built). The new counter is on
`PathsConstructed`'s own terms — counted only where a frame counter is threaded, so the ghost,
selection, handle and marquee paths (which pass none) are excluded, and a shape served from
`LayoutPathCache` contributes nothing because nothing was built.

**A shape whose geometry is ANALYTIC contributes none.** A circle, a via annulus and a rounded rectangle
are Skia primitives with no vertex list to thin; inventing a tessellated count for them would report a
number the renderer never produced. A `Rect` contributes its four corners, which is literally what it
is. A `PathShape` contributes its CENTRELINE's vertices, not its stroked outline's — the outline is
generated, and counting it would report the stroker's fidelity rather than the document's.

### One file had to move below the firewall beyond RND-1's measured closure

`SvgFontNormalizer.cs`, from `src/Ui/Diagnostics` to `src/Render`. It is framework-free (string and
regex only) and it was already on every SVG path in the repository — the three clipboard exports, the
plot exporter and wBond's all pass Skia's output through `RepairPositionLists` on the way out, because
Skia writes each text run's per-glyph position list with a trailing separator that Firefox reads as
invalid and drops, putting every run a line above its baseline where the clip eats it.

Leaving it in `src/Ui` would have meant the headless SVG and the application's differed by exactly that
defect, in exactly the direction R-rnd0-2 forbids: correct in Chrome and Safari, unreadable in Firefox,
and reported as a success. `SvgPostPass` stayed — it is the docs generator's size pass and reaches
`System.Xml.Linq`, not a renderer's concern.

### `tests/Ui.Tests` now LINKS `CircuitRF.Cli` as well as launching it

Two of this brief's gates are about what happens inside ONE process and are unreachable by exec'ing a
DLL that answers one command and exits:

- **Gate 7 (cancellation).** A render cancelled through `RunHost`'s `RunControl` must exit 130 and leave
  no output file. There is no signal a test can send a child process that means "cancel at a work
  boundary".
- **Gate 5 (the layer-selection leak).** `TechnologyCache` hands back a shared instance, so flipping
  `LayerDef.Visible` on the resolved technology narrows every LATER render taken through that cache.
  Across two processes the defect is invisible *by construction*, so a two-process test of it would
  pass on the broken implementation.

`CircuitRF.Cli` grants `InternalsVisibleTo("CircuitRF.Ui.Tests")` and the project reference lost its
`ReferenceOutputAssembly="false"`. **Every byte-identity gate still launches the real DLL** — a
same-process call cannot show a difference only a second process can have, which is the whole reason
RND-1's own gate 2 was not constructible until this verb existed.

### The layer clone lives in `src/Design`, not in the verb

R-rnd2-8 asks for a CLONE. `TechnologyLayerSelection.WithVisibility` is beside `Technology` because it
is data manipulation on the design model, RND-3's `explain --layers` wants the same answer, and a second
copy would be free to disagree about what was actually drawn. **The copy is reflective rather than
written out field by field**: a hand-written copy is correct on the day it is written and silently drops
whatever is added to `LayerDef` afterwards, and the symptom would be a layer that renders differently
only when a layer selection is in force.

### Progress: measured, and the honest answer is "for `serve`, not for a terminal"

R-rnd2-10 says whether a render is slow enough to need progress is a measurement. Taken on a synthetic
board matched to `LayoutRenderDetail`'s own measured import (3,284 shapes, 764,032 vertices, 20 layers),
Release build, whole board in view at 1600x1200:

| format | `--detail` | wall | file | vertices emitted |
|---|---|---|---|---|
| svg | full | 1.44 s | 23.5 MB | 764,032 |
| svg | screen | 0.36 s | 6.2 MB | 375,996 |
| pdf | full | 1.22 s | 9.6 MB | 764,032 |
| pdf | screen | 0.51 s | 2.6 MB | 375,996 |
| png | full | 0.55 s | 1.4 MB | 764,032 |
| png | screen | 0.35 s | 1.0 MB | 375,996 |

Reading and parsing the 10.9 MB `.clay` and measuring its extents is ~0.26 s of every row (measured by
windowing to a region containing nothing); process start is 0.02 s. So **the worst case is under a
second and a half and nothing here needs a bar.** What the `RunControl` wiring buys is CANCELLATION for
`serve` — a client that gave up at a timeout and retried would pay for the run twice — and the four
stage labels, both of which come free through `RunHost` with no plumbing in the verb.

**The vector/raster asymmetry is the number a caller actually needs.** An undecimated SVG of that board
is 23.5 MB and its `--detail screen` counterpart is 6.2 MB; the PNG barely moves, because a raster's
size is set by its pixels and not by the geometry behind them. `--detail screen` is the answer for
anyone who wants the picture, `full` for anyone who wants the geometry, and RND-5's docs must say so
plainly.

**The 2.03x vertex ratio is this fixture's, not a universal one.** The brief's 7.6x comes from a real
6-layer import decimated at half a device pixel; the ratio tracks how many stored vertices fall on one
device pixel, so it rises with the board and falls with the page. What the gate asserts is the counter,
not the ratio.

### What has no CLI spelling, and what has one that could be argued with

Reported rather than absorbed (§8):

- **No spelling, and correctly so:** the EM mesh overlay, the plan-view current density, the reference
  planes, the DRC markers, the PCell pin overlay, the snap glyph, the selection chrome. Every one of
  them is a view of something that is not in the document — a run result or an editor state — and
  `LayoutRenderOptions` defaults each to off, so this verb never sets one and draws none by
  construction. The clipboard export's DRC-marker and mesh flags exist because a person had the panel
  open; nothing headless has one.
- **No spelling, and it is a real gap:** `ForceMergeTier`, `InstanceRasterMaxDevicePixels` and the other
  five tier knobs are reachable only through `--detail`'s three settings. That is deliberate — a knob
  nobody can explain is a knob nobody will set correctly — but a caller chasing a specific tier has no
  way to. If one is ever wanted, it belongs as a named `--detail` mode, not as seven flags.
- **Has a spelling and it is worth stating why:** `--no-rulers`. Rulers default ON because `ShowRulers`'
  own remarks say a ruler is document CONTENT rather than overlay state — it is in the `.clay` and an
  export that dropped it would contradict `layout-view.md` §9B.9. The flag exists because a picture for
  a report is a case where the measurement is chrome, and it is the one overlay-shaped thing a caller
  can turn off.

### Two smaller things

- **`ThemeResolver.Resolve` cannot fail**, so R-rnd2-9's "a theme name that resolves to nothing is a
  refusal" had to be answered BEFORE the chain runs: the resolver's last step is `ColorTheme.BuiltIn`,
  which always succeeds, so a misspelling would otherwise resolve to a differently-coloured picture and
  be reported as a success — the same silent fallback R-rnd1-5 removed from the resolver itself. What
  the verb adds is the answer to "did a step actually match", which the resolver does not return, and it
  reports which one did in `theme.resolvedFrom`.
- **The extents are the PAINTED box, not the stored one.** A `LabelShape`'s stored bbox is its anchor —
  a point — an EM port paints a width bar and an arrow at the conductor end, and an instance's extent
  resolves through its cell. Framing on the stored boxes is what cropped a pasted page's ports off the
  bottom (`LayoutClipboard.ComputeSelectionBounds`' header records it), and the rulers still need
  exactly two passes for the same reason that method does.

---

## RC-7 — `history commit` and `history versions` (2026-09-06)

`brief-revision-control-7-commit-and-history.md` R-rc7-22, §5.3d. Two nouns on the existing verb, each
calling the `src/Design` function the GUI's own command calls. Gated by
`tests/Ui.Tests/Revision/CommitAndHistoryTests.cs`, which compares the commit the process makes against
the one the window's command makes — same tree, same message, byte for byte.

**Four refusals, and three of them are circuitRF's rather than git's.** Held, off and
no-history-here are states circuitRF decided; only the fourth (nothing to commit) comes out of the
recording path, and it is decided by comparing trees rather than by matching git's English. Nothing here
translates a git failure locally — R-rc7-19/R-rc7-20 mean a translation living only in one surface is a
translation the other does not have, and the gate scans for a read of `StdErr` in either.

**`versions` is a different list from `list`, and the separation is the point** (R-rc7-9). `list` is the
safety net; `versions` is the narrative. An off period appears in `versions` as its own row carrying its
dates and its reason, never as an ordinary interval between two versions.

**`--changes <version>` reports at the granularity of DOCUMENTS.** Which cells differ, not which lines
inside one — that is the design layer's question, and a per-document comparison needs a reader per
document type, which is a different piece of work.

## AUT-1 — `--json`: structured results and structured failures (2026-09-05)

`brief-automation-1-structured-output.md`. Every verb gained `--json`; nothing that does not pass the
flag changed. The contract is now `docs/design/cli.md` §3.2.

### The gate that mattered, and what it actually proved

§6.1 asked for a golden-file test per verb asserting the human output is byte-identical before and
after. It is `tests/Ui.Tests/Cli/CliStructuredOutputTests` with committed bytes in
`tests/Ui.Tests/Cli/golden/`.

**It was also run the other way, against a pristine `git worktree` of HEAD, before any golden was
committed** — every verb, plus twelve `convert` refusal paths and one real DXF→GDSII conversion,
comparing stdout, stderr AND exit code. Result: **byte-identical everywhere except two absolute
paths** (the repository root inside a resolved SnP reference, and a scratch GUID in a temp
directory), neither of which is a behaviour difference. That is the evidence that moving
`PrintLoadpullGrid`'s and `PrintPursuitOptima`'s selections into `LoadpullResultSummary` moved no
number.

**The goldens carry `<ROOT>` and `<TMP>` placeholders, not paths.** A netlist's resolved SnP
reference and an explicit `-o` both print absolute paths, and a public repository must not carry
anybody's home directory. Two substitutions are applied to both sides and nothing else is
normalised.

### What "never a second computation" had to mean mechanically

R-aut1-1 is easy to agree with and easy to violate by accident, because the violation looks like
ordinary code. The console's loadpull table was making four decisions that are not formatting:

- each grid point is read at its **last converged, non-tickle drive step** — not a fixed index,
  which would mix compressed and uncompressed points in one column;
- **both cube spellings** are read (`Pout_dBm`/`Pout`, `Gt_dB`/`Gt`, `Efficiency`/`DE`), because a
  plain loadpull is Enriched and a pursuit's follow-on grid is not;
- efficiency's **scale follows from which spelling was found** (percent vs fraction);
- the grid axis is located **by name** (`gridPoint`), never by position.

Writing those a second time for the JSON would have produced two answers to the same question the
first time anyone changed one. They now live once, in `RfCore.Loadpull.LoadpullResultSummary`, which
returns values; the console formats them and the document serializes them. **The scale is carried,
not applied** — the summary hands out the engine's own number plus the factor the terminal
multiplies by, so a column headed `DE%` and a document field holding `0.6961…` are the same
measurement and say so.

### The two verbs whose result could not be shared, and why

Reported rather than absorbed, per the brief's own instruction:

- **`elab` has no `result`.** It is a development dump of the elaborated netlist — components,
  nodes, resolved parameters — and there is no `DataSet` anywhere in its path. Giving it one would
  mean inventing a result shape, which is a format decision this brief does not get to make. Its
  document carries `status`, `exitCode`, `diagnostics` and `outputs`, and that is honest.
- **`dc` HAD no shared selection, but it did have a DataSet.** `NonlinearDcEngine.Run` returns a
  `DcResult`, not a `DataSet`, so the console table reads node voltages and probe currents straight
  off it. `DcResultPacker.Pack` — the packer the GUI and `ParametricSweepEngine` already use — turns
  that same `DcResult` into the canonical cubes, so the document uses it. The two read one object
  and cannot disagree about a number; what they do not share is a selection **because the table has
  none** (it prints every node and every probe). Worth stating plainly rather than claiming a
  sharing that is not there.

### `--json` guarantees "nothing else on stdout" structurally, not by convention

The moment the flag is parsed, `Console.Out` is replaced with `TextWriter.Null` and the real stdout
is held in `JsonRun` until the document is written to it. A verb prints its table exactly as it
always did, into a sink. **This is why a later-added `Console.WriteLine` cannot leak into a caller's
parser** — a rule that every printer has to remember is a rule that eventually gets forgotten, and
the failure mode is a caller's parser breaking rather than a test going red.

`--json`, `--only` and `--group` are pulled out of the argument list before dispatch, the way
`--kits` already was. That is not only tidiness: `convert` refuses an unrecognised `-`-prefixed
argument, so a flag left in the list would have made `circuitrf convert x.dxf -o y.gds --json` a
usage error.

### NaN is not a JSON number, and pretending otherwise loses a fact

A loadpull grid genuinely contains NaN wherever a point never converged, and the console prints an
em dash for it. JSON has no number for NaN. The document uses System.Text.Json's
`AllowNamedFloatingPointLiterals`, so it arrives as the string `"NaN"`. **Dropping the key or
substituting a zero would turn "no measurement" into a measurement**, which is the same class of
error as printing `Pout=0 dBm` beside "DID NOT converge" — a mistake the pursuit printer had already
been written to avoid.

### Diagnostics: what was converted, and what is deliberately still prose

R-aut1-7 asked for counts rather than a guess.

| | Count |
|---|---|
| Ids in `src/Cli/CliDiagnostics.cs` (new) | **50** |
| Un-diagnosed refusals left in `Program.cs` / `LayoutConvert.cs` | **0** |
| Pre-existing ids elsewhere (`EmDiagnostics` 8, `FileAccessDiagnostics` 3) | 11 |
| Still prose: `Messages.Warning`/`Error` call sites in `src/Ui` | **296**, of which **124** launder an exception's `.Message` |
| Still prose: `Console.Error` sites in `src/Engine`, `src/Core`, `src/Design` | **41** |

Every `Console.Error.WriteLine` that precedes a `return 1` in either CLI file now goes through a
`Diagnostic`. What remains on those two files' stderr and is not itself a diagnostic is **usage text
and continuation lines** — the "Inferred: …" evidence under the Excellon refusal, the offending
coordinates under the GDSII overflow refusal, the pending layer mappings under the Gerber refusal.
Each of those already has a recorded diagnostic beside it carrying the sentence and the typed
values; the extra lines are detail for a human reading a terminal.

**Warnings authored deep in the engines are wrapped, not re-authored.** `elab.note`,
`elab.warning`, `em.run.note`/`warning`/`error`, `cli.measurement.failed` and `cli.worker.output`
each take an already-finished English sentence and give it an id. That is R-aut1-7's own last clause
and it is the right trade: an id is enough to filter, group and deduplicate on, whereas inventing
typed arguments would mean parsing the prose back apart — which is precisely what the coded form
exists to stop anyone doing.

### Brief premises that did not survive contact

- **§4 says `EmRunResult`'s "three lists … already carry a `Diagnostic` alongside the string".** They
  do not. `Notes`, `Warnings` and `Errors` are `IReadOnlyList<string>`; only the top-level refusal
  has a coded twin (`EmRunResult.Diagnostic`, non-null for every non-Ok status). The refusal is now
  read structurally — that part of the brief was right and the CLI had genuinely been discarding it
  — and the three lists are wrapped argument-free with an id naming which list they came from, which
  is the distinction the split exists for in the first place.
- **§4 and the architecture note both say adoption is "6 construction sites in 2 files".** It is 11
  across two files (8 in `EmDiagnostics`, 3 in `FileAccessDiagnostics`). Immaterial to the work, but
  the number is quoted in two documents and someone will check it.

### `convert`'s "Input not found" is not `sparam`'s "File not found"

Merging the two into one diagnostic changed one line of stderr and the before/after comparison
caught it immediately. It stays separate on its own merits as well: **`convert`'s input may be a
folder** (a Gerber file set), so "Input" is the accurate word and always was.

### An id minted from a caller's string is not a contract

The first cut had one `Convert(kind, text)` factory building `"convert." + kind` at each call site.
That is a set of permanent contracts nobody can enumerate, review, or hold still — and R-aut1-8 says
an id is chosen once. They are 29 named factories now (14 more scoped `cli.`, plus 7 forwarding and refusal ids), and
`CliStructuredOutputTests.DiagnosticIds_AreTheCommittedSet_UniqueAndCaseDistinct` asserts the whole
list against committed bytes, so adding one tells you to record it and renaming one tells you that
you have made a new diagnostic.

That test reads the ids **out of the source file** rather than by reflection, because
`tests/Ui.Tests` references `src/Cli` with `ReferenceOutputAssembly="false"` — the CLI is launched
as a process, not linked, so its types cannot be bound against. The regex anchors on
`DiagnosticSeverity.` following the literal, not on line layout: the first attempt anchored on
"a string alone on a line ending in a comma" and silently missed every single-line factory.

### A test trap worth remembering

`RunCli(params string[] args)` beside `RunCli(string? env, params string[] args)` compiles, and
`RunCli("hb", "file.cnl")` binds **`"hb"` to `env`**. Ten tests failed with
`No such command: 'file.cnl'` before the cause was obvious. A `params` overload whose extra leading
parameter is also a string is not an overload; it is a trap. The culture variant is named
`Launch(culture, …)` now.

---

## AUT-3 — `new workspace`, `new cell`, `import part` (2026-09-05)

`brief-automation-3-authoring-verbs.md`. Three verbs that create a correct INITIAL document with no
display attached, plus the extraction that lets them exist. The contract is `docs/design/cli.md` §2
and §9; the capabilities themselves are recorded in `src/Design/RESOLVED.md`.

`src/Cli/Authoring.cs` is ~590 lines and none of them create anything. That is the point: every verb
calls the function the GUI's own command calls, so what is left in the CLI is argument parsing,
refusals and reporting. The gate is `tests/Ui.Tests/AuthoringCliVerbTests` (25 tests, ~2 s), written
the way `EmCliVerbTests` is — the real CLI as a process, compared byte for byte against the
in-process call.

### The brief's stated `--views` default contradicts its own reason, and the reason won

R-aut3-8 says the default is "symbol and schematic, matching the GUI's own New Cell". **The GUI's New
Cell creates a schematic and nothing else** — `NewCellAsync` calls `CellFolder.CreateCellFolder` and
then `CreateAndOpenSchematicFileAsync`; the symbol and layout sub-folders are made and left empty
(R-cc-1: "a New Cell always creates that cell's primary schematic", singular). So the stated value
and the stated reason disagree, and the reason is the rule the whole brief runs on — R-aut3-3's "a
headless default that differs from the dialog's is a second product". **`--views` defaults to
`schematic`.** `--views symbol,schematic` is one flag away for a caller that wants both.

### `import part` has no session, so the layer install cannot be copied — only reported or written

The GUI's `ApplyImportToTechnology` installs the part's new layers into the SESSION's live technology
and says in as many words that **nothing was written to disk**; the user keeps them by opening the
technology and saving it. Headless there is no session, so "do what the GUI does" is not available:
the honest options are to report them or to write them, and which one a caller wants is not
something to guess. So they are **always reported**, and `--add-layers` writes them into the
resolved `.ctech`. Reporting is not optional — a layer silently dropped is the trap
`src/Design/RESOLVED.md` already records for `convert`.

**A null destination technology reports NO layers as new**, which is the same trap from the other
side: with nothing to reconcile against, `LayersToAdd` comes back empty and the part's layers land
with numeric keys and no names. The GUI is in exactly that state with a technology-less workspace, so
the verb matches it and says so (`import.no-technology`) rather than inventing a technology the GUI
would not have had.

### `--variant` cannot be the candidate's Location, which is what the chooser shows

The brief's `--cell N` / `--variant V` map onto the chooser dialog's two distinguishing columns, and
the obvious reading of `--variant` is `ComponentCandidate.Location` — the folder a candidate came
from, which is what separates "one part written out once per target format". **That reading is inert
on the ordinary case.** `testdata/component-samples/widget9` holds one part as three candidates in
ONE folder — a `.kicad_sym` symbol, a `.lib` symbol, and the bare land pattern — so all three share a
name, a Location of `""` and a family. Neither flag could name one.

`--variant` therefore takes the Location when there is one and otherwise the **extension of the file
the candidate begins at** (its symbol file, else its first footprint) — which is the same file
`DisplayName` is taken from, so nothing new is invented. `--list-parts` prints exactly that key in
its second column, and the ambiguity refusal prints the whole listing.

### Two things the argument shape had to settle

- **`new workspace <dir>` with no `--name` treats `<dir>` as the workspace itself**, and with
  `--name` as its parent. Both are how a person types it and the flag says which was meant.
- **`--tech none` is how a caller asks for the dialog's own "None" row.** An absent `--tech` cannot
  mean "none": R-aut3-3 binds it to what the combobox opens on, which is a real technology.

### 29 new diagnostic ids

`new.*` (14) and `import.*` (15), all recorded in `CliStructuredOutputTests`' committed list. That
list is asserted in **ordinal order over the whole set**, so a new group cannot simply be appended —
the first attempt appended `import.*` after `lp.export.no-surface` and failed on position 49.

---

## AUT-4 — `check` and `explain`, and the DRC engine below the firewall (2026-09-05)

`brief-automation-4-check-and-explain.md`. Two read-only verbs that close a headless client's loop.
The contract is `docs/design/cli.md` §10; this records what turned out to be true while building it.

### The finding that changed the design: a `.csch` goes through the `.cnl`

**The first `check` reported four errors the application does not have**, on four schematics in the
owner's own workspace: *"elaboration failed — Unresolved name 'on' in scope 'global'"*.

The cause is an asymmetry between two readers that was already documented in two places, each
contradicting the other. A Tuner's `BiasTee` parameter is stored bare (`on` / `off`), and:

- `ComponentTypeRegistry`'s `BiasTeeOptions` says the value is committed bare because "`CnlReader` is
  what quotes it on the way to the elaborator";
- `HarmonicaSchematicExport` writes `"\"off\""` QUOTED, with a comment saying a bare `off` "resolves
  as a variable name and elaboration fails with Unresolved name 'off'".

Both are right, about different paths. `CnlReader` quotes a bare word; `NetExtractor` does not
(`AsLiteralExpression` exists and is applied only to a KIT's fixed parameters). And the GUI's Simulate
does not hand extraction straight to the elaborator — `WorkspaceViewModel.WriteNetlist` writes a
`.cnl` and `SchematicRunService.Prepare` reads it back, so every schematic parameter is laundered
through `CnlReader` on the way. Verified in both directions: `testdata/Hero3/hero3.cnl` carries
`BiasTee=on` and `circuitrf elab` resolves it without complaint.

**So the round trip is load-bearing, and `src/Cli/CircuitSource.cs` performs it — in memory, since
R-aut4-6 forbids writing.** This is R-aut4-2's rule in the mirror: a rule that lives only in `check`
is a rule the GUI does not enforce, and a rule `check` applies that the GUI does not is just as bad.
With the round trip, those four schematics check clean.

**Not fixed here, and worth deciding separately:** the two comments above still contradict each
other, and a `.csch` handed directly to `Elaborator` by any future caller will hit the same wall.
Either `NetExtractor` should apply `AsLiteralExpression` to a Tuner's string-valued parameters as it
does to a kit's, or `ComponentTypeRegistry` should commit them quoted as `HarmonicaSchematicExport`
already does. Both change what new schematics write, and neither repairs an existing file, which is
why this brief left it alone.

### R-aut4-3: the DRC engine moved, and the closure really was free

`src/Ui/Layout/Drc` → `src/Design/Layout/Drc`, plus `src/Ui/Layout/Assembly` (the `.wasm` rule-file
model) → `src/Design/Layout/Assembly`. **The whole move produced five compiler errors**, and none of
them was a coupling: a stale `using CircuitRF.Ui.Schematic` in `WasmPersistence`, an `AtomicFile`
that resolves to `CircuitRF.Design.Cells`' one anyway, and three fully-qualified `Ui.WBond.WBondSnap`
calls. `tests/Ui.Tests` passed **unchanged**, 12,053 of them.

**Two files stayed, on purpose, and neither is the engine:**

- `DrcRunReport` — posts a run's verdict to the Messages panel. It takes an `IMessageSink`; that is a
  UI surface, not a design rule.
- `WBondWireClearance` — reads the built-in wire clearance from the per-USER preferences file. The
  engine already takes the number as `DrcRunSettings.WireClearanceNm`, so the preference is the
  GUI's to read and the default (circuitRF's own half a mil) is the right answer for a caller with no
  user to ask.

**One thing had to move that the brief did not list**: `WBondClearance` converts a layout into
nanometres through `WBondSnap.ToNm`, and `WBondSnap` cannot cross — it needs `LayoutSnapQuery` and
`SnapFeatureKind`, which ARE the layout editor. The two-line integer pair moved to
`LayoutUnits.NmToDbu`/`DbuToNm` instead and `WBondSnap` forwards to them, so there is still exactly
one implementation — the property `WBondClearance`'s own header depends on, having shipped broken
twice already from a second copy. **The arithmetic is unchanged, `double` and all.** Re-deriving it
in `decimal` beside `LayoutUnits`' other pair would be more exact past 2^53 and would also change
measured clearances, which is a numeric change smuggled in under a file move.

**26 allow-list entries, moved not authored.** `UserFacingTextGateTests` fires on user-facing text
below the firewall, and the `.wasm` predicate parser's messages are user-facing text: they are what a
person who wrote a bad rule expression reads. They are listed in
`tests/Firewall.Tests/user-facing-text-allowlist.txt` under a dated "moved, not authored" heading
rather than converted, because R-aut4-3 moves whole files without reshaping them and converting 26
parser messages under cover of a file move is the change nobody could review. They are also the one
family where a plain sentence is nearly defensible — a parse error already carries the offending TEXT
and a character POSITION, which is the typed half a `Diagnostic` would have added.

### `SelectTop` had to become a function that returns a decision

`explain --analysis` reports what chain selection would do WITHOUT doing it, and the old `SelectTop`
had no account of itself beyond two `Console.Error.WriteLine` calls. It is now
`ChainSelector.Select`, returning `Selected` / `Candidates` / `Requested` / `PromotedFrom` / `Why`,
and the CALLER writes the sentence — the run verbs write exactly the two they always wrote (R-aut0-3
holds: stderr is unchanged character for character), `explain` writes none and renders the same facts.

**One honesty problem surfaced immediately.** `SelectTop` ends `return owner ?? named`, so
`lp -a HB1` hands back HB1 — an HB, to the loadpull verb. The first `explain` reported that as "lp
dispatches HB1", which describes a run that cannot happen. It now counts a selection only when the
chain it picked bottoms out in that verb's own base analysis. The behaviour of the RUN verbs is
untouched; what changed is what `explain` claims about them.

### What `check` needed and no existing validator provided — three gaps

R-aut4-2 says a finding with no validator behind it is the most valuable thing this brief can turn
up. Three:

1. **"Does this document declare a runnable analysis at all?"** exists exactly once, as a private
   pair inside `SchematicRunService.Prepare` (`src/Ui`, above the firewall): a typed analysis, OR a
   RAW `analysis … type=sparam` directive, which never becomes a typed one. `check` could not call it
   and `CircuitSource.DeclaresARunnableAnalysis` restates it. **It is worth pulling down beside the
   netlist model** — it is the GUI's own `RunStatus.NoAnalysis` test and nothing about it is a UI
   concern. Asking `ChainSelector` instead is NOT equivalent and the first attempt proved it: chain
   selection is per KIND, so a bench declaring only an S-parameter sweep has no HB chain and every
   S-parameter and DC document in the tree was warned about.
2. **A `.cws`'s own reference lists.** `LibraryRefs`, `KnownFiles` and `DefaultTechRef` are shown as
   warning nodes by the project tree, but that rule lives in the tree's view models rather than in a
   validator, so `check` tests existence itself. A `WorkspaceValidation.Analyze` returning typed
   problems the way `TechValidation` does would serve both.
3. **Unconnected nets have no validator anywhere.** The brief's §1 lists them among the soundness
   questions and nothing in the tree answers one: elaboration numbers nodes and `NetExtractor`
   treats an unconnected pin as ground "for safety" (`NetExtractor.cs:1730`). `check` reports
   nothing about them, deliberately — inventing the rule here would have been the thing R-aut4-2
   forbids.

### §5.2 — the shipped documents, in full

| Tree | Result |
|---|---|
| `src/Ui/resources/schematic-templates` (4 `.csch`) | **clean** — 0 errors, 0 warnings |
| `src/Ui/resources/doc-schematics` (4 `.csch`) | 0 errors; **1 warning**, `Inline_Value_Editor.csch` declares no analysis — which is correct, it is a documentation figure |
| `src/Design/resources/technologies` (5 `.ctech`) | **clean at `--severity warning`** |

Both schematic trees are asserted in `CheckAndExplainCliVerbTests.ShippedSchematics_CheckClean`, so
nobody has to remember to look.

The repository ships no example WORKSPACE — `circuitRF_demo/` is the owner's own tree and is not
committed — so it was checked as evidence rather than as a gate: **30 documents, 0 errors, 2
warnings.** Both warnings are real states the application would also show (one cell declares no
analysis; one cell's symbol sub-folder holds two files with no primary chosen).

### §5.7 — the measurement, and where the time actually goes

Debug build, warm, wall clock including the ~35 ms process start.

| Input | Time |
|---|---|
| 30-document workspace, layouts empty | **0.20 s** |
| 4 documents, one 5,000-shape layout, spacing rule, no violations | **0.45 s** |
| the same 5,000 shapes with 19,577 violations | **0.95 s** |
| 20,000 shapes with 79,154 violations | **4.9 s** |

**Fast enough to call after every edit, and the cost is DRC, not the walk.** Reading and elaborating
30 documents is a fifth of a second; a single layout with real geometry is more than all of them
together, and the violation COUNT costs as much as the shape count because every violation is
rendered and recorded. A design with thousands of outstanding violations is not the case to optimise
for — it is the case to fix — but a caller checking a large board on every edit should point `check`
at the document it is editing rather than at the workspace.

No timing test was added (`feedback-no-new-timing-benchmark-tests`): a wall-clock assertion measures
the machine and flakes.

### `convert`'s carry-through, which was asked for and turned up three real gaps

`convert` answered `--json` from AUT-1, but three things a caller needs reached only the terminal.
Each was found by RUNNING the verb, not by reading it:

1. **`--list-cells` produced an empty document.** The listing goes to stdout, which `--json`
   replaces — so the one invocation whose entire result is a list answered with `"diagnostics": []`.
   Now `convert.cell.listed`, one per cell.
2. **The import's own notes were not in `diagnostics`.** "1 × unfilled zone … not imported", "F.Cu→
   added", "the technology's stackup was left EMPTY" — these are how a caller learns what a
   conversion DROPPED, and a `--json` consumer was reading a report that omitted the losses. Now
   `convert.note`, forwarded argument-free like the other engine-authored sentences.
3. **The minted `.ctech` was not in `outputs`.** Headless there is no workspace to graft layers onto,
   so an import writes a technology of its own — and the cells it produced reference it by RELATIVE
   PATH, so a caller that took the cells and not that file has a design whose layers resolve to
   nothing. It is now reported **exactly when it survives**: the target was `clay`, or `--keep-cells`
   named somewhere. Reporting a path that is about to be deleted with the scratch directory would be
   worse than reporting nothing, which is why `MintTechnology` records it and `Run` decides.

The fourth carry-through is in the other direction and is in `check`/`explain`: an interchange file
is classified through **`convert`'s own `DetectSource`** — including its content sniff, the only thing
that can name a Gerber or Excellon file — rather than through a second table. A GDSII file `convert`
can read is reported as interchange, never as something circuitRF does not handle.

### Smaller things worth keeping

- **`check`'s findings go to stderr, not stdout.** They are not a RESULT (`cli.md` §3.1); stdout
  carries the one-line tally the way every other verb's stdout carries its table. That is also what
  lets `check … --json` put the findings in `diagnostics` with nothing else in the way.
- **A waived DRC violation is reported as a NOTE, not at the rule's own severity.** §9A.1 requires
  waiving to be "persisted, and visible", and counting a waived violation against the exit code would
  make a fully-waived design fail CI forever.
- **`explain`'s three questions are refused together rather than ordered.** `--expr`, `--analysis`
  and `--ref` ask different things and a document answering two would need a precedence nobody stated.
- **`--severity` decides only the exit code.** Warnings are always reported. R-aut4-5 is explicit
  about why: a check that hid warnings to keep the exit code clean makes the exit code useless.
- **`NameValidator`'s finding is unreachable on Windows and the test says so.** Every name it rejects
  is a name Windows itself refuses — that is why the rule exists — so the broken fixture cannot be
  created there. `NonWindowsFactAttribute` skips WITH A REASON rather than asserting something weaker.
- **A kit part that does not resolve is a WARNING naming the registry, not a missing-cell error.**
  A `pdk://` reference lives in a registry the GUI populates when it opens a workspace and nothing
  populates headlessly; `check.ref.kit-not-loaded` says that. Reporting every part in a PDK design as
  a missing cell folder names the wrong repair and is exactly the noise that stops a check being run.
- **43 new diagnostic ids** — `check.*` (28), `explain.*` (13) and `convert.note` /
  `convert.cell.listed` — bringing `CliStructuredOutputTests`' committed list to 122. That list is
  asserted in ordinal order over the whole set, so a new group cannot be appended.

---

## AUT-5 — `circuitrf serve`, and `circuitrf read` (2026-09-05)

`brief-automation-5-protocol-adapter.md`. One protocol adapter, a parity gate, and the one capability
gap building it exposed. `docs/design/cli.md` §11 is the design; this is what turned out to be true.

### The adapter calls the verb, and that decided the shape of the whole change

R-aut-13 says nothing may be reachable through the server that is not reachable through the CLI, and
vice versa. There are two ways to satisfy it: build a second caller of the capability layer and write
a test that compares the two, or **make them one caller**. The second is strictly better here,
because the first is a second copy of the wiring — which agrees with the original right up until one
of them is edited, and the test then tells you a year later.

So a tool call becomes an **argument vector** and is handed to `CliEntry.Run`, the same function
`Program.cs` hands the real command line to. The parity gate then compares two documents that came
out of one function, and a drift between the adapters is not a thing that can happen.

**What that cost: `Program.cs`'s top-level statements and its 40-odd static local functions moved
wholesale into `CliEntry`.** A local function of a top-level program is a member of `<Main>$` and is
private to it; `public partial class Program` makes the generated CLASS accessible but not those. The
move is mechanical — the functions were already `static`, so they became static methods unchanged —
and `Program.cs` is now three lines.

### Three pieces of state outlived the single invocation the CLI was written for

Calling `Run` twice in one process is not what any of this was built for, and each of the three was
found by running the server rather than by reading the code:

- **`JsonRun`'s collectors.** A second document carried the first call's diagnostics and outputs — a
  stale success a caller cannot tell from a real one. `JsonRun.Reset()` now runs before every call.
- **The device-worker log subscription is an EVENT.** A second `Run` subscribed a second handler and
  every worker line printed twice, which reads as the worker having said it twice. Hooked once.
- **`ExternalDeviceRegistry.AddResolver` has no remove.** The same `--kits` folder set would stack an
  identical resolver per call; the folder sets already added are remembered.

None of the three is observable from a command line, and all three would have been observable to a
client as something wrong with circuitRF rather than with its adapter.

### The capability gap: nothing could hand a file back

**Reported as the brief asks, because by R-aut-1 it is a capability gap and should have been caught
in an earlier brief.** The tool surface §3 specifies has a `read` in it — "read a document or a
result file back" — and there was no verb behind it. A tool with no verb is exactly the privileged
adapter R-aut-13 forbids, and the parity gate for it could not have been written at all.

It is now `circuitrf read`, exposed on **both** adapters in the same commit. It adds no logic: a
`.npy` goes through `DataSetImporter`, a Touchstone through `TouchstoneIO` + `DataSetBuilder.FromSnp`
— the same pair `DataSourceEntryViewModel` uses to put a file into the Data Display's source library
— and one of circuitRF's own documents comes back as its own bytes. `ResultPayload` gained one field,
`document`.

**Verbatim, not re-serialized**, and that is a decision worth recording: the formats ARE the
interface, so a client that just wrote a `.csch` needs the file back, not a round trip through a
reader and a writer that would differ from disk wherever the reader is lossy — invisibly.

Two things `read` refuses rather than guessing at: a **directory** (what a workspace holds is what
`check` and `explain` answer, and walking one returns an unbounded document nobody asked for), and an
**interchange file**, which is refused *naming `convert`* — half those formats are binary, and
handing back a GDSII stream as a JSON string is an encoding decision this verb has no business
making.

### Every stdout leak §5.2 found: none — and why that is not luck

The audit runs every tool the server exposes, including a real EM run and a DC run whose device model
lives in a second process, with stdout captured, and asserts every line of it is a protocol frame.
**Nothing leaked.**

That is structural rather than fortunate. `JsonRun` already replaced `Console.Out` with a sink the
moment `--json` was parsed — for its own reason, R-aut1-3 — and `serve` replaces it again at startup
before a single capability runs, so a stray `Console.WriteLine` on a path nobody thought about has
nowhere to land. The three chatterers the brief names (`PrintWorkerOutput`, the
`ProcessDeviceWorkerTransport.Logged` hook, `EmProgressToStderr`) were already on stderr and are
untouched.

**The one thing a `Console.Out` redirect cannot cover is a CHILD process inheriting the real stdout
handle**, and that was checked rather than assumed: every process this program starts — the device
worker, the PCell host, the Python interpreter probe, the updater — sets
`RedirectStandardOutput = true`. Four call sites, all of them. A future one that does not would leak
into the protocol stream, and the §5.2 audit only catches it if the new launcher is on a path the
audit exercises.

`--json` on `serve` itself is **refused**, not silently ignored: it would have captured the very
stream the framing needs, and two writers on one stream is precisely R-aut5-2's failure. It is
refused **after** the argument and root checks, not before — so `serve --root <missing> --json` still
answers with a document carrying `serve.root.not-found`, the way every other verb's refusals do
(R-aut-7). Only the case where the server would actually start has no document to give, because from
that point stdout belongs to the protocol.

### Root confinement: the escape a string comparison cannot see

`../` and an absolute path outside the root are both easy. The third is not, and the first
implementation had it wrong: **`ResolveLinkTarget` answers about the item you call it on**, so asking
it about `<root>/link/file.cnl` reports "not a link" — the *file* is not one, the *directory* above it
is — and the escape goes straight through a comparison of resolved strings. The path is now walked
from its root downward with every component resolved in turn. The test that caught it is the symlink
case in `APathLeavingTheRoot_IsRefused_NamingTheRoot`, which failed on the first run.

Both sides are resolved, not just the candidate: on macOS `/tmp` is itself a link to `/private/tmp`,
so resolving only one side refuses perfectly legitimate paths.

A non-existent path — an output file — resolves its deepest existing ancestor and re-appends the
tail, which is the only thing that can be done and is what the check has to mean anyway: a file is
created inside the directory it lands in.

### Where the parity test could not be written exactly as specified

**Two normalizations, and both are properties of the operation rather than of the adapter.** The
brief allows "only what is legitimately variable (a write timestamp, as `EmCliVerbTests` already
exempts for provenance)"; these are the analogues.

1. **The adapter resolves paths** — that IS R-aut5-8's confinement — so the document records an
   absolute path where a CLI invoked with a relative one records the relative one. The CLI side of
   each comparison is therefore given the resolved path: the same path, spelled the way the server
   had to spell it.
2. **A verb that CREATES something cannot create it twice.** AUT-3's R-aut3-6 refuses to overwrite an
   existing workspace, correctly, so `create` and `import` run into two different destinations and
   the destination string is substituted out of both documents. Everything else — including the
   copied technology's file name and the full outputs list — is compared verbatim.

Nothing else is exempted. For `run`, `check`, `explain` and `read` the documents are byte-identical
with no substitution at all.

**`--kits` is not a tool argument, deliberately, and it is not a parity hole.** It is one of the flags
taken before dispatch, so `serve --root <dir> --kits <dir>` registers the resolver for the whole
server and every `run` through it resolves an external device model. Making it a tool argument would
let a client point the server at an arbitrary directory on its say-so, and a kit folder is installed
software that lives outside the design root by nature. The parity test passes `--kits` to both sides
equally.

### Cancellation is pinned through the queue, not through a stopwatch

R-aut5-8 wants a long run cancellable. Wiring it was small — `RunHost` carries the host's
`RunControl`, the same one `em` already used, into the sparam, sweep, loadpull and pursuit call sites,
and is null on a command line so nothing there changed.

**Gating it honestly was the harder half, and the finding is that there is no long run in the default
test tier.** Measured: the `em` fixture at 3 frequency points is 180 ms; at 400 points it is still
under a second; `lp` on Hero3 is 0.20 s; a 10,001-point `sparam` on Hero1 is 0.17 s. The one thing
that genuinely takes minutes is a de-embedded full-wave point, which is `Category=Benchmark`
territory and cannot live in the routine gate. A sleep-then-cancel test would therefore be measuring
the machine, which is exactly what this repo says not to write.

So what is pinned is the **mechanism**: capability calls are serialized, so of two calls sent
together the second is queued while the first runs, and cancelling *that* one is deterministic. The
token still reaches the run, the run still refuses to produce a result, and 130 still comes back — the
code `cli.md` §7 already gives a run stopped at a work boundary. The in-flight case is the same code
path with the token cancelled a moment later.

**Progress is gated on a 4,000-point EM sweep** (~0.3 s), which is enough for the throttle to deliver
several observations and for the assertion "it advanced" to mean something. A single observation
repeated is a bar that never moves, which is what a client with no progress already has.

### A client that disconnects: the deadlock was in the TEST, and the fix belongs in both

The first version of the disconnect gate took 30 seconds and passed. The server's shutdown joins its
worker for 30 s, and the worker was blocked **writing a result document to a pipe nobody was
reading** — the test had closed stdin but still held the stdout handle open, so there was no `EPIPE`,
just a full buffer.

Two changes came out of it, and only one is a test change:

- the session drains **both** pipes continuously on their own threads. A result document for a
  400-point sweep is comfortably past a pipe's buffer, so reading stdout only when an answer is
  wanted deadlocks — the same trap `EmCliVerbTests` already records for stderr, on the other stream.
- `JsonRpc.Send` treats a broken pipe as **the far end being gone** and stops writing. A real client
  that exits closes its read end, and every subsequent frame would otherwise throw on a worker thread
  for a frame nobody will read.

### The tool schema is generated from the translation table

`ToolCatalog` is one list of rows, and both the advertised JSON schema and the argument vector are
built from it. This is not tidiness: an advertised argument that the translation drops is a client
paying for a description of something that does not work, and it is the single most likely way this
file rots. Adding a flag is one row.

An argument that belongs to another mode of the same tool is refused **naming that** — "`grid` does
not apply to lpp" rather than "unknown option `grid`" — because the two sentences send a reader to
different places.

### The README's three source-tree lists had drifted, and now have a gate

§7 asked for the annotations this series falsified to be corrected. Read against the tree rather than
assumed, the corrections were: `src/Design` gained `Schematic/`, `Symbol/`, `Layout/Assembly/`,
`Layout/Interchange/`, `Theming/` and `resources/`; the `Drc/` annotation was **inverted** (the engine
moved in AUT-4, so "the DRC engine stays in src/Ui" was exactly backwards, and what is left in
`src/Ui/Layout/Drc` is the Messages report and a per-user preference); `src/Ui/Layout/Interchange/`
does not exist any more; `src/Ui/Schematic/` is the editor half only; and the firewall paragraph said
**seven** assemblies while the gate lists **eight** — `src/Diagnostics` was missing from all three
lists and from the source-layout tree entirely.

`tests/Ui.Tests/ReadmeSourceLayoutTests` now holds it: every `src/…` folder the tree draws must exist,
every project under `src/` must be drawn, and the firewall paragraph must name every project
`UiFirewallTests.NonUiAssemblies` gates — including the count, spelled out. It cannot check prose, and
does not pretend to; what it does is fail at the moment somebody is already reading the sentence next
to the stale path.

### Firewall

`serve` lives in `src/Cli/Serve/`, so it is inside a project `UiFirewallTests.NonUiAssemblies`
already gates (R-aut5-3 needed no new entry). It has no dependency of its own: the JSON-RPC framing
is hand-rolled over `System.Text.Json`, ~130 lines, because the protocol layer is the disposable one
by design and a package here would outlive the adapter it serves.

---

## Post-series review — the catalog advertised flags the verbs did not read (2026-09-05)

A review of the whole AUT-1 … AUT-5 series, after it landed. Three defects and one latent one; the
findings below are what the per-tool parity gate could not see, and why.

### Six advertised arguments were not flags at all

`ServeProtocolAdapterTests`' parity tests make **one call per tool**, so they compare the two
adapters on the arguments that call happens to pass and say nothing about the rest of the table.
Read against the verbs' own argument loops, `ToolCatalog` was offering:

| Mode | Advertised | The verb's loop reads |
|---|---|---|
| `run/sparam` | `-a`, `--set` | neither — `--freq` and `-o` only |
| `run/dc` | `--set`, `--tol`, `--maxharm`, `--maxmix` | none of them — `--max-iter`, `--dc-steps`, `--gmin` |
| `run/lp`, `run/lpp` | `--maxmix` | HB's alone; the loadpull loop has no case for it |

**And the failure was not "unknown option".** Four of the five run verbs find their input by *"the
first token that does not start with a dash"*, so a dropped flag's **value** became the input path —
`lp x.cnl --maxmix 3` answered `File not found: 3`, a refusal naming neither the real problem nor the
file the caller gave. `dc` reads its path positionally instead, so `dc x.cnl --set Vg=1` reported
nothing at all and ran without the override: a run answering a different question than the one asked.

Fixed in both directions. The catalog now names only flags the verb reads (`SolverOptions` is HB's;
`LoadpullSolverOptions` and `DcOptions` are the other two), **and all five run verbs now refuse an
unrecognised option** (`cli.args.unknown-option`), which `convert`, `new`, `import`, `check`,
`explain` and `read` have done since they were written. `dc` also gained
`cli.args.multiple-inputs`, since its own scan is the only place a second positional is visible.

**That refusal is what makes the gate behavioural rather than a source scan.** With it,
`EveryAdvertisedArgument_IsAFlagTheVerbActuallyReads` asks the real server for the real schema, calls
**every** advertised argument of **every** mode against a path that does not exist — every argument
loop runs to completion before the file is looked at, so nothing is read, run or written — and fails
on any `.args.unknown-option`. Verified red by putting `--maxmix` back on `LoadpullSolverOptions`.

### `--analysis ""` was refused by the verb its own description described

`explain --analysis` takes its name as an **optional following token**, so the catalog's `OptKind.Str`
forwarded an empty string as a value and `explain` looked for a chain called `""` — *"No analysis
named ''"* — while the argument's description said an empty string asks about all of them. The
description was the true half. `OptKind.StrOptional` emits the bare flag for an empty string; a name
still selects one chain.

### `convert` could do two things through the CLI that the protocol could not

`--workspace` and `--dbu` are both real `LayoutConvert` flags and both are documented in
`docs/user/src/reference/cli.md`; neither was in the catalog. That is R-aut-13's *"or vice versa"*
half — the direction a per-tool parity test cannot fail on, because it only compares the calls it
makes. Both added.

### Progress notifications raced the result they were meant to precede

`RunHost.Install` wrapped the observer in `Progress<RunProgress>`, which captures the
`SynchronizationContext` at construction and, where there is none, posts every observation to the
thread pool. The tool call runs on `McpServer`'s worker thread, which has none — so notifications
raced each other and the result frame. A client would see a bar that jumps backwards and observations
arriving for a call it has already been told finished. Replaced with a synchronous `IProgress<T>`;
the only observer is a JSON-RPC write, which is serialized and already throttled by
`RunControl.MinReportIntervalMs`.

### Left standing, and reported rather than absorbed

- **`sparam` and `dc` take no `--set`.** Every other run verb does, `cli.md` §5 describes it as the
  CLI's override mechanism, and its absence means a bias or frequency global cannot be overridden for
  the two oldest verbs — headlessly or through the protocol. Adding it is a capability change to
  verbs this series did not otherwise touch, so it is named here rather than folded into a review.
- **`sparam` takes no `-a`.** It runs the first typed `SParameterAnalysis`; a netlist declaring two
  has no way to say which.
- **`elab` is a verb with no tool.** It is reachable from the command line and not through `serve`,
  which is the R-aut-13 asymmetry the series otherwise closed. It is a development dump rather than a
  capability, and `explain` answers most of what it is reached for — but the omission is not recorded
  anywhere as deliberate, so it is recorded here.

### `convert`'s treatment at each layer, audited

Asked for directly, because the briefs name `convert` only in passing.

- **AUT-1 (`--json`)** — complete. `LayoutConvert` carries 41 `JsonRun` calls: `InputPath`, six
  `AddOutput` sites (a Gerber file set writes several), and **11 distinct refusal ids plus 12
  `Note`s**. It is the single most `Diagnostic`-dense verb in the CLI, which is right — it is the one
  with the most ways to be told no.
- **AUT-5 (`serve`)** — reachable, as `import { what: "convert" }`, and **parity-tested byte for
  byte** against `circuitrf convert --json` (`Import_ThroughTheServer_IsTheDocumentTheCliWrites`).
  Two flags were missing from the catalog and are now added; see above.
- **The §6.1 human-output golden set is `sparam`, `dc`, `hb`, `lp`, `lpp`, `elab` — and that is not
  an oversight about `convert`.** Those goldens compare **stdout only**, and `convert`'s entire human
  report is on **stderr**: its stdout is one line (the written path, or the cell names under
  `--list-cells`). A stdout golden for it would pin almost nothing, which is why AUT-1 verified it
  the other way instead — 12 refusal paths and one real DXF→GDSII conversion compared against a
  `git worktree` of HEAD, stdout, stderr AND exit code. `em` is absent for the same reason, and has
  `EmCliVerbTests`' byte-for-byte `.sNp` comparison instead.
- What WAS missing: `Json_AlwaysParses_AndStatusAgreesWithTheExitCode` exercised `convert` only on
  its **refusal**. A verb whose document is tested only on the failing path is a verb whose
  successful document is untested. The theory now covers a real conversion, and `check`, `explain`
  and both halves of `read` alongside it — the five verbs that produce no `DataSet` and whose
  payload is therefore the one least like every other verb's.

---

## AUT-6 — `circuitrf reference`, and the component catalogue (2026-09-05)

`brief-automation-6-reference-and-components.md`. One read-only verb and one MCP resource surface,
answering the question the rest of the series left open: **what may a caller write, before it writes
it.** No new document, no new analysis, no change to any existing verb.

### Did the seventh tool earn its place?

**Yes, and the margin is not large.** Measured against what a resource-only surface would have cost:

- **Resources are genuinely cheaper.** `resources/list` publishes nine entries of a URI, a title, a
  one-line description and a size. The `reference` tool costs a name, a three-line description and a
  two-property schema, carried for the whole session whether or not anything calls it. If every
  client surfaced resources to the model, the tool would be pure overhead and should not exist.
- **They do not.** Resource support is optional in the protocol and a client that implements it may
  still not put resources in front of the model — and a capability the model cannot reach is not a
  capability. That is the whole of the argument, and it is enough.
- **The cost is bounded because the surface is not per-topic.** One tool with a topic argument, not
  nine. `tools/list` grew by ~330 bytes.

**The thing that made this cheap is that both channels call the same verb.** `resources/read` and
`tools/call` both translate to `reference <topic> --json` and hand back `CliEntry.Run`'s bytes, so
the second channel is an envelope rather than a second implementation, and
`EveryResource_ReturnsTheBytesTheCliWritesForItsTopic` compares them against the CLI byte for byte.
Had the resource returned Markdown and the tool returned JSON, this would have been two surfaces that
drift, and the honest answer would have been to ship one.

**If it turns out not to pay, removing it is one `ToolSpec` and two test lines.** Adding it later,
after clients have been written against a resource-only surface, would have been worse.

### The brief's own naming collision, resolved

R-aut6-1 spells the generated catalogue `circuitrf reference components`; R-aut6-2 and R-aut6-8 spell
the PROSE page `components` as well. They cannot both be, and the brief did not notice.

**The catalogue keeps `components`** — it is the machine-facing answer the whole brief exists for, and
`reference components MLIN` only reads correctly that way. **The page ships as `component-notes`.**
Nothing was dropped: R-aut6-8's own words are that the catalogue answers "what may I write" and the
page answers "what does it mean", and a client that wants both asks for both. Each says so in its own
listing line.

Of R-aut6-8's two offered routes — strip the placeholders and serve the page, or exclude it — the
**first** was taken. Read without its tables the page is still 71 kB of prose that nothing else in the
program carries: what a ferrite bead is *not*, why an SRLC is the shape a real capacitor takes above a
few hundred megahertz, which of two FET laws to reach for. The 70 `{{symbol:}}` placeholders were
figures a text client could not use anyway, and the 67 `{{table:}}` placeholders are exactly the
generated half the catalogue serves better. Removing a placeholder that is alone on its line takes the
line with it, so the result reads as prose rather than as a page with holes in it.

### Both §2.3 lists, re-measured on the day it was built

Unchanged from the brief's own measurement. Asserted by token in
`ReferenceCliVerbTests`, and derived from the registries in the test rather than typed, so a change
in either direction fails by name:

- **5 `EngineReference` targets no factory entry answers to**: `GND`, `MEAS`, `Pin`, `SpiceModel`,
  `VAR`. Four of those are not components at all — schematic elements the extractor consumes — which
  is itself the answer for them, and the catalogue says so.
- **7 factory types no `EngineReference` maps to**: `Chain`, `ExtDevice`, `I_nTone`, `SemiC`,
  `Short`, `Term`, `V_nTone`. Writable in a `.cnl`, drawn by nothing, and therefore carrying no
  declared parameters.

The catalogue is **73 entries over 68 factory tokens** — the 68 plus those 5.

### What the registries could not answer

Two gaps, and the second is the more valuable one.

**1. Nothing states which parameter sets a variadic component's port count.** R-aut6-9 requires the
catalogue to report "it depends, and here is what on", and no registry held that fact —
`LibraryCatalog` knows Snp/ZPort/Sdd are dynamic for the palette's sake, `IsRemovableParameter` knows
ZPort's `NumPorts` is structural, and neither is an answer to the question. Per R-aut-1 the fix went
into the CAPABILITY layer rather than into the adapter: `ComponentTypeRegistry.PortCountParameter`, a
pure lookup beside `OwnsUniquePortNum`. It is not a transcription because the gate does not trust it —
`AKindWhosePinsDependOnN_ReportsThatAndNamesTheParameter` MEASURES variadicity (`SymbolPortDefs.For(k,
2).Length != SymbolPortDefs.For(k, 3).Length`) and fails any kind that answers differently at 2 and 3
without naming its parameter. A future variadic component added without an arm fails there.

**2. `ComponentModel.PortCount` is not the number of nets an instance line writes, and nothing below
the firewall states that number for a type with no symbol.**

This is the finding worth keeping. The brief says to take the port count "from the model", and the
model cannot give it: `PortCount` is the model's own port count in the MNA sense. `IProbeModel`
reports **1** and takes two nets. `FetModelBase` reports **2** and takes three. A 2-port `SddModel`
reports **2** and takes four (`SDD:M1 Vin 0 Vout 0`). `ResistorModel` reports 2 and takes two, which
is why the discrepancy is easy to miss — the commonest components agree by coincidence.

So the catalogue reads `SymbolPortDefs`, which IS the contract `NetExtractor` emits nets by, and
constructs no model at all — not even a parameterless one, where R-aut6-9 would have allowed it. The
consequence is the gap: for the seven factory-only tokens above there is no symbol, so **nothing below
the UI firewall states how many nets they take**, and the catalogue says exactly that rather than
guessing. `ExtDevice` is the honest case (its node count is the provider's descriptor's, known only at
elaboration); `Short` and `Term` are the ones a declarative fact could close, and closing it is a
registry question rather than one this brief may answer by inventing a number.

### R-aut6-12's four unarmed kinds

`DefaultParameters` has an explicit arm for 71 of 75 `SymbolKind`s. The four without one, split as the
brief asks:

| Kind | Which it is |
|---|---|
| `Ground` | **genuinely parameterless** — a ground symbol has no values to carry |
| `IProbe` | **genuinely parameterless** — `IProbeModel` reads no parameters at all; it stamps a 0 V branch |
| `Generic` | **the question does not arise** — an internal fallback glyph, in `LibraryCatalog.InternalOnlyKinds`, not user-placeable |
| `Unknown` | **likewise** — the sentinel a newer file's unrecognised component loads as |

So there is no undescribed component among them, and **no arm was added**. Two other kinds return an
empty list from an arm that means it — `Var` and `Meas` author their own rows — and `Mutual` returns
none because it references instances by name rather than connecting to nets.

### The total embedded size

**145,410 bytes** of reference text, across 8 topics, in `CircuitRF.Design` — so in every binary on
every platform, the GUI included. `component-notes` is half of it at 73 kB. It is reported by
`EveryTopic_IsEmbedded_AndItsBytesAreTheAuthoredFile`'s test output rather than asserted, so the
number is always current in the TRX without a threshold anyone has to maintain.

### Two things found on the way that were not this brief's

- **`tools/DocGen` had not compiled since AUT-2.** `Placeholders.cs` uses `ComponentTypeRegistry` and
  `SymbolKind`, which moved to `CircuitRF.Design.Schematic`; `src/Ui` absorbed that with its own
  `GlobalUsings.cs`, but global usings are per-project and **DocGen is not in `circuitRF.slnx`**, so a
  plain `dotnet build` never noticed. One `using` line. The lesson is the one the missing-resource
  trap teaches in a different key: a project outside the solution is a project nothing tells you
  about.
- **`docs/user`'s generated HTML was already stale before this change** — `cli.html` by ~315 lines,
  from AUT-5's edit to `cli.md`. See "The docs check" below.

### The docs check

`tools/DocGen/check-docs-current.sh` **fails**, and it regenerates as its mechanism, so the run was
reverted afterwards and only the Markdown sources under `docs/user/src/` are changed here. What it
reported, classified:

| Page | Why |
|---|---|
| `reference/components.html` | **this change** — 67 new terminal tables, one per component section |
| `reference/cli.html` | partly this change (the new `reference` section), partly AUT-5's already-pending edit |
| `reference/{layout-editor,schematic-editor,veriloga,wbond}.html`, `assets/js/search-index.js` | already pending before this change |
| `assets/figures/workspace-{overview,regions}*.svg`, `workspace.html`, `quick-start/`, `new-user-guide/` | the known non-deterministic figure families — a live capture, not a content change |

Regenerating is one deliberate pass and belongs to whoever is ready to review 2,200 lines of it.

---

## RC-5 — `history list` / `history restore`, and the one tool that is not a command line (2026-09-06)

`brief-revision-control-5-checkpoints.md` §6a. RC-3 shipped `history checkpoint`; this adds the other
two nouns and the batch.

### `--ref` and `--message` are gone from `history checkpoint`; `--intent` replaces them

RC-5 owns where a restore point lands (`refs/crf/restore/<sequence>`) and what its message says
(§5.5's three origins, plus the trailers). A caller that could name a reference could hand out a
number the ordering sequence had already used, and a caller that could write the whole message could
omit the origin — so both flags went. `--message` is still accepted as a spelling of `--intent`, for
a caller that learned it against RC-3. RC-3's own identity gate used `--ref` and was updated rather
than kept working: it is testing who the commit is BY, and the reference name was incidental to it.

### The batch cannot be a verb, and the reason is structural

`history checkpoint`, `list` and `restore` are verbs because each is one operation that begins and
ends inside one process. **A batch is a session**: opened before an agent's first modification, held
open while it works, closed when it is done. A process that exits after one command cannot hold that,
so `serve` is the only surface in circuitRF that can — which is what `revision-control.md` §5.3d says,
and it is why `HistoryBatch` is the one tool `ToolCatalog` does not build a command line for.

That exemption had to be made explicit in two gates that were written on the assumption that every
advertised tool is a verb: `EveryAdvertisedArgument_IsAFlagTheVerbActuallyReads` skips it (there is no
argument loop to scan), and the tool-count assertion names it and says why. **Its own gate is that the
state and the ten rules come back in the surface's own output** — a rule an agent cannot read is a
rule that does not exist.

### Every refusal names what to do instead, and that is a requirement rather than politeness

`revision-control.md` §5.3b's rules 3 and 9 are the load-bearing pair: they prevent the HELPFUL
failure, where an agent that finds no checkpoint mechanism makes its own arrangements and reports
success. A vague refusal is what makes an agent improvise, so all five — off, held, nowhere to record,
a window holding unsaved changes, no git — say what was not done and what to do about it, and a
`batch.improvise-nothing` warning accompanies every one of them. A gate asserts each refusal contains
the words "nothing was changed", because "the batch was refused" and "nothing was modified" are two
claims and only the second is the promise.

### The `history` payload carries no object identity

`HistoryReportJson` / `RestorePointJson` report the sequence, the time, the origin, the label, whether
it is kept, and what was left out — the same surface the panel renders. The commit identity is
deliberately absent: R-rc0-6 keeps git vocabulary away from a designer, and `--point <sequence>` is
what `restore` takes, so nothing a caller needs is missing. RC-7's explicit commit is the one place an
identifier is ever named, because there the user asked for it and it is theirs to refer to.

---

## RC-9 — `history clone`, `history pins|pin|unpin`, `history fetch|send` (2026-09-07)

`brief-revision-control-9-clone-and-pins.md` R-rc9-20. Six nouns on the existing `history` verb, each
calling the `src/Design` function the GUI's own command calls. **Adding them to the repo-root
`CLAUDE.md`'s verb list is the owner's edit — flagged, not made.**

### Why this brief has a headless spelling at all, which is not the usual reason

The other verbs are here because an operation that lives only in a view model is not a capability. This
one is here for a stronger reason the brief states outright: **a build machine reproducing a signed-off
result is why the pin exists.** `history pins --json` is the noun that matters — it is the only way a
CI job can find out which version of each referenced library a design is built against, and therefore
the only way it can tell "the same design" from "the same files".

### `history pins` exits 1 on a pin that cannot be honoured

Deliberately not a warning. A build machine that treated it as one would produce a result against
content the design was never verified against, which is the single outcome the pin exists to prevent.
It is the same posture `check` takes for an error and the opposite of the one it takes for a warning,
and the difference is that this one changes what the result MEANS.

### `pins` and `pin` need no repository, and requiring one would refuse the main case

The pin lives in the consuming design's `.cws`, not in a repository. `Bind` — the existing helper — binds
a workspace AND a git driver AND asserts a repository is there, which is right for `checkpoint`,
`restore` and `commit` and wrong here: the consuming design is very often a workspace circuitRF has never
kept a history for, referencing a library somebody else versions. `BindWorkspace` is the shorter one.

### `clone` refuses to derive a destination, and `--json` says so

Git happily works a folder name out of an address. Circuit RF will not: a folder appearing somewhere the
caller did not name is the surprise §0 forbids, and headless there is nobody to notice it. Both positions
are required. The GUI's dialog *suggests* a leaf name into a field the designer can see and edit, which
is the same rule seen from the other side.

`CloneJson` carries `RestorePoints: false` as a field rather than leaving it implied — §5.2a's three
journeys disagree deliberately, and a caller who is not told will assume the strongest of the three.

### One pre-existing `rev-parse` in this file, and the gate says so out loud

RC-9's source-scan gate holds `src/Cli/History.cs` to a weaker list than the two UI surfaces, because
RC-7's `history versions --changes` asks git directly whether a version has a parent before handing the
comparison to `HistoryBrowser`. It is one read and it is not RC-9's; the gate asserts there is exactly
**one** occurrence, so a second cannot arrive quietly. Every noun this brief added assembles no git
argument at all.


## AUT-8 R-aut8-8 — `explain` reported `runnable: true` for a chain that could not run (2026-09-07)

A DUT cell came back `runnable: true, dispatched: true, dispatchedBy: lpp` while naming a load tuner
and a source tuner that do not exist in it. `runnable` was `AnalysisChain.IsChainRunnable` alone —
"the chain bottoms out in an enabled analysis" — which says nothing about the references the analysis
names by string.

`Explain.UnresolvedReferences` now resolves those: the tuner instance names (present, and actually a
`Tuner`), the inner analysis a sweep wraps, the swept variable, and every free variable in a tone
expression. Each is a lookup against a list already in memory, so R-aut4-1's no-solve budget is
untouched.

Three things worth keeping:

- **`dispatched` had to move with `runnable`.** Reporting a chain as not runnable while still saying a
  verb would dispatch it is the same optimistic claim wearing a different field name.
- **The false case has to say WHICH reference failed**, or the caller is no better off than with a
  clean report. `unresolved` carries one line per failure naming the key and what it pointed at, and
  the tuner case lists the design's actual `Tuner` instances when there are any.
- **What is deliberately NOT claimed.** The same DUT also contained no bias source. That is a property
  of the SOLVED circuit; asserting it inside a verb that does not solve would be the same overreach in
  the other direction, and the brief's own instruction is that the weaker claim stated honestly beats
  the stronger one stated wrongly.

---

## AUT-9 — result documents a caller can trust and afford (2026-09-07)

Twelve requirements, none of which changes a computed value: every one is about the reporting of one.
The evidence is `brief-automation-7-mcp-hardening.md` §1 — the whole surface driven end to end by an
out-of-process client with the MCP server and nothing else. What follows is what turned out to be
true while building against it.

### The one requirement whose obvious fix was the wrong fix

**R-aut9-2 (per-port Z0) was implemented twice.** A two-port whose second port was declared
`Z=12 Ohm` wrote a Touchstone whose header said `Port 2: Z0 = <50; 0>` — the uniform value printed
once per port, which is a *positive claim about ports nobody had looked at* — above a matrix
generalized w.r.t. [50, 12] under an option line saying `R 50`. `read` on that file then reported a
`Z0` cube of `[[50,0],[50,0]]`.

The first attempt made the file self-consistent by **renormalizing** the data to the single reference
it declares, through `RFNetwork.SToS`'s power-wave formula. That is defensible on paper and wrong
here, and the thing that said so was an existing acceptance test:
`Engine.Tests/Devices/MatchStampTests.ACnlContainingAMatch_RunsHeadlessUnderCliSparam` runs a 50-to-10
transformer between a 50 Ω port and a **10 Ω** one and asserts `S21 > -0.2 dB` in band. Referenced to
[50, 10] a matched transformer reads ~0 dB; renormalized to a uniform 50 Ω it reads **-2.55 dB**, which
is `1 - |Γ|²` for `Γ = (10-50)/60` and is a correct answer to a different question. Renormalizing had
quietly taken the quantity the ports were declared to ask about out of the file.

So: **nothing is renormalized.** `SNP.Z0PerPort` carries the per-port references alongside the single
`Z0` the option line declares; `TouchstoneIO` writes them as a header note saying *the data is
referenced to THESE, not to the option line's single R*, and reads that note back; `FromSnp` builds
the `Z0` cube from them when they are there. `run` was already right about the ports — measured, not
assumed — and it was `read` and the header that were not.

Two smaller things fell out of the same block, both recorded as unfixed in
`src/Ui/DataDisplay/RESOLVED.md` and both now closed:

- `! NOTE: Original data had complex Z0.` was emitted on **every** non-strict export regardless of the
  actual Z0 — a false statement heading most Touchstone files circuitRF has ever written. It is
  conditional now, and says which of the two things is true.
- The option line's `R` keeps only the real part, so a complex reference lost its reactance on a round
  trip. A `! NOTE: reference impedance is complex: <50; -10>` note carries it and is read back — and
  honoured only when its real part agrees with the option line, so a note that has drifted from its
  data is ignored rather than believed. The spelling is invariant (`"R"` round-trip format under
  `InvariantCulture`), because a data file whose numbers change with the machine's locale is not one.

### R-aut9-8's blanket rule was too blunt by exactly one diagnostic

"Drop any string argument whose value equals `message`" is the obvious reading, and it broke
`ConvertCliVerbTests.Json_ListCells_AnswersInTheDocument`. `convert.cell.listed` is templated
`"{cell}"`: its argument is the **answer** — one cell name, the whole point of the call — which
happens also to be the whole sentence. The rule is by NAME now (`text` only), which is what the brief
literally says and which keeps every argument that is a value rather than prose. The volume it was
written for is untouched: `elab.note`, `convert.note`, `import.note` and the Gerber diagnostics are
all `"{text}"`.

### `--format summary` could not be called that

`render --format pdf` already exists and means the picture's file format. The brief suggests
`format: "summary"` for the shape-only mode; taking it would have made one word mean two things
across two verbs, which a caller gets wrong once and then forever. It is **`--result full|summary`**
on the CLI and `result` in the tool catalog — named after the document section it governs, and the
adapter's "every argument is named after the CLI flag it becomes" rule is kept.

### Where the narrowing refusals live, and why not in `CliDiagnostics`

`--at`/`--range` are taken before dispatch, but the axes they name do not exist until a run has
produced them, so an unknown axis can only be refused at document-build time. The refusals are
`RfCore.Export.NarrowingDiagnostics`' own — `narrow.axis.unknown` and three siblings — not
`src/Cli`'s: the rule about which axes exist belongs where the axes do, and `Firewall.Tests`'
`UserFacingTextGateTests` said so directly when they were plain strings. Only the SPELLING refusal
(`cli.narrow.malformed`) is the CLI's, because `--at freq` with no value can be caught from the
command line alone and there is no reason to make a caller wait for a solve to learn it.

**An unhonourable narrowing fails the invocation** (exit 1), and the document then carries the
result's `shape` and no `groups`. Returning the un-narrowed values instead would hand a caller the
whole payload — at exactly the size the requirement exists to avoid — under the impression that it
answers the question asked.

### The three loadpull findings are read off the RESULT, not plumbed through the engine

R-aut9-4/5/6 are all answerable from cubes the run already publishes (`Converged`, `IsTickle`,
`PavlDbm`, `Pout`, `StopCode`), so `RfCore.Loadpull.LoadpullRunFindings` reads them and returns
values; `CliEntry.ReportLoadpullFindings` only spells them. No engine signature changed, and the
sweep path gets them for free — which a pre-run check plumbed through `LoadpullEngine.Resolve` would
not have, since `ParametricSweepEngine` resolves per point inside itself.

**R-aut9-6's warning is conditional on R-aut9-5's**, and that is not a shortcut. The tickle is
*designed* to sit tens of dB below `PinStart` — the shipped default pair is -50 and -20, a 30 dB gap —
so a warning that fired on the gap alone would fire on every loadpull ever run and mean nothing. It
fires only where nothing past the tickle converged, which is exactly where lowering `PinStart` is the
first thing to try; the exercise's own 44-point failure converged in 38 s at `PinStart = -45`.

### R-aut9-11's "a path to the full text" is a sentence, not a file

The brief asks `--summary` to return "counts by severity plus the outputs, and a path to the full
text". Nothing here writes a side file: `--summary` collapses only the **`info`** diagnostics (every
warning and error still travels in full), and `diagnosticSummary.full` names the invocation that
returns everything. Writing a file the caller did not ask for to hold text stderr already carried
would be a surprise, and on `--list-cells` there is no output directory to put it in. Recorded as a
deliberate deviation rather than left to look like an oversight.

### Structured content doubles the frame, and that is the protocol's own recommendation

R-aut9-12's `structuredContent` sits **beside** the text block rather than replacing it. MCP says a
tool returning structured content SHOULD also return the serialized JSON as text, and the parity gate's
whole premise is that `content[0].text` is the CLI's own bytes. So the frame carries the document
twice. The levers for size are `--at`, `--range`, `--result summary` and `--summary`, not this.

### Measured

`sparam` on `testdata/Hero1/hero1.cnl`, `--json`, as the byte-budget gate reports it:

| Asked for | Bytes |
|---|---|
| the whole result | ~21,000 |
| `--at freq=2GHz` | under a quarter of it |
| `--result summary` | under 4,000, and under an eighth of the whole |
| `convert --list-cells --summary` on a `.kicad_pcb` | under 4,000 |

The gate asserts the ratios rather than the absolute figures — it is a tripwire for a structural
regression (the duplicated diagnostic text coming back, a result becoming un-narrowable), not a
benchmark of the serializer.

## WSP-6 — `NDF=yes`, and reading a diagnostic key off a message that quotes another (2026-09-08)

`sparam` prints `NDF: N right-half-plane pole(s)` with the unrounded net encirclement beside it, and
`--json` carries the same under `ndf` together with the `ndf.*` findings the run raised.
`explain --analysis` lists the passivation each instance will use and the whole refusal if the run
would be refused — which is how a caller sees a refusal coming without running, and it is the SAME
`NdfPassivation.Survey` the engine calls before its first factorisation, so the sentence is the one a
run raises, word for word.

**The findings list first matched `ndf.` ANYWHERE in a warning**, which picked up the keys a message
quotes in its own advice: the counter-clockwise note ends by telling the reader to go and look at the
`ndf.passivation-not-passive` notes, and that sentence was being reported as a passivation note on a
run that had none. Every `ndf.*` message now begins with its own key, and the parser reads the
LEADING token only.

Two smaller notes. The listing prints only instances that are NOT plainly passive, because a hundred
resistors saying "passive" is noise — and each `ActiveExact` model supplies its own one-line
`PassivationNote` (`R → |R|, which is its ordinary stamp unless R < 0`) so the line says something
rather than restating the enum. A refused instance carries its measurement into the listing too
(`σ_max = 5.097 > 1 at 2.7 GHz`), so the reason is visible without reading the refusal block below it.

## Review round after WSP-9 — `hb` reported no probes, and `explain` listed every resistor (2026-09-08)

Two things a review of the finished WSProbe series found on the verbs. Neither is a wrong number;
both are a caller unable to read a right one.

**`hb` printed no WSProbe line at all.** `PrintWsProbes` was wired into `sparam` only, so a
harmonic-balance run with an `SSStart/SSStop` sweep wrote `wsp`, the six default cubes and both
margins — and no label ↔ idx map to read them by, and no margin minimum, and no `wsprobes[]` in
`--json`. Both are required: R-wsp1-12(a) reports `idx` precisely because it depends on the other
probes and cannot be guessed, and brief-wsprobe-5 §4 says the run summary prints the
per-operating-point minimum. The threshold Info note DID fire under HB (it is raised by
`WspCubePacker`, which both engines call), so the run said "this probe's margin collapsed" while
printing nothing to say which probe or where.

The line is now the same line over either verb, and it takes its sweep axis from the probe's own
cubes rather than from a passed-in `freqs` — `freq` under S-parameters, `ssfreq` under harmonic
balance, and a drive-swept run's `{Pin, ssfreq}` still names the frequency because
`ParametricSweepEngine` PREPENDS its axis and the frequency stays innermost. The
`S-parameters: none (no ports)` line stays on `sparam` alone: under harmonic balance a run without
ports is ordinary and that sentence would be noise. Gate:
`WsProbeMarginCliTests.Hb_ReportsTheSameProbeLineOverSsfreq_AndCarriesItInTheJson`, which also holds
that an HB run with no small-signal sweep prints nothing new.

**`explain --analysis` listed every resistor as carrying activity.** The listing filters on
`Activity != passive` "because a hundred resistors saying passive is noise" — but `ResistorModel`
answered `ActiveExact` at the TYPE level and had no instance-level override, so the filter kept all
of them and `N carrying activity to passivate` was the resistor count plus the active devices (10 of
18 on `three_probe.cnl`, of which 3 are the VCCSs that actually are active). `ResistorModel` now
overrides `ActivityFor`, which is that hook's stated purpose, and answers `Passive` for `R ≥ 0`;
a negative resistor is still §8's negative resistance and still `ActiveExact`. Nothing numerical
moves — see `src/Core/RESOLVED.md`. The same fixture now reads 3.


## Every run verb silently dropped hierarchical cell instances (2026-09-15)

Found while authoring the shipped example workspaces, which is the first time anything headless ran
a design somebody had actually drawn WITH A SUB-CELL IN IT.

**`NetExtractor.Extract` takes its `ICellResolver` as an OPTIONAL argument, and treats a null one as
"flat caller — skip silently".** That is the right answer for a caller extracting a single schematic
that cannot contain a cell instance, and the wrong one for everything else. Both CLI extraction
sites passed null: `CircuitSource.CnlTextOf` (which every run verb and `netlist` go through) and
`Check.cs`.

**Nothing failed.** A design whose device lives in a sub-cell extracted to the passive network
around the hole where the device used to be, and then ran:

```
  Converged: yes (31 solve(s))     Residual: 9.4e-07 (worst)
  Gt_dB:     -72.16 ... -84.06     over a 0 -> 30 dBm drive sweep
```

A power amplifier reporting −72 dB of gain, converging on every point, with no message anywhere.
`circuitrf check` called the same design clean, because it extracts the same way — so the one tool
whose job is to say "is this sound" was blind to it too. The root `CLAUDE.md`'s promise that *a
`.cnl` that works headless works when opened* was false in the direction nobody looks: the window
has always passed its own resolver, so the two disagreed about what the design **was**.

**The fix is `DiskCellResolver` (`src/Design/Schematic/`), and both sites now pass it, never null.**
It is built on `HierarchyResolver`, which moved below the firewall for this — see
`src/Design/RESOLVED.md`. It is the window's own descent minus the one thing a process that exits
cannot have: memory-else-disk, so an unsaved tab is what a GUI run sees.

**Three things worth keeping:**

- **The diagnostic exists and was never reached.** `NetExtractor` has a conflict note for an
  instance it cannot resolve — *"Cell instance 'X1' (cell '../../FET') has no primary schematic;
  skipped"* — but the null-resolver path returns before any of them. An optional argument whose
  absence means "silently do less" is the shape to be suspicious of.
- **`circuitrf explain --ref ../../FET` resolved the cell correctly the whole time.** Resolution and
  extraction are different code paths, so the one diagnostic a user would reach for said the
  reference was fine while the extraction was dropping it.
- **A structural gate would not have caught this.** Exit code 0, a written `.cnl`, a converged run
  and a well-formed `.npy` were all true. `tests/Ui.Tests/Cli/CliHierarchyExtractionTests.cs`
  asserts on the GAIN for that reason, plus a byte-for-byte comparison of the headless netlist
  against the window's own resolver.


## `check` called a `.cdd` a file circuitRF does not read (2026-09-17)

Found running `circuitrf check` over the System Design example, which is the first workspace to ship
data displays.

`DocumentKinds.Classify` has mapped `.cdd` to `DocumentKind.DataDisplay` since RND-4, and `Check.cs`
had no arm for it. `CheckPath`'s `default:` case therefore fired, and its message is a claim rather
than a shrug:

```
error: .../TxSuperhet.cdd: Nothing circuitRF reads is named '.../TxSuperhet.cdd' — check takes a
workspace, a cell folder, or a .csch, .csym, .clay, .ctech, .cem, .cnl, .wasm or a Touchstone .sNp.
```

That is false about a document the application opens, renders and exports, and it fired once per
display: `check` on a six-bench workspace reported **6 errors and exited 1** with nothing wrong with
it. The folder WALK skips what it does not recognise (`Unknown`, `Interchange`, `Touchstone`), so the
kind being recognised-but-unhandled is what made it loud.

**Two decisions in the arm that was added, and the second is the load-bearing one.**

- **The reader is the renderer's**, `JsonSerializer.Deserialize<DataDisplayConfig>` through
  `DataDisplayJson.Options`, with the same v1-plots/v2-tabs fallback `RenderDataDisplay.Draw` makes.
  A `.cdd` that opens checks clean and one that does not is named here rather than at the moment
  somebody double-clicks it. No second reader, per R-aut4-2.
- **A result file the display names and cannot find is a NOTE.** A display is a view of a run and a
  run is not a document; `examples/` ships displays with no results beside them deliberately, and
  `.gitignore` excludes `results/` from every workspace in the repo. If the missing `.npy` were an
  error then every design nobody had simulated yet would fail its own check, which is how a check
  stops being run. What IS reported as a defect is a document that cannot be read, one that holds no
  plots, and a trace bound to neither a cube nor an expression — the one broken state that survives
  being opened, because it draws nothing and says nothing about why.

The reference collection and the beside-it-then-`results/` locator are `CddSources.Describe`, which
is `Bind` with the loading left out, rather than a second copy in the checker: which references a
document carries and where a relative one is looked for are decisions that already existed.

Gate: `CheckAndExplainCliVerbTests.ADataDisplayWithNoResultsBesideIt_IsANoteAndNotAnError`, which
asserts the note's id AND the absence of `check.path.unknown-kind`, since a green exit code alone
would also pass if the walk had simply been taught to skip `.cdd` entirely.


## A run verb's `-o` did not create the folder it was told to write into (2026-09-17)

Exposed by the same example, one change later: the results were removed from what it ships, and the
command its README documents stopped working.

```
$ circuitrf hb "TxDirectConversion/schematic/TxDirectConversion.csch" -o results/TxDirectConversion.npy
Error: Could not find a part of the path '.../System Design/results/TxDirectConversion.npy'.
```

**The GUI has always created it.** `ResultsWriter.WriteRun` does `Directory.CreateDirectory` on the
way past, which is why Simulate works on a workspace that has never been run. So the headless path
was the one that required somebody to have run it already — and the failure was invisible for as
long as every example shipped a `results/` folder, because the folder was always there.

`netlist`, `render` and `plot` had each solved this locally (`Netlist.cs:88`, `Render.cs:1377`,
`PlotVerb.cs:298`) — the newer verbs got it right and the run verbs never did. There are four
distinct writers involved (`DataSetExporter`, `TouchstoneIO.WriteFile`, the loadpull `.spl`/
`.lpcwave` writers, and `em`'s `SnpOutputPathOverride`), which is why fixing one would not have been
noticed to leave three. `EnsureOutputDirectory` in `CliEntry.cs` is called at all four, and
deliberately swallows its own failure: the write that follows is already inside a try/catch that
names the file, and a folder that cannot be created is one problem, reported once.

Gate: `MissingVerbsCliTests.ARunVerbCreatesTheOutputFolderItWasGiven`, over `sparam` to both
Touchstone and `.npy` and over `hb`. The destination is **two** levels deep, so a fix that made only
the immediate parent would still fail it.

---

## R-rail10 — `circuitrf rail`, and the four things that were not in the brief (2026-09-18)

`brief-railrf-10-cli-verb.md`. The verb itself went in as specified — argument parsing, refusals and
reporting over `src/Design/RailRf`, gated by byte identity against the in-process call plus a
comment-stripped source scan. What follows is what turned out to be true while building it.

### The brief's "drawn by brief 9's one function" does not compile, and the fix is a new file below the firewall

§11.7's rule is *there is one route from an overlay to a page and not two*, and brief 9 wrote
`RailGraphicExport.ContextFor` / `BuildSvg` / `BuildPdf` with a header saying **"brief 10 calls the
first three and writes them to a file"**. It cannot: that file is in `src/Ui`, it composes through
`LayoutClipboard`, and `LayoutClipboard` performs `IClipboard` traffic and returns an Avalonia
`Bitmap`. `src/Cli` may not reference `src/Ui` — the invariant `tests/Firewall.Tests` enforces — so
the brief's own instruction is unbuildable as written.

Both alternatives are worse than they look:

* **Move `LayoutClipboard` below the firewall.** It is ~500 lines entangled with `IClipboard`,
  `WindowsClipboard`'s P/Invoke session and `Bitmap`, and brief 9's own header records that this path
  *"has cost real debugging time across three platforms and none of it should be spent again"*.
* **Draw the page in `src/Cli/Rail.cs`.** That is precisely the second route §11.7 forbids, and the
  drift would be invisible: two compositions that agree today and stop agreeing the first time either
  is touched, each producing a plausible page.

So the composition is **`src/Render/Renderers/RailReportPage.cs`**, below the firewall where both
callers reach it: the verb calls it now, and `Report ▸` — which brief 7 left wired to a disabled
button — calls it when it lands. It decides the layout of a page and nothing else; every pixel of the
picture is `LayoutRenderer.Draw` plus `RailMapRenderer`, and every line of the text is the result's
own sentence (`RailPortDrop.Describe`, `RailRegulatorHeadroom.Describe`, the breakdown rows), because
a page that re-worded them would make the window and the report disagree about one result.

*Worth repeating from `LayoutClipboard.ExportOptions`:* the page turns **all seven** level-of-detail
tiers off, not just `DetailPixelThreshold`. That is the one direction the mistake produces a
plausible picture — a picture of LESS geometry than the document holds.

### The placement table is never joined into the request, so a refdes anchor cannot resolve — in the window either

`RailPortAnchor` documents a refdes-and-pin as *the spelling* and a coordinate as *the fallback*
where there is no placement file. Headless there is always no placement file: a `.crail` carries
`ArtworkCellRef`, `TechnologyRef` and `PartLibraryRef` and nothing else, so `rail` has nothing to
fill `PdnExtractionRequest.Pads` from and `U1.VDD` resolves to nothing.

**This is not a CLI gap.** `RailRfWindow.Import.cs` reads the placement table
(`PlacementFile.ReadFile`) and hands it to `RailRfViewModel.ApplyImport`, which stores it on
`Placement` — and `RailRfViewModel.BuildRequest` sets `Shapes`, `Technology`, `DbuPerMicron`, `Model`
and `Mesh` and **leaves `Pads`, `NetPoints`, `SeriesElements` and `ShuntParts` empty**. So a
refdes-anchored document is refused by the extractor (*"names U1.IN, and no pad of that reference is
on this board"*) in the GUI exactly as on the command line.

The consequence that costs something: **a rail CHAIN is unsolvable today.** `RailOrder` links two
rails only by a refdes appearing as a load on one and a source on the other, so a chained document's
ports are refdes-anchored by construction — which is the one shape that cannot resolve. The cycle
REFUSAL still works, because `RailOrder.Resolve` runs before any pad is looked up, and
`RailCliVerbTests` gates it there.

Left alone rather than patched from the CLI: the join belongs where the placement table is read, and
a `--placement` flag on this verb would be a second way to supply what the document ought to name.

### `--set` is in the option table and has nothing to override

Every run verb's `--set name=expr` replaces a global before elaboration. A `.crail` declares no
globals — every quantity in it is a stated number in base SI and none is an expression — so there is
nothing to replace. Accepted-and-dropped is the defect `cli.md` §3.3 records for the five older run
verbs (*"the run answered a different question than the one asked"*), so it is a refusal naming the
flags that DO state those quantities: `--source`, `--load`, `--target-drop`, `--target-z`,
`--reference`, `--extent`.

### `RenderCliVerbTests` does not compare "with no exclusion at all"

The brief says so; the file says otherwise, and the file is right. `AssertSameSvg` normalises Skia's
`cl_`/`img_`/`gr_`/`fp_` ids — the SVG device numbers its `clipPath` elements from a **process-wide**
counter it never resets, so two renders in different processes carry different ids for the same clip —
and `StripPdfDates` normalises the PDF creation timestamp. Both are properties of neither code path,
both are applied only when the raw bytes actually differ, and `RailCliVerbTests` follows the same
shape and says so. Without them the SVG gate fails at the first `clipPath` id, which is what it did
on the first run.

### Two smaller things

* **One encoder, not two.** `Render.Emit`'s body moved to `src/Cli/VectorPage.cs` and both verbs call
  it. Two copies of an encoder diverge in exactly one visible way — the `SvgFontNormalizer` repair
  gets applied by one of them — and R-rail10-8 asks for no second export path in so many words.
* **A technology that does not resolve is a REFUSAL here**, where `render`'s orphan `.clay` is only a
  note (R-rnd2-1). The asymmetry is deliberate and is in `CliDiagnostics.RailNoTechnology`'s own
  remarks: a picture on the fallback palette is honestly a picture of geometry, where copper priced
  with no thickness and no conductivity produces numbers indistinguishable from numbers with physics
  behind them.

---

## railRF review round 2 — two defects in the `rail` verb, neither of which reported a failure (2026-09-18)

Review of briefs 9-16. Both findings are in `src/Cli/Rail.cs` and both produce a run that exits 0
with a plausible page.

### 1. The provenance banner counted the LIBRARY's rows, not the board's parts

`Provenance` asked `PartLibrary.Coverage` about `library.Rows.Select(row => row.PartNumber)` — every
row the library holds. `Coverage`'s parameter is named `referencedPartNumbers` and R-rail11-6's two
headline numbers are statements about **the board**: *how many parts are modelled from a file*, and
*how many have no bias curve*. A shared library of 500 rows in front of a twelve-part rail therefore
printed the library's own totals into the console banner, the CSV comment header, the `.npy`
provenance group and `--json` — as a number that reads like a checked board.

The board's parts were available the whole time: `RailSpec.Parts` is the document's own part list,
each row carrying the internal part number the model attaches to (brief 11's `RailPart`). The verb
now counts over the parts of the rails it is REPORTING, and resolves each through
`PartLibrary.ResolveModel` for the file count.

**It was also a verb disagreeing with the window about one document.** `RailRfViewModel.RebuildParts`
computes the same two numbers over `bom.PartNumbers` — the parts on the board — so the status strip
and the CLI banner answered differently for the same `.crail`. That is what R-rail10-8's rule is
about, and a source scan cannot see it because both sides are one call into `src/Design`.

**A rail with no part rows now says so rather than printing a zero.** Nothing to count is not a count
of nothing — the same rule the "no part library resolved" line already followed one row along.

### 2. `--target-z`, `--mask` and `--aggressor` were accepted and dropped in silence

All three are parsed, validated against the document (`--mask` even refuses a mask that lands on no
port, for exactly this reason: *"a mask that landed on nothing is a mask that will not be applied,
and a silent one reads on the report exactly like a mask that was honoured"*) and written onto the
rail — and then the verb runs `RailDcRun` and prints a DC answer, where none of the three can appear.

That is `cli.md` §3.3's accepted-and-dropped defect, and it is the one this same file already refuses
`--set` for, one flag along. It is a NOTE rather than a refusal (`CliDiagnostics.
RailFrequencyFlagsNotInThisPhase`, stderr + `--json`), because a `.crail` legitimately states both
halves and a caller wanting the DC answer out of one should get it — what they may not have is the
flag going by without a word.

### Reported, not changed: `rail` still cannot produce Z(f)

`PdnSweep` (brief 12) is reachable only from `RailRfViewModel.Response` and from tests. The verb's
`-o out.sNp` is still `CliDiagnostics.RailTouchstoneNotYet`, its CSV still carries
*"anti-resonances: none are reported here … this is the DC phase"*, and §2.4's coincidence check —
*the sentence the tool exists to produce* — has no headless spelling at all.

Brief 10 wrote that refusal as a P0 placeholder in so many words (R-rail10-4: *"at P0 this is a
refusal naming `--accurate` and the phase"*), and brief 12 shipped the sweep without coming back for
it. Wiring it is a real piece of work — a band/grid surface on the verb, the Touchstone write, the
frequency tables in the CSV and on the page — so it is recorded here as an owner decision rather than
taken unasked. §5's claim that *a board can be gated in CI* is true of the DC half today and of
nothing above it.

---

## SMITH-10 — `circuitrf smith`, and the four things that were wiring after all (2026-09-19)

`brief-smith-10-cli-verb.md`. The brief's premise — *it depends on brief 2 and not on the window,
which is the whole point of having put the arithmetic below the firewall from day one* — held for the
NUMBERS exactly as promised: `SmithCascade`, `SmithBand`, `SmithQArcs` and `SmithDesign.Refusal` were
already in `src/Design` and the verb reads them directly. It did not hold for the PICTURE, and the
gap was not where the brief expected it.

### The chart's plot half was framework-free and in the wrong project

`SmithPlotBuilder`, `SmithChartScene`, `SmithMarkerBridge` and `SmithOverlayResolver` reference
`CircuitRF.Design.Smith`, `CircuitRF.Render.DataDisplay`, `RfCore` and SkiaSharp — **no Avalonia, and
no view model.** They were nonetheless in `src/Ui`, which `src/Cli` cannot reference, so
R-smith10-3's "no second drawing path" was unreachable without moving them. The move was mechanical:
four files, namespace `CircuitRF.Ui.Smith` → `CircuitRF.Render.Smith`, one line each in
`src/Ui/GlobalUsings.cs` and its `tests/Ui.Tests` mirror, and the existing 201 Smith tests passed
unchanged.

**Framework-free is not the same as below the firewall, and only the second is enforced.**
`tests/Firewall.Tests` asserts that `src/Design`, `src/Render` and `src/Cli` reference no Avalonia; it
cannot assert that a file in `src/Ui` which HAPPENS to need no Avalonia is in the right place. That
gap is only ever discovered by the first caller from underneath — which is what a P3 phase is.

### `SmithGripperOverlay` was one class doing two jobs

Its `Draw` produced the arrowheads, the load-point frequency labels, the generator anchor and the
gripper rings; its `HitTest`/`DragBegin`/`DragTo`/`DragEnd` mutated the design through the view model.
Only the first half is a picture. It is split now: `SmithChartChrome` (in `src/Render/Smith`) draws,
taking an explicit `SmithChromeState` — hovered node, dragged node, hovered/dragged Q handle — and
the overlay keeps the gesture and passes its own state down.

**Why the split had to happen rather than "the export simply omits the chrome".** `PlacedPlot.Overlay`
exists precisely because it does not: its own remark records that without it *an overlay that a
`PlotControl` draws on every frame is silently absent from every export — the picture is still
produced, it still looks correct, and the arrowheads and the frequency labels the user copied it for
are gone.* A headless verb that dropped them would have reproduced that defect on purpose. The gate
compares the chrome too, by handing the in-process side the same delegate.

`SmithChromeState.None` — nothing hovered, nothing dragged — is what an export passes. Those are
states a live pointer has and a picture does not, and inventing one would put a highlighted ring on a
chart nobody was touching.

### Two rules that lived in a view model, and therefore did not exist

Both were found by R-smith10-1's source scan rather than by a failure:

- **`ActiveParameterOf`** — which parameter a gripper on an element drags — was
  `SmithChartViewModel`'s. The gripper RING is drawn wherever the chart is drawn, so the rule moved to
  `SmithComponentMap` beside `DefaultParameter`, which is the table it falls back to. Left where it
  was, a headless chart would have put a ring on a file element and the window would not.
- **VSWR and the mismatch loss** were computed inline in `ComputeStatusLine`. They are
  `SmithReadings` now, and the strip formats what that type computes. A verb deriving them a second
  time is a second chance to get a sign, a conjugate or a square wrong in a quantity whose wrong value
  looks entirely ordinary — a VSWR of 3.3 and a VSWR of 1.9 are both perfectly plausible numbers.
  (The mismatch was against `conj(Z_gen)` when this was written; Q-17 moved it to the chart's own Z₀
  on 2026-09-19, which is one line inside that same type and no change at all here — which is the
  property this entry is about.)

Same shape, one project along: **what a Smith Chart `Plot` IS** — panning unlocked, readout fixed —
was two statements in `BuildChartHost`. `SmithPlotBuilder.NewChartPlot`/`Configure` is the one place
now; the window calls `Configure` on the plot its container created, the verb calls `NewChartPlot`.
Neither flag is visible in a picture, which is exactly why a second copy would have survived.

### `MatchValueFormat` had to cross too, and it is not a Match Designer type

The load-point labels on the chart are formatted by it, and so is every frequency the verb prints. It
moved to `src/Design/Matching` (namespace `CircuitRF.Design.Matching`); the other 25 files of
`CircuitRF.Ui.Matching` stayed and reach it through the global using. It also turned out to be the
right parser for `--at`: `TryParseWithUnit` is what the window's own frequency field uses, so
`2.4 GHz`, `900 MHz` and `1.9e9` mean here exactly what they mean there, and a unit typed into a
frequency field is honoured rather than silently read as hertz.

### `--at` collided with AUT-9's global `--at`, and the verb had to win by NAME

`JsonRun.TakeFlags` pulls `--at axis=value` out of every command line before dispatch, so
`smith --at 2GHz` was answered with *"--at '2GHz' is malformed. Write --at &lt;axis&gt;=&lt;value&gt;"* —
a refusal about a flag the caller did not mean, from a narrowing mechanism for a result document this
verb does not produce.

The fix is one line, and **the test is the VERB, not the shape of the value.** Deciding by whether the
text contains an `=` would make `smith --at freq=2GHz` mean something different from
`smith --at 2GHz`, silently, which is the guess a refusal exists to avoid. This is the first verb to
own a pre-dispatch flag name; if a second ever does, the condition is where it goes.

### Three smaller ones

- **`TouchstoneIO` writes `Encoding.ASCII`**, so an em dash or an Ω in a `CommentEntry` lands as `?`.
  Both comment lines are plain ASCII for that reason. Visible, harmless and shabby — and a reader
  would take it for a corrupted file rather than for a comment nobody checked.
- **The `.s1p` carries no date comment**, on purpose (`includeDateComment` left off). The same
  document written twice is the same bytes, so the byte-identity gate needed **no exclusion at all** —
  not even the Skia `clipPath` counter the render gates allow for, which the SVG comparison also came
  back clean of. A caller can diff two revisions of a matching network and see only the network.
- **`RenderDataDisplay` grew two internal seams rather than a third composer.** `Emit` is the page,
  the theme, `PlotComposer`, `PlotDocumentWriter` and the write, now shared by the `.cdd` half,
  `plot` and `smith`; `Place` takes the six numbers directly for a plot that no `PlotContainerConfig`
  describes. A `.csmith` chart is a third way of ARRIVING at a `PlacedPlot`, and from that point on
  there is one set of decisions.

### `--set` is refused, and that is the honest answer

A `.csmith` states every element value as a number in base SI and holds no expression scope, so
nothing in it is an override's to replace. `rail`'s own `--set` refusal is the precedent and §3.3's
accepted-and-dropped defect is the reason: a run that took the flag and answered a different question
than the one asked. The sentence names `--at` — the one thing that IS overridable — and the standing
rule for everything else: once a document exists, the way to change it is to write it.

## `smith --sweep` is gone, and `-o out.s1p` usually writes a band now (2026-09-19)

The swept band stopped being a setting (owner instruction — see `src/Ui/RESOLVED.md`): it is the
generator table's own span, always walked. So the flag that turned it on had nothing left to turn on
and is removed rather than accepted-and-ignored, which is §3.3's own rule.

**The visible consequence is the Touchstone.** §18.4's pair of answers is unchanged in shape — a
caller who asked for a band must not get a point, and the reverse — but which one you get is now
decided by the TABLE rather than by a flag: a multi-row table writes the whole band, and a
single-row table writes the one frequency the report is about, because one row is one impedance and
a band needs two ends. `SmithBandJson` lost its `Clamped` field with the clamp it reported.

`--at` is untouched and is now the ONLY thing that can put a design frequency outside the table's
span — the document's own is the table's median, which is inside it by construction. That is why
`SmithDesign.DesignFrequencyOverrideHz` exists and why `SmithDesign.Refusal` still carries the rule.

## `check` and `explain --footprints` (brief-footprint-4 R-fp4-4, 2026-09-20)

`explain` is a **seventh** question now, refused beside the other six rather than ordered against
them, and the MCP tool gained the matching flag because R-aut-13 says nothing reachable from the
command line may be unreachable there.

**Neither verb writes a rule of its own.** Both findings and the whole report come from
`FootprintCatalog` in `src/Design` — the same resolution `SchematicToLayoutGenerator` performs. A
rule living only here is a rule the application does not enforce, and a design that passed
headlessly and was refused when somebody opened it would be the worst possible answer for a build
machine.

Three things about the shape of the answers that are worth knowing.

- **`--footprints` reads the SCHEMATIC, never the netlist.** `Footprint` is artwork, not a value,
  and R-fp2-6 drops it before parameter resolution — it is not in an elaborated netlist and never
  will be. A version that asked the netlist would report every design as stating none, with nothing
  saying why.
- **The technology is reported for a built-in and only for a built-in**, and resolved ONCE for the
  document rather than per component. A generated land pattern picks its copper, mask and
  silkscreen BY ROLE against whatever technology is in force, and the shipped technologies disagree
  about every layer key (the series overview's §1b), so which one that is is part of what the
  artwork WILL BE. A cell's artwork is already on disk on keys of its own; naming a technology
  beside it would suggest it was about to be re-resolved.
- **`check`'s two findings are warnings and the exit code stays 0.** The design is still simulable
  and what is missing is artwork. The pad count is printed against the port count on the same line
  for the same reason the GUI's refusal names both: two numbers in two places is how a mismatch
  goes unread. The one that bites is an `SnP` with `RefNode` set, which has one more port than its
  file has — `EffectivePortCount`, not `PortCount`.

---

## `netlist` over a BOARD — brief-authored-board-3 (2026-09-20)

**`netlist` grew a second document kind rather than a fourth verb.** A board netlist, a placement
table and a bill of materials are the extraction a LAYOUT performs, which is the same sentence
`netlist` already makes about a schematic. `src/Cli/NetlistBoard.cs` holds the argument parsing, the
target resolution and the refusals; every byte comes from `BoardCompanions` in `src/Design`, which is
what the layout editor's File ▸ Export rows call.

**Which extraction runs is decided by the document kind, and by the flags for a cell folder.** A
`.clay` is always the board path. A cell folder holding both views is the ordinary case, so the
presence of any of `--ipc` / `--placement` / `--bom` is what says which view is meant — asking for a
placement table out of a cell is unambiguous, and defaulting to the schematic there would silently
extract the wrong document.

**Two disagreeing sentences in the brief, resolved in favour of the explicit one.** R-ab3-2e says a
part whose pins cannot be joined "writes no records for that part and is named"; gate 8's summary
lists the same case among the ones where "the output files do not exist". The per-part reading is
what is implemented, because it is brief 1's own behaviour (`PdnLayoutPads.JoinPinsToPorts` returns
null, the instance contributes nothing and a note names it) and because refusing to write a whole
board over one 2-pin part whose footprint pins are named inconsistently would make the verb unusable
mid-design — which is the same argument R-ab2-5c makes for reporting a divergence rather than
refusing on it. The two whole-board refusals — over the flatten ceiling, and a piece of copper
carrying two names — write nothing at all, and the gate asserts the files' absence.

**The three UI rows are one method.** `LayoutEditorView.OnExportCompanionAsync` takes which table it
is writing; three pickers differing only in an extension is how one of them comes to write a
slightly different file, which is the drift the Gerber/GDSII/DXF handlers beside it already warn
about. The refusal is checked BEFORE the picker is shown — asking where to put a file that will not
be written is the wrong order to ask it in.

**`ExportBoardNetlistCommand` / `ExportPlacementCommand` / `ExportBomCommand` are in BOTH
`NotifyCanExecuteChanged` fan-outs.** That is this file's standing gotcha and Gerber's own scar is
two lines above them in `WorkspaceViewModel`: a `[RelayCommand(CanExecute=…)]` gated on the active
document is not re-evaluated on its own, and one missed from a fan-out is a menu row greyed out
permanently with nothing to say so.

---

## `netlist`'s board half was not reachable through `serve` (2026-09-20, review of brief-authored-board-3)

R-aut-13 — restated in `ToolCatalog` six lines above the `explain --footprints` entry the footprint
series added — is that a capability reachable from the command line is reachable from the MCP
surface. The board projection was not: the `netlist` tool declared only `-o` and `--cell`, and its
`path` said "A .csch, a cell folder, or a workspace with cell". A caller that handed it a `.clay`
therefore got R-ab3-2b's refusal, which names three flags — `--ipc`, `--placement`, `--bom` — that
the tool did not offer, so the refusal was a dead end rather than an instruction.

The three are declared individually rather than folded into `-o` for the reason the refusal itself
gives: two of the three tables are `.csv` and the extension cannot say which.

---

## `lvs` — the verb, and the two things the brief could not have known (2026-09-21)

`brief-lvs-11-cli-verb.md`. The verb itself is unremarkable by design — argument parsing, refusals,
reporting and one call into `LvsRun.Run` — which is the point. Contract in `cli.md` §19. Three
findings are worth keeping.

**`--set` had nowhere to land, and adding it to the CLI would have been the wrong place.**
`LvsRunOptions` carried no override and `SchematicRead.Read` took none, so the only way to honour
R-lvs11-2d from inside `src/Cli` would have been to load the `.csch`, round-trip it and elaborate it
here — which is exactly the second comparison the brief's own source scan exists to forbid. The
override is a field on `LvsRunOptions` instead (`Set`), applied in `SchematicRead` immediately after
the round trip and immediately before the elaborator, so **the GUI panel gets it for free and the two
surfaces cannot diverge**. Worth knowing where it does and does not reach: it changes the resolved
parameter VALUES brief 10 compares, and it does not change topology, because LVS reads its topology
from the drawing's own instances and nets rather than from the elaborated netlist (R-lvs4-2a).

**A global the design never declared is still settable, and that is what makes the gate sharp.** The
apply is `RemoveAll` then `Add`, so `--set Rshunt=294` binds a name nothing declared. The gate uses
it: a copy of the correct board with R3's value re-pointed at `Rshunt` gives three distinguishable
answers from one design — no flag is `lvs.schematic.elaboration-failed`, `Rshunt=294` matches,
`Rshunt=150` is `lvs.property.mismatch` with both values typed. A fixture whose two answers were
"clean" and "clean" would have proved nothing.

**Brief gate 11's premise does not survive the fixture, and the gate was rewritten rather than
tuned.** It asks that `--no-reduce` and `--flat` *change the counts* on the correct board. Measured:
they change nothing at all. Neither example board has a series or parallel group to collapse, and the
MMIC's only sub-cells are leaf parts, so every count is identical in all four combinations. What both
flags DO change is what the run says it did — `lvs.reduce.mode` on both sides, the `reduction` field
per cell, and the word in the human report — and that is what `LvsCliVerbTests` pins. The half of the
requirement that had a real fixture behind it (R-lvs6-5c: the mode is on the face of the result
either way) is fully covered; the half that did not is recorded here rather than faked with a
purpose-built board nobody else uses.

**A refusal carries no `lvs` payload, deliberately.** R-lvs11-4c says a refusal must not be reported
as a clean run with a note, and under `--json` the way to make that structural rather than a habit is
for `JsonRun.Lvs` to stay null: the document then has no `result.lvs` key at all, so a caller cannot
read a run that could not happen as one that concluded something. The gate asserts the absence, not
just the exit code.

## `reference layout`, `em-setup` and `wbond` — the 2D authoring pages (2026-09-24)

Three more generated format topics, so a client can write the documents an EM or wirebond run needs
without copying one that happens to be on the machine. `docs/design/em-3d.md` §4.6 points at them:
the 3D setup it plans is a `.cem` generated from the same three inputs, so these pages are the
authoring surface 3D extends, not a 2D side project. Each example was run end to end when written —
the `.clay` through `check` and `render`, the `.cem` through `em` (4 points, ~5 s, |S11| < −35 dB),
the `.wBond` through a `.cnl` and `sparam` — and `GeneratedReferenceTests` holds the cheap half: each
example is read by the format's OWN reader, not merely parsed as JSON.

**The walk could not describe a `.clay` before this, and nothing said so.** `DocumentSchema.Walk`
listed a polymorphic base's own four fields and stopped: `LayoutShape` names neither its `$type`
discriminator nor any derived kind, so the page had no coordinates on it at all. It now reads the
same `JsonPolymorphic`/`JsonDerivedType` attributes System.Text.Json reads, adds the discriminator row
to the base and one block per kind, titled with the value that selects it. Arrays (`long[]` — a
polygon's `Xy`) also spelled as the CLR name and were not descended into; both fixed.

**`$type` must be the FIRST field of a shape.** Written after `Layer`, the whole file is refused
("must specify a type discriminator") — System.Text.Json's default, measured, not assumed. The page
says so, because a hand-written or generated file will not naturally put it first.

**`WBondIo`'s four DTOs are public now**, for the walk and nothing else — they were private nested
types, which reflection from another assembly cannot name at compile time. The comment above them
says no one may construct one.

**A wBond's relative `File=` resolved against the PROCESS's working directory — fixed in
`CnlReader`.** The same `.cnl` ran from its own folder and failed from anywhere else. The elaborator
already resolved it (`ResolveWBondParameters` → `ResolveSnpFilePath`), but only against
`Elaborator.BaseDirectory`, which the GUI sets to the workspace root and **no run verb sets at all**.
What the run verbs rely on for an SnP is `CnlReader`, which makes a relative `File=` absolute against
the source directory at read time — the `.cnl`'s own folder, or a `.csch`'s workspace root through
`SchematicCircuit.ReferenceBaseOf` — and that rule named SnP alone. It now covers wBond too. The GUI is
unchanged: Simulate reads `netlist.cnl` at the workspace root, so the reader's base and the
elaborator's are the same folder. Gate: `CnlReaderTests.AWBondsRelativeFile_ResolvesAgainstTheSourceDirectory`,
and the page's example now names the file relatively.

**`DispersionCorrection` is the one `.cem` flag whose omission is not the GUI's default** — the field
is non-nullable on purpose (its model default flipped to true and every existing file carries it), so
a file that leaves it out reads false. The page says to write it.
