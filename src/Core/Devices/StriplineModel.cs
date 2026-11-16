using System.Numerics;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Devices.Planar;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// SLIN — stripline, centred or offset, 2-port, ground-referenced (brief-artsch-1 R-as1-2;
/// docs/design/planar-line-models.md §3). Stamped by <see cref="TLineModel.StampUniformLine"/>.
///
/// <para>Parameters (SI): <c>W</c>, <c>L</c>, <c>H1</c> dielectric from the strip to the plane above,
/// <c>H2</c> to the plane below (<c>H1 = H2</c> is the centred line), <c>T</c>, <c>Er</c>, <c>TanD</c>,
/// <c>Sigma</c>, optional <c>Roughness</c>. A schematic instance's are injected from its technology by
/// <c>PlanarLineSubstrateInjection</c>.</para>
///
/// <para>TEM: ε_eff = Er at every frequency and Z₀ = <see cref="Stripline.Z0Air"/>/√Er. Conductor loss by
/// Wheeler's incremental-inductance rule; dielectric loss tanδ only, α_d = π·f·√εr·tanδ/c.</para>
/// </summary>
public sealed class StriplineModel : ComponentModel, IPlanarLineModel, IDcShortedTerminals, IUniformLine
{
    /// <summary>Lossless at DC (every loss term here vanishes at 0 Hz): a short from port 1 to port 2.</summary>
    public IReadOnlyList<(int A, int B)> DcShortedTerminals { get; } = [(0, 1)];

    public override int PortCount => 2;
    public override ModelKind Kind => ModelKind.Linear;
    public string LineKind => Stripline.ModelName;

    private readonly double _w, _l, _h1, _h2, _t, _er, _sigma, _tanD, _roughness;
    private readonly MicrostripValidityReporter _reporter;

    public IReadOnlyList<(string Key, string Message)> DrainWarnings() => _reporter.Drain();

    public StriplineModel(double wMeters, double lMeters, double h1Meters, double h2Meters, double tMeters,
        double epsR, double sigmaSPerM, double tanD, string instancePath, double roughnessMeters = 0.0)
    {
        _w = wMeters; _l = lMeters; _h1 = h1Meters; _h2 = h2Meters; _t = tMeters;
        _er = epsR; _sigma = sigmaSPerM; _tanD = tanD; _roughness = roughnessMeters;
        _reporter = new MicrostripValidityReporter(instancePath);
    }

    public MicrostripLineParameters LineParameters(double freqHz)
    {
        const string model = "SLIN (Cohn/Wheeler)";
        double b = _h1 + _h2 + _t;
        _reporter.CheckRange(model, "W/b", _w / b, Stripline.WOverBRange.Min, Stripline.WOverBRange.Max);
        _reporter.CheckRange(model, "T/b", _t / b, Stripline.TOverBRange.Min, Stripline.TOverBRange.Max);
        _reporter.CheckRange("SLIN (offset, parallel combination)", "max(H1,H2)/min(H1,H2)",
            Math.Max(_h1, _h2) / Math.Min(_h1, _h2), Stripline.OffsetRatioRange.Min, Stripline.OffsetRatioRange.Max);

        double z0 = Stripline.Z0Air(_w, _h1, _h2, _t) / Math.Sqrt(_er);
        double alphaC = 0, alphaD = 0, beta = 0, lossPhase = 0;
        if (freqHz > 0)
        {
            double dn = PlanarLineLoss.RecessionFraction * (_t > 0 ? Math.Min(Math.Min(_w, _t), Math.Min(_h1, _h2)) : Math.Min(_w, Math.Min(_h1, _h2)));
            double dt = _t > 0 ? 2 : 0;
            double Conductor(double f) => PlanarLineLoss.ConductorLossNpPerM(f, _sigma, _t, _roughness, z0,
                d => Stripline.Z0Air(_w - 2 * d, _h1 + 2 * d, _h2 + 2 * d, _t - dt * d), dn);
            alphaC = Conductor(freqHz);
            alphaD = Math.PI * freqHz * Math.Sqrt(_er) * _tanD / MicrostripLoss.SpeedOfLight;
            beta = 2 * Math.PI * freqHz / MicrostripLoss.SpeedOfLight * Math.Sqrt(_er);
            lossPhase = CausalLoss.Phase(freqHz, alphaC, Conductor(CausalLoss.ConductorFloorHz), alphaD);
        }
        return new MicrostripLineParameters(z0, _er, z0, _er, alphaC, alphaD, beta, lossPhase);
    }

    public (double Z0, Complex GammaLength) LineAt(double freqHz)
    {
        var p = LineParameters(freqHz);
        return (p.Z0, new Complex(p.AlphaNpPerM * _l, p.PhaseRadPerM * _l));
    }

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        var (z0, gammaLength) = LineAt(omega / (2.0 * Math.PI));
        TLineModel.StampUniformLine(mna, c.Nodes[0], c.Nodes[1], new Complex(z0, 0.0), gammaLength);
    }
}
