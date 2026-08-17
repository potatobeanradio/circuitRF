using System;
using System.Globalization;
using System.IO;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Assembly;
using CircuitRF.WBond;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    /// <summary>
    /// Which canvases are showing (owner, 2026-08-16). Cycled by the toolbar button and by <c>V</c>,
    /// and persisted in the <c>.wBond</c>'s view state.
    ///
    /// <para>The Array Inductance panel is deliberately NOT part of this: it is never hidden by a view
    /// mode, because the whole reason to enlarge a canvas is to look at geometry while watching the
    /// inductance change. <see cref="PanelVisible"/> is its own switch, on its own key.</para>
    /// </summary>
    [ObservableProperty] private WBondViewMode _viewMode = WBondViewMode.Both;

    /// <summary>Whether the Array Inductance panel is showing — the <c>I</c> key. Persisted.</summary>
    [ObservableProperty] private bool _panelVisible = true;

    /// <summary>
    /// Whether both canvases carry rulers along their top and left edges. One toolbar button drives
    /// both, and it persists with the document.
    /// </summary>
    [ObservableProperty] private bool _rulersVisible = true;

    /// <summary>
    /// Which tool a click means (owner, 2026-08-16) — the Layout Editor's own <c>ActiveTool</c>
    /// shape, so the two editors' toolbars behave identically rather than approximately.
    ///
    /// <para><b>The overlay's two armed flags are DERIVED from this, never set beside it.</b> They
    /// were previously the source of truth, one per <c>ToggleButton</c>, each responsible for
    /// un-pressing the other — which left "neither armed" as a state the toolbar could not show and
    /// Escape could not be seen to have reached.</para>
    /// </summary>
    [ObservableProperty] private WBondTool _activeTool = WBondTool.Select;

    partial void OnActiveToolChanged(WBondTool value)
    {
        Overlay.WireDrawArmed   = value == WBondTool.DrawWire;
        Overlay.WireRotateArmed = value == WBondTool.Rotate;
    }

    /// <summary>
    /// Whether anything is selected — the gate on every toolbar command that acts on a selection
    /// (owner, 2026-08-16: Straighten and Transform were live with nothing selected and silently did
    /// nothing).
    ///
    /// <para>Mirrored from the editor rather than read through a binding path, because a
    /// <c>WireSelection</c> is replaced wholesale on every change and a binding to
    /// <c>Editor.Selection.IsEmpty</c> would never be re-evaluated: nothing raises a notification for
    /// a property OF the selection object, only for the selection itself.</para>
    /// </summary>
    public bool HasSelection => !Editor.Selection.IsEmpty;

    /// <summary>
    /// The toolbar's three tool buttons, bound by NAME exactly as the Layout Editor's are — same
    /// command shape, same <c>EnumEqualsBool</c> highlight, so one toolbar teaches the other.
    /// </summary>
    public IRelayCommand<string> SetActiveToolCommand { get; }


    public bool ProfileVisible => ViewMode is WBondViewMode.Both or WBondViewMode.Profile;

    public bool LayoutVisible => ViewMode is WBondViewMode.Both or WBondViewMode.Layout;

    public bool ProfileOnly => ViewMode == WBondViewMode.Profile;

    public bool LayoutOnly => ViewMode == WBondViewMode.Layout;

    /// <summary>True only in Both, where the two canvases share the area and need a splitter.</summary>
    public bool SplitterVisible => ViewMode == WBondViewMode.Both;

    /// <summary>How many wires the design holds — the metadata bar's own count.</summary>
    public string WireCountText => Editor.Design.WireCount.ToString(CultureInfo.InvariantCulture);

    public string ViewModeTooltip => ViewMode switch
    {
        WBondViewMode.Profile => "Showing: profile only  (V cycles)",
        WBondViewMode.Layout => "Showing: layout only  (V cycles)",
        _ => "Showing: profile and layout  (V cycles)",
    };

    partial void OnViewModeChanged(WBondViewMode value)
    {
        OnPropertyChanged(nameof(ProfileVisible));
        OnPropertyChanged(nameof(LayoutVisible));
        OnPropertyChanged(nameof(ProfileOnly));
        OnPropertyChanged(nameof(LayoutOnly));
        OnPropertyChanged(nameof(SplitterVisible));
        OnPropertyChanged(nameof(ViewModeTooltip));
    }

    /// <summary>Both → Profile → Layout → Both. The <c>V</c> key and the toolbar button share it.</summary>
    public void CycleViewMode() => ViewMode = ViewMode switch
    {
        WBondViewMode.Both => WBondViewMode.Profile,
        WBondViewMode.Profile => WBondViewMode.Layout,
        _ => WBondViewMode.Both,
    };

    /// <summary>Captures the current arrangement into the design, ready to be written to disk.</summary>
    public void CaptureViewState() => new WBondViewState
    {
        ViewMode = ViewMode,
        PanelVisible = PanelVisible,
        RulersVisible = RulersVisible,
        ProfileAzimuthRadians = Editor.ProfileAzimuthRadians,
        DisplayUnit = Editor.DisplayUnit,
    }.To(Editor.Design);

    /// <summary>Applies the arrangement a design was saved with. Absent state leaves the defaults.</summary>
    public void ApplyViewState()
    {
        var state = WBondViewState.From(Editor.Design);

        ViewMode = state.ViewMode;
        PanelVisible = state.PanelVisible;
        RulersVisible = state.RulersVisible;
        Editor.ProfileAzimuthRadians = state.ProfileAzimuthRadians;
        Editor.DisplayUnit = state.DisplayUnit;
    }

    public WBondViewModel Editor { get; }

    public WBondPanelViewModel Panel { get; } = new();

    /// <summary>The wire layer drawn over — and given first refusal on input to — the layout canvas.</summary>
    public WBondLayoutOverlay Overlay { get; }

    public WBondDocumentViewModel(WBondViewModel? editor = null)
    {
        Editor = editor ?? new WBondViewModel();
        Editor.DirtyChanged += () => IsDirty = true;
        Editor.ReadoutChanged += () =>
        {
            Panel.Update(Editor.Readout);
            OnPropertyChanged(nameof(WireCountText));
        };

        // §6.5 — the panel's LENGTH rows follow the editor's display unit. Pushed rather than pulled
        // so the panel stays a plain formatter with no reference back to the editor.
        Editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(WBondViewModel.Selection))
            {
                OnPropertyChanged(nameof(HasSelection));
                return;
            }

            if (e.PropertyName != nameof(WBondViewModel.DisplayUnit)) return;

            Panel.Unit = Editor.DisplayUnit;
            PushDisplayUnitToReferenceLayout();
        };

        Overlay = new WBondLayoutOverlay(Editor);

        SetActiveToolCommand = new RelayCommand<string>(name =>
        {
            if (name is not null && Enum.TryParse<WBondTool>(name, out var tool)) ActiveTool = tool;
        });

        Panel.Unit = Editor.DisplayUnit;
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

        // A LayoutView's SnapDbu defaults to ZERO, and zero means "no grid" to LayoutRenderer as well
        // as "no snapping" to the editor — so a wBond opened on a scratch layout drew no grid at all,
        // which is what the owner saw. One mil is the pitch a bonder works to and the unit this editor
        // defaults to; the Snap box in the metadata bar changes it from here on.
        if (value.SnapDbu <= 0)
        {
            // The LADDER describes one mil — the step a bonder works to, and the unit this editor
            // defaults to — so its rungs come out as 0.1 / 0.5 / 1 / 5 / 10 / 25 / 50 mil. The
            // document's own snap starts at the FINEST of those (owner, 2026-08-16). The two are
            // stated separately on purpose: deriving the ladder from the snap would re-base it on
            // 0.1 mil and offer a 0.01 mil rung nobody asked for.
            value.SnapLadderBaseDbu = LayoutUnits.ToDbu(1m, LayoutUnit.Mil, value.Model.DbuPerMicron);
            value.SnapDbu = LayoutUnits.ToDbu(0.1m, LayoutUnit.Mil, value.Model.DbuPerMicron);

            // The layout view-model's constructor already built the ladder — against the zero snap we
            // have just replaced. Left alone it would offer the µm-scale fallback rungs all session.
            value.RefreshSnapLadder();
        }

        PushDisplayUnitToReferenceLayout();

        // The design object itself, not a copy: the editor mutates its wires in place, so a snapshot
        // would leave the DRC checking geometry the user has since moved.
        value.WireDesign    = Editor.Design;
        value.AssemblyRules = AssemblyRules;
    }

    /// <summary>
    /// Puts the reference layout on the SAME display unit the wBond editor is showing.
    ///
    /// <para><b>The Snap box is the reference layout's own</b> — its ladder and its committing text
    /// field, reused rather than reimplemented — and both are formatted in the layout's
    /// <c>DisplayUnit</c>, which defaults to microns. So a document set to <c>mil</c> offered a snap
    /// ladder in µm right beside a Unit box saying mil. Mirroring the unit is what makes the metadata
    /// bar internally consistent, and it carries the layout's cursor readout, extent and Zoom 1:1 with
    /// it, which is the same answer for the same reason.</para>
    ///
    /// <para>§6.5's "independent of the <c>.ctech</c> display unit" is untouched by this: that rule is
    /// about the wBond NOT being forced to follow the technology's unit. The arrow still points the
    /// other way — the editor's chosen unit drives the reference layout, never the reverse. The
    /// reference layout here is always a scratch or unpacked one, so the preference dirty-flag this
    /// sets belongs to nothing anyone saves.</para>
    /// </summary>
    private void PushDisplayUnitToReferenceLayout()
    {
        if (ReferenceLayout is not { } layout) return;

        var unit = ToLayoutUnit(Editor.DisplayUnit);
        if (layout.DisplayUnit != unit) layout.DisplayUnit = unit;
    }

    /// <summary>
    /// The layout editor's spelling of a wBond unit.
    ///
    /// <para>Written out rather than cast: the two enums list the same five units in the same order
    /// today, and an ordinal cast would keep compiling and start lying the moment either gains a
    /// member. They are separate types because <c>CircuitRF.WBond</c> may not reference
    /// <c>CircuitRF.Ui</c> — see <c>WBondUnits</c>'s own note on why that table is duplicated.</para>
    /// </summary>
    internal static LayoutUnit ToLayoutUnit(WBondUnit unit) => unit switch
    {
        WBondUnit.Nm => LayoutUnit.Nm,
        WBondUnit.Um => LayoutUnit.Um,
        WBondUnit.Mm => LayoutUnit.Mm,
        WBondUnit.Mil => LayoutUnit.Mil,
        WBondUnit.Inch => LayoutUnit.Inch,
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown wBond unit."),
    };

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

    /// <summary>
    /// Raised by the toolbar's Save / Save As buttons (owner, 2026-08-16). <b>The host answers it</b>,
    /// because where a <c>.wBond</c> goes is a question only the host can answer: the workspace routes
    /// it through <c>SaveWBondDoc</c>, which owns the picker, the open-document map and the message
    /// log; the standalone binary routes it through its own window's <c>SaveAsync</c>. Neither gains a
    /// second way to write a file. The argument is true for Save As.
    /// </summary>
    public event Action<bool>? SaveRequested;

    /// <summary>Asks the host to save this document; <paramref name="saveAs"/> forces the picker.</summary>
    public void RequestSave(bool saveAs) => SaveRequested?.Invoke(saveAs);

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

    /// <summary>
    /// The tab name a scratch wBond opens under when the host supplies none.
    ///
    /// <para>Hosts that can see the other open documents pass a numbered title
    /// (<c>Untitled-wBond-1</c>, <c>-2</c>, …), matching every other scratch document type; this is
    /// the fallback for the standalone binary, which shows one document per WINDOW and therefore has
    /// no tab strip to disambiguate.</para>
    /// </summary>
    public const string DefaultScratchTitle = "Untitled-wBond-1";

    public WBondDocument(WBondViewModel? editor = null, string? filePath = null, string? title = null)
    {
        ViewModel = new WBondDocumentViewModel(editor);
        FilePath = filePath;
        _baseTitle = filePath is not null
            ? Path.GetFileNameWithoutExtension(filePath)
            : title ?? DefaultScratchTitle;

        // The scratch title IS the identity, exactly as HarmonicaDocument's is — the close-confirm
        // dialog and the "next free Untitled-N" search both read Id, and a Guid there would name the
        // document to the user as "wbond-3f8c…".
        Id = filePath is null ? _baseTitle : "wbond-" + Guid.NewGuid().ToString("N");
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
        document.ViewModel.ApplyViewState();

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

        // How the editor was arranged travels with the file (§9's ViewState field), so reopening a
        // document does not put the user back through the same three toolbar clicks.
        ViewModel.CaptureViewState();

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
