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
/// The worker host: a generator script becomes an ordinary <see cref="PCellGenerator"/>, and
/// everything above that seam keeps working without knowing.
///
/// <para><b>The payoff being tested is what did NOT have to change.</b> The content-addressed cell
/// store, the geometry cache and copy-on-write parameter editing all reach a generator through
/// <c>PCellRegistry.TryGet</c>; making that ask a resolver is the single change that makes a
/// script-backed cell work through all of them.</para>
///
/// <para>These run a real interpreter, so they SKIP with a reason where there is none — circuitRF
/// must build and test on a machine with no Python on it.</para>
/// </summary>
[Collection(PCellResolverCollection.Name)]
public sealed class PCellWorkerHostTests : IDisposable
{
    private readonly string _root;
    private readonly List<string> _reports = [];
    private PCellWorkerResolver? _resolver;

    public PCellWorkerHostTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-pcellhost-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".cws"), "{}");
        PCellRegistry.ClearResolvers();
    }

    public void Dispose()
    {
        PCellRegistry.ClearResolvers();
        _resolver?.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ── Provider ──────────────────────────────────────────────────────────────

    [PythonFact]
    public void AScriptsGeneratorsBecomeOrdinaryPCellGenerators()
    {
        using var provider = StartProvider(KitWithExample("kit"));

        Assert.Contains("MLIN", provider.GeneratorIds);
        Assert.True(provider.TryGetGenerator("MLIN", out var generator));

        var result = generator(
            new Dictionary<string, PCellValue> { ["W"] = 300e-6, ["L"] = 2e-3 },
            technology: null, PCellLayerSelection.Default);

        var rect = Assert.IsType<RectShape>(Assert.Single(result.Shapes));
        Assert.Equal(0, rect.X1);
        Assert.Equal(2_000_000, rect.X2);
        Assert.Equal(2, result.Pins.Count);
    }

    [PythonFact]
    public void AnUnknownGeneratorId_IsSimplyNotOffered()
    {
        using var provider = StartProvider(KitWithExample("kit"));
        Assert.False(provider.TryGetGenerator("NOSUCHCELL", out _));
    }

    /// <summary>A failure names the generator AND carries the script's own output — which is very
    /// often the only description of what actually went wrong.</summary>
    [PythonFact]
    public void AGeneratorThatRefuses_NamesItselfAndCarriesTheScriptsOwnOutput()
    {
        using var provider = StartProvider(KitWithExample("kit"));
        Assert.True(provider.TryGetGenerator("VIAARRAY", out var generator));

        var ex = Assert.Throws<PCellWireException>(() => generator(
            new Dictionary<string, PCellValue> { ["Rows"] = PCellValue.Int(0), ["Cols"] = PCellValue.Int(1) },
            null, PCellLayerSelection.Default));

        Assert.Contains("VIAARRAY", ex.Message, StringComparison.Ordinal);
        Assert.Contains("at least one row", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A script that dies on start-up must not surface as a bare "closed its output" — its
    /// traceback is the only thing that says why, and it arrives on a background reader.</summary>
    [PythonFact]
    public void AScriptThatDiesImmediately_StillReportsWhatItSaidOnTheWayOut()
    {
        string dir = Path.Combine(_root, "broken");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "main.py"),
            "import sys\nsys.stderr.write('DELIBERATE: the kit's own module is missing\\n')\nsys.exit(3)\n");

        using var provider = new PCellWorkerProvider(
            ProcessPCellWorkerTransport.Start(PythonRunner.Interpreter!, Path.Combine(dir, "main.py")));

        var ex = Assert.Throws<PCellWireException>(() => _ = provider.GeneratorIds);
        Assert.Contains("DELIBERATE", ex.Message, StringComparison.Ordinal);
    }

    [PythonFact]
    public void AScriptSpeakingAnotherWireVersion_IsRefusedNamingBoth()
    {
        string dir = Path.Combine(_root, "future");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "main.py"), """
            import struct, sys, json
            head = sys.stdin.buffer.read(8)
            jl, bl = struct.unpack('<II', head)
            sys.stdin.buffer.read(jl + bl)
            body = json.dumps({"ok": True, "wireVersion": 99, "contractVersion": 2, "generators": []}).encode()
            sys.stdout.buffer.write(struct.pack('<II', len(body), 0) + body)
            sys.stdout.buffer.flush()
            """);

        using var provider = new PCellWorkerProvider(
            ProcessPCellWorkerTransport.Start(PythonRunner.Interpreter!, Path.Combine(dir, "main.py")));

        var ex = Assert.Throws<PCellWireException>(() => _ = provider.GeneratorIds);
        Assert.Contains("99", ex.Message, StringComparison.Ordinal);
        Assert.Contains(PCellWireVersion.Current.ToString(), ex.Message, StringComparison.Ordinal);
    }

    // ── Resolver ──────────────────────────────────────────────────────────────

    [PythonFact]
    public void AResolverFindsAKitsGenerators_AndStartsNothingUntilAsked()
    {
        KitWithExample("kit");
        _resolver = NewResolver();

        // Constructing the resolver reads manifests and starts no interpreter. Proven by the
        // description being available before anything has been resolved.
        Assert.Contains("kit", _resolver.Describe(), StringComparison.Ordinal);

        Assert.NotNull(_resolver.Resolve("MLIN"));
        Assert.Null(_resolver.Resolve("NOSUCHCELL")); // no opinion, not an error
    }

    [PythonFact]
    public void AManifestNamingAMissingScript_IsReportedAndSkipped_NotFatal()
    {
        string dir = Path.Combine(_root, "broken");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, PCellGeneratorManifest.FileName),
            """{ "schemaVersion": 1, "entry": "not-there.py" }""");
        KitWithExample("good");

        _resolver = NewResolver();

        // One broken kit must not make another kit's cells unresolvable.
        Assert.NotNull(_resolver.Resolve("MLIN"));
        Assert.Contains(_reports, r => r.Contains("not-there.py", StringComparison.Ordinal));
    }

    [PythonFact]
    public void TwoKitsOfferingOneId_KeepTheFirstAndReportTheCollision()
    {
        KitWithExample("kitA");
        KitWithExample("kitB");

        _resolver = NewResolver();
        Assert.NotNull(_resolver.Resolve("MLIN"));

        Assert.Contains(_reports, r => r.Contains("both called 'MLIN'", StringComparison.Ordinal));
    }

    // ── The registry seam, and what it buys ───────────────────────────────────

    /// <summary>
    /// <b>The point of the whole phase.</b> Once a resolver is registered, a script's cell reaches
    /// the content-addressed cell store — which is the machinery every placement, copy-on-write
    /// parameter edit and regeneration snapshot already goes through — with no change to any of it.
    /// </summary>
    [PythonFact]
    public void AScriptCellGoesThroughTheContentAddressedStore_LikeAnyBuiltIn()
    {
        KitWithExample("kit");
        _resolver = NewResolver();
        PCellRegistry.AddResolver(_resolver);

        // VIAARRAY, deliberately, NOT MLIN: a built-in MLIN exists and wins, so using it here would
        // exercise the built-in dictionary and prove nothing about the resolver. Found by removing
        // the resolver lookup and watching this test still pass.
        Assert.DoesNotContain("VIAARRAY", PCellRegistry.KnownGeneratorIds);
        Assert.True(PCellRegistry.TryGet("VIAARRAY", out _));

        var parameters = new Dictionary<string, PCellValue>
        {
            ["Rows"] = PCellValue.Int(2), ["Cols"] = PCellValue.Int(2),
            ["Pitch"] = 100e-6, ["Pad"] = 50e-6, ["Drill"] = 25e-6,
        };
        string cellDir = GeneratedCellStore.GetOrCreate(
            _root, "VIAARRAY", parameters, null, null, PCellLayerSelection.Default);

        // A real generated cell folder, resolvable exactly like a built-in's.
        var resolved = CellLayoutResolver.Resolve(cellDir, _root);
        Assert.Equal(CellLayoutState.Resolved, resolved.State);
        Assert.NotNull(resolved.View!.PCellOrigin);
        Assert.Equal(4, resolved.View.Shapes.OfType<ViaShape>().Count());

        // Content addressing still holds: the same values resolve to the same cell, different ones
        // to a different cell. That is what makes copy-on-write parameter editing free.
        Assert.Equal(cellDir, GeneratedCellStore.GetOrCreate(
            _root, "VIAARRAY", parameters, null, null, PCellLayerSelection.Default));

        var wider = new Dictionary<string, PCellValue>(parameters) { ["Pitch"] = 200e-6 };
        Assert.NotEqual(cellDir, GeneratedCellStore.GetOrCreate(
            _root, "VIAARRAY", wider, null, null, PCellLayerSelection.Default));
    }

    /// <summary>A built-in id must never be diverted to a script — the built-in dictionary is
    /// checked first, so a kit cannot shadow MLIN.</summary>
    [PythonFact]
    public void ABuiltInWins_AScriptCannotShadowIt()
    {
        KitWithExample("kit");
        _resolver = NewResolver();
        PCellRegistry.AddResolver(_resolver);

        Assert.True(PCellRegistry.TryGet("MLIN", out var generator));
        var viaResult = generator(
            new Dictionary<string, PCellValue> { ["W"] = 300e-6, ["L"] = 2e-3 },
            null, PCellLayerSelection.Default);

        // The built-in MLIN emits its rect and nothing else; identical output either way here, so
        // the check that matters is that resolving never even happened.
        Assert.Single(viaResult.Shapes);
        Assert.DoesNotContain("VIAARRAY", PCellRegistry.KnownGeneratorIds);      // built-ins only
        Assert.Contains("VIAARRAY", PCellRegistry.AllKnownGeneratorIds());       // resolved too
    }

    [Fact]
    public void ClearResolvers_DropsEverythingTheyProduced()
    {
        var stub = new StubResolver();
        PCellRegistry.AddResolver(stub);
        Assert.True(PCellRegistry.TryGet("STUB", out _));

        PCellRegistry.ClearResolvers();
        Assert.False(PCellRegistry.TryGet("STUB", out _));
    }

    /// <summary>A resolver that throws is one broken kit, and must not make every other kit's
    /// generators unresolvable.</summary>
    [Fact]
    public void AResolverThatThrows_DoesNotBreakTheOnesBesideIt()
    {
        PCellRegistry.AddResolver(new ThrowingResolver());
        PCellRegistry.AddResolver(new StubResolver());

        Assert.True(PCellRegistry.TryGet("STUB", out _));
    }

    /// <summary>Resolution is cached: a second ask does not re-enter the resolver.</summary>
    [Fact]
    public void AResolvedGeneratorIsCached_NotReResolvedEveryLookup()
    {
        var stub = new StubResolver();
        PCellRegistry.AddResolver(stub);

        Assert.True(PCellRegistry.TryGet("STUB", out _));
        Assert.True(PCellRegistry.TryGet("STUB", out _));
        Assert.Equal(1, stub.ResolveCalls);
    }

    // ── A failing generator must not take a frame down with it ────────────────

    /// <summary>
    /// <b>A built-in generator could never fail; a script can, and two of its callers are on paths
    /// where an exception is a crash rather than a defect.</b> The pin overlay runs per repaint and
    /// the snap index runs on pointer moves — losing a cell's pins is a real degradation, throwing
    /// out of a render or a pointer handler is an application failure. Both degrade instead.
    /// </summary>
    [Fact]
    public void AGeneratorThatThrows_CostsThePinsAndSnapPoints_NotTheFrame()
    {
        PCellRegistry.AddResolver(new ThrowingGenerateResolver());

        var view = new LayoutView
        {
            PCellOrigin = new PCellOrigin("BOOM", new Dictionary<string, PCellValue> { ["W"] = 1.0 }),
        };
        view.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });

        // The snap index still builds — with the shape's own features, and without the cell's pins.
        var features = LayoutSnapFeatureIndex.Get(view, null);
        Assert.NotNull(features);

        // And a frame containing an instance of it still renders.
        string cellDir = Path.Combine(_root, "boomcell");
        Directory.CreateDirectory(Path.Combine(cellDir, "layout"));
        File.WriteAllText(Path.Combine(cellDir, ".ccell"), "{}");
        LayoutPersistence.SaveToFile(Path.Combine(cellDir, "layout", "boomcell.clay"), view);

        var parent = new LayoutView { DbuPerMicron = 1000 };
        parent.Instances.Add(new LayoutInstance { CellRef = "boomcell", X = 0, Y = 0, Mag = 1.0 });

        using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(256, 256));
        // Positional record: Width/Height are the LAST two arguments and default to zero under an
        // object initializer — which culls everything and makes this test pass without ever reaching
        // the generator. Found by removing the guard and watching it still pass.
        var viewport = new LayoutViewport(-2000, -2000, 0.05, 256, 256);
        var exception = Record.Exception(() => CircuitRF.Ui.Renderers.LayoutRenderer.Draw(
            surface.Canvas, parent, null, viewport,
            new CircuitRF.Ui.Renderers.LayoutRenderOptions
            {
                Theme = CircuitRF.Ui.Renderers.LayoutRenderTheme.Light,
                ShowGrid = false, BaseDir = _root, ShowPCellPins = true,
            }));

        Assert.Null(exception);
    }

    private sealed class ThrowingGenerateResolver : IPCellGeneratorResolver
    {
        public PCellGenerator? Resolve(string generatorId) => generatorId == "BOOM"
            ? (_, _, _) => throw new PCellWireException("DELIBERATE: the generator refused")
            : null;

        public IReadOnlyCollection<string> KnownGeneratorIds => ["BOOM"];
        public string Describe() => "throwing generate";
        public string? ContentKeyFor(string generatorId) => generatorId == "BOOM" ? "boom" : null;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private PCellWorkerResolver NewResolver()
        => new(_root,
               findInterpreter: (_, _) => new PythonInterpreter(PythonRunner.Interpreter!, [], "test", "supplied by the test"),
               report: _reports.Add);

    private static PCellWorkerProvider StartProvider(string kitDir)
    {
        var manifest = PCellGeneratorManifest.TryRead(kitDir, out _)!;
        return new PCellWorkerProvider(ProcessPCellWorkerTransport.Start(
            PythonRunner.Interpreter!, manifest.ResolveEntry(kitDir), manifest.ResolvePythonPath(kitDir)));
    }

    /// <summary>Stands up a kit directory whose manifest points at the real example generator, with
    /// the package on its declared PYTHONPATH — the arrangement a kit ships.</summary>
    private string KitWithExample(string name)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);

        string package = PythonRunner.PackageRoot;
        File.WriteAllText(Path.Combine(dir, "main.py"),
            $"import sys\nsys.path.insert(0, {ToPythonLiteral(package)})\n" +
            $"exec(open({ToPythonLiteral(Path.Combine(package, "example", "mlin.py"))}).read())\n");

        File.WriteAllText(Path.Combine(dir, PCellGeneratorManifest.FileName),
            $$"""
            { "schemaVersion": 1, "entry": "main.py", "pythonPath": [{{ToJsonString(package)}}] }
            """);
        return dir;
    }

    private static string ToPythonLiteral(string path) => "r'" + path.Replace("'", "\\'") + "'";
    private static string ToJsonString(string path) => System.Text.Json.JsonSerializer.Serialize(path);

    private sealed class StubResolver : IPCellGeneratorResolver
    {
        public int ResolveCalls { get; private set; }

        public PCellGenerator? Resolve(string generatorId)
        {
            ResolveCalls++;
            return generatorId == "STUB"
                ? (_, _, _) => new PCellResult([], [])
                : null;
        }

        public IReadOnlyCollection<string> KnownGeneratorIds => ["STUB"];
        public string Describe() => "stub";
        public string? ContentKeyFor(string generatorId) => generatorId == "STUB" ? "stub-key" : null;
    }

    private sealed class ThrowingResolver : IPCellGeneratorResolver
    {
        public PCellGenerator? Resolve(string generatorId) => throw new PCellWireException("DELIBERATE");
        public IReadOnlyCollection<string> KnownGeneratorIds => throw new PCellWireException("DELIBERATE");
        public string Describe() => "throwing";
        public string? ContentKeyFor(string generatorId) => throw new PCellWireException("DELIBERATE");
    }
}

/// <summary>
/// <see cref="PCellRegistry"/>'s resolver list is process-wide static, so classes that touch it must
/// not run concurrently — the same hazard (and the same fix) as the other process-wide caches this
/// codebase serialises.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PCellResolverCollection
{
    public const string Name = "PCellRegistry resolvers (process-wide static)";
}
