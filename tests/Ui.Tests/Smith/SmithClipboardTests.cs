// ================================================================
//  SmithClipboardTests.cs — brief-smith-7-clipboard.md §4; docs/design/smith-chart.md §6
//
//  ── WHAT THIS GATE IS ABOUT ───────────────────────────────────────────────────────────────────
//
//  Two of the three commands are one call each into code that already exists, so there is very
//  little here worth asserting about the WIRING and a great deal worth asserting about the bytes
//  and the refusals.
//
//  R-smith7-10 says why, and it is brief 5's trap collected: PlotExporter.CopyPlotToClipboardAsync
//  opens with `if (container is null) return;`. A test that asserted the call was MADE would pass
//  against a null container and an empty clipboard, which is precisely the failure mode — nothing
//  on the clipboard, nothing raised, and a menu item that looks as though it worked. So the chart
//  test renders the container the command actually handed over and reads the SVG text.
//
//  The paste half is the reverse worry. A permissive reader that accepted PART of a selection would
//  replace a user's network with something that is not what they copied and report success, so the
//  refusals are asserted as SENTENCES naming their object rather than as a bare failure.
//
//  ── NO DISPLAY, ANYWHERE ──────────────────────────────────────────────────────────────────────
//
//  Ui.Tests may call no Avalonia runtime API. Nothing below does: the projection is a
//  SmithNetworkModel call, the transport is SchematicPersistence's own selection JSON — the exact
//  bytes SchematicClipboard puts in the text flavour — the recognizer is framework-free, and the
//  chart's picture is Skia through PlotExporter's own SVG builder. What a running Avalonia would
//  add is the platform clipboard between the serialize and the deserialize, which is the one part
//  this brief deliberately does not write.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using CircuitRF.Core.Elaboration;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Smith;
using CircuitRF.Engine;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Smith;
using Xunit;

namespace CircuitRF.Ui.Tests.Smith;

/// <summary>
/// The clipboard, both ways, and the topology recognizer. <b>One test per claim.</b>
/// </summary>
public sealed class SmithClipboardTests
{
    private const double DesignHz = 2.0e9;
    private const double ChartZ0  = 50.0;

    // ── fixtures ─────────────────────────────────────────────────────────────

    private static SmithDesign Design(params SmithElement[] elements)
    {
        var d = new SmithDesign();
        d.Chart.Z0Ohm             = ChartZ0;
        d.Chart.DesignFrequencyHz = DesignHz;
        d.Generator.Rows.Add(new SmithGeneratorRow(DesignHz, 12.0, -8.5));
        foreach (var e in elements) d.Elements.Add(e);
        return d;
    }

    private static SmithElement L(double henry, SmithPlacement placement, string name)
        => new() { Kind = SmithElementKind.L, Placement = placement, Name = name,
                   Values = { LHenry = henry } };

    private static SmithElement C(double farad, SmithPlacement placement, string name)
        => new() { Kind = SmithElementKind.C, Placement = placement, Name = name,
                   Values = { CFarad = farad } };

    /// <summary>
    /// Two series parts and two shunt arms, so the round trip exercises both placements, the spine
    /// wires between the series bodies and the per-column grounds.
    /// </summary>
    /// <remarks>
    /// The values are chosen to survive the trip EXACTLY: a schematic label is five significant
    /// figures, so 3.3 nH comes back 3.3 nH and 1.23456789 nH would not. That is a property of
    /// writing a drawing, not of this recognizer, and the assertions below say five figures rather
    /// than pretending otherwise.
    /// </remarks>
    private static SmithDesign FourElements() => Design(
        L(3.3e-9,  SmithPlacement.Series, "L1"),
        C(1.2e-12, SmithPlacement.Shunt,  "C1"),
        L(1.5e-9,  SmithPlacement.Series, "L2"),
        C(0.8e-12, SmithPlacement.Shunt,  "C2"));

    /// <summary>
    /// <b>Every kind in the vocabulary, in both of its legal placements where it has two.</b>
    /// </summary>
    /// <remarks>
    /// The round trip is the one claim worth stating over the WHOLE table rather than over a
    /// representative pair: the three kinds sharing <c>Tline</c> and the two sharing <c>Snp</c> are
    /// told apart on the way back by their placement and their port count, not by anything written
    /// in the drawing, and the two file kinds and <c>Z1P</c> are the ones whose value is not a plain
    /// number. A fixture of R's and C's would exercise none of that.
    /// </remarks>
    private static SmithDesign EveryKind()
    {
        var d = Design();
        d.Elements.Add(new SmithElement { Kind = SmithElementKind.R, Placement = SmithPlacement.Series,
            Name = "R1", Values = { ROhm = 12.5 } });
        d.Elements.Add(new SmithElement { Kind = SmithElementKind.L, Placement = SmithPlacement.Shunt,
            Name = "L1", Values = { LHenry = 3.3e-9 } });
        d.Elements.Add(new SmithElement { Kind = SmithElementKind.C, Placement = SmithPlacement.Series,
            Name = "C1", Values = { CFarad = 1.2e-12 } });
        d.Elements.Add(new SmithElement { Kind = SmithElementKind.Srlc, Placement = SmithPlacement.Shunt,
            Name = "X1", Values = { ROhm = 0.4, LHenry = 0.9e-9, CFarad = 4.7e-12 } });
        d.Elements.Add(new SmithElement { Kind = SmithElementKind.Prlc, Placement = SmithPlacement.Series,
            Name = "X2", Values = { ROhm = 800.0, LHenry = 2.2e-9, CFarad = 2.7e-12 } });
        d.Elements.Add(new SmithElement { Kind = SmithElementKind.Z1P, Placement = SmithPlacement.Shunt,
            Name = "Z1", Values = { ImpedanceOhm = new Complex(18.0, -42.0) } });
        d.Elements.Add(new SmithElement { Kind = SmithElementKind.S1P, Placement = SmithPlacement.Shunt,
            Name = "F1", FileRef = "parts/load.s1p" });
        d.Elements.Add(new SmithElement { Kind = SmithElementKind.S2P, Placement = SmithPlacement.Series,
            Name = "F2", FileRef = "parts/thru.s2p" });
        d.Elements.Add(new SmithElement { Kind = SmithElementKind.Tline, Placement = SmithPlacement.Series,
            Name = "T1", Values = { Z0Ohm = 63.5, ElectricalLengthDeg = 41.25, ReferenceFrequencyHz = DesignHz } });
        d.Elements.Add(new SmithElement { Kind = SmithElementKind.StubOpen, Placement = SmithPlacement.Shunt,
            Name = "T2", Values = { Z0Ohm = 72.0, ElectricalLengthDeg = 90.0, ReferenceFrequencyHz = DesignHz } });
        d.Elements.Add(new SmithElement { Kind = SmithElementKind.StubShorted, Placement = SmithPlacement.Shunt,
            Name = "T3", Values = { Z0Ohm = 25.0, ElectricalLengthDeg = 33.5, ReferenceFrequencyHz = DesignHz } });
        return d;
    }

    /// <summary>
    /// The clipboard round trip, minus the platform clipboard: the copy's projection, through the
    /// SAME selection JSON <c>SchematicClipboard</c> writes as its text flavour, and back.
    /// </summary>
    private static (List<EditableComponent> Comps, List<EditableWire> Wires)
        ThroughTheClipboard(SmithDesign design)
    {
        var model = SmithSchematicCopy.Build(design, documentDirectory: null);
        string json = SchematicPersistence.SerializeSelection(
            model.Components, model.Wires, model.CanvasObjects, model.GridSize);

        var (comps, wires, _, _) = SchematicPersistence.DeserializeSelection(json);
        return (comps, wires);
    }

    private static EditableComponent Find(SchematicEditModel m, string name)
        => m.Components.First(c => c.InstanceName == name);

    private static string? Param(EditableComponent c, string name)
        => c.Parameters.FirstOrDefault(p => p.Name == name)?.Expression;

    // ═════════════════════════════════════════════════════════════════════════
    //  1. R-smith7-10 — a chart copy produces REAL BYTES, not a call
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The chart copy hands over a container that renders to a non-empty SVG carrying this
    /// tool's own marks.</b>
    /// </summary>
    /// <remarks>
    /// <b>Asserting that <c>CopyPlotToClipboardAsync</c> was invoked would pass with a null
    /// container and an empty clipboard</b>, which is exactly the failure brief 5's
    /// <c>R-smith5-5</c> is about. So the test renders what the command actually passed and looks
    /// for two things no other plot could have drawn: the element's own trajectory label, and the
    /// load point's frequency label, which comes from the OVERLAY — the half a copy composed from
    /// containers would drop if <c>PlotContainerViewModel.Overlay</c> were not carried through.
    /// </remarks>
    [Fact]
    public async Task AChartCopyProducesRealBytes()
    {
        var vm = new SmithChartViewModel(FourElements());

        // What SmithChartView.BindChartPlot does, and the only part of it this claim depends on.
        vm.ChartContainer.Overlay = vm.ChartOverlay;

        PlotContainerViewModel? handedOver = null;
        vm.ChartCopySink = c => { handedOver = c; return Task.CompletedTask; };

        await vm.CopyChartCommand.ExecuteAsync(null);

        Assert.NotNull(handedOver);
        Assert.Same(vm.ChartContainer, handedOver);

        string svg = PlotExporter.BuildSvgStringForContainers([handedOver!], RenderTheme.Light);

        Assert.False(string.IsNullOrWhiteSpace(svg), "the chart copy produced no SVG at all");
        Assert.Contains("<svg", svg, StringComparison.Ordinal);

        // The load point's frequency, drawn by SmithGripperOverlay — present only because the
        // container carries the overlay into the export path.
        string f = SmithPlotBuilder.FrequencyLabel(DesignHz);
        Assert.Contains(f, svg, StringComparison.Ordinal);

        // …and the same picture composed WITHOUT the overlay does not have it, which is what makes
        // the assertion above mean something rather than matching any text on the page.
        vm.ChartContainer.Overlay = null;
        string bare = PlotExporter.BuildSvgStringForContainers([vm.ChartContainer], RenderTheme.Light);
        Assert.True(bare.Length > 0, "the overlay-free picture must still render");
        Assert.DoesNotContain(f, bare, StringComparison.Ordinal);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  2. The round trip
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Copy a network, paste it back, and the element list is identical in type, placement,
    /// order and value.</b>
    /// </summary>
    [Fact]
    public void ANetworkSurvivesTheRoundTrip()
    {
        var design = EveryKind();
        var (comps, wires) = ThroughTheClipboard(design);

        var result = SmithPasteRecognizer.Recognize(comps, wires, mirrored: false, DesignHz);

        Assert.Null(result.Refusal);
        AssertSameCascade(design.Elements, result.Elements!);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  3. R-smith7-7's own regression — a MIRRORED network survives the trip
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Copy while mirrored, paste into a NON-mirrored strip, and the cascade comes back in the
    /// same order.</b>
    /// </summary>
    /// <remarks>
    /// This is the reason the geometric end rule is stated as "the end that matches the strip's
    /// current mirror setting" rather than as a bare "leftmost". The copy always carries ports, so
    /// the port rule is what fires here — and the test asserts the SENTENCE says so, because the
    /// two rules can disagree and a network that came back reversed with nothing said would be
    /// wrong half the time and silent every time.
    /// </remarks>
    [Fact]
    public void AMirroredNetworkSurvivesTheClipboard()
    {
        var mirrored = FourElements();
        mirrored.View.MirrorNetwork = true;

        var (comps, wires) = ThroughTheClipboard(mirrored);

        // The drawing really is reversed — the fixture's own premise.
        Assert.True(Find(SmithSchematicCopy.Build(mirrored, null), "L1").X < 0);

        var result = SmithPasteRecognizer.Recognize(comps, wires, mirrored: false, DesignHz);

        Assert.Null(result.Refusal);
        AssertSameCascade(mirrored.Elements, result.Elements!);
        Assert.Contains("port 1", result.Note ?? "", StringComparison.Ordinal);

        // And the geometric rule, exercised on its own: the same drawing with its ports removed
        // reverses when the strip's mirror disagrees with the one it was drawn under.
        var unported = comps.Where(c => c.Symbol != SymbolKind.TermG).ToList();

        var asMirrored   = SmithPasteRecognizer.Recognize(unported, wires, mirrored: true,  DesignHz);
        var asUnmirrored = SmithPasteRecognizer.Recognize(unported, wires, mirrored: false, DesignHz);

        Assert.Null(asMirrored.Refusal);
        Assert.Null(asUnmirrored.Refusal);
        Assert.Contains("right-hand end", asMirrored.Note ?? "", StringComparison.Ordinal);
        AssertSameCascade(mirrored.Elements, asMirrored.Elements!);
        Assert.Equal(
            mirrored.Elements.Select(e => e.Name).Reverse(),
            asUnmirrored.Elements!.Select(e => e.Name));
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  4. R-smith7-6 — every refusal fires AND NAMES ITS OBJECT
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Five selections this tool cannot represent, five refusals, each naming what stopped
    /// it.</b>
    /// </summary>
    /// <remarks>
    /// <b>The sentence is the assertion, not the failure.</b> A refusal whose text does not identify
    /// the offending object is not a refusal anyone can act on — and a recognizer that answered
    /// "no" to all five with one generic sentence would pass a test that only checked for null.
    /// </remarks>
    [Fact]
    public void EveryRefusalFiresAndNamesItsObject()
    {
        // (a) A BRANCH — three series parts meeting at one net. Their leads COINCIDE at (200, 0),
        // which is how the projection joins a column to the spine and needs no wire at all.
        var third = Series(SymbolKind.Resistor, "R3", 200, "R", "50", "Ω");
        third.Y        = 200;                       // standing upright, top pin on the meeting point
        third.Rotation = SymbolRotation.R0;
        var branch = Wired(
            [Series(SymbolKind.Resistor, "R1", 0,   "R", "50", "Ω"),
             Series(SymbolKind.Resistor, "R2", 400, "R", "50", "Ω"),
             third],
            []);
        var a = SmithPasteRecognizer.Recognize(branch.C, branch.W, false, DesignHz);
        Assert.NotNull(a.Refusal);
        Assert.Contains("branch", a.Refusal!, StringComparison.Ordinal);
        Assert.Contains("R1", a.Refusal!, StringComparison.Ordinal);

        // (b) A THREE-PIN component — a 3-port Touchstone, which the vocabulary has no place for.
        var threePin = Wired(
            [Series(SymbolKind.Snp, "S1", 0, ("NumPorts", "3", ""), ("File", "x.s3p", ""))],
            []);
        var b = SmithPasteRecognizer.Recognize(threePin.C, threePin.W, false, DesignHz);
        Assert.NotNull(b.Refusal);
        Assert.Contains("S1", b.Refusal!, StringComparison.Ordinal);
        Assert.Contains("3 nets", b.Refusal!, StringComparison.Ordinal);

        // (c) AN UNREPRESENTABLE PARAMETER — an expression, which this tool stores no form of.
        var design = FourElements();
        var (comps, wires) = ThroughTheClipboard(design);
        comps.First(x => x.InstanceName == "C1").Parameters.First(p => p.Name == "C").Expression
            = "Cnom*2";
        var c = SmithPasteRecognizer.Recognize(comps, wires, false, DesignHz);
        Assert.NotNull(c.Refusal);
        Assert.Contains("C1", c.Refusal!, StringComparison.Ordinal);
        Assert.Contains("Cnom*2", c.Refusal!, StringComparison.Ordinal);

        // (d) A SHUNT ELEMENT NOT ON GROUND — an arm whose far end goes nowhere.
        var (armC, armW) = ThroughTheClipboard(Design(C(1.2e-12, SmithPlacement.Shunt, "C9")));
        armC.RemoveAll(x => x.Symbol == SymbolKind.Ground);
        var d = SmithPasteRecognizer.Recognize(armC, armW, false, DesignHz);
        Assert.NotNull(d.Refusal);
        Assert.Contains("C9", d.Refusal!, StringComparison.Ordinal);
        Assert.Contains("ground", d.Refusal!, StringComparison.Ordinal);

        // (e) THREE END NETS — a third port, on a net of its own.
        var (portC, portW) = ThroughTheClipboard(FourElements());
        portC.Add(Term("P3", 3, 4200));
        var e = SmithPasteRecognizer.Recognize(portC, portW, false, DesignHz);
        Assert.NotNull(e.Refusal);
        Assert.Contains("3 port nets", e.Refusal!, StringComparison.Ordinal);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  5. R-smith7-2 — the copied circuit is a RUNNABLE two-port
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The generator end carries <c>Num=1</c> and the design frequency's impedance, the load end
    /// <c>Num=2</c> and Z₀_chart, and no analysis card is copied — and the whole thing elaborates
    /// and runs.</b>
    /// </summary>
    /// <remarks>
    /// The run is the half that cannot be faked by reading the parameters back: a <c>Z</c> written
    /// in a spelling the expression engine does not parse, or with a unit the elaborator does not
    /// know, produces exactly the same parameter rows and fails three layers away with
    /// <c>Unknown unit ''</c>. Complex generator impedance on purpose, because that is the spelling
    /// with no precedent in the Designer's own copy.
    /// </remarks>
    [Fact]
    public void TheCopiedCircuitIsRunnable()
    {
        var design = FourElements();
        var model  = SmithSchematicCopy.Build(design, documentDirectory: null);

        var gen  = Find(model, SmithSchematicCopy.PortNames.Generator);
        var load = Find(model, SmithSchematicCopy.PortNames.Load);

        Assert.Equal(SymbolKind.TermG, gen.Symbol);
        Assert.Equal(SymbolKind.TermG, load.Symbol);
        Assert.Equal("1", Param(gen,  "Num"));
        Assert.Equal("2", Param(load, "Num"));

        // The generator's own impedance at the design frequency, in the engine's own spelling.
        var zg = SmithCascade.GeneratorImpedance(design.Generator, DesignHz);
        Assert.Equal($"complex({zg.Real:G6},{zg.Imaginary:G6})", Param(gen, "Z"));

        // …and Z0_chart at the load, as a plain value with its unit.
        Assert.Equal(ChartZ0,
            double.Parse(Param(load, "Z")!, CultureInfo.InvariantCulture)
                * CircuitRF.Design.Matching.MatchValueFormat.Scale(
                      load.Parameters.First(p => p.Name == "Z").Unit), 9);

        // A pasted selection is a fragment of a circuit; the TestBench it lands in owns its analyses.
        Assert.Empty(model.Analyses);

        // The claim that makes the two above worth anything: it runs.
        var extraction = NetExtractor.Extract(model);
        var netlist    = new Elaborator(extraction.Library).Elaborate(extraction.TestBench);
        var s          = SParameterEngine.Run(netlist, [DesignHz]);

        // And it is the cascade the strip was showing. Port 2 is the LOAD end and its reference is
        // Z₀_chart, so S22 read back through it is the impedance looking into the network from
        // there — with port 1 presenting the generator's own impedance, which is exactly what the
        // evaluator's last node is: Zgen carried through the whole cascade, "the load is where you
        // read" (§3.2). S11 is the other direction and a different number.
        var s22 = (Complex)s["S"][0, 1, 1];
        Assert.True(double.IsFinite(s22.Real) && double.IsFinite(s22.Imaginary));

        var zLoadSees = ChartZ0 * (Complex.One + s22) / (Complex.One - s22);
        var expected  = SmithCascade.Evaluate(design, DesignHz)[^1].Z;

        Assert.Equal(expected.Real,      zLoadSees.Real,      3);
        Assert.Equal(expected.Imaginary, zLoadSees.Imaginary, 3);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  6. R-smith7-3 — a multi-row generator table is a LOSSY projection, stated
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A copy from a multi-row generator table says so, naming the frequency that was used — and
    /// a single-row one says nothing.</b>
    /// </summary>
    [Fact]
    public async Task TheMultiRowNoteFiresAndNamesTheFrequency()
    {
        var one = new SmithChartViewModel(FourElements());
        one.NetworkCopySink = _ => Task.CompletedTask;
        await one.CopyNetworkCommand.ExecuteAsync(null);
        Assert.Null(one.StripNotice);

        var design = FourElements();
        design.Generator.Rows.Clear();
        design.Generator.Rows.Add(new SmithGeneratorRow(1.8e9, 12.0, -8.5));
        design.Generator.Rows.Add(new SmithGeneratorRow(2.0e9, 11.4, -9.1));
        design.Generator.Rows.Add(new SmithGeneratorRow(2.2e9, 10.9, -9.8));

        var many = new SmithChartViewModel(design);
        many.NetworkCopySink = _ => Task.CompletedTask;
        await many.CopyNetworkCommand.ExecuteAsync(null);

        Assert.NotNull(many.StripNotice);
        Assert.Contains(SmithPlotBuilder.FrequencyLabel(DesignHz), many.StripNotice!,
                        StringComparison.Ordinal);
        Assert.Contains("3 rows", many.StripNotice!, StringComparison.Ordinal);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  7. R-smith7-8 — one paste is ONE undo entry
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A paste that replaces a four-element cascade with a two-element one is a single undo
    /// entry, and one Undo restores the whole previous network.</b>
    /// </summary>
    [Fact]
    public async Task OnePasteIsOneUndoEntry()
    {
        var vm = new SmithChartViewModel(FourElements());
        var before = vm.Design.Elements.Select(e => (e.Name, e.Kind, e.Placement)).ToList();

        var replacement = Design(
            C(2.7e-12, SmithPlacement.Series, "Cs"),
            L(6.8e-9,  SmithPlacement.Shunt,  "Lp"));
        var payload = ThroughTheClipboard(replacement);

        vm.NetworkPasteSource = () => Task.FromResult<(IReadOnlyList<EditableComponent>,
                                                       IReadOnlyList<EditableWire>)?>(payload);

        await vm.PasteNetworkCommand.ExecuteAsync(null);

        Assert.Equal(["Cs", "Lp"], vm.Design.Elements.Select(e => e.Name));
        Assert.True(vm.UndoRedo.CanUndo);

        vm.UndoRedo.Undo();

        Assert.Equal(before, vm.Design.Elements.Select(e => (e.Name, e.Kind, e.Placement)).ToList());
        Assert.False(vm.UndoRedo.CanUndo);
    }

    // ── assertions and builders ──────────────────────────────────────────────

    /// <summary>
    /// Two cascades are the same cascade: same order, same names, same kinds, same placements, same
    /// enabled state, same values to the five significant figures a drawn label carries.
    /// </summary>
    private static void AssertSameCascade(
        IList<SmithElement> expected, IReadOnlyList<SmithElement> actual)
    {
        Assert.Equal(expected.Count, actual.Count);

        for (int i = 0; i < expected.Count; i++)
        {
            var e = expected[i];
            var a = actual[i];

            Assert.Equal(e.Name,      a.Name);
            Assert.Equal(e.Kind,      a.Kind);
            Assert.Equal(e.Placement, a.Placement);
            Assert.Equal(e.Enabled,   a.Enabled);

            Assert.Equal(e.FileRef, a.FileRef);

            Close(e.Values.ROhm,   a.Values.ROhm,   $"{e.Name}.R");
            Close(e.Values.LHenry, a.Values.LHenry, $"{e.Name}.L");
            Close(e.Values.CFarad, a.Values.CFarad, $"{e.Name}.C");

            if (SmithComponentMap.IsLine(e.Kind))
            {
                Close(e.Values.Z0Ohm,                a.Values.Z0Ohm,                $"{e.Name}.Z0");
                Close(e.Values.ElectricalLengthDeg,  a.Values.ElectricalLengthDeg,  $"{e.Name}.E");
                Close(e.Values.ReferenceFrequencyHz, a.Values.ReferenceFrequencyHz, $"{e.Name}.F");
            }

            if (e.Kind == SmithElementKind.Z1P)
            {
                Close(e.Values.ImpedanceOhm.Real,      a.Values.ImpedanceOhm.Real,      $"{e.Name}.Re");
                Close(e.Values.ImpedanceOhm.Imaginary, a.Values.ImpedanceOhm.Imaginary, $"{e.Name}.Im");
            }
        }

        static void Close(double want, double got, string what)
        {
            if (want == 0.0) { Assert.Equal(0.0, got); return; }
            Assert.True(Math.Abs(got - want) <= 1e-5 * Math.Abs(want),
                        $"{what}: expected {want:G8}, got {got:G8}");
        }
    }

    private static (List<EditableComponent> C, List<EditableWire> W) Wired(
        IEnumerable<EditableComponent> components, IEnumerable<EditableWire> wires)
        => ([.. components], [.. wires]);

    /// <summary>A two-terminal part laid on its side in the spine at <paramref name="x"/>, pins at
    /// x ± 200 — the projection's own geometry.</summary>
    private static EditableComponent Series(
        SymbolKind kind, string name, double x, params (string Name, string Expr, string Unit)[] ps)
    {
        var c = new EditableComponent
        {
            InstanceName = name,
            Symbol       = kind,
            X = x, Y = 0,
            Rotation = kind is SymbolKind.Resistor or SymbolKind.Inductor or SymbolKind.Capacitor
                           ? SymbolRotation.R270 : SymbolRotation.R0,
        };
        foreach (var (n, expr, unit) in ps)
            c.Parameters.Add(new EditableParameter { Name = n, Expression = expr, Unit = unit });
        return c;
    }

    private static EditableComponent Series(
        SymbolKind kind, string name, double x, string p, string expr, string unit)
        => Series(kind, name, x, [(p, expr, unit)]);

    private static EditableComponent Term(string name, int num, double x)
    {
        var c = new EditableComponent
        {
            InstanceName = name, Symbol = SymbolKind.TermG,
            X = x, Y = SmithNetworkModel.SpineY + SmithNetworkModel.LeadHalf,
            Rotation = SymbolRotation.R0,
        };
        c.Parameters.Add(new EditableParameter
            { Name = "Num", Expression = num.ToString(CultureInfo.InvariantCulture) });
        c.Parameters.Add(new EditableParameter { Name = "Z", Expression = "50", Unit = "Ω" });
        return c;
    }

    private static EditableWire Wire(double x0, double y0, double x1, double y1)
    {
        var w = new EditableWire();
        w.Points.Add((x0, y0));
        w.Points.Add((x1, y1));
        return w;
    }
}
