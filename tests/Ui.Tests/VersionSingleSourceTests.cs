using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The version number is written in exactly ONE place — the repo-root <c>VERSION</c> file — and
/// everything else reads it.
///
/// <para><b>This is a regression suite, not a style rule.</b> Before it, circuitRF gave three
/// different answers to "what version is this?": the About box said 0.9.0 (Beta), the three macOS
/// plists said 0.1.0, and the assembly said 1.0.0 (the .NET default, because no project set one).
/// Nothing was wrong with any single file; the versions simply drifted apart, which is what a
/// duplicated constant does. These tests fail when a literal version string comes back.</para>
/// </summary>
public class VersionSingleSourceTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;

        Assert.True(dir is not null, "Could not locate the repository root from the test output directory.");
        return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
    }

    private static string ReadRepoFile(params string[] parts) => File.ReadAllText(RepoFile(parts));

    /// <summary>A file with its comments removed — a note ABOUT a rule must not satisfy the rule.</summary>
    private static string WithoutComments(string text)
    {
        text = Regex.Replace(text, @"<!--.*?-->", "", RegexOptions.Singleline);
        text = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return string.Join("\n", text.Split('\n').Select(line =>
        {
            int slash = line.IndexOf("//", StringComparison.Ordinal);
            int hash = line.IndexOf('#');
            int cut = slash < 0 ? hash : (hash < 0 ? slash : Math.Min(slash, hash));
            return cut < 0 ? line : line[..cut];
        }));
    }

    private static string VersionFile => ReadRepoFile("VERSION").Trim();

    /// <summary>
    /// The end-to-end check: what the app reports came out of the VERSION file, through
    /// Directory.Build.props, into the assembly attribute the About box reads.
    /// </summary>
    [Fact]
    public void TheAppReportsTheVersionFilesVersion()
    {
        Assert.Equal(VersionFile, CircuitRF.Ui.AppVersion.Display);
    }

    /// <summary>
    /// <b>Every download link in <c>README.md</c> must name the version in the <c>VERSION</c> file.</b>
    ///
    /// <para>The README's download table hard-codes the release tag and the asset file name in each
    /// URL, because a GitHub release asset URL contains both and there is no "latest" spelling that
    /// avoids it — the file name itself carries the version. So the table goes stale on every
    /// release, and it goes stale <em>silently</em>: the links keep working, because the previous
    /// release still exists, and they quietly hand every new visitor the old build.</para>
    ///
    /// <para>That is the same shape as the packaging and version bugs this file already guards —
    /// nothing errors, nothing is missing, and the wrong thing ships. It was caught for
    /// 1.0.0-beta.2 only because the owner thought to ask, which is not a mechanism.</para>
    ///
    /// <para>Checked against <c>VERSION</c> rather than against a literal, so bumping the one file
    /// is still the only edit a release needs — and this test then names the README as the next
    /// thing to update rather than letting it drift.</para>
    /// </summary>
    [Fact]
    public void EveryReadmeDownloadLinkNamesTheVersionFilesVersion()
    {
        string readme  = ReadRepoFile("README.md");
        string version = VersionFile;

        var links = Regex.Matches(readme, @"https://github\.com/[^)\s]+/releases/download/([^/]+)/([^)\s]+)")
                         .Select(m => (Tag: m.Groups[1].Value, Asset: m.Groups[2].Value))
                         .ToList();

        Assert.True(links.Count > 0,
                    "README.md has no release download links at all. If the download table moved, "
                    + "point this test at wherever it went rather than deleting it.");

        var stale = links.Where(l => l.Tag != version || !l.Asset.Contains(version, StringComparison.Ordinal))
                         .Select(l => $"{l.Tag}/{l.Asset}")
                         .ToList();

        Assert.True(stale.Count == 0,
            $"README.md still links to a release other than VERSION's {version}. These links WORK — "
            + "they serve the previous release — so nothing will fail except the user, who gets the "
            + $"wrong build:\n  {string.Join("\n  ", stale)}");
    }

    /// <summary>
    /// <b>The Linux unpack example must name the same version too.</b> It is a copy-paste recipe —
    /// <c>tar xzf …</c> followed by <c>./circuitRF-&lt;version&gt;/install.sh</c> — so a stale
    /// version there does not serve an old build, it simply fails with "No such file or directory"
    /// for anyone who pastes it.
    /// </summary>
    [Fact]
    public void TheReadmeLinuxUnpackExampleNamesTheVersionFilesVersion()
    {
        string readme  = ReadRepoFile("README.md");
        string version = VersionFile;

        var named = Regex.Matches(readme, @"circuitRF-(\d+\.\d+\.\d+[^/\s`)\]]*)(?:/install\.sh|-linux-)")
                         .Select(m => m.Groups[1].Value.TrimEnd('-'))
                         .Distinct()
                         .ToList();

        Assert.True(named.Count > 0, "README.md no longer shows a Linux unpack example to check.");

        foreach (string v in named)
            Assert.True(v.StartsWith(version, StringComparison.Ordinal),
                        $"README.md's Linux example names circuitRF-{v}, but VERSION says {version}. "
                        + "Pasted as-is it fails with 'No such file or directory'.");
    }

    [Fact]
    public void TheVersionFileIsOneLineAndParseable()
    {
        string raw = ReadRepoFile("VERSION");

        Assert.Single(raw.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Matches(@"^\d+(\.\d+)*(-[0-9A-Za-z.\-]+)?$", raw.Trim());
    }

    /// <summary>
    /// Every project's assembly identity is derived from that file, and the numeric-only fields have
    /// the prerelease suffix stripped — AssemblyVersion and FileVersion are Win32 VERSIONINFO fields
    /// and a build fails outright on "0.9.0-beta.1".
    /// </summary>
    [Fact]
    public void DirectoryBuildPropsDerivesEveryVersionPropertyFromTheFile()
    {
        string props = WithoutComments(ReadRepoFile("Directory.Build.props"));

        Assert.Contains("ReadAllText", props);
        Assert.Contains("VERSION", props);

        foreach (string property in new[] { "Version", "InformationalVersion", "AssemblyVersion", "FileVersion" })
            Assert.Contains($"<{property}>$(", props);

        Assert.Contains("<AssemblyVersion>$(CircuitRfVersionCore)", props);
        Assert.Contains("<FileVersion>$(CircuitRfVersionCore)", props);
    }

    /// <summary>
    /// The About box is where the stale "0.9.0 (Beta)" actually lived. It must render
    /// <see cref="CircuitRF.Ui.AppVersion"/>, not a literal.
    /// </summary>
    [Fact]
    public void TheAboutDialogRendersAppVersionRatherThanALiteral()
    {
        string xaml = WithoutComments(ReadRepoFile("src", "Ui", "Views", "Dialogs", "AboutWindow.axaml"));
        string code = WithoutComments(ReadRepoFile("src", "Ui", "Views", "Dialogs", "AboutWindow.axaml.cs"));

        Assert.DoesNotMatch(@"\d+\.\d+\.\d+", xaml);
        Assert.Contains("AppVersion.Display", code);
    }

    /// <summary>
    /// The macOS bundles: the plists in Assets/ are placeholders, and each bundle script reads the
    /// shared version and stamps both keys into the COPY it puts inside the .app. A script that
    /// carried its own VERSION= literal is how the plists came to disagree with the app in the first
    /// place.
    /// </summary>
    [Theory]
    [InlineData("bundleForMacOS.sh")]
    [InlineData("bundleForHarmonicaMacOS.sh")]
    [InlineData("bundleForWBondMacOS.sh")]
    public void EveryBundleScriptStampsTheSharedVersionIntoItsPlist(string script)
    {
        string text = WithoutComments(ReadRepoFile("src", "Ui", script));

        Assert.Contains("packaging/version.sh", text);
        Assert.Contains("Set :CFBundleShortVersionString ${CRF_VERSION}", text);

        // CFBundleVersion must be purely numeric, so it gets the numeric head — not the full string.
        Assert.Contains("Set :CFBundleVersion ${CRF_VERSION_CORE}", text);

        Assert.DoesNotMatch(@"VERSION=""\d", text);
    }

    /// <summary>
    /// The Windows and Linux installers name themselves from the same file. A literal default here
    /// would silently ship an installer whose file name disagrees with the app inside it.
    /// </summary>
    [Fact]
    public void ThePackagingScriptsTakeTheirVersionFromTheSharedHelper()
    {
        string deb = WithoutComments(ReadRepoFile("packaging", "linux", "build-linux.sh"));
        Assert.Contains("packaging/version.sh", deb);
        Assert.Contains("$CRF_DEB_VERSION", deb);        // dpkg's ~ spelling, not the raw string
        Assert.DoesNotMatch(@"VERSION=""?\d", deb);

        string msi = WithoutComments(ReadRepoFile("packaging", "windows", "build-windows.ps1"));
        Assert.Contains("version.ps1", msi);
        Assert.Contains("$CrfMsiVersion", msi);
        Assert.DoesNotMatch(@"\$Version\s*=\s*'\d", msi);

        string dmg = WithoutComments(ReadRepoFile("packaging", "macos", "build-macos.sh"));
        Assert.Contains("packaging/version.sh", dmg);
        Assert.Contains("$CRF_VERSION", dmg);
    }
}
