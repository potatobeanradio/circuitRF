using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// "Offset…" dimension prompt (docs/sonnet-briefs/brief-L1e-clipper-operations.md §3: "Dimension
/// field, unit-suffixed"). Unlike most staged dimension fields on <see cref="LayoutEditorViewModel"/>,
/// a negative value is valid and meaningful here (shrink), so validation only rejects text that fails
/// to parse at all — not non-positive values. Returns the raw validated text via
/// <c>ShowDialog&lt;string?&gt;</c> (the caller commits it through
/// <see cref="LayoutEditorViewModel.CommitOffsetText"/>, reusing the same staged-field parse path the
/// rest of this VM's dimension fields already go through), or null on cancel.
/// </summary>
public partial class OffsetDialog : Window
{
    private readonly LayoutEditorViewModel? _vm;

    public OffsetDialog() => InitializeComponent();

    public OffsetDialog(LayoutEditorViewModel vm) : this()
    {
        _vm = vm;
        // Pre-fill with the last-used offset (staged on the VM) so repeated offsets in one session
        // don't require re-typing the same distance; falls back to a formatted zero on first use.
        OffsetBox.Text = string.IsNullOrEmpty(vm.OffsetText)
            ? LayoutUnits.Format(0, vm.DisplayUnit, vm.Model.DbuPerMicron)
            : vm.OffsetText;
        Opened += (_, _) => { OffsetBox.Focus(); OffsetBox.SelectAll(); };
    }

    private void OnOffsetChanged(object? sender, TextChangedEventArgs e) => UpdateValidation();

    private bool IsValid() =>
        _vm is not null && LayoutUnits.TryParse(OffsetBox.Text ?? "", _vm.DisplayUnit, _vm.Model.DbuPerMicron, out _);

    private void UpdateValidation() => ValidationMessage.IsVisible = !IsValid();

    private void OnOkClick(object? sender, RoutedEventArgs e) => TryCommit();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Enter) { TryCommit(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close(null); e.Handled = true; }
    }

    private void TryCommit()
    {
        if (!IsValid()) { ValidationMessage.IsVisible = true; return; }
        Close(OffsetBox.Text);
    }
}
