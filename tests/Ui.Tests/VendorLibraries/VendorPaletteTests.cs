using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Design.VendorLibraries;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels.Dock;
using Xunit;

namespace CircuitRF.Ui.Tests.VendorLibraries;

/// <summary>
/// Brief vendorlib-2 §5: the palette's Type menu, its virtualized part list, value-aware search,
/// arming by part identity, and what a placed part draws and lands with. Every library is generated
/// (<see cref="VendorLibraryFixture"/>); every gate is a counter or a structure, never a clock.
/// </summary>
public sealed class VendorPaletteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-vl2-" + Guid.NewGuid().ToString("N"));

    public VendorPaletteTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private string IndexedFixture()
    {
        string lib = Path.Combine(_dir, "lib");
        VendorLibraryFixture.Write(lib);
        VendorLibraryIndex.StartBuild(lib).GetAwaiter().GetResult();
        return lib;
    }

    private static PaletteTool PaletteOn(string library, string root)
    {
        var tool = new PaletteTool();
        tool.SetVendorLibraries([(library, root)]);
        tool.SelectedCategory = tool.Categories.First(c => c.DisplayName == library);
        return tool;
    }

    // 1 — the list realizes only the rows on screen. Hosting the view needs the Avalonia runtime,
    // which this project does not start, so the realized-container count was measured headlessly out
    // of tree (src/Ui/RESOLVED.md: 17 of 20,000, and 17 after scrolling to the end). What this holds is
    // the SHAPE that count depends on: a ListBox keeps its virtualizing panel, and nothing around it
    // is a ScrollViewer — one there hands the panel an infinite height and every row is built.
    [Fact]
    public void ThePartList_IsAListBoxWithItsVirtualizingPanel_AndNoScrollViewerAroundIt()
    {
        var xaml = XDocument.Load(Path.Combine(RepoRoot(), "src", "Ui", "Views", "Palette", "PaletteToolView.axaml"));
        var list = xaml.Descendants().Single(e => (string?)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "VendorList");

        Assert.Equal("ListBox", list.Name.LocalName);
        Assert.DoesNotContain(list.Elements(), e => e.Name.LocalName == "ListBox.ItemsPanel");
        Assert.DoesNotContain(list.Ancestors(), e => e.Name.LocalName == "ScrollViewer");
    }

    // 2 — typing in the search box, under a library and under All, reads no file.
    [Fact]
    public void AKeystroke_ReadsNoFile()
    {
        string lib = IndexedFixture();
        var tool = PaletteOn("Parts", lib);

        long before = VendorLibraryIndex.FileReads(lib);
        foreach (string q in new[] { "1", "10", "10p", "10p C0G", "TSTL", "" })
            tool.SearchQuery = q;
        tool.SelectedCategory = tool.Categories[0];
        foreach (string q in new[] { "4", "4n7", "" })
            tool.SearchQuery = q;

        Assert.Equal(before, VendorLibraryIndex.FileReads(lib));
    }

    // 3 — a value token matches the stated value whatever its spelling, and tokens AND.
    [Fact]
    public void ValueSearch_ReadsTheHeadersValue_AndTokensAnd()
    {
        string lib = Path.Combine(_dir, "lib");
        VendorLibraryFixture.Write(lib);
        var build = VendorLibraryIndex.Build(lib);
        VendorLibraryEntry Part(string name) => build.Entries.Single(e => e.Part == name);

        var tenPicoInMicro = Part("TSTC0201A0018");   // its header: C = 1.00e-05[uF]
        Assert.Equal(10e-12, tenPicoInMicro.Header.Value!.Value, 1e-18);
        Assert.True(VendorLibrarySearch.Match(tenPicoInMicro, "10p"));

        Assert.True(VendorLibrarySearch.Match(Part("TSTL0603B0037"), "4n7"));   // L = 4.7[nH]

        // Both series hold a 10 pF part; only one is C0G.
        Assert.Equal(["TSTC0201A0018"], build.Entries.Where(e => VendorLibrarySearch.Match(e, "10p C0G")).Select(e => e.Part));
    }

    // 4 — the Type menu follows the library's folders, counts its leaves, opens every sub-menu with
    // "All in", collapses a single-child folder and skips a single top folder.
    [Fact]
    public void TheTypeMenu_FollowsTheFolderTree_CollapsingLevelsThatOfferNoChoice()
    {
        static void Write(string root)
        {
            WriteParts(Path.Combine(root, "caps", "x.lib"), "X1", "X2");
            WriteParts(Path.Combine(root, "caps", "y.lib"), "Y1");
            WriteParts(Path.Combine(root, "ind", "sub", "z.lib"), "Z1");
        }
        string flat = Path.Combine(_dir, "flat");
        string nested = Path.Combine(_dir, "nested");
        Write(flat);
        Write(Path.Combine(nested, "top"));

        var menu = VendorMenuTree.Build("Lib", VendorLibraryIndex.Build(flat));
        Assert.Equal(["All in Lib", "caps", "ind ▸ sub ▸ z (1)"], menu.Children.Select(n => n.Header));
        Assert.Equal(["All in caps", "x (2)", "y (1)"], menu.Children[1].Children.Select(n => n.Header));
        Assert.Equal("Lib ▸ ind ▸ sub ▸ z", menu.Children[2].Entry!.Breadcrumb);

        var skipped = VendorMenuTree.Build("Lib", VendorLibraryIndex.Build(nested));
        Assert.Equal(menu.Children.Select(n => n.Header), skipped.Children.Select(n => n.Header));
        Assert.Equal("Lib ▸ top ▸ caps ▸ x", skipped.Children[1].Children[1].Entry!.Breadcrumb);
    }

    // 5 — arming one part lights that row and no other: every vendor part shares one kind.
    [Fact]
    public void ArmingAPart_ArmsThatRowAlone()
    {
        string lib = IndexedFixture();
        var tool = PaletteOn("Parts", lib);
        var svc = new PlacementService();
        tool.SetPlacementService(svc);

        tool.VendorRows[3].ArmCommand.Execute(null);
        tool.VendorRows[7].ArmCommand.Execute(null);

        Assert.Equal([tool.VendorRows[7]], tool.VendorRows.Where(r => r.IsArmed));
        Assert.Equal(tool.VendorRows[7].Part, svc.Pending!.Vendor!.Part);
    }

    // 6 — a placed C-glyph part whose library folder is gone still draws the capacitor, and reads no file.
    [Fact]
    public void AGlyphPart_WithItsLibraryMissing_DrawsTheCapacitor_AndReadsNoFile()
    {
        string ws = Path.Combine(_dir, "ws");
        string gone = Path.Combine(_dir, "moved-away");
        Directory.CreateDirectory(Path.Combine(ws, "cells", "top"));
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"),
            new CwsFile { VendorLibraries = [new CwsVendorLibraryRef { Name = "Gone", Path = gone }] });

        string symbolRef = SpiceModelSymbolProvider.RefFor("vendorlib://Gone/caps/x.lib", "TSTC0402A0001",
            SnpPinConfig.Standard, SnpPitch.Loose, glyph: "C");
        var res = CellSymbolResolver.Resolve(symbolRef, Path.Combine(ws, "cells", "top"));

        Assert.Equal(CellSymbolState.NotFound, res.State);
        Assert.Same(BuiltInSymbols.Primitives(SymbolKind.Capacitor, 2).Primitives, res.Symbol!.Primitives);
        Assert.Equal(0, VendorLibraryIndex.FileReads(gone));
    }

    // 7 — the footprint comes from the case pair, both codes matching one row.
    [Fact]
    public void TheFootprint_ComesFromTheCasePair_BothCodesMatching()
    {
        var pcb  = ShippedTechnologies.Load("pcb-2layer_FR-4_70mil_1oz");
        var mmic = ShippedTechnologies.Load("mmic-GaAs_2LM_100um");

        Assert.Equal("smt:0402@N",   FootprintDefaults.ForCaseCodes("1005", "0402", pcb));
        Assert.Equal("smt:0201@N",   FootprintDefaults.ForCaseCodes("0603", "0201", pcb));
        Assert.Equal("smt:008004@N", FootprintDefaults.ForCaseCodes("0201", "008004", pcb));

        Assert.Null(FootprintDefaults.ForCaseCodes("0402", "0201", pcb));   // a pair the table lacks
        Assert.Null(FootprintDefaults.ForCaseCodes(null, null, pcb));       // no pair (every inductor)
        Assert.Null(FootprintDefaults.ForCaseCodes("1005", "0402", mmic));  // not a board
        // No technology yet: the part still carries its case, and the land pattern waits for a board technology.
        Assert.Equal("smt:0402@N", FootprintDefaults.ForCaseCodes("1005", "0402", null));
    }

    // 7b — a header that states the body in millimetres names the case whose body it is; a size no row fits names none.
    [Theory]
    [InlineData("* Size(LxWxT): 1x0.5x0.5mm, 0.039x0.02x0.02inches", "1005", "0402")]
    [InlineData("* Size(LxWxT): 3.2x2.5x2.2mm", "3225", "1210")]   // also inside a square RF chip's band; the nearer wins
    [InlineData("* Size(LxWxT): 2.5x2x1.8mm", null, null)]          // no row of the table
    public void ABodySizeInMillimetres_NamesTheCase(string line, string? metric, string? imperial)
    {
        var h = VendorLibraryHeader.Parse(["* P/N: TSTC0001", "* Property: C=680pF", line], out _);
        Assert.Equal(metric, h.CaseMetric);
        Assert.Equal(imperial, h.CaseImperial);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static void WriteParts(string file, params string[] parts)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, string.Concat(parts.Select(p =>
            $"* Property : C = 1[pF]\n.SUBCKT {p} 1 2\nC1 1 2 1p\n.ENDS {p}\n")));
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "circuitRF.slnx"))) d = d.Parent;
        return d!.FullName;
    }
}
