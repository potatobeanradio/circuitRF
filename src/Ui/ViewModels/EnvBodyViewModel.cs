using System;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Design.Schematic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// The Envelope analysis card body (type=env). Every field is the directive's own raw text, as the HB
/// body keeps it — expressions resolve at run time. The modulated stimulus is the schematic's MOD_Source:
/// the body only says how long, how accurately and how to split the work. What a run records is the
/// schematic's to say (brief ENV-MEAS-3): every labeled net's voltage and every IProbe's power, which the
/// card shows read-only.
/// </summary>
public sealed partial class EnvBodyViewModel : ObservableObject
{
    public static readonly string[] AccuracyOptions = ["draft", "standard", "fine"];
    public static readonly string[] AccuracyHelp =
    [
        "draft — 1x the signal rate: fastest; ACPR2 and bias-node memory read ~0.4 dB / 20 % off on a measured PA",
        "standard — 4x the signal rate: converged on every measured case (ACPR within 0.03 dB of 8x)",
        "fine — 8x the signal rate: reference runs",
    ];

    public static readonly string[] EnvelopeOptions = ["auto", "single", "lattice"];
    public static readonly string[] EnvelopeHelp =
    [
        "auto — carriers farther apart than 3x their bandwidth each get their own fundamental (a two-fundamental lattice); otherwise one envelope",
        "single — ONE envelope around Fc spans every carrier and their IM3: the whole multi-carrier signal in one time vector, at a step set by the spacing (slower when wide)",
        "lattice — as auto: widely spaced carriers get their own fundamentals and the mixing lattice up to the mix order",
    ];

    [ObservableProperty] private string _stop = "500e-6";
    [ObservableProperty] private string _envelope = "auto";
    [ObservableProperty] private string _accuracy = "standard";
    [ObservableProperty] private string _maxHarm = "5";
    [ObservableProperty] private string _maxMixOrder = "7";
    [ObservableProperty] private string _segments = "auto";
    [ObservableProperty] private string _warmup = "auto";
    [ObservableProperty] private bool _saveWaveforms = true;
    /// <summary><c>Noise=</c> (brief NZ-7): thermal noise injected from every noisy part.</summary>
    [ObservableProperty] private bool _noise;
    /// <summary><c>Seed=</c>: the seed of the thermal noise and of every source's phase-noise mask.</summary>
    [ObservableProperty] private string _seed = EnvelopeAnalysis.SeedDefault;
    /// <summary><c>SeedPerPoint=</c>: a new seed at each parametric sweep point.</summary>
    [ObservableProperty] private bool _seedPerPoint;
    /// <summary><c>EvmSplit=</c> (brief NZ-8): run the record again noiseless, to split EVM and measure the SNR.</summary>
    [ObservableProperty] private bool _evmSplit;

    /// <summary>What the schematic's MOD_Sources will play — read-only context for the card.</summary>
    public string StimulusSummary { get; }

    /// <summary>"Records 4 labeled nets and 3 IProbes": what a run keeps, counted on the schematic.</summary>
    public string RecordsSummary { get; }

    /// <summary>The nets and IProbes <see cref="RecordsSummary"/> counts, by name (its tooltip).</summary>
    public string RecordsDetail { get; }

    public string AccuracyDescription => AccuracyHelp[Math.Max(0, Array.IndexOf(AccuracyOptions, Accuracy))];

    partial void OnAccuracyChanged(string value) => OnPropertyChanged(nameof(AccuracyDescription));

    public string EnvelopeDescription => EnvelopeHelp[Math.Max(0, Array.IndexOf(EnvelopeOptions, Envelope))];

    partial void OnEnvelopeChanged(string value) => OnPropertyChanged(nameof(EnvelopeDescription));

    public EnvBodyViewModel(SchematicEditModel model)
    {
        var mods = model.Components.Where(c => c.Symbol == SymbolKind.ModSource).ToList();
        StimulusSummary = mods.Count == 0
            ? "No MOD_Source on the schematic — place one (Library ▸ Sources) to give the envelope its signal."
            : string.Join("\n", mods.Select(m =>
            {
                int n = m.Parameters.Count(p => p.Name.StartsWith("Carrier[", StringComparison.OrdinalIgnoreCase));
                var fc = m.Parameters.FirstOrDefault(p => p.Name == "Fc");
                return $"{m.InstanceName}: {Math.Max(1, n)} carrier(s) around {fc?.Expression} {fc?.Unit}".TrimEnd();
            }));

        // A net is labeled by naming it, or by a VProbe on it; ground is never one.
        var nets = model.NetLabels.Select(l => l.Name)
            .Concat(model.Components.Where(c => c.Symbol == SymbolKind.VProbe).Select(c => c.InstanceName))
            .Where(n => !string.IsNullOrWhiteSpace(n) && n != "0")
            .Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var probes = model.Components.Where(c => c.Symbol == SymbolKind.IProbe).Select(c => c.InstanceName)
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
        static string Count(int n, string one) => $"{n} {one}{(n == 1 ? "" : "s")}";
        RecordsSummary = $"Records {Count(nets.Count, "labeled net")} and {Count(probes.Count, "IProbe")}";
        RecordsDetail = (nets.Count > 0 ? "Voltages: " + string.Join(", ", nets) : "Voltages: no net is labeled, so the MOD_Source nodes") +
                        "\n" + (probes.Count > 0 ? "Power: " + string.Join(", ", probes)
                                                 : "Power: no IProbe. Place one in series with a load to measure its power.");
    }

    public static EnvBodyViewModel FromAnalysis(EnvelopeAnalysis a, SchematicEditModel model) => new(model)
    {
        Stop = a.StopExpr, Accuracy = a.Accuracy, MaxHarm = a.MaxHarmExpr, MaxMixOrder = a.MaxMixOrderExpr,
        Envelope = Array.IndexOf(EnvelopeOptions, a.Envelope.ToLowerInvariant()) >= 0 ? a.Envelope.ToLowerInvariant() : "auto",
        Segments = a.SegmentsExpr, Warmup = a.WarmupExpr,
        SaveWaveforms = !a.SaveWaveforms.Equals("no", StringComparison.OrdinalIgnoreCase),
        Noise = a.Noise, Seed = a.SeedExpr, SeedPerPoint = a.SeedPerPoint, EvmSplit = a.EvmSplit,
    };

    public EnvelopeAnalysis BuildAnalysis(string name, bool enabled) => new(name)
    {
        Enabled = enabled,
        StopExpr = Stop.Trim(), StepExpr = "auto", Accuracy = Accuracy, MaxHarmExpr = MaxHarm.Trim(),
        MaxMixOrderExpr = MaxMixOrder.Trim(),
        SegmentsExpr = Segments.Trim(), WarmupExpr = Warmup.Trim(), SaveWaveforms = SaveWaveforms ? "yes" : "no",
        Envelope = Envelope,
        NoiseExpr = Noise ? "yes" : Analysis.NoiseDefault,
        SeedExpr = string.IsNullOrWhiteSpace(Seed) ? EnvelopeAnalysis.SeedDefault : Seed.Trim(),
        SeedPerPointExpr = SeedPerPoint ? "yes" : Analysis.NoiseDefault,
        EvmSplitExpr = EvmSplit ? "yes" : Analysis.NoiseDefault,
    };
}
