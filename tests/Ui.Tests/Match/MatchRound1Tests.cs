// ================================================================
//  MatchRound1Tests.cs  —  the owner's 2026-08-19 round-1 list for the Match component.
//
//  View-model and geometry tests, not pixel tests — the same discipline MatchDesignerTests keeps.
//  Where the ask is genuinely about a drawing (the symbol's slashes, the ladder's wiring, the
//  pictogram's orientation) the assertion is made against the geometry the renderer reads.
// ================================================================

using System;
using System.Linq;
using CircuitRF.Core.Matching;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.Matching;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.Match;

public sealed class MatchRound1Tests
{
    private static (SchematicViewModel Vm, EditableComponent Comp, MatchDesignerViewModel Designer)
        Open(MatchDesign? design = null)
    {
        var model = new SchematicEditModel();
        var comp = new EditableComponent { InstanceName = "MN1", Symbol = SymbolKind.Match, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Match, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        if (design is not null)
            comp.Parameters.First(p => p.Name == "Design").Expression = MatchEmbedding.Encode(design);
        model.Components.Add(comp);
        var vm = new SchematicViewModel(model);
        var designer = new MatchDesignerViewModel();
        designer.SetTarget(vm, comp);
        return (vm, comp, designer);
    }

    // ── The symbol: shorter slashes, sloping DOWN left-to-right ────────────────

    /// <summary>
    /// "The Match symbol needs to have its upper and lower angled lines much shorter and sloped in
    /// the opposite direction... from left to right, slashes need to be angled downward."
    /// </summary>
    /// <remarks>
    /// Downward-to-the-right means y INCREASES with x — symbol space puts y positive downward, which
    /// is why the top wave sits at y = −55. The length bound is against the previous geometry
    /// (±55 in x, 50 in y ⇒ ~121 units); "much shorter", then "make them half as long", lands at
    /// ~38 units — so the bound is 50.
    /// </remarks>
    [Fact]
    public void TheSymbolSlashes_SlopeDownwardToTheRight_AndAreMuchShorter()
    {
        var slashes = BuiltInSymbols.Primitives(SymbolKind.Match).Primitives
            .OfType<LinePrimitive>()
            .Where(l => l.X1 != l.X2 && l.Y1 != l.Y2)
            .ToList();

        Assert.Equal(2, slashes.Count);
        foreach (var l in slashes)
        {
            // Normalise so the first point is the left-hand one, then the right-hand y must be lower
            // on the page — a larger y.
            (double x1, double y1, double x2, double y2) = l.X1 <= l.X2
                ? (l.X1, l.Y1, l.X2, l.Y2)
                : (l.X2, l.Y2, l.X1, l.Y1);
            Assert.True(y2 > y1, $"({x1},{y1})->({x2},{y2}) does not slope downward to the right");

            double length = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
            Assert.True(length < 50.0, $"slash length {length:F1} is not half of 'much shorter'");
        }
    }

    // ── Formatting: three digits, and a real ∞ ────────────────────────────────

    /// <summary>"Set the default significant digits for the network component readout to 3."</summary>
    [Fact]
    public void TheNetworkReadout_DefaultsToThreeSignificantDigits()
    {
        Assert.Equal(3, new MatchDesignerSettings().SignificantDigits);
        // Three significant digits, and it ROUNDS rather than truncating: 153.5169 pH -> 154 pH.
        Assert.Equal("154 pH",
            MatchValueFormat.FormatWithUnit(153.5169e-12, MatchQuantity.Inductance, "Auto", 3));
        Assert.Equal("1.23 nH",
            MatchValueFormat.FormatWithUnit(1.23456e-9, MatchQuantity.Inductance, "Auto", 3));
    }

    /// <summary>
    /// <b>The entry fields do NOT round to three.</b> A field the user types into has to round-trip
    /// what was typed; a readout does not.
    /// </summary>
    [Fact]
    public void TheEntryFields_KeepEnoughDigitsToRoundTrip()
    {
        var (_, _, d) = Open(new MatchDesign
        {
            F1 = 1.23456e9, F2 = 2e9, Order = 3,
            Term1 = Termination.Resistive(50), Term2 = Termination.Resistive(50),
        });
        Assert.StartsWith("1.23456", d.F1Entry, StringComparison.Ordinal);
    }

    /// <summary>"Use a real infinity symbol instead of the words 'infinity'."</summary>
    [Fact]
    public void AnInfiniteValue_RendersAsTheGlyph_NeverTheWord()
    {
        string text = MatchValueFormat.FormatWithUnit(
            double.PositiveInfinity, MatchQuantity.Capacitance, "pF", 3);
        Assert.Contains("∞", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", text, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("-∞",
            MatchValueFormat.FormatWithUnit(double.NegativeInfinity, MatchQuantity.Capacitance, "pF", 3),
            StringComparison.Ordinal);

        // NaN keeps its own spelling: it is not an infinity, and saying so would misreport which
        // failure happened.
        Assert.Contains("NaN",
            MatchValueFormat.FormatWithUnit(double.NaN, MatchQuantity.Capacitance, "pF", 3),
            StringComparison.Ordinal);
    }

    // ── The ladder drawing ────────────────────────────────────────────────────

    /// <summary>
    /// "A component that has an invalid value should have its value rendered in 'bad' colour, not the
    /// whole component."
    /// </summary>
    [Fact]
    public void AnOutOfRangeElement_RedensItsValueOnly_NotItsSymbol()
    {
        var bad = new MatchLadderElement("C9", ElementType.C, false, -1e-12,
                                         MatchElementRole.OutOfRange, 0, 0, "-1 pF");
        Assert.Equal(ColorRole.SchematicSymbolLine, bad.ColorRoleKey);
        Assert.Equal(ColorRole.MatchNegative, bad.ValueColorRoleKey);

        var ok = bad with { Role = MatchElementRole.Normal };
        Assert.Equal(ColorRole.SchematicSymbolLine, ok.ColorRoleKey);
        Assert.Equal(ColorRole.SchematicParameterNameText, ok.ValueColorRoleKey);
        Assert.Equal(ColorRole.SchematicInstanceNameText, ok.NameColorRoleKey);
    }

    /// <summary>An absorbed element is still dimmed — the legend depends on it saying so.</summary>
    [Fact]
    public void AnAbsorbedElement_StaysDimmed()
    {
        var e = new MatchLadderElement("C1", ElementType.C, true, 1e-12,
                                       MatchElementRole.Absorbed, 0, 0, "1 pF");
        Assert.Equal(ColorRole.MatchAbsorbed, e.ColorRoleKey);
        Assert.Equal(ColorRole.MatchAbsorbed, e.ValueColorRoleKey);
    }

    /// <summary>
    /// "The horizontal spacing between components in the network diagram is too small." The pitch has
    /// to clear a label — and a label's width in WORLD units is fixed, since the font size is a fixed
    /// multiple of the drawing's own scale.
    /// </summary>
    [Fact]
    public void TheColumnPitch_ClearsAShuntLabel()
    {
        // A shunt label starts 130 world units right of its column and the widest realistic string
        // ("153 pH" at ~90 world units per em, roughly 0.55 em per character) runs ~300 units.
        const double labelStart = 130.0, labelWidth = 300.0;
        Assert.True(MatchLadderLayout.Pitch > labelStart + labelWidth,
                    $"pitch {MatchLadderLayout.Pitch} does not clear a shunt label");
    }

    /// <summary>
    /// The ladder's series elements sit a full lead-length apart from the ports, which is what lets
    /// the renderer draw the spine in the GAPS rather than through the symbol bodies.
    /// </summary>
    [Fact]
    public void SeriesElements_DoNotOverlapEachOtherOrThePorts()
    {
        var (_, _, d) = Open();
        var layout = d.Ladder;
        Assert.NotEmpty(layout.Elements);

        var series = layout.Elements.Where(e => !e.IsShunt).OrderBy(e => e.X).ToList();
        foreach (var e in series)
        {
            Assert.True(e.X - 200 > layout.PortLeftX, "a series element runs into the left port");
            Assert.True(e.X + 200 < layout.PortRightX, "a series element runs into the right port");
        }
        for (int i = 1; i < series.Count; i++)
            Assert.True(series[i].X - 200 >= series[i - 1].X + 200,
                        "two series elements overlap, so the spine has no gap to draw in");
    }

    /// <summary>
    /// <b>The interface pins actually draw.</b> The Pin glyph is a hexagon POLYGON plus one stem
    /// line, and the preview's primitive walker had no polygon case — so the hexagon drew as
    /// nothing and the stem landed exactly on the wire it connects to, which is why the owner saw no
    /// pins at all rather than half of one. Nothing else this preview draws is a polygon, which is
    /// how the gap went unnoticed.
    /// </summary>
    /// <remarks>
    /// Asserted by reading the source: <c>Ui.Tests</c> has no live Avalonia application, so a
    /// <c>DrawingContext</c> cannot be created and a render cannot be exercised. What is checkable is
    /// that the glyph IS a polygon and that the walker has a case for one — the two halves whose
    /// disagreement was the bug.
    /// </remarks>
    [Fact]
    public void ThePreview_DrawsPolygons_WhichIsWhatAPinIsMadeOf()
    {
        Assert.Contains(BuiltInSymbols.Primitives(SymbolKind.Pin).Primitives,
                        prim => prim is PolygonPrimitive);

        string src = System.IO.File.ReadAllText(System.IO.Path.Combine(
            RepoRoot(), "src", "Ui", "Views", "Match", "MatchLadderPreview.cs"));
        Assert.Contains("case PolygonPrimitive", src, StringComparison.Ordinal);
        Assert.Contains("SymbolKind.Pin", src, StringComparison.Ordinal);
    }

    /// <summary>
    /// The preview paints the schematic's own background and labels each element with the schematic's
    /// own three text roles — type, instance, value (owner, 2026-08-19).
    /// </summary>
    [Fact]
    public void ThePreview_UsesTheSchematicColourRoles()
    {
        string src = System.IO.File.ReadAllText(System.IO.Path.Combine(
            RepoRoot(), "src", "Ui", "Views", "Match", "MatchLadderPreview.cs"));

        foreach (string role in new[]
        {
            nameof(ColorRole.SchematicBackground),
            nameof(ColorRole.SchematicWire),
            nameof(ColorRole.SchematicSymbolLine),
            nameof(ColorRole.SchematicComponentNameText),
            nameof(ColorRole.SchematicConnectedPin),
        })
            Assert.Contains("ColorRole." + role, src, StringComparison.Ordinal);

        // Instance and parameter roles reach the renderer through the layout element, which is where
        // the meaning-to-colour mapping lives.
        var e = new MatchLadderElement("L2", ElementType.L, false, 1e-9,
                                       MatchElementRole.Normal, 0, 0, "1 nH");
        Assert.Equal(ColorRole.SchematicInstanceNameText, e.NameColorRoleKey);
        Assert.Equal(ColorRole.SchematicParameterNameText, e.ValueColorRoleKey);
    }

    // ── The specification pane ────────────────────────────────────────────────

    /// <summary>Termination 1's resistor is on the LEFT branch of a parallel pictogram, 2's on the right.</summary>
    [Fact]
    public void ThePictogram_PutsTermination1sResistorOnTheLeft()
    {
        var (_, _, d) = Open();
        Assert.True(d.Term1.ResistorOnLeft);
        Assert.False(d.Term2.ResistorOnLeft);
    }

    /// <summary>The inline editor's entry string carries the unit, and a typed unit is honoured.</summary>
    [Fact]
    public void TheResistanceEntry_CarriesItsUnit_AndAcceptsATypedOne()
    {
        var (_, _, d) = Open();
        Assert.Equal("50 Ω", d.Term1.ResistanceEntry);

        d.Term1.ResistanceEntry = "1.2 kΩ";
        Assert.Equal(1200.0, d.Term1.Resistance, 9);
        Assert.Equal("kΩ", d.Term1.ResistanceUnit);

        // A bare number keeps the field's current unit.
        d.Term1.ResistanceEntry = "2";
        Assert.Equal(2000.0, d.Term1.Resistance, 9);
    }

    /// <summary>A unit that belongs to a different dimension is refused, not silently reinterpreted.</summary>
    [Fact]
    public void AWrongDimensionUnit_IsRefused()
    {
        var (_, _, d) = Open();
        double before = d.Term1.Resistance;
        d.Term1.ResistanceEntry = "3 nH";
        Assert.Equal(before, d.Term1.Resistance);
    }

    /// <summary>Both band edges share one display unit, so a unit typed into either moves both.</summary>
    [Fact]
    public void ABandUnitTypedIntoOneEdge_MovesBoth()
    {
        var (_, _, d) = Open();
        d.F1Entry = "900 MHz";
        Assert.Equal(900e6, d.F1, 1e-3);
        Assert.Equal("MHz", d.BandUnit);
        Assert.EndsWith("MHz", d.F2Entry, StringComparison.Ordinal);
    }

    /// <summary>
    /// The order field is an inline editor now, and an order the terminations cannot absorb is
    /// REFUSED with the permitted set named — the same refusal the ComboBox made by not listing it.
    /// </summary>
    [Fact]
    public void TheOrderEntry_RefusesAnOrderTheParityForbids()
    {
        var (_, _, d) = Open(new MatchDesign
        {
            F1 = 1e9, F2 = 2e9, Order = 3,
            Term1 = new Termination(200.0, ReactanceKind.C, TerminationTopology.Parallel, 1e-12),
            Term2 = new Termination(20.0, ReactanceKind.C, TerminationTopology.Parallel, 5e-12),
        });

        var valid = d.OrderOptions;
        int forbidden = Enumerable.Range(2, 5).First(n => !valid.Contains(n));
        int before = d.Order;

        d.OrderEntry = forbidden.ToString(System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(before, d.Order);
        Assert.Contains(string.Join(", ", valid), d.OrderNote, StringComparison.Ordinal);

        // A permitted one goes straight through.
        int allowed = valid.First(n => n != before);
        d.OrderEntry = allowed.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(allowed, d.Order);
    }

    /// <summary>
    /// The Response selector is a ComboBox now; picking an infeasible family is refused and SAYS so
    /// rather than reverting in silence.
    /// </summary>
    [Fact]
    public void SelectingAnInfeasibleResponse_IsRefusedOutLoud()
    {
        var (_, _, d) = Open(new MatchDesign
        {
            F1 = 3.3e9, F2 = 5.0e9, Order = 4,
            Term1 = new Termination(200.0, ReactanceKind.C, TerminationTopology.Parallel, 0.125e-12),
            Term2 = new Termination(1.25, ReactanceKind.C, TerminationTopology.Series, 10e-12),
        });

        var blocked = d.ResponseOptions.FirstOrDefault(o => !o.IsEnabled);
        Assert.NotNull(blocked);
        Assert.Equal(0.45, blocked!.ListOpacity, 3);

        var before = d.Response;
        d.SelectedResponseOption = blocked;
        Assert.Equal(before, d.Response);
        Assert.False(string.IsNullOrWhiteSpace(d.ResponseRefusal));

        // The selection property still reports the design's OWN response, not the rejected pick.
        Assert.Equal(before, d.SelectedResponseOption!.Shape);
    }

    // ── The default design opens clean ────────────────────────────────────────

    /// <summary>
    /// "The default settings need to show solutions and not have any immediate dead ends for the user
    /// to play with the UI."
    /// </summary>
    [Fact]
    public void TheShippedDefault_OpensWithSolutions_AndNoRefusalAnywhere()
    {
        var (_, _, d) = Open();

        Assert.NotEmpty(d.Solutions);
        Assert.Equal("", d.SolutionsRefusal);
        Assert.Equal("", d.PayloadError);
        Assert.Equal("", d.ResponseError);
        Assert.Empty(d.Notes);

        // No refusal, no unreached transform target, and therefore no flagged termination.
        Assert.Null(d.Status.Refusal);
        Assert.True(d.Status.OnTarget);
        Assert.False(d.Term1.IsFlagged);
        Assert.False(d.Term2.IsFlagged);

        // Every response family is pickable, and no element is out of range.
        Assert.All(d.ResponseOptions, o => Assert.True(o.IsEnabled, $"{o.Display} is refused"));
        Assert.False(d.Ladder.HasOutOfRange);
    }

    /// <summary>
    /// <b>And every order the picker offers is a live one.</b> The former default's own first entry
    /// (order 2) returned nothing, which is the dead end the owner hit.
    /// </summary>
    [Fact]
    public void TheShippedDefault_HasSolutionsAtEveryOrderItOffers()
    {
        var (_, _, d) = Open();
        foreach (int n in d.OrderOptions.ToList())
        {
            d.Order = n;
            Assert.True(d.Solutions.Count > 0, $"order {n} is offered but returns no solutions");
            Assert.Equal("", d.SolutionsRefusal);
        }
    }

    // ── The shared inline-edit helpers ────────────────────────────────────────

    /// <summary>
    /// The editor opens on a DOUBLE click, not a single one, and it sizes itself to the text rather
    /// than stretching to its slot; a click anywhere outside it commits and closes it.
    /// </summary>
    /// <remarks>
    /// Source-scanned for the same reason as everything else about this control here — no live
    /// Avalonia application, so no control tree to gesture at. Each assertion names the specific
    /// mechanism the owner reported missing, not merely that the file mentions the word.
    /// </remarks>
    [Fact]
    public void TheInlineEditor_OpensOnDoubleClick_SizesToItsText_AndClosesOnAnOutsideClick()
    {
        string src = System.IO.File.ReadAllText(System.IO.Path.Combine(
            RepoRoot(), "src", "Ui", "Controls", "InlineEditText.cs"));

        Assert.Contains("DoubleTapped +=", src, StringComparison.Ordinal);
        Assert.DoesNotContain("PointerPressed += ", src, StringComparison.Ordinal);

        // Explicit width from a measurement, re-measured as the user types, and never stretched.
        Assert.Contains("HorizontalAlignment = HorizontalAlignment.Left", src, StringComparison.Ordinal);
        Assert.Contains("Width = InlineEdit.MeasureWidth(_pristine, FontSize, typeface)", src,
                        StringComparison.Ordinal);
        Assert.Contains("box.TextChanged +=", src, StringComparison.Ordinal);

        // A click outside is caught by TUNNELLING from the top level: LostFocus alone never fires
        // when the thing clicked cannot take focus.
        Assert.Contains("RoutingStrategies.Tunnel", src, StringComparison.Ordinal);
        Assert.Contains("TopLevel.GetTopLevel(this)", src, StringComparison.Ordinal);
    }

    /// <summary>"It selects units within the text properly" — the number, never the unit.</summary>
    [Theory]
    [InlineData("50 Ω", 2)]
    [InlineData("1.5 nH", 3)]
    [InlineData("-1.5e-9 F", 7)]
    [InlineData("12", 2)]
    [InlineData("", 0)]
    public void TheInlineEditor_SelectsTheValueAndLeavesTheUnit(string text, int expected)
        => Assert.Equal(expected, InlineEdit.ValueSelectionLength(text));

    /// <summary>
    /// The width is a MEASUREMENT, shared with harmonicaRF's strip rather than copied.
    /// </summary>
    /// <remarks>
    /// Asserted by reading the source, not by calling it: <c>FormattedText</c> resolves
    /// <c>IFontManagerImpl</c> the moment it measures, and <c>Ui.Tests</c> runs with no live Avalonia
    /// application to provide one. What is checkable here is that there is ONE formula and that it
    /// measures — the numbers it produces are exercised by the running application.
    /// </remarks>
    [Fact]
    public void TheInlineEditorWidth_IsMeasured_AndSharedWithHarmonica()
    {
        string root = RepoRoot();
        string shared = System.IO.File.ReadAllText(
            System.IO.Path.Combine(root, "src", "Ui", "Controls", "InlineEdit.cs"));
        Assert.Contains("new FormattedText(", shared, StringComparison.Ordinal);
        Assert.DoesNotContain("* 0.55", shared, StringComparison.Ordinal);

        string strip = System.IO.File.ReadAllText(System.IO.Path.Combine(
            root, "src", "Ui", "Views", "Harmonica", "ReadoutStripView.axaml.cs"));
        Assert.Contains("InlineEdit.MeasureWidth(text, fontSize, typeface)", strip, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    // ── The golden-ratio plot frames ──────────────────────────────────────────

    /// <summary>"The two rect plots need to use the Golden aspect ratio."</summary>
    [Fact]
    public void TheAspectRatioPanel_DerivesHeightFromWidth()
    {
        var fit = AspectRatioPanel.Fit(new Avalonia.Size(360, double.PositiveInfinity),
                                       AspectRatioPanel.Golden);
        Assert.Equal(360.0, fit.Width, 6);
        Assert.Equal(360.0 / AspectRatioPanel.Golden, fit.Height, 6);

        // When the height is the binding constraint, the width follows it instead.
        var tall = AspectRatioPanel.Fit(new Avalonia.Size(1000, 100), AspectRatioPanel.Golden);
        Assert.Equal(100.0, tall.Height, 6);
        Assert.Equal(100.0 * AspectRatioPanel.Golden, tall.Width, 6);

        // Nothing bounding it at all is degenerate, and must not propagate an infinity into arrange.
        var none = AspectRatioPanel.Fit(
            new Avalonia.Size(double.PositiveInfinity, double.PositiveInfinity), AspectRatioPanel.Golden);
        Assert.Equal(0.0, none.Width);
        Assert.Equal(0.0, none.Height);
    }
}
