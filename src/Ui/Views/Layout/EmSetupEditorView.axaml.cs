using System.IO;
using System.Linq;
using Avalonia.Platform.Storage;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
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
        DetachWindowHandlers();
        if (TopLevel.GetTopLevel(this) is not Window w) return;
        _hostWindow = w;
        w.Deactivated += OnHostDeactivated;
        w.Activated   += OnHostActivated;
    }

    private void OnDetachedFromVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
        => DetachWindowHandlers();

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

    private void OnSnpOutputPathKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (e.Key == Key.Enter)  { vm.CommitSnpOutputPath(); e.Handled = true; }
        if (e.Key == Key.Escape) { vm.RevertSnpOutputPath(); e.Handled = true; }
    }

    /// <summary>
    /// Picks where the s-parameters land. The picker lives here rather than on the view model
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
            Title               = "EM output file",
            SuggestedFileName   = vm.SnpOutputPathText is { Length: > 0 } t
                                      ? Path.GetFileName(t)
                                      : vm.SnpOutputPlaceholder,
            ShowOverwritePrompt = false,   // the run writes it; this only names it
            FileTypeChoices     = [new FilePickerFileType("Touchstone") { Patterns = ["*.s*p"] }],
        });
        if (file?.TryGetLocalPath() is not { Length: > 0 } path) return;

        vm.SnpOutputPathText = vm.MakeOutputPathRef(path);
        vm.CommitSnpOutputPath();
    }

    /// <summary>
    /// Save As (owner request, 2026-08-09). The picker lives here rather than on the view model
    /// because everything under <c>src/Ui/Layout/</c> is framework-free — the VM takes a resolved
    /// path and does the I/O, exactly as the symbol editor's own Save As does.
    /// </summary>
    private async void OnSaveAsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EmSetupDocument doc) return;
        var top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider is not { } sp) return;

        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title                  = "Save EM Setup As",
            SuggestedFileName      = Path.GetFileName(doc.ViewModel.FilePath),
            DefaultExtension       = "cem",
            ShowOverwritePrompt    = true,
            FileTypeChoices        = [new FilePickerFileType("circuitRF EM Setup") { Patterns = ["*.cem"] }],
        });
        if (file?.TryGetLocalPath() is { Length: > 0 } path) doc.ViewModel.SaveAs(path);
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

    private void OnFieldLostFocus(object? sender, RoutedEventArgs e) => CommitField(sender);

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
        if (sender is not Control { DataContext: EmPortZ0Row row }) return;
        if (DataContext is not EmSetupDocument doc) return;
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
