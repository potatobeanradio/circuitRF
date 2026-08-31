using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// VM for one row in the .ctech editor's stackup list — an ordered top-to-bottom plain list
/// (no diagram; see docs/design/layout-view.md §10.4, which is L6). The detail pane shows only
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
        }
    }

    public bool IsPlatedVia => IsVia && SelectedFill == ViaFillKind.Plated;

    // ── MIM-6 — which surface of its own band a conductor's analysis sheet sits on ─────────────
    //
    // A ComboBox rather than the "Ground reference" checkbox beside it, for one reason: the two
    // values are not a flag and its absence. Both are real, named modelling choices a process author
    // picks between — and a capacitor's LOWER plate wants Top while the metal directly above it
    // wants Bottom, so the same technology carries both and the row has to SAY which it is rather
    // than leave it to an unticked box. It commits immediately and undoably, exactly as that
    // checkbox and the via Fill combo do.
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
            _owner.CommitEdit(before, $"Put {Layer.Name}'s analysis sheet at the {value.ToString().ToLowerInvariant()} of its band");
        }
    }

    // ── MIM-7 — the dielectric that is patterned with a plate rather than laterally continuous ──
    //
    // A ComboBox of conductor names with an explicit "(none)", exactly like the via Spans row above,
    // and for the same reason: the value IS another stackup entry's name, so a free-text box would
    // let a tie be spelled wrong and only fail at extraction. "(none)" is the ordinary dielectric —
    // laterally infinite, present in every run — and is what every entry authored before this field
    // means.
    public IReadOnlyList<string> PresentWithChoices =>
        [SpanNone, .. _owner.Working.Stackup.Layers
                          .Where(l => l.Kind == StackupKind.Conductor && !l.IsGroundReference)
                          .Select(l => l.Name)];

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

    public StackupLayerRowViewModel(StackupLayer layer, TechEditorViewModel owner)
    {
        Layer = layer;
        _owner = owner;

        RemoveCommand   = new RelayCommand(() => owner.RemoveStackupLayer(this));
        MoveUpCommand   = new RelayCommand(() => owner.MoveStackupLayer(this, -1));
        MoveDownCommand = new RelayCommand(() => owner.MoveStackupLayer(this, +1));

        RefreshFromModel();
    }

    public void RefreshFromModel()
    {
        _isRefreshing = true;
        StagedName          = Layer.Name;
        StagedThicknessText  = LayoutUnits.Format(Layer.ThicknessDbu, _owner.Working.DefaultDisplayUnit, LayoutUnits.DefaultDbuPerMicron);
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
            StagedWallThickness = Layer.WallThicknessDbu is { } w
                ? LayoutUnits.Format(w, _owner.Working.DefaultDisplayUnit, LayoutUnits.DefaultDbuPerMicron)
                : "";
            OnPropertyChanged(nameof(SpanChoices));
            OnPropertyChanged(nameof(IsPlatedVia));
        }

        _isRefreshing = false;

        ApplyDrawingLayerFilter();
        OnPropertyChanged(nameof(DrawingLayerSummary));
    }

    private void ApplyDrawingLayerFilter()
    {
        FilteredDrawingLayerOptions.Clear();
        string q = DrawingLayerFilter.Trim();
        foreach (var o in DrawingLayerOptions)
            if (q.Length == 0 || o.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                FilteredDrawingLayerOptions.Add(o);
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
        if (from ? Layer.SpanFromLayer == v : Layer.SpanToLayer == v) return;

        var before = _owner.SnapshotJson();
        if (from) Layer.SpanFromLayer = v; else Layer.SpanToLayer = v;
        _owner.CommitEdit(before, $"Set {Layer.Name} span");
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
    }

    public void CommitName()
    {
        var name = StagedName.Trim();
        if (name.Length == 0 || name == Layer.Name) { RefreshFromModel(); return; }
        var before = _owner.SnapshotJson();
        Layer.Name = name;
        _owner.CommitEdit(before, $"Rename stackup layer to {name}");
    }

    public void CommitThickness()
    {
        if (!LayoutUnits.TryParse(StagedThicknessText, _owner.Working.DefaultDisplayUnit,
                LayoutUnits.DefaultDbuPerMicron, out var dbu) || dbu <= 0)
        {
            ThicknessError = "Enter a positive length, e.g. 1.6mm, 35u, 100 um.";
            return;
        }
        ThicknessError = null;
        if (dbu == Layer.ThicknessDbu) return;
        var before = _owner.SnapshotJson();
        Layer.ThicknessDbu = dbu;
        _owner.CommitEdit(before, $"Set thickness of {Layer.Name}");
        RefreshFromModel();
    }

    public void CommitEpsr()
    {
        if (!double.TryParse(StagedEpsr, System.Globalization.NumberStyles.Float, Inv, out var v))
        { RefreshFromModel(); return; }
        if (System.Math.Abs(v - Layer.Epsr) < 1e-12) return;
        var before = _owner.SnapshotJson();
        Layer.Epsr = v;
        _owner.CommitEdit(before, $"Set εr of {Layer.Name}");
    }

    public void CommitTanD()
    {
        if (!double.TryParse(StagedTanD, System.Globalization.NumberStyles.Float, Inv, out var v))
        { RefreshFromModel(); return; }
        if (System.Math.Abs(v - Layer.TanD) < 1e-15) return;
        var before = _owner.SnapshotJson();
        Layer.TanD = v;
        _owner.CommitEdit(before, $"Set tanδ of {Layer.Name}");
    }

    public void CommitMur()
    {
        if (!double.TryParse(StagedMur, System.Globalization.NumberStyles.Float, Inv, out var v))
        { RefreshFromModel(); return; }
        if (System.Math.Abs(v - Layer.Mur) < 1e-12) return;
        var before = _owner.SnapshotJson();
        Layer.Mur = v;
        _owner.CommitEdit(before, $"Set µr of {Layer.Name}");
    }

    public void CommitSigmaSm()
    {
        if (!double.TryParse(StagedSigmaSm, System.Globalization.NumberStyles.Float, Inv, out var v))
        { RefreshFromModel(); return; }
        if (System.Math.Abs(v - Layer.SigmaSm) < 1e-6) return;
        var before = _owner.SnapshotJson();
        Layer.SigmaSm = v;
        _owner.CommitEdit(before, $"Set σ of {Layer.Name}");
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
