// ================================================================
//  SeriesChainTests.cs — brief-railrf-35-series-parts-from-the-window.md §4, §5
//
//  Series parts from the window, a library model for them, and more than one per rail. The first
//  test is the one that matters if the rest pass and it fails: a CLOSED-FORM oracle for a two-element
//  chain, written out by hand with nothing from railRF in it — every other test here would pass as
//  happily on a rail whose sections were quietly merged.
//
//  Brief 25's own fixtures (the board helpers, the capacitor library rows) are SeriesElementTests',
//  reused rather than copied.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Clipper2Lib;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CircuitRF.Ui.RailRf;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;
using static CircuitRF.Ui.Tests.RailRf.SeriesElementTests;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class SeriesChainTests(ITestOutputHelper output)
{
    // ══ GATE 1 — THE CLOSED-FORM ORACLE ══════════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 1.</b> Source R-L → series R1 → a section with C1 → series R2 → a section with C2 and
    /// the load. An observation port in each of the three sections; each matches a closed form
    /// written out by hand, and no two of them read the same curve.
    /// </summary>
    /// <remarks>
    /// With Zs the source, Zc1/Zc2 the two capacitors (ESR + package L + mounting L − 1/ωC) and n0,
    /// n1, n2 the three sections:
    /// <c>Z(n0) = Zs ∥ (R1 + Zc1 ∥ (R2 + Zc2))</c>, <c>Z(n1) = Zc1 ∥ (R1 + Zs) ∥ (R2 + Zc2)</c>,
    /// <c>Z(n2) = Zc2 ∥ (R2 + Zc1 ∥ (R1 + Zs))</c>. The typed route builds the chain from ROW order
    /// and places C1 and the middle port by naming the element they sit behind (R-rail35-3c).
    /// </remarks>
    [Fact]
    public void Gate1_ThreePortsOfATwoElementChain_AgainstAClosedForm()
    {
        const double r1 = 0.5, r2 = 1.0, rs = 1.0, ls = 1e-6;

        var rail = new RailSpec { Name = "VDD_IO", Band = new RailBand(1e3, 2e8, 301, true) };
        rail.Sources.Add(new RailSource { Anchor = new RailPortAnchor { Refdes = "BT1", Pin = "1" },
                                          OpenCircuitVoltageV = 3.6, SeriesResistanceOhms = rs,
                                          SeriesInductanceHenries = ls });
        rail.Loads.Add(new RailLoad { Anchor = new RailPortAnchor { Refdes = "TP0" }, Side = RailSection.Upstream });
        rail.Loads.Add(new RailLoad { Anchor = new RailPortAnchor { Refdes = "TP1" }, Behind = "R1" });
        rail.Loads.Add(new RailLoad { Anchor = new RailPortAnchor { Refdes = "U1", Pin = "VDD" } });
        rail.Parts.Add(Series("R1", r1));
        rail.Parts.Add(new RailPart { Refdes = "C1", PartNumber = "PN-1U", MountingInductanceHenries = MountingH, Behind = "R1" });
        rail.Parts.Add(Series("R2", r2));
        rail.Parts.Add(new RailPart { Refdes = "C2", PartNumber = "PN-100N", MountingInductanceHenries = MountingH });

        Assert.Null(rail.Refusal());

        var partition = RailSeriesPartition.Typed(rail);
        Assert.Equal(3, partition.SectionCount);

        var result = PdnSweep.Run(new PdnSweepRequest
        {
            Rail = rail,
            Parts = new RailPartResolver(Library()).ResolveAll(rail.Parts, 3.6),
            Sources = [new RailSourceModel(0, "BT1", RailSourceBasis.Rl, rs, ls, 3.6)],
            Series = [.. rail.SeriesElements.Select(e => RailSeriesModel.Of(e)!)],
            Partition = partition,
            RankRemovals = false,
        });

        Assert.Null(result.Refusal);
        Assert.Equal(3, result.Ports.Count);

        double worst = 0, leastRatio = double.MaxValue;
        for (int i = 0; i < result.FrequenciesHz.Length; i++)
        {
            double w = 2 * Math.PI * result.FrequenciesHz[i];
            var zs = new Complex(rs, w * ls);
            var zc1 = Cap(w, 1e-6, 5.31e6, 8e-3);
            var zc2 = Cap(w, 100e-9, 16.0e6, 5e-3);

            double[] oracle =
            [
                P(zs, r1 + P(zc1, r2 + zc2)).Magnitude,
                P(P(zc1, r1 + zs), r2 + zc2).Magnitude,
                P(zc2, r2 + P(zc1, r1 + zs)).Magnitude,
            ];

            for (int k = 0; k < 3; k++)
                worst = Math.Max(worst, Math.Abs(result.Ports[k].MagnitudeOhms[i] - oracle[k]) / oracle[k]);

            for (int a = 0; a < 3; a++)
                for (int b = a + 1; b < 3; b++)
                {
                    double ratio = result.Ports[a].MagnitudeOhms[i] / result.Ports[b].MagnitudeOhms[i];
                    leastRatio = Math.Min(leastRatio, Math.Max(ratio, 1 / ratio));
                }
        }

        output.WriteLine($"worst relative error {worst:0.###e+00}; closest two ports ever come: ×{leastRatio:0.####}");
        Assert.True(worst < 1e-9, $"a port is off the closed form by {worst:0.###e+00}");
        Assert.True(leastRatio > 1.0001, $"two ports coincide somewhere (ratio {leastRatio})");

        static Complex Cap(double w, double c, double f0, double esr) =>
            new(esr, w * (1.0 / (Math.Pow(2 * Math.PI * f0, 2) * c) + MountingH) - 1.0 / (w * c));

        static Complex P(Complex a, Complex b) => a * b / (a + b);
    }

    // ══ GATE 3 — THE DROP THROUGH A CHAIN IS THE HAND SUM ════════════════════════════════════

    /// <summary>
    /// <b>Gate 3.</b> Off the artwork, two DCRs in a chain are two ranked rows of the breakdown, each
    /// carrying the load current, and the drop to the load is the sum of every row — the source, the
    /// copper and each DCR.
    /// </summary>
    [Fact]
    public void Gate3_TheDcDropThroughATwoElementChain_IsTheHandSum()
    {
        const double load = 0.35, dcr1 = 0.100, dcr2 = 0.200;

        var run = RailDcRun.Run(ChainDcRequest(ChainRail(dcr1, dcr2), ChainShapes()));
        Assert.Null(run.Refusal);

        var rail = run.Rails[0];
        var rows = rail.Breakdown;
        output.WriteLine(string.Join("\n", rows.Select(r => $"{r.Label}: {r.ResistanceOhms * 1e3:0.###} mΩ, {r.DropV * 1e3:0.####} mV")));

        var e1 = Assert.Single(rows, r => r.Label.Contains("R1", StringComparison.Ordinal));
        var e2 = Assert.Single(rows, r => r.Label.Contains("R2", StringComparison.Ordinal));
        Assert.Equal(load * dcr1, e1.DropV, 1e-9);
        Assert.Equal(load * dcr2, e2.DropV, 1e-9);

        var port = Assert.Single(rail.Ports, p => p.CurrentA is not null);
        Assert.Equal(rows.Sum(r => r.DropV), port.DropV!.Value, 1e-9);

        // Three sections on the map, not two (R-rail35-3d).
        Assert.Equal(3, rail.Sections.Values.Distinct().Count());
    }

    // ══ §5.4 / §5.5 — THE PARTITION, AND WHAT IT REFUSES ═════════════════════════════════════

    /// <summary>
    /// <b>§5.4 and §5.5, on one board and two variants of it.</b> Two elements in a chain cut the
    /// rail into three sections with each row where its pads are; a third element closing a loop is
    /// refused naming all three; copper around the second is refused naming it.
    /// </summary>
    [Fact]
    public void TwoElementsCutThreeSections_ACycleAndABridgedSecondElementAreRefusedByName()
    {
        var rail = ChainRail(0.1, 0.2);
        var pads = ChainPads();

        var chain = RailSeriesPartition.FromArtworkRegions(rail, ChainWalk(ChainShapes()), pads);
        Assert.Null(chain.Refusal);
        Assert.Equal(3, chain.SectionCount);
        Assert.Equal([(0, 1), (1, 2)], chain.Elements.Select(e => (e.Upstream, e.Downstream)));
        Assert.Equal(0, chain.SourceSection(0));
        Assert.Equal(0, chain.PartSection("C1"));
        Assert.Equal(1, chain.PartSection("C2"));
        Assert.Equal(2, chain.LoadSection(0));

        // A third element from the source's copper straight to the load's closes a loop.
        var cyclic = ChainRail(0.1, 0.2);
        cyclic.Parts.Add(Series("R3", 1.0, dcr: 0.1));
        var loop = RailSeriesPartition.FromArtworkRegions(cyclic, ChainWalk(ChainShapes()), pads);
        output.WriteLine(loop.Refusal);
        Assert.NotNull(loop.Refusal);
        Assert.Contains("LOOP", loop.Refusal, StringComparison.Ordinal);
        Assert.Contains("R3", loop.Refusal, StringComparison.Ordinal);
        Assert.Contains("R1 and R2", loop.Refusal, StringComparison.Ordinal);

        // Copper joining the second element's two sides: R2 is bridged, and named.
        var bridged = RailSeriesPartition.FromArtworkRegions(
            rail, ChainWalk([.. ChainShapes(), Rect(Top, Mm(9), Mm(0.1), Mm(13), Mm(0.2))]), pads);
        output.WriteLine(bridged.Refusal);
        Assert.Contains("R2", bridged.Refusal!, StringComparison.Ordinal);
        Assert.Contains("BRIDGED", bridged.Refusal!, StringComparison.Ordinal);
    }

    // ══ §5.2 — AN OTHER LIBRARY ROW IS A SERIES ELEMENT'S MODEL ══════════════════════════════

    /// <summary>
    /// <b>§5.2 (R-rail35-2a, R-rail35-2b).</b> A series row that states nothing inherits the DCR and
    /// the Touchstone file from its library row classed Other — the file read SERIES-thru — and a
    /// value on the row overrides the library's. The model-source column names the winner.
    /// </summary>
    [Fact]
    public void AnOtherLibraryRow_SuppliesTheDcrAndTheFile_ARowValueOverrides_AndTheColumnNamesTheWinner()
    {
        string dir = Directory.CreateTempSubdirectory("brief35-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "bead.s2p"), SeriesThruFile(f => BeadZ(f)));
            var library = new PartLibrary { BaseDirectory = dir };
            library.Rows.Add(BeadRow());

            var resolver = new RailPartResolver(library);
            RailMeasuredPart? Read(string p) => resolver.ReadMeasured(p, PassiveExtraction.SeriesThrough, out _);

            // ── the row states nothing: both fields are the library's ─────────────────────────
            var bare = new RailPart { Refdes = "FB1", PartNumber = "PN-BEAD", Connection = RailPartConnection.Series };
            var inherited = RailSeriesModel.Resolve(bare, library, Read)!;

            Assert.Equal(0.050, inherited.DcResistanceOhms!.Value, 1e-12);
            Assert.Equal(RailSeriesValueSource.Library, inherited.DcResistanceFrom);
            Assert.Equal(RailSeriesValueSource.Library, inherited.ImpedanceFrom);
            Assert.Null(inherited.BiasDependentLine);

            // Read series-thru: the curve comes back as the impedance the file was written from.
            // Shunt-thru arithmetic on the same file reads 1 MHz's 30+j6.3 Ω as about 7 Ω.
            var z = inherited.ImpedanceAt(1e6);
            Assert.True((z - BeadZ(1e6)).Magnitude / BeadZ(1e6).Magnitude < 1e-6, $"read {z}");

            var row = new RailPartRowViewModel(bare, null, library.ResolveModel("PN-BEAD"), null, null, series: inherited);
            Assert.Equal("Z library file · DCR library", row.ModelSourceText);
            Assert.Equal("50 mΩ", row.EsrText);

            // ── the row states both: the row wins, field by field ─────────────────────────────
            var own = RailSeriesModel.Resolve(
                bare with { DcResistanceOhms = 0.080, SeriesResistanceOhms = 2.0 }, library, Read)!;
            Assert.Equal(0.080, own.DcResistanceOhms!.Value, 1e-12);
            Assert.Equal(RailSeriesValueSource.Row, own.DcResistanceFrom);
            Assert.Equal(RailSeriesValueSource.Row, own.ImpedanceFrom);
            Assert.Equal("Z row · DCR row", own.SourceText);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ══ §5.3 — A SHUNT ROW THAT IS NOT A CAPACITOR ═══════════════════════════════════════════

    /// <summary>
    /// <b>§5.3 (R-rail35-2d).</b> A shunt row whose library row is classed Other is refused at Run,
    /// naming the part — the state the field report's two rows were in.
    /// </summary>
    [Fact]
    public void AShuntRowWhoseLibraryRowIsOther_IsRefusedNamingThePart()
    {
        var library = Library();
        library.Rows.Add(BeadRow());

        var rail = TypedRail(withSeriesElement: false);
        rail.Parts.Add(new RailPart { Refdes = "FB1", PartNumber = "PN-BEAD" });

        var result = PdnSweep.Run(new PdnSweepRequest
        {
            Rail = rail,
            Parts = new RailPartResolver(library).ResolveAll(rail.Parts, 3.6),
            Sources = [new RailSourceModel(0, "BT1", RailSourceBasis.Rl, 1.0, 1e-6, 3.6)],
            RankRemovals = false,
        });

        output.WriteLine(result.Refusal);
        Assert.NotNull(result.Refusal);
        Assert.Contains("FB1 (PN-BEAD)", result.Refusal, StringComparison.Ordinal);
        Assert.Contains("NOT A CAPACITOR", result.Refusal, StringComparison.Ordinal);
        Assert.Contains("Make series element", result.Refusal, StringComparison.Ordinal);
    }

    // ══ §5.1 — THE GESTURE ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>§5.1 (R-rail35-1a).</b> Make series and make shunt are one undo step each, and the
    /// document written after each reads back as what the window shows.
    /// </summary>
    [Fact]
    public void MakeSeriesAndMakeShunt_AreOneUndoStepEach_AndRoundTripTheCrail()
    {
        var doc = new RailDocument { Name = "field report shape" };
        var rail = TypedRail(withSeriesElement: false);
        rail.Parts.Add(new RailPart { Refdes = "FB1", PartNumber = "PN-BEAD" });
        doc.Rails.Add(rail);

        var vm = new RailRfViewModel(doc, null)
        {
            PostToUi     = a => a(),
            RunOffThread = (work, _) => Task.FromResult(work()),
        };
        vm.RebuildParts();

        RailPart FB1(RailDocument d) => d.Rails[0].Parts.Single(p => p.Refdes == "FB1");
        RailDocument RoundTrip() => RailDocumentIo.Deserialize(RailDocumentIo.Serialize(vm.Document));

        Assert.Equal(1, vm.SetPartsConnection(["FB1"], RailPartConnection.Series));
        Assert.True(FB1(RoundTrip()).IsSeries);
        Assert.True(vm.Parts.Single(p => p.Refdes == "FB1").IsSeries);

        Assert.Equal(1, vm.SetPartsConnection(["FB1"], RailPartConnection.Shunt));
        Assert.False(FB1(RoundTrip()).IsSeries);

        // ONE undo takes back the shunt, ONE more takes back the series — and no third is needed.
        vm.UndoCommand.Execute(null);
        Assert.True(FB1(vm.Document).IsSeries);
        vm.UndoCommand.Execute(null);
        Assert.False(FB1(vm.Document).IsSeries);
        Assert.False(vm.CanUndo);
    }

    // ══ GATE 4 — THE FIELD REPORT'S SHAPE ════════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 4.</b> A bead classed Other with a stated ESR and a Touchstone file, a resistor classed
    /// Other with a stated ESR, both added as SHUNT rows as the field report's were, then marked
    /// series from the window: the rail solves, both DCRs are breakdown rows, and the bead's
    /// lumped-R-L caveat is absent because it has a measured curve.
    /// </summary>
    [Fact]
    public void Gate4_TheFieldReportsShape_MarkedSeriesFromTheWindow_Solves()
    {
        string dir = Directory.CreateTempSubdirectory("brief35-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "bead.s2p"), SeriesThruFile(f => BeadZ(f)));
            var library = Library();
            library.BaseDirectory = dir;
            library.Rows.Add(BeadRow());
            library.Rows.Add(new PartLibraryRow { PartNumber = "PN-RON", DielectricClass = PartLibraryRow.OtherClass, EsrOhms = 0.200 });

            // As the designer had it: two SHUNT rows, and nothing on them but a part number. The
            // chain fixture's first element (R1) is the bead here, and its second (R2) the resistor.
            var rail = ChainRail(null, null);
            for (int i = 0; i < rail.Parts.Count; i++)
                if (rail.Parts[i].IsSeries)
                    rail.Parts[i] = new RailPart
                    {
                        Refdes = rail.Parts[i].Refdes,
                        PartNumber = rail.Parts[i].Refdes == "R1" ? "PN-BEAD" : "PN-RON",
                    };

            var doc = new RailDocument { Name = "field report shape" };
            doc.Rails.Add(rail);

            var vm = new RailRfViewModel(doc, Path.Combine(dir, "board.crail"))
            {
                PostToUi     = a => a(),
                RunOffThread = (work, _) => Task.FromResult(work()),
                PartLibrary  = library,
                Board = new RailBoardInputs
                {
                    Technology = Board(),
                    Shapes     = ChainShapes(),
                    Pads       = ChainPads(),
                    NetPoints  = [.. ChainPads().Select(p => new PdnNetPoint(p.Net!, p.X, p.Y))],
                },
            };
            vm.RebuildParts();

            Assert.Equal(2, vm.SetPartsConnection(["R1", "R2"], RailPartConnection.Series));
            if (vm.Sweep is null) vm.RunCommand.Execute(null);

            Assert.Null(vm.Sweep!.Refusal);
            var dc = vm.SelectedRailResult!;
            Assert.Contains(dc.Breakdown, r => r.Label.Contains("R1", StringComparison.Ordinal));
            Assert.Contains(dc.Breakdown, r => r.Label.Contains("R2", StringComparison.Ordinal));

            output.WriteLine(string.Join("\n", vm.Sweep.Warnings));
            Assert.DoesNotContain(vm.Sweep.Warnings, w => w.Contains("R1 is modelled as a lumped R-L", StringComparison.Ordinal));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    private static Complex BeadZ(double f) => new(30.0, 2 * Math.PI * f * 1e-6);

    private static PartLibraryRow BeadRow() => new()
    {
        PartNumber = "PN-BEAD", DielectricClass = PartLibraryRow.OtherClass,
        EsrOhms = 0.050, ModelRef = "bead.s2p",
    };

    private static RailPart Series(string refdes, double ohms, double? dcr = null) => new()
    {
        Refdes = refdes, Connection = RailPartConnection.Series,
        TerminalA = new RailPortAnchor { Refdes = refdes, Pin = "1" },
        TerminalB = new RailPortAnchor { Refdes = refdes, Pin = "2" },
        SeriesResistanceOhms = ohms, DcResistanceOhms = dcr,
    };

    /// <summary>A two-port Touchstone file of a series-thru fixture: <c>S21 = 2Z0/(2Z0 + Z)</c>.</summary>
    private static string SeriesThruFile(Func<double, Complex> z)
    {
        var sb = new StringBuilder("# Hz S RI R 50\n");
        foreach (double f in PdnSweep.Grid(new RailBand(1e3, 1e9, 121, true)))
        {
            var zf = z(f);
            var s11 = zf / (100 + zf);
            var s21 = 100 / (100 + zf);
            sb.Append(string.Create(CultureInfo.InvariantCulture,
                $"{f:R} {s11.Real:R} {s11.Imaginary:R} {s21.Real:R} {s21.Imaginary:R} {s21.Real:R} {s21.Imaginary:R} {s11.Real:R} {s11.Imaginary:R}\n"));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Three pieces of rail copper, R1 bridging the first gap and R2 the second — the source and C1
    /// on the first, C2 on the second, the load on the third. Every row states the DEFAULT side, so a
    /// section that agrees with the placement is a measurement.
    /// </summary>
    private static RailSpec ChainRail(double? dcr1, double? dcr2)
    {
        var rail = new RailSpec { Name = "VBAT", NetName = "VBAT", ReferenceLayer = Bot };
        rail.Sources.Add(new RailSource
        {
            Anchor = new RailPortAnchor { Refdes = "BT1", Pin = "1" },
            OpenCircuitVoltageV = 3.7, SeriesResistanceOhms = 0.05,
        });
        rail.Loads.Add(new RailLoad { Anchor = new RailPortAnchor { Refdes = "U1", Pin = "VDD" }, DcCurrentA = 0.35 });
        rail.Parts.Add(Series("R1", 1.0, dcr1));
        rail.Parts.Add(Series("R2", 1.0, dcr2));
        rail.Parts.Add(new RailPart { Refdes = "C1", PartNumber = "PN-1U", MountingInductanceHenries = MountingH });
        rail.Parts.Add(new RailPart { Refdes = "C2", PartNumber = "PN-100N", MountingInductanceHenries = MountingH });
        return rail;
    }

    private static IReadOnlyList<PlacedPin> ChainPads() =>
    [
        new("BT1", "1", "VBAT", Mm(0.5), Mm(0.15), PinSource.BoardNetlist),
        new("C1", "1", "VBAT", Mm(2.0), Mm(0.15), PinSource.BoardNetlist),
        new("R1", "1", "VBAT", Mm(3.5), Mm(0.15), PinSource.BoardNetlist),
        new("R1", "2", "VBAT", Mm(6.5), Mm(0.15), PinSource.BoardNetlist),
        new("C2", "1", "VBAT", Mm(8.0), Mm(0.15), PinSource.BoardNetlist),
        new("R2", "1", "VBAT", Mm(9.5), Mm(0.15), PinSource.BoardNetlist),
        new("R2", "2", "VBAT", Mm(12.5), Mm(0.15), PinSource.BoardNetlist),
        new("U1", "VDD", "VBAT", Mm(17.5), Mm(0.15), PinSource.BoardNetlist),
        new("R3", "1", "VBAT", Mm(1.2), Mm(0.15), PinSource.BoardNetlist),
        new("R3", "2", "VBAT", Mm(16.5), Mm(0.15), PinSource.BoardNetlist),
    ];

    private static IReadOnlyList<LayoutShape> ChainShapes() =>
    [
        Rect(Top, 0, 0, Mm(4), Mm(0.3)),
        Rect(Top, Mm(6), 0, Mm(10), Mm(0.3)),
        Rect(Top, Mm(12), 0, Mm(18), Mm(0.3)),
        Rect(Bot, Mm(-1), Mm(-2), Mm(19), Mm(2)),
    ];

    private static PdnRailRegionSet ChainWalk(IReadOnlyList<LayoutShape> shapes)
    {
        var byLayer = shapes.OfType<RectShape>()
            .GroupBy(r => r.Layer)
            .ToDictionary(g => g.Key, g => Clipper.Union(
                new Paths64(g.Select(r => Box(r.X1, r.Y1, r.X2, r.Y2))), LayoutClipper.Rule));

        return Regions.Walk(
            byLayer, Board(),
            [.. ChainPads().Select(p => new PdnNetPoint(p.Net!, p.X, p.Y))],
            railNet: "VBAT", referenceLayer: Bot, referenceNet: null, extraRailSeeds: []);
    }

    private static RailDcRequest ChainDcRequest(RailSpec rail, IReadOnlyList<LayoutShape> shapes)
    {
        var doc = new RailDocument { Name = "a chain" };
        doc.Rails.Add(rail);
        return new RailDcRequest
        {
            Document = doc, Technology = Board(), Shapes = shapes, Pads = ChainPads(),
            NetPoints = [.. ChainPads().Select(p => new PdnNetPoint(p.Net!, p.X, p.Y))],
        };
    }
}
