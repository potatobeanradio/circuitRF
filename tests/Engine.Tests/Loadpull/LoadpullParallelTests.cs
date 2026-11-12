using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Loadpull;
using RfCore.Data;
using RfCore.Loadpull;
using Xunit;

namespace CircuitRF.Engine.Tests.Loadpull;

/// <summary>
/// LP-1 — a loadpull's loads run side by side and the result is the serial loop's, BIT FOR BIT, at any worker count: every
/// cube, the warnings and notes in their order, and the <c>.spl</c>/<c>.lpcwave</c> a user exports. The serial run is
/// <c>MaxParallelism=1</c>, which takes the loop every caller had before. See <c>LoadpullEngine.RunParallel</c> for why
/// exact is possible: a load seeds from an EARLIER load's first rung only, and that rung is published the moment it is
/// solved.
/// </summary>
[Collection("ExternalDeviceRegistry")]
public sealed class LoadpullParallelTests : IDisposable
{
    public void Dispose() => ExternalDeviceRegistry.Clear();

    internal sealed record Outcome(DataSet Ds, string[] Warnings, string[] Notes, int Degree);

    internal static string Hero3(string file) => LoadpullCostTests.Find("testdata/Hero3/" + file);

    /// <summary>One loadpull run at <paramref name="workers"/> (1 = the serial loop), on a fresh elaboration.</summary>
    internal static Outcome Run(Func<(Library Lib, TestBench Tb)> read, int workers, double inductanceRegR = 1e-6,
                                string? dir = null)
    {
        var (lib, tb) = read();
        using var nl = new Elaborator(lib) { BaseDirectory = dir }.Elaborate(tb);
        var settings = new AnalysisSettings { MaxParallelism = workers, InductanceRegR = inductanceRegR };
        DataSet ds;
        if (tb.Analyses.OfType<LoadpullPursuitAnalysis>().FirstOrDefault() is { } lpp)
            ds = new LoadpullPursuitEngine(new LoadpullEngine(nl, tb, settings, lib, dir))
                     .Run(LoadpullPursuitEngine.Resolve(lpp, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit));
        else
        {
            var lpa = tb.Analyses.OfType<LoadpullAnalysis>().First();
            ds = new LoadpullEngine(nl, tb, settings, lib, dir).Run(LoadpullEngine.Resolve(lpa, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit));
        }
        return new Outcome(ds, [.. nl.Warnings], [.. nl.Notes], LoadpullEngine.LastLoadpullDegree);
    }

    internal static void AssertSame(Outcome serial, Outcome parallel)
    {
        Assert.Equal(serial.Ds.Cubes.Keys, parallel.Ds.Cubes.Keys);
        foreach (var (name, a) in serial.Ds.Cubes)
        {
            var b = parallel.Ds.Cubes[name];
            Assert.Equal(a.DataKind, b.DataKind);
            Assert.Equal(a.Axes.Select(x => x.Name), b.Axes.Select(x => x.Name));
            for (int i = 0; i < a.Axes.Count; i++)
            {
                Assert.Equal(Bits(a.Axes[i].Values), Bits(b.Axes[i].Values));
                Assert.Equal(a.Axes[i].Labels, b.Axes[i].Labels);
            }
            if (a.DataKind == DataKind.Complex)
                Assert.True(Bits(a.ComplexValues.SelectMany(c => new[] { c.Real, c.Imaginary }))
                            .SequenceEqual(Bits(b.ComplexValues.SelectMany(c => new[] { c.Real, c.Imaginary }))), $"cube {name} differs");
            else
                Assert.True(Bits(a.RealValues).SequenceEqual(Bits(b.RealValues)), $"cube {name} differs");
        }
        Assert.Equal(serial.Warnings, parallel.Warnings);
        Assert.Equal(serial.Notes, parallel.Notes);
    }

    private static long[] Bits(IEnumerable<double> v) => [.. v.Select(BitConverter.DoubleToInt64Bits)];

    /// <summary>The serial run and the same bench at 2, 3 and 8 workers.</summary>
    private static Outcome Gate(Func<(Library, TestBench)> read, double inductanceRegR = 1e-6, string? dir = null)
    {
        var serial = Run(read, 1, inductanceRegR, dir);
        Assert.Equal(1, serial.Degree);
        foreach (int w in new[] { 2, 3, 8 })
        {
            var parallel = Run(read, w, inductanceRegR, dir);
            Assert.True(parallel.Degree > 1, $"{w} workers ran serially");
            AssertSame(serial, parallel);
        }
        return serial;
    }

    [Theory]
    [InlineData("hero3.cnl")]
    [InlineData("hero3_classF.cnl")]
    [InlineData("hero3_classF_active.cnl")]
    [InlineData("RLSweep.cnl")]
    public void TheHeroBenches_AreBitIdenticalAtAnyWorkerCount(string file)
        => Gate(() => CnlReader.ReadFile(Hero3(file)), dir: Path.GetDirectoryName(Hero3(file)));

    /// <summary>
    /// A load whose first rung does not converge: it seeds nobody, and the loads after it must wait for that answer exactly
    /// as for a converged one. On Hero 3 a cold 20 dBm first rung into a near-open load fails in ten iterations, and the
    /// loads around it, cold or seeded, converge.
    /// </summary>
    [Fact]
    public void ALoadWhoseFirstRungFails_IsBitIdenticalToo()
    {
        double[] mag = [0.995, 0, 0.995, 0.2, 1.5, 0.4, 0.995, 0.6, 0.4], deg = [0, 0, 180, 0, 0, 90, 90, 180, 270];
        string gam = LoadpullCostTests.Gam(mag.Select((m, i) => Complex.FromPolarCoordinates(m, deg[i] * Math.PI / 180)));
        string cnl = File.ReadAllText(Hero3("hero3.cnl"))
                         .Replace("Grid=\"hero3_load.gam\"", $"Grid=\"{gam}\"").Replace("Tickle=-50", "Tickle=off")
                         .Replace("PinStart=-20", "PinStart=20").Replace("PinMax=10", "PinMax=25").Replace("MaxIter=100", "MaxIter=10");
        var serial = Gate(() => new CnlReader().Read(cnl));
        var stop = serial.Ds["StopCode"].RealValues;
        var conv = serial.Ds["Converged"].RealValues;
        int nP = serial.Ds["Converged"].Axes[1].Length;
        Assert.True(stop[0] == 2 && conv[0] == 0, $"stop codes {string.Join(",", stop)}");   // load 0 fails its first rung
        Assert.All(Enumerable.Range(1, stop.Length - 1), gi => Assert.Equal(1, conv[gi * nP]));
    }

    /// <summary>Tuners behind a linear network (the off-device interface path) and NF at every rung (the per-solve noise
    /// read), on the benches of <see cref="LoadpullOffDeviceTunerTests"/> and <see cref="LoadpullNoiseTests"/>.</summary>
    [Fact]
    public void OffDeviceTunersAndNoise_AreBitIdenticalToo()
    {
        string gam = LoadpullCostTests.Gam([0, new(0.3, 0.1), new(-0.2, 0.3), new(0.1, -0.4), new(-0.3, -0.2)]);
        string offDevice =
            "define MyFET (gate drain)\n  B = 1130\n" +
            "  SDD:X1  gate  0  drain  0  I[1,0]=_v1/5000  " +
            "I[2,0]=(B*1.507*tanh(_v2*0.176*(tanh(0.089*(4.268 - _v1 + _v2*0.001 + 0.71*ln(exp(-(-0.837 - _v1)/0.71) + 1)))+1))*" +
            "ln(exp(-(2*4.268 - 2*_v1 +2*_v2*0.001 + 2*0.71*ln(exp(-(-0.837 - _v1)/0.71) + 1))/1.507) + 1) * (_v2*0.0012 + 1))/2\n" +
            "end MyFET\n\nRFfreq = 2.2 GHz\nVgs = -3.05\nVDD = 48\n\n" +
            "MyFET:X1  n1  n2\nR:RG n1 tg R=1e-6 Ohm\nR:RD n2 td R=1e-6 Ohm\n" +
            "Tuner:Src  tg  0  Zdefault=1e-6  Z0=50  BiasTee=on  Vbias=Vgs  Z[1]=50\n" +
            "Tuner:Load  td  0  Zdefault=1e-6  Z0=50  BiasTee=on  Vbias=VDD  Z[1]=50\n" +
            $"analysis LP1 type=loadpull Tone=\"RFfreq\" ToneUnit=GHz MaxHarm=3 LoadTuner=Load SourceTuner=Src Grid=\"{gam}\" " +
            "Sweep=Load TuneHarm=1 Compression=3 GainType=Gt PinStart=-5 PinStep=2.5 PinMax=10 MaxIter=100 Tol=1e-7\n";
        Gate(() => new CnlReader().Read(offDevice));

        string noisy = """
            C:C1  in b  C=20e-12
            R:RB  vcc b  R=150e3
            BJT_NPN:Q1  c b 0  Is=1e-15 Bf=100 Rb=20 Kf=1e-14 Cje=0.5e-12 Cjc=0.2e-12
            L:LC  vcc c  L=100e-9
            R:RC  vcc c  R=400
            C:C2  c out  C=20e-12
            Vdc:V1  vcc 0  Vdc=3
            Tuner:TS  in 0   Zdefault=50 Z0=50 BiasTee=on Vbias=0 Z[1]=50
            Tuner:TL  out 0  Zdefault=50 Z0=50 BiasTee=on Vbias=0 Z[1]=50
            """ + $"\nanalysis LP1 type=loadpull Tone=1e9 MaxHarm=3 LoadTuner=TL SourceTuner=TS Grid=\"{gam}\" Sweep=Source " +
            "PinStart=-130 PinStep=10 PinMax=-120 Tickle=off Compression=3 Tol=1e-12 Noise=yes";
        var serial = Gate(() => new CnlReader().Read(noisy), inductanceRegR: 1e-3);
        Assert.True(serial.Ds.Contains("NF"));
    }

    /// <summary>A pursuit's follow-on loadpull runs in parallel with the pursuit's Zsource on the source tuner of every
    /// worker — set on the caller's tuner only, it would be lost on the copies and every follow-on number would move.</summary>
    [Fact]
    public void APursuitsFollowOn_IsBitIdenticalToo()
    {
        // The recommended-terminations grid goes to a temp file, not beside the committed bench.
        string gam = Path.Combine(Directory.CreateTempSubdirectory("crf-lp1-").FullName, "out.gam");
        string cnl = File.ReadAllText(LoadpullCostTests.Find("testdata/Hero3B/hero3B_at_compression.cnl"))
                         .Replace("OutputGrid=\"loadpull_pursuit_output.gam\"", $"OutputGrid=\"{gam}\"");
        Assert.Contains(gam, cnl);
        var serial = Run(() => new CnlReader().Read(cnl), 1);
        var parallel = Run(() => new CnlReader().Read(cnl), 4);
        Assert.Equal(4, parallel.Degree);
        Assert.True(serial.Ds.Contains("GammaLoad"), "the follow-on loadpull ran");
        AssertSame(serial, parallel);
    }

    /// <summary>What a user exports — the <c>.spl</c> and <c>.lpcwave</c> of the enriched result — byte for byte.</summary>
    [Fact]
    public void TheExports_AreByteIdentical()
    {
        static (string Spl, string Wave) Export(DataSet ds)
        {
            var e = LoadpullPostProcessor.Enrich(ds);
            var spl = new StringWriter();
            var wave = new StringWriter();
            SplWriter.WriteSpl(e, spl);
            LpcwaveWriter.WriteLpcwave(e, wave);
            return (spl.ToString(), wave.ToString());
        }
        string dir = Path.GetDirectoryName(Hero3("hero3.cnl"))!;
        var serial = Export(Run(() => CnlReader.ReadFile(Hero3("hero3.cnl")), 1, dir: dir).Ds);
        var parallel = Export(Run(() => CnlReader.ReadFile(Hero3("hero3.cnl")), 8, dir: dir).Ds);
        Assert.Equal(serial.Spl, parallel.Spl);
        Assert.Equal(serial.Wave, parallel.Wave);
        Assert.True(serial.Spl.Length > 1000 && serial.Wave.Length > 1000);
    }

    /// <summary>A swept loadpull's points each run their grid in parallel: the sweep engine hands the engine its library.</summary>
    [Fact]
    public void ASweptLoadpull_RunsEachPointsGridInParallel()
    {
        DataSet Sweep(int workers)
        {
            var (lib, tb) = CnlReader.ReadFile(Hero3("hero3.cnl"));
            var sw = new ParametricSweepAnalysis("SW1", "RFfreq", [1.9e9, 2.1e9], "LP1");
            return ParametricSweepEngine.Run(sw, lib, tb, new AnalysisSettings { MaxParallelism = workers },
                                             baseDirectory: Path.GetDirectoryName(Hero3("hero3.cnl")));
        }
        var serial = Sweep(1);
        Assert.Equal(1, LoadpullEngine.LastLoadpullDegree);
        var parallel = Sweep(4);
        Assert.Equal(4, LoadpullEngine.LastLoadpullDegree);
        AssertSame(new Outcome(serial, [], [], 1), new Outcome(parallel, [], [], 4));
    }

    /// <summary><c>Workers=</c> on the directive caps the degree over the settings' default, and is written back only when
    /// set, so a line without it round-trips to the bytes it always did.</summary>
    [Fact]
    public void TheWorkersKey_CapsTheDegree_AndRoundTrips()
    {
        string With(string key) => File.ReadAllText(Hero3("hero3.cnl"))
            .Replace("Grid=\"hero3_load.gam\"", $"Grid=\"{Hero3("hero3_load.gam")}\"{key}");
        var (_, tb) = new CnlReader().Read(With(" Workers=3"));
        Assert.Equal("3", tb.Analyses.OfType<LoadpullAnalysis>().Single().WorkersExpr);
        Assert.Contains(" Workers=3", CnlWriter.Write(tb), StringComparison.Ordinal);
        Assert.DoesNotContain("Workers", CnlWriter.Write(new CnlReader().Read(With("")).Item2), StringComparison.Ordinal);

        var three = Run(() => new CnlReader().Read(With(" Workers=3")), workers: 0);
        Assert.Equal(3, three.Degree);
        var one = Run(() => new CnlReader().Read(With(" Workers=1")), workers: 0);
        Assert.Equal(1, one.Degree);
        AssertSame(one, three);
    }

    [Fact]
    public void PlanDegree_IsSerialWithoutALibraryAtOneWorkerOrOneLoad()
    {
        var (lib, tb) = CnlReader.ReadFile(Hero3("hero3.cnl"));
        using var nl = new Elaborator(lib).Elaborate(tb);
        Assert.Equal(1, new LoadpullEngine(nl, tb).PlanDegree(20));
        Assert.Equal(1, new LoadpullEngine(nl, tb, new AnalysisSettings { MaxParallelism = 1 }, lib).PlanDegree(20));
        Assert.Equal(1, new LoadpullEngine(nl, tb, null, lib).PlanDegree(1));
        Assert.Equal(5, new LoadpullEngine(nl, tb, new AnalysisSettings { MaxParallelism = 5 }, lib).PlanDegree(20));
        Assert.Equal(Math.Min(Environment.ProcessorCount, 20), new LoadpullEngine(nl, tb, null, lib).PlanDegree(20));
    }

    /// <summary>An external device is one worker process whose channel serialises every call: its loads stay serial.</summary>
    [FixtureFact("tools/osdi-worker/osdi-worker", "run tools/osdi-worker/build.sh (needs a C compiler)")]
    public void PlanDegree_IsSerialForAnExternalDevice()
    {
        var (lib, tb) = LoadpullCostTests.Bench("external");
        using var nl = new Elaborator(lib).Elaborate(tb);
        Assert.Equal(1, new LoadpullEngine(nl, tb, new AnalysisSettings { MaxParallelism = 8 }, lib).PlanDegree(20));
    }
}
