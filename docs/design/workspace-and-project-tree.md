# circuitRF — Workspace & Project Tree Design

**Status:** Steps 1–7 done · project-tree arc complete · §5A added · **Date:** 2026-07-28 · **Phase:** 6g (post-symbol-editor)

Specifies the **workspace model** (filesystem structure), the **Project Tree** (the tree view that reads it),
the **cell reference model** (how a placed component resolves to a cell's primary symbol — the linkage that
unblocks the deferred symbol-editor live-update and cell-driven open), **foreign documents** (§5A — files
open from outside the current workspace), and the **cell-parameter editor**.
Refines `project-file-formats.md` (which this updates for `.ccell`, the cell subfolder layout, and the
filesystem-is-truth membership model). Companions: `symbol-editor.md` (the `.csym` consumer), `ui-design.md`
§1/§2.2 (hierarchy, palette), `color-themes.md` (System.Warning role), `src/Ui/CLAUDE.md`.

**The governing principle:** **the filesystem IS the workspace.** Membership, cells, and views are determined
by *scanning the folder structure*, not by a manifest list. A `.cws`/`.ccell`/`.clib` records configuration
and primacy, never "what files belong" — that is read from disk. This cannot drift from reality, and the user
may edit structure in Finder/Explorer (at their own risk, below).

---

## 1. On-disk structure

### 1.1 Workspace
A **workspace is a filesystem folder**. The **root folder name is the workspace name**. At the root:
- exactly one **`.cws`** file — literally named `.cws` (no stem, no prefix); only one permitted per workspace.
  It records Dock layout, referenced libraries, Known Files, and the active color scheme (§5). It does **not**
  list members.
- one **sub-folder per cell** (each a cell folder, §1.2).
- **arbitrary user folders** holding any `.cdd`, `.ccolor`, or other files the user organizes however they
  like. The tree surfaces these by extension (§3).

```
MyWorkspace/                     ← workspace name = folder name
   .cws                          ← Dock layout, library refs, known files, color scheme
   AmpStage/                     ← a cell folder (§1.2)
   Bias/                         ← a cell folder
   displays/                     ← arbitrary user folder
       sweep.cdd
   themes/
       midnight.ccolor
```

### 1.2 Cell
A **cell is a filesystem folder**. The **folder name is the cell name**. It contains:
- exactly one **`.ccell`** file at its root (only one permitted) — records the cell's **parameters + defaults**
  and which view files are **primary** (§2). The cell folder name, not the `.ccell` filename, is the name.
- three **view sub-folders** (always present, created with the cell): **`schematic/`**, **`symbol/`**,
  **`layout/`**. `layout/` exists but is unused in v1 (created empty; layout is v2).
- inside each sub-folder, **any number** of view files: `schematic/*.csch`, `symbol/*.csym`,
  `layout/*.clay`. A cell may have many schematics, many symbols.

```
AmpStage/                        ← cell name = folder name
   .ccell                        ← params + defaults + which view is primary (per type)
   schematic/
       amp_v1.csch
       amp_v2.csch               ← (one of these is primary, recorded in .ccell)
   symbol/
       amp.csym                  ← (sole symbol ⇒ primary by default, §2)
   layout/                       ← present but empty in v1
```

A cell is authored **incrementally** — any view may be absent while the user works (a cell with a `.ccell`
but no `.csch` or `.csym` yet is normal and **not** an error, §3.4).

### 1.3 Library
A **library is a folder of cell folders** plus a **`.clib`** manifest (lightweight: name, version, metadata —
**not** a cell list; cells are discovered by scanning, like everything else).
```
StdLib/
   .clib                         ← name, version, metadata (NOT a cell list)
   Resistor/   (cell folder)
   Capacitor/  (cell folder)
```
- **Built-in libraries ship as real cell folders** with their own `.clib` and a `.ccell` per cell — the
  standard library dogfoods the format. Built-in cells typically have **one symbol + one schematic, no
  layout** (they are generic/broad).
- A user library is just a folder of cells the workspace references (§5).

### 1.4 Naming — cross-platform-safe character set
Workspace, Library, Cell, Schematic, Symbol, and Layout names are restricted to the **intersection of
characters legal in filenames on Windows, Linux, AND macOS**, so these elements move freely between machines.
- **Disallowed:** `< > : " / \ | ? *`, control chars (0x00–0x1F), names ending in space or dot, and the
  Windows reserved names (`CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9`, `LPT1`–`LPT9`). Case-insensitive
  uniqueness within a folder (Windows/macOS are case-insensitive).
- **Enforced at create/rename inside circuitRF** (reject with a clear message — `NameValidator` helper). The
  rule is documented so a user editing the filesystem directly stays within it.

---

## 2. `.ccell` — parameters + primacy (the cell's interface record)

The `.ccell` is the cell's **interface + primacy** record (System.Text.Json, enum-as-string, format_version
reject-on-mismatch, `Id` never persisted — same conventions as all formats):
- **Parameters:** the list the cell **declares** — name, default expression, unit, dimension, show-on-schematic
  default. This is the cell's *interface* (what instances accept); it is the on-disk form of what
  `ComponentTypeRegistry` hardcodes today (§6 reconciles them). Edited by the **cell-parameter editor** (§7).
- **Primary view per type:** `primary_schematic`, `primary_symbol`, `primary_layout` — each a **filename
  relative to its view sub-folder** (e.g. `primary_symbol: "amp.csym"`), never a path or index.
- **TestBench flag:** `IsTestBench` (a cell whose schematic carries analyses/measurements — the role moves
  here from the old `.csch` metadata, `project-file-formats.md`).

**Primacy resolution rules:**
- **Sole-file-implies-primary:** if a view sub-folder contains exactly **one** file, that file **is** the
  primary regardless of what `.ccell` says (applies to symbol, schematic, and layout independently).
- **Named primary present:** if `.ccell` names a primary and that file exists, it is primary.
- **Named primary MISSING (the contradiction):** if `.ccell` names a primary that is **not present**, the
  cell is flagged (System.Warning, §3.4) — a blatant contradiction surfaced to the user.
- **No primary, multiple files:** if a sub-folder has multiple files and `.ccell` names none, there is **no
  primary** (not an error — the user hasn't chosen; the cell just has no primary of that type yet).

---

## 3. The Project Tree

Reads the workspace folder structure and renders it as a disclosure tree. **Refresh: manual + on-focus** for
v1 (re-scan when the tree regains focus or via a Refresh command). *(A `FileSystemWatcher` for live updates
is planned for a future version — documented now, not built in v1.)*

### 3.1 Structure shown
- The **workspace** root, its **cells** (each disclosing `schematic/`/`symbol/`/`layout/` → their view files),
  its **arbitrary user folders** and the `.cdd`/`.ccolor`/other files within (surfaced by extension),
  **referenced libraries** (each its own sub-tree of cells), and **Known Files** (§5).
- **Empty view sub-folders show no disclosure triangle** (a cell with no symbols yet shows no `symbol/`
  expander). Don't render empty expanders.
- **Ordering is customizable** (user-arrangeable) **and the tree is filterable** (§3.3).

### 3.2 Visual states (color + weight)
- **Primary view files** (the resolved primary `.csch`/`.csym`/`.clay`) render **bold** when their sub-folder
  is disclosed.
- **Broken references → System.Warning color + italics**, with a **tooltip giving the reason**:
  - a **referenced library** whose path cannot be resolved;
  - a **Known File** whose path cannot be resolved;
  - a **cell whose `.ccell` names a primary view that is missing** (the contradiction, §2) — tooltip e.g.
    *"Primary symbol reference broken: amp.csym not found."*
- **Not a warning:** a cell that simply **lacks** a view (no symbol/schematic yet) renders normally — the
  user may still be authoring it. Only the *contradiction* (named-but-absent primary) warns.
- Tooltips show the **relative path** of a file node.

### 3.3 Filtering — independently-toggleable categories
The filter is a **set of independently-toggleable categories**, not a flat radio list (so any combination is
reachable from one mechanism; "All" = every category on):
**Cells · Libraries · TestBenches · Data Displays (`.cdd`) · Color Themes (`.ccolor`) · Known Files ·
Workspace File-System (arbitrary folders/files under the workspace)**.
- "Only Cells" = Cells on, rest off. "Cells + Libraries" = those two on. "Only TestBench" = Cells filtered to
  `IsTestBench`. "All" = everything on. The owner's enumerated cases are all expressible as category subsets.

### 3.4 Context menus
- **On the workspace or a library node:** **New Cell** (creates a sibling cell folder + `.ccell` + the three
  empty view sub-folders, then opens it for authoring). *(New Cell is a workspace/library-level action — it
  creates a cell, so it lives on the container node, not on a cell.)*
- **On a cell node:** **New Schematic**, **New Symbol**, **New Layout** (each creates a new view file in the
  appropriate sub-folder and opens a document tab to author it). **New Layout is disabled/greyed until layout
  ships (v2).** Also **Edit Parameters** (opens the cell-parameter editor, §7).
- **On a view file (`.csym`/`.csch`/`.clay`):** **Make Primary** (writes the choice into `.ccell`),
  **Reveal in Explorer/Finder** (opens the OS file manager pointing at the file).
- **Reveal in Explorer/Finder** is available on file nodes generally.

### 3.5 Double-click behavior
- **Cell node** → opens the **cell-parameter editor** (§7).
- **Schematic file** → opens that schematic in a workspace content tab (or activates it if already open).
- **Symbol file** → opens that symbol in the Symbol Editor (tab or window; activates if already open).
- **Layout file** → deferred (v2).
- **`.cdd` / `.ccolor` / unknown types** → attempts to open; **deferred (no-op) for file types we have no
  viewer for yet** (e.g. `.cdd` until Phase 7's data display). No crash, no error — just nothing yet.

---

## 4. The cell reference model (the linkage that unblocks live-update)

This is the load-bearing addition: how a placed schematic component resolves to a cell and its primary symbol.

- **A placed component references its cell by RELATIVE PATH** (relative to the referencing `.csch`/workspace).
  This applies equally to a cell in the workspace and a cell from an **external referenced library** (the
  reference is **library-qualified** by virtue of the path resolving into that library's folder).
- **Same-named cells in different libraries are allowed** — disambiguated because each resolves via its own
  relative path into its own library folder.
- **Glyph/pin resolution chain (replaces the static `SymbolKind → BuiltInSymbols` path):**
  `instance → (relative path) → cell folder → .ccell → primary .csym → primitives + pins`.
  The schematic reads the cell's `.ccell` to learn the **primary symbol**, loads that `.csym`, and renders +
  derives pins from it.
- **Broken reference → "Not Found" glyph.** If the relative path does not resolve (file/folder moved or
  deleted), the schematic renders a distinct **"Not Found" glyph** on that instance. The user must **re-point
  the reference** to fix it ("unbreak"). *(This is distinct from "cell resolves but its primary `.csym` is
  missing" — that is the cell's `.ccell` contradiction, surfaced in the tree as System.Warning, §3.4; on the
  canvas such an instance falls back to the plain-rectangle stand-in, `project-file-formats.md`.)*
- **Changing the primary symbol updates the schematic live.** When the user sets a different primary `.csym`
  (via Make Primary, §3.4), every schematic instance of that cell re-renders to the new symbol.
- **Primary-symbol change is a RISKY user operation (surfaced, not blocked).** Different `.csym` files in a
  cell may declare **different pin counts/positions**. When the primary changes, pins re-resolve from the new
  `.csym`, and **any wire that no longer meets a pin shows as unconnected** (the existing positional
  connectivity model handles this — no auto-rewiring; that is the deferred Option B, `symbol-editor.md` §6).
  The risk is made visible (dangling wires + re-rendered glyph), and the user accepts it.

### 4.1 Rename / move at the user's risk
**Editing the cell/library filesystem directly *is* editing the cell/library definition.** If the user
renames or moves a cell folder in Finder/Explorer and thereby breaks a referencing schematic, that is **their
responsibility** — the schematic shows "Not Found" until re-pointed. circuitRF does **not** track a
rename-surviving cell ID (consistent with the `Id`-never-persisted rule). *(Renaming a cell from within the
tree may, in a future version, offer to fix references; v1 does not — Finder-edits are at-risk by design.)*

### 4.2 The three missing-symbol states (keep distinct — do NOT collapse into one path)

There are **three distinct ways a symbol can be "missing,"** each with its own cause, surface, and remedy.
They look superficially similar ("no symbol to draw") but must be implemented as **separate paths** — a future
implementation must not collapse them into a single "draw a rectangle" fallback, because the right user
response differs in each case.

| # | State | Cause | Where it surfaces | What the user sees / does |
|---|---|---|---|---|
| 1 | **No symbol at first placement** | The user tries to *place* a component whose cell has **no `.csym` at all** yet | At the **placement** gesture (not on an existing instance) | Placement is **deferred or cancelled**; the user is **prompted** to create the symbol (Symbol Editor) or run **auto-gen** (`project-file-formats.md`, Cell section). No un-symboled instance is ever committed this way. |
| 2 | **Broken cell reference** | An *already-placed* instance's **relative path to its cell does not resolve** (cell folder moved/renamed/deleted) | On the **canvas**, on that instance (and the referencing schematic) | A distinct **"Not Found" glyph** on the instance; the user must **re-point** the reference to unbreak it (§4). |
| 3 | **Primary-symbol contradiction** | The cell **resolves**, but its `.ccell` **names a primary `.csym` that is absent** | In the **tree** (the cell node) **and** on the **canvas** | Tree: cell shown **System.Warning + italics** with a reason tooltip (§3.2/§2). Canvas: the instance falls back to the **plain-rectangle stand-in** (`project-file-formats.md`). |

**Why they must stay separate:**
- **#1 is a pre-placement gate** (prompt → create/auto-gen), not a fallback render — the goal is to *avoid*
  ever having an un-symboled instance, so it never reaches a draw path.
- **#2 is a reference failure** — the cell itself is gone/unreachable, so the remedy is *re-point the path*;
  the "Not Found" glyph signals "I can't find what you asked for."
- **#3 is a contradiction within a present cell** — the cell is right there but lies about its primary, so the
  remedy is *fix the `.ccell`/restore the file*; the System.Warning + rectangle signals "this cell
  contradicts itself."

A cell that simply **lacks** a symbol (no `.csym`, and `.ccell` names none) while being authored is **none of
these** — it is normal and silent (§3.4); it only becomes #1 if someone tries to place it, or #3 if `.ccell`
names a primary that isn't there.

---

## 5. `.cws` contents (refines `project-file-formats.md`)

The `.cws` records **configuration only — never membership** (membership is the filesystem, §intro):
- **Dock layout** (the panel/tab arrangement, restored on open).
- **Referenced libraries** — relative-or-absolute paths to external library folders. Unresolvable →
  System.Warning + italics in the tree (§3.2).
- **Known Files** — an arbitrary list of paths to other files the user finds convenient to keep at hand while
  working (no semantic role; just convenient bookmarks). Unresolvable → System.Warning + italics, same as a
  broken library.
- **Active color scheme name** (`ColorSchemeName`, nullable) — resolved workspace dir → user themes →
  `/Assets/Color` → `BuiltIn` (`color-themes.md`).
- **Tree view state** (optional): the user's custom ordering and active filter set (§3.1/§3.3), so the tree
  restores as arranged.

(The old `.cws` "member files list" is **removed** — membership is read from the folder structure.)

---

## 5A. Foreign documents (files open from outside the current workspace)

**Status:** design · implemented per `docs/sonnet-briefs/brief-foreign-documents.md`.

The governing principle at the top of this document — *the filesystem IS the workspace* — already implies
this section. If membership is decided by **where a file sits on disk**, then a document open in the editor
either sits under the current workspace root or it does not. It has always been possible to open one that
does not: `File ▸ Open ▸ Schematic… / Symbol… / Layout…` takes an arbitrary path. So *"every open document
belongs to the current workspace"* was never true; this section makes the consequences deliberate rather
than accidental.

**Definition.** A document is **foreign** when its file does not belong to the currently open workspace —
determined by its path, never by how it was opened or where it is displayed. Everything else is
**workspace-bound**.

### 5A.1 Two orthogonal axes

Docked-versus-torn-off and bound-versus-foreign are unrelated, and conflating them is the mistake to avoid.

|  | **Docked** | **Torn off** |
|---|---|---|
| **Workspace-bound** | the normal case | **also normal — full privileges** |
| **Foreign** | opened from outside the workspace | a reference document in its own window |

**R30. Tearing a document off is a presentation act, not a semantic one.** A document from the current
workspace keeps every privilege when torn off — tree node, dirty dot, Save-All, Remove/Rename Cell
participation, `.cws` session membership. A user who simply wants a larger canvas must not lose anything for
it. Any behaviour that keys off "is torn off" to decide something other than presentation is a defect.

**R31. A workspace switch replaces the contents of the window it happens in; other windows are unaffected.**
So docked documents close on a switch, as before, and **torn-off windows survive and become foreign**. This
is what lets a schematic or layout from another workspace stay open for reference while authoring elsewhere.
Note it does not contradict R30: the *switch* is window-scoped, rather than the *document* being privileged.
A dirty torn-off document does not prompt on switch — it stays open and dirty.

### 5A.2 Technology resolution — a foreign document keeps its own

**R32. `TechRef = null` resolves against the document's OWN parent workspace, not the currently open one.**

`layout-view.md` §2.4 defines a null technology reference as "the workspace default." The correct reading is
*the document's* workspace: a `.clay` under workspace **A** means what **A**'s technology says it means,
whatever happens to be loaded. Resolving against the open workspace instead would silently reinterpret a
foreign layout's layers — and because the shipped starter technologies both use layer keys `(1,0)`–`(8,0)`,
Drill would quietly become Substrate with nothing missing and no warning. That is `layout-view.md` §13's
cross-technology collision arriving through a new door.

**Mechanism:** walk up from the document's own absolute path to the nearest ancestor `.cws` — the same
find-the-project-root pattern used by version-control and solution files. Nothing new is stored: the document
already knows its path, and the answer stays correct when a project folder is moved wholesale.

**Resolve live; never snapshot.** Copying the technology at the moment a document becomes foreign would go
stale the first time that workspace's `.ctech` is edited.

**R33. A file with no ancestor `.cws` prompts** — browse for a `.ctech`, choose one from the *current*
workspace, or use a built-in starter technology. The answer is remembered for the session, and **no
`TechRef` is written into the user's file**; making it permanent is what layout's `Change Technology…`
command is for. Falling back to generated fallback colours without asking is **not** acceptable here — an
unknown *layer* inside a known technology can be generated silently, but a missing *technology* cannot, since
the result would look like the document rendering incorrectly.

### 5A.3 What a foreign document participates in

It is fully **editable and saveable**, to its own path.

| | Foreign document |
|---|---|
| Edit, save, undo | **Yes** |
| Hierarchy navigation (push-in) | **Yes** — cell references are relative to their own file (§4), so they resolve against its own workspace on disk |
| Save All, quit prompt | **Yes** — R34 |
| Project-tree node, dirty dot | No — it is not in this workspace |
| Remove Cell / Rename Cell rewriting (§4.1) | **No**, and those operations must not reach it |
| `.cws` session membership (§5) | **No** — R35 |

**R34. Save All and the quit prompt sweep open documents, not tree nodes.** A dirty document with no tree
node that Save All cannot reach, and that stays silent on quit, is a data-loss trap created by a convenience
feature.

**R35. A foreign document is never recorded in the current workspace's `.cws`** (refines §5). It is not part
of this workspace's session and must not reappear when the workspace is reopened.

**`Save As` into the current workspace adopts the document** — it becomes workspace-bound and gains a tree
node. This falls out of the path-based definition rather than needing its own mechanism, and it is the
natural "bring this into my project" gesture.

### 5A.4 Marking — chrome only

**R36. Mark the window chrome; never tint rendered content.** Layer colours are literal user-authored values
(`layout-view.md` §2.2) precisely so a layer's colour survives a theme change and matches third-party
viewers. Tinting the drawing would corrupt the one thing a reference document is open to show.

Three surfaces, all of them:

1. **Title bar** — `mylayout — [AmpProject]`, naming the source workspace. Informative rather than
   decorative: it answers *which* workspace, which a bare marker cannot. The existing dirty bullet is
   preserved (`• mylayout — [AmpProject]`); asterisks are not used, as they already read as "unsaved."
2. **A thin tinted band along the document's edge**, naming the workspace, with an affordance to open it.
3. **The tab header tinted** to match, so a docked foreign document is identifiable among its neighbours.

Use the palette's *unusual-but-fine* accent rather than an error colour — a foreign document is a normal,
supported state, and red would mislabel it. When there is no parent workspace, the band says so rather than
naming one.

### 5A.5 Foreignness is a runtime concept

**R37. No cross-workspace path is ever persisted.** Foreignness exists only for the duration of a session;
nothing in this section writes a reference to another workspace into `.cws`, `.ccell` or a view file.

This is deliberate groundwork. **Instancing cells from another workspace** — the "Add Library" feature — is a
separate design whose central question is *a named library alias resolved through `.cws` versus a raw path
recorded in every file*. Raw paths mean relocating a library breaks every document that referenced it. That
decision deserves its own treatment, and answering it accidentally here would saddle the feature with a
convention chosen for an unrelated problem. Note that cell references are currently **relative** to their
containing file (§4), so cross-workspace instancing is a new mechanism rather than an extension of the
existing one.

---

## 6. Cell parameters vs. the component-type registry (reconciliation)

Today `ComponentTypeRegistry` hardcodes per-`SymbolKind` parameters/defaults. `.ccell` now stores a cell's
parameters as **data**. These are the **same concept** — `.ccell` is the on-disk form of the registry's
hardcoded table. Direction:
- **Built-in library cells carry `.ccell` files** with their parameters (§1.3) — the standard library is real
  cell folders, so its parameters live in `.ccell`, not (only) in code.
- The registry's role **migrates toward a loader**: built-in cell parameters come from their shipped `.ccell`
  files; user cells get parameters from their own `.ccell`. *(The mechanical migration of `ComponentTypeRegistry`
  from a hardcoded table to a `.ccell`-backed loader is its own implementation step, sequenced when this design
  is built — flagged here, not done in the doc.)*
- A schematic instance's parameter **values** still live in the `.csch` (the instance overrides the cell's
  defaults); the cell's `.ccell` provides the **declared set + defaults**. Two editors, two scopes (§7).

---

## 7. The cell-parameter editor (HIG-compliant)

A view that edits a **cell's declared parameter interface** in its `.ccell` — deliberately **similar in look
to the instance-parameter editor** (`parameter-editor.md`) but **different in purpose**: the instance editor
sets *values* on one placed instance (fixed parameter set); the **cell editor defines the *interface*** — the
list of parameters the cell declares and their defaults.

**The key UI delta:** the cell editor's parameter list is **editable as a list** — **add, remove, and rename
parameter rows** — because it defines *which* parameters exist. (The instance editor deliberately cannot
add/remove rows; its set is fixed by the cell.) Per row: **Name** (editable here, unlike the instance editor's
read-only name) · **Default value/expression** · **Unit** (the dimension-keyed closed ComboBox, reused from
the instance editor) · **Dimension** · **Show-on-schematic default**.

**HIG (per `ui-design.md` interaction spec / the instance editor's conventions):**
- Opened from the tree (double-click a cell, or **Edit Parameters** context item, §3.4); hosted as a content
  tab or dialog (reuse the instance editor's host pattern).
- A fixed **header** (cell name/type) · the **scrollable editable parameter list** (Name · Default · Unit ·
  Dimension · Show-default, shared-size aligned columns, matching the instance editor's column grid) · an
  **add-row** affordance (e.g. a "＋ Add Parameter" button at the list foot) and a per-row **remove** control ·
  a fixed footer (Help; Close in dialog).
- **Every edit is undoable** through the appropriate stack (the cell-document's own stack, per the
  per-document-undo rule) and commits to `.ccell` — add/remove/rename/default-change all via commands that
  notify in both Execute and Undo. **No RGB color** anywhere (consistent with the rest); reuse the instance
  editor's unit ComboBox and validation.
- **Renaming a parameter** is the consequential edit: it changes the cell's interface, so existing instances
  referencing the old name will need reconciliation. For v1, **renaming is allowed and surfaced** (instances
  with values keyed to the old name fall back to the new default / show as unset) — the cell editor warns that
  renaming/removing a parameter affects placed instances. *(Automatic instance-value migration on rename is a
  later enhancement; v1 makes the consequence visible.)*

---

## 8. Implementation order (smallest correct first; sequenced when built)

1. **`.ccell` model + read/write** (params + primacy + IsTestBench); the **cell subfolder layout**
   (`schematic/`/`symbol/`/`layout/`); `NameValidator` (cross-platform charset, §1.4).
   **DONE** — `src/Ui/Schematic/NameValidator.cs`, `CellPersistence.cs`, `CellFolder.cs`;
   primacy resolution centralised in `CellFolder.ResolvePrimary`; 89 tests green.
2. **Filesystem scan → Project Tree model** (read-only first): structure, primary resolution (§2), visual
   states (§3.2), empty-folder handling. Manual + on-focus refresh.
   **DONE** — `src/Ui/Schematic/ProjectTreeNode.cs` (`NodeKind` enum + `ProjectTreeNode`),
   `WorkspaceScanner.cs` (`WorkspaceScanner.Scan`), `WorkspaceModel.cs` (refresh wrapper).
   `CwsFile.KnownFiles` added to `WorkspacePersistence.cs` (additive, backward-compatible v1 field).
   41 new tests green; 362 total.
3. **Project Tree view**: disclosure, bold primaries, System.Warning broken refs + tooltips, the
   category-toggle filter (§3.3), manual + on-focus refresh.
   **DONE** — `ProjectTreeFilterState` (7-toggle ObservableObject), `ProjectTreeNodeViewModel` (wraps
   `ProjectTreeNode`; exposes `IconKind`, `IsWarning`, `IsBold`, `IsItalic`, `FilteredChildren`),
   `ProjectTreeTool` (Refresh command, `SetWorkspace`/`ClearWorkspace`, expand-state preservation),
   `ProjectTreeView.axaml` (TreeDataTemplate, per-kind Material icons, bold/italic/CrfWarningBrush
   styles, filter Flyout, on-focus debounced refresh). `CrfWarningBrush` DynamicResource wired in
   `App.axaml` + `App.axaml.cs` (follows `ThemeService.ThemeChanged`). 10 new VM tests green; 372 total.
4. **Context menus + double-click** (§3.4/§3.5): New Cell (container nodes), New Schematic/Symbol/Layout
   (cell node; Layout greyed), Make Primary, Reveal in Finder, the open/activate behaviors.
   **DONE** — `ITreeActions` interface (injected from WorkspaceViewModel); commands on
   `ProjectTreeNodeViewModel` (ActivateCommand, MakePrimaryCommand, RevealCommand, New*Command);
   `InputNameDialog` (name-prompt Window with NameValidator); `ProjectTreeTool.SetActions(ITreeActions)`;
   `WorkspaceViewModel implements ITreeActions`: OpenNode (`.csym` → SymbolEditor, `.csch` →
   SchematicDocument, cell → placeholder stub), MakePrimary (writes `.ccell` + Refresh), Reveal
   (macOS `open -R`, Windows `explorer /select,`, Linux `xdg-open`), NewCellAsync / NewSymbolAsync /
   NewSchematicAsync (InputNameDialog + NameValidator + file create + open tab + Refresh); open/activate
   dedup by absolute path via `_openDocsByPath[absPath]`; Layout greyed; cell double-click opens
   placeholder stub (step 6); unviewable types no-op. 372 tests green; firewall green.
5. **Cell reference model** (§4): instance → relative-path → `.ccell` → primary `.csym` resolution, replacing
   the static `SymbolKind → BuiltInSymbols` path; "Not Found" glyph; **live re-render on primary-symbol
   change** (this is the symbol-editor 4c-later payoff). *(Registry → `.ccell`-loader migration, §6, lands
   here or just before.)*
   **DONE (Phase 6g Step 5).**
   - **L1 (entry points):** `NewWorkspace` now creates a real `.cws` + folder on disk. `File → New Cell`
     (`NewCellInWorkspaceCommand`, greyed when no workspace); tree-header New Cell button (`IsVisible=HasWorkspace`).
     `ITreeActions.NewCellInWorkspaceAsync()` shared by File menu and header button. `InputNameDialog` +
     `NameValidator`; `CellFolder.CreateCellFolder` + Refresh. 763 tests green.
   - **L2 (resolver):** `CellRef: string?` added to `EditableComponent` and `CschComponent` (nullable,
     `WhenWritingNull`; round-tripped through `SchematicPersistence`). `SchematicDirectory: string?` on
     `SchematicEditModel` (set by `LoadFromFile`). Framework-free `CellSymbolResolver.Resolve(cellRef, baseDir)`
     returns `CellSymbolResolution { State, Symbol? }` with `CellSymbolState` enum
     (`Resolved / NotFound / PrimaryMissing`). Cache keyed by `(cellAbsDir, primaryFilename, symFileMtime)`;
     `Invalidate(cellAbsDir)` and `InvalidateAll()`. Three-state gate test (8 headless tests).
   - **L3 (render):** `SchematicComponent` gained `CellRefState: CellSymbolState?` and
     `CellRefPrimitives: IReadOnlyList<SymbolPrimitive>?`. `BuildRenderModel` pre-resolves all cell-refs via
     `ResolveAllCellRefs()`, threads through connectivity pass (resolved pins for port positions) and
     `ToRenderComponent`. `SchematicRenderer` dispatches on `CellRefState`: `Resolved` → `DrawSymbol(CellRefPrimitives)`;
     `NotFound` → warning box + "Not Found" label (`DrawCellRefNotFoundGlyph`);
     `PrimaryMissing` → plain-rectangle stand-in (`DrawCellRefPrimaryMissingGlyph`). Built-in path unchanged.
   - **L4 (live update):** `SymbolEditorViewModel.SymbolSaved: event Action<string>?` fires from `PerformSave`
     with the saved `.csym` path. `SchematicViewModel.TriggerRebuild()` calls `EditModel.NotifyChanged()`.
     `WorkspaceViewModel.OnSymbolSaved` derives the cell dir, calls `CellSymbolResolver.Invalidate(cellDir)`,
     then `RebuildOpenSchematics()` (iterates `_openDocsByPath`, calls `TriggerRebuild` on every
     `SchematicDocument`). `MakePrimary` also calls `Invalidate + RebuildOpenSchematics` when the symbol
     primary changes. Both `OpenOrActivateSymbol` and `NewSymbolAsync` subscribe `vm.SymbolSaved += OnSymbolSaved`.
   - **Step 5 fixes (post-L4):**
     - **Bug 1 — New Workspace never prompted:** `$parent[Window]` binding resolves to null on macOS for
       both `NativeMenuItem.CommandParameter` and `Window.KeyBindings.CommandParameter` — neither lives in
       the visual tree. `desktop.MainWindow` was also null (App never assigns it). Fixed with `ResolveOwner`
       that walks `ApplicationLifetime.Windows` for the window whose `DataContext` is `this`. Applied to
       `NewWorkspace`, `OpenWorkspace`, `SaveWorkspaceAs`. Gotcha recorded in `src/Ui/CLAUDE.md`.
     - **Bug 2 — layout rebuild orphaned ProjectTreeTool:** `NewWorkspace` sets `CurrentWorkspacePath`
       (which triggers `OnCurrentWorkspacePathChanged → SetWorkspace` on the OLD tool), then calls
       `CreateDefaultLayout()` which replaces `factory.ProjectTreeTool` with a fresh instance. The new
       layout's tool never had `SetWorkspace` called → `HasWorkspace = false`, "No workspace open."
       placeholder visible. Fixed: after `Layout = newLayout`, call `SetActions(this)` then
       `SetWorkspace(workspaceDir)` on the new tool. Gotcha recorded in `src/Ui/CLAUDE.md`.
     - **Bug 3 — Project Tree header static:** `Tool.Title` (Dock base) `SetProperty`-based
       `PropertyChanged` not reliably picked up by Avalonia compiled bindings. Fixed with a separate
       `[ObservableProperty] string _workspaceName` on `ProjectTreeTool`; view binds `Text="{Binding WorkspaceName}"`.
       Header toolbar changed from `StackPanel` to `Grid ColumnDefinitions="*,Auto,Auto,Auto"` so the label
       fills available width and `TextTrimming="CharacterEllipsis"` elides long names without stretching.
       Gotcha recorded in `src/Ui/CLAUDE.md`.
6. **Cell-parameter editor** (§7): the editable-list editor writing `.ccell`, undoable.
7. **`.cws` refinement** (§5): Known Files, library refs, tree view-state; remove the member-files list.

Steps 1–4 stand up the tree against the filesystem; step 5 is the linkage that unblocks symbol live-update +
cell-driven open (the deferred 4c-later items); 6–7 complete authoring + workspace config.

---

## 9. Open / deferred
- **`FileSystemWatcher`** live tree refresh (v1 is manual + on-focus).
- **Layout view** (`.clay`, `layout/`, New Layout) — folder exists, command greyed; v2.
- **Rename-surviving cell references** / rename-fixes-references-from-tree — v1 is rename-at-risk (§4.1).
- **Automatic instance-value migration on cell-parameter rename/remove** — v1 surfaces the consequence (§7).
- **Registry → `.ccell`-loader mechanical migration** (§6) — sequenced with step 5.
- **`.cdd` / unviewable-type open** — deferred until the owning viewer exists (§3.5).
- **Torn-off window position persistence (v2)** — v1 persists the open-document set in `.cws` and restores
  torn-off documents as TABS on workspace open. v2: also persist which documents are floated plus each host
  window's bounds (X/Y/W/H), and reconstruct the floats at their saved positions on open. Scoping (≈half a
  focused session for a basic version, up to a full session with edge cases): L1 — add float flag + window
  bounds to the `.cws` `OpenDocuments` schema and write them (enumerate `_wiredHostWindows`; `Window.Position`
  / `Width` / `Height` give geometry) — small, lands clean. L2 — INSTRUMENT-FIRST the programmatic
  float-to-bounds on open (prove the Dock float API places a doc at a given rectangle before building the
  restore loop; the programmatic path is fiddlier than the drag gesture and may need a deferred/post-layout
  pass like `TryWireHostWindowsUndo`). L3 — the restore loop + an OFF-SCREEN GUARD that clamps restored
  windows to a currently-visible monitor (a doc saved on a now-disconnected display must not vanish).
  Edge cases that push toward a full session: multiple tabs sharing one torn-off window, multi-monitor
  coordinates, and a float window that was itself re-docked into a split. See `docs/v2-backlog.md`.
