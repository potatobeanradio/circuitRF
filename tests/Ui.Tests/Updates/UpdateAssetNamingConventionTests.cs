using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using CircuitRF.Ui.Updates;
using Xunit;

namespace CircuitRF.Ui.Tests.Updates;

/// <summary>
/// R-AU-21 — <b>the test that prevents silent, permanent failure.</b>
///
/// <para>The three packaging scripts CONSTRUCT the release-asset names that
/// <see cref="UpdateAssetNames"/> PARSES. Rename an artifact in a script and updates stop: no error
/// anywhere, no log line, and no user report, because a user who is not being offered an update has
/// nothing to notice. It is the same class of guard as the pure-ASCII <c>.ps1</c> rule and the
/// single-source <c>VERSION</c> test that live beside it, and it exists for the same reason.</para>
///
/// <para>The assertion is deliberately made against the scripts' own literal text rather than
/// against a second copy of the convention written here — a second copy would agree with itself and
/// prove nothing.</para>
/// </summary>
public class UpdateAssetNamingConventionTests
{
    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;

        Assert.True(dir is not null, "Could not locate the repository root from the test output directory.");
        return dir!;
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine([RepoRoot().FullName, .. parts]));

    /// <summary>
    /// Turns a shell or PowerShell name expression into the concrete name it would produce, by
    /// substituting the version and architecture variables the scripts interpolate.
    /// </summary>
    private static string Concretise(string expression, string app, string version, string arch)
        => expression
            .Replace("${NAME}", app)
            .Replace("${CRF_VERSION}", version).Replace("$CrfVersion", version).Replace("${VERSION}", version)
            .Replace("${ARCH}", arch).Replace("$Arch", arch);

    private const string Version = "1.0.0-beta.1";

    // ── macOS: the .dmg, which is ALSO the update payload (design §7 — no new mac asset) ────────

    [Theory]
    [InlineData("circuitRF",   "arm64")]
    [InlineData("circuitRF",   "x64")]
    [InlineData("harmonicaRF", "arm64")]
    [InlineData("wBond",       "x64")]
    public void TheDmgNameTheMacScriptBuilds_IsWhatTheUpdaterMatches(string app, string arch)
    {
        string script = Read("packaging", "macos", "build-macos.sh");

        // The one expression the script interpolates:  ${NAME}-${VERSION}-${ARCH}.dmg, where NAME is
        // the application (all three are built from this script) and VERSION is CRF_VERSION.
        System.Text.RegularExpressions.Match m =
            Regex.Match(script, @"\$\{NAME\}-\$\{VERSION\}-\$\{ARCH\}\.dmg");
        Assert.True(m.Success, "build-macos.sh no longer constructs a name of the documented shape.");
        Assert.Contains("VERSION=\"$CRF_VERSION\"", script);   // and VERSION really is the one source

        Assert.Equal(
            UpdateAssetNames.Expected(app, Version, UpdatePlatform.MacOS, arch),
            Concretise(m.Value, app, Version, arch));
    }

    // ── Windows: the .zip the per-user channel emits ────────────────────────────────────────────

    [Theory]
    [InlineData("x64")]
    [InlineData("arm64")]
    [InlineData("x86")]
    public void TheZipNameTheWindowsScriptBuilds_IsWhatTheUpdaterMatches(string arch)
    {
        string script = Read("packaging", "windows", "build-windows.ps1");

        System.Text.RegularExpressions.Match m = Regex.Match(script, @"circuitRF-\$CrfVersion-win-\$Arch\.zip");
        Assert.True(m.Success,
            "build-windows.ps1 no longer constructs the update payload name the updater matches.");

        Assert.Equal(
            UpdateAssetNames.Expected("circuitRF", Version, UpdatePlatform.Windows, arch),
            Concretise(m.Value, "circuitRF", Version, arch));
    }

    // ── Linux: the .tar.gz the user-local channel emits ─────────────────────────────────────────

    [Theory]
    [InlineData("x64")]
    [InlineData("arm64")]
    public void TheTarballNameTheLinuxScriptBuilds_IsWhatTheUpdaterMatches(string arch)
    {
        string script = Read("packaging", "linux", "build-linux.sh");

        System.Text.RegularExpressions.Match m = Regex.Match(script, @"circuitRF-\$\{CRF_VERSION\}-linux-\$\{ARCH\}\.tar\.gz");
        Assert.True(m.Success, "build-linux.sh no longer constructs the documented tarball name.");

        Assert.Equal(
            UpdateAssetNames.Expected("circuitRF", Version, UpdatePlatform.Linux, arch),
            Concretise(m.Value, "circuitRF", Version, arch));
    }

    // ── the shapes that must NOT be mistaken for an update payload ───────────────────────────────

    /// <summary>
    /// The <c>.msi</c> and the <c>.deb</c> survive unchanged and become notify-only. Their names must
    /// not collide with an update payload's, or a machine-wide install would be offered as one.
    /// </summary>
    [Fact]
    public void ThePerMachineInstallerNames_AreNotUpdatePayloadNames()
    {
        var payloads = new HashSet<string>(StringComparer.Ordinal);
        foreach (string app in new[] { "circuitRF", "harmonicaRF", "wBond" })
        {
            payloads.Add(UpdateAssetNames.Expected(app, Version, UpdatePlatform.MacOS,   "arm64"));
            payloads.Add(UpdateAssetNames.Expected(app, Version, UpdatePlatform.MacOS,   "x64"));
            payloads.Add(UpdateAssetNames.Expected(app, Version, UpdatePlatform.Windows, "x64"));
            payloads.Add(UpdateAssetNames.Expected(app, Version, UpdatePlatform.Linux,   "x64"));
        }

        foreach (string installer in new[]
                 {
                     $"circuitRF-{Version}-x64.msi",
                     $"circuitRF-{Version}-arm64.msi",
                     $"circuitRF-{Version}-x86.msi",
                     $"circuitRF-{Version}-win-x64-user.msi",
                     $"circuitRF-1.0.0~beta.1-x64.deb",       // dpkg's ~ spelling
                     $"circuitRF-{Version}-x64.deb",
                 })
        {
            Assert.DoesNotContain(installer, payloads);
        }
    }

    /// <summary>
    /// A release carrying every artifact the three pipelines produce offers each installed
    /// application exactly one file, and never an installer.
    /// </summary>
    [Fact]
    public void AReleaseOfEverything_OffersEachApplicationExactlyOneFile()
    {
        var assets = new List<ReleaseAsset>();

        void Add(string name) => assets.Add(new ReleaseAsset(name, "https://x/" + name, 1));

        foreach (string app in new[] { "circuitRF", "harmonicaRF", "wBond" })
        {
            Add($"{app}-{Version}-arm64.dmg");
            Add($"{app}-{Version}-x64.dmg");
            Add($"{app}-{Version}-win-x64.zip");
            Add($"{app}-{Version}-win-arm64.zip");
            Add($"{app}-{Version}-win-x86.zip");
            Add($"{app}-{Version}-linux-x64.tar.gz");
            Add($"{app}-{Version}-linux-arm64.tar.gz");

            // ...and the installers, which must never be selected.
            Add($"{app}-{Version}-x64.msi");
            Add($"{app}-{Version}-win-x64-user.msi");
            Add($"{app}-1.0.0~beta.1-x64.deb");
        }

        var release = new ReleaseInfo($"v{Version}", SemanticVersion.Parse(Version), true, false, assets);

        foreach (string app in new[] { "circuitRF", "harmonicaRF", "wBond" })
        {
            ReleaseAsset? mac = UpdateAssetNames.Select(release, app, UpdatePlatform.MacOS, Architecture.Arm64);
            ReleaseAsset? win = UpdateAssetNames.Select(release, app, UpdatePlatform.Windows, Architecture.X64);
            ReleaseAsset? lin = UpdateAssetNames.Select(release, app, UpdatePlatform.Linux, Architecture.X64);

            Assert.Equal($"{app}-{Version}-arm64.dmg", mac?.Name);
            Assert.Equal($"{app}-{Version}-win-x64.zip", win?.Name);
            Assert.Equal($"{app}-{Version}-linux-x64.tar.gz", lin?.Name);

            foreach (ReleaseAsset? picked in new[] { mac, win, lin })
            {
                Assert.NotNull(picked);
                Assert.False(picked!.Name.EndsWith(".msi", StringComparison.Ordinal));
                Assert.False(picked.Name.EndsWith(".deb", StringComparison.Ordinal));
            }
        }
    }

    /// <summary>
    /// The per-user Windows channel and the Linux tarball are the ONLY ones that can update
    /// themselves, so the scripts that produce them must exist at all. A phase that deleted one and
    /// left the updater matching its name would fail silently in exactly the way this file guards.
    /// </summary>
    [Fact]
    public void TheTwoUserLocalChannelsExist()
    {
        Assert.Contains("perUser", Read("packaging", "windows", "build-windows.ps1"));
        Assert.Contains("Scope=$Scope", Read("packaging", "windows", "build-windows.ps1"));
        Assert.True(File.Exists(Path.Combine(RepoRoot().FullName, "packaging", "linux", "build-linux.sh")));
        Assert.True(File.Exists(Path.Combine(RepoRoot().FullName, "packaging", "linux", "install.sh")));
        Assert.True(File.Exists(Path.Combine(RepoRoot().FullName, "packaging", "windows", "stub", "circuitrf-stub.c")));
    }

    /// <summary>
    /// The pure-ASCII rule covers every .ps1 under packaging/, so the two new ones are covered by the
    /// existing guard — this asserts they are actually reached by it rather than trusting the glob.
    /// </summary>
    [Fact]
    public void TheNewPowerShellScriptsArePureAscii()
    {
        foreach (string rel in new[]
                 {
                     Path.Combine("packaging", "windows", "build-windows.ps1"),
                     Path.Combine("packaging", "windows", "stub", "build-stub.ps1"),
                 })
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(RepoRoot().FullName, rel));
            int bad = bytes.Count(b => b > 0x7F);
            Assert.True(bad == 0, $"{rel} contains {bad} non-ASCII byte(s); see build-windows.ps1's own header.");
        }
    }

    /// <summary>
    /// The Linux installer re-points `current` by symlink-then-rename, never rm-then-ln. The naive
    /// form has a window in which the application has no launch path at all.
    /// </summary>
    [Fact]
    public void TheLinuxInstaller_RepointsCurrentAtomically()
    {
        string install = Read("packaging", "linux", "install.sh");

        Assert.Contains("current.tmp", install);
        Assert.Contains("mv -Tf", install);
        Assert.DoesNotContain("rm -f \"${ROOT}/current\"", install);
    }

    /// <summary>
    /// The launcher stub is what never changes, so the .desktop entry and the ~/.local/bin launcher
    /// must both point at the STABLE current/ path. Pointing either at a versioned directory would
    /// make every update re-register file associations, which is the thing the layout exists to avoid.
    /// </summary>
    [Fact]
    public void TheLinuxLaunchPathsPointAtCurrent_NotAtAVersion()
    {
        string install = Read("packaging", "linux", "install.sh");

        Assert.Contains("${ROOT}/current/circuitRF", install);
        Assert.DoesNotContain("Exec=${ROOT}/app-", install);
        Assert.DoesNotContain("${ROOT}/${VERSION_DIR}/circuitRF\"", install);
    }
}
