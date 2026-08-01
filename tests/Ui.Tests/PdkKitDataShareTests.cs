using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Pdk;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The macOS entry offers the kit's own data tree to the VM, mounted where it already lives.
///
/// <para><b>The failure this exists for.</b> A compiled model is told which data files to read
/// through its OWN parameters — this vendor's FET declares exactly four, and one of them is the path
/// to its <c>.mdl</c>. Those arrive from the netlist long after the VM has started, so unlike the
/// model library there is no command line left in which to rewrite them. The model then refuses
/// every operating point, cleanly and with no crash, and the only visible symptom is a non-finite
/// result far downstream.</para>
///
/// <para>Mounting the tree at its own absolute path means nothing has to be rewritten anywhere: a
/// path on the Mac is a path in the guest.</para>
///
/// <para>These fixtures name no vendor and no part. Discovery recognises a model library by the
/// entry points our own worker calls, which is a plain byte scan, so a file containing that name is
/// all it takes.</para>
/// </summary>
[Collection(PdkToolsDirectoryCollection.Name)]
public sealed class PdkKitDataShareTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "crf-share-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>Two levels below the scratch directory on purpose — library discovery widens outward
    /// when the narrow search finds nothing, and a root sitting directly in the system temp folder
    /// lets that walk reach another concurrently-running test's fixtures.</summary>
    private string Root         => Path.Combine(_scratch, "delivery", "root");
    private string KitDir       => Path.Combine(Root, "kit");
    private string NetlistDir   => Path.Combine(KitDir, "circuit", "models");
    private string DataDir      => Path.Combine(KitDir, "circuit", "data", "PartData");
    private string WorkspaceDir => Path.Combine(Root, "ws");

    private const string DeviceType = "CRF_TEST_V1";

    public PdkKitDataShareTests()
    {
        Directory.CreateDirectory(NetlistDir);
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(Path.Combine(DataDir, "part.mdl"), "");

        // The layout a kit uses: netlists in one folder, data in a sibling.
        File.WriteAllText(Path.Combine(NetlistDir, "kit.net"), $"""
            define PART_A ( g d s )
              {DeviceType}:M1  g d s  File="PartData/part.mdl"
            end PART_A
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best effort */ }
    }

    private static readonly byte[] ElfMagic = [0x7F, (byte)'E', (byte)'L', (byte)'F'];

    private void WriteLinuxLibrary()
    {
        string abs = Path.Combine(KitDir, "linux_x86_64", "models.so");
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);

        byte[] marker = Encoding.ASCII.GetBytes(
            DeviceLibraryDiscovery.Profiles[0].ExportPrefix + DeviceType + "\0");
        File.WriteAllBytes(abs, [.. ElfMagic, .. marker]);
    }

    private void WriteWorkerBesideKit()
    {
        var elf = new byte[64];
        elf[0] = 0x7F; elf[1] = (byte)'E'; elf[2] = (byte)'L'; elf[3] = (byte)'F';
        File.WriteAllBytes(Path.Combine(KitDir, DeviceLibraryDiscovery.Profiles[0].Worker), elf);
    }

    private JsonNode Import()
    {
        WriteWorkerBesideKit();
        WriteLinuxLibrary();

        var report = new PdkImportReport { RootPath = KitDir, KitName = "SampleKit" };
        report.Add(new PdkAsset(Path.Combine("circuit", "models", "kit.net"), PdkAssetKind.Netlist,
                                PdkAssetSupport.Supported, "kit netlist"));
        report.Parts.Add(new PdkPart("PART_A", "Part A"));

        var outcome = PdkPartInstaller.Install(report);
        Assert.NotNull(outcome.Settings);
        return outcome.Settings!;
    }

    private static string[] Arguments(JsonNode manifest)
        => [.. manifest["workers"]!.AsArray()
                .Single(w => w!["platform"]!.GetValue<string>() == "osx")!["arguments"]!
                .AsArray().Select(a => a!.GetValue<string>())];

    [Fact]
    public void TheOsxEntryOffersTheKitsDataTree_MountedWhereItLives()
    {
        var args = Arguments(Import());

        int at = Array.IndexOf(args, VmHostArguments.ShareAtFlag);
        Assert.True(at >= 0, "no share mounted at its own path — a kit's data files cannot be opened");

        // The tree offered is exactly the one the reader can anchor a data file within. Anything
        // narrower would leave a file it resolved unopenable; anything wider is somebody else's.
        string expected = KitDataFileResolver.OutermostSearchRoot(NetlistDir)!;
        Assert.Equal(VmHostArguments.ShareValue("kitdata", expected), args[at + 1]);
    }

    [Fact]
    public void TheDataTreeCoversTheFileTheReaderResolved()
    {
        // The two must agree, and this is the assertion that actually ties them: resolve a data file
        // the way a run does, then check the shared tree contains it. Two separate notions of "near
        // the kit" would drift, and the failure when they do is a path that resolves perfectly at
        // import and cannot be opened at run time.
        var read = KitNetlistReader.ReadFile(Path.Combine(NetlistDir, "kit.net"));

        string resolved = read.Library.Cells
            .SelectMany(c => c.Instances)
            .Single().Overrides.Single(o => o.Name == "File").Expression.Trim('"');

        Assert.True(Path.IsPathRooted(resolved), "the reader did not anchor the data file at all");

        var args = Arguments(Import());
        string shared = args[Array.IndexOf(args, VmHostArguments.ShareAtFlag) + 1];
        string tree   = shared["kitdata=".Length..^":ro".Length];

        Assert.StartsWith(tree + Path.DirectorySeparatorChar, resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void TheShareIsReadOnly()
    {
        // A vendor kit is not ours to write to, and the guest runs the library's own code.
        var args = Arguments(Import());

        Assert.EndsWith(":ro", args[Array.IndexOf(args, VmHostArguments.ShareAtFlag) + 1]);
    }

    [Fact]
    public void TheWorkerAndLibraryShares_AreUnchanged()
    {
        // The two that already worked keep their /mnt form: the guest argv naming them is written
        // here too, so they need no path to be true anywhere.
        var args = Arguments(Import());

        Assert.Equal(2, args.Count(a => a == VmHostArguments.ShareFlag));
        Assert.Contains(VmHostArguments.GuestPath("crfw", DeviceLibraryDiscovery.Profiles[0].Worker), args);
        Assert.Contains(VmHostArguments.GuestPath("kit",  "models.so"), args);
    }

    [Fact]
    public void SettingsFromAnOlderBuild_AreRedone_NotReplayedRunnableAndWrong()
    {
        // They name programs that all still exist, so nothing else would ever replace them — and they
        // are missing the data share, which is exactly the state that fails at run time only.
        var stale = Import();
        stale["generatedFormat"] = 1;
        stale["workers"] = new JsonArray(new JsonObject
        {
            ["platform"]  = "osx",
            ["command"]   = VmHostArguments.Command,
            ["arguments"] = new JsonArray("--share", "crfw=/x:ro", "--", "/mnt/crfw/w", "/mnt/kit/models.so"),
        });

        var report = new PdkImportReport { RootPath = KitDir, KitName = "SampleKit" };
        report.Add(new PdkAsset(Path.Combine("circuit", "models", "kit.net"), PdkAssetKind.Netlist,
                                PdkAssetSupport.Supported, "kit netlist"));
        report.Parts.Add(new PdkPart("PART_A", "Part A"));

        var settings = PdkPartInstaller.Install(report, stale).Settings;
        Assert.NotNull(settings);

        Assert.Contains(VmHostArguments.ShareAtFlag, Arguments(settings!));
    }
}
