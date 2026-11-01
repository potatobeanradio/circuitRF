// CAP-3 — what the frequency-independent cores of a mesh WILL hold, counted before they are built.
//
// The production core layout (PlanarCoreLayout.Classes) holds a 4-byte translation-class index per
// ordered band pair, 112 bytes per class (seven primitives × two kernels) plus its 8-byte key, rows for
// the cells and bases a class cannot serve, and O(N) vectors. Until CAP-3 PlanarSystem.CoreBytes quoted
// P4's old triangle layout instead (≈ 6·N²), three to twelve times what the class layout allocates on
// real artwork — and every ceiling and refusal was sized on that quote.
//
// THE INDEX IS EXACT FROM THE MESH ALONE: the band is RampTopology's (c ≥ MinInner[a]), O(N) to size.
// THE CLASS COUNT IS A PROPERTY OF THE ARTWORK — 1.2× reuse on a short GaAs line, 29× on a 38.5 mm FR-4
// line at N = 11,201, 1,800× on a uniform patch — and no function of N and the cell count bounds it
// within a useful factor (RESOLVED.md §CAP-3 shows the seven P5 fixtures cannot be fitted by one). So a
// mesh in hand is COUNTED: every band pair is keyed exactly as PlanarFill.BuildCores keys it, and the
// distinct keys are the classes the build will integrate. That is one pass of PairClassifier.Key over
// the band — the build's own first pass, without the build's 12 bytes per pair of transients — and it
// is cached per mesh, so the mesher, the pre-solve check, the standards' pricing and the guards ask it
// once between them.
//
// With NO mesh (the unknown count a memory figure allows, before anything is meshed) the class term is a
// STATED assumption, not a bound: Estimate's reuse of 50. Reuse grows with the mesh — measured 15× at
// N = 3,731, 29× at 11,201, 48× at 14,781 and 460–500× at 29,087–55,831 on FR-4 lines, 1,800–5,400× on
// uniform patches — so at the sizes where a memory figure binds, 50 sits under every mesh measured.
// Every judgment of an actual mesh uses its own count.

using System.Runtime.CompilerServices;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// <b>CAP-3 — the bytes a mesh's cached cores hold, by part, and the class count the fill's
/// per-frequency tables are sized on.</b> <see cref="Count"/> is exact for a mesh (gated equal to
/// <see cref="PlanarFillCores.CoreBytes"/>); <see cref="Estimate"/> is the a-priori figure for an
/// unknown count with no mesh.
/// </summary>
/// <param name="Cells">The mesh's cell count, m — what the fill's cell potential P is over.</param>
/// <param name="BandPairs">Ordered cell pairs in P4's band — the 4-byte class index's length.</param>
/// <param name="Classes">Translation classes the band falls into.</param>
/// <param name="Kernels">Doubles per primitive: 2 at the shipped extraction order, 3 at Linear.</param>
/// <param name="OtherBytes">Everything else the cores hold: the band's row starts, the per-basis
/// moments, the classifier's own tables, the memo flags and the rows for cut cells and cut bases.</param>
/// <param name="Counted">True when <see cref="Classes"/> was counted on a mesh, false when it is
/// <see cref="Estimate"/>'s stated assumption.</param>
public readonly record struct PlanarCoreFootprint(int Cells, long BandPairs, long Classes, int Kernels,
                                                  long OtherBytes, bool Counted)
{
    /// <summary>The a-priori band, as a fraction of m²: measured 0.512 (a uniform patch) to 0.602
    /// (the GaAs line) on nine meshes; the band is c ≥ a − n_x, so it falls toward ½ as a mesh widens.</summary>
    public const double BandPerCellSquared = 0.61;

    /// <summary>The a-priori translation reuse (band pairs per class) — see the file header.</summary>
    public const double AssumedReuse = 50.0;

    /// <summary>Bytes of one class: the seven primitives (<see cref="Kernels"/> doubles each) and its key.</summary>
    public long BytesPerClass => 8L * (7 * Kernels) + 8L;

    /// <summary>The 4-byte class index over the band — exact on a mesh.</summary>
    public long IndexBytes => 4L * BandPairs;

    /// <summary>The class table: <see cref="Classes"/> × <see cref="BytesPerClass"/>.</summary>
    public long ClassBytes => Classes * BytesPerClass;

    /// <summary>Everything <see cref="PlanarFillCores.CoreBytes"/> reports for these cores.</summary>
    public long Bytes => IndexBytes + ClassBytes + OtherBytes;

    /// <summary>
    /// What one dense fill adds per frequency on top of the cores, per class: the seven complex remainder
    /// sums of the vector block (112 bytes) and the pulse remainder of the scalar block (16).
    /// </summary>
    public long RemainderBytes => 128L * Classes;

    /// <summary>
    /// <b>The a-priori footprint for <paramref name="n"/> unknowns on <paramref name="cellCount"/> cells</b>
    /// (derived from N at 1.95 bases per cell when 0): the band at <see cref="BandPerCellSquared"/>·m², the
    /// classes at <see cref="AssumedReuse"/>, the shipped extraction order, and O(N) vectors.
    /// </summary>
    public static PlanarCoreFootprint Estimate(int n, int cellCount = 0)
    {
        long m = cellCount > 0 ? cellCount : (long)Math.Round(n / PlanarSystem.BasesPerCell);
        long band = (long)Math.Ceiling(BandPerCellSquared * m * m);
        long classes = (long)Math.Ceiling(band / AssumedReuse);
        return new((int)m, band, classes, 2, 8L * (m + 1) + 8L * n + 5L * n, Counted: false);
    }

    private static readonly ConditionalWeakTable<PlanarMesh,
        System.Collections.Concurrent.ConcurrentDictionary<(PlanarExtractionOrder, double, double), PlanarCoreFootprint>> Cache = new();

    /// <summary>Meshes up to this many unknowns are counted; past it the band is 1.8·10⁹ pairs and more, and
    /// the estimate is stated instead (no mesh this size is solved dense on any machine this runs on).</summary>
    public const int CountedUpTo = 60_000;

    /// <summary>
    /// <b>The footprint a judgment of <paramref name="n"/> unknowns should use</b>: the mesh's own count when
    /// there is a mesh (up to <see cref="CountedUpTo"/> unknowns), the a-priori estimate otherwise.
    /// </summary>
    public static PlanarCoreFootprint For(PlanarMesh? mesh, int n, int cellCount, PlanarFillSettings? settings = null)
        => mesh is not null && n <= CountedUpTo
            ? Count(mesh, settings)
            : Estimate(n, cellCount > 0 ? cellCount : mesh?.Cells.Count ?? 0);

    /// <summary>
    /// <b>The exact footprint of <see cref="PlanarFill.BuildCores"/>' cores for this mesh</b> at
    /// <paramref name="settings"/> (null = the shipped defaults). Cached per mesh and per the three settings
    /// the count depends on (the extraction order and the two τ thresholds in the class key).
    /// </summary>
    public static PlanarCoreFootprint Count(PlanarMesh mesh, PlanarFillSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var st = settings ?? PlanarFillSettings.Default;
        var perMesh = Cache.GetValue(mesh, _ => new());
        return perMesh.GetOrAdd((st.Order, st.NearRatio, st.FarRatio), _ => PlanarFill.CountCores(mesh, st));
    }
}

public static partial class PlanarFill
{
    /// <summary>
    /// CAP-3 — <see cref="BuildCores"/>' allocation without building: the same topology, classifier and
    /// band, every band pair keyed exactly as the build keys it, the distinct keys counted. See
    /// PlanarCoreFootprint.cs.
    /// </summary>
    internal static PlanarCoreFootprint CountCores(PlanarMesh mesh, PlanarFillSettings? settings)
    {
        var st = settings ?? PlanarFillSettings.Default;
        int n = mesh.Bases.Count, m = mesh.Cells.Count;
        int kernels = st.Order >= PlanarExtractionOrder.Linear ? 3 : 2;

        var topo       = RampTopology.Build(mesh);
        var classifier = PairClassifier.Build(mesh);
        var memo       = MemoisedBases(mesh, topo, classifier);
        var ok         = classifier.Classifiable;

        long band = 0;
        for (int a = 0; a < m; a++) band += m - topo.MinInner[a];

        int classes = DistinctBandKeys(mesh, topo, classifier, st).Length;

        // Rows for what no class serves — CutRows' own arithmetic.
        static long Rows(int count, int rows, int kernels) => rows == 0 ? 0 : 4L * count + 8L * rows * count * kernels;
        int cutCells = 0;
        for (int c = 0; c < m; c++) if (!ok[c]) cutCells++;
        long cut = Rows(m, cutCells, kernels);
        foreach (var d in new[] { topo.X, topo.Y })
        {
            int rows = 0;
            for (int p = 0; p < d.Count; p++) if (!memo[d.Idx[p]]) rows++;
            cut += Rows(d.Count, rows, kernels);
        }

        long other = 8L * (m + 1)                       // BandStart
                   + 8L * n                             // VMoment
                   + classifier.Bytes + memo.Length     // ClassifierBytes
                   + cut;
        return new PlanarCoreFootprint(m, band, classes, kernels, other, Counted: true);
    }

    /// <summary>
    /// The translation-class keys of every classifiable band pair, distinct and ascending — the class ids
    /// <see cref="BuildCores"/> assigns by rank, and the count <see cref="CountCores"/> reports. Gathered per
    /// worker rather than from a key per pair, so it holds O(classes), not O(band).
    /// </summary>
    internal static long[] DistinctBandKeys(PlanarMesh mesh, RampTopology topo, PairClassifier classifier,
                                            PlanarFillSettings st)
    {
        int m = mesh.Cells.Count;
        var ok = classifier.Classifiable;
        // Chunks of outer cells through ForRowsLocal, so the work spends the run's one parallel budget. EMB-4
        // R-emb4-6: each WORKER accumulates into its own set for every chunk it takes and merges ONCE, when it
        // leaves the loop. Merging per chunk (256 merges, each a UnionWith of a set as large as the class count
        // under one lock) serialised the tail of the count — `check` sat on one core for 16+ minutes at 46k
        // unknowns. The keys are sorted at the end, so the merge order changes nothing.
        int chunks = Math.Min(m, 256);
        var distinct = new HashSet<long>();
        var gate = new object();
        ForRowsLocal(st, chunks, () => new HashSet<long>(), (k, local) =>
        {
            for (int a = (int)((long)m * k / chunks), end = (int)((long)m * (k + 1) / chunks); a < end; a++)
            {
                if (!ok[a]) continue;
                var ca = mesh.Cells[a];
                for (int c = topo.MinInner[a]; c < m; c++)
                    if (ok[c]) local.Add(classifier.Key(ca, mesh.Cells[c], st, out _));
            }
        }, local =>
        {
            lock (gate)
            {
                // The largest set absorbs the others, so the one merge under the lock is the smaller one.
                if (local.Count > distinct.Count) { local.UnionWith(distinct); distinct = local; }
                else distinct.UnionWith(local);
            }
        });
        var keys = distinct.ToArray();
        Array.Sort(keys);
        return keys;
    }
}
