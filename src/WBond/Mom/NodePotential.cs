using System.Threading.Tasks;

namespace CircuitRF.WBond.Mom;

/// <summary>
/// <b>P</b>, the node-basis coefficient-of-potential matrix — N_n × N_n, real, symmetric positive
/// definite, in inverse farads, and <b>frequency-independent</b>. The electrostatic dual of
/// <see cref="SegmentInductance"/>, filled from the same geometry with the same kernels.
///
/// <h3>The charge cell is a HALF-cell, and it is the only subtle piece of geometry in kernel W1</h3>
/// <para>Ruehli's PEEC pairing puts current on segments and charge on <b>nodes</b>, where node
/// <i>n</i>'s charge cell is the union of the <b>halves</b> of its incident segments nearest to it —
/// so an interior node's cell is the second half of segment <i>k−1</i> plus the first half of segment
/// <i>k</i>, and a wire-end node's cell is the outer half of its single incident segment.
/// <see cref="WireMomMesh"/> builds those halves once, two per segment, and hands them over as a CSR
/// list per node.</para>
///
/// <code>
/// P[m,n] = 1/(4πε · l_m · l_n) · Σ_{p ∈ cell m} Σ_{q ∈ cell n} [ K(p,q) − K(p, Image(q)) ]
/// </code>
///
/// <h3>ε carries the overmold, and it is the ONLY thing ε_r changes in this kernel</h3>
/// <para><c>ε = ε₀ · </c><see cref="WBondDesign.OvermoldEr"/>, read from the mesh's own design. The
/// kernel is quasi-static and the encapsulant is non-magnetic, so <b>L</b>, <b>D(ω)</b> and the
/// internal impedance are untouched and this matrix is divided by ε_r — which is exactly what
/// <see cref="PotentialCoefficients.Fill"/> does on the wire basis, so the <c>Bᵀ P B</c> identity
/// gate holds in any medium as long as both sides are filled in the same one.</para>
///
/// <h3>The image term is SUBTRACTED — the second sign rule</h3>
/// <para><see cref="SegmentInductance"/> <b>adds</b> its image term because
/// <see cref="Filament.Image"/> carries the current reversal in its direction. A charge has no
/// direction, and its image in a ground plane is <b>negative</b>, so the minus has to be written. The
/// two rules resolve in opposite directions for exactly that reason, and this is copied verbatim from
/// <see cref="PotentialCoefficients.Block"/>, which is the validated wire-basis original.</para>
///
/// <h3>Cost</h3>
/// <para>At most 4 sub-pairs per entry, ×2 for images = 8 <see cref="PotentialCoefficients.Kernel"/>
/// calls, against 2 <see cref="Grover.Mutual"/> calls per entry in <b>L</b>. The far branch is a single
/// reciprocal square root and the near branch is mostly <see cref="Grover.ParallelScalarKernel"/>, so
/// the charge fill lands at a fraction of the inductance fill rather than 4× it — the measured ratio is
/// in <c>RESOLVED.md</c>.</para>
/// </summary>
public static class NodePotential
{
    /// <summary>
    /// Fills <b>P</b> row-major. Upper triangle only, mirrored.
    /// </summary>
    /// <param name="farThresholdFactor">
    /// Override for <see cref="WireMomSettings.FarThresholdFactor"/>. Pass
    /// <see cref="double.PositiveInfinity"/> to force the accurate kernel on every pair — which is what
    /// the identity gate against <see cref="PotentialCoefficients"/> does on both sides, so the near/far
    /// split cannot be what makes them differ.
    /// </param>
    /// <param name="run">
    /// Cancellation and progress, or null. Ticked once per node ROW — see
    /// <see cref="SegmentInductance.Fill"/> for why rows and not entries, and why the pace is not
    /// uniform.
    /// </param>
    /// <param name="relativePermittivity">
    /// Override for the design's own <see cref="WBondDesign.OvermoldEr"/>. Null takes the mesh's
    /// design, which is what every production caller does; the parameter exists so a gate can fill the
    /// same geometry in two media and compare.
    /// </param>
    public static double[] Fill(WireMomMesh mesh, bool? parallel = null, double? farThresholdFactor = null,
                                WBondRunControl? run = null, double? relativePermittivity = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        int n = mesh.NodeCount;
        if ((long)n * n > int.MaxValue)
            throw new InvalidOperationException(
                $"A {n} x {n} potential matrix does not fit in one array. The mesh ceiling exists to " +
                "prevent this and has been bypassed.");

        double far = farThresholdFactor ?? mesh.Settings.FarThresholdFactor;
        var p = new double[n * n];

        var halves = mesh.Halves;
        var images = mesh.HalfImages;
        bool hasImages = mesh.HasImages;
        var cellStart = mesh.NodeCellStart;
        var cellIndex = mesh.NodeCellIndex;
        var cellLength = mesh.NodeCellLength;

        // THE MEDIUM. ε = ε₀·ε_r, so the overmold divides every entry of P and multiplies every
        // capacitance by ε_r. Folded into the one scale factor that was already here — there is no
        // second place in this fill where a permittivity could be applied or forgotten.
        double er = relativePermittivity ?? mesh.Design.OvermoldEr;
        if (!(er >= 1.0) || !double.IsFinite(er))
            throw new ArgumentOutOfRangeException(nameof(relativePermittivity),
                $"The relative permittivity is {er}; it must be at least 1.");

        double scale = 1.0 / (4.0 * Math.PI * PotentialCoefficients.Epsilon0 * er);

        void Row(int m)
        {
            int row = m * n;
            for (int q = m; q < n; q++)
            {
                double acc = 0.0;
                for (int ci = cellStart[m]; ci < cellStart[m + 1]; ci++)
                {
                    ref readonly var fp = ref halves[cellIndex[ci]];
                    for (int cj = cellStart[q]; cj < cellStart[q + 1]; cj++)
                    {
                        int j = cellIndex[cj];
                        acc += PotentialCoefficients.Kernel(in fp, in halves[j], far);

                        // THE SIGN THAT FLIPS. The image CHARGE is negative.
                        if (hasImages) acc -= PotentialCoefficients.Kernel(in fp, in images[j], far);
                    }
                }

                double v = scale * acc / (cellLength[m] * cellLength[q]);
                p[row + q] = v;
                p[q * n + m] = v;
            }

            run?.TickStage();
        }

        run?.BeginStage("filling the potential matrix", n);

        if (parallel ?? mesh.Settings.Parallel) Parallel.For(0, n, Row);
        else for (int m = 0; m < n; m++) Row(m);

        return p;
    }

    /// <summary>
    /// <c>B</c>, the node-cell → wire reduction used by the identity gate: <c>B[m, i] = l_m / l_i</c>
    /// for cell <i>m</i> on wire <i>i</i>, zero elsewhere. Row-major, N_n × N_wires.
    ///
    /// <para>It is the charge dual of <see cref="SegmentInductance.SumToWireBasis"/>: uniform charge
    /// per unit length along a wire puts a share <c>l_m / l_i</c> of the wire's charge on cell
    /// <i>m</i>, so <c>Bᵀ P B</c> must reproduce <see cref="PotentialCoefficients"/>' own wire-basis
    /// matrix.</para>
    /// </summary>
    public static double[] WireReduction(WireMomMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        int nn = mesh.NodeCount;
        int w = mesh.WireCount;
        var b = new double[nn * w];

        var wireLength = new double[w];
        for (int i = 0; i < w; i++)
        {
            int start = mesh.WireNodeStart[i], end = start + mesh.WireSegCount[i] + 1;
            for (int n = start; n < end; n++) wireLength[i] += mesh.NodeCellLength[n];
        }

        for (int i = 0; i < w; i++)
        {
            int start = mesh.WireNodeStart[i], end = start + mesh.WireSegCount[i] + 1;
            for (int n = start; n < end; n++) b[n * w + i] = mesh.NodeCellLength[n] / wireLength[i];
        }

        return b;
    }
}
