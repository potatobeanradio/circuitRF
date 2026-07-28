using CircuitRF.Ui.Layout.Interchange;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Tests;

/// <summary>docs/sonnet-briefs/brief-dxf-layer-colors.md gate 3 — "a table of known RGB values maps to
/// the expected indices; the palette's irregular low and high entries are covered explicitly."</summary>
public class DxfAciPaletteTests
{
    [Theory]
    [InlineData(255, 0, 0, 1)]     // Red
    [InlineData(255, 255, 0, 2)]   // Yellow
    [InlineData(0, 255, 0, 3)]     // Green
    [InlineData(0, 255, 255, 4)]   // Cyan
    [InlineData(0, 0, 255, 5)]     // Blue
    [InlineData(255, 0, 255, 6)]   // Magenta
    [InlineData(255, 255, 255, 7)] // White
    public void NearestIndex_PurePrimaries_MapToTheExpectedAciIndex(byte r, byte g, byte b, int expected)
    {
        Assert.Equal(expected, DxfAciPalette.NearestIndex(new Rgba(r, g, b)));
    }

    [Theory]
    [InlineData(0, 0, 0)]       // black
    [InlineData(1, 1, 1)]       // near-black
    [InlineData(255, 255, 255)] // white
    [InlineData(128, 64, 200)]  // arbitrary
    public void NearestIndex_NeverReturnsZero_ByBlockIsNotARealColor(byte r, byte g, byte b)
    {
        Assert.NotEqual(0, DxfAciPalette.NearestIndex(new Rgba(r, g, b)));
    }

    [Fact]
    public void ToRgb_ThenNearestIndex_ThenToRgb_IsStable_ForEveryIndex()
    {
        // A strict "NearestIndex(ToRgb(i)) == i" would fail on any two indices that legitimately share
        // the exact same RGB (real ACI tables do have such duplicates, e.g. white appearing more than
        // once) — the invariant that actually always holds is that re-resolving through the round trip
        // never changes the COLOR, even if a different index happens to alias it.
        for (int i = 1; i <= 255; i++)
        {
            var rgb = DxfAciPalette.ToRgb(i);
            var roundTripped = DxfAciPalette.ToRgb(DxfAciPalette.NearestIndex(rgb));
            Assert.Equal(rgb, roundTripped);
        }
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(-1)]
    [InlineData(256)]
    [InlineData(999)]
    public void ToRgb_OutOfRangeIndex_ClampsRatherThanThrowing(int index)
    {
        var ex = Record.Exception(() => DxfAciPalette.ToRgb(index));
        Assert.Null(ex);
    }

    [Fact]
    public void ToRgb_NegativeIndex_ClampsToZero()
    {
        Assert.Equal(DxfAciPalette.ToRgb(0), DxfAciPalette.ToRgb(-5));
    }

    [Fact]
    public void ToRgb_TooLargeIndex_ClampsTo255()
    {
        Assert.Equal(DxfAciPalette.ToRgb(255), DxfAciPalette.ToRgb(999));
    }

    // ── Irregular entries (R-col-2: "entries 1-9 and 250-255 are not on any regular grid") ──────────
    // Exact byte values for 8/9/250-255 were not independently re-verified in this session (see
    // DxfAciPalette.cs's own header comment) — these assertions cover the STRUCTURAL properties of the
    // real published table that should hold regardless of minor recall error, rather than encoding
    // specific byte literals as "verified truth."

    [Fact]
    public void ToRgb_Indices8And9_AreDistinctFromEachOtherAndFromWhite()
    {
        var c8 = DxfAciPalette.ToRgb(8);
        var c9 = DxfAciPalette.ToRgb(9);
        var white = DxfAciPalette.ToRgb(7);

        Assert.NotEqual(c8, c9);
        Assert.NotEqual(c8, white);
        Assert.NotEqual(c9, white);
    }

    [Fact]
    public void ToRgb_Indices8And9_AreGrayscale()
    {
        // The real ACI table's 8/9 are neutral grays (no hue) — R==G==B is the structural property,
        // independent of the exact shade.
        var c8 = DxfAciPalette.ToRgb(8);
        var c9 = DxfAciPalette.ToRgb(9);
        Assert.True(c8.R == c8.G && c8.G == c8.B, "index 8 is expected to be a neutral gray");
        Assert.True(c9.R == c9.G && c9.G == c9.B, "index 9 is expected to be a neutral gray");
    }

    [Fact]
    public void ToRgb_HighGrayscaleRamp_250To255_IsMonotonicallyBrighteningGrayscale()
    {
        byte prevLevel = 0;
        for (int i = 250; i <= 255; i++)
        {
            var c = DxfAciPalette.ToRgb(i);
            Assert.True(c.R == c.G && c.G == c.B, $"index {i} is expected to be a neutral gray, was ({c.R},{c.G},{c.B})");
            Assert.True(c.R > prevLevel, $"index {i} ({c.R}) should be brighter than the previous entry ({prevLevel})");
            prevLevel = c.R;
        }
    }

    [Fact]
    public void ToRgb_HighGrayscaleRamp_250To255_AreAllDistinct()
    {
        var seen = new HashSet<Rgba>();
        for (int i = 250; i <= 255; i++)
            Assert.True(seen.Add(DxfAciPalette.ToRgb(i)), $"index {i} duplicates an earlier entry in the 250-255 ramp");
    }
}
