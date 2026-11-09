using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// The noise controls a loadpull and a loadpull pursuit share (brief NZ-6): <c>Noise=yes</c> adds NF [gridPoint, pinStep],
/// read at every converged rung. Off by default, and every key keeps its loaded spelling while its control is unchanged, so
/// opening and closing the dialog on an analysis without noise writes the line it always had.
/// </summary>
public sealed partial class LoadpullNoiseViewModel : ObservableObject
{
    /// <summary>The input picker's first row: the source tuner, whichever it is — <c>NoiseIn</c> left blank.</summary>
    public const string SourceTunerChoice = "Source tuner";
    /// <summary>The output picker's first row: the load tuner — <c>NoiseOut</c> left blank.</summary>
    public const string LoadTunerChoice = "Load tuner";

    private readonly SchematicEditModel _model;
    private ILoadpullNoiseKeys? _source;
    private string _prevUnit = "GHz";

    public static readonly string[] FreqUnits = FreqUnitHelper.Units;

    /// <summary><c>Noise=yes</c>.</summary>
    [ObservableProperty] private bool _noise;

    /// <summary><c>NoiseFreq=</c>'s coefficient; blank reads 1 % above the tone.</summary>
    [ObservableProperty] private string _noiseFreqCoeff = "";
    [ObservableProperty] private string _noiseFreqUnit = "GHz";
    [ObservableProperty] private string _noiseFreqPreview = "blank = 1 % above the tone";

    /// <summary>The input termination: <see cref="SourceTunerChoice"/>, a tuner's name, or a port number.</summary>
    [ObservableProperty] private string _noiseIn = SourceTunerChoice;
    /// <summary>The output termination: <see cref="LoadTunerChoice"/>, a tuner's name, or a port number.</summary>
    [ObservableProperty] private string _noiseOut = LoadTunerChoice;

    /// <summary><c>NoiseSideband=</c>: k, the input frequency being the output frequency plus k·f₀.</summary>
    [ObservableProperty] private string _noiseSidebandExpr = HarmonicBalanceAnalysis.NoiseSidebandDefault;

    /// <summary>Input picker rows: the source tuner, every tuner by name, every port number. Stable: built once, extended
    /// only by <see cref="Load"/> so a loaded value is never silently dropped.</summary>
    public List<string> InChoices { get; } = [SourceTunerChoice];
    /// <summary>Output picker rows, as <see cref="InChoices"/> with the load tuner first.</summary>
    public List<string> OutChoices { get; } = [LoadTunerChoice];

    public LoadpullNoiseViewModel(SchematicEditModel model, IEnumerable<string> tunerNames)
    {
        _model = model;
        var ports = model.Components
            .Where(c => ComponentTypeRegistry.OwnsUniquePortNum(c.Symbol))
            .Select(c => c.Parameters.FirstOrDefault(p => p.Name == "Num")?.Expression?.Trim())
            .Where(n => int.TryParse(n, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            .Select(n => n!).Distinct().OrderBy(n => int.Parse(n, CultureInfo.InvariantCulture)).ToList();
        var rest = tunerNames.Concat(ports).ToList();
        InChoices.AddRange(rest);
        OutChoices.AddRange(rest);
    }

    partial void OnNoiseFreqCoeffChanged(string value) => NoiseFreqPreview = Preview();

    partial void OnNoiseFreqUnitChanged(string value)
    {
        if (NoiseFreqCoeff.Trim().Length > 0) NoiseFreqCoeff = FreqUnitHelper.Rescale(NoiseFreqCoeff, _prevUnit, value);
        _prevUnit = value;
        NoiseFreqPreview = Preview();
    }

    private string Preview() => NoiseFreqCoeff.Trim().Length == 0
        ? "blank = 1 % above the tone"
        : AnalysisPreviewHelper.ComputeFreqPreview(NoiseFreqCoeff, NoiseFreqUnit, _model);

    // ── Build / load ──────────────────────────────────────────────────────────

    public string NoiseExpr => _source is { } s && s.Noise == Noise ? s.NoiseExpr : Noise ? "yes" : Analysis.NoiseDefault;
    public string NoiseFreqExpr => NoiseFreqCoeff.Trim();
    public string NoiseFreqUnitExpr => NoiseFreqCoeff.Trim().Length == 0 ? "Hz" : NoiseFreqUnit;
    public string NoiseInExpr  => NoiseIn  == SourceTunerChoice ? "" : NoiseIn;
    public string NoiseOutExpr => NoiseOut == LoadTunerChoice   ? "" : NoiseOut;

    /// <summary>Fill the controls from a loaded analysis. A value the pickers do not list joins them first.</summary>
    public void Load(ILoadpullNoiseKeys keys)
    {
        _source = keys;
        Noise = keys.Noise;
        string unit = keys.NoiseFreqUnit is { Length: > 0 } u ? u : "Hz";
        string coeff = keys.NoiseFreqExpr;
        if (coeff.Trim().Length == 0) unit = "GHz";
        else if (unit == "Hz") (coeff, unit) = FreqUnitHelper.Split(coeff);
        _prevUnit = unit;
        NoiseFreqUnit = unit;
        NoiseFreqCoeff = coeff;
        string i = keys.NoiseInExpr.Trim().Length == 0 ? SourceTunerChoice : keys.NoiseInExpr.Trim();
        string o = keys.NoiseOutExpr.Trim().Length == 0 ? LoadTunerChoice : keys.NoiseOutExpr.Trim();
        if (!InChoices.Contains(i)) InChoices.Add(i);
        if (!OutChoices.Contains(o)) OutChoices.Add(o);
        NoiseIn = i;
        NoiseOut = o;
        NoiseSidebandExpr = keys.NoiseSidebandExpr;
    }
}
