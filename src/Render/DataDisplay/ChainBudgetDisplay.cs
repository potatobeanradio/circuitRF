// ================================================================
//  ChainBudgetDisplay.cs  —  the Data Displays a chain's Transfer to Schematic writes beside its benches
//
//  One per bench, named after it, so Simulate opens it on the bench's own result (RunResultsWriter.AutoDisplayCandidates
//  finds an authored display beside the bench first). Each is Table plots: the chain budget's prediction, the simulated
//  value and their difference side by side, then the same per stage. 1-Tone: gain, NF and compression, and each stage's
//  NF. 2-Tone: gain and the intercepts, and each stage's gain and OIP3. Envelope: SNR, EVM, output power and noise, and
//  the output spectrum. Steering: the array pattern against the arriving angle for each steer angle, over the closed-form
//  array factor, and the gain, NF and array gain at each beam's peak. Combiners: the four combiners side by side. Every
//  column is a measurement the bench's MEAS block computes (ChainBenches), so nothing here computes a number.
// ================================================================

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Chain;

namespace CircuitRF.Render.DataDisplay;

public static class ChainBudgetDisplay
{
    private const double Col = 132, Head = 24, Gap = 18, H = 64;

    /// <summary>Writes each bench's display beside it and returns their paths.</summary>
    public static IReadOnlyList<string> WriteAll(string dir, string chain, ChainDesign design, ChainBudgetResult budget)
    {
        var json = new JsonSerializerOptions(DataDisplayJson.Options) { WriteIndented = true };
        var paths = new List<string>();
        foreach (var b in ChainBenches.BenchesFor(design))
        {
            string path = Path.Combine(dir, ChainBenches.CellName(chain, b) + ".cdd");
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(Build(chain, b, design, budget), json));
            paths.Add(path);
        }
        return paths;
    }

    public static DataDisplayConfig Build(string chain, ChainBench bench, ChainDesign design, ChainBudgetResult budget)
    {
        string src = ChainBenches.CellName(chain, bench) + ".npy";
        if (bench is ChainBench.Steering or ChainBench.Combiners)
            return Display(chain, bench, src, bench == ChainBench.Steering ? Steering(src) : Combiners(src, design));
        double bocc = ChainBenches.Carrier(design.BandwidthHz).OccupiedHz / 1e6;
        var (rows, perStage) = bench switch
        {
            ChainBench.OneTone => (new (string, string)[] { ("Gain, dB", "G"), ("Noise figure, dB", "NF"), ("Input P1dB, dBm", "IP1dB"), ("Output P1dB, dBm", "OP1dB") },
                                   new (string, string)[] { ("NF", "NF, dB") }),
            ChainBench.TwoTone => (new (string, string)[] { ("Gain, dB", "G"), ("IIP3, dBm", "IIP3"), ("OIP3, dBm", "OIP3") },
                                   new (string, string)[] { ("G", "gain, dB"), ("OIP3", "OIP3, dBm") }),
            _ => (new (string, string)[] { ($"SNR in {bocc:0.##} MHz, dB", "SNR"), ("Output power, dBm", "Pout"), ($"Output noise in {bocc:0.##} MHz, dBm", "Nout") },
                  new (string, string)[0]),
        };

        var plots = new List<PlotContainerConfig>();
        double y = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            var (title, name) = rows[i];
            // A table plot draws no title, so the first column's header says what the table holds.
            string unit = title[(title.LastIndexOf(',') + 1)..].Trim();
            var p = Table(title, src, [($"{name}_calc", $"{name} budget, {unit}"), ($"{name}_sim", "simulated"), ($"{name}_err", "difference")]);
            p.Left = (i % 2) * (p.Width + Gap);
            p.Top = (i / 2) * (H + Gap);
            plots.Add(p);
            y = p.Top + H + Gap;
        }
        if (bench == ChainBench.Envelope)
        {
            var evm = Table("EVM, its noise part, %", src, [("EVM_calc", "EVM noise %: budget"), ("EVM_sim", "simulated")]);
            evm.Left = (rows.Length % 2) * (plots[0].Width + Gap);
            evm.Top = (rows.Length / 2) * (H + Gap);
            plots.Add(evm);
            y = evm.Top + H + Gap;
            plots.Add(new PlotContainerConfig
            {
                PlotType = PlotType.Rect, Left = 0, Top = y, Width = 620, Height = 360,
                CustomTitle = "Output spectrum", CustomTitleOn = true,
                CustomYLabel = "PSD (dBm/Hz)", CustomYLabelOn = true,
                Traces = [new TraceConfig { SourcePath = src, Expression = "ENV1.PSD[\"Iout\", :]", Label = "PSD at the output" }],
            });
        }

        // Per stage: one table a stage, its cumulative value at its output, predicted and simulated.
        foreach (var s in perStage.Length == 0 ? [] : budget.Stages.ToList())
        {
            var cols = perStage.SelectMany(q => new[] { ($"{q.Item1}_{s.Name}_calc", $"{s.Name} {q.Item1} budget"), ($"{q.Item1}_{s.Name}_sim", "simulated") }).ToArray();
            var t = Table($"{s.Name}: at its output, {string.Join(" and ", perStage.Select(q => q.Item2))}", src, cols);
            t.Left = 0;
            t.Top = y;
            y += H + Gap;
            plots.Add(t);
        }

        return Display(chain, bench, src, plots);
    }

    private static DataDisplayConfig Display(string chain, ChainBench bench, string src, List<PlotContainerConfig> plots) => new()
    {
        FormatVersion      = DataDisplayConfig.CurrentFormatVersion,
        SelectedDataSource = src,
        Tabs               = [new TabConfig { Name = ChainBenches.CellName(chain, bench), Plots = plots }],
    };

    /// <summary>The pattern for each steer angle over the array factor, then a table a steer angle.</summary>
    private static List<PlotContainerConfig> Steering(string src)
    {
        // One trace a steer angle, the simulation in its own colour and the closed form dashed over it. AF_calc's axes come
        // out of the arithmetic theta_in first, the simulation's theta_steer first, so each is sliced in its own order.
        TraceConfig One(string cube, int steer, string label, bool steerFirst, LineType line, double width, int color) => new()
        {
            SourcePath = src, CubeName = cube, Label = label,
            CubeSlice = steerFirst
                ? [new AxisSliceConfig { AxisName = "theta_steer", Role = AxisRole.PinToIndex, Index = steer },
                   new AxisSliceConfig { AxisName = "theta_in", Role = AxisRole.KeepAsX }]
                : [new AxisSliceConfig { AxisName = "theta_in", Role = AxisRole.KeepAsX },
                   new AxisSliceConfig { AxisName = "theta_steer", Role = AxisRole.PinToIndex, Index = steer }],
            Properties = new TracePropertiesConfig { LineType = line, LineWidth = width, LineColorIndex = color, MarkerColorIndex = color },
        };
        var traces = new List<TraceConfig>();
        for (int i = 0; i < ChainBenches.SteerAnglesDeg.Length; i++)
            traces.Add(One("Pattern_sim", i, $"steered to {ChainBenches.SteerAnglesDeg[i]:0}°", true, LineType.Solid, 2.5,
                           TraceProperties.LineColorOrder[1 + i]));
        for (int i = 0; i < ChainBenches.SteerAnglesDeg.Length; i++)
            traces.Add(One("AF_calc", i, $"array factor, {ChainBenches.SteerAnglesDeg[i]:0}°", false, LineType.Dashed, 1.2, 5));
        var plots = new List<PlotContainerConfig>
        {
            new()
            {
                PlotType = PlotType.Rect, Left = 0, Top = 0, Width = 720, Height = 380,
                CustomTitle = "Array pattern against the arriving angle, one trace a steer angle", CustomTitleOn = true,
                CustomXLabel = "theta_in, degrees", CustomXLabelOn = true,
                CustomYLabel = "gain over the budget's, dB", CustomYLabelOn = true,
                // The nulls go to −170 dB; the pattern that matters is the main lobe and the first sidelobes.
                Axes = new AxesConfig { AutoscaleX = false, WindowX = -90, WindowWidth = 180, AutoscaleY = false, WindowY = -40, WindowHeight = 45 },
                Traces = traces,
            },
        };
        double y = 380 + Gap;
        foreach (double a in ChainBenches.SteerAnglesDeg)
        {
            string n = a < 0 ? $"m{-a:0}" : a > 0 ? $"p{a:0}" : "0";
            var t = Table($"Steered to {a:0}°: at the peak", src,
                          [("G_calc", $"{a:0}°: G budget, dB"), ($"G_{n}_sim", "simulated"), ("NFw_calc", "NF budget, dB"),
                           ($"NF_{n}_sim", "simulated"), ("A_calc", "array gain budget, dB"), ($"A_{n}_sim", "simulated")]);
            t.Top = y;
            y += H + Gap;
            plots.Add(t);
        }
        return plots;
    }

    /// <summary>A table a combiner variant, in the bench's order.</summary>
    private static List<PlotContainerConfig> Combiners(string src, ChainDesign design)
    {
        int n = design.Stages.FirstOrDefault(s => s.Part == ChainParts.Combiner) is { } c ? ChainParts.ReadCombiner(c).N : 2;
        var plots = new List<PlotContainerConfig>();
        double y = 0;
        foreach (var (topo, kind, code) in ChainBenches.CombinerVariants.Where(v => (n & (n - 1)) == 0 || v.Topology == "Direct"))
        {
            var t = Table($"{topo}, {kind.ToLowerInvariant()}", src,
                          [($"G_{code}_calc", $"{topo}, {kind.ToLowerInvariant()}: G budget, dB"), ($"G_{code}_sim", "simulated"),
                           ($"NF_{code}_calc", "NF budget, dB"), ($"NF_{code}_sim", "simulated"),
                           ($"IIP3_{code}_calc", "IIP3 budget, dBm"), ($"OIP3_{code}_calc", "OIP3 budget, dBm"),
                           ($"A_{code}_calc", "array gain, dB")], first: 190);
            t.Top = y;
            y += H + Gap;
            plots.Add(t);
        }
        return plots;
    }

    /// <summary>
    /// The cascade as the RF Chain Budget window shows it: one Table plot over the budget's <c>stage</c> axis
    /// (<see cref="ChainBudget.ToDataSet"/>), a row per stage and a column per cumulative quantity.
    /// </summary>
    public static PlotContainerConfig CascadeTable(string src)
    {
        (string Cube, string Header)[] cols =
        [
            ("G_dB", "G, dB"), ("NF_dB", "NF, dB"), ("IIP3_dBm", "IIP3, dBm"), ("OIP3_dBm", "OIP3, dBm"),
            ("IP1dB_dBm", "IP1dB, dBm"), ("OP1dB_dBm", "OP1dB, dBm"), ("Pout_dBm", "Pout, dBm"), ("SNR_dB", "SNR, dB"),
            ("NFpct", "share of F − 1, %"),
        ];
        return new PlotContainerConfig
        {
            PlotType = PlotType.Table, Width = 120 + cols.Length * 104, Height = 180,
            CustomTitle = "Cascade, at each stage's output", CustomTitleOn = true, FreqColumnWidth = 120,
            Traces =
            [
                .. cols.Select((c, i) => new TraceConfig
                {
                    SourcePath = src, CubeName = c.Cube, Label = c.Header,
                    CubeSlice = [new AxisSliceConfig { AxisName = "stage", Role = AxisRole.KeepAsX }],
                    FormatString = PrecisionFormat.F, MaximumFractionDigits = 2, ColumnWidth = 104,
                    Properties = new TracePropertiesConfig
                    {
                        LineColorIndex = TraceProperties.LineColorOrder[i % TraceProperties.LineColorOrder.Length],
                        MarkerColorIndex = TraceProperties.LineColorOrder[i % TraceProperties.LineColorOrder.Length],
                    },
                }),
            ],
        };
    }

    /// <summary>One budget, in memory, as the Data Display's source: what the window's cascade table reads.</summary>
    public sealed class Source(string path, RfCore.Data.DataSet data) : IPlotDataSources
    {
        public string? ResolveAbs(string? sourceRef) => sourceRef is null || sourceRef == path ? path : null;
        public bool Contains(string absPath) => absPath == path;
        public RfCore.SNP? NetworkFor(string absPath) => null;
        public RfCore.Data.DataSet? DataFor(string absPath) => absPath == path ? data : null;
        public string? AliasFor(string absPath) => null;
        public string? DisplayNameFor(string absPath) => absPath == path ? "budget" : null;
        public bool HasMultipleSources => false;
        public RfCore.Data.DataSet? SelectedData => data;
    }

    /// <summary>One Table plot of scalar measurement columns; the first, which names the table, <paramref name="first"/>
    /// wide.</summary>
    private static PlotContainerConfig Table(string title, string src, (string Cube, string Header)[] cols, double first = Col) => new()
    {
        PlotType = PlotType.Table,
        Width = Head + (cols.Length - 1) * Col + first,
        Height = H,
        CustomTitle = title,
        CustomTitleOn = true,
        FreqColumnWidth = Head,
        Traces =
        [
            .. cols.Select((c, i) => new TraceConfig
            {
                SourcePath = src, CubeName = c.Cube, CubeSlice = [], Label = c.Header,
                // A difference is read for its size, so it keeps its significant digits however small it is.
                FormatString = c.Cube.EndsWith("_err", System.StringComparison.Ordinal) ? PrecisionFormat.G : PrecisionFormat.F,
                MaximumFractionDigits = 4, ColumnWidth = i == 0 ? first : Col,
                Properties = new TracePropertiesConfig
                {
                    LineColorIndex = TraceProperties.LineColorOrder[i % TraceProperties.LineColorOrder.Length],
                    MarkerColorIndex = TraceProperties.LineColorOrder[i % TraceProperties.LineColorOrder.Length],
                },
            }),
        ],
    };
}
