using System.Text.RegularExpressions;

namespace CircuitRF.Core.Devices.External;

/// <summary>
/// Turns a worker's raw error output into an explanation, where one particular class of failure is
/// recognisable and the raw text is not actionable on its own.
///
/// <para><b>Why this exists at all.</b> A worker's stderr is already attached to every failure a
/// user sees, and for most failures that is the right amount of help: the worker says what went
/// wrong in its own words. There is one exception, and it is the worst-behaved failure macOS has to
/// offer here — a device model library that <c>dlopen</c> refuses because macOS QUARANTINED it.
/// Nothing is broken. The file is intact, circuitRF's own signing is irrelevant, and the model
/// works perfectly on the machine it was built on. What the user gets instead is 400 characters of
/// dyld search-path noise with the actual reason at the end of the third clause.</para>
///
/// <para><b>There is no prompt for this, which is why it has to be explained.</b> Measured on
/// macOS 26, in the kernel log: <c>ASP: Library load (… -&gt; …/model.osdi) rejected: library load
/// disallowed by system policy</c>. Unlike a blocked application, a blocked LIBRARY produces no
/// dialog and no "Allow Anyway" entry in System Settings — there is nothing for the user to click,
/// ever. Approving circuitRF itself does not help either: a kit is installed separately and carries
/// its own quarantine attribute.</para>
/// </summary>
public static class WorkerOutputDiagnosis
{
    /// <summary>
    /// dyld's wording for the quarantine refusal. Distinct from every other load failure, and
    /// verified against the alternative rather than assumed: a library refused by LIBRARY
    /// VALIDATION (hardened runtime, different Team ID) says "mapping process and mapped file
    /// (non-platform) have different Team IDs" instead. The two causes have different remedies, so
    /// the phrases are matched separately and neither is treated as the other.
    /// </summary>
    private const string QuarantinePhrase = "library load disallowed by system policy";

    /// <summary>The library-validation refusal. See <see cref="QuarantinePhrase"/>.</summary>
    private const string TeamIdPhrase = "have different Team IDs";

    /// <summary>
    /// The path dyld was asked for. Anchored on the <c>dlopen(&lt;path&gt;, 0x…)</c> prefix rather
    /// than on the quoted repetitions further along the message, because that first occurrence is
    /// the path the CALLER passed — the others are search-path candidates dyld invented, and naming
    /// one of those in advice would send the user to a file that does not exist.
    /// </summary>
    private static readonly Regex DlopenPath =
        new(@"dlopen\((?<path>.+?),\s*0x", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// An actionable explanation of <paramref name="workerOutput"/>, or null when there is nothing
    /// to add beyond what the worker already said.
    ///
    /// <para>Returns null far more often than not, and that is the intended ratio. This is not a
    /// general-purpose error rewriter: a worker that explains itself is left alone, because a layer
    /// that paraphrases every message eventually paraphrases one it has misunderstood.</para>
    /// </summary>
    public static string? Explain(string? workerOutput)
    {
        if (string.IsNullOrWhiteSpace(workerOutput)) return null;

        if (workerOutput.Contains(QuarantinePhrase, StringComparison.Ordinal))
            return QuarantineExplanation(PathFrom(workerOutput));

        if (workerOutput.Contains(TeamIdPhrase, StringComparison.Ordinal))
            return TeamIdExplanation(PathFrom(workerOutput));

        return null;
    }

    private static string? PathFrom(string workerOutput)
    {
        var match = DlopenPath.Match(workerOutput);
        if (!match.Success) return null;

        string path = match.Groups["path"].Value.Trim().Trim('\'', '"');
        return path.Length == 0 ? null : path;
    }

    /// <summary>
    /// <b>The remedy names the kit's DIRECTORY, not the one file.</b> Quarantine is applied per
    /// file, and a kit is many files — the model that failed first is rarely the only one that
    /// would. Clearing just the named library gets the user one model further and then stops, which
    /// reads like a different fault.
    /// </summary>
    private static string QuarantineExplanation(string? path)
    {
        string? directory = null;
        try { directory = path is null ? null : Path.GetDirectoryName(path); }
        catch (ArgumentException) { /* an unparseable path is still worth explaining */ }

        string target = string.IsNullOrEmpty(directory) ? "<the kit's folder>" : Quote(directory);

        var message = new System.Text.StringBuilder();
        message.Append("macOS refused to load this model library because it is QUARANTINED");
        if (path is not null) message.Append(": ").Append(path);
        message.Append('.');

        message.Append(Environment.NewLine).Append(Environment.NewLine);
        message.Append(
            "That attribute is put on anything downloaded, and it is not a sign that the file is " +
            "damaged or that anything is wrong with circuitRF. macOS shows no prompt for a blocked " +
            "LIBRARY and adds no entry to System Settings, so there is nothing to allow — and " +
            "approving circuitRF itself does not cover a kit installed separately.");

        message.Append(Environment.NewLine).Append(Environment.NewLine);
        message.Append("Clear it on the whole kit, then run again:");
        message.Append(Environment.NewLine);
        message.Append("    xattr -dr com.apple.quarantine ").Append(target);

        return message.ToString();
    }

    private static string TeamIdExplanation(string? path)
    {
        var message = new System.Text.StringBuilder();
        message.Append("macOS refused to load this model library because it is signed by someone " +
                       "other than the signer of the program loading it");
        if (path is not null) message.Append(": ").Append(path);
        message.Append('.');

        message.Append(Environment.NewLine).Append(Environment.NewLine);
        message.Append(
            "This is library validation, not quarantine, so clearing the quarantine attribute will " +
            "not change it. A vendor's model is never signed with our certificate, so the worker " +
            "that loads one is signed with com.apple.security.cs.disable-library-validation " +
            "(src/Ui/Assets/macOS/Entitlements.plist). Seeing this means that entitlement did not " +
            "reach the worker in this build — which is a packaging fault, not anything the user can " +
            "fix.");

        return message.ToString();
    }

    /// <summary>Shell-quotes only when it would otherwise be wrong to paste — a path with a space.</summary>
    private static string Quote(string path) =>
        path.Contains(' ', StringComparison.Ordinal) ? "\"" + path + "\"" : path;
}
