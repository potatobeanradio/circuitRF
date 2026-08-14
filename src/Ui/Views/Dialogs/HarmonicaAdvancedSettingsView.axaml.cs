using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.Harmonica;
using RfCore.Loadpull;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// brief-harmonicarf-r6a §2.2/§3 — the former HarmonicaAdvancedSettingsDialog's own loadline pts /
/// FFT× / charge / M rows, as a <see cref="HarmonicaSettingsDialog"/> tab, plus §3's new contour
/// surface controls (kernel / smooth / epsilon).
///
/// <para><b>A second surface onto the identical write, exactly like <see cref="HarmonicaSetZ0Dialog"/>
/// — never a second write path.</b> The four original fields commit through
/// <see cref="HarmonicaViewModel.ApplyInput"/> with the SAME key <c>HarmonicaInputs.Build</c> always
/// used for that row. The contour controls commit through
/// <see cref="HarmonicaViewModel.ApplyContourSettings"/> — a NON-structural value write that re-fits
/// the loadpull surface without re-solving a single Γ point (see that method's own remarks).</para>
/// </summary>
public partial class HarmonicaAdvancedSettingsView : UserControl
{
    private HarmonicaViewModel _vm = null!;
    private bool _updating;

    public HarmonicaAdvancedSettingsView()
    {
        InitializeComponent();
        KernelCombo.ItemsSource = new[] { RbfKernel.Multiquadric, RbfKernel.ThinPlate, RbfKernel.Gaussian };
    }

    public void Attach(HarmonicaViewModel vm)
    {
        _vm = vm;
        RefreshFromModel();
    }

    private void RefreshFromModel()
    {
        _updating = true;
        var s = _vm.Model.Settings;
        LoadlineSamplesBox.Text = s.LoadlineSamples.ToString(CultureInfo.InvariantCulture);
        FftOverSampleBox.Text   = s.FftOverSample.ToString(CultureInfo.InvariantCulture);
        MultiplicityBox.Text    = _vm.Model.Dut.Multiplicity.ToString("0.#######", CultureInfo.InvariantCulture);
        ComputeChargeCheck.IsChecked = s.ComputeCharge;

        KernelCombo.SelectedItem  = s.ContourKernel;
        ContourSmoothBox.Text     = s.ContourSmooth.ToString("0.####", CultureInfo.InvariantCulture);
        ContourEpsilonBox.Text    = s.ContourEpsilon?.ToString("0.####", CultureInfo.InvariantCulture) ?? "";
        _updating = false;
    }

    // ── loadline / FFT× / M — unchanged from the former dialog ──────────────────

    private void OnFieldLostFocus(object? sender, RoutedEventArgs e) => Commit(sender as TextBox);

    private void OnFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (_updating) return;
        if (e.Key == Key.Return)
        {
            Commit(sender as TextBox);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            RefreshFromModel();
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

    // ── §3 — the contour surface's own kernel / smooth / epsilon ────────────────

    private void OnKernelChanged(object? sender, SelectionChangedEventArgs e) => CommitContour();

    private void OnContourFieldLostFocus(object? sender, RoutedEventArgs e) => CommitContour();

    private void OnContourFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (_updating) return;
        if (e.Key == Key.Return) { CommitContour(); e.Handled = true; }
        else if (e.Key == Key.Escape) { RefreshFromModel(); HideError(); e.Handled = true; }
    }

    /// <summary>
    /// Reads all three contour controls and commits them together — <see cref="HarmonicaSettings"/>
    /// carries them as one group, so a partial write is not representable. Validation, robustly: smooth
    /// ≥ 0 and finite; epsilon either blank (= auto) or &gt; 0 and finite.
    /// <see cref="HarmonicaViewModel.ApplyContourSettings"/> refuses (never substitutes a value) and
    /// this reverts every box to the model's last-good values, exactly like <see cref="Commit"/> above.
    /// </summary>
    private void CommitContour()
    {
        if (_updating) return;
        if (KernelCombo.SelectedItem is not RbfKernel kernel) { RefreshFromModel(); return; }

        if (!double.TryParse(ContourSmoothBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture,
                              out double smooth))
        {
            ShowError("Smooth must be a number.");
            RefreshFromModel();
            return;
        }

        double? epsilon = null;
        string epsText = ContourEpsilonBox.Text?.Trim() ?? "";
        if (epsText.Length > 0)
        {
            if (!double.TryParse(epsText, NumberStyles.Float, CultureInfo.InvariantCulture, out double eps))
            {
                ShowError("Epsilon must be a number, or blank for auto.");
                RefreshFromModel();
                return;
            }
            epsilon = eps;
        }

        if (!_vm.ApplyContourSettings(kernel, smooth, epsilon))
        {
            ShowError("That contour setting was rejected — smooth must be ≥ 0, epsilon (if given) must be > 0.");
            RefreshFromModel();
            return;
        }
        HideError();
        RefreshFromModel();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    private void HideError() => ErrorText.IsVisible = false;
}
