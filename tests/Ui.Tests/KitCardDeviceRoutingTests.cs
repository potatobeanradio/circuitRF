using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Pdk;
using CircuitRF.Engine;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// brief-gdf-1b §3/§4: a kit device on a plain model card runs on circuitRF's own physics when no
/// compiled model claims it. The kits are synthetic and written by these tests.
/// </summary>
[Collection(PdkToolsDirectoryCollection.Name)]
public sealed class KitCardDeviceRoutingTests : IDisposable
{
    private const string Kit = "CardKit";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "crf-cardkit-" + Guid.NewGuid().ToString("N")[..8]);

    public KitCardDeviceRoutingTests() => Directory.CreateDirectory(Path.Combine(_root, "models"));

    public void Dispose()
    {
        PdkKitRegistry.ResetAllForTests();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Two diodes on one card inside one kit cell: the first states a perimeter, which has no home.</summary>
    private const string Library = """
        .subckt dpart a b area0=2
        D1 a b dsyn area=area0 pj=4u
        D2 a b dsyn area=area0
        .ends dpart
        .model dsyn d is=1e-15 n=1.05 rs=2 ffe=1.1
        """;

    private void Write(string relative, string text)
    {
        string full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private void InstallSubcircuitKit(IReadOnlyList<OsdiModel>? osdi = null, string library = Library)
    {
        Write("models/dev.lib", library);
        var report = new PdkImportReport { RootPath = _root, KitName = Kit };
        report.Parts.Add(new PdkPart(
            "DPART", "D part",
            Parameters: [new PdkPartParameter("area0", "2")],
            Pins: [new KitSymbolPin("a", 0, 0), new KitSymbolPin("b", 500, 0)],
            DefinitionRelativePath: "models/dev.lib",
            DefinitionCell: "dpart"));
        var outcome = PdkPartInstaller.Install(report);
        PdkKitRegistry.SetKit(null, outcome.KitName, outcome.Parts ?? [], osdi);
    }

    private static EditableComponent Place(string part)
    {
        var installed = PdkKitRegistry.Find(PdkKitRegistry.RefFor(Kit, part), null)
                        ?? throw new Xunit.Sdk.XunitException($"'{part}' is not loaded");
        var comp = new EditableComponent
        {
            InstanceName = "X1", Symbol = SymbolKind.Generic, CellRef = PdkKitRegistry.RefFor(Kit, part),
        };
        foreach (var p in installed.Ccell.Parameters)
            comp.Parameters.Add(new EditableParameter
            {
                Name = p.Name, Expression = p.DefaultExpression ?? "", Unit = p.Unit ?? "",
            });
        return comp;
    }

    private static NetExtractor.ExtractionResult Extract(string part, IReadOnlyList<PdkCornerSection>? sections = null)
    {
        var model = new SchematicEditModel { SchematicDirectory = Path.GetTempPath() };
        model.Components.Add(Place(part));
        return NetExtractor.Extract(model, "tb", cornerSections: sections);
    }

    private static string Value(Instance i, string name) => i.Overrides.Single(o => o.Name == name).Expression;

    // ── 2 — a supported card with no compiled implementor runs on the built-in ──

    [Fact]
    public void ACardDevice_WithNoCompiledModel_BecomesTheBuiltIn_CardUnderInstance()
    {
        InstallSubcircuitKit();

        var ext = Extract("DPART");
        var d1  = ext.Library.Find("dpart")!.Instances.Single(i => i.InstanceName == "D1");

        Assert.Equal("Diode", d1.Reference);
        Assert.Equal("1E-15", Value(d1, "Is"));     // the card's
        Assert.Equal("area0", Value(d1, "Area"));   // the instance's geometry, in circuitRF's spelling
        Assert.DoesNotContain(d1.Overrides, o => o.Name.Equals("pj", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(ext.Conflicts, c => c.Contains("Verilog-A", StringComparison.Ordinal));
    }

    // ── 3 — a compiled implementor still wins ──────────────────────────────────

    [Fact]
    public void ACompiledModelImplementingTheCardsType_WinsOverTheBuiltIn()
    {
        InstallSubcircuitKit([new OsdiModel("/compiled/diode.osdi", [new OsdiModule("d", ["is", "n"])])]);

        var d1 = Extract("DPART").Library.Find("dpart")!.Instances.Single(i => i.InstanceName == "D1");

        Assert.Equal("ExtDevice", d1.Reference);
        Assert.Equal("d", Value(d1, "Type"));
    }

    // ── brief-gdf-6 R-gdf6-3 — compile advice only where the kit ships the source ──

    private const string SyntheticTypeLibrary = """
        .subckt dpart a b area0=2
        Q1 a b b qsyn
        .ends dpart
        .model qsyn synthva_bjt
        """;

    /// <summary>The kit's Verilog-A sources, as the import reads them off disk.</summary>
    private IReadOnlyDictionary<string, string> SourcesOnDisk()
        => PdkWorkspaceImport.Read(_root, Kit).VerilogASources;

    private string NoBuiltInSentence(bool shipSource)
    {
        if (shipSource)
            Write("va/synth.va", "// a synthetic model\n`include \"disciplines.vams\"\nmodule synthva_bjt(c, b, e);\nendmodule\n");
        var sources = SourcesOnDisk();
        InstallSubcircuitKit(library: SyntheticTypeLibrary);
        var installed = PdkKitRegistry.PartsOf(null, Kit);
        PdkKitRegistry.SetKit(null, Kit, installed, verilogASources: sources);
        return Assert.Single(Extract("DPART").Conflicts, c => c.Contains("synthva_bjt", StringComparison.Ordinal));
    }

    [Fact]
    public void ATypeTheKitShipsSourceFor_IsToldToCompileThatSource()
    {
        string said = NoBuiltInSentence(shipSource: true);

        Assert.Contains("compile it", said, StringComparison.Ordinal);
        Assert.Contains("va/synth.va", said, StringComparison.Ordinal);
    }

    [Fact]
    public void ATypeWithNoSourceInTheKit_IsNotToldToCompileAnything()
    {
        string said = NoBuiltInSentence(shipSource: false);

        Assert.Contains("no compiled model, no source in the kit and no built-in for 'synthva_bjt'", said, StringComparison.Ordinal);
        Assert.DoesNotContain("Verilog-A compiler", said, StringComparison.Ordinal);   // no advice to compile anything
    }

    // ── 4 — what the built-in does not carry is reported once per card ─────────

    [Fact]
    public void UnmappedParameters_AreReportedOncePerCard_NotPerInstance()
    {
        InstallSubcircuitKit();

        var ext = Extract("DPART");

        // Two instances of one card: each thing the card leaves behind is said once.
        Assert.Single(ext.Conflicts, c => c.Contains("'dsyn'", StringComparison.Ordinal)
                                       && c.Contains("ffe", StringComparison.OrdinalIgnoreCase));
        Assert.Single(ext.Conflicts, c => c.Contains("'dsyn'", StringComparison.Ordinal)
                                       && c.Contains("'pj'", StringComparison.Ordinal));
    }

    /// <summary>A junction perimeter on an instance is dropped only while the card states no sidewall
    /// term: with CJSW stated it is half the junction, and the built-in has no home for it.</summary>
    [Fact]
    public void APerimeterOnACardStatingASidewall_IsRefused_NotDropped()
    {
        InstallSubcircuitKit(library: """
            .subckt dpart a b area0=2
            D1 a b dsw area=area0 pj=4u
            .ends dpart
            .model dsw d is=1e-15 cjsw=1e-15
            """);

        var ext = Extract("DPART");
        var d1  = ext.Library.Find("dpart")!.Instances.Single(i => i.InstanceName == "D1");

        Assert.Equal("dsw", d1.Reference);   // left as written, so the failure names it
        Assert.Contains(ext.Conflicts, c => c.Contains("'pj'", StringComparison.Ordinal)
                                         && c.Contains("CJSW", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A card family with no record of which of its parameters move a DC answer is not run on
    /// the built-in: what it leaves behind cannot be shown harmless, so it is refused, not approximated.</summary>
    [Fact]
    public void ACardFamilyWithNoDcList_IsNotRunOnTheBuiltIn()
    {
        InstallSubcircuitKit(library: """
            .subckt dpart a b
            J1 a b 0 jsyn
            .ends dpart
            .model jsyn njf vto=-2 beta=1e-3
            """);

        var ext = Extract("DPART");
        var j1  = ext.Library.Find("dpart")!.Instances.Single(i => i.InstanceName == "J1");

        Assert.Equal("jsyn", j1.Reference);
        Assert.Contains(ext.Conflicts, c => c.Contains("'jsyn'", StringComparison.Ordinal)
                                         && c.Contains("refused rather than approximated", StringComparison.Ordinal));
    }

    // ── 5 — a card follows the SELECTED corner section ────────────────────────

    /// <summary>
    /// The same card stated in two corner sections gives two operating points: a forward-biased
    /// diode driven at 1 mA sits n·Vt·ln(10⁴) ≈ 0.25 V higher on the section whose saturation
    /// current is 10⁴ smaller.
    /// </summary>
    [Fact]
    public void ACardInsideCornerSections_FollowsTheSelectedSection()
    {
        InstallSubcircuitKit();
        Write("models/corners.lib", """
            .lib leaky
            .model dsyn d is=1e-12 n=1.05
            .endl leaky
            .lib tight
            .model dsyn d is=1e-16 n=1.05
            .endl tight
            """);
        string corners = Path.Combine(_root, "models", "corners.lib");

        double ForwardVolts(string section)
        {
            var read = PdkCorners.SectionFor(corners, section, out _)!;
            var ext  = Extract("DPART", [read]);
            var x1   = ext.TestBench.Instances.Single(i => i.InstanceName == "X1");
            ext.TestBench.Instances.Add(new Instance("I1", "I_1Tone", [x1.NetBindings[0], "0"],
                [new("Idc", "1e-3"), new("Freq", "1e9"), new("I", "0")]));
            ext.TestBench.Instances.Add(new Instance("V0", "Vdc", [x1.NetBindings[1], "0"], [new("Vdc", "0")]));

            var nl = new Elaborator(ext.Library).Elaborate(ext.TestBench);
            var dc = NonlinearDcEngine.Run(nl);
            return dc.NodeVoltages[nl.Nodes.IndexOf(x1.NetBindings[0]) - 1];
        }

        double leaky = ForwardVolts("leaky"), tight = ForwardVolts("tight");
        Assert.True(tight > leaky + 0.2, $"tight {tight.ToString(CultureInfo.InvariantCulture)} V vs leaky {leaky.ToString(CultureInfo.InvariantCulture)} V");
    }

    // ── 7 — a symbol whose netlist line is a primitive element on a card ───────

    [Fact]
    public void APrimitiveElementSymbol_OnACard_EmitsTheBuiltIn()
    {
        Write("libs.sym/dprim.sym", """
            v {version=1.2}
            K {type=diode
            format="@name @pinlist @model area='@r_w * @r_l ' m=@mult"
            template="name=D1
            model=dprim_card
            r_w=2u
            r_l=3u
            mult=1"
            }
            L 4 0 -20 0 20 {}
            B 5 -2.5 -22.5 2.5 -17.5 {name=p dir=inout}
            B 5 -2.5 17.5 2.5 22.5 {name=n dir=inout}
            """);
        Write("libs.spice/prims.lib", ".model dprim_card d is=2e-15 n=1.1\n");

        var outcome = PdkPartInstaller.Install(PdkImporter.Import(_root, Kit));
        PdkKitRegistry.SetKit(null, outcome.KitName, outcome.Parts ?? []);

        var ext  = Extract("dprim");
        var cell = ext.Library.Cells.Single(c => c.Name.StartsWith("kitelem_", StringComparison.Ordinal));
        var d    = Assert.Single(cell.Instances);

        Assert.Equal("Diode", d.Reference);
        Assert.Equal("2E-15", Value(d, "Is"));
        Assert.Contains("r_w", Value(d, "Area"), StringComparison.Ordinal);
        Assert.Equal(cell.Name, ext.TestBench.Instances.Single(i => i.InstanceName == "X1").Reference);
    }

    /// <summary>
    /// Two kits whose primitive parts share an id get two cells. The extraction keeps the first cell
    /// of a name it meets, and a name made from the part id alone ran the second kit's part on the
    /// first kit's card, silently.
    /// </summary>
    [Fact]
    public void PrimitivePartsOfOneIdFromTwoKits_EachKeepTheirOwnCard()
    {
        foreach (var (kit, isat) in new[] { ("KitOne", "2e-15"), ("KitTwo", "7e-15") })
        {
            string root = Path.Combine(_root, kit);
            Directory.CreateDirectory(Path.Combine(root, "libs.sym"));
            Directory.CreateDirectory(Path.Combine(root, "libs.spice"));
            File.WriteAllText(Path.Combine(root, "libs.sym", "dprim.sym"), """
                v {version=1.2}
                K {type=diode
                format="@name @pinlist @model"
                template="name=D1 model=dprim_card"
                }
                B 5 -2.5 -22.5 2.5 -17.5 {name=p dir=inout}
                B 5 -2.5 17.5 2.5 22.5 {name=n dir=inout}
                """);
            File.WriteAllText(Path.Combine(root, "libs.spice", "prims.lib"), $".model dprim_card d is={isat}\n");
            var outcome = PdkPartInstaller.Install(PdkImporter.Import(root, kit));
            PdkKitRegistry.SetKit(null, outcome.KitName, outcome.Parts ?? []);
        }

        var model = new SchematicEditModel { SchematicDirectory = Path.GetTempPath() };
        int n = 0;
        foreach (string kit in new[] { "KitOne", "KitTwo" })
            model.Components.Add(new EditableComponent
            {
                InstanceName = $"X{++n}", Symbol = SymbolKind.Generic, CellRef = PdkKitRegistry.RefFor(kit, "dprim"),
            });
        var ext = NetExtractor.Extract(model, "tb");

        string IsOf(string instance)
        {
            string cell = ext.TestBench.Instances.Single(i => i.InstanceName == instance).Reference;
            return Value(Assert.Single(ext.Library.Find(cell)!.Instances), "Is");
        }
        Assert.Equal("2E-15", IsOf("X1"));
        Assert.Equal("7E-15", IsOf("X2"));
    }
}
