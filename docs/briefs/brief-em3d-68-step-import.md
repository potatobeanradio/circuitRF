# Brief 68 — STEP import

**Series:** [3D EM, fourth series](brief-em3d-60-overview.md) · **Tag:** `R-em3d68-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §6.3 (Tier B), §6.4 (naming); overview §1g (a STEP part's
faces), §1d (the kernel absent), D7 (the file is copied), D9 (the headless spelling)
**Area:** `src/Design/ThreeD/Step/StepImport.cs` (new — the one function the GUI and `convert` call),
`src/Design/ThreeD/C3dDocument.cs` (`C3dStep`), `tools/geometry-worker` (the `read-step` request),
`src/Ui/ThreeD/` (the Import STEP dialog and the material-mapping table), `src/Ui/Views/WorkspaceWindow.axaml` +
`src/Ui/Views/Shared/TornOffFileMenuView.axaml` (File ▸ Import ▸ STEP…), `src/Cli/LayoutConvert.cs` (`.step` →
`.c3d`), `src/Cli/Check.cs`, `src/Cli/Explain*.cs`, `src/Cli/Reference.cs`, `tests/`
**Depends on:** 64 (the `Step` object kind and its elaboration), 63 (the client and the capability); 66 for the
tree rows · **Blocks:** 69's round-trip gate, 70

---

## 0. What this brief delivers

Connectors, packages and housings come from their makers as STEP. When this brief is done a user can put one in
a `.c3d`:

- **File ▸ Import ▸ STEP…** and **3D ▸ Import STEP…** open a file dialog, then one small dialog: the file's parts
  as a table — name, colour swatch, solid or not, the suggested material, a material combo — and a units line
  (*"File is in inches; imported exactly"*). OK imports; the parts appear in the viewport and the tree, selected,
  ready to Move.
- **Each part is one object** of the new kind `Step`, with its own name, material, placement and faces. A part is
  therefore everything any other solid is: movable, a boolean operand, a fillet target, a port's face.
- **The file is copied into the cell** (`<cell>/3d/<file>.step`, D7) and its **content hash** recorded, so the
  workspace is self-contained, a revision commit carries it, and a face reference cannot drift silently.
- **Reload from Source** re-reads the original file after its maker revises it, re-validating every reference
  into it (§5).
- **Headless**, `circuitrf convert part.step -o <cell>/3d/part.c3d` makes a new `.c3d` from a STEP file through
  the same function (D9). Importing into an **existing** document headlessly is writing `Step` objects into it —
  the format is the contract, and the reference page describes every field.

Nothing in this brief interprets STEP in managed code: the worker reads it (OCCT's STEP reader with names and
colours), heals it, and answers with a description. `StepImport` decides what becomes an object.

---

## 1. `R-em3d68-1` — one object per part, not one per file

**`R-em3d68-1a` The decision.** A `Step` object is **one solid part** of one STEP file:

```json
{ "$type": "Step", "Name": "shell", "Material": "Brass",
  "File": "sma-body.step", "Part": "0:1:1:2", "Hash": "sha256:…", "Unit": "inch",
  "SourcePath": "../../../incoming/sma-body.step" }
```

- `File` is relative to the `.c3d`; `Part` is the part's **occurrence path** in the file's assembly (the
  reader's label path — stable for identical bytes); `Hash` is the SHA-256 of the copied file; `Unit` is the
  file's own length unit, recorded for `explain` (the geometry is converted on every evaluation, §2);
  `SourcePath` is where it came from, relative to the `.c3d` when it is inside the workspace, absolute otherwise.
- Several objects may name the same `File`. The file is copied once.

**`R-em3d68-1b` Why not one object per file.** A file-level object would have to carry a material per part, a
placement per part, and a face namespace per part — a small document inside the document. Worse, it could not be
a boolean operand: overview §1i gives a boolean result **the Blank's material**, which a multi-material compound
does not have. One solid per object keeps every series-3 and series-4 rule true unchanged: one name, one
material, one role, one placement, `face<n>` meaning one thing. The price — moving a whole imported connector
means selecting its parts — is paid by the import selecting them all on arrival, and by Move acting on a
selection.

**`R-em3d68-1c` The assembly transform is part of `Part`, not of the placement.** A STEP assembly places each
occurrence with an arbitrary rigid transform. Baking that into `C3dPlacement` would round an arbitrary rotation
into the integer origin and axis-rotation list (brief 40 §1d). Instead the worker applies the occurrence's
location when it builds the shape, the object's `Placement` starts at identity, and anything the user does
afterwards composes on top. **An imported part lands exactly where its file puts it, with no rounding.** A part
instanced *n* times in the file is *n* objects with *n* `Part` paths.

**`R-em3d68-1d` Names.** A part's object name is its STEP product name, made valid by `NameValidator`'s rule and
unique in the document by `_2`, `_3` suffixes, exactly as Duplicate names a copy. An unnamed part is
`<file-stem>_<n>`. The dialog shows the final names and lets the user edit them before OK.

**`R-em3d68-1e` Faces** are `face<n>` in the part's own topological order as the worker explores it (overview
§1g), `n` from 1. They are meaningful **for this `Hash`**; §5 is what happens when the hash changes. Edges are
named from faces exactly as on any solid (`face3|face7`, brief 67).

## 2. `R-em3d68-2` — units, exactly

**`R-em3d68-2a`** The worker reads the file's length unit (mm, inch, m, µm, or any conversion-based unit the
reader resolves) and converts to the document's DBU on every evaluation: the scale is `DbuPerMicron` × the
unit's micrometres. It is exact for every SI unit and for inch (25 400 µm).

**`R-em3d68-2b`** The dialog states the unit found, in words. A file whose unit the reader cannot resolve is a
**refusal naming what it found**, never a guess — the rule `convert` already follows for an Excellon file's
coordinate format (CLAUDE.md, *an unstated Excellon coordinate format is a REFUSAL*).

**`R-em3d68-2c`** The document's display unit does not change on import. A connector in inches inside a
package in mil is simply geometry.

## 3. `R-em3d68-3` — colours to materials

**`R-em3d68-3a` Auto-match, in this order**, against the document's technology's resolved materials (the
technology's own list plus its `.cmat` libraries, brief 53):
1. the part's name equals a material name (case-insensitive);
2. the part's colour equals a material's `Color` (exact RGB);
3. otherwise **no material**.

Nothing fuzzier. A near-colour match would silently give a gold-coloured plastic σ = 41 MS/m.

**`R-em3d68-3b` No material is not a refusal.** An object with no material is already a **warning, and ignored
by the solver** (em-3d.md §6.4, round 2). An imported part the user has not mapped is exactly that: drawn, in
the tree under *No material*, excluded from the run, and named by `check`. So an import never blocks on mapping.

**`R-em3d68-3c` The table** lists every part: its colour swatch (the part's colour, else *none*), the match and
**why** (*by name*, *by colour*, *unmatched*), and a material combo with the document's materials. A
**Map all of this colour** action sets every part sharing a colour at once — a connector's twelve brass parts
are one gesture.

**`R-em3d68-3d` A document with no technology.** The destination is **an empty technology**, never `null`:
`src/Design/RESOLVED.md` §3 (*a null destination technology silently drops every layer*) is the trap every
headless reuse of an importer meets, and here it would make every part silently unmatched with no reason given.
With an empty technology every part is an unmatched row, which is exactly the truth, and the dialog says why
(*"This document's technology defines no materials"*).

## 4. `R-em3d68-4` — what is imported, and what is reported

**`R-em3d68-4a` Healing.** The worker runs OCCT's shape healing on every part and reports what it changed per
part (faces fixed, gaps closed, edges merged, tolerance raised). Each becomes a **note** on the import — in the
dialog, in the Messages panel, and on `convert`'s stderr — never a silent repair.

**`R-em3d68-4b` A part that is not a closed solid** after healing (a surface model, an open shell, a lone face)
is listed in the table, **unchecked and disabled**, with the reason. It is not imported: a `Step` object is a
solid, and an open shell has no inside for a dielectric to fill or a conductor to exclude. The note counts them.

**`R-em3d68-4c` Big parts.** Reading runs on the same `RunControl` the EM runs use: a progress line, and
**Cancel** that leaves the document and the cell folder untouched. The worker reports **part count, face count
and display-triangle count** before anything is committed; the dialog shows them. Above a threshold (the
display-tessellation budget brief 63 sets) the dialog says the part will be drawn coarser and why; it does not
refuse. The solver tessellations are brief 65's, requested separately.

**`R-em3d68-4d` Nothing is written until OK.** The copy into `3d/` and the document edit are one commit and one
undo entry. Undo removes the objects; the copied file stays until the document is saved without a reference to
it (the save then deletes a `3d/*.step` that nothing names and that the import created in this session — it
never deletes a file it did not create).

**`R-em3d68-4e` The copy (D7).** `<cell>/3d/<source file name>`. If a file of that name exists with the **same**
hash, it is reused; with a **different** hash, the copy is `<stem>_2.step`, and so on.

## 5. `R-em3d68-5` — Reload from Source

**`R-em3d68-5a`** A `Step` object's context menu, and the tree's, offer **Reload from Source**, enabled when
`SourcePath` resolves to a readable file. It acts on every object naming that `File`.

**`R-em3d68-5b` Same hash:** nothing changes, and the status bar says so.

**`R-em3d68-5c` A different hash.** The new file is read; both files are in hand, so every reference into the
old one is re-validated **by geometry, not by index**: for each port face, face boundary and fillet/chamfer
edge that names a `face<n>` of an affected part, the worker fingerprints the old face (surface kind, area,
centroid, normal at the centroid) and finds the new face with the same fingerprint within the document's
tolerance. One match: the reference is re-pointed and the note says so. **None or several: the reload is
refused** for that part, naming the reference and the face (*"port P1 is on shell/face12, which the revised
file no longer has"*). Overview §1g's rule — a reference that no longer lands is a refusal, never a guess.

**`R-em3d68-5d` Parts** are matched by `Part` path. A part the new file lacks: its object is listed and the
reload refused for it. A new part: offered in the same table as an import, unchecked by default.

**`R-em3d68-5e`** An accepted reload replaces the copied file, updates `Hash`, and is one undo entry.

## 6. `R-em3d68-6` — the kernel, and its absence

**`R-em3d68-6a`** With the kernel absent (overview §1d), *Import STEP…* is **shown disabled** with the
capability's tooltip and action; `convert x.step` refuses with the same sentence and exit 1. A document holding
a `Step` object is a kernel-using document: refused on open (D3), by brief 64's rule, not a second one here.

**`R-em3d68-6b`** `check` on a document with `Step` objects verifies, with the kernel present: the file exists,
its hash matches `Hash` (a mismatch is an **error** naming the file — it was edited outside circuitRF, and
face references may now mean other faces), the part path exists, and the part is a closed solid. `explain`
reports each object's file, part path, the file's unit, and the healing notes.

## 7. `R-em3d68-7` — headless: `convert x.step -o y.c3d` (D9)

**`R-em3d68-7a`** `LayoutConvert` gains `step` as a **source** format with one legal target, `.c3d`. It calls
`StepImport.Import` — the function the dialog calls — and holds no import logic (the rule `Authoring.cs` states;
the gate's comment-stripped source scan holds it, as `AuthoringCliVerbTests` does).

**`R-em3d68-7b` Defaults are the dialog's.** Auto-match (§3a); unmatched parts stay unassigned and are listed as
notes; non-solids are skipped and listed. Anything the dialog would have *asked* becomes a flag:
`--material <part>=<name>` (repeatable), `--tech <path>` (the `.c3d`'s `TechRef`; otherwise the workspace's
default, found by the walk-up `explain` reports), `--part <path>` (repeatable; import only these).

**`R-em3d68-7c` Refusals:** a target `.c3d` that exists (*"importing into an existing document is writing its
`Step` objects; see `reference topic=c3d`"*); a target outside any cell's `3d/` folder is allowed, and the note
says the result is a document belonging to no cell, which `render`, `check` and `em` all accept; an unknown
unit (§2b); zero solid parts.

**`R-em3d68-7d`** `.step` and `.stp` are recognised by extension; an unknown extension is classified by content
(the ISO 10303-21 header line), through the same classifier `check` uses to name a foreign file — never a
second rule (`LayoutConvert.DetectSource`'s comment says why).

## 8. Gates

`tests/Ui.Tests/ThreeD/StepImportTests.cs` (and `tests/Ui.Tests/ThreeD/StepConvertCliTests.cs` for the process).
Every test that needs the worker is `[KernelFact]` (brief 63): absent, it **skips with the reason**.

**Fixtures** are STEP files **written by circuitRF's own worker** from boxes and cylinders a test builds, plus
one **hand-written minimal AP214 text file** for the unit and colour cases. **No third-party STEP file enters the
repository** — no connector maker's file, no vendor name in a fixture's product name or path.

1. **One object per part:** a three-part fixture (two instances of one part) imports as three objects with
   distinct `Part` paths, the two instances congruent and at their file positions to within the kernel's
   tolerance.
2. **Units:** the same box written in mm and in inch imports to the same DBU extents; an unresolvable unit is a
   refusal naming it.
3. **Materials:** by name, by colour, unmatched — each part lands where §3a says, with the reason; a
   technology-less document gives every part *unmatched* with the empty-technology reason (§3d).
4. **Non-solid:** an open-shell part is listed, not imported, and counted.
5. **Faces:** a port on `face<n>` elaborates onto the same geometric face after a save and reopen.
6. **Reload:** a revised file that renumbers faces re-points a port by fingerprint; one that removes the face
   refuses, naming port and face; an unchanged file is a no-op.
7. **Operand:** a `Step` part as a Boolean Blank and as a Fillet target elaborates (brief 64's elaborator).
8. **Cancel** mid-read leaves the document byte-identical and `3d/` unchanged.
9. **CLI = in-process:** `convert x.step -o y.c3d` run as a process writes the same `.c3d` bytes as
   `StepImport.Import` in-process; the source scan finds no import logic in `src/Cli`.
10. **Counters:** re-opening a document with an imported part makes **zero** `read-step` calls when the cache
    holds its hash (brief 63's cache).

## 9. Owner check (Debug build)

1. *File ▸ Import ▸ STEP…* a connector body you have (any maker's file — it stays on your machine).
2. Read the units line and the table; *Map all of this colour* for the metal parts.
3. OK; move the imported parts as one selection; undo; redo.
4. Subtract one part from a drawn box; see the result.
5. Edit the source file elsewhere (or swap in a revised one); *Reload from Source*; read the note.
6. Close and reopen the document; note whether it felt immediate.

Pixels were not seen by the agent that built this brief; the completion note says so.

## 10. Scope

- **STEP only** (AP203, AP214, AP242 as OCCT reads them). IGES is deferred (overview §4).
- **No managed STEP parser**, and no STEP text interpreted outside the worker except the one header line §7d
  classifies by.
- **No product-manufacturing information** (tolerances, annotations) is imported; the note says the file carried
  some, if it did.
- **No vendor names** in fixtures, tests, notes or docs.
- Findings go in `src/Design/RESOLVED.md` (and `src/Cli/RESOLVED.md` for the verb), never `CLAUDE.md`.
