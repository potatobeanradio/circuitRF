# Brief 64 — operations in the document: Boolean, Fillet, Chamfer and Step in the `.c3d`

**Series:** [3D EM, fourth series](brief-em3d-60-overview.md) · **Tag:** `R-em3d64-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §6.3 (Tier B), §6.3a (precedence), §6.4 (naming)
**Area:** `src/Design/ThreeD/{C3dDocument,C3dPersistence,C3dResolver,C3dValidation,C3dElaborator,
C3dLowering,C3dDiagnostics,C3dPorts}.cs`, new `src/Design/ThreeD/C3dKernelUse.cs`,
`src/Engine/Em3d/{Em3dProblem,Em3dTessellation,Em3dFaceBoundary}.cs` (one new primitive),
`src/Ui/ViewModels/WorkspaceViewModel.ThreeD.cs` (the refusal on open), `src/Cli/{Check,ExplainEm3d,
DocumentSchema}.cs`, `docs/user/src/reference/drawing-in-3d.md`, `tests/Ui.Tests/ThreeD/`
**Depends on:** 63 · **Blocks:** 65, 66, 67, 68, 69

---

## 0. What this brief delivers

The `.c3d` learns four new `$type`s, and the elaborator turns each into an ordinary `Em3dSolid` whose
primitive the worker built. After it, a `.c3d` written **by hand** with a boolean in it simulates headlessly,
and the editor (still without a Boolean command — that is brief 66) draws it.

| `$type` | What it is | Faces it yields |
|---|---|---|
| `Boolean` | `Op` (`Subtract`, `Unite`, `Intersect`) of one `Blank` and one or more `Tools`, owned inline; **it takes the Blank's name** | the Blank's bare (`zmax`), a Tool's `<tool>:<face>`, split pieces `#n` |
| `Fillet` | a `Target` object with named edges rounded by a `Radius`; **it takes the target's name** | the target's, plus `fillet(<edge>)` |
| `Chamfer` | a `Target` object with named edges cut by `Distance` (and optionally `Distance2`); **it takes the target's name** | the target's, plus `chamfer(<edge>)` |
| `Step` | **one solid part** of a STEP file in the cell's `3d/` folder: the file, the part's occurrence path, the file's hash, a material | `face<n>` |

It also delivers the owner's rule for a machine without the kernel (D3): **a document that uses any of these
is refused on open**, with the explanation and the action; a document that uses none opens exactly as today.

The rule of the series holds (overview §1h): **a document with no kernel object elaborates byte-identically
and makes no worker call.**

---

## 1. `R-em3d64-1` — the format

**`R-em3d64-1a` Shapes.** Every new kind is a `C3dObject` — `Name`, `Placement`, `Hidden` and `Unread`
mean what they mean on a box — registered with `[JsonDerivedType]` beside the existing seven, so
`C3dObject.Kinds` (the reader's own list, and `check`'s) grows by four with nothing written twice.

```json
{ "$type": "Boolean", "Name": "lid", "Op": "Subtract",
  "Blank": { "$type": "Box", "Material": "Lid alloy",
             "Min": [0, 0, 500000], "Size": [4000000, 3000000, 250000] },
  "Tools": [ { "$type": "Cylinder", "Name": "bore", "Base": [2000000, 1500000, 400000],
               "Length": 500000, "Radius": 300000 } ] }

{ "$type": "Fillet", "Name": "lid", "Radius": { "Expr": "r_fil", "Unit": "Um" },
  "Edges": [ "bore:side|zmax" ],
  "Target": { "$type": "Boolean", "Op": "Subtract", "Blank": { "…": "…" }, "Tools": [ "…" ] } }

{ "$type": "Chamfer", "Name": "pin", "Distance": 20000,
  "Edges": [ "side|top" ], "Target": { "$type": "Cylinder", "Base": [0, 0, 0], "…": "…" } }

{ "$type": "Step", "Name": "shell", "Material": "Brass",
  "File": "sma-body.step", "Part": "0:1:1:2", "Hash": "sha256:9f2c…", "Unit": "inch",
  "SourcePath": "../../../incoming/sma-body.step" }
```

The `Step` shape is **brief 68's** (R-em3d68-1), restated here only so this brief's format section is
complete: one object per solid part, several objects may name one `File`, and the part's **assembly
transform is applied by the worker** from `Part` (the occurrence path), never written into `Placement`, which
starts at identity so an imported part lands exactly where its file puts it with no rounding. Brief 68 owns
every field's meaning; if the two briefs ever disagree, 68 wins.

**`R-em3d64-1b` Omit at default** (brief 41's rule): `Enabled` (default `true`) and `KeepTools` (default
`false`) are written only when they differ; ``Distance2` only when set; a Step object's `Role` only when set. A new document's bytes therefore say only what the user chose.

**`R-em3d64-1c` Dimensions are expressions** exactly as brief 51 made every other dimension: `Radius`,
`Distance`, `Distance2` take a DBU integer or `{ "Expr", "Unit" }`, bind through `IC3dBindable`, and resolve
in `C3dResolver` before anything reaches the worker. A negative or zero radius from an expression is refused
at resolution, as a negative size is.

**`R-em3d64-1d` The wrapper takes the name; Tools keep theirs.** A `Boolean` **is** its Blank as far as the
rest of the document is concerned: it carries the Blank's name, and the Blank stored inside it has **no**
`Name` (the key is omitted, and `check` refuses one if written). A `Fillet` or `Chamfer` wraps its Target the
same way. The rule applies recursively — the fillet above is `lid`, its target boolean is unnamed, and that
boolean's Blank is unnamed. **Tools keep their own names**, which are unique across the whole document
including nesting (`C3dValidation`'s duplicate-name rule walks into `Tools` at every depth). Dissolving or
deleting a wrapper (briefs 66, 67) gives the name back to the object inside it. The point: a port or a
boundary the user put on `lid` before subtracting anything from it is still on `lid` afterwards, by name,
with nothing rewritten.

**`R-em3d64-1e` What may be an operand.** Solids only: `Box`, `Prism`, `Cylinder`, `Polyhedron`, `Boolean`,
`Fillet`, `Chamfer`, `Step`. A `Sheet`, `Polyline` or `Wire` operand is a refusal naming it (sheet booleans
are deferred, overview §4; a wire's resolved rings are brief 50's and stay managed). Because a `Step` object is
one solid part (brief 68), it is an operand like any other, Blank or Tool.

**`R-em3d64-1f` Unknown kinds, unchanged.** An older build reading a `Boolean` refuses it with the existing
`C3dDiagnostics.UnknownKinds` sentence (*"…probably written by a later build"*). This build's scan for unknown
kinds (`C3dPersistence`'s pre-pass) now **recurses** into `Blank`, `Tools` and `Target`, so an unknown kind
nested in a boolean is named as clearly as one at the top. `FormatVersion` is **not** bumped: the new kinds
are additive and the unknown-kind refusal already says the right thing.

**`R-em3d64-1g` Placement composes.** A kernel object's `Placement` applies to its whole result, after each
operand's own (and, for a `Step` object, after the assembly transform the worker applies from `Part`). Moving a boolean moves its placement (brief 46 unchanged); moving an operand moves the
operand's. Both stay integer-exact at 90° multiples (brief 40 §1d).

## 2. `R-em3d64-2` — names, so a port or a boundary survives the operation (overview §1g)

**`R-em3d64-2a` Faces of a boolean result** are named by the worker from OCCT's history
(`Modified` / `Generated` / `IsDeleted`):
- **the Blank's faces keep their bare names** — `zmax`, `xmin` — so every port, boundary and fillet edge
  already on the Blank keeps working when something is subtracted from it;
- **a Tool's faces** are `<tool>:<face>` — `bore:side`, `cavity:xmin`. The separator is `:` because `/`
  already means an instance path (`U1/pad3`) and the air box's faces (`airbox/zmin`); `NameValidator` already
  forbids `:`, `|` and `/` in a name, so no object name can collide with any of these;
- **nesting** applies the rule at each level: a Tool that is itself a boolean contributes its own Blank's
  faces as `<tool>:<face>` and its Tools' as `<tool>:<inner-tool>:<face>`;
- **a face the operation split** is `zmax#1`, `zmax#2` (or `bore:side#1`), numbered by §2d's order, and **a
  reference to `zmax` covers every piece** — the fold rule brief 40 §1e already gives polyhedra and
  `Em3dFaceBoundary` already honours (*"split faces share one name, so a boundary covers every piece"*).

**`R-em3d64-2b` Edges** are named **relative to the object** by the two faces they separate, ordinal-sorted,
joined by `|`: `xmax|zmax`, or with a Tool's face `bore:side|zmax`. Seam edges (a face meeting itself, as a
cylinder's side does) and degenerate edges are not named and cannot be filleted. Where two faces share more
than one edge, each takes a **third `|` field, its number** — `trench:side|zmax|1`, `trench:side|zmax|2` —
not `#n`, which a split face already uses (brief 61 Q7 / overview §1g: `x|zmax#2` was ambiguous between a
repeated edge and an edge of piece 2); **brief 67 pins that order** (and §2d is its default until it does).

**`R-em3d64-2c` Faces a fillet or chamfer creates** are `fillet(<edge>)` / `chamfer(<edge>)` with the edge's
name — `fillet(xmax|zmax)`. Faces the operation trims keep their names. The worker names from **every edge of
each contour** OCCT builds (`MakeFillet::Edge(contour, i)`), not only the edges the object lists: a tangent
chain propagates, and brief 61 Q7 left 7 of 18 faces unnamed when it named from the listed edge alone.

**`R-em3d64-2d` The `#n` order, pinned.** Pieces of a split face and edges sharing a face pair are numbered
by their centroid in the **object's own frame** (before its `Placement`), compared x, then y, then z, each
rounded to 1 nm. Own frame, so moving or rotating the object never renumbers; rounded, so the order does not
depend on the last bit of a double. The worker computes it; a test builds a slab split in three and a box
with a doubled edge and pins the numbering.

**`R-em3d64-2e` A STEP part's faces** are `face<n>`, `n` from 1 in the part's own topological order as the
worker explores it; as a Tool, `<part-object>:face<n>` like any Tool's faces, and as a Blank, bare. They mean one thing **for
the recorded `Hash`**. When a file is replaced through *Reload from Source*, references are re-matched **by
the face's geometry** (surface kind, area, centroid, normal), never by its number, and a reference with no
single match refuses the reload (brief 68 §5). A file whose bytes changed **outside** circuitRF is a `check`
error naming *Reload from Source* (§6a) — its `face<n>` numbers no longer mean what the references assumed.

**`R-em3d64-2f` References resolve the same whether the operation is enabled or not.** A face boundary or a
port contact states an object and a face (`Em3dFaceBoundary(Object, Face)`). With §1d and §2a together:
- `Object = "lid", Face = "zmax"` is the Blank's top — on the boolean `lid` while it is enabled, on the
  standalone Blank elaborated under the name `lid` while it is disabled (§4a);
- `Object = "lid", Face = "bore:side"` is the bore's wall *in the result*. While the boolean is disabled that
  face does not exist on `lid` — it is `Object = "bore", Face = "side"` — so the reference resolves to the
  standalone `bore` by rewriting `<tool>:<face>` to `(<tool>, <face>)` at resolution, never in the file.
A reference therefore survives the Enabled toggle in both directions with nothing rewritten, and one whose
face no longer exists is a refusal naming the object and the face (em-3d.md §6.4's rule).

## 3. `R-em3d64-3` — elaboration: resolved numbers in, one `Em3dSolid` out

**`R-em3d64-3a` The path.** For each top-level kernel object, the elaborator: resolves it (`C3dResolver`),
builds the canonical tree (`GeometryKernelTree`, brief 63 §5), asks `GeometryKernel` to build it (a cache
hit costs nothing), and lowers the answer to

```csharp
public sealed record Em3dShapeSolid(
    ReadOnlyMemory<byte> Brep, string BrepHash,              // opaque; only the worker reads it
    Em3dTriangleMesh Display,                                // the display tessellation, metres
    IReadOnlyList<Em3dShapeFace> Faces,                      // name, kind, tight box (m), min radius (m), triangle range
    IReadOnlyList<Em3dShapeEdge> Edges) : Em3dPrimitive;     // name, the two faces, curve kind, min radius, polyline
```

carried by an ordinary `Em3dSolid(Name, Material, Role, Primitive, Order)` — one per top-level kernel object,
a `Step` object included (it is one part). It is a **primitive**, not a
second kind of solid, so `Em3dPrecedence`, `Order`, materials and the provenance map need no new case. The
Engine never parses `Brep`; `Em3dTessellation` returns `Display` for it (each triangle's `Face` is the face
table index, as brief 42 made it for every primitive), which is what makes `render`'s sections, the viewer
and picking work with no further change. Equality is by `BrepHash` plus the display deflection, so the
scene's tessellation cache (keyed by primitive) hits across re-elaborations.

**`R-em3d64-3b` Material and role come from the Blank** (overview §1i). A `Boolean`, `Fillet` or `Chamfer`
states no `Material` or `Role` of its own — setting one is a `check` error pointing at the Blank (or
Target). A `Unite` whose Tools name other materials elaborates with the Blank's and the elaboration note says
so, naming each replaced material (D11). A `Step` object states its own `Material` and `Role` like a box; one
with no material is ignored with the existing `C3dElaborator.NoMaterialWarning` sentence, as any object with
no material is (em-3d.md §6.4).

**`R-em3d64-3c` `KeepTools`.** With `Op = Subtract` and `KeepTools = true`, each Tool also elaborates as its
own solid, **immediately after** the result in construction order, with its own material — the direct way
to state a dielectric fill in a bore (overview §1i; em-3d.md §6.3a's closing case). A kept Tool with no
material is the ordinary no-material warning.

**`R-em3d64-3d` A kernel refusal is the object's, not the document's — and it is not an undo.** A build that
fails (a fillet too large, a boolean with an empty result, a worker crash) reports **that object** with the
client's sentence (brief 63 §4d, §7a) and leaves it out of the problem; the rest elaborates. The edit that
caused it **stays in the document**: the object's tree row is marked refused (brief 66's rule) and the user
fixes it or undoes it themselves. `em` refuses the run with the list — a problem missing a solid is not
something to solve silently.

**`R-em3d64-3e` Counters.** `C3dElaborator.KernelTreesBuilt` (trees handed to the worker) beside the existing
`ObjectsElaborated`. An edit that touches one boolean raises it by one; an unrelated edit, undo of an
unrelated edit, and re-elaboration of an unchanged document raise it by zero.

## 4. `R-em3d64-4` — Enabled: as if the operation were not there (overview §1f)

**`R-em3d64-4a` A disabled `Boolean`** elaborates its Blank **under the boolean's name**, then each Tool
under its own, as independent top-level objects **at the boolean's position** in construction order, with
their own materials and faces —
**and makes no worker call**, since the operands are managed objects (unless an operand is itself a kernel
object). A Tool with no material is then the ordinary no-material warning: a cutter the user drew as a
bare shape is correctly left out of the solve.

**`R-em3d64-4b` A disabled `Fillet` or `Chamfer`** elaborates its Target, under the wrapper's name, exactly as
if unwrapped.

**`R-em3d64-4c` The toggle is geometry.** It adds an undo entry and marks the document dirty (unlike the
display unit, brief 40 §1d, which moves nothing).

## 5. `R-em3d64-5` — without the kernel: refused on open, with the reason and the action (D3)

**`R-em3d64-5a` What "uses the kernel" means — one rule, by kind, and deliberately so.**
`C3dKernelUse.Of(document)` lists every `Boolean`, `Fillet`, `Chamfer` and `Step`, **at any depth and whether
or not it is enabled**. A disabled boolean of managed operands *could* elaborate without the kernel (§4a), and
this rule refuses it anyway, on purpose (overview §1d): a document that opens or refuses depending on a
checkbox is harder to explain than one rule by kind, and a document opened because its one boolean happened
to be disabled would then fail the moment the user re-enabled it, mid-session, with work in the editor. A
test pins the disabled case as a refusal so nobody "optimises" it later.

**`R-em3d64-5b` The editor refuses to open it.** `WorkspaceViewModel.OpenOrActivateC3dEditor` asks
`C3dKernelUse.Of` after `C3dPersistence.LoadFromFile` succeeds; if the list is non-empty and
`GeometryKernel.Capability` is not available, it opens **nothing** and shows a dialog, plus the same line in
the Messages panel:

> **This 3D view cannot be opened.** 'lid' (a Boolean) and 'shell' (a Step part) need the geometry
> kernel, which this installation does not have: the worker was not found at …/geometry-kernel/.
> Reinstall circuitRF to restore it.
> [Open Settings ▸ 3D EM] [Close]

The middle sentence is `GeometryKernel.NeedsKernel(...)` (brief 63 §3b); only the object list is this
brief's. More than five objects are listed as five and *"and N more"*.

**`R-em3d64-5c` Everything else is unchanged.** A `.c3d` with no kernel object opens and edits normally;
kernel commands are disabled with the capability's tooltip (briefs 66–69).

**`R-em3d64-5d` Instances.** A parent `.c3d` that has no kernel object of its own **opens**, even when a
child it instances uses one: the child's instance elaborates to a refusal naming the child and the reason,
draws nothing, and Simulate is refused with the same sentence. Refusing the parent would lock a user out of
a document that is not itself kernel data. (The overview's D3 speaks of the document itself; this is the
reading for hierarchy, and the owner may overrule it.)

**`R-em3d64-5e` Headless.** `check` reads the document (it never needed the kernel to *read*), reports one
**error** finding per kernel object with the same sentence, and exits 1. `em` refuses before writing any
mesh or grid. `explain` still explains everything that does not need the kernel.

## 6. `R-em3d64-6` — `check` and `explain`

**`R-em3d64-6a` `check` gains rules, in `C3dValidation`** (no rule of its own in `src/Cli`, the rule
`cli.md` §10 states): exactly one Blank and at least one Tool; operand kinds (§1e); no `Material`/`Role` on
an operation (§3b); no `Name` on a Blank or a wrapped Target, and Tool names unique at every depth (§1d); a Step `File` that exists, lies inside the cell's
`3d/` folder, and matches `Hash` (a mismatch is an **error** naming the file and *Reload from Source*,
as brief 68 §6 words it — the file was edited outside circuitRF and its `face<n>` numbers no longer mean what
the references assumed); every edge and face reference resolves — which
needs the kernel, so with it absent this rule reports the capability instead. Build refusals from the worker
(§3d) are findings too, so `check` fails a document `em` would refuse.

**`R-em3d64-6b` `explain`** (`ExplainEm3d`) gains a *Geometry kernel* block: available or not, where it was
found (`HowFound`, as the solver rows say theirs), the OCCT version; and per kernel object: its operands as
an indented tree, whether its build was a cache hit or built, its face and edge counts, the smallest radius
of curvature on it, and any notes (healing, the replaced materials of D11). `--json` carries the same.

## 7. `R-em3d64-7` — the format contract

**`R-em3d64-7a`** `src/Cli/DocumentSchema.cs`'s `3d-view` topic — what `circuitrf reference 3d-view` and the
MCP server's `reference` tool serve — documents all four kinds with the JSON of §1a, the naming of §2, the
operand rule, `Enabled`, `KeepTools`, and the refusal when the kernel is absent. The user reference page
`docs/user/src/reference/drawing-in-3d.md` gets the same (source only; DocGen is not run per brief).

**`R-em3d64-7b`** A test writes each §1a example to disk, runs `check` on it, and expects zero errors with the
kernel and exactly the §5e error without — so the page cannot drift from what the reader accepts.

## 8. Gates

1. **Byte identity.** Every existing `.c3d` in `tests/` and `examples/` elaborates to the same problem, and
   the same Palace and openEMS bytes, as before this brief; `GeometryKernel.RequestsSent` stays **0**
   throughout.
2. **Round trip.** Each §1a example reads and writes back byte-identically; defaults are omitted.
3. **Unknown nested kind.** A `Boolean` whose Tool is `"$type": "Torus"` is refused naming *"object 'x' (a
   "Torus")"* inside it.
4. **Names.** `[KernelFact]` — box minus cylinder: the result has `zmax` (one piece, with a hole), `bore:side`,
   and the box's other five faces by their bare names; a nested Tool's faces are `outer:inner:face`; a slab
   split in three gives `zmax#1…#3` in §2d's order; moving the whole boolean renames nothing; the Blank inside
   a written boolean carries no `Name`, and one written with a `Name` is a `check` error.
5. **References through the toggle.** A face boundary on (`lid`, `zmax`) and one on (`lid`, `bore:side`) land
   on the boolean's faces enabled, and on the standalone `lid`'s `zmax` and `bore`'s `side` disabled; the
   file is byte-identical after toggling twice. A port placed on `lid`'s `zmax` **before** the subtraction is
   still on `zmax` after it.
6. **Disabled costs nothing.** Disabling a boolean of managed operands: `KernelTreesBuilt` + 0,
   `RequestsSent` + 0.
7. **Precedence.** `[KernelFact]` — a Subtract whose Blank is a dielectric and whose kept Tool is a
   conductor lowers with the conductor above the dielectric (`Em3dPrecedence` unchanged).
8. **Refusal on open.** With a fake absent capability, opening a kernel-using document opens no editor and
   raises the §5b message listing the objects — **including a document whose only boolean is disabled**
   (§5a); a document with none opens.
9. **`check`.** Exit 1 with the §5e finding when absent; exit 0 with it present on the §1a examples.

## 9. Owner check list (Debug build)

1. Hand-write the §1a boolean into a copy of the 3D Package example; open it; see the bore through the lid.
2. Toggle `"Enabled": false` in the file; reopen; see the cylinder standing in the lid.
3. Hide the geometry kernel (rename its folder); open the file; read the refusal and follow its action.

## 10. Scope

- **No new command.** Creating these objects from the editor is briefs 66–68; this brief makes them real.
- **No direct face or vertex edit on a kernel result** (D13) — brief 66 words the refusal.
- **No change to any existing lowering** (gate 1). The Palace and openEMS lowerings of `Em3dShapeSolid` are
  brief 65; until it lands, a problem containing one is refused by both backends with *"…needs brief 65"*
  replaced by the real refusal wording: *"'<name>' is a kernel solid, which this build cannot yet write for
  <solver>."*
- Findings go in `src/Design/RESOLVED.md`, never `CLAUDE.md`.
