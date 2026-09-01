namespace CircuitRF.Core.Devices;

/// <summary>
/// A linear model that carries its inductance on a Group-2 branch-current unknown, and can
/// therefore be one end of a <see cref="MutualInductanceModel"/>.
///
/// <para>The interface exists because three models now do this — <see cref="InductorModel"/>,
/// <see cref="SeriesRlcModel"/> and <see cref="ParallelRlcModel"/> — and the half-dozen places that
/// need the branch index (mutual coupling, inductance regularization, branch labelling, the SDD's
/// control-current reference) must reach all three. Every one of those sites used to pattern-match
/// <c>InductorModel</c> by type, and a pattern match is exactly the wrong shape here: adding a
/// fourth inductive model would leave each site silently correct-looking and quietly skipping it.
/// Regularization skipping a branch is not an error message, it is a singular matrix somewhere
/// else.</para>
///
/// <para><b>What implementers must guarantee.</b> The branch this reports is the one carrying the
/// element's INDUCTOR current, with the constraint row containing a <c>-jωL</c> diagonal term, so
/// that adding <c>-jωM</c> at the (this, other) and (other, this) off-diagonals is the correct
/// mutual stamp. A model whose branch carries something else must not implement this.</para>
///
/// <para>The inductance value itself is NOT on the interface: every implementer states it as an
/// <c>L</c> parameter, which is where <see cref="MutualInductanceModel"/> already reads it from,
/// and duplicating it here would create a second place for the two to disagree.</para>
/// </summary>
public interface IInductiveBranch
{
    /// <summary>
    /// Branch index assigned on the most recent Stamp call, or −1 before the first.
    /// Set during each frequency pass; stable across frequencies for a fixed topology.
    /// </summary>
    int LastBranchIndex { get; }
}
