using System.Collections.Generic;
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

    public OscBodyViewModel(SchematicEditModel model)
    {
        Probes = [.. model.Components.Where(c => c.Symbol == SymbolKind.OscProbe).Select(c => c.InstanceName)
                                     .OrderBy(n => n, System.StringComparer.OrdinalIgnoreCase)];
        Probe = Probes.Count == 1 ? Probes[0] : null;
    }

    /// <summary>The body for an existing analysis.</summary>
    public static OscBodyViewModel From(OscillatorAnalysis a, SchematicEditModel model) => new(model)
    {
        Probe = a.Probe.Length > 0 ? a.Probe : (model.Components.Count(c => c.Symbol == SymbolKind.OscProbe) == 1
                    ? model.Components.First(c => c.Symbol == SymbolKind.OscProbe).InstanceName : null),
        Fguess = a.FguessExpr, Fstart = a.FstartExpr, Fstop = a.FstopExpr, Unit = a.Unit, MaxHarm = a.MaxHarmonicExpr,
        FftOverSample = a.FFTOverSampleExpr, Tol = a.TolExpr, Aguess = a.AguessExpr,
    };

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
    };

    private static string Blank(string s, string def) => string.IsNullOrWhiteSpace(s) ? def : s.Trim();
}
