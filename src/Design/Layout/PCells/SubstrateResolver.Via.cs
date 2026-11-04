namespace CircuitRF.Design.Layout.PCells;

/// <summary>One ground plane a via's barrel passes through a clearance in, and the dielectric its
/// capacitance is taken over (brief-via-component.md R-viac-2).</summary>
/// <param name="Name">The plane's stackup conductor.</param>
/// <param name="ThicknessMeters">The length of barrel this plane owns: half the dielectric to the
/// neighbouring conductor on each side, inside the drill. A plane at the very end of a drill has one
/// side only.</param>
/// <param name="RelativePermittivity">That dielectric's εr, thickness-weighted.</param>
public sealed record CrossedPlane(string Name, double ThicknessMeters, double RelativePermittivity);

/// <summary>A length of barrel running on past the conductor a terminal lands on, to the end of the
/// drill: an open stub.</summary>
/// <param name="EndConductorName">The conductor the drill ends on.</param>
/// <param name="LengthMeters">Mid-plane to mid-plane, as the barrel itself is measured.</param>
/// <param name="Planes">The planes the stub passes.</param>
public sealed record ViaStub(string EndConductorName, double LengthMeters, IReadOnlyList<CrossedPlane> Planes);

/// <summary>
/// Everything a via's electrical model needs, resolved from a technology's stackup between two
/// conductors (brief-via-component.md R-viac-2). The same numbers reach the schematic's model and the
/// parameter editor's readout, so neither can compute a different via from the other.
/// </summary>
/// <param name="From">The conductor terminal A lands on.</param>
/// <param name="To">The conductor terminal B lands on — for a via to ground, the ground plane.</param>
/// <param name="ViaEntry">The stackup's via entry whose drill this is, or null when no entry spans the
/// two conductors (the barrel is then taken as exactly From to To, and a warning says so).</param>
/// <param name="LengthMeters">h: the z-distance between the two conductors' mid-planes (the brief's D2).</param>
/// <param name="Planes">Every ground plane strictly between From and To.</param>
/// <param name="StubBeyondTo">The drill past To, or null.</param>
/// <param name="StubBeyondFrom">The drill past From, on the other side, or null.</param>
/// <param name="ConductivitySPerM">The barrel's σ: the via entry's own, else From's copper.</param>
/// <param name="Solid">The via entry is filled.</param>
/// <param name="PlatingMeters">The via entry's wall thickness, else the plated default.</param>
/// <param name="PlatingDefaulted">True when <paramref name="PlatingMeters"/> is the default.</param>
/// <param name="MaxRelativePermittivity">εr of the densest dielectric the whole drill crosses.</param>
/// <param name="DielectricThicknessMeters">The dielectric between From and To alone (no conductor
/// thickness) — what a pad-to-plane capacitance is taken over.</param>
/// <param name="DielectricPermittivity">Its εr, thickness-weighted.</param>
/// <param name="DrillMeters">The technology's default drill, or null when it states none.</param>
/// <param name="PadMeters">The technology's default pad, or null when it states none.</param>
public sealed record ResolvedViaSpan(
    StackupLayer From,
    StackupLayer To,
    StackupLayer? ViaEntry,
    double LengthMeters,
    IReadOnlyList<CrossedPlane> Planes,
    ViaStub? StubBeyondTo,
    ViaStub? StubBeyondFrom,
    double ConductivitySPerM,
    bool Solid,
    double PlatingMeters,
    bool PlatingDefaulted,
    double MaxRelativePermittivity,
    double DielectricThicknessMeters,
    double DielectricPermittivity,
    double? DrillMeters,
    double? PadMeters)
{
    /// <summary>The drawing layer the barrel is drawn on — the via entry's, when there is one.</summary>
    public LayerKey? DrillLayer => ViaEntry is { DrawingLayers.Count: > 0 } e ? e.DrawingLayers[0] : null;
}

public static partial class SubstrateResolver
{
    /// <summary>The copper a via's barrel defaults to when neither the via entry nor the conductor it
    /// starts on states one.</summary>
    private const double CopperSigma = 5.8e7;

    /// <summary>
    /// Resolves a via between two conductors of <paramref name="technology"/> (brief-via-component.md
    /// R-viac-2) — the barrel length, the planes it passes, the stub past either end, its materials.
    ///
    /// <para><b>Defaults.</b> An unnamed <paramref name="fromName"/> is the topmost conductor, as a
    /// microstrip's signal layer is. An unnamed <paramref name="toName"/> is, for a via to ground
    /// (<paramref name="toGround"/>), the nearest ground-designated conductor beneath From (else above);
    /// for a signal via, the farthest conductor the stackup's drills reach from From that is NOT a
    /// ground plane, or the farthest one when every one is. A name that is not a conductor reports and
    /// falls back to that default, as <see cref="ResolveElectrical"/> does. A defaulted From that no
    /// drill joins to To moves to the conductor nearest it that a drill reaching To starts on.</para>
    ///
    /// <para><b>Which drill.</b> The via entry whose span covers both conductors, the shortest such span
    /// first: a blind via beats a through via for the same pair, as a fab would drill it. Whatever the
    /// drill runs on past either conductor is a stub. With no entry covering both, the barrel is taken
    /// as exactly From to To and a warning says so.</para>
    /// </summary>
    public static (ResolvedViaSpan? Span, SubstrateResolutionFailure? Failure, IReadOnlyList<string> Warnings) ResolveViaSpan(
        Technology? technology, string? fromName, string? toName, bool toGround)
    {
        var warn = new List<string>();
        if (technology is null)
            return (null, new SubstrateResolutionFailure("no technology resolved for this document"), warn);

        var layers = technology.Stackup.Layers;
        var conductors = layers.Where(l => l.Kind == StackupKind.Conductor).ToList();
        if (conductors.Count < 2)
            return (null, new SubstrateResolutionFailure(
                $"technology '{technology.Name}' has fewer than two conductors, so there is nothing for a via to join"), warn);

        var from = FindSignalConductor(technology, fromName, warn)!;
        int iFrom = layers.IndexOf(from);

        StackupLayer? to = null;
        if (toName is { Length: > 0 } tn)
        {
            to = conductors.FirstOrDefault(c => string.Equals(c.Name, tn, StringComparison.OrdinalIgnoreCase));
            if (to is null)
                warn.Add($"no conductor named '{tn}' in technology '{technology.Name}' — falling back to the default " +
                         (toGround ? "ground plane" : "far layer"));
        }
        to ??= toGround
            ? FindNearestGroundBeneath(technology.Stackup, iFrom) ?? FindNearestGroundAbove(technology.Stackup, iFrom)
            : DefaultFarConductor(technology, from, conductors);

        if (to is null)
            return (null, new SubstrateResolutionFailure(
                $"technology '{technology.Name}' has no ground-designated conductor for a via from '{from.Name}' to land on " +
                "(mark a conductor StackupLayer.IsGroundReference, or name one in GroundLayer)"), warn);
        if (ReferenceEquals(to, from))
            return (null, new SubstrateResolutionFailure(
                $"a via from '{from.Name}' to itself joins nothing; choose a different {(toGround ? "GroundLayer" : "ToLayer")}"), warn);

        int iTo = layers.IndexOf(to);

        // An UNNAMED From that no drill joins to To starts where a drill to To does, nearest the top:
        // a GaAs die's backside via lands on Metal1, not on the air-bridge metal above it, and the
        // topmost-conductor default would otherwise open every placed VIAGND with a warning.
        bool fromDefaulted = !conductors.Any(c => string.Equals(c.Name, fromName, StringComparison.OrdinalIgnoreCase));
        if (fromDefaulted && !ViaEntries(technology).Any(e => e.Top <= Math.Min(iFrom, iTo) && e.Bottom >= Math.Max(iFrom, iTo)))
        {
            var start = ViaEntries(technology)
                .Where(e => e.Top <= iTo && iTo <= e.Bottom)
                .Select(e => iTo > iFrom ? e.Top : e.Bottom)
                .Where(i => i != iTo)
                .OrderBy(i => Math.Abs(i - iFrom))
                .Select(i => (int?)i)
                .FirstOrDefault();
            if (start is { } s) (from, iFrom) = (layers[s], s);
        }

        int lo = Math.Min(iFrom, iTo), hi = Math.Max(iFrom, iTo);

        // The drill: the shortest via entry covering both conductors.
        StackupLayer? entry = null;
        int drillLo = lo, drillHi = hi;
        foreach (var (e, top, bottom) in ViaEntries(technology))
        {
            if (top > lo || bottom < hi) continue;
            if (entry is null || bottom - top < drillHi - drillLo)
                (entry, drillLo, drillHi) = (e, top, bottom);
        }
        if (entry is null)
            warn.Add($"no via layer in technology '{technology.Name}' spans '{from.Name}' to '{to.Name}'; " +
                     "the barrel is taken as exactly that span, with no stub");

        var z = MidPlanes(layers);
        double h = Math.Abs(z[iTo] - z[iFrom]);

        var planes = PlanesBetween(layers, lo, hi, lo, hi);

        // Stubs: the drill beyond To and beyond From, in the direction away from the other terminal.
        bool downward = iTo > iFrom;
        ViaStub? Stub(int terminal, bool beyondIsBelow)
        {
            int end = beyondIsBelow ? drillHi : drillLo;
            if (end == terminal) return null;
            int a = Math.Min(terminal, end), b = Math.Max(terminal, end);
            var sp = PlanesBetween(layers, a, b, a, b, includeEnd: end);
            return new ViaStub(layers[end].Name, Math.Abs(z[end] - z[terminal]), sp);
        }
        var stubTo = Stub(iTo, beyondIsBelow: downward);
        var stubFrom = Stub(iFrom, beyondIsBelow: !downward);

        double sigma = entry is { SigmaSm: > 0 } ? entry.SigmaSm : from.SigmaSm > 0 ? from.SigmaSm : CopperSigma;
        bool solid = entry?.Fill == ViaFillKind.Solid;
        bool platingDefaulted = entry?.WallThicknessDbu is not > 0;
        double plating = platingDefaulted
            ? (double)ViaDefaults.PlatedWallThicknessUm * 1e-6
            : DbuToMeters(entry!.WallThicknessDbu!.Value, FallbackDbuPerMicron);

        double erMax = 1.0;
        for (int i = drillLo + 1; i < drillHi; i++)
            if (layers[i].Kind == StackupKind.Dielectric) erMax = Math.Max(erMax, layers[i].Epsr);

        var (tDiel, erDiel) = Dielectric(layers, lo, hi);

        double? drill = technology.DefaultViaDrillDbu > 0 ? DbuToMeters(technology.DefaultViaDrillDbu, FallbackDbuPerMicron) : null;
        double? pad = technology.DefaultViaPadDbu > 0 ? DbuToMeters(technology.DefaultViaPadDbu, FallbackDbuPerMicron) : null;

        return (new ResolvedViaSpan(from, to, entry, h, planes, stubTo, stubFrom, sigma, solid, plating,
            platingDefaulted, erMax, tDiel, erDiel, drill, pad), null, warn);
    }

    /// <summary>Every via entry with the stackup indices of the two conductors it joins, top first. An
    /// entry naming no span goes through every conductor, as everywhere else in the layout. A NON-PLATED entry is not
    /// one: its holes join nothing (designer feedback round 34 — a schematic's ground via landed on an imported board's
    /// "Drill (non-plated)" layer, the first of two spanless entries, and grounded nothing).</summary>
    private static IEnumerable<(StackupLayer Entry, int Top, int Bottom)> ViaEntries(Technology technology)
    {
        var layers = technology.Stackup.Layers;
        int firstConductor = layers.FindIndex(l => l.Kind == StackupKind.Conductor);
        int lastConductor = layers.FindLastIndex(l => l.Kind == StackupKind.Conductor);
        foreach (var e in layers.Where(l => l.Kind == StackupKind.Via && l.Plated != false))
        {
            if (e.SpanFromLayer is null && e.SpanToLayer is null)
            {
                yield return (e, firstConductor, lastConductor);
                continue;
            }
            if (ViaSpanResolver.Resolve(e, technology) is { } span)
                yield return (e, layers.IndexOf(span.Top), layers.IndexOf(span.Bottom));
        }
    }

    /// <summary>A signal via's default far conductor: of every conductor some drill reaches from
    /// <paramref name="from"/>, the farthest that is not a ground plane, else the farthest.</summary>
    private static StackupLayer? DefaultFarConductor(Technology technology, StackupLayer from, List<StackupLayer> conductors)
    {
        var layers = technology.Stackup.Layers;
        int iFrom = layers.IndexOf(from);
        var reach = ViaEntries(technology).Where(e => e.Top <= iFrom && iFrom <= e.Bottom).ToList();
        int top = reach.Count > 0 ? reach.Min(e => e.Top) : layers.IndexOf(conductors[0]);
        int bottom = reach.Count > 0 ? reach.Max(e => e.Bottom) : layers.IndexOf(conductors[^1]);

        // The side with room: below, unless From is already the bottom of the reach.
        bool below = bottom > iFrom;
        var candidates = conductors
            .Where(c => !ReferenceEquals(c, from))
            .Select(c => (c, i: layers.IndexOf(c)))
            .Where(t => below ? t.i > iFrom && t.i <= bottom : t.i < iFrom && t.i >= top)
            .OrderByDescending(t => Math.Abs(t.i - iFrom))
            .ToList();
        return candidates.FirstOrDefault(t => !t.c.IsGroundReference).c ?? candidates.FirstOrDefault().c;
    }

    /// <summary>The z of every layer's mid-plane, measured down from the top of the stack (m).</summary>
    private static double[] MidPlanes(List<StackupLayer> layers)
    {
        var z = new double[layers.Count];
        double top = 0;
        for (int i = 0; i < layers.Count; i++)
        {
            double t = layers[i].Kind == StackupKind.Via ? 0 : DbuToMeters(layers[i].ThicknessDbu, FallbackDbuPerMicron);
            z[i] = top + t / 2;
            top += t;
        }
        return z;
    }

    /// <summary>
    /// The ground planes strictly between stackup indices <paramref name="a"/> and <paramref name="b"/>
    /// (and the one at <paramref name="includeEnd"/>, a drill's end), each with the length of barrel it
    /// OWNS: half the dielectric to the neighbouring conductor on each side, inside
    /// [<paramref name="clipLo"/>, <paramref name="clipHi"/>].
    ///
    /// <para><b>Why half, per side.</b> Johnson and Graham's <c>T</c> is a barrel length — their C grows
    /// with it — measured over a whole board of planes. Giving each plane the barrel between the
    /// midpoints to its neighbours partitions the barrel among the planes it passes, so on the
    /// many-plane board the formula was written for the terms sum back to the board thickness, and a
    /// single plane is not charged for barrel that runs past other conductors. Measured against a
    /// Palace solve of one via through one plane (testdata/em3d/f0/B-via), this reading puts ∠S21
    /// within 0.2° to 6 GHz; taking each plane's whole neighbouring dielectric instead was 4° out.</para>
    /// </summary>
    private static List<CrossedPlane> PlanesBetween(List<StackupLayer> layers, int a, int b, int clipLo, int clipHi,
                                                    int includeEnd = -1)
    {
        var planes = new List<CrossedPlane>();
        for (int p = a; p <= b; p++)
        {
            bool inside = p > a && p < b;
            if (!inside && p != includeEnd) continue;
            var layer = layers[p];
            if (layer.Kind != StackupKind.Conductor || !layer.IsGroundReference) continue;

            int above = p, below = p;
            for (int i = p - 1; i >= clipLo; i--) { above = i; if (layers[i].Kind == StackupKind.Conductor) break; }
            for (int i = p + 1; i <= clipHi; i++) { below = i; if (layers[i].Kind == StackupKind.Conductor) break; }
            var (tA, erA) = Dielectric(layers, above, p);
            var (tB, erB) = Dielectric(layers, p, below);
            double t = (tA + tB) / 2;
            if (t > 0) planes.Add(new CrossedPlane(layer.Name, t, (tA * erA + tB * erB) / (tA + tB)));
        }
        return planes;
    }

    /// <summary>The dielectric thickness between two stackup indices, exclusive, and its thickness-
    /// weighted εr.</summary>
    private static (double Thickness, double EpsR) Dielectric(List<StackupLayer> layers, int a, int b)
    {
        long t = 0;
        double weighted = 0;
        for (int i = Math.Min(a, b) + 1; i < Math.Max(a, b); i++)
        {
            if (layers[i].Kind != StackupKind.Dielectric) continue;
            t += layers[i].ThicknessDbu;
            weighted += layers[i].Epsr * layers[i].ThicknessDbu;
        }
        return t > 0 ? (DbuToMeters(t, FallbackDbuPerMicron), weighted / t) : (0, 1);
    }
}
