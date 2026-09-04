using System.Reflection;
using System.Runtime.InteropServices;

namespace CircuitRF.Ui;

/// <summary>
/// The version circuitRF reports to the user — read from the assembly, never written down here.
///
/// <para>The single source is the repo-root <c>VERSION</c> file: <c>Directory.Build.props</c> reads
/// it into <c>InformationalVersion</c> at build time, the packaging scripts read the same file for
/// the <c>.msi</c>/<c>.dmg</c>/<c>.deb</c> names and the macOS bundle's <c>Info.plist</c>. Hard-coding
/// a version string anywhere in the UI is what put "0.9.0 (Beta)" in the About box while the plists
/// said 0.1.0 and the assembly said 1.0.0 — three answers to one question.</para>
/// </summary>
public static class AppVersion
{
    /// <summary>e.g. <c>0.9.0-beta.1</c>. Never empty; falls back to the numeric assembly version.</summary>
    public static string Display { get; } = Read();

    /// <summary>
    /// Which build of circuitRF this is — e.g. <c>Windows arm64</c>, <c>macOS x64 build on arm64</c>.
    ///
    /// <para><b>The PROCESS architecture, not the machine's, and the difference is the whole point.</b>
    /// Windows on arm64 runs an x64 build under translation and macOS on Apple Silicon runs one under
    /// Rosetta, both without saying so anywhere a user can see — so "which one did I install?" is a
    /// question the application is the only thing able to answer. When the two differ, both are named:
    /// the build first, because that is what was downloaded and what a bug report is about, and the
    /// machine after it, because that is what makes the pairing worth reading.</para>
    ///
    /// <para>The spelling matches the release artifacts' own (<c>UpdateAssetNames.ArchToken</c>), so
    /// what the About box says and what the user would go and download read the same.</para>
    /// </summary>
    public static string Platform { get; } = ReadPlatform();

    private static string ReadPlatform()
    {
        string os = OperatingSystem.IsWindows() ? "Windows"
                  : OperatingSystem.IsMacOS()   ? "macOS"
                  : OperatingSystem.IsLinux()   ? "Linux"
                  : RuntimeInformation.OSDescription;

        string build   = Arch(RuntimeInformation.ProcessArchitecture);
        string machine = Arch(RuntimeInformation.OSArchitecture);

        return build == machine ? $"{os} {build}" : $"{os} {build} build on {machine}";
    }

    private static string Arch(Architecture arch) => arch switch
    {
        Architecture.X64   => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86   => "x86",
        Architecture.Arm   => "arm32",
        _                  => arch.ToString().ToLowerInvariant(),
    };

    private static string Read()
    {
        Assembly asm = typeof(AppVersion).Assembly;

        string? v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?? asm.GetName().Version?.ToString();

        if (string.IsNullOrWhiteSpace(v)) return "unknown";

        // The SDK can append "+<commit sha>"; Directory.Build.props turns that off, and this makes
        // the About box independent of whether it stays off.
        int plus = v.IndexOf('+');
        return plus >= 0 ? v[..plus] : v;
    }
}
