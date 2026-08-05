using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Layout.PCells.Wire;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout.PCells;

/// <summary>
/// A kit's own parametric cells reaching circuitRF as ordinary <c>PCellGenerator</c>s, driven through
/// the PRODUCTION <see cref="PCellWorkerProvider"/> rather than a test harness.
///
/// <para><b>The kit here is synthetic, deliberately.</b> The repository commits no third-party kit
/// data, and a test keyed to a vendor library on one machine is a test that fails on a fresh clone.
/// What needs proving is the MECHANISM — that cells are discovered from a package rather than listed
/// by hand, that their parameters cross as the kit's own text, and that a boolean asked mid-generate
/// is serviced by the real provider — and a small kit written against the same <c>cni</c> surface
/// proves all three. The production kit is exercised separately, by running it.</para>
/// </summary>
[Collection(PCellResolverCollection.Name)]
public sealed class PCellVendorBridgeTests
{
    private static readonly Technology Tech = new()
    {
        Name   = "T",
        Layers = { new LayerDef { Key = new LayerKey(1, 0), Name = "M1" } },
    };

    private static readonly PCellLayerSelection NoLayers = new(null, null);

    /// <summary>
    /// Builds a throwaway kit — a device package plus the additions folder that names it — and returns
    /// the entry script and the directories that must go on <c>PYTHONPATH</c>.
    /// </summary>
    private static (string Script, string[] PythonPath) WriteSyntheticKit()
    {
        string root = Path.Combine(Path.GetTempPath(), "crf-kit-" + Guid.NewGuid().ToString("N")[..8]);
        string pkg  = Path.Combine(root, "demo_kit", "devices");
        Directory.CreateDirectory(pkg);
        File.WriteAllText(Path.Combine(root, "demo_kit", "__init__.py"), "");
        File.WriteAllText(Path.Combine(pkg, "__init__.py"), "");

        // A plain cell: one rectangle sized by its own parameters, in the kit's own engineering
        // notation ('2u'), parsed by the kit rather than by circuitRF.
        File.WriteAllText(Path.Combine(pkg, "pad_code.py"), """
            from cni.dlo import Box, DloGen, Layer, Rect

            def _um(text):
                text = str(text)
                return float(text[:-1]) if text.endswith("u") else float(text)

            class pad(DloGen):
                @classmethod
                def defineParamSpecs(cls, specs):
                    specs('w', '2u', 'Width')
                    specs('h', '3u', 'Height')

                def setupParams(self, params):
                    self.w = _um(params['w'])
                    self.h = _um(params['h'])

                def genLayout(self):
                    Rect(Layer('M1'), Box(0, 0, self.w, self.h))
            """);

        // A cell that asks circuitRF to clip, so the production service loop is on the tested path.
        File.WriteAllText(Path.Combine(pkg, "ring_code.py"), """
            from cni.dlo import Box, DloGen, Layer, Rect
            from cni.geo import fgNot

            class ring(DloGen):
                @classmethod
                def defineParamSpecs(cls, specs):
                    specs('outer', '10', 'Outer size')
                    specs('inner', '4', 'Inner size')

                def setupParams(self, params):
                    self.outer = float(params['outer'])
                    self.inner = float(params['inner'])

                def genLayout(self):
                    m = (self.outer - self.inner) / 2.0
                    big = Rect(Layer('M1'), Box(0, 0, self.outer, self.outer))
                    cut = Rect(Layer('M1'), Box(m, m, m + self.inner, m + self.inner))
                    fgNot(big, cut)
            """);

        // The additions folder: a small folder of one's own that NAMES the kit, rather than anything
        // written into the (usually read-only) kit itself — the same shape the PDK importer already
        // uses for a kit it must not modify.
        string additions = Path.Combine(root, "DemoKit");
        Directory.CreateDirectory(additions);
        string script = Path.Combine(additions, "vendor_cells.py");
        File.WriteAllText(script, """
            import sys
            import circuitrf_pcell as crf
            from cni.bridge import register_kit

            result = register_kit("demo_kit.devices")
            for problem in result.problems:
                print(problem, file=sys.stderr)

            crf.run()
            """);

        return (script, [PythonRunner.PackageRoot, root]);   // additions folder name == kit name
    }

    private static PCellWorkerProvider StartProvider(string script, string[] pythonPath)
        => new(ProcessPCellWorkerTransport.Start(PythonRunner.Interpreter!, script, pythonPath));

    // ── discovery ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Every cell in the kit's package becomes a generator, discovered by walking the package — so a
    /// kit that gains a device gains a generator with nothing to update anywhere.
    /// </summary>
    [PythonFact]
    public void EveryCellInTheKit_BecomesAGenerator_DiscoveredNotDeclared()
    {
        var (script, pythonPath) = WriteSyntheticKit();
        using var provider = StartProvider(script, pythonPath);

        Assert.Equal(["pad", "ring"], provider.GeneratorIds.OrderBy(x => x, StringComparer.Ordinal));
    }

    /// <summary>
    /// A cell generates through the ordinary <c>PCellGenerator</c> delegate, at the kit's OWN
    /// defaults — nothing above this line knows the geometry came from a vendor's Python.
    /// </summary>
    [PythonFact]
    public void AVendorCell_GeneratesThroughTheOrdinaryGeneratorDelegate_AtTheKitsOwnDefaults()
    {
        var (script, pythonPath) = WriteSyntheticKit();
        using var provider = StartProvider(script, pythonPath);

        Assert.True(provider.TryGetGenerator("pad", out var generator));
        var result = generator(new Dictionary<string, PCellValue>(), Tech, NoLayers);

        var rect = Assert.IsType<RectShape>(Assert.Single(result.Shapes));
        // '2u' x '3u' at 1000 DBU per micrometre — the kit parsed its own notation, and the micron →
        // DBU conversion happened once, on the script side, using the resolution wire version 2 states.
        Assert.Equal(0, rect.X1);
        Assert.Equal(0, rect.Y1);
        Assert.Equal(2000, rect.X2);
        Assert.Equal(3000, rect.Y2);
    }

    /// <summary>
    /// A parameter crosses as the kit's OWN text and is parsed by the kit. circuitRF hosts the kit's
    /// parameter language rather than reinterpreting it — declaring a width as a length would hand
    /// the cell a database-unit count its own reader never expected.
    /// </summary>
    [PythonFact]
    public void AParameterCrossesAsTheKitsOwnText_AndTheKitParsesIt()
    {
        var (script, pythonPath) = WriteSyntheticKit();
        using var provider = StartProvider(script, pythonPath);

        Assert.True(provider.TryGetGenerator("pad", out var generator));
        var result = generator(
            new Dictionary<string, PCellValue> { ["w"] = PCellValue.Text("5u") },
            Tech, NoLayers);

        var rect = Assert.IsType<RectShape>(Assert.Single(result.Shapes));
        Assert.Equal(5000, rect.X2);   // the kit read '5u' with its own parser
        Assert.Equal(3000, rect.Y2);   // and the untouched parameter kept its own default
    }

    /// <summary>
    /// A vendor cell that asks circuitRF to clip is serviced by the PRODUCTION provider, mid-generate.
    /// This is the whole point of wire version 3 reaching a kit: without it the cell fails, and
    /// with a second clipper on the script side it would silently disagree with circuitRF's own.
    /// </summary>
    [PythonFact]
    public void AVendorCellThatAsksCircuitRfToClip_IsServicedByTheRealProvider()
    {
        var (script, pythonPath) = WriteSyntheticKit();
        using var provider = StartProvider(script, pythonPath);

        Assert.True(provider.TryGetGenerator("ring", out var generator));
        var result = generator(new Dictionary<string, PCellValue>(), Tech, NoLayers);

        var poly = Assert.IsType<PolygonShape>(Assert.Single(result.Shapes));
        var hole = Assert.Single(poly.Holes!);

        // A 10 um square with a centred 4 um square removed: the hole survives as a hole, and its
        // extent is exactly the region the cell asked to be cut.
        var hx = hole.Where((_, i) => i % 2 == 0).ToArray();
        Assert.Equal(3000, hx.Min());
        Assert.Equal(7000, hx.Max());
    }

    /// <summary>
    /// A device the bridge cannot read costs that one cell, not the kit. One unreadable module must
    /// never take a user's other thirty with it — the same rule the kit importer already follows for
    /// a symbol it cannot parse.
    /// </summary>
    [PythonFact]
    public void AnUnreadableDevice_CostsThatCellOnly_NotTheKit()
    {
        var (script, pythonPath) = WriteSyntheticKit();
        string pkg = Path.Combine(pythonPath[1], "demo_kit", "devices");
        File.WriteAllText(Path.Combine(pkg, "broken_code.py"), "this is not valid python(\n");

        using var provider = StartProvider(script, pythonPath);

        Assert.Equal(["pad", "ring"], provider.GeneratorIds.OrderBy(x => x, StringComparer.Ordinal));
        Assert.True(provider.TryGetGenerator("pad", out var generator));
        Assert.Single(generator(new Dictionary<string, PCellValue>(), Tech, NoLayers).Shapes);
    }

    // ── zero configuration ────────────────────────────────────────────────────

    /// <summary>
    /// A kit's manifest names ONLY the kit — circuitRF supplies its own Python package itself, so
    /// nothing machine-specific has to be written into a file the kit ships. This is the whole
    /// difference between "reference a kit where it lies" and "edit a path into it first".
    /// </summary>
    [PythonFact]
    public void AManifestNamingOnlyTheKit_ResolvesItsCells_WithNoPathToCircuitRfWrittenAnywhere()
    {
        var (script, pythonPath) = WriteSyntheticKit();
        string additions = Path.GetDirectoryName(script)!;
        string kitRoot   = pythonPath[1];

        // Deliberately does NOT mention PythonRunner.PackageRoot: if the resolver did not supply
        // circuitRF's own package, `import circuitrf_pcell` would fail and nothing would resolve.
        File.WriteAllText(Path.Combine(additions, PCellGeneratorManifest.FileName), $$"""
            {
              "entry": "vendor_cells.py",
              "pythonPath": [{{System.Text.Json.JsonSerializer.Serialize(kitRoot)}}]
            }
            """);

        var problems = new List<string>();
        using var resolver = new PCellWorkerResolver(
            additions,
            findInterpreter: (_, _) => new PythonInterpreter(PythonRunner.Interpreter!, [], "3.x", "test"),
            report: problems.Add,
            trust: _ => PCellTrustDecision.Allowed);

        Assert.Equal(["pad", "ring"], resolver.KnownGeneratorIds.OrderBy(x => x, StringComparer.Ordinal));

        var generator = resolver.Resolve("pad");
        Assert.NotNull(generator);
        Assert.Single(generator!(new Dictionary<string, PCellValue>(), Tech, NoLayers).Shapes);
        Assert.Empty(problems);
    }

    /// <summary>
    /// This build can find its own Python package. Checked by the package's own presence rather than
    /// a folder name, so a directory that merely looks right is never adopted silently.
    /// </summary>
    [Fact]
    public void CircuitRfFindsItsOwnPythonPackage()
    {
        Assert.NotNull(PCellPythonPackage.RootDirectory);
        Assert.True(File.Exists(
            Path.Combine(PCellPythonPackage.RootDirectory!, "circuitrf_pcell", "__init__.py")));
    }

    // ── placeable ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A kit's cells declare their own defaults, so circuitRF can place one without already knowing
    /// its parameters. This is what makes a placed vendor cell ADJUSTABLE — a cell placed with an
    /// empty set generates fine and then has nothing to edit.
    /// </summary>
    [PythonFact]
    public void AVendorCell_DeclaresItsOwnDefaults_SoItCanBePlacedAndThenEdited()
    {
        var (script, pythonPath) = WriteSyntheticKit();
        using var provider = StartProvider(script, pythonPath);

        var defaults = provider.DeclaredDefaults("pad");
        Assert.NotNull(defaults);

        // The kit's own notation, verbatim — not a number circuitRF invented for it.
        Assert.Equal("2u", defaults!["w"].AsText());
        Assert.Equal("3u", defaults["h"].AsText());

        // And placing at exactly those declared values reproduces the cell's own default geometry.
        Assert.True(provider.TryGetGenerator("pad", out var generator));
        var rect = Assert.IsType<RectShape>(Assert.Single(generator(defaults, Tech, NoLayers).Shapes));
        Assert.Equal(2000, rect.X2);
        Assert.Equal(3000, rect.Y2);
    }

    /// <summary>A parameter with no declared default is ABSENT, never invented.</summary>
    [PythonFact]
    public void AParameterWithNoDeclaredDefault_IsAbsent_NotInvented()
    {
        string root = Path.Combine(Path.GetTempPath(), "crf-kit-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        string script = Path.Combine(root, "g.py");
        File.WriteAllText(script, """
            import circuitrf_pcell as crf

            @crf.generator("BARE", [crf.Parameter.real("stated", 1.5), crf.Parameter.real("unstated")])
            def bare(params, tech):
                return crf.Result(shapes=[], pins=[])

            crf.run()
            """);

        using var provider = StartProvider(script, [PythonRunner.PackageRoot]);
        var defaults = provider.DeclaredDefaults("BARE");

        Assert.NotNull(defaults);
        Assert.True(defaults!.ContainsKey("stated"));
        Assert.False(defaults.ContainsKey("unstated"),
            "a parameter the generator declared no default for must not acquire one");
    }

    /// <summary>An id no resolver owns yields null rather than an empty set — "nobody knows" and
    /// "declares nothing" are different answers.</summary>
    [Fact]
    public void AnUnknownGeneratorId_HasNoDeclaredDefaults()
        => Assert.Null(PCellRegistry.DeclaredDefaults("NOT-A-GENERATOR"));

    /// <summary>
    /// A kit's cell is PLACEABLE, not merely resolvable — it appears in the placeable list with the
    /// kit's own defaults, and placing it produces an ordinary instance of a generated cell.
    ///
    /// <para>This is the end of the arc: reference a kit, and its cells can be used. Before it,
    /// placement was keyed on <c>SymbolKind</c>, so a kit's cells were structurally unreachable from
    /// the application no matter how well they resolved.</para>
    /// </summary>
    [PythonFact]
    public void AKitsCell_IsPlaceable_WithItsOwnDefaults()
    {
        var (script, pythonPath) = WriteSyntheticKit();
        using var provider = StartProvider(script, pythonPath);

        string workspace = Path.Combine(Path.GetTempPath(), "crf-ws-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workspace);
        // WorkspaceRootDir is found by walking up to an ancestor .cws, exactly as in production —
        // poking the property directly would test a path the application never takes.
        File.WriteAllText(Path.Combine(workspace, ".cws"), "{}");
        var resolver = new StubResolver(provider);
        PCellRegistry.AddResolver(resolver);
        try
        {
            // A document that lives IN the workspace, so WorkspaceRootDir/InstanceBaseDir resolve
            // the way they do in production rather than being poked directly.
            string docPath = Path.Combine(workspace, "top.clay");
            var vm = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000 }, docPath)
            {
                CurrentWorkspaceRootDirProvider = () => workspace,
            };
            // Without a technology the kit's own layer names resolve to nothing and its shapes are
            // skipped with a diagnostic — correct behaviour, but it would make this test's geometry
            // assertion vacuous. Give it the same technology the generation tests above use.
            vm.ApplyTechResolution(new TechResolution(Tech, null, TechResolutionSource.WorkspaceDefault, []));

            var placeable = vm.PlaceablePCells();
            var pad = Assert.Single(placeable, g => g.Id == "pad");
            Assert.Equal("2u", pad.Parameters["w"].AsText());        // the kit's own default, declared

            Assert.True(vm.PlacePCell(pad.Id, pad.Parameters, 0, 0));

            var inst = Assert.Single(vm.Model.Instances);
            var cellView = LayoutPersistence.LoadFromFile(
                Directory.GetFiles(Path.Combine(workspace, inst.CellRef), "*.clay", SearchOption.AllDirectories).Single());

            // A generated cell like any other: it records what generated it, so its parameters are
            // editable afterwards rather than frozen at whatever the script fell back to.
            Assert.Equal("pad", cellView.PCellOrigin!.GeneratorId);
            Assert.Equal("2u", cellView.PCellOrigin.Parameters["w"].AsText());
            Assert.Single(cellView.Shapes);
        }
        finally
        {
            PCellRegistry.ClearResolvers();
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        }
    }

    /// <summary>Adapts one provider to the registry's resolver seam, exactly as
    /// <c>PCellWorkerResolver</c> does for a whole workspace.</summary>
    private sealed class StubResolver(PCellWorkerProvider provider) : IPCellGeneratorResolver
    {
        public PCellGenerator? Resolve(string id) => provider.TryGetGenerator(id, out var g) ? g : null;
        public IReadOnlyCollection<string> KnownGeneratorIds => provider.GeneratorIds;
        public string Describe() => "stub";
        public string? ContentKeyFor(string id) => null;
        public IReadOnlyDictionary<string, PCellValue>? DeclaredDefaults(string id) => provider.DeclaredDefaults(id);
    }

    // ── drag and drop from the palette ────────────────────────────────────────

    /// <summary>
    /// The drag payload carries the generator id, and an OLDER payload still parses. Both halves
    /// matter: a kit tile shares the placeholder <c>SymbolKind</c> every kit tile uses, so without
    /// the id a dropped vendor cell would place whatever that placeholder means — nothing.
    /// </summary>
    [Fact]
    public void ADragPayload_CarriesTheGeneratorId_AndOlderPayloadsStillParse()
    {
        var payload = new PaletteDragPayload(SymbolKind.Generic, 0, null, "rfnmos");
        Assert.True(PaletteDragPayload.TryParse(payload.Serialize(), out var back));
        Assert.Equal("rfnmos", back.PCellGeneratorId);
        Assert.Null(back.CellDir);

        // A kit-part payload (a cell folder tail) must not be mistaken for a generator id, and a
        // path may itself contain ':' — which is why both tails are marked and the path is last.
        var kitPart = new PaletteDragPayload(SymbolKind.Generic, 0, "/kits/a:b/CellX");
        Assert.True(PaletteDragPayload.TryParse(kitPart.Serialize(), out var kitBack));
        Assert.Equal("/kits/a:b/CellX", kitBack.CellDir);
        Assert.Null(kitBack.PCellGeneratorId);

        // BOTH tails together — one tile for a part that has a schematic symbol AND a layout
        // generator. Emitting only one would make the same tile work on one canvas and silently do
        // nothing on the other.
        var both = new PaletteDragPayload(SymbolKind.Generic, 0, "/kits/a:b/nmos", "nmos");
        Assert.True(PaletteDragPayload.TryParse(both.Serialize(), out var bothBack));
        Assert.Equal("nmos", bothBack.PCellGeneratorId);
        Assert.Equal("/kits/a:b/nmos", bothBack.CellDir);

        // An OLDER payload, whose path tail carries no marker, still parses — a drag begun before an
        // update must not become unparseable mid-gesture.
        Assert.True(PaletteDragPayload.TryParse("circuitrf-palette:Generic:0:/kits/x/CellY", out var legacy));
        Assert.Equal("/kits/x/CellY", legacy.CellDir);
        Assert.Null(legacy.PCellGeneratorId);

        // And a built-in tile's payload, which carries neither.
        Assert.True(PaletteDragPayload.TryParse("circuitrf-palette:Mlin:2", out var plain));
        Assert.Equal(SymbolKind.Mlin, plain.Kind);
        Assert.Null(plain.PCellGeneratorId);
        Assert.Null(plain.CellDir);
    }

    /// <summary>
    /// Dragging a kit's cell onto a layout places it — the drop path the canvas takes, driven end to
    /// end against a real generator over the real transport.
    /// </summary>
    [PythonFact]
    public void AKitsCell_CanBeDroppedFromThePalette()
    {
        var (script, pythonPath) = WriteSyntheticKit();
        using var provider = StartProvider(script, pythonPath);

        string workspace = Path.Combine(Path.GetTempPath(), "crf-ws-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, ".cws"), "{}");

        PCellRegistry.AddResolver(new StubResolver(provider));
        try
        {
            var vm = new LayoutEditorViewModel(
                new LayoutView { DbuPerMicron = 1000 }, Path.Combine(workspace, "top.clay"))
            {
                CurrentWorkspaceRootDirProvider = () => workspace,
            };
            vm.ApplyTechResolution(new TechResolution(Tech, null, TechResolutionSource.WorkspaceDefault, []));

            // The drag-over cursor's own yes/no, before release.
            Assert.True(vm.CanDropPCellGenerator("pad"));
            Assert.False(vm.CanDropPCellGenerator("not-a-generator"));

            // The ghost is the generator's REAL output at its DECLARED DEFAULTS — not an outline, not
            // a placeholder box. A ghost that did not match what the drop produces would mislead at
            // exactly the moment the user is deciding where to put it.
            vm.UpdatePCellDragGhost("pad", 1000, 2000);
            var ghost = vm.Overlay.PendingPCellPlacement;
            Assert.NotNull(ghost);
            Assert.Equal(1000, ghost!.Value.X);
            Assert.Equal(2000, ghost.Value.Y);

            var ghostRect = Assert.IsType<RectShape>(Assert.Single(ghost.Value.GhostView.Shapes));
            Assert.Equal(2000, ghostRect.X2);   // '2u' — the kit's own declared default
            Assert.Equal(3000, ghostRect.Y2);   // '3u'

            // And it TRACKS: a later tick moves it without rebuilding the geometry.
            vm.UpdatePCellDragGhost("pad", 4000, 5000);
            var moved = vm.Overlay.PendingPCellPlacement;
            Assert.Equal(4000, moved!.Value.X);
            Assert.Equal(5000, moved.Value.Y);
            Assert.Same(ghost.Value.GhostView, moved.Value.GhostView);

            Assert.True(vm.CommitPCellDrop("pad", 5000, 7000));

            var inst = Assert.Single(vm.Model.Instances);
            Assert.Equal(5000, inst.X);
            Assert.Equal(7000, inst.Y);
            Assert.Null(vm.Overlay.PendingPCellPlacement);   // the ghost is cleared by the commit
        }
        finally
        {
            PCellRegistry.ClearResolvers();
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        }
    }

    /// <summary>
    /// A kit's cells are attributed to THAT KIT, so they list under the kit's own heading in the
    /// palette exactly as its schematic parts do. A user placing a device cares which kit it came
    /// from — not whether it happens to be generated rather than drawn.
    /// </summary>
    [PythonFact]
    public void AKitsCells_AreAttributedToThatKit_ForTheKitsOwnPaletteHeading()
    {
        var (script, pythonPath) = WriteSyntheticKit();
        string additions = Path.GetDirectoryName(script)!;
        string kitRoot   = pythonPath[1];

        File.WriteAllText(Path.Combine(additions, PCellGeneratorManifest.FileName), $$"""
            {
              "entry": "vendor_cells.py",
              "pythonPath": [{{System.Text.Json.JsonSerializer.Serialize(kitRoot)}}]
            }
            """);

        using var resolver = new PCellWorkerResolver(
            additions,
            findInterpreter: (_, _) => new PythonInterpreter(PythonRunner.Interpreter!, [], "3.x", "test"),
            trust: _ => PCellTrustDecision.Allowed);

        var byKit = resolver.KitNameByGeneratorId;

        // The kit's own folder name, the same identity its schematic parts are filed under.
        Assert.Equal("DemoKit", byKit["pad"]);
        Assert.Equal("DemoKit", byKit["ring"]);
        Assert.Equal(2, byKit.Count);
    }
}
