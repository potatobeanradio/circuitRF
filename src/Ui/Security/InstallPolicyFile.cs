using System;
using System.IO;

namespace CircuitRF.Ui.Security;

/// <summary>
/// The administrator's lock: a marker file dropped BESIDE an installation that turns one of
/// circuitRF's permissions off and takes it out of the user's hands.
///
/// <para><b>One lookup, two policies.</b> <c>no-auto-update</c> (design §11) and
/// <c>no-device-workers</c> answer different questions but are found the same way, and a rule stated
/// twice is a rule that will eventually be two rules — the second copy is the one that forgets the
/// macOS case below.</para>
/// </summary>
public static class InstallPolicyFile
{
    /// <summary>
    /// True when <paramref name="fileName"/> sits at, or beside, <paramref name="installRoot"/>.
    ///
    /// <para><b>Both locations, and the second is the one that matters on macOS.</b> There the
    /// install root IS the <c>.app</c> bundle, and a file put INSIDE a bundle breaks its code
    /// signature — which is a spectacular way to disable an application while meaning to disable one
    /// of its features. So an administrator drops the marker next to the bundle, and this looks in
    /// both places rather than making them read a note about which.</para>
    /// </summary>
    public static bool PresentBeside(string? installRoot, string fileName)
    {
        if (string.IsNullOrWhiteSpace(installRoot) || string.IsNullOrWhiteSpace(fileName)) return false;

        try
        {
            if (File.Exists(Path.Combine(installRoot, fileName))) return true;

            string? beside = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(installRoot));
            return beside is not null && File.Exists(Path.Combine(beside, fileName));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
