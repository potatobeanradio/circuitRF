# `src/Design/Layout/Lvs/` — findings

---

## An LVS waiver key must NOT reuse `LvsFinding.Key` (brief-lvs-12-gui.md R-lvs12-4b)

`LvsFinding.Key` is `DrcEngine.KeyFor`'s form: id, objects, **and the marker's exact box**. Its own
doc comment said "brief 12 consumes it"; brief 12 says the opposite, and the difference is the whole
point of R-lvs-53.

- A **DRC** waiver names a *place*. Moving the shape stops it applying, correctly — the place no
  longer exists.
- An **LVS** waiver names a *relationship*: "R7's pin 2 is deliberately not connected". That
  survives moving R7 across the board, and it stops being true the moment the **schematic** changes.

Keying an LVS waiver on a box would be wrong in both directions at once: silently un-waived on every
re-route, and kept alive across the schematic edit that invalidated it. So `LvsWaiverKey.For` is a
second, independent key — `(id, the schematic-side identity, the terminal)` — read off the
diagnostic's **typed arguments**, because the id is the contract and the sentence is not (R-lvs8-2a).
`LvsFinding.Key` is kept and still used, for what it is actually good at: identifying which row's
marker is selected.

**It is an argument PREFERENCE ORDER, not a per-id table.** `LvsReport` already holds the table
saying which argument of which id names an object; a second copy here would be the same fault twice,
drifting silently because nothing compares them. The order is
`schematicPath, path, nets, net, designator`, then the finding's own `Objects`, then the id alone
for a run-level line — the designer's own name for the object, on the schematic side wherever there
is one, and never a coordinate.

## The waiver is applied in `LvsRun`, not in either surface

Both the CLI and the panel honour waivers because `LvsRun.Run` applies `layout.LvsWaivers` to the
findings before returning — the same argument that put the whole comparison behind one entry point.
A sub-cell's findings arrive already marked against **its own** `.clay`, which is right: a waiver is
a statement about the drawing it is stored on.

**The run still writes nothing** (R-lvs12-4g, and `check`'s R-aut4-6). It reads the list and hands
back marked findings; the list on the document is untouched, which is what keeps a comparison usable
on a read-only tree and on a workspace another process has open. There is deliberately no `--waive`:
a waiver is a deliberate, reasoned act performed beside the thing being waived.

---

## An empty wBond array is NOT "an ordinary mid-design state" (brief-lvs-13-assemblies.md R-lvs13-4d)

The brief asks for an array with no wires to read as two opens and to be no error, on the grounds
that it is a normal state of a design being drawn. **The extraction half of that is done and gated.
The premise behind it is false**, and it has been false since WB-B:

- `WBondDesign.Validate` **refuses** an empty array — it makes the mapping matrix rank-deficient and
  the array-basis inductance singular, and the refusal is deliberately there rather than in the
  linear algebra so the failure names its cause.
- The schematic's own array editor **cannot create one**: a new array arrives carrying a default
  wire, for exactly that reason (`ParameterEditorViewModel.WBond`'s own header says so).

So the state is reachable only by hand-editing a document, and a design in it does not elaborate.
LVS reports the elaborator's own sentence, unmodified, and the layout side still reads the array as
two opens and invents nothing. `AssemblyTests.AnArrayWithNoWiresIsTwoOpensAndNotAWBondFinding`
asserts BOTH halves — the reading and the refusal — so that nobody later reads the passing test as
evidence the state is supported.

## A cross-technology layer REMAP silently unmakes a sub-cell's boundary pads

Found building brief 13's fixture, and it is the reason `LvsRun` now supplies `resolveTechAt`.

`LayoutReadHierarchy.CopperFor` keeps a module's shape in the parent's partition when that shape
covers one of the placement's declared pins **on that pin's own layer** (`shape.Layer == layer`).
The pin layers come from `PlacedPins`, which projects them in the SUB-CELL's own numbering. The
shapes, by then, have been through `LayoutDesignFlatten`'s cross-technology reconciliation and may
carry the PARENT's numbering instead.

When the two differ — a die whose metal is layer 7 named `Top`, reconciled by name onto a board's
layer 1 — every boundary pad fails the test, leaves the partition with the rest of the module's
internals, and the die reads as a part whose every pin is on no copper. The symptom is
`lvs.pin.no-copper` on a correctly-abutted die, plus every bond wire landing on nothing, and it
appears only when two technologies are in play.

**Not fixed here, and the brief's own fixture is why it did not have to be:** R-lvs13-2a is about a
layer-number COINCIDENCE, so the assembly fixture puts the die's metal on the same key the board
uses (layer 1, named the same in both technologies), which reconciles to the identity and exercises
the case the brief is actually about. The remap case is a real gap in the hierarchical reading, it
is recorded here, and it wants either `PlacedPins` to reconcile alongside the shapes or `CopperFor`
to compare on the reconciled key.

## `LayoutRead.NetTable` is internal, deliberately

A bond wire's foot is located by the same point-in-piece lookup a pad is, so it has to arrive at the
SAME net table — `AssemblyRead.Emit` takes it rather than building one. A second table would give
one design two numbering schemes and two answers to "is this wire on the input net", and nothing
would compare them. The `out LvsGeometry` overload of `LayoutRead.Read` went internal with it, which
cost nothing: `LvsRun` was its only caller.

## Recognition reads a resistive film that is NOT a stackup conductor — and it must (brief 14)

The deck's first rule is a NiCr resistor: `Body` is the resistive-film layer, `Terminals` are
Metal1. Two things about that layer pull in opposite directions and both are load-bearing.

**The body must be VISIBLE to the region evaluator.** `LayerRegions.Build` drops a declared drawing
layer no `Conductor` or `Via` stackup entry claims, so a soldermask opening cannot join the copper
under it. A nitride window is dropped by the same clause, and `MIM Metal AND Nitride` — the deck's
own second example — would then evaluate to nothing and recognise nothing, **silently**. So
`DeviceCandidates` passes `electricalOnly: false`, which is the reading `DrcEngine` already performs
for its own regions: a DRC rule measures a mask clearance too. The drop is a question about
CONNECTIVITY and it still applies to the partition, which is where terminals get their nets.

**And the body must NOT be a stackup conductor**, or every resistor it recognises is shorted. A
recognised resistor's two terminals are two nets only because nothing joins the two Metal1 pads at
DC; declare the film a plain `Conductor` and the partition unions the body with both pads, the
device comes back with its terminals on one net, and the comparison reports an open circuit's worth
of findings about a correct design. That is the technology author's decision, not the recognition
pass's, and there is nothing here that can detect it — the geometry of a correct resistor and the
geometry of a shorted one are identical.

## The ambiguous-axis rule is judged per FORMULA, not per candidate

R-lvs14-3c says a body within a few percent of square "does not guess", because a resistor read the
wrong way round is off by (L/W)². Applied per CANDIDATE it would fire on the deck's own MIM
capacitor, which is square by construction and whose `C = CapDensity * Area` has no opinion about
which way is along. So the withholding is decided by whether the formula actually references
`Length` or `Width`, and a rule that reads neither is unaffected.

The device is still EMITTED in either case, with its terminals, claiming nothing about the
parameters that were withheld. That is R-lvs3-5a's rule applied one level down: a device the
comparison cannot fully handle must still appear in the count, or the two sides disagree about how
many parts there are for a reason the report never gave. Brief 10's compare-only-where-both-claim
then does the right thing with a device that claims nothing.

## `Length`/`Width` are the MINIMUM-AREA rectangle, not the bounding box

An axis-aligned bounding box was the one-line candidate and it is wrong for exactly the artwork this
feature is for. A NiCr body drawn at 30° measures longer and much wider than it is, and
`R = SheetRho * Length / Width` comes out low by a factor of several with nothing saying so — the
same silent class of error the ambiguity rule exists to prevent, arriving by a different door. So
`DeviceCandidates.MinimumAreaRectangle` runs rotating calipers over the convex hull, which is exact
for a rectangle at any angle (including one the flatten turned into a polygon) and yields the
principal axis as a direction rather than as a guess about which way is up.

## `Kind` is a STRING in the `.ctech`, and that is not laziness

`System.Text.Json` throws on an enum member it does not know. A deck carrying one typo in `Kind`
would make the whole TECHNOLOGY unloadable — the layer table, the stackup and every DRC rule with
it. R-lvs14-2d requires an unknown kind to be a `check` error listing the real ones, which it cannot
be if the file never opens. The same argument keeps `Body` and `Terminals` as text: a malformed
expression is one unusable RULE, reported by name, and the rest of the deck still runs.

`Terminals` reads a bare string OR an array (`StringOrStringsConverter`) and always writes the
array, so a hand-edited one-layer rule reads naturally and a round trip is still stable.

## The deck's problems land under `TechProblemArea.Drc`, not an area of their own

`TechProblemArea` names the EDITOR TAB whose fields would fix a problem, and R-lvs14-4d ships no
rule-authoring UI. A `Devices` member with no tab behind it would count on no tab header and would
be invisible in the Technology editor — reported by `circuitrf check` and nowhere else, which is the
half-visible state the enum exists to prevent. The deck is hand-edited beside `DrcRules`, in the same
file and the same layer grammar, so the DRC Rules tab is where someone editing one is already
looking.

## A fresh `Evaluator` per candidate, or every device after the first is wrong

`Evaluator` memoizes by `scope::name`. One shared across candidates answers the second body's
`Length` with the first body's — every device after the first silently carrying the wrong geometry,
with every value plausible and nothing to compare it against. The scope chain is
`device -> technology`, so the constants resolve once per candidate and their own cycle detection is
the engine's.

---

# The LVS series' own traps (brief-lvs-15-docs-and-example.md §3b)

Six things whose failure mode is **silence** — a clean report over a broken design, or a broken
report nobody can act on. They are collected here because each is the kind of defect no test catches
by accident: the output looks right.

## 1. The precedence inversion — recorded next door, and it is the big one

`src/Design/Layout/Extraction/RESOLVED.md` holds it in full, because that is where `PinNaming`
lives. The short form, because it is the single most important fact about this subsystem:

railRF's rule is *net(pad) = the schematic's own binding, else the net stated on the copper*. Read
that way, **LVS asks the artwork what the artwork says, gets the SCHEMATIC's answer back, and every
net on every design matches** — no exception, no warning, no finding. A tool that passes everything,
and nothing about the output looks wrong.

`PinNaming` is an **enum**, it is **required**, and nullable delegates were rejected precisely
because null-means-artwork-only is a shape a later caller re-enters by accident. Making the caller
write the word is the whole mechanism.

## 2. The undrawn ground reference, and why the reminder is unconditional

`LayoutRead` infers net 0 for a via terminating on a ground-reference conductor **that draws no
layer**. The shipped MMIC technology's `Backside Metal` is exactly that: a die's backside metal
exists in the stackup and nowhere in the artwork, so read naively every grounded device on that
process is **open**.

**What it costs read the other way round is worse, and it is why the rule is narrowed.** The note's
R-lvs-15 says *every piece on a ground-reference conductor's drawing layers, if it has any, is net
`"0"`*. Three of the four shipped PCB technologies flag their **bottom copper** as the ground
reference (and the four-layer one flags it *beside* its real inner plane). Applied literally, every
bottom-side trace on a two-layer board becomes ground, they all merge, and the board **passes LVS
while being one short** — the exact failure this series exists to prevent. No rule keyed on the flag
alone can separate the four-layer technology's real plane from its routing layer, because both carry
it. So a reference conductor **with** drawing layers is ordinary copper and is read from the artwork
like everything else. `ADrawnGroundReferenceIsOrdinaryCopperAndNotOneNet` is the gate.

**`lvs.ground.reference-undrawn` is INFO, unconditional, and fires on a clean run.** It is not a
warning because nothing is wrong, and it is not suppressible because **this is the one inference in
the whole extraction the user cannot check by looking at their own screen**. It names the stackup
entry and the number of vias that reached ground through it, so the reader can count them against
what they drew. A reminder that only appears when something else is also wrong is a reminder nobody
sees on the run that mattered.

## 3. The terminal map's four derivations, and the one that must not be a fallback

`TerminalMap.Resolve` answers *which layout pin is which schematic port*, and **returns which rule
produced the answer** — the whole point, because the four are not equally trustworthy and the
consumer has to be able to say so.

| | |
|---|---|
| **declared** | the `.ccell` states it |
| **imported** | R-lvs1-3a's import table, from the source the part came in from |
| **by name** | every symbol pin is named, and every one of those names is a layout pin name |
| **by order** | **only** when no pin on either side is named at all and the counts match |

**A partial name match must NOT fall through to positional (R-lvs1-3e), and this is the trap.**
Three of five names matching is *evidence the author meant them to match and got two wrong*.
Reading the whole cell positionally there produces a **confident wrong answer over a visible clue** —
and a wrong terminal map is not a finding, it is a comparison against the wrong pads, which
manufactures findings everywhere else and hides the real one. So `ByName` returns **null** rather
than a partial map, and the positional branch is guarded on *every* pin on *both* sides being
unnamed. The partial case is reported, naming both unmatched lists.

`ByOrder` reports `check.terminals.derived-by-order` at **warning** on every run that uses it,
including an otherwise clean one, because it is a guess that is usually right — and "usually right"
is the dangerous kind.

**`None` covers two states and only one is a failure.** See §13.4 of `docs/design/lvs.md`: applied
literally the note's ladder errors on a cell with a symbol and no layout, which is most cells in
every workspace. `Terminals: []` in a `.ccell` **derives** and does not declare, for the same
reason — it is "circuitRF made this cell and it has no terminals yet".

## 4. The terminal index in the colour hash — drop it and a drain matches a source

`LvsCompare`'s refinement colours devices and nets by their neighbours, iterating to a fixed point.
Each recolouring appends, per edge, the pair **(terminal position, the far end's colour)** —
`Recolour` adds `port[e]` *and* `endColour[end[e]]`, and `GroupSort` sorts within an owner by
`(port, far-end colour)` through three stable counting sorts.

**Dropping the port index is the invisible defect.** A resistor is symmetric, so every
two-terminal-passive test still passes. A FET is not: a device whose drain is on net A and source on
net B gets the same signature as one wired the other way round, the refinement calls them
indistinguishable, and the comparison happily pairs a drain to a source. Every other test in the
suite goes on passing — the counts agree, the nets agree, the report is clean.

It is one term in a hash, it costs nothing, and it is the difference between a comparison and a
degree count. The code says so at the line (R-lvs7-3c) so nobody optimises it away.

## 5. The nm↔DBU coincidence at 1000 DBU/µm, in its third recorded form

A wBond `Wire`'s points are `Point3` in **nanometres**; a layout is in **DBU**. At the shipped
default of **1000 DBU/µm the two numbers are numerically identical**, so a wrong conversion — or no
conversion at all — is invisible on every default-configured design and fails only on somebody
else's.

This is the third time it has been recorded (WB-C is the first, and `src/Ui/RESOLVED.md` carries the
second), which is the argument for the two rules `AssemblyRead` follows:

- **`LayoutUnits.NmToDbu` is spelled out at the call site**, not folded into a helper. The
  conversion has to be *visible* in the one place the two unit systems meet, because the reader of
  that line is the only person who can notice it is missing.
- **The fixture uses a non-default resolution and coordinates that are not round micron values.** A
  test written at 1000 DBU/µm on whole microns asserts nothing whatever about the conversion — it
  passes identically with the conversion deleted. This is the part that generalises past wBond: a
  unit-conversion test at the default is not a test.

## 6. What the list above did not predict

### An LVS waiver key must not reuse `LvsFinding.Key`

Recorded in full at the top of this file. In one line: a **DRC** waiver names a *place* and
correctly stops applying when the shape moves; an **LVS** waiver names a *relationship* and must
survive a re-route while dying on the schematic edit that invalidated it. Keying it on a marker box
would be wrong in both directions at once — silently un-waived on every re-route, and kept alive
across the change that made it false.

### The shipped example's own six faults are the oracle, and one of them is not a topology fault

`examples/LVS/` ships a correct board and the **same schematic, byte for byte**, against artwork
with six deliberate faults. Five are topology. The sixth — `R3` re-pointed from a 294 Ω part to a
150 Ω one — is the right kind of part, in the right place, with the right designator, wired
correctly, and it is the wrong resistor. **No topology check of any kind finds it**, which is why
the property comparison is not an optional extra, and why the broken board carries it.

The property **tolerances are measured, not chosen**: `Bias tee`'s four parts quantise onto a
0.25 µm grid with a worst-case spread of 0.2 %, and one E96 step — the smallest wrong part anybody
could fit — is 2.4 %. The default sits between them at 1 %. Every dimension with no measured floor
is compared **exactly**, and the report says no tolerance has been established for it rather than a
plausible number being invented.

### `Bias tee` reports one short, and it is the comparison being right

A spiral inductor is one continuous run of metal, so a galvanic extraction finds its two terminals
on one net. The comparison correctly says two schematic nets are one piece of copper. The missing
rule is that a recognised **device's** internal copper is not interconnect — §4.1 tier 3's — and
this cell is the first fixture in the repository that could show it. It is stated on the user
documentation page as a limit rather than hidden.

### Findings un-reduce, or reduction makes the report unusable

Reduction is on by default because every production LVS reduces and a fingered FET reporting as
seven extra devices is how a tool stops being run. But the reduced objects are **not the objects the
designer drew**, so every finding un-reduces before it is reported: a collapsed four-finger device
names all four. A report naming an object that exists only inside the comparison is a report nobody
can act on, which is the same rule that makes a short carry its neck and a property mismatch carry
its tolerance.

---

# Review of the shipped series (2026-09-21)

Read after the fifteen briefs landed, against the briefs and the note. Three things were wrong and
are fixed here; two are recorded because they are not what they look like.

## A board-level waiver un-waived every finding a CELL had signed off

`LvsWaivers.Apply` clears the waiver on every finding no key matches — deliberately, because that
is what makes un-waiving in the panel work: the row is removed from the document's list and the
result in hand is re-marked. A finding hoisted out of a sub-cell by `LvsHierarchy.Within` carries a
key naming the PLACEMENT (`U1/R3`), which the cell's own `.clay` cannot possibly have recorded — so
the parent's list never matches it and the clearing branch took it.

The effect was conditional on the parent having **any** waiver at all (`Apply` returns early on an
empty list), which is why it was invisible: the same design signed off one way reported six errors
and signed off another way reported seven, and nothing said which was the run's own opinion. The
section above — *"a sub-cell's findings arrive already marked against its own `.clay`, which is
right"* — is the stated intent, and the code did not do it.

`LvsFinding.InheritedWaiver` now carries the cell's own sign-off up with the finding, and `Apply`
falls back to it rather than to "not waived". The board can still waive a hoisted finding by its
prefixed key, and can still un-waive its own. `HierarchyTests` gate 11 is the claim.

## The descent stack's spelling, and the cycle it would have missed

`LvsRun` entered `Descending` with `Path.GetFullPath(cellDir)` and `LayoutReadHierarchy` consulted
it with the resolver's own answer. `GetFullPath` **keeps a trailing separator** and the resolver
never writes one, so `circuitrf lvs "cells/Amp/"` on a cell that places itself would have recursed
until the stack ran out rather than flattening and reporting it. One spelling now —
`LvsHierarchyContext.IdentityOf` — used on both sides of the test.

## `LvsReduceOptions.MeasuredNames` is inert, and R-lvs6-3c is satisfied by the clause below it

Nothing in the run path populates it: `LvsRun` passes `LvsReduceOptions.Default` or `NoReduce` and
there is no flag for it. That is **not** a hole in R-lvs6-3a, because the measure clause in
`NodeIsCollapsible` can only fire when the node carries a label and the very next clause refuses
every labelled node. A net's name here IS its label — `tb.LabeledNets` plus the cell ports on the
schematic side, the copper's own `Net` stamp on the layout side — so a node no measure could name
is a node already excluded. `ReductionTests`' own measure test says the same thing in its closing
comment.

The option is kept rather than deleted because the label clause is the one that would be relaxed if
hierarchy stitching ever carried a name that is not a label, and the measure refusal must outlive
it. **Wiring it today would be dead plumbing**, so it is documented instead of plumbed.

## Two in-process CLI test classes cannot run concurrently, and xUnit ran them that way

`LvsPanelTests` and `LvsCliVerbTests` both drive `CliEntry.Run` in process and capture stdout, and
`Console.Out` is one process-wide writer — as is `JsonRun`'s state. xUnit parallelises distinct test
classes, so both verbs' JSON documents landed in whichever buffer was installed last and the second
parse failed with *"'{' is invalid after a single JSON value"*. Reproducible on the filtered run,
not a load-dependent flake. Both classes are now in one collection
(`tests/Ui.Tests/Lvs/LvsCliConsoleCollection.cs`); every other CLI gate in the repository launches
a real process and was never exposed to it.

## The series' own gate (`R-lvs0-1`, `R-lvs0-2`) had not been written

The overview says a test parses the note's requirement numbers and the traceability table and
compares the sets. It did not exist. `tests/Ui.Tests/Lvs/SeriesTraceabilityTests.cs` is it, and it
passes as written — the table does cover `R-lvs-1 … R-lvs-55` with no hole — so what it buys is the
next requirement somebody adds to the note.
