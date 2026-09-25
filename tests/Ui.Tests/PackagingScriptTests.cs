using System;
using System.Collections.Generic;
using System.IO;
using CircuitRF.Ui.Updates;
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
    /// <b>Every platform that can build a helper must actually RUN its build step.</b>
    ///
    /// <para>Reported from Windows arm64, 2026-09: placing a compiled Verilog-A model refused,
    /// naming a helper the user had no way to obtain. It was not an architecture problem. The OSDI
    /// worker's build step was conditioned <c>'$(OS)' != 'Windows_NT'</c> and had no Windows
    /// counterpart at all, so a Windows machine ran no OSDI build of any kind — and every Windows
    /// installer circuitRF has released shipped without it, on both architectures.</para>
    ///
    /// <para>The failure was silent in the way this file already guards against twice over: the
    /// application builds, installs, opens a kit and describes it correctly, and only refuses at the
    /// moment a compiled model is asked to evaluate. So the invariant is asserted structurally — for
    /// each helper, a build step conditioned on Windows and one conditioned off it.</para>
    /// </summary>
    [Theory]
    [InlineData("senior-worker", "ensure-built.sh", "ensure-built.cmd")]
    [InlineData("osdi-worker",   "build.sh",        "build.cmd")]
    public void EveryHelper_IsBuiltOnWindowsToo(string tool, string posixScript, string windowsScript)
    {
        Assert.True(File.Exists(RepoFile("tools", tool, posixScript)),   $"{tool}/{posixScript} is missing.");
        Assert.True(File.Exists(RepoFile("tools", tool, windowsScript)), $"{tool}/{windowsScript} is missing.");

        string project = File.ReadAllText(RepoFile("src", "Ui", "CircuitRF.Ui.csproj"));

        var execs = Regex.Matches(project, @"<Exec[\s\S]*?/>")
                         .Select(m => m.Value)
                         .Where(e => e.Contains(windowsScript, StringComparison.Ordinal))
                         .ToList();

        Assert.True(execs.Count > 0,
            $"{tool}/{windowsScript} exists but the .csproj never runs it, so a Windows build "
            + "produces no helper and every Windows installer ships without one.");

        // Conditioned ON Windows, not merely present: the defect was an OSDI step that existed and
        // was excluded, which reads as covered until someone checks which way the condition points.
        Assert.Contains(execs, e =>
            e.Contains("IsOSPlatform('Windows')", StringComparison.Ordinal)
            || e.Contains("'$(OS)' == 'Windows_NT'", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>Both Windows architectures of the OSDI worker ship, and packaging checks for both.</b>
    ///
    /// <para>Not a copy of the arch loop that builds the installers. This worker loads the user's own
    /// compiled model into its own process, so it must match THE MODEL'S architecture rather than
    /// circuitRF's — and an arm64 Windows machine routinely runs a translated x64 Verilog-A compiler,
    /// whose output is x64. Shipping one of the pair leaves the other's users with a helper that
    /// cannot load anything they can compile, and they find out at Run.</para>
    /// </summary>
    [Fact]
    public void BothWindowsOsdiWorkers_ArePublishedAndDemandedByPackaging()
    {
        string project = File.ReadAllText(RepoFile("src", "Ui", "CircuitRF.Ui.csproj"));
        string script  = File.ReadAllText(RepoFile("packaging", "windows", "build-windows.ps1"));
        string build   = File.ReadAllText(RepoFile("tools", "osdi-worker", "build.cmd"));

        foreach (string product in new[] { "osdi-worker-x64.exe", "osdi-worker-arm64.exe" })
        {
            Assert.True(build.Contains(product.Replace(".exe", string.Empty), StringComparison.Ordinal)
                        || build.Contains("osdi-worker-%2.exe", StringComparison.Ordinal),
                        $"tools/osdi-worker/build.cmd never produces '{product}'.");

            Assert.True(project.Contains($"$(OutDir){product}\"", StringComparison.Ordinal),
                        $"'{product}' is built beside the assemblies but never published, so no "
                        + "installer contains it. Add it to CrfPublishHelperPrograms.");

            Assert.True(script.Contains(product, StringComparison.Ordinal),
                        $"packaging/windows/build-windows.ps1 does not check for '{product}'. "
                        + "Building only warns when a compiler is missing; a RELEASE must not.");
        }
    }

    /// <summary>
    /// <b>The FLAT <c>osdi-worker.exe</c> follows the RID being published, never the machine doing
    /// the publishing.</b>
    ///
    /// <para>Windows ships three copies of this helper: the arch-suffixed pair, which
    /// <c>VerilogAFileResolver</c> picks between by reading the model's own PE header, and a flat
    /// <c>osdi-worker.exe</c>, which is what a kit's <c>device-provider.json</c> reaches by BARE
    /// COMMAND. That last route has no model file to read an architecture out of, so the copy has
    /// to be right when it is made.</para>
    ///
    /// <para><b>It was chosen from <c>%PROCESSOR_ARCHITECTURE%</c>,</b> which is correct for a
    /// developer build and wrong for every release: one run of <c>build-windows.ps1</c> publishes
    /// x86, x64 and arm64 from a single machine. 1.0.0-beta.16 therefore shipped an arm64 flat
    /// worker inside its x86 and x64 payloads — measured in the released <c>.zip</c> files, not
    /// supposed.</para>
    ///
    /// <para><b>Nothing downstream catches it,</b> and that is the point of testing it here. The
    /// resolver prefers the suffixed pair and accepts a candidate only on the evidence of its own
    /// header, so the <c>.osdi</c> route stays correct and silent; only the bare-command route
    /// fails, on a user's machine, launching a binary the processor cannot execute. This is the
    /// same rule as the Linux test below, on the platform where the flat copy exists.</para>
    /// </summary>
    [Fact]
    public void TheFlatWindowsOsdiWorker_FollowsTheTargetRid_NotTheBuildingMachine()
    {
        string project = File.ReadAllText(RepoFile("src", "Ui", "CircuitRF.Ui.csproj"));
        string build   = File.ReadAllText(RepoFile("tools", "osdi-worker", "build.cmd"));
        string script  = File.ReadAllText(RepoFile("packaging", "windows", "build-windows.ps1"));

        // 1. Every Windows RID derives a target, by RID rather than by asking the machine - the
        //    rule the Linux RIDs already carry.
        foreach (string rid in new[] { "win-x64", "win-arm64", "win-x86" })
            Assert.True(project.Contains($"'$(RuntimeIdentifier)' == '{rid}'", StringComparison.Ordinal),
                        $"the .csproj derives no OSDI worker architecture for '{rid}', so "
                        + $"`dotnet publish -r {rid}` leaves the flat worker as whatever the machine "
                        + "doing the publishing happens to be.");

        // 2. ...and it is HANDED to build.cmd. Deriving a target and not passing it is exactly the
        //    shape of the bug: everything reads correctly and nothing acts on it.
        Assert.True(Regex.IsMatch(project, @"build\.cmd&quot;[^\r\n]*\$\(_CrfOsdiTargetFlags\)"),
                    "tools/osdi-worker/build.cmd is invoked without $(_CrfOsdiTargetFlags), so it "
                    + "has nothing to go on but %PROCESSOR_ARCHITECTURE% and every payload cut on "
                    + "one machine gets that machine's flat worker.");

        // 3. build.cmd chooses the flat copy from the target it was given.
        Assert.True(build.Contains("--arch", StringComparison.Ordinal)
                    && build.Contains("targetarch=%~2", StringComparison.Ordinal),
                    "tools/osdi-worker/build.cmd ignores --arch again, so the flag above is passed "
                    + "and discarded.");

        Assert.True(build.Contains("osdi-worker-%flatarch%.exe", StringComparison.Ordinal),
                    "tools/osdi-worker/build.cmd no longer copies the flat osdi-worker.exe from the "
                    + "target's build. Copying it from %hostarch% is the defect this test exists for.");

        // 4. And packaging does not take any of that on trust, exactly as build-linux.sh reads the
        //    ELF header back out of its publish tree. Building only warns; a RELEASE must not.
        Assert.True(script.Contains("Get-PeMachine", StringComparison.Ordinal)
                    && script.Contains("0xAA64", StringComparison.Ordinal),
                    "packaging/windows/build-windows.ps1 does not read the flat osdi-worker.exe's "
                    + "PE machine back out of the publish tree, so a helper that quietly fell back "
                    + "to the building machine reaches a released package - which is how it did.");
    }

    /// <summary>
    /// <b>Both Linux architectures of the OSDI worker are targeted by the build and demanded by
    /// packaging.</b>
    ///
    /// <para>The Windows half of this rule is above; this is the same rule on the platform where it
    /// went unnoticed longest. Until 2026-09 <c>tools/osdi-worker/build.sh</c> took its
    /// <c>--arch</c> flag only on macOS and the .csproj derived one only for the <c>osx-*</c> RIDs,
    /// so a Linux publish compiled for whatever machine happened to be running it and dropped the
    /// result into that RID's publish tree under the bare name packaging copies verbatim — an x86-64
    /// ELF in the arm64 <c>.deb</c> and tarball when cut on an x64 box, and a Mach-O binary
    /// altogether when a Mac published for Linux.</para>
    ///
    /// <para><b>Nothing downstream catches it.</b> The architecture guard in
    /// <c>VerilogAFileResolver</c> reads a PE header, and a POSIX worker that declares no
    /// architecture is deliberately used as-is — see <c>OsdiWorkerArchitectureTests</c> — so the
    /// first symptom is an exec failure on a user's machine, naming a file that is plainly there.
    /// Three things therefore have to hold together, and each is asserted where it lives.</para>
    /// </summary>
    [Fact]
    public void BothLinuxOsdiWorkers_AreTargetedByTheBuildAndDemandedByPackaging()
    {
        string build   = File.ReadAllText(RepoFile("tools", "osdi-worker", "build.sh"));
        string project = File.ReadAllText(RepoFile("src", "Ui", "CircuitRF.Ui.csproj"));
        string script  = File.ReadAllText(RepoFile("packaging", "linux", "build-linux.sh"));

        // 1. The script can produce EITHER architecture, from either machine. One compiler builds
        //    one target, so a cross route has to be named for this to be true at all.
        foreach (string target in new[] { "x86_64-linux-gnu", "aarch64-linux-gnu" })
            Assert.True(build.Contains(target, StringComparison.Ordinal),
                        $"tools/osdi-worker/build.sh cannot cross-build '{target}', so the other "
                        + "Linux architecture can only ever be built on a machine that already is one.");

        // 2. ...and it is TOLD which one, by RID rather than by asking the machine. The target OS
        //    travels with it: an architecture alone leaves a Mach-O and an ELF indistinguishable,
        //    which is exactly how a Mach-O reached publish/linux-x64.
        foreach (string rid in new[] { "linux-x64", "linux-arm64" })
            Assert.True(project.Contains($"'$(RuntimeIdentifier)' == '{rid}'", StringComparison.Ordinal),
                        $"the .csproj derives no OSDI worker architecture for '{rid}', so "
                        + "`dotnet publish -r " + rid + "` builds for the machine doing the publishing.");

        Assert.True(project.Contains("--os $(_CrfOsdiOs)", StringComparison.Ordinal),
                    "the OSDI worker's build step is never told its target OS, so a Mac publishing "
                    + "for Linux writes a Mach-O binary into the Linux publish tree.");

        // 3. And packaging does not take any of that on trust, exactly as the macOS script runs lipo
        //    over the bundle. Building only warns; a RELEASE must not.
        Assert.True(script.Contains("osdi-worker", StringComparison.Ordinal),
                    "packaging/linux/build-linux.sh never mentions the OSDI worker, so a Linux "
                    + "package can ship without it — or with one of the wrong architecture — in "
                    + "silence.");

        Assert.True(script.Contains("7f454c46:linux-x64:3e00", StringComparison.Ordinal)
                    && script.Contains("7f454c46:linux-arm64:b700", StringComparison.Ordinal),
                    "packaging/linux/build-linux.sh does not read the OSDI worker's ELF magic and "
                    + "machine back out of the publish tree, so a helper that quietly fell back to "
                    + "the building machine reaches a released package.");
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
    /// <b>A packaging <c>.ps1</c> may only capture a native command's stderr with <c>2&gt;&amp;1</c>
    /// while <c>$ErrorActionPreference</c> is <c>'Continue'</c>.</b>
    ///
    /// <para>Under Windows PowerShell 5.1, merging a NATIVE command's stderr into the success stream
    /// while the preference is <c>'Stop'</c> does not capture that stderr — it turns the first line
    /// of it into a TERMINATING error. PowerShell wraps the line in a <c>NativeCommandError</c>,
    /// throws, and <c>$LASTEXITCODE</c> is never read. The command's exit code is irrelevant; it can
    /// be 0.</para>
    ///
    /// <para><b>So any warning at all becomes a build failure</b>, which is what makes this worth a
    /// test. It cost a release the whole x86 per-user channel (owner-reported, 2026-08-25): zig
    /// printed one line — <c>'-macrofusio' is not a recognized feature for this target (ignoring
    /// feature)</c>, an LLVM warning that says in its own text that it is carrying on — the compile
    /// SUCCEEDED, and the launcher stub was reported as unbuildable. The .msi and the .zip update
    /// payload were then skipped for that architecture, so nobody on it would have been offered the
    /// next version.</para>
    ///
    /// <para>The tell is that nothing the script itself prints appeared: the one line in the log was
    /// the caught exception's own Message, because a NativeCommandError's message IS the stderr text
    /// it objected to.</para>
    ///
    /// <para>The rule is checkable, so it is checked rather than remembered: every <c>2&gt;&amp;1</c>
    /// must sit inside a block that has just set the preference to <c>'Continue'</c>. It is a real
    /// constraint on new code — the natural way to write this capture is the broken way, and it
    /// works perfectly under the pwsh 7 on a developer's machine, which dropped the behaviour.</para>
    /// </summary>
    [Fact]
    public void PowerShellScripts_CaptureNativeStderr_OnlyWithErrorActionContinue()
    {
        var scripts = Directory.GetFiles(RepoFile("packaging"), "*.ps1", SearchOption.AllDirectories);
        Assert.NotEmpty(scripts);

        // The preference is TRACKED down the file rather than looked for within some window of lines
        // above the capture: a window is a proxy for "inside the relaxed block", and it gets the
        // answer wrong in both directions once the block is longer than the guess. Restoring it from
        // a saved variable counts as back to 'Stop', which is the conservative reading and the one
        // every one of these scripts actually means.
        var offences = new List<string>();
        foreach (var path in scripts)
        {
            var lines = File.ReadAllLines(path);
            bool relaxed = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                bool comment = line.TrimStart().StartsWith("#", StringComparison.Ordinal);

                if (!comment)
                {
                    var set = Regex.Match(line, @"\$ErrorActionPreference\s*=\s*(.+)$");
                    if (set.Success)
                        relaxed = set.Groups[1].Value.TrimStart().StartsWith("'Continue'", StringComparison.Ordinal);
                }

                if (comment || !line.Contains("2>&1", StringComparison.Ordinal)) continue;

                if (!relaxed)
                    offences.Add($"{Path.GetFileName(path)}:{i + 1}: {line.Trim()}");
            }
        }

        Assert.True(offences.Count == 0,
            "A native command's stderr is captured with 2>&1 while $ErrorActionPreference is 'Stop'. "
            + "Under Windows PowerShell 5.1 that makes the first WARNING a terminating error and the "
            + "exit code is never read, so a compiler that succeeded is reported as having failed:\n  "
            + string.Join("\n  ", offences));
    }

    /// <summary>
    /// <b>IconGen must name a Linux native SkiaSharp itself.</b> SkiaSharp 4.148.0 - what Svg.Skia
    /// 5.2.1 resolves to - declares <c>SkiaSharp.NativeAssets.Win32</c> and <c>.macOS</c> as
    /// dependencies and no Linux equivalent, so on Linux nothing puts a <c>libSkiaSharp.so</c> in the
    /// output and the tool dies at its first <c>SKSvg()</c> with a <c>DllNotFoundException</c>. Every
    /// packaging script runs IconGen first, so this took out the whole of <c>build-linux.sh</c> at its
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
    [InlineData("packaging/windows/build-windows.ps1", "$exeName = 'circuitRF.exe'")]
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
    /// <b>Both launcher-stub scripts must compile the application icon into the stub.</b> In a
    /// per-user install the file at the install root is the stub, not the application, so the
    /// stub's own PE resources are what Explorer draws for the <c>.exe</c>, what a shortcut
    /// inherits, and what every <c>Icon="CircuitRfExe"</c> file association in the <c>.wxs</c>
    /// resolves to. Built without one the stub had <b>no <c>.rsrc</c> section at all</b> - read
    /// back out of the PE - and the reported symptom was a Desktop shortcut wearing the generic
    /// Windows icon (owner-reported, 2026-09-02).
    ///
    /// <para><c>build-stub.sh</c> is checked alongside <c>build-stub.ps1</c> because THE TWO MUST
    /// AGREE - its own header says so, and it has drifted from the .ps1 before and produced a stub
    /// that could not work at all, which nothing noticed because a release is cut on Windows.</para>
    ///
    /// <para>This checks the wiring, not the output: that each script finds the <c>.ico</c>, writes
    /// a resource script, and hands it to the compiler. Whether the icon actually landed is checked
    /// at build time by each script itself, against the PE it just produced.</para>
    /// </summary>
    [Theory]
    [InlineData("build-stub.ps1")]
    [InlineData("build-stub.sh")]
    public void LauncherStubScriptsCompileTheIconIntoTheStub(string script)
    {
        var text = File.ReadAllText(RepoFile("packaging", "windows", "stub", script));

        Assert.True(text.Contains("Icon.ico", StringComparison.Ordinal),
            $"{script} no longer locates the application .ico in src/Ui/Assets, so the stub it "
            + "builds carries no icon and the Desktop shortcut falls back to the Windows default.");

        Assert.True(text.Contains("ICON", StringComparison.Ordinal) &&
                    text.Contains("-icon.rc", StringComparison.Ordinal),
            $"{script} no longer generates the .rc resource script that carries the icon into the "
            + "stub's PE resources.");

        // The icon is worth nothing if the resource script never reaches the compiler. Each script
        // spells that differently - the .ps1 through an $iconArgs/$resArgs splat, the .sh through
        // $rc_arg on the zig line - so this asserts the variable that carries it is USED, not just
        // assigned.
        int uses = script.EndsWith(".ps1", StringComparison.Ordinal)
            ? Occurrences(text, "$iconArgs") + Occurrences(text, "$resArgs")
            : Occurrences(text, "rc_arg");

        Assert.True(uses >= 2,
            $"{script} builds the resource script but never passes it to the compiler - the stub "
            + "would compile cleanly and still have no icon.");
    }

    private static int Occurrences(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    /// <summary>
    /// <b>Every <c>Shortcut</c> must NAME its icon.</b> Windows draws a non-advertised shortcut with
    /// the icon embedded in its TARGET, and in perUser scope the target is the launcher stub
    /// <c>build-stub.ps1</c> compiles from one C file with no resource script — so it carries no
    /// icon at all and the Desktop shortcut the installer laid down drew the generic default
    /// (owner-reported). Naming <c>Icon</c> fixes both scopes without depending on the stub's
    /// toolchain, and the <c>Icon</c> element it points at is the one <c>ARPPRODUCTICON</c> already
    /// uses.
    ///
    /// <para>This is the kind of regression nothing else catches: <c>wix build</c> is perfectly
    /// happy with an icon-less shortcut, so the first report comes from a user looking at their
    /// Desktop after an install.</para>
    /// </summary>
    [Fact]
    public void WindowsInstallerShortcutsNameTheirIcon()
    {
        var wxs = XDocument.Load(RepoFile("packaging", "windows", "circuitRF.wxs"));
        XNamespace w = "http://wixtoolset.org/schemas/v4/wxs";

        var declaredIcons = wxs.Descendants(w + "Icon")
                               .Select(i => (string?)i.Attribute("Id"))
                               .Where(id => id is not null)
                               .ToHashSet();

        var shortcuts = wxs.Descendants(w + "Shortcut").ToList();
        Assert.True(shortcuts.Count > 0, "circuitRF.wxs declares no Shortcut at all.");

        var offenders = shortcuts
            .Select(sc => new { Id = (string?)sc.Attribute("Id"), Icon = (string?)sc.Attribute("Icon") })
            .Where(x => string.IsNullOrEmpty(x.Icon) || !declaredIcons.Contains(x.Icon))
            .Select(x => $"{x.Id} (Icon=\"{x.Icon}\")")
            .ToList();

        Assert.True(offenders.Count == 0,
            "circuitRF.wxs has a Shortcut that does not name a declared Icon: "
            + string.Join("; ", offenders)
            + ". Without it the shortcut inherits its target's embedded icon, and the perUser "
            + "target is the icon-less launcher stub — so the Desktop shortcut shows the generic "
            + "Windows default. Add Icon=\"circuitRFIcon.ico\" IconIndex=\"0\".");
    }

    /// <summary>
    /// <b>No two Windows packages may share an UpgradeCode.</b> Six of them: three architectures
    /// times two install scopes.
    ///
    /// <para>One UpgradeCode per scope is what shipped through 1.0.0-beta.16, and it tells Windows
    /// Installer that the x86, x64 and arm64 packages are ONE product built for different machines.
    /// Running one over another is then a major upgrade — a 32-bit package asked to remove a 64-bit
    /// product's components — which is the case Windows Installer handles worst. The symptom is not
    /// an error: the install stalls in InstallValidate with "Computing space requirements" on
    /// screen and eventually gives up. That was reported against the x86 installer.</para>
    ///
    /// <para>They are not one product. Each carries a different apphost and different native Skia
    /// and ANGLE binaries, and in perMachine scope each installs to a different Program Files. The
    /// two should simply never see each other.</para>
    ///
    /// <para><b>x64 keeps the codes it has already shipped with.</b> An UpgradeCode is a product's
    /// identity over time, so abandoning one orphans every install carrying it — the next version
    /// cannot find the old one to remove and lands beside it instead. x64 is the install base, so
    /// it keeps its lineage; x86 and arm64, whose lineage is worth less than the hazard it carries,
    /// took new codes. Those two literals are pinned here for that reason and must not be
    /// "tidied".</para>
    /// </summary>
    [Fact]
    public void WindowsUpgradeCodes_AreDistinctPerScopeAndArchitecture()
    {
        string wxs    = File.ReadAllText(RepoFile("packaging", "windows", "circuitRF.wxs"));
        string script = File.ReadAllText(RepoFile("packaging", "windows", "build-windows.ps1"));

        var pairs = Regex.Matches(
                wxs,
                @"<\?(?:if|elseif)\s+\$\(var\.Arch\)\s*=\s*""(\w+)""\s*\?>\s*<\?define\s+UpgradeGuid\s*=\s*""([0-9A-Fa-f-]{36})""\s*\?>")
            .Select(m => new { Arch = m.Groups[1].Value, Guid = m.Groups[2].Value.ToUpperInvariant() })
            .ToList();

        Assert.True(pairs.Count == 6,
                    $"circuitRF.wxs declares {pairs.Count} architecture-keyed UpgradeCodes, not 6 "
                    + "(x86, x64 and arm64, in each of perMachine and perUser). build-windows.ps1 "
                    + "builds all six by default, so every one of them needs its own.");

        var shared = pairs.GroupBy(x => x.Guid).Where(g => g.Count() > 1)
                          .Select(g => g.Key + " <- " + string.Join(", ", g.Select(x => x.Arch)))
                          .ToList();

        Assert.True(shared.Count == 0,
                    "circuitRF.wxs gives one UpgradeCode to more than one Windows package: "
                    + string.Join("; ", shared)
                    + ". Windows Installer then treats them as the same product, so installing one "
                    + "over another is a cross-architecture major upgrade - which stalls in "
                    + "InstallValidate rather than failing.");

        // x64's two codes are its identity across every release so far. Changing either orphans
        // every x64 install that carries it.
        Assert.True(pairs.Any(x => x.Arch == "x64" && x.Guid == "70CB9791-A58B-444F-8153-63DB32CE7235"),
                    "the perMachine x64 UpgradeCode has changed. Every x64 install already out "
                    + "there carries the old one, and the next version will no longer find it to "
                    + "remove - it will install beside it.");

        Assert.True(pairs.Any(x => x.Arch == "x64" && x.Guid == "1E4F8D22-6C0B-4A57-9E3D-0B71C4A8F015"),
                    "the perUser x64 UpgradeCode has changed; see above. This is the scope that "
                    + "updates itself, so an orphaned install keeps updating the wrong copy.");

        // The .wxs cannot read wix's own -arch back, so it is told separately.
        Assert.True(script.Contains("-d \"Arch=$Arch\"", StringComparison.Ordinal),
                    "packaging/windows/build-windows.ps1 does not pass -d Arch to wix, so the .wxs "
                    + "cannot select an UpgradeCode by architecture at all.");

        // An architecture with no code of its own must STOP the build. Falling through to a shared
        // one is the defect, and a silent default would reintroduce it the day a fourth is added.
        Assert.True(wxs.Contains("<?error", StringComparison.Ordinal),
                    "circuitRF.wxs has no <?error?> branch for an architecture it has no "
                    + "UpgradeCode for, so a fourth one would silently share a code with x64.");
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
    /// <b>Every macOS bundle must declare the file-access usage descriptions.</b> macOS gates
    /// <c>~/Documents</c>, <c>~/Desktop</c>, <c>~/Downloads</c>, removable volumes and network
    /// volumes behind per-folder privacy grants and shows a consent prompt the first time an app
    /// touches one; these strings are that prompt's explanatory text, and Apple documents them as
    /// required for these services.
    ///
    /// <para><b>Gated because the failure is silent and self-concealing.</b> With no string declared
    /// the prompt is at best unexplained and at worst never shown — and a request that is never
    /// prompted is denied. The app then reports "Access to the path … is denied" for a file whose
    /// own permissions are perfectly normal, and it may never appear in System Settings &gt; Privacy
    /// &amp; Security &gt; Files and Folders at all, because that list is populated by apps that have
    /// actually asked. The user is told to enable a setting that is not there. Nothing crashes and
    /// nothing is logged — the app simply cannot open the user's own documents.</para>
    ///
    /// <para>All three bundles, because a key added to circuitRF's plist and forgotten in
    /// harmonicaRF's or wBond's fails only in the app nobody tested — the same three-place trap the
    /// bundle identity keys already carry.</para>
    /// </summary>
    [Theory]
    [InlineData("Info.plist", "circuitRF")]
    [InlineData("Harmonica-Info.plist", "harmonicaRF")]
    [InlineData("WBond-Info.plist", "wBond")]
    public void MacBundleDeclaresTheFileAccessUsageDescriptions(string plist, string appName)
    {
        string text = File.ReadAllText(RepoFile("src", "Ui", "Assets", "macOS", plist));

        foreach (var key in new[]
                 {
                     "NSDocumentsFolderUsageDescription",
                     "NSDesktopFolderUsageDescription",
                     "NSDownloadsFolderUsageDescription",
                     "NSRemovableVolumesUsageDescription",
                     "NSNetworkVolumesUsageDescription",
                 })
        {
            int i = text.IndexOf($"<key>{key}</key>", StringComparison.Ordinal);
            Assert.True(i >= 0,
                $"{plist} does not declare {key}. Without it macOS may deny access to that location " +
                $"WITHOUT ever prompting, and {appName} will not appear in the Files and Folders " +
                $"privacy list for the user to grant — see the note above this test.");

            // The prompt shows this sentence. An empty or placeholder string is the same failure
            // wearing a key, and it is what a copy-paste between the three plists produces.
            int open  = text.IndexOf("<string>", i, StringComparison.Ordinal);
            int close = text.IndexOf("</string>", open, StringComparison.Ordinal);
            string value = text[(open + "<string>".Length)..close].Trim();

            Assert.True(value.Length >= 30, $"{plist}'s {key} is too short to explain anything: '{value}'");
            Assert.StartsWith(appName, value, StringComparison.Ordinal);
        }
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
        string script = File.ReadAllText(RepoFile("packaging", "linux", "build-linux.sh"));

        // Comments explain the history; only the fpm invocation is the package's actual metadata.
        var depends = script.Split('\n')
                            .Where(l => !l.TrimStart().StartsWith("#", StringComparison.Ordinal))
                            .Where(l => l.Contains("--depends", StringComparison.Ordinal))
                            .ToList();

        Assert.All(depends, line =>
            Assert.False(Regex.IsMatch(line, @"libicu\d"),
                "packaging/linux/build-linux.sh declares a versioned libicu dependency again — a .deb "
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
            $"{relativePath} no longer honours CRF_RID, which is how packaging/macos/build-macos.sh "
            + "asks for the architecture it is currently building — without it that script's two "
            + "passes would both produce the host's architecture, silently.");
    }

    /// <summary>
    /// <b><c>build-macos.sh</c> builds BOTH architectures when it is not told otherwise.</b> A release
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
        var lines = File.ReadAllLines(RepoFile("packaging", "macos", "build-macos.sh"))
                        .Where(l => !l.TrimStart().StartsWith("#", StringComparison.Ordinal))
                        .ToList();
        string script = string.Join("\n", lines);

        Assert.True(Regex.IsMatch(script, @"\$\{2:-both\}"),
            "packaging/macos/build-macos.sh no longer defaults its architecture argument to `both`. "
            + "A release that ships only the architecture of the machine it was cut on fails "
            + "silently — the missing .dmg is simply absent, with nothing to notice.");

        foreach (var rid in new[] { "osx-arm64", "osx-x64" })
            Assert.True(script.Contains(rid, StringComparison.Ordinal),
                $"packaging/macos/build-macos.sh no longer names {rid}. It is the only list of the "
                + "architectures circuitRF ships a macOS build for.");
    }
    /// <summary>
    /// <b>ONE script per platform, and each builds EVERYTHING that platform ships.</b> This is the
    /// same rule <see cref="BuildDmg_DefaultsToBothArchitectures"/> holds for macOS, applied to the
    /// two platforms that did not have it — and it is written down because the absence of it cost a
    /// release.
    ///
    /// <para>1.0.0-beta.2 shipped with seven of its fifteen artifacts. Windows defaulted to ONE
    /// architecture in ONE install scope, so a complete Windows release was six invocations and
    /// looked like three; Linux was two scripts each defaulting to one architecture. Whoever cut the
    /// release ran the obvious form of each and got the notify-only <c>.msi</c> and <c>.deb</c>
    /// files, with the <c>.zip</c> and <c>.tar.gz</c> the updater fetches simply absent. Nothing
    /// errored. Nobody on Windows or Linux would have been offered the next version — or, because
    /// <c>UpdateSelector</c> needs a matching asset before it will even post the notify-only line,
    /// TOLD about it.</para>
    ///
    /// <para>A release script whose obvious invocation produces an incomplete release is the
    /// script's bug, not the operator's.</para>
    /// </summary>
    [Fact]
    public void EachPlatformScript_BuildsEverythingByDefault()
    {
        string windows = File.ReadAllText(RepoFile("packaging", "windows", "build-windows.ps1"));

        Assert.True(Regex.IsMatch(windows, @"\$Arch\s*=\s*'all'"),
            "packaging/windows/build-windows.ps1 no longer defaults -Arch to 'all'. A release cut "
            + "with the default would ship one architecture and silently omit the other two.");

        Assert.True(Regex.IsMatch(windows, @"\$Scope\s*=\s*'all'"),
            "packaging/windows/build-windows.ps1 no longer defaults -Scope to 'all'. A release cut "
            + "with the default would ship the notify-only .msi files and omit BOTH the per-user "
            + "installer and the .zip the updater fetches — which stops Windows updates silently.");

        string linux = File.ReadAllText(RepoFile("packaging", "linux", "build-linux.sh"));

        Assert.True(linux.Contains("${1:-both}", StringComparison.Ordinal),
            "packaging/linux/build-linux.sh no longer defaults its architecture argument to `both`.");

        Assert.True(linux.Contains("${2:-both}", StringComparison.Ordinal),
            "packaging/linux/build-linux.sh no longer defaults its package-kind argument to `both`. "
            + "The tarball is the only self-updating Linux install AND the update payload; a release "
            + "without it stops Linux updates with no error anywhere.");
    }

    /// <summary>
    /// <b>Exactly three build scripts, one per platform.</b> The count is the point: a fourth is a
    /// second thing to remember to run, which is precisely how 1.0.0-beta.2 shipped incomplete.
    /// The retired names are asserted gone rather than merely unreferenced, so a re-added copy fails
    /// here instead of quietly becoming the one somebody runs.
    /// </summary>
    [Fact]
    public void ThereAreExactlyThreeBuildScripts_OnePerPlatform()
    {
        foreach (var (dir, name) in new[]
                 {
                     ("windows", "build-windows.ps1"),
                     ("linux",   "build-linux.sh"),
                     ("macos",   "build-macos.sh"),
                 })
            Assert.True(File.Exists(RepoFile("packaging", dir, name)),
                        $"packaging/{dir}/{name} is missing.");

        foreach (var (dir, name) in new[]
                 {
                     ("windows", "build-msi.ps1"),
                     ("linux",   "build-deb.sh"),
                     ("linux",   "build-tarball.sh"),
                     ("macos",   "build-dmg.sh"),
                 })
            Assert.False(File.Exists(RepoFile("packaging", dir, name)),
                         $"packaging/{dir}/{name} is back. There is one build script per platform; "
                         + "a second one is a second thing to remember to run.");

        var found = Directory
            .GetFiles(Path.Combine(RepoRoot().FullName, "packaging"), "build-*", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .Where(n => n is not null && !n.StartsWith("build-stub", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "build-linux.sh", "build-macos.sh", "build-windows.ps1" }, found);
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
    /// <b>Every macOS bundle script refuses a directory with a dot in its name under
    /// <c>Contents/MacOS</c>, before it reaches <c>codesign</c>.</b>
    ///
    /// <para>codesign reads every directory under <c>Contents/MacOS</c> as code, and a dot in a
    /// directory's NAME makes it read that directory as a nested bundle. Finding no Info.plist, it
    /// rejects the whole app:</para>
    ///
    /// <code>
    /// circuitRF.app: bundle format unrecognized, invalid, or unsuitable
    /// In subcomponent: .../Contents/MacOS/examples/PDK PCells/.generated-cells
    /// </code>
    ///
    /// <para>Measured 2026-09-16, and each half separately: an EMPTY directory called <c>.foo</c>
    /// fails identically, so the contents are irrelevant; <c>--deep</c> changes nothing, so the flag
    /// is irrelevant; a directory called <c>results</c> signs; and the same tree under
    /// <c>Contents/Resources</c> signs. Dot FILES are unaffected — the example workspaces' <c>.cws</c>
    /// and <c>.ccell</c> sign without comment.</para>
    ///
    /// <para>The one that actually broke a release build was a workspace's local
    /// <c>.generated-cells</c> PCell cache, which <c>CircuitRF.Ui.csproj</c> now excludes from the
    /// examples copy. The refusal stays anyway, because the publish tree under <c>src/Ui/bin</c> is
    /// never cleaned — an exclusion added today leaves yesterday's copy sitting there — and because
    /// codesign's own message names the directory and neither the rule nor the remedy.</para>
    /// </summary>
    [Theory]
    [InlineData("src/Ui/bundleForMacOS.sh")]
    [InlineData("src/Ui/bundleForHarmonicaMacOS.sh")]
    [InlineData("src/Ui/bundleForWBondMacOS.sh")]
    public void MacBundleScripts_RefuseADottedDirectoryUnderContentsMacOS(string relativePath)
    {
        var lines = File.ReadAllLines(RepoFile(relativePath.Split('/')))
                        .Where(l => !l.TrimStart().StartsWith("#", StringComparison.Ordinal))
                        .ToList();

        int check = lines.FindIndex(l => l.Contains("-type d -name '*.*'", StringComparison.Ordinal)
                                      && l.Contains("MAC_OS_DIR", StringComparison.Ordinal));
        Assert.True(check >= 0,
            $"{relativePath} no longer scans $MAC_OS_DIR for a directory whose NAME contains a dot. "
            + "codesign reads such a directory as a nested bundle and rejects the entire .app with "
            + "\"bundle format unrecognized, invalid, or unsuitable\" — naming the directory and "
            + "nothing else, so the cause is not apparent from the message.");

        // It has to REFUSE, not warn: an unsigned or half-signed .app is not a shippable outcome.
        string after = string.Join("\n", lines.Skip(check).Take(24));
        Assert.True(after.Contains("exit 1", StringComparison.Ordinal),
            $"{relativePath} finds the dotted directory but does not exit non-zero, so the build "
            + "carries on into codesign and fails there instead — with codesign's message, which is "
            + "the one this check exists to replace.");

        // And BEFORE codesign, or it has replaced nothing.
        int sign = lines.FindIndex(l => l.Contains("codesign", StringComparison.Ordinal));
        Assert.True(sign > check,
            $"{relativePath} runs codesign before the dotted-directory check, so codesign still gets "
            + "there first and the check never speaks.");
    }

    /// <summary>
    /// <b>Every macOS bundle script drops the OTHER two applications' hosts out of
    /// <c>Contents/MacOS</c>.</b>
    ///
    /// <para>circuitRF, harmonicaRF and wBond are one project with three <c>Main</c>s, and
    /// <c>CrfApp</c> selects the StartupObject and the renamed host — NOT <c>$(PublishDir)</c>. So
    /// all three publish into the same directory, and a publish tree never deletes anything. Once a
    /// machine has bundled all three for one RID, that directory holds all three hosts, and each is
    /// a SELF-CONTAINED SINGLE-FILE binary carrying the entire application: ~131 MB apiece. The
    /// bundle scripts copy the tree wholesale, so circuitRF.app shipped harmonicaRF and wBond
    /// inside it.</para>
    ///
    /// <para>Nothing failed. The app installed, launched and ran correctly the whole time — the only
    /// symptom was size, and size alone reads as "self-contained .NET is large". It was found on
    /// 2026-09-16 because the arm64 circuitRF .dmg was 234 MB against the x64 one's 122 MB, and the
    /// only difference between those two machines-worth of output was that the other two
    /// applications had never been bundled for Intel.</para>
    ///
    /// <para>Which is why this is a test and not a comment: the leak is invisible in every check
    /// that already runs, it only appears once someone happens to build all three, and it comes
    /// straight back the moment the prune is dropped from a script during an edit.</para>
    /// </summary>
    [Theory]
    [InlineData("src/Ui/bundleForMacOS.sh")]
    [InlineData("src/Ui/bundleForHarmonicaMacOS.sh")]
    [InlineData("src/Ui/bundleForWBondMacOS.sh")]
    public void MacBundleScripts_DropTheOtherApplicationsHosts(string relativePath)
    {
        var lines = File.ReadAllLines(RepoFile(relativePath.Split('/')))
                        .Where(l => !l.TrimStart().StartsWith("#", StringComparison.Ordinal))
                        .ToList();

        int copy = lines.FindIndex(l => l.Contains("cp -R", StringComparison.Ordinal)
                                     && l.Contains("PUBLISH_DIR", StringComparison.Ordinal));
        Assert.True(copy >= 0, $"{relativePath} no longer copies the publish tree into the bundle.");

        // The loop names all three hosts, so adding a fourth application is a compile-time-visible
        // edit here rather than a silent 131 MB somewhere.
        int prune = lines.FindIndex(copy, l => l.Contains("circuitRF harmonicaRF wBond", StringComparison.Ordinal));
        Assert.True(prune >= 0,
            $"{relativePath} copies the whole publish tree into Contents/MacOS and never removes the "
            + "other two applications' hosts. All three apps publish to ONE directory (CrfApp changes "
            + "the StartupObject, not $(PublishDir)) and nothing cleans it, so on any machine that has "
            + "bundled all three this ships two extra self-contained single-file binaries — about "
            + "262 MB — inside an app that works perfectly and says nothing.");

        string body = string.Join("\n", lines.Skip(prune).Take(8));
        Assert.True(body.Contains("EXECUTABLE_NAME", StringComparison.Ordinal),
            $"{relativePath} prunes by a hard-coded name rather than by EXECUTABLE_NAME, so the three "
            + "scripts no longer say the same thing and one of them removes the host it is building.");
        Assert.True(body.Contains("rm -f", StringComparison.Ordinal)
                 && body.Contains("MAC_OS_DIR", StringComparison.Ordinal),
            $"{relativePath} names the other hosts but does not delete them from $MAC_OS_DIR.");

        // Before codesign, or two foreign binaries are sealed into the signature.
        int sign = lines.FindIndex(l => l.Contains("codesign", StringComparison.Ordinal));
        Assert.True(sign > prune,
            $"{relativePath} signs the bundle before dropping the other applications' hosts.");
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

    // -- the release key, and what each script says about it ---------------------------------

    /// <summary>
    /// All three platform scripts must end by saying whether this build can be installed as an
    /// automatic update. It is the one thing about a packaging run that is invisible in its output
    /// and silent when wrong: a release published without a signed manifest is offered to nobody,
    /// and says so nowhere.
    /// </summary>
    [Theory]
    [InlineData("packaging/macos/build-macos.sh")]
    [InlineData("packaging/linux/build-linux.sh")]
    [InlineData("packaging/windows/build-windows.ps1")]
    public void EveryPlatformScriptReportsWhetherAReleaseKeyIsCompiledIn(string script)
    {
        string text = File.ReadAllText(RepoFile(script.Split('/')));

        // Every one of them names the source of truth it reads the key out of.
        Assert.Contains("ReleaseKeys.cs", text);

        // The two shell scripts share one implementation (packaging/signing-status.sh); the .ps1 cannot
        // source a .sh, so it restates it. Either way the run must END by saying which it is.
        Assert.True(text.Contains("crf_report_release_key") || text.Contains("Release key:"),
                    $"{script} does not report whether a release key is compiled in.");
    }

    /// <summary>
    /// The wording the two shell scripts share lives in one file, and it has to name the script that
    /// finishes the job — a build that says "signed releases auto-update" without saying what signs
    /// one has moved the silent failure rather than removed it.
    /// </summary>
    [Fact]
    public void TheSharedReportNamesTheSigningStep()
    {
        string helper = File.ReadAllText(RepoFile("packaging", "signing-status.sh"));

        Assert.Contains("Release key:", helper);
        Assert.Contains("sign-release.sh", helper);
        Assert.Contains("notify-only", helper);      // the Windows consequence of having no key
    }

    /// <summary>
    /// Signing lives in ONE script, and deliberately not in the three platform ones: a release
    /// carries a single manifest covering every asset, and the three run on three different machines
    /// that each see only their own share. Three partial manifests cannot be combined.
    /// </summary>
    [Fact]
    public void SigningIsOneScript_AndNoPlatformScriptSignsAManifest()
    {
        Assert.True(File.Exists(RepoFile("packaging", "sign-release.sh")));

        foreach (string script in new[]
                 {
                     "packaging/macos/build-macos.sh",
                     "packaging/linux/build-linux.sh",
                     "packaging/windows/build-windows.ps1",
                 })
        {
            string text = File.ReadAllText(RepoFile(script.Split('/')));
            Assert.DoesNotContain("ReleaseSigner", text);
        }
    }

    /// <summary>
    /// <c>build-windows.ps1</c> extracts the compiled-in public key with a regex, and the constant is
    /// written as adjacent string literals across several lines — so a pattern that took only the
    /// first quoted run would truncate the key silently and every release would then verify against
    /// nothing.
    ///
    /// <para>Run here in .NET because there is no PowerShell on a macOS build box; what is pinned is
    /// the PATTERN and the shape of the declaration it reads, which is the half that can rot. The
    /// shell scripts' <c>sed</c>/<c>grep</c> spelling of the same extraction is exercised directly
    /// whenever they run.</para>
    /// </summary>
    [Fact]
    public void TheWindowsScriptsKeyRegexReadsTheWholeKey()
    {
        string keys = File.ReadAllText(RepoFile("src", "Ui", "Updates", "ReleaseKeys.cs"));

        // Exactly what the .ps1 does, in the same order.
        string decl = Regex.Replace(keys, @"(?s).*PublicKeySpkiBase64\s*=", "").Split(';')[0];
        string pub  = string.Concat(Regex.Matches(decl, "\"([^\"]*)\"")
                                         .Select(m => m.Groups[1].Value));

        Assert.Equal(ReleaseKeys.PublicKeySpkiBase64, pub);

        // And it is not passing by both being empty, which would prove nothing about the joining.
        Assert.NotEqual("", pub);
        Assert.True(decl.Split('"').Length - 1 >= 2, "the declaration is no longer split across literals");
    }
    /// <summary>
    /// The GitHub pre-release flag is the WHOLE channel mechanism — <c>UpdateSelector</c> filters on
    /// <c>includeBetas || !r.IsPreRelease</c> and on nothing else — so the release script must set it,
    /// and must set it from the one thing that decides it. Getting this wrong is silent in both
    /// directions: a beta published as a stable release is pushed to every user who never asked for
    /// beta code, and a stable release published as a pre-release is offered to nobody but testers.
    /// </summary>
    [Fact]
    public void TheReleaseScriptDerivesThePreReleaseFlagFromTheVersion()
    {
        string sign = File.ReadAllText(RepoFile("packaging", "sign-release.sh"));

        Assert.Contains("--prerelease", sign);

        // Derived from the '-' in the VERSION string, never asked for and never hard-coded.
        Assert.Matches(@"case\s+""\$CRF_VERSION""\s+in", sign);
        Assert.Contains("IS_PRERELEASE=1", sign);
        Assert.Contains("IS_PRERELEASE=0", sign);

        // The flag is set ONCE, at creation. `gh release edit` builds its request from the flags
        // actually passed - every bool is a NilBoolFlag, so `--draft=false` sends only `draft` and
        // leaves `prerelease` alone. So the publish line must NOT re-pass it (that would teach the
        // wrong rule) and must instead tell the reader how to confirm what actually landed.
        Assert.DoesNotContain("--draft=false --prerelease", sign);
        Assert.Contains("--json isDraft,isPrerelease", sign);
    }

    /// <summary>
    /// <b>No <c>v</c> on the tag.</b> The manifest's asset URLs are built as
    /// <c>.../releases/download/&lt;tag&gt;/&lt;file&gt;</c> from the <c>VERSION</c> string, so a tag
    /// spelled <c>v1.1.0</c> gives every client a URL that 404s — which presents as an update that
    /// never arrives rather than as an error anyone sees. Pinned across the script and the document
    /// someone actually follows, because the two drifted apart once already.
    /// </summary>
    [Fact]
    public void NothingTellsAnyoneToPrefixTheReleaseTagWithV()
    {
        string sign     = File.ReadAllText(RepoFile("packaging", "sign-release.sh"));
        string building = File.ReadAllText(RepoFile("BUILDING.md"));

        Assert.DoesNotMatch(@"download/v\$?\{?CRF_VERSION", sign);
        Assert.DoesNotMatch(@"`v<version>`|download/v<version>", building);

        // The tag the script creates and the base URL it writes are the same string, so they cannot
        // disagree about the prefix whatever that string turns out to be.
        Assert.Contains("releases/download/${CRF_VERSION}", sign);
        Assert.Contains("gh release create \"$CRF_VERSION\"", sign);
    }

    /// <summary>
    /// A partly-uploaded DRAFT must be resumable: the script's own advice after a failed upload is to
    /// re-run it, and a blanket "this tag already exists" refusal made that advice unreachable. A
    /// PUBLISHED release is still refused — re-uploading over it swaps assets under clients being
    /// offered it right now.
    /// </summary>
    [Fact]
    public void AFailedUploadCanBeRetriedIntoTheDraftButNotOverAPublishedRelease()
    {
        string sign = File.ReadAllText(RepoFile("packaging", "sign-release.sh"));

        Assert.Contains("--json isDraft", sign);
        Assert.Contains("already PUBLISHED", sign);
        Assert.Contains("Resuming the existing DRAFT", sign);
        Assert.Contains("--clobber", sign);
    }

    /// <summary>
    /// <b>The example workspaces reach all three packaged builds, dotfiles and all.</b>
    ///
    /// <para>Every platform packages the PUBLISH TREE, so the item group in
    /// <c>src/Ui/CircuitRF.Ui.csproj</c> is what puts them there and
    /// <c>ExampleWorkspacesTests</c> gates that. What this gates is the three packagers not
    /// dropping them on the way: macOS copies the tree wholesale, the Debian package maps it to
    /// <c>/opt/circuitrf/</c>, and Windows HARVESTS it into <c>Files.wxs</c> — the one route that
    /// walks the tree itself and can therefore leave something out.</para>
    ///
    /// <para><b>The Windows harvest needs <c>-Force</c> and this is the test that says why.</b> A
    /// workspace's manifest is a dotfile named literally <c>.cws</c>, as is the <c>.ccell</c> beside
    /// every cell. Windows does not infer the hidden ATTRIBUTE from a leading dot, but any tool that
    /// does leaves it set, and <c>Get-ChildItem</c> without <c>-Force</c> then skips those files in
    /// silence. The installer would ship every example's cells and none of its manifests — and the
    /// symptom is an empty Tools ▸ Examples menu on the installed copy only, with nothing anywhere
    /// saying why. That is the same shape of failure as <c>tools/pcell-python</c> shipping without
    /// its package, one layer further out.</para>
    /// </summary>
    [Fact]
    public void EveryPackagerCarriesTheExampleWorkspaces()
    {
        string win = File.ReadAllText(RepoFile("packaging", "windows", "build-windows.ps1"));

        // The harvester's two enumerations, both forced. Asserted on the harvester specifically,
        // because a -Force anywhere else in the script would satisfy a looser check.
        int at = win.IndexOf("function Add-Directory", StringComparison.Ordinal);
        Assert.True(at >= 0, "build-windows.ps1 no longer harvests the publish tree into Files.wxs.");
        string harvester = win[at..win.IndexOf("function Get-PeMachine", at, StringComparison.Ordinal)];
        Assert.Contains("-File -Force", harvester, StringComparison.Ordinal);
        Assert.Contains("-Directory -Force", harvester, StringComparison.Ordinal);

        // macOS: the whole publish tree into Contents/MacOS, which is also where the app looks.
        foreach (string bundle in new[] { "bundleForMacOS.sh", "bundleForHarmonicaMacOS.sh", "bundleForWBondMacOS.sh" })
            Assert.Contains("${PUBLISH_DIR}/.", File.ReadAllText(RepoFile("src", "Ui", bundle)),
                            StringComparison.Ordinal);

        // Linux: the .deb maps the publish root, the tarball copies it.
        string linux = File.ReadAllText(RepoFile("packaging", "linux", "build-linux.sh"));
        Assert.Contains("\"${PUBLISH}/=/opt/circuitrf/\"", linux, StringComparison.Ordinal);
        Assert.Contains("cp -a \"${PUBLISH}/.\"", linux, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Every platform script runs the command line out of what it just built</b>
    /// (brief-automation-13-installed-cli.md). 1.0.0-beta.1 through beta.32 shipped with no command
    /// line at all while every CLI gate was green, because they all launched src/Cli/bin and nothing
    /// ever ran what an installer holds. tools/CliSmoke is that check; this is what keeps a script from
    /// losing it quietly — a step one script drops is the defect the brief exists to remove.
    /// </summary>
    [Theory]
    [InlineData("packaging/macos/build-macos.sh", "tools/CliSmoke")]
    [InlineData("packaging/linux/build-linux.sh", "tools/CliSmoke")]
    [InlineData("packaging/windows/build-windows.ps1", "tools\\CliSmoke")]
    public void EachPlatformScript_SmokeTestsTheCommandLineItBuilt(string script, string tool)
    {
        string text = File.ReadAllText(RepoFile(script.Split('/')));
        Assert.Contains(tool, text, StringComparison.Ordinal);
        // Run, not merely built: the script passes the built application and the VERSION to it.
        Assert.Matches(@"dotnet\s+""?\$\{?(SMOKE_DLL|smokeDll)\}?""?\s", text);
    }

    /// <summary>
    /// <b>Opening a document by double-click is untouched by the executable becoming the CLI.</b>
    /// Every Windows open verb targets the .EXE — never circuitRF.com, which is console-subsystem and
    /// would flash a console window on every double-click — and the Linux desktop entry still hands
    /// the application full paths (%F), which CliEntry.IsVerb never mistakes for a verb.
    /// </summary>
    [Fact]
    public void DoubleClickRoutes_StillTargetTheApplicationExe()
    {
        string wxs = File.ReadAllText(RepoFile("packaging", "windows", "circuitRF.wxs"));
        var verbs = System.Text.RegularExpressions.Regex.Matches(wxs, "<Verb [^>]*>");
        Assert.NotEmpty(verbs);
        foreach (System.Text.RegularExpressions.Match v in verbs)
            Assert.Contains("TargetFile=\"CircuitRfExe\"", v.Value, StringComparison.Ordinal);
        Assert.Contains("<File Id=\"CircuitRfCom\" Name=\"circuitRF.com\"", wxs, StringComparison.Ordinal);

        string exec = File.ReadAllLines(RepoFile("packaging", "linux", "circuitrf.desktop"))
                          .Single(l => l.StartsWith("Exec=", StringComparison.Ordinal));
        Assert.EndsWith(" %F", exec.TrimEnd(), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>circuitRF.com is the stub built for the CONSOLE subsystem, and both builders say so.</b>
    /// A GUI-subsystem .com is the one wrong answer nobody would see: a typed <c>circuitrf check .</c>
    /// would get no console and the shell would print its prompt before any output. The stub builders
    /// already read the subsystem back out of the PE for the .exe; this holds them to demanding 3 for
    /// the .com as well.
    /// </summary>
    [Theory]
    [InlineData("packaging/windows/stub/build-stub.sh", "want_subsys=0003", "-Wl,--subsystem,console")]
    [InlineData("packaging/windows/stub/build-stub.ps1", "$wantSubsystem = 3", "-Wl,--subsystem,console")]
    public void StubBuilders_DemandTheConsoleSubsystemForTheCom(string script, string demand, string flag)
    {
        string text = File.ReadAllText(RepoFile(script.Split('/')));
        Assert.Contains(demand, text, StringComparison.Ordinal);
        Assert.Contains(flag, text, StringComparison.Ordinal);
        Assert.Contains("CRF_CONSOLE", text, StringComparison.Ordinal);
    }
}
