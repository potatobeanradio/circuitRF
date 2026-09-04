# circuitRF — Workspace & Project Tree Design

**Status:** Steps 1–7 done · project-tree arc complete · §5A added · §1.2.1 attachments added 2026-08-17 ·
**§5B / §5C added AND built 2026-09-03** ·
**Date:** 2026-09-03 · **Phase:** 6g (post-symbol-editor)

Specifies the **workspace model** (filesystem structure), the **Project Tree** (the tree view that reads it),
the **cell reference model** (how a placed component resolves to a cell's primary symbol — the linkage that
unblocks the deferred symbol-editor live-update and cell-driven open), **foreign documents** (§5A — files
open from outside the current workspace), **multiple open workspaces** (§5B) and **external cell
references** (§5C), and the **cell-parameter editor**.
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
- **A user folder may also hold cell folders, at any depth** *(stated explicitly 2026-08-25; it was always
  true and always scanned, but was never written down)*. Nesting costs nothing anywhere else because a
  `CellRef` is a **relative path** (§4) — a cell resolves from any depth — and both the scanner and the
  instance cell-picker recurse. **Cells do not nest inside cells**: a folder carrying a `.ccell` is a cell,
  and its sub-folders are its views, never further cells.

```
MyWorkspace/                     ← workspace name = folder name
   .cws                          ← Dock layout, library refs, known files, color scheme
   AmpStage/                     ← a cell folder (§1.2)
   Bias/                         ← a cell folder
   eval_board/                   ← a user folder holding cells (§1.1a)
       eval_board/               ← the board's own cell
       R0402/                    ← a footprint cell
       C0603/
   displays/                     ← arbitrary user folder
       sweep.cdd
   themes/
       midnight.ccolor
```

#### 1.1a Where an import's cells land *(added 2026-08-25)*
A board import can create **dozens** of cells in one action — one per distinct footprint definition plus the
board's own — and dropped at the workspace root they bury everything the user authored. **An import therefore
creates a folder named after the source file (minus its extension) and puts every generated cell inside it.**

- The name goes through the **same sanitize-then-suffix rule the imported cells themselves use**
  (`DxfNaming.NameCellsForImport`'s predicate): a file name is not a safe path component in general.
- **A name already taken becomes `name_2`, `name_3`, …** — importing the same board twice must never merge
  two boards into one folder, because that would silently overwrite by cell name.
- **A cancelled or failed import leaves nothing behind**: the folder is removed if it is still empty.
- This is a **real directory, not a synthetic tree group.** A synthetic group was considered and rejected —
  the filesystem already expresses this, `Libraries`/`Known Files` are synthetic only because they are *not*
  filesystem members, and a second grouping mechanism would need its own persistence, its own refresh rule
  and its own answer for every consumer that walks the workspace.

### 1.2 Cell
A **cell is a filesystem folder**. The **folder name is the cell name**. It contains:
- exactly one **`.ccell`** file at its root (only one permitted) — records the cell's **parameters + defaults**
  and which view files are **primary** (§2). The cell folder name, not the `.ccell` filename, is the name.
- three **view sub-folders** (always present, created with the cell): **`schematic/`**, **`symbol/`**,
  **`layout/`**. `layout/` exists but is unused in v1 (created empty; layout is v2).
- inside each sub-folder, **any number** of view files: `schematic/*.csch`, `symbol/*.csym`,
  `layout/*.clay`. A cell may have many schematics, many symbols.
- optionally, **attachments** beside those view files (§1.2.1) — e.g. `layout/amp_v1.wBond`.

```
AmpStage/                        ← cell name = folder name
   .ccell                        ← params + defaults + which view is primary (per type)
   schematic/
       amp_v1.csch
       amp_v2.csch               ← (one of these is primary, recorded in .ccell)
   symbol/
       amp.csym                  ← (sole symbol ⇒ primary by default, §2)
   layout/
       amp_v1.clay
       amp_v1.wBond              ← an ATTACHMENT to that .clay (§1.2.1)
```

A cell is authored **incrementally** — any view may be absent while the user works (a cell with a `.ccell`
but no `.csch` or `.csym` yet is normal and **not** an error, §3.4).

#### 1.2.1 Attachments — the third file shape *(added 2026-08-17)*

Until now the workspace had exactly **two** shapes of file: a **cell view** (in a view sub-folder,
primacy-resolved, §2) and a **loose workspace file** (`.cdd`, `.ccolor`, in an arbitrary user folder,
§1.1). The `.wBond` fits neither, and that — not its directory — is why it looked out of place sitting
at a cell's root. Its category had never been named. Others are coming (a package stack-up, assembly
notes, a thermal map), so the category is settled here rather than one folder at a time.

> **An ATTACHMENT is a file that is always used *together with* one view file, never *instead of* it.**
> It lives **in that view's sub-folder, sharing that view file's stem**. It has **no primacy** and never
> appears in `.ccell`. The tree renders it as a **child of the view it attaches to**. An **orphaned**
> attachment — one whose view file is absent — is **reported**, never silently ignored.

**Why an attachment is not a view.** A view sub-folder carries a specific contract: N files, **at most
one primary**, and an instance in a parent resolves *through* that primacy (§4). That means a view is
*an alternative description of the same cell, of which one is in force at a time* — which is exactly
what "Make Primary" is for. Wires and artwork are not alternatives: they are used together, always. A
`wires/` view sub-folder would have imported primacy semantics that are not merely unnecessary but
**wrong** — `wbond.md` WB28 deliberately refuses a wBond singleton, so two `.wBond` files in one cell
means *both are real and both are solved*, whereas "one primary, the rest inert" would silently drop
one from the simulation.

**Why the stem, and not the cell root.** A cell may hold `layout/amp_v1.clay` *and* `layout/amp_v2.clay`.
Wires are drawn over *specific artwork* — pads at specific coordinates — so "the cell's wires" is not
a well-formed idea the moment a cell has two layouts. Stem-pairing makes the association **defined**
instead of assumed, and it costs nothing elsewhere: primacy already resolves per view type **by
extension** (§2), so an attachment in a view sub-folder can never be miscounted as a second view file.

**The cost, stated rather than discovered later.** Renaming or copying a `.clay` in Finder detaches its
attachment. (Renaming it through **Rename Cell** does not — that path moves the `.wBond` with it, §4.1.) §4.1 already accepts Finder-edits as at-risk, but every existing failure mode there is
*loud* (a "Not Found" glyph, a System.Warning row); a silently dropped attachment could instead remove
wires from a simulation the user believes includes them. That is why the orphan **must** be reported —
the report is not a nicety, it is the price of the placement.

**What is NOT an attachment, and stays a plain cell:**
- **An assembly** (multiple die + substrate + wires in a package) *contains instances of other cells*, so
  it is an ordinary cell whose **layout view is hierarchical** — already expressible today, including
  a mix of technologies, because flatten resolves each sub-cell's own technology reference rather than
  imposing the parent's. What assembly actually still needs is **z** — a die-attach height on a layout
  instance — which is a model change, not a directory.
- **An application / evaluation board** *instantiates* this cell; it does not describe it. Making it a
  view inverts the hierarchy and immediately poses a meaningless question ("which of the three boards
  is *primary*?"). It is a sibling cell, and what is wanted from it is **association and navigation** —
  a `.ccell` reference field to that cell, resolved by §4's existing relative-path model and surfaced
  by §3.2's existing broken-reference warning.

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

**A rescan that finds nothing changed does nothing** *(added 2026-08-25)*. The on-focus rescan fires on open,
on every alt-tab back and on every dialog close, and a rebuild replaces every node VM and hands the TreeView
a new collection — so it tore down and rebuilt every row each time, which is what the user saw as a flash.
The scan result is hashed (shape, order, path, kind, name, primacy, test-bench flag, warning text) and the
rebuild is skipped when it matches. The **on-focus** rescan also runs **off the UI thread** — `WorkspaceScanner`
is framework-free and touches only the filesystem. The explicit `Refresh` stays synchronous, because its
callers expect a finished tree on return.

**A REFERENCED sub-tree is exempt from the on-focus rescan** *(SL4 R-sl4-10, 2026-09-03)*. The signature above
is computed **from the scan's result**, so the walk always happens — and after SL1 that walk includes every
folder of a referenced library, measured at ~2,800 filesystem round trips for a 200-cell one. A referenced
library or workspace is therefore walked on exactly **three** gestures — **workspace open, explicit Refresh,
and the first expansion of an unread node** — and never on window activation. The workspace's **own** folders
keep today's behaviour exactly, on every scan: they are local almost always, they are the ones the user is
editing, and they are the reason the on-focus rescan exists. A referenced library is neither — it changes on
someone else's schedule, possibly at the far end of a cable, and the user's own gesture is a better trigger
than alt-tab.

**A referenced sub-tree that has not been walked renders as ITSELF, never as empty** *(R-sl4-11)*. The
on-focus scan carries the previous walk's contents forward; where there was no previous walk — a reference the
librarian added to the `.cws` after this window opened — the node shows a single *"Not read yet — expand or
Refresh to browse"* row. **An empty library is the exact symptom SL1 exists to remove, and it must not come
back through a caching rule.** That placeholder is also the mechanism and not only the message: a TreeView
draws no expander for a childless node, so without it there would be nothing to expand and the third trigger
could never fire. It is italic but is **not** a warning — an unread library is an ordinary, expected state.

*Not yet off-thread: the workspace OPEN itself* — see §9.

### 3.1 Structure shown
- The **workspace** root, its **cells** (each disclosing `schematic/`/`symbol/`/`layout/` → their view files),
  its **arbitrary user folders** and the `.cdd`/`.ccolor`/other files within (surfaced by extension),
  **referenced libraries** (each its own sub-tree of cells), **referenced workspaces** (§5C — the same
  sub-tree shape as a referenced library, read-only), and **Known Files** (§5).
- **User folders render recursively, and a cell inside one renders as an ordinary cell node** (§1.1) — the
  same icon, the same context menu, the same double-click. Depth is not a special case anywhere in the tree.
- **A referenced library and a referenced workspace render their cells at ANY depth too, by the same rule**
  *(SL1 R-sl1-1, 2026-09-03)*. Both used to make one pass over the referenced root and keep only the folders
  holding a `.ccell`, so a librarian who organised two hundred cells into `passives/`, `amplifiers/` and
  `footprints/` — the first thing anyone does with two hundred cells — published a library that rendered
  **empty**. References into those cells resolved the whole time (resolution is path arithmetic and never
  consults the tree), so only the browsing was missing, which is the entire point of a library. Three rules
  bound the recursion:
  - **Folders, never another `.cws`** (R-sl1-2). A workspace nested inside a referenced one is walked as an
    ordinary folder; its own libraries, Known Files and referenced workspaces are **its** business, and
    rendering them here would let one reference reach transitively through a chain nobody chose. What stops
    at the nested `.cws` is the CONFIGURATION, not the directory walk.
  - **A referenced sub-tree carries cells and nothing else** — no loose files, which is unchanged from before
    the recursion — so **a folder with no cell anywhere beneath it is not rendered**. It would otherwise be a
    dead end the user opens once and learns to distrust. The workspace's OWN scan keeps such folders because
    it renders their files too.
  - **`.generated-cells` never appears, at any depth.** The reserved-folder exclusion (§3.1's R-L5g-9) used
    to be applied only in the root loop, which was latent rather than correct — the folder only ever exists at
    a workspace root, where the "has a `.ccell`" predicate happened to skip it. It now lives in the one
    directory-listing helper every walk passes through, so it cannot be true in three places and false in the
    fourth. **This also fixed a pre-existing hole on the workspace's own side**: a `.generated-cells` folder
    inside a user folder was browsable before SL1.
- **Cost, measured rather than assumed** *(2026-09-03)*: scanning a referenced workspace of 200 cells in 10
  folders costs **2,834 filesystem calls**, against **2,804** for the same 200 cells flat at its root. The
  recursion itself is ~1% — about **3 calls per folder**; the cost is **~14 calls per CELL**
  (`BuildCellNode`'s three `ResolvePrimary` probes plus its own per-view listing and the `.ccell` read). Over
  a network that is what an alt-tab rescan pays, and it is SL4's problem, not the recursion's.
  **Answered** *(SL4 R-sl4-10, 2026-09-03)*: an alt-tab rescan no longer pays it at all — see §3's
  referenced-sub-tree rule above. The per-cell cost itself was deliberately not optimised.
- **Empty view sub-folders show no disclosure triangle** (a cell with no symbols yet shows no `symbol/`
  expander). Don't render empty expanders.
- **Attachments (§1.2.1) render as children of the view file they attach to**, not as siblings of it —
  `layout/amp_v1.clay` discloses to `amp_v1.wBond`. They are never bold (they have no primacy) and they
  are never a separate sub-folder node.
- **An ORPHANED attachment** — one whose stem matches no view file in that sub-folder — renders at the
  sub-folder level in **System.Warning + italics**, with the reason in a tooltip (*"amp_v1.wBond has no
  amp_v1.clay — its wires are not attached to any layout."*). This is the §1.2.1 rule that keeps a
  Finder rename from quietly removing wires from a simulation, so it is not optional.
- **Ordering is customizable** (user-arrangeable) **and the tree is filterable** (§3.3).
- **The tree scrolls HORIZONTALLY** *(added 2026-08-25)*. Not a default worth inheriting: a `ScrollViewer`'s
  default `HorizontalScrollBarVisibility` is `Disabled`, and `Disabled` does not mean "no scrollbar" — it
  constrains the content to the viewport width, so a long cell or file name was clipped with no way to reach
  the rest of it. It also squeezed the columns beside a long row, which is what made the disclosure triangle
  hard to pick out. The full name is also always available in the node's tooltip (§3.2).

### 3.2 Visual states (color + weight)
- **Primary view files** (the resolved primary `.csch`/`.csym`/`.clay`) render **bold** when their sub-folder
  is disclosed.
- **Broken references → System.Warning color + italics**, with a **tooltip giving the reason**:
  - a **referenced library** whose path cannot be resolved;
  - a **referenced workspace** whose `.cws` cannot be resolved (§5C) — the same treatment, deliberately:
    one broken-external-reference appearance, not two;
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

#### 3.3a Name search *(added 2026-08-25)*
A **free-text name search**, the direct answer to "I can't find my own cells among fifty imported footprints",
and one that does not depend on how the workspace happens to be organised.

**It lives behind a magnifier toggle in the toolbar**, to the right of the category filter. Clicking it reveals
the field **in the workspace name's place** — so the toolbar stays one row, every button stays where it was,
and the field gets the full width without overlaying controls it would then have to hit-test around. Closing
it puts the name back. A permanently-present field would spend every session eating the width the name and
the buttons need, for a control most sessions never touch.
- **Opening it focuses the field** — revealing a box the user then has to click is two gestures for one intent.
- **Exactly two things collapse the field: Escape, and the X inside it.** Both are explicit. **Emptying the
  text does NOT** — clearing the query and putting the field away are different intentions, and backspacing to
  the start of a re-typed query is the common one. (An earlier round closed on empty; it was reversed for that
  reason.) The consequence for the X is that it must do *both* halves itself — a clear that only cleared the
  text would leave the box sitting open.
- **Closing always clears the query.** A filter still applied with nothing on screen to explain it is the worst
  state this panel can be in: the tree silently hides cells and the one affordance that would say why has just
  been put away.
- **Closing hands focus back to the tree.** Otherwise the caret stays in a control that is no longer on screen,
  which swallows keystrokes *and* makes §3.3b's Home/End yield to a text field the user cannot see.
- **Empty or whitespace means no filter**, never "match nothing".
- A node survives when **its own name matches, an ancestor's did, or a descendant's did.** The ancestor rule
  is what keeps the folders on the path to a match visible; the *descendant* rule is what keeps a matched
  cell **openable** — a cell's children are named for their views (`schematic`, `symbol`, `layout`), never for
  the cell, so filtering them by the same text would show the row and then empty it.
- **The workspace ROOT is excluded from text matching**, and this is not a detail — without it, typing the
  workspace's own name matched the root, whose subtree is *everything*, so the filter appeared to do nothing
  (owner report, 2026-08-25). The root is also the one node that is **never rendered** (§3.1: the panel header
  names the workspace and the tree binds to the root's children), so there is not even a matched row on screen
  to explain the result. Excluding it loses no visible row.
  **A visible folder still passes its contents through** — typing an import folder's name to see what is in it
  is the intended use (§1.1a). The root is special because it is invisible *and* universal, not because
  subtree pass-through is wrong.
- **The text filter and the category toggles are ANDed.** Clearing a checkbox while a search runs must still
  hide that category, or the checkbox appears to stop working the moment anything is typed.
- A live search **force-expands the path down to each match and restores the previous expansion when the box
  is cleared** — a match six levels down is invisible behind a collapsed folder, and a search should not leave
  the whole workspace hanging open behind the user.

#### 3.3b Keyboard scrolling *(added 2026-08-25)*
**Page Up / Page Down / Home / End scroll the listing** when the tree has focus — and the Library palette's
tile grid behaves identically, from one shared rule (`PanelScrollKeys`, which the `.ctech` editor's row lists
already used). They **scroll; they never move the selection** — paging past a long listing must not change
what is selected on the way. Home/End yield to a text field (there they are caret motion); Page Up/Down do
not, so the palette user can type a search and page through its results without leaving the box.

**A panel claims keyboard focus when it is activated**, and that is what makes the above work at all: a key
handler only fires when the focused element is on the event's route, and clicking a panel's TAB leaves focus
on Dock's chrome, outside the panel's view. Tool panels now do what document tabs have always done
(`IActivatableDocument` → `IActivatableTool`). Both the tree's scroller and the palette's tile area declare
`Focusable="True"` explicitly — `TreeView` and `ScrollViewer` are both non-focusable by default, so without it
the focus call silently does nothing.

### 3.4 Context menus
- **On any CONTAINER node — the workspace, a library, or a user folder:** **New Cell** (creates a cell folder
  + `.ccell` + the three empty view sub-folders, then opens it for authoring), **New Folder…**, and
  **New Technology…**. *(These are container-level actions — they create something *inside* — so they live on
  the container node, never on a cell.)* **A user folder was added to this set on 2026-08-25**: it had always
  been scanned and rendered, and a cell inside one had always resolved, but nothing could be created in one —
  which made folders look unsupported when only the affordance was missing. All three verbs share ONE gate
  (`CanCreateInside`) so they cannot drift apart.
- **New Folder** is offered on the tree header too, beside New Cell.
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
- **A `CellRef` has four forms, and each states its own kind** *(taxonomy recorded 2026-09-03)*. A form
  that announces itself cannot be mistaken for a mistyped path, which is what lets each have its own
  repair flow rather than one "not found" that covers all of them:

  | Form | Resolves against | Introduced by |
  |---|---|---|
  | `../../Amp` — a relative path | the referencing file's own folder | §4, the ordinary case |
  | `pdk://<kit>/<part>` | the kit registry, scoped to the referencing document's own workspace | `pdk-import.md` |
  | `wbond://…`, `spicemodel://…` | a symbol GENERATED from the file the reference names | `wbond.md`, `spice-models.md` |
  | `ws://<alias>/<path>` | the alias table in the referencing document's own `.cws` | **§5C** |

  **The last three are not paths and must never be reported as bad ones.** Falling through to path
  resolution produces a "not found" naming a directory nobody ever expected to exist.
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
- **How live is "live": within T = 2 seconds** *(SL4 R-sl4-7, 2026-09-03)*. `CellSymbolResolver.Resolve`
  re-stats the `.csym` before it will take a symbol-cache hit, which is what makes the librarian's edit reach
  every user without a restart — and it is **four filesystem round trips per referenced component**
  (`Directory.Exists` on the cell folder, `Directory.Exists` + `Directory.GetFiles` on its `symbol/`, and the
  primary's mtime; six when the folder holds more than one symbol, since the `.ccell` is then read too).
  `BuildRenderModel` re-resolves every component on **every model change**, so a forty-component schematic
  costs ~160 round trips per edit — free on a local disk, free on a LAN, and several seconds per
  keystroke-scale edit over a VPN. Those four answers are therefore cached, **positively only, for T**, so a
  burst of edits inside T costs nothing. The guarantee is stated in one place (`CellStat.Freshness`) and it is
  the ONLY thing that changed: the mtime check is still the mechanism, and a stale mtime is a stale drawing,
  so the resolution result is never cached for longer than the stat it rests on.
  - **A NEGATIVE is never cached** *(R-sl4-8)*. A cell folder that was not there, a `symbol/` with no `.csym`
    in it, an mtime for a file that is not present — each is re-asked on the very next resolve. Caching "not
    found" for even a second turns a share that blinked, or a folder the librarian is half-way through
    renaming, into a design full of Not-Found glyphs that persist after the network has recovered, which
    reads as data loss and is not.
  - **Only the reference-resolution path caches.** The project tree's own scan calls the same primacy helper
    and reads the filesystem every time, because a user who has just created a symbol and pressed Refresh
    must see it. The tree's cost has its own answer, in §3: don't walk a referenced sub-tree on focus.
  - **T is bounded by the fastest way a person can observe the weakening** — save a cell on one machine, walk
    or alt-tab to a second, look at a design that places it. That round trip is never under a few seconds.
  - Dropped by `WorkspaceRootFinder.InvalidateCache` alongside the walk-up, the alias table and the
    writability probe *(R-sl4-9)*, and by `CellSymbolResolver.Invalidate` for the one cell a Make-Primary
    rewrote — so a gesture the user made themselves takes effect at once rather than within T.
- **Primary-symbol change is a RISKY user operation (surfaced, not blocked).** Different `.csym` files in a
  cell may declare **different pin counts/positions**. When the primary changes, pins re-resolve from the new
  `.csym`, and **any wire that no longer meets a pin shows as unconnected** (the existing positional
  connectivity model handles this — no auto-rewiring; that is the deferred Option B, `symbol-editor.md` §6).
  The risk is made visible (dangling wires + re-rendered glyph), and the user accepts it.

  **That bargain now has a MECHANISM behind it, and it needed one** *(§4.3, built 2026-09-03)*. "Made
  visible" was true only when the person who changed the cell and the person who accepts the risk were the
  same person in the same minute. Across an organisation they are different people weeks apart, and what
  is "visible" is a wire that renders slightly differently on a page nobody re-reads. §4.3 records the
  interface at placement and compares it at resolve, so the change is **stated** rather than left to be
  noticed. **The bargain itself is unchanged** — nothing is blocked and nothing is auto-rewired.

### 4.1 Rename / move at the user's risk
**Editing the cell/library filesystem directly *is* editing the cell/library definition.** If the user
renames or moves a cell folder in Finder/Explorer and thereby breaks a referencing schematic, that is **their
responsibility** — the schematic shows "Not Found" until re-pointed. circuitRF does **not** track a
rename-surviving cell ID (consistent with the `Id`-never-persisted rule).

**Renaming a cell from within the TREE is a different thing and does repair itself** (this superseded the
"a future version may" note that stood here). It rewrites every `CellRef` in the workspace, renames the
primary `.csch`/`.csym`/`.clay` to match, updates the `.ccell`'s primaries — **and renames the `.wBond`
attached to that `.clay`, repointing the schematics linked to it.** The wirebond half is not tidiness:
attachment is by shared stem (§1.2.1), so renaming the artwork alone detaches the wires. Finder-edits
remain at-risk by design; the tree operation is the one that is not.

**MOVING a cell, a folder or a loose file from within the TREE also repairs itself** *(TM1, built
2026-09-03; `brief-tree-move-1-moving-within-a-workspace.md`)*. Drag it onto a folder — or onto a cell,
which targets that cell's parent — and it moves, with every reference the move invalidates repointed.
The history below is kept because the reason it was deferred is the reason the implementation has the
shape it does.

*(What stood here until TM1: a move was a Finder-level, at-your-own-risk action, deferred with a
specific reason rather than for want of effort —* `CellUsageScanner.RewriteCellReferences` *matches and
rewrites the **last path segment** of a stored* `CellRef`*, because that is what a rename changes; a
move changes the path **prefix** and leaves the name alone, which that rewriter cannot express. Two
further blockers were named: same-named cells in two folders, and the fields beyond* `.csch`*/*`.clay`
`CellRef`*s that also point at a cell. §5C closed the first — see the paragraphs below, which are
unchanged and still describe how* `RewriteCellReferences` *works today for RENAME. TM1 closed the
other two.)*

**A rename changes ONE set of references; a move changes TWO** *(R-tm1-1 — the whole engineering
content of the feature)*. A rename keeps the cell in its parent folder, so its **depth** is unchanged
and every reference stored **inside** the cell — `../../lib/Rload`, `../Other/x.wBond` — still resolves
afterwards with no edit at all. A move changes the depth, so both directions break:

- **inbound** — every reference from elsewhere *into* the moved subtree, and
- **outbound** — every relative reference stored *inside* the moved subtree pointing anywhere outside it.

**An implementation that handles only the inbound half is a move that silently guts the cell it just
tidied away.** That is why a move is NOT an extension of the rename rewriter but a separate one —
`WorkspaceMove`, whose whole content is one map:

```
Relocate(abs) = abs is inside oldRoot ? newRoot + abs.Substring(oldRoot.Length) : abs
```

For every registered reference in every reachable file: resolve it to an absolute path **before** the
directory moves, and re-store it afterwards as `Store(Relocate(target), Relocate(base))`. A referrer
and a target that moved together produce an unchanged string and no write; everything else falls out.
There is deliberately no second rewriter — a second one is where the two halves drift apart. And the
ordering is the OPPOSITE of `RewriteCellReferences`'s (which runs *after* `Directory.Move` precisely
because a stale reference still spells the old path): resolution needs the filesystem and the memoised
alias table, so it happens first.

**What a move must NOT do is rewrite a reference that did not move** *(R-tm1-6)*. A slot whose resolved
target did not move AND whose base did not move is left **byte for byte** alone — not re-derived to an
equivalent spelling. Matching is on resolved absolute paths, never last segments and never a
string-prefix match on the stored spelling: a prefix rewriter gets `cells/AmpX` wrong when `cells/Amp`
moves, and both halves of that are gated.

**`CellRef` is not the only path-shaped field, and a move breaks all of them equally**, so the rewrite
is driven by ONE registry of (file predicate, JSON location, base-directory rule) — `MoveRefRegistry`.
A format that is not registered is not rewritten, which is the point: the alternative is a rewrite per
call site, which is how the table acquires a row nobody rewrites, and the symptom of a missed row is a
dangling reference in a file the user did not touch — which reads as data loss, not as a missing
feature. The bases genuinely differ (a `CellRef` is document-relative; an SnP `File` is
workspace-root-relative; a `.cem`'s `LayoutRef` is root-relative with the `.cem`'s own directory as the
no-workspace fallback), so each row names the shared producer it routes through.

**A cell's own insides do not move** *(R-tm1-8)* — the drag never starts for `schematic/`, `symbol/`,
`layout/` or the view files in them. That is the owner's rule and it is also structural: `CellFolder`
resolves those folders **by name** and the `.ccell` names primaries within them, so a cell whose views
have been rearranged is not a cell with a different shape, it is a cell that no longer resolves.

**The destination is shown as a ROW highlight, never an insertion caret** *(R-tm1-12)*. This tree is
sorted, not user-ordered — `FilteredChildren` is rebuilt by the scanner on every refresh — so a caret
between two rows would promise an ordering the tree cannot keep, and the first thing the user would do
after dropping is watch the row jump elsewhere. The highlighted row is the one that will actually
receive the drop, which for a hover over a cell is that cell's **parent**: an indicator that highlights
what is under the cursor rather than what will receive the drop teaches a rule that is false. The drag
effect is `Move` inside one workspace and `Copy` across two, which is free platform-native cursor
feedback on all three operating systems.

**A rewrite failure is REPORTED, never rolled back** *(R-tm1-16)* — Rename's shipped bargain, and the
right one: a partial rewrite leaves references that a re-run repairs, whereas an attempted rollback
moves the folder back underneath references that were already updated. **There is no in-app undo**
*(R-tm1-19)*, exactly as there is none for Rename or Remove Cell; the success message names both the
old and the new location, because that sentence is what lets a user put it back.

**Every move also appends a forwarding record to `.cmoves` at the workspace root** *(R-tm1-20; the
format and its consumer are TM2)*. The rewrite reaches this workspace plus every other workspace open
in this process, and `CellUsageScanner`'s own doc comment already names the limit: a referrer in a
workspace nobody has open cannot be found. Within one workspace that limit is invisible; it stops being
invisible the moment another project references this one through `ws://alias/…`, because that remainder
is **this** workspace's relative spelling and the move just invalidated it, in a file on someone else's
disk. The record is written **unconditionally**, including inside a workspace nobody shares — a
workspace that is private today is referenced next month, and a redirect that was never written cannot
be reconstructed.

Editing the cell/library filesystem **in Finder/Explorer** remains at-risk by design; the tree
operations are the ones that are not.

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

**A fourth state sits beside these three and is NOT one of them** — see §4.3. `InterfaceChanged` is the case
where the cell resolves, the primary symbol is present, and the glyph drawn is correct; nothing is missing at
all. It must not be folded into `CellSymbolState`, because every member of that enum puts the instance on a
*draw a placeholder instead* path, and here the placeholder would be the wrong render: the new symbol is the
truth. It is carried as its own runtime mark and is why §4.2's own "do not collapse these" argument extends
to four states rather than three.

### 4.3 The interface-change report (SL3, built 2026-09-03)

`brief-shared-library-3-interface-change.md`. **This is a report, not a version-control feature, not a
locking feature, and not an approval workflow.** It records one fact at placement and compares it at
resolve.

- **What an instance depends on is the cell's INTERFACE, and nothing else** — the pins in `PortIndex`
  order, the symbol's `PortCount`, and the parameter names the `.ccell` declares. Deliberately excluded:
  the drawing primitives, the parameter *defaults*, the cell's schematic, and its layout view. None of
  those can break a referencing design, and a report the user learns to dismiss costs more than it is
  worth. The field itself is specified in `project-file-formats.md`.
- **The remedy is a REPORT, never a refusal and never an automatic repair.** The librarian's new symbol is
  the truth and must render; auto-rewiring is `symbol-editor.md` §6's deferred Option B and stays deferred.
- **Three surfaces, all already built:** the Messages panel on open — **one line per affected CELL, not per
  instance**, because forty instances of one changed cell is one problem; the instance's Properties
  inspector; and the chrome marking §5C R51 already defines. **Never the rendered geometry** — R36 without
  exception.
- **The report names the newly-unconnected ports**, which is the electrical consequence and the reason the
  feature exists, taken from the connectivity pass that is already running rather than recomputed.
- **Accepting is one explicit, undoable gesture** — for the selected instances, or for every instance of
  that cell in the document. It must not happen on open, on save, or as a side effect of any edit: the
  recorded hash is the only evidence the design was authored against a different interface, and a product
  that erases that evidence on open has implemented nothing.
- **It applies to every cell reference, not only `ws://` ones.** The same failure exists for a cell in your
  own workspace with a smaller blast radius, and a rule that fires only sometimes is a rule nobody learns.
- **Version PINNING is deliberately not built** (R-sl-6): an alias points wherever the librarian says it
  points, so `…/stdlib/v2.3/.cws` is a complete pinning story with no resolver, no manifest and no
  "which version is newer" question — and two versions run side by side as two aliases.

**Where the built thing differs from the brief.** R-sl3-8 asks the report to say *"4 pins → 5, pin `vg`
moved"*. That cannot be said from a stored hash, and R-sl3-3 is the reason: only the hash is recorded, so
the OLD interface is not in hand at the moment the comparison fires. The report therefore states the
interface as it is NOW (pins, ports, declared parameters), names every affected instance, and — from what
the instance itself carries — names the ports that are unconnected and the declared parameters that
appeared or went away. Recording the signature instead would have satisfied the sentence at the cost of
putting a copy of the library's interface into every referencing file, which R-sl3-3 rejects for good
reason. Gate: `tests/Ui.Tests/CellInterfaceChangeTests.cs`.

---

## 5. `.cws` contents (refines `project-file-formats.md`)

The `.cws` records **configuration only — never membership** (membership is the filesystem, §intro):
- **Dock layout** (the panel/tab arrangement, restored on open).
- **Referenced libraries** — relative-or-absolute paths to external library folders. Unresolvable →
  System.Warning + italics in the tree (§3.2).
- **Referenced workspaces** *(§5C)* — `{ Alias, Path }` pairs naming another workspace's `.cws`. **This is
  the ONLY place a cross-workspace path is written**; every `ws://` cell reference names the alias, never a
  path, so relocating the other project is one edit here rather than a rewrite of every document that
  referenced it. Unresolvable → System.Warning + italics, exactly as a broken library.
- **Known Files** — an arbitrary list of paths to other files the user finds convenient to keep at hand while
  working (no semantic role; just convenient bookmarks). Unresolvable → System.Warning + italics, same as a
  broken library.
- **Those three path fields — and only those three — accept `${NAME}` tokens, expanded from the environment
  at resolution time** *(SL1 R-sl1-5/-6, 2026-09-03)*: `ReferencedWorkspaces[].Path`, `LibraryRefs` and
  `KnownFiles`. A library on a share is always the absolute branch, and the absolute spelling is per-machine
  — `Z:\eda\stdlib\.cws`, `\\server\eda\stdlib\.cws`, `/Volumes/eda/stdlib/.cws` — so a librarian could
  not hand out a starter workspace with a working library reference in it, which is the one thing a librarian
  most wants to hand out. A `.cws` naming `${CRF_LIB}/stdlib/v2.3/.cws` is portable to everyone who has
  `CRF_LIB` set, and **version pinning is then a path the librarian publishes** rather than a resolver anyone
  has to build (two versions side by side = two aliases).
  - **One syntax on every platform: `${NAME}`.** Never `%NAME%`, never bare `$NAME` — a `.cws` travels between
    machines, and a per-platform spelling resolves on the machine that wrote it and nowhere else.
  - **A `CellRef` is NEVER expanded.** It is the workspace-relative remainder (§5C R45) and has no business
    naming a machine; a token there would be a second place a cross-workspace path can hide, which is what
    the alias form exists to prevent. Nor is `PdkRefs`, which SL1 deliberately left outside the rule.
  - **An unset token is a BROKEN reference that NAMES the token**, never an empty expansion:
    *"Referenced workspace unresolved: `${CRF_LIB}` is not set on this machine."* Substituting empty turns
    `${CRF_LIB}/stdlib/v2.3/.cws` into `/stdlib/v2.3/.cws` — a rooted path that resolves to somewhere real on
    some machines and reports a missing folder on others, and both are worse than the truth.
  - **Nothing is ever WRITTEN with a token in it.** circuitRF writes a plain path; a token is what a librarian
    or a site template puts there by hand. Resolve it, never produce it — the same treatment R-mw2-5 gives the
    raw relative `CellRef`.
  - **There is no token DEFINITION mechanism**, no settings page and no `.cws` field mapping names to paths:
    the environment is where a site already configures this on all three platforms, and a second definition
    site would need its own precedence rules. No `~`, no `%USERPROFILE%`, no path variables of our own.
  - Expansion lives in `src/Design/Workspace/PathTokens.cs`, beside `ExternalCellRef`, because a headless
    `circuitrf convert` or `em` run resolves these references too.
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

It is fully **editable and saveable**, to its own path — **when its own path can be written.**

| | Foreign document | Foreign **read-only** document *(added 2026-09-03, §5D)* |
|---|---|---|
| Edit, undo | **Yes** | **Yes** — R-sl2-8: a file must not become un-scrollable because it is un-writable |
| Save | **Yes** | **No** — disabled, with the reason stated (R-sl2-7) |
| Save As | Yes | **Yes**, and it is the offered route |
| Hierarchy navigation (push-in) | **Yes** — cell references are relative to their own file (§4), so they resolve against its own workspace on disk | **Yes**, unchanged — resolution is a read |
| Save All, quit prompt | **Yes** — R34 | **Yes** — R34 still sweeps it, but through Save As (R-sl2-8) |
| Project-tree node, dirty dot | No — it is not in this workspace | No |
| Remove Cell / Rename Cell rewriting (§4.1) | **No**, and those operations must not reach it | **No** |
| `.cws` session membership (§5) | **No** — R35 | **No** — R35 |

**The read-only column does not contradict the first one; it is the case this section never had to
consider.** §5A was written for a colleague's file on your own disk, where "fully editable and saveable"
is correct because Save really does succeed. Applied to a locked-down share the same rule offers Save on
a master cell and the user learns otherwise *after* the edit. Read-only is a property of the filesystem,
discovered by probing it — never a flag in a file (§5D).

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

**§5D adds READ-ONLY to surface 2's wording, and to nothing else** *(2026-09-03)*. The band already says
*this belongs to another workspace*, which is half the message; whether you can save to it is the other
half, and a fourth surface or a second colour for it would be two marks for one fact. Two consequences:
the band now appears for a read-only document that is **not** foreign (the whole open workspace is the
read-only share, so surfaces 1 and 3 correctly stay quiet), and the accent rule above applies unchanged —
a read-only library document is a normal, supported, *desirable* state. The band's "open it" affordance
belongs to the FOREIGN half only: offering to open the workspace a file belongs to is meaningless when the
file already belongs to the one that is open and is merely unwritable.

### 5A.5 Foreignness is a runtime concept

**R37. No cross-workspace path is ever persisted BY THIS SECTION.** Foreignness exists only for the duration
of a session; nothing in §5A writes a reference to another workspace into `.cws`, `.ccell` or a view file.
A document is foreign because of where it sits, and closing it ends the fact.

R37 originally read *"no cross-workspace path is ever persisted"* full stop, and deferred **instancing cells
from another workspace** as a separate design — naming its central question as *a named library alias
resolved through `.cws` versus a raw path recorded in every file*, and warning that raw paths mean
relocating the other project breaks every document that referenced it. **§5C answers that question**
(alias, and the reasoning is there), so R37 is now scoped to §5A rather than global. The two remain
different things and neither implies the other:

| | **§5A foreign document** | **§5C external reference** |
|---|---|---|
| What it is | a file open from elsewhere | an instance pointing at a cell elsewhere |
| Lifetime | the session | persisted in `.cws` + the view file |
| User intent | "let me look at this" | "my design depends on this" |
| Survives reopening the workspace | no (R35) | yes, by design |

---

---

## 5B. Multiple open workspaces (one window each)

**Status:** BUILT 2026-09-03 · `docs/sonnet-briefs/brief-multi-workspace-0-overview.md` and
`-1-windows.md` · the per-window/per-process boundary §5B.2 records is also §5A of
`ui-architecture.md` *(the status line was left saying "not built" when MW1 landed; corrected while
building §5C, which depends on R41/R42)*.

More than one workspace open at once, each in its own window, so two designs can be viewed and inspected
side by side. §5A already made the *file* case deliberate; this makes the *workspace* case deliberate.

### 5B.1 The governing rule

**R38. A workspace window owns a workspace, and everything a workspace means is scoped to that window.**
Panels, dock arrangement, edit sessions, technology cache, undo, the Project Tree, `Reset Layout`. Nothing
is shared between windows except the application itself.

This is less of a change than it sounds: `WorkspaceViewModel` is not a singleton and neither is the dock
factory it creates, so the panels, the technology cache and the session registries are **already**
per-window. What is *not* already scoped is §5B.3.

**R39. Documents do not dock across windows.** A document tab carries dirty tracking, undo routing, `.cws`
session membership (§5) and Rename/Remove-Cell participation (§4.1), all of which resolve through the
workspace that owns the dock. Moving the tab would move none of it, so the tab does not move. A user who
wants a document from another workspace opens that workspace in its own window, or opens the file as a
foreign document (§5A) — two routes that already exist.

**R40. A workspace is open in at most one window, and a file has at most one edit session process-wide.**
Two view models over one `.cws` means two undo stacks and two dirty flags over the same files, and
last-save-wins. Opening one that is already open **activates that window** instead. This is what makes
§5C's external references safe: a cell referenced from B and edited in A cannot also be edited through B.

### 5B.2 What is per-window and what is per-process

The durable architectural fact this section exists to record.

| | Scope |
|---|---|
| Workspace, its documents, its `.cws` | **window** |
| Dock arrangement, all tool panels, `Reset Layout` | **window** |
| Technology cache, edit-session registries, undo | **window** |
| Mounted kits, PCell generators, device-worker providers | **window** (§5B.3) |
| Colour theme, application preferences, external-worker consent | **process** |
| The default Window Layout that `Reset Layout` resets *to* | **process** — one place to choose a layout |
| Path-and-mtime-keyed caches (symbols, SPICE peeks, generated wBond symbols) | **process, and correct** — the key already carries identity |

### 5B.3 The part that is not already scoped

**R41. A workspace's kits, PCell generators and device-worker providers belong to that workspace, and
opening another one must not unmount them.** Four registries are process-global and are cleared wholesale
on every workspace open — `PdkKitRegistry`, `KitLayoutGenerators`, `PCellRegistry`'s resolvers and
`ExternalDeviceRegistry`'s resolvers. Under one window that is correct and invisible. Under two it means
opening B silently unmounts A's kits, and the first symptom is A's kit parts drawing as pin-less
placeholders (§4.2 state 2) with nothing reported — in the window the user is not looking at.

**R42. A `pdk://` reference resolves against the referencing document's OWN parent workspace**, found by
walking up to the nearest ancestor `.cws`. This is R32's mechanism, applied to a second kind of reference,
and it is deliberately the same one: a document means what its own workspace says it means, whatever
happens to be open. It is also what makes §5C's kit story fall out instead of needing a mechanism of its
own.

A cell with **no ancestor `.cws`** that contains a kit part is unresolvable and stays that way. Unlike a
missing *technology* (R33, which prompts, because a `.ctech` can be chosen), a kit cannot be chosen — the
reference *is* the identity. It renders as §4.2 state 2, which is the correct, repairable, honest answer.

### 5B.4 What is deliberately not persisted

**R43. The set of open windows is not recorded anywhere.** There is no session-of-workspaces file. Each
window persists its own dock layout into its own `.cws` exactly as before, and a launch opens one window
per the launch action. Recording the set would raise a question this design has no reason to answer — what
the launch action means when three workspaces were open — and the answer would be guessed rather than
designed.

---

## 5C. External cell references (instancing a cell from another workspace)

**Status:** BUILT 2026-09-03 · `docs/sonnet-briefs/brief-multi-workspace-2-external-cell-refs.md` ·
supersedes the deferral in R37 *(specified 2026-09-03, built the same day)*.

A design in workspace **B** instances a cell that lives in workspace **A**, by reference — A's cell stays
the single copy, and B sees changes to it.

### 5C.1 The reference form — an alias, not a path

**R44. An external reference is an ALIAS resolved through the referencing document's own `.cws`.**

```
CellRef:   ws://RfFrontEnd/cells/Amp
.cws:      ReferencedWorkspaces: [ { Alias: "RfFrontEnd", Path: "../rf-front-end/.cws" } ]
```

This answers R37's open question in the direction R37 leaned. The reasons, in the order they matter:

1. **Relocating the other project is one `.cws` edit** rather than a rewrite of every document that
   referenced it — R37's own stated objection to raw paths.
2. **It states its own kind** (§4's taxonomy), so it can never be mistaken for a mistyped relative path,
   and it gets its own repair flow. This is the same reasoning `pdk://` was given.
3. **It names the workspace**, which is exactly what §5C.2's technology check and §5B.3's kit resolution
   both need. A raw path would make them infer the workspace from a resolved path — an inference that
   fails silently the moment the path is stale.
4. **`.cws` already has the shape** — referenced libraries (§5) are the same kind of entry for a `.clib`
   folder rather than a workspace. It remains *configuration, never membership*.
5. **The Project Tree already draws it** — §3.1's library sub-tree, §3.2's System.Warning + italics for an
   unresolvable one.

**R45. The path after the alias is workspace-relative**, resolved under the target workspace's root the
same way any workspace-relative path already is. One convention, not a second.

**R46. A raw `../../Other/cells/Amp` `CellRef` is not blessed.** It resolves today by accident — nothing in
§4's resolution constrains a relative path to the workspace — and it will go on resolving, because breaking
it would help nobody. But circuitRF never *writes* one, and it is not a documented feature. Blessing it
would create exactly the second convention R37 warned against.

**R44a. There are exactly two ways one comes into existence** *(built 2026-09-03)*: **`File ▸ Reference
Workspace…`**, which picks the other workspace's `.cws`, applies §5C.2's check BEFORE writing anything, and
names the alias (defaulting to that workspace's folder name); and MW3's drag-drop gesture, which reuses an
existing alias for the same target rather than adding a second one — two aliases for one workspace would
make the same cell reachable under two names, and a repair would then have to guess which.

**The palette and every existing placement path stay workspace-scoped.** An external reference is a
deliberate act, never something a user arrives at by not noticing. The tree renders each referenced
workspace as its own sub-tree of cells (§3.1's library shape), and a cell placed from there gets the alias
form because `ExternalCellRef.MakeCellRef` — the ONE producing rule, which every placement site now calls
instead of `Path.GetRelativePath` — returns it only for a cell inside a workspace this one references. A
cell in a referenced **library** keeps its relative path: a library is not a workspace.

**Cells shown from a referenced workspace are not this workspace's to change.** Rename Cell and Remove Cell
refuse on one, by the same `IsOutside` test §5A already applies to a foreign document — otherwise a window
that is not showing another project could rename a cell inside it and break every other workspace that
references it.

### 5C.2 Technology — the constraint that shapes the feature

**R47. An external cell may be PLACED in a layout only when the two layouts resolve to the same
technology.** Different technology → the placement is refused, naming both technologies, both workspaces
and what actually differs between them, and pointing at the two routes the user has: copy the cell
instead, or `Change Technology…`.

The reason is R32's, arriving through a third door. A layout's whole instance hierarchy is compiled against
**one** technology and layers are matched by numeric key; both starter technologies use `(1,0)`–`(8,0)`, so
A's Drill would silently become B's Substrate — right colours, right geometry, wrong meaning, nothing
missing and no warning (`layout-view.md` §13).

**R47a. "The same technology" means the same LAYER TABLE, not the same file.** The key set, and each key's
name and purpose. That is exactly what can be reinterpreted; a shape carries nothing but its key across the
boundary. Two workspaces holding COPIES of one process technology — the ordinary way to lay out two boards
for one fab — are therefore the same technology, and two identical files at different paths were the false
refusal the original path comparison produced. Colour, z-order, visibility and stipple are how a workspace
chose to DRAW a layer and are not compared; neither are the stackup or the DRC rules, which change what a
solver computes rather than what the layout view means.

**R47b. The refusal is at PLACEMENT, not at creation of the reference.** Creating a reference writes one
alias into a `.cws` and draws nothing, and a workspace holds as many `.ctech` files as it likes — so its
DEFAULT technology cannot decide anything about the one cell someone will place. `File ▸ Reference
Workspace…` warns when the two defaults disagree and creates the reference anyway; a workspace-default
refusal would also have blocked a purely schematic reference, which the paragraph below exempts outright.
*(Supersedes this section's original "refused at creation" — see `src/Ui/RESOLVED.md`, MW2 follow-up.)*

**Per-instance technology is an explicit non-goal**, not an oversight. Rendering a sub-hierarchy under a
different layer table changes what a single layout view *means*, and makes DRC's answer ambiguous. It is a
real feature and it is a different one.

**Schematics carry no technology and are unaffected** — the constraint applies to layout views only.

### 5C.3 Sub-cells, kits, and the three states

**R48. A reference is to one cell; its sub-cells come along by reference, always.** There is no
"reference this cell but copy its sub-cells" mode — it would produce a cell whose contents disagree with
its source. Copy-versus-reference for sub-cells is a question the **copy** gesture asks, never this one.

**R49. Kits inside a referenced cell resolve through R42**, which means the referenced cell's parts resolve
when **its own** workspace is open — the window doing the referencing needs none of A's kits. A referenced
cell whose workspace is not open has unresolved parts; that is repaired by opening it, and circuitRF does
**not** offer to mount another workspace's kit without opening that workspace. Mounting a kit is a side
effect nobody asked for, and it would make "which workspace is this part from" unanswerable.

**R49 is unchanged, and §5D is what makes obeying it free** *(added 2026-09-03).* The repair R49 offers —
"open that workspace" — was previously unperformable against a read-only share, because opening a
workspace meant writing its dock layout, its open-document list and its settled interpreter back into a
`.cws` the user cannot write; the product recommended a repair it could not carry out and reported the
failure as a save error at an unrelated moment. The problem was never this rule. It was that *"open it"*
implied *"write to it"*. A workspace now opens **read-only** and writes nothing (§5D R-sl2-5/-11), so the
repair costs a window and needs no exception here. Gated directly by
`ExternalCellReferenceTests.R_sl2_12_KitPartResolves_WhenItsWorkspaceIsOpenReadOnly`, beside the
`WorkspaceNotOpen` case it completes.

**R50. An external reference has three states, and they stay distinct** — the same discipline §4.2 applies
to the three missing-symbol states, and for the same reason: the right user response differs.

| State | Cause | Remedy offered |
|---|---|---|
| **Resolved (external)** | fine, but not from this workspace | none — marked only (R51) |
| *(orthogonal)* **Interface changed** | the cell resolves and draws correctly, but no longer publishes the interface the instance was placed against (§4.3) | none — reported and marked; *Accept the new interface* when the design is right |
| **Unresolved — workspace not open** | R49 | open that workspace in a new window |
| **Broken** | the alias does not resolve; or an unowned cell carrying kit content (§5B.3) | Locate… / copy into this workspace |

**R51. A resolved external reference is marked, not only a broken one**, and marking follows R36 exactly:
**chrome only, never the rendered geometry.** Seeing at a glance that a cell in your layout is not yours is
the entire safety story for this feature — it is the difference between a reference and a trap. Name the
source workspace where there is room (`Amp — [RfFrontEnd]`, R36's convention).

**R51 also carries §4.3's interface-change mark, as a SECOND and separate surround** *(2026-09-03)*. The two
say different things — "this cell is not yours" and "this cell changed shape underneath you" — and an
instance in a shared library routinely carries both at once, so one mark cannot serve for both. The
interface mark is drawn in the warning role, outside the external one, and obeys R36 for the stronger
reason: there the geometry merely belongs to someone else, here it is *correct* and it is the wires around
it that are in doubt.

### 5C.4 What must not silently break it

Three existing workspace operations reach a `CellRef` and none of them expects an external one. All three
are part of building §5C, not follow-ups to it.

- **Rename** — §4.1's last-segment rewriter must become an absolute-path comparison, or renaming a cell in
  B repoints a `ws://` reference into A. §4.1 records this.
- **Remove Cell** — `CellUsageScanner`'s reference count enumerates the current workspace only, so deleting
  a cell another workspace references reports "no references." It must count every **open** workspace, and
  must word its confirmation honestly: a referrer in a workspace that is not open cannot be found, so the
  prompt says *"no other open workspace references this"*, never *"nothing references this."*
- **Workspace archive** — a reference is recognised by whether the string resolves to a **file**, and a
  `CellRef` names a **directory**, so external references were invisible to the archive scan and the
  recipient got an archive referencing nothing.

**R53. An archive carries the referenced CELLS and enough of the other workspace's spine for the alias to
keep resolving** *(built 2026-09-03)*. `ExternalCellArchive` walks each `ws://` reference the workspace's
documents actually use, follows that cell's own hierarchy inside its workspace (bounded there — a reference
that leaves it is a chain nobody chose), and copies those cell folders at their own **workspace-relative**
offsets under `refs/<alias>/`, beside that workspace's `.cws` and its default `.ctech`. Keeping the offsets
is what lets each level's own relative `CellRef` go on resolving untouched (R48); copying the spine is what
keeps the alias pointing at a *workspace*. **The repoint is then one line** — `ReferencedWorkspaces[].Path`
in the referencing `.cws` — which is R44's first reason paying for itself. The row is **ticked by default**,
unlike a kit: a kit is the vendor's content and its bulk makes including it a judgement call, while these
are the user's own design and an archive without them opens showing placeholders. A `pdk://` reference
inside a copied cell travels only as the kit row it already belongs to; there is no second kit-packaging
path.

**R52. Headless behaves identically, including the refusals** — and the way it does so turned out to be
structural rather than new code *(corrected at build, 2026-09-03)*. The brief assumed the CLI would have to
walk an alias itself. It does not, and cannot: `circuitrf elab`/`sparam`/`hb` read a **`.cnl`**, and a
`.cnl` carries every cell inline as a `define … end` block (`CnlWriter.Write(tb, library)`). **Extraction is
where a cell reference — external or not — is resolved and absorbed**, so by the time a file exists for the
CLI to read there is no cell reference left in it and no workspace to walk up to. Either extraction resolved
the kit, and the definition is in the `.cnl`; or it did not, and `NetExtractor` reported the conflict — the
same conflict, in the same words, in the GUI and headlessly. The gate
(`tests/Ui.Tests/ExternalCellRefCliTests.cs`) is that the two elaborate to the same netlist.

An **archive** is the case that actually needed work here, and it is R53's.

---

## 5D. Read-only workspaces *(added 2026-09-03)*

**Status:** BUILT · `docs/sonnet-briefs/brief-shared-library-2-read-only-workspaces.md` · findings and the
per-site table in `src/Ui/RESOLVED.md`, the probe itself in `src/Design/RESOLVED.md`.

The workflow this exists for: a company keeps one workspace of cells on a network share, engineers
reference cells from it into their own designs, and **the share is read-only to everyone but the
librarian**, so nobody can overwrite the masters. Before this section circuitRF had no concept of
read-only at all — every path took the optimistic branch and discovered the truth at the write, which
means after the work rather than before it.

**R-sl2-A. circuitRF observes; it never enforces.** Read-only is a property of the filesystem, discovered
by attempting a write — never a `ReadOnly: true` field in `.cws`, which would be advisory, would have to
be maintained by hand, and would be wrong on precisely the machine where it mattered. The share's own
permissions are the enforcement, and a product-level permission model on top of a filesystem that already
has one is two sources of truth. `File.GetAttributes` reports the DOS read-only bit and says nothing about
a share ACL or a POSIX mode, so **the only portable answer is to try**: create a uniquely-named file in
the directory, delete it, and treat ANY failure as read-only. One probe per directory, memoised for the
session and dropped by the same `WorkspaceRootFinder.InvalidateCache` that drops the ancestor walk-up and
the alias table.

**R-sl2-B. There are two questions, and the per-DOCUMENT one is the one that decides Save.** "Is this
workspace writable" governs session state; "is this document's own directory writable" governs Save,
because a document open from a read-only library sits in a workspace this window did not open at all, and
a workspace can be writable while one cell folder inside it is not.

**R-sl2-C. A read-only workspace writes NOTHING** — not the dock layout, not the open-document list, not
the active document, not the settled interpreter, not the kit settings, not the tree view state, not
`.generated-cells`, not the results-layout migration. All of it is convenience state about a session, and
none of it is worth a failed write, a diagnostic, or a modal at the end of a session the user is trying
to close. Enforced in **one** method (`WorkspacePersistence.SaveToFileAtomic`, which now returns `bool`),
because the rule had fifteen call sites and a rule fifteen callers must remember is a rule that is true in
fourteen places. The two writes that ARE the user's own gesture — an alias just typed, a kit reference
just repaired — read the return value and refuse out loud; everything else is skipped silently.

**R-sl2-D. Opening a library read-only is the point of the feature, so it is never a refusal.** `File ▸
Open Workspace` on an unwritable workspace opens it, says so once, and carries on: browsing it, reading a
schematic, pushing into a hierarchy, seeing a cell's parameters and resolving the kits a referenced cell
needs are all read operations. This is what makes §5C R49's repair performable — see R49's own note.

**R-sl2-E. Save is disabled before the edit; editing is not.** Save is greyed with its reason stated, and
Save As is offered in its place — *"`Amp` belongs to `stdlib`, which is read-only on this machine. Save a
copy into your own workspace instead."* Editing stays allowed: reading a library cell and pulling it about
to understand it is legitimate and common, and the product must not make a file un-scrollable because it
is un-writable. The quit prompt routes each read-only document through Save As rather than offering a Save
that cannot succeed; Save All skips them and says so, because a sweep must not open one picker per
document. `Save As` into the current workspace is §5A.3's adopt gesture, unchanged.

**R-sl2-F. The marking is §5A.4's band, with read-only added to its wording** — not a fourth surface and
not a second colour. §5A.4's three surfaces already say *this belongs to another workspace*, which is half
the message; read-only is the other half. Two consequences: the band now appears for a read-only document
that is **not** foreign at all (the whole open workspace is the share), and §5A.4's "unusual-but-fine
accent, never an error colour" applies unchanged — **a read-only library document is a normal, supported,
desirable state**, and colouring it as a problem would mislabel the workflow this exists to support.

**R-sl2-G. Creating INTO an unwritable place refuses at the picker, naming the directory.** New Workspace,
Save Workspace As and the save-plan dialog's workspace step share one rule. The save plan is the reason it
has to be at the picker: `SavePlanExecutor` runs after a plan the user has *confirmed* and creates the
workspace folder and its `.cws` before it creates any cell, so discovering the parent is unwritable inside
the executor means a confirmed plan that cannot be carried out and a half-made workspace to clean up.

---

## 5E. Two people, one workspace *(added 2026-09-03)*

**Status:** BUILT · `docs/sonnet-briefs/brief-shared-library-4-concurrency-and-latency.md` §1 · findings in
`src/Ui/RESOLVED.md`, the mechanism in `src/Design/Workspace/WorkspaceLock.cs`.

`SwitchToWorkspace` has always refused to open one workspace twice, and its own comment gives the reason
exactly: two view models over one `.cws` means two independent edit-session registries over the same files —
two undo stacks, two dirty flags, last-save-wins. That reasoning is entirely correct and entirely
**process-local**: the check is `App.WindowShowing`, which enumerates this process's windows. Every
consequence it names is equally true of two people on two machines, and none of it was detected there.

**What is actually at stake is the `.cws`, not the documents.** A clobbered `.csch` is at least visible. Two
users with one workspace open both write dock layout, open-document list, kit settings and **the alias table**
on save; last writer wins, silently, and a referenced-workspace alias that vanished from someone else's file
is not a symptom anyone attributes correctly.

**R-sl4-A. An advisory lock file beside the `.cws`.** `.crf-open.json` — user, host, process id, time taken —
written when the workspace is opened **and is writable**, removed on close. A read-only workspace takes none
and needs none: nobody can write it, so there is nothing to lose to a race, and §5D's probe already answers
that question. It is hidden from the project tree by name (that list is an explicit set, **not** dotfiles in
general — the trap §5D's write probe fell into first).

**R-sl4-B. It is advisory, and the wording says so. This is not negotiable for a convenience gain.** The
notice names who and where — *"'stdlib' was opened by [a colleague] on lab-99 about 20 minutes ago…"* — states
plainly that circuitRF cannot tell whether they still have it open, and offers **both** answers, always: open
read-only, or open anyway. It never says locked, blocked, unavailable or denied, because it is none of those.
**A lock this product treated as authoritative would become a stale file that locks out a team**, which is a
worse failure than the one being prevented and is unfixable by anyone who does not know the file exists.

**R-sl4-B2. A session that cannot WRITE is never shown the notice, and takes no lock.** Last-writer-wins
is the only thing the notice bounds, and a read-only opener cannot be a writer. §5D's shared library is
exactly that case, so without this rule every engineer opening the corporate library would be told the
librarian is in there — a modal about a hazard they cannot cause, in front of the workflow this whole
section exists to support. §5D's own open-time line already tells them the fact that does apply.

**R-sl4-C. "Open read-only" is §5D's state, reached by choice rather than by permissions.** It routes through
the same writability answer, so it inherits every behaviour R-sl2-C…-F already built and tested — the `.cws`
write choke point skipping silently, Save disabled with its reason, Save As in its place, the provenance
band. "A workspace we have chosen not to write" and "a workspace we cannot write" want identical behaviour
from all of them, and a parallel flag would be the one that is true in fourteen places. The choice is
per-open and per-workspace, and is asked afresh every time.

**R-sl4-D. A stale lock is treated as stale, by two independent rules**, and costs nobody a dialog: it names
**this** host and a process id that is not running (a crash), or it is older than a generous threshold —
**hours, not minutes**, because an engineer leaves a workspace open over lunch and a threshold short enough to
catch a crash promptly is short enough to declare a colleague's live session dead. A lock from another host
can only ever be stale by the second rule; that machine's process ids say nothing here. Both rules are
heuristics and both may be wrong, which is acceptable precisely because R-sl4-B makes the answer overridable
either way — being wrong costs a notice, never access.

**R-sl4-E. No open file handle.** `CrashReporter` holds one with `FileShare.Read` so an exclusive open by a
probe proves ownership, and the single-instance check uses the same idiom. That is the right mechanism
**locally**, and its guarantees do not survive SMB, NFS or a dropped connection — a handle-based lock over a
share fails in the direction that produces a confident false statement about another person, which is the one
direction this must not fail in. The file is written and closed.

**R-sl4-F. Nothing merges.** Detect, report, let the user choose. Reconciling two `.cws` files or two `.csch`
files is a different product. Release removes only a lock this process took — deleting someone else's because
we happened to close a window would silently disarm the notice for the person who is actually in there.

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
- **Moving the workspace OPEN off the UI thread** *(raised 2026-08-25; the on-focus RESCAN already is,
  see §3)*. `SetWorkspace` costs ~92 ms of filesystem scan plus ~68 ms of VM-tree construction for a
  600-cell workspace, both on the UI thread, and it scales with the workspace. **The naive async version is
  not obviously better**: it trades a freeze for an empty tree of the same duration, which is a flash of a
  different kind — the very complaint that prompted the work. It becomes clearly right at a size where the
  freeze is long enough to need a "Scanning…" state to explain itself, and that state is the actual design
  question, not the threading. The two halves also differ in difficulty: **the scan is trivially safe
  off-thread** (`WorkspaceScanner` is framework-free), while **the VM build is not** — node VMs create
  `ObservableCollection`s and the root subscribes to `ProjectTreeFilterState`, so moving it needs the
  subscription split out and performed on the UI thread after the tree lands.
- **`FileSystemWatcher`** live tree refresh (v1 is manual + on-focus). **§5B raises the stakes**: with two
  workspaces open, an edit made in one window to a cell the other window references (§5C) is exactly the
  case a manual refresh is worst at. Not a blocker for §5B/§5C — the on-focus rescan fires when the user
  switches windows, which is the moment that matters — but the argument for a watcher is stronger now.
  **It stays deliberately unbuilt for a shared library** *(SL4, 2026-09-03)*: watchers over SMB are
  unreliable, `TechnologyCache` and `CellLayoutResolver` both already record why circuitRF does not use
  them, and the mtime-on-resolve check — now bounded by §4's T — is the mechanism and is enough. §3's
  referenced-subtree rule moves the tree the other way on purpose: a referenced library is read on the
  user's own gesture, not on a watcher and not on alt-tab.
- **§5B multiple open workspaces and §5C external cell references** — designed above, **not built**.
  Sequenced as `brief-multi-workspace-1-windows.md` → `-2-external-cell-refs.md` → `-3-workspace-dnd.md`;
  §5B ships alone and is independently useful, §5C depends on §5B.3's per-workspace kit scoping, and the
  workspace-to-workspace drag-drop gesture depends on §5C's Reference outcome being real.
- **In-tree cell MOVE** (§4.1) — still deferred, but for **one** remaining reason rather than two: §5C's
  absolute-path rewriter closes the same-named-cells blocker; a move changes the path *prefix*, which the
  rewriter still cannot express.
- **Layout view** (`.clay`, `layout/`, New Layout) — folder exists, command greyed; v2.
- **Rename-surviving cell references** / rename-fixes-references-from-tree — v1 is rename-at-risk (§4.1).
- **Docking documents across workspace windows** — an explicit **non-goal**, not a deferral (R39).
- **Per-instance technology** (a referenced cell rendered under its own layer table) — an explicit
  **non-goal** for §5C (R47), and a real feature in its own right if it is ever wanted.
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
