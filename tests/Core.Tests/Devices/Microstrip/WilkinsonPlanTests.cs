using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.Microstrip;

/// <summary>
/// <see cref="WilkinsonPlan"/>'s numbers against the closed form (Wilkinson 1960; Pozar): arms of √2·Z0, a
/// quarter wave long at F in MLIN's own dispersive εeff, an isolation resistor of 2·Z0, and a ring that closes
/// across Gap.
/// </summary>
public class WilkinsonPlanTests
{
    /// <summary>20 mil RO4350B, 1 oz copper — the example workspace's board.</summary>
    private static readonly MicrostripSubstrate Ro4350b = new(0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037);

    private const double C0 = 2.99792458e8;

    private static MicrostripLineParameters Line(double w, double f)
        => new MicrostripLineModel(w, 1.0, 0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037, "test").LineParameters(f);

    [Theory]
    [InlineData(50.0, 2.4e9, 1.35e-3)]
    [InlineData(50.0, 5.8e9, 1.35e-3)]
    [InlineData(35.0, 1.0e9, 2.0e-3)]
    public void TheNumbers_AreTheClosedForm(double z0, double f, double gap)
    {
        var d = WilkinsonPlan.Build(z0, f, gap, Ro4350b);

        Assert.Equal(Math.Sqrt(2.0) * z0, d.ArmZ0, 12);
        Assert.Equal(2.0 * z0, d.IsolationOhms, 12);
        // Widths are MLIN's at F, held on the 2 nm grid: within a few milliohms of the asked impedance.
        Assert.Equal(z0, Line(d.W0Meters, f).Z0, 2);
        Assert.Equal(d.ArmZ0, Line(d.ArmWMeters, f).Z0, 2);
        Assert.Equal(Line(d.ArmWMeters, f).Eeff, d.ArmEeff, 12);
        Assert.Equal(C0 / (4.0 * f * Math.Sqrt(d.ArmEeff)), d.QuarterWaveMeters, 12);

        // Each arm, reference plane to reference plane: the input tee's line, then the curve's effective length.
        var tee = Assert.IsType<PlanTee>(d.Plan.Sections[0]);
        var curve = Assert.IsType<PlanCurve>(d.Plan.Sections[1]);
        double arm = tee.L1 - tee.W3 / 2.0 + MicrostripCurve.ElectricalLength(curve.Radius, curve.W, curve.AngleDeg);
        Assert.True(Math.Abs(arm - d.QuarterWaveMeters) < 4e-9, $"arm {arm} vs lambda/4 {d.QuarterWaveMeters}");
    }

    /// <summary>The curves come back facing each other across Gap: R2 and R3 are Gap apart, and every piece
    /// starts where the one before it ends.</summary>
    [Fact]
    public void TheRing_ClosesAcrossTheGap()
    {
        var d = WilkinsonPlan.Build(50.0, 2.4e9, 1.35e-3, Ro4350b);
        var s = d.Plan.Sections;
        var top = (PlanCurve)s[1];
        var outTop = (PlanTee)s[3];
        var outBottom = (PlanTee)s[4];

        Assert.Equal(["1", "2", "3", "R2", "R3"], d.Plan.Pins);
        Assert.Equal(top.X + 2.0 * top.Radius, outTop.X, 12);                 // the curve's far end is the output tee's
        Assert.Equal(top.Y, outTop.Y + outTop.L1 + outTop.L2, 12);
        Assert.Equal(d.GapMeters, outTop.Y - (outBottom.Y + outBottom.L1 + outBottom.L2), 12);   // R2 to R3
        Assert.Equal(1.35e-3, d.GapMeters, 15);
    }

    /// <summary>The equal split's plan, every number of it, fingerprinted as it was before the split ratio and section
    /// count existed: K = 1 and N = 1 must give the same doubles, bit for bit, so no placed divider moves.</summary>
    [Theory]
    [InlineData(50.0, 2.4e9, 1.35e-3, "5E0B0760C8247930")]
    [InlineData(50.0, 5.8e9, 1.35e-3, "EDA90C548615DF20")]
    [InlineData(35.0, 1.0e9, 2.0e-3, "73DB42B48E0BAF65")]
    public void TheEqualSplit_IsUnchangedBitForBit(double z0, double f, double gap, string fingerprint)
        => Assert.Equal(fingerprint, Fingerprint(WilkinsonPlan.Build(z0, f, gap, Ro4350b)));

    private static string Fingerprint(WilkinsonDesign d)
        => Fingerprint(d.Plan, [d.W0Meters, d.ArmZ0, d.ArmWMeters, d.ArmEeff, d.QuarterWaveMeters,
                                d.RingRadiusMeters, d.FeedMeters, d.IsolationOhms]);

    /// <summary>Every double of a plan, after <paramref name="head"/>, hashed: a change to any of them changes it.</summary>
    internal static string Fingerprint(PassivePlan plan, IEnumerable<double> head)
    {
        var bits = new List<double>(head);
        foreach (var section in plan.Sections)
        {
            bits.AddRange([section.X, section.Y, section.RotationDeg, .. section.Nodes.Select(n => (double)n)]);
            bits.AddRange(section switch
            {
                PlanTee t => [t.W1, t.W2, t.W3, t.L1, t.L2, t.L3],
                PlanCurve c => [c.W, c.AngleDeg, c.Radius],
                PlanLine l => [l.W, l.L],
                _ => throw new InvalidOperationException(),
            });
        }
        var bytes = bits.SelectMany(BitConverter.GetBytes).ToArray();
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16];
    }

    /// <summary>The variants at <c>Corner = Curve</c>, fingerprinted before the corners existed: every number of their
    /// plans and every line and resistor, bit for bit.</summary>
    [Theory]
    [InlineData(1.5, 1, 2.0, "9F27EA18D959577A")]
    [InlineData(0.6, 1, 2.0, "4312E3E826DFAE05")]
    [InlineData(1.0, 2, 2.0, "24F13F8689C487BA")]
    [InlineData(1.0, 3, 3.0, "387DC2EB25DDA04C")]
    public void TheVariants_AtCurvedCorners_AreUnchangedBitForBit(double k, int n, double band, string fingerprint)
    {
        var d = WilkinsonPlan.Build(50.0, 2.4e9, 1.35e-3, Ro4350b, k, n, band);
        Assert.Equal(fingerprint, Fingerprint(d.Plan,
        [
            d.W0Meters, d.ArmZ0, d.ArmWMeters, d.ArmEeff, d.QuarterWaveMeters, d.RingRadiusMeters, d.FeedMeters,
            .. d.Lines.SelectMany(l => new[] { l.Z, l.WMeters, l.Eeff, l.QuarterWaveMeters }),
            .. d.Resistors.Select(r => r.Ohms),
        ]));
    }

    // ══ Corners: a rectangle of MMITER or MBEND corners ═════════════════════════════════════════════

    /// <summary>With corners each arm is still λg/4 at F between the junctions' reference planes — the input tee's line,
    /// its straight runs, and two corners at their own electrical length from the corner's model — for every K and N; the
    /// runs in the plan are those, every piece meets the next, and R2 and R3 are still Gap apart.</summary>
    [Theory]
    [InlineData(PlanCorner.Miter, 1.0, 1)]
    [InlineData(PlanCorner.Square, 1.0, 1)]
    [InlineData(PlanCorner.Miter, 1.5, 1)]
    [InlineData(PlanCorner.Square, 0.6, 1)]
    [InlineData(PlanCorner.Miter, 1.0, 2)]
    [InlineData(PlanCorner.Square, 1.0, 3)]
    public void WithCorners_EachArmIsStillAQuarterWave(PlanCorner corner, double k, int n)
    {
        var d = WilkinsonPlan.Build(50.0, 2.4e9, 1.35e-3, Ro4350b, k, n, 2.0, corner);
        var s = d.Plan.Sections;
        var tees = s.OfType<PlanTee>().ToList();
        var (input, outTop, outBottom) = (tees[0], tees[1], tees[2]);
        var bends = s.OfType<PlanBend>().ToList();
        Assert.Equal(4, bends.Count);
        Assert.All(bends, b => Assert.Equal(corner == PlanCorner.Miter, b.M is not null));

        var arms = d.Arms.Count == 1 ? [d.Arms[0], d.Arms[0]] : d.Arms.ToList();
        var lines = arms.Select(a => d.Lines.Single(l => l.Name == a.Name)).ToList();
        double[] teeLine = [input.L1 - input.W3 / 2.0, input.L2 - input.W3 / 2.0];
        for (int i = 0; i < 2; i++)
        {
            var bend = bends.First(b => b.W == lines[i].WMeters);
            Assert.Equal(bend.LineLength(Ro4350b, 2.4e9, lines[i].Z, lines[i].QuarterWaveMeters), arms[i].CornerMeters, 15);
            double arm = teeLine[i] + 2.0 * arms[i].RiseMeters + arms[i].AcrossMeters + 2.0 * arms[i].CornerMeters;
            Assert.True(Math.Abs(arm - lines[i].QuarterWaveMeters) < 10e-9, $"arm {i}: {arm} vs {lines[i].QuarterWaveMeters}");
        }
        Assert.Equal(arms.SelectMany(a => a.RiseMeters > 0 ? new[] { a.AcrossMeters, a.RiseMeters, a.RiseMeters } : [a.AcrossMeters])
                         .Order(), s.OfType<PlanLine>().Select(l => l.L).Order());
        HybridPlanTests.AssertJoined(d.Plan);
        Assert.Equal(d.GapMeters, outTop.Y - (outBottom.Y + outBottom.L1 + outBottom.L2), 12);

        // The rectangle is as tall as the curved ring: the upper run across is at the half-turn's top (above it by half
        // the arms' difference when K ≠ 1), so the arms stay a ring's diameter apart.
        var curved = WilkinsonPlan.Build(50.0, 2.4e9, 1.35e-3, Ro4350b, k, n, 2.0);
        double crest = ((PlanTee)curved.Plan.Sections[0]).L1 + curved.RingRadiusMeters;
        double upper = s.OfType<PlanLine>().Where(l => l.RotationDeg == 0.0).Max(l => l.Y);
        Assert.True(k == 1.0 ? Math.Abs(upper - crest) < 1.5e-9 : upper > crest - 1.5e-9, $"run at {upper}, crest {crest}");
    }

    [Theory]
    [InlineData(PlanCorner.Miter)]
    [InlineData(PlanCorner.Square)]
    public void WithCorners_ARingTooSmallIsACodedRefusal(PlanCorner corner)
    {
        var ex = Assert.Throws<ParameterRefusalException>(() =>
            WilkinsonPlan.Build(50.0, 40e9, 1.35e-3, Ro4350b, corner: corner));
        Assert.Equal("device.wilkinson.ring-too-small", ex.Diagnostic?.Id);
    }

    [Theory]
    [InlineData(0.0, 2.4e9, 1.35e-3, "device.wilkinson.spec-not-positive")]
    [InlineData(50.0, -1.0, 1.35e-3, "device.wilkinson.spec-not-positive")]
    [InlineData(50.0, 40e9, 1.35e-3, "device.wilkinson.ring-too-small")]
    [InlineData(500.0, 2.4e9, 1.35e-3, "device.wilkinson.width-unreachable")]
    public void ASpecThatCannotBeBuilt_IsACodedRefusal(double z0, double f, double gap, string code)
    {
        var ex = Assert.Throws<ParameterRefusalException>(() => WilkinsonPlan.Build(z0, f, gap, Ro4350b));
        Assert.Equal(code, ex.Diagnostic?.Id);
    }

    [Theory]
    [InlineData(0.0, 1.0, 2.0, "device.wilkinson.spec-not-positive")]
    [InlineData(4.0, 1.0, 2.0, "device.wilkinson.split-unrealisable")]    // the arm to port 2 would be 412 Ω
    [InlineData(1.0, 8.0, 2.0, "device.wilkinson.sections-unsupported")]
    [InlineData(1.0, 1.5, 2.0, "device.wilkinson.sections-unsupported")]
    [InlineData(2.0, 2.0, 2.0, "device.wilkinson.unequal-multisection")]
    [InlineData(1.0, 2.0, 1.0, "device.wilkinson.band-not-above-one")]
    public void AVariantThatCannotBeBuilt_IsACodedRefusal(double k, double n, double band, string code)
    {
        var ex = Assert.Throws<ParameterRefusalException>(() => WilkinsonPlan.Build(50.0, 2.4e9, 1.35e-3, Ro4350b, k, n, band));
        Assert.Equal(code, ex.Diagnostic?.Id);
    }

    // ══ The unequal split (Parad and Moynihan, IEEE Trans. MTT, 1965) ═══════════════════════════════

    /// <summary>The arms, transformers and resistor are the closed form for P3/P2 = K², each width is MLIN's for its
    /// impedance, and both arms are λg/4 between the junctions' reference planes on the one curve radius.</summary>
    [Theory]
    [InlineData(1.5)]
    [InlineData(2.0)]
    [InlineData(0.6)]
    public void AnUnequalSplit_IsTheClosedForm(double k)
    {
        const double z0 = 50.0, f = 2.4e9;
        var d = WilkinsonPlan.Build(z0, f, 1.35e-3, Ro4350b, k);
        WilkinsonLine L(string name) => d.Lines.Single(l => l.Name == name);

        Assert.Equal(z0 * Math.Sqrt((1 + k * k) / (k * k * k)), L("arm to 3").Z, 12);
        Assert.Equal(k * k * L("arm to 3").Z, L("arm to 2").Z, 9);
        Assert.Equal(z0 * Math.Sqrt(k), L("transformer at 2").Z, 12);
        Assert.Equal(z0 / Math.Sqrt(k), L("transformer at 3").Z, 12);
        var r = Assert.Single(d.Resistors);
        Assert.Equal(("R2", "R3"), (r.PinA, r.PinB));
        Assert.Equal(z0 * (k + 1 / k), r.Ohms, 12);
        foreach (var line in d.Lines)
        {
            Assert.Equal(line.Z, Line(line.WMeters, f).Z0, 2);
            Assert.Equal(C0 / (4.0 * f * Math.Sqrt(line.Eeff)), line.QuarterWaveMeters, 12);
        }

        // Each arm, reference plane to reference plane: its input-tee line, then its curve.
        var s = d.Plan.Sections;
        var input = (PlanTee)s[0];
        var top = (PlanCurve)s[1];
        var bottom = (PlanCurve)s[2];
        Assert.Equal(top.Radius, bottom.Radius);
        double arm2 = input.L1 - input.W3 / 2.0 + MicrostripCurve.ElectricalLength(top.Radius, top.W, top.AngleDeg);
        double arm3 = input.L2 - input.W3 / 2.0 + MicrostripCurve.ElectricalLength(bottom.Radius, bottom.W, bottom.AngleDeg);
        Assert.True(Math.Abs(arm2 - L("arm to 2").QuarterWaveMeters) < 4e-9, $"arm to 2: {arm2}");
        Assert.True(Math.Abs(arm3 - L("arm to 3").QuarterWaveMeters) < 6e-9, $"arm to 3: {arm3}");

        // Each output tee's branch is its transformer, λg/4 past the through line, ending on pin 2 or 3.
        var out2 = (PlanTee)s[3];
        var out3 = (PlanTee)s[4];
        Assert.Equal(L("transformer at 2").WMeters, out2.W3);
        Assert.True(Math.Abs(out2.L3 - out2.W1 / 2.0 - L("transformer at 2").QuarterWaveMeters) < 2e-9);
        Assert.True(Math.Abs(out3.L3 - out3.W1 / 2.0 - L("transformer at 3").QuarterWaveMeters) < 2e-9);
        Assert.Equal([4, 8, 2], out2.Nodes);
        Assert.Equal([9, 5, 3], out3.Nodes);
        // R2 and R3 still face each other Gap apart, on the output tees' common line.
        Assert.Equal(out2.X, out3.X);
        Assert.Equal(1.35e-3, out2.Y - (out3.Y + out3.L1 + out3.L2), 12);
    }

    // ══ Multi-section (Cohn, IEEE Trans. MTT, 1968) ═════════════════════════════════════════════════

    /// <summary>
    /// Two sections over an octave, against closed forms derived independently of the solver: the two-section
    /// equal-ripple transformer from 2 to 1, z1 = √(A + √(A² + 2)) with A = 1/(2·tan²θ1), and the two resistors that
    /// match the odd mode at θ1, R2 = 2·z1·z2/√((z1 + z2)(z2 − z1·cot²θ1)) and R1 = 2·R2·(z1 + z2)/(R2·(z1 + z2) − 2·z2).
    /// </summary>
    [Theory]
    [InlineData(1.5)]
    [InlineData(2.0)]
    [InlineData(3.0)]
    public void TwoSections_AreTheClosedForm(double band)
    {
        var (z, r) = WilkinsonPlan.BroadbandSections(2, band);
        double theta1 = Math.Acos(Math.Cos(Math.PI / (1 + band)) / Math.Sqrt(2.0));
        double a = 1.0 / (2.0 * Math.Pow(Math.Tan(theta1), 2));
        double z1 = Math.Sqrt(a + Math.Sqrt(a * a + 2.0)), z2 = 2.0 / z1;
        double cot2 = Math.Pow(1.0 / Math.Tan(theta1), 2);
        double r2 = 2 * z1 * z2 / Math.Sqrt((z1 + z2) * (z2 - z1 * cot2));
        double r1 = 2 * r2 * (z1 + z2) / (r2 * (z1 + z2) - 2 * z2);

        Assert.Equal(z1, z[0], 12);
        Assert.Equal(z2, z[1], 12);
        Assert.Equal(r1, r[0], 9);
        Assert.Equal(r2, r[1], 9);
    }

    /// <summary>The octave's numbers, to four places: two sections 1.2196 and 1.6399 with 4.8428 and 1.9539; three
    /// 1.1130, √2 and 1.7970 with 10.4683, 3.9009 and 1.9410 — sections and resistors counted from the outputs.</summary>
    [Fact]
    public void AnOctave_GivesTheTabulatedNumbers()
    {
        var (z2, r2) = WilkinsonPlan.BroadbandSections(2, 2.0);
        Assert.Equal([1.2196, 1.6399], z2.Select(v => Math.Round(v, 4)));
        Assert.Equal([4.8428, 1.9539], r2.Select(v => Math.Round(v, 4)));
        var (z3, r3) = WilkinsonPlan.BroadbandSections(3, 2.0);
        Assert.Equal([1.1130, 1.4142, 1.7970], z3.Select(v => Math.Round(v, 4)));
        Assert.Equal([10.4683, 3.9009, 1.9410], r3.Select(v => Math.Round(v, 4)));
    }

    /// <summary>A multi-section divider's pins and pieces: a resistor pin pair per section, every pair Gap apart across
    /// the pair's centre line, and every section λg/4 between the reference planes of the junctions at its ends.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void AMultiSectionDivider_PlacesAResistorPairPerSection(int n)
    {
        const double f = 2.4e9, gap = 1.35e-3;
        var d = WilkinsonPlan.Build(50.0, f, gap, Ro4350b, 1.0, n, 2.0);
        var (zn, rn) = WilkinsonPlan.BroadbandSections(n, 2.0);

        Assert.Equal(WilkinsonPlan.PinsFor(n), d.Plan.Pins);
        Assert.Equal(3 + 2 * n, d.Plan.Pins.Count);
        Assert.Equal(n, WilkinsonPlan.SectionsForPinCount(d.Plan.Pins.Count));
        Assert.Equal(rn.Select(r => r * 50.0), d.Resistors.Select(r => r.Ohms));
        Assert.Equal(d.Plan.Pins.Skip(3), d.Resistors.SelectMany(r => new[] { r.PinA, r.PinB }));
        Assert.Equal(zn.Select(z => z * 50.0), d.Lines.Select(l => l.Z));
        Assert.Equal(zn[^1] * 50.0, d.ArmZ0, 12);

        // The resistor pins, where the plan's parts put them: each pair one above the other, Gap apart.
        var at = new Dictionary<int, (double X, double Y)>();
        foreach (var t in d.Plan.Sections.OfType<PlanTee>())
        {
            var (bx, by) = t.RotationDeg switch
            {
                0.0 => (t.X + t.L1, t.Y - t.L3),
                180.0 => (t.X - t.L1, t.Y + t.L3),
                _ => (double.NaN, double.NaN),
            };
            if (t.RotationDeg == 90.0)
            {
                at[t.Nodes[0]] = (t.X, t.Y);                    // through_in: R2 above the gap, the arrival below it
                at[t.Nodes[1]] = (t.X, t.Y + t.L1 + t.L2);
            }
            else at[t.Nodes[2]] = (bx, by);
        }
        for (int m = 1; m <= n; m++)
        {
            var top = at[4 + 2 * (m - 1)];
            var bottom = at[5 + 2 * (m - 1)];
            Assert.Equal(top.X, bottom.X, 12);
            Assert.Equal(gap, top.Y - bottom.Y, 12);
        }

        // Each further section: the junction it leaves (the ring's output tee's branch, or a straight tee's outgoing
        // arm) and the one it arrives at (that tee's incoming arm carries no line) — λg/4 between them.
        var ring = (PlanTee)d.Plan.Sections[3];
        Assert.True(Math.Abs(ring.L3 - ring.W1 / 2.0 - d.Lines[n - 2].QuarterWaveMeters) < 2e-9);
        var straights = d.Plan.Sections.OfType<PlanTee>().Where(t => t.RotationDeg == 0.0).ToList();
        Assert.Equal(n - 1, straights.Count);
        for (int i = 0; i < straights.Count; i++)
        {
            var t = straights[i];
            Assert.True(t.L1 - t.W3 / 2.0 <= 1e-12);
            double outgoing = t.L2 - t.W3 / 2.0;
            double expected = i + 1 < straights.Count ? d.Lines[n - 3 - i].QuarterWaveMeters : d.FeedMeters;
            Assert.True(Math.Abs(outgoing - expected) < 2e-9, $"junction {i}: {outgoing} vs {expected}");
        }
    }

    // ══ MWPD up to seven sections (Cohn 1968) ═══════════════════════════════════════════════════

    /// <summary>The general solve gives the two- and three-section numbers the bisection gives, so the one rule is the
    /// same for every N.</summary>
    [Theory]
    [InlineData(2, 2.0)]
    [InlineData(2, 4.0)]
    [InlineData(3, 2.0)]
    [InlineData(3, 1.5)]
    public void TheGeneralSolve_IsTheBisectionsForTwoAndThree(int n, double band)
    {
        var (z, r) = WilkinsonPlan.BroadbandSections(n, band);
        var (zg, rg) = WilkinsonPlan.ChebyshevSections(n, band);
        for (int i = 0; i < n; i++)
        {
            Assert.Equal(z[i], zg[i], 11);
            Assert.Equal(r[i], rg[i], 9);
        }
    }

    /// <summary>
    /// N = 4 to 7: the even mode's transformer from 2 to 1 is symmetric (z_k·z_(N+1−k) = 2) and has zero reflection at
    /// every zero of the N-th Chebyshev polynomial mapped into the band, so it is the equal-ripple one; every resistor is
    /// positive. (That the odd mode is matched there too is checked on ideal lines in the engine's tests.)
    /// </summary>
    [Theory]
    [InlineData(4, 2.0)]
    [InlineData(5, 1.5)]
    [InlineData(6, 3.0)]
    [InlineData(7, 2.0)]
    [InlineData(7, 1.2)]
    [InlineData(7, 10.0)]
    public void MoreSections_AreTheEqualRippleTransformer(int n, double band)
    {
        var (z, r) = WilkinsonPlan.BroadbandSections(n, band);
        Assert.Equal(n, z.Length);
        for (int k = 0; k < n; k++) Assert.Equal(2.0, z[k] * z[n - 1 - k], 12);
        Assert.All(r, x => Assert.True(x > 0.0));
        double thetaM = Math.PI / (1 + band);
        for (int j = 1; j <= n; j++)
        {
            double theta = Math.Acos(Math.Cos(thetaM) * Math.Cos((2 * j - 1) * Math.PI / (2.0 * n)));
            Complex zl = 1.0;
            foreach (double zk in z) zl = zk * (zl + Complex.ImaginaryOne * zk * Math.Tan(theta)) / (zk + Complex.ImaginaryOne * zl * Math.Tan(theta));
            Assert.True(((zl - 2.0) / (zl + 2.0)).Magnitude < 1e-12, $"zero {j}: {zl}");
        }
    }

    /// <summary>The octave's numbers for four and seven sections, to four places, as components.md tabulates them.</summary>
    [Fact]
    public void AnOctave_GivesTheTabulatedNumbers_UpToSeven()
    {
        var (z4, r4) = WilkinsonPlan.BroadbandSections(4, 2.0);
        Assert.Equal([1.0591, 1.2572, 1.5908, 1.8883], z4.Select(v => Math.Round(v, 4)));
        Assert.Equal([21.3804, 6.5989, 3.6670, 1.9667], r4.Select(v => Math.Round(v, 4)));
        var (z7, r7) = WilkinsonPlan.BroadbandSections(7, 2.0);
        Assert.Equal([1.0089, 1.0568, 1.1863, 1.4142, 1.6859, 1.8924, 1.9823], z7.Select(v => Math.Round(v, 4)));
        Assert.Equal([161.4511, 29.9643, 11.6766, 7.0555, 5.2838, 3.8258, 2.0029], r7.Select(v => Math.Round(v, 4)));
    }

    /// <summary>A seven-section divider plans: 17 pins, a resistor pair per section Gap apart, every piece joined.</summary>
    [Fact]
    public void SevenSections_Plan()
    {
        var d = WilkinsonPlan.Build(50.0, 2.4e9, 1.35e-3, Ro4350b, 1.0, 7, 3.0);
        Assert.Equal(17, d.Plan.Pins.Count);
        Assert.Equal(7, WilkinsonPlan.SectionsForPinCount(17));
        Assert.Equal(7, d.Resistors.Count);
        HybridPlanTests.AssertJoined(d.Plan);
    }

    /// <summary>Four or more sections over a band narrower than 1.1 is refused: the odd mode's equations there are
    /// nearly dependent, and seven sections lose the solution at 1.05.</summary>
    [Fact]
    public void ABandTooNarrowForTheSections_IsACodedRefusal()
    {
        var ex = Assert.Throws<ParameterRefusalException>(() => WilkinsonPlan.Build(50.0, 2.4e9, 1.35e-3, Ro4350b, 1.0, 7, 1.05));
        Assert.Equal("device.wilkinson.sections-unsolved", ex.Diagnostic?.Id);
        Assert.Equal(3, WilkinsonPlan.Build(50.0, 2.4e9, 1.35e-3, Ro4350b, 1.0, 3, 1.05).Sections);
    }
}
