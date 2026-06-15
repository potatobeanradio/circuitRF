# Sonnet Brief — Data Display tab never shows its dirty indicator (•)

**Bug:** A Data Display document tab in the Content panel never shows the unsaved-changes bullet (`•`), even
after the display is edited. (`.dds` in the report = the `.cdd` data-display tabs.)

**Root cause (confirmed).** `DataDisplayDocument.IsDirty` drives the tab title bullet and is wired to follow
`DataDisplayDocumentViewModel.IsDirty` (it subscribes to that VM's `PropertyChanged`). But
`DataDisplayDocumentViewModel.IsDirty` is an `[ObservableProperty]` that is **never set** — nothing propagates the
display's actual unsaved state into it. The authoritative dirty signal is `DisplayWindowViewModel.HasUnsavedChanges()`
(serializes all tabs' config and compares to a baseline captured at construction/save — so it correctly ignores
view-only state like selection, zoom, theme). We need to (a) emit a "dirty may have changed" event from the
window when content changes, and (b) recompute `IsDirty = Window.HasUnsavedChanges()` on that event.

Two edit channels must both trigger the recompute:
- **Structural edits** (add/remove/move plots, markers, paste, tab add/remove) → go through
  `DataDisplayViewModel.UndoRedo` / window `TabUndoRedo` (`StateChanged`).
- **Inspector edits** (trace color, plot type, number format, +Trace, etc.) → do **not** push undo; they fire
  `PlotInspectorViewModel.PlotNeedsRedraw`, forwarded as `PlotContainerViewModel.PlotNeedsRedraw`.

Recomputing `HasUnsavedChanges()` (not trusting the trigger) means noisy triggers are harmless — a redraw from
selection recomputes to `false` and leaves the bullet off.

## Change 1 — `DataDisplayViewModel`: raise a `ContentChanged` event
Add `using System.Collections.Specialized;` and:
```csharp
/// <summary>Fired when something that affects the saved config may have changed
/// (structural undo edits OR inspector redraws). Consumers recompute HasUnsavedChanges().</summary>
public event EventHandler? ContentChanged;

private void RaiseContentChanged() => ContentChanged?.Invoke(this, EventArgs.Empty);
private void OnContainerRedraw(object? s, EventArgs e) => RaiseContentChanged();

private void OnPlotsCollectionChanged(object? s, NotifyCollectionChangedEventArgs e)
{
    if (e.NewItems is not null)
        foreach (PlotContainerViewModel c in e.NewItems) c.PlotNeedsRedraw += OnContainerRedraw;
    if (e.OldItems is not null)
        foreach (PlotContainerViewModel c in e.OldItems) c.PlotNeedsRedraw -= OnContainerRedraw;
    RaiseContentChanged();
}
```
Wire it in the constructor (after `_library` setup, before/after the initial `AddPlot` — either is fine since
the baseline is captured later by the window):
```csharp
_plots.CollectionChanged += OnPlotsCollectionChanged;
UndoRedo.StateChanged    += (_, _) => RaiseContentChanged();
```
**Leak caveat:** `ObservableCollection.Clear()` raises a `Reset` with `OldItems == null`, so the
CollectionChanged path won't unhook on clear. In `LoadFromTabConfigAsync`, unhook explicitly **before**
`_plots.Clear()`:
```csharp
foreach (var c in _plots) c.PlotNeedsRedraw -= OnContainerRedraw;
_plots.Clear();
```
(The add sites — `InternalAddContainer`, `LoadPlotContainerConfigAsync`, `PasteFromConfigAsync` — all add to
`_plots`, so `OnPlotsCollectionChanged` hooks them uniformly; no per-site wiring needed.)

## Change 2 — `DisplayWindowViewModel`: bubble a `DirtyChanged` event
```csharp
/// <summary>Fired when the document's unsaved state may have changed (content edit, save, or load).
/// The hosting DataDisplayDocumentViewModel recomputes IsDirty = HasUnsavedChanges() on this.</summary>
public event EventHandler? DirtyChanged;

private void RaiseDirtyChanged() => DirtyChanged?.Invoke(this, EventArgs.Empty);
private void OnActiveDisplayContentChanged(object? s, EventArgs e) => RaiseDirtyChanged();
```
- In `OnActiveTabChanged`, subscribe/unsubscribe the active tab's `ContentChanged` alongside the existing
  `PropertyChanged`/`UndoRedo.StateChanged` wiring:
  ```csharp
  if (_subscribedTab?.DataDisplay is { } old)
  {
      old.PropertyChanged       -= OnActiveDisplayPropertyChanged;
      old.UndoRedo.StateChanged -= OnUndoRedoStateChanged;
      old.ContentChanged        -= OnActiveDisplayContentChanged;   // NEW
  }
  …
  if (value?.DataDisplay is { } dd)
  {
      dd.PropertyChanged       += OnActiveDisplayPropertyChanged;
      dd.UndoRedo.StateChanged += OnUndoRedoStateChanged;
      dd.ContentChanged        += OnActiveDisplayContentChanged;    // NEW
  }
  ```
  (Mirror the same unsubscribe in `LoadAllAsync` where it detaches `_subscribedTab?.DataDisplay` before
  clearing tabs.)
- In `OnUndoRedoStateChanged` (already fires for the active tab's undo + window `TabUndoRedo`), also
  `RaiseDirtyChanged();` so tab add/remove flips the bullet.
- After each `CaptureBaseline();` call (in `SaveAllAsync` and `LoadAllAsync`), add `RaiseDirtyChanged();` so the
  bullet **clears on save** and resets on load.

## Change 3 — `DataDisplayDocumentViewModel`: consume it
```csharp
public DataDisplayDocumentViewModel()
{
    Window = new DisplayWindowViewModel();
    Window.DirtyChanged += (_, _) => IsDirty = Window.HasUnsavedChanges();
}
```
`IsDirty` is `[ObservableProperty]`; setting it raises `PropertyChanged`, which `DataDisplayDocument` already
turns into the `• ` title prefix. (This is also the field the close/quit pipeline left unset — it's now live,
though that pipeline correctly reads `HasUnsavedChanges()` directly and needs no change.)

**Perf note (optional):** `HasUnsavedChanges()` re-serializes the tab configs on each event. Data-display
configs are small, so direct recompute is fine. If an inspector slider drag (which fires `PlotNeedsRedraw` per
tick) feels heavy, debounce the recompute (coalesce on a short `DispatcherTimer`/Background post). Not required
for correctness.

## Tests (`tests/Ui.Tests`, headless — no rendering)
1. **`DataDisplay_DirtyBullet_OnStructuralEdit`**: new `DataDisplayDocumentViewModel`; assert `IsDirty == false`
   initially (baseline). `AddPlot()` on the active tab's `DataDisplay` → assert `IsDirty == true`.
2. **`DataDisplay_DirtyBullet_ClearsOnSave`**: after a dirtying edit, call `Window.SaveAllAsync(tempPath)` →
   `IsDirty == false`.
3. If feasible, **`DataDisplay_DirtyBullet_OnInspectorEdit`**: change a trace's `LineColorIndex` (or plot type)
   via the active container's inspector so `PlotNeedsRedraw` fires → `IsDirty == true` (guards the inspector
   channel, not just undo).

## Gate
Build 0W/0E; tests green. Manually: open/create a data display, edit it (add a plot, change a trace color or
plot type) → the tab shows `•`; save (⌘S / Save All) → bullet clears; selecting/zooming/panning alone does **not**
set the bullet.

## On completion
Note in `src/Ui/CLAUDE.md`: data-display tab dirty state is now live — `DataDisplayViewModel.ContentChanged`
(undo edits + container `PlotNeedsRedraw`) bubbles to `DisplayWindowViewModel.DirtyChanged`, and
`DataDisplayDocumentViewModel` recomputes `IsDirty = HasUnsavedChanges()` from it (authoritative, ignores
view-only state).
