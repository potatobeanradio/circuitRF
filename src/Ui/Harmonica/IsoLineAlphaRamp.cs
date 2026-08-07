using System;

namespace CircuitRF.Ui.Harmonica;

/// <summary>
/// R-h45-6 / §7.2 — the iso-line alpha ramp.
///
/// <code>
/// levels L₀ &lt; L₁ &lt; … &lt; L_{n−1}
/// α_i = α_floor + (1 − α_floor) · ( i / (n−1) ) ^ p        α_{n−1} = 1 exactly
/// </code>
///
/// <para><b>Iso-lines fade with WHICH LEVEL THEY ARE, not with position.</b> So the highest-level
/// iso-line is fully opaque <i>wherever it lands on the Γ plane</i> and successively lower levels fade
/// out. The top contour is the answer — the one bounding the region of best Pout or best efficiency —
/// and the lower ones are context, so the ramp puts emphasis exactly where the design decision is
/// made. Position on the chart is irrelevant to it.</para>
///
/// <para><b>RANKED, not value-proportional</b>, and this is the part most likely to be "simplified"
/// back. With evenly spaced levels (<c>ContourData.LevelStep</c>, the usual case) the two are
/// identical, so a value-proportional implementation looks correct on every ordinary fixture. It
/// fails on a metric with a long low tail — efficiency near a hole, say — where it crushes almost
/// every contour to near-invisible while the ranked form degrades gracefully.
/// <c>ForLevels</c> is deliberately independent of the level VALUES for exactly that reason: it takes
/// a count, not a table.</para>
///
/// <para><b>One flat alpha per polyline.</b> Every vertex of one iso-line shares one level, so this
/// is a single paint alpha per contour — no shader, no per-vertex work, and no geometry-change cache
/// to maintain. Labels, when on, INHERIT their line's alpha; a faded contour carrying a
/// full-opacity label would misread as the important one.</para>
///
/// <para>This assumes <b>higher is more interesting</b>, which holds for both shipped metrics (Pout
/// and efficiency). A future lower-is-better metric would need the ramp direction inverted; that
/// becomes a per-chart flag if and when such a metric appears, not now.</para>
/// </summary>
public static class IsoLineAlphaRamp
{
    /// <summary>
    /// The alpha for the level at rank <paramref name="rank"/> out of <paramref name="levelCount"/>,
    /// in [0, 1]. Rank 0 is the LOWEST level; rank <c>levelCount − 1</c> is the highest and returns
    /// <b>exactly</b> 1.0 — not 0.999…, because "the top contour is the answer" is a claim the ramp
    /// has to keep at the boundary, not merely approach.
    /// </summary>
    public static double Alpha(int rank, int levelCount, double alphaFloor, double exponent)
    {
        if (levelCount <= 1) return 1.0;                 // a lone contour IS the top one

        int r = Math.Clamp(rank, 0, levelCount - 1);
        if (r == levelCount - 1) return 1.0;             // exact, by construction — never by rounding

        double floor = Math.Clamp(alphaFloor, 0.0, 1.0);
        double p     = Math.Max(1e-6, exponent);
        double t     = (double)r / (levelCount - 1);
        return floor + (1.0 - floor) * Math.Pow(t, p);
    }

    /// <summary>
    /// The ramp for a whole level set, lowest level first. Takes a COUNT rather than the level
    /// values — see the "ranked, not value-proportional" note above; handing this method the values
    /// would be the first step toward the version that crushes a long low tail.
    /// </summary>
    public static double[] ForLevels(int levelCount, double alphaFloor, double exponent)
    {
        if (levelCount <= 0) return [];
        var a = new double[levelCount];
        for (int i = 0; i < levelCount; i++) a[i] = Alpha(i, levelCount, alphaFloor, exponent);
        return a;
    }

    /// <summary>
    /// The rank of <paramref name="level"/> within an ASCENDING level set — the index the ramp is a
    /// function of. Uses the nearest level rather than exact equality, because an iso-polyline
    /// carries the level it was extracted at and floating-point equality against the generating table
    /// is not something to rely on.
    /// </summary>
    public static int RankOf(double level, IReadOnlyList<double> ascendingLevels)
    {
        if (ascendingLevels.Count == 0) return 0;
        int best = 0;
        double bestD = Math.Abs(ascendingLevels[0] - level);
        for (int i = 1; i < ascendingLevels.Count; i++)
        {
            double d = Math.Abs(ascendingLevels[i] - level);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    /// <summary>The alpha as a byte, for a Skia paint.</summary>
    public static byte AlphaByte(int rank, int levelCount, double alphaFloor, double exponent)
        => (byte)Math.Clamp((int)Math.Round(Alpha(rank, levelCount, alphaFloor, exponent) * 255.0), 0, 255);
}
