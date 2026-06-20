# Phase 6g — Workspace Step 7: `.cws` refinement (atomic save, tolerate-corruption, view-state) (Claude Code / Sonnet)

The final project-tree step: make the `.cws` lifecycle correct — **atomic writes everywhere**, **debounced +
on-close** saves (not realtime), **tolerate a corrupt/missing `.cws` by rescanning**, persist **tree
view-state** (ordering + active filter) and **Known Files / library refs** through the real UI, and **remove
the dead `MemberFiles` list**. Much of the model already exists (`SaveToFileAtomic`, `KnownFiles`,
`LibraryRefs`, `DockLayout`, `ColorSchemeName`) — step 7 is mostly **wiring + durability**, not new model. Read
`workspace-and-project-tree.md` §5 and `scratch-and-save-lifecycle.md` §6 first. Sub-gated; **report and stop
between every layer.** Firewall green.

> Read first: `workspace-and-project-tree.md` §5 (`.cws` = config only; Known Files; library refs; tree
> view-state) + §3.3 (filter categories) + §3.1 (ordering); `scratch-and-save-lifecycle.md` §6 (atomic write,
> debounced + on-close, tolerate-corruption-by-rescan, `.cws` is UI-config only). Context code:
> `src/Ui/Schematic/WorkspacePersistence.cs` (`CwsFile` + `SaveToFile`/**`SaveToFileAtomic`** (exists!)/
> `LoadFromFile`; `MemberFiles` already noted "scanner ignores" — to **remove**; `KnownFiles`/`LibraryRefs`/
> `DockLayout`/`ColorSchemeName` present), `src/Ui/ViewModels/WorkspaceViewModel.cs` (`WriteWorkspaceFile` —
> currently `SaveToFile` non-atomic and drops most fields; `OpenWorkspace`/`OpenRecentWorkspace` load path —
> needs corruption tolerance; the loose-Known-File registration from scratch step 3), `src/Ui/ViewModels/
> Dock/ProjectTreeTool.cs` (`FilterState`, ordering, expand-state), `src/Ui/Schematic/WorkspaceScanner.cs`
> (rescan = the recovery path on corrupt `.cws`). Design docs win on any conflict.

## The spine
- **`.cws` is UI-config only** — never design data. So a corrupt/missing `.cws` is **recoverable**: log,
  start from defaults, **rescan the folder** for the real content. Never fail the open.
- **Atomic writes everywhere** (`SaveToFileAtomic`) — temp + rename; a crash never leaves a half-written
  `.cws`.
- **Debounced + on-close, not realtime** — coalesce config changes; flush on workspace close / quit.
- **Membership is the filesystem** — remove `MemberFiles` (already dead).
- **Scope fence (step 7):** `.cws` durability + the config fields' real wiring (view-state, Known Files,
  library refs, dock layout, color scheme). No new tree features beyond persisting what exists.

---

## LAYER 1 — atomic writes + tolerate-corruption-on-load

1. **All `.cws` writes use `SaveToFileAtomic`.** Replace `WorkspaceViewModel.WriteWorkspaceFile`'s
   `WorkspacePersistence.SaveToFile` (and any other `.cws` write — the scratch step-2 executor's `new CwsFile()`
   create, Known-File registration, etc.) with `SaveToFileAtomic`. (The create-new path may stay plain since
   the file doesn't exist yet, but prefer atomic uniformly.)
2. **Tolerate corruption on load.** In `OpenWorkspace` / `OpenRecentWorkspace`, the `.cws` load is currently in
   a `try` that swallows only the color-scheme step — make the whole open **never fail on a bad `.cws`**: if
   `LoadFromFile` throws (corrupt / format-version mismatch), **log a Message**, proceed with a **default
   `CwsFile`** (default dock layout, no view-state), and **rescan the folder** for content (the tree still
   shows cells — filesystem is truth). The workspace opens successfully either way.
3. Confirm a **missing** `.cws` (folder is a workspace dir but `.cws` absent) is likewise tolerated, OR is the
   "not a workspace" rejection per the Open flow — keep the existing "no `.cws` → not a workspace" gate for
   *Open*, but a `.cws` that exists-but-is-corrupt degrades to defaults (don't reject). State the distinction.

**Layer 1 gate:** every `.cws` write is atomic; opening a workspace whose `.cws` is deliberately corrupted
still opens (default layout + a logged Message + the tree populated by rescan); a valid `.cws` loads normally.
Report.

---

## LAYER 2 — debounced + on-close save; persist dock layout + color scheme + library refs + Known Files

1. **`WriteWorkspaceFile` writes the real config**, not a near-empty `CwsFile`: capture the current **dock
   layout** (serialize to `DockLayout` if the Dock library supports it — if not readily available, leave
   `DockLayout` null and note it), the active **color scheme** (already done), **library refs**, and **Known
   Files** (preserve the existing list; the scratch step-3 loose-save appends here).
2. **Debounced autosave of `.cws`** on meaningful config changes (filter/ordering/known-files/library-refs/
   layout) — a short debounce timer, not per-event; **flush on workspace close / app quit**. Do **not** write
   on every pan/zoom.
3. **Known Files + library refs round-trip:** adding a Known File (scratch step-3 loose save) or a library ref
   persists to `.cws` atomically and survives reopen; broken paths still surface as System.Warning in the tree
   (already wired by the scanner).

**Layer 2 gate:** changing config (e.g. add a Known File, change color scheme) persists to `.cws`
(debounced/on-close, atomic) and survives reopen; rapid changes don't thrash the disk; pan/zoom doesn't write
`.cws`. Report.

---

## LAYER 3 — persist tree view-state (ordering + filter) + remove MemberFiles

1. **Tree view-state in `.cws`:** persist the Project Tree's **active filter categories** (§3.3) and
   **custom ordering** (§3.1) — add a small `TreeViewState` to `CwsFile` (filter flags + ordering) written
   atomically/debounced; restore it on open so the tree comes back as arranged. (Expand-state is already
   preserved across in-session refresh; persisting it across launches is optional — state which you did.)
2. **Remove `MemberFiles`** from `CwsFile` (it's dead — the scanner ignores it; membership is the filesystem).
   Since alpha = no migration, just delete the property; old files with the field deserialize fine (extra JSON
   ignored) — confirm `PropertyNameCaseInsensitive`/default options ignore unknown members (they do by
   default).

**Layer 3 gate:** set a filter + ordering, close + reopen the workspace → the tree restores the same filter +
ordering; `MemberFiles` is gone from the model and writes no longer emit it; an old `.cws` with `member_files`
still loads. Report.

## Acceptance (step 7)
1. All `.cws` writes are atomic (`SaveToFileAtomic`); a corrupt `.cws` degrades to defaults + rescan (open
   never fails); the "no `.cws` → not a workspace" Open gate is retained.
2. `.cws` saves are debounced + on-close (not realtime) and persist dock layout (or null + note), color
   scheme, library refs, and Known Files — all round-tripping across reopen.
3. Tree view-state (filter + ordering) persists and restores; `MemberFiles` removed; old `.cws` files still
   load.
4. `dotnet build`/`dotnet test` green; firewall green; nothing else regresses. The project-tree arc
   (steps 1–7) is complete.

## Guardrails
- **`.cws` = UI config only; tolerate corruption by rescan** — never fail an open on a bad `.cws`; design
  data lives in cells.
- **Atomic writes everywhere; debounced + on-close, never realtime.**
- **Filesystem is truth** — remove `MemberFiles`; don't reintroduce a membership list.
- Reuse `SaveToFileAtomic` + the scanner; don't duplicate.
- **Scope fence:** `.cws` durability + persisting existing config — no new tree features.
- Sub-gate the three layers; report and stop between each.
- Update `workspace-and-project-tree.md` §8 status (step 7 done; project-tree arc complete) and
  `src/Ui/CLAUDE.md` (`.cws` atomic/debounced/tolerate-corruption; view-state persisted; MemberFiles removed).

*Exit: the `.cws` lifecycle is correct and durable — atomic, debounced, corruption-tolerant, persisting layout/
theme/refs/known-files/view-state — completing the workspace & project-tree arc (steps 1–7).*
