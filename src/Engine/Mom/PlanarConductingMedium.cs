// SI-SIG — WHAT A DIELECTRIC'S CONDUCTIVITY DOES TO A PLANAR RUN, AND WHERE THE RUN CANNOT ANSWER
//
// A conducting substrate is not one thing across a band, and a single loss tangent cannot stand in for
// it: tanδ = σ/(ωε₀εᵣ) falls as 1/f. Which of three regimes it is in is set by two frequencies.
//
//   f_relax = σ/(2πε₀εᵣ)     above it the layer is a lossy dielectric and the wave travels in it;
//                            below it the layer conducts more than it displaces, so the capacitance
//                            of a line over an insulator rises toward the insulator's alone.
//   f_skin                   above it the skin depth is under twice the layer's thickness, so current
//                            flows ALONG the layer and the return moves out of the ground plane.
//
// The first two are served. The electrostatic C* of each frequency carries the conduction, and quasi-TEM
// is still the right description. The third takes `PlanarSubstrateReturn`'s series term below the crossover:
// without it, γ = jω√(LC) has no current in the silicon, which on a 1 mm line over 0.01 Ω·cm silicon read
// α 22× low and β 44 % high at 20 GHz against a 3D reference. A standard that declines the term, and a layer
// at 10 Ω·cm or above that is thick enough to reach its skin onset, still has the range named as a warning.
// What is left with the term is named too. `RESOLVED.md` §SI-LOW holds the tables.

using System.Globalization;

namespace CircuitRF.Engine.Mom;

/// <summary>The regime arithmetic and the sentences a run says about a conducting substrate.</summary>
public static class PlanarConductingMedium
{
    /// <summary>Skin depth, as a multiple of the layer's thickness, below which current along the layer
    /// matters. At 1.6 thicknesses a measured line's α was already 5× low; at 0.7 its |ΔS| was 0.08.</summary>
    public const double SkinDepthOverThickness = 2.0;

    /// <summary>Where the quasi-static γ's missing dispersion shows, as a fraction of the crossover, on a layer
    /// that has not shielded the line by then. |ΔS| on a 1 mm line at 1000 S/m was 0.027 at 10 GHz and 0.034 at
    /// 20 GHz, and under 0.01 at 5 GHz.</summary>
    public const double DispersionFromCrossoverFraction = 1.0 / 3.0;

    /// <summary>The skin depth at the crossover, as a multiple of the layer's thickness, under which the layer
    /// has shielded the line and that error stays in tolerance: 0.3 at 1e4 S/m read 0.014, and 0.96 at
    /// 1000 S/m read 0.034.</summary>
    public const double ShieldedSkinDepthOverThickness = 0.5;

    /// <summary>The frequency where conduction current equals displacement current.</summary>
    public static double RelaxationHz(EmMaterial m) =>
        m.SigmaSm / (2.0 * Math.PI * EmConstants.Eps0 * m.EpsR);

    /// <summary>δ = 1/√(πfµσ), metres.</summary>
    public static double SkinDepthM(double sigmaSm, double fHz, double muR = 1.0) =>
        1.0 / Math.Sqrt(Math.PI * fHz * EmConstants.Mu0 * muR * sigmaSm);

    /// <summary>The frequency above which the skin depth is under <see cref="SkinDepthOverThickness"/>
    /// thicknesses of the layer.</summary>
    public static double SkinOnsetHz(EmMaterial m, double thicknessM)
    {
        double delta = SkinDepthOverThickness * thicknessM;
        return 1.0 / (Math.PI * EmConstants.Mu0 * m.MuR * m.SigmaSm * delta * delta);
    }

    /// <summary>
    /// The note for each conducting layer, and a warning naming any range the calibration cannot serve.
    /// </summary>
    /// <param name="stack">The medium, before any frequency is applied.</param>
    /// <param name="crossoverHz">Where γ stops being quasi-static; 0 on a run with no calibration.</param>
    /// <param name="anyGroup">Whether a coupled-port calibration group is in the run.</param>
    /// <param name="substrateReturnSupplied">Whether every calibration's γ carries current in the substrate
    /// (<see cref="PlanarSubstrateReturn"/>).</param>
    public static IReadOnlyList<EmFinding> Findings(LayerStack stack, double fLoHz, double fHiHz,
                                                    double crossoverHz, bool anyGroup,
                                                    bool substrateReturnSupplied = false)
    {
        var found = new List<EmFinding>();
        foreach (var layer in stack.Layers)
        {
            var m = layer.Material;
            if (!(m.SigmaSm > 0)) continue;

            double relax = RelaxationHz(m), skin = SkinOnsetHz(m, layer.ThicknessM);
            string what = $"{SurfaceMesher.Eng(layer.ThicknessM)}m of εᵣ = {G(m.EpsR)} with σ = {G(m.SigmaSm)} S/m " +
                          $"({G(100.0 / m.SigmaSm)} Ω·cm)";

            string regime = fHiHz > skin
                ? $"Above {SurfaceMesher.Eng(skin)}Hz its skin depth is under twice its thickness, so it " +
                  "behaves increasingly like a lossy ground plane and return current moves into it."
                : fLoHz < relax
                    ? $"Below {SurfaceMesher.Eng(relax)}Hz it conducts more than it displaces, so a line " +
                      "over an insulator on it slows down and its phase velocity falls."
                    : "Across this sweep it is a lossy dielectric, and the wave travels in it.";

            found.Add(EmFinding.Note(
                $"A dielectric layer conducts: {what}. That amounts to tanδ {G(m.ConductionTanD(fLoHz))} at " +
                $"{SurfaceMesher.Eng(fLoHz)}Hz and {G(m.ConductionTanD(fHiHz))} at {SurfaceMesher.Eng(fHiHz)}Hz, " +
                $"beside the layer's own tanδ {G(m.TanD)}, and each frequency is solved with its own value. {regime}"));

            double from = Math.Max(fLoHz, skin), to = Math.Min(fHiHz, crossoverHz);
            if (from <= to && !substrateReturnSupplied)
                found.Add(EmFinding.Warn(
                    $"From {SurfaceMesher.Eng(from)}Hz to {SurfaceMesher.Eng(to)}Hz these S-parameters are not " +
                    $"reliable. Current flows along {what} there, and the de-embedding's γ has no term for it, " +
                    "so loss reads far too low and phase too high. Run this range on a 3D solver with the " +
                    "silicon's skin depth meshed."));

            // SI-LOW — the term puts current in the silicon, but γ is still quasi-static, and neither route
            // closes what remains: the measured γ is further off on the same mesh.
            if (substrateReturnSupplied && m.SigmaSm > PlanarSubstrateReturn.MinConductivitySm &&
                SkinDepthM(m.SigmaSm, crossoverHz, m.MuR) > ShieldedSkinDepthOverThickness * layer.ThicknessM)
            {
                double near = Math.Max(fLoHz, DispersionFromCrossoverFraction * crossoverHz);
                if (near <= to)
                    found.Add(EmFinding.Warn(
                        (near < to ? $"From {SurfaceMesher.Eng(near)}Hz to {SurfaceMesher.Eng(to)}Hz" : $"At {SurfaceMesher.Eng(to)}Hz") +
                        " phase reads about 2 % low " +
                        $"and loss about 3 % low over {what}, because the de-embedding's γ has no dispersion there. " +
                        "On a 1 mm line that was up to 0.035 in |S| against a 3D reference, and a longer line " +
                        "reads worse in proportion. Check this range on a 3D solver if it matters to that accuracy."));
            }
        }

        if (anyGroup)
            found.Add(EmFinding.Warn(
                $"From {SurfaceMesher.Eng(fLoHz)}Hz to {SurfaceMesher.Eng(fHiHz)}Hz these S-parameters are not " +
                "reliable on the coupled-port group. Its modal reference impedances are taken from one " +
                "capacitance at the top of the sweep, which a conducting substrate does not have."));
        return found;

        static string G(double v) => (v is >= 1000 and < 1e7 ? v.ToString("0", CultureInfo.InvariantCulture)
                                                             : v.ToString("G3", CultureInfo.InvariantCulture));
    }
}
