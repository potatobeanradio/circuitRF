using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Views.Properties;

/// <summary>Code-behind for <see cref="PCellParameterListView"/> — see that file's own doc comment
/// for why this exists as a standalone control. Handlers moved verbatim from
/// <c>LayoutShapePropertiesView.axaml.cs</c>, unchanged.</summary>
public partial class PCellParameterListView : UserControl
{
    public PCellParameterListView() => InitializeComponent();

    private LayoutShapePropertiesViewModel? Vm => DataContext as LayoutShapePropertiesViewModel;

    private void OnPCellParamFieldGotFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: PCellParamRowViewModel row })
            Vm?.SetFocusedField(row.FieldKey);
    }

    private void OnPCellParamFieldLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: PCellParamRowViewModel row } tb) return;
        Vm?.SetFocusedField(null);
        row.Commit(tb.Text ?? "");
    }

    private void OnPCellParamFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: PCellParamRowViewModel row } tb) return;
        if (e.Key is Key.Enter or Key.Return)
            row.Commit(tb.Text ?? "");
    }
}
