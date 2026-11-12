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
        // A bench of one of the chain's conversions is displayed against that conversion's own budget.
        foreach (var b in ChainBenches.Build(design, budget, chain))
        {
            string path = Path.Combine(dir, b.CellName + ".cdd");
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(Build(b.Chain, b.Kind, b.Design, b.Budget), json));
            paths.Add(path);
        }
        return paths;
    }

    public static DataDisplayConfig Build(string chain, ChainBench bench, ChainDesign design, ChainBudgetResult budget)
    {
        string src = ChainBenches.CellName(chain, bench) + ".npy";
        switch (bench)
        {
            case ChainBench.Steering: return Display(chain, bench, src, Steering(src));
            case ChainBench.Combiners: return Display(chain, bench, src, Combiners(src, design));
            case ChainBench.Levels: return Display(chain, bench, src, Levels(chain, design, budget, blockersOnly: false));
            case ChainBench.Blockers: return Display(chain, bench, src, Levels(chain, design, budget, blockersOnly: true));
            case ChainBench.Canceller: return Display(chain, bench, src, Canceller(src));
            case ChainBench.Demo: return Display(chain, bench, src, Demo(src, ChainBenches.DemoPowers(budget.Totals!)));
        }
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
            // A stage before anything nonlinear has no intercept, and its bench no row for one.
            var qs = perStage.Where(q => q.Item1 != "OIP3" || double.IsFinite(s.Oip3Dbm)).ToArray();
            var cols = qs.SelectMany(q => new[] { ($"{q.Item1}_{s.Name}_calc", $"{s.Name} {q.Item1} budget"), ($"{q.Item1}_{s.Name}_sim", "simulated") }).ToArray();
            var t = Table($"{s.Name}: at its output, {string.Join(" and ", qs.Select(q => q.Item2))}", src, cols);
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

    // ── the levels, the canceller and the demo ────────────────────────────────

    private static TraceConfig Over(string src, string cube, string axis, string label, int color, bool simulated) => new()
    {
        SourcePath = src, CubeName = cube, Label = label,
        CubeSlice = [new AxisSliceConfig { AxisName = axis, Role = AxisRole.KeepAsX }],
        Properties = simulated
            ? new TracePropertiesConfig { LineEnabled = false, MarkerEnabled = true, MarkerType = MarkerType.Circle, MarkerSize = 1.8,
                                          LineColorIndex = color, MarkerColorIndex = color }
            : new TracePropertiesConfig { LineWidth = 2, LineColorIndex = color, MarkerColorIndex = color },
    };

    private static int C(int i) => TraceProperties.LineColorOrder[i % TraceProperties.LineColorOrder.Length];

    /// <summary>A Rect plot; a stated window fixes both axes (a window given for one axis alone leaves the other at
    /// its default rather than autoscaling it), and none autoscales both.</summary>
    private static PlotContainerConfig Rect(string title, string x, string y, double left, double top, double w, double h,
                                            List<TraceConfig> traces, (double Lo, double Hi)? yWindow = null,
                                            (double Lo, double Hi)? xWindow = null) => new()
    {
        PlotType = PlotType.Rect, Left = left, Top = top, Width = w, Height = h,
        CustomTitle = title, CustomTitleOn = true, CustomXLabel = x, CustomXLabelOn = true, CustomYLabel = y, CustomYLabelOn = true,
        Legend = LegendPlacement.Below, Traces = traces,
        Axes = yWindow is { } yw && xWindow is { } xw
            ? new AxesConfig { AutoscaleX = false, WindowX = xw.Lo, WindowWidth = xw.Hi - xw.Lo,
                               AutoscaleY = false, WindowY = yw.Lo, WindowHeight = yw.Hi - yw.Lo }
            : null,
    };

    /// <summary>
    /// The chain's levels node by node, the budget's as lines and the simulation's as points: the signal and its IM3
    /// (the Levels bench), the noise floor and the NF (the 1-Tone bench), each blocker (the Blockers bench), a
    /// canceller's interference; and in dB the NF, the SNR and the margin to compression. The x axis is the node, 0 the
    /// input and k the output of stage k. <paramref name="blockersOnly"/>: the Blockers bench's own display.
    /// </summary>
    private static List<PlotContainerConfig> Levels(string chain, ChainDesign d, ChainBudgetResult r, bool blockersOnly)
    {
        var lv = ChainLevels.Evaluate(d, r);
        string lev = ChainBenches.CellName(chain, ChainBench.Levels) + ".npy";
        string one = ChainBenches.CellName(chain, ChainBench.OneTone) + ".npy";
        string blk = ChainBenches.CellName(chain, ChainBench.Blockers) + ".npy";
        string nodes = string.Join(", ", lv.Select((n, i) => $"{i} {n.Node}"));
        var traces = new List<TraceConfig>();
        if (!blockersOnly)
        {
            traces.Add(Over(lev, "Psig_calc", ChainBenches.NodeVar, "signal, budget", C(1), false));
            traces.Add(Over(lev, "Psig_sim", ChainBenches.NodeVar, "signal, simulated", C(1), true));
            traces.Add(Over(one, "Noise_node_calc", ChainBenches.NodeVar, "noise floor, budget", C(3), false));
            traces.Add(Over(one, "Noise_node_sim", ChainBenches.NodeVar, "noise floor, simulated NF", C(3), true));
            traces.Add(Over(lev, "IM3_calc", ChainBenches.NodeVar, "IM3, budget", C(4), false));
            traces.Add(Over(lev, "IM3_sim", ChainBenches.NodeVar, "IM3, simulated", C(4), true));
        }
        for (int k = 0; k < d.Blockers.Count; k++)
        {
            string name = $"blocker {k + 1} ({d.Blockers[k].OffsetHz / 1e6:0.##} MHz, {d.Blockers[k].LevelDbm:0.#} dBm)";
            traces.Add(Over(blk, $"Blk{k + 1}_calc", ChainBenches.NodeVar, name + ", budget", C(5 + k), false));
            traces.Add(Over(blk, $"Blk{k + 1}_sim", ChainBenches.NodeVar, name + ", simulated", C(5 + k), true));
        }
        if (!blockersOnly && lv.Any(n => double.IsFinite(n.InterferenceDbm)))
            traces.Add(Over(lev, "Int_calc", ChainBenches.NodeVar, "interference, budget", C(2), false));
        var finite = lv.SelectMany(n => (blockersOnly ? [] : new[] { n.SignalDbm, n.NoiseDbm, n.Im3Dbm, n.InterferenceDbm }).Concat(n.BlockerDbm))
                       .Where(double.IsFinite).ToList();
        double lo = Math.Floor((finite.Min() - 10) / 10) * 10, hi = Math.Ceiling((finite.Max() + 10) / 10) * 10;
        var plots = new List<PlotContainerConfig>
        {
            Rect(blockersOnly ? "The blockers at each node, dBm" : "Levels at each node, dBm", $"node: {nodes}", "dBm", 0, 0, 760, 420,
                 traces, (lo, hi), (-0.5, lv.Count - 0.5)),
        };
        if (!blockersOnly)
        {
            var db = new List<TraceConfig>
            {
                Over(one, "NF_node_calc", ChainBenches.NodeVar, "NF, budget", C(1), false),
                Over(one, "NF_node_sim", ChainBenches.NodeVar, "NF, simulated", C(1), true),
                Over(lev, "SNR_calc", ChainBenches.NodeVar, "SNR, budget", C(3), false),
                Over(lev, "Margin_calc", ChainBenches.NodeVar, "margin to P1dB, budget", C(4), false),
            };
            double top = Math.Ceiling((lv.Select(n => n.SnrDb).Max() + 10) / 10) * 10;
            plots.Add(Rect("NF, SNR and margin to compression at each node, dB", $"node: {nodes}", "dB", 0, 420 + Gap, 760, 340, db,
                           (-5, top), (-0.5, lv.Count - 0.5)));
        }
        return plots;
    }

    /// <summary>The canceller's depth against each error, simulated and closed form for every interference carrier; the
    /// wanted carriers' EVM and SNR without and with cancellation; their constellations both ways.</summary>
    private static List<PlotContainerConfig> Canceller(string src)
    {
        var plots = new List<PlotContainerConfig>();
        var bands = ChainBenches.CancInterference;
        double x = 0;
        foreach (var (sw, axis, title) in new[] { ("P", "phase_err", "phase error, deg"), ("A", "amp_err", "amplitude error, dB"), ("D", "delay_err", "delay mismatch, s") })
        {
            var t = new List<TraceConfig>();
            for (int k = 0; k < bands.Length; k++)
            {
                string tag = $"I{k + 1}", what = $"{bands[k].Mhz} MHz";
                t.Add(Over(src, $"Depth{sw}_{tag}_calc", axis, $"{what}, closed form", C(1 + k), false));
                t.Add(Over(src, $"Depth{sw}_{tag}_sim", axis, $"{what}, simulated", C(1 + k), true));
            }
            plots.Add(Rect($"Depth against the {title[..title.IndexOf(',')]}", title, "depth, dB", x, 0, 420, 360, t));
            x += 420 + Gap;
        }
        var evm = Table("The wanted carriers' EVM and SNR at the output", src,
                        [("EVM_QAM64_off", "64-QAM EVM %, uncancelled"), ("EVM_QAM64_on", "cancelled"), ("SNR_QAM64_on", "SNR dB"),
                         ("EVM_QPSK_off", "QPSK EVM %, uncancelled"), ("EVM_QPSK_on", "cancelled"), ("SNR_QPSK_on", "SNR dB")], first: 190);
        evm.Top = 360 + Gap;
        plots.Add(evm);
        double y = 360 + Gap + H + Gap;
        for (int k = 0; k < 2; k++)
            for (int on = 0; on < 2; on++)
            {
                string kind = k == 0 ? "64-QAM" : "QPSK";
                plots.Add(Constellation($"{kind}, {(on == 1 ? "cancelled" : "uncancelled")}", src, "ENVO",
                                        $"[{on}, 0, {k}, :]", $"[{on}, {k}, :]", (k * 2 + on) * (300 + Gap), y, 300));
            }
        return plots;
    }

    /// <summary>The demo: the 64-QAM carrier's constellation at the middle input power and at the highest, the EVM
    /// against the input power with the budget's from its SNR, its split into noise and distortion, and the SNR.</summary>
    private static List<PlotContainerConfig> Demo(string src, double[] pins)
    {
        (double, double) px = (pins[0] - 2, pins[^1] + 2);
        var plots = new List<PlotContainerConfig>
        {
            Rect("EVM against the input power", "input power per element, dBm", "EVM, %", 0, 0, 560, 380,
                 [Over(src, "EVM_QAM_calc", "Pin", "64-QAM, budget 1/√SNR", C(1), false), Over(src, "EVM_QAM_sim", "Pin", "64-QAM, simulated", C(1), true),
                  Over(src, "EVM_BPSK_sim", "Pin", "DBPSK, simulated", C(2), true)], (0, 30), px),
            Rect("The 64-QAM carrier's error, split", "input power per element, dBm", "%", 560 + Gap, 0, 560, 380,
                 [Over(src, "EVMnoise_QAM", "Pin", "noise", C(3), true), Over(src, "EVMdist_QAM", "Pin", "distortion", C(4), true)], (0, 30), px),
            Rect("SNR in the carrier's channel", "input power per element, dBm", "dB", 0, 380 + Gap, 560, 320,
                 [Over(src, "SNR_QAM_calc", "Pin", "budget", C(1), false), Over(src, "SNR_QAM_sim", "Pin", "simulated", C(1), true)]),
        };
        plots.Add(Constellation("64-QAM at the output, mid power", src, "ENVD", "[3, 0, 0, :]", "[3, 0, :]", 560 + Gap, 380 + Gap, 320));
        plots.Add(Constellation("64-QAM at the output, highest power", src, "ENVD", "[6, 0, 0, :]", "[6, 0, :]", 900 + 2 * Gap, 380 + Gap, 320));
        return plots;
    }

    /// <summary>A constellation as the signal-quality preset draws it: the received points over the ideal ones, Im
    /// against Re on one scale.</summary>
    private static PlotContainerConfig Constellation(string title, string src, string group, string at, string refAt, double left, double top, double size)
    {
        TraceConfig Points(string cube, string slice, string label, int color, MarkerType shape, double markerSize) => new()
        {
            SourcePath = src, Expression = $"imag({group}.{cube}{slice})", XSpec = $"real({group}.{cube}{slice})", Label = label,
            Properties = new TracePropertiesConfig { LineEnabled = false, MarkerEnabled = true, MarkerType = shape, MarkerSize = markerSize,
                                                     LineColorIndex = color, MarkerColorIndex = color },
        };
        return new PlotContainerConfig
        {
            PlotType = PlotType.Rect, Left = left, Top = top, Width = size, Height = size + 20,
            CustomTitle = title, CustomTitleOn = true, CustomXLabel = "I", CustomXLabelOn = true, CustomYLabel = "Q", CustomYLabelOn = true,
            Legend = LegendPlacement.Below,
            Traces = [Points("Const", at, "received", C(1), MarkerType.Circle, 0.8), Points("ConstRef", refAt, "ideal", C(0), MarkerType.Cross, 1.6)],
            Axes = new AxesConfig { WindowWidth = 0, WindowHeight = 0, WindowSecondaryWidth = 0, WindowSecondaryHeight = 0, EqualAspect = true },
        };
    }

    /// <summary>The window's Levels tab: the levels in dBm and the dB quantities, each budget cube as a line and each
    /// simulated cube the window found beside it (<c>Psig_sim</c>, …) as points, all over the in-memory node axis.</summary>
    public static IReadOnlyList<PlotContainerConfig> WindowLevels(ChainDesign d, IReadOnlyList<ChainNodeLevel> lv)
    {
        const string src = "budget";
        var t = new List<TraceConfig>
        {
            Over(src, "Psig_dBm", "node", "signal", C(1), false), Over(src, "Psig_sim", "node", "signal, simulated", C(1), true),
            Over(src, "Noise_dBm", "node", "noise floor", C(3), false), Over(src, "Noise_node_sim", "node", "noise floor, simulated", C(3), true),
            Over(src, "IM3_dBm", "node", "IM3", C(4), false), Over(src, "IM3_sim", "node", "IM3, simulated", C(4), true),
        };
        for (int k = 0; k < d.Blockers.Count; k++)
        {
            t.Add(Over(src, $"Blk{k + 1}_dBm", "node", $"blocker {k + 1}", C(5 + k), false));
            t.Add(Over(src, $"Blk{k + 1}_sim", "node", $"blocker {k + 1}, simulated", C(5 + k), true));
        }
        if (lv.Any(n => double.IsFinite(n.InterferenceDbm))) t.Add(Over(src, "Int_dBm", "node", "interference", C(2), false));
        var db = new List<TraceConfig>
        {
            Over(src, "NF_dB", "node", "NF", C(1), false), Over(src, "NF_node_sim", "node", "NF, simulated", C(1), true),
            Over(src, "SNR_dB", "node", "SNR", C(3), false), Over(src, "Margin_dB", "node", "margin to P1dB", C(4), false),
        };
        return [Rect("Levels at each node, dBm", "node", "dBm", 0, 0, 560, 240, t),
                Rect("NF, SNR and margin, dB", "node", "dB", 0, 0, 480, 240, db)];
    }

    /// <summary>The window's Canceller tab, in closed form: the depth against the phase error for five amplitude errors,
    /// and against the bandwidth for four delay mismatches, the stage's own errors marked by its own curve.</summary>
    public static (RfCore.Data.DataSet Data, IReadOnlyList<PlotContainerConfig> Plots) WindowCanceller(CancellerSpec c)
    {
        var ds = new RfCore.Data.DataSet();
        var ph = Enumerable.Range(0, 81).Select(i => -10 + 0.25 * i).ToArray();
        var phAxis = new RfCore.Data.Axis("phase_err", ph, "deg");
        // In MHz and unitless: an axis in Hz is a frequency, which the plot rescales to its own unit.
        var bw = Enumerable.Range(0, 100).Select(i => 1.0 + i).ToArray();
        var bwAxis = new RfCore.Data.Axis("bandwidth", bw, "");
        var t1 = new List<TraceConfig>();
        double[] amps = [0, 0.1, 0.5, 1, 2];
        for (int k = 0; k < amps.Length; k++)
        {
            ds.Add($"DepthPh{k}", new RfCore.Data.DataCube([phAxis], [.. ph.Select(p => Depth(amps[k], p, c.DelayErrS, c.BintHz))]) { Unit = "dB" });
            t1.Add(Over("budget", $"DepthPh{k}", "phase_err", $"amplitude error {amps[k]:0.##} dB", C(1 + k), false));
        }
        var t2 = new List<TraceConfig>();
        double[] delays = [0.1e-9, 0.5e-9, 1e-9, 2e-9];
        for (int k = 0; k < delays.Length; k++)
        {
            ds.Add($"DepthBw{k}", new RfCore.Data.DataCube([bwAxis], [.. bw.Select(b => Depth(c.AmpErrDb, c.PhaseErrDeg, delays[k], b * 1e6))]) { Unit = "dB" });
            t2.Add(Over("budget", $"DepthBw{k}", "bandwidth", $"delay mismatch {delays[k] * 1e9:0.##} ns", C(1 + k), false));
        }
        return (ds, [Rect($"Depth against the phase error, delay mismatch {c.DelayErrS * 1e9:0.###} ns over {c.BintHz / 1e6:0.##} MHz",
                          "phase error, deg", "depth, dB", 0, 0, 520, 240, t1, (0, 60), (-10, 10)),
                     Rect($"Depth against the bandwidth, amplitude error {c.AmpErrDb:0.##} dB and phase error {c.PhaseErrDeg:0.##}°",
                          "bandwidth, MHz", "depth, dB", 0, 0, 520, 240, t2, (0, 60), (0, 100))]);
    }

    private static double Depth(double ampErrDb, double phaseErrDeg, double delayErrS, double bwHz)
        => -10 * System.Math.Log10(System.Math.Max(CancellerSpec.Residual(ampErrDb, phaseErrDeg, delayErrS, bwHz), 1e-12));

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
