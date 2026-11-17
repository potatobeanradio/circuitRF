using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.PhasedArray;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Views.PhasedArray;

/// <summary>
/// brief-paa-9 — the Phased Array tool window, Tools ▸ Phased Array, or the button on a Phased Antenna Array's Component Parameters: one
/// window per instance, closed with the window that opened it. The view model does the work; this holds what needs a window — the UI thread
/// for the view model's host, the schematic's Undo and Redo, Q and Esc while this window has the keyboard, the page buttons, and starting the
/// scan map, the failure trials and the frequency study when their pages are shown.
/// </summary>
public partial class PhasedArrayWindow : Window, ICrfMenuWindow
{
    private static readonly Dictionary<EditableComponent, PhasedArrayWindow> Open = new();

    public PhasedArrayWindow()
    {
        InitializeComponent();
        // a left click anywhere but the aperture (which answers its own) and the controls clears the selection; a right click never does
        AddHandler(PointerPressedEvent, OnAnyPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        DataContextChanged += (_, _) =>
        {
            if (_watched is not null) _watched.PropertyChanged -= OnVmPropertyChanged;
            _watched = Vm;
            if (_watched is not null) _watched.PropertyChanged += OnVmPropertyChanged;
            SyncPageButtons();
            SyncBars();
        };
        // the schematic's own Undo and Redo, so an edit made here is undone here as in the schematic
        KeyDown += (_, e) =>
        {
            if (Vm is not { } vm || e.Source is TextBox or NumericUpDown or SelectableTextBlock) return;
            // Esc, as everywhere else: nothing selected
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
            {
                if (vm.ClearSelection()) e.Handled = true;
                return;
            }
            // Q, as in the schematic: the selected elements off (or on again), else the element under the pointer
            if (e.Key == Key.Q && e.KeyModifiers == KeyModifiers.None)
            {
                if (vm.ToggleSelectedOrHovered()) e.Handled = true;
                return;
            }
            bool cmd = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
            // R, or Cmd/Ctrl+R: run the scan maps, from any page
            if (e.Key == Key.R && (e.KeyModifiers == KeyModifiers.None || cmd && (e.KeyModifiers & ~(KeyModifiers.Meta | KeyModifiers.Control)) == 0))
            {
                RunScanFromKeyboard(vm);
                e.Handled = true;
                return;
            }
            if (!cmd || e.Key != Key.Z && e.Key != Key.Y) return;
            if (e.Key == Key.Y || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) vm.Schematic.UndoRedo.Redo();
            else vm.Schematic.UndoRedo.Undo();
            e.Handled = true;
        };
        Closed += (_, _) =>
        {
            _workBar?.Stop();
            _scanBar?.Stop();
            if (_watched is not null) _watched.PropertyChanged -= OnVmPropertyChanged;
            if (Vm is { } vm) { Open.Remove(vm.Instance); vm.Dispose(); }
        };
    }

    private PhasedArrayToolViewModel? Vm => DataContext as PhasedArrayToolViewModel;

    private PhasedArrayToolViewModel? _watched;

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (e.PropertyName == nameof(PhasedArrayToolViewModel.Frame)) StartPageWork();
        if (e.PropertyName == nameof(PhasedArrayToolViewModel.Title)) Title = vm.Title;
        if (e.PropertyName == nameof(PhasedArrayToolViewModel.Page)) { SyncPageButtons(); StartPageWork(); }
        if (e.PropertyName is nameof(PhasedArrayToolViewModel.Working) or nameof(PhasedArrayToolViewModel.WorkProgress)
                           or nameof(PhasedArrayToolViewModel.ScanWorking) or nameof(PhasedArrayToolViewModel.ScanProgress)) SyncBars();
        // the Channel page goes with the channel
        if (e.PropertyName == nameof(PhasedArrayToolViewModel.HasChannel) && !vm.HasChannel && vm.Page == ArrayPage.Channel) vm.Page = ArrayPage.Beam;
    }

    /// <summary>How long work must run before its bar appears: quick work never shows one.</summary>
    internal static readonly TimeSpan WorkBarDelay = TimeSpan.FromSeconds(0.1);

    private DelayedBar? _workBar, _scanBar;

    /// <summary>The two bars: the window's, for evaluations, failure trials and the frequency study; the Scan maps page's, beside its run
    /// button, for the scan maps.</summary>
    private void SyncBars()
    {
        if (Vm is not { } vm) return;
        (_workBar ??= new DelayedBar(WorkBar, WorkProgressBar)).Sync(vm.Working, vm.WorkProgress);
        (_scanBar ??= new DelayedBar(ScanProgressBar, ScanProgressBar)).Sync(vm.ScanWorking, vm.ScanProgress);
    }

    /// <summary>R from any page: the Scan maps page shown, and its maps computed again (unless showing the page has just started them).</summary>
    private void RunScanFromKeyboard(PhasedArrayToolViewModel vm)
    {
        // showing the page starts the maps itself when they are wanted: R then has nothing more to do
        bool showingStartsIt = vm.Page != ArrayPage.ScanMaps && ScanWanted(vm);
        vm.Page = ArrayPage.ScanMaps;
        SyncPageButtons();
        if (!showingStartsIt) vm.StartScan();
    }

    /// <summary>The Scan maps page computes its maps when shown: none yet, or the last cancelled by an edit, and none running.</summary>
    private static bool ScanWanted(PhasedArrayToolViewModel vm)
        => vm.Frame is not null && !vm.Dragging && !vm.ScanRunning
           && (vm.ScanPoints is null || vm.ScanStatus.StartsWith("Cancelled", StringComparison.Ordinal));

    /// <summary>
    /// A progress bar that appears only once its work has run for <see cref="WorkBarDelay"/>, and goes the moment the work ends. It FADES
    /// — its place in the layout is always kept — so nothing beside it moves either way. Hidden, it is not indeterminate: an indeterminate
    /// bar animates, and an animation nobody can see still redraws the window every frame.
    /// <para><b>The delay is measured, not only timed.</b> A first version waited on a <c>DispatcherTimer</c> alone, at its default
    /// Background priority — below every post the work makes, and a scan posts its progress once per direction — so the tick could be
    /// starved for the whole run and the bar never appeared. Now each progress update checks the clock itself, and the timer (at Normal
    /// priority) is only the fallback for work that reports nothing while it runs.</para>
    /// </summary>
    private sealed class DelayedBar(Control shown, ProgressBar bar)
    {
        private DispatcherTimer? _timer;
        private bool _working;
        private double _progress = double.NaN;
        private readonly System.Diagnostics.Stopwatch _since = new();

        public void Sync(bool working, double progress)
        {
            _progress = progress;
            if (!working)
            {
                _working = false;
                _since.Reset();
                Stop();
                shown.Opacity = 0;
                bar.IsIndeterminate = false;
                return;
            }
            if (!_working) { _working = true; _since.Restart(); }
            if (shown.Opacity > 0) { Show(); return; }
            if (_since.Elapsed >= WorkBarDelay) { Elapsed(); return; }
            if (_timer is not null) return;
            _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = WorkBarDelay - _since.Elapsed };
            _timer.Tick += (_, _) => Elapsed();
            _timer.Start();
        }

        /// <summary>The delay has passed: shown, if the work still runs. (A headless capture calls this itself: the headless platform
        /// never fires a dispatcher timer.)</summary>
        public void Elapsed()
        {
            Stop();
            if (!_working) return;
            shown.Opacity = 1;
            Show();
        }

        public void Stop()
        {
            _timer?.Stop();
            _timer = null;
        }

        private void Show()
        {
            bool known = double.IsFinite(_progress);
            bar.IsIndeterminate = !known;
            if (known) bar.Value = _progress;
        }
    }

    /// <summary>A page button: that page shown. A click on the page already shown leaves it shown.</summary>
    private void OnPageClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control { Tag: string tag } && Enum.TryParse<ArrayPage>(tag, out var page) && Vm is { } vm) vm.Page = page;
        SyncPageButtons();   // the page already shown: its button was just unchecked by the click, and is checked again
    }

    /// <summary>The page buttons checked as the view model's page says — exactly one.</summary>
    private void SyncPageButtons()
    {
        var page = Vm?.Page ?? ArrayPage.Beam;
        foreach (var b in PageBar.Children.OfType<ToggleButton>())
            b.IsChecked = b.Tag is string tag && tag == page.ToString();
    }

    /// <summary>
    /// A left click on another plot, a pane's background, the readouts or any other part of the window that is not a control: nothing
    /// selected, as Esc does. The aperture answers its own presses (an element toggles, its empty space and legend clear, a drag selects).
    /// A control is left alone — a press on "Switch selected off" must not clear the selection it is about to act on — and a right click
    /// never clears, so the Copy/Export menu leaves the selection as it was.
    /// </summary>
    private void OnAnyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is not { } vm || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        for (var v = e.Source as Avalonia.Visual; v is not null && !ReferenceEquals(v, this); v = Avalonia.VisualTree.VisualExtensions.GetVisualParent(v))
            if (v is ApertureCanvas or Button or ComboBox or TextBox or NumericUpDown or Slider or GridSplitter or ScrollBar) return;
        vm.ClearSelection();
    }

    /// <summary>The readouts on the clipboard, one per line, name and value separated by a tab.</summary>
    private async void OnCopyReadoutsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Vm is { } vm && Clipboard is { } clipboard) await clipboard.SetTextAsync(vm.ReadoutsText());
    }

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

    /// <summary>R-paa9-5/6 — a shown Scan maps page computes its map once a settled frame is in (an edit cancelled the last one), a shown
    /// Failures page its trials and a shown Frequency page its study, each once per frame; none runs during a drag.</summary>
    private void StartPageWork()
    {
        if (Vm is not { Frame: not null } vm || vm.Dragging) return;
        if (vm.Page == ArrayPage.ScanMaps && ScanWanted(vm)) vm.StartScan();
        if (vm.Page == ArrayPage.Failures && (vm.Failures is null || !ReferenceEquals(_failuresOf, vm.Frame)))
        {
            _failuresOf = vm.Frame;
            vm.RunFailures();
        }
        if (vm.Page == ArrayPage.Frequency && (vm.Frequency is null || !ReferenceEquals(_frequencyOf, vm.Frame)))
        {
            _frequencyOf = vm.Frame;
            vm.RunFrequency();
        }
    }

    private object? _frequencyOf, _failuresOf;

    /// <summary>The Phased Antenna Array's section of the component reference.</summary>
    private void OnHelpClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => DocLauncher.Open("reference/phased-array.html", "tool");
}
