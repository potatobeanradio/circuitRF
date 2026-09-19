using System;
using System.IO;
using CircuitRF.Design.Smith;
using CircuitRF.Ui.Commands;
using Dock.Model.Mvvm.Controls;

namespace CircuitRF.Ui.Smith;

/// <summary>
/// Dock <c>Document</c> for one open Smith Chart — <b>a document, not an application</b>
/// (<c>R-smith4-1</c>, <c>docs/design/smith-chart.md</c> §5.1, owner decision).
/// </summary>
/// <remarks>
/// <b>harmonicaRF, wBond and railRF each open a window of their own and each has a reason; this has
/// neither.</b> harmonicaRF and wBond ship as standalone binaries and railRF's centre panel is the
/// layout editor's canvas. So a `.csmith` opens as a docked tab exactly as a `.cdd` does, and there is
/// no standalone <c>smithRF</c> binary and none is proposed.
///
/// <para>Modelled on <c>DataDisplayDocument</c>, field for field: scratch vs. materialized keyed on
/// <see cref="FilePath"/>, dirty mirrored FROM the view model (the view model is the source of truth;
/// the document reflects it, never the reverse), a bullet in the tab title while dirty, and
/// <see cref="IActivatableDocument"/> so the view takes keyboard focus on tab activation without a
/// preliminary click.</para>
///
/// <para><b>Why <see cref="IUndoableDocument"/> where the Data Display implements the smaller
/// <see cref="IEditHistoryDocument"/> directly.</b> <c>R-smith4-1</c> names
/// <c>IEditHistoryDocument</c> because that is the interface the shell's Undo command is typed to —
/// the one that exists because a FLOATING Data Display once undid an edit in an unfocused schematic,
/// macOS's menu bar being app-global and the same <c>NativeMenu</c> attached to every torn-off window.
/// <c>IUndoableDocument</c> <i>is</i> an <c>IEditHistoryDocument</c>, and is the documented shortcut
/// for "a document whose edit history IS an <c>UndoRedoStack</c>", which this one's is. Taking it
/// answers all six questions with no code and gives the menu item a real description — <c>Undo
/// "Conjugate generator"</c> rather than a bare verb. The Data Display cannot take it only because its
/// history is the ported <c>UndoRedoManager</c>; there is no reason to copy that limitation here.</para>
/// </remarks>
public sealed class SmithChartDocument : Document, IActivatableDocument, IFileBackedDocument,
                                         IUndoableDocument
{
    // ── Undo / Redo — the shell's own Ctrl/Cmd+Z, routed here ────────────────

    /// <inheritdoc/>
    public UndoRedoStack UndoRedo => ViewModel.UndoRedo;

    // ── Activation focus — the view claims the keyboard on tab-switch ────────

    private bool _activationFocusPending;
    public event Action? ActivationFocusRequested;
    public void RequestActivationFocus() { _activationFocusPending = true; ActivationFocusRequested?.Invoke(); }
    public bool ConsumeActivationFocus() { var p = _activationFocusPending; _activationFocusPending = false; return p; }

    // ── Edit ▸ Cut / Copy / Paste — the shell's own menu, routed here ────────

    /// <summary>
    /// Raised by the shell's Edit ▸ Copy / Paste. <b>The view decides what they mean</b>
    /// (<c>R-smith7-9</c>): Copy is the chart when the chart has focus and the network when the
    /// network does, which is a question only the focused pane can answer.
    /// </summary>
    /// <remarks>
    /// <c>LayoutDocument</c>'s own shape, for its own reason — a document's clipboard verbs belong to
    /// whatever has the keyboard inside it, and <c>WorkspaceViewModel.InvokeClipboardAsync</c> has no
    /// way to know that. <b>There is deliberately no Cut</b>: cutting the network would leave the
    /// document with no cascade and the chart with nothing on it, which is Delete on every element
    /// and is spelled that way.
    /// </remarks>
    public event Action? CopyRequested;

    /// <inheritdoc cref="CopyRequested"/>
    public event Action? PasteRequested;

    public void RequestCopy()  => CopyRequested?.Invoke();
    public void RequestPaste() => PasteRequested?.Invoke();

    private string _baseTitle;
    private bool   _isDirty;

    public SmithChartViewModel ViewModel { get; }

    /// <summary>Absolute path of the <c>.csmith</c>, or null for a scratch document.</summary>
    public string? FilePath { get; private set; }

    /// <summary>
    /// True when this document has no on-disk path yet.
    /// </summary>
    /// <remarks>
    /// <b>A scratch <c>.csmith</c> opens with no workspace loaded</b>, on harmonicaRF's own terms
    /// (<c>R-smith4-1</c>), and Save As gives it a home. Nothing in this type or its view model reads a
    /// workspace; what a workspace supplies is the project-tree node and a starting folder for the
    /// picker.
    /// </remarks>
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

    public SmithChartDocument(string title, SmithChartViewModel vm, string? filePath = null)
    {
        _baseTitle = title;
        Id         = filePath ?? title;
        Title      = title;
        FilePath   = filePath;
        ViewModel  = vm;

        ViewModel.DocumentDirectory = filePath is null ? null : Path.GetDirectoryName(filePath);
        ViewModel.DirtyChanged += () => IsDirty = ViewModel.IsDirty;
    }

    /// <summary>Opens an existing <c>.csmith</c>. The read is <see cref="SmithDesignIo"/>'s.</summary>
    /// <exception cref="InvalidDataException">The file is empty, from a newer circuitRF, or not well
    /// formed — the sentence names what is wrong with it.</exception>
    public static SmithChartDocument Open(string path)
    {
        string full   = Path.GetFullPath(path);
        var    design = SmithDesignIo.LoadFromFile(full);
        var    vm     = new SmithChartViewModel(design);

        vm.DocumentDirectory = Path.GetDirectoryName(full);
        vm.MarkSaved();

        return new SmithChartDocument(Path.GetFileNameWithoutExtension(full), vm, full);
    }

    /// <summary>
    /// Scratch → materialized, or a Save-As onto a new path. Safe to call repeatedly.
    /// </summary>
    /// <remarks>
    /// <see cref="SmithChartViewModel.DocumentDirectory"/> moves with it, because that is what an
    /// S1P/S2P element's relative <c>FileRef</c> resolves against — a Save As that left it pointing at
    /// the old folder would make the same document evaluate differently depending on where it had been.
    /// </remarks>
    internal void OnSavedToPath(string path)
    {
        FilePath   = path;
        _baseTitle = Path.GetFileNameWithoutExtension(path);
        Id         = path;

        ViewModel.DocumentDirectory = Path.GetDirectoryName(path);
        ViewModel.MarkSaved();

        _isDirty = ViewModel.IsDirty;
        Title    = _isDirty ? $"• {_baseTitle}" : _baseTitle;
    }
}
