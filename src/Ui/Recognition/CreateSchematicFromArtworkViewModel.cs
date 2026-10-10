// Design ▸ Create Schematic from Artwork… — the artwork source. brief-artsch-8-gui-command.md R-as8-2 … R-as8-4;
// overview D2, D4, D10, D11, D15, D16; brief-img-5-dialog.md R-im5-1.
//
// The body below the source — target, options, parts table, report, Create — is RecognitionSessionViewModel's, shared
// with the picture source. What is here is only what the artwork adds: the layout on disk, its scope (D11), its
// companion files, the cross-probe onto the layout, and the entry point that writes (IArtworkRecognitionRunner — the
// function `circuitrf recognize` calls).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Engine;

namespace CircuitRF.Ui.Recognition;

/// <summary>The dialog, on an artwork source.</summary>
public sealed partial class CreateSchematicFromArtworkViewModel : RecognitionSessionViewModel
{
    private readonly RecognitionInput _base;
    private readonly IArtworkRecognitionRunner _runner;
    private readonly IReadOnlyList<long[]> _selection;

    /// <param name="baseInput">The layout as the CLI reads it (<see cref="IArtworkRecognitionRunner.Load"/>); its
    /// <see cref="RecognitionInput.ClayPath"/> must be set — a scratch layout never reaches here.</param>
    /// <param name="selectionRings">The layout editor's selection outline, DBU; empty when nothing is selected.</param>
    /// <param name="runner">The recognition; the real one when null.</param>
    /// <param name="post">How a result from the background reaches the UI thread; immediate when null.</param>
    /// <param name="debounce">How long a burst of changes is let settle before recognising.</param>
    public CreateSchematicFromArtworkViewModel(
        RecognitionInput baseInput, IReadOnlyList<long[]> selectionRings,
        IArtworkRecognitionRunner? runner = null, Action<Action>? post = null, TimeSpan? debounce = null)
        : base((baseInput ?? throw new ArgumentNullException(nameof(baseInput))).EmSetup, post, debounce)
    {
        _base = baseInput;
        ClayPath = Path.GetFullPath(baseInput.ClayPath ?? throw new ArgumentException("The layout must be saved.", nameof(baseInput)));
        _runner = runner ?? ArtworkRecognitionRunner.Instance;
        _selection = selectionRings ?? [];

        ArtworkCellName = Path.GetFileName(RecognitionTarget.CellOf(ClayPath) ?? Path.GetFileNameWithoutExtension(ClayPath));
        ArtworkCellOffered = RecognitionTarget.ArtworkCellOffered(ClayPath);
        SelectionAvailable = RecognitionScope.Polygons(_selection) is { IsWhole: false };
        _scopeSelection = SelectionAvailable;
        EndConstruction(RecognitionTarget.DefaultCellName(ClayPath), targetOwn: false);
    }

    // ── the artwork ──────────────────────────────────────────────────────────────────────────────────

    public string ClayPath { get; }
    public string ArtworkCellName { get; }
    public override string Title => $"Create Schematic from Artwork — {ArtworkCellName}";

    protected override EmSetup? EmSetupForSweep => _base.EmSetup;

    // ── target (R-as8-2, D4) ─────────────────────────────────────────────────────────────────────────

    /// <summary>Whether <i>This cell's schematic</i> is offered: the artwork cell has no schematic view (D4).</summary>
    public bool ArtworkCellOffered { get; }

    public override bool OwnTargetOffered => ArtworkCellOffered;
    public override string OwnTargetLabel => "This cell's schematic";
    public override string OwnTargetTip => "Write the schematic into the artwork's own cell, which has none yet, to check and clean up there.";
    public override string NewCellTip => "A new cell beside the artwork's, holding a schematic only.";

    public bool TargetArtworkCell
    {
        get => TargetOwn;
        set => TargetOwn = value;
    }

    protected override void OnTargetChanged() => OnPropertyChanged(nameof(TargetArtworkCell));

    protected override string NewCellDir(string name) => RecognitionTarget.NewCellDir(ClayPath, name);
    protected override bool IsReplaceable(string cellDir) => RecognitionTarget.IsReplaceable(cellDir);
    protected override string NotReplaceableText(string name) =>
        $"A cell named '{name}' already exists and was not created from artwork; choose another name.";

    /// <summary>The target as the run takes it.</summary>
    public RecognitionTarget Target =>
        TargetArtworkCell ? RecognitionTarget.ArtworkCell
        : IsReplace ? RecognitionTarget.Replace(RecognitionTarget.NewCellDir(ClayPath, NewCellName.Trim()))
        : RecognitionTarget.NewCell(NewCellName.Trim());

    // ── scope (D11) ──────────────────────────────────────────────────────────────────────────────────

    public bool SelectionAvailable { get; }
    [ObservableProperty] private bool _scopeSelection;

    public bool ScopeWhole
    {
        get => !ScopeSelection;
        set => ScopeSelection = !value;
    }

    partial void OnScopeSelectionChanged(bool value)
    {
        OnPropertyChanged(nameof(ScopeWhole));
        ScheduleRecognition();
    }

    // ── companion files ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The three origins, as railRF's import dialog lists and spells them — the same control (R-as8-2).</summary>
    public static IReadOnlyList<(PlacementOrigin Origin, string Label)> PlacementOrigins { get; } =
    [
        (PlacementOrigin.SymbolOrigin, "the footprint's symbol origin"),
        (PlacementOrigin.BodyCentre,   "the part body's centre"),
        (PlacementOrigin.PinOne,       "pin 1"),
    ];

    public static IReadOnlyList<string> PlacementOriginLabels { get; } = [.. PlacementOrigins.Select(o => o.Label)];

    [ObservableProperty] private string _bomPath = "";
    [ObservableProperty] private string _placementPath = "";

    /// <summary>The origin choice: −1 is NOTHING CHOSEN, which is where it starts — never a guess (railRF's rule).</summary>
    [ObservableProperty] private int _placementOriginIndex = -1;

    [ObservableProperty] private bool _placementNeedsOrigin;
    [ObservableProperty] private string _companionError = "";

    private BomTable? _bom;
    private PlacementTable? _placement;

    /// <summary>Set by the window: a file picker for the BOM… and Placement… buttons.</summary>
    public Func<string, Task<string?>>? PickFileAsync { get; set; }

    [RelayCommand]
    private async Task PickBom()
    {
        if (PickFileAsync is { } pick && await pick("Bill of materials") is { } path) BomPath = path;
    }

    [RelayCommand]
    private async Task PickPlacement()
    {
        if (PickFileAsync is { } pick && await pick("Placement file") is { } path) PlacementPath = path;
    }

    partial void OnBomPathChanged(string value)
    {
        _bom = value.Trim().Length == 0 ? null : RecognitionBom.ReadFile(value.Trim());
        RefreshCompanionError();
        ScheduleRecognition();
    }

    partial void OnPlacementPathChanged(string value)
    {
        PlacementOriginIndex = -1;
        ReadPlacement();
        ScheduleRecognition();
    }

    partial void OnPlacementOriginIndexChanged(int value)
    {
        ReadPlacement();
        ScheduleRecognition();
    }

    private void ReadPlacement()
    {
        string path = PlacementPath.Trim();
        PlacementOrigin? origin = PlacementOriginIndex >= 0 && PlacementOriginIndex < PlacementOrigins.Count
            ? PlacementOrigins[PlacementOriginIndex].Origin : null;
        // Whether the FILE states its origin, read with none given — so the choice stays on screen once made.
        var stated = path.Length == 0 ? null : PlacementFile.ReadFile(path, _base.View.DbuPerMicron);
        PlacementNeedsOrigin = stated is { OriginEvidence: PlacementOriginEvidence.Unstated };
        _placement = PlacementNeedsOrigin && origin is not null
            ? PlacementFile.ReadFile(path, _base.View.DbuPerMicron, origin)
            : stated;
        RefreshCompanionError();
    }

    private void RefreshCompanionError()
    {
        var errors = new List<string>();
        if (BomPath.Trim().Length > 0 && _bom is null) errors.Add($"'{Path.GetFileName(BomPath.Trim())}' could not be read.");
        if (PlacementPath.Trim().Length > 0 && _placement is null) errors.Add($"'{Path.GetFileName(PlacementPath.Trim())}' could not be read.");
        CompanionError = string.Join(" ", errors);
    }

    /// <summary>Why recognition is waiting for the user, or null. An unstated placement origin is asked, never guessed.</summary>
    public override string? Blocked
    {
        get
        {
            if (PlacementNeedsOrigin && PlacementOriginIndex < 0)
                return $"'{Path.GetFileName(PlacementPath.Trim())}' does not state its coordinate origin; choose it to recognise.";
            if (CompanionError.Length > 0) return CompanionError;
            return base.Blocked;
        }
    }

    // ── the cross-probe onto the layout (R-as8-3) ────────────────────────────────────────────────────

    /// <summary>Set by the window: draws a part on the layout (rings and what to bring on screen), or clears it (null).</summary>
    public Action<IReadOnlyList<long[]>?, Bbox>? PartHighlighted { get; set; }

    protected override void OnRowSelected(PartsTableRowViewModel? row)
    {
        if (row is null) { PartHighlighted?.Invoke(null, Bbox.Empty); return; }
        var rings = ArtworkCrossProbe.PartRings(row.Row, _placement is { Refusal: null } p ? p : null);
        PartHighlighted?.Invoke(rings, ArtworkCrossProbe.Extent(rings));
    }

    // ── the read and the write (D2) ──────────────────────────────────────────────────────────────────

    /// <summary>The input the run is handed — the CLI's, field for field (D2).</summary>
    public RecognitionInput BuildInput() => _base with
    {
        Scope = ScopeSelection && SelectionAvailable ? RecognitionScope.Polygons(_selection) : RecognitionScope.Whole,
        Options = BuildOptions(_base.Options),
        Placement = _placement,
        Bom = _bom,
        PartsCsvText = PartsCsvText,
    };

    protected override Func<RunControl, RecognitionPreview> BeginPreview()
    {
        var input = BuildInput();
        var emit = BuildEmit();
        return control =>
        {
            var (result, _) = _runner.Preview(input, emit, control);
            return new RecognitionPreview(result.Parts, result.Report, result.Refusal, result);
        };
    }

    /// <summary>Raised on the UI thread when a schematic was written.</summary>
    public event Action<RecognitionRun>? Created;

    protected override CreateJob BeginCreate()
    {
        var input = BuildInput();
        var target = Target;
        var options = new RecognitionRunOptions { Emit = BuildEmit() };
        return new CreateJob(
            TargetArtworkCell ? $"Writing {ArtworkCellName}'s schematic…" : $"Writing {NewCellName.Trim()}…",
            () =>
            {
                var run = _runner.Run(input, target, options, null);
                return new CreateOutcome(run.Refusal, run.Report, () => Created?.Invoke(run));
            },
            "The schematic could not be created");
    }

    /// <summary>Closing the dialog: stops a recognition in flight and takes the part off the layout.</summary>
    public override void Close()
    {
        base.Close();
        PartHighlighted?.Invoke(null, Bbox.Empty);
    }
}
