using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The generated-cell folder survives a workspace close and open, and is kept honest by a prune pass
/// rather than by a wipe — see <see cref="GeneratedCellsLifecycle.WipeOnOpenAndClose"/> for why that
/// changed. These are the four facts the switch-over rests on:
///
/// <list type="number">
/// <item>a cell already on disk is REUSED, not regenerated, when the layout is walked again;</item>
/// <item>a cell no layout names any more is COLLECTED;</item>
/// <item>the prune refuses to run when its view of the workspace is incomplete;</item>
/// <item>editing the technology in place invalidates the cells drawn against it — the one thing the
/// old wipe-on-every-open was silently covering, since the cell name keyed on the .ctech PATH.</item>
/// </list>
///
/// <para>The original R-L5g-7 policy is still reachable and still tested, at the bottom: the user
/// asked for the wipe path to be kept working, not merely kept present.</para>
/// </summary>
[Collection("GeneratedCellWipePolicy")]
public sealed class GeneratedCellCachePersistenceTests : IDisposable
{
    private readonly string _root;
    private readonly bool _wipeWas = GeneratedCellsLifecycle.WipeOnOpenAndClose;

    public GeneratedCellCachePersistenceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-gencache-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".cws"), "{}");
        CellLayoutResolver.InvalidateUnder(_root);
    }

    public void Dispose()
    {
        GeneratedCellsLifecycle.WipeOnOpenAndClose = _wipeWas;
        CellLayoutResolver.InvalidateUnder(_root);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static readonly PCellLayerSelection NoLayers = new(null, null);

    private static Dictionary<string, PCellValue> MlinParams(double widthMetres) => new()
    {
        ["W"] = PCellValue.Real(widthMetres),
        ["L"] = PCellValue.Real(0.005),
    };

    private string GenRoot => Path.Combine(_root, GeneratedCellStore.ReservedFolderName);

    /// <summary>Writes a layout naming <paramref name="cells"/>, and returns its path.</summary>
    private string WriteLayoutNaming(string cellFolder, params (string Cell, IReadOnlyDictionary<string, PCellValue> P)[] cells)
    {
        string cellDir  = CellFolder.CreateCellFolder(_root, cellFolder);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        string clayPath  = Path.Combine(layoutDir, cellFolder + ".clay");

        var view = new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 };
        foreach (var (name, p) in cells)
        {
            string dir = GeneratedCellStore.GetOrCreate(_root, "MLIN", p, null, null, NoLayers);
            GeneratedCellStore.RecordSnapshot(view, dir, "MLIN", p, null, NoLayers);
            view.Instances.Add(new LayoutInstance { CellRef = Path.GetFileName(dir), SchematicId = name });
        }

        LayoutPersistence.SaveToFile(clayPath, view);
        return clayPath;
    }

    [Fact]
    public void ACellAlreadyOnDisk_IsReused_NotRegenerated()
    {
        WriteLayoutNaming("Amp", ("X1", MlinParams(0.0006)));

        int writtenAfterFirst = GeneratedCellStore.CellsWrittenUnder(_root);
        Assert.Equal(1, writtenAfterFirst);

        // "Open the workspace again" — the walk that used to follow a full wipe.
        var outcome = GeneratedCellsLifecycle.RegenerateAll(_root, _ => null);

        Assert.Equal(writtenAfterFirst, GeneratedCellStore.CellsWrittenUnder(_root));  // nothing re-generated
        Assert.Equal(0, outcome.InstancesRepointed);
        Assert.Equal(0, outcome.CellsPruned);
        Assert.Single(Directory.EnumerateDirectories(GenRoot));
    }

    [Fact]
    public void ACellNoLayoutNamesAnyMore_IsPruned()
    {
        WriteLayoutNaming("Amp", ("X1", MlinParams(0.0006)));

        // A cell nothing references — what an edited generator or parameter leaves behind.
        string orphan = GeneratedCellStore.GetOrCreate(
            _root, "MLIN", MlinParams(0.0011), null, null, NoLayers);
        Assert.Equal(2, Directory.EnumerateDirectories(GenRoot).Count());

        var outcome = GeneratedCellsLifecycle.RegenerateAll(_root, _ => null);

        Assert.Equal(1, outcome.CellsPruned);
        Assert.False(Directory.Exists(orphan));
        Assert.Single(Directory.EnumerateDirectories(GenRoot));
    }

    [Fact]
    public void ThePrune_RefusesToRun_WhenALayoutIsHeldOutOfTheWalk()
    {
        // A layout the caller is holding open in memory is not walked, so its cells are absent from
        // the live set. Pruning on that view would delete artwork the open document is using.
        string clayPath = WriteLayoutNaming("Amp", ("X1", MlinParams(0.0006)));
        var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(clayPath) };

        var outcome = GeneratedCellsLifecycle.RegenerateAll(_root, _ => null, report: null, skipPaths: held);

        Assert.Equal(0, outcome.CellsPruned);
        Assert.Single(Directory.EnumerateDirectories(GenRoot));
    }

    [Fact]
    public void ThePrune_RefusesToRun_WhenALayoutCouldNotBeRead()
    {
        WriteLayoutNaming("Amp", ("X1", MlinParams(0.0006)));

        // A second layout that will not parse. Its snapshots are unknown, so the live set is
        // incomplete and nothing may be collected on the strength of it.
        string brokenDir = CellFolder.SubFolderPath(CellFolder.CreateCellFolder(_root, "Broken"), ViewType.Layout);
        File.WriteAllText(Path.Combine(brokenDir, "Broken.clay"), "{ this is not a layout");

        string orphan = GeneratedCellStore.GetOrCreate(
            _root, "MLIN", MlinParams(0.0011), null, null, NoLayers);

        var outcome = GeneratedCellsLifecycle.RegenerateAll(_root, _ => null);

        Assert.Equal(0, outcome.CellsPruned);
        Assert.True(Directory.Exists(orphan));
    }

    /// <summary>
    /// A minimal <c>.ctech</c> carrying one layer, so a single field can be varied against a fixed
    /// rest. Written as text rather than built from <c>Technology</c> because what is under test is
    /// exactly which parts of the FILE are stamped.
    /// </summary>
    private static string Ctech(bool visible = true, bool selectable = true, int red = 255,
                                int zOrder = 0, long snapDbu = 1000)
        => "{\n" +
           "  \"FormatVersion\": 1,\n" +
           "  \"Name\": \"T\",\n" +
           $"  \"DefaultSnapDbu\": {snapDbu},\n" +
           "  \"Layers\": [\n" +
           "    {\n" +
           "      \"Key\": { \"Layer\": 40, \"Datatype\": 0 },\n" +
           "      \"Name\": \"Metal1.drawing\",\n" +
           $"      \"Color\": {{ \"r\": {red}, \"g\": 0, \"b\": 0, \"a\": 255 }},\n" +
           "      \"FillOpacity\": 0.35,\n" +
           $"      \"ZOrder\": {zOrder},\n" +
           $"      \"Visible\": {(visible ? "true" : "false")},\n" +
           $"      \"Selectable\": {(selectable ? "true" : "false")},\n" +
           "      \"Purpose\": \"drawing\"\n" +
           "    }\n" +
           "  ],\n" +
           "  \"FillPatterns\": [],\n" +
           "  \"Stackup\": { \"Layers\": [] },\n" +
           "  \"DrcRules\": []\n" +
           "}\n";

    /// <summary>
    /// The cell name <paramref name="ctech"/> produces for one fixed set of parameters.
    ///
    /// <para>Always the SAME file, edited in place — the identity (the path) is part of the cell name
    /// in its own right, so writing each variant to its own file would compare two things that differ
    /// for a reason this test is not about. The write time is stepped explicitly because the content
    /// stamp is memoized on it.</para>
    /// </summary>
    private int _techEdits;

    private string NameUnder(string ctech)
    {
        string techPath = Path.Combine(_root, "process.ctech");
        File.WriteAllText(techPath, ctech);
        File.SetLastWriteTimeUtc(techPath, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(++_techEdits));
        return Path.GetFileName(GeneratedCellStore.GetOrCreate(
            _root, "MLIN", MlinParams(0.0006), null, techPath, NoLayers));
    }

    [Theory]
    // What a user toggles while looking at a design. Renaming every generated cell for one of these
    // would regenerate the lot and rewrite every layout that places them — for a change that cannot
    // reach the artwork, since a generated shape carries a layer KEY and the renderer resolves
    // colour, stipple, opacity, draw order, visibility and selectability live.
    [InlineData("hidden")]
    [InlineData("locked")]
    [InlineData("recoloured")]
    [InlineData("reordered")]
    public void HowALayerIsDRAWN_DoesNotChangeTheCellName(string change)
    {
        string baseline = NameUnder(Ctech());

        string edited = change switch
        {
            "hidden"     => NameUnder(Ctech(visible: false)),
            "locked"     => NameUnder(Ctech(selectable: false)),
            "recoloured" => NameUnder(Ctech(red: 12)),
            "reordered"  => NameUnder(Ctech(zOrder: 7)),
            _            => throw new ArgumentOutOfRangeException(nameof(change)),
        };

        Assert.Equal(baseline, edited);
    }

    [Fact]
    public void WhatTheProcessSAYS_DoesChangeTheCellName()
    {
        // The other side of the same rule: a field a generator can actually consume must invalidate.
        Assert.NotEqual(NameUnder(Ctech()), NameUnder(Ctech(snapDbu: 500)));
    }

    [Fact]
    public void ReformattingTheFile_DoesNotChangeTheCellName()
    {
        // TechPersistence may rewrite the whole file on any save. Only what it SAYS is stamped.
        string dense = Ctech().Replace("\n", "").Replace("  ", "");
        Assert.Equal(NameUnder(Ctech()), NameUnder(dense));
    }

    [Fact]
    public void EditingTheTechnologyInPlace_ChangesTheCellName_SoTheOldArtworkIsNeverReused()
    {
        // The identity is the .ctech PATH, and it does not change when the file behind it does. What
        // has to change is the cell's own name; otherwise a technology edit resolves back to artwork
        // drawn against the layers as they were.
        string techPath = Path.Combine(_root, "edited-in-place.ctech");
        File.WriteAllText(techPath, Ctech());

        var p = MlinParams(0.0006);
        string before = GeneratedCellStore.GetOrCreate(_root, "MLIN", p, null, techPath, NoLayers);

        File.WriteAllText(techPath, Ctech(snapDbu: 250));
        File.SetLastWriteTimeUtc(techPath, DateTime.UtcNow.AddSeconds(1));   // the stat the memo keys on

        string after = GeneratedCellStore.GetOrCreate(_root, "MLIN", p, null, techPath, NoLayers);

        Assert.NotEqual(Path.GetFileName(before), Path.GetFileName(after));
    }

    [Fact]
    public void AnIdentityThatIsNotAReadableFile_KeepsTheNameItAlreadyHad()
    {
        // Every existing workspace with no technology, and every fixture that passes an arbitrary
        // identity string, must keep resolving to the cell folder its instances already name.
        var p = MlinParams(0.0006);
        string withNone   = GeneratedCellStore.GetOrCreate(_root, "MLIN", p, null, null, NoLayers);
        string withGhost  = GeneratedCellStore.GetOrCreate(_root, "MLIN", p, null, "", NoLayers);

        Assert.Equal(Path.GetFileName(withNone), Path.GetFileName(withGhost));
    }

    [Fact]
    public void TheOriginalWipePolicy_StillWorks_WhenTurnedBackOn()
    {
        GeneratedCellsLifecycle.WipeOnOpenAndClose = true;

        WriteLayoutNaming("Amp", ("X1", MlinParams(0.0006)));
        Assert.Single(Directory.EnumerateDirectories(GenRoot));

        // "Close": the whole folder goes.
        GeneratedCellsLifecycle.DeleteGeneratedCellsFolder(_root);
        Assert.False(Directory.Exists(GenRoot));

        // "Open": everything the layout names is rebuilt from its own snapshots, and the prune stays
        // out of the way — there is nothing for it to collect after a wipe.
        var outcome = GeneratedCellsLifecycle.RegenerateAll(_root, _ => null);

        Assert.Equal(0, outcome.CellsPruned);
        Assert.Single(Directory.EnumerateDirectories(GenRoot));
    }
}

/// <summary>
/// Run alone. <see cref="GeneratedCellsLifecycle.WipeOnOpenAndClose"/> is process-wide, and the last
/// test here flips it — under a parallel run that would silently disarm the prune pass inside
/// whatever else happened to be walking a workspace at the time. Serialising is cheaper than making
/// a policy flag thread-scoped for the sake of one test.
/// </summary>
[CollectionDefinition("GeneratedCellWipePolicy", DisableParallelization = true)]
public sealed class GeneratedCellWipePolicyCollection;
