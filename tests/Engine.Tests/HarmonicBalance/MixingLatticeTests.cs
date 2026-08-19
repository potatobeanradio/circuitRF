using CircuitRF.Engine.HarmonicBalance;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// The T-tone diamond lattice (harmonic-balance.md §6.5). Pure combinatorics — microseconds.
///
/// The load-bearing test here is <see cref="Lattice_AtTwoTones_ReproducesMixingGridExactly"/>:
/// the T ≥ 3 enumeration rule (ascending total order, then lexicographic descending within the
/// half-space) is claimed to be the general form of <see cref="MixingGrid"/>'s LOCKED two-tone
/// order. If that claim is wrong, the generalization is built on a different index map than the
/// one the measurement library and every two-tone cube already depend on.
/// </summary>
public class MixingLatticeTests(ITestOutputHelper output)
{
    [Fact]
    public void Lattice_AtTwoTones_ReproducesMixingGridExactly()
    {
        for (int order = 0; order <= 6; order++)
        {
            var grid    = new MixingGrid(order);
            var lattice = new MixingLattice(2, order);

            Assert.Equal(grid.MixCount, lattice.MixCount);
            for (int m = 0; m < grid.MixCount; m++)
            {
                var (k1, k2) = grid.ToneOf(m);
                var k        = lattice.ToneOf(m);
                Assert.True(k1 == k[0] && k2 == k[1],
                    $"order {order}, mixIdx {m}: MixingGrid ({k1},{k2}) vs MixingLattice ({k[0]},{k[1]})");
            }
        }
        output.WriteLine("MixingLattice(2, O) == MixingGrid(O) element for element, O = 0..6.");
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void CountFor_MatchesActualEnumeration(int tones)
    {
        for (int order = 0; order <= 4; order++)
        {
            var lattice = new MixingLattice(tones, order);
            Assert.Equal(MixingLattice.CountFor(tones, order), lattice.MixCount);
        }
    }

    [Theory]
    [InlineData(3)] [InlineData(4)] [InlineData(6)]
    public void Retained_IsAHalfSpace_AndEveryProductIsAddressable(int tones)
    {
        var lattice = new MixingLattice(tones, 3);
        var seen    = new HashSet<string>();

        for (int m = 0; m < lattice.MixCount; m++)
        {
            var k = lattice.ToneOf(m);

            // Total order within the diamond.
            Assert.True(lattice.OrderOf(m) <= 3);

            // Half-space: the first nonzero component is positive (DC excepted).
            int firstNonZero = 0;
            foreach (int c in k) { if (c != 0) { firstNonZero = c; break; } }
            Assert.True(firstNonZero >= 0, $"mixIdx {m} {MixingLattice.Label(k)} is not a half-space rep");

            // No duplicates, and the conjugate partner is NOT also retained.
            Assert.True(seen.Add(MixingLattice.Label(k)), $"duplicate {MixingLattice.Label(k)}");
            var neg = new int[tones];
            for (int t = 0; t < tones; t++) neg[t] = -k[t];
            if (firstNonZero != 0)
                Assert.Equal(-1, lattice.IndexOf(neg));

            // Round-trips through IndexOf.
            Assert.Equal(m, lattice.IndexOf(k));
        }
    }

    [Fact]
    public void RaisingMaxMixOrder_OnlyAppends_SoCubeIndicesAreStable()
    {
        // The whole point of ascending-total-order enumeration: a user raising MaxMixOrder must
        // not renumber the products their existing plots and measurements already reference.
        for (int tones = 2; tones <= 6; tones++)
        {
            var small = new MixingLattice(tones, 2);
            var big   = new MixingLattice(tones, 3);

            Assert.True(big.MixCount > small.MixCount);
            for (int m = 0; m < small.MixCount; m++)
                Assert.Equal(MixingLattice.Label(small.ToneOf(m)), MixingLattice.Label(big.ToneOf(m)));
        }
    }

    [Fact]
    public void DcIsIndexZero_AndCarriersFollowInToneOrder()
    {
        var lattice = new MixingLattice(3, 4);

        Assert.Equal("(0,0,0)", lattice.Label(0));
        Assert.Equal("(1,0,0)", lattice.Label(1));
        Assert.Equal("(0,1,0)", lattice.Label(2));
        Assert.Equal("(0,0,1)", lattice.Label(3));

        // The label form is what the data display renders and the measurement language matches on.
        int im3 = lattice.IndexOf([2, -1, 0]);
        Assert.True(im3 > 0);
        Assert.Equal("(2,-1,0)", lattice.Label(im3));
    }

    [Fact]
    public void FrequencyOf_IsSigned_AndSumsTheToneVector()
    {
        var lattice = new MixingLattice(3, 3);
        double[] f  = [1.00e9, 1.01e9, 1.02e9];

        int baseband = lattice.IndexOf([1, -1, 0]);
        Assert.True(baseband > 0);
        Assert.Equal(-10e6, lattice.FrequencyOf(baseband, f), 3);

        int threeToneIm = lattice.IndexOf([1, 1, -1]);
        Assert.True(threeToneIm > 0);
        Assert.Equal(0.99e9, lattice.FrequencyOf(threeToneIm, f), 3);
    }

    [Fact]
    public void CountFor_ReportsTheSizesTheCeilingIsSetAgainst()
    {
        // These are the numbers the analysis ceiling and its refusal message quote. Pinned so a
        // change to the truncation rule cannot silently move the practical tone/order envelope.
        Assert.Equal(31,   MixingLattice.CountFor(2, 5));
        Assert.Equal(116,  MixingLattice.CountFor(3, 5));
        Assert.Equal(65,   MixingLattice.CountFor(4, 3));
        Assert.Equal(189,  MixingLattice.CountFor(6, 3));
        Assert.Equal(645,  MixingLattice.CountFor(6, 4));   // over the 600 cap — must be refused
        Assert.Equal(1827, MixingLattice.CountFor(6, 5));

        // Saturates rather than overflowing, so an absurd request still compares > cap.
        Assert.Equal(int.MaxValue, MixingLattice.CountFor(10, 30));
    }
}
