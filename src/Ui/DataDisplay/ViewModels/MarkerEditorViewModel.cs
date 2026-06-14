// ================================================================
//  MarkerEditorViewModel.cs
//
//  Thin wrapper that exposes Marker model properties as observable
//  two-way bindings for the compact marker editor flyout.
//  Writing any property updates the model directly; the caller
//  (MarkerInfoBoxViewModel) is notified via the Changed event so
//  it can refresh the info box content.
//
//  Frequency entry is buffered in FreqDisplayText and only committed
//  to the model when CommitFrequency() is called (user presses Enter).
//  The committed value is snapped to the nearest frequency in the
//  trace's data set.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RfCore;
using CircuitRF.Ui.DataDisplay;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

/// <summary>One row in the multi-marker section of the MarkerEditorView.</summary>
public sealed class MultiTraceLineItem
{
    public string DataText { get; init; } = "";
    public string Z0Text   { get; init; } = "";
}

public partial class MarkerEditorViewModel : ViewModelBase
{
    private readonly Marker                _marker;
    private readonly MarkerInfoBoxViewModel _parent;

    public event EventHandler? MarkerChanged;

    // ---- Picker lists ---------------------------------------------------

    public static IReadOnlyList<MarkerStyle>     AllStyles          { get; } = Enum.GetValues<MarkerStyle>().ToList();
    public static IReadOnlyList<MatrixFormat>    AllFormats         { get; } = Enum.GetValues<MatrixFormat>().ToList();
    public static IReadOnlyList<PrecisionFormat> AllPrecisionFormats{ get; } = Enum.GetValues<PrecisionFormat>().ToList();

    // ---- Marker name ----------------------------------------------------

    [ObservableProperty]
    private string _name;

    partial void OnNameChanged(string value)
    {
        _marker.Name = value;
        NotifyParent();
    }

    // ---- Frequency (edit buffer — committed to model on Enter) ----------
    //  FreqDisplayText is the raw string the user types; it is NOT written
    //  to Marker.Freq until CommitFrequency() is called.  That call also
    //  snaps the value to the nearest supported frequency.

    [ObservableProperty]
    private string _freqDisplayText = string.Empty;

    public void CommitFrequency()
    {
        if (!double.TryParse(FreqDisplayText, NumberStyles.Any,
                             CultureInfo.CurrentCulture, out double val))
            return;

        double freqHz  = val / _marker.FreqUnits.Scale();
        double snapped = SnapToNearestFreq(freqHz);
        _marker.Freq   = snapped;

        // Reflect the snapped (possibly different) value back to the text field.
        FreqDisplayText = (snapped * _marker.FreqUnits.Scale()).ToString("G6");
        NotifyParent();
    }

    private double SnapToNearestFreq(double freqHz)
    {
        var freqs = _parent.Trace.Data?.Frequencies;
        if (freqs is null || freqs.Length == 0) return freqHz;

        double best     = freqs[0];
        double bestDiff = Math.Abs(freqHz - freqs[0]);
        for (int i = 1; i < freqs.Length; i++)
        {
            double d = Math.Abs(freqHz - freqs[i]);
            if (d < bestDiff) { bestDiff = d; best = freqs[i]; }
        }
        return best;
    }

    // ---- Matrix format --------------------------------------------------

    [ObservableProperty]
    private MatrixFormat _matrixFormat;

    partial void OnMatrixFormatChanged(MatrixFormat value)
    {
        _marker.MatrixFormat = value;
        NotifyParent();
    }

    // ---- Marker style ---------------------------------------------------

    [ObservableProperty]
    private MarkerStyle _style;

    partial void OnStyleChanged(MarkerStyle value)
    {
        _marker.Style = value;
        NotifyParent();
    }

    // ---- Precision digits -----------------------------------------------

    [ObservableProperty]
    private int _digits;

    partial void OnDigitsChanged(int value)
    {
        _marker.MaximumFractionDigits = Math.Clamp(value, 1, 9);
        NotifyParent();
    }

    // ---- Normalized impedance -------------------------------------------

    [ObservableProperty]
    private bool _useNormalized;

    partial void OnUseNormalizedChanged(bool value)
    {
        _marker.UseNormalizedImpedance = value;
        NotifyParent();
    }

    // ---- Precision format (Auto/Fixed/Scientific) -----------------------

    [ObservableProperty]
    private PrecisionFormat _formatString;

    partial void OnFormatStringChanged(PrecisionFormat value)
    {
        _marker.FormatString = value;
        NotifyParent();
    }

    // ---- Multi-marker / delta mode (Rect plots only) --------------------

    [ObservableProperty]
    private bool _isMulti;

    partial void OnIsMultiChanged(bool value)
    {
        _marker.IsMulti = value;
        NotifyParent();
        // IsMulti controls whether a vertical line is drawn in the PlotControl.
        // NotifyParent() only redraws the InfoBox, so we must also invalidate the plot.
        _parent.Container.RequestPlotRedraw();
    }

    [ObservableProperty]
    private bool _isDelta;

    partial void OnIsDeltaChanged(bool value)
    {
        _marker.IsDelta = value;
        NotifyParent();
    }

    /// <summary>True when the host plot is a Rect chart — gates IsMulti / IsDelta controls.</summary>
    public bool ShowMultiDeltaControls => _parent is not null && _parent.PlotType == PlotType.Rect;

    /// <summary>
    /// Matrix-format selector is only meaningful on Smith/Polar plots where the marker
    /// displays a complex value.  On Rect plots markers always show scalar values, so
    /// the Format ComboBox is hidden.  Defaults true when no parent (design time).
    /// </summary>
    public bool ShowFormatSelector => _parent is null || _parent.PlotType != PlotType.Rect;

    // ---- Design-time instance (AXAML previewer) -------------------------
    //
    //  Usage in MarkerEditorView.axaml:
    //    <Design.DataContext>
    //        <x:Static Member="vm:MarkerEditorViewModel.DesignInstance"/>
    //    </Design.DataContext>

    private static readonly Lazy<MarkerEditorViewModel> _designInstance =
        new(CreateDesignInstance);
    public static MarkerEditorViewModel DesignInstance => _designInstance.Value;

    private static MarkerEditorViewModel CreateDesignInstance()
    {
        var snp   = new SNP(new[] { 1e9, 2e9, 3e9 }, 2, MatrixType.S, MatrixFormat.DB);
        var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
        var marker = new Marker(trace, 2.45e9, isMulti: false, isDelta: false, index: 1)
        {
            Style                  = MarkerStyle.Medium,
            MaximumFractionDigits  = 3,
            UseNormalizedImpedance = false,
            FormatString           = PrecisionFormat.G,
        };
        return new MarkerEditorViewModel(marker);
    }

    // ---- Constructors ---------------------------------------------------

    // Private design-time constructor — _parent is null; guarded in all accessors.
    private MarkerEditorViewModel(Marker marker)
    {
        _marker          = marker;
        _parent          = null!;
        _name            = marker.Name;
#pragma warning disable MVVMTK0034
        _freqDisplayText = (marker.Freq * marker.FreqUnits.Scale()).ToString("G6");
#pragma warning restore MVVMTK0034
        _matrixFormat    = marker.MatrixFormat;
        _style           = marker.Style;
        _digits          = marker.MaximumFractionDigits;
        _useNormalized   = marker.UseNormalizedImpedance;
        _formatString    = marker.FormatString;
        _isMulti         = marker.IsMulti;
        _isDelta         = marker.IsDelta;
    }

    public MarkerEditorViewModel(MarkerInfoBoxViewModel parent)
    {
        _marker      = parent.Marker;
        _parent      = parent;

        _name           = _marker.Name;
#pragma warning disable MVVMTK0034
        _freqDisplayText= (_marker.Freq * _marker.FreqUnits.Scale()).ToString("G6");
#pragma warning restore MVVMTK0034
        _matrixFormat   = _marker.MatrixFormat;
        _style          = _marker.Style;
        _digits         = _marker.MaximumFractionDigits;
        _useNormalized  = _marker.UseNormalizedImpedance;
        _formatString   = _marker.FormatString;
        _isMulti        = _marker.IsMulti;
        _isDelta        = _marker.IsDelta;
    }

    // ---- Data-point display (always shown) and multi-marker rows --------

    /// <summary>Own trace data description, e.g. "dB(S(2,1))=−12.3 dB ∠−45°".</summary>
    public string OwnDataLine => _parent is null ? "dB(S(2,1)) = −3.45 dB ∠−45°"
        : _parent.Trace.GetMarkerValString(_marker, showFilePrefix: false);

    /// <summary>Own trace reference impedance, e.g. "Z0=50 Ω".</summary>
    public string OwnZ0Line => _parent is null ? "Z0=50 Ω"
        : $"Z0={ComplexStringHelper.Format(_parent.Trace.Z0)} Ω";

    /// <summary>True when the multi-trace section should be visible.</summary>
    public bool HasMultiLines => _parent is not null && IsMulti && _parent.PlotType == PlotType.Rect;

    /// <summary>
    /// One item per other trace in the plot.  Values are absolute or delta depending on IsDelta.
    /// </summary>
    public IReadOnlyList<MultiTraceLineItem> MultiLines
    {
        get
        {
            if (!HasMultiLines) return Array.Empty<MultiTraceLineItem>();
            var result = new List<MultiTraceLineItem>();
            foreach (var t in _parent.Container.PlotVM.Plot.Traces)
            {
                if (t == _parent.Trace) continue;
                result.Add(new MultiTraceLineItem
                {
                    // Delegate to Trace.GetMultiMarkerLine so formatting is identical to the InfoBox.
                    DataText = _parent.Trace.GetMultiMarkerLine(_marker, t),
                    Z0Text   = $"Z0={ComplexStringHelper.Format(t.Z0)} Ω",
                });
            }
            return result;
        }
    }

    // ---- Helpers --------------------------------------------------------

    private void NotifyParent()
    {
        _parent?.OnMarkerMoved();
        MarkerChanged?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(OwnDataLine));
        OnPropertyChanged(nameof(HasMultiLines));
        OnPropertyChanged(nameof(MultiLines));
    }

    public string FreqUnitLabel => _marker.FreqUnits.Description();
}
