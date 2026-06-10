using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// Modal host for <see cref="AnalysesListViewModel"/> — the same VM the dock panel uses.
/// Open via <c>WorkspaceViewModel.SetupAnalysesCommand</c>.
/// </summary>
public partial class SetupAnalysesDialog : Window
{
    public SetupAnalysesDialog() => InitializeComponent();

    public SetupAnalysesDialog(AnalysesListViewModel vm) : this()
    {
        DataContext = vm;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
