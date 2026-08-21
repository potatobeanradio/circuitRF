using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Pdk;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The worker program is looked for PER TARGET, and a target whose worker is not on this machine
/// still gets its manifest entry.
///
/// <para><b>What went wrong.</b> A kit imported on Windows reported that the program evaluating its
/// devices could not be found, and the import produced no settings at all — while the identical kit
/// imported on macOS worked. Two independent causes, both of which only bite on Windows:</para>
///
/// <list type="number">
/// <item>the search near the kit looked for the worker under ONE spelling of its name, the one with
/// no <c>.exe</c>, so a Windows worker sitting right beside the kit was never even a candidate;</item>
/// <item>every candidate was then required to be an ELF, so a Windows worker that HAD been found
/// would have been discarded for being a Windows program.</item>
/// </list>
///
/// <para>And one consequence: because a single worker had to be found before any entry could be
/// written, not finding it abandoned the whole manifest — including the <c>win-x64</c> entry, whose
/// command was always meant to be a bare name resolved on the machine that runs it.</para>
///
/// <para>These fixtures name no vendor and no part. A "library" here is a file carrying the right
/// magic bytes and the entry-point name OUR OWN worker calls, which is all the real scan reads.</para>
/// </summary>
[Collection(PdkToolsDirectoryCollection.Name)]
public sealed class PdkWorkerPerTargetDiscoveryTests : IDisposable
{
    private readonly string _scratch =
        Path.Combine(Path.GetTempPath(), "crf-wpt-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly string _previousTools = DeviceWorkerManifest.ToolsDirectory;

    /// <summary>
    /// Two levels below the scratch directory ON PURPOSE — discovery widens outward when the
    /// narrower search finds nothing, and a root sitting directly in the system temp folder lets the
    /// walk reach another concurrently-running test's fixtures.
    /// </summary>
    private string _root => Path.Combine(_scratch, "delivery", "root");

    private string KitDir   => Path.Combine(_root, "kit");
    private string ToolsDir => Path.Combine(_root, "tools");

    private const string DeviceType = "CRF_TEST_V1";

    public PdkWorkerPerTargetDiscoveryTests()
    {
        Directory.CreateDirectory(KitDir);
        Directory.CreateDirectory(ToolsDir);

        // Pointed at an EMPTY folder rather than left at the test binary's own, so what the machine
        // running this happens to have installed cannot decide the answer.
        DeviceWorkerManifest.ToolsDirectory = ToolsDir;
    }

    public void Dispose()
    {
        DeviceWorkerManifest.ToolsDirectory = _previousTools;
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best effort */ }
    }

    // ── fixture ───────────────────────────────────────────────────────────────

    private static readonly byte[] ElfMagic = [0x7F, (byte)'E', (byte)'L', (byte)'F'];
    private static readonly byte[] PeMagic  = [(byte)'M', (byte)'Z', 0x90, 0x00];

    private static string Worker => DeviceLibraryDiscovery.Profiles[0].Worker;

    private void WriteKitNetlist()
    {
        string dir = Path.Combine(KitDir, "netlists");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "kit.net"), $"""
            define PART_A ( g d s )
              {DeviceType}:M1  g d s  W=1e-4
            end PART_A
            """);
    }

    private string WriteLibrary(string relativePath, byte[] magic)
    {
        string abs = Path.Combine(KitDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);

        byte[] marker = System.Text.Encoding.ASCII.GetBytes(
            DeviceLibraryDiscovery.Profiles[0].ExportPrefix + DeviceType + "\0");
        File.WriteAllBytes(abs, [.. magic, .. marker]);
        return abs;
    }

    private string WriteLinuxLibrary()   => WriteLibrary(Path.Combine("linux_x86_64", "models.so"), ElfMagic);
    private string WriteWindowsLibrary() => WriteLibrary(Path.Combine("win32_64", "models.dll"), PeMagic);

    /// <summary>A stand-in worker of one platform's format, under one spelling of the name.</summary>
    private string WriteWorker(string directory, string fileName, byte[] magic)
    {
        Directory.CreateDirectory(directory);
        string abs = Path.Combine(directory, fileName);
        File.WriteAllBytes(abs, [.. magic, .. new byte[60]]);
        return abs;
    }

    private (JsonNode? Manifest, PdkPartInstaller.InstallOutcome Outcome) Import()
    {
        var report = new PdkImportReport { RootPath = KitDir, KitName = "SampleKit" };
        report.Add(new PdkAsset("netlists/kit.net", PdkAssetKind.Netlist,
                                PdkAssetSupport.Supported, "kit netlist"));
        report.Parts.Add(new PdkPart("PART_A", "Part A"));

        var outcome = PdkPartInstaller.Install(report);
        return (outcome.Settings, outcome);
    }

    private static string[] Platforms(JsonNode manifest)
        => [.. manifest["workers"]!.AsArray().Select(w => w!["platform"]!.GetValue<string>())];

    private static JsonNode Entry(JsonNode manifest, string platform)
        => manifest["workers"]!.AsArray().Single(w => w!["platform"]!.GetValue<string>() == platform)!;

    // ── the search itself ─────────────────────────────────────────────────────

    [Fact]
    public void AWindowsWorkerBesideTheKit_IsFound_ThoughItsNameCarriesAnExeSuffix()
    {
        // The near-the-kit search used to look for exactly one spelling of the name — the one with
        // no extension — so the Windows worker was never a candidate on the platform that has it.
        string exe = WriteWorker(KitDir, Worker + ".exe", PeMagic);

        Assert.Equal(exe, DeviceLibraryDiscovery.FindWorker(
            DeviceLibraryDiscovery.Profiles[0], KitDir,
            format: DeviceLibraryDiscovery.LibraryFormat.Pe));
    }

    [Fact]
    public void TheFormatAskedFor_IsWhatDecides_NotTheNameOrTheExtension()
    {
        // Two files, right names, wrong platforms for what each search wants. The magic is the only
        // property a naming convention cannot get wrong, so it is the only one consulted.
        WriteWorker(KitDir, Worker,          PeMagic);    // named like Linux, actually Windows
        WriteWorker(KitDir, Worker + ".exe", ElfMagic);   // named like Windows, actually Linux

        var profile = DeviceLibraryDiscovery.Profiles[0];

        Assert.EndsWith(Worker, DeviceLibraryDiscovery.FindWorker(
            profile, KitDir, format: DeviceLibraryDiscovery.LibraryFormat.Pe) ?? "", StringComparison.Ordinal);
        Assert.EndsWith(".exe", DeviceLibraryDiscovery.FindWorker(
            profile, KitDir, format: DeviceLibraryDiscovery.LibraryFormat.Elf) ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void AWorkerOfTheWrongPlatform_IsNotOfferedAsTheRightOne()
    {
        // The alternative — taking whatever had the right name — hands a Linux binary to Windows or
        // a Windows stub to the Linux VM, and both fail at Run complaining about a program that
        // plainly IS there.
        WriteWorker(ToolsDir, Worker, ElfMagic);

        Assert.Null(DeviceLibraryDiscovery.FindWorker(
            DeviceLibraryDiscovery.Profiles[0], KitDir,
            format: DeviceLibraryDiscovery.LibraryFormat.Pe));
    }

    // ── what that means for the manifest ──────────────────────────────────────

    [Fact]
    public void AWindowsWorkerBesideTheKit_BecomesTheWindowsCommand()
    {
        WriteKitNetlist();
        string dll = WriteWindowsLibrary();
        string exe = WriteWorker(KitDir, Worker + ".exe", PeMagic);

        var (manifest, _) = Import();
        Assert.NotNull(manifest);

        var win = Entry(manifest!, "win-x64");
        Assert.Equal(exe, win["command"]!.GetValue<string>());
        Assert.Equal(dll, win["arguments"]!.AsArray()[0]!.GetValue<string>());
    }

    [Fact]
    public void NoWorkerAnywhere_StillWritesTheNativeEntries_ByBareName()
    {
        // This is the reported failure: nothing found, so nothing written, and a kit whose parts
        // placed fine could not be simulated at all. The worker is circuitRF's own component and
        // ships in its tools folder on the machine that runs the design — a bare name is a promise
        // that machine keeps, and it is the same promise the win-x64 entry has always been written
        // with when the import happened somewhere else.
        WriteKitNetlist();
        WriteLinuxLibrary();
        WriteWindowsLibrary();

        var (manifest, outcome) = Import();

        Assert.NotNull(manifest);
        Assert.Contains("win-x64",   Platforms(manifest!));
        Assert.Contains("linux-x64", Platforms(manifest!));

        Assert.Equal(Worker + ".exe", Entry(manifest!, "win-x64")["command"]!.GetValue<string>());
        Assert.Equal(Worker,          Entry(manifest!, "linux-x64")["command"]!.GetValue<string>());

        Assert.DoesNotContain(outcome.Diagnostics,
            d => d.Contains("The kit's parts still build", StringComparison.Ordinal));
    }

    [Fact]
    public void WithNoLinuxWorker_TheMacOsEntryIsOmitted_AndSaidRatherThanLeftBlank()
    {
        // macOS is the one platform that cannot fall back to a bare name: the worker is shared into
        // the VM from a host folder, so the file itself has to be here. Omitting the entry is right;
        // omitting it silently is not — on macOS that is the only entry that can run.
        WriteKitNetlist();
        WriteLinuxLibrary();

        var (manifest, outcome) = Import();

        Assert.NotNull(manifest);
        Assert.DoesNotContain("osx", Platforms(manifest!));
        Assert.Contains(outcome.Diagnostics, d => d.Contains("no macOS entry", StringComparison.Ordinal));
    }

    [Fact]
    public void WithALinuxWorkerPresent_TheMacOsEntrySharesItsRealFolder()
    {
        // The counterpart of the above: when the file IS here, the osx entry is written and names
        // the host folder the VM mounts.
        WriteKitNetlist();
        WriteLinuxLibrary();
        string elf = WriteWorker(ToolsDir, Worker, ElfMagic);

        var (manifest, _) = Import();
        Assert.NotNull(manifest);

        var args = Entry(manifest!, "osx")["arguments"]!.AsArray()
                       .Select(a => a!.GetValue<string>()).ToArray();

        Assert.Contains(args, a => a.Contains(Path.GetDirectoryName(elf)!, StringComparison.Ordinal));
    }

    [Fact]
    public void EachPlatformsEntryNamesItsOwnWorker_WhenBothAreHere()
    {
        // Both builds present, both workers present: neither entry may borrow the other's program.
        WriteKitNetlist();
        WriteLinuxLibrary();
        WriteWindowsLibrary();

        string elf = WriteWorker(ToolsDir, Worker,          ElfMagic);
        string exe = WriteWorker(KitDir,   Worker + ".exe", PeMagic);

        var (manifest, _) = Import();
        Assert.NotNull(manifest);

        Assert.Equal(elf, Entry(manifest!, "linux-x64")["command"]!.GetValue<string>());
        Assert.Equal(exe, Entry(manifest!, "win-x64")["command"]!.GetValue<string>());
    }

    // ── sharing a workspace between platforms ─────────────────────────────────

    [Fact]
    public void SettingsWrittenOnAnotherPlatform_AreRedoneHere_NotReplayed()
    {
        // A workspace is shared, and the colleague opens it on a different operating system. What
        // circuitRF worked out over there names files over there — replaying it is worse than having
        // recorded nothing, because every part places and only Run fails.
        WriteKitNetlist();
        string dll = WriteWindowsLibrary();
        string so  = WriteLinuxLibrary();
        WriteWorker(ToolsDir, Worker,          ElfMagic);
        WriteWorker(ToolsDir, Worker + ".exe", PeMagic);

        var (mine, _) = Import();
        Assert.NotNull(mine);

        // The same settings as circuitRF writes, with THIS machine's entry pointed at a library that
        // is not here — which is exactly the shape a workspace from another platform arrives in.
        var foreign = mine!.DeepClone();
        string here = DeviceWorkerManifest.CurrentOs() switch
        {
            "win" => "win-x64",
            "osx" => "osx",
            _     => "linux-x64",
        };
        var entry = foreign["workers"]!.AsArray()
                        .Single(w => w!["platform"]!.GetValue<string>() == here)!;

        if (here == "osx")
        {
            // The macOS entry names host folders it shares into the VM; point one at a folder that
            // is not here rather than at a guest path, which is supposed not to exist.
            var args = entry["arguments"]!.AsArray();
            for (int i = 0; i < args.Count; i++)
                if (args[i]!.GetValue<string>().StartsWith("crfw=", StringComparison.Ordinal))
                    args[i] = VmHostArguments.ShareValue("crfw", Path.Combine(_scratch, "gone"));
        }
        else
        {
            entry["arguments"]!.AsArray()[0] = Path.Combine(_scratch, "gone", "models.bin");
        }

        var report = new PdkImportReport { RootPath = KitDir, KitName = "SampleKit" };
        report.Add(new PdkAsset("netlists/kit.net", PdkAssetKind.Netlist,
                                PdkAssetSupport.Supported, "kit netlist"));
        report.Parts.Add(new PdkPart("PART_A", "Part A"));

        var settled = PdkPartInstaller.Install(report, foreign).Settings;

        Assert.NotNull(settled);
        Assert.True(JsonNode.DeepEquals(mine, settled),
            "circuitRF's own settings should have been re-derived for this machine, not replayed.");
        Assert.Contains(new[] { so, dll },
            p => settled!["workers"]!.AsArray()
                     .Any(w => w!["arguments"]!.AsArray().Any(a => a!.GetValue<string>() == p)));
    }

    [Fact]
    public void SettingsDescribingNoPlatformThisMachineRuns_AreRedone()
    {
        // The bluntest form of the same thing: an entry list this host is simply not in.
        WriteKitNetlist();
        WriteLinuxLibrary();
        WriteWindowsLibrary();

        var foreign = new JsonObject
        {
            ["provider"]        = "SampleKit",
            ["baseDirectory"]   = KitDir,
            ["generatedBy"]     = "circuitRF",
            ["generatedFormat"] = 5,
            ["workers"]         = new JsonArray(new JsonObject
            {
                ["platform"]  = "some-platform-that-is-not-here",
                ["command"]   = Worker,
                ["arguments"] = new JsonArray(JsonValue.Create("models.bin")!),
            }),
        };

        var report = new PdkImportReport { RootPath = KitDir, KitName = "SampleKit" };
        report.Add(new PdkAsset("netlists/kit.net", PdkAssetKind.Netlist,
                                PdkAssetSupport.Supported, "kit netlist"));
        report.Parts.Add(new PdkPart("PART_A", "Part A"));

        var settled = PdkPartInstaller.Install(report, foreign).Settings;

        Assert.NotNull(settled);
        Assert.DoesNotContain("some-platform-that-is-not-here", Platforms(settled!));
    }

    [Fact]
    public void SettingsTheKitOrTheUserWrote_AreNeverRedone_EvenWhenTheyDoNotResolveHere()
    {
        // Only circuitRF's own working-out is reconsidered. Somebody else's settings are theirs,
        // and silently replacing them loses their work — even when they are broken.
        WriteKitNetlist();
        WriteLinuxLibrary();

        var theirs = new JsonObject
        {
            ["provider"]  = "SampleKit",
            ["workers"]   = new JsonArray(new JsonObject
            {
                ["platform"]  = "any",
                ["command"]   = Worker,
                ["arguments"] = new JsonArray(JsonValue.Create(Path.Combine(_scratch, "gone", "x.bin"))!),
            }),
        };

        var report = new PdkImportReport { RootPath = KitDir, KitName = "SampleKit" };
        report.Add(new PdkAsset("netlists/kit.net", PdkAssetKind.Netlist,
                                PdkAssetSupport.Supported, "kit netlist"));
        report.Parts.Add(new PdkPart("PART_A", "Part A"));

        var settled = PdkPartInstaller.Install(report, theirs).Settings;

        Assert.NotNull(settled);
        Assert.Equal(["any"], Platforms(settled!));
    }

    [Fact]
    public void SettingsThatStillResolveHere_AreReplayedUnchanged()
    {
        // The recording is there to be used: an ordinary reopen on the machine that wrote them must
        // not pay for the byte scan again.
        WriteKitNetlist();
        WriteLinuxLibrary();
        WriteWindowsLibrary();
        WriteWorker(ToolsDir, Worker,          ElfMagic);
        WriteWorker(ToolsDir, Worker + ".exe", PeMagic);

        var (mine, _) = Import();
        Assert.NotNull(mine);

        var report = new PdkImportReport { RootPath = KitDir, KitName = "SampleKit" };
        report.Add(new PdkAsset("netlists/kit.net", PdkAssetKind.Netlist,
                                PdkAssetSupport.Supported, "kit netlist"));
        report.Parts.Add(new PdkPart("PART_A", "Part A"));

        var replayed = PdkPartInstaller.Install(report, mine).Settings;

        Assert.Same(mine, replayed);
    }
}
