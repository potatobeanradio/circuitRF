using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Planar;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Transient;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Transient;

/// <summary>
/// brief-trn-4b's gates: every shipped single-line model's loss comes with its phase (CausalLoss), so the transient realizes
/// it — a delay and a rational residual — within |ΔS21| ≤ 1e-3 of the model; the dielectric is unchanged at its reference
/// frequency and a lossless line is unchanged everywhere; and a lossy line's step response is the S-parameter engine's S21.
/// </summary>
public sealed class CausalLineTests(ITestOutputHelper output)
{
    private static IUniformLine Line(string which) => which switch
    {
        "MLIN board" => new MicrostripLineModel(0.55e-3, 10e-3, 0.254e-3, 35e-6, 3.66, 5.8e7, 0.0037, "a"),
        "MLIN FR4" => new MicrostripLineModel(1.5e-3, 25e-3, 0.8e-3, 35e-6, 4.4, 5.8e7, 0.02, "b"),
        "MLIN GaAs" => new MicrostripLineModel(70e-6, 2e-3, 100e-6, 3e-6, 12.9, 4.1e7, 0.0006, "c"),
        "SLIN board" => new StriplineModel(0.35e-3, 10e-3, 0.25e-3, 0.25e-3, 17e-6, 3.5, 5.8e7, 0.003, "d"),
        "SLIN FR4" => new StriplineModel(0.4e-3, 25e-3, 0.5e-3, 0.5e-3, 35e-6, 4.4, 5.8e7, 0.02, "e"),
        "SLIN LTCC" => new StriplineModel(0.1e-3, 3e-3, 0.2e-3, 0.2e-3, 10e-6, 7.8, 4.1e7, 0.002, "f"),
        "CPWG board" => new CoplanarLineModel(0.5e-3, 0.2e-3, 10e-3, 0.254e-3, 35e-6, 3.66, 5.8e7, 0.0037, "g"),
        "CPWG FR4" => new CoplanarLineModel(1e-3, 0.25e-3, 25e-3, 0.8e-3, 35e-6, 4.4, 5.8e7, 0.02, "h"),
        "CPWG GaAs" => new CoplanarLineModel(50e-6, 35e-6, 2e-3, 100e-6, 3e-6, 12.9, 4.1e7, 0.0006, "i"),
        "TLIN electrical A" => new TLineModel(50, Math.PI, 1e9, "j", attenuationDb: 0.5),
        "TLIN physical Ac Ad" => TLineModel.Physical(50, 25e-3, 3.2, 2e9, 4, 2, "k"),
        _ => throw new ArgumentException(which),
    };

    /// <summary>
    /// Gate 1 — the causality of every shipped single-line model: at f_max = 20 GHz (5 GHz is easier: every case within
    /// 9.4e-4 in the step-1 measurement) each line is realized within |ΔS21| ≤ 1e-3. A model whose loss had no matching phase
    /// fails here: before brief TRN-4b these read 2.8e-3 to 0.22.
    /// </summary>
    [Theory]
    [InlineData("MLIN board")]
    [InlineData("MLIN FR4")]
    [InlineData("MLIN GaAs")]
    [InlineData("SLIN board")]
    [InlineData("SLIN FR4")]
    [InlineData("SLIN LTCC")]
    [InlineData("CPWG board")]
    [InlineData("CPWG FR4")]
    [InlineData("CPWG GaAs")]
    [InlineData("TLIN electrical A")]
    [InlineData("TLIN physical Ac Ad")]
    public void Gate1_EveryShippedLineModel_IsRealizedWithin1e3(string which)
    {
        var fit = TransientRealization.FitLossyLine(Line(which), 1, 20e9);
        output.WriteLine($"{which}: τ {fit.Tau * 1e9:G4} ns, order {fit.Order}, |ΔS21| {fit.ErrorS21:G3}, |ΔS11| {fit.ErrorS11:G3}");
        Assert.NotNull(fit.Residual);
        Assert.True(fit.ErrorS21 <= TransientRealization.LineTolerance, $"{which}: |ΔS21| {fit.ErrorS21:G3}");
    }

    /// <summary>
    /// Gate 2 — the pin: at 1 GHz, where Er and TanD are stated, the phase a planar line gains is its conductor term alone;
    /// TLIN's dielectric term is pinned at its own F; and a lossless line gains nothing anywhere.
    /// </summary>
    [Fact]
    public void Gate2_TheDielectricIsPinnedAtItsReference_ALosslessLineIsUnchanged()
    {
        foreach (var m in new IPlanarLineModel[]
                 {
                     new MicrostripLineModel(1.5e-3, 25e-3, 0.8e-3, 35e-6, 4.4, 5.8e7, 0.02, "a"),
                     new StriplineModel(0.4e-3, 25e-3, 0.5e-3, 0.5e-3, 35e-6, 4.4, 5.8e7, 0.02, "b"),
                     new CoplanarLineModel(1e-3, 0.25e-3, 25e-3, 0.8e-3, 35e-6, 4.4, 5.8e7, 0.02, "c"),
                 })
        {
            var p = m.LineParameters(CausalLoss.DielectricReferenceHz);
            var p0 = m.LineParameters(CausalLoss.ConductorFloorHz);
            Assert.Equal(p.ConductorLossNpPerM - p0.ConductorLossNpPerM, p.LossPhaseRadPerM, 12);
        }
        var tl = new TLineModel(50, Math.PI, 2e9, "t", attenuationDb: 1);
        Assert.Equal(Math.PI, tl.LineAt(2e9).GammaLength.Imaginary, 14);
        foreach (double f in new[] { 1e6, 1e9, 2e10 })
        {
            Assert.Equal(0.0, new MicrostripLineModel(1.5e-3, 25e-3, 0.8e-3, 35e-6, 4.4, double.PositiveInfinity, 0, "l").LineParameters(f).LossPhaseRadPerM);
            Assert.Equal(new TLineModel(50, Math.PI, 1e9, "u").ElectricalLengthRad(f), new TLineModel(50, Math.PI, 1e9, "u").LineAt(f).GammaLength.Imaginary);
        }
    }

    /// <summary>
    /// Gate 3 — a 0.2 ns pulse through 25 mm of lossy FR4 microstrip in 50 Ω: the output's transform over the incident wave
    /// equals the S-parameter engine's S21 of the same line within 3e-4 from 0.25 to 4 GHz (the realization's own |ΔS21| plus
    /// the integration's; 1.1e-4 measured). The output grid is 1 ps: on a coarser one the pulse's spectral images fold back
    /// into the band and the transform itself is off by more than the line (5 ps put 1.4e-3 at 4 GHz, on a line realized within
    /// 2.6e-4). This gate also holds the error check on a segment's first step: while that step went unchecked its damping put
    /// 9.1e-4 here whatever RelTol said (src/Engine/RESOLVED.md, brief-trn-4b).
    /// </summary>
    [Fact]
    public void Gate3_ALossyLine_StepResponseIsTheSParameterS21()
    {
        const string line = "MLIN:TL1 in out W=1.5 mm L=25 mm H=0.8 mm T=35 um Er=4.4 TanD=0.02 Sigma=5.8e7";
        var (lib, tb) = new CnlReader().Read($"""
            V_Pulse:VS s 0 V1=0 V2=1 Delay=0.1n Rise=0.1n Fall=0.1n Width=0
            R:RS s in R=50
            {line}
            R:RL out 0 R=50
            analysis TR1 type=tran Stop=10n Step=1p MaxStep=50p RelTol=1e-7
            """);
        var nl = new Elaborator(lib).Elaborate(tb);
        var r = new TransientEngine(nl).Run(TransientRunner.Resolve(tb.Analyses.OfType<TransientAnalysis>().Single(), nl.ResolvedGlobals));
        var rep = Assert.Single(r.Lines);
        output.WriteLine($"realized: τ {rep.DelaysS[0] * 1e9:G4} ns, order {rep.Order}, |ΔS21| {rep.Error:G3}, |ΔS11| {rep.ErrorS11:G3}, {r.AcceptedSteps} steps");
        Assert.True(rep.Order > 0);

        var freqs = Enumerable.Range(1, 16).Select(k => 0.25e9 * k).ToArray();
        var (lib2, tb2) = new CnlReader().Read($"Port:P1 in 0 Num=1\n{line}\nPort:P2 out 0 Num=2\nanalysis SP1 type=sparam start=1 stop=2 npts=2 Unit=GHz\n");
        var s = SParameterEngine.Run(new Elaborator(lib2).Elaborate(tb2), freqs)["S"];
        int vs = Array.IndexOf(r.NodeNames, "s"), vo = Array.IndexOf(r.NodeNames, "out");
        double worst = 0;
        for (int k = 0; k < freqs.Length; k++)
        {
            Complex a = 0, b = 0;
            for (int t = 0; t < r.Time.Length; t++)
            {
                var e = Complex.FromPolarCoordinates(1, -2 * Math.PI * freqs[k] * r.Time[t]);
                a += 0.5 * r.V[t][vs] * e; b += r.V[t][vo] * e;
            }
            var s21 = s.ComplexValues[k * 4 + 1 * 2 + 0];
            worst = Math.Max(worst, (b / a - s21).Magnitude);
        }
        output.WriteLine($"transform against the S-parameter engine's S21, 0.25-4 GHz: max |Δ| {worst:G3}");
        Assert.True(worst <= 3e-4, $"{worst:G3}");
    }
}
