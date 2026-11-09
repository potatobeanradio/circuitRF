using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CircuitRF.Core.Design;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// Brief NZ-9 — the oscillator analysis's body in the Analysis dialog: the OscProbe its generator goes at, where the start-up
/// search looks, and the harmonic-balance keys it shares with HB.
/// </summary>
public sealed partial class OscBodyViewModel : ObservableObject
{
    /// <summary>The frequency units the three frequency boxes share.</summary>
    public static IReadOnlyList<string> Units { get; } = ["Hz", "kHz", "MHz", "GHz"];

    /// <summary>The OscProbes on this bench, the choices for Probe.</summary>
    public IReadOnlyList<string> Probes { get; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Problem))] private string? _probe;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Problem), nameof(IsValid))] private string _fguess = "1";
    [ObservableProperty] private string _fstart = "";
    [ObservableProperty] private string _fstop = "";
    [ObservableProperty] private string _unit = "GHz";
    [ObservableProperty] private string _maxHarm = "7";
    [ObservableProperty] private string _fftOverSample = "1";
    [ObservableProperty] private string _tol = "1e-6";
    [ObservableProperty] private string _aguess = "";

    // Brief NZ-10: phase noise.
    [ObservableProperty] private bool _phaseNoise;
    [ObservableProperty] private string _pnStart = OscillatorAnalysis.DefaultPnStart;
    [ObservableProperty] private string _pnStop = OscillatorAnalysis.DefaultPnStop;
    [ObservableProperty] private string _pnNpts = OscillatorAnalysis.DefaultPnNpts;
    [ObservableProperty] private bool _pnLog = true;
    [ObservableProperty] private string _pnPort = DefaultReadAt;

    /// <summary>The PnPort picker's first row: blank in the directive — across a shunt OscProbe, or at the one port.</summary>
    public const string DefaultReadAt = "OscProbe or only port";

    /// <summary>Where the phase noise may be read: the default, then every numbered Port and Term on the bench.</summary>
    public List<string> PnPortChoices { get; } = [DefaultReadAt];

    public OscBodyViewModel(SchematicEditModel model)
    {
        Probes = [.. model.Components.Where(c => c.Symbol == SymbolKind.OscProbe).Select(c => c.InstanceName)
                                     .OrderBy(n => n, System.StringComparer.OrdinalIgnoreCase)];
        Probe = Probes.Count == 1 ? Probes[0] : null;
        PnPortChoices.AddRange(model.Components
            .Where(c => ComponentTypeRegistry.OwnsUniquePortNum(c.Symbol))
            .Select(c => c.Parameters.FirstOrDefault(p => p.Name == "Num")?.Expression?.Trim())
            .Where(n => int.TryParse(n, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            .Select(n => n!).Distinct().OrderBy(n => int.Parse(n, CultureInfo.InvariantCulture)));
    }

    /// <summary>The body for an existing analysis.</summary>
    public static OscBodyViewModel From(OscillatorAnalysis a, SchematicEditModel model) => new OscBodyViewModel(model)
    {
        Probe = a.Probe.Length > 0 ? a.Probe : (model.Components.Count(c => c.Symbol == SymbolKind.OscProbe) == 1
                    ? model.Components.First(c => c.Symbol == SymbolKind.OscProbe).InstanceName : null),
        Fguess = a.FguessExpr, Fstart = a.FstartExpr, Fstop = a.FstopExpr, Unit = a.Unit, MaxHarm = a.MaxHarmonicExpr,
        FftOverSample = a.FFTOverSampleExpr, Tol = a.TolExpr, Aguess = a.AguessExpr,
        PhaseNoise = a.PhaseNoise, PnStart = a.PnStartExpr, PnStop = a.PnStopExpr, PnNpts = a.PnNptsExpr, PnLog = a.PnLog,
        PnPort = a.PnPortExpr.Trim().Length > 0 ? a.PnPortExpr.Trim() : DefaultReadAt,
    }.WithPortChoice(a.PnPortExpr.Trim());

    /// <summary>A PnPort the bench no longer numbers stays selectable, so opening the dialog never rewrites it.</summary>
    private OscBodyViewModel WithPortChoice(string port)
    {
        if (port.Length > 0 && !PnPortChoices.Contains(port)) PnPortChoices.Add(port);
        return this;
    }

    /// <summary>What the run would refuse, or null. Shown, not blocking: a probe may be placed after the analysis is added.</summary>
    public string? Problem =>
        string.IsNullOrWhiteSpace(Fguess) ? "Give Fguess, where the start-up search is centred."
        : Probes.Count == 0 ? "Place an OscProbe in the oscillator's loop, or across its resonator with Mode=shunt."
        : Probes.Count > 1 && string.IsNullOrWhiteSpace(Probe) ? "Choose the OscProbe the generator goes at."
        : null;

    public bool IsValid => !string.IsNullOrWhiteSpace(Fguess);

    public OscillatorAnalysis BuildAnalysis(string name, bool enabled) => new(name)
    {
        Enabled = enabled,
        // One probe on the bench is the one the run uses, so it is not written; several need the choice stated.
        Probe = Probes.Count > 1 ? (Probe ?? "").Trim() : "",
        FguessExpr = Fguess.Trim(), FstartExpr = Fstart.Trim(), FstopExpr = Fstop.Trim(), Unit = Unit,
        MaxHarmonicExpr = Blank(MaxHarm, "7"), FFTOverSampleExpr = Blank(FftOverSample, "1"), TolExpr = Blank(Tol, "1e-6"),
        AguessExpr = Aguess.Trim(),
        PhaseNoise = PhaseNoise, PnStartExpr = Blank(PnStart, OscillatorAnalysis.DefaultPnStart),
        PnStopExpr = Blank(PnStop, OscillatorAnalysis.DefaultPnStop), PnNptsExpr = Blank(PnNpts, OscillatorAnalysis.DefaultPnNpts),
        PnLog = PnLog, PnPortExpr = PnPort == DefaultReadAt ? "" : (PnPort ?? "").Trim(),
    };

    private static string Blank(string s, string def) => string.IsNullOrWhiteSpace(s) ? def : s.Trim();
}
