using System;
using System.IO;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Updates;

/// <summary>What this launch should do about the Release Notes dialog.</summary>
public enum ReleaseNotesDecision
{
    /// <summary>Nothing. This version's notes have already been dealt with.</summary>
    None,

    /// <summary>
    /// Record this version as seen and show nothing — the clean-install case, and the case where the
    /// user has turned the dialog off. Recording either way is what stops a backlog accumulating.
    /// </summary>
    RecordSilently,

    /// <summary>Fetch the notes and open the dialog.</summary>
    Show,
}

/// <summary>
/// The <b>one</b> place that decides whether this launch opens the Release Notes dialog, and the only
/// thing that reads <see cref="AppPreferences.ShowReleaseNotes"/>.
///
/// <para><b>The whole feature turns on a distinction the state file cannot make on its own:</b> a
/// launch that finds no recorded version is either a brand new installation — which must show nothing,
/// because a user who has just installed circuitRF for the first time has no "what changed" to be told
/// — or an existing installation running a build that has just gained this feature, which should. The
/// discriminator is whether this installation had any per-user state at all before the launch started,
/// which is why <see cref="CaptureAtStartup"/> runs in <c>Main</c> before Avalonia and before anything
/// can write a preferences file and make the answer yes.</para>
///
/// <para><b>Only circuitRF captures and only circuitRF shows.</b> One <c>preferences.json</c> and one
/// <c>state.json</c> serve all three applications, so a wBond launch that recorded a version as seen
/// would silently consume circuitRF's one showing of it. harmonicaRF and wBond call neither half.</para>
/// </summary>
public static class ReleaseNotesGate
{
    private static bool _installationExisted;
    private static bool _captured;

    /// <summary>
    /// Answers "did this installation exist before this launch", once, from <c>Main</c>.
    ///
    /// <para><b>Called before <see cref="UpdateStartup.RunBeforeUi"/>, not after.</b> That call writes
    /// <c>state.json</c> on every path that applies an update, so asking afterwards would report a
    /// fresh installation as an existing one. Ordering is the entire mechanism here, so it is stated
    /// rather than left to be inferred from the call site.</para>
    ///
    /// <para>Idempotent: a second call is ignored, so the macOS <c>execv</c> path — where a launch
    /// restarts inside a new process image — cannot re-answer the question against the state the
    /// first answer produced.</para>
    /// </summary>
    public static void CaptureAtStartup()
    {
        if (_captured) return;
        _captured = true;

        try
        {
            _installationExisted = AppPreferencesIo.FileExists || File.Exists(UpdatePaths.StateFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Unreadable state directory: treat it as a fresh install, which shows nothing. The
            // failure mode of guessing wrong in the other direction is a dialog on a clean machine.
            _installationExisted = false;
        }
    }

    /// <summary>What <see cref="CaptureAtStartup"/> found. False until it has run.</summary>
    public static bool InstallationExisted => _installationExisted;

    /// <summary>
    /// Whether the user wants to see release notes at all. Null in the file means yes — see
    /// <see cref="AppPreferences.ShowReleaseNotes"/> for why absence has to be the default.
    /// </summary>
    public static bool ShowPreference => AppPreferencesIo.Load().ShowReleaseNotes ?? true;

    /// <summary>Writes the preference. The dialog's own checkbox and Settings both come through here.</summary>
    public static void SetShowPreference(bool show)
        => AppPreferencesIo.Update(p => p.ShowReleaseNotes = show);

    /// <summary>
    /// Whether this installation may contact the update host at all.
    ///
    /// <para><b>The release notes are an outbound network call, so they are bound by the same
    /// overrides the update check is.</b> An administrator who drops a <c>no-auto-update</c> file
    /// beside the install, or an environment carrying <c>CRF_NO_UPDATE_CHECK=1</c>, has said this
    /// binary does not talk to the update host — and a second code path that fetches from it anyway is
    /// precisely what <see cref="UpdatePolicy"/>'s own rule about forgotten overrides forbids.</para>
    ///
    /// <para><b>Only the OVERRIDES, not the plain preference.</b> A user who turned automatic updates
    /// off still installs new versions by hand, and their notes are still what they want to read —
    /// which is what the Settings checkbox promises by not being a sub-item of that one.</para>
    /// </summary>
    public static bool NetworkPermitted => !UpdatePolicy.Current.IsOverridden;

    /// <summary>The real decision, against the live state file, preferences and running version.</summary>
    public static ReleaseNotesDecision Resolve()
    {
        // Recorded rather than left pending, so lifting the policy later offers the NEXT version's
        // notes instead of replaying every version installed while it was in force.
        if (!NetworkPermitted) return ReleaseNotesDecision.RecordSilently;

        return Decide(_installationExisted, UpdateStateIo.Load().ReleaseNotesShownFor,
                      AppVersion.Display, ShowPreference);
    }

    /// <summary>
    /// The testable form. Order matters and each rule is one of the owner's four:
    ///
    /// <list type="number">
    /// <item><description>already shown for this version → nothing, on every subsequent launch of it</description></item>
    /// <item><description>no installation before this launch → a clean system, which never sees them</description></item>
    /// <item><description>turned off → recorded, so turning it back on does not replay a backlog</description></item>
    /// <item><description>otherwise → show</description></item>
    /// </list>
    ///
    /// <para>Rule 2 also covers the one-off case of an existing installation whose state directory has
    /// been wiped: it costs that user one release's notes and cannot produce the failure that matters,
    /// which is a first-run dialog on a machine that has never run circuitRF.</para>
    /// </summary>
    public static ReleaseNotesDecision Decide(bool installationExisted, string? shownFor,
                                              string currentVersion, bool showPreference)
    {
        if (string.IsNullOrWhiteSpace(currentVersion)) return ReleaseNotesDecision.None;
        if (string.Equals(shownFor, currentVersion, StringComparison.Ordinal)) return ReleaseNotesDecision.None;

        if (!installationExisted) return ReleaseNotesDecision.RecordSilently;
        if (!showPreference)      return ReleaseNotesDecision.RecordSilently;

        return ReleaseNotesDecision.Show;
    }

    /// <summary>
    /// Records <paramref name="version"/> as dealt with.
    ///
    /// <para><b>Called when the dialog OPENS, not when it closes, and not only when notes were
    /// found.</b> A network failure produces the dialog's "couldn't reach the repository" form, and
    /// that is still a showing — retrying it on every launch until the network happens to be up would
    /// put an error dialog in front of an offline user indefinitely. The dialog names the repository
    /// so they can look for themselves whenever they like.</para>
    /// </summary>
    public static void MarkShown(string version)
        => UpdateStateIo.Update(s => s.ReleaseNotesShownFor = version);

    /// <summary>Test seam: sets what <see cref="CaptureAtStartup"/> would have found.</summary>
    internal static void OverrideCaptureForTests(bool installationExisted)
    {
        _captured           = true;
        _installationExisted = installationExisted;
    }
}
