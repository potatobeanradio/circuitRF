using RfCore.Data;

namespace CircuitRF.Engine.Mom;

[Flags]
public enum EmCapabilities
{
    None                 = 0,
    UniformCrossSection  = 1,
    Planar               = 2,
    LayeredWithVias      = 4,
    Wires                = 8,
    Surfaces             = 16,
}

/// <summary>The answer to "can you solve this?" — with a <i>specific</i> reason when not.</summary>
public sealed record EmSuitability(bool Ok, string? Reason)
{
    public static readonly EmSuitability Yes = new(true, null);
    public static EmSuitability No(string reason) => new(false, reason);
}

/// <summary>
/// The EM kernel seam (§10.3.4), corrected per <b>R-mom-1</b>: the kernel consumes the neutral
/// <see cref="EmProblem"/> defined in <c>src/Engine/Mom/</c>, in SI units, and knows nothing about
/// DBU, <c>.clay</c> shapes, layer tables or <c>LayerKey</c>.
///
/// <para>The design note's own signature — <c>Solve(LayoutFragment, Stackup, Port[], …)</c> — is
/// not simultaneously satisfiable with "the kernel lives in <c>src/Engine/Mom/</c>":
/// <c>LayoutFragment</c>, <c>Stackup</c> and <c>Technology</c> live in <c>src/Ui/Layout/</c>, the
/// reference graph is Ui → Engine → Core → RfCore, and inverting that arrow would break the UI
/// firewall <c>tests/Firewall.Tests</c> enforces. The Ui-side cross-section extractor produces an
/// <see cref="EmProblem"/>; producing it is what extraction already had to do. This is also the
/// standing invariant <i>"the numeric layer sees only fully-resolved values"</i> applied to
/// geometry — and it is what lets the whole kernel be tested without constructing a layout
/// document.</para>
///
/// <para><b>There is deliberately no kernel registry yet.</b> One kernel, constructed directly. A
/// registry earns its place when kernel W or B exists; adding it now is speculative plumbing with
/// no second implementation to constrain it.</para>
/// </summary>
public interface IEmKernel
{
    string         Name         { get; }
    EmCapabilities Capabilities { get; }

    /// <summary>
    /// <b>R-mom-17: the only place a refusal is worded.</b> Every rejection names the specific
    /// feature and where the capability arrives instead — the difference between v1 reading as
    /// bounded and reading as broken.
    /// </summary>
    EmSuitability CanSolve(EmProblem problem);

    /// <summary>The pre-solve mesh, for the viewer and for the unknown count.</summary>
    EmMeshReport Mesh(EmProblem problem, EmMeshSettings settings);

    DataSet Solve(EmProblem problem, EmMeshSettings settings, double[] freqsHz, CancellationToken ct);
}
