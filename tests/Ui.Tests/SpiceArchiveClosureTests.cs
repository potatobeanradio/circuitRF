using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using CircuitRF.Core.Netlist.Spice;
using CircuitRF.Ui.Archive;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Archiving a workspace that points at a SPICE deck — <b>the whole deck, not its entry point</b>.
///
/// <para>A <c>SpiceModel</c> naming an outside library file was already found, offered and repointed.
/// But <see cref="DocumentFileRefs"/> walks a design document as JSON and never opens the deck, so a
/// library file that <c>.include</c>s a shared model file contributed exactly one row: the recipient
/// got the entry point and none of its contents. And the failure is quiet — the reader notes the
/// missing file, marks the enclosing cell incomplete, and the subcircuit is refused at simulate time,
/// in another session, on another machine, about a file the recipient has never heard of.</para>
///
/// <para><b>The assertion that matters is the last one</b>: extracting the archive and re-reading the
/// deck from inside it yields the same cells as reading the original. Counting three copied files
/// would pass just as well with the relative structure flattened, which is the failure mode.</para>
/// </summary>
public sealed class SpiceArchiveClosureTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "crf-spicearch-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string File_(string relative, string content)
    {
        var p = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, content);
        return p;
    }

    /// <summary>A workspace whose one schematic points a SpiceModel at <paramref name="storedRef"/>.</summary>
    private string Workspace(string storedRef, string cwsJson = """{"FormatVersion":2,"LibraryRefs":[],"KnownFiles":[]}""")
    {
        var ws = Path.Combine(_root, "ws");
        Directory.CreateDirectory(ws);
        System.IO.File.WriteAllText(Path.Combine(ws, ".cws"), cwsJson);

        File_("ws/Amp/.ccell", """{"FormatVersion":1,"PrimarySchematic":"Amp.csch"}""");
        File_("ws/Amp/schematic/Amp.csch", $$"""
            {"FormatVersion":2,"CellName":"Amp","Components":[
              {"InstanceName":"X1","Symbol":"SpiceModel","Parameters":[
                {"Name":"File","Expression":"{{storedRef.Replace("\\", "/")}}"}]}]}
            """);
        return ws;
    }

    /// <summary>
    /// A three-file deck: the entry point includes a sibling in a sub-folder, which includes a third
    /// one level back out. The deepest common ancestor is <c>decks/</c>, and the relative offsets are
    /// what has to survive the copy.
    /// </summary>
    private void ThreeFileDeck()
    {
        File_("decks/entry.lib", """
            .include sub/mid.lib
            .subckt TOP a b
            X1 a b MID
            .ends
            """);
        File_("decks/sub/mid.lib", """
            .include ../shared/leaf.lib
            .subckt MID a b
            X1 a b LEAF
            .ends
            """);
        File_("decks/shared/leaf.lib", """
            .subckt LEAF a b
            R1 a b 1k
            .ends
            """);
    }

    private static string[] EntryNames(string zip)
    {
        using var z = ZipFile.OpenRead(zip);
        return [.. z.Entries.Select(e => e.FullName)];
    }

    private static string ReadEntry(string zip, string name)
    {
        using var z = ZipFile.OpenRead(zip);
        using var r = new StreamReader(z.GetEntry(name)!.Open());
        return r.ReadToEnd();
    }

    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ADeckThatIncludesOtherFiles_IsOneRowCarryingItsWholeClosure()
    {
        ThreeFileDeck();
        var ws = Workspace("../decks/entry.lib");

        var plan = WorkspaceArchiveScanner.Scan(ws);

        var row = Assert.Single(plan.ExternalFiles);
        Assert.True(row.IsDirectory);
        Assert.Equal(3, row.Members.Count);
        Assert.StartsWith("external/spice/", row.ArchivePath);
        Assert.True(row.Selected);          // unchanged: small, and the design needs it

        // The row says how many files travel and what pulled them in — a recipient's "why is there a
        // folder of model files in here?" is answered without opening it.
        Assert.Contains("3 files", row.Detail);
        Assert.Contains("entry.lib", row.Detail);
        Assert.Contains("Amp.csch", row.Detail);
    }

    [Fact]
    public void TheArchivedCopyKeepsTheDecksOwnStructure_SoEveryIncludeStillResolves()
    {
        ThreeFileDeck();
        var ws = Workspace("../decks/entry.lib");

        var plan = WorkspaceArchiveScanner.Scan(ws);
        var zip  = Path.Combine(_root, "out.zip");
        WorkspaceArchiveWriter.Write(plan, zip);

        var names = EntryNames(zip);
        Assert.Contains("ws/external/spice/decks/entry.lib",      names);
        Assert.Contains("ws/external/spice/decks/sub/mid.lib",    names);
        Assert.Contains("ws/external/spice/decks/shared/leaf.lib", names);

        // ONLY the entry point is repointed. The includes inside the deck are untouched — that is
        // the property that makes this robust rather than clever.
        var csch = JsonNode.Parse(ReadEntry(zip, "ws/Amp/schematic/Amp.csch"))!;
        Assert.Equal("external/spice/decks/entry.lib",
                     csch["Components"]![0]!["Parameters"]![0]!["Expression"]!.GetValue<string>());
        Assert.Contains(".include sub/mid.lib", ReadEntry(zip, "ws/external/spice/decks/entry.lib"));

        // The assertion that actually proves it: the extracted deck reads to the same cells as the
        // original. Three files at three paths would pass a copy count and fail this.
        var into = Path.Combine(_root, "unpacked");
        ZipFile.ExtractToDirectory(zip, into);

        var original = SpiceNetlistReader.ReadFile(Path.Combine(_root, "decks", "entry.lib"));
        var archived = SpiceNetlistReader.ReadFile(
            Path.Combine(into, "ws", "external", "spice", "decks", "entry.lib"));

        Assert.Equal(["LEAF", "MID", "TOP"], original.Library.Cells.Select(c => c.Name).Order());
        Assert.Equal(original.Library.Cells.Select(c => c.Name).Order(),
                     archived.Library.Cells.Select(c => c.Name).Order());
        Assert.Empty(archived.IncompleteCells);
    }

    [Fact]
    public void ASelfContainedDeck_IsStillOneFlatRow_ExactlyAsItWasBefore()
    {
        // The common case, and it must not gain a folder around it: a closure of one is one row at
        // external/<name>, which is what every existing archive already carries.
        File_("decks/solo.lib", """
            .subckt SOLO a b
            R1 a b 1k
            .ends
            """);
        var ws = Workspace("../decks/solo.lib");

        var plan = WorkspaceArchiveScanner.Scan(ws);

        var row = Assert.Single(plan.ExternalFiles);
        Assert.False(row.IsDirectory);
        Assert.Empty(row.Members);
        Assert.Equal("external/solo.lib", row.ArchivePath);

        var zip = Path.Combine(_root, "out.zip");
        WorkspaceArchiveWriter.Write(plan, zip);
        Assert.Contains("ws/external/solo.lib", EntryNames(zip));
    }

    [Fact]
    public void AClosureMemberInsideAnIncludedKit_IsNotDuplicatedIntoExternal()
    {
        // A library file reached through a kit is part of THAT kit's row: its internal references are
        // written against the kit's own folder, and a second copy under external/ would be a
        // divergent duplicate of a file the recipient already has (spice-models.md §12.4).
        File_("kit/models/shared.lib", """
            .subckt LEAF a b
            R1 a b 1k
            .ends
            """);
        File_("decks/entry.lib", $$"""
            .include {{Path.Combine(_root, "kit", "models", "shared.lib").Replace("\\", "/")}}
            .subckt TOP a b
            X1 a b LEAF
            .ends
            """);

        var ws = Workspace("../decks/entry.lib", $$"""
            {"FormatVersion":2,"LibraryRefs":["{{Path.Combine(_root, "kit").Replace("\\", "/")}}"],"KnownFiles":[]}
            """);

        var plan = WorkspaceArchiveScanner.Scan(ws);

        Assert.Single(plan.Kits);

        // Only the entry point is left outside the kit, so the closure is one file and the row is
        // the ordinary flat one.
        var row = Assert.Single(plan.ExternalFiles);
        Assert.False(row.IsDirectory);
        Assert.Equal("external/entry.lib", row.ArchivePath);
    }

    [Fact]
    public void AClosureMemberAlreadyInsideTheWorkspace_IsNotCopiedASecondTime()
    {
        // It already travels — unconditionally, as part of the workspace.
        var ws = Workspace("../decks/entry.lib");
        File_("ws/models/shared.lib", """
            .subckt LEAF a b
            R1 a b 1k
            .ends
            """);
        File_("decks/entry.lib", $$"""
            .include {{Path.Combine(_root, "ws", "models", "shared.lib").Replace("\\", "/")}}
            .subckt TOP a b
            X1 a b LEAF
            .ends
            """);

        var plan = WorkspaceArchiveScanner.Scan(ws);

        var row = Assert.Single(plan.ExternalFiles);
        Assert.False(row.IsDirectory);
        Assert.Contains("models/shared.lib", plan.AlwaysIncluded);
    }

    /// <summary>
    /// <see cref="DocumentFileRefs.TryResolve"/> rejects any extension outside [2,12] characters
    /// before it ever touches the filesystem, so a deck spelled with the SHORTEST extension a
    /// supplier uses has to be confirmed rather than assumed.
    /// </summary>
    [Fact]
    public void TheShortestSpiceExtensionStillResolves()
    {
        File_("decks/part.sp", """
            .include shared/leaf.sp
            .subckt TOP a b
            X1 a b LEAF
            .ends
            """);
        File_("decks/shared/leaf.sp", """
            .subckt LEAF a b
            R1 a b 1k
            .ends
            """);

        var ws   = Workspace("../decks/part.sp");
        var plan = WorkspaceArchiveScanner.Scan(ws);

        var row = Assert.Single(plan.ExternalFiles);
        Assert.Equal(2, row.Members.Count);
    }

    [Fact]
    public void TwoDecksSharingADirectory_AreOneSubtreeRatherThanTwoOverlappingCopies()
    {
        // Two overlapping subtree rows would copy the shared model file twice and leave the recipient
        // with two divergent copies of one file.
        File_("decks/shared/leaf.lib", """
            .subckt LEAF a b
            R1 a b 1k
            .ends
            """);
        File_("decks/a.lib", """
            .include shared/leaf.lib
            .subckt PART_A a b
            X1 a b LEAF
            .ends
            """);
        File_("decks/b.lib", """
            .include shared/leaf.lib
            .subckt PART_B a b
            X1 a b LEAF
            .ends
            """);

        var ws = Workspace("../decks/a.lib");
        File_("ws/Amp/schematic/Second.csch", $$"""
            {"FormatVersion":2,"CellName":"Second","Components":[
              {"InstanceName":"X2","Symbol":"SpiceModel","Parameters":[
                {"Name":"File","Expression":"../decks/b.lib"}]}]}
            """);

        var plan = WorkspaceArchiveScanner.Scan(ws);

        var row = Assert.Single(plan.ExternalFiles);
        Assert.Equal(3, row.Members.Count);
        Assert.Equal(1, row.Members.Count(m => m.RelativePath == "shared/leaf.lib"));

        var zip = Path.Combine(_root, "out.zip");
        WorkspaceArchiveWriter.Write(plan, zip);

        var names = EntryNames(zip);
        Assert.Contains("ws/external/spice/decks/a.lib", names);
        Assert.Contains("ws/external/spice/decks/b.lib", names);

        // BOTH entry points repointed, one shared copy behind them.
        var first  = JsonNode.Parse(ReadEntry(zip, "ws/Amp/schematic/Amp.csch"))!;
        var second = JsonNode.Parse(ReadEntry(zip, "ws/Amp/schematic/Second.csch"))!;
        Assert.Equal("external/spice/decks/a.lib",
                     first["Components"]![0]!["Parameters"]![0]!["Expression"]!.GetValue<string>());
        Assert.Equal("external/spice/decks/b.lib",
                     second["Components"]![0]!["Parameters"]![0]!["Expression"]!.GetValue<string>());
    }

    [Fact]
    public void OnlyTheClosureTravels_NotEverythingElseInTheFolderItIsRootedAt()
    {
        // The root is a real directory on this machine and routinely holds a great deal the deck
        // never reads. Copying the folder would archive a model library to carry three files.
        ThreeFileDeck();
        File_("decks/unrelated.lib", ".subckt NOBODY a b\nR1 a b 1k\n.ends\n");
        File_("decks/sub/notes.txt", "not part of the deck");

        var ws   = Workspace("../decks/entry.lib");
        var plan = WorkspaceArchiveScanner.Scan(ws);
        var zip  = Path.Combine(_root, "out.zip");
        WorkspaceArchiveWriter.Write(plan, zip);

        var names = EntryNames(zip);
        Assert.DoesNotContain("ws/external/spice/decks/unrelated.lib", names);
        Assert.DoesNotContain("ws/external/spice/decks/sub/notes.txt", names);
    }
}
