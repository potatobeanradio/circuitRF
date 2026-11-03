// Export to Library's Parameters table (Tolerance column) and Corners table — brief vendorlib-13 R-vl13-1b, R-vl13-2c.
//
// A shell over ModelCellEdits in src/Design: what a spread means, how it is written as a distribution function, and every
// corner refusal live there. Both tables write the SOURCE cell's .ccell, so the next export keeps them, and say so.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Statistics;
using CircuitRF.Design.VendorLibraries.ModelLibrary;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ViewModels;

/// <summary>One parameter row: its nominal, and the tolerance typed in the Yield panel's grammar.</summary>
public sealed partial class ExportParameterRowViewModel : ObservableObject
{
    /// <summary>The distributions offered, in combo order: none, then the two that have a distribution function.</summary>
    public static IReadOnlyList<string> DistributionLabels { get; } = ["None", "Gaussian", "Uniform"];

    private readonly Action<ExportParameterRowViewModel> _commit;
    private bool _loading = true;

    public ExportParameterRowViewModel(ModelParameterRow row, Action<ExportParameterRowViewModel> commit)
    {
        _commit = commit;
        Name = row.Name;
        Nominal = row.Unit.Length > 0 ? $"{row.Nominal} {row.Unit}" : row.Nominal;
        _distributionIndex = row.Distribution switch { StatDistribution.Gauss => 1, StatDistribution.Unif => 2, _ => 0 };
        _spread = row.Unreadable is { } raw ? raw : row.Spread;
        _loading = false;
    }

    public string Name { get; }
    public string Nominal { get; }
    public string DistributionTip => ModelTolerance.NotOfferedReason;

    [ObservableProperty] private int _distributionIndex;
    [ObservableProperty] private string _spread;
    [ObservableProperty] private string _error = "";

    public StatDistribution Distribution => DistributionIndex switch { 1 => StatDistribution.Gauss, 2 => StatDistribution.Unif, _ => StatDistribution.None };
    public bool HasSpread => DistributionIndex != 0;

    partial void OnDistributionIndexChanged(int value)
    {
        OnPropertyChanged(nameof(HasSpread));
        if (_loading) return;
        // A distribution chosen with nothing typed takes the panel's own default spread; one that cannot read the old
        // spread is converted the panel's way.
        var old = ToleranceText.Parse(Spread, Distribution);
        var spread = ToleranceText.Convert(old, Distribution);
        _loading = true;
        Spread = value == 0 ? "" : ToleranceText.Format(Distribution, spread);
        _loading = false;
        _commit(this);
    }

    partial void OnSpreadChanged(string value)
    {
        if (!_loading) _commit(this);
    }
}

/// <summary>One value cell of the Corners table: a parameter, and the corner's value for it (blank = nominal).</summary>
public sealed partial class ExportCornerCellViewModel(string parameter, string value, Action changed) : ObservableObject
{
    public string Parameter { get; } = parameter;
    [ObservableProperty] private string _value = value;
    partial void OnValueChanged(string value) => changed();
}

/// <summary>One corner row: its name and a value per parameter.</summary>
public sealed partial class ExportCornerRowViewModel : ObservableObject
{
    private readonly Action _changed;

    public ExportCornerRowViewModel(string name, IEnumerable<string> parameters, IReadOnlyDictionary<string, string> values, Action changed)
    {
        _changed = changed;
        _name = name;
        foreach (var p in parameters) Cells.Add(new ExportCornerCellViewModel(p, values.GetValueOrDefault(p, ""), changed));
    }

    [ObservableProperty] private string _name;
    public ObservableCollection<ExportCornerCellViewModel> Cells { get; } = [];
    partial void OnNameChanged(string value) => _changed();

    public CcellCorner ToCorner() => new()
    {
        Name = Name,
        Values = Cells.ToDictionary(c => c.Parameter, c => c.Value ?? "", StringComparer.Ordinal),
    };
}

public sealed partial class ExportToLibraryViewModel
{
    /// <summary>The cell's parameters, each with its tolerance.</summary>
    public ObservableCollection<ExportParameterRowViewModel> ParameterRows { get; } = [];

    /// <summary>The Corners table's columns: the cell's parameter names.</summary>
    public ObservableCollection<string> ParameterNames { get; } = [];

    /// <summary>The Corners table's rows.</summary>
    public ObservableCollection<ExportCornerRowViewModel> CornerRows { get; } = [];

    /// <summary>"Written to …" after a table changed the cell; empty before.</summary>
    [ObservableProperty] private string _cellEditNote = "";

    /// <summary>Why the Corners table cannot be stored as it stands; empty when it can.</summary>
    [ObservableProperty] private string _cornerProblem = "";

    public bool HasParameters => ParameterRows.Count > 0;

    private bool _loadingTables;

    /// <summary>Reads both tables from the source cell — once, when the dialog opens.</summary>
    private void LoadTables()
    {
        _loadingTables = true;
        ParameterRows.Clear();
        ParameterNames.Clear();
        CornerRows.Clear();
        foreach (var row in ModelCellEdits.Parameters(CellDir))
        {
            ParameterRows.Add(new ExportParameterRowViewModel(row, CommitTolerance));
            ParameterNames.Add(row.Name);
        }
        foreach (var c in ModelCellEdits.Corners(CellDir))
            CornerRows.Add(new ExportCornerRowViewModel(c.Name, ParameterNames, c.Values, CommitCorners));
        _loadingTables = false;
        OnPropertyChanged(nameof(HasParameters));
    }

    private void CommitTolerance(ExportParameterRowViewModel row)
    {
        string? why = ModelCellEdits.SetTolerance(CellDir, row.Name, row.Distribution, row.Spread ?? "");
        row.Error = why ?? "";
        if (why is null) Changed();
    }

    private void CommitCorners()
    {
        if (_loadingTables) return;
        var corners = CornerRows.Select(r => r.ToCorner()).ToList();
        string? why = ModelCellEdits.SetCorners(CellDir, corners);
        CornerProblem = why ?? "";
        if (why is null) Changed();
    }

    private void Changed()
    {
        CellEditNote = $"Written to the cell {CellName}, so the next export keeps it.";
        Replan();
    }

    [RelayCommand]
    private void AddCorner()
    {
        var taken = CornerRows.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string name = new[] { "min", "max", "typ" }.FirstOrDefault(n => !taken.Contains(n))
                   ?? Enumerable.Range(1, 999).Select(i => $"corner{i}").First(n => !taken.Contains(n));
        CornerRows.Add(new ExportCornerRowViewModel(name, ParameterNames, new Dictionary<string, string>(), CommitCorners));
        CommitCorners();
    }

    [RelayCommand]
    private void RemoveCorner(ExportCornerRowViewModel? row)
    {
        if (row is null || !CornerRows.Remove(row)) return;
        CommitCorners();
    }
}

/// <summary>Text the Export dialog's XAML reads statically.</summary>
public static class ExportToLibraryDialogText
{
    /// <summary>The Tolerance column's tooltip: why only Gaussian and Uniform are offered.</summary>
    public static string ToleranceTip { get; } =
        ModelTolerance.NotOfferedReason + " A value inside the model that is not a parameter takes a function written in the value itself.";
}
