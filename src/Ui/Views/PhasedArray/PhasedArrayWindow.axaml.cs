using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.PhasedArray;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Views.PhasedArray;

/// <summary>
/// brief-paa-9 — the Phased Array tool window, Tools ▸ Phased Array: one window per instance, closed with the window that opened it. The view
/// model does the work; this holds what needs a window — the UI thread for the view model's host, the schematic's Undo and Redo while this
/// window has the keyboard, and starting the scan map and the frequency study when their tabs are shown.
/// </summary>
public partial class PhasedArrayWindow : Window, ICrfMenuWindow
{
    private static readonly Dictionary<EditableComponent, PhasedArrayWindow> Open = new();

    public PhasedArrayWindow()
    {
        InitializeComponent();
        Tabs.SelectionChanged += (_, _) => StartTabWork();
        // the schematic's own Undo and Redo, so an edit made here is undone here as in the schematic
        KeyDown += (_, e) =>
        {
            if (Vm is not { } vm || e.Source is TextBox) return;
            bool cmd = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
            if (!cmd || e.Key != Key.Z && e.Key != Key.Y) return;
            if (e.Key == Key.Y || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) vm.Schematic.UndoRedo.Redo();
            else vm.Schematic.UndoRedo.Undo();
            e.Handled = true;
        };
        Closed += (_, _) =>
        {
            if (Vm is { } vm) { Open.Remove(vm.Instance); vm.Dispose(); }
        };
    }

    private PhasedArrayToolViewModel? Vm => DataContext as PhasedArrayToolViewModel;

    /// <summary>brief-pch-9 — a row of the Beams list clicked: that pair selected on the channel.</summary>
    private void OnBeamRowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control { DataContext: BeamRow row }) Vm?.SelectPair(row);
    }

    public string WindowMenuHeader => Vm?.Title ?? "Phased Array";

    /// <summary>The window's host: the UI thread, and the thread pool.</summary>
    private sealed class DispatcherHost : IPhasedArrayHost
    {
        public void Post(Action action) => Dispatcher.UIThread.Post(action);
        public void Run(Action work) => Task.Run(work);
    }

    /// <summary>The window on <paramref name="instance"/>: the one already open on it, or a new one.</summary>
    public static PhasedArrayWindow ShowFor(SchematicViewModel schematic, EditableComponent instance,
                                            Func<SchematicViewModel, PreparedCircuit?> prepare, Window? owner)
    {
        if (Open.TryGetValue(instance, out var open))
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return open;
        }
        var vm = new PhasedArrayToolViewModel(schematic, instance, prepare, new DispatcherHost());
        var w = new PhasedArrayWindow { DataContext = vm, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PhasedArrayToolViewModel.Frame)) w.StartTabWork();
            if (e.PropertyName == nameof(PhasedArrayToolViewModel.Title)) w.Title = vm.Title;
        };
        Open[instance] = w;
        if (owner is not null)
        {
            void CloseWithOwner(object? _, EventArgs __) => w.Close();
            owner.Closed += CloseWithOwner;
            w.Closed += (_, _) => owner.Closed -= CloseWithOwner;
        }
        w.Show();
        if (owner is not null) NewWindowFront.Keep(w, owner);
        return w;
    }

    /// <summary>R-paa9-5/6 — a shown Scan maps tab computes its map once a settled frame is in (an edit cancelled the last one), and a shown
    /// Frequency tab its study; neither runs during a drag.</summary>
    private void StartTabWork()
    {
        if (Vm is not { Frame: not null } vm || vm.Dragging) return;
        if (ReferenceEquals(Tabs.SelectedItem, ScanTab) && !vm.ScanRunning && (vm.ScanPoints is null || vm.ScanStatus.StartsWith("Cancelled", StringComparison.Ordinal)))
            vm.StartScan();
        if (ReferenceEquals(Tabs.SelectedItem, FrequencyTab) && (vm.Frequency is null || !ReferenceEquals(_frequencyOf, vm.Frame)))
        {
            _frequencyOf = vm.Frame;
            vm.RunFrequency();
        }
    }

    private object? _frequencyOf;

    /// <summary>The Phased Antenna Array's section of the component reference.</summary>
    private void OnHelpClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => DocLauncher.Open("reference/phased-array.html", "tool");
}
