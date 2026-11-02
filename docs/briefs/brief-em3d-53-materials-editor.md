# Brief 53 — material libraries (`.cmat`) and the Materials editor

**Series:** [3D EM, third series](brief-em3d-40-overview.md) · **Tag:** `R-em3d53-n` ·
**Status:** Built 2026-09-26, uncommitted (M4–M9 took the defaults) · **Date:** 2026-09-26 · **M1–M3 and M10 decided by the owner, 2026-09-26;
M4–M9 open** (§11)
**Design note:** [`em-3d.md`](../design/em-3d.md) **rev 7** §4.1a (*material libraries*); overview §1j
(same-name materials from two technologies are merged or qualified `Name@technology` — unchanged here)
**Origin:** the owner's *3D Editor Bugs (Round 1)* list, 2026-09-26: a dialog to create custom materials a
`.c3d` can use, probably linked into the `.ctech`. The owner then asked whether custom materials should live
in a JSON file of their own, and decided (same day) that they do — a **`.cmat` named by the technology**.
**Closes:** [`brief-em3d-2`](brief-em3d-2-materials-and-bodies.md) `R-em3d2-5d` (*"the GUI table is a later,
small brief"*).
**Area:** `src/Design/Layout/` (the `.cmat` reader, technology resolution, shared validation, the cache),
`src/Design/ThreeD/` (one extracted rule), `src/Cli/` (`check`, `explain`, `reference`, `find`,
`DocumentKinds`), `src/Ui/Layout/` (the table, as a document and as a technology-editor tab),
`src/Ui/ThreeD/` (entry points), `tests/`
**Depends on:** 41, 42, 43, 46 (the *Material ▸* menu). Independent of 47–52.

---

## 0. What this brief delivers

Today a material can be made only by **writing the `.ctech` by hand**. The technology editor has no
Materials tab; the stackup picker only *chooses* among existing names; and a `.c3d` object's material must be
one of its technology's `Materials`. On a technology that defines none (anything written before brief-em3d-2)
the user can draw a box and has **no way at all** to give it a material — the dead end behind the owner's
round-1 report of a box that could never be seen. (Drawing a material-less object as wireframe is a separate
round-1 fix; this brief is the way out of that state.)

When this brief is done:

- **A `.cmat` material library**: JSON, a `FormatVersion` and a `Materials` list in **exactly** the schema of a
  `.ctech`'s `Materials` block — one record type (`TechMaterial`), one reader, unknown keys kept (§1a).
- **A technology names its libraries** — `MaterialLibraries`, relative to the `.ctech`'s own directory — and
  **nothing else does**: not the workspace, not a `.cem`, not a `.c3d`. Those reach a library only through the
  technology they already resolve, so **a technology resolves identically everywhere**. Several technologies
  may share one library (§2).
- **The technology's own list stays** (a foundry's σ for its gold is process truth). A library adds names.
- **A name defined twice with different values is a `check` error and a refusal to load**; equal values are
  fine. Nothing is shadowed (§3). A missing or unreadable library is a refusal naming the path, never an empty
  list.
- **One Materials editor**, one view and one view model: a `.cmat` opens as **its own document** (dirty, undo,
  save), and the same table is the technology editor's **Materials tab**, where library records appear with
  their source and are edited in their own `.cmat` (§5). The 3D editor reaches it through *3D ▸ Materials…*
  and *Material ▸ New Material…* (§6).
- **Headless:** `check` validates a standalone `.cmat` and a technology's references; `explain` names each
  material's source file; `reference materials` is a generated topic. **No edit verbs** (§8).
- **A generic material library ships with circuitRF** (M10, §8a): `generic-materials.cmat`, an embedded resource
  of generic metals, ceramics, semiconductors and laminates, every number with a cited source. Every shipped
  technology names it; New Workspace copies it beside the technology it copies; an existing technology gains it
  with one button.

---

## 1. Things that are not obvious, resolved here once

### 1a. One record, two spellings

```json
{
  "FormatVersion": 1,
  "Materials": [
    { "Name": "Mould compound", "Epsr": 3.9, "TanD": 0.008, "Mur": 1 },
    { "Name": "Plated gold",    "Sigma20": 3.3e7, "Alpha20": 0.0034 }
  ]
}
```

- `Materials` is a list of `TechMaterial`, read and written by the **same** System.Text.Json contract the
  `.ctech` uses for its own block. No DTO, no mapping: a record cut from a `.ctech` into a `.cmat`, or the
  reverse, reads identically. A source scan holds that no second material type exists.
- `FormatVersion` and `Materials` are **always written**, even empty — as with a `.c3d` (overview §1c), that is
  how a `.cmat` is told from another program's file of the same extension. A newer `FormatVersion` than this
  build knows is refused by name.
- **Unknown keys are kept.** `TechMaterial` gains a `[JsonExtensionData]` bag (brief 41's `Unread` pattern), so
  a newer build's property survives an older build's save — in a `.cmat` **and** in a `.ctech`, since it is one
  class. Today a `.ctech` material drops an unknown key silently; gate 1 proves no shipped file's bytes move.
- The reader is `MaterialLibraryPersistence` in `src/Design/Layout/`, beside `TechPersistence`. `.cmat` joins
  `DocumentKinds`, so `check`, `find`, `explain` and the Project Tree classify it.

### 1b. The technology's OWN list and its RESOLVED list are different things — keep them apart

This is the trap in the brief. `Technology.Materials` is what the `.ctech` **stores**, and ~a dozen readers
use it as "every material". If the loader merged library records into it, the next **save of the technology
would write every library record into the `.ctech`** — duplicating them, and freezing a copy that no longer
follows the library. So:
- `Technology.Materials` stays the persisted, own list;
- the loader fills a new `[JsonIgnore] LibraryMaterials` (each record with its source path), and a
  `ResolvedMaterials` view is own + library;
- `FindMaterial` answers from the resolved view;
- **every reader of `.Materials` is audited** and moved to `ResolvedMaterials` where it means "every material
  this technology can name" (the `.c3d` editor's `Materials`, the wire-metal and conductor lists in
  `C3dEditorViewModel.Simulate/Wires`, the stackup row's picker, `TechValidation`'s use checks) — and kept on
  `.Materials` where it means "what this file stores" (the table's own rows, persistence). A source scan lists
  the remaining `.Materials` readers and the gate fixes that list, so a new reader is a conscious choice.

### 1c. Resolution happens where it already happens — and needs the path

Named materials reach stackup entries in **one** place, `TechPersistence.ResolveMaterials`, on read. Library
resolution goes there too, before it. That needs the `.ctech`'s directory, which `Deserialize(string)` does
not have: add a path-aware load, and give the shipped-technology loader (`TechnologyCatalog`) a
resource-relative resolver (§2c). `TechnologyCache` keys technologies by path and shares one instance across
workspaces, which is exactly why a technology-relative path is the right anchor: the cached instance is
correct for every consumer.

### 1d. "Refusal to load" must not lock the user out of the fix

A technology whose libraries conflict (§3) or cannot be read (§2b) **fails to resolve**: `TechPersistence`
throws a typed `MaterialLibraryException` naming the path (and, for a conflict, the name, both sources and
both values), and `TechnologyResolver` turns it into the non-fatal diagnostic it already produces for a
corrupt `.ctech` — a layout falls back as it does today, a `.c3d` elaboration refuses with that sentence, a
`.cem` run refuses. **But the technology editor must still open that file**, or nobody can fix it: it loads
the `.ctech`'s own JSON without resolving libraries, shows the refusal in its banner, and lists each library
row with its load state. A `.cmat` document opens on its own and does not need its technologies.

### 1e. Null is "not stated", and a blank field must stay null

Every `TechMaterial` property is nullable and **null means NOT STATED** (TechModel.cs, above `TechMaterial`):
the shipped wire metals state `Sigma20`, `Alpha20` and `DensityKgM3` and no `Epsr`. In the editor an empty
field **is** null and is written as an omitted key, never `0` or `1`; clearing `Epsr` is a meaningful edit (it
can turn an *ambiguous* material into a conductor, §4); the εr tensor is an *Anisotropic* check box revealing
xx / yy / zz, and unchecking it removes `EpsrTensor` rather than writing `[1,1,1]`.

### 1f. An untouched value is never rewritten

The stackup row formats σ to three significant figures and parses it back, which is why
`ConductorMaterials.Match` needs a tolerance. The editor must not repeat that: **commit only the fields the user
edited**; a field displayed and not touched is copied bit for bit. Displays use a round-trip format; entry
goes through the app's existing number parser (`41e6`, `41M`).

### 1g. `@` is reserved, and `Air` is special by name

`Gold@gaas-mmic` is how overview §1j qualifies a name across technologies. A name containing `@` would be
indistinguishable from a qualified one, so the editor refuses it and `check` reports
**`material.reserved-character`** (error) in a `.ctech` or a `.cmat`; verify rather than assume that no shipped
or example file trips it. §1j itself is **unchanged** — it is a rule between technologies, and this brief
changes nothing between technologies.
`C3dElaborator.Role` makes a material named `Air` (case-insensitive) role *Air* whatever it states; the editor
marks that row, and renaming to or from `Air` warns that the role changes with the name.

### 1h. What may be edited

A `.cmat` or `.ctech` in a referenced workspace not toggled editable, or a read-only file, opens
**read-only** and says why, naming the file. The walk that decides "may edit" is the one the Project Tree
already uses for referenced workspaces — find it; do not write a second.

---

## 2. `R-em3d53-1` — the reference: a technology names its libraries

**`R-em3d53-1a` The field.** `Technology` gains `MaterialLibraries: List<string>?` — paths relative to the
`.ctech`'s own directory, omitted when null, additive, no `.ctech` `FormatVersion` bump. Order is display
order only; it is **not** a precedence (§3 makes precedence unnecessary). Absolute paths are accepted and
`check` warns about them, as it does for other absolute references.

**`R-em3d53-1b` A missing or unreadable library** — no file, bad JSON, newer `FormatVersion`, a path that is a
directory — is a **refusal naming the path and the `.ctech` that named it** (§1d). Never an empty list: a
technology that silently lost its libraries would give every object naming one an *unknown material*, or
worse, a stackup entry would fall back to its own numbers and move a planar answer with nothing looking wrong.

**`R-em3d53-1c` Shipped technologies name the shipped generic library, and nothing else** (M10, §8a). It is an
embedded resource beside the technologies and resolves the same way (resource-relative, §1c). `WorkspaceCreate`
copies a shipped technology into `tech/`; it must then copy every library that technology names beside it, so
the copied `.ctech`'s relative path still resolves on disk. A workspace's copy is **its own file from then on**:
a later release's generic library never reaches it silently (§8a).

**`R-em3d53-1d` Nothing else names a library.** Not the `.cws`, not a `.cem`, not a `.c3d`. A `.c3d` and a
`.cem` reach a library only through the technology they already resolve — for a `.cem`, through its layout's
technology: its stackup entries, bodies and wire metals may name library materials, resolved on read like any
named material, so the planar solvers and the 3D generator see them identically. For a `.c3d`, instances keep
overview §1i: a layout instance resolves through **its own** technology, and so through that technology's
libraries.

**`R-em3d53-1e` Every reference walker learns the new reference** (`.ctech` → `.cmat`): workspace archive,
technology Save As (re-pointing relative paths, as layout Save As does), revision control's tracked-file set,
`find`, `check`, `explain --ref`. Brief 41 had the same obligation for `.c3d`; its list of walkers is the
starting point.

**`R-em3d53-1f` Live changes.** `TechnologyCache.TechnologyChanged` is the existing live-refresh seam. A `.cmat`
edited in its document installs a live override for the library, and the cache re-resolves **every cached
technology that names it**, raising `TechnologyChanged` for each. **Check first that an open `.c3d` listens at
all**: `C3dEditorViewModel` is handed a `TechnologyCache`, but nothing under `src/Ui/ThreeD` subscribes to a
change. If an εr edited in the technology editor does not already move an open `.c3d`, that is a defect to fix
here, library or no library.

---

## 3. `R-em3d53-2` — one name, two definitions

Within **one** technology the sources are its own list and each library it names.

- **Equal resolved values** (compared as `C3dElaborator.Materials` compares them today): fine. One material;
  `explain` lists every file that defines it.
- **Different values: a `check` error (`material.conflict`) and a refusal to load** (§1d), naming the material,
  each source file and each value. **Never silent shadowing** — a technology whose `Gold` could be either of two
  conductivities has no defined answer, and picking one would give a wrong σ with nothing looking wrong.
- Duplicates **inside** one file keep today's rule (`tech.material.duplicate`, an error that does not stop the
  load) — this brief does not tighten an existing rule.

Between **two technologies** (a `.c3d` holding a GaAs die and a ceramic package) overview §1j applies exactly as
built: merge equal values, otherwise qualify `Name@technology` with a note.

---

## 4. `R-em3d53-3` — the role a material implies, in one place

`C3dElaborator.Role` holds the rule: explicit Role wins; `Air` by name; σ only → conductor; εr only →
dielectric; both → refuse and ask for a Role; neither → refuse. Extract the material half to
`src/Design/ThreeD/C3dMaterialRole.cs`:

```csharp
public enum C3dImpliedRole { Conductor, Dielectric, Air, Ambiguous, Unstated }
public static C3dImpliedRole Implied(TechMaterial m);
```

The elaborator calls it and keeps its own refusal sentences; the editor's Role column calls it. A source scan
proves the elaborator no longer tests `Sigma20`/`Epsr` itself. **No elaboration result changes** (gate 7).

---

## 5. `R-em3d53-4` — the Materials editor: one table, two homes

`MaterialsTableViewModel` (`src/Ui/Layout/`) edits **one** `List<TechMaterial>` it is handed and raises one
"changed" per committed gesture. `MaterialsTableView` is its UserControl.

**Columns** (every numeric one blank-able, §1e): Name · Source (*derived*: this file, or the `.cmat` a row came
from) · Role (*derived*, §4, tooltip gives the reason) · εr (+ *Anisotropic* xx/yy/zz) · tanδ · μr · σ₂₀
(S/m) · α₂₀ (1/K) · Colour (M6) · Used by (*derived*: stackup entries and bodies of the technologies in view,
and open `.c3d` objects; tooltip lists them). A collapsed **Thermal — not read by any solver yet** section
carries `ThermalK`, `DensityKgM3`, `SpecificHeat` (M7). `SigmaVsTemp`/`ThermalKVsTemp` are placeholders no
solver reads: shown as *"has a σ(T) table — preserved"*, never edited, kept byte for byte.

**Actions:** *Add* (`Material1`, `Material2`… stating nothing, so Role reads *"states nothing — give it σ₂₀ or
εr"*), *Duplicate*, *Rename*, *Delete*. Validation comes from one function,
`MaterialValidation.Validate(IReadOnlyList<TechMaterial>)`, extracted from `TechValidation`'s material-self
rules (duplicate, invalid tensor, temperature tables, reserved character) so a `.ctech` and a `.cmat` are
checked by the same code; **no rule lives in a view model** (source scan).

**`R-em3d53-4a` Home 1 — a `.cmat` is its own document.** `MaterialsDocument`: `IUndoableDocument`,
`IFileBackedDocument`, dirty mark, Save, whole-list snapshot undo as `TechSnapshotCommand` does for a
technology. The Project Tree shows `.cmat` files where it shows `.ctech` files. Its header names the
technologies in the workspace that reference it (the `R-em3d53-1e` walk), since an edit here changes all of them.

**`R-em3d53-4b` Home 2 — the technology editor's fifth tab, *Materials*.** Inside `TechEditorViewModel`'s
existing snapshot undo; the header counts its problems (`Materials (2)`).
- The technology's **own** rows are edited in place, on the technology's undo stack.
- **Library rows are shown below them, read-only, grouped by source file**, each group with *Open Library*,
  which opens (or activates) that `.cmat`'s document and selects the row. **They are not edited through the
  technology** (M4): an edit to a shared library is an edit to every technology that names it, and it belongs on
  that file's undo stack, dirty mark and Save — not smuggled into one technology's snapshot, where Undo in one
  tab would revert a change another technology already depends on.
- *Add Library…* (pick an existing `.cmat`) and *New Library…* (write an empty `.cmat` beside the `.ctech` —
  `MaterialLibraryCreate.Create` in `src/Design/Layout/` — and add its relative path) edit the technology's
  `MaterialLibraries` as **one** technology undo entry; the new file is written at once, as New Cell writes its
  file. *Remove Library* removes the reference only, never the file.
- *Add Generic Materials* (shown while the technology does not already name the generic library) copies the
  shipped `generic-materials.cmat` beside the `.ctech` and adds the reference, as one technology undo entry
  (§8a, `R-em3d53-8d`).
- Add `TechProblemArea.Materials`: the material-**self** problems and the new `material.conflict` /
  missing-library problems move there; the **use** problems (`unknown`, `missing-property`, `partial`,
  `disagrees`) stay on Stackup, where the fix is made. Ids and text of existing rules are unchanged, so `check`'s
  output for them is unchanged (gate 6). The stackup row's picker gains *Edit Materials…*.

**`R-em3d53-4c` Rename and delete across files (M5).** There is no cross-document undo in circuitRF, and this
brief does not invent one. So a change that spans files is **one entry on each file's own stack**, applied
together and named alike (*"Rename material Gold → Plated gold"*):
- **Rename** in a `.cmat`: the library entry on its stack; each **open** technology document that names the
  library and references the material (stackup entry, body) one entry on its stack; each **open** `.c3d` whose
  objects, face boundaries or wires name it one `C3dEdit`. **Unopened** technologies and `.c3d` files are
  **listed, not rewritten**, and `check` then reports them as unknown material — which is the truth. Undoing
  one side alone leaves the same visible, reported state. A rename of a technology's own row does the same with
  the technology as the first stack.
- **Delete** refuses a material in use and lists every use (the same three kinds, open and on disk; files
  through `find`'s bounded walk). Unused, it is one entry on its own stack.

---

## 6. `R-em3d53-5` — from the 3D editor

All enabled only with a 3D document active (overview §1m), tooltips saying so.

- **3D ▸ Materials…** opens the Materials tab of the active `.c3d`'s technology (opening the technology
  document if it is not open). Instance technologies are listed in the dialog's footer read-only, each with
  *Open Technology* (M8): editing a child's process from its parent is editing another design.
- **Material ▸ New Material…** (right-click, and the last item of the 3D toolbar's material combo and of
  Properties ▸ Material) opens a small **modal** dialog hosting the same table on one new row, with a *Save to*
  selector: the technology's own list, or one of its libraries (default: the first library if the technology
  names one, else the technology — M9). *OK* commits the row to that file's document as **one** entry
  (opening it in the background if needed; `OpenTechnologyDocumentBesideLayout` is the precedent) and gives the
  right-clicked objects the material as **one** `C3dEdit`. Nothing reaches disk until each document is saved;
  the dialog's footer names what became dirty.
- The `.c3d`'s `Materials` list (toolbar combo, Properties, *Material ▸*) is the technology's
  `ResolvedMaterials` (§1b). `SyncCurrentMaterial` keeps working after a delete (it already falls back to the
  first).
- A technology that **fails to resolve** (§1d) leaves the list empty and the status line carrying the refusal
  sentence, with *Open Technology* to fix it.

---

## 7. `R-em3d53-6` — elaboration

- `C3dElaborator`, `C3dValidation`'s `isKnownMaterial` and `C3dProblemAssembly.FaceBoundaries` already go
  through `tech.FindMaterial`, which now answers library materials (§1b); nothing else changes in the lookup.
- `MaterialSources` names the **file**: `technology 'gaas-mmic'` or `technology 'gaas-mmic' via library
  'tech/lab-materials.cmat'`. `Em3dLayoutSolids.MaterialSources` does the same for a `.cem`.

---

## 8. `R-em3d53-7` — headless

- **`check <file.cmat>`** runs `MaterialValidation` on a standalone library. **`check`** on a `.ctech`,
  workspace, `.c3d` or `.cem` also reports a missing or unreadable library (naming the `.ctech` that names it), a
  `material.conflict` with both files and values, and an absolute library path (warning).
- **`explain`** on a `.c3d` already walks materials (`WalkMaterials`); each step now names the **source file**
  that answered, and for a merged name every file that agreed. `explain` on a `.ctech` lists its libraries and
  what each contributed; on a `.cmat`, the technologies in the workspace that name it.
- **`reference materials`** — a generated topic from `MaterialLibraryPersistence`'s types, like `technology` and
  `3d-view`; `reference technology` gains `MaterialLibraries` with no page written. The MCP `reference`
  resource serves it as it serves the others.
- **`find`** lists `.cmat` files and which `.ctech` names each.
- **No edit verbs, and no `new` noun.** A material is authored by writing the `.cmat`; a library is referenced by
  writing one path into the `.ctech` (CLAUDE.md, *the format is the contract*). An empty `.cmat` is two keys,
  which `reference materials` states — there is no hidden second edit for a creation function to protect,
  which was the reason `new cell`/`new workspace` earned theirs.
- The copy-between-files *Import/Export* idea from an earlier draft is **dropped**: a library shared by
  reference makes copying the wrong default, and cut-and-paste of one record already works because it is one
  schema.

---

## 8a. `R-em3d53-8` — the shipped generic library (M10, decided)

**`R-em3d53-8a` The file.** `src/Design/resources/materials/generic-materials.cmat`, an `EmbeddedResource`
beside the shipped technologies (CLAUDE.md: moving or adding such a class without its `EmbeddedResource` item
leaves it enumerating nothing, silently — gate 14 counts the records). Contents, one record each, generic names
only:
- **metals** — Gold, Aluminium, Copper, Silver (σ₂₀, α₂₀, density);
- **ceramics** — alumina 96 %, alumina 99.6 %, aluminium nitride, fused silica;
- **semiconductors and films** — high-resistivity silicon, GaAs, silicon nitride;
- **laminates and films** — PTFE (unfilled), FR-4 (generic epoxy-glass), polyimide.
`Air` is not in it: it is special by name (§1g).

**`R-em3d53-8b` Every number has a cited source.** `TechMaterial` gains `Source: string?` — free text saying where
the values come from and, for a dielectric, **the frequency they are stated at** (every backend receives constant
εr and tanδ, §12, so a laminate's number means nothing without it). Additive, nullable, omitted when null, shown
and editable in the table like any field. Every record in the generic library states it (gate 14). Sources are
handbooks, standards bodies or peer-reviewed papers — **never a manufacturer's datasheet**, whose name would put a
vendor into the repo (§12). Where the literature gives a range (FR-4 above all), the record takes a stated
representative value and the `Source` text says it is one.

**`R-em3d53-8c` No conflict with any shipped technology, by construction.** The shipped technologies already
define Gold, Aluminium, Copper, Silver, GaAs and FR-4. For every name a shipped technology defines, the generic
record carries **field-for-field identical values** (today: σ₂₀ 4.10e7 / 3.77e7 / 5.80e7 / 6.30e7 S/m with their
α₂₀ and density; GaAs εr 12.9, tanδ 0.0006, μr 1; FR-4 εr 4.4, tanδ 0.02, μr 1), so §3 merges them and nothing
moves. If a cited source disagrees with a shipped technology's number, **that is reported to the owner, not
fixed here** — changing a shipped technology's value moves answers (gate 7). Gate 14 asserts every shipped
technology resolves against the library with no `material.conflict`.

**`R-em3d53-8d` Who names it.**
- **Every shipped technology** gains `"MaterialLibraries": ["../materials/generic-materials.cmat"]` (or whatever
  relative spelling the resource layout makes true), which replaces the earlier "shipped technologies name no
  library" rule. Only names are added: no existing record changes, so gate 7's answers stay byte-identical.
- **New Workspace** (GUI and `circuitrf new workspace`, one `WorkspaceCreate`) copies the library into the
  workspace's `tech/` folder beside the copied technology and writes the `.ctech`'s reference to match.
  `--tech none` creates no technology and therefore no library; it says nothing more than it does today.
- **An existing technology** — one copied before this brief, or written by hand — gains it through the
  technology tab's **Add Generic Materials** (`R-em3d53-4`): copy the shipped library beside the `.ctech` (refusing
  if a different file of that name is already there, naming it) and add the reference, as **one** undo entry on
  the technology; Undo removes the reference and leaves the file, as *New Library…* does. The 3D editor's
  *3D ▸ Materials…* shows the same button when the active technology names no library.
- **A copy is frozen.** Once in a workspace it is the user's file: editable, archived, tracked by revision
  control, and never rewritten by an upgrade — a result must not move because the application was updated.
  `check` does **not** compare it with the shipped version.

---

## 9. Gate

`tests/Ui.Tests/Em3d/MaterialLibraryTests.cs` (format, resolution, CLI) and `MaterialsEditorTests.cs` (view
models), plus the named existing classes. Run those classes and `Firewall.Tests` — never the suite.

1. **Round trip.** Every shipped and `examples/`/`testdata/` `.ctech` loads and saves byte-identical with the
   `[JsonExtensionData]` bag present. A `.cmat` with an unknown key round-trips it. A record cut from a `.ctech`
   into a `.cmat` deserialises equal (source scan: one material type).
2. **A technology save never writes library records** (§1b): load a technology naming a library, save it; its
   bytes are unchanged and contain no library record. The `.Materials` reader list (§1b) matches the fixed
   list in the test.
3. **Null stays null; untouched stays untouched** (§1e, §1f): a material with only σ₂₀ writes exactly `Name`,
   `Sigma20`; clearing `Epsr` removes the key; *Anisotropic* off removes `EpsrTensor`; with
   `Sigma20 = 41234567.891` and only `TanD` edited, `Sigma20` saves bit-identical.
4. **Resolution** (§2, §3), one fixture each: library only; own + library equal (loads; `explain` names both);
   own + library different (refused: `check` `material.conflict` with both files and values, `TechnologyResolver`
   diagnostic, `.c3d` elaboration refusal); two libraries different (same); missing library (refusal naming
   path and `.ctech`); **the same technology resolved from two workspaces is identical**; the technology editor
   still opens the refused file and shows the refusal.
5. **Role column = elaborator** for the five `C3dImpliedRole` cases; source scan that the elaborator's role code
   no longer reads `Sigma20`/`Epsr`.
6. **Validation is shared; existing `check` text is unchanged:** `@` refused by the editor and reported as
   `material.reserved-character` for both file kinds; no shipped file trips it; `check`'s text output on a
   fixture where every pre-existing material rule fires is **byte-identical** after the area move.
7. **Nothing moves.** `Em3dGeneratorDumpTests`, the Em3d goldens and brief-em3d-2's planar gate are
   byte-identical; every shipped technology names exactly the generic library and its own `Materials` records
   are unchanged (asserted against the pre-brief bytes of that block).
8. **The `.cem` reaches a library through its technology, extraction only.** A fixture technology naming a
   `.cmat`, whose dielectric entry and body name library materials: `PlanarExtractor`'s problem carries the
   library's εr and `Em3dGenerator`'s carries the body's values.
9. **Live** (`R-em3d53-1f`): editing a `.cmat` document re-resolves exactly the cached technologies that name it (counter),
   and a headless `C3dEditorViewModel`'s next elaboration carries the new value; the same for an edit in the
   technology tab.
10. **Tab behaviour** (`R-em3d53-4b`): library rows are read-only in the technology tab; *Open Library* activates that
    `.cmat` document with the row selected; *New Library…* writes the file and adds one undo entry to the
    technology; Undo removes the reference and leaves the file.
11. **Cross-file rename and delete** (`R-em3d53-4c`): rename in a library rewrites one open technology's stackup entry and
    one open `.c3d` object — **one entry on each of the three stacks** — lists one unopened `.ctech` and one
    unopened `.c3d` without touching their bytes; delete in use is refused with the same list.
12. **Walkers** (`R-em3d53-1e`): an archive of a fixture workspace carries the library; technology Save As re-points it;
    `WorkspaceCreate` from **each real shipped technology** copies it and the generic library, and the copied
    `.ctech` resolves from disk with the same materials as the embedded one.
13. **CLI as a process:** `check`/`explain` on a `.cmat`, a conflicting `.ctech` and a `.c3d` on it; `reference
    materials` exists and lists every `TechMaterial` field.
14. **The generic library** (`R-em3d53-8`): the embedded resource loads with the expected record count (not zero);
    every record states `Source`, and every dielectric's `Source` states a frequency; every shipped technology
    resolves against it with no `material.conflict`, and every name shared with a shipped technology is
    field-for-field identical; the generic library's text passes the repo's vendor-name scan; *Add Generic
    Materials* on a technology with no library copies the file, adds the reference as one undo entry, and refuses
    on a same-named different file.

---

## 10. Owner check (pixels not seen)

In the **Debug** build:
- on a workspace technology with no materials, draw a box, right-click ▸ *Material ▸ New Material…*, give it εr,
  *OK*: the technology tab appears dirty, the box takes the material and is drawn;
- in the technology editor's Materials tab, *New Library…*; add a material there; see it in the `.c3d`'s combo
  with its source;
- open the `.cmat` from the Project Tree: the same table, as a document with its own undo and Save;
- add `Gold` to the library with a different σ from the technology's: see the refusal (and `circuitrf check`'s
  `material.conflict`); fix it in the tab without being locked out;
- give a material both σ₂₀ and εr: Role reads *ambiguous*; Simulate refuses until the object states a Role;
- rename a library material used by an open `.c3d` and a stackup entry; undo each file separately;
- name a library material on a stackup entry and see a `.cem` on that technology use it;
- create a new workspace from a shipped technology: `tech/` holds `generic-materials.cmat`, and a `.c3d` box can
  be given alumina with no further step; on an older workspace, *Add Generic Materials* does the same.

---

## 11. Decisions

| # | Decision | Outcome |
|---|---|---|
| M1 | Where custom materials live | **DECIDED (owner, 2026-09-26): a `.cmat` library** in the technology's material schema; the `.ctech` keeps its own list (§1a) |
| M2 | Who names a library | **DECIDED: only the `.ctech`**, relative to its own directory; never the workspace, `.cem` or `.c3d` (§2) |
| M3 | Same name, different values | **DECIDED: `check` error and refusal to load**, never shadowing; equal values fine; §1j between technologies unchanged (§3) |
| M10 | A shipped generic-materials `.cmat` | **DECIDED (owner, 2026-09-26): ship it in this brief** — metals, alumina, aluminium nitride, fused silica, high-resistivity silicon, GaAs, silicon nitride, PTFE, FR-4, polyimide; every number cited; named by every shipped technology and copied by New Workspace (§8a) |

Open, with the brief's default:

| # | Decision | Brief's default | Alternatives |
|---|---|---|---|
| M4 | **Editing a library row from the technology tab** | **Open it**: read-only in the tab, *Open Library* edits it in its own document (`R-em3d53-4b`) | edit through, putting the `.cmat` change on its own stack while the tab shows it live |
| M5 | **Rename across files** | **One entry per open file's own stack; unopened files listed, not rewritten** (`R-em3d53-4c`) | also rewrite unopened files on disk (no undo reaches them); no Rename (Duplicate + Delete) |
| M6 | **A display colour on a material** | **In, optional** — `Color` (`#rrggbb`), nullable, additive; the 3D scene uses it when stated, today's palette otherwise | out |
| M7 | **Thermal fields** | **Shown collapsed, editable (scalars only)**, labelled *not read by any solver yet*; tables preserved | hidden until F3 |
| M8 | **An instance technology's materials from the parent** | **Read-only**, with *Open Technology* | editable from the parent |
| M9 | **Where *New Material…* from the 3D editor saves** | **The technology's first library if it names one, else the technology's own list**, with a *Save to* selector | always the technology's own list; always a library (creating one if none) |

---

## 12. Scope

- **No `.cws`, `.cem` or `.c3d` reference to a library** (M2).
- **No `.cem` material override.** A `.cem`'s materials come from its layout's technology; a setup that wants
  another substrate edits or copies the technology. An override on the setup is a later brief if wanted.
- **No frequency-dependent dielectric model.** Every backend receives constant εr and tanδ (`Em3dMaterial`).
- **No σ(T) or k(T) table editing** (placeholders, §5).
- **No change to any answer:** goldens, dumps and the planar gate unchanged (gate 7).
- **`WireMaterials.All` stays in code** for kernel W (brief-em3d-2 `R-em3d2-5b`).
- **No edit verbs, no `new` noun** (§8).
- **Commercial names stay out**, including material names, examples, fixtures and the generic library's
  `Source` citations: generic names only, and no manufacturer's datasheet cited (§8a).
- **On completion, findings go in `src/Design/RESOLVED.md`, `src/Ui/RESOLVED.md` and `src/Cli/RESOLVED.md`
  §brief-em3d-53**, never `CLAUDE.md`. `em-3d.md` is already at rev 7 for this decision; keep it in step with
  anything the build changes. Doc sources are edited; DocGen is not run.
