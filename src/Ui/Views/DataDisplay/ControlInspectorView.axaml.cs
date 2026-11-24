using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.Views.DataDisplay;

public partial class ControlInspectorView : UserControl
{
    public ControlInspectorView() => InitializeComponent();

    private ControlInspectorViewModel? Vm => DataContext as ControlInspectorViewModel;

    /// <summary>Enter commits a field as leaving it does (the bindings update on LostFocus); Escape puts the shown
    /// value back.</summary>
    private void OnFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (e.Key is Key.Enter or Key.Return)
        {
            BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateSource();
            ShowModelValue(box);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateTarget();
            e.Handled = true;
        }
    }

    private void OnFieldLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox box) ShowModelValue(box);
    }

    /// <summary>
    /// After a commit the box shows what the control now holds. A refused name or number leaves the old value in place,
    /// and the binding does not show it again: the property did not change, so a notification raised for it is not
    /// passed on. The box is set from the view model once the write is done, as the current value so its binding stays.
    /// Each box's <c>Tag</c> names its property.
    /// </summary>
    private void ShowModelValue(TextBox box)
    {
        if (box.Tag is not string property || Vm is not { } vm) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(box.DataContext, vm) && vm.TextOf(property) is { } text && box.Text != text)
            {
                box.SetCurrentValue(TextBox.TextProperty, text);
                box.ClearSelection();
                box.CaretIndex = text.Length;
            }
        });
    }

    // The unit box takes any unit typed, and commits as a text field does; a unit picked from its list commits at once.
    private void OnUnitCommit(object? sender, RoutedEventArgs e)
    {
        if (sender is ComboBox cb && Vm is { } vm && (cb.Text ?? "") != vm.UnitText) vm.UnitText = cb.Text ?? "";
    }

    private void OnUnitKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return) || sender is not ComboBox cb || Vm is not { } vm) return;
        vm.UnitText = cb.Text ?? "";
        e.Handled = true;
    }

    private void OnUnitSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: string unit } && Vm is { } vm && unit != vm.UnitText) vm.UnitText = unit;
    }

    private async void PickAccent(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { Target: { } target } vm || TopLevel.GetTopLevel(this) is not Window window) return;
        var color = (vm.AccentSwatch as ISolidColorBrush)?.Color ?? Colors.SteelBlue;
        var picked = await new ColorPickerDialog(new Rgba(color.R, color.G, color.B, 255)).ShowDialog<Rgba?>(window);
        if (picked is { } value && ReferenceEquals(DataContext, vm) && ReferenceEquals(vm.Target, target))
            vm.SetAccent(Color.FromRgb(value.R, value.G, value.B));
    }

    private void ResetAccent(object? sender, RoutedEventArgs e) => Vm?.SetAccent(null);
}
