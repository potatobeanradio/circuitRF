using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CircuitRF.Design.Chain;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.Chain;

namespace CircuitRF.Ui.Views.Chain;

/// <summary>
/// The RF Chain Budget window. The view model holds the chain and its cascade; this holds what needs a window — the file
/// pickers, the confirmation, and Transfer to Schematic, which writes the three benches and the display through the
/// calls <c>circuitrf chain --to-schematic</c> makes, then opens them in the workspace window that opened this one.
/// </summary>
public partial class ChainBudgetWindow : Window, ICrfMenuWindow
{
    /// <summary>One window per file: a second request for a file already open brings that window forward.</summary>
    private static readonly Dictionary<string, ChainBudgetWindow> OpenByPath = new(StringComparer.OrdinalIgnoreCase);

    private Window? _owner;

    public string WindowMenuHeader => Vm?.Title ?? ChainBudgetViewModel.ToolName;

    public ChainBudgetWindow()
    {
        InitializeComponent();
        // The table is a bare PlotControl, refilled in place, so this window repaints it and gives it the theme, as the
        // S-Parameter Viewer does its own.
        DataContextChanged += (_, _) =>
        {
            SyncPlotTheme();
            if (Vm is { } vm) vm.PlotsUpdated += (_, _) => this.FindControl<CircuitRF.Ui.DataDisplay.Controls.PlotControl>("CascadePlotControl")?.InvalidateVisual();
        };
        ActualThemeVariantChanged += (_, _) => SyncPlotTheme();
        Closed += (_, _) =>
        {
            foreach (var k in OpenByPath.Where(kv => ReferenceEquals(kv.Value, this)).Select(kv => kv.Key).ToList()) OpenByPath.Remove(k);
        };
    }

    private ChainBudgetViewModel? Vm => DataContext as ChainBudgetViewModel;

    private void SyncPlotTheme()
    {
        if (Vm is null) return;
        Vm.PlotHost.Theme = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? CircuitRF.Render.DataDisplay.RenderTheme.Dark : CircuitRF.Render.DataDisplay.RenderTheme.Light;
        this.FindControl<CircuitRF.Ui.DataDisplay.Controls.PlotControl>("CascadePlotControl")?.SetValue(CircuitRF.Ui.DataDisplay.Controls.PlotControl.PlotThemeProperty, Vm.PlotHost.Theme);
    }

    /// <summary>Tools ▸ RF Chain Budget: a new chain.</summary>
    public static ChainBudgetWindow ShowStandalone(Window? owner)
    {
        var w = Create(owner, new ChainBudgetViewModel());
        Present(w, owner);
        return w;
    }

    /// <summary>The window on <paramref name="path"/>: the one already showing it, or a new one reading it.</summary>
    public static ChainBudgetWindow? ShowForFile(string path, Window? owner)
    {
        string full = Path.GetFullPath(path);
        if (OpenByPath.TryGetValue(full, out var open))
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return open;
        }
        var vm = new ChainBudgetViewModel();
        try { vm.LoadFile(full); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            WorkspaceLocator.For(owner)?.Messages.Error($"{Path.GetFileName(full)} did not open: {ex.Message}");
            return null;
        }
        var w = Create(owner, vm);
        OpenByPath[full] = w;
        Present(w, owner);
        return w;
    }

    private static ChainBudgetWindow Create(Window? owner, ChainBudgetViewModel vm)
    {
        var w = new ChainBudgetWindow { DataContext = vm, _owner = owner };
        vm.PickOpenPathAsync = w.PickOpenAsync;
        vm.PickSavePathAsync = w.PickSaveAsync;
        vm.Report = text => Messages(owner)?.Warning(text);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChainBudgetViewModel.FilePath) && vm.FilePath is { } p) OpenByPath[p] = w;
        };
        return w;
    }

    private static CircuitRF.Ui.Messages.IMessageSink? Messages(Window? owner) => WorkspaceLocator.For(owner)?.Messages;

    private static void Present(ChainBudgetWindow window, Window? owner)
    {
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (owner is not null)
        {
            void CloseWithOwner(object? _, EventArgs __) => window.Close();
            owner.Closed += CloseWithOwner;
            window.Closed += (_, _) => owner.Closed -= CloseWithOwner;
        }
        window.Show();
        if (owner is not null) NewWindowFront.Keep(window, owner);
    }

    private static readonly FilePickerFileType ChainFiles = new("RF chain budget (*.cchain)") { Patterns = ["*" + ChainDesignIo.Extension] };

    private async Task<string?> PickOpenAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open RF Chain Budget", AllowMultiple = false, FileTypeFilter = [ChainFiles, FilePickerFileTypes.All],
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<string?> PickSaveAsync(string? current)
    {
        string? dir = current is not null ? Path.GetDirectoryName(current)
                    : WorkspaceLocator.For(_owner)?.CurrentWorkspacePath is { } ws ? Path.GetDirectoryName(ws) : null;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save RF Chain Budget",
            SuggestedFileName = current is not null ? Path.GetFileName(current) : "Chain" + ChainDesignIo.Extension,
            SuggestedStartLocation = dir is not null ? await StorageProvider.TryGetFolderFromPathAsync(dir) : null,
            DefaultExtension = ChainDesignIo.Extension.TrimStart('.'),
            ShowOverwritePrompt = true,
            FileTypeChoices = [ChainFiles],
        });
        return file?.TryGetLocalPath();
    }

    private void OnHelpClick(object? sender, RoutedEventArgs e) => DocLauncher.Open("reference/chain-budget.html");

    /// <summary>
    /// Transfer to Schematic: the chain is saved first, because the benches go beside it — into the folder the
    /// <c>.cchain</c> is in, named after it — and a display that reads results needs a workspace to find them in.
    /// </summary>
    private async void OnTransferClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var sink = Messages(_owner);
        if (vm.Budget is { Totals: null } refused)
        {
            sink?.Error("Transfer to Schematic: " + string.Join(" ", refused.Refusals));
            return;
        }
        if ((vm.IsDirty || vm.FilePath is null) && !await vm.Save()) return;
        string dir = Path.GetDirectoryName(vm.FilePath!)!, name = Path.GetFileNameWithoutExtension(vm.FilePath!);
        var design = vm.ToDesign();

        bool replace = false;
        var existing = Enum.GetValues<ChainBench>().Select(b => ChainBenches.CellName(name, b))
                           .Where(c => Directory.Exists(Path.Combine(dir, c))).ToList();
        if (existing.Count > 0)
        {
            replace = await Dialogs.TextConfirmDialog.AskAsync(this, ChainBudgetViewModel.ToolName, "Replace the benches?",
                $"{string.Join(", ", existing)} already {(existing.Count == 1 ? "exists" : "exist")} beside {Path.GetFileName(vm.FilePath)}. " +
                "Replacing writes them again from this chain; anything changed in them by hand is lost.", "Replace");
            if (!replace) return;
        }

        var written = ChainBenches.Write(design, dir, name, replace);
        if (written.SchematicPaths.Count == 0)
        {
            foreach (var r in written.Refusals) sink?.Error("Transfer to Schematic: " + r);
            return;
        }
        _ = ChainBudgetDisplay.WriteAll(dir, name, design, ChainBudget.Evaluate(design));
        foreach (var n in written.Notes) sink?.Info(n);
        sink?.Info($"Transferred {name}: {string.Join(", ", written.SchematicPaths.Select(Path.GetFileNameWithoutExtension))}, each with " +
                   "its Data Display beside it. Simulate a bench and its display opens on the result, the budget beside every simulated number.",
                   written.SchematicPaths[0]);
        var ws = WorkspaceLocator.For(_owner);
        ws?.OpenDocumentByPath(written.SchematicPaths[0]);
    }
}
