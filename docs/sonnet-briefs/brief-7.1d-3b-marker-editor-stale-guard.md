# Sonnet Brief — Phase 7.1d-3b: marker-editor stale-marker guard (minimal undo tidy)

**Design:** `docs/design/data-display.md` §7.1d-3 ("tidy marker undo edge-cases"). **Scope decision (owner):
MINIMAL** — do **not** make marker edits undoable; just stop an open marker editor from mutating a marker that
has been removed. One file: `DataDisplay/ViewModels/MarkerEditorViewModel.cs`. No undo-command changes, no
PlotControl changes, no behavior change for live markers.

## The edge case (confirmed)
`PlotControl.ShowMarkerEditorFlyout` opens the editor in a **light-dismiss** flyout bound to a fresh
`MarkerEditorViewModel(infoVm)`. Clicking elsewhere dismisses it — but a **keyboard undo (Ctrl+Z)** does not.
So: double-click a marker glyph → editor opens → press Ctrl+Z to undo the marker's creation
(`RemoveMarkerCommand`/`AddMarkerCommand.Undo` → `InternalRemoveMarker` → `trace.Markers.Remove(marker)` +
info-box rebuild) → the editor is still open, now bound to a **detached** marker. Every
`MarkerEditorViewModel` setter writes `_marker.X = value` directly, so edits silently mutate a removed marker
object (and on redo the resurrected marker carries those phantom edits). The model writes are the only unsafe
part — the read-only display props (`OwnDataLine`/`OwnZ0Line`/`MultiLines`) just compute strings and are safe.

## Fix — a liveness guard in the VM (self-contained)
Add a liveness check and early-return from every model-mutating path. A marker is "live" iff it is still on
its trace:
```csharp
// _parent is null at design time; _parent.Trace is the marker's trace.
private bool MarkerIsLive => _parent is not null && _parent.Trace.Markers.Contains(_marker);
```
Add `if (!MarkerIsLive) return;` as the **first line** of each mutator (before the `_marker.X = value` write):
`OnNameChanged`, `OnMatrixFormatChanged`, `OnStyleChanged`, `OnDigitsChanged`, `OnUseNormalizedChanged`,
`OnFormatStringChanged`, `OnIsMultiChanged`, `OnIsDeltaChanged`, and `CommitFrequency()`.

Notes:
- The backing fields are set directly in the constructors (not via the properties), so these handlers don't
  fire during construction — the guard only affects post-open user edits. Design-time (`_parent is null`) is
  naturally a no-op.
- The check uses reference identity (`List.Contains` on the same `Marker` instance). After undo removes the
  marker → not live → edits no-op; after redo re-adds the **same** instance → live again → edits resume
  correctly. No resurrection of phantom edits.
- Leave the read-only display properties as-is (already `_parent`-null-guarded; safe on a detached marker).

## Out of scope (per the owner's MINIMAL choice — do NOT implement)
- Making frequency moves (drag / arrow / typed) or any editor field edits undoable.
- "Change to trace" undo.
- Auto-closing the editor flyout on marker removal (would need PlotControl to hold + hide the flyout — a nicety,
  not required; the guard already prevents corruption and the flyout is light-dismiss).

## Gate (verify in the running app)
1. Double-click a marker glyph to open the editor; press Ctrl+Z to undo the marker's creation. The marker
   disappears, no exception, and interacting with the still-open editor does nothing to the (removed) marker.
2. Press Ctrl+Shift+Z (redo): the marker returns in its pre-undo state — it does **not** carry any edits made
   while it was detached.
3. Normal editing of a live marker (Name/Frequency/Format/Precision/Digits/Size/Normalize/Multi/Δ) is
   unchanged. Builds green.

## On completion
This completes **7.1d-3** (with 7.1d-3a restyle). Tick the 7.1d-3 bullet in `docs/design/data-display.md` and
add a "Phase 7.1d-3 — COMPLETE" status line (marker editor restyled to the inspector idiom + stale-marker
guard; undo coverage intentionally left as-is per owner). Note it in `src/Ui/CLAUDE.md`. Next sub-phase per the
plan: **7.2** (DataSet as the trace data source).
