// PMS-1 R-pms1-1 — the synthetic MMIC chip (Engine.Tests/Mom/Support/SyntheticMmicChip.cs, linked into this project)
// as a layout on the SHIPPED MMIC GaAs technology, so the chip reaches the planar solver through the extractor and
// EmRunService.Preflight exactly as a drawn layout does — the path `circuitrf check`, `em --dry-run` and the panel take.

using CircuitRF.Engine.Tests.Mom.Support;

namespace CircuitRF.Ui.Tests.Em;

internal static class SyntheticMmicChipLayout
{
    public const string TechnologyId = "mmic-GaAs_2LM_100um";
    public const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    private static readonly LayerKey Metal1      = new(1, 0);
    private static readonly LayerKey Metal2      = new(2, 0);
    private static readonly LayerKey Post        = new(3, 0);   // "Metal1-Metal2 Post"
    private static readonly LayerKey BacksideVia = new(8, 0);   // "Backside Via"

    /// <summary>The chip's artwork: Metal1, Metal2, the posts and the region on the post layer, the backside vias, and
    /// a port label on each feed's drawn end (Metal2).</summary>
    public static LayoutView View(SyntheticMmicChip chip)
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        foreach (var ring in chip.Metal1) view.Shapes.Add(Polygon(Metal1, ring));
        foreach (var ring in chip.Metal2) view.Shapes.Add(Polygon(Metal2, ring));
        foreach (var ring in chip.Posts) view.Shapes.Add(Polygon(Post, ring));
        foreach (var ring in chip.Backside) view.Shapes.Add(Polygon(BacksideVia, ring));
        foreach (var (number, x, y, _) in chip.PortSites)
            view.Shapes.Add(new LabelShape
            {
                Layer = Metal2, X = Um(x), Y = Um(y), Text = $"P{number}", Height = Um(4), IsPort = true,
            });
        return view;
    }

    /// <summary>The source a setup reads: the view, written to <paramref name="clayPath"/> and read back with the
    /// persistence the editor uses, on the shipped technology.</summary>
    public static EmLayoutSource Source(SyntheticMmicChip chip, string clayPath)
    {
        LayoutPersistence.SaveToFile(clayPath, View(chip));
        return new EmLayoutSource(clayPath, LayoutPersistence.LoadFromFile(clayPath),
                                  ShippedTechnologies.Load(TechnologyId), Dbu);
    }

    private static long Um(double v) => (long)Math.Round(v * Dbu);

    private static PolygonShape Polygon(LayerKey layer, List<(double X, double Y)> ring)
    {
        var xy = new long[ring.Count * 2];
        for (int i = 0; i < ring.Count; i++) { xy[2 * i] = Um(ring[i].X); xy[2 * i + 1] = Um(ring[i].Y); }
        return new PolygonShape { Layer = layer, Xy = xy };
    }
}
