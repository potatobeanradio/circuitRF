using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia;
using CircuitRF.Ui.Clipboard;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.Controls;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Matching;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Views.Match;

/// <summary>
/// The Match Designer window (match.md §9). Non-modal, resizable, <b>one per instance</b>: invoking
/// it again on a component that already has one raises the existing window rather than opening a
/// second view of the same design, which would let two windows write the same <c>Design</c> parameter
/// from two different working copies.
/// </summary>
public partial class MatchDesignerWindow : Window
{
    // Keyed on the component, held weakly: a Designer must not be the reason a component the user
    // deleted stays alive, and closing the window removes the entry anyway.
    private static readonly ConditionalWeakTable<EditableComponent, MatchDesignerWindow> Open = new();

    /// <summary>Designer-only constructor; the AXAML designer needs a parameterless one.</summary>
    public MatchDesignerWindow()
    {
        InitializeComponent();
    }

    /// <summary>The view-model, or null before one is bound.</summary>
    private MatchDesignerViewModel? Vm => DataContext as MatchDesignerViewModel;

    /// <summary>
    /// Opens (or raises) the Designer for one placed <c>Match</c>.
    /// </summary>
    public static MatchDesignerWindow Show(
        CircuitRF.Ui.ViewModels.SchematicViewModel schematicVm, EditableComponent comp, Window? owner)
    {
        ArgumentNullException.ThrowIfNull(schematicVm);
        ArgumentNullException.ThrowIfNull(comp);

        if (Open.TryGetValue(comp, out var existing))
        {
            existing.Activate();
            return existing;
        }

        var vm = new MatchDesignerViewModel();
        vm.SetTarget(schematicVm, comp);

        var window = new MatchDesignerWindow { DataContext = vm };
        Open.Add(comp, window);
        window.Closed += (_, _) =>
        {
            Open.Remove(comp);
            vm.Dispose();
        };

        window.Show(owner!);
        return window;
    }

    // ── Field commits ─────────────────────────────────────────────────────────

    // The termination R/X fields, the band edges, the order and the ripple are all InlineEditText now
    // (owner, 2026-08-19). That control owns the whole three-key contract and commits through the
    // view-model's own *Entry properties, so the LostFocus handlers and the BindNumericBox plumbing
    // those fields used to need are gone rather than left wired to controls that no longer exist.

    private void OnTransformNLostFocus(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is MatchTransformRowViewModel r) r.CommitN();
    }

    // ── The slider gesture ────────────────────────────────────────────────────
    //
    // Begin/end around the pointer, not around each value change: the ladder and every element value
    // track the slider live, and only the response PLOTS are held for the gesture (brief §5).

    private void OnSliderPressed(object? sender, PointerPressedEventArgs e) => Vm?.BeginTransformDrag();

    private void OnSliderReleased(object? sender, PointerReleasedEventArgs e) => Vm?.EndTransformDrag();

    // ── Buttons ───────────────────────────────────────────────────────────────

    // ── Flatten to Cell (match.md §11) ────────────────────────────────────────

    /// <summary>
    /// The footer's <c>Flatten to Cell…</c>. Only the VIEW knows which window owns this panel and
    /// how to show a dialog; the decision, the writing and the undo entry all live in
    /// <see cref="MatchDesignerViewModel.Flatten"/>, which the schematic's context menu calls too.
    /// </summary>
    private async void OnFlattenToCell(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;

        var availability = Vm.FlattenAvailability;
        if (!availability.CanRun || availability.ParentDir is null) return;

        var dialog = new Dialogs.MatchFlattenDialog(
            Vm.InstanceName, availability.DefaultName, availability.ParentDir);
        var choice = await dialog.ShowDialog<Dialogs.MatchFlattenChoice?>(this);
        if (choice is null) return;

        Vm.Flatten(choice.ParentDir, choice.CellName, choice.ReplaceInPlace);
    }

    private void OnApplySolution(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is MatchSolutionRowViewModel row) row.Apply();
    }

    private void OnSortGrid(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string column) Vm?.SortElements(column);
    }

    /// <inheritdoc/>
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (Vm is null) return;

        WireButton("CloseButton", (_, _) => Close());
        WireButton("ApplyButton", (_, _) => Vm.Apply());
        WireButton("RevertButton", (_, _) => Vm.Revert());
        WireElementsContextMenu();
        WireSchematicContextMenu();
        WirePlotHost();
        WireButton("RemoveTransformButton", (_, _) => Vm.RemoveLastTransform());
        WireButton("AddTransformButton", (s, _) => ShowAddTransformMenu(s as Control));
        WireButton("ExportButton", (s, _) => ShowExportMenu(s as Control));
        WireButton("SettingsButton", (s, _) => ShowSettingsMenu(s as Control));
        // Same deep link the Parameter Editor's own Help button uses, to the same Reference page.
        // The Match CHAPTER, not the components page's one-paragraph entry (brief-user-docs-content
        // §10 gave Match a chapter of its own, and this window is what it documents). Listed in
        // DocAnchors.WholePages, so the docs build fails if that page stops being emitted.
        WireButton("HelpButton", (_, _) => DocLauncher.Open("reference/match.html"));

        BindNumericBox("BandFractionBox", () => (Vm.PlotBandFraction * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%",
                       t => { if (double.TryParse(t.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) Vm.PlotBandFraction = v / 100.0; });
        BindNumericBox("PlotPointsBox", () => Vm.PlotPoints.ToString(CultureInfo.InvariantCulture),
                       t => { if (int.TryParse(t, out int v)) Vm.PlotPoints = v; });
    }

    private void WireButton(string name, EventHandler<RoutedEventArgs> handler)
    {
        if (this.FindControl<Button>(name) is { } b) b.Click += handler;
    }

    /// <summary>
    /// <b>Copy as CSV</b>, on the network listing's own context menu (owner, 2026-08-20: "remove the
    /// Copy as CSV button — instead make a context menu for the entire grid view with a Copy as CSV
    /// menu that performs the same operation").
    /// </summary>
    /// <remarks>
    /// Built here rather than declared in the AXAML for the reason every other menu in this window is:
    /// a <c>ContextMenu</c> is a popup with its own visual root, so a <c>MenuItem</c> declared inside
    /// one is not reliably reachable by <c>FindControl</c> from the window, and a handler that
    /// silently never attaches is a menu entry that does nothing. The listing itself IS in the name
    /// scope, so the menu is attached to it here and the item is wired in the same breath.
    /// </remarks>
    private void WireElementsContextMenu()
    {
        if (this.FindControl<ItemsControl>("ElementsList") is not { } list) return;

        var csv = new MenuItem { Header = "Copy as CSV" };
        csv.Click += async (_, _) =>
        {
            var clipboard = Clipboard;
            if (Vm is not null && clipboard is not null) await clipboard.SetTextAsync(Vm.ElementsCsv);
        };

        // "Copy" ABOVE "Copy as CSV" (owner, 2026-08-20). The two views are two views of ONE network,
        // so the grid offers the same picture the schematic does — the schematic's own item, built by
        // the same method, not a second implementation of it.
        list.ContextMenu = new ContextMenu { ItemsSource = new[] { CopySchematicMenuItem(), csv } };
    }

    /// <summary>
    /// <b>Copy</b> — the network as a real schematic selection, on the clipboard in every format
    /// <c>SchematicClipboard</c> writes: circuitRF JSON (so it pastes into a schematic page), SVG and
    /// PDF, a PNG, and on Windows CF_ENHMETAFILE, which is the vector form PowerPoint pastes.
    /// </summary>
    /// <remarks>
    /// The projection is <see cref="MatchSchematicCopy"/>'s and the clipboard write is the schematic
    /// editor's own — this method is only the menu entry and the two things a VIEW knows: which
    /// clipboard, and (on Windows) which window handle owns it.
    /// </remarks>
    private MenuItem CopySchematicMenuItem()
    {
        var item = new MenuItem { Header = "Copy" };
        item.Click += async (_, _) => await CopySchematicAsync();
        return item;
    }

    private async Task CopySchematicAsync()
    {
        var clipboard = Clipboard;
        if (Vm is null || clipboard is null) return;

        var model = MatchSchematicCopy.Build(Vm.Ladder);
        if (model.Components.Count == 0) return;   // a refused design has no drawing to copy

        IntPtr ownerHwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        await SchematicClipboard.CopyAsync(
            clipboard, model.Components, model.Wires, model.CanvasObjects, model.GridSize,
            netLabels: null, schematicDirectory: null, ownerHwnd: ownerHwnd);
    }

    /// <summary>
    /// The network pane's own one-item context menu.
    /// </summary>
    /// <remarks>
    /// Attached here rather than declared in the AXAML for the reason every other menu in this window
    /// is: a <c>ContextMenu</c> is a popup with its own visual root, so a <c>MenuItem</c> declared
    /// inside one is not reliably reachable by <c>FindControl</c>, and a handler that silently never
    /// attaches is a menu entry that does nothing.
    /// </remarks>
    private void WireSchematicContextMenu()
    {
        if (this.FindControl<MatchSchematicCanvas>("NetworkSchematic") is not { } canvas) return;
        canvas.ContextMenu = new ContextMenu { ItemsSource = new[] { CopySchematicMenuItem() } };
    }

    // ── The response plots' Data Display host ─────────────────────────────────

    /// <summary>
    /// Connects the two <c>PlotControl</c>s to the view-model's <c>PlotHost</c> — the wiring
    /// <c>PlotContainerView</c> does for a plot on the Data Display canvas, done here because these
    /// two live in a fixed pane instead.
    /// </summary>
    /// <remarks>
    /// <b>Every provider below is a question a PlotControl asks its host, and a null host answers all
    /// of them with silence.</b> That was the shape of four owner-reported 2026-08-20 bugs at once: no
    /// marker info box appeared (nobody created one on <c>MarkerAdded</c>), <c>Copy</c> did nothing
    /// (<c>PlotExporter.CopyPlotToClipboardAsync</c> returns immediately on a null container), marker
    /// selection never highlighted, and arrow keys did not step a marker.
    /// </remarks>
    private void WirePlotHost()
    {
        if (Vm is null) return;

        // Cached, because SyncPlotContainers runs on every layout pass — a name-scope lookup per
        // frame for two controls that cannot change is the kind of cost that only shows up later.
        _magnitudePlot   = this.FindControl<PlotControl>("MagnitudePlotControl");
        _phasePlot       = this.FindControl<PlotControl>("PhasePlotControl");
        _markerInfoLayer = this.FindControl<ItemsControl>("MarkerInfoBoxLayer");

        Bind(_magnitudePlot, Vm.MagnitudeContainer);
        Bind(_phasePlot,     Vm.PhaseContainer);

        SyncPlotTheme();
        ActualThemeVariantChanged += (_, _) => SyncPlotTheme();

        // The containers' logical rectangles must equal the PlotControls' real ones, because that is
        // the coordinate space a marker info box is placed and dragged in (see the overlay's own note
        // in the AXAML). LayoutUpdated rather than SizeChanged: the pane's ScrollViewer can move a
        // plot without resizing it, and a box that stayed behind would be pointing at nothing.
        LayoutUpdated += (_, _) => SyncPlotContainers();
        SyncPlotContainers();

        void Bind(PlotControl? plot, PlotContainerViewModel container)
        {
            if (plot is null) return;

            plot.NextMarkerIndexProvider     = container.GetNextMarkerIndex;
            plot.FindMarkerInfoBoxVmProvider = container.FindMarkerInfoBoxVm;
            plot.ContainerProvider           = () => container;
            plot.SelectedMarkersProvider     = container.GetSelectedMarkers;
            plot.StepSelectedMarkersHandler  = container.StepSelectedMarkers;

            plot.PlotChanged += container.OnPlotChanged;
            plot.MarkerMoved += (_, _) => container.OnMarkerMoved();
            plot.MarkerAdded += container.OnMarkerAdded;
            container.PlotNeedsRedraw += (_, _) => plot.InvalidateVisual();

            // DeletePlotRequested is deliberately NOT handled — the menu item is disabled in the
            // AXAML (CanDeletePlot="False"), so it can never fire, and an unhandled event is the
            // honest reading of "this host does not delete plots".
        }
    }

    private PlotControl?  _magnitudePlot;
    private PlotControl?  _phasePlot;
    private ItemsControl? _markerInfoLayer;

    private void SyncPlotTheme()
    {
        if (Vm is null) return;
        Vm.PlotHost.Theme = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark
            ? RenderTheme.Dark
            : RenderTheme.Light;
        _magnitudePlot?.SetValue(PlotControl.PlotThemeProperty, Vm.PlotHost.Theme);
        _phasePlot?.SetValue(PlotControl.PlotThemeProperty, Vm.PlotHost.Theme);
    }

    private void SyncPlotContainers()
    {
        if (Vm is null || _markerInfoLayer is not { } layer) return;
        Place(_magnitudePlot, Vm.MagnitudeContainer);
        Place(_phasePlot,     Vm.PhaseContainer);

        void Place(PlotControl? plot, PlotContainerViewModel container)
        {
            if (plot is null || plot.Bounds.Width < 1 || plot.Bounds.Height < 1) return;
            if (plot.TranslatePoint(default, layer) is not { } origin) return;

            if (Math.Abs(container.Left - origin.X) < 0.5
                && Math.Abs(container.Top - origin.Y) < 0.5
                && Math.Abs(container.Width - plot.Bounds.Width) < 0.5
                && Math.Abs(container.Height - plot.Bounds.Height) < 0.5)
                return;   // LayoutUpdated fires constantly; only a real move is worth notifying

            container.Left   = origin.X;
            container.Top    = origin.Y;
            container.Width  = plot.Bounds.Width;
            container.Height = plot.Bounds.Height;
            container.NotifyViewProperties();
        }
    }

    private void BindNumericBox(string name, Func<string> read, Action<string> write)
    {
        if (this.FindControl<TextBox>(name) is not { } box) return;
        box.Text = read();
        box.LostFocus += (_, _) => { write(box.Text ?? ""); box.Text = read(); };
    }

    // ── Menus ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The <c>+ add</c> menu: every currently transformable pair, <b>by element name</b> (§9.4).
    /// Recomputed each time it opens, against the ladder as it stands.
    /// </summary>
    private void ShowAddTransformMenu(Control? anchor)
    {
        if (Vm is null || anchor is null) return;
        var pairs = Vm.AvailablePairs();

        var menu = new ContextMenu();
        if (pairs.Count == 0)
        {
            menu.ItemsSource = new[]
            {
                new MenuItem { Header = "No transformable pair in this ladder", IsEnabled = false },
            };
        }
        else
        {
            var items = new List<MenuItem>();
            foreach (var pair in pairs)
            {
                var item = new MenuItem { Header = pair.Display };
                var captured = pair;
                item.Click += (_, _) => Vm.AddTransform(captured);
                items.Add(item);
            }
            menu.ItemsSource = items;
        }
        menu.PlacementTarget = anchor;
        menu.Open(anchor);
    }

    private void ShowExportMenu(Control? anchor)
    {
        if (Vm is null || anchor is null) return;

        var touchstone = new MenuItem { Header = "Touchstone (.s2p) of the design response" };
        touchstone.Click += async (_, _) => await SaveAsync("s2p", "Touchstone", w => Vm.ExportTouchstone(w));

        var listing = new MenuItem { Header = "Component listing (.csv)" };
        listing.Click += async (_, _) => await SaveAsync("csv", "CSV", w => w.Write(Vm.ComponentListingCsv()));

        var gvalues = new MenuItem { Header = "Prototype g-values (.csv)" };
        gvalues.Click += async (_, _) => await SaveAsync("csv", "CSV", w => w.Write(Vm.PrototypeGValuesCsv()));

        var menu = new ContextMenu { ItemsSource = new[] { touchstone, listing, gvalues } };
        menu.PlacementTarget = anchor;
        menu.Open(anchor);
    }

    /// <summary>
    /// Settings (§9.9): display units per dimension, significant digits, Qmin, and whether Q-adjusted
    /// solutions are offered. <b>Nothing else</b> — and in particular no standard-value series.
    /// </summary>
    private void ShowSettingsMenu(Control? anchor)
    {
        if (Vm is null || anchor is null) return;
        var s = Vm.Settings;

        var items = new List<MenuItem>
        {
            UnitMenu("Inductance", MatchDesignerSettings.InductanceUnitOptions,
                     () => s.InductanceUnit, v => s.InductanceUnit = v),
            UnitMenu("Capacitance", MatchDesignerSettings.CapacitanceUnitOptions,
                     () => s.CapacitanceUnit, v => s.CapacitanceUnit = v),
            UnitMenu("Resistance", MatchDesignerSettings.ResistanceUnitOptions,
                     () => s.ResistanceUnit, v => s.ResistanceUnit = v),
            DigitsMenu(),
            QMinMenu(),
        };

        var offer = new MenuItem
        {
            Header = "Offer Q-adjusted solutions",
            Icon = s.OfferQAdjustedSolutions ? new TextBlock { Text = "✓" } : null,
        };
        offer.Click += (_, _) => s.OfferQAdjustedSolutions = !s.OfferQAdjustedSolutions;
        items.Add(offer);

        var menu = new ContextMenu { ItemsSource = items };
        menu.PlacementTarget = anchor;
        menu.Open(anchor);

        MenuItem UnitMenu(string header, IReadOnlyList<string> options, Func<string> read, Action<string> write)
        {
            var parent = new MenuItem { Header = header };
            var children = new List<MenuItem>();
            foreach (string option in options)
            {
                var item = new MenuItem
                {
                    Header = option,
                    Icon = read() == option ? new TextBlock { Text = "✓" } : null,
                };
                string captured = option;
                item.Click += (_, _) => write(captured);
                children.Add(item);
            }
            parent.ItemsSource = children;
            return parent;
        }

        MenuItem DigitsMenu()
        {
            var parent = new MenuItem { Header = "Significant digits" };
            var children = new List<MenuItem>();
            for (int d = 3; d <= 9; d++)
            {
                int captured = d;
                var item = new MenuItem
                {
                    Header = d.ToString(CultureInfo.InvariantCulture),
                    Icon = s.SignificantDigits == d ? new TextBlock { Text = "✓" } : null,
                };
                item.Click += (_, _) => s.SignificantDigits = captured;
                children.Add(item);
            }
            parent.ItemsSource = children;
            return parent;
        }

        MenuItem QMinMenu()
        {
            var parent = new MenuItem { Header = "Q-adjust floor (Qmin)" };
            var children = new List<MenuItem>();
            foreach (double q in new[] { 1.0, 1.5, 2.0, 3.0, 5.0 })
            {
                double captured = q;
                var item = new MenuItem
                {
                    Header = q.ToString("0.#", CultureInfo.InvariantCulture),
                    Icon = Math.Abs(s.QMin - q) < 1e-9 ? new TextBlock { Text = "✓" } : null,
                };
                item.Click += (_, _) => s.QMin = captured;
                children.Add(item);
            }
            parent.ItemsSource = children;
            return parent;
        }
    }

    private async Task SaveAsync(string extension, string label, Action<TextWriter> write)
    {
        var sp = StorageProvider;
        if (sp is null || Vm is null) return;

        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Export {label}",
            SuggestedFileName = $"{Vm.InstanceName}.{extension}",
            DefaultExtension = extension,
            FileTypeChoices = [new FilePickerFileType(label) { Patterns = [$"*.{extension}"] }],
        });
        if (file is null) return;

        try
        {
            await using var stream = await file.OpenWriteAsync();
            using var writer = new StreamWriter(stream);
            write(writer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Vm.ResponseError = $"Export failed: {ex.Message}";
        }
    }
}
