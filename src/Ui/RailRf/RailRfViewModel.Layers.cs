// The board panel's own layer visibility (brief-railrf-20-layer-visibility.md R-rail20-1).
//
// ── WHY THE WINDOW HAS A LAYER LIST AT ALL ─────────────────────────────────────────────────────
//
// The board panel is the only place in railRF where the artwork is read, and the four overlay tabs
// all draw ON TOP of it. On a four-layer board with copper everywhere plus vias, "which layer is
// this run on" is unanswerable without turning layers off — and until this existed the only route
// was to open the `.ctech`, untick Vis, save, and come back (a first-time designer's report,
// 2026-09-20).
//
// That route is wrong on its own terms. LAYER VISIBILITY IS A PROPERTY OF THE VIEW, NOT OF THE
// PROCESS. A technology is a manufacturing document shared across a workspace, so using its Vis
// boxes as a per-window display switch makes one user's navigation another user's diff.
//
// ── ONE VISIBLE SET REACHES THE DRAWING, NOT TWO ───────────────────────────────────────────────
//
// The window OVERRIDES the technology in either direction — `RailDocument.HiddenLayers` hides a
// layer the `.ctech` draws, `ShownLayers` shows one it does not — and `RailDocument.Shows` is the
// one answer both the renderer and the map overlay are given (SyncHiddenLayers). Two answers to one
// question is how the overlay came to float a drop map over copper that was no longer drawn, which
// is the defect SyncHiddenLayers' own comment records.
//
// Until 2026-09-23 the window could only HIDE: its set was unioned with the technology's, so a
// layer whose Vis box was off in the `.ctech` was a disabled row here and the only way to see it
// was to edit the technology — exactly the route this list was built to retire (a field report).
// A layer the window has not decided about still follows the `.ctech`, live.
//
// ── AND IT NEVER RE-SOLVES ─────────────────────────────────────────────────────────────────────
//
// StackupSignature already draws this line and states why: a user turning a layer off is asking a
// question about the PICTURE, and losing the answer they just ran for it would make the toggle cost
// a re-solve. Nothing here goes near ClearResults.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CircuitRF.Design.Layout;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.RailRf;

public sealed partial class RailRfViewModel
{
    /// <summary>One row per drawing layer of the resolved technology — the board pane's own list.</summary>
    public ObservableCollection<RailLayerRowViewModel> BoardLayers { get; } = [];

    /// <summary>
    /// Whether the list is expanded. <b>Session state, unlike the layers themselves</b>: which layers
    /// a reader wants to see is about this board, but whether a disclosure triangle happens to be
    /// open is about this minute, and writing it to the `.crail` would dirty a document for a
    /// gesture that showed nothing new.
    /// </summary>
    [ObservableProperty]
    private bool _showLayerList;

    [RelayCommand]
    private void ToggleLayerList() => ShowLayerList = !ShowLayerList;

    /// <summary>The list's header — the count is what says the list is worth opening.</summary>
    public string LayerListHeader =>
        BoardLayers.Count == 0 ? "Layers"
        : HiddenLayerCount == 0 ? $"Layers ({BoardLayers.Count})"
        : $"Layers ({BoardLayers.Count - HiddenLayerCount} of {BoardLayers.Count} shown)";

    private int HiddenLayerCount => BoardLayers.Count(r => !r.Visible);

    /// <summary>True while this window draws any layer differently from the technology — what
    /// <see cref="FollowTechnologyCommand"/> has to undo, and the only state in which it does
    /// anything.</summary>
    public bool HasLayerOverrides => _document.HiddenLayers.Count > 0 || _document.ShownLayers.Count > 0;

    /// <summary>
    /// Clears the window's overrides, so every layer reads what the <c>.ctech</c> says now
    /// (R-rail20-1e).
    /// </summary>
    /// <remarks>
    /// <b>Without it the two drift with nothing able to reconcile them.</b> A user who has hidden
    /// four layers has no other way to find out what the document itself states — and no way back to
    /// it short of ticking four boxes and hoping they were the four.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(HasLayerOverrides))]
    private void FollowTechnology()
    {
        if (!HasLayerOverrides) return;
        _document.HiddenLayers.Clear();
        _document.ShownLayers.Clear();
        ApplyLayerVisibility();
        RefreshDirty();
    }

    /// <summary>
    /// Rebuilds the rows from the resolved technology, keeping this window's own overrides.
    /// </summary>
    /// <remarks>
    /// <b>The rows are SEEDED from the technology's <c>Visible</c></b> and are the window's own
    /// thereafter. An override naming a layer the new technology does not define is dropped here
    /// rather than kept: it cannot be shown in the list, so it would be an invisible override that
    /// nothing could clear but "Follow the technology".
    /// </remarks>
    private void RebuildBoardLayers()
    {
        BoardLayers.Clear();

        if (Board?.Technology is { } tech)
        {
            _document.HiddenLayers.RemoveWhere(k => !tech.Layers.Any(l => l.Key == k));
            _document.ShownLayers.RemoveWhere(k => !tech.Layers.Any(l => l.Key == k));

            foreach (var layer in tech.Layers.OrderBy(l => l.ZOrder).ThenBy(l => l.Key.Layer)
                                             .ThenBy(l => l.Key.Datatype))
                BoardLayers.Add(new RailLayerRowViewModel(
                    layer, _document.Shows(layer), !layer.Visible, SetLayerVisible));
        }

        AnnounceLayerList();
    }

    /// <summary>
    /// One row's box moved. <b>The only writer of the two override sets.</b>
    /// </summary>
    /// <remarks>
    /// An override is recorded only where the box now DIFFERS from the technology — a box ticked back
    /// to what the <c>.ctech</c> says removes the entry, so the layer follows the technology again
    /// and "Follow the technology" has nothing left to do for it.
    /// </remarks>
    private void SetLayerVisible(LayerKey key, bool visible)
    {
        bool techShows = Board?.Technology.Layers.FirstOrDefault(l => l.Key == key)?.Visible ?? true;

        bool changed = _document.HiddenLayers.Remove(key) | _document.ShownLayers.Remove(key);
        if (visible != techShows)
            changed |= (visible ? _document.ShownLayers : _document.HiddenLayers).Add(key);
        if (!changed) return;

        ApplyLayerVisibility();

        // Document state that queues no solve, so the funnel in QueueResolve never sees it — the
        // mark has to be refreshed here or hiding a layer would look free. RailRfViewModel.Panes'
        // own rule, for the same class of edit.
        RefreshDirty();
    }

    /// <summary>
    /// Pushes the current hidden set at the drawing — the canvas's technology and the overlay's
    /// hidden set, which are the two halves R-rail20-1c says must move together.
    /// </summary>
    private void ApplyLayerVisibility()
    {
        SyncCanvasTechnology();
        SyncHiddenLayers();

        foreach (var row in BoardLayers)
            row.Refresh(row.HiddenByTechnology ? _document.ShownLayers.Contains(row.Key)
                                               : !_document.HiddenLayers.Contains(row.Key));

        AnnounceLayerList();
    }

    private void AnnounceLayerList()
    {
        OnPropertyChanged(nameof(LayerListHeader));
        OnPropertyChanged(nameof(HasLayerOverrides));
        FollowTechnologyCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// What the CANVAS draws with: the board's technology where this window overrides nothing, and a
    /// clone of it with this window's visibility where it does.
    /// </summary>
    /// <remarks>
    /// <b>A clone, and never the resolved instance.</b> <c>TechnologyCache</c> hands back a SHARED
    /// object — the same one every open layout document is drawing with — so flipping
    /// <c>Visible</c> on it would not narrow one picture but every later one taken through that
    /// cache, in the layout editor beside this window included.
    /// <see cref="TechnologyLayerSelection"/> exists for exactly this and says so at length.
    ///
    /// <para><b>And the resolved instance where nothing is overridden</b>, deliberately: a clone per
    /// adoption would make <see cref="AdoptTechnology"/>'s reference test compare two copies of one
    /// technology, and it would cost a reflective copy of the whole layer table on every live
    /// <c>.ctech</c> keystroke for a picture that is not being narrowed.</para>
    /// </remarks>
    private void SyncCanvasTechnology()
    {
        if (BoardLayout is not { } canvas || Board?.Technology is not { } tech) return;

        canvas.Technology = !HasLayerOverrides
            ? tech
            : TechnologyLayerSelection.WithVisibility(tech, _document.Shows);
    }
}
