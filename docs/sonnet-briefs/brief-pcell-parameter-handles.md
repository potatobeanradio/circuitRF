# Sonnet Brief — PCell parameter handles (geometry-driven parameter editing)

**Design:** **`docs/design/pcell-parameter-handles.md` is authoritative — read it first, in full.**
Its **R-pch-1 … R-pch-10** are the rules; this brief is how to build them and in what order. If the
two disagree, the design doc wins and the brief is wrong.

**Also read:** `pcell-contract.md` (R2 one parameter list, R5 determinism, R6 evaluate-once, R9
generated artwork is read-only), `pcell-wire-schema.md` (§1 no metres, §2 frames, §4.3 the reply,
§7 versioning), `layout-view.md` §6.3, and `src/Ui/CLAUDE.md`'s **L1d** (handle drag), **L1h**
(scale drag — the third drag state machine), **L3a** (instances, transforms, the pin overlay),
**L5** (generated cells, copy-on-write) and **B0–B7/C2** (the wire and the Python package) entries.

**Consumes** L5 and Track B/C in full. **Touches** `src/Ui/Layout/**`, `src/Ui/Renderers/**`,
`src/Ui/Views/Layout/**`, `tools/pcell-python/**`, `tests/Ui.Tests/**`, `docs/design/**`.

**Do not touch** `src/Core`, `src/Engine`, `RfCore`, the schematic editor, or the symbol editor.

Gate command is plain `dotnet test` (the fast default), plus
`python3 tools/pcell-python/verify.py`.

---

## 0. What you are building, in one paragraph

A placed PCell instance grows draggable grips. Each grip is declared by the generator (which
parameter, where the grip is, which way it moves), the host measures how much the parameter changes
per unit of travel by regenerating with a perturbation, and a drag commits one parameter edit
through the **existing** copy-on-write path. Nothing about generated artwork becomes editable; what
becomes editable is a parameter that happens to be drawn somewhere.

**The feature is optional per generator and every field is additive.** When you are done, a
generator that declares nothing must behave exactly as it does today — same geometry, **same
generated-cell folder name**, same everything.

---

## 1. Milestones

Build in this order. Each one is independently testable and M1–M3 need no UI at all.

### M1 — the contract, and the built-ins that demonstrate it

`src/Ui/Layout/PCells/PCellContract.cs`:

- `PCellHandleKind { Linear, Angular }` and the `PCellHandle` record, both exactly as the design doc
  §2.1 spells them. Coordinates are **cell-local DBU**, the same frame `PCellPin` already uses.
- `PCellResult` gains `IReadOnlyList<PCellHandle>? Handles = null` — **a trailing, defaulted,
  positional parameter**, so every existing construction site compiles untouched.

Then declare handles on **MLIN** (`L`, `W`), **MTaper** (`L`, `W1`, `W2`) and **MKlopf** (`L`,
`Offset`). Those three cover a straight run, two independent widths on one cell, and a parameter
whose geometry relationship is not linear — which is what M2 has to survive.

> **⚠ The trap that will cost a workspace if you miss it: do NOT bump
> `PCellRegistry.GeneratorVersion` for any generator you add handles to.** That field feeds the
> generated cell's **content hash**, i.e. its folder name. Bumping it renames every generated cell
> in every existing workspace while every placed `LayoutInstance.CellRef` still names the old
> folder — every one of them renders as the "Not Found" placeholder. The field's own doc comment
> states the rule: bump only when the geometry **output** changes for an existing parameter set.
> **Handles are not geometry.** Gate 1 pins this.

### M2 — `PCellHandleSolver` (framework-free, headless, where the real logic lives)

New `src/Ui/Layout/PCells/PCellHandleSolver.cs`. No Avalonia, no Skia, no view model — it takes a
`Func<IReadOnlyDictionary<string, PCellValue>, PCellResult>` and a handle, and answers *"what value
of this parameter puts this grip at that point?"*

Two operations:

1. **Probe** (once per drag): perturb the parameter by δ, regenerate, project the grip's
   displacement onto the declared axis, and report value-per-DBU. **Choose δ unit-free** — relative
   to the current value with an absolute fallback at zero — growing it geometrically to a small
   fixed cap until the grip moves measurably. R-pch-2's whole promise is that no unit appears in the
   declaration; a δ chosen from an assumed unit would put one back in through the side door.
2. **Solve** (per drag step): propose a value from the measured sensitivity, regenerate, and if the
   grip missed the target by more than a tolerance, correct and retry to a bounded iteration count
   (two or three). Return the achieved value **and** the achieved grip position — R-pch-3 makes the
   latter what the UI draws.

**R-pch-11 (new, and the design doc should gain it — see §5): the solver is deterministic.** Same
start parameters and same target ⇒ same committed value, bit-for-bit. Fixed δ schedule, fixed
iteration count, fixed tolerance, no wall-clock and no early-exit that depends on how many
iterations happened to be cheap. Then **round the committed value to a fixed significant-digit
count** before it is written back.

Both halves are load-bearing and the reason is not style: the committed value goes into
`PCellValue.ToString()`, which **is** the content hash that names the generated cell. A value that
differs in its seventeenth digit between two identical drags produces two different cell folders for
the same design intent — silently defeating R6's sharing and churning `.generated-cells/`. Gate 9.

Test it against synthetic generators, not against the built-ins: linear, quadratic, integer-quantized,
internally-clamped, and zero-derivative. That is the only way to cover the paths that matter.

### M3 — the wire (version 6) and the Python package

- **Schema:** `handles` array in the generate reply. Anchor and position are **coordinates**, so all
  four int64s ride in the **binary payload** addressed by a `"span": {"at": i, "count": 4}` —
  §2's "no coordinate ever appears in the JSON" is absolute and this is not the place to make an
  exception for brevity. `min`/`max` are parameter *values* and are encoded as §3 already encodes a
  value.
- **`WIRE_VERSION` 5 → 6** on **both** sides (`host.py` and the C# constant). The bump is required
  even though the field is additive, because `describe` refuses on inequality by design.
  **`CONTRACT_VERSION` stays 2** — `Generate`'s signature has not changed.
- **Python:** a `Handle` dataclass in `geometry.py`, exported from `__init__.py`, and
  `Result.handles`. Match the design doc §2.2 sample exactly — that sample is the documentation
  a cell author will copy.
- **R-pch-6:** an unrecognised `kind` string drops that one handle and reports once. It must not
  fail the generate, and it must not drop the *other* handles.
- Update `docs/design/pcell-wire-schema.md`: §4.3's reply, the version note at the top, and §7's
  table. Update the `WIRE_VERSION` comment block in `host.py` the same way the previous four bumps
  did.
- `tools/pcell-python/verify.py` gains checks for the new encoding.

### M4 — the editor

- **Resolve.** For the single selected instance, resolve its cell (`CellLayoutResolver.Resolve`),
  read `PCellOrigin`, get the generator (`PCellRegistry.TryGet`) and invoke it through
  **`PCellGeometryCache`**. This is the same shape `LayoutRenderer.Instances.cs`'s pin overlay
  already uses to get a cell's pins from its generator — copy that structure, including its
  degrade-on-throw contract. **Handles are never persisted in `.clay`**; a cell with no invokable
  generator simply has none.
- **Draw.** Handles for a single selected PCell instance only, on the **base (0,0) array
  placement** only. New `ColorRole.LayoutPCellHandle` + `LayoutRenderTheme` token, light and dark.
  **It must not look like an L1d vertex handle** — one edits geometry, the other edits a parameter,
  and a user who confuses them will be surprised in a way that is hard to undo. Draw a short axis
  hint so the travel direction is visible before the drag starts.
- **Hit-test.** Radius in **device pixels, computed fresh per query from the current zoom** — never
  cached, never derived from `SnapDbu`. This codebase has already shipped that bug once.
- **Drag.** A **fourth** state machine alongside `_selectDragKind`, `_handleDragKind` and
  `_scaleDragKind`, and it must be checked **before** the instance-body move drag in
  `HandleSelectPress` — otherwise grabbing a grip moves the whole instance instead.
  - Snap the pointer to `SnapDbu` **in world space**, then
    `LayoutInstanceTransform.InverseTransformPoint` into cell-local space, then project.
    **Never the other way round.** Doing it in world would need the magnification division written
    by hand (at `Mag = 2`, two millimetres on screen is one in the cell) and the mirror axis flipped
    by hand — both silent when wrong. Gate 3.
  - Alt suspends snap. Escape cancels with nothing committed and no undo entry.
  - Live readout through the existing `DrawReadoutText`: `Label = value` in the document's display
    unit.
- **Commit.** On release only, through the existing
  `LayoutEditorViewModel.EditInstancePCellParameters` — one `ReplaceInstanceCommand`, one undo
  entry, copy-on-write for free. **Preserve the parameter's `PCellValue` kind**: an Int parameter is
  written back as `Int`, never as a `Real` that happens to be whole. B0's rule ("which kind a
  parameter is belongs to the cell that declares it") applies here unchanged, and a flipped kind
  changes the content hash.
- The **document-is-the-PCell** case (`Model.PCellOrigin` non-null) uses the identical declaration
  and commits through `RegeneratePCell` instead. Same handles, different target.

### M5 — the preview budget and the disk guard

- **R-pch-9: no generated cell is written during a drag.** Preview goes through the in-memory
  `PCellGeometryCache`; `GeneratedCellStore.GetOrCreate` runs exactly once, on release. Add a
  test-visible write counter to `GeneratedCellStore` — **gate 5 is a counter assertion, never a
  timing one**, matching this repo's own convention for every other cost claim.
- **R-pch-10:** time the first regeneration of each drag. Above ~16 ms that drag falls back to
  deferred preview — pre-drag artwork stays, grip and axis hint follow the cursor, readout updates
  linearly from the measured sensitivity, artwork regenerates once on release. Not an error, not
  reported.
- **Suppress `PCellResult.Diagnostics` during probing and preview.** The probe deliberately
  regenerates with an off-nominal value; a validity-range reporter would post a Messages warning per
  pointer move. Surface diagnostics on **commit** only.

### M6 — degradation, all of it reported and none of it blocking

Implement the design doc §8 table exactly. Every row degrades to "no grip for that parameter, and
the parameter is still editable in the Properties Inspector". Specifically: a handle naming an
undeclared parameter, a handle on a `String` or `Bool` parameter, an unmeasurable sensitivity, a
generator that throws while probing, a drag that hits a declared `Min`/`Max`, and a non-converging
solve.

### M7 — `Angular` (stretch; decide and say which you did)

`Linear` is the whole of M1–M6. `Angular` is defined in the enum and in the wire vocabulary from M1
so that adding it later needs no second bump.

**Until it lands, a handle declaring `Angular` is dropped-and-reported through the R-pch-6 path** —
which conveniently gives that path a real exercise rather than a synthetic one. If you do implement
it, the projection is `atan2` about the anchor relative to `AxisDeg` and the M2 solver is unchanged;
MBend's `Angle` is the demonstrating case.

---

## 2. Reuse, do not reinvent

| Need | Use |
|---|---|
| Cell-local ↔ world for an instance | `LayoutInstanceTransform.TransformPoint` / `InverseTransformPoint` |
| Snapping a dragged point | `LayoutSnapping.SnapPoint` / `SnapValue` (Alt-suspend included) |
| Invoking a generator for preview | `PCellGeometryCache.GetOrGenerate` |
| Committing a parameter edit | `LayoutEditorViewModel.EditInstancePCellParameters` |
| Writing / recording a generated cell | `GeneratedCellStore.GetOrCreate` / `RecordSnapshot` |
| Undo | `Commands.Layout.ReplaceInstanceCommand` (already what the commit path uses) |
| Resolving an instance's cell | `CellLayoutResolver.Resolve` |
| Getting a generator by id | `PCellRegistry.TryGet` |
| Per-instance decoration on the canvas | the pin overlay in `LayoutRenderer.Instances.cs` |
| Drag readout | `LayoutEditorViewModel.DrawReadoutText` |
| Colour | a new `ColorRole` + `LayoutRenderTheme` token — never a literal |

**There must be exactly one commit path.** The Properties Inspector's PCell parameter list and a
handle drag both go through `EditInstancePCellParameters`; two paths would be two chances to
disagree about what an edit means.

---

## 3. Guardrails

- **Do not** make generated artwork editable. R9 is untouched; you are adding grips on parameters.
- **Do not** bump `GeneratorVersion` for any generator (§1 M1).
- **Do not** add a second handle-declaration surface — no separate file, no metadata beside the kit,
  no `describe`-time declaration. Handles come back with `generate`, on the `Result`, and nowhere
  else.
- **Do not** implement a `coerce` / guiding-shape inverse. Design doc §6; explicitly out of scope.
- **Do not** build two-dimensional (corner) handles, a typed-value override during the drag,
  connectivity that follows a parametric edit, or an automatic schematic round-trip. All deferred
  in design doc §10, each with its reason.
- **Do not** persist handles in `.clay`.
- **Do not** touch `src/Core`, `src/Engine`, `RfCore`.

---

## 4. Gate

Plain `dotnet test` green with no regression, plus `python3 tools/pcell-python/verify.py`. Then:

1. **Folder names unchanged (the workspace-breaker).** For every built-in, the generated cell folder
   name for a given parameter set is **byte-identical** to what it was before this work. Assert
   against a hard-coded pre-change name, not against a freshly computed one — a test that recomputes
   both sides passes whatever you did.
2. **Round trip (the property everything rests on).** For each built-in that declares handles: drag
   to a target, commit, resolve the new cell, assert the handle came back within tolerance of the
   target.
3. **Transform.** A rotated / mirrored / magnified instance drags correctly, over all eight
   rotation×mirror combinations plus a non-unit `Mag`. **Confirm it bites:** forward-transforming
   the handle and projecting in world instead must turn this red.
4. **Copy-on-write.** Two instances of one generated cell; drag one; the other's `CellRef` and
   resolved geometry are byte-identical afterwards.
5. **No disk during a drag.** `GeneratedCellStore`'s write counter is exactly 1 per drag and 0
   during pointer moves, over a drag of ≥ 50 moves.
6. **Undo.** A drag of N pointer moves is one undo entry, restoring the original `CellRef`.
7. **Read-only preserved.** The original generated cell's `.clay` is unmodified after a drag.
8. **Non-linear.** A synthetic generator whose grip position is quadratic in its parameter converges
   within the iteration cap.
9. **Determinism (R-pch-11).** The same drag, run twice from the same start, commits a bit-identical
   value and resolves to the **same** generated cell folder.
10. **Degradation.** No handles declared → no grips, nothing reported. Undeclared parameter →
    dropped and reported. `String` parameter → dropped and reported. Unmeasurable sensitivity →
    dropped and reported. Unknown wire `kind` → that handle dropped, the others kept.
11. **Slow cell.** A synthetic generator over the budget forces deferred preview and still commits
    the correct value on release.
12. **Wire round trip.** Handles survive encode→decode for every built-in, driven through the real
    transport, alongside the existing shape/pin round-trip theory.
13. **Kind preserved.** Dragging a handle on an `Int` parameter commits an `Int`, not a `Real`.
14. **Diagnostics not spammed.** A drag across a generator's validity bound posts at most one
    Messages entry, on commit — not one per pointer move.

**Interactive verification is not available in this environment** (no visual driver, matching every
prior Layout Editor phase). Say so in the completion note and list what the owner must confirm by
hand: that a grip is visually distinct from an L1d vertex handle, that the axis hint reads
correctly, that the live readout tracks, and that a heavy script-backed cell falls back to deferred
preview without stuttering.

---

## 5. On completion

**Update `docs/design/pcell-parameter-handles.md` first, not only `CLAUDE.md`.** It was written
before any implementation and implementation will have found things it got wrong or left vague.
Specifically:

- Move the status line off **Design proposal**.
- **Add R-pch-11 (solver determinism) to §2 or §4.3 as a numbered rule** — this brief raises it and
  the design doc does not yet name it, which is the design doc's gap, not the brief's addition.
- Record the actual δ schedule, iteration cap, tolerance and significant-digit rounding chosen in
  M2, and the measured preview budget in M5. Those are the numbers the next person will need and
  they are all currently written as "~" or "a small fixed cap".
- Say whether `Angular` shipped (§10).

**Then `docs/design/pcell-wire-schema.md`** for wire version 6 (§4.3, the version note, §7).

**Then `src/Ui/CLAUDE.md`**, recording: that handles are declared per-`generate` and never
persisted; that `GeneratorVersion` must **not** be bumped for a handle-only change and what happens
if it is; that the cursor is inverse-transformed into cell space rather than the handle forward-
transformed, and why; that no generated cell is written during a drag; that the solver is
deterministic and why the content hash makes that non-negotiable; and that there is exactly one
commit path shared with the Properties Inspector.
