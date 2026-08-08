namespace CircuitRF.WBond;

/// <summary>
/// The profile view's clutter answer: one editable curve per array plus a translucent min/max band
/// over its bound members, with free wires drawn individually (wbond.md §6.2 idea 3; R-wbc-5).
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
    /// <param name="MinHeightNm">Lowest height above the chord among the bound members, nanometres.</param>
    /// <param name="MaxHeightNm">Highest.</param>
    public readonly record struct Band(double Span, double MinHeightNm, double MaxHeightNm);

    /// <summary>What the profile view should draw for one array.</summary>
    /// <param name="ArrayName">The array this describes.</param>
    /// <param name="ProfileName">The loop profile its bound members follow, if any.</param>
    /// <param name="BoundWires">Indices (within the array) of wires that follow the profile.</param>
    /// <param name="FreeWires">
    /// Indices of wires drawn individually — either detached from the profile, or not profile-editable
    /// at all because their XY path backtracks (§6.2's stated residual limit).
    /// </param>
    /// <param name="Bands">The min/max envelope over the bound members.</param>
    public readonly record struct ArrayProfile(
        string ArrayName,
        string? ProfileName,
        IReadOnlyList<int> BoundWires,
        IReadOnlyList<int> FreeWires,
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

        var bound = new List<int>();
        var free = new List<int>();

        for (int i = 0; i < array.Wires.Count; i++)
        {
            var wire = array.Wires[i];

            bool isBound = array.Profile is not null
                        && string.Equals(wire.ProfileBinding, array.Profile, StringComparison.OrdinalIgnoreCase)
                        && IsProfileEditable(wire);

            if (isBound) bound.Add(i);
            else free.Add(i);
        }

        var bands = bound.Count == 0 ? [] : BuildBands(array, bound, samples);
        return new ArrayProfile(array.Name, array.Profile, bound, free, bands);
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

    private static Band[] BuildBands(WireArray array, List<int> bound, int samples)
    {
        var bands = new Band[samples];

        for (int k = 0; k < samples; k++)
        {
            double span = (double)k / (samples - 1);
            double min = double.MaxValue, max = double.MinValue;

            foreach (int index in bound)
            {
                double h = HeightAt(array.Wires[index], span);
                if (h < min) min = h;
                if (h > max) max = h;
            }

            bands[k] = new Band(span, min, max);
        }

        return bands;
    }

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

    // ---------------------------------------------------------------- binding (R-wbc-6 / D5)

    /// <summary>
    /// Binds a wire to a profile, resampling its points onto it. Its feet are preserved exactly.
    /// </summary>
    public static void Bind(Wire wire, LoopProfile profile)
    {
        ArgumentNullException.ThrowIfNull(wire);
        ArgumentNullException.ThrowIfNull(profile);
        if (wire.Points.Count < 2) return;

        profile.ApplyTo(wire, wire.Points[0], wire.Points[^1]);
    }

    /// <summary>
    /// Detaches a wire from its profile (D5).
    ///
    /// <para><b>The points are left exactly as they are.</b> A binding is a generator, not a
    /// constraint — breaking it must not move the wire, or a user who nudges one vertex would see the
    /// whole wire jump.</para>
    /// </summary>
    public static void Detach(Wire wire)
    {
        ArgumentNullException.ThrowIfNull(wire);
        wire.ProfileBinding = null;
    }

    /// <summary>
    /// Which wires a profile edit would move — what the "N wires detached" toast counts, and what the
    /// editor must warn about before a destructive profile change.
    /// </summary>
    public static IReadOnlyList<Wire> WiresFollowing(WBondDesign design, string profileName)
    {
        ArgumentNullException.ThrowIfNull(design);
        return [.. design.AllWires().Where(w =>
            string.Equals(w.ProfileBinding, profileName, StringComparison.OrdinalIgnoreCase))];
    }
}
