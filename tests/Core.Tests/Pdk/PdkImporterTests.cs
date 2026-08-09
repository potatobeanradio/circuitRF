using System.IO.Compression;
using CircuitRF.Core.Pdk;
using Xunit;

namespace CircuitRF.Core.Tests.Pdk;

/// <summary>
/// Import behaviour, exercised against synthetic kits built in a temp folder.
///
/// <para>The fixtures are invented rather than copied from any kit: they only need the SHAPES
/// a kit takes — a netlist beside model data, drawings in a per-cell database directory, icons in a
/// folder of their own — and inventing them keeps the suite free of anyone's kit.</para>
/// </summary>
public sealed class PdkImporterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf_pdk_" + Guid.NewGuid().ToString("N"));

    public PdkImporterTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        PdkFormatRegistry.Clear();
    }

    private string Write(string relativePath, string content)
    {
        string full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private void WriteBinary(string relativePath, int length)
    {
        string full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var bytes = new byte[length];
        bytes[0] = 0x67; bytes[1] = 0x45; bytes[2] = 0x00;      // a NUL early ⇒ treated as binary
        File.WriteAllBytes(full, bytes);
    }

    // ── the good case ─────────────────────────────────────────────────────────

    [Fact]
    public void KitWithNetlistAndIcons_ImportsPartsAndAttachesIcons()
    {
        Write("models/parts.net", """
            define WidgetA ( p1 p2 )
              R:R1 p1 p2 R=50 Ohm
            end WidgetA
            define WidgetB ( p1 p2 p3 )
              R:R1 p1 p2 R=10 Ohm
            end WidgetB
            """);
        // Both need artwork to be PARTS: a subcircuit the kit never drew is an internal building
        // block, not a component. Only WidgetA gets an icon — the icon assertion below is the point.
        Write("bitmaps/WidgetA.bmp", "not really a bitmap, only the name matters here");
        Write("symbols/WidgetB_SYM.dsn", "1  0 0 0\n");
        Write("data/net.s2p", "! touchstone\n# HZ S RI R 50\n1e9 0 0 0 0 0 0 0 0\n");

        var r = PdkImporter.Import(_root);

        Assert.Equal(2, r.Parts.Count);
        Assert.Contains(r.Parts, p => p.Id == "WidgetA");
        Assert.Contains(r.Parts, p => p.Id == "WidgetB");

        var a = r.Parts.Single(p => p.Id == "WidgetA");
        Assert.EndsWith("WidgetA.bmp", a.IconRelativePath);

        Assert.Contains(r.Assets, x => x.Kind == PdkAssetKind.NetworkData && x.FormatName.Contains("Touchstone"));
        Assert.NotEqual(PdkImportStatus.Failed, r.Status);
        Assert.NotEqual(PdkImportStatus.NotRecognized, r.Status);
    }

    // ── artwork is never lost, even when unreadable ───────────────────────────

    [Fact]
    public void UnreadableArtwork_IsStillFoundClassifiedAndAttachedToItsPart()
    {
        Write("models/parts.net", "define BigFet ( g d s )\n  R:R1 g d R=1 Ohm\nend BigFet\n");
        WriteBinary("lib/BigFet/symbol/symbol.oa", 256);
        WriteBinary("lib/BigFet/layout/layout.oa", 512);

        var r = PdkImporter.Import(_root);

        Assert.True(r.HasSymbolArtwork);
        Assert.True(r.HasLayoutArtwork);

        // Classified by the VIEW directory, so the role is known even though the bytes are not.
        var sym = r.Assets.Single(a => a.Kind == PdkAssetKind.SymbolArtwork);
        var lay = r.Assets.Single(a => a.Kind == PdkAssetKind.LayoutArtwork);
        Assert.Equal(PdkAssetSupport.RecognizedNotSupported, sym.Support);
        Assert.Equal(PdkAssetSupport.RecognizedNotSupported, lay.Support);

        // ...and both are attached to the part, so nothing has to be re-found later.
        var part = r.Parts.Single(p => p.Id == "BigFet");
        Assert.NotNull(part.SymbolArtwork);
        Assert.NotNull(part.LayoutArtwork);

        Assert.Contains(r.Findings, f => f.Summary.Contains("symbol drawing"));
        Assert.Contains(r.Findings, f => f.Summary.Contains("layout drawing"));
        Assert.All(r.Findings.Where(f => f.Severity == PdkFindingSeverity.Warning),
                   f => Assert.NotEmpty(f.SuggestedAction));
    }

    [Fact]
    public void ArtworkIsFoundWhereverTheKitPutIt_IncludingEscapedCellDirectories()
    {
        // Some kits escape capitals in directory names to survive case-insensitive filesystems,
        // and some keep drawings nowhere near the netlist. Both must still resolve.
        Write("models/parts.net", "define MyCell ( a b )\n  R:R1 a b R=1 Ohm\nend MyCell\n");
        WriteBinary("some/deep/place/%M%y%Cell/symbol/symbol.oa", 128);
        Write("elsewhere/icons/MYCELL.bmp", "icon");

        var r = PdkImporter.Import(_root);

        var part = r.Parts.Single(p => p.Id == "MyCell");
        Assert.NotNull(part.SymbolArtwork);
        Assert.NotNull(part.IconRelativePath);
    }

    [Fact]
    public void CellThatExistsOnlyAsArtwork_StillBecomesAPart()
    {
        WriteBinary("lib/OrphanCell/symbol/symbol.oa", 64);
        var r = PdkImporter.Import(_root);
        Assert.Contains(r.Parts, p => p.Id == "OrphanCell");
    }

    // ── the failure cases the user will actually hit ──────────────────────────

    [Fact]
    public void UnrecognizedKit_SaysWhatItSawRatherThanJustFailing()
    {
        Write("mystery/a.xyzzy", "?");
        Write("mystery/b.xyzzy", "?");
        Write("mystery/c.frobnicate", "?");

        var r = PdkImporter.Import(_root);

        Assert.Equal(PdkImportStatus.NotRecognized, r.Status);

        // The value is in the summary naming what was found, so the user has somewhere to go.
        string s = r.ToSummary();
        Assert.Contains("Not recognised", s);
        Assert.Contains(".xyzzy", s);
        Assert.Contains("×2", s);
        Assert.Contains(r.Findings, f => f.Severity == PdkFindingSeverity.Blocker &&
                                         !string.IsNullOrEmpty(f.SuggestedAction));
    }

    [Fact]
    public void MissingPath_AndNonKitFile_AreDistinguished()
    {
        var missing = PdkImporter.Import(Path.Combine(_root, "nope"));
        Assert.Equal(PdkImportStatus.Failed, missing.Status);
        Assert.Contains(missing.Findings, f => f.Summary.Contains("Nothing exists"));

        string stray = Write("stray.txt", "hello");
        var notAKit = PdkImporter.Import(stray);
        Assert.Equal(PdkImportStatus.Failed, notAKit.Status);
        Assert.Contains(notAKit.Findings, f => f.Summary.Contains("not a kit"));
    }

    [Fact]
    public void EmptyFolder_FailsClearly()
    {
        var r = PdkImporter.Import(_root);
        Assert.Equal(PdkImportStatus.Failed, r.Status);
        Assert.Contains(r.Findings, f => f.Summary.Contains("empty"));
    }

    [Fact]
    public void EmptyPath_DoesNotThrow()
    {
        var r = PdkImporter.Import("");
        Assert.Equal(PdkImportStatus.Failed, r.Status);
    }

    // ── zip ───────────────────────────────────────────────────────────────────

    [Fact]
    public void ZippedKit_ImportsTheSameWayAsAFolder()
    {
        Write("models/parts.net", "define Zipped ( a b )\n  R:R1 a b R=1 Ohm\nend Zipped\n");
        Write("bitmaps/Zipped.bmp", "icon");

        string zipPath = Path.Combine(Path.GetTempPath(), $"crf_pdk_{Guid.NewGuid():N}.zip");
        try
        {
            ZipFile.CreateFromDirectory(_root, zipPath);
            var r = PdkImporter.Import(zipPath);

            Assert.NotEqual(PdkImportStatus.Failed, r.Status);
            Assert.Contains(r.Assets, a => a.Kind == PdkAssetKind.Netlist);
            Assert.Contains(r.Assets, a => a.Kind == PdkAssetKind.PaletteIcon);
        }
        finally { try { File.Delete(zipPath); } catch { } }
    }

    [Fact]
    public void CorruptZip_ReportsThatRatherThanThrowing()
    {
        string bad = Path.Combine(_root, "broken.zip");
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(bad, [0x50, 0x4B, 0x03, 0x04, 0x00, 0x01, 0x02]);

        var r = PdkImporter.Import(bad);
        Assert.Equal(PdkImportStatus.Failed, r.Status);
        Assert.Contains(r.Findings, f => f.Severity == PdkFindingSeverity.Blocker);
    }

    // ── layer technology ──────────────────────────────────────────────────────

    [Fact]
    public void LayerTechnology_IsRecognizedFromContentNotJustExtension()
    {
        Write("tech/LayerMap.map", """
            #Layer Purpose GDSLayer GDSPurpose
            metal1 drawing 1 0
            metal2 drawing 2 0
            via drawing 3 0
            """);
        Write("tech/library.tech", """
            <!DOCTYPE Technology>
            <Lpp_List>
              <LPP purpose="drawing" layer="metal1" rgb="16711680" visible="1"/>
            </Lpp_List>
            """);
        Write("models/parts.net", "define P ( a b )\n  R:R1 a b R=1 Ohm\nend P\n");

        var r = PdkImporter.Import(_root);

        Assert.NotNull(r.LayerTechnology);
        Assert.Equal(2, r.Assets.Count(a => a.Kind == PdkAssetKind.LayerTechnology));
        Assert.Contains(r.Findings, f => f.Summary.Contains("Layer technology found"));
    }

    /// <summary>
    /// The two files an open-source kit ships for the simulator it was written for. Both were being
    /// reported as "unknown", which is wrong in opposite directions.
    ///
    /// <para>The <c>.osdi</c> is a COMPILED MODEL — the loader's own shared-object format under
    /// another extension, and the thing circuitRF actually evaluates the kit's devices with. Calling
    /// it unrecognised told the user their models were unreadable at the moment those models were
    /// what made the kit simulate.</para>
    ///
    /// <para>The <c>.spiceinit</c> is the other simulator's start-up file. It is genuinely not
    /// circuitRF's to run — it names search paths and which compiled models to load, both of which
    /// circuitRF works out for itself — but "unknown" invites the reader to go and make it work.
    /// Naming it and saying nothing needs running is the answer to the question it raises.</para>
    /// </summary>
    [Fact]
    public void ACompiledModelAndTheOtherSimulatorsSetup_AreNamedRatherThanCalledUnknown()
    {
        Write("models/parts.net", "define P ( a b )\n  R:R1 a b R=1 Ohm\nend P\n");
        WriteBinary("models/osdi/psp.osdi", 512);
        Write("models/.spiceinit", "osdi 'psp.osdi'\nsetcs sourcepath = ( $sourcepath ./models )\n");

        var r = PdkImporter.Import(_root);

        var osdi = r.Assets.Single(a => a.RelativePath.EndsWith("psp.osdi"));
        Assert.Equal(PdkAssetSupport.Supported, osdi.Support);
        Assert.Equal(PdkAssetKind.ModelData, osdi.Kind);
        Assert.Contains("Verilog-A", osdi.FormatName);

        var init = r.Assets.Single(a => a.RelativePath.EndsWith(".spiceinit"));
        Assert.NotEqual(PdkAssetSupport.Unrecognized, init.Support);
        Assert.Contains("start-up", init.FormatName);
        // It says the thing a reader needs: there is nothing here for them to run.
        Assert.Contains("needs running", init.Detail);

        Assert.DoesNotContain(r.Unrecognized, a => a.RelativePath.EndsWith(".osdi")
                                                || a.RelativePath.EndsWith(".spiceinit"));
    }

    [Fact]
    public void MapFileThatIsNotALayerMap_IsNotMistakenForOne()
    {
        Write("notes/readme.map", "this is just prose, not a layer table at all\nsecond line\n");
        Write("models/parts.net", "define P ( a b )\n  R:R1 a b R=1 Ohm\nend P\n");

        var r = PdkImporter.Import(_root);
        Assert.Null(r.LayerTechnology);
    }

    [Fact]
    public void LayoutArtworkWithoutLayerTechnology_IsCalledOut()
    {
        WriteBinary("lib/C1/layout/layout.oa", 64);
        var r = PdkImporter.Import(_root);
        Assert.Contains(r.Findings, f => f.Summary.Contains("no layer technology"));
    }

    // ── extensibility ─────────────────────────────────────────────────────────

    [Fact]
    public void HostRegisteredRecognizer_TakesPrecedenceOverTheBuiltIns()
    {
        Write("models/thing.net", "define P ( a b )\n  R:R1 a b R=1 Ohm\nend P\n");
        PdkFormatRegistry.Register(new StubRecognizer());

        var r = PdkImporter.Import(_root);

        var net = r.Assets.Single(a => a.RelativePath.EndsWith("thing.net"));
        Assert.Equal("stub format", net.FormatName);
    }

    private sealed class StubRecognizer : IPdkFormatRecognizer
    {
        public int Priority => 100;
        public PdkAsset? Recognize(string path, Func<string> peek)
            => path.EndsWith(".net", StringComparison.Ordinal)
                ? new PdkAsset(path, PdkAssetKind.Netlist, PdkAssetSupport.Supported, "stub format")
                : null;
    }

    [Fact]
    public void ARecognizerThatThrows_DoesNotFailTheImport()
    {
        Write("models/parts.net", "define P ( a b )\n  R:R1 a b R=1 Ohm\nend P\n");
        Write("symbols/P_SYM.dsn", "1  0 0 0\n");     // drawn, so it is a component
        PdkFormatRegistry.Register(new ThrowingRecognizer());

        var r = PdkImporter.Import(_root);
        Assert.NotEqual(PdkImportStatus.Failed, r.Status);
        Assert.Contains(r.Parts, p => p.Id == "P");
    }

    private sealed class ThrowingRecognizer : IPdkFormatRecognizer
    {
        public int Priority => 1000;
        public PdkAsset? Recognize(string path, Func<string> peek) => throw new InvalidOperationException("boom");
    }
}
