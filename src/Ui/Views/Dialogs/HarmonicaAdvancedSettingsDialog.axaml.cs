using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.Harmonica;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// Owner request (2026-08-13) — "Remove the loadline pts, FFTx, charge and M display settings (and
/// the horizontal bar below them) from the display. These are to be set via a menu item AND a
/// settings in a separate dialog." <see cref="ReadoutStripView"/> no longer renders
/// <see cref="HarmonicaInputs.KeyLoadlineSamples"/>/<see cref="HarmonicaInputs.KeyFftOverSample"/>/
/// <see cref="HarmonicaInputs.KeyComputeCharge"/>/<see cref="HarmonicaInputs.KeyMultiplicity"/> at all
/// (see its own <c>HiddenFromStripKeys</c>) — this dialog is where they live instead.
///
/// <para><b>A second surface onto the identical write, exactly like <see cref="HarmonicaSetZ0Dialog"/>
/// — never a second write path.</b> Every field here commits through
/// <see cref="HarmonicaViewModel.ApplyInput"/> with the SAME key <c>HarmonicaInputs.Build</c> always
/// used for that row, so this dialog and the model can never drift apart on validation, undo or
/// structural classification. Four INDEPENDENT fields, each with its own key — unlike
/// <see cref="HarmonicaPowerSweepDialog"/>'s combined Start/Stop/Step (one range, one validator),
/// there is no cross-field relationship here to validate together.</para>
/// </summary>
public partial class HarmonicaAdvancedSettingsDialog : Window
{
    private readonly HarmonicaViewModel _vm;
    private bool _updating;

    // Parameterless ctor satisfies the Avalonia XAML resource loader (AVLN3001).
    public HarmonicaAdvancedSettingsDialog() : this(new HarmonicaViewModel()) { }

    public HarmonicaAdvancedSettingsDialog(HarmonicaViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        RefreshFromModel();
        Opened += (_, _) => LoadlineSamplesBox.Focus();
    }

    private void RefreshFromModel()
    {
        _updating = true;
        var s = _vm.Model.Settings;
        LoadlineSamplesBox.Text = s.LoadlineSamples.ToString(CultureInfo.InvariantCulture);
        FftOverSampleBox.Text   = s.FftOverSample.ToString(CultureInfo.InvariantCulture);
        MultiplicityBox.Text    = _vm.Model.Dut.Multiplicity.ToString("0.#######", CultureInfo.InvariantCulture);
        ComputeChargeCheck.IsChecked = s.ComputeCharge;
        _updating = false;
    }

    private void OnFieldLostFocus(object? sender, RoutedEventArgs e) => Commit(sender as TextBox);

    private void OnFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (_updating) return;
        if (e.Key == Key.Return)
        {
            Commit(sender as TextBox);
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

    private void Commit(TextBox? box)
    {
        if (_updating || box is null) return;

        string key = ReferenceEquals(box, LoadlineSamplesBox) ? HarmonicaInputs.KeyLoadlineSamples
                   : ReferenceEquals(box, FftOverSampleBox)   ? HarmonicaInputs.KeyFftOverSample
                   : ReferenceEquals(box, MultiplicityBox)    ? HarmonicaInputs.KeyMultiplicity
                   : "";
        if (key.Length == 0) return;

        if (!_vm.ApplyInput(key, box.Text ?? ""))
        {
            ShowError(_vm.InputError ?? "That value was rejected.");
            return;
        }
        HideError();
        RefreshFromModel();
    }

    private void OnComputeChargeClick(object? sender, RoutedEventArgs e)
    {
        if (_updating) return;
        string next = ComputeChargeCheck.IsChecked == true ? "1" : "0";
        if (!_vm.ApplyInput(HarmonicaInputs.KeyComputeCharge, next))
        {
            ShowError(_vm.InputError ?? "That value was rejected.");
            RefreshFromModel();          // put the checkbox back — the model did not move
            return;
        }
        HideError();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    private void HideError() => ErrorText.IsVisible = false;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
