# Brief — 3D EM, fourth series: the geometry kernel (booleans, fillets, chamfers, STEP)

**Status:** Briefed, not built · **Date:** 2026-09-27 · **D1 proposed, owner to confirm** (§3) ·
**Design note:** [`docs/design/em-3d.md`](../design/em-3d.md) rev 6 (Draft) — phase **F4b**, §6.2 Route B, §6.4, §6.5
**Previous series:** [`brief-em3d-0-overview.md`](brief-em3d-0-overview.md) (1–10),
[`brief-em3d-20-overview.md`](brief-em3d-20-overview.md) (20–31) and
[`brief-em3d-40-overview.md`](brief-em3d-40-overview.md) (40–53), all built
**Area:** `tools/geometry-worker/` (new: C++, MIT, links OpenCASCADE), `packaging/` (all three scripts),
`THIRD-PARTY-NOTICES.md` + `licenses/`, `src/Design/ThreeD/Occ/` (new: the managed client),
`src/Design/ThreeD/` (three new object kinds), `src/Engine/Em3d/` (one new primitive),
`src/Design/Em3d/{GmshGeoWriter,CsxcadWriter}.cs`, `src/Render/Scene3D/` (edges), `src/Ui/ThreeD/`
(the Boolean dialog, Edge mode, Fillet/Chamfer, Import/Export STEP), `src/Cli/` (`convert`, `check`,
`explain`), `examples/`, `tests/`
**Requirement tag:** `R-em3d<n>-<m>` as before. This series is numbered **60–70**.

---

## 0. The short answer

Series 3 built a managed modeller: boxes, prisms, sheets, cylinders and planar polyhedra, drawn, edited
and simulated. It deliberately stopped short of three things (brief 40 §1b): **booleans**, **fillets and
chamfers on curved edges**, and **STEP import**. This series adds all three, plus **STEP export**, by
shipping **OpenCASCADE Technology (OCCT)** inside circuitRF, behind a separate worker process. When it is
done:

- **Booleans.** Select two or more objects, right-click, *Boolean ▸ Subtract / Unite / Intersect…*. A small
  dialog shows the selection with a role per row: the **first-selected object is the Tool**, one other
  row is the **Blank**, and either can be changed before OK. The result previews in the viewport before
  it is committed.
- **Operations are visible in the object tree.** A boolean is a tree node with its operands beneath it; a
  fillet or chamfer is a row under the solid it rounds. Clicking one shows it in the Properties inspector,
  with an **Enabled** toggle. Disabling an operation shows the model without it; nothing is lost.
- **Fillets and chamfers**, on straight **and curved** edges, from a new **Edge** selection mode.
- **STEP import** of a connector, a package or a housing into a `.c3d`, with its part names, its colours
  mapped to materials, and its units converted.
- **STEP export** of a `.c3d` (and of a layout's 3D form) for a mechanical colleague: what the solver gets,
  with names and material colours.
- **Both solvers take the result, and say what they cannot respect.** Palace gets the exact curved
  surfaces; openEMS gets a staircase, and the run says, per object, how coarsely.

### The one rule this series adds

> **One kernel, one answer.** Whatever the editor draws, Palace meshes and openEMS grids for a boolean, a
> fillet or a STEP part comes out of **one** OCCT evaluation, in the worker, from **resolved numbers only**.
> Nothing re-derives it: not the `.geo` script, not the CSXCAD writer, not the viewer.

It is series 3's rule — *what the editor shows is what the solver gets* — carried across a kernel boundary.

---

## 1. Things that are not obvious, resolved here once

### 1a. OpenCASCADE, not our own kernel

The managed kernel (`src/Design/ThreeD/Kernel/`) is a **polyhedral** kernel: planar faces, integer DBU,
Int128-exact normals. It is the right tool for what it does and it stays (§1h). It cannot grow into what
this series needs without becoming a different program:

| Need | What it takes | Own kernel? |
|---|---|---|
| Booleans of **planar** solids | robust polygon clipping in 3D, coplanar-face handling, exact predicates | feasible, months — and it still would not do a cylinder |
| Booleans involving **curved** faces (a via bore, a coax pin, a round cavity) | surface–surface intersection of analytic and NURBS surfaces, tolerant topology | years |
| **Fillets and chamfers on curved edges** | rolling-ball blends, blend-to-blend corner patches, tangent-chain propagation | years; this is the hardest part of any kernel |
| **STEP import** | the ISO 10303 AP203/AP214/AP242 schemas, entity-by-entity mapping, **shape healing** of other systems' tolerance mistakes | years; healing never ends |
| **STEP export** | the same schemas, written | months, and only worth it with the rest |

OCCT has had all of these in production for decades. It is open source, it builds on every platform
circuitRF ships to, and — a fact that decides a lot — **it is already in circuitRF's Palace pipeline**:
Gmsh uses OCCT as its geometry kernel (em-3d.md §6.2 Route A). Using OCCT in circuitRF as well means the
shape circuitRF shows and the shape Gmsh meshes come from the same kernel family.

**Alternatives looked at:**
- **An open mesh-boolean library** (manifold-style, permissive licence): fast and robust booleans on
  triangle meshes. No exact curved surfaces, no fillets, no STEP. It would give booleans only, faceted, and
  Palace would lose the curved surface it can otherwise mesh exactly.
- **An open implicit/CSG kernel:** no B-rep, no STEP.
- **Commercial kernels:** closed, per-seat licensing, incompatible with an MIT distribution.
- **Anything GPL** (some computational-geometry libraries' exact polyhedral booleans): excluded by
  CLAUDE.md.

So: **OCCT.** The owner's guess was right.

### 1b. Shipping it: the licence allows it, and it is the same class of obligation circuitRF already carries

OCCT 6.7.0 and later is **LGPL-2.1 only, with the Open CASCADE Exception 1.0**. Current stable is
**8.0.1** (2026-07-30); the spike (brief 61) pins the exact version.

What that licence allows, and what it asks:

| | |
|---|---|
| Ship OCCT inside circuitRF's installers | **Yes**, as **unmodified shared libraries** the user can replace |
| circuitRF stays MIT | **Yes.** An LGPL library used through its interface does not reach the program using it. The exception goes further: object code that incorporates material from OCCT's **headers** (inline functions, templates — unavoidable in C++) may be distributed on terms of our choosing, *provided circuitRF gives prominent notice that it uses OCCT* |
| Obligations | Ship the LGPL-2.1 text and the exception text; give **prominent notice** (About box and `THIRD-PARTY-NOTICES.md`); make the **corresponding source** of the exact OCCT build available — by a **written offer** in `THIRD-PARTY-NOTICES.md`, not a published archive (owner, 2026-09-27, while [OCCT#1564](https://github.com/Open-Cascade-SAS/OCCT/issues/1564) is open; brief 62 §6c), plus the build recipe; **link dynamically**, so a user can substitute a modified OCCT; do not forbid reverse engineering for that purpose (MIT does not) |
| Static linking | **Not done.** It would require shipping relinkable object files. Shared libraries make the obligation trivial |
| macOS signing | Replacing a signed dylib breaks the bundle's signature; the user can re-sign it. **LGPL-2.1 has no "installation information" clause** (that is GPLv3/LGPLv3), so this is compliant |

**circuitRF already carries an LGPL-2.1 component:** `THIRD-PARTY-NOTICES.md` §1 is CSparse.NET. The
mechanism differs — CSparse is bundled into the single-file host and its relinking right is met by
publishing complete source, while OCCT ships as a separate folder of replaceable libraries — but its
"what this means if you redistribute" paragraph is the pattern OCCT's entry follows.

**Why Palace, openEMS and Gmsh are different** (em-3d.md §7.1): Palace's default build carries ParMETIS,
whose terms allow commercial use for evaluation only; openEMS and Gmsh are **GPL**, which would put a
source-offer obligation on every circuitRF binary and, for an in-process use, reach circuitRF's own code.
OCCT is neither. The owner's preference — ship it — is available here and was not there.

**It is a native dependency** in CLAUDE.md's sense (*ask before*). The owner has asked for it; D1 (§3)
records the confirmation, and CLAUDE.md's prior "agreed in principle" (PRD §17, em-3d.md §6.2) becomes a
decision.

### 1c. Out of process — a worker, not a library

OCCT is C++ with no maintained cross-platform .NET binding. There are two ways in: a C shim loaded with
P/Invoke, or a separate program. **This series uses a separate program**, `tools/geometry-worker`, spoken
to over stdin/stdout, as em-3d.md §6.2 Route B already specified. It follows `tools/senior-worker`'s
and `tools/osdi-worker`'s **build and packaging** pattern (own script, `ensure-built`, shipped in the
publish tree), not their runtime arrangement — it is C++ and runs natively on every platform:

- **A crash costs one operation, not the user's unsaved work.** A boolean or a fillet on degenerate input
  can fault inside any kernel. In process that kills the editor; out of process the worker dies, circuitRF
  refuses that one operation with a sentence, restarts the worker, and the document is untouched.
- **A runaway operation can be stopped.** A fillet that takes a minute is cancelled by killing a process,
  which always works; nothing has to cooperate.
- **The licence boundary is a file boundary.** The worker's own source is MIT; it links OCCT's shared
  libraries; circuitRF's managed code references neither. `tests/Firewall.Tests` holds that line the way
  `SolverBoundaryTests` holds Gmsh's — with one inversion: the notices gate there requires Palace, Gmsh
  and openEMS to be **absent** from `THIRD-PARTY-NOTICES.md`, while OCCT's entry must be **present**. That
  is a separate check beside the existing one (brief 62), not a change to it.
- **Headless is free.** `check`, `em`, `convert` and the MCP server run the same worker with no display.

The cost is IPC: a request and a tessellation cross a pipe. For the sizes a `.c3d` holds that is well under
a frame, and brief 63 makes it a counter, not a hope.

### 1d. Shipped, but "absent" still exists — so the greyed-out path is not moot

With OCCT in every installer, a user never installs anything. The owner's rules for a machine *without* it
still matter, because three machines genuinely lack it:

1. **A developer build.** `dotnet build` never *builds OCCT* — it is an hour-scale C++ build. Following
   `tools/senior-worker`'s pattern (an `ensure-built` step in `CircuitRF.Ui.csproj` that only warns on
   failure), `dotnet build` compiles the small worker and copies OCCT from a per-user cache **if the cache
   is there**, and warns otherwise (brief 62). Running from source with no cache is the ordinary state of
   a fresh clone — and of CI.
2. **A platform the spike cannot ship.** Windows **x86** is the likely one (brief 61 decides; D2).
3. **A damaged install** — the worker or a library deleted, or a worker that fails its start-up handshake.

So the owner's rules are built, once, in the client (brief 63) and the document (brief 64):

- **Editing without the kernel works wherever it can.** Every series-3 command still works; the managed
  kernel needs nothing.
- **Kernel commands are shown disabled**, never hidden, each with a tooltip naming what enables it:
  *"Needs the geometry kernel, which this installation does not have. <action>"*. The action depends on
  why (reinstall circuitRF; build `tools/geometry-worker`; this platform does not include it).
- **A `.c3d` that uses a kernel feature is refused on open**, with the explanation and the action (owner,
  2026-09-27). A `.c3d` that uses none opens normally. *"Uses a kernel feature"* means it contains a
  Boolean, Fillet, Chamfer or Step object anywhere, **enabled or not**. This is stricter than necessary on
  purpose: a disabled boolean or fillet *could* elaborate without the kernel, but a document that opens
  or refuses depending on a checkbox is harder to explain than one rule by kind, and re-enabling it would
  then fail mid-session. Brief 64 states the rule as deliberate.
  This **supersedes** brief 40 §1b's recorded constraint (*"draws the operands, marked, with a banner"*):
  that was written for a user-installed kernel, where "absent" was common and a refusal would lock users
  out of their own files; with the kernel shipped, absence is a fault, and a refusal that says how to fix
  it is the honest answer.
- **Headless, the same:** `check` reports the capability and refuses a kernel-using document with the same
  sentence and exit 1; `em` refuses before any mesh is written.
- **Tests that need the worker** skip **with a reason** when it is absent (the `FixtureFact` pattern
  `RfCore.Tests` already uses), so a fresh clone is green and says what it did not test.

### 1e. Do Palace and openEMS respect fillets and chamfers? Palace yes, openEMS approximately

Neither solver models geometry; each consumes a discretisation of it. What survives depends on that
discretisation:

- **Palace (FEM, tetrahedra from Gmsh): respected.** circuitRF already writes **second-order curved
  elements** (`GmshGeoWriter`: `Mesh.ElementOrder = 2`, `HighOrderOptimize = 2`), so a fillet is meshed as
  a curved surface, not a facet fan, and a chamfer is exact. The limit is the **initial mesh**: Palace's
  adaptive refinement splits elements but does not snap new nodes back onto the CAD surface, so a fillet
  whose radius is small against the local mesh size is carried by a few curved elements and no more.
  **Curvature sizing already exists**: `GmshGeoWriter` writes `Mesh.MeshSizeFromCurvature` for every
  problem (12 elements per full turn) with no minimum size, so an imported curved face is resolved with no
  new rule — and adding a floor would move every existing mesh. What brief 65 warns about is therefore
  **cost** (a small fillet that dominates the element count) and **loss-model validity** at small radii
  (the flat-surface impedance, F0 Q6), extending the existing `Em3dRunService.ThinRoundConductors` note
  to kernel faces rather than adding a second rule.
- **openEMS (FDTD, a Cartesian grid): approximated.** Every non-axis-aligned surface is **staircased** to
  the grid — this is FDTD's nature, not a missing feature. A fillet of radius *r* is represented by about
  *r / Δ* cells; below about one cell it is **not represented at all**, and a chamfer becomes a step. The
  kernel's tessellation reaches openEMS as CSXCAD's polyhedron primitive (F0 Q8: STL and PLY both read),
  and the run carries a **per-object note** giving the cell count across the smallest rounded feature. The
  existing oblique-face warning (`FdtdGrid`, brief 42) is the pattern.
- **Both:** a **STEP** part is fine in both — exact in Palace, staircased in openEMS, with the same notes.

**The owner's requirement — warn when a feature will not be respected — is brief 65**, surfaced three
places: the run's notes, `check`, and the editor's Setups panel before Simulate.

### 1f. Where a boolean lives: a small tree per object, not a global history

Series 3 made the `.c3d` a **flat, ordered list of objects** (brief 40 §1f) and reserved *"a boolean is an
object whose operands are other objects"*. That is what is built:

- **A `Boolean` object** holds `Op` (`Subtract`, `Unite`, `Intersect`), one **`Blank`** object and one or
  more **`Tools`**, owned inline — each operand is an ordinary object (a box, a cylinder, another boolean,
  a Step part) with its own name, placement and, for a tool, its own material. `Enabled` and `KeepTools`
  are its switches.
- **A `Fillet` and a `Chamfer`** are objects that **wrap** one target object and list the target's edges by
  **name** (§1g), with a radius (or distances) that is an expression like any other dimension (brief 51).
- **A `Step` object** references a STEP file and carries the part-to-material map (brief 68).

The top-level list keeps its meaning — construction order, `Em3dPrecedence` — and each top-level entry is a
**small tree** evaluated bottom-up by the worker. There is no global timeline to replay: an edit re-evaluates
only the tree it touched, and a tree whose resolved inputs are unchanged is not re-evaluated at all (brief
63's cache).

**Disabled means "as if the operation were not there":**
- a disabled `Boolean` elaborates its Blank and its Tools as **ordinary independent objects** — in the tree
  they stay under the boolean node, drawn and editable, and the solver sees each one;
- a disabled `Fillet`/`Chamfer` elaborates its target unrounded.

That is what the "Enable" toggle means in the tools the owner has used, and it makes a toggle a cheap A/B
experiment ("does this fillet change S21?").

### 1g. Names survive a boolean, and edges get names too

§6.4's rule — *never store a face index* — holds through a kernel operation:

- **The wrapper takes the name.** A `Boolean` takes its **Blank's** name, and the Blank stored inside it
  has none of its own; a `Fillet` or `Chamfer` takes its target's name the same way. Tools keep their own
  names, which stay unique across the document. Dissolving the wrapper hands the name back. So subtracting
  a cavity from `lid` leaves an object called `lid`, and every reference to `lid` still lands, **enabled
  or disabled** (a disabled boolean elaborates its Blank under the boolean's name).
- **Faces of a boolean result** are named from OCCT's own history (`Modified` / `Generated` / `IsDeleted`).
  **The Blank's faces keep their bare names** — the lid's top is still `zmax` — so an existing port or
  boundary on the Blank survives being subtracted from. A face from a Tool is `<tool>:<face>`
  (`cavity:xmin`). A face the operation split in two is `zmax#1`, `zmax#2`, and a reference to `zmax` goes
  to **all** its pieces (brief 40 §1e's fold rule). A face that no longer exists is a refusal naming it.
  **`:` and not `/`**: `/` already means an instance path (`U1/pad3`) and an air-box face
  (`airbox/zmin`). `NameValidator` forbids `:`, `|` and `/` in names, so no separator here can collide
  with a name.
- **Edges** are named, relative to their object, by the **two faces they separate**, sorted and joined by
  `|`: `xmax|zmax`, or `cavity:xmin|zmax`. Where two faces share more than one edge the name takes a
  **third field, the edge's number**: `trench:side|zmax|1`, `trench:side|zmax|2`. *(Corrected by brief 61
  Q7: this was a `#n` suffix, which collides with a split face's — `x|zmax#2` would mean the second edge
  between `x` and `zmax` in one document and the edge between `x` and piece 2 of `zmax` in another, so a
  stored edge could silently land elsewhere after an edit split `zmax`. `|` is forbidden in names and no
  face name is a bare number, so the third field is unambiguous.)* Split pieces and repeated edges are
  numbered in a **geometric** order (brief 64 §2d), never in OCCT's list order, which Q7 measured to differ.
  A seam edge (a face meeting itself, as a cylinder's side does) is not named. A fillet stores edge names,
  so resizing the box keeps the fillet on the same edge.
- **Faces a fillet creates** are `fillet(<edge>)`; a chamfer's, `chamfer(<edge>)` — for **every** edge
  OCCT rounds, including the tangent edges it propagates to from the ones listed. *(Brief 61 Q7: naming
  from the listed edge alone left 7 of 18 faces of a tangent-chain fillet unnamed; the worker names from
  each contour's edges, `MakeFillet::Edge(contour, i)`.)*
- **A STEP part's faces** are `face<n>` in the file's topological order, valid for the file's **content
  hash**, which the object records. A changed file with references into it is re-validated, and a reference
  that no longer lands is a refusal (brief 68).

### 1h. The managed kernel stays the editing path

Brief 40 §1b said F4b *adds operations and does not take over* the managed ones. That holds:

- boxes, prisms, sheets, cylinders, polyhedra, wires and everything series 3 built **elaborate exactly as
  before, with no worker call**. A document with no kernel object writes **byte-identical** Palace and
  openEMS inputs (§6, gate 3);
- face and vertex edits (brief 47) apply to managed objects — **including an operand inside a boolean**,
  which is then re-evaluated at commit;
- **a kernel-made solid is not face- or vertex-edited directly.** Its faces are selectable (for ports,
  boundaries, measurement and snapping), but *Move Face* on a boolean result is refused with a sentence
  that says what to edit instead (the operand, or the operation's parameters). Editing OCCT B-reps
  directly is a different feature and is not in this series;
- **a drag stays a preview**: moving an operand during a drag moves its drawn batch only; the worker is
  called **once, on release** (brief 40 §1b's counter, restated in brief 63).

### 1i. The neutral problem grows one record, and the Engine stays kernel-free

`Em3dProblem` (the numeric layer) gains **`Em3dShapeSolid`**, a new **`Em3dPrimitive`** carried by an
ordinary `Em3dSolid` — so precedence, construction order and provenance need no new case. It holds an
opaque B-rep (bytes, content-hashed), the kernel's tessellation of it (for openEMS and the viewer), and its
**face table** — name, tight bounding box, surface kind (plane / cylinder / cone / sphere / torus /
free-form), and triangle range. The Engine never interprets the B-rep bytes; only the worker does.

- **Palace:** the `.geo` imports the B-rep with `ShapeFromFile`, and recovers every named face by the same
  **tight bounding-box query** it already uses (em-3d.md §6.2, F0 Q5, `OCCBoundsUseStl`) — now against the
  worker's per-face boxes. Whether Gmsh's own embedded OCCT reads our B-rep files, or whether the hand-off
  must be STEP, is **spike question Q4** (brief 61); D12 records the answer.
- **openEMS:** the tessellation, at a deflection derived from the grid, written as PLY/STL for CSXCAD's
  `PolyhedronReader` — checked to exist before the run (F0 Q8: a missing file is silently skipped).
  **openEMS's polyhedron drops grid nodes lying exactly on its faces** (brief 42 measured 518 grid edges
  falling to 78), which a kernel solid's flat faces meet as any polyhedron's do; brief 65 offsets those
  faces outward by 10⁻⁴ of a cell, gated by one short openEMS run.
- **FDTD grid:** a kernel solid contributes its axis-aligned planar faces' lines and its extremes; curved
  faces are staircased and **noted** (§1e).
- **Precedence:** a boolean result has **the Blank's material and role**; `Em3dPrecedence` is unchanged.
  A `Unite` of different materials is allowed and the dialog and the elaboration note both say the tools'
  materials are replaced. `KeepTools` (subtract a dielectric slug from a lid and keep the slug as a fill)
  is how §6.3a's "a dielectric fill inside an air bore" case is finally stated directly.

### 1j. Performance: the worker is never on the frame path

- **Tessellation is cached** by the hash of each tree's resolved inputs; undo/redo, re-opening a document
  and an unrelated edit make **zero** worker calls for an unchanged tree (counter).
- **Preview is asynchronous.** The Boolean and Fillet dialogs request a preview; the viewport keeps
  rendering; the preview appears when it arrives, and a newer request supersedes an older one (only the
  latest is drawn).
- **Display tessellation** uses a deflection relative to the object's size and an angular deflection;
  **openEMS's** tessellation is requested separately at its own, grid-derived deflection, so a fine
  display never costs FDTD memory and a coarse display never costs FDTD accuracy.

### 1k. Nothing in this series can be seen from an agent's session

As in series 2 and 3: every gate is a counter or a headless check, never a timing and never a pixel; every
brief with a visible surface ends with an **owner check list** for the **Debug** build; every completion
note says pixels were not seen. The worker itself **is** fully testable headlessly, and most gates live
there.

---

## 2. The briefs

| # | Brief | Delivers | Depends on |
|---|---|---|---|
| 61 | [the kernel spike](brief-em3d-61-kernel-spike.md) | OCCT pinned; the module set; build time and **size per RID**; which RIDs ship (D2); booleans on coincident DBU faces; fillet on a curved edge; STEP round trip; Gmsh reads our B-rep (D12); openEMS reads our tessellation; licence checklist verified against the built tree. Findings note | — |
| 62 | [the worker, built and shipped](brief-em3d-62-geometry-worker-and-shipping.md) | `tools/geometry-worker` (C++, MIT); the pinned OCCT recipe and its build cache; the three packaging scripts ship it; `CliSmoke` exercises it; `THIRD-PARTY-NOTICES.md`, `licenses/`, the written offer of OCCT source (no archive published); About's notice; firewall tests | 61 |
| 63 | [the managed client](brief-em3d-63-kernel-client.md) | `src/Design/ThreeD/Occ/`: discovery, handshake and version check, the protocol, crash/timeout/cancel, the cache, async preview; the **capability** (§1d) every UI and CLI surface reads; `KernelFact` test attribute | 62 |
| 64 | [operations in the document](brief-em3d-64-operations-in-the-document.md) | `Boolean`, `Fillet`, `Chamfer`, `Step` in the `.c3d`; naming (§1g); resolution and elaboration to `Em3dShapeSolid`; refusal on open without the kernel; `check`/`explain`; the reference page | 63 |
| 65 | [both solvers, and what they cannot respect](brief-em3d-65-solvers-and-fidelity.md) | Palace via B-rep import (existing curvature sizing); openEMS via tessellation + the face-node offset + grid notes; the fidelity warnings in notes, `check` and the Setups panel; every existing golden byte-identical | 64 |
| 66 | [booleans in the editor](brief-em3d-66-booleans-in-the-editor.md) | the context-menu entry, the Boolean dialog (Tool/Blank, swap, keep tools, preview), tree nodes with operands, the Properties inspector with **Enabled**, operand selection and editing, disabled commands with tooltips | 63, 64 |
| 67 | [edges, fillets and chamfers](brief-em3d-67-edges-fillets-chamfers.md) | **Edge** selection mode, edge highlight and snapping, tangent-chain selection, Fillet and Chamfer (dialog, preview, expressions), their tree rows and **Enabled** | 66 |
| 68 | [STEP import](brief-em3d-68-step-import.md) | *Import STEP…*: units, assembly tree, names, colour → material mapping, healing report, file copied into the cell, content hash; placement; large-part behaviour | 64 (66 for the tree) |
| 69 | [STEP export](brief-em3d-69-step-export.md) | *Export STEP…* of a `.c3d` or a layout's 3D form: the elaborated model, names, colours, units, flattened or as an assembly; `convert` gains `step` | 64 |
| 70 | [the showcase](brief-em3d-70-showcase.md) | an example: a connector launch with a filleted pin, a subtracted cavity and an imported STEP body, simulated in both solvers with the fidelity notes visible; user pages; owner walk-through | all |

**Tracks:**
- **Platform:** 61 → 62 → 63. Nothing else starts before 63's capability exists.
- **Document and solvers:** 64 → 65.
- **Editor:** 66 → 67.
- **Interchange:** 68, 69 (parallel, after 64).
- Brief 70 comes last.

**Smallest demonstrable cut:** 61, 62, 63, 64, 65, 66 — draw a lid and a cavity, subtract, simulate in
Palace. Fillets (67) and STEP (68, 69) are the next cuts.

---

## 2A. Traceability — the owner's request, and where each part is built

| Request (paraphrased) | Where |
|---|---|
| Booleans, fillets and chamfers on curved edges, STEP import, using OpenCASCADE | 61–68; §1a |
| STEP export too | 69 |
| OpenCASCADE, or our own kernel? | §1a — OCCT |
| A minimal boolean UI: select several, right-click, a dialog | 66 |
| The first-selected object is the tool part; changeable in the dialog | 66, D4 |
| Should operations appear in the object tree, with an Enable toggle in the inspector? | §1f, 66, 67 — **yes** |
| Can circuitRF ship OCCT, or must the user install it? | §1b — **ship it**, D1 |
| Edit `.c3d` files without OCCT where possible; kernel features greyed out | §1d, 63, 66, 67 |
| A `.c3d` that needs OCCT, opened without it, is refused with explanation and action | §1d, 64 |
| Do Palace and openEMS respect fillets and chamfers? Warn when not | §1e, 65 |
| A series of briefs | this document |

---

## 3. Decisions — proposed; the owner confirms

| # | Decision | Brief's default | Blocks |
|---|---|---|---|
| D1 | The kernel, and how it reaches the user | **OCCT, shipped in every installer, behind an out-of-process worker** (§1a–§1c). Reverses series 3's D2 ("user-installed") at the owner's stated preference; it is a native dependency, so it is recorded here as the owner's decision once confirmed | 61 |
| D2 | Which platforms ship it | **Every RID the spike builds and runs**; Windows x86 is the doubtful one. A RID that cannot ship it takes the absent path (§1d) and says so in its installer notes | 62 |
| D3 | A kernel-using `.c3d` without the kernel | **Refused on open**, with explanation and action (owner, 2026-09-27); supersedes brief 40 §1b's "draw the operands, marked". Documents with no kernel object open normally, kernel commands disabled with tooltips | 63, 64 |
| D4 | Tool and Blank from the selection | **The first-selected object is a Tool** (owner). A boolean has **exactly one Blank**: by default the **last-selected** object; every other selected object is a Tool. With two objects that is simply *first = Tool, second = Blank*. The dialog sets any row to Blank (a radio column) and has a **Swap** button for two | 66 |
| D5 | Operations in the object tree | **Yes.** A boolean is a node with its operands beneath it; fillets and chamfers are rows under their target; each has **Enabled** in the inspector. Disabled = as if absent (§1f) | 64, 66, 67 |
| D6 | The Edge-mode key | **E**, matching O / F / V (the initial of the thing selected). Face mode's *Extrude face* moves from **E** to **Shift+E**, the one existing binding this changes | 67 |
| D7 | Where an imported STEP file lives | **Copied into the cell's `3d/` folder**, so the workspace is self-contained and under revision control; the object records the file's content hash and its original path (for a *Reload from source* command) | 68 |
| D8 | What STEP export writes | **The elaborated model** — what the solver gets — with names and material colours, ports and air box excluded; **flattened by default**, with an *As assembly* option that keeps instances as STEP instances | 69 |
| D9 | The headless spelling | **`convert`** gains `step`: `.c3d` / `.clay` / cell → `.step` exports; `.step` → a new `.c3d` imports. Importing into an **existing** document headlessly is writing its `Step` object — the format is the contract; no new verb | 68, 69 |
| D10 | *Keep tools* default | **Off**, remembered per session | 66 |
| D11 | Unite of different materials | **Allowed**; the result takes the Blank's material; the dialog and the elaboration note both say so | 64, 66 |
| D12 | Palace hand-off format | **The worker's B-rep**, if Gmsh's embedded OCCT reads it at every validated Gmsh version (spike Q4); otherwise **STEP**, which it reads everywhere | 65 |
| D13 | Direct face/vertex edits on a kernel-made solid | **Not in this series** (§1h): refused with a sentence naming what to edit instead | 66 |
| D14 | A parent `.c3d` with no kernel object of its own, instancing a child that has one, opened without the kernel | **The parent opens**; that one instance is refused and marked, and Simulate is refused naming it. D3 is about a document's own content; refusing a whole assembly for one child would lock the user out of everything above it | 64 |
| D15 | The device-worker consent prompt (`DeviceWorkerPolicy`) | **Does not apply** to the shipped geometry worker — that gate is for programs a kit names; pinned by a source test | 63 |
| D16 | Entering a boolean to edit its operands | **Double-click, or Ctrl/Cmd+]** (the existing push-into key), not Alt+click — Alt+drag already pans and Alt suspends snapping | 66 |
| D17 | Keyboard shortcuts for Subtract / Unite / Intersect | **None**; context menu and the `3D` menu only | 66 |
| D18 | A snap to the centre of a circle or arc | **In** (brief 67); curved edges make it the snap a user reaches for first | 67 |

---

## 4. What is deferred, and why

- **Direct editing of kernel B-reps** (push a face of a boolean result) — D13. It is OCCT's "local
  operations" and a feature of its own.
- **Sweeps along a path, lofts, shells, drafts.** Each is a few worker lines once this series exists; none
  was asked for. A general sweep was already deferred by series 3.
- **Sheet booleans** (imprinting a port outline on a face). The port tool already draws sheets; the fragment
  in the `.geo` already imprints them.
- **IGES, and other neutral formats.** STEP covers the request; OCCT reads IGES too, so it is a later brief if
  wanted.
- **STEP export of the schematic or of a planar EM result.** Only geometry.
- **A parametric sweep over a fillet radius.** Radii are expressions (brief 67); sweeping them is the
  `parametric_sweep` machinery's, as series 3 deferred for every dimension.

---

## 5. Where the code goes

```
tools/geometry-worker/                NEW (62): C++17, MIT; README (the protocol); build.sh / build.cmd /
                                      ensure-built.*; occt/ — the pinned recipe (version, checksum, CMake options)
src/Design/ThreeD/Occ/                NEW, no UI (63)
    GeometryKernel.cs                 discovery, handshake, version check, capability (absent + reason + action)
    GeometryKernelSession.cs          the process, framing, timeouts, cancellation, restart-after-crash
    GeometryKernelCache.cs            resolved-input hash → shape, tessellation, face and edge tables
src/Design/ThreeD/C3dDocument.cs      C3dBoolean, C3dFillet, C3dChamfer, C3dStep (64)
src/Design/ThreeD/C3dElaborator.cs    kernel trees → Em3dShapeSolid (64)
src/Design/ThreeD/Step/               StepImport, StepExport — the functions the GUI and `convert` both call (68, 69)
src/Engine/Em3d/Em3dProblem.cs        Em3dShapeSolid (64)
src/Design/Em3d/GmshGeoWriter.cs      ShapeFromFile + face recovery (65)
src/Design/Em3d/CsxcadWriter.cs       polyhedron from kernel tessellation + fidelity notes (65)
src/Engine/Em3d/FdtdGrid.cs           lines from kernel solids (65)
src/Render/Scene3D/                   edge picking via the pick patch and feature table (the ID pass stays
                                      object+face), edge highlight, edge snaps (67)
src/Ui/ThreeD/                        BooleanDialog, FilletDialog, Edge mode, Import/Export STEP (66–69)
src/Cli/                              convert step, check/explain report the kernel (64, 69)
packaging/{windows,macos,linux}/      ship worker + OCCT libraries; notices; smoke; a shipping RID without the
                                      kernel fails unless CRF_ALLOW_NO_KERNEL=1 (beside CRF_ALLOW_UNSMOKED) (62)
THIRD-PARTY-NOTICES.md, licenses/     OCCT entry, LGPL-2.1 (exists), OCCT-exception-1.0 (62)
examples/                             the showcase (70)
```

---

## 6. The series' own gate

**`R-em3d60-1`** Every brief in §2 exists and every link resolves.

**`R-em3d60-2`** When the owner confirms D1–D4, `em-3d.md` goes to **rev 7**: F4b is **shipped**, not
user-installed (§6.2's rev-6 block is replaced, not appended to); §6.4 gains edge names and boolean face
names (§1g); §6.5 gains the kernel solid's lowering; PRD §17's "agreed in principle" becomes a decision.
CLAUDE.md is **not** edited: its Stack section gains OCCT only as part of brief 62, and that is the owner's
call to make at commit.

**`R-em3d60-3`** Byte identity: every existing Palace and openEMS golden, every tessellation count, and
every shipped example's numbers are unchanged by the whole series (§1h).

**`R-em3d60-4` The owner's walk-through (owner check).** In the Debug build, with no terminal at any step:
1. open the 3D Package example (*Tools ▸ Examples* makes a copy, so the shipped files are untouched);
   draw a cylinder through the lid;
2. select the cylinder then the lid; right-click ▸ *Boolean ▸ Subtract…*; confirm the cylinder is the Tool;
   OK; see the hole;
3. in the tree, select the boolean; untick **Enabled**; see the cylinder back; tick it again;
4. press **E**, pick the lid's top edge around the hole, *Fillet…* 50 µm; see it round;
5. *Import STEP…* a connector body; map its parts to materials;
6. Simulate in Palace, then in openEMS; read the fidelity note on the openEMS run;
7. *Export STEP…*; open the file in any STEP viewer.

The owner records the elapsed time of each step and anything that did not feel immediate.

---

## 7. Scope for the series

- **No change to any existing answer** (gate 3).
- **One native dependency, OCCT, and only through the worker.** No managed assembly P/Invokes it; the
  firewall test proves it.
- **No per-primitive edit verbs.** A boolean is authored headlessly by writing it; the reference page
  describes every field.
- **No timing tests.** Counters only.
- **Commercial names stay out of the repository** — no commercial kernel, CAD tool or EM tool is named,
  including in "alternatives considered". OCCT, Gmsh, Palace and openEMS are open-source projects and are
  named, as em-3d.md already names them.
- **Nothing quotes the owner** in the repository; requests are paraphrased.
- **On completion of each brief, findings go in the relevant `RESOLVED.md`**, never `CLAUDE.md`. Doc
  sources are edited; DocGen is not run per brief.
- **Keep EM runs short.** A gate that needs a solve uses the smallest case that tests the claim; anything
  over ~5 s is `Category=Benchmark`.
