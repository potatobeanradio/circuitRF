using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using CircuitRF.Engine.Loadpull;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Loadpull;

/// <summary>
/// Where a batch loadpull's time goes, per device kind (brief LP-0): one serial <c>Run</c> for the totals and the
/// counters, then every rung of one load replayed piece by piece — the K+1 extractions, the K+1 source right-hand
/// sides <c>RunSinglePoint</c> builds again for its back-solver, the Newton solve and the device evaluations inside it,
/// and the back-solve the FOMs read. Counters and clocks only; nothing is asserted about time.
/// </summary>
[Collection("ExternalDeviceRegistry")]
public sealed class LoadpullCostTests(ITestOutputHelper output) : IDisposable
{
    private const string Provider = "LpCost";

    public void Dispose() => ExternalDeviceRegistry.Clear();

    internal static string Find(string relative)
    {
        for (var dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            string cand = Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(cand) || Directory.Exists(cand)) return Path.GetFullPath(cand);
        }
        throw new FileNotFoundException(relative);
    }

    private static string N(double v) => v.ToString("G17", CultureInfo.InvariantCulture);

    /// <summary>A <c># gamma mag_ang</c> grid file of <paramref name="gammas"/> in a fresh temp folder.</summary>
    internal static string Gam(IEnumerable<Complex> gammas)
    {
        string path = Path.Combine(Directory.CreateTempSubdirectory("crf-lpcost-").FullName, "grid.gam");
        File.WriteAllLines(path, ["# gamma Z0=50 mag_ang",
            .. gammas.Select(g => $"{N(g.Magnitude)} {N(g.Phase * 180 / Math.PI)}")]);
        return path;
    }

    internal static IReadOnlyList<Complex> Hero3Grid()
        => GamReader.ReadFile(Find("testdata/Hero3/hero3_load.gam")).Points.Select(p => p.Gamma).ToList();

    private static string Analysis(string grid, double pinStart, double pinMax, int maxHarm = 4) =>
        $"analysis LP1 type=loadpull Tone=RFfreq MaxHarm={maxHarm} LoadTuner=Load SourceTuner=Src Sweep=Load TuneHarm=1 " +
        $"Grid=\"{grid}\" Compression=3 GainType=Gt PinStart={N(pinStart)} PinStep=1 PinMax={N(pinMax)} Tickle=-50 " +
        "MaxIter=100 Tol=1e-6";

    /// <summary>The four device kinds of brief LP-0, each on a bench of its own size.</summary>
    internal static (Library Lib, TestBench Tb) Bench(string kind, IReadOnlyList<Complex>? grid = null)
    {
        string cnl = kind switch
        {
            "sdd" => grid is null ? "" : File.ReadAllText(Find("testdata/Hero3/hero3.cnl"))
                                              .Replace("Grid=\"hero3_load.gam\"", $"Grid=\"{Gam(grid)}\""),
            "native" => $"""
                RFfreq = 2e9
                Tuner:Src  n_gate 0  Z[1]=50  Zdefault=1e-6  BiasTee=on  Vbias=-0.4
                FET_Angelov:Q1  n_gate n_drain 0  Cgs=1.2e-12  Cgd=0.15e-12
                Tuner:Load  n_drain 0  Z[1]=50  Zdefault=1e-6  BiasTee=on  Vbias=10
                {Analysis(Gam(grid ?? Hero3Grid()), -20, 20)}
                """,
            "composed" => $"""
                RFfreq = 2e9
                Tuner:Src  n_gate 0  Z[1]=50  Z[2]=1e-6  Z[3]=1e-6  Zdefault=1e-6  BiasTee=on  Vbias=-0.8
                ComposedFET:X1  n_gate n_drain 0  File="{Find("examples/Model Composer/synthetic-gan/gan_4x125.cmodel")}"  N=8
                Tuner:Load  n_drain 0  Z[1]=50  Z[2]=1e-6  Z[3]=1e-6  Zdefault=1e-6  BiasTee=on  Vbias=8
                {Analysis(Gam(grid ?? GanGrid().Take(12)), -4, 26, 5)}
                """,
            "external" => $"""
                RFfreq = 2e9
                Tuner:Src  n_gate 0  Z[1]=50  Zdefault=1e-6  BiasTee=on  Vbias=-1.5
                ExtDevice:X1  n_gate n_drain 0  Provider={Provider} Type=crf_fet beta=0.06 vth=-2.5 lambda=0.02 alpha=1.5 \
                    delta=0.2 cgs=2e-12 cgd=0.2e-12 ggs=1e-6
                Tuner:Load  n_drain 0  Z[1]=80  Zdefault=1e-6  BiasTee=on  Vbias=28
                {Analysis(Gam(grid ?? Hero3Grid()), -10, 30)}
                """,
            _ => throw new ArgumentException(kind),
        };
        if (kind == "sdd" && grid is null)
            return CnlReader.ReadFile(Find("testdata/Hero3/hero3.cnl"));
        if (kind == "external" && !ExternalDeviceRegistry.ProviderNames.Contains(Provider))
            ExternalDeviceRegistry.Register(DeviceWorkerProvider.Launch(
                Provider, Find("tools/osdi-worker/osdi-worker"), [Find("tools/fake-osdi-model/fake_osdi.osdi")]));
        return new CnlReader().Read(cnl);
    }

    internal static IReadOnlyList<Complex> GanGrid()
        => GamReader.ReadFile(Find("examples/Model Composer/GanLoadpull/schematic/load_grid.gam")).Points.Select(p => p.Gamma).ToList();

    private static T Field<T>(object o, string name)
        => (T)o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;

    /// <summary>
    /// LP-1's speedup record: one grid serially and at 4, 8 and 16 workers (the cap; the machine's own count is printed),
    /// every parallel result checked bit for bit against the serial one. A 61-point ring grid on the SDD and native FETs,
    /// GanLoadpull's 49 loads on the composed model; the external device stays serial by design and is timed once.
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Theory]
    [InlineData("sdd")]
    [InlineData("native")]
    [InlineData("composed")]
    [InlineData("external")]
    public void M1_TheParallelGridsSpeedup(string kind)
    {
        if (kind == "external" && !FixturePaths.Exists("tools/osdi-worker/osdi-worker")) return;
        var grid = kind == "composed" ? GanGrid() : RingGrid(5, 12, 0.6);
        LoadpullParallelTests.Run(() => Bench(kind, grid), 1);   // JIT
        double Time(int w, out LoadpullParallelTests.Outcome o)
        {
            var sw = Stopwatch.StartNew();
            var err = Console.Error;
            Console.SetError(TextWriter.Null);
            try { o = LoadpullParallelTests.Run(() => Bench(kind, grid), w); } finally { Console.SetError(err); }
            return sw.Elapsed.TotalSeconds;
        }
        double t1 = Time(1, out var serial);
        output.WriteLine($"[{kind}] {grid.Count} loads, {Environment.ProcessorCount} cores visible: serial {t1:F2} s");
        foreach (int w in new[] { 4, 8, 16 })
        {
            double tw = Time(w, out var par);
            LoadpullParallelTests.AssertSame(serial, par);
            output.WriteLine($"[{kind}]   {w,2} workers (ran {par.Degree}): {tw:F2} s = {t1 / tw:F2}x, bit-identical");
            if (kind != "external") Assert.Equal(w, par.Degree);
        }
    }

    internal static Complex[] RingGrid(int rings, int spokes, double maxGamma)
        => [0, .. from r in Enumerable.Range(1, rings) from s in Enumerable.Range(0, spokes)
                  select Complex.FromPolarCoordinates(maxGamma * r / rings, 2 * Math.PI * s / spokes)];

    [Trait("Category", "Benchmark")]
    [Theory]
    [InlineData("sdd")]
    [InlineData("native")]
    [InlineData("composed")]
    [InlineData("external")]
    public void M0_WhereALoadpullsTimeGoes(string kind)
    {
        if (kind == "external" && !FixturePaths.Exists("tools/osdi-worker/osdi-worker")) return;

        var sw = Stopwatch.StartNew();
        var (lib, tb) = Bench(kind);
        using var nl = new Elaborator(lib).Elaborate(tb);
        double tElab = sw.Elapsed.TotalMilliseconds;
        var lpa = tb.Analyses.OfType<LoadpullAnalysis>().Single();
        var p = LoadpullEngine.Resolve(lpa, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
        var eng = new LoadpullEngine(nl, tb);

        // The engine prints a line per rung, and what reads that pipe sets its cost: each run is timed with stderr discarded
        // and as the test host takes it. The first, discarded, run pays the JIT.
        var err = Console.Error;
        double Quiet()
        {
            Console.SetError(TextWriter.Null);
            var t = Stopwatch.StartNew();
            try { new LoadpullEngine(nl, tb).Run(p); } finally { Console.SetError(err); }
            return t.Elapsed.TotalSeconds;
        }
        Quiet();
        double tQuiet = Quiet();
        HbNewton.ResetEvaluations();
        NonlinearDcEngine.ResetRuns();
        sw.Restart();
        var ds = eng.Run(p);
        double tRun = sw.Elapsed.TotalSeconds;
        int evals = HbNewton.Evaluations, dcRuns = NonlinearDcEngine.Runs;
        var hb = Field<HbEngine>(eng, "_hbEngine");
        int lus = hb.LinearFactorizations;
        int loads = p.Grid.Points.Count;
        int rungs = ds["Converged"].RealValues.Count(v => v == 1);

        // ── one load's rungs, replayed piece by piece on the run's own engine and extractor ──
        var ctx = eng.PrepareContext(p);
        var z = p.Grid.Points[loads / 2].Z;
        var gpr = eng.RunOneTermination(p, ctx, z, loads / 2);
        var ex = Field<HbLinearExtractor>(hb, "_extractor");
        int K = ctx.K, nIf = ctx.InterfaceNodes.Length;
        double f0 = p.ToneHz, w0 = 2 * Math.PI * f0;
        int gridN = HbFft.GridSize(K, p.FFTOverSample);
        var steps = gpr.PinSteps.Where(s => s.Converged).ToList();
        double tExtract = 1e9, tRhs = 1e9, tNewton = 1e9, tEval = 1e9, tBack = 1e9, tSingle = 1e9;
        int iters = 0, newtonEvals = 0;
        for (int rep = 0; rep < 3; rep++)
        {
            double e = 0, r = 0, n = 0, ev = 0, b = 0, s1 = 0;
            iters = 0; newtonEvals = 0;
            for (int j = 1; j < steps.Count; j++)
            {
                ctx.SweptModel.SetHarmonicOverride(p.TuneHarm, z);
                ctx.SrcModel.SetSourceDrive(f0, Math.Pow(10, (steps[j].PavlDbm - 30) / 10));
                var warm = steps[j - 1].V;

                sw.Restart();
                var yNN = new Complex[K + 1][,]; var iSrc = new Complex[K + 1][];
                (yNN[0], iSrc[0]) = ex.ExtractDC();
                for (int k = 1; k <= K; k++) (yNN[k], iSrc[k]) = ex.Extract(k * w0);
                e += sw.Elapsed.TotalMilliseconds;

                sw.Restart();
                var rhs = new Complex[K + 1][];
                for (int k = 0; k <= K; k++) rhs[k] = ex.BuildSourceRhs(k * w0);
                r += sw.Elapsed.TotalMilliseconds;

                var v = (Complex[,])warm.Clone();
                int before = HbNewton.Evaluations;
                sw.Restart();
                var sr = HbNewton.Solve(v, yNN, iSrc, f0, K, nIf, nl, ctx.InterfaceNodes, gridN, ctx.SolveSettings, p.Tol,
                                        ctx.HbParams.Lambda, p.GuardHarmonic);
                n += sw.Elapsed.TotalMilliseconds;
                iters += sr.Iterations;
                newtonEvals += HbNewton.Evaluations - before;

                sw.Restart();
                HbNewton.EvaluateNonlinear(v, nIf, K, gridN, nl, ctx.InterfaceNodes);
                ev += sw.Elapsed.TotalMilliseconds;

                sw.Restart();
                var back = new HbLinearBackSolver(ex, f0, K, [sr.INl], [rhs], nl);
                for (int k = 0; k <= K; k++) back.GetSolution(k, 0);
                b += sw.Elapsed.TotalMilliseconds;

                sw.Restart();
                hb.RunSinglePoint(ctx.HbParams, warm, ctx.SolveSettings);
                s1 += sw.Elapsed.TotalMilliseconds;
            }
            int m = Math.Max(1, steps.Count - 1);
            tExtract = Math.Min(tExtract, e / m); tRhs = Math.Min(tRhs, r / m); tNewton = Math.Min(tNewton, n / m);
            tEval = Math.Min(tEval, ev / m); tBack = Math.Min(tBack, b / m); tSingle = Math.Min(tSingle, s1 / m);
        }
        int solves = Math.Max(1, steps.Count - 1);
        double evalPerSolve = (double)newtonEvals / solves, evalMs = tEval * evalPerSolve;
        double pct(double x) => 100 * x / tSingle;

        output.WriteLine($"[{kind}] {loads} loads, {rungs} converged rungs, run {tRun:F2} s ({tRun / loads * 1e3:F0} ms/load), " +
                         $"elaboration {tElab:F0} ms, {Environment.ProcessorCount} cores visible, serial; " +
                         $"with stderr discarded {tQuiet:F2} s ({tQuiet / loads * 1e3:F0} ms/load)");
        output.WriteLine($"[{kind}] counters: {evals} device evaluations ({(double)evals / rungs:F1}/rung), " +
                         $"{lus} LU factorisations, {dcRuns} DC solves");
        output.WriteLine($"[{kind}] one warm solve (load {loads / 2}, {solves} rungs, best of 3): RunSinglePoint {tSingle:F3} ms = " +
                         $"extract K+1 {tExtract:F3} ms ({pct(tExtract):F0} %) + duplicate RHS K+1 {tRhs:F3} ms ({pct(tRhs):F0} %) + " +
                         $"Newton {tNewton:F3} ms ({pct(tNewton):F0} %: {(double)iters / solves:F1} iterations, {evalPerSolve:F1} evaluations " +
                         $"x {tEval:F3} ms = {evalMs:F3} ms device, {tNewton - evalMs:F3} ms linear algebra) + back-solve {tBack:F3} ms " +
                         $"({pct(tBack):F0} %)");
        output.WriteLine($"[{kind}] solves in the run ~ {tQuiet * 1e3 / tSingle:F0} at that cost; device evaluations ~ " +
                         $"{100 * evals * tEval / (tQuiet * 1e3):F0} % of the quiet run");
    }
}
