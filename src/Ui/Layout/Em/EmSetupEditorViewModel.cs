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
using CircuitRF.Engine;
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
    public string FilePath { get; private set; }

    public EmSetup Working { get; private set; }

    public UndoRedoStack UndoRedo { get; } = new();
    public IRelayCommand UndoCommand { get; }
    public IRelayCommand RedoCommand { get; }
    public IRelayCommand SaveCommand { get; }

    [ObservableProperty] private bool _isDirty;

    /// <summary>Wired by the workspace. Returns null when the reference does not resolve — which
    /// degrades to a stated message, never a throw (Tier D).</summary>
    public Func<string, EmLayoutSource?>? ResolveLayout { get; set; }

    /// <summary>
    /// Turns an absolute <c>.clay</c> path into the form <see cref="EmSetup.LayoutRef"/> stores —
    /// workspace-relative when the layout is inside the workspace, absolute otherwise. The rule is
    /// the workspace's (it owns the root), and it must be the exact inverse of the resolution
    /// <see cref="ResolveLayout"/> performs, or Change Layout would write a reference that then
    /// fails to resolve. Unset falls back to the absolute path, which always resolves.
    /// </summary>
    public Func<string, string>? MakeLayoutRef { get; set; }

    /// <summary>What <see cref="EmGeometry.Flatten"/> had to say about the last refresh's geometry —
    /// carried on a field because <c>RefreshPlanar</c> builds its own note list and both paths must
    /// report the same thing about the same artwork.</summary>
    private IReadOnlyList<string> _geometryNotes = [];

    public event Action<string>? SaveError;
    public event Action<string>? EmSetupSaved;

    /// <summary>Raised after a successful <see cref="SaveAs"/>, with the NEW absolute path. Distinct
    /// from <see cref="EmSetupSaved"/> because the workspace has to re-key its open-document map and
    /// the document has to retitle — neither is needed for an ordinary save.</summary>
    public event Action<string>? EmSetupSavedAs;

    /// <summary>
    /// D2 — <b>Simulate</b> runs the EM simulation; <b>Mesh</b> computes the mesh only. The Mesh
    /// button never solves. This seam is what the workspace wires the run path onto (R-em-18: the
    /// same five steps as <c>RunSchematicDocAsync</c> with a different middle) — the VM itself owns
    /// no dispatcher, no results writer and no Data Display.
    /// </summary>
    public Func<EmSetupEditorViewModel, Task>? RunRequested { get; set; }

    /// <summary>
    /// Cancels the run currently in flight. Set by the host alongside <see cref="IsRunning"/>, and
    /// cleared with it — so the Cancel button can only ever exist while there is something to cancel.
    ///
    /// <para>The cancellation SOURCE stays with the host, not here: this view model has no business
    /// owning a <c>CancellationTokenSource</c>, and keeping it out is what lets this whole file stay
    /// framework-free.</para>
    /// </summary>
    public Action? CancelRequested { get; set; }

    /// <summary>
    /// Runs the Mesh button's work off the UI thread, with a progress row and a Cancel. Set by the
    /// host; when it is null the command falls back to meshing synchronously, which is what every
    /// headless caller and test does.
    ///
    /// <para><b>Why Mesh gets the same treatment as Simulate</b> (owner, 2026-08-09: "I've seen
    /// geometry in commercial MoM take 2 min to mesh or longer — it depends on geometry"). Measured
    /// on this codebase's own single-polygon line fixture, meshing is 0.1-0.4 ms — but that fixture
    /// is one polygon, and the mesher's dominant term is layers x grid rows x POLYGONS in the span
    /// scan. R17's ceiling bounds the cell count, not the polygon count, so real artwork is exactly
    /// the case the measurement does not cover.</para>
    /// </summary>
    public Func<EmSetupEditorViewModel, Task>? MeshRequested { get; set; }

    /// <summary>Cancels the mesh in flight. Set and cleared by the host alongside
    /// <see cref="IsMeshing"/>.</summary>
    public Action? CancelMeshRequested { get; set; }

    /// <summary>True while a mesh is in flight. Drives the toolbar's Mesh/Cancel swap.</summary>
    [ObservableProperty] private bool _isMeshing;

    partial void OnIsMeshingChanged(bool value)
    {
        BuildActiveMeshCommand.NotifyCanExecuteChanged();
        CancelMeshCommand.NotifyCanExecuteChanged();
        SimulateCommand.NotifyCanExecuteChanged();
    }

    /// <summary>True while either long operation is in flight — the one gate that stops a Mesh and a
    /// Simulate overlapping, which would have them both meshing the same problem at once.</summary>
    public bool IsBusy => IsRunning || IsMeshing;

    /// <summary>
    /// True from the moment a stop is asked for until the operation actually ends.
    ///
    /// <para><b>It has to be a state rather than an instant</b>: cancellation lands at a work
    /// boundary — a frequency point, a grid row — so a full-wave run can keep going for tens of
    /// seconds after Cancel. A button that still reads "Cancel" through all of that reads as a button
    /// whose press was missed, which is exactly what makes a user press it again. Set by the host
    /// (the run owns the token, this view model deliberately owns no
    /// <c>CancellationTokenSource</c>), and cleared with <see cref="IsRunning"/>/<see cref="IsMeshing"/>.</para>
    ///
    /// <para>ONE flag for both operations, because <see cref="IsBusy"/> already guarantees a mesh and a
    /// simulate can never overlap.</para>
    /// </summary>
    [ObservableProperty] private bool _isCancelling;

    partial void OnIsCancellingChanged(bool value)
    {
        CancelMeshCommand.NotifyCanExecuteChanged();
        CancelSimulateCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CancelButtonText));
    }

    /// <summary>What both Cancel buttons say. Bound rather than literal so the pending stop is
    /// visible on whichever button started the work.</summary>
    public string CancelButtonText => IsCancelling ? "Cancelling…" : "Cancel";

    [RelayCommand(CanExecute = nameof(CanCancelMesh))]
    public void CancelMesh() => CancelMeshRequested?.Invoke();

    private bool CanCancelMesh() => IsMeshing && !IsCancelling;

    /// <summary>True while an EM run is in flight. Drives the toolbar's Simulate/Cancel swap and
    /// gates both commands, so the two can never both be available.</summary>
    [ObservableProperty] private bool _isRunning;

    partial void OnIsRunningChanged(bool value)
    {
        SimulateCommand.NotifyCanExecuteChanged();
        CancelSimulateCommand.NotifyCanExecuteChanged();
        BuildActiveMeshCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsBusy));
    }

    /// <summary>Raised when the extraction/mesh state changed — the layout canvas's cue to
    /// re-render (or drop) the mesh overlay (R-em-17).</summary>
    public event Action? AnalysisRefreshed;

    // ── Resolved state, recomputed on every settings change ────────────────────────────────────

    [ObservableProperty] private EmCrossSectionReadback? _readback;
    [ObservableProperty] private string? _extractionRefusal;
    [ObservableProperty] private string? _kernelRefusal;
    [ObservableProperty] private ObservableCollection<string> _notes = [];

    /// <summary>Gates the Notes label AND its list together, so a heading is never left standing over
    /// nothing — the failure a bare "Cross-section" heading over a null readback already produced
    /// once (owner report, 2026-08-11).</summary>
    public bool HasNotes => Notes.Count > 0;

    partial void OnNotesChanged(ObservableCollection<string> value) => OnPropertyChanged(nameof(HasNotes));
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

    // ── Output file (owner request, 2026-08-09) ────────────────────────────────────────────────

    /// <summary>
    /// Staged text for <see cref="EmSetupModel.SnpOutputPathOverride"/> — where the s-parameters land.
    /// Committed on LostFocus/Enter like every other typed field in this panel, so a half-typed path
    /// never reaches the model.
    ///
    /// <para><b>Blank means the default</b>, which is shown as the box's placeholder rather than
    /// pre-filled: pre-filling would turn "follow the layout's name" into a frozen literal the moment
    /// anyone renamed anything, which is the same trap <c>TechRef = null</c> avoids by meaning
    /// "the workspace default" rather than a copied path.</para>
    /// </summary>
    [ObservableProperty] private string _snpOutputPathText = "";

    /// <summary>What a blank box resolves to — the layout's own name with its <c>.clay</c> swapped
    /// for <c>.sNp</c>, N being however many ports the run finds. Shown as the placeholder.</summary>
    public string SnpOutputPlaceholder
    {
        get
        {
            string stem = Working.LayoutRef is { Length: > 0 } r
                ? Path.GetFileNameWithoutExtension(r)
                : Working.Name;
            if (stem.Length == 0) stem = "results";
            return $"{stem}.sNp";
        }
    }

    /// <summary>Commits the staged path. Same value is a no-op, so tabbing through pushes no undo
    /// entry.</summary>
    public void CommitSnpOutputPath()
    {
        string v = (SnpOutputPathText ?? "").Trim();
        if (v == Working.SnpOutputPathOverride) return;

        var before = SnapshotJson();
        Working.SnpOutputPathOverride = v;
        CommitEdit(before, "Set output file");
        SnpOutputPathText = Working.SnpOutputPathOverride;
    }

    /// <summary>Discards the staged text — Escape's half of the contract.</summary>
    public void RevertSnpOutputPath() => SnpOutputPathText = Working.SnpOutputPathOverride;

    /// <summary>Where this workspace writes results, supplied by the host (this file is
    /// framework-free and has no workspace of its own). Null when no workspace is open.</summary>
    public Func<string?>? ResultsRootProvider { get; set; }

    /// <summary>
    /// Turns a browsed absolute path into the form the setup stores: RELATIVE to the results folder
    /// when it sits inside it, absolute otherwise.
    ///
    /// <para>Same rule <c>EmRunService.ResolveSnpBasePath</c> already reads it by, and the same
    /// reasoning <c>WorkspaceRefs</c> applies to data sources: a path inside the workspace must
    /// survive that workspace being moved, and one outside it cannot be made portable by any
    /// encoding — so it is stored plainly rather than as a <c>../../..</c> chain that breaks on the
    /// first move.</para>
    /// </summary>
    public string MakeOutputPathRef(string absolutePath)
    {
        if (ResultsRootProvider?.Invoke() is not { Length: > 0 } root) return absolutePath;
        try
        {
            string rel = Path.GetRelativePath(root, absolutePath);
            return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)
                ? absolutePath
                : rel.Replace(Path.DirectorySeparatorChar, '/');
        }
        catch { return absolutePath; }
    }

    /// <summary>The mesh row's own outcome, appended to the end of the row it already owns — so the
    /// finished line still says WHAT was meshed rather than collapsing to a bare "done".</summary>
    public string MeshOutcomeText()
    {
        if (PlanarMeshReport is { } pr)
            return $"{pr.UnknownCount:N0} unknown(s) over {pr.CellCount:N0} cell(s)";
        if (MeshReport is { } mr)
            return $"{mr.UnknownCount:N0} unknown(s)";
        return PlanarExtractionRefusal ?? ExtractionRefusal ?? "nothing to mesh";
    }

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

    partial void OnSelectedKernelChanged(EmAnalysisKind value)
    {
        OnPropertyChanged(nameof(PortsHelpText));
        // ShowPortList and ShowNearFarPortZ0 both read SelectedKernel, and a setup that switches
        // kernels without its port COUNT changing would otherwise leave the wrong one of the two
        // port controls on screen — RebuildPortRows is the only other place they are published.
        OnPropertyChanged(nameof(ShowPortList));
        OnPropertyChanged(nameof(ShowNearFarPortZ0));
    }

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

    /// <summary>
    /// One line saying what the SELECTED analysis is for, in the user's terms rather than ours
    /// (owner request, 2026-08-09). It answers the question the dropdown alone cannot — "why would
    /// I ever pick the other one?" — which for the cross-section analysis has a concrete answer:
    /// it is exact for a uniform line and roughly a thousand times faster, which on a 101-point
    /// sweep is the difference between under a second and over an hour.
    /// </summary>
    public string AnalysisKindDescription => AnalysisKind switch
    {
        EmAnalysisKind.Auto =>
            "Picks the analysis from the geometry, preferring the faster one whenever it applies, " +
            "and always says which it picked and why.",
        EmAnalysisKind.CrossSection =>
            "For a straight, constant-width line: solves its cross-section for Z₀, ε_eff, " +
            "loss and delay. Exact for that geometry and effectively instant, because the answer " +
            "does not change with frequency.",
        EmAnalysisKind.Planar =>
            "For arbitrary artwork: bends, stubs, gaps, coupled structures and multi-level metal " +
            "with vias. It sees discontinuities, coupling and radiation — and costs a full " +
            "solve at every frequency.",
        _ => "",
    };

    public bool IsPlanarAnalysis => SelectedKernel == EmAnalysisKind.Planar;

    /// <summary>Latest successfully-extracted problem — the one thing Mesh and Simulate both
    /// consume, so they can never disagree about what is being solved.</summary>
    public EmProblem? Problem { get; private set; }

    public string? BlockingReason =>
        SelectedKernel == EmAnalysisKind.Planar
            ? PlanarExtractionRefusal ?? KernelRefusal ?? PortRefusal ?? PlanarBudgetRefusal
            : InternalPortOnTheWrongKernel ?? ExtractionRefusal ?? KernelRefusal;

    /// <summary>
    /// R-em-13, applied to the one refusal a user would otherwise meet only after pressing Simulate:
    /// an internal delta-gap port on a setup that resolved to the uniform-line kernel.
    ///
    /// <para>The run refuses it (<see cref="EmRunService.InternalPortNeedsFullWave"/>, whose wording
    /// this shares rather than paraphrases). Showing it live matters more here than for most, because
    /// the user did not choose that kernel — <c>Auto</c> did, on the grounds that the geometry
    /// reduces to a uniform cross-section, which a line with a gap on it does. Nothing on screen
    /// would otherwise connect "I set port 3 to Internal delta gap" to "the answer has two ports".
    /// The same applies to an internal port, whose via the cross-section kernel does not model either.</para>
    /// </summary>
    public string? InternalPortOnTheWrongKernel =>
        SelectedKernel == EmAnalysisKind.CrossSection && Working.DeclaresInternalPort()
            ? EmRunService.InternalPortNeedsFullWave(SelectedKernelName is { Length: > 0 } n
                                                        ? n : "uniform-line (quasi-static) kernel")
            : null;

    public bool CanRun =>
        !IsBusy &&
        (SelectedKernel == EmAnalysisKind.Planar
            ? PlanarProblem is not null && BlockingReason is null
            : Problem is not null && BlockingReason is null);

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

    /// <summary>
    /// <b>The fixed "Port 1 Z₀ / Port 2 Z₀" pair, shown only when the per-port list is NOT.</b>
    ///
    /// <para>Owner report, 2026-08-25: "I don't see a Port 3 Z₀ option in the .cem editor." The panel
    /// was drawing BOTH — a grid captioned <i>Port 1</i> and <i>Port 2</i>, and beneath it a list
    /// with a row for every port including those same two. A user reads the captioned pair as the
    /// port list, finds it stops at two, and has no reason to read the unlabelled rows below it as
    /// the same thing continued. Worse, the two really are one value: a row shows
    /// <see cref="EmSetup.ResolvePortZ0"/>, which falls back to exactly this pair until the row is
    /// overridden, so editing the caption changed the row and editing the row then shadowed the
    /// caption.</para>
    ///
    /// <para><see cref="ShowPortList"/>'s own note already said this pair and the list must never
    /// appear together — "putting a two-row list beside those same two fields would be two controls
    /// for one value". That held while the list was cross-section-only and needed three ports to
    /// appear; the planar kernel shows the list unconditionally, and the rule was never extended to
    /// match.</para>
    /// </summary>
    public bool ShowNearFarPortZ0 => !ShowPortList;

    /// <summary>
    /// The Ports group's own explanation, shown as the header's tooltip rather than as two paragraphs
    /// in the panel (owner request, 2026-08-11). It follows the CHOSEN kernel because the two
    /// analyses answer "where is the port?" completely differently — one has no meshed port at all.
    ///
    /// <para>The de-embedding reference plane's position is deliberately NOT restated here: it is a
    /// property of the method rather than a setting, so it belongs in the documentation, and every
    /// resolved port already carries its own note.</para>
    /// </summary>
    public string PortsHelpText => SelectedKernel == EmAnalysisKind.Planar
        ? "Each port is a port LABEL in the layout — place them with the layout editor's Port tool. " +
          "Which end of a conductor a label names is inferred from the geometry and reported in the " +
          "notes; an ambiguous one is refused rather than guessed.\n\n" +
          "The list below sets each port's reference impedance individually. Ports may sit on " +
          "different conductors — that is what a coupled or multi-port structure is."
        : "The uniform-line analysis has no meshed port, so the ports ARE the ends of the extracted " +
          "conductors by construction — there is nothing to place, and de-embedding is a no-op.\n\n" +
          "Port 2k−1 is conductor k's near end and port 2k its far end, so two conductors give four " +
          "ports. The list below sets each one's reference impedance individually.";

    /// <summary>
    /// The Analysis-levels expander's header — it answers the question the collapsed list would, so
    /// the list itself only has to be opened when a level is actually being changed.
    /// </summary>
    public string AnalysisLevelsSummary
    {
        get
        {
            int total = AnalysisLevelRows.Count;
            int on    = AnalysisLevelRows.Count(r => r.IsIncluded);
            return on == 0
                ? $"Analysis levels — every level with artwork ({total} available)"
                : $"Analysis levels — {on} of {total} included";
        }
    }

    // ── Mesh settings, staged as text (all six — R-em-11) ──────────────────────────────────────

    [ObservableProperty] private string _minCellsAcrossWidthText = "";
    [ObservableProperty] private string _edgeCellsText           = "";
    [ObservableProperty] private string _edgeFractionText        = "";
    [ObservableProperty] private string _edgeGrowthText          = "";
    [ObservableProperty] private string _truncationHeightsText   = "";
    [ObservableProperty] private string _truncationTailCellsText = "";

    /// <summary>
    /// Bug report, 2026-08-14: "EM setup has a couple of text boxes that are not validated properly."
    /// These six shared one silent failure mode the Port Z0 fields above never had: an unparseable or
    /// out-of-range value fell through <see cref="CommitMeshField"/>'s switch to the unchanged-model
    /// arm, which called <see cref="RefreshMeshText"/> and silently overwrote whatever the user had
    /// just typed back to the last-committed value — no message, no red text, nothing. Mirrors
    /// <see cref="Port1Z0Error"/>'s own contract: set on an invalid commit (and the bad text is left
    /// in place rather than reverted, so there is something to fix), cleared on the next valid one.
    /// </summary>
    [ObservableProperty] private string? _meshFieldError;

    // ── L8b's D3 planar mesh controls — THREE, plus the conformal FOURTH ──────────────────────
    //
    // D3 said "exactly three user controls, and no more"; the conformal-boundary-cells brief adds a
    // fourth on the owner's explicit instruction, and D3's reasoning still stands for everything
    // else. Cells per wavelength and Edge cells change how FINELY the same structure is discretised;
    // Boundary cells changes WHICH STRUCTURE is discretised at all, which is a modelling decision.

    [ObservableProperty] private string _planarCellsPerWavelengthText = "";
    [ObservableProperty] private string _planarEdgeCellsText          = "";
    [ObservableProperty] private bool   _planarEdgeMesh;

    /// <summary>
    /// M0's mesh-frequency control, staged as text like every other dimensioned field in this panel.
    /// <b>Blank means "max sweep"</b> — the model stores <c>null</c>, the mesher sizes at the
    /// sweep's own top, and that is exactly the behaviour every existing <c>.cem</c> already has.
    /// </summary>
    [ObservableProperty] private string _planarMeshFrequencyText = "";
    [ObservableProperty] private PlanarBoundaryCells _planarBoundaryCells =
        PlanarMeshSettings.DefaultBoundaryCells;

    /// <summary>Same contract as <see cref="MeshFieldError"/>, for this group's three staged-text
    /// fields (Cells per wavelength, Edge cells, Mesh frequency) — kept separate because the two
    /// groups are never both visible at once (<c>IsPlanarAnalysis</c> toggles which one shows).</summary>
    [ObservableProperty] private string? _planarMeshFieldError;

    /// <summary>The two boundary models, for the panel's combo. Sourced from the enum rather than
    /// hand-listed so a third member cannot silently fail to appear.</summary>
    public static IReadOnlyList<PlanarBoundaryCells> BoundaryCellsChoices { get; } =
        Enum.GetValues<PlanarBoundaryCells>();

    /// <summary>
    /// The unit the mesh-frequency field is edited in — <b>the sweep's own top-frequency unit</b>,
    /// never a second unit choice of its own. The mesh frequency is only ever read against the
    /// sweep's top, so giving it an independent unit selector would be one more thing to keep in
    /// step for no gain.
    /// </summary>
    public string MeshFrequencyUnit => Frequency.StopUnit;

    /// <summary>Blank means "max sweep", and the placeholder says so rather than leaving the
    /// user to infer it from an empty box.</summary>
    public string MeshFrequencyPlaceholder
    {
        get
        {
            double top = TryMaxFrequency();
            if (!(top > 0)) return "max sweep";
            double mult = ViewModels.FreqUnitHelper.Multiplier(MeshFrequencyUnit);
            return $"max sweep ({(top / mult).ToString("G6", CultureInfo.InvariantCulture)})";
        }
    }

    // ── Signal layer + dispersion ──────────────────────────────────────────────────────────────

    [ObservableProperty] private ObservableCollection<string> _conductorLayerChoices = [];
    [ObservableProperty] private string _signalLayerChoice = InferSignalLayer;
    [ObservableProperty] private bool   _dispersionCorrection;
    [ObservableProperty] private bool   _adaptiveSampling = true;
    [ObservableProperty] private bool   _directVerticalKernel;
    [ObservableProperty] private bool   _acceleratedSolve;

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

    /// <summary>
    /// Write this setup to a DIFFERENT <c>.cem</c> and follow it from then on (owner request,
    /// 2026-08-09). The original file is left exactly as it was on disk — Save As is not a move.
    ///
    /// <para>The file picker itself lives in the view's code-behind, not here: everything under
    /// <c>src/Ui/Layout/</c> is framework-free, so this method takes a resolved path and does the
    /// I/O. <see cref="FilePath"/> is the one piece of mutable identity, and it moves only here.
    /// </para>
    /// </summary>
    public void SaveAs(string newPath)
    {
        if (string.IsNullOrWhiteSpace(newPath)) return;
        try
        {
            EmSetupPersistence.SaveToFile(newPath, Working);
        }
        catch (Exception ex)
        {
            SaveError?.Invoke($"Couldn't save EM setup to '{newPath}': {ex.Message}");
            return;
        }
        FilePath = newPath;
        UndoRedo.MarkSaved();
        EmSetupSavedAs?.Invoke(newPath);
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

        // The mesh-frequency field is edited in the SWEEP's own unit and stored in hertz, so a
        // change to the sweep's top-frequency unit has to re-render it. Without this, a stored
        // 10 GHz would silently read as "10" beside an "MHz" label — a factor of a thousand,
        // reported by nothing.
        RefreshMeshText();
        Refresh();
    }

    /// <summary>
    /// Point this setup at a different <c>.clay</c> (owner request, 2026-08-09: "user needs an
    /// elegant way to change which .clay file the EM Setup is for").
    ///
    /// <para>Undoable like every other field here, and it runs the SAME <see cref="Refresh"/> a
    /// frequency or port edit does — so the cross-section readback, the extraction refusal, the port
    /// list and the mesh state all re-derive against the new artwork immediately, and a previously
    /// computed mesh is dropped rather than left on screen belonging to a layout that is no longer
    /// the subject.</para>
    /// </summary>
    public void SetLayoutRef(string absoluteClayPath)
    {
        if (_suppressCommit || string.IsNullOrWhiteSpace(absoluteClayPath)) return;

        string layoutRef = MakeLayoutRef?.Invoke(absoluteClayPath) ?? absoluteClayPath;
        if (string.Equals(layoutRef, Working.LayoutRef, StringComparison.Ordinal)) return;

        var before = SnapshotJson();
        Working.LayoutRef = layoutRef;
        CommitEdit(before, "Change EM setup layout");
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

    /// <summary>
    /// Commits one row's port TYPE. Same shape as <see cref="CommitPortRow"/> above — pad the list
    /// from the default so a change on port 4 cannot silently retype ports 1-3, no-change guard
    /// before the undo entry, one undo entry per change.
    ///
    /// <para><b>It calls <see cref="InvalidateMesh"/> and every other per-port setting does not.</b>
    /// The impedance beside it is a renormalisation applied to the answer; the type decides WHERE
    /// the excitation is cut and therefore which rooftops are driven, so a mesh report computed
    /// under the other type is about a different excitation and must not go on being shown.</para>
    /// </summary>
    public void CommitPortKind(int index)
    {
        if (_suppressCommit) return;
        if (index < 0 || index >= PortRows.Count) return;

        var kind = PortRows[index].Kind;
        var list = Working.PortKinds;
        while (list.Count <= index) list.Add(Working.ResolvePortKind(list.Count));
        if (list[index] == kind) return;                    // no-change guard: no undo entry

        var before = SnapshotJson();
        list[index] = kind;
        CommitEdit(before, $"Change port {PortRows[index].PortNumber} type");
        InvalidateMesh();
        OnPropertyChanged(nameof(InternalPortOnTheWrongKernel));
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
        OnPropertyChanged(nameof(ShowNearFarPortZ0));
    }

    public void CommitMeshField(string field)
    {
        if (_suppressCommit) return;
        var m  = Working.Mesh;
        var pm = Working.PlanarMesh;

        EmMeshSettings     updated       = m;
        PlanarMeshSettings updatedPlanar = pm;
        string?            error         = null;

        switch (field)
        {
            case nameof(EmMeshSettings.MinCellsAcrossWidth):
                if (TryInt(MinCellsAcrossWidthText, 1, out int minCells)) updated = m with { MinCellsAcrossWidth = minCells };
                else error = "Enter a whole number of 1 or more.";
                break;
            case nameof(EmMeshSettings.EdgeCells):
                if (TryInt(EdgeCellsText, 0, out int edgeCells)) updated = m with { EdgeCells = edgeCells };
                else error = "Enter a whole number of 0 or more.";
                break;
            case nameof(EmMeshSettings.EdgeFractionOfWidth):
                if (TryDouble(EdgeFractionText, out double edgeFrac) && edgeFrac > 0) updated = m with { EdgeFractionOfWidth = edgeFrac };
                else error = "Enter a number greater than 0 (a fraction of the conductor width, e.g. 0.15).";
                break;
            case nameof(EmMeshSettings.EdgeGrowthRatio):
                if (TryDouble(EdgeGrowthText, out double growth) && growth > 1) updated = m with { EdgeGrowthRatio = growth };
                else error = "Enter a number greater than 1.";
                break;
            case nameof(EmMeshSettings.TruncationHeights):
                if (TryDouble(TruncationHeightsText, out double trunc) && trunc > 0) updated = m with { TruncationHeights = trunc };
                else error = "Enter a number greater than 0 (in substrate heights).";
                break;
            case nameof(EmMeshSettings.TruncationTailCells):
                if (TryInt(TruncationTailCellsText, 1, out int tail)) updated = m with { TruncationTailCells = tail };
                else error = "Enter a whole number of 1 or more.";
                break;

            // D3's three planar controls share this one committer rather than growing a second: they
            // are the same kind of staged-text edit and the same undo entry, and two committers would
            // be two places to forget InvalidateMesh().
            case "CellsPerWavelength":
                if (TryInt(PlanarCellsPerWavelengthText, 1, out int cpw)) updatedPlanar = pm with { Auto = false, CellsPerWavelength = cpw };
                else error = "Enter a whole number of 1 or more.";
                break;
            case "PlanarEdgeCells":
                if (TryInt(PlanarEdgeCellsText, 0, out int pec)) updatedPlanar = pm with { Auto = false, EdgeCells = pec };
                else error = "Enter a whole number of 0 or more.";
                break;

            // M0 / R-emp-5 — the mesh frequency. Two things here are deliberate and easy to get
            // wrong: BLANK is a real value (null = max sweep), not "leave it alone"; and this
            // control does NOT clear Auto, unlike the two above it. Auto decides cells/λ and edge
            // cells — a resolution — and has no opinion about which frequency that resolution is
            // applied at. Clearing Auto here would silently pin the cell size the moment a user
            // touched a performance knob.
            case "MeshFrequency":
                if (PlanarMeshFrequencyText.Trim().Length == 0)
                    updatedPlanar = pm with { MeshFrequencyHz = null };
                else if (TryDouble(PlanarMeshFrequencyText, out double freq) && freq > 0)
                    updatedPlanar = pm with { MeshFrequencyHz = freq * ViewModels.FreqUnitHelper.Multiplier(MeshFrequencyUnit) };
                else
                    error = "Enter a positive frequency, or leave it blank to use the sweep's maximum.";
                break;
        }

        bool isPlanarField = field is "CellsPerWavelength" or "PlanarEdgeCells" or "MeshFrequency";
        if (isPlanarField) PlanarMeshFieldError = error; else MeshFieldError = error;

        // Bug report, 2026-08-14: an invalid commit used to fall through to RefreshMeshText(), which
        // silently overwrote the box back to the last-good value — the user's typed text vanished
        // with no explanation. It now stays exactly as typed, beside the message above, so there is
        // something to fix — matching how Port1Z0Error/Port2Z0Error already behave.
        if (error is not null) return;

        if (updated == m && updatedPlanar == pm) { RefreshMeshText(); return; }

        var before = SnapshotJson();
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

    /// <summary>The conformal-boundary-cells control. Deliberately NOT routed through
    /// <see cref="CommitMeshField"/> — that committer is for staged TEXT fields, and this is a
    /// closed choice that commits on selection, exactly like the edge-mesh checkbox above.
    ///
    /// <para><b><see cref="PlanarMeshSettings.Auto"/> is NOT cleared here, unlike every sibling.</b>
    /// The other three controls change how finely Auto's own sizing is applied, so setting one by
    /// hand means "stop deciding this for me". The boundary model is orthogonal to sizing: Auto has
    /// no opinion about whether a cell follows the metal, and <c>Resolved</c> carries it through
    /// rather than throwing it away. Clearing Auto here would silently pin the cell size the moment
    /// a user changed the boundary model, which is a different mesh for a reason they never
    /// asked for.</para></summary>
    partial void OnPlanarBoundaryCellsChanged(PlanarBoundaryCells value)
    {
        if (_suppressCommit) return;
        if (value == Working.PlanarMesh.BoundaryCells) return;
        var before = SnapshotJson();
        Working.PlanarMesh = Working.PlanarMesh with { BoundaryCells = value };
        CommitEdit(before, "Change planar boundary cells");
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
        // The description follows the SELECTION, not the commit — a suppressed or no-op change
        // still has to relabel the line under the dropdown.
        OnPropertyChanged(nameof(AnalysisKindDescription));

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

    partial void OnAcceleratedSolveChanged(bool value)
    {
        if (_suppressCommit) return;
        if (value == Working.AcceleratedSolve) return;
        var before = SnapshotJson();
        Working.AcceleratedSolve = value;
        CommitEdit(before, "Change accelerated solve");
        // Deliberately NO InvalidateMesh(): this chooses a SOLVER for a mesh, and the mesh it would
        // be solving is the same one either way. Every mesh control in this panel invalidates; this
        // one is not a mesh control, which is why it sits under Solver options beside the vertical
        // kernel and the core cap rather than in the Surface-mesh group.
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

    /// <summary>
    /// R13a, for M5's accelerator — why it is unavailable, or null when it is. <b>The multi-level
    /// case is a refusal the ENGINE already owns</b> (<c>PlanarSolve.SolveAt</c> throws by name when
    /// <c>Aim</c> is set on a problem needing the general kernel), so this is the panel declining to
    /// let a user arm a run that cannot start — never a second copy of the judgement. Asking
    /// <see cref="PlanarProblem.RequiresGeneralKernel"/> is asking the same question the engine asks.
    /// </summary>
    public string? AcceleratedSolveDisabledReason =>
        Working.AnalysisKind == EmAnalysisKind.CrossSection
            ? "The accelerated solve is part of the planar (full-wave) analysis; this setup uses the " +
              "cross-section kernel, whose solve is closed-form per frequency."
            : PlanarProblem is { } ap && ap.RequiresGeneralKernel
                ? "This layout needs the multi-level kernel (more than one metal level, or a via). " +
                  "The accelerator models the single-level horizontal basis family only — a via's " +
                  "vertical current needs its own grid kernel per height pairing, which is a separate " +
                  "piece of work."
                : null;

    // ── M1 — the solver's core cap: SHOWN here, STORED in AppPreferences (R-emp-6) ──────────────
    //
    // This is the one control in this panel that is NOT part of the design. A core count is a
    // property of the MACHINE, and a `.cem` travels with the workspace — opening a colleague's EM
    // setup must not pin your core count to theirs. So it carries no undo entry, does not dirty the
    // document, and does not call InvalidateMesh(): it cannot change a mesh, and R-emp-8 asserts it
    // cannot change an answer either. It is here because this is where the user is standing when the
    // cost lands.

    /// <summary>The machine's own choice list — Automatic, then powers of two up to the core count.</summary>
    public IReadOnlyList<EmSolveCoreChoice> SolveCoreChoices { get; } = EmSolveCores.ChoiceRows();

    [ObservableProperty] private EmSolveCoreChoice _selectedSolveCores =
        EmSolveCores.ChoiceRows().FirstOrDefault(c => c.Cap == EmSolveCores.Preferred)
        ?? EmSolveCores.ChoiceRows()[0];

    partial void OnSelectedSolveCoresChanged(EmSolveCoreChoice value)
    {
        // Written straight to the preference. No CommitEdit, deliberately — see the block comment
        // above; an undo stack that could revert a machine setting would be undoing the wrong thing.
        if (EmSolveCores.Preferred != value.Cap) EmSolveCores.Preferred = value.Cap;
    }

    /// <summary>The one-line note R-emp-6 asks for: this is a machine setting, not part of the design.</summary>
    public string SolveCoresNote =>
        $"A machine setting, not part of this design — it is not saved in the .cem, and it changes no " +
        $"answer. This machine reports {EmSolveCores.ProcessorCount} core(s).";

    // ── Refresh: extract, ask CanSolve, project the readback ───────────────────────────────────

    private void RebuildAll()
    {
        _suppressCommit = true;
        Port1Z0Text = FormatComplexOhms(Working.Port1Z0);
        Port2Z0Text = FormatComplexOhms(Working.Port2Z0);
        DispersionCorrection = Working.DispersionCorrection;
        AdaptiveSampling     = Working.AdaptiveSampling;
        DirectVerticalKernel = Working.DirectVerticalKernel;
        AcceleratedSolve     = Working.AcceleratedSolve;
        AnalysisKind = Working.AnalysisKind;
        SignalLayerChoice = Working.SignalStackupLayerName is { Length: > 0 } s ? s : InferSignalLayer;
        SnpOutputPathText = Working.SnpOutputPathOverride;
        OnPropertyChanged(nameof(SnpOutputPlaceholder));
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
        PlanarBoundaryCells          = pm.BoundaryCells;
        PlanarMeshFrequencyText      = pm.MeshFrequencyHz is { } mf
            ? (mf / ViewModels.FreqUnitHelper.Multiplier(MeshFrequencyUnit))
                .ToString("G6", CultureInfo.InvariantCulture)
            : "";
        _suppressCommit = false;
        OnPropertyChanged(nameof(MeshFrequencyUnit));
        OnPropertyChanged(nameof(MeshFrequencyPlaceholder));
    }

    /// <summary>Re-resolve the layout, re-extract, re-ask the kernel. Cheap enough to run on every
    /// settings change, which is exactly what R-em-13 asks for.</summary>
    public void Refresh()
    {
        Problem            = null;
        PlanarProblem      = null;
        OnPropertyChanged(nameof(DirectVerticalKernelDisabledReason));
        OnPropertyChanged(nameof(AcceleratedSolveDisabledReason));
        OnPropertyChanged(nameof(AdaptiveSamplingDisabledReason));
        Readback           = null;
        ExtractionRefusal  = null;
        KernelRefusal      = null;
        PlanarExtractionRefusal = null;
        PortRefusal        = null;
        PlanarPorts        = [];
        InternalPortMarkAnchors = [];
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

        // Same two fields PreparePlanarMesh sets, reused here for BuildMesh's own (kernel A) length
        // formatter — Refresh() runs entirely on the UI thread, so this could read source.View
        // directly at the point of use, but keeping ONE place these are captured is what keeps the
        // two mesh paths from drifting onto two different ideas of "the current display unit".
        _pendingDisplayUnit  = source.View.DisplayUnit;
        _pendingDbuPerMicron = source.DbuPerMicron;

        // ── R-res-1: the registry chooses, here as at run time, from the same two verdicts ─────
        double fMax = TryMaxFrequency();

        // Flattened, not source.View.Shapes: a schematic-generated layout carries every piece of
        // metal inside a placed instance and nothing at top level (owner report, 2026-08-09).
        var geometry = EmGeometry.Flatten(source.View, source.AbsolutePath);
        _geometryNotes = geometry.Notes;

        var crossSection = CrossSectionExtractor.Extract(
            geometry.Shapes, source.Technology, source.DbuPerMicron,
            Working.ToExtractionSettings(Working.LayoutRef));

        var planar = PlanarExtractor.Extract(
            geometry.Shapes, source.Technology, source.DbuPerMicron, fMax,
            Working.ToExtractionSettings(Working.LayoutRef), geometry.GeneratorIds);

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

        Notes = [.. _geometryNotes, .. crossSection.Notes];

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
        InternalPortMarkAnchors = [];
        DispersionDisabledReason =
            "The Kirschning–Jansen correction belongs to the cross-section analysis, where it is " +
            "applied on top of a quasi-static answer. A full-wave planar solve has dispersion in " +
            "the solve itself, so there is nothing to bolt on.";

        if (!choice.Ok)
        {
            PlanarExtractionRefusal = choice.Refusal;
            RaiseState();
            return;
        }

        PlanarProblem = planar.Problem;
        OnPropertyChanged(nameof(DirectVerticalKernelDisabledReason));
        OnPropertyChanged(nameof(AcceleratedSolveDisabledReason));
        OnPropertyChanged(nameof(AdaptiveSamplingDisabledReason));

        var verdict = new PlanarKernel().CanSolve(planar.Problem!);
        KernelRefusal = verdict.Ok ? null : verdict.Reason;

        var ports = EmPortExtraction.Extract(
            source.View.Shapes, planar.Problem!, source.DbuPerMicron, Working.ResolvePortZ0,
            source.View.DisplayUnit, Working.ResolvePortKind,
            EmPortExtraction.DefaultGroundPathWidthM(source.Technology));

        PortRefusal = ports.Ok ? null : ports.Refusal;
        PlanarPorts = ports.Ports;

        // ── THE MARKS AND THE ROWS COME FROM `Rows`, WHICH SURVIVES A REFUSAL ────────────────
        //
        // Owner reports, 2026-08-25: "if any ports aren't touching metal, the .cem editor will not
        // list the ports", and "P3 renders as edge port (even though it is a gap port) when P2 is
        // not on a conductor." One cause: both of these read `ports.Ports`, which is empty on any
        // refusal — so ONE bad label emptied the panel's port list AND silently retyped every
        // internal port in the layout back to an edge port, neither of which is true of the ports
        // the user actually drew. `Rows` reports every numbered label whether it resolved or not.
        //
        // The KIND comes from the .cem (`ResolvePortKind`) rather than from the resolved port,
        // because that is where it lives and because an unresolved row has no port to ask. For a
        // resolved row the two are the same value by construction — `kindFor` above IS this
        // function — so this is not a second opinion, it is the only one.
        var anchors = new List<(long X, long Y, PlanarPortKind Kind)>();
        for (int i = 0; i < ports.Rows.Count; i++)
        {
            var kind = Working.ResolvePortKind(i);
            if (kind != PlanarPortKind.Edge)
                anchors.Add((ports.Rows[i].Label.X, ports.Rows[i].Label.Y, kind));
        }
        InternalPortMarkAnchors = anchors;

        var notes = new List<string>(_geometryNotes);
        notes.AddRange(planar.Notes);
        notes.AddRange(ports.Notes);
        Notes = [.. notes];

        RebuildPlanarPortRows(ports.Rows);
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
    private void RebuildPlanarPortRows(IReadOnlyList<EmPortRow> ports)
    {
        PortRows.Clear();
        for (int i = 0; i < ports.Count; i++)
        {
            var kind = Working.ResolvePortKind(i);
            var port = ports[i].Port;
            var row = new EmPortZ0Row
            {
                PortNumber = ports[i].Number,
                // An internal gap has no "end" — it is a cut in the middle of the metal, and the
                // side only says which way its current is positive. An internal port has neither: its
                // terminals are the metal and the ground plane, so naming a direction at all would
                // be the panel inventing one. Labelling either as an end would be the panel
                // contradicting the run's own notes.
                //
                // An UNRESOLVED port names no side at all: the side is inferred from the conductor,
                // and there isn't one. It says what it is instead, with the reason carried in
                // `Problem` below — a row captioned "low-x end" for a label sitting on bare
                // dielectric would be the panel asserting the very thing the refusal denies.
                Label      = port is null
                    ? $"Port {ports[i].Number} — not resolved"
                    : kind switch
                    {
                        PlanarPortKind.InternalDeltaGap =>
                            $"Port {ports[i].Number} — gap, current {SideLabel(port.Side)}",
                        PlanarPortKind.Internal =>
                            $"Port {ports[i].Number} — internal, to ground",
                        _ => $"Port {ports[i].Number} — {SideLabel(port.Side)} end",
                    },
                Text       = FormatComplexOhms(Working.ResolvePortZ0(i)),
                ShowKind   = true,
                Kind       = kind,
                Problem    = ports[i].Problem,
            };
            // Wired AFTER construction: the initialiser above seeds Kind from what is already
            // stored, and a seed must not commit an edit or push an undo entry.
            int index = i;
            row.KindChanged += _ => CommitPortKind(index);
            PortRows.Add(row);
        }
        OnPropertyChanged(nameof(ShowPortList));
        OnPropertyChanged(nameof(ShowNearFarPortZ0));
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
        // ── THIS GUARD CLEARED THE PLANAR PORT LIST THE INSTANT IT WAS BUILT ──────────────────
        //
        // It exists to drop stale rows when an extraction fails, and it asked only about the
        // CROSS-SECTION problem. A planar refresh leaves `Problem` null by construction — its
        // problem is `PlanarProblem` — so `RebuildPlanarPortRows` filled the list, RaiseState ran
        // one line later, and emptied it again. **The per-port reference-impedance list has
        // therefore never once appeared for a full-wave setup**, from L8e until now: `ShowPortList`
        // is true for a planar analysis, and there was nothing in it to show. Found while wiring the
        // port-type control into the same rows, and it had no test because every existing PortRows
        // test drives the cross-section kernel.
        //
        // Both problems have to be null for the rows to be stale — which is what "no extraction
        // succeeded" actually means now that there are two extractors.
        if (Problem is null && PlanarProblem is null && PortRows.Count > 0) RebuildPortRows(null);
        OnPropertyChanged(nameof(InternalPortOnTheWrongKernel));
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
        OnPropertyChanged(nameof(AcceleratedSolveDisabledReason));
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
    public void BuildPlanarMesh() => BuildPlanarMesh(null);

    /// <param name="control">Progress and cancellation for the mesher, or null for neither.</param>
    public void BuildPlanarMesh(RunControl? control)
    {
        if (PreparePlanarMesh() is not { } problem) return;
        AdoptPlanarMeshReport(ComputePlanarMesh(problem, control));
    }

    /// <summary>
    /// The UI-THREAD half of a planar mesh: resolve the layout, flatten it, extract the problem, and
    /// assign every piece of view-model state that follows from those. Returns the problem to mesh,
    /// or null when there is nothing to mesh — in which case the state it just wrote already says why.
    ///
    /// <para><b>This split exists because of a real crash, and the boundary is where it is on purpose</b>
    /// (owner report, 2026-08-09: <i>"I pressed the mesh button but got: the calling thread cannot
    /// access this object because a different thread owns it"</i>). The Mesh button had been moved
    /// onto the thread pool wholesale — but this method does far more than mesh: it writes
    /// <see cref="PlanarMeshNotes"/>, <see cref="PlanarProblem"/> and friends, every one of which
    /// raises <c>PropertyChanged</c> straight into bound Avalonia controls, and it fires
    /// <see cref="AnalysisRefreshed"/>, which the workspace turns into opening a layout document.
    /// None of that may happen off the UI thread.</para>
    ///
    /// <para><b>Flatten and extract stay HERE rather than joining the mesher on the pool, and that is
    /// deliberate rather than laziness.</b> They read <c>source.View</c> — the LIVE
    /// <see cref="LayoutView"/> of an open layout document, which the user can be editing. Reading it
    /// from a background thread would be a data race, which is a worse bug than a button that is
    /// briefly slow. Only <see cref="SurfaceMesher.Mesh"/> is offloaded, because it works on the
    /// already-extracted <see cref="PlanarProblem"/> — a snapshot nothing else can mutate.</para>
    /// </summary>
    public PlanarProblem? PreparePlanarMesh()
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
            // (display unit / DBU-per-micron are left at their prior value here — nothing was
            // resolved, so ComputePlanarMesh is never reached for this attempt)
            RaiseState();   // PlanarExtractionRefusal feeds BlockingReason/CanRun — see AdoptPlanarMeshReport's note
            return null;
        }

        // Held so ComputePlanarMesh — run off the UI thread — can build the length formatter without
        // touching the live LayoutView itself: these are plain value types, unlike source.View, so
        // copying them here carries none of PreparePlanarMesh's own data-race concern (see its header).
        _pendingDisplayUnit  = source.View.DisplayUnit;
        _pendingDbuPerMicron = source.DbuPerMicron;

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

        var meshGeometry = EmGeometry.Flatten(source.View, source.AbsolutePath);
        var extraction = PlanarExtractor.Extract(
            meshGeometry.Shapes, source.Technology, source.DbuPerMicron, fMax, Working.ToExtractionSettings(),
            meshGeometry.GeneratorIds);

        PlanarMeshNotes = [.. meshGeometry.Notes, .. extraction.Notes];
        if (!extraction.Ok)
        {
            PlanarExtractionRefusal = extraction.Refusal;
            OnPropertyChanged(nameof(PlanarMeshSummary));
            RaiseState();   // PlanarExtractionRefusal feeds BlockingReason/CanRun — see AdoptPlanarMeshReport's note
            return null;
        }

        PlanarProblem = extraction.Problem;
        // Held so AdoptPlanarMeshReport can prepend them to the mesher's own — the notes are built in
        // two places and must read in one order.
        _pendingPlanarNotes = [.. meshGeometry.Notes, .. extraction.Notes];
        // ── The mesh PREVIEW is of the problem that will be SOLVED, vias included ───────────────
        //
        // An internal port with no via drawn under it grows its own path to the plane before meshing
        // (PlanarGroundPath), so a preview of the bare extraction would be a picture of a structure
        // the run does not solve — and, worse, the port's own mark would have no footprint to
        // measure and would silently stay at its glyph size. The ports here are the ones the last
        // refresh resolved; with none, this is exactly the old behaviour.
        return PlanarPorts.Count > 0
            ? PlanarGroundPath.Extend(extraction.Problem!, PlanarPorts).Problem
            : extraction.Problem;
    }

    private IReadOnlyList<string> _pendingPlanarNotes = [];

    // The length-formatter's own two inputs, captured by PreparePlanarMesh (see its own comment
    // there) — plain value types, so reading them from ComputePlanarMesh's background thread carries
    // none of the live-LayoutView data race PreparePlanarMesh's own split exists to avoid.
    private LayoutUnit _pendingDisplayUnit  = LayoutUnit.Um;
    private int        _pendingDbuPerMicron = LayoutUnits.DefaultDbuPerMicron;

    /// <summary>The POOLABLE half: pure, and touches no view-model state. Safe on any thread because
    /// <paramref name="problem"/> is an already-extracted snapshot.</summary>
    public PlanarMeshReport ComputePlanarMesh(PlanarProblem problem, RunControl? control)
        => SurfaceMesher.Mesh(problem, Working.PlanarMesh, PlanarEdgeReference.ConductorWidth, control,
                              accelerated: Working.AcceleratedSolve,
                              lengthFormat: EmLengthFormat.For(_pendingDisplayUnit, _pendingDbuPerMicron));

    /// <summary>The UI-THREAD half again: adopt the report and everything that follows from it.</summary>
    public void AdoptPlanarMeshReport(PlanarMeshReport report)
    {
        PlanarMeshReport = report;

        // R-em-16, unchanged for kernel B: surface the engine's own notes VERBATIM. The mesher wrote
        // the λ_g sentence, the staircasing sentence and the R-msh-8a analytic-model sentence
        // carefully; print them, do not re-word them.
        var notes = new List<string>(_pendingPlanarNotes);
        notes.AddRange(report.Notes);
        PlanarMeshNotes = [.. notes];

        OnPropertyChanged(nameof(PlanarMeshSummary));

        // Bug report, 2026-08-14: "Simulate button was disabled once when it should have been
        // enabled. Had to change a parameter for it to update. (After clicking Mesh)." CanRun's
        // planar branch folds in PlanarBudgetRefusal (R17's ceiling verdict), and this is the ONLY
        // place that becomes known — it comes back WITH the mesh, not with extraction. The bare
        // OnPropertyChanged(PlanarBudgetRefusal) this used to end on told the panel's disabled-reason
        // TEXT to refresh, but nothing told SimulateCommand its CanExecute might have flipped, so a
        // mesh that newly cleared (or newly hit) the budget left the button showing its PRE-mesh
        // state until an unrelated field edit called RaiseState() for an unrelated reason.
        RaiseState();
    }

    /// <summary>
    /// The header Mesh button: mesh whichever kernel this setup is ACTUALLY going to use.
    ///
    /// <para><b>Owner report, 2026-08-09: "I pressed Mesh for my EM Setup (full wave) but nothing
    /// happened and no messages were displayed."</b> The header button was bound straight to
    /// <see cref="BuildMesh"/> — the CROSS-SECTION mesher — whose second line is
    /// <c>if (Problem is null) return;</c>. On a full-wave setup <c>Problem</c> is null by
    /// construction (the planar problem lives in <see cref="PlanarProblem"/>), so the most prominent
    /// button in the editor returned silently. Meanwhile the planar mesher sat on a SECOND button,
    /// also labelled "Mesh", buried inside the Surface mesh group. Two identical labels, one of them
    /// inert in the mode the user was in.</para>
    ///
    /// <para>Dispatch after <see cref="Refresh"/>, never before: the refresh is what settles which
    /// kernel the registry chose, and therefore which mesher this button means.</para>
    /// </summary>
    /// <summary>
    /// Dispatches to the host's background mesh when one is wired (progress row + Cancel button), and
    /// meshes inline otherwise — so every headless caller and every existing test keeps the exact
    /// synchronous behaviour it had.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMesh))]
    public async Task BuildActiveMesh()
    {
        if (MeshRequested is { } mesh) await mesh(this);
        else BuildActiveMesh(null);
    }

    private bool CanMesh() => !IsBusy;

    /// <param name="control">Progress and cancellation, or null. The host supplies one when it runs
    /// this off the UI thread; a headless caller passes none and meshes inline exactly as before.</param>
    public void BuildActiveMesh(RunControl? control)
    {
        Refresh();
        if (IsPlanarAnalysis) BuildPlanarMesh(control);
        else BuildMesh();
    }

    /// <summary>R-em-14: the Mesh button calls <see cref="IEmKernel.Mesh"/> and nothing else. No
    /// solve, no RLGC, no s-parameters — the cheap "is my mesh sane?" answer §10.5 says should land
    /// before the solver, and it must stay cheap enough to press repeatedly.
    ///
    /// <para>Reached through <see cref="BuildActiveMesh"/> for the cross-section kernel. A null
    /// <see cref="Problem"/> here is REPORTED rather than returned from silently — that silence is
    /// exactly what made the full-wave case above read as a dead button.</para></summary>
    [RelayCommand]
    public void BuildMesh()
    {
        Refresh();
        if (Problem is null)
        {
            MeshReport = null;
            MeshNotes  = [ExtractionRefusal ?? KernelRefusal ??
                "There is no cross-section to mesh yet. The panel above says why the geometry has " +
                "not resolved; fix that and press Mesh again."];
            AnalysisRefreshed?.Invoke();
            return;
        }

        var report = new QuasiStaticKernel().Mesh(Problem, Working.Mesh,
            EmLengthFormat.For(_pendingDisplayUnit, _pendingDbuPerMicron));
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

    /// <summary>
    /// Stops the run in flight (owner request, 2026-08-09: "we need a Stop simulation for EM, just
    /// like we have for circuit simulation. Perhaps the Simulate button changes to Cancel?").
    ///
    /// <para><b>Cancellation lands at a work boundary, never inside a solve</b> — the same contract
    /// <c>RunControl</c> already states for the circuit engines. A full-wave point is checked between
    /// the Green's-function fit, the structure solve and each calibration standard, so Cancel is
    /// answered within one of those rather than instantly. Finer would mean a token check inside the
    /// numerical loops, which is exactly where this engine cannot afford one.</para>
    ///
    /// <para><b>A cancelled run writes nothing.</b> Every file this run would produce is written
    /// after the solve it belongs to, so abandoning the solve abandons the write by construction —
    /// there is no half-written <c>.snp</c> to clean up.</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCancelSimulate))]
    public void CancelSimulate() => CancelRequested?.Invoke();

    private bool CanCancelSimulate() => IsRunning && !IsCancelling;

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

    /// <summary>
    /// The DBU anchors of the port labels this setup drives as INTERNAL DELTA GAPS — what the layout
    /// editor needs in order to draw the right mark, since the type lives here and not on the shape.
    ///
    /// <para><b>Populated on every refresh, not only after a run</b>, unlike
    /// <see cref="ReferencePlanes"/>: a reference plane is a location the ENGINE reports and does not
    /// exist until something is solved, while a port's type is a decision the user has just made and
    /// has to see immediately. Anchors rather than port numbers — see
    /// <c>LayoutRenderOptions.InternalPortMarks</c> for why.</para>
    /// </summary>
    [ObservableProperty] private IReadOnlyList<(long X, long Y, PlanarPortKind Kind)> _internalPortMarkAnchors = [];

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
        OnPropertyChanged(nameof(AnalysisLevelsSummary));
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
        OnPropertyChanged(nameof(AnalysisLevelsSummary));
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

    /// <summary>
    /// Whether this row offers a port TYPE at all. <b>Planar rows only</b> — the cross-section
    /// kernel's ports are the two ends of a uniform line by construction, so there is nothing an
    /// internal gap could mean there and offering the choice would be offering a setting that does
    /// nothing.
    /// </summary>
    public bool ShowKind { get; init; }

    /// <summary>The two port types, from the enum rather than hand-listed, so a third cannot
    /// silently fail to appear in the panel — the same rule <c>BoundaryCellsChoices</c> follows.</summary>
    public static IReadOnlyList<PlanarPortKind> KindChoices { get; } = Enum.GetValues<PlanarPortKind>();

    [ObservableProperty] private string  _text = "50";
    [ObservableProperty] private string? _error;

    /// <summary>
    /// Why this port could not be resolved from the geometry, or null when it was. <b>Separate from
    /// <see cref="Error"/>, which is about the TEXT in the impedance box</b> — the two have
    /// different lifetimes and would otherwise clobber each other: a successful impedance commit
    /// clears <c>Error</c>, and it must not thereby erase "this port is not on any conductor",
    /// which is still true and is not the user's typing.
    /// </summary>
    public string? Problem { get; init; }

    /// <summary>True when this row has anything to warn about, from either source.</summary>
    public bool HasProblem => Problem is not null;

    /// <summary>
    /// Edge or internal delta gap. <b>Raised through <see cref="KindChanged"/> rather than committed
    /// here</b>, because a row knows nothing about the document, the undo stack or which index it
    /// is — the panel owns all three, exactly as it does for the impedance text beside it.
    /// </summary>
    [ObservableProperty] private PlanarPortKind _kind = PlanarPortKind.Edge;

    /// <summary>Raised when the user picks a different type. Wired by the panel AFTER the row is
    /// constructed, so seeding <see cref="Kind"/> from the stored setup raises nothing.</summary>
    public event Action<EmPortZ0Row>? KindChanged;

    public bool HasError => Error is not null;

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnKindChanged(PlanarPortKind value) => KindChanged?.Invoke(this);
}
