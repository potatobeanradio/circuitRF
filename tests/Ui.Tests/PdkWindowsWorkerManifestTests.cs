using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Pdk;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// A synthesised <c>device-provider.json</c> names Windows again.
///
/// <para><b>Why this changed.</b> The <c>win-x64</c> entry used to be omitted deliberately, and the
/// reasoning was sound at the time: a Windows model IMPORTS its host callbacks from a NAMED MODULE,
/// an executable's exports are never consulted for that, and a manifest naming a way to run a device
/// that cannot work is worth less than no entry at all. What changed is that circuitRF now ships a
/// module — <c>crf-model-host.dll</c>, staged per user under whatever name the model itself asks for
/// — so the entry is a promise it can keep. There was no test asserting the old absence to update;
/// this one asserts the new presence.</para>
///
/// <para>These fixtures name no vendor and no part. The libraries are synthetic: discovery
/// recognises a model library by the entry points OUR OWN worker calls, which is a plain byte scan,
/// so a file containing that name is all it takes.</para>
/// </summary>
[Collection(PdkToolsDirectoryCollection.Name)]
public sealed class PdkWindowsWorkerManifestTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "crf-win-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>
    /// Two levels below the scratch directory ON PURPOSE — library discovery widens outward when the
    /// narrower search finds nothing, and a root sitting directly in the system temp folder lets the
    /// walk reach another concurrently-running test's fixtures.
    /// </summary>
    private string _root => Path.Combine(_scratch, "delivery", "root");

    private string KitDir       => Path.Combine(_root, "kit");
    private string WorkspaceDir => Path.Combine(_root, "ws");
    private string ManifestPath => Path.Combine(WorkspaceDir, "pdk", "SampleKit", DeviceWorkerManifest.FileName);

    private const string DeviceType = "CRF_TEST_V1";

    public PdkWindowsWorkerManifestTests() => Directory.CreateDirectory(KitDir);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best effort */ }
    }

    // ── fixture ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A kit whose netlist instantiates a device type it does not define — which is exactly how a
    /// compiled model is recognised: a cell instantiates primitives, sibling cells, or its own
    /// compiled models, so whatever is left over is the third.
    /// </summary>
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

    /// <summary>
    /// A file discovery recognises as serving <see cref="DeviceType"/> for one platform: the right
    /// magic bytes (which is what says WHICH platform) followed by the entry-point name the byte
    /// scan looks for.
    /// </summary>
    private string WriteModelLibrary(string relativePath, byte[] magic, byte[]? body = null)
    {
        string abs = Path.Combine(KitDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);

        byte[] marker = Encoding.ASCII.GetBytes(
            DeviceLibraryDiscovery.Profiles[0].ExportPrefix + DeviceType + "\0");
        File.WriteAllBytes(abs, body is null ? [.. magic, .. marker] : body);
        return abs;
    }

    private static readonly byte[] ElfMagic = [0x7F, (byte)'E', (byte)'L', (byte)'F'];
    private static readonly byte[] PeMagic  = [(byte)'M', (byte)'Z', 0x90, 0x00];

    private string WriteLinuxLibrary()   => WriteModelLibrary(Path.Combine("linux_x86_64", "models.so"), ElfMagic);
    private string WriteWindowsLibrary() => WriteModelLibrary(Path.Combine("win32_64", "models.dll"), PeMagic);

    /// <summary>The worker circuitRF ships, placed beside the kit. Only an ELF is accepted there —
    /// sharing a macOS binary into a Linux VM because it had the right name is a bad afternoon.</summary>
    private void WriteWorkerBesideKit()
    {
        var elf = new byte[64];
        elf[0] = 0x7F; elf[1] = (byte)'E'; elf[2] = (byte)'L'; elf[3] = (byte)'F';
        File.WriteAllBytes(Path.Combine(KitDir, DeviceLibraryDiscovery.Profiles[0].Worker), elf);
    }

    private (JsonNode Manifest, PdkPartInstaller.InstallOutcome Outcome) Import()
    {
        var report = new PdkImportReport { RootPath = KitDir, KitName = "SampleKit" };
        report.Add(new PdkAsset("netlists/kit.net", PdkAssetKind.Netlist,
                                PdkAssetSupport.Supported, "kit netlist"));
        report.Parts.Add(new PdkPart("PART_A", "Part A"));

        var outcome = PdkPartInstaller.Install(report, WorkspaceDir);
        return (JsonNode.Parse(File.ReadAllText(ManifestPath))!, outcome);
    }

    private static string[] Platforms(JsonNode manifest)
        => [.. manifest["workers"]!.AsArray().Select(w => w!["platform"]!.GetValue<string>())];

    private static JsonNode Entry(JsonNode manifest, string platform)
        => manifest["workers"]!.AsArray().Single(w => w!["platform"]!.GetValue<string>() == platform)!;

    // ── the entry is written again ────────────────────────────────────────────

    [Fact]
    public void AKitShippingBothBuilds_GetsAWindowsEntryNamingTheWindowsLibrary()
    {
        WriteKitNetlist();
        WriteWorkerBesideKit();
        string so  = WriteLinuxLibrary();
        string dll = WriteWindowsLibrary();

        var (manifest, _) = Import();

        Assert.Contains("win-x64", Platforms(manifest));

        var win = Entry(manifest, "win-x64");
        Assert.Equal(dll, win["arguments"]!.AsArray()[0]!.GetValue<string>());

        // The Linux entry still names the Linux build: one search per TARGET, not one for the host.
        Assert.Equal(so, Entry(manifest, "linux-x64")["arguments"]!.AsArray()[0]!.GetValue<string>());
    }

    [Fact]
    public void TheWindowsCommandIsABareExeName_ResolvedInTheToolsFolderWhereverItRuns()
    {
        // The import very often happens on a Mac or a Linux box, where the Windows worker simply
        // does not exist to point at — so an absolute path would be a path to nothing. A bare name
        // resolves through DeviceWorkerManifest's ToolsDirectory on the machine that runs it.
        WriteKitNetlist();
        WriteWorkerBesideKit();
        WriteLinuxLibrary();
        WriteWindowsLibrary();

        var (manifest, _) = Import();

        string command = Entry(manifest, "win-x64")["command"]!.GetValue<string>();
        Assert.Equal(DeviceLibraryDiscovery.Profiles[0].Worker + ".exe", command);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, command);
    }

    [Fact]
    public void AKitWithOnlyAWindowsBuild_IsNowSimulable_NotReportedAsUnsupported()
    {
        // This is the case the old suppression turned into a dead end: a kit shipping only a
        // Windows build produced no entries at all and a diagnostic saying so.
        WriteKitNetlist();
        WriteWorkerBesideKit();
        WriteWindowsLibrary();

        var (manifest, outcome) = Import();

        Assert.Equal(["win-x64"], Platforms(manifest));
        Assert.DoesNotContain(outcome.Diagnostics,
            d => d.Contains("Linux build only", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AKitWithOnlyALinuxBuild_GetsNoWindowsEntry()
    {
        // Unchanged, and the reason is unchanged: naming a platform whose build is not there turns
        // "this kit has no Windows build" into a failure to start a program.
        WriteKitNetlist();
        WriteWorkerBesideKit();
        WriteLinuxLibrary();

        var (manifest, _) = Import();

        Assert.DoesNotContain("win-x64", Platforms(manifest));
        Assert.Contains("linux-x64", Platforms(manifest));
    }

    // ── the host-module report ────────────────────────────────────────────────

    [Fact]
    public void AWindowsBuildImportingOurAbi_HasItsHostModuleNamedInTheImportReport()
    {
        // Which module the model resolves its callbacks from decides whether it is drivable at all.
        // Finding that out at Run instead of at import is a much worse way to learn it.
        WriteKitNetlist();
        WriteWorkerBesideKit();
        WriteLinuxLibrary();
        WriteModelLibrary(Path.Combine("win32_64", "models.dll"), PeMagic,
            PeFixture.WithImportsFrom(
                "SomeSimulatorHost.dll", DeviceLibraryDiscovery.Profiles[0].HostCallbacks[0], DeviceType));

        var (_, outcome) = Import();

        Assert.Contains(outcome.Notes ?? [], n => n.Contains("SomeSimulatorHost.dll", StringComparison.Ordinal));
    }

    [Fact]
    public void AWindowsBuildImportingNoneOfOurAbi_IsReportedAsUndrivable_NotSilently()
    {
        WriteKitNetlist();
        WriteWorkerBesideKit();
        WriteLinuxLibrary();
        WriteModelLibrary(Path.Combine("win32_64", "models.dll"), PeMagic,
            PeFixture.WithImportsFrom("KERNEL32.dll", "GetProcAddress", DeviceType));

        var (_, outcome) = Import();

        Assert.Contains(outcome.Diagnostics,
            d => d.Contains("host callbacks its worker supplies", StringComparison.Ordinal));
    }

    [Fact]
    public void AWindowsBuildWhoseHeadersCannotBeRead_StillImports_AndIsStillReported()
    {
        // The report is a courtesy, not a gate: a kit whose Windows build cannot be parsed must
        // still import, with the entry still written, and must not take the import down with it.
        // It IS still reported as undrivable, because that is the outcome either way.
        WriteKitNetlist();
        WriteWorkerBesideKit();
        WriteLinuxLibrary();
        WriteWindowsLibrary();   // MZ magic, then nothing that parses as a PE

        var (manifest, outcome) = Import();

        Assert.Contains("win-x64", Platforms(manifest));
        Assert.Contains(outcome.Diagnostics,
            d => d.Contains("host callbacks its worker supplies", StringComparison.Ordinal));
    }

    /// <summary>
    /// The smallest PE that carries one import descriptor — enough for the host-module report to
    /// have something real to read, with the discovery byte scan's entry-point name appended so the
    /// same file is still recognised as a model library.
    /// </summary>
    private static class PeFixture
    {
        internal static byte[] WithImportsFrom(string module, string symbol, string deviceType)
        {
            const uint sectionRva = 0x1000;
            const int  sectionRaw = 0x400;
            const int  peOffset   = 0x80;

            var content = new System.Collections.Generic.List<byte>();
            content.AddRange(new byte[40]);                       // one descriptor + terminator

            uint Rva() => sectionRva + (uint)content.Count;

            uint symRva = Rva();
            content.AddRange(new byte[2]);                        // hint
            content.AddRange(Encoding.ASCII.GetBytes(symbol));
            content.Add(0);

            while (content.Count % 8 != 0) content.Add(0);
            uint thunkRva = Rva();
            content.AddRange(BitConverter.GetBytes((ulong)symRva));
            content.AddRange(new byte[8]);                        // terminating thunk

            uint nameRva = Rva();
            content.AddRange(Encoding.ASCII.GetBytes(module));
            content.Add(0);

            // The name discovery scans for, so this file stays a recognisable model library.
            content.AddRange(Encoding.ASCII.GetBytes(
                DeviceLibraryDiscovery.Profiles[0].ExportPrefix + deviceType));
            content.Add(0);

            void U32(System.Collections.Generic.IList<byte> b, int at, uint v)
            {
                b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); b[at + 3] = (byte)(v >> 24);
            }

            U32(content, 0,  thunkRva);
            U32(content, 12, nameRva);
            U32(content, 16, thunkRva);

            var file = new System.Collections.Generic.List<byte>();
            file.AddRange(new byte[peOffset]);
            file[0] = (byte)'M'; file[1] = (byte)'Z';
            U32(file, 0x3C, peOffset);
            file.AddRange("PE\0\0"u8.ToArray());

            var coff = new byte[20];
            coff[0] = 0x64; coff[1] = 0x86;                       // Machine: x64
            coff[2] = 1;                                          // NumberOfSections
            coff[16] = 240;                                       // SizeOfOptionalHeader (PE32+)
            file.AddRange(coff);

            var opt = new byte[240];
            opt[0] = 0x0B; opt[1] = 0x02;                         // PE32+
            U32(opt, 112 + 8, sectionRva);                        // DataDirectory[1].VirtualAddress
            U32(opt, 112 + 12, 40);                               // DataDirectory[1].Size
            file.AddRange(opt);

            var sec = new byte[40];
            Encoding.ASCII.GetBytes(".idata").CopyTo(sec, 0);
            U32(sec, 8,  (uint)content.Count);
            U32(sec, 12, sectionRva);
            U32(sec, 16, (uint)content.Count);
            U32(sec, 20, sectionRaw);
            file.AddRange(sec);

            while (file.Count < sectionRaw) file.Add(0);
            file.AddRange(content);
            return [.. file];
        }
    }
}
