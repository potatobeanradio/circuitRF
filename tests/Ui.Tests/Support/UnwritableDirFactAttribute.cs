using System;
using System.IO;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// SL2 §5 item 2: like <see cref="FactAttribute"/>, but SKIPS with a stated reason on a platform (or
/// in a process) that cannot actually produce an unwritable directory.
///
/// <para><b>Why this exists — the platform problem, named rather than discovered.</b> A directory
/// that is genuinely unwritable is one <c>chmod 500</c> away on macOS and Linux and an ACL edit away
/// on Windows, and a test process running elevated may be able to write to it regardless. A gate
/// that silently passes on one platform is not a gate, so the real-filesystem probe test carries
/// this attribute and reports <i>Skipped, with a reason naming the platform</i> rather than passing
/// vacuously. The BEHAVIOUR tests (R-sl2-5 … R-sl2-13) do not need it: they drive
/// <see cref="WorkspaceWritability.WritabilityProbe"/> and run identically everywhere.</para>
///
/// <para>The capability is established by DOING it — make a directory, remove write permission, try
/// to create a file in it — for the same reason R-sl2-1 probes rather than reading an attribute.
/// Asking the operating system its name would answer a different question.</para>
/// </summary>
public sealed class UnwritableDirFactAttribute : FactAttribute
{
    public UnwritableDirFactAttribute()
    {
        if (Reason() is { } why) Skip = why;
    }

    /// <summary>Null when the platform can express an unwritable directory here; otherwise the
    /// sentence to skip with.</summary>
    private static string? Reason()
    {
        if (OperatingSystem.IsWindows())
            return "This process cannot make a directory unwritable on Windows without an ACL edit, " +
                   "and a test host running elevated could write to it anyway. The behavioural gate " +
                   "(WritabilityProbe seam) covers Windows; the real-filesystem probe does not.";

        string dir = Path.Combine(Path.GetTempPath(), "crf_ro_cap_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(dir);
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                File.WriteAllText(Path.Combine(dir, "probe"), "");
                return "This process can write into a mode-500 directory (running as root?), so the " +
                       "real-filesystem probe cannot be exercised here.";
            }
            catch { return null; } // refused, as it should be — the capability exists
        }
        catch (Exception ex)
        {
            return $"Could not set up an unwritable directory to test against: {ex.Message}";
        }
        finally
        {
            try
            {
                File.SetUnixFileMode(dir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Directory.Delete(dir, recursive: true);
            }
            catch { /* best effort */ }
        }
    }
}
