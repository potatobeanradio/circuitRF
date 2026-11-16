// brief-pch-9 R-pch9-1 / R-pch9-5 — what a beam selection may do to one Feed-mode Phased Antenna Array: weigh it at another steering
// direction, exactly as the elaboration weighs it at the authored one (the same FeedWeights, the array's own steer() with θ0 and φ0 replaced,
// then PhaseBits and AttenStep), or the sentence saying why it may not.
//
// Set by the elaborator on every Feed-mode array (and again by a re-weigh, whose scope is the new one), consumed by src/Engine's
// BeamSelection at the start of a run, when the carrier is known. Nothing here changes the array: Weigh returns a beamformer, and the
// selection copies its weights into the array's own (PhasedArrayFeedModel.TakeWeights) for the run.

namespace CircuitRF.Core.Devices.Arrays;

/// <summary>R-pch9-5 — the steering a selection may set on one Feed-mode array.</summary>
public sealed class ArraySteering
{
    /// <summary>The steer() or steerDelay() call the weights are, or null when they are none.</summary>
    public required SteerCall? Call { get; init; }

    /// <summary>Why a selection cannot set the beam (a sentence that names BeamSelect=manual), or null when it can.</summary>
    public required string? Refusal { get; init; }

    /// <summary>The authored θ0, φ0 (radians), when both resolve.</summary>
    public (double Theta, double Phi)? Authored { get; init; }

    /// <summary>The beamformer with the call's θ0, φ0 set to (θ, φ) in degrees. Throws <see cref="InvalidOperationException"/> when
    /// <see cref="Refusal"/> is not null.</summary>
    public required Func<double, double, PhasedArrayFeedModel> Weigh { get; init; }

    /// <summary>
    /// brief-pch-9 — the same beamformer in closed form, for SCORING a codebook: present only when the field is exactly the bare call (Phase
    /// <c>steer(θ0, φ0, f0)</c> or Delay <c>steerDelay(θ0, φ0)</c>, positions the element's own) and f0 reads no element name. Then each
    /// element's phase is −360·f0/c·(x·sinθ·cosφ + y·sinθ·sinφ) degrees at the field's site unit (or its delay that over c), its amplitude and
    /// the other field's values are the elaboration's at one exact weighing, and PhaseBits and AttenStep quantise as FeedWeights does — no
    /// expression is evaluated per element. Its weights equal <see cref="Weigh"/>'s to round-off (a gate holds it), so a phase lying exactly on a
    /// quantisation boundary may round the other way: a candidate's score may move by that, never the run, since whatever is APPLIED is
    /// weighed through <see cref="Weigh"/>.
    /// </summary>
    public Func<double, double, PhasedArrayFeedModel>? Fast { get; init; }

    /// <summary>Beamformers formed through <see cref="Fast"/> on this thread.</summary>
    public static long FastWeighings => _fast;
    [ThreadStatic] private static long _fast;
    internal static void CountFast() => _fast++;

    /// <summary>Beamformers weighed through <see cref="Weigh"/> on this thread: one per codebook entry of a steered end.</summary>
    public static long Weighings => _weighings;
    [ThreadStatic] private static long _weighings;
    internal static void CountWeighing() => _weighings++;
}
