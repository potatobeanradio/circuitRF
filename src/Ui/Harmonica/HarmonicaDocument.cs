using System;
using System.IO;
using CircuitRF.Ui.Commands;
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;

namespace CircuitRF.Ui.Harmonica;

/// <summary>Document-shell view model for one harmonicaRF tab. Wraps the real
/// <see cref="HarmonicaViewModel"/> rather than merging with it, exactly as
/// <c>DataDisplayDocumentViewModel</c> wraps <c>DisplayWindowViewModel</c>.</summary>
public sealed partial class HarmonicaDocumentViewModel : ObservableObject
{
    [ObservableProperty] private bool _isDirty;

    public HarmonicaViewModel Harmonica { get; }

    /// <summary>R-h9r2-11 — the docked-focus state <see cref="SetNativeMenuDockedFocus"/> last reported,
    /// held here even while <see cref="NativeMenuDockedFocusChanged"/> has no subscriber yet. A document
    /// that is already the active dockable at the instant its view is first realized receives that
    /// notification BEFORE <c>HarmonicaView.OnDataContextChanged</c> has wired the delegate — the same
    /// race <c>ConsumeActivationFocus</c> exists to close for activation focus. Without this, the
    /// notification is simply lost and the native menu never attaches until the user switches tabs away
    /// and back.</summary>
    private bool _nativeMenuDockedFocusPending;
    private Action<bool>? _nativeMenuDockedFocusChanged;

    /// <summary>
    /// R-h9a-3's action seam. <c>HarmonicaView</c> installs this in <c>OnDataContextChanged</c> — the
    /// same "view injects a delegate into its VM" shape <c>DisplayWindowViewModel</c>'s own
    /// <c>SetLoadRunResultsAction</c> already uses — so <c>WorkspaceViewModel</c>'s dock-level focus
    /// tracking (which has no reference to this document's realized view) can tell it "you are now
    /// the active docked tab" (<c>true</c>) or "you no longer are" (<c>false</c>) without either side
    /// needing to know about Avalonia's <c>NativeMenu</c> type — that stays entirely inside the view.
    /// Deliberately plain <c>Action&lt;bool&gt;</c>, not an Avalonia-typed signature, so this class
    /// stays framework-free.
    ///
    /// <para><b>R-h9r2-11:</b> setting this (view wiring up) immediately re-applies whatever docked-focus
    /// state <see cref="SetNativeMenuDockedFocus"/> most recently reported, rather than waiting for the
    /// next real focus change — closing the race described on <see cref="_nativeMenuDockedFocusPending"/>.</para>
    /// </summary>
    public Action<bool>? NativeMenuDockedFocusChanged
    {
        get => _nativeMenuDockedFocusChanged;
        set
        {
            _nativeMenuDockedFocusChanged = value;
            value?.Invoke(_nativeMenuDockedFocusPending);
        }
    }

    /// <summary>R-h9r2-11 — the ONE way <c>WorkspaceViewModel</c>'s dock-level focus tracking reports
    /// docked-focus changes now; it must never invoke <see cref="NativeMenuDockedFocusChanged"/>
    /// directly, or a state reported before the view wired up is lost rather than remembered here.</summary>
    public void SetNativeMenuDockedFocus(bool hasFocus)
    {
        _nativeMenuDockedFocusPending = hasFocus;
        _nativeMenuDockedFocusChanged?.Invoke(hasFocus);
    }

    public HarmonicaDocumentViewModel(HarmonicaViewModel? vm = null)
    {
        Harmonica = vm ?? new HarmonicaViewModel();
        Harmonica.DirtyChanged += () => IsDirty = true;
    }

    /// <summary>Clears the dirty flag after a successful save.</summary>
    public void MarkSaved() => IsDirty = false;
}

/// <summary>
/// Dock Document for an open harmonicaRF instrument.
///
/// <para>Mirrors <c>DataDisplayDocument</c>'s shape exactly — scratch vs. materialized keyed on
/// <see cref="FilePath"/>, dirty mirrored FROM the VM (the VM is the source of truth; the document
/// reflects it, never the reverse), a bullet in the tab title while dirty, and
/// <see cref="IActivatableDocument"/> so the view takes keyboard focus on tab activation without a
/// click.</para>
/// </summary>
public sealed class HarmonicaDocument : Document, IActivatableDocument
{
    private bool _activationFocusPending;
    public event Action? ActivationFocusRequested;
    public void RequestActivationFocus() { _activationFocusPending = true; ActivationFocusRequested?.Invoke(); }
    public bool ConsumeActivationFocus() { var p = _activationFocusPending; _activationFocusPending = false; return p; }

    private string _baseTitle;
    private bool   _isDirty;

    public HarmonicaDocumentViewModel ViewModel { get; }

    /// <summary>Absolute path of the <c>.charm</c>, or null for a scratch document.</summary>
    public string? FilePath { get; private set; }

    public bool IsScratch => FilePath is null;

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

    public HarmonicaDocument(string title, HarmonicaDocumentViewModel vm, string? filePath = null)
    {
        _baseTitle = title;
        Id         = title;
        Title      = title;
        FilePath   = filePath;
        ViewModel  = vm;

        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(HarmonicaDocumentViewModel.IsDirty))
                IsDirty = ViewModel.IsDirty;
        };
    }

    /// <summary>Scratch → materialized, or a Save-As onto a new path. Safe to call repeatedly.</summary>
    internal void OnSavedToPath(string path)
    {
        FilePath   = path;
        _baseTitle = Path.GetFileNameWithoutExtension(path);
        Id         = path;
        ViewModel.MarkSaved();
        Title      = _isDirty ? $"• {_baseTitle}" : _baseTitle;
    }
}
