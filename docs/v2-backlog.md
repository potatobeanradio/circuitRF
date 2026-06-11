# circuitRF — v2 / deferred backlog

Cross-cutting list of features and enhancements deliberately deferred past v1 (alpha). Each entry notes WHY it
was parked and roughly what it would take, so future-you can pick it up without re-deriving the scope. Items
that live primarily in one design doc are cross-linked; this file is the index.

---

## Workspace / Dock

### Torn-off window position persistence
**Status:** parked (v1 ships tear-off working + open-document persistence; floats restore as TABS).
**Why parked:** v1 already persists *which* documents are open per workspace and restores them in order with
the correct active tab. Restoring torn-off documents to their *exact float positions* is a real feature with
real edge cases (monitor geometry that no longer exists on reopen, off-screen windows) and lower value than
the persistence work already banked. The fallback (reopen torn-off docs as tabs) is a defensible v1 stop.
**What it takes (~half a focused session basic; up to a full session with edge cases):**
- **L1 (small, lands clean):** extend the `.cws` `OpenDocuments` schema with a `floating: true` flag + host
  window bounds (X/Y/W/H), and write them on save. The host windows are already enumerable via
  `_wiredHostWindows` (see `TryWireHostWindowsUndo`); Avalonia `Window.Position` / `Width` / `Height` give the
  geometry. Bump `.cws` `format_version` (alpha: reject-on-mismatch, no migration).
- **L2 (instrument-first — the real work):** prove the programmatic Dock float API can place a document at a
  given rectangle BEFORE building the restore loop. The programmatic float path is fiddlier than the drag
  gesture (you orchestrate by hand what the drag does); window must exist + be shown before bounds can be set,
  so expect a deferred/post-layout pass like `TryWireHostWindowsUndo`. Do NOT code against remembered Dock API
  names — verify against the installed `Dock.Avalonia` 12.x surface first (same discipline that made tear-off
  itself work first try).
- **L3 (restore loop + guard):** open each floated doc, float it to saved bounds, set active; CLAMP restored
  windows to a currently-visible monitor so a doc saved on a now-disconnected display doesn't vanish off-screen.
**Edge cases that push toward a full session:** multiple tabs sharing one torn-off window; multi-monitor
coordinates; a float window that was itself re-docked into a split.
**Owning design doc:** `docs/design/workspace-and-project-tree.md` §9 (Open / deferred).
**Prereq already done:** tear-off + drag-back working (`CircuitRfDockFactory.DefaultHostWindowLocator` +
`DockControl.Factory` wiring + HostWindow styles); open-document persistence restoring docs as tabs.

---

(Add further deferred items below as they arise; keep the WHY + rough scope so they can be picked up cold.)
