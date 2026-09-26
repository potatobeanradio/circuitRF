// brief-em3d-26 — the ONE door to the Windows Subsystem for Linux.
//
// Everything circuitRF asks of a Linux distribution on Windows goes through wsl.exe with an argument
// LIST, and through this interface — which is what lets every gate of the brief run on macOS and Linux
// against a fake (tests/Ui.Tests/Em3d/WslLocationTests.cs), and what lets a test read back every
// command that was issued (gate 4: no /mnt/ path; gate 6: the kill sequence). Nothing in the routine
// gate starts a real wsl.exe.
//
// Two directions of file access, and only these (R-em3d26-4):
//   * Linux → Windows: the \\wsl.localhost\<distro> prefix (WindowsPath), which is how the Windows side
//     reads and writes a distribution's own Linux filesystem — mesh and config in, CSVs out.
//   * Windows → Linux: `wslpath -u` inside the distribution (WslSession.ToLinux), never a hand-built
//     /mnt/<drive> string.

using System.Diagnostics;
using System.Text;

namespace CircuitRF.Design.Em3d.Wsl;

/// <summary>What one short wsl.exe call returned. <see cref="Stdout"/> is kept as BYTES: wsl.exe's own
/// output (a listing, an error) is UTF-16LE when redirected, while a Linux program's is UTF-8, and only
/// the caller knows which it asked for.</summary>
/// <param name="Failure">Set when wsl.exe could not be started or did not answer in time.</param>
public sealed record WslOutput(int ExitCode, byte[] Stdout, byte[] Stderr, string? Failure)
{
    /// <summary>A Linux program's output: UTF-8.</summary>
    public string Text => Encoding.UTF8.GetString(Stdout);

    /// <summary>wsl.exe's own words, from either stream, decoded as it wrote them.</summary>
    public string Message => (WslText.Decode(Stdout) + "\n" + WslText.Decode(Stderr)).Trim();

    public bool Ok => Failure is null && ExitCode == 0;
}

/// <summary>
/// wsl.exe, and the \\wsl.localhost\ share. The real implementation is <see cref="WslExe"/>; a test
/// supplies a fake that records the argument lists and maps the share onto a local directory.
/// </summary>
public interface IWsl
{
    /// <summary>Whether wsl.exe exists on this computer — false where the Windows feature was never
    /// enabled (and on every other operating system).</summary>
    bool Available { get; }

    /// <summary>Runs wsl.exe with <paramref name="arguments"/> and waits for it.</summary>
    WslOutput Run(IReadOnlyList<string> arguments, TimeSpan timeout);

    /// <summary>A wsl.exe the caller starts and streams itself — a Palace run, an install step.</summary>
    ProcessStartInfo StartInfo(IReadOnlyList<string> arguments);

    /// <summary>The Windows path of <paramref name="linuxPath"/> inside <paramref name="distribution"/>:
    /// the <c>\\wsl.localhost\&lt;distro&gt;</c> share.</summary>
    string WindowsPath(string distribution, string linuxPath);
}

/// <summary>The real wsl.exe (Windows only).</summary>
public sealed class WslExe : IWsl
{
    /// <summary>The share every distribution's filesystem is reached through (Windows 11, and WSL from the
    /// Store on Windows 10). The older <c>\\wsl$</c> spelling names the same share.</summary>
    public const string SharePrefix = @"\\wsl.localhost\";

    private static string Exe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");

    public bool Available => OperatingSystem.IsWindows() && File.Exists(Exe);

    public ProcessStartInfo StartInfo(IReadOnlyList<string> arguments)
    {
        var psi = new ProcessStartInfo(Exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
        };
        foreach (string a in arguments) psi.ArgumentList.Add(a);
        // WSL_UTF8=1 switches wsl.exe's OWN messages to UTF-8. Removed so they are always the UTF-16LE the
        // decoder expects: a setting in the user's environment must not change what a parser reads.
        psi.Environment.Remove("WSL_UTF8");
        return psi;
    }

    public WslOutput Run(IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        try
        {
            var psi = StartInfo(arguments);
            using var p = Process.Start(psi);
            if (p is null) return new(-1, [], [], "wsl.exe could not be started");
            p.StandardInput.Close();
            var o = new MemoryStream();
            var e = new MemoryStream();
            var copyOut = p.StandardOutput.BaseStream.CopyToAsync(o);
            var copyErr = p.StandardError.BaseStream.CopyToAsync(e);
            if (!p.WaitForExit(timeout))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return new(-1, [], [], $"wsl.exe did not answer within {timeout.TotalSeconds:0} s");
            }
            p.WaitForExit();
            Task.WaitAll(copyOut, copyErr);
            return new(p.ExitCode, o.ToArray(), e.ToArray(), null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new(-1, [], [], $"wsl.exe could not be started ({ex.Message})");
        }
    }

    public string WindowsPath(string distribution, string linuxPath) => WslPaths.ToWindows(distribution, linuxPath);
}

/// <summary>How wsl.exe's own output is decoded.</summary>
public static class WslText
{
    /// <summary>
    /// <b>R-em3d26-1a — wsl.exe writes UTF-16LE when its output is redirected</b>, whatever the console
    /// code page. Read as UTF-8, <c>Ubuntu</c> arrives as <c>U\0b\0u\0n\0t\0u\0</c> and a parser looking
    /// for a name finds none, silently. So the listing is decoded as UTF-16LE explicitly
    /// (<see cref="DecodeUtf16"/>). <see cref="Decode"/> is for output that may be either — an error from
    /// wsl.exe, or a Linux program's UTF-8 relayed through it — and decides by the NUL bytes.
    /// </summary>
    public static string DecodeUtf16(byte[] bytes)
    {
        int skip = bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE ? 2 : 0;
        return Encoding.Unicode.GetString(bytes, skip, bytes.Length - skip).Replace("\0", "");
    }

    /// <summary>UTF-16LE when a quarter or more of the odd-position bytes are NUL (ASCII text in UTF-16LE),
    /// else UTF-8.</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length == 0) return "";
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return DecodeUtf16(bytes);
        int odd = 0, nul = 0;
        for (int i = 1; i < bytes.Length; i += 2) { odd++; if (bytes[i] == 0) nul++; }
        return odd > 0 && nul * 4 >= odd ? DecodeUtf16(bytes) : Encoding.UTF8.GetString(bytes);
    }
}
