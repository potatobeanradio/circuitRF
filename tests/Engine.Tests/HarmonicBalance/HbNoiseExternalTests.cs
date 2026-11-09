using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// Brief NZ-5 gate 5: a compiled device's noise in an HB noise run is read once per instance per operating point, every time
/// sample in one request — and what that one request carries is the same noise the S-parameter analysis reads one bias at
/// a time, in the small-signal limit. The device is an in-process provider that counts what it is asked: a shunt
/// <c>i = g·v + a·v²</c> with shot noise <c>2q|i|</c>.
/// </summary>
[Collection("ExternalDeviceRegistry")]
public sealed class HbNoiseExternalTests : IDisposable
{
    private const string Provider = "nz5-shot";
    private readonly ITestOutputHelper _output;

    public HbNoiseExternalTests(ITestOutputHelper output)
    {
        _output = output;
        ExternalDeviceRegistry.Clear();
        ShotProvider.Created.Clear();
        ExternalDeviceRegistry.Register(new ShotProvider(Provider));
    }

    public void Dispose() => ExternalDeviceRegistry.Clear();

    private sealed class ShotProvider(string name) : IExternalDeviceProvider
    {
        public const string TypeName = "Shot";
        public string Name { get; } = name;
        public static readonly List<Instance> Created = [];

        public static readonly ExternalDeviceDescriptor TypeDescriptor = new(
            TypeId: TypeName, DisplayName: "Shot (synthetic)", ExternalPinCount: 1, InternalNodeCount: 0,
            Parameters:
            [
                new ExternalParamDescriptor("G", ExternalParamKind.Double, "0.01", "S"),
                new ExternalParamDescriptor("A", ExternalParamKind.Double, "0.005", "A/V2"),
            ],
            Nodes: [new ExternalNodeDescriptor(0, External: true, NodeQuantityKind.Electrical, "p")],
            NoiseSources: [new ExternalNoiseSourceDescriptor("shot", 0, -1)]);

        public IReadOnlyList<ExternalDeviceDescriptor> Describe() => [TypeDescriptor];

        public IExternalDeviceInstance Create(string typeId, IReadOnlyDictionary<string, string> parameters)
        {
            var i = new Instance(parameters);
            Created.Add(i);
            return i;
        }

        public sealed class Instance(IReadOnlyDictionary<string, string> p) : IExternalDeviceInstance
        {
            private readonly double _g = Get(p, "G", 0.01), _a = Get(p, "A", 0.005);
            public int SingleBias, Batches;

            private static double Get(IReadOnlyDictionary<string, string> p, string k, double d) =>
                p.TryGetValue(k, out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;

            public ExternalDeviceDescriptor Descriptor => TypeDescriptor;

            public ExternalDeviceEvaluation Evaluate(IReadOnlyList<double> v) => new(
                [_g * v[0] + _a * v[0] * v[0]], [0.0], new[,] { { _g + 2 * _a * v[0] } }, new double[1, 1]);

            private double Density(double v) => 2 * 1.602176634e-19 * Math.Abs(_g * v + _a * v * v);

            public double[,]? EvaluateNoise(IReadOnlyList<double> v, IReadOnlyList<double> hz)
            {
                SingleBias++;
                var d = new double[hz.Count, 1];
                for (int k = 0; k < hz.Count; k++) d[k, 0] = Density(v[0]);
                return d;
            }

            public double[,,]? EvaluateNoiseBatch(IReadOnlyList<IReadOnlyList<double>> v, IReadOnlyList<double> hz)
            {
                Batches++;
                var d = new double[v.Count, hz.Count, 1];
                for (int t = 0; t < v.Count; t++)
                    for (int k = 0; k < hz.Count; k++) d[t, k, 0] = Density(v[t][0]);
                return d;
            }

            public void Dispose() { }
        }
    }

    private static string Bench(double pavl, string analysis) => $"""
        P1Tone:P1  in 0  Num=1 Z=50 Ohm Pavl={pavl.ToString("R", CultureInfo.InvariantCulture)} dBm Freq=1e9
        L:L1  v in  L=1e-3
        Vdc:V1  v 0  Vdc=1
        ExtDevice:X1  in  Provider={Provider} Type={ShotProvider.TypeName}
        C:C1  in out  C=1e-9
        L:L2  v out  L=1e-3
        ExtDevice:X2  out  Provider={Provider} Type={ShotProvider.TypeName} G=0.02
        Port:P2  out 0  Num=2 Z=50 Ohm
        {analysis}
        """;

    private static DataSet Hb(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl, "tb");
        var nl = new Elaborator(lib).Elaborate(tb);
        var p = HbEngine.Resolve(tb.Analyses.OfType<HarmonicBalanceAnalysis>().Single(), nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
        return new HbEngine(nl, tb).Run(p).DataSet;
    }

    /// <summary>Driven hard, each instance is asked once — one batch with every sample — and never one bias at a time.</summary>
    [Fact]
    public void Gate5_OneBatchPerInstancePerOperatingPoint()
    {
        var ds = Hb(Bench(10, "analysis HB1 type=hb Tone=1e9 MaxHarm=5 Tol=1e-9 SSStart=0.37 SSStop=1.37 SSNpts=5 SSUnit=GHz Noise=yes"));
        Assert.True(ds.Contains("NF"));
        Assert.Equal(2, ShotProvider.Created.Count);
        foreach (var i in ShotProvider.Created)
        {
            _output.WriteLine($"batches {i.Batches}, single-bias {i.SingleBias}");
            Assert.Equal(1, i.Batches);
            Assert.Equal(0, i.SingleBias);
        }
    }

    /// <summary>At −200 dBm the batch carries the same noise the S-parameter analysis reads one bias at a time: NF to
    /// 1e-6 dB, the S-parameter DC pre-pass converged as gate 1's is.</summary>
    [Fact]
    public void BatchedNoise_InTheSmallSignalLimit_EqualsSParameterNoise()
    {
        var hb = Hb(Bench(-200, "analysis HB1 type=hb Tone=1e9 MaxHarm=3 Tol=1e-9 SSStart=0.37 SSStop=1.37 SSNpts=3 SSUnit=GHz Noise=yes"));
        var (lib, tb) = new CnlReader().Read(Bench(-200, "analysis SP1 type=sparam start=0.37 stop=1.37 npts=3 Unit=GHz Noise=yes"), "tb");
        var nl = new Elaborator(lib).Elaborate(tb);
        var sp = SParameterEngine.Run(nl, tb.Analyses.OfType<SParameterAnalysis>().Single().Expand(nl.ResolvedGlobals),
            new AnalysisSettings { NonlinearAbsTol = 1e-14 }.WithNoise(true));
        for (int k = 0; k < 3; k++)
        {
            _output.WriteLine($"HB {hb["NF"].RealValues[k]:R} dB, S-parameter {sp["NF"].RealValues[k]:R} dB");
            Assert.Equal(sp["NF"].RealValues[k], hb["NF"].RealValues[k], 1e-6);
        }
    }
}
