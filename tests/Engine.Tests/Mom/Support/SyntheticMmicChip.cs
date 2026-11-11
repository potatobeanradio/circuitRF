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
    int    Seed             = 7,
    bool   MimCapacitors    = false,
    bool   DiagonalTraces   = false)
{
    /// <summary>brief-pms-0's fixture S.</summary>
    public static SyntheticMmicChipSpec S => new();

    /// <summary>brief-pms-0's fixture L: denser posts and traces.</summary>
    public static SyntheticMmicChipSpec L => new(TracesPerLevel: 30, SmallVias: 150);

    /// <summary>PMS-2 R-pms2-1 — density S or L at another chip size. Feature counts scale with area and the region's
    /// side with the size, so every member has the density of the 1.4 mm preset; at 1,400 µm it IS that preset.</summary>
    public static SyntheticMmicChipSpec Family(char density, double sizeUm)
    {
        var at = density switch { 'S' => S, 'L' => L, _ => throw new ArgumentOutOfRangeException(nameof(density)) };
        if (sizeUm == at.SizeUm) return at;
        double area = sizeUm * sizeUm / (at.SizeUm * at.SizeUm);
        int Scale(int n) => Math.Max(1, (int)Math.Round(n * area));
        return at with
        {
            SizeUm = sizeUm,
            TracesPerLevel = Scale(at.TracesPerLevel),
            SmallVias = Scale(at.SmallVias),
            BacksideVias = Scale(at.BacksideVias),
            RegionUm = at.RegionUm * sizeUm / at.SizeUm,
        };
    }
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
    /// <summary>MIM capacitor top plates (the MIM Metal level), with the option on.</summary>
    public List<List<(double X, double Y)>> MimMetal { get; } = [];
    /// <summary>MIM Via windows joining a MIM plate to the Metal2 pad over it.</summary>
    public List<List<(double X, double Y)>> MimVia { get; } = [];
    /// <summary>Ports fed on Metal1 rather than Metal2 (the accuracy blocks' third port).</summary>
    public HashSet<int> PortsOnMetal1 { get; } = [];
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
        double rx = size / 2, ry = size * 100 / 1400, r = spec.RegionUm;
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

        // PMS-2 options, each on its own stream so the presets above are unchanged with them off.
        if (spec.MimCapacitors)
        {
            // A MIM capacitor: a Metal1 bottom plate, the MIM Metal top plate 4 µm inside it, a Metal2 pad over the top
            // plate, and a MIM Via window 4 µm inside the plate joining the two. Six per 1.4 mm chip, 40–100 µm plates.
            var mr = new Random(spec.Seed + 101);
            int caps = Math.Max(1, (int)Math.Round(6 * size * size / (1400.0 * 1400.0)));
            for (int i = 0, tries = 0; i < caps && tries < 500; tries++)
            {
                double side = 40 + mr.Next(0, 7) * 10;
                double x = mr.NextDouble() * (size - side - 120) + 60, y = mr.NextDouble() * (size - side - 120) + 60;
                if (region && x + side > rx - 20 && x < rx + r + 20 && y + side > ry - 20 && y < ry + r + 20) continue;
                chip.Metal1.Add(Rect(x - 4, y - 4, x + side + 4, y + side + 4));
                chip.MimMetal.Add(Rect(x, y, x + side, y + side));
                chip.Metal2.Add(Rect(x, y, x + side, y + side));
                chip.MimVia.Add(Rect(x + 4, y + 4, x + side - 4, y + side - 4));
                i++;
            }
        }
        if (spec.DiagonalTraces)
        {
            // One 45° trace per level: a 15 µm strip, up to 300 µm long, staircased by the tensor grid.
            var dr = new Random(spec.Seed + 202);
            double len = Math.Min(300, 0.4 * size);
            foreach (var into in new[] { chip.Metal1, chip.Metal2 })
            {
                double d = 15 / Math.Sqrt(2), l = len / Math.Sqrt(2);
                double x, y;
                int tries = 0;
                do
                {
                    x = dr.NextDouble() * (size - len - 60) + 30; y = dr.NextDouble() * (size - len - 60) + 30;
                }
                // Kept off the region, where both levels are already metal and the trace would add nothing.
                while (region && tries++ < 500 && x + l + d > rx && x < rx + r && y + l > ry && y - d < ry + r);
                into.Add([(x, y), (x + d, y - d), (x + d + l, y - d + l), (x + l, y + l)]);
            }
        }
        return chip;
    }

    /// <summary>
    /// PMS-2 R-pms2-1 — the accuracy blocks. A 250 µm block on the same medium: a 20 µm Metal2 line across it
    /// (ports 1 and 2), a 20 µm Metal1 line from the bottom edge (port 3) up to an 80 µm backside-via pad, two posts on
    /// each line (6 µm cuts on 14 µm pads on both levels), and — <paramref name="regionVia"/>, A1 — a 120 µm region
    /// where Metal2 overlaps Metal1 and is joined to it, on the Metal2 line. A2 is the same block without the region.
    /// </summary>
    public static SyntheticMmicChip AccuracyBlock(bool regionVia)
    {
        var chip = new SyntheticMmicChip(new SyntheticMmicChipSpec(
            SizeUm: 250, TracesPerLevel: 1, SmallVias: 4, BacksideVias: 1, RegionUm: regionVia ? 120 : 0));
        chip.Metal2.Add(Rect(0, 115, 250, 135));
        chip.Metal1.Add(Rect(200, 0, 220, 200));
        chip.Metal1.Add(Octagon(212, 212, 80));
        chip.Backside.Add(Octagon(212, 212, 60));
        foreach (var (x, y) in new[] { (30.0, 125.0), (230.0, 125.0), (210.0, 40.0), (210.0, 80.0) })
        {
            chip.Metal1.Add(Rect(x - 7, y - 7, x + 7, y + 7));
            chip.Metal2.Add(Rect(x - 7, y - 7, x + 7, y + 7));
            chip.Posts.Add(Rect(x - 3, y - 3, x + 3, y + 3));
        }
        if (regionVia)
        {
            chip.Metal1.Add(Rect(60, 60, 180, 180));
            chip.Metal2.Add(Rect(60, 60, 180, 180));
            chip.Posts.Add(Rect(60, 60, 180, 180));
        }
        chip.PortSites.Add((1, 0, 125, PlanarPortSide.MinX));
        chip.PortSites.Add((2, 250, 125, PlanarPortSide.MaxX));
        chip.PortSites.Add((3, 210, 0, PlanarPortSide.MinY));
        chip.PortsOnMetal1.Add(3);
        return chip;
    }

    /// <summary>The two-level GaAs medium: 100 µm GaAs, a 3 µm εr 2.7 dielectric, Metal1 on the interface, Metal2 on top.</summary>
    public static LayerStack Stack => new(Termination.Pec,
        [new MediumLayer(100e-6, new EmMaterial(12.90, 0.0020)), new MediumLayer(3e-6, new EmMaterial(2.70, 0.0020))],
        Termination.Air);

    /// <summary>PMS-2 — the three-level medium with MIM Metal ticked: 100 µm GaAs, the 0.2 µm εr 6.8 MIM dielectric
    /// (Metal1 under it, MIM Metal on it), then 2.8 µm εr 2.7 up to Metal2.</summary>
    public static LayerStack MimStack => new(Termination.Pec,
        [new MediumLayer(100e-6, new EmMaterial(12.90, 0.0020)), new MediumLayer(0.2e-6, new EmMaterial(6.8, 0.001)),
         new MediumLayer(2.8e-6, new EmMaterial(2.70, 0.0020))],
        Termination.Air);

    /// <summary>Metal2's level index in <see cref="ToProblem"/>.</summary>
    public int Metal2Level => Spec.MimCapacitors ? 2 : 1;

    /// <summary>The chip as the planar kernel's input, at <paramref name="maxFrequencyHz"/>.</summary>
    public PlanarProblem ToProblem(double maxFrequencyHz = 10e9)
    {
        if (Spec.MimCapacitors) return ToMimProblem(maxFrequencyHz);
        var stack = Stack;
        var vias = new List<PlanarVia>();
        if (Posts.Count > 0) vias.Add(new PlanarVia(0, 1, Posts.Select(Poly).ToList(), SigmaGold));
        if (Backside.Count > 0) vias.Add(new PlanarVia(PlanarVia.GroundTerminal, 0, Backside.Select(Poly).ToList(), SigmaGold));
        return new PlanarProblem(
            [new PlanarConductorLayer("M1", Metal1.Select(Poly).ToList(), SigmaGold, 2e-6, stack.InterfaceZ[1]),
             new PlanarConductorLayer("M2", Metal2.Select(Poly).ToList(), SigmaGold, 3e-6, stack.TopZ)],
            GroundedSlab.GaAsStarter, maxFrequencyHz, null, stack, vias);
    }

    /// <summary>
    /// The MIM option as <c>PlanarExtractor</c> resolves it: Metal1, MIM Metal and Metal2 as three levels. A post spans
    /// MIM Metal, so it becomes a chain — its footprint put on MIM Metal and one via per gap — exactly as
    /// <c>PlanarExtractor</c>'s <c>AddSpan</c> builds it; the MIM Via windows join MIM Metal to Metal2.
    /// </summary>
    private PlanarProblem ToMimProblem(double maxFrequencyHz)
    {
        var stack = MimStack;
        var posts = Posts.Select(Poly).ToList();
        var mim = MimMetal.Select(Poly).ToList();
        mim.AddRange(posts);
        var vias = new List<PlanarVia>();
        if (posts.Count > 0)
        {
            vias.Add(new PlanarVia(0, 1, posts, SigmaGold));
            vias.Add(new PlanarVia(1, 2, posts, SigmaGold));
        }
        if (MimVia.Count > 0) vias.Add(new PlanarVia(1, 2, MimVia.Select(Poly).ToList(), SigmaGold));
        if (Backside.Count > 0) vias.Add(new PlanarVia(PlanarVia.GroundTerminal, 0, Backside.Select(Poly).ToList(), SigmaGold));
        return new PlanarProblem(
            [new PlanarConductorLayer("M1", Metal1.Select(Poly).ToList(), SigmaGold, 2e-6, stack.InterfaceZ[1]),
             new PlanarConductorLayer("MIM", mim, SigmaGold, 0.25e-6, stack.InterfaceZ[2]),
             new PlanarConductorLayer("M2", Metal2.Select(Poly).ToList(), SigmaGold, 3e-6, stack.TopZ)],
            GroundedSlab.GaAsStarter, maxFrequencyHz, null, stack, vias);
    }

    /// <summary>The edge ports, 50 Ω: on Metal2 unless listed in <see cref="PortsOnMetal1"/>.</summary>
    public List<PlanarPort> Ports()
        => PortSites.Select(p => new PlanarPort(p.Number, new EmPoint(p.X * 1e-6, p.Y * 1e-6), p.Side, 50,
                                                PortsOnMetal1.Contains(p.Number) ? 0 : Metal2Level)).ToList();

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
