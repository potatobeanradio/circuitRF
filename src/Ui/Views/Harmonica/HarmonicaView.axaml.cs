using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Views.Harmonica;

public partial class HarmonicaView : UserControl
{
    private HarmonicaDocument? _doc;
    private bool _solvedOnce;

    public HarmonicaView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += (_, _) => Refresh();
        // §7.1's layout is in FRACTIONS, so the readout strip has to be re-placed whenever the
        // panel area resizes — one layout source (CharmLayout), two consumers (canvas + strip).
        PanelHost.SizeChanged += (_, _) => PlaceReadoutStrip();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_doc is not null)
        {
            _doc.ActivationFocusRequested -= OnActivationFocusRequested;
            _doc.ViewModel.Harmonica.RedrawRequested -= OnRedraw;
            _doc.ViewModel.Harmonica.Pool.Completed -= OnFrameCompleted;
            _doc.ViewModel.Harmonica.Pool.Failed    -= OnFrameFailed;
            _doc.ViewModel.Harmonica.EditDisplay.UnlockedChanged -= OnRedraw;
            _doc.ViewModel.NativeMenuDockedFocusChanged = null;
            (Menus.DataContext as HarmonicaMenuViewModel)?.Detach();
            // R-h9a-8 — ApplyVariant() used to run only here, at attach time, so an OS light/dark
            // switch never reached an already-open document. Unsubscribe on detach so a closed/
            // torn-down document's handler doesn't outlive it.
            if (Application.Current is { } appDetach)
                appDetach.ActualThemeVariantChanged -= OnActualThemeVariantChanged;

            // R-h9a-9 — subscribed at attach, below. Unsubscribed here for the same reason as
            // ActualThemeVariantChanged: ThemeService.ThemeChanged is a static, process-wide event,
            // and HarmonicaViewModel has no IDisposable/teardown of its own to unsubscribe from it —
            // the VIEW owns this subscription's lifetime, exactly like SchematicCanvas already does
            // for the same event.
            ThemeService.ThemeChanged -= OnThemeServiceChanged;
        }

        _doc = DataContext as HarmonicaDocument;
        if (_doc is null) return;

        _doc.ActivationFocusRequested += OnActivationFocusRequested;
        _doc.ViewModel.Harmonica.RedrawRequested += OnRedraw;

        // R-h9a-3's action seam — WorkspaceViewModel's dock-level focus tracking drives this without
        // needing to know Menus is a HarmonicaMenuView, or that NativeMenu exists at all.
        _doc.ViewModel.NativeMenuDockedFocusChanged = Menus.SetDockedFocus;

        // R-h45-8 — the pool completes on a worker thread; publishing is the ONE thing that must
        // happen on the UI thread, so it is marshalled here rather than inside the view model.
        _doc.ViewModel.Harmonica.Pool.Completed += OnFrameCompleted;
        _doc.ViewModel.Harmonica.Pool.Failed    += OnFrameFailed;

        Canvas.ViewModel = _doc.ViewModel.Harmonica;

        // §7.6's menus are the document's own; the hooks the workspace can serve are wired here so
        // harmonicaRF keeps working with none of them (§1.2 — it opens with no workspace at all).
        var menus = new HarmonicaMenuViewModel(_doc.ViewModel.Harmonica);
        WireMenuHooks(menus);
        Menus.DataContext = menus;
        _doc.ViewModel.Harmonica.EditDisplay.UnlockedChanged += OnRedraw;

        // The view binds AFTER the activation request in the first-open case — see src/Ui/CLAUDE.md's
        // own note on IActivatableDocument — so consume any pending request here too.
        if (_doc.ConsumeActivationFocus()) FocusCanvas();

        // R-h9a-8 — subscribe to the SAME global event App itself already listens to for
        // UpdateCrfWarningBrush, so a later OS light/dark switch re-applies to this document too,
        // not just whichever document happened to be open at attach time.
        if (Application.Current is { } appAttach)
            appAttach.ActualThemeVariantChanged += OnActualThemeVariantChanged;

        // R-h9a-9 — the same seam SchematicCanvas already uses for a Settings-dialog colour edit:
        // ThemeService.ThemeChanged fires when ThemeService.Active is REPLACED (never on a mutation
        // of the same instance), and HarmonicaViewModel.RenderTheme reads ThemeService.Active fresh on
        // every access, so re-rendering is the whole of what this has to do.
        ThemeService.ThemeChanged += OnThemeServiceChanged;

        ApplyVariant();
        Refresh();
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() => { ApplyVariant(); Refresh(); }, DispatcherPriority.Background);

    private void OnThemeServiceChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(Refresh, DispatcherPriority.Background);

    private void OnActivationFocusRequested() => FocusCanvas();

    private void FocusCanvas()
        => Dispatcher.UIThread.Post(() => Canvas.Focus(), DispatcherPriority.Background);

    private void OnRedraw() => Dispatcher.UIThread.Post(Refresh, DispatcherPriority.Background);

    private void OnFrameCompleted(HarmonicaFrame frame, long seq) => Dispatcher.UIThread.Post(() =>
    {
        _doc?.ViewModel.Harmonica.PublishFrame(frame);
        Refresh();
    }, DispatcherPriority.Background);

    private void OnFrameFailed(Exception ex, long seq) => Dispatcher.UIThread.Post(() =>
    {
        _doc?.ViewModel.Harmonica.PublishFailure(ex);
        Refresh();
    }, DispatcherPriority.Background);

    /// <summary>The variant follows <c>ActualThemeVariant</c>, exactly as the schematic canvas already
    /// does (§7.9.3's closing sentence).</summary>
    private void ApplyVariant()
    {
        if (_doc is null) return;
        _doc.ViewModel.Harmonica.Variant =
            Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light
                ? ColorVariant.Light : ColorVariant.Dark;
    }

    /// <summary>
    /// The first frame is solved LAZILY, on first attach, and at the coarse ring set — §6.8's tier B
    /// default. A new document that spent a second solving a full grid before it drew anything would
    /// contradict §1's whole claim about liveness.
    /// </summary>
    private void EnsureFirstSolve()
    {
        if (_solvedOnce || _doc is null) return;
        _solvedOnce = true;
        _doc.ViewModel.Harmonica.RequestFrame();
    }

    private void Refresh()
    {
        if (_doc is null) return;
        EnsureFirstSolve();

        var h = _doc.ViewModel.Harmonica;

        PlaneLabel.Text = h.IntrinsicPlane ? "intrinsic" : "extrinsic";
        XUnitLabel.Text = h.PowerSweepXUnit.Label();

        // R-h6-5 — FrameScheduler.StatusMessage reaches the strip, ALWAYS. D4's whole point is that a
        // model which cannot hold the target is TOLD about, never silently stuttered at, and until
        // this line existed the message was computed and displayed nowhere.
        StatusText.Text = h.StatusMessage is { Length: > 0 } msg
            ? msg
            : $"{h.LastSolveCount} HB solves · {h.Frame.SmithPower.GridPoints.Count} Γ points · " +
              $"{h.Frame.SmithPower.GridPoints.Count(p => p.IsHole)} holes · {h.Frame.Quality}";

        CursorModeLabel.Text = h.SnapCursorToCompression
            ? $"cursor: compression ({h.OperatingPointDbm:0.#} dBm)"
            : $"cursor: {h.CursorPinDbm:0.#} dBm";

        EditDisplayToggle.IsChecked = h.EditDisplay.Unlocked;
        EditDisplayLabel.Text = h.EditDisplay.Unlocked ? "editing layout" : "edit display";

        var brush = ReadoutStripView.BrushFor(h.RenderTheme);
        Readouts.SetInputs(h.Inputs, brush, (key, text) => h.ApplyInput(key, text));
        Readouts.SetInputError(h.InputError, brush);
        Readouts.SetItems(h.Frame.Readouts, brush);
        PlaceReadoutStrip();
        Canvas.InvalidateVisual();
    }

    private void OnToggleEditDisplay(object? sender, RoutedEventArgs e)
    {
        if (_doc is null) return;
        var ed = _doc.ViewModel.Harmonica.EditDisplay;
        ed.Unlocked = !ed.Unlocked;
        Refresh();
    }

    // ── §7.6's document-scoped hooks ─────────────────────────────────────────

    /// <summary>
    /// Wires the menu's host hooks. Each is optional by design — harmonicaRF opens with no workspace
    /// (§1.2) and ships standalone (§3.1), so anything that cannot be served here simply stays
    /// unwired rather than being faked.
    /// </summary>
    private void WireMenuHooks(HarmonicaMenuViewModel menus)
    {
        // §7.6's File menu. There is no workspace command for a `.charm` — H4–H6 built
        // HarmonicaDocument.OnSavedToPath and CharmIo but never a File route to them — so the
        // document serves its own open/save here. It needs no workspace to do it (§1.2).
        menus.OpenDocumentHook    = () => RunHook(OpenCharmAsync);
        menus.SaveDocumentHook    = () => RunHook(() => SaveCharmAsync(saveAs: false));
        menus.SaveDocumentAsHook  = () => RunHook(() => SaveCharmAsync(saveAs: true));
        menus.NewDocumentHook     = NewDocument;
        menus.CloseDocumentHook   = CloseDocument;

        menus.ImportGamHook       = () => RunHook(ImportGamAsync);
        menus.ExportGamHook       = () => RunHook(ExportGamAsync);
        menus.ExportTestbenchHook = () => RunHook(ExportTestbenchAsync);
        menus.CopyTerminationsHook= () => RunHook(CopyTerminationsAsync);
        menus.CopyReadoutsHook    = () => RunHook(CopyReadoutsAsync);
        menus.PreferencesHook     = () => RunHook(ShowPreferencesAsync);
        menus.AddTraceHook        = () => RunHook(ShowTracePickerAsync);

        // H8 — the four H7 left deliberately null. An unwired hook is honest where a faked
        // implementation is not; this phase is what pays the debt.
        menus.SetDutHook          = () => RunHook(ShowSetDutAsync);
        menus.ExportDataHook      = () => RunHook(ExportDataAsync);
        menus.CopyPlotHook        = () => RunHook(CopyPlotAsync);
        menus.HelpHook            = ShowHelp;
    }

    /// <summary>
    /// R-h9a-13 — the ONE place a menu hook's own exception is actually observed. An `async Task`
    /// method's compiler-generated state machine ALWAYS captures a thrown exception into the returned
    /// Task rather than letting it escape synchronously — regardless of whether the throw happens
    /// before or after the method's first `await` — so a discarded/unobserved Task
    /// (`() => _ = SomeAsyncMethod();`, what every hook above used to be) loses that exception
    /// permanently. Routing every hook through this instead means the next "menu item does nothing"
    /// report arrives with the exception's own message in the readout strip, not silence.
    /// </summary>
    private async void RunHook(Func<System.Threading.Tasks.Task> op)
    {
        try { await op(); }
        catch (Exception ex)
        {
            if (Vm is { } h) { h.SolveError = ex.Message; Refresh(); }
        }
    }

    /// <summary>§4.3's <i>Set DUT…</i>. The dialog produces a <c>DutSpec</c>; applying it is
    /// <c>ApplyDut</c>'s job, which is H7's own structural write-back rather than a second one.</summary>
    private async System.Threading.Tasks.Task ShowSetDutAsync()
    {
        if (Vm is not { } h || TopLevel.GetTopLevel(this) is not Window owner) return;

        // The folder-based resolver is installed HERE rather than at startup, because installing it
        // is what makes a kit reachable and this is the first moment anything asks. It starts nothing.
        HarmonicaDutCatalog.RegisterKitResolver();

        var chosen = await Dialogs.HarmonicaSetDutDialog.ShowAsync(owner, h.Model.Dut);
        if (chosen is null) return;

        h.ApplyDut(chosen);
        Refresh();
    }

    private async System.Threading.Tasks.Task ShowTracePickerAsync()
    {
        if (Vm is not { } h || TopLevel.GetTopLevel(this) is not Window owner) return;
        await new Dialogs.HarmonicaTracePickerDialog(h).ShowDialog<bool>(owner);
        Refresh();
    }

    private HarmonicaViewModel? Vm => _doc?.ViewModel.Harmonica;

    /// <summary>The workspace this view is hosted in, or null when harmonicaRF is standalone (§3.1).
    /// Resolved per call from the hosting window rather than held — a torn-off document window has a
    /// different one, which is R-menu-4's own per-window rule.</summary>
    private ViewModels.WorkspaceViewModel? Workspace
        => (TopLevel.GetTopLevel(this) as Window)?.DataContext as ViewModels.WorkspaceViewModel;

    private void NewDocument()
    {
        if (Workspace?.NewHarmonicaCommand is { } cmd && cmd.CanExecute(null)) cmd.Execute(null);
    }

    private void CloseDocument()
    {
        if (_doc is null) return;
        // Dock owns document lifetime; asking the factory to close it is what the tab's own × does.
        Workspace?.DockFactory.CloseDockable(_doc);
    }

    private async System.Threading.Tasks.Task OpenCharmAsync()
    {
        if (_doc is null || TopLevel.GetTopLevel(this) is not { } top) return;

        var picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open harmonicaRF document",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("harmonicaRF document") { Patterns = ["*.charm"] }],
        });
        if (picked.Count == 0) return;
        LoadCharmFile(picked[0].Path.LocalPath);
    }

    /// <summary>
    /// Loads a <c>.charm</c> into this view's document. Public because the standalone shell's
    /// double-click route needs it (M3) — and it is the SAME route File ▸ Open takes, so a file
    /// opened by double-click gets §8.1's unresolved-reference report exactly like one opened from
    /// the menu.
    /// </summary>
    public void LoadCharmFile(string path)
    {
        if (_doc is null) return;
        var h = _doc.ViewModel.Harmonica;
        try
        {
            var unresolved = h.LoadCharm(System.IO.File.ReadAllText(path),
                                         System.IO.Path.GetDirectoryName(path));

            // §8.1 — an unresolved reference must SAY which file is missing rather than fail silently
            // or substitute another model. The document still opens, so the reference can be repointed.
            h.SolveError = unresolved.Count == 0
                ? null
                : string.Join("  ", unresolved.Select(u => u.Message));

            _doc.OnSavedToPath(path);
            h.ResetSchedule();
            h.RequestScheduledFrame(dragging: false);
        }
        catch (Exception ex) { h.SolveError = ex.Message; }
        Refresh();
    }

    private async System.Threading.Tasks.Task SaveCharmAsync(bool saveAs)
    {
        if (_doc is null || Vm is not { } h) return;

        string? path = saveAs ? null : _doc.FilePath;
        if (path is null)
        {
            if (TopLevel.GetTopLevel(this) is not { } top) return;
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title             = "Save harmonicaRF document",
                DefaultExtension  = "charm",
                SuggestedFileName = (_doc.FilePath is { } p
                    ? System.IO.Path.GetFileName(p) : "harmonica.charm"),
            });
            if (file is null) return;
            path = file.Path.LocalPath;
        }

        try
        {
            await System.IO.File.WriteAllTextAsync(path, h.ToCharmJson());
            _doc.OnSavedToPath(path);

            // Open item 6 — a .charm saved into an open workspace appears in the tree with no reload.
            // Null standalone (§1.2), where there is no tree to refresh and nothing to register.
            Workspace?.NotifyHarmonicaSaved(_doc, path);
        }
        catch (Exception ex) { h.SolveError = ex.Message; }
        Refresh();
    }

    private async System.Threading.Tasks.Task ImportGamAsync()
    {
        if (Vm is not { } h || TopLevel.GetTopLevel(this) is not { } top) return;

        var picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Γ grid (.gam)",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Termination grid") { Patterns = ["*.gam"] }],
        });
        if (picked.Count == 0) return;

        try
        {
            string text = await System.IO.File.ReadAllTextAsync(picked[0].Path.LocalPath);
            var points = HarmonicaInterchange.ImportGam(text, h.Model.Settings.FrequencyHz,
                                                        out var notes);
            if (points.Count == 0)
            {
                h.SolveError = $"'{picked[0].Name}' carries no usable Γ points.";
                Refresh();
                return;
            }

            h.SetGammaGrid(points);
            h.SolveError = notes.Count > 0 ? string.Join(" ", notes) : null;
            h.RequestScheduledFrame(dragging: false);
        }
        catch (Exception ex) { h.SolveError = ex.Message; }
        Refresh();
    }

    private async System.Threading.Tasks.Task ExportGamAsync()
    {
        if (Vm is not { } h || TopLevel.GetTopLevel(this) is not { } top) return;

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Γ grid (.gam)",
            DefaultExtension = "gam",
            SuggestedFileName = "harmonica-grid.gam",
        });
        if (file is null) return;

        try
        {
            string text = HarmonicaInterchange.ExportGam(h.Frame.SmithPower.GridPoints,
                                                         freqHz: h.Model.Settings.FrequencyHz);
            await System.IO.File.WriteAllTextAsync(file.Path.LocalPath, text);
        }
        catch (Exception ex) { h.SolveError = ex.Message; Refresh(); }
    }

    private async System.Threading.Tasks.Task ExportTestbenchAsync()
    {
        if (Vm is not { } h || TopLevel.GetTopLevel(this) is not { } top) return;

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export testbench (.cnl)",
            DefaultExtension = "cnl",
            SuggestedFileName = "harmonica-testbench.cnl",
        });
        if (file is null) return;

        try
        {
            string text = HarmonicaInterchange.ExportTestbench(
                h.Model, h.Terminations, h.OperatingPointDbm);
            await System.IO.File.WriteAllTextAsync(file.Path.LocalPath, text);
        }
        catch (Exception ex) { h.SolveError = ex.Message; Refresh(); }
    }

    private async System.Threading.Tasks.Task CopyTerminationsAsync()
    {
        if (Vm is not { } h || TopLevel.GetTopLevel(this)?.Clipboard is not { } clip) return;
        await clip.SetTextAsync(HarmonicaInterchange.CopyTerminationSet(h.Terminations, h.Model.Bias));
    }

    /// <summary>
    /// §7.6's <i>Export Data</i>. R-h8-11 — it writes the frame's OWN published <c>DataSet</c>
    /// (R-h7-6's, the one the panels drew from), never a re-solve: a file that disagreed with what is
    /// on screen would be the worst possible export.
    /// </summary>
    private async System.Threading.Tasks.Task ExportDataAsync()
    {
        if (Vm is not { } h || TopLevel.GetTopLevel(this) is not { } top) return;

        if (h.Frame.Published is not { } ds)
        {
            h.SolveError = "There is no solved frame to export yet.";
            Refresh();
            return;
        }

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title            = "Export data",
            DefaultExtension = "npy",
            SuggestedFileName = (_doc?.FilePath is { } p
                ? System.IO.Path.GetFileNameWithoutExtension(p) : "harmonica") + ".npy",
            FileTypeChoices =
            [
                new FilePickerFileType("NumPy (.npy)")     { Patterns = ["*.npy"] },
                new FilePickerFileType("MATLAB (.mat)")    { Patterns = ["*.mat"] },
                new FilePickerFileType("Tab-delimited text") { Patterns = ["*.txt"] },
            ],
        });
        if (file is null) return;

        string path = file.Path.LocalPath;
        try
        {
            // The format follows the extension the user chose. Same three DataSetExporter already
            // writes for the Data Display, through the same call — the picker is the only new part.
            var format = System.IO.Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".mat" => RfCore.Export.ExportFormat.Mat,
                ".txt" => RfCore.Export.ExportFormat.Tsv,
                _      => RfCore.Export.ExportFormat.Npy,
            };
            RfCore.Export.DataSetExporter.Export(ds, path, format);
            h.SolveError = null;
        }
        catch (Exception ex) { h.SolveError = ex.Message; }
        Refresh();
    }

    /// <summary>
    /// §7.6's <i>Copy Plot</i>. Copies the panel under the pointer, falling back to the whole canvas
    /// — see <see cref="HarmonicaClipboard"/> for why that choice and not the other one.
    /// </summary>
    private async System.Threading.Tasks.Task CopyPlotAsync()
    {
        if (Vm is not { } h) return;
        try
        {
            string what = await HarmonicaClipboard.CopyAsync(Canvas, h, Canvas.PanelUnderPointer());
            h.SolveError = $"Copied {what} to the clipboard.";
        }
        catch (Exception ex) { h.SolveError = ex.Message; }
        Refresh();
    }

    /// <summary>§7.6's <i>Help</i>. The bundled User Documentation, opened the same way every other
    /// Help button in the application opens it.</summary>
    private void ShowHelp() => DocLauncher.Open("index.html");

    private async System.Threading.Tasks.Task CopyReadoutsAsync()
    {
        if (Vm is not { } h || TopLevel.GetTopLevel(this)?.Clipboard is not { } clip) return;
        var sb = new System.Text.StringBuilder();
        foreach (var (label, value, _) in h.Frame.Readouts)
            sb.Append(label).Append('\t').AppendLine(value);
        await clip.SetTextAsync(sb.ToString());
    }

    private async System.Threading.Tasks.Task ShowPreferencesAsync()
    {
        if (_doc is null || TopLevel.GetTopLevel(this) is not Window owner) return;
        await new Dialogs.HarmonicaPreferencesDialog(_doc.ViewModel.Harmonica).ShowDialog(owner);
        Refresh();
    }

    /// <summary>Positions the strip at the placement <see cref="CharmLayout"/> gives it — the SAME
    /// source the canvas reads, so the strip can never end up somewhere the canvas did not leave
    /// room for.</summary>
    private void PlaceReadoutStrip()
    {
        if (_doc is null || PanelHost.Bounds.Width <= 0) return;

        var p = _doc.ViewModel.Harmonica.Layout.PlacementOf(HarmonicaPanelId.ReadoutStrip);
        ReadoutHost.Margin = new Thickness(p.X * PanelHost.Bounds.Width,
                                           p.Y * PanelHost.Bounds.Height, 0, 0);
        ReadoutHost.Width  = p.W * PanelHost.Bounds.Width;
        ReadoutHost.Height = p.H * PanelHost.Bounds.Height;
    }

    private void OnSolveClick(object? sender, RoutedEventArgs e)
    {
        if (_doc is null) return;
        // The toolbar's Solve is the FULL user grid at the full raster — the "on release" quality of
        // D5, reachable explicitly while M6's scheduler does not exist yet to reach it automatically.
        // The toolbar's Solve is the FULL user grid at the full raster — D5's "on release" quality,
        // reachable explicitly while M6's scheduler does not exist yet to reach it automatically.
        _doc.ViewModel.Harmonica.RequestFrame(new HarmonicaSolver.Options
        {
            Rings = 5, Spokes = 12,
            RasterResolution = HarmonicaSolver.Options.FullRasterResolution,
        });
        Refresh();
    }

    private void OnCycleXUnitClick(object? sender, RoutedEventArgs e)
    {
        _doc?.ViewModel.Harmonica.CyclePowerSweepXUnitCommand.Execute(null);
        Refresh();
    }

    /// <summary>
    /// R-h6-11 — <i>snap to compression</i> is what makes "set the load at compression" expressible
    /// without typing a drive level. Turning it off pins the cursor where the user put it; the
    /// inverse solve is posed at whichever the toggle selects, because intrinsic impedance is
    /// drive-dependent and the equation is only well-posed at a stated drive.
    /// </summary>
    private void OnToggleCursorSnap(object? sender, RoutedEventArgs e)
    {
        if (_doc is null) return;
        var h = _doc.ViewModel.Harmonica;
        if (h.SnapCursorToCompression) h.CursorPinDbm = h.OperatingPointDbm;   // keep it where it is
        h.SnapCursorToCompression = !h.SnapCursorToCompression;
        h.RequestScheduledFrame(dragging: false);
        Refresh();
    }
}
