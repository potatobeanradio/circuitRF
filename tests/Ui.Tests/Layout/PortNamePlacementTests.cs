using CircuitRF.Design.Layout;
using CircuitRF.Engine.Mom;
using CircuitRF.Render;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout;

/// <summary>
/// brief-dfr32-2 R-dfr32-2-4 / R-dfr32-2-5 — a port's name is drawn at a fixed screen size beside its mark, and two
/// names never overlap: the later one moves outward along its port's direction, the same way every time.
/// Stroke-font labels throughout, whose geometry needs no font file.
/// </summary>
public sealed class PortNamePlacementTests
{
    private const int Dbu = 1000;
    private static readonly LayerKey Top = new(1, 0);

    private static long Um(double v) => (long)Math.Round(v * Dbu);

    /// <summary>Lines 80 µm wide ending at x = 0, <paramref name="pitchUm"/> apart, a port on each end, each label
    /// stored 220 µm tall at the port's own point.</summary>
    private static List<LayoutShape> Cluster(int count, double pitchUm, string prefix = "")
    {
        var shapes = new List<LayoutShape>();
        for (int i = 0; i < count; i++)
        {
            double y = i * pitchUm;
            shapes.Add(new RectShape { Layer = Top, X1 = Um(-1500), Y1 = Um(y - 40), X2 = 0, Y2 = Um(y + 40) });
            shapes.Add(new LabelShape
            {
                Layer = Top, PortLayer = Top, X = 0, Y = Um(y), Text = $"{prefix}{i + 1}", Height = Um(220), IsPort = true,
                PortKind = PlanarPortKind.Edge, PortDirection = LayoutRotation.R180,
            });
        }
        return shapes;
    }

    private static List<LayoutRenderer.PortNamePlacement> Place(List<LayoutShape> shapes, double devicePxPerDbu) =>
        LayoutRenderer.PlacePortNames(
            shapes.Select((s, i) => (i, s)).Where(p => p.s is LabelShape { IsPort: true }).Select(p => (p.i, (LabelShape)p.s)),
            LayoutPortDirection.LookupFor(shapes), devicePxPerDbu, null, Dbu);

    /// <summary>The same port at two zooms four times apart: its name is the same height on screen.</summary>
    [Fact]
    public void APortName_IsTheSameSizeOnScreen_AtTwoZooms()
    {
        var shapes = Cluster(1, 0);
        double near = 0.01, far = 0.0025;   // device pixels per DBU: 10 and 2.5 px per µm
        double hNear = Place(shapes, near).Single().Box is var a ? (a.MaxY - a.MinY) * near : 0;
        double hFar = Place(shapes, far).Single().Box is var b ? (b.MaxY - b.MinY) * far : 0;

        Assert.InRange(hNear, 10, 20);
        Assert.Equal(hNear, hFar, 0.75);
    }

    /// <summary>Twelve ports 160 µm apart at a zoom where every name is several times the pitch (0.05 px per µm): no two
    /// names' boxes meet, the names that moved far have a leader, and a second placement is identical.</summary>
    [Fact]
    public void OverlappingNames_AreSeparated_TheSameWayEveryTime()
    {
        var shapes = Cluster(12, 160);
        const double zoom = 5e-5;
        var first = Place(shapes, zoom);

        for (int i = 0; i < first.Count; i++)
            for (int j = i + 1; j < first.Count; j++)
                Assert.False(first[i].Box.Intersects(first[j].Box), $"names {i + 1} and {j + 1} overlap");
        Assert.Contains(first, p => p.Leader is not null);

        var second = Place(shapes, zoom);
        Assert.Equal(first.Select(p => (p.Index, p.Box, p.Drawn.X, p.Drawn.Y, p.Leader)),
                     second.Select(p => (p.Index, p.Box, p.Drawn.X, p.Drawn.Y, p.Leader)));
    }

    /// <summary>brief-dfr33-1 R-dfr33-1-2 — nine ports 160 µm apart, named "P1".."P9". Zoomed out, a name that cannot
    /// clear the others within three of its heights falls back to its number, then to nothing, and every port is
    /// either placed or reported as left out. Zoomed in far enough that each fits beside its port, all nine come back
    /// in full.</summary>
    [Fact]
    public void ACrowdedName_FallsBackToItsNumberThenToNothing_AndComesBackWhenZoomedIn()
    {
        var shapes = Cluster(9, 160, prefix: "P");

        var omitted = new List<int>();
        var far = LayoutRenderer.PlacePortNames(
            shapes.Select((s, i) => (i, s)).Where(p => p.s is LabelShape { IsPort: true }).Select(p => (p.i, (LabelShape)p.s)),
            LayoutPortDirection.LookupFor(shapes), 5e-5, null, Dbu, omitted);
        Assert.Contains(far, p => p.Shortened && p.Drawn.Text == p.Drawn.Text.TrimStart('P'));
        Assert.NotEmpty(omitted);
        Assert.Equal(9, far.Count + omitted.Count);
        Assert.Contains(far, p => !p.Shortened);

        omitted.Clear();
        var near = LayoutRenderer.PlacePortNames(
            shapes.Select((s, i) => (i, s)).Where(p => p.s is LabelShape { IsPort: true }).Select(p => (p.i, (LabelShape)p.s)),
            LayoutPortDirection.LookupFor(shapes), 0.01, null, Dbu, omitted);
        Assert.Empty(omitted);
        Assert.Equal(9, near.Count(p => !p.Shortened));
    }
}
