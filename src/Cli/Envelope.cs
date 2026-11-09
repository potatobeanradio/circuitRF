using CircuitRF.Design.Circuit;
using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Engine;
using CircuitRF.Engine.Envelope;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf env &lt;file.cnl|.csch&gt; [-a name] [--set var=expr] [--stop t] [--accuracy draft|standard|fine]
/// [--segments n|auto] [--warmup t] [--probes a,b] [--all] [-o out.{npy,mat,txt}]</c> — runs the netlist's envelope
/// (<c>type=env</c>) analysis. The modulated stimulus is the circuit's MOD_Source; every override replaces a field
/// of the directive, as for <c>hb</c>. The run records every labeled net's voltage and every IProbe's power;
/// <c>--probes</c> is the retired <c>Probes</c> setting, kept so an existing command line still records the nodes it
/// names (brief ENV-MEAS-3 D2).
/// </summary>
internal static class EnvelopeVerb
{
    public static int Run(string[] args)
    {
        string? input = null, exportPath = null, name = null;
        string? stop = null, acc = null, segs = null, warm = null, probes = null, step = null, mode = null;
        var sets = new List<(string, string)>();
        bool all = false;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
            switch (a)
            {
                case "-a" or "--analysis": name = Next(); break;
                case "-o" or "--export": exportPath = Next(); break;
                case "--stop": stop = Next(); break;
                case "--step": step = Next(); break;
                case "--accuracy": acc = Next(); break;
                case "--envelope": mode = Next(); break;
                case "--segments": segs = Next(); break;
                case "--warmup": warm = Next(); break;
                case "--probes": probes = Next(); break;
                case "--all": all = true; break;
                case "--set":
                {
                    var kv = Next(); int eq = kv.IndexOf('=');
                    if (eq <= 0) return JsonRun.Fail(CliDiagnostics.SetMalformed("env", kv));
                    sets.Add((kv[..eq].Trim(), kv[(eq + 1)..].Trim()));
                    break;
                }
                default:
                    if (a.StartsWith('-')) return JsonRun.Fail(CliDiagnostics.RunUnknownOption("env", a));
                    input = a; break;
            }
        }
        if (input is null)
        {
            int code = JsonRun.Fail(CliDiagnostics.InputRequired("env", ".cnl"));
            Console.Error.WriteLine("Usage: circuitrf env <file.cnl|.csch> [-a name] [--set var=expr] [--stop t] [--step t]");
            Console.Error.WriteLine("                     [--accuracy draft|standard|fine] [--envelope auto|single|lattice] [--segments n|auto]");
            Console.Error.WriteLine("                     [--warmup t] [--all] [-o out.{npy,mat,txt}]");
            Console.Error.WriteLine("  --probes a,b is deprecated: the nodes it names join the labeled nets, whose voltages are recorded.");
            return code;
        }
        JsonRun.InputPath = input;
        if (!File.Exists(input)) return JsonRun.Fail(CliDiagnostics.FileNotFound(input));
        try
        {
            if (CircuitSource.ReadRunInput("env", input, out int refusal) is not { } source) return refusal;
            var (lib, tb) = source;
            // Under --json the waveforms and spectrum stay in -o's file unless asked for (JsonRun.InlineCubeLimit).
            JsonRun.AllCubes = all;
            JsonRun.InlineCubeLimit = 10_000;
            // Every elaboration of this run resolves relative references (SnP, .cmodel) against the document.
            string? refBase = CircuitSource.ReferenceBaseOf(input);
            foreach (var (n, e) in sets)
            {
                HbCircuitRun.ApplySet(tb, n, e);
                Console.Error.WriteLine($"[circuitRF] set {n} = {e}");
            }
            var envs = tb.Analyses.OfType<EnvelopeAnalysis>().ToList();
            var env = name is null ? envs.FirstOrDefault() : envs.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (env is null)
                return JsonRun.Fail(CliDiagnostics.NoAnalysis("env", name is null
                    ? "the netlist declares no envelope analysis (analysis <name> type=env Stop=... )"
                    : $"no envelope analysis named '{name}'"));
            env = new EnvelopeAnalysis(env.Name)
            {
                StopExpr = stop ?? env.StopExpr, StepExpr = step ?? env.StepExpr, Accuracy = acc ?? env.Accuracy,
                MaxHarmExpr = env.MaxHarmExpr, MaxMixOrderExpr = env.MaxMixOrderExpr, Probes = probes ?? env.Probes, ZmeasExpr = env.ZmeasExpr,
                SegmentsExpr = segs ?? env.SegmentsExpr, WarmupExpr = warm ?? env.WarmupExpr, StartExpr = env.StartExpr,
                TolExpr = env.TolExpr, SaveWaveforms = env.SaveWaveforms, Envelope = mode ?? env.Envelope,
                // the noise keys (NZ-7, NZ-8) carry through as written: rebuilt without them, Noise=yes ran noiseless
                NoiseExpr = env.NoiseExpr, SeedExpr = env.SeedExpr, SeedPerPointExpr = env.SeedPerPointExpr,
                EvmSplitExpr = env.EvmSplitExpr,
            };
            // A parametric_sweep wrapping this envelope (e.g. Pavg -> ACPR vs Pout) is dispatched at the SWEEP, the
            // same promotion `hb` applies: running the inner alone would silently drop the sweep axis.
            if (tb.Analyses.OfType<ParametricSweepAnalysis>().FirstOrDefault(s => WrapsEnvelope(tb, s, env.Name)) is { } sweep
                && !tb.Analyses.OfType<ParametricSweepAnalysis>().Any(o => o.InnerAnalysisName == sweep.Name))
            {
                // overrides land in the directive itself, so every sweep point re-resolves them
                int at = tb.Analyses.FindIndex(x => x.Name == env.Name);
                tb.Analyses[at] = env;
                Console.Error.WriteLine($"[circuitRF] envelope {env.Name} inside sweep {sweep.Name} ({sweep.SweepVarName})");
                // Each point elaborates a netlist of its own and throws it away; this one collects what they reported.
                using var diag = new Elaborator(lib) { BaseDirectory = refBase }.Elaborate(tb);
                var sds = ParametricSweepEngine.Run(sweep, lib, tb, new AnalysisSettings(), refBase, RunHost.Control,
                                                    diagnosticsInto: diag);
                foreach (var w in diag.Warnings) Console.Error.WriteLine($"[circuitRF] warning: {w}");
                foreach (var n in diag.Notes) Console.Error.WriteLine($"[circuitRF] note: {n}");
                JsonRun.Analysis = sweep.Name;
                JsonRun.Data = sds;
                PrintSweep(sds, sweep);
                if (exportPath is not null)
                {
                    var fmt = exportPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase) ? ExportFormat.Mat : ExportFormat.Npy;
                    var dir0 = Path.GetDirectoryName(Path.GetFullPath(exportPath));
                    if (!string.IsNullOrEmpty(dir0)) Directory.CreateDirectory(dir0);
                    DataSetExporter.Export(sds, exportPath, fmt, new ExportOptions(Format: fmt));
                    Console.WriteLine($"Wrote {exportPath}");
                    JsonRun.AddOutput(JsonRun.KindOf(exportPath), exportPath);
                }
                return 0;
            }
            using var first = new Elaborator(lib) { BaseDirectory = refBase }.Elaborate(tb);
            var plan = EnvelopeRunner.Resolve(env, first.ResolvedGlobals);
            bool used = false;
            ElaboratedNetlist Elab()
            {
                lock (lib)
                {
                    if (!used) { used = true; return first; }
                    return new Elaborator(lib) { BaseDirectory = refBase }.Elaborate(tb);
                }
            }
            Console.Error.WriteLine($"[circuitRF] envelope {env.Name}: Stop {plan.Params.StopS * 1e6:0.###} us, accuracy x{plan.Params.Oversample}, segments {(plan.Segments == 0 ? "auto" : plan.Segments.ToString(CultureInfo.InvariantCulture))}");
            var ds = EnvelopeRunner.Run(plan, Elab, new AnalysisSettings(), RunHost.Control, n => Console.Error.WriteLine($"[circuitRF] note: {n}"));
            foreach (var w in first.Warnings) Console.Error.WriteLine($"[circuitRF] warning: {w}");
            foreach (var n in first.Notes) Console.Error.WriteLine($"[circuitRF] note: {n}");
            JsonRun.Analysis = env.Name;
            JsonRun.Data = ds;
            Print(ds, env.Name, input);
            if (exportPath is not null)
            {
                var format = exportPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase) ? ExportFormat.Mat : ExportFormat.Npy;
                var dir = Path.GetDirectoryName(Path.GetFullPath(exportPath));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                DataSetExporter.Export(ds, exportPath, format, new ExportOptions(Format: format));
                Console.WriteLine($"Wrote {exportPath}");
                JsonRun.AddOutput(JsonRun.KindOf(exportPath), exportPath);
            }
            return 0;
        }
        catch (OperationCanceledException) { return 130; }
        catch (Exception ex)
        {
            if (Environment.GetEnvironmentVariable("CRF_DEBUG") is not null) Console.Error.WriteLine(ex);
            return JsonRun.Fail(CliDiagnostics.RunFailed(ex.Message));
        }
    }

    private static double S(DataSet ds, string k) => ds.Cubes.TryGetValue(k, out var c) ? c.RealValues[0] : double.NaN;

    private static bool WrapsEnvelope(TestBench tb, ParametricSweepAnalysis s, string envName)
    {
        string inner = s.InnerAnalysisName;
        for (int guard = 0; guard < 16; guard++)
        {
            if (inner.Equals(envName, StringComparison.OrdinalIgnoreCase)) return true;
            var next = tb.Analyses.OfType<ParametricSweepAnalysis>().FirstOrDefault(x => x.Name.Equals(inner, StringComparison.OrdinalIgnoreCase));
            if (next is null) return false;
            inner = next.InnerAnalysisName;
        }
        return false;
    }

    /// <summary>The IProbes carrying RF: those whose gain is a number somewhere in <paramref name="gt"/>
    /// (<c>Gt_dB</c>, branch axis last). A supply or bias probe is NaN there and has nothing to print.</summary>
    private static int[] RfBranches(DataCube? gt, int nBranch)
    {
        if (gt is null) return [];
        int outer = gt.RealValues.Length / Math.Max(1, nBranch);
        return [.. Enumerable.Range(0, nBranch).Where(b => Enumerable.Range(0, outer).Any(o => double.IsFinite(gt.RealValues[o * nBranch + b])))];
    }

    /// <summary>Per IProbe carrying RF, one line per sweep point: the swept value, Pavg, Gt and the worst ACPR1/ACPR2.</summary>
    private static void PrintSweep(DataSet ds, ParametricSweepAnalysis sweep)
    {
        var inv = CultureInfo.InvariantCulture;
        if (ds.Cubes.TryGetValue("PulseDroop_dB", out var droop)) { PrintPulseSweep(ds, sweep, droop); return; }
        if (!ds.Cubes.TryGetValue("Gt_dB", out var gt)) { Console.WriteLine("(no IProbe: place one in series with a load to measure its power)"); return; }
        var vals = gt.Axes[0].Values;
        var branches = gt.Axes[^1].Labels!;
        int nb = branches.Length;
        var a = ds.Cubes.TryGetValue("ACLR", out var ac) ? ac : null;
        var pavg = ds.Cubes.TryGetValue("Pavg_dBm", out var pa) ? pa.RealValues : null;
        foreach (int b in RfBranches(gt, nb))
        {
            Console.WriteLine($"Sweep {sweep.Name} over {sweep.SweepVarName} at IProbe {branches[b]}   (worst of carriers)");
            Console.WriteLine("  value        Pavg dBm   Gt dB    ACPR1 dBc   ACPR2 dBc");
            for (int i = 0; i < vals.Length; i++)
            {
                double w1 = double.NaN, w2 = double.NaN;
                if (a is not null)
                {
                    int nCar = a.Axes[^2].Values.Length, nMet = a.Axes[^1].Values.Length;
                    w1 = w2 = double.NegativeInfinity;
                    for (int c = 0; c < nCar; c++)
                    {
                        int o = ((i * nb + b) * nCar + c) * nMet;
                        w1 = Math.Max(w1, Math.Max(a.RealValues[o + 1], a.RealValues[o + 2]));
                        w2 = Math.Max(w2, Math.Max(a.RealValues[o + 3], a.RealValues[o + 4]));
                    }
                }
                Console.WriteLine(string.Format(inv, "  {0,-11:G5} {1,8:0.00} {2,7:0.00} {3,10:0.00} {4,10:0.00}",
                    vals[i], pavg?[i * nb + b] ?? double.NaN, gt.RealValues[i * nb + b], w1, w2));
            }
        }
    }

    private static void Print(DataSet ds, string name, string input)
    {
        var inv = CultureInfo.InvariantCulture;
        Console.WriteLine($"Analysis: {name}   ({input})");
        Console.WriteLine(string.Format(inv, "  f0 {0:0.######} GHz   step {1:0.###} ns   signal rate {2:0.##} MS/s   segments {3}   wall {4:0.##} s   Newton/step {5:0.##}",
            S(ds, "__EnvF0_Hz") / 1e9, S(ds, "__EnvStep_s") * 1e9, S(ds, "__EnvSignalRate_Hz") / 1e6, S(ds, "__EnvSegments"),
            S(ds, "__EnvWall_s"), S(ds, "__EnvNewtonPerStep")));
        foreach (var k in new[] { "Pin_avg_dBm", "Pdc_W" })
            if (ds.Cubes.ContainsKey(k)) Console.WriteLine(string.Format(inv, "  {0,-18} {1:0.####}", k, S(ds, k)));
        if (!ds.Cubes.TryGetValue("Gt_dB", out var gt))
        {
            Console.WriteLine("  (no IProbe: place one in series with a load to measure its power)");
        }
        else
        {
            var branches = gt.Axes[0].Labels!;
            double At(string cube, int b) => ds.Cubes.TryGetValue(cube, out var c) ? c.RealValues[b] : double.NaN;
            Console.WriteLine("  IProbe               Pavg dBm    Gt dB    DE %   NMSE dB   PAPR dB");
            for (int b = 0; b < branches.Length; b++)
                Console.WriteLine(string.Format(inv, "  {0,-20} {1,8:0.00} {2,8:0.00} {3,7:0.00} {4,9:0.00} {5,9:0.00}",
                    branches[b], At("Pavg_dBm", b), At("Gt_dB", b), At("DE_pct", b), At("NMSE_dB", b), At("PAPR_dB", b)));
            var rf = RfBranches(gt, branches.Length);
            if (ds.Cubes.TryGetValue("ACLR", out var a))
            {
                var car = a.Axes[1].Labels!;
                Console.WriteLine("  ACLR (dBc)            Pch dBm   ACPR1L   ACPR1U   ACPR2L   ACPR2U");
                foreach (int p in rf)
                    for (int c = 0; c < car.Length; c++)
                    {
                        int o = (p * car.Length + c) * 5;
                        Console.WriteLine(string.Format(inv, "  {0,-20} {1,8:0.00} {2,8:0.00} {3,8:0.00} {4,8:0.00} {5,8:0.00}   {6}",
                            branches[p], a.RealValues[o], a.RealValues[o + 1], a.RealValues[o + 2], a.RealValues[o + 3], a.RealValues[o + 4], car[c]));
                    }
            }
            // brief NZ-8: each carrier's received quality, as its kind is measured, with the split and the SNR when
            // EvmSplit ran; a carrier another table measures is left out of this one
            bool split = ds.Cubes.ContainsKey("SNR_dB");
            void Quality(string head, string err, string[] cols, string[] keys, string dist, string noise)
            {
                if (!ds.Cubes.TryGetValue(err, out var cube)) return;
                var car = cube.Axes[1].Labels!;
                double V(string k, int p, int c) => ds.Cubes[k].RealValues[p * car.Length + c];
                Console.WriteLine($"  {head,-20}" + string.Concat(cols.Select(h => $" {h,10}")) + (split ? "     dist %    noise %     SNR dB" : ""));
                foreach (int p in rf)
                    for (int c = 0; c < car.Length; c++)
                    {
                        if (double.IsNaN(V(err, p, c)) && !rf.Any(q => !double.IsNaN(V(err, q, c)))) continue;
                        Console.WriteLine($"  {branches[p],-20}" + string.Concat(keys.Select(k => string.Format(inv, " {0,10:0.000}", V(k, p, c)))) +
                                          (split ? string.Format(inv, " {0,10:0.000} {1,10:0.000} {2,10:0.00}", V(dist, p, c), V(noise, p, c), V("SNR_dB", p, c)) : "") +
                                          $"   {car[c]}");
                    }
            }
            Quality("EVM", "EVM_pct", ["rms %", "peak %", "MER dB"], ["EVM_pct", "EVMpeak_pct", "MER_dB"], "EVMdist_pct", "EVMnoise_pct");
            Quality("FSK", "FSKerr_pct", ["error %", "dev Hz", "offset Hz"], ["FSKerr_pct", "FreqDev_Hz", "FreqOffset_Hz"], "FSKerrDist_pct", "FSKerrNoise_pct");
            Quality("CSS (de-chirped)", "CSSerr_pct", ["error %"], ["CSSerr_pct"], "CSSerrDist_pct", "CSSerrNoise_pct");
            if (ds.Cubes.TryGetValue("PulseDroop_dB", out var droop))
            {
                // a pulsed-CW source: the pulse figures stand where ACLR does for a modulated one
                int n = droop.Axes[1].Values.Length;
                var top = ds.Cubes["PulseTop_dBm"].RealValues; var ph = ds.Cubes["PulsePhaseDroop_deg"].RealValues;
                var rise = ds.Cubes["PulseRise_s"].RealValues; var fall = ds.Cubes["PulseFall_s"].RealValues;
                foreach (int b in rf)
                {
                    Console.WriteLine($"  at IProbe {branches[b]}: pulse   top dBm   droop dB   phase droop deg   rise ns   fall ns");
                    for (int i = 0; i < n; i++)
                    {
                        int o = b * n + i;
                        Console.WriteLine(string.Format(inv, "  {0,5} {1,9:0.000} {2,10:0.0000} {3,17:0.000} {4,9:0.0} {5,9:0.0}",
                            i + 1, top[o], droop.RealValues[o], ph[o], rise[o] * 1e9, fall[o] * 1e9));
                    }
                    Console.WriteLine(string.Format(inv, "  pulse to pulse {0:0.####} dB, {1:0.####} deg", At("PulseToPulse_dB", b), At("PulseToPulse_deg", b)));
                }
            }
        }
        foreach (var k in new[] { "__EnvSeamError", "__EnvTangentError" })
            if (ds.Cubes.ContainsKey(k)) Console.WriteLine(string.Format(inv, "  {0,-18} {1:0.####}", k.TrimStart('_'), S(ds, k)));
    }

    /// <summary>Per IProbe carrying RF, one line per sweep point of a pulsed-CW run: the swept value, then each pulse's
    /// droop and top.</summary>
    private static void PrintPulseSweep(DataSet ds, ParametricSweepAnalysis sweep, DataCube droop)
    {
        var inv = CultureInfo.InvariantCulture;
        var vals = droop.Axes[0].Values;
        var branches = droop.Axes[^2].Labels!;
        int nb = branches.Length, n = droop.Axes[^1].Values.Length;
        var top = ds.Cubes.TryGetValue("PulseTop_dBm", out var t) ? t.RealValues : null;
        foreach (int b in RfBranches(ds.Cubes.TryGetValue("Gt_dB", out var gt) ? gt : null, nb))
        {
            Console.WriteLine($"Sweep {sweep.Name} over {sweep.SweepVarName} at IProbe {branches[b]}:   droop dB (top dBm) of pulses 1..{n}");
            for (int i = 0; i < vals.Length; i++)
                Console.WriteLine(string.Format(inv, "  {0,-11:G5} ", vals[i]) + string.Join("  ", Enumerable.Range(0, n).Select(j =>
                {
                    int o = (i * nb + b) * n + j;
                    return string.Format(inv, "{0:0.0000} ({1:0.00})", droop.RealValues[o], top?[o] ?? double.NaN);
                })));
        }
    }
}
