using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Layout.PCells.Wire;
using CircuitRF.Ui.Messages;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout.PCells;

/// <summary>
/// B7: editing a generator script and seeing the change — the loop an author actually works in.
///
/// <para><b>Most of this closes a gap B5 opened.</b> A generated cell's folder name is a hash that now
/// includes the generator's own content, so editing a script moves every cell it produces. Nothing
/// repointed the instances that named the old folder, so an edit followed by a reopen would have left
/// the design full of Not Found placeholders with nothing saying why.</para>
/// </summary>
[Collection(PCellResolverCollection.Name)]
public sealed class PCellAuthoringLoopTests : IDisposable
{
    private readonly string _root;
    private readonly List<string> _reports = [];
    private readonly List<PCellWorkerResolver> _resolvers = [];

    public PCellAuthoringLoopTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-pcellauthor-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".cws"), "{}");
        PCellRegistry.ClearResolvers();
        CellLayoutResolver.InvalidateUnder(_root);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_root);
        PCellRegistry.ClearResolvers();
        foreach (var r in _resolvers) r.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private sealed class FakeMessageSink : IMessageSink
    {
        public List<(MessageLevel Level, string Text)> Posted { get; } = [];
        public void Post(MessageLevel level, string text, string? filePath = null) => Posted.Add((level, text));
        public void Clear() => Posted.Clear();
    }

    // ── An edited script must take effect, and must not strand what is placed ──

    /// <summary>
    /// <b>The headline.</b> Edit the script, regenerate, and the placed instance follows its cell to
    /// the new folder — with the new geometry actually in it. Without the repoint the instance keeps
    /// naming a folder that will never be built again.
    /// </summary>
    [PythonFact]
    public void EditingAScript_MovesThePlacedInstanceToTheNewlyGeneratedCell()
    {
        WriteKit("kit", length: 1000);
        var view = PlaceOneInstance(out string firstCellRef);

        WriteKit("kit", length: 2000);           // the same generator, different geometry
        NewResolver(rescanning: true);

        int moved = GeneratedCellsLifecycle.Regenerate(_root, view, _ => null, _reports.Add);

        Assert.Equal(1, moved);
        string after = view.Instances[0].CellRef;
        Assert.NotEqual(firstCellRef, after);

        // The instance resolves, and to the EDITED geometry — not merely to a different name.
        var resolved = CellLayoutResolver.Resolve(after, LayoutBaseDir());
        Assert.Equal(CellLayoutState.Resolved, resolved.State);
        Assert.Equal(2000, resolved.View!.Shapes.OfType<RectShape>().Single().X2);

        // The snapshot table is re-keyed with it, so the NEXT regeneration starts from the truth.
        Assert.Equal(Path.GetFileName(Path.TrimEndingDirectorySeparator(after)),
                     Assert.Single(view.PCellSnapshots).Key);
    }

    /// <summary>The ordinary case — nothing edited, nothing moves. A regeneration that repointed
    /// anyway would dirty every layout on every open.</summary>
    [PythonFact]
    public void AnUnchangedGenerator_MovesNothing()
    {
        WriteKit("kit", length: 1000);
        var view = PlaceOneInstance(out string cellRef);

        NewResolver(rescanning: true);
        Assert.Equal(0, GeneratedCellsLifecycle.Regenerate(_root, view, _ => null, _reports.Add));
        Assert.Equal(cellRef, view.Instances[0].CellRef);
    }

    /// <summary>The on-disk sweep, which is what runs at workspace open — the moment the reopen-after-
    /// an-edit case is actually hit.</summary>
    [PythonFact]
    public void RegenerateAll_RewritesTheLayoutOnDisk_AndReportsWhatMoved()
    {
        WriteKit("kit", length: 1000);
        var view = PlaceOneInstance(out string firstCellRef);
        string clayPath = ClayPath();
        Directory.CreateDirectory(Path.GetDirectoryName(clayPath)!);
        LayoutPersistence.SaveToFile(clayPath, view);

        WriteKit("kit", length: 2000);
        NewResolver(rescanning: true);

        var outcome = GeneratedCellsLifecycle.RegenerateAll(_root, _ => null, _reports.Add);

        Assert.Equal(1, outcome.InstancesRepointed);
        Assert.Equal(1, outcome.LayoutsRewritten);
        Assert.NotEqual(firstCellRef, LayoutPersistence.LoadFromFile(clayPath).Instances[0].CellRef);
    }

    /// <summary>A layout the caller is holding open is left alone on disk — rewriting the file under
    /// an open document would fight whatever is unsaved in it.</summary>
    [PythonFact]
    public void RegenerateAll_LeavesAnOpenLayoutsFileAlone()
    {
        WriteKit("kit", length: 1000);
        var view = PlaceOneInstance(out string firstCellRef);
        string clayPath = ClayPath();
        Directory.CreateDirectory(Path.GetDirectoryName(clayPath)!);
        LayoutPersistence.SaveToFile(clayPath, view);

        WriteKit("kit", length: 2000);
        NewResolver(rescanning: true);

        var outcome = GeneratedCellsLifecycle.RegenerateAll(
            _root, _ => null, _reports.Add, new HashSet<string> { Path.GetFullPath(clayPath) });

        Assert.Equal(0, outcome.LayoutsRewritten);
        Assert.Equal(firstCellRef, LayoutPersistence.LoadFromFile(clayPath).Instances[0].CellRef);
    }

    // ── The content hash has to be re-read, or the edit does nothing ───────────

    /// <summary>
    /// <b>The one that makes authoring iteration work at all.</b> The per-kit content hash is computed
    /// once and cached for the session, so a mid-session edit would otherwise keep the old key and
    /// resolve straight back to the cell the previous version wrote — the edit would appear to do
    /// nothing at all, which is the most confusing possible outcome.
    /// </summary>
    [PythonFact]
    public void Rescan_RereadsTheContentHash_SoAnEditedScriptResolvesSomewhereNew()
    {
        WriteKit("kit", length: 1000);
        var resolver = NewResolver();
        PCellRegistry.AddResolver(resolver);

        string before = PCellRegistry.GeneratorContentKey("TESTCELL");

        WriteKit("kit", length: 2000);
        resolver.Rescan();
        PCellRegistry.InvalidateResolved();

        Assert.NotEqual(before, PCellRegistry.GeneratorContentKey("TESTCELL"));
    }

    /// <summary>The deliberate counterpart: a permission change does NOT re-read the scripts, so a
    /// session-scoped fallback key stays stable and the same cells are not regenerated twice.</summary>
    [PythonFact]
    public void StopProviders_KeepsTheContentHash()
    {
        WriteKit("kit", length: 1000);
        var resolver = NewResolver();
        PCellRegistry.AddResolver(resolver);

        string before = PCellRegistry.GeneratorContentKey("TESTCELL");

        resolver.StopProviders();
        PCellRegistry.InvalidateResolved();

        Assert.Equal(before, PCellRegistry.GeneratorContentKey("TESTCELL"));
    }

    // ── Error surfacing ───────────────────────────────────────────────────────

    /// <summary>
    /// A generator is now somebody's own code, so it can fail. The failure has to reach the author
    /// with the script's own traceback attached — that traceback is usually the only description of
    /// what went wrong, and nobody thinks to open a log for it.
    /// </summary>
    [PythonFact]
    public void AScriptThatRaises_IsReportedWithItsOwnTraceback_AndTheInstanceIsUnchanged()
    {
        WriteKit("kit", length: 1000);
        var view = PlaceOneInstance(out string cellRef);

        WriteBrokenKit("kit");
        NewResolver(rescanning: true);

        var sink = new FakeMessageSink();
        var vm = new LayoutEditorViewModel(view, ClayPath(), sink);   // finds _root by the ancestor .cws walk

        bool ok = vm.EditInstancePCellParameters(0, new Dictionary<string, PCellValue> { ["W"] = 9e-6 });

        Assert.False(ok);
        Assert.Equal(cellRef, view.Instances[0].CellRef);   // the design is exactly as it was

        var error = Assert.Single(sink.Posted, m => m.Level == MessageLevel.Error);
        Assert.Contains("TESTCELL", error.Text);
        Assert.Contains("deliberate authoring error", error.Text);   // the script's own words
    }

    /// <summary>A snapshot whose generator will not run is reported and SKIPPED — one broken kit must
    /// not stop the rest of the workspace opening, and the snapshot must survive so the cell comes
    /// back once the script is fixed.</summary>
    [PythonFact]
    public void AFailingGenerator_IsReported_AndItsSnapshotSurvives()
    {
        WriteKit("kit", length: 1000);
        var view = PlaceOneInstance(out string cellRef);
        string snapshotKey = view.PCellSnapshots.Keys.Single();

        WriteBrokenKit("kit");
        NewResolver(rescanning: true);

        GeneratedCellsLifecycle.Regenerate(_root, view, _ => null, _reports.Add);

        Assert.Contains(_reports, r => r.Contains("could not be rebuilt") && r.Contains("TESTCELL"));
        Assert.Equal(cellRef, view.Instances[0].CellRef);
        Assert.Equal(snapshotKey, view.PCellSnapshots.Keys.Single());
    }

    // ── R9: generated geometry is read-only, script cells included ────────────

    /// <summary>R9 predates scripts, and it applies to them unchanged — a script-generated cell is an
    /// ordinary generated cell, marked by the same <see cref="LayoutView.PCellOrigin"/>.</summary>
    [PythonFact]
    public void AScriptGeneratedCell_IsReadOnly_LikeABuiltInsIs()
    {
        WriteKit("kit", length: 1000);
        NewResolver();
        string cellDir = GenerateCell();

        var generated = LayoutPersistence.LoadFromFile(
            Path.Combine(cellDir, "layout", Path.GetFileName(cellDir) + ".clay"));

        Assert.NotNull(generated.PCellOrigin);
        Assert.Equal("TESTCELL", generated.PCellOrigin!.GeneratorId);

        var sink = new FakeMessageSink();
        var vm = new LayoutEditorViewModel(generated, null, sink);
        Assert.True(vm.IsPCellReadOnly);

        vm.Execute(new Commands.Layout.AddShapeCommand(generated, new RectShape { X2 = 1, Y2 = 1 }));

        Assert.Single(generated.Shapes.OfType<RectShape>());          // the added shape was refused
        Assert.Contains(sink.Posted, m => m.Level == MessageLevel.Warning);
    }

    // ── Determinism ───────────────────────────────────────────────────────────

    /// <summary>
    /// <b>Determinism is a stated rule because its failure is silent and cache-poisoning.</b> The
    /// generated cell's folder name is a hash of its inputs; if identical inputs produced different
    /// geometry, two users would sit on the same cell name holding different artwork and nothing would
    /// ever notice. Generated from two SEPARATE interpreter processes, so anything address- or
    /// session-derived leaking into the output path shows up here.
    ///
    /// <para>What this cannot cover, stated rather than implied: two DIFFERENT Python versions. Only
    /// one interpreter exists in this environment. The rule is documented for kit authors in the
    /// package README, and the ordering guarantee below is the part of it circuitRF itself owns.</para>
    /// </summary>
    [PythonFact]
    public void TheSameInputs_ProduceByteIdenticalGeometry_AcrossSeparateProcesses()
    {
        WriteKit("kit", length: 1000);

        NewResolver();
        string first = File.ReadAllText(ClayOf(GenerateCell()));

        // A second resolver is a second interpreter process, started from scratch.
        Directory.Delete(Path.Combine(_root, GeneratedCellStore.ReservedFolderName), recursive: true);
        PCellRegistry.ClearResolvers();
        NewResolver(rescanning: true);
        string second = File.ReadAllText(ClayOf(GenerateCell()));

        Assert.Equal(first, second);
    }

    /// <summary>Shapes come back in the order the script returned them. An ordering leak — a set
    /// iteration, an address-derived sort — would change the geometry silently while every parameter
    /// stayed identical.</summary>
    [PythonFact]
    public void ShapeOrder_IsExactlyWhatTheScriptReturned()
    {
        WriteOrderedKit("ordered");
        NewResolver();

        PCellRegistry.AddResolver(_resolvers[^1]);
        Assert.True(PCellRegistry.TryGet("ORDERED", out var generator));

        for (int attempt = 0; attempt < 3; attempt++)
        {
            var result = generator(new Dictionary<string, PCellValue>(), null, PCellLayerSelection.Default);
            Assert.Equal([0L, 1000L, 2000L, 3000L], result.Shapes.OfType<RectShape>().Select(r => r.X1));
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private PCellWorkerResolver NewResolver(bool rescanning = false)
    {
        var resolver = new PCellWorkerResolver(_root,
            (_, _) => new PythonInterpreter(PythonRunner.Interpreter ?? "python3", [], "test", "supplied by the test"),
            _reports.Add);

        if (rescanning)
        {
            PCellRegistry.ClearResolvers();
            foreach (var old in _resolvers) old.Dispose();
            _resolvers.Clear();
        }

        _resolvers.Add(resolver);
        PCellRegistry.AddResolver(resolver);
        return resolver;
    }

    private string LayoutBaseDir() => Path.Combine(_root, "Doc", "layout");
    private string ClayPath() => Path.Combine(LayoutBaseDir(), "main.clay");
    private static string ClayOf(string cellDir)
        => Path.Combine(cellDir, "layout", Path.GetFileName(cellDir) + ".clay");

    private string GenerateCell() => GeneratedCellStore.GetOrCreate(
        _root, "TESTCELL", new Dictionary<string, PCellValue> { ["W"] = 300e-6 },
        null, null, PCellLayerSelection.Default);

    /// <summary>A layout holding one instance of one script-generated cell, with the snapshot every
    /// regeneration works from — exactly what a placement leaves behind.</summary>
    private LayoutView PlaceOneInstance(out string cellRef)
    {
        if (_resolvers.Count == 0) NewResolver();
        string cellDir = GenerateCell();
        var view = new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 };

        cellRef = Path.GetRelativePath(LayoutBaseDir(), cellDir);
        view.Instances.Add(new LayoutInstance { CellRef = cellRef, X = 0, Y = 0, Mag = 1.0 });
        GeneratedCellStore.RecordSnapshot(
            view, cellDir, "TESTCELL", new Dictionary<string, PCellValue> { ["W"] = 300e-6 },
            null, PCellLayerSelection.Default);
        return view;
    }

    private string WriteKit(string name, long length)
        => WriteScript(name, $"""
            @generator("TESTCELL", [Parameter.length("W")])
            def testcell(params, tech):
                w = params.length("W")
                return Result(shapes=[Rect(tech.signal_layer or Layer(1, 0), 0, 0, {length}, w)], pins=[])
            """);

    private string WriteBrokenKit(string name)
        => WriteScript(name, """
            @generator("TESTCELL", [Parameter.length("W")])
            def testcell(params, tech):
                raise ValueError("deliberate authoring error")
            """);

    private string WriteOrderedKit(string name)
        => WriteScript(name, """
            @generator("ORDERED", [])
            def ordered(params, tech):
                layer = tech.signal_layer or Layer(1, 0)
                return Result(shapes=[Rect(layer, i * 1000, 0, i * 1000 + 500, 500) for i in range(4)],
                              pins=[])
            """);

    private string WriteScript(string name, string body)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);

        string package = PythonRunner.PackageRoot;
        File.WriteAllText(Path.Combine(dir, "main.py"), $"""
            import sys
            sys.path.insert(0, r'{package}')
            from circuitrf_pcell import Layer, Parameter, Rect, Result, generator, run

            {body}

            run()
            """);

        File.WriteAllText(Path.Combine(dir, PCellGeneratorManifest.FileName),
            $$"""{ "schemaVersion": 1, "entry": "main.py", "pythonPath": [{{System.Text.Json.JsonSerializer.Serialize(package)}}] }""");
        return dir;
    }
}
