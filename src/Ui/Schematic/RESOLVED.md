# Schematic — resolved issues (see also `CLAUDE.md`)

Per-topic notes that don't belong in the standing `CLAUDE.md` file. Newest first.

## An unsaved schematic's relative references resolved into the recovery folder (2026-09-04)

**Symptom:** a placed SPICE model reported that its file was not found, naming a path inside the
per-session autosave directory under `LocalApplicationData` — a folder nothing had ever been pointed
at, and one that is deleted on clean exit. Reported separately: an unsaved schematic behaves oddly in
general. Both are the same defect, seen from two sides.

**A space in the containing folder name is not the cause and never could be.** The failing call is
`FileInfo.Exists` inside `SpiceModelPeek.Read` — managed code, no shell, no quoting anywhere on the
path. The 12 hex characters in the reported directory name are the tell:
`Guid.NewGuid().ToString("N")[..12]`, which is `RecoveryManager`'s session id and nothing else in the
product.

### 1. Autosave rebased the live document (the headline)

`SchematicPersistence.SaveToFile` records the directory it wrote to *on the model* — deliberately, so
a New-Schematic document acquires a base directory the first time it is really saved, which is what
`CellRef` resolution needs. `RecoveryManager.AutoSave` called that same method with a destination
under `LocalApplicationData/circuitRF/recovery/<session>/`.

So **30 seconds into editing an unsaved schematic, `SchematicDirectory` silently became the recovery
folder** — and stayed there. Every relative reference the document carried resolved against it from
then on: the SPICE model file, Touchstone, `CellRef`. No gesture triggers it and nothing on screen
changes; the design is simply broken from that tick onward. Worse than a wrong answer, it also turned
honest guards into false passes — cell placement's `if (SchematicDirectory is null)` refusal stopped
firing, and computed a `CellRef` relative to the recovery folder instead.

`SaveToFile` now takes `rebaseDirectory` (default true — a real save still rebases); `AutoSave`
passes false. **An autosave is not a save-as.**

### 2. A restored document was based on a folder about to be deleted

`RecoveryManager.LoadSession` reads the recovery `.csch` through `LoadFromFile`, which bases the
model on the file it read — the recovery directory — and `WorkspaceViewModel.CheckForRecovery`
deletes every prior-session directory immediately after restoring. A restored document is a SCRATCH
document again, so `LoadSession` now clears `SchematicDirectory`.

### 3. Store and resolve used two different workspace roots

Independent of recovery, and the reason a scratch schematic could not hold a SPICE model at all:

- **store** — `ParameterEditorViewModel.PickSpiceModelFileAsync` passed the OPEN WINDOW's root
  (`SchematicViewModel.WorkspaceRoot`, i.e. `CurrentWorkspacePath`).
- **resolve** — `SpiceModelSymbolProvider.ResolvePath` walks UP from the schematic's own directory
  for a `.cws`, falling back to that directory when there is none. This is deliberate (a foreign
  document belongs to the workspace it came from) and is documented on the method.

They agree for a saved document inside the open workspace and disagree everywhere else. A scratch
document has no directory to walk up from, so the pick stored a workspace-relative path that the
resolver could only join to whatever base the model happened to carry. **Before the first autosave
that base is null, so the component drew as the pinless broken-reference placeholder — unwirable;
after it, the recovery folder above.**

Fixed at the store end: `SpiceModelSymbolProvider.ToStored` now sits beside `ResolvePath` and derives
the root by the same walk-up, so the pair cannot drift. With no root there is nothing portable to
write, so the absolute path is kept — which is what `SnpPathPolicy.ToStored` already does for a null
root, and what `MoveRefRegistry` already promises to leave alone (`Path.IsPathRooted(was) ? abs : …`).

**Note the SnP path does NOT share this rule**: `ParameterEditorViewModel` stores and previews a
Touchstone against the window root, and `WorkspaceViewModel`'s Run passes the window root to
`Elaborator.BaseDirectory`. Self-consistent, so SnP works in a scratch document — but it is a second
rule, and the two are worth collapsing if this area is touched again.

### What is still degraded in a scratch schematic (pre-existing, unchanged, reported not fixed)

Everything keyed on `SchematicDirectory` is inert or refused until the document is saved:

- **Microstrip technology defaults** — `MicrostripSubstrateInjection.ApplyTechnologyDefaults` resolves
  the technology by ancestor `.cws`, gets null, and returns without doing anything. An MLIN placed in
  an unsaved schematic keeps the hardcoded mm defaults instead of the workspace's own unit, and gets
  no 50 Ω width synthesis. **Saving does not retro-apply them** — the values are already the user's.
- **Cell placement** refused (with a clear message), and `PlacedCellRef.HashFor`, `CellMoveWatch` and
  `CellInterfaceWatch` are inert, so no staleness is detected.
- **A linked wBond payload** cannot resolve its path.
- `ParameterRowViewModel` / `ParameterEditorViewModel` skip `ResolveCcell` for a non-kit `CellRef`.

The scratch → workspace save itself is sound: `SavePlanExecutor` calls `SaveToFile` on the default
path, so the base directory is set exactly once, by a real save.

**Gates:** `tests/Ui.Tests/RecoverySessionLifecycleTests.cs` (three new — scratch and saved documents
both survive an autosave, a restored document has no base directory; all three verified red without
the fix) and `tests/Ui.Tests/SpiceModelScratchPathTests.cs` (the store/resolve round trip, saved and
scratch, with a space in the containing folder name carried through every case).

## VAR/MEAS label placement, and a parameter added after the labels were moved (2026-09-02)

Two reports about the same label block. Both are geometry, and both were single-source-of-truth
problems rather than drawing bugs.

### The anchor was sized for a part with leads

`SchematicComponent.LabelBaseOffsetX` (-155) and `LabelBaseY` (280) are one shared anchor for every
symbol. They were chosen for a two-terminal part, whose leads run out to ±200 — the block has to
clear them, so it starts left of the body and hangs well below it. **A VAR or a MEAS has no leads.**
Its glyph is an ±80 × ±60 box, so on those two the shared anchor put the type, the instance name and
every row down and to the LEFT of a box with nothing to clear: adrift of the symbol they belong to,
which is what the report was about.

`LabelBaseXFor(SymbolKind)` is now the X half of the anchor, mirroring the existing `LabelBaseYFor`,
and `LabelRowGeometry` — the one helper the renderer, the hit-test, the cull box and the inline
editor all read — calls it. For VAR/MEAS it returns the box's own left edge (-80, left-justified to
the glyph), and `LabelBaseYFor` returns `glyphBottom + AnnotationLabelPadY + LabelWorldHeight`, so
the first row's CAP TOPS clear the box bottom by the padding rather than its baseline doing so. The
Y is measured from the REAL glyph extent the caller passes, not from the constant, so it stays right
if the box ever changes size. Row pitch is untouched.

This moves existing VAR/MEAS instances too, not only newly placed ones — the anchor is computed, not
persisted, and a saved `LabelOffsets` is a delta from it. That is deliberate: seeding the placement
with per-instance offsets instead would bake one geometry decision into every file and guarantee the
drift this shared helper exists to prevent.

### A parameter added after a label move rendered back at the default position

`LabelOffsets` is parallel to `Labels` (0 = type, 1 = instance name, 2+ = shown parameters) and is
written whole by `MoveLabelsCommand`, so it is exactly as long as the label list was **when the move
happened**. Add a parameter afterwards and the new row has no entry — and every reader spelled the
same fallback, `row < Count ? offsets[row] : (0,0)`, which put that one row back at the un-moved
default anchor while the rest of the block stayed where the user dragged it.

`SchematicComponent.LabelOffsetAt(offsets, i)` replaces all five copies of that expression (renderer,
hit-test, `EditableComponent.GetLabelOffset`, the FullBb pass, the Match label reader) and falls back
to the LAST stored offset instead of to zero: rows below the last moved one belong directly under it.
`CommitMoveLabels` and `ResetLabelOffsets` pad their snapshots the same way, so a second move does
not re-orphan the row the first one never knew about.

### Two stale copies of the label arithmetic, found on the way

`SchematicCanvas.RaiseLabelDoubleTap` and `BeginInlineParamEdit` each recomputed the label anchor by
hand — `cpx - zoom*155`, `cpy + zoom*120 + textSize + row*(textSize+2)` — an approximation of a
helper that had already moved on (the real first-row baseline is 280, not ~190, and the real pitch is
72 world units, not `textSize + 2`). It survived because the double-tap position is immediately
recomputed by `SchematicView.RepositionInlineEditBox` from the real helper, and because
`BeginInlineParamEdit` sweeps candidate rows and accepts only a probe the real hit-test agrees with —
a search that absorbed the error. It could not have absorbed a per-symbol anchor, so both now call
`LabelRowGeometry` through one small `LabelRowAnchorWorld` helper.

## What the "+" button offered a ZNP and an SDD — neither could use it (2026-09-02)

Reported against the ZNP: on a Z1P the Parameter Editor's "+" creates a `Z[1]`, which does nothing.
It is worse than nothing, and the SDD, checked at the same time, was worse still.

### `IndexedParamGroup` cannot describe either component's parameters

The "+"/"−" buttons add and remove one `Name[n]` group for an ever-increasing n. That fits P1Tone's
`Z[k]`, a tone source's `Freq[n]`/`V[n]`/`Phase[n]`, a VAR row. It fits neither of these:

- **A ZNP's parameters are exactly the N×N `Z[p,q]` matrix its port count fixes**, all of them
  seeded at placement. There is nothing to add. `ComponentModelFactory` reads a Z entry through
  `^Z\[(\d+),(\d+)\]$` only, so the added `Z[n]` matched nothing, fell through to `numericParams`
  under a name no expression can reference, and was inert in every path on every run.
- **An SDD's slots are two-dimensional and bounded by the port count.** `TryParseTemplateIndex` reads
  one index, so it saw the seeded `I[1,0]` as an unindexed name: the first "+" offered `I[1]` —
  which IS valid sugar for `I[1,0]`, so `ValidateAndBind`'s `target[p-1] = …` **silently replaced
  the seeded equation**, whichever of the two the parameter dictionary happened to yield last. Press
  on and `I[3]` on a 2-port is a hard refusal at Run ("references port 3 but only 2 port(s) of nets
  were given"). And every added row arrived BLANK, which is a `ParseException` at Run, because an
  SDD parameter reaches the factory verbatim and is parsed there.

### ZNP: no "+", and no row rename either

`UserParamTemplate` returns null for `ZPort` now, which removes both — and the rename is the same
fact from the other side, since a renamed `Z[i,j]` stops being read at all. A helper constant reaches
`Z[i,j]` expressions from a VAR component, which `Elaborator.InjectZPortScopeVars` already resolves,
so nothing was lost. `IsRemovableParameter` gained a ZPort arm so a design already carrying an inert
`Z[n]` can still delete it: a matrix entry is structural and never removable, anything else is.

### SDD: a picker over the slots THIS device can still use

`SddEquationSlots.Available(portCount, existing)` enumerates them and `SddEquationPickerDialog`
shows them; `ComponentTypeRegistry.AllowsIndexedParamAdd` excludes the SDD from the generic "+"
while it KEEPS its template, which still drives row-name editing and canonical sorting. The rules
that make an offer safe are the catalog's, not the dialog's:

- Nothing already present, **under either spelling** — `I[p]` and `Q[p]` occupy `I[p,0]` and
  `I[p,1]`. That is the duplicate that was silently replacing a seeded equation.
- No port past the port count.
- `V[p]` and any current equation at the same port are **mutually exclusive** (the factory refuses a
  port stating both), so each suppresses the other. On a freshly placed SDD every port carries an
  `I[p,0]`, so the branch equation is offered nowhere — `SddEquationSlots.Notes` says so in the
  dialog, because a silently absent slot reads as a missing feature.
- A weighted `I[p,w]` for a NEW w is created **together with its `H[w]`**, in one command so one
  undo takes both: the factory refuses an `I[p,w]` whose `H[w]` is undefined, and an `H[w]` nothing
  references does nothing.
- `C[n]` is offered **only once an equation already reads `_cn`** — its value is an instance NAME,
  not an expression, so there is no seed that runs, and it is the one slot deliberately created
  blank. Offered speculatively it would be exactly the defect being fixed.
- **Nothing else is ever created blank.** A current or charge is seeded at `0`, which is not a guess:
  `SddModel` documents an absent equation as zero, so the seeded row means what the absent slot means.

Per-row "✕" replaces the footer's "−" for the SDD (`IsRemovableParameter`), which is what a picker
implies — one named slot added, one named slot removed.

### Two more holes the picker's own gate test found

Both are cases where the shape being added was legal and the surrounding machinery refused it.

- **`ParameterRowViewModel.TryValidateSddName` had drifted behind the factory.** It accepted only
  `I[p]`, `I[p,w]`, `Q[p]` and `H[w]`, so `V[p]`, `C[n]`/`Cport[n]` and a plainly-named constant —
  all read by `CreateSddModel` — were refused by a dialog whose own engine runs them. It now accepts
  what the picker creates, rejects a leading `_` (a constant shadowing `_v1`/`_c1` would change what
  every equation using it means), and takes a `portCount` so `I[3]` on a 2-port is refused HERE
  rather than as the factory's own message a simulation later.
- **`CnlReader.SddAssignmentHeader` recognised neither `V[p]=` nor a plain `name=`.** That regex
  decides where one assignment's expression ENDS, and the failure hid perfectly: a line whose ONLY
  assignment is one of those never reaches the scanner — the generic whitespace-token path handles
  it, which is why every existing `V[p]` test passes. Put one beside an equation and the PRECEDING
  equation swallows it, and the line dies with `Parse error at position 12: Unexpected '='` pointing
  into an expression nobody wrote. So `V[1]=0.5*_v2` alone worked and
  `I[1,0]=_v1/50  V[1]=0.5*_v2` did not; likewise a per-instance constant could not be written
  beside an equation at all. The plain-name alternative is guarded on both sides — `(?<![\w\]])` so
  it can only start a token, and `=(?!=)` so an unparenthesised `==` is not read as a new assignment
  (a parenthesised one is already at depth > 0, where the scanner does not look).

Gates: `tests/Ui.Tests/SddEquationSlotsTests.cs` — every offered slot is added at its seeded value
and ELABORATED, against the real factory rather than against the catalog's own idea of the grammar,
with the two parameters the old "+" produced as the counter-case that must still refuse;
`tests/Core.Tests/Netlist/SddWhitespaceTests.cs` for the two reader boundaries;
`ParameterEditorAddParamTests` and `SddEquationNameValidationTests` for the gating and the grammar.

## Palette and Parameter-dialog polish: the plain Z tile, SPICE's filters, the Tuner pickers (2026-09-01)

Four small owner requests, one of which turned out to have a real defect underneath it.

**The bare "Z" tile is gone; Z1P/Z2P/Z3P stay.** The plain (PortCount == 0) tile for
`SymbolKind.ZPort` placed a 2-PORT impedance network — precisely what the Z2P tile beside it places
— so the palette offered one part under two names and the second name did not say what it did.
`LibraryCatalog.NoPlainTileKinds` suppresses it. **It is deliberately NOT `InternalOnlyKinds`**: that
set removes a KIND from the catalog entirely, and ZPort must stay — it is still in `AllItems` (via
its port-count entry points), still searchable, still placeable. `RecentlyUsed` needed nothing: its
`FirstOrDefault(PortCount == 0) ?? g.First()` already falls through to Z1P.

**SPICE lists under Devices and Nonlinear as EXTRA categories, keeping DataFiles as its primary.**
What a `SpiceModel` references is usually a transistor or a diode, so somebody shopping for an active
part has to find it there — but the component itself IS a file reference, and its primary category is
what decides where it sorts and that it appears exactly once. Same shape as R-hk-4's Z1P/Terminals
and the SDD1/SDD2 Devices keyword.

**`BiasTee` and `ShowBias` are pickers now** (`ComponentTypeRegistry.NamedParamOptions`, shared by all
three Tuner tiles). Neither is a number and each takes one of two spellings, so a text box could only
ever be got wrong: a typo'd `ON ` or `0` reads as the default with nothing said anywhere. **Both
commit BARE, not quoted** — `off`/`on`, `false`/`true` — matching what `DefaultParameters` writes,
because `CnlReader` is what quotes `BiasTee` on the way to the elaborator (a quoted spelling here
would arrive double-quoted) and `ShowBias` never reaches the engine at all. They ride the
`IsRegistryChoiceParam` path, so they keep their place in the registry's parameter order rather than
floating to the top of the dialog the way a kit part's choice row does.

**What the Tuner's frequency response actually is**, since the user docs now state it and it was
worth checking rather than assuming (`TunerModel.GetZ`):

| | What it presents |
|---|---|
| DC (\|ω\| < 1 rad/s) | `Zdefault`, behind the ideal DC-block cap |
| S-parameters (`_toneFreqHz == 0`) | **`Z[1]`, flat over the whole band** — `Z[2]`, `Z[3]`, … are ignored |
| HB | `Z[k]` at k = round(ω/ω₀); undeclared or off-grid → `Zdefault` |
| Loadpull / pursuit | the same, except the tuned harmonic comes from the grid point |

And the SourceTuner's RF generator is stamped only when something has called `SetSourceDrive` — which
only `LoadpullEngine`/`LoadpullPursuitEngine` do. **On a plain HB testbench a SourceTuner is a passive
termination**, because `HbEngine.GiveTunerItsBandRuler` sets a tone only for `TunerRole.Load` and
nothing outside the loadpull engines ever assigns a role.

## A page whose body opens on a heading lost its LEDE from the docs search index (2026-09-01)

Reported as "searching *bondwire* does not find the wBond page", which it should have: that page's own
lede reads "Bondwire arrays: geometry, inductance, …". It was never indexed.

`SearchIndex.Sections` yields a lead section only when there is prose BEFORE the first heading, and
`Add` attaches the lede to that lead section. But a Reference Guide page opens with its on-page
contents card — a `<nav>`, which `Strippable` removes — and then its first `h2`. So there is no lead
section, and the lede is dropped. **19 of the 35 pages**, including every long reference page, were
carrying their one-sentence summary nowhere in the index. `Add` now inserts an empty lead section when
a page has a lede and no lead section of its own, and the existing line fills it.

The wBond page also names the one-word spelling in its opening sentence and in its first heading;
with the heading match ("bondwire" scores 8 + 5 in a heading, plus the 40-point whole-phrase bonus)
the page goes from a 6% margin over the landing page to a 3× one.

## Behavioural sources become two different things, chosen by SHAPE (2026-09-01)

`brief-spice-behavioural-sources.md` M2–M4. A controlled source line is translated by looking at what
its transfer IS, not at which letter wrote it (`SpiceSourceTranslation`):

- **One sensed quantity, one coefficient, no offset** → the ideal `VCVS`/`VCCS`, a LINEAR element.
  That matters beyond neatness: 53 of the 234 controlled-source lines measured are ideal, and turning
  each into an equation-defined device would make a linear macromodel nonlinear — an S-parameter run
  on it would start needing an operating point it has no reason to need.
- **Anything else** → the equation-defined device, carrying the expression as a port current
  (`I[1,0]`, behavioural current source) or as a held port voltage (`V[1]`, behavioural voltage
  source — see `src/Engine/RESOLVED.md`).

Every form is normalised to the `VALUE` one by the reader first, so translation reads ONE shape: a
positional gain is exactly `k*V(c+,c−)` and a current-controlled source exactly `k*I(Vname)`.
`SpiceBehaviouralSource` then parses that expression with circuitRF's own parser — never the text —
and turns each `V(…)` into a port and each `I(…)` into a control reference. **The source's own pair is
port 1 whether the expression names it or not**, and an expression that reads it reads `_v1` rather
than opening a second port onto the same two nodes.

**A zero-volt `V` line is an `IProbe`, not a zero-volt supply.** That idiom is the majority of the `V`
lines in these files, it exists so something else can name the branch current, and `IProbe` is
precisely the component a control reference can name.

### Two traps, both silent

- **A variadic symbol reads its own port count off a PARAMETER.** `SubcircuitCellBuilder.PlaceElement`
  asked `SymbolPortDefs.For(kind)` before the parameters were on the component, so an equation-defined
  device of any width got the two-port default — four pins for a device the netlist binds six nets to.
  Nothing looks wrong on screen; the extra nets are simply bound nowhere. Gated by
  `SpiceSourceImportTests.S18`, which extracts the built schematic back and checks the terminals.
- **A `.func` must be inlined at TRANSLATION, not at model construction**, or the written cell is not
  self-contained — see `src/Core/RESOLVED.md`. With the file's own functions substituted, any call
  LEFT in an expression is one circuitRF does not have, and the translator refuses it there by name.
  That cost 3 of the 36 subcircuits that would otherwise have "imported" — and took the number that
  reach a DC operating point from 2 to 22, because every one it removed threw from inside the solver
  instead.

## The SnP `File` base was wrong in the editor, and is now ONE function (2026-09-01)

`SnpPathPolicy.ToStored` writes a picked path relative to the WORKSPACE ROOT (that is what makes a
design portable) and `Elaborator.ResolveSnpFilePath` resolves it against the same root at Run, which
`WorkspaceViewModel` supplies as `CurrentWorkspaceRoot`. **`SetSnpFileCommand` resolved it against
the SCHEMATIC's own directory** when it sniffed the port count off disk.

The two bases agree only when the schematic sits at the workspace root — the usual layout, and why
nothing ever reported it. For a schematic in a sub-folder (`cells/Amp/schematic/`, which is where a
cell's schematic actually lives) the sniff missed a perfectly readable file, `TryGetPortCount`
failed, and the command fell back to the OLD `NumPorts` — so an inline edit of `File` from a 2-port
to a 3-port left the symbol drawing two pins and the netlist binding two nets, silently. Only the
INLINE edit path was affected; the dialog's Browse… sniffs the absolute picked path.

**It was already known and worked around rather than fixed**: `EmBackAnnotation`'s
`SetSnpReferenceCommand` cited exactly this as its reason for not reusing the command. That
workaround stands on its own merits — the EM kernel knows the port count exactly and has no reason
to re-read it — and its comment has been corrected so it no longer describes a live defect.

The fix is `SnpPathPolicy.Resolve(stored, workspaceRoot, schematicDirectory)`: one function, placed
beside `ToStored` because the two are halves of one contract, mirroring the elaborator's rule
including its tolerance of a Windows-authored `\` in a relative path. `SetSnpFileCommand` now takes
the workspace root as a constructor parameter — **it is passed, not derived**, because the run's own
root is `CurrentWorkspaceRoot` and a command holding only a `SchematicEditModel` cannot see it;
walking up to the nearest `.cws` would give a different answer for a foreign document. The Reveal
button and `SpiceModelSymbolProvider.ResolvePath` call the same function.

`SpiceModelSymbolProvider` DOES derive the root by walking up to the nearest `.cws`, because it is
reached from `CellSymbolResolver` and `NetExtractor` — both framework-free, neither holding a view
model. For a document inside the open workspace the two agree; for a foreign document the walk-up is
the one that is right. Editor and extractor call the same function, so the drawn pins and the
simulated circuit cannot disagree about which file was read.

Gate: `tests/Ui.Tests/SnpFilePathResolutionTests.cs`. The two tests that drive the command through a
sub-folder schematic were confirmed to FAIL against the old base before the fix went in.

## The `SpiceModel` component — a SPICE file placed, not copied (2026-09-01)

The reference half of the pair whose other half is the import above: **Copy to Workspace as Cell…**
makes an editable cell out of a `.model` card or `.subckt`, and `SymbolKind.SpiceModel` places a
component that runs the file where it lies. Both read it through the same `SpiceCellImport.Scan`,
deliberately — a definition that imports as one thing and places as another is a bug neither side
can detect. There is no pop-in: there is no `.csch` behind it, and `HierarchyResolver.CanPushInto`
already refuses on `CellRef is null` with nothing added.

### The symbol is a FOURTH virtual reference, and the alternatives were each ruled out

`SpiceModelSymbolProvider` registers a `spicemodel://` scheme on the seam `CellSymbolResolver`
already holds open for `pdk://` and `wbond://`. The three existing mechanisms each fail on a
different point:

- **A fixed `SymbolKind`** draws one glyph. This one draws a *diode* for a diode card and a
  *four-pin box* for a four-port subcircuit, and which is a property of the FILE.
- **A variadic `SymbolKind` + `PortCount`** (SnP, SDD, ZPort) lets the USER set the count. Here the
  file already knows it, and the pins carry the subcircuit's own port NAMES, which that route has
  nowhere to put — the same objection `WBondSymbolProvider` records.
- **A `CellRef` to a folder** needs a `.csym` on disk, which is a second copy of the interface that
  goes stale on the next edit of the file. That is the whole reason the reference form exists.

The reference is `pitch | pinconfig | name | file`, derived from the instance's parameters on every
access and never persisted. **The file's CONTENT is deliberately not in it**: `SpiceModelPeek` keys
its own cache on mtime, so editing the `.subckt` and returning to the schematic redraws the pins
with nothing here invalidated.

### An unconfigured instance resolves to `Resolved`, not `NotFound`

A blank `File` draws the generic two-port and IS wirable. That is not tidiness: the
broken-reference placeholder has no pins at all, so a component drawn as one cannot be dropped and
wired first and pointed at a file second, which is the order people actually work in. A file that is
SET and does not read is `NotFound`, and a definition that is named and refused is `PrimaryMissing`
— both report.

### A MESFET card is the one `.model` that emits a CELL rather than a device

`RD`/`RS` on a MESFET card are real series resistors and circuitRF's MESFET has no parameter for
them (unlike the diode's `Rs` and the BJT's `Rb`/`Re`/`Rc`, which the elaborator mints internal
nodes for). There is nowhere on a bare device instance to put them, and dropping them is ohms in the
source and drain leads of a power device — so `SpiceModelNetlist` mints a cell holding the device
and its two resistors, exactly as importing the same card as a cell already builds, and says so in
the extraction report. Every other card emits the primitive directly, which keeps probe paths flat.

### Two files defining one name is reported, never resolved by read order

`NetlistImports.SpiceCells` records which cell name came from which file for the whole extraction, so
two instances of the SAME file share one cell (one definition placed twice) while two instances of
DIFFERENT files that both define `lowpass` refuse the second rather than binding it to the first
one's circuit. A design's own cell of that name always wins, as it does on the kit path.

### Known and deliberate: the first peek of a large file happens on the render path

`SpiceModelPeek.Read` is reached from `CellSymbolResolver.Resolve`, which runs during a schematic
model rebuild, and it parses the whole file through `SpiceCellImport.Scan`. The mtime cache makes
that **once per file per session**, so the cost is a single first-resolve stall, not a per-frame one
— but on a very large vendor library that one stall is real.

Not guarded by a size limit, on purpose: refusing to read a legitimate 40 MB model library would be
worse than the stall it avoids, and `LooksLikeSpice`'s own 32 MB cutoff exists for a different
question (deciding a FORMAT from content, where reading the whole file buys nothing). If this ever
needs fixing the answer is an async first load with the generic two-port shown meanwhile, not a
refusal — recorded here so that decision is made rather than discovered.

### The gate is that the TWO DOORS agree, numerically

Owner instruction, 2026-09-01: placing the file and importing the same definition as a cell must
give the same simulation results. `SpiceModelVersusImportedCellTests` runs both doors over one
`.subckt` — a shunt-branch two-port, an asymmetric three-port, and a nested part — and compares the
S-parameter `DataSet`s at 1e-9. **Neither side is the reference**: a disagreement says one of them
is wrong without saying which, which is the finding worth having. It also happens to be the only
check that can catch the IMPORT path's own characteristic failure, because circuitRF reads
connectivity off the drawing and a wire laid a grid square out does not look wrong — it joins two
nets.

Two things the test has to do to be worth anything, both learned by getting them wrong first:

- **Assert the ports are actually connected.** An all-ports-open network is every `|S|` exactly 0
  or 1 on BOTH sides, which agrees perfectly and tests nothing. The guard is one entry strictly
  between 0 and 1, written without depending on the cube's axis order.
- **Use an ASYMMETRIC fixture.** A symmetric two-port agrees with its own ports swapped. Reversing
  the port order on one door only is the mutation the suite was checked against; the three-port tee
  and the L-then-R lowpass both fail it, and the symmetric nested part does not.

The `DataSet` an S-parameter run returns holds ONE cube named `S` (N x N x freq), not a cube per
element — there is no `S(2,1)` key to look up.

### `Name` defaults to the HIGHEST-LEVEL definition, not the first

Owner instruction. A vendor file is a part plus every piece the part is built from, each a definition
in its own right and usually written leaf-first, so "the first supported one" places an internal
transistor where the user asked for the package. `SpiceModelPeek` marks a subcircuit top-level when
nothing else in the file calls it — read straight off `SubcircuitTranslation.Dependencies`, which the
translator has already resolved transitively — and a card is never top-level, because a card in a
file that also defines subcircuits is there to support one of them.

## Importing a SPICE `.subckt` as a cell (2026-09-01)

The same two doors now take a `.subckt` definition as well as a `.model` card — the project tree's
**Copy to Workspace as Cell…** and **File ▸ Import ▸ Model or Subcircuit…**, which is one gesture
because a supplier's file routinely holds both (the subcircuit that is the part, and the cards that
are its transistors) and a user opening it has "the file for this part", not a classification.
`SpiceCellImport` reads the file once and lists everything importable in one picker.

**Which extensions the TREE offers it on is a separate, narrower question from the file picker's**,
because the tree's item appears on a bookmarked file with nothing having read it — so the extension
is the whole of what decides, and a dead menu item on most of a workspace is the cost of casting too
wide. The line is "does this extension name a SPICE deck and nothing else?": `.model`, `.mod`,
`.subckt`, `.sub`, `.ckt`, `.sp`, `.spi`, `.cir` pass — **`.sp` was missed on the first pass and is at
least as common a spelling for a file holding a `.subckt` as `.subckt` itself** (owner, 2026-09-01).
`.lib` and `.txt` do not and stay picker-only: `.lib` is a static library everywhere outside this
dialect, and `.txt` is anything at all.

A subcircuit becomes a cell whose schematic holds the definition's own components, wired to each
other as the file wires them, one `Pin` per declared port, and `AutoSymbolGenerator`'s generic N-port
box for a symbol — the same box an SnP gets, reused rather than reinvented, and for the same reason:
circuitRF does not know what the user's subcircuit IS, so any glyph more specific asserts something
untrue.

### Geometry IS connectivity, so the router is not a cosmetic component

This is the whole difficulty and it is not obvious from the outside. circuitRF reads a schematic's
nets off the drawing (`SchematicEditModel.ComputeConnectivityGeometry`), so a wire laid across
another net's wire in the wrong way **does not look wrong — it JOINS the two nets**, and the imported
cell then simulates as a different circuit with nothing reporting it. `SchematicAutoRouter` is that
contract restated as three routing rules:

- **A pure crossing is safe.** Two wires passing through a point with neither having a vertex there
  is not a connection; it joins only through a user-placed dot. This is what makes routing possible
  at all — a route may cross another net at right angles.
- **A vertex on another net's wire IS a connection.** Three or more incident segments with a vertex
  among them auto-dots, so a route may never BEND or END on a cell any other net's wire touches.
  Note this is *not* the same test as the crossing one and a crossing-only check misses it: the
  dangerous case is our corner landing on their straight run.
- **Collinear overlap is not a connection but is a lie** — two nets' wires along one line read as one
  wire. Forbidden for the same reason, one step weaker.

**The A\* state is (cell, arrival direction), not (cell).** Whether a cell may be used depends on how
the wire passes through it — straight through is legal where a corner is not — so a plain per-cell
search either forbids legal crossings or permits illegal corners. That doubled state space is the
reason this is A\* rather than a flood fill.

**A route that cannot be found falls back to a net label, and the fallback is reported.** A net label
is a real connection (same-name labels are one net), so the cell is still the circuit the file wrote;
only the drawing suffered. Leaving a terminal open instead would produce a different circuit in
silence. `ADenseDefinition_…` asserts `Empty(model.NetLabels)` for exactly this reason — the fallback
would otherwise make every round-trip test pass on a schematic with no wires in it.

### Placement, and the one keep-out subtlety

Components go on a 1000-unit grid in breadth-first net order seeded from the declared ports (nothing
is optimised; connected things end up near each other, which is most of what makes an auto-drawn
netlist readable and what keeps the router's paths findable). **Ground is not walked through** — it
touches nearly every element, so following it makes one hop out of any two components in the circuit.

Each component's keep-out is its footprint one grid square proud of everything it draws, **minus each
port's own cell AND the cell immediately outside it**. Both halves are load-bearing: without the
exemption the pin is walled in and unreachable; without the footprint a wire creeps along the device
body and arrives at the pin sideways, drawn across the glyph.

### Ground is drawn, not routed

Net `0` is every SPICE netlist's busiest net and routing it lays a rail across the sheet. Each
terminal on it gets its own `Ground` on a 200-unit lead instead. **Each ground is given a PRIVATE net
name** (`0#3`, not a name any SPICE net can have) — handing them all `"0"` would ask the router to
join every ground to every other one, which is the rail this avoids. Extraction names them all `0`
regardless, because a `Ground` component NAMES its net. Skipped entirely when the definition declares
`0` as a port, since the `Pin` then has to be able to reach it.

### Refusals: one bad element refuses the whole definition

A netlist with a line missing is not a smaller circuit, it is a DIFFERENT one — and a different one
that elaborates, simulates and produces numbers. So a definition is refused whole when the reader
marked it incomplete, when any element is refused, when a nested call is refused, or when the calls
form a cycle (**refused rather than cut**: cutting one builds a hierarchy that terminates and is not
the file's). Named refusals: a model the file does not define, a card circuitRF has no model for
(carrying the CARD's own reason, which is what says whether the fix is a different file or a
feature), a net count that disagrees with the component's terminal count, a definition with no ports,
and `SemiC` — whose engine model exists but which has no schematic component to draw.

**A terminal count is never reconciled.** A four-net bipolar line (the fourth is the substrate)
against a three-terminal component is refused; tying the substrate somewhere plausible is a different
circuit that solves.

### Nesting creates more cells, and says so

A circuitRF cell instance references a cell FOLDER, so a nested `.subckt` has nowhere else to live: a
call becomes a `CellRef` instance and the called definition becomes its own cell folder beside the
parent, named after itself. **All-or-nothing across every folder**, not just the one asked for — a
half-written nested import leaves a parent pointing at a child that is not there, which the workspace
scanner lists and a user places. Every extra cell is named in the report.

### Two reader gaps this exposed, both silent

- **`M` required four nets.** A lateral MOSFET states drain/gate/source/bulk; a VERTICAL one states
  three, because the source-to-body short is inside the silicon — and both are written with an `M`.
  So every VDMOS line was refused outright with a message about a missing net the file was right not
  to have. The minimum is 3 now; the existing name-is-the-last-bare-word rule already separates the
  nets from the model name at either width, so nothing else changed.
- **There was no `J`.** circuitRF has had a JFET model since the model-card work, but with no element
  letter for one, a netlist instantiating a JFET was skipped as an unreadable kind — which marked the
  cell incomplete and took the whole subcircuit down with it.

### The oracle is extraction, not the drawing

`SubcircuitImportTests` asserts what has to be true rather than what the router happens to draw: the
built schematic read BACK through `NetExtractor` — the same path a run takes — is the netlist the
file wrote, compared as a PARTITION (the map from the file's net names to the extracted ones must be
a bijection, which is exactly "the same terminals are shorted together, and no others"). Ground is
pinned by name rather than merely mapped, since `0` is the one net whose name is a fact. Two of the
tests run the whole thing through the files on disk, one of them nested, because every other
round-trip test works on the in-memory model.

### The case-insensitivity trap, one layer up from the reader's

An element line writing `AREA=4` against a registry row declared `Area` is the same parameter to
every simulator that reads the format, and a name circuitRF has never heard of — it compares
ordinally. `ModelCardCellBuilder.ImportedParameter` now takes the ROW's spelling when only the case
differs, which is the only direction that cannot invent a parameter. **The device multiplier is
exempt and that is not a detail**: this dialect's `m=4` means four devices in parallel while
upper-case `M` on the same diode is the junction grading coefficient, so re-spelling it would give
that diode a grading coefficient of 4, no multiplier at all, and an ordinary-looking simulation.
`SpiceNetlistReader.NormaliseInstanceParameter` guards the same pair one layer down.

## Importing a SPICE `.model` card as a cell (2026-09-01)

A `.MODEL` file dropped into the Project Tree gets **Copy to Workspace as Cell…** on its right-click
menu, and **File ▸ Import ▸ Model Card…** does the same without bookmarking anything first. One card
becomes a cell: a `.csch` holding the native circuitRF component with the card's parameters and its
pins already wired, a `.csym` copied from that component's own artwork, and a `.ccell` naming both.

**Almost none of this was new machinery, and finding that out was most of the work.**
`SpiceNetlistReader` already parsed `.model` cards into `SpiceModelCard` (it handles the
bracket-glued-to-type case, continuations and `.include`), `MatchFlatten.Write` already had the
all-or-nothing cell-writing shape, and `MatchSymbolCopy` already had the symbol-deep-copy idiom. The
two genuinely new pieces are `SpiceModelCardTranslation` (card type → engine reference + circuitRF
parameter spellings, in `src/Core`, no UI in it) and `ModelCardCellBuilder` (that binding → a cell).

### The unit trap, which is the one that would have shipped silently

**A schematic parameter row carries a value AND a unit, and the registry's defaults use convenient
ones** — the diode's `Cj0` is declared in **picofarads**, the inductor's `L` in nanohenries. A
`.model` card states everything **unscaled**. Writing a card's `CJO=2e-12` into a row that still says
`pF` is a capacitance a **trillion times too small**, and it simulates perfectly. Every imported row
therefore gets the BASE unit for its dimension (`ModelCardCellBuilder.BaseUnit`), taken from the
registry's own declaration of what that dimension is.

The test for it asserts the **evaluated SI value**, not the expression string: a string comparison
passes just as happily with the unit left at `pF`, which is the entire failure being guarded.

*Adjacent gotcha found while writing that test:* `Evaluator.Eval`'s unit table is keyed `"Ohm"`/`"u"`
and the editor's tokens are `"Ω"`/`"µ"`. **`UnitNormalizer.ToEngineUnit` is the boundary** — calling
`Eval` with a registry unit token straight off a parameter row throws `Unknown unit 'Ω'`.

### The diode's registry rows and its factory had drifted

`ComponentModelFactory.CreateDiodeModel` has always read **`Xti`, `Eg`, `Tnom`, `Area`, `Isr`, `Nr`
and `Nbv`**, and `ComponentTypeRegistry` declared **none of them** — so all seven were live,
invisible in the parameter dialog, unsweepable, and had nowhere for a model card's `XTI` to land.
Owner noticed the `XTI` gap; the other six came with it. All seven now have rows, at the factory's
own fallback values (note `Eg` is **1.16**, `Temperature.SiliconBandgapEv`, not the BJT row's 1.11 —
stating a different number would silently change every diode already in a design).

**`DevicePaletteWiringTests.P4` is what holds the two lists together**, and adding rows to it needs
one non-obvious thing: its activation dictionary must set **`Temp` far from `Tnom`**. At `Temp ==
Tnom` every temperature relation collapses to the identity, so `Xti`, `Eg` and `Tnom` all read as
*unwired parameters* while doing exactly what they say. `Isr` must be non-zero or `Nr` is inert with
it, and `Nbv` is only live below −`Bv`, which is why the diode probe's bias grid runs to −6 V.

### Refusals, and why nothing is approximated

The nearest-native temptation is real: a JFET's square law looks like the Curtice quadratic with the
`tanh` ignored, a ferrite bead looks like a parallel RLC, a p-channel part looks like an n-channel
one with signs flipped. **Every one of those produces a cell that simulates and is quantitatively
wrong with nothing anywhere reporting it.** So `NJF`/`PJF`, `PMF`, `NMOS`/`PMOS`, `VDMOS`,
`NIGBT`/`PIGBT` and `BEAD` are refused **by name**, each saying what circuitRF does not have. The
picker lists refused cards **with their reasons** rather than hiding them — "why is my VDMOS not
offered?" is otherwise answered by an absence.

A `RES`/`CAP`/`IND` card stating only a sheet resistance or an area coefficient is refused for the
same reason `SpicePassiveModelBinding` already refuses it on an instance: there is no geometry here
to apply it to, and the alternative is a value of zero that simulates.

**Nothing is dropped quietly either.** Card parameters with no circuitRF home (`CJS`, `KF`, `AF`,
`PTF`, `LEVEL`, …) are reported in the Messages panel *and* written onto the cell's own annotation.

### Two smaller decisions

- **Which MESFET law an `NMF` card states is decided from its PARAMETERS, never its `LEVEL`.** The
  level numbering is not portable — the same integer selects a different law in different dialects —
  so honouring it would make the choice depend on which simulator the file was written for, a fact
  the file does not record. `B` (the doping-profile tail) appears in the Statz law and no other, so
  its presence is the file's own unambiguous statement. A stated `LEVEL` is listed as unmapped so
  nobody concludes it was honoured.
- **A MESFET card's `RD`/`RS` become real series resistors in the schematic.** circuitRF's FET family
  has no lead-resistance parameter, and a cell IS a schematic — so they go where they physically are
  rather than into the unmapped list. `C2`/`C4` are conversely NOT aliased onto `Ise`/`Isc`: they are
  multipliers of `IS` in old SPICE, and reading one as the other is off by fourteen orders of
  magnitude on a card that looks entirely ordinary.

### What is now imported, and what is still refused (updated 2026-09-01)

The engine models landed (see `src/Core/RESOLVED.md`), so most of the refusals above are gone. **Each
type left the refusal list only when there was a real model behind it** — the test that holds them is
a theory whose list shrinks, never because a refusal was inconvenient.

| Card type | Now |
|---|---|
| `NJF` / `PJF` | `JFET_N` / `JFET_P` — the Shichman-Hodges square law. |
| `PMF` | `PFET_Curtice` / `PFET_Statz` — both laws a MESFET card can be read as have a p-channel form. |
| `NMOS` / `PMOS` | `MOS1_*` or `MOS3_*`, chosen from the card's `LEVEL`. |
| `VDMOS` | `VDMOS_N` / `VDMOS_P`, chosen from the card's bare `pchan` keyword. |
| `BEAD` | `Bead`, unless the card describes no ferrite at all. |
| `NIGBT` / `PIGBT` | **Still refused** — for a new reason. |

**Four decisions on the import side worth keeping:**

- **A MOS card's `LEVEL` IS read; a MESFET card's is not.** That is not an inconsistency. A MESFET
  card's level selects between laws that different dialects number differently, so honouring it would
  make the choice depend on which simulator the file was written for. A MOS card's numbering for the
  *classical* levels is the one thing about it that is portable: 1, 2 and 3 mean the same three
  published models wherever they appear.
- **Level 2 is read as level 1 with a note; level 4 and above is REFUSED.** Every parameter the
  classical levels share means the same thing in all of them, so reading a level-2 card as level 1
  gives a device that is right at low field and optimistic at high — a far better answer than none,
  provided the user is told. Level 4 and above are the compact-model families, whose parameters name
  different quantities under different spellings; almost nothing would be carried and what came out
  would be circuitRF's transistor wearing default numbers under the card's name.
- **A short-channel parameter must not be carried onto a level-1 device.** It has no home there, and
  landing it on the transistor would look exactly like it had been honoured — the row would be
  present, with the card's value in it, read by nothing. They are moved into the unmapped list and
  reported by name.
- **A MOS or JFET card's `RD`/`RS` must NOT become placed series resistors**, even though they are
  spelled exactly as a MESFET card spells its own lead resistances — which DO become resistors,
  because circuitRF's MESFET has no parameter for them. The MOS and JFET families carry them as model
  parameters on internal nodes the elaborator mints. Placing them a second time would put the
  resistance in the device AND beside it, and the schematic would look entirely ordinary.

**`NIGBT`/`PIGBT` is the interesting refusal: circuitRF has an IGBT and the card is still refused.**
Its parameters belong to the published ambipolar transport model and describe the silicon (base
width, doping, carrier lifetime); circuitRF's is an equivalent-circuit model parameterised by what a
data sheet gives (threshold, transconductance, current gain, transit time). Neither set can be
derived from the other by renaming — that is a device-modelling extraction — so the refusal now says
*that* rather than "no model exists", and points at the VerilogA component.

**One reader bug found on the way, and it was silent.** `.model` cards were parsed with the bare
words discarded, so a `VDMOS` card's lone `pchan` keyword vanished and an n-channel and a p-channel
card read identically with every number right. `SpiceModelCard` now carries `Flags`.

**The unit trap keeps claiming new victims and the test shape is what catches it.** Every new family
added rows in a convenience unit — `W`/`L` in µm, `Tox` in nm, `Cgdmax` in pF, `L` (the bead's) in µH
— and every one of them is a card value written unscaled. The assertions are all on the **evaluated
SI value**, never the expression string, for the reason the original entry gives.

## SRLC and PRLC: the pin contract is the whole design constraint (2026-08-31)

Two new lumped tiles, `SymbolKind.Srlc` and `SymbolKind.Prlc`, over the engine components `SRLC` and
`PRLC`. Owner-approved glyphs.

**The brief's real requirement was not "draw a smaller R, L and C" — it was "put the pins where R, L
and C already put theirs".** Small was the means: three borrowed glyphs have to fit inside one
400-unit span so the leads can still reach (0,∓200). Everything about the geometry follows from
that — the resistor drops to 4 zigs, the inductor to 3 coils at r=20, the capacitor's plates to 60
wide — and none of it is a free aesthetic choice. Both kinds fall through `SymbolPortDefs.For`'s
DEFAULT arm, which already returns exactly R/L/C's two pins, so there is no second copy of the
coordinates to drift.

**That contract needed a test, because breaking it is invisible.** The glyph lives in
`BuiltInSymbols.cs` and the pin table in `EditableSchematic.cs`; a redraw that nudged a pin would
leave a part that still places, still saves, still simulates — while every schematic it had been
dropped into came apart at the wires. `RlcPaletteWiringTests.R2` asserts the pins against R, L and
C's OWN values, read live, rather than against copied literals that would move together with the
mistake — and it first checks that R, L and C still agree with each other, since otherwise it is
measuring the wrong thing. `R2b` adds the complementary claim from the primitive geometry: nothing
drawn crosses y = ±200, and the leads reach both pins exactly.

**PRLC's ±110 symmetry is deliberate.** The three branches sit at x = −80 / 0 / +80; the resistor
keeps its full ±30 zig amplitude (there is room sideways) and the capacitor's plates are 60 wide, so
the extreme left and right land at exactly ∓110. An asymmetric glyph would sit visibly off-centre
against its own wire.

**One docs-generation trap, unrelated to this work but found by it.**
`docs/user/assets/figures/analysis-editor-hb-dark.svg` is NOT deterministic: it contains a `use`
whose transform is a rotation matrix that changes on every DocGen run (measured 7.46° then 10.75°,
same tree, same command). Anyone running `tools/DocGen/check-docs-current.sh` will see that one file
dirty no matter what they changed. It is a capture-time artefact, not a drift — leave it out of an
otherwise-clean change set rather than committing a random phase.

---

## A file inside the workspace was listed twice: in place, and again under Known Files (2026-08-30)

Owner report. A Known File is a **bookmark to a file the tree cannot otherwise show**; once the file
lives inside the workspace the ordinary scan already renders it where it sits, so the Known Files
group was showing a second copy the user has to learn to ignore. `Import Data` and a file drop onto
the tree both land here — `AddKnownFile` records any picked path, and R-stb-10/11 explicitly allows
an in-workspace reference (stored workspace-relative).

**Fixed as a rendering filter, not a list filter** (`WorkspaceScanner.Scan`). A `.cws` entry whose
resolved path is already an `AbsolutePath` somewhere in the tree just built is skipped, and the group
node is omitted entirely when nothing survives.

- **The test is "already in the tree", NOT "inside the workspace root".** Those are different
  questions and the difference is load-bearing: `IsHiddenTreeFile` deliberately hides `.DS_Store` and
  `*.source` from the ordinary scan, so naming one as a Known File is the only way to see it, and
  that opt-in has its own test. A "was it inside the root" test would have broken it. Same for a
  broken in-workspace reference — nothing on disk to render, so the warning node is still the only
  way the user learns the reference is dead.
- **The `.cws` list itself is untouched, deliberately.** `GetKnownTouchstoneFiles` /
  `GetKnownLoadpullFiles` feed the Data Display's data-source library from `KnownFiles`, **not from
  the tree** — `DataSourceLibraryViewModel` otherwise only enumerates `results/*.npy`. Dropping an
  in-workspace `.sNp`/`.spl` from the list at write time (the tempting "don't record it at all" fix)
  would silently remove an imported measurement from every trace picker.
- Comparison is on the **resolved, fully-qualified, trailing-separator-trimmed** path
  (`WorkspaceScanner.PathKey`), because the stored form is relative for an in-workspace reference and
  absolute for an outside one, and `ResolveRef` returns a rooted ref unnormalized.

**Second, latent bug found on the way: `RemoveKnownFile` could never remove a relative entry.** It
compared `node.AbsolutePath` against the raw stored string, so for the workspace-relative form the
`RemoveAll` matched nothing, the `.cws` was rewritten unchanged, and the user still got
"Reference removed (file not deleted)". It now matches the resolved path as well. This is reachable
after the fix above — a hidden file opted in by name is stored relative and is still shown.


## Drag-follow redrew the whole wire, and mid-span taps left the net (2026-08-30)

Owner testing turned up seven drag defects on three real sheets. **Two were disconnects** — the
serious kind, because the schematic still looks wired and simulates as something else — and five were
shape damage. **All seven are one line of code:** both component-drag follow paths, the live tick
(`UpdateConnectedWireEndpointsLive`) and the commit (`CommitDragAsCommand`), threw the followed wire's
whole polyline away and redrew it as `WireGeometry.OrthogonalRoute`'s bare L between the two
endpoints.

**Why that loses connections, not just tidiness.** A wire's mid-span T-taps are geometric: a pin on a
segment interior IS on that net (`ComputeConnectivityGeometry`). The bare L is a *different wire* — it
does not pass where the original's interior was — so every tap on it is dropped, silently. Two
capacitors tapping the middle of a horizontal wire between an inductor and another capacitor left the
net when the inductor was nudged **one grid step**, and the resulting netlist is a different circuit
that still runs.

The five "annoying" reports are the same L seen from the other side: a vertical run comes back
horizontal, a horizontal run moves off its row, and in one case the new leg landed exactly on top of
an unrelated vertical wire — where a reader cannot tell one net from two — and ran through a
transistor's symbol on the way.

**The rule now: a moved endpoint deforms its own wire as little as the geometry allows**
(`WireGeometry.FollowEndpoints`). An orthogonal polyline alternates H and V legs, so the delta at a
moved end splits into a part ALONG that end's leg — absorbed by lengthening it, changing nothing else
— and a part ACROSS it, handed to the **one** neighbouring vertex, where the next leg (perpendicular
by construction) absorbs it as its own length. **Propagation stops there**: nothing past the second
vertex ever moves, so bends, rows and columns survive, and so does every tap not on the two legs that
changed. When the neighbour is the far ENDPOINT it is held by whatever is at the other end and can
absorb nothing — a plain two-point wire is exactly this case — so an elbow is inserted AT THE MOVED
END, leaving the original leg (and its taps) untouched. That elbow is the vertical jog a user expects
to see appear under a part they nudged off its row, and it is what fixes both disconnects.

**A tap that leaves its wire now grows a stub; the wire is not bent to chase it.** The old
`RouteBodyFollow` re-routed the tapped wire through the moved pin — but that branch is only reached
when NEITHER of the wire's endpoints moved, i.e. when both ends are anchored by something staying put,
so it was dragging a run the user placed (and everything else tapping it) on behalf of a part with no
claim on either end. `BuildTapStubs` creates a `PlaceWireCommand` stub instead, chained into the same
undoable composite as the move, exactly as the segment-drag path already did for the same situation
(`BuildInteriorPortStubs`). **The stub leaves the wire at a right angle**, from the foot of the
perpendicular dropped onto the nearest segment, so it never runs ALONG the wire it joins.

**A gap closed while there:** the stub is built from the wire's POST-follow geometry, so a tap also
survives when the tapped wire is itself following a moved endpoint. Dragging the inductor and one of
the tapping capacitors *together* used to lose the other capacitor, and the old code could not have
caught it — it `continue`d past the body-follow branch whenever an endpoint had moved.

**Not attempted, and it should not be assumed:** general obstacle-aware routing. Wire-over-wire and
wire-over-symbol are listed as out of scope in
`docs/design/placement-connectivity-and-drag-follow.md`, and both reported instances of them were
*produced by* the whole-wire redraw, so preserving shape removes them at the cause rather than by
avoidance. A drag that genuinely needs a detour still will not get one.

Gated by `tests/Ui.Tests/Schematic/DragRoutePreservesShapeTests.cs` (19 cases): net-level extraction
oracles for both disconnects, the exact expected polyline for each of the five shape reports (so a
future tidy-up cannot quietly go back to the L), the stub's geometry and its undo, the group-drag
case above, and a wire-over-wire overlap check on the sheet where the old route produced one.

## An added parameter group rendered no label, whatever "show on schematic" said (2026-08-29)

Owner report: adding a second tone to a VTone (and to the new ITone) with **View on schematic** ticked
put no label on the instance.

**The checkbox was being honoured; the value was missing.** `EditableSchematic.BuildRenderModel` skips
any label parameter whose `Expression` is empty — right for a label, since "Freq[2] = " is noise — and
`ParameterEditorViewModel.AddGroup` created every member of a new group with `Expression = ""`. So the
one moment a user ticks that box is the one moment it appears to do nothing.

**Fixed at the cause, not at the render rule.** `IndexedParamGroup` now carries `DefaultExpressions`,
and every group whose members are `ShowOnSchematic` states real ones — the tone families (VTone, ITone,
PnTone) and the impedance ones (P1Tone's `Z[k]`, ZPort's `Z[n]`, both 50 Ω). SDD equation slots and VAR
rows deliberately keep no default: blank genuinely is the right start there, and an invented value is a
guess the user then has to notice and undo. `EveryShownMemberOfAnAddedGroup_HasANonBlankDefault` is the
gate, and it is expressed as the RULE (shown ⟹ non-blank) rather than as a list, so a future group
cannot be added blank without failing it.

Second-order: a blank shown parameter also made `SchematicViewModel`'s `LabelCount` (`2 +
LabelParameters().Count()`, unfiltered) disagree with the renderer's own filtered list, so per-label
drag offsets would index the wrong row. With no group ever added blank, that condition no longer
arises from this path; the underlying mismatch is untouched and is worth its own look if a blank shown
parameter can be reached another way.

## ITone and the VCCS: the arrow is the whole of the direction cue (2026-08-29)

`SymbolKind.CurrentToneSource` ("ITone", `I_1Tone`) and `SymbolKind.Vccs` ("VCCS").

**Both reuse the BJT's arrowhead** (owner request) — a filled three-point `Poly` lying ON the lead, at
the BJT's own size — because that glyph is already the thing a reader looks for when they want to know
which way something flows.

**They point OPPOSITE ways, and that is correct.** ITone's points UP, at pin 1, where an independent
source delivers its current (`src/Engine/CLAUDE.md` → "Current-source direction"). The VCCS's points
DOWN, at `out−`: a controlled transconductance SINKS its current from `out+`, which is how a
small-signal gm source is drawn in every device model. `Vccs_Glyph_…` asserts both arrows in one test,
against each other, so a later "make these consistent" pass cannot flip one and leave the schematic
lying about a direction.

**ITone is VTone's body with the polarity marks swapped for the arrow.** Same circle, same sine, same
two pins in the same places — so the two read as one family — and deliberately NOT the textbook
circle-with-an-arrow-inside: the body is 120 across and already carries the sine, so an arrowhead
inside it would either collide with the sine or shrink to nothing at palette size. On the lead it is
legible at every zoom and cannot be mistaken for the AC mark.

**The VCCS's control leads stop short of the diamond, and the gap IS the drawing.** They end at
x = −170; the diamond's left vertex is at x = −90. A lead touching the body would draw a connection the
device does not have — the control pair senses voltage and carries no current at all. A glyph test
asserts the gap rather than trusting the coordinates to stay put.

**Pin ORDER is the engine contract, in the 2N ± pair form** `VccsModel` reads:
`[out+, out−, ctrl+, ctrl−]`. Swapping either pair reverses the source's sign and still solves, so the
order is asserted by test, not left to the geometry.

## The bipolar transistor: two kinds, one law — and what that costs elsewhere (2026-08-29)

`SymbolKind.BjtNpn` / `BjtPnp`, engine references `BJT_NPN` / `BJT_PNP`. Both place the SAME model
(`BjtModel`) with the SAME parameter list; only a sign differs. That is the inverse of the FET family
sitting beside them in the palette, where five kinds denote five different drain-current laws, and the
inversion is worth stating because it makes two rules in this directory read the wrong way round.

**Why not one kind with a polarity parameter.** Because the two DRAW differently, and the emitter
arrow is the entire cue a reader has. A parameter would leave the drawing and the netlist free to
disagree — an n-p-n on screen, a p-n-p in the run — with nothing reporting it. `EngineReference` puts
the polarity in the NETLIST for the same reason. This is also why they are the one place in
`BuiltInSymbols` where two kinds of one family do NOT share a glyph, and `DevicePaletteWiringTests`
now asserts that they don't, so a later "same topology, share the glyph" tidy-up fails loudly.

**Both polarity names are search terms on BOTH tiles.** Somebody typing "PNP" is looking for the pair,
not for one of them.

**The two are not interchangeable at a bias point, which broke the palette-wiring test's own probe.**
`DevicePaletteWiringTests.P4` perturbs every registry parameter and requires the device's behaviour to
move. Applied to a p-n-p with the n-p-n's bias grid it reports `Tf` — and anything else that only
lives in forward conduction — as an unwired parameter, because at those voltages the p-n-p is
reverse-active. The grid is therefore mirrored by polarity (`BjtBiases`), which is what
`BjtModel.IsNpn` exists for. The same trap is waiting for any future probe over this family.

**The saturation row of that grid is load-bearing.** `Br`, `Nr`, `Ikr`, `Isc` and `Nc` do essentially
nothing with the collector junction reverse-biased — their contribution is 1e-20-ish and lands under
the test's own change threshold. Without a bias with BOTH junctions forward, five real parameters
read as unwired. (And the shipped `Vtf` default cannot be probed at any bias at all — see
`src/Core/RESOLVED.md`'s own note for why, and why the test's activation value differs from it.)


## Four PDK defects: two kits' models crossing, an empty part-to-cell map, and a symbol that vanished when zoomed out (2026-08-19)

Four owner reports, three unrelated root causes and one shared lesson: **each failure looked like the
feature simply not working, because in every case the wrong answer was indistinguishable from a
legitimate one.**

### 1. An imported kit's parts all reported "no layout artwork", and only reopening the workspace fixed it

Owner report: import an open-process kit, place one of its components, and updating the layout from
the schematic reports every placed part as having no artwork — including parts whose layout cells the
kit plainly ships. Devices that used to render stopped rendering.

**Root cause: the part-to-cell map is filled by a background reading that exactly one path started.**
Which of a kit's parametric cells is a given schematic part's layout view is settled once, by the
palette (`KitPaletteMerge`), and published for everything else to read (`KitLayoutGenerators`). That
reading has to START a kit's Python interpreter, so `WorkspaceViewModel.RefreshPCellPaletteItems`
runs it off the UI thread — and it was reachable from one place only, the workspace PATH changing.

A kit imported into an already-open workspace declares its parametric-cell library **during that
import** (`DeclareKitPCellLibrary`), which sets `_pcellDeclarationsAdded` and calls
`ReloadPCellGenerators` — and that method rescans, invalidates and regenerates, but never re-read the
palette. So the map stayed empty for the whole session. The same hole swallowed the consent path:
a kit refused permission cannot be listed, so the reading taken while it was `Unknown` found nothing,
and granting permission afterwards never took it again.

**Verified rather than inferred.** Driving `PdkImporter` → `PdkPartInstaller` → `PCellWorkerResolver`
→ `KitPaletteMerge` against a kit pairs all 34 of its cells correctly — including the several
whose schematic part and layout cell are named nothing alike, which the model rule settles — and
generating one produces real geometry. So the matching rules were never the problem, and neither was
the artwork. Only the publishing was.

**Fix, in two parts, because one of them is only a narrowing of the window:**

- Every path that can change what the resolver would answer now refreshes: `ReloadPCellGenerators`
  and the consent-granted branch of `RequestPCellConsent`, alongside the workspace open. The reading
  itself is split into `CollectPCellGeneratorInfo` (no UI work) and `ApplyPCellGeneratorInfo`, and
  carries a generation counter so a slow earlier pass cannot land on top of a later one.
- `KitLayoutGenerators.SetRefresher` — a lookup that MISSES may ask, once, for the reading to be
  taken now. This is what makes the answer independent of timing rather than merely likelier to be
  right: a part placed in the seconds between a kit being declared and its interpreter answering
  would otherwise still get the wrong answer. It costs nothing once the map is populated (the hook
  returns immediately), it is asked at most once per lookup, it cannot re-enter itself, and a hook
  that throws is treated as a miss. Starting an interpreter there is no more than the
  `PCellRegistry.TryGet` on the very next line already does.

**The message for a part that genuinely has no layout cell is now one short clause.** It used to
carry three, telling the user to go and drop the cell from the palette themselves — written for a
period when the pairing was routinely failing outright. Once the pairing works, what reaches this
line is a model-only part (a parasitic capacitance, a technology include) with no artwork to place,
and a paragraph of recovery advice per placed part is noise.

### 2. Importing one kit picked up a NEIGHBOURING kit's compiled models — and broke its simulation

Owner report: importing a kit found "a whole bunch of `.osdi` models"; separately, a kit that used to
simulate now fails elaboration with the provider exposing a device type that belongs to a completely
different kit.

**These are one bug.** `PdkPartInstaller.FindCompiledModels` searched the kit root **and two ancestor
levels**. Unpacked kits routinely live side by side under one folder, so importing a kit whose
devices come from a compiled model LIBRARY found the compiled-Verilog-A artefacts of an unrelated
kit two levels up — seven of them, in the reported case — took the compiled-Verilog-A branch of
`SynthesiseProviderSettings` on the strength of them, and wrote settings naming the other kit's
worker and artefact. Everything imported cleanly. The failure surfaced only at Run.

**Reproduced exactly** by pointing `DeviceWorkerManifest.ToolsDirectory` at a build that ships the
OSDI worker (a test host does not, which is why no existing test could see this) and importing the
kit: the derived settings named the neighbour's artefact, and the same import with the ancestor walk
removed derives the correct compiled-library settings instead.

**Why the rule differs from the library search's, which DOES walk up.** A model library is recognised
by the entry points circuitRF's own worker will call, so finding one beside a kit is evidence about
that kit. An `.osdi` file carries nothing of the sort — it is one compiled module, and a folder of
kits therefore answers every one of them with the first kit's models. An ancestor is a coincidence;
the kit's own tree, and the folders the workspace was TOLD hold model libraries, are statements.

**Fix:** the search starts at the kit root (still recursive, so a kit's own artefact is found however
deep it sits) and adds the declared library roots. Nothing else.

**And the search fix alone would not have repaired anyone's workspace.** Derived settings are
RECORDED in `.cws` and win outright on every open — that is the whole point of recording them. So
`GeneratedFormat` is bumped 4 → 5, which is the mechanism `KeepIfStillCurrent` already carries for
exactly this: circuitRF's own earlier working-out is redone, while a kit's own settings and a user's
edits are untouched.

### 3. A kit's schematic symbol disappeared when zoomed out

Owner report: one PDK symbol does not render when zoomed out; at normal zoom it is fine.

**Root cause: the level-of-detail stand-in was sized from a nominal built-in symbol.**
`SchematicRenderer` decides to substitute a filled rectangle when `zoom * 300 < 6` — 300 world units
being a built-in's nominal width — and then drew that rectangle at `300 x 100` world units scaled.
Both numbers are an order of magnitude wrong for an imported kit's symbol. The reported part measures
**3,275 x 3,375 world units** (measured, not estimated: its terminals and artwork through
`KitTemplateSymbol`), so at the zoom where the substitution switches on it is still ~65 px across —
and was being replaced by a **4 x 1.4 px** speck. Nothing errored; the part simply looked absent
while every built-in around it stayed legible.

**Fix:** both the decision and the rectangle come from the component's own `GlyphBb`. A symbol whose
artwork is genuinely too small to read is still stood in for (that is what the substitution is for,
and the built-in case is unchanged); one still large on screen is drawn. The rectangle is centred on
the GLYPH, not on the component origin — a kit symbol's artwork is often nowhere near its origin, so
the two are not interchangeable.

### Gates

- `tests/Ui.Tests/KitLayoutGeneratorRefreshTests.cs` — the fallback hook (asked once, not re-entered,
  a throwing hook is a miss, `Clear` keeps the hook) plus source-level wiring checks that the reload
  and consent paths refresh, since `WorkspaceViewModel` cannot be constructed in a test.
- `tests/Ui.Tests/PdkNeighbouringKitIsolationTests.cs` — 5 tests over two kits side by side. Verified
  to fail with the ancestor walk restored, and the format-bump test verified to fail at
  `GeneratedFormat = 4`.
- `tests/Ui.Tests/SchematicLodGlyphSizeTests.cs` — 6 tests, oracle is the painted-pixel extent off a
  real Skia render rather than the renderer's own arithmetic. Verified to fail against the previous
  renderer.
- `KitPartToLayoutTests.L1` updated to the new skip wording, and now also holds the line SHORT.
- `KitLayoutArtworkTests` joined `PdkToolsDirectoryCollection`: `KitLayoutGenerators` is process-wide
  and now carries an installable hook, so classes that publish into it must not run alongside.

End-to-end against the reported workspace: the previously-skipped part is added to the layout with
its correct artwork, and the only remaining skip line is the model-only part that genuinely has no
layout cell.

## Wire hitbox is too thin when zoomed out (2026-08-19)

Owner report: schematic wires are hard to click and drag when the view is zoomed out.

**Root cause: the pick band is a WORLD constant, and the wire's stroke is a PIXEL one.**
`SchematicHitTest` used a flat `WireHitTol = 8` world units (and `EndpointHitTol = 12`) regardless of
view scale, while `SchematicRenderer` draws a wire at `max(1 px, zoom * 4)`. The two therefore move
in opposite directions as the user zooms out: at zoom 0.1 the wire is still drawn 1 px wide, but its
clickable band has collapsed to 0.8 px either side of the centreline — **narrower than the stroke the
user is aiming at**. Nothing about the wire looks unclickable, which is why it reads as a hitbox bug
rather than a zoom bug.

**Fix:** `Test` and `TestStack` take an optional `zoom` (default 1.0, so every existing call and test
keeps its old meaning) and derive the band from `WireTolFor`/`EndpointTolFor`:
`max(world constant, min(pixel floor / zoom, 45 % of GridSize))`. The floors are 7 px for a segment
and 10 px for an endpoint. Both sit *below* the old world constants at 1:1 (8 and 12), so **the feel
at 1:1 and at any zoom-in is bit-identical** — the new term only ever binds on zoom-out. The four
call sites pass the live scale: `SchematicCanvas` its `_zoom`, `SchematicViewModel` its `CanvasZoom`
(already maintained for the canvas-object gripper, and already synced at every zoom mutation).

**Two traps the obvious version falls into:**

- **The spatial-index window must grow with the band.** A wire's bounding box is a zero-thickness
  line along its run, so querying the old `hitRadius`-sized window returns no candidate at all for a
  click 30 world units off a horizontal wire — the widened band would have been dead code. `half` is
  now `max(hitRadius, wireTol, endTol)`.
- **A grown endpoint zone must not swallow its own segment.** At zoom 0.05 a 10 px endpoint radius is
  200 world units; on a 200-unit wire the two endpoint zones cover the whole thing and the segment —
  the thing the owner wants to drag — becomes unreachable. `CapEndpointTol` caps the radius at 40 %
  of the adjacent segment, floored at the original `EndpointHitTol` so short wires at 1:1 are
  untouched.

**Why the band is capped at 45 % of the connection grid rather than growing without bound.** The
picker returns the *topmost* candidate, not the *nearest*. Two parallel wires one grid pitch apart
are 10 px apart on screen at zoom 0.1; an uncapped 7 px band would overlap its neighbour's and hand
back a wire the user was not pointing at. 45 % of a 100-unit grid leaves a 10-unit gap between
adjacent bands, so the answer stays unambiguous. The cost is that below zoom ~0.156 the band is
grid-limited rather than 7 px (4.5 px at zoom 0.1 — still 5.6x the old 0.8 px). Making the picker
nearest-wins instead would lift that cap, but it changes the Z-order semantics that click-through
cycling and the overlapping-wire tests depend on, so it was not done.

**Not changed, deliberately:** the drag *threshold* (`5.0` world units in `HandleSelectDrag`) has the
same world-vs-pixel confusion but fails in the harmless direction on zoom-out (a drag starts too
easily, not too late). The wire-drawing snap tolerances (`NearestWireEndpoint`,
`NearestPointOnWireSegment`, `NearestWireCrossing`, all 15) are left in world units on purpose:
they decide *electrical connectivity*, and connectivity must not depend on how far the user happened
to be zoomed out when they drew the wire.

Tests: `HitTestTests` — `WireSegment_ZoomedOut_IsPickableSevenScreenPixelsOff` (3 zooms, each also
asserting the same click misses under the old fixed band), `..._StaysPickableThroughTestStack` (the
path the select tool actually presses through), `WireBand_FarZoomOut_IsCappedByTheGrid_NotTheStroke`,
`WirePick_AtUnityZoom_MatchesLegacyBand`, and
`WireEndpoint_GrabZone_NeverSwallowsItsOwnSegment`.

## Library Palette: explicit "All" order, "All - Alphabetical", "Nonlinear" filter (2026-08-16)

Owner report: the "All" filter's order looked "random" — it was never random, it was
`LibraryCatalog.BuildAllItems()`'s category-rank-then-DisplayName sort (`CategorySortKey`), which
reads as arbitrary unless you know the category priority order. Three owner-requested changes:

- **`LibraryCatalog.AllItemsPinnedOrder()`** — the "All" filter now shows an explicit 22-row pin
  list first (`AllFilterPinnedOrder`, keyed by `(SymbolKind, PortCount)` because Snp/ZPort/Sdd share
  one Kind across several port-count entry points), then every remaining built-in in `AllItems`'s
  own order. `PaletteTool.ComputeRawItems` calls this instead of `LibraryCatalog.AllItems` for the
  `All` category; PDK parts are still appended after, unsorted, unchanged from before.
- **`LibraryCatalog.AllItemsAlphabetical()`** + `PaletteTool.WithPdkAlphabeticalByKit` — the new
  "All - Alphabetical" filter (`PaletteCategoryKind.AllAlphabetical`, listed directly under "All" in
  `BuildCategories`). Built-ins pure-alphabetical by DisplayName, then PDK parts grouped by kit (kit
  groups alphabetical, matching the kit list's own ordering elsewhere), alphabetical within each kit,
  never interleaved across kits.
- **`ComponentCategory.Nonlinear`** — a new Real-category filter. Deliberately an
  `ExtraCategories` membership on nine registry entries (NonlinearC, VerilogA, Diode, the 5 FETs, and
  the shared `Sdd` entry, which covers all of SDD/SDD1/SDD2/SDD3), never anyone's *primary* Category
  — so it changes nothing about where those items sort in `AllItems`/the pinned "All" order, it only
  adds one more filter that finds them.

**"VnTone" resolved to `ToneSource`.** The owner's pin list paired `PnTone` with a `VnTone` that
does not exist anywhere in the codebase — the actual single-tone voltage source is `SymbolKind.ToneSource`,
`DisplayName` "VTone" (no "n"; `EngineReference` is `V_1Tone`, which is likely where the "V1Tone"
naming came from). Confirmed with the owner directly — pinned row 14 is `ToneSource`.

## The charge pair collapses at import — and that is what completes M4 (2026-09-01)

`SpiceChargePairCollapse`. A behavioural voltage source driving a linear capacitor, with nothing else
on the node between them, is how this whole family of models writes a nonlinear CHARGE — and the pair
is algebraically one charge: `Q = K·(v_port − f)`, the capacitor's own value cancelling out of
whatever `f` divided by it.

**It is not an optimisation.** The uncollapsed pair is a branch equation, which DC and S-parameter
analysis solve exactly and harmonic balance refuses — HB's unknowns are the voltage phasors at the
nonlinear-facing nodes, and a branch current is neither one of them nor reducible into the linear
subnetwork. The collapsed device states a charge, which HB has carried since it was written. So this
is the only form in which the idiom runs in the analysis the physics is written for.

Three conditions, and the first is the whole correctness of it:

- **Nothing else on the interior node**, checked against the definition's own port list and every
  other element in it — never against the text. A port is wired by the CALL SITE and ground is shared
  by the whole design, so neither is ever an interior node however few elements name it.
- **The expression must not sense the pair it constrains.** That pair is exactly what the collapse
  dissolves; a source reading its own output is implicit in itself and has no collapsed form.
- The other element must be a plain linear capacitor.

A pattern that does not hold exactly is left as the general branch-row device, which still solves at
DC and in S-parameters. Held by `SpiceSourceImportTests.M1`–`M4` (the collapse, and the three things
that must PREVENT it) and by `Engine.Tests`' `SddBranchEquationTests.M4b1`, which asserts the two
paths agree to 10 decimal places in S₁₁ at three frequencies.

**Where the collapse lives is not arbitrary.** It runs after `.func` inlining and after the
time-taint refusal, so what it wraps is already the final expression text and a refused pair is never
rewritten. It touches `Elements`, which is what `SubcircuitCellBuilder` iterates — the interior node
disappears from the built cell because nothing names it any more.
