using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// Brief NZ-10 — oscillator phase noise. Each test is one of the brief's gates, against a closed form: the NZ-9 cubic
/// oscillator with one white current across its tank, the same with a flicker source tuning its tank, the far-out floor
/// against brief NZ-5's harmonic-balance noise, the parts' sum, and a run without the key.
/// </summary>
public class OscPhaseNoiseTests(ITestOutputHelper output)
{
    private const double Kb = 1.380649e-23, Q = 1.602176634e-19;

    // i(v) = −g1·v + g3·v³ across a parallel LC tank loaded by RL (brief NZ-9's gate 1). RL at 290 K is the one noise source.
    private const double L = 10e-9, C = 10e-12, RL = 1000, G1 = 2e-3, G3 = 1e-3;
    private static string Tank(string load = $"R:RL n 0 R=1000 Temp=16.85", string extra = "") => $@"
L:L1   n 0  L={L:R}
C:C1   n 0  C={C:R}
{load}
SDD:D1 n 0  Ports=1  I[1,0]=-{G1:R}*_v1+{G3:R}*_v1^3
OscProbe:X1 n 0 Mode=shunt
{extra}";

    private static OscResult Run(string cnl, OscillatorAnalysis a)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var p = OscEngine.Resolve(a, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
        return OscEngine.Run(nl, tb, p);
    }

    private static OscillatorAnalysis Osc(string start = "100 Hz", string stop = "10 MHz", string npts = "61", string port = "",
                                          string fguess = "0.45", bool pn = true, int maxHarm = 1) => new("OSC1")
    {
        FguessExpr = fguess, Unit = "GHz", MaxHarmonicExpr = maxHarm.ToString(), FFTOverSampleExpr = "4", TolExpr = "1e-12",
        PhaseNoise = pn, PnStartExpr = start, PnStopExpr = stop, PnNptsExpr = npts, PnPortExpr = port,
    };

    private static double[] Offsets(DataSet ds) => ds["PN"].Axes[0].Values.ToArray();

    // Gate 1. A noise current of density S across a parallel tank of capacitance C oscillating at amplitude A moves the
    // frequency by Im(I)/(4πCA) per ampere in quadrature, so L(f_m) = S / (16π²·C²·A²·f_m²) everywhere the tank is linear in
    // its detuning — the whole 1/f² region.
    [Fact]
    public void Gate1_LinearResonatorWhiteNoise_FollowsTheClosedForm()
    {
        var r = Run(Tank(), Osc());
        double a = r.Aosc;
        double s = 4 * Kb * 290 / RL;
        var pn = r.Data["PN"].RealValues;
        var f = Offsets(r.Data);
        double worst = 0;
        for (int i = 0; i < f.Length; i++)
        {
            double closed = 10 * Math.Log10(s / (16 * Math.PI * Math.PI * C * C * a * a * f[i] * f[i]));
            worst = Math.Max(worst, Math.Abs(pn[i] - closed));
            if (i % 10 == 0) output.WriteLine($"{f[i],12:G4} Hz  PN {pn[i],9:F3}  closed form {closed,9:F3} dBc/Hz");
        }
        output.WriteLine($"worst difference {worst:F4} dB over {f[0]:G3}-{f[^1]:G3} Hz");
        Assert.True(worst < 0.5, $"PN is {worst:F3} dB from the closed form");
        for (int i = 0; i + 10 < f.Length; i += 10)
        {
            double slope = (pn[i + 10] - pn[i]) / Math.Log10(f[i + 10] / f[i]);
            Assert.True(Math.Abs(slope + 20) < 0.1, $"slope {slope:F3} dB/decade at {f[i]:G3} Hz");
        }
    }

    // Gate 2. A nonlinear capacitor C(v) = C0 + C1·v couples the tank to a bias node b through Cbig; a forward-biased diode
    // holds b and its flicker current, Kf·I/f, moves b by Zb·i and the frequency by Kv per volt. The closed form:
    //   L_flicker = Kv²·Zb²·Kf·I / (2 f³),   L_white = S_RL/(16π²C_t²A²f²) + Kv²·Zb²·(2qI + 4kT/Rb)/(2 f²),
    // and the corner is where they are equal.
    private const double C0 = 1e-12, C1v = 0.5e-12, Cbig = 1e-9, Rb = 4300, Vbias = 5, Kf = 1.6e-13;
    private static string Flicker() => Tank(extra: $@"
NonlinearC:CV n b C0={C0:R} C1={C1v:R}
C:CB b 0 C={Cbig:R}
R:RB vb b R={Rb:R} Temp=16.85
Vdc:VB vb 0 Vdc={Vbias:R}
Diode:DB b 0 Is=1e-14 Kf={Kf:R} Af=1 Temp=16.85");

    [Fact]
    public void Gate2_FlickerSourceGivesThirtyDbPerDecade_BelowTheClosedFormCorner()
    {
        var r = Run(Flicker(), Osc(start: "1 Hz", stop: "10 MHz", npts: "71", fguess: "0.43"));
        var v = r.Data["V"];
        int Node(string n) => Array.FindIndex(v.Axes[0].Labels!, x => x == n);
        double a = ((Complex)v[Node("n"), 1]).Magnitude;
        double vb = ((Complex)v[Node("b"), 0]).Real;
        double id = (Vbias - vb) / Rb;
        double vt = Kb * 290 / Q;
        double zb = 1 / (1 / Rb + id / vt);
        double cnl = C0 - C1v * vb;                          // C at the fundamental: C0 + C1·(v_dc), v_dc = −vb
        double div = Cbig / (cnl + Cbig);
        double ct = C + cnl * div;
        double fosc = r.FoscHz;
        double kv = fosc / (2 * ct) * C1v * div * div;       // |df/dvb|: raising vb lowers C(v) and raises f
        double kz2 = kv * kv * zb * zb;
        double white = 4 * Kb * 290 / RL / (16 * Math.PI * Math.PI * ct * ct * a * a) + kz2 * (2 * Q * id + 4 * Kb * 290 / Rb) / 2;
        double flick = kz2 * Kf * id / 2;
        double cornerClosed = flick / white;
        double corner = r.Data["PNcorner"].RealValues[0];
        output.WriteLine($"Fosc {fosc / 1e6:F3} MHz, A {a:G5} V, vb {vb:G5} V, Id {id * 1e3:G5} mA, Zb {zb:G5} Ohm, Kv {kv / 1e6:G5} MHz/V");
        output.WriteLine($"corner {corner:G5} Hz, closed form {cornerClosed:G5} Hz ({100 * (corner / cornerClosed - 1):F2} %)");
        Assert.InRange(corner / cornerClosed, 0.8, 1.2);

        // Well below the corner the slope is −30 dB/decade; well above it, −20.
        var pn = r.Data["PN"].RealValues;
        var f = Offsets(r.Data);
        double Slope(double lo, double hi)
        {
            int i = Array.FindIndex(f, x => x >= lo), j = Array.FindIndex(f, x => x >= hi);
            return (pn[j] - pn[i]) / Math.Log10(f[j] / f[i]);
        }
        double below = Slope(corner / 300, corner / 30), above = Slope(corner * 30, corner * 300);
        output.WriteLine($"slope {below:F3} dB/decade below, {above:F3} above");
        Assert.True(Math.Abs(below + 30) < 0.5, $"below the corner: {below:F3} dB/decade");
        Assert.True(Math.Abs(above + 20) < 0.5, $"above the corner: {above:F3} dB/decade");
    }

    // Gate 3. Far from the carrier, the phase and amplitude noise together are all the noise at the carrier's two sidebands:
    // L + AN = (N(f0 + fm) + N(f0 − fm)) / (2·Pc). Brief NZ-5's harmonic-balance noise of the same circuit, held at the
    // oscillation by a driven solve at Fosc started on it, gives N at each sideband as an output frequency. NZ-5 centres its
    // sidebands on the output frequency, so at MaxHarm=1 the carrier's other sideband is outside its set and the phase mode
    // with it; at MaxHarm=3 both sets hold both carrier sidebands and differ only in the third harmonic's outermost ones.
    [Fact]
    public void Gate3_FarOutFloor_EqualsHarmonicBalanceNoiseOfTheHeldOscillation()
    {
        const string load = "Port:P1 n 0 Num=1 Z=1000 Ohm";
        var r = Run(Tank(load), Osc(start: "3 MHz", stop: "60 MHz", npts: "4", port: "1", maxHarm: 3));
        var f = Offsets(r.Data);
        var pn = r.Data["PN"].RealValues;
        var an = r.Data["AN"].RealValues;

        var (lib, tb) = new CnlReader().Read(Tank(load));
        var nl = new Elaborator(lib).Elaborate(tb);
        var engine = new HbEngine(nl, tb, AnalysisSettings.Default);
        var hp = new HbAnalysisParams([r.FoscHz], 3, MaxMixOrder: 1, FFTOverSample: 4, Tol: 1e-12,
            DriveStepping: DcBiasSteppingMode.IfNecessary, GuardHarmonic: 0, SweepVarName: null, SweepStart: 0, SweepStop: 0, SweepStep: 1);
        var sp = engine.RunSinglePoint(hp, r.Run.InterfaceV);
        Assert.True(sp.Converged);
        Complex v1 = sp.BackSolver!.GetNodeVoltage(1, 1, 0);
        double pc = v1.Magnitude * v1.Magnitude / (2 * 1000);
        var req = new HbNoiseRequest(1, 1, [0], false);
        for (int i = 0; i < f.Length; i++)
        {
            double nUp = engine.NoiseAt(sp, req, r.FoscHz + f[i])!["Nout"].RealValues[0];
            double nDn = engine.NoiseAt(sp, req, r.FoscHz - f[i])!["Nout"].RealValues[0];
            double hb = 10 * Math.Log10((nUp + nDn) / (2 * pc));
            double ours = 10 * Math.Log10(Math.Pow(10, pn[i] / 10) + Math.Pow(10, an[i] / 10));
            output.WriteLine($"{f[i] / 1e6,8:G4} MHz  PN {pn[i]:F3}  AN {an[i]:F3}  PN+AN {ours:F4}  HB noise {hb:F4} dBc/Hz");
            Assert.True(Math.Abs(ours - hb) < 0.1, $"{f[i]} Hz: {ours} vs {hb}");
        }
    }

    // Gate 4. Each part's share of the phase noise sums to the total, close to the carrier and far from it.
    [Fact]
    public void Gate4_ContributionsSumToTheTotal()
    {
        var r = Run(Flicker(), Osc(start: "10 Hz", stop: "50 MHz", npts: "15", fguess: "0.43"));
        var pn = r.Data["PN"].RealValues;
        var pnc = r.Data["PNc"];
        var pct = r.Data["PNpct"].RealValues;
        int np = pnc.Axes[1].Length;
        output.WriteLine("parts: " + string.Join(", ", pnc.Axes[1].Labels!));
        Assert.True(np >= 3);
        for (int i = 0; i < pn.Length; i++)
        {
            double sum = 0, pctSum = 0;
            for (int k = 0; k < np; k++) { sum += Math.Pow(10, pnc.RealValues[i * np + k] / 10); pctSum += pct[i * np + k]; }
            Assert.True(Math.Abs(10 * Math.Log10(sum) - pn[i]) < 1e-9, $"offset {i}: parts {10 * Math.Log10(sum)} vs {pn[i]}");
            Assert.True(Math.Abs(pctSum - 100) < 1e-9, $"offset {i}: {pctSum} %");
        }
    }

    // The system-level link: PN written as a source's PhaseNoise= mask reads back as the same curve.
    [Fact]
    public void Pn_PastesIntoAPhaseNoiseMask()
    {
        var r = Run(Tank(), Osc(npts: "21"));
        var pn = r.Data["PN"].RealValues;
        var f = Offsets(r.Data);
        string assignment = RfCore.Signals.PhaseNoiseMask.Assignment(f.Zip(pn));
        output.WriteLine(assignment);
        Assert.StartsWith("PhaseNoise=\"", assignment);
        var mask = RfCore.Signals.PhaseNoiseMask.Parse(assignment["PhaseNoise=".Length..]);
        for (int i = 0; i < f.Length; i++) Assert.True(Math.Abs(mask.At(f[i]) - pn[i]) < 0.006, $"{f[i]} Hz: {mask.At(f[i])} vs {pn[i]}");
    }

    // Gate 5. Without PhaseNoise=yes the run is the one from before: no phase-noise cube, and every other cube bit for bit
    // what a run with the key writes beside its phase-noise cubes (which are computed after it).
    [Fact]
    public void Gate5_NoKey_NoChange()
    {
        var with = Run(Tank(), Osc(start: "1 kHz", stop: "1 MHz", npts: "4")).Data;
        var without = Run(Tank(), Osc(pn: false)).Data;
        string[] pnCubes = ["PN", "AN", "PNc", "PNpct", "PNcorner"];
        Assert.DoesNotContain(without.Cubes.Keys, k => pnCubes.Contains(k));
        Assert.Equal(without.Cubes.Keys.OrderBy(k => k), with.Cubes.Keys.Except(pnCubes).OrderBy(k => k));
        foreach (var k in without.Cubes.Keys)
        {
            var a = without.Cubes[k];
            var b = with.Cubes[k];
            Assert.Equal(a.DataKind, b.DataKind);
            if (a.DataKind == DataKind.Real) Assert.Equal(a.RealValues.ToArray(), b.RealValues.ToArray());
            else Assert.Equal(a.ComplexValues.ToArray(), b.ComplexValues.ToArray());
        }
    }
}
