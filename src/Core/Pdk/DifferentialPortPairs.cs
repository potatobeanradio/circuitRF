using System.Numerics;
using NumFlat;
using RfCore;

namespace CircuitRF.Core.Pdk;

/// <summary>One pair of ports that behave as a single differential port.</summary>
/// <param name="PortA">1-based port index, the lower of the two.</param>
/// <param name="PortB">1-based port index, the higher of the two.</param>
/// <param name="Residual">
/// How completely the two rows cancel: <c>max|Y[a,:] + Y[b,:]| / max|Y[a,:]|</c>. Zero is a perfect
/// differential pair; a number near 1 means the ports are unrelated. Reported so the evidence
/// travels with the answer.
/// </param>
public sealed record DifferentialPortPair(int PortA, int PortB, double Residual);

/// <summary>Every differential pair in a network, and the ports that are not in one.</summary>
/// <param name="Pairs">The pairs found, ordered by first port.</param>
/// <param name="UnpairedPorts">
/// 1-based ports left over, ascending. A port with no differential partner is one nothing bridges —
/// which is what an externally-connectable pin looks like.
/// </param>
/// <param name="WorstResidual">The least convincing accepted pair; NaN when none were accepted.</param>
public sealed record DifferentialPortScan(
    IReadOnlyList<DifferentialPortPair> Pairs,
    IReadOnlyList<int>                  UnpairedPorts,
    double                              WorstResidual);

/// <summary>What a pairing attempt concluded, and why.</summary>
/// <param name="Pairs">The pairs found, ordered by first port. Empty unless <paramref name="Paired"/>.</param>
/// <param name="Paired">True only when every candidate port was matched.</param>
/// <param name="Reason">Why, in words, for a user-facing report.</param>
/// <param name="WorstResidual">The least convincing pair's residual — the number to argue with.</param>
public sealed record DifferentialPortPairing(
    IReadOnlyList<DifferentialPortPair> Pairs,
    bool                                Paired,
    string                              Reason,
    double                              WorstResidual);

/// <summary>
/// Finds which of a network's ports are two halves of ONE differential port, by measuring the
/// network rather than by assuming an ordering convention.
///
/// <para><b>What this is for.</b> A network extracted from a physical structure exposes an opening
/// where each lumped component attaches. A component with two leads leaves TWO ports there — one per
/// pad — and the component bridges them. To rebuild the circuit, a caller has to know which port
/// goes with which, and getting it wrong produces a netlist that simulates perfectly and is a
/// different circuit.</para>
///
/// <para><b>Why it must be measured.</b> The obvious rule — pair them up in the order the file
/// lists them — is a convention, and a convention is exactly the kind of thing that is right until
/// it is not. The network states the answer itself: if two ports are one differential port, the
/// structure responds only to the difference between them, so a common-mode drive draws no current
/// and their two admittance rows are equal and opposite. That is a property of the physics, not of
/// anyone's port-numbering habit, and it is checkable per file.</para>
///
/// <para><b>Nothing here knows anything about any supplier, kit or part.</b> It takes a network and
/// a range of ports and reports what the numbers say.</para>
///
/// <para><b>It refuses rather than guesses.</b> If the ports do not cancel cleanly, or a port's best
/// partner does not choose it back, the result is unpaired with a reason. A caller that wanted a
/// pairing gets nothing instead of a plausible one.</para>
/// </summary>
public static class DifferentialPortPairs
{
    /// <summary>
    /// A pair is accepted when its rows cancel to better than this, relative to the row's own
    /// magnitude.
    ///
    /// <para>The gap being resolved is enormous — a genuine pair cancels to around 1e-6 or better at
    /// the lowest frequency, and two unrelated ports sit near 1 (their rows simply add). Anything in
    /// between is not a marginal pair, it is a sign that the assumption does not hold, so the
    /// threshold sits far from both and its exact value is not load-bearing.</para>
    /// </summary>
    public const double DefaultTolerance = 1e-3;

    /// <summary>
    /// Finds every differential pair in the whole network, and reports what is left over.
    ///
    /// <para><b>This needs no port range and no labels.</b> A port that nothing bridges has no
    /// differential partner, so the leftovers ARE the externally-connectable pins — the split falls
    /// out of the measurement instead of having to be supplied. That matters because a file need not
    /// label its ports at all, and many do not; asking the network is available whenever the data
    /// is.</para>
    ///
    /// <para>Only mutually-best pairs below <paramref name="tolerance"/> are accepted, so a port
    /// that merely resembles half of a pair is left unpaired rather than matched.</para>
    /// </summary>
    public static DifferentialPortScan Scan(SNP network, double tolerance = DefaultTolerance)
    {
        ArgumentNullException.ThrowIfNull(network);

        int n = network.IsEmpty ? 0 : network.Ports;
        if (n < 2) return new([], Enumerable.Range(1, Math.Max(n, 0)).ToArray(), double.NaN);

        double[,] residual = ResidualMatrix(AdmittanceAtLowestFrequency(network), 0, n);

        var pairs = new List<DifferentialPortPair>();
        var taken = new bool[n];
        double worst = double.NaN;

        for (int i = 0; i < n; i++)
        {
            if (taken[i]) continue;
            int j = BestPartner(residual, n, i, taken);
            if (j < 0 || !(residual[i, j] <= tolerance)) continue;

            // Mutual, or not at all: a port whose best partner prefers someone else is not half of
            // a pair, and accepting it would build a circuit out of a coincidence.
            if (BestPartner(residual, n, j, taken) != i) continue;

            taken[i] = taken[j] = true;
            worst = double.IsNaN(worst) ? residual[i, j] : Math.Max(worst, residual[i, j]);
            pairs.Add(new DifferentialPortPair(Math.Min(i, j) + 1, Math.Max(i, j) + 1, residual[i, j]));
        }

        pairs.Sort((a, b) => a.PortA.CompareTo(b.PortA));

        var loose = new List<int>();
        for (int i = 0; i < n; i++) if (!taken[i]) loose.Add(i + 1);

        return new DifferentialPortScan(pairs, loose, worst);
    }

    /// <summary>
    /// Pairs up ports <paramref name="firstPort"/>..<paramref name="lastPort"/> (1-based, inclusive).
    /// </summary>
    /// <param name="network">The network, in any representation; converted to Y internally.</param>
    /// <param name="firstPort">First port to consider, 1-based.</param>
    /// <param name="lastPort">Last port to consider, 1-based, inclusive.</param>
    /// <param name="tolerance">Override for <see cref="DefaultTolerance"/>.</param>
    public static DifferentialPortPairing Find(SNP network, int firstPort, int lastPort,
                                               double tolerance = DefaultTolerance)
    {
        ArgumentNullException.ThrowIfNull(network);

        if (network.IsEmpty)
            return Unpaired("the network carries no frequency points.");

        int n = network.Ports;
        if (firstPort < 1 || lastPort > n || lastPort < firstPort)
            return Unpaired($"ports {firstPort}-{lastPort} are not inside a {n}-port network.");

        int count = lastPort - firstPort + 1;
        if (count < 2)
            return Unpaired("a differential pair needs at least two ports.");
        if (count % 2 != 0)
            return Unpaired($"{count} ports cannot pair up — an odd count leaves one unmatched.");

        double[,] residual = ResidualMatrix(AdmittanceAtLowestFrequency(network),
                                            firstPort - 1, count);

        var partner = new int[count];
        for (int i = 0; i < count; i++) partner[i] = BestPartner(residual, count, i, null);

        // MUTUAL choice, not just a good one. A port whose best partner prefers someone else is not
        // half of a pair — it is a port that happens to resemble one, and accepting it would build a
        // circuit out of a coincidence.
        var pairs = new List<DifferentialPortPair>();
        double worst = 0;
        var taken = new bool[count];

        for (int i = 0; i < count; i++)
        {
            if (taken[i]) continue;
            int j = partner[i];

            if (partner[j] != i)
                return Unpaired($"port {firstPort + i} pairs best with port {firstPort + j}, " +
                                $"but port {firstPort + j} prefers port {firstPort + partner[j]}; " +
                                "the ports do not fall into pairs.");

            double r = residual[i, j];
            if (!(r <= tolerance))
                return Unpaired($"the closest pairing of ports {firstPort + i} and {firstPort + j} " +
                                $"leaves a residual of {r:G3}, above the {tolerance:G3} a genuine " +
                                "differential pair shows. These ports are not two halves of one.",
                                r);

            taken[i] = taken[j] = true;
            worst = Math.Max(worst, r);
            pairs.Add(new DifferentialPortPair(firstPort + Math.Min(i, j),
                                               firstPort + Math.Max(i, j), r));
        }

        pairs.Sort((a, b) => a.PortA.CompareTo(b.PortA));
        return new DifferentialPortPairing(
            pairs, true,
            $"{pairs.Count} differential pair(s); worst cancellation {worst:G3} against a " +
            $"tolerance of {tolerance:G3}.",
            worst);
    }

    /// <summary>
    /// Residual for every candidate pairing among <paramref name="count"/> ports starting at the
    /// 0-based <paramref name="offset"/>. Measured once and reused: the exhaustive comparison is
    /// cheap and it removes the question of which order to try them in.
    /// </summary>
    private static double[,] ResidualMatrix(Mat<Complex> y, int offset, int count)
    {
        var r = new double[count, count];
        for (int i = 0; i < count; i++)
        for (int j = 0; j < count; j++)
            r[i, j] = i == j ? double.PositiveInfinity
                             : CancellationResidual(y, offset + i, offset + j);
        return r;
    }

    /// <summary>The best-cancelling partner for <paramref name="i"/>, skipping ports already taken.</summary>
    private static int BestPartner(double[,] residual, int count, int i, bool[]? taken)
    {
        int best = -1;
        for (int j = 0; j < count; j++)
        {
            if (j == i || (taken is not null && taken[j])) continue;
            if (best < 0 || residual[i, j] < residual[i, best]) best = j;
        }
        return best;
    }

    /// <summary>
    /// How completely two admittance rows cancel, relative to the size of the row itself.
    ///
    /// <para>Relative, not absolute: an admittance row's magnitude depends on the structure and on
    /// the units, so an absolute residual would mean something different for every file.</para>
    /// </summary>
    private static double CancellationResidual(Mat<Complex> y, int rowA, int rowB)
    {
        double sum = 0, scale = 0;
        for (int c = 0; c < y.ColCount; c++)
        {
            sum   = Math.Max(sum,   (y[rowA, c] + y[rowB, c]).Magnitude);
            scale = Math.Max(scale, y[rowA, c].Magnitude);
        }
        // A row of zeros is a disconnected port; it "cancels" with anything and means nothing.
        return scale > 0 ? sum / scale : double.PositiveInfinity;
    }

    /// <summary>
    /// The admittance matrix at the lowest frequency in the file.
    ///
    /// <para>The lowest point on purpose: cancellation is cleanest there and degrades with frequency
    /// as solver discretisation error grows, so the lowest point is where the distinction between a
    /// pair and a non-pair is widest.</para>
    /// </summary>
    private static Mat<Complex> AdmittanceAtLowestFrequency(SNP network)
    {
        int lowest = 0;
        for (int i = 1; i < network.FrequencyCount; i++)
            if (network.Frequencies[i] < network.Frequencies[lowest]) lowest = i;

        return network.Type switch
        {
            MatrixType.Y => network[lowest],
            MatrixType.Z => RFNetwork.ZToY(network[lowest]),
            _            => RFNetwork.SToY(network[lowest], network.Z0),
        };
    }

    private static DifferentialPortPairing Unpaired(string reason, double worst = double.NaN) =>
        new([], false, reason, worst);
}
