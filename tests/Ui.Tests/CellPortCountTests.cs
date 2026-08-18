using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Owner, 2026-08-17: a cell instance placed by Update Schematic from Layout "does not render the
/// pins." Generating the missing symbol only fixes that if the generated symbol has the RIGHT number of
/// pins — and the port count it was read from, <c>.ccell NumPorts</c>, is a field nothing in circuitRF
/// ever derives. A cell whose schematic the user drew with N <c>Pin</c> components, and whose cell
/// editor they never opened, declares zero; the auto-symbol generator's fixed fallback then made a
/// two-pin symbol for it however many pins the cell really had.
/// </summary>
public sealed class CellPortCountTests : IDisposable
{
    private readonly string _root;

    public CellPortCountTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-portcount-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <param name="pinNums">One entry per Pin component; null means the pin declares no Num.</param>
    private string MakeCell(string name, int? declaredNumPorts, params int?[] pinNums)
    {
        string cellDir = CellFolder.CreateCellFolder(_root, name);

        if (declaredNumPorts is { } n)
        {
            string ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
            var ccell = CellPersistence.LoadFromFile(ccellPath);
            ccell.NumPorts = n;
            CellPersistence.SaveToFile(ccellPath, ccell);
        }

        if (pinNums.Length > 0)
        {
            var model = new SchematicEditModel();
            for (int i = 0; i < pinNums.Length; i++)
            {
                var comp = new EditableComponent { InstanceName = $"Pin{i + 1}", Symbol = SymbolKind.Pin, X = i * 200 };
                if (pinNums[i] is { } num)
                    comp.Parameters.Add(new EditableParameter { Name = "Num", Expression = num.ToString() });
                model.Components.Add(comp);
            }
            // A non-port component alongside them, so "count the Pins" is actually being tested rather
            // than "count the components".
            model.Components.Add(new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor });

            string schDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
            SchematicPersistence.SaveToFile(Path.Combine(schDir, name + ".csch"), model, cellName: name);
        }

        return cellDir;
    }

    [Fact]
    public void ADeclaredPortCountWins()
    {
        string cellDir = MakeCell("Declared", declaredNumPorts: 3, 1, 2);
        Assert.Equal(3, CellPortCount.Resolve(cellDir));   // the user set it and meant it
    }

    /// <summary>The reported case: a real schematic with pins, and a NumPorts nobody ever filled in.</summary>
    [Fact]
    public void AnUndeclaredCountFallsBackToTheSchematicsOwnPins()
    {
        string cellDir = MakeCell("FromSchematic", declaredNumPorts: 0, 1, 2, 3, 4);
        Assert.Equal(4, CellPortCount.Resolve(cellDir));   // not 2, and not 0
    }

    /// <summary>Pins numbered 1, 2 and 4 mean FOUR ports with the third left open. Answering "three"
    /// would quietly renumber the user's own port 4 into a port 3 that connects somewhere else.</summary>
    [Fact]
    public void AGapInThePortNumbersCountsToTheHighest_NotToThePinCount()
    {
        string cellDir = MakeCell("Gapped", declaredNumPorts: null, 1, 2, 4);
        Assert.Equal(4, CellPortCount.Resolve(cellDir));
    }

    /// <summary>And the converse: pins that declare no number at all are still ports, so the count can
    /// never come out below the number of pins on the schematic.</summary>
    [Fact]
    public void UnnumberedPinsStillCount()
    {
        string cellDir = MakeCell("Unnumbered", declaredNumPorts: null, null, null, null);
        Assert.Equal(3, CellPortCount.Resolve(cellDir));

        string mixed = MakeCell("Mixed", declaredNumPorts: null, 1, null, null);
        Assert.Equal(3, CellPortCount.Resolve(mixed));
    }

    [Fact]
    public void ACellWithNeitherSourceAnswersZero_LeavingTheFallbackToTheOnePlaceThatStatesIt()
    {
        string cellDir = MakeCell("Bare", declaredNumPorts: null);
        Assert.Equal(0, CellPortCount.Resolve(cellDir));

        // AutoSymbolGenerator is that place, and it is unchanged: <= 0 means 2.
        Assert.Equal(2, AutoSymbolGenerator.Generate("Bare", 0).Pins.Count);
    }

    /// <summary>The composition the two callers actually use, end to end — this is the pairing that
    /// decides how many pins a generated symbol has, and either half being right on its own is not
    /// enough.</summary>
    [Fact]
    public void GeneratingASymbolForACellWithFourPins_ProducesFourPins()
    {
        string cellDir = MakeCell("FourPort", declaredNumPorts: 0, 1, 2, 3, 4);

        var symbol = AutoSymbolGenerator.Generate(Path.GetFileName(cellDir), CellPortCount.Resolve(cellDir));

        Assert.Equal(4, symbol.Pins.Count);
        Assert.Equal([0, 1, 2, 3], symbol.Pins.Select(p => p.PortIndex).OrderBy(i => i));
    }

    [Fact]
    public void AMissingOrUnreadableCellAnswersZeroRatherThanThrowing()
    {
        Assert.Equal(0, CellPortCount.Resolve(Path.Combine(_root, "does-not-exist")));
        Assert.Null(CellPortCount.FromCcell(Path.Combine(_root, "does-not-exist")));
        Assert.Null(CellPortCount.FromSchematic(Path.Combine(_root, "does-not-exist")));
    }

    [Fact]
    public void ADisabledPinIsNotAPort()
    {
        string cellDir = CellFolder.CreateCellFolder(_root, "Disabled");
        var model = new SchematicEditModel();
        model.Components.Add(new EditableComponent
        {
            InstanceName = "Pin1", Symbol = SymbolKind.Pin,
            Parameters = { new EditableParameter { Name = "Num", Expression = "1" } },
        });
        model.Components.Add(new EditableComponent
        {
            InstanceName = "Pin2", Symbol = SymbolKind.Pin, Disable = DisableState.Open,
            Parameters = { new EditableParameter { Name = "Num", Expression = "2" } },
        });
        SchematicPersistence.SaveToFile(
            Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Schematic), "Disabled.csch"),
            model, cellName: "Disabled");

        Assert.Equal(1, CellPortCount.Resolve(cellDir));
    }
}

/// <summary>
/// Owner, 2026-08-17: the auto-generated symbol's port labels "are smaller than the font size for SNP
/// components." They were a private 12.0 in <c>AutoSymbolGenerator</c> while every other dynamic symbol
/// had been raised to <c>BuiltInSymbols.SddPortLabelFontSize</c>, and nothing held the two together.
/// </summary>
public sealed class AutoSymbolLabelStyleTests
{
    private static IEnumerable<TextPrimitive> LabelsOf(Symbol s) => s.Primitives.OfType<TextPrimitive>();

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]   // the N=3 special layout
    [InlineData(4)]
    [InlineData(7)]
    public void PortLabels_UseTheSameFontSizeAsEveryOtherDynamicSymbol(int numPorts)
    {
        var symbol = AutoSymbolGenerator.Generate("Cell", numPorts);

        var labels = LabelsOf(symbol).ToList();
        Assert.Equal(numPorts, labels.Count);
        Assert.All(labels, t => Assert.Equal(BuiltInSymbols.SddPortLabelFontSize, t.FontSize));
    }

    /// <summary>SnP centres its labels on the lead they name. TextPrimitive defaults to Baseline, which
    /// hangs the glyph above the lead instead — invisible enough at 12, obvious at 18.</summary>
    [Fact]
    public void PortLabels_AreVerticallyCentredOnTheirLead_NotSittingOnItsBaseline()
    {
        var symbol = AutoSymbolGenerator.Generate("Cell", 4);
        Assert.All(LabelsOf(symbol), t => Assert.Equal(SymbolTextVAlign.Middle, t.VAlign));
    }

    /// <summary>The label must clear the INNER rect it sits inside, at the larger glyph. Checked as
    /// geometry rather than as the constant, so a later change to either inset is caught by the thing
    /// that actually matters.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(12)]   // two-digit port numbers, the widest labels this generates
    public void PortLabels_ClearTheInnerRect(int numPorts)
    {
        var symbol = AutoSymbolGenerator.Generate("Cell", numPorts);

        // The inner rect is the Thin-stroked one; the outer is Normal.
        var inner = symbol.Primitives.OfType<RectPrimitive>().Single(r => r.StrokeTier == SymbolStrokeTier.Thin);
        double innerLeft = inner.Cx - inner.W / 2, innerRight = inner.Cx + inner.W / 2;
        double innerTop  = inner.Cy - inner.H / 2, innerBottom = inner.Cy + inner.H / 2;

        foreach (var t in LabelsOf(symbol))
        {
            // Anchors sit strictly inside, with room left over rather than flush against the edge.
            Assert.True(t.AnchorX > innerLeft && t.AnchorX < innerRight,
                $"label anchor x={t.AnchorX} is outside the inner rect [{innerLeft}, {innerRight}]");
            Assert.True(t.AnchorY > innerTop && t.AnchorY < innerBottom,
                $"label anchor y={t.AnchorY} is outside the inner rect [{innerTop}, {innerBottom}]");

            // A left label flows right from its anchor and a right label flows left, so the clearance
            // that matters is between the anchor and the edge it is anchored against.
            double clearance = t.Align == SymbolTextAlign.Left ? t.AnchorX - innerLeft : innerRight - t.AnchorX;
            Assert.True(clearance >= 10, $"label clearance {clearance} from the inner rect is too tight");

            // Vertically, half a line of text must fit above and below the anchor.
            Assert.True(t.AnchorY - innerTop >= t.FontSize / 2, "label overruns the inner rect's top");
            Assert.True(innerBottom - t.AnchorY >= t.FontSize / 2, "label overruns the inner rect's bottom");
        }
    }
}
