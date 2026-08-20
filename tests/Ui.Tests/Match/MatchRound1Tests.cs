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
        // The response feasibility and the solutions are computed on a worker now; a test asserting
        // on either is asserting on nothing until that pass has landed.
        designer.WaitForAnalysis();
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

    /// <summary>
    /// <b>An absorbed element is NOT dimmed</b> — Round 3 (owner, 2026-08-20: "do not render any
    /// component as dimmed; all components should render the same brightness, even the components
    /// that represent the absorbed parasitic"). This test asserted the opposite in Round 1 and is
    /// kept, inverted, because the property is the one that changed rather than one that went away.
    /// </summary>
    [Fact]
    public void AnAbsorbedElement_IsDrawnLikeEveryOtherElement()
    {
        var e = new MatchLadderElement("C1", ElementType.C, true, 1e-12,
                                       MatchElementRole.Absorbed, 0, 0, "1 pF");
        var ordinary = e with { Role = MatchElementRole.Normal };

        Assert.Equal(ordinary.ColorRoleKey,     e.ColorRoleKey);
        Assert.Equal(ordinary.ValueColorRoleKey, e.ValueColorRoleKey);
        Assert.Equal(ordinary.NameColorRoleKey,  e.NameColorRoleKey);
        Assert.Equal(ColorRole.SchematicSymbolLine, e.ColorRoleKey);
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
    /// <b>Each end of the network is a grounded termination, and there is no interface pin anywhere
    /// in the drawing</b> (owner, 2026-08-20: "remove the pins from the Match Designer schematic;
    /// instead place a TermG at each end of the network — left side instance name is 'Termination 1',
    /// right side is 'Termination 2'").
    /// </summary>
    /// <remarks>
    /// Rounds 1 and 2 asserted the opposite of this — four <c>Pin</c>s, two of them reference
    /// terminals on a ground rail. Both the rail and the pins are gone: a <c>TermG</c> IS the
    /// reference, so nothing is left for a reference pin to mark, and each <c>TermG</c>'s own "+"
    /// terminal lands exactly on the end of the spine.
    ///
    /// <para><b>Round 4 turned them upright</b> (owner, 2026-08-20: "the TermG components need to be
    /// rotated 90 deg with the ground at the bottom"). The assertion below is amended rather than
    /// duplicated in a round-4 file: it is the same fact — where each end's termination is — and two
    /// tests disagreeing about it is how one of them goes stale unnoticed.</para>
    /// </remarks>
    [Fact]
    public void TheProjection_PutsAGroundedTerminationOnEachEnd_AndNoPins()
    {
        var (_, _, d) = Open();
        var model = MatchSchematicModel.Build(d.Ladder);

        Assert.DoesNotContain(model.Components, c => c.Symbol == SymbolKind.Pin);

        var terms = model.Components.Where(c => c.Symbol == SymbolKind.TermG)
                                    .OrderBy(c => c.X).ToList();
        Assert.Equal(2, terms.Count);

        var (left, right) = (terms[0], terms[1]);
        Assert.Equal("Termination 1", left.InstanceName);
        Assert.Equal("Termination 2", right.InstanceName);

        // TermG's own pin is local (0, −200) and at R0 that IS the world offset, so each glyph hangs
        // a lead-length BELOW the spine end it terminates, ground bars pointing down — and its pin
        // lands on the spine with no wire between them, exactly as a shunt arm does.
        Assert.Equal(SymbolRotation.R0, left.Rotation);
        Assert.Equal(SymbolRotation.R0, right.Rotation);
        Assert.Equal(d.Ladder.PortLeftX,  left.X,  6);
        Assert.Equal(d.Ladder.PortRightX, right.X, 6);
        Assert.All(terms, t => Assert.Equal(MatchLadderLayout.SpineY + 200, t.Y, 6));

        // Both are labelled the schematic's own three rows, the third carrying the port reference.
        Assert.All(terms, t => Assert.Equal(3, t.Labels.Count));
        Assert.Equal("TermG", left.Labels[0]);
        Assert.StartsWith("Z = ", right.Labels[2], StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Every shunt arm carries its own GND, and no wire runs below one</b> (owner, 2026-08-20:
    /// "remove all the 'ground' wires for the shunt components; ground each such component with its
    /// own GND component"). <c>Ground</c>'s pin is at its local origin, so one placed a lead-length
    /// below a shunt element's centre sits ON that element's lower pin — there is nothing to wire.
    /// </summary>
    [Fact]
    public void EveryShuntArm_CarriesItsOwnGround_WithNoWireBelowIt()
    {
        var (_, _, d) = Open();
        var model = MatchSchematicModel.Build(d.Ladder);

        var shunts = d.Ladder.Elements.Where(e => e.IsShunt).ToList();
        Assert.NotEmpty(shunts);

        var grounds = model.Components.Where(c => c.Symbol == SymbolKind.Ground).ToList();
        Assert.Equal(shunts.Count, grounds.Count);

        foreach (var e in shunts)
        {
            var g = Assert.Single(grounds, c => Math.Abs(c.X - e.X) < 1e-9);
            Assert.Equal(e.Y + MatchSchematicGeometry.LeadHalf, g.Y, 6);
            // Neither the type label nor the instance name is drawn — the glyph says it all.
            Assert.Empty(g.Labels);
        }

        // Nothing at all is drawn below a shunt element's lower pin.
        double floor = shunts[0].Y + MatchSchematicGeometry.LeadHalf;
        Assert.DoesNotContain(model.Wires, w =>
            w.Points.Any(pt => pt.Y > floor - 1e-9));
    }

    /// <summary>
    /// <b>The network pane is a circuitRF schematic, not a drawing of one</b> (owner, 2026-08-19).
    /// Every element becomes a placed component of the editor's own built-in kind, at the editor's
    /// own grid size, labelled with the editor's own three rows — so the colours, the glyphs and the
    /// label roles are the schematic's by construction rather than by a second implementation
    /// agreeing with the first.
    /// </summary>
    [Fact]
    public void TheProjection_IsARealSchematicModel()
    {
        var (_, _, d) = Open();
        var model = MatchSchematicModel.Build(d.Ladder);

        Assert.Equal(100.0, model.GridSize);
        Assert.NotEmpty(model.Wires);

        foreach (var e in d.Ladder.Elements)
        {
            var c = Assert.Single(model.Components, x => x.Id == e.Name);
            Assert.Equal(e.Type == ElementType.L ? SymbolKind.Inductor : SymbolKind.Capacitor, c.Symbol);

            // A shunt arm keeps the built-in glyph's own vertical orientation; a series arm is the
            // SAME glyph at R270 — the rotation MatchFlatten writes, so the Designer's drawing and
            // the cell it flattens into are the same drawing (owner, 2026-08-20).
            Assert.Equal(e.IsShunt ? SymbolRotation.R0 : SymbolRotation.R270, c.Rotation);
            Assert.Equal(MatchSchematicModel.SeriesRotation, SymbolRotation.R270);

            // Type, instance, value — the schematic's three label rows, in the schematic's order.
            Assert.Equal(3, c.Labels.Count);
            Assert.Equal(e.Type == ElementType.L ? "L" : "C", c.Labels[0]);
            Assert.Equal(e.Name, c.Labels[1]);
            Assert.Contains(e.ValueText, c.Labels[2], StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The spine is drawn in the GAPS between series elements, never through them — a built-in glyph
    /// carries its own leads out to ±200, so a port-to-port line would lay a second wire across every
    /// series body.
    /// </summary>
    [Fact]
    public void TheProjection_LeavesNoWireUnderASeriesBody()
    {
        var (_, _, d) = Open();
        var model = MatchSchematicModel.Build(d.Ladder);

        foreach (var e in d.Ladder.Elements.Where(x => !x.IsShunt))
        {
            var horizontals = model.Wires.Where(w =>
                Math.Abs(w.Points[0].Y - MatchLadderLayout.SpineY) < 1e-9 &&
                Math.Abs(w.Points[1].Y - MatchLadderLayout.SpineY) < 1e-9);
            foreach (var w in horizontals)
            {
                double x0 = Math.Min(w.Points[0].X, w.Points[1].X);
                double x1 = Math.Max(w.Points[0].X, w.Points[1].X);
                Assert.False(x0 < e.X - 200 + 1e-9 && x1 > e.X + 200 - 1e-9,
                             $"a spine wire runs straight through {e.Name}");
            }
        }
    }

    /// <summary>
    /// Every colour the pane uses is a theme ROLE, and the two the schematic has no way to express
    /// stay where the meaning lives — on the layout element, which the renderer's overlay reads.
    /// </summary>
    [Fact]
    public void ThePane_UsesTheSchematicColourRoles()
    {
        string src = System.IO.File.ReadAllText(System.IO.Path.Combine(
            RepoRoot(), "src", "Ui", "Views", "Match", "MatchSchematicCanvas.cs"));

        // The schematic's own colours arrive as a whole token bundle rather than one role at a time
        // — which is the point: there is no second list of roles here to fall behind the editor's.
        Assert.Contains("SchematicRenderTheme.FromTheme", src, StringComparison.Ordinal);
        Assert.Contains("SchematicRenderer.Draw", src, StringComparison.Ordinal);

        // The one exception left, saying something a schematic cannot: which value is unbuildable.
        Assert.Contains("ColorRole." + nameof(ColorRole.MatchNegative), src, StringComparison.Ordinal);

        // The BRACE picks no colour of its own at all any more — it asks the bracket record, which
        // answers with two of the schematic's own roles (owner, 2026-08-20).
        Assert.Contains("Role(b.ColorRoleKey)", src, StringComparison.Ordinal);
        Assert.Contains("Role(b.LabelColorRoleKey)", src, StringComparison.Ordinal);
        var bracket = new MatchTransformBracket("N1", 0, 0, 100, 0);
        Assert.Equal(ColorRole.SchematicParameterNameText, bracket.ColorRoleKey);
        Assert.Equal(ColorRole.SchematicComponentNameText, bracket.LabelColorRoleKey);

        // Nothing dims anything: the wash that used to be painted over an absorbed glyph is gone.
        Assert.DoesNotContain("ColorRole." + nameof(ColorRole.MatchAbsorbed), src, StringComparison.Ordinal);

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
            d.WaitForAnalysis();
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
        // The box takes the control's OWN content alignment (Round 2: a right-aligned properties row
        // must not jump to the left edge of its column the moment the editor opens) — the default is
        // still Left, so this is the same behaviour every existing call site had.
        Assert.Contains("HorizontalAlignment = HorizontalContentAlignment", src, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment.Left", src, StringComparison.Ordinal);
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
