namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>
/// Shared section-count rule for every non-uniform line modelled as a cascade of short uniform
/// MLIN sections — MTaper and MKlopf alike (brief-mtaper-mklopf.md §1.1, R-tap-1). "There are two
/// discretizations, not one" (R-tap-2): this class answers ONLY the ELECTRICAL question (how many
/// sections does the S-parameter model need); the ARTWORK's own tessellation (how finely the drawn
/// outline is chorded) is a separate, purely geometric decision made elsewhere and must never be
/// coupled to this number.
///
/// <b>R-tap-1: take whichever of the two criteria demands more sections.</b>
/// <list type="number">
/// <item><b>Electrically short sections</b> — each section's physical length must satisfy
/// <c>length ≤ λ_min / 20</c>, where <c>λ_min = c / (f_max·√εeff_max)</c>. Evaluated at the
/// frequency actually being stamped (this is a per-<c>Stamp</c>-call decision, not a one-time
/// sweep-wide constant — the practical reading of "derive the default from the analysis sweep"
/// available to a <c>ComponentModel</c>, which only ever sees one frequency per call: a lower
/// analysis frequency naturally needs fewer sections, and the cascade never does more work than a
/// given frequency point actually requires).</item>
/// <item><b>Profile resolution</b> — no section's width may differ from its neighbour's by more
/// than 2% of the taper's total width range. A linear taper (MTaper) needs exactly N=50 sections to
/// satisfy this UNIFORMLY along its length, regardless of how large the taper ratio is (uniform
/// ΔW/section). A Klopfenstein taper's profile changes fastest near its own midpoint, so this
/// criterion can demand a much larger N there even at a frequency low enough that criterion 1 alone
/// would ask for very few sections — implemented generically here (not linear-taper-specific) by
/// sampling the actual width function, precisely so MKlopf can reuse this class unmodified.</item>
/// </list>
/// </summary>
public static class MicrostripCascadeSectioning
{
    private const double SpeedOfLight = 2.99792458e8;
    private const double MaxSectionLengthFraction = 1.0 / 20.0;
    private const double MaxFractionalWidthStep = 0.02;

    /// <summary>Electrical section count only (criterion 1) — exposed separately so a caller that
    /// wants to report each criterion's own contribution (R-tap-1's "report the value used") can.</summary>
    public static int ElectricalSectionCount(double lengthMeters, double freqHz, double eeffMax)
    {
        if (freqHz <= 0.0 || eeffMax <= 0.0) return 1;
        double lambdaMin = SpeedOfLight / (freqHz * Math.Sqrt(eeffMax));
        double maxSectionLen = lambdaMin * MaxSectionLengthFraction;
        return Math.Max(1, (int)Math.Ceiling(lengthMeters / maxSectionLen));
    }

    /// <summary>Profile-resolution section count only (criterion 2) — the smallest N (a power of
    /// two, for a cheap doubling search) such that every pair of adjacent sample points (N+1
    /// samples of <paramref name="widthAtFraction"/> over t∈[0,1]) differs by no more than 2% of
    /// the profile's own overall width range. Generic over the profile shape — a linear taper and a
    /// Klopfenstein taper both call this unmodified.</summary>
    public static int GeometricSectionCount(Func<double, double> widthAtFraction, int maxSections = 4096)
    {
        // Sample coarsely first to find the overall width range (cheap: 33 points).
        double wMin = double.PositiveInfinity, wMax = double.NegativeInfinity;
        for (int i = 0; i <= 32; i++)
        {
            double w = widthAtFraction(i / 32.0);
            wMin = Math.Min(wMin, w);
            wMax = Math.Max(wMax, w);
        }
        double range = wMax - wMin;
        if (range <= 0.0) return 1; // uniform width — no profile-resolution constraint at all

        double toleranceAbs = MaxFractionalWidthStep * range;
        for (int n = 1; n <= maxSections; n *= 2)
        {
            bool ok = true;
            double prev = widthAtFraction(0.0);
            for (int i = 1; i <= n; i++)
            {
                double cur = widthAtFraction((double)i / n);
                if (Math.Abs(cur - prev) > toleranceAbs) { ok = false; break; }
                prev = cur;
            }
            if (ok) return n;
        }
        return maxSections;
    }

    /// <summary>The combined rule (R-tap-1): the larger of the two criteria above.</summary>
    public static int Resolve(double lengthMeters, double freqHz, double eeffMax,
        Func<double, double> widthAtFraction, int maxSections = 4096)
        => Math.Max(
            ElectricalSectionCount(lengthMeters, freqHz, eeffMax),
            GeometricSectionCount(widthAtFraction, maxSections));

    /// <summary>
    /// R-mk-4/R-mk-5 (brief-mklopf-performance-and-messages.md): non-uniform section PLACEMENT —
    /// returns <paramref name="n"/>+1 boundary positions t∈[0,1] spaced at equal Δ(ln Z) rather
    /// than equal Δ(arc fraction) or equal ΔW, so section density follows the profile's OWN
    /// steepness. A small reflection scales with Δ(ln Z)/2 (R-mk-5), so bounding Δ(ln Z) bounds
    /// each section's own contribution to the discretization error directly — ΔW is only a proxy
    /// for it, and a poor one wherever dZ/dW is small. <paramref name="impedanceAtFraction"/> is the
    /// profile's own Z(t), assumed monotonic between its own endpoints (true for any physical
    /// impedance-transforming taper) — inverted per boundary via bisection (cheap; this runs once
    /// per resolved section count, never per frequency point).
    ///
    /// Generic over any profile exposing an impedance-as-a-function-of-position, so a FUTURE
    /// non-uniform-Z taper can reuse this unmodified — not built as a Klopfenstein-only method,
    /// even though <see cref="CircuitRF.Core.Devices.MicrostripKlopfModel"/> is the only caller in
    /// this pass (MTaper's own width-linear profile is not re-placed through this method here —
    /// see <see cref="CircuitRF.Core.Devices.MicrostripTaperModel"/>'s own doc comment for why).
    /// </summary>
    public static double[] NonUniformBoundaries(Func<double, double> impedanceAtFraction, int n,
        int bisectionIterations = 60)
    {
        double zAt0 = impedanceAtFraction(0.0), zAt1 = impedanceAtFraction(1.0);
        double lnZ0 = Math.Log(zAt0), lnZ1 = Math.Log(zAt1);
        bool increasing = zAt1 > zAt0;

        var boundaries = new double[n + 1];
        boundaries[0] = 0.0;
        boundaries[n] = 1.0;
        for (int j = 1; j < n; j++)
        {
            double targetZ = Math.Exp(lnZ0 + (lnZ1 - lnZ0) * j / n);
            double lo = 0.0, hi = 1.0;
            for (int iter = 0; iter < bisectionIterations; iter++)
            {
                double mid = 0.5 * (lo + hi);
                double zMid = impedanceAtFraction(mid);
                bool tooLow = increasing ? zMid < targetZ : zMid > targetZ;
                if (tooLow) lo = mid; else hi = mid;
            }
            boundaries[j] = 0.5 * (lo + hi);
        }
        return boundaries;
    }
}
