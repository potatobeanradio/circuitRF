namespace CircuitRF.Core.Matching;

/// <summary>
/// The band set a multiband design is actually synthesised to (match.md §18.3).
/// </summary>
/// <remarks>
/// <b>A real network's <c>|Gamma(jOmega)|^2</c> is an even function of Omega</b>, so the two bands a
/// resonated dual-band ladder produces are mirror images about omega0 in LOG frequency: <c>f1*f4 =
/// f2*f3</c>, equivalently equal ratio bandwidths <c>f2/f1 = f4/f3</c>. Four frequencies a user types
/// will not satisfy that, and designing to a spec the user did not ask for — silently — is the one
/// thing this must not do. So the requested bands are symmetrised HERE, once, in a pure function, and
/// the Designer shows what came out.
/// </remarks>
/// <param name="F1">Effective lower edge of band 1, Hz.</param>
/// <param name="F2">Effective upper edge of band 1, Hz.</param>
/// <param name="F3">Effective lower edge of band 2, Hz.</param>
/// <param name="F4">Effective upper edge of band 2, Hz.</param>
/// <param name="Widened">True when symmetrisation moved an edge.</param>
/// <param name="WidenedBand">1 or 2 — which band was widened; 0 when neither was, and for tri-band,
/// where <see cref="Note"/> names every band that moved instead.</param>
/// <param name="Note">The one sentence the Designer shows, or null when nothing moved.</param>
/// <param name="F5">Effective lower edge of band 3, Hz. Tri-band only.</param>
/// <param name="F6">Effective upper edge of band 3, Hz. Tri-band only.</param>
/// <param name="Count">How many bands this describes — 1, 2 or 3.</param>
public sealed record EffectiveBands(
    double F1, double F2, double F3, double F4, bool Widened, int WidenedBand, string? Note,
    double F5 = 0.0, double F6 = 0.0, int Count = 2)
{
    /// <summary>
    /// The band centre the ladder resonates at, rad/s.
    /// </summary>
    /// <remarks>
    /// <b>The MIDDLE band's centre for tri-band</b> (match.md §18.3: the middle band is kept and
    /// defines omega0), and the outer pair's for one or two bands. The two agree by construction —
    /// symmetrisation makes <c>f1'.f6' = f2'.f5' = f3.f4</c> exactly — so this reads the middle band,
    /// which is the pair the user typed and did not have moved.
    /// </remarks>
    public double Omega0 => 2.0 * Math.PI * Math.Sqrt(Count >= 3 ? F3 * F4 : F1 * F4);

    /// <summary>The OUTER fractional bandwidth, <c>(f_high - f_low)/f0</c>.</summary>
    public double W => (Outer.Hi - Outer.Lo) / Math.Sqrt(Count >= 3 ? F3 * F4 : F1 * F4);

    /// <summary>The outermost pair of edges — (F1, F4) for one or two bands, (F1, F6) for three.</summary>
    public (double Lo, double Hi) Outer => (F1, Count >= 3 ? F6 : F4);

    /// <summary>
    /// True when a tri-band set's widened outer bands have run into the middle one — a refusal.
    /// </summary>
    /// <remarks>
    /// Only <c>F2 &lt; F3</c> needs testing, because <c>F2.F5 = F3.F4</c> after symmetrisation makes
    /// <c>F4 &lt; F5</c> the same statement; both are asked anyway, since a spec that is not yet
    /// increasing reaches here mid-edit and neither should throw.
    /// </remarks>
    public bool Overlaps => Count >= 3 && (!(F2 < F3) || !(F4 < F5));

    /// <summary>
    /// The passband as a union of intervals in the prototype variable <c>u = Omega^2</c>.
    /// </summary>
    /// <remarks>
    /// <b>Every edge maps through the SAME expression</b>, which is what makes this three lines:
    /// <c>Omega(f) = (f/f0 - f0/f)/w</c>, and the mirror relations <c>f1.f4 = f2.f3</c> (dual) and
    /// <c>f1.f6 = f2.f5 = f3.f4</c> (tri) collapse it to a ratio of frequency DIFFERENCES over the
    /// outer span — no square roots and no cancellation.
    /// <list type="bullet">
    /// <item><b>One band</b>: <c>[0, 1]</c>. Nothing in the single-band path reads this; it is here so
    /// the property is total.</item>
    /// <item><b>Two bands</b>: <c>[a^2, 1]</c> with <c>a = (F3 - F2)/(F4 - F1)</c> — match.md §18.2's
    /// band-shifted family, the gap becoming the prototype's own stopband below a.</item>
    /// <item><b>Three bands</b>: <c>[0, a^2] u [b^2, 1]</c> with <c>a = (F4 - F3)/(F6 - F1)</c> and
    /// <c>b = (F5 - F2)/(F6 - F1)</c> — §18.5's union, the middle band straddling Omega = 0.</item>
    /// </list>
    /// </remarks>
    public IReadOnlyList<(double Lo, double Hi)> Intervals
    {
        get
        {
            if (Count <= 1) return [(0.0, 1.0)];
            double span = Outer.Hi - Outer.Lo;
            if (!(span > 0)) return [(0.0, 1.0)];
            if (Count == 2)
            {
                double a2 = (F3 - F2) / span;
                return [(a2 * a2, 1.0)];
            }
            double a = (F4 - F3) / span, b = (F5 - F2) / span;
            return [(0.0, a * a), (b * b, 1.0)];
        }
    }
}

/// <summary>match.md §18.3's symmetrisation: keep the wider band, widen the narrower one AWAY from
/// the gap.</summary>
public static class MatchBands
{
    /// <summary>Relative slack below which two ratio bandwidths count as already mirrored.</summary>
    /// <remarks>
    /// A user who types a mirrored spec (or one that came back out of a previous widening and was
    /// stored) must get <c>Widened = false</c> and no note; a rounding difference in the last bit is
    /// not a widening and a note about it would be noise.
    /// </remarks>
    private const double MirrorTolerance = 1e-9;

    /// <summary>
    /// The bands the synthesis will design to, given the four requested edges.
    /// </summary>
    /// <remarks>
    /// <b>Away from the gap, not toward it.</b> The gap is where the Fano budget is saved (§18.1);
    /// widening toward it would spend budget on frequencies the user did not ask for AND shrink the
    /// reclaim, so the narrower band grows outward — band 1 downward, band 2 upward.
    ///
    /// <para><b>Ordering is not validated here.</b> <c>0 &lt; f1 &lt; f2 &lt; f3 &lt; f4</c> is a
    /// REFUSAL in the synthesis, carrying its four numbers, and this function is called by the design
    /// record's derived properties on every access — including while a user is halfway through typing
    /// a second band. It returns the inputs unchanged rather than throwing on a spec that is not yet
    /// a spec.</para>
    /// </remarks>
    public static EffectiveBands Symmetrise(double f1, double f2, double f3, double f4)
    {
        if (!(f1 > 0) || !(f2 > f1) || !(f3 > f2) || !(f4 > f3))
            return new EffectiveBands(f1, f2, f3, f4, false, 0, null);

        double r1 = f2 / f1, r2 = f4 / f3;
        if (Math.Abs(r1 - r2) <= MirrorTolerance * Math.Max(r1, r2))
            return new EffectiveBands(f1, f2, f3, f4, false, 0, null);

        if (r2 > r1)
        {
            // Band 1 is the narrower: widen it DOWNWARD to band 2's ratio.
            double f1New = f2 / r2;
            double centre = Math.Sqrt(f1New * f4);
            return new EffectiveBands(
                f1New, f2, f3, f4, true, 1, Note(1, f1New, f2, centre));
        }

        // Band 2 is the narrower: widen it UPWARD.
        double f4New = f3 * r1;
        double c = Math.Sqrt(f1 * f4New);
        return new EffectiveBands(f1, f2, f3, f4New, true, 2, Note(2, f3, f4New, c));
    }

    /// <summary>
    /// match.md §18.3/§18.5's tri-band rule: <b>keep the middle band, and widen each outer band to
    /// cover both itself and the log-mirror of its partner.</b>
    /// </summary>
    /// <remarks>
    /// <b>The middle band is the one that cannot move</b>, because it is what defines omega0: a
    /// three-band response is symmetric about its own centre in log frequency, and the only band that
    /// can straddle that centre is the middle one. So <c>f0^2 = f3.f4</c> is fixed and the outer pair
    /// is stretched onto the mirror it has to satisfy —
    /// <c>f1' = min(f1, f0^2/f6)</c>, <c>f2' = max(f2, f0^2/f5)</c>,
    /// <c>f5' = min(f5, f0^2/f2)</c>, <c>f6' = max(f6, f0^2/f1)</c>.
    ///
    /// <para><b>This is a UNION with the mirror, not the dual-band rule's "widen away from the
    /// gap".</b> With two bands either one may be kept and only one edge moves; with three, band 1 and
    /// band 3 constrain each other from both sides at once, so each is widened to cover its partner's
    /// image — which can move an inner edge TOWARD a gap. The alternative would be to shrink the
    /// wider band, and shrinking silently designs to LESS than the user asked for, which is the one
    /// thing §18.3 forbids. Widening over-delivers, and the note says where.</para>
    ///
    /// <para>The four expressions above always leave <c>f1'.f6' = f2'.f5' = f0^2</c> exactly, because
    /// in each pair precisely one side is the mirror of the other (whichever comparison the min/max
    /// took, the partner's took the opposite). That identity is what
    /// <see cref="EffectiveBands.Intervals"/> and <see cref="EffectiveBands.Overlaps"/> rest on.</para>
    ///
    /// <para><b>A widened outer band that reaches the middle one is not resolved here.</b> It is a
    /// refusal, and the refusal belongs to the synthesis, which has the design to name — see
    /// <see cref="EffectiveBands.Overlaps"/>. As with <see cref="Symmetrise"/>, an ordering that is
    /// not yet a spec comes back unchanged rather than throwing.</para>
    /// </remarks>
    public static EffectiveBands Symmetrise3(
        double f1, double f2, double f3, double f4, double f5, double f6)
    {
        if (!(f1 > 0) || !(f2 > f1) || !(f3 > f2) || !(f4 > f3) || !(f5 > f4) || !(f6 > f5))
            return new EffectiveBands(f1, f2, f3, f4, false, 0, null, f5, f6, 3);

        double f0Sq = f3 * f4;
        double n1 = Math.Min(f1, f0Sq / f6), n2 = Math.Max(f2, f0Sq / f5);
        double n5 = Math.Min(f5, f0Sq / f2), n6 = Math.Max(f6, f0Sq / f1);

        bool moved1 = Moved(n1, f1) || Moved(n2, f2);
        bool moved3 = Moved(n5, f5) || Moved(n6, f6);
        if (!moved1 && !moved3)
            return new EffectiveBands(f1, f2, f3, f4, false, 0, null, f5, f6, 3);

        double centre = Math.Sqrt(f0Sq);
        var parts = new List<string>(2);
        if (moved1) parts.Add($"band 1 to {Ghz(n1)}–{Ghz(n2)} GHz");
        if (moved3) parts.Add($"band 3 to {Ghz(n5)}–{Ghz(n6)} GHz");

        string note =
            $"Widened {string.Join(" and ", parts)} to mirror about {Ghz(centre)} GHz "
            + "(band 2 is kept and sets the centre).";

        return new EffectiveBands(n1, n2, f3, f4, true, moved1 && moved3 ? 0 : (moved1 ? 1 : 3),
                                  note, n5, n6, 3);
    }

    private static bool Moved(double now, double was) =>
        Math.Abs(now - was) > MirrorTolerance * Math.Max(now, was);

    private static string Note(int band, double lo, double hi, double centre) =>
        $"Band {band} widened to {Ghz(lo)}–{Ghz(hi)} GHz to mirror band "
        + $"{(band == 1 ? 2 : 1)} about {Ghz(centre)} GHz.";

    /// <summary>Three significant figures in GHz — the register match.md §18.7's sentence is in.</summary>
    private static string Ghz(double hz) =>
        (hz / 1e9).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
