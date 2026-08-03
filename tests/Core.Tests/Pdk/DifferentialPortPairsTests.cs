using System.Numerics;
using CircuitRF.Core.Pdk;
using NumFlat;
using RfCore;
using Xunit;

namespace CircuitRF.Core.Tests.Pdk;

/// <summary>
/// A network extracted from a physical structure leaves two ports at each place a two-lead component
/// attaches. Which port goes with which decides what circuit gets rebuilt — and the wrong answer
/// simulates perfectly while being a different circuit, so these tests care as much about the
/// refusals as the answers.
///
/// <para>The networks here are BUILT, not loaded: each is assembled from a known admittance matrix
/// so the right answer is known independently of the code under test. A fixture would only show that
/// the detector agrees with whatever produced the fixture.</para>
/// </summary>
public class DifferentialPortPairsTests
{
    /// <summary>
    /// Builds an N-port whose ports <paramref name="pairs"/> are genuine differential pairs.
    ///
    /// <para>Construction is the definition itself: a component bridging ports (a,b) contributes
    /// +g to (a,a) and (b,b) and −g to (a,b) and (b,a), which makes rows a and b equal and opposite
    /// in every column it touches. Externals get their own shunt admittance and a little coupling,
    /// so they are not accidentally pairable.</para>
    /// </summary>
    private static SNP BuildNetwork(int ports, (int a, int b)[] pairs, int externals)
    {
        var y = new Mat<Complex>(ports, ports);

        for (int e = 0; e < externals; e++)
            y[e, e] = new Complex(0.02 + 0.001 * e, 0);

        // Couple the externals to each other so their rows are populated and distinct.
        for (int i = 0; i < externals; i++)
        for (int j = i + 1; j < externals; j++)
        {
            var c = new Complex(0.003 * (i + 1) * (j + 1), 0.0007);
            y[i, j] -= c; y[j, i] -= c; y[i, i] += c; y[j, j] += c;
        }

        // Each bridged pair, plus a link from one of its legs to an external so the pair's rows are
        // not merely two isolated 2x2 blocks.
        int k = 0;
        foreach (var (a, b) in pairs)
        {
            int ia = a - 1, ib = b - 1;
            var g = new Complex(0.01 * (k + 1), 0.002 * (k + 1));
            y[ia, ia] += g; y[ib, ib] += g; y[ia, ib] -= g; y[ib, ia] -= g;

            if (externals > 0)
            {
                // Coupled to an external DIFFERENTIALLY — a transadmittance, which is what a
                // balun-fed structure actually presents. The external responds to (Va − Vb), and
                // the pair responds to the external with equal and opposite currents.
                //
                // It has to be built this way or the test is meaningless: an ordinary conductance
                // from one leg to an external is a COMMON-MODE path, which destroys the
                // antisymmetry — and would also mean the two ports were never one differential
                // port. Getting this wrong is what made this test fail first time round, and the
                // detector was right to reject it.
                int ext = k % externals;
                var t = new Complex(0.004, 0.0011);
                y[ia, ext] += t; y[ext, ia] += t;
                y[ib, ext] -= t; y[ext, ib] -= t;
            }
            k++;
        }

        return SNP.FromYSweep([1e9, 5e9], [y, y]);
    }

    // ── the answer ────────────────────────────────────────────────────────────

    [Fact]
    public void AdjacentPairs_AreRecovered_FromTheNetworkAlone()
    {
        // 3 external pins, then 8 device ports bridged in adjacent pairs — the shape an extracted
        // passive structure with four two-lead components takes.
        var snp = BuildNetwork(11, [(4, 5), (6, 7), (8, 9), (10, 11)], externals: 3);

        var result = DifferentialPortPairs.Find(snp, 4, 11);

        Assert.True(result.Paired, result.Reason);
        Assert.Equal([(4, 5), (6, 7), (8, 9), (10, 11)],
                     result.Pairs.Select(p => (p.PortA, p.PortB)));
    }

    /// <summary>
    /// The point of measuring rather than pairing in file order: the same detector has to find a
    /// pairing that ISN'T adjacent, or it is only confirming the convention it was meant to replace.
    /// </summary>
    [Fact]
    public void OffsetPairs_AreRecovered_SoTheOrderingConventionIsNotBakedIn()
    {
        var snp = BuildNetwork(11, [(4, 8), (5, 9), (6, 10), (7, 11)], externals: 3);

        var result = DifferentialPortPairs.Find(snp, 4, 11);

        Assert.True(result.Paired, result.Reason);
        Assert.Equal([(4, 8), (5, 9), (6, 10), (7, 11)],
                     result.Pairs.Select(p => (p.PortA, p.PortB)));
    }

    [Fact]
    public void APairsResidual_IsFarBelowTheTolerance_SoTheThresholdIsNotLoadBearing()
    {
        var snp = BuildNetwork(11, [(4, 5), (6, 7), (8, 9), (10, 11)], externals: 3);

        var result = DifferentialPortPairs.Find(snp, 4, 11);

        Assert.True(result.Paired, result.Reason);
        // Orders of magnitude of headroom is the claim; an exact value would just track round-off.
        Assert.True(result.WorstResidual < DifferentialPortPairs.DefaultTolerance / 100,
                    $"worst residual {result.WorstResidual:G3} is uncomfortably close to the " +
                    $"tolerance {DifferentialPortPairs.DefaultTolerance:G3}");
    }

    // ── the refusals, which matter as much ────────────────────────────────────

    [Fact]
    public void PortsThatAreNotDifferential_AreRefused_NotPairedUpAnyway()
    {
        // Every device port shunted to ground independently: nothing bridges anything, so there is
        // no pairing to find. A detector that pairs these would be inventing a circuit.
        var y = new Mat<Complex>(7, 7);
        for (int i = 0; i < 7; i++) y[i, i] = new Complex(0.02 * (i + 1), 0.001);
        var snp = SNP.FromYSweep([1e9], [y]);

        var result = DifferentialPortPairs.Find(snp, 4, 7);

        Assert.False(result.Paired);
        Assert.Empty(result.Pairs);
        Assert.Contains("not two halves", result.Reason);
    }

    [Fact]
    public void AnOddNumberOfPorts_IsRefused_BecauseOneWouldBeLeftOver()
    {
        var snp = BuildNetwork(11, [(4, 5), (6, 7), (8, 9), (10, 11)], externals: 3);

        var result = DifferentialPortPairs.Find(snp, 4, 10);

        Assert.False(result.Paired);
        Assert.Contains("odd count", result.Reason);
    }

    // ── scanning the whole network, with no split supplied ────────────────────

    [Fact]
    public void Scan_FindsThePairs_AndLeavesTheExternalPinsOver()
    {
        var snp = BuildNetwork(11, [(4, 5), (6, 7), (8, 9), (10, 11)], externals: 3);

        var scan = DifferentialPortPairs.Scan(snp);

        Assert.Equal([(4, 5), (6, 7), (8, 9), (10, 11)],
                     scan.Pairs.Select(p => (p.PortA, p.PortB)));
        // The leftovers ARE the pins — nothing bridges them, so the split falls out of the
        // measurement and no port labels are needed to find it.
        Assert.Equal([1, 2, 3], scan.UnpairedPorts);
    }

    /// <summary>
    /// The case that justifies measuring instead of pairing ports in the order the file lists them.
    ///
    /// <para>A solver writes its ports in whatever order it likes. Two networks with the IDENTICAL
    /// topology can therefore have completely different port orderings, and "pair them up two at a
    /// time" is right for one and wrong for the other — while producing a netlist that simulates
    /// perfectly either way. Here the same four bridged sites are written in an order under which
    /// adjacent pairing gets every single pair wrong.</para>
    /// </summary>
    [Fact]
    public void Scan_IsUnaffectedByThePortORDER_WhichAdjacentPairingWouldGetWrong()
    {
        // Site 1 spans the FIRST and LAST device ports; the rest are consecutive. Pairing by port
        // order would give (4,5) (6,7) (8,9) (10,11) — not one of which is a real site.
        (int a, int b)[] sites = [(4, 11), (5, 6), (7, 8), (9, 10)];
        var snp = BuildNetwork(11, sites, externals: 3);

        var scan = DifferentialPortPairs.Scan(snp);

        Assert.Equal(sites.Select(s => (s.a, s.b)).OrderBy(s => s.a),
                     scan.Pairs.Select(p => (p.PortA, p.PortB)));
        Assert.Equal([1, 2, 3], scan.UnpairedPorts);

        // Spelled out, because this is the whole point: the convention disagrees here.
        Assert.DoesNotContain(scan.Pairs, p => p.PortA == 4 && p.PortB == 5);
    }

    [Fact]
    public void Scan_FindsNothing_WhenNoPortsAreDifferential()
    {
        var y = new Mat<Complex>(6, 6);
        for (int i = 0; i < 6; i++) y[i, i] = new Complex(0.02 * (i + 1), 0.001);
        var snp = SNP.FromYSweep([1e9], [y]);

        var scan = DifferentialPortPairs.Scan(snp);

        Assert.Empty(scan.Pairs);
        Assert.Equal([1, 2, 3, 4, 5, 6], scan.UnpairedPorts);
    }

    [Fact]
    public void APortRangeOutsideTheNetwork_IsRefused()
    {
        var snp = BuildNetwork(11, [(4, 5), (6, 7), (8, 9), (10, 11)], externals: 3);

        Assert.False(DifferentialPortPairs.Find(snp, 4, 12).Paired);
        Assert.False(DifferentialPortPairs.Find(snp, 0, 4).Paired);
    }

    /// <summary>
    /// Three ports mutually bridged look pairable one at a time — each has a partner it cancels
    /// against reasonably well — but they cannot all be satisfied at once. Requiring the choice to
    /// be MUTUAL is what rejects them; taking each port's best partner alone would not.
    /// </summary>
    [Fact]
    public void PortsThatDoNotChooseEachOtherBack_AreRefused()
    {
        var y = new Mat<Complex>(8, 8);
        for (int i = 0; i < 4; i++) y[i, i] = new Complex(0.02, 0);

        // 5-6 is a clean pair; 7 and 8 are each bridged to 5, so their best partners are already
        // spoken for.
        void Bridge(int a, int b, double g)
        {
            y[a, a] += g; y[b, b] += g; y[a, b] -= g; y[b, a] -= g;
        }
        Bridge(4, 5, 0.01);
        Bridge(4, 6, 0.01);
        Bridge(4, 7, 0.01);

        var snp = SNP.FromYSweep([1e9], [y]);

        var result = DifferentialPortPairs.Find(snp, 5, 8);

        Assert.False(result.Paired);
        Assert.Empty(result.Pairs);
    }

    /// <summary>
    /// A network with no frequency points cannot be built — <see cref="SNP"/> rejects it outright —
    /// so the empty-network guard in the detector is unreachable from here. Asserted as the type's
    /// behaviour rather than deleted, because the guard is only redundant for as long as that stays
    /// true, and a test claiming to exercise it would be claiming something false.
    /// </summary>
    [Fact]
    public void ANetworkWithNoFrequencyPoints_CannotBeConstructedAtAll()
    {
        Assert.Throws<ArgumentException>(() => new SNP([], 4));
        Assert.Throws<ArgumentNullException>(() => DifferentialPortPairs.Find(null!, 1, 4));
    }
}
