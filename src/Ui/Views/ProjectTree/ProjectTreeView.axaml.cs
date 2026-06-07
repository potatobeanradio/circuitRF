using Avalonia.Controls;
using Avalonia.Input;
using CircuitRF.Ui.ViewModels.Dock;
using CircuitRF.Ui.ViewModels.ProjectTree;

namespace CircuitRF.Ui.Views.ProjectTree;

public partial class ProjectTreeView : UserControl
{
    public ProjectTreeView()
    {
        InitializeComponent();
    }

    private void OnItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not ProjectTreeTool tool) return;
        if (tool.SelectedItem is { } item)
            tool.OpenItemCommand.Execute(item);
    }
}
