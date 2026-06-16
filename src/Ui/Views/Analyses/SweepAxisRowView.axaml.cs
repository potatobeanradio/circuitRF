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
        // Wire the Remove button click → parent AnalysisEditorViewModel.RemoveSweepAxisCommand.
        // Tag holds the SweepAxisRowViewModel; we walk up to find the editor VM.
        if (RemoveButton is Button btn)
        {
            btn.Click += OnRemoveClick;
        }
    }

    private void OnRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SweepAxisRowViewModel row) return;

        // Walk the visual tree to find the AnalysisEditorViewModel.
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
}
