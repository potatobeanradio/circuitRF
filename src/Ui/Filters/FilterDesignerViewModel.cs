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

/// <summary>One realiser in the Designer's list, and whether it can build the specification.</summary>
public sealed record FilterRealiserRow(IFilterRealiser Realiser, bool Supported, string Reason)
{
    public string Name => Realiser.Name;
    public string Status => Supported ? "can build it" : Reason;
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
    /// none.</param>
    /// <param name="family">The part whose Properties opened the Designer: its family is preselected.</param>
    /// <param name="realisers">The realisers to offer; <see cref="FilterRealisers.All"/> when null.</param>
    public FilterDesignerViewModel(SchematicViewModel? schematic, SymbolKind? family = null,
        IReadOnlyList<IFilterRealiser>? realisers = null)
    {
        _schematic = schematic;
        _all = realisers ?? FilterRealisers.All;
        _technology = schematic is null ? null : SchematicTechnology.Of(schematic.EditModel);
        _substrate = FilterSynthesisService.Substrate(_technology, null, null, out _);
        _container = PlotHost.AddPlot(PlotType.Rect, FreqUnit.GHz, left: 0, top: 0, width: 640, height: 400);
        Family = family switch { SymbolKind.Mlad => "MLAD", SymbolKind.Mcrc => "MCRC", SymbolKind.Mcla => "MCLA", _ => AnyFamily };
        // A ladder is most often a lowpass; the coupled families are bandpass filters.
        if (family == SymbolKind.Mlad) { Band = FilterBand.Lowpass; F1 = "1 GHz"; }
        _ready = true;
        Refresh();
    }

    public string Title => "Filter Designer" + (_schematic is null ? "" : " — " + _schematic.DocumentName);

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

    partial void OnResponseChanged(FilterResponse value) => Refresh();
    partial void OnBandChanged(FilterBand value) => Refresh();
    partial void OnOrderChanged(string value) => Refresh();
    partial void OnF1Changed(string value) => Refresh();
    partial void OnF2Changed(string value) => Refresh();
    partial void OnRippleChanged(string value) => Refresh();
    partial void OnStopChanged(string value) => Refresh();
    partial void OnZ0Changed(string value) => Refresh();
    partial void OnFamilyChanged(string value) => Refresh();

    public bool IsBand => Band is FilterBand.Bandpass or FilterBand.Bandstop;

    /// <summary>The realisers of the chosen family, each with whether it can build the specification.</summary>
    public ObservableCollection<FilterRealiserRow> Realisers { get; } = [];

    public FilterRealiserRow? Selected
    {
        get => _selected;
        set { if (SetProperty(ref _selected, value)) Realise(); }
    }

    /// <summary>The plot's host: one Data Display container, as the Match Designer's two are.</summary>
    public DataDisplayViewModel PlotHost { get; } = new(new DataSourceLibraryViewModel(), addEmptyPlot: false, selectEmptyPlot: false);

    /// <summary>|S21| and |S11|, ideal and realised.</summary>
    public Plot Plot => _container.PlotVM.Plot;

    /// <summary>The realisation the plot shows, which Place would put down.</summary>
    public FilterRealisation? Realisation => _realisation;

    /// <summary>The specification as typed, the list of realisers for it, the first that can build it selected.</summary>
    public void Refresh()
    {
        if (!_ready) return;
        OnPropertyChanged(nameof(IsBand));
        _spec = Spec(out string? why);
        Realisers.Clear();
        if (_spec is not null)
            foreach (var r in _all.Where(r => Family == AnyFamily || r.Name.Contains(Family, StringComparison.OrdinalIgnoreCase)))
                Realisers.Add(new FilterRealiserRow(r, r.Supports(_spec, out string? reason), reason ?? ""));
        _selected = Realisers.FirstOrDefault(r => r.Supported);
        OnPropertyChanged(nameof(Selected));
        Realise();
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

    /// <summary>The ideal response and the selected realiser's, run through the engine and plotted.</summary>
    private void Realise()
    {
        _realisation = null;
        ParametersText = "";
        SNP? ideal = null, realised = null;
        if (_spec is { } spec)
        {
            var f = FilterSynthesisService.Frequencies(spec, Points);
            string idealNote = "";
            try { ideal = FilterSynthesisService.Response(FilterSynthesisService.Ideal(spec, Points), f, spec.Z0); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { idealNote = $" No ideal response: {ex.Message}"; }

            if (Selected is { Supported: true } row)
            {
                try
                {
                    var realisation = row.Realiser.Realise(spec, _substrate);
                    var notes = new List<string>(realisation.Notes);
                    realised = FilterSynthesisService.Response(
                        FilterSynthesisService.Cnl(spec, realisation, row.Name, _substrate, _technology, Points, notes), f, spec.Z0);
                    _realisation = realisation;
                    ParametersText = string.Join("\n",
                        realisation.Parameters.Select(p => $"{p.Key} = {p.Value}")
                            .Concat(realisation.Sites.Select(s => $"site {s.PinA}{(s.PinB is null ? "" : "-" + s.PinB)}: {s.Role} {s.Value:G4}"))
                            .Concat(notes));
                    Status = $"{row.Name}: {realisation.PartType} with {realisation.Sites.Count} site part(s).{idealNote}";
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Status = $"{row.Name} cannot build it: {ex.Message}{idealNote}";
                }
            }
            else Status = (Realisers.Count == 0 ? "No realiser of this family." : "No realiser can build this; each says why.") + idealNote;
        }
        BuildPlot(ideal, realised);
        OnPropertyChanged(nameof(Realisation));
        PlaceCommand.NotifyCanExecuteChanged();
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
