using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// R-h9b-12 — the DCIV Sweeps dialog: right-click anywhere on the DCIV panel opens this, editing
/// <see cref="DcivFamily.Key"/>'s six sweep numbers as an override of
/// <see cref="DcivFamily.DefaultKey"/>. <c>DrainPort</c> is deliberately not offered.
///
/// <para><b>The commit shape is the strip's own</b> (§7.5 / R-h7-3): Return applies and handles the
/// key so the dialog's default button does not steal it; Escape reverts the box to the last-applied
/// values; LostFocus applies. <b>Invalid input keeps the old trace</b> — a rejected candidate never
/// reaches <see cref="HarmonicaViewModel.ApplyDcivOverride"/>, so whatever family is on screen stays
/// exactly as it was, and the offending box(es) are flagged in <see cref="ErrorText"/> rather than
/// silently reverted out from under a still-focused edit.</para>
/// </summary>
public partial class HarmonicaDcivSweepsDialog : Window
{
    private readonly HarmonicaViewModel _vm;
    private bool _updating;

    // Parameterless ctor satisfies the Avalonia XAML resource loader (AVLN3001).
    public HarmonicaDcivSweepsDialog() : this(new HarmonicaViewModel()) { }

    public HarmonicaDcivSweepsDialog(HarmonicaViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        RefreshFromModel();
    }

    /// <summary>The key currently in effect — the override if one is set, else the default. What the
    /// boxes show whenever they are not mid-edit.</summary>
    private DcivFamily.Key Current => DcivFamily.ResolvedKey(_vm.Model);

    private void RefreshFromModel()
    {
        _updating = true;
        var k = Current;
        VgsMinBox.Text   = Num(k.VgsMin);
        VgsMaxBox.Text   = Num(k.VgsMax);
        VgsStepsBox.Text = k.VgsSteps.ToString(CultureInfo.InvariantCulture);
        VdsMinBox.Text   = Num(k.VdsMin);
        VdsMaxBox.Text   = Num(k.VdsMax);
        VdsStepsBox.Text = k.VdsSteps.ToString(CultureInfo.InvariantCulture);

        // brief-harmonicarf-r6e §3.1 — the Axis limits section. A stored limit shows what it holds;
        // an ABSENT one (never set — §2.2's "compute once from the first frame" has not run yet, or
        // this document predates the setting) shows blank rather than a fabricated number, so the
        // boxes never claim a value the document does not actually have.
        var s = _vm.Model.Settings;
        AxisXMinBox.Text = s.DcivXMin is { } xMin ? Num(xMin) : "";
        AxisXMaxBox.Text = s.DcivXMax is { } xMax ? Num(xMax) : "";
        AxisYMinBox.Text = s.DcivYMin is { } yMin ? Num(yMin) : "";
        AxisYMaxBox.Text = s.DcivYMax is { } yMax ? Num(yMax) : "";
        AutoscaleCheck.IsChecked = s.DcivAutoscale;
        SetAxisBoxesEnabled(!s.DcivAutoscale);
        _updating = false;
    }

    private void SetAxisBoxesEnabled(bool enabled)
    {
        AxisXMinBox.IsEnabled = enabled;
        AxisXMaxBox.IsEnabled = enabled;
        AxisYMinBox.IsEnabled = enabled;
        AxisYMaxBox.IsEnabled = enabled;
    }

    private static string Num(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    private void OnFieldLostFocus(object? sender, RoutedEventArgs e) => Commit();

    private void OnFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            Commit();
            // Without this the dialog's default button (Close) takes the Return instead of applying.
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            RefreshFromModel();          // revert every box to the last-applied values
            HideError();
            e.Handled = true;
        }
    }

    private void Commit()
    {
        if (_updating) return;

        if (!TryReal(VgsMinBox.Text, out double vgsMin) || !TryReal(VgsMaxBox.Text, out double vgsMax) ||
            !TryInt(VgsStepsBox.Text, out int vgsSteps) ||
            !TryReal(VdsMinBox.Text, out double vdsMin) || !TryReal(VdsMaxBox.Text, out double vdsMax) ||
            !TryInt(VdsStepsBox.Text, out int vdsSteps))
        {
            ShowError("Every field must be a number (steps a whole number).");
            return;
        }

        if (!DcivFamily.IsValidOverride(vgsMin, vgsMax, vgsSteps, vdsMin, vdsMax, vdsSteps))
        {
            ShowError("min must be less than max on both axes, and steps must be at least 2.");
            return;
        }

        _vm.ApplyDcivOverride(vgsMin, vgsMax, vgsSteps, vdsMin, vdsMax, vdsSteps);
        HideError();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    private void HideError() => ErrorText.IsVisible = false;

    // ── brief-harmonicarf-r6e §3.1 — Axis limits: X min/max, Y min/max, Autoscale. Same commit
    // shape as the sweep-range fields above (LostFocus / Enter applies, Escape reverts) — see this
    // class's own remarks at the top for why.

    private void OnAxisFieldLostFocus(object? sender, RoutedEventArgs e) => CommitAxisLimits();

    private void OnAxisFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            CommitAxisLimits();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            RefreshFromModel();
            HideError();
            e.Handled = true;
        }
    }

    /// <summary>Reject-and-keep on bad input (R6A §6's rule) — a rejected candidate never reaches
    /// <see cref="HarmonicaViewModel.ApplyDcivAxisLimits"/>, so the stored window stays exactly what
    /// it was and the offending box is flagged in <see cref="ErrorText"/>.</summary>
    private void CommitAxisLimits()
    {
        if (_updating) return;

        if (!TryReal(AxisXMinBox.Text, out double xMin) || !TryReal(AxisXMaxBox.Text, out double xMax) ||
            !TryReal(AxisYMinBox.Text, out double yMin) || !TryReal(AxisYMaxBox.Text, out double yMax))
        {
            ShowError("Every axis-limit field must be a number.");
            return;
        }

        if (!_vm.ApplyDcivAxisLimits(xMin, xMax, yMin, yMax))
        {
            ShowError("min must be less than max on both axes.");
            return;
        }

        HideError();
    }

    private void OnAutoscaleClick(object? sender, RoutedEventArgs e)
    {
        bool on = AutoscaleCheck.IsChecked == true;
        _vm.SetDcivAutoscale(on);
        SetAxisBoxesEnabled(!on);
        HideError();
    }

    private void OnResetClick(object? sender, RoutedEventArgs e)
    {
        _vm.ResetDcivOverride();
        RefreshFromModel();
        HideError();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private static bool TryReal(string? s, out double v)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private static bool TryInt(string? s, out int v)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
}
