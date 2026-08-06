// VM for the .cem EM setup editor (brief-L6-L7-em-ui.md U3).
//
// D3 — every EM setting is controlled here. Nothing that affects the answer lives in a transient
// dialog, a canvas mode, or a hardcoded panel default.
//
// R-em-13 — CanSolve is called on every settings change and its reason is shown LIVE, not on the
// Simulate click. The kernel already words every problem-level refusal; surfacing it as you type is
// free, and it is what makes the panel feel bounded rather than broken.
//
// R-em-21 — no physics in src/Ui. Every number this panel shows comes from the extractor's readback,
// from EmMeshReport, or from the returned DataSet. If a quantity is wanted that the engine does not
// return, add it to the engine and its "tline" group — do not compute it here.

using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Commands;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Layout.Em;

/// <summary>What the workspace hands back when a <c>.cem</c>'s <see cref="EmSetup.LayoutRef"/> is
/// resolved. R-em-10: the geometry is read HERE, at use time, never embedded in the <c>.cem</c> —
/// which is the whole reason re-running after a layout edit picks the edit up.</summary>
public sealed record EmLayoutSource(
    string      AbsolutePath,
    LayoutView  View,
    Technology? Technology,
    int         DbuPerMicron);

/// <summary>One read-only stackup row for R-em-12's "the stackup is SHOWN, not edited" panel.</summary>
public sealed record EmStackupRow(
    string Kind,
    string Name,
    string Thickness,
    string Electrical,
    string DrawingLayers,
    bool   IsSignal,
    bool   IsGround);

public sealed partial class EmSetupEditorViewModel : ObservableObject
{
    /// <summary>Absolute path of the <c>.cem</c>. Never null — R-em-9: a <c>.cem</c> is
    /// workspace-scoped and never scratch, mirroring <c>TechDocument</c>.</summary>
    public string FilePath { get; }

    public EmSetup Working { get; private set; }

    public UndoRedoStack UndoRedo { get; } = new();
    public IRelayCommand UndoCommand { get; }
    public IRelayCommand RedoCommand { get; }
    public IRelayCommand SaveCommand { get; }

    [ObservableProperty] private bool _isDirty;

    /// <summary>Wired by the workspace. Returns null when the reference does not resolve — which
    /// degrades to a stated message, never a throw (Tier D).</summary>
    public Func<string, EmLayoutSource?>? ResolveLayout { get; set; }

    public event Action<string>? SaveError;
    public event Action<string>? EmSetupSaved;

    /// <summary>
    /// D2 — <b>Simulate</b> runs the EM simulation; <b>Mesh</b> computes the mesh only. The Mesh
    /// button never solves. This seam is what the workspace wires the run path onto (R-em-18: the
    /// same five steps as <c>RunSchematicDocAsync</c> with a different middle) — the VM itself owns
    /// no dispatcher, no results writer and no Data Display.
    /// </summary>
    public Func<EmSetupEditorViewModel, Task>? RunRequested { get; set; }

    /// <summary>Raised when the extraction/mesh state changed — the layout canvas's cue to
    /// re-render (or drop) the mesh overlay (R-em-17).</summary>
    public event Action? AnalysisRefreshed;

    // ── Resolved state, recomputed on every settings change ────────────────────────────────────

    [ObservableProperty] private EmCrossSectionReadback? _readback;
    [ObservableProperty] private string? _extractionRefusal;
    [ObservableProperty] private string? _kernelRefusal;
    [ObservableProperty] private ObservableCollection<string> _notes = [];
    [ObservableProperty] private ObservableCollection<EmStackupRow> _stackupRows = [];
    [ObservableProperty] private string _layoutStatus = "";
    [ObservableProperty] private string _technologyName = "";

    /// <summary>The mesh, when the Mesh button has been pressed and nothing has invalidated it
    /// since. R-em-17: an edited layout CLEARS this; it never keeps showing a stale mesh.</summary>
    [ObservableProperty] private EmMeshReport? _meshReport;

    [ObservableProperty] private ObservableCollection<string> _meshNotes = [];

    // ── L8b: the PLANAR mesh, alongside the cross-section one rather than instead of it ────────
    //
    // D7 — the .cem says which analysis it is and there is no automatic kernel selection here; the
    // registry that would make one is L8e's. So this VM carries BOTH meshes, the button that builds
    // each is separate, and which overlay the canvas shows follows from which report is non-null.

    /// <summary>The surface mesh, when Mesh has been pressed on a planar setup. R-em-17 applies:
    /// an edited layout clears it.</summary>
    [ObservableProperty] private PlanarMeshReport? _planarMeshReport;

    [ObservableProperty] private ObservableCollection<string> _planarMeshNotes = [];

    /// <summary>The planar extractor's own refusal, when it has one. Separate from
    /// <see cref="ExtractionRefusal"/> because the two extractors refuse for different reasons and
    /// merging them would put one's wording on the other's path.</summary>
    [ObservableProperty] private string? _planarExtractionRefusal;

    /// <summary>R-msh-7 — the R17 verdict, surfaced so the panel can disable Simulate with the
    /// engine's own words rather than re-deriving the budget rule.</summary>
    public string? PlanarBudgetRefusal => PlanarMeshReport?.Refusal;

    /// <summary>R-msh-8's numbers, in the engine's own units, formatted once. The panel prints this;
    /// it computes nothing.</summary>
    public string PlanarMeshSummary => PlanarMeshReport is not { } r
        ? ""
        : $"{r.UnknownCount:N0} unknowns · {r.CellCount:N0} cells · " +
          $"max cell {r.MaxCellEdgeM * 1e6:G4} µm (λ_g/{(r.MaxCellSizeM > 0 ? r.GuidedWavelengthM / r.MaxCellSizeM : 0):G3} " +
          $"at {r.FrequencyHz / 1e9:G4} GHz) · " +
          $"{r.CellsAcrossNarrowestConductor} across the narrowest conductor ({r.NarrowestConductorWidthM * 1e6:G4} µm) · " +
          r.Verdict;

    /// <summary>The planar problem, when the planar extractor last succeeded.</summary>
    public PlanarProblem? PlanarProblem { get; private set; }

    // ── L8e: the registry's choice, surfaced live (R-res-1, R-em-13) ───────────────────────────

    /// <summary>Which kernel the registry chose for the CURRENT geometry and setting — never
    /// <see cref="EmAnalysisKind.Auto"/>, which is a request rather than an outcome.</summary>
    [ObservableProperty] private EmAnalysisKind _selectedKernel = EmAnalysisKind.CrossSection;

    [ObservableProperty] private string _selectedKernelName = "";

    /// <summary>R-res-1 — the reason, in the registry's own words, shown as you type rather than
    /// only in the run's notes. A user who gets the slow kernel must be able to see why in one
    /// line.</summary>
    [ObservableProperty] private string _kernelChoiceReason = "";

    /// <summary>D3/R-res-5 — the port extractor's refusal, live. Separate from the two extractor
    /// refusals because a perfectly extractable layout can still have an ambiguous port label, and
    /// merging them would put one's wording on the other's path.</summary>
    [ObservableProperty] private string? _portRefusal;

    /// <summary>L9d/D5 — one row per signal conductor entry; checked rows are the analysis levels.
    /// None checked means "infer", which is every level that carries artwork.</summary>
    [ObservableProperty] private ObservableCollection<EmAnalysisLevelRow> _analysisLevelRows = [];

    /// <summary>The ports the layout's own <c>IsPort</c> labels resolved to, for the panel's port
    /// list and for the R18 readback. Empty for a cross-section setup, whose two ports ARE the two
    /// ends of the extracted line by construction.</summary>
    public IReadOnlyList<PlanarPort> PlanarPorts { get; private set; } = [];

    /// <summary>The user-facing choices for the analysis-kind selector, in the order they are
    /// offered: Auto first, because D2's rule is conservative and correct for the overwhelming
    /// majority of setups.</summary>
    public static IReadOnlyList<EmAnalysisKind> AnalysisKindChoices { get; } =
        [EmAnalysisKind.Auto, EmAnalysisKind.CrossSection, EmAnalysisKind.Planar];

    [ObservableProperty] private EmAnalysisKind _analysisKind = EmAnalysisKind.CrossSection;

    public bool IsPlanarAnalysis => SelectedKernel == EmAnalysisKind.Planar;

    /// <summary>Latest successfully-extracted problem — the one thing Mesh and Simulate both
    /// consume, so they can never disagree about what is being solved.</summary>
    public EmProblem? Problem { get; private set; }

    public string? BlockingReason =>
        SelectedKernel == EmAnalysisKind.Planar
            ? PlanarExtractionRefusal ?? KernelRefusal ?? PortRefusal ?? PlanarBudgetRefusal
            : ExtractionRefusal ?? KernelRefusal;

    public bool CanRun =>
        SelectedKernel == EmAnalysisKind.Planar
            ? PlanarProblem is not null && BlockingReason is null
            : Problem is not null && BlockingReason is null;

    // ── Frequency (R-em-11: reuse FrequencySpecViewModel, never a second frequency editor) ─────

    public ViewModels.FrequencySpecViewModel Frequency { get; private set; }

    // ── Port Z0, as staged text so a complex value can be typed ────────────────────────────────

    [ObservableProperty] private string _port1Z0Text = "50";
    [ObservableProperty] private string _port2Z0Text = "50";
    [ObservableProperty] private string? _port1Z0Error;
    [ObservableProperty] private string? _port2Z0Error;

    /// <summary>
    /// R-cpl-6 — the per-port list, one row per extracted port in D3 order. Rebuilt from the
    /// extraction on every <see cref="Refresh"/>, because the port COUNT is a property of the
    /// geometry (2N for N conductors), not something the user types.
    ///
    /// <para><b>Shown only when there are more than two ports.</b> A single line's two ports are
    /// fully described by the near/far pair above, and putting a two-row list beside those same two
    /// fields would be two controls for one value. The list is what a coupled pair needs.</para>
    /// </summary>
    public System.Collections.ObjectModel.ObservableCollection<EmPortZ0Row> PortRows { get; } = [];

    /// <summary>True once the cross-section resolves to more than a single line's two ports.</summary>
    public bool ShowPortList => PortRows.Count > 2 || SelectedKernel == EmAnalysisKind.Planar;

    // ── Mesh settings, staged as text (all six — R-em-11) ──────────────────────────────────────

    [ObservableProperty] private string _minCellsAcrossWidthText = "";
    [ObservableProperty] private string _edgeCellsText           = "";
    [ObservableProperty] private string _edgeFractionText        = "";
    [ObservableProperty] private string _edgeGrowthText          = "";
    [ObservableProperty] private string _truncationHeightsText   = "";
    [ObservableProperty] private string _truncationTailCellsText = "";

    // ── L8b's D3 planar mesh controls — THREE and no more, staged the same way ────────────────

    [ObservableProperty] private string _planarCellsPerWavelengthText = "";
    [ObservableProperty] private string _planarEdgeCellsText          = "";
    [ObservableProperty] private bool   _planarEdgeMesh;

    // ── Signal layer + dispersion ──────────────────────────────────────────────────────────────

    [ObservableProperty] private ObservableCollection<string> _conductorLayerChoices = [];
    [ObservableProperty] private string _signalLayerChoice = InferSignalLayer;
    [ObservableProperty] private bool   _dispersionCorrection;
    [ObservableProperty] private bool   _adaptiveSampling = true;
    [ObservableProperty] private bool   _directVerticalKernel;

    /// <summary>Non-null when the dispersion opt-in must be disabled, with the reason. The panel
    /// ASKS <see cref="QuasiStaticKernel.TryMicrostripDispersion"/> rather than re-deriving the
    /// condition (R-em-11).</summary>
    [ObservableProperty] private string? _dispersionDisabledReason;

    public const string InferSignalLayer = "(infer from the drawn geometry)";

    private bool _suppressCommit;

    public EmSetupEditorViewModel(string filePath, EmSetup setup)
    {
        FilePath = filePath;
        Working  = setup;

        UndoCommand = new RelayCommand(() => UndoRedo.Undo(), () => UndoRedo.CanUndo);
        RedoCommand = new RelayCommand(() => UndoRedo.Redo(), () => UndoRedo.CanRedo);
        SaveCommand = new RelayCommand(Save);

        UndoRedo.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(UndoRedoStack.CanUndo))    UndoCommand.NotifyCanExecuteChanged();
            if (e.PropertyName is nameof(UndoRedoStack.CanRedo))    RedoCommand.NotifyCanExecuteChanged();
            if (e.PropertyName is nameof(UndoRedoStack.IsModified)) IsDirty = UndoRedo.IsModified;
        };

        Frequency = NewFrequencyVm();
        RebuildAll();
    }

    /// <summary>
    /// <c>FrequencySpecViewModel</c> takes a <c>SchematicEditModel</c> only so its inline "≈"
    /// previews can resolve variable references. A <c>.cem</c> has no schematic and no variables —
    /// an EM sweep is literal numbers — so an empty model is the honest argument, and it is what
    /// lets R-em-11's "reuse it, do not write a second frequency editor" actually hold.
    /// </summary>
    private ViewModels.FrequencySpecViewModel NewFrequencyVm()
    {
        var vm = new ViewModels.FrequencySpecViewModel(new Schematic.SchematicEditModel(), Working.Frequency)
        {
            CanRemoveSelf = false,
        };
        vm.PropertyChanged += (_, _) => CommitFrequency();
        return vm;
    }

    // ── Save ───────────────────────────────────────────────────────────────────────────────────

    private void Save()
    {
        try
        {
            EmSetupPersistence.SaveToFile(FilePath, Working);
        }
        catch (Exception ex)
        {
            SaveError?.Invoke($"Couldn't save EM setup to '{FilePath}': {ex.Message}");
            return;
        }
        UndoRedo.MarkSaved();
        EmSetupSaved?.Invoke(FilePath);
    }

    // ── Snapshot undo plumbing ─────────────────────────────────────────────────────────────────

    internal string SnapshotJson() => EmSetupPersistence.Serialize(Working);

    internal void CommitEdit(string beforeJson, string description)
    {
        var afterJson = SnapshotJson();
        if (afterJson == beforeJson) return;
        UndoRedo.Execute(new EmSetupSnapshotCommand(this, beforeJson, afterJson, description));
    }

    internal void ApplySnapshot(string json)
    {
        Working = EmSetupPersistence.Deserialize(json);
        Frequency = NewFrequencyVm();
        OnPropertyChanged(nameof(Frequency));
        RebuildAll();
    }

    // ── Field commits ──────────────────────────────────────────────────────────────────────────

    private void CommitFrequency()
    {
        if (_suppressCommit) return;
        var spec = Frequency.Build();
        var before = SnapshotJson();
        Working.Frequency = spec;
        CommitEdit(before, "Change EM frequency sweep");
        Refresh();
    }

    public void CommitPortZ0(int port)
    {
        if (_suppressCommit) return;
        string text = port == 1 ? Port1Z0Text : Port2Z0Text;
        if (!TryParseComplexOhms(text, out var z))
        {
            const string msg = "Enter a resistance in ohms, or a complex value like 50+10j.";
            if (port == 1) Port1Z0Error = msg; else Port2Z0Error = msg;
            return;
        }
        if (port == 1) Port1Z0Error = null; else Port2Z0Error = null;

        var before = SnapshotJson();
        if (port == 1) Working.Port1Z0 = z; else Working.Port2Z0 = z;
        CommitEdit(before, $"Change port {port} reference impedance");
        Refresh();
    }

    /// <summary>
    /// Commits one row of the per-port list (R-cpl-6). Writes through
    /// <see cref="EmSetup.PortZ0s"/>, padding it from the near/far defaults so a list that is
    /// shorter than the port count stays meaningful — an override on port 4 must not silently
    /// change ports 1–3.
    /// </summary>
    public void CommitPortRow(int index)
    {
        if (_suppressCommit) return;
        if (index < 0 || index >= PortRows.Count) return;

        var row = PortRows[index];
        if (!TryParseComplexOhms(row.Text, out var z))
        {
            row.Error = "Enter a resistance in ohms, or a complex value like 50+10j.";
            return;
        }
        row.Error = null;

        var before = SnapshotJson();
        var list = Working.PortZ0s;
        while (list.Count <= index) list.Add(Working.ResolvePortZ0(list.Count));
        if (list[index] == z) return;                       // no-change guard: no undo entry
        list[index] = z;

        CommitEdit(before, $"Change port {row.PortNumber} reference impedance");
        Refresh();
    }

    /// <summary>Rebuilds <see cref="PortRows"/> from the extracted problem — the port count comes
    /// from the geometry, never from the user.</summary>
    private void RebuildPortRows(EmProblem? problem)
    {
        PortRows.Clear();
        if (problem is not null)
        {
            var ordered = new List<EmPort>(problem.Ports);
            ordered.Sort((a, b) => a.Number.CompareTo(b.Number));
            for (int i = 0; i < ordered.Count; i++)
            {
                var p = ordered[i];
                PortRows.Add(new EmPortZ0Row
                {
                    PortNumber = p.Number,
                    // D3 in the user's own terms, so the numbering is legible without the brief.
                    Label      = $"Port {p.Number} — '{p.Conductor}', {(i % 2 == 0 ? "near" : "far")} end",
                    Text       = FormatComplexOhms(Working.ResolvePortZ0(i)),
                });
            }
        }
        OnPropertyChanged(nameof(ShowPortList));
    }

    public void CommitMeshField(string field)
    {
        if (_suppressCommit) return;
        var m = Working.Mesh;
        var before = SnapshotJson();
        EmMeshSettings updated = field switch
        {
            nameof(EmMeshSettings.MinCellsAcrossWidth) when TryInt(MinCellsAcrossWidthText, 1, out int v)
                => m with { MinCellsAcrossWidth = v },
            nameof(EmMeshSettings.EdgeCells) when TryInt(EdgeCellsText, 0, out int v)
                => m with { EdgeCells = v },
            nameof(EmMeshSettings.EdgeFractionOfWidth) when TryDouble(EdgeFractionText, out double v) && v > 0
                => m with { EdgeFractionOfWidth = v },
            nameof(EmMeshSettings.EdgeGrowthRatio) when TryDouble(EdgeGrowthText, out double v) && v > 1
                => m with { EdgeGrowthRatio = v },
            nameof(EmMeshSettings.TruncationHeights) when TryDouble(TruncationHeightsText, out double v) && v > 0
                => m with { TruncationHeights = v },
            nameof(EmMeshSettings.TruncationTailCells) when TryInt(TruncationTailCellsText, 1, out int v)
                => m with { TruncationTailCells = v },
            _ => m,
        };

        // D3's three planar controls share this one committer rather than growing a second: they are
        // the same kind of staged-text edit and the same undo entry, and two committers would be two
        // places to forget InvalidateMesh().
        var pm = Working.PlanarMesh;
        PlanarMeshSettings updatedPlanar = field switch
        {
            "CellsPerWavelength" when TryInt(PlanarCellsPerWavelengthText, 1, out int v)
                => pm with { Auto = false, CellsPerWavelength = v },
            "PlanarEdgeCells" when TryInt(PlanarEdgeCellsText, 0, out int v)
                => pm with { Auto = false, EdgeCells = v },
            _ => pm,
        };

        if (updated == m && updatedPlanar == pm) { RefreshMeshText(); return; }

        Working.Mesh       = updated;
        Working.PlanarMesh = updatedPlanar;
        CommitEdit(before, $"Change mesh setting {field}");
        InvalidateMesh();
        Refresh();
    }

    partial void OnPlanarEdgeMeshChanged(bool value)
    {
        if (_suppressCommit) return;
        if (value == Working.PlanarMesh.EdgeMesh) return;
        var before = SnapshotJson();
        Working.PlanarMesh = Working.PlanarMesh with { Auto = false, EdgeMesh = value };
        CommitEdit(before, "Change planar edge mesh");
        InvalidateMesh();
        Refresh();
    }

    partial void OnSignalLayerChoiceChanged(string value)
    {
        if (_suppressCommit) return;
        string wanted = value == InferSignalLayer ? "" : value;
        if (wanted == Working.SignalStackupLayerName) return;
        var before = SnapshotJson();
        Working.SignalStackupLayerName = wanted;
        CommitEdit(before, "Change EM signal conductor layer");
        InvalidateMesh();
        Refresh();
    }

    partial void OnAnalysisKindChanged(EmAnalysisKind value)
    {
        if (_suppressCommit) return;
        if (value == Working.AnalysisKind) return;
        var before = SnapshotJson();
        Working.AnalysisKind = value;
        CommitEdit(before, "Change EM analysis kind");
        InvalidateMesh();
        Refresh();
    }

    partial void OnDispersionCorrectionChanged(bool value)
    {
        if (_suppressCommit) return;
        if (value == Working.DispersionCorrection) return;
        var before = SnapshotJson();
        Working.DispersionCorrection = value;
        CommitEdit(before, "Change dispersion correction");
    }

    partial void OnAdaptiveSamplingChanged(bool value)
    {
        if (_suppressCommit) return;
        if (value == Working.AdaptiveSampling) return;
        var before = SnapshotJson();
        Working.AdaptiveSampling = value;
        CommitEdit(before, "Change adaptive frequency sampling");
    }

    partial void OnDirectVerticalKernelChanged(bool value)
    {
        if (_suppressCommit) return;
        if (value == Working.DirectVerticalKernel) return;
        var before = SnapshotJson();
        Working.DirectVerticalKernel = value;
        CommitEdit(before, "Change vertical-kernel integration");
    }

    /// <summary>
    /// R13a — why adaptive sampling is unavailable, or null when it is. It only ever applies to the
    /// planar kernel: a cross-section solve is a closed form per frequency, so modelling one to save
    /// it would save nothing.
    /// </summary>
    public string? AdaptiveSamplingDisabledReason =>
        Working.AnalysisKind == EmAnalysisKind.CrossSection
            ? "Adaptive sampling applies to the planar (full-wave) analysis; a cross-section solve " +
              "is closed-form per frequency and every point is already cheap."
            : null;

    /// <summary>
    /// R13a — why the direct ẑẑ kernel is unavailable, or null when it is. It only ever affects
    /// G_A^zz, which is evaluated in exactly one place (pairs of VERTICAL bases), so on a layout
    /// with no vias it would change nothing at all and is disabled rather than silently inert.
    /// </summary>
    public string? DirectVerticalKernelDisabledReason =>
        Working.AnalysisKind == EmAnalysisKind.CrossSection
            ? "The direct vertical kernel is part of the planar (full-wave) analysis; this setup uses " +
              "the cross-section kernel."
            : PlanarProblem is { } pp && pp.ViaList.Count == 0
                ? "This layout has no vias, so there is no vertical current and G_A^zz is never " +
                  "evaluated — this setting would change nothing."
                : null;

    // ── Refresh: extract, ask CanSolve, project the readback ───────────────────────────────────

    private void RebuildAll()
    {
        _suppressCommit = true;
        Port1Z0Text = FormatComplexOhms(Working.Port1Z0);
        Port2Z0Text = FormatComplexOhms(Working.Port2Z0);
        DispersionCorrection = Working.DispersionCorrection;
        AdaptiveSampling     = Working.AdaptiveSampling;
        DirectVerticalKernel = Working.DirectVerticalKernel;
        AnalysisKind = Working.AnalysisKind;
        SignalLayerChoice = Working.SignalStackupLayerName is { Length: > 0 } s ? s : InferSignalLayer;
        RefreshMeshText();
        _suppressCommit = false;
        Refresh();
    }

    private void RefreshMeshText()
    {
        _suppressCommit = true;
        var m = Working.Mesh;
        MinCellsAcrossWidthText = m.MinCellsAcrossWidth.ToString(CultureInfo.InvariantCulture);
        EdgeCellsText           = m.EdgeCells.ToString(CultureInfo.InvariantCulture);
        EdgeFractionText        = m.EdgeFractionOfWidth.ToString("G6", CultureInfo.InvariantCulture);
        EdgeGrowthText          = m.EdgeGrowthRatio.ToString("G6", CultureInfo.InvariantCulture);
        TruncationHeightsText   = m.TruncationHeights.ToString("G6", CultureInfo.InvariantCulture);
        TruncationTailCellsText = m.TruncationTailCells.ToString(CultureInfo.InvariantCulture);

        var pm = Working.PlanarMesh;
        PlanarCellsPerWavelengthText = pm.CellsPerWavelength.ToString(CultureInfo.InvariantCulture);
        PlanarEdgeCellsText          = pm.EdgeCells.ToString(CultureInfo.InvariantCulture);
        PlanarEdgeMesh               = pm.EdgeMesh;
        _suppressCommit = false;
    }

    /// <summary>Re-resolve the layout, re-extract, re-ask the kernel. Cheap enough to run on every
    /// settings change, which is exactly what R-em-13 asks for.</summary>
    public void Refresh()
    {
        Problem            = null;
        PlanarProblem      = null;
        OnPropertyChanged(nameof(DirectVerticalKernelDisabledReason));
        OnPropertyChanged(nameof(AdaptiveSamplingDisabledReason));
        Readback           = null;
        ExtractionRefusal  = null;
        KernelRefusal      = null;
        PlanarExtractionRefusal = null;
        PortRefusal        = null;
        PlanarPorts        = [];
        Notes              = [];
        StackupRows        = [];
        TechnologyName     = "";
        DispersionDisabledReason = "The cross-section has not resolved yet.";

        if (Working.LayoutRef is not { Length: > 0 })
        {
            LayoutStatus = "No layout selected. Pick the layout this EM setup analyses.";
            RaiseState();
            return;
        }

        var source = ResolveLayout?.Invoke(Working.LayoutRef);
        if (source is null)
        {
            LayoutStatus = $"The layout '{Working.LayoutRef}' could not be found. Pick it again, or " +
                            "restore the file — this EM setup references its layout by path and reads " +
                            "the geometry when it runs, so the reference has to resolve.";
            RaiseState();
            return;
        }

        LayoutStatus = Working.LayoutRef;

        if (source.Technology is null)
        {
            ExtractionRefusal =
                $"The layout '{Working.LayoutRef}' has no technology resolved, so nothing says how " +
                "thick its metal is, what is underneath it, or where the ground plane sits. Set a " +
                "workspace default technology, or give the layout its own.";
            RaiseState();
            return;
        }

        TechnologyName = source.Technology.Name;
        BuildStackupRows(source.Technology);
        BuildConductorChoices(source.Technology);

        // ── R-res-1: the registry chooses, here as at run time, from the same two verdicts ─────
        double fMax = TryMaxFrequency();

        var crossSection = CrossSectionExtractor.Extract(
            source.View.Shapes, source.Technology, source.DbuPerMicron,
            Working.ToExtractionSettings(Working.LayoutRef));

        var planar = PlanarExtractor.Extract(
            source.View.Shapes, source.Technology, source.DbuPerMicron, fMax,
            Working.ToExtractionSettings(Working.LayoutRef));

        var choice = EmKernelRegistry.Choose(
            Working.AnalysisKind,
            crossSection.Ok ? EmExtractorVerdict.Yes : EmExtractorVerdict.No(crossSection.Refusal ?? ""),
            planar.Ok       ? EmExtractorVerdict.Yes : EmExtractorVerdict.No(planar.Refusal ?? ""));

        SelectedKernel     = choice.Kind;
        SelectedKernelName = choice.KernelName;
        KernelChoiceReason = choice.Reason;
        OnPropertyChanged(nameof(IsPlanarAnalysis));

        if (choice.Kind == EmAnalysisKind.Planar)
        {
            RefreshPlanar(source, planar, choice);
            return;
        }

        Notes = [.. crossSection.Notes];

        if (!choice.Ok)
        {
            ExtractionRefusal = choice.Refusal;
            RaiseState();
            return;
        }

        Problem  = crossSection.Problem;
        Readback = crossSection.Readback;
        MarkSignalGroundRows();
        RebuildPortRows(crossSection.Problem);

        // R-em-13: ask the kernel now, not at Simulate time.
        var verdict = new QuasiStaticKernel().CanSolve(crossSection.Problem!);
        KernelRefusal = verdict.Ok ? null : verdict.Reason;

        DispersionDisabledReason = QuasiStaticKernel.TryMicrostripDispersion(crossSection.Problem!) is null
            ? "Kirschning–Jansen is derived for a single microstrip — one conductor over a ground " +
              "plane on one substrate — so it does not apply to this cross-section."
            : null;

        RaiseState();
    }

    /// <summary>
    /// The planar half of <see cref="Refresh"/>: R-em-13's "ask live, not at Simulate time" applied
    /// to kernel B, including the PORT refusals — an ambiguous port label is exactly the kind of
    /// thing a user wants to hear about while placing it, not after a three-minute sweep.
    /// </summary>
    private void RefreshPlanar(EmLayoutSource source, PlanarExtractionResult planar, EmKernelChoice choice)
    {
        Notes         = [.. planar.Notes];
        PlanarPorts   = [];
        DispersionDisabledReason =
            "The Kirschning–Jansen correction is kernel A's, applied on top of a quasi-static " +
            "answer. Kernel B is full-wave: dispersion is in the solve, not bolted onto it.";

        if (!choice.Ok)
        {
            PlanarExtractionRefusal = choice.Refusal;
            RaiseState();
            return;
        }

        PlanarProblem = planar.Problem;
        OnPropertyChanged(nameof(DirectVerticalKernelDisabledReason));
        OnPropertyChanged(nameof(AdaptiveSamplingDisabledReason));

        var verdict = new PlanarKernel().CanSolve(planar.Problem!);
        KernelRefusal = verdict.Ok ? null : verdict.Reason;

        var ports = EmPortExtraction.Extract(
            source.View.Shapes, planar.Problem!, source.DbuPerMicron, Working.ResolvePortZ0);

        PortRefusal = ports.Ok ? null : ports.Refusal;
        PlanarPorts = ports.Ports;

        var notes = new List<string>(planar.Notes);
        notes.AddRange(ports.Notes);
        Notes = [.. notes];

        RebuildPlanarPortRows(ports.Ports);
        RaiseState();
    }

    /// <summary>The highest swept frequency, or 0 when the sweep will not expand — the mesher's D4
    /// input. A broken sweep is a real problem and the panel already says so; it must not stop the
    /// geometry from extracting.</summary>
    private double TryMaxFrequency()
    {
        try
        {
            double f = 0;
            foreach (double v in Working.Frequency.Expand()) f = Math.Max(f, v);
            return f;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
        {
            return 0;
        }
    }

    /// <summary>The per-port Z₀ list for a planar setup. The port COUNT comes from the layout's own
    /// port labels — the geometry, not something the user types here (R-cpl-6's own rule, applied to
    /// kernel B's ports).</summary>
    private void RebuildPlanarPortRows(IReadOnlyList<PlanarPort> ports)
    {
        PortRows.Clear();
        for (int i = 0; i < ports.Count; i++)
            PortRows.Add(new EmPortZ0Row
            {
                PortNumber = ports[i].Number,
                Label      = $"Port {ports[i].Number} — {SideLabel(ports[i].Side)} end",
                Text       = FormatComplexOhms(Working.ResolvePortZ0(i)),
            });
        OnPropertyChanged(nameof(ShowPortList));
    }

    private static string SideLabel(PlanarPortSide s) => s switch
    {
        PlanarPortSide.MinX => "low-x",
        PlanarPortSide.MaxX => "high-x",
        PlanarPortSide.MinY => "low-y",
        _                   => "high-y",
    };

    private void RaiseState()
    {
        if (Problem is null && PortRows.Count > 0) RebuildPortRows(null);
        OnPropertyChanged(nameof(BlockingReason));
        OnPropertyChanged(nameof(CanRun));
        SimulateCommand.NotifyCanExecuteChanged();
        AnalysisRefreshed?.Invoke();
    }

    /// <summary>R-em-17: an edited layout clears the displayed mesh; it never keeps drawing the old
    /// one. Called by the workspace when the referenced <c>.clay</c> changes, and by every setting
    /// that would change the mesh.</summary>
    public void InvalidateMesh()
    {
        bool any = MeshReport is not null || MeshNotes.Count > 0
                || PlanarMeshReport is not null || PlanarMeshNotes.Count > 0
                || CurrentDensity is not null || ReferencePlanes.Count > 0;
        if (!any) return;

        MeshReport             = null;
        MeshNotes              = [];
        PlanarMeshReport       = null;
        PlanarMeshNotes        = [];
        PlanarExtractionRefusal = null;
        PlanarProblem          = null;
        OnPropertyChanged(nameof(DirectVerticalKernelDisabledReason));
        OnPropertyChanged(nameof(AdaptiveSamplingDisabledReason));
        // R-em-17, and it matters MORE for the heat map than for the mesh: a current map drawn over
        // edited artwork looks like it still matches the artwork underneath it.
        CurrentDensity         = null;
        ReferencePlanes        = [];
        OnPropertyChanged(nameof(PlanarMeshSummary));
        OnPropertyChanged(nameof(PlanarBudgetRefusal));
        AnalysisRefreshed?.Invoke();
    }

    /// <summary>
    /// L8b — the planar counterpart of <see cref="BuildMesh"/>: extract the planar problem and mesh
    /// it, and <b>nothing else</b>. §10.5 puts the mesh viewer before the solver on purpose, and this
    /// slice has no solver at all; the N report IS the product. R17's verdict comes back with it.
    ///
    /// <para>The mesh is frequency-dependent (D4) but computed ONCE per sweep, from the sweep's
    /// HIGHEST frequency alone — the Green's function is the genuinely per-frequency thing, and that
    /// must not leak into the mesher.</para>
    /// </summary>
    [RelayCommand]
    public void BuildPlanarMesh()
    {
        PlanarMeshReport        = null;
        PlanarMeshNotes         = [];
        PlanarExtractionRefusal = null;
        PlanarProblem           = null;

        var source = Working.LayoutRef is { Length: > 0 } r ? ResolveLayout?.Invoke(r) : null;
        if (source?.Technology is null)
        {
            PlanarExtractionRefusal =
                "The layout this EM setup analyses could not be resolved, or has no technology, so " +
                "there is nothing to say how thick its metal is or what is underneath it.";
            OnPropertyChanged(nameof(PlanarMeshSummary));
            OnPropertyChanged(nameof(PlanarBudgetRefusal));
            AnalysisRefreshed?.Invoke();
            return;
        }

        double fMax = 0;
        try
        {
            var freqs = Working.Frequency.Expand();
            for (int i = 0; i < freqs.Length; i++) fMax = Math.Max(fMax, freqs[i]);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
        {
            // A frequency sweep that will not expand is a real problem, but it is the sweep's, and
            // the panel already says so. Mesh on geometry alone rather than refusing to draw one.
            fMax = 0;
        }

        var extraction = PlanarExtractor.Extract(
            source.View.Shapes, source.Technology, source.DbuPerMicron, fMax, Working.ToExtractionSettings());

        PlanarMeshNotes = [.. extraction.Notes];
        if (!extraction.Ok)
        {
            PlanarExtractionRefusal = extraction.Refusal;
            OnPropertyChanged(nameof(PlanarMeshSummary));
            OnPropertyChanged(nameof(PlanarBudgetRefusal));
            AnalysisRefreshed?.Invoke();
            return;
        }

        PlanarProblem = extraction.Problem;
        var report = SurfaceMesher.Mesh(extraction.Problem!, Working.PlanarMesh);
        PlanarMeshReport = report;

        // R-em-16, unchanged for kernel B: surface the engine's own notes VERBATIM. The mesher wrote
        // the λ_g sentence, the staircasing sentence and the R-msh-8a analytic-model sentence
        // carefully; print them, do not re-word them.
        var notes = new List<string>(extraction.Notes);
        notes.AddRange(report.Notes);
        PlanarMeshNotes = [.. notes];

        OnPropertyChanged(nameof(PlanarMeshSummary));
        OnPropertyChanged(nameof(PlanarBudgetRefusal));
        AnalysisRefreshed?.Invoke();
    }

    /// <summary>R-em-14: the Mesh button calls <see cref="IEmKernel.Mesh"/> and nothing else. No
    /// solve, no RLGC, no s-parameters — the cheap "is my mesh sane?" answer §10.5 says should land
    /// before the solver, and it must stay cheap enough to press repeatedly.</summary>
    [RelayCommand]
    public void BuildMesh()
    {
        Refresh();
        if (Problem is null) return;

        var report = new QuasiStaticKernel().Mesh(Problem, Working.Mesh);
        MeshReport = report;
        // R-em-16: surface the engine's own report VERBATIM. The engine already wrote those
        // sentences carefully — including the R-mom-13 Wheeler-crossover note, which is the one
        // that tells a user sweeping down to 1 MHz that conductor loss is on the DC floor. Print
        // them; do not re-word them.
        MeshNotes = [.. report.Notes];
        AnalysisRefreshed?.Invoke();
    }

    /// <summary>D2: Simulate. Runs the whole EM analysis and lands the result. Disabled with the
    /// live <see cref="BlockingReason"/> visible, per R-em-13 and the established R-L1h-3
    /// disabled-with-a-reason pattern.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    public async Task Simulate()
    {
        if (RunRequested is { } run) await run(this);
    }

    /// <summary>A Simulate run meshes as a side effect, so its report is adopted here rather than
    /// making the user press Mesh again to see the same mesh the answer came from.</summary>
    public void AdoptMeshReport(EmMeshReport report)
    {
        MeshReport = report;
        MeshNotes  = [.. report.Notes];
        AnalysisRefreshed?.Invoke();
    }

    // ── L8e/D5: the current-density heat map, and its scale ───────────────────────────────────

    /// <summary>The per-cell |J| map a planar run produced, or null. R-em-17 applies unchanged, and
    /// it matters MORE here than for the mesh overlay: a current map drawn over edited artwork looks
    /// like it still matches.</summary>
    [ObservableProperty] private PlanarCurrentDensityMap? _currentDensity;

    /// <summary>R-res-8 — the scale, with its units and its normalisation, in the engine's own
    /// words. The panel prints this; it computes nothing.</summary>
    public string CurrentDensityScale => CurrentDensity?.ScaleCaption ?? "";

    /// <summary>The resolved ports of the last planar run, so the layout can draw the de-embedding
    /// reference planes §10.6 asks to be shown — over a location the ENGINE reports
    /// (<c>PlanarPortResolution.ReferencePlaneM</c>), never one this layer derives.</summary>
    [ObservableProperty] private IReadOnlyList<PlanarPortResolution> _referencePlanes = [];

    partial void OnCurrentDensityChanged(PlanarCurrentDensityMap? value)
        => OnPropertyChanged(nameof(CurrentDensityScale));

    /// <summary>A planar Simulate run meshes and solves as a side effect; its report, its heat map
    /// and its resolved ports are adopted here so the layout overlay shows the mesh the ANSWER came
    /// from rather than whatever Mesh was last pressed on.</summary>
    public void AdoptPlanarResult(PlanarMeshReport report, PlanarSolveResult? solve)
    {
        PlanarMeshReport = report;
        PlanarMeshNotes  = [.. report.Notes];
        _ = solve;   // reserved: the sweep's own diagnostics already ride the run's warnings.
        OnPropertyChanged(nameof(PlanarMeshSummary));
        OnPropertyChanged(nameof(PlanarBudgetRefusal));
        AnalysisRefreshed?.Invoke();
    }

    /// <summary>Adopts the heat map and the reference planes a planar run produced.</summary>
    public void AdoptCurrentDensity(
        PlanarCurrentDensityMap? map, IReadOnlyList<PlanarPortResolution> ports)
    {
        CurrentDensity  = map;
        ReferencePlanes = ports;
        AnalysisRefreshed?.Invoke();
    }

    // ── Read-only projections ──────────────────────────────────────────────────────────────────

    private void BuildStackupRows(Technology tech)
    {
        // R-em-12: the stackup is SHOWN, not edited — two editors for one piece of process data is
        // how they diverge. The panel links out to the .ctech instead.
        var rows = new ObservableCollection<EmStackupRow>();
        foreach (var l in tech.Stackup.Layers)
        {
            string electrical = l.Kind switch
            {
                StackupKind.Dielectric =>
                    $"εr {l.Epsr.ToString("G4", CultureInfo.InvariantCulture)} · " +
                    $"tanδ {l.TanD.ToString("G4", CultureInfo.InvariantCulture)} · " +
                    $"µr {l.Mur.ToString("G4", CultureInfo.InvariantCulture)}",
                StackupKind.Conductor =>
                    $"σ {l.SigmaSm.ToString("G4", CultureInfo.InvariantCulture)} S/m" +
                    (l.IsGroundReference ? " · ground reference" : ""),
                _ => l.Fill is { } f ? f.ToString() : "",
            };
            string layers = l.DrawingLayers.Count == 0
                ? "—"
                : string.Join(", ", l.DrawingLayers.Select(k => $"{k.Layer}/{k.Datatype}"));
            rows.Add(new EmStackupRow(
                l.Kind.ToString(), l.Name,
                CrossSectionExtractor.FormatMeters(l.ThicknessDbu / (LayoutUnits.DefaultDbuPerMicron * 1e6)),
                electrical, layers, false, l.Kind == StackupKind.Conductor && l.IsGroundReference));
        }
        StackupRows = rows;
    }

    private void MarkSignalGroundRows()
    {
        if (Readback is null) return;
        for (int i = 0; i < StackupRows.Count; i++)
        {
            var r = StackupRows[i];
            bool sig = string.Equals(r.Name, Readback.SignalLayerName, StringComparison.Ordinal);
            bool gnd = Readback.GroundLayerName is { } g && string.Equals(r.Name, g, StringComparison.Ordinal);
            if (sig != r.IsSignal || gnd != r.IsGround)
                StackupRows[i] = r with { IsSignal = sig, IsGround = gnd };
        }
    }

    private void BuildConductorChoices(Technology tech)
    {
        var choices = new ObservableCollection<string> { InferSignalLayer };
        foreach (var l in tech.Stackup.Layers)
            if (l.Kind == StackupKind.Conductor && !l.IsGroundReference)
                choices.Add(l.Name);
        ConductorLayerChoices = choices;
        BuildAnalysisLevelRows(tech);
    }

    // ── L9d/D5 — which conductor levels the PLANAR analysis includes ───────────────────────────
    //
    // A row per signal conductor entry, checked when the setup names it. None checked means "infer",
    // which is every level that carries artwork — the pre-L9d behaviour and what every existing .cem
    // asks for. This deliberately does NOT replace the single "Signal conductor" combo above: that is
    // the cross-section kernel's own control (R-em-4b) and means something different — WHICH single
    // conductor a uniform cross-section is about, not which set of levels a full-wave solve meshes.
    private void BuildAnalysisLevelRows(Technology tech)
    {
        var rows = new ObservableCollection<EmAnalysisLevelRow>();
        foreach (var l in tech.Stackup.Layers.AsEnumerable().Reverse())      // Layers are TOP-to-bottom
            if (l.Kind == StackupKind.Conductor && !l.IsGroundReference)
            {
                var row = new EmAnalysisLevelRow(l.Name)
                {
                    IsIncluded = Working.AnalysisLevelNames.Contains(l.Name, StringComparer.Ordinal),
                };
                row.Toggled += OnAnalysisLevelToggled;
                rows.Add(row);
            }
        AnalysisLevelRows = rows;
    }

    private void OnAnalysisLevelToggled(EmAnalysisLevelRow row)
    {
        if (_suppressCommit) return;
        var before = SnapshotJson();
        if (row.IsIncluded)
        {
            if (!Working.AnalysisLevelNames.Contains(row.Name, StringComparer.Ordinal))
                Working.AnalysisLevelNames.Add(row.Name);
        }
        else Working.AnalysisLevelNames.RemoveAll(n => string.Equals(n, row.Name, StringComparison.Ordinal));

        CommitEdit(before, $"{(row.IsIncluded ? "Include" : "Exclude")} EM analysis level '{row.Name}'");
        InvalidateMesh();
        Refresh();
    }

    // ── Parsing helpers ────────────────────────────────────────────────────────────────────────

    private static bool TryInt(string s, int min, out int v)
        => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v >= min;

    private static bool TryDouble(string s, out double v)
        => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    /// <summary>Accepts <c>50</c>, <c>50+10j</c>, <c>50 - 10j</c>. Complex is permitted because
    /// <c>RFNetwork.ZToS</c> already handles a complex per-port reference — refusing it here would
    /// be the UI narrowing a capability the engine has.</summary>
    public static bool TryParseComplexOhms(string text, out Complex z)
    {
        z = default;
        string s = text.Replace(" ", "").Replace("Ω", "").Replace("ohm", "", StringComparison.OrdinalIgnoreCase);
        if (s.Length == 0) return false;

        int jAt = s.IndexOf('j');
        if (jAt < 0) jAt = s.IndexOf('i');
        if (jAt < 0)
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double re)
                   && Finish(re, 0, out z);

        // Split at the sign that begins the imaginary term (never the leading sign, never an
        // exponent's sign).
        int split = -1;
        for (int i = 1; i < s.Length; i++)
        {
            if (s[i] is not ('+' or '-')) continue;
            if (s[i - 1] is 'e' or 'E') continue;
            split = i;
        }
        if (split < 0)
        {
            string imagOnly = s.Remove(jAt, 1);
            if (imagOnly is "" or "+" ) imagOnly = "1";
            if (imagOnly is "-") imagOnly = "-1";
            return double.TryParse(imagOnly, NumberStyles.Float, CultureInfo.InvariantCulture, out double im)
                   && Finish(0, im, out z);
        }

        string realPart = s[..split];
        string imagPart = s[split..].Remove(s[split..].IndexOf('j') >= 0 ? s[split..].IndexOf('j') : s[split..].IndexOf('i'), 1);
        if (imagPart is "+") imagPart = "1";
        if (imagPart is "-") imagPart = "-1";

        return double.TryParse(realPart, NumberStyles.Float, CultureInfo.InvariantCulture, out double r)
            && double.TryParse(imagPart, NumberStyles.Float, CultureInfo.InvariantCulture, out double i2)
            && Finish(r, i2, out z);

        static bool Finish(double re, double im, out Complex outZ)
        {
            outZ = new Complex(re, im);
            return true;
        }
    }

    public static string FormatComplexOhms(Complex z)
        => z.Imaginary == 0
            ? z.Real.ToString("G6", CultureInfo.InvariantCulture)
            : $"{z.Real.ToString("G6", CultureInfo.InvariantCulture)}" +
              $"{(z.Imaginary < 0 ? "-" : "+")}" +
              $"{Math.Abs(z.Imaginary).ToString("G6", CultureInfo.InvariantCulture)}j";
}

/// <summary>
/// One row of the panel's per-port reference-impedance list (R-cpl-6). Staged text, like every other
/// typed field in this panel, so a complex value can be typed and a bad one reverts rather than
/// throwing.
/// </summary>
public sealed partial class EmPortZ0Row : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public int    PortNumber { get; init; }
    public string Label      { get; init; } = "";

    [ObservableProperty] private string  _text = "50";
    [ObservableProperty] private string? _error;

    public bool HasError => Error is not null;

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));
}
