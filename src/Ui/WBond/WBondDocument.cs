using System;
using System.IO;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Assembly;
using CircuitRF.WBond;
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;

namespace CircuitRF.Ui.WBond;

/// <summary>
/// Document-shell view model for one wBond editor tab. Wraps <see cref="WBondViewModel"/> rather than
/// merging with it, exactly as <c>HarmonicaDocumentViewModel</c> wraps <c>HarmonicaViewModel</c>.
/// </summary>
public sealed partial class WBondDocumentViewModel : ObservableObject
{
    [ObservableProperty] private bool _isDirty;

    /// <summary>
    /// The layout shown underneath the wires as reference geometry, or null when the editor was
    /// opened with no layout context (wbond.md §10, third entry point — the user drags cells in from
    /// the project tree instead).
    /// </summary>
    [ObservableProperty] private LayoutEditorViewModel? _referenceLayout;

    public WBondViewModel Editor { get; }

    public WBondPanelViewModel Panel { get; } = new();

    /// <summary>The wire layer drawn over — and given first refusal on input to — the layout canvas.</summary>
    public WBondLayoutOverlay Overlay { get; }

    public WBondDocumentViewModel(WBondViewModel? editor = null)
    {
        Editor = editor ?? new WBondViewModel();
        Editor.DirtyChanged += () => IsDirty = true;
        Editor.ReadoutChanged += () => Panel.Update(Editor.Readout);

        Overlay = new WBondLayoutOverlay(Editor);

        Panel.Update(Editor.Readout);
    }

    /// <summary>
    /// Gives the editor an empty reference layout when it has none, so cells can be dragged in from
    /// the project tree (§6.6) — the third entry point's whole workflow.
    ///
    /// <para>Without a layout view model the canvas has nothing to drop INTO and the existing
    /// palette-drag path silently does nothing, which reads as drag-and-drop being broken rather than
    /// as there being no layout yet.</para>
    /// </summary>
    /// <param name="scratchLayoutDir">
    /// Where a dropped cell's reference resolves from. Instance CellRefs are relative to the layout's
    /// own directory, so a blank layout with no path can hold a dropped cell but cannot resolve it.
    /// </param>
    public void EnsureReferenceLayout(string scratchLayoutDir)
    {
        if (ReferenceLayout is not null) return;

        Directory.CreateDirectory(scratchLayoutDir);
        ReferenceLayout = new LayoutEditorViewModel(new LayoutView())
        {
            CurrentLayoutPath = Path.Combine(scratchLayoutDir, "reference.clay"),
        };
    }

    /// <summary>
    /// The resolved assembly rule set, or null when nothing was referenced — which is a normal state,
    /// not an error (wbond.md §8 / <c>WasmResolver</c>).
    /// </summary>
    public WasmResolution? AssemblyRules { get; private set; }

    /// <summary>
    /// Installs a resolved rule set and pushes it to the reference layout, whose DRC run is where
    /// assembly rules are actually evaluated.
    ///
    /// <para><b>The wire check runs from the LAYOUT's own DRC, not from a second checker.</b> The
    /// panel, the markers, the waiver store and the run itself already exist there; a wire violation
    /// is one more row in that list. That is §8.1's "a new rule vocabulary over an existing DRC, not a
    /// second DRC", expressed as plumbing rather than as an intention.</para>
    /// </summary>
    public void ApplyAssemblyRules(WasmResolution? resolution)
    {
        AssemblyRules = resolution;
        if (ReferenceLayout is { } layout) layout.AssemblyRules = resolution;
    }

    partial void OnReferenceLayoutChanged(LayoutEditorViewModel? value)
    {
        Overlay.ReferenceLayout = value?.Model;
        Overlay.ReferenceTechnology = value?.Technology;
        Overlay.ReferenceBaseDir = value?.InstanceBaseDir;

        if (value is null) return;

        // The design object itself, not a copy: the editor mutates its wires in place, so a snapshot
        // would leave the DRC checking geometry the user has since moved.
        value.WireDesign    = Editor.Design;
        value.AssemblyRules = AssemblyRules;
    }

    /// <summary>Clears the dirty flag after a successful save.</summary>
    public void MarkSaved() => IsDirty = false;
}

/// <summary>
/// Dock Document for an open wBond editor.
///
/// <para>Mirrors <c>HarmonicaDocument</c>'s shape exactly — scratch vs. materialised keyed on
/// <see cref="FilePath"/>, dirty mirrored FROM the view-model (the VM is the source of truth; the
/// document reflects it, never the reverse), and a bullet in the tab title while dirty.</para>
/// </summary>
public sealed class WBondDocument : Document
{
    private string _baseTitle;
    private bool _isDirty;

    public WBondDocumentViewModel ViewModel { get; }

    // ── Zoom To Fit request — see SchematicDocument.ZoomToFitRequested for the pattern this mirrors.
    public event Action? ZoomToFitRequested;
    public void RequestZoomToFit() => ZoomToFitRequested?.Invoke();

    // ── Toolbar Cut/Copy/Paste — same pattern; the view runs the real wire/geometry clipboard ops.
    public event Action? CutRequested;
    public event Action? CopyRequested;
    public event Action? PasteRequested;
    public void RequestCut()   => CutRequested?.Invoke();
    public void RequestCopy()  => CopyRequested?.Invoke();
    public void RequestPaste() => PasteRequested?.Invoke();

    /// <summary>Absolute path of the <c>.wBond</c>, or null for a scratch document.</summary>
    public string? FilePath { get; private set; }

    public bool IsScratch => FilePath is null;

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (_isDirty == value) return;
            _isDirty = value;
            RefreshTitle();
        }
    }

    public WBondDocument(WBondViewModel? editor = null, string? filePath = null)
    {
        ViewModel = new WBondDocumentViewModel(editor);
        FilePath = filePath;
        _baseTitle = filePath is null ? "wBond" : Path.GetFileNameWithoutExtension(filePath);

        Id = "wbond-" + Guid.NewGuid().ToString("N");
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WBondDocumentViewModel.IsDirty))
                IsDirty = ViewModel.IsDirty;
        };

        RefreshTitle();
    }

    /// <summary>
    /// Opens a design from disk into a new document, unpacking any embedded geometry (§9.1).
    ///
    /// <para><paramref name="scratchDir"/> is where embedded cells are unpacked as ordinary cell
    /// folders. With none supplied the geometry stays in the file unread — the wires still open and
    /// edit, which is the point of WB35's "never fails, never substitutes".</para>
    /// </summary>
    public static WBondDocument Open(string path, string? scratchDir = null)
    {
        var design = WBondIo.ReadFile(path);
        var document = new WBondDocument(new WBondViewModel(design), path);

        if (scratchDir is not null &&
            WBondGeometryEmbedding.Unpack(design.EmbeddedGeometryJson, scratchDir) is { } unpacked)
        {
            // InstanceBaseDir is DERIVED from the layout's own path, so pointing the embedded view at
            // the unpacked folder is done by giving it a path there — not by overriding the base dir,
            // which would let the two disagree.
            var layout = new LayoutEditorViewModel(unpacked.Root)
            {
                CurrentLayoutPath = Path.Combine(unpacked.BaseDir, "embedded.clay"),
            };

            document.ViewModel.ReferenceLayout = layout;
            document.HasEmbeddedGeometry = true;
        }

        return document;
    }

    /// <summary>True when this document is showing geometry that travelled inside the file.</summary>
    public bool HasEmbeddedGeometry { get; private set; }

    /// <summary>
    /// Resolves this document's assembly rules — its own <c>AssemblyRef</c> first, then the
    /// workspace default, then none (§M1's resolution order, §5 open question 1 answered "both").
    ///
    /// <para>Every outcome is non-fatal: a missing or malformed rule file leaves the document open and
    /// editable with its diagnostics reported. "None" is reported ONCE, as an absence of rules rather
    /// than as a failure.</para>
    /// </summary>
    public WasmResolution ResolveAssemblyRules(
        string? workspaceRootDir, string? workspaceDefaultRef, WasmCache cache)
    {
        var resolution = WasmResolver.Resolve(
            ViewModel.Editor.Design.AssemblyRef,
            FilePath is null ? null : Path.GetDirectoryName(FilePath),
            workspaceRootDir,
            workspaceDefaultRef,
            cache);

        ViewModel.ApplyAssemblyRules(resolution);
        return resolution;
    }

    /// <summary>
    /// Writes the design and clears the dirty state.
    ///
    /// <para>With <paramref name="embedGeometry"/> the reference layout travels inside the file, so
    /// it can be handed to someone with no access to the originating workspace — the owner's stated
    /// goal for §9.1. The caller is expected to have shown <see cref="WBondGeometryEmbedding.Analyze"/>'s
    /// plan first (WB33): what a save costs must be stated before it happens, not discovered by the
    /// recipient.</para>
    /// </summary>
    public void Save(string? path = null, bool embedGeometry = false)
    {
        string target = path ?? FilePath
            ?? throw new InvalidOperationException("A scratch wBond document needs a path to save to.");

        var design = ViewModel.Editor.Design;

        if (embedGeometry &&
            ViewModel.ReferenceLayout is { } layout &&
            layout.InstanceBaseDir is { } baseDir)
        {
            design.EmbeddedGeometryJson = WBondGeometryEmbedding.Embed(layout.Model, baseDir);
        }

        WBondIo.WriteFile(target, design);

        FilePath = target;
        _baseTitle = Path.GetFileNameWithoutExtension(target);
        ViewModel.MarkSaved();
        IsDirty = false;
    }

    private void RefreshTitle() => Title = _isDirty ? _baseTitle + " •" : _baseTitle;
}
