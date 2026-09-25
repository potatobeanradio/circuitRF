using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// The layout editor's Impedance Analysis (round 8): target Z0, ± tolerance and the copper layers,
/// then Export — a save picker, then the run with a per-layer progress bar and Cancel. A cancelled
/// run still writes the report for the layers it finished (owner, 2026-09-25).
/// </summary>
/// <remarks>
/// The window only asks and reports. The run is <see cref="LayoutEditorViewModel.ExportTraceImpedanceAsync"/>,
/// which is <see cref="TraceImpedanceAnalysis"/> and the PDF composer `circuitrf impedance` also calls.
/// The target, tolerance and unticked layers are remembered for the session, so a reviewer
/// re-running after a fix does not re-type them.
/// </remarks>
public partial class TraceImpedanceAnalysisDialog : Window
{
    private static double s_target = TraceImpedanceOptions.DefaultTargetOhms;
    private static double s_tolerance = TraceImpedanceOptions.DefaultTolerancePercent;
    private static readonly HashSet<string> s_unticked = new(StringComparer.Ordinal);

    private readonly LayoutEditorViewModel? _vm;
    private readonly List<(LayoutEditorViewModel.TraceImpedanceLayerChoice Choice, CheckBox Box)> _layers = [];
    private CancellationTokenSource? _cts;
    private bool _running;

    public TraceImpedanceAnalysisDialog()
    {
        InitializeComponent();
    }

    public TraceImpedanceAnalysisDialog(LayoutEditorViewModel vm) : this()
    {
        _vm = vm;
        TargetBox.Text = s_target.ToString("0.##", CultureInfo.InvariantCulture);
        ToleranceBox.Text = s_tolerance.ToString("0.##", CultureInfo.InvariantCulture);

        foreach (var choice in vm.TraceImpedanceLayers())
        {
            var swatch = new Border
            {
                Width = 12, Height = 12, CornerRadius = new Avalonia.CornerRadius(3),
                Background = new SolidColorBrush(Color.FromRgb(choice.Color.R, choice.Color.G, choice.Color.B)),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var label = new TextBlock { Text = choice.Name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { swatch, label } };
            if (!choice.HasCopper)
                content.Children.Add(new TextBlock { Text = "no copper", FontSize = 10, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center });
            var box = new CheckBox
            {
                Content = content,
                IsChecked = choice.HasCopper && !s_unticked.Contains(choice.Name),
                IsEnabled = choice.HasCopper,
                MinHeight = 26,
            };
            box.IsCheckedChanged += (_, _) => Validate();
            LayerList.Children.Add(box);
            _layers.Add((choice, box));
        }
        if (_layers.Count == 0)
            LayerList.Children.Add(new TextBlock
            {
                Text = "This layout's technology binds no drawing layer to a conductor of its stackup.",
                FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap,
            });
        Validate();
    }

    // ── the inputs ───────────────────────────────────────────────────────────────────────────

    private static bool TryNumber(string? text, out double value) =>
        double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private void OnInputChanged(object? sender, TextChangedEventArgs e) => Validate();

    /// <summary>The pass band, and whether Export can go.</summary>
    private bool Validate()
    {
        if (BandText is null || ExportButton is null) return false;
        string? problem = null;
        if (!TryNumber(TargetBox.Text, out double target) || !(target > 0))
            problem = "Enter the target impedance in ohms, e.g. 50.";
        else if (!TryNumber(ToleranceBox.Text, out double tol) || !(tol > 0) || tol >= 100)
            problem = "Enter the tolerance as a percentage above 0 and below 100, e.g. 10.";
        else
        {
            BandText.Text = string.Create(CultureInfo.InvariantCulture,
                $"Pass band {target * (1 - tol / 100):0.0} – {target * (1 + tol / 100):0.0} Ω");
            if (!_layers.Any(l => l.Box.IsChecked == true)) problem = "Tick at least one layer.";
        }

        ValidationText.Text = problem ?? "";
        ValidationText.IsVisible = problem is not null;
        ExportButton.IsEnabled = problem is null && !_running;
        return problem is null;
    }

    private void OnAllClick(object? sender, RoutedEventArgs e)
    {
        foreach (var (choice, box) in _layers) if (choice.HasCopper) box.IsChecked = true;
    }

    private void OnNoneClick(object? sender, RoutedEventArgs e)
    {
        foreach (var (_, box) in _layers) box.IsChecked = false;
    }

    // ── the run ──────────────────────────────────────────────────────────────────────────────

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || _running || !Validate()) return;
        TryNumber(TargetBox.Text, out double target);
        TryNumber(ToleranceBox.Text, out double tolerance);
        var chosen = _layers.Where(l => l.Box.IsChecked == true).ToList();

        string suggested = _vm.CurrentLayoutPath is { Length: > 0 } p
            ? TraceImpedanceAnalysis.CellTitle(p) + " impedance.pdf" : "impedance.pdf";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Impedance Analysis",
            SuggestedFileName = suggested,
            DefaultExtension = "pdf",
            FileTypeChoices = [new FilePickerFileType("PDF report") { Patterns = ["*.pdf"] }],
        });
        if (file is null) return;

        s_target = target;
        s_tolerance = tolerance;
        s_unticked.Clear();
        foreach (var (choice, box) in _layers) if (box.IsChecked != true) s_unticked.Add(choice.Name);

        _running = true;
        _cts = new CancellationTokenSource();
        TargetCard.IsEnabled = false;
        LayersCard.IsEnabled = false;
        ExportButton.IsEnabled = false;
        ProgressCard.IsVisible = true;
        FootText.Text = "Cancel stops the run and still writes the layers that finished.";
        LayerText.Text = "Reading the copper…";
        LayerCountText.Text = "";
        StageText.Text = "";
        LayerBar.Value = 0;
        OverallBar.Value = 0;

        // Progress<T> is created on the UI thread, so every report arrives on it.
        var control = new RunControl
        {
            Token = _cts.Token,
            Total = chosen.Count,
            MinReportIntervalMs = 60,
            Progress = new Progress<RunProgress>(OnProgress),
        };

        var options = new TraceImpedanceOptions
        {
            TargetOhms = target,
            TolerancePercent = tolerance,
            Layers = [.. chosen.Select(l => l.Choice.Key)],
        };

        try
        {
            await _vm.ExportTraceImpedanceAsync(file.Path.LocalPath, options, control);
        }
        finally
        {
            _running = false;
            _cts.Dispose();
            _cts = null;
        }
        Close(true);
    }

    private static readonly Regex StageShape = new(@"^(?<layer>.*) \((?<i>\d+) of (?<n>\d+)\): (?<what>.*)$");

    private void OnProgress(RunProgress p)
    {
        if (!_running) return;
        var m = StageShape.Match(p.Stage);
        if (!m.Success)
        {
            LayerText.Text = p.Stage.Length > 0 ? p.Stage + "…" : LayerText.Text;
            return;
        }
        int i = int.Parse(m.Groups["i"].Value, CultureInfo.InvariantCulture);
        int n = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
        string what = m.Groups["what"].Value;
        double stage = what == "solving" && p.StageTotal > 0 ? (double)p.StageCompleted / p.StageTotal
                     : what == "cutting" ? 0.05 : 0;

        LayerText.Text = m.Groups["layer"].Value;
        LayerCountText.Text = $"Layer {i} of {n}";
        LayerBar.Value = stage;
        OverallBar.Value = (i - 1 + stage) / Math.Max(1, n);
        StageText.Text = what == "solving" && p.StageTotal > 0
            ? $"Solving cross-sections — {p.StageCompleted:N0} of {p.StageTotal:N0}"
            : char.ToUpperInvariant(what[0]) + what[1..] + "…";
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (_running && _cts is { } cts)
        {
            if (cts.IsCancellationRequested) return;
            cts.Cancel();
            CancelButton.IsEnabled = false;
            StageText.Text = "Cancelling — writing the report for the layers that finished…";
            return;
        }
        Close(false);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Closing the window mid-run is a Cancel: the run finishes its current point, writes what it
        // has, and the dialog closes itself when that is done.
        if (_running)
        {
            e.Cancel = true;
            OnCancelClick(this, new RoutedEventArgs());
        }
        base.OnClosing(e);
    }
}
