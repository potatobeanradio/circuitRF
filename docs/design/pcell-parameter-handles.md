# circuitRF — PCell Parameter Handles (geometry-driven parameter editing)

**Status:** Shipped (`Linear` and `Angular` handles) · **Date:** 2026-08-06 · **Phase:** implemented from `docs/sonnet-briefs/brief-pcell-parameter-handles.md`

**Reads with:** `pcell-contract.md` (what a PCell is — R2 the one parameter list, R5 determinism, R6
per-parameter-set evaluation, R9 generated artwork is read-only), `pcell-wire-schema.md` (§1 no
metres on the wire, §4.3 the generate reply, §7 versioning), `layout-view.md` §6.3 (editing
handles), and `src/Ui/CLAUDE.md`'s L1d/L1h/L3a entries (the handle, drag and instance machinery this
builds on).

---

## 0. What this is

Today a placed PCell is edited only through its parameter list: open the Properties Inspector (or
double-click for the parameter dialog), type a new `W`, and the artwork regenerates. That is correct
and it is also the wrong shape for a layout-driven workflow. A user laying out a board thinks
*"this trace needs to reach that pad"* — not *"L is 3.4 mm"*. Every other primitive in the editor
answers the first sentence directly: grab an edge, drag it, watch the number follow.

**This document specifies how a PCell instance becomes draggable without giving up being
parametric.** The user grabs a piece of the generated artwork; the parameter that produced it
changes; the cell regenerates. An MLIN keeps its electrical model and its schematic link and edits
like a `Rect`.

**It is optional per PCell, by construction** — a generator that declares nothing gets exactly
today's behaviour, and no existing cell, in this repository or in anyone's kit, has to be revisited.

**Two things this is NOT.** It is not a way to hand-edit generated geometry (R9 stands, unchanged —
see §7). And it is not an inverse function: nothing here asks a generator to read its own output
back and work out what parameters produced it.

---

## 1. Why the editor must be told, and exactly what it must be told

The owner's instinct is right and worth writing down as a rule, because the alternative is tempting
and fails silently.

**R-pch-1. A PCell declares what is editable. The layout editor never infers it.**

An editor that guessed would have to answer *"the user moved this edge; which parameter did they
mean?"* from geometry alone. For an MLIN the guess is easy and for anything real it is not: an edge
of a spiral inductor is a function of `W`, `S`, `Turns` and the mitre rule together, and the guess
that "looks obviously right" is wrong about a third of the time. The failure mode is the worst kind
this codebase has: the artwork regenerates, it renders perfectly, and one parameter is now a value
the user never chose. Nothing on screen says so.

So the generator says. The question is *what*, and the answer that keeps authoring simple is the
narrowest one that still works:

| The generator states | The host works out |
|---|---|
| **Which parameter** this grip drives | — |
| **Where the grip is**, in cell-local DBU | Where it is on screen, through the instance transform |
| **Which way it moves** (an axis, or a pivot for a rotation) | How to project a drag onto it |
| Optionally, a **label** and a **legal range** | Everything else |

**R-pch-2. The generator never states how much the parameter changes per unit of travel — the host
measures it, by asking the generator.**

This is the decision that makes the feature cheap to author, and it deserves its reasoning in full.

The obvious alternative is an affine mapping declared by the author: `value = offset + scale ×
distance`. It is one more field and it looked simpler until it met the three real cases:

- **An in-process C# generator receives lengths in SI metres** (`pcell-contract.md` R7) while its
  own geometry is in DBU, so `scale` for MLIN's `L` is `1/dbuPerMetre` — a constant nobody should be
  hand-writing into a cell definition, and exactly the class of mistake `describe`'s dimension field
  already carries a "worth checking twice" warning about.
- **A script generator receives lengths already in DBU** (`pcell-wire-schema.md` §1), so the same
  cell written in Python needs `scale = 1`. Two languages, two constants, same geometry — every
  authoring guide would have to explain the difference.
- **A centred width** (`W` drawn as ±W/2 about the axis) needs `scale = 2`, which is fine, and a
  Klopfenstein taper's length-versus-`Offset` relationship is not affine at all, which is not.

Measuring removes all three. At drag start the host regenerates once with the parameter perturbed
and reads where the declared grip moved to — a finite difference, in whatever units the parameter
happens to be in, with no unit bridge anywhere in the declaration. The author writes only what they
can see in their own drawing code.

**The split is worth restating because it is the whole design:** the declaration says **what** is
editable and **where** the grip is; the host measures **how much**. The editor guesses neither.

**R-pch-3. Regeneration is authoritative — the grip lands where the generator put it, not where the
cursor was.** A parameter may be quantized (an integer finger count), clamped (a minimum via
enclosure), or related to the geometry non-linearly. In every one of those cases the host proposes a
value, regenerates, and the grip snaps to wherever the fresh artwork says it now is. The user sees
the legal answer immediately instead of a drag that lies until release. This is also what makes
§1's measurement safe: a mis-measured sensitivity costs an extra iteration, never a wrong value.

---

## 2. The handle

**R-pch-4. A handle drives exactly one parameter, along one degree of freedom.**

One parameter, because a grip that drove two would need the host to apportion a two-dimensional drag
between them, and every apportionment rule is arbitrary. One degree of freedom, because it makes the
projection of a drag onto a parameter a single well-defined scalar, which is the entire reason this
model is tractable where a general inverse is not.

The two consequences are both acceptable and both should be stated rather than discovered:

- **Dragging a corner is not offered.** A rectangle's corner changes width *and* length; the user
  drags an edge for each. That is the natural microstrip gesture anyway (widen the trace, lengthen
  the trace), and the Properties Inspector still edits both at once.
- **Several handles may drive the same parameter, and this needs no mechanism.** A centred trace
  declares a grip on its top edge and another on its bottom edge, both naming `W`. Dragging either
  moves both, because regeneration re-places every handle (R-pch-3).

**R-pch-4a. A grip may declare a SECOND parameter on the axis perpendicular to its own — and that is
not the case R-pch-4 rules out.**

R-pch-4 forbids apportioning one drag between two parameters, because every apportionment rule is a
tie-break and every tie-break is arbitrary. An **orthogonal decomposition** is not an apportionment:
travel along the axis and travel across it are independent scalars, each with exactly one parameter,
and the split is unique. There is nothing to guess.

The case that earns it is a taper's far end, which genuinely means two things at once — *how long*
along its axis and *how far off centre* across it:

```csharp
new PCellHandle("L", 0, 0, l, offset, AxisDeg: 0, Cross: new PCellHandleCrossAxis("Offset"))
```

Three properties follow, and they are why this shape is worth having over two coincident grips:

- **Two grips at one point are indistinguishable under the cursor.** An earlier draft of MKlopf
  declared exactly that — an `L` grip and an `Offset` grip both at pin 2 — and the hit-test could
  only pick one arbitrarily. One two-axis grip is what the geometry actually is.
- **Both axes commit as ONE edit, and therefore one undo entry.** A single drag that took two undo
  steps to unwind would not match what the user did.
- **The axes are solved in sequence, not simultaneously** — the primary first, then the cross against
  the parameters the primary has already settled. They are independent as *scalars*, but the geometry
  a cell draws for one may well depend on the other, and solving the cross axis against a stale
  primary chases a target that has already moved.
- **The live ghost is built from BOTH solved values**, so a diagonal drag redraws the artwork as
  moved *and* stretched rather than merely stretched, and the readout names both parameters. The
  readout carries both in the deferred mode too — it is what a deferred drag steers by.

**Measured, because "is it live?" is a question about a clock rather than about the code.** MKlopf is
the real cell this exists for and it previews live comfortably: **~1.2 ms per generate warm**, ~4 ms
for the first pointer move (which pays two solves *and* builds the ghost) and ~2.6 ms per move after
that, against the 16 ms budget. The press itself costs ~7 ms because it runs **two** sensitivity
probes, one per axis — once per gesture, not per move. A heavier cell that does trip the budget
degrades to grip-plus-readout with both numbers still tracking, which is why nothing about
correctness depends on the margin.

### 2.1 Shape of the declaration

```csharp
public enum PCellHandleKind
{
    /// <summary>The grip travels along a straight line through Anchor in direction AxisDeg.
    /// The drag's projection onto that line is the scalar the parameter follows.</summary>
    Linear,

    /// <summary>The grip swings about Anchor. The angle of (cursor − Anchor) measured
    /// counter-clockwise from AxisDeg is the scalar the parameter follows.</summary>
    Angular,
}

/// <summary>One draggable grip on generated artwork, in CELL-LOCAL DBU — exactly the frame
/// <see cref="PCellPin"/> already uses, so the instance transform applies to both identically.</summary>
public sealed record PCellHandle(
    string Parameter,                 // must name a parameter this generator declares (R2's one list)
    long AnchorX, long AnchorY,       // the fixed reference: the point the grip measures FROM
    long X, long Y,                   // where the grip is right now, for these parameter values
    double AxisDeg,                   // Linear: direction of travel. Angular: reference direction.
    PCellHandleKind Kind = PCellHandleKind.Linear,
    string? Label = null,             // shown in the readout; defaults to Parameter
    double? Min = null,               // in the parameter's own units — see below
    double? Max = null);
```

and `PCellResult` gains one optional field:

```csharp
public sealed record PCellResult(
    IReadOnlyList<LayoutShape> Shapes,
    IReadOnlyList<PCellPin> Pins,
    IReadOnlyList<string>? Diagnostics = null,
    IReadOnlyList<PCellHandle>? Handles = null);   // ← additive, null = not draggable
```

**Additive and defaulted null is what makes R-pch-0's "optional" structural rather than a promise.**
Every existing generator — the six built-ins, every cell in a third-party kit — compiles and runs
unchanged and is simply not draggable. There is no migration and nothing to opt out of.

**`Min`/`Max` are in the parameter's own units** — SI metres for a length in an in-process C#
generator, DBU for the same length on the wire — because that is what the parameter values
themselves already are on each side (`pcell-contract.md` R7; `pcell-wire-schema.md` §1). They are a
convenience: the editor stops the grip at the bound and names it, instead of letting the drag run
past and having regeneration pull it back. **A generator that clamps internally needs neither**, and
that path always works because of R-pch-3.

### 2.2 What MLIN looks like

The whole point is that this is a description of the drawing, not of a formula:

```csharp
// src/Ui/Layout/PCells/MlinPCell.cs — the two lines this feature costs a built-in.
var handles = new[]
{
    // Pin 1 is the anchor; the far end travels along +X and IS the length.
    new PCellHandle("L", 0, 0, l, 0, axisDeg: 0),
    // The trace is centred on y = 0, so the top edge is the width grip.
    new PCellHandle("W", l / 2, 0, l / 2, w / 2, axisDeg: 90),
};
return new PCellResult([line], pins, Handles: handles);
```

and in Python, where a cell author is most likely to be working:

```python
@generator("MLIN", [Parameter.length("W"), Parameter.length("L")])
def mlin(params, tech):
    w = params.length("W")
    l = params.length("L")
    layer = tech.signal_layer
    return Result(
        shapes=[Rect(layer, 0, -w // 2, l, w // 2)],
        pins=[Pin("1", 0, 0, layer, w, 180.0), Pin("2", l, 0, layer, w, 0.0)],
        handles=[
            Handle("L", anchor=(0, 0), at=(l, 0), axis=0),
            Handle("W", anchor=(l // 2, 0), at=(l // 2, w // 2), axis=90),
        ],
    )
```

**No units appear in either version.** That is the payoff of R-pch-2, and it is the same code in
both languages — which matters, because B7's own gate is that the same cell written twice must
produce identical geometry, and a feature that read differently in the two would erode it.

### 2.3 Authoring surface — the answer to "where does the user write this?"

**In the same `@generator` function, on the same `Result`, in the same file.** There is no separate
handle-declaration file, no second DSL, no registration step. A cell author who can already write a
generator can write a handle by adding one list argument, and the authoring loop is unchanged: edit
the `.py`, **Design ▸ Reload Generated Artwork**, drag.

That is deliberate and it is the reason the declaration was kept to what it is. A mechanism that
needed its own file, or its own metadata format alongside the kit, would be one more thing to keep
in step with the generator — and this codebase's own PDK history is a list of exactly that failure
(`describe` is the only source of a script's generator list precisely so a written-down second copy
cannot go stale).

**Handles are returned per-`generate`, never declared in `describe`.** Their positions are functions
of the parameter values, so they must come with the geometry; and nothing needs to know whether a
generator has handles before it has generated something to grab. One place, no second copy.

---

**R-pch-4b. A grip may ask for its own ANCHOR to be held still in world space — and that is the only
way "drag this end, keep the other end fixed" can be expressed at all.**

A generator cannot move its own origin: R4 puts pin 1 at (0,0) and the principal axis along +X. So a
cell asked for a longer `L` grows to the RIGHT, always. Dragging the LEFT edge of a trace therefore
grows it away from the cursor — the opposite of the gesture — and no amount of generator-side
cleverness fixes it, because the constraint is the contract.

`KeepAnchorFixed` moves the responsibility to the one side that can act on it. The host reads where
the generator re-emitted the anchor, and translates the placed **instance** by
`anchorWorldBefore − anchorWorldAfter`:

```csharp
new PCellHandle("L", anchorX: l, anchorY: 0, x: 0, y: 0, AxisDeg: 180, KeepAnchorFixed: true)
```

Four consequences, each of which was a real defect before it was written down:

- **It is expressed on the ANCHOR, not as a free "fixed point."** The anchor is already what the grip
  measures from, and it is already re-emitted on every generate — so the host can READ where it moved
  rather than being told a rule for predicting it. There is no rule that would work: MLIN's left-edge
  anchor moves by the whole length change, its top-edge anchor by half the width change.
- **Declaring the OPPOSITE edge as the anchor is the whole of the feature.** The projection from far
  edge to near edge is then the dimension itself, not half of it, and pinning holds the far edge. One
  declaration says both things.
- **The projection is measured from the REGENERATED anchor, not the original one.** This inverts the
  ordinary rule (§4.1 measures against the original anchor precisely so a generator that moves its own
  frame cannot contaminate the measurement) and the inversion is load-bearing: a pinned grip's cell
  coordinates do not move at all — MLIN's left-edge grip sits at (0,0) for every value of `L`, and only
  its anchor moves. Measured against the original anchor it reads as a grip that never moves, i.e.
  `Unmeasurable`, and the drag is refused outright.
- **The parameter edit and the translate are ONE command.** Two would let Undo restore the geometry
  while leaving the instance moved.

A no-op when the anchor does not move, so it is safe — and more readable — to set it on every grip of
a set rather than only the ones that need it.

**A pinned grip's CROSS axis is pinned too.** `AsCrossHandle()` carries `KeepAnchorFixed` across, and
it has to: the cross axis is solved through the ordinary machinery, so it needs the same inverted
measurement frame the primary does. Dropping the flag there makes a pinned two-axis grip's second
axis read as dead — it is silently dropped and reported, while the primary axis goes on working, so
the grip half-works rather than failing visibly.

**R-pch-12. A handle may declare WHAT KIND of quantity its parameter is — and that does not
reintroduce the declared scale R-pch-2 rejected.**

`PCellHandleQuantity` is `Unspecified` (default), `Length`, or `Angle`. It says nothing about *how
much* the parameter changes per unit of travel — that is still measured, still unit-free, and R-pch-2
is untouched. It says only what the number IS, which the host needs for exactly two things it cannot
work out for itself:

- **The drag readout.** A width printed as `0.0039116` is not a width. With the quantity declared the
  readout is `W = 154 mil` — the document's own display unit, the same one every other dimension in
  the editor is shown in. An angle prints as degrees.
- **The snap grid.** A user who sets 1 mil snapping means the committed *parameter*, not just the
  cursor. Snapping the cursor alone (which is all that happened before) leaves the solver free to
  stop anywhere inside its convergence tolerance, so a width dragged to 468 mil commits as
  468.00006 mil — and that value is what a schematic push-back and every later export then carry.
  With `Length` declared, the solver's own candidate lattice becomes the snap grid.

Three things about the snapping are load-bearing:

- **It is applied INSIDE the solve, not to the answer afterwards.** The solver then regenerates *at*
  the quantized value, so the grip is drawn where the committed value actually puts it and the
  anchor-pin translate is computed from that same geometry. Quantizing afterwards would leave the
  preview and the commit up to half a snap step apart, which reads as the artwork jumping on release.
- **Only a length is quantized.** Rounding an impedance or an angle onto a distance lattice is
  arithmetic with no meaning behind it. MKlopf's edge grips drive `Z1`/`Z2` and are deliberately
  exempt.
- **Alt suspends it, and snapping off disables it**, exactly as for every other snapped gesture — or
  "snapping is off" would only be half true.

`Unspecified` is the default and is not a defect: the readout falls back to the raw value and no grid
is applied, which is precisely how every handle behaved before this existed. A script-supplied grip
that says nothing keeps working unchanged.

## 3. Wire format

`pcell-wire-schema.md` §4.3's reply gains one optional array.

```jsonc
← { "ok": true,
    "shapes": [ … ],
    "pins":   [ … ],
    "handles": [
      { "parameter": "L", "kind": "linear",
        "span": { "at": 24, "count": 4 },     // anchorX, anchorY, x, y — int64 DBU in the payload
        "axisDeg": 0.0,
        "quantity": "length",                 // optional — R-pch-12; absent = Unspecified
        "label": "Length", "min": 50000, "max": null }
    ] }
```

**`quantity`/`crossQuantity` are additive and deliberately NOT a wire-version bump.** R-pch-5's rule
(a version mismatch refuses rather than negotiating) is about a reply an older host cannot READ.
This one it can: an absent field decodes to `Unspecified`, which is exactly the behaviour every
handle had before the field existed, so a version-6 script that says nothing keeps working and one
that does say something gets a better readout without either side negotiating. An unrecognised
*value* is also silently `Unspecified` — the opposite trade from an unknown handle `kind` (dropped
and reported), and deliberately so: an unknown kind would silently lose a grip, whereas an unknown
quantity costs only a nicer readout, and a hint with no effect on the answer must not cost a working
cell.

**Coordinates ride in the binary payload, exactly like every other coordinate**
(`pcell-wire-schema.md` §2: no coordinate ever appears in the JSON, so a fractional one is
unrepresentable). `min`/`max` are parameter *values*, not coordinates, so they are ordinary JSON
numbers encoded as §3 already encodes a value — the same rule, no new one.

**R-pch-5. This is wire version 6, and the bump is required even though the field is additive.**
The schema refuses on any version mismatch rather than negotiating (§7's own reasoning: negotiation
means N code paths of which the rare ones are wrong), so `WIRE_VERSION` is a strict equality check
and adding a field to a reply is a bump by definition. The contract version is untouched — a
generator's *signature* has not changed, only what it may optionally include in its result. The two
numbers move independently and this is the case they were separated for.

**R-pch-6. An unrecognised handle `kind` drops that handle and reports it once — it never fails the
generate.** This is what lets a third `kind` be added later without another bump becoming a cliff: a
newer script talking to an older host loses the handles the older host cannot draw and keeps its
artwork, which is the correct degradation. Degrade, never deny — the same rule a missing kit, a
missing layout and a foreign document already follow.

---

## 4. What the editor does with it

### 4.1 Where the drag actually happens, and why that is the interesting part

A placed PCell is a **`LayoutInstance` in a parent layout, pointing at a content-addressed generated
cell** under `.generated-cells/` that may be shared by every other instance with the same parameters
(`pcell-contract.md` R6, and `src/Ui/CLAUDE.md`'s L5 R-L5-1/R-L5-2). Three consequences fall out and
all three are already solved by machinery that exists:

**R-pch-7. A handle drag edits the INSTANCE's parameters, never the generated cell.** It commits
through the existing `LayoutEditorViewModel.EditInstancePCellParameters`, which is copy-on-write by
construction: the new parameter set hashes to a different cell folder and the instance is repointed
at it via one `ReplaceInstanceCommand`. The old cell — and every sibling instance still referencing
it — is untouched. **Dragging one instance can never change another**, and this needs no new code,
only a second caller.

It also means **R9 is not weakened**. The generated artwork is still read-only; what the user is
dragging is a grip on a parameter that happens to be drawn where the artwork is.

**R-pch-8. Handles are declared in cell-local DBU and the cursor is inverse-transformed into that
frame — never the other way round.** `LayoutInstanceTransform.InverseTransformPoint` already exists
and is already how L3a hit-tests inside an instance. Doing it this way makes rotation, mirroring and
magnification correct for free, including the one that is easy to get wrong: **with `Mag = 2`,
dragging 2 mm on screen is 1 mm in the cell.** Transforming the handle out to world space and
projecting there would need that division written by hand, in a place where getting it wrong is a
silent factor-of-two.

**R-pch-8a. Placement is circuitRF's problem, never the cell author's.** A generator declares its
grips in its own cell-local frame, exactly as it declares its shapes and its pins, and is never told
how — or whether — the cell has been placed. Rotation, mirroring, magnification and array position
are applied by the host on the way out and undone by the host on the way in. Concretely, and each of
these is a place an author would otherwise have had to get right:

| | Handled by |
|---|---|
| Where the grip is drawn on screen | `TransformPoint` on the declared position |
| Which way the axis hint points | the declared axis transformed as a **direction** — a probe point along it, transformed and differenced, so a mirrored cell hints the direction it will really move |
| Which screen direction is "along" vs "across" the axis | falls out of the inverse transform, because the projection happens in cell space |
| How far a screen drag is in cell terms | the inverse transform, including the `Mag` division |
| Which array cell was grabbed | the base (0,0) placement, since an arrayed instance is one object with one parameter set |

The consequence worth stating plainly: **a cell written for an unrotated placement works rotated,
mirrored and magnified with no change and no awareness.** An author who writes `axis: 0` for "along
the trace" is describing their own drawing, not the screen. There is deliberately no way for a
generator to ask how it was placed, because a generator that could would be a generator that could
get it wrong.

This is pinned by the transform gate — all eight rotation×mirror combinations plus a non-unit `Mag`,
each asserting that a world-space drag lands the *same* parameter value.

**Arrays show handles on the base placement only.** An arrayed instance is one object with one
parameter set (R-L3a-5), so every placement would show the same grips driving the same values —
2,500 copies of them on a 50×50 array. One set, on the (0,0) placement, is the whole truth.

### 4.2 The gesture

Mirrors L1d's handle drag exactly, because a user should not have to learn a second grammar:

| | |
|---|---|
| **When shown** | A single selected instance whose resolved cell is PCell-backed and whose result declares handles. Never on a multi-selection (L1d's own rule). |
| **Hit radius** | Device pixels, computed fresh per query from the current zoom — never cached, never derived from `SnapDbu`. This codebase has been burned by the alternative once already. |
| **Priority** | Handles are tested before the instance body, mirroring L1d. There is no conflict with L1d's own handles: a shape shows geometry handles, an instance shows parameter handles, and an instance has never had geometry handles. |
| **Snap** | The projected point snaps to the layout's own `SnapDbu` **in world space, before the inverse transform** — the user is aligning to the grid they can see. Alt suspends it, as everywhere. |
| **Readout** | The existing toolbar `DrawReadoutText`, showing `Label = value` in the document's display unit, live. This is the part a layout-driven user actually reads. |
| **Escape** | Cancels; nothing committed, no undo entry. |
| **Commit** | One `ReplaceInstanceCommand` on release — one undo entry per drag, however many pointer moves it took. |
| **Visual** | **L1d's own grab square, reused**, in its own theme role (`ColorRole.LayoutPCellHandle`), with a small hollow centre and a dashed axis hint showing which way it travels — two hints on a two-axis grip. The editor already has a visual language for "this is draggable" and a second one would be a second thing to learn; the *difference* that matters (this edits a parameter, not geometry) is carried by colour, the centre mark, and the axis hint, which no L1d handle has. Inventing a shape is also how the first draft went wrong: it used a hollow diamond, which is already L1d's **bulge** handle. The two glyph sets are never on screen together — a shape shows geometry handles, an instance shows parameter grips — so the difference has to read across a change of selection, not side by side. |

**A typed override during the drag** — type an exact value and commit there regardless of where the
pointer sits — is the natural follow-on and costs almost nothing, since L1h already established the
pattern and the readout field already exists. Not required for a first version.

### 4.3 Sensitivity measurement, concretely

At drag start, once per gesture:

1. Perturb the parameter by δ and regenerate **in memory** (`PCellGeometryCache`, never
   `GeneratedCellStore` — see R-pch-9).
2. Read the same handle's new position; project the displacement onto the declared axis.
3. If it did not move measurably, grow δ geometrically and retry, to a small fixed cap.

**δ is chosen unit-free** — relative to the current value, with an absolute fallback when the value
is zero — precisely so the host never needs to know whether it is dealing with metres, DBU, ohms or
a turn count. That is what keeps R-pch-2's promise: no unit bridge anywhere.

Then per drag step: propose a value, regenerate, and if the grip did not land within tolerance of
the drag target, correct and retry to a bounded cap.

**The correction uses a SECANT, not the probe's slope, and that turned out to be load-bearing.**
Implementation measured it: a quadratic cell driven by the fixed probe slope *oscillates* around the
target and was still 12% out after three corrections. Re-deriving the slope from the last two
(value, projection) pairs converges on the fourth, while a linear cell still converges on the first.

#### The numbers, as implemented (`PCellHandleSolver`)

| | Value | Why |
|---|---|---|
| First probe δ | `|V| × 1e-3` | Relative, so it is unit-free. |
| δ at `V = 0` | `1e-9` absolute | Nothing to be relative to; growth finds the scale. |
| δ for an `Int` | `1` | The smallest meaningful step, by definition of the kind. |
| δ growth | `×10`, up to 12 attempts | Also tries `−δ`, so a parameter sitting on its own upper clamp is still measurable rather than reading as dead. |
| "Moved measurably" | 4 DBU (linear) | Grip positions are integer DBU; one DBU of movement would make the finite difference mostly quantization noise. |
| Solve iteration cap | **6** | Three was the first choice and was not enough — see above. |
| Convergence tolerance | 1 DBU (linear) | The grid snap already discretizes the target. |
| Value lattice | **12 significant digits** | R-pch-11. Far beyond any real geometric precision, well inside a double's 15–17. |

**R-pch-11. The solver is deterministic, and every candidate value is snapped to the lattice — not
only the committed one.**

Same start parameters and same target ⇒ same committed value, bit for bit: a fixed δ schedule, a
fixed iteration cap, a fixed tolerance, and no wall-clock anywhere in the decision. Snapping *every*
candidate means the whole iteration runs on the lattice, so the outcome cannot depend on how many
corrections it happened to take.

This is not tidiness. The committed value is fed to `PCellValue.ToString()`, which **is** the content
hash that names the generated cell (`GeneratedCellStore`). A value differing in its seventeenth digit
between two identical drags mints a second cell folder for one design intent — silently defeating
R6's sharing and churning `.generated-cells/`.

**R-pch-9. No generated cell is written to disk during a drag.** Preview regeneration goes through
the in-memory `PCellGeometryCache`; `GeneratedCellStore.GetOrCreate` runs exactly once, on release.
A drag that wrote a folder per pointer move would leave hundreds of orphaned generated cells behind
(there is no garbage collection for them by design), and it would make the cost of dragging depend
on filesystem latency.

**R-pch-10. Live artwork preview is a budget, not a guarantee — and a generator that already knows
it is expensive may skip straight past the measurement.** `PCellResult.Preview` is `Auto` by default,
which is the behaviour below; a generator that declares `Deferred` is believed without being timed at
all, saving the one full regeneration Auto spends finding out. That is the whole of the saving, and
the whole of the risk: **an author who is wrong about their own cell costs the user a live preview
they could have had, never a wrong answer** — the committed value is identical either way, because
the preview mode governs only what is drawn on the way there. An unrecognised value on the wire reads
as `Auto`, deliberately: refusing a generate over a performance hint would trade a working cell for a
preference.

Under `Auto`, the first regeneration of a drag is timed. Above **16 ms** — one frame at 60 Hz, timed on the first solve of the gesture and held for the rest of it, because re-deciding per move would make the preview flicker between modes on a cell sitting near the budget — that drag falls back to a **deferred preview**:
the pre-drag artwork stays on screen, the grip and its axis hint follow the cursor, the numeric
readout updates from the measured sensitivity, and the artwork regenerates once on release. A
743-shape vendor cell that issues 115 boolean round-trips per generate cannot be regenerated per
frame and should not try. The drag never stutters, the readout never lies, and correctness never
depends on how fast the generator is.

### 4.4 Two commit targets, one declaration

A layout document can itself *be* the generated layout (`LayoutView.PCellOrigin` non-null — the
`RegeneratePCell` path). Handles work there identically; only the commit target differs
(`RegeneratePCell` instead of `EditInstancePCellParameters`). Nothing in the declaration changes.

### 4.5 The Properties Inspector stays the primary surface

Handles are an accelerator for the parameters a cell chose to expose geometrically, not a
replacement for the list. Every parameter is still editable there — including the ones no handle can
sensibly drive (a Klopfenstein's Γmax, a model name, a boolean mode). **Both surfaces commit through
the same `EditInstancePCellParameters`**, so they cannot disagree about what an edit means.

---

## 5. What this unlocks, and what still needs a click

The owner's framing — *"a user gets the benefit of an MLIN for schematic simulation, but also the
ease of layout design editing of a Rect primitive"* — closes end to end with no new mechanism beyond
this document:

1. Drag the MLIN's end in layout → `L` changes → artwork regenerates.
2. **Design ▸ Update Schematic from Layout** → `LayoutToSchematicGenerator` pushes the new `L` onto
   the linked schematic component, in the workspace's own display unit.
3. Run → the electrical model sees the length the board actually has.

Step 2 stays **explicit, not automatic**, and that is deliberate: a round trip that fires on every
drag would make a layout experiment silently rewrite a schematic the user may be mid-way through
simulating. The command already exists, already reports what changed, and already undoes.

**Known consequence worth stating before someone reports it as a bug:** dragging a pin-bearing
parameter moves the cell's pins, and **nothing follows them**. Layout has no ratsnest and no
auto-routing to PCell pins (the guide-line ratsnest was removed as real geometry — see
`src/Ui/CLAUDE.md`'s L5 follow-ups §2). A wire drawn to a pin does not stretch. Making connectivity
follow a parametric edit is a separate feature with its own design question, and it is not gated on
anything here.

---

## 6. The alternative that was considered and not chosen

Neither the model specified here nor the one below is invented. Layout tools have converged on two
answers to "make a parametric cell draggable", and this document takes the narrower of them.

**The rejected one is the coercion model:** the cell exposes an editable *guiding shape*, the user
edits it with the ordinary geometry tools, and the cell is handed the modified shape and asked to
work out which parameter values would have produced it. It is genuinely more general — arbitrary
editing, arbitrary interpretation.

It is not the default here for one reason: **it moves the hard part onto the cell author.** Coercion
is the inverse problem in full — the author must handle a shape edited in a way no parameter set can
produce, decide which of several parameters the user meant, and get it right for every edit the
editor permits. That is a large, subtle, per-cell obligation, and the owner's own constraint is that
authoring stay simple enough for a user writing their own PCells.

A `coerce` op could be added later as an **additional** capability for cells that need it — a
generator that offers it gets free-form editing, one that offers only handles gets grips, one that
offers neither gets today's dialog. It would sit alongside this, not replace it. It is explicitly
out of scope.

---

### 4.6 R-pch-12 — telling the two gestures apart (grip-lock and hover)

**Owner report, 2026-08-27.** Click-dragging the corner of a PCell that has grips — MKlopf is the
case it was reported on — sometimes moved the whole instance and sometimes edited the parameter, and
which one a press would produce felt like a coin flip. It was not one bug but three, all of which
made the same press ambiguous:

1. **The gesture depended on selection state.** Grips only resolve for a selection of exactly one
   instance, so the drag that SELECTS an unselected PCell moves it, while the identical drag on the
   identical pixel a moment later edits a parameter. The deciding input was the user's selection
   history, not anything under the cursor.
2. **The two targets were the same pixels, with nothing marking the boundary.** A grip claimed a
   press within the ordinary ~4-device-pixel hit radius; a press one pixel further moved the cell.
   There was no hover feedback of any kind — no highlight, no cursor change — so the user was asked
   to hit an invisible disc whose miss-behaviour was a completely different operation. It is worst
   exactly where it was reported: MKlopf's grips sit ON the corners, which is also where a user
   naturally grabs to move.
3. **A drawn grip could silently refuse and fall through to a move.** A grip draws once `Validate`
   passes, but the press additionally runs `MeasureSensitivity` against the real generator; when that
   failed, the press fell through to the instance-body move. The user clicked exactly on a visible
   grip and the cell moved.

**Grip-lock: Alt held at press.** The nearest grip within a much larger radius wins, and the press is
CONSUMED whatever happens — no move drag, no marquee, no selection change, and no fall-through when
the grip refuses. Holding Alt is a statement that the user is only talking to grips.

- **Gated on the selection actually having grips** (and on Scale mode being off, since its own
  handles take priority over everything — R-L1h-5). This is what leaves every other meaning of Alt in
  this editor untouched: suspend-snap, Alt+click overlap cycling, scale-about-centre.
- **The radius is BOUNDED** (`LayoutCanvas.GripLockHitTolerancePixels`, 24 device pixels, converted
  fresh from the live zoom like every other tolerance here). Owner's call, against "nearest grip
  anywhere": an unbounded radius means an Alt+press well away from the cell yanks a grip the user was
  not looking at, and a wide cell is where that is easiest to do. Outside the radius the press does
  nothing, which is the promise, not a failure of it.
- **ALT IS SPENT BY THE PRESS.** For the rest of that gesture Alt no longer means suspend-snap. This
  carve-out is the design's own precondition, not a convenience: the workflow grip-lock exists to
  make reliable is *grab the grip, then snap it to a real feature elsewhere in the layout*, and Alt
  keeping its usual meaning would silently turn the geometry snap off in exactly that gesture. It is
  stripped once, at the top of `HandleSelectMove`, so the snap QUERY (R-snp-11's own suppression) and
  the grip solver can never disagree about whether snap is on. A drag begun **without** Alt keeps
  Alt = suspend-snap, unchanged — if you want to suspend snap, do not lock.

**Hover.** The other half, and the one that works without a modifier: on an idle hover the grip under
the cursor is drawn filled and enlarged, and the canvas swaps in an axis cursor derived from that
grip's own travel direction (two-axis and angular grips report the omnidirectional cursor rather than
a lie about a single axis). While Alt is held, hover uses the LOCK radius — the highlight has to
promise exactly what the press will deliver. While Alt is held with grips showing, every grip draws a
halo, because what the user needs to see is that the mode is on, not merely which grip is nearest.

**Superseded in part, same day (R-dup-1/R-dup-2).** Alt now also arms a DUPLICATE drag, and no longer
suspends snap anywhere in the layout editor. Two consequences here: the "Alt is spent by the press"
carve-out above is moot — a locked drag has nothing to spend, because snapping no longer answers to
any modifier — and grip-lock consumes an Alt press only when a grip is actually CLAIMED. A press that
finds no grip falls through to ordinary handling, so Alt+dragging a PCell's body copies it rather than
doing nothing. What grip-lock still guarantees is unchanged: a grip in range wins, a grip that refuses
still refuses without falling through, and neither outcome moves the original. See `src/Ui/RESOLVED.md`.

**The armed state is a held-key latch, and it is cleared on LostFocus.** Hold Alt, click a toolbar
button, and the key-up is delivered to whatever took focus. This editor has already shipped that bug
once with Space-to-pan; `ClearGripLockArmed` is why it is not shipping it again.

**What is deliberately NOT changed:** grips still resolve for the SELECTION only, so the click that
selects a PCell still cannot edit a parameter. Resolving grips for whatever instance the cursor
happens to be over would invoke a generator per hover, which a 743-shape vendor cell in `Deferred`
mode cannot afford. Cause 1 above therefore survives — but under hover feedback it is no longer
invisible: an unselected PCell shows no grips and no grip cursor, and grip-lock covers the impatient
case once it is selected.

---

## 7. Invariants this must not break

Restated because each one is load-bearing and each is easy to break by accident:

- **R5 (determinism)** — a handle declaration is part of `Generate`'s output and must depend only on
  the declared inputs, exactly like the shapes do. R-pch-2's measurement regenerates the cell; a
  generator whose handle positions wobbled between identical calls would make a drag jitter.
- **R6 (evaluate once per parameter set)** — preview regeneration goes through the geometry cache,
  which is already keyed on the parameter fingerprint. A drag produces new values, so each preview
  is a genuine miss; nothing here defeats the cache for the placement case it exists for.
- **R9 (generated artwork is read-only)** — unchanged. §4.1.
- **R2 (one parameter list)** — a handle naming a parameter the generator does not declare is a
  defect and is reported as one (§8), never silently accepted as a new parameter.
- **Copy-on-write across instances** — §4.1, R-pch-7.
- **One undo entry per gesture** — §4.2.

---

## 8. Failure modes, all of which report and degrade

| Situation | Behaviour |
|---|---|
| Generator declares no handles | No grips. Exactly today's behaviour. Not an error, not reported. |
| Handle names an undeclared parameter | Dropped, reported once naming the handle and the generator. |
| Handle drives a **String** or **Bool** parameter | Dropped, reported. There is no continuum for a drag to move along; those belong in the dialog. |
| Sensitivity unmeasurable (grip does not move for any δ) | That handle is dropped for this session with a stated reason. The parameter stays editable in the dialog. Under grip-lock the press is consumed and **nothing moves** (R-pch-12); an ordinary press still falls through to a body move, which is what a plain press is allowed to mean. |
| Generator throws while probing or previewing | Drag is abandoned, design unchanged, the script's own traceback surfaced — the same treatment a failed parameter edit already gets. |
| Drag hits a declared `Min`/`Max` | Grip stops at the bound; readout names the bound. |
| Regeneration does not converge on the dragged position | Best achieved value is committed and the grip shows where it actually landed (R-pch-3). |
| Unknown handle `kind` (older host, newer script) | That handle dropped, reported once. R-pch-6. |

**None of these block editing, and none of them are silent.** That pairing is the rule the whole
PCell area already runs on.

---

## 9. Gates

The ones that would actually catch a regression, not merely exercise the path:

1. **Round trip** — for each built-in that declares handles: drag to a target, commit, resolve the
   new cell, assert the handle came back within tolerance of the target. This is the property the
   whole feature rests on.
2. **Transform** — a rotated / mirrored / magnified instance drags correctly, over all eight
   rotation×mirror combinations plus a non-unit `Mag`. The analogue of L3a's own pixel-identity gate,
   and the one that catches R-pch-8 being done backwards.
3. **Copy-on-write** — two instances of the same generated cell; drag one; assert the other's
   `CellRef` and resolved geometry are byte-identical.
4. **Undo** — one drag of N pointer moves is one undo entry, restoring the original `CellRef`.
5. **No disk during a drag** — a counter on `GeneratedCellStore` writes exactly once per drag and
   zero times during pointer moves. R-pch-9 is a counter assertion, never a timing one.
6. **Read-only preserved** — the original generated cell's `.clay` is unmodified after a drag.
7. **Degradation** — a generator with no handles, one naming an undeclared parameter, and one on a
   String parameter: no grips / dropped-and-reported / dropped-and-reported respectively.
8. **Slow-cell fallback** — a synthetic generator over the budget forces deferred preview and still
   commits the correct value on release.
9. **Non-linear cell** — a generator whose grip position is quadratic in its parameter converges to
   the dragged position within the iteration cap.
10. **Wire round trip** — handles survive encode→decode for every built-in, driven through the real
    transport, alongside the existing shape/pin round-trip theory.

---

## 10. Deferred, with reasons

- **Free two-dimensional handles** (a corner driving an arbitrary X/Y parameter pair). R-pch-4's
  apportionment problem is real. Note that R-pch-4a's **orthogonal** two-axis grip is not this and
  did ship — the difference is that a decomposition onto perpendicular axes is unique where a general
  2-D apportionment is not.
- **`coerce` / free-form guiding shapes.** §6.
- **A grip that must ROTATE its instance.** `LayoutInstance.Rot` is a four-value enum
  (R0/R90/R180/R270), so an arbitrary rotation is not representable and R-pch-4b's translate has no
  rotational sibling.

  **This was briefly — and wrongly — used to argue that MBend's `Angle` could only be driven from
  pin 2.** The reasoning was that pin 1 sits at (0,0) for every value of `Angle`, so a grip there is
  invariant in the parameter; that much is true, and a pivot-anchored pin-1 grip really would be
  refused as `Unmeasurable`. The error was concluding that no anchoring works. **Anchoring the pin-1
  grip on PIN 2 makes the same parameter perfectly measurable**, because pin 2 is the end that moves
  — and holding pin 2 still (R-pch-4b) needs only a translation, never a rotation. Both pins now
  carry an angle grip. The general lesson is worth more than the case: *when a grip appears
  unmeasurable, check every candidate anchor before concluding the parameter is unreachable from
  that point* — the anchor is a free choice, and a moving anchor with `KeepAnchorFixed` is often the
  one that works.
- **Connectivity following a parametric edit.** §5.
- **Automatic schematic round-trip.** §5, deliberately.
- **Handles on a kit's text-valued length parameters** (`'1u'` as a string — the shape a production
  kit uses). A drag must produce a number; a kit wanting grips declares those parameters as reals.
  Stated so it is a known limit rather than a surprise.
