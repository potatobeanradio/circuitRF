using System;
using System.Globalization;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Harmonica;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// R-h9c-7's "Set…" — edits one marker's termination in EITHER format, per the brief's own words.
/// The impedance (R + jX) and the reflection (|Γ| ∠ angle) fields are kept in sync LIVE — editing
/// either moves both, for the user's convenience — but <b>OK reports which group was actually typed
/// in last</b>, and the caller writes through the MATCHING call
/// (<c>SetMarkerImpedance</c>/<c>SetMarkerGamma</c>) — never a converted-and-relabelled call. That is
/// what keeps <c>SetMarkerGamma</c>'s own Γ = 1 nudge the ONE place that guard lives, rather than a
/// second copy of it living in this dialog.
/// </summary>
public partial class HarmonicaSetTerminationDialog : Window
{
    /// <summary>Exactly one of the two is set — whichever group the user last typed in (or neither,
    /// if they changed nothing and simply pressed OK, in which case the marker's own current value
    /// travels back as an impedance, a no-op write).</summary>
    public readonly record struct TerminationEdit(Complex? Impedance, Complex? Gamma);

    private double _z0 = 50.0;
    private Complex _z;
    private Complex _gamma;
    private bool _lastEditWasGamma;
    private bool _loading;

    // Parameterless ctor satisfies the Avalonia XAML resource loader (AVLN3001).
    public HarmonicaSetTerminationDialog() => InitializeComponent();

    public HarmonicaSetTerminationDialog(string markerName, Complex initialGamma, double z0) : this()
    {
        _z0    = z0;
        _gamma = initialGamma;
        _z     = HarmonicaDataSet.ImpedanceOf(initialGamma, z0);
        MarkerLabel.Text = $"{markerName} — termination against Z0={FormatZ0(z0)} Ω";
        LoadFields();
    }

    /// <summary>The edit the user settled on, or null when cancelled.</summary>
    public static async Task<TerminationEdit?> ShowAsync(Window owner, string markerName,
                                                          Complex initialGamma, double z0)
        => await new HarmonicaSetTerminationDialog(markerName, initialGamma, z0)
            .ShowDialog<TerminationEdit?>(owner);

    private void LoadFields()
    {
        _loading = true;
        try
        {
            RealBox.Text  = _z.Real.ToString("0.###", CultureInfo.InvariantCulture);
            ImagBox.Text  = _z.Imaginary.ToString("0.###", CultureInfo.InvariantCulture);
            MagBox.Text   = _gamma.Magnitude.ToString("0.###", CultureInfo.InvariantCulture);
            AngleBox.Text = (_gamma.Phase * 180.0 / Math.PI).ToString("0.##", CultureInfo.InvariantCulture);
        }
        finally { _loading = false; }
    }

    private void OnRealImagChanged(object? sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (!TryDouble(RealBox.Text, out double re) || !TryDouble(ImagBox.Text, out double im)) return;
        _lastEditWasGamma = false;
        _z     = new Complex(re, im);
        _gamma = HarmonicaDataSet.GammaOf(_z, _z0);
        LoadFields();
    }

    private void OnMagAngleChanged(object? sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (!TryDouble(MagBox.Text, out double mag) || !TryDouble(AngleBox.Text, out double angDeg)) return;
        _lastEditWasGamma = true;
        _gamma = Complex.FromPolarCoordinates(mag, angDeg * Math.PI / 180.0);
        // Live preview only — SetMarkerGamma's own Γ = 1 nudge is what actually protects the write;
        // this just keeps the R/X fields on screen finite while the user is mid-type.
        double previewMag = Math.Min(mag, 0.999);
        _z = HarmonicaDataSet.ImpedanceOf(Complex.FromPolarCoordinates(previewMag, angDeg * Math.PI / 180.0), _z0);
        LoadFields();
    }

    private static bool TryDouble(string? s, out double v)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private static string FormatZ0(double z0)
        => z0 == Math.Floor(z0) ? ((long)z0).ToString(CultureInfo.InvariantCulture) : z0.ToString("0.##", CultureInfo.InvariantCulture);

    private void OnOkClick(object? sender, RoutedEventArgs e)
        => Close(_lastEditWasGamma
            ? new TerminationEdit(null, _gamma)
            : new TerminationEdit(_z, null));

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
