namespace CircuitRF.Core.Devices.External;

/// <summary>
/// Whether this installation is allowed to start a device worker at all — the consent gate for
/// <b>external workers</b>, which are the second kind of program a kit can make circuitRF run.
///
/// <para><b>Why it exists.</b> A kit's <c>device-provider.json</c> names a <c>command</c> that
/// resolves against the kit's own folder (<see cref="DeviceWorkerManifest"/>), so a kit can ship an
/// executable and circuitRF starts it. That is the same class of thing as a kit's PCell generator
/// scripts, which have been gated behind an explicit prompt since B6 — and until this existed, the
/// worker half was not gated at all (security review, 2026-08-25). One of two declarations beside an
/// imported kit named a program to run and asked; the other did not.</para>
///
/// <para><b>Why the gate lives HERE and not at the two call sites.</b> There are three launch paths
/// today — the kit-declared worker a simulation resolves, the discovery worker a PDK import runs over
/// a compiled model, and <c>src/Cli</c> — and a check written at each is a check a fourth one will
/// not have. <see cref="ProcessDeviceWorkerTransport.Start"/> is the line that actually starts a
/// process, so that is where the question is asked. Same reasoning as
/// <c>UpdateStager.Promote</c>'s live-pointer refusal and <c>UpdateReclaimer.Remove</c>'s
/// running-directory refusal: the check belongs at the line that consumes the value.</para>
///
/// <para><b>The default is ALLOWED, and it has to be.</b> <c>src/Core</c> cannot read
/// <c>AppPreferences</c> — that is <c>src/Ui</c>'s, and the UI firewall is not negotiable — so the
/// policy is a hook the application installs. A build that has not installed one (the CLI, a test,
/// a headless tool) behaves exactly as it always did. The application installs it in <c>Main</c>,
/// before anything can resolve a device, and <c>ExternalWorkerConsentTests</c> pins that all three
/// entry points do.</para>
/// </summary>
public static class DeviceWorkerPolicy
{
    /// <summary>
    /// Asked before any worker process starts, with the provider it would serve (empty when the
    /// caller has no name to give). Returns <b>null to allow</b>, or the sentence to refuse with —
    /// so the reason travels with the refusal instead of being reconstructed by whoever catches it.
    ///
    /// <para>Null (the default) is "nobody installed a policy", which allows.</para>
    /// </summary>
    public static Func<string, string?>? Gate { get; set; }

    /// <summary>
    /// Why a worker for <paramref name="forProvider"/> may not start, or null when it may.
    ///
    /// <para><b>A gate that throws allows</b>, deliberately. This is a user preference, not a trust
    /// anchor: the thing it protects against is a kit running code the user did not want run, and the
    /// stated default is on. A policy read that fails is a bug in the policy, and turning it into a
    /// simulation that cannot run would be a worse failure than the one it is guarding.</para>
    /// </summary>
    public static string? RefusalReason(string forProvider = "")
    {
        Func<string, string?>? gate = Gate;
        if (gate is null) return null;

        try
        {
            string? why = gate(forProvider ?? "");
            return string.IsNullOrWhiteSpace(why) ? null : why;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when a worker may be started. The readable spelling of "no refusal".</summary>
    public static bool MayStart(string forProvider = "") => RefusalReason(forProvider) is null;
}
