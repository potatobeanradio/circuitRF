using CircuitRF.WBond;
using Xunit;

namespace CircuitRF.WBond.Tests;

/// <summary>
/// The loop-height definition (wbond.md §3.1a): <b>a wire's maximum z minus its minimum z</b>.
///
/// <para>The distinction these tests exist for is that this is NOT the rise above the chord. The two
/// coincide only when both feet sit at the same z — which is exactly why the whole pre-existing suite
/// passed unchanged when the definition was tightened: every fixture in it had level feet. An
/// asymmetric loop is the case that separates them, and it is the ordinary chip-and-wire one.</para>
/// </summary>
public class LoopHeightDefinitionTests
{
    private const long Mil = 25_400;

    /// <summary>A seeded arch between feet at two DIFFERENT heights — the case that discriminates.</summary>
    private static Wire AsymmetricWire(long loopHeightNm, long startZ, long endZ) =>
        LoopShape.CreateSeedWire(
            new Point3(0, 0, startZ),
            new Point3(100 * Mil, 0, endZ),
            diameterNm: Mil,
            material: WireMaterials.Default.Name,
            loopHeightNm: loopHeightNm);

    // ---------------------------------------------------------------- the definition

    [Fact]
    public void LoopHeight_IsMaxZMinusMinZ_NotRiseAboveTheChord()
    {
        var wire = new Wire
        {
            Points =
            [
                new Point3(0, 0, 0),
                new Point3(10, 0, 700),
                new Point3(20, 0, 1_000),   // the crest
                new Point3(30, 0, 600),
                new Point3(40, 0, 400),     // the far foot, well above the near one
            ],
        };

        Assert.Equal(1_000, wire.LoopHeightNm);   // 1000 − 0
        Assert.Equal(400, wire.FootDropNm);       // |400 − 0|
    }

    [Fact]
    public void LoopHeight_OfAStraightSlopedWire_IsItsFootDrop()
    {
        var wire = new Wire
        {
            Points = [new Point3(0, 0, 0), new Point3(50, 0, 300), new Point3(100, 0, 600)],
        };

        Assert.Equal(600, wire.LoopHeightNm);
        Assert.Equal(wire.FootDropNm, wire.LoopHeightNm);
    }

    [Fact]
    public void LoopHeight_OfADegenerateWire_IsZero_NotAThrow()
    {
        Assert.Equal(0, new Wire().LoopHeightNm);
        Assert.Equal(0, new Wire { Points = [new Point3(0, 0, 5)] }.LoopHeightNm);
    }

    // ---------------------------------------------------------------- LoopShape.Write honours it

    /// <summary>
    /// <b>The headline.</b> A profile asked for 20 mil of loop height produces a wire that measures
    /// 20 mil from its lowest point to its highest — even with the feet 8 mil apart in z.
    /// </summary>
    [Theory]
    [InlineData(0L, 0L)]                 // level feet: the two definitions coincide
    [InlineData(0L, 8L * 25_400)]        // rising to a substrate lead
    [InlineData(8L * 25_400, 0L)]        // and the reverse, so no sign is assumed
    [InlineData(3L * 25_400, 5L * 25_400)]
    public void Write_ProducesAWireWhoseMeasuredLoopHeightIsTheRequestedOne(long startZ, long endZ)
    {
        long requested = 20 * Mil;

        var wire = AsymmetricWire(requested, startZ, endZ);

        // One DBU of slack for the rounding of each interior point to an integer nanometre.
        Assert.InRange(wire.LoopHeightNm, requested - 2, requested + 2);
    }

    /// <summary>
    /// The old behaviour, stated as a comparison so the change is unambiguous: measuring rise above
    /// the CHORD on an asymmetric loop reads low, and by exactly the amount the foot drop contributes.
    /// </summary>
    [Fact]
    public void RiseAboveTheChord_ReadsLowerThanTheLoopHeight_WhenTheFeetDiffer()
    {
        long requested = 20 * Mil;
        var wire = AsymmetricWire(requested, 0, 8 * Mil);

        var start = wire.Points[0];
        var end = wire.Points[^1];

        double peakAboveChord = 0;
        foreach (var p in wire.Points)
        {
            double t = WireEdits.ChordParameter(start, end, p);
            peakAboveChord = System.Math.Max(peakAboveChord, p.Z - (start.Z + t * (end.Z - start.Z)));
        }

        Assert.True(peakAboveChord < requested,
            $"rise above the chord ({peakAboveChord}) must be strictly less than the loop height ({requested}) " +
            "on a loop whose feet are at different heights — that gap is the whole reason the definition matters");

        // And the wire itself still measures the requested height, by the definition.
        Assert.InRange(wire.LoopHeightNm, requested - 2, requested + 2);
    }

    /// <summary>The feet land exactly on their pads regardless — the invariant the solve must not disturb.</summary>
    [Fact]
    public void Write_LeavesBothFeetExactlyOnTheirPads()
    {
        var start = new Point3(123, 456, 7_000);
        var end = new Point3(99_000, -400, 210_000);

        var wire = LoopShape.CreateSeedWire(start, end, Mil, WireMaterials.Default.Name, 20 * Mil);

        Assert.Equal(start, wire.Points[0]);
        Assert.Equal(end, wire.Points[^1]);
    }

    /// <summary>
    /// A loop height below the feet's own separation is not achievable by any shape — a dead-straight
    /// wire already measures that much. The wire comes back at that floor rather than being refused
    /// or arched upward to fake the number.
    /// </summary>
    [Fact]
    public void Write_RequestBelowTheFootDrop_ClampsToTheFloor_RatherThanRefusing()
    {
        long footDrop = 30 * Mil;
        var wire = AsymmetricWire(loopHeightNm: 5 * Mil, startZ: 0, endZ: footDrop);

        Assert.Equal(0.0, LoopShape.SolveAmplitudeNm(LoopShape.Seed(), 5 * Mil, 0, footDrop));
        Assert.Equal(footDrop, wire.LoopHeightNm);
        Assert.Equal(footDrop, wire.FootDropNm);
    }

    // ---------------------------------------------------------------- round trip

    /// <summary>
    /// Reading a wire's shape back and re-applying it reproduces the same loop height — the property
    /// Copy Coordinates / Paste and the free-wire "Set Loop Height" path both rest on.
    /// </summary>
    [Fact]
    public void SolveAmplitude_RoundTrips_ForAWireItItselfProduced()
    {
        var start = new Point3(0, 0, 0);
        var end = new Point3(100 * Mil, 0, 8 * Mil);

        var wire = LoopShape.CreateSeedWire(start, end, Mil, WireMaterials.Default.Name, 20 * Mil);
        long first = wire.LoopHeightNm;

        LoopShape.Write(wire, start, end, LoopShape.Seed(), 20 * Mil);

        Assert.Equal(first, wire.LoopHeightNm);
    }

    /// <summary>
    /// <b><see cref="LoopShape.Read"/> is the exact inverse of <see cref="LoopShape.Write"/></b> —
    /// reading a wire's own geometry back and writing it again at its own loop height reproduces the
    /// wire to the nanometre, on unequal feet. Copy Coordinates and the group flip both rest on it.
    /// </summary>
    [Fact]
    public void Read_ThenWrite_ReproducesTheWireExactly()
    {
        var start = new Point3(0, 0, 3 * Mil);
        var end = new Point3(140 * Mil, 20 * Mil, 11 * Mil);

        var wire = LoopShape.CreateSeedWire(start, end, Mil, WireMaterials.Default.Name, 25 * Mil);
        var before = wire.Points.ToArray();

        LoopShape.Write(wire, start, end, LoopShape.Read(wire), wire.LoopHeightNm);

        Assert.Equal(before, wire.Points.ToArray());
    }

    /// <summary>Doubling the requested loop height doubles the measured one (level feet, no floor in play).</summary>
    [Fact]
    public void DoublingTheRequestedHeight_DoublesTheMeasuredLoopHeight()
    {
        var start = new Point3(0, 0, 0);
        var end = new Point3(100 * Mil, 0, 0);

        var wire = LoopShape.CreateSeedWire(start, end, Mil, WireMaterials.Default.Name, 10 * Mil);
        Assert.InRange(wire.LoopHeightNm, 10 * Mil - 2, 10 * Mil + 2);

        LoopShape.Write(wire, start, end, LoopShape.Seed(), 20 * Mil);

        Assert.InRange(wire.LoopHeightNm, 20 * Mil - 2, 20 * Mil + 2);
    }
}
