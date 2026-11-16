using System.Numerics;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;
using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Devices;

/// <summary>
/// MCLIN — a symmetric pair of edge-coupled microstrip lines, 4-port, ground-referenced.
///
/// <para>Port order is CLIN's (<see cref="CLineModel"/>): Nodes[0] = 1, line A near end; Nodes[1] = 2,
/// line A far end; Nodes[2] = 3, line B near end (beside 1); Nodes[3] = 4, line B far end.</para>
///
/// <para>Electrically: each mode is a uniform line of its own impedance and its own propagation
/// constant, recombined by <see cref="CLineModel.StampModalPair"/>. Per mode, at every frequency:
/// Z₀e/o(f) and εeff,e/o(f) from <see cref="KirschningJansenCoupled"/> (static Kirschning-Jansen
/// with the thickness corrections, then their dispersion); β = 2πf·√εeff/c with the mode's OWN εeff,
/// so the odd mode runs faster than the even one and the pair shows microstrip's finite directivity;
/// conductor loss by MLIN's Wheeler form Rs/(Z_mode·W) with Hammerstad-Bekkadal roughness, and
/// dielectric loss by MLIN's filling-factor form at the mode's εeff
/// (<see cref="MicrostripLoss"/>).</para>
///
/// <para>Parameters (SI, units applied by the elaborator; the substrate injected as MLIN's is):
/// <c>W</c> strip width, <c>S</c> edge-to-edge gap, <c>L</c> length (m); <c>H</c>, <c>T</c>,
/// <c>Er</c>, <c>Sigma</c>, <c>TanD</c>, optional <c>Roughness</c> — exactly MLIN's.</para>
/// </summary>
public sealed class MicrostripCoupledLineModel : ComponentModel, IReportsWarnings, IDcShortedTerminals, ICoupledPair
{
    /// <summary>Lossless at DC (both modes' γ are zero there): each strip a short end to end (1-2, 3-4).</summary>
    public IReadOnlyList<(int A, int B)> DcShortedTerminals { get; } = [(0, 1), (2, 3)];

    public override int PortCount => 4;
    public override ModelKind Kind => ModelKind.Linear;

    private readonly double _w, _s, _l, _h, _t, _er, _sigma, _tanD, _rough;
    private readonly MicrostripValidityReporter _reporter;

    public IReadOnlyList<(string Key, string Message)> DrainWarnings() => _reporter.Drain();

    public MicrostripCoupledLineModel(double wMeters, double sMeters, double lMeters, double hMeters, double tMeters,
        double epsR, double sigmaSPerM, double tanD, string instancePath, double roughnessMeters = 0.0)
    {
        if (Refusal(wMeters, sMeters, lMeters, hMeters, tMeters, epsR) is { } why)
            throw new ArgumentException(why.Render());
        _w = wMeters; _s = sMeters; _l = lMeters; _h = hMeters; _t = tMeters; _er = epsR;
        _sigma = sigmaSPerM; _tanD = tanD; _rough = roughnessMeters;
        _reporter = new MicrostripValidityReporter(instancePath);
    }

    /// <summary>The coded refusal for a geometry no coupled pair can have; null when it is valid.</summary>
    public static Diagnostic? Refusal(double w, double s, double l, double h, double t, double epsR)
    {
        if (!(w > 0.0))
            return Diagnostic.Create("mclin.width.not-positive", DiagnosticSeverity.Error,
                "MCLIN: W={w} m must be positive.", ("w", w));
        if (!(s > 0.0))
            return Diagnostic.Create("mclin.gap.not-positive", DiagnosticSeverity.Error,
                "MCLIN: gap S={s} m must be positive. Touching strips are one wider line, not a coupled pair.", ("s", s));
        if (!(l >= 0.0))
            return Diagnostic.Create("mclin.length.negative", DiagnosticSeverity.Error,
                "MCLIN: L={l} m must not be negative.", ("l", l));
        if (!(h > 0.0))
            return Diagnostic.Create("mclin.height.not-positive", DiagnosticSeverity.Error,
                "MCLIN: substrate H={h} m must be positive.", ("h", h));
        if (!(t >= 0.0))
            return Diagnostic.Create("mclin.thickness.negative", DiagnosticSeverity.Error,
                "MCLIN: T={t} m must not be negative.", ("t", t));
        if (!(epsR >= 1.0))
            return Diagnostic.Create("mclin.er.below-one", DiagnosticSeverity.Error,
                "MCLIN: Er={er} is below 1; a dielectric's relative permittivity is at least that of vacuum.", ("er", epsR));
        return null;
    }

    /// <summary>The pair's modal Z and εeff at <paramref name="freqHz"/> (0 = static), as stamped.</summary>
    public KirschningJansenCoupled.Modes ModesAt(double freqHz)
        => KirschningJansenCoupled.Compute(freqHz, _w, _s, _h, _t, _er, _reporter);

    /// <summary>Per-mode propagation constants γ = α + jβ (1/m) at <paramref name="freqHz"/>.</summary>
    public (Complex Even, Complex Odd) Gamma(double freqHz, KirschningJansenCoupled.Modes m)
    {
        if (!(freqHz > 0.0)) return (Complex.Zero, Complex.Zero);
        Complex One(double z, double eeff)
        {
            double alpha = MicrostripLoss.ConductorLossNpPerM(freqHz, _sigma, _w, z, _rough)
                         + (_er > 1.0 ? MicrostripLoss.DielectricLossNpPerM(freqHz, _er, eeff, _tanD) : 0.0);
            return new Complex(alpha, 2.0 * Math.PI * freqHz / MicrostripLoss.SpeedOfLight * Math.Sqrt(eeff));
        }
        return (One(m.Ze, m.EeffE), One(m.Zo, m.EeffO));
    }

    /// <summary>brief TRN-6 — each mode's impedance and γl over the pair's length at <paramref name="freqHz"/>, as stamped.</summary>
    public (double Ze, Complex GammaLengthEven, double Zo, Complex GammaLengthOdd) PairAt(double freqHz)
    {
        var m = ModesAt(freqHz);
        var (ge, go) = Gamma(freqHz, m);
        return (m.Ze, ge * _l, m.Zo, go * _l);
    }

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        double f = omega / (2.0 * Math.PI);
        var m = ModesAt(f);
        var (ge, go) = Gamma(f, m);
        CLineModel.StampModalPair(mna, c.Nodes[0], c.Nodes[1], c.Nodes[2], c.Nodes[3],
                                  m.Ze, ge * _l, m.Zo, go * _l);
    }
}
