// Where each conductor sits in z, and what the dielectric separation between two of them is
// (docs/sonnet-briefs/brief-railrf-13-distributed.md R-rail13-1 / R-rail13-5, railrf.md §4.1, §4.3).
//
// ── ONE READING OF THE STACKUP'S Z AXIS ────────────────────────────────────────────────────────
//
// §4.1's `h` and §4.3's via lengths are the same measurement taken between different pairs of
// conductors, and both are the quantity §4.3 says the form factor actually moves. Two readings of
// the stackup that disagreed about it would make the mesh's inductance and the mounting loop's
// inductance describe different boards — on the same run, with nothing to say so.
//
// A VIA ENTRY OCCUPIES NO Z. The stackup lists conductors and dielectrics in order and a via entry
// is a declaration ABOUT a span rather than a layer of the sandwich; PdnAssembly's own z walk skips
// it for the same reason, and this file is the shared version of that walk.

namespace CircuitRF.Design.Layout.Pdn;

/// <summary>One conductor's place in the sandwich, in metres from the top of the stackup.</summary>
/// <param name="Name">The stackup entry's own name.</param>
/// <param name="Index">Its ordinal among the conductors, top first.</param>
/// <param name="NearMetres">The z of its upper face.</param>
/// <param name="FarMetres">The z of its lower face.</param>
/// <param name="ThicknessMetres">Its finished copper thickness.</param>
/// <param name="DrawingLayers">The drawing layers it claims.</param>
public sealed record PdnConductorZ(
    string Name, int Index, double NearMetres, double FarMetres, double ThicknessMetres,
    IReadOnlyList<LayerKey> DrawingLayers)
{
    /// <summary>The middle of the copper — what a via's LENGTH is measured between, because a
    /// barrel carries current from one conductor's body to the other's rather than between two
    /// faces nobody can name.</summary>
    public double MidMetres => (NearMetres + FarMetres) / 2.0;
}

/// <summary>The stackup's z axis, read once.</summary>
public static class PdnStackupGeometry
{
    /// <summary>
    /// Every conductor of <paramref name="tech"/>'s stackup, top first, with its z band in metres.
    /// </summary>
    public static IReadOnlyList<PdnConductorZ> Conductors(Technology tech, int dbuPerMicron)
    {
        var list = new List<PdnConductorZ>();
        if (tech is null || dbuPerMicron <= 0) return list;

        double z = 0;
        foreach (var l in tech.Stackup.Layers)
        {
            if (l.Kind == StackupKind.Via) continue;

            double t = l.ThicknessDbu / (double)dbuPerMicron * 1e-6;
            if (l.Kind == StackupKind.Conductor)
                list.Add(new PdnConductorZ(l.Name, list.Count, z, z + t, t, l.DrawingLayers));
            z += t;
        }

        return list;
    }

    /// <summary>The conductor claiming <paramref name="layer"/> as a drawing layer, or null.</summary>
    public static PdnConductorZ? ConductorOf(IReadOnlyList<PdnConductorZ> conductors, LayerKey layer)
    {
        foreach (var c in conductors)
            foreach (var k in c.DrawingLayers)
                if (k == layer) return c;
        return null;
    }

    /// <summary>The conductor of that stackup name, or null.</summary>
    public static PdnConductorZ? ConductorNamed(IReadOnlyList<PdnConductorZ> conductors, string? name) =>
        name is { Length: > 0 }
            ? conductors.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal))
            : null;

    /// <summary>
    /// §4.1's <c>h</c> — the DIELECTRIC separation between two conductors, in metres: the gap
    /// between their facing faces, not the distance between their middles.
    ///
    /// <para><b>The faces, because that is what the field crosses.</b> On a tight four-layer pair
    /// the copper is 35 µm and the dielectric 100 µm, so taking mid-to-mid would report 135 µm and
    /// overstate every plane-pair inductance in the extraction by 35 % — silently, and in the
    /// pessimistic direction, which is the direction nobody investigates.</para>
    ///
    /// <para>Zero where either conductor is missing or where they are the same one: a conductor is
    /// not its own return (the mesh extractor says so in its own words) and a pair with no gap has
    /// no plane-pair inductance to give.</para>
    /// </summary>
    public static double SeparationMetres(PdnConductorZ? a, PdnConductorZ? b)
    {
        if (a is null || b is null || a.Index == b.Index) return 0.0;
        var (upper, lower) = a.Index < b.Index ? (a, b) : (b, a);
        return Math.Max(0.0, lower.NearMetres - upper.FarMetres);
    }

    /// <summary>The z distance a barrel joining two conductors carries current over, in metres —
    /// middle to middle. <b>Not <see cref="SeparationMetres"/></b>: a via runs through the copper
    /// it lands on, and its own length is what sets its partial inductance.</summary>
    public static double SpanMetres(PdnConductorZ? a, PdnConductorZ? b) =>
        a is null || b is null ? 0.0 : Math.Abs(a.MidMetres - b.MidMetres);
}
