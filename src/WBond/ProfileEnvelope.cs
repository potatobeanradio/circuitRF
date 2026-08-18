namespace CircuitRF.WBond;

/// <summary>
/// The profile view's clutter answer: one editable curve per array plus a translucent min/max band
/// over its members (wbond.md §6.2 idea 3; R-wbc-5).
///
/// <para><b>The band spans EVERY member of the array, unconditionally</b> (owner, 2026-08-18:
/// <i>"I want the envelope rendering to always be the entire envelope for that group."</i>).</para>
///
/// <para>It has been narrowed twice and both narrowings were wrong in the same way — a wire dropped
/// out of the thing that claims to describe its group, as a side effect of an edit that was about
/// something else. First it spanned only the members bound to the array's stored <c>LoopProfile</c>,
/// so detaching a wire removed it. Then, with profiles gone, it spanned only the members that are
/// <see cref="IsProfileEditable"/> — so dragging one point far enough along the span made that wire's
/// XY path backtrack and it silently left the band. <b>Neither had anything to do with the spread the
/// band exists to show.</b></para>
///
/// <para><see cref="IsProfileEditable"/> survives as a REPORT (<see cref="ArrayProfile.NonMonotone"/>)
/// rather than as an exclusion: it is still true that such a wire cannot be drawn against normalised
/// span without self-overlap, and the panel may still want to say so — but it is a member of its
/// group and the band covers it.</para>
///
/// <para><b>Why a band rather than 200 overlaid polylines.</b> The owner asked to limit how many
/// wires the profile view draws. Drawing every member of a 200-wire array is unreadable and slow;
/// drawing only a representative hides the spread, which is exactly what a user needs to see. The
/// band shows the spread in O(1) drawn curves.</para>
///
/// <para>Computed against <b>normalised span</b> (<see cref="WireEdits.ChordParameter"/>) so wires of
/// different length and angle are directly comparable — which is the whole point of §6.2's
/// parameterisation.</para>
/// </summary>
public static class ProfileEnvelope
{
    /// <summary>One sample of the band: the min and max height above the chord at a span position.</summary>
    /// <param name="Span">Normalised position along the XY chord, 0 at the input foot and 1 at the output.</param>
    /// <param name="MinHeightNm">Lowest height above the chord among the drawable members, nanometres.</param>
    /// <param name="MaxHeightNm">Highest.</param>
    public readonly record struct Band(double Span, double MinHeightNm, double MaxHeightNm);

    /// <summary>What the profile view should draw for one array.</summary>
    /// <param name="ArrayName">The array this describes.</param>
    /// <param name="Members">
    /// Every wire of the array the band spans — which is every wire with at least two points, with no
    /// further test (owner, 2026-08-18). Nothing a user can do to a wire removes it from its group's
    /// own envelope.
    /// </param>
    /// <param name="NonMonotone">
    /// A <b>SUBSET</b> of <paramref name="Members"/>, reported and not excluded: the wires whose XY
    /// path backtracks, so they have no monotone span and cannot be drawn against normalised span
    /// without self-overlap (§6.2's stated residual limit). They are still in the band.
    /// </param>
    /// <param name="Bands">The min/max envelope over <paramref name="Members"/>.</param>
    public readonly record struct ArrayProfile(
        string ArrayName,
        IReadOnlyList<int> Members,
        IReadOnlyList<int> NonMonotone,
        IReadOnlyList<Band> Bands);

    /// <summary>
    /// Builds the profile-view description of one array.
    /// </summary>
    /// <param name="samples">
    /// How many span positions to sample the band at. The default is a compromise between a smooth
    /// band and a per-frame cost; this runs on every profile-view redraw.
    /// </param>
    public static ArrayProfile Build(WireArray array, int samples = 33)
    {
        ArgumentNullException.ThrowIfNull(array);
        if (samples < 2) throw new ArgumentOutOfRangeException(nameof(samples), samples, "Need at least 2 samples.");

        var members = new List<int>();
        var nonMonotone = new List<int>();

        for (int i = 0; i < array.Wires.Count; i++)
        {
            // A wire with fewer than two points has no chord to measure a span against, so there is
            // nothing to sample — that is a degenerate wire, not an edit the user made.
            if (array.Wires[i].Points.Count < 2) continue;

            members.Add(i);
            if (!IsProfileEditable(array.Wires[i])) nonMonotone.Add(i);
        }

        var bands = members.Count == 0 ? [] : BuildBands(array, members, samples);
        return new ArrayProfile(array.Name, members, nonMonotone, bands);
    }

    /// <summary>Builds the description for every array of a design.</summary>
    public static IReadOnlyList<ArrayProfile> BuildAll(WBondDesign design, int samples = 33)
    {
        ArgumentNullException.ThrowIfNull(design);
        return [.. design.Arrays.Select(a => Build(a, samples))];
    }

    /// <summary>
    /// Whether a wire can be drawn in the profile view at all.
    ///
    /// <para><b>A wire whose XY path backtracks has a non-monotone span</b> and cannot be drawn
    /// against normalised span without self-overlap. That is legal geometry — it solves correctly —
    /// and it is simply not profile-editable. §6.2 states this residual limit rather than preventing
    /// the geometry, and this is where it is decided.</para>
    ///
    /// <para><b>This does NOT decide band membership</b> (owner, 2026-08-18). It once did, and the
    /// result was a wire leaving its own group's envelope because a point had been dragged past its
    /// neighbour — see this class's own remarks.</para>
    /// </summary>
    public static bool IsProfileEditable(Wire wire)
    {
        ArgumentNullException.ThrowIfNull(wire);
        if (wire.Points.Count < 2) return false;

        var start = wire.Points[0];
        var end = wire.Points[^1];

        double previous = double.NegativeInfinity;
        foreach (var p in wire.Points)
        {
            double s = WireEdits.ChordParameter(start, end, p);
            if (s < previous - 1e-9) return false;   // went backwards along the span
            previous = s;
        }

        return true;
    }

    private static Band[] BuildBands(WireArray array, List<int> members, int samples)
    {
        var spans = SampleSpans(array, members, samples);

        // Every member's height at every sample, kept rather than reduced on the spot: the crossing
        // refinement below needs to know WHICH member is on top, not just how high it is.
        int m = members.Count;
        var height = new double[spans.Count][];

        for (int k = 0; k < spans.Count; k++)
        {
            height[k] = new double[m];
            for (int i = 0; i < m; i++)
                height[k][i] = HeightAt(array.Wires[members[i]], spans[k]);
        }

        var refined = RefineAtCrossings(array, members, spans, height);
        return refined;
    }

    /// <summary>
    /// The min/max envelope, <b>with a sample inserted wherever the member achieving the min or the
    /// max changes</b> (owner, 2026-08-18: <i>"envelope rendering appears a little strange if wires
    /// within the same group have a different number of vertices."</i>).
    ///
    /// <h3>Why the band could show spread no wire has</h3>
    /// <para>Members are piecewise linear and the samples already include every member's own vertices,
    /// so between two consecutive samples every member is a straight line. <b>But the ENVELOPE of a
    /// set of straight lines is not straight — it has a corner wherever two of them cross</b>, and the
    /// band drew a straight line from one sample to the next. So near a crossing the drawn maximum ran
    /// ABOVE every member and the drawn minimum ran BELOW every member: a bulge reporting a spread the
    /// group does not have.</para>
    ///
    /// <para>It is invisible while an array's members share a vertex lattice — they cross only AT
    /// their shared vertices, which are already sampled. <b>Mixed vertex counts interleave two
    /// lattices</b>, so the members cross repeatedly in mid-interval and every one of those crossings
    /// grew a bulge. Measured on a 7-point and a 9-point wire of the same nominal loop: 2,591 nm of
    /// overshoot, at a span that is a vertex of neither.</para>
    ///
    /// <para><b>The crossing is exact, not searched for.</b> Over one interval two members are lines
    /// in the sample parameter, so where the leader changes the two lines meet at
    /// <c>t = (aâ‚€ âˆ’ bâ‚€) / ((aâ‚€ âˆ’ bâ‚€) + (bâ‚ âˆ’ aâ‚))</c> — one division, and the identity of the pair comes
    /// from the argmax that changed. At most one extra sample per interval per edge, so the drawn
    /// curve count is unchanged and the cost stays O(samples Ã— members).</para>
    /// </summary>
    private static Band[] RefineAtCrossings(WireArray array, List<int> members,
                                            List<double> spans, double[][] height)
    {
        int m = members.Count;
        var bands = new List<Band>(spans.Count * 2);

        int ArgMax(int k)
        {
            int best = 0;
            for (int i = 1; i < m; i++) if (height[k][i] > height[k][best]) best = i;
            return best;
        }

        int ArgMin(int k)
        {
            int best = 0;
            for (int i = 1; i < m; i++) if (height[k][i] < height[k][best]) best = i;
            return best;
        }

        Band At(int k) => new(spans[k], height[k].Min(), height[k].Max());

        bands.Add(At(0));

        for (int k = 1; k < spans.Count; k++)
        {
            // Where two members swap places, the exact span at which they do.
            foreach (double t in Crossings(k))
            {
                double span = spans[k - 1] + (spans[k] - spans[k - 1]) * t;

                double lo = double.MaxValue, hi = double.MinValue;
                for (int i = 0; i < m; i++)
                {
                    double h = HeightAt(array.Wires[members[i]], span);
                    if (h < lo) lo = h;
                    if (h > hi) hi = h;
                }

                bands.Add(new Band(span, lo, hi));
            }

            bands.Add(At(k));
        }

        return [.. bands];

        // The crossing parameters within interval (k-1, k), in order. At most two — one for the top
        // edge and one for the bottom — and either may be absent.
        IEnumerable<double> Crossings(int k)
        {
            var found = new List<double>(2);

            Add(ArgMax(k - 1), ArgMax(k));
            Add(ArgMin(k - 1), ArgMin(k));

            found.Sort();
            return found;

            void Add(int before, int after)
            {
                if (before == after) return;

                double a0 = height[k - 1][before], a1 = height[k][before];
                double b0 = height[k - 1][after],  b1 = height[k][after];

                double denominator = (a0 - b0) + (b1 - a1);
                if (Math.Abs(denominator) < 1e-12) return;   // parallel: they never actually meet

                double t = (a0 - b0) / denominator;

                // Strictly inside, or the crossing is one of the two samples already recorded.
                if (t > 1e-9 && t < 1.0 - 1e-9) found.Add(t);
            }
        }
    }

    /// <summary>
    /// Where the band is sampled: a uniform ladder <b>plus every member's own vertices</b>
    /// (owner, 2026-08-16).
    ///
    /// <para><b>Uniform samples alone cut every corner.</b> A member's height is piecewise linear
    /// between its own points, so a band edge that steps over a vertex joins the two samples on
    /// either side with a straight line and misses the corner between them. Drawn as a translucent
    /// fill that was invisible; drawn with an OUTLINE it is a second line diverging from the wire at
    /// exactly the place two segments join — which is the report. Sampling at the vertices makes the
    /// band's edge follow the same polyline the wires do, by construction rather than by having
    /// enough samples.</para>
    ///
    /// <para>Duplicates are collapsed with a tolerance, so an array whose members share their vertex
    /// positions — the ordinary case — does not sample the same span N times.</para>
    /// </summary>
    private static List<double> SampleSpans(WireArray array, List<int> members, int samples)
    {
        var spans = new List<double>(samples + members.Count * 8);

        for (int k = 0; k < samples; k++) spans.Add((double)k / (samples - 1));

        foreach (int index in members)
        {
            var wire = array.Wires[index];
            if (wire.Points.Count < 2) continue;

            var start = wire.Points[0];
            var end = wire.Points[^1];

            // The feet are already the ladder's own 0 and 1; only the interior vertices are new.
            for (int i = 1; i < wire.Points.Count - 1; i++)
            {
                double s = WireEdits.ChordParameter(start, end, wire.Points[i]);
                if (s > 0.0 && s < 1.0) spans.Add(s);
            }
        }

        spans.Sort();

        var unique = new List<double>(spans.Count) { spans[0] };
        for (int i = 1; i < spans.Count; i++)
            if (spans[i] - unique[^1] > SampleSpanTolerance) unique.Add(spans[i]);

        // The band must reach BOTH feet. The dedupe keeps the first of a cluster and drops the rest,
        // so a member vertex sitting a hair short of 1.0 would swallow the ladder's own final sample
        // and the band would stop short of the output foot — visible as an envelope that does not
        // close on the pad. The endpoints are the one pair of samples that are not negotiable.
        if (unique[^1] < 1.0) unique[^1] = 1.0;

        return unique;
    }

    /// <summary>
    /// How close two sample positions have to be to count as one. A thousandth of the chord — far
    /// finer than any corner a reader can see, and coarse enough that two members whose vertices
    /// differ only in the last bits of a division do not double the sample count.
    /// </summary>
    private const double SampleSpanTolerance = 1e-3;

    /// <summary>
    /// A wire's height above its chord at a normalised span position, linearly interpolated between
    /// its own points.
    /// </summary>
    public static double HeightAt(Wire wire, double span)
    {
        ArgumentNullException.ThrowIfNull(wire);
        if (wire.Points.Count < 2) return 0.0;

        var start = wire.Points[0];
        var end = wire.Points[^1];

        double previousSpan = 0.0, previousHeight = 0.0;

        for (int i = 0; i < wire.Points.Count; i++)
        {
            var p = wire.Points[i];
            double s = WireEdits.ChordParameter(start, end, p);
            double chordZ = start.Z + (end.Z - start.Z) * s;
            double h = p.Z - chordZ;

            if (i == 0) { previousSpan = s; previousHeight = h; continue; }

            if (span <= s)
            {
                double width = s - previousSpan;
                double t = width <= 0.0 ? 0.0 : (span - previousSpan) / width;
                return previousHeight + (h - previousHeight) * t;
            }

            previousSpan = s;
            previousHeight = h;
        }

        return previousHeight;
    }
}
