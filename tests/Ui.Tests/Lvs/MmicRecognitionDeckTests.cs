// ================================================================
//  MmicRecognitionDeckTests.cs — the gate for brief-agent-authoring-overview.md AA-2.
//
//  ── WHAT IS BEING PINNED ──────────────────────────────────────────────────────────────────────
//
//  The shipped GaAs technology carries a recognition deck, so an MMIC drawn as PLAIN ARTWORK — no
//  placed parts, only rectangles on the process's layers, which is what an agent writes and what a
//  flattened GDSII import is — passes `lvs --recognize` against its schematic. And a design whose
//  wiring differs from the artwork's fails, naming what differs.
//
//  The die is built in CODE on the real shipped technology, as MimCapacitorTests builds its
//  fixtures: a copy of the technology here would be a second one that drifts. Every recognised
//  value is checked against hand arithmetic laid out beside the artwork, never against another
//  circuitRF path.
//
//  Fixture paths are anonymized to the SHAPE of a path — a temp folder and invented cell names.
// ================================================================

using System.Linq;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Lvs;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Lvs;

public sealed class MmicRecognitionDeckTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-aa2-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const string GaAs = "mmic-GaAs_2LM_100um";
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static long Um(double v) => (long)Math.Round(v * Dbu);

    private static readonly LayerKey Metal1  = new(1, 0);
    private static readonly LayerKey Metal2  = new(2, 0);
    private static readonly LayerKey Post    = new(3, 0);
    private static readonly LayerKey Res     = new(4, 0);
    private static readonly LayerKey Nitride = new(6, 0);
    private static readonly LayerKey BackVia = new(8, 0);
    private static readonly LayerKey MimMet  = new(9, 0);
    private static readonly LayerKey MimVia  = new(10, 0);
    private static readonly LayerKey LineMk  = new(20, 0);

    // ── The hand arithmetic, once ───────────────────────────────────────────────────────────────
    //
    // MIM: εr 6.8 over 0.2 µm, so C/A = ε0·6.8/0.2e-6 = 301.04 µF/m²; the top plate is 40 µm square.
    // TFR: 50 Ω/□, exposed between its contacts 80 µm long and 20 µm wide — four squares.
    private const double Eps0 = 8.8541878128e-12;
    private const double CapPf = Eps0 * 6.8 / 0.2e-6 * 40e-6 * 40e-6 * 1e12;   // 0.48167 pF
    private const double ROhm = 50.0 * 80.0 / 20.0;                            // 200 Ω

    /// <summary>
    /// The deck is valid as shipped, and the two process numbers its formulas use are DERIVED, not
    /// restated: the capacitance density is the stackup's (εr and thickness of the film the extractor
    /// solves with) and the sheet resistance is the resistor layer's, the ones MIMCAP and TFR read.
    /// </summary>
    [Fact]
    public void TheShippedDeckValidatesAndReadsItsConstantsFromTheProcess()
    {
        var tech = ShippedTechnologies.Load(GaAs);

        Assert.DoesNotContain(TechValidation.Analyze(tech), p => p.Area == TechProblemArea.Drc);
        Assert.Equal(6, tech.DeviceRules.Count);

        var film = tech.Stackup.Layers.Single(l => l.Name == "MIM Dielectric");
        double fromStack = Eps0 * film.Epsr / (film.ThicknessDbu / (double)Dbu * 1e-6);
        var scope = DeviceRecognition.ConstantsOf(tech);
        var eval = new CircuitRF.Core.Expressions.Evaluator();

        Assert.DoesNotContain(tech.Constants, c => c.Name is "MimCapDensity" or "TfrSheetResistance");
        Assert.Equal(fromStack, eval.Eval("MimCapDensity", scope).AsReal(), fromStack * 1e-9);
        Assert.Equal(ResistiveFilms.Of(tech, tech.Layers.Single(l => l.Name == "Resistor").Key)!.SheetResistanceOhmPerSq,
            eval.Eval("TfrSheetResistance", scope).AsReal());
    }

    /// <summary>
    /// <b>The gate.</b> A filter drawn as plain artwork — two marked lines, an open stub, a shunt MIM
    /// capacitor to a backside via, and a series thin-film resistor — passes against its schematic,
    /// with every value read out of the copper.
    /// </summary>
    [Fact]
    public void APlainArtworkFilterPassesLvsAgainstItsSchematic()
    {
        string cell = Die("Filter", Schematic());
        var result = LvsRun.Run(cell, new LvsRunOptions { Recognize = true });
        output.WriteLine(LvsReportText.Of("Filter", result));

        Assert.True(result.IsClean, LvsReportText.Of("Filter", result));
        Assert.Equal(6, result.Layout.Devices.Count);   // 3 lines, C, R, via — and nothing else
        Assert.Contains(result.Findings, f => f.Id == "lvs.recognize.in-use");

        var c = Assert.Single(result.Layout.Devices, d => d.Type.Kind == DeviceKind.Capacitor);
        Assert.Equal(CapPf, Assert.IsType<double>(c.Parameters["C"]) * 1e12, 6);
        var r = Assert.Single(result.Layout.Devices, d => d.Type.Kind == DeviceKind.Resistor);
        Assert.Equal(ROhm, Assert.IsType<double>(r.Parameters["R"]), 6);
    }

    /// <summary>
    /// A schematic whose capacitor hangs off the OTHER line junction fails, and the one finding names
    /// both nets — what the schematic says and what the copper says.
    /// </summary>
    /// <remarks>
    /// <b>The artwork names its parts here</b> (a <c>Component</c> on each body's shapes), and that is
    /// what makes the finding ONE line. Unnamed, every recognised device is matched on structure
    /// alone, and one moved capacitor changes the structure everywhere: the same fault arrives as
    /// twelve unmatched devices — a failure, correctly, but not one anybody can act on.
    /// </remarks>
    [Fact]
    public void SwappingTwoNetsFailsNamingBoth()
    {
        string cell = Die("Swapped", Schematic(capacitorOn: "b"), named: true);
        var result = LvsRun.Run(cell, new LvsRunOptions { Recognize = true });
        output.WriteLine(LvsReportText.Of("Swapped", result));

        var wrong = Assert.Single(result.Findings, f => f.Severity == CircuitRF.Diagnostics.DiagnosticSeverity.Error);
        Assert.Equal("lvs.terminal.wrong-net", wrong.Id);

        string line = wrong.Diagnostic.Render();
        Assert.Contains("C1", line, StringComparison.Ordinal);
        Assert.Contains("'b' in the schematic", line, StringComparison.Ordinal);
        Assert.Contains("'a' in the layout", line, StringComparison.Ordinal);
    }

    // ── Fixtures ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The die, in µm. RF1 pad → TL1 (10 × 300, marked) → junction a → TL2 (10 × 300, marked) →
    /// junction b → R1 (thin film) → RF2 pad. Off a: C1, whose bottom plate IS a's metal and whose
    /// top plate climbs through the MIM via, Metal2 and a post to a pad on a backside via. Off b: TL3,
    /// an open stub 10 × 200, marked to its open end.
    /// </summary>
    private string Die(string name, SchematicEditModel schematic, bool named = false)
    {
        string ws = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(ws, "tech"));
        TechPersistence.SaveToFile(Path.Combine(ws, "tech", "die.ctech"), ShippedTechnologies.Load(GaAs));
        WorkspacePersistence.SaveToFile(
            Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = Path.Combine("tech", "die.ctech") });

        string cellDir = CellFolder.CreateCellFolder(ws, name);
        var view = new LayoutView { DbuPerMicron = Dbu, TechRef = Path.Combine("..", "..", "tech", "die.ctech") };
        var s = view.Shapes;
        string? N(string part) => named ? part : null;

        s.Add(Rect(Metal1, -60, -20, 0, 20));                                   // RF1 pad
        s.Add(Rect(Metal1, 0, -5, 300, 5, part: N("TL1")));   s.Add(Rect(LineMk, 0, -10, 300, 10));    // TL1
        s.Add(Rect(Metal1, 300, -20, 340, 20, net: "a"));                       // junction a
        s.Add(Rect(Metal1, 340, -5, 640, 5, part: N("TL2"))); s.Add(Rect(LineMk, 340, -10, 640, 10));  // TL2
        s.Add(Rect(Metal1, 640, -20, 700, 20, net: "b"));                       // junction b + R1's contact
        s.Add(Rect(Res, 690, -10, 790, 10, part: N("R1")));                     // R1: 80 µm exposed
        s.Add(Rect(Metal1, 780, -20, 840, 20));                                 // R1's contact + RF2 pad

        // C1 off a: bottom plate continuous with a, 40 µm square top plate, up and over to the via.
        s.Add(Rect(Metal1, 300, 20, 360, 80));
        s.Add(Rect(Nitride, 310, 30, 350, 70));
        s.Add(Rect(MimMet, 310, 30, 350, 70, part: N("C1")));
        s.Add(Rect(MimVia, 320, 40, 340, 60));
        s.Add(Rect(Metal2, 320, 40, 340, 160));
        s.Add(Rect(Post, 320, 140, 340, 160));
        s.Add(Rect(Metal1, 300, 130, 360, 190));                               // via pad
        s.Add(Rect(BackVia, 315, 145, 345, 175, part: N("V1")));

        // TL3 off b, open at its far end — marked all the way, so nothing but b touches it.
        s.Add(Rect(Metal1, 655, 20, 665, 220, part: N("TL3"))); s.Add(Rect(LineMk, 650, 20, 670, 220));

        view.Pins.Add(new LayoutPin { Name = "RF1", X = Um(-50), Y = 0, WidthDbu = Um(40), Layer = Metal1 });
        view.Pins.Add(new LayoutPin { Name = "RF2", X = Um(830), Y = 0, WidthDbu = Um(40), Layer = Metal1 });

        LayoutPersistence.SaveToFile(
            Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), name + ".clay"), view);
        SchematicPersistence.SaveToFile(
            Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Schematic), name + ".csch"), schematic, name);

        CellPersistence.SaveToFile(Path.Combine(cellDir, CellFolder.CcellFileName), new CcellFile
        {
            NumPorts = 2,
            Terminals =
            [
                new CcellTerminal { Port = 1, Name = "RF1", LayoutPin = ["RF1"] },
                new CcellTerminal { Port = 2, Name = "RF2", LayoutPin = ["RF2"] },
            ],
        });
        return cellDir;
    }

    /// <summary>The same circuit as a schematic, every pin named by a net label sitting on it.</summary>
    private static SchematicEditModel Schematic(string capacitorOn = "a", bool swapPorts = false)
    {
        var model = new SchematicEditModel();
        int slot = 0;

        void Add(string inst, SymbolKind kind, string[] nets, params (string Name, string Expr, string Unit)[] ps)
        {
            var comp = new EditableComponent { InstanceName = inst, Symbol = kind, X = 1000 * slot++, Y = 0 };
            foreach (var (n, e, u) in ps) comp.Parameters.Add(new EditableParameter { Name = n, Expression = e, Unit = u });
            model.Components.Add(comp);

            var pins = SymbolPortDefs.For(kind);
            for (int i = 0; i < nets.Length && i < pins.Length; i++)
                if (nets[i] is { Length: > 0 } net)
                    model.NetLabels.Add(new EditableNetLabel { X = comp.X + pins[i].LocalX, Y = comp.Y + pins[i].LocalY, Name = net });
        }

        Add("RF1", SymbolKind.Pin, ["rf1"], ("Num", "1", ""), ("Name", "RF1", ""));
        Add("RF2", SymbolKind.Pin, ["rf2"], ("Num", "2", ""), ("Name", "RF2", ""));
        Add("TL1", SymbolKind.Mlin, [swapPorts ? "rf2" : "rf1", "a"], ("W", "10", "um"), ("L", "300", "um"));
        Add("TL2", SymbolKind.Mlin, ["a", "b"], ("W", "10", "um"), ("L", "300", "um"));
        Add("TL3", SymbolKind.Mlin, ["b", ""], ("W", "10", "um"), ("L", "200", "um"));
        Add("C1", SymbolKind.Capacitor, [capacitorOn, "g1"], ("C", CapPf.ToString("R", System.Globalization.CultureInfo.InvariantCulture), "pF"));
        Add("V1", SymbolKind.ViaGnd, ["g1"]);
        Add("R1", SymbolKind.Resistor, ["b", swapPorts ? "rf1" : "rf2"], ("R", "200", "Ohm"));
        return model;
    }

    private static RectShape Rect(
        LayerKey layer, double x1, double y1, double x2, double y2, string? net = null, string? part = null) =>
        new() { Layer = layer, X1 = Um(x1), Y1 = Um(y1), X2 = Um(x2), Y2 = Um(y2), Net = net, Component = part };
}
