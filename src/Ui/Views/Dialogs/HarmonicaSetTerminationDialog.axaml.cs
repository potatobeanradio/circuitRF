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
/// <para><b>R8B §1.2/§1.3 — the dialog holds no state of its own.</b> Every parse/format/ownership rule
/// lives in <see cref="TerminationEditModel"/>, a plain class with no Avalonia reference. This shell
/// only: sets <see cref="TerminationEditModel.Editing"/> on <c>GotFocus</c>/clears it on
/// <c>LostFocus</c> (before the lost-focus reformat, so that reformat's own echo is disowned too),
/// forwards <c>TextChanged</c> to <see cref="TerminationEditModel.Edit"/>, and writes
/// <see cref="TerminationEditModel.TextFor"/> into the two boxes that are not being edited. An
/// ownership test — "is this box the one the user is actually in" — replaces the old
/// <c>_loading</c> re-entrancy flag, which was a window in time rather than a statement about
/// identity: an echo that landed after that window closed was processed as if the user had typed it,
/// which is what let a Z edit get silently overwritten mid-type.</para>
/// </summary>
public partial class HarmonicaSetTerminationDialog : Window
{
    private TerminationEditModel _model = null!;

    // Parameterless ctor satisfies the Avalonia XAML resource loader (AVLN3001).
    public HarmonicaSetTerminationDialog() => InitializeComponent();

    public HarmonicaSetTerminationDialog(string markerName, Complex initialGamma, double z0,
        HarmonicaTerminationEntryFormat focusFormat = HarmonicaTerminationEntryFormat.ZRealImag) : this()
    {
        _model = new TerminationEditModel(initialGamma, z0);
        MarkerLabel.Text = $"{markerName} (Z0={FormatZ0(z0)} Ω)";
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

    private void LoadFields(TextBox? except = null)
    {
        if (!ReferenceEquals(except, GammaRealImagBox))
            GammaRealImagBox.Text = _model.TextFor(TerminationField.GammaRealImag);
        if (!ReferenceEquals(except, GammaMagAngleBox))
            GammaMagAngleBox.Text = _model.TextFor(TerminationField.GammaMagAngle);
        if (!ReferenceEquals(except, ZRealImagBox))
            ZRealImagBox.Text = _model.TextFor(TerminationField.ZRealImag);
    }

    private void OnGammaRealImagGotFocus(object? sender, RoutedEventArgs e) => _model.Editing = TerminationField.GammaRealImag;
    private void OnGammaMagAngleGotFocus(object? sender, RoutedEventArgs e) => _model.Editing = TerminationField.GammaMagAngle;
    private void OnZRealImagGotFocus(object? sender, RoutedEventArgs e) => _model.Editing = TerminationField.ZRealImag;

    private void OnGammaRealImagChanged(object? sender, TextChangedEventArgs e)
    {
        if (_model.Edit(TerminationField.GammaRealImag, GammaRealImagBox.Text))
            LoadFields(except: GammaRealImagBox);
    }

    private void OnGammaMagAngleChanged(object? sender, TextChangedEventArgs e)
    {
        if (_model.Edit(TerminationField.GammaMagAngle, GammaMagAngleBox.Text))
            LoadFields(except: GammaMagAngleBox);
    }

    private void OnZRealImagChanged(object? sender, TextChangedEventArgs e)
    {
        if (_model.Edit(TerminationField.ZRealImag, ZRealImagBox.Text))
            LoadFields(except: ZRealImagBox);
    }

    /// <summary>
    /// R7A §1.3(c) — the Z row's live preview while the user is mid-type in a Γ box. An active
    /// termination (|Γ| &gt; 1) is ordinary here, not an error (see <see cref="IntrinsicGlyphScale"/>'s
    /// own remark on the same point for the glyph) — so this nudges ONLY the genuine singularity,
    /// exactly as <see cref="HarmonicaDataSet.ImpedanceOf"/> already does on its own
    /// (<c>|1 − Γ| &lt; 1e-12</c>), and otherwise passes Γ straight through.
    /// </summary>
    internal static Complex PreviewImpedance(Complex gamma, double z0)
        => HarmonicaDataSet.ImpedanceOf(gamma, z0);

    /// <summary>
    /// Reformats the OTHER two boxes once the user has moved on from this one — never while a box
    /// still has focus and may be mid-keystroke. Clears <see cref="TerminationEditModel.Editing"/>
    /// FIRST, so this reformat's own echo is disowned exactly like every other box's.
    /// </summary>
    private void OnFieldLostFocus(object? sender, RoutedEventArgs e)
    {
        _model.Editing = null;
        LoadFields(except: sender as TextBox);
    }

    private static string FormatZ0(double z0)
        => z0 == Math.Floor(z0) ? ((long)z0).ToString(CultureInfo.InvariantCulture) : z0.ToString("0.##", CultureInfo.InvariantCulture);

    private void OnOkClick(object? sender, RoutedEventArgs e) => Close(_model.Commit());

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
