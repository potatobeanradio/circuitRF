// Units and handedness for the board interchange format (docs/sonnet-briefs/
// brief-L4d-kicad-pcb-import.md R-L4d-2, R-L4d-3). Two rules, both of which fail SILENTLY when broken,
// which is why they live in one small file with tests of their own rather than inline at each call:
//
//  1. Coordinates are decimal millimetres and the destination grid is DBU. At the default 1000 DBU/µm
//     one DBU is one nanometre and 1 mm is 1,000,000 DBU EXACTLY — this format's internal grid and
//     ours are the same grid. Convert with Math.Round, never a cast: (long)(x * 1e6) truncates toward
//     ZERO, so it is wrong only on the negative side, which is exactly the bug a fixture drawn in the
//     first quadrant cannot see (R-L4d-2's own framing, and gate 3 is the negative case).
//
//  2. Y is DOWN in the source and UP in .clay. The flip happens HERE, once, at the point a millimetre
//     becomes a DBU, and nowhere else. A sign error yields a mirrored board that looks entirely
//     plausible — the WB-C lesson, which is also why the handedness fixture is asymmetric on BOTH axes.

namespace CircuitRF.Design.Layout.Interchange;

public static class PcbUnits
{
    /// <summary>Microns per millimetre — the only place the two length units meet.</summary>
    public const int MicronsPerMillimeter = 1000;

    /// <summary>DBU per millimetre for a destination resolution of <paramref name="dbuPerMicron"/>
    /// (1,000,000 at the 1000 DBU/µm default).</summary>
    public static long DbuPerMillimeter(int dbuPerMicron) => (long)dbuPerMicron * MicronsPerMillimeter;

    /// <summary>A source X coordinate (mm) as DBU. Rounds — never truncates (R-L4d-2).</summary>
    public static long X(double millimeters, int dbuPerMicron)
        => (long)Math.Round(millimeters * DbuPerMillimeter(dbuPerMicron), MidpointRounding.AwayFromZero);

    /// <summary>A source Y coordinate (mm) as DBU, <b>with the one and only handedness flip</b>
    /// (R-L4d-3). Never call <see cref="X"/> for a Y coordinate.</summary>
    public static long Y(double millimeters, int dbuPerMicron) => -X(millimeters, dbuPerMicron);

    /// <summary>A source LENGTH (mm) as DBU — a width, a diameter, a thickness. Unsigned by nature, so
    /// it never flips: the distinction from <see cref="Y"/> is the entire point of having three
    /// methods instead of one.</summary>
    public static long Length(double millimeters, int dbuPerMicron) => X(millimeters, dbuPerMicron);

    /// <summary>
    /// A source placement/orientation angle (degrees) as a circuitRF placement angle (degrees CCW in
    /// the Y-up DBU frame) — <b>the identity</b>, and that is a measured result rather than an
    /// assumption.
    ///
    /// <para>In the source's raw Y-DOWN frame a positive angle rotates CLOCKWISE (measured against four
    /// real boards by placing each rotated footprint's pads and checking which sense lands them on a
    /// track endpoint of their own net: 31 hits vs 8, and 28 vs 9, once the 0°/180° placements that
    /// cannot distinguish the two senses are excluded). Flipping Y turns a clockwise rotation in a
    /// Y-down frame into a counter-clockwise one in a Y-up frame, which is precisely circuitRF's
    /// convention — so the number passes through untouched.</para>
    ///
    /// <para>Kept as a named method anyway: "we checked, and it is the identity" is a fact worth being
    /// able to point at, and a future reader who assumes a negation has somewhere to find out.</para>
    /// </summary>
    public static double Angle(double degrees) => degrees;
}
