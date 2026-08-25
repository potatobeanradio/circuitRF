# circuitRF — PCell Contract Design

**Status:** Shipped (contract version 2) · **Date:** 2026-07-29, implemented 2026-07-28; version 2 2026-08-03 · **Phase:** L5a, revised B0

**Contract version 2 (2026-08-03) — read this before R2 and R5 below.** Two changes, both made before
any third party could write a cell against version 1, because neither is retrofittable afterwards:

1. **Parameters are kinded values, not bare doubles** (`PCellValue`: Real, Int, Bool, String). A real
   cell names a model, counts fingers, picks a display mode — every one of those is a string, an
   integer or a flag, and a `double`-only contract forces each to be smuggled through as a number the
   generator decodes by private convention. Int is separate from Real deliberately: a count that
   arrives as 3.0000000000000004 either gets rounded by a rule the caller cannot see or produces
   geometry nobody asked for.
2. **R5's literal "no file reads" is replaced by a determinism obligation** — see R5 below. The
   prohibition was unsatisfiable for the script host §9 leaves open, which reads its own modules by
   nature.

*Neither is a file-format change.* A parameter of the only kind a version-1 workspace can contain — a
Real — is written to `.clay` as the same bare JSON number and hashed into a generated cell's folder
name as the same string as before, so every existing workspace resolves unchanged. Only a genuinely
new kind takes a tagged form. The six built-in generators were migrated with a byte-comparison of
their geometry against output recorded beforehand (`PCellGeometryGoldenTests`): no coordinate moved.

**Implementation note (L5a completion):** the contract described below is implemented as designed.
One resolution the implementation made, recorded here since it affects how every future reader
should picture the contract: **the four built-in components (MLIN, MBend, MTee, MCross) are
SymbolKind-registered exactly like TLIN/R/L/C — not literal on-disk cell folders.** R1's "it is a
real cell — it has a folder, a `.ccell`" describes the contract's *general shape* (what a future
user-authored or PDK-shipped PCell must look like); for the phase's own built-ins, that shape is
satisfied in spirit (they participate in the same parameter/symbol/electrical-model machinery any
component does) without literal `.ccell` files, via a small built-in `PCellRegistry`
(`src/Ui/Layout/PCells/`) keyed by the same type name the electrical `ComponentModel` uses. Full
`CellFolder.ResolvePrimary` integration (a project-tree node, a real placed `LayoutInstance`
resolving to "generated") is L5's own scope per this brief's guardrail — this phase proves the
contract through direct API/harness-level tests (`PCellContractTests.cs`,
`PCellReadOnlyAndRegenerationTests.cs`) rather than full schematic→layout placement. §9's own "the
host is undecided" note stands unchanged by this — built-ins being C# was already the plan; only
*where they live on disk* was resolved.

**Parameter validation (§9 open item, now settled for this phase):** a PCell may report an
out-of-range parameter via a `MicrostripValidityReporter`-shaped mechanism (report once per
distinct violation, to stderr, naming the parameter and its published bound — R-pc-16). This is
NOT a hard refusal (the formula still evaluates; R4 of microstrip-models.md is explicit that
silent extrapolation is the thing to avoid, not evaluation past the bound). Whether a PCell can
declare a hard *rejection* range (values it refuses outright) remains open beyond what this phase
needed.

Specifies the **PCell contract** — the interface between circuitRF and a cell whose layout artwork is
**generated from parameters** rather than stored as a file. This is the interface a future **user-authored
PDK** will depend on, which is why it is written down before the first PCell exists rather than inferred
from one afterwards.

**Reads with:** `layout-view.md` §3.1 (primitives), §9 (schematic→layout), `mom-engine.md` §10.4 (stackup);
`workspace-and-project-tree.md` §2 (`.ccell` parameters), §5A.2 (technology resolution);
`expressions.md` (the parameter expression engine); `data-model.md` §2.2 (parameter scope).

**Scope note.** This document defines **what a PCell must satisfy**, not **how a third party writes one**.
The choice of host — scripting language, compiled plugin, or both — is deliberately open (§9), and the
built-in components that motivated this contract are ordinary C#. Separating the two is what lets the
component library proceed without settling the extensibility question.

---

## 1. Why the contract exists

`layout-view.md` §9's schematic→layout resolves each cell to a **static** `.clay` layout view. That is
sufficient for hand-drawn cells and insufficient for anything parametric: a microstrip line's artwork depends
on its `W` and `L`, and no stored file can express "width = the W parameter."

A **PCell** closes that gap. The contract below is deliberately small, because it is the surface a PDK
vendor's cells will be written against, and a small surface is one that can be kept stable.

**PDK support has two halves.** The *model* half — device equations and their evaluation — is the subject of
the PDK model-interface effort. The *artwork* half is this document. They are two faces of one feature and
should be versioned and documented as such.

## 2. What a PCell is

**R1. A PCell is a cell whose layout view is generated rather than stored.** It is otherwise an ordinary
cell: a cell folder, a `.ccell`, a parameter list, a symbol, a project-tree node. Hierarchy, instances,
arrays, push-in navigation and the per-cell geometry cache all work unchanged.

Exactly one thing differs: `CellFolder.ResolvePrimary(…, ViewType.Layout)` may answer *"this cell's layout is
generated"* rather than naming a file.

This is the rule that keeps the feature cheap. A PCell that were a *new kind of thing* would need parallel
handling in every consumer; a PCell that is a cell needs none.

## 3. The contract

```
Generate(parameters, technology) → { shapes, pins }
```

### 3.1 Inputs

**Parameters** are the resolved values of the cell's own parameters, from its `.ccell` — kinded
values (Real, Int, Bool, String) as of contract version 2, not bare numbers.

**R2. A PCell reads the same parameter list its symbol displays.** One list, not two. A symbol showing `W`
and a generator reading a different `W` is a defect that surfaces only as wrong geometry.

**Technology** is the resolved `.ctech` (§5), supplying the substrate and the layer table.

### 3.2 Outputs

**Shapes** are ordinary `LayoutShape`s (`layout-view.md` §3.1) in DBU, on layers named by the technology.
A PCell introduces no new geometry type.

**R3. A PCell returns pins alongside shapes.** Geometry alone cannot be connected. §9 stamps nets onto
instance pins; abutment needs to know where a line ends; §10.6's EM ports attach to them.

A pin carries:

| Field | Why |
|---|---|
| **Name** | Must match the symbol's pin, or schematic and layout disagree about connectivity |
| **Location** | Where it sits in cell-local DBU |
| **Layer** | Which conductor it lands on |
| **Width** and **outward direction** | A microstrip connection is an **edge**, not a point — and a bend needs to know which way its arm faces |

The last row is the one most easily omitted and the most expensive to add later, because every PCell written
without it would need revisiting.

### 3.3 Origin and orientation

**R4. Pin 1 sits at the cell origin, and the cell's principal axis runs along +X.**

Stated because otherwise every author chooses differently and nothing abuts. For a line, the origin is the
input end with the line running right; for a bend, the input arm; for a symmetric junction, the centre with
arm 1 along +X.

### 3.4 Purity

**R5. `Generate` is deterministic given its declared inputs.** The same parameters and technology
produce byte-identical output, always — on any machine, in any process, at any time. No clock, no
ambient or global state, no randomness, no set-iteration order, no address-derived hashing, no
accumulation whose order varies between runs.

*Restated at contract version 2.* R5 used to read "no file reads." That was the wrong way to say it:
a script host reads its own modules in order to exist at all, so the literal prohibition was
unsatisfiable for the very extension this contract is meant to admit — and it named a mechanism
instead of the property that actually matters. A generator that reads a file is fine **provided that
file's content is part of its cache key**; that is the content-hash obligation, and it belongs with
the generator-content hashing work rather than here.

This is not a style preference. The per-cell geometry cache keys on **(cell, parameter values, technology)**,
so an impure generator breaks that cache **silently** — producing stale or inconsistent geometry with no
error anywhere and no way to tell from the result that anything went wrong. It is the same class of failure
as a stale technology resolution, and equally hard to trace back to its cause. Two users on different
machines getting different geometry is the same failure seen from the other end.

### 3.5 Evaluation and caching

**R6. A PCell is evaluated once per unique parameter set, never once per placement.** A 50×50 array of one
PCell evaluates once and draws 2,500 times.

This is the same requirement the geometry cache already imposes on static cells, and it must survive the
PCell addition rather than be defeated by it. R5 is what makes it safe.

### 3.6 Units

Parameters are expression values — `Real`, dimensionless as far as `expressions.md` is concerned. Geometry is
integer DBU.

**R7. PCell length parameters are SI metres, and the conversion to DBU happens in one place with one
documented rounding rule.** Two generators must never disagree about where a 2.9 mm edge lands. The
parameter editor accepts unit-suffixed entry (`2.9mm`, `115mil`) as everywhere else; the *stored* value is
metres.

### 3.7 Versioning

**R8. The contract carries a version.** A PDK declares which version it targets. This costs nothing while
there is one version and cannot be retrofitted once third-party cells exist in the wild.

`PCellContractVersion.Current`. Bump it, and record the reason here, whenever `Generate`'s signature
or its guarantees change.

| Version | Change |
|---|---|
| 1 | Initial contract (L5a). |
| 2 | Parameters widened from `double` to kinded `PCellValue`; R5 restated as determinism rather than as a file-access prohibition. Both had to land before any third party wrote a cell. |

## 4. Regeneration, and the escape hatch

Generated geometry is **derived**. Hand-editing it would produce edits that vanish on the next regeneration
— silently, which is the worst way for an editor to behave.

**R9. A PCell's generated layout is read-only in the editor. To modify it, flatten it.**

`layout-view.md` §6.1's **Flatten Hierarchy** already converts an instance into ordinary geometry, and that
is precisely the right escape hatch: flatten, then edit freely, accepting that the result no longer tracks
its parameters. The disabled editing tools say so (§6.1 R13a) rather than ignoring edits.

**Regeneration triggers** on a parameter change or a technology change, through the existing invalidation
seam that already refreshes a parent when a sub-cell is edited. No new mechanism.

## 5. Technology and substrate

**R10. A PCell resolves its technology the same way any document does** —
`workspace-and-project-tree.md` §5A.2, i.e. the *document's* workspace via the ancestor-`.cws` walk. A
schematic opened from another workspace therefore gets that workspace's substrate, not the currently open
one.

For components that need a substrate (microstrip and the like):

**R11. Layer selection is `Signal Layer` + `Ground Reference`.** From those two the stackup yields everything
a model needs: `h` from their separation, `εr` from the dielectric between them, `t` and `σ` from the signal
metal. Not "top/bottom conductor" — that phrasing does not survive contact with a four-layer board.

Both default from the stackup — topmost conductor, nearest ground-designated conductor beneath — and both are
**per-instance overridable**. Which layer you route on is *design intent*; the stackup itself is *process*.
That is the same division `layout-view.md` applies to via fill and plating.

**R12. Report when the selected layers are not the structure the model assumes.** A signal layer with ground
both above *and* below is **stripline**, and a microstrip closed form is simply wrong for it. Say so rather
than returning confident wrong numbers.

**No technology resolved:** artwork is still generatable — dimensions are parameters — but an electrical
model is not, since there is no `εr` or `h`. Generate the geometry; refuse to stamp, naming what is missing.

### 5.1 Two kinds of PCell, and two different answers

R10 as stated — *the technology follows the document* — is right for one kind of PCell and wrong for the
other. The distinction is worth drawing explicitly, because conflating them produces silently wrong physics.

| | Where its technology comes from |
|---|---|
| **Generic component** — a microstrip line, a bend | **The using design.** An MLIN dropped into an FR-4 board is FR-4; the same cell in a GaAs design is GaAs. It is a template parameterised by substrate, and this is what makes the zero-configuration workflow work. |
| **Process-specific cell** — a PDK's spiral inductor, a vendor's coupler | **Its own definition.** A cell authored against a particular process carries that process's physics with it. Reinterpreting it against the host design's substrate would not be a feature; it would be a bug. |

**R13. A cell may declare its own technology reference; absent, it is generic and follows the using
design.** The reference resolves exactly as `.clay`'s `tech_ref` does (`layout-view.md` §2.4 and
`workspace-and-project-tree.md` §5A.2), so there is no second resolution mechanism to keep in step.

This is also precisely what a **library** needs — instancing a vendor's cell must bring the vendor's process
with it — so building it here means the eventual library feature inherits the mechanism rather than
inventing one. Cross-*workspace* references remain deferred with that feature
(`workspace-and-project-tree.md` §5A.5 R37).

### 5.2 Three safeguards, because the failure mode is silent

A cell generating geometry against a different technology is genuinely useful and genuinely dangerous, and
the danger is the quiet kind.

**R14. Geometry generated against a different technology is reconciled into the host's layer vocabulary.**
A PCell emits shapes on *its* technology's layers; those land in a design whose technology may assign the
same `(layer, datatype)` keys to entirely different purposes — the collision `layout-view.md` documents,
where Drill quietly becomes Substrate. Reuse the same layer-mapping machinery cross-technology paste and
cross-technology flatten already use. Do not write a third reconciliation.

**R15. An instance whose cell uses a different technology is marked, and the difference is reported.**
The same principle `workspace-and-project-tree.md` §5A.4 applies to foreign documents: a normal, supported
state that the user must nonetheless be able to see. Nothing about the rendered geometry reveals that its
physics came from elsewhere.

**R16. The declaration belongs to the cell, never to the placement.** A user must not be able to flip a
switch on one instance and silently get different physics from an identical instance beside it. A cell is
either process-specific or generic; that is a property of what the cell *is*.

**No change to the contract itself.** Technology is already an input (§3.1) and already part of the cache key
(R6), so "which technology" is a *resolution* question rather than a contract question. R5's purity and R6's
caching hold unchanged.

## 6. What a PCell must not do

- **Depend on anything but its parameters and its technology** (R5). Not on where it is placed, not on its
  neighbours, not on the design around it.
- **Emit geometry outside its own cell.** A PCell describes one cell's contents.
- **Assume a placement transform.** It generates in cell-local coordinates; rotation, mirroring and
  magnification are applied by the instance.

The first is worth restating because it is tempting: a component that wanted to account for its neighbour —
a microstrip line adjusting for the width of whatever it connects to — would have to read the design, and
would break both R5 and R6. Junction effects belong to the junction, not to the components either side of it
(§8).

## 7. Relationship to schematic→layout

`layout-view.md` §9 places an instance per schematic component and reports those with no layout view. A PCell
simply resolves — its layout exists, it is merely computed. §9 needs no change beyond accepting the generated
answer from R1.

The pins of R3 are what §9 stamps nets onto, and what a future ratsnest draws between.

## 8. Junction discontinuities — deliberately out of scope

Microstrip junction effects (the step between differing widths, tee and cross junctions) are **not** modelled
by the components themselves.

The reasoning belongs here because it constrains the contract: a component that modelled its own junction
would need to know its neighbour's width, violating R5's purity and R6's per-parameter-set caching. **A
junction is a property of the junction**, and if it is ever modelled it belongs in the elaborator, driven by
a single switch on the analysis rather than a per-component flag — a per-component flag double-counts, since
a junction has two sides and any tie-break between them is arbitrary.

Deferred until there is a full-wave reference to validate a discontinuity model against. Documented for the
user where they will meet it, so the omission is known rather than discovered.

## 9. Open / deferred

- **The host.** How a third party authors a PCell is undecided. The deciding question is *who writes them*:
  a vendor shipping a PDK can reasonably be asked for a compiled plugin or a C# script, while a user
  authoring their own needs a language they already know — which in this domain is Python (KLayout,
  gdsfactory and most open PDK tooling). The engineering tension is that embedding CPython in .NET means a
  native dependency and a user-supplied interpreter, while pure-managed hosts avoid that and are not the
  domain's language. **The contract above is deliberately host-neutral so that more than one binding can
  exist.**
- **Trust.** A vendor PDK executing arbitrary code needs sandboxing, or at minimum an explicit trust prompt
  on first load. Not a question while all PCells are built-in.
- **Stripline and other structures.** R12 warns; a stripline component is not specified here.
- **How a process-specific cell declares its technology.** R13 establishes that it may; the storage (a
  `.ccell` field, presumably, mirroring `.clay`'s `tech_ref`) is unspecified until a cell needs it. All the
  components in the first library are generic.
- **Junction discontinuity modelling** (§8).
- **Parameter validation.** Whether a PCell can declare legal ranges for its parameters, and where such a
  violation surfaces, is unspecified.
