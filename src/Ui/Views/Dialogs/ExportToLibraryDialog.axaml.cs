// Export to Library's dialog — brief vendorlib-10 R-vl10-3. The state is ExportToLibraryViewModel's; this file owns the
// pickers (the library folder, the documents), the symbol menu, the replace question, and closing with the result.
// Export writes; Cancel writes nothing, and during an export it stops it with nothing left behind.

using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.VendorLibraries.ModelLibrary;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Views.Dialogs;

public partial class ExportToLibraryDialog : Window
{
    private readonly ExportToLibraryViewModel? _vm;

    /// <summary>For the XAML previewer only.</summary>
    public ExportToLibraryDialog() => InitializeComponent();

    public ExportToLibraryDialog(ExportToLibraryViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        vm.PropertyChanged += OnVmChanged;
        ShowSymbol();
        Opened += (_, _) => { PartBox.Focus(); PartBox.SelectAll(); };
    }

    /// <summary>Shows the dialog and returns the finished export, or null when nothing was exported.</summary>
    public static Task<ExportResult?> AskAsync(Window owner, ExportToLibraryViewModel vm)
        => new ExportToLibraryDialog(vm).ShowDialog<ExportResult?>(owner);

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ExportToLibraryViewModel.CurrentSymbol)) ShowSymbol();
    }

    private void ShowSymbol()
    {
        if (_vm?.CurrentSymbol is not { } row || SymbolGlyph is null) return;
        SymbolGlyph.Kind = row.Kind;
        SymbolGlyph.PortCount = row.PortCount;
        SymbolGlyph.SymbolCellDir = row.GlyphRef;
    }

    private void OnSymbolButton(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var flyout = new MenuFlyout();
        foreach (var row in _vm.Symbols)
        {
            var item = new MenuItem
            {
                Header = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children =
                    {
                        new PaletteGlyphControl { Kind = row.Kind, PortCount = row.PortCount, SymbolCellDir = row.GlyphRef, Width = 48, Height = 40 },
                        new TextBlock { Text = row.Label, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 },
                    },
                },
            };
            var chosen = row;
            item.Click += (_, _) => _vm.Symbol = chosen.Value;
            flyout.Items.Add(item);
        }
        flyout.ShowAt(SymbolButton);
    }

    private void OnKindChosen(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || (sender as ComboBox)?.SelectedItem is not string kind || kind == _vm.EffectiveKind) return;
        _vm.Kind = kind;
    }

    private void OnKnownLibraryChosen(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is not null && (sender as ComboBox)?.SelectedItem is ExportLibraryChoice c) _vm.LibraryPath = c.Root;
    }

    private async void OnBrowseLibrary(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Export to Library — choose the library folder (an empty folder starts a new library)",
            AllowMultiple = false,
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { Length: > 0 } path) _vm.LibraryPath = path;
    }

    private async void OnAddDocument(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Export to Library — documents to copy into the model's docs/", AllowMultiple = true,
        });
        foreach (var f in files)
            if (f.TryGetLocalPath() is { Length: > 0 } path && !_vm.Documents.Contains(path)) _vm.Documents.Add(path);
        _vm.Replan();
    }

    private void OnRemoveDocument(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || DocumentsList.SelectedItem is not string path) return;
        _vm.Documents.Remove(path);
        _vm.Replan();
    }

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { CanExport: true, Plan: { } plan }) return;
        bool replace = false;
        if (plan.Exists)
        {
            replace = await TextConfirmDialog.AskAsync(this, "Export to Library", $"Replace {plan.PartNumber}?",
                $"{plan.PartNumber} is already exported to this library (revision {plan.ExistingRevision}). Exporting again replaces it as revision {plan.ExistingRevision + 1}.",
                "Replace");
            if (!replace) return;
        }
        var result = await _vm.Export(replace);
        if (result.Succeeded) Close(result);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        if (_vm is { IsExporting: true }) { _vm.Cancel(); return; }
        Close(null);
    }
}
