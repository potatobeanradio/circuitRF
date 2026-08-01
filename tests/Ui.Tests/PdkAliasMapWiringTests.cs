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
    private string ManifestPath => Path.Combine(WorkspaceDir, "pdk", "SampleKit", DeviceWorkerManifest.FileName);

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

    private string WriteWorkerBesideKit()
    {
        string abs = Path.Combine(KitDir, DeviceLibraryDiscovery.Profiles[0].Worker);
        var elf = new byte[64];
        elf[0] = 0x7F; elf[1] = (byte)'E'; elf[2] = (byte)'L'; elf[3] = (byte)'F';
        File.WriteAllBytes(abs, elf);
        return abs;
    }

    private string WriteAliasMapBesideWorker()
    {
        string abs = Path.Combine(KitDir, DeviceLibraryDiscovery.AliasMapFileName);
        File.WriteAllText(abs, $$"""{ "{{DeviceType}}": { "6": 5 } }""");
        return abs;
    }

    /// <summary>
    /// The map dropped into the kit's own workspace folder, beside its <c>device-provider.json</c> —
    /// where a library-specific alias belongs, since circuitRF cannot derive it and it serves exactly
    /// one kit.
    /// </summary>
    private string WriteAliasMapInKitWorkspaceFolder()
    {
        string dir = Path.Combine(WorkspaceDir, "pdk", "SampleKit");
        Directory.CreateDirectory(dir);
        string abs = Path.Combine(dir, DeviceLibraryDiscovery.AliasMapFileName);
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

    private JsonNode Import()
    {
        WriteWorkerBesideKit();
        WriteLinuxLibrary();
        WriteWindowsLibrary();

        var report = new PdkImportReport { RootPath = KitDir, KitName = "SampleKit" };
        report.Add(new PdkAsset(Path.Combine("circuit", "models", "kit.net"), PdkAssetKind.Netlist,
                                PdkAssetSupport.Supported, "kit netlist"));
        report.Parts.Add(new PdkPart("PART_A", "Part A"));

        PdkPartInstaller.Install(report, WorkspaceDir);
        return JsonNode.Parse(File.ReadAllText(ManifestPath))!;
    }

    private static string[] Arguments(JsonNode manifest, string platform)
        => [.. manifest["workers"]!.AsArray()
                .Single(w => w!["platform"]!.GetValue<string>() == platform)!["arguments"]!
                .AsArray().Select(a => a!.GetValue<string>())];

    // ── The kit's own folder is where a library-specific alias belongs ─────────

    /// <summary>
    /// Which node a degenerate node follows is definition data about one library's models, so it lives
    /// beside that kit's other declarations rather than in circuitRF's tree. This is the path that
    /// makes dropping it there work at all.
    /// </summary>
    [Fact]
    public void AnAliasMapInTheKitsOwnWorkspaceFolder_IsNamedByEveryPlatformEntry()
    {
        string aliasMap = WriteAliasMapInKitWorkspaceFolder();
        var manifest = Import();

        Assert.Equal(aliasMap, Arguments(manifest, "linux-x64")[1]);
        Assert.Equal(aliasMap, Arguments(manifest, "win-x64")[1]);

        // The map is not under the worker's folder here, so the guest reaches it through a share of
        // its own — naming it under crfw would point at a place nothing was mounted.
        var args = Arguments(manifest, "osx");
        Assert.Contains(args, a => a.EndsWith("/" + DeviceLibraryDiscovery.AliasMapFileName, StringComparison.Ordinal)
                                && a.StartsWith("/mnt/", StringComparison.Ordinal));
        Assert.Contains(args, a => a.Contains("=" + Path.GetDirectoryName(aliasMap), StringComparison.Ordinal));
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
        string inKitFolder  = WriteAliasMapInKitWorkspaceFolder();

        var manifest = Import();

        Assert.Equal(inKitFolder, Arguments(manifest, "linux-x64")[1]);
        Assert.NotEqual(besideWorker, Arguments(manifest, "linux-x64")[1]);
    }

    /// <summary>
    /// The shipped map still carries the entries that make a FRESH import converge, and emptying it
    /// is a slow, silent regression — this test exists because that was done once.
    ///
    /// <para>The kit-folder override above is what lets a per-kit entry live beside its own kit; it
    /// is not a reason to drop the fallback. circuitRF cannot derive which node a degenerate node
    /// follows, so with no entry anywhere the first import of that kit goes back to 279,127
    /// iterations at residual 35.6 instead of 5 at 7.6e-12 — and nothing about that symptom points
    /// at a missing file.</para>
    /// </summary>
    [Fact]
    public void TheShippedAliasMap_StillCarriesTheFallbackEntries_AFreshImportNeeds()
    {
        var shipped = JsonNode.Parse(ReadRepoFile("tools/senior-worker/alias-map.json"))!.AsObject();

        var families = shipped.Where(kv => kv.Value is JsonObject).ToList();
        Assert.True(families.Count > 0,
            "circuitRF's shipped alias map declares no families. A fresh import of a kit whose model " +
            "has undriven internal nodes will grind (279,127 iterations vs 5) with no message saying " +
            "why. Per-kit overrides belong in <workspace>/pdk/<kit>/alias-map.json — they do not " +
            "replace this fallback.");

        // Each entry must be node→master integer pairs, or the worker skips it and the fallback is
        // silently absent rather than merely wrong.
        foreach (var (name, value) in families)
            foreach (var (node, master) in value!.AsObject())
                Assert.True(int.TryParse(node, out _) && master is JsonValue v && v.TryGetValue<int>(out _),
                    $"'{name}' has a non-integer alias entry '{node}'; the worker will skip it.");
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
    public void AManifestWrittenBeforeTheAliasMapExisted_IsRedoneRatherThanLeftRunnableAndWrong()
    {
        // It names programs that all still exist, so nothing else would ever replace it — and it is
        // missing the one argument that decides whether the bias ramp converges at all.
        WriteAliasMapBesideWorker();
        Import();

        var stale = JsonNode.Parse(File.ReadAllText(ManifestPath))!;
        stale["generatedFormat"] = 2;
        File.WriteAllText(ManifestPath, stale.ToJsonString());

        PdkPartInstaller.LoadInstalled(WorkspaceDir);

        Assert.False(File.Exists(ManifestPath),
            "a manifest from before the alias map was wired was left in place — it runs, so nothing " +
            "else would replace it, and the model would keep solving equations it never stated");
    }
}
