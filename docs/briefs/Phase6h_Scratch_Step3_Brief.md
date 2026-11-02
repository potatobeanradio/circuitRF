# Phase 6h — Scratch Step 3: three-tier save + close/quit prompts + autosave/recovery (Claude Code / Sonnet)

The final scratch slice, completing "never lose work": **(A)** the three-tier save (loose-into-workspace as a
Known File, and loose plain-file with no workspace); **(B)** save prompts on close-tab / close-workspace /
open-workspace / quit; **(C)** minimal autosave-to-recovery-cache + restore-on-launch. Builds on step 2's
`SavePlan`/`SavePlanBuilder`/`SavePlanExecutor`. Read `scratch-and-save-lifecycle.md` §2, §4, §5 first.
Sub-gated; **report and stop between every layer.** Firewall green.

> Read first: `scratch-and-save-lifecycle.md` §2 (autosave/recovery), §4 (three-tier save), §5 (close/quit
> triggers), §6 (`.cws` atomic write — relevant to autosave write safety). Context: `src/Ui/Schematic/
> SavePlan.cs` + `SavePlanExecutor.cs` (step 2 — extend, don't duplicate), `src/Ui/Schematic/
> SchematicDocument.cs` (`IsScratch`/`IsDirty`/`FilePath`), `src/Ui/ViewModels/WorkspaceViewModel.cs`
> (`_scratchDocs`, the Save/plan wiring, `CurrentWorkspacePath`, `OpenWorkspace`/`NewWorkspace`,
> `QuitApplication`, `Messages`), `src/Ui/Views/Dialogs/SaveChangesDialog.axaml(.cs)` (an existing
> save/discard/cancel dialog — reuse if suitable), `src/Ui/Theming/AppPreferences.cs` (`AppPreferencesIo` —
> recovery dir lives in the same app-data family; the corrupt-prefs-start-fresh pattern to mirror),
> `src/Ui/Schematic/WorkspacePersistence.cs` (Known Files in `.cws`). Design docs win on any conflict.

## The spine
- **Three tiers (§4):** save into a cell (step 2, done) · loose into a workspace → **Known File** · loose plain
  file, no workspace. Known-File-ness = referenced by an open workspace's `.cws`, **not** a property of the
  file.
- **Offer structure, never force it (§4):** a no-workspace save offers to create a workspace once; if declined,
  write a plain file (tier 3).
- **No work lost (§5):** every close/quit/open path with dirty work prompts Save / Don't Save / Cancel.
- **Autosave is a safety net for scratch only (§2):** periodic serialize of dirty scratch docs to a per-session
  recovery dir; clear on clean save/exit; offer restore at launch if non-empty.
- **Scope fence:** these three. No new document types beyond what exists; analysis⇒TestBench stays the step-2
  hook.

---

## LAYER 1 — three-tier save (loose Known File + loose plain file)

Add a **"Save loose"** path alongside the step-2 into-cell plan (e.g. a Save-As-style action, or a choice in
the no-workspace flow):
1. **Loose into a workspace (tier 2):** write the scratch schematic as a `.csch` to a user-picked location
   (file picker), then **register it as a Known File** in the open workspace's `.cws` (append to Known Files,
   write `.cws`), Refresh, report the path. The doc materializes (FilePath set, dirty cleared, moved out of
   `_scratchDocs`) but is **not** a cell view.
2. **Loose plain file, no workspace (tier 3):** if no workspace is open, the save flow **offers once** to
   create a workspace (→ the step-2 plan). If the user **declines**, write the `.csch` to a picked location as
   a **plain file** — no workspace, no Known-File registration, not in any tree. Doc materializes (FilePath
   set, dirty cleared).
3. Both reuse `SchematicPersistence.SaveToFile`; report full paths (§3.5).

**Layer 1 gate:** with a workspace open, "save loose" writes a `.csch` and it appears under Known Files; with
no workspace, the flow offers a workspace and, on decline, writes a plain `.csch` (no workspace created); both
clear dirty + report the path. Report.

---

## LAYER 2 — save prompts on close / quit / open-workspace

Route every exit-with-dirty-work through a Save / Don't Save / Cancel prompt (reuse `SaveChangesDialog` if it
fits; else a small equivalent):
1. **Close a scratch/dirty tab:** prompt. Save → the appropriate save flow (plan for scratch, write-file for
   materialized); Don't Save → discard + close; Cancel → keep open.
2. **Close workspace / Open another workspace / New Workspace:** if any dirty scratch/doc exists, prompt with
   the **plan** (step 2) or per-doc save as appropriate; Cancel aborts the close/open.
3. **Quit:** if any dirty work exists, prompt; Cancel aborts quit. (Hook the window-close / `QuitApplication`
   path.)
4. Multiple dirty docs at quit/close-workspace → a single **plan dialog** (step 2) covers them, not N prompts.

**Layer 2 gate:** closing a dirty scratch tab prompts and respects Save/Don't Save/Cancel; quitting with dirty
work prompts and Cancel aborts the quit; opening another workspace with dirty work prompts first. Report.

---

## LAYER 3 — minimal autosave + restore-on-launch

1. **Recovery dir:** `…/LocalApplicationData/circuitRF/recovery/<session>/` (same app-data family as
   `preferences.json`).
2. **Autosave:** periodically (a timer, e.g. every ~30s) and/or on significant edits, serialize each **dirty
   scratch** document's content (`.csch` payload) + minimal metadata (doc name, type) into the recovery dir.
   Use an **atomic write** (temp + rename, §6) so a crash mid-write can't corrupt a recovery file.
3. **Clear on clean exit/save:** when a scratch doc materializes (step 2) or the app exits cleanly, remove its
   recovery file; clear the session dir on clean quit.
4. **Restore at launch:** if a recovery dir from a **prior** session is non-empty (ungraceful exit), offer to
   **restore** — reopen those scratch docs as tabs. Decline → discard. (Offer once; keep it simple.)
5. **Scope:** scratch (unsaved) docs only; materialized files are saved normally. Don't autosave materialized
   docs in v1.

**Layer 3 gate:** edit a scratch schematic, simulate the recovery path (kill/relaunch, or a test hook), and the
launch offer restores the unsaved scratch doc; a clean save clears its recovery file (no stale restore offer).
Report.

## Acceptance (step 3)
1. Three-tier save: into-cell (step 2) · loose→Known File · loose plain-file (no workspace, offered-then-
   declined); each materializes the doc + reports the path.
2. Close-tab / close-workspace / open-workspace / quit with dirty work all prompt Save / Don't Save / Cancel;
   multiple dirty docs use one plan dialog; Cancel aborts the action.
3. Autosave writes dirty scratch docs to a per-session recovery dir (atomic), clears on clean save/exit, and
   offers restore at launch after an ungraceful exit.
4. `dotnet build`/`dotnet test` green; firewall green; nothing else regresses.

## Guardrails
- **Three tiers; offer structure, never force it** (no-workspace declines → plain file).
- **Known-File-ness = referenced by `.cws`**, not a file property.
- **No work lost** — every close/quit/open with dirty work prompts; Cancel aborts.
- **Autosave = scratch only, atomic write, clear-on-clean-exit, restore-on-launch**; a materialized doc is
  never re-offered as scratch (the step-2 transition already moves it out of `_scratchDocs`).
- Reuse `SavePlan`/`SavePlanExecutor` and `SaveChangesDialog`; don't duplicate.
- Sub-gate the three layers; report and stop between each.
- Update `scratch-and-save-lifecycle.md` §7 status (step 3 done; the scratch/save lifecycle complete) and
  `src/Ui/CLAUDE.md`.

*Exit: the first-impression lifecycle is complete — start immediately, save through one plan (into cells, loose
Known Files, or plain files), be prompted before losing anything on close/quit, and recover unsaved scratch
work after a crash.*
