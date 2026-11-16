using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Views.DataDisplay;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>
/// The Transient page's figures (brief TRN-9 R-trn9-1, gate 2): each a run of one of the shipped transient examples' benches, at
/// its own settings, drawn by the Transient preset (<see cref="TransientPreset"/>) the toolbar and the Messages panel offer.
///
/// <para><b>Composed, not hand-built.</b> Every plot is the preset's own configuration, pasted as a <c>.cdd</c> opens; the
/// waveform figures keep only the preset traces they are about and, where the start-up is not the subject, window the time
/// axis — the edits a reader makes on the trace card and the axes. The benches run in about a second between them on the
/// native engine (none of these is an ngspice bench, so no figure depends on what is installed), and are run on every
/// regeneration rather than committed, as every other Data Display figure's data is (<see cref="DocRunData"/>).</para>
/// </summary>
public static partial class DocDataDisplayFixtures
{
    private static readonly Dictionary<string, (string Id, DataSet Data)> _transient = new(StringComparer.Ordinal);

    /// <summary>One bench of a shipped transient example, run as Simulate runs it, its result written to the docs results root.</summary>
    private static (string Id, DataSet Data) TransientResult(string workspace, string cell)
    {
        if (_transient.TryGetValue(cell, out var done)) return done;
        string root = ExampleWorkspaces.ResolveRoot()
            ?? throw new InvalidOperationException("No examples/ tree beside the generator or above it, so the Transient figures have no design to run.");
        string csch = Path.Combine(root, workspace, cell, "schematic", cell + ".csch");

        var circuit = PreparedCircuit.FromSchematic(csch);
        if (circuit.ReadError is { } error)
            throw new InvalidOperationException($"The Transient figures could not read '{csch}': {error}");
        var plan = CircuitEvaluation.Plan(circuit, new CircuitEvaluationRequest { Measurements = true });
        var result = CircuitEvaluation.Execute(plan, null, true, [], null);
        if (result.Status != RunStatus.Success || result.GroupedResults is not { } ds || !TransientPreset.Available(ds))
            throw new InvalidOperationException(
                $"The Transient figures' run of '{csch}' produced no transient result ({result.StatusMessage}), so its figure would be empty.");

        Directory.CreateDirectory(DocRunData.ResultsRoot);
        string file = cell + ".npy";
        string path = Path.Combine(DocRunData.ResultsRoot, file);
        if (File.Exists(path)) File.Delete(path);
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        return _transient[cell] = (file, ds);
    }

    /// <summary>
    /// A document holding the plots <paramref name="choose"/> picks from the preset's own configuration of the bench's result,
    /// narrowed as it likes (the result is passed for a plot that needs an index read from it).
    /// </summary>
    private static (DataDisplayDocumentViewModel Doc, PlotContainerViewModel[] Plots) TransientPlots(
        string workspace, string cell, Func<IReadOnlyList<PlotContainerConfig>, DataSet, List<PlotContainerConfig>> choose)
    {
        var (id, ds) = TransientResult(workspace, cell);
        var vm = Sourced(id);
        var display = vm.Window.DataDisplay
            ?? throw new InvalidOperationException("The Data Display document has no active tab.");
        foreach (var seeded in display.Plots.ToList()) display.RemovePlot(seeded);

        var picked = choose(TransientPreset.Build(ds, id), ds);
        if (picked.Count == 0)
            throw new InvalidOperationException($"The Transient preset gave '{cell}' no plot to draw, so its figure would be empty.");
        var paste = display.PasteFromConfigAsync(new DataDisplayConfig { Plots = picked });
        Await(paste);
        return (vm, [.. paste.Result]);
    }

    /// <summary>Keeps only the traces of a waveform plot labelled <paramref name="labels"/>, in that order.</summary>
    private static void KeepTraces(PlotContainerConfig pc, params string[] labels)
    {
        var kept = labels.Select(l => pc.Traces.FirstOrDefault(t => t.Label == l)
            ?? throw new InvalidOperationException($"The Transient preset drew no trace labelled '{l}'.")).ToList();
        pc.Traces = kept;
    }

    /// <summary>A trace of IProbe <paramref name="probe"/>'s current against time, in the preset's own form for a voltage.</summary>
    private static TraceConfig ProbeCurrent(PlotContainerConfig waveforms, DataSet ds, string probe, int colorIndex)
    {
        var like = waveforms.Traces[0];
        string group = TransientPreset.GroupOf(ds)!;
        var labels = ds.CubesIn(group)["I"].Axes[1].Labels ?? [];
        int index = Array.IndexOf(labels, probe);
        if (index < 0) throw new InvalidOperationException($"The transient result records no IProbe '{probe}'.");
        return new TraceConfig
        {
            SourcePath = like.SourcePath, CubeName = like.CubeName![..^1] + "I", Label = $"I({probe})",
            CubeSlice =
            [
                new AxisSliceConfig { AxisName = "time", Role = AxisRole.KeepAsX },
                new AxisSliceConfig { AxisName = "branch", Index = index, Label = probe },
            ],
            Properties = new TracePropertiesConfig { LineColorIndex = colorIndex, MarkerColorIndex = colorIndex },
        };
    }

    /// <summary>The plots, kept where the preset placed them relative to each other, centred together in the canvas.</summary>
    private static Action<Control> CentredGroup(PlotContainerViewModel[] plots) => root =>
    {
        var canvas = root.GetVisualDescendants().OfType<ItemsControl>().FirstOrDefault(c => c.Name == "PlotCanvas")
            ?? throw new InvalidOperationException("The Data Display view no longer has a control named 'PlotCanvas'.");
        double w = canvas.Bounds.Width, h = canvas.Bounds.Height;
        double left = plots.Min(p => p.ViewContainerLeft), right = plots.Max(p => p.ViewContainerLeft + p.ViewTotalWidth);
        double top = plots.Min(p => p.ViewTop), bottom = plots.Max(p => p.ViewTop + p.ViewHeight);
        double dx = (w - (right - left)) / 2.0 - left, dy = (h - (bottom - top)) / 2.0 - top;
        foreach (var p in plots)
        {
            double zoom = p.ZoomLevel > 0 ? p.ZoomLevel : 1.0;
            p.Left += Math.Round(dx / zoom);
            p.Top  += Math.Round(dy / zoom);
        }
    };

    private static FigureScene TransientScene(string title, (DataDisplayDocumentViewModel Doc, PlotContainerViewModel[] Plots) built,
                                              bool group = false)
        => new(new DataDisplayView { DataContext = Document(built.Doc, title) })
            { AfterLayout = group ? CentredGroup(built.Plots) : built.Plots.Length == 1 ? Centred(built.Plots[0]) : Row(built.Plots) };

    /// <summary>
    /// The Transient preset on the Pulse Response example's <c>Filter_Native</c>: the step at the filter's input and output and
    /// the bench's measures — rise time, overshoot, settling time and final value. A step has no period, so the preset draws no
    /// spectrum.
    /// </summary>
    public static FigureScene TransientFilterStep()
        => TransientScene("Filter_Native", TransientPlots("Pulse Response", "Filter_Native", (preset, _) =>
        {
            KeepTraces(preset[0], "in", "out");
            return [.. preset];
        }), group: true);

    /// <summary>The Pulse Response example's <c>Line_Native</c>: the step leaving, arriving 1 ns later, and its reflection.</summary>
    public static FigureScene TransientLineReflection()
        => TransientScene("Line_Native", TransientPlots("Pulse Response", "Line_Native", (preset, _) =>
        {
            var pc = preset[0];
            KeepTraces(pc, "s", "in", "out");
            (pc.Width, pc.Height) = (700, 400);
            return [pc];
        }));

    /// <summary>The Buck Converter example's <c>Startup</c>: the output overshooting and settling, beside the inductor current.</summary>
    public static FigureScene TransientBuckStartup()
        => TransientScene("Startup", TransientPlots("Buck Converter", "Startup", (preset, ds) =>
        {
            var v = preset[0];
            var current = new PlotContainerConfig
            {
                PlotType = PlotType.Rect, Width = 560, Height = 400,
                CustomTitle = "Inductor current", CustomTitleOn = true,
                CustomXLabel = "Time (s)", CustomXLabelOn = true,
                CustomYLabel = "Current (A)", CustomYLabelOn = true,
                Legend = LegendPlacement.Below,
                Traces = [ProbeCurrent(v, ds, "IL", TraceProperties.LineColorOrder[1])],
            };
            KeepTraces(v, "out");
            v.CustomTitle = "Output voltage";
            return [v, current];
        }));

    /// <summary>
    /// The whole Transient preset on the Class-E example's <c>ClassE_Transient</c>: the drain and the output over the last three
    /// periods (the drain swinging up while the switch is open and back to zero before it closes), the power through
    /// <c>IOUT</c> at each harmonic over the last period, and the bench's measures.
    /// </summary>
    public static FigureScene TransientPresetClassE()
        => TransientScene("ClassE_Transient", TransientPlots("Class-E with a Transient Seed", "ClassE_Transient", (preset, _) =>
        {
            var pc = preset[0];
            KeepTraces(pc, "d", "out");
            pc.CustomTitle = "Drain and output, last three periods";
            pc.Axes = new AxesConfig { AutoscaleX = false, AutoscaleY = false, WindowX = 57e-9, WindowWidth = 3e-9, WindowY = -8, WindowHeight = 28 };
            return [.. preset];
        }), group: true);
}

/// <summary>
/// The transient analysis card (brief TRN-9 R-trn9-2), as the analysis editor hosts it, on the Pulse Response example's
/// <c>Filter_ngspice</c> settings with its More section open so the ngspice-only keys are seen.
///
/// <para><b>Discovery is pinned to ngspice 47 for the capture and put back afterwards.</b> The card enables its ngspice choice
/// only while discovery finds one (TRN-7 R-trn7-7), so a figure that asked the machine would draw a greyed choice on one computer
/// and an enabled one on the next — a figure that measures the machine.</para>
/// </summary>
public static class DocTransientFixtures
{
    public static FigureScene AnalysisCard()
    {
        var before = CircuitRF.Ui.ViewModels.NgspiceAvailability.Current;
        CircuitRF.Ui.ViewModels.NgspiceAvailability.Set(new CircuitRF.Engine.Transient.NgspiceProgram("/usr/local/bin/ngspice", "47"));
        var view = new CircuitRF.Ui.Views.Analyses.TranBodyView
        {
            DataContext = CircuitRF.Ui.ViewModels.TranBodyViewModel.FromAnalysis(new CircuitRF.Core.Design.TransientAnalysis("TR1")
            {
                StopExpr = "10n", StepExpr = "5p", Solver = CircuitRF.Core.Design.TransientAnalysis.SolverNgspice,
            }),
        };
        return new FigureScene(view)
        {
            AfterLayout = root =>
            {
                foreach (var e in root.GetVisualDescendants().OfType<Expander>()) e.IsExpanded = true;
            },
            Cleanup = () => CircuitRF.Ui.ViewModels.NgspiceAvailability.Set(before),
        };
    }
}
