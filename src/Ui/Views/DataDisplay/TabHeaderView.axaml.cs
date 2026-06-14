using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CircuitRF.Ui.DataDisplay.ViewModels;

namespace CircuitRF.Ui.Views.DataDisplay;

public partial class TabHeaderView : UserControl
{
    public TabHeaderView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is TabViewModel vm)
            vm.PropertyChanged += (_, pe) =>
            {
                if (pe.PropertyName == nameof(TabViewModel.IsEditingName) && vm.IsEditingName)
                    Dispatcher.UIThread.Post(FocusEditBox);
            };
    }

    private void FocusEditBox()
    {
        var tb = this.FindControl<TextBox>("TabNameEdit");
        if (tb is null) return;
        tb.Focus();
        tb.SelectAll();
    }

    private void OnNameTextDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TabViewModel vm)
            vm.IsEditingName = true;
    }

    private void OnNameEditKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Escape)
        {
            if (DataContext is TabViewModel vm)
                vm.IsEditingName = false;
            e.Handled = true;
        }
    }

    private void OnNameEditLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TabViewModel vm)
            vm.IsEditingName = false;
    }
}
