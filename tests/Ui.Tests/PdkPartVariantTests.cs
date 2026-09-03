using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Pdk;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Some kits ship several formulations of the same part and a parameter that picks between them.
/// circuitRF cannot work out which formulations exist, what the parameter is called, which one
/// should be the default, or which ones it can actually build — so the kit states all four as data
/// and these tests drive that data through the real installer, editor row and extractor.
///
/// <para>The behaviour that matters most: a freshly placed part arrives on the choice that WORKS, so
/// importing a kit and pressing Run produces results rather than an explanation.</para>
/// </summary>
[Collection(PdkToolsDirectoryCollection.Name)]
public sealed class PdkPartVariantTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "crf-var-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>
    /// Two levels below the scratch directory ON PURPOSE — library discovery widens outward when the
    /// narrower search finds nothing, and a root sitting directly in the system temp folder lets the
    /// walk reach another concurrently-running test's fixtures.
    /// </summary>
    private string _root => Path.Combine(_scratch, "delivery", "root");

    private string KitDir       => Path.Combine(_root, "kit");
    private string WorkspaceDir => Path.Combine(_root, "ws");
    private string SchematicDir => Path.Combine(WorkspaceDir, "tb", "schematic");

    public PdkPartVariantTests()
    {
        PdkKitRegistry.ResetAllForTests();
        Directory.CreateDirectory(KitDir);
        Directory.CreateDirectory(SchematicDir);
    }

    public void Dispose()
    {
        PdkKitRegistry.ResetAllForTests();
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best effort */ }
    }

    private const string SymbolFile = """
        1     7.707    0 0
        10    1    "PART_SYM"    2    1    0    0    341    0
        20    0    ""    0 0 0 0 0    2 -3 1    1    0    "schematic.prf" "schematic.lay"
        44    0    -600    600    600    1    0    0
        50    2    0 0 500 0 1    0    0    0    0    0    0    0    0
        60    4    0    2    0 0 500 0 1    0    0    0    0
        70    0 0    500 0
        42    1    2    "gate"      1    2    0    0 0 180000    0    0   ""
        42    2    2    "drain"     2    1    0    500 0 0    0    0   ""
        21
        """;

    /// <summary>A kit declaring two formulations, only the first of which circuitRF can build.</summary>
    private const string ManifestWithVariant = """
        {
          "workers":  [ { "platform": "any", "command": "worker" } ],
          "variants": [ { "parameter":   "ModelAs",
                          "choices":     ["Compact", "Behavioural"],
                          "default":     "Compact",
                          "unsupported": ["Behavioural"] } ]
        }
        """;

    private string InstallPart(string manifestJson, params PdkPartParameter[] declared)
    {
        File.WriteAllText(Path.Combine(KitDir, DeviceWorkerManifest.FileName), manifestJson);

        string symRel = Path.Combine("symbols", "part.dsn");
        string symAbs = Path.Combine(KitDir, symRel);
        Directory.CreateDirectory(Path.GetDirectoryName(symAbs)!);
        File.WriteAllText(symAbs, SymbolFile);

        var report = new PdkImportReport { RootPath = KitDir, KitName = "SampleKit" };
        report.Parts.Add(new PdkPart(
            "PART_A", "Part A",
            SymbolArtwork: new PdkAsset(symRel.Replace(Path.DirectorySeparatorChar, '/'),
                                        PdkAssetKind.SymbolArtwork, PdkAssetSupport.Supported,
                                        "symbol description (.dsn)"),
            Parameters: declared.Length > 0 ? declared : null));

        var outcome = PdkPartInstaller.Install(report);
        PdkKitRegistry.SetKit(null, outcome.KitName, outcome.Parts ?? []);
        _lastSettings = outcome.Settings;
        return outcome.Items[0].Pdk!.CellDir!;
    }

    private JsonNode? _lastSettings;

    /// <summary>The settings the installer settled on for the kit this manifest describes.</summary>
    private JsonNode? LastSettings(string manifestJson)
    {
        InstallPart(manifestJson);
        return _lastSettings;
    }

    /// <summary>
    /// The part's published interface. Held in memory rather than read from a <c>.ccell</c> on disk —
    /// a kit part is not the user's cell and no longer has a folder of its own.
    /// </summary>
    private static CcellFile Installed(string cellRef)
        => PdkKitRegistry.Find(cellRef, null)?.Ccell
           ?? throw new Xunit.Sdk.XunitException($"no kit part is loaded for '{cellRef}'");

    private (SchematicEditModel Model, EditableComponent Comp) Placed(string cellRef)
    {
        var model = new SchematicEditModel { SchematicDirectory = SchematicDir };
        var comp = new EditableComponent
        {
            InstanceName = "X1",
            Symbol       = SymbolKind.Generic,
            // Virtual, not a relative path: the part exists in memory only, so there is nothing for
            // a relative path to be relative to.
            CellRef      = cellRef,
            X = 0, Y = 0,
        };

        // Placement seeds the instance from the cell's published interface — mirrored here, because
        // the real seeding lives on SchematicViewModel, which needs an Avalonia host to construct.
        foreach (var cp in Installed(cellRef).Parameters)
            comp.Parameters.Add(new EditableParameter
            {
                Name = cp.Name, Expression = cp.DefaultExpression, Unit = cp.Unit,
                Dimension = cp.Dimension, ShowOnSchematic = cp.ShowOnSchematic,
            });

        model.Components.Add(comp);
        return (model, comp);
    }

    // ── Discovery and defaults ────────────────────────────────────────────────

    [Fact]
    public void ADeclaredVariant_BecomesTheCellsOwnParameter_DefaultedToTheChoiceThatWorks()
    {
        // The headline: nothing to configure before the first Run.
        var p = Assert.Single(Installed(InstallPart(ManifestWithVariant)).Parameters);

        Assert.Equal("ModelAs", p.Name);
        Assert.Equal("Compact", p.DefaultExpression);
        Assert.Equal(["Compact", "Behavioural"], p.Choices);
        Assert.Equal(["Behavioural"], p.UnsupportedChoices);
    }

    [Fact]
    public void AVariantIsListedAheadOfTheKitsOrdinaryParameters()
    {
        var ccell = Installed(InstallPart(ManifestWithVariant, new PdkPartParameter("Rth", "-1")));

        // ModelLibrary is circuitRF's own and comes first; then the choice, then the values.
        Assert.Equal(["ModelLibrary", "ModelAs", "Rth"], ccell.Parameters.Select(x => x.Name));
    }

    [Fact]
    public void AKitDeclaringNoVariants_InstallsExactlyAsBefore()
    {
        var ccell = Installed(InstallPart(
            """{ "workers": [ { "platform": "any", "command": "worker" } ] }""",
            new PdkPartParameter("Rth", "-1")));

        var p = Assert.Single(ccell.Parameters, x => x.Name == "Rth");
        Assert.Null(p.Choices);
        Assert.Null(p.UnsupportedChoices);
        Assert.DoesNotContain(ccell.Parameters, x => x.Choices is { Count: > 0 });
    }

    [Theory]
    // No parameter name, fewer than two choices, and a default that is not one of the choices are
    // each a declaration that describes no usable choice. Dropped whole rather than half-applied —
    // a part offering a broken picker is worse than a part offering none.
    [InlineData("""{ "choices": ["A","B"], "default": "A" }""")]
    [InlineData("""{ "parameter": "M", "choices": ["A"], "default": "A" }""")]
    [InlineData("""{ "parameter": "M", "choices": ["A","B"], "default": "C" }""")]
    public void AContradictoryVariantDeclaration_IsIgnoredEntirely(string variantJson)
    {
        string json = $$"""
            { "workers": [ { "platform": "any", "command": "worker" } ], "variants": [ {{variantJson}} ] }
            """;

        Assert.Empty(Installed(InstallPart(json)).Parameters);
    }

    [Fact]
    public void TheDeclarationSurvivesIntoTheSettledSettings()
    {
        // The settings are what the workspace records and replays, so a declaration that did not
        // reach them would be re-derived — or lost — on every open.
        var settings = LastSettings(ManifestWithVariant);
        var manifest = PdkPartInstaller.ManifestFrom(settings, KitDir, "SampleKit");

        Assert.NotNull(manifest);
        var v = Assert.Single(manifest!.Variants);
        Assert.Equal("ModelAs", v.Parameter);
        Assert.Equal("Compact", v.Default);
        Assert.Equal(["Behavioural"], v.Unsupported);
    }

    // ── What Run does with the choice ─────────────────────────────────────────

    [Fact]
    public void TheDefaultChoice_Simulates_AndTheChoiceItselfIsNotSentToTheProvider()
    {
        // A model-selection parameter picks WHICH implementation is built; it is not a value the
        // implementation takes, and a provider handed one would rightly reject it as unknown.
        var (model, _) = Placed(InstallPart(ManifestWithVariant));

        var result = NetExtractor.Extract(model, "tb");
        var inst   = Assert.Single(result.TestBench.Instances);

        Assert.Empty(result.Conflicts);
        Assert.Equal("ExtDevice", inst.Reference);
        Assert.DoesNotContain(inst.Overrides, o => o.Name == "ModelAs");
    }

    [Fact]
    public void AnUnimplementedChoice_RefusesTheInstance_AndSaysWhichChoiceAndWhichPart()
    {
        var (model, comp) = Placed(InstallPart(ManifestWithVariant));
        comp.Parameters.Single(p => p.Name == "ModelAs").Expression = "Behavioural";

        var result = NetExtractor.Extract(model, "tb");

        Assert.Empty(result.TestBench.Instances);
        string message = Assert.Single(result.Conflicts);
        Assert.Contains("X1", message);
        Assert.Contains("ModelAs", message);
        Assert.Contains("Behavioural", message);
        Assert.Contains("not implemented", message);
    }

    // ── What the Parameter Editor shows ───────────────────────────────────────

    [Fact]
    public void TheEditorOffersEveryChoice_IncludingTheOneItCannotBuild_AndNoTextBox()
    {
        // Leaving the unsupported choice out of the list would read as the kit missing something.
        // Offering it and refusing at Run is information; hiding it is not.
        var (model, comp) = Placed(InstallPart(ManifestWithVariant));

        var row = Row(model, comp, "ModelAs");

        Assert.True(row.IsChoiceParam);
        Assert.Equal(["Compact", "Behavioural"], row.ChoiceOptions);
        Assert.Equal("Compact", row.SelectedChoice);
        Assert.False(row.ShowExpressionTextBox);
        Assert.False(row.ShowUnitCombo);
        Assert.False(row.HasChoiceUnsupportedWarning);
    }

    [Fact]
    public void SelectingTheUnimplementedChoice_WarnsInPlace_NotOnlyAtRun()
    {
        var (model, comp) = Placed(InstallPart(ManifestWithVariant));
        comp.Parameters.Single(p => p.Name == "ModelAs").Expression = "Behavioural";

        var row = Row(model, comp, "ModelAs");

        Assert.True(row.HasChoiceUnsupportedWarning);
        Assert.Contains("not implemented", row.ChoiceUnsupportedWarning);
    }

    [Fact]
    public void AValueTheCellNoLongerOffers_IsStillTheVisiblySelectedItem()
    {
        // An Avalonia ComboBox whose SelectedItem is absent from its ItemsSource renders blank,
        // which reads as the value having been lost.
        var (model, comp) = Placed(InstallPart(ManifestWithVariant));
        comp.Parameters.Single(p => p.Name == "ModelAs").Expression = "Retired";

        var row = Row(model, comp, "ModelAs");

        Assert.Contains("Retired", row.ChoiceOptions);
        Assert.Equal("Retired", row.SelectedChoice);
    }

    [Fact]
    public void AnOrdinaryParameterOnTheSamePart_KeepsItsTextBox()
    {
        var (model, comp) = Placed(InstallPart(ManifestWithVariant, new PdkPartParameter("Rth", "-1")));

        var row = Row(model, comp, "Rth");

        Assert.False(row.IsChoiceParam);
        Assert.True(row.ShowExpressionTextBox);
    }

    // ── Parts whose definition is a circuit, not a device ─────────────────────

    /// <summary>Two formulations of one two-terminal part, distinguishable by resistance.</summary>
    private const string PackageNetlist = """
        ; kit-supplied definitions
        define Pkg_Compact ( pin1 pin2 )
          parameters Rth=1
          R:R1  pin1 pin2  R=50 Ohm
        end Pkg_Compact

        define Pkg_Slow ( pin1 pin2 )
          R:R1  pin1 pin2  R=75 Ohm
        end Pkg_Slow

        define Pkg_OnePin ( pin1 )
          R:R1  pin1 0  R=1 Ohm
        end Pkg_OnePin
        """;

    /// <summary>A kit whose part is a circuit, with both formulations buildable.</summary>
    private string NetlistManifest(string cellPattern, string netlistFile = "circuit/pkg.cnl") => $$"""
        {
          "workers":  [ { "platform": "any", "command": "worker" } ],
          "variants": [ { "parameter": "ModelAs", "choices": ["Compact","Slow"], "default": "Compact" } ],
          "parts":    [ { "id": "PART_A", "netlist": "{{netlistFile}}", "cell": "{{cellPattern}}" } ]
        }
        """;

    private string InstallNetlistBackedPart(string cellPattern, bool writeNetlist = true)
    {
        if (writeNetlist)
        {
            string netAbs = Path.Combine(KitDir, "circuit", "pkg.cnl");
            Directory.CreateDirectory(Path.GetDirectoryName(netAbs)!);
            File.WriteAllText(netAbs, PackageNetlist);
        }
        return InstallPart(NetlistManifest(cellPattern));
    }

    [Fact]
    public void ACircuitBackedPart_EmitsACellInstance_AndBringsItsDefinitionAlong()
    {
        // The point of the whole path: once the definition is in the library, everything downstream
        // treats the part exactly like a cell the user drew.
        var (model, _) = Placed(InstallNetlistBackedPart("Pkg_{ModelAs}"));

        var result = NetExtractor.Extract(model, "tb");
        var inst   = Assert.Single(result.TestBench.Instances);

        Assert.Empty(result.Conflicts);
        Assert.Equal("Pkg_Compact", inst.Reference);
        Assert.NotNull(result.Library.Find("Pkg_Compact"));
    }

    [Fact]
    public void TheChoiceSelectsWhichDefinitionIsBuilt()
    {
        var (model, comp) = Placed(InstallNetlistBackedPart("Pkg_{ModelAs}"));
        comp.Parameters.Single(p => p.Name == "ModelAs").Expression = "Slow";

        var inst = Assert.Single(NetExtractor.Extract(model, "tb").TestBench.Instances);

        Assert.Equal("Pkg_Slow", inst.Reference);
    }

    [Fact]
    public void ACircuitBackedPart_IsNotEmittedAsADevice_EvenThoughTheKitAlsoNamesAProvider()
    {
        // A package is a subcircuit, whatever else the kit says about it.
        var (model, _) = Placed(InstallNetlistBackedPart("Pkg_{ModelAs}"));

        Assert.NotEqual("ExtDevice", Assert.Single(NetExtractor.Extract(model, "tb").TestBench.Instances).Reference);
    }

    [Fact]
    public void OnlyParametersTheDefinitionDeclares_ReachIt()
    {
        // A subcircuit handed a parameter it never named is an error in the elaborator, and the
        // choice that selected the definition has already done its job by this point.
        var (model, comp) = Placed(InstallNetlistBackedPart("Pkg_{ModelAs}"));
        comp.Parameters.Add(new EditableParameter { Name = "Rth",     Expression = "7" });
        comp.Parameters.Add(new EditableParameter { Name = "NotMine", Expression = "3" });

        var inst = Assert.Single(NetExtractor.Extract(model, "tb").TestBench.Instances);

        Assert.Equal("7", Assert.Single(inst.Overrides, o => o.Name == "Rth").Expression);
        Assert.DoesNotContain(inst.Overrides, o => o.Name is "NotMine" or "ModelAs");
    }

    [Fact]
    public void ADefinitionTheKitsNetlistDoesNotHold_IsRefusedByName()
    {
        var (model, _) = Placed(InstallNetlistBackedPart("Pkg_Missing"));

        var result = NetExtractor.Extract(model, "tb");

        Assert.Empty(result.TestBench.Instances);
        Assert.Contains("Pkg_Missing", Assert.Single(result.Conflicts));
    }

    // ── Kit-native device types inside a kit's own netlist ────────────────────

    /// <summary>A package whose cell mixes all three kinds of reference a kit netlist can hold.</summary>
    private const string PackageWithNativeDevice = """
        define Pkg_Sub ( a b )
          R:Rp  a b  R=1 kOhm
        end Pkg_Sub

        define Pkg_Compact ( pin1 pin2 )
          R:R1            pin1 mid   R=50 Ohm
          Pkg_Sub:S1      mid  pin2
          KIT_FET_v1:FET1 pin1 mid pin2  Width=100  File="model.mdl"
        end Pkg_Compact
        """;

    private string InstallPartWithNativeDevice()
    {
        string netAbs = Path.Combine(KitDir, "circuit", "pkg.cnl");
        Directory.CreateDirectory(Path.GetDirectoryName(netAbs)!);
        File.WriteAllText(netAbs, PackageWithNativeDevice);
        return InstallPart(NetlistManifest("Pkg_{ModelAs}"));
    }

    /// <summary>
    /// A kit netlist names its own compiled models natively. They are neither circuitRF primitives
    /// nor cells the kit defines — which is exactly what identifies them — and the kit has already
    /// said which provider evaluates them.
    /// </summary>
    [Fact]
    public void AKitNativeDeviceType_BecomesAnExtDeviceCarryingTheKitsProvider()
    {
        var (model, _) = Placed(InstallPartWithNativeDevice());

        var result = NetExtractor.Extract(model, "tb");
        var cell   = result.Library.Find("Pkg_Compact");

        Assert.NotNull(cell);
        var dev = Assert.Single(cell!.Instances, i => i.InstanceName == "FET1");

        Assert.Equal("ExtDevice", dev.Reference);
        Assert.Equal("SampleKit",  dev.Overrides.Single(o => o.Name == "Provider").Expression);
        Assert.Equal("KIT_FET_v1", dev.Overrides.Single(o => o.Name == "Type").Expression);
    }

    [Fact]
    public void ItsOwnParametersAndNets_AreCarriedThroughUntouched()
    {
        // Everything but Provider/Type is the kit's own, for the provider to match against the names
        // its descriptor declares.
        var (model, _) = Placed(InstallPartWithNativeDevice());

        var dev = Assert.Single(
            NetExtractor.Extract(model, "tb").Library.Find("Pkg_Compact")!.Instances,
            i => i.InstanceName == "FET1");

        Assert.Equal(["pin1", "mid", "pin2"], dev.NetBindings);
        Assert.Equal("100",         dev.Overrides.Single(o => o.Name == "Width").Expression);
        Assert.Equal("\"model.mdl\"", dev.Overrides.Single(o => o.Name == "File").Expression);
    }

    /// <summary>
    /// The classification is the whole trick, so both things it must NOT touch are pinned: a
    /// circuitRF primitive, and a cell the same kit defines. Rewriting either would replace real
    /// circuitry with a device nobody can evaluate.
    /// </summary>
    [Fact]
    public void APrimitiveAndASiblingKitCell_AreLeftAlone()
    {
        var (model, _) = Placed(InstallPartWithNativeDevice());
        var cell = NetExtractor.Extract(model, "tb").Library.Find("Pkg_Compact")!;

        Assert.Equal("R",       Assert.Single(cell.Instances, i => i.InstanceName == "R1").Reference);
        Assert.Equal("Pkg_Sub", Assert.Single(cell.Instances, i => i.InstanceName == "S1").Reference);
    }

    [Fact]
    public void PlacingTheSamePartTwice_RewritesEachDeviceExactlyOnce()
    {
        // The netlist read is cached, so both instances share one Cell object. A rewrite that ran
        // twice would nest ExtDevice inside itself or duplicate Provider/Type.
        string cellDir = InstallPartWithNativeDevice();
        var (model, _) = Placed(cellDir);
        NetExtractor.Extract(model, "tb");

        var dev = Assert.Single(
            NetExtractor.Extract(model, "tb").Library.Find("Pkg_Compact")!.Instances,
            i => i.InstanceName == "FET1");

        Assert.Equal("ExtDevice", dev.Reference);
        Assert.Single(dev.Overrides, o => o.Name == "Provider");
        Assert.Single(dev.Overrides, o => o.Name == "Type");
    }

    [Fact]
    public void ADefinitionWithADifferentTerminalCount_IsRefusedRatherThanGuessedAt()
    {
        // Guessing an alignment would wire the design wrong in silence.
        var (model, _) = Placed(InstallNetlistBackedPart("Pkg_OnePin"));

        var result = NetExtractor.Extract(model, "tb");

        Assert.Empty(result.TestBench.Instances);
        Assert.Contains("pin", Assert.Single(result.Conflicts));
    }

    [Fact]
    public void AKitNamingANetlistThatIsNotThere_StillInstallsThePart()
    {
        // Reported at Run against the instance that needs it, not against a finished import.
        string cellDir = InstallNetlistBackedPart("Pkg_{ModelAs}", writeNetlist: false);

        var ccell = Installed(cellDir);

        Assert.Null(ccell.ExternalNetlistPath);
        Assert.Equal("SampleKit", ccell.ExternalProvider);
    }

    [Fact]
    public void TheKitsOwnSupportingDeclarations_ComeWithIt()
    {
        // A copied cell references them by bare name, so they have to reach the testbench.
        string netAbs = Path.Combine(KitDir, "circuit", "pkg.cnl");
        Directory.CreateDirectory(Path.GetDirectoryName(netAbs)!);
        File.WriteAllText(netAbs, "KitScale = 3\n\n" + PackageNetlist);

        var (model, _) = Placed(InstallPart(NetlistManifest("Pkg_{ModelAs}")));

        var tb = NetExtractor.Extract(model, "tb").TestBench;

        Assert.Contains(tb.GlobalVariables, v => v.Name == "KitScale");
    }

    // ── Supplementing a read-only kit ─────────────────────────────────────────
    //
    // A kit is very often read-only, and duplicating one to add a file to it is not a workflow. The
    // surviving route is the ADDITIONS FOLDER (below): a small folder of one's own that names the
    // kit. The former second route — dropping a file into the folder the workspace made for the kit
    // — is gone with that folder; a kit's parts now live in memory and the workspace holds only a
    // reference to the kit.

    /// <summary>Installs a part from a kit that declares nothing at all — the read-only vendor case.</summary>
    private string InstallBareKit()
        => InstallPart("""{ "workers": [ { "platform": "any", "command": "worker" } ] }""");

    [Fact]
    public void ADeclarationTheKitGainsLater_IsPickedUpOnTheNextLoad()
    {
        // A workspace open re-reads the kit, so a declaration that arrives after the first import is
        // picked up without the user pointing at anything.
        string cellRef = InstallBareKit();
        Assert.Empty(Installed(cellRef).Parameters);

        Assert.Equal(cellRef, InstallPart(ManifestWithVariant));

        var p = Assert.Single(Installed(cellRef).Parameters, x => x.Name == "ModelAs");
        Assert.Equal("Compact", p.DefaultExpression);
    }

    [Fact]
    public void LoadingTwice_YieldsTheSamePart()
    {
        // Re-reading is what an open does, so it has to be repeatable — a kit that translated
        // differently the second time would be a workspace that opens differently each morning.
        string cellRef = InstallPart(ManifestWithVariant);
        string after   = CellPersistence.Serialize(Installed(cellRef));

        InstallPart(ManifestWithVariant);

        Assert.Equal(after, CellPersistence.Serialize(Installed(cellRef)));
    }

    [Fact]
    public void AValueTheUserAlreadyChose_SurvivesTheKitBeingLoadedAgain()
    {
        // The instance carries the user's own choice; re-reading the kit rebuilds the part's
        // interface and must not reach into a design and undo an edit.
        string cellRef = InstallPart(ManifestWithVariant);
        var (_, comp) = Placed(cellRef);

        comp.Parameters.Single(x => x.Name == "ModelAs").Expression = "Behavioural";
        InstallPart(ManifestWithVariant);

        Assert.Equal("Behavioural", comp.Parameters.Single(x => x.Name == "ModelAs").Expression);
    }

    [Fact]
    public void APartPlacedBeforeTheDeclarationArrived_StillGetsTheParameter()
    {
        // An instance is seeded from the cell's interface at placement, so a cell that gains a
        // parameter afterwards would otherwise leave every earlier instance without it — which for
        // a kit whose declarations arrive later is the ordinary case, not an edge one.
        string cellRef = InstallBareKit();
        var (model, comp) = Placed(cellRef);
        Assert.Empty(comp.Parameters);

        InstallPart(ManifestWithVariant);

        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(new SchematicViewModel(model), comp, showClose: false);

        Assert.Contains(editor.Rows, r => r.Name == "ModelAs");
        Assert.Equal("Compact", comp.Parameters.Single(p => p.Name == "ModelAs").Expression);
    }

    // ── Importing an "additions" folder ───────────────────────────────────────
    //
    // A supplier's kit is routinely read-only and often far too large to copy, so what circuitRF
    // needs lives in its own small folder that NAMES the kit. The user imports that one folder and
    // the importer reads both. Nothing is copied: the kit becomes an explicit, repairable
    // dependency of the workspace rather than a set of files silently duplicated into it.

    private string AdditionsDir => Path.Combine(_root, "additions");

    /// <summary>Builds a read-only-style kit holding the symbol, plus a separate additions folder.</summary>
    private string ImportAdditionsFolder(string netlistRelPath = "circuitrf/pkg.cnl", bool writeNetlist = true)
    {
        string symRel = "part/symbol/part.dsn";
        string symAbs = Path.Combine(KitDir, symRel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(symAbs)!);
        File.WriteAllText(symAbs, SymbolFile);

        Directory.CreateDirectory(AdditionsDir);
        if (writeNetlist)
        {
            string netAbs = Path.Combine(AdditionsDir, netlistRelPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(netAbs)!);
            File.WriteAllText(netAbs, PackageNetlist);
        }

        File.WriteAllText(Path.Combine(AdditionsDir, DeviceWorkerManifest.FileName), $$"""
            {
              "provider":      "SampleKit",
              "baseDirectory": "../kit",
              "workers":  [ { "platform": "any", "command": "worker" } ],
              "variants": [ { "parameter": "ModelAs", "choices": ["Compact","Slow"], "default": "Compact" } ],
              "parts":    [ { "id": "part", "netlist": "{{netlistRelPath}}", "cell": "Pkg_{ModelAs}" } ]
            }
            """);

        var outcome = PdkPartInstaller.Install(PdkImporter.Import(AdditionsDir));
        PdkKitRegistry.SetKit(null, "SampleKit", outcome.Parts ?? []);
        return outcome.Items[0].Pdk!.CellDir!;
    }

    [Fact]
    public void ImportingTheAdditionsFolder_ReadsTheKitItNames()
    {
        // The symbol lives only in the kit; the manifest only in the additions folder. One import.
        var report = PdkImporter.Import(AdditionsDirWithManifest());

        Assert.Equal(KitDir, report.KitRoot);
        Assert.Single(report.Parts);
    }

    private string AdditionsDirWithManifest()
    {
        ImportAdditionsFolder();
        return AdditionsDir;
    }

    [Fact]
    public void ThePartPointsAtTheCircuitDefinitionWhereItLives_NothingIsCopied()
    {
        // The definition is the kit author's file and stays theirs. Pointing at it rather than at a
        // duplicate is what makes the kit one dependency to repair instead of a copy that silently
        // stops matching the original.
        string cellRef = ImportAdditionsFolder();

        string expected = Path.Combine(AdditionsDir, "circuitrf", "pkg.cnl");
        Assert.Equal(expected, Installed(cellRef).ExternalNetlistPath);
        Assert.False(Directory.Exists(Path.Combine(WorkspaceDir, "pdk")),
            "the import wrote into the workspace — the whole point is that it writes nothing");
    }

    [Fact]
    public void ThePartBuilds_FromTheDefinitionWhereItLives()
    {
        var (model, _) = Placed(ImportAdditionsFolder());
        var result = NetExtractor.Extract(model, "tb");

        Assert.Empty(result.Conflicts);
        Assert.Equal("Pkg_Compact", Assert.Single(result.TestBench.Instances).Reference);
    }

    [Fact]
    public void TheInstalledKitIsNamedForTheKit_NotForTheFolderThatWasImported()
    {
        // An additions folder may be called anything. Each installed cell records Provider = the kit
        // name and a netlist asks for that name, so getting it from the folder would leave every step
        // working except the one that resolves the provider.
        var ccell = Installed(ImportAdditionsFolder());

        Assert.Equal("SampleKit", ccell.ExternalProvider);
    }

    [Fact]
    public void AKitNamingACircuitDefinitionThatIsNotThere_SaysSo_AndStillInstallsThePart()
    {
        string symRel = "part/symbol/part.dsn";
        string symAbs = Path.Combine(KitDir, symRel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(symAbs)!);
        File.WriteAllText(symAbs, SymbolFile);

        Directory.CreateDirectory(AdditionsDir);
        File.WriteAllText(Path.Combine(AdditionsDir, DeviceWorkerManifest.FileName), $$"""
            {
              "provider":      "SampleKit",
              "baseDirectory": "../kit",
              "workers": [ { "platform": "any", "command": "worker" } ],
              "parts":   [ { "id": "part", "netlist": "gone.cnl", "cell": "Pkg" } ]
            }
            """);

        var outcome = PdkPartInstaller.Install(PdkImporter.Import(AdditionsDir));

        Assert.Single(outcome.Items);
        Assert.Contains(outcome.Diagnostics, d => d.Contains("gone.cnl"));
    }

    [Fact]
    public void ARelativeBaseDirectory_ResolvesAgainstTheFolderItIsWrittenIn()
    {
        // Written relative so the whole tree can be moved or checked out elsewhere. Every additions
        // test here declares it that way, so this pins the property they all rest on.
        var report = PdkImporter.Import(AdditionsDirWithManifest());

        Assert.Equal(Path.GetFullPath(KitDir), Path.GetFullPath(report.KitRoot!));
    }

    [Fact]
    public void TheImportSaysWhatSimulationSettingsItRead()
    {
        // Importing the wrong folder otherwise shows up three steps later as a parameter that is not
        // there, which is a bad way to learn it.
        ImportAdditionsFolder();
        var outcome = PdkPartInstaller.Install(PdkImporter.Import(AdditionsDir));

        Assert.Contains(outcome.Notes ?? [],
            d => d.Contains("model-selection parameter") && d.Contains("defined by a circuit"));
    }

    [Fact]
    public void AKitNamingNoWorker_SaysSo_WithoutClaimingItsPartsAreUnusable()
    {
        string symRel = Path.Combine("symbols", "part.dsn");
        string symAbs = Path.Combine(KitDir, symRel);
        Directory.CreateDirectory(Path.GetDirectoryName(symAbs)!);
        File.WriteAllText(symAbs, SymbolFile);

        var report = new PdkImportReport { RootPath = KitDir, KitName = "SampleKit" };
        report.Parts.Add(new PdkPart("PART_A", "Part A",
            SymbolArtwork: new PdkAsset(symRel.Replace(Path.DirectorySeparatorChar, '/'),
                                        PdkAssetKind.SymbolArtwork, PdkAssetSupport.Supported,
                                        "symbol description (.dsn)")));

        var outcome = PdkPartInstaller.Install(report);

        Assert.Contains(outcome.Notes ?? [], d => d.Contains("names no program to evaluate its devices"));
    }

    // ── Dialog ordering: which file, then which formulation, then the values ──

    private const string ManifestWithFileParam = """
        {
          "workers":        [ { "platform": "any", "command": "worker" } ],
          "variants":       [ { "parameter": "ModelAs", "choices": ["Compact","Slow"], "default": "Compact" } ],
          "fileParameters": [ "ModelFile" ]
        }
        """;

    [Fact]
    public void TheDialogAsksWhichFileFirst_ThenWhichFormulation_ThenTheValues_AndCircuitRfsOwnOverrideLast()
    {
        // That is the order the questions actually arrive in — the later answers only mean anything
        // once the earlier ones are settled — and it puts the two a user of an imported kit reaches
        // for at the top instead of buried among a dozen numbers.
        //
        // ModelLibrary is the exception and goes LAST (owner-reported). It is file-valued, so the rule
        // above put it first on EVERY kit part — the top of the dialog, on all of them — while being
        // an override almost nobody sets, and one that does nothing at all on a part the kit defines
        // with its own netlist. A row that leads every part and applies to few reads as a required
        // first step, which is how it came to be filled in with a path that then failed to parse. The
        // rule it is an exception to is about a file the KIT asked for; this one is circuitRF's own.
        string cellDir = InstallPart(ManifestWithFileParam,
                                     new PdkPartParameter("Rth", "-1"),
                                     new PdkPartParameter("ModelFile", ""));
        var (model, comp) = Placed(cellDir);

        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(new SchematicViewModel(model), comp, showClose: false);

        Assert.Equal(["ModelFile", "ModelAs", "Rth", "ModelLibrary"], editor.Rows.Select(r => r.Name));

        // Still a file row with a picker — moved, not demoted to a text box, because it is still a
        // path and still the thing that makes two revisions of a library comparable side by side.
        Assert.True(editor.Rows.Single(r => r.Name == "ModelLibrary").IsFilePathParam);
    }

    [Fact]
    public async Task AFileValuedParameter_OffersAPicker_AndCommitsWhatWasPicked()
    {
        // A path is exactly the kind of value nobody should be asked to type.
        string cellDir = InstallPart(ManifestWithFileParam, new PdkPartParameter("ModelFile", ""));
        var (model, comp) = Placed(cellDir);

        var row = Row(model, comp, "ModelFile");
        Assert.True(row.IsFilePathParam);
        Assert.False(row.ShowBrowseButton);            // no host, no picker

        row.PickFileAsync = () => Task.FromResult<string?>("/models/lib.so");
        Assert.True(row.ShowBrowseButton);
        await row.BrowseForFileAsync();

        Assert.Equal("/models/lib.so", comp.Parameters.Single(p => p.Name == "ModelFile").Expression);
    }

    [Fact]
    public async Task ThePickerSuppliedAfterRowsAreBuilt_StillReachesTheRow()
    {
        // The ordering the real view actually uses, and the one that shipped broken: rows are built
        // by SetTargetDirect, and only THEN does the view's DataContextChanged supply the picker.
        // Assigning the row's own PickFileAsync by hand (as every other test here does) exercises an
        // ordering production never takes, which is exactly why the missing wiring survived.
        string cellDir = InstallPart(ManifestWithFileParam, new PdkPartParameter("ModelFile", ""));
        var (model, comp) = Placed(cellDir);

        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(new SchematicViewModel(model), comp, showClose: false);

        var row = editor.Rows.Single(r => r.Name == "ModelFile");
        Assert.False(row.ShowBrowseButton);          // nothing has supplied a picker yet

        editor.PickModelFileAsync = () => Task.FromResult<string?>("/models/lib.so");

        Assert.True(row.ShowBrowseButton);
        await row.BrowseForFileAsync();
        Assert.Equal("/models/lib.so", comp.Parameters.Single(p => p.Name == "ModelFile").Expression);
    }

    [Fact]
    public void TheParameterEditorView_ActuallyAssignsTheModelFilePicker()
    {
        // The VM declares PickModelFileAsync and BuildRows reads it, but for one release nothing
        // ever assigned it — every Browse… button was invisible. A UserControl cannot be constructed
        // headlessly, so the wiring is pinned by source scan, the same way this repo pins other
        // Avalonia-only call sites.
        var code = ReadRepoFile("src/Ui/Views/ParameterEditor/ParameterEditorView.axaml.cs");

        Assert.Contains("vm.PickModelFileAsync", code);
        Assert.Contains("private async Task<string?> PickModelFileAsync()", code);
    }

    private static string ReadRepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root (no CLAUDE.md found walking up from this test file).");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }

    [Fact]
    public void AFileValuedRow_ShowsBrowse_AndNotTheUnitCombo_TheyShareAColumn()
    {
        // Reported from the running app: the unit combo was drawn ON TOP of the Browse… button, so
        // the file could not be picked at all. Both live in Grid.Column 2 of the row template, which
        // makes them mutually exclusive by construction — and two controls sharing a cell look
        // perfectly fine in the markup, so the invariant is asserted here instead.
        string cellDir = InstallPart(ManifestWithFileParam, new PdkPartParameter("ModelFile", ""));
        var (model, comp) = Placed(cellDir);

        var row = Row(model, comp, "ModelFile");
        row.PickFileAsync = () => Task.FromResult<string?>("/models/lib.so");

        Assert.True(row.ShowBrowseButton);
        Assert.False(row.ShowUnitCombo);
    }

    [Fact]
    public void NoTwoOccupantsOfTheSharedColumn_AreEverVisibleTogether()
    {
        // Column 2 holds the Browse… button, the unit combo and the enum readout. Whatever the row
        // is, at most one of them may be showing. Anything added to that column must join this test.
        string cellDir = InstallPart(ManifestWithFileParam,
                                     new PdkPartParameter("ModelFile", ""),
                                     new PdkPartParameter("Rth", "-1"));
        var (model, comp) = Placed(cellDir);

        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(new SchematicViewModel(model), comp, showClose: false);
        editor.PickModelFileAsync = () => Task.FromResult<string?>("/models/lib.so");

        Assert.NotEmpty(editor.Rows);
        foreach (var row in editor.Rows)
        {
            int showing = (row.ShowBrowseButton ? 1 : 0)
                        + (row.ShowUnitCombo    ? 1 : 0)
                        + (row.IsEnumParam      ? 1 : 0);
            Assert.True(showing <= 1,
                $"row '{row.Name}' shows {showing} controls in the shared column; they would overlap");
        }
    }

    [Fact]
    public async Task ACancelledPick_ChangesNothing()
    {
        string cellDir = InstallPart(ManifestWithFileParam, new PdkPartParameter("ModelFile", "/was/here.so"));
        var (model, comp) = Placed(cellDir);

        var row = Row(model, comp, "ModelFile");
        row.PickFileAsync = () => Task.FromResult<string?>(null);
        await row.BrowseForFileAsync();

        Assert.Equal("/was/here.so", comp.Parameters.Single(p => p.Name == "ModelFile").Expression);
    }

    [Fact]
    public void AnOrdinaryParameter_IsNeitherAFileNorAChoice()
    {
        string cellDir = InstallPart(ManifestWithFileParam, new PdkPartParameter("Rth", "-1"));
        var (model, comp) = Placed(cellDir);

        var row = Row(model, comp, "Rth");

        Assert.False(row.IsFilePathParam);
        Assert.False(row.IsChoiceParam);
        Assert.True(row.ShowExpressionTextBox);
    }

    [Fact]
    public void AVariantScopedToOnePart_DoesNotAppearOnTheOthers()
    {
        // A kit's parts are not alike: the same folder holds real components and the helper cells
        // they are assembled from. Caught in a real workspace, where a formulation choice belonging
        // to a packaged part had been given to a pin-less include cell as well.
        File.WriteAllText(Path.Combine(KitDir, DeviceWorkerManifest.FileName), """
            {
              "workers":  [ { "platform": "any", "command": "worker" } ],
              "variants": [ { "parameter": "ModelAs", "choices": ["Compact","Slow"],
                              "default": "Compact", "parts": ["REAL_PART"] } ]
            }
            """);

        string symRel = Path.Combine("symbols", "part.dsn");
        string symAbs = Path.Combine(KitDir, symRel);
        Directory.CreateDirectory(Path.GetDirectoryName(symAbs)!);
        File.WriteAllText(symAbs, SymbolFile);

        var art = new PdkAsset(symRel.Replace(Path.DirectorySeparatorChar, '/'),
                               PdkAssetKind.SymbolArtwork, PdkAssetSupport.Supported, "symbol description (.dsn)");
        var report = new PdkImportReport { RootPath = KitDir, KitName = "SampleKit" };
        report.Parts.Add(new PdkPart("REAL_PART",   "Real",   SymbolArtwork: art));
        report.Parts.Add(new PdkPart("HELPER_CELL", "Helper", SymbolArtwork: art));

        var outcome = PdkPartInstaller.Install(report);
        PdkKitRegistry.SetKit(null, outcome.KitName, outcome.Parts ?? []);
        var items = outcome.Items;

        Assert.Contains(Installed(items[0].Pdk!.CellDir!).Parameters, p => p.Name == "ModelAs");
        Assert.DoesNotContain(Installed(items[1].Pdk!.CellDir!).Parameters, p => p.Name == "ModelAs");
    }

    [Fact]
    public void AVariantNamingNoParts_StillAppliesThroughout()
    {
        // Empty means the kit is saying it genuinely applies to everything it ships.
        var ccell = Installed(InstallPart(ManifestWithVariant));

        Assert.Contains(ccell.Parameters, p => p.Name == "ModelAs");
    }

    private static ParameterRowViewModel Row(SchematicEditModel model, EditableComponent comp, string name)
        => new(comp.Parameters.Single(p => p.Name == name),
               new SchematicViewModel(model), comp.Symbol, comp);

    // ── A kit that says nothing about how to simulate its devices ─────────────

    /// <summary>
    /// The ordinary case for an unmodified vendor kit: it is written for its own simulator and says
    /// nothing about circuitRF. The importer works out which compiled library serves the device
    /// types the kit's netlists name, and records it — the difference between "import the kit" and
    /// "import the kit, then go and configure it".
    /// </summary>
    private JsonNode? InstallBareKitWithCompiledDevice(bool withLibrary = true)
    {
        string netAbs = Path.Combine(KitDir, "circuit", "pkg.cnl");
        Directory.CreateDirectory(Path.GetDirectoryName(netAbs)!);
        // Two formulations, as a kit ships: that is what makes the part netlist-backed, so the
        // installed cell records where its circuit lives — the anchor healing needs later.
        File.WriteAllText(netAbs, """
            define PART_A_Compact ( pin1 pin2 )
              R:R1            pin1 mid   R=50 Ohm
              KIT_FET_v1:FET1 pin1 mid pin2
            end PART_A_Compact

            define PART_A_Slow ( pin1 pin2 )
              R:R1            pin1 mid   R=75 Ohm
              KIT_FET_v1:FET1 pin1 mid pin2
            end PART_A_Slow
            """);

        // circuitRF's own worker, as it would sit in the tools folder or beside the kit. An ELF
        // header because only a Linux executable is accepted for the VM target.
        string workerPath = Path.Combine(_root, "SharedModels", "senior_worker");
        Directory.CreateDirectory(Path.GetDirectoryName(workerPath)!);
        File.WriteAllBytes(workerPath, [0x7F, (byte)'E', (byte)'L', (byte)'F', 0, 0, 0, 0]);

        if (withLibrary)
        {
            // A vendor puts the shared library package BESIDE the kit, not inside it.
            //
            // The ELF header is load-bearing, not decoration: discovery runs one search PER TARGET,
            // and the file's own magic is what says WHICH target a build is for. The path hints only
            // rank within a target — without the magic this file would answer the Windows search
            // too, and the kit would be described as shipping a Windows build it does not have.
            string lib = Path.Combine(_root, "SharedModels", "bin", "linux_x86_64", "Models.so");
            Directory.CreateDirectory(Path.GetDirectoryName(lib)!);
            File.WriteAllBytes(lib,
            [
                0x7F, (byte)'E', (byte)'L', (byte)'F',
                .. System.Text.Encoding.ASCII.GetBytes(
                    "\0padding\0" + DeviceLibraryDiscovery.Profiles[0].ExportPrefix + "KIT_FET_v1\0"),
            ]);
        }

        // No device-provider.json anywhere: this is the kit exactly as shipped.
        string symRel = Path.Combine("symbols", "part.dsn");
        string symAbs = Path.Combine(KitDir, symRel);
        Directory.CreateDirectory(Path.GetDirectoryName(symAbs)!);
        File.WriteAllText(symAbs, SymbolFile);

        var report = new PdkImportReport { RootPath = KitDir, KitName = "SampleKit" };
        report.Parts.Add(new PdkPart("PART_A", "Part A",
            SymbolArtwork: new PdkAsset(symRel.Replace(Path.DirectorySeparatorChar, '/'),
                                        PdkAssetKind.SymbolArtwork, PdkAssetSupport.Supported,
                                        "symbol description (.dsn)")));
        report.Assets.Add(new PdkAsset("circuit/pkg.cnl", PdkAssetKind.Netlist,
                                       PdkAssetSupport.Supported, "netlist"));

        var outcome = PdkPartInstaller.Install(report);
        PdkKitRegistry.SetKit(null, "SampleKit", outcome.Parts ?? []);
        return outcome.Settings;
    }

    /// <summary>The settled settings as a manifest, resolved against the kit — what the resolver sees.</summary>
    private DeviceWorkerManifest? ManifestOf(JsonNode? settings)
        => PdkPartInstaller.ManifestFrom(settings, KitDir, "SampleKit");

    [Fact]
    public void AKitShippingNoSettings_GetsThemDerived_NamingTheLibraryThatServesItsDevices()
    {
        var settings = InstallBareKitWithCompiledDevice();
        Assert.NotNull(settings);

        var manifest = ManifestOf(settings);
        Assert.NotNull(manifest);
        Assert.Equal("SampleKit", manifest!.ProviderName);

        // Every entry names circuitRF's own worker and the discovered library.
        Assert.NotEmpty(manifest.Launches);
        Assert.All(manifest.Launches, l => Assert.Contains("Models.so",
            string.Join(" ", l.Arguments), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheDerivedSettings_AreWhatTheResolverThenUses()
    {
        // Settling on something nothing reads back would be worse than settling on nothing.
        var manifest = ManifestOf(InstallBareKitWithCompiledDevice());

        var launch = manifest!.Launches.FirstOrDefault(
            l => l.Platform.Contains("linux", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(launch);

        var (_, arguments) = manifest.Resolve(launch!);
        Assert.True(File.Exists(arguments[0]),
            $"the recorded library path does not resolve to a real file: {arguments[0]}");
    }

    [Fact]
    public void AKitWhoseLibraryIsNowhere_SettlesOnNothing_AndSaysWhy()
    {
        // Guessing a path would fail much later, from inside a worker launch, naming nothing useful.
        Assert.Null(InstallBareKitWithCompiledDevice(withLibrary: false));
    }

    /// <summary>
    /// Recorded settings are replayed rather than re-derived. That is what keeps a workspace open
    /// fast and repeatable — library discovery byte-scans candidate builds and is the one part of an
    /// open with a cost worth caring about.
    /// </summary>
    [Fact]
    public void RecordedSettings_AreReplayed_NotRederived()
    {
        var recorded = JsonNode.Parse("""
            { "provider": "SampleKit",
              "workers": [ { "platform": "any", "command": "recorded-worker", "arguments": [] } ] }
            """);

        var report = new PdkImportReport { RootPath = KitDir, KitName = "SampleKit" };
        report.Parts.Add(new PdkPart("PART_A", "Part A"));

        var settings = PdkPartInstaller.Install(report, recorded).Settings;

        Assert.Equal("recorded-worker",
                     settings!["workers"]!.AsArray()[0]!["command"]!.GetValue<string>());
    }
}
