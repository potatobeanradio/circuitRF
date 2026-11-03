using System.Numerics;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// MTEE — microstrip T-junction, 3-port (pin 1 through-arm input, pin 2 through-arm output,
/// pin 3 branch — R-pc-3's own MTee convention).
///
/// <b>Gap closed (2026-07-29): the L1/L2/CT closed forms were located and confirmed.</b> Source:
/// Gupta, Garg &amp; Chadha, <i>Computer-Aided Design of Microwave Circuits</i>, Artech House, 1981,
/// §6.2.6 "T-Junction", eqs (6.41)-(6.42) (<c>docs/sonnet-briefs/extract.pdf</c>, an image-only
/// scan read at 320-1200 DPI, transcribed directly from the page images per R1/R19 — never from
/// memory or from a secondary summary). This traces to the SAME Garg &amp; Bahl / Gopinath lineage
/// §4 of <c>microstrip-models.md</c> already names; the earlier "ideal junction" stamp (Hammerstad-
/// Bekkadal's own T-junction paper being unobtainable) is retired.
///
/// <b>Topology (Fig. 6.3(d) of the source, confirmed by its own published 3-port S-matrix, eqs
/// 6.43a-d — this class does not stamp those S-parameters directly; it stamps the equivalent
/// Y-block derived from the same star network, below).</b> A single internal star node joins three
/// branches — series <c>L1</c> to the through-in port, series <c>L1</c> (the SAME value — the
/// source's own Fig. 6.3(d) draws one <c>L1</c> reused on both main-line legs, both referenced to
/// the same <c>Z1</c>) to the through-out port, and series <c>L2</c> to the branch port — plus a
/// single shunt <c>CT</c> from the star node to ground. No reference-plane shift beyond what the
/// star network itself already represents (R-pc-3's pin positions are unchanged).
///
/// <b>Y-matrix derivation (own algebra, not transcribed — only L1/L2/CT are from the source):</b>
/// with A = 1/(jωL1), B = 1/(jωL2), Yc = jωCT, D = 2A+B+Yc, eliminating the star node via KCL gives
/// a reciprocal 3×3 port admittance block: Y11=Y22=A−A²/D, Y33=B−B²/D, Y12=Y21=−A²/D,
/// Y13=Y31=Y23=Y32=−AB/D.
///
/// <b>Main-line width asymmetry (R11-style, applied here to MTee for the first time):</b> the
/// source's own model assumes ONE uniform main-line width (its own "W1", referenced identically at
/// both through ports, per Fig. 6.3(d)'s single "Z1" label on both sides). This component accepts
/// independent through-in/through-out widths; when they differ, the arithmetic mean is used for the
/// main-line-width terms in eqs (6.41)/(6.42), and the divergence is reported once, never silently.
///
/// <b>R24 (microstrip-models.md): this model's coefficients are calibrated at a single point, εr =
/// 9.9 (alumina) — the source states this explicitly and, unlike its own gap/step sections, gives
/// no εr-scaling law for the T-junction.</b> Evaluating at any other εr (FR-4's 4.4, for instance)
/// is therefore an extrapolation beyond what the source itself validates, reported once per
/// instance rather than silently assumed accurate.
///
/// <b>Quasi-static, no dispersion:</b> the source's own equations carry no frequency term — L1, L2,
/// CT are frequency-independent lumped values (the "closed-form curve fit" framing). The per-arm
/// characteristic impedance/eeff feeding eq (6.37)'s <c>Lw</c> and eq (6.41)'s stub <c>Zo</c> are
/// the SAME static Hammerstad-Jensen values <see cref="MicrostripLineModel"/> uses (R7/R-pc-12's
/// "one implementation"), evaluated without Kirschning-Jansen dispersion — matching the source's
/// own quasi-static scope.
///
/// <b>Arms (brief DFR26-5 R-dfr26-5-3): the tee simulates the arms it draws.</b> <c>L1</c>/<c>L2</c>/<c>L3</c>
/// are each arm's length from the junction centre to its pin, as <c>MTeePCell</c> draws them. The
/// through arms carry MLIN sections of <c>L1 − W3/2</c> and <c>L2 − W3/2</c> and the branch one of
/// <c>L3 − max(W1, W2)/2</c> — the reference planes the star network above is referred to — with the
/// junction between them (<see cref="JunctionArms"/>). When the branch has no metal past the through
/// line, the drawn shape is a straight line with a pin on it, so the tee is two lines <c>L1</c> and
/// <c>L2</c> meeting at pin 3 and carries no junction: with pin 3 open it IS an MLIN of <c>L1 + L2</c>.
/// A tee that declares no length (a file from before the lengths existed) is the junction alone, as it
/// always simulated, reported once.
/// </summary>
public sealed class MicrostripTeeModel : ComponentModel, IReportsWarnings
{
    public override int PortCount => 3;
    public override ModelKind Kind => ModelKind.Linear;
    public override string[] TerminalNames => ["through_in", "through_out", "branch"];

    private const double SpeedOfLight = 2.99792458e8;

    private readonly double _w1, _w2, _w3, _h, _t, _epsR;
    private readonly string _instancePath;
    private readonly MicrostripValidityReporter _reporter;
    private bool _warnedAsymmetry;
    private bool _warnedEpsR;

    // The arm sections, pin k → junction port k (null = no line: the pin is the junction's port), or —
    // in the straight-line limit — the full L1 and L2 lines meeting at pin 3. All null and not straight
    // when no length is declared: the junction alone.
    private readonly MicrostripLineModel?[] _arms = new MicrostripLineModel?[3];
    private readonly string? _unmodelledArms;

    /// <summary>True when the branch has no metal past the through line, so the tee is two lines
    /// joined at pin 3 with no junction.</summary>
    public bool IsStraightLine { get; }

    /// <summary>The length of line each arm carries to the junction (m) — 0 for an arm with none.
    /// In the straight-line limit, the two through lines' full lengths and 0.</summary>
    public IReadOnlyList<double> ArmSectionLengths { get; }

    /// <summary>R-mk-7/8 (brief-mklopf-performance-and-messages.md): routes this instance's
    /// validity-range warnings into ElaboratedNetlist.Warnings via the engine's post-Stamp drain —
    /// see IReportsWarnings' own doc comment. This model's own separate direct console notices
    /// below are pre-existing, out of this brief's stated scope, and are untouched.</summary>
    public IReadOnlyList<(string Key, string Message)> DrainWarnings()
        => [.. _reporter.Drain(), .. JunctionArms.DrainSections(_arms)];

    /// <param name="l1Meters">Arm lengths from the junction centre to each pin, as drawn; null where the
    /// instance declares none. All three null is the junction alone (a file written before the lengths
    /// existed).</param>
    public MicrostripTeeModel(double w1Meters, double w2Meters, double w3Meters,
        double hMeters, double tMeters, double epsR, double sigmaSPerM, double tanD, string instancePath,
        double? l1Meters = null, double? l2Meters = null, double? l3Meters = null, double roughnessMeters = 0.0)
    {
        _w1 = w1Meters;
        _w2 = w2Meters;
        _w3 = w3Meters;
        _h = hMeters;
        _t = tMeters;
        _epsR = epsR;
        _instancePath = instancePath;
        _reporter = new MicrostripValidityReporter(instancePath);

        double[] lengths = [0.0, 0.0, 0.0];
        if (l1Meters is null && l2Meters is null && l3Meters is null)
        {
            _unmodelledArms = "declares no arm lengths (L1, L2, L3), so its arms are not simulated — only the "
                + "junction is. Set L1-L3 to simulate the arms the layout draws.";
        }
        else
        {
            MicrostripLineModel? Line(double len, double w)
                => JunctionArms.Section(len, w, hMeters, tMeters, epsR, sigmaSPerM, tanD, roughnessMeters, instancePath);

            double branch = (l3Meters ?? 0.0) - Math.Max(w1Meters, w2Meters) / 2.0;
            IsStraightLine = l3Meters is not null && branch <= JunctionArms.ZeroLengthMeters;
            lengths = IsStraightLine
                ? [Math.Max(0.0, l1Meters ?? 0.0), Math.Max(0.0, l2Meters ?? 0.0), 0.0]
                : [Math.Max(0.0, (l1Meters ?? 0.0) - w3Meters / 2.0),
                   Math.Max(0.0, (l2Meters ?? 0.0) - w3Meters / 2.0),
                   Math.Max(0.0, branch)];
            _arms[0] = Line(lengths[0], w1Meters);
            _arms[1] = Line(lengths[1], w2Meters);
            _arms[2] = IsStraightLine ? null : Line(lengths[2], w3Meters);

            var missing = new[] { ("L1", l1Meters), ("L2", l2Meters), ("L3", l3Meters) }
                .Where(a => a.Item2 is null).Select(a => a.Item1).ToArray();
            if (missing.Length > 0)
                _unmodelledArms = $"declares no {string.Join(", ", missing)}, so "
                    + (missing.Length == 1 ? "that arm is" : "those arms are") + " not simulated.";
        }
        for (int k = 0; k < 3; k++) if (_arms[k] is null) lengths[k] = 0.0;
        ArmSectionLengths = lengths;
    }

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        int n1 = c.Nodes[0], n2 = c.Nodes[1], n3 = c.Nodes[2];

        if (omega <= 0.0)
        {
            // DC: ideal inductors are short circuits, the shunt capacitor is an open, and the arm
            // lines are lossless at DC — the three ports collapse to one node, which is the true DC
            // limit of this same network.
            var gDc = new Complex(1.0e9, 0.0);
            mna.AddAdmittance(n1, n2, gDc);
            mna.AddAdmittance(n1, n3, gDc);
            return;
        }

        if (_unmodelledArms is not null)
            _reporter.ReportOnce("MTee:Arms", $"MTEE {_unmodelledArms}");

        if (IsStraightLine)
        {
            // The drawn shape is a straight line with pin 3 on it: two lines meeting at pin 3.
            double freqHz = omega / (2.0 * Math.PI);
            JunctionArms.StampLine(mna, n1, n3, _arms[0], freqHz);
            JunctionArms.StampLine(mna, n3, n2, _arms[1], freqHz);
            return;
        }

        if (_arms.All(a => a is null))
        {
            StampJunction(mna, n1, n2, n3, omega);
            return;
        }

        JunctionArms.Stamp(mna, [n1, n2, n3], _arms, omega,
            (block, ports) => StampJunction(block, ports[0], ports[1], ports[2], omega));
    }

    /// <summary>The Gupta-Garg-Chadha star network on its three reference-plane nodes.</summary>
    private void StampJunction(IMnaContext mna, int n1, int n2, int n3, double omega)
    {
        if (!_warnedEpsR && (_epsR < 9.0 || _epsR > 10.8))
        {
            _reporter.ReportOnce($"MTEE:EpsR:{_instancePath}",
                $"L1/L2/CT (Gupta-Garg-Chadha eq 6.41-6.42) are " +
                $"calibrated at a single point, epsR=9.9, with no epsR-scaling law given by the " +
                $"source; this instance's epsR={_epsR:G4} is an extrapolation beyond that.");
            _warnedEpsR = true;
        }

        double wMain = (_w1 + _w2) / 2.0;
        if (!_warnedAsymmetry && _w1 != _w2)
        {
            double meanFrac = wMain > 0 ? Math.Abs(_w1 - _w2) / wMain : 0.0;
            _reporter.ReportOnce($"MTEE:Asymmetry:{_instancePath}",
                $"through_in/through_out widths differ " +
                $"(W1={_w1:G3}m, W2={_w2:G3}m, diverge {meanFrac:P1}) — the source's own model assumes " +
                "one uniform main-line width; the arithmetic mean is used for the L1/CT terms.");
            _warnedAsymmetry = true;
        }
        double wStub = _w3;

        double uMain = wMain / _h;
        double uStub = wStub / _h;

        var (z0Main, eeffMain) = HammerstadJensen.Compute(wMain, _h, _t, _epsR, _reporter);
        var (z0Stub, eeffStub) = HammerstadJensen.Compute(wStub, _h, _t, _epsR, _reporter);

        _reporter.CheckRange("MTee", "StubZ0", z0Stub, 25.0, 100.0, "ohm");
        _reporter.CheckRange("MTee", "MainW/h(L1)", uMain, 0.5, 2.0);
        _reporter.CheckRange("MTee", "StubW/h(L1)", uStub, 0.5, 2.0);
        _reporter.CheckRange("MTee", "MainW/h(L2)", uMain, 1.0, 2.0);
        _reporter.CheckRange("MTee", "StubW/h(L2)", uStub, 0.5, 2.0);

        // eq (6.37): Lw (H/m) for a uniform line of the given static Z0/eeff.
        double lwMainNhPerM = z0Main * Math.Sqrt(eeffMain) / SpeedOfLight * 1e9;
        double lwStubNhPerM = z0Stub * Math.Sqrt(eeffStub) / SpeedOfLight * 1e9;

        // eq (6.42a): L1/h (nH/m), a dimensionless bracket scaled by Lw(main).
        double l1OverH = -uStub * (uStub * (-0.016 * uMain + 0.064) + 0.016 / uMain) * lwMainNhPerM;
        double l1 = l1OverH * _h * 1e-9;

        // eq (6.42b): L2/h (nH/m), a dimensionless bracket scaled by Lw(stub).
        double l2Bracket = (0.12 * uMain - 0.47) * uStub + 0.195 * uMain - 0.357
            + 0.0283 * Math.Sin(Math.PI * uMain - 0.75 * Math.PI);
        double l2OverH = l2Bracket * lwStubNhPerM;
        double l2 = l2OverH * _h * 1e-9;

        // eq (6.41): CT/W1 (pF/m); W1 here is the source's main-line width (our wMain).
        double ctOverW1 = 100.0 / Math.Tanh(0.0072 * z0Stub) + 0.64 * z0Stub - 261.0;
        double ct = ctOverW1 * wMain * 1e-12;

        var a = 1.0 / (Complex.ImaginaryOne * omega * l1);
        var b = 1.0 / (Complex.ImaginaryOne * omega * l2);
        var yc = new Complex(0.0, omega * ct);
        var d = 2.0 * a + b + yc;

        var y11 = a - a * a / d;
        var y33 = b - b * b / d;
        var y12 = -(a * a) / d;
        var y13 = -(a * b) / d;

        mna.AddBlockAdmittance(n1, n1, y11);
        mna.AddBlockAdmittance(n2, n2, y11);
        mna.AddBlockAdmittance(n3, n3, y33);
        mna.AddBlockAdmittance(n1, n2, y12);
        mna.AddBlockAdmittance(n2, n1, y12);
        mna.AddBlockAdmittance(n1, n3, y13);
        mna.AddBlockAdmittance(n3, n1, y13);
        mna.AddBlockAdmittance(n2, n3, y13);
        mna.AddBlockAdmittance(n3, n2, y13);
    }
}
