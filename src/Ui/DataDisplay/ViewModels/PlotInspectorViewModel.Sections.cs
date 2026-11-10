// ================================================================
//  PlotInspectorViewModel.Sections.cs  —  Traces · Labels · Axes
//
//  The panel's three sections. Traces is what the panel always was. Labels
//  and Axes hold what had been reachable only from the plot's context menu
//  (Axes Labels…, Axes Limits): the title and axis labels, the limits and
//  each axis's autoscale, and the plot's hatched limit areas. Autoscale is
//  the plot's own, per axis — nothing here is display-wide.
// ================================================================

using System;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Text;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

/// <summary>Which of the Plot Inspector's sections is showing.</summary>
public enum InspectorSection { Traces, Labels, Axes }

public partial class PlotInspectorViewModel
{
    [ObservableProperty] private InspectorSection _section;

    partial void OnSectionChanged(InspectorSection value)
    {
        if (value != InspectorSection.Traces) RefreshAxesEditors();
        NotifySections();
    }

    /// <summary>The section strip is offered on a plot that has axes and labels: Rect, Smith and Polar.</summary>
    public bool HasAxesSections => _plot.PlotType is PlotType.Rect or PlotType.Smith or PlotType.Polar;

    public bool IsTracesSection => Section == InspectorSection.Traces || !HasAxesSections;
    public bool IsLabelsSection => Section == InspectorSection.Labels && HasAxesSections;
    public bool IsAxesSection   => Section == InspectorSection.Axes   && HasAxesSections;

    [RelayCommand] private void ShowTracesSection() => Section = InspectorSection.Traces;
    [RelayCommand] private void ShowLabelsSection() => Section = InspectorSection.Labels;
    [RelayCommand] private void ShowAxesSection()   => Section = InspectorSection.Axes;

    private void NotifySections()
    {
        OnPropertyChanged(nameof(HasAxesSections));
        OnPropertyChanged(nameof(IsTracesSection));
        OnPropertyChanged(nameof(IsLabelsSection));
        OnPropertyChanged(nameof(IsAxesSection));
        OnPropertyChanged(nameof(ShowLimitAreas));
    }

    // ---- The label and limit editors ------------------------------------------------------------

    private AxesLabelsViewModel? _labels;
    private AxesLimitsViewModel? _limits;

    /// <summary>The plot's title and axis labels — the editor the context menu's Axes Labels… opens.</summary>
    public AxesLabelsViewModel Labels => _labels ??= Attach(new AxesLabelsViewModel(_plot, () => { }));

    /// <summary>The plot's axis limits and per-axis autoscale — the editor the context menu's Axes Limits opens.</summary>
    public AxesLimitsViewModel Limits => _limits ??= Attach(new AxesLimitsViewModel(_plot, () => { }));

    private AxesLabelsViewModel Attach(AxesLabelsViewModel vm)
    {
        vm.PlotNeedsRedraw += OnSectionEdited;
        return vm;
    }

    private AxesLimitsViewModel Attach(AxesLimitsViewModel vm)
    {
        vm.PlotNeedsRedraw += OnSectionEdited;
        return vm;
    }

    private void OnSectionEdited(object? sender, EventArgs e) => PlotNeedsRedraw?.Invoke(this, EventArgs.Empty);

    /// <summary>Builds both editors afresh from the plot — after a plot-type change (Smith couples its axes) or
    /// an undo put the plot back under them.</summary>
    internal void RefreshAxesEditors()
    {
        if (_labels is not null) _labels.PlotNeedsRedraw -= OnSectionEdited;
        if (_limits is not null) _limits.PlotNeedsRedraw -= OnSectionEdited;
        _labels = null;
        _limits = null;
        OnPropertyChanged(nameof(Labels));
        OnPropertyChanged(nameof(Limits));
        RebuildLimitAreaRows();
        NotifySections();
    }

    /// <summary>The window moved under the limit boxes (an autoscale, a pan, new data): they show where it is now.</summary>
    internal void RefreshLimitTexts() => _limits?.RefreshFromPlot();

    /// <summary>Fits every axis to the data once, leaving each axis's Auto setting as it is.</summary>
    [RelayCommand]
    private void FitNow()
    {
        _plot.Autoscale(force: true);
        _limits?.RefreshFromPlot();
        PlotNeedsRedraw?.Invoke(this, EventArgs.Empty);
    }

    // ---- Hatched limit areas --------------------------------------------------------------------

    public ObservableCollection<LimitAreaRowViewModel> LimitAreaRows { get; } = new();

    /// <summary>Limit areas are drawn on a Rect plot.</summary>
    public bool ShowLimitAreas => IsRectPlot;

    /// <summary>The right-hand axis exists to draw an area against.</summary>
    public bool HasSecondaryAxis => _plot.Axes.ShowSecondary;

    internal void RebuildLimitAreaRows()
    {
        LimitAreaRows.Clear();
        foreach (var a in _plot.LimitAreas) LimitAreaRows.Add(new LimitAreaRowViewModel(a, this));
        OnPropertyChanged(nameof(HasSecondaryAxis));
    }

    /// <summary>
    /// A new area: a ceiling a quarter of the way down from the top of the window, rounded to two significant
    /// figures and across the whole X range — somewhere it can be seen, ready to be typed over.
    /// </summary>
    [RelayCommand]
    private void AddLimitArea()
    {
        var w = _plot.Axes.Window;
        double y = w.Top + 0.75 * (w.Bottom - w.Top);
        var area = new LimitArea { Side = LimitAreaSide.Above, Limit = RoundTo2(y) };
        _plot.LimitAreas.Add(area);
        LimitAreaRows.Add(new LimitAreaRowViewModel(area, this));
        PlotNeedsRedraw?.Invoke(this, EventArgs.Empty);
    }

    internal void RemoveLimitArea(LimitAreaRowViewModel row)
    {
        _plot.LimitAreas.Remove(row.Area);
        LimitAreaRows.Remove(row);
        PlotNeedsRedraw?.Invoke(this, EventArgs.Empty);
    }

    internal void OnLimitAreaEdited() => PlotNeedsRedraw?.Invoke(this, EventArgs.Empty);

    private static double RoundTo2(double v)
    {
        if (!double.IsFinite(v) || v == 0) return 0;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(Math.Abs(v))) - 1);
        return Math.Round(v / mag) * mag;
    }
}

/// <summary>One row of the Axes section's limit-area list.</summary>
public partial class LimitAreaRowViewModel : ObservableObject
{
    private readonly PlotInspectorViewModel _owner;

    public LimitArea Area { get; }

    public LimitAreaRowViewModel(LimitArea area, PlotInspectorViewModel owner)
    {
        Area   = area;
        _owner = owner;
    }

    /// <summary>The two sides a passing curve stays out of, in the order the picker lists them.</summary>
    public static string[] SideChoices { get; } = ["Max", "Min"];

    public int SideIndex
    {
        get => Area.Side == LimitAreaSide.Above ? 0 : 1;
        set
        {
            var side = value == 1 ? LimitAreaSide.Below : LimitAreaSide.Above;
            if (side == Area.Side) return;
            Area.Side = side;
            OnPropertyChanged();
            _owner.OnLimitAreaEdited();
        }
    }

    public string LimitText
    {
        get => Format(Area.Limit);
        set
        {
            if (!TryParse(value, out double v) || v == Area.Limit) return;
            Area.Limit = v;
            _owner.OnLimitAreaEdited();
        }
    }

    /// <summary>Where the area starts; empty runs it from the plot's left edge.</summary>
    public string FromText
    {
        get => double.IsFinite(Area.XStart) ? Format(Area.XStart) : "";
        set
        {
            double v = double.NaN;
            if (!string.IsNullOrWhiteSpace(value) && !TryParse(value, out v)) return;
            if (v.Equals(Area.XStart)) return;
            Area.XStart = v;
            _owner.OnLimitAreaEdited();
        }
    }

    /// <summary>Where the area ends; empty runs it to the plot's right edge.</summary>
    public string ToText
    {
        get => double.IsFinite(Area.XEnd) ? Format(Area.XEnd) : "";
        set
        {
            double v = double.NaN;
            if (!string.IsNullOrWhiteSpace(value) && !TryParse(value, out v)) return;
            if (v.Equals(Area.XEnd)) return;
            Area.XEnd = v;
            _owner.OnLimitAreaEdited();
        }
    }

    public bool OnSecondaryAxis
    {
        get => Area.OnSecondaryAxis;
        set
        {
            if (value == Area.OnSecondaryAxis) return;
            Area.OnSecondaryAxis = value;
            OnPropertyChanged();
            _owner.OnLimitAreaEdited();
        }
    }

    public bool ShowSecondaryToggle => _owner.HasSecondaryAxis;

    [RelayCommand] private void Remove() => _owner.RemoveLimitArea(this);

    private static string Format(double v) => v.ToString("G6", CultureInfo.InvariantCulture);

    private static bool TryParse(string? text, out double v)
        => NumericText.TryParseDouble(text ?? "", out v) && double.IsFinite(v);
}
