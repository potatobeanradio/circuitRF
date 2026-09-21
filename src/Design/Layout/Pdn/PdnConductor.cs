// One conductor of the stackup, PRICED — railrf §2.8.
//
// R-lvs2-1b: SHEET RESISTANCE STAYS BEHIND. This type came out of Regions.cs when the
// galvanic walk was promoted to Layout/Extraction and did not go with it, because it is the half
// railRF needs and LVS never asks for — LVS never prices anything. What it gained instead is a
// projection onto the neutral Conductor, so the two readings enumerate the stackup once.

using CircuitRF.Design.Layout.Extraction;

namespace CircuitRF.Design.Layout.Pdn;

/// <summary>
/// One conductor of the stackup as the extraction reads it: its drawing layer, its thickness and its
/// conductivity. <see cref="SheetResistanceOhmsPerSquare"/> is the whole of the DC model.
/// </summary>
/// <param name="StackupName">The <see cref="StackupLayer.Name"/> it came from.</param>
/// <param name="Layer">The drawing layer its copper is on.</param>
/// <param name="ThicknessMetres">Finished copper thickness.</param>
/// <param name="ConductivitySm">Conductivity at the extraction's stated temperature, S/m.</param>
public sealed record PdnConductor(
    string StackupName, LayerKey Layer, double ThicknessMetres, double ConductivitySm)
{
    /// <summary>This conductor as a reader that does not price copper sees it — R-lvs2-1b.</summary>
    public Conductor AsConductor => new(StackupName, [Layer]);

    /// <summary>
    /// <c>Rs = ρ / T</c> — the sheet resistance BELOW TWO SKIN DEPTHS, which at ω = 0 is every
    /// frequency this brief covers. 0.49 mΩ/square at 1 oz, 0.99 mΩ/square at 0.5 oz (§2.8).
    ///
    /// <para><b>This is ONE conductor's sheet resistance, and §4.1's <c>R = 2·Rs</c> is the
    /// LOOP's.</b> The factor of two is the two planes in series in the loop — see
    /// <see cref="PdnMeshExtractor"/>'s own note at the site where it is stamped, and read it before
    /// putting a 2 anywhere near this property.</para>
    /// </summary>
    public double SheetResistanceOhmsPerSquare =>
        ThicknessMetres > 0 && ConductivitySm > 0 ? 1.0 / (ConductivitySm * ThicknessMetres) : 0.0;
}
