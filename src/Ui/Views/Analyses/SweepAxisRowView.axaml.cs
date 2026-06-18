using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Views.Analyses;

public partial class SweepAxisRowView : UserControl
{
    public SweepAxisRowView() => InitializeComponent();

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        // Wire Up/Down/Remove buttons → parent AnalysisEditorViewModel commands via visual-tree walk.
        if (RemoveButton   is Button rm)   rm.Click   += OnRemoveClick;
        if (MoveUpButton   is Button up)   up.Click   += OnMoveUpClick;
        if (MoveDownButton is Button down) down.Click += OnMoveDownClick;
    }

    private void OnRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SweepAxisRowViewModel row) return;
        var ctrl = Parent;
        while (ctrl is not null)
        {
            if (ctrl.DataContext is AnalysisEditorViewModel editorVm)
            {
                editorVm.RemoveSweepAxisCommand.Execute(row);
                return;
            }
            ctrl = ctrl.Parent as Control;
        }
    }

    private void OnMoveUpClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SweepAxisRowViewModel row) return;
        var ctrl = Parent;
        while (ctrl is not null)
        {
            if (ctrl.DataContext is AnalysisEditorViewModel editorVm)
            {
                editorVm.MoveSweepAxisUpCommand.Execute(row);
                return;
            }
            ctrl = ctrl.Parent as Control;
        }
    }

    private void OnMoveDownClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SweepAxisRowViewModel row) return;
        var ctrl = Parent;
        while (ctrl is not null)
        {
            if (ctrl.DataContext is AnalysisEditorViewModel editorVm)
            {
                editorVm.MoveSweepAxisDownCommand.Execute(row);
                return;
            }
            ctrl = ctrl.Parent as Control;
        }
    }
}
