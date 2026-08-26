using System;
using System.Collections.Generic;
using System.IO;
using CircuitRF.Ui;
using CircuitRF.Ui.Updates;
using Xunit;

namespace CircuitRF.Ui.Tests.Updates;

/// <summary>
/// <c>state.json</c> is ordinary JSON in the user's application-data directory, so its contents are
/// whatever is in the file rather than whatever the updater last wrote. Two of its fields reached
/// operations that destroy or replace a directory, and neither was checked (security review,
/// 2026-08-25):
///
/// <list type="bullet">
/// <item><c>staged_path</c> reached <c>Directory.Delete(recursive: true)</c> whenever the user
///   unchecked "Automatic updates" — an arbitrary recursive delete driven by a settings toggle.</item>
/// <item>On macOS the same field reached the directory EXCHANGE that lands in the installed
///   <c>.app</c>, which on a shared Mac is what every account on the machine launches.</item>
/// </list>
///
/// <para>Design §13 rule 6 already said the updater never touches anything outside its own
/// directories. These tests are that rule asserted rather than intended.</para>
/// </summary>
public sealed class UpdateStatePathGuardTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _installRoot;

    public UpdateStatePathGuardTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "crf-state-guard-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tmp);

        _installRoot = Path.Combine(_tmp, "install");
        Directory.CreateDirectory(_installRoot);

        AppDataRoot.RedirectTo(Path.Combine(_tmp, "appdata"));
        Directory.CreateDirectory(UpdatePaths.Root);
    }

    public void Dispose()
    {
        AppDataRoot.RedirectTo(null);
        try { Directory.Delete(_tmp, true); } catch { }
    }

    // ── what Discard will and will not delete ───────────────────────────────────────────────

    [Fact]
    public void Discard_RefusesAPathThatIsNotTheUpdatersOwn()
    {
        string victim = Path.Combine(_tmp, "Documents");
        Directory.CreateDirectory(victim);
        File.WriteAllText(Path.Combine(victim, "thesis.cws"), "the user's afternoon");

        UpdateStager.Discard(victim, _installRoot);

        Assert.True(Directory.Exists(victim));
    }

    /// <summary>The exact shape the settings toggle used to hand it: an absolute path out of state.json.</summary>
    [Theory]
    [InlineData("..")]
    [InlineData("../..")]
    [InlineData("../elsewhere")]
    public void Discard_RefusesAnInstallRootSiblingReachedByTraversal(string relative)
    {
        string victim = Path.GetFullPath(Path.Combine(_installRoot, relative));
        Directory.CreateDirectory(victim);

        UpdateStager.Discard(Path.Combine(_installRoot, relative), _installRoot);

        Assert.True(Directory.Exists(victim));
    }

    [Fact]
    public void Discard_StillRemovesAStagedVersionDirectory()
    {
        string staged = Path.Combine(_installRoot, "app-1.0.1");
        Directory.CreateDirectory(staged);

        UpdateStager.Discard(staged, _installRoot);

        Assert.False(Directory.Exists(staged));
    }

    [Fact]
    public void Discard_StillRemovesAStagedMacBundle()
    {
        string staged = Path.Combine(UpdatePaths.Staged, "1.0.1", "circuitRF.app");
        Directory.CreateDirectory(staged);

        UpdateStager.Discard(staged, _installRoot);

        Assert.False(Directory.Exists(staged));
    }

    /// <summary>
    /// With no install root in hand, only the updater's own tree is deletable — a caller that cannot
    /// say where the installation is has not earned the right to delete inside it.
    /// </summary>
    [Fact]
    public void Discard_WithNoInstallRoot_WillNotTouchAnAppDirectory()
    {
        string staged = Path.Combine(_installRoot, "app-1.0.1");
        Directory.CreateDirectory(staged);

        UpdateStager.Discard(staged, installRoot: null);

        Assert.True(Directory.Exists(staged));
    }

    // ── what the macOS swap will accept as "staged" ─────────────────────────────────────────

    /// <summary>
    /// The bundle swap replaces the installed application. A <c>staged_path</c> pointing anywhere
    /// else is refused before <c>SwapDirectories</c> is reached, so the field cannot nominate a
    /// directory to be promoted into <c>/Applications</c>.
    /// </summary>
    [Fact]
    public void TheBundleSwap_RefusesAStagedPathOutsideTheUpdatersOwnStagingTree()
    {
        string appRoot = Path.Combine(_tmp, "Applications", "circuitRF.app");
        Directory.CreateDirectory(Path.Combine(appRoot, "Contents"));

        string attacker = Path.Combine(_tmp, "elsewhere", "circuitRF.app");
        Directory.CreateDirectory(Path.Combine(attacker, "Contents"));

        var site  = new InstallSite(appRoot, InstallShape.MacOsBundle, true, appRoot);
        var state = new UpdateState { StagedVersion = "1.0.1", StagedPath = attacker };

        SwapResult result = UpdateSwap.ApplyAtLaunch(site, state, UpdatePaths.Root, "irrelevant");

        Assert.Equal(SwapOutcome.Failed, result.Outcome);
        Assert.True(Directory.Exists(Path.Combine(attacker, "Contents")));
        Assert.True(Directory.Exists(Path.Combine(appRoot, "Contents")));
    }

    // ── the predicate itself ────────────────────────────────────────────────────────────────

    [Fact]
    public void IsOurs_IsExactlyTheTwoPlacesDesign13Rule6Names()
    {
        Assert.True(UpdatePaths.IsOurs(Path.Combine(UpdatePaths.Staging, "x.dmg"), _installRoot));
        Assert.True(UpdatePaths.IsOurs(Path.Combine(_installRoot, "app-1.0.1"), _installRoot));
        Assert.True(UpdatePaths.IsOurs(Path.Combine(_installRoot, "app-1.0.1.partial"), _installRoot));

        Assert.False(UpdatePaths.IsOurs(UpdatePaths.Root, _installRoot));            // the root itself is not "inside" it
        Assert.False(UpdatePaths.IsOurs(_installRoot, _installRoot));
        Assert.False(UpdatePaths.IsOurs(Path.Combine(_installRoot, "kits"), _installRoot));
        Assert.False(UpdatePaths.IsOurs(Path.Combine(_installRoot, "app-1.0.1", "sub"), _installRoot));
        Assert.False(UpdatePaths.IsOurs(_tmp, _installRoot));
        Assert.False(UpdatePaths.IsOurs(null, _installRoot));
        Assert.False(UpdatePaths.IsOurs("", _installRoot));
    }

    // ── a release tag's own spelling is a path segment ──────────────────────────────────────

    /// <summary>
    /// <c>SemanticVersion.TryParse</c> <c>Trim()</c>s before it validates, so a tag written with
    /// surrounding whitespace parses — while <c>VersionText</c>, which is the tag's own spelling and
    /// has to be, keeps it. That string is then joined to the install root.
    /// </summary>
    [Theory]
    [InlineData(" 1.0.1")]
    [InlineData("1.0.1 ")]
    [InlineData("1.0.1\t")]
    [InlineData("\n1.0.1")]
    public void ATagWithWhitespace_ParsesButIsNotOfferedAsAnUpdate(string tag)
    {
        Assert.True(SemanticVersion.TryParse(tag, out SemanticVersion? v));

        var release = new ReleaseInfo(tag, v!, IsPreRelease: false, IsDraft: false, Assets: []);
        Assert.False(release.HasUsableVersionText);

        Assert.Null(UpdateSelector.SelectRelease([release], SemanticVersion.Parse("1.0.0"),
                                                 includeBetas: true));
    }

    [Fact]
    public void TheTagsWeActuallyPublish_AreStillOffered()
    {
        var v = SemanticVersion.Parse("1.0.1");
        var release = new ReleaseInfo("v1.0.1", v, IsPreRelease: false, IsDraft: false, Assets: []);

        Assert.True(release.HasUsableVersionText);
        Assert.NotNull(UpdateSelector.SelectRelease([release], SemanticVersion.Parse("1.0.0"),
                                                    includeBetas: false));
    }
}
