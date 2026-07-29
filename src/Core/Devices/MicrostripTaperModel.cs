using System.Numerics;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// MTAPER — a linearly tapered microstrip line, 2-port. Width varies linearly from <c>W1</c> at
/// pin 1 to <c>W2</c> at pin 2 over length <c>L</c> (brief-mtaper-mklopf.md §1).
///
/// <b>Electrical model: a cascade of N short uniform MLIN sections</b>, each evaluated at its own
/// local width via the SAME Hammerstad-Jensen/Kirschning-Jansen/loss physics
/// <see cref="MicrostripLineModel"/> uses (R7/R-pc-12's "one implementation") — never a second
/// transcription of that physics. Each section's own ABCD matrix
/// (<see cref="MicrostripAbcd.UniformSection"/>) is cascaded in physical order (port 1 → port 2);
/// the overall 2-port is converted to Z-parameters and stamped via the SAME branch-current
/// technique <see cref="MicrostripBendModel"/> uses for its own closed-form Z-matrix.
///
/// <b>N is resolved per <see cref="MicrostripCascadeSectioning"/> (R-tap-1) — the larger of the
/// electrically-short-sections criterion and the profile-resolution criterion</b> — re-evaluated at
/// each frequency actually stamped (a lower analysis frequency needs fewer sections; see that
/// class's own doc comment for why this is the practical reading of "derive from the analysis
/// sweep" available to a per-frequency <c>Stamp</c> call). <see cref="LastSectionCount"/> exposes
/// the value used, and it is reported once via stderr on first use (R-tap-1's "report the value
/// used").
///
/// <b>R-tap-2: the artwork's own tessellation is a SEPARATE, purely geometric decision</b> — for
/// MTaper specifically, the drawn outline is an exact 4-vertex trapezoid (a linear width profile
/// has no curvature to approximate), so there is no tessellation parameter to couple to N in the
/// first place; <c>MicrostripCascadeSectioning</c> exists as shared, profile-agnostic
/// infrastructure precisely so a FUTURE curved profile (MKlopf) can reuse the identical electrical
/// rule while genuinely needing its own separate geometric tessellation decision.
/// </summary>
public sealed class MicrostripTaperModel : ComponentModel, IReportsWarnings
{
    public override int PortCount => 2;
    public override ModelKind Kind => ModelKind.Linear;

    private readonly double _w1, _w2, _l, _h, _t, _epsR, _sigma, _tanD;
    private readonly int _sectionCountOverride;
    private readonly string _instancePath;
    private readonly MicrostripValidityReporter _reporter;
    private bool _reportedN;

    /// <summary>R-mk-7/8 (brief-mklopf-performance-and-messages.md): routes this instance's
    /// validity-range warnings into ElaboratedNetlist.Warnings via the engine's post-Stamp drain —
    /// see IReportsWarnings' own doc comment. The section-count notice below is a separate,
    /// pre-existing direct console message, out of this brief's stated scope, and is untouched;
    /// MTaper's own sectioning/hoisting is likewise untouched (see MicrostripCascadeSectioning's
    /// own updated doc comment on why the new non-uniform log-Z helpers, though shareable, are not
    /// applied here in this pass).</summary>
    public IReadOnlyList<(string Key, string Message)> DrainWarnings() => _reporter.Drain();

    /// <summary>The number of cascade sections used on the most recent <see cref="Stamp"/> call
    /// (0 before the first call) — exposed for direct testing and for a future UI surface (R-tap-1's
    /// "report the value used").</summary>
    public int LastSectionCount { get; private set; }

    public MicrostripTaperModel(double w1Meters, double w2Meters, double lMeters,
        double hMeters, double tMeters, double epsR, double sigmaSPerM, double tanD,
        string instancePath, int sectionCountOverride = 0)
    {
        _w1 = w1Meters;
        _w2 = w2Meters;
        _l = lMeters;
        _h = hMeters;
        _t = tMeters;
        _epsR = epsR;
        _sigma = sigmaSPerM;
        _tanD = tanD;
        _instancePath = instancePath;
        _sectionCountOverride = sectionCountOverride;
        _reporter = new MicrostripValidityReporter(instancePath);
    }

    private double WidthAtFraction(double t) => _w1 + (_w2 - _w1) * t;

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        int n1 = c.Nodes[0], n2 = c.Nodes[1];
        double freqHz = omega / (2.0 * Math.PI);

        int n;
        if (_sectionCountOverride > 0)
        {
            n = _sectionCountOverride;
        }
        else
        {
            var (_, eeff1) = HammerstadJensen.Compute(_w1, _h, _t, _epsR, _reporter);
            var (_, eeff2) = HammerstadJensen.Compute(_w2, _h, _t, _epsR, _reporter);
            double eeffMax = Math.Max(eeff1, eeff2);
            n = MicrostripCascadeSectioning.Resolve(_l, freqHz, eeffMax, WidthAtFraction);
        }
        LastSectionCount = n;

        if (!_reportedN)
        {
            Console.Error.WriteLine(
                $"[circuitRF] MTAPER:{_instancePath}: cascade uses N={n} sections " +
                $"(W1={_w1:G3}m, W2={_w2:G3}m, L={_l:G3}m).");
            _reportedN = true;
        }

        if (omega <= 0.0)
        {
            // DC: every section is an ideal (lossless, zero-electrical-length) short at DC — the
            // whole cascade collapses to a direct tie between the two ports.
            mna.AddAdmittance(n1, n2, new Complex(1.0e9, 0.0));
            return;
        }

        double sectionLen = _l / n;
        var total = MicrostripAbcd.Identity;
        for (int i = 0; i < n; i++)
        {
            double wMid = WidthAtFraction((i + 0.5) / n);
            var (z0Static, eeff0) = HammerstadJensen.Compute(wMid, _h, _t, _epsR, _reporter);
            var (z0, eeff) = KirschningJansen.Compute(freqHz, wMid / _h, _epsR, _h, z0Static, eeff0, _reporter);

            double alphaNpPerM = MicrostripLoss.ConductorLossNpPerM(freqHz, _sigma, wMid, z0)
                + MicrostripLoss.DielectricLossNpPerM(freqHz, _epsR, eeff, _tanD);
            double betaRadPerM = 2.0 * Math.PI * freqHz / MicrostripLoss.SpeedOfLight * Math.Sqrt(eeff);
            var gammaLength = new Complex(alphaNpPerM * sectionLen, betaRadPerM * sectionLen);

            var section = MicrostripAbcd.UniformSection(new Complex(z0, 0.0), gammaLength);
            total = total.Cascade(section);
        }

        var (z11, z12, z21, z22) = total.ToZ();

        int b1 = mna.AddBranch();
        int b2 = mna.AddBranch();
        mna.AddBranchCurrent(b1, n1, 0);
        mna.AddBranchCurrent(b2, n2, 0);
        mna.AddConstraint(b1, n1, Complex.One);
        mna.AddConstraint(b2, n2, Complex.One);
        mna.AddBranchConstraint(b1, b1, -z11);
        mna.AddBranchConstraint(b1, b2, -z12);
        mna.AddBranchConstraint(b2, b1, -z21);
        mna.AddBranchConstraint(b2, b2, -z22);
    }
}
