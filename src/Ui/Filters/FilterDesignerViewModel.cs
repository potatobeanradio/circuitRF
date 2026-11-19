using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Synthesis.Filters;
using CircuitRF.Core.Systems;
using CircuitRF.Design.Filters;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RfCore;

namespace CircuitRF.Ui.Filters;

/// <summary>What realising one realiser on one specification found: the realisation, or why it refused.</summary>
internal sealed record FilterRealised(FilterRealisation? Realisation, string? Refusal);

/// <summary>
/// One realiser in the Designer's list. Its own check answers at once (<see cref="IFilterRealiser.Supports"/>, which designs
/// nothing); realising it in the background then answers for certain, because a realiser that says it can build the filter
/// may still refuse once it designs it (a feed the board cannot give). The row is updated in place as that answer arrives.
/// </summary>
public sealed partial class FilterRealiserRow(IFilterRealiser realiser, bool supported, string reason) : ObservableObject
{
    public IFilterRealiser Realiser { get; } = realiser;
    public string Name => Realiser.Name;

    /// <summary>Whether it can build the specification: its check's answer, then its realisation's.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Status))] private bool _supported = supported;

    /// <summary>Why not, in one sentence: its check's, or its realisation's refusal.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Status))] private string _reason = reason;

    /// <summary>True once it has been realised on the specification, whether it built it or refused.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Status))] private bool _realised;

    public string Status => !Supported ? (Realised ? "refuses: " + Reason : Reason) : Realised ? "builds it" : "can build it; checking…";

    internal void Took(FilterRealised result)
    {
        Supported = result.Realisation is not null;
        Reason = result.Refusal ?? "";
        Realised = true;
    }
}

/// <summary>
/// The Filter Designer (docs/design/filter-synthesis.md §6), laid out as the Match Designer is: the specification on the
/// left, then every realiser with whether it can build it and why not; on the right the ideal response, the prototype's
/// ladder run by the engine, with the selected realisation's own bench overlaid. Place puts the part and a part on each of
/// its sites into the schematic as one undo step. Every number comes from <see cref="FilterSynthesisService"/>, which the
/// <c>filter</c> verb calls too.
/// </summary>
public sealed partial class FilterDesignerViewModel : ObservableObject
{
    public const string AnyFamily = "Any";
    private const int Points = 401;

    /// <summary>How long a typed field waits for the next keystroke before the realisers run on it.</summary>
    internal static readonly TimeSpan EditDelay = TimeSpan.FromMilliseconds(250);

    private readonly SchematicViewModel? _schematic;
    private readonly IReadOnlyList<IFilterRealiser> _all;
    private readonly Technology? _technology;
    private readonly MicrostripSubstrate? _substrate;
    private readonly PlotContainerViewModel _container;
    private FilterSpec? _spec;
    private FilterRealisation? _realisation;
    private FilterRealiserRow? _selected;
    private bool _ready;

    /// <param name="schematic">The schematic Place puts the filter in, and whose technology gives the substrate; null for
    /// none, and Place is then greyed.</param>
    /// <param name="family">The part whose Properties opened the Designer: its family is preselected.</param>
    /// <param name="realisers">The realisers to offer; <see cref="FilterRealisers.All"/> when null.</param>
    /// <param name="technology">The technology with no schematic (Tools ▸ Filter Designer… takes the workspace's); a
    /// schematic's own wins.</param>
    public FilterDesignerViewModel(SchematicViewModel? schematic, SymbolKind? family = null,
        IReadOnlyList<IFilterRealiser>? realisers = null, Technology? technology = null)
    {
        _schematic = schematic;
        _all = realisers ?? FilterRealisers.All;
        _technology = schematic is null ? technology : SchematicTechnology.Of(schematic.EditModel) ?? technology;
        _substrate = FilterSynthesisService.Substrate(_technology, null, null, out _);
        _container = PlotHost.AddPlot(PlotType.Rect, FreqUnit.GHz, left: 0, top: 0, width: 640, height: 400);
        Family = family switch { SymbolKind.Mlad => "MLAD", SymbolKind.Mcrc => "MCRC", SymbolKind.Mcla => "MCLA", _ => AnyFamily };
        // A ladder is most often a lowpass; the coupled families are bandpass filters.
        if (family == SymbolKind.Mlad) { Band = FilterBand.Lowpass; F1 = "1 GHz"; }
        _ready = true;
        Refresh();
    }

    public string Title => "Filter Designer" + (_schematic is null ? "" : " — " + _schematic.DocumentName);

    /// <summary>What Place does, or why it cannot.</summary>
    public string PlaceTip => _schematic is null
        ? "No schematic was open when the Designer started: open one, then start the Designer from it to place the filter."
        : "Put the part and a part on each of its sites into the schematic, as one undo step";

    /// <summary>What the stack gives the realisers, or that there is none.</summary>
    public string SubstrateText => _substrate is { } s
        ? $"{_technology?.Name}: H {s.HMeters * 1e3:0.####} mm, εr {s.EpsR:0.###}"
        : "No technology substrate: a realiser that needs one refuses.";

    public static IReadOnlyList<FilterResponse> Responses { get; } = Enum.GetValues<FilterResponse>();
    public static IReadOnlyList<FilterBand> Bands { get; } = Enum.GetValues<FilterBand>();
    public static IReadOnlyList<string> Families { get; } = [AnyFamily, "MLAD", "MCRC", "MCLA"];

    [ObservableProperty] private FilterResponse _response = FilterResponse.Chebyshev;
    [ObservableProperty] private FilterBand _band = FilterBand.Bandpass;
    [ObservableProperty] private string _order = "3";
    [ObservableProperty] private string _f1 = "2.2 GHz";
    [ObservableProperty] private string _f2 = "2.6 GHz";
    [ObservableProperty] private string _ripple = "0.1";
    [ObservableProperty] private string _stop = "40";
    [ObservableProperty] private string _z0 = "50";
    [ObservableProperty] private string _family = AnyFamily;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _parametersText = "";

    // A choice runs at once; a typed field waits for the typing to pause (EditDelay).
    partial void OnResponseChanged(FilterResponse value) => Refresh();
    partial void OnBandChanged(FilterBand value) => Refresh();
    partial void OnOrderChanged(string value) => Refresh(debounce: true);
    partial void OnF1Changed(string value) => Refresh(debounce: true);
    partial void OnF2Changed(string value) => Refresh(debounce: true);
    partial void OnRippleChanged(string value) => Refresh(debounce: true);
    partial void OnStopChanged(string value) => Refresh(debounce: true);
    partial void OnZ0Changed(string value) => Refresh(debounce: true);
    partial void OnFamilyChanged(string value) => Refresh();

    public bool IsBand => Band is FilterBand.Bandpass or FilterBand.Bandstop;

    /// <summary>The realisers of the chosen family, each with whether it can build the specification.</summary>
    public ObservableCollection<FilterRealiserRow> Realisers { get; } = [];

    public FilterRealiserRow? Selected
    {
        get => _selected;
        // _ready is false while Refresh rebuilds the list, whose own clearing of the selection is no choice
        set { if (SetProperty(ref _selected, value) && _ready) Realise(debounce: false, auto: false); }
    }

    /// <summary>The plot's host: one Data Display container, as the Match Designer's two are.</summary>
    public DataDisplayViewModel PlotHost { get; } = new(new DataSourceLibraryViewModel(), addEmptyPlot: false, selectEmptyPlot: false);

    /// <summary>|S21| and |S11|, ideal and realised.</summary>
    public Plot Plot => _container.PlotVM.Plot;

    /// <summary>The realisation the plot shows, which Place would put down.</summary>
    public FilterRealisation? Realisation => _realisation;

    /// <summary>The specification as typed, the list of realisers for it, the first that can build it selected.</summary>
    public void Refresh() => Refresh(debounce: false);

    private void Refresh(bool debounce)
    {
        if (!_ready) return;
        OnPropertyChanged(nameof(IsBand));
        _spec = Spec(out string? why);
        _ready = false;
        Realisers.Clear();
        if (_spec is { } spec)
            foreach (var r in _all.Where(r => Family == AnyFamily || r.Name.Contains(Family, StringComparison.OrdinalIgnoreCase)))
            {
                var row = new FilterRealiserRow(r, r.Supports(spec, out string? reason), reason ?? "");
                if (row.Supported && Realisations.TryGetValue((spec, _substrate, r), out var known) && known.IsCompletedSuccessfully)
                    row.Took(known.Result);
                Realisers.Add(row);
            }
        _ready = true;
        _selected = Realisers.FirstOrDefault(r => r.Supported);
        OnPropertyChanged(nameof(Selected));
        int check = ++_check;
        Checking = _spec is { } s ? CheckAsync(check, s, debounce) : Task.CompletedTask;
        Realise(debounce, auto: true);
        if (why is not null) Status = why;
    }

    private FilterSpec? Spec(out string? why)
    {
        why = null;
        double hi = 0.0;
        if (!int.TryParse(Order, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1) why = "Order is a whole number of at least 1.";
        else if (!TryHz(F1, out double lo)) why = "F1 is a frequency above zero (2.2 GHz).";
        else if (IsBand && (!TryHz(F2, out hi) || hi <= lo)) why = "F2 is a frequency above F1.";
        else if (!TryPositive(Ripple, out double ripple) || !TryPositive(Stop, out double stop)) why = "Ripple and stopband are dB above zero.";
        else if (!MatchValueFormat.TryParseWithUnit(Z0, MatchQuantity.Resistance, "Ω", out double z0, out _) || !(z0 > 0)) why = "Z0 is a resistance above zero.";
        else return new FilterSpec(Response, Band, n, lo, IsBand ? hi : lo, ripple, stop, z0);
        return null;

        static bool TryHz(string t, out double hz) => MatchValueFormat.TryParseWithUnit(t, MatchQuantity.Frequency, "Hz", out hz, out _) && hz > 0;
        static bool TryPositive(string t, out double v) => double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0;
    }

    // ══ Realising, off the UI thread ══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Every realisation started in this process, by specification, substrate and realiser, which is all a realisation
    /// depends on: a realiser selected again, a specification typed back or a second window on the same one takes it from
    /// here, and one still running is joined rather than started twice. A refusal is a result like any other. Realising is
    /// the slow part (an MCLA's cross-section fit, an MCRC's k and Qe bisections: seconds each); simulating the bench is
    /// a tenth of a second at most, so only realisations are kept.
    /// </summary>
    // ponytail: cleared whole at RealisationLimit, as the N-line solver's cache is; least-recently-used if a session
    // is found to thrash it.
    private static readonly ConcurrentDictionary<(FilterSpec, MicrostripSubstrate?, IFilterRealiser), Task<FilterRealised>> Realisations = new();
    private const int RealisationLimit = 256;

    /// <summary>The realisation of <paramref name="realiser"/> on <paramref name="spec"/>: kept, running, or started on a
    /// worker now.</summary>
    private Task<FilterRealised> RealisationOf(FilterSpec spec, IFilterRealiser realiser)
    {
        var key = (spec, _substrate, realiser);
        if (Realisations.TryGetValue(key, out var known)) return known;
        if (Realisations.Count >= RealisationLimit) Realisations.Clear();
        var substrate = _substrate;
        return Realisations.GetOrAdd(key, _ => Task.Run(() =>
        {
            try { return new FilterRealised(realiser.Realise(spec, substrate), null); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return new FilterRealised(null, ex.Message); }
        }));
    }

    /// <summary>True while the selected realiser's response computes off the UI thread; the window shows a bar beside
    /// "Realisers" once this has stayed true for <see cref="Controls.DelayedBar.Delay"/>.</summary>
    [ObservableProperty] private bool _working;

    /// <summary>The latest request's response and its plotting: done once it is shown, or once a newer request has replaced
    /// it. A test awaits it; the window never does.</summary>
    public Task Realising { get; private set; } = Task.CompletedTask;

    /// <summary>The latest specification's check of every listed realiser, each row updated as its realisation arrives:
    /// done once the last is in, or once the specification has changed. A test awaits it; the window never does.</summary>
    public Task Checking { get; private set; } = Task.CompletedTask;

    private int _generation, _check;

    /// <summary>What one request computed, ready to show.</summary>
    private sealed record Outcome(SNP? Ideal, SNP? Realised, FilterRealisation? Realisation, string Parameters, string Status);

    /// <summary>
    /// The ideal response and the selected realiser's: the ideal first (milliseconds), plotted alone while a realisation is
    /// still running, then the realisation (<see cref="RealisationOf"/>) and its bench run through the engine, each on a
    /// worker. The realisation Place would put down is dropped at once, so Place never writes a part the specification no
    /// longer describes. Only the latest request is shown: an older one is discarded when its step ends. With
    /// <paramref name="auto"/> (the list's own first choice, not the user's) a realiser that refuses once it designs hands
    /// the selection to the next one that still can.
    /// </summary>
    private void Realise(bool debounce, bool auto)
    {
        int generation = ++_generation;
        _realisation = null;
        OnPropertyChanged(nameof(Realisation));
        PlaceCommand.NotifyCanExecuteChanged();
        if (_spec is not { } spec)
        {
            Working = false;
            ParametersText = "";
            BuildPlot(null, null);
            Realising = Task.CompletedTask;
            return;
        }
        Working = true;
        Realising = RealiseAsync(generation, spec, Selected is { Supported: true } r ? r : null, debounce, auto);
    }

    private async Task RealiseAsync(int generation, FilterSpec spec, FilterRealiserRow? row, bool debounce, bool auto)
    {
        if (debounce) await Task.Delay(EditDelay);
        if (generation != _generation) return;
        Outcome outcome;
        try
        {
            var f = FilterSynthesisService.Frequencies(spec, Points);
            var (ideal, idealNote) = await Task.Run(() => Ideal(spec, f));
            if (generation != _generation) return;
            outcome = new Outcome(ideal, null, null, "",
                (Realisers.Count == 0 ? "No realiser of this family." : "No realiser can build this; each says why.") + idealNote);
            while (row is not null)
            {
                var pending = RealisationOf(spec, row.Realiser);
                if (!pending.IsCompleted) BuildPlot(ideal, null);   // the ideal at once; the realised joins it
                var result = await pending;
                if (generation != _generation) return;
                row.Took(result);
                if (result.Realisation is { } realisation)
                {
                    var chosen = row;
                    outcome = await Task.Run(() => Bench(spec, chosen.Name, realisation, f, ideal, idealNote));
                    break;
                }
                outcome = new Outcome(ideal, null, null, "", $"{row.Name} cannot build it: {result.Refusal}{idealNote}");
                if (!auto || Realisers.FirstOrDefault(r => r.Supported) is not { } next) break;
                row = _selected = next;
                OnPropertyChanged(nameof(Selected));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            outcome = new Outcome(null, null, null, "", $"The response could not be computed: {ex.Message}");
        }
        // back on the UI thread (each await resumes on the context it started on)
        if (generation != _generation) return;
        Working = false;
        _realisation = outcome.Realisation;
        ParametersText = outcome.Parameters;
        Status = outcome.Status;
        BuildPlot(outcome.Ideal, outcome.Realised);
        OnPropertyChanged(nameof(Realisation));
        PlaceCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Every listed realiser that says it can build <paramref name="spec"/>, realised one at a time in list order
    /// (the selected one is realised by <see cref="RealiseAsync"/>, and joined here), each row updated as it arrives.</summary>
    private async Task CheckAsync(int check, FilterSpec spec, bool debounce)
    {
        var rows = Realisers.ToList();
        if (debounce) await Task.Delay(EditDelay);
        foreach (var row in rows)
        {
            if (check != _check) return;
            if (!row.Supported || row.Realised) continue;
            var result = await RealisationOf(spec, row.Realiser);
            if (check != _check) return;
            row.Took(result);
        }
    }

    /// <summary>On a worker: the ideal response, or a note saying why there is none.</summary>
    private static (SNP? Ideal, string Note) Ideal(FilterSpec spec, double[] f)
    {
        try { return (FilterSynthesisService.Response(FilterSynthesisService.Ideal(spec, Points), f, spec.Z0), ""); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return (null, $" No ideal response: {ex.Message}"); }
    }

    /// <summary>On a worker: the realisation's bench run through the engine, and what Place writes.</summary>
    private Outcome Bench(FilterSpec spec, string name, FilterRealisation realisation, double[] f, SNP? ideal, string idealNote)
    {
        var notes = new List<string>(realisation.Notes);
        var realised = FilterSynthesisService.Response(
            FilterSynthesisService.Cnl(spec, realisation, name, _substrate, _technology, Points, notes), f, spec.Z0);
        string parameters = string.Join("\n",
            realisation.Parameters.Select(p => $"{p.Key} = {p.Value}")
                .Concat(realisation.Sites.Select(s => $"site {s.PinA}{(s.PinB is null ? "" : "-" + s.PinB)}: {s.Role} {s.Value:G4}"))
                .Concat(notes));
        return new Outcome(ideal, realised, realisation, parameters,
            $"{name}: {realisation.PartType} with {realisation.Sites.Count} site part(s).{idealNote}");
    }

    private void BuildPlot(SNP? ideal, SNP? realised)
    {
        Plot.Traces.Clear();
        void Add(SNP? snp, int row, int colour, string label)
        {
            if (snp is null) return;
            int index = TraceProperties.LineColorOrder[colour];
            var t = new Trace(snp, MatrixType.S, row, 0, DependentVarFormat.Db, false,
                new TraceProperties { LineColorIndex = index, MarkerColorIndex = index, FillColorIndex = index, Custom = false })
                { Label = label };
            t.BuildPath(PlotType.Rect, Plot.FreqUnits);
            Plot.Traces.Add(t);
        }
        Add(ideal, 1, 0, "ideal S21");
        Add(ideal, 0, 1, "ideal S11");
        Add(realised, 1, 2, "realised S21");
        Add(realised, 0, 3, "realised S11");
        Plot.CustomTitleOn = true;
        Plot.CustomTitle = "Insertion and return loss";
        if (Plot.Traces.Count > 0) Plot.Autoscale(force: true);
        _container.OnPlotChanged(this, EventArgs.Empty);
        OnPropertyChanged(nameof(Plot));
        _container.RequestPlotRedraw();
    }

    private bool CanPlace() => _realisation is not null && _schematic is not null;

    /// <summary>
    /// The realised part, right of everything already drawn, and a part on each of its sites, as Place Site Parts puts
    /// them (<see cref="PassiveSites.PlaceParts"/>): one <see cref="PlaceSitePartsCommand"/>, so one Undo takes it all off.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPlace))]
    public void Place()
    {
        if (_schematic is null || _realisation is null) return;
        var model = _schematic.EditModel;
        var taken = _schematic.TakenInstanceNames().ToList();
        var part = FilterSynthesisService.Part(_realisation, "F1");
        part.InstanceName = SchematicEditModel.NextAvailableName(taken, ComponentTypeRegistry.InstancePrefix(part.Symbol));
        part.X = model.Components.Count == 0 ? 0 : Math.Round(model.Components.Max(c => c.X) / 100.0) * 100 + 2000;
        part.Y = model.Components.Count == 0 ? 0 : Math.Round(model.Components.Min(c => c.Y) / 100.0) * 100;

        // The sites are read as Place Site Parts reads them, from the part's own plan: so for that one read it is in the
        // schematic, and the command then adds it with its parts.
        PassiveSites.Placement sites;
        string? refusal;
        model.Components.Add(part);
        try
        {
            sites = PassiveSites.Read(model, out refusal).FirstOrDefault(p => ReferenceEquals(p.Component, part)) is { } passive
                ? PassiveSites.PlaceParts(model, passive, _technology, [.. taken, part.InstanceName])
                : new PassiveSites.Placement([], [], []);
        }
        finally { model.Components.Remove(part); }

        _schematic.Execute(new PlaceSitePartsCommand(model, new PassiveSites.Placement([part, .. sites.Parts], sites.Wires, sites.Notes)));
        Status = $"Placed {part.InstanceName}" + (sites.Parts.Count > 0 ? $" and {string.Join(", ", sites.Parts.Where(p => p.Symbol != SymbolKind.Ground).Select(p => p.InstanceName))}." : ".")
               + (refusal is null ? "" : $" Its sites could not be read: {refusal}") + string.Concat(sites.Notes.Select(n => " " + n));
    }
}
