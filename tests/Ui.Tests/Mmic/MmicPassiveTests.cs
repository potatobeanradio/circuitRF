// ================================================================
//  MmicPassiveTests.cs — the four MMIC passives (brief-agent-authoring-overview.md AA-1).
//
//  One test per claim: each closed form against the independent formula it claims to be, the
//  technology binding (schematic and hand-written .cnl alike), the artwork agreeing with the model's
//  geometry and terminals, and the EM extraction's refusal for a part the solve cannot see. The
//  spiral's estimate against a planar EM extraction of the same drawn coil is a Benchmark — its
//  measured numbers are recorded in src/Design/RESOLVED.md (AA-1).
// ================================================================

using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Mmic;

public sealed class MmicPassiveTests(ITestOutputHelper output) : IDisposable
{
    private const string GaAs = "mmic-GaAs_2LM_100um";
    private const double Eps0 = 8.8541878128e-12;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-mmic-" + Guid.NewGuid().ToString("N")[..12]);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    /// <summary>The model the run would build for <paramref name="kind"/> on the shipped GaAs process,
    /// with <paramref name="geometry"/> in SI.</summary>
    private static T Model<T>(SymbolKind kind, params (string Name, double Value)[] geometry) where T : class
    {
        var (overrides, warning) = MmicPassiveInjection.Build(ShippedTechnologies.Load(GaAs), kind);
        Assert.Null(warning);
        var values = overrides.ToDictionary(o => o.Name, o => new Value(double.Parse(o.Expression, CultureInfo.InvariantCulture)));
        foreach (var (name, value) in geometry) values[name] = new Value(value);
        return Assert.IsType<T>(ComponentModelFactory.TryCreate(ComponentTypeRegistry.EngineReference(kind), values));
    }

    // ── the closed forms ───────────────────────────────────────────────────────────────────────

    /// <summary>MIMCAP's C is the parallel-plate capacitance of the shipped film with Palmer's edge
    /// fringing, written out here independently — and it is what the model STAMPS, read back off its
    /// admittance at 1 MHz.</summary>
    [Fact]
    public void Mimcap_IsTheParallelPlatePlusFringeFormula_AndThatIsWhatItStamps()
    {
        double w = 60e-6, l = 40e-6, t = 0.2e-6, er = 6.8;      // the shipped film: 0.2 µm of εr 6.8
        double expected = Eps0 * er * w * l / t
                        * (1 + t / (Math.PI * w) * (1 + Math.Log(2 * Math.PI * w / t)))
                        * (1 + t / (Math.PI * l) * (1 + Math.Log(2 * Math.PI * l / t)));

        var m = Model<MimCapModel>(SymbolKind.MimCap, ("W", w), ("L", l));
        Assert.Equal(expected, m.Capacitance, expected * 1e-12);

        double omega = 2 * Math.PI * 1e6;
        var (_, y12, _) = m.Admittance(omega);
        Assert.Equal(expected, -y12.Imaginary / omega, expected * 1e-6);
        output.WriteLine($"C = {m.Capacitance * 1e12:F4} pF (parallel plate {Eps0 * er * w * l / t * 1e12:F4} pF)");
    }

    /// <summary>TFR is the technology's sheet resistance times L/W squares, at DC.</summary>
    [Fact]
    public void Tfr_IsSheetResistanceTimesSquares()
    {
        var m = Model<ThinFilmResistorModel>(SymbolKind.Tfr, ("W", 10e-6), ("L", 25e-6));
        var (y11, _, _) = m.Admittance(0);
        Assert.Equal(50.0 * 25 / 10, 1 / y11.Real, 1e-9);     // the shipped film is 50 Ω/sq
    }

    /// <summary>The properties panels' readout is the model's own value, from the instance's µm rows on
    /// the technology: a TFR's R as squares × sheet resistance, a MIMCAP's C as the model computes it.</summary>
    [Fact]
    public void Readout_IsTheModelsValue_FromTheMicronRows()
    {
        var tech = ShippedTechnologies.Load(GaAs);
        static EditableParameter Um(string name, string value) => new() { Name = name, Expression = value, Unit = "µm" };

        Assert.Equal("≈ 125 Ω (2.5 sq × 50 Ω/sq)",
            MmicPassiveInjection.Readout(tech, SymbolKind.Tfr, [Um("W", "10"), Um("L", "25")], out var tfrNote));
        Assert.Null(tfrNote);

        var c = Model<MimCapModel>(SymbolKind.MimCap, ("W", 60e-6), ("L", 40e-6)).Capacitance;
        Assert.Equal("≈ " + (c * 1e12).ToString("0.###", CultureInfo.InvariantCulture) + " pF",
            MmicPassiveInjection.Readout(tech, SymbolKind.MimCap, [Um("W", "60"), Um("L", "40")], out _));
    }

    /// <summary>OSPIRAL's free-space comparison value is the modified Wheeler estimate with the paper's
    /// OCTAGONAL coefficients (K1 2.25, K2 3.55), written out here independently.</summary>
    [Fact]
    public void OctagonalSpiral_ComparesAgainstTheOctagonalWheelerFormula()
    {
        double n = 2.5, w = 10e-6, s = 10e-6, din = 100e-6;
        double dout = din + 2 * n * w + 2 * (n - 1) * s, davg = (dout + din) / 2, rho = (dout - din) / (dout + din);
        double expected = 2.25 * 4e-7 * Math.PI * n * n * davg / (1 + 3.55 * rho);

        var oct = Model<SpiralInductorModel>(SymbolKind.OctSpiral, ("N", n), ("W", w), ("S", s), ("Din", din));
        Assert.True(oct.Octagonal);
        Assert.Equal(expected, oct.WheelerInductance, expected * 1e-6);
        var sq = Model<SpiralInductorModel>(SymbolKind.Spiral, ("N", n), ("W", w), ("S", s), ("Din", din));
        output.WriteLine($"on GaAs: OSPIRAL L = {oct.Inductance * 1e9:F3} nH (Wheeler {oct.WheelerInductance * 1e9:F3}), "
            + $"SPIRAL L = {sq.Inductance * 1e9:F3} nH (Wheeler {sq.WheelerInductance * 1e9:F3})");
    }

    /// <summary>The inductance is summed over the coil the layout draws: the shared walk, its escape and
    /// its pad (SpiralWalk.Path), run in DBU, puts terminal 2 exactly where each generator's pin 2 is
    /// relative to its pin 1.</summary>
    [Theory]
    [InlineData(false, 2.5)]
    [InlineData(false, 3.25)]
    [InlineData(true, 2.5)]
    [InlineData(true, 2.875)]
    public void TheSummedPath_IsTheDrawnCoil(bool octagonal, double turns)
    {
        var tech = ShippedTechnologies.Load(GaAs);
        var p = new Dictionary<string, PCellValue>
        {
            ["N"] = PCellValue.Real(turns), ["W"] = PCellValue.Real(10e-6), ["S"] = PCellValue.Real(10e-6), ["Din"] = PCellValue.Real(100e-6),
        };
        var art = octagonal ? OctSpiralPCell.Generate(p, tech, PCellLayerSelection.Default)
                            : SpiralPCell.Generate(p, tech, PCellLayerSelection.Default);
        var pin1 = art.Pins.Single(q => q.Name == "1");
        var pin2 = art.Pins.Single(q => q.Name == "2");

        var path = SpiralWalk.Path(turns, 10_000, 10_000, 100_000, bridgeHeight: 0, octagonal);   // DBU
        var outer = path[0];
        double len = outer.Length, ux = (outer.X1 - outer.X2) / len, uy = (outer.Y1 - outer.Y2) / len;
        double p1x = outer.X1 + ux * 5_000, p1y = outer.Y1 + uy * 5_000;                         // half a width past
        var end = path[^1];
        Assert.Equal(pin2.X - pin1.X, end.X2 - p1x, 1.0);
        Assert.Equal(pin2.Y - pin1.Y, end.Y2 - p1y, 1.0);
    }

    /// <summary>AIRBRIDGE is a four-port: the bridge from 1 to 2, the crossed line from 3 to 4, and the two
    /// coupled only through the overlap capacitance — so at low frequency driving 1 pushes jωC through the
    /// bridge's midpoint (at half the drive) into the crossed segment's midpoint, which splits it between
    /// 3 and 4: Y31 = −jωC/4. Nothing reaches ground, so every row sums to zero, and the drawn segment
    /// between pins 3 and 4 is the model's two bridge widths.</summary>
    [Fact]
    public void Airbridge_IsAFourPort_CoupledThroughTheOverlap()
    {
        var m = Model<AirbridgeModel>(SymbolKind.Airbridge, ("L", 30e-6), ("W", 10e-6), ("Wu", 10e-6));
        Assert.Equal(4, m.PortCount);

        double omega = 2 * Math.PI * 1e6;
        var y = m.Admittance(omega);
        for (int i = 0; i < 4; i++)
        {
            Complex row = 0;
            for (int j = 0; j < 4; j++)
            {
                row += y[i, j];
                Assert.True((y[i, j] - y[j, i]).Magnitude <= 1e-9 * y[i, i].Magnitude, $"Y{i + 1}{j + 1} != Y{j + 1}{i + 1}");
            }
            Assert.True(row.Magnitude < 1e-9 * y[i, i].Magnitude, $"row {i + 1} sums to {row}");
        }
        double expected = -omega * m.CrossingCapacitance / 4;
        Assert.Equal(expected, y[2, 0].Imaginary, Math.Abs(expected) * 1e-3);

        var art = AirbridgePCell.Generate(new Dictionary<string, PCellValue> { ["W"] = PCellValue.Real(10e-6) },
            ShippedTechnologies.Load(GaAs), PCellLayerSelection.Default);
        var p3 = art.Pins.Single(p => p.Name == "3");
        var p4 = art.Pins.Single(p => p.Name == "4");
        Assert.Equal(m.CrossedLength * 1e9, p4.Y - p3.Y, 1.0);   // DBU are nanometres
    }

    // ── the technology binding ─────────────────────────────────────────────────────────────────

    /// <summary>A hand-written .cnl inside a GaAs workspace takes the process from the technology,
    /// says so, and states it in the line; inside a board workspace with no MIM film it says which
    /// piece is missing rather than simulating a guess silently.</summary>
    [Fact]
    public void ACnlLine_TakesTheProcessFromTheWorkspace_OrSaysWhatIsMissing()
    {
        Directory.CreateDirectory(_root);
        string Cnl(string tech)
        {
            string ws = WorkspaceCreate.Create(_root, tech, tech).WorkspaceDir;
            string dir = Path.Combine(ws, "cell");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "t.cnl");
            File.WriteAllText(path, "Port:P1 a 0 Num=1 Z=50 Ohm\nMIMCAP:C1 a 0 W=50um L=50um\n");
            return path;
        }

        var (_, gaas) = CnlTechnologyBinding.ReadFile(Cnl(GaAs));
        var c1 = gaas.Instances.Single(i => i.Reference == "MIMCAP");
        Assert.Equal("6.8", c1.Overrides.Single(o => o.Name == "Er").Expression);
        Assert.Equal(2e-7, double.Parse(c1.Overrides.Single(o => o.Name == "Td").Expression, CultureInfo.InvariantCulture), 1e-15);
        Assert.Contains(gaas.ReadNotes, n => n.Contains("MIMCAP:C1") && n.Contains(GaAs));

        var (_, board) = CnlTechnologyBinding.ReadFile(Cnl(ShippedTechnologies.DefaultId));
        Assert.Contains(board.ReadWarnings, w => w.Contains("MIMCAP:C1") && w.Contains("no capacitor dielectric"));
    }

    // ── the artwork ────────────────────────────────────────────────────────────────────────────

    /// <summary>Every MMIC generator's pins are the schematic symbol's terminals, in order, all on the
    /// base metal (Metal1) where a line or an EM port can land on them; and on the shipped process
    /// nothing is drawn on a fallback layer.</summary>
    [Fact]
    public void EveryMmicGenerator_PinsAreTheSchematicTerminals_OnTheBaseMetal()
    {
        var tech = ShippedTechnologies.Load(GaAs);
        foreach (var kind in new[] { SymbolKind.MimCap, SymbolKind.Spiral, SymbolKind.OctSpiral, SymbolKind.Tfr, SymbolKind.Airbridge })
        {
            Assert.True(PCellRegistry.TryGet(ComponentTypeRegistry.EngineReference(kind), out var gen), kind.ToString());
            var art = gen(new Dictionary<string, PCellValue>(), tech, PCellLayerSelection.Default);
            Assert.Equal(SymbolPortDefs.For(kind).Select(p => p.Name), art.Pins.Select(p => p.Name));
            Assert.All(art.Pins, p => Assert.Equal(new LayerKey(1, 0), p.Layer));
            Assert.True(art.Diagnostics is null or { Count: 0 }, $"{kind}: {string.Join("; ", art.Diagnostics ?? [])}");
        }
    }

    /// <summary>The drawn part is the modelled part: MIMCAP's top plate is exactly W×L on the plate
    /// metal and its mask encloses it; TFR's contacts are exactly L apart over a film exactly W wide;
    /// SPIRAL's escape bridge crosses exactly the turns the model's crossing capacitance counts.</summary>
    [Fact]
    public void TheDrawnPart_IsTheModelledGeometry()
    {
        var tech = ShippedTechnologies.Load(GaAs);
        static long Um(double um) => (long)Math.Round(um * 1000);
        Dictionary<string, PCellValue> P(params (string, double)[] kv) => kv.ToDictionary(x => x.Item1, x => PCellValue.Real(x.Item2));

        var mim = MimCapPCell.Generate(P(("W", 60e-6), ("L", 40e-6)), tech, PCellLayerSelection.Default);
        var top = mim.Shapes.OfType<RectShape>().Single(r => r.Layer == new LayerKey(9, 0));
        Assert.Equal((Um(40), Um(60)), (top.X2 - top.X1, top.Y2 - top.Y1));
        var mask = mim.Shapes.OfType<RectShape>().Single(r => r.Layer == new LayerKey(6, 0));
        Assert.True(mask.X1 < top.X1 && mask.X2 > top.X2 && mask.Y1 < top.Y1 && mask.Y2 > top.Y2);

        var tfr = TfrPCell.Generate(P(("W", 10e-6), ("L", 25e-6)), tech, PCellLayerSelection.Default);
        var contacts = tfr.Shapes.OfType<RectShape>().Where(r => r.Layer == new LayerKey(1, 0)).OrderBy(r => r.X1).ToList();
        var film = tfr.Shapes.OfType<RectShape>().Single(r => r.Layer == new LayerKey(4, 0));
        Assert.Equal(Um(25), contacts[1].X1 - contacts[0].X2);
        Assert.Equal(Um(10), film.Y2 - film.Y1);

        // The escape is the one Metal2 shape; every coil side it passes over is a crossing.
        foreach (double turns in new[] { 1.0, 2.5, 3.25 })
        {
            var spiral = SpiralPCell.Generate(P(("N", turns), ("W", 10e-6), ("S", 10e-6), ("Din", 100e-6)), tech, PCellLayerSelection.Default);
            var bridge = spiral.Shapes.OfType<RectShape>().Single(r => r.Layer == new LayerKey(2, 0));
            var coil = spiral.Shapes.OfType<PolygonShape>().Single();
            Assert.Equal(SpiralInductorModel.Crossings(turns), CrossingsUnder(coil, bridge));
        }

        // OSPIRAL's escape leaves the middle of the innermost flat and crosses each later lap's flat.
        foreach (double turns in new[] { 1.0, 2.5, 2.875, 3.125 })
        {
            var oct = OctSpiralPCell.Generate(P(("N", turns), ("W", 10e-6), ("S", 10e-6), ("Din", 100e-6)), tech, PCellLayerSelection.Default);
            var bridge = oct.Shapes.OfType<RectShape>().Single(r => r.Layer == new LayerKey(2, 0));
            var coil = oct.Shapes.OfType<PolygonShape>().Single();
            Assert.Equal(SpiralInductorModel.OctagonalCrossings(turns), CrossingsUnder(coil, bridge));
        }
    }

    /// <summary>How many separate runs of the coil polygon cross the bridge's vertical centreline
    /// strictly between the bridge's two posts.</summary>
    private static int CrossingsUnder(PolygonShape coil, RectShape bridge)
    {
        long x = (bridge.X1 + bridge.X2) / 2;
        long postTop = bridge.Y2 - (bridge.X2 - bridge.X1), postBottom = bridge.Y1 + (bridge.X2 - bridge.X1);
        int crossings = 0, n = coil.Xy.Length / 2;
        for (int i = 0; i < n; i++)
        {
            long x1 = coil.Xy[2 * i], y1 = coil.Xy[2 * i + 1];
            long x2 = coil.Xy[2 * ((i + 1) % n)], y2 = coil.Xy[2 * ((i + 1) % n) + 1];
            if (y1 != y2 || Math.Min(x1, x2) > x || Math.Max(x1, x2) < x) continue;
            if (y1 > postBottom && y1 < postTop) crossings++;
        }
        return crossings / 2;            // each crossed run has an upper and a lower edge
    }

    // ── the EM extraction ──────────────────────────────────────────────────────────────────────

    /// <summary>A film that is not bound to a conductor level is a mask a planar solve cannot see, so it would
    /// meet two unconnected contacts: the extraction refuses it by name rather than returning a converged answer
    /// about a different part.</summary>
    [Fact]
    public void ExtractingATfrWhoseFilmIsUnbound_IsRefused_NamingTheFilm()
    {
        var tech = ShippedTechnologies.Load(GaAs);
        tech.Stackup.Layers.First(l => l.Name == "Metal1").DrawingLayers.Remove(new LayerKey(4, 0));
        var prepared = ComponentEmExtraction.Prepare("TFR", new Dictionary<string, PCellValue>(), tech,
            new FrequencySpec("1e9", "2e9", "1e9"), Path.Combine(_root, "r.clay"), out string? refusal);
        Assert.Null(prepared);
        Assert.Contains("'Resistor'", refusal);
    }

    /// <summary>
    /// <b>EM-TFR gate 3:</b> the TFR PCell's artwork EM-solved against the TFR circuit model (R between two halves of the
    /// film's parallel-plate capacitance), judged by |ΔS| at a low frequency. The drawn part adds its contacts, which is
    /// the difference the tolerance holds.
    /// </summary>
    [Fact]
    public void TfrArtwork_AgreesWithTheCircuitModel_AtLowGHz()
    {
        var prepared = ComponentEmExtraction.Prepare("TFR", new Dictionary<string, PCellValue>(), ShippedTechnologies.Load(GaAs),
            new FrequencySpec("2e9", "2e9", "1e9"), Path.Combine(_root, "r.clay"), out string? refusal);
        Assert.True(prepared is not null, refusal);
        var run = EmRunService.Run(prepared!.Setup, prepared.Source, Path.Combine(_root, "results"));
        Assert.True(run.Status == EmRunStatus.Ok, run.Error);

        var s = run.Data!.Cubes["S"];
        Complex Em(int i, int j) => s.ComplexValues[i * 2 + j];

        var model = Model<ThinFilmResistorModel>(SymbolKind.Tfr);
        var (y11, y12, y22) = model.Admittance(2 * Math.PI * 2e9);
        Complex d = (1 + 50 * y11) * (1 + 50 * y22) - 2500 * y12 * y12;
        Complex s21 = -2 * 50 * y12 / d, s11 = ((1 - 50 * y11) * (1 + 50 * y22) + 2500 * y12 * y12) / d;
        output.WriteLine($"S21 EM {Em(1, 0)} model {s21}; S11 EM {Em(0, 0)} model {s11}");
        // Measured 1.5e-3 and 8e-4 at 2 GHz.
        Assert.InRange((Em(1, 0) - s21).Magnitude, 0, 0.005);
        Assert.InRange((Em(0, 0) - s11).Magnitude, 0, 0.005);
    }

    /// <summary>
    /// <b>The gate the brief sets for SPIRAL</b>: the modified Wheeler estimate against a planar EM
    /// extraction of the same drawn coil on the shipped GaAs stack, through the extraction
    /// <c>circuitrf em --component</c> runs, read at 6 GHz — above the extraction's low-frequency
    /// rise and well below the coil's resonance (src/Design/RESOLVED.md, AA-1). The estimate is a
    /// formula for the coil alone; the drawn part adds its escape and leads. The tolerance holds what
    /// the component's catalogue entry says the estimate is worth.
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void Spiral_EstimateAgainstAPlanarEmExtractionOfTheDrawnCoil()
    {
        var tech = ShippedTechnologies.Load(GaAs);
        var geometry = new Dictionary<string, PCellValue>
        {
            ["N"] = PCellValue.Real(2.5), ["W"] = PCellValue.Real(10e-6),
            ["S"] = PCellValue.Real(10e-6), ["Din"] = PCellValue.Real(100e-6),
        };
        var prepared = ComponentEmExtraction.Prepare("SPIRAL", geometry, tech, new FrequencySpec("6e9", "6e9", "1e9"),
                                                     Path.Combine(_root, "spiral.clay"), out string? refusal);
        Assert.True(prepared is not null, refusal);
        var run = EmRunService.Run(prepared!.Setup, prepared.Source, Path.Combine(_root, "results"));
        Assert.True(run.Status == EmRunStatus.Ok, run.Error);

        var s = run.Data!.Cubes["S"];
        double f = s.Axes[0].Values[0], omega = 2 * Math.PI * f;
        Complex S(int i, int j) => s.ComplexValues[(0 * 2 + i) * 2 + j];
        Complex d = (1 + S(0, 0)) * (1 + S(1, 1)) - S(0, 1) * S(1, 0);
        Complex y21 = -2 * S(1, 0) / d / 50.0;
        double lEm = (-1 / y21).Imaginary / omega;

        var model = Model<SpiralInductorModel>(SymbolKind.Spiral, ("N", 2.5), ("W", 10e-6), ("S", 10e-6), ("Din", 100e-6));
        double ratio = model.Inductance / lEm;
        output.WriteLine($"at {f / 1e9:G3} GHz: EM {lEm * 1e9:F3} nH, modified Wheeler {model.Inductance * 1e9:F3} nH, ratio {ratio:F3}");
        // Measured 0.84 (1.44 nH against 1.72 nH). The window is the catalogue entry's claim: an
        // estimate that reads up to a quarter low on this process, never high.
        Assert.InRange(ratio, 0.75, 1.0);
    }
}
