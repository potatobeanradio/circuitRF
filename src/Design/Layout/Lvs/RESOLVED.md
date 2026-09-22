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
