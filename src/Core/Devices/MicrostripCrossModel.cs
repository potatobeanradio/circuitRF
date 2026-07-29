using System.Numerics;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// MCROSS — microstrip cross-junction, 4-port (arms at ±X/±Y from centre — R-pc-3's own
/// symmetric-junction convention; arm1=+X/right, arm2=-Y/up, arm3=-X/left, arm4=+Y/down).
///
/// <b>Gap closed (2026-07-29): the C/L1/L2/L3 closed forms were located and confirmed.</b> Source:
/// Gupta, Garg &amp; Chadha, <i>Computer-Aided Design of Microwave Circuits</i>, Artech House, 1981,
/// §6.2.7 "Cross-Junction", eqs (6.44)-(6.45) (<c>docs/sonnet-briefs/extract.pdf</c>, image-only
/// scan, transcribed directly from the page images at up to 1200 DPI per R1/R19 — the circuit
/// topology in particular (below) was re-verified against three independent high-resolution crops
/// of Fig. 6.3(e) before being trusted, specifically because it turned out to be asymmetric in a
/// way that is easy to misread). The earlier "ideal junction" stamp (Garg-Bahl's own cross-junction
/// paper being unobtainable at the time) is retired.
///
/// <b>Topology (Fig. 6.3(e) — CONFIRMED ASYMMETRIC, not a misreading):</b> a single internal star
/// node (the junction centre) plus ONE extra internal node on the arm2 side only:
/// <list type="bullet">
/// <item>arm1 (through) — shunt <c>C</c> at the port, series <c>L1</c> to the centre.</item>
/// <item>arm3 (through) — shunt <c>C</c> at the port, series <c>L1</c> (the SAME value) to the
/// centre.</item>
/// <item>arm4 (stub) — shunt <c>C</c> at the port, series <c>L2</c> DIRECTLY to the centre.</item>
/// <item>arm2 (stub) — shunt <c>C</c> at the port, series <c>L2</c> (the SAME value as arm4's) to
/// an extra internal node, then series <c>L3</c> from there to the centre.</item>
/// </list>
/// The source draws <c>L3</c> once, on the arm2 side only — confirmed by two independent
/// high-resolution crops of the figure (arm4's path shows exactly one coil between the centre and
/// the port; arm2's shows two, with the shunt <c>C</c> at the port in both cases). This asymmetry
/// is the source's own equivalent-circuit choice (a reference-plane/fitting convenience), not
/// something this implementation introduces or could "correct" toward apparent symmetry without
/// fabricating data — arm2 vs. arm4 is this component's own arbitrary-but-fixed mapping of that
/// choice onto ±Y, recorded here so it is never silently re-derived differently later.
///
/// <b>Y-matrix derivation (own algebra, not transcribed — only C/L1/L2/L3 are from the source):</b>
/// with a1=1/(jωL1), a2=1/(jωL2), a3=1/(jωL3), yc=jωC, sum23=a2+a3,
/// E = 2·a1·sum23 + a2·(a2+2·a3), k1=a1·sum23/E, k2=a2·a3/E, k4=a2·sum23/E, the reduced 4×4 port
/// admittance block (eliminating both internal nodes via KCL) is:
/// Y11=Y33=yc+a1·(1−k1); Y13=Y31=−a1·k1; Y12=Y21=Y23=Y32=−a1·a2·a3/E;
/// Y14=Y41=Y34=Y43=−a1·a2·sum23/E; Y22=yc+a2·a3·(1−k2)/sum23; Y44=yc+a2·(1−k4);
/// Y24=Y42=−a2²·a3/E. Confirmed reciprocal (every off-diagonal pair equal) before use.
///
/// <b>R11/R22 (microstrip-models.md): the source's own model is parameterised by exactly TWO
/// widths</b> — a uniform through-line width and a uniform stub width (its own "W1"/"W2",
/// confirming R22's symmetry-restriction finding directly from the primary source, not by
/// inference). This component accepts four independent widths; opposing arms are averaged
/// (R11's "preferable" option — usable, never silently mean-substituted) and the divergence is
/// reported once, exactly as before this gap was closed.
///
/// <b>R24 (microstrip-models.md): calibrated at εr = 9.9</b>, same caveat as <c>MicrostripTeeModel</c>
/// — no εr-scaling law is given by the source for this section either.
///
/// <b>Quasi-static, no dispersion</b> — same as <c>MicrostripTeeModel</c>: C/L1/L2/L3 carry no
/// frequency term in the source; static Hammerstad-Jensen Z0/eeff feed the through/stub Lw values
/// only where <see cref="HammerstadJensen"/> itself is invoked for the validity-range machinery
/// (R7/R-pc-12 — the same static physics <see cref="MicrostripLineModel"/> uses).
/// </summary>
public sealed class MicrostripCrossModel : ComponentModel, IReportsWarnings
{
    public override int PortCount => 4;
    public override ModelKind Kind => ModelKind.Linear;
    public override string[] TerminalNames => ["arm1", "arm2", "arm3", "arm4"];

    private readonly double _w1, _w2, _w3, _w4, _h, _t, _epsR;
    private readonly string _instancePath;
    private readonly MicrostripValidityReporter _reporter;
    private bool _warnedAsymmetry;
    private bool _warnedEpsR;

    /// <summary>R-mk-7/8 (brief-mklopf-performance-and-messages.md): routes this instance's
    /// validity-range warnings into ElaboratedNetlist.Warnings via the engine's post-Stamp drain —
    /// see IReportsWarnings' own doc comment. This model's own separate direct console notices
    /// below are pre-existing, out of this brief's stated scope, and are untouched.</summary>
    public IReadOnlyList<(string Key, string Message)> DrainWarnings() => _reporter.Drain();

    public MicrostripCrossModel(double w1Meters, double w2Meters, double w3Meters, double w4Meters,
        double hMeters, double tMeters, double epsR, double sigmaSPerM, double tanD, string instancePath)
    {
        _w1 = w1Meters;
        _w2 = w2Meters;
        _w3 = w3Meters;
        _w4 = w4Meters;
        _h = hMeters;
        _t = tMeters;
        _epsR = epsR;
        _instancePath = instancePath;
        _reporter = new MicrostripValidityReporter(instancePath);
        _ = (tMeters, sigmaSPerM, tanD); // conductor/dielectric loss: not part of this quasi-static LC model
    }

    /// <summary>True when the R11 opposing-pair-mean approximation is in effect for this
    /// instance's parameters (W1≠W3 or W2≠W4) — exposed for direct testing of the report gate
    /// independent of the runtime stderr warning.</summary>
    public bool UsesOpposingMeanApproximation(out double w13DivergenceFraction, out double w24DivergenceFraction)
    {
        double w13Mean = (_w1 + _w3) / 2.0;
        double w24Mean = (_w2 + _w4) / 2.0;
        w13DivergenceFraction = w13Mean > 0 ? Math.Abs(_w1 - _w3) / w13Mean : 0.0;
        w24DivergenceFraction = w24Mean > 0 ? Math.Abs(_w2 - _w4) / w24Mean : 0.0;
        return _w1 != _w3 || _w2 != _w4;
    }

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        int n1 = c.Nodes[0], n2 = c.Nodes[1], n3 = c.Nodes[2], n4 = c.Nodes[3];

        if (omega <= 0.0)
        {
            // DC: ideal inductors short, the shunt capacitors open — all four ports collapse to
            // one node, the true DC limit of this same LC network.
            var gDc = new Complex(1.0e9, 0.0);
            mna.AddAdmittance(n1, n2, gDc);
            mna.AddAdmittance(n1, n3, gDc);
            mna.AddAdmittance(n1, n4, gDc);
            return;
        }

        if (!_warnedEpsR && (_epsR < 9.0 || _epsR > 10.8))
        {
            Console.Error.WriteLine(
                $"[circuitRF] MCROSS:{_instancePath}: C/L1/L2/L3 (Gupta-Garg-Chadha eq 6.44-6.45) are " +
                $"calibrated at a single point, epsR=9.9, with no epsR-scaling law given by the " +
                $"source; this instance's epsR={_epsR:G4} is an extrapolation beyond that.");
            _warnedEpsR = true;
        }

        if (!_warnedAsymmetry && UsesOpposingMeanApproximation(out double d13, out double d24))
        {
            Console.Error.WriteLine(
                $"[circuitRF] MCROSS:{_instancePath}: opposing arms are not equal-width " +
                $"(W1={_w1:G3}m/W3={_w3:G3}m diverge {d13:P1}; W2={_w2:G3}m/W4={_w4:G3}m diverge {d24:P1}) " +
                "— the source's own cross-junction model (microstrip-models.md R11/R22) requires opposing " +
                "arms equal; this uses the arithmetic-mean-of-each-pair approximation, valid only while " +
                "opposing widths are similar.");
            _warnedAsymmetry = true;
        }

        double wThrough = (_w1 + _w3) / 2.0;
        double wStub = (_w2 + _w4) / 2.0;
        double u1 = wThrough / _h; // source's own "W1/h" (through)
        double u2 = wStub / _h;    // source's own "W2/h" (stub)

        _reporter.CheckRange("MCross", "ThroughW/h(C)", u1, 0.3, 3.0);
        _reporter.CheckRange("MCross", "StubW/h(C)", u2, 0.1, 3.0);
        _reporter.CheckRange("MCross", "ThroughW/h(L)", u1, 0.5, 2.0);
        _reporter.CheckRange("MCross", "StubW/h(L)", u2, 0.5, 2.0);

        // eq (6.44): C/W1 (pF/m).
        double bracket = (37.61 * u2 - 13.42 * Math.Sqrt(u2) + 159.38) * Math.Log(u1)
            + Math.Pow(u2, 3) + 74.0 * u2 + 130.0;
        double cOverW1 = 0.25 * bracket * Math.Pow(u1, -1.0 / 3.0) - 60.0 + 0.5 / u2 - 0.375 * u1 * (1.0 - u2);
        double cCap = cOverW1 * wThrough * 1e-12;

        // eq (6.45a): L1/h (nH/m).
        double l1OverH = ((165.6 * u2 + 31.2 * Math.Sqrt(u2) - 11.8 * u2 * u2) * u1 - 32.0 * u2 + 3.0)
            * Math.Pow(u1, -1.5);
        double l1 = l1OverH * _h * 1e-9;

        // L2/h: "obtained by replacing W1 by W2 and vice versa in (6.45a)".
        double l2OverH = ((165.6 * u1 + 31.2 * Math.Sqrt(u1) - 11.8 * u1 * u1) * u2 - 32.0 * u1 + 3.0)
            * Math.Pow(u2, -1.5);
        double l2 = l2OverH * _h * 1e-9;

        // eq (6.45b): "-L3/h (nH/m) = ...", i.e. L3/h is the negative of the RHS.
        double l3OverHNegated = 337.5 + (1.0 + 7.0 / u1) / u2 - 5.0 * u2 * Math.Cos(Math.PI / 2.0 * (1.5 - u1));
        double l3 = -l3OverHNegated * _h * 1e-9;

        var a1 = 1.0 / (Complex.ImaginaryOne * omega * l1);
        var a2 = 1.0 / (Complex.ImaginaryOne * omega * l2);
        var a3 = 1.0 / (Complex.ImaginaryOne * omega * l3);
        var yc = new Complex(0.0, omega * cCap);

        var sum23 = a2 + a3;
        var e = 2.0 * a1 * sum23 + a2 * (a2 + 2.0 * a3);
        var k1 = a1 * sum23 / e;
        var k2 = a2 * a3 / e;
        var k4 = a2 * sum23 / e;

        var yThroughThrough = yc + a1 * (1.0 - k1);      // Y11 = Y33
        var yThroughAcross = -a1 * k1;                    // Y13 = Y31
        var yThroughStub2 = -(a1 * a2 * a3) / e;           // Y12=Y21=Y23=Y32 (arm2, the L3-bearing stub)
        var yThroughStub4 = -(a1 * a2 * sum23) / e;        // Y14=Y41=Y34=Y43 (arm4, the direct stub)
        var y22 = yc + a2 * a3 * (1.0 - k2) / sum23;       // Y22
        var y44 = yc + a2 * (1.0 - k4);                    // Y44
        var y24 = -(a2 * a2 * a3) / e;                     // Y24 = Y42

        // Diagonal.
        mna.AddBlockAdmittance(n1, n1, yThroughThrough);
        mna.AddBlockAdmittance(n3, n3, yThroughThrough);
        mna.AddBlockAdmittance(n2, n2, y22);
        mna.AddBlockAdmittance(n4, n4, y44);

        // Off-diagonal, both directions (reciprocal).
        mna.AddBlockAdmittance(n1, n3, yThroughAcross);
        mna.AddBlockAdmittance(n3, n1, yThroughAcross);

        mna.AddBlockAdmittance(n1, n2, yThroughStub2);
        mna.AddBlockAdmittance(n2, n1, yThroughStub2);
        mna.AddBlockAdmittance(n3, n2, yThroughStub2);
        mna.AddBlockAdmittance(n2, n3, yThroughStub2);

        mna.AddBlockAdmittance(n1, n4, yThroughStub4);
        mna.AddBlockAdmittance(n4, n1, yThroughStub4);
        mna.AddBlockAdmittance(n3, n4, yThroughStub4);
        mna.AddBlockAdmittance(n4, n3, yThroughStub4);

        mna.AddBlockAdmittance(n2, n4, y24);
        mna.AddBlockAdmittance(n4, n2, y24);
    }
}
