// TFR-V R-tfrv-5 — one TFR, both contact styles. The film decides the sheet resistance and the contact, on a test-only technology
// with one overlap film, on Metal1, and two via films, on a level of their own.

using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Lvs;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Tests.Em3d;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.Mmic;

public sealed class TfrFilmChoiceTests : IDisposable
{
    private static readonly LayerKey Metal1 = new(1, 0), Contact = new(11, 0), Via50 = new(4, 0), Hi = new(41, 0), Lap = new(40, 0);
    private const string Via50Name = "Resistor", HiName = "Resistor Hi", LapName = "Resistor Lap";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-tfrfilm-" + Guid.NewGuid().ToString("N")[..12]);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private static Technology TwoStyles()
    {
        var tech = TechPersistence.LoadFromFile(Path.Combine(
            PalaceBackendTests.RepoRoot(), "testdata", "em-tfr", "zigzag-via", "tech", "zigzag-via-GaAs.ctech"));
        LayerDef Add(string name, LayerKey key)
        {
            var def = new LayerDef { Name = name, Key = key, Color = tech.Layers[0].Color };
            tech.Layers.Add(def);
            return def;
        }
        Add(HiName, Hi);
        Add(LapName, Lap);
        var stack = tech.Stackup.Layers;
        var level = stack.Single(l => l.Name == "TFR Level");
        level.Films!.Add(new StackupFilm { Layer = Hi, SheetResistanceOhmPerSq = 500 });
        var metal = stack.Single(l => l.Name == "Metal1");
        metal.Films = [new StackupFilm { Layer = Lap, SheetResistanceOhmPerSq = 100 }];
        tech.DeviceRules.Add(new DeviceRule
        {
            Name = "Thin-film resistor Hi", Kind = "Resistor", Body = "41/0", Terminals = ["11/0"],
            Parameters = new() { ["R"] = "TfrSheetResistance * Length / Width" },
        });
        return tech;
    }

    private static Dictionary<string, PCellValue> Part(string? film, int n = 1, double w = 40e-6, double l = 180e-6)
    {
        var p = new Dictionary<string, PCellValue>
        {
            ["N"] = PCellValue.Real(n), ["W"] = PCellValue.Real(w), ["L"] = PCellValue.Real(l), ["Lc"] = PCellValue.Real(10e-6),
        };
        if (film is not null) p["Film"] = PCellValue.Text(film);
        return p;
    }

    private static double Injected(Technology tech, string? film, string name)
    {
        var (overrides, warning) = MmicPassiveInjection.Build(tech, SymbolKind.Tfr, film);
        Assert.Null(warning);
        return double.Parse(overrides.Single(o => o.Name == name).Expression, CultureInfo.InvariantCulture);
    }

    // ── Gate 12: the film decides ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Via50Name, 50.0)]
    [InlineData(HiName, 500.0)]
    [InlineData(LapName, 100.0)]
    [InlineData(null, 100.0)]      // the first film in stackup order is the one on Metal1
    public void TheFilmSetsTheSheetResistance(string? film, double rs) => Assert.Equal(rs, Injected(TwoStyles(), film, "Rs"));

    /// <summary>H and ErSub read under the film's own level: 100 µm against Metal1's 100.2 µm, on this stack.</summary>
    [Fact]
    public void TheSubstrateIsReadUnderTheChosenFilm()
    {
        var tech = TwoStyles();
        Assert.Equal(100e-6, Injected(tech, Via50Name, "H"), 1e-12);
        Assert.Equal(100.2e-6, Injected(tech, LapName, "H"), 1e-12);
    }

    /// <summary>A via film draws vias whose inner edges are L apart, inside the film and inside the heads on the contact metal.</summary>
    [Fact]
    public void AViaFilmDrawsItsContactsAndTheyDefineTheLength()
    {
        var art = TfrPCell.Generate(Part(Via50Name), TwoStyles(), PCellLayerSelection.Default);
        var vias = art.Shapes.OfType<RectShape>().Where(s => s.Layer == Contact).OrderBy(s => s.X1).ToList();
        var film = art.Shapes.OfType<RectShape>().Single(s => s.Layer == Via50);
        var heads = art.Shapes.OfType<RectShape>().Where(s => s.Layer == Metal1).OrderBy(s => s.X1).ToList();
        Assert.Equal(2, vias.Count);
        Assert.Equal(2, heads.Count);
        Assert.Equal(180_000, vias[1].X1 - vias[0].X2);
        foreach (var (via, head) in vias.Zip(heads))
        {
            Assert.True(via.X1 > film.X1 && via.X2 < film.X2 && via.Y1 > film.Y1 && via.Y2 < film.Y2, "the via leaves the film");
            Assert.True(via.X1 > head.X1 && via.X2 < head.X2 && via.Y1 > head.Y1 && via.Y2 < head.Y2, "the via leaves its head");
        }
        Assert.All(art.Pins, p => Assert.Equal(Metal1, p.Layer));
    }

    [Fact]
    public void AnOverlapFilmDrawsNoVia()
    {
        var art = TfrPCell.Generate(Part(LapName), TwoStyles(), PCellLayerSelection.Default);
        Assert.DoesNotContain(art.Shapes, s => s.Layer == Contact);
        Assert.Contains(art.Shapes, s => s.Layer == Lap);
    }

    /// <summary>A serpentine ends the same way: a via at each free end, whichever side an even N leaves pin 2 on.</summary>
    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void ASerpentineOnAViaFilmEndsInItsVias(int n)
    {
        var art = TfrPCell.Generate(Part(HiName, n, 10e-6, 40e-6), TwoStyles(), PCellLayerSelection.Default);
        Assert.Equal(2, art.Shapes.Count(s => s.Layer == Contact));
    }

    // ── Gate 13: a part stating no Film ────────────────────────────────────────────────────────────

    /// <summary>The shipped technology's part reads exactly as it did, and no Film reaches the netlist or the cell's name.</summary>
    [Fact]
    public void NoFilmIsTheFirstFilmAndChangesNothing()
    {
        var tech = ShippedTechnologies.Load("mmic-GaAs_2LM_100um");
        var (none, _) = MmicPassiveInjection.Build(tech, SymbolKind.Tfr);
        var (named, _) = MmicPassiveInjection.Build(tech, SymbolKind.Tfr, MmicStackResolver.FilmNames(tech)[0]);
        Assert.Equal(none.Select(o => (o.Name, o.Expression)), named.Select(o => (o.Name, o.Expression)));

        var bare = TfrPCell.Generate(Part(null), tech, PCellLayerSelection.Default);
        var first = TfrPCell.Generate(Part(MmicStackResolver.FilmNames(tech)[0]), tech, PCellLayerSelection.Default);
        Assert.Equal(bare.Shapes.Count, first.Shapes.Count);
        Assert.DoesNotContain(bare.Shapes, s => s.Layer == Contact);

        Assert.Contains(ComponentTypeRegistry.DefaultParameters(SymbolKind.Tfr, 0), p => p.Name == "Film" && p.Expression.Length == 0);
    }

    // ── Gate 14: a name matching no film ───────────────────────────────────────────────────────────

    [Fact]
    public void AFilmNamingNoFilmIsARefusalListingTheRealOnes()
    {
        var tech = TwoStyles();
        string why = MmicPassiveInjection.FilmRefusal(tech, "Nichrome")!;
        foreach (string word in new[] { "Nichrome", Via50Name, HiName, LapName }) Assert.Contains(word, why);
        Assert.Null(MmicPassiveInjection.Readout(tech, SymbolKind.Tfr, new Dictionary<string, double> { ["W"] = 10e-6, ["L"] = 20e-6 }, out string? note, "Nichrome"));
        Assert.Equal(why, note);
        Assert.Empty(TfrPCell.Generate(Part("Nichrome"), tech, PCellLayerSelection.Default).Shapes);
    }

    [Fact]
    public void ARunRefusesItToo()
    {
        Directory.CreateDirectory(_root);
        string ws = CircuitRF.Design.Workspace.WorkspaceCreate.Create(_root, "w", "mmic-GaAs_2LM_100um").WorkspaceDir;
        string cnl = Path.Combine(ws, "t.cnl");
        File.WriteAllText(cnl, "Port:P1 a 0 Num=1 Z=50 Ohm\nTFR:R1 a 0 W=10um L=20um Film=\"Nichrome\"\n");
        var e = Assert.Throws<InvalidOperationException>(() => CnlTechnologyBinding.ReadFile(cnl));
        Assert.Contains("Nichrome", e.Message);
    }

    /// <summary>A named film on a line binds, and the word does not reach the numeric layer.</summary>
    [Fact]
    public void ANamedFilmOnALineIsConsumed()
    {
        Directory.CreateDirectory(_root);
        string ws = CircuitRF.Design.Workspace.WorkspaceCreate.Create(_root, "w", "mmic-GaAs_2LM_100um").WorkspaceDir;
        string cnl = Path.Combine(ws, "t.cnl");
        string film = MmicStackResolver.FilmNames(ShippedTechnologies.Load("mmic-GaAs_2LM_100um"))[0];
        File.WriteAllText(cnl, $"Port:P1 a 0 Num=1 Z=50 Ohm\nTFR:R1 a 0 W=10um L=20um Film=\"{film}\"\n");
        var (_, tb) = CnlTechnologyBinding.ReadFile(cnl);
        var r1 = tb.Instances.Single(i => i.Reference == "TFR");
        Assert.DoesNotContain(r1.Overrides, o => o.Name == "Film");
        Assert.Contains(r1.Overrides, o => o.Name == "Rs");
    }

    // ── The dialog ────────────────────────────────────────────────────────────────────────────────

    private (EditableComponent Comp, ParameterRowViewModel Row, ParameterEditorViewModel Editor) Dialog(string film)
    {
        Directory.CreateDirectory(Path.Combine(_root, "tech"));
        TechPersistence.SaveToFile(Path.Combine(_root, "tech", "t.ctech"), TwoStyles());
        foreach (string lib in Directory.EnumerateFiles(Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em-tfr", "zigzag-via", "tech"), "*.cmat"))
            File.Copy(lib, Path.Combine(_root, "tech", Path.GetFileName(lib)), true);
        CircuitRF.Design.Workspace.WorkspacePersistence.SaveToFile(Path.Combine(_root, ".cws"),
            new CircuitRF.Design.Workspace.CwsFile { DefaultTechRef = "tech/t.ctech" });
        string dir = Path.Combine(_root, "Amp", "schematic");
        Directory.CreateDirectory(dir);
        var model = new SchematicEditModel { SchematicDirectory = dir };
        // Placed before the parameter existed: it states N, W, L and Lc, and the dialog gives it the row.
        var comp = new EditableComponent { Symbol = SymbolKind.Tfr, InstanceName = "R1" };
        foreach (var d in ComponentTypeRegistry.DefaultParameters(SymbolKind.Tfr, 0).Where(d => d.Name != "Film"))
            comp.Parameters.Add(new EditableParameter { Name = d.Name, Expression = d.Expression, Unit = d.Unit, Dimension = d.Dimension });
        if (film.Length > 0) comp.Parameters.Add(new EditableParameter { Name = "Film", Expression = film });
        model.Components.Add(comp);
        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(new SchematicViewModel(model), comp, showClose: false);
        return (comp, editor.Rows.Single(r => r.Name == "Film"), editor);
    }

    /// <summary>Each row states what the film gives, in stackup order, and choosing one stores its drawing layer's name.</summary>
    [Fact]
    public void TheDialogOffersEveryFilmAndStoresItsLayerName()
    {
        var (comp, row, editor) = Dialog("");
        Assert.True(row.IsLayerChoiceParam);
        Assert.False(row.ShowExpressionTextBox);
        Assert.Equal(["(Default)", "Resistor Lap · 100 Ω/sq · Metal1 (overlap)", "Resistor · 50 Ω/sq · TFR Level (via)", "Resistor Hi · 500 Ω/sq · TFR Level (via)"],
                     row.LayerChoiceOptions);
        Assert.Equal("(Default)", row.SelectedLayerChoice);

        row.SelectedLayerChoice = "Resistor Hi · 500 Ω/sq · TFR Level (via)";
        Assert.Equal("Resistor Hi", comp.Parameters.Single(p => p.Name == "Film").Expression);
        Assert.Equal("Resistor Hi · 500 Ω/sq · TFR Level (via)", row.SelectedLayerChoice);
        Assert.False(row.HasLayerChoiceMissingWarning);
        Assert.Contains("500", editor.MmicReadoutText);
    }

    [Fact]
    public void TheDialogRefusesANameMatchingNoFilm()
    {
        var (_, row, editor) = Dialog("Nichrome");
        Assert.True(row.HasLayerChoiceMissingWarning);
        Assert.Contains("Resistor Hi", row.LayerChoiceMissingWarning);
        Assert.DoesNotContain("falling back", row.LayerChoiceMissingWarning);
        Assert.Contains("Nichrome", row.LayerChoiceOptions);
        Assert.Empty(editor.MmicReadoutText);
    }

    // ── LVS (gates 6 and 12) ───────────────────────────────────────────────────────────────────────

    /// <summary>A cell with the example's one-TFR schematic stating <paramref name="film"/>, on the two-style technology.</summary>
    private string Cell(string? film)
    {
        string example = Path.Combine(PalaceBackendTests.RepoRoot(), "examples", "PDK PCells");
        Directory.CreateDirectory(Path.Combine(_root, "tech"));
        TechPersistence.SaveToFile(Path.Combine(_root, "tech", "mmic-GaAs_2LM_100um.ctech"), TwoStyles());
        foreach (string lib in Directory.EnumerateFiles(Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em-tfr", "zigzag-via", "tech"), "*.cmat"))
            File.Copy(lib, Path.Combine(_root, "tech", Path.GetFileName(lib)));
        CircuitRF.Design.Workspace.WorkspacePersistence.SaveToFile(Path.Combine(_root, ".cws"),
            new CircuitRF.Design.Workspace.CwsFile { DefaultTechRef = "tech/mmic-GaAs_2LM_100um.ctech" });
        string cell = Path.Combine(_root, "ThinFilmResistor");
        foreach (string f in Directory.EnumerateFiles(Path.Combine(example, "ThinFilmResistor"), "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(cell, Path.GetRelativePath(Path.Combine(example, "ThinFilmResistor"), f));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to);
        }
        string csch = Path.Combine(cell, "schematic", "ThinFilmResistor.csch");
        var model = SchematicPersistence.LoadFromFile(csch).model;
        var r1 = model.Components.Single(c => c.InstanceName == "R1");
        void Set(string name, string value)
        {
            if (r1.Parameters.FirstOrDefault(p => p.Name == name) is { } row) row.Expression = value;
            else r1.Parameters.Add(new EditableParameter { Name = name, Expression = value });
        }
        Set("N", "1"); Set("W", "40"); Set("L", "180");
        if (film is not null) Set("Film", film);
        SchematicPersistence.SaveToFile(csch, model);
        return cell;
    }

    private void WriteLayout(string cell, IEnumerable<LayoutShape> shapes)
    {
        var view = new LayoutView { DbuPerMicron = 1000, TechRef = "../../tech/mmic-GaAs_2LM_100um.ctech" };
        view.Shapes.AddRange(shapes);
        LayoutPersistence.SaveToFile(Path.Combine(cell, "layout", "ThinFilmResistor.clay"), view);
    }

    private static RectShape Rect(LayerKey layer, long x1, long y1, long x2, long y2) => new()
    {
        Layer = layer, X1 = x1 * 1000, Y1 = y1 * 1000, X2 = x2 * 1000, Y2 = y2 * 1000,
    };

    private static LabelShape Term(string name, long x) => new()
    {
        Layer = Metal1, X = x * 1000, Y = 0, Text = name, Height = 5000, IsPort = true,
    };

    /// <summary>180 µm of a 40 µm film between the vias' inner edges, with the film running on past them.</summary>
    private static List<LayoutShape> Flat(LayerKey film, bool bothVias = true)
    {
        var art = new List<LayoutShape>
        {
            Rect(Metal1, 0, -25, 100, 25), Rect(Metal1, 220, -25, 320, 25),
            Rect(film, 60, -20, 260, 20),
            Rect(Contact, 60, -15, 70, 15),
            Term("1", 0), Term("2", 320),
        };
        if (bothVias) art.Add(Rect(Contact, 250, -15, 260, 15));
        return art;
    }

    private static readonly LvsRunOptions Recognize = new() { Recognize = true };

    /// <summary>Each film's resistor compares against its own R_s, and the length is between the vias' inner edges.</summary>
    [Theory]
    [InlineData(Via50Name, 225.0)]
    [InlineData(HiName, 2250.0)]
    public void EachDrawnViaFilmPassesAgainstItsOwnSheetResistance(string name, double ohms)
    {
        string cell = Cell(name);
        WriteLayout(cell, Flat(name == HiName ? Hi : Via50));
        var result = LvsRun.Run(cell, Recognize);
        Assert.True(result.IsClean, LvsReportText.Of("t", result));
        var r = Assert.Single(result.Layout.Devices, d => d.Type.Kind == DeviceKind.Resistor);
        Assert.Equal(ohms, Assert.IsType<double>(r.Parameters["R"]), 6);
    }

    /// <summary>Gate 12's other half: the wrong film drawn is a mismatch, not a match on topology.</summary>
    [Fact]
    public void TheWrongFilmDrawnDoesNotPass()
    {
        string cell = Cell(Via50Name);
        WriteLayout(cell, Flat(Hi));
        Assert.False(LvsRun.Run(cell, Recognize).IsClean);
    }

    /// <summary>Gate 6's LVS half: a contact via missing is an open, and said to be, where the planar run reads one too
    /// (<c>FilmLevelTests.AMissingContactViaIsAnOpen</c>).</summary>
    [Fact]
    public void AMissingContactViaIsReportedAsAnOpen()
    {
        string cell = Cell(Via50Name);
        WriteLayout(cell, Flat(Via50, bothVias: false));
        var result = LvsRun.Run(cell, Recognize);
        Assert.False(result.IsClean);
        Assert.Contains(result.Findings, f => f.Id == "lvs.recognize.film-open");
    }

    /// <summary>A placed part: the film the schematic asked for against the layer its cell actually drew.</summary>
    [Theory]
    [InlineData(Via50Name, Via50Name, true)]
    [InlineData(HiName, HiName, true)]
    [InlineData(LapName, LapName, true)]
    [InlineData(Via50Name, HiName, false)]
    public void APlacedPartComparesItsFilmAgainstTheLayerDrawn(string asked, string drawn, bool clean)
    {
        string cell = Cell(asked);
        var tech = TwoStyles();
        string tfr = GeneratedCellStore.GetOrCreate(_root, TfrPCell.GeneratorId, Part(drawn), tech, "tech/mmic-GaAs_2LM_100um.ctech", PCellLayerSelection.Default);
        var view = LayoutPersistence.LoadFromFile(Path.Combine(cell, "layout", "ThinFilmResistor.clay"));
        view.TechRef = "../../tech/mmic-GaAs_2LM_100um.ctech";
        view.PCellSnapshots.Clear();
        view.SchematicPCellSnapshots.Clear();
        var inst = view.Instances.Single();
        view.Instances[0] = new LayoutInstance
        {
            CellRef = "../../.generated-cells/" + Path.GetFileName(tfr), X = 0, Y = 0, SchematicId = inst.SchematicId,
        };
        view.Shapes.Clear();
        var pins = TfrPCell.Generate(Part(drawn), tech, PCellLayerSelection.Default).Pins;
        foreach (var pin in pins)
            view.Shapes.Add(new LabelShape { Layer = pin.Layer, X = pin.X, Y = pin.Y, Text = pin.Name, Height = 5000, IsPort = true });
        LayoutPersistence.SaveToFile(Path.Combine(cell, "layout", "ThinFilmResistor.clay"), view);

        var result = LvsRun.Run(cell);
        var report = LvsReportText.Of("t", result);
        Assert.True(result.IsClean == clean, report);
        if (!clean) Assert.Contains("Film", report);
    }
}
