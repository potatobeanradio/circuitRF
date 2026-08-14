using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// brief-harmonicarf-r6e §3.2 — the power-sweep panel's own axis-limits dialog, opened from that
/// panel's title fly menu in EITHER mode the slot can show (§4): Power Sweep or Time Domain. Which
/// mode it edits is fixed at construction — the panel is always showing one or the other, so there
/// is never a live toggle inside this dialog itself.
///
/// <para>Same commit shape as <see cref="HarmonicaDcivSweepsDialog"/>'s own Axis limits section:
/// LostFocus/Enter applies, Escape reverts, reject-and-keep on bad input (never a silent
/// substitution or a clamp the user cannot see).</para>
/// </summary>
public partial class HarmonicaPowerSweepAxesDialog : Window
{
    private readonly HarmonicaViewModel _vm;
    private readonly bool _timeDomain;
    private bool _updating;

    // Parameterless ctor satisfies the Avalonia XAML resource loader (AVLN3001).
    public HarmonicaPowerSweepAxesDialog() : this(new HarmonicaViewModel(), timeDomain: false) { }

    public HarmonicaPowerSweepAxesDialog(HarmonicaViewModel vm, bool timeDomain)
    {
        _vm = vm;
        _timeDomain = timeDomain;
        InitializeComponent();

        if (_timeDomain)
        {
            Title = "Time Domain — Axis Limits";
            HeaderText.Text = "Time Domain";
            YMinLabel.Text = "Vds min";  YMaxLabel.Text = "Vds max";
            Y2MinLabel.Text = "Ids min"; Y2MaxLabel.Text = "Ids max";
        }
        else
        {
            Title = "Power Sweep — Axis Limits";
            HeaderText.Text = "Power Sweep";
            YMinLabel.Text = "Gain min";  YMaxLabel.Text = "Gain max";
            string eff = _vm.EfficiencyMetric == GridMetric.Pae ? "PAE" : "Efficiency";
            Y2MinLabel.Text = $"{eff} min"; Y2MaxLabel.Text = $"{eff} max";
        }

        RefreshFromModel();
    }

    private HarmonicaSettings Settings => _vm.Model.Settings;

    private void RefreshFromModel()
    {
        _updating = true;
        var s = Settings;

        double? xMin, xMax, yMin, yMax, y2Min, y2Max;
        bool autoscale;
        if (_timeDomain)
        {
            (xMin, xMax, yMin, yMax, y2Min, y2Max, autoscale) =
                (s.TimeDomainXMin, s.TimeDomainXMax, s.TimeDomainYMin, s.TimeDomainYMax,
                 s.TimeDomainY2Min, s.TimeDomainY2Max, s.TimeDomainAutoscale);
        }
        else
        {
            (xMin, xMax, yMin, yMax, y2Min, y2Max, autoscale) =
                (s.PowerSweepXMin, s.PowerSweepXMax, s.PowerSweepYMin, s.PowerSweepYMax,
                 s.PowerSweepY2Min, s.PowerSweepY2Max, s.PowerSweepAutoscale);
        }

        XMinBox.Text  = Num(xMin);  XMaxBox.Text  = Num(xMax);
        YMinBox.Text  = Num(yMin);  YMaxBox.Text  = Num(yMax);
        Y2MinBox.Text = Num(y2Min); Y2MaxBox.Text = Num(y2Max);
        AutoscaleCheck.IsChecked = autoscale;
        SetBoxesEnabled(!autoscale);
        _updating = false;
    }

    private static string Num(double? v) => v is { } d ? d.ToString("0.####", CultureInfo.InvariantCulture) : "";

    private void SetBoxesEnabled(bool enabled)
    {
        XMinBox.IsEnabled = enabled;   XMaxBox.IsEnabled = enabled;
        YMinBox.IsEnabled = enabled;   YMaxBox.IsEnabled = enabled;
        Y2MinBox.IsEnabled = enabled;  Y2MaxBox.IsEnabled = enabled;
    }

    private void OnFieldLostFocus(object? sender, RoutedEventArgs e) => Commit();

    private void OnFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            Commit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            RefreshFromModel();
            HideError();
            e.Handled = true;
        }
    }

    /// <summary>Reject-and-keep on bad input — a rejected candidate never reaches
    /// <see cref="HarmonicaViewModel.ApplyPowerSweepAxisLimits"/>/<see
    /// cref="HarmonicaViewModel.ApplyTimeDomainAxisLimits"/>, so the stored window stays exactly
    /// what it was.</summary>
    private void Commit()
    {
        if (_updating) return;

        if (!TryReal(XMinBox.Text, out double xMin) || !TryReal(XMaxBox.Text, out double xMax) ||
            !TryReal(YMinBox.Text, out double yMin) || !TryReal(YMaxBox.Text, out double yMax) ||
            !TryReal(Y2MinBox.Text, out double y2Min) || !TryReal(Y2MaxBox.Text, out double y2Max))
        {
            ShowError("Every axis-limit field must be a number.");
            return;
        }

        bool ok = _timeDomain
            ? _vm.ApplyTimeDomainAxisLimits(xMin, xMax, yMin, yMax, y2Min, y2Max)
            : _vm.ApplyPowerSweepAxisLimits(xMin, xMax, yMin, yMax, y2Min, y2Max);

        if (!ok)
        {
            ShowError("min must be less than max on every axis.");
            return;
        }

        HideError();
    }

    private void OnAutoscaleClick(object? sender, RoutedEventArgs e)
    {
        bool on = AutoscaleCheck.IsChecked == true;
        if (_timeDomain) _vm.SetTimeDomainAutoscale(on);
        else             _vm.SetPowerSweepAutoscale(on);
        SetBoxesEnabled(!on);
        HideError();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    private void HideError() => ErrorText.IsVisible = false;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private static bool TryReal(string? s, out double v)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
}
