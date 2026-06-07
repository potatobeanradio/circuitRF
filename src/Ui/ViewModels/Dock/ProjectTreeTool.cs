using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using CircuitRF.Ui.ViewModels.ProjectTree;

namespace CircuitRF.Ui.ViewModels.Dock;

/// <summary>
/// Dock Tool for the Project Tree region (top-left). Hosts a stub tree model in 6b;
/// wired to the real design model in 6c.
/// </summary>
public partial class ProjectTreeTool : Tool
{
    public ObservableCollection<ProjectTreeItemViewModel> Items { get; } = new();

    // The item selected in the tree — used to drive the active Content tab when double-clicked.
    private ProjectTreeItemViewModel? _selectedItem;
    public ProjectTreeItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (_selectedItem == value) return;
            _selectedItem = value;
            OnPropertyChanged();
        }
    }

    public ProjectTreeTool()
    {
        Id    = "ProjectTree";
        Title = "Project Tree";

        // Stub model: a couple of demo libraries and cells so the tree is non-empty.
        var lib1 = new ProjectTreeItemViewModel("My Project", ProjectTreeItemKind.Library) { IsExpanded = true };
        lib1.Children.Add(new ProjectTreeItemViewModel("PA_TestBench",  ProjectTreeItemKind.TestBench));
        lib1.Children.Add(new ProjectTreeItemViewModel("GaN_FET",       ProjectTreeItemKind.Cell));
        lib1.Children.Add(new ProjectTreeItemViewModel("OutputNetwork", ProjectTreeItemKind.Cell));
        lib1.Children.Add(new ProjectTreeItemViewModel("PA_DataDisplay",ProjectTreeItemKind.DataDisplay));

        var lib2 = new ProjectTreeItemViewModel("RfLib", ProjectTreeItemKind.Library);
        lib2.Children.Add(new ProjectTreeItemViewModel("Inductor", ProjectTreeItemKind.Cell));
        lib2.Children.Add(new ProjectTreeItemViewModel("Capacitor", ProjectTreeItemKind.Cell));

        // 6c demo: stress-test cell — opens a programmatically generated 10k-component schematic.
        var demos = new ProjectTreeItemViewModel("Demos", ProjectTreeItemKind.Library) { IsExpanded = true };
        demos.Children.Add(new ProjectTreeItemViewModel("Hero2 PA",      ProjectTreeItemKind.Cell));
        demos.Children.Add(new ProjectTreeItemViewModel("StressTest10k", ProjectTreeItemKind.Cell));

        Items.Add(lib1);
        Items.Add(lib2);
        Items.Add(demos);
    }

    // Called by the view when an item is double-clicked — opens a stub Content tab.
    // The real open logic is injected by WorkspaceViewModel in 6c.
    public System.Action<ProjectTreeItemViewModel>? OpenItemRequested { get; set; }

    [RelayCommand]
    private void OpenItem(ProjectTreeItemViewModel? item)
    {
        if (item is null) return;
        OpenItemRequested?.Invoke(item);
    }
}
