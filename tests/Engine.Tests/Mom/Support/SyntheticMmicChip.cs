// PMS-1 R-pms1-1 — a synthetic MMIC chip: the ingredients of a medium GaAs layout, generated from a fixed seed.
//
// What it stands for: a chip-scale layout whose many small features put lines across the whole shared tensor grid —
// Manhattan traces on two levels, Metal1–Metal2 posts on small pads, octagonal backside vias, one large via-joined
// region where Metal2 overlaps Metal1, and three edge-fed ports. Preset S is the one brief-pms-0 measured
// (941,867 unknowns on a 795 × 790 grid at 10 GHz); L is denser (1,687,424 on 1,103 × 1,097).
//
// The geometry is held in micrometres as plain rings, so the same chip is emitted both as an engine-level
// PlanarProblem (here) and as a layout on the shipped MMIC GaAs technology (Ui.Tests links this file and adds the
// layout emitter beside it).

using CircuitRF.Engine.Mom;

namespace CircuitRF.Engine.Tests.Mom.Support;

/// <summary>The knobs of a synthetic chip. Every count is per chip unless named per level.</summary>
public sealed record SyntheticMmicChipSpec(
    double SizeUm           = 1400,
    int    TracesPerLevel   = 20,
    int    SmallVias        = 80,
    int    BacksideVias     = 20,
    double RegionUm         = 625,
    bool   OctagonalBackside = true,
    int    Seed             = 7)
{
    /// <summary>brief-pms-0's fixture S.</summary>
    public static SyntheticMmicChipSpec S => new();

    /// <summary>brief-pms-0's fixture L: denser posts and traces.</summary>
    public static SyntheticMmicChipSpec L => new(TracesPerLevel: 30, SmallVias: 150);
}

/// <summary>A generated chip: rings in µm, by role, and the three ports' drawn locations.</summary>
public sealed class SyntheticMmicChip
{
    public const double SigmaGold = 4.1e7;

    public SyntheticMmicChipSpec Spec { get; }
    public List<List<(double X, double Y)>> Metal1 { get; } = [];
    public List<List<(double X, double Y)>> Metal2 { get; } = [];
    /// <summary>Metal1–Metal2 posts, the large region included (last).</summary>
    public List<List<(double X, double Y)>> Posts { get; } = [];
    public List<List<(double X, double Y)>> Backside { get; } = [];
    /// <summary>Port number, location on its feed's drawn end (µm), and the side it is fed from.</summary>
    public List<(int Number, double X, double Y, PlanarPortSide Side)> PortSites { get; } = [];

    private SyntheticMmicChip(SyntheticMmicChipSpec spec) => Spec = spec;

    public static SyntheticMmicChip Build(SyntheticMmicChipSpec spec)
    {
        var chip = new SyntheticMmicChip(spec);
        var rng = new Random(spec.Seed);
        double size = spec.SizeUm;

        void Traces(List<List<(double, double)>> into)
        {
            for (int i = 0; i < spec.TracesPerLevel; i++)
            {
                double w = 8 + rng.Next(0, 5) * 5;                 // 8 … 28 µm
                double x = rng.NextDouble() * (size - 100) + 50, y = rng.NextDouble() * (size - 100) + 50;
                double len = 80 + rng.NextDouble() * 300;
                if (rng.Next(2) == 0) into.Add(Rect(x, y, Math.Min(size, x + len), y + w));
                else into.Add(Rect(x, y, x + w, Math.Min(size, y + len)));
            }
        }
        Traces(chip.Metal1);
        Traces(chip.Metal2);

        // Three 70 µm edge feeds on Metal2, each fed from its drawn end at the chip edge.
        double feed = size * 5 / 28, y1 = size * 3 / 7, y2 = size * 9 / 14, x3 = size * 3 / 14;
        chip.Metal2.Add(Rect(0, y1, feed, y1 + 70));
        chip.Metal2.Add(Rect(size - feed, y2, size, y2 + 70));
        chip.Metal2.Add(Rect(x3, 0, x3 + 70, feed));
        chip.PortSites.Add((1, 0, y1 + 35, PlanarPortSide.MinX));
        chip.PortSites.Add((2, size, y2 + 35, PlanarPortSide.MaxX));
        chip.PortSites.Add((3, x3 + 35, 0, PlanarPortSide.MinY));

        // The region: Metal2 over Metal1, joined by one via footprint the size of the overlap.
        double rx = size / 2, ry = 100, r = spec.RegionUm;
        bool region = r > 0;
        if (region)
        {
            chip.Metal1.Add(Rect(rx, ry, rx + r, ry + r));
            chip.Metal2.Add(Rect(rx, ry, rx + r, ry + r));
        }

        // Posts: a 6 µm cut on a 14 µm pad on both levels, kept clear of the region.
        for (int i = 0; i < spec.SmallVias; i++)
        {
            double x = rng.NextDouble() * (size - 100) + 50, y = rng.NextDouble() * (size - 100) + 50;
            if (region && x > rx - 20 && x < rx + r + 20 && y > ry - 20 && y < ry + r + 20) { i--; continue; }
            chip.Metal1.Add(Rect(x - 7, y - 7, x + 7, y + 7));
            chip.Metal2.Add(Rect(x - 7, y - 7, x + 7, y + 7));
            chip.Posts.Add(Rect(x - 3, y - 3, x + 3, y + 3));
        }
        if (region) chip.Posts.Add(Rect(rx, ry, rx + r, ry + r));

        // Backside vias: a 60 µm via on an 80 µm Metal1 pad.
        for (int i = 0; i < spec.BacksideVias; i++)
        {
            double x = rng.NextDouble() * (size - 150) + 75, y = rng.NextDouble() * (size - 150) + 75;
            if (spec.OctagonalBackside) { chip.Metal1.Add(Octagon(x, y, 80)); chip.Backside.Add(Octagon(x, y, 60)); }
            else { chip.Metal1.Add(Rect(x - 40, y - 40, x + 40, y + 40)); chip.Backside.Add(Rect(x - 30, y - 30, x + 30, y + 30)); }
        }
        return chip;
    }

    /// <summary>The two-level GaAs medium: 100 µm GaAs, a 3 µm εr 2.7 dielectric, Metal1 on the interface, Metal2 on top.</summary>
    public static LayerStack Stack => new(Termination.Pec,
        [new MediumLayer(100e-6, new EmMaterial(12.90, 0.0020)), new MediumLayer(3e-6, new EmMaterial(2.70, 0.0020))],
        Termination.Air);

    /// <summary>The chip as the planar kernel's input, at <paramref name="maxFrequencyHz"/>.</summary>
    public PlanarProblem ToProblem(double maxFrequencyHz = 10e9)
    {
        var stack = Stack;
        var vias = new List<PlanarVia>();
        if (Posts.Count > 0) vias.Add(new PlanarVia(0, 1, Posts.Select(Poly).ToList(), SigmaGold));
        if (Backside.Count > 0) vias.Add(new PlanarVia(PlanarVia.GroundTerminal, 0, Backside.Select(Poly).ToList(), SigmaGold));
        return new PlanarProblem(
            [new PlanarConductorLayer("M1", Metal1.Select(Poly).ToList(), SigmaGold, 2e-6, stack.InterfaceZ[1]),
             new PlanarConductorLayer("M2", Metal2.Select(Poly).ToList(), SigmaGold, 3e-6, stack.TopZ)],
            GroundedSlab.GaAsStarter, maxFrequencyHz, null, stack, vias);
    }

    /// <summary>The three edge ports, on Metal2 (level 1), 50 Ω.</summary>
    public List<PlanarPort> Ports()
        => PortSites.Select(p => new PlanarPort(p.Number, new EmPoint(p.X * 1e-6, p.Y * 1e-6), p.Side, 50, 1)).ToList();

    private static PlanarPolygon Poly(List<(double X, double Y)> ring)
        => new(ring.Select(v => new EmPoint(v.X * 1e-6, v.Y * 1e-6)).ToList());

    private static List<(double X, double Y)> Rect(double x0, double y0, double x1, double y1)
        => [(x0, y0), (x1, y0), (x1, y1), (x0, y1)];

    private static List<(double X, double Y)> Octagon(double cx, double cy, double across)
    {
        var ring = new List<(double, double)>(8);
        for (int k = 0; k < 8; k++)
        {
            double a = Math.PI / 8 + k * Math.PI / 4;
            ring.Add((cx + across / 2 * Math.Cos(a), cy + across / 2 * Math.Sin(a)));
        }
        return ring;
    }
}
