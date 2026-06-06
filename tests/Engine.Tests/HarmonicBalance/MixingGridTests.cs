using CircuitRF.Engine.HarmonicBalance;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// Unit tests for MixingGrid — the diamond-truncated two-tone index map.
///
/// Tests verify:
///   (1) Enumeration order is locked (§16 item 1).
///   (2) Total count formula M = 1 + MaxMixOrder*(MaxMixOrder+1).
///   (3) Hero-5 mixing products land on the expected indices.
///   (4) Raising MaxMixOrder only appends — existing indices unchanged.
///   (5) IndexOf / ToneOf round-trip.
///   (6) Conjugates (half-plane negatives) are NOT in the retained set.
/// </summary>
public class MixingGridTests
{
    // ── (1) DC is always index 0 ───────────────────────────────────────────────

    [Theory]
    [InlineData(1)] [InlineData(3)] [InlineData(5)]
    public void DcIsIndexZero(int maxMixOrder)
    {
        var g = new MixingGrid(maxMixOrder);
        Assert.Equal((0, 0), g.ToneOf(0));
        Assert.Equal(0, g.IndexOf(0, 0));
    }

    // ── (2) Count formula ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(0,  1)]
    [InlineData(1,  3)]
    [InlineData(2,  7)]
    [InlineData(3, 13)]
    [InlineData(4, 21)]
    [InlineData(5, 31)]
    public void MixCountMatchesFormula(int maxMixOrder, int expected)
    {
        var g = new MixingGrid(maxMixOrder);
        Assert.Equal(expected, g.MixCount);
    }

    // ── (3) Hero-5 products at exact indices ──────────────────────────────────

    // Enumeration for MaxMixOrder=5 (worked out from §16 locked order):
    //  m=0: (0,0)                              → 0
    //  m=1: (1,0),(0,1)                        → 1,2
    //  m=2: (2,0),(1,1),(1,-1),(0,2)           → 3,4,5,6
    //  m=3: (3,0),(2,1),(2,-1),(1,2),(1,-2),(0,3) → 7,8,9,10,11,12
    //  m=4: (4,0),(3,1),(3,-1),(2,2),(2,-2),(1,3),(1,-3),(0,4) → 13..20
    //  m=5: (5,0),(4,1),(4,-1),(3,2),(3,-2),(2,3),(2,-3),(1,4),(1,-4),(0,5) → 21..30

    [Fact]
    public void Hero5Products_LandOnExpectedIndices()
    {
        var g = new MixingGrid(5);

        // Carriers
        Assert.Equal(1, g.IndexOf(1, 0));
        Assert.Equal(2, g.IndexOf(0, 1));

        // IM2 baseband: (1,-1)
        Assert.Equal(5, g.IndexOf(1, -1));

        // IM3: (2,-1) and half-plane rep of (-1,2) → (1,-2)
        Assert.Equal(9,  g.IndexOf(2, -1));
        Assert.Equal(11, g.IndexOf(1, -2));

        // IM5: (3,-2) and half-plane rep of (-2,3) → (2,-3)
        Assert.Equal(25, g.IndexOf(3, -2));
        Assert.Equal(27, g.IndexOf(2, -3));
    }

    [Fact]
    public void Hero5Products_CorrectFrequencies()
    {
        const double f1 = 1.995e9;
        const double f2 = 2.005e9;
        var g = new MixingGrid(5);

        Assert.Equal(f1, g.FrequencyOf(g.IndexOf(1, 0), f1, f2), 1.0);  // carrier 1
        Assert.Equal(f2, g.FrequencyOf(g.IndexOf(0, 1), f1, f2), 1.0);  // carrier 2

        // IM3 lower: 2f1−f2 = 1.985 GHz
        Assert.Equal(2*f1 - f2, g.FrequencyOf(g.IndexOf(2,-1), f1, f2), 1.0);

        // IM5 lower: 3f1−2f2 = 1.975 GHz
        Assert.Equal(3*f1 - 2*f2, g.FrequencyOf(g.IndexOf(3,-2), f1, f2), 1.0);
    }

    // ── (4) Raising MaxMixOrder only appends ──────────────────────────────────

    [Fact]
    public void RaisingMaxMixOrder_OnlyAppends()
    {
        var g4 = new MixingGrid(4);
        var g5 = new MixingGrid(5);

        // All indices from MaxMixOrder=4 are unchanged in MaxMixOrder=5.
        for (int i = 0; i < g4.MixCount; i++)
            Assert.Equal(g4.ToneOf(i), g5.ToneOf(i));

        // MaxMixOrder=5 adds exactly 10 more indices.
        Assert.Equal(g4.MixCount + 10, g5.MixCount);
    }

    [Fact]
    public void RaisingMaxMixOrder_IndexOfUnchanged()
    {
        var g3 = new MixingGrid(3);
        var g5 = new MixingGrid(5);

        foreach (var (k1, k2) in g3.All())
            Assert.Equal(g3.IndexOf(k1, k2), g5.IndexOf(k1, k2));
    }

    // ── (5) IndexOf / ToneOf round-trip ──────────────────────────────────────

    [Theory]
    [InlineData(3)] [InlineData(5)]
    public void RoundTrip(int maxMixOrder)
    {
        var g = new MixingGrid(maxMixOrder);
        for (int i = 0; i < g.MixCount; i++)
        {
            var (k1, k2) = g.ToneOf(i);
            Assert.Equal(i, g.IndexOf(k1, k2));
        }
    }

    // ── (6) Conjugate half-plane negatives are NOT retained ──────────────────

    [Fact]
    public void ConjugatesNotRetained()
    {
        var g = new MixingGrid(5);

        // (0,-1): k1=0, k2=-1 < 0 → not in half-plane
        Assert.Equal(-1, g.IndexOf(0, -1));
        Assert.Equal(-1, g.IndexOf(0, -2));

        // (-1,2): k1=-1 < 0 → not in half-plane
        Assert.Equal(-1, g.IndexOf(-1, 2));
        Assert.Equal(-1, g.IndexOf(-2, 3));
    }

    // ── (7) Enumeration order: ascending m, k1 desc, k2 desc ────────────────

    [Fact]
    public void EnumerationOrder_AscendingM_Then_K1Desc_K2Desc()
    {
        var g = new MixingGrid(3);

        // m=0
        Assert.Equal((0, 0), g.ToneOf(0));
        // m=1: k1 desc
        Assert.Equal((1, 0), g.ToneOf(1));
        Assert.Equal((0, 1), g.ToneOf(2));
        // m=2: k1 desc, within k1: k2 desc
        Assert.Equal((2,  0), g.ToneOf(3));
        Assert.Equal((1,  1), g.ToneOf(4));
        Assert.Equal((1, -1), g.ToneOf(5));
        Assert.Equal((0,  2), g.ToneOf(6));
        // m=3
        Assert.Equal((3,  0), g.ToneOf(7));
        Assert.Equal((2,  1), g.ToneOf(8));
        Assert.Equal((2, -1), g.ToneOf(9));
        Assert.Equal((1,  2), g.ToneOf(10));
        Assert.Equal((1, -2), g.ToneOf(11));
        Assert.Equal((0,  3), g.ToneOf(12));
    }

    // ── (8) All retained (k1,k2) are within the diamond ──────────────────────

    [Theory]
    [InlineData(3)] [InlineData(5)]
    public void AllRetained_WithinDiamond(int maxMixOrder)
    {
        var g = new MixingGrid(maxMixOrder);
        foreach (var (k1, k2) in g.All())
            Assert.True(Math.Abs(k1) + Math.Abs(k2) <= maxMixOrder,
                $"({k1},{k2}) outside diamond |k1|+|k2|≤{maxMixOrder}");
    }

    // ── (9) No duplicates ────────────────────────────────────────────────────

    [Theory]
    [InlineData(5)]
    public void NoDuplicates(int maxMixOrder)
    {
        var g = new MixingGrid(maxMixOrder);
        var seen = new HashSet<(int,int)>();
        for (int i = 0; i < g.MixCount; i++)
        {
            var t = g.ToneOf(i);
            Assert.True(seen.Add(t), $"Duplicate at index {i}: ({t.k1},{t.k2})");
        }
    }
}
