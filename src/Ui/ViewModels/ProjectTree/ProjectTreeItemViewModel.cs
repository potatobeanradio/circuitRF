using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ViewModels.ProjectTree;

public enum ProjectTreeItemKind { Library, Cell, TestBench, DataDisplay }

public partial class ProjectTreeItemViewModel : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private ProjectTreeItemKind _kind;
    [ObservableProperty] private bool _isExpanded;

    public ObservableCollection<ProjectTreeItemViewModel> Children { get; } = new();

    public ProjectTreeItemViewModel(string name, ProjectTreeItemKind kind)
    {
        _name = name;
        _kind = kind;
    }

    // Right-click context menu commands — placeholder handlers for 6b.
    [RelayCommand] private void Open() { /* wired in 6c */ }
    [RelayCommand] private void OpenInNewWindow() { /* wired in 6c */ }
    [RelayCommand] private void Delete() { /* wired in 6c */ }
}
