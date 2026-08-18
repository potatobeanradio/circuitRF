using System.Reflection;

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
