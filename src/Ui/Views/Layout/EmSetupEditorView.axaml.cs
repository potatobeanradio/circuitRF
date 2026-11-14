using System.IO;
using System.Linq;
using Avalonia.Platform.Storage;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Views.Layout;

/// <summary>
/// Code-behind for the <c>.cem</c> EM setup editor. Every staged field commits through one generic
/// dispatcher keyed by the control's <see cref="Control.Tag"/>, mirroring
/// <see cref="TechEditorView"/> — one handler pair rather than one per field.
/// </summary>
public partial class EmSetupEditorView : UserControl
{
    public EmSetupEditorView()
    {
        InitializeComponent();
        EditTechButton.Click     += OnEditTechnologyClick;
        SaveAsButton.Click       += OnSaveAsClick;
        ChangeLayoutButton.Click += OnChangeLayoutClick;

        // Staged, exactly like every other typed field here and like the Analyses panel's own
        // "Results file:" box: commit on LostFocus/Enter, Escape reverts. A half-typed path must
        // never reach the model.
        HelpButton.Click += (_, _) => DocLauncher.Open("reference/em-setup.html");

        BrowseSnpOutputButton.Click += OnBrowseSnpOutputClick;
        SnpOutputPathBox.LostFocus  += (_, _) => Vm?.CommitSnpOutputPath();
        SnpOutputPathBox.KeyDown    += OnSnpOutputPathKeyDown;

        AttachedToVisualTree   += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    // ── Scroll position survives the window regaining focus ────────────────────────────────────
    //
    // Owner report, 2026-08-11: "whenever an EM Setup document gets focus, it scrolls to the top —
    // only when it's docked, not as a torn-away document, and not when switching document tabs. It
    // appears only when the workspace window gets focus."
    //
    // Activating a window makes the focus manager re-focus something inside it, and focusing a
    // control raises RequestBringIntoView, which the enclosing ScrollViewer honours by scrolling to
    // it. This panel is one long scrolling column whose first focusable control is the analysis combo
    // at the very top, so "restore focus" and "scroll to the top" are the same gesture. It does not
    // happen on a tab switch because the view is re-attached and re-laid-out there rather than
    // re-focused into an existing scroll position.
    //
    // The fix is to capture the offset when the window loses focus and put it back when it regains
    // it. That is deliberately narrower than cancelling BringIntoView outright: a genuine
    // bring-into-view (tabbing to a field further down) must still work, and while the window is
    // deactivated the user cannot scroll, so there is nothing a restore could overwrite.
    private Window? _hostWindow;
    private Avalonia.Vector _savedScroll;

    private void OnAttachedToVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        // brief-emi-6 R-emi6-2 — which solvers are installed is read when the panel opens (and when Settings ▸ Solvers
        // closes); never polled.
        Em3dSolverInstallState.Changed -= OnSolverInstallChanged;
        Em3dSolverInstallState.Changed += OnSolverInstallChanged;
        Vm?.UpdateSolverAvailability();
        _ = Em3dSolverInstallState.RefreshAsync();
        // brief-dfr31-1 R-dfr31-1-4 — the pickers' rows, built once off the UI thread when the panel opens.
        if (Vm is { } setupVm)
        {
            setupVm.IsShown = true;
            _ = setupVm.ListBodiesAsync();
        }
        DetachWindowHandlers();
        if (TopLevel.GetTopLevel(this) is not Window w) return;
        _hostWindow = w;
        w.Deactivated += OnHostDeactivated;
        w.Activated   += OnHostActivated;
    }

    private void OnDetachedFromVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        Em3dSolverInstallState.Changed -= OnSolverInstallChanged;
        DetachWindowHandlers();
        // A Pick left armed in a closed window took the next object selected in the 3D view, long after, unasked.
        if (Vm is { } setupVm)
        {
            setupVm.IsShown = false;
            setupVm.PickingRow = null;
        }
    }

    private void OnSolverInstallChanged() => Vm?.UpdateSolverAvailability();

    private void DetachWindowHandlers()
    {
        if (_hostWindow is null) return;
        _hostWindow.Deactivated -= OnHostDeactivated;
        _hostWindow.Activated   -= OnHostActivated;
        _hostWindow = null;
    }

    private void OnHostDeactivated(object? sender, EventArgs e) => _savedScroll = BodyScroll.Offset;

    private void OnHostActivated(object? sender, EventArgs e)
    {
        if (_savedScroll.Y <= 0) return;
        var wanted = _savedScroll;
        // Background priority: the focus restoration (and the BringIntoView it provokes) has to have
        // run before this, or it would simply scroll back over the top of us.
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => { if (_hostWindow is not null) BodyScroll.Offset = wanted; },
            Avalonia.Threading.DispatcherPriority.Background);
    }

    private EmSetupEditorViewModel? Vm =>
        DataContext is EmSetupDocument d ? d.ViewModel : null;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Vm is { } vm) vm.CommitFocusedField = CommitFocusedField;
    }

    /// <summary>Moves the focus off the field being typed in and back, so the field's own LostFocus commits it.</summary>
    private void CommitFocusedField()
    {
        if (TopLevel.GetTopLevel(this)?.FocusManager is not { } focus) return;
        if (focus.GetFocusedElement() is not Control field || !this.IsVisualAncestorOf(field)) return;
        focus.Focus(null);
        field.Focus();
    }

    private void OnSnpOutputPathKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (e.Key == Key.Enter)  { vm.CommitSnpOutputPath(); e.Handled = true; }
        if (e.Key == Key.Escape) { vm.RevertSnpOutputPath(); e.Handled = true; }
    }

    /// <summary>
    /// Picks the result's name and folder. The picker lives here rather than on the view model
    /// because everything under <c>src/Ui/Layout/</c> is framework-free.
    ///
    /// <para>The chosen path is stored RELATIVE to the workspace's results folder when it sits
    /// inside it, and absolute otherwise — the same rule <c>ResolveSnpBasePath</c> already reads it
    /// by, so a setup that stays inside the workspace survives that workspace being moved.</para>
    /// </summary>
    private async void OnBrowseSnpOutputClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider is not { } sp) return;

        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title               = "EM output name",
            SuggestedFileName   = vm.SnpOutputPathText is { Length: > 0 } t
                                      ? Path.GetFileName(t)
                                      : vm.SnpOutputPlaceholder,
            ShowOverwritePrompt = false,   // the run writes it; this only names it
            FileTypeChoices     = [new FilePickerFileType("EM result") { Patterns = ["*" + EmResultNames.RecordExtension] }],
        });
        if (file?.TryGetLocalPath() is not { Length: > 0 } path) return;

        vm.SnpOutputPathText = vm.MakeOutputPathRef(path);
        vm.CommitSnpOutputPath();
    }

    /// <summary>
    /// Save As (owner request, 2026-08-09). The picker lives on the WORKSPACE rather than here or on
    /// the view model: everything under <c>src/Ui/Layout/</c> is framework-free, so the VM takes a
    /// resolved path and does the I/O, and the document TAB's own Save As… (2026-09-09) needs the
    /// same picker from a place that has no view — one route, called from both buttons.
    /// </summary>
    private async void OnSaveAsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EmSetupDocument doc) return;
        if (WorkspaceLocator.For(this) is not { } workspace) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        await workspace.SaveEmSetupAs(doc, owner);
    }

    /// <summary>
    /// Change which <c>.clay</c> this setup analyses. Writing the reference back in the form the
    /// workspace stores it is the VM's <c>MakeLayoutRef</c> seam, not this handler's — the base
    /// directory is the workspace's own business.
    /// </summary>
    private async void OnChangeLayoutClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EmSetupDocument doc) return;
        var top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider is not { } sp) return;

        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Choose the layout this EM setup analyses",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("circuitRF Layout") { Patterns = ["*.clay"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { Length: > 0 } path)
            doc.ViewModel.SetLayoutRef(path);
    }

    /// <summary>brief-em3d-79 R-em3d79-4 — Currents ▸ From circuit…: the schematic (or netlist) whose HB drives the ports.</summary>
    private async void OnBrowseCircuitClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EmSetupDocument doc) return;
        var top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider is not { } sp) return;
        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Choose the circuit whose harmonic balance drives the ports",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Schematic or netlist") { Patterns = ["*.csch", "*.cnl"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { Length: > 0 } path)
            doc.ViewModel.SetCircuitSchematic(path);
    }

    private void OnFieldLostFocus(object? sender, RoutedEventArgs e) => CommitField(sender);

    // brief-icp-7 R-icp7-2 — a cut instance's Z0 and keep-out box commit on Enter or lost focus, through the row.
    private void OnInstanceZ0Commit(object? sender, RoutedEventArgs e) => ((sender as Control)?.DataContext as EmComponentInstanceRow)?.CommitZ0();

    private void OnInstanceZ0Key(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return)) return;
        ((sender as Control)?.DataContext as EmComponentInstanceRow)?.CommitZ0();
        e.Handled = true;
    }

    private void OnInstanceKeepOutCommit(object? sender, RoutedEventArgs e) => ((sender as Control)?.DataContext as EmComponentInstanceRow)?.CommitKeepOut();

    private void OnInstanceKeepOutKey(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return)) return;
        ((sender as Control)?.DataContext as EmComponentInstanceRow)?.CommitKeepOut();
        e.Handled = true;
    }

    // brief-em3d-75 R-em3d75-3 — the thermal page: text commits on Enter or lost focus, a picker when it changes.
    private void OnThermalLostFocus(object? sender, RoutedEventArgs e) => Vm?.CommitThermal();

    private void OnThermalKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Vm?.CommitThermal();
        e.Handled = true;
    }

    private void OnThermalSelection(object? sender, SelectionChangedEventArgs e) => Vm?.CommitThermal();

    // ── brief-dfr31-1 R-dfr31-1-4 — the terminal and ground pickers ────────────────────────────────────────────────────

    /// <summary>Type-to-filter for both pickers: a row whose label or name holds the typed text.</summary>
    public static AutoCompleteFilterPredicate<object?> PickFilter { get; } = EmSetupEditorViewModel.PickMatches;

    /// <summary>▾ — the whole list, whatever is typed: the box's own filter shows only matches.</summary>
    private void OnOpenPickerClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Parent is not Panel p || p.Children.OfType<AutoCompleteBox>().FirstOrDefault() is not { } box) return;
        OpenPicker(box);
    }

    /// <summary>Opens <paramref name="box"/>'s list unfiltered.</summary>
    internal static void OpenPicker(AutoCompleteBox box)
    {
        box.Focus();
        var filter = box.ItemFilter;
        box.ItemFilter = (_, _) => true;
        box.PopulateComplete();
        box.IsDropDownOpen = true;
        box.ItemFilter = filter;
    }

    // brief-dfr34-4 R-dfr34-4-2 — a picker commits on Enter or a click in its list, never on a selection change: the box's
    // selection follows the arrow keys through the list, and each of those had written the setup. A selection is held until
    // the list closes; it closed on a choice when the box still has the focus and reads the chosen row's name (Escape puts
    // back what was typed, and a click elsewhere takes the focus first). Any other close puts back the text the list opened
    // on, so arrowing through the list and clicking away changes nothing.
    private readonly Dictionary<AutoCompleteBox, (string? Before, Em3dBodyPick? Pending)> _pickers = [];

    private void OnPickerOpened(object? sender, EventArgs e)
    {
        if (sender is AutoCompleteBox box) _pickers[box] = (box.Text, null);
    }

    private void OnBodyPicked(object? sender, SelectionChangedEventArgs e) => HoldPick(sender);

    private void OnGroundPicked(object? sender, SelectionChangedEventArgs e) => HoldPick(sender);

    private void HoldPick(object? sender)
    {
        if (sender is not AutoCompleteBox { SelectedItem: Em3dBodyPick pick } box) return;
        _pickers[box] = (_pickers.TryGetValue(box, out var held) ? held.Before : box.Text, pick);
    }

    private void OnPickerClosed(object? sender, EventArgs e)
    {
        if (sender is not AutoCompleteBox box || !_pickers.Remove(box, out var held) || Vm is not { } vm) return;
        bool chosen = held.Pending is { } p && box.IsKeyboardFocusWithin && box.Text == p.Token;
        if (!chosen)
        {
            if (held.Pending is { } navigated && box.Text == navigated.Token && held.Before is { } before) box.Text = before;
            return;
        }
        var pick = held.Pending!;
        // A commit rebuilds the terminal rows (the snapshot is re-applied), so it runs after the box's own events have
        // finished: committing inside them tore the box's list down mid-event.
        if (box.DataContext is Em3dTerminalRow row)
        {
            row.Net = pick.Token;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                vm.CommitTerminals();
                vm.HighlightRow(vm.TerminalRows.FirstOrDefault(r => r.Name == row.Name) ?? row);
            });
        }
        else Avalonia.Threading.Dispatcher.UIThread.Post(() => vm.PickGround(pick));
    }

    private void OnTerminalRowFocus(object? sender, FocusChangedEventArgs e)
    {
        if ((sender as Control)?.DataContext is Em3dTerminalRow row) Vm?.HighlightRow(row);
    }

    private void OnPickTerminalClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: Em3dTerminalRow row } button || Vm is not { } vm) return;
        vm.PickTerminalCommand.Execute(row);
        button.IsChecked = row.IsPicking;
    }

    private void OnPickGroundClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button || Vm is not { } vm) return;
        vm.PickTerminalCommand.Execute(vm.GroundPickRow);
        button.IsChecked = vm.GroundPickRow.IsPicking;
    }

    private void OnFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            CommitField(sender);
            e.Handled = true;
        }
    }

    private void CommitField(object? sender)
    {
        if (sender is not Control c || c.Tag is not string tag) return;
        if (DataContext is not EmSetupDocument doc) return;
        var vm = doc.ViewModel;

        switch (tag)
        {
            case "Port1Z0": vm.CommitPortZ0(1); break;
            case "Port2Z0": vm.CommitPortZ0(2); break;
            case "PatternFrequency": vm.CommitPatternFrequency(); break;
            case "MeshFrequency3D": vm.CommitMeshFrequency3D(); break;
            case { } palace when palace.StartsWith("Palace.", StringComparison.Ordinal):
                vm.CommitPalaceField(palace);
                break;
            case "Terminals3D": vm.CommitTerminals(); break;
            case "Ports3D":     vm.CommitPorts3D(); break;
            case "Port3DZ0" when c.DataContext is Em3dPortRow row: vm.CommitPort3DZ0(row); break;
            case "Eigenmode":   vm.CommitEigenmode(); break;
            case { } openEms when openEms.StartsWith("OpenEms.", StringComparison.Ordinal):
                vm.CommitOpenEmsField(openEms);
                break;
            case { } emerge when emerge.StartsWith("Emerge.", StringComparison.Ordinal):
                vm.CommitEmergeField(emerge);
                break;
            case { } sar when sar.StartsWith("Sar.", StringComparison.Ordinal):       // brief-sar-6
                vm.CommitSarField(sar);
                break;
            default:        vm.CommitMeshField(tag); break;
        }
    }

    // R-cpl-6 — the per-port list. Its rows are their own DataContext (EmPortZ0Row), so they need
    // their own handler pair rather than the Tag-keyed CommitField dispatcher above: the index the
    // VM commits is the row's position, which only the collection knows.

    private void OnPortRowLostFocus(object? sender, RoutedEventArgs e) => CommitPortRow(sender);

    private void OnPortRowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            CommitPortRow(sender);
            e.Handled = true;
        }
    }

    private void CommitPortRow(object? sender)
    {
        if (sender is not Control { DataContext: EmPortZ0Row row } box) return;
        if (DataContext is not EmSetupDocument doc) return;
        // brief-lcp-3 — a component port row's width box writes the part's entry.
        if (box.Tag is "ComponentWidth") { doc.ViewModel.CommitComponentPortWidth(row); return; }
        int index = doc.ViewModel.PortRows.IndexOf(row);
        if (index >= 0) doc.ViewModel.CommitPortRow(index);
    }

    /// <summary>
    /// R-em-12 — the stackup is shown here, edited in the <c>.ctech</c>. Reaches the workspace the
    /// same way the Layout Editor's own Technology ▾ ▸ Edit does (this view's DataContext is an
    /// <see cref="EmSetupDocument"/>, not the workspace), rather than duplicating a stackup editor.
    /// </summary>
    private void OnEditTechnologyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EmSetupDocument doc) return;
        if (doc.ViewModel.ResolveLayout?.Invoke(doc.ViewModel.Working.LayoutRef) is not { } source) return;
        if (source.Technology is null) return;

        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime desktop) return;

        var ws = desktop.Windows
            .Select(w => w.DataContext)
            .OfType<WorkspaceViewModel>()
            .FirstOrDefault();
        if (ws?.ResolvedTechPathFor(source.AbsolutePath) is { } techPath)
            ws.OpenTechnologyDocument(techPath);
    }
}
