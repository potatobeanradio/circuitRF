using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
}
