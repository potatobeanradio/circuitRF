using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Harmonica.Tests;

/// <summary>
/// M1 of brief-harmonicarf-h0-h3: harmonic balance asks an external device for a whole time grid in
/// ONE round trip instead of one per sample, and the answer does not move.
///
/// <para><b>Why this is here and not in Engine.Tests.</b> The brief's gate command scopes the risk
/// to <c>Engine.Tests</c>'s existing ~1,000 tests and forbids adding to them; harmonicaRF's own
/// tests live in this project. Nothing in this file is harmonicaRF-specific — the change benefits
/// every HB sweep on an external model — but this is where the brief puts it.</para>
///
/// <para><b>The fixture is a NONLINEAR external device, and that mattered.</b>
/// <c>tools/fake-osdi-model</c>'s two original devices (<c>crf_rc</c>, <c>crf_collapse</c>) are both
/// LINEAR. A linear device's Newton loop converges in one or two iterations, so it evaluates a
/// fraction of the operating points a real transistor does and any per-evaluation cost measured
/// against it is understated in exactly the ratio being measured. A third device, <c>crf_fet</c>,
/// was added for this: a square-law FET with a smooth pinch-off, a smooth triode→saturation knee,
/// Cgs/Cgd charge, and three terminals. It is still not a model — its closed form is written in the
/// library's own comment and <see cref="M1_0_TheFixtureIsGenuinelyNonlinear"/> asserts against that
/// arithmetic rather than against another implementation.</para>
///
/// <para><b>How "unbatched" is reproduced.</b> Not by a flag in the engine. <c>Unbatched</c> below
/// is a provider whose instances forward only the scalar <c>Evaluate</c> and inherit
/// <c>IExternalDeviceInstance.EvaluateBatch</c>'s default scalar loop — which is exactly what a
/// provider that has not implemented batching gets, and exactly the number of round trips the
/// engine made before this change. So the comparison runs both sides through identical engine code
/// and differs only in how many times the pipe is crossed.</para>
/// </summary>
// One collection for the registry AND for the timing: ExternalDeviceRegistry is global static, and
// the benchmark collection is non-parallel, so membership gives both protections at once.
[Collection("HarmonicaBenchmarks")]
public sealed class ExternalDeviceBatchingTests : IDisposable
{
    private const string WorkerRel = "tools/osdi-worker/osdi-worker";
    private const string ModelRel  = "tools/fake-osdi-model/fake_osdi.osdi";
    private const string HowTo     = "run tools/osdi-worker/build.sh (needs a C compiler)";

    private const string BatchedProvider   = "OsdiBatched";
    private const string UnbatchedProvider = "OsdiUnbatched";

    private readonly ITestOutputHelper _out;

    public ExternalDeviceBatchingTests(ITestOutputHelper output)
    {
        _out = output;
        ExternalDeviceRegistry.Clear();
    }

    public void Dispose() => ExternalDeviceRegistry.Clear();

    // ── the device, in closed form (mirrors the library's own comment) ─────────

    private const double Beta = 0.06, Vth = -2.5, Lambda = 0.02, Alpha = 1.5, Delta = 0.2;
    private const double Cgs  = 2.0e-12, Cgd = 0.2e-12, Ggs = 1.0e-6;

    private static double DrainCurrent(double vgs, double vds)
    {
        double u    = vgs - Vth;
        double vov  = 0.5 * (u + Math.Sqrt(u * u + Delta * Delta));
        double x    = Alpha * vds;
        double sat  = x / Math.Sqrt(1.0 + x * x);
        return Beta * vov * vov * (1.0 + Lambda * vds) * sat;
    }

    // ── fixtures ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers both providers against the same worker executable and the same model library. Two
    /// worker processes, so neither measurement is serialised behind the other's pipe.
    /// </summary>
    private static void RegisterProviders()
    {
        string worker = FixturePaths.Require(WorkerRel);
        string model  = FixturePaths.Require(ModelRel);

        ExternalDeviceRegistry.Register(DeviceWorkerProvider.Launch(BatchedProvider, worker, [model]));
        ExternalDeviceRegistry.Register(
            new Unbatched(DeviceWorkerProvider.Launch(UnbatchedProvider, worker, [model])));
    }

    private static string N(double v) => v.ToString("G17", CultureInfo.InvariantCulture);

    /// <summary>
    /// A grounded-source PA around the external FET: 50 Ω source with a 2 GHz drive, a fundamental
    /// load, near-shorts above it, and ideal bias tees. The same shape as Hero 2, with the SDD
    /// replaced by the external device — so the Newton loop is doing real work at every sample.
    /// </summary>
    private static string Netlist(string provider, double pavlDbm, int maxHarm) => $"""
        RFfreq = 2e9
        Vgg    = -1.5
        Vdd    = 28
        Zs     = 50
        Pavl_w = 10^(({N(pavlDbm)} - 30)/10)
        Vs_mag = sqrt(8 * Pavl_w * Zs)

        V_1Tone:Vdrive  n_src 0      Freq=RFfreq  V=Vs_mag  Phase=0
        Z_Port:Zsource  n_src n_zs   Z[1,1]=if(freq <= RFfreq) then Zs else 1e-6 endif
        C:Cblock_g      n_zs n_gate  C=1 uF
        V_1Tone:Vgate   n_gbias 0    Vdc=Vgg
        L:Lchoke_g      n_gbias n_gate  L=1 uH

        ExtDevice:X1  n_gate n_drain 0  Provider={provider} Type=crf_fet \
            beta={N(Beta)} vth={N(Vth)} lambda={N(Lambda)} alpha={N(Alpha)} delta={N(Delta)} \
            cgs={N(Cgs)} cgd={N(Cgd)} ggs={N(Ggs)}

        C:Cblock_d      n_drain n_zl  C=1 uF
        Z_Port:Zload    n_zl 0        Z[1,1]=if(freq <= RFfreq) then 80 else 1e-6 endif
        L:Lchoke_d      n_drain n_dbias  L=1 uH
        V_1Tone:Vdrain  n_dbias 0     Vdc=Vdd

        analysis HB1 type=hb Tone=RFfreq MaxHarm={maxHarm} Tol=1e-8
        """;

    /// <summary>The built-in comparison: Hero 2's SDD GaN HEMT, same testbench shape.</summary>
    private static string BuiltInNetlist(double pavlDbm, int maxHarm) => $"""
        Sv=-0.837
        Sc=0.71
        TV0=4.268
        TC=1.507
        th=0.001
        a=0.176
        g=0.089
        lam=0.0012
        B=1130
        RFfreq = 2e9
        Vgg    = -3.05
        Vdd    = 48
        Zs     = 25
        Pavl_w = 10^(({N(pavlDbm)} - 30)/10)
        Vs_mag = sqrt(8 * Pavl_w * Zs)

        V_1Tone:Vdrive  n_src 0      Freq=RFfreq  V=Vs_mag  Phase=0
        Z_Port:Zsource  n_src n_zs   Z[1,1]=if(freq <= RFfreq) then Zs else 1e-6 endif
        C:Cblock_g      n_zs n_gate  C=1 uF
        V_1Tone:Vgate   n_gbias 0    Vdc=Vgg
        L:Lchoke_g      n_gbias n_gate  L=1 uH

        SDD:M1  n_gate 0  n_drain 0  \
          I[1,0]=_v1/50  \
          I[2,0]=(B*TC*tanh(_v2*a*(tanh(g*(TV0 - _v1 + _v2*th + Sc*ln(exp(-(Sv - _v1)/Sc) + 1))) + 1))*ln(exp(-(2*TV0 - 2*_v1 + 2*_v2*th + 2*Sc*ln(exp(-(Sv - _v1)/Sc) + 1))/TC) + 1)*(_v2*lam + 1))/2

        C:Cblock_d      n_drain n_zl  C=1 uF
        Z_Port:Zload    n_zl 0        Z[1,1]=if(freq <= RFfreq) then 80 else 1e-6 endif
        L:Lchoke_d      n_drain n_dbias  L=1 uH
        V_1Tone:Vdrain  n_dbias 0     Vdc=Vdd

        analysis HB1 type=hb Tone=RFfreq MaxHarm={maxHarm} Tol=1e-8
        """;

    private static (HbEngine Engine, HbAnalysisParams Params) Build(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);

        var hb = (CircuitRF.Core.Design.HarmonicBalanceAnalysis)tb.Analyses[0];
        var p  = HbEngine.Resolve(hb, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
        return (new HbEngine(nl, tb), p);
    }

    /// <summary>
    /// The design's "warm-seeded" case, measured honestly: the seed comes from a solve at a
    /// NEIGHBOURING drive level, not from this point's own answer.
    ///
    /// <para>Seeding from the exact converged solution reads 1 Newton iteration and therefore about
    /// a quarter of the device evaluations a real step makes — which understates every per-evaluation
    /// cost in exactly the ratio being measured. A Pin step, or a marker move, lands a couple of
    /// iterations away, and that is what the budget table's 0.94 ms is a measurement of.</para>
    /// </summary>
    private static (double MsPerSolve, int Iterations) TimeWarmSolves(
        Func<double, (HbEngine Engine, HbAnalysisParams Params)> at,
        double seedPoint, double measurePoint, int warm)
    {
        var (seedEngine, seedParams) = at(seedPoint);
        var seed = seedEngine.RunSinglePoint(seedParams);
        Assert.True(seed.Converged, $"the seed point must converge before anything can be timed: {seed.FailReason}");

        var (engine, p) = at(measurePoint);

        // One untimed warm solve so JIT and the worker's buffers are not in the measurement.
        var settle = engine.RunSinglePoint(p, seed.V);
        Assert.True(settle.Converged, $"the measured point must converge: {settle.FailReason}");
        Assert.True(settle.Iterations >= 2,
            $"the warm start must be a real step, not the answer itself — it took {settle.Iterations} iteration(s)");

        // BEST of three batches, not the mean. The quantity asked for is "how fast is this path",
        // and on a machine also running other tests the mean measures the other tests. The minimum
        // is the standard robust estimator for it and is what makes the comparison survive a shared
        // run — see BenchmarkCollection for the measurement that forced this.
        double best = double.MaxValue;
        HbEngine.SinglePointResult last = settle;
        for (int rep = 0; rep < 3; rep++)
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < warm; i++) last = engine.RunSinglePoint(p, seed.V);
            sw.Stop();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds / warm);
        }

        Assert.True(last.Converged);
        return (best, last.Iterations);
    }

    // ── M1_0 — the fixture is a real transistor, checked against its own closed form ──

    [FixtureFact(ModelRel, HowTo)]
    public void M1_0_TheFixtureIsGenuinelyNonlinear()
    {
        RegisterProviders();

        var instance = ExternalDeviceRegistry.Require(BatchedProvider).Create("crf_fet",
            new Dictionary<string, string>
            {
                ["beta"] = N(Beta), ["vth"] = N(Vth), ["lambda"] = N(Lambda),
                ["alpha"] = N(Alpha), ["delta"] = N(Delta),
                ["cgs"] = N(Cgs), ["cgd"] = N(Cgd), ["ggs"] = N(Ggs),
            });

        using (instance)
        {
            // Node order is G, D, S. The drain current must follow the closed form, and the device
            // must actually bend: a straight line through these three biases would prove nothing.
            double[] vgsSweep = [-2.4, -1.5, -0.5];
            var currents = new double[vgsSweep.Length];

            for (int k = 0; k < vgsSweep.Length; k++)
            {
                var r = instance.Evaluate([vgsSweep[k], 28.0, 0.0]);
                currents[k] = r.Current[1];
                Assert.Equal(DrainCurrent(vgsSweep[k], 28.0), r.Current[1], 12);
            }

            double firstStep  = currents[1] - currents[0];
            double secondStep = currents[2] - currents[1];
            Assert.True(secondStep > 1.5 * firstStep,
                $"the fixture must be curved, not affine: steps {firstStep:G4} then {secondStep:G4} A");

            // And it must store charge, or the batched path never carries a q at all. The
            // quantity is ~1e-11, so the comparison is relative — an absolute decimal-places
            // assertion at this magnitude passes for any value at all.
            var mid = instance.Evaluate([-1.5, 28.0, 0.0]);
            double expectedQ = Cgs * -1.5 + Cgd * (-1.5 - 28.0);
            Assert.True(Math.Abs(mid.Charge[0] - expectedQ) <= 1e-12 * Math.Abs(expectedQ),
                $"gate charge: expected {expectedQ:G17}, got {mid.Charge[0]:G17}");
        }
    }

    // ── M1_1 — Tier 5: batched and unbatched agree BIT FOR BIT ────────────────

    [FixtureFact(ModelRel, HowTo)]
    public void M1_1_BatchedAndUnbatchedExternalEvaluationAreBitIdentical()
    {
        RegisterProviders();

        var (batchedEngine,   batchedParams)   = Build(Netlist(BatchedProvider,   10.0, 5));
        var (unbatchedEngine, unbatchedParams) = Build(Netlist(UnbatchedProvider, 10.0, 5));

        var a = batchedEngine.RunSinglePoint(batchedParams);
        var b = unbatchedEngine.RunSinglePoint(unbatchedParams);

        Assert.True(a.Converged, $"batched did not converge: {a.FailReason}");
        Assert.True(b.Converged, $"unbatched did not converge: {b.FailReason}");
        Assert.Equal(b.Iterations, a.Iterations);

        // Bit-identical, not "within a tolerance". One round trip per grid and one per sample hand
        // the engine the same doubles in the same order, so anything else is a defect rather than
        // arithmetic noise — and a tolerance here would hide exactly the reordering that would be.
        int n = a.V.GetLength(0), k = a.V.GetLength(1);
        Assert.Equal(b.V.GetLength(0), n);

        bool sawSomethingLarge = false;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < k; j++)
            {
                Assert.Equal(b.V[i, j].Real,      a.V[i, j].Real);
                Assert.Equal(b.V[i, j].Imaginary, a.V[i, j].Imaginary);
                Assert.Equal(b.INl[i, j].Real,      a.INl[i, j].Real);
                Assert.Equal(b.INl[i, j].Imaginary, a.INl[i, j].Imaginary);
                if (a.V[i, j].Magnitude > 0.1) sawSomethingLarge = true;
            }

        Assert.True(sawSomethingLarge, "the solution must be non-trivial, or equality proves nothing");
    }

    // ── M1_2 — the three measurements the brief asks for ──────────────────────

    [Trait("Category", "Benchmark")]
    [FixtureFact(ModelRel, HowTo)]
    public void M1_2_CostOfAnHbSolve_Batched_Unbatched_AndBuiltIn()
    {
        RegisterProviders();

        const int Warm = 40, K = 5;

        // A 1 dB Pin step — the step a drive-up ladder or a secant search actually takes.
        var u = TimeWarmSolves(pin => Build(Netlist(UnbatchedProvider, pin, K)),  9.0,  10.0, Warm);
        var b = TimeWarmSolves(pin => Build(Netlist(BatchedProvider,   pin, K)),  9.0,  10.0, Warm);
        var i = TimeWarmSolves(pin => Build(BuiltInNetlist(pin, K)),             -7.0,  -6.0, Warm);

        _out.WriteLine($"grid samples per pass = {HbFft.GridSize(K, 1)}, K = {K}");
        _out.WriteLine($"external, UNBATCHED : {u.MsPerSolve:F3} ms/solve  ({u.Iterations} Newton iterations)");
        _out.WriteLine($"external, BATCHED   : {b.MsPerSolve:F3} ms/solve  ({b.Iterations} Newton iterations)");
        _out.WriteLine($"built-in (Hero-2 SDD): {i.MsPerSolve:F3} ms/solve  ({i.Iterations} Newton iterations)");
        _out.WriteLine($"batched speed-up    : {u.MsPerSolve / b.MsPerSolve:F1}x");
        _out.WriteLine($"batched / built-in  : {b.MsPerSolve / i.MsPerSolve:F1}x");

        Assert.True(b.MsPerSolve < u.MsPerSolve,
            $"batching must not be slower: {b.MsPerSolve:F3} ms vs {u.MsPerSolve:F3} ms");
    }

    // ── the unbatched provider ────────────────────────────────────────────────

    /// <summary>
    /// Wraps a provider so its instances expose only the scalar <c>Evaluate</c>. Everything else is
    /// forwarded. What this reproduces is a provider that never implemented batching — which is what
    /// the interface's default scalar loop is for, and what the engine's per-sample call did before
    /// this change.
    /// </summary>
    private sealed class Unbatched(DeviceWorkerProvider inner) : IExternalDeviceProvider, IDisposable
    {
        public string Name => inner.Name;

        public IReadOnlyList<ExternalDeviceDescriptor> Describe() => inner.Describe();

        public IExternalDeviceInstance Create(string typeId, IReadOnlyDictionary<string, string> parameters)
            => new ScalarOnly(inner.Create(typeId, parameters));

        public void Dispose() => inner.Dispose();

        private sealed class ScalarOnly(IExternalDeviceInstance inner) : IExternalDeviceInstance
        {
            public ExternalDeviceDescriptor Descriptor => inner.Descriptor;

            public ExternalDeviceEvaluation Evaluate(IReadOnlyList<double> nodeVoltages)
                => inner.Evaluate(nodeVoltages);

            // EvaluateBatch is deliberately NOT overridden — the interface default loops.

            public void Dispose() => inner.Dispose();
        }
    }
}
