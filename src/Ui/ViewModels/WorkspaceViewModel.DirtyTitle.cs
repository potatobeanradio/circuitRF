using System.Collections.Generic;
using System.ComponentModel;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// The workspace window's title carries the same <see cref="DirtyMark"/> the tabs do whenever ANY
/// open document has unsaved work — the mark a user looks for next to the project's name, which the
/// tabs and the project tree alone did not give them (field feedback, 2026-10-15).
///
/// <para><b>Driven by each document's <c>Title</c></b>, not by a per-type dirty hook. Every document
/// type already puts the mark on its own tab title when its dirty state flips, so a Title change is
/// the one signal they all share; the answer itself is <see cref="HasAnyDirtyWork"/>, the same test
/// the quit prompt asks, so the title can never claim unsaved work the prompt would not offer to
/// save. A Data Display's tab title is not live, so its own <c>DirtyChanged</c> calls
/// <see cref="RefreshWindowTitle"/> as well (<see cref="WireDataDisplayTreeDirty"/>).</para>
/// </summary>
public partial class WorkspaceViewModel
{
    /// <summary>"name — circuitRF", without the mark; set when the workspace changes.</summary>
    private string _windowTitleBase = "circuitRF";

    private readonly HashSet<INotifyPropertyChanged> _titleWatchedDocuments =
        new(ReferenceEqualityComparer.Instance);

    private void WireDirtyWindowTitle()
    {
        _factory.DockableAdded  += (_, e) => WatchDocumentTitle(e.Dockable);
        _factory.DockableDocked += (_, e) => WatchDocumentTitle(e.Dockable);
        _factory.DockableMoved  += (_, e) => WatchDocumentTitle(e.Dockable);
        _factory.DockableClosed += (_, e) =>
        {
            if (e.Dockable is INotifyPropertyChanged npc && _titleWatchedDocuments.Remove(npc))
                npc.PropertyChanged -= OnWatchedDocumentPropertyChanged;
            RefreshWindowTitle();
        };
    }

    private void WatchDocumentTitle(IDockable? dockable)
    {
        if (dockable is not IDocument || dockable is not INotifyPropertyChanged npc) return;
        if (_titleWatchedDocuments.Add(npc))
            npc.PropertyChanged += OnWatchedDocumentPropertyChanged;
        RefreshWindowTitle();
    }

    /// <summary>
    /// A document's title changed: the workspace window's mark, and the title of a torn-off window showing it. A save that names the
    /// document (an untitled one's first save, a Save As) renames its tab in place, with no dock event, so a torn-off window kept the old
    /// name — <see cref="RefreshFloatingTitles"/> ran only on dock changes. Every document type renames this way, so it is answered here,
    /// once, rather than in each type's save.
    /// </summary>
    private void OnWatchedDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(IDockable.Title)) return;
        RefreshWindowTitle();
        PostRefreshFloatingTitles();
    }

    /// <summary>Re-reads whether anything is unsaved and re-composes <see cref="WindowTitle"/>.</summary>
    internal void RefreshWindowTitle()
        => WindowTitle = HasAnyDirtyWork() ? DirtyMark + _windowTitleBase : _windowTitleBase;
}
