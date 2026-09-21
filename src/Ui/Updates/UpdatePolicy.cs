using System;
using System.IO;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Updates;

/// <summary>Which override, if any, is forcing automatic updates off.</summary>
public enum UpdateOverride
{
    None,

    /// <summary>A <c>no-auto-update</c> file beside the install. Beats everything, including the preference.</summary>
    PolicyFile,

    /// <summary><c>CRF_NO_UPDATE_CHECK=1</c> in the environment. Beats the preference.</summary>
    Environment,
}

/// <summary>The effective setting, and why it is what it is.</summary>
/// <param name="AutomaticUpdates">Whether the subsystem may run at all.</param>
/// <param name="IncludeBetas">Whether prereleases are on the channel.</param>
/// <param name="Override">What is forcing the answer, if anything.</param>
/// <param name="Reason">User-facing sentence for the disabled checkbox. Empty when nothing overrides.</param>
public sealed record UpdatePolicyState(
    bool AutomaticUpdates, bool IncludeBetas, UpdateOverride Override, string Reason)
{
    /// <summary>True when the user's own checkbox cannot change the answer, so it renders disabled.</summary>
    public bool IsOverridden => Override != UpdateOverride.None;
}

/// <summary>
/// The <b>one</b> accessor that resolves whether circuitRF checks for updates.
///
/// <list type="table">
/// <item><term>1</term><description>the <c>no-auto-update</c> policy file beside the install — beats everything</description></item>
/// <item><term>2</term><description><c>CRF_NO_UPDATE_CHECK=1</c> in the environment — beats the preference</description></item>
/// <item><term>3</term><description><see cref="AppPreferences.AutomaticUpdates"/> — beats the default</description></item>
/// <item><term>4</term><description>the default, which is ON</description></item>
/// </list>
///
/// <para>The policy file overriding the preference is the whole point of it: an administrator can
/// deploy an installation the user cannot re-enable.</para>
///
/// <para><b>Nothing else may read <see cref="AppPreferences.AutomaticUpdates"/> directly.</b> One
/// accessor, or the override precedence will be right in one place and absent in another — and the
/// place it is absent is the one that opens a socket on a machine whose administrator forbade it.</para>
/// </summary>
public static class UpdatePolicy
{
    /// <summary>The file an administrator drops beside the install to disable updates permanently.</summary>
    public const string PolicyFileName = "no-auto-update";

    /// <summary>The environment kill switch.</summary>
    public const string EnvironmentVariable = "CRF_NO_UPDATE_CHECK";

    /// <summary>
    /// Recomputed on every read, deliberately: the settings dialog changes the preference and then
    /// asks again, and a cached value would show the user the state before their own click.
    /// </summary>
    public static UpdatePolicyState Current => Resolve(UpdateInstallSite.Detect().Root, AppPreferencesIo.Load());

    /// <summary>The testable form — no real install, no real preferences file.</summary>
    public static UpdatePolicyState Resolve(string installRoot, AppPreferences prefs)
    {
        // Both defaults come from the nullable idiom rather than a line of first-run seeding: a
        // machine with NO preferences.json at all reads automatic updates ON and betas ON.
        //
        // Null is not merely "unset", it is "the user has never touched that box" — the settings
        // dialog's populate guard is what keeps that true — so changing the fallback here reaches
        // an EXISTING installation that never expressed a preference, and provably cannot reach
        // one that stored an explicit false. That is the whole migration; there is no seeding pass
        // and nothing to write.
        bool wanted = prefs.AutomaticUpdates   ?? true;
        bool betas  = prefs.IncludeBetaUpdates ?? true;

        if (PolicyFilePresent(installRoot))
            return new UpdatePolicyState(false, false, UpdateOverride.PolicyFile,
                "Automatic updates are disabled for this installation by an administrator "
                + $"(a '{PolicyFileName}' file beside the application).");

        if (System.Environment.GetEnvironmentVariable(EnvironmentVariable) == "1")
            return new UpdatePolicyState(false, false, UpdateOverride.Environment,
                $"Automatic updates are disabled by {EnvironmentVariable}=1 in this environment.");

        return new UpdatePolicyState(wanted, betas, UpdateOverride.None, "");
    }

    /// <summary>
    /// Looks for the policy file beside the install. On macOS the install root is the <c>.app</c>
    /// itself, and an administrator drops the file NEXT TO the bundle rather than inside it — putting
    /// it inside would break the bundle's code signature, which is a spectacular way to disable an
    /// application while meaning to disable its updater.
    /// </summary>
    public static bool PolicyFilePresent(string installRoot)
        => Security.InstallPolicyFile.PresentBeside(installRoot, PolicyFileName);
}
