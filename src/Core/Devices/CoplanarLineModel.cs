using System.Numerics;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Devices.Planar;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// A uniform line whose per-unit-length description is computed per frequency — MLIN, CPWG and SLIN. What
/// the line calculator and the parameter editor's impedance readout ask, so each reports what an
/// elaborated line IS rather than evaluating its closed forms a second time.
/// </summary>
public interface IPlanarLineModel : IReportsWarnings
{
    /// <summary>The engine reference: "MLIN", "CPWG" or "SLIN".</summary>
    string LineKind { get; }

    /// <summary>What <see cref="ComponentModel.Stamp"/> stamps at <paramref name="freqHz"/>; 0 Hz is the
    /// static line, lossless.</summary>
    MicrostripLineParameters LineParameters(double freqHz);
}

/// <summary>
/// CPWG — conductor-backed (grounded) coplanar waveguide, 2-port, ground-referenced (brief-artsch-1
/// R-as1-1; docs/design/planar-line-models.md §2). Stamped by <see cref="TLineModel.StampUniformLine"/>, as
/// MLIN and TLIN are.
///
/// <para>Parameters (SI, resolved by the elaborator; a schematic instance's H/T/Er/Sigma/TanD are injected
/// from its technology by <c>PlanarLineSubstrateInjection</c> as MLIN's are): <c>W</c> centre strip,
/// <c>G</c> gap to the coplanar ground on each side, <c>L</c> length, <c>H</c> substrate to the backing
/// plane, <c>T</c> conductor thickness, <c>Er</c>, <c>TanD</c>, <c>Sigma</c>, and the optional RMS
/// <c>Roughness</c>.</para>
///
/// <para>Static Z₀ and ε_eff from <see cref="GroundedCoplanar.Static"/>; dispersion by Frankel et al. on the
/// coplanar branch and by MLIN's Kirschning–Jansen on the microstrip one, so a CPWG whose grounds have
/// gone far away IS an MLIN at every frequency; conductor loss by Wheeler's incremental-inductance rule
/// (<see cref="PlanarLineLoss"/>); dielectric loss from the filling factor, MLIN's
/// <see cref="MicrostripLoss.DielectricLossNpPerM"/>. Out-of-range geometry is a warning, never a refusal.</para>
/// </summary>
public sealed class CoplanarLineModel : ComponentModel, IPlanarLineModel, IDcShortedTerminals, IUniformLine
{
    /// <summary>Lossless at DC (every loss term here vanishes at 0 Hz): a short from port 1 to port 2.</summary>
    public IReadOnlyList<(int A, int B)> DcShortedTerminals { get; } = [(0, 1)];

    public override int PortCount => 2;
    public override ModelKind Kind => ModelKind.Linear;
    public string LineKind => GroundedCoplanar.ModelName;

    private readonly double _w, _g, _l, _h, _t, _er, _sigma, _tanD, _roughness;
    private readonly MicrostripValidityReporter _reporter;

    public IReadOnlyList<(string Key, string Message)> DrainWarnings() => _reporter.Drain();

    public CoplanarLineModel(double wMeters, double gMeters, double lMeters, double hMeters, double tMeters,
        double epsR, double sigmaSPerM, double tanD, string instancePath, double roughnessMeters = 0.0)
    {
        _w = wMeters; _g = gMeters; _l = lMeters; _h = hMeters; _t = tMeters;
        _er = epsR; _sigma = sigmaSPerM; _tanD = tanD; _roughness = roughnessMeters;
        _reporter = new MicrostripValidityReporter(instancePath);
    }

    public MicrostripLineParameters LineParameters(double freqHz)
    {
        const string model = "CPWG (Ghione-Naldi)";
        _reporter.CheckRange(model, "W/H", _w / _h, GroundedCoplanar.WOverHRange.Min, GroundedCoplanar.WOverHRange.Max);
        _reporter.CheckRange(model, "G/H", _g / _h, GroundedCoplanar.GOverHRange.Min, GroundedCoplanar.GOverHRange.Max);
        _reporter.CheckRange(model, "T/G", _t / _g, GroundedCoplanar.TOverGRange.Min, GroundedCoplanar.TOverGRange.Max);

        var s = GroundedCoplanar.Static(_w, _g, _h, _t, _er, _reporter);
        double z0s = s.Z0, e0 = s.Eeff;
        double z0 = z0s, eeff = e0;
        if (freqHz > 0)
        {
            if (s.Microstrip)
            {
                (z0, eeff) = KirschningJansen.Compute(freqHz, _w / _h, _er, _h, z0s, e0, _reporter);
            }
            else
            {
                const string disp = "CPWG dispersion (Frankel)";
                _reporter.CheckRange(disp, "W/G", _w / _g, GroundedCoplanar.DispersionWOverGRange.Min, GroundedCoplanar.DispersionWOverGRange.Max);
                _reporter.CheckRange(disp, "W/H", _w / _h, GroundedCoplanar.DispersionWOverHRange.Min, GroundedCoplanar.DispersionWOverHRange.Max);
                eeff = GroundedCoplanar.FrankelEeff(freqHz, _w, _g, _h, _er, e0);
                z0 = z0s * Math.Sqrt(e0 / eeff);
            }
        }

        double alphaC = 0, alphaD = 0, beta = 0, lossPhase = 0;
        if (freqHz > 0)
        {
            var quiet = new MicrostripValidityReporter("(CPWG recession, not reported)");
            double dn = PlanarLineLoss.RecessionFraction * (_t > 0 ? Math.Min(Math.Min(_w, _g), Math.Min(_h, _t)) : Math.Min(Math.Min(_w, _g), _h));
            double dt = _t > 0 ? 2 : 0;
            double Conductor(double f, double zz) => PlanarLineLoss.ConductorLossNpPerM(f, _sigma, _t, _roughness, zz,
                d => GroundedCoplanar.Static(_w - 2 * d, _g + 2 * d, _h + 2 * d, _t - dt * d, _er, quiet).Z0Air, dn);
            alphaC = Conductor(freqHz, z0);
            alphaD = _er > 1 ? MicrostripLoss.DielectricLossNpPerM(freqHz, _er, eeff, _tanD) : 0.0;
            beta = 2 * Math.PI * freqHz / MicrostripLoss.SpeedOfLight * Math.Sqrt(eeff);
            lossPhase = CausalLoss.Phase(freqHz, alphaC, Conductor(CausalLoss.ConductorFloorHz, z0s), alphaD);
        }
        return new MicrostripLineParameters(z0s, e0, z0, eeff, alphaC, alphaD, beta, lossPhase);
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
