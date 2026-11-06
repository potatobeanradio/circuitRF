using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Em3d;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// brief-sar-9 — the Add/Edit Analysis dialog's body for a fields analysis: the EM Field Data block whose per-port fields are
/// recombined, the frequency (one its 3D run kept, or every one of a far field), the driving circuit port or ports and their weights (brief-sar-10), what the results are per watt of, and whether the
/// averaged SAR is computed. Every value is the directive's own text, as <see cref="FieldsAnalysis"/> stores it.
/// </summary>
public sealed partial class FieldsBodyViewModel : ObservableObject
{
    public static IReadOnlyList<string> FreqUnits { get; } = CircuitRF.Core.Expressions.FreqUnit.Units;
    public static IReadOnlyList<string> Norms { get; } = [FieldsAnalysis.NormAccepted, FieldsAnalysis.NormAvailable];

    /// <summary>What the Frequency control shows for <see cref="FieldsAnalysis.EveryFrequency"/>.</summary>
    public const string AllFrequencies = "All";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Frequencies), nameof(HasSarFields))]
    private string _block = "";

    /// <summary>brief-cosim-pattern-2 R-cp2-7 — the schematic's EM Field Data blocks by path, sub-cells included; the control still takes typed text.</summary>
    public IReadOnlyList<string> Blocks => _blocks ??= BlockSource?.Invoke() ?? [];
    private IReadOnlyList<string>? _blocks;

    /// <summary>Where <see cref="Blocks"/> comes from, asked only when a fields body is shown: listing them extracts the schematic.</summary>
    internal Func<IReadOnlyList<string>>? BlockSource { get; set; }

    /// <summary>Where each block's stored frequencies come from, asked once and only when they are shown: it extracts the schematic and reads records.</summary>
    internal Func<IReadOnlyList<EmFieldBlockData>>? DataSource { get; set; }
    private IReadOnlyList<EmFieldBlockData>? _data;
    private EmFieldBlockData? Named => (_data ??= DataSource?.Invoke() ?? []).FirstOrDefault(d => d.Path == Block.Trim());

    /// <summary>R-cp6-4 — the frequencies the named block's record stores, in <see cref="FreqUnit"/>, and <see cref="AllFrequencies"/> when it has a far field.</summary>
    public IReadOnlyList<string> Frequencies
    {
        get
        {
            if (Named is not { } block) return [];
            double unit = CircuitRF.Core.Expressions.FreqUnit.Multiplier(FreqUnit);
            var list = block.StoredHz.Select(hz => (hz / unit).ToString("G12", CultureInfo.InvariantCulture)).ToList();
            if (block.HasFarField) list.Add(AllFrequencies);
            return list;
        }
    }

    /// <summary>False only when the named block's record is read and holds no field set: norm and average then have nothing to act on.</summary>
    public bool HasSarFields => Named is not { HasFieldSet: false };

    [ObservableProperty] private string _freq = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Frequencies))]
    private string _freqUnit = "MHz";

    [ObservableProperty] private string _drive = "1";
    [ObservableProperty] private string _weights = "";
    [ObservableProperty] private string _norm = FieldsAnalysis.NormAccepted;
    [ObservableProperty] private bool _average = true;

    public static FieldsBodyViewModel From(FieldsAnalysis fa) => new()
    {
        Block = fa.Block, Freq = fa.AtEveryFrequency ? AllFrequencies : fa.FreqExpr, FreqUnit = fa.FreqUnit, Drive = fa.DriveExpr, Weights = fa.WeightsExpr,
        Norm = fa.PerAvailable ? FieldsAnalysis.NormAvailable : FieldsAnalysis.NormAccepted, Average = fa.Average,
    };

    /// <summary>A block and a frequency are what the directive cannot run without.</summary>
    public bool IsValid => Block.Trim().Length > 0 && Freq.Trim().Length > 0;

    public FieldsAnalysis BuildAnalysis(string name, bool enabled) => new(name)
    {
        Enabled = enabled, Block = Block.Trim(),
        FreqExpr = Freq.Trim().Equals(AllFrequencies, StringComparison.OrdinalIgnoreCase) ? FieldsAnalysis.EveryFrequency : Freq.Trim(), FreqUnit = FreqUnit,
        DriveExpr = Drive.Trim().Length > 0 ? Drive.Trim() : "1",
        WeightsExpr = Weights.Trim(), Norm = Norm, AverageExpr = Average ? "yes" : "no",
    };
}
