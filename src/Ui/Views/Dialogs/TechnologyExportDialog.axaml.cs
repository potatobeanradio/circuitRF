using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>Picks which sections of the current technology to write to a portable `.ctech`.</summary>
public partial class TechnologyExportDialog : Window
{
    // InitializeComponent(), NEVER AvaloniaXamlLoader.Load(this) directly — the generated method
    // loads the XAML *and* assigns every x:Name field. See src/Ui/CLAUDE.md.
    public TechnologyExportDialog() => InitializeComponent();

    public TechnologyExportDialog(string techName, TechSection available) : this()
    {
        HeaderText.Text = $"Export from \"{techName}\"";

        Configure(LayersCheck,  available.HasFlag(TechSection.Layers));
        Configure(StackupCheck, available.HasFlag(TechSection.Stackup));
        Configure(RulesCheck,   available.HasFlag(TechSection.DrcRules));

        LayersCheck.IsCheckedChanged += (_, _) => UpdateNote();
        RulesCheck.IsCheckedChanged  += (_, _) => UpdateNote();
        UpdateNote();
    }

    private static void Configure(CheckBox box, bool available)
    {
        box.IsEnabled = available;
        box.IsChecked = available;
        if (!available) box.Content += "  (nothing to export)";
    }

    /// <summary>
    /// Rules without layers is the most likely way this is misused — "just send me the rules" — and
    /// the result is a rule that looks healthy and measures nothing. Said at the moment of choosing,
    /// where it can still change the answer.
    /// </summary>
    private void UpdateNote() =>
        RulesOnlyNote.IsVisible = RulesCheck.IsChecked == true && LayersCheck.IsChecked != true;

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var s = TechSection.None;
        if (LayersCheck.IsChecked  == true) s |= TechSection.Layers;
        if (StackupCheck.IsChecked == true) s |= TechSection.Stackup;
        if (RulesCheck.IsChecked   == true) s |= TechSection.DrcRules;
        Close(s == TechSection.None ? null : (TechSection?)s);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
