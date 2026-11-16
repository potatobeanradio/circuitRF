using System.Numerics;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Netlist.Spice;

/// <summary>
/// A frequency-defined element's rational fit as the transient engine realizes it (brief TRN-4): its S matrix
/// <c>S(s) = D + Σ_k R_k/(s − a_k)</c> referred to <see cref="Z0"/>, poles shared by every entry, complex poles in conjugate
/// pairs with the positive-imaginary member first, entries indexed <c>i·Ports + j</c>.
/// </summary>
/// <param name="Nodes">Each port's node (into the elaborated netlist).</param>
/// <param name="ReferenceNode">The node every port is referred to.</param>
public sealed record SpiceBlockFit(
    IReadOnlyList<int> Nodes, int ReferenceNode, double Z0, IReadOnlyList<Complex> Poles,
    IReadOnlyList<IReadOnlyList<Complex>> Residues, IReadOnlyList<double> D);

/// <summary>
/// A lossy two-conductor line as the transient engine realizes it (brief TRN-4b): its delay τ with a characteristic impedance
/// Zc, and the causal residual <c>H~(s) = D + Σ_k R_k/(s − a_k)</c> between the wave that left the far end τ earlier and the
/// near end's row, <c>V − Zc·I = H~ * W_far(t − τ)</c>, <c>W = V + Zc·I</c>. A residual of no poles and D = 1 is the lossless
/// line.
/// </summary>
public sealed record SpiceLineResidual(
    int NodeA, int NodeB, double Zc, double TauS, IReadOnlyList<Complex> Poles, IReadOnlyList<Complex> Residues, double D);

/// <summary>The fits of one netlist, by instance path, and the elements the fit could not realize with its sentence.</summary>
public sealed class SpiceFitSet
{
    public Dictionary<string, SpiceBlockFit> Blocks { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, SpiceLineResidual> Lines { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Refusals { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Supplies the transient engine's own fits to the SPICE deck writer for <c>SpiceFit=yes</c> (docs/design/transient.md §9.2.3,
/// brief TRN-6 R-trn6-8). The fits live in the numeric layer; this seam lets the writer, which is in the design layer, write
/// them without a second fitter — the deck then realizes exactly what a native run would have stepped.
/// </summary>
public interface ISpiceFitSource
{
    /// <summary>Fits every frequency-defined element and lossy line of <paramref name="netlist"/> over DC to
    /// <paramref name="fMaxHz"/>. The netlist is left as it was.</summary>
    SpiceFitSet Realize(ElaboratedNetlist netlist, double fMaxHz);
}
