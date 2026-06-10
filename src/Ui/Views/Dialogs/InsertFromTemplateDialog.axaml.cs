using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// Template picker with minimal Manage (delete).
/// Shows all templates from the resolution chain (workspace → user).
/// Returns the selected <see cref="AnalysisTemplate"/> on Insert, or null on Cancel.
/// </summary>
public partial class InsertFromTemplateDialog : Window
{
    private string? _workspaceDir;
    private IReadOnlyList<AnalysisTemplate> _templates = [];

    public InsertFromTemplateDialog() => InitializeComponent();

    public static async System.Threading.Tasks.Task<AnalysisTemplate?> ShowAsync(
        Window? owner, string? workspaceDir)
    {
        if (owner is null) return null;
        var dlg = new InsertFromTemplateDialog { _workspaceDir = workspaceDir };
        dlg.RefreshTemplateList();
        return await dlg.ShowDialog<AnalysisTemplate?>(owner);
    }

    private void RefreshTemplateList()
    {
        _templates = TemplateManager.LoadAll(_workspaceDir);
        TemplateList.ItemsSource = _templates;
        EmptyState.IsVisible     = _templates.Count == 0;
        TemplateList.IsVisible   = _templates.Count > 0;
        PreviewSection.IsVisible = false;
        InsertButton.IsEnabled   = false;
    }

    private void OnTemplateSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (TemplateList.SelectedItem is not AnalysisTemplate t)
        {
            PreviewSection.IsVisible = false;
            InsertButton.IsEnabled   = false;
            return;
        }

        PreviewHeader.Text = $"Analyses in this template ({t.Analyses.Count}):";
        AnalysisPreview.ItemsSource = t.Analyses
            .Select(a => $"{KindLabel(a)}  ·  {a.Name}")
            .ToList();
        PreviewSection.IsVisible = true;
        InsertButton.IsEnabled   = true;
    }

    private void OnInsertClick(object? sender, RoutedEventArgs e)
    {
        if (TemplateList.SelectedItem is AnalysisTemplate t)
            Close(t);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (TemplateList.SelectedItem is not AnalysisTemplate t) return;
        try
        {
            TemplateManager.DeleteTemplate(t.FilePath);
        }
        catch { /* file may already be gone — proceed */ }
        RefreshTemplateList();
    }

    private static string KindLabel(CircuitRF.Core.Design.Analysis a) => a switch
    {
        CircuitRF.Core.Design.DcAnalysis              => "DC",
        CircuitRF.Core.Design.SParameterAnalysis      => "SP",
        CircuitRF.Core.Design.HarmonicBalanceAnalysis => "HB",
        _                                             => "?",
    };
}
