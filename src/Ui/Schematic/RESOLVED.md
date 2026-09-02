# Schematic — resolved issues (see also `CLAUDE.md`)

Per-topic notes that don't belong in the standing `CLAUDE.md` file. Newest first.

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
