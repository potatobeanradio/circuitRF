// C0 — technology import. Every fixture here is SYNTHETIC: the repository commits no third-party
// process data, and a made-up process exercises the rules exactly as a real one does. The names are
// invented on purpose.

using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.TechImport;
using CircuitRF.Ui.Theming;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout.TechImport;

public class ProcessStackReaderTests
{
    /// <summary>
    /// A small back-end stack in the shape a real one has: an ambient slab, conductors embedded in the
    /// oxide above them, and a via naming the two conductors it joins.
    /// </summary>
    internal const string Stack = """
        $ a comment, and the header below is the technology's own name for itself
        TECHNOLOGY = FABX

        DIELECTRIC air       {THICKNESS=4.0   ER=1.0 }
        DIELECTRIC capOx     {THICKNESS=2.5   ER=4.0 }
        CONDUCTOR  MetalTop  {THICKNESS=2.0   WMIN=1.0  SMIN=1.2  RPSQ=0.02 }
        DIELECTRIC midOx     {THICKNESS=1.5   ER=4.0 }
        CONDUCTOR  MetalLow  {THICKNESS=0.5   WMIN=0.2  SMIN=0.25 RPSQ=0.1  }
        DIELECTRIC baseOx    {THICKNESS=0.8   ER=3.9 }

        VIA ViaMid { FROM=MetalLow  TO=MetalTop  AREA=0.04  RPV=5 }
        """;

    /// <summary>The fixture stack, parsed — so a sibling test file need not repeat the parse call.</summary>
    internal static ProcessStackDescription Read() => ProcessStackReader.Read(Stack);

    [Fact]
    public void RecognisedByGrammar_NotByExtension()
    {
        Assert.True(ProcessStackReader.LooksLikeStackFile(Stack));

        // A declaration with no conductors describes no stack, and a conductor with no declaration is
        // not this format. Both are refused, so a scan cannot pick up an unrelated file.
        Assert.False(ProcessStackReader.LooksLikeStackFile("TECHNOLOGY = FABX\n"));
        Assert.False(ProcessStackReader.LooksLikeStackFile("CONDUCTOR M1 {THICKNESS=1}\n"));
        Assert.False(ProcessStackReader.LooksLikeStackFile(""));
    }

    [Fact]
    public void ReadsEveryStatementInFileOrder_BecauseTheOrderIsTheGeometry()
    {
        var d = ProcessStackReader.Read(Stack);

        Assert.Equal("FABX", d.TechnologyName);
        Assert.Equal(
            ["air", "capOx", "MetalTop", "midOx", "MetalLow", "baseOx", "ViaMid"],
            d.Entries.Select(e => e.Name));

        var top = d.Entries.Single(e => e.Name == "MetalTop");
        Assert.Equal(ProcessStackEntryKind.Conductor, top.Kind);
        Assert.Equal(2.0,  top.ThicknessUm);
        Assert.Equal(0.02, top.SheetResistanceOhmPerSquare);
        Assert.Equal(1.0,  top.MinWidthUm);
        Assert.Equal(1.2,  top.MinSpacingUm);

        var via = d.Entries.Single(e => e.Name == "ViaMid");
        Assert.Equal(ProcessStackEntryKind.Via, via.Kind);
        Assert.Equal("MetalLow", via.SpanFrom);
        Assert.Equal("MetalTop", via.SpanTo);
        Assert.Equal(0.04, via.CrossSectionUm2);
        Assert.Equal(5.0,  via.ResistanceOhms);
    }

    [Fact]
    public void ACommentNeverSwallowsTheRestOfTheFile()
    {
        // The comment marker is stripped to end of LINE, not to end of text — the other reading
        // silently drops every statement after the first commented one.
        var d = ProcessStackReader.Read(
            "TECHNOLOGY = T $ named here\nCONDUCTOR A {THICKNESS=1 RPSQ=1} $ trailing\n" +
            "CONDUCTOR B {THICKNESS=2 RPSQ=1}\n");

        Assert.Equal(["A", "B"], d.Entries.Select(e => e.Name));
    }

    [Fact]
    public void StatementsThisReaderPassesOverAreNamed_NotSilentlyIgnored()
    {
        var d = ProcessStackReader.Read(Stack + "\nGLOBAL_TEMPERATURE = 25\nBACKGROUND_ER {ER=1}\n");

        string note = Assert.Single(d.Notes, n => n.Contains("passed over"));
        Assert.Contains("GLOBAL_TEMPERATURE", note);
        Assert.Contains("BACKGROUND_ER", note);
    }

    [Fact]
    public void ABraceBlockMaySpanLines()
    {
        var d = ProcessStackReader.Read(
            "TECHNOLOGY = T\nCONDUCTOR A\n{\n  THICKNESS=1.5\n  RPSQ=0.5\n}\n");

        var a = Assert.Single(d.Entries);
        Assert.Equal(1.5, a.ThicknessUm);
        Assert.Equal(0.5, a.SheetResistanceOhmPerSquare);
    }
}

public class LayerPropertiesReaderTests
{
    /// <summary>
    /// A layer table in the shape a real one has: a default namespace, purpose-suffixed names, a
    /// grouping row that names no stream layer, and a row stating no colour.
    /// </summary>
    internal const string Table = """
        <?xml version='1.0' encoding='UTF-8'?>
        <layer-properties xmlns='http://example.invalid/layer-properties'>
          <properties>
            <fill-color>#112233</fill-color>
            <frame-color>#445566</frame-color>
            <visible>true</visible>
            <name>MetalTop.drawing</name>
            <source>10/0</source>
          </properties>
          <properties>
            <fill-color>#112233</fill-color>
            <visible>false</visible>
            <name>MetalTop.pin</name>
            <source>10/2</source>
          </properties>
          <properties>
            <frame-color>#778899</frame-color>
            <visible>true</visible>
            <name>MetalLow.drawing</name>
            <source>5/0</source>
          </properties>
          <properties>
            <visible>true</visible>
            <name>ViaMid.drawing</name>
            <source>7/0</source>
          </properties>
          <properties>
            <name>a grouping row</name>
          </properties>
        </layer-properties>
        """;

    [Fact]
    public void RecognisedByStructure_ThroughItsNamespace()
    {
        // The namespace names the tool that wrote the file; matching on the LOCAL name is what lets
        // one carrying a namespace read exactly like one that does not.
        Assert.True(LayerPropertiesReader.LooksLikeLayerPropertiesFile(Table));

        Assert.False(LayerPropertiesReader.LooksLikeLayerPropertiesFile("<other><properties/></other>"));
        Assert.False(LayerPropertiesReader.LooksLikeLayerPropertiesFile("<layer-properties>not xml"));
        Assert.False(LayerPropertiesReader.LooksLikeLayerPropertiesFile("plain text"));
    }

    [Fact]
    public void ReadsStreamNumbers_NamesAndPurposes()
    {
        var t = LayerPropertiesReader.Read(Table);

        Assert.Equal(4, t.Entries.Count);

        var drawing = t.Entries[0];
        Assert.Equal(10, drawing.Layer);
        Assert.Equal(0,  drawing.Datatype);
        Assert.Equal("MetalTop", drawing.BaseName);
        Assert.Equal("drawing",  drawing.Purpose);
        Assert.Equal("MetalTop.drawing", drawing.FullName);
        Assert.Equal(0, drawing.Order);

        // Fill colour wins; frame colour is the fallback when there is no fill.
        Assert.Equal(new Rgba(0x11, 0x22, 0x33), drawing.Color);
        Assert.Equal(new Rgba(0x77, 0x88, 0x99), t.Entries[2].Color);

        // A row stating no colour at all yields none, so the builder can generate one.
        Assert.Null(t.Entries[3].Color);

        Assert.False(t.Entries[1].Visible);
    }

    [Fact]
    public void ARowNamingNoStreamLayerBecomesNoLayer_AndIsCounted()
    {
        var t = LayerPropertiesReader.Read(Table);

        Assert.DoesNotContain(t.Entries, e => e.BaseName.Contains("grouping"));
        Assert.Contains(t.Notes, n => n.Contains("name no stream layer"));
    }

    [Fact]
    public void ADuplicateStreamNumberKeepsTheFirst_AndSaysSo()
    {
        var t = LayerPropertiesReader.Read("""
            <layer-properties>
              <properties><name>First.drawing</name><source>3/0</source></properties>
              <properties><name>Second.drawing</name><source>3/0</source></properties>
            </layer-properties>
            """);

        Assert.Equal("First", Assert.Single(t.Entries).BaseName);
        Assert.Contains(t.Notes, n => n.Contains("declared twice"));
    }

    [Fact]
    public void ASourceQualifierIsDropped_NotReadAsPartOfTheNumber()
    {
        var t = LayerPropertiesReader.Read("""
            <layer-properties>
              <properties><name>A.drawing</name><source>12/3@1</source></properties>
              <properties><name>B.drawing</name><source>*/*</source></properties>
            </layer-properties>
            """);

        var a = Assert.Single(t.Entries);
        Assert.Equal(12, a.Layer);
        Assert.Equal(3,  a.Datatype);   // "3@1", not 3 followed by a guessed 1
    }

    [Fact]
    public void MalformedXmlIsReported_NeverThrown()
    {
        var t = LayerPropertiesReader.Read("<layer-properties><properties>");

        Assert.Empty(t.Entries);
        Assert.NotEmpty(t.Notes);
    }
}

public class ProcessTechnologyBuilderTests
{
    private static TechnologyImportResult BuildFixture() =>
        ProcessTechnologyBuilder.Build(
            ProcessStackReader.Read(ProcessStackReaderTests.Stack),
            LayerPropertiesReader.Read(LayerPropertiesReaderTests.Table),
            fallbackName: "unused");

    private static double ThicknessUm(StackupLayer l) => l.ThicknessDbu / ProcessTechnologyBuilder.DbuPerMicron;

    [Fact]
    public void AConductorsThicknessIsTakenOutOfTheInsulationAboveIt()
    {
        // THE load-bearing conversion. The file states a conductor's thickness AND the thickness of
        // the dielectric it sits inside, so the two overlap; circuitRF's stackup is a pile of slabs
        // whose thicknesses add up. Carried across verbatim the stack would be 2.5 µm too tall here
        // (the two conductors), and nothing downstream would say so.
        var stack = BuildFixture().Technology.Stackup.Layers
                                  .Where(l => l.Kind != StackupKind.Via).ToList();

        Assert.Equal(
            ["air", "capOx", "MetalTop", "midOx", "MetalLow", "baseOx"],
            stack.Select(l => l.Name));

        Assert.Equal(4.0, ThicknessUm(stack[0]), 9);   // air, untouched — no conductor in its run
        Assert.Equal(0.5, ThicknessUm(stack[1]), 9);   // capOx 2.5 − MetalTop 2.0
        Assert.Equal(2.0, ThicknessUm(stack[2]), 9);
        Assert.Equal(1.0, ThicknessUm(stack[3]), 9);   // midOx 1.5 − MetalLow 0.5
        Assert.Equal(0.5, ThicknessUm(stack[4]), 9);
        Assert.Equal(0.8, ThicknessUm(stack[5]), 9);   // below the bottom conductor: untouched

        // And the property that actually matters: the clear distance between the two conductors is
        // the separation the file describes, not the separation plus a metal thickness.
        Assert.Equal(1.0, ThicknessUm(stack[3]), 9);
    }

    [Fact]
    public void ThicknessIsTakenNearestSlabFirst_SoASplitRunDoesNotGoNegative()
    {
        // A process routinely splits the insulation above a conductor in two to model a liner
        // separately. Taking the whole conductor thickness out of the last slab alone drives that one
        // negative while the run as a whole is thick enough.
        var r = ProcessTechnologyBuilder.Build(
            ProcessStackReader.Read("""
                TECHNOLOGY = T
                DIELECTRIC linerA {THICKNESS=1.0 ER=4.0}
                DIELECTRIC linerB {THICKNESS=1.0 ER=4.0}
                CONDUCTOR  Thick  {THICKNESS=1.5 RPSQ=0.01}
                """),
            layerTable: null, fallbackName: "T");

        var stack = r.Technology.Stackup.Layers;
        Assert.Equal(["linerA", "Thick"], stack.Select(l => l.Name));   // linerB fully consumed
        Assert.Equal(0.5, ThicknessUm(stack[0]), 9);
        Assert.All(stack, l => Assert.True(l.ThicknessDbu >= 0));
    }

    [Fact]
    public void AConductorThickerThanItsInsulationKeepsItsThickness_AndIsReported()
    {
        var r = ProcessTechnologyBuilder.Build(
            ProcessStackReader.Read("""
                TECHNOLOGY = T
                DIELECTRIC thin  {THICKNESS=0.2 ER=4.0}
                CONDUCTOR  Fat   {THICKNESS=1.0 RPSQ=0.01}
                """),
            layerTable: null, fallbackName: "T");

        var fat = Assert.Single(r.Technology.Stackup.Layers, l => l.Name == "Fat");
        Assert.Equal(1.0, ThicknessUm(fat), 9);       // never silently thinned to fit
        Assert.Contains(r.Notes, n => n.Contains("Fat") && n.Contains("zero separation"));
    }

    [Fact]
    public void ConductivityComesFromSheetResistanceAndThickness()
    {
        var tech = BuildFixture().Technology;

        // σ = 1/(Rs·t): a square of sheet of thickness t has resistance 1/(σ·t).
        Assert.Equal(1.0 / (0.02 * 2.0e-6),
                     Assert.Single(tech.Stackup.Layers, l => l.Name == "MetalTop").SigmaSm, 3);
        Assert.Equal(1.0 / (0.10 * 0.5e-6),
                     Assert.Single(tech.Stackup.Layers, l => l.Name == "MetalLow").SigmaSm, 3);
    }

    [Fact]
    public void AVIasConductivityUsesTheDistanceItActuallyReaches()
    {
        var via = Assert.Single(BuildFixture().Technology.Stackup.Layers,
                                l => l.Kind == StackupKind.Via);

        // The reach is the clear separation between the two conductors — 1.0 µm here, which is the
        // converted midOx slab, not the file's stated 1.5 µm. σ = length/(R·A).
        Assert.Equal(1.0e-6 / (5.0 * 0.04e-12), via.SigmaSm, 3);

        Assert.Equal("MetalLow", via.SpanFromLayer);
        Assert.Equal("MetalTop", via.SpanToLayer);
        Assert.Equal(ViaFillKind.Solid, via.Fill);
        Assert.Equal(0, via.ThicknessDbu);            // a via has no thickness of its own (R-via-3)
    }

    [Fact]
    public void AVIaNamingAnAbsentConductorIsReported_NotGuessedAt()
    {
        var r = ProcessTechnologyBuilder.Build(
            ProcessStackReader.Read("""
                TECHNOLOGY = T
                DIELECTRIC ox {THICKNESS=1.0 ER=4.0}
                CONDUCTOR  A  {THICKNESS=0.5 RPSQ=0.1}
                VIA V {FROM=A TO=NotHere AREA=0.04 RPV=5}
                """),
            layerTable: null, fallbackName: "T");

        Assert.Equal(0, Assert.Single(r.Technology.Stackup.Layers, l => l.Kind == StackupKind.Via).SigmaSm);
        Assert.Contains(r.Notes, n => n.Contains("name a conductor the stack does not contain"));
    }

    [Fact]
    public void StackLayersAreBoundToTheLayerTableRowTheyAreDrawnOn()
    {
        var tech = BuildFixture().Technology;

        Assert.Equal([new LayerKey(10, 0)],
                     Assert.Single(tech.Stackup.Layers, l => l.Name == "MetalTop").DrawingLayers);
        Assert.Equal([new LayerKey(5, 0)],
                     Assert.Single(tech.Stackup.Layers, l => l.Name == "MetalLow").DrawingLayers);
        Assert.Equal([new LayerKey(7, 0)],
                     Assert.Single(tech.Stackup.Layers, l => l.Kind == StackupKind.Via).DrawingLayers);

        // The drawing purpose wins over every other purpose of the same layer — that is the geometry.
        Assert.DoesNotContain(tech.Stackup.Layers,
                              l => l.DrawingLayers.Contains(new LayerKey(10, 2)));
    }

    [Fact]
    public void ALayerNameCarriesItsPurpose_SoTwoPurposesOfOneLayerAreDistinguishable()
    {
        // Every name-first match in circuitRF (paste reconciliation, technology retargeting) keys on
        // LayerDef.Name. Two rows both called "MetalTop" would make that ambiguous.
        var layers = BuildFixture().Technology.Layers;

        Assert.Equal(["MetalTop.drawing", "MetalTop.pin", "MetalLow.drawing", "ViaMid.drawing"],
                     layers.Select(l => l.Name));
        Assert.Equal(layers.Select(l => l.Name).Distinct().Count(), layers.Count);
        Assert.Equal("drawing", layers[0].Purpose);
    }

    [Fact]
    public void ALayerStatingNoColourGetsAGeneratedOne_NotASharedDefault()
    {
        var r = BuildFixture();

        var generated = Assert.Single(r.Technology.Layers, l => l.Name == "ViaMid.drawing");
        Assert.Equal(FallbackPalette.For(new LayerKey(7, 0)).Color, generated.Color);
        Assert.Contains(r.Notes, n => n.Contains("no display colour"));
    }

    [Fact]
    public void MinimumWidthAndSpacingBecomeRules_WhereTheProcessStatesThem()
    {
        var rules = BuildFixture().Technology.DrcRules;

        var w = Assert.Single(rules, r => r.Kind == DrcRuleKind.MinWidth && r.Layer == new LayerKey(10, 0));
        Assert.Equal(1000, w.ValueDbu);                       // 1.0 µm
        var s = Assert.Single(rules, r => r.Kind == DrcRuleKind.MinSpacing && r.Layer == new LayerKey(5, 0));
        Assert.Equal(250, s.ValueDbu);                        // 0.25 µm
    }

    [Fact]
    public void DefaultsAreDerivedFromTheProcessRatherThanFixed()
    {
        var tech = BuildFixture().Technology;

        Assert.Equal(LayoutUnit.Um, tech.DefaultDisplayUnit);
        Assert.Equal(20,   tech.DefaultSnapDbu);              // a tenth of the 0.2 µm finest feature
        Assert.Equal(20,   tech.DefaultFlattenTolDbu);
        Assert.Equal(2000, tech.DefaultLabelHeightDbu);
        Assert.Equal(200,  tech.DefaultViaPadDbu);            // √0.04 µm², the smallest via's side
        Assert.Equal("FABX", tech.Name);                      // the file's own name, not the fallback
    }

    [Fact]
    public void TheLowestConductorIsChosenAsTheGroundReference_AndSaidSo()
    {
        // A process file states no ground plane, so this IS an inference — which is why it is stated
        // rather than made quietly. The bottom boundary of the stack is already a ground plane (the
        // bulk the stack is built on), so the lowest conductor is the one closest to it.
        var r = BuildFixture();

        var ground = Assert.Single(r.Technology.Stackup.Layers, l => l.IsGroundReference);
        Assert.Equal("MetalLow", ground.Name);
        Assert.Contains(r.Notes, n => n.Contains("MetalLow") && n.Contains("return path"));
    }

    [Fact]
    public void ADeviceLayerIsNeverChosenAsTheGroundReference()
    {
        // The one thing a process file DOES say about a conductor's role: which sheets are parts of a
        // transistor rather than layers anything routes on. Without it the lowest conductor in a
        // front-end stack is a diffusion, and every microstrip component would resolve its substrate
        // against a device layer while looking perfectly correct.
        var r = ProcessTechnologyBuilder.Build(
            ProcessStackReader.Read("""
                TECHNOLOGY = FABY
                DIELECTRIC ox     {THICKNESS=2.0 ER=4.0 }
                CONDUCTOR  MetalA {THICKNESS=1.0 RPSQ=0.02 }
                DIELECTRIC ox2    {THICKNESS=1.0 ER=4.0 }
                CONDUCTOR  MetalB {THICKNESS=0.5 RPSQ=0.10 }
                DIELECTRIC fox    {THICKNESS=0.4 ER=3.9 }
                CONDUCTOR  Gate   {THICKNESS=0.2 RPSQ=7.0 LAYER_TYPE=GATE}
                DIELECTRIC iso    {THICKNESS=0.4 ER=8.0 }
                CONDUCTOR  Diff   {THICKNESS=0.4 RPSQ=1.0 LAYER_TYPE=DIFFUSION}
                """), null, "fallback");

        var ground = Assert.Single(r.Technology.Stackup.Layers, l => l.IsGroundReference);
        Assert.Equal("MetalB", ground.Name);
        Assert.Contains(r.Notes, n => n.Contains("MetalB") && n.Contains("device"));
    }

    [Fact]
    public void ValidationFindsNothingAtAll()
    {
        // The whole point of the conversion: what comes out is a technology the editor accepts, with
        // nothing left to fix before a microstrip component can resolve a substrate.
        Assert.Empty(TechValidation.Validate(BuildFixture().Technology));
    }

    [Fact]
    public void TheResultRoundTripsThroughTheRealCtechWriterAndReader()
    {
        var tech = BuildFixture().Technology;
        var back = TechPersistence.Deserialize(TechPersistence.Serialize(tech));

        Assert.Equal(tech.Name, back.Name);
        Assert.Equal(tech.Layers.Count, back.Layers.Count);
        Assert.Equal(tech.DrcRules.Count, back.DrcRules.Count);
        Assert.Equal(tech.Stackup.Layers.Select(l => (l.Name, l.Kind, l.ThicknessDbu)),
                     back.Stackup.Layers.Select(l => (l.Name, l.Kind, l.ThicknessDbu)));
    }

    [Fact]
    public void AStackWithNoLayerTableStillBuilds_Degraded()
    {
        // Refusing would leave a user with a process whose stack circuitRF can plainly read and no
        // way to get at it. The degradation is reported rather than hidden.
        var r = ProcessTechnologyBuilder.Build(
            ProcessStackReader.Read(ProcessStackReaderTests.Stack), null, "fallback");

        Assert.Empty(r.Technology.Layers);
        Assert.NotEmpty(r.Technology.Stackup.Layers);
        Assert.Contains(r.Notes, n => n.Contains("No layer-table row matches these conductors"));
    }
}

public class ProcessTechnologyScanTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-techimport-" + Guid.NewGuid().ToString("N")[..12]);

    public ProcessTechnologyScanTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string Write(string relative, string text)
    {
        string path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void FilesAreFoundByContent_WhateverTheyAreCalledAndWhereverTheyAre()
    {
        Write("extract/rc/typical.dat",  ProcessStackReaderTests.Stack);
        Write("display/tech/table.xml",  LayerPropertiesReaderTests.Table);
        Write("docs/readme.txt",         "This kit is a technology. It has conductors.");

        var scan = ProcessTechnologyImport.Scan(_root);

        Assert.Equal("extract/rc/typical.dat", Assert.Single(scan.StackFiles).RelativePath);
        Assert.Equal("FABX", scan.StackFiles[0].Label);
        Assert.Equal("display/tech/table.xml", Assert.Single(scan.LayerTables).RelativePath);
        Assert.True(scan.HasStack);
    }

    [Fact]
    public void SeveralStackFilesAreAllOffered_BecauseTheyAreUsuallyProcessCorners()
    {
        Write("itf/fast.itf", ProcessStackReaderTests.Stack.Replace("FABX", "FABX_fast"));
        Write("itf/slow.itf", ProcessStackReaderTests.Stack.Replace("FABX", "FABX_slow"));
        Write("itf/typ.itf",  ProcessStackReaderTests.Stack);

        var scan = ProcessTechnologyImport.Scan(_root);

        // Sorted, so the choice a user is offered is the same run to run.
        Assert.Equal(["itf/fast.itf", "itf/slow.itf", "itf/typ.itf"],
                     scan.StackFiles.Select(f => f.RelativePath));
        Assert.Equal(["FABX_fast", "FABX_slow", "FABX"], scan.StackFiles.Select(f => f.Label));
    }

    [Fact]
    public void BinaryFilesAreNeverRead()
    {
        File.WriteAllBytes(Path.Combine(_root, "artwork.gds"),
                           [0, 6, 0, 2, 0, 7, 0, 0, 84, 69, 67, 72, 78, 79, 76, 79, 71, 89]);
        Write("itf/typ.itf", ProcessStackReaderTests.Stack);

        var scan = ProcessTechnologyImport.Scan(_root);

        Assert.Single(scan.StackFiles);
        Assert.Empty(scan.LayerTables);
    }

    [Fact]
    public void NothingFoundIsSaidPlainly_WithWhatWasMissing()
    {
        Write("docs/readme.txt", "nothing to see");

        var scan = ProcessTechnologyImport.Scan(_root);

        Assert.False(scan.HasStack);
        Assert.Contains(scan.Notes, n => n.Contains("No interconnect technology file"));
    }

    [Fact]
    public void AStackWithNoTableSaysWhatThatCosts()
    {
        Write("itf/typ.itf", ProcessStackReaderTests.Stack);

        var scan = ProcessTechnologyImport.Scan(_root);

        Assert.True(scan.HasStack);
        Assert.Contains(scan.Notes, n => n.Contains("No layer table"));
    }

    [Fact]
    public void AMissingFolderIsReported_NotThrown()
    {
        var scan = ProcessTechnologyImport.Scan(Path.Combine(_root, "nope"));

        Assert.False(scan.HasStack);
        Assert.NotEmpty(scan.Notes);
    }

    [Fact]
    public void ImportReadsTheChosenPairEndToEnd()
    {
        string stack = Write("itf/typ.itf", ProcessStackReaderTests.Stack);
        string table = Write("tech/table.lyp", LayerPropertiesReaderTests.Table);

        var r = ProcessTechnologyImport.Import(stack, table);

        Assert.Equal("FABX", r.Technology.Name);
        Assert.Equal(4, r.Technology.Layers.Count);
        Assert.Equal(7, r.Technology.Stackup.Layers.Count);   // 4 dielectric slabs, 2 conductors, 1 via
    }
}
