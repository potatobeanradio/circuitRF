using System.Threading.Tasks;

namespace CircuitRF.WBond;

/// <summary>
/// The wire-basis <b>coefficient-of-potential</b> matrix <b>P</b> (wbond.md §3.7) — the electrostatic
/// dual of <see cref="InductanceMatrix"/>, filled from the same filaments, the same images and the
/// same pair loop.
///
/// <h3>The model</h3>
/// <para>One charge basis function per <b>wire</b>: uniform charge per unit length along the wire.
/// That is the standard single-basis-function-per-conductor electrostatic model — the same
/// approximation that produces the textbook wire-over-plane capacitance — and it is an
/// approximation with a name, not an exact solve. With <c>λ_j = Q_j / l_j</c> and the potential of
/// wire <i>i</i> taken as its own length average:</para>
/// <code>
/// P_ij = 1/(4πε · l_i · l_j) · Σ_p Σ_q [ K(p, q) − K(p, Image(q)) ]
/// K(p, q) = ∫∫ ds ds′ / |r(s) − r(s′)|
/// </code>
///
/// <h3>ε is ε₀·ε_r, and ε_r is the overmold</h3>
/// <para><c>ε = ε₀ · </c><see cref="WBondDesign.OvermoldEr"/>, taken from the mesh's own design at
/// <see cref="Fill"/> time. A non-magnetic encapsulant leaves the inductance alone and divides this
/// whole matrix by ε_r, so every capacitance rises by exactly that factor. It is applied ONCE, in
/// <see cref="Fill"/>, rather than inside <see cref="Block"/> or <see cref="Kernel"/>: the kernel is
/// geometry and the permittivity is the medium, and keeping them apart is what lets the near/far
/// gates and the <c>Bᵀ P B</c> identity gate compare kernels without a material in the way.</para>
///
/// <h3>The image sign FLIPS, and this is the second sign rule</h3>
/// <para><see cref="InductanceMatrix.Block"/> <b>adds</b> its image term, because
/// <see cref="Filament.Image"/> bakes the current reversal into the returned direction vector. A
/// charge has no direction to carry, and its image in a ground plane is <b>negative</b> — so this
/// block <b>subtracts</b>. The two rules resolve in opposite directions for exactly that reason.</para>
///
/// <para><b>An image-sign error here is finite and plausible, not a NaN</b>, which is why it has a
/// test of its own that flips the sign and confirms the oracle fails (gate C2). The independent tell
/// is monotonicity: raising a wire lowers its capacitance, and a sign error inverts that.</para>
///
/// <h3>Cost</h3>
/// <para>The electrostatic pair loop is <i>cheaper per pair</i> than the inductance one — no
/// <c>cos ε</c>, no four <c>Atanh</c>, no four <c>Atan2</c>, just a reciprocal square root. With the
/// near/far split of <see cref="FarThresholdFactor"/> the blended fill is a fraction of the
/// inductance fill's cost; <c>CapacitanceCostTests</c>' own C4 gate is what holds that.</para>
/// </summary>
public sealed class PotentialCoefficients
{
    private readonly double[] _p;

    private PotentialCoefficients(double[] p, int n)
    {
        _p = p;
        Order = n;
    }

    /// <summary>Vacuum permittivity, F/m. CODATA 2022.</summary>
    public const double Epsilon0 = 8.8541878188e-12;

    /// <summary>
    /// The axis separation, in units of the longer filament's length, above which the kernel is
    /// evaluated centre-to-centre rather than by quadrature.
    ///
    /// <para><b>Measured, not guessed (gate C3), and recorded here the way
    /// <see cref="Grover.ParallelEpsilon"/> records its own.</b> Sweeping the threshold against an
    /// all-near reference on a 60-wire / 6-array ball-bond design, the worst array-basis
    /// <c>C_arr</c> error falls monotonically: <b>3.07 % at 1, 0.712 % at 2, 0.172 % at 3,
    /// 0.103 % at 3.25, 0.0706 % at 3.5, 0.0095 % at 4</b> and below 1e-5 % from 5. <b>3.5 is the
    /// smallest value inside the 0.1 % target</b> and is the shipped one; the brief's proposed 3
    /// misses it, at 0.17 %. On a two-wire array every threshold in that range is exact to the last
    /// bit, because 4 mil-pitch wires 100 mil long are never far from each other — which is why the
    /// threshold has to be measured on a design with widely separated arrays in it.</para>
    ///
    /// <para>The cost of the extra half is nothing: 3.5 fills the 600-wire reference in the same
    /// ~10 ms as 3.0 (Release, parallel). <b>Do not raise it to a "safe" 10 either.</b> The far
    /// kernel is what makes the whole capacitance fill 0.06–0.08 × the inductance fill, and the cost
    /// argument for capacitance rests on most pairs taking it.</para>
    /// </summary>
    public const double FarThresholdFactor = 3.5;

    /// <summary>N, the number of wires.</summary>
    public int Order { get; }

    /// <summary>P[i,j] in inverse farads. Symmetric positive definite.</summary>
    public double this[int i, int j] => _p[i * Order + j];

    /// <summary>The backing row-major store, for the linear-algebra layer.</summary>
    public double[] Values => _p;

    /// <summary>
    /// One wire-pair block of <b>P</b>, direct minus image, normalised by both wire lengths.
    ///
    /// <para><b>In vacuum</b> — this is the geometric kernel. The medium's ε_r is divided out by
    /// <see cref="Fill"/>, which is the only place it is applied.</para>
    /// </summary>
    /// <param name="farThresholdFactor">
    /// Override for <see cref="FarThresholdFactor"/>. Exists for gate C3's sweep and for nothing else;
    /// pass <c>double.PositiveInfinity</c> to force the accurate kernel on every pair.
    /// </param>
    public static double Block(WireMesh mesh, double[] wireLength, int wi, int wj,
                               double farThresholdFactor = FarThresholdFactor)
    {
        // CANONICAL ORDER, for the same reason InductanceMatrix.Block has one: the double sum is not
        // bit-symmetric, and Fill computes the upper triangle. The image half is symmetric under the
        // swap too — mirroring through z = 0 is an isometry, so |r_p − mirror(r_q)| equals
        // |mirror(r_p) − r_q|.
        if (wi > wj) (wi, wj) = (wj, wi);

        var filaments = mesh.Filaments;
        var images = mesh.Images;
        bool hasImages = mesh.HasImages;

        int pStart = mesh.WireStart[wi], pEnd = pStart + mesh.WireLength[wi];
        int qStart = mesh.WireStart[wj], qEnd = qStart + mesh.WireLength[wj];

        double acc = 0.0;
        for (int p = pStart; p < pEnd; p++)
        {
            ref readonly var fp = ref filaments[p];
            for (int q = qStart; q < qEnd; q++)
            {
                acc += Kernel(in fp, in filaments[q], farThresholdFactor);

                // THE SIGN THAT FLIPS. The image CHARGE is negative; Filament.Image() carries a
                // current reversal that means nothing here, so the minus has to be written.
                if (hasImages)
                    acc -= Kernel(in fp, in images[q], farThresholdFactor);
            }
        }

        return acc / (4.0 * Math.PI * Epsilon0 * wireLength[wi] * wireLength[wj]);
    }

    /// <summary>Each wire's developed length in metres, summed from the mesh's own filaments.</summary>
    public static double[] WireLengths(WireMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        var lengths = new double[mesh.WireCount];
        for (int w = 0; w < lengths.Length; w++)
        {
            double total = 0.0;
            int start = mesh.WireStart[w], end = start + mesh.WireLength[w];
            for (int f = start; f < end; f++) total += mesh.Filaments[f].Length;
            lengths[w] = total;
        }
        return lengths;
    }

    /// <summary>
    /// Assembles the full matrix. Only the upper triangle is computed; <b>P</b> is symmetric because
    /// the kernel is.
    /// </summary>
    /// <param name="relativePermittivity">
    /// Override for the design's own <see cref="WBondDesign.OvermoldEr"/> — the medium the wires sit
    /// in. Null takes the mesh's design, which is the only thing any production caller should do; the
    /// parameter exists so a gate can fill the same geometry in two media and compare.
    /// </param>
    public static PotentialCoefficients Fill(WireMesh mesh, bool parallel = false,
                                             double farThresholdFactor = FarThresholdFactor,
                                             double? relativePermittivity = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        int n = mesh.WireCount;
        var lengths = WireLengths(mesh);
        var p = new double[n * n];

        // THE MEDIUM, applied once. P is inversely proportional to ε, so an overmold of ε_r divides
        // every entry by ε_r and multiplies every capacitance by it. Refused below 1 by
        // WBondDesign.Validate, which WireMesh.Build has already run — this reads a checked value.
        double er = relativePermittivity ?? mesh.Design.OvermoldEr;
        if (!(er >= 1.0) || !double.IsFinite(er))
            throw new ArgumentOutOfRangeException(nameof(relativePermittivity),
                $"The relative permittivity is {er}; it must be at least 1.");

        if (parallel)
        {
            Parallel.For(0, n, wi =>
            {
                for (int wj = wi; wj < n; wj++)
                {
                    double v = Block(mesh, lengths, wi, wj, farThresholdFactor) / er;
                    p[wi * n + wj] = v;
                    p[wj * n + wi] = v;
                }
            });
        }
        else
        {
            for (int wi = 0; wi < n; wi++)
            {
                for (int wj = wi; wj < n; wj++)
                {
                    double v = Block(mesh, lengths, wi, wj, farThresholdFactor) / er;
                    p[wi * n + wj] = v;
                    p[wj * n + wi] = v;
                }
            }
        }

        return new PotentialCoefficients(p, n);
    }

    // ---------------------------------------------------------------- the kernel

    /// <summary>
    /// <c>K(p, q) = ∫∫ ds ds′ / R</c> over two filaments, in metres. Positive, and independent of
    /// either filament's traversal direction — a charge has no direction.
    /// </summary>
    public static double Kernel(in Filament p, in Filament q, double farThresholdFactor = FarThresholdFactor)
    {
        // Centre-to-centre first: the far test needs the distance and so does the far kernel, so one
        // square root serves both.
        double cx = (q.Ax + 0.5 * q.Ux * q.Length) - (p.Ax + 0.5 * p.Ux * p.Length);
        double cy = (q.Ay + 0.5 * q.Uy * q.Length) - (p.Ay + 0.5 * p.Uy * p.Length);
        double cz = (q.Az + 0.5 * q.Uz * q.Length) - (p.Az + 0.5 * p.Uz * p.Length);
        double dSq = cx * cx + cy * cy + cz * cz;

        double reach = farThresholdFactor * Math.Max(p.Length, q.Length);
        if (dSq > reach * reach)
            return p.Length * q.Length / Math.Sqrt(dSq);

        // NEAR. Parallel filaments — which is most of a bond-wire array, and every self pair — have
        // the closed form for free; anything else takes the quadrature.
        double cosEps = p.Ux * q.Ux + p.Uy * q.Uy + p.Uz * q.Uz;
        if (cosEps > 1.0) cosEps = 1.0;
        else if (cosEps < -1.0) cosEps = -1.0;

        double sinSq = 1.0 - cosEps * cosEps;
        if (sinSq <= Grover.ParallelEpsilon * Grover.ParallelEpsilon)
            return Grover.ParallelScalarKernel(in p, in q);

        return GaussKernel(in p, in q);
    }

    /// <summary>Gauss-Legendre nodes on [−1, 1], 4-point.</summary>
    private static readonly double[] GaussNodes =
        [-0.8611363115940526, -0.3399810435848563, 0.3399810435848563, 0.8611363115940526];

    /// <summary>Weights index-parallel to <see cref="GaussNodes"/>.</summary>
    private static readonly double[] GaussWeights =
        [0.3478548451374538, 0.6521451548625461, 0.6521451548625461, 0.3478548451374538];

    /// <summary>
    /// The near kernel for non-parallel filaments: a 4×4 tensor-product Gauss-Legendre rule with the
    /// <b>same GMD floor</b> the inductance path applies.
    ///
    /// <para><b>The floor is physics, not a numerical guard</b> (see
    /// <see cref="Grover.MinimumSeparation"/>). Two consecutive filaments of one wire share an
    /// endpoint, so their axes intersect and the integrand is singular there; the physically correct
    /// separation is not zero but the cross-section's GMD, <c>√(a_p·a_q)</c>. Flooring <i>R</i> at
    /// that value is the same statement applied point by point.</para>
    /// </summary>
    private static double GaussKernel(in Filament p, in Filament q)
    {
        double dMin = Grover.MinimumSeparation(in p, in q);
        double halfP = 0.5 * p.Length, halfQ = 0.5 * q.Length;

        double px0 = p.Ax + halfP * p.Ux, py0 = p.Ay + halfP * p.Uy, pz0 = p.Az + halfP * p.Uz;
        double qx0 = q.Ax + halfQ * q.Ux, qy0 = q.Ay + halfQ * q.Uy, qz0 = q.Az + halfQ * q.Uz;

        double acc = 0.0;
        for (int a = 0; a < GaussNodes.Length; a++)
        {
            double sa = halfP * GaussNodes[a];
            double ax = px0 + sa * p.Ux, ay = py0 + sa * p.Uy, az = pz0 + sa * p.Uz;
            double wa = GaussWeights[a];

            for (int b = 0; b < GaussNodes.Length; b++)
            {
                double sb = halfQ * GaussNodes[b];
                double dx = (qx0 + sb * q.Ux) - ax;
                double dy = (qy0 + sb * q.Uy) - ay;
                double dz = (qz0 + sb * q.Uz) - az;

                double r = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (r < dMin) r = dMin;

                acc += wa * GaussWeights[b] / r;
            }
        }

        return acc * halfP * halfQ;
    }
}
