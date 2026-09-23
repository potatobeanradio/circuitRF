using System;
using System.IO;
using Avalonia.Platform.Storage;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Views.Dialogs;

public partial class NonlinearCvEditorView : UserControl
{
    public NonlinearCvEditorView()
    {
        InitializeComponent();
    }

    private NonlinearCvEditorViewModel? Vm => DataContext as NonlinearCvEditorViewModel;

    // ── V column handlers ─────────────────────────────────────────────────────

    private void OnRowVLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is CvRowViewModel row)
        {
            row.CommitV();
            Vm?.Validate();
        }
    }

    private void OnRowVKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Enter or Key.Tab)
        {
            if (sender is TextBox tb && tb.DataContext is CvRowViewModel row)
            {
                row.CommitV();
                Vm?.Validate();
            }
            e.Handled = true; // prevent IsDefault Apply from firing on cell Enter
        }
    }

    // ── C column handlers ─────────────────────────────────────────────────────

    private void OnRowCLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is CvRowViewModel row)
        {
            row.CommitC();
            Vm?.Validate();
        }
    }

    private void OnRowCKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Enter or Key.Tab)
        {
            if (sender is TextBox tb && tb.DataContext is CvRowViewModel row)
            {
                row.CommitC();
                Vm?.Validate();
            }
            e.Handled = true; // prevent IsDefault Apply from firing on cell Enter
        }
    }

    // ── Text mode handler ─────────────────────────────────────────────────────

    private void OnTextBoxLostFocus(object? sender, RoutedEventArgs e)
        => Vm?.Validate();

    // ── Import ────────────────────────────────────────────────────────────────

    /// <summary>Picks a C-V table and hands its text to the view model, which owns every rule.</summary>
    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Import C-V Data",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Comma-separated values") { Patterns = ["*.csv"] },
                new FilePickerFileType("Text") { Patterns = ["*.txt", "*.tsv"] },
            ],
        });
        if (files is not [var file] || file.TryGetLocalPath() is not { Length: > 0 } path) return;

        string text;
        try { text = await File.ReadAllTextAsync(path); }
        catch (Exception ex)
        {
            vm.ImportNote = $"{Path.GetFileName(path)} could not be read: {ex.Message}";
            return;
        }
        vm.ImportCvTable(text, Path.GetFileName(path));
    }

    // ── Footer ────────────────────────────────────────────────────────────────

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        // Close discards staged edits — do NOT apply.
        var win = TopLevel.GetTopLevel(this) as Window;
        win?.Close();
    }

    private void OnHelpClick(object? sender, RoutedEventArgs e)
        => DocLauncher.Open("reference/nonlinear-capacitor.html");
}
