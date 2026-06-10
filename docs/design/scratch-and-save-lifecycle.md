# circuitRF — Scratch Mode & Save Lifecycle Design

**Status:** Draft (rev 1) for review · **Date:** 2026-06-09 · **Phase:** 6g (post-project-tree)

Specifies the **first-run / "just start doing things" experience**: an in-memory **scratch workspace** the user
can edit and simulate immediately without saving, and a **save lifecycle** that materializes on-disk structure
only when the user saves — asking the minimum, losing nothing. Covers scratch mode, autosave/crash-recovery,
the **plan dialog** (the one-ask save), the **three-tier save**, and **`.cws` durability**. Companions:
`workspace-and-project-tree.md` (filesystem-is-truth, cells, Known Files, `IsTestBench`), `project-file-formats.md`
(`.cws`/`.ccell`/`.csch`/`.csym`), `parameter-editor.md`/`symbol-editor.md` (the documents being saved),
`src/Ui/CLAUDE.md`.

**The governing goals:**
1. **Start immediately.** At launch the user can create and edit (and *simulate*) a schematic with **zero**
   save friction — the empty workspace exists only in memory.
2. **Never lose work.** Graceful close prompts to save; an ungraceful crash is covered by autosave/recovery.
   A user can *always* get their bytes to disk, even with no workspace.
3. **Ask the minimum, as late as possible.** Saving materializes only the missing structure, asked once via a
   reviewable plan — the system does as much for the user as it safely can.

> **Tension this resolves (read first):** `workspace-and-project-tree.md` makes the **filesystem the source of
> truth** — the tree, primacy, and cell references all read from disk. Scratch work has **no disk presence
> yet**. So scratch is an explicit **separate world** ("in-memory scratch" vs. "materialized on disk"), and
> **materialization** is a one-way transition that happens at save. The two worlds must not blur — the project
> tree shows on-disk reality; scratch documents live in tabs until materialized.

---

## 1. Scratch mode (the in-memory world)

### 1.1 Launch state
- At app launch there is **no on-disk workspace** — an **in-memory scratch workspace** exists instead.
- **File → New Schematic (⇧N)** creates a new empty in-memory `.csch` document and shows it **immediately** in
  a content tab. The user edits freely. No save prompt to begin.
- The same applies to other in-memory documents the user may create before saving (symbols, data displays,
  layouts) — all may exist as **scratch documents** with no disk path yet.
- A scratch document is **dirty** from creation (it has unsaved content). The tab shows an unsaved indicator.

### 1.2 The two worlds
- **Scratch (in-memory):** documents with **no on-disk path**, owned by tabs/windows, invisible to the project
  tree (which reflects disk). Tracked by the workspace VM as "open dirty scratch documents."
- **Materialized (on-disk):** once saved, a document has a real path inside a real cell/workspace and becomes
  first-class — it appears in the tree, can be a primary view, can be referenced by instances.
- **Materialization is one-way and happens at save** (§4). After materialization a document is a normal
  on-disk document; its scratch identity is gone.

### 1.3 Scratch simulation
- A scratch schematic **can be simulated** without saving — net extraction + the engine run proceed, and a
  **`netlist.cnl` is produced like any other run** (the run is not special-cased).
- Because scratch has no workspace folder, scratch run artifacts (`netlist.cnl`, and run outputs) are written
  to the **scratch-session working directory** = the **recovery cache dir** (§2) — the same per-session
  location autosave uses. (One scratch-session location, not two temp dirs.)
- *(Exactly how/where scratch simulation **data/results** are persisted and surfaced is deferred to the next
  phase; this doc fixes only that scratch sim needs a writable scratch working dir, and that it is the
  recovery cache.)*

---

## 2. Autosave & crash recovery (minimal, v1)

Graceful close is handled by the save prompt (§3); a **crash or power loss** would otherwise lose in-memory
scratch work. v1 provides **minimal autosave-to-recovery-cache** so "never lose work" holds across ungraceful
exits too.

- **Recovery dir:** a per-session directory under the app data location
  (`…/LocalApplicationData/circuitRF/recovery/<session>/`), the same root family as `preferences.json`
  (`AppPreferencesIo`).
- **What:** periodically (and on significant edits) serialize **dirty in-memory scratch documents** to the
  recovery dir (their working content — `.csch`/`.csym`/etc. payloads — plus enough metadata to restore the
  tab: document name, type). Also the scratch-session scratch working dir (where scratch `netlist.cnl` lands).
- **Clear:** on a **clean save or clean exit**, clear the session's recovery dir (nothing to recover).
- **Restore:** at launch, if a recovery dir is **non-empty** (previous session didn't exit cleanly), offer to
  **restore** the recovered scratch documents (reopen them as scratch tabs). Decline → discard (or keep until
  next launch — keep it simple: offer once, discard on decline).
- **Scope:** this is a safety net for **scratch** (unsaved) work. Already-materialized documents are saved to
  their real files by the normal save path; autosave need not duplicate them (v1). *(Full per-document autosave
  of materialized files is a later enhancement.)*

---

## 3. The save lifecycle — materialize missing ancestors, asked once

### 3.1 The dependency chain
A document cannot be saved without the structure it depends on:
**schematic/symbol/layout → must live in a cell → which must live in a workspace.**
So saving scratch work is *"materialize the missing ancestors, topmost-first, then save the documents"*:
1. **No workspace?** → run the **New Workspace** path (`workspace-and-project-tree.md`; the custom dialog).
2. **No cell for a document?** → create/choose the cell it will live in.
3. **Save** each document into its cell's view sub-folder; **report** every path.

This **one algorithm** is shared by all document types (schematic/symbol/data-display/layout), parameterized by
view type — not a per-type save flow.

### 3.2 Decisions, not documents (why one ask scales)
The number of **asks** is driven by missing *ancestors*, not by document count:
- **Workspace:** at most **one** decision (create once).
- **Cells:** one decision **per distinct destination cell** — many documents targeting one cell = one cell
  decision.
- **Already-homed documents:** **zero** decisions (just save).
So even 5 unsaved documents is a small, reviewable set of decisions — surfaced **once**, up front, as a plan.

### 3.3 The Plan Dialog (the one-ask save) — HIG-compliant
When the user saves (Save / Save All, or via a close/quit/open-workspace prompt, §5) with scratch/dirty work,
present a **single plan dialog** — a reviewable, editable summary of everything that will be created and saved,
**not** a stream of prompts.

**Contents:**
- **Title + explanatory subtitle:** e.g. *"Save your work"* / *"circuitRF will create the following and save
  your documents."*
- **A global mode control** (segmented/radio) for new schematics:
  **( ) Each in its own cell** (default) · **( ) All in one cell:** `[<name> ▾]`
  - *Each in its own cell:* every orphan schematic seeds its own cell from its **document/tab name**.
  - *All in one cell:* all orphan **schematics** target one cell (multiple `.csch` in its `schematic/`); the
    **first becomes primary**, the rest non-primary views. The shared cell name **seeds from the first
    schematic's document name** (editable). A quiet hint notes only one will be primary.
  - The toggle **live-rewrites** the destination column below. Scoped to **schematics** in v1 (symbols/data
    displays default to their own cell; the table still allows manual per-row override).
- **A scrollable plan table**, one row per action, each row: an **icon**, an **action verb** (Create
  workspace / Create cell / Save), the **destination** (path or cell), and an **editable name** field
  (validated inline via `NameValidator`, error shown on the row). Rows include:
  - *Create workspace **<name>** at `<parent>/`* (only if no workspace; ≤1 such row).
  - *Create cell **<name>*** — annotated ***(TestBench)*** when the schematic(s) for that cell contain analysis
    (§3.4).
  - *Save **<file>*** → into its cell's view sub-folder.
  - Rows for documents that already have a home: just *Save **<file>*** (no create).
- **Buttons (HIG):** **Save All** is the **default** button (prominent, trailing); **Cancel** beside it
  (Esc). Standard platform button ordering/spacing; **button labels centered**; long paths **elide** with the
  full path on hover. All actions here are **creates/saves** (nothing destructive).

**Behavior:**
- Defaults are chosen so the **common case is a single "Save All" click** (names seeded from document names,
  mode = each-own-cell, workspace at the tracked default location).
- On Save All: materialize ancestors **at most once each** (the workspace once; each distinct cell once —
  *don't ask for the workspace twice if it was just created*), save all documents, then show a **completion
  Message listing every file written with its full path** (§3.5).
- Cancel aborts the whole plan (nothing written).

### 3.4 Analysis ⇒ TestBench
When creating a cell for a schematic, if that schematic contains **any analysis directives**, set
**`IsTestBench = true`** in the new `.ccell` (`workspace-and-project-tree.md` §2 — TestBench is a cell flag, not
a type). The plan row shows ***(TestBench)*** so the user sees it was recognized. In **all-in-one-cell** mode,
if **any** schematic going into the shared cell has analysis, the cell is a TestBench.

### 3.5 Reporting
After any save operation, the **Message system reports every file saved with its full path** (workspace `.cws`,
each `.ccell`, each `.csch`/`.csym`/etc.). Full paths, not just names — so the user (and developers) know
exactly what landed where.

---

## 4. The three-tier save (a user can always save)

A document can be saved at one of three levels of structure. The system **offers the most structured** option
but never forces it — the user can always at least get a file to disk.

1. **Into a cell** (full structure) — saved as `<cell>/<viewtype>/<file>`, a **first-class cell view** (can be
   primary, can be referenced by instances). This is the default the plan dialog drives toward.
2. **Loose into a workspace** — a raw view file (e.g. a `.csch`) written to a user-chosen location and
   **registered as a Known File** of the open workspace (`workspace-and-project-tree.md` §5). It is **not** a
   cell view (no primary status, not instance-referenceable) — an inspect/debug/escape-hatch artifact that the
   tree still surfaces (under Known Files).
3. **Loose as a plain file, no workspace** — just write the file to a chosen location. **No workspace, no
   Known-File membership, no tree.** The true "I just want a `.csch` on disk" path.

**Known-File-ness is a property of being referenced by an open workspace's `.cws`, not of the file itself.** So
the same loose `.csch` is tier-3 with no workspace, and becomes tier-2 (a Known File) if saved loose while a
workspace is open.

**No-workspace save:** if the user saves with no workspace at all, the system **offers once** to create a
workspace (tiers 1–2, the "do as much for the user as possible" path). If the user **declines**, fall back to
**tier 3** (plain file). The system tries to give structure but never forces it.

---

## 5. When saves are triggered (close / quit / open-workspace)

Unsaved scratch/dirty work is protected at every exit:
- **Close a scratch tab**, **close the workspace**, **open another workspace**, or **quit the app** → if there
  is dirty/scratch work, run the **plan dialog** (§3.3) so nothing is lost; the user may Save All, or decline
  ("Don't Save") to discard, or Cancel to abort the close/open/quit.
- A **clean** save/exit **clears the recovery cache** (§2).

---

## 6. `.cws` durability

The `.cws` holds **UI configuration only** — Dock layout, referenced libraries, Known Files, color scheme,
tree view-state (`workspace-and-project-tree.md` §5). **No design data** lives in it (that's in the cells). This
shapes its durability rules:

- **When written:** on **meaningful config changes** (debounced) and on **workspace close / app quit** — **not
  realtime** (no write on every pan/zoom/dock-drag; that churns disk for reconstructable data).
- **Atomic write:** write to a **temp file, then rename** over the real `.cws` (rename is atomic on Win/macOS/
  Linux). A crash mid-write leaves the old file or the new file intact — **never a half-written one**.
- **Tolerate corruption by rescan:** if `.cws` is **unreadable/corrupt** on open, **do not fail the workspace**
  — log it, start from **default config** (default dock layout, no remembered view-state), and **re-scan the
  folder** for the real content (cells/files). Because the filesystem is truth, the workspace's **substance
  survives a lost `.cws`**; only UI preferences are lost, and they regenerate. (Mirrors `AppPreferencesIo`'s
  corrupt-prefs-start-fresh behavior.)
- **Consequence (by design):** a corrupted `.cws` is **acceptable** — it degrades to "default layout," never
  "lost work." This is an argument for keeping `.cws` strictly UI-config; **nothing load-bearing may migrate
  into it.**

---

## 7. Implementation order (sequenced when built)

1. **Scratch documents + launch state:** in-memory scratch workspace; File → New Schematic (⇧N) opens an
   editable scratch tab; dirty-tracking + unsaved indicator; the two-world separation (scratch invisible to the
   tree). **✓ Done (Phase 6h Step 1).** `SchematicDocument` carries `FilePath?` / `IsScratch` / `IsDirty`;
   scratch starts dirty, `• ` prefix on tab title; `WorkspaceViewModel._scratchDocs` tracks open scratch docs
   separately from `_openDocsByPath`; `NewScratchSchematicCommand` (⇧⌘N / Ctrl+Shift+N, always enabled,
   no workspace required); auto-opens `Untitled-Schematic-1` at launch. Closing a scratch tab in step 1 loses
   it silently — the close-prompt is added in step 3.
2. **The materialize-ancestors algorithm + plan model:** the framework-free "given a set of dirty documents,
   compute the plan (workspace? which cells? which saves?), de-duped per ancestor" — testable headless.
3. **The Plan Dialog (HIG):** the reviewable/editable table + global mode toggle + TestBench annotation +
   Save All / Cancel; drives the algorithm; reports all paths.
4. **Three-tier save** wiring (into-cell / loose-Known-File / loose-plain-file; offer-then-fallback for
   no-workspace).
5. **Close/quit/open-workspace prompts** routed through the plan dialog.
6. **`.cws` durability:** atomic write, debounced + on-close, tolerate-corruption-by-rescan.
7. **Minimal autosave/recovery:** per-session recovery dir; periodic dirty-scratch serialization; restore-on-
   launch; clear-on-clean-exit; the shared scratch working dir for scratch `netlist.cnl`.

Order 1–3 deliver the core first-impression (start immediately + save once); 4–5 complete the save matrix; 6
hardens `.cws`; 7 adds crash safety.

---

## 8. Open / deferred
- **Scratch simulation data/results persistence + surfacing** — deferred to the next phase (this doc fixes only
  that scratch sim writes `netlist.cnl` to the scratch-session recovery working dir).
- **Full per-document autosave** of *materialized* files (v1 autosaves only dirty **scratch** work).
- **Recovery UX depth** (multiple stale sessions, partial restore) — v1 offers once, discard on decline.
- **Tracked location persistence** — the last-used New-Workspace location is **in-memory only** (not persisted
  across launches; Recent Workspaces persists instead — see the workspace-open fixes).
