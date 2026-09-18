// Barrel resistance from the drill, the span and the plating
// (docs/sonnet-briefs/brief-railrf-3-mesh-extractor.md R-rail3-9 / R-rail3-10, railrf.md §4.2).
//
// ── A VIA IS A READING OF THE ARTWORK, NOT AN APPROXIMATION OF IT ──────────────────────────────
//
// The drill data gives every hole. The stackup's via entries give the layers each span joins and
// carry a plated-wall thickness, which is what sets barrel resistance. The connectivity walk bridges
// layers THROUGH VIA GEOMETRY rather than by assuming the metal above and below overlaps.
//
// Parallel vias fall out as parallel resistances with NO SPECIAL CASE ANYWHERE. A 0.3 mm plated via
// through a 1.6 mm board is about 1.2 mΩ: twenty in parallel is negligible, two is not. And shared
// return vias are handled naturally — two parts sharing one return via are coupled through it
// because the mesh has a SINGLE NODE there, not two.
//
// ── THE ONE HONEST LIMIT FROM ARTWORK ALONE (R-rail3-10) ───────────────────────────────────────
//
// A via and a plated component hole are indistinguishable from geometry. `BoardNetlistFile` already
// carries the distinction and `DrillViaPairing` already declares it: a record with a component
// reference and a pin is a component hole; a record with a net and NO component reference is a via.
// Use it; do not re-derive it from hole diameter. Nothing in this file looks at a diameter to decide
// what a hole IS — only to say what it costs.
//
// ── NO CURRENT LIMIT HERE ──────────────────────────────────────────────────────────────────────
//
// Brief 6. This file computes the barrel RESISTANCE; the limit is a different question with a
// different basis (§4.2's own argument about the drill-size table disagreeing with itself by a
// factor of two), and it is that brief's to answer.

namespace CircuitRF.Design.Layout.Pdn;

/// <summary>Where a barrel's plated-wall thickness came from. <b>Every number carries its
/// provenance</b> — §4.2 quotes a 0.3 mm drill at 20 µm of plating at twice the current the
/// drill-size table gives it, and the one term that differs is this one.</summary>
public enum PdnPlatingBasis
{
    /// <summary>The stackup's own via entry stated it (<see cref="StackupLayer.WallThicknessDbu"/>).
    /// The only basis that is a measurement of this board.</summary>
    StackupViaEntry,

    /// <summary>The document's own typed setting
    /// (<c>RailSettings.ViaPlatingThicknessMicrometres</c>), which is what Q-22 asks for where the
    /// stackup is silent.</summary>
    Setting,

    /// <summary>Neither stated one, so <see cref="PdnViaModel.DefaultPlatingMicrometres"/> was used.
    /// <b>Reported as defaulted wherever it appears</b>, because a defaulted number and a measured
    /// one are indistinguishable on a report that does not say.</summary>
    Defaulted,
}

/// <summary>
/// One plated barrel of the extraction.
/// </summary>
/// <param name="X">The hole's centre, DBU.</param>
/// <param name="Y">DBU.</param>
/// <param name="FromLayer">The conductor drawing layer at one end of the span.</param>
/// <param name="ToLayer">The conductor drawing layer at the other.</param>
/// <param name="DrillMetres">The drilled hole DIAMETER — the plating is inside it.</param>
/// <param name="PlatingMetres">The plated wall thickness.</param>
/// <param name="Basis">Where that thickness came from.</param>
/// <param name="SpanMetres">The z distance the barrel carries current over.</param>
/// <param name="ResistanceOhms">What it costs.</param>
public sealed record PdnViaBarrel(
    long X, long Y,
    LayerKey FromLayer, LayerKey ToLayer,
    double DrillMetres, double PlatingMetres, PdnPlatingBasis Basis,
    double SpanMetres, double ResistanceOhms);

/// <summary>Barrel resistance, and nothing else.</summary>
public static class PdnViaModel
{
    /// <summary>
    /// 25 µm — one mil, the conventional minimum for a plated through hole, and the value that
    /// reproduces §4.2's own worked number: a 0.3 mm drill through a 1.6 mm board comes out at
    /// 1.28 mΩ against the note's "about 1.2 mΩ". At 20 µm the same hole is 1.57 mΩ, which is why
    /// <see cref="PdnPlatingBasis"/> exists rather than this being a constant nobody reads.
    /// </summary>
    public const double DefaultPlatingMicrometres = 25.0;

    /// <summary>
    /// The conducting copper annulus of a plated hole, in square metres.
    ///
    /// <para>The drill makes a hole of diameter <paramref name="drillMetres"/> and the plating is
    /// deposited INSIDE it, so the copper runs from radius <c>r − t</c> to <c>r</c>:
    /// <c>A = π·t·(2r − t)</c>. Plating the hole shut (<c>t ≥ r</c>) is a solid post of radius
    /// <c>r</c>, which is the limit this expression already gives.</para>
    /// </summary>
    public static double AnnulusAreaSquareMetres(double drillMetres, double platingMetres)
    {
        double r = drillMetres / 2.0;
        if (!(r > 0) || !(platingMetres > 0)) return 0.0;
        double t = Math.Min(platingMetres, r);
        return Math.PI * t * (2.0 * r - t);
    }

    /// <summary>
    /// <c>R = ρ·h / A</c> over the barrel's own annulus. Returns 0 for a hole with no conducting
    /// annulus at all, which is what a non-plated hole is — and a non-plated hole is not a via, so
    /// the caller never gets here with one.
    /// </summary>
    /// <param name="drillMetres">Hole diameter.</param>
    /// <param name="platingMetres">Plated wall thickness.</param>
    /// <param name="spanMetres">The z distance the barrel carries current over.</param>
    /// <param name="resistivityOhmMetres">Copper resistivity at the extraction's stated temperature.</param>
    public static double BarrelResistanceOhms(
        double drillMetres, double platingMetres, double spanMetres, double resistivityOhmMetres)
    {
        double a = AnnulusAreaSquareMetres(drillMetres, platingMetres);
        if (!(a > 0) || !(spanMetres > 0)) return 0.0;
        return resistivityOhmMetres * spanMetres / a;
    }

    /// <summary>
    /// The plated-wall thickness to use for a via on <paramref name="viaEntry"/>, and where it came
    /// from. <b>Never silently defaulted</b> — the third return is a <see cref="PdnPlatingBasis"/>
    /// the report prints.
    /// </summary>
    /// <param name="viaEntry">The stackup's via entry, or null where none matched.</param>
    /// <param name="dbuPerMicron">This layout's DBU resolution.</param>
    /// <param name="settingMicrometres">The document's own typed setting, or null.</param>
    public static (double Metres, PdnPlatingBasis Basis) ResolvePlating(
        StackupLayer? viaEntry, int dbuPerMicron, double? settingMicrometres)
    {
        if (viaEntry?.WallThicknessDbu is { } wall && wall > 0 && dbuPerMicron > 0)
            return (wall / (double)dbuPerMicron * 1e-6, PdnPlatingBasis.StackupViaEntry);

        if (settingMicrometres is { } typed && typed > 0)
            return (typed * 1e-6, PdnPlatingBasis.Setting);

        return (DefaultPlatingMicrometres * 1e-6, PdnPlatingBasis.Defaulted);
    }

    /// <summary>The sentence a report prints beside a barrel, naming the basis rather than only the
    /// number.</summary>
    public static string DescribePlating(double metres, PdnPlatingBasis basis) =>
        basis switch
        {
            PdnPlatingBasis.StackupViaEntry =>
                $"{metres * 1e6:0.##} µm of plating, which the stackup's via entry states",
            PdnPlatingBasis.Setting =>
                $"{metres * 1e6:0.##} µm of plating, from this document's own setting",
            _ => $"{metres * 1e6:0.##} µm of plating — nothing stated one, so this is the default " +
                 "and every number derived from it says so",
        };
}
