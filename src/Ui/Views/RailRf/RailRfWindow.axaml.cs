using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
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

        DataContextChanged += (_, _) =>
        {
            if (Vm is not { } vm) return;

            // The view model is framework-free and defaults this to an inline call, which is what
            // lets a test drive the Fast loop with no application host. The WINDOW is what knows
            // there is a dispatcher — the same split the rest of this view model keeps.
            vm.PostToUi = a => Dispatcher.UIThread.Post(a);
            vm.RunOffThread = (work, token) => System.Threading.Tasks.Task.Run(work, token);

            SyncTabs();
            vm.PropertyChanged += OnVmPropertyChanged;
        };
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
