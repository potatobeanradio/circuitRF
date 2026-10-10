// Which colour is which layer — brief-img-3-trace-layout-image.md R-im3-4 (D7).
//
// A picture's colours are its only layer information. Each colour cluster (IM-1) is a ROW the user edits: the
// background, a technology layer (or two — an overlap), Drill, Board outline, Silkscreen or Ignore. Auto fills the rows
// once; a map the user has edited is never replaced by a re-run — it is rebound to the re-run's clusters by colour.
//
// Auto, in this order:
//   - the background is the cluster with the most border contact (ties: the lightest);
//   - Drill is a cluster whose components are at least 80 % circles sitting inside other copper;
//   - Silkscreen is a cluster of thin strokes with text-like rows;
//   - an OVERLAP — a viewer drawing one translucent layer over another — is a colour within ΔE 12 of the second
//     composited under the first: obs(P over Q) = obs(P) + β·(obs(Q) − background), β the top layer's transparency.
//     It maps to BOTH layers;
//   - the rest by descending area onto the technology's copper layers, top first; any past the last copper layer are
//     ignored.

using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Design.Layout.Recognition.Silkscreen;

namespace CircuitRF.Design.Layout.Recognition.Image;

/// <summary>What a colour is read as.</summary>
public enum ImageLayerRole
{
    /// <summary>The paper. Never traced.</summary>
    Background,

    /// <summary>Copper (or any other technology layer): traced as filled shapes on <see cref="ImageLayerRow.Layers"/>.</summary>
    Layer,

    /// <summary>Plated holes: each circle becomes the technology's via.</summary>
    Drill,

    /// <summary>The board's edge: its centre lines become paths on the outline layer.</summary>
    BoardOutline,

    /// <summary>Legend: traced as filled shapes on the silkscreen layer, and kept as strokes for IM-4's text reader.</summary>
    Silkscreen,

    /// <summary>Not traced.</summary>
    Ignore,
}

/// <summary>One colour and what it is read as.</summary>
/// <param name="Cluster">Its index in the picture's <see cref="ColourClusterSet"/>.</param>
/// <param name="Rgb">The colour, 0xRRGGBB.</param>
/// <param name="Share">The share of the picture it covers.</param>
/// <param name="Layers">Technology layer NAMES it is traced onto: one for a layer, two for an overlap, the target layer for
/// Drill (the via layer), Board outline and Silkscreen when the technology has one; empty otherwise.</param>
/// <param name="Overlap">Auto read it as the overlap of two other colours.</param>
public sealed record ImageLayerRow(int Cluster, int Rgb, double Share, ImageLayerRole Role, IReadOnlyList<string> Layers,
                                   bool Overlap = false)
{
    public string Hex => $"#{Rgb:x6}";

    /// <summary>The provenance spelling: the layer names joined with <c>+</c>, or the role.</summary>
    public string Spelling => Role switch
    {
        ImageLayerRole.Layer => Layers.Count == 0 ? "ignore" : string.Join("+", Layers),
        ImageLayerRole.Background => "background",
        ImageLayerRole.Drill => "drill",
        ImageLayerRole.BoardOutline => "outline",
        ImageLayerRole.Silkscreen => "silkscreen",
        _ => "ignore",
    };
}

/// <summary>A picture's colour → layer mapping.</summary>
/// <param name="Edited">The user changed it: a re-run rebinds it and never replaces it.</param>
public sealed record ImageLayerMap(IReadOnlyList<ImageLayerRow> Rows, bool Edited = false)
{
    /// <summary>Colours within this ΔE are the same colour when a map is rebound to another reading.</summary>
    public const double MatchDeltaE = 8.0;

    /// <summary>A colour within this ΔE of one composited under another is their overlap.</summary>
    public const double OverlapDeltaE = 12.0;

    /// <summary>A cluster is Drill when at least this share of its components are circles inside other copper.</summary>
    public const double DrillCircleShare = 0.8;

    /// <summary>The row of cluster <paramref name="cluster"/>, or null.</summary>
    public ImageLayerRow? Of(int cluster) => Rows.FirstOrDefault(r => r.Cluster == cluster);

    /// <summary>The map with one row changed — an edit, so the result is <see cref="Edited"/>.</summary>
    public ImageLayerMap With(int cluster, ImageLayerRole role, params string[] layers) =>
        new([.. Rows.Select(r => r.Cluster == cluster ? r with { Role = role, Layers = layers, Overlap = false } : r)], Edited: true);

    /// <summary>The provenance block: <c>#rrggbb</c> → layer name(s) or role.</summary>
    public SortedDictionary<string, string> ToProvenance()
    {
        var d = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in Rows) d[r.Hex] = r.Spelling;
        return d;
    }

    /// <summary>
    /// This map laid over another reading's clusters: each cluster takes the row whose colour is nearest within
    /// <see cref="MatchDeltaE"/>; a cluster no row matches is ignored. What makes an edited map survive a re-run — and
    /// a preset apply to a new picture from the same source.
    /// </summary>
    public ImageLayerMap Rebind(ColourClusterSet clusters)
    {
        var rows = new List<ImageLayerRow>(clusters.Clusters.Count);
        foreach (var c in clusters.Clusters)
        {
            ImageLayerRow? best = null;
            double bd = MatchDeltaE * MatchDeltaE;
            foreach (var r in Rows)
            {
                double d = c.Lab.DeltaE2(CieLab.FromRgb(r.Rgb));
                if (d <= bd) { bd = d; best = r; }
            }
            rows.Add(best is null
                ? new ImageLayerRow(c.Index, c.Rgb, c.Share, ImageLayerRole.Ignore, [])
                : best with { Cluster = c.Index, Rgb = c.Rgb, Share = c.Share });
        }
        return new ImageLayerMap(rows, Edited);
    }

    // ── Auto ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The map Auto reads (D7): see this file's header for the rules.</summary>
    public static ImageLayerMap Auto(ColourClusterSet clusters, Technology technology)
    {
        ArgumentNullException.ThrowIfNull(clusters);
        ArgumentNullException.ThrowIfNull(technology);
        var cs = clusters.Clusters;
        int n = cs.Count;
        var role = new ImageLayerRole?[n];
        var layers = new List<string>[n];
        for (int k = 0; k < n; k++) layers[k] = [];

        int bg = 0;
        for (int k = 1; k < n; k++)
        {
            double d = cs[k].BorderShare - cs[bg].BorderShare;
            if (d > 1e-9 || (Math.Abs(d) <= 1e-9 && cs[k].Lab.L > cs[bg].Lab.L)) bg = k;
        }
        role[bg] = ImageLayerRole.Background;

        var masks = new BinaryImage?[n];
        BinaryImage Mask(int k) => masks[k] ??= MaskOf(clusters, k);

        for (int k = 0; k < n; k++)
        {
            if (role[k] is not null) continue;
            if (IsDrill(clusters, k, bg, Mask(k)))
            {
                role[k] = ImageLayerRole.Drill;
                if (ViaBarrelLayer(technology) is { } via) layers[k].Add(NameOf(technology, via));
            }
            else if (IsSilkscreen(Mask(k)))
            {
                role[k] = ImageLayerRole.Silkscreen;
                if (SilkscreenText.Layers(technology).FirstOrDefault() is { Layer: var silk } && technology.Layers.Any(l => l.Key == silk))
                    layers[k].Add(NameOf(technology, silk));
            }
        }

        // Overlaps among what is left, then the copper by area.
        var copperCandidates = Enumerable.Range(0, n).Where(k => role[k] is null).ToList();
        var overlapOf = new Dictionary<int, (int P, int Q)>();
        foreach (int o in copperCandidates)
        {
            var others = copperCandidates.Where(k => k != o && !overlapOf.ContainsKey(k)).ToList();
            double best = OverlapDeltaE;
            (int, int)? pair = null;
            for (int i = 0; i < others.Count; i++)
                for (int j = 0; j < others.Count; j++)
                {
                    if (i == j) continue;
                    int p = others[i], q = others[j];
                    // An overlap is never larger than both of the layers it is the overlap of.
                    if (cs[o].Share > Math.Max(cs[p].Share, cs[q].Share)) continue;
                    double d = OverlapDistance(cs[o].Rgb, cs[p].Rgb, cs[q].Rgb, cs[bg].Rgb);
                    if (d < best) { best = d; pair = (Math.Min(p, q), Math.Max(p, q)); }
                }
            if (pair is { } pq) overlapOf[o] = pq;
        }

        var copperLayers = CopperLayers(technology);
        int next = 0;
        foreach (int k in copperCandidates.Where(k => !overlapOf.ContainsKey(k)).OrderByDescending(k => cs[k].Share).ThenBy(k => k))
        {
            if (next < copperLayers.Count)
            {
                role[k] = ImageLayerRole.Layer;
                layers[k].Add(NameOf(technology, copperLayers[next++]));
            }
            else role[k] = ImageLayerRole.Ignore;
        }
        foreach (var (o, (p, q)) in overlapOf)
        {
            var both = layers[p].Concat(layers[q]).Distinct(StringComparer.Ordinal).ToList();
            role[o] = both.Count > 0 ? ImageLayerRole.Layer : ImageLayerRole.Ignore;
            layers[o] = both;
        }

        var rows = new List<ImageLayerRow>(n);
        for (int k = 0; k < n; k++)
            rows.Add(new ImageLayerRow(k, cs[k].Rgb, cs[k].Share, role[k] ?? ImageLayerRole.Ignore, layers[k], overlapOf.ContainsKey(k)));
        return new ImageLayerMap(rows);
    }

    /// <summary>How far <paramref name="o"/> is from <paramref name="p"/> drawn translucently over <paramref name="q"/>:
    /// the least ΔE over the top layer's transparency β of <c>p + β·(q − background)</c>, in the sRGB bytes.</summary>
    internal static double OverlapDistance(int o, int p, int q, int background)
    {
        var lo = CieLab.FromRgb(o);
        double best = double.MaxValue;
        for (int i = 1; i < 20; i++)
        {
            double beta = i / 20.0;
            int Ch(int shift) => Math.Clamp((int)Math.Round(((p >> shift) & 0xFF) + beta * (((q >> shift) & 0xFF) - ((background >> shift) & 0xFF))), 0, 255);
            best = Math.Min(best, lo.DeltaE(CieLab.FromRgb(Ch(16), Ch(8), Ch(0))));
        }
        return best;
    }

    internal static BinaryImage MaskOf(ColourClusterSet clusters, int k)
    {
        var m = new BinaryImage(clusters.Width, clusters.Height);
        for (int y = 0, i = 0; y < clusters.Height; y++)
            for (int x = 0; x < clusters.Width; x++, i++)
                if (clusters.Label(x, y) == k) m.Pixels[i] = 1;
        return m;
    }

    /// <summary>At least <see cref="DrillCircleShare"/> of the cluster's components are round and ringed by other,
    /// non-background colour.</summary>
    private static bool IsDrill(ColourClusterSet clusters, int k, int bg, BinaryImage mask)
    {
        var comps = Components.Label(mask).Components.Where(c => c.Area >= 4).ToList();
        if (comps.Count == 0) return false;
        int circles = 0;
        foreach (var c in comps)
        {
            int w = c.Right - c.Left + 1, h = c.Bottom - c.Top + 1;
            if (c.Holes > 0 || Math.Abs((double)w / h - 1) > 0.15) continue;
            double fill = (double)c.Area / (w * h);
            if (fill < 0.68 || fill > 0.88) continue;
            double r = Math.Sqrt(c.Area / Math.PI) + 1.5;
            int inside = 0, samples = 16;
            for (int s = 0; s < samples; s++)
            {
                double a = 2 * Math.PI * s / samples;
                int x = (int)Math.Floor(c.CentroidX + r * Math.Cos(a)), y = (int)Math.Floor(c.CentroidY + r * Math.Sin(a));
                if ((uint)x >= (uint)clusters.Width || (uint)y >= (uint)clusters.Height) continue;
                int l = clusters.Label(x, y);
                if (l != k && l != bg) inside++;
            }
            if (inside >= 0.75 * samples) circles++;
        }
        return circles >= DrillCircleShare * comps.Count;
    }

    /// <summary>Thin strokes — nothing deeper than a few pixels — that read as rows of text.</summary>
    private static bool IsSilkscreen(BinaryImage mask)
    {
        long count = mask.Count();
        if (count == 0) return false;
        var d2 = DistanceTransform.SquaredToBackground(mask);
        long deep = 0;
        double depth2 = Math.Pow(Math.Max(3.0, 0.006 * Math.Min(mask.Width, mask.Height)), 2);
        foreach (long v in d2) if (v > depth2) deep++;
        if (deep > 0.1 * count) return false;
        return ImageKind.TextShare(mask, count) >= 0.3;
    }

    // ── the technology's layers ─────────────────────────────────────────────────────────────────────

    internal static string NameOf(Technology tech, LayerKey key) =>
        tech.Layers.FirstOrDefault(l => l.Key == key)?.Name is { Length: > 0 } name ? name : $"{key.Layer}/{key.Datatype}";

    internal static LayerKey? KeyOf(Technology tech, string name) =>
        tech.Layers.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.Ordinal))?.Key
        ?? tech.Layers.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase))?.Key;

    /// <summary>Each stackup conductor's first drawing layer, top to bottom.</summary>
    internal static List<LayerKey> CopperLayers(Technology tech) =>
        [.. Conductors.Of(tech).Where(c => c.DrawingLayers.Count > 0).Select(c => c.DrawingLayers[0]).Distinct()];

    /// <summary>The via barrel layer: a plated via entry's drawing layer first.</summary>
    internal static LayerKey? ViaBarrelLayer(Technology tech)
    {
        var vias = tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Via && l.DrawingLayers.Count > 0).ToList();
        var pick = vias.FirstOrDefault(v => v.Plated != false) ?? vias.FirstOrDefault();
        return pick?.DrawingLayers[0];
    }

    /// <summary>The board outline layer: purpose <c>outline</c>, the board format's edge alias, or a layer named so.</summary>
    internal static LayerKey? OutlineLayer(Technology tech) =>
        tech.Layers.FirstOrDefault(l => string.Equals(l.Purpose?.Trim(), "outline", StringComparison.OrdinalIgnoreCase))?.Key
        ?? tech.Layers.FirstOrDefault(l => string.Equals(l.Interchange?.PcbLayerName, "Edge.Cuts", StringComparison.OrdinalIgnoreCase))?.Key
        ?? tech.Layers.FirstOrDefault(l => (l.Name ?? "").Contains("outline", StringComparison.OrdinalIgnoreCase))?.Key;

    /// <summary>The first documentation layer: purpose <c>documentation</c>, else a layer whose name says so.</summary>
    internal static LayerKey? DocumentationLayer(Technology tech) =>
        tech.Layers.FirstOrDefault(l => (l.Purpose?.Trim().ToLowerInvariant()) is "documentation" or "annotation")?.Key
        ?? tech.Layers.FirstOrDefault(l => (l.Name ?? "").ToLowerInvariant() is var n
                                           && (n.Contains("documentation") || n.Contains("dwgs") || n.Contains("comment")))?.Key;
}
