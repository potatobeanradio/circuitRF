using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Devices.External;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using CircuitRF.Harmonica.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Harmonica.Tests;

/// <summary>
/// Where harmonicaRF's grid build spends its time, per device kind (brief LP-0): a 37-point ring grid built serially and
/// on the solve pool, and one warm <see cref="HarmonicaContext.Solve"/> split into the algebraic closure, the Newton
/// solve and the device evaluation it repeats after the solve. Clocks only; nothing about time is asserted.
/// </summary>
[Collection("HarmonicaBenchmarks")]
public sealed class HarmonicaCostTests(ITestOutputHelper output) : IDisposable
{
    private const string Provider = "HrCost";

    public void Dispose() => ExternalDeviceRegistry.Clear();

    internal static HarmonicaSettings Settings => new()
    {
        HarmonicCount = 4, FrequencyHz = 2e9, BiasChokeHenries = 1e-6, DcBlockFarads = 1e-9, Tol = 1e-8,
        CompressionDb = 3.0, PinStartDbm = -10, PinMaxDbm = 34, PinStepDbm = 1.0,
    };

    internal static CircuitModel Model(string kind) => kind switch
    {
        "sdd" => new CircuitModel
        {
            Dut = new DutSpec
            {
                Kind = DutKind.Sdd, TypeName = "SDD",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["I[1,0]"] = "_v1/50",
                    ["I[2,0]"] = "(1130*1.507*tanh(_v2*0.176*(tanh(0.089*(4.268-_v1+_v2*0.001+0.71*ln(exp(-(-0.837-_v1)/0.71)+1)))+1))" +
                                 "*ln(exp(-(2*4.268-2*_v1+2*_v2*0.001+2*0.71*ln(exp(-(-0.837-_v1)/0.71)+1))/1.507)+1)*(_v2*0.0012+1))/2",
                },
            },
            Bias = new BiasSpec { Vgs = -3.05, Vds = 48 }, Settings = Settings,
        },
        "native" => new CircuitModel
        {
            Dut = new DutSpec
            {
                Kind = DutKind.NativeFet, TypeName = "FET_Angelov",
                Parameters = new Dictionary<string, string> { ["Cgs"] = "1.2e-12", ["Cgd"] = "0.15e-12" },
            },
            Bias = new BiasSpec { Vgs = -0.4, Vds = 10.0 }, Settings = Settings with { PinStartDbm = -20, PinMaxDbm = 20 },
        },
        // The fixture's own settings (a 1e-8 A tolerance is under this model's residual floor), at loadpull's drive range.
        "composed" => ComposedFixtures.Model() with
        {
            Settings = ComposedFixtures.Model().Settings with { HarmonicCount = 5, CompressionDb = 3.0, PinStartDbm = -4, PinMaxDbm = 26 },
        },
        "external" => new CircuitModel
        {
            Dut = new DutSpec
            {
                Kind = DutKind.External, TypeName = "crf_fet", Provider = Provider,
                Parameters = new Dictionary<string, string>
                {
                    ["beta"] = "0.06", ["vth"] = "-2.5", ["lambda"] = "0.02", ["alpha"] = "1.5", ["delta"] = "0.2",
                    ["cgs"] = "2e-12", ["cgd"] = "0.2e-12", ["ggs"] = "1e-6",
                },
            },
            Bias = new BiasSpec { Vgs = -1.5, Vds = 28 }, Settings = Settings with { PinMaxDbm = 30 },
        },
        _ => throw new ArgumentException(kind),
    };

    [Trait("Category", "Benchmark")]
    [Theory]
    [InlineData("sdd")]
    [InlineData("native")]
    [InlineData("composed")]
    [InlineData("external")]
    public void M0_WhereAGridBuildsTimeGoes(string kind)
    {
        if (kind == "external")
        {
            if (!FixturePaths.Exists("tools/osdi-worker/osdi-worker")) return;
            ExternalDeviceRegistry.Clear();
            ExternalDeviceRegistry.Register(DeviceWorkerProvider.Launch(Provider, FixturePaths.Require("tools/osdi-worker/osdi-worker"),
                                                                        [FixturePaths.Require("tools/fake-osdi-model/fake_osdi.osdi")]));
        }
        var model = Model(kind);
        var settings = HarmonicaContext.DefaultSettings(model);
        var sw = Stopwatch.StartNew();
        var ctx = HarmonicaContext.Create(model, settings);
        double tCreate = sw.Elapsed.TotalMilliseconds;
        int K = model.Settings.HarmonicCount;
        var terms = new TerminationSet(K);
        terms.Set(TerminationSide.Source, 1, new Complex(kind == "sdd" ? 25 : 50, 0));
        var ring = ContourGrid.RingGrid(3, 12, 0.6);

        new ContourGrid().Build(ctx, terms, ring);          // JIT
        sw.Restart();
        var serial = new ContourGrid();
        serial.Build(ctx, terms, ring);
        double tSerial = sw.Elapsed.TotalSeconds;
        sw.Restart();
        var pooled = new ContourGrid();
        pooled.BuildParallel(ctx, terms, ring);
        double tPool = sw.Elapsed.TotalSeconds;

        // One warm solve, split. Solve no longer evaluates the devices again after Newton (LP-1); what that evaluation cost
        // is timed on its own, against the solve it was part of.
        double pin = model.Settings.PinStartDbm + 5;
        var seed = ctx.Solve(terms, pin - 1);
        int N = ctx.Interface.InterfaceCount, gridN = HbFft.GridSize(K, model.Settings.FftOverSample);
        double tSolve = 1e9, tNewton = 1e9, tEval = 1e9;
        OperatingPoint pt = seed;
        for (int rep = 0; rep < 50; rep++)
        {
            sw.Restart();
            pt = ctx.Solve(terms, pin, seed.V);
            tSolve = Math.Min(tSolve, sw.Elapsed.TotalMilliseconds);
            var v = (Complex[,])seed.V.Clone();
            sw.Restart();
            HbNewton.Solve(v, pt.YNN, pt.ISrc, model.Settings.FrequencyHz, K, N, ctx.Netlist, ctx.Interface.DeviceNodes, gridN,
                           settings, ctx.EffectiveTol, model.Settings.Lambda, model.Settings.GuardHarmonic);
            tNewton = Math.Min(tNewton, sw.Elapsed.TotalMilliseconds);
            sw.Restart();
            HbNewton.EvaluateNonlinear(v, N, K, gridN, ctx.Netlist, ctx.Interface.DeviceNodes);
            tEval = Math.Min(tEval, sw.Elapsed.TotalMilliseconds);
        }
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[{kind}] {ring.Length} points, context {tCreate:F0} ms; serial build {tSerial:F2} s ({serial.SolveCount} solves, " +
            $"{tSerial * 1e3 / Math.Max(1, serial.SolveCount):F3} ms/solve, {serial.HoleCount} holes: {serial.PinMaxHoleCount} PinMax, " +
            $"{serial.NonConvergenceHoleCount} non-convergence); pool ({SolvePool<object>.DefaultWorkerCount} workers) {tPool:F2} s = " +
            $"{tSerial / tPool:F2}x"));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[{kind}] one warm Solve {tSolve:F3} ms ({pt.Iterations} iterations) = Newton {tNewton:F3} ms + closure and the rest " +
            $"{Math.Max(0, tSolve - tNewton):F3} ms; the post-solve evaluation it no longer makes {tEval:F3} ms = " +
            $"{100 * tEval / (tSolve + tEval):F0} % of the solve it was part of"));
    }
}
