using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using CircuitRF.Ui.Updates;
using Xunit;

namespace CircuitRF.Ui.Tests.Updates;

/// <summary>
/// R-AU-10 — asset selection by name, per application, platform and architecture.
///
/// <para>Gate item 6: every artifact the three packaging scripts produce maps to exactly ONE
/// application x platform x architecture, and wBond is never offered circuitRF's payload.</para>
/// </summary>
public class UpdateAssetNameTests
{
    private static readonly string[] Apps = ["circuitRF", "harmonicaRF", "wBond"];

    private static readonly (UpdatePlatform Platform, Architecture Arch)[] Targets =
    [
        (UpdatePlatform.MacOS,   Architecture.Arm64),
        (UpdatePlatform.MacOS,   Architecture.X64),
        (UpdatePlatform.Windows, Architecture.X64),
        (UpdatePlatform.Windows, Architecture.Arm64),
        (UpdatePlatform.Windows, Architecture.X86),
        (UpdatePlatform.Linux,   Architecture.X64),
        (UpdatePlatform.Linux,   Architecture.Arm64),
    ];

    /// <summary>Every name a release could carry for one version, across all three applications.</summary>
    private static List<string> AllAssetNames(string version) =>
        (from app in Apps
         from t in Targets
         select UpdateAssetNames.Expected(app, version, t.Platform, UpdateAssetNames.ArchToken(t.Platform, t.Arch)))
        .ToList();

    [Theory]
    [InlineData("circuitRF",   UpdatePlatform.MacOS,   "arm64", "circuitRF-1.0.0-beta.1-arm64.dmg")]
    [InlineData("circuitRF",   UpdatePlatform.MacOS,   "x64",   "circuitRF-1.0.0-beta.1-x64.dmg")]
    [InlineData("harmonicaRF", UpdatePlatform.Windows, "x64",   "harmonicaRF-1.0.0-beta.1-win-x64.zip")]
    [InlineData("harmonicaRF", UpdatePlatform.Windows, "x86",   "harmonicaRF-1.0.0-beta.1-win-x86.zip")]
    [InlineData("wBond",       UpdatePlatform.Linux,   "arm64", "wBond-1.0.0-beta.1-linux-arm64.tar.gz")]
    public void TheConventionIsSpelledExactlyThisWay(
        string app, UpdatePlatform platform, string arch, string expected)
        => Assert.Equal(expected, UpdateAssetNames.Expected(app, "1.0.0-beta.1", platform, arch));

    [Fact]
    public void EveryNameIsUnique_AcrossApplicationPlatformAndArchitecture()
    {
        List<string> names = AllAssetNames("1.0.0-beta.1");
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Equal(21, names.Count);   // 3 applications x 7 targets
    }

    /// <summary>
    /// The one that would matter most in the field: a release carries all 21 assets, and each
    /// application picks its own. A shared updater must never offer circuitRF's 160 MB payload to
    /// wBond, which would install the wrong application over the right one.
    /// </summary>
    [Fact]
    public void EachApplicationSelectsItsOwnPayload_FromAReleaseCarryingAllOfThem()
    {
        var release = new ReleaseInfo(
            "v1.0.0-beta.1", SemanticVersion.Parse("1.0.0-beta.1"), false, false,
            AllAssetNames("1.0.0-beta.1")
                .Select(n => new ReleaseAsset(n, "https://x/" + n, 1))
                .ToList());

        foreach (string app in Apps)
        {
            foreach ((UpdatePlatform platform, Architecture arch) in Targets)
            {
                ReleaseAsset? a = UpdateAssetNames.Select(release, app, platform, arch);
                Assert.NotNull(a);
                Assert.StartsWith(app + "-", a!.Name);

                foreach (string other in Apps.Where(x => x != app))
                    Assert.False(a.Name.StartsWith(other + "-", System.StringComparison.Ordinal));
            }
        }
    }

    /// <summary>
    /// An x64 build under Rosetta on Apple Silicon reports X64 and must be LEFT there. Silently
    /// migrating a user across architectures is not an update.
    /// </summary>
    [Fact]
    public void RosettaStaysOnX64()
        => Assert.Equal("x64", UpdateAssetNames.ArchToken(UpdatePlatform.MacOS, Architecture.X64));

    [Theory]
    [InlineData(UpdatePlatform.MacOS,  Architecture.X86)]
    [InlineData(UpdatePlatform.Linux,  Architecture.X86)]
    [InlineData(UpdatePlatform.MacOS,  Architecture.Arm)]
    public void NoArtifactIsPublishedForThese(UpdatePlatform platform, Architecture arch)
        => Assert.Throws<System.PlatformNotSupportedException>(
            () => UpdateAssetNames.ArchToken(platform, arch));

    [Fact]
    public void SelectReturnsNull_RatherThanThrowing_OnAnUnpublishedArchitecture()
    {
        var release = CannedReleases.Release("v1.0.0", false, false, "circuitRF-1.0.0-x64.dmg");
        Assert.Null(UpdateAssetNames.Select(release, "circuitRF", UpdatePlatform.MacOS, Architecture.X86));
    }

    /// <summary>
    /// The file names carry the VERSION file's spelling verbatim (packaging/version.sh's CRF_VERSION),
    /// which for a two-field VERSION is not what SemanticVersion normalises to. Matching on the
    /// normalised form alone would look right and find nothing.
    /// </summary>
    [Fact]
    public void ATwoFieldVersionTag_StillMatchesItsArtifact()
    {
        var release = CannedReleases.Release("v1.0", false, false, "circuitRF-1.0-arm64.dmg");
        Assert.Equal("1.0", release.VersionText);
        Assert.Equal("1.0.0", release.Version.ToString());

        Assert.Equal("circuitRF-1.0-arm64.dmg",
            UpdateAssetNames.Select(release, "circuitRF", UpdatePlatform.MacOS, Architecture.Arm64)?.Name);
    }

    [Fact]
    public void ANormalisedArtifactName_AlsoMatches_WhenTheTagIsShort()
    {
        var release = CannedReleases.Release("v1.0", false, false, "circuitRF-1.0.0-arm64.dmg");
        Assert.Equal("circuitRF-1.0.0-arm64.dmg",
            UpdateAssetNames.Select(release, "circuitRF", UpdatePlatform.MacOS, Architecture.Arm64)?.Name);
    }

    [Fact]
    public void TheRunningApplicationNamesItself_FromTheAssemblyProduct()
    {
        // Three applications share one assembly, so the name cannot come from the assembly NAME.
        Assert.Contains(UpdateApp.Name, Apps);
    }
}
