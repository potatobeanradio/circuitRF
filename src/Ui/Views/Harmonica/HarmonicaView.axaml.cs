using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CircuitRF.Harmonica;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.Harmonica.Renderers;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.Views.Dialogs;

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
        // §4 (R1C) — a resize also moves the strip's PIXEL size, and with it its font (ReadoutFontSize
        // reads the same placement); Refresh() re-places AND re-fonts in one call, through the
        // in-place update path so a resize mid-typing does not eat the caret.
        PanelHost.SizeChanged += (_, _) => Refresh();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_doc is not null)
        {
            _doc.ActivationFocusRequested -= OnActivationFocusRequested;
            _doc.ViewModel.Harmonica.RedrawRequested -= OnRedraw;
            _doc.ViewModel.Harmonica.Pool.Completed -= OnFrameCompleted;
            _doc.ViewModel.Harmonica.Pool.Failed    -= OnFrameFailed;
            _doc.ViewModel.Harmonica.GridSolveProgress -= OnGridSolveProgress;
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
        // §3 (R1C) — fires on a worker thread; marshalled here exactly like Pool.Completed/Failed.
        _doc.ViewModel.Harmonica.GridSolveProgress += OnGridSolveProgress;

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
        // brief-harmonicarf-r5 §3 — the other half of conflate-and-pace: settles the marker-drag
        // pacing state and resubmits a conflated move, if one arrived while this solve was running.
        _doc?.ViewModel.Harmonica.OnPoolSettled(seq);
        Refresh();
    }, DispatcherPriority.Background);

    private void OnFrameFailed(Exception ex, long seq) => Dispatcher.UIThread.Post(() =>
    {
        _doc?.ViewModel.Harmonica.PublishFailure(ex);
        _doc?.ViewModel.Harmonica.OnPoolSettled(seq);
        Refresh();
    }, DispatcherPriority.Background);

    /// <summary>
    /// §3 (R1C) — one Γ point ticked. <b>No throttle</b>: a grid is at most a few hundred points and
    /// each tick is a cheap dispatcher post, so the ~25/s rule other progress bars in this codebase
    /// need is already satisfied by the grid's own size — and the final point always ticks, so the
    /// bar can never land short of full.
    /// </summary>
    private void OnGridSolveProgress(int done, int total) => Dispatcher.UIThread.Post(() =>
    {
        if (_doc is null || total <= 0) return;
        SolveProgressBar.Value      = (double)done / total;
        SolveProgressCounter.Text   = $"{done} / {total}";
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

        // §2 (R1C) — the bottom message line. R-h6-5's rule survives unchanged: the scheduler's own
        // message is never suppressed. When there is nothing to say, the idle line is the same
        // solve-cost summary the removed toolbar's StatusText showed — it updates once per published
        // frame, not continuously, so it is a populated line rather than a changing one.
        var messagesBrush = ToBrush(h.RenderTheme.Messages);
        MessageText.Foreground = messagesBrush;
        MessageText.Text = h.StatusMessage is { Length: > 0 } msg
            ? msg
            : $"{h.LastSolveCount} HB solves · {h.Frame.SmithPower.GridPoints.Count} Γ points · " +
              $"{h.Frame.SmithPower.GridPoints.Count(p => p.IsHole)} holes · {h.Frame.Quality}";

        // §3 (R1C) — "Solving…" plus an inline bar, shown only for a frame that actually sweeps a
        // grid (h.IsSolvingGrid). Reset to empty when idle so a later grid frame starts the bar at 0
        // rather than showing the previous frame's leftover value for one tick.
        bool solvingGrid = h.IsSolvingGrid;
        var progressBrush = ToBrush(h.RenderTheme.ProgressBar);
        SolvingText.IsVisible          = solvingGrid;
        SolvingText.Foreground         = messagesBrush;
        SolveProgressBar.IsVisible     = solvingGrid;
        SolveProgressBar.Foreground    = progressBrush;
        SolveProgressCounter.IsVisible = solvingGrid;
        SolveProgressCounter.Foreground = messagesBrush;
        if (!solvingGrid)
        {
            SolveProgressBar.Value    = 0;
            SolveProgressCounter.Text = "";
        }

        var brush    = ReadoutStripView.BrushFor(h.RenderTheme);
        double fontSize = ReadoutFontSize();
        Readouts.SetInputs(h.Inputs, brush, (key, text) => h.ApplyInput(key, text), fontSize);
        Readouts.SetInputError(h.InputError, brush, fontSize);
        Readouts.SetItems(h.Frame.Readouts, brush, fontSize,
                          ReadoutFormatFor, OnReadoutFormatChanged, OnReadoutCommitEdit, OnReadoutOpenSetDialog);
        PlaceReadoutStrip();

        // brief-harmonicarf-r5 §1 — the canvas has no reference to Readouts itself; this is the
        // channel its own diagnostics overlay reads these two numbers through.
        Canvas.ReadoutSetItemsMs  = Readouts.LastSetItemsMs;
        Canvas.ReadoutSetInputsMs = Readouts.LastSetInputsMs;

        Canvas.InvalidateVisual();
    }

    // ── §5 (R1C) — the readout strip's per-row format, inline editor and Set… dialog ────────────

    /// <summary>R-h9c-7's persisted per-row format, absent ⇒ real/imaginary.</summary>
    private ReadoutFormat ReadoutFormatFor(string key)
        => Vm is { } h && h.Appearance.ReadoutFormats.TryGetValue(key, out var v)
                        && Enum.TryParse<ReadoutFormat>(v, out var f)
            ? f : ReadoutFormat.RealImaginary;

    /// <summary>Display-only — writes ONLY <c>CharmAppearance.ReadoutFormats</c>, never the model
    /// (R-h9c-7).</summary>
    private void OnReadoutFormatChanged(string key, ReadoutFormat format)
    {
        if (Vm is not { } h) return;
        var next = new Dictionary<string, string>(h.Appearance.ReadoutFormats, StringComparer.Ordinal)
        {
            [key] = format.ToString(),
        };
        h.Appearance = h.Appearance with { ReadoutFormats = next };
        Refresh();
    }

    /// <summary>
    /// R-h9c-8's inline editor commit. Parses the typed text in the row's OWN current format
    /// (what-you-see-is-what-you-can-type-back), then writes through the SAME two calls a drag
    /// uses — <c>SetMarkerImpedance</c>/<c>SetMarkerGamma</c> — never a third path.
    /// </summary>
    private bool OnReadoutCommitEdit(HarmonicaReadout row, string text)
    {
        if (Vm is not { } h || row.Side is not { } side || row.Band <= 0) return false;
        var marker = h.Markers.FirstOrDefault(m => m.Side == side && m.Band == row.Band);
        if (marker is null) return false;

        var format = ReadoutFormatFor(row.FormatKey ?? "");
        if (!HarmonicaReadoutFormatting.TryParse(text, format, out var value)) return false;

        if (row.IsGamma) h.SetMarkerGamma(marker, value);
        else             h.SetMarkerImpedance(marker, value);

        h.RequestScheduledFrame(dragging: false);
        return true;
    }

    /// <summary>R-h9c-7's "Set…" — the strip's own commit shape (see
    /// <see cref="Dialogs.HarmonicaSetTerminationDialog"/>) over the SAME write-through as the
    /// inline editor.</summary>
    private async System.Threading.Tasks.Task OnReadoutOpenSetDialogAsync(HarmonicaReadout row)
    {
        if (Vm is not { } h || row.Side is not { } side || row.Band <= 0
            || TopLevel.GetTopLevel(this) is not Window owner) return;
        var marker = h.Markers.FirstOrDefault(m => m.Side == side && m.Band == row.Band);
        if (marker is null) return;

        double z0 = h.Model.Settings.Z0;
        var result = await Dialogs.HarmonicaSetTerminationDialog.ShowAsync(owner, marker.Name, marker.Gamma, z0);
        if (result is not { } edit) return;

        // The SAME two calls a drag uses — whichever the user actually typed in last, never a
        // converted-and-relabelled third path.
        if (edit.Gamma is { } g) h.SetMarkerGamma(marker, g);
        else if (edit.Impedance is { } z) h.SetMarkerImpedance(marker, z);
        else return;

        h.RequestScheduledFrame(dragging: false);
        Refresh();
    }

    /// <summary>R-h9a-13's own rule applies here too — routed through <see cref="RunHook"/> rather
    /// than a discarded Task, so a dialog-construction exception lands on the message line instead
    /// of disappearing.</summary>
    private void OnReadoutOpenSetDialog(HarmonicaReadout row) => RunHook(() => OnReadoutOpenSetDialogAsync(row));

    /// <summary>§2/§3 (R1C) — projects a render-theme <c>SKColor</c> role to an Avalonia brush, the
    /// same conversion <see cref="ReadoutStripView.BrushFor"/> already does for
    /// <c>Harmonica.ReadoutText</c>.</summary>
    private static IBrush ToBrush(SkiaSharp.SKColor c)
        => new SolidColorBrush(Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue));

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
        menus.PowerSweepHook      = () => RunHook(ShowPowerSweepAsync);
        menus.SetZ0Hook           = () => RunHook(ShowSetZ0Async);
        menus.AdvancedSettingsHook= () => RunHook(ShowAdvancedSettingsAsync);

        // H8 — the four H7 left deliberately null. An unwired hook is honest where a faked
        // implementation is not; this phase is what pays the debt.
        menus.SetDutHook          = () => RunHook(ShowSetDutAsync);
        menus.RefreshDutHook      = OnRefreshDut;
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

    /// <summary>
    /// §4.3's <i>Set DUT…</i>. The dialog produces a <c>DutSpec</c>; applying it is
    /// <c>ApplyDut</c>'s job, which is H7's own structural write-back rather than a second one.
    ///
    /// <para><b>R-h9c-10 — "does nothing" traced to here.</b> This guard used to <c>return</c> on a
    /// failed <c>Vm</c>/<c>TopLevel</c> resolution with no message at all — a SILENT no-op that 1A's
    /// own RunHook fix (R-h9a-13) cannot help with, because there is no exception for it to catch: a
    /// guarded early return throws nothing. Every OTHER dialog-opening hook in this file shares the
    /// identical guard shape and is equally silent on the same failure; this one is fixed because it
    /// is the one under report, and R-h9c-13's own rule ("every failure is reported by name") is what
    /// closes it rather than papering over the specific case.</para>
    /// </summary>
    private async System.Threading.Tasks.Task ShowSetDutAsync()
    {
        if (Vm is not { } h) return;   // no document attached — nothing to report against yet
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            h.SolveError = "Set DUT… could not find a window to open the dialog in.";
            Refresh();
            return;
        }

        // The folder-based resolver is installed HERE rather than at startup, because installing it
        // is what makes a kit reachable and this is the first moment anything asks. It starts nothing.
        HarmonicaDutCatalog.RegisterKitResolver();

        var chosen = await Dialogs.HarmonicaSetDutDialog.ShowAsync(owner, h.Model.Dut);
        if (chosen is null) return;   // the dialog's own Cancel — not a failure

        h.ApplyDut(chosen);
        Refresh();
    }

    /// <summary>§4.3's <i>Refresh DUT</i> (R-h9c-12) — re-elaborates the SAME DUT unconditionally.
    /// A real menu affordance was required because the toolbar is gone (§1); it lives beside Set
    /// DUT… on both menu surfaces.</summary>
    private void OnRefreshDut()
    {
        if (Vm is not { } h) return;
        h.RefreshDut();
        Refresh();
    }

    private async System.Threading.Tasks.Task ShowTracePickerAsync()
    {
        if (Vm is not { } h || TopLevel.GetTopLevel(this) is not Window owner) return;
        await new Dialogs.HarmonicaTracePickerDialog(h).ShowDialog<bool>(owner);
        Refresh();
    }

    /// <summary>R-h9b-12 — right-click anywhere on the DCIV panel.</summary>
    private async System.Threading.Tasks.Task ShowDcivSweepsAsync()
    {
        if (Vm is not { } h || TopLevel.GetTopLevel(this) is not Window owner) return;
        await new Dialogs.HarmonicaDcivSweepsDialog(h).ShowDialog(owner);
        Refresh();
    }

    /// <summary>R-h9r2-18 — Display ▸ Power Sweep….</summary>
    private async System.Threading.Tasks.Task ShowPowerSweepAsync()
    {
        if (Vm is not { } h || TopLevel.GetTopLevel(this) is not Window owner) return;
        await new Dialogs.HarmonicaPowerSweepDialog(h).ShowDialog(owner);
        Refresh();
    }

    /// <summary>R-h9r2-20 — Display ▸ Set Z0…. Same silent-guard bug <see cref="ShowPreferencesAsync"/>
    /// had (found while fixing that one, in this same file) — fixed the same way.</summary>
    private async System.Threading.Tasks.Task ShowSetZ0Async()
    {
        if (Vm is not { } h) return;
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            h.SolveError = "Set Z0… could not find a window to open the dialog in.";
            Refresh();
            return;
        }
        await new Dialogs.HarmonicaSetZ0Dialog(h).ShowDialog(owner);
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

    /// <summary>
    /// §7 (R-h9c-15). The default is now a runnable <c>.csch</c> — placed components, wires on the
    /// connection grid, the same bias/terminations, and an HB analysis matching harmonicaRF's own —
    /// built by <see cref="HarmonicaSchematicExport.Export"/>. The <c>.cnl</c> path stays reachable
    /// (R-h7-13's own gate; <c>HarmonicaTestbenchCliTests</c> runs it through the real CLI process)
    /// for the two DUT shapes <c>.csch</c> cannot yet express — an External DUT or a Touchstone-
    /// embedded package — which is exactly why the format follows the EXTENSION the user picks
    /// (the same "picker chooses the format" idiom <see cref="ExportDataAsync"/> already uses for
    /// .npy/.mat/.txt) rather than a second menu item.
    /// </summary>
    private async System.Threading.Tasks.Task ExportTestbenchAsync()
    {
        if (Vm is not { } h || TopLevel.GetTopLevel(this) is not { } top) return;

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = "Export testbench",
            DefaultExtension  = "csch",
            SuggestedFileName = "harmonica-testbench.csch",
            FileTypeChoices =
            [
                new FilePickerFileType("circuitRF schematic (.csch)") { Patterns = ["*.csch"] },
                new FilePickerFileType("Netlist (.cnl)")              { Patterns = ["*.cnl"] },
            ],
        });
        if (file is null) return;

        string path = file.Path.LocalPath;
        try
        {
            if (System.IO.Path.GetExtension(path).Equals(".cnl", StringComparison.OrdinalIgnoreCase))
            {
                string text = HarmonicaInterchange.ExportTestbench(
                    h.Model, h.Terminations, h.OperatingPointDbm);
                await System.IO.File.WriteAllTextAsync(path, text);
            }
            else
            {
                var schematic = HarmonicaSchematicExport.Export(h.Model, h.Terminations, h.OperatingPointDbm);
                SchematicPersistence.SaveToFile(path, schematic);
            }
            h.SolveError = null;
        }
        catch (Exception ex) { h.SolveError = ex.Message; }
        Refresh();
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

    /// <summary>
    /// §5 (R1C) — tab-separated, one row per readout, grouped under a column heading so the shape a
    /// reader pastes into a spreadsheet still reads as four columns rather than one flattened run.
    /// </summary>
    private async System.Threading.Tasks.Task CopyReadoutsAsync()
    {
        if (Vm is not { } h || TopLevel.GetTopLevel(this)?.Clipboard is not { } clip) return;
        var sb = new System.Text.StringBuilder();
        foreach (var column in new[]
                 { ReadoutColumn.General, ReadoutColumn.Source, ReadoutColumn.Load,
                   ReadoutColumn.Mxp, ReadoutColumn.Mxe })
        {
            var rows = h.Frame.Readouts.Where(r => r.Column == column).ToList();
            if (rows.Count == 0) continue;

            sb.AppendLine($"[{column}]");
            foreach (var r in rows) sb.Append(r.Label).Append('\t').AppendLine(r.Value);
        }
        await clip.SetTextAsync(sb.ToString());
    }

    /// <summary>
    /// Owner-reported: "Edit ▸ Settings menu does not open up a settings dialog" — this IS §7.6's
    /// Edit ▸ Preferences… item; harmonicaRF has no menu item literally named "Settings".
    ///
    /// <para><b>Same bug class as R-h9c-10's own fix, in this same file</b> — a silent guard that
    /// `return`s on a failed <c>_doc</c>/<c>TopLevel</c> resolution with no message at all, which
    /// <see cref="RunHook"/> cannot help with because a guarded early return throws nothing. Fixed the
    /// identical way <see cref="ShowSetDutAsync"/> was: report by name instead of returning silently.
    /// </para>
    /// </summary>
    private async System.Threading.Tasks.Task ShowPreferencesAsync()
    {
        if (Vm is not { } h) return;   // no document attached — nothing to report against yet
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            h.SolveError = "Preferences… could not find a window to open the dialog in.";
            Refresh();
            return;
        }
        await new Dialogs.HarmonicaPreferencesDialog(h).ShowDialog(owner);
        Refresh();
    }

    /// <summary>Owner request — Display ▸ Advanced Settings…, for loadline pts / FFT× / charge / M,
    /// moved out of the strip. Uses the R-h9c-10 error-reporting guard (new code, not the silent shape
    /// <see cref="ShowPreferencesAsync"/> just had fixed).</summary>
    private async System.Threading.Tasks.Task ShowAdvancedSettingsAsync()
    {
        if (Vm is not { } h) return;
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            h.SolveError = "Advanced Settings… could not find a window to open the dialog in.";
            Refresh();
            return;
        }
        await new Dialogs.HarmonicaAdvancedSettingsDialog(h).ShowDialog(owner);
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

    /// <summary>
    /// §4 (R1C) — the strip's font tracks its own PLACED pixel size, the same source
    /// <see cref="PlaceReadoutStrip"/> reads, so it moves exactly when the strip's own box does —
    /// on a window resize, and on an Edit Display drag/resize of the strip's panel. Mirrors the Smith
    /// panels' own convention (<c>HarmonicaPanelRenderer.TitleBandHeight</c>): a fraction of the
    /// panel's SHORTER side, clamped so the strip never goes unreadable (below
    /// <see cref="ReadoutStripView.MinFontSize"/>) or stops being dense (above
    /// <see cref="ReadoutStripView.MaxFontSize"/>).
    /// </summary>
    private double ReadoutFontSize()
    {
        if (_doc is null || PanelHost.Bounds.Width <= 0) return 10;

        var p = _doc.ViewModel.Harmonica.Layout.PlacementOf(HarmonicaPanelId.ReadoutStrip);
        return ReadoutStripView.FontSizeFor(p.W * PanelHost.Bounds.Width, p.H * PanelHost.Bounds.Height);
    }

    /// <summary>
    /// R-h9b-10 / R-h9b-12 — the two panels' own right-click gestures. The target is only RECORDED by
    /// <see cref="CircuitRF.Ui.Controls.HarmonicaCanvas"/> (L1-fix's pattern); this rebuilds the menu
    /// fresh every time it opens, so it can never show stale items for a click that landed elsewhere.
    /// </summary>
    private void OnCanvasContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var target = Canvas.ConsumeContextMenuTarget();
        if (target is not { } p || _doc is null || Canvas.Bounds.Width <= 0)
        {
            e.Cancel = true;
            return;
        }

        var h = _doc.ViewModel.Harmonica;
        double w = Canvas.Bounds.Width, ht = Canvas.Bounds.Height;

        var items = new List<object>();

        // R-h9r2-6 — resolve a MARKER first, through the exact hit test a drag uses (same radius, same
        // z-order), BEFORE the panel-scoped branches below — a marker sits inside a Smith panel and the
        // panel-level items must not shadow it.
        var grab = HarmonicaHitTest.Resolve(h.Layout, h.Markers, p.X, p.Y, w, ht,
                                            Canvas.RenderScaling, HarmonicaHitTest.GrabRadiusDevicePixels,
                                            h.Frame.SmithPower.GridPoints, h.ShowGridPoints, h.TopmostMarker,
                                            h.Model.Settings.Z0);

        if (grab.Kind is HarmonicaGrabKind.ExtrinsicMarker or HarmonicaGrabKind.IntrinsicGlyph
                       or HarmonicaGrabKind.VswrHandle)
        {
            BuildMarkerMenu(items, h, grab.Marker!);
            if (sender is ContextMenu markerMenu) markerMenu.ItemsSource = items;
            return;
        }

        var (kind, panelId) = HarmonicaEditTarget.Resolve(h.Layout, [.. h.PickedTraces], p.X, p.Y, w, ht);

        if (panelId == HarmonicaPanelId.PowerSweep)
        {
            // R-h9b-10 — "right-clicking on the power sweep plot X-AXIS LABEL", resolved to the EXACT
            // rect: AxesRenderer.ComputeLabelHitRects is a standalone (non-drawing) accessor for it,
            // so this needs no "generous band" fallback. HarmonicaEditTarget resolves the whole panel,
            // not the label sub-rect, so a click anywhere ELSE in the panel must NOT open this menu.
            var (local, size) = HarmonicaHitTest.ToPanel(h.Layout, panelId, p.X, p.Y, w, ht);
            var plot = HarmonicaPanelRenderer.BuildPowerSweepPlot(h.Frame.PowerSweep, h.RenderTheme);
            var rects = AxesRenderer.ComputeLabelHitRects(plot, size);
            if (rects.XLabel.Contains((float)local.X, (float)local.Y))
            {
                foreach (PowerSweepXUnit unit in Enum.GetValues<PowerSweepXUnit>())
                {
                    var mi = new MenuItem { Header = unit.Label(), IsChecked = h.PowerSweepXUnit == unit };
                    mi.Click += (_, _) => { h.SetPowerSweepXUnitCommand.Execute(unit); Refresh(); };
                    items.Add(mi);
                }
            }
        }
        else if (panelId == HarmonicaPanelId.Loadline)
        {
            // R-h9b-12 — "if user right-clicks ANYWHERE on the DCIV plot". The loadline panel IS the
            // DCIV panel (§7.1's layout combines DCIV + loadline into one rect).
            var dciv = new MenuItem { Header = "DCIV Sweeps…" };
            dciv.Click += (_, _) => RunHook(ShowDcivSweepsAsync);
            items.Add(dciv);
        }

        if (items.Count == 0) { e.Cancel = true; return; }
        if (sender is ContextMenu menu) menu.ItemsSource = items;
    }

    // ── §4 (R2A) — the per-marker context menu ──────────────────────────────

    /// <summary>
    /// R-h9r2-6/7/8/9/10 — three read-only format rows (each with its own "Set…"), a VSWR toggle, a
    /// Snap to Grid toggle, a separator, then Remove — disabled with a stated reason on band 1, on
    /// BOTH sides, per §4's own rule.
    /// </summary>
    private void BuildMarkerMenu(List<object> items, HarmonicaViewModel h, HarmonicaMarker marker)
    {
        double z0 = h.Model.Settings.Z0;
        var z = HarmonicaDataSet.ImpedanceOf(marker.Gamma, z0);

        items.Add(BuildFormatRow(
            $"Γ = {HarmonicaReadoutFormatting.FormatGamma(marker.Gamma, ReadoutFormat.RealImaginary)}",
            HarmonicaTerminationEntryFormat.GammaRealImag, h, marker));
        items.Add(BuildFormatRow(
            $"Γ = {HarmonicaReadoutFormatting.FormatGamma(marker.Gamma, ReadoutFormat.MagnitudeAngle)}",
            HarmonicaTerminationEntryFormat.GammaMagAngle, h, marker));
        items.Add(BuildFormatRow(
            $"Z = {HarmonicaReadoutFormatting.FormatZ(z, ReadoutFormat.RealImaginary)}",
            HarmonicaTerminationEntryFormat.ZRealImag, h, marker));

        items.Add(new Separator());

        // R-h9r2-8 — the VSWR circle. The value itself is edited by dragging its own on-chart handle
        // (HarmonicaGrabKind.VswrHandle), not from this menu — this toggle only shows/hides it, and
        // shows the current ratio so the menu itself doubles as a readout.
        var vswr = new MenuItem
        {
            Header    = $"VSWR Circle ({marker.VswrValue:0.##}:1)",
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = marker.VswrEnabled,
        };
        vswr.Click += (_, _) => { h.ToggleMarkerVswrEnabled(marker); Refresh(); };
        items.Add(vswr);

        // R-h9r2-9 — a snap with no grid to snap to is a no-op, not an error; the tooltip says so
        // rather than the item being disabled.
        bool hasGrid = h.Frame.SmithPower.GridPoints.Count > 0;
        var snap = new MenuItem
        {
            Header    = "Snap to Grid",
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = marker.SnapToGridEnabled,
        };
        if (!hasGrid)
            ToolTip.SetTip(snap, "No grid has been solved yet — this takes effect once one has.");
        snap.Click += (_, _) => { h.ToggleMarkerSnapToGrid(marker); Refresh(); };
        items.Add(snap);

        items.Add(new Separator());

        // R-h9r2-10 — band 1 (S1 and L1) is always present on both sides; disabled with a reason
        // rather than hidden, per R13a.
        bool canRemove = marker.Band != 1;
        var remove = new MenuItem
        {
            Header    = $"Remove {marker.Name}",
            IsEnabled = canRemove,
        };
        if (!canRemove)
            ToolTip.SetTip(remove, $"{marker.Name} is the fundamental and is always present.");
        remove.Click += (_, _) => { h.RemoveMarkerAndShort(marker); Refresh(); };
        items.Add(remove);
    }

    /// <summary>One read-only "Γ = …" / "Z = …" row, carrying its own "Set…" child that opens
    /// <see cref="Dialogs.HarmonicaSetTerminationDialog"/> focused on this row's own format
    /// (R-h9r2-7).</summary>
    private MenuItem BuildFormatRow(string header, HarmonicaTerminationEntryFormat format,
                                    HarmonicaViewModel h, HarmonicaMarker marker)
    {
        var set = new MenuItem { Header = "Set…" };
        set.Click += (_, _) => RunHook(() => ShowMarkerSetDialogAsync(h, marker, format));
        return new MenuItem { Header = header, ItemsSource = new object[] { set } };
    }

    /// <summary>R-h9r2-7's "Set…", opened from the marker menu rather than the readout strip — the
    /// SAME dialog, the SAME two write-through calls
    /// (<see cref="HarmonicaViewModel.SetMarkerGamma"/>/<see cref="HarmonicaViewModel.SetMarkerImpedance"/>)
    /// <see cref="OnReadoutOpenSetDialogAsync"/> already uses, just reached from a different menu and
    /// pre-focused on the row the user actually clicked.</summary>
    private async System.Threading.Tasks.Task ShowMarkerSetDialogAsync(
        HarmonicaViewModel h, HarmonicaMarker marker, HarmonicaTerminationEntryFormat format)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        double z0 = h.Model.Settings.Z0;
        var result = await Dialogs.HarmonicaSetTerminationDialog.ShowAsync(owner, marker.Name, marker.Gamma, z0, format);
        if (result is not { } edit) return;

        if (edit.Gamma is { } g) h.SetMarkerGamma(marker, g);
        else if (edit.Impedance is { } z) h.SetMarkerImpedance(marker, z);
        else return;

        h.RequestScheduledFrame(dragging: false);
        Refresh();
    }
}
