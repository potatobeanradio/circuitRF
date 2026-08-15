using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// brief-harmonicarf-r6b §2.1 — the marker menu's "VSWR: &lt;val&gt; ▸ Set…". A single numeric field,
/// OK/Cancel gated: bad input is REJECTED-AND-KEPT (the text stays on screen, an error line explains
/// why) rather than silently substituted or clamped.
///
/// <para><b>Round 10 dropped the "at least 1" rule.</b> A VSWR below 1 — negative included — names the
/// half of the circle family that lies OUTSIDE the Smith chart, which the owner asked to be able to
/// reach ("VSWR can be any value, except NaN or infinity"). What is left refused is exactly those two:
/// text that is not a number, and ±∞. The floor that used to be applied downstream in
/// <see cref="Harmonica.HarmonicaViewModel.SetMarkerVswr"/> is gone with it.
/// </summary>
public partial class HarmonicaSetVswrDialog : Window
{
    private double _result;

    // Parameterless ctor satisfies the Avalonia XAML resource loader (AVLN3001).
    public HarmonicaSetVswrDialog() => InitializeComponent();

    public HarmonicaSetVswrDialog(string markerName, double initialVswr) : this()
    {
        MarkerLabel.Text = $"{markerName} — VSWR circle";
        VswrBox.Text = initialVswr.ToString("0.##", CultureInfo.InvariantCulture);
        Opened += (_, _) => { VswrBox.Focus(); VswrBox.SelectAll(); };
    }

    /// <summary>The accepted VSWR, or null when cancelled.</summary>
    public static async Task<double?> ShowAsync(Window owner, string markerName, double initialVswr)
    {
        var dialog = new HarmonicaSetVswrDialog(markerName, initialVswr);
        bool ok = await dialog.ShowDialog<bool>(owner);
        return ok ? dialog._result : null;
    }

    private void OnFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            TryCommit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close(false);
            e.Handled = true;
        }
    }

    /// <summary>Refuses (leaves the text and the dialog open, shows why) rather than substituting a
    /// silent default. Non-finite input only — every finite value, including one below 1, is a real
    /// circle (see this type's own summary).</summary>
    private bool TryCommit()
    {
        if (!double.TryParse(VswrBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            || !double.IsFinite(v))
        {
            ShowError("VSWR must be a number.");
            return false;
        }
        _result = v;
        Close(true);
        return true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e) => TryCommit();
    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
