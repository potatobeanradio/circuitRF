// PMS-2 R-pms2-2 — where every gridline of the shared tensor grid comes from.
//
// An instrument on the pattern of ConformalDiagnostics: SurfaceMesher.Mesh fills it when one is supplied and never
// reads it, so the mesh is bit-identical with it attached (gated). It records, per axis, the final gridlines, the
// source each one is attributed to, every hard-line request with the stretch of the line its feature occupies, and
// every edge-fan attractor with the stretch of the edge that raised it. The stretches are what a locally refined grid
// would keep a line to; on the tensor grid every line runs across the whole part.

namespace CircuitRF.Engine.Mom;

/// <summary>What a final gridline is attributed to. A line requested by several things takes the first in this order:
/// a port's feed, a conductor edge, a resistive film edge, a via footprint, a port's edge span; then, for a line no
/// hard edge asked for, the edge fan if the fan's field is finer than the bulk cap there, else the bulk cap.</summary>
public enum PlanarGridLineSource
{
    /// <summary>An edge of the conductor polygon a port sits on (its feed, grown lead included).</summary>
    PortFeed,
    /// <summary>An axis-parallel edge of conductor artwork.</summary>
    Conductor,
    /// <summary>An edge of a resistive film region.</summary>
    Film,
    /// <summary>An axis-parallel edge of a via footprint.</summary>
    ViaFootprint,
    /// <summary>One end of a narrowed edge port's span.</summary>
    PortSpan,
    /// <summary>A line the graded edge fan placed.</summary>
    EdgeFan,
    /// <summary>A bulk subdivision line where the cap is λ_g / cells per wavelength.</summary>
    BulkWavelength,
    /// <summary>A bulk subdivision line where the cap is the narrowest metal ÷ MinCellsAcrossConductor.</summary>
    BulkMinCellsAcross,
    /// <summary>A bulk subdivision line under a current-model pitch field.</summary>
    BulkPitchField,
}

/// <summary>One hard-line request: the line's coordinate, what asked for it, and the stretch of the line (along it,
/// in metres) the requesting edge occupies. <see cref="LayerIndex"/> is the conductor level, or −1 for a via or a
/// port span.</summary>
public sealed record PlanarGridLineOrigin(double Coord, PlanarGridLineSource Source, double AlongLoM, double AlongHiM,
                                          int LayerIndex);

/// <summary>One edge-fan attractor as the marcher used it: coordinate, finest cell, growth rate (r − 1), and the stretch
/// of the edge that raised it.</summary>
public sealed record PlanarGridAttractor(double Coord, double C0M, double Growth, double AlongLoM, double AlongHiM);

/// <summary>One axis of the grid, attributed.</summary>
public sealed class PlanarGridAxis
{
    /// <summary>The final gridlines, ascending — the mesh's own <c>GridX</c>/<c>GridY</c>.</summary>
    public double[] Lines { get; internal set; } = [];
    /// <summary>The source of each line in <see cref="Lines"/>.</summary>
    public PlanarGridLineSource[] Source { get; internal set; } = [];
    /// <summary>Every hard-line request, before de-duplication (several features can ask for one line).</summary>
    public List<PlanarGridLineOrigin> HardOrigins { get; } = [];
    /// <summary>The edge-fan attractors the marcher graded toward. Empty when the edge mesh is off.</summary>
    public List<PlanarGridAttractor> Attractors { get; } = [];
    /// <summary>The bulk cap: λ_g / cells per wavelength.</summary>
    public double WaveCapM { get; internal set; }
    /// <summary>The bulk cap's geometry term: the narrowest metal on this axis ÷ MinCellsAcrossConductor.</summary>
    public double NarrowCapM { get; internal set; }
    /// <summary>The bulk cap the grid was built with (the coarsest pitch when a pitch field is on).</summary>
    public double BulkCapM { get; internal set; }

    /// <summary>Lines per source; sums to <c>Lines.Length</c>.</summary>
    public IReadOnlyDictionary<PlanarGridLineSource, int> CountBySource()
    {
        var counts = new Dictionary<PlanarGridLineSource, int>();
        foreach (var s in Enum.GetValues<PlanarGridLineSource>()) counts[s] = 0;
        foreach (var s in Source) counts[s]++;
        return counts;
    }
}

/// <summary>
/// <b>PMS-2 — the grid's line provenance.</b> Pass one to <see cref="SurfaceMesher.Mesh"/>; it is filled and never read
/// by the mesher, so the mesh is identical with or without it.
/// </summary>
public sealed class PlanarGridDiagnostics
{
    public PlanarGridAxis X { get; } = new();
    public PlanarGridAxis Y { get; } = new();

    /// <summary>The detail floor the mesh was built with (0 when off).</summary>
    public double DetailFloorM { get; internal set; }

    /// <summary>Edges long enough to crowd that raised no fan because they are shorter than the detail floor.</summary>
    public int FansSuppressedByDetailFloor { get; internal set; }

    /// <summary>Whether a current-model pitch field set the bulk cap.</summary>
    public bool PitchField { get; internal set; }

    internal void AttributeAxis(PlanarGridAxis axis, double[] lines, Func<double, double> cap,
                                IReadOnlyList<GradedAttractor> graded, IReadOnlyList<EdgeAttractor> raw,
                                bool anyGrowth, double waveCap, double narrowCap, double bulkCap)
    {
        axis.Lines = lines;
        axis.WaveCapM = waveCap;
        axis.NarrowCapM = narrowCap;
        axis.BulkCapM = bulkCap;
        for (int i = 0; i < graded.Count; i++)
        {
            var e = raw[i];
            axis.Attractors.Add(new PlanarGridAttractor(graded[i].Coord, graded[i].C0, graded[i].Growth,
                                                        e.AlongLo, e.AlongHi));
        }

        // The hard requests, sorted by coordinate, each with the highest-priority source asking for that coordinate.
        var hard = axis.HardOrigins.OrderBy(o => o.Coord).ToList();
        double span = lines.Length > 1 ? lines[^1] - lines[0] : 1.0;
        double tol = Math.Max(span * 1e-9, 1e-15);

        var src = new PlanarGridLineSource[lines.Length];
        int h = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            double x = lines[i];
            while (h < hard.Count && hard[h].Coord < x - tol) h++;
            var best = (PlanarGridLineSource?)null;
            for (int k = h; k < hard.Count && hard[k].Coord <= x + tol; k++)
                if (best is null || hard[k].Source < best) best = hard[k].Source;
            if (best is { } b) { src[i] = b; continue; }

            double c = cap(x);
            double fan = double.PositiveInfinity;
            if (anyGrowth)
                foreach (var a in graded)
                    if (a.C0 > 0) fan = Math.Min(fan, a.C0 + a.Growth * Math.Abs(x - a.Coord));
            if (fan < c * (1 - 1e-9)) src[i] = PlanarGridLineSource.EdgeFan;
            else if (PitchField) src[i] = PlanarGridLineSource.BulkPitchField;
            else src[i] = waveCap <= narrowCap ? PlanarGridLineSource.BulkWavelength
                                               : PlanarGridLineSource.BulkMinCellsAcross;
        }
        axis.Source = src;
    }
}
