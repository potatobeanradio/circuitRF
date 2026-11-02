# Brief 93 — "Model": keep an object in the drawing and out of the solve

**Tag:** `R-em3d93-n` · **Series:** 3D editor round 8 (90–95).
**Area:** `src/Design/ThreeD/C3dDocument.cs`, `C3dPersistence`, `C3dElaborator.cs`, `C3dProblemAssembly.cs`,
`C3dValidation.cs`, `src/Design/Thermal/ThermalLowering.cs` (+ the wire, heat-source and probe lowerings),
`src/Ui/ThreeD/C3dEditorViewModel.cs` + `.TreeGrouping.cs` + `.Groups.cs`, `C3dPropertiesViewModel.cs`,
`src/Ui/Views/ThreeD/C3dEditorView.axaml`, `src/Cli/DocumentSchema.cs`, `src/Cli/Check.cs`, `src/Cli/Explain*.cs`,
`docs/user/src/reference/drawing-in-3d.md`, `em-3d.md`, `thermal.md`, the `.sNp` writer (port-map header lines)
**Depends on:** — · **Blocks:** 95 (copy/paste carries it)

## Why

A user often wants to try a run without an object (the lid, a second bond-wire array, a fixture) and keep it on screen to
edit and to put back. Today the choices are to delete it, or to hide it, and hiding does not remove it from the solve.
`Hidden` is display state only. Effective blocks (`Enabled`) and fillets (`Enabled`) already have a switch of their own
for exactly this. This brief gives every drawn object one.

## 1. `R-em3d93-1` — the property

- `C3dObject.Model`: bool, **default true**, written **only when false** (`"Model": false`), so no existing file changes.
- **Carried by:** box, prism, cylinder, sheet, polygon, wire, an operation (boolean / fillet / chamfer, for its result),
  an **instance** (the whole placed cell), and a **port** (§1a). `C3dInstance` and `C3dPort` are **not** `C3dObject`s
  (each is its own class in `C3dDocument.cs`), so each needs the property added separately, with the same key, the same
  default and the same write-only-when-false rule.
- **Heat sources and mesh regions** are thermal places and not `C3dObject`s. **Owner decision D1:** give them the same
  `Model` key (recommended for heat sources, since leaving a source out is the thermal equivalent of this whole feature;
  a mesh region may simply be deleted). **Effective blocks keep `Enabled`**, which means the same thing; the Inspector
  labels it "Model" there so there is one word for it (D1).
- **Exceptions** (no Model row, and the brief says why in the tooltip):
  - a **polyline** is construction geometry and is never modelled anyway;
  - a boolean's **operand** belongs to its result;
  - a fillet/chamfer **feature row** already has `Enabled`;
  - **probes**, **field plots**, **symmetry planes**, **thermal boundaries** and the **air box** are readings or setup,
    not model content.
- Undoable, dirty-marking, exactly as `Hidden`.

### 1a. Ports (owner, 2026-09-29)

A port carries `Model` too. **This is how a user turns a port off for one simulation** without deleting it and losing
its placement, its Z0 and its voltage path.

- **A port that is not modelled is left out of the solve entirely.** It gets no excitation, no port sheet and no lumped
  element. The gap it spanned is whatever the geometry and the background are there: open, not shorted and not
  terminated. The run's notes say this in those words, because "turned off" is sometimes assumed to mean "terminated in
  Z0", which is a different circuit. **Owner decision D2a:** whether a port that is off should instead be *terminated
  in its Z0* (kept as a load, not excited, and left out of the result). The owner said "turn a port off", which this
  brief reads as "not there"; confirm.
- **The result's port count is the modelled ports'.** A Touchstone file's ports are 1…N with no gaps, so the modelled
  ports are **renumbered contiguously in their `Number` order** for the result. `P1, P3, P4` with P2 off become result
  ports 1, 2, 3. The mapping is recorded:
  - in the run's notes and the Messages panel (`Port 2 not modelled: the result's ports are P1→1, P3→2, P4→3`);
  - in the `.sNp`'s comment header, one line per port, so the file carries its own record;
  - in the diagnostics group of the result `.npy`, as the provenance already records ports.

  The document's port numbers are **not** changed: turning P2 back on restores the four-port result. **Owner decision
  D2b:** renumber (recommended), or refuse to run with a gap.
- **Every EM path does the same.** Palace and openEMS both take their ports from the one filtered elaboration (§2), so
  neither renumbers on its own. Do the renumbering once, in `src/Design`, where the problem's ports are assembled.
- **At least one modelled port** is required for an S-parameter run. With all ports off, the run is refused
  (`Every port is turned off (Model): there is nothing to excite`).
- **A consumer of the result sees the new port count.** A schematic referencing this `.sNp` gets a different pin count
  after a run with a port off. The run's report says so when the count differs from the document's port count:
  `The result has 3 ports; this 3D view declares 4 — a schematic placing it expects 4`. Nothing is blocked.
- **A port whose conductor is not modelled** is a refusal only while the **port itself is modelled** (§2). Turning the
  port off too is the way through.
- **The tree:** a port that is off is greyed in the Ports group. By type, it lists under Not Modeled as every other kind
  does (§3), so the rule stays uniform. Its hover and the port arrow's tooltip say "(not modelled)".
- **Field plots and brief 82's port-referenced readings** on a port that is off say so, rather than showing a field
  from an excitation that did not happen.

## 2. `R-em3d93-2` — the solve leaves it out, in one place

- **The elaboration keeps a not-modelled object**, because the scene is built from it and the object must stay drawn,
  pickable and editable. It marks the object: `C3dElaboration` gains the set of solid/sheet/wire names that are not
  modelled (instance content included, by the instance's flag).
- **One function in `src/Design`**, e.g. `C3dModelled.Filter(C3dElaboration)`, returns the elaboration a **solve** sees.
  Every solver path calls it and nothing else filters:
  - the EM problem assembly (Palace and openEMS alike, `C3dProblemAssembly`, including the air-box extent, so a
    not-modelled object far away does not grow the box);
  - the thermal lowering (solids, sheets, wires, the wire chains, heat sources in solids, RF currents);
  - `em` and the thermal run from the CLI, which go through the same assembly.

  Find the single point where the elaboration's solids feed the problem builders and put the call there. If there are
  two such points, there are two callers of the one function, never two filters.
- **References to a not-modelled object are REFUSALS, never silently dropped.** A result that ran without something the
  user did not realise was missing is the outcome this feature must not create. Each refusal names both parties:
  - a **modelled** port whose `+` or `−` conductor is not modelled (§1a);
  - a wire whose pad is not modelled (the wire itself modelled);
  - a heat source `in` a not-modelled solid;
  - a probe on a not-modelled solid/face/wire;
  - a thermal boundary on a not-modelled face;
  - a field plot's face on a not-modelled object. This one is a warning, not a refusal: it is a reading and can simply
    show nothing.
- **Staleness:** toggling Model is a document edit, so brief 87's inputs manifest makes the previous result stale. Check
  that this holds; add nothing unless it does not.
- **STEP export** (brief 69) and **Export Drawing** export geometry, not the solve: a not-modelled object **is**
  exported. **Owner decision D3** if not.

## 3. `R-em3d93-3` — the tree

- **By type:** a not-modelled object is listed under a **`Not Modeled`** group (after the type groups, before
  Instances), not under its type. Its row's detail says its type (`Box`), so nothing is lost.
- **By material:** it stays under its material, with its **name greyed** (the row's `TextBlock` foreground bound to a
  theme's disabled/secondary brush, via a new `C3dTreeItem.IsModelled`). The tooltip says "Not modelled: drawn and
  editable, left out of every simulation run".
- **In a group:** the member's row is greyed (by type and by material alike). A group whose members are **all** not
  modelled has a greyed header. In the By-type view a group's row is not split out into Not Modeled, because moving
  members out of their group's row would break the group's display; the grey is the signal there. State this rule in the
  code comment.
- The tree filter's type list gains `Not Modeled` as a type, so the filter can show or hide them as a set.

## 4. `R-em3d93-4` — the Inspector, and gestures

- A **Model** check row on every object that carries it. A group selected whole (and a multi-selection) shows one row:
  ticked, unticked, or indeterminate when members differ. Ticking or unticking writes every member at every depth: one
  undo entry through `ChangeObjects`.
- A context-menu item **Model** (with a ✓ when on) on the canvas and the tree, calling the same function.
- The 3D view **draws it as it is**, since the owner asked for it to stay visible and editable. The hover tooltip gains
  "(not modelled)". **Owner decision D4:** also draw a subtle cue, e.g. dashed silhouette edges. The recommendation is
  no cue beyond the tree's grey and the tooltip, to start.

## 5. `R-em3d93-5` — check, explain, the run's report

- `check x.c3d`: INFO `N object(s) are not modelled: lid, fixture` (exit 0), plus the refusals of §2 as errors, through
  the same validator the run uses (`C3dValidation`). Nothing is written only in `check`, per the repo's rule.
- `explain x.c3d --analysis` (or the default summary) lists the not-modelled objects.
- The run's own notes (the Messages panel, and the result's notes) say which objects were left out, so a result carries
  its own record of what it was solved from.

## 6. Gate

1. Round trip: `"Model": false` on a box, a group member and an instance loads and saves byte-identical; a file without
   the key is byte-identical to before.
2. `C3dModelled.Filter`: a document with two boxes, one not modelled. The EM problem has one solid, the air box sized to
   it alone, and the scene still two objects.
3. The thermal lowering on the same document: one solid. A heat source in the not-modelled box is a refusal naming both.
4. A port on a not-modelled conductor is a refusal naming the port and the conductor. The same through `check` as a
   process: exit 1.
5. Tree: By type lists the box under `Not Modeled`; By material lists it under its material with `IsModelled == false`.
6. A group of three with mixed Model: the Inspector row is indeterminate; ticking sets all three in one undo entry.
7. Staleness: after a run, toggling Model marks the result stale (brief 87's mechanism, asserted and not re-implemented).
8. Ports: four ports with P2 off. The assembled EM problem has three ports numbered 1–3, mapped P1→1, P3→2, P4→3, and the
   mapping sits in the notes. The same document assembled for Palace and for openEMS gives the same three ports. All
   four off is a refusal. P2 off with its conductor also off is no refusal. `"Model": false` on a port round-trips
   byte-identical.
9. The `.sNp` header writer, given the mapping, emits one comment line per port (a unit test of the writer; no solve).

No full-wave run in any gate. Assert on the assembled problem, not on a solve (keep EM runs short).

## 7. Decisions for the owner

**Settled (owner, 2026-09-29):** ports carry Model; it is how a port is turned off for one run (§1a).

**Still open, built as recommended unless the owner says otherwise:** D1 heat sources carry Model, and effective blocks'
`Enabled` is labelled "Model" · **D2a** a port that is off is *absent* (open), not terminated in Z0 · **D2b** the modelled
ports are renumbered contiguously, with the mapping recorded, rather than refusing a gap · D3 not-modelled objects are
still exported to STEP and drawings · D4 no extra visual cue in the view, to start.

## Docs

`drawing-in-3d.md` (the property, the tree's grey and Not Modeled group), `em-3d.md` and `thermal.md` (what a run leaves
out and the refusals). Doc sources only.

## On completion

`src/Design/RESOLVED.md` and `src/Ui/RESOLVED.md`, never CLAUDE.md. Do not commit unless the owner asks.
