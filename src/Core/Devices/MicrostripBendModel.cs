using System.Numerics;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// MBEND — microstrip right-angle/general-angle bend, 2-port.
///
/// <b>Gap closed (2026-07-29, brief-mtaper-mklopf.md §1A): the L-C-L equivalent circuit is now
/// implemented</b>, replacing the earlier geometry-only (miter-length-as-line-shortening) stand-in.
/// Source: Kirschning, Jansen &amp; Koster 1983, as reproduced in Poole &amp; Darwazeh's <i>Lecture 3 -
/// Practical Transmission Lines</i> eqs (20)-(25) — see <see cref="MicrostripBendLC"/> for the
/// coefficients themselves and their own honesty notes (units verification, the Optimal-miter
/// coefficient gap, the validity-range inference).
///
/// <b>Topology — eq (25)'s own Z-matrix, stamped directly, no Y-matrix inversion needed:</b>
/// <c>Z = [[jωL+1/(jωC), 1/(jωC)], [1/(jωC), jωL+1/(jωC)]]</c> — a symmetric T-network (series L
/// each side, meeting at an internal node shunted to ground by C). Stamped via the SAME
/// branch-current Z-parameter technique <see cref="ZPortModel"/> already uses for an arbitrary
/// Z(ω) N-port (single-ended: each port's own node against ground, no internal MNA node needed —
/// the T-network's centre node is eliminated analytically by the Z-matrix itself).
///
/// <b>Chamfer geometry (R-bnd-1, unchanged from before this gap closed — it was already correct):
/// </b> <see cref="MicrostripDiscontinuities.MiterCutLength"/> already returns the per-outer-edge
/// LEG of the 45°-cut isoceles triangle (= (M/100)·W, where M/100 is Douville &amp; James's own
/// diagonal-referenced fraction and the √2 cancels between the diagonal distance and the per-edge
/// projection — see that class's own doc comment for the full derivation). This model does not use
/// that geometry directly (the electrical model no longer stands in for missing reactance data via
/// a length correction); the PCell (<c>MBendPCell</c>) uses it for the ARTWORK cut, independently.
///
/// <b>R-pc-18 (mitered vs. unmitered are distinct discontinuities) is now satisfied by real,
/// distinct published L/C values</b> (None vs. Fifty/Optimal-with-Fifty's-coefficients), not merely
/// by a geometric length proxy.
/// </summary>
public sealed class MicrostripBendModel : ComponentModel, IReportsWarnings
{
    public override int PortCount => 2;
    public override ModelKind Kind => ModelKind.Linear;

    private readonly double _wMeters, _hMeters, _epsR;
    private readonly MicrostripBendMiter _miter;
    private readonly MicrostripValidityReporter _reporter;
    private bool _warnedApproximated;
    private readonly string _instancePath;

    /// <summary>R-mk-7/8 (brief-mklopf-performance-and-messages.md): routes this instance's
    /// validity-range warnings into ElaboratedNetlist.Warnings via the engine's post-Stamp drain —
    /// see IReportsWarnings' own doc comment. The Optimal-miter approximation notice below is a
    /// separate, pre-existing direct console message, out of this brief's stated scope, and is
    /// untouched.</summary>
    public IReadOnlyList<(string Key, string Message)> DrainWarnings() => _reporter.Drain();

    public MicrostripBendModel(double wMeters, double angleDeg, MicrostripBendMiter miter,
        double hMeters, double tMeters, double epsR, double sigmaSPerM, double tanD, string instancePath)
    {
        _wMeters = wMeters;
        _hMeters = hMeters;
        _epsR = epsR;
        _miter = miter;
        _instancePath = instancePath;
        _reporter = new MicrostripValidityReporter(instancePath);
        _ = (angleDeg, tMeters, sigmaSPerM, tanD); // reserved: no verified angle/loss dependence is available (see MicrostripBendLC)
    }

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        int n1 = c.Nodes[0], n2 = c.Nodes[1];

        if (omega <= 0.0)
        {
            // DC: both series inductors are shorts, so port1/port2/the internal node all collapse
            // to one point regardless of the (open, at DC) shunt capacitor — the true DC limit.
            mna.AddAdmittance(n1, n2, new Complex(1.0e9, 0.0));
            return;
        }

        var (l, cap, approximated) = MicrostripBendLC.Compute(_wMeters, _hMeters, _epsR, _miter, _reporter);

        if (!_warnedApproximated && approximated)
        {
            Console.Error.WriteLine(
                $"[circuitRF] MBEND:{_instancePath}: Optimal-miter geometry has no matching published " +
                "electrical coefficients (Douville-James characterised the geometry, not the residual " +
                "reactance, per their own 1978 paper being inaccessible here) — using the 50%-mitered " +
                "(Fifty) coefficients as the nearest available data point. See MicrostripBendLC's own " +
                "doc comment.");
            _warnedApproximated = true;
        }

        var zSeries = new Complex(0.0, omega * l);
        var zShunt = 1.0 / new Complex(0.0, omega * cap);
        var z11 = zSeries + zShunt;
        var z12 = zShunt;

        int b1 = mna.AddBranch();
        int b2 = mna.AddBranch();
        mna.AddBranchCurrent(b1, n1, 0);
        mna.AddBranchCurrent(b2, n2, 0);
        mna.AddConstraint(b1, n1, Complex.One);
        mna.AddConstraint(b2, n2, Complex.One);
        mna.AddBranchConstraint(b1, b1, -z11);
        mna.AddBranchConstraint(b1, b2, -z12);
        mna.AddBranchConstraint(b2, b1, -z12);
        mna.AddBranchConstraint(b2, b2, -z11);
    }
}
