using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Drc;
using CircuitRF.Ui.Theming;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// DRC v2's measurement half (docs/design/layout-view.md §9A.5): separation, enclosure, overlap and
/// notch, plus rules whose operand is a DERIVED region rather than a bare layer.
///
/// <para><b>Every kind here inherits the width check's one-DBU backoff</b>, so its detection
/// threshold is "under the rule by MORE than one DBU" — one nanometre at the default resolution.
/// The one-DBU cases below are therefore PASS cases on purpose, not tolerances that drifted: a
/// check that fired on a rule drawn exactly at its limit is the one failure that would stop people
/// running it, and backing off by a DBU is what prevents that.</para>
///
/// <para>Every threshold case is tested from BOTH sides — a check that fires on a real violation
/// but also on legal geometry is worse than no check, and the at-limit case is the one real
/// artwork actually sits on.</para>
/// </summary>
public class DrcTwoRegionCheckTests
{
    private static readonly LayerKey M1 = new(1, 0);
    private static readonly LayerKey M2 = new(2, 0);
    private static readonly LayerKey M3 = new(3, 0);

    private static Technology Tech(params DrcRule[] rules)
    {
        var t = new Technology { Name = "Test" };
        foreach (var (key, name) in new[] { (M1, "M1"), (M2, "M2"), (M3, "M3") })
            t.Layers.Add(new LayerDef { Key = key, Name = name, Color = new Rgba(200, 200, 200, 255) });
        t.DrcRules.AddRange(rules);
        return t;
    }

    private static RectShape Rect(LayerKey layer, long x1, long y1, long x2, long y2) =>
        new() { Layer = layer, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };

    private static DrcRule Rule(
        DrcRuleKind kind, long value, LayerKey? marker = null,
        string? regionA = null, string? regionB = null) =>
        new()
        {
            Name = kind.ToString(),
            Kind = kind,
            Layer = marker ?? M1,
            ValueDbu = value,
            RegionA = regionA,
            RegionB = regionB,
        };

    // ── Separation: a gap between two different regions ─────────────────────────────────────────

    [Theory]
    [InlineData(100, true)]    // gap is 50, rule is 100 → too close
    [InlineData(51, true)]     // one DBU over the gap → still too close
    [InlineData(50, false)]    // exactly at the rule → legal, and this is the common case
    [InlineData(20, false)]
    public void MinSeparation_FiresExactlyBelowTheRule(long ruleDbu, bool expectViolation)
    {
        var shapes = new List<LayoutShape>
        {
            Rect(M1, 0, 0, 100, 1000),
            Rect(M2, 150, 0, 250, 1000),   // 50 DBU gap
        };

        var result = DrcEngine.Run(shapes, Tech(Rule(DrcRuleKind.MinSeparation, ruleDbu, regionB: "2/0")));

        Assert.Equal(expectViolation, result.Violations.Count > 0);
        if (expectViolation)
            Assert.Equal(DrcRuleKind.MinSeparation, result.Violations[0].Kind);
    }

    /// <summary>
    /// The marker is the GAP, centred between the two regions — the thing the user has to open, not
    /// a highlight parked on metal that is not itself wrong. Same reasoning as the spacing check's.
    /// </summary>
    [Fact]
    public void MinSeparation_MarkerSitsInTheGap()
    {
        var shapes = new List<LayoutShape>
        {
            Rect(M1, 0, 0, 100, 1000),
            Rect(M2, 150, 0, 250, 1000),
        };

        var v = Assert.Single(DrcEngine.Run(shapes, Tech(Rule(DrcRuleKind.MinSeparation, 100, regionB: "2/0"))).Violations);

        long centre = (v.Marker.MinX + v.Marker.MaxX) / 2;
        Assert.InRange(centre, 100, 150);
        Assert.True(v.Marker.MinX > 0 && v.Marker.MaxX < 250,
            $"marker must not span either whole region; got {v.Marker.MinX}..{v.Marker.MaxX}");
    }

    /// <summary>
    /// Where two regions genuinely OVERLAP there is no gap to measure. Reporting one would turn
    /// every deliberate contact-under-metal into a separation violation — which on a real process
    /// would be hundreds of false findings, since contacts are supposed to sit under metal.
    /// </summary>
    [Fact]
    public void MinSeparation_OverlappingRegions_ReportNothing()
    {
        var shapes = new List<LayoutShape>
        {
            Rect(M1, 0, 0, 1000, 1000),
            Rect(M2, 400, 400, 600, 600),   // entirely inside M1
        };

        Assert.Empty(DrcEngine.Run(shapes, Tech(Rule(DrcRuleKind.MinSeparation, 500, regionB: "2/0"))).Violations);
    }

    // ── Enclosure: A must surround B by at least the rule ───────────────────────────────────────

    [Theory]
    [InlineData(500, true)]    // margin is 400 → short
    [InlineData(402, true)]    // two DBU under → caught
    [InlineData(401, false)]   // ONE DBU under → below the check's own resolution, by design
    [InlineData(400, false)]   // exactly at the rule → legal
    [InlineData(100, false)]
    public void MinEnclosure_FiresExactlyBelowTheRule(long ruleDbu, bool expectViolation)
    {
        var shapes = new List<LayoutShape>
        {
            Rect(M1, 0, 0, 1000, 1000),      // enclosing metal
            Rect(M2, 400, 400, 600, 600),    // enclosed contact, 400 margin all round
        };

        var result = DrcEngine.Run(shapes, Tech(Rule(DrcRuleKind.MinEnclosure, ruleDbu, regionB: "2/0")));
        Assert.Equal(expectViolation, result.Violations.Count > 0);
    }

    [Fact]
    public void MinEnclosure_ContactRunningOffTheEdge_IsReported()
    {
        var shapes = new List<LayoutShape>
        {
            Rect(M1, 0, 0, 1000, 1000),
            Rect(M2, 950, 400, 1050, 600),   // hangs over the right edge
        };

        Assert.NotEmpty(DrcEngine.Run(shapes, Tech(Rule(DrcRuleKind.MinEnclosure, 100, regionB: "2/0"))).Violations);
    }

    /// <summary>
    /// Enclosure is only a question where the two regions meet. A contact nowhere near the metal is
    /// not "enclosed by zero" — it is a different rule's problem, and reporting it here would bury
    /// the real findings under one violation per unrelated shape on the layer.
    /// </summary>
    [Fact]
    public void MinEnclosure_ARegionNowhereNearTheEnclosingLayer_IsNotReported()
    {
        var shapes = new List<LayoutShape>
        {
            Rect(M1, 0, 0, 1000, 1000),
            Rect(M2, 5000, 5000, 5200, 5200),   // far away
        };

        Assert.Empty(DrcEngine.Run(shapes, Tech(Rule(DrcRuleKind.MinEnclosure, 100, regionB: "2/0"))).Violations);
    }

    // ── Overlap: where two regions meet, the overlap must be wide enough ────────────────────────

    [Theory]
    [InlineData(200, true)]    // overlap is 100 wide → too narrow
    [InlineData(102, true)]    // two DBU under → caught
    [InlineData(101, false)]   // ONE DBU under → below the check's own resolution, by design
    [InlineData(100, false)]   // exactly at the rule → legal
    [InlineData(50, false)]
    public void MinOverlap_FiresExactlyBelowTheRule(long ruleDbu, bool expectViolation)
    {
        var shapes = new List<LayoutShape>
        {
            Rect(M1, 0, 0, 1000, 100),      // horizontal bar
            Rect(M2, 500, 0, 600, 1000),    // vertical bar → 100 x 100 overlap
        };

        var result = DrcEngine.Run(shapes, Tech(Rule(DrcRuleKind.MinOverlap, ruleDbu, regionB: "2/0")));
        Assert.Equal(expectViolation, result.Violations.Count > 0);
    }

    /// <summary>
    /// No overlap at all is a presence question ("this must be covered"), not a width one. Answering
    /// it here would report every unrelated shape pair on two layers.
    /// </summary>
    [Fact]
    public void MinOverlap_RegionsThatDoNotMeet_ReportNothing()
    {
        var shapes = new List<LayoutShape>
        {
            Rect(M1, 0, 0, 100, 100),
            Rect(M2, 5000, 5000, 5100, 5100),
        };

        Assert.Empty(DrcEngine.Run(shapes, Tech(Rule(DrcRuleKind.MinOverlap, 200, regionB: "2/0"))).Violations);
    }

    // ── Notch: a gap WITHIN one conductor ───────────────────────────────────────────────────────

    /// <summary>A U-shape: two arms joined by a base, with a 400-DBU slot between them.</summary>
    private static List<LayoutShape> UShape() =>
    [
        Rect(M1, 0, 0, 200, 1000),      // left arm
        Rect(M1, 600, 0, 800, 1000),    // right arm
        Rect(M1, 0, 0, 800, 200),       // base joins them into ONE conductor
    ];

    [Theory]
    [InlineData(500, true)]    // slot is 400 → too narrow
    [InlineData(402, true)]    // two DBU under → caught
    [InlineData(401, false)]   // ONE DBU under → below the check's own resolution, by design
    [InlineData(400, false)]   // exactly at the rule → legal
    [InlineData(200, false)]
    public void MinNotch_FiresExactlyBelowTheRule(long ruleDbu, bool expectViolation)
    {
        var result = DrcEngine.Run(UShape(), Tech(Rule(DrcRuleKind.MinNotch, ruleDbu)));
        Assert.Equal(expectViolation, result.Violations.Count > 0);
    }

    /// <summary>
    /// The whole reason notch is its own kind. Two SEPARATE conductors the same distance apart are a
    /// spacing question, answered by the spacing check with net attribution this one does not have.
    /// A notch check that also reported those would duplicate every spacing finding.
    /// </summary>
    [Fact]
    public void MinNotch_TwoSeparateConductors_AreNotANotch()
    {
        var shapes = new List<LayoutShape>
        {
            Rect(M1, 0, 0, 200, 1000),
            Rect(M1, 600, 0, 800, 1000),   // same 400 gap, but NOT joined
        };

        Assert.Empty(DrcEngine.Run(shapes, Tech(Rule(DrcRuleKind.MinNotch, 500))).Violations);
    }

    // ── Derived regions as operands ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The headline of v2: a rule measures an expression, not a layer. Here the raw M1 shape is
    /// comfortably wide, and only the DERIVED region — M1 with the M2 keep-out removed — is narrow.
    /// A v1 rule could not express this at all.
    /// </summary>
    [Fact]
    public void AWidthRule_OnADerivedRegion_MeasuresTheDerivedRegion_NotTheRawLayer()
    {
        var shapes = new List<LayoutShape>
        {
            Rect(M1, 0, 0, 1000, 1000),      // a wide pour
            Rect(M2, 0, 0, 1000, 950),       // removing this leaves a 50-DBU strip
        };

        // The raw layer passes.
        Assert.Empty(DrcEngine.Run(shapes, Tech(Rule(DrcRuleKind.MinWidth, 200))).Violations);

        // The derived region does not.
        var derived = DrcEngine.Run(shapes, Tech(Rule(DrcRuleKind.MinWidth, 200, regionA: "not(1/0, 2/0)")));
        Assert.NotEmpty(derived.Violations);
    }

    [Fact]
    public void ASeparationRule_BetweenTwoDerivedRegions_Resolves()
    {
        var shapes = new List<LayoutShape>
        {
            Rect(M1, 0, 0, 100, 1000),
            Rect(M2, 0, 0, 100, 1000),      // and(1/0, 2/0) is the left bar
            Rect(M3, 150, 0, 250, 1000),    // 50 DBU away
        };

        var result = DrcEngine.Run(shapes, Tech(
            Rule(DrcRuleKind.MinSeparation, 100, regionA: "and(1/0, 2/0)", regionB: "3/0")));

        Assert.NotEmpty(result.Violations);
    }

    // ── A rule that cannot be checked says so ───────────────────────────────────────────────────

    /// <summary>
    /// R16b's "never blocks editing" cuts both ways: a rule circuitRF cannot evaluate must not stop
    /// the run, and must not pass silently either. Silently passing is the failure this whole
    /// feature exists to avoid.
    /// </summary>
    [Fact]
    public void ATwoRegionRuleWithNoSecondRegion_IsReported_AndTheOtherRulesStillRun()
    {
        var shapes = new List<LayoutShape> { Rect(M1, 0, 0, 1000, 60) };   // narrow: trips the width rule

        var result = DrcEngine.Run(shapes, Tech(
            Rule(DrcRuleKind.MinSeparation, 100),                 // no RegionB — unusable
            Rule(DrcRuleKind.MinWidth, 100)));

        Assert.Single(result.Violations);                                   // the width rule still ran
        Assert.Contains(result.Diagnostics, d => d.Contains("MinSeparation") && d.Contains("second region"));
    }

    [Fact]
    public void AnUnreadableExpression_IsReportedByName_AndTheOtherRulesStillRun()
    {
        var shapes = new List<LayoutShape> { Rect(M1, 0, 0, 1000, 60) };

        var result = DrcEngine.Run(shapes, Tech(
            Rule(DrcRuleKind.MinWidth, 100, regionA: "and(1/0"),   // unterminated
            Rule(DrcRuleKind.MinWidth, 100)));

        Assert.Single(result.Violations);
        Assert.Contains(result.Diagnostics, d => d.Contains("not a valid layer expression"));
    }

    /// <summary>
    /// A deck written for a full process names layers a simpler technology omits. Those rules
    /// contribute nothing rather than failing, and the run says which layers went unread — the
    /// difference between "your design is clean" and "your design is clean against the rules I
    /// could actually evaluate".
    /// </summary>
    [Fact]
    public void RulesNamingUndefinedLayers_AreReportedOnce_NotOncePerRule()
    {
        var shapes = new List<LayoutShape> { Rect(M1, 0, 0, 1000, 1000) };

        var result = DrcEngine.Run(shapes, Tech(
            Rule(DrcRuleKind.MinWidth, 100, regionA: "and(1/0, 88/0)"),
            Rule(DrcRuleKind.MinWidth, 100, regionA: "and(1/0, 88/0)")));

        Assert.Single(result.Diagnostics, d => d.Contains("88/0"));
    }

    // ── Validation surfaces the same problems before a run ──────────────────────────────────────

    [Fact]
    public void TechValidation_FlagsAnUnreadableExpression_AndAMissingSecondRegion()
    {
        var problems = TechValidation.Validate(Tech(
            Rule(DrcRuleKind.MinWidth, 100, regionA: "frobnicate(1/0, 2/0)"),
            Rule(DrcRuleKind.MinSeparation, 100)));

        Assert.Contains(problems, p => p.Contains("unreadable first region"));
        Assert.Contains(problems, p => p.Contains("states no second region"));
    }

    [Fact]
    public void TechValidation_FlagsALayerNamedOnlyInsideAnExpression()
    {
        var problems = TechValidation.Validate(Tech(
            Rule(DrcRuleKind.MinWidth, 100, regionA: "and(1/0, 77/2)")));

        Assert.Contains(problems, p => p.Contains("(77,2)"));
    }

    /// <summary>
    /// The whole v2 model change is additive: a rule that states no expression behaves exactly as it
    /// did, which is what keeps every pre-v2 `.ctech` and every hand-authored rule working.
    /// </summary>
    [Fact]
    public void ARuleWithNoExpression_MeasuresItsOwnLayer_ExactlyAsBefore()
    {
        var shapes = new List<LayoutShape> { Rect(M1, 0, 0, 1000, 60) };

        var withoutExpr = DrcEngine.Run(shapes, Tech(Rule(DrcRuleKind.MinWidth, 100)));
        var withExpr = DrcEngine.Run(shapes, Tech(Rule(DrcRuleKind.MinWidth, 100, regionA: "1/0")));

        Assert.Equal(withoutExpr.Violations.Count, withExpr.Violations.Count);
        Assert.Equal(withoutExpr.Violations[0].Marker, withExpr.Violations[0].Marker);
    }
}
