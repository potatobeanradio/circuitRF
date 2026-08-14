using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// brief-harmonicarf-r6b §2.1 — the marker menu's "VSWR: &lt;val&gt; ▸ Set…". A single numeric field,
/// OK/Cancel gated: bad input is REJECTED-AND-KEPT (the text stays on screen, an error line explains
/// why) rather than silently substituted or clamped. The only value this refuses is anything less
/// than 1 or non-finite — 1 is the mathematical floor a VSWR is even defined above; the caller
/// (<see cref="Harmonica.HarmonicaViewModel.SetMarkerVswr"/>) applies the small further nudge to
/// <see cref="Harmonica.HarmonicaVswrHandle.MinVswr"/> that keeps the circle non-degenerate, the same
/// floor a drag already respects.
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
    /// silent default — non-finite, or below the VSWR = 1 floor.</summary>
    private bool TryCommit()
    {
        if (!double.TryParse(VswrBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            || !double.IsFinite(v))
        {
            ShowError("VSWR must be a number.");
            return false;
        }
        if (v < 1.0)
        {
            ShowError("VSWR must be at least 1.");
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
