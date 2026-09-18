using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// VM for one row in the .ctech editor's stackup CARD LIST — the lower of the Stackup tab's two
/// panes, below the cross-section drawing (docs/design/mom-engine.md §10.4a; §10 moved out of
/// layout-view.md with its numbering preserved).
///
/// <para><b>The card list is one of TWO views of the same stackup, and this VM is the write path
/// for both.</b> The drawing above it does not mutate <c>Working.Stackup</c> and pushes no undo
/// entry of its own: a band dragged, a value typed on the canvas or a menu item picked there all
/// arrive at <see cref="CommitName"/> / <see cref="CommitThickness"/> / the owner's
/// <c>MoveStackupLayer</c> / <c>RemoveStackupLayer</c> / <c>CommitEdit</c>, which is why undo/redo,
/// <c>TechValidation</c>, the dirty mark and the live-technology push need no second copy to stay in
/// step. A comment-stripped source scan holds it.</para>
///
/// <para>The detail pane shows only
/// the fields <see cref="Kind"/> actually uses (§2.4's rule): a dielectric never shows σ, a
/// conductor/via never shows εr/tanδ/µr. Thickness is a physical dimension, parsed/formatted via
/// <see cref="LayoutUnits"/> in the technology's <see cref="Technology.DefaultDisplayUnit"/> —
/// never a hand-rolled number parser. Drawing-layer selection is a closed set against the current
/// layer table (<see cref="DrawingLayerOptions"/>), not free text, so it is impossible in the UI
/// to name a layer that doesn't exist.
///
/// <b>Cardinality per §10.4 is NOT uniform across kinds.</b> A conductor is explicitly "bound to
/// one or more drawing layers" (e.g. a plane split/repeated across several drawn layer numbers) —
/// multi-select is correct there. A via is "bound to a drawing layer" (singular) and a dielectric
/// slab likewise corresponds to at most one outline/extent layer — for both, checking a new layer
/// in <see cref="SetDrawingLayerChecked"/> clears any previous selection instead of adding to it,
/// so the UI enforces the same one-drawing-layer invariant the model already implies.
/// </summary>
public sealed partial class StackupLayerRowViewModel : ObservableObject
{

    /// <summary>
    /// The culture the staged numeric strings in this row are both formatted with and parsed with.
    /// Invariant on purpose — see <see cref="RefreshFromModel"/>.
    /// </summary>
    private static readonly System.Globalization.CultureInfo Inv =
        System.Globalization.CultureInfo.InvariantCulture;
    private readonly TechEditorViewModel _owner;
    private bool _isRefreshing;

    internal StackupLayer Layer { get; }

    public StackupKind Kind => Layer.Kind;
    public bool IsDielectric => Kind == StackupKind.Dielectric;
    public bool IsConductor  => Kind == StackupKind.Conductor;
    public bool IsVia        => Kind == StackupKind.Via;

    /// <summary>Only a conductor may bind more than one drawing layer (§10.4).</summary>
    public bool AllowMultipleDrawingLayers => Kind == StackupKind.Conductor;

    /// <summary>The complement — the kinds that get a plain ComboBox instead of a checkbox list.
    /// Still true for a dielectric, because the CARDINALITY rule is unchanged: if a dielectric ever
    /// carries a binding (a shipped file may, and they round-trip untouched) it carries at most one.
    /// What changed is that the editor no longer OFFERS one — see
    /// <see cref="ShowsDrawingLayerPicker"/>.</summary>
    public bool IsSingleDrawingLayer => !AllowMultipleDrawingLayers;

    /// <summary>
    /// Whether the row shows a drawing-layer picker at all. <b>A dielectric no longer does.</b>
    ///
    /// <para>The binding never placed the slab — <c>PlanarExtractor.BuildMediumStack</c> reads only
    /// εr/tanδ/µr/thickness and every dielectric is laterally infinite — so the field asked the user
    /// for a physical fact it did not consume. Its ONE real effect was to stop
    /// <c>CrossSectionExtractor</c> refusing on artwork drawn over the slab (an MMIC die outline),
    /// and that refusal has since been fixed at its source: a layer the technology declares but
    /// binds to no stackup entry is now ignored with a note, exactly as silk, soldermask and the
    /// board outline always should have been. With the workaround unnecessary, the control is gone.
    /// User-proposed, 2026-08-30.</para>
    ///
    /// <para>The MODEL field stays. Shipped and user <c>.ctech</c> files carrying a dielectric
    /// binding still parse, still validate, and still round-trip through
    /// <see cref="TechnologyMerge"/> unchanged — removing the control must not rewrite anyone's
    /// file.</para>
    /// </summary>
    public bool ShowsDrawingLayerPicker => Kind == StackupKind.Via;

    public string DrawingLayersLabel => AllowMultipleDrawingLayers ? "Drawing layers:" : "Drawing layer:";

    /// <summary>Subtle units reminder shown next to the Thickness field — the technology's own
    /// <see cref="Technology.DefaultDisplayUnit"/>, the same unit <see cref="StagedThicknessText"/>
    /// is parsed/formatted in.</summary>
    public string ThicknessUnitSuffix => LayoutUnits.Suffix(_owner.Working.DefaultDisplayUnit);

    [ObservableProperty] private string _stagedName = "";
    [ObservableProperty] private string _stagedThicknessText = "";
    [ObservableProperty] private string? _thicknessError;
    public bool HasThicknessError => ThicknessError is not null;
    partial void OnThicknessErrorChanged(string? value) => OnPropertyChanged(nameof(HasThicknessError));

    [ObservableProperty] private string _stagedEpsr = "";
    [ObservableProperty] private string _stagedTanD = "";
    [ObservableProperty] private string _stagedMur  = "";
    [ObservableProperty] private string _stagedSigmaSm = "";

    // ── The subtle mark on a field an EM run cannot use (owner, 2026-09-08) ────────────────────
    //
    // A Gerber import used to leave every substrate value at zero, and the only thing that said so was
    // a sentence in the problems list naming a layer — which is a long way from the box the number
    // has to be typed into. Ten rows of that is a technology somebody believes is finished and that
    // refuses at Simulate.
    //
    // The PREDICATE is not here. StackupFieldReadiness (src/Design) holds one answer per field and
    // TechValidation phrases the same predicates as sentences, so the mark beside the box and the
    // problem in the list can never disagree — which is the failure that matters, a field marked
    // fine that stops a run. This file only asks, and re-asks after every edit.
    //
    // It marks UNUSABLE, never IMPLAUSIBLE. A defaulted 4.4 on a board that is really 3.66 is not
    // marked, because nothing here knows which board it is; the import's own message is what says
    // those numbers are guesses.

    public string? ThicknessNeeds     => StackupFieldReadiness.Problem(Layer, StackupField.Thickness);
    public string? SigmaNeeds         => StackupFieldReadiness.Problem(Layer, StackupField.Sigma);
    public string? EpsrNeeds          => StackupFieldReadiness.Problem(Layer, StackupField.Epsr);
    public string? TanDNeeds          => StackupFieldReadiness.Problem(Layer, StackupField.TanD);
    public string? MurNeeds           => StackupFieldReadiness.Problem(Layer, StackupField.Mur);
    public string? WallThicknessNeeds => StackupFieldReadiness.Problem(Layer, StackupField.WallThickness);

    public bool ThicknessNeedsValue     => ThicknessNeeds     is not null;
    public bool SigmaNeedsValue         => SigmaNeeds         is not null;
    public bool EpsrNeedsValue          => EpsrNeeds          is not null;
    public bool TanDNeedsValue          => TanDNeeds          is not null;
    public bool MurNeedsValue           => MurNeeds           is not null;
    public bool WallThicknessNeedsValue => WallThicknessNeeds is not null;

    /// <summary>
    /// Re-asks every field. Called after ANY commit rather than only after the one that changed —
    /// the fields are not independent (a via's Fill decides whether its wall is asked for at all,
    /// and a kind change moves which of them apply), and six property notifications on an edit
    /// nobody can measure is the cheap side of that trade.
    /// </summary>
    private void RaiseReadiness()
    {
        OnPropertyChanged(nameof(ThicknessNeeds));
        OnPropertyChanged(nameof(SigmaNeeds));
        OnPropertyChanged(nameof(EpsrNeeds));
        OnPropertyChanged(nameof(TanDNeeds));
        OnPropertyChanged(nameof(MurNeeds));
        OnPropertyChanged(nameof(WallThicknessNeeds));
        OnPropertyChanged(nameof(ThicknessNeedsValue));
        OnPropertyChanged(nameof(SigmaNeedsValue));
        OnPropertyChanged(nameof(EpsrNeedsValue));
        OnPropertyChanged(nameof(TanDNeedsValue));
        OnPropertyChanged(nameof(MurNeedsValue));
        OnPropertyChanged(nameof(WallThicknessNeedsValue));
    }

    // ── GI3 R-gi3-1/R-gi3-2 — the conductivity preset ─────────────────────────────────────────
    //
    // σ was one of the two fields on this panel with no tooltip, no preset and — after a Gerber
    // import — no value, while every field beside it carried guidance. This is a SHORTCUT, not a
    // constraint: the text box stays freely typeable and remains the source of truth, the model
    // stores only a number, and nothing in the .ctech, the extractors or the validator has ever heard
    // of a material name. A value matching no metal reads as "Custom", which is a readout of the
    // field rather than a value — selecting it does nothing.
    //
    // The table itself lives in src/Design (ConductorMaterials) because five other sites already held
    // copies of these numbers. Element conductivities only, and never a laminate table — see that
    // file's header.
    public static IReadOnlyList<string> ConductorMaterialChoices { get; } =
        [ConductorMaterials.Custom, .. ConductorMaterials.All.Select(m => m.Name)];

    private string _selectedConductorMaterial = ConductorMaterials.Custom;
    public string SelectedConductorMaterial
    {
        get => _selectedConductorMaterial;
        set
        {
            if (!SetProperty(ref _selectedConductorMaterial, value) || _isRefreshing) return;
            if (ConductorMaterials.ByName(value) is not { } metal) return;   // "Custom" is a readout

            // Written THROUGH the staged text and the existing commit, not straight onto the model:
            // one code path sets σ, so the undo entry, the dirty mark and the redisplay are the ones
            // every other edit on this row already produces.
            StagedSigmaSm = metal.SigmaSm.ToString("0.###e+0", Inv);
            CommitSigmaSm();
        }
    }

    /// <summary>brief-technology-editor-units-and-layers.md R-tec-1: settable ONLY on conductor rows
    /// (meaningless on dielectric/via — <see cref="StackupLayer.IsGroundReference"/>'s own doc
    /// comment). Commits immediately on toggle, mirroring <c>LayerRowViewModel</c>'s own
    /// Visible/Selectable checkboxes rather than the staged-text convention used for numeric fields.</summary>
    [ObservableProperty] private bool _isGroundReference;

    public ObservableCollection<DrawingLayerCheckItem> DrawingLayerOptions { get; } = [];

    /// <summary>
    /// <see cref="DrawingLayerOptions"/> narrowed by <see cref="DrawingLayerFilter"/> — what the
    /// conductor multi-select list actually binds to.
    ///
    /// <para>A real process carries several hundred drawing layers (an imported PDK measured
    /// carries 377). The original selector was a WrapPanel of one CheckBox per layer, repeated for
    /// every stackup entry: unusable to read, and ~10,000 realized controls on that one tab. Filtering
    /// is what makes the list findable; the view virtualizes it, which is what makes it cheap.</para>
    /// </summary>
    public ObservableCollection<DrawingLayerCheckItem> FilteredDrawingLayerOptions { get; } = [];

    /// <summary>
    /// <see cref="FilteredDrawingLayerOptions"/> chunked into rows of
    /// <see cref="TechEditorMetrics.DrawingLayerPickerColumns"/> — <b>what the conductor picker
    /// actually binds to</b> since R-stk2-8 made it three across.
    ///
    /// <para><b>The chunking is what keeps the picker virtualized</b>, and that is the whole reason it
    /// exists. The obvious spelling of "three across" is a <c>UniformGrid</c> items panel, and a
    /// <c>UniformGrid</c> is not a virtualizing panel: on the 377-layer technology the comment above
    /// names, it would realize and arrange all 377 CheckBoxes per conductor card — the same order of
    /// magnitude as the WrapPanel that arrangement replaced, reintroduced to save two rows of height.
    /// A <c>VirtualizingStackPanel</c> over ROWS realizes four of these, each holding three
    /// CheckBoxes, and looks identical.</para>
    /// </summary>
    public ObservableCollection<DrawingLayerCheckRow> FilteredDrawingLayerRows { get; } = [];

    [ObservableProperty] private string _drawingLayerFilter = "";

    partial void OnDrawingLayerFilterChanged(string value) => ApplyDrawingLayerFilter();

    /// <summary>
    /// The single-selection face of the same data, for the kinds §10.4 binds to at most ONE drawing
    /// layer (via, dielectric). A ComboBox is the right control for a closed single choice and is what
    /// this now uses; the checkbox list is kept for the conductor case, which is genuinely multi.
    /// </summary>
    public ObservableCollection<DrawingLayerChoice> DrawingLayerChoices { get; } = [];

    private DrawingLayerChoice? _selectedDrawingLayerChoice;
    public DrawingLayerChoice? SelectedDrawingLayerChoice
    {
        get => _selectedDrawingLayerChoice;
        set
        {
            if (!SetProperty(ref _selectedDrawingLayerChoice, value) || _isRefreshing) return;
            // The sentinel is a real object with a default Key, so it must be turned into "no
            // binding" here rather than passed through — binding it would silently record layer 0/0.
            SetSingleDrawingLayer(value is null || value.IsNone ? null : value.Key);
        }
    }

    /// <summary>What is currently bound, for the collapsed summary line above the conductor list.</summary>
    public string DrawingLayerSummary =>
        Layer.DrawingLayers.Count == 0
            ? "none"
            : string.Join(", ", Layer.DrawingLayers.Select(NameOf));

    private string NameOf(LayerKey k)
    {
        foreach (var l in _owner.Working.Layers)
            if (l.Key.Equals(k)) return l.Name;
        return $"{k.Layer}/{k.Datatype}";
    }

    // ── Via span (R-via-3) ─────────────────────────────────────────────────────
    // Shown because it was invisible: the stackup list keeps vias in list order, which is NOT the
    // physical stack order (a via has no z band of its own), so a user scanning for "the via between
    // Metal1 and Metal2" scrolls past the dielectric between them and finds nothing. The span is the
    // only thing that says which two conductors a via actually joins, and it was already imported,
    // already persisted, already validated — and shown nowhere.

    /// <summary>Conductor names, the only legal values for a span end. "(none)" is index 0.</summary>
    public IReadOnlyList<string> SpanChoices =>
        [SpanNone, .. _owner.Working.Stackup.Layers
                          .Where(l => l.Kind == StackupKind.Conductor)
                          .Select(l => l.Name)];

    internal const string SpanNone = "(none)";

    private string _selectedSpanFrom = SpanNone;
    public string SelectedSpanFrom
    {
        get => _selectedSpanFrom;
        set
        {
            if (!SetProperty(ref _selectedSpanFrom, value) || _isRefreshing) return;
            CommitSpan(from: true, value);
        }
    }

    private string _selectedSpanTo = SpanNone;
    public string SelectedSpanTo
    {
        get => _selectedSpanTo;
        set
        {
            if (!SetProperty(ref _selectedSpanTo, value) || _isRefreshing) return;
            CommitSpan(from: false, value);
        }
    }

    public static IReadOnlyList<ViaFillKind> FillChoices { get; } = Enum.GetValues<ViaFillKind>();

    private ViaFillKind _selectedFill = ViaFillKind.Plated;
    public ViaFillKind SelectedFill
    {
        get => _selectedFill;
        set
        {
            if (!SetProperty(ref _selectedFill, value) || _isRefreshing) return;
            var before = _owner.SnapshotJson();
            Layer.Fill = value;
            _owner.CommitEdit(before, $"Set {Layer.Name} fill to {value}");
            OnPropertyChanged(nameof(IsPlatedVia));
            RaiseReadiness();     // a solid via is not asked for a wall; a plated one is
        }
    }

    public bool IsPlatedVia => IsVia && IsPlated && SelectedFill == ViaFillKind.Plated;

    // GI1 R-gi1-2 — is this via layer's hole metal AT ALL? Separate from Fill, which is a fill model
    // whose two values are both conductive. A checkbox rather than a combo (unlike SheetAt beside
    // it) because this genuinely IS a flag and its absence: null on the model means plated, which is
    // what every technology authored before the field meant, so an unticked box is the one state
    // that has to be written down explicitly.
    //
    // A Gerber import writes false here for a drill file that declared itself non-plated and for a
    // rout-only file, and the import's own message points the user at this box — so it must exist,
    // or a wrong inference is uncorrectable outside a text editor.
    public bool IsPlated
    {
        get => Layer.Plated != false;
        set
        {
            if (IsPlated == value) return;
            if (_isRefreshing) { OnPropertyChanged(); return; }
            var before = _owner.SnapshotJson();
            // Written as null rather than true when ticked, so a technology that never had an opinion
            // round-trips through the editor byte-for-byte instead of gaining a field.
            Layer.Plated = value ? null : false;
            _owner.CommitEdit(before, $"Set {Layer.Name} {(value ? "plated" : "non-plated")}");
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsPlatedVia));
        }
    }

    // ── MIM-6 — which neighbouring dielectric absorbs this conductor's thickness ───────────────
    //
    // A ComboBox rather than the "Ground reference" checkbox beside it, for one reason: the two
    // values are not a flag and its absence. Both are real, named modelling choices a process author
    // picks between — and a capacitor's LOWER plate wants Top while the metal directly above it
    // wants Bottom, so the same technology carries both and the row has to SAY which it is rather
    // than leave it to an unticked box. It commits immediately and undoably, exactly as that
    // checkbox and the via Fill combo do.
    //
    // The STORED value is still a surface of the conductor's own z band; the row displays the
    // consequence instead (SheetAtLabelConverter), because the band is an internal construct of the
    // extractor and naming it in the UI asked the user a question they had no way to answer.
    // A THIRD "symmetric" value was considered and declined (owner, 2026-09-09): it is
    // representable — half the band to each neighbour puts the sheet on the interface between the
    // two grown regions, so PlanarProblem.CanSolve is satisfied — but no structure wants it. It
    // would sit every ordinary line half a metal thickness away from the closed-form microstrip
    // models circuitRF validates against, forever, and it is the row a reader picks by false
    // analogy with the expand-up/expand-down/symmetric convention of tools that grow REAL metal
    // from a drawing plane. That convention answers a different question; this stackup places the
    // metal explicitly and never has to.
    public static IReadOnlyList<ConductorSheetSurface> SheetAtChoices { get; } =
        Enum.GetValues<ConductorSheetSurface>();

    private ConductorSheetSurface _selectedSheetAt = ConductorSheetSurface.Bottom;
    public ConductorSheetSurface SelectedSheetAt
    {
        get => _selectedSheetAt;
        set
        {
            if (!SetProperty(ref _selectedSheetAt, value) || _isRefreshing) return;
            if (Layer.SheetAt == value) return;
            var before = _owner.SnapshotJson();
            Layer.SheetAt = value;
            // The undo entry says what the user chose, in the words the row says it in — "at the
            // bottom of its band" named an internal z interval nobody had been shown.
            _owner.CommitEdit(before,
                $"Give {Layer.Name}'s thickness to {Converters.SheetAtLabelConverter.Label(value)}");
        }
    }

    // ── MIM-7 — the dielectric that is patterned with a plate rather than laterally continuous ──
    //
    // A ComboBox of names with an explicit "(none)", exactly like the via Spans row above, and for
    // the same reason: the value IS another entry's name, so a free-text box would let a tie be
    // spelled wrong and only fail at extraction. "(none)" is the ordinary dielectric — laterally
    // infinite, present in every run — and is what every entry authored before this field means.
    //
    // MIM-11 — the list is BOTH namespaces the tie resolves in, conductors first because that is the
    // order the resolver uses, then the drawing layers, which is where a MASK lives. A drawing layer
    // whose name a conductor already carries is left out rather than listed twice: picking it would
    // store a string that resolves to the conductor, so the second row would be a choice that does
    // not do what it says.
    public IReadOnlyList<string> PresentWithChoices
    {
        get
        {
            var conductors = _owner.Working.Stackup.Layers
                .Where(l => l.Kind == StackupKind.Conductor && !l.IsGroundReference)
                .Select(l => l.Name).ToList();
            var taken = new HashSet<string>(
                _owner.Working.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor)
                                             .Select(l => l.Name), StringComparer.Ordinal);
            return [SpanNone, .. conductors,
                    .. _owner.Working.Layers.Select(l => l.Name)
                             .Where(n => n is { Length: > 0 } && !taken.Contains(n))
                             .Distinct(StringComparer.Ordinal)];
        }
    }

    private string _selectedPresentWith = SpanNone;
    public string SelectedPresentWith
    {
        get => _selectedPresentWith;
        set
        {
            if (!SetProperty(ref _selectedPresentWith, value) || _isRefreshing) return;
            string? v = value == SpanNone ? null : value;
            if (Layer.PresentWithLayer == v) return;
            var before = _owner.SnapshotJson();
            Layer.PresentWithLayer = v;
            _owner.CommitEdit(before, v is null
                ? $"Make {Layer.Name} a continuous dielectric"
                : $"Pattern {Layer.Name} with {v}");
        }
    }

    [ObservableProperty] private string _stagedWallThickness = "";

    public IRelayCommand RemoveCommand   { get; }
    public IRelayCommand MoveUpCommand   { get; }
    public IRelayCommand MoveDownCommand { get; }

    /// <summary>
    /// GI3 R-gi3-10. <b>A via row cannot be moved, because there is nothing for it to move within.</b>
    /// <c>Stackup.Layers</c> is documented "Ordered TOP to BOTTOM" and that order IS z — but a via
    /// entry has no z band of its own, so its position in the list means nothing. Swapping it with a
    /// conductor changed the reading order and not one physical fact, and under
    /// <see cref="ShowsViaGroupHeader"/>'s grouping it would now appear to do nothing at all.
    /// </summary>
    public bool CanMove => !IsVia;

    /// <summary>
    /// GI3 R-gi3-9. True on the FIRST via row of the presented list — the row the "outside the z
    /// order" separator is drawn above. Set by <see cref="TechEditorViewModel"/> as it builds the
    /// filtered projection, because which row is first depends on the filter, not on the model.
    /// </summary>
    public bool ShowsViaGroupHeader
    {
        get => _showsViaGroupHeader;
        internal set => SetProperty(ref _showsViaGroupHeader, value);
    }

    private bool _showsViaGroupHeader;

    /// <summary>
    /// R-stk3-6. True on the one row <see cref="TechEditorViewModel.SelectedStackupLayerName"/>
    /// resolves to; the card's <c>Border</c> shades its whole background from it, so the thing the
    /// user clicked in the drawing and the fields they are now looking at are visibly the same thing.
    ///
    /// <para>SHADE, not outline, and at a low alpha: the card's own text and its <c>needsvalue</c>
    /// field marking both have to stay legible on top of it.</para>
    ///
    /// <para>Set by the owner alone (<c>SyncStackupSelection</c>) — a row does not decide whether it
    /// is selected, because the selection is one thing shared with the drawing and a row that set its
    /// own would be a second copy of it.</para>
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    private bool _isSelected;

    public StackupLayerRowViewModel(StackupLayer layer, TechEditorViewModel owner)
    {
        Layer = layer;
        _owner = owner;

        RemoveCommand   = new RelayCommand(() => owner.RemoveStackupLayer(this));
        MoveUpCommand   = new RelayCommand(() => owner.MoveStackupLayer(this, -1));
        MoveDownCommand = new RelayCommand(() => owner.MoveStackupLayer(this, +1));

        RefreshFromModel();
    }

    /// <summary>
    /// A stored length as the text of an EDITABLE box, at the one precision that survives being
    /// parsed back (<see cref="LayoutUnits.SpellDecimals"/>).
    ///
    /// <para><b>Not <see cref="LayoutUnits.Format"/>'s default of four places</b>, which is a display
    /// precision and is COARSER than the stored value in two of the five units: four places of a mil
    /// is 2.54 nm and of an inch is 2.54 um, against a 1 nm DBU. The box then showed a rounded number
    /// and <see cref="CommitThickness"/> — which parses this very string — wrote the rounding back on
    /// a focus round trip with nothing typed. Owner report, 2026-09-17: a thickness entered as 35 um
    /// read "1.378" after the display unit was changed to mil, and came back as 35.001 um; the second
    /// attempt "worked", because by then the stored value WAS the rounded one. Trailing zeros are
    /// trimmed, so a round number is still spelled round — 57.68 mil stays "57.68".</para>
    ///
    /// <para>The read-only labels on this tab (<c>StackTotalText</c>, <c>BoardThicknessText</c> and
    /// the stack-height difference) deliberately keep the four-place display precision: nothing parses
    /// them back, and a total is easier to read at four places than at six.</para>
    /// </summary>
    private string LengthText(long dbu) =>
        LayoutUnits.Format(
            dbu, _owner.Working.DefaultDisplayUnit, LayoutUnits.DefaultDbuPerMicron,
            LayoutUnits.SpellDecimals(_owner.Working.DefaultDisplayUnit, LayoutUnits.DefaultDbuPerMicron));

    public void RefreshFromModel()
    {
        _isRefreshing = true;
        StagedName          = Layer.Name;
        StagedThicknessText  = LengthText(Layer.ThicknessDbu);
        ThicknessError       = null;
        // Invariant, to match the Commit* parses below. These four are NOT display text: the same
        // string is written here and read back there, so the format and the parse are two halves of
        // one round trip and must agree on the decimal separator. Formatting in the user's culture
        // while parsing invariantly would make every focus-out revert silently for a comma-decimal
        // user, even when they typed nothing. (See brief-localization-groundwork.md §4 and §2.4 —
        // the rule against converting display formatting is about status lines and messages, not
        // about an editable value that has to survive a round trip.)
        StagedEpsr           = Layer.Epsr.ToString("0.####", Inv);
        StagedTanD           = Layer.TanD.ToString("0.######", Inv);
        StagedMur            = Layer.Mur.ToString("0.####", Inv);
        StagedSigmaSm        = Layer.SigmaSm.ToString("0.###e+0", Inv);
        SelectedConductorMaterial = ConductorMaterials.Match(Layer.SigmaSm)?.Name ?? ConductorMaterials.Custom;
        IsGroundReference    = Layer.IsGroundReference;
        SelectedSheetAt      = Layer.SheetAt ?? ConductorSheetSurface.Bottom;
        SelectedPresentWith  = Layer.PresentWithLayer is { Length: > 0 } p ? p : SpanNone;
        OnPropertyChanged(nameof(PresentWithChoices));

        DrawingLayerOptions.Clear();
        foreach (var l in _owner.Working.Layers)
            DrawingLayerOptions.Add(new DrawingLayerCheckItem(l.Key, l.Name, Layer.DrawingLayers.Contains(l.Key), this));

        DrawingLayerChoices.Clear();
        DrawingLayerChoices.Add(DrawingLayerChoice.None);
        foreach (var l in _owner.Working.Layers)
            DrawingLayerChoices.Add(new DrawingLayerChoice(l.Key, l.Name));

        // The single-select face shows whatever the model actually holds. A row that (legally, from a
        // hand-edited file) carries more than one while its kind allows one shows the FIRST rather
        // than silently claiming "none" — the list below it still shows the truth.
        // Skip(1) past the None sentinel deliberately: its Key is default(LayerKey), which a real
        // layer at 0/0 would match, and the sentinel must never win that comparison.
        SelectedDrawingLayerChoice = Layer.DrawingLayers.Count == 0
            ? DrawingLayerChoice.None
            : DrawingLayerChoices.Skip(1).FirstOrDefault(c => c.Key.Equals(Layer.DrawingLayers[0]))
              ?? DrawingLayerChoice.None;

        if (IsVia)
        {
            SelectedSpanFrom    = Layer.SpanFromLayer is { Length: > 0 } f ? f : SpanNone;
            SelectedSpanTo      = Layer.SpanToLayer   is { Length: > 0 } t ? t : SpanNone;
            SelectedFill        = Layer.Fill ?? ViaFillKind.Plated;
            OnPropertyChanged(nameof(IsPlated));
            StagedWallThickness = Layer.WallThicknessDbu is { } w ? LengthText(w) : "";
            OnPropertyChanged(nameof(SpanChoices));
            OnPropertyChanged(nameof(IsPlatedVia));
        }

        _isRefreshing = false;

        ApplyDrawingLayerFilter();
        OnPropertyChanged(nameof(DrawingLayerSummary));
        RaiseReadiness();
    }

    private void ApplyDrawingLayerFilter()
    {
        FilteredDrawingLayerOptions.Clear();
        string q = DrawingLayerFilter.Trim();
        foreach (var o in DrawingLayerOptions)
            if (q.Length == 0 || o.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                FilteredDrawingLayerOptions.Add(o);

        FilteredDrawingLayerRows.Clear();
        for (int i = 0; i < FilteredDrawingLayerOptions.Count; i += TechEditorMetrics.DrawingLayerPickerColumns)
            FilteredDrawingLayerRows.Add(new DrawingLayerCheckRow(
                FilteredDrawingLayerOptions[i],
                i + 1 < FilteredDrawingLayerOptions.Count ? FilteredDrawingLayerOptions[i + 1] : null,
                i + 2 < FilteredDrawingLayerOptions.Count ? FilteredDrawingLayerOptions[i + 2] : null));
    }

    /// <summary>Binds exactly one drawing layer (or none) — the single-select path.</summary>
    private void SetSingleDrawingLayer(LayerKey? key)
    {
        bool same = key is { } k
            ? Layer.DrawingLayers.Count == 1 && Layer.DrawingLayers[0].Equals(k)
            : Layer.DrawingLayers.Count == 0;
        if (same) return;

        var before = _owner.SnapshotJson();
        Layer.DrawingLayers.Clear();
        if (key is { } kk) Layer.DrawingLayers.Add(kk);
        _owner.CommitEdit(before, $"Set {Layer.Name} drawing layer");
    }

    private void CommitSpan(bool from, string value)
    {
        string? v = value == SpanNone ? null : value;
        CommitSpanPair(
            from ? v : Layer.SpanFromLayer,
            from ? Layer.SpanToLayer : v,
            $"Set {Layer.Name} span");
    }

    /// <summary>
    /// Writes BOTH ends of the span as one edit — the one writer, which the two combo boxes above and
    /// brief 5's canvas drags all go through.
    ///
    /// <para>It exists because R-stk5-4's barrel drag moves both ends together and R-stk5-6 requires
    /// that to be ONE undo entry: two calls to the per-end path would leave the user pressing Ctrl-Z
    /// twice to put a barrel back where it was, and — worse — the state between the two writes is a
    /// span whose ends have crossed, which is a shape neither the validator nor the drawing should
    /// ever be asked about. Routing the per-end setters through here as well is what keeps the drag
    /// and the combo boxes from being two answers to the same question.</para>
    /// </summary>
    internal void CommitSpanPair(string? from, string? to, string description)
    {
        if (Layer.SpanFromLayer == from && Layer.SpanToLayer == to) return;

        var before = _owner.SnapshotJson();
        Layer.SpanFromLayer = from;
        Layer.SpanToLayer   = to;
        _owner.CommitEdit(before, description);
    }

    /// <summary>Commits the plated-wall thickness. Blank clears it — which is the correct value for a
    /// solid via and for a plated one whose wall the process never stated.</summary>
    public void CommitWallThickness()
    {
        string s = StagedWallThickness.Trim();
        long? v = null;
        if (s.Length > 0)
        {
            if (!LayoutUnits.TryParse(s, _owner.Working.DefaultDisplayUnit,
                                      LayoutUnits.DefaultDbuPerMicron, out var dbu) || dbu <= 0)
            {
                RefreshFromModel();
                return;
            }
            v = dbu;
        }

        if (Layer.WallThicknessDbu == v) { RefreshFromModel(); return; }
        var before = _owner.SnapshotJson();
        Layer.WallThicknessDbu = v;
        _owner.CommitEdit(before, $"Set {Layer.Name} wall thickness");
        RaiseReadiness();
    }

    public void CommitName()
    {
        var name = StagedName.Trim();
        if (name.Length == 0 || name == Layer.Name) { RefreshFromModel(); return; }

        // R-stk3-8. The selection is held by NAME (see TechEditorViewModel.SelectedStackupLayerName),
        // which survives everything ApplySnapshot destroys — except a rename, which is the one edit
        // that changes the key itself. Asked BEFORE the write, because the old name is what the
        // selection is still spelled as, and re-pointed AFTER the commit, because the commit rebuilds
        // every row VM and re-resolves the selection against the name it had going in. Without this,
        // renaming the entry you are looking at deselects it.
        bool wasSelected = string.Equals(_owner.SelectedStackupLayerName, Layer.Name, StringComparison.Ordinal);

        var before = _owner.SnapshotJson();
        Layer.Name = name;
        _owner.CommitEdit(before, $"Rename stackup layer to {name}");

        if (wasSelected) _owner.SelectedStackupLayerName = name;
    }

    /// <summary>
    /// Commits the band thickness, or REFUSES and puts the field back.
    ///
    /// <para><b>A refusal restores the box from the model</b> (owner, 2026-09-13: a conductor given a
    /// negative thickness drew one number on the cross-section and showed another on its card). The
    /// refused text used to be left standing, so the card read "-35" while the drawing above it went
    /// on reading the thickness the design actually has — two surfaces disagreeing about one value,
    /// with only a message to say which of them was the design. Reverting is what every other Commit
    /// on this row already does; the message stays, and says why the typing did not take.</para>
    /// </summary>
    public void CommitThickness()
    {
        if (!LayoutUnits.TryParse(StagedThicknessText, _owner.Working.DefaultDisplayUnit,
                LayoutUnits.DefaultDbuPerMicron, out var dbu) || dbu <= 0)
        {
            // AFTER the refresh, which clears it: the box goes back to the design's own value and the
            // message is what is left to explain the reversion.
            RefreshFromModel();
            ThicknessError = "Enter a positive length, e.g. 1.6mm, 35u, 100 um.";
            return;
        }
        ThicknessError = null;
        if (dbu == Layer.ThicknessDbu) return;
        var before = _owner.SnapshotJson();
        Layer.ThicknessDbu = dbu;
        _owner.CommitEdit(before, $"Set thickness of {Layer.Name}");
        RefreshFromModel();   // raises readiness
    }

    public void CommitEpsr()
    {
        if (!double.TryParse(StagedEpsr, System.Globalization.NumberStyles.Float, Inv, out var v))
        { RefreshFromModel(); return; }
        if (System.Math.Abs(v - Layer.Epsr) < 1e-12) return;
        var before = _owner.SnapshotJson();
        Layer.Epsr = v;
        _owner.CommitEdit(before, $"Set εr of {Layer.Name}");
        RaiseReadiness();
    }

    public void CommitTanD()
    {
        if (!double.TryParse(StagedTanD, System.Globalization.NumberStyles.Float, Inv, out var v))
        { RefreshFromModel(); return; }
        if (System.Math.Abs(v - Layer.TanD) < 1e-15) return;
        var before = _owner.SnapshotJson();
        Layer.TanD = v;
        _owner.CommitEdit(before, $"Set tanδ of {Layer.Name}");
        RaiseReadiness();
    }

    public void CommitMur()
    {
        if (!double.TryParse(StagedMur, System.Globalization.NumberStyles.Float, Inv, out var v))
        { RefreshFromModel(); return; }
        if (System.Math.Abs(v - Layer.Mur) < 1e-12) return;
        var before = _owner.SnapshotJson();
        Layer.Mur = v;
        _owner.CommitEdit(before, $"Set µr of {Layer.Name}");
        RaiseReadiness();
    }

    public void CommitSigmaSm()
    {
        if (!double.TryParse(StagedSigmaSm, System.Globalization.NumberStyles.Float, Inv, out var v))
        { RefreshFromModel(); return; }
        if (System.Math.Abs(v - Layer.SigmaSm) < 1e-6) return;
        var before = _owner.SnapshotJson();
        Layer.SigmaSm = v;
        _owner.CommitEdit(before, $"Set σ of {Layer.Name}");
        RaiseReadiness();
    }

    partial void OnIsGroundReferenceChanged(bool value)
    {
        if (_isRefreshing || value == Layer.IsGroundReference) return;
        var before = _owner.SnapshotJson();
        Layer.IsGroundReference = value;
        _owner.CommitEdit(before, $"Toggle ground reference for {Layer.Name}");
    }

    // Called by DrawingLayerCheckItem on toggle.
    internal void SetDrawingLayerChecked(LayerKey key, bool isChecked)
    {
        if (_isRefreshing) return;
        bool already = Layer.DrawingLayers.Contains(key);
        if (isChecked == already) return;
        var before = _owner.SnapshotJson();
        if (isChecked)
        {
            // Via/Dielectric: at most one drawing layer — checking a new one replaces, not adds.
            if (!AllowMultipleDrawingLayers) Layer.DrawingLayers.Clear();
            Layer.DrawingLayers.Add(key);
        }
        else
        {
            Layer.DrawingLayers.Remove(key);
        }
        _owner.CommitEdit(before, $"Set drawing layers of {Layer.Name}");
        OnPropertyChanged(nameof(DrawingLayerSummary));
    }
}

/// <summary>
/// One entry in the SINGLE-selection drawing-layer ComboBox (via, dielectric — §10.4's
/// "bound to a drawing layer", singular). <see cref="None"/> is the explicit unbound choice, so
/// clearing a binding is a selection rather than a checkbox the user has to find and untick.
/// </summary>
public sealed class DrawingLayerChoice(LayerKey key, string name)
{
    public LayerKey Key { get; } = key;
    public string Name { get; } = name;

    public static DrawingLayerChoice None { get; } = new(default, "(none)");

    /// <summary>Whether this is the unbound sentinel. Asked rather than compared against
    /// <see cref="Key"/>: the sentinel's key is <c>default</c>, which a real layer at 0/0 shares.</summary>
    public bool IsNone => ReferenceEquals(this, None);

    /// <summary>Bound directly by the ComboBox's default item template.</summary>
    public override string ToString() =>
        IsNone ? Name : $"{Name}  ({Key.Layer}/{Key.Datatype})";
}

/// <summary>One checkable row in a stackup layer's drawing-layer multi-select — a closed set
/// against the current layer table, never free text.</summary>
public sealed partial class DrawingLayerCheckItem : ObservableObject
{
    private readonly StackupLayerRowViewModel _owner;

    public LayerKey Key { get; }
    public string Name  { get; }

    [ObservableProperty] private bool _isChecked;

    public DrawingLayerCheckItem(LayerKey key, string name, bool isChecked, StackupLayerRowViewModel owner)
    {
        Key = key;
        Name = name;
        _owner = owner;
        _isChecked = isChecked;
    }

    partial void OnIsCheckedChanged(bool value) => _owner.SetDrawingLayerChecked(Key, value);
}

/// <summary>
/// Up to three <see cref="DrawingLayerCheckItem"/>s, side by side — one row of the conductor card's
/// 3 x 4 drawing-layer picker (R-stk2-8).
///
/// <para>THREE NAMED CELLS, not a list, and deliberately: the row template binds three CheckBoxes
/// and nothing iterates, so a realized row costs three controls rather than three plus an
/// ItemsControl and its presenter. The last row of an un-even list carries nulls, which the template
/// hides — a blank cell, which is what the fourth column of a three-column grid looks like anyway.
/// The column count is <see cref="TechEditorMetrics.DrawingLayerPickerColumns"/>; changing it means
/// changing this type, which is the honest amount of work for changing the shape of a grid.</para>
/// </summary>
public sealed class DrawingLayerCheckRow
{
    public DrawingLayerCheckRow(DrawingLayerCheckItem a, DrawingLayerCheckItem? b, DrawingLayerCheckItem? c)
    {
        A = a;
        B = b;
        C = c;
    }

    public DrawingLayerCheckItem  A { get; }
    public DrawingLayerCheckItem? B { get; }
    public DrawingLayerCheckItem? C { get; }

    public bool HasB => B is not null;
    public bool HasC => C is not null;
}
