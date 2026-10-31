using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CircuitRF.Diagnostics;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RfCore;
using RfCore.Data;
using SkiaSharp;

namespace CircuitRF.Ui.SParameters;

/// <summary>One line of the file pane: a label and what the file states.</summary>
public sealed record SParameterFactRow(string Label, string Value)
{
    /// <summary>The warning mark, in text presentation so a yellow foreground colours it.</summary>
    public const string WarningGlyph = "⚠\uFE0E";

    /// <summary>True when this line carries the warning mark — a DC point the file does not have.</summary>
    public bool Warn => Value.Contains('⚠');

    /// <summary>The value with the warning mark taken off; the mark is drawn beside it, in yellow.</summary>
    public string Text => Warn ? Value.Replace("⚠", "", StringComparison.Ordinal).TrimEnd() : Value;
}

/// <summary>One trace name beside its plot: the colour it is drawn in, and the name the plot would have given it.</summary>
public sealed record SParameterLegendEntry(string Label, SKColor Color);

/// <summary>One line of the term list: up to <see cref="SParameterViewerViewModel.TermColumns"/> terms of one matrix row.</summary>
public sealed record SParameterTermLine(IReadOnlyList<SParameterTermViewModel> Cells);

/// <summary>One term of the matrix, ticked or not.</summary>
public sealed partial class SParameterTermViewModel : ObservableObject
{
    private readonly Action _changed;

    public SParameterTermViewModel(SParameterViewerPreset.Term term, string name, bool isChecked, Action changed)
    {
        Term = term;
        Name = name;
        _isChecked = isChecked;
        _changed = changed;
    }

    public SParameterViewerPreset.Term Term { get; }

    public string Name { get; }

    public bool IsReflection => Term.IsReflection;

    private bool _isChecked;
    private bool _quiet;

    /// <summary>Ticked: drawn. Every change redraws, except one made by <see cref="SetQuietly"/>.</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (!SetProperty(ref _isChecked, value) || _quiet) return;
            _changed();
        }
    }

    /// <summary>The colour the term is drawn in, while it is drawn.</summary>
    [ObservableProperty] private SKColor _color = SKColors.Transparent;

    public string Tooltip => IsReflection
        ? $"{Name}: the reflection at port {Term.Row + 1}. Drawn on the Smith chart and on magnitude and phase."
        : $"{Name}: port {Term.Col + 1} to port {Term.Row + 1}. Drawn on the polar chart and on magnitude and phase.";

    /// <summary>Sets the tick without asking for a redraw — for the bulk choices, which redraw once.</summary>
    internal void SetQuietly(bool value)
    {
        _quiet = true;
        try { IsChecked = value; }
        finally { _quiet = false; }
    }
}

/// <summary>
/// The S-Parameter Viewer: one Touchstone file, what it states about itself, and its reflections on a
/// Smith chart with every chosen term in dB and in phase against frequency.
/// </summary>
/// <remarks>
/// <b>It owns no reading, rule or plot.</b> The file is read through <see cref="TouchstoneSource"/>, the
/// facts and findings are <see cref="TouchstoneFacts"/>' and the plots are <see cref="SParameterViewerPreset"/>'s,
/// loaded through <see cref="PlotConfigLoader"/> — the same traces and axes <c>circuitrf snp</c> draws.
/// The window hosts them stacked and puts each plot's names beside it; the picture and the saved
/// Data Display keep the preset's titles and legend.
///
/// <para><b>The plots are hosted as the Signal Designer's are</b>: one <see cref="DataDisplayViewModel"/>
/// with three containers, so markers, their info boxes, zoom and copy all work as in a Data Display. It is
/// not a Data Display document; Save as Data Display writes one.</para>
/// </remarks>
public sealed partial class SParameterViewerViewModel : ObservableObject
{
    public const string ToolName = "S-Parameter Viewer";

    private TouchstoneSource? _source;

    public SParameterViewerViewModel()
    {
        SmithContainer     = PlotHost.AddPlot(PlotType.Smith, FreqUnit.GHz, left: 0, top: 0, width: 420, height: 420);
        PolarContainer     = PlotHost.AddPlot(PlotType.Polar, FreqUnit.GHz, left: 0, top: 420, width: 420, height: 420);
        MagnitudeContainer = PlotHost.AddPlot(PlotType.Rect,  FreqUnit.GHz, left: 0, top: 840, width: 520, height: 321);
        PhaseContainer     = PlotHost.AddPlot(PlotType.Rect,  FreqUnit.GHz, left: 0, top: 1161, width: 520, height: 321);
        PlotHost.SelectOnly((PlotContainerViewModel?)null);
        Status = "Open a Touchstone file (.s1p … .sNp) to view it.";
    }

    // ── the plots ─────────────────────────────────────────────────────────────

    public DataDisplayViewModel PlotHost { get; } = new(new DataSourceLibraryViewModel(), addEmptyPlot: false, selectEmptyPlot: false);
    public PlotContainerViewModel SmithContainer { get; }
    public PlotContainerViewModel PolarContainer { get; }
    public PlotContainerViewModel MagnitudeContainer { get; }
    public PlotContainerViewModel PhaseContainer { get; }
    public Plot SmithPlot => SmithContainer.PlotVM.Plot;
    public Plot PolarPlot => PolarContainer.PlotVM.Plot;
    public Plot MagnitudePlot => MagnitudeContainer.PlotVM.Plot;
    public Plot PhasePlot => PhaseContainer.PlotVM.Plot;

    /// <summary>Raised after the four plots' traces were replaced, so the window repaints them.</summary>
    public event EventHandler? PlotsUpdated;

    // ── the window's seams ────────────────────────────────────────────────────

    /// <summary>Asks for a Touchstone file to open. Set by the window, which owns the picker.</summary>
    public Func<Task<string?>>? PickFileAsync { get; set; }

    /// <summary>Asks where to save a Data Display, given a suggested file name.</summary>
    public Func<string, Task<string?>>? PickDisplayPathAsync { get; set; }

    /// <summary>Opens a written <c>.cdd</c> in a workspace window, when the viewer was opened from one.</summary>
    public Action<string>? OpenDataDisplay { get; set; }

    /// <summary>Asks a yes-or-no question, given its heading and its sentence; true is yes. Set by the window.
    /// Unset, nothing is asked and the answer is yes.</summary>
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    // ── state ─────────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(FileName), nameof(HasFile), nameof(IsEmpty))]
    [NotifyCanExecuteChangedFor(nameof(ReloadCommand), nameof(SaveAsDataDisplayCommand))]
    private string? _filePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    [NotifyCanExecuteChangedFor(nameof(OpenFileCommand), nameof(ReloadCommand), nameof(SaveAsDataDisplayCommand))]
    private bool _isLoading;

    /// <summary>Why the file could not be read; null while it could.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(IsEmpty))]
    private string? _errorText;

    [ObservableProperty] private string _status = "";

    /// <summary>The open file has no sample at 0 Hz. The file pane and the status bar both warn.</summary>
    [ObservableProperty] private bool _dcPointMissing;

    /// <summary>
    /// Which plots the toolbar has on. The window opens on the Smith chart and magnitude; polar and
    /// phase are a click away. Every one of them can be off, and then the pane is empty.
    /// </summary>
    [ObservableProperty] private bool _showSmith = true;
    [ObservableProperty] private bool _showPolar;
    [ObservableProperty] private bool _showMagnitude = true;
    [ObservableProperty] private bool _showPhase;

    /// <summary>The window title and its Window-menu entry.</summary>
    public string Title => FileName is { } name ? $"{ToolName} — {name}" : ToolName;

    public string? FileName => FilePath is null ? null : Path.GetFileName(FilePath);

    /// <summary>A file is loaded and drawn.</summary>
    public bool HasFile => FilePath is not null && _source is not null;

    public bool HasError => ErrorText is not null;

    /// <summary>Nothing opened yet: the empty state's invitation shows.</summary>
    public bool IsEmpty => !HasFile && !HasError && !IsLoading;

    public ObservableCollection<SParameterFactRow> Facts { get; } = [];

    /// <summary>The file's defects, as their sentences.</summary>
    public ObservableCollection<string> Findings { get; } = [];

    public bool HasFindings => Findings.Count > 0;

    public ObservableCollection<SParameterTermViewModel> Terms { get; } = [];

    /// <summary>Trace names drawn beside each plot. Empty past <see cref="SParameterViewerPreset.LegendUpToTerms"/>,
    /// where the list would take the width the curves need; the tick boxes still carry the colours.</summary>
    public ObservableCollection<SParameterLegendEntry> SmithLegend { get; } = [];
    public ObservableCollection<SParameterLegendEntry> PolarLegend { get; } = [];
    public ObservableCollection<SParameterLegendEntry> MagnitudeLegend { get; } = [];
    public ObservableCollection<SParameterLegendEntry> PhaseLegend { get; } = [];

    /// <summary>
    /// The tick boxes as the list shows them: each row of the matrix cut into lines of <see cref="TermColumns"/>.
    /// The list builds only the lines in view, so a file of hundreds of ports — tens of thousands of terms —
    /// opens as quickly as a two-port. Replaced whole rather than filled a line at a time: twenty thousand
    /// separate additions to a bound list are each a layout pass.
    /// </summary>
    [ObservableProperty] private IReadOnlyList<SParameterTermLine> _termLines = [];

    /// <summary>How many tick boxes a line of the term list holds: one per port up to four, then as many of the
    /// longer names as the pane has room for.</summary>
    [ObservableProperty] private int _termColumns = 2;

    /// <summary>Tick boxes to a line for a file of <paramref name="ports"/>: the names lengthen at ten ports
    /// (<c>S(10,1)</c>) and again at a hundred.</summary>
    public static int TermColumnsFor(int ports)
        => ports <= SParameterViewerPreset.AllTermsUpToPorts ? Math.Max(1, ports)
         : ports < 10  ? 4
         : ports < 100 ? 3
         :               2;

    // ── loading ───────────────────────────────────────────────────────────────

    /// <summary>Reads <paramref name="path"/> and draws it. The read runs off the UI thread.</summary>
    public async Task LoadAsync(string path)
    {
        string full = Path.GetFullPath(path);
        IsLoading = true;
        Status = $"Reading {Path.GetFileName(full)}…";
        var (source, refusal) = await Task.Run(() => (TouchstoneSource.Read(full, out var why), why));
        IsLoading = false;
        Show(full, source, refusal);
    }

    /// <summary>The synchronous half of <see cref="LoadAsync"/>: puts a read file — or its refusal — on show.</summary>
    internal void Show(string full, TouchstoneSource? source, Diagnostic? refusal)
    {
        var keep = _source is not null && string.Equals(_source.Path, full, StringComparison.OrdinalIgnoreCase)
                 ? Selection() : null;

        _source = source;
        FilePath = full;
        Facts.Clear();
        Findings.Clear();
        Terms.Clear();
        TermLines = [];

        if (source is null)
        {
            ErrorText = refusal?.Render() ?? $"'{Path.GetFileName(full)}' could not be read.";
            Status = "Not shown: the file could not be read.";
            DcPointMissing = false;
            ClearPlots();
            OnPropertyChanged(nameof(HasFile));
            OnPropertyChanged(nameof(HasFindings));
            return;
        }

        ErrorText = null;
        var f = source.Facts;
        DcPointMissing = !f.HasDcPoint;
        foreach (var (label, value) in SParameterViewerPreset.FileFacts(f))
            Facts.Add(new SParameterFactRow(label, value));
        foreach (var d in source.Findings) Findings.Add(d.Render());

        var chosen = (keep ?? SParameterViewerPreset.DefaultSelection(f.Ports)).ToHashSet();
        foreach (var t in SParameterViewerPreset.AllTerms(f.Ports))
            Terms.Add(new SParameterTermViewModel(t, SParameterViewerPreset.Name(t, f.Ports), chosen.Contains(t), Redraw));
        TermColumns = TermColumnsFor(f.Ports);
        var lines = new List<SParameterTermLine>();
        for (int row = 0; row < f.Ports; row++)
            for (int col = 0; col < f.Ports; col += TermColumns)
            {
                int n = Math.Min(TermColumns, f.Ports - col);
                var cells = new SParameterTermViewModel[n];
                for (int k = 0; k < n; k++) cells[k] = Terms[row * f.Ports + col + k];
                lines.Add(new SParameterTermLine(cells));
            }
        TermLines = lines;

        OnPropertyChanged(nameof(HasFile));
        OnPropertyChanged(nameof(HasFindings));
        Redraw();
    }

    // ── the terms ─────────────────────────────────────────────────────────────

    /// <summary>The ticked terms, row-major.</summary>
    public IReadOnlyList<SParameterViewerPreset.Term> Selection() => [.. Terms.Where(t => t.IsChecked).Select(t => t.Term)];

    /// <summary>All draws every term without asking up to this many — eight ports' worth. Past it All asks
    /// first: a file of hundreds of ports has tens of thousands of terms, and drawing them all on every plot
    /// holds the window for as long as that takes.</summary>
    public const int AllWithoutAskingUpTo = 64;

    [RelayCommand]
    private async Task SelectAll()
    {
        if (Terms.Count > AllWithoutAskingUpTo && ConfirmAsync is not null && _source is not null
            && !await ConfirmAsync($"Draw all {Terms.Count:N0} terms?",
                                   $"This file has {_source.Facts.Ports} ports. Drawing every term puts {Terms.Count:N0} curves " +
                                   "on each plot, and can take a long time."))
            return;
        Choose(_ => true);
    }

    [RelayCommand] private void SelectReflections() => Choose(t => t.IsReflection);
    [RelayCommand] private void SelectNone()        => Choose(_ => false);

    [RelayCommand]
    private void SelectDefault()
    {
        if (_source is null) return;
        var d = SParameterViewerPreset.DefaultSelection(_source.Facts.Ports).ToHashSet();
        Choose(t => d.Contains(t.Term));
    }

    private void Choose(Func<SParameterTermViewModel, bool> pick)
    {
        foreach (var t in Terms) t.SetQuietly(pick(t));
        Redraw();
    }

    // ── drawing ───────────────────────────────────────────────────────────────

    /// <summary>Refills the four plots from the preset, keeping every marker on its term.</summary>
    public void Redraw()
    {
        if (_source is null) return;
        var selection = Selection();
        foreach (var row in Terms)
            row.Color = row.IsChecked
                ? TraceProperties.ColorLUT[SParameterViewerPreset.ColorIndexOf(row.Term, selection)]
                : SKColors.Transparent;

        foreach (var composed in SParameterViewerPreset.Compose(_source, selection))
            Refill(ContainerOf(composed.Kind), composed.Config, _source);
        FillLegend(SmithLegend, SmithPlot);
        FillLegend(PolarLegend, PolarPlot);
        FillLegend(MagnitudeLegend, MagnitudePlot);
        FillLegend(PhaseLegend, PhasePlot);

        int traces = selection.Count;
        Status = selection.Count == 0
            ? "No terms ticked: tick one under Parameters to draw it."
            : $"{_source.Facts.FrequencyCount} points, {SParameterViewerPreset.Hz(_source.Facts.FirstFrequencyHz)} to {SParameterViewerPreset.Hz(_source.Facts.LastFrequencyHz)}"
              + $" · {traces} of {Terms.Count} term(s) drawn" + (Findings.Count > 0 ? $" · {Findings.Count} finding(s)" : " · no defects found");
        PlotsUpdated?.Invoke(this, EventArgs.Empty);
    }

    private PlotContainerViewModel ContainerOf(SParameterViewerPreset.PlotKind kind) => kind switch
    {
        SParameterViewerPreset.PlotKind.Smith     => SmithContainer,
        SParameterViewerPreset.PlotKind.Polar     => PolarContainer,
        SParameterViewerPreset.PlotKind.Magnitude => MagnitudeContainer,
        _                                         => PhaseContainer,
    };

    private static void Refill(PlotContainerViewModel container, PlotContainerConfig pc, TouchstoneSource source)
    {
        var plot = container.PlotVM.Plot;
        var kept = new Dictionary<(int, int), List<Marker>>();
        foreach (var t in plot.Traces.Where(t => t.Markers.Count > 0))
            (kept.TryGetValue((t.Row, t.Col), out var list) ? list : kept[(t.Row, t.Col)] = []).AddRange(t.Markers);

        plot.Traces.Clear();
        if (plot.FreqUnits != pc.FreqUnit) plot.FreqUnits = pc.FreqUnit;
        plot.CustomTitle    = pc.CustomTitle;
        plot.CustomTitleOn  = pc.CustomTitleOn;
        plot.CustomYLabel   = pc.CustomYLabel;
        plot.CustomYLabelOn = pc.CustomYLabelOn;
        plot.Legend         = pc.Legend;

        foreach (var tc in pc.Traces)
        {
            if (PlotConfigLoader.LoadTrace(tc, pc.PlotType, pc.FreqUnit, source) is not { } trace) continue;
            if (kept.Remove((trace.Row, trace.Col), out var markers)) trace.Markers.AddRange(markers);
            plot.Traces.Add(trace);
        }
        PlotConfigLoader.RestoreAxes(plot, pc.Axes);
        PresentInPane(plot);
        container.OnPlotChanged(container, EventArgs.Empty);
        container.RequestPlotRedraw();
    }

    /// <summary>
    /// Hosts <paramref name="plot"/> in a fixed row. A Data Display grows a Smith canvas so the title and the
    /// frequency rows under the disc have somewhere to go; this row cannot grow, and that extra used to paint
    /// past the row — over the window, and over whichever plots were turned on below it. The names sit beside
    /// the plot instead, and a rectangular plot gives the band its title took back to the curves.
    /// </summary>
    private static void PresentInPane(Plot plot)
    {
        plot.CustomTitleOn = false;
        plot.Legend = LegendPlacement.Off;
        if (plot.PlotType is PlotType.Smith or PlotType.Polar)
        {
            // The page's one frequency row and its one strip of names are the saved display's; here the names
            // sit beside the plot and the row has nowhere to go.
            plot.CustomXLabelOn = false;
            plot.CustomYLabelOn = false;
            foreach (var t in plot.Traces) t.ExcludeFromAxisLabels = true;
            return;
        }
        if (plot.PlotType != PlotType.Rect) return;
        var v = plot.Axes.Viewport;
        const double top = 0.012;
        if (v.Y > top)
            plot.Axes.Viewport = new PlotRect(v.X, top, v.Width, v.Height + (v.Y - top));
    }

    /// <summary>The names <see cref="LegendRenderer"/> would have put in the plot, listed beside it instead.</summary>
    private static void FillLegend(ObservableCollection<SParameterLegendEntry> target, Plot plot)
    {
        target.Clear();
        if (plot.Traces.Count == 0 || plot.Traces.Count > SParameterViewerPreset.LegendUpToTerms) return;
        var names = TraceLabeler.ComputeMinimalLabels(plot.Traces);
        for (int i = 0; i < plot.Traces.Count; i++)
        {
            var t = plot.Traces[i];
            string name = i < names.Count ? names[i] : t.ShortDescription;
            target.Add(new SParameterLegendEntry(
                plot.PlotType == PlotType.Rect ? t.RectYLabel(name, false) : name,
                t.Properties.LineColor));
        }
    }

    private void ClearPlots()
    {
        foreach (var c in new[] { SmithContainer, PolarContainer, MagnitudeContainer, PhaseContainer })
        {
            c.PlotVM.Plot.Traces.Clear();
            c.OnPlotChanged(c, EventArgs.Empty);
            c.RequestPlotRedraw();
        }
        SmithLegend.Clear();
        PolarLegend.Clear();
        MagnitudeLegend.Clear();
        PhaseLegend.Clear();
        PlotsUpdated?.Invoke(this, EventArgs.Empty);
    }

    // ── commands ──────────────────────────────────────────────────────────────

    private bool CanOpen() => !IsLoading;

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenFile()
    {
        if (PickFileAsync is null || await PickFileAsync() is not { } path) return;
        await LoadAsync(path);
    }

    /// <summary>
    /// Set when the viewer was opened as a placed SnP component's Preview: makes a file dropped on the window
    /// that component's File, as one undoable schematic edit, and returns the sentence saying so (null when
    /// nothing changed). Null on a viewer opened any other way, where a drop only shows the file.
    /// </summary>
    public Func<string, string?>? SetComponentFile { get; set; }

    /// <summary>
    /// A Touchstone file dropped on the window: shown as Open… would show it, and — on a component's Preview —
    /// made that component's File. Only a file that READ is handed on: a refused one stays on show with its
    /// reason, and the component keeps the file it had.
    /// </summary>
    public async Task DropFileAsync(string path)
    {
        if (IsLoading) return;
        await LoadAsync(path);
        if (HasFile && SetComponentFile is { } set && FilePath is { } full && set(full) is { } said) Status = said;
    }

    /// <summary>The file-manager wording for the path link.</summary>
    public string RevealTip => FileReveal.Label;

    /// <summary>Shows the open file in the system file manager.</summary>
    [RelayCommand]
    private void RevealFile()
    {
        if (FilePath is not { } path) return;
        if (!File.Exists(path))
        {
            Status = "That file is no longer there.";
            return;
        }
        FileReveal.Reveal(path, ex => Status = ex.Message);
    }

    private bool CanReload() => FilePath is not null && !IsLoading;

    /// <summary>Reads the file again — after it was rewritten by a measurement or a run.</summary>
    [RelayCommand(CanExecute = nameof(CanReload))]
    private Task Reload() => LoadAsync(FilePath!);

    private bool CanSave() => _source is not null && !IsLoading;

    /// <summary>
    /// Writes these plots as a Data Display (<c>.cdd</c>) — the document <c>circuitrf snp -o x.cdd</c>
    /// writes — and opens it in the workspace the viewer came from, if any.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsDataDisplay()
    {
        if (_source is null || PickDisplayPathAsync is null) return;
        if (await PickDisplayPathAsync(Path.GetFileNameWithoutExtension(_source.Path) + ".cdd") is not { } target) return;
        var config = SParameterViewerPreset.Build(_source, Selection());
        try
        {
            await File.WriteAllTextAsync(target, JsonSerializer.Serialize(config, DataDisplayJson.Options),
                                         new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Not saved: {ex.Message}";
            return;
        }
        Status = $"Saved {Path.GetFileName(target)}.";
        OpenDataDisplay?.Invoke(target);
    }

    /// <summary>Removes every selected marker, as Delete does in a Data Display; the four plots stay.</summary>
    [RelayCommand]
    public void DeleteSelectedMarkers()
    {
        foreach (var box in PlotHost.MarkerInfoBoxes.Where(b => b.IsSelected).ToList())
            box.Container.RemoveMarkerWithUndo(box.Marker, box.Trace);
    }
}
