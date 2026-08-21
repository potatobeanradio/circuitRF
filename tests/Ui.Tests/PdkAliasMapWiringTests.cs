using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Pdk;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The alias map — circuitRF's own record of internal nodes a compiled model never drives — has to
/// reach the worker on every platform the manifest describes, or the fix works on whichever machine
/// it was tested on and silently does nothing on the others.
///
/// <para><b>The failure this exists for.</b> A node the model writes no equation for still gets an
/// unknown minted for it; the row is held up by <c>gmin</c> alone and the bias ramp stalls rather than
/// failing. Measured: 279,127 iterations at residual 35.6 without the map, 5 iterations
/// at 7.6e-12 with it. Nothing about the symptom points at a missing command-line argument.</para>
///
/// <para>These fixtures name no vendor and no part. A model library is recognised by the entry points
/// our own worker calls — a plain byte scan — so a file containing that name is all it takes.</para>
/// </summary>
[Collection(PdkToolsDirectoryCollection.Name)]
public sealed class PdkAliasMapWiringTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "crf-alias-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>Two levels below the scratch directory on purpose — library discovery widens outward
    /// when the narrow search finds nothing, and a root sitting directly in the system temp folder
    /// lets that walk reach another concurrently-running test's fixtures.</summary>
    private string Root         => Path.Combine(_scratch, "delivery", "root");
    private string KitDir       => Path.Combine(Root, "kit");
    private string NetlistDir   => Path.Combine(KitDir, "circuit", "models");
    private string WorkspaceDir => Path.Combine(Root, "ws");

    private const string DeviceType = "CRF_ALIAS_V1";

    public PdkAliasMapWiringTests()
    {
        Directory.CreateDirectory(NetlistDir);
        File.WriteAllText(Path.Combine(NetlistDir, "kit.net"), $"""
            define PART_A ( g d s )
              {DeviceType}:M1  g d s
            end PART_A
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best effort */ }
    }

    private static byte[] Marker() =>
        Encoding.ASCII.GetBytes(DeviceLibraryDiscovery.Profiles[0].ExportPrefix + DeviceType + "\0");

    /// <summary>A Linux build, recognised by the ELF magic and the exported entry point.</summary>
    private string WriteLinuxLibrary()
    {
        string abs = Path.Combine(KitDir, "linux_x86_64", "models.so");
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllBytes(abs, [0x7F, (byte)'E', (byte)'L', (byte)'F', .. Marker()]);
        return abs;
    }

    /// <summary>A Windows build of the SAME library — same name, different container format, which is
    /// what makes the manifest describe three platform entries from one kit.</summary>
    private string WriteWindowsLibrary()
    {
        string abs = Path.Combine(KitDir, "win32_64", "models.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllBytes(abs, [(byte)'M', (byte)'Z', .. Marker()]);
        return abs;
    }

    /// <summary>
    /// The worker in a folder of its OWN under the kit, not the kit's root — so "the kit's folder"
    /// and "the worker's folder" are two different places and the search order between them can be
    /// tested at all.
    /// </summary>
    private string WriteWorkerBesideKit()
    {
        string dir = Path.Combine(KitDir, "bin");
        Directory.CreateDirectory(dir);
        string abs = Path.Combine(dir, DeviceLibraryDiscovery.Profiles[0].Worker);
        var elf = new byte[64];
        elf[0] = 0x7F; elf[1] = (byte)'E'; elf[2] = (byte)'L'; elf[3] = (byte)'F';
        File.WriteAllBytes(abs, elf);
        return abs;
    }

    private string WriteAliasMapBesideWorker()
    {
        string dir = Path.Combine(KitDir, "bin");
        Directory.CreateDirectory(dir);
        string abs = Path.Combine(dir, DeviceLibraryDiscovery.AliasMapFileName);
        File.WriteAllText(abs, $$"""{ "{{DeviceType}}": { "6": 5 } }""");
        return abs;
    }

    /// <summary>
    /// The map dropped beside the kit itself — where a library-specific alias belongs, since circuitRF
    /// cannot derive it and it describes exactly one kit's models.
    /// </summary>
    private string WriteAliasMapBesideKit()
    {
        string abs = Path.Combine(KitDir, DeviceLibraryDiscovery.AliasMapFileName);
        File.WriteAllText(abs, $$"""{ "{{DeviceType}}": { "7": 4 } }""");
        return abs;
    }

    private static string ReadRepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root.");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }

    private static PdkImportReport Report(string kitDir)
    {
        var report = new PdkImportReport { RootPath = kitDir, KitName = "SampleKit" };
        report.Add(new PdkAsset(Path.Combine("circuit", "models", "kit.net"), PdkAssetKind.Netlist,
                                PdkAssetSupport.Supported, "kit netlist"));
        report.Parts.Add(new PdkPart("PART_A", "Part A"));
        return report;
    }

    private JsonNode Import(JsonNode? recorded = null)
    {
        WriteWorkerBesideKit();
        WriteLinuxLibrary();
        WriteWindowsLibrary();

        var outcome = PdkPartInstaller.Install(Report(KitDir), recorded);
        Assert.NotNull(outcome.Settings);
        return outcome.Settings!;
    }

    private static string[] Arguments(JsonNode manifest, string platform)
        => [.. manifest["workers"]!.AsArray()
                .Single(w => w!["platform"]!.GetValue<string>() == platform)!["arguments"]!
                .AsArray().Select(a => a!.GetValue<string>())];

    // ── The kit's own folder is where a library-specific alias belongs ─────────

    /// <summary>
    /// Which node a degenerate node follows is definition data about one library's models, so it lives
    /// beside that kit rather than in circuitRF's tree. This is the path that makes dropping it there
    /// work at all.
    /// </summary>
    [Fact]
    public void AnAliasMapBesideTheKit_IsNamedByEveryPlatformEntry()
    {
        string aliasMap = WriteAliasMapBesideKit();
        var manifest = Import();

        Assert.Equal(aliasMap, Arguments(manifest, "linux-x64")[1]);
        Assert.Equal(aliasMap, Arguments(manifest, "win-x64")[1]);

        // The guest reaches it through a share. Here that is the kit's DATA tree, which is mounted at
        // its own absolute path — so the map's host path is already true inside the guest and must be
        // left exactly as it is. Rewriting it to /mnt/<tag>/… would name a place nothing was mounted.
        var args = Arguments(manifest, "osx");
        Assert.Equal(aliasMap, args[^1]);
        Assert.Contains(args, a => a.StartsWith("kitdata=" + KitDir, StringComparison.Ordinal));
    }

    /// <summary>
    /// The kit's own folder WINS. The other two locations are shared by every kit, so if either could
    /// shadow this one the user's dropped file would be silently ignored — and the symptom is the
    /// grinding bias ramp the map exists to fix, never anything naming a shadowed file.
    /// </summary>
    [Fact]
    public void TheKitsOwnFolderBeatsAMapSittingBesideTheWorker()
    {
        string besideWorker = WriteAliasMapBesideWorker();
        string inKitFolder  = WriteAliasMapBesideKit();

        var manifest = Import();

        Assert.Equal(inKitFolder, Arguments(manifest, "linux-x64")[1]);
        Assert.NotEqual(besideWorker, Arguments(manifest, "linux-x64")[1]);
    }

    /// <summary>
    /// The shipped map declares no families. Data belongs beside the library it applies to, at
    /// <c>&lt;workspace&gt;/pdk/&lt;kit&gt;/alias-map.json</c>, which the override test above proves
    /// is found first.
    /// </summary>
    [Fact]
    public void TheShippedAliasMap_DeclaresNoFamilies()
    {
        var shipped = JsonNode.Parse(ReadRepoFile("tools/senior-worker/alias-map.json"))!.AsObject();

        var families = shipped.Where(kv => kv.Value is JsonObject).Select(kv => kv.Key).ToList();
        Assert.True(families.Count == 0,
            $"The shipped alias map declares {families.Count} family entr{(families.Count == 1 ? "y" : "ies")} " +
            $"({string.Join(", ", families)}). This file ships with none; data belongs at " +
            "<workspace>/pdk/<kit>/alias-map.json, beside the library it applies to.");
    }

    /// <summary>
    /// The file documents its own format, so an entry can be written without reading the worker
    /// source. Kept as a test because the file ships empty and the note is the only thing in it.
    /// </summary>
    [Fact]
    public void TheShippedAliasMap_DocumentsItsFormat()
    {
        var note = string.Join(' ',
            JsonNode.Parse(ReadRepoFile("tools/senior-worker/alias-map.json"))!
                    .AsObject()["_note"]!.AsArray().Select(n => n!.GetValue<string>()));

        Assert.Contains("FAMILY_NAME", note);
        Assert.Contains("<workspace>/pdk/<kit>/alias-map.json", note);
    }

    [Fact]
    public void AnAliasMapBesideTheWorker_IsNamedByEveryPlatformEntry()
    {
        string aliasMap = WriteAliasMapBesideWorker();
        var manifest = Import();

        // The two native hosts run the worker directly and name the map by its real path.
        Assert.Equal(2, Arguments(manifest, "linux-x64").Length);
        Assert.Equal(aliasMap, Arguments(manifest, "linux-x64")[1]);

        Assert.Equal(2, Arguments(manifest, "win-x64").Length);
        Assert.Equal(aliasMap, Arguments(manifest, "win-x64")[1]);

        // macOS runs the worker in the VM, so the map arrives through the share it already sits in.
        Assert.Contains(VmHostArguments.GuestPath("crfw", DeviceLibraryDiscovery.AliasMapFileName),
                        Arguments(manifest, "osx"));
    }

    [Fact]
    public void WithNoAliasMapPresent_NoEntryNamesOne()
    {
        // Absence is a meaningful state, not a gap: every node stays an ordinary unknown, which is the
        // correct default for a model with nothing to declare.
        var manifest = Import();

        Assert.Single(Arguments(manifest, "linux-x64"));
        Assert.Single(Arguments(manifest, "win-x64"));
        Assert.DoesNotContain(Arguments(manifest, "osx"),
                              a => a.Contains(DeviceLibraryDiscovery.AliasMapFileName, StringComparison.Ordinal));
    }

    [Fact]
    public void TheOsxEntry_NamesTheAliasMapInTheGuest_NeverItsHostPath()
    {
        // A host path inside the guest's argv is the failure this share exists to prevent: the file is
        // plainly there on the Mac and the worker reports it missing.
        string aliasMap = WriteAliasMapBesideWorker();
        var args = Arguments(Import(), "osx");

        int marker = Array.IndexOf(args, "--");
        Assert.True(marker >= 0, "the VM host's argv has no '--' separating its own flags from the guest's");

        Assert.DoesNotContain(args.Skip(marker + 1), a => a == aliasMap);
        Assert.Contains(VmHostArguments.GuestPath("crfw", DeviceLibraryDiscovery.AliasMapFileName),
                        args.Skip(marker + 1));
    }

    [Fact]
    public void AWorkerFoundBesideAKit_StillGetsCircuitRfsOwnAliasMap_ThroughItsOwnShare()
    {
        // A worker sitting beside a kit is found on purpose, so a user holding one is not blocked
        // until a release ships — but that copy has no reason to carry circuitRF's own map. Looking
        // only beside the worker drops the map for exactly that user, and the symptom is a bias ramp
        // that grinds rather than anything naming a missing file.
        string tools = Path.Combine(_scratch, "crf-tools");
        Directory.CreateDirectory(tools);
        string aliasMap = Path.Combine(tools, DeviceLibraryDiscovery.AliasMapFileName);
        File.WriteAllText(aliasMap, $$"""{ "{{DeviceType}}": { "6": 5 } }""");

        // Deliberately holds NO worker: the one found is still the kit's own, which is the case
        // under test. Serialized against every other PDK import by this class's collection.
        string previous = DeviceWorkerManifest.ToolsDirectory;
        DeviceWorkerManifest.ToolsDirectory = tools;
        try
        {
            var manifest = Import();

            Assert.Equal(aliasMap, Arguments(manifest, "linux-x64")[1]);
            Assert.Equal(aliasMap, Arguments(manifest, "win-x64")[1]);

            // The map is NOT under the worker's folder here, so the guest reaches it through a share
            // of its own — naming it under crfw would point at a place nothing was mounted.
            var args = Arguments(manifest, "osx");
            Assert.Contains(args, a => a.EndsWith("/" + DeviceLibraryDiscovery.AliasMapFileName, StringComparison.Ordinal)
                                    && a.StartsWith("/mnt/", StringComparison.Ordinal)
                                    && a != VmHostArguments.GuestPath("crfw", DeviceLibraryDiscovery.AliasMapFileName));
            Assert.Contains(args, a => a.Contains("=" + tools, StringComparison.Ordinal));
        }
        finally { DeviceWorkerManifest.ToolsDirectory = previous; }
    }

    [Fact]
    public void SettingsRecordedBeforeTheAliasMapExisted_AreRedone_NotReplayedRunnableAndWrong()
    {
        // They name programs that all still exist, so nothing else would ever replace them — and they
        // are missing the one argument that decides whether the bias ramp converges at all.
        string aliasMap = WriteAliasMapBesideWorker();

        var stale = Import();
        stale["generatedFormat"] = 2;
        foreach (var w in stale["workers"]!.AsArray())
            w!["arguments"] = new JsonArray(w["arguments"]!.AsArray()[0]!.GetValue<string>());

        var settings = Import(stale);

        Assert.Equal(aliasMap, Arguments(settings, "linux-x64")[1]);
    }

    [Fact]
    public void SettingsAKitShipped_AreNeverRedone_EvenWhenTheyLookOld()
    {
        // Only circuitRF's own derivation is reconsidered. A kit's file — or one a user edited — is
        // theirs, and quietly replacing it would lose their work.
        WriteAliasMapBesideWorker();

        var theirs = JsonNode.Parse("""
            { "provider": "SampleKit",
              "workers": [ { "platform": "linux-x64", "command": "their-worker", "arguments": [] } ] }
            """)!;

        var settings = Import(theirs);

        Assert.Equal("their-worker",
                     settings["workers"]!.AsArray()[0]!["command"]!.GetValue<string>());
    }
}
