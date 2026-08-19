// ================================================================
//  AxesLimitsViewModel.cs
//
//  Flyout ViewModel for manually setting axis window limits.
//  Bound to AxesLimitsView.axaml.
//
//  Supports Rect plots (separate X, Y, Y2 autoscale flags) and
//  Smith/Polar plots (single AutoscaleMag flag tied to both X and Y).
//
//  Text fields update the plot in real-time as the user types.
//  Invalid input is silently ignored — no error messages are shown.
// ================================================================

using System;
using System.Globalization;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Ui.DataDisplay;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

public partial class AxesLimitsViewModel : ViewModelBase
{
    private readonly Plot   _plot;
    private readonly Action _close;
    // Prevents TryApply* from firing during programmatic text refresh.
    private bool _suppressApply;

    public event EventHandler? PlotNeedsRedraw;

    // ---- Plot type helpers -----------------------------------------------

    public bool   IsRect     => _plot.PlotType.IsRect();
    public bool   IsComplex  => _plot.PlotType.IsComplex();

    /// <summary>Unit suffix shown next to the X Axis header on Rect plots — the X quantity's own
    /// unit, not an assumed frequency (see <see cref="Plot.XAxisUnitLabel"/>).</summary>
    public string XUnitLabel => _plot.XAxisUnitLabel;

    /// <summary>True when the Y2 (secondary right) axis section should be visible.</summary>
    public bool ShowY2 => IsRect && _plot.Axes.ShowSecondary;

    // ---- X axis ----------------------------------------------------------

    [ObservableProperty] private string _xMinText = "";
    [ObservableProperty] private string _xMaxText = "";
    [ObservableProperty] private bool   _xAutoscale;

    public bool XFieldsEnabled => !XAutoscale;

    partial void OnXMinTextChanged(string value) => TryApplyX();
    partial void OnXMaxTextChanged(string value) => TryApplyX();

    partial void OnXAutoscaleChanged(bool value)
    {
        if (_suppressApply) return;
        if (IsComplex)
            SetBothAxesAutoscale(value);
        else
        {
            _plot.AutoscaleX = value;
            OnPropertyChanged(nameof(XFieldsEnabled));
            if (value) RefreshXText();
            RaiseRedraw();
        }
    }

    // ---- Y axis (primary left) -------------------------------------------

    [ObservableProperty] private string _yMinText = "";
    [ObservableProperty] private string _yMaxText = "";
    [ObservableProperty] private bool   _yAutoscale;

    public bool YFieldsEnabled => !YAutoscale;

    partial void OnYMinTextChanged(string value) => TryApplyY();
    partial void OnYMaxTextChanged(string value) => TryApplyY();

    partial void OnYAutoscaleChanged(bool value)
    {
        if (_suppressApply) return;
        if (IsComplex)
            SetBothAxesAutoscale(value);
        else
        {
            _plot.AutoscaleY = value;
            OnPropertyChanged(nameof(YFieldsEnabled));
            if (value) RefreshYText();
            RaiseRedraw();
        }
    }

    // ---- Y2 axis (secondary right) ---------------------------------------

    [ObservableProperty] private string _y2MinText = "";
    [ObservableProperty] private string _y2MaxText = "";
    [ObservableProperty] private bool   _y2Autoscale;

    public bool Y2FieldsEnabled => !Y2Autoscale;

    partial void OnY2MinTextChanged(string value) => TryApplyY2();
    partial void OnY2MaxTextChanged(string value) => TryApplyY2();

    partial void OnY2AutoscaleChanged(bool value)
    {
        if (_suppressApply) return;
        _plot.AutoscaleRightY = value;
        OnPropertyChanged(nameof(Y2FieldsEnabled));
        if (value) RefreshY2Text();
        RaiseRedraw();
    }

    // ---- Constructor -----------------------------------------------------

    public AxesLimitsViewModel(Plot plot, Action close)
    {
        _plot  = plot;
        _close = close;

        // Initialise backing fields directly to avoid triggering partial callbacks
        // before the object is fully constructed.
        _suppressApply = true;
        try
        {
#pragma warning disable MVVMTK0034
            _xAutoscale  = IsComplex ? _plot.AutoscaleMag    : _plot.AutoscaleX;
            _yAutoscale  = IsComplex ? _plot.AutoscaleMag    : _plot.AutoscaleY;
            _y2Autoscale = _plot.AutoscaleRightY;
            _xMinText    = FormatValue(_plot.Axes.Window.Left);
            _xMaxText    = FormatValue(_plot.Axes.Window.Right);
            _yMinText    = FormatValue(_plot.Axes.Window.Top);
            _yMaxText    = FormatValue(_plot.Axes.Window.Bottom);
            _y2MinText   = FormatValue(_plot.Axes.WindowSecondary.Top);
            _y2MaxText   = FormatValue(_plot.Axes.WindowSecondary.Bottom);
#pragma warning restore MVVMTK0034
        }
        finally { _suppressApply = false; }
    }

    [RelayCommand]
    private void Close() => _close();

    // ---- Private apply logic ---------------------------------------------

    private void TryApplyX()
    {
        if (_suppressApply || XAutoscale) return;
        if (!TryParse(XMinText, out double xMin) || !TryParse(XMaxText, out double xMax)) return;
        if (Math.Abs(xMax - xMin) < 1e-15) return;

        if (IsComplex)
        {
            ApplySquareFromEditedAxis(xMin, xMax, editedIsX: true);
            return;
        }

        var w = _plot.Axes.Window;
        _plot.Axes.Window      = new Rect(Math.Min(xMin, xMax), w.Y, Math.Abs(xMax - xMin), w.Height);
        _plot.Axes.WindowState = _plot.Axes.Window;

        // On Rect plots the secondary axis shares the X range.
        if (IsRect && _plot.Axes.ShowSecondary)
        {
            var ws = _plot.Axes.WindowSecondary;
            _plot.Axes.WindowSecondary      = new Rect(Math.Min(xMin, xMax), ws.Y, Math.Abs(xMax - xMin), ws.Height);
            _plot.Axes.WindowSecondaryState = _plot.Axes.WindowSecondary;
        }

        RaiseRedraw();
    }

    private void TryApplyY()
    {
        if (_suppressApply || YAutoscale) return;
        if (!TryParse(YMinText, out double yMin) || !TryParse(YMaxText, out double yMax)) return;
        if (Math.Abs(yMax - yMin) < 1e-15) return;

        if (IsComplex)
        {
            ApplySquareFromEditedAxis(yMin, yMax, editedIsX: false);
            return;
        }

        var w = _plot.Axes.Window;
        _plot.Axes.Window      = new Rect(w.X, Math.Min(yMin, yMax), w.Width, Math.Abs(yMax - yMin));
        _plot.Axes.WindowState = _plot.Axes.Window;
        RaiseRedraw();
    }

    /// <summary>
    /// Smith/Polar (brief-dd-plot-type-integrity.md §3): a manual edit of ONE axis still yields a
    /// square window centred at the origin — reuses <see cref="Plot.SquareCentredOnOrigin"/>, the
    /// SAME helper <see cref="Plot.Autoscale"/> uses, so a manual edit followed by an autoscale can
    /// never jump between two different notions of "square". The edited axis's span is applied as
    /// the square's span (its own extent on both sides of the origin, even if asymmetric); the other
    /// axis is whatever that square implies — both text boxes are refreshed so the user sees the
    /// coupled value immediately.
    /// </summary>
    private void ApplySquareFromEditedAxis(double lo, double hi, bool editedIsX)
    {
        var edited = editedIsX
            ? new Rect(Math.Min(lo, hi), 0, Math.Abs(hi - lo), 0)
            : new Rect(0, Math.Min(lo, hi), 0, Math.Abs(hi - lo));

        var square = Plot.SquareCentredOnOrigin(edited);
        _plot.Axes.Window      = square;
        _plot.Axes.WindowState = square;

        RefreshXText();
        RefreshYText();
        RaiseRedraw();
    }

    private void TryApplyY2()
    {
        if (_suppressApply || Y2Autoscale) return;
        if (!TryParse(Y2MinText, out double y2Min) || !TryParse(Y2MaxText, out double y2Max)) return;
        if (Math.Abs(y2Max - y2Min) < 1e-15) return;

        var ws = _plot.Axes.WindowSecondary;
        _plot.Axes.WindowSecondary      = new Rect(ws.X, Math.Min(y2Min, y2Max), ws.Width, Math.Abs(y2Max - y2Min));
        _plot.Axes.WindowSecondaryState = _plot.Axes.WindowSecondary;
        RaiseRedraw();
    }

    // ---- Smith/Polar: X and Y share AutoscaleMag ------------------------

    /// <summary>
    /// Sets AutoscaleMag and syncs both XAutoscale and YAutoscale backing fields
    /// without re-triggering their partial callbacks (avoids infinite recursion).
    /// </summary>
    private void SetBothAxesAutoscale(bool value)
    {
        _plot.AutoscaleMag = value;

        // Update sibling field via backing field to avoid re-entering this method.
        bool savedSuppress = _suppressApply;
        _suppressApply = true;
        try
        {
#pragma warning disable MVVMTK0034
            if (_xAutoscale != value) { _xAutoscale = value; OnPropertyChanged(nameof(XAutoscale)); }
            if (_yAutoscale != value) { _yAutoscale = value; OnPropertyChanged(nameof(YAutoscale)); }
#pragma warning restore MVVMTK0034
        }
        finally { _suppressApply = savedSuppress; }

        OnPropertyChanged(nameof(XFieldsEnabled));
        OnPropertyChanged(nameof(YFieldsEnabled));

        if (value) { RefreshXText(); RefreshYText(); }
        RaiseRedraw();
    }

    // ---- Refresh text fields from current model state -------------------
    // Save-and-restore _suppressApply so nested calls from constructors or
    // SetBothAxesAutoscale don't prematurely re-enable TryApply*.

    private void RefreshXText()
    {
        bool saved = _suppressApply;
        _suppressApply = true;
        try
        {
            XMinText = FormatValue(_plot.Axes.Window.Left);
            XMaxText = FormatValue(_plot.Axes.Window.Right);
        }
        finally { _suppressApply = saved; }
    }

    private void RefreshYText()
    {
        bool saved = _suppressApply;
        _suppressApply = true;
        try
        {
            YMinText = FormatValue(_plot.Axes.Window.Top);
            YMaxText = FormatValue(_plot.Axes.Window.Bottom);
        }
        finally { _suppressApply = saved; }
    }

    private void RefreshY2Text()
    {
        bool saved = _suppressApply;
        _suppressApply = true;
        try
        {
            Y2MinText = FormatValue(_plot.Axes.WindowSecondary.Top);
            Y2MaxText = FormatValue(_plot.Axes.WindowSecondary.Bottom);
        }
        finally { _suppressApply = saved; }
    }

    // ---- Helpers --------------------------------------------------------

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value) &&
        double.IsFinite(value);

    private static string FormatValue(double v) =>
        v.ToString("G6", CultureInfo.InvariantCulture);

    private void RaiseRedraw() => PlotNeedsRedraw?.Invoke(this, EventArgs.Empty);
}
