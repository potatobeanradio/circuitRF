using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Synthesis.Filters;
using CircuitRF.Core.Systems;
using CircuitRF.Design.Filters;
using CircuitRF.Design.PCellDesigner;
using CircuitRF.Engine;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RfCore;

namespace CircuitRF.Ui.Filters;

/// <summary>What realising one realiser on one specification found: the realisation, or why it refused.</summary>
internal sealed record FilterRealised(FilterRealisation? Realisation, string? Refusal);

/// <summary>One row of the Designer's metrics: an extracted quantity, what the spec or the synthesis asks of it, and
/// "pass" or "fail" (blank for a quantity the spec asks nothing of).</summary>
public sealed record FilterMetricRow(string Name, string Value, string Target, string Verdict)
{
    public bool Fail => Verdict == "fail";

    internal static FilterMetricRow Of(FilterCheck c) => new(c.Name, c.Format(c.Value), c.Format(c.Target), c.Pass ? "pass" : "fail");

    /// <summary>The spec's rows of <paramref name="x"/>: centre, bandwidth, ripple, return loss and any zeros, each against the
    /// spec, then the insertion loss at f0 and the unloaded Q beside <paramref name="boardQu"/> when known. The Filter
    /// Designer's table and the PCell Designer's <c>FilterMetricsPanel</c> both show these (brief-pcd-12 D8).</summary>
    public static IReadOnlyList<FilterMetricRow> SpecRows(ExtractedFilter x, double? boardQu) =>
        [.. x.Checks.Where(c => !c.Name.Contains('(')).Select(Of),
            new("Insertion loss", string.Create(CultureInfo.InvariantCulture, $"{x.InsertionLossDb:0.00} dB"), "", ""),
            new("Unloaded Q", string.Create(CultureInfo.InvariantCulture, $"{x.UnloadedQ:0}"),
                boardQu is { } qu ? string.Create(CultureInfo.InvariantCulture, $"board {qu:0}") : "", "")];

    /// <summary>The matrix's rows of <paramref name="x"/>: each external Q, coupling and resonator frequency against the
    /// synthesis's.</summary>
    public static IReadOnlyList<FilterMetricRow> MatrixRows(ExtractedFilter x) => [.. x.Checks.Where(c => c.Name.Contains('(')).Select(Of)];

    /// <summary>"<paramref name="source"/>: every check passes." or how many fail.</summary>
    public static string Note(string source, ExtractedFilter x)
    {
        int fail = x.Checks.Count(c => !c.Pass);
        return source + (fail == 0 ? ": every check passes." : $": {fail} of {x.Checks.Count} checks fail.");
    }
}

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
/// <c>filter</c> verb calls too. Under the plot, the response's prototype read back (§6c): the circuit bench's, or once
/// Run Planar EM has solved the layout (§6d), the EM response's; Polish and Calibrate from EM correct the realisation.
/// </summary>
public sealed partial class FilterDesignerViewModel : ObservableObject
{
    public const string AnyFamily = "Any";
    private const int Points = 401;

    /// <summary>Run Planar EM's frequencies: enough for a bandpass of order 7 to be extracted.</summary>
    public const int EmPoints = 31;

    /// <summary>Where Run Planar EM solves: a bandpass across the extraction's window (ω within
    /// ±<see cref="FilterExtraction.Window"/>), where every point counts towards its metrics; any other band across the
    /// plot's sweep.</summary>
    public static double[] EmFrequencies(FilterSpec spec)
    {
        if (spec.Band != FilterBand.Bandpass) return FilterSynthesisService.Frequencies(spec, EmPoints);
        double f0 = spec.CentreHz, x = FilterExtraction.Window * spec.FractionalBandwidth / 2.0;
        double lo = f0 * (Math.Sqrt(x * x + 1.0) - x), hi = f0 * (Math.Sqrt(x * x + 1.0) + x);
        return [.. Enumerable.Range(0, EmPoints).Select(i => lo + (hi - lo) * i / (EmPoints - 1))];
    }

    /// <summary>How long a typed field waits for the next keystroke before the realisers run on it.</summary>
    internal static readonly TimeSpan EditDelay = TimeSpan.FromMilliseconds(250);

    private readonly SchematicViewModel? _schematic;
    private readonly Func<SchematicViewModel?>? _placeTarget;
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
    /// <param name="placeTarget">With no <paramref name="schematic"/>: asked at Place for the schematic to place into
    /// (Tools ▸ Filter Designer… gives the one active then, else a new cell), so a Designer opened before any schematic
    /// was can still place. Null leaves Place greyed with no schematic.</param>
    public FilterDesignerViewModel(SchematicViewModel? schematic, SymbolKind? family = null,
        IReadOnlyList<IFilterRealiser>? realisers = null, Technology? technology = null,
        Func<SchematicViewModel?>? placeTarget = null)
    {
        _schematic = schematic;
        _placeTarget = schematic is null ? placeTarget : null;
        _all = realisers ?? FilterRealisers.All;
        _technology = schematic is null ? technology : SchematicTechnology.Of(schematic.EditModel) ?? technology;
        _substrate = FilterSynthesisService.Substrate(_technology, null, null, out _);
        _container = PlotHost.AddPlot(PlotType.Rect, FreqUnit.GHz, left: 0, top: 0, width: 640, height: 400);
        _progress = new Progress<RunProgress>(p =>
        {
            BusyIndeterminate = p.Total <= 0;
            BusyProgress = p.Total > 0 ? (double)p.Completed / p.Total : 0.0;
        });
        Family = family switch { SymbolKind.Mlad => "MLAD", SymbolKind.Mcrc => "MCRC", SymbolKind.Mcla => "MCLA", _ => AnyFamily };
        // A ladder is most often a lowpass; the coupled families are bandpass filters.
        if (family == SymbolKind.Mlad) { Band = FilterBand.Lowpass; F1 = "1 GHz"; }
        _ready = true;
        Refresh();
    }

    public string Title => "Filter Designer" + (_schematic is null ? "" : " — " + _schematic.DocumentName);

    /// <summary>What Place does, or why it cannot.</summary>
    public string PlaceTip => _schematic is not null
        ? "Put the part and a part on each of its sites into the schematic, as one undo step"
        : _placeTarget is not null
            ? "Put the part and a part on each of its sites into the active schematic, as one undo step; with no schematic open, into a new cell"
            : "No schematic was open when the Designer started: open one, then start the Designer from it to place the filter.";

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

    /// <summary>The plot's Data Display container, whose marker tools the window wires to the plot.</summary>
    public PlotContainerViewModel ResponseContainer => _container;

    /// <summary>Markers by trace name, kept across a re-run while their trace is gone, as the PCell Designer keeps them.</summary>
    private readonly Dictionary<string, List<Marker>> _parkedMarkers = new(StringComparer.Ordinal);

    /// <summary>The realisation the plot shows, which Place would put down.</summary>
    public FilterRealisation? Realisation => _realisation;

    /// <summary>The specification as typed, the list of realisers for it, the first that can build it selected.</summary>
    public void Refresh() => Refresh(debounce: false);

    private void Refresh(bool debounce)
    {
        if (!_ready) return;
        OnPropertyChanged(nameof(IsBand));
        var previous = _spec;
        _spec = Spec(out string? why);
        if (_spec != previous)
        {
            _cancel.Cancel();   // a newer specification: every run of the older one stops at its next solve
            _cancel = new CancellationTokenSource();
        }
        _ready = false;
        Realisers.Clear();
        if (_spec is { } spec)
            foreach (var r in _all.Where(r => Family == AnyFamily || r.Name.Contains(Family, StringComparison.OrdinalIgnoreCase)))
            {
                var row = new FilterRealiserRow(r, r.Supports(spec, out string? reason), reason ?? "");
                if (row.Supported && Realisations.TryGetValue((spec, _substrate, r), out var known)
                    && known.IsCompletedSuccessfully && known.Result is { } result)
                    row.Took(result);
                Realisers.Add(row);
            }
        _ready = true;
        _selected = Realisers.FirstOrDefault(r => r.Supported);
        OnPropertyChanged(nameof(Selected));
        int check = ++_check;
        Checking = _spec is { } s ? CheckAsync(check, s, debounce, _cancel.Token) : Task.CompletedTask;
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
    /// here, and one still running is joined rather than started twice. A refusal is a result like any other; a run
    /// cancelled because its specification was left (null) is none, and the next to ask starts it again. Realising is the
    /// slow part (an MCLA's cross-section fit, an MCRC's k and Qe bisections: seconds each); simulating the bench is a
    /// tenth of a second at most, so only realisations are kept.
    /// </summary>
    // ponytail: cleared whole at RealisationLimit, as the N-line solver's cache is; least-recently-used if a session
    // is found to thrash it.
    private static readonly ConcurrentDictionary<(FilterSpec, MicrostripSubstrate?, IFilterRealiser), Task<FilterRealised?>> Realisations = new();
    private const int RealisationLimit = 256;

    /// <summary>The realisation of <paramref name="realiser"/> on <paramref name="spec"/>: kept, running, or started on a
    /// worker now, stopped by <paramref name="cancel"/>. Null when the run was cancelled.</summary>
    private Task<FilterRealised?> RealisationOf(FilterSpec spec, IFilterRealiser realiser, CancellationToken cancel)
    {
        var key = (spec, _substrate, realiser);
        if (Realisations.TryGetValue(key, out var known) && !(known.IsCompletedSuccessfully && known.Result is null)) return known;
        if (Realisations.Count >= RealisationLimit) Realisations.Clear();
        var substrate = _substrate;
        return Realisations[key] = Task.Run<FilterRealised?>(() =>
        {
            try { return new FilterRealised(realiser.Realise(spec, substrate, cancel), null); }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return new FilterRealised(null, ex.Message); }
        });
    }

    /// <summary>The realisation, waited for: started again under <paramref name="cancel"/> when the run it joined was
    /// another window's and was cancelled; null once <paramref name="cancel"/> is.</summary>
    private async Task<FilterRealised?> RealisedAsync(FilterSpec spec, IFilterRealiser realiser, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
            if (await RealisationOf(spec, realiser, cancel) is { } result) return result;
        return null;
    }

    /// <summary>True while the selected realiser's response computes off the UI thread; the window shows a bar beside
    /// "Realisers" once this has stayed true for <see cref="Controls.DelayedBar.Delay"/>.</summary>
    [ObservableProperty] private bool _working;

    /// <summary>The latest request's ideal on the plot: done in milliseconds, before any realisation. A test awaits it;
    /// the window never does.</summary>
    public Task Drawn { get; private set; } = Task.CompletedTask;

    /// <summary>The latest request's response and its plotting: done once it is shown, or once a newer request has replaced
    /// it. A test awaits it; the window never does.</summary>
    public Task Realising { get; private set; } = Task.CompletedTask;

    /// <summary>The latest specification's check of every listed realiser, each row updated as its realisation arrives:
    /// done once the last is in, or once the specification has changed. A test awaits it; the window never does.</summary>
    public Task Checking { get; private set; } = Task.CompletedTask;

    private int _generation, _check;

    /// <summary>The current specification's runs: cancelled when it changes, and when the window closes.</summary>
    private CancellationTokenSource _cancel = new();

    /// <summary>Stops everything this Designer has running; the window calls it as it closes.</summary>
    public void Cancel()
    {
        _cancel.Cancel();
        _work.Cancel();
        _generation++;
        _check++;
    }

    /// <summary>The ideal responses of one specification on its sweep: lossless, and for a bandpass on a substrate with
    /// every resonator at the board's unloaded Q (<see cref="CoupledResonatorDesign.UnloadedQ"/>).</summary>
    private sealed record Ideals(FilterSpec Spec, double[] F, SNP? Lossless, SNP? Lossy, double? Qu, string Note);

    /// <summary>The last ideals computed, reused while their specification stands (a new selection).</summary>
    private Ideals? _ideals;

    /// <summary>What one request computed, ready to show.</summary>
    private sealed record Outcome(Ideals? Ideals, SNP? Realised, FilterRealisation? Realisation, string Parameters, string Status,
        Metrics? Circuit = null, string? EmRefusal = null);

    /// <summary>
    /// The ideal response and the selected realiser's. The ideal is computed on a worker in milliseconds and plotted at once,
    /// without waiting for the typing to pause, with "realising…" while the selected realisation is still to come. That
    /// realisation (<see cref="RealisedAsync"/>) and its bench then run on workers, after the pause, and fill in when they
    /// arrive. The realisation Place would put down is dropped at once, so Place never writes a part the specification no
    /// longer describes. Only the latest request is shown; a newer specification cancels an older one's realisations at
    /// their next solve. With <paramref name="auto"/> (the list's own first choice, not the user's) a realiser that refuses
    /// once it designs hands the selection to the next one that still can.
    /// </summary>
    private void Realise(bool debounce, bool auto)
    {
        int generation = ++_generation;
        _realisation = null;
        (_circuit, _emRefusal) = (null, null);
        OnPropertyChanged(nameof(Realisation));
        PlaceCommand.NotifyCanExecuteChanged();
        MakePCellCommand.NotifyCanExecuteChanged();
        NotifyWork();
        if (_spec is not { } spec)
        {
            Working = false;
            ParametersText = "";
            Show(null, null);
            Drawn = Realising = Task.CompletedTask;
            return;
        }
        Working = true;
        var row = Selected is { Supported: true } r ? r : null;
        var drawn = DrawAsync(generation, spec, row);
        Drawn = drawn;
        Realising = RealiseAsync(generation, spec, row, debounce, auto, drawn, _cancel.Token);
    }

    /// <summary>The ideals on the plot, alone while the selected realisation is still to come.</summary>
    private async Task<Ideals?> DrawAsync(int generation, FilterSpec spec, FilterRealiserRow? row)
    {
        var ideals = _ideals is { } known && known.Spec == spec ? known : await Task.Run(() => IdealsOf(spec));
        if (generation != _generation) return null;
        _ideals = ideals;
        bool ready = row is not null && Realisations.TryGetValue((spec, _substrate, row.Realiser), out var run)
                     && run.IsCompletedSuccessfully && run.Result is not null;
        if (!ready)
        {
            Show(ideals, null);
            if (row is not null) Status = $"{row.Name}: realising…";
        }
        return ideals;
    }

    private async Task RealiseAsync(int generation, FilterSpec spec, FilterRealiserRow? row, bool debounce, bool auto,
        Task<Ideals?> drawn, CancellationToken cancel)
    {
        Outcome outcome;
        try
        {
            if (debounce) await Task.Delay(EditDelay, cancel);
            if (await drawn is not { } ideals || generation != _generation) return;
            outcome = new Outcome(ideals, null, null, "",
                (Realisers.Count == 0 ? "No realiser of this family." : "No realiser can build this; each says why.") + ideals.Note);
            while (row is not null)
            {
                var result = await RealisedAsync(spec, row.Realiser, cancel);
                if (result is null || generation != _generation) return;
                row.Took(result);
                if (result.Realisation is { } realisation)
                {
                    var chosen = row;
                    outcome = await Task.Run(() => Bench(spec, chosen.Name, realisation, ideals));
                    break;
                }
                outcome = new Outcome(ideals, null, null, "", $"{row.Name} cannot build it: {result.Refusal}{ideals.Note}");
                if (!auto || Realisers.FirstOrDefault(r => r.Supported) is not { } next) break;
                row = _selected = next;
                OnPropertyChanged(nameof(Selected));
                Status = $"{row.Name}: realising…";
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            outcome = new Outcome(null, null, null, "", $"The response could not be computed: {ex.Message}");
        }
        // back on the UI thread (each await resumes on the context it started on)
        if (generation != _generation) return;
        Working = false;
        _realisation = outcome.Realisation;
        (_circuit, _emRefusal) = (outcome.Circuit, outcome.EmRefusal);
        ParametersText = outcome.Parameters;
        Status = outcome.Status;
        Show(outcome.Ideals, outcome.Realised);
        OnPropertyChanged(nameof(Realisation));
        PlaceCommand.NotifyCanExecuteChanged();
        MakePCellCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Every listed realiser that says it can build <paramref name="spec"/>, realised one at a time in list order
    /// (the selected one is realised by <see cref="RealiseAsync"/>, and joined here), each row updated as it arrives.</summary>
    private async Task CheckAsync(int check, FilterSpec spec, bool debounce, CancellationToken cancel)
    {
        var rows = Realisers.ToList();
        try { if (debounce) await Task.Delay(EditDelay, cancel); }
        catch (OperationCanceledException) { return; }
        foreach (var row in rows)
        {
            if (check != _check) return;
            if (!row.Supported || row.Realised) continue;
            var result = await RealisedAsync(spec, row.Realiser, cancel);
            if (result is null || check != _check) return;
            row.Took(result);
        }
    }

    /// <summary>On a worker: the ideals, or a note saying why there is none.</summary>
    private Ideals IdealsOf(FilterSpec spec)
    {
        var f = FilterSynthesisService.Frequencies(spec, Points);
        SNP? lossless = null, lossy = null;
        double? qu = null;
        string note = "";
        try { lossless = FilterSynthesisService.Response(FilterSynthesisService.Ideal(spec, Points), f, spec.Z0); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { note = $" No ideal response: {ex.Message}"; }
        if (spec.Band == FilterBand.Bandpass && _substrate is { } s)
            try
            {
                double q = CoupledResonatorDesign.UnloadedQ(spec.Z0, spec.CentreHz, s);
                if (double.IsFinite(q)) (qu, lossy) = (q, FilterSynthesisService.LossyIdeal(spec, f, q));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* no Z0 line on this board, or no prototype: no lossy ideal */ }
        return new Ideals(spec, f, lossless, lossy, qu, note);
    }

    /// <summary>On a worker: the realisation's bench run through the engine, and what Place writes.</summary>
    private Outcome Bench(FilterSpec spec, string name, FilterRealisation realisation, Ideals ideals)
    {
        var notes = new List<string>(realisation.Notes);
        var realised = FilterSynthesisService.Response(
            FilterSynthesisService.Cnl(spec, realisation, name, _substrate, _technology, Points, notes), ideals.F, spec.Z0);
        string parameters = string.Join("\n",
            realisation.Parameters.Select(p => $"{p.Key} = {p.Value}")
                .Concat(realisation.Sites.Select(s => $"site {s.PinA}{(s.PinB is null ? "" : "-" + s.PinB)}: {s.Role} {s.Value:G4}"))
                .Concat(notes));
        return new Outcome(ideals, realised, realisation, parameters,
            $"{name}: {realisation.PartType} with {realisation.Sites.Count} site part(s).{ideals.Note}", MetricsOf(spec, realised),
            _substrate is { } s ? FilterEmService.Refusal(spec, realisation, s) : null);
    }

    // ══ The plot and the loss readout ═════════════════════════════════════════════════════════════════════════════

    /// <summary>Draw the ideal with every resonator at the board's unloaded Q too.</summary>
    [ObservableProperty] private bool _showLoss = true;

    /// <summary>The board's unloaded Q, what it costs the ideal at f0, and what the realised part loses there; empty when
    /// there is no lossy ideal (not a bandpass, or no substrate).</summary>
    [ObservableProperty] private string _lossText = "";

    /// <summary>True when the specification has a lossy ideal to draw.</summary>
    public bool HasLoss => _shown.Ideals?.Lossy is not null;

    private (Ideals? Ideals, SNP? Realised) _shown;

    partial void OnShowLossChanged(bool value) => Show(_shown.Ideals, _shown.Realised);

    private void Show(Ideals? ideals, SNP? realised)
    {
        _shown = (ideals, realised);
        LossText = LossReadout(ideals, realised);
        OnPropertyChanged(nameof(HasLoss));
        BuildPlot(ideals?.Lossless, ShowLoss ? ideals?.Lossy : null, realised, Em?.S);
        ShowMetrics();
    }

    /// <summary>
    /// "Unloaded Q 112, a 50 Ω line's conductor and dielectric loss at 2.4 GHz. At f0 the ideal with that Q loses 2.6 dB;
    /// the realised part loses 2.9 dB, 2.8 dB of it dissipated." Dissipated is the insertion loss less the mismatch's,
    /// −10·log(|S21|²/(1 − |S11|²)): what a lossless filter cannot lose, whatever its match.
    /// </summary>
    private static string LossReadout(Ideals? ideals, SNP? realised)
    {
        if (ideals is not { Qu: { } qu, Lossy: { } lossy } i) return "";
        int at = Array.FindIndex(i.F, f => f >= i.Spec.CentreHz);
        if (at > 0 && i.Spec.CentreHz - i.F[at - 1] < i.F[at] - i.Spec.CentreHz) at--;
        static double Loss(SNP snp, int k) => -20.0 * Math.Log10(snp.Matrices[k][1, 0].Magnitude);
        string text = string.Create(CultureInfo.InvariantCulture,
            $"Unloaded Q {qu:0}, a {i.Spec.Z0:0.##} Ω line's conductor and dielectric loss at {i.Spec.CentreHz / 1e9:0.###} GHz. "
            + $"At f0 the ideal with that Q loses {Loss(lossy, at):0.00} dB");
        if (realised is null) return text + ".";
        var m = realised.Matrices[at];
        double s21 = m[1, 0].Magnitude, s11 = m[0, 0].Magnitude;
        return text + string.Create(CultureInfo.InvariantCulture,
            $"; the realised part loses {Loss(realised, at):0.00} dB, {-10.0 * Math.Log10(s21 * s21 / (1.0 - s11 * s11)):0.00} dB of it dissipated.");
    }

    private void BuildPlot(SNP? ideal, SNP? lossy, SNP? realised, SNP? em)
    {
        // The traces are rebuilt on every edit: each marker goes to the new trace of the same name, at its own x.
        foreach (var old in Plot.Traces)
            if (old.Markers.Count > 0) _parkedMarkers[old.Label ?? ""] = [.. old.Markers];
            else _parkedMarkers.Remove(old.Label ?? "");
        Plot.Traces.Clear();
        void Add(SNP? snp, int row, int colour, string label)
        {
            if (snp is null) return;
            int index = TraceProperties.LineColorOrder[colour];
            var t = new Trace(snp, MatrixType.S, row, 0, DependentVarFormat.Db, false,
                new TraceProperties { LineColorIndex = index, MarkerColorIndex = index, FillColorIndex = index, Custom = false })
                { Label = label };
            if (_parkedMarkers.Remove(label, out var kept)) t.Markers.AddRange(kept);
            t.BuildPath(PlotType.Rect, Plot.FreqUnits);
            Plot.Traces.Add(t);
        }
        Add(ideal, 1, 0, "ideal S21");
        Add(ideal, 0, 1, "ideal S11");
        Add(lossy, 1, 4, "ideal S21 at Qu");
        Add(realised, 1, 2, "realised S21");
        Add(realised, 0, 3, "realised S11");
        Add(em, 1, 5, "EM S21");
        Add(em, 0, 6, "EM S11");
        Plot.CustomTitleOn = true;
        Plot.CustomTitle = "Insertion and return loss";
        if (Plot.Traces.Count > 0) Plot.Autoscale(force: true);
        _container.OnPlotChanged(this, EventArgs.Empty);
        OnPropertyChanged(nameof(Plot));
        _container.RequestPlotRedraw();
    }

    // ══ The metrics, Planar EM, Polish and Calibrate from EM ══════════════════════════════════════════════════════

    /// <summary>A response's prototype read back (<see cref="FilterSynthesisService.Extract"/>), or why there is none.</summary>
    private sealed record Metrics(ExtractedFilter? Filter, string? Why);

    /// <summary>A Planar EM run of a realisation and its metrics.</summary>
    private sealed record EmRun(SNP S, Metrics Metrics, double Seconds);

    /// <summary>On a worker: <paramref name="s"/>'s metrics, or the extraction's refusal (not a bandpass, too few points).</summary>
    private static Metrics MetricsOf(FilterSpec spec, SNP s)
    {
        try { return new Metrics(FilterSynthesisService.Extract(spec, s), null); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return new Metrics(null, ex.Message); }
    }

    /// <summary>
    /// Every Planar EM run in this process, by the stored values of the part it solved. A realisation's values are a
    /// dictionary of its own (a polish makes new ones), and its sites, spec and substrate come with it, so they name the
    /// run; weakly held, they go when the realisation does.
    /// </summary>
    private static readonly ConditionalWeakTable<IReadOnlyDictionary<string, string>, EmRun> EmRuns = new();

    /// <summary>What each polished part's values were solved for, so a further polish or calibration goes on from there.</summary>
    private static readonly ConditionalWeakTable<IReadOnlyDictionary<string, string>, PolishAim> Aims = new();

    private Metrics? _circuit;
    private string? _emRefusal;

    /// <summary>The shown realisation's Planar EM run, if it has one.</summary>
    private EmRun? Em => _realisation is { } r && EmRuns.TryGetValue(r.Parameters, out var run) ? run : null;

    public bool HasEm => Em is not null;

    /// <summary>The spec's rows: centre, bandwidth, ripple, return loss and any zeros, each against the spec, then the
    /// insertion loss at f0 and the unloaded Q.</summary>
    [ObservableProperty] private IReadOnlyList<FilterMetricRow> _specRows = [];

    /// <summary>The matrix's rows: each external Q, coupling and resonator frequency against the synthesis's.</summary>
    [ObservableProperty] private IReadOnlyList<FilterMetricRow> _matrixRows = [];

    /// <summary>Whose response the metrics read, and how many checks fail; or why there are none.</summary>
    [ObservableProperty] private string _metricsNote = "";

    /// <summary>Read the metrics, and draw the overlay, from the Planar EM run when there is one; else the bench's.</summary>
    [ObservableProperty] private bool _showEm;

    partial void OnShowEmChanged(bool value) => ShowMetrics();

    private void ShowMetrics()
    {
        var em = Em;
        bool onEm = ShowEm && em is not null;
        var metrics = onEm ? em!.Metrics : _circuit;
        if (metrics?.Filter is { } x)
        {
            SpecRows = FilterMetricRow.SpecRows(x, _shown.Ideals?.Qu);
            MatrixRows = FilterMetricRow.MatrixRows(x);
            MetricsNote = FilterMetricRow.Note(onEm ? $"Planar EM, {em!.S.FrequencyCount} points" : "Circuit bench", x);
        }
        else
        {
            SpecRows = MatrixRows = [];
            MetricsNote = metrics?.Why ?? "";
        }
        OnPropertyChanged(nameof(HasEm));
        NotifyWork();
    }

    /// <summary>True while Planar EM, Polish or Calibrate from EM runs; one at a time.</summary>
    [ObservableProperty] private bool _busy;

    /// <summary>How far the running EM solve is, 0 to 1.</summary>
    [ObservableProperty] private double _busyProgress;

    /// <summary>True when the running work cannot say how far it is (a polish, or the EM solve before its first point).</summary>
    [ObservableProperty] private bool _busyIndeterminate;

    partial void OnBusyChanged(bool value) => NotifyWork();

    /// <summary>The latest Planar EM, Polish or Calibrate from EM: done once it is shown, cancelled or failed. A test awaits
    /// it; the window never does.</summary>
    public Task Work { get; private set; } = Task.CompletedTask;

    private CancellationTokenSource _work = new();

    public string EmTip => _substrate is null ? "No technology substrate: Planar EM needs one."
        : _realisation is null ? "Planar EM solves the selected realisation, once it is realised."
        : _emRefusal ?? $"Solve the realisation's layout on Planar MoM, coarse mesh, at {EmPoints} frequencies across the band. "
          + "Its |S21| and |S11| join the plot, and the metrics read from it.";

    public string PolishTip => _substrate is null ? "No technology substrate: Polish runs the bench on one."
        : _realisation is null ? "Polish corrects the selected realisation, once it is realised."
        : FilterPolish.Refusal(_realisation)?.Message ?? "Correct the gaps, the tap and the lengths until the bench extracts to "
          + "the synthesised matrix, in three passes. The polished part replaces this realisation, and Place puts it down.";

    public string CalibrateTip => _realisation is not null && _substrate is not null && FilterPolish.Refusal(_realisation) is null
        && Em is null ? "Run Planar EM first: this corrects the part against its EM response."
        : _realisation is not null && _substrate is not null && FilterPolish.Refusal(_realisation) is null
            ? "One Polish pass against the Planar EM response in place of the bench's, and one more EM run. The corrected part "
              + "replaces this realisation."
            : PolishTip;

    private bool CanRunEm() => !Busy && _realisation is not null && _substrate is not null && _emRefusal is null;
    private bool CanPolish() => !Busy && _realisation is not null && _substrate is not null && FilterPolish.Refusal(_realisation) is null;
    private bool CanCalibrate() => CanPolish() && Em is not null;
    private bool CanCancelWork() => Busy;

    private void NotifyWork()
    {
        OnPropertyChanged(nameof(EmTip));
        OnPropertyChanged(nameof(PolishTip));
        OnPropertyChanged(nameof(MakePCellTip));
        OnPropertyChanged(nameof(CalibrateTip));
        RunEmCommand.NotifyCanExecuteChanged();
        PolishCommand.NotifyCanExecuteChanged();
        CalibrateCommand.NotifyCanExecuteChanged();
        MakePCellCommand.NotifyCanExecuteChanged();
        CancelWorkCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The selected realisation's layout on Planar MoM (<see cref="FilterEmService"/>), on a worker: its |S21| and
    /// |S11| overlaid, and the metrics switched to it. A run is kept, so asking again shows it at once.</summary>
    [RelayCommand(CanExecute = nameof(CanRunEm))]
    public void RunEm()
    {
        if (_spec is not { } spec || _realisation is not { } realisation || _substrate is not { } substrate) return;
        Work = RunWork("Planar EM", async token =>
        {
            var run = await Task.Run(() => EmOf(spec, realisation, substrate, token), token);
            if (ReferenceEquals(_realisation, realisation))
            {
                ShowEm = true;
                Show(_shown.Ideals, _shown.Realised);
            }
            return string.Create(CultureInfo.InvariantCulture, $"Planar EM: {run.S.FrequencyCount} points in {run.Seconds:0.0} s. ")
                   + (run.Metrics.Filter is { } x ? string.Create(CultureInfo.InvariantCulture,
                       $"Centre {x.CentreHz / 1e9:0.0000} GHz, bandwidth {x.BandwidthHz / 1e6:0.0} MHz, worst return loss {x.ReturnLossDb:0.0} dB.")
                       : run.Metrics.Why);
        });
    }

    /// <summary>Polish (<see cref="FilterPolish"/>, three passes on the bench), on a worker: the polished part replaces the
    /// selected realisation, and the status gives its worst return loss before and after.</summary>
    [RelayCommand(CanExecute = nameof(CanPolish))]
    public void Polish() => Correct(fromEm: false);

    /// <summary>One Polish pass with the Planar EM response in place of the bench's, and the corrected part solved on EM.</summary>
    [RelayCommand(CanExecute = nameof(CanCalibrate))]
    public void Calibrate() => Correct(fromEm: true);

    [RelayCommand(CanExecute = nameof(CanCancelWork))]
    public void CancelWork() => _work.Cancel();

    private void Correct(bool fromEm)
    {
        if (_spec is not { } spec || _realisation is not { } realisation || _substrate is not { } substrate || Selected is not { } row) return;
        string what = fromEm ? "Calibrate from EM" : "Polish";
        Work = RunWork(what, async token =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Func<FilterRealisation, SNP>? response = fromEm ? part => EmOf(spec, part, substrate, token).S : null;
            var aim = Aims.TryGetValue(realisation.Parameters, out var known) ? known : null;
            var (polished, history) = await Task.Run(() => FilterPolish.Run(spec, realisation, substrate, fromEm ? 1 : 3, response, aim, token), token);
            var kept = history.First(h => ReferenceEquals(h.Parameters, polished.Parameters));
            Aims.AddOrUpdate(polished.Parameters, kept.Aim);
            // The polished part is the realiser's realisation of this spec from now on: selected again, it is this one.
            Realisations[(spec, substrate, row.Realiser)] = Task.FromResult<FilterRealised?>(new FilterRealised(polished, null));
            if (ReferenceEquals(_realisation, realisation))
            {
                Realise(debounce: false, auto: false);
                await Realising;
            }
            string on = fromEm ? " on Planar EM" : "";
            return string.Create(CultureInfo.InvariantCulture, $"{what}: worst return loss across the band "
                + $"{history[0].Extraction.ReturnLossDb:0.0} dB before, {kept.Extraction.ReturnLossDb:0.0} dB after{on} "
                + $"({clock.Elapsed.TotalSeconds:0} s).");
        });
    }

    /// <summary>On a worker: <paramref name="realisation"/>'s Planar EM run, kept by its values; the bar follows its points.</summary>
    private EmRun EmOf(FilterSpec spec, FilterRealisation realisation, MicrostripSubstrate substrate, CancellationToken token)
    {
        if (EmRuns.TryGetValue(realisation.Parameters, out var known)) return known;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var s = FilterEmService.Run(spec, realisation, substrate, EmFrequencies(spec), _progress, token).S;
        var run = new EmRun(s, MetricsOf(spec, s), clock.Elapsed.TotalSeconds);
        EmRuns.AddOrUpdate(realisation.Parameters, run);
        return run;
    }

    /// <summary>The EM solve's progress, posted to the thread the Designer was made on (the constructor makes it).</summary>
    private readonly IProgress<RunProgress> _progress;

    /// <summary><paramref name="work"/> with the bar and Cancel; its own line in the status when it ends, or why not.</summary>
    private async Task RunWork(string what, Func<CancellationToken, Task<string>> work)
    {
        _work = new CancellationTokenSource();
        Busy = true;
        (BusyProgress, BusyIndeterminate) = (0.0, true);
        Status = $"{what}…";
        try { Status = await work(_work.Token); }
        catch (OperationCanceledException) { Status = $"{what} cancelled."; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = $"{what} failed: {ex.Message}"; }
        finally { Busy = false; }
    }

    private bool CanPlace() => _realisation is not null && (_schematic is not null || _placeTarget is not null);

    /// <summary>
    /// The realised part, right of everything already drawn, and a part on each of its sites, as Place Site Parts puts
    /// them (<see cref="PassiveSites.PlaceParts"/>): one <see cref="PlaceSitePartsCommand"/>, so one Undo takes it all off.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPlace))]
    public void Place()
    {
        if (_realisation is null) return;
        var schematic = _schematic ?? _placeTarget?.Invoke();
        if (schematic is null)
        {
            Status = "Nothing to place into: open a workspace, or a schematic, and Place again.";
            return;
        }
        var model = schematic.EditModel;
        var taken = schematic.TakenInstanceNames().ToList();
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

        schematic.Execute(new PlaceSitePartsCommand(model, new PassiveSites.Placement([part, .. sites.Parts], sites.Wires, sites.Notes)));
        Status = $"Placed {part.InstanceName}" + (sites.Parts.Count > 0 ? $" and {string.Join(", ", sites.Parts.Where(p => p.Symbol != SymbolKind.Ground).Select(p => p.InstanceName))}." : ".")
               + (refusal is null ? "" : $" Its sites could not be read: {refusal}") + string.Concat(sites.Notes.Select(n => " " + n))
               + (_schematic is null ? $" In {schematic.DocumentName}; Update Layout from Schematic draws its layout." : "");
    }

    // ══ Make PCell (brief-pcd-12 R-pcd12-2) ════════════════════════════════════════════════════════════════════════

    /// <summary>The workspace a designed cell goes into; the window gives the open one. With none, the schematic's.</summary>
    public Func<string?>? WorkspaceRoot { get; set; }

    /// <summary>Opens the PCell Designer on a cell folder in a workspace root; the window gives it.</summary>
    public Action<string, string>? OpenDesigner { get; set; }

    private string? MakeRoot => WorkspaceRoot?.Invoke() ?? _schematic?.KitWorkspaceRoot;

    /// <summary>What Make PCell… does, or why it cannot.</summary>
    public string MakePCellTip => _realisation is null ? "Make PCell turns the selected realisation into a designed cell, once it is realised."
        : MakeRoot is null ? "Open a workspace first: a designed cell is a cell of one."
        : "Make the realisation a designed cell (FilterPCell1, 2, …): the part, its site parts and two Ports, each length, gap "
          + "and width a parameter with a ±20 % range; then open it in the PCell Designer.";

    private bool CanMakePCell() => !Busy && _realisation is not null && _spec is not null && Selected is not null && MakeRoot is not null;

    /// <summary>
    /// The selected realisation as the designed cell <c>FilterPCell&lt;k&gt;</c> in the workspace (<see cref="PCellFilter.Make"/>,
    /// what <c>circuitrf filter --pcell</c> calls), on a worker; then the PCell Designer opened on it. The status names its
    /// parameters and the design check's errors.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMakePCell))]
    public void MakePCell()
    {
        if (_spec is not { } spec || _realisation is not { } realisation || Selected is not { } row || MakeRoot is not { } root) return;
        int k = 1;
        while (Directory.Exists(Path.Combine(root, $"FilterPCell{k}"))) k++;
        string name = $"FilterPCell{k}";
        Work = RunWork("Make PCell", async _ =>
        {
            var made = await Task.Run(() => PCellFilter.Make(root, name, spec, realisation, row.Name));
            LastPCell = made.CellDir;
            if (made.Refusal is { } why) return $"Make PCell: {why}";
            int errors = made.Findings.Count(f => f.Severity == CircuitRF.Diagnostics.DiagnosticSeverity.Error);
            OpenDesigner?.Invoke(made.CellDir!, root);
            return $"Made {name}: {made.Parameters.Count} parameters ({string.Join(", ", made.Parameters.Select(p => p.Name))}); "
                   + (errors == 0 ? "the design check passes." : $"the design check finds {errors} error(s): {made.Findings.First(f => f.Severity == CircuitRF.Diagnostics.DiagnosticSeverity.Error).Render()}");
        });
    }

    /// <summary>The cell folder the last Make PCell wrote, or null.</summary>
    public string? LastPCell { get; private set; }
}
