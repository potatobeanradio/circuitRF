// ================================================================
//  MatchRound4Tests.cs  —  the owner's 2026-08-20 round-4 list for the Match Designer.
//
//  Same discipline as rounds 1-3: view-model, geometry and projection tests, never pixels. Where the
//  ask is about layout declared in AXAML, or about a rendering path that needs a live Avalonia
//  application, the assertion is made against the source the mechanism is written in and NAMES that
//  mechanism — a scan for "the file mentions the word" would pass over a broken fix.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Core.Matching;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Matching;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Match;

public sealed class MatchRound4Tests(ITestOutputHelper output)
{
    private static (SchematicViewModel Vm, EditableComponent Comp, MatchDesignerViewModel Designer)
        Open()
    {
        var model = new SchematicEditModel();
        var comp = new EditableComponent { InstanceName = "MN1", Symbol = SymbolKind.Match, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Match, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        model.Components.Add(comp);
        var vm = new SchematicViewModel(model);
        var designer = new MatchDesignerViewModel();
        designer.SetTarget(vm, comp);
        return (vm, comp, designer);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>
    /// One source file with its COMMENTS STRIPPED — every scan below is about what the code does, and
    /// a comment that quotes the thing it replaced is exactly the text a naive scan trips over.
    /// </summary>
    private static string Src(params string[] parts)
    {
        string raw = File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));
        raw = Regex.Replace(raw, @"<!--.*?-->", "", RegexOptions.Singleline);   // XML / AXAML
        raw = Regex.Replace(raw, @"/\*.*?\*/", "", RegexOptions.Singleline);    // C# block
        raw = Regex.Replace(raw, @"//[^\n]*", "", RegexOptions.None);           // C# line + XML doc
        return raw;
    }

    private static string Xaml() => Src("src", "Ui", "Views", "Match", "MatchDesignerWindow.axaml");
    private static string Code() => Src("src", "Ui", "Views", "Match", "MatchDesignerWindow.axaml.cs");

    // ══ The schematic: terminations, shunts, and no vertical wire ════════════

    /// <summary>
    /// <b>Both terminations stand upright with their ground at the bottom</b> (owner: "the TermG
    /// components in the Match Designer schematic need to be rotated 90 deg with the ground at the
    /// bottom").
    /// </summary>
    /// <remarks>
    /// Asserted through the GEOMETRY rather than through the rotation enum alone: what the owner is
    /// looking at is where the ground bars point and where the pin lands, so the test computes both.
    /// <c>TermG</c>'s pin is local (0, −200) and its bars run to local +270; at R0 those are the world
    /// offsets, so the pin must sit ON the spine and the body BELOW it.
    /// </remarks>
    [Fact]
    public void EachTermination_StandsUpright_WithItsGroundBelowTheSpine()
    {
        var (_, _, d) = Open();
        var model = MatchSchematicModel.Build(d.Ladder);

        var terms = model.Components.Where(c => c.Symbol == SymbolKind.TermG).OrderBy(c => c.X).ToList();
        Assert.Equal(2, terms.Count);

        foreach (var t in terms)
        {
            Assert.Equal(SymbolRotation.R0, t.Rotation);

            // The "+" pin, in world coordinates, is on the spine.
            var pin = t.Ports.Single();
            output.WriteLine($"{t.InstanceName}: centre ({t.X:F0},{t.Y:F0}) pin y {t.Y + pin.LocalY:F0} "
                             + $"glyph y [{t.GlyphBbMinY - t.Y:F0}, {t.GlyphBbMaxY - t.Y:F0}]");
            Assert.Equal(MatchLadderLayout.SpineY, t.Y + pin.LocalY, 6);

            // The body — and therefore the ground bars — is entirely BELOW the spine.
            Assert.True(t.GlyphBbMaxY > MatchLadderLayout.SpineY,
                        "the termination's ground bars are not below the spine");
        }

        Assert.Equal(d.Ladder.PortLeftX,  terms[0].X, 6);
        Assert.Equal(d.Ladder.PortRightX, terms[1].X, 6);

        d.Dispose();
    }

    /// <summary>
    /// <b>A shunt element's upper pin lands exactly on the spine</b> (owner: "the shunt component
    /// placement needs to move up such that the shunt components are exactly at the top horizontal
    /// wire").
    /// </summary>
    [Fact]
    public void EveryShuntElement_HasItsUpperPinOnTheSpine()
    {
        var (_, _, d) = Open();
        var model = MatchSchematicModel.Build(d.Ladder);

        var shunts = d.Ladder.Elements.Where(e => e.IsShunt).ToList();
        Assert.NotEmpty(shunts);

        foreach (var e in shunts)
        {
            var comp = model.Components.Single(c => c.Id == e.Name);
            double top = comp.Ports.Min(p => comp.Y + p.LocalY);
            output.WriteLine($"{e.Name}: centre y {comp.Y:F0}, upper pin y {top:F0}");
            Assert.Equal(MatchLadderLayout.SpineY, top, 6);

            // …and its own Ground still sits on the LOWER pin, so that end needs no wire either.
            double bottom = comp.Ports.Max(p => comp.Y + p.LocalY);
            var gnd = model.Components.Single(
                c => c.Symbol == SymbolKind.Ground && Math.Abs(c.X - e.X) < 1e-9);
            Assert.Equal(bottom, gnd.Y, 6);
        }

        d.Dispose();
    }

    /// <summary>
    /// <b>The drawing contains no vertical wire at all</b> (owner: "there should be no vertical wires
    /// rendered in the schematic").
    /// </summary>
    /// <remarks>
    /// Asserted over the wires the model actually holds, not over the source: the shunt drop was the
    /// last one and it is gone, but so is the reason a NEW one could appear — every pin that used to
    /// need a wire now coincides with the thing it connects to. A zero-length wire would pass a
    /// "no vertical segment" check while still being a wire nobody wants, so the test also refuses a
    /// degenerate one.
    /// </remarks>
    [Fact]
    public void TheDrawing_HasNoVerticalWire()
    {
        var (_, _, d) = Open();
        var model = MatchSchematicModel.Build(d.Ladder);

        Assert.NotEmpty(model.Wires);
        foreach (var w in model.Wires)
        {
            for (int i = 1; i < w.Points.Count; i++)
            {
                var (a, b) = (w.Points[i - 1], w.Points[i]);
                Assert.Equal(a.Y, b.Y, 9);                                  // horizontal
                Assert.True(Math.Abs(b.X - a.X) > 1e-6, "a zero-length wire segment");
            }
        }

        d.Dispose();
    }

    // ══ Shunt labels that would bleed into the next column ═══════════════════

    /// <summary>
    /// <b>An ordinary shunt label still sits beside its symbol.</b> The fallback added this round is a
    /// fallback; if it fired for the shipped default design it would be a redesign of the pane rather
    /// than a fix for a long name.
    /// </summary>
    [Fact]
    public void ADefaultDesignsShuntLabels_StillSitBesideTheirSymbols()
    {
        var (_, _, d) = Open();
        var model = MatchSchematicModel.Build(d.Ladder);

        var shunts = d.Ladder.Elements.Where(e => e.IsShunt).ToList();
        Assert.NotEmpty(shunts);

        output.WriteLine($"budget {MatchShuntLabels.WidthBudget(MatchLadderLayout.Pitch):F0} world units");
        foreach (var e in shunts)
        {
            var comp = model.Components.Single(c => c.Id == e.Name);
            output.WriteLine($"  {string.Join(" | ", comp.Labels)}  → "
                             + $"{MatchShuntLabels.EstimateWidth(comp.Labels):F0}");
            Assert.All(comp.LabelOffsets, o =>
            {
                Assert.Equal(MatchSchematicModel.ShuntLabelDx, o.DX, 9);
                Assert.Equal(MatchSchematicModel.ShuntLabelDy, o.DY, 9);
            });
        }

        d.Dispose();
    }

    /// <summary>
    /// <b>A shunt label block too wide for the gap goes under the arm's ground</b> (owner: "if a shunt
    /// component instance name gets too long, its text rendering bleeds into the component to its
    /// right — make it so that all of the component text gets rendered underneath the GND component
    /// below it").
    /// </summary>
    /// <remarks>
    /// The two assertions that matter are geometric and are made against the editor's own label
    /// metrics: the block must clear the ground glyph vertically, and it must no longer reach the next
    /// column horizontally. Checking only "the offset changed" would pass on a fallback that moved the
    /// text somewhere else useless.
    /// </remarks>
    [Fact]
    public void ALongShuntName_MovesTheWholeLabelBlockUnderTheGround()
    {
        var net = new MatchNetwork { R1 = 50, R2 = 50 };
        net.Elements.Add(new MatchElement
        {
            Name = "CFano_N1_2_N2_3", Type = ElementType.C, IsShunt = true, Value = 1.23e-12,
        });
        var layout = MatchLadderLayout.Build(net, null, _ => "1.23 pF", (_, r) => $"{r:F0} Ω");
        var model = MatchSchematicModel.Build(layout);

        var comp = model.Components.Single(c => c.Id == "CFano_N1_2_N2_3");
        var (dx, dy) = comp.LabelOffsets[0];
        Assert.All(comp.LabelOffsets, o => Assert.Equal((dx, dy), (o.DX, o.DY)));
        output.WriteLine($"offsets ({dx:F1}, {dy:F1}) vs beside "
                         + $"({MatchSchematicModel.ShuntLabelDx:F1}, {MatchSchematicModel.ShuntLabelDy:F1})");

        // Row 0's top edge clears the ground glyph's bottom.
        var (lx, ly, _, _) = SchematicComponent.LabelRowGeometry(
            comp.X, comp.Y, 0, dx, dy, comp.Symbol, comp.Ports.Count / 2, comp.GlyphBbMaxY - comp.Y);
        double groundBottom = MatchLadderLayout.ShuntGroundY + MatchShuntLabels.GroundGlyphDepth;
        Assert.True(ly - SchematicComponent.LabelWorldHeight >= groundBottom,
                    $"the block's top ({ly - SchematicComponent.LabelWorldHeight:F0}) is not below "
                    + $"the ground glyph ({groundBottom:F0})");

        // …and it no longer reaches where the next column's symbol would be.
        double blockRight = lx + MatchShuntLabels.EstimateWidth(comp.Labels);
        double nextGlyphLeft = comp.X + MatchLadderLayout.Pitch - MatchShuntLabels.GlyphHalfWidth;
        output.WriteLine($"block right {blockRight:F0}, next glyph left {nextGlyphLeft:F0}");
        Assert.True(blockRight < nextGlyphLeft, "the block still runs into the next column");
    }

    /// <summary>
    /// The rule's own arithmetic, stated once: the budget is the distance from the label anchor to the
    /// next column's glyph, and it is derived from the constants that place both rather than tuned.
    /// </summary>
    [Fact]
    public void TheLabelWidthBudget_IsTheGapToTheNextColumnsGlyph()
    {
        double anchor = SchematicComponent.LabelBaseOffsetX + MatchSchematicModel.ShuntLabelDx;
        double expected = MatchLadderLayout.Pitch - MatchShuntLabels.GlyphHalfWidth
                          - MatchShuntLabels.Clearance - anchor;
        Assert.Equal(expected, MatchShuntLabels.WidthBudget(MatchLadderLayout.Pitch), 9);
        output.WriteLine($"budget {expected:F0} world units");

        // The decision is exactly "measured width vs budget" — walk a growing string across it and
        // check the two agree at every step, so neither can grow a rule of its own.
        bool crossed = false;
        for (int n = 1; n <= 40; n++)
        {
            var rows = new[] { new string('W', n) };
            double w = MatchShuntLabels.EstimateWidth(rows);
            Assert.Equal(w > expected, MatchShuntLabels.Overflows(rows, MatchLadderLayout.Pitch));
            crossed |= w > expected;
        }
        Assert.True(crossed, "nothing in the sweep was wide enough to overflow");

        // …and the widest row wins, not the first or the last.
        Assert.Equal(MatchShuntLabels.EstimateWidth(["a very long row indeed"]),
                     MatchShuntLabels.EstimateWidth(["C", "a very long row indeed", "C = 1 pF"]), 6);
    }

    // ══ Units ════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>pH and pF are the shipped defaults</b> (owner: "in the Match Designer Settings, change the
    /// default inductor units to pH and the default capacitance units to pF"), and both are units the
    /// Settings flyout actually offers — a default the picker cannot re-select is a one-way door.
    /// </summary>
    [Fact]
    public void TheDefaultDisplayUnits_ArePicoHenriesAndPicoFarads()
    {
        var s = new MatchDesignerSettings();
        Assert.Equal("pH", s.InductanceUnit);
        Assert.Equal("pF", s.CapacitanceUnit);
        Assert.Contains("pH", MatchDesignerSettings.InductanceUnitOptions);
        Assert.Contains("pF", MatchDesignerSettings.CapacitanceUnitOptions);

        Assert.Equal("1.23 pF", MatchValueFormat.FormatWithUnit(
            1.23e-12, MatchQuantity.Capacitance, s.CapacitanceUnit, s.SignificantDigits));
        // …and a nanohenry-sized inductor reads in pH rather than switching ladder rung.
        Assert.Equal("1230 pH", MatchValueFormat.FormatWithUnit(
            1.23e-9, MatchQuantity.Inductance, s.InductanceUnit, s.SignificantDigits));
    }

    /// <summary>
    /// <b>A fixed display unit must not turn an ordinary value into scientific notation.</b>
    /// </summary>
    /// <remarks>
    /// Found by taking the owner's new default seriously rather than by report: <c>"G3"</c> goes
    /// exponential the moment the exponent reaches the precision, so 1.23 nH shown in pH at three
    /// significant digits read <c>"1.23E+03 pH"</c>. Auto had masked it by always choosing a unit that
    /// keeps the mantissa under 1000 — which is precisely what a fixed unit gives up.
    /// </remarks>
    [Theory]
    [InlineData(1.23e-9,   "pH", 3, "1230")]
    [InlineData(153.5169e-12, "pH", 3, "154")]
    [InlineData(1.23e-12,  "pF", 3, "1.23")]
    [InlineData(47e-9,     "pH", 3, "47000")]
    [InlineData(2e-12,     "pF", 9, "2")]          // padding zeros trimmed, not rendered
    [InlineData(0.05e-12,  "pF", 3, "0.05")]
    public void AFixedUnit_NeverRendersAnOrdinaryValueInExponentialNotation(
        double value, string unit, int digits, string expected)
    {
        var quantity = unit.EndsWith('H') ? MatchQuantity.Inductance : MatchQuantity.Capacitance;
        var (text, u) = MatchValueFormat.Format(value, quantity, unit, digits);
        output.WriteLine($"{value:E3} in {unit} @{digits} → {text} {u}");
        Assert.Equal(expected, text);
        Assert.DoesNotContain("E+", text, StringComparison.OrdinalIgnoreCase);
    }

    // ══ The brace ════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The brace's stem has been shortened twice</b> — 70 to 52.5 ("reduce the curly brace vertical
    /// line length by a factor of 0.75"), then 52.5 to 26.25 on 2026-08-20 ("reduce the curly brace's
    /// vertical line length rendering above the N1, N2 text"). The stem is the brace's only straight
    /// vertical run; the label hangs the same distance below its foot, so the whole assembly moves up
    /// with it.
    /// </summary>
    [Fact]
    public void TheBraceStem_IsHalfOfWhatRound4LeftIt()
    {
        Assert.Equal(70.0 * 0.75 * 0.5, MatchLadderLayout.BraceStem, 9);

        var stem = MatchBraceGeometry.Stem(0, 1000, 900, MatchLadderLayout.BraceCurl,
                                           MatchLadderLayout.BraceStem,
                                           MatchLadderLayout.BraceLabelDrop);
        Assert.NotNull(stem);
        var s = stem!.Value;
        Assert.Equal(MatchLadderLayout.BraceStem, s.Y1 - s.Y0, 9);
        Assert.Equal(MatchLadderLayout.BraceLabelDrop, s.LabelBaselineY - s.Y1, 9);
    }

    // ══ Copy ═════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Copy projects the pane onto real editable objects</b> (owner: "add a context menu to the
    /// schematic with a menu 'Copy' — this puts the schematic on the clipboard that can be pasted into
    /// a real circuitRF schematic or into PowerPoint as EMF").
    /// </summary>
    /// <remarks>
    /// What is checked here is the half that is testable without a clipboard: the projection is the
    /// SAME drawing the pane shows — same components at the same coordinates with the same rotations
    /// — and it carries real parameters, so what lands in a schematic page simulates rather than
    /// merely looking right.
    /// </remarks>
    [Fact]
    public void CopyProjectsTheSameDrawing_AsRealComponentsWithRealParameters()
    {
        var (_, _, d) = Open();
        var pane = MatchSchematicModel.Build(d.Ladder);
        var copy = MatchSchematicCopy.Build(d.Ladder);

        // Same component count, same places. (The pane names its terminations for the reader; the
        // copy names them T1/T2 so the result is a legal instance name — see MatchSchematicCopy.)
        Assert.Equal(pane.Components.Count, copy.Components.Count);
        foreach (var e in d.Ladder.Elements)
        {
            var a = pane.Components.Single(c => c.Id == e.Name);
            var b = copy.Components.Single(c => c.InstanceName == e.Name);
            Assert.Equal(a.X, b.X, 9);
            Assert.Equal(a.Y, b.Y, 9);
            Assert.Equal(a.Rotation, b.Rotation);
            Assert.Equal(a.Symbol, b.Symbol);
            Assert.Equal(a.LabelOffsets.Count, b.LabelOffsets.Count);

            var p = b.Parameters.Single(x => x.Name is "L" or "C");
            Assert.Equal($"{p.Expression} {p.Unit}", e.ValueText);
        }

        Assert.Equal(2, copy.Components.Count(c => c.Symbol == SymbolKind.TermG));
        Assert.Equal(pane.Wires.Count, copy.Wires.Count);
        Assert.DoesNotContain(copy.Components, c => c.InstanceName.Contains(' ', StringComparison.Ordinal));

        d.Dispose();
    }

    /// <summary>
    /// Copy is wired on BOTH surfaces the owner asked for — the schematic pane, and the value listing
    /// above its <c>Copy as CSV</c> — and both go through the schematic editor's own clipboard writer
    /// rather than a second implementation.
    /// </summary>
    [Fact]
    public void Copy_IsOnTheSchematicAndAboveCopyAsCsv_AndReusesSchematicClipboard()
    {
        string code = Code();

        // One builder, used by both menus.
        Assert.Contains("private MenuItem CopySchematicMenuItem()", code, StringComparison.Ordinal);
        Assert.Contains("SchematicClipboard.CopyAsync", code, StringComparison.Ordinal);
        Assert.Contains("MatchSchematicCopy.Build", code, StringComparison.Ordinal);

        // The listing's menu: Copy FIRST, Copy as CSV second.
        int copy = code.IndexOf("new[] { CopySchematicMenuItem(), csv }", StringComparison.Ordinal);
        Assert.True(copy >= 0, "the listing's menu no longer puts Copy above Copy as CSV");

        // The schematic pane's own menu, attached to the named canvas.
        Assert.Contains("Name=\"NetworkSchematic\"", Xaml(), StringComparison.Ordinal);
        Assert.Contains("\"NetworkSchematic\"", code, StringComparison.Ordinal);
    }

    // ══ The pane's chrome ════════════════════════════════════════════════════

    /// <summary>
    /// <b>The hint line above the schematic is gone</b> (owner: "remove the entire 'scroll to zoom...'
    /// text that is rendered above the Match Designer schematic view"), and so is the transform rack's
    /// trailing pair indicator ("remove the on '(C2, C3)' indicator text").
    /// </summary>
    [Fact]
    public void TheSchematicHint_AndTheTransformPairIndicator_AreGone()
    {
        string xaml = Xaml();
        Assert.DoesNotContain("scroll to zoom", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("drag to pan", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ActsOn", xaml, StringComparison.Ordinal);

        // The property is gone too, not merely unbound — an unread view-model property is a second
        // place for the removed text to come back from.
        Assert.DoesNotContain("ActsOn", Src("src", "Ui", "Match", "MatchTransformRowViewModel.cs"),
                              StringComparison.Ordinal);
    }

    // ══ The response plots ═══════════════════════════════════════════════════

    /// <summary>
    /// <b>S(2,1) reads the right-hand axis and is drawn in the Data Display's blue</b> (owner: "S(2,1)
    /// should be placed on the right-y axis, it should be in blue color… use the same plot colors as
    /// the data display — same shade of red and same shade of blue for all plot traces").
    /// </summary>
    /// <remarks>
    /// The colours are asserted through <c>TraceProperties.LineColorOrder</c>, which is the table the
    /// Data Display's own "add trace" walks — not against the literal red and blue, which would pass
    /// just as happily if this window kept its own copy of them.
    /// </remarks>
    [Fact]
    public void S21_IsOnTheSecondaryAxis_InTheDataDisplaysSecondColour()
    {
        var (_, _, d) = Open();

        var mag = d.MagnitudePlot;
        Assert.Equal(2, mag.Traces.Count);
        var (s11, s21) = (mag.Traces[0], mag.Traces[1]);

        Assert.Equal(0, s11.Row);
        Assert.Equal(1, s21.Row);
        Assert.False(s11.UseSecondaryAxis);
        Assert.True(s21.UseSecondaryAxis);

        var lut = TraceProperties.ColorLUT;
        output.WriteLine($"S11 {s11.Properties.LineColor}, S21 {s21.Properties.LineColor}");
        Assert.Equal(lut[TraceProperties.LineColorOrder[0]], s11.Properties.LineColor);
        Assert.Equal(lut[TraceProperties.LineColorOrder[1]], s21.Properties.LineColor);
        Assert.NotEqual(s11.Properties.LineColor, s21.Properties.LineColor);

        // The phase plot uses the same two, in the same order — one palette across the window.
        Assert.Equal(2, d.PhasePlot.Traces.Count);
        Assert.Equal(s11.Properties.LineColor, d.PhasePlot.Traces[0].Properties.LineColor);
        Assert.Equal(s21.Properties.LineColor, d.PhasePlot.Traces[1].Properties.LineColor);

        // Marker glyphs take the trace's colour too — the owner reported markers rendering in the
        // wrong one, and a line/marker mismatch is how that happens.
        Assert.Equal(s21.Properties.LineColor, s21.Properties.MarkerColor);

        d.Dispose();
    }

    /// <summary>
    /// <b>Axes panning starts locked</b> (owner: "have the plot's Lock Axes Panning set to true when
    /// user first opens Match Designer").
    /// </summary>
    [Fact]
    public void BothResponsePlots_OpenWithAxesPanningLocked()
    {
        var (_, _, d) = Open();
        Assert.True(d.MagnitudePlot.Axes.LockedPanning);
        Assert.True(d.PhasePlot.Axes.LockedPanning);
        d.Dispose();
    }

    /// <summary>
    /// <b>The two plots are hosted on a real Data Display</b> — which is what makes the marker info
    /// box, the marker selection highlight and the plot's own <c>Copy</c> work at all.
    /// </summary>
    /// <remarks>
    /// <c>PlotExporter.CopyPlotToClipboardAsync</c> returns immediately when its container argument is
    /// null, and <c>PlotControl</c> reads that container from <c>ContainerProvider</c>; a marker's
    /// info box is created by <c>DataDisplayViewModel</c> in response to the container's
    /// <c>OnMarkerAdded</c>. All three were silent no-ops while the plots had no host. This test holds
    /// the host itself — two containers, both carrying the plots the window binds — and the wiring
    /// that connects each control to its own.
    /// </remarks>
    [Fact]
    public void TheResponsePlots_HaveADataDisplayHost_WiredToBothControls()
    {
        var (_, _, d) = Open();

        Assert.Equal(2, d.PlotHost.Plots.Count);
        Assert.Same(d.MagnitudePlot, d.MagnitudeContainer.PlotVM.Plot);
        Assert.Same(d.PhasePlot,     d.PhaseContainer.PlotVM.Plot);
        Assert.NotSame(d.MagnitudeContainer, d.PhaseContainer);

        // Adding a marker through the container's own path creates its info box — the collection the
        // Response pane's overlay is bound to.
        var trace = d.MagnitudePlot.Traces[0];
        var marker = new Marker(trace, trace.Data.Frequencies[1], false, false,
                                d.MagnitudeContainer.GetNextMarkerIndex(), d.MagnitudePlot.FreqUnits);
        trace.Markers.Add(marker);
        d.MagnitudeContainer.OnMarkerAdded(marker, trace);
        d.MagnitudeContainer.OnPlotChanged(null, EventArgs.Empty);

        Assert.Contains(d.PlotHost.MarkerInfoBoxes, m => ReferenceEquals(m.Marker, marker));
        Assert.NotNull(d.MagnitudeContainer.FindMarkerInfoBoxVm(marker));
        output.WriteLine($"info boxes: {d.PlotHost.MarkerInfoBoxes.Count}");

        // …and the window really does hand each control its container and the overlay a place to draw.
        string code = Code();
        var wire = Between(code, "private void WirePlotHost()");
        foreach (string provider in new[]
                 {
                     "NextMarkerIndexProvider", "FindMarkerInfoBoxVmProvider", "ContainerProvider",
                     "SelectedMarkersProvider", "StepSelectedMarkersHandler", "MarkerAdded",
                 })
            Assert.Contains(provider, wire, StringComparison.Ordinal);
        Assert.Contains("Vm.MagnitudeContainer", wire, StringComparison.Ordinal);
        Assert.Contains("Vm.PhaseContainer", wire, StringComparison.Ordinal);

        Assert.Contains("PlotHost.MarkerInfoBoxes", Xaml(), StringComparison.Ordinal);
        Assert.Contains("MarkerInfoBoxView", Xaml(), StringComparison.Ordinal);

        d.Dispose();
    }

    /// <summary>
    /// <b>Closing the Designer releases its plot host.</b>
    /// </summary>
    /// <remarks>
    /// Not a reported bug — a consequence of giving this window a <c>DataDisplayViewModel</c> of its
    /// own. That type subscribes to the process-wide <c>AppSettingsViewModel.Instance</c>, which for a
    /// Data Display window (one per session) costs nothing and for a Designer (one per component, per
    /// open) would strand a display, its containers, its plots and its response SNP on a static event
    /// for the rest of the run.
    /// </remarks>
    [Fact]
    public void DisposingTheDesigner_UnsubscribesItsPlotHostFromTheSettingsSingleton()
    {
        var settings = AppSettingsViewModel.Instance;

        // A field-like event's backing field is private on whichever type DECLARES the event, so walk
        // the hierarchy rather than guessing which one that is.
        System.Reflection.FieldInfo? field = null;
        for (var t = settings.GetType(); t is not null && field is null; t = t.BaseType)
            field = t.GetField("PropertyChanged",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.DeclaredOnly);

        Assert.True(field is not null, "AppSettingsViewModel's PropertyChanged backing field moved — "
                                       + "the leak this guards is no longer measurable here");

        // Asked about THIS host's own handler, not about the total count: the singleton is
        // process-wide and every other test in this assembly is building displays of its own in
        // parallel, so a before/after tally of the invocation list measures the suite, not the fix.
        bool StillSubscribed(object host) =>
            (field!.GetValue(settings) as Delegate)?.GetInvocationList()
                .Any(dg => ReferenceEquals(dg.Target, host)) ?? false;

        var (_, _, d) = Open();
        Assert.True(StillSubscribed(d.PlotHost), "the plot host did not subscribe at all");
        d.Dispose();
        Assert.False(StillSubscribed(d.PlotHost));
    }

    /// <summary>
    /// <b>Delete Plot and Plot Properties… are disabled in this window</b> (owner: "the plot's context
    /// menu shows Delete Plot as enabled — it should be disabled in a Match Designer window. Same with
    /// Plot Properties… menu").
    /// </summary>
    /// <remarks>
    /// Two halves, and both matter. The window turns the flags off; and <c>PlotControl</c> re-reads
    /// them on every menu OPEN rather than once when it builds the (cached, lifetime-long) menu, so a
    /// flag set after the first right-click is still honoured. A test that only checked the AXAML
    /// would pass on a control that ignored the property.
    /// </remarks>
    [Fact]
    public void DeletePlotAndPlotProperties_AreDisabledHere_AndReReadOnEveryOpen()
    {
        string xaml = Xaml();
        Assert.Equal(2, Regex.Matches(xaml, @"CanDeletePlot=""False""").Count);
        Assert.Equal(2, Regex.Matches(xaml, @"CanEditPlotProperties=""False""").Count);

        string plot = Src("src", "Ui", "DataDisplay", "Controls", "PlotControl.cs");
        var opening = Between(plot, "menu.Opening +=");
        Assert.Contains("CanEditPlotProperties", opening, StringComparison.Ordinal);
        Assert.Contains("CanDeletePlot", opening, StringComparison.Ordinal);

        // The inspector itself refuses, so a double-tap cannot get in round the back.
        var inspector = Between(plot, "private void ShowPlotInspector(int scrollToTraceIndex = -1)");
        Assert.Contains("CanEditPlotProperties", inspector, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The Response pane sits on the Data Display's own canvas colour</b> (owner: "background color
    /// of the Response group UI needs to be the same as the background color of the Data display"),
    /// and there is exactly ONE definition of that colour in the repository.
    /// </summary>
    [Fact]
    public void TheResponsePane_PaintsWithTheDataDisplayCanvasBrush_DefinedOnlyOnce()
    {
        Assert.Contains("Background=\"{DynamicResource CrfDataDisplayCanvasBrush}\"",
                        Xaml(), StringComparison.Ordinal);

        // Defined once, at application scope; PlotCanvasView reads the same key rather than repeating
        // the literal colours.
        string resources = Src("src", "Ui", "Styles", "CircuitRfResources.axaml");
        Assert.Equal(2, Regex.Matches(resources, @"x:Key=""CrfDataDisplayCanvasBrush""").Count);  // light + dark

        string canvas = Src("src", "Ui", "Views", "DataDisplay", "PlotCanvasView.axaml");
        Assert.DoesNotContain("x:Key=\"CrfDataDisplayCanvasBrush\"", canvas, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource CrfDataDisplayCanvasBrush}", canvas, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The plots follow the application's light/dark variant</b> (owner: "plot axis colors and
    /// markers are currently rendering in wrong color — change the colors so that everything matches").
    /// </summary>
    /// <remarks>
    /// A <c>PlotControl</c>'s <c>PlotTheme</c> defaults to <c>RenderTheme.Light</c> and nothing was
    /// setting it, so the grid, ticks, axis text, border and plot background all came out of the light
    /// theme in a dark window. The window now drives it from its own <c>ActualThemeVariant</c>, through
    /// the host's <c>Theme</c> — one value, so the plots, the info boxes and the exported copy cannot
    /// disagree about which theme they are in.
    /// </remarks>
    [Fact]
    public void ThePlotTheme_FollowsTheWindowsThemeVariant()
    {
        var (_, _, d) = Open();

        d.PlotHost.Theme = RenderTheme.Dark;
        Assert.Same(RenderTheme.Dark, d.MagnitudeContainer.Theme);
        Assert.Same(RenderTheme.Dark, d.PhaseContainer.Theme);

        var sync = Between(Code(), "private void SyncPlotTheme()");
        Assert.Contains("ActualThemeVariant", sync, StringComparison.Ordinal);
        Assert.Contains("RenderTheme.Dark", sync, StringComparison.Ordinal);
        Assert.Contains("RenderTheme.Light", sync, StringComparison.Ordinal);
        Assert.Contains("PlotControl.PlotThemeProperty", sync, StringComparison.Ordinal);

        Assert.Contains("ActualThemeVariantChanged", Code(), StringComparison.Ordinal);

        d.Dispose();
    }

    // ── helper ────────────────────────────────────────────────────────────────

    private static string Between(string src, string signature)
    {
        int i = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{signature}' is not in the source any more");
        int open = src.IndexOf('{', i);
        int depth = 0;
        for (int k = open; k < src.Length; k++)
        {
            if (src[k] == '{') depth++;
            else if (src[k] == '}' && --depth == 0) return src[open..(k + 1)];
        }
        Assert.Fail($"'{signature}' has no closing brace");
        return "";
    }
}
