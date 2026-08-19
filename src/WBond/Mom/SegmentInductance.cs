using System.Threading.Tasks;

namespace CircuitRF.WBond.Mom;

/// <summary>
/// <b>L</b>, the segment-basis partial-inductance matrix — N_s × N_s, real, symmetric, in henries,
/// and <b>frequency-independent</b>.
///
/// <code>
/// L[p,q] = Grover.Mutual(seg p, seg q)  +  Grover.Mutual(seg p, image of seg q)
/// </code>
///
/// <h3>This costs exactly what the wire-basis fill already costs</h3>
/// <para><see cref="InductanceMatrix.Block"/> walks every ordered filament pair of two wires and
/// <i>sums them down</i> into one number. This walks the identical pairs and simply does not sum down.
/// <b>The fill is not more expensive; it keeps more of its output.</b> That is the fact that makes the
/// whole distributed kernel affordable, and it is why the segment mesh is a finer
/// <see cref="WireMesh"/> rather than a different construction.</para>
///
/// <h3>The image term is ADDED</h3>
/// <para><see cref="Filament.Image"/> bakes the current reversal into the image filament's own
/// direction vector, so the minus sign of the textbook formula is carried by the geometry. The
/// electrostatic dual, <see cref="NodePotential"/>, <b>subtracts</b> instead — a charge has no
/// direction to carry and its image is negative. The two rules are opposite for exactly that reason,
/// and getting either wrong produces a finite, plausible, non-NaN wrong answer.</para>
///
/// <h3>Parallel by default, unlike the wire basis</h3>
/// <para><see cref="InductanceMatrix.Fill"/> defaults to serial because at the wire basis its whole
/// fill is milliseconds and the drag path refills incrementally. Here N_s is 24× larger and the fill is
/// the dominant one-time cost, so the default flips.</para>
/// </summary>
public static class SegmentInductance
{
    /// <summary>
    /// Fills <b>L</b> row-major. Upper triangle only, mirrored — the kernel is symmetric, so the
    /// matrix is.
    /// </summary>
    /// <param name="run">
    /// Cancellation and progress, or null. Ticked once per ROW, against a stage total of N_s rows.
    /// <b>The pace is not uniform and that is inherent</b>: row <i>p</i> fills the upper triangle from
    /// <i>p</i> to N_s, so the early rows are the expensive ones and the bar accelerates toward the
    /// end. Rows are still the right unit — they are what a reader can check against the unknown count
    /// the mesh report already gave them — and the alternative, counting matrix ENTRIES, buys a linear
    /// bar at the price of a counter reading "1,204,517 / 2,345,678" that means nothing to anyone.
    /// </param>
    public static double[] Fill(WireMomMesh mesh, bool? parallel = null, WBondRunControl? run = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        int n = mesh.SegmentCount;
        if ((long)n * n > int.MaxValue)
            throw new InvalidOperationException(
                $"A {n} x {n} inductance matrix does not fit in one array. The mesh ceiling exists to " +
                "prevent this and has been bypassed.");

        var l = new double[n * n];

        var segments = mesh.Segments;
        var images = mesh.SegmentImages;
        bool hasImages = mesh.HasImages;

        void Row(int p)
        {
            ref readonly var fp = ref segments[p];
            int row = p * n;
            for (int q = p; q < n; q++)
            {
                double v = p == q
                    ? Grover.SelfExternal(in fp)
                    : Grover.Mutual(in fp, in segments[q]);

                // ADDED. See the class remarks.
                if (hasImages) v += Grover.Mutual(in fp, in images[q]);

                l[row + q] = v;
                l[q * n + p] = v;
            }

            run?.TickStage();
        }

        run?.BeginStage("filling the inductance matrix", n);

        if (parallel ?? mesh.Settings.Parallel) Parallel.For(0, n, Row);
        else for (int p = 0; p < n; p++) Row(p);

        return l;
    }

    /// <summary>
    /// Sums a segment-basis matrix down to the wire basis:
    /// <c>L_wire[i,j] = Σ_{p ∈ wire i} Σ_{q ∈ wire j} L[p,q]</c>.
    ///
    /// <para>This is the <b>uniform-current</b> reduction, and it is what makes the identity gate
    /// against <see cref="InductanceMatrix"/> possible: a double line integral does not care how its
    /// domain is partitioned, so the sum must reproduce the wire-basis block exactly. Public because
    /// the gate is worth more than the encapsulation.</para>
    /// </summary>
    public static double[] SumToWireBasis(WireMomMesh mesh, double[] segmentMatrix)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(segmentMatrix);

        int n = mesh.SegmentCount;
        int w = mesh.WireCount;
        var wire = new double[w * w];

        for (int i = 0; i < w; i++)
        {
            int pStart = mesh.WireSegStart[i], pEnd = pStart + mesh.WireSegCount[i];
            for (int j = 0; j < w; j++)
            {
                int qStart = mesh.WireSegStart[j], qEnd = qStart + mesh.WireSegCount[j];
                double acc = 0.0;
                for (int p = pStart; p < pEnd; p++)
                {
                    int row = p * n;
                    for (int q = qStart; q < qEnd; q++) acc += segmentMatrix[row + q];
                }
                wire[i * w + j] = acc;
            }
        }

        return wire;
    }
}
