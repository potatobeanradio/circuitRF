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

    private void OnResistanceLostFocus(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is MatchTerminationViewModel t) t.CommitResistance();
    }

    private void OnReactanceLostFocus(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is MatchTerminationViewModel t) t.CommitReactance();
    }

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
        WireButton("CopyGridButton", async (_, _) =>
        {
            var clipboard = Clipboard;
            if (clipboard is not null) await clipboard.SetTextAsync(Vm.ElementsCsv);
        });
        WireButton("RemoveTransformButton", (_, _) => Vm.RemoveLastTransform());
        WireButton("AddTransformButton", (s, _) => ShowAddTransformMenu(s as Control));
        WireButton("ExportButton", (s, _) => ShowExportMenu(s as Control));
        WireButton("SettingsButton", (s, _) => ShowSettingsMenu(s as Control));
        // Same deep link the Parameter Editor's own Help button uses, to the same Reference page.
        WireButton("HelpButton", (_, _) => DocLauncher.OpenComponent(SymbolKind.Match));

        BindNumericBox("RippleBox", () => Vm.RippleDb.ToString("0.###", CultureInfo.InvariantCulture),
                       t => { if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) Vm.RippleDb = v; });
        BindNumericBox("BandFractionBox", () => (Vm.PlotBandFraction * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%",
                       t => { if (double.TryParse(t.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) Vm.PlotBandFraction = v / 100.0; });
        BindNumericBox("PlotPointsBox", () => Vm.PlotPoints.ToString(CultureInfo.InvariantCulture),
                       t => { if (int.TryParse(t, out int v)) Vm.PlotPoints = v; });

        foreach (string name in new[] { "F1Box", "F2Box" })
            if (this.FindControl<TextBox>(name) is { } box)
                box.LostFocus += (_, _) => Vm.CommitBand();
    }

    private void WireButton(string name, EventHandler<RoutedEventArgs> handler)
    {
        if (this.FindControl<Button>(name) is { } b) b.Click += handler;
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
