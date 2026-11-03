using CircuitRF.Design.Layout.Pdn;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.SchematicSync;

/// <summary>
/// brief-sdl-3 §4: the hard cases, one test per claim the designs alone do not make — the coupled staircase lands at
/// the coupled lines' own offsets (H2), a long any-angle chain is exact and would not be if it were rounded per link
/// (H6), an array is not chained and is said once, parts joined only through a part with no layout are placed apart
/// and said so, and the report is grouped by kind with the ids a click selects. The designs H1-H7 themselves are gated
/// by <see cref="SdlDesignSetTests"/>.
/// </summary>
public sealed class HardCaseTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "crf-sdl3-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>Each coupled section starts at the previous one's pin 4: one length along, and that section's own
    /// W + S across (0.8 + 0.15 mm, then 0.8 + 0.4 mm), in the first section's frame.</summary>
    [Fact]
    public void H2_TheStaircaseComesOutAtEachSectionsOwnOffset()
    {
        var p = SdlChecker.Prepare("H2-coupled-bandpass", _scratch);
        var layout = p.Applied(p.Plan());
        LayoutInstance Cl(string id) => layout.Instances.Single(i => i.SchematicId == id);
        var (c1, c2, c3) = (Cl("CL1"), Cl("CL2"), Cl("CL3"));
        Assert.Equal(c1.RotationDegrees, c2.RotationDegrees, 9);
        Assert.Equal(c1.RotationDegrees, c3.RotationDegrees, 9);

        (double Along, double Across) InFrame(LayoutInstance a, LayoutInstance b)
        {
            var (c, s) = LayoutAngle.CosSin(-c1.RotationDegrees);
            double dx = b.X - a.X, dy = b.Y - a.Y;
            return (dx * c - dy * s, (dx * s + dy * c) * (c1.MirrorX ? -1 : 1));
        }
        var (along12, across12) = InFrame(c1, c2);
        var (along23, across23) = InFrame(c2, c3);
        Assert.Equal(10_000_000, along12, 0.5);
        Assert.Equal(-950_000, across12, 0.5);
        Assert.Equal(10_000_000, along23, 0.5);
        Assert.Equal(-1_200_000, across23, 0.5);
    }

    /// <summary>
    /// H6: twenty 17° curves. The last free pin, read through the WRITTEN instance, is within a DBU of the chain
    /// carried in doubles from the first curve's written pose. Rounding each pose to the DBU before placing the next
    /// part — what SDL-1 D3 rules out — misses by more, so dropping that rule would fail this design. (The joint
    /// arithmetic itself is held against an independent computation by
    /// <c>ChainPlacementTests.D3_ATwelvePartThirtyDegreeChainEndsWithinOneDbuOfTheExactResult</c>; this is about the
    /// rounding.)
    /// </summary>
    [Fact]
    public void H6_TheLastPinIsExact_AndRoundingAtEveryLinkWouldNotBe()
    {
        var p = SdlChecker.Prepare("H6-long-curve-chain", _scratch);
        var layout = p.Applied(p.Plan());
        var curves = Enumerable.Range(1, 20).Select(k => layout.Instances.Single(i => i.SchematicId == $"CV{k}")).ToList();
        var cell = CellLayoutResolver.Resolve(curves[0], p.LayoutDir, p.Tech).View!;
        var pins = CellPins.Resolve(cell, p.Tech);
        var (pin1, pin2) = (SchematicLayoutOrientation.CellPinFor(1, pins)!, SchematicLayoutOrientation.CellPinFor(2, pins)!);

        var exact = ChainPose.Of(curves[0]);
        var perLink = exact;
        foreach (var next in curves.Skip(1))
        {
            exact = ChainPlacer.Child(exact, pin2, pin1, next.MirrorX);
            perLink = ChainPlacer.Child(perLink, pin2, pin1, next.MirrorX);
            perLink = perLink with { X = Math.Round(perLink.X), Y = Math.Round(perLink.Y) };
        }
        var (ex, ey, _) = exact.Apply(pin2);
        var (wx, wy, _) = ChainPose.Of(curves[^1]).Apply(pin2);
        var (rx, ry, _) = perLink.Apply(pin2);
        output.WriteLine($"exact ({ex:F3}, {ey:F3}); written ({wx:F3}, {wy:F3}); rounded per link ({rx:F3}, {ry:F3})");

        Assert.True(Math.Abs(wx - ex) <= 1 && Math.Abs(wy - ey) <= 1, $"written ({wx}, {wy}), exact ({ex:F3}, {ey:F3})");
        Assert.True(Math.Abs(rx - ex) > 1 || Math.Abs(ry - ey) > 1,
                    $"rounding per link ended at ({rx:F3}, {ry:F3}), within a DBU of the exact ({ex:F3}, {ey:F3})");
    }

    /// <summary>A part placed as an array is not one part with one set of pins: it is not joined to its neighbours,
    /// stays where it is, and the run says so exactly once.</summary>
    [Fact]
    public void AnArrayIsNotChained_AndIsReportedOnce()
    {
        var p = SdlChecker.Prepare("C1-through-line", _scratch);
        p.Plan().ApplyTo(p.Target);
        var tl2 = p.Target.Instances.Single(i => i.SchematicId == "TL2");
        tl2.Rows = 2;
        var (x, y) = (tl2.X, tl2.Y);

        var plan = p.Plan();
        var lines = plan.Lines.Where(l => l.Text.Contains("array", StringComparison.Ordinal)).ToList();
        var line = Assert.Single(lines);
        Assert.Equal("TL2", line.InstanceName);
        Assert.Equal(SchematicLayoutPlanner.ReportKind.Unjoined, line.Kind);
        Assert.Contains("not joined to its neighbours", line.Text);
        var after = p.Applied(plan).Instances.Single(i => i.SchematicId == "TL2");
        Assert.Equal((x, y), (after.X, after.Y));
    }

    /// <summary>ML3 and ML4 are joined only through an ideal resistor: two chains, placed apart, and one line naming
    /// the three parts — which is what a click selects.</summary>
    [Fact]
    public void PartsJoinedOnlyThroughAPartWithNoLayoutArePlacedApart_AndSaidSo()
    {
        var p = SdlChecker.Prepare("H1b-through-ideal-r", _scratch);
        var plan = p.Plan();
        var line = Assert.Single(plan.Lines, l => l.Text.Contains("joined through", StringComparison.Ordinal));
        Assert.Equal("ML3 and ML4 are joined through R1, which has no layout; they are placed apart", line.Text);
        Assert.Equal(["ML3", "ML4", "R1"], line.Ids);
        Assert.Equal(SchematicLayoutPlanner.ReportKind.Unjoined, line.Kind);
    }

    /// <summary>An SnP's reference terminal is its last pin: the schematic's nets for the part carry it, so a three-pad
    /// footprint under a two-port SnP with a reference has a net for every pad, and the ground via goes on the third (the package calls it E).</summary>
    [Fact]
    public void AnSnpsReferenceTerminalIsItsLastNet()
    {
        var p = SdlChecker.Prepare("F7b-snp-reference", _scratch);
        Assert.Equal(["n2", "n3", "0"], PdnSchematicNets.Of(p.Model, p.SchematicPath).For("X1")!.Nets);
        Assert.Contains(p.Plan().Ground.KeysAdded, k => k == GroundArtwork.KeyOf("X1", "E"));
    }

    /// <summary>A layer row that names no conductor is said on the part's own line, with the names it could take — a
    /// line meant for the bottom copper was otherwise drawn on the top with nothing said.</summary>
    [Fact]
    public void ALayerRowNamingNoConductorIsReported()
    {
        var p = SdlChecker.Prepare("H3-layer-change", _scratch);
        p.Model.Components.Single(c => c.InstanceName == "TL2").Parameters.Single(q => q.Name == "SignalLayer").Expression = "Bottom Copper";
        var line = Assert.Single(p.Plan().Lines, l => l.Text.Contains("no conductor is named", StringComparison.Ordinal));
        Assert.Equal("TL2", line.InstanceName);
        Assert.Equal(SchematicLayoutPlanner.ReportSeverity.Warning, line.Severity);
        Assert.Contains("'Bottom Copper (1 oz)'", line.Text);
    }

    /// <summary>R-sdl3-2: the connected-placement lines come out grouped by kind, each with the ids a click selects;
    /// every other line is left for the ordinary report.</summary>
    [Fact]
    public void TheReportIsGroupedByKind_EachLineNamingItsParts()
    {
        var p = SdlChecker.Prepare("H7-self-crossing", _scratch);
        var plan = p.Plan();
        var group = Assert.Single(SchematicLayoutPlanner.GroupConnectionReport(plan.Lines));
        Assert.Equal(SchematicLayoutPlanner.ReportKind.Overlap, group.Kind);
        Assert.Equal("1 overlap of unconnected copper", group.Heading);
        Assert.Equal(SchematicLayoutPlanner.ReportSeverity.Warning, group.Severity);
        Assert.Equal(["TL1", "TL4"], Assert.Single(group.Lines).Ids);
        Assert.Contains(plan.Lines, l => l.Kind == SchematicLayoutPlanner.ReportKind.General && l.Text.StartsWith("Joined"));
    }
}
