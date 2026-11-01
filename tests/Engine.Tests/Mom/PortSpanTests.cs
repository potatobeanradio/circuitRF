using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using Xunit;

namespace CircuitRF.Engine.Tests.Mom;

// brief-port-width-planar-and-c3d R-pw1-1 / R-pw1-2 — a planar edge port that drives only part of the edge it stands on.
// An 8 × 2.9 mm FR-4 pour (H = 1.6 mm): port 1 on its left end, the whole 2.9 mm; port 2 on its bottom side at
// x = 5 mm, a side 8 mm long, either the whole side or a 1 mm span centred on the port.
public sealed class PortSpanTests
{
    private const double SpanLo = 4.5e-3, SpanHi = 5.5e-3;

    private static PlanarProblem Pour(params PlanarPolygon[] more) =>
        PlanarLineFixtures.Problem(GroundedSlab.Fr4Starter, 2e9,
            [PlanarLineFixtures.Rect(0, 0, 8e-3, 2.9e-3), .. more]);

    private static PlanarPort[] Ports(bool span) =>
    [
        new PlanarPort(1, new EmPoint(0, 1.45e-3), PlanarPortSide.MinX, 50),
        new PlanarPort(2, new EmPoint(5e-3, 0), PlanarPortSide.MinY, 50,
                       Span: span ? new PlanarPortSpan(SpanLo, SpanHi) : null),
    ];

    private static PlanarPortResolution Resolve(PlanarProblem problem, PlanarPort[] ports, int number)
    {
        var mesh = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Coarse, ports: ports).Mesh;
        return PlanarPorts.ResolveAll(mesh, ports).Single(r => r.Number == number);
    }

    /// <summary>A stated span narrows the port's rooftop row to the span: fewer rooftops, cut on gridlines at its two
    /// ends, so the port is exactly the span wide.</summary>
    [Fact]
    public void ASpan_NarrowsTheRooftopRow_ToTheSpan()
    {
        var whole = Resolve(Pour(), Ports(span: false), 2);
        var part  = Resolve(Pour(), Ports(span: true), 2);
        Assert.Equal(8e-3, whole.WidthM, 12);
        Assert.Equal(SpanHi - SpanLo, part.WidthM, 12);
        Assert.Equal(SpanLo, part.TransverseLines[0], 12);
        Assert.Equal(SpanHi, part.TransverseLines[^1], 12);
        Assert.True(part.BasisCount < whole.BasisCount, $"{part.BasisCount} rooftops against {whole.BasisCount}");
    }

    /// <summary>A span that is the whole edge is today's port bit for bit: the same mesh, rooftops and cross-section.</summary>
    [Fact]
    public void ASpanOverTheWholeEdge_IsTodaysPortBitForBit()
    {
        var problem = PlanarLineFixtures.Fr4Line(20e-3, 5e9);
        var plain = PlanarLineFixtures.EndPorts(problem);
        var (_, y0, _, y1) = problem.Bounds();
        var spanned = plain.Select(p => p with { Span = new PlanarPortSpan(y0, y1) }).ToArray();

        var m0 = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Shipping, ports: plain).Mesh;
        var m1 = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Shipping, ports: spanned).Mesh;
        Assert.Equal(m0.GridX, m1.GridX);
        Assert.Equal(m0.GridY, m1.GridY);
        var r0 = PlanarPorts.ResolveAll(m0, plain);
        var r1 = PlanarPorts.ResolveAll(m1, spanned);
        for (int i = 0; i < r0.Count; i++)
        {
            Assert.Equal(r0[i].BasisIndices, r1[i].BasisIndices);
            Assert.Equal(r0[i].WidthM, r1[i].WidthM);
            Assert.Equal(r0[i].TransverseLines, r1[i].TransverseLines);
        }
    }

    /// <summary>R-pw1-2 — the calibration feed of a port narrower than its edge is grown at the span's width, centred on
    /// the span: a tab on the side, not the whole side pushed out.</summary>
    [Fact]
    public void ANarrowPortsFeed_IsGrownAtTheSpansWidth()
    {
        var r = PlanarFeedExtension.ExtendDetailed(Pour(), Ports(span: true));
        var lead = Assert.Single(r.Leads);
        Assert.Equal(2, lead.PortNumber);
        Assert.Empty(r.NotDeembedded);
        var grown = r.Problem.Layers[0].Polygons[0];
        double below = -0.5 * lead.LengthM;
        Assert.True(grown.Contains(5e-3, below));
        Assert.True(grown.Contains(SpanLo + 1e-6, below) && grown.Contains(SpanHi - 1e-6, below));
        Assert.False(grown.Contains(SpanLo - 1e-6, below) || grown.Contains(SpanHi + 1e-6, below));
    }

    /// <summary>Where the feed cannot be grown (metal behind the port, across part of its span), the port is not de-embedded and the run says so
    /// by number — never de-embedded at the edge's width.</summary>
    [Fact]
    public void ANarrowPortWhoseFeedCannotGrow_IsNotDeembedded_AndSaysSo()
    {
        // Beside the port's own line (x = 5 mm), so the port's end face is still the pour's side, but inside its span.
        var r = PlanarFeedExtension.ExtendDetailed(Pour(PlanarLineFixtures.Rect(5.1e-3, -3e-3, 6e-3, -1e-3)), Ports(span: true));
        Assert.DoesNotContain(r.Leads, l => l.PortNumber == 2);
        Assert.Contains(2, r.NotDeembedded);
        Assert.Contains(r.Notes, n => n.StartsWith("Port 2 is narrower than the edge it stands on", StringComparison.Ordinal) &&
                                      n.Contains("NOT de-embedded", StringComparison.Ordinal));
    }

    /// <summary>A short planar run: the 1 mm port and the whole-side port give different answers at port 2.</summary>
    [Fact]
    public void APlanarRun_WithASpan_DiffersFromTheWholeEdge()
    {
        var st = new PlanarSolveSettings(Deembed: false);
        Complex S22(bool span) => new PlanarKernel().Solve(Pour(), PlanarLineFixtures.Coarse, Ports(span), [2e9], st)
                                                    .Solve.Points[0].S[1, 1];
        Complex whole = S22(false), part = S22(true);
        Assert.True((whole - part).Magnitude > 1e-2, $"S22 {whole} whole edge against {part} spanned");
    }
}
