using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Guards on the packaging scripts — the parts that fail SILENTLY, on a platform CI does not
/// necessarily exercise, and that nothing else in the build can notice.
/// </summary>
public class PackagingScriptTests
{
    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;

        Assert.True(dir is not null, "Could not locate the repository root from the test output directory.");
        return dir!;
    }

    private static string RepoFile(params string[] parts) =>
        Path.Combine(new[] { RepoRoot().FullName }.Concat(parts).ToArray());

    /// <summary>
    /// <b>Every helper program a build produces must be listed for PUBLISH, not merely dropped into
    /// the output folder.</b>
    ///
    /// <para>The device worker, the OSDI worker and the macOS VM host are built by scripts that copy
    /// their products beside the assemblies — which is where circuitRF looks for them at run time, so
    /// <c>dotnet run</c> works and the arrangement looks complete. It is not: a file an
    /// <c>Exec</c> drops into the output folder is not an MSBuild item, and <c>dotnet publish</c>
    /// copies items. Every packaged build (.msi, .dmg and .deb all package the publish tree) therefore
    /// shipped without them, and the symptom appears an entire install later — a kit that evaluates
    /// under <c>dotnet run</c> and refuses on the installed copy, naming a program the user never
    /// installed and could not have.</para>
    ///
    /// <para>The names are read from the SCRIPT rather than written twice, so a product added there
    /// and forgotten here fails this test instead of going missing from the installer.</para>
    /// </summary>
    [Fact]
    public void EveryDeviceWorkerProduct_IsListedForPublish()
    {
        string script  = File.ReadAllText(RepoFile("tools", "senior-worker", "build.sh"));
        string project = File.ReadAllText(RepoFile("src", "Ui", "CircuitRF.Ui.csproj"));

        var products = Regex.Matches(script, @"\$out/([A-Za-z0-9._-]+)")
                            .Select(m => m.Groups[1].Value)
                            .Distinct()
                            .ToList();

        Assert.NotEmpty(products);
        Assert.Contains("ResolvedFileToPublish", project, StringComparison.Ordinal);

        foreach (string product in products)
            Assert.True(project.Contains($"$(OutDir){product}\"", StringComparison.Ordinal),
                        $"'{product}' is built beside the assemblies but never published, so no " +
                        $"installer contains it. Add it to CrfPublishHelperPrograms.");
    }

    /// <summary>
    /// <b>Every <c>.ps1</c> in <c>packaging/</c> must be pure ASCII.</b>
    ///
    /// <para>Windows PowerShell 5.1 — still the default <c>powershell.exe</c> on every Windows box —
    /// reads a <c>.ps1</c> with no byte-order mark as ANSI (cp1252), not UTF-8. A UTF-8 emoji or
    /// box-drawing character therefore arrives as its individual bytes, and bytes 0x93 / 0x94 are
    /// the CURLY QUOTES U+201C / U+201D, which PowerShell honours as string delimiters exactly like
    /// a straight <c>"</c>.</para>
    ///
    /// <para><b>The failure mode is what makes this worth a test.</b> Nothing errors. The parser
    /// takes the stray quote as opening a string, swallows everything up to the next quote-class
    /// byte — comments, commands, whole steps — PRINTS that block instead of executing it, and
    /// carries on. That is what happened: <c>Write-Host "📦 Publishing $rid..."</c> ended its string
    /// early on the 0x93 inside the emoji, the trailing <c>"</c> opened a new one, and the entire
    /// <c>dotnet publish</c> block was echoed rather than run. The first visible symptom was a
    /// <c>Get-ChildItem</c> "Cannot find path ...\publish\win-x64" from a LATER step, which names
    /// the wrong cause entirely.</para>
    ///
    /// <para>A BOM would also fix it, but a BOM is invisible and survives no round-trip through an
    /// editor or a copy-paste that anyone would notice. ASCII is checkable, which is why this is the
    /// rule.</para>
    /// </summary>
    [Fact]
    public void PowerShellScripts_ArePureAscii_BecausePs51ReadsThemAsCp1252()
    {
        var scripts = Directory.GetFiles(RepoFile("packaging"), "*.ps1", SearchOption.AllDirectories);
        Assert.NotEmpty(scripts);

        var offences = new List<string>();
        foreach (var path in scripts)
        {
            var bytes = File.ReadAllBytes(path);
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] < 0x80) continue;
                int line = bytes.Take(i).Count(b => b == (byte)'\n') + 1;
                offences.Add($"{Path.GetFileName(path)}:{line} byte 0x{bytes[i]:X2}");
                break;      // one report per file is enough to name it
            }
        }

        Assert.True(offences.Count == 0,
            "Non-ASCII bytes in a packaging .ps1. Windows PowerShell 5.1 reads these as cp1252, and "
            + "0x93/0x94 become curly quotes that silently swallow the rest of the script:\n  "
            + string.Join("\n  ", offences));
    }

    /// <summary>
    /// <b>IconGen must name a Linux native SkiaSharp itself.</b> SkiaSharp 4.148.0 - what Svg.Skia
    /// 5.2.1 resolves to - declares <c>SkiaSharp.NativeAssets.Win32</c> and <c>.macOS</c> as
    /// dependencies and no Linux equivalent, so on Linux nothing puts a <c>libSkiaSharp.so</c> in the
    /// output and the tool dies at its first <c>SKSvg()</c> with a <c>DllNotFoundException</c>. Every
    /// packaging script runs IconGen first, so this took out the whole of <c>build-deb.sh</c> at its
    /// first step (owner-reported, 2026-08-21, Linux arm64 - and an x64 Linux box fails identically).
    ///
    /// <para>This is invisible from Windows and macOS, where the transitive native assets are present
    /// and the tool works, which is exactly the class of failure this file exists for.</para>
    ///
    /// <para><b>.NoDependencies specifically.</b> The plain <c>SkiaSharp.NativeAssets.Linux</c> ships a
    /// <c>.so</c> linked against <c>libfontconfig.so.1</c>, and on a machine without it the tool fails
    /// one layer later with the same exception type. The artwork has no text in it, so no font is
    /// needed; measured in a bare <c>dotnet/sdk:10.0</c> container, .NoDependencies renders all three
    /// icon sets with no system packages installed at all.</para>
    /// </summary>
    [Fact]
    public void IconGenNamesALinuxNativeSkiaSharp()
    {
        var project = File.ReadAllText(RepoFile("tools", "IconGen", "IconGen.csproj"));

        Assert.True(project.Contains("SkiaSharp.NativeAssets.Linux.NoDependencies", StringComparison.Ordinal),
            "tools/IconGen/IconGen.csproj no longer references SkiaSharp.NativeAssets.Linux.NoDependencies - "
            + "IconGen, and therefore every packaging script's first step, cannot run on Linux without it.");

        // Host-conditioned: the package is ~192 MB unpacked across 13 Linux RIDs, and this tool always
        // rasterises for the machine it runs on. If the condition is ever dropped the reference still
        // works - so this asserts the intent, not the mechanism.
        Assert.True(project.Contains("IsOSPlatform('Linux')", StringComparison.Ordinal),
            "the Linux native SkiaSharp reference is no longer conditioned on the host being Linux.");
    }

    /// <summary>
    /// The published executable is named after the APPLICATION (circuitRF / harmonicaRF / wBond),
    /// not after the assembly (CircuitRF.Ui). The assembly name cannot change — RfCore grants it
    /// <c>InternalsVisibleTo</c> — so <c>CircuitRF.Ui.csproj</c>'s <c>CrfRenameApphost</c> target
    /// renames the native host after publish, and four packaging consumers must agree with it.
    ///
    /// <para>They agree by literal string, in files no compiler reads. This test is the only thing
    /// that notices when one of them drifts; the symptom otherwise is an installer that builds
    /// cleanly and ships a shortcut to a file that is not there.</para>
    /// </summary>
    [Theory]
    [InlineData("src/Ui/CircuitRF.Ui.csproj", "<CrfExeName Condition=\"'$(CrfApp)' == 'circuitrf'\">circuitRF</CrfExeName>")]
    [InlineData("packaging/windows/circuitRF.wxs", "Source=\"$(var.PublishDir)\\circuitRF.exe\"")]
    [InlineData("packaging/windows/circuitRF.wxs", "Target=\"[INSTALLFOLDER]circuitRF.exe\"")]
    [InlineData("packaging/windows/build-msi.ps1", "$exeName = 'circuitRF.exe'")]
    [InlineData("packaging/linux/postinst", "ln -sf /opt/circuitrf/circuitRF /usr/bin/circuitrf")]
    [InlineData("packaging/linux/circuitrf.desktop", "Exec=/opt/circuitrf/circuitRF %F")]
    [InlineData("src/Ui/bundleForMacOS.sh", "EXECUTABLE_NAME=\"circuitRF\"")]
    [InlineData("src/Ui/bundleForHarmonicaMacOS.sh", "EXECUTABLE_NAME=\"harmonicaRF\"")]
    [InlineData("src/Ui/bundleForWBondMacOS.sh", "EXECUTABLE_NAME=\"wBond\"")]
    public void PackagingRefersToTheRenamedHost(string relativePath, string expected)
    {
        var text = File.ReadAllText(RepoFile(relativePath.Split('/')));
        Assert.True(text.Contains(expected, StringComparison.Ordinal),
            $"{relativePath} no longer contains \"{expected}\" — the published executable name has drifted "
            + "from CircuitRF.Ui.csproj's CrfRenameApphost target.");
    }

    /// <summary>
    /// <b>No <c>ProgId</c> in the .wxs may declare the same <c>Verb</c> twice.</b> A Verb is
    /// registered against the PROGID, not against the extension it is nested in: WiX writes
    /// <c>HKCR\&lt;ProgId&gt;\shell\open</c> and <c>HKCR\&lt;ProgId&gt;\shell\open\command</c>, and
    /// neither key mentions the extension. So a ProgId claiming two extensions — the workspace
    /// claims both <c>.crfw</c> and <c>.cws</c> — must carry the verb ONCE. Repeating it under the
    /// second extension emits two byte-identical registry rows, and <c>wix build</c> stops with
    /// WIX0091 on the duplicate generated identifier. That is exactly how the 1.0.0-beta.1
    /// installer failed to build (2026-08-23).
    ///
    /// <para>Reproduced and fixed against the real toolset, not reasoned about: with the verb
    /// duplicated the linker reports two WIX0091s; with it removed the file links clean, and
    /// <c>.cws</c> still gets its own <c>HKCR\.cws</c> default value naming the ProgId whose single
    /// open verb Explorer then runs. A <c>ContentType</c> may safely repeat across extensions —
    /// that row IS per-extension — which is why this checks verbs and not content types.</para>
    ///
    /// <para>Nothing else notices. <c>wix build</c> is the only other check, and it runs on Windows,
    /// with the WiX toolset installed, at release time — the furthest possible point from the
    /// edit.</para>
    /// </summary>
    [Fact]
    public void WindowsInstallerDeclaresEachProgIdVerbOnlyOnce()
    {
        var wxs = XDocument.Load(RepoFile("packaging", "windows", "circuitRF.wxs"));
        XNamespace w = "http://wixtoolset.org/schemas/v4/wxs";

        var offenders = wxs.Descendants(w + "ProgId")
            .Select(p => new
            {
                ProgId = (string?)p.Attribute("Id"),
                Duplicated = p.Descendants(w + "Verb")
                              .GroupBy(v => (string?)v.Attribute("Id") ?? "open")
                              .Where(g => g.Count() > 1)
                              .Select(g => g.Key)
                              .ToList(),
            })
            .Where(x => x.Duplicated.Count > 0)
            .Select(x => $"{x.ProgId} repeats verb(s) {string.Join(", ", x.Duplicated)}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "circuitRF.wxs declares a Verb more than once under one ProgId: "
            + string.Join("; ", offenders)
            + ". A Verb registers against the ProgId, not the extension, so the second one is a "
            + "duplicate registry row and wix build fails with WIX0091. Declare the verb under the "
            + "first extension only — the others reach it through the shared ProgId.");
    }

    /// <summary>
    /// Each macOS <c>CFBundleExecutable</c> must name the renamed host for ITS application. macOS
    /// refuses to launch a bundle whose CFBundleExecutable is not a file in Contents/MacOS/, and it
    /// says so only in the system log.
    /// </summary>
    [Theory]
    [InlineData("Info.plist", "circuitRF")]
    [InlineData("Harmonica-Info.plist", "harmonicaRF")]
    [InlineData("WBond-Info.plist", "wBond")]
    public void MacBundleExecutableNamesTheRenamedHost(string plist, string expected)
    {
        var lines = File.ReadAllLines(RepoFile("src", "Ui", "Assets", "macOS", plist));
        int key = Array.FindIndex(lines, l => l.Contains("<key>CFBundleExecutable</key>", StringComparison.Ordinal));
        Assert.True(key >= 0 && key + 1 < lines.Length, $"{plist} has no CFBundleExecutable key.");
        Assert.Equal($"<string>{expected}</string>", lines[key + 1].Trim());
    }

    /// <summary>
    /// <b>The .deb must not name a versioned ICU package.</b> ICU bumps its SONAME every release and
    /// the Debian package name follows it (<c>libicu76</c>, <c>libicu77</c>, …), so a
    /// <c>Depends: libicu76 | libicu74 | …</c> lists only the versions that existed the day it was
    /// written. On a distribution shipping a newer one, apt does not fall back — it refuses the whole
    /// package ("none of the choices are installable: [no choices]"), which is how the 1.0.0-beta.1
    /// arm64 build failed to install (2026-08-21).
    ///
    /// <para>Widening the list would only move the date. The pin was never what made ICU work: the
    /// build is self-contained and .NET's globalization shim dlopen()s <c>libicuuc.so.&lt;N&gt;</c>
    /// over a wide range of N, so it finds whatever the machine has. <c>postinst</c> warns — without
    /// failing — when it finds none.</para>
    /// </summary>
    [Fact]
    public void DebDeclaresNoVersionedIcuDependency()
    {
        string script = File.ReadAllText(RepoFile("packaging", "linux", "build-deb.sh"));

        // Comments explain the history; only the fpm invocation is the package's actual metadata.
        var depends = script.Split('\n')
                            .Where(l => !l.TrimStart().StartsWith("#", StringComparison.Ordinal))
                            .Where(l => l.Contains("--depends", StringComparison.Ordinal))
                            .ToList();

        Assert.All(depends, line =>
            Assert.False(Regex.IsMatch(line, @"libicu\d"),
                "packaging/linux/build-deb.sh declares a versioned libicu dependency again — a .deb "
                + "carrying one cannot be installed on any distribution shipping an ICU release newer "
                + $"than the newest name in the list: {line.Trim()}"));

        string postinst = File.ReadAllText(RepoFile("packaging", "linux", "postinst"));
        Assert.True(postinst.Contains("libicuuc", StringComparison.Ordinal),
            "packaging/linux/postinst no longer checks for ICU at all — with no dependency declared, "
            + "that check is the only thing that tells a user with no ICU installed what to do.");
    }

    /// <summary>
    /// <b>No macOS bundle script may hard-code an architecture.</b> circuitRF ships for Apple
    /// Silicon AND Intel, and the RID is derived — from <c>CRF_RID</c> when the packaging script
    /// sets it, otherwise from <c>uname -m</c>. A literal is what was there before
    /// (<c>RID="osx-arm64"</c>, with a comment saying to edit it for Intel), and editing a
    /// checked-in script to change what it builds is exactly the step that gets forgotten — or
    /// worse, committed, so the next release quietly ships one architecture's helpers inside the
    /// other's bundle. Neither failure announces itself: the <c>.app</c> launches either way and
    /// only the device worker stops working.
    /// </summary>
    [Theory]
    [InlineData("src/Ui/bundleForMacOS.sh")]
    [InlineData("src/Ui/bundleForHarmonicaMacOS.sh")]
    [InlineData("src/Ui/bundleForWBondMacOS.sh")]
    public void MacBundleScriptsDeriveTheRid_NeverHardCodeIt(string relativePath)
    {
        var lines = File.ReadAllLines(RepoFile(relativePath.Split('/')))
                        .Where(l => !l.TrimStart().StartsWith("#", StringComparison.Ordinal))
                        .ToList();

        var hardCoded = lines.Where(l => Regex.IsMatch(l, @"^\s*RID=""?osx-")).ToList();
        Assert.True(hardCoded.Count == 0,
            $"{relativePath} assigns a literal RID: {string.Join(" | ", hardCoded.Select(l => l.Trim()))}. "
            + "Derive it from CRF_RID / uname -m instead — a checked-in literal means the release for "
            + "the other architecture is one forgotten edit away, and it fails silently.");

        Assert.True(lines.Any(l => l.Contains("uname -m", StringComparison.Ordinal)),
            $"{relativePath} no longer reads `uname -m`, so nothing in it falls back to the machine "
            + "it is running on when CRF_RID is unset.");

        Assert.True(lines.Any(l => l.Contains("CRF_RID", StringComparison.Ordinal)),
            $"{relativePath} no longer honours CRF_RID, which is how packaging/macos/build-dmg.sh "
            + "asks for the architecture it is currently building — without it that script's two "
            + "passes would both produce the host's architecture, silently.");
    }

    /// <summary>
    /// <b><c>build-dmg.sh</c> builds BOTH architectures when it is not told otherwise.</b> A release
    /// needs an Apple Silicon disk image and an Intel one, and the failure mode of a one-at-a-time
    /// default is silent in the worst way: whoever cuts the release ships whichever architecture
    /// they happened to be sitting at, and the other one simply does not exist. Nothing errors, and
    /// nobody finds out until an Intel user asks where their download is.
    ///
    /// <para>Both RIDs must appear too — this is the file that decides what "both" means, and there
    /// is no other list of the architectures circuitRF ships for.</para>
    /// </summary>
    [Fact]
    public void BuildDmg_DefaultsToBothArchitectures()
    {
        var lines = File.ReadAllLines(RepoFile("packaging", "macos", "build-dmg.sh"))
                        .Where(l => !l.TrimStart().StartsWith("#", StringComparison.Ordinal))
                        .ToList();
        string script = string.Join("\n", lines);

        Assert.True(Regex.IsMatch(script, @"\$\{2:-both\}"),
            "packaging/macos/build-dmg.sh no longer defaults its architecture argument to `both`. "
            + "A release that ships only the architecture of the machine it was cut on fails "
            + "silently — the missing .dmg is simply absent, with nothing to notice.");

        foreach (var rid in new[] { "osx-arm64", "osx-x64" })
            Assert.True(script.Contains(rid, StringComparison.Ordinal),
                $"packaging/macos/build-dmg.sh no longer names {rid}. It is the only list of the "
                + "architectures circuitRF ships a macOS build for.");
    }
    /// <summary>
    /// <b>Every macOS bundle script must re-sign <c>crf-vmhost</c> with its OWN entitlements after
    /// the deep pass.</b> <c>codesign --deep</c> re-signs every nested executable with the
    /// entitlements it was given, and circuitRF's are not crf-vmhost's — so the deep pass replaced
    /// <c>com.apple.security.virtualization</c> with circuitRF's file-access entitlement and the
    /// packaged VM host, correctly signed by <c>tools/macos-vmhost/build.sh</c> minutes earlier,
    /// arrived unable to create a virtual machine at all.
    ///
    /// <para><b>Nothing about the build said so.</b> It shows only in a bundle that has been through
    /// one of these scripts, and only when a compiled device model is actually run — so
    /// <c>dotnet run</c> worked throughout and every <c>.dmg</c> shipped a VM host that could not
    /// start. Measured 2026-08-22 with <c>codesign -d --entitlements</c>, then confirmed by booting
    /// the packaged binary before and after.</para>
    ///
    /// <para>The re-seal that follows it matters just as much and is easy to drop as redundant: the
    /// outer signature records each nested binary's cdhash, so re-signing crf-vmhost invalidates the
    /// bundle unless the bundle is signed again — and that second pass must NOT be <c>--deep</c>, or
    /// it strips the entitlement straight back off.</para>
    /// </summary>
    [Theory]
    [InlineData("src/Ui/bundleForMacOS.sh")]
    [InlineData("src/Ui/bundleForHarmonicaMacOS.sh")]
    [InlineData("src/Ui/bundleForWBondMacOS.sh")]
    public void MacBundleScripts_ReSignTheVmHostWithItsOwnEntitlements(string relativePath)
    {
        var lines = File.ReadAllLines(RepoFile(relativePath.Split('/')))
                        .Where(l => !l.TrimStart().StartsWith("#", StringComparison.Ordinal))
                        .ToList();

        Assert.True(lines.Any(l => l.Contains("crf-vmhost.entitlements", StringComparison.Ordinal)),
            $"{relativePath} never names tools/macos-vmhost/crf-vmhost.entitlements. Without a "
            + "re-sign after the `codesign --deep` pass, the bundled crf-vmhost carries circuitRF's "
            + "entitlements instead of com.apple.security.virtualization and cannot start a VM — "
            + "silently, and only in a packaged build.");

        // The re-seal: a codesign of the bundle that is NOT --deep, after the vmhost line. Matched
        // over the joined text rather than line by line, because a codesign invocation here is
        // wrapped across a backslash continuation and its $BUNDLE_DIR sits on the second line.
        int vmhost = lines.FindIndex(l => l.Contains("crf-vmhost.entitlements", StringComparison.Ordinal));
        string after = string.Join("\n", lines.Skip(vmhost)).Replace("\\\n", " ");
        bool resealed = after.Split('\n')
                             .Any(l => l.Contains("codesign", StringComparison.Ordinal)
                                    && l.Contains("BUNDLE_DIR", StringComparison.Ordinal)
                                    && !l.Contains("--deep", StringComparison.Ordinal));
        Assert.True(resealed,
            $"{relativePath} re-signs crf-vmhost but never re-seals the bundle afterwards (with a "
            + "codesign of $BUNDLE_DIR that is NOT --deep). The outer signature records the nested "
            + "binary's cdhash, so without the re-seal the bundle fails validation; with --deep it "
            + "strips the virtualization entitlement back off again.");
    }
    /// <summary>
    /// <b>No <c>--</c> inside an XML comment in any macOS plist.</b> It is illegal XML, but
    /// <c>plutil -lint</c> accepts it, so the file looks fine right up until <c>codesign</c> reads
    /// the ENTITLEMENTS file with its own stricter parser and refuses the whole thing:
    ///
    /// <code>Failed to parse entitlements: AMFIUnserializeXML: syntax error near line 19</code>
    ///
    /// <para>which names a line and not a cause. It is easy to walk into because the natural thing
    /// to write in a comment about signing is a codesign flag — <c>--options runtime</c>,
    /// <c>--timestamp</c> — and that is exactly the forbidden sequence. Hit on 2026-08-22 while
    /// documenting the hardened-runtime entitlements; spell the flags out in prose instead.</para>
    ///
    /// <para>Only the entitlements file is read by that strict parser, so the three
    /// <c>*-Info.plist</c>s had carried an illegal <c>--entitlements</c> in a comment for a long
    /// time without anything failing. They are covered here anyway: the sequence is invalid XML
    /// wherever it sits, and a comment migrating from one plist to another is exactly how a dormant
    /// one becomes fatal.</para>
    /// </summary>
    [Fact]
    public void MacPlists_HaveNoDoubleHyphenInsideAComment()
    {
        foreach (var path in Directory.GetFiles(RepoFile("src", "Ui", "Assets", "macOS"), "*.plist"))
        {
            string text = File.ReadAllText(path);
            foreach (System.Text.RegularExpressions.Match comment in Regex.Matches(text, @"<!--(.*?)-->", RegexOptions.Singleline))
            {
                string body = comment.Groups[1].Value;
                Assert.False(body.Contains("--", StringComparison.Ordinal),
                    $"{Path.GetFileName(path)} has a comment containing \"--\", which is illegal XML. "
                    + "plutil -lint accepts it; codesign does not, and refuses the file with "
                    + "\"AMFIUnserializeXML: syntax error near line N\". Write the flag out in words.");
            }
        }
    }
}
