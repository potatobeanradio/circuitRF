using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.Theming;
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
    private HarmonicaColorEditor _editor = null!;
    private bool _updating;

    public HarmonicaAdvancedSettingsView()
    {
        InitializeComponent();
        KernelCombo.ItemsSource = new[] { RbfKernel.Multiquadric, RbfKernel.ThinPlate, RbfKernel.Gaussian };
    }

    /// <summary>
    /// R8A §3 — this tab now also owns the iso-line fade sliders and label toggle, moved here verbatim
    /// from <see cref="HarmonicaAppearanceSettingsView"/>. Both write through <c>_editor</c>
    /// (<c>_editor.IsoAlphaFloor</c>/<c>_editor.ShowIsoLineLabels</c>) exactly as they did there — ONE
    /// <see cref="HarmonicaColorEditor"/> instance, handed to both tabs by
    /// <see cref="HarmonicaSettingsDialog"/>, never two.
    /// </summary>
    public void Attach(HarmonicaViewModel vm, HarmonicaColorEditor editor)
    {
        _vm     = vm;
        _editor = editor;
        RefreshFromModel();
        LoadFade();
        LoadTickleDefault();
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

    // ── R8A §3 — moved verbatim from HarmonicaAppearanceSettingsView ────────────
    // ── §7.2's fade parameters ───────────────────────────────────────────────

    private void LoadFade()
    {
        _updating = true;
        try
        {
            AlphaFloorSlider.Value = _editor.IsoAlphaFloor;
            AlphaExpSlider.Value   = _editor.IsoAlphaExponent;
            IsoLabelsCheck.IsChecked = _editor.ShowIsoLineLabels;
            AlphaFloorLabel.Text = _editor.IsoAlphaFloor.ToString("0.00");
            AlphaExpLabel.Text   = _editor.IsoAlphaExponent.ToString("0.00");
        }
        finally { _updating = false; }
    }

    private void OnFadeChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updating) return;
        _editor.IsoAlphaFloor    = AlphaFloorSlider.Value;
        _editor.IsoAlphaExponent = AlphaExpSlider.Value;
        AlphaFloorLabel.Text = AlphaFloorSlider.Value.ToString("0.00");
        AlphaExpLabel.Text   = AlphaExpSlider.Value.ToString("0.00");
    }

    private void OnIsoLabelsChanged(object? sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _editor.ShowIsoLineLabels = IsoLabelsCheck.IsChecked == true;
        _vm.ShowIsoLineLabels     = _editor.ShowIsoLineLabels;
    }

    // ── R-h9r2-18a — the tickle default a brand new document seeds from ─────────

    private void LoadTickleDefault()
    {
        _updating = true;
        try
        {
            TickleDefaultEnabledCheck.IsChecked = HarmonicaTickleDefaults.Enabled;
            TickleDefaultDbmBox.Text = HarmonicaTickleDefaults.Dbm.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            TickleDefaultDbmBox.IsEnabled = HarmonicaTickleDefaults.Enabled;
        }
        finally { _updating = false; }
    }

    private void OnTickleDefaultChanged(object? sender, RoutedEventArgs e)
    {
        if (_updating) return;
        TickleDefaultDbmBox.IsEnabled = TickleDefaultEnabledCheck.IsChecked == true;
        CommitTickleDefault();
    }

    private void OnTickleDefaultDbmLostFocus(object? sender, RoutedEventArgs e) => CommitTickleDefault();

    private void OnTickleDefaultDbmKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return) { CommitTickleDefault(); e.Handled = true; }
        else if (e.Key == Key.Escape) { LoadTickleDefault(); e.Handled = true; }
    }

    private void CommitTickleDefault()
    {
        if (_updating) return;
        if (!double.TryParse(TickleDefaultDbmBox.Text, System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out double dbm))
        {
            LoadTickleDefault();
            return;
        }

        bool enabled = TickleDefaultEnabledCheck.IsChecked == true;
        AppPreferencesIo.Update(p =>
        {
            p.HarmonicaTickleEnabled = enabled;
            p.HarmonicaTickleDbm     = dbm;
        });
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
