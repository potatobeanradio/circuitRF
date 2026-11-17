using System.Collections.ObjectModel;
using CircuitRF.Design.Filters;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Filters;

/// <summary>One row of the realization table — Series / Part are editable when library parts are loaded.</summary>
public sealed partial class FilterElementRow : ObservableObject
{
    private readonly FilterDesignerViewModel _host;
    private bool _suppress;

    /// <summary>The element name in the netlist.</summary>
    public string Name { get; }

    /// <summary>Series or shunt, and L or C.</summary>
    public string Kind { get; }

    /// <summary>The theoretical value, formatted.</summary>
    public string Exact { get; }

    /// <summary>The theoretical value in base SI.</summary>
    public double ExactValue { get; }

    /// <summary>True when this element is an inductor.</summary>
    public bool Inductor { get; }

    /// <summary>Row fill for the chain-budget zebra (#FFFFFF / #F0F0F0).</summary>
    public string RowBackground { get; }

    /// <summary>True when Series / Part are ComboBoxes rather than plain text.</summary>
    public bool LibraryEditable { get; }

    /// <summary>Series choices for this element kind (L or C).</summary>
    public ObservableCollection<FilterLibrarySeries> SeriesChoices { get; } = [];

    /// <summary>Parts of the selected series that match this element's value.</summary>
    public ObservableCollection<FilterPart> PartChoices { get; } = [];

    [ObservableProperty] private string _used = "";
    [ObservableProperty] private string _seriesName = "";
    [ObservableProperty] private string _part = "";
    [ObservableProperty] private string _tolerance = "";
    [ObservableProperty] private FilterLibrarySeries? _selectedSeries;
    [ObservableProperty] private FilterPart? _selectedPart;

    public FilterElementRow(
        FilterDesignerViewModel host,
        string name,
        string kind,
        string exact,
        double exactValue,
        bool inductor,
        string used,
        string seriesName,
        string part,
        string tolerance,
        string rowBackground,
        bool libraryEditable,
        FilterLibrarySeries? series,
        FilterPart? chosen,
        IEnumerable<FilterLibrarySeries> seriesChoices)
    {
        _host = host;
        Name = name;
        Kind = kind;
        Exact = exact;
        ExactValue = exactValue;
        Inductor = inductor;
        RowBackground = rowBackground;
        LibraryEditable = libraryEditable;
        _used = used;
        _seriesName = seriesName;
        _part = part;
        _tolerance = tolerance;

        _suppress = true;
        foreach (var s in seriesChoices) SeriesChoices.Add(s);
        SelectedSeries = series;
        RefreshPartChoices(chosen);
        _suppress = false;
    }

    partial void OnSelectedSeriesChanged(FilterLibrarySeries? value)
    {
        if (_suppress) return;
        SeriesName = value?.Label ?? "";
        RefreshPartChoices(preferred: null);
        if (SelectedPart is { } p)
            ApplyPart(p);
        else
            _host.OnRowPartChanged(this);
    }

    partial void OnSelectedPartChanged(FilterPart? value)
    {
        if (_suppress || value is null) return;
        ApplyPart(value);
    }

    private void ApplyPart(FilterPart part)
    {
        Part = part.Name;
        Tolerance = part.Tolerance;
        Used = _host.FormatElementValue(Inductor, part.Value);
        _host.OnRowPartChanged(this);
    }

    private void RefreshPartChoices(FilterPart? preferred)
    {
        PartChoices.Clear();
        if (SelectedSeries is null)
        {
            SelectedPart = null;
            return;
        }

        double target = preferred?.Value > 0 ? preferred.Value : ExactValue;
        foreach (var p in FilterValues.PartsAtValue(SelectedSeries, target))
            PartChoices.Add(p);

        FilterPart? pick = preferred;
        if (pick is null || !PartChoices.Contains(pick))
            pick = FilterValues.Nearest(ExactValue, PartChoices) ?? PartChoices.FirstOrDefault();

        _suppress = true;
        SelectedPart = pick;
        if (pick is not null)
        {
            Part = pick.Name;
            Tolerance = pick.Tolerance;
        }
        _suppress = false;
    }
}
