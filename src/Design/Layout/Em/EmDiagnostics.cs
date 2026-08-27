using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Layout.Em;

/// <summary>
/// The EM run service's refusals, as coded diagnostics — the one family converted end to end to
/// establish the pattern (<c>docs/sonnet-briefs/brief-localization-groundwork.md</c> R-loc-5 §8.3).
///
/// <para><b>Why this family and not another.</b> It already had named outcomes
/// (<see cref="EmRunStatus.Refused"/> / <c>NoLayout</c> / <c>EngineError</c> / <c>Cancelled</c>) and
/// a DOCUMENTED external contract: <c>docs/design/cli.md</c> §8 promises that a refusal stays a
/// refusal — exit 1 with the run service's own sentence, and 130 for a cancellation. That contract
/// is what makes the conversion checkable rather than merely plausible: the CLI's stderr text and
/// exit codes must come out byte-identical before and after, which is the proof that the diagnostic
/// carries everything the string did. <c>EmDiagnosticsTests</c> and <c>EmCliVerbTests</c> hold it.</para>
///
/// <para><b>The ids are the durable part.</b> Reword any template here freely — that is the point of
/// separating them. Changing an <see cref="Diagnostic.Id"/>, on the other hand, is making a new
/// diagnostic: ids are what dedup, filtering and any future resource lookup key on.</para>
///
/// <para><b>Two kinds live here, and the difference is deliberate.</b> The sentences EmRunService
/// AUTHORS become real templates with typed arguments. The sentences it FORWARDS — a kernel's
/// <c>CanSolve</c> verdict, the port extractor's refusal, a mesh-ceiling exception — are wrapped
/// with <see cref="Forwarded"/> instead: an id that says where the refusal came from, carrying the
/// original text as an argument. Wrapping rather than re-authoring keeps this conversion to one
/// family (§10: "if it starts touching more than the one converted family plus the type plus the
/// render point, stop"), while still giving every refusal an id to group and filter by. Converting
/// the kernels' own text is the natural next step and is deliberately not taken here.</para>
/// </summary>
public static class EmDiagnostics
{
    // ── Authored here ────────────────────────────────────────────────────────

    /// <summary>A stopped run — a normal outcome, not a failure, and it wrote nothing.</summary>
    public static Diagnostic Cancelled() => new(
        "em.run.cancelled",
        DiagnosticSeverity.Info,
        "The EM run was stopped. Nothing was written.");

    /// <summary>The <c>.cem</c> names a layout that could not be resolved.</summary>
    public static Diagnostic NoLayout(string layoutRef) => Diagnostic.Create(
        "em.layout.not-found",
        DiagnosticSeverity.Error,
        "The layout '{layoutRef}' could not be found, so there is no geometry to analyse. " +
        "Point this EM setup at a layout that exists.",
        ("layoutRef", layoutRef));

    /// <summary>The layout resolved, but nothing says how thick its metal is.</summary>
    public static Diagnostic NoTechnology(string layoutRef) => Diagnostic.Create(
        "em.layout.no-technology",
        DiagnosticSeverity.Error,
        "The layout '{layoutRef}' has no technology resolved, so nothing says how thick its metal " +
        "is or where the ground plane sits.",
        ("layoutRef", layoutRef));

    /// <summary>The sweep expression would not expand.</summary>
    public static Diagnostic FrequencySweepUnresolvable(string reason) => Diagnostic.Create(
        "em.frequency.unresolvable",
        DiagnosticSeverity.Error,
        "The frequency sweep could not be resolved: {reason}",
        ("reason", reason));

    /// <summary>The sweep expanded, to nothing.</summary>
    public static Diagnostic FrequencySweepEmpty() => new(
        "em.frequency.no-points",
        DiagnosticSeverity.Error,
        "The frequency sweep produced no points. Check the start, stop and step or count.");

    /// <summary>
    /// An internal port on a kernel that has nowhere to put one. Refused rather than re-routed,
    /// because running anyway publishes a complete and plausible answer for the wrong network.
    /// </summary>
    public static Diagnostic InternalPortNeedsFullWave(string kernelName) => Diagnostic.Create(
        "em.port.internal-needs-full-wave",
        DiagnosticSeverity.Error,
        "This EM setup declares an internal port, and the analysis it resolved to is the " +
        "{kernelName}. An internal delta gap is a cut across a conductor at a mesh gridline, and an " +
        "internal port is the foot of a via down to the ground plane — and the uniform-line " +
        "kernel never meshes the " +
        "plane at all: it solves a cross-section for per-unit-length RLGC and forms the network of a " +
        "length-L line in closed form, so its only ports are the two ends of that line, by " +
        "construction. There is nowhere for either to be. Running anyway would publish a complete " +
        "and plausible answer for your line WITHOUT the port you asked for, which is why this is " +
        "refused rather than reported. Set Analysis to the full-wave planar kernel, or change the " +
        "port back to an edge port.",
        ("kernelName", kernelName));

    /// <summary>The solve threw. Distinct from a refusal: circuitRF failed, the setup did not.</summary>
    public static Diagnostic SolveFailed(string reason) => Diagnostic.Create(
        "em.solve.failed",
        DiagnosticSeverity.Error,
        "The EM solve failed: {reason}",
        ("reason", reason));

    // ── Forwarded from deeper ────────────────────────────────────────────────

    /// <summary>
    /// Wraps a refusal authored below this file — a kernel verdict, the port extractor, the mesh
    /// ceiling — so it still arrives with an id, without re-authoring text this file does not own.
    /// <paramref name="source"/> becomes the last segment of the id (<c>em.refused.kernel</c>).
    ///
    /// <para>The text is nullable because the refusal fields it wraps are. The CALL SITES keep
    /// passing the original nullable value to <c>Error</c> rather than this diagnostic's render, so
    /// a null stays a null and no existing consumer sees a behaviour change — the whole conversion
    /// is required to be observationally identical.</para>
    /// </summary>
    public static Diagnostic Forwarded(string source, string? text) => Diagnostic.Create(
        "em.refused." + source,
        DiagnosticSeverity.Error,
        "{text}",
        ("text", text));
}
