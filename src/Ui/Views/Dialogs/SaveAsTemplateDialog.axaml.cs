using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Views.Dialogs;

public sealed record SaveAsTemplateResult(string Name, string? Description);

/// <summary>
/// Dialog: template name + optional description + preview list of analyses to be saved.
/// Returns <see cref="SaveAsTemplateResult"/> on Save, or null on Cancel.
/// </summary>
public partial class SaveAsTemplateDialog : Window
{
    public SaveAsTemplateDialog() => InitializeComponent();

    /// <summary>
    /// Shows the dialog.  Pre-populates the preview list from <paramref name="analyses"/>.
    /// Returns null if the user cancels or if <paramref name="owner"/> is null.
    /// </summary>
    public static async System.Threading.Tasks.Task<SaveAsTemplateResult?> ShowAsync(
        Window? owner, IReadOnlyList<Analysis> analyses)
    {
        var dlg = new SaveAsTemplateDialog();
        dlg.PreviewHeader.Text = $"Analyses to save ({analyses.Count}):";
        dlg.PreviewList.ItemsSource = analyses
            .Select(a => $"{KindLabel(a)}  ·  {a.Name}")
            .ToList();

        if (owner is null) return null;
        return await dlg.ShowDialog<SaveAsTemplateResult?>(owner);
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)  => TryCommit();
    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Enter) { TryCommit(); e.Handled = true; }
        else if (e.Key == Key.Escape)         { Close(null); e.Handled = true; }
    }

    private void TryCommit()
    {
        var name   = NameBox.Text?.Trim() ?? "";
        var reason = NameValidator.Validate(name);
        if (reason is not null)
        {
            ValidationMessage.Text      = reason;
            ValidationMessage.IsVisible = true;
            return;
        }
        var desc = string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text.Trim();
        Close(new SaveAsTemplateResult(name, desc));
    }

    private static string KindLabel(Analysis a) => a switch
    {
        DcAnalysis              => "DC",
        SParameterAnalysis      => "SP",
        HarmonicBalanceAnalysis => "HB",
        _                       => "?",
    };
}
