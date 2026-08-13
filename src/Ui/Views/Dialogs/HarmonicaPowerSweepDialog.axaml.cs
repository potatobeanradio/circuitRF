using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// R-h9r2-18/18a/17a — the Power Sweep dialog: Start/Stop/Step for the EXPLICIT tier-A ladder, the
/// tickle (on/off + its absolute level), and <c>ExactCompressionSolve</c>. Mirrors
/// <see cref="HarmonicaDcivSweepsDialog"/>'s own commit shape (Return applies and handles the key,
/// Escape reverts, LostFocus applies) and its "invalid input keeps the old value" rule — every commit
/// goes through <see cref="HarmonicaViewModel.ApplyPowerSweepSettings"/>, which validates BEFORE
/// writing anything, so a rejected candidate can never transiently reach <c>Model</c>.
/// </summary>
public partial class HarmonicaPowerSweepDialog : Window
{
    private readonly HarmonicaViewModel _vm;
    private bool _updating;

    // Parameterless ctor satisfies the Avalonia XAML resource loader (AVLN3001).
    public HarmonicaPowerSweepDialog() : this(new HarmonicaViewModel()) { }

    public HarmonicaPowerSweepDialog(HarmonicaViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        RefreshFromModel();
    }

    private void RefreshFromModel()
    {
        _updating = true;
        var s = _vm.Model.Settings;
        StartBox.Text     = Num(s.PinStartDbm);
        StopBox.Text      = Num(s.PinMaxDbm);
        StepBox.Text      = Num(s.PinStepDbm);
        TickleEnabledCheck.IsChecked = s.TickleEnabled;
        TickleDbmBox.Text = Num(s.TickleDbm);
        TickleDbmBox.IsEnabled = s.TickleEnabled;
        ExactCompressionSolveCheck.IsChecked = s.ExactCompressionSolve;
        UpdatePointCount();
        _updating = false;
    }

    private static string Num(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    private void UpdatePointCount()
    {
        if (!TryReal(StartBox.Text, out double start) || !TryReal(StopBox.Text, out double stop) ||
            !TryReal(StepBox.Text, out double step) || !PowerSweepValidation.IsValidRange(start, stop, step, out int count))
        {
            PointCountText.Text = "";
            return;
        }
        PointCountText.Text = $"{count} point{(count == 1 ? "" : "s")}";
    }

    private void OnFieldLostFocus(object? sender, RoutedEventArgs e) => Commit();

    private void OnFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (_updating) return;
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
        else
        {
            UpdatePointCount();          // live preview as the user types, before commit
        }
    }

    private void OnTickleEnabledChanged(object? sender, RoutedEventArgs e)
    {
        if (_updating) return;
        TickleDbmBox.IsEnabled = TickleEnabledCheck.IsChecked == true;
        Commit();
    }

    private void OnExactChanged(object? sender, RoutedEventArgs e)
    {
        if (_updating) return;
        Commit();
    }

    private void Commit()
    {
        if (_updating) return;

        if (!TryReal(StartBox.Text, out double start) || !TryReal(StopBox.Text, out double stop) ||
            !TryReal(StepBox.Text, out double step) || !TryReal(TickleDbmBox.Text, out double tickleDbm))
        {
            ShowError("Every field must be a number.");
            return;
        }

        bool tickleEnabled = TickleEnabledCheck.IsChecked == true;
        bool exact          = ExactCompressionSolveCheck.IsChecked == true;

        if (!_vm.ApplyPowerSweepSettings(start, stop, step, tickleEnabled, tickleDbm, exact, out int count))
        {
            ShowError(!PowerSweepValidation.IsValidRange(start, stop, step, out _)
                ? "start must be less than stop, step must be positive, and the point count must be " +
                  $"at most {HarmonicaSettings.MaxSweepPoints}."
                : $"the tickle must be below start ({start:0.##} dBm).");
            return;
        }

        HideError();
        PointCountText.Text = $"{count} point{(count == 1 ? "" : "s")}";
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
