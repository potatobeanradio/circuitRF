using System;
using CircuitRF.Core.Devices.External;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Security;

/// <summary>Which override, if any, is forcing external device workers off.</summary>
public enum ExternalWorkerOverride
{
    None,

    /// <summary>A <c>no-device-workers</c> file beside the install. Beats everything, including the preference.</summary>
    PolicyFile,

    /// <summary><c>CRF_NO_DEVICE_WORKERS=1</c> in the environment. Beats the preference.</summary>
    Environment,
}

/// <summary>The effective setting, and why it is what it is.</summary>
/// <param name="Allowed">Whether a kit's worker may be started.</param>
/// <param name="Override">What is forcing the answer, if anything.</param>
/// <param name="Reason">User-facing sentence for the disabled checkbox. Empty when nothing overrides.</param>
public sealed record ExternalWorkerPolicyState(
    bool Allowed, ExternalWorkerOverride Override, string Reason)
{
    /// <summary>True when the user's own checkbox cannot change the answer, so it renders disabled.</summary>
    public bool IsOverridden => Override != ExternalWorkerOverride.None;
}

/// <summary>
/// The <b>one</b> accessor that resolves whether circuitRF may run a kit's external device worker.
///
/// <list type="table">
/// <item><term>1</term><description>the <c>no-device-workers</c> policy file beside the install — beats everything</description></item>
/// <item><term>2</term><description><c>CRF_NO_DEVICE_WORKERS=1</c> in the environment — beats the preference</description></item>
/// <item><term>3</term><description><see cref="AppPreferences.ExternalDeviceWorkers"/> — beats the default</description></item>
/// <item><term>4</term><description>the default, which is <b>ON</b></description></item>
/// </list>
///
/// <para><b>Deliberately the same shape as <c>UpdatePolicy</c>.</b> The two questions that tab now
/// answers — may this binary FETCH code, may it RUN somebody else's — should not have two different
/// precedence rules, two different kill switches and two different spellings of "an administrator
/// decided this". A user auditing what circuitRF is permitted to do reads one pattern twice.</para>
///
/// <para><b>ON by default, and that is a compatibility decision rather than a security one.</b> Every
/// kit installed before this existed evaluates its devices through a worker; shipping this off would
/// break those workspaces silently at the next Run, for a gate the user never asked for. The value of
/// the gate is that there is now a place to say no — and one an administrator can hold shut.</para>
///
/// <para><b>Nothing else may read <see cref="AppPreferences.ExternalDeviceWorkers"/> directly.</b>
/// One accessor, or the override precedence will be right in one place and absent in another — and
/// the place it is absent is the one that starts a vendor's executable on a machine whose
/// administrator forbade it.</para>
/// </summary>
public static class ExternalWorkerPolicy
{
    /// <summary>The file an administrator drops beside the install to forbid workers permanently.</summary>
    public const string PolicyFileName = "no-device-workers";

    /// <summary>The environment kill switch.</summary>
    public const string EnvironmentVariable = "CRF_NO_DEVICE_WORKERS";

    /// <summary>
    /// Recomputed on every read, deliberately — the same reason <c>UpdatePolicy.Current</c> is: the
    /// settings dialog changes the preference and then asks again, and the gate is consulted at each
    /// launch so unchecking the box takes effect on the next Run rather than the next session.
    /// </summary>
    public static ExternalWorkerPolicyState Current
        => Resolve(Updates.UpdateInstallSite.Detect().Root, AppPreferencesIo.Load());

    /// <summary>The testable form — no real install, no real preferences file.</summary>
    public static ExternalWorkerPolicyState Resolve(string installRoot, AppPreferences prefs)
    {
        // Absence IS the default: a machine with no preferences.json at all reads ON, with no
        // first-run seeding anywhere.
        bool wanted = prefs.ExternalDeviceWorkers ?? true;

        if (InstallPolicyFile.PresentBeside(installRoot, PolicyFileName))
            return new ExternalWorkerPolicyState(false, ExternalWorkerOverride.PolicyFile,
                "External device workers are disabled for this installation by an administrator "
                + $"(a '{PolicyFileName}' file beside the application).");

        if (Environment.GetEnvironmentVariable(EnvironmentVariable) == "1")
            return new ExternalWorkerPolicyState(false, ExternalWorkerOverride.Environment,
                $"External device workers are disabled by {EnvironmentVariable}=1 in this environment.");

        return new ExternalWorkerPolicyState(wanted, ExternalWorkerOverride.None, "");
    }

    /// <summary>
    /// Installs this policy as <see cref="DeviceWorkerPolicy"/>'s gate. Called once from each
    /// application's <c>Main</c>, before anything can resolve a device.
    ///
    /// <para><b>An application that forgets this call runs workers.</b> That is the stated default and
    /// not a hole, but it is also the kind of omission a new entry point makes silently — so
    /// <c>tests/Ui.Tests/Security/ExternalWorkerConsentTests</c> scans the three <c>Program*.cs</c>
    /// files for it, the same way <c>PCellTrustGateTests</c> pins that the PCell resolver is always
    /// handed a trust policy.</para>
    /// </summary>
    public static void Install() => DeviceWorkerPolicy.Gate = RefusalFor;

    /// <summary>
    /// The gate itself: null when a worker may start, otherwise the sentence to refuse with — which
    /// names <b>what stopped it and where to change that</b>, because this surfaces as a run error and
    /// "the kit could not be started" would send the reader to look at the kit.
    /// </summary>
    public static string? RefusalFor(string forProvider)
    {
        ExternalWorkerPolicyState policy = Current;
        if (policy.Allowed) return null;

        string who = string.IsNullOrWhiteSpace(forProvider) || forProvider == "osdi"
            ? "a kit's devices cannot be evaluated"
            : $"the kit '{forProvider}' cannot evaluate its devices";

        return policy.IsOverridden
            ? $"{policy.Reason} So {who}."
            : "circuitRF is not allowed to run external device workers on this machine, so "
              + $"{who}. Settings, under Security & Permissions, turns them back on.";
    }
}
