using System.Globalization;
using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Tests.External;
using RfCore.Data;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Linear;

/// <summary>
/// Brief NZ-4: noise sources in the built-in nonlinear models, on the one seam (<see cref="ComponentModel.NoiseSources"/>).
/// Each source kind against its closed form at a stated bias, the in-process seam against the OSDI worker's on the same
/// sources, a composed FET's parasitics at its self-heated temperature, and a device with no noise keys left as it was.
/// </summary>
[Collection("ExternalDeviceRegistry")]
public sealed class DeviceNoiseTests(ITestOutputHelper output)
{
    private const double K = NoiseMetrics.Boltzmann, Q = 1.602176634e-19, Z0 = 50, KT0 = NoiseMetrics.KT0;

    private static string N(double x) => x.ToString("R", CultureInfo.InvariantCulture);

    private static ElaboratedNetlist Elaborate(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        return new Elaborator(lib).Elaborate(tb);
    }

    private static DataSet Run(ElaboratedNetlist nl, double[] hz) =>
        SParameterEngine.Run(nl, hz, AnalysisSettings.Default.WithNoise(true));

    /// <summary>A shunt device at node x between two 50 Ω ports, fed from Vdc through 1 H and blocked by 1 F: a shunt noise
    /// current of density S at x gives F − 1 = S·Z0/(4kT0), whatever the device's conductance.</summary>
    private static string Shunt(double vdc, string device) => $"""
        temp = 26.85
        Port:P1  p1 0  Num=1 Z=50 Ohm
        Port:P2  p2 0  Num=2 Z=50 Ohm
        C:C1  p1 x  C=1 F
        C:C2  x p2  C=1 F
        L:L1  v x  L=1 H
        Vdc:V1  v 0  Vdc={N(vdc)}
        {device}
        """;

    private static double Vt => Temperature.ThermalVoltage(300.0);

    // ── gate 1: closed forms ──────────────────────────────────────────────────────

    /// <summary>A forward-biased diode across the port: shot noise 2q·Id, and with Kf a flicker term Kf·Id/f, so F − 1 falls
    /// by the flicker term's tenfold per decade (−10 dB/decade at Af = 1).</summary>
    [Fact]
    public void Diode_ShotAndFlicker_MeetTheClosedForm()
    {
        const double Is = 1e-14, V = 0.6, Kf = 1e-12;
        double id = Is * (Math.Exp(V / Vt) - 1);
        double[] hz = [1e3, 1e4];
        var ds = Run(Elaborate(Shunt(V, $"Diode:D1  x 0  Is={N(Is)} Kf={N(Kf)}")), hz);
        for (int k = 0; k < hz.Length; k++)
        {
            double expected = (2 * Q * id + Kf * id / hz[k]) * Z0 / (4 * KT0);
            double got = ds["Fn"].RealValues[k] - 1;
            output.WriteLine($"{hz[k]} Hz: F − 1 = {got:R}, closed form {expected:R}");
            Assert.Equal(1.0, got / expected, 1e-9);
        }
        double flicker(int k) => ds["Fn"].RealValues[k] - 1 - 2 * Q * id * Z0 / (4 * KT0);
        Assert.Equal(10.0, flicker(0) / flicker(1), 1e-6);
    }

    /// <summary>The BJT's collector shot 2q·|Ic| and base shot 2q·|Ib| at a forward-active bias, Ic and Ib from the
    /// transport equations with no Early effect or high injection.</summary>
    [Fact]
    public void Bjt_CollectorAndBaseShot_MeetTheClosedForm()
    {
        const double Is = 1e-15, Bf = 100, Br = 1, Vbe = 0.7, Vbc = -2;
        var nl = Elaborate($"BJT_NPN:Q1  c b e  Is={N(Is)} Bf={N(Bf)} Br={N(Br)} Nf=1 Nr=1 Vaf=0 Var=0 Ikf=0 Ikr=0 Ise=0 Isc=0 Rb=0 Re=0 Rc=0 Xtb=0");
        var m = nl.Components.Single().Model;
        double icc = Is * (Math.Exp(Vbe / Vt) - 1), iec = Is * (Math.Exp(Vbc / Vt) - 1);
        double ic = (icc - iec) - iec / Br, ib = icc / Bf + iec / Br;
        var d = new NoiseDensity[m.NoiseSources.Count];
        m.EvaluateNoise(new PortVoltages([Vbe, Vbc, Vbe - Vbc, Vbc]), 300, d);
        Assert.Equal(["shot_base", "shot_collector"], m.NoiseSources.Select(s => s.Name));
        Assert.Equal(1.0, d[1].White / (2 * Q * Math.Abs(ic)), 1e-9);
        Assert.Equal(1.0, d[0].White / (2 * Q * Math.Abs(ib)), 1e-9);
    }

    /// <summary>A level-1 MOSFET in saturation: channel thermal (8/3)kT·gm, gm = Kp·(W/L)·(Vgs − Vto).</summary>
    [Fact]
    public void Mosfet_ChannelThermal_IsEightThirdsKTgm()
    {
        const double Kp = 2e-4, W = 10e-6, L = 1e-6, Vto = 0.7, Vgs = 1.5, Vds = 3;
        var nl = Elaborate($"MOS1_N:M1  d g s b  Kp={N(Kp)} W={N(W)} L={N(L)} Vto={N(Vto)}");
        var m = nl.Components.Single().Model;
        var d = new NoiseDensity[m.NoiseSources.Count];
        // Ports: (d,s), (b,s), (b,d), (g,s), (g,d), (g,b).
        m.EvaluateNoise(new PortVoltages([Vds, 0, -Vds, Vgs, Vgs - Vds, Vgs]), 300, d);
        double gm = Kp * W / L * (Vgs - Vto);
        Assert.Equal(1.0, d[0].White / (8.0 / 3 * K * 300 * gm), 1e-9);
    }

    /// <summary>A Curtice FET with only Td: the output noise current is 4k·Td·gds, gds from the law's own derivative.</summary>
    [Fact]
    public void Fet_DrainTemperatureOnly_Is4kTdGds()
    {
        const double Vto = -2, Beta = 0.02, Lambda = 0.05, Alpha = 2, Td = 2500, Vgs = -1, Vds = 1.2;
        var nl = Elaborate($"FET_CURTICE:F1  g d s  Vto={N(Vto)} Beta={N(Beta)} Lambda={N(Lambda)} Alpha={N(Alpha)} Td={N(Td)}");
        var m = nl.Components.Single().Model;
        Assert.Equal(["thermal_gds"], m.NoiseSources.Select(s => s.Name));
        var d = new NoiseDensity[1];
        m.EvaluateNoise(new PortVoltages([Vgs, Vds]), 300, d);
        double vg = Vgs - Vto, th = Math.Tanh(Alpha * Vds);
        double gds = Beta * vg * vg * (Lambda * th + (1 + Lambda * Vds) * Alpha * (1 - th * th));
        Assert.Equal(1.0, d[0].White / (4 * K * Td * gds), 1e-9);
    }

    /// <summary>Ri on a FET is a real resistance on an internal node, thermal at Tg: the gate side's noise current is
    /// 4k·Tg/Ri.</summary>
    [Fact]
    public void Fet_Ri_IsThermalAtTg()
    {
        var nl = Elaborate("FET_STATZ:F1  g d s  Ri=4 Ohm Tg=400");
        var ec = nl.Components.Single();
        Assert.Equal(8, ec.Nodes.Length);
        Assert.Equal(["thermal_ri"], ec.Model.NoiseSources.Select(s => s.Name));
        var d = new NoiseDensity[1];
        ec.Model.EvaluateNoise(new PortVoltages([0, -1, 2, -3]), 300, d);
        Assert.Equal(1.0, d[0].White / (4 * K * 400 / 4.0), 1e-12);
    }

    // ── gate 2: the seam against the worker ───────────────────────────────────────

    /// <summary>crf_noise's sources — thermal 4kTg, shot 2q|g·v| and flicker kf|g·v|/f between its terminals — written as an
    /// in-process model.</summary>
    private sealed class Conductance(double g, double kf) : ComponentModel
    {
        public override int PortCount => 1;
        public override ModelKind Kind => ModelKind.Nonlinear;
        public override string[] TerminalNames => ["a", "b"];
        public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega) { }
        public override NonlinearResult Evaluate(in PortVoltages v) =>
            new([g * v[0]], [0.0], new double[,] { { g } }, new double[,] { { 0 } });
        public override IReadOnlyList<NoiseSourceDecl> NoiseSources =>
            [NoiseSourceDecl.OnPort("thermal", 0), NoiseSourceDecl.OnPort("shot", 0), NoiseSourceDecl.OnPort("flicker", 0)];
        public override void EvaluateNoise(in PortVoltages v, double kelvin, Span<NoiseDensity> into)
        {
            into[0] = NoiseDensity.Thermal(g, kelvin);
            into[1] = NoiseDensity.Shot(g * v[0]);
            into[2] = NoiseDensity.Flicker(kf, 1, g * v[0]);
        }
    }

    [FixtureFact("tools/osdi-worker/osdi-worker", "run tools/osdi-worker/build.sh (needs a C compiler)")]
    public void InProcess_AndOsdi_GiveTheSameNf()
    {
        const double G = 2e-3, Kf = 1e-11, V = 0.8;
        ExternalDeviceRegistry.Clear();
        try
        {
            ExternalDeviceRegistry.Register(DeviceWorkerProvider.Launch("DevNoise",
                Locate("tools/osdi-worker/osdi-worker"), [Locate("tools/fake-osdi-model/fake_osdi.osdi")]));
            double[] hz = [1e3, 1e6, 1e9];
            using var osdi = Elaborate(Shunt(V, $"ExtDevice:X1  x 0  Provider=DevNoise Type=crf_noise g={N(G)} kf={N(Kf)}"));
            var a = Run(osdi, hz);

            using var mine = Elaborate(Shunt(V, $"ExtDevice:X1  x 0  Provider=DevNoise Type=crf_noise g={N(G)} kf={N(Kf)}"));
            int at = mine.Components.FindIndex(c => c.InstancePath == "X1");
            var x1 = mine.Components[at];
            mine.Components[at] = new ElaboratedComponent(x1.ComponentType, x1.InstancePath, x1.Nodes, x1.Parameters, new Conductance(G, Kf));
            var b = Run(mine, hz);

            for (int k = 0; k < hz.Length; k++)
            {
                output.WriteLine($"{hz[k]} Hz: OSDI NF {a["NF"].RealValues[k]:R}, in-process {b["NF"].RealValues[k]:R}");
                Assert.Equal(a["NF"].RealValues[k], b["NF"].RealValues[k], 1e-12);
            }
        }
        finally { ExternalDeviceRegistry.Clear(); }
    }

    /// <summary>A compiled Verilog-A device's worker is told the instance's own resolved temperature: crf_noise with
    /// Temp=126.85 at no bias is thermal at 400 K, F − 1 = (400/T0)·g·Z0, whatever the run's 26.85 °C ambient.</summary>
    [FixtureFact("tools/osdi-worker/osdi-worker", "run tools/osdi-worker/build.sh (needs a C compiler)")]
    public void VerilogA_InstanceTemp_ReachesTheWorker()
    {
        const double G = 1e-3;
        string previous = DeviceWorkerManifest.ToolsDirectory;
        DeviceWorkerManifest.ToolsDirectory = Path.GetDirectoryName(Locate("tools/osdi-worker/osdi-worker"))!;
        try
        {
            using var nl = Elaborate(Shunt(0, $"VerilogA:X1  x 0  File=\"{Locate("tools/fake-osdi-model/fake_osdi.osdi")}\" " +
                                              $"Model=crf_noise Pins=2 g={N(G)} Temp=126.85"));
            var ds = Run(nl, [1e9]);
            Assert.Equal(1.0, (ds["Fn"].RealValues[0] - 1) / (400.0 / NoiseMetrics.T0 * G * Z0), 1e-9);
        }
        finally
        {
            ExternalDeviceRegistry.ResetResolved();
            DeviceWorkerManifest.ToolsDirectory = previous;
        }
    }

    private static string Locate(string relative)
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            string candidate = Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        throw new InvalidOperationException($"fixture '{relative}' not found");
    }

    // ── gate 3: self-heating ──────────────────────────────────────────────────────

    /// <summary>
    /// The shipped self-heating composed FET at a dissipating bias: its network's gate resistor is thermal at the ambient
    /// plus the thermal node's DC rise, and with the instance's Rth taken to zero (1 nK/W: zero itself is refused) the rise
    /// is gone and it is back at ambient. The
    /// thermal network itself (Rth, Cth) carries heat, and is noiseless by definition.
    /// </summary>
    [Theory]
    [InlineData("", true)]
    [InlineData("Rth=1e-9", false)]
    public void ComposedFet_ParasiticsFollowTheHeatedTemperature(string rth, bool heated)
    {
        string model = Path.Combine(Path.GetDirectoryName(Locate("examples/Model Composer/synthetic-gan/gan_4x125.cmodel"))!, "gan_4x125.cmodel");
        using var nl = Elaborate($"""
            temp = 25
            Port:P1  p1 0  Num=1 Z=50 Ohm
            Port:P2  p2 0  Num=2 Z=50 Ohm
            C:CB1  p1 g  C=1 F
            C:CB2  p2 d  C=1 F
            L:LG  vg g  L=1 H
            L:LD  vd d  L=1 H
            Vdc:VG  vg 0  Vdc=-0.5
            Vdc:VD  vd 0  Vdc=20
            ComposedFET:X1  g d 0  File="{model}" {rth}
            """);
        double[] hz = [2e9];
        var dc = NonlinearDcEngine.Run(nl, AnalysisSettings.Default);
        Assert.True(dc.Converged);

        var core = nl.Components.Single(c => c.Model is ComposedCoreModel);
        int tNode = core.Nodes[2 * ((ComposedCoreModel)core.Model).ThermalPort];
        double rise = dc.NodeVoltages[tNode - 1];

        var sources = SParameterNoise.Sources(nl, hz, dc.NodeVoltages);
        var r1 = nl.Components.Single(c => c.InstancePath == "X1.R1");
        var c = sources.Single(s => s.Label == "X1.R1").At(2 * Math.PI * hz[0]).C;
        double kelvin = c[0, 0].Real / (4 * K * Math.Abs(r1.Parameters["R"].AsReal() is var r && r != 0 ? 1 / r : 0));
        output.WriteLine($"Rth '{rth}': rise {rise:G6} K, R1 thermal at {kelvin:R} K");
        Assert.Equal(298.15 + rise, kelvin, 1e-9 * kelvin);
        if (heated) Assert.True(rise > 1, $"rise {rise}");
        else Assert.True(rise < 1e-6, $"rise {rise}");

        var table = SParameterNoise.Table(nl, hz);
        Assert.Contains(table, row => row.Label == "X1.Rth" && row is { Kind: NoiseModelKind.Noiseless, ByDefinition: true });
    }

    // ── gate 5: no keys ───────────────────────────────────────────────────────────

    /// <summary>A FET with no noise keys declares no sources and is named noiseless as before, and it is the two-port it
    /// always was.</summary>
    [Fact]
    public void Fet_WithNoNoiseKeys_IsUnchanged()
    {
        using var nl = Elaborate("FET_ANGELOV:F1  g d s");
        var ec = nl.Components.Single();
        Assert.Empty(ec.Model.NoiseSources);
        Assert.Equal(4, ec.Nodes.Length);
        var row = SParameterNoise.Table(nl, [1e9]).Single();
        Assert.Equal(NoiseModelKind.Noiseless, row.Kind);
        Assert.Contains("Ri, Td or Kf", row.Reason);
    }
}
