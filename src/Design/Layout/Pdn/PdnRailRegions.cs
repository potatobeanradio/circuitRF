// Which copper IS this rail, and is it one region or three islands
// (docs/sonnet-briefs/brief-railrf-3-mesh-extractor.md R-rail3-3 / R-rail3-4).
//
// ── THE WALK IS ALREADY WRITTEN, AND THERE IS NO SECOND ONE ────────────────────────────────────
//
// Layout/Drc/DrcConnectivity.cs partitions flat per-layer geometry into electrically-joined pieces,
// bridging layers THROUGH VIA GEOMETRY rather than by assuming the metal above and below overlaps —
// which is what makes an offset staircase of metal connect correctly. It is `internal`, which is not
// an obstacle: this file is the same assembly.
//
// DO NOT COPY IT, DO NOT MAKE IT PUBLIC, AND DO NOT WRITE A SECOND WALK. A rail whose island
// structure the DRC and railRF disagree about is a bug neither of them reports.
//
// What it does NOT do is name a net. Net identity comes from the board netlist or the .kicad_pcb
// (brief 2) and this file joins the two: a NET POINT is a (net name, coordinate) pair, and the
// pieces containing those points are the rail.
//
// ── THE COPPER STOPS AT EVERY PAD, AND THAT IS CORRECT (§2.8) ──────────────────────────────────
//
// On imported artwork the board is not electrically continuous until the user has said what bridges
// each gap. So the island structure is a first-class OUTPUT — "this rail is three regions joined by
// a 20 mil neck" — and §2.3 step 2 says that alone has caught real problems. Two islands joined by
// nothing at DC and by a capacitor at AC is NOT an error and must not be reported as one. It is two
// regions, stated.

using Clipper2Lib;
using CircuitRF.Design.Layout.Drc;

namespace CircuitRF.Design.Layout.Pdn;

/// <summary>
/// One conductor of the stackup as the extraction reads it: its drawing layer, its thickness and its
/// conductivity. <see cref="SheetResistanceOhmsPerSquare"/> is the whole of the DC model.
/// </summary>
/// <param name="StackupName">The <see cref="StackupLayer.Name"/> it came from.</param>
/// <param name="Layer">The drawing layer its copper is on.</param>
/// <param name="ThicknessMetres">Finished copper thickness.</param>
/// <param name="ConductivitySm">Conductivity at the extraction's stated temperature, S/m.</param>
public sealed record PdnConductor(
    string StackupName, LayerKey Layer, double ThicknessMetres, double ConductivitySm)
{
    /// <summary>
    /// <c>Rs = ρ / T</c> — the sheet resistance BELOW TWO SKIN DEPTHS, which at ω = 0 is every
    /// frequency this brief covers. 0.49 mΩ/square at 1 oz, 0.99 mΩ/square at 0.5 oz (§2.8).
    ///
    /// <para><b>This is ONE conductor's sheet resistance, and §4.1's <c>R = 2·Rs</c> is the
    /// LOOP's.</b> The factor of two is the two planes in series in the loop — see
    /// <see cref="PdnMeshExtractor"/>'s own note at the site where it is stamped, and read it before
    /// putting a 2 anywhere near this property.</para>
    /// </summary>
    public double SheetResistanceOhmsPerSquare =>
        ThicknessMetres > 0 && ConductivitySm > 0 ? 1.0 / (ConductivitySm * ThicknessMetres) : 0.0;
}

/// <summary>A (net, coordinate) pair — what the board netlist or the <c>.kicad_pcb</c> knows that
/// the geometry does not. <c>BoardNetlistRecord</c> maps onto this directly.</summary>
/// <param name="Net">The net name, as that file spells it.</param>
/// <param name="X">DBU, on the artwork's own coordinate system.</param>
/// <param name="Y">DBU.</param>
public readonly record struct PdnNetPoint(string Net, long X, long Y);

/// <summary>
/// One galvanically-joined island of a rail's copper, across every layer it reaches.
/// </summary>
/// <param name="Index">0-based, in descending area order — so island 0 is the main body and the
/// report reads the way a user would say it.</param>
/// <param name="Copper">The island's geometry, per drawing layer.</param>
/// <param name="Bounds">Its bounding box, DBU.</param>
/// <param name="AreaSquareDbu">Total copper area summed over every layer.</param>
public sealed record PdnRegion(
    int Index,
    IReadOnlyList<(LayerKey Layer, Paths64 Paths)> Copper,
    Bbox Bounds,
    double AreaSquareDbu);

/// <summary>
/// The rail's copper and its reference's, each partitioned into islands, plus the sentence a report
/// prints.
/// </summary>
/// <param name="Power">The rail's own islands, largest first. Empty means the net names no copper —
/// which the extractor refuses rather than meshing nothing.</param>
/// <param name="Reference">The reference conductor's islands, largest first, after the reference
/// extent has been applied.</param>
/// <param name="IslandReport">The sentence — "this rail is one region" or "this rail is three
/// regions". Carried into <see cref="PdnProvenance.IslandReport"/> and onto every export.</param>
/// <param name="Diagnostics">Everything else worth saying.</param>
public sealed record PdnRailRegionSet(
    IReadOnlyList<PdnRegion> Power,
    IReadOnlyList<PdnRegion> Reference,
    string IslandReport,
    IReadOnlyList<string> Diagnostics);

/// <summary>The galvanic region walk — <see cref="DrcConnectivity"/> joined to a net name.</summary>
public static class PdnRailRegions
{
    /// <summary>
    /// Partitions <paramref name="layerRegions"/> into the rail's islands and the reference's.
    /// </summary>
    /// <param name="layerRegions">Per-layer unioned copper, DBU — built exactly as the DRC run
    /// builds it, so the two cannot disagree.</param>
    /// <param name="tech">Supplies the stackup that says which layers a via joins.</param>
    /// <param name="netPoints">What the board netlist knows. Only points naming
    /// <paramref name="railNet"/> or <paramref name="referenceNet"/> are read.</param>
    /// <param name="railNet">The power net's name, or null where the caller seeds by point instead.</param>
    /// <param name="referenceLayer">The reference return's drawing layer (§2.2, Q-8 — asked for,
    /// never inferred).</param>
    /// <param name="referenceNet">The reference net's name, or null to take the whole reference
    /// layer.</param>
    /// <param name="extraRailSeeds">Coordinates that are on the rail whatever the netlist says —
    /// the source and load anchors, which is how a rail with no net name at all is still found
    /// (§2.2's assisted Gerber path).</param>
    public static PdnRailRegionSet Walk(
        IReadOnlyDictionary<LayerKey, Paths64> layerRegions,
        Technology tech,
        IReadOnlyList<PdnNetPoint> netPoints,
        string? railNet,
        LayerKey referenceLayer,
        string? referenceNet,
        IReadOnlyList<(long X, long Y)> extraRailSeeds)
    {
        var diagnostics = new List<string>();
        var pieces = DrcConnectivity.Extract(layerRegions, tech);

        if (pieces.Count == 0)
            return new PdnRailRegionSet([], [], "No copper was found on any layer.", diagnostics);

        var railSeeds = new List<(long X, long Y)>();
        var refSeeds = new List<(long X, long Y)>();

        foreach (var p in netPoints)
        {
            if (railNet is { Length: > 0 } && string.Equals(p.Net, railNet, StringComparison.OrdinalIgnoreCase))
                railSeeds.Add((p.X, p.Y));
            else if (referenceNet is { Length: > 0 } && string.Equals(p.Net, referenceNet, StringComparison.OrdinalIgnoreCase))
                refSeeds.Add((p.X, p.Y));
        }

        railSeeds.AddRange(extraRailSeeds);

        // A seed point sits over the RETURN as well as over the rail — a pad is a coordinate and the
        // reference plane is usually under all of them. Seeding off it would make the reference an
        // island OF THE RAIL, which is a shorted board reported as an ordinary one.
        var railNets = NetsAt(pieces, railSeeds, anyLayer: true, exceptLayer: referenceLayer);

        // The reference is the copper on ITS OWN layer. Seeding it by net where the netlist names
        // one, and by "everything on that layer" where it does not — which is the ordinary case,
        // because a reference plane is usually the only thing on its layer and asking a user to name
        // its net twice buys nothing.
        var refNets = refSeeds.Count > 0
            ? NetsAt(pieces, refSeeds, anyLayer: false, onlyLayer: referenceLayer)
            : pieces.Where(p => p.Layer == referenceLayer).Select(p => p.Net).ToHashSet();

        var power = Islands(pieces, railNets, onlyLayer: null);
        var reference = Islands(pieces, refNets, onlyLayer: referenceLayer);

        string report = Describe("rail", power) + " " + Describe("reference", reference);

        if (power.Count > 1)
            diagnostics.Add(
                $"The rail's copper is {power.Count} galvanically separate regions. On imported " +
                "artwork the copper stops at every pad, so this is ordinary and not an error — but " +
                "nothing bridges them at DC until a series part says what does.");

        return new PdnRailRegionSet(power, reference, report, diagnostics);
    }

    private static string Describe(string what, IReadOnlyList<PdnRegion> islands) =>
        islands.Count switch
        {
            0 => $"The {what} has no copper.",
            1 => $"The {what} is one region.",
            _ => $"The {what} is {islands.Count} regions.",
        };

    /// <summary>The net indices of every piece containing one of <paramref name="seeds"/>.</summary>
    private static HashSet<int> NetsAt(
        IReadOnlyList<DrcNetPiece> pieces,
        IReadOnlyList<(long X, long Y)> seeds,
        bool anyLayer,
        LayerKey onlyLayer = default,
        LayerKey? exceptLayer = null)
    {
        var found = new HashSet<int>();
        foreach (var (x, y) in seeds)
            foreach (var piece in pieces)
            {
                if (exceptLayer is { } skip && piece.Layer == skip) continue;
                if (!anyLayer && piece.Layer != onlyLayer) continue;
                if (!piece.Bounds.Contains(x, y)) continue;
                if (Contains(piece.Paths, x, y)) found.Add(piece.Net);
            }
        return found;
    }

    /// <summary>
    /// Whether <paramref name="paths"/> covers (<paramref name="x"/>, <paramref name="y"/>), holes
    /// honoured.
    ///
    /// <para>Clipped against a 2 DBU square rather than a winding count, because a pad coordinate
    /// from a drill file lands ON a boundary as often as inside one — a via's own centre is the
    /// centre of the hole it drilled, which is a HOLE in the copper. A square straddling the
    /// boundary still meets the annulus; a winding test at the exact centre does not.</para>
    /// </summary>
    internal static bool Contains(Paths64 paths, long x, long y)
    {
        Paths64 probe = [[new Point64(x - 1, y - 1), new Point64(x + 1, y - 1),
                          new Point64(x + 1, y + 1), new Point64(x - 1, y + 1)]];
        return Clipper.BooleanOp(ClipType.Intersection, paths, probe, LayoutClipper.Rule).Count > 0;
    }

    /// <summary>Groups the pieces of the given nets into islands, largest first.</summary>
    private static List<PdnRegion> Islands(
        IReadOnlyList<DrcNetPiece> pieces, HashSet<int> nets, LayerKey? onlyLayer)
    {
        var byNet = new Dictionary<int, List<DrcNetPiece>>();
        var order = new List<int>();

        foreach (var piece in pieces)
        {
            if (!nets.Contains(piece.Net)) continue;
            if (onlyLayer is { } only && piece.Layer != only) continue;
            if (!byNet.TryGetValue(piece.Net, out var list))
            {
                byNet[piece.Net] = list = [];
                order.Add(piece.Net);
            }
            list.Add(piece);
        }

        var islands = new List<(double Area, Bbox Bounds, List<(LayerKey, Paths64)> Copper, int Net)>();

        foreach (int net in order)
        {
            var list = byNet[net];
            double area = 0;
            var bounds = Bbox.Empty;
            var copper = new List<(LayerKey, Paths64)>();

            foreach (var piece in list)
            {
                area += Math.Abs(Clipper.Area(piece.Paths));
                bounds = bounds.Union(piece.Bounds);
                copper.Add((piece.Layer, piece.Paths));
            }

            islands.Add((area, bounds, copper, net));
        }

        // Descending area, then by net index so two islands of identical area never swap between
        // runs — the determinism gate of §7 depends on it, and so do briefs 9 and 17.
        islands.Sort((a, b) =>
        {
            int byArea = b.Area.CompareTo(a.Area);
            return byArea != 0 ? byArea : a.Net.CompareTo(b.Net);
        });

        var result = new List<PdnRegion>(islands.Count);
        for (int i = 0; i < islands.Count; i++)
            result.Add(new PdnRegion(i, islands[i].Copper, islands[i].Bounds, islands[i].Area));
        return result;
    }
}
