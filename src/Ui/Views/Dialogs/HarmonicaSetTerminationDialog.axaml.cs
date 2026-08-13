using System;
using System.Globalization;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// R-h9r2-7 — which of the three combined-text rows a caller wants initial focus on, matching the
/// format of whichever context-menu row's "Set…" the user actually clicked. <see cref="ZRealImag"/> is
/// the default so <see cref="HarmonicaSetTerminationDialog.ShowAsync"/>'s original 4-arg call site
/// (R1C's readout-strip editor) keeps compiling with its old behaviour unchanged.
/// </summary>
public enum HarmonicaTerminationEntryFormat { ZRealImag, GammaRealImag, GammaMagAngle }

/// <summary>
/// R-h9c-7 / R-h9r2-7's "Set…" — edits one marker's termination in any of THREE combined-text rows:
/// Γ (real/imag), Γ (mag/angle), Z (real/imag). All three are kept in sync LIVE — editing any one moves
/// the other two, for the user's convenience — but <b>OK reports which was actually typed in last</b>,
/// and the caller writes through the MATCHING call (<c>SetMarkerImpedance</c>/<c>SetMarkerGamma</c>) —
/// never a converted-and-relabelled call. That is what keeps <c>SetMarkerGamma</c>'s own Γ = 1 nudge the
/// ONE place that guard lives, rather than a second copy of it living in this dialog.
///
/// <para><b>Every field reads and writes through <see cref="HarmonicaReadoutFormatting"/> — never a
/// second formatter.</b> R1C's readout strip already parses/formats "50+j10" and "0.5∠45°" as combined
/// text; using the SAME functions here is what makes "the readout strip agrees with the marker menu
/// about how a number is spelled" (R-h9r2-6's own gate 11) true by construction rather than by two
/// hand-matched implementations.</para>
/// </summary>
public partial class HarmonicaSetTerminationDialog : Window
{
    /// <summary>Exactly one of the two is set — whichever row the user last typed in (or neither, if
    /// they changed nothing and simply pressed OK, in which case the marker's own current value travels
    /// back as an impedance, a no-op write).</summary>
    public readonly record struct TerminationEdit(Complex? Impedance, Complex? Gamma);

    private double _z0 = 50.0;
    private Complex _z;
    private Complex _gamma;
    private bool _lastEditWasGamma;
    private bool _loading;

    // Parameterless ctor satisfies the Avalonia XAML resource loader (AVLN3001).
    public HarmonicaSetTerminationDialog() => InitializeComponent();

    public HarmonicaSetTerminationDialog(string markerName, Complex initialGamma, double z0,
        HarmonicaTerminationEntryFormat focusFormat = HarmonicaTerminationEntryFormat.ZRealImag) : this()
    {
        _z0    = z0;
        _gamma = initialGamma;
        _z     = HarmonicaDataSet.ImpedanceOf(initialGamma, z0);
        MarkerLabel.Text = $"{markerName} — termination against Z0={FormatZ0(z0)} Ω";
        LoadFields();

        var focusBox = focusFormat switch
        {
            HarmonicaTerminationEntryFormat.GammaRealImag => GammaRealImagBox,
            HarmonicaTerminationEntryFormat.GammaMagAngle => GammaMagAngleBox,
            _ => ZRealImagBox,
        };
        Opened += (_, _) => { focusBox.Focus(); focusBox.SelectAll(); };
    }

    /// <summary>The edit the user settled on, or null when cancelled.</summary>
    public static async Task<TerminationEdit?> ShowAsync(Window owner, string markerName,
        Complex initialGamma, double z0,
        HarmonicaTerminationEntryFormat focusFormat = HarmonicaTerminationEntryFormat.ZRealImag)
        => await new HarmonicaSetTerminationDialog(markerName, initialGamma, z0, focusFormat)
            .ShowDialog<TerminationEdit?>(owner);

    private void LoadFields()
    {
        _loading = true;
        try
        {
            GammaRealImagBox.Text = HarmonicaReadoutFormatting.FormatGamma(_gamma, ReadoutFormat.RealImaginary);
            GammaMagAngleBox.Text = HarmonicaReadoutFormatting.FormatGamma(_gamma, ReadoutFormat.MagnitudeAngle);
            ZRealImagBox.Text     = HarmonicaReadoutFormatting.FormatZ(_z, ReadoutFormat.RealImaginary);
        }
        finally { _loading = false; }
    }

    private void OnGammaRealImagChanged(object? sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (!HarmonicaReadoutFormatting.TryParse(GammaRealImagBox.Text, ReadoutFormat.RealImaginary, out var g)) return;
        ApplyGammaEdit(g);
    }

    private void OnGammaMagAngleChanged(object? sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (!HarmonicaReadoutFormatting.TryParse(GammaMagAngleBox.Text, ReadoutFormat.MagnitudeAngle, out var g)) return;
        ApplyGammaEdit(g);
    }

    private void OnZRealImagChanged(object? sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (!HarmonicaReadoutFormatting.TryParse(ZRealImagBox.Text, ReadoutFormat.RealImaginary, out var z)) return;
        _lastEditWasGamma = false;
        _z     = z;
        _gamma = HarmonicaDataSet.GammaOf(z, _z0);
        LoadFields();
    }

    private void ApplyGammaEdit(Complex g)
    {
        _lastEditWasGamma = true;
        _gamma = g;
        // Live preview only — SetMarkerGamma's own Γ = 1 nudge is what actually protects the write;
        // this just keeps the Z row on screen finite while the user is mid-type.
        double mag = g.Magnitude;
        var previewGamma = mag > 0.999 ? g / mag * 0.999 : g;
        _z = HarmonicaDataSet.ImpedanceOf(previewGamma, _z0);
        LoadFields();
    }

    private static string FormatZ0(double z0)
        => z0 == Math.Floor(z0) ? ((long)z0).ToString(CultureInfo.InvariantCulture) : z0.ToString("0.##", CultureInfo.InvariantCulture);

    private void OnOkClick(object? sender, RoutedEventArgs e)
        => Close(_lastEditWasGamma
            ? new TerminationEdit(null, _gamma)
            : new TerminationEdit(_z, null));

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
