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
