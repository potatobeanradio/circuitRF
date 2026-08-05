using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Layout.PCells.Wire;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout.PCells;

/// <summary>
/// Caching and invalidation for generator scripts.
///
/// <para><b>The failure this closes has already happened in this codebase once.</b>
/// <c>GeneratedCellStore</c> originally keyed a generated cell on
/// <c>(generator, parameters, technology, layers)</c> alone, so fixing a generator's own geometry bug
/// never invalidated the cells the buggy version had written — the fix landed, the tests passed, and
/// the artwork did not change. Built-ins solved that with a hand-maintained version number. A
/// generator that is a FILE THE USER EDITS cannot have one, so the number becomes a hash of the file.</para>
/// </summary>
[Collection(PCellResolverCollection.Name)]
public sealed class PCellGeneratorCachingTests : IDisposable
{
    private readonly string _root;
    private readonly List<string> _reports = [];
    private readonly List<PCellWorkerResolver> _resolvers = [];

    public PCellGeneratorCachingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-pcellcache-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".cws"), "{}");
        PCellRegistry.ClearResolvers();
    }

    public void Dispose()
    {
        PCellRegistry.ClearResolvers();
        foreach (var r in _resolvers) r.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ── Back-compat: every existing workspace must keep its cells ─────────────

    /// <summary>
    /// <b>The compatibility gate.</b> The generator's contribution to a built-in's content hash is
    /// byte-identical to what it was before content keys existed, so every generated cell in every
    /// existing workspace keeps its folder name and every already-placed instance keeps resolving.
    /// Compared against the literal previous expression, not a constant copied out of the new code.
    /// </summary>
    [Theory]
    [InlineData("MLIN")]
    [InlineData("MTEE")]   // the one built-in carrying a bumped version
    [InlineData("MKLOPF")]
    public void ABuiltInsContentKey_IsExactlyItsOldVersionNumber(string generatorId)
        => Assert.Equal(PCellRegistry.GeneratorVersion(generatorId).ToString(),
                        PCellRegistry.GeneratorContentKey(generatorId));

    // ── Editing a generator invalidates its cells ─────────────────────────────

    /// <summary>
    /// <b>The point of the phase.</b> Edit the script, and the same parameters resolve to a different
    /// generated cell — so the edit is actually seen instead of being masked by the cell the previous
    /// version wrote.
    /// </summary>
    [PythonFact]
    public void EditingAGeneratorScript_ResolvesToADifferentCell()
    {
        string kit = WriteKit("kit", Wide: false);
        string before = ResolveCell(kit, "TESTCELL");

        WriteKit("kit", Wide: true);          // the same generator, emitting different geometry
        string after = ResolveCell(kit, "TESTCELL", fresh: true);

        Assert.NotEqual(Path.GetFileName(before), Path.GetFileName(after));

        // And the new cell genuinely carries the new geometry, rather than merely a new name.
        var view = CellLayoutResolver.Resolve(after, _root).View!;
        Assert.Equal(2000, view.Shapes.OfType<RectShape>().Single().X2);
    }

    /// <summary>An unedited kit keeps its cells — the hash must not depend on anything that changes
    /// by itself, or every open would regenerate everything.</summary>
    [PythonFact]
    public void AnUnchangedGenerator_KeepsItsCellsAcrossSessions()
    {
        string kit = WriteKit("kit", Wide: false);
        string first = ResolveCell(kit, "TESTCELL");
        string second = ResolveCell(kit, "TESTCELL", fresh: true); // a new resolver = a new session
        Assert.Equal(first, second);
    }

    /// <summary>
    /// A DECLARED data file is part of the key. A generator that reads a file is fine provided that
    /// file's content is part of its key — this is how it becomes part of it.
    /// </summary>
    [PythonFact]
    public void ChangingADeclaredDataFile_InvalidatesTheCells()
    {
        string kit = WriteKit("kit", Wide: false, dataFile: "table.csv");
        File.WriteAllText(Path.Combine(kit, "table.csv"), "pad,50\n");
        string before = ResolveCell(kit, "TESTCELL");

        File.WriteAllText(Path.Combine(kit, "table.csv"), "pad,80\n");
        string after = ResolveCell(kit, "TESTCELL", fresh: true);

        Assert.NotEqual(before, after);
    }

    /// <summary>The manifest itself counts: changing the entry point or the declared data is a change
    /// to what the generators ARE, even with every script untouched.</summary>
    [PythonFact]
    public void ChangingTheManifest_InvalidatesTheCells()
    {
        string kit = WriteKit("kit", Wide: false);
        string before = ResolveCell(kit, "TESTCELL");

        File.WriteAllText(Path.Combine(kit, PCellGeneratorManifest.FileName),
            """{ "schemaVersion": 1, "entry": "main.py", "dataFiles": [] }""");
        string after = ResolveCell(kit, "TESTCELL", fresh: true);

        Assert.NotEqual(before, after);
    }

    // ── The hash itself ───────────────────────────────────────────────────────

    /// <summary>
    /// Moving or copying a kit must NOT change the hash. Kits are routinely copied, and regenerating
    /// every cell in a workspace because a folder moved would be a cost with no cause.
    /// </summary>
    [Fact]
    public void MovingAKitDoesNotChangeItsHash()
    {
        string a = WriteKit("here", Wide: false);
        string b = WriteKit("elsewhere", Wide: false);

        string hashA = PCellGeneratorContentHash.Compute(a, PCellGeneratorManifest.TryRead(a, out _)!, out _);
        string hashB = PCellGeneratorContentHash.Compute(b, PCellGeneratorManifest.TryRead(b, out _)!, out _);

        Assert.Equal(hashA, hashB);
        Assert.NotEmpty(hashA);
    }

    /// <summary>Renaming a file is a change: which module a script imports can turn on it.</summary>
    [Fact]
    public void RenamingASourceFile_ChangesTheHash()
    {
        string kit = WriteKit("kit", Wide: false);
        var manifest = PCellGeneratorManifest.TryRead(kit, out _)!;
        File.WriteAllText(Path.Combine(kit, "helper.py"), "VALUE = 1\n");
        string before = PCellGeneratorContentHash.Compute(kit, manifest, out _);

        File.Move(Path.Combine(kit, "helper.py"), Path.Combine(kit, "renamed.py"));
        string after = PCellGeneratorContentHash.Compute(kit, manifest, out _);

        Assert.NotEqual(before, after);
    }

    /// <summary>
    /// A declared source set too large to be a kit's scripts is REPORTED and gets no stable key —
    /// and the resolver then regenerates rather than reusing. A partial hash presented as a complete
    /// one would look stable and not be, which is the worst available answer.
    /// </summary>
    [Fact]
    public void AnImplausiblyLargeSourceSet_IsRefusedRatherThanPartiallyHashed()
    {
        string kit = WriteKit("kit", Wide: false);
        string big = Path.Combine(kit, "toobig");
        Directory.CreateDirectory(big);
        for (int i = 0; i <= PCellGeneratorContentHash.MaxFiles + 1; i++)
            File.WriteAllText(Path.Combine(big, $"m{i}.py"), "x = 1\n");

        var manifest = PCellGeneratorManifest.TryRead(kit, out _)!;
        string hash = PCellGeneratorContentHash.Compute(kit, manifest, out var problem);

        Assert.Empty(hash);
        Assert.NotNull(problem);
        Assert.Contains("regenerated every session", problem!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>And when there is no stable key, cells are regenerated rather than reused — the safe
    /// direction, verified through the resolver rather than assumed from the hash alone.</summary>
    [PythonFact]
    public void WithNoStableKey_CellsAreRegeneratedRatherThanReused()
    {
        string kit = WriteKit("kit", Wide: false);
        string big = Path.Combine(kit, "toobig");
        Directory.CreateDirectory(big);
        for (int i = 0; i <= PCellGeneratorContentHash.MaxFiles + 1; i++)
            File.WriteAllText(Path.Combine(big, $"m{i}.py"), "x = 1\n");

        string first = ResolveCell(kit, "TESTCELL");
        string second = ResolveCell(kit, "TESTCELL", fresh: true);

        Assert.NotEqual(first, second);
        Assert.Contains(_reports, r => r.Contains("regenerated every session", StringComparison.OrdinalIgnoreCase));
    }

    // ── Why there is no warm process pool ─────────────────────────────────────

    /// <summary>
    /// <b>A warm process pool was scoped for this phase and deliberately not built; this is the
    /// measurement that says why.</b> The concern it addressed was per-cell interpreter startup — but
    /// one process serves a kit for the whole session, so generating many cells starts exactly ONE.
    ///
    /// <para>Measured on this machine: startup + describe ≈ 53 ms, paid once; 200 distinct generates
    /// ≈ 4.0 ms in total, about 0.02 ms each. A pool could parallelise those 4 ms and nothing else.
    /// Gated on the PROCESS COUNT rather than the timing, per this repo's own convention that
    /// counters are the gate and wall-clock is only ever a recorded number — a timing assertion here
    /// could not survive the parallel-start burst of a full-solution run.</para>
    /// </summary>
    [PythonFact]
    public void GeneratingManyCells_StartsExactlyOneProcess()
    {
        string kit = WriteKit("kit", Wide: false);
        var resolver = NewResolver();

        int before = ProcessPCellWorkerTransport.StartCount;
        var generator = resolver.Resolve("TESTCELL");
        Assert.NotNull(generator);

        for (int i = 1; i <= 200; i++)
            generator!(new Dictionary<string, PCellValue> { ["W"] = i * 1e-6 }, null, PCellLayerSelection.Default);

        Assert.Equal(1, ProcessPCellWorkerTransport.StartCount - before);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private PCellWorkerResolver NewResolver()
    {
        var resolver = new PCellWorkerResolver(_root,
            (_, _) => new PythonInterpreter(PythonRunner.Interpreter!, [], "test", "supplied by the test"),
            _reports.Add);
        _resolvers.Add(resolver);
        return resolver;
    }

    /// <summary>Generates a cell through the store, exactly as a placement would.
    /// <paramref name="fresh"/> stands in for a new session — a new resolver re-reads the scripts.</summary>
    private string ResolveCell(string kitDir, string generatorId, bool fresh = false)
    {
        _ = kitDir;
        PCellRegistry.ClearResolvers();
        var resolver = fresh || _resolvers.Count == 0 ? NewResolver() : _resolvers[^1];
        if (fresh) resolver.Rescan();
        PCellRegistry.AddResolver(resolver);

        return GeneratedCellStore.GetOrCreate(
            _root, generatorId,
            new Dictionary<string, PCellValue> { ["W"] = 300e-6 },
            null, null, PCellLayerSelection.Default);
    }

    /// <summary>
    /// A kit whose one generator emits a rect. <paramref name="Wide"/> changes the GEOMETRY the
    /// script produces for identical parameters — which is exactly the edit a content hash exists to
    /// notice, and which nothing else in the key can see.
    /// </summary>
    private string WriteKit(string name, bool Wide, string? dataFile = null)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);

        string package = PythonRunner.PackageRoot;
        long length = Wide ? 2000 : 1000;
        File.WriteAllText(Path.Combine(dir, "main.py"), $"""
            import sys
            sys.path.insert(0, r'{package}')
            from circuitrf_pcell import Layer, Parameter, Rect, Result, generator, run

            @generator("TESTCELL", [Parameter.length("W")])
            def testcell(params, tech):
                w = params.length("W")
                return Result(shapes=[Rect(tech.signal_layer or Layer(1, 0), 0, 0, {length}, w)], pins=[])

            run()
            """);

        string data = dataFile is null ? "" : $", \"dataFiles\": [\"{dataFile}\"]";
        File.WriteAllText(Path.Combine(dir, PCellGeneratorManifest.FileName),
            $$"""{ "schemaVersion": 1, "entry": "main.py", "pythonPath": [{{System.Text.Json.JsonSerializer.Serialize(package)}}]{{data}} }""");
        return dir;
    }
}
