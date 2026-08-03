using CircuitRF.Core.Pdk;
using Xunit;

namespace CircuitRF.Core.Tests.Pdk;

/// <summary>
/// A kit need not have a netlist OR a cell-database tree. It can declare its parts in a catalog and
/// supply their behaviour from a compiled model library — and such a kit used to import as a pile of
/// recognised files with an empty palette and no explanation.
///
/// <para>Fixtures are synthetic. The catalog element names are format vocabulary; nothing here keys
/// off a schema URI or namespace, and these tests use an invented one to prove it.</para>
/// </summary>
public class ComponentCatalogDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf_cat_" + Guid.NewGuid().ToString("N"));

    public ComponentCatalogDiscoveryTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private void Write(string relative, string content)
    {
        string full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static string Catalog(params (string Name, string Model)[] parts)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\"?>");
        sb.AppendLine("<XML_COMPONENT_DATA xmlns=\"urn:example-not-read\">");
        foreach (var (name, model) in parts)
        {
            sb.AppendLine($"  <COMPONENT Name=\"{name}\">");
            sb.AppendLine($"    <MODEL>{model}</MODEL>");
            sb.AppendLine($"    <DESC>a synthetic part</DESC>");
            sb.AppendLine($"    <SYMBOL>Shape@Sample.sym</SYMBOL>");
            sb.AppendLine( "  </COMPONENT>");
        }
        sb.AppendLine("</XML_COMPONENT_DATA>");
        return sb.ToString();
    }

    // ── recognition ───────────────────────────────────────────────────────────

    [Fact]
    public void ACatalogIsRecognised_AndItsPartsBecomePlaceable()
    {
        Write("XML/SampleParts.xml", Catalog(("PART_A", "PART_A"), ("PART_B", "PART_B")));

        var r = PdkImporter.Import(_root);

        Assert.Contains(r.Assets, a => a.Kind == PdkAssetKind.ComponentCatalog &&
                                       a.Support == PdkAssetSupport.Supported);
        Assert.Equal(["PART_A", "PART_B"], r.Parts.Select(p => p.Id).OrderBy(x => x));
    }

    [Fact]
    public void TheModelNameIsTheIdentity_NotTheDisplayName()
    {
        // Real catalogs disagree between the two: an entry named for a package variant can point at
        // a shared model. The id has to be the name the kit will be ASKED for, or nothing resolves.
        Write("XML/SampleParts.xml", Catalog(("PART_VARIANT_PKG", "PART_SHARED_MODEL")));

        var r = PdkImporter.Import(_root);

        Assert.Equal("PART_SHARED_MODEL", Assert.Single(r.Parts).Id);
    }

    [Fact]
    public void AnEntryWithNoModel_FallsBackToItsDeclaredName()
    {
        Write("XML/SampleParts.xml",
              "<?xml version=\"1.0\"?><ROOT>" +
              "<COMPONENT Name=\"PART_A\"><DESC>x</DESC></COMPONENT>" +
              "<COMPONENT Name=\"PART_B\"><DESC>y</DESC></COMPONENT></ROOT>");

        var r = PdkImporter.Import(_root);
        Assert.Equal(["PART_A", "PART_B"], r.Parts.Select(p => p.Id).OrderBy(x => x));
    }

    [Fact]
    public void ANamespacePrefixedCatalogReadsToo()
    {
        // Element lookup is by LOCAL name; a kit written by a different tool with the same shape
        // must read, which is why nothing here matches on a namespace.
        Write("XML/SampleParts.xml",
              "<?xml version=\"1.0\"?><x:ROOT xmlns:x=\"urn:whatever\">" +
              "<x:COMPONENT Name=\"PART_A\"><x:MODEL>MODEL_A</x:MODEL></x:COMPONENT>" +
              "<x:COMPONENT Name=\"PART_B\"><x:MODEL>MODEL_B</x:MODEL></x:COMPONENT></x:ROOT>");

        var r = PdkImporter.Import(_root);
        Assert.Equal(["MODEL_A", "MODEL_B"], r.Parts.Select(p => p.Id).OrderBy(x => x));
    }

    [Fact]
    public void AnXmlFileThatMerelyMENTIONSComponents_IsNotACatalog()
    {
        // A document that talks about components is not a catalog. What separates them is an
        // element that actually NAMES one — not the number of times the word appears, which was
        // the first rule tried and which wrongly rejected a kit offering a single part.
        Write("XML/Notes.xml",
              "<?xml version=\"1.0\"?><doc><!-- each COMPONENT is described below --> " +
              "<section>COMPONENT overview</section><COMPONENT/><COMPONENT/></doc>");

        var r = PdkImporter.Import(_root);
        Assert.DoesNotContain(r.Assets, a => a.Kind == PdkAssetKind.ComponentCatalog);
    }

    [Fact]
    public void ACatalogOfferingASINGLEPart_IsStillACatalog()
    {
        Write("XML/SampleParts.xml", Catalog(("PART_ONLY", "PART_ONLY")));

        var r = PdkImporter.Import(_root);

        Assert.Contains(r.Assets, a => a.Kind == PdkAssetKind.ComponentCatalog);
        Assert.Equal("PART_ONLY", Assert.Single(r.Parts).Id);
    }

    [Fact]
    public void DuplicateEntriesAcrossCatalogs_YieldOnePartEach()
    {
        Write("XML/FamilyOne.xml", Catalog(("PART_A", "PART_A"), ("PART_B", "PART_B")));
        Write("XML/FamilyTwo.xml", Catalog(("PART_B", "PART_B"), ("PART_C", "PART_C")));

        var r = PdkImporter.Import(_root);
        Assert.Equal(["PART_A", "PART_B", "PART_C"], r.Parts.Select(p => p.Id).OrderBy(x => x));
    }

    [Fact]
    public void TheCatalogFileNameGroupsThePart()
    {
        // A kit of this shape splits its catalog by part family, one file each — the only grouping
        // it offers, and a real one.
        Write("XML/Mixers.xml", Catalog(("PART_A", "PART_A"), ("PART_B", "PART_B")));

        var r = PdkImporter.Import(_root);
        Assert.All(r.Parts, p => Assert.Equal("Mixers", p.Category));
    }

    [Fact]
    public void ACatalogPartFindsItsIcon()
    {
        Write("XML/SampleParts.xml", Catalog(("PART_A", "PART_A")));
        Write("bitmaps/PART_A.bmp", "not really a bitmap");

        var r = PdkImporter.Import(_root);
        Assert.EndsWith("PART_A.bmp", Assert.Single(r.Parts).IconRelativePath);
    }

    // ── binding a part to the symbol it names ─────────────────────────────────

    /// <summary>Builds a symbol library the way the format lays one out.</summary>
    private static byte[] SymbolLibrary(params (string Name, int Pins)[] symbols)
    {
        var bytes = new List<byte>();
        foreach (var (name, pins) in symbols)
        {
            bytes.AddRange("KDefaultSymb_2"u8.ToArray());
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(name + "@Sample.lib"));
            for (int p = 1; p <= pins; p++)
            {
                bytes.AddRange("KNodePos"u8.ToArray());
                foreach (int v in new[] { 3, p * 100, 0, -100, -50, 0, 1 })
                    bytes.AddRange(BitConverter.GetBytes(v));
                bytes.AddRange(System.Text.Encoding.ASCII.GetBytes($"{p}]["));
            }
        }
        return [.. bytes];
    }

    private void WriteBytes(string relative, byte[] content)
    {
        string full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
    }

    private static string CatalogWithSymbol(string part, string symbolRef) =>
        $"""
        <?xml version="1.0"?><ROOT>
          <COMPONENT Name="{part}"><MODEL>{part}</MODEL><SYMBOL>{symbolRef}</SYMBOL></COMPONENT>
          <COMPONENT Name="{part}_2"><MODEL>{part}_2</MODEL><SYMBOL>{symbolRef}</SYMBOL></COMPONENT>
        </ROOT>
        """;

    [Fact]
    public void APartTakesItsTerminalsFromTheSymbolItNAMES()
    {
        WriteBytes("Symbols/Sample.syf", SymbolLibrary(("ThreePin", 3)));
        Write("XML/Parts.xml", CatalogWithSymbol("PART_A", "ThreePin@Sample.syf"));

        var r = PdkImporter.Import(_root);

        Assert.All(r.Parts, p => Assert.Equal(3, p.PinCount));
        Assert.All(r.Parts, p => Assert.Equal(3, p.Pins!.Count));
    }

    [Fact]
    public void SEVERALPartsSHARETheSameTemplate()
    {
        // The whole point of a library: a handful of templates serve a whole kit, and every part
        // that names one gets the same geometry — which is what lets the palette show the symbol
        // the schematic will use.
        WriteBytes("Symbols/Sample.syf", SymbolLibrary(("Shared", 2)));
        Write("XML/Parts.xml", CatalogWithSymbol("PART_A", "Shared@Sample.syf"));

        var r = PdkImporter.Import(_root);

        Assert.Equal(2, r.Parts.Count);
        Assert.Equal(r.Parts[0].Pins, r.Parts[1].Pins);
    }

    [Fact]
    public void AKitSpellingItsOwnSymbolDifferently_StillBinds()
    {
        // A kit references `IQ_Mixer` for a symbol its own library declares as `IQ Mixer`.
        // Matching only exactly cost every part of that family its pins — 18 of 109 — and did it
        // silently, since a part with no pins still imports and still appears.
        WriteBytes("Symbols/Sample.syf", SymbolLibrary(("Two Word", 4)));
        Write("XML/Parts.xml", CatalogWithSymbol("PART_A", "Two_Word@Sample.syf"));

        var r = PdkImporter.Import(_root);
        Assert.All(r.Parts, p => Assert.Equal(4, p.PinCount));
    }

    [Fact]
    public void APartNamingAnUnKNOWNSymbol_GetsNoInventedPins()
    {
        WriteBytes("Symbols/Sample.syf", SymbolLibrary(("Present", 3)));
        Write("XML/Parts.xml", CatalogWithSymbol("PART_A", "Absent@Sample.syf"));

        var r = PdkImporter.Import(_root);

        Assert.NotEmpty(r.Parts);
        Assert.All(r.Parts, p => Assert.Equal(0, p.PinCount));
        Assert.All(r.Parts, p => Assert.Null(p.Pins));
    }

    [Fact]
    public void ASymbolLibraryIsNotMistakenForOnePartsArtwork()
    {
        // It is not one part's drawing; matching it to a part by file name would find nothing, and
        // counting it as unreadable symbol artwork would warn about a file that reads perfectly.
        WriteBytes("Symbols/Sample.syf", SymbolLibrary(("ThreePin", 3)));
        Write("XML/Parts.xml", CatalogWithSymbol("PART_A", "ThreePin@Sample.syf"));

        var r = PdkImporter.Import(_root);

        Assert.Contains(r.Assets, a => a.Kind == PdkAssetKind.SymbolLibrary &&
                                       a.Support == PdkAssetSupport.Supported);
        Assert.DoesNotContain(r.Assets, a => a.Kind == PdkAssetKind.SymbolArtwork);
        Assert.All(r.Parts, p => Assert.Null(p.SymbolArtwork));
    }

    // ── the findings, which are the other half of the fix ─────────────────────

    [Fact]
    public void AKitYieldingNoParts_ALWAYS_SaysWhy()
    {
        // Recognisable files, no parts. This is the case that used to report nothing at all: the
        // user saw an empty palette with no reason for it.
        Write("data/net.s2p", "! synthetic\n# GHZ S RI R 50\n1.0 0 0 0 0 0 0 0 0\n");

        var r = PdkImporter.Import(_root);

        Assert.Empty(r.Parts);
        var f = Assert.Single(r.Findings, x => x.Summary.Contains("No placeable parts"));
        Assert.False(string.IsNullOrWhiteSpace(f.SuggestedAction),
                     "a finding with no suggested action leaves the user nowhere to go");
        Assert.Contains("none of the ways", f.Summary);
    }

    [Fact]
    public void TheReasonNamesWhichSHAPEWasFound()
    {
        // A kit with drawings that are not a cell database fails for a different reason than one
        // with no declaration at all, and the two need different messages to be actionable.
        Write("art/PART_A.dsn", "symbol");
        Write("art/PART_B.dsn", "symbol");

        var r = PdkImporter.Import(_root);

        Assert.Empty(r.Parts);
        Assert.Contains(r.Findings, x => x.Summary.Contains("not arranged as a cell database"));
    }

    [Fact]
    public void AKitThatDOESYieldParts_DoesNotGetTheEmptyWarning()
    {
        Write("XML/SampleParts.xml", Catalog(("PART_A", "PART_A"), ("PART_B", "PART_B")));

        var r = PdkImporter.Import(_root);

        Assert.NotEmpty(r.Parts);
        Assert.DoesNotContain(r.Findings, x => x.Summary.Contains("No placeable parts"));
    }
}
