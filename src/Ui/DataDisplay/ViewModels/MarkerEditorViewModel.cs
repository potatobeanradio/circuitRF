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
using System.Numerics;
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

    private bool MarkerIsLive => _parent is not null && _parent.Trace.Markers.Contains(_marker);

    // ---- Picker lists ---------------------------------------------------

    public static IReadOnlyList<MarkerStyle>     AllStyles          { get; } = Enum.GetValues<MarkerStyle>().ToList();
    public static IReadOnlyList<MatrixFormat>    AllFormats         { get; } = Enum.GetValues<MatrixFormat>().ToList();
    public static IReadOnlyList<PrecisionFormat> AllPrecisionFormats{ get; } = Enum.GetValues<PrecisionFormat>().ToList();

    // ---- Marker name ----------------------------------------------------

    [ObservableProperty]
    private string _name;

    partial void OnNameChanged(string value)
    {
        if (!MarkerIsLive) return;
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
        if (!MarkerIsLive) return;
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
        if (!MarkerIsLive) return;
        _marker.MatrixFormat = value;
        NotifyParent();
    }

    // ---- Marker style ---------------------------------------------------

    [ObservableProperty]
    private MarkerStyle _style;

    partial void OnStyleChanged(MarkerStyle value)
    {
        if (!MarkerIsLive) return;
        _marker.Style = value;
        NotifyParent();
    }

    // ---- Precision digits -----------------------------------------------

    [ObservableProperty]
    private int _digits;

    partial void OnDigitsChanged(int value)
    {
        if (!MarkerIsLive) return;
        _marker.MaximumFractionDigits = Math.Clamp(value, 1, 9);
        NotifyParent();
    }

    // ---- Normalized impedance -------------------------------------------

    [ObservableProperty]
    private bool _useNormalized;

    partial void OnUseNormalizedChanged(bool value)
    {
        if (!MarkerIsLive) return;
        _marker.UseNormalizedImpedance = value;
        NotifyParent();
    }

    // ---- Precision format (Auto/Fixed/Scientific) -----------------------

    [ObservableProperty]
    private PrecisionFormat _formatString;

    partial void OnFormatStringChanged(PrecisionFormat value)
    {
        if (!MarkerIsLive) return;
        _marker.FormatString = value;
        NotifyParent();
    }

    // ---- Multi-marker / delta mode (Rect plots only) --------------------

    [ObservableProperty]
    private bool _isMulti;

    partial void OnIsMultiChanged(bool value)
    {
        if (!MarkerIsLive) return;
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
        if (!MarkerIsLive) return;
        _marker.IsDelta = value;
        NotifyParent();
    }

    // ---- ShowInfoBox toggle ---------------------------------------------

    /// <summary>Info-box visibility is locked ON for Table plots — a Table has no on-canvas way to
    /// re-open a hidden box, so switching to Table forces all boxes on and disables this toggle.</summary>
    public bool CanToggleInfoBox => _parent is null || _parent.PlotType != PlotType.Table;

    [ObservableProperty]
    private bool _showInfoBox;

    partial void OnShowInfoBoxChanged(bool value)
    {
        if (!MarkerIsLive) return;
        _marker.ShowInfoBox = value;
        NotifyParent();
        // Targeted add/remove of THIS marker's InfoBox only — a full rebuild would recreate
        // every InfoBox VM (including the one this editor flyout is bound to) and dismiss the flyout.
        _parent?.Container.SetMarkerInfoBoxVisibility(_marker, _parent.Trace);
    }

    // ---- VSWR enable + value -------------------------------------------

    [ObservableProperty]
    private bool _vswrEnabled;

    partial void OnVswrEnabledChanged(bool value)
    {
        if (!MarkerIsLive) return;
        _marker.VswrEnabled = value;
        NotifyParent();
        _parent?.Container.RequestPlotRedraw();
    }

    [ObservableProperty]
    private string _vswrValueText = "2";

    public void CommitVswrValue()
    {
        if (!MarkerIsLive) return;
        if (double.TryParse(VswrValueText, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.CurrentCulture, out double v))
        {
            _marker.VswrValue = v;
            VswrValueText = v.ToString("G6");
            NotifyParent();
            _parent?.Container.RequestPlotRedraw();
        }
        else
        {
            VswrValueText = _marker.VswrValue.ToString("G6");
        }
    }

    // ---- Contour mode (Mode 1 free / Mode 2 snapped) -------------------

    [ObservableProperty]
    private bool _contourSnapped;

    partial void OnContourSnappedChanged(bool value)
    {
        if (!MarkerIsLive) return;
        _marker.ContourSnapped = value;
        _marker.PositionStatic = _parent!.Trace.ResolveContourMarkerPosition(_marker, _marker.PositionStatic);
        NotifyParent();
        _parent.Container.RequestPlotRedraw();
    }

    // ---- Impedance (buffered, contour markers only) ---------------------

    [ObservableProperty]
    private string _impedanceText = "";

    private Complex RealZ0()
    {
        var z0 = _parent!.Trace.Z0;
        return z0 == Complex.Zero ? new Complex(50, 0) : z0;
    }

    public void SyncImpedanceText()
    {
        if (_parent is null || !_parent.Trace.IsContourTrace) return;
        var pos = _marker.PositionStatic;
        // Trace.ContourImpedance is the single Γ→Z conversion — the info box's "Z=" row reads the
        // same call, so this field and that row cannot state different terminations for one marker.
        ImpedanceText = ComplexStringHelper.Format(
            _parent.Trace.ContourImpedance(new Complex(pos.X, pos.Y),
                                           gammaPlane: _parent.PlotType != PlotType.Rect), "G6");
    }

    public void CommitImpedance()
    {
        if (!MarkerIsLive || _parent is null || !_parent.Trace.IsContourTrace) return;
        // An unparseable entry is not an error to report — the field simply snaps back to the
        // marker's actual impedance, which is the correction the user needs to see.
        if (!ComplexStringHelper.TryParse(ImpedanceText, out Complex z)) { SyncImpedanceText(); return; }
        Complex posC = _parent.PlotType == PlotType.Rect
            ? z
            : RfHelpers.Z2G(z / RealZ0());
        var world = new System.Numerics.Vector2((float)posC.Real, (float)posC.Imaginary);
        _marker.PositionStatic = _parent.Trace.ResolveContourMarkerPosition(_marker, world);
        SyncImpedanceText();
        NotifyParent();
        _parent.Container.RequestPlotRedraw();
    }

    // ---- Visibility gates -----------------------------------------------

    public bool IsContour  => _parent is not null && _parent.Trace.IsContourTrace;
    public bool IsSpectrum => _parent is not null && _parent.Trace.IsHarmonicStem;
    public bool IsRectPlot => _parent is not null && _parent.PlotType == PlotType.Rect;

    /// <summary>VSWR controls show only for markers on a Smith/Γ plot (§6.1 gate).</summary>
    public bool ShowVswrControls =>
        _parent is not null &&
        PlotRenderer.VswrAvailableFor(_parent.Container.PlotVM.Plot, _parent.Trace, _marker);

    /// <summary>Contour mode toggle shows only for contour markers.</summary>
    public bool ShowContourModeToggle => _parent is not null && _parent.Trace.IsContourTrace;

    /// <summary>True when the host plot is a Rect chart and not a contour trace.</summary>
    public bool ShowMultiDeltaControls => _parent is not null && _parent.PlotType == PlotType.Rect && !IsContour;

    /// <summary>Z0 line shows only for network matrix traces (S/Y/Z) — the only traces with a
    /// meaningful port reference impedance. Hidden for cube-bound traces (HB spectra, families,
    /// measurement expressions), contours, and derived (stability/gain) traces.</summary>
    public bool ShowZ0Line =>
        _parent is not null && !_parent.Trace.IsCubeBound && !_parent.Trace.IsContourTrace
        && !_parent.Trace.IsDerived;

    /// <summary>Norm Z toggle shows only when the marker actually displays an impedance readout
    /// (a reflection S-parameter, Sii, on a complex plane) — the only case the toggle affects.</summary>
    public bool ShowNormZ => _parent is not null && _parent.Trace.MarkerShowsImpedance(_marker);

    /// <summary>Frequency field shown only for traces whose X axis is the network 'freq' axis
    /// (SNP/derived traces). Cube-bound traces (HB spectra, measurement expressions, families,
    /// contours) are swept on other axes and have no editable frequency.</summary>
    public bool ShowFrequencyField =>
        _parent is not null && !_parent.Trace.IsCubeBound && !IsContour && !IsSpectrum;

    /// <summary>Impedance field shown for contour markers.</summary>
    public bool ShowImpedanceField => IsContour;

    /// <summary>
    /// Matrix-format selector is only meaningful where the marker displays a COMPLEX value.
    /// That covers Smith/Polar plots (markers show complex values) and contour markers (the
    /// coordinate/impedance readout is complex Z/Γ) — including contour-on-Rect, where the plot
    /// type alone would otherwise hide it. Hidden for ordinary scalar Rect traces.
    /// Defaults true when no parent (design time).
    /// </summary>
    public bool ShowFormatSelector =>
        _parent is null || _parent.PlotType != PlotType.Rect || IsContour;

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
        _showInfoBox     = marker.ShowInfoBox;
        _vswrEnabled     = marker.VswrEnabled;
        _vswrValueText   = marker.VswrValue.ToString("G6");
        _contourSnapped  = marker.ContourSnapped;
        _impedanceText   = "";
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
        _showInfoBox    = _marker.ShowInfoBox;
        _vswrEnabled    = _marker.VswrEnabled;
        _vswrValueText  = _marker.VswrValue.ToString("G6");
        _contourSnapped = _marker.ContourSnapped;
        _impedanceText  = "";
#pragma warning restore MVVMTK0034
        _matrixFormat   = _marker.MatrixFormat;
        _style          = _marker.Style;
        _digits         = _marker.MaximumFractionDigits;
        _useNormalized  = _marker.UseNormalizedImpedance;
        _formatString   = _marker.FormatString;
        _isMulti        = _marker.IsMulti;
        _isDelta        = _marker.IsDelta;
        SyncImpedanceText();
    }

    // ---- Data-point display (always shown) and multi-marker rows --------

    /// <summary>Own trace data description, e.g. "dB(S(2,1))=−12.3 dB ∠−45°".</summary>
    public string OwnDataLine => _parent is null ? "dB(S(2,1)) = −3.45 dB ∠−45°"
        : _parent.Trace.GetEditorDataLine(_marker, showFilePrefix: false);

    /// <summary>Own trace reference impedance, e.g. "Z0=50 Ω" — the reference the readout above is
    /// actually against (the port's own when Z0 Override is off), not the trace's port-1 mirror.</summary>
    public string OwnZ0Line => _parent is null ? "Z0=50 Ω"
        : $"Z0={ComplexStringHelper.Format(_parent.Trace.MarkerZ0)} Ω";

    /// <summary>True when the extra-trace section should be visible: a multi-marker on a Rect plot,
    /// or a contour marker with sibling contour traces to read out beside its own.</summary>
    public bool HasMultiLines =>
        _parent is not null &&
        ((IsMulti && _parent.PlotType == PlotType.Rect) || SiblingContours.Count > 0);

    /// <summary>The OTHER contour traces in this marker's plot, in placement order. Empty unless the
    /// marker sits on a contour — the readout only makes sense between loadpull surfaces.</summary>
    private IReadOnlyList<Trace> SiblingContours =>
        _parent is null || !_parent.Trace.IsContourTrace
            ? Array.Empty<Trace>()
            : _parent.Container.PlotVM.Plot.Traces
                     .Where(t => t.IsContourTrace && t != _parent.Trace).ToList();

    /// <summary>
    /// One item per other trace in the plot.  Values are absolute or delta depending on IsDelta.
    /// For a contour marker these are the plot's other contour metrics at the SAME termination —
    /// the same rows, in the same order, that the info box shows under the marker's own.
    /// </summary>
    public IReadOnlyList<MultiTraceLineItem> MultiLines
    {
        get
        {
            if (!HasMultiLines) return Array.Empty<MultiTraceLineItem>();

            if (_parent!.Trace.IsContourTrace)
            {
                var coord = new Complex(_marker.PositionStatic.X, _marker.PositionStatic.Y);
                return SiblingContours
                    .Select(t => new MultiTraceLineItem
                    {
                        DataText = t.ContourMetricLine(_marker, coord),
                        Z0Text   = "",
                    })
                    .ToList();
            }

            var result = new List<MultiTraceLineItem>();
            foreach (var t in _parent.Container.PlotVM.Plot.Traces)
            {
                if (t == _parent.Trace) continue;
                bool tHasZ0 = !t.IsCubeBound && !t.IsContourTrace && !t.IsDerived;
                result.Add(new MultiTraceLineItem
                {
                    // Delegate to Trace.GetMultiMarkerLine so formatting is identical to the InfoBox.
                    DataText = _parent.Trace.GetMultiMarkerLine(_marker, t),
                    Z0Text   = tHasZ0 ? $"Z0={ComplexStringHelper.Format(t.MarkerZ0)} Ω" : "",
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
        SyncImpedanceText();
    }

    public string FreqUnitLabel => _marker.FreqUnits.Description();
}
