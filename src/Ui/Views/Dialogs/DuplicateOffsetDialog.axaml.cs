using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>The settled offset of a "Duplicate…", in the layout's own DBU — handed straight to
/// <see cref="LayoutEditorViewModel.Duplicate(long,long)"/>.</summary>
public sealed record DuplicateOffsetResult(long DxDbu, long DyDbu);

/// <summary>
/// "Duplicate…" — an X/Y offset prompt in the layout's DISPLAY unit (owner, 2026-08-27), shown by
/// both surfaces that reach the command: Ctrl+D and the canvas context menu. Mirrors
/// <see cref="OffsetDialog"/>'s shape exactly (a <see cref="Window"/> returning a typed result via
/// <c>ShowDialog&lt;T&gt;</c>, or null on cancel) and parses through the same
/// <see cref="LayoutUnits.TryParse"/> path, so "500nm" in a document displaying µm means what it
/// says.
///
/// <para>Both fields default to a formatted ZERO on every opening rather than pre-filling the
/// last-used offset the way <see cref="OffsetDialog"/> does: a duplicate landing exactly on top of
/// the original is the asked-for default, and silently reusing a previous offset would move a copy
/// somewhere the user did not ask for it this time.</para>
///
/// <para>A negative value is valid in both fields (the layout's world is Y-UP, so a negative Y moves
/// the copy down), so validation only rejects text that fails to parse at all.</para>
/// </summary>
public partial class DuplicateOffsetDialog : Window
{
    private readonly LayoutEditorViewModel? _vm;

    public DuplicateOffsetDialog() => InitializeComponent();

    public DuplicateOffsetDialog(LayoutEditorViewModel vm) : this()
    {
        _vm = vm;
        string zero = LayoutUnits.Format(0, vm.DisplayUnit, vm.Model.DbuPerMicron);
        OffsetXBox.Text = zero;
        OffsetYBox.Text = zero;
        Opened += (_, _) => { OffsetXBox.Focus(); OffsetXBox.SelectAll(); };
    }

    private void OnOffsetChanged(object? sender, TextChangedEventArgs e) => UpdateValidation();

    private bool TryRead(out long dx, out long dy)
    {
        dx = dy = 0;
        return _vm is not null
            && LayoutUnits.TryParse(OffsetXBox.Text ?? "", _vm.DisplayUnit, _vm.Model.DbuPerMicron, out dx)
            && LayoutUnits.TryParse(OffsetYBox.Text ?? "", _vm.DisplayUnit, _vm.Model.DbuPerMicron, out dy);
    }

    private void UpdateValidation() => ValidationMessage.IsVisible = !TryRead(out _, out _);

    private void OnOkClick(object? sender, RoutedEventArgs e) => TryCommit();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Enter) { TryCommit(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close(null); e.Handled = true; }
    }

    private void TryCommit()
    {
        if (!TryRead(out long dx, out long dy)) { ValidationMessage.IsVisible = true; return; }
        Close(new DuplicateOffsetResult(dx, dy));
    }
}
