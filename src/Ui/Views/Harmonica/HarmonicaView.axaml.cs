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
using Material.Icons;
using Material.Icons.Avalonia;

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
        InstallShortcuts(menus);
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
    /// R9C §4 — the first frame goes through the SAME scheduled path every later frame does. It used
    /// to call <c>RequestFrame()</c> bare, which took <c>Options</c>' own defaults — the ladder's
    /// COARSE rung — so the launch picture was a 25-point grid while every frame after it was 37:
    /// measured, that moved the DE optimum from Z = 122.579 − j0.805 to Z = 132.319 − j1.786 and
    /// carried 4 holes instead of 1, which is what the owner saw as "the contours change when I move
    /// L1" (brief-harmonicarf-r9c §0.1). The saving was ~65 solves on a grid that measured 451 ms
    /// whole, in Debug — paid once, on open, deliberately.
    /// </summary>
    private void EnsureFirstSolve()
    {
        if (_solvedOnce || _doc is null) return;
        _solvedOnce = true;
        _doc.ViewModel.Harmonica.RequestScheduledFrame(dragging: false);
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
        // R9A §11 — owner ruling: nothing is posted to the message line while a gesture is live. The
        // idle solve-cost summary updates on every published mid-drag frame, which is a changing line
        // under a moving hand — the one thing §2 (R1C) said this line must not be. IsLive covers a
        // marker drag, an intrinsic-glyph drag, a grid-point drag and an Edit Display grab, which is
        // every case the owner can be inside. The line is restored by the very next Refresh after
        // release, so a solve error raised mid-drag is still reported — one frame later, when it can
        // be read.
        MessageText.Text = MessageLineText(Canvas.Gesture is { IsLive: true }, h.StatusMessage,
            $"{h.LastSolveCount} HB solves · {h.Frame.SmithPower.GridPoints.Count} Γ points · " +
            $"{h.Frame.SmithPower.GridPoints.Count(p => p.IsHole)} holes · {h.Frame.Quality}");

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
        Readouts.SetInputs(h.Inputs, brush, (key, text) => h.ApplyInput(key, text), fontSize,
                          CapacitanceRowActionsFor(h));
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

    /// <summary>R9A §11 — what the message line shows. Pure, so Ui.Tests can pin it without a control.</summary>
    internal static string MessageLineText(bool gestureLive, string? statusMessage, string idleSummary)
        => gestureLive ? "" : (statusMessage is { Length: > 0 } m ? m : idleSummary);

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

    // ── R7D §3.4/§4 — the Settings column's Cgs/Cdg/Cds rows ────────────────────

    /// <summary>Resolves one capacitance row's key to the <see cref="DutCapacitance"/> it names, and
    /// back — the ONE place that mapping is written, so <see cref="CapacitanceRowActionsFor"/>'s three
    /// callbacks cannot disagree about which key is which capacitor.</summary>
    private static DutCapacitance CapacitanceFor(DutCapacitances caps, string key) => key switch
    {
        HarmonicaInputs.KeyCgs => caps.Cgs,
        HarmonicaInputs.KeyCdg => caps.Cdg,
        _                      => caps.Cds,
    };

    private static DutCapacitances With(DutCapacitances caps, string key, DutCapacitance value) => key switch
    {
        HarmonicaInputs.KeyCgs => caps with { Cgs = value },
        HarmonicaInputs.KeyCdg => caps with { Cdg = value },
        _                      => caps with { Cds = value },
    };

    private static string CapacitanceLabel(string key) => key switch
    {
        HarmonicaInputs.KeyCgs => "Cgs",
        HarmonicaInputs.KeyCdg => "Cdg",
        _                      => "Cds",
    };

    /// <summary>Built fresh every <see cref="Refresh"/> — cheap (three closures) and avoids a field
    /// that would otherwise have to track the current <see cref="Vm"/>.</summary>
    private ReadoutStripView.CapacitanceRowActions? CapacitanceRowActionsFor(HarmonicaViewModel h)
        => new(
            IsNonlinear: key => CapacitanceFor(h.Model.Dut.Capacitances, key).IsNonlinear,
            OpenEditor:  key => RunHook(() => OpenCapacitanceEditorAsync(key)),
            UseLinear:   UseLinearCapacitance);

    /// <summary>
    /// R7D §3.4/§4.3 — "Use Nonlinear…"/"Edit Nonlinear C(V)…": hosts the detached Parameter Editor
    /// (<see cref="HarmonicaNonlinearCEditor"/>) seeded from the capacitor's current coefficients
    /// (linear/absent: just its own C0; nonlinear: its own C0…Cn), then writes the result back through
    /// <see cref="HarmonicaViewModel.ApplyDut"/> — R-h8-1's own structural write-back, never a second
    /// mechanism.
    /// </summary>
    private async System.Threading.Tasks.Task OpenCapacitanceEditorAsync(string key)
    {
        if (Vm is not { } h || TopLevel.GetTopLevel(this) is not Window owner) return;

        var current = CapacitanceFor(h.Model.Dut.Capacitances, key);
        IReadOnlyList<double> seed = current.IsNonlinear ? current.Coefficients! : [current.Farads];

        var coeffs = await HarmonicaNonlinearCEditor.EditAsync(owner, CapacitanceLabel(key), seed);
        if (coeffs is null) return;

        var updated = new DutCapacitance { Coefficients = coeffs };
        h.ApplyDut(h.Model.Dut with { Capacitances = With(h.Model.Dut.Capacitances, key, updated) });
        Refresh();
    }

    /// <summary>R7D §3.4 — "Use Linear": drops the coefficients back to the capacitor's own C0 as the
    /// linear value, and states in the status line what was discarded (the owner's own wording).</summary>
    private void UseLinearCapacitance(string key)
    {
        if (Vm is not { } h) return;

        var current = CapacitanceFor(h.Model.Dut.Capacitances, key);
        if (!current.IsNonlinear) return;

        double farads = current.Coefficients!.Count > 0 ? current.Coefficients![0] : 0.0;
        int discarded  = current.Coefficients!.Count - 1;

        var updated = new DutCapacitance { Farads = farads };
        h.ApplyDut(h.Model.Dut with { Capacitances = With(h.Model.Dut.Capacitances, key, updated) });

        if (discarded > 0)
            h.InputError = $"{CapacitanceLabel(key)} switched to linear — discarded {discarded} " +
                           $"higher-order term{(discarded == 1 ? "" : "s")}.";
        Refresh();
    }

    /// <summary>§2/§3 (R1C) — projects a render-theme <c>SKColor</c> role to an Avalonia brush, the
    /// same conversion <see cref="ReadoutStripView.BrushFor"/> already does for
    /// <c>Harmonica.ReadoutText</c>.</summary>
    private static IBrush ToBrush(SkiaSharp.SKColor c)
        => new SolidColorBrush(Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue));

    // ── §7.6's document-scoped keyboard shortcuts ────────────────────────────

    /// <summary>
    /// Round 11 §4 — <b>Ctrl+L</b> toggles Display ▸ Grid Points, through the SAME command the menu
    /// item runs, so the two can never disagree about what the toggle means or about persisting it.
    ///
    /// <para><b>Ctrl only, deliberately — ⌘L is declared on the NativeMenu surfaces instead</b>
    /// (<c>HarmonicaMenuView.axaml</c> and <c>HarmonicaAppMenuInjector</c>). A macOS menu key
    /// equivalent is consumed by AppKit before Avalonia's input pipeline runs, so a gesture declared
    /// on BOTH surfaces would be one keystroke with two live handlers and would toggle twice — i.e.
    /// do nothing. Splitting the two modifiers across the two surfaces gives the user one working
    /// shortcut per platform with no overlap anywhere.</para>
    ///
    /// <para>Rebuilt on every <c>DataContextChanged</c> rather than added once, because the binding
    /// targets THIS document's menu view model; a view reused for a second document would otherwise
    /// keep toggling the first one's grid points.</para>
    /// </summary>
    private void InstallShortcuts(HarmonicaMenuViewModel menus)
    {
        KeyBindings.Clear();
        KeyBindings.Add(new Avalonia.Input.KeyBinding
        {
            Gesture = new Avalonia.Input.KeyGesture(Avalonia.Input.Key.L, Avalonia.Input.KeyModifiers.Control),
            Command = menus.ToggleShowGridPointsCommand,
        });
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
        menus.SettingsHook        = () => RunHook(ShowSettingsAsync);
        menus.AddTraceHook        = () => RunHook(ShowTracePickerAsync);
        menus.PowerSweepHook      = () => RunHook(ShowPowerSweepAsync);
        menus.SetZ0Hook           = () => RunHook(ShowSetZ0Async);

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

    /// <summary>brief-harmonicarf-r6e §3.2/§4 — the power-sweep panel's title fly menu's own
    /// "Axis Limits…", in whichever mode the panel is actually showing.</summary>
    private async System.Threading.Tasks.Task ShowPowerSweepAxesDialogAsync(bool timeDomain)
    {
        if (Vm is not { } h || TopLevel.GetTopLevel(this) is not Window owner) return;
        await new Dialogs.HarmonicaPowerSweepAxesDialog(h, timeDomain).ShowDialog(owner);
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
                                                         z0: h.Model.Settings.Z0,
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
            // NO extension here: the picker appends DefaultExtension itself, so a suggested name that
            // already carries one comes up as "harmonica-testbench.csch.csch" (owner-reported).
            SuggestedFileName = "harmonica-testbench",
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
                   ReadoutColumn.Mxp, ReadoutColumn.Mxe, ReadoutColumn.IntrinsicVds, ReadoutColumn.IntrinsicIds })
        {
            var rows = h.Frame.Readouts.Where(r => r.Column == column).ToList();
            if (rows.Count == 0) continue;

            sb.AppendLine($"[{column}]");
            sb.Append(HarmonicaClipboard.RowsText(rows.Select(r => (r.Label, r.Value))));
        }
        await clip.SetTextAsync(sb.ToString());
    }

    /// <summary>
    /// brief-harmonicarf-r6a §2 — the ONE Settings… dialog, merging what used to be Edit ▸
    /// Preferences… (the colour/appearance editor) and Display ▸ Advanced Settings… (loadline pts /
    /// FFT× / charge / M / §3's contour-kernel controls) into one tabbed
    /// <see cref="Dialogs.HarmonicaSettingsDialog"/>.
    ///
    /// <para><b>Owner-reported: "Edit ▸ Settings menu does not open up a settings dialog."</b>
    /// Investigated (§2.1): the item the owner was actually clicking was circuitRF's OWN
    /// <c>WorkspaceWindow</c> "Settings…" (File menu / the macOS app menu ⌘,) — a genuinely different,
    /// application-scoped dialog with no connection to harmonicaRF, which does something (it is not
    /// dead) but is not what a harmonicaRF document's own settings are. The path that really was
    /// unreachable is harmonicaRF's own settings <i>while docked</i>: before §1.3's injected
    /// <c>harmonicaRF</c> top-level menu, the docked bar carried no document-scoped Edit menu at all —
    /// harmonicaRF's own in-window Menu is hidden on macOS, and the injected set was Markers/Display/
    /// Grid only. §1.3's own <c>Settings…</c> item closes that gap; harmonicaRF's torn-off/in-window
    /// Preferences… item already worked (R-h9c-10's guard was already fixed in an earlier round).
    /// </para>
    /// </summary>
    private async System.Threading.Tasks.Task ShowSettingsAsync()
    {
        if (Vm is not { } h) return;   // no document attached — nothing to report against yet
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            h.SolveError = "Settings… could not find a window to open the dialog in.";
            Refresh();
            return;
        }
        await new Dialogs.HarmonicaSettingsDialog(h).ShowDialog(owner);
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

        // brief-harmonicarf-r6b §4 — the Smith panels' own fly menus, resolved next (still ahead of
        // the Edit-Display panel branches below): a click anywhere inside a Smith panel is always
        // about THAT panel, title band or body.
        var smithPanelId = HarmonicaHitTest.PanelAt(h.Layout, p.X, p.Y, w, ht);
        if (smithPanelId is not null)
        {
            if (Menus.DataContext is HarmonicaMenuViewModel menus)
            {
                var (local, size) = HarmonicaHitTest.ToPanel(h.Layout, smithPanelId, p.X, p.Y, w, ht);
                if (local.Y < HarmonicaPanelRenderer.TitleBandHeight(size))
                    BuildSmithTitleMenu(items, h, menus, smithPanelId);
                else
                    BuildSmithBodyMenu(items, h, menus, smithPanelId);
            }

            if (items.Count == 0) { e.Cancel = true; return; }
            if (sender is ContextMenu smithMenu) smithMenu.ItemsSource = items;
            return;
        }

        var (kind, panelId) = HarmonicaEditTarget.Resolve(h.Layout, [.. h.PickedTraces], p.X, p.Y, w, ht);

        if (panelId == HarmonicaPanelId.PowerSweep)
        {
            // R-h9b-10 — "right-clicking on the power sweep plot X-AXIS LABEL", resolved to the EXACT
            // rect: AxesRenderer.ComputeLabelHitRects is a standalone (non-drawing) accessor for it,
            // so this needs no "generous band" fallback. HarmonicaEditTarget resolves the whole panel,
            // not the label sub-rect, so a click anywhere ELSE in the panel must NOT open this menu.
            //
            // brief-harmonicarf-r6d §4 — this slot's own TITLE band is also a fly-menu target now
            // (Power Sweep / Time Domain), resolved against whichever plot is actually on screen —
            // the two plots' title bands need not coincide in general, so the mode decides which one
            // to hit-test against rather than assuming the power-sweep shape.
            var (local, size) = HarmonicaHitTest.ToPanel(h.Layout, panelId, p.X, p.Y, w, ht);
            var plot = h.ShowPowerSweepTimeDomain
                ? HarmonicaPanelRenderer.BuildTimeDomainPlot(h.Frame.Loadline, h.RenderTheme,
                    HarmonicaPanelRenderer.TimeDomainLimits(h.Model.Settings))
                : HarmonicaPanelRenderer.BuildPowerSweepPlot(h.Frame.PowerSweep, h.RenderTheme,
                    HarmonicaPanelRenderer.PowerSweepLimits(h.Model.Settings));
            var rects = AxesRenderer.ComputeLabelHitRects(plot, size);

            if (rects.Title.Contains((float)local.X, (float)local.Y))
            {
                BuildPowerSweepTitleMenu(items, h);
            }
            // R-hui-2 — owner-reported: the X-axis label's own menu had been merged into the title
            // menu, so it wrongly offered Copy/Time Domain/Autoscale/etc. It gets its OWN, minimal
            // menu again — just the X-unit cycle, checkmarked — and stays power-sweep-mode-only (a
            // plain time axis has no unit to pick).
            else if (!h.ShowPowerSweepTimeDomain && rects.XLabel.Contains((float)local.X, (float)local.Y))
            {
                BuildPowerSweepXUnitMenu(items, h);
            }
            else
            {
                // R-hui-3, owner-reported — right-clicking anywhere ELSE in the panel body (neither
                // the title band nor the X-axis label) must show the SAME menu as the title, not just
                // a bare Copy: BuildPowerSweepTitleMenu already ends with Copy itself.
                BuildPowerSweepTitleMenu(items, h);
            }
        }
        else if (panelId == HarmonicaPanelId.Loadline)
        {
            // brief-harmonicarf-r6e §4 — the SAME property HarmonicaDcivSweepsDialog's own Axis
            // limits checkbox writes (HarmonicaViewModel.SetDcivAutoscale) — one property, two
            // surfaces, no shadow state.
            // R7A §2.3 — Autoscale/Locked, dynamic-icon-only (see AddAutoscaleLockedItems' own
            // remark). R-hui-1/R-hui-6's own rule survives unchanged: HarmonicaViewModel.LockDcivAxes
            // (not a bare SetDcivAutoscale(false)) is what makes "Locked" a real freeze of whatever is
            // ACTUALLY on screen rather than a stale value.
            bool dcivAutoscaleOn = h.Model.Settings.DcivAutoscale;
            AddAutoscaleLockedItems(items, dcivAutoscaleOn,
                onAutoscaleClick: () => { h.SetDcivAutoscale(true); Refresh(); },
                onLockedClick:    () => { h.LockDcivAxes(); Refresh(); });

            // R-h9b-12 — "if user right-clicks ANYWHERE on the DCIV plot". The loadline panel IS the
            // DCIV panel (§7.1's layout combines DCIV + loadline into one rect).
            items.Add(Item("DCIV Sweeps…", MaterialIconKind.Cog, () => RunHook(ShowDcivSweepsAsync)));

            // Copy is always the LAST item, with a separator above it.
            items.Add(new Separator());
            items.Add(BuildCopyMenuItem(panelId));
        }
        else if (!string.IsNullOrEmpty(panelId) && panelId != HarmonicaPanelId.ReadoutStrip)
        {
            // brief-harmonicarf-r6d §6 — a picked trace's own panel (§7.7). Reached through the SAME
            // resolve Edit Display's own move/resize gesture already uses, so this is reachable
            // without a second hit test.
            items.Add(BuildCopyMenuItem(panelId));
        }

        if (items.Count == 0) { e.Cancel = true; return; }
        if (sender is ContextMenu menu) menu.ItemsSource = items;
    }

    /// <summary>brief-harmonicarf-r6d §4 — the power-sweep panel's own TITLE-BAND fly menu: the mode
    /// toggle, then Autoscale/Locked/Axis Limits…, then Copy last with a separator above it.
    /// <b>Owner-reported (R-hui-2): this must NOT also be what the X-axis label opens</b> — R-hui-1
    /// briefly merged the two, which wrongly put Copy/mode-toggle/Autoscale on the axis-label menu;
    /// see <see cref="BuildPowerSweepXUnitMenu"/> for that menu's own, separate, minimal shape.
    /// </summary>
    private void BuildPowerSweepTitleMenu(List<object> items, HarmonicaViewModel h)
    {
        if (Menus.DataContext is not HarmonicaMenuViewModel menus) return;

        bool timeDomainMode = h.ShowPowerSweepTimeDomain;

        // R8B §5.3 — "Power Sweep" and "Time Domain" are a two-state RADIO, grouped under one
        // "Mode ▸" submenu row rather than two loose top-level checkboxes that were secretly
        // exclusive. The submenu's own header names the current mode. Note the trap already recorded
        // at AddAutoscaleLockedItems' call sites: a MenuItem with children never raises Click, so
        // `mode` itself carries NO handler — only its two children do.
        var powerSweep = Toggle("Power Sweep", !timeDomainMode,
            () => { menus.SetPowerSweepModeCommand.Execute("PowerSweep"); Refresh(); }, glyph: MenuGlyph.Radio);
        var timeDomain = Toggle("Time Domain", timeDomainMode,
            () => { menus.SetPowerSweepModeCommand.Execute("TimeDomain"); Refresh(); }, glyph: MenuGlyph.Radio);

        var mode = new MenuItem
        {
            Header = $"Mode: {(timeDomainMode ? "Time Domain" : "Power Sweep")}",
            ItemsSource = new object[] { powerSweep, timeDomain },
        };
        items.Add(mode);

        // brief-harmonicarf-r6e §4 — Autoscale/Locked/Axis Limits…, resolved against whichever mode is
        // ACTUALLY on screen right now: the Time Domain view edits its own SEPARATE stored limit set
        // (§4's own "switching modes must not corrupt the other mode's axes" rule), never the
        // power-sweep one.
        items.Add(new Separator());

        // R7A §2.3 — Autoscale/Locked, dynamic-icon-only (see AddAutoscaleLockedItems' own remark).
        // R-hui-1/R-hui-6's own rule survives unchanged: each mode edits its own SEPARATE stored limit
        // set, so a Time Domain lock/autoscale never touches the power-sweep one or vice versa.
        bool autoscaleOn = timeDomainMode ? h.Model.Settings.TimeDomainAutoscale
                                          : h.Model.Settings.PowerSweepAutoscale;
        AddAutoscaleLockedItems(items, autoscaleOn,
            onAutoscaleClick: () =>
            {
                if (timeDomainMode) h.SetTimeDomainAutoscale(true); else h.SetPowerSweepAutoscale(true);
                Refresh();
            },
            onLockedClick: () =>
            {
                if (timeDomainMode) h.LockTimeDomainAxes(); else h.LockPowerSweepAxes();
                Refresh();
            });

        items.Add(Item("Axis Limits…", MaterialIconKind.Cog,
            () => RunHook(() => ShowPowerSweepAxesDialogAsync(timeDomainMode))));

        // Copy is always the LAST item, with a separator above it.
        items.Add(new Separator());
        items.Add(BuildCopyMenuItem(HarmonicaPanelId.PowerSweep));
    }

    /// <summary>
    /// §5, split back out from <see cref="BuildPowerSweepTitleMenu"/> (R-hui-2, owner-reported) — the
    /// X-axis label's OWN fly menu: just the X-unit cycle (Pout dBm/W, Pin available dBm/W), each
    /// item checkmarked against the CURRENT axis unit so the menu doubles as a readout — and nothing
    /// else. No mode toggle, no Autoscale/Locked/Axis Limits…, no Copy; those belong to the title
    /// band's own menu only. Power-sweep-mode only — the caller gates on
    /// <c>!h.ShowPowerSweepTimeDomain</c> before ever calling this, since a plain time axis has no
    /// unit to pick.
    /// </summary>
    private void BuildPowerSweepXUnitMenu(List<object> items, HarmonicaViewModel h)
    {
        foreach (PowerSweepXUnit unit in Enum.GetValues<PowerSweepXUnit>())
        {
            items.Add(Toggle(unit.Label(), h.PowerSweepXUnit == unit,
                () => { h.SetPowerSweepXUnitCommand.Execute(unit); Refresh(); }, glyph: MenuGlyph.Radio));
        }
    }

    // ── R7A §2 — every fly menu is a real context menu, with icons ──────────

    /// <summary>The repo's existing icon convention (<c>WorkspaceWindow.axaml</c>'s
    /// <c>MenuItem.Icon</c>), reproduced in code since these menus are built in code.</summary>
    private static MaterialIcon Icon(MaterialIconKind kind, double opacity = 1.0)
        => new() { Kind = kind, Width = 16, Height = 16, Opacity = opacity };

    /// <summary>R7A §2.2 — the ONE way a plain (non-toggle, non-submenu) action item is built, so an
    /// icon is never sprinkled at a call site by hand. Every item built through here carries an icon
    /// AND a click handler, never both plus <c>ItemsSource</c> — that combination is what §2.4's VSWR
    /// bug was (a <c>MenuItem</c> with children never raises <c>Click</c>). <paramref name="icon"/> is
    /// nullable (R8B §6) — null leaves <c>Icon</c> unset, for a row that genuinely carries none rather
    /// than being handed a substitute glyph that doesn't mean anything for that row.</summary>
    private static MenuItem Item(string header, MaterialIconKind? icon, Action onClick,
        bool enabled = true, string? tooltip = null)
    {
        var mi = new MenuItem { Header = header, IsEnabled = enabled };
        if (icon is { } k) mi.Icon = Icon(k);
        if (tooltip is not null) ToolTip.SetTip(mi, tooltip);
        mi.Click += (_, _) => onClick();
        return mi;
    }

    /// <summary>R8B §5 — which glyph a <see cref="Toggle"/> row carries: a genuine on/off (checkbox)
    /// or one row of a mutually-exclusive group (radio).</summary>
    private enum MenuGlyph { Check, Radio }

    /// <summary>
    /// R8B §5.2 — the ONE way a toggle row is built, matching Data Display's loadpull marker menu
    /// (<c>MarkerInfoBoxView.PopulateMarkerMenu</c>). <b>Never <c>ToggleType</c></b>: the check glyph
    /// and <c>Icon</c> share the Fluent <c>MenuItem</c> template's leading slot and fight for it (see
    /// <see cref="AddAutoscaleLockedItems"/>'s own remark) — the icon slot carries the state instead,
    /// exactly like every other icon-carrying row in these menus.
    /// </summary>
    private static MenuItem Toggle(string header, bool on, Action onClick,
        bool enabled = true, string? tooltip = null, MenuGlyph glyph = MenuGlyph.Check)
    {
        var kind = glyph == MenuGlyph.Radio
            ? (on ? MaterialIconKind.RadioboxMarked : MaterialIconKind.RadioboxBlank)
            : (on ? MaterialIconKind.CheckboxOutline : MaterialIconKind.CheckboxBlankOutline);
        var mi = new MenuItem { Header = header, IsEnabled = enabled, Icon = Icon(kind) };
        if (tooltip is not null) ToolTip.SetTip(mi, tooltip);
        mi.Click += (_, _) => onClick();
        return mi;
    }

    /// <summary>
    /// R7A §2.3 — "Locked" and "Autoscale", the owner's chosen resolution for the Fluent
    /// <c>MenuItem</c> template's own trap: the check glyph and <c>Icon</c> compete for the same
    /// leading slot, so a <c>ToggleType.CheckBox</c> item that ALSO sets <c>Icon</c> can show a
    /// missing icon, a missing checkmark, or a doubled indent depending on theme (verified visually —
    /// see <c>src/Ui/RESOLVED.md</c>). Neither item carries <c>ToggleType</c> here; the icon alone
    /// carries the state, and both stay always present and always clickable — clicking the
    /// already-active one is a harmless no-op that re-captures the current limits.
    /// </summary>
    private static void AddAutoscaleLockedItems(List<object> items, bool autoscaleOn,
        Action onAutoscaleClick, Action onLockedClick)
    {
        // R9A §10 — Locked now uses the SAME checkbox glyph pair Toggle already gives "Show Grid
        // Points" (CheckboxOutline/CheckboxBlankOutline), rather than a Lock/LockOpenVariant pair — the
        // owner wants Locked to read as the checkbox toggle it is. Autoscale/Locked are a
        // mutually-exclusive pair of the ONE state (autoscaleOn), so Locked's own "on" is !autoscaleOn.
        items.Add(Toggle("Autoscale", autoscaleOn, onAutoscaleClick));
        items.Add(Toggle("Locked", !autoscaleOn, onLockedClick));
    }

    // ── §4 (R2A) — the per-marker context menu ──────────────────────────────

    /// <summary>
    /// R-h9r2-6/7/8/9/10, extended by brief-harmonicarf-r6b §2 — three read-only format rows (each
    /// with its own "Set…"), a "VSWR: &lt;val&gt;" toggle with its own "Set…" submenu, a Snap to Grid
    /// toggle, Add Grid Points, Add Grid Points to VSWR (R9A §6), a separator, then Remove — disabled with a stated reason
    /// on band 1, on BOTH sides, per §4's own rule.
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

        // R8B §7.1 — "Show VSWR" is now its own toggle row (§5.1's Toggle, dynamic icon, never
        // ToggleType); the "VSWR: <val>" row and its "Set…" child appear ONLY when the circle is
        // actually on — turning it off used to leave a value row and a Set… row for a circle that
        // wasn't there. The value row carries NO Click handler (R7A §2.4's trap: a MenuItem with
        // children never raises Click) — it is purely a label now that Set… is its own child rather
        // than a sibling. Saturated (§7.3) reports the bound, not a clamped number the marker can't
        // actually be dragged to.
        items.Add(Toggle("Show VSWR", marker.VswrEnabled,
            () => { h.ToggleMarkerVswrEnabled(marker); Refresh(); }));

        if (marker.VswrEnabled)
        {
            bool saturated = Math.Abs(marker.VswrValue) >= HarmonicaVswrHandle.InfiniteVswr;
            var setItem = Item("Set…", MaterialIconKind.Cog,
                () => RunHook(() => ShowMarkerSetVswrDialogAsync(h, marker)));
            items.Add(new MenuItem
            {
                Header      = HarmonicaReadoutFormatting.FormatVswr(marker.VswrValue, saturated),
                ItemsSource = new object[] { setItem },
            });
        }

        // R-h9r2-9 — a snap with no grid to snap to is a no-op, not an error; the tooltip says so
        // rather than the item being disabled.
        bool hasGrid = h.Frame.SmithPower.GridPoints.Count > 0;
        items.Add(Toggle("Snap to Grid", marker.SnapToGridEnabled,
            () => { h.ToggleMarkerSnapToGrid(marker); Refresh(); },
            tooltip: hasGrid ? null : "No grid has been solved yet — this takes effect once one has."));

        // R9D §2 — S1 only. On any other marker the row is absent rather than disabled: "match the SOURCE to
        // the device's own input impedance" is not a thing a load marker or a harmonic marker can mean, so
        // there is nothing to explain in a tooltip (R13a's "disabled with a stated reason" rule is for items
        // that are meaningful but unavailable, which this is not).
        if (marker.Side == TerminationSideKind.Source && marker.Band == 1)
            items.Add(Item("Match to Zin*", MaterialIconKind.SwapHorizontal,
                () => { h.RequestConjugateMatch(HarmonicaViewModel.ConjugateMatchBackoffDb); Refresh(); },
                tooltip: "Sets ZS1 to the conjugate of Zin at the nearest already-solved drive level about " +
                         "5 dB below compression, then re-solves the loadpull."));

        // brief-harmonicarf-r6b §2.2 — a Γ point AT the marker's own Γ, additive on top of the
        // current ring/spoke preset; persists in the .charm.
        items.Add(Item("Add Grid Points", MaterialIconKind.PlusCircleOutline,
            () => { h.AddGridPoint(marker.Gamma); Refresh(); },
            tooltip: "Adds this marker's own Γ to the loadpull grid, on top of the current preset — " +
                     "persists in the file. Grid ▸ Reset Grid or picking a new Grid Preset clears it."));

        // brief-harmonicarf-r6b §2.3 — 12 points uniformly spaced on the marker's own VSWR locus,
        // through the same path. Disabled (greyed) when the circle itself is off.
        items.Add(Item("Add Grid Points to VSWR", MaterialIconKind.PlusCircleMultipleOutline,
            () => { h.AddGridPointsOnVswrCircle(marker); Refresh(); },
            enabled: marker.VswrEnabled,
            tooltip: marker.VswrEnabled ? null : "Turn on this marker's VSWR circle first."));

        items.Add(new Separator());

        // R-h9r2-10 — band 1 (S1 and L1) is always present on both sides; disabled with a reason
        // rather than hidden, per R13a.
        bool canRemove = marker.Band != 1;
        items.Add(Item($"Remove {marker.Name}", MaterialIconKind.Delete,
            () => { h.RemoveMarkerAndShort(marker); Refresh(); },
            enabled: canRemove,
            tooltip: canRemove ? null : $"{marker.Name} is the fundamental and is always present."));
    }

    /// <summary>brief-harmonicarf-r6b §2.1's "Set…" — validates finite and ≥ 1, reject-and-keep on bad
    /// input; OK commits through <see cref="HarmonicaViewModel.SetMarkerVswr"/>, the same call the
    /// drag uses (which also enables the circle if it was off).</summary>
    private async System.Threading.Tasks.Task ShowMarkerSetVswrDialogAsync(
        HarmonicaViewModel h, HarmonicaMarker marker)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var result = await Dialogs.HarmonicaSetVswrDialog.ShowAsync(owner, marker.Name, marker.VswrValue);
        if (result is not { } v) return;

        h.SetMarkerVswr(marker, v);
        Refresh();
    }

    // ── §4 (R6B) — the Smith panels' own fly menus ───────────────────────────

    /// <summary>brief-harmonicarf-r6b §4.2 — a right-click anywhere in a Smith panel's BODY: Copy
    /// (this panel, never the whole canvas — the panel id is already resolved) and Show Grid Points,
    /// bound to the SAME commands the Edit/Display menus use.</summary>
    private void BuildSmithBodyMenu(List<object> items, HarmonicaViewModel h,
                                    HarmonicaMenuViewModel menus, string panelId)
    {
        // R8B §4 — "Add Load Marker" / "Add Source Marker", above Show Grid Points.
        items.Add(BuildAddMarkerMenuItem(h, TerminationSideKind.Load));
        items.Add(BuildAddMarkerMenuItem(h, TerminationSideKind.Source));

        var gridPoints = Toggle("Show Grid Points", h.ShowGridPoints,
            () => { menus.ToggleShowGridPointsCommand.Execute(null); Refresh(); });
        items.Add(gridPoints);

        // Copy is always the LAST item, with a separator above it.
        items.Add(new Separator());
        items.Add(BuildCopyMenuItem(panelId));
    }

    /// <summary>R8B §4 — "Add Load/Source Marker": adds the next unused band, lowest first. Disabled
    /// with a stated reason once every band up to the harmonic order already has a marker (R13a's
    /// standing rule — never hidden).</summary>
    private MenuItem BuildAddMarkerMenuItem(HarmonicaViewModel h, TerminationSideKind side)
    {
        string label = side == TerminationSideKind.Load ? "Add Load Marker" : "Add Source Marker";
        int? next = NextUnusedBand(h.Markers, side, h.Terminations.HarmonicCount);

        return Item(label, MaterialIconKind.PlusCircleOutline,
            () => { if (next is { } band) { h.AddMarkerBandAndShow(side, band); Refresh(); } },
            enabled: next is not null,
            tooltip: next is not null ? null :
                $"All {(side == TerminationSideKind.Load ? "load" : "source")} bands up to the " +
                $"harmonic order (K = {h.Terminations.HarmonicCount}) already have markers.");
    }

    /// <summary>The lowest band ≥ 1 on <paramref name="side"/> with no marker yet, or null once every
    /// band up to <paramref name="harmonicCount"/> is taken. <c>internal static</c> and pure so it can
    /// be tested directly — the menu itself cannot be instantiated in <c>tests/Ui.Tests</c>.</summary>
    internal static int? NextUnusedBand(IEnumerable<HarmonicaMarker> markers, TerminationSideKind side, int harmonicCount)
    {
        var taken = markers.Where(m => m.Side == side).Select(m => m.Band).ToHashSet();
        for (int band = 1; band <= harmonicCount; band++)
            if (!taken.Contains(band)) return band;
        return null;
    }

    /// <summary>brief-harmonicarf-r6b §4.3 — a right-click on a Smith panel's TITLE band: Contour
    /// Plane and Contour Harmonic on both charts, plus Efficiency Metric on the efficiency chart only
    /// — every item bound to the SAME command <c>Display ▸ …</c> already uses, with the current
    /// selection shown checked so the menu doubles as a readout.</summary>
    private void BuildSmithTitleMenu(List<object> items, HarmonicaViewModel h,
                                     HarmonicaMenuViewModel menus, string panelId)
    {
        var plane = new MenuItem { Header = "Contour Plane", Icon = Icon(MaterialIconKind.ChartBellCurve) };
        var load = Toggle("Load", h.GridSide == TerminationSide.Load,
            () => { menus.SetGridSideCommand.Execute("Load"); Refresh(); }, glyph: MenuGlyph.Radio);
        var source = Toggle("Source", h.GridSide == TerminationSide.Source,
            () => { menus.SetGridSideCommand.Execute("Source"); Refresh(); }, glyph: MenuGlyph.Radio);
        plane.ItemsSource = new object[] { load, source };
        items.Add(plane);

        // menus.ContourHarmonics already tracks K (RebuildBandMenus) — never hardcode f₀/2f₀/3f₀,
        // the exact owner-reported bug R-h7-2's own item names.
        var harmonic = new MenuItem { Header = "Contour Harmonic", Icon = Icon(MaterialIconKind.SineWave) };
        var harmonicItems = new List<object>();
        foreach (var band in menus.ContourHarmonics)
        {
            harmonicItems.Add(Toggle(band.Header, h.GridHarmonic == band.Band,
                () => { band.SelectCommand.Execute(null); Refresh(); }, glyph: MenuGlyph.Radio));
        }
        harmonic.ItemsSource = harmonicItems;
        items.Add(harmonic);

        if (panelId == HarmonicaPanelId.SmithEfficiency)
        {
            var eff = new MenuItem { Header = "Efficiency Metric", Icon = Icon(MaterialIconKind.Percent) };
            var de = Toggle("Drain Efficiency", h.EfficiencyMetric == GridMetric.DrainEfficiency,
                () => { menus.SetEfficiencyMetricCommand.Execute("DE"); Refresh(); }, glyph: MenuGlyph.Radio);
            var pae = Toggle("PAE", h.EfficiencyMetric == GridMetric.Pae,
                () => { menus.SetEfficiencyMetricCommand.Execute("PAE"); Refresh(); }, glyph: MenuGlyph.Radio);
            eff.ItemsSource = new object[] { de, pae };
            items.Add(eff);
        }
    }

    /// <summary>
    /// brief-harmonicarf-r6d §6 — "one helper that takes a resolved panelId and builds the Copy
    /// MenuItem, used by every branch." A failure lands on <see cref="HarmonicaViewModel.SolveError"/>
    /// through <see cref="RunHook(Func{System.Threading.Tasks.Task})"/>, like every other menu hook
    /// (R-h9a-13) — this is the ONE Click handler shape every caller shares, so a future panel needs
    /// no new hook wiring of its own.
    /// </summary>
    private MenuItem BuildCopyMenuItem(string panelId)
        => Item("Copy", MaterialIconKind.ContentCopy, () => RunHook(() => CopyPanelAsync(panelId)));

    /// <summary>brief-harmonicarf-r6b §4.2, generalised by r6d §6 to every panel's own fly menu — the
    /// SAME <see cref="HarmonicaClipboard.CopyAsync"/> Edit ▸ Copy Plot calls, with the resolved
    /// <paramref name="panelId"/> in place of <c>Canvas.PanelUnderPointer()</c> — never a second
    /// exporter.</summary>
    private async System.Threading.Tasks.Task CopyPanelAsync(string panelId)
    {
        if (Vm is not { } h) return;
        try
        {
            string what = await HarmonicaClipboard.CopyAsync(Canvas, h, panelId);
            h.SolveError = $"Copied {what} to the clipboard.";
        }
        catch (Exception ex) { h.SolveError = ex.Message; }
        Refresh();
    }

    /// <summary>
    /// One "Γ = …" / "Z = …" row that opens <see cref="Dialogs.HarmonicaSetTerminationDialog"/>
    /// focused on this row's own format (R-h9r2-7).
    ///
    /// <para><b>R7A §2.4 — flattened, no more "Set…" child.</b> A <c>MenuItem</c> that has children
    /// never raises <c>Click</c> — pointing at or clicking the row only ever opened its lone "Set…"
    /// submenu, so the row itself (the only thing it does) was unreachable in one click. The row is
    /// now the click target directly.</para>
    ///
    /// <para><b>R8B §6 — no icon.</b> Ω made no sense on a Γ row (this is a Γ menu, not an impedance
    /// one) and was never anything but a placeholder to satisfy <see cref="Item"/>'s old
    /// non-nullable icon parameter. Explicitly null rather than substituted with a different glyph —
    /// nothing else in these menus is icon-less today, so a future reader should find this deliberate
    /// rather than fill it back in.</para>
    /// </summary>
    private MenuItem BuildFormatRow(string header, HarmonicaTerminationEntryFormat format,
                                    HarmonicaViewModel h, HarmonicaMarker marker)
        => Item(header, icon: null,
            () => RunHook(() => ShowMarkerSetDialogAsync(h, marker, format)));

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
