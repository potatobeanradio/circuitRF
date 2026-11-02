# Brief — going back to an earlier state without closing the workspace

**Status: PHASE 1 COMPLETED 2026-09-08.** Investigation done 2026-09-08 against `main` @
`ff0dfc37`. Phases 2 and 3 are not started. What phase 1 measured, what it found on the way, and the
one consequence it accepts are in `src/Design/RESOLVED.md` and `src/Ui/RESOLVED.md`.

**Reported (owner, 2026-09-08):** changing state from the History window stalls the UI for a moment
and then the workspace window flashes as everything redraws. The ask is that it feel instantaneous —
and specifically that the window not be wiped and rebuilt when most of it did not change.

---

## 1. What actually happens today, and why it looks like that

`HistoryTool` ▸ *Go back* → `WorkspaceViewModel.GoBackTo` →
`ReloadWorkspaceAfterFilesChangedUnderneath` → **`SwitchToWorkspace(cws)`**.

That last call is the point. `SwitchToWorkspace` is the function that opens a *different* workspace.
Going back to an earlier state of the workspace you already have open runs the whole of it, on the
same path, with the same `.cws`. In order (`src/Ui/ViewModels/WorkspaceViewModel.cs:2204`):

| # | Step | Cost |
|---|------|------|
| 1 | `PersistOutgoingWorkspaceSession()` — writes the `.cws` | a file write, **onto the `.cws` the restore just restored** (see §3) |
| 2 | `TakeCloseCheckpoint(CurrentWorkspacePath)` | a whole-workspace **git commit, synchronous, on the UI thread**. Already measured — `LastCloseCheckpointMs` |
| 3 | `_registry.Clear()`, `_layoutRegistry.Clear()`, `ResetTechCache()`, the scratch-document maps | every open editor's session is dropped |
| 4 | `_factory.CreateDefaultLayout(...)`; **`Layout = newLayout`** | **the entire dock tree is replaced by a fresh default one.** Every tool panel and every document view is destroyed and reconstructed |
| 5 | `RestoreInstalledPdks()`, re-wire project tree / panes / analyses | the new instances have to be re-subscribed one by one |
| 6 | `Messages.Clear()` | the Messages panel is emptied and re-seeded |
| 7 | `ApplyRestoredDockShell` + `await ShellRenderedAsync()` | a frame is deliberately forced here |
| 8 | `DeleteGeneratedCellsFolder` + `await RegenerateAllGeneratedCellsAsync` | **every PCell in the workspace is regenerated from scratch**, which on a kit-backed design shells out to Python |
| 9 | `await RestoreOpenDocumentsAsync(...)` | every open tab is re-read **from disk**. A 27 MB board is ~154 MB of managed heap, and there is already a progress row for the case where this passes 1 s |
| 10 | `FinishRestoredDockLayout`, results migration, `OnWorkspaceOpenedForRevision` | |

**The flash is step 4 and the freeze is steps 2, 8 and 9.** Step 4 is also why the window appears to
blank before redrawing: the default layout is assigned to the live `DockControl` and the workspace's
own arrangement is applied afterwards, so there is a moment where the window is a freshly-built empty
shell. The
comment at that line says *"Nothing awaits between the clean-slate rebuild above and this, so the
default arrangement is never painted"* — that holds for the arrangement, but the content is
genuinely torn down and rebuilt either way.

**None of this is wrong for its own purpose.** `SwitchToWorkspace` is correct: opening another
workspace really must drop every registry, every cache and every panel. Reusing it for a restore was
the right first move — R-rc5-12b's second half (open documents reloaded, undo stacks discarded) is
genuinely required, and `ReloadWorkspaceAfterFilesChangedUnderneath` exists precisely so a restore
and an agent batch close cannot drift apart. What is wrong is the **scope**: a restore that changed
one schematic pays for a workspace switch that changed everything.

## 2. The fact the current design does not use

**Git already knows exactly which files differ, and it is almost never many.**
`WorkspaceRestore.Restore` holds both tree ids — `currentTree` (the pre-restore checkpoint) and
`target.TreeId` — a few lines apart. `git diff --name-status <currentTree> <targetTree>` answers
"which paths changed, and how" in one process, in milliseconds, before a single file is written.

Two things follow, and they are the whole brief:

1. **Only the changed files need writing.** Today `WorkspaceRestore.WriteFiles` runs
   `checkout-index -a -f`, which rewrites **every file in the workspace** whether it differs or not
   (`src/Design/Revision/WorkspaceRestore.cs:264`). That is why `FilesWritten` reports the whole
   workspace, and why the message a designer reads after going back says a number that has nothing
   to do with what changed.
2. **Only the documents whose files changed need reloading.** Everything else — the dock tree, the
   panels, the project tree, the Messages panel, the technology cache, and every open tab the
   restore did not touch — is already correct and can be left alone.

## 3. Two defects found on the way, both worth fixing regardless of the performance work

**(a) The restore's own `.cws` is overwritten a moment after it is restored.** `SwitchToWorkspace`
step 1 calls `PersistOutgoingWorkspaceSession()`, which writes the current session's open tabs and
dock arrangement into the `.cws` — the same `.cws` that `WorkspaceRestore` restored seconds earlier.
So going back to a state where a different set of tabs was open silently does not restore them, and
whichever arrangement the restore brought back is discarded before it is ever read.

**(b) Every restore probably writes a spurious "closed" entry.** Step 2 then takes a close
checkpoint. `WorkspaceCheckpoints.Take` skips an unchanged tree — but step 1 has just changed the
tree, by writing the `.cws`. So the tree test passes and an automatic entry is recorded, labelled as
a workspace close, immediately after every single restore. This is a plausible contributor to the
report, the same day, that every automatic entry reads alike, and it is history
noise generated by the act of reading history.

Both are consequences of routing a restore through the workspace-switch path, and both disappear
under §4's phase 1.

## 4. The work

### Phase 1 — a restore reloads what changed, and nothing else *(the whole of the visible win)*

- `RestoreResult` gains **`ChangedPaths`** — workspace-relative, with the kind (written / removed),
  taken from `git diff --name-status currentTree targetTree` inside `WorkspaceRestore.Restore`. It
  already computes both ends; this is one more `git.Run` on a read-only option set.
- `WriteFiles` writes **that set** via `checkout-index -f -- <paths>` rather than `-a`. The chunked
  path already exists for gate 17c's interrupt seam, so this is the same call with a different list.
  `FilesWritten` then means what its name says. *Keep the removal pass exactly as it is* — rule 2 of
  R-rc5-12c is about which files a removal may act on and is not a performance question.
- `GoBackTo` / `GoBackToVersion` stop calling `ReloadWorkspaceAfterFilesChangedUnderneath` and call a
  new **`ReloadChangedDocuments(IReadOnlyList<ChangedPath>)`** instead, which:
  - leaves `Layout`, every tool panel, the project tree, the Messages panel and the tech cache alone;
  - for each open document whose path changed: closes and reopens **that tab**, discarding its
    edit session — R-rc5-12b's requirement is per document, not per workspace, and is fully met by
    doing it to the documents that changed;
  - for each open document whose path did **not** change: does nothing at all;
  - refreshes the project tree from disk (cheap) and the History panel.
- **The agent-batch close keeps the same entry point.** `ReloadChangedDocuments` becomes the one
  implementation both callers share, which is the property `ReloadWorkspaceAfterFilesChangedUnderneath`
  was written to have. A batch also knows its changed set, so it gains the same benefit for free.
- The `.cws` write and the close checkpoint of §3 simply do not happen, because the workspace is
  never being left.

**Fall back, do not fail.** If the diff cannot be produced for any reason, call the full switch as
today. A restore that is slow is a nuisance; a restore that half-reloads is the class of defect §1.3
of the design doc is written against.

### Phase 2 — reload a document's content in place

Closing and reopening a tab still destroys its view, its scroll position and its zoom. For the
document kinds where the content can be swapped underneath a live view, do that instead — the undo
stack is still discarded (non-negotiable), but the frame the designer is looking at survives. Take
the kinds one at a time and only where it is genuinely safe; a wrong answer here is a view showing
one file's content over another's state.

### Phase 3 — get the remaining stalls off the UI thread

Only after phases 1 and 2, and only against a measurement:

- the pre-restore checkpoint (`WorkspaceCheckpoints.Take`) is a git commit on the UI thread. It has
  to complete before any file is written, so it cannot be made concurrent — but it can run off-thread
  with the panel showing that it is working.
- the per-document reads in phase 1's reload are the same reads `PreloadRestoredLayoutsAsync`
  already does on a background thread with a deferred progress row. Reuse it; do not write a second
  one.

## 5. What "instantaneous" should mean, and how to know

**Measure before writing any of it.** `LastCloseCheckpointMs` already exists as a precedent for
recording a boundary's cost to the trace listener rather than asserting it in a test — this repo
does not add timing tests (they measure the machine and flake), so the gate is a **counter**, not a
clock:

- **the dock layout is not rebuilt** — assert `Layout` is the same instance across a restore;
- **untouched documents are not reopened** — a restore that changes one of five open documents
  reopens exactly one, asserted by count;
- **no file the restore did not change is written** — assert on `ChangedPaths` against a fixture with
  a known one-file difference;
- **no automatic entry is created by the act of restoring** — the entry count after a restore is the
  pre-restore checkpoint and nothing else, which is §3(b) held shut.

Report the measured wall clock in the completion write-up, from the trace listener, on a workspace
with a real board in it.

## 6. Out of scope

- Anything that changes what is stored, or the shape of the history. This is a reload brief.
- The undo-stack rule. R-rc5-12b's second half stands exactly as written; phase 1 narrows *which*
  documents it applies to, and never whether it applies.
- The wording in the History panel — that was done separately on 2026-09-08.

## 7. On completion

Write the findings to `src/Ui/RESOLVED.md` and `src/Design/RESOLVED.md` as appropriate — **not to any
`CLAUDE.md`**. Record the measured before/after, and record §3(a) and §3(b) as their own findings
whether or not they turn out to have been the cause of anything else.
