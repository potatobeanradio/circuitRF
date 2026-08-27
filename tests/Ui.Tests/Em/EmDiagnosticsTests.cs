using System.Globalization;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Diagnostics;
using CircuitRF.Ui.Messages;

namespace CircuitRF.Ui.Tests.Em;

/// <summary>
/// The acceptance gate for R-loc-5's one converted family (brief-localization-groundwork.md §8.3).
///
/// <para><b>The contract being proved.</b> <c>docs/design/cli.md</c> §8 promises that a refusal
/// stays a refusal: exit 1 with the run service's own sentence, 130 for a cancellation. So the test
/// that a diagnostic carries everything the string did is that the TEXT IS UNCHANGED — every
/// sentence below is pinned verbatim against what <c>EmRunService</c> emitted before the
/// conversion. If a diagnostic had dropped an interpolated value, or reordered a clause, or lost the
/// apostrophes around a path, one of these fails.</para>
///
/// <para>Pinning prose is normally a bad test — reword the sentence, break the test — and §8.1 lists
/// that as one of the four reasons coded diagnostics are worth having. These pins are the deliberate
/// exception and they are temporary in spirit: they exist to prove ONE refactor was behaviour-
/// preserving. A later, intentional rewording updates the pin here and changes nothing else, which
/// is exactly the property the ids buy.</para>
/// </summary>
public sealed class EmDiagnosticsTests
{
    // ── The text is byte-identical to what shipped before the conversion ─────

    [Fact]
    public void Cancelled_RendersTheOriginalSentence() =>
        Assert.Equal("The EM run was stopped. Nothing was written.",
            EmDiagnostics.Cancelled().Render());

    [Fact]
    public void NoLayout_RendersTheOriginalSentence() =>
        Assert.Equal(
            "The layout 'Amp/layout/Amp.clay' could not be found, so there is no geometry to " +
            "analyse. Point this EM setup at a layout that exists.",
            EmDiagnostics.NoLayout("Amp/layout/Amp.clay").Render());

    [Fact]
    public void NoTechnology_RendersTheOriginalSentence() =>
        Assert.Equal(
            "The layout 'Amp/layout/Amp.clay' has no technology resolved, so nothing says how " +
            "thick its metal is or where the ground plane sits.",
            EmDiagnostics.NoTechnology("Amp/layout/Amp.clay").Render());

    [Fact]
    public void FrequencySweepUnresolvable_RendersTheOriginalSentence() =>
        Assert.Equal("The frequency sweep could not be resolved: Unresolved name 'fstop'",
            EmDiagnostics.FrequencySweepUnresolvable("Unresolved name 'fstop'").Render());

    [Fact]
    public void FrequencySweepEmpty_RendersTheOriginalSentence() =>
        Assert.Equal("The frequency sweep produced no points. Check the start, stop and step or count.",
            EmDiagnostics.FrequencySweepEmpty().Render());

    [Fact]
    public void SolveFailed_RendersTheOriginalSentence() =>
        Assert.Equal("The EM solve failed: matrix is singular",
            EmDiagnostics.SolveFailed("matrix is singular").Render());

    /// <summary>
    /// The long one, and the one most likely to have lost a clause in the move — it was a six-line
    /// concatenation of interpolated and plain fragments. <c>EmRunService.InternalPortNeedsFullWave</c>
    /// now delegates here, so this also pins that the two have not drifted apart.
    /// </summary>
    [Fact]
    public void InternalPortNeedsFullWave_RendersTheOriginalSentence()
    {
        const string expected =
            "This EM setup declares an internal port, and the analysis it resolved to is the " +
            "cross-section kernel. An internal delta gap is a cut across a conductor at a mesh gridline, and an " +
            "internal port is the foot of a via down to the ground plane — and the uniform-line " +
            "kernel never meshes the " +
            "plane at all: it solves a cross-section for per-unit-length RLGC and forms the network of a " +
            "length-L line in closed form, so its only ports are the two ends of that line, by " +
            "construction. There is nowhere for either to be. Running anyway would publish a complete " +
            "and plausible answer for your line WITHOUT the port you asked for, which is why this is " +
            "refused rather than reported. Set Analysis to the full-wave planar kernel, or change the " +
            "port back to an edge port.";

        Assert.Equal(expected, EmDiagnostics.InternalPortNeedsFullWave("cross-section kernel").Render());
        Assert.Equal(expected, EmRunService.InternalPortNeedsFullWave("cross-section kernel"));
    }

    /// <summary>A forwarded refusal must pass its text through completely untouched — no prefix, no
    /// re-wording, no quoting. It is another layer's sentence and this one does not own it.</summary>
    [Fact]
    public void Forwarded_PassesTheOriginalTextThroughUnchanged()
    {
        const string refusal = "The mesh needs 7,749 unknowns, which is past this build's ceiling.";
        Assert.Equal(refusal, EmDiagnostics.Forwarded("mesh-ceiling", refusal).Render());
    }

    [Fact]
    public void Forwarded_TreatsANullRefusalAsEmpty() =>
        Assert.Equal("", EmDiagnostics.Forwarded("kernel", null).Render());

    // ── The structure the ids exist to provide ───────────────────────────────

    /// <summary>
    /// Ids are the durable half — dedup, filtering and any future resource lookup key on them, so a
    /// duplicate would silently merge two unrelated diagnostics and a stray capital or space would
    /// make one unmatchable. Both are cheap to prevent and impossible to notice by hand.
    /// </summary>
    [Fact]
    public void EveryId_IsWellFormedAndUnique()
    {
        var all = AllDiagnostics();

        foreach (var d in all)
        {
            Assert.StartsWith("em.", d.Id, StringComparison.Ordinal);
            Assert.Matches("^[a-z0-9]+(?:[.-][a-z0-9]+)*$", d.Id);
        }

        var duplicates = all.GroupBy(d => d.Id, StringComparer.Ordinal)
                            .Where(g => g.Count() > 1)
                            .Select(g => g.Key)
                            .ToList();
        Assert.True(duplicates.Count == 0, "Duplicate diagnostic ids: " + string.Join(", ", duplicates));
    }

    /// <summary>Every argument a template names must actually be supplied — an unmatched placeholder
    /// renders as the literal <c>{name}</c>, which reaches the user as visible debris.</summary>
    [Fact]
    public void NoRenderedDiagnostic_LeavesAnUnsubstitutedPlaceholder()
    {
        foreach (var d in AllDiagnostics())
            Assert.DoesNotContain("{", d.Render(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The CLI writes these to stderr, and a diagnostic that acquired a comma decimal because of
    /// where the machine is would break every user's grep. Rendering is invariant by construction
    /// (<see cref="Diagnostic"/>); this pins it.
    /// </summary>
    [Fact]
    public void Rendering_IsCultureInvariant()
    {
        var withNumbers = Diagnostic.Create(
            "em.test.numeric", DiagnosticSeverity.Error,
            "The mesh needs {unknowns} unknowns at {freq} Hz.",
            ("unknowns", 7749), ("freq", 2_400_000_000.5));

        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            string reference = withNumbers.Render();
            Assert.Equal("The mesh needs 7749 unknowns at 2400000000.5 Hz.", reference);

            foreach (var probe in new[] { "de-DE", "fi-FI" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(probe);
                Assert.Equal(reference, withNumbers.Render());
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    /// <summary>The typed half — the whole point of not shipping a finished sentence. A consumer
    /// that wants the layout path reads it, instead of parsing the prose back apart.</summary>
    [Fact]
    public void Arguments_AreReadableWithoutParsingTheSentence()
    {
        var d = EmDiagnostics.NoLayout("Amp/layout/Amp.clay");
        Assert.Equal("Amp/layout/Amp.clay", Assert.IsType<string>(d.Arguments["layoutRef"]));
    }

    /// <summary>Severity has to survive to the Messages window, or the render point cannot pick a
    /// level and every diagnostic reads as an error.</summary>
    [Fact]
    public void RenderPoint_MapsSeverityOntoTheMessagesLevel()
    {
        Assert.Equal(MessageLevel.Info,  DiagnosticRenderer.LevelOf(EmDiagnostics.Cancelled()));
        Assert.Equal(MessageLevel.Error, DiagnosticRenderer.LevelOf(EmDiagnostics.FrequencySweepEmpty()));
        Assert.Equal(MessageLevel.Warning, DiagnosticRenderer.LevelOf(
            new Diagnostic("em.test.warn", DiagnosticSeverity.Warning, "careful")));
    }

    /// <summary>The render point is what the UI must go through, so that a resource lookup, a dedup
    /// or a filter lands in ONE place later rather than in another 118-site sweep.</summary>
    [Fact]
    public void RenderPoint_ProducesTheSameTextAsTheDiagnosticItself()
    {
        foreach (var d in AllDiagnostics())
            Assert.Equal(d.Render(), DiagnosticRenderer.Render(d));
    }

    private static List<Diagnostic> AllDiagnostics() =>
    [
        EmDiagnostics.Cancelled(),
        EmDiagnostics.NoLayout("Amp/layout/Amp.clay"),
        EmDiagnostics.NoTechnology("Amp/layout/Amp.clay"),
        EmDiagnostics.FrequencySweepUnresolvable("Unresolved name 'fstop'"),
        EmDiagnostics.FrequencySweepEmpty(),
        EmDiagnostics.SolveFailed("matrix is singular"),
        EmDiagnostics.InternalPortNeedsFullWave("cross-section kernel"),
        EmDiagnostics.Forwarded("kernel", "some kernel refusal"),
        EmDiagnostics.Forwarded("ports", "some port refusal"),
        EmDiagnostics.Forwarded("analysis-choice", "some choice refusal"),
        EmDiagnostics.Forwarded("mesh-ceiling", "some ceiling refusal"),
    ];
}
