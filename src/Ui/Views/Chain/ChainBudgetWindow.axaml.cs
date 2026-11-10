using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using CircuitRF.Design.Chain;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.Chain;
using CircuitRF.Ui.Controls;

namespace CircuitRF.Ui.Views.Chain;

/// <summary>
/// The RF Chain Budget window. The view model holds the chain and its cascade; this holds what needs a window — the file
/// pickers, the confirmation, the Blocks view's click, and Transfer to Schematic, which writes the chain's benches and
/// their displays through the calls <c>circuitrf chain --to-schematic</c> makes, then opens the first in the workspace
/// window that opened this one.
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
            if (Vm is { } vm)
            {
                vm.PlotsUpdated += (_, _) => { foreach (var p in Plots()) p.InvalidateVisual(); };
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ChainBudgetViewModel.SelectedStage)) ScrollStageListToSelection();
                };
            }
        };
        // A marker on a levels trace selects the stage whose output its node is, in the views and the stage list.
        foreach (var name in new[] { "LevelsPlotControl", "LevelsDbPlotControl" })
            if (this.FindControl<CircuitRF.Ui.DataDisplay.Controls.PlotControl>(name) is { } pc)
            {
                CircuitRF.Render.DataDisplay.Marker? last = null;
                pc.MarkerAdded += (m, _) => { last = m; Vm?.SelectNode(NodeOf(m)); };
                pc.MarkerMoved += (_, _) => { if (last is { } m) Vm?.SelectNode(NodeOf(m)); };
            }
        // The plots are bare PlotControls, so this window tells each which container holds its plot, as the S-Parameter
        // Viewer does: its context menu's Copy and Export render the CONTAINER, and with no provider they did nothing.
        foreach (var p in Plots())
        {
            var plot = p;
            plot.ContainerProvider = () => Vm?.PlotHost.Plots.FirstOrDefault(c => ReferenceEquals(c.PlotVM.Plot, plot.Plot));
        }
        // The Blocks view's menu acts where it was opened: the right-click is recorded on the way down, before a block's
        // own press handler takes it.
        BlocksScroller?.AddHandler(PointerPressedEvent, RecordBlocksRightClick, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        // Leaving a box, or Enter in it, puts a value the chain refuses back to its last good one.
        AddHandler(LostFocusEvent, (_, e) => { if (e.Source is TextBox) Vm?.CommitEdits(); },
                   Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Avalonia.Input.Key.Enter && e.Source is TextBox) Vm?.CommitEdits(); },
                   Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        ActualThemeVariantChanged += (_, _) => SyncPlotTheme();
        // Back to the window after simulating a bench: its levels pick up the new results.
        Activated += (_, _) => Vm?.Recompute();
        Closed += (_, _) =>
        {
            foreach (var k in OpenByPath.Where(kv => ReferenceEquals(kv.Value, this)).Select(kv => kv.Key).ToList()) OpenByPath.Remove(k);
        };
    }

    private ChainBudgetViewModel? Vm => DataContext as ChainBudgetViewModel;

    // A marker on a cube trace keeps its x, the node, in its position and a frequency of 0; one on a frequency trace
    // keeps it in its frequency.
    private static double NodeOf(CircuitRF.Render.DataDisplay.Marker m) => m.Freq != 0 ? m.Freq : m.PositionStatic.X;

    private void SyncPlotTheme()
    {
        if (Vm is null) return;
        Vm.PlotHost.Theme = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? CircuitRF.Render.DataDisplay.RenderTheme.Dark : CircuitRF.Render.DataDisplay.RenderTheme.Light;
        foreach (var p in Plots()) p.SetValue(CircuitRF.Ui.DataDisplay.Controls.PlotControl.PlotThemeProperty, Vm.PlotHost.Theme);
    }

    private IEnumerable<CircuitRF.Ui.DataDisplay.Controls.PlotControl> Plots()
    {
        foreach (var name in new[] { "CascadePlotControl", "LevelsPlotControl", "LevelsDbPlotControl", "DepthPhasePlotControl", "DepthBandPlotControl" })
            if (this.FindControl<CircuitRF.Ui.DataDisplay.Controls.PlotControl>(name) is { } p) yield return p;
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
            // Without the extension: the picker adds DefaultExtension itself (SuggestedFileNameGateTests).
            SuggestedFileName = current is not null ? Path.GetFileNameWithoutExtension(current) : "Chain",
            SuggestedStartLocation = dir is not null ? await StorageProvider.TryGetFolderFromPathAsync(dir) : null,
            DefaultExtension = ChainDesignIo.Extension.TrimStart('.'),
            ShowOverwritePrompt = true,
            FileTypeChoices = [ChainFiles],
        });
        return file?.TryGetLocalPath();
    }

    // ── Dragging a block to move its stage ───────────────────────────────────────────────────────────────────────────
    //
    // A press selects; a press that then travels more than DragThreshold is a drag. While it lasts the block is dimmed
    // and BlockDropMarker stands in the gap it would land in; the move is made on release, so the row is never rebuilt
    // under the pointer that is dragging it. Losing the capture (Escape, a window switch) cancels with nothing moved.

    private const double DragThreshold = 4;
    private ChainStageViewModel? _dragStage;
    private Control? _dragBlock;
    private Point _dragStart;
    private bool _dragging;
    private int _dropBefore = -1;

    /// <summary>A click on a block selects its stage, in the stage list and in the schematic, and may start a drag.</summary>
    private void OnBlockPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: ChainStageViewModel stage } block || Vm is not { } vm) return;
        vm.SelectedStage = stage;
        BlocksScroller?.Focus();
        if (!e.GetCurrentPoint(block).Properties.IsLeftButtonPressed || BlockItems is null) return;
        _dragStage = stage;
        _dragBlock = block;
        _dragStart = e.GetPosition(BlockItems);
        _dragging = false;
        e.Pointer.Capture(block);
    }

    private void OnBlockMoved(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (_dragStage is null || BlockItems is not { } items || Vm is not { } vm) return;
        var p = e.GetPosition(items);
        if (!_dragging)
        {
            if (Math.Abs(p.X - _dragStart.X) < DragThreshold && Math.Abs(p.Y - _dragStart.Y) < DragThreshold) return;
            _dragging = true;
            if (_dragBlock is not null) _dragBlock.Opacity = 0.5;
        }

        (_dropBefore, double markerX, double top, double height) = GapAt(p);
        if (BlockDropMarker is { } marker)
        {
            int from = vm.Stages.IndexOf(_dragStage);
            bool nowhere = _dropBefore == from || _dropBefore == from + 1;
            marker.IsVisible = !nowhere;
            marker.Height = height;
            Canvas.SetLeft(marker, markerX - marker.Width / 2);
            Canvas.SetTop(marker, top);
        }
        e.Handled = true;
    }

    private void OnBlockReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        var (stage, before, dragging) = (_dragStage, _dropBefore, _dragging);
        EndBlockDrag();
        e.Pointer.Capture(null);
        if (dragging && stage is not null && before >= 0) Vm?.MoveStage(stage, before);
    }

    /// <summary>
    /// The gap between blocks nearest <paramref name="p"/> (in <c>BlockItems</c>' coordinates): before the first block
    /// whose middle is right of it, or after the last. Where a dragged block drops and where Insert Block inserts, with
    /// the gap's x and the row's top and height for the marker.
    /// </summary>
    private (int Before, double X, double Top, double Height) GapAt(Point p)
    {
        int count = Vm?.Stages.Count ?? 0;
        double x = 0, top = 0, height = 0;
        if (BlockItems is not { } items) return (count, x, top, height);
        for (int i = 0; i < count; i++)
        {
            if (items.ContainerFromIndex(i) is not Control c || c.TranslatePoint(default, items) is not { } at) continue;
            (top, height) = (at.Y, c.Bounds.Height);
            if (p.X < at.X + c.Bounds.Width / 2) return (i, at.X, top, height);
            x = at.X + c.Bounds.Width;
        }
        return (count, x, top, height);
    }

    // ── The Blocks view's right-click menu ───────────────────────────────────────────────────────────────────────────

    /// <summary>Where the last right-click on the Blocks view landed, in <c>BlockItems</c>' coordinates, and the block
    /// under it; consumed by the next Opening, so a menu opened from the keyboard does not act on an old click.</summary>
    private Point? _menuPoint;
    private ChainStageViewModel? _menuStage;

    private void RecordBlocksRightClick(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (BlockItems is not { } items || !e.GetCurrentPoint(items).Properties.IsRightButtonPressed) return;
        _menuPoint = e.GetPosition(items);
        _menuStage = (e.Source as Avalonia.Visual)?.GetSelfAndVisualAncestors().OfType<Control>()
                         .Select(c => c.DataContext).OfType<ChainStageViewModel>().FirstOrDefault();
    }

    /// <summary>
    /// The menu, built fresh: <b>Insert Block</b> ▸ a part, into the gap nearest the right-click (the left half of a block
    /// inserts before it, the right half after it, as a dragged block would drop); <b>Copy</b>; and, under a separator,
    /// <b>Remove Block</b> on the block right-clicked, greyed when the click was not on one.
    /// </summary>
    private void OnBlocksMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not ContextMenu menu || Vm is not { } vm) { e.Cancel = true; return; }
        var (point, stage) = (_menuPoint, _menuStage);
        (_menuPoint, _menuStage) = (null, null);
        int before = point is { } p ? GapAt(p).Before
                   : vm.SelectedStage is { } sel ? vm.Stages.IndexOf(sel) + 1 : vm.Stages.Count;

        var insert = new MenuItem { Header = "Insert Block" };
        foreach (var part in ChainStageViewModel.Parts)
        {
            var item = new MenuItem { Header = part.Title };
            item.Click += (_, _) => vm.InsertStage(part, before);
            insert.Items.Add(item);
        }

        var copy = new MenuItem { Header = "Copy" };
        copy.Click += OnCopyBlocksClick;

        var remove = new MenuItem { Header = stage is null ? "Remove Block" : $"Remove Block {stage.Name}", IsEnabled = stage is not null };
        if (stage is not null) remove.Click += (_, _) => vm.Remove(stage);

        menu.ItemsSource = new List<object> { insert, copy, new Separator(), remove };
    }

    /// <summary>Delete (or Backspace) over the Blocks view removes the selected stage, as the toolbar's Remove does.</summary>
    private void OnBlocksKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key is not (Avalonia.Input.Key.Delete or Avalonia.Input.Key.Back) || e.KeyModifiers != Avalonia.Input.KeyModifiers.None) return;
        if (Vm is not { SelectedStage: not null } vm || !vm.RemoveStageCommand.CanExecute(null)) return;
        vm.RemoveStageCommand.Execute(null);
        e.Handled = true;
    }

    private void OnBlockCaptureLost(object? sender, Avalonia.Input.PointerCaptureLostEventArgs e) => EndBlockDrag();

    private void EndBlockDrag()
    {
        if (_dragBlock is not null) _dragBlock.Opacity = 1;
        if (BlockDropMarker is { } marker) marker.IsVisible = false;
        _dragStage = null;
        _dragBlock = null;
        _dragging = false;
        _dropBefore = -1;
    }

    private void OnHelpClick(object? sender, RoutedEventArgs e) => DocLauncher.Open("reference/chain-budget.html");

    /// <summary>Schematic ▸ Copy: the drawing on the clipboard, as the schematic editor copies.</summary>
    private async void OnCopySchematicClick(object? sender, RoutedEventArgs e)
    {
        if (ChainSchematic is { } canvas) await canvas.CopyToClipboardAsync();
    }

    /// <summary>The Schematic tab's Fit button: F, for the mouse.</summary>
    private void OnFitSchematicClick(object? sender, RoutedEventArgs e)
    {
        ChainSchematic?.ZoomToFit();
        ChainSchematic?.Focus();
    }

    /// <summary>Blocks ▸ Copy: the lineup as a picture on the clipboard.</summary>
    private async void OnCopyBlocksClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) await ChainBlocksGraphicExport.CopyToClipboardAsync(this, vm);
    }

    /// <summary>The stage list's one eased scroll, so a second selection cancels the first one's scroll.</summary>
    private readonly ScrollOffsetAnimator _stageScroll = new();

    /// <summary>
    /// Brings the selected stage's row to the top of the stage list with an eased scroll, when the selection came from
    /// somewhere else: a block, the schematic or a levels marker. A row picked in the list itself is where the user is
    /// already looking, so the list stays put. Posted at Background priority so the list has arranged first, as the
    /// .ctech editor posts its own.
    /// </summary>
    private void ScrollStageListToSelection()
    {
        if (StageList is not { } list || list.IsPointerOver || list.IsKeyboardFocusWithin) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (Vm is not { SelectedStage: { } stage } vm) return;
            ListBoxEasedScroll.BringToTop(list, stage, vm.Stages.IndexOf(stage), vm.Stages.Count, _stageScroll);
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// Transfer to Schematic: the chain is saved first, because the benches go beside it — into the folder the
    /// <c>.cchain</c> is in, named after it — and a display that reads results needs a workspace to find them in.
    /// </summary>
    private async void OnTransferClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.CommitEdits();   // a value still refused goes back before anything is written
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
        var existing = ChainBenches.BenchesFor(design).Select(b => ChainBenches.CellName(name, b))
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
