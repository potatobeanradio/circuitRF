using System.Globalization;
using System.Numerics;
using System.Text;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Engine.Tests.Devices;

/// <summary>
/// The lumped realisations (MWPD, MBLC, MRRC, MCBL with Realization = Lumped) and the resistive pad (MRPAD), through the
/// engine. Each part alone is its interconnect, which matches the same MCROSS, MTEE, MLIN and MBEND sections wired by hand;
/// its sites filled with ideal parts of the plan's own values, on the copper, it divides, couples or attenuates at F as
/// its distributed sibling does; and with every copper island one node the closed forms are exact.
/// </summary>
public class LumpedSParamTests
{
    private const string Sub = "H=0.508e-3 T=35e-6 Er=3.66 Sigma=5.8e7 TanD=0.0037";
    private static readonly MicrostripSubstrate Ro4350b = new(0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037);
    private const double F0 = 0.5e9;

    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Each part's .cnl line (its pins as nets p1 …) and the plan it builds, at F0 = 500 MHz on 20 mil RO4350B.</summary>
    private static (string Line, PassivePlan Plan, string[] Ports, double[] Z) Part(string kind) => kind switch
    {
        "MWPD-Pi" => ($"MWPD:X1 {Nets(12)} Z0=50 Ohm F={F(F0)} Hz Realization=Lumped {Sub}",
            LumpedPlan.Wilkinson(50, F0, 1.35e-3, Ro4350b, LumpedArm.Pi).Plan, ["1", "2", "3"], [50, 50, 50]),
        "MWPD-Tee" => ($"MWPD:X1 {Nets(15)} Z0=50 Ohm F={F(F0)} Hz Realization=Lumped Arm=Tee {Sub}",
            LumpedPlan.Wilkinson(50, F0, 1.35e-3, Ro4350b, LumpedArm.Tee).Plan, ["1", "2", "3"], [50, 50, 50]),
        "MBLC" => ($"MBLC:X1 {Nets(16)} Z0=50 Ohm F={F(F0)} Hz Realization=Lumped {Sub}",
            LumpedPlan.BranchLine(50, F0, 1.35e-3, Ro4350b).Plan, ["1", "2", "3", "4"], [50, 50, 50, 50]),
        "MRRC" => ($"MRRC:X1 {Nets(14)} Z0=50 Ohm F={F(F0)} Hz Realization=Lumped {Sub}",
            LumpedPlan.RatRace(50, F0, 1.35e-3, Ro4350b).Plan, ["1", "2", "3", "4"], [50, 50, 50, 50]),
        "MCBL" => ($"MCBL:X1 {Nets(9)} Z0=50 Ohm ZL=200 Ohm F={F(F0)} Hz Gap=1.35mm Realization=Lumped {Sub}",
            LumpedPlan.LatticeBalun(50, 200, F0, 1.35e-3, Ro4350b).Plan, ["1", "2", "3"], [50, 200, 200]),
        _ => throw new ArgumentException(kind),
    };

    private static (string Line, PassivePlan Plan, string[] Ports) Pad(ResistivePadForm form, double db)
    {
        var plan = LumpedPlan.ResistivePad(form, 50, db, 1.35e-3, Ro4350b).Plan;
        bool splitter = form is ResistivePadForm.Wye or ResistivePadForm.Delta;
        return ($"MRPAD:X1 {Nets(plan.Pins.Count)} Z0=50 Ohm Attenuation={F(db)} Form={form} {Sub}", plan,
            splitter ? ["1", "2", "3"] : ["1", "2"]);
    }

    private static string Nets(int pins) => string.Join(" ", Enumerable.Range(1, pins).Select(k => $"p{k}"));

    private static ElaboratedNetlist Elaborate(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        return new Elaborator(lib).Elaborate(tb);
    }

    private static Complex[,] SAt(ElaboratedNetlist nl, double freqHz, int ports)
    {
        var s = SParameterEngine.Run(nl, [freqHz])["S"];
        var m = new Complex[ports, ports];
        for (int i = 0; i < ports; i++)
            for (int j = 0; j < ports; j++)
                m[i, j] = (Complex)s[0, i, j];
        return m;
    }

    private static string PortLines(IEnumerable<string> nets, IReadOnlyList<double>? z = null)
        => string.Concat(nets.Select((n, i) => $"Port:P{i + 1} {n} 0 Num={i + 1} Z={F(z?[i] ?? 50.0)} Ohm\n"));

    /// <summary>The plan's sections as explicit MCROSS, MTEE, MLIN and MBEND instances, pin k on net pk.</summary>
    private static string ByHand(PassivePlan plan)
    {
        int pins = plan.Pins.Count;
        string Node(int n) => n <= pins ? $"p{n}" : $"i{n}";
        var b = new StringBuilder();
        int k = 0;
        foreach (var section in plan.Sections)
        {
            string nodes = string.Join(" ", section.Nodes.Select(Node));
            b.AppendLine(section switch
            {
                PlanCross x => $"MCROSS:X{++k} {nodes} W1={F(x.W)} W2={F(x.W)} W3={F(x.W)} W4={F(x.W)} L1={F(x.L)} L2={F(x.L)} L3={F(x.L)} L4={F(x.L)} {Sub}",
                PlanTee t => $"MTEE:T{++k} {nodes} W1={F(t.W1)} W2={F(t.W2)} W3={F(t.W3)} L1={F(t.L1)} L2={F(t.L2)} L3={F(t.L3)} {Sub}",
                PlanLine l => $"MLIN:L{++k} {nodes} W={F(l.W)} L={F(l.L)} {Sub}",
                PlanBend { M: null } bend => $"MBEND:B{++k} {nodes} W={F(bend.W)} Angle={F(bend.AngleDeg)} deg Miter=0 {Sub}",
                _ => throw new InvalidOperationException(section.GetType().Name),
            });
        }
        return b.ToString();
    }

    /// <summary>An ideal part on each of the plan's sites, of its value; <paramref name="net"/> names the net a pin is on.</summary>
    private static string Sites(PassivePlan plan, Func<string, string> net)
    {
        var b = new StringBuilder();
        int k = 0;
        foreach (var site in plan.Sites)
        {
            string a = net(site.PinA), z = site.PinB is { } pb ? net(pb) : "0";
            b.AppendLine(site.Role switch
            {
                PlanSiteRole.L => $"L:S{++k} {a} {z} L={F(site.Value!.Value)} H",
                PlanSiteRole.C => $"C:S{++k} {a} {z} C={F(site.Value!.Value)} F",
                _ => $"R:S{++k} {a} {z} R={F(site.Value!.Value)} Ohm",
            });
        }
        return b.ToString();
    }

    private static string PinNet(PassivePlan plan, string pin) => $"p{plan.Pins.ToList().IndexOf(pin) + 1}";

    /// <summary>Every pin's net with each copper island one node: the island's first pin's (the model's own DC tree).</summary>
    private static Func<string, string> IslandNet(PassivePlan plan)
    {
        var model = new MicrostripPlanModel(plan, Ro4350b, "X1");
        var root = Enumerable.Range(0, plan.Pins.Count).ToArray();
        foreach (var (a, b) in model.DcShortedTerminals) root[b] = root[a];
        return pin => $"p{root[plan.Pins.ToList().IndexOf(pin)] + 1}";
    }

    private static double Db(Complex x) => 20.0 * Math.Log10(x.Magnitude);

    private static double PhaseDeg(Complex a, Complex b)
    {
        double d = (a.Phase - b.Phase) * 180.0 / Math.PI;
        while (d <= -180.0) d += 360.0;
        while (d > 180.0) d -= 360.0;
        return d;
    }

    [Theory]
    [InlineData("MWPD-Pi")]
    [InlineData("MWPD-Tee")]
    [InlineData("MBLC")]
    [InlineData("MRRC")]
    [InlineData("MCBL")]
    public void ThePartAlone_IsItsInterconnectWiredByHand(string kind)
    {
        var (line, plan, _, _) = Part(kind);
        int pins = plan.Pins.Count;
        string ports = PortLines(Enumerable.Range(1, pins).Select(k => $"p{k}"));
        foreach (double f in new[] { F0, 4.0 * F0 })
        {
            var a = SAt(Elaborate(ports + line), f, pins);
            var b = SAt(Elaborate(ports + ByHand(plan)), f, pins);
            for (int i = 0; i < pins; i++)
                for (int j = 0; j < pins; j++)
                    Assert.True((a[i, j] - b[i, j]).Magnitude < 1e-9, $"{kind} S{i + 1},{j + 1} at {f / 1e9} GHz: {a[i, j]} vs {b[i, j]}");
        }
    }

    [Theory]
    [InlineData(ResistivePadForm.Pi)]
    [InlineData(ResistivePadForm.T)]
    [InlineData(ResistivePadForm.BridgedT)]
    [InlineData(ResistivePadForm.Wye)]
    [InlineData(ResistivePadForm.Delta)]
    public void ThePadAlone_IsItsInterconnectWiredByHand(ResistivePadForm form)
    {
        var (line, plan, _) = Pad(form, 6.0);
        int pins = plan.Pins.Count;
        string ports = PortLines(Enumerable.Range(1, pins).Select(k => $"p{k}"));
        var a = SAt(Elaborate(ports + line), 1e9, pins);
        var b = SAt(Elaborate(ports + ByHand(plan)), 1e9, pins);
        for (int i = 0; i < pins; i++)
            for (int j = 0; j < pins; j++)
                Assert.True((a[i, j] - b[i, j]).Magnitude < 1e-9, $"{form} S{i + 1},{j + 1}: {a[i, j]} vs {b[i, j]}");
    }

    /// <summary>The S-matrix at F of the part's ports with each site filled by an ideal part of the plan's value, on the
    /// copper (<paramref name="onCopper"/>) or with each copper island one node.</summary>
    private static Complex[,] Filled(string kind, bool onCopper)
    {
        var (line, plan, ports, z) = Part(kind);
        var net = onCopper ? (Func<string, string>)(p => PinNet(plan, p)) : IslandNet(plan);
        string cnl = PortLines(ports.Select(net), z) + (onCopper ? line + "\n" : "") + Sites(plan, net);
        return SAt(Elaborate(cnl), F0, ports.Length);
    }

    /// <summary>Each island one node, the closed forms are the ideal device at F: matched, isolated, split exactly.</summary>
    [Theory]
    [InlineData("MWPD-Pi")]
    [InlineData("MWPD-Tee")]
    [InlineData("MBLC")]
    [InlineData("MRRC")]
    [InlineData("MCBL")]
    public void TheSiteValues_AreTheIdealDeviceAtF(string kind)
    {
        var s = Filled(kind, onCopper: false);
        double half = 1.0 / Math.Sqrt(2.0);
        Complex j = Complex.ImaginaryOne;
        // The ideal S-matrices at F, port 1 driven (the distributed parts' own phases).
        Complex[] want = kind switch
        {
            "MWPD-Pi" or "MWPD-Tee" => [0, -j * half, -j * half],
            "MBLC" => [0, -j * half, -half, 0],
            "MRRC" => [0, -j * half, -j * half, 0],
            _ => [0, -j * half, j * half],
        };
        for (int i = 0; i < want.Length; i++)
            Assert.True((s[i, 0] - want[i]).Magnitude < 1e-9, $"{kind} S{i + 1}1 = {s[i, 0]}, want {want[i]}");
        if (kind.StartsWith("MWPD", StringComparison.Ordinal))
            Assert.True(s[2, 1].Magnitude < 1e-9 && s[1, 1].Magnitude < 1e-9, $"{kind}: S22 {s[1, 1]}, S32 {s[2, 1]}");
        if (kind == "MRRC")
            Assert.True(s[1, 3].Magnitude - half is > -1e-9 and < 1e-9 && (s[1, 3] + s[2, 3]).Magnitude < 1e-9,
                $"MRRC Δ: S24 {s[1, 3]}, S34 {s[2, 3]}");
    }

    /// <summary>On the copper, at F = 500 MHz on 20 mil RO4350B, where each island's few millimetres of interconnect are small
    /// against the parts: still matched, isolated and split as the ideal device, within the interconnect's detuning. The
    /// branch-line's four crosses detune it most: |S11| and |S41| about −19.7 dB, the split 2.86 and 3.38 dB.</summary>
    [Theory]
    [InlineData("MWPD-Pi")]
    [InlineData("MWPD-Tee")]
    [InlineData("MBLC")]
    [InlineData("MRRC")]
    [InlineData("MCBL")]
    public void FilledWithIdealParts_TheCopperSplitsAtF(string kind)
    {
        var s = Filled(kind, onCopper: true);
        int n = s.GetLength(0);
        string all = string.Join(", ", Enumerable.Range(0, n).SelectMany(i => Enumerable.Range(0, n)
            .Select(k => $"S{i + 1}{k + 1} {Db(s[i, k]):F2} dB {s[i, k].Phase * 180 / Math.PI:F1}°")));
        Assert.True(Db(s[0, 0]) < -15.0, $"{kind} match: {all}");
        Assert.InRange(Db(s[1, 0]), -3.5, -2.8);
        Assert.InRange(Db(s[2, 0]), -3.5, -2.8);
        double phase = PhaseDeg(s[1, 0], s[2, 0]);
        switch (kind)
        {
            case "MWPD-Pi" or "MWPD-Tee":
                Assert.True(Db(s[2, 1]) < -15.0 && Db(s[1, 1]) < -15.0, $"{kind} isolation: {all}");
                Assert.InRange(phase, -2.0, 2.0);
                break;
            case "MBLC":
                Assert.True(Db(s[3, 0]) < -15.0, $"MBLC isolation: {all}");
                Assert.InRange(phase, 85.0, 95.0);
                break;
            case "MRRC":
                Assert.True(Db(s[3, 0]) < -15.0 && Db(s[2, 1]) < -15.0, $"MRRC isolation: {all}");
                Assert.InRange(phase, -5.0, 5.0);
                Assert.InRange(Math.Abs(PhaseDeg(s[1, 3], s[2, 3])), 175.0, 180.0);
                break;
            default:
                Assert.InRange(Math.Abs(phase), 175.0, 180.0);
                break;
        }
    }

    /// <summary>Each form, its resistors ideal and each island one node: its loss and its match at Z0, to 1e-6.</summary>
    [Theory]
    [InlineData(ResistivePadForm.Pi, 3.0)]
    [InlineData(ResistivePadForm.Pi, 10.0)]
    [InlineData(ResistivePadForm.T, 6.0)]
    [InlineData(ResistivePadForm.T, 20.0)]
    [InlineData(ResistivePadForm.BridgedT, 6.0)]
    [InlineData(ResistivePadForm.BridgedT, 15.0)]
    [InlineData(ResistivePadForm.Wye, 6.0)]
    [InlineData(ResistivePadForm.Delta, 6.0)]
    public void ThePad_GivesItsLossAndMatch(ResistivePadForm form, double db)
    {
        var (_, plan, ports) = Pad(form, db);
        var net = IslandNet(plan);
        var s = SAt(Elaborate(PortLines(ports.Select(net)) + Sites(plan, net)), 1e9, ports.Length);
        double want = ports.Length == 3 ? 20.0 * Math.Log10(2.0) : db;
        for (int i = 0; i < ports.Length; i++)
        {
            Assert.True(s[i, i].Magnitude < 1e-6, $"{form}: |S{i + 1}{i + 1}| = {s[i, i].Magnitude}");
            for (int k = 0; k < ports.Length; k++)
                if (k != i) Assert.Equal(-want, Db(s[i, k]), 6);
        }
    }
}
