using System;
using System.IO;
using System.Linq;
using System.Text;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.External;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.External;

/// <summary>
/// Establishing which compiled library serves a kit's devices, in an unmodified vendor tree.
///
/// <para><b>Why it has to be worked out at all.</b> A vendor delivery is several read-only kits
/// beside one shared library package. A part kit names its device types but never says which library
/// implements them — its own simulator resolves them by name across everything loaded. So the
/// binding is written down nowhere, and either the importer establishes it or somebody hand-writes a
/// file per kit before anything can be simulated.</para>
///
/// <para>These fixtures are synthetic: a "library" here is a file containing the exported entry-point
/// name, which is all the real scan looks for.</para>
/// </summary>
public sealed class DeviceLibraryDiscoveryTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "crf-dld-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>
    /// Two levels below the scratch directory ON PURPOSE. <c>Find</c> widens outward when the
    /// narrower search finds nothing, so a root sitting directly in the system temp folder lets the
    /// walk reach every other test's fixtures — including another assembly's, running concurrently.
    /// That is not hypothetical: it is what this codebase's own "widen only when nothing was found"
    /// note already records having been bitten by. Burying the root keeps the walk inside it.
    /// </summary>
    private string _root => Path.Combine(_scratch, "delivery", "root");

    public DeviceLibraryDiscoveryTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_scratch, true); } catch { } }

    private static string Prefix => DeviceLibraryDiscovery.Profiles[0].ExportPrefix;

    /// <summary>Writes a stand-in library advertising <paramref name="types"/>.</summary>
    private string Library(string relativePath, params string[] types)
        => Library(relativePath, magic: null, types);

    /// <summary>
    /// As above, with a container-format magic prefix. Which platform a build is FOR is decided by
    /// the file's own magic bytes, not by its extension or the folder a vendor happened to put it
    /// in — so a fixture that wants to be found by a per-target search has to carry it.
    /// </summary>
    private string Library(string relativePath, byte[]? magic, params string[] types)
    {
        string abs = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);

        var sb = new StringBuilder("\0\0padding\0\0");
        foreach (var t in types) sb.Append('\0').Append(Prefix).Append(t).Append('\0');

        byte[] body = Encoding.ASCII.GetBytes(sb.ToString());
        File.WriteAllBytes(abs, magic is null ? body : [.. magic, .. body]);
        return abs;
    }

    private static readonly byte[] ElfMagic = [0x7F, (byte)'E', (byte)'L', (byte)'F'];
    private static readonly byte[] PeMagic  = [(byte)'M', (byte)'Z', 0x90, 0x00];

    // ── Which types a kit needs ───────────────────────────────────────────────

    /// <summary>
    /// The classification is the whole trick: primitives and the kit's own cells are recognisable,
    /// so whatever is left is a compiled model. Nothing knows a type name.
    /// </summary>
    [Fact]
    public void NativeDeviceTypes_AreWhatIsNeitherAPrimitiveNorACellTheKitDefines()
    {
        var lib = new Library("kit");
        var sub = new Cell("Helper");
        var top = new Cell("Part");
        top.Instances.Add(new Instance("R1",   "R",          ["a", "b"]));
        top.Instances.Add(new Instance("S1",   "Helper",     ["a", "b"]));
        top.Instances.Add(new Instance("FET1", "KIT_FET_v1", ["a", "b", "c"]));
        top.Instances.Add(new Instance("FET2", "KIT_FET_v1", ["a", "b", "c"]));   // same type twice
        lib.Cells.Add(sub);
        lib.Cells.Add(top);

        Assert.Equal(["KIT_FET_v1"], DeviceLibraryDiscovery.NativeDeviceTypes(lib));
    }

    [Fact]
    public void AKitThatDefinesEverythingItUses_NeedsNoLibrary()
    {
        var lib = new Library("kit");
        var top = new Cell("Part");
        top.Instances.Add(new Instance("R1", "R", ["a", "b"]));
        lib.Cells.Add(top);

        Assert.Empty(DeviceLibraryDiscovery.NativeDeviceTypes(lib));
    }

    // ── Finding the library ───────────────────────────────────────────────────

    [Fact]
    public void TheLibraryAdvertisingTheTypeIsFound_AndOthersAreNot()
    {
        Library("other/Unrelated.so", "SOMETHING_ELSE");
        string wanted = Library("pkg/bin/linux_x86_64/Models.so", "KIT_FET_v1");

        var m = DeviceLibraryDiscovery.Find(["KIT_FET_v1"], _root);

        Assert.NotNull(m);
        Assert.Equal(wanted, m!.Path);
        Assert.Equal(["KIT_FET_v1"], m.Types);
    }

    [Fact]
    public void NothingAdvertisingTheType_FindsNothing()
    {
        Library("pkg/bin/Models.so", "SOMETHING_ELSE");
        Assert.Null(DeviceLibraryDiscovery.Find(["KIT_FET_v1"], _root));
    }

    /// <summary>
    /// The case the whole search exists for: a vendor puts the shared library package BESIDE the
    /// kits, so it is not inside the folder that was imported.
    /// </summary>
    [Fact]
    public void ALibraryBesideTheImportedKit_IsFound()
    {
        Directory.CreateDirectory(Path.Combine(_root, "MY_KIT", "circuit"));
        string lib = Library("SharedModels/bin/linux_x86_64/Models.so", "KIT_FET_v1");

        var m = DeviceLibraryDiscovery.Find(["KIT_FET_v1"], Path.Combine(_root, "MY_KIT"));

        Assert.Equal(lib, m?.Path);
    }

    [Fact]
    public void TheWalkOutwardIsBounded()
    {
        // Without a bound this would search ever upward from a folder on somebody's disk.
        string deep = Path.Combine(_root, "a", "b", "c", "d", "e");
        Directory.CreateDirectory(deep);
        Library("Models.so", "KIT_FET_v1");

        Assert.Null(DeviceLibraryDiscovery.Find(["KIT_FET_v1"], deep, ancestorLevels: 1));
    }

    // ── Choosing between candidates ───────────────────────────────────────────

    /// <summary>
    /// Several DIFFERENT libraries serving the same types is genuine ambiguity — the choice changes
    /// which model evaluates the design — so it is refused and reported rather than guessed.
    /// </summary>
    [Fact]
    public void DifferentLibrariesServingTheSameType_AreRefusedAndReported()
    {
        Library("a/linux_x86_64/ModelsOne.so", "KIT_FET_v1");
        Library("b/linux_x86_64/ModelsTwo.so", "KIT_FET_v1");

        string? said = null;
        var m = DeviceLibraryDiscovery.Find(["KIT_FET_v1"], _root, report: s => said = s);

        Assert.Null(m);
        Assert.Contains("ModelsOne.so", said);
        Assert.Contains("ModelsTwo.so", said);
    }

    /// <summary>
    /// The SAME library built for a dozen toolchains is the ordinary case, not ambiguity — refusing
    /// it would reject every real vendor delivery. The most specifically named build wins, and the
    /// choice is reported because it was made automatically.
    /// </summary>
    [Fact]
    public void SeveralBuildsOfOneLibrary_PickTheMostSpecificAndSaySo()
    {
        Library("bin/linux_x86_64/Models.so",                 "KIT_FET_v1");
        Library("bin/linux_x86_64_GCC820/Models.so",          "KIT_FET_v1");
        string newest = Library("bin/SIM_2025_linux_x86_64_GCC1210/Models.so", "KIT_FET_v1");
        Library("bin/SIM_2023_linux_x86_64_GCC1210/Models.so", "KIT_FET_v1");

        string? said = null;
        var m = DeviceLibraryDiscovery.Find(["KIT_FET_v1"], _root, report: s => said = s);

        Assert.Equal(newest, m?.Path);
        Assert.Contains("4 builds", said);
    }

    [Fact]
    public void TheCallersPlatformHints_DecideWhichBuildIsWanted()
    {
        Library("bin/linux_x86_64/Models.so",   "KIT_FET_v1");
        string win = Library("bin/win32_64/Models.dll", "KIT_FET_v1");

        var m = DeviceLibraryDiscovery.Find(
            ["KIT_FET_v1"], _root, preferPathContaining: ["win32_64", ".dll"]);

        Assert.Equal(win, m?.Path);
    }

    [Fact]
    public void TheLibrarySERVINGTheMostWantedTypes_Wins()
    {
        Library("bin/linux_x86_64/Partial.so", "KIT_A");
        string both = Library("bin/linux_x86_64/Full.so", "KIT_A", "KIT_B");

        var m = DeviceLibraryDiscovery.Find(["KIT_A", "KIT_B"], _root);

        Assert.Equal(both, m?.Path);
        Assert.Equal(2, m!.Types.Count);
    }

    [Fact]
    public void AskingForNothing_FindsNothing()
        => Assert.Null(DeviceLibraryDiscovery.Find([], _root));

    // ── Which PLATFORM a build is for ─────────────────────────────────────────
    //
    // The hints above only RANK. They cannot decide which platform a file is for, because a vendor
    // names its folders for its own toolchains and its files for whatever it likes. The format
    // filter is what separates the per-target searches, and it reads the file's own magic bytes.

    [Fact]
    public void AKitWithOnlyALinuxBuild_AnswersNoWindowsSearch()
    {
        // The bug this closes: hints alone made one library answer BOTH searches, so a Linux-only
        // kit was described as having a Windows build — and the entry naming it would then fail at
        // launch, which is exactly what naming a platform must never do.
        Library("bin/linux_x86_64/Models.so", ElfMagic, "KIT_FET_v1");

        var win = DeviceLibraryDiscovery.Find(
            ["KIT_FET_v1"], _root, ["win32_64", ".dll"], format: DeviceLibraryDiscovery.LibraryFormat.Pe);

        Assert.Null(win);
    }

    [Fact]
    public void EachTargetsSearch_FindsItsOwnBuild_WhenBothAreShippedSideBySide()
    {
        string so  = Library("bin/linux_x86_64/Models.so",  ElfMagic, "KIT_FET_v1");
        string dll = Library("bin/win32_64/Models.dll",     PeMagic,  "KIT_FET_v1");

        Assert.Equal(so, DeviceLibraryDiscovery.Find(
            ["KIT_FET_v1"], _root, ["linux_x86_64"], format: DeviceLibraryDiscovery.LibraryFormat.Elf)?.Path);
        Assert.Equal(dll, DeviceLibraryDiscovery.Find(
            ["KIT_FET_v1"], _root, ["win32_64"], format: DeviceLibraryDiscovery.LibraryFormat.Pe)?.Path);
    }

    [Fact]
    public void TheMagicDecides_NotTheExtensionOrTheFolderName()
    {
        // A file named and filed as a Windows build, but actually an ELF. Trusting the name would
        // hand a loader something it cannot load; the first four bytes cannot be a convention.
        Library("bin/win32_64/Models.dll", ElfMagic, "KIT_FET_v1");

        Assert.Null(DeviceLibraryDiscovery.Find(
            ["KIT_FET_v1"], _root, format: DeviceLibraryDiscovery.LibraryFormat.Pe));
        Assert.NotNull(DeviceLibraryDiscovery.Find(
            ["KIT_FET_v1"], _root, format: DeviceLibraryDiscovery.LibraryFormat.Elf));
    }

    [Fact]
    public void WithNoFormatAsked_EveryCandidateStillCounts()
    {
        // The default is unchanged, so every existing caller behaves exactly as before.
        string plain = Library("bin/linux_x86_64/Models.so", "KIT_FET_v1");   // no magic at all

        Assert.Equal(plain, DeviceLibraryDiscovery.Find(["KIT_FET_v1"], _root)?.Path);
    }
}
