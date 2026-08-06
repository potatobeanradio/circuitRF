// The extractor's output: an EmProblem plus the §10.3.3 R16a readback, or a refusal.
//
// R-em-8 — the readback is a STRUCTURED RECORD, not a formatted string built in a view model.
// Same rule as EmMeshReport on the engine side: report it from the layer that knows, so the UI has
// nothing to recompute. The one formatted string here (Summary) is built by the extractor for the
// same reason — it is the §10.3.3 one-liner, and the panel prints it verbatim.

using CircuitRF.Engine.Mom;

namespace CircuitRF.Ui.Layout.Em;

/// <summary>Which way the line runs in the layout plane.</summary>
public enum EmPropagationAxis
{
    /// <summary>Along +X.</summary>
    X,
    /// <summary>Along +Y.</summary>
    Y,
    /// <summary>Neither — see <see cref="EmCrossSectionReadback.AxisAngleDeg"/>.</summary>
    Oblique,
}

/// <param name="Name">The conductor's name in the produced <see cref="EmProblem"/>.</param>
/// <param name="WidthMeters">Extent across the cross-section.</param>
/// <param name="ThicknessMeters">Metal thickness, from the stackup.</param>
/// <param name="CenterMeters">Cross-section coordinate of the conductor's centre, after the whole
/// cross-section has been centred on the conductors' own extent.</param>
public sealed record EmConductorReadback(
    string Name,
    double WidthMeters,
    double ThicknessMeters,
    double CenterMeters,
    double ZBottomMeters,
    double ZTopMeters);

public sealed record EmRegionReadback(
    string Name,
    double EpsR,
    double TanD,
    double MuR,
    double YBottomMeters,
    double YTopMeters);

/// <summary>
/// The §10.3.3 R16a readback: everything the EM setup panel shows about what was extracted, already
/// resolved. <c>"uniform 2-conductor cross-section · W = 2.9 mm · gap — · ℓ = 20 mm"</c> is
/// <see cref="Summary"/>; every number behind it is a field here.
/// </summary>
public sealed record EmCrossSectionReadback(
    IReadOnlyList<EmConductorReadback> Conductors,
    IReadOnlyList<double>              GapsMeters,
    double                             LengthMeters,
    EmPropagationAxis                  Axis,
    double                             AxisAngleDeg,
    string                             SignalLayerName,
    string?                            GroundLayerName,
    double                             GroundYMeters,
    double                             GroundSigmaSm,
    double                             SignalSigmaSm,
    IReadOnlyList<EmRegionReadback>    Regions,
    string                             Summary);

/// <summary>
/// Either an <see cref="EmProblem"/> + its readback, or a refusal. R-em-6: a refusal always names
/// the specific feature, where it was found, and where the capability arrives — the same shape
/// <c>QuasiStaticKernel.CanSolve</c> uses for the problem-level refusals it owns.
/// </summary>
public sealed record EmExtractionResult(
    EmProblem?                 Problem,
    EmCrossSectionReadback?    Readback,
    string?                    Refusal,
    IReadOnlyList<string>      Notes)
{
    public bool Ok => Problem is not null && Refusal is null;

    public static EmExtractionResult No(string refusal, IEnumerable<string>? notes = null)
        => new(null, null, refusal, notes is null ? [] : [.. notes]);

    public static EmExtractionResult Yes(EmProblem problem, EmCrossSectionReadback readback,
                                         IEnumerable<string>? notes = null)
        => new(problem, readback, null, notes is null ? [] : [.. notes]);
}
