using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CircuitRF.Render;
using CircuitRF.Ui.RailRf;
using CircuitRF.Ui.Views.Match;

namespace CircuitRF.Ui.Views.RailRf;

/// <summary>
/// The railRF window (railrf.md §11).
/// </summary>
/// <remarks>
/// <b>It is the Match Designer's window, structurally.</b> Shown UNOWNED so it can go behind the
/// workspace, cascaded off the workspace's corner by <see cref="MatchWindowPlacement"/>'s own
/// arithmetic, closed with its owner, and deduplicated per DOCUMENT rather than per window — two
/// views of one <c>.crail</c> would write it from two working copies.
///
/// <para><b>The code-behind does three things and no more</b>, which is the line that window already
/// draws: it opens and positions the window, it binds the two tab strips (a <c>ToggleButton</c> strip
/// has no single selected-value property to bind, so the grouping is done here rather than with four
/// converters), and it gives the view model its UI-thread post. Everything else is the view model's.
/// </para>
/// </remarks>
public partial class RailRfWindow : Window
{
    /// <summary>One window per DOCUMENT PATH. A standalone window (no path yet) is not
    /// deduplicated — two of those are two independent scratch documents, which is a thing a user may
    /// legitimately want side by side, exactly as the Match Designer's standalone is.</summary>
    private static readonly Dictionary<string, RailRfWindow> Open =
        new(StringComparer.OrdinalIgnoreCase);

    public RailRfWindow()
    {
        InitializeComponent();

        CopperTab.Click     += (_, _) => SetOverlay(RailBoardOverlay.Copper);
        DropTab.Click       += (_, _) => SetOverlay(RailBoardOverlay.Drop);
        ImpedanceTab.Click  += (_, _) => SetOverlay(RailBoardOverlay.Impedance);
        ClassTab.Click      += (_, _) => SetOverlay(RailBoardOverlay.Class);
        DcTab.Click         += (_, _) => SetResultsTab(RailResultsTab.Dc);
        FrequencyTab.Click  += (_, _) => SetResultsTab(RailResultsTab.Frequency);

        WireImportButton();
        WireBoardCanvas();

        DataContextChanged += (_, _) =>
        {
            if (Vm is not { } vm) return;

            // The view model is framework-free and defaults this to an inline call, which is what
            // lets a test drive the Fast loop with no application host. The WINDOW is what knows
            // there is a dispatcher — the same split the rest of this view model keeps.
            vm.PostToUi = a => Dispatcher.UIThread.Post(a);
            vm.RunOffThread = (work, token) => System.Threading.Tasks.Task.Run(work, token);

            SyncTabs();
            BindBoardOverlay(vm);
            vm.PropertyChanged += OnVmPropertyChanged;
        };
    }

    // ── The board view (brief 8) ─────────────────────────────────────────────────────

    /// <summary>
    /// Everything the board panel needs that is not the view model’s: the overlay seam, the wider
    /// keyboard gate, and the four buttons.
    /// </summary>
    /// <remarks>
    /// <b>No navigation code, and that is the whole design</b> (§11.6). Every gesture is
    /// <c>LayoutCanvas</c>’s own — the wheel, the pan latch, <c>F</c>, <c>Z</c>, Ctrl/⌘ +/-, the arrow
    /// keys and Escape — so someone who has learned the layout editor has learned this window. What is
    /// added here is R-rail8-4’s WIDER gate, which is a predicate and not a gesture: railRF’s left
    /// column is editable rows, so no navigation key may fire while focus is in a text field.
    /// </remarks>
    private void WireBoardCanvas()
    {
        BoardCanvas.NavigationKeysSuppressed = RailKeyboardGate.For(this);

        // The magnifier’s lit state comes from the CANVAS, which owns the mode — including the disarm
        // it performs itself when the drag ends or Escape is pressed, neither of which this window can
        // see. LayoutEditorView’s own pattern, for its own reason.
        BoardCanvas.ZoomBoxArmedChanged += (_, _) =>
            BoardZoomBoxBtn.Classes.Set("ToolActive", BoardCanvas.ZoomBoxArmed);

        // A theme change is TWO different events and a view that paints its own colours needs both
        // (src/Ui/CLAUDE.md): this one is light-vs-dark, and ThemeService.ThemeChanged is a different
        // THEME being selected. Without the second, picking a new theme repaints the artwork and
        // leaves the map in the old colours — owner-reported twice, in different views.
        // Unsubscribe first on attach: ThemeService.ThemeChanged is a static, process-wide event and
        // a re-attach must not stack a second handler on it.
        ActualThemeVariantChanged += (_, _) => ApplyMapTheme();
        AttachedToVisualTree += (_, _) =>
        {
            ThemeService.ThemeChanged -= OnThemeChanged;
            ThemeService.ThemeChanged += OnThemeChanged;
            ApplyMapTheme();
        };
        DetachedFromVisualTree += (_, _) => ThemeService.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyMapTheme();

    private void ApplyMapTheme()
    {
        if (Vm is not { } vm) return;
        vm.BoardOverlayLayer.Theme = RailMapTheme.FromTheme(
            ThemeService.Active,
            ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? ColorVariant.Dark : ColorVariant.Light);
    }

    private RailLayoutOverlay? _boundOverlay;

    private void BindBoardOverlay(RailRfViewModel vm)
    {
        if (ReferenceEquals(_boundOverlay, vm.BoardOverlayLayer)) return;

        if (_boundOverlay is not null) _boundOverlay.OverlayChanged -= OnOverlayChanged;
        _boundOverlay = vm.BoardOverlayLayer;
        _boundOverlay.OverlayChanged += OnOverlayChanged;

        BoardCanvas.CanvasOverlay = _boundOverlay;
        ApplyMapTheme();
    }

    /// <summary>
    /// A map repaint, and <b>nothing else</b>.
    /// </summary>
    /// <remarks>
    /// R-rail8-2: <c>InvalidateOverlay</c> repaints without disturbing <c>LayoutPathCache</c>, which
    /// on a real board is the difference between a repaint and a rebuild of half a million shapes.
    /// Anything that reached for <c>LayoutView</c> to make the map change would pay that cost on every
    /// frame of every solve.
    /// </remarks>
    private void OnOverlayChanged() => BoardCanvas.InvalidateOverlay();

    private void OnBoardZoomToFit(object? sender, RoutedEventArgs e)
    {
        BoardCanvas.ZoomToFit();
        BoardCanvas.Focus();
    }

    /// <summary>The magnifier ARMS and does not zoom — the box the next left-drag draws is what gets
    /// framed. Focus goes back to the canvas because the gesture, its Escape and its rubber band all
    /// live there.</summary>
    private void OnBoardZoomBoxTool(object? sender, RoutedEventArgs e)
    {
        if (BoardCanvas.ZoomBoxArmed) BoardCanvas.DisarmZoomBox(); else BoardCanvas.ArmZoomBox();
        BoardCanvas.Focus();
    }

    private void OnBoardZoomOut(object? sender, RoutedEventArgs e)
    {
        BoardCanvas.ZoomOut();
        BoardCanvas.Focus();
    }

    private void OnBoardZoom1To1(object? sender, RoutedEventArgs e)
    {
        BoardCanvas.Zoom1To1();
        BoardCanvas.Focus();
    }

    /// <summary>The view model, or null before one is bound.</summary>
    private RailRfViewModel? Vm => DataContext as RailRfViewModel;

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RailRfViewModel.SelectedBoardOverlay)
                           or nameof(RailRfViewModel.SelectedResultsTab))
            SyncTabs();
    }

    private void SetOverlay(RailBoardOverlay overlay)
    {
        if (Vm is { } vm) vm.SelectedBoardOverlay = overlay;
        SyncTabs();
    }

    private void SetResultsTab(RailResultsTab tab)
    {
        if (Vm is { } vm) vm.SelectedResultsTab = tab;
        SyncTabs();
    }

    /// <summary>
    /// Keeps the two strips showing what the view model says.
    /// </summary>
    /// <remarks>
    /// <b>Re-asserted rather than left to the toggle</b>: a <c>ToggleButton</c> toggles itself on
    /// click, so clicking the already-selected tab would otherwise turn the strip OFF and leave the
    /// board showing an overlay no tab claims. Setting all of them from the one property means the
    /// strip cannot get into a state the view model does not name.
    /// </remarks>
    private void SyncTabs()
    {
        var overlay = Vm?.SelectedBoardOverlay ?? RailBoardOverlay.Copper;
        CopperTab.IsChecked    = overlay == RailBoardOverlay.Copper;
        DropTab.IsChecked      = overlay == RailBoardOverlay.Drop;
        ImpedanceTab.IsChecked = overlay == RailBoardOverlay.Impedance;
        ClassTab.IsChecked     = overlay == RailBoardOverlay.Class;

        var tab = Vm?.SelectedResultsTab ?? RailResultsTab.Dc;
        DcTab.IsChecked        = tab == RailResultsTab.Dc;
        FrequencyTab.IsChecked = tab == RailResultsTab.Frequency;
    }

    // ── Opening ───────────────────────────────────────────────────────────────

    /// <summary>Opens (or raises) the window for one <c>.crail</c>.</summary>
    public static RailRfWindow Show(CircuitRF.Design.RailRf.RailDocument document, string path, Window? owner)
    {
        ArgumentNullException.ThrowIfNull(document);

        string key = Path.GetFullPath(path);
        if (Open.TryGetValue(key, out var existing))
        {
            existing.Activate();
            return existing;
        }

        var vm = new RailRfViewModel(document, key);
        var window = new RailRfWindow { DataContext = vm };
        Open[key] = window;
        window.Closed += (_, _) =>
        {
            Open.Remove(key);
            vm.Dispose();
        };

        ShowUnowned(window, owner);
        return window;
    }

    /// <summary>Opens a window bound to nothing — Tools ▸ railRF, before an import.</summary>
    /// <remarks>
    /// <b>Not deduplicated</b>, unlike <see cref="Show"/>. That method keeps one window per document
    /// because two views of one <c>.crail</c> would write it from two working copies; a standalone
    /// window writes no document at all until it is saved.
    /// </remarks>
    public static RailRfWindow ShowStandalone(Window? owner)
    {
        var vm = new RailRfViewModel();
        var window = new RailRfWindow { DataContext = vm };
        window.Closed += (_, _) => vm.Dispose();

        ShowUnowned(window, owner);
        return window;
    }

    /// <summary>The result a railRF window currently shows for that document, or null when none is
    /// open. What the Properties panel's summary reads — it runs nothing of its own.</summary>
    public static RailResultView? ResultFor(string path) =>
        Open.TryGetValue(Path.GetFullPath(path), out var window)
            ? (window.DataContext as RailRfViewModel)?.Current
            : null;

    /// <summary>
    /// Shows the window as an INDEPENDENT top-level, positioned over <paramref name="owner"/> but not
    /// owned by it.
    /// </summary>
    /// <remarks>
    /// <b>The Match Designer's own method, for its own reason.</b> An owned window is not merely
    /// non-modal — every platform keeps it above its owner for as long as it exists, so clicking the
    /// workspace raises the workspace UNDERNEATH and the window never goes behind. Dropping the owner
    /// costs placement and lifetime, so both are done explicitly: cascaded off the owner's top-left
    /// through <see cref="MatchWindowPlacement.Cascade"/> (set before <c>Show</c>, because a window
    /// positioned only once it is on screen is a visible jump, and re-asserted after, because whether
    /// a platform honours a Move on an unshown window is the platform's business), and closed with
    /// the owner.
    /// </remarks>
    private static void ShowUnowned(RailRfWindow window, Window? owner)
    {
        if (owner is null)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.Show();
            return;
        }

        window.WindowStartupLocation = WindowStartupLocation.Manual;

        void CloseWithOwner(object? _, EventArgs __) => window.Close();
        owner.Closed += CloseWithOwner;
        window.Closed += (_, _) => owner.Closed -= CloseWithOwner;

        var at = MatchWindowPlacement.Cascade(
            owner.Position,
            owner.RenderScaling,
            owner.Screens?.ScreenFromWindow(owner)?.WorkingArea);

        window.Position = at;
        window.Show();
        window.Position = at;
    }
}
