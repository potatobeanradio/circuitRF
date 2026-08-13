using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.Harmonica;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// R-h9r2-20 — "Currently no way for the user to set Z0 of the Smith Charts. Add a Set Z0 to menu."
/// Z0 was already user-settable (R-h9b-6's §7.5 input row, <see cref="HarmonicaInputs.KeyZ0"/>) — the
/// owner simply had not found it, which is a discoverability report, not an absence. This is a SECOND
/// SURFACE onto the identical write: every commit here goes through
/// <see cref="HarmonicaViewModel.ApplyInput"/> with <see cref="HarmonicaInputs.KeyZ0"/>, the same
/// parse, the same non-structural classification, the same re-solve — never a second write to
/// <c>Model.Settings.Z0</c>, or the strip row and this dialog could drift apart on validation.
/// </summary>
public partial class HarmonicaSetZ0Dialog : Window
{
    private readonly HarmonicaViewModel _vm;
    private bool _updating;

    // Parameterless ctor satisfies the Avalonia XAML resource loader (AVLN3001).
    public HarmonicaSetZ0Dialog() : this(new HarmonicaViewModel()) { }

    public HarmonicaSetZ0Dialog(HarmonicaViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        RefreshFromModel();
        Opened += (_, _) => { Z0Box.Focus(); Z0Box.SelectAll(); };
    }

    private void RefreshFromModel()
    {
        _updating = true;
        Z0Box.Text = _vm.Model.Settings.Z0.ToString("0.####", CultureInfo.InvariantCulture);
        _updating = false;
    }

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
            RefreshFromModel();          // revert to the last-applied value
            HideError();
            e.Handled = true;
        }
    }

    private void Commit()
    {
        if (_updating) return;

        if (!_vm.ApplyInput(HarmonicaInputs.KeyZ0, Z0Box.Text ?? ""))
        {
            ShowError("Z0 must be a positive number.");
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

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
