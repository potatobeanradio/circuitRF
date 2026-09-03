using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Layout.PCells.Wire;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// What "Update Layout from Schematic" hands a KIT's own layout cell.
///
/// <para><b>Both rules here were broken at once on a kit, and the second one was silent.</b>
/// Every parameter on a placed component was pushed through the numeric expression resolver, so:</para>
///
/// <list type="number">
/// <item>circuitRF's own <c>ModelLibrary</c> row — a FILE PATH, present and blank on every kit part —
/// took the whole instance's artwork down with it. Blank gave
/// <c>parameter 'ModelLibrary': no value set — skipped</c>; filling it in, which is the obvious thing
/// to try, gave <c>Parse error at position 0: Unexpected token '/'</c>. Either way the part got no
/// layout at all, and neither message has anything to do with artwork.</item>
/// <item>A vendor cell library routinely declares every parameter as TEXT (its own defaults are
/// written <c>6.99u</c>, <c>600n</c>, <c>1</c>), and a NUMBER sent to a text parameter is ignored
/// without complaint — the cell draws its own default size, perfectly, and nothing says so. Measured
/// on the owner's kit: a 30 µm × 30 µm capacitor came back as the 6.99 µm default. That is worse than
/// the error above, because the result looks right.</item>
/// </list>
///
/// <para>The fixture is synthetic and names no vendor: a generator declaring a text-valued dimension
/// and no <c>ModelLibrary</c>, which is the shape every vendor cell library has.</para>
/// </summary>
[Collection(PdkToolsDirectoryCollection.Name)]
public sealed class KitPartLayoutParametersTests : IDisposable
{
    // Distinct from every other kit fixture's names: these registries are process-wide, and two
    // classes using one name would be reading each other's parts even when serialised, because a
    // class that does not clean up leaves its entry behind.
    private const string Kit       = "ParamRulesKit";
    private const string Part      = "PARAM_PART";
    private const string Generator = "param_cell";

    private readonly string _root;

    public KitPartLayoutParametersTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-kitparm-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);

        PdkKitRegistry.ResetAllForTests();
        PdkKitRegistry.SetKit(_root, Kit, [MakePart()]);

        PCellRegistry.ClearResolvers();
        PCellRegistry.AddResolver(new DeclaringResolver(_seen));

        // The palette's settled answer, exactly as WorkspaceViewModel publishes it: this kit's part
        // is drawn by this generator, whose id is deliberately NOT the part id (a kit names the two
        // independently — see KitPaletteMerge).
        KitLayoutGenerators.Publish(_root, [
            new PaletteItem(SymbolKind.Generic, 0, Part, ComponentCategory.Other, [Part], false, null,
                new PdkPartRef(Kit, Part, null, PdkKitRegistry.RefFor(Kit, Part), "", "a_model"),
                Generator),
        ]);
    }

    public void Dispose()
    {
        KitLayoutGenerators.ResetAllForTests();
        PCellRegistry.ClearResolvers();
        PdkKitRegistry.ResetAllForTests();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static PdkKitPart MakePart()
    {
        var sym = new Symbol(
            primitives: [new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal, -100, 0, 100, 0)],
            pins:       [new SymbolPin(-200, 0, 1, "a"), new SymbolPin(200, 0, 2, "b")],
            portCount:  2);
        return new PdkKitPart(Part, sym, new CcellFile { NumPorts = 2 }, IconPath: null);
    }

    /// <summary>The parameter set the generator was actually invoked with, per run. Per INSTANCE —
    /// xUnit builds a fresh instance per test method, so a static here would let one method's run
    /// leak into the next one's assertion.</summary>
    private readonly List<IReadOnlyDictionary<string, PCellValue>> _seen = [];

    /// <summary>
    /// A kit generator shaped like every real one: text-valued dimensions with the kit's own spelling
    /// as the default, and NO parameter called ModelLibrary — because no kit has ever heard of it.
    /// </summary>
    private sealed class DeclaringResolver(List<IReadOnlyDictionary<string, PCellValue>> seen)
        : IPCellGeneratorResolver
    {
        private static readonly Dictionary<string, PCellValue> Declared = new(StringComparer.Ordinal)
        {
            ["model"]   = PCellValue.Text("a_model"),
            ["Display"] = PCellValue.Text("Selected"),
            ["w"]       = PCellValue.Text("6.99u"),
            ["l"]       = PCellValue.Text("6.99u"),
            ["turns"]   = PCellValue.Int(3),
        };

        public IReadOnlyCollection<string> KnownGeneratorIds => [Generator];
        public string Describe() => "test resolver";
        public string? ContentKeyFor(string generatorId) => generatorId == Generator ? "v1" : null;

        public IReadOnlyDictionary<string, PCellValue>? DeclaredDefaults(string generatorId)
            => generatorId == Generator ? Declared : null;

        public PCellGenerator? Resolve(string generatorId)
        {
            if (generatorId != Generator) return null;
            return (parameters, _, _) =>
            {
                seen.Add(new Dictionary<string, PCellValue>(parameters, StringComparer.Ordinal));
                return new PCellResult(
                    [new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 }], []);
            };
        }
    }

    private SchematicEditModel ModelWith(params (string Name, string Expression)[] parameters)
    {
        var comp = new EditableComponent
        {
            InstanceName = "X1",
            Symbol       = SymbolKind.Generic,
            CellRef      = PdkKitRegistry.RefFor(Kit, Part),
            X = 0, Y = 0,
        };
        foreach (var (name, expr) in parameters)
            comp.Parameters.Add(new EditableParameter { Name = name, Expression = expr });

        var model = new SchematicEditModel { SchematicDirectory = _root };
        model.Components.Add(comp);
        return model;
    }

    private SchematicToLayoutGenerator.GenerationResult Run(SchematicEditModel model)
    {
        _seen.Clear();
        return SchematicToLayoutGenerator.Run(
            model, new LayoutView(), _root, _root, _root, null, null, null);
    }

    // ── ModelLibrary is not an artwork parameter ──────────────────────────────

    /// <summary>
    /// The reported failure, in both of the states the user saw it: blank (the default on every kit
    /// part) and filled in with a real library path.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("/kits/vendor/models/capacitors.lib")]
    public void ModelLibrary_NeverReachesTheArtwork_AndNeverCostsIt(string modelLibrary)
    {
        var result = Run(ModelWith((PdkPartInstaller.ModelLibraryParameter, modelLibrary), ("w", "30u")));

        Assert.Empty(result.NoLayoutWarnings);
        Assert.Equal(1, result.AddedCount);

        // Not merely "no warning" — the generator must never SEE it either. A generator handed a row
        // it does not declare is entitled to reject the whole call.
        Assert.DoesNotContain(PdkPartInstaller.ModelLibraryParameter, Assert.Single(_seen).Keys);
    }

    /// <summary>
    /// Every other row a kit part carries that is not a dimension either — a model-selection choice,
    /// a corner name. One unparseable row used to cost the instance its entire layout.
    /// </summary>
    [Fact]
    public void AParameterTheCellDoesNotDeclare_IsNotItsParameter()
    {
        var result = Run(ModelWith(
            ("Variant", "mos_tt"),                       // a word, not a number
            ("SomeKitRow", "$PDK_ROOT/x"),               // not even an expression
            ("w", "30u")));

        Assert.Empty(result.NoLayoutWarnings);
        var passed = Assert.Single(_seen);
        Assert.DoesNotContain("Variant", passed.Keys);
        Assert.DoesNotContain("SomeKitRow", passed.Keys);
    }

    // ── the silent half: kinds ────────────────────────────────────────────────

    /// <summary>
    /// The schematic says 30 µm; the cell must be TOLD 30 µm. A number sent to a text-declared
    /// parameter is discarded by the generator without complaint, so the assertion is on the KIND as
    /// well as the value — a Real here means the artwork silently reverts to the kit's own default.
    /// </summary>
    [Fact]
    public void ATextDeclaredDimension_ArrivesAsText_InTheEnginesOwnSiUnits()
    {
        // "30e-6" is the spelling a placed kit part actually carries: a kit writes its own default
        // "6.99u", and the importer has already normalized that into an expression circuitRF's
        // engine can read (measured on the owner's kit: w=7.0e-6).
        Run(ModelWith(("w", "30e-6"), ("l", "0.5")));
        var passed = Assert.Single(_seen);

        Assert.Equal(PCellValueKind.String, passed["w"].Kind);
        Assert.Equal(PCellValueKind.String, passed["l"].Kind);

        // The engine's base unit for a length is the metre, and this is the same value
        // TryResolveSiValue produces — formatted so it round-trips exactly.
        Assert.Equal(3E-05, double.Parse(passed["w"].AsText(), System.Globalization.CultureInfo.InvariantCulture), 15);
        Assert.Equal(0.5,   double.Parse(passed["l"].AsText(), System.Globalization.CultureInfo.InvariantCulture), 15);
    }

    /// <summary>
    /// A unit GLYPH on the row is honoured too — the parameter editor stores "µm", and a text-declared
    /// parameter must not be the one place that silently ignores it and sends 30 metres.
    /// </summary>
    [Fact]
    public void AUnitOnTheRow_IsAppliedBeforeTheTextIsFormed()
    {
        _seen.Clear();
        var model = new SchematicEditModel { SchematicDirectory = _root };
        model.Components.Add(new EditableComponent
        {
            InstanceName = "X1",
            Symbol       = SymbolKind.Generic,
            CellRef      = PdkKitRegistry.RefFor(Kit, Part),
            Parameters   = { new EditableParameter { Name = "w", Expression = "30", Unit = "µm" } },
        });

        SchematicToLayoutGenerator.Run(model, new LayoutView(), _root, _root, _root, null, null, null);

        Assert.Equal(3E-05, double.Parse(Assert.Single(_seen)["w"].AsText(),
                                         System.Globalization.CultureInfo.InvariantCulture), 15);
    }

    /// <summary>An expression is still an expression: it is EVALUATED, never handed over as source
    /// text for somebody else's parser to guess at.</summary>
    [Fact]
    public void ATextDeclaredParameterIsStillEvaluated_NotPassedThroughVerbatim()
    {
        var model = ModelWith(("w", "2*Wg"));
        model.Components.Add(new EditableComponent
        {
            InstanceName = "VAR1",
            Symbol       = SymbolKind.Var,
            Parameters   = { new EditableParameter { Name = "Wg", Expression = "10e-6" } },
        });

        Run(model);

        Assert.Equal(2E-05, double.Parse(Assert.Single(_seen)["w"].AsText(),
                                         System.Globalization.CultureInfo.InvariantCulture), 15);
    }

    /// <summary>
    /// A kit spells its values the way its own simulator does — <c>60u</c> — and circuitRF's
    /// expression engine does not read engineering suffixes (measured: <c>60u</c> is
    /// <c>Parse error at position 2</c>; <c>60</c> with the unit µm resolves). The cell's own parser
    /// DOES read it, so the artwork comes out right and the instance must not lose its layout over it.
    ///
    /// <para>But the same row goes to the simulator as an expression and fails there, a long way from
    /// here. So it is said once, at the point the value is used, and it is a note rather than a
    /// failure — which is the difference between a fixable row and a mystery at Run.</para>
    /// </summary>
    [Fact]
    public void AKitsOwnSuffixSpelling_ReachesTheCellVerbatim_AndIsReportedNotSwallowed()
    {
        var result = Run(ModelWith(("w", "60u")));

        Assert.Empty(result.NoLayoutWarnings);              // the artwork was still produced
        Assert.Equal("60u", Assert.Single(_seen)["w"].AsText());

        var line = Assert.Single(result.Lines, l => l.Text.Contains("60u"));
        Assert.Equal(SchematicToLayoutGenerator.ReportSeverity.Warning, line.Severity);
        Assert.Contains("unit field", line.Text);
        Assert.Contains("simulated", line.Text);
    }

    /// <summary>And a genuine word is not mistaken for a mistyped dimension — no note for it.</summary>
    [Fact]
    public void AWordValuedParameter_DrawsNoSuchNote()
    {
        var result = Run(ModelWith(("Display", "Selected"), ("model", "a_model")));

        Assert.DoesNotContain(result.Lines, l => l.Text.Contains("unit field"));
    }

    /// <summary>A genuinely word-valued parameter survives as the word it is.</summary>
    [Fact]
    public void ADeclaredParameterThatIsNotNumericAtAll_KeepsItsOwnText()
    {
        Run(ModelWith(("Display", "Selected"), ("model", "a_model")));
        var passed = Assert.Single(_seen);

        Assert.Equal("Selected", passed["Display"].AsText());
        Assert.Equal("a_model",  passed["model"].AsText());
    }

    /// <summary>A parameter the cell declares as a NUMBER is still resolved as one — the kind comes
    /// from the declaration, never from a guess about the text.</summary>
    [Fact]
    public void ANumericDeclaredParameter_IsStillResolvedNumerically()
    {
        Run(ModelWith(("turns", "5")));

        Assert.Equal(5.0, Assert.Single(_seen)["turns"].AsReal());
    }

    // ── what the instance does not say ────────────────────────────────────────

    /// <summary>
    /// An instance states three of a cell's parameters; the cell is generated at its own declared
    /// defaults for the rest — the SAME set a palette drop of that cell produces, so one parameter
    /// set is one cell rather than two identical ones under different names.
    /// </summary>
    [Fact]
    public void TheCellsOwnDeclaredDefaults_FillWhatTheInstanceDoesNotState()
    {
        Run(ModelWith(("w", "30u")));
        var passed = Assert.Single(_seen);

        Assert.Equal("6.99u", passed["l"].AsText());        // untouched by the instance
        Assert.Equal("a_model", passed["model"].AsText());
        Assert.Equal(3L, passed["turns"].AsInt());
    }

    // ── the built-ins are not affected ────────────────────────────────────────

    /// <summary>
    /// A built-in generator declares its interface in code, not over the wire, and is fed exactly as
    /// it was before. Pinned because the fix reads a DECLARATION to decide how to feed a generator,
    /// and a kit that happens to name a cell <c>MLIN</c> must not get to describe circuitRF's own.
    /// </summary>
    [Fact]
    public void ABuiltInGenerator_IsUnaffectedByAnyKitsDeclaration()
    {
        Assert.Null(PCellRegistry.DeclaredDefaults("MLIN"));

        var model = new SchematicEditModel { SchematicDirectory = _root };
        model.Components.Add(new EditableComponent
        {
            InstanceName = "TL1",
            Symbol       = SymbolKind.Mlin,
            X = 0, Y = 0,
            Parameters =
            {
                new EditableParameter { Name = "W", Expression = "1", Unit = "mm" },
                new EditableParameter { Name = "L", Expression = "5", Unit = "mm" },
            },
        });

        var result = SchematicToLayoutGenerator.Run(
            model, new LayoutView(), _root, _root, _root, null, null, null);

        Assert.Empty(result.NoLayoutWarnings);
        Assert.Equal(1, result.AddedCount);
    }
}
