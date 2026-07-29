using System.Numerics;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// MLIN — microstrip line, 2-port. brief-L5a-pcell-contract-and-microstrip.md §3.1 /
/// microstrip-models.md §2: the five-layer line model (static Hammerstad-Jensen Z₀/εeff + finite
/// thickness, Kirschning-Jansen dispersion, Wheeler conductor loss + Hammerstad-Bekkadal
/// roughness, dielectric loss) computed fresh per frequency inside <see cref="Stamp"/> — exactly
/// like <see cref="TLineModel"/> already is — and stamped via the SAME
/// <see cref="TLineModel.StampUniformLine"/> nodal-admittance equations (R-pc-11/R3: "one
/// implementation," never a second copy). <see cref="TLineModel"/>'s own θ=E·(f/F) model is
/// non-dispersive by construction and cannot express Kirschning-Jansen's genuine εeff(f)/Z₀(f)
/// curvature — that is why MLIN computes its own γ(f) rather than being built as a parameterized
/// <see cref="TLineModel"/> instance.
///
/// Parameters (SI, already resolved by the elaborator — see the substrate-injection seam in
/// <c>src/Ui/Schematic/</c> for how a schematic instance's H/Er/T/Sigma/TanD get here from a
/// workspace's resolved technology): <c>W</c>, <c>L</c> — line width/length (m). <c>H</c> — the
/// resolved substrate height (m). <c>T</c> — signal conductor thickness (m). <c>Er</c> — dielectric
/// εᵣ. <c>Sigma</c> — conductor conductivity (S/m). <c>TanD</c> — dielectric loss tangent.
/// <c>Roughness</c> — OPTIONAL RMS surface roughness (m, default 0 = smooth).
///
/// R-pc-16/microstrip-models.md R4: out-of-range parameters are reported once per distinct
/// violation via <see cref="MicrostripValidityReporter"/>, never silently extrapolated.
/// </summary>
public sealed class MicrostripLineModel : ComponentModel, IReportsWarnings
{
    public override int PortCount => 2;
    public override ModelKind Kind => ModelKind.Linear;

    private readonly double _wMeters, _lMeters, _hMeters, _tMeters, _epsR, _sigmaSPerM, _tanD, _roughnessMeters;
    private readonly MicrostripValidityReporter _reporter;

    /// <summary>R-mk-7/8 (brief-mklopf-performance-and-messages.md): routes this instance's
    /// validity-range warnings into ElaboratedNetlist.Warnings via the engine's post-Stamp drain —
    /// see IReportsWarnings' own doc comment.</summary>
    public IReadOnlyList<(string Key, string Message)> DrainWarnings() => _reporter.Drain();

    public MicrostripLineModel(double wMeters, double lMeters, double hMeters, double tMeters,
        double epsR, double sigmaSPerM, double tanD, string instancePath, double roughnessMeters = 0.0)
    {
        _wMeters = wMeters;
        _lMeters = lMeters;
        _hMeters = hMeters;
        _tMeters = tMeters;
        _epsR = epsR;
        _sigmaSPerM = sigmaSPerM;
        _tanD = tanD;
        _roughnessMeters = roughnessMeters;
        _reporter = new MicrostripValidityReporter(instancePath);
    }

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        double freqHz = omega / (2.0 * Math.PI);
        double u = _wMeters / _hMeters;

        var (z0Static, eeff0) = HammerstadJensen.Compute(_wMeters, _hMeters, _tMeters, _epsR, _reporter);

        double z0 = z0Static, eeff = eeff0;
        if (freqHz > 0.0)
            (z0, eeff) = KirschningJansen.Compute(freqHz, u, _epsR, _hMeters, z0Static, eeff0, _reporter);

        double alphaNpPerM = 0.0;
        if (freqHz > 0.0)
        {
            alphaNpPerM += MicrostripLoss.ConductorLossNpPerM(freqHz, _sigmaSPerM, _wMeters, z0, _roughnessMeters);
            alphaNpPerM += MicrostripLoss.DielectricLossNpPerM(freqHz, _epsR, eeff, _tanD);
        }

        double betaRadPerM = freqHz > 0.0
            ? 2.0 * Math.PI * freqHz / MicrostripLoss.SpeedOfLight * Math.Sqrt(eeff)
            : 0.0;

        var gammaLength = new Complex(alphaNpPerM * _lMeters, betaRadPerM * _lMeters);
        TLineModel.StampUniformLine(mna, c.Nodes[0], c.Nodes[1], new Complex(z0, 0.0), gammaLength);
    }
}
