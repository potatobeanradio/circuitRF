using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Design.Layout.Interchange;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// Which step of an IPC-2581 file to import, asked only when it holds several, because a file with a panel step and a
/// board step is a question only the user can answer (brief-ipx-4 R-ipx4-2c). A panel holds no artwork of its own, so
/// the first step that is not a panel starts selected. Returns the step's name, or null on Cancel, which aborts and
/// creates nothing.
/// </summary>
public partial class Ipc2581StepDialog : Window
{
    private readonly IReadOnlyList<Ipc2581Step> _steps = [];

    public Ipc2581StepDialog() => InitializeComponent();

    public Ipc2581StepDialog(IReadOnlyList<Ipc2581Step> steps) : this()
    {
        _steps = steps;
        StepList.ItemsSource = steps.Select(Row).ToList();
        int first = steps.ToList().FindIndex(s => !s.IsPanel);
        StepList.SelectedIndex = Math.Max(first, 0);
        Opened += (_, _) => ImportButton.Focus();
    }

    /// <summary>One step as the list shows it.</summary>
    public static string Row(Ipc2581Step step) => step.IsPanel
        ? $"{step.Name}: a panel placing {string.Join(", ", step.Places)}"
        : $"{step.Name}: {step.LayerCount} layer(s), {step.NetCount} net(s)";

    private void Choose()
    {
        if (StepList.SelectedIndex >= 0) Close(_steps[StepList.SelectedIndex].Name);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnImportClick(object? sender, RoutedEventArgs e) => Choose();

    private void OnStepDoubleTapped(object? sender, TappedEventArgs e) => Choose();
}
