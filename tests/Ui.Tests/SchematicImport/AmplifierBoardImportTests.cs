// An amplifier board's schematic, imported and simulated the way a user would: a polarized capacitor, a ferrite bead and a
// supply connector that should have been circuitRF's own parts; an amplifier symbol filled solid and its curved
// capacitor plate missing; free text upside down and the box around it never drawn; a ground left standing by a dropped
// power flag; two parts the source draws closer than circuitRF's symbols are long. And the defect that cost the most: a
// placed cell with no model, or whose Pins did not match its symbol, was left out of the run with a warning, so the
// amplifier simulated as an open.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Pdk;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Schematic.Import;
using CircuitRF.Design.Symbol;
using Xunit;

namespace CircuitRF.Ui.Tests.ImportSchematic;

public sealed class AmplifierBoardImportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-ampboard-" + Guid.NewGuid().ToString("N")[..8]);

    public AmplifierBoardImportTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static ForeignWire W(params (double X, double Y)[] points) => new(points);

    private static ForeignSymbol TwoPin(string key, double top, double bottom, string name1 = "~", string name2 = "~") => new(key, key, "lib",
    [
        new ForeignSymbolUnit(1,
            [new ForeignPin("1", name1, ForeignPinType.Passive, 0, top, ForeignPinDirection.Down),
             new ForeignPin("2", name2, ForeignPinType.Passive, 0, bottom, ForeignPinDirection.Up)],
            [new KitSymbolRectangle(-40, -50, 40, 50, false)]),
    ]);

    private static readonly ForeignSymbol Ground = new("GND", "GND", "power",
        [new ForeignSymbolUnit(1, [new ForeignPin("1", "GND", ForeignPinType.PowerIn, 0, 0, ForeignPinDirection.Down, Hidden: true)],
                               [new KitSymbolLine(-50, 0, 50, 0)])]);

    /// <summary>A three-pin amplifier — in, GND, out — between two labelled nets, its ground pin on a ground symbol.</summary>
    private static ForeignSchematic Amplifier() => new("dsn", "amp.dsn",
    [
        new ForeignSheet("Page1", 3000, 3000)
        {
            Instances = [new("U1", 1, "AMP", 1000, 1000, Value: "AMP"), new("#G1", 1, "GND", 1200, 1500)],
            Wires = [W((500, 1000), (1000, 1000)), W((1600, 1000), (2100, 1000)), W((1200, 1300), (1200, 1500))],
            Labels = [new(ForeignLabelKind.Global, "IN", 500, 1000), new(ForeignLabelKind.Global, "OUT", 2100, 1000)],
        },
    ],
    [
        new ForeignSymbol("AMP", "AMP", "lib",
        [
            new ForeignSymbolUnit(1,
                [new ForeignPin("1", "~", ForeignPinType.Input, 0, 0, ForeignPinDirection.Right),
                 new ForeignPin("2", "GND", ForeignPinType.PowerIn, 200, -300, ForeignPinDirection.Up),
                 new ForeignPin("3", "~", ForeignPinType.Output, 600, 0, ForeignPinDirection.Left)],
                [new KitSymbolPath([100, 200, 500, 0, 100, -200], true, false)]),
        ]),
        Ground,
    ], "Page1");

    private SchematicImportResult ImportAmplifier()
    {
        var result = SchematicImport.Import(Amplifier(), _dir, "Top");
        Assert.True(result.Ok, result.Refusal);
        Assert.True(result.Connectivity!.Matched, string.Join("\n", result.Messages));
        return result;
    }

    private string AmpCell() => Directory.GetDirectories(_dir, "AMP", SearchOption.AllDirectories).Single();

    // ── a placed cell the netlist would leave out refuses the run ───────────────────────────────

    [Fact]
    public void ASymbolCellWithNoModel_RefusesTheRun_RatherThanSimulatingAnOpen()
    {
        var result = ImportAmplifier();

        var (extracted, _) = SchematicCircuit.ExtractFile(result.SchematicPath!);

        Assert.Single(extracted.DroppedInstances);
        Assert.Contains("U1", extracted.RunRefusal, StringComparison.Ordinal);
        var prepared = CircuitRF.Design.Circuit.PreparedCircuit.FromSchematic(result.SchematicPath!);
        Assert.Equal(extracted.RunRefusal, prepared.ReadError);
    }

    [Fact]
    public void ACellWhosePinsDoNotMatchItsSymbol_RefusesTheRun_SayingWhichPinsItNeeds()
    {
        var result = ImportAmplifier();
        // Two Pins under a three-pin symbol: in and out, the ground pin forgotten.
        var twoPins = new SchematicEditModel();
        foreach (var (num, y) in new[] { ("1", 0.0), ("2", 200.0) })
        {
            var pin = new EditableComponent { Symbol = SymbolKind.Pin, X = -600, Y = y };
            pin.Parameters.Add(new EditableParameter { Name = "Num", Expression = num });
            twoPins.Components.Add(pin);
        }
        CellCreate.WriteSchematicView(AmpCell(), "AMP", "AMP", twoPins);
        CellSymbolResolver.Invalidate(AmpCell());

        var (extracted, _) = SchematicCircuit.ExtractFile(result.SchematicPath!);

        Assert.Contains("Num 1 to 3", extracted.RunRefusal, StringComparison.Ordinal);
    }

    [Fact]
    public void ANewSchematicInACellWithASymbol_StartsWithOnePinPerSymbolPin()
    {
        ImportAmplifier();

        var model = CellCreate.PinsFromSymbol(AmpCell())!;

        var pins = model.Components.Where(c => c.Symbol == SymbolKind.Pin).ToList();
        Assert.Equal(["1", "2", "3"], pins.Select(p => p.Parameters.Single(q => q.Name == "Num").Expression));
        Assert.Equal(["", "GND", ""], pins.Select(p => p.Parameters.Single(q => q.Name == "Name").Expression));
    }

    // ── what a part becomes ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void APolarizedCapacitor_AFerriteBead_AndASupplyConnector_BecomeCircuitRFParts()
    {
        var source = new ForeignSchematic("dsn", "parts.dsn",
        [
            new ForeignSheet("Page1", 3000, 3000)
            {
                Instances =
                [
                    new("C6", 1, "CP1", 500, 500, Value: "10uF"),
                    new("FB1", 1, "Ferrite_Bead_Small", 1000, 500, Value: "220R/1A"),
                    new("J2", 1, "SUPPLY", 1500, 500, Value: "SUPPLY"),
                ],
            },
        ], [TwoPin("CP1", 150, -150), TwoPin("Ferrite_Bead_Small", 150, -150), TwoPin("SUPPLY", 0, -100, "GND", "5V")], "Page1");

        var map = SchematicImportMap.Decide(source).ToDictionary(d => d.Reference);

        Assert.Equal((SymbolKind.Capacitor, "10"), (map["C6"].Native!.Value, map["C6"].Expression));
        Assert.Equal((SymbolKind.Bead, "Rp", "220"), (map["FB1"].Native!.Value, map["FB1"].Parameter, map["FB1"].Expression));
        Assert.Equal((SymbolKind.Vdc, "5"), (map["J2"].Native!.Value, map["J2"].Expression));
        Assert.Equal(["2", "1"], map["J2"].PinOrder);   // + on the 5V pin, whichever the symbol lists first
        Assert.DoesNotContain(SchematicImport.PortCandidates(source), c => c.Key == "FB1");   // "Small" is not "SMA"
    }

    [Theory]
    [InlineData("5V", "5")]
    [InlineData("+12V", "12")]
    [InlineData("3V3", "3.3")]
    [InlineData("+3.3V", "3.3")]
    [InlineData("VCC", null)]
    [InlineData("3.3V3", null)]
    public void ASupplyPinName_StatesItsVoltage(string name, string? volts) =>
        Assert.Equal(volts, SchematicImportMap.VoltageOf(name));

    // ── the drawing ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TwoPartsDrawnCloserThanTheirSymbolsAreLong_MeetPinToPin_InsteadOfFallingBackToLabels()
    {
        // A resistor (pins 850, 1150) and a capacitor (pins 1200, 1500) joined by a 50-mil wire: circuitRF's symbols are
        // 400 long, so fitted where each is drawn they overlap. One grid further down the capacitor meets the resistor.
        var source = new ForeignSchematic("dsn", "stack.dsn",
        [
            new ForeignSheet("Page1", 3000, 3000)
            {
                Instances = [new("R1", 1, "R", 1000, 1000, Value: "47R"), new("C3", 1, "C", 1000, 1350, Value: "1nF"),
                             new("#G1", 1, "GND", 1000, 1700)],
                Wires = [W((1000, 500), (1000, 850)), W((1000, 1150), (1000, 1200)), W((1000, 1500), (1000, 1700))],
                Labels = [new(ForeignLabelKind.Global, "IN", 1000, 500)],
            },
        ], [TwoPin("R", 150, -150), TwoPin("C", 150, -150), Ground], "Page1");

        var built = SchematicImportBuild.Build(source, "Top", Path.Combine(_dir, "Top", "schematic"), Path.Combine(_dir, "parts"));

        Assert.True(built.Ok, string.Join("\n", built.Refusals));
        Assert.DoesNotContain(built.Notes, n => n.Contains("could not be wired", StringComparison.Ordinal));
        var model = built.Top!.Model;
        var r1 = model.Components.Single(c => c.InstanceName == "R1");
        var c3 = model.Components.Single(c => c.InstanceName == "C3");
        Assert.Equal(r1.Y + 200, c3.Y - 200);
        Assert.True(ImportConnectivity.Compare(source, built).Matched);
    }

    [Fact]
    public void ALineDrawnOnTheSheet_IsInTheRenderModel()
    {
        var model = new SchematicEditModel();
        model.CanvasObjects.Add(new EditablePrimitive { Shape = PrimitiveShape.Line, P1X = 0, P1Y = 0, P2X = 1000, P2Y = 0 });

        var line = Assert.IsType<LinePrimitive>(Assert.Single(model.BuildRenderModel().Model.Shapes).Primitive);
        Assert.Equal((0, 1000), (line.X1, line.X2));
    }

    // ── the reader ──────────────────────────────────────────────────────────────────────────────

    private ForeignSchematic Read(string body)
    {
        string path = Path.Combine(_dir, "board.kicad_sch");
        File.WriteAllText(path, "(kicad_sch (version 20201015) (generator handwritten) (uuid \"00000000-0000-0000-0000-000000000001\") (paper \"A4\")\n"
                                + body + "\n(sheet_instances (path \"/\" (page \"1\"))))");
        var read = SchematicSexprReader.Read(path);
        Assert.True(read.Ok, read.Refusal);
        return read.Schematic!;
    }

    private const string PowerSymbols = """
        (symbol "power:GND" (power) (pin_names (offset 0)) (in_bom yes) (on_board yes)
          (property "Reference" "#PWR" (id 0) (at 0 -6.35 0) (effects (font (size 1.27 1.27))))
          (property "Value" "GND" (id 1) (at 0 -3.81 0) (effects (font (size 1.27 1.27))))
          (symbol "GND_0_1" (polyline (pts (xy 0 0) (xy 0 -1.27)) (stroke (width 0)) (fill (type none))))
          (symbol "GND_1_1" (pin power_in line (at 0 0 270) (length 0) hide
            (name "GND" (effects (font (size 1.27 1.27)))) (number "1" (effects (font (size 1.27 1.27)))))))
        (symbol "power:PWR_FLAG" (power) (pin_numbers hide) (pin_names (offset 0) hide) (in_bom yes) (on_board yes)
          (property "Reference" "#FLG" (id 0) (at 0 1.905 0) (effects (font (size 1.27 1.27))))
          (property "Value" "PWR_FLAG" (id 1) (at 0 3.81 0) (effects (font (size 1.27 1.27))))
          (symbol "PWR_FLAG_0_0" (pin power_out line (at 0 0 90) (length 0)
            (name "pwr" (effects (font (size 1.27 1.27)))) (number "1" (effects (font (size 1.27 1.27)))))))
        """;

    [Fact]
    public void AnArcByItsCentre_ABackgroundFill_AndHiddenPinNames_AreReadAsTheSourceDrawsThem()
    {
        var source = Read("""
            (lib_symbols
              (symbol "x:CPOL" (pin_numbers hide) (pin_names (offset 0.254) hide) (in_bom yes) (on_board yes)
                (property "Reference" "C" (id 0) (at 0.635 2.54 0) (effects (font (size 1.27 1.27))))
                (property "Value" "CPOL" (id 1) (at 0.635 -2.54 0) (effects (font (size 1.27 1.27))))
                (symbol "CPOL_0_1"
                  (arc (start -2.032 -1.27) (end 2.032 -1.27) (radius (at 0 -3.81) (length 3.2512) (angles 128.7 51.3))
                    (stroke (width 0.508)) (fill (type none)))
                  (polyline (pts (xy -2.032 0.762) (xy 2.032 0.762) (xy 0 2) (xy -2.032 0.762)) (stroke (width 0.254)) (fill (type background))))
                (symbol "CPOL_1_1"
                  (pin passive line (at 0 3.81 270) (length 2.794) (name "~" (effects (font (size 1.27 1.27)))) (number "1" (effects (font (size 1.27 1.27)))))
                  (pin passive line (at 0 -3.81 90) (length 3.302) (name "B" (effects (font (size 1.27 1.27)))) (number "2" (effects (font (size 1.27 1.27))))))))
            """);

        var symbol = source.Symbols.Single(s => s.DisplayName == "CPOL");
        var shapes = symbol.Units[0].Graphics;
        var arc = Assert.Single(shapes.OfType<KitSymbolArc>());
        Assert.InRange(arc.Radius, 127, 129);                       // 3.2512 mm about its centre, the shorter way round
        Assert.InRange(Math.Abs(arc.SweepDeg), 76, 79);
        Assert.False(Assert.Single(shapes.OfType<KitSymbolPath>()).Filled);   // the pale body colour is not a fill
        Assert.True(symbol.PinNamesHidden);
    }

    [Fact]
    public void TextAtOneEighty_ReadsLevel_AnchoredWhereItsJustificationSays()
    {
        var source = Read("""
            (lib_symbols)
            (text "Matching" (at 40.64 20.32 180) (effects (font (size 1.27 1.27)) (justify right bottom)))
            """);

        var text = Assert.Single(source.Pages.First().Texts);
        Assert.Equal((0, SymbolTextAlign.Right, SymbolTextVAlign.Bottom), (text.Rotation, text.Align, text.VAlign));
    }

    [Fact]
    public void AGroundThatOnlyHeldAPowerFlag_GoesWithTheFlag_WireAndAll()
    {
        var source = Read($"""
            (lib_symbols {PowerSymbols})
            (symbol (lib_id "power:PWR_FLAG") (at 60.96 76.2 0) (unit 1) (in_bom yes) (on_board yes)
              (property "Reference" "#FLG1" (id 0) (at 60.96 74.3 0)) (property "Value" "PWR_FLAG" (id 1) (at 60.96 71.9 0)))
            (symbol (lib_id "power:GND") (at 60.96 78.74 0) (unit 1) (in_bom yes) (on_board yes)
              (property "Reference" "#PWR1" (id 0) (at 60.96 85.09 0)) (property "Value" "GND" (id 1) (at 61.07 83.06 0)))
            (wire (pts (xy 60.96 76.2) (xy 60.96 78.74)) (stroke (width 0) (type solid) (color 0 0 0 0)))
            """);

        var page = source.Pages.First();
        Assert.Empty(page.Labels);
        Assert.Empty(page.Wires);
        Assert.Contains(source.Notes, n => n.Contains("touched only a flag", StringComparison.Ordinal));
    }
}
