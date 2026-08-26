using System;
using System.IO;
using System.Runtime.InteropServices;

namespace CircuitRF.Ui.Updates;

/// <summary>Which of the three packaging pipelines produced an artifact.</summary>
public enum UpdatePlatform { MacOS, Windows, Linux }

/// <summary>
/// The release-asset naming convention — produced by <c>build-macos.sh</c>, <c>build-windows.ps1</c> and
/// <c>build-linux.sh</c>, parsed here.
///
/// <code>
///   circuitRF-&lt;version&gt;-&lt;arch&gt;.dmg            arch in { arm64, x64 }
///   circuitRF-&lt;version&gt;-win-&lt;arch&gt;.zip        arch in { x64, arm64, x86 }
///   circuitRF-&lt;version&gt;-linux-&lt;arch&gt;.tar.gz   arch in { x64, arm64 }
/// </code>
///
/// <para>and the same with <c>harmonicaRF-</c> and <c>wBond-</c>.</para>
///
/// <para><b>Why this is a class and not three string comparisons at the call site.</b> Rename an
/// artifact in a packaging script and updates stop — with no error anywhere, no log line and no user
/// report, because a user who is not being offered an update has nothing to notice. So the
/// convention is written once, here, and <c>tests/Ui.Tests/PackagingScriptTests.cs</c> asserts the
/// scripts still construct exactly these names. Same class of guard as the pure-ASCII <c>.ps1</c>
/// rule that lives beside it, and it exists for the same reason: the failure is silent.</para>
/// </summary>
public static class UpdateAssetNames
{
    /// <summary>
    /// The one asset this application, on this platform and architecture, would accept from a
    /// release of <paramref name="version"/>.
    /// </summary>
    public static string Expected(string app, SemanticVersion version, UpdatePlatform platform, Architecture arch)
        => Expected(app, version.ToString(), platform, ArchToken(platform, arch));

    /// <summary>The name-building half, kept string-typed so a test can drive it from a script's literals.</summary>
    public static string Expected(string app, string version, UpdatePlatform platform, string archToken) => platform switch
    {
        UpdatePlatform.MacOS   => $"{app}-{version}-{archToken}.dmg",
        UpdatePlatform.Windows => $"{app}-{version}-win-{archToken}.zip",
        UpdatePlatform.Linux   => $"{app}-{version}-linux-{archToken}.tar.gz",
        _ => throw new ArgumentOutOfRangeException(nameof(platform)),
    };

    /// <summary>
    /// How <see cref="Architecture"/> is spelled in an artifact name.
    ///
    /// <para><b>An x64 build running under Rosetta on Apple Silicon stays on x64.</b>
    /// <c>ProcessArchitecture</c> correctly reports <see cref="Architecture.X64"/> there and must be
    /// left reporting it: silently moving a user to the arm64 payload is not an update, it is a
    /// different application, and it would do it without asking.</para>
    /// </summary>
    public static string ArchToken(UpdatePlatform platform, Architecture arch) => (platform, arch) switch
    {
        (_, Architecture.Arm64) => "arm64",
        (_, Architecture.X64)   => "x64",
        // x86 exists only as a Windows .msi target; there is no 32-bit macOS or Linux artifact.
        (UpdatePlatform.Windows, Architecture.X86) => "x86",
        _ => throw new PlatformNotSupportedException($"No release asset is published for {arch} on {platform}."),
    };

    /// <summary>
    /// True when <paramref name="name"/> is safe to use as a file name inside <c>staging/</c>.
    ///
    /// <para><b>Why an asset name needs a check at all.</b> It is written by whoever published the
    /// release, and <see cref="UpdateDownloader"/> combines it with the staging directory to get a
    /// path — so <c>../../something</c> writes outside the one directory the updater is allowed to
    /// own. GitHub will not produce such a name, but <see cref="UpdateManifest"/> lets a release
    /// supply its own, and "the host would never do that" is the assumption this whole subsystem
    /// declines to make about the host everywhere else (design §9).</para>
    ///
    /// <para>Deliberately the same rule the Windows launcher stub applies to <c>current</c>: no path
    /// separator, no drive letter, no <c>..</c>. One rule, stated twice, in the two places a
    /// caller-supplied name becomes a path.</para>
    /// </summary>
    public static bool IsSafeAssetFileName(string? name)
        => !string.IsNullOrWhiteSpace(name)
           && name.Length <= 255
           && name.IndexOfAny(['/', '\\', ':']) < 0
           && name != "."
           && name != ".."
           && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    /// <summary>The platform this process is running on, or null on one we publish no artifact for.</summary>
    public static UpdatePlatform? CurrentPlatform()
    {
        if (OperatingSystem.IsMacOS())   return UpdatePlatform.MacOS;
        if (OperatingSystem.IsWindows()) return UpdatePlatform.Windows;
        if (OperatingSystem.IsLinux())   return UpdatePlatform.Linux;
        return null;
    }

    /// <summary>
    /// Selects the asset for <paramref name="app"/> on this platform/architecture out of a release,
    /// by exact name. Null when the release publishes nothing for it — which is a normal outcome, not
    /// an error: an arm64 Linux user simply sees nothing in a release that shipped only x64.
    /// </summary>
    public static ReleaseAsset? Select(
        ReleaseInfo release, string app, UpdatePlatform platform, Architecture arch)
    {
        string archToken;
        try { archToken = ArchToken(platform, arch); }
        catch (PlatformNotSupportedException) { return null; }

        // The tag's own spelling first, because that is what the packaging scripts interpolated;
        // the normalised form second, so a `1.0` tag still finds a `1.0.0`-named artifact. Matching
        // is by EXACT name either way, so at most one asset can answer and the mapping stays
        // one-to-one.
        string primary  = Expected(app, release.VersionText, platform, archToken);
        string fallback = Expected(app, release.Version.ToString(), platform, archToken);

        foreach (ReleaseAsset a in release.Assets)
            if (string.Equals(a.Name, primary, StringComparison.Ordinal))
                return a;

        if (!string.Equals(primary, fallback, StringComparison.Ordinal))
            foreach (ReleaseAsset a in release.Assets)
                if (string.Equals(a.Name, fallback, StringComparison.Ordinal))
                    return a;

        return null;
    }
}
