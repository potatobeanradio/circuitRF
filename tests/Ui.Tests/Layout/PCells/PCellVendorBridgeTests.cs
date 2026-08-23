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


        // A cell that declares a pin on its own metal and THEN moves everything — the origin-reset
        // idiom (see the pin test below for why it is the case that matters).
        File.WriteAllText(Path.Combine(pkg, "moved_code.py"), """
            from cni.dlo import Box, DloGen, Layer, Point, Rect

            class moved(DloGen):
                @classmethod
                def defineParamSpecs(cls, specs):
                    specs('shift', '4', 'How far the cell moves after it is drawn')

                def setupParams(self, params):
                    self.shift = float(params['shift'])

                def genLayout(self):
                    # Drawn in a frame with negative coordinates, exactly as a real cell does when it
                    # builds outward from the device rather than from the cell's corner.
                    body = Rect(Layer('M1'), Box(-self.shift, -self.shift, 6, 6))
                    pinBox = Box(-self.shift, -self.shift, -self.shift + 1, -self.shift + 1)
                    self.addPin('P', 'P', pinBox, Layer('M1', 'pin'))
                    Rect(Layer('M1', 'pin'), pinBox)
                    # ...then the origin is reset, which moves every figure drawn so far.
                    for fig in self.getShapes():
                        fig.moveBy(self.shift, self.shift)
            """);

        // A cell in the CDF shape a real vendor kit is written in: a label and an enumeration on
        // every parameter that has one, a "Calculate" selector naming the quantities the cell solves
        // among, and a capacitance the cell DERIVES and never reads. This is the shape that used to
        // arrive as five identical free-text boxes.
        File.WriteAllText(Path.Combine(pkg, "cap_code.py"), """
            from cni.dlo import Box, ChoiceConstraint, DloGen, Layer, RangeConstraint, Rect

            def _um(text):
                text = str(text)
                return float(text[:-1]) if text.endswith("u") else float(text)

            class cap(DloGen):
                @classmethod
                def defineParamSpecs(cls, specs):
                    specs('Calculate', 'w&l', 'Calculate', ChoiceConstraint(['C', 'w', 'l', 'w&l']))
                    specs('C', '74.6f', 'C')
                    specs('w', '6u', 'Width')
                    specs('l', '6u', 'Length')
                    specs('model', 'cap_mim', 'Model name')
                    specs('guard', 'Yes', 'Guard ring', ChoiceConstraint(['Yes', 'No']))
                    specs('ng', 1, 'Number of Gates', RangeConstraint(1, 64))

                def setupParams(self, params):
                    # C is NEVER read: the vendor's own dialog back-solved it, and this port kept the
                    # declaration without the behaviour.
                    self.w = _um(params['w'])
                    self.l = _um(params['l'])

                def genLayout(self):
                    Rect(Layer('M1'), Box(0, 0, self.w, self.l))
            """);

        // The same shape again, and deliberately with NO calculator registered beside it — the
        // ordinary case for a vendor kit, where the arithmetic lived in the dialog and nobody has
        // put it back. Its output must still be identified as one.
        File.WriteAllText(Path.Combine(pkg, "res_code.py"), """
            from cni.dlo import Box, ChoiceConstraint, DloGen, Layer, Rect

            class res(DloGen):
                @classmethod
                def defineParamSpecs(cls, specs):
                    specs('Calculate', 'R', 'Calculate', ChoiceConstraint(['R', 'w', 'l']))
                    specs('R', '1k', 'R')
                    specs('w', '2', 'Width')
                    specs('l', '8', 'Length')

                def setupParams(self, params):
                    self.w = float(params['w'])
                    self.l = float(params['l'])

                def genLayout(self):
                    Rect(Layer('M1'), Box(0, 0, self.w, self.l))
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

            # The kit's cells derive C and never read it, but nothing in them computes it either --
            # the vendor's dialog did. This is where that arithmetic is put back.
            def _um(text):
                text = str(text)
                return float(text[:-1]) if text.endswith("u") else float(text)

            def _cap(params, tech):
                area = _um(params.text('w', '0')) * _um(params.text('l', '0'))
                return {"C": "%.1ff" % (area * 2.0)}

            crf.reports_computed("cap", _cap)

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

        Assert.Equal(["cap", "moved", "pad", "res", "ring"], provider.GeneratorIds.OrderBy(x => x, StringComparer.Ordinal));
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

        Assert.Equal(["cap", "moved", "pad", "res", "ring"], provider.GeneratorIds.OrderBy(x => x, StringComparer.Ordinal));
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

        Assert.Equal(["cap", "moved", "pad", "res", "ring"], resolver.KnownGeneratorIds.OrderBy(x => x, StringComparer.Ordinal));

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

    /// <summary>
    /// <b>It finds it BESIDE THE EXECUTABLE — the branch a shipped build has, not the source-tree
    /// walk-up.</b>
    ///
    /// <para>The test above passes either way, and that is exactly how this shipped broken: nothing
    /// copied the package into the build output, the resolver's second branch walked up to
    /// <c>tools/pcell-python</c>, and every development run worked. An installed
    /// <c>circuitRF.app</c> has no repository above it, so importing a kit ended at
    /// <c>ModuleNotFoundError: No module named 'circuitrf_pcell'</c> and every one of that kit's
    /// cells drew as a placeholder.</para>
    ///
    /// <para>This asserts the resolved directory IS <c>AppContext.BaseDirectory/pcell-python</c>,
    /// which can only be true when the copy actually happened. It is a real gate rather than a
    /// reading of the .csproj: the package is copied to the output of anything referencing
    /// <c>src/Ui</c>, this test project included.</para>
    /// </summary>
    [Fact]
    public void ThePythonPackageIsShippedBesideTheExecutable_NotFoundBySourceTreeWalkUp()
    {
        string beside = Path.Combine(AppContext.BaseDirectory, "pcell-python");

        Assert.Equal(beside, PCellPythonPackage.RootDirectory);

        // Both packages, not just the marker the resolver checks: a kit's entry script imports
        // circuitrf_pcell AND cni.bridge, so half a copy is still a ModuleNotFoundError.
        Assert.True(File.Exists(Path.Combine(beside, "circuitrf_pcell", "__init__.py")));
        Assert.True(File.Exists(Path.Combine(beside, "cni", "bridge.py")));
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
    /// <b>A pin follows the artwork it names, through whatever the cell does to itself afterwards.</b>
    ///
    /// <para>Declaring a pin and drawing its metal is one action written as two lines — <c>addPin</c>
    /// with a box, then a rectangle on the same box — and a kit habitually draws the whole cell in
    /// whatever frame suited it and THEN moves every figure, most often to put the origin at the
    /// cell's lower-left corner. A pin recorded as a fixed box does not follow that move, so every pin
    /// ends up offset by exactly the reset: off its own metal, and outside the cell entirely for any
    /// pin whose frame coordinates were negative. It draws perfectly and connects to nothing, which is
    /// why it survived until someone looked at where the pins had landed.</para>
    ///
    /// <para>Measured before the fix: four RF transistor cells reported pins at
    /// IDENTICAL absolute coordinates despite their geometry differing in size by 40%, two of them
    /// outside the cell's own bounding box. After: every pin inside, each moved by exactly the
    /// translation its cell applies.</para>
    /// </summary>
    [PythonFact]
    public void APinFollowsItsArtwork_WhenTheCellResetsItsOwnOrigin()
    {
        var (script, pythonPath) = WriteSyntheticKit();
        using var provider = StartProvider(script, pythonPath);

        Assert.True(provider.TryGetGenerator("moved", out var generator));
        var result = generator(new Dictionary<string, PCellValue> { ["shift"] = "4" },
                               Tech, PCellLayerSelection.Default);

        // The body was drawn at [-4,-4]..[6,6] and the cell then moved everything by +4, so the
        // artwork now starts at the origin. That is the frame the pin has to be in.
        var bbox = result.Shapes.Select(LayoutGeometry.BboxOf)
                                .Aggregate(Bbox.Empty, (a, b) => a.Union(b));
        Assert.Equal(0, bbox.MinX);
        Assert.Equal(0, bbox.MinY);

        var pin = Assert.Single(result.Pins);
        Assert.Equal("P", pin.Name);

        // Declared at [-4,-4]..[-3,-3], centre (-3.5, -3.5) µm; moved by +4 µm, centre (0.5, 0.5) µm.
        // Before the fix this was (-3500, -3500) — outside the cell, in the frame it was drawn in.
        Assert.Equal(500, pin.X);
        Assert.Equal(500, pin.Y);
        Assert.True(bbox.MinX <= pin.X && pin.X <= bbox.MaxX, "pin X is outside the artwork");
        Assert.True(bbox.MinY <= pin.Y && pin.Y <= bbox.MaxY, "pin Y is outside the artwork");
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
        Assert.Equal("DemoKit", byKit["moved"]);
        Assert.Equal("DemoKit", byKit["cap"]);
        Assert.Equal("DemoKit", byKit["res"]);
        Assert.Equal(5, byKit.Count);
    }

    // ── wire version 7: the editor hints a kit already states ─────────────────

    /// <summary>
    /// A kit's own <c>defineParamSpecs</c> metadata reaches circuitRF. Every bit of this was already
    /// written down in the kit and was discarded at this boundary, which is why a model name, a
    /// yes/no flag and a gate count all rendered as the same free-text box.
    /// </summary>
    [PythonFact]
    public void AKitsOwnLabelsEnumerationsAndBounds_ReachTheHost()
    {
        var (script, pythonPath) = WriteSyntheticKit();
        using var provider = StartProvider(script, pythonPath);

        var declared = provider.DeclaredParameters("cap")!;

        var w = declared.Single(p => p.Name == "w");
        Assert.Equal("Width", w.Label);

        var guard = declared.Single(p => p.Name == "guard");
        Assert.Equal(["Yes", "No"], guard.Choices!.Select(c => c.AsText()));
        Assert.True(guard.IsYesNoPair);          // -> a checkbox, not a two-item dropdown

        var ng = declared.Single(p => p.Name == "ng");
        Assert.Equal(PCellValueKind.Int, ng.Kind);
        Assert.Equal(1, ng.Minimum);
        Assert.Equal(64, ng.Maximum);

        // A label identical to the name is not sent: it would say nothing and would suppress the
        // name the host shows in its place.
        Assert.Null(declared.Single(p => p.Name == "C").Label);
    }

    /// <summary>
    /// The cell's derived quantity is identified as one, and the parameters it genuinely reads are
    /// not. <b>Both halves of the rule are exercised here and each alone gets a different answer
    /// wrong.</b>
    ///
    /// <para>Reading the kit's <c>Calculate</c> selector literally says w and l are the outputs and C
    /// is the input — the exact opposite of what the code does, because the vendor's dialog is where
    /// the back-solve lived and the layout port kept only the declaration. Going purely by what the
    /// cell reads would instead take <c>model</c> away from the user, which is a netlist parameter
    /// that never had anything to do with the artwork.</para>
    /// </summary>
    [PythonFact]
    public void TheQuantityTheCellDerives_IsNamedAsAnOutput_AndNothingElseIs()
    {
        var (script, pythonPath) = WriteSyntheticKit();
        using var provider = StartProvider(script, pythonPath);

        Assert.True(provider.TryGetGenerator("res", out var generator));
        var result = generator(new Dictionary<string, PCellValue>(), Tech, NoLayers);

        Assert.Equal(["R"], result.ComputedParameters);

        // Named without a value: nothing computes the resistance — not the cell, and no calculator
        // beside it — and claiming a number for it would be inventing one. Naming it is still worth
        // doing on its own: it is what stops the field being offered as an input.
        Assert.Null(result.ComputedValues);
    }

    /// <summary>
    /// C is not merely unread — it cannot change the artwork, which is what makes locking its field
    /// the right call rather than a guess. Measured the only way that settles it: generate twice and
    /// compare, once varying the derived parameter and once varying a real input.
    /// </summary>
    [PythonFact]
    public void TheDerivedParameterCannotChangeTheGeometry_AndARealInputCan()
    {
        var (script, pythonPath) = WriteSyntheticKit();
        using var provider = StartProvider(script, pythonPath);
        Assert.True(provider.TryGetGenerator("cap", out var generator));

        static (long, long) Extent(PCellResult r)
        {
            var rect = Assert.IsType<RectShape>(Assert.Single(r.Shapes));
            return (rect.X2, rect.Y2);
        }

        var atDefaults = Extent(generator(new Dictionary<string, PCellValue>(), Tech, NoLayers));

        var cChanged = Extent(generator(
            new Dictionary<string, PCellValue> { ["C"] = PCellValue.Text("1p") }, Tech, NoLayers));
        Assert.Equal(atDefaults, cChanged);

        // ...and the selector saying "solve for C" does not change that either. The declaration is
        // not what decides; the code is.
        var cAndSelector = Extent(generator(
            new Dictionary<string, PCellValue>
            { ["Calculate"] = PCellValue.Text("C"), ["C"] = PCellValue.Text("1p") }, Tech, NoLayers));
        Assert.Equal(atDefaults, cAndSelector);

        var wChanged = Extent(generator(
            new Dictionary<string, PCellValue> { ["w"] = PCellValue.Text("20u") }, Tech, NoLayers));
        Assert.NotEqual(atDefaults, wChanged);
    }

    /// <summary>
    /// A derived value the cell itself cannot produce is supplied beside it, and tracks the geometry.
    ///
    /// <para>circuitRF can tell that C is an output; it cannot produce the NUMBER, because a derived
    /// value is a function only the cell knows and this cell does not compute it either. So the
    /// arithmetic is put back where the kit is set up, and the row stops showing whatever number the
    /// design happened to be stored with.</para>
    /// </summary>
    [PythonFact]
    public void ADerivedValueSuppliedBesideTheCell_TracksTheGeometry()
    {
        var (script, pythonPath) = WriteSyntheticKit();
        using var provider = StartProvider(script, pythonPath);
        Assert.True(provider.TryGetGenerator("cap", out var generator));

        // 6u x 6u
        var atDefaults = generator(new Dictionary<string, PCellValue>(), Tech, NoLayers);
        Assert.Equal("72.0f", atDefaults.ComputedValues!["C"].AsText());

        // 6u x 12u — the derived value follows, which is the whole point of reporting it per run.
        var wider = generator(
            new Dictionary<string, PCellValue> { ["l"] = PCellValue.Text("12u") }, Tech, NoLayers);
        Assert.Equal("144.0f", wider.ComputedValues!["C"].AsText());
    }

    /// <summary>
    /// The two sources of truth about a derived parameter compose. <b>This is the ordering that is
    /// easy to get wrong</b>: the host learns C is an output by MEASURING the cell, and records it
    /// with no value; a calculator supplied beside the cell then runs afterwards and must fill that
    /// in. A plain "don't overwrite what is already there" would see the name present, keep the
    /// empty claim, and drop every value silently — the readout would look wired up and never show
    /// a number.
    /// </summary>
    [PythonFact]
    public void AMeasuredOutputWithNoValue_IsFilledInByTheCalculator_NotLeftEmpty()
    {
        var (script, pythonPath) = WriteSyntheticKit();
        using var provider = StartProvider(script, pythonPath);
        Assert.True(provider.TryGetGenerator("cap", out var generator));

        var result = generator(new Dictionary<string, PCellValue>(), Tech, NoLayers);

        Assert.Equal(["C"], result.ComputedParameters);
        Assert.NotNull(result.ComputedValues);
        Assert.Equal("72.0f", result.ComputedValues!["C"].AsText());
    }
}
