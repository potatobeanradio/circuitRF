using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Design.Cells;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Importing SEVERAL definitions out of one SPICE file — the case a library file is actually made of.
///
/// <para><b>The gap was not "several cells", it was SIBLINGS.</b> Nested definitions have always
/// imported as one cell per transitive dependency, leaf-first, and that is the right model: a
/// circuitRF cell instance references a cell FOLDER, so a nested definition has nowhere else to live.
/// What did not work is two TOP-LEVEL parts over a shared core — measured at 4 shared cells in one
/// library file and 1 in another. Importing one variant wrote the core; importing the other then
/// planned that same folder, found it there, and was refused. Never overwriting is right, so the
/// second variant was permanently unimportable.</para>
///
/// <para>Two changes fix it and neither bends the never-overwrite rule: one gesture plans every
/// chosen definition together so a shared core is written once, and an existing folder is reused only
/// when its recorded provenance PROVES it is the same definition, unedited.</para>
/// </summary>
public sealed class SpiceMultiDefinitionImportTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "crf-multidef-" + Guid.NewGuid().ToString("N")[..8]);

    public SpiceMultiDefinitionImportTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>Two top-level parts built over one shared core — the shape two of four measured files have.</summary>
    private const string TwoPartsOneCore = """
        .subckt CORE p n
        R1 p n 1k
        .ends
        .subckt PART_A a b
        X1 a b CORE
        .ends
        .subckt PART_B a b
        X1 a b CORE
        C1 a b 1p
        .ends
        """;

    private string WriteFile(string name, string text)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        return path;
    }

    private string Dir(string name)
    {
        string p = Path.Combine(_root, name);
        Directory.CreateDirectory(p);
        return p;
    }

    private static SpiceCellCandidate Candidate(SpiceCellScan scan, string name)
        => scan.Candidates.Single(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every file under a folder, keyed by its relative path — the whole of what was written.</summary>
    private static Dictionary<string, byte[]> Tree(string root)
        => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                    .ToDictionary(
                        f => Path.GetRelativePath(root, f).Replace('\\', '/'),
                        File.ReadAllBytes,
                        StringComparer.Ordinal);

    // ── The collision that made a second variant unimportable ────────────────

    [Fact]
    public void ImportingOneSibling_ThenTheOther_Succeeds_AndTheSharedCoreIsWrittenOnce()
    {
        string file = WriteFile("lib.lib", TwoPartsOneCore);
        var    scan = SpiceCellImport.Scan(file);
        var    into = Dir("ws");

        SpiceCellImport.Write(into, "PART_A", Candidate(scan, "PART_A"), scan, file);
        string coreSchematic = Path.Combine(into, "CORE", "schematic", "CORE.csch");
        var    coreBefore    = File.ReadAllBytes(coreSchematic);

        // This is the call that used to throw: CORE is already there because PART_A needed it.
        var second = SpiceCellImport.Write(into, "PART_B", Candidate(scan, "PART_B"), scan, file);

        Assert.True(Directory.Exists(Path.Combine(into, "PART_B")));
        Assert.Equal(["CORE", "PART_A", "PART_B"],
                     Directory.GetDirectories(into).Select(Path.GetFileName).Order());

        // ONE copy, and it is the SAME copy — reused, not rewritten.
        Assert.Equal(coreBefore, File.ReadAllBytes(coreSchematic));
        Assert.Contains(second.Report, l => l.Contains("'CORE'") && l.Contains("reused"));
    }

    [Fact]
    public void ImportingTheSecondSibling_WhenTheCoreOnDiskHasBeenEdited_RefusesAndSaysThat()
    {
        string file = WriteFile("lib.lib", TwoPartsOneCore);
        var    scan = SpiceCellImport.Scan(file);
        var    into = Dir("ws");

        SpiceCellImport.Write(into, "PART_A", Candidate(scan, "PART_A"), scan, file);

        // The user opened CORE and changed it. Reuse is only ever legitimate on PROVEN identity, so
        // this has to become a refusal — and one that says WHY, rather than the generic
        // already-exists sentence, which would send them looking for a name clash that is not there.
        string coreSchematic = Path.Combine(into, "CORE", "schematic", "CORE.csch");
        File.WriteAllText(coreSchematic, File.ReadAllText(coreSchematic).Replace("\"Wires\"", "\"wires\""));

        var ex = Assert.Throws<IOException>(
            () => SpiceCellImport.Write(into, "PART_B", Candidate(scan, "PART_B"), scan, file));

        Assert.Contains("edited since it was imported", ex.Message);
        Assert.Contains("CORE", ex.Message);

        // All or nothing: nothing of the refused import is left behind.
        Assert.False(Directory.Exists(Path.Combine(into, "PART_B")));
    }

    [Fact]
    public void AMultiSelectImport_WritesExactlyWhatTwoSequentialImportsWould()
    {
        string file = WriteFile("lib.lib", TwoPartsOneCore);
        var    scan = SpiceCellImport.Scan(file);

        var together = Dir("together");
        SpiceCellImport.WriteMany(
            together,
            [(Candidate(scan, "PART_A"), "PART_A"), (Candidate(scan, "PART_B"), "PART_B")],
            scan, file);

        var oneAtATime = Dir("oneAtATime");
        SpiceCellImport.Write(oneAtATime, "PART_A", Candidate(scan, "PART_A"), scan, file);
        SpiceCellImport.Write(oneAtATime, "PART_B", Candidate(scan, "PART_B"), scan, file);

        var a = Tree(together);
        var b = Tree(oneAtATime);

        Assert.Equal(a.Keys.Order(), b.Keys.Order());
        foreach (var (rel, bytes) in a)
            Assert.True(bytes.SequenceEqual(b[rel]), $"{rel} differs between the two routes");
    }

    [Fact]
    public void AMultiSelectImport_OpensTheFirstDefinitionTheUserChose_NotTheCoreUnderneathIt()
    {
        string file = WriteFile("lib.lib", TwoPartsOneCore);
        var    scan = SpiceCellImport.Scan(file);
        var    into = Dir("ws");

        var result = SpiceCellImport.WriteMany(
            into,
            [(Candidate(scan, "PART_A"), "PART_A"), (Candidate(scan, "PART_B"), "PART_B")],
            scan, file);

        // CORE is planned FIRST (leaf-first, so a parent's reference resolves), which is exactly why
        // "the first cell written" is the wrong answer to "what should open".
        Assert.Equal(Path.Combine(into, "PART_A"), result.CellDir);

        // Everything else this created is reported rather than left to be discovered.
        Assert.Contains(Path.Combine(into, "PART_B"), result.AlsoCreated);
        Assert.Contains(Path.Combine(into, "CORE"),   result.AlsoCreated);
    }

    // ── Provenance ───────────────────────────────────────────────────────────

    [Fact]
    public void AWrittenCellRecordsWhereItCameFrom_WhichIsWhatMakesReuseALookup()
    {
        string file = WriteFile("parts.lib", TwoPartsOneCore);
        var    scan = SpiceCellImport.Scan(file);
        var    into = Dir("ws");

        SpiceCellImport.Write(into, "PART_A", Candidate(scan, "PART_A"), scan, file);

        var ccell = CellPersistence.LoadFromFile(Path.Combine(into, "CORE", ".ccell"));
        var from  = Assert.IsType<CcellImportProvenance>(ccell.ImportedFrom);

        Assert.Equal("CORE", from.Definition);
        Assert.NotEmpty(from.ContentHash);

        // The file's NAME, never its path: a .ccell travels into archives and onto other machines,
        // where the sender's absolute path means nothing and should not have gone.
        Assert.Equal("parts.lib", from.Source);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, from.Source);
    }

    [Fact]
    public void ADifferentDefinitionUnderANameAlreadyInTheWorkspace_IsStillRefused()
    {
        string first = WriteFile("a.lib", """
            .subckt CORE p n
            R1 p n 1k
            .ends
            """);
        string second = WriteFile("b.lib", """
            .subckt CORE p n
            R1 p n 2k
            .ends
            """);

        var into = Dir("ws");
        var scanA = SpiceCellImport.Scan(first);
        SpiceCellImport.Write(into, "CORE", Candidate(scanA, "CORE"), scanA, first);

        var scanB = SpiceCellImport.Scan(second);
        var ex = Assert.Throws<IOException>(
            () => SpiceCellImport.Write(into, "CORE", Candidate(scanB, "CORE"), scanB, second));

        // Reuse is on proven CONTENT identity, never on the name matching.
        Assert.Contains("different definition", ex.Message);
        Assert.Contains("a.lib", ex.Message);
    }

    [Fact]
    public void ACellThatNoImportWrote_KeepsTheGenericRefusal()
    {
        string file = WriteFile("lib.lib", TwoPartsOneCore);
        var    scan = SpiceCellImport.Scan(file);
        var    into = Dir("ws");

        // A cell the user drew has no provenance, so there is nothing to prove identity with and
        // the only safe answer is the refusal that was always there.
        Directory.CreateDirectory(Path.Combine(into, "CORE"));

        var ex = Assert.Throws<IOException>(
            () => SpiceCellImport.Write(into, "PART_A", Candidate(scan, "PART_A"), scan, file));

        Assert.Contains("already exists here", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(into, "PART_A")));
    }

    [Fact]
    public void ImportingTheSameDefinitionTwiceUnderTheSameName_ReusesRatherThanRefusing()
    {
        string file = WriteFile("lib.lib", TwoPartsOneCore);
        var    scan = SpiceCellImport.Scan(file);
        var    into = Dir("ws");

        SpiceCellImport.Write(into, "PART_A", Candidate(scan, "PART_A"), scan, file);
        var again = SpiceCellImport.Write(into, "PART_A", Candidate(scan, "PART_A"), scan, file);

        Assert.Contains(again.Report, l => l.Contains("'PART_A'") && l.Contains("reused"));
    }

    // ── The case that must not regress ───────────────────────────────────────

    [Fact]
    public void ASingleDefinitionWithNestedDependencies_StillImportsExactlyAsItDid()
    {
        string file = WriteFile("lib.lib", TwoPartsOneCore);
        var    scan = SpiceCellImport.Scan(file);

        var once  = Dir("once");
        var twice = Dir("twice");

        SpiceCellImport.Write(once,  "PART_A", Candidate(scan, "PART_A"), scan, file);
        SpiceCellImport.Write(twice, "PART_A", Candidate(scan, "PART_A"), scan, file);

        var a = Tree(once);
        var b = Tree(twice);

        Assert.Equal(a.Keys.Order(), b.Keys.Order());
        foreach (var (rel, bytes) in a)
            Assert.True(bytes.SequenceEqual(b[rel]), $"{rel} is not deterministic");
    }
}
