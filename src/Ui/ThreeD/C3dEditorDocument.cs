// brief-em3d-43 R-em3d43-1a — the 3D editor as a Dock document. It mirrors EmSetupDocument: a .c3d is a
// cell's view — FilePath is set once and the tab carries the dirty mark every other editor does. The one
// exception is On Launch ▸ New 3D Design (3D editor bugs round 2): a scratch design whose path is where it
// WOULD be, written by its first Save As (IsScratch). The 3D pane inside is brief 28's, whose GPU device and buffers live in the view model's
// session, so a float or a re-dock re-imports three images and uploads no geometry (R-em3d28-1d).

using Dock.Model.Mvvm.Controls;
using CircuitRF.Ui.Commands;

namespace CircuitRF.Ui.ThreeD;

public sealed class C3dEditorDocument : Document, IUndoableDocument, IActivatableDocument, IFileBackedDocument
{
    private bool _activationFocusPending;
    public event Action? ActivationFocusRequested;
    public void RequestActivationFocus() { _activationFocusPending = true; ActivationFocusRequested?.Invoke(); }
    public bool ConsumeActivationFocus() { var p = _activationFocusPending; _activationFocusPending = false; return p; }

    private string _baseTitle;
    private bool _isDirty;

    public C3dEditorViewModel ViewModel { get; }
    public UndoRedoStack UndoRedo => ViewModel.UndoRedo;
    /// <summary>The TOP document's file — the tab's, whatever frame is pushed in (brief-em3d-48).</summary>
    public string FilePath => ViewModel.TopFilePath;

    /// <summary>The key this document is registered under among the open ones: its full path, or for a design held in a
    /// Hertzon project the project's path and the design's name.</summary>
    public string Key => ViewModel.Source.Key;

    /// <summary>HZN-3 — false for a design held in a project, which has no file of its own to write a copy to.</summary>
    public bool CanSaveAs => ViewModel.Source.ReadOnlyReason is null;

    /// <summary>HZN-3 — false for a design held in a project: it is not a file the workspace reopens by path.</summary>
    public bool IsPersistedFile => !IsScratch && ViewModel.Source is C3dFileSource;

    /// <summary>Nothing written yet: saving it is a Save As (<see cref="C3dEditorViewModel.IsScratch"/>).</summary>
    public bool IsScratch => ViewModel.IsScratch;

    /// <summary>The key a .c3d is registered under among the open documents: its full path.</summary>
    public static string KeyFor(string path) => Path.GetFullPath(path);

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (_isDirty == value) return;
            _isDirty = value;
            Title = _isDirty ? $"• {_baseTitle}" : _baseTitle;
        }
    }

    /// <summary>Save As landed: the tab follows the new file.</summary>
    public void FollowSavedAs()
    {
        _baseTitle = Path.GetFileName(ViewModel.TopFilePath);
        Id = KeyFor(ViewModel.TopFilePath);
        Title = _isDirty ? $"• {_baseTitle}" : _baseTitle;
    }

    /// <summary>
    /// brief-em3d-98 R-em3d98-5 item 2 — the "solved" glyphs after the tab's name, bound by the document tab's header template.
    /// A property of its own, never text in <see cref="Title"/>: the Window menu, the save-before-close prompts and the shell
    /// header all read <see cref="Title"/> and trim its <c>•</c>, and a glyph there would leak into every one of them.
    /// </summary>
    public SolveBadgeSet SolveBadges => ViewModel.SolveBadges;

    /// <summary>R-em3d98-5 item 4 — the floating window title's suffix (<c>solved (FEM, thermal)</c>), or null.</summary>
    public string? SolveTitleSuffix => ViewModel.SolveTitleSuffix;

    public C3dEditorDocument(C3dEditorViewModel viewModel)
    {
        ViewModel = viewModel;
        _baseTitle = viewModel.Source.Title;
        Id = viewModel.Source.Key;
        Title = _baseTitle;
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(C3dEditorViewModel.IsDirty)) IsDirty = ViewModel.IsDirty;
            else if (e.PropertyName is nameof(C3dEditorViewModel.SolveBadges)) OnPropertyChanged(nameof(SolveBadges));
            else if (e.PropertyName is nameof(C3dEditorViewModel.SolveTitleSuffix)) OnPropertyChanged(nameof(SolveTitleSuffix));
        };
    }
}
