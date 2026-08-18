using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// brief-wbond-wbb2 — placing a wBond in a schematic.
///
/// <para>A placed wBond CARRIES its wires (<c>WBondEmbedding</c>) rather than naming a <c>.wBond</c>:
/// a design file may also hold layout artwork, which a schematic has nowhere to put, and a component
/// that referenced one arrived pointing at nothing and drew the Not-Found placeholder. So the
/// property most of these fixtures are about is that the payload travels IN the schematic — several
/// of them delete the source file and assert the component still renders and runs.</para>
/// </summary>
public sealed class WBondSchematicPlacementTests : IDisposable
{
    private readonly string _root;

    public WBondSchematicPlacementTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"wbond-wbb2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".cws"), "{}");
        WBondSymbolProvider.InvalidateAll();
    }

    public void Dispose()
    {
        WBondSymbolProvider.InvalidateAll();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A design whose wires are BOUND to a loop profile, so a <c>LoopHeight</c> override has real
    /// geometry to regenerate — and whose wires rise above the plane, because a wire lying flat in
    /// it has zero loop inductance and the reduction is then singular.
    /// </summary>
    private static WBondDesign MakeDesign(double loopHeightMil, params string[] arrayNames)
    {
        var profile = LoopProfile.BallBond(WBondUnits.ToNm(loopHeightMil, WBondUnit.Mil), points: 7);
        var design  = new WBondDesign();
        design.Profiles.Add(profile);

        double y = 0;
        foreach (string name in arrayNames)
        {
            var array = new WireArray { Name = name, Profile = profile.Name };
            for (int i = 0; i < 2; i++, y += 6.0)
                array.Wires.Add(profile.CreateWire(
                    Point3.Mils(0, y, 4), Point3.Mils(100, y, 1),
                    WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold"));
            design.Arrays.Add(array);
            y += 20.0;   // clear gap between arrays
        }
        return design;
    }

    /// <summary>Writes a design at a workspace-relative path and returns its absolute path.</summary>
    private string WriteDesign(string relativePath, WBondDesign design)
    {
        string abs = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        WBondIo.WriteFile(abs, design);
        return abs;
    }

    /// <summary>
    /// A schematic that is deliberately NOT at the workspace root — <c>&lt;ws&gt;/Amp/schematic/</c>
    /// — which is the only place R-wbb2-3's two bases can be told apart.
    /// </summary>
    private SchematicEditModel NewSchematic(string cellName = "Amp")
    {
        string dir = Path.Combine(_root, cellName, "schematic");
        Directory.CreateDirectory(dir);
        return new SchematicEditModel { SchematicDirectory = dir };
    }

    /// <summary>A placed wBond carrying <paramref name="design"/> — the production shape.</summary>
    /// <param name="referencePin">
    /// Whether the placed component exposes the floating <c>REF</c> terminal.
    ///
    /// <para><b>True here, against the shipped default of false</b> (owner, 2026-08-16 — the pin is
    /// now optional and off, matching SnP's own <c>RefNode</c>). Most of the tests in this file were
    /// written about the 2M+1-terminal shape — REF's position in <c>NetBindings</c>, the terminal
    /// order the model publishes, a testbench that grounds it — and that shape is still a supported
    /// configuration, so they go on testing it rather than being weakened to the new default. The
    /// DEFAULT's own shape is pinned separately, by
    /// <see cref="APaletteDroppedWBond_RendersItsOwnSymbol_NotTheNotFoundPlaceholder"/> and by the
    /// round-4 tests.</para>
    /// </param>
    private static EditableComponent WBondAt(string instanceName, WBondDesign? design, double x, double y,
                                             bool referencePin = true)
    {
        var comp = WBondPlacement.BuildCarrying(design, instanceName);
        comp.X = x; comp.Y = y;
        comp.Parameters.First(p => p.Name == "RefPin").Expression = referencePin ? "true" : "false";
        return comp;
    }

    /// <summary>A placed wBond whose payload is set by hand — for the malformed/blank cases.</summary>
    private static EditableComponent WBondCarryingRaw(string instanceName, string payload, double x, double y)
    {
        var comp = WBondPlacement.BuildCarrying(null, instanceName);
        comp.X = x; comp.Y = y;
        comp.Parameters.First(p => p.Name == WBondPlacement.DesignParameter).Expression = payload;
        comp.Parameters.First(p => p.Name == WBondPlacement.ArraysParameter).Expression = "";
        return comp;
    }

    private static EditableWire Wire(params (double X, double Y)[] pts)
    {
        var w = new EditableWire();
        w.Points.AddRange(pts);
        return w;
    }

    private static Instance InstanceOf(NetExtractor.ExtractionResult r, string name)
        => r.TestBench.Instances.First(i => i.InstanceName == name);

    // ═══════════════════════════════════════════════════════════════════════════
    //  M1 — the component exists and can be placed
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>M1 — the palette offers exactly one wBond tile, under Other, and it is findable.</summary>
    [Fact]
    public void M1_ThePaletteOffersExactlyOneWBondTile()
    {
        var tiles = LibraryCatalog.AllItems.Where(i => i.Kind == SymbolKind.WBond).ToList();
        Assert.Single(tiles);
        Assert.Equal(ComponentCategory.Other, tiles[0].Category);

        // §5 question 4 — one tile for the component type, not one per .wBond in the workspace.
        Assert.Contains(tiles[0], LibraryCatalog.ByCategory(ComponentCategory.Other));

        foreach (string term in new[] { "wbond", "wirebond", "bond wire", "package" })
            Assert.Contains(LibraryCatalog.Search(term), i => i.Kind == SymbolKind.WBond);
    }

    /// <summary>M1 — the registry entry the extractor and the netlist both depend on.</summary>
    [Fact]
    public void M1_TheRegistryEntry_NamesTheEngineComponentAndParsesItsCode()
    {
        Assert.Equal("wBond", ComponentTypeRegistry.EngineReference(SymbolKind.WBond, 0));

        Assert.True(ComponentTypeRegistry.TryParseCode("WBOND", out var kind, out _));
        Assert.Equal(SymbolKind.WBond, kind);

        var defaults = ComponentTypeRegistry.DefaultParameters(SymbolKind.WBond, 0);
        Assert.Contains(defaults, p => p.Name == WBondPlacement.DesignParameter);
        Assert.Contains(defaults, p => p.Name == WBondPlacement.ArraysParameter);

        // WB-G / WB45 (wbond.md §9.7) REVERSES what this used to assert. `File` is declared again —
        // but BLANK, and inert until `Source` says Linked. The old assertion ("there is no File
        // parameter any more") was right for §5.0's world, where carrying was the only behaviour;
        // carrying is now the DEFAULT rather than the only option, and a linked instance genuinely
        // points at a path, so there is again something to browse for.
        Assert.Contains(defaults, p => p.Name == "File" && p.Expression.Length == 0);
        Assert.True(ComponentTypeRegistry.IsFilePathParameter(SymbolKind.WBond, "File"));

        // Carried by construction: a freshly placed wBond has no cell and no file to link to.
        Assert.Contains(defaults, p => p.Name == "Source"
                                    && p.Expression == nameof(WBondPlacement.WireSource.Carried));

        // §2.2 — the trap that must not ship. Every controlling parameter is declared and UNSET;
        // a wBond arriving with `LoopHeight = 20 mil` among its defaults would silently regenerate
        // every existing design's wires to 20 mil on its next run.
        foreach (string name in new[] { "LoopHeight", "Diameter", "Material", "Temp", "GroundPlane" })
            Assert.Contains(defaults, p => p.Name == name && p.Expression.Length == 0);
    }

    /// <summary>
    /// M1's headline gate — a placed wBond with a valid <c>File</c> shows 2M+1 pins named
    /// <c>G1.i</c>/<c>G1.o</c>/…/<c>REF</c> in ARRAY ORDER.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void M1_APlacedWBond_ShowsTwoPinsPerArrayPlusRef_InArrayOrder(int arrays)
    {
        var names = Enumerable.Range(1, arrays).Select(i => $"G{i}").ToArray();

        var model = NewSchematic();
        model.Components.Add(WBondAt("W1", MakeDesign(20.0, names), 0, 0));

        var (render, _) = model.BuildRenderModel();
        var comp = render.Components.Single();

        Assert.Equal(2 * arrays + 1, comp.Ports.Count);
        for (int k = 0; k < arrays; k++)
        {
            Assert.Equal($"{names[k]}.i", comp.Ports[2 * k].Name);
            Assert.Equal($"{names[k]}.o", comp.Ports[2 * k + 1].Name);
        }
        Assert.Equal("REF", comp.Ports[^1].Name);
    }

    /// <summary>
    /// M1 — a <c>.wBond</c> with no arrays is refused BY NAME, not placed with no pins.
    ///
    /// <para>A component that draws as an empty placeholder in the middle of a schematic is a worse
    /// answer than a message saying which file and why.</para>
    /// </summary>
    [Fact]
    public void M1_ADesignWithNoArrays_IsRefusedByName()
    {
        string abs = WriteDesign("bonds/empty.wBond", new WBondDesign());

        var built = WBondPlacement.TryBuild(abs, _root, "W1");

        Assert.Null(built.Component);
        Assert.NotNull(built.Error);
        Assert.Contains("empty.wBond", built.Error);
        Assert.Contains("no wire arrays", built.Error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <b>The reported bug.</b> A wBond dropped from the palette — no file, no configuration — must
    /// render a real symbol and extract, not the Not-Found placeholder.
    ///
    /// <para>It carries the default one-array, one-wire design, so it has G1.i / G1.o from the moment
    /// it lands — and NOT REF, which is off by default since 2026-08-16 (owner), matching SnP's own
    /// floating-reference-pin checkbox. This test is the one that pins the SHIPPED DEFAULT's shape;
    /// every other test in this file opts the pin back on through <c>WBondAt</c>, because the
    /// 2M+1-terminal configuration is what they were written about and is still supported.</para>
    /// </summary>
    [Fact]
    public void APaletteDroppedWBond_RendersItsOwnSymbol_NotTheNotFoundPlaceholder()
    {
        var model = NewSchematic();

        // Exactly what SchematicViewModel.CommitPlacement seeds a dropped component with.
        var comp = new EditableComponent { InstanceName = "W1", Symbol = SymbolKind.WBond };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.WBond, 0))
            comp.Parameters.Add(new EditableParameter
                { Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit, ShowOnSchematic = dp.ShowOnSchematic });
        model.Components.Add(comp);

        var rendered = model.BuildRenderModel().Model.Components.Single();
        Assert.Equal(2, rendered.Ports.Count);
        Assert.Equal("G1.i", rendered.Ports[0].Name);
        Assert.Equal("G1.o", rendered.Ports[1].Name);
        Assert.DoesNotContain(rendered.Ports, p => p.Name == "REF");

        // Resolved, not the placeholder — the state the report was about.
        Assert.Equal(CellSymbolState.Resolved,
            WBondSymbolProvider.Resolve(comp.ExternalSymbolRef!, model.SchematicDirectory).State);

        // And it is a real instance in the netlist rather than a refusal.
        var result = NetExtractor.Extract(model, "tb");
        Assert.Contains(result.TestBench.Instances, i => i.InstanceName == "W1");
    }

    /// <summary>
    /// A blank or unreadable payload draws the existing placeholder and is reported; nothing throws.
    ///
    /// <para>Only a hand-edited <c>.csch</c> can reach either state now — the placement paths always
    /// write a real payload — but the renderer must survive one, and the extractor must refuse it by
    /// name rather than emit a pin-less instance.</para>
    /// </summary>
    [Theory]
    [InlineData("")]                       // hand-cleared
    [InlineData("not a payload at all")]   // not base64, not JSON
    [InlineData("{ \"FormatVersion\": 1 }")]  // readable, declares no arrays
    public void AnUnreadablePayload_DrawsThePlaceholderAndReports_NeverThrows(string payload)
    {
        var model = NewSchematic();
        model.Components.Add(WBondCarryingRaw("W1", payload, 0, 0));

        var (render, _) = model.BuildRenderModel();
        Assert.Empty(render.Components.Single().Ports);

        var result = NetExtractor.Extract(model, "tb");
        Assert.DoesNotContain(result.TestBench.Instances, i => i.InstanceName == "W1");
        Assert.Contains(result.Conflicts, c => c.Contains("W1", StringComparison.Ordinal));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  The payload travels IN the schematic
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The property the whole change rests on:</b> a placed wBond keeps working when the
    /// <c>.wBond</c> it was imported from is DELETED. Nothing is resolved at render time, so there is
    /// no path to break and no "Not Found" state to reach.
    /// </summary>
    [Fact]
    public void APlacedWBond_StillRenders_AfterTheSourceDesignIsDeleted()
    {
        string abs = WriteDesign("bonds/pkg.wBond", MakeDesign(20.0, "G1", "G2"));

        var built = WBondPlacement.TryBuild(abs, _root, "W1");
        var comp  = Assert.IsType<EditableComponent>(built.Component);

        var model = NewSchematic();
        model.Components.Add(comp);

        // Two arrays, two pins each. WBondPlacement.TryBuild seeds the SHIPPED defaults, and the
        // external reference pin is off among them (owner, 2026-08-16).
        Assert.Equal(4, model.BuildRenderModel().Model.Components.Single().Ports.Count);

        File.Delete(abs);

        Assert.Equal(4, model.BuildRenderModel().Model.Components.Single().Ports.Count);
    }

    /// <summary>
    /// Importing a design that carries layout ARTWORK brings its wires only. A schematic has nowhere
    /// to put artwork, and copying someone's board into every placed instance is precisely what
    /// referencing a whole <c>.wBond</c> used to threaten.
    /// </summary>
    [Fact]
    public void ImportingADesignWithEmbeddedArtwork_CarriesTheWiresOnly()
    {
        var design = MakeDesign(20.0, "G1");
        design.EmbeddedGeometryJson = "{\"pretend\":\"a whole board\"}";
        string abs = WriteDesign("bonds/withart.wBond", design);

        var comp = Assert.IsType<EditableComponent>(WBondPlacement.TryBuild(abs, _root, "W1").Component);
        string payload = comp.Parameters.First(p => p.Name == WBondPlacement.DesignParameter).Expression;

        Assert.True(WBondEmbedding.TryDecode(payload, out var carried));
        Assert.Null(carried!.EmbeddedGeometryJson);
        Assert.Single(carried.Arrays);
        Assert.Equal(2, carried.WireCount);

        // Encoding must not have edited the caller's own design — it strips a COPY.
        Assert.NotNull(design.EmbeddedGeometryJson);
    }

    /// <summary>
    /// The payload survives a <c>.csch</c> round trip byte for byte, and the reloaded component still
    /// resolves to the same pins. A payload that only worked in memory would be worse than a path.
    /// </summary>
    [Fact]
    public void ThePayload_SurvivesASchematicRoundTrip()
    {
        var model = NewSchematic();
        model.Components.Add(WBondAt("W1", MakeDesign(20.0, "G1", "D1"), 0, 0));

        string schPath = Path.Combine(model.SchematicDirectory!, "Amp.csch");
        SchematicPersistence.SaveToFile(schPath, model, cellName: "Amp");

        var (reloaded, _, _) = SchematicPersistence.LoadFromFile(schPath);
        var before = model.Components[0].Parameters.First(p => p.Name == WBondPlacement.DesignParameter).Expression;
        var after  = reloaded.Components[0].Parameters.First(p => p.Name == WBondPlacement.DesignParameter).Expression;

        Assert.Equal(before, after);
        Assert.Equal(5, reloaded.BuildRenderModel().Model.Components.Single().Ports.Count);
    }

    /// <summary>
    /// The payload is a single bare token — no quote, no space, no newline — which is what lets it
    /// cross a <c>.cnl</c> line at all. The <c>.cnl</c> format has no way to escape a quote inside a
    /// quoted string, so a raw-JSON payload could not be written there.
    /// </summary>
    [Fact]
    public void ThePayload_IsASingleBareTokenSafeInACnlLine()
    {
        string payload = WBondEmbedding.Encode(MakeDesign(20.0, "G1", "G2"));

        Assert.DoesNotContain(payload, char.IsWhiteSpace);
        Assert.DoesNotContain('"', payload);

        Assert.True(WBondEmbedding.TryDecode(payload, out var back));
        Assert.Equal(["G1", "G2"], back!.Arrays.Select(a => a.Name));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  M2 — importing wires over a placed component
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A design imported over a SELECTED wBond replaces its wires in place, so the wiring the user
    /// already drew survives — which is the only reason to import into an existing component rather
    /// than place a new one.
    /// </summary>
    [Fact]
    public void Import_OverASelectedWBond_ReplacesItsWiresInPlace()
    {
        string abs = WriteDesign("bonds/pkg.wBond", MakeDesign(20.0, "G1", "D1"));

        var model = NewSchematic();
        var comp  = WBondAt("W1", MakeDesign(20.0, "G1"), 0, 0);
        model.Components.Add(comp);

        var vm = new SchematicViewModel(model);
        vm.Selection.SelectOne(comp.Id);

        Assert.Equal(1, vm.ImportWBondWires(abs));

        // Same component — replaced, not duplicated — now showing the imported design's pins.
        var only = Assert.Single(model.Components);
        Assert.Same(comp, only);
        Assert.Equal(5, model.BuildRenderModel().Model.Components.Single().Ports.Count);
        Assert.Equal("G1|D1", comp.Parameters.First(p => p.Name == WBondPlacement.ArraysParameter).Expression);
    }

    /// <summary>An import with nothing selected places a new component instead of failing.</summary>
    [Fact]
    public void Import_WithNothingSelected_PlacesANewComponent()
    {
        string abs = WriteDesign("bonds/pkg.wBond", MakeDesign(20.0, "G1", "D1"));

        var model = NewSchematic();
        var vm = new SchematicViewModel(model);

        Assert.Equal(1, vm.ImportWBondWires(abs));
        var comp = Assert.Single(model.Components);
        Assert.Equal(SymbolKind.WBond, comp.Symbol);

        // Two arrays, two pins each: an imported component gets the shipped defaults, and the
        // external reference pin is off among them (owner, 2026-08-16).
        Assert.Equal(4, model.BuildRenderModel().Model.Components.Single().Ports.Count);
    }

    /// <summary>One import is ONE undo entry, and undo puts the previous wires back.</summary>
    [Fact]
    public void Import_IsOneUndoEntry_AndUndoRestoresThePreviousWires()
    {
        string abs = WriteDesign("bonds/pkg.wBond", MakeDesign(20.0, "G1", "D1"));

        var model = NewSchematic();
        var comp  = WBondAt("W1", MakeDesign(20.0, "G1"), 0, 0);
        model.Components.Add(comp);
        string before = comp.Parameters.First(p => p.Name == WBondPlacement.DesignParameter).Expression;

        var vm = new SchematicViewModel(model);
        vm.Selection.SelectOne(comp.Id);
        vm.ImportWBondWires(abs);

        vm.UndoRedo.Undo();

        Assert.Equal(before,
            model.Components[0].Parameters.First(p => p.Name == WBondPlacement.DesignParameter).Expression);
        Assert.Equal(3, model.BuildRenderModel().Model.Components.Single().Ports.Count);
        Assert.False(vm.UndoRedo.CanUndo);
    }

    /// <summary>
    /// M2's silent-failure gate, and the one this whole brief is built around —
    /// <b>an import that REORDERS the arrays is REPORTED, never applied in silence.</b>
    ///
    /// <para>A reorder leaves every pin exactly where it was and moves its NAME to a different row,
    /// so a wire that was on <c>G1.i</c> is now on <c>G2.i</c>: correctly-named pins wired to the
    /// wrong nets. There is no re-mapping that keeps the user's wires correct without moving the
    /// artwork they drew, so the answer is to say so — and the message must name the reorder
    /// specifically, because "the array list changed" reads as something harmless.</para>
    ///
    /// <para>Now that the design travels inside the component, the import is the ONLY moment its pins
    /// can move — which is why the check lives here rather than on every load.</para>
    /// </summary>
    [Fact]
    public void M2_ImportingAReorderedArrayList_IsReportedRatherThanSilentlyRePointingTheWiring()
    {
        var comp = WBondAt("W1", MakeDesign(20.0, "G1", "G2"), 0, 0);

        // Nothing to report while the incoming design is what the wiring was drawn against.
        Assert.Null(WBondPlacement.DriftBetween(comp, MakeDesign(20.0, "G1", "G2"), "pkg.wBond"));

        // Same arrays, swapped order.
        var drift = WBondPlacement.DriftBetween(comp, MakeDesign(20.0, "G2", "G1"), "pkg.wBond");
        Assert.NotNull(drift);
        Assert.Equal("W1", drift!.InstanceName);
        Assert.Equal("G1|G2", drift.Recorded);
        Assert.Equal("G2|G1", drift.Current);
        Assert.True(drift.IsReorder, "a same-set/different-order change must be recognised as a reorder");
        Assert.Contains("REORDER", drift.Message, StringComparison.OrdinalIgnoreCase);
        // WB30a's own rule, applied here: name the remedy, not only the problem.
        Assert.Contains("Check the wiring", drift.Message, StringComparison.Ordinal);

        // And the pins really do move, which is what makes the report worth making.
        var model = NewSchematic();
        model.Components.Add(comp);
        WBondPlacement.ApplyDesign(comp, MakeDesign(20.0, "G2", "G1"));
        Assert.Equal("G2.i", model.BuildRenderModel().Model.Components.Single().Ports[0].Name);
    }

    /// <summary>
    /// M2 — an ADDED array is reported too, and is NOT called a reorder: the two need different
    /// answers from the user, and conflating them would hide the dangerous one inside the ordinary
    /// one.
    /// </summary>
    [Fact]
    public void M2_AnAddedArray_IsReported_ButNotAsAReorder()
    {
        var comp  = WBondAt("W1", MakeDesign(20.0, "G1"), 0, 0);
        var drift = WBondPlacement.DriftBetween(comp, MakeDesign(20.0, "G1", "D1"), "pkg.wBond");

        Assert.NotNull(drift);
        Assert.False(drift!.IsReorder);
        Assert.DoesNotContain("REORDER", drift.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// M2 — an instance with NO recorded array list (hand-authored) is not reported. Nothing is known
    /// about what it was wired against, and a warning that cannot be acted on is noise.
    /// </summary>
    [Fact]
    public void M2_AnInstanceWithNoRecordedArrayList_IsNotReported()
    {
        var comp = WBondAt("W1", MakeDesign(20.0, "G1"), 0, 0);
        comp.Parameters.First(p => p.Name == WBondPlacement.ArraysParameter).Expression = "";

        Assert.Null(WBondPlacement.DriftBetween(comp, MakeDesign(20.0, "G2", "G1"), "pkg.wBond"));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  M3 / R-wbb2-2 — the terminal ORDER is the contract
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// R-wbb2-2's ORACLE — two arrays wired to FOUR DIFFERENT nets, and the emitted
    /// <c>NetBindings</c> must name them in the order <c>WBondModel</c> reads them:
    /// <c>[G1.i, G1.o, G2.i, G2.o, REF]</c>.
    ///
    /// <para>A test that only counted terminals would pass a transposition — which is a circuit that
    /// solves, converges, and reports the wrong array's inductance on the wrong net. Distinct nets
    /// on every pin are what give this test teeth.</para>
    ///
    /// <para>§5 question 2 is answered here too: <b>REF DOES appear in NetBindings</b>, as the last
    /// entry, and the model ignores it. <c>WBondModel.PortCount</c> is 2M+1, so the elaborator binds
    /// 2M+1 nets; the stamp then uses 2M of them. WB20 makes REF a declaration the user has to be
    /// able to SAY, and a declaration nobody can wire is not one.</para>
    /// </summary>
    [Fact]
    public void RWbb22_NetBindings_ArriveInWBondModelsOwnTerminalOrder()
    {
        var design = MakeDesign(20.0, "G1", "G2");

        var model = NewSchematic();
        var comp  = WBondAt("W1", design, 0, 0);
        model.Components.Add(comp);

        // One labelled wire per pin, so every terminal lands on a distinguishable net.
        var (render, _) = model.BuildRenderModel();
        var rendered    = render.Components.Single();
        string[] expected = ["g1in", "g1out", "g2in", "g2out", "wbref"];

        for (int i = 0; i < rendered.Ports.Count; i++)
        {
            var p = rendered.Ports[i];
            var (wx, wy) = SchematicGeometry.LocalToWorld(
                p.LocalX, p.LocalY, comp.X, comp.Y, comp.Rotation, comp.MirrorX);
            model.Wires.Add(Wire((wx, wy), (wx + 100, wy)));
            model.NetLabels.Add(new EditableNetLabel { X = wx, Y = wy, Name = expected[i] });
        }

        var result = NetExtractor.Extract(model, "tb");
        var inst   = InstanceOf(result, "W1");

        Assert.Equal(expected, inst.NetBindings);

        // The same order WBondModel declares, read from the model itself rather than restated:
        // no independent list of terminal names to drift from the one the stamp uses.
        // referencePin: true to match WBondAt's own default — this test is about the 2M+1 shape,
        // in which REF is the last terminal and does appear in NetBindings.
        var wbModel = new CircuitRF.Core.Devices.WBondModel(design, referencePin: true);
        Assert.Equal(wbModel.PortCount, inst.NetBindings.Count);
        Assert.Equal("REF", wbModel.TerminalNames[^1]);
        Assert.Equal("wbref", inst.NetBindings[^1]);
    }

    /// <summary>
    /// R-wbb2-2 — the ordering above is by PIN NUMBER, not by list position. Feeding the resolver a
    /// symbol whose pin list is shuffled must still produce terminal order; this is the property the
    /// contract rests on, and the two coincide today only because the generator happens to emit
    /// them in order.
    /// </summary>
    [Fact]
    public void RWbb22_PinsAreWalkedByPinNumber_NotByListPosition()
    {
        var design = MakeDesign(20.0, "G1", "G2");

        // referencePin: true to match WBondAt below — the point of this test is the ORDER a shuffled
        // pin list resolves in, so both halves must describe the same component.
        var symbol = WBondSymbolGenerator.Build(design, referencePin: true)!;
        var shuffled = new Symbol(symbol.Primitives, [.. symbol.Pins.AsEnumerable().Reverse()]);

        var model = NewSchematic();
        var comp  = WBondAt("W1", design, 0, 0);
        model.Components.Add(comp);

        var byId = new Dictionary<string, CellSymbolResolution>(StringComparer.Ordinal)
        {
            [comp.Id] = new CellSymbolResolution { State = CellSymbolState.Resolved, Symbol = shuffled },
        };

        var defs = model.PortDefsOf(comp, byId);
        Assert.Equal(Enumerable.Range(1, 5), defs.Select(d => d.PortIndex));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  M3 — it runs end to end, and a loop-height sweep still works
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Extracts, writes the netlist where a real run writes it (the workspace root, which is what
    /// makes a workspace-relative <c>File</c> resolve at run time), and runs it.
    /// </summary>
    private RunResult RunPlaced(SchematicEditModel model)
    {
        var result = NetExtractor.Extract(model, "tb");
        Assert.Empty(result.Conflicts);

        string cnlPath = Path.Combine(_root, "netlist.cnl");
        File.WriteAllText(cnlPath, CnlWriter.Write(result.TestBench, result.Library));

        // baseDirectory is the WORKSPACE ROOT, exactly as WorkspaceViewModel.RunAnalysis passes it —
        // and it is what makes a workspace-relative `File` resolve at run time (R-wbb2-3). Omitting
        // it here would test a path the product never takes.
        return SchematicRunService.RunNetlist(cnlPath, baseDirectory: _root);
    }

    /// <summary>Two Terms across one array, REF grounded — the smallest thing that actually solves.</summary>
    private SchematicEditModel OneArrayTestbench(WBondDesign design, params Analysis[] analyses)
    {
        var model = NewSchematic();
        var comp  = WBondAt("W1", design, 0, 0);
        model.Components.Add(comp);

        var (render, _) = model.BuildRenderModel();
        var ports = render.Components.Single().Ports;
        Assert.Equal(3, ports.Count);   // G1.i, G1.o, REF

        (double X, double Y) World(int i) => SchematicGeometry.LocalToWorld(
            ports[i].LocalX, ports[i].LocalY, comp.X, comp.Y, comp.Rotation, comp.MirrorX);

        // Term "+" is at local (0,-200); place each Term so its + pin lands on the wBond pin.
        var (ix, iy) = World(0);
        var t1 = new EditableComponent { InstanceName = "T1", Symbol = SymbolKind.Term, X = ix, Y = iy + 200 };
        t1.Parameters.Add(new EditableParameter { Name = "Num", Expression = "1" });
        t1.Parameters.Add(new EditableParameter { Name = "Z",   Expression = "50" });

        var (ox, oy) = World(1);
        var t2 = new EditableComponent { InstanceName = "T2", Symbol = SymbolKind.Term, X = ox, Y = oy + 200 };
        t2.Parameters.Add(new EditableParameter { Name = "Num", Expression = "2" });
        t2.Parameters.Add(new EditableParameter { Name = "Z",   Expression = "50" });

        model.Components.Add(t1);
        model.Components.Add(t2);

        // Both Terms' "−" pins and the wBond's REF pin go to ground.
        var (rx, ry) = World(2);
        model.Components.Add(new EditableComponent
            { InstanceName = "GND1", Symbol = SymbolKind.Ground, X = rx, Y = ry });
        foreach (var t in new[] { t1, t2 })
            model.Components.Add(new EditableComponent
                { InstanceName = $"GND_{t.InstanceName}", Symbol = SymbolKind.Ground, X = t.X, Y = t.Y + 200 });

        foreach (var a in analyses) model.Analyses.Add(a);
        return model;
    }

    /// <summary>
    /// M3 — a placed wBond runs end to end through the product path: extract → <c>.cnl</c> →
    /// elaborate → S-parameter engine → <c>DataSet</c>.
    /// </summary>
    [Fact]
    public void M3_APlacedWBond_RunsEndToEnd()
    {
        var sp = new SParameterAnalysis("SP1",
            new FrequencySpec("1", "5", "1", SweepKind.Linear, "GHz", "GHz", "GHz"));

        // Nothing is written to disk anywhere: the .cnl carries the wires itself, which is the whole
        // of what "self-contained" means for a run.
        var run = RunPlaced(OneArrayTestbench(MakeDesign(20.0, "G1"), sp));

        Assert.True(run.Status == RunStatus.Success, run.StatusMessage);
        Assert.Single(run.DataSets);
        Assert.True(run.DataSets[0].Contains("S"));
    }

    /// <summary>
    /// M3 / WB21 — <b>a parametric sweep over a loop height still works from a PLACED component</b>,
    /// not only from a hand-authored netlist. This is the feature a PA designer actually uses the
    /// tool for, so it is gated on the geometry genuinely being regenerated: a taller loop is a more
    /// inductive one, and |S21| through a series inductance falls as the inductance rises.
    /// </summary>
    [Fact]
    public void M3_AParametricSweepOverLoopHeight_WorksFromAPlacedComponent()
    {
        // Both the base and the sweep stay ENABLED: a disabled base makes the whole chain inert
        // (AnalysisChain.IsChainRunnable), and the base is not dispatched standalone because a
        // sweep references it as its inner.
        var sp = new SParameterAnalysis("SP1",
            new FrequencySpec("5", "5", "1", SweepKind.Linear, "GHz", "GHz", "GHz"));

        // Sweep values are in base SI, which is what ParametricSweepAnalysis's array constructor
        // documents: 10 mil and 45 mil, expressed in metres.
        var sweep = new ParametricSweepAnalysis("SW1", "loopH", [10 * 25.4e-6, 45 * 25.4e-6], "SP1");

        var model = OneArrayTestbench(MakeDesign(20.0, "G1"), sp, sweep);

        // The loop height is an ordinary circuitRF expression bound to a global — which is exactly
        // what makes it sweepable (WB21).
        var wb = model.Components.First(c => c.Symbol == SymbolKind.WBond);
        wb.Parameters.Add(new EditableParameter { Name = "LoopHeight", Expression = "loopH" });

        // The global is declared UNITLESS, with its value in metres. When this test was written that
        // was the ONLY spelling that worked — a length-dimensioned global could not be swept at all,
        // silently, because Units.BaseUnit("mm") was "m" (the SI prefix MILLI) and "mil" was absent
        // from the base-unit map entirely. brief-core-length-units FIXED that; the unitless spelling
        // is still perfectly valid and is kept here so this test keeps measuring what it is about
        // (does a placed wBond regenerate under a sweep), not the units table.
        // M4_ASweptLoopHeightInMil_AgreesWithTheHandWrittenNetlist below is where the mil-declared
        // sweep is gated.
        var varComp = new EditableComponent { InstanceName = "VAR1", Symbol = SymbolKind.Var, X = -800, Y = -800 };
        varComp.Parameters.Add(new EditableParameter { Name = "loopH", Expression = "0.0005" });
        model.Components.Add(varComp);

        var run = RunPlaced(model);

        Assert.True(run.Status == RunStatus.Success, run.StatusMessage);
        var ds = Assert.Single(run.DataSets);
        Assert.True(ds.Contains("S"), "the swept run must still publish an S cube");

        var s = ds["S"];
        Assert.Contains(s.Axes, a => a.Name.Contains("loopH", StringComparison.OrdinalIgnoreCase));

        // The two loop heights must give DIFFERENT answers — a sweep that regenerates nothing
        // produces a perfectly plausible flat curve.
        //
        // S21 is indexed EXPLICITLY, from the cube's own axis lengths. Comparing the first and last
        // flattened elements would compare S11 at one height against S22 at the other, and on a
        // reciprocal two-port those are equal by symmetry — a comparison that passes whatever the
        // sweep did.
        int nSweep = s.Axes[0].Values.Length;
        int nFreq  = s.Axes[1].Values.Length;
        int nPort  = s.Axes[2].Values.Length;
        Assert.Equal(2, nSweep);

        int S21(int h) => ((h * nFreq + 0) * nPort + 1) * nPort + 0;
        var values = s.ComplexValues;

        Assert.NotEqual(values[S21(0)].Magnitude, values[S21(1)].Magnitude, 12);

        // …and in the direction the physics requires: a taller loop is more inductive, and more
        // series inductance passes less through. Asserting only "different" would pass a sweep that
        // regenerated the geometry backwards.
        Assert.True(values[S21(1)].Magnitude < values[S21(0)].Magnitude,
            $"a 45 mil loop must pass less than a 10 mil one; got |S21| {values[S21(1)].Magnitude:G6} " +
            $"against {values[S21(0)].Magnitude:G6}.");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  brief-core-length-units M4 — the phase gate: WB21's own sweep, in mil
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The effective series inductance a two-port series element presents between two <c>Z0</c>
    /// terminations, recovered from its own published <c>S21</c>: for a series <c>Z</c> between two
    /// ports referenced to ground, <c>S21 = 2·Z0 / (2·Z0 + Z)</c>, so <c>Z = 2·Z0·(1/S21 − 1)</c> and
    /// <c>L = Im(Z)/ω</c>. Derived from the definition rather than read out of the model, so it is an
    /// independent measurement of what the run actually published.
    /// </summary>
    private static double SeriesInductanceH(System.Numerics.Complex s21, double freqHz, double z0 = 50.0)
    {
        var z = 2 * z0 * (System.Numerics.Complex.One / s21 - System.Numerics.Complex.One);
        return z.Imaginary / (2 * Math.PI * freqHz);
    }

    /// <summary>
    /// <b>brief-core-length-units M4 — the phase gate.</b> A wBond loop-height sweep at 10 mil and
    /// 45 mil, driven through the product path from a PLACED component with a <c>mil</c>-DECLARED
    /// global, must produce exactly what the same two heights produce from a hand-written netlist.
    ///
    /// <para>This is the one claim in that brief that proves the fix reaches a user rather than a
    /// dictionary. Before it, <c>Units.BaseUnit("mil")</c> returned <c>"mil"</c> — absent from the
    /// base-unit map — so <see cref="ParametricSweepEngine"/>'s already-SI sweep value was re-scaled
    /// by a further 2.54e-5 on its way back in. Compounded with the table's own error a <c>mil</c>
    /// sweep landed at 6.45e-10 of intent: the loop height collapsed below the wire's own foot drop,
    /// clamped to the same geometry at both ends, and the sweep produced a perfectly plausible FLAT
    /// curve rather than an error. That is exactly the failure WB-B2 measured and left standing.</para>
    ///
    /// <para><b>The oracle is the hand-written netlist, not a stored number.</b> Each height is also
    /// run on its own, with <c>LoopHeight</c> a literal in metres and no sweep anywhere — so the two
    /// paths share no unit resolution at all. The measured inductances are reported for the record;
    /// the assertion is that the two paths AGREE.</para>
    /// </summary>
    [Fact]
    public void M4_ASweptLoopHeightInMil_AgreesWithTheHandWrittenNetlist()
    {
        const double freqHz = 5e9;
        const double milM   = 2.54e-5;

        // ── The swept run: a mil-DECLARED global, swept 10 → 45 mil ──────────
        var sp = new SParameterAnalysis("SP1",
            new FrequencySpec("5", "5", "1", SweepKind.Linear, "GHz", "GHz", "GHz"));

        // The sweep spec carries "mil" too, so ParametricSweepAnalysis scales the coefficients to SI
        // at expansion and ParametricSweepEngine re-attaches BaseUnit("mil") = "metre" (scale 1.0) —
        // the property M2 gates, exercised here through the product path.
        var sweep = new ParametricSweepAnalysis("SW1", "loopH",
            new SweepSpec(10, 45, 2, SweepAxisMode.PointCount, SweepKind.Linear, "mil"), "SP1");

        var sweptModel = OneArrayTestbench(MakeDesign(20.0, "G1"), sp, sweep);

        var wb = sweptModel.Components.First(c => c.Symbol == SymbolKind.WBond);
        wb.Parameters.Add(new EditableParameter { Name = "LoopHeight", Expression = "loopH" });

        var varComp = new EditableComponent
            { InstanceName = "VAR1", Symbol = SymbolKind.Var, X = -800, Y = -800 };
        varComp.Parameters.Add(new EditableParameter
            { Name = "loopH", Expression = "10", Unit = "mil" });
        sweptModel.Components.Add(varComp);

        var sweptRun = RunPlaced(sweptModel);
        Assert.True(sweptRun.Status == RunStatus.Success, sweptRun.StatusMessage);

        var sweptS = Assert.Single(sweptRun.DataSets)["S"];
        int nFreq = sweptS.Axes[1].Values.Length;
        int nPort = sweptS.Axes[2].Values.Length;
        Assert.Equal(2, sweptS.Axes[0].Values.Length);

        // The axis itself must already carry the hand-computed SI metres.
        Assert.Equal(10 * milM, sweptS.Axes[0].Values[0], 15);
        Assert.Equal(45 * milM, sweptS.Axes[0].Values[1], 15);

        int S21(int h) => ((h * nFreq + 0) * nPort + 1) * nPort + 0;
        var swept10 = sweptS.ComplexValues[S21(0)];
        var swept45 = sweptS.ComplexValues[S21(1)];

        // ── The oracle: the same two heights, hand-written, no sweep anywhere ──
        System.Numerics.Complex HandWritten(double heightMetres)
        {
            var spOnly = new SParameterAnalysis("SP1",
                new FrequencySpec("5", "5", "1", SweepKind.Linear, "GHz", "GHz", "GHz"));

            var model = OneArrayTestbench(MakeDesign(20.0, "G1"), spOnly);
            var comp  = model.Components.First(c => c.Symbol == SymbolKind.WBond);

            // A literal, in metres, with no unit and no variable — so this path shares no unit
            // resolution at all with the swept one above.
            comp.Parameters.Add(new EditableParameter
            {
                Name       = "LoopHeight",
                Expression = heightMetres.ToString("G17", CultureInfo.InvariantCulture),
            });

            var run = RunPlaced(model);
            Assert.True(run.Status == RunStatus.Success, run.StatusMessage);

            var s = Assert.Single(run.DataSets)["S"];
            int nf = s.Axes[0].Values.Length;
            int np = s.Axes[1].Values.Length;
            return s.ComplexValues[((0 * nf + 0) * np + 1) * np + 0];
        }

        var hand10 = HandWritten(10 * milM);
        var hand45 = HandWritten(45 * milM);

        // ── The gate ─────────────────────────────────────────────────────────
        Assert.Equal(hand10.Real,      swept10.Real,      12);
        Assert.Equal(hand10.Imaginary, swept10.Imaginary, 12);
        Assert.Equal(hand45.Real,      swept45.Real,      12);
        Assert.Equal(hand45.Imaginary, swept45.Imaginary, 12);

        // …and the sweep genuinely regenerated the geometry: a taller loop is more inductive, so it
        // passes less. Agreement alone would be satisfied by two identical (clamped) answers.
        double l10 = SeriesInductanceH(swept10, freqHz);
        double l45 = SeriesInductanceH(swept45, freqHz);

        Assert.True(l45 > l10,
            $"a 45 mil loop must be more inductive than a 10 mil one; got {l45 * 1e12:F1} pH " +
            $"against {l10 * 1e12:F1} pH.");
        Assert.True(swept45.Magnitude < swept10.Magnitude,
            $"a 45 mil loop must pass less than a 10 mil one; got |S21| {swept45.Magnitude:G6} " +
            $"against {swept10.Magnitude:G6}.");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  R-wbb2-4 — the coupling audit fires from the placement path
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// R-wbb2-4 / WB30a — <b>two placed wBonds whose wires are close together warn, from the run.</b>
    ///
    /// <para>Before this phase the audit was reachable only from a hand-constructed netlist in a
    /// test: nothing in the product called it. Placing a SECOND wBond is the moment it becomes
    /// reachable by an ordinary user, and with <c>CouplingDomain</c> deferred to v2 it is the whole
    /// of the v1 safety mechanism — so the message must also name the manual remedy.</para>
    /// </summary>
    [Fact]
    public void RWbb24_TwoPlacedWBonds_WarnFromTheRun_AndTheMessageNamesTheRemedy()
    {
        var sp = new SParameterAnalysis("SP1",
            new FrequencySpec("1", "2", "1", SweepKind.Linear, "GHz", "GHz", "GHz"));

        var model = OneArrayTestbench(MakeDesign(20.0, "G1"), sp);

        // A second wBond, its wires in the same place as the first's — the case the audit exists for.
        var second = WBondAt("W2", MakeDesign(20.0, "D1"), 2000, 2000);
        model.Components.Add(second);
        var secondPorts = model.BuildRenderModel().Model.Components.First(c => c.Id == second.Id).Ports;
        for (int i = 0; i < secondPorts.Count; i++)
        {
            var (wx, wy) = SchematicGeometry.LocalToWorld(
                secondPorts[i].LocalX, secondPorts[i].LocalY,
                second.X, second.Y, second.Rotation, second.MirrorX);
            model.Components.Add(new EditableComponent
                { InstanceName = $"GND_W2_{i}", Symbol = SymbolKind.Ground, X = wx, Y = wy });
        }

        var run = RunPlaced(model);

        Assert.True(run.Status == RunStatus.Success, run.StatusMessage);
        Assert.Contains(run.Warnings, w =>
            w.Contains("W1", StringComparison.Ordinal) && w.Contains("W2", StringComparison.Ordinal));
        Assert.Contains(run.Warnings, w =>
            w.Contains("not modelled", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The audit's own control: ONE placed wBond warns about nothing. An audit that fires on every
    /// run is one people learn to ignore, which would defeat the one above.
    /// </summary>
    [Fact]
    public void RWbb24_OnePlacedWBond_WarnsAboutNoCoupling()
    {
        var sp = new SParameterAnalysis("SP1",
            new FrequencySpec("1", "2", "1", SweepKind.Linear, "GHz", "GHz", "GHz"));

        var run = RunPlaced(OneArrayTestbench(MakeDesign(20.0, "G1"), sp));

        Assert.True(run.Status == RunStatus.Success, run.StatusMessage);
        Assert.DoesNotContain(run.Warnings, w => w.Contains("coupling", StringComparison.OrdinalIgnoreCase));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  M4 — route 3: wires AND geometry as a new cell
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// M4 — a <c>.wBond</c> with NO embedded geometry is route 2, not a failure. The check that
    /// diverts it is the design's own <c>EmbeddedGeometryJson</c>, so there is nothing to guess.
    /// </summary>
    [Fact]
    public void M4_ADesignWithNoEmbeddedGeometry_IsRoute2NotAFailure()
    {
        var design = MakeDesign(20.0, "G1");
        Assert.Null(design.EmbeddedGeometryJson);

        // It is still perfectly placeable as a component — which is what "route 2, not a failure"
        // means concretely.
        string abs = WriteDesign("bonds/pkg.wBond", design);
        Assert.NotNull(WBondPlacement.TryBuild(abs, _root, "W1").Component);
    }

    /// <summary>
    /// M4 — the cell route, composed exactly as <c>WorkspaceViewModel.AddWBondAsCellAsync</c>
    /// composes it (that method needs a live window and cannot be constructed headlessly, so the
    /// sequence is mirrored here — this repo's own convention for workspace-level logic).
    ///
    /// <para>Asserts the three things that make it a real cell: a layout view whose instances still
    /// RESOLVE after being rebased out of the unpack folder, a schematic view holding the wBond
    /// component, and a <c>.ccell</c> naming both primaries.</para>
    /// </summary>
    [Fact]
    public void M4_ADesignWithEmbeddedGeometry_BecomesACellWithBothViews()
    {
        // A tiny referenced layout: one sub-cell, instanced once by the root.
        string subCellDir = Path.Combine(_root, "Pad");
        CellFolder.CreateCellFolder(_root, "Pad");
        string subLayoutDir = CellFolder.SubFolderPath(subCellDir, ViewType.Layout);
        var subView = new LayoutView();
        subView.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });
        LayoutPersistence.SaveToFile(Path.Combine(subLayoutDir, "Pad.clay"), subView);
        CellPersistence.SaveToFile(Path.Combine(subCellDir, CellFolder.CcellFileName),
            new CcellFile { PrimaryLayout = "Pad.clay" });

        string rootLayoutDir = Path.Combine(_root, "Board", "layout");
        Directory.CreateDirectory(rootLayoutDir);
        var rootView = new LayoutView();
        rootView.Instances.Add(new LayoutInstance
            { CellRef = Path.GetRelativePath(rootLayoutDir, subCellDir), X = 0, Y = 0, Mag = 1.0 });

        var design = MakeDesign(20.0, "G1", "G2");
        design.EmbeddedGeometryJson = WBondGeometryEmbedding.Embed(rootView, rootLayoutDir);
        string wbondPath = WriteDesign("bonds/pkg.wBond", design);

        // ── the production sequence ───────────────────────────────────────────
        const string name = "Package";
        string cellDir = Path.Combine(_root, name);
        CellFolder.CreateCellFolder(_root, name);

        var unpacked = WBondGeometryEmbedding.Unpack(design.EmbeddedGeometryJson, Path.Combine(cellDir, "geometry"));
        Assert.NotNull(unpacked);
        var (unpackedRoot, unpackedBaseDir) = unpacked!.Value;

        string layoutDir  = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        string layoutFile = name + CellFolder.ViewExtension(ViewType.Layout);
        foreach (var inst in unpackedRoot.Instances)
            inst.CellRef = LayoutFlatten.RebaseCellRef(inst.CellRef, unpackedBaseDir, layoutDir);
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, layoutFile), unpackedRoot);

        string schematicDir  = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        string schematicFile = name + CellFolder.ViewExtension(ViewType.Schematic);
        var built = WBondPlacement.TryBuild(wbondPath, _root, "W1");
        Assert.NotNull(built.Component);

        var schModel = new SchematicEditModel();
        schModel.Components.Add(built.Component!);
        SchematicPersistence.SaveToFile(Path.Combine(schematicDir, schematicFile), schModel, cellName: name);

        CellPersistence.SaveToFile(Path.Combine(cellDir, CellFolder.CcellFileName),
            new CcellFile { PrimarySchematic = schematicFile, PrimaryLayout = layoutFile });

        // ── the gate ──────────────────────────────────────────────────────────
        // The .ccell names its primary views, so the cell is placeable in another schematic like
        // any other.
        var ccell = CellPersistence.LoadFromFile(Path.Combine(cellDir, CellFolder.CcellFileName));
        Assert.Equal(layoutFile,    ccell.PrimaryLayout);
        Assert.Equal(schematicFile, ccell.PrimarySchematic);
        Assert.Equal(layoutFile,    CellFolder.ResolvePrimary(cellDir, ViewType.Layout).ResolvedName);
        Assert.Equal(schematicFile, CellFolder.ResolvePrimary(cellDir, ViewType.Schematic).ResolvedName);

        // The layout view WORKS: its instance resolves after the rebase. Writing the .clay somewhere
        // other than the folder Unpack resolved against is exactly the step that would otherwise
        // leave a cell full of Not-Found placeholders.
        var reloadedLayout = LayoutPersistence.LoadFromFile(Path.Combine(layoutDir, layoutFile));
        var instance = Assert.Single(reloadedLayout.Instances);
        Assert.Equal(CellLayoutState.Resolved,
            CellLayoutResolver.Resolve(instance.CellRef, layoutDir).State);

        // The schematic view holds the wBond, and it resolves to its 2M pins from inside the cell —
        // two arrays, two pins each, and no REF, which is off in the shipped defaults an imported
        // component is seeded with (owner, 2026-08-16).
        var (reloadedSch, _, _) = SchematicPersistence.LoadFromFile(Path.Combine(schematicDir, schematicFile));
        var placed = reloadedSch.BuildRenderModel().Model.Components.Single();
        Assert.Equal(4, placed.Ports.Count);
        Assert.DoesNotContain(placed.Ports, p => p.Name == "REF");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Placement plumbing
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The placement path records the array list it was wired against, so the very first change to
    /// the design is reportable. An instance placed with nothing recorded can never be checked.
    /// </summary>
    [Fact]
    public void Placement_RecordsTheArrayListTheWiringWasDrawnAgainst()
    {
        string abs = WriteDesign("bonds/pkg.wBond", MakeDesign(20.0, "G1", "D1"));

        var built = WBondPlacement.TryBuild(abs, _root, "W1");
        var comp  = Assert.IsType<EditableComponent>(built.Component);

        Assert.True(WBondEmbedding.TryDecode(
            comp.Parameters.First(p => p.Name == WBondPlacement.DesignParameter).Expression, out var carried));
        Assert.Equal(["G1", "D1"], carried!.Arrays.Select(a => a.Name));
        Assert.Equal("G1|D1",
            comp.Parameters.First(p => p.Name == WBondPlacement.ArraysParameter).Expression);
    }

    /// <summary>
    /// <c>Arrays</c> is circuitRF's own bookkeeping and must never reach the engine, and a blank
    /// value must be DROPPED rather than emitted — an empty <c>Temp=</c> in a <c>.cnl</c> is the
    /// trap where the reader glues the next token on as the value and eats the parameters after it.
    /// </summary>
    [Fact]
    public void Extraction_DropsTheArraysBookkeepingAndEveryBlankParameter()
    {
        var model = NewSchematic();
        var comp  = WBondAt("W1", MakeDesign(20.0, "G1"), 0, 0);
        model.Components.Add(comp);

        var inst = InstanceOf(NetExtractor.Extract(model, "tb"), "W1");

        Assert.DoesNotContain(inst.Overrides, o => o.Name == WBondPlacement.ArraysParameter);
        Assert.DoesNotContain(inst.Overrides, o => string.IsNullOrWhiteSpace(o.Expression));
        Assert.Contains(inst.Overrides, o => o.Name == WBondPlacement.DesignParameter);
    }

    /// <summary>
    /// A wBond that fails to resolve has NO built-in geometry to fall back to. Inventing two
    /// terminals for it would emit a two-net instance for a component the elaborator expects 2M+1
    /// nets from — a different circuit that still parses.
    /// </summary>
    [Fact]
    public void AnUnresolvedWBond_NeverFallsBackToTwoTerminalGeometry()
    {
        Assert.Empty(SymbolPortDefs.For(SymbolKind.WBond, 2));

        var model = NewSchematic();
        model.Components.Add(WBondCarryingRaw("W1", "", 0, 0));

        Assert.Empty(model.PortDefsOf(model.Components[0]));
    }

    /// <summary>
    /// The embedded payload must carry NO base64 padding.
    ///
    /// <para><c>CnlReader.MergeSpacedAssignments</c> reads a token ending in <c>=</c> as
    /// <c>name=</c> with an empty value and glues the NEXT token on as that value — the known
    /// empty-parameter-value defect recorded in <c>src/Core/CLAUDE.md</c>. A padded payload is
    /// therefore fine as the LAST parameter on an instance line and silently swallows whichever
    /// parameter follows it, so this asserts the round trip with a following parameter present.
    /// That is exactly the shape a swept <c>LoopHeight</c> produces.</para>
    /// </summary>
    [Fact]
    public void TheEmbeddedPayload_CarriesNoBase64Padding_SoAFollowingParameterSurvivesTheCnl()
    {
        string payload = WBondEmbedding.DefaultPayload;
        Assert.DoesNotContain('=', payload);
        Assert.True(WBondEmbedding.TryDecode(payload, out _));

        // A padded payload still decodes — an older file, or a hand-authored one, must not break.
        // Built as canonical base64 of the same bytes, so this is the exact form that used to ship.
        string padded = Convert.ToBase64String(Convert.FromBase64String(
            payload + new string('=', (4 - payload.Length % 4) % 4)));
        Assert.EndsWith("=", padded);
        Assert.True(WBondEmbedding.TryDecode(padded, out _));

        var tb = new TestBench("TB");
        tb.Instances.Add(new Instance("W1", "wBond", ["n1", "n2", "0"],
        [
            new ParameterAssignment(WBondPlacement.DesignParameter, payload),
            new ParameterAssignment("LoopHeight", "loopH"),
        ]));
        tb.GlobalVariables.Add(new Variable("loopH", "0.000254"));

        string cnlPath = Path.Combine(_root, "padding.cnl");
        File.WriteAllText(cnlPath, CnlWriter.Write(tb, new Library("lib")));

        var read = CnlReader.ReadFile(cnlPath);
        var readInst = Assert.Single(read.TestBench.Instances);

        // The parameter AFTER the payload is what the padding would have eaten.
        var loop = Assert.Single(readInst.Overrides, o => o.Name == "LoopHeight");
        Assert.Equal("loopH", loop.Expression);

        var design = Assert.Single(readInst.Overrides, o => o.Name == WBondPlacement.DesignParameter);
        Assert.True(WBondEmbedding.TryDecode(design.Expression.Trim('"'), out var decoded));
        Assert.NotNull(decoded);
        Assert.NotEmpty(decoded!.Arrays);
    }
}
