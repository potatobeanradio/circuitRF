using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using RfCore.Numerics;
using Xunit;

namespace CircuitRF.Engine.Tests.Devices;

/// <summary>
/// MRADIAL, the microstrip radial stub. Its closed form is the radial line's own equations integrated numerically; as
/// a shunt stub it shorts the line where that closed form's numerator vanishes, about a quarter wave past its narrow
/// end; a butterfly is two sectors in parallel; and a <see cref="PlanRadialStub"/> on a tee's branch is the same
/// MTEE and MRADIAL wired by hand.
/// </summary>
public class RadialStubTests
{
    private const string Sub = "H=0.508e-3 T=35e-6 Er=3.66 Sigma=5.8e7 TanD=0.0037";
    private static readonly MicrostripSubstrate Ro4350b = new(0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037);
    private const double W = 1.1e-3, Ro = 8e-3, Angle = 60.0;

    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static string Stub(string node, int sides, string name = "S1")
        => $"MRADIAL:{name} {node} W={F(W)} Ro={F(Ro)} Angle={F(Angle)} deg Sides={sides} {Sub}";

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

    private static void AssertSame(Complex[,] a, Complex[,] b, double f)
    {
        for (int i = 0; i < a.GetLength(0); i++)
            for (int j = 0; j < a.GetLength(1); j++)
                Assert.True((a[i, j] - b[i, j]).Magnitude < 1e-9, $"S{i + 1}{j + 1} at {f / 1e9} GHz: {a[i, j]} vs {b[i, j]}");
    }

    private const string TwoPorts = """
        Port:P1 p1 0 Num=1 Z=50 Ohm
        Port:P2 p1 0 Num=2 Z=50 Ohm
        """;

    /// <summary>
    /// The Bessel closed form against the equations it solves: dV/dr = −Z'·I and dI/dr = −Y'·V with
    /// Z' = jωμ₀h(1 − jδc)/(α·r) and Y' = jωε₀εeff·α·r(1 − jδd)/h, integrated by fourth-order Runge-Kutta from the
    /// open arc (V = 1, I = 0) in to the narrow end. No Bessel function is evaluated on this side.
    /// </summary>
    [Theory]
    [InlineData(1e9)]
    [InlineData(4e9)]
    [InlineData(9e9)]
    public void TheClosedForm_IsTheRadialLineEquationsIntegrated(double f)
    {
        var line = new MicrostripLineModel(MicrostripRadialStub.EquivalentWidth(W, Ro, Angle), 0.0, Ro4350b.HMeters,
            Ro4350b.TMeters, Ro4350b.EpsR, Ro4350b.SigmaSPerM, Ro4350b.TanD, "S1");
        var z = MicrostripRadialStub.SectorImpedance(f, W, Ro, Angle, Ro4350b, line, out _);

        var p = line.LineParameters(f);
        double omega = 2.0 * Math.PI * f, alpha = Angle * Math.PI / 180.0, h = Ro4350b.HMeters;
        double eeff = Math.Pow(p.BetaRadPerM / (omega / MicrostripLoss.SpeedOfLight), 2.0);
        var series = new Complex(1.0, -2.0 * p.ConductorLossNpPerM / p.BetaRadPerM);
        var shunt = new Complex(1.0, -2.0 * p.DielectricLossNpPerM / p.BetaRadPerM);
        double eps0 = 1.0 / (MicrostripLoss.Mu0 * MicrostripLoss.SpeedOfLight * MicrostripLoss.SpeedOfLight);
        Complex Zp(double r) => Complex.ImaginaryOne * omega * MicrostripLoss.Mu0 * h / (alpha * r) * series;
        Complex Yp(double r) => Complex.ImaginaryOne * omega * eps0 * eeff * alpha * r / h * shunt;

        double ri = MicrostripRadialStub.InnerRadius(W, Angle);
        double roe = Ro + MicrostripOpenEnd.Extension(MicrostripRadialStub.EquivalentWidth(W, Ro, Angle), h,
            Ro4350b.EpsR, p.EeffStatic);
        const int n = 20000;
        double dr = (ri - roe) / n;
        Complex v = 1.0, i = 0.0;
        (Complex, Complex) D(double r, Complex vv, Complex ii) => (-Zp(r) * ii, -Yp(r) * vv);
        for (int k = 0; k < n; k++)
        {
            double r = roe + k * dr;
            var (a1, b1) = D(r, v, i);
            var (a2, b2) = D(r + dr / 2, v + dr / 2 * a1, i + dr / 2 * b1);
            var (a3, b3) = D(r + dr / 2, v + dr / 2 * a2, i + dr / 2 * b2);
            var (a4, b4) = D(r + dr, v + dr * a3, i + dr * b3);
            v += dr / 6 * (a1 + 2 * a2 + 2 * a3 + a4);
            i += dr / 6 * (b1 + 2 * b2 + 2 * b3 + b4);
        }
        Complex integrated = v / i;
        Assert.True((z - integrated).Magnitude < 1e-7 * integrated.Magnitude, $"{z} vs {integrated}");
    }

    /// <summary>
    /// Shunted across a 50 Ω line, the stub is a short at its quarter-wave resonance: |S21| dips to its minimum where
    /// the closed form's numerator J₀(k·r_i)·Y₁(k·r_o) − Y₀(k·r_i)·J₁(k·r_o) crosses zero (found here from the Bessel
    /// functions and MLIN's β alone). A sector is a line whose impedance falls as it widens, so it resonates short of
    /// a quarter wave: on this board, narrow end to effective arc is two thirds of one.
    /// </summary>
    [Fact]
    public void TheQuarterWaveResonance_FallsWhereTheClosedFormPutsIt()
    {
        var line = new MicrostripLineModel(MicrostripRadialStub.EquivalentWidth(W, Ro, Angle), 0.0, Ro4350b.HMeters,
            Ro4350b.TMeters, Ro4350b.EpsR, Ro4350b.SigmaSPerM, Ro4350b.TanD, "S1");
        double ri = MicrostripRadialStub.InnerRadius(W, Angle);
        double Roe(double f) => Ro + MicrostripOpenEnd.Extension(MicrostripRadialStub.EquivalentWidth(W, Ro, Angle),
            Ro4350b.HMeters, Ro4350b.EpsR, line.LineParameters(f).EeffStatic);
        double Numerator(double f)
        {
            double k = line.LineParameters(f).BetaRadPerM;
            return (Bessel.J0(k * ri) * Bessel.Y1(k * Roe(f)) - Bessel.Y0(k * ri) * Bessel.J1(k * Roe(f))).Real;
        }
        double lo = 1e9, hi = 9e9;
        Assert.True(Math.Sign(Numerator(lo)) != Math.Sign(Numerator(hi)));
        for (int k = 0; k < 60; k++)
        {
            double mid = (lo + hi) / 2;
            if (Math.Sign(Numerator(mid)) == Math.Sign(Numerator(lo))) lo = mid; else hi = mid;
        }
        double f0 = (lo + hi) / 2;

        var nl = Elaborate($"{TwoPorts}\n{Stub("p1", 1)}");
        var freqs = Enumerable.Range(0, 801).Select(k => f0 * (0.9 + 0.2 * k / 800.0)).ToArray();
        var s = SParameterEngine.Run(nl, freqs)["S"];
        double fMin = freqs.MinBy(fk => ((Complex)s[Array.IndexOf(freqs, fk), 1, 0]).Magnitude);
        Assert.True(Math.Abs(fMin - f0) / f0 < 0.5e-3, $"|S21| minimum at {fMin / 1e9:F4} GHz, closed form {f0 / 1e9:F4} GHz");
        Assert.True(20 * Math.Log10(((Complex)s[Array.IndexOf(freqs, fMin), 1, 0]).Magnitude) < -20.0);

        double quarterWave = MicrostripLoss.SpeedOfLight / (4.0 * f0 * Math.Sqrt(line.LineParameters(f0).Eeff));
        Assert.InRange((Roe(f0) - ri) / quarterWave, 0.6, 0.75);
    }

    /// <summary>A butterfly is its two sectors in parallel: Sides = 2 on a node is two Sides = 1 stubs on it.</summary>
    [Theory]
    [InlineData(2e9)]
    [InlineData(5e9)]
    [InlineData(9e9)]
    public void TheButterfly_IsTwoSectorsInParallel(double f)
        => AssertSame(SAt(Elaborate($"{TwoPorts}\n{Stub("p1", 2)}"), f, 2),
                      SAt(Elaborate($"{TwoPorts}\n{Stub("p1", 1)}\n{Stub("p1", 1, "S2")}"), f, 2), f);

    /// <summary>
    /// A <see cref="PlanRadialStub"/> hung on a <see cref="PlanTee"/>'s branch, stamped through
    /// <see cref="MicrostripPlanModel"/>, is the same MTEE and MRADIAL as explicit instances: the section reaches the
    /// element's own model, angle and sides included.
    /// </summary>
    [Theory]
    [InlineData(1, 2e9)]
    [InlineData(1, 6e9)]
    [InlineData(2, 6e9)]
    public void ThePlanSection_IsTheElementWiredByHand(int sides, double f)
    {
        const double w0 = 1.1e-3, l = 1.1e-3;
        var plan = new PassivePlan(["1", "2"],
        [
            new PlanTee(w0, w0, W, l, l, l, [1, 2, 3], 0.0, 0.0, 0.0),
            new PlanRadialStub([3], l, -l, 270.0, W, Ro, Angle, sides),
        ], 3);
        const string ports = """
            Port:P1 p1 0 Num=1 Z=50 Ohm
            Port:P2 p2 0 Num=2 Z=50 Ohm
            """;
        var planned = Elaborate($"{ports}\nMLIN:X p1 p2 W={F(w0)} L={F(l)} {Sub}");
        int at = planned.Components.FindIndex(c => c.InstancePath == "X");
        var x = planned.Components[at];
        planned.Components[at] = new ElaboratedComponent(nameof(MicrostripPlanModel), x.InstancePath, x.Nodes,
            x.Parameters, new MicrostripPlanModel(plan, Ro4350b, "X"));

        var byHand = Elaborate($"""
            {ports}
            MTEE:T1 p1 p2 n3 W1={F(w0)} W2={F(w0)} W3={F(W)} L1={F(l)} L2={F(l)} L3={F(l)} {Sub}
            {Stub("n3", sides)}
            """);
        AssertSame(SAt(planned, f, 2), SAt(byHand, f, 2), f);
    }

    [Theory]
    [InlineData(1.1e-3, 1e-3, 60.0, 1, "device.radialstub.too-short")]
    [InlineData(1.1e-3, 8e-3, 200.0, 1, "device.radialstub.angle")]
    [InlineData(1.1e-3, 8e-3, 60.0, 3, "device.radialstub.sides")]
    [InlineData(0.0, 8e-3, 60.0, 1, "device.radialstub.not-positive")]
    public void AStubTheClosedFormCannotDescribe_IsRefusedByName(double w, double ro, double angle, int sides, string id)
    {
        var ex = Assert.Throws<ParameterRefusalException>(() => new MicrostripRadialStubModel(w, ro, angle, sides, Ro4350b, "S1"));
        Assert.Equal(id, ex.Diagnostic!.Id);
    }
}
