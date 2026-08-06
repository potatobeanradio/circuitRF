// L8e — the kernel registry §10.3.4 has been deferring since L6, and the SECOND correction to that
// section's own signature.
//
// D1 — THE REGISTRY IS KEYED ON THE ANALYSIS KIND, AND IT UNIFIES THE OUTPUT CONTRACT, NOT THE
// INPUT TYPE.
//
// The obvious design is one interface both kernels implement. It cannot be built, and the reason is
// already on the record: L8b's D1 decided that PlanarProblem is a SIBLING of EmProblem — "no shared
// base, no interface implemented by both: two things that are genuinely different, described by two
// types, is the cheapest arrangement to be correct in" — and nothing since has weakened that. An
// EmProblem is a CROSS-SECTION (conductors as finite-thickness polygons in the x-y plane of a cut,
// dielectric regions as horizontal slabs, a propagation length); a PlanarProblem is a PLAN VIEW
// (filled regions on one metal level over a grounded slab, with no length at all). Forcing a common
// input type now would either resurrect the base class L8b rejected or push a nullable-fields union
// through every call site.
//
// So IEmKernel is left exactly as it is and stays kernel A's; kernel B gets its own entry point with
// its own honest signature (PlanarKernel). What the registry unifies is what comes OUT — a DataSet,
// a note list and an EmSuitability — because that is the only thing the two genuinely share, and it
// is enough for every caller.
//
// R-res-1 — THIS IS THE ONLY PLACE A KERNEL IS CHOSEN, and the choice plus its reason appear in the
// notes on every run. No caller constructs a kernel directly once this exists.
//
// THE UI FIREWALL IS WHY Choose TAKES VERDICTS RATHER THAN GEOMETRY. Both extractors live in
// src/Ui/Layout/Em (they read .clay shapes, a Technology and a layer table — R-mom-1's whole point),
// and the reference graph is Ui → Engine. The registry therefore cannot run extraction; it takes
// what the two extractors SAID and decides. That is also what makes D2's rule testable in
// Engine.Tests with no layout document anywhere near it.

using RfCore.Data;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// Which analysis — which kernel — an EM setup means.
///
/// <para><b>Lives in the engine because the registry is keyed on it (D1).</b> It was defined in
/// <c>src/Ui/Layout/Em/EmSetupModel.cs</c> at L8b, when there was no registry and the only consumer
/// was the <c>.cem</c>; the enum names are unchanged, so a <c>.cem</c> written before L8e reads
/// back byte-identically.</para>
///
/// <para><b><see cref="CrossSection"/> must stay the zero value.</b> <c>EmSetupPersistence</c> omits
/// the field from the file when it holds that value, which is what keeps every pre-L8b <c>.cem</c>
/// round-tripping unchanged.</para>
/// </summary>
public enum EmAnalysisKind
{
    /// <summary>Kernel A — the 2-D quasi-static per-unit-length cross-section solve (L6/L7). The
    /// default, and what every pre-L8b <c>.cem</c> means.</summary>
    CrossSection = 0,

    /// <summary>Kernel B — the full-wave planar solve on a single grounded slab (L8).</summary>
    Planar = 1,

    /// <summary>D2 — let the registry choose, conservatively, and say which and why.</summary>
    Auto = 2,
}

/// <summary>One registered kernel: what it is, what it can do, and the one-line reason a user would
/// be given for landing on it.</summary>
public sealed record EmKernelDescriptor(
    EmAnalysisKind Kind,
    string         Name,
    EmCapabilities Capabilities,
    string         Summary);

/// <summary>
/// What ONE extractor said about one piece of geometry — the only thing the registry needs from the
/// Ui side, so the firewall holds and D2's rule stays testable without a layout document.
/// </summary>
public readonly record struct EmExtractorVerdict(bool Accepts, string? Refusal)
{
    public static EmExtractorVerdict Yes => new(true, null);

    public static EmExtractorVerdict No(string refusal) => new(false, refusal);
}

/// <summary>
/// The registry's answer: which kernel, whether it can run, why — and, when it cannot, a refusal
/// that quotes the extractor's own words rather than re-wording them.
/// </summary>
/// <param name="Kind">Never <see cref="EmAnalysisKind.Auto"/> — Auto is a request, not an outcome.</param>
/// <param name="Reason">R-res-1: goes in the notes on every run, chosen or refused. R-msh-8a's own
/// shape — name the thing, name the alternative — so a user who got the slow kernel can see why in
/// one line.</param>
public sealed record EmKernelChoice(
    EmAnalysisKind Kind,
    string         KernelName,
    bool           Ok,
    string?        Refusal,
    string         Reason);

/// <summary>
/// The unified OUTPUT contract (D1). Both kernels' Ui-side run paths end here, so a caller that
/// only wants "the DataSet, the notes and whether it worked" needs to know nothing about which
/// kernel produced it.
/// </summary>
public sealed record EmKernelOutcome(
    EmAnalysisKind        Kind,
    string                KernelName,
    DataSet?              Data,
    EmSuitability         Suitability,
    IReadOnlyList<string> Notes)
{
    public bool Ok => Data is not null && Suitability.Ok;

    public static EmKernelOutcome Refused(
        EmAnalysisKind kind, string kernelName, string reason, IEnumerable<string>? notes = null)
        => new(kind, kernelName, null, EmSuitability.No(reason), notes is null ? [] : [.. notes]);
}

public static class EmKernelRegistry
{
    /// <summary>
    /// How much cheaper kernel A is than kernel B on geometry both accept, as an order of magnitude
    /// rather than a promise. Kernel A's whole model is frequency-independent (R-mom-11) so a
    /// 101-point sweep costs four matrix fills; kernel B measured 7.66 s per de-embedded point on
    /// §10.7's own hero (L8d Tier 7), i.e. ~780 s for the same sweep. That is the number behind
    /// D2's "conservative", and it is why Auto prefers A whenever A accepts.
    /// </summary>
    private const string CheaperByRoughly = "about a thousand times cheaper";

    public static readonly EmKernelDescriptor CrossSection = new(
        EmAnalysisKind.CrossSection,
        QuasiStaticKernel.KernelName,
        EmCapabilities.UniformCrossSection,
        "A 2-D quasi-static per-unit-length solve of a uniform cross-section: exact for straight, " +
        "mutually parallel, constant-width conductors, validated to ≤1.3% on ε_eff against " +
        "Hammerstad-Jensen, and effectively instant because the whole model is frequency-independent.");

    /// <summary>
    /// <b>L9d/M5 — the capability is taken from the kernel itself rather than restated.</b> It gained
    /// <see cref="EmCapabilities.LayeredWithVias"/> when a two-level structure could actually be
    /// solved; reading it from <see cref="PlanarKernel.Capabilities"/> is what keeps the registry and
    /// the kernel from drifting apart, exactly as <c>Describe</c> already keeps the kind and the flag
    /// from drifting.
    /// </summary>
    public static readonly EmKernelDescriptor Planar = new(
        EmAnalysisKind.Planar,
        PlanarKernel.KernelName,
        new PlanarKernel().Capabilities,
        "A full-wave planar (MoM) solve of arbitrary artwork on N conductor levels over an " +
        "arbitrary stratified medium, with vias carrying z-directed current between adjacent " +
        "levels: it sees discontinuities, radiation and resonance, which is exactly what a " +
        "cross-section kernel cannot. It fills and factors a dense complex matrix at every " +
        "frequency, so it is orders of magnitude slower.");

    /// <summary>Every registered kernel, in the order Auto considers them (cheapest first).</summary>
    public static IReadOnlyList<EmKernelDescriptor> Kernels { get; } = [CrossSection, Planar];

    /// <summary>
    /// The capability a given analysis kind needs. <b>This is what finally reads
    /// <see cref="EmCapabilities"/></b> — the flag has existed since L6 and nothing consumed it;
    /// <see cref="Describe"/> resolves a kind to a kernel by asking which registered kernel declares
    /// the matching flag, so adding kernel W or C is a registration rather than an edit here.
    /// </summary>
    public static EmCapabilities RequiredCapability(EmAnalysisKind kind) => kind switch
    {
        EmAnalysisKind.CrossSection => EmCapabilities.UniformCrossSection,
        EmAnalysisKind.Planar       => EmCapabilities.Planar,
        _                           => EmCapabilities.None,
    };

    /// <summary>The kernel registered for a kind, found by its declared capability.</summary>
    public static EmKernelDescriptor Describe(EmAnalysisKind kind)
    {
        var need = RequiredCapability(kind);
        if (need != EmCapabilities.None)
            foreach (var k in Kernels)
                if (k.Capabilities.HasFlag(need)) return k;

        throw new ArgumentOutOfRangeException(
            nameof(kind), kind,
            "No registered EM kernel declares the capability this analysis kind needs. " +
            "EmAnalysisKind.Auto is a request, not a kernel — resolve it with Choose first.");
    }

    /// <summary>
    /// D2 — <b>the ONE place a kernel is chosen.</b>
    ///
    /// <code>
    /// Auto → kernel A  if the cross-section extractor ACCEPTS   (validated, and ~1000× cheaper)
    ///      → kernel B  if it refuses and the planar extractor accepts
    ///      → refuse, quoting BOTH refusals, if neither
    /// </code>
    ///
    /// <para><b>Explicit stays explicit (R-res-3).</b> A <c>.cem</c> that names
    /// <see cref="EmAnalysisKind.CrossSection"/> or <see cref="EmAnalysisKind.Planar"/> is honoured
    /// even when the other would work — auto never overrides a user's choice, in either direction.
    /// What it does instead is SAY so: an explicit planar setup on geometry kernel A also accepts is
    /// told that A would have been picked and is far cheaper, and an explicit cross-section setup
    /// that gets refused is told that the planar kernel accepts it and how to switch.</para>
    /// </summary>
    public static EmKernelChoice Choose(
        EmAnalysisKind requested, EmExtractorVerdict crossSection, EmExtractorVerdict planar)
    {
        switch (requested)
        {
            case EmAnalysisKind.Auto when crossSection.Accepts:
                return Ok(EmAnalysisKind.CrossSection,
                    $"Auto chose the quasi-static cross-section kernel (A): this geometry reduces to " +
                    $"a uniform cross-section, which A solves exactly and is {CheaperByRoughly} than " +
                    "the full-wave planar kernel (B). Set this EM setup's analysis to Planar if you " +
                    "want the full-wave answer anyway.");

            case EmAnalysisKind.Auto when planar.Accepts:
                return Ok(EmAnalysisKind.Planar,
                    "Auto chose the full-wave planar kernel (B), because the quasi-static " +
                    $"cross-section kernel (A) refused this geometry: {crossSection.Refusal}");

            case EmAnalysisKind.Auto:
                return Refuse(EmAnalysisKind.Planar,
                    "Neither EM kernel can analyse this geometry. The quasi-static cross-section " +
                    $"kernel (A) refused it: {crossSection.Refusal} The full-wave planar kernel (B) " +
                    $"refused it too: {planar.Refusal}",
                    "Auto could not choose a kernel: both refused this geometry.");

            case EmAnalysisKind.CrossSection when crossSection.Accepts:
                return Ok(EmAnalysisKind.CrossSection,
                    "This EM setup names the quasi-static cross-section kernel (A) explicitly, and " +
                    "auto-selection never overrides that." +
                    (planar.Accepts
                        ? " The full-wave planar kernel (B) would also accept this geometry, at " +
                          "orders of magnitude more cost."
                        : ""));

            case EmAnalysisKind.CrossSection:
                return Refuse(EmAnalysisKind.CrossSection,
                    crossSection.Refusal +
                    (planar.Accepts
                        ? " The full-wave planar kernel (B) does accept this geometry — set this EM " +
                          "setup's analysis to Planar (or to Auto, which would pick it) to solve it."
                        : ""),
                    "This EM setup names the quasi-static cross-section kernel (A) explicitly, and " +
                    "that kernel refused the geometry.");

            case EmAnalysisKind.Planar when planar.Accepts:
                return Ok(EmAnalysisKind.Planar,
                    "This EM setup names the full-wave planar kernel (B) explicitly, and " +
                    "auto-selection never overrides that." +
                    (crossSection.Accepts
                        ? " The quasi-static cross-section kernel (A) also accepts this geometry and " +
                          $"is {CheaperByRoughly}; Auto would have picked it."
                        : ""));

            case EmAnalysisKind.Planar:
                return Refuse(EmAnalysisKind.Planar,
                    planar.Refusal +
                    (crossSection.Accepts
                        ? " The quasi-static cross-section kernel (A) does accept this geometry — set " +
                          "this EM setup's analysis to Cross-section (or to Auto, which would pick " +
                          "it) to solve it."
                        : ""),
                    "This EM setup names the full-wave planar kernel (B) explicitly, and that kernel " +
                    "refused the geometry.");

            default:
                throw new ArgumentOutOfRangeException(nameof(requested), requested, null);
        }

        static EmKernelChoice Ok(EmAnalysisKind kind, string reason)
            => new(kind, Describe(kind).Name, true, null, reason);

        static EmKernelChoice Refuse(EmAnalysisKind kind, string? refusal, string reason)
            => new(kind, Describe(kind).Name, false,
                   refusal is { Length: > 0 } r ? r : reason, reason);
    }
}
