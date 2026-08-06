using System.Numerics;

namespace CircuitRF.Engine.Mom;

public enum EmSegmentKind
{
    /// <summary>A conductor perimeter segment. Carries <b>free</b> charge.</summary>
    Conductor,
    /// <summary>A dielectric interface segment. Carries <b>bound</b> (polarisation) charge.</summary>
    DielectricInterface,
}

/// <summary>
/// One boundary segment — the unit of unknown. The solved σ on a segment is always the
/// <b>equivalent free-space</b> charge density: the quantity that, radiating into vacuum, produces
/// the true field. On a conductor surface that is <i>not</i> the free charge when the conductor
/// faces a dielectric — see <see cref="EpsOutside"/>.
/// </summary>
/// <param name="A">Start point (metres).</param>
/// <param name="B">End point (metres).</param>
/// <param name="Normal">
/// Unit reference normal. For a conductor segment this is the <b>outward</b> normal (used only to
/// decide which dielectric region the surface faces). For a dielectric-interface segment it points
/// from region 1 into region 2 — "up" for the horizontal interfaces kernel A meshes — and it is
/// the normal the R-mom-6 continuity row is written against.
/// </param>
/// <param name="ConductorIndex">Index into <see cref="EmMesh.ConductorNames"/>, or −1.</param>
/// <param name="InterfaceIndex">Interface ordinal, or −1.</param>
/// <param name="EpsOutside">
/// Conductor segments only: the relative complex permittivity of the medium the surface faces.
/// The <b>free</b> charge on the surface is σ_free = ε_r·σ, because the bound charge that the
/// dielectric lays down immediately against the metal is folded into σ. (D_n = σ_free and
/// E_n = σ/ε₀ with all charge explicit, so σ_free = ε_r σ.) Omitting this factor is what makes a
/// fully-filled coax come out at the air value instead of ε_r times it.
/// </param>
/// <param name="K">
/// Dielectric-interface segments only: K = (ε₁ − ε₂)/(ε₁ + ε₂) with ε₁ <i>behind</i>
/// <paramref name="Normal"/> and ε₂ <i>ahead</i> of it. Complex, per R-mom-6.
/// </param>
public sealed record EmSegment(
    EmPoint       A,
    EmPoint       B,
    EmPoint       Normal,
    EmSegmentKind Kind,
    int           ConductorIndex,
    int           InterfaceIndex,
    Complex       EpsOutside,
    Complex       K)
{
    public double  Length => (B - A).Norm;
    public EmPoint Mid    => new(0.5 * (A.X + B.X), 0.5 * (A.Y + B.Y));
}

/// <summary>
/// A meshed problem: the segment list plus the two things the solver needs that are not per-segment
/// — how many conductors there are, and whether an image ground plane is present.
///
/// <para><b>Why the solver takes a mesh rather than an <see cref="EmProblem"/>.</b> The physics of
/// §3 is stated over segments, not over horizontal slabs. Keeping <see cref="ChargeSolver"/>
/// neutral about how the segments were produced is what lets the exact <i>cylindrical</i>-interface
/// oracles (two-layer coax) be tested at all — they are not expressible in the horizontal-slab
/// <see cref="EmProblem"/> that R-mom-3 deliberately restricts the mesher to.</para>
/// </summary>
public sealed record EmMesh(
    IReadOnlyList<EmSegment> Segments,
    IReadOnlyList<string>    ConductorNames,
    EmGroundPlane?           Ground)
{
    public int ConductorCount => ConductorNames.Count;
}

/// <summary>
/// The per-edge subdivision fractions of every conductor outline, captured once so that a
/// <i>perturbed</i> geometry (Wheeler's receded outline, R-mom-12) can be meshed with a
/// topologically identical mesh. Without this, the finite difference ∂L/∂n would be contaminated
/// by the discretisation changing underneath it.
/// </summary>
/// <param name="EdgeFractions">
/// <c>[conductor][edge]</c> → cumulative fractions in (0, 1] along that edge, ending at exactly 1.
/// </param>
public sealed record ConductorMeshTemplate(IReadOnlyList<IReadOnlyList<double[]>> EdgeFractions);
