using System;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.RailRf;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// <b>Edit Model Source…</b> for one series row (owner, 2026-09-24) — its DC resistance, and either an
/// R-L or a Touchstone file, over the same <see cref="RailSeriesEditorViewModel"/> the in-pane editor
/// used, so every value still goes through the railRF window's one write path, its re-solve and its
/// undo.
/// </summary>
/// <remarks>
/// The file picker and Save to library are the WINDOW's — they need its document path and the
/// workspace behind it — and are handed in rather than reached for.
/// </remarks>
public partial class RailSeriesModelDialog : Window
{
    private readonly Action<Window>? _browse;
    private readonly Action? _saveToLibrary;

    public RailSeriesModelDialog() => InitializeComponent();

    public RailSeriesModelDialog(RailSeriesEditorViewModel editor, Action<Window> browse, Action saveToLibrary) : this()
    {
        DataContext = editor;
        Title = $"Edit Model Source — {editor.Refdes}";
        _browse = browse;
        _saveToLibrary = saveToLibrary;

        // A box still holding focus when the window closes has not been left, so its LostFocus
        // commit never fires; push whatever it holds before the dialog goes.
        Closing += (_, _) => CommitAll();

        // Enter applies the box it is pressed in, as it does in every other value field of railRF.
        foreach (var box in new[] { DcrBox, ResistanceBox, InductanceBox, FileBox })
            box.KeyDown += (_, k) =>
            {
                if (k.Key != Key.Enter) return;
                BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateSource();
                k.Handled = true;
            };
    }

    private void CommitAll()
    {
        foreach (var box in new[] { DcrBox, ResistanceBox, InductanceBox, FileBox })
            BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateSource();
    }

    private void OnBrowseClick(object? sender, RoutedEventArgs e) => _browse?.Invoke(this);

    private void OnSaveToLibraryClick(object? sender, RoutedEventArgs e)
    {
        CommitAll();
        _saveToLibrary?.Invoke();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
