using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    [NotifyPropertyChangedFor(nameof(Frequencies), nameof(HasSarFields), nameof(FreqProblem))]
    private string _block = "";

    /// <summary>brief-cosim-pattern-2 R-cp2-7 — the schematic's EM Field Data blocks by path, sub-cells included; the control still takes typed text.</summary>
    public IReadOnlyList<string> Blocks
    {
        get
        {
            _blocks ??= BlockSource?.Invoke() ?? [];
            // A block a file names and the schematic no longer has stays on show: the run refuses it by name.
            return Block.Trim() is { Length: > 0 } named && !_blocks.Contains(named) ? [.. _blocks, named] : _blocks;
        }
    }
    private IReadOnlyList<string>? _blocks;

    /// <summary>With one block there is nothing to choose.</summary>
    public bool CanChooseBlock => Blocks.Count > 1;

    /// <summary>A new analysis takes the first block. Called when the body is shown, since listing the blocks extracts the schematic.</summary>
    internal void PickBlock()
    {
        if (Block.Trim().Length == 0 && Blocks.FirstOrDefault() is { } first) Block = first;
        OnPropertyChanged(nameof(Blocks));
        OnPropertyChanged(nameof(CanChooseBlock));
    }

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FreqProblem))]
    private string _freq = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Frequencies), nameof(FreqProblem))]
    private string _freqUnit = "MHz";

    /// <summary>What the run would refuse, said while it can still be changed: a number the named block's record stores no result at.
    /// An expression, or a block whose record cannot be read, is the run's to judge. It does not stop the dialog closing, since the
    /// EM run may be repeated to add that frequency.</summary>
    public string? FreqProblem
    {
        get
        {
            if (Named is not { StoredHz.Count: > 0 } block
                || !double.TryParse(Freq.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) return null;
            double hz = value * CircuitRF.Core.Expressions.FreqUnit.Multiplier(FreqUnit);
            return block.StoredHz.Any(f => Math.Abs(f - hz) <= 1e-9 * Math.Max(f, hz)) ? null : "No result at this frequency: pick one from the list.";
        }
    }

    /// <summary>The ports this analysis drives, each with its amplitude: <c>drive=</c> and <c>weights=</c>, a row at a time, so that
    /// removing one cannot move another's amplitude onto the wrong port.</summary>
    public ObservableCollection<FieldDriveRowViewModel> Drives { get; } = [];

    private IReadOnlyList<FieldDriver> _drivers = [];

    public FieldsBodyViewModel() => Drives.Add(new FieldDriveRowViewModel(this, "1", ""));

    private void SetDrives(string driveExpr, string weightsExpr)
    {
        var drives = CircuitRF.Design.Em3d.FieldCosimulation.SplitList(driveExpr);
        var weights = CircuitRF.Design.Em3d.FieldCosimulation.SplitList(weightsExpr, keepEmpty: true);
        Drives.Clear();
        for (int i = 0; i < drives.Count; i++) Drives.Add(new FieldDriveRowViewModel(this, drives[i], i < weights.Count ? weights[i] : ""));
        if (Drives.Count == 0) Drives.Add(new FieldDriveRowViewModel(this, "1", ""));
    }

    /// <summary>Gives the rows the schematic's drivers, and shows a drive the file states by number or name as its entry.
    /// <paramref name="choose"/> says the analysis is new, so its one row starts on the first port rather than on a number.</summary>
    internal void UseDrivers(IReadOnlyList<FieldDriver> drivers, bool choose = false)
    {
        _drivers = drivers;
        if (choose && drivers.FirstOrDefault(d => d.Problem is null) is { } first) Drives[0].Driver = first.Label;
        var labels = drivers.Select(d => d.Label).ToList();
        foreach (var row in Drives)
        {
            string shown = DriverOf(row.Driver)?.Label ?? row.Driver;
            row.SetItems(labels.Contains(shown) ? labels : [.. labels, shown]);
            row.Driver = shown;
        }
        DrivesChanged();
    }

    private FieldDriver? DriverOf(string text)
    {
        string t = text.Trim();
        return _drivers.FirstOrDefault(d => d.Label == t || d.Value == t || d.Instance == t);
    }

    private string ValueOf(FieldDriveRowViewModel row) => DriverOf(row.Driver)?.Value ?? row.Driver.Trim();

    /// <summary>The next port no row drives; a new row starts there, and with none left there is nothing to add.</summary>
    private FieldDriver? NextPort => _drivers.FirstOrDefault(d => d.Problem is null && Drives.All(r => ValueOf(r) != d.Value));

    private bool CanAddDrive => NextPort is not null;

    [RelayCommand(CanExecute = nameof(CanAddDrive))]
    private void AddDrive()
    {
        if (NextPort is not { } port) return;
        var row = new FieldDriveRowViewModel(this, port.Label, "");
        row.SetItems([.. _drivers.Select(d => d.Label)]);
        Drives.Add(row);
        DrivesChanged();
    }

    internal void RemoveDrive(FieldDriveRowViewModel row)
    {
        if (Drives.Count > 1 && Drives.Remove(row)) DrivesChanged();
    }

    internal void DrivesChanged()
    {
        foreach (var row in Drives) row.Refresh();
        AddDriveCommand.NotifyCanExecuteChanged();
    }

    internal bool HasPavl(FieldDriveRowViewModel row) => DriverOf(row.Driver) is { HasPavl: true };

    private bool TakesPavl(FieldDriveRowViewModel row) => HasPavl(row) && row.Weight.Trim().Length == 0;

    /// <summary>What a run would refuse about a row: a source that is not a port, a port driven twice, a Pavl with nothing to compare it with, or a number no port has. An
    /// expression, or a schematic with no ports to compare with, is the run's to judge. None of it stops the dialog closing.</summary>
    internal string? ProblemOf(FieldDriveRowViewModel row)
    {
        if (DriverOf(row.Driver) is { Problem: { } refused }) return refused;
        int at = Drives.IndexOf(row);
        if (Drives.Take(Math.Max(at, 0)).Any(r => ValueOf(r) == ValueOf(row))) return "This port is already driven above.";
        if (TakesPavl(row) && !Drives.All(TakesPavl)) return "Pavl can't be compared with the others. Set every amplitude.";
        return DriverOf(row.Driver) is null && _drivers.Any(d => d.Problem is null)
               && int.TryParse(row.Driver.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int port)
            ? $"This schematic has no port {port}." : null;
    }

    [ObservableProperty] private string _norm = FieldsAnalysis.NormAccepted;
    [ObservableProperty] private bool _average = true;

    public static FieldsBodyViewModel From(FieldsAnalysis fa)
    {
        var body = new FieldsBodyViewModel
        {
            Block = fa.Block, Freq = fa.AtEveryFrequency ? AllFrequencies : fa.FreqExpr, FreqUnit = fa.FreqUnit,
            Norm = fa.PerAvailable ? FieldsAnalysis.NormAvailable : FieldsAnalysis.NormAccepted, Average = fa.Average,
        };
        body.SetDrives(fa.DriveExpr, fa.WeightsExpr);
        return body;
    }

    /// <summary>A block and a frequency are what the directive cannot run without.</summary>
    public bool IsValid => Block.Trim().Length > 0 && Freq.Trim().Length > 0;

    public FieldsAnalysis BuildAnalysis(string name, bool enabled) => new(name)
    {
        Enabled = enabled, Block = Block.Trim(),
        FreqExpr = Freq.Trim().Equals(AllFrequencies, StringComparison.OrdinalIgnoreCase) ? FieldsAnalysis.EveryFrequency : Freq.Trim(), FreqUnit = FreqUnit,
        DriveExpr = string.Join(",", Drives.Select(ValueOf)),
        WeightsExpr = Drives.Any(r => r.Weight.Trim().Length > 0) ? string.Join(",", Drives.Select(r => r.Weight.Trim())) : "",
        Norm = Norm, AverageExpr = Average ? "yes" : "no",
    };
}
