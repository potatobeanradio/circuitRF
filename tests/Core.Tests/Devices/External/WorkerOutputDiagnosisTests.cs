using CircuitRF.Core.Devices.External;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.External;

/// <summary>
/// <b>The quarantined-model-library explanation.</b> A kit downloaded from a vendor carries macOS's
/// <c>com.apple.quarantine</c> attribute, and <c>dlopen</c> then refuses it outright.
///
/// <para><b>Why this needs translating when circuitRF's other worker failures do not.</b> There is
/// no prompt and nothing to allow. Measured on macOS 26, in the kernel log:
/// <c>ASP: Library load (… -&gt; …/model.osdi) rejected: library load disallowed by system
/// policy</c> — unlike a blocked application, a blocked LIBRARY produces no dialog and no System
/// Settings entry, so a user who knows the "Open Anyway" routine has nowhere to apply it. Approving
/// circuitRF does not help either: the kit is installed separately and carries its own attribute.
/// The raw dyld text says so only in the third clause of four hundred characters of search-path
/// noise, and reads far more like a corrupt file than like a working one macOS declined to open.</para>
///
/// <para><b>The fixtures are real output</b>, captured from this machine rather than composed to
/// match the matcher — including the repeated quoted paths and the Cryptexes candidate dyld invents,
/// which is exactly the noise the path extraction has to see past.</para>
/// </summary>
public class WorkerOutputDiagnosisTests
{
    /// <summary>Verbatim stderr from osdi-worker asked to load a quarantined model.</summary>
    private const string QuarantinedOutput =
        "osdi-worker: dlopen failed: dlopen(/Users/x/kits/vendor/models/bsim.osdi, 0x0002): tried: " +
        "'/Users/x/kits/vendor/models/bsim.osdi' (code signature in " +
        "<5B073811-0094-3E77-95AB-669499A5572C> '/Users/x/kits/vendor/models/bsim.osdi' not valid " +
        "for use in process: library load disallowed by system policy), " +
        "'/System/Volumes/Preboot/Cryptexes/OS/Users/x/kits/vendor/models/bsim.osdi' (no such file), " +
        "'/Users/x/kits/vendor/models/bsim.osdi' (code signature in " +
        "<5B073811-0094-3E77-95AB-669499A5572C> '/Users/x/kits/vendor/models/bsim.osdi' not valid " +
        "for use in process: library load disallowed by system policy)";

    /// <summary>
    /// The OTHER refusal, which shares the "not valid for use in process" prefix and means something
    /// else entirely. Captured the same way, from a hardened-runtime process with library validation
    /// left on, loading an unquarantined library.
    /// </summary>
    private const string TeamIdOutput =
        "osdi-worker: dlopen failed: dlopen(/Users/x/kits/vendor/models/bsim.osdi, 0x0002): tried: " +
        "'/Users/x/kits/vendor/models/bsim.osdi' (code signature in " +
        "<5B073811-0094-3E77-95AB-669499A5572C> '/Users/x/kits/vendor/models/bsim.osdi' not valid " +
        "for use in process: mapping process and mapped file (non-platform) have different Team IDs)";

    [Fact]
    public void AQuarantinedLibrary_IsNamedAsQuarantined_NotAsBroken()
    {
        string? explanation = WorkerOutputDiagnosis.Explain(QuarantinedOutput);

        Assert.NotNull(explanation);
        Assert.Contains("QUARANTINED", explanation, StringComparison.Ordinal);

        // It must say the file is FINE. The whole reason this text exists is that the raw message
        // reads like corruption, and a user who concludes their kit is damaged re-downloads it —
        // arriving at an identically quarantined copy.
        Assert.Contains("not a sign that the file is damaged", explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// The remedy has to be runnable as printed. A user meeting this has no other route: there is no
    /// dialog to answer, so a command they can paste is the entire remedy.
    /// </summary>
    [Fact]
    public void TheRemedyNamesTheKitsFolder_NotTheOneLibraryThatHappenedToFailFirst()
    {
        string? explanation = WorkerOutputDiagnosis.Explain(QuarantinedOutput);

        Assert.NotNull(explanation);
        Assert.Contains("xattr -dr com.apple.quarantine /Users/x/kits/vendor/models",
                        explanation, StringComparison.Ordinal);

        // Quarantine is per file and a kit is many files, so clearing only the named library gets
        // the user one model further and then stops — which reads as a new and different fault.
        Assert.DoesNotContain("xattr -dr com.apple.quarantine /Users/x/kits/vendor/models/bsim.osdi",
                              explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// The path taken must be the one the CALLER asked for. dyld's message repeats the path several
    /// times and also lists candidates it invented — most visibly a
    /// <c>/System/Volumes/Preboot/Cryptexes/OS/…</c> one that does not exist. Naming that in advice
    /// would send the user to clear an attribute on a file that is not there.
    /// </summary>
    [Fact]
    public void TheCryptexesCandidateDyldInvented_IsNeverTheOneQuoted()
    {
        string? explanation = WorkerOutputDiagnosis.Explain(QuarantinedOutput);

        Assert.NotNull(explanation);
        Assert.DoesNotContain("Cryptexes", explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// Library validation is a DIFFERENT failure with a different remedy, and telling someone to
    /// clear a quarantine attribute that is not set would waste the one instruction they were given.
    /// Both messages contain "not valid for use in process", so matching on that prefix would
    /// conflate them; the distinguishing clauses are matched instead.
    /// </summary>
    [Fact]
    public void LibraryValidation_IsNotReportedAsQuarantine()
    {
        string? explanation = WorkerOutputDiagnosis.Explain(TeamIdOutput);

        Assert.NotNull(explanation);
        Assert.DoesNotContain("QUARANTINED", explanation, StringComparison.Ordinal);
        Assert.DoesNotContain("xattr", explanation, StringComparison.Ordinal);
        Assert.Contains("library validation", explanation, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <b>Silence is the common case and the correct one.</b> A worker that explains itself is left
    /// alone: a layer that paraphrases every message eventually paraphrases one it misunderstood,
    /// and the worker's own words are the only description of most failures.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("senior_worker: eval: SIGSEGV caught at point 41")]
    [InlineData("osdi-worker: dlopen failed: dlopen(/x/y.osdi, 0x0002): tried: '/x/y.osdi' (no such file)")]
    [InlineData("model: could not open parameter file 'corner.lib'")]
    public void AnythingElse_IsLeftToTheWorkersOwnWords(string? output)
        => Assert.Null(WorkerOutputDiagnosis.Explain(output));

    /// <summary>
    /// A malformed message must not cost the user the explanation. The reason is recognisable from
    /// the phrase alone; the path is a nicety on top, so losing it degrades the advice rather than
    /// suppressing it.
    /// </summary>
    [Fact]
    public void APhraseWithNoUsablePath_StillExplainsItself()
    {
        string? explanation = WorkerOutputDiagnosis.Explain(
            "osdi-worker: dlopen failed: library load disallowed by system policy");

        Assert.NotNull(explanation);
        Assert.Contains("QUARANTINED", explanation, StringComparison.Ordinal);
        Assert.Contains("xattr -dr com.apple.quarantine", explanation, StringComparison.Ordinal);
    }
}
