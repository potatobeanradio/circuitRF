// ================================================================
//  MatchRound3Tests.cs  —  the owner's 2026-08-20 round-3 list for the Match Designer.
//
//  Same discipline as rounds 1 and 2: view-model, geometry and projection tests, never pixels. Where
//  the ask is about layout declared in AXAML, or about a rendering path that needs a live Avalonia
//  application to exercise, the assertion is made against the source the mechanism is written in and
//  NAMES that mechanism — a scan for "the file mentions the word" would pass over a broken fix.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Core.Matching;
using CircuitRF.Ui.Matching;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Match;

public sealed class MatchRound3Tests(ITestOutputHelper output)
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
    /// One source file with its COMMENTS STRIPPED. Every scan below is about what the code does, and
    /// a comment that quotes the thing it replaced ("never ZoomToFit()", "the Copy as CSV button") is
    /// exactly the text a naive scan trips over — the comment saying why something is gone would make
    /// the test that checks it is gone fail.
    /// </summary>
    private static string Src(params string[] parts)
    {
        string raw = File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));
        raw = Regex.Replace(raw, @"<!--.*?-->", "", RegexOptions.Singleline);   // XML / AXAML
        raw = Regex.Replace(raw, @"/\*.*?\*/", "", RegexOptions.Singleline);    // C# block
        raw = Regex.Replace(raw, @"//[^\n]*", "", RegexOptions.None);           // C# line + XML doc
        return raw;
    }

    private static string Xaml() =>
        Src("src", "Ui", "Views", "Match", "MatchDesignerWindow.axaml");

    private static string Canvas() =>
        Src("src", "Ui", "Views", "Match", "MatchSchematicCanvas.cs");

    // ══ The schematic pane ═══════════════════════════════════════════════════

    /// <summary>
    /// <b>The pointer is the ordinary arrow</b> (owner: "don't use mouse cross hair cursor in the
    /// Match Designer schematic; stick to regular arrow icon"). <c>SizeAll</c> — the four-way
    /// move cursor — advertised an edit gesture this read-only pane does not have.
    /// </summary>
    [Fact]
    public void TheNetworkPane_UsesTheArrowCursor()
    {
        string src = Canvas();
        Assert.Contains("StandardCursorType.Arrow", src, StringComparison.Ordinal);
        Assert.DoesNotContain("StandardCursorType.SizeAll", src, StringComparison.Ordinal);
        Assert.DoesNotContain("StandardCursorType.Cross", src, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Owner-reported crash:</b> changing a PI network to a T threw
    /// <c>InvalidOperationException: Visual was invalidated during the render pass</c> out of
    /// <c>MatchSchematicCanvas.ZoomToFit</c>, called from <c>Render</c>.
    /// </summary>
    /// <remarks>
    /// Ui.Tests has no live Avalonia application, so the render pass itself cannot be entered here.
    /// What CAN be checked exactly is the property the fix rests on: <c>Render</c> re-frames through
    /// the invalidate-free <c>Fit()</c>, and <c>InvalidateVisual</c> appears nowhere between
    /// <c>Fit()</c>'s signature and its closing brace. A test that merely asserted "Render does not
    /// contain the string ZoomToFit" would pass on a fix that moved the invalidate one call deeper.
    /// </remarks>
    [Fact]
    public void ARefit_DuringTheRenderPass_NeverInvalidatesTheVisual()
    {
        string src = Canvas();

        // Render asks for the invalidate-free path, by name.
        var render = Between(src, "public override void Render(DrawingContext context)");
        Assert.Contains("Fit();", render, StringComparison.Ordinal);
        Assert.DoesNotContain("ZoomToFit()", render, StringComparison.Ordinal);

        // ...and that path really is invalidate-free.
        var fit = Between(src, "private bool Fit()");
        Assert.DoesNotContain("Invalidate", fit, StringComparison.Ordinal);
        Assert.Contains("_fitted = true;", fit, StringComparison.Ordinal);

        // The public entry point still repaints — otherwise a double-click would re-frame nothing.
        var zoom = Between(src, "public void ZoomToFit()");
        Assert.Contains("Fit()", zoom, StringComparison.Ordinal);
        Assert.Contains("InvalidateVisual();", zoom, StringComparison.Ordinal);
    }

    /// <summary>The body of the method whose signature line is <paramref name="signature"/>.</summary>
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

    // ══ Shunt labels ═════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A shunt element's three label rows are centred on the symbol's own centre</b> (owner: "the
    /// component type, instance name and value text renderings for the shunt components is placed too
    /// high relative to the symbol — adjust the vertical alignment such that the center of all 3 rows
    /// of text is at the same y coordinate as the center of the component symbol").
    /// </summary>
    /// <remarks>
    /// Measured through <c>SchematicComponent.LabelRowGeometry</c> — the editor's own label geometry,
    /// the same call the renderer and the hit-test make — rather than against the constant, so a
    /// change to the editor's metrics that broke the centring would fail here.
    /// </remarks>
    [Fact]
    public void AShuntElementsLabelBlock_IsCentredOnItsSymbol()
    {
        var (_, _, d) = Open();
        var model = MatchSchematicModel.Build(d.Ladder);

        var shunts = d.Ladder.Elements.Where(e => e.IsShunt).ToList();
        Assert.NotEmpty(shunts);

        foreach (var e in shunts)
        {
            var comp = Assert.Single(model.Components, c => c.Id == e.Name);
            Assert.Equal(3, comp.Labels.Count);

            double glyphHalfH = comp.GlyphBbMaxY - comp.Y;
            var rows = Enumerable.Range(0, 3).Select(i =>
            {
                var (_, baseline, _, _) = SchematicComponent.LabelRowGeometry(
                    comp.X, comp.Y, i, comp.LabelOffsets[i].DX, comp.LabelOffsets[i].DY,
                    comp.Symbol, comp.Ports.Count / 2, glyphHalfH);
                return baseline;
            }).ToList();

            double top    = rows[0] - SchematicComponent.LabelWorldHeight;
            double bottom = rows[^1];
            output.WriteLine($"{e.Name}: rows {string.Join(", ", rows.Select(r => r.ToString("F1")))}"
                             + $" → centre {(top + bottom) / 2:F1}, symbol centre {comp.Y:F1}");

            Assert.Equal(comp.Y, (top + bottom) / 2, 6);

            // ...and they are still clear of the glyph, to the RIGHT of the column.
            Assert.All(comp.LabelOffsets, o => Assert.True(o.DX > 0));
        }

        d.Dispose();
    }

    /// <summary>
    /// The same DECISION is made in the FLATTENED cell (owner: "do this for the flattened cell too"),
    /// by the same method rather than by a copied pair of numbers — two copies of a number that must
    /// agree is how the two drawings drift apart.
    /// </summary>
    /// <remarks>
    /// <b>Round 4 made the offsets conditional</b> (owner, 2026-08-20: "if the instance name does
    /// overlap with its adjacent component, all of the component text gets rendered underneath the GND
    /// component below it… the flatten to cell should also do this"), so what this test holds is no
    /// longer a constant: it is that both drawings ask <see cref="MatchShuntLabels"/> about the rows
    /// THEY draw, and take the answer. Asserting the beside-the-symbol constant unconditionally would
    /// now be asserting that the fallback never fires.
    /// </remarks>
    [Fact]
    public void TheFlattenedCell_UsesTheSameShuntLabelRule()
    {
        var (_, _, d) = Open();
        var model = MatchFlatten.BuildSchematic(
            d.Rebuild!, d.Design, "MN1", new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc));

        // The LADDER's shunt arms — an R0 element that has a Ground directly below it. The two
        // termination annexes also hold R0 elements, and they deliberately keep the fixed offsets
        // (nothing stands to their right to bleed into).
        double groundDy = 300.0;   // MatchFlatten's ShuntGroundY − ShuntY
        var shunts = model.Components
            .Where(c => c.Symbol is SymbolKind.Inductor or SymbolKind.Capacitor)
            .Where(c => c.Rotation == SymbolRotation.R0 && c.Disable == DisableState.None)
            .ToList();
        Assert.NotEmpty(shunts);

        foreach (var c in shunts)
        {
            Assert.Equal(3, c.LabelOffsets.Count);

            var p = c.Parameters.Single(x => x.Name is "L" or "C");
            var expected = MatchShuntLabels.Offsets(
                [p.Name, c.InstanceName, $"{p.Name} = {p.Expression} {p.Unit}"], 700.0, groundDy);

            Assert.All(c.LabelOffsets, o =>
            {
                Assert.Equal(expected.Dx, o.DX, 9);
                Assert.Equal(expected.Dy, o.DY, 9);
            });
        }

        d.Dispose();
    }

    /// <summary>
    /// <b>A series element is at R270 in BOTH drawings</b> (owner: "have the series L components
    /// rotated 180 degrees from their current orientation in the Match Designer schematic; make sure
    /// the flattened cell uses the same final orientation"). The Designer was at R90 and the
    /// flattened cell already at R270, so the two were mirror images of one another.
    /// </summary>
    [Fact]
    public void ASeriesElement_IsAtR270_InThePaneAndInTheFlattenedCell()
    {
        var (_, _, d) = Open();

        var pane = MatchSchematicModel.Build(d.Ladder);
        foreach (var e in d.Ladder.Elements.Where(x => !x.IsShunt))
            Assert.Equal(SymbolRotation.R270, pane.Components.Single(c => c.Id == e.Name).Rotation);

        var cell = MatchFlatten.BuildSchematic(
            d.Rebuild!, d.Design, "MN1", new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc));
        var series = cell.Components
            .Where(c => c.Symbol is SymbolKind.Inductor or SymbolKind.Capacitor)
            .Where(c => Math.Abs(c.Y) < 1e-9)         // on the spine — the annexes are elsewhere
            .ToList();
        Assert.NotEmpty(series);
        Assert.All(series, c => Assert.Equal(SymbolRotation.R270, c.Rotation));

        d.Dispose();
    }

    // ══ The transform brace ══════════════════════════════════════════════════

    /// <summary>
    /// The brace is a real curly brace — a curl at each end, a stem from the centre of its horizontal
    /// run down to the label — and a stacked row is deep enough to hold all three.
    /// </summary>
    [Fact]
    public void TheTransformBrace_HasCurlsAStemAndRoomToStack()
    {
        const double x0 = 300, x1 = 1700, y = 900, curl = MatchLadderLayout.BraceCurl;
        var outline = MatchBraceGeometry.Outline(x0, x1, y, curl);
        foreach (var s in outline) output.WriteLine($"{s.Kind,-4} → ({s.X,7:F0}, {s.Y,6:F0})");

        // Four quarter-turns — one at each end, two into the centre tip — and two straight runs.
        Assert.Equal(4, outline.Count(s => s.Kind == MatchBraceStepKind.Quad));
        Assert.Equal(2, outline.Count(s => s.Kind == MatchBraceStepKind.Line));
        Assert.Equal(MatchBraceStepKind.Move, outline[0].Kind);

        // The ends curl UP, towards the elements the brace is about, and the centre tip points DOWN,
        // towards the label. That is what makes it an under-brace rather than a bracket.
        Assert.Equal(y - curl, outline[0].Y, 9);            // left end, above the run
        Assert.Equal(y - curl, outline[^1].Y, 9);           // right end, likewise
        Assert.Equal(y + curl, outline.Max(s => s.Y), 9);   // the tip is the lowest point
        double xm = (x0 + x1) / 2;
        Assert.Equal(xm, outline.Single(s => Math.Abs(s.Y - (y + curl)) < 1e-9).X, 9);

        // Every turn's CONTROL POINT is the corner itself — tangent to both the vertical and the
        // horizontal. A control point anywhere else gives a rounded bracket, not a brace.
        foreach (var s in outline.Where(s => s.Kind == MatchBraceStepKind.Quad))
            Assert.Equal(y, s.CY, 9);
        Assert.Equal([x0, xm, xm, x1],
                     outline.Where(s => s.Kind == MatchBraceStepKind.Quad).Select(s => s.CX));

        // The stem runs from the tip down, and the label hangs off the end of it.
        var stem = MatchBraceGeometry.Stem(x0, x1, y, curl, MatchLadderLayout.BraceStem,
                                           MatchLadderLayout.BraceLabelDrop)!.Value;
        Assert.Equal(xm, stem.X, 9);
        Assert.Equal(y + curl, stem.Y0, 9);
        Assert.True(stem.Y1 > stem.Y0);
        Assert.True(stem.LabelBaselineY > stem.Y1);

        // ...and a stacked row is deep enough for all of it plus the label's own cap height
        // (~0.7 of the 90-world-unit font the canvas draws it in).
        double needed = stem.LabelBaselineY - y;
        output.WriteLine($"row pitch {MatchLadderLayout.BracketRowPitch}, brace is {needed:F0} deep");
        Assert.True(MatchLadderLayout.BracketRowPitch > needed + 63,
                    $"a stacked brace would overlap the row above it: pitch "
                    + $"{MatchLadderLayout.BracketRowPitch} < {needed + 63:F0}");

        // The canvas draws exactly this outline — it owns no shape of its own.
        string canvas = Canvas();
        Assert.Contains("MatchBraceGeometry.Outline", canvas, StringComparison.Ordinal);
        Assert.Contains("MatchBraceGeometry.Stem", canvas, StringComparison.Ordinal);
    }

    /// <summary>
    /// A one-element span is narrower than four curls, and the brace shrinks its curl rather than
    /// letting the two halves cross — the arithmetic the renderer does, checked here directly.
    /// </summary>
    [Theory]
    [InlineData(0.0, 0.0)]           // degenerate: nothing to draw
    [InlineData(100.0, 25.0)]        // narrow: curl shrinks to a quarter of the span
    [InlineData(560.0, 50.0)]        // wide: the full curl
    public void ABraceNarrowerThanFourCurls_ShrinksItsCurl(double span, double expected)
    {
        double r = Math.Min(MatchLadderLayout.BraceCurl, span / 4.0);
        Assert.Equal(expected, r, 9);
        Assert.True(span <= 0 || 4 * r <= span + 1e-9);
    }

    // ══ The grid ═════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Owner-reported:</b> "the column headers in the grid view are not horizontally aligned with
    /// the contents below it in its own column." They could not be — the header row carried a sixth
    /// <c>Auto</c> column for the Copy as CSV button, so it divided a star pool ~80px narrower than
    /// the rows did and every boundary landed somewhere else.
    /// </summary>
    [Fact]
    public void TheGridHeader_DividesTheSameColumnsAsItsRows()
    {
        string xaml = Xaml();

        // Exactly two column strings in the listing, and they are the same string.
        var cols = Regex.Matches(xaml, @"ColumnDefinitions=""([0-9.*,]*\*[0-9.*,]*)""")
                        .Select(m => m.Groups[1].Value)
                        .Where(v => v.Count(ch => ch == ',') == 2 && v.Contains('*'))
                        .ToList();
        var listing = cols.Where(v => v == "1.6*,*,1.4*").ToList();
        output.WriteLine(string.Join(" | ", cols));
        Assert.Equal(2, listing.Count);

        // Three columns, three headers — no fourth, and no Auto column smuggled in beside them.
        Assert.Equal(3, listing[0].Split(',').Length);
        Assert.DoesNotContain("Auto", listing[0], StringComparison.Ordinal);

        // The headers are left-aligned and unpadded horizontally, because the text under them is a
        // bare TextBlock starting at the column's own left edge.
        Assert.Contains("Selector=\"Button.gridhdr\"", xaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalContentAlignment\" Value=\"Left\"", xaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Value and unit are one column called Value</b>, and the "surplus"/"absorbed" column is gone
    /// (owner). The halves stay separate on the row model because the CSV and the numeric sort both
    /// want them.
    /// </summary>
    [Fact]
    public void TheGrid_ShowsValueAndUnitTogether_AndHasNoNoteColumn()
    {
        string xaml = Xaml();

        Assert.Contains("{Binding ValueWithUnit}", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding Unit}", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding Note}", xaml, StringComparison.Ordinal);

        var (_, _, d) = Open();
        var row = d.Elements.First();
        Assert.Equal($"{row.ValueText} {row.Unit}", row.ValueWithUnit);
        Assert.NotEmpty(row.Unit);            // the halves are still there underneath
        d.Dispose();
    }

    /// <summary>
    /// <b>Copy as CSV is the listing's own context menu, not a button</b> (owner). Wired in
    /// code-behind rather than declared in the AXAML: a <c>ContextMenu</c> is a popup with its own
    /// visual root, and a handler that silently never attaches is a menu entry that does nothing.
    /// </summary>
    [Fact]
    public void CopyAsCsv_IsAContextMenuOnTheListing_NotAButton()
    {
        string xaml = Xaml();
        string code = Src("src", "Ui", "Views", "Match", "MatchDesignerWindow.axaml.cs");

        Assert.DoesNotContain("Copy as CSV", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CopyGridButton", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CopyGridButton", code, StringComparison.Ordinal);

        Assert.Contains("Name=\"ElementsList\"", xaml, StringComparison.Ordinal);
        var wire = Between(code, "private void WireElementsContextMenu()");
        Assert.Contains("\"ElementsList\"", wire, StringComparison.Ordinal);
        Assert.Contains("Copy as CSV", wire, StringComparison.Ordinal);
        Assert.Contains("ElementsCsv", wire, StringComparison.Ordinal);
        Assert.Contains("list.ContextMenu =", wire, StringComparison.Ordinal);
    }

    // ══ The inline editor ════════════════════════════════════════════════════

    /// <summary>
    /// <b>Owner-reported:</b> "when the user invokes the inline text editor, the view around it
    /// shifts by a few pixels — the entire Band Group box gets larger when the user double-clicks on
    /// the f1 value. There should be no shifts or movement in any other UI element."
    /// </summary>
    /// <remarks>
    /// The cause was structural: <see cref="InlineEditText"/> is a <c>Panel</c>, a <c>Panel</c>
    /// measures to the union of its children, and the open editor is a second child that is bigger
    /// than the text it covers in both directions. The fix is that the measure reports the RESTING
    /// text's size and only that, so nothing above it in the tree ever hears about the editor. Asserted
    /// against the source because Ui.Tests has no live Avalonia application to run a layout pass in —
    /// and specifically: the measure returns the display's desired size, and the typing handler
    /// re-ARRANGES rather than re-measuring.
    /// </remarks>
    [Fact]
    public void TheInlineEditor_IsInvisibleToLayout_SoOpeningOneMovesNothing()
    {
        string src = Src("src", "Ui", "Controls", "InlineEditText.cs");

        var measure = Between(src, "protected override Size MeasureOverride(Size availableSize)");
        Assert.Contains("return _display.DesiredSize;", measure, StringComparison.Ordinal);

        var arrange = Between(src, "protected override Size ArrangeOverride(Size finalSize)");
        Assert.Contains("_display.Arrange", arrange, StringComparison.Ordinal);
        Assert.Contains("box.Arrange", arrange, StringComparison.Ordinal);

        // Typing changes the box's width; it must never change this control's OWN measured size.
        Assert.Contains("InvalidateArrange();", src, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidateMeasure();", src, StringComparison.Ordinal);
    }

    // ══ Discover / Apply must agree ══════════════════════════════════════════

    /// <summary>
    /// <b>Owner-reported crash:</b> adding a transform threw <c>InvalidOperationException: Making a
    /// transform pair adjacent would swap C2_N1_1_N2_1_N3_2 past C2_N1_1_N2_1_N3_3, which have
    /// different orientations</c> — straight out of a menu click, with no dialog and no recovery.
    /// </summary>
    /// <remarks>
    /// <c>Discover</c> offered a gap-3 pair whose two inward walks would have crossed a node.
    /// Its guard tested only that the two middle elements shared a TYPE, which is not a proxy for
    /// sharing an ARM: a previous transform's own three products are all one type and alternate in
    /// orientation by construction (pi is shunt-series-shunt, T is series-shunt-series), which is
    /// exactly the configuration in the crash. The ladder below is that shape, minimally.
    /// </remarks>
    [Fact]
    public void Discover_NeverOffersAPairThatCannotBeMadeAdjacent()
    {
        // C series, C shunt, C series, C shunt — (0,3) is opposite and same-type, so the old TYPE
        // test passed it; making it adjacent would have to swap index 0 past a shunt element.
        var net = new MatchNetwork { R1 = 50, R2 = 50 };
        bool[] shunt = [false, true, false, true];
        for (int i = 0; i < 4; i++)
            net.Elements.Add(new MatchElement
            {
                Name = $"C{i + 1}", Type = ElementType.C, IsShunt = shunt[i], Value = 1e-12 * (i + 1),
            });

        var pairs = NortonTransform.Discover(net);
        foreach (var p in pairs) output.WriteLine($"{p.NameA}/{p.NameB}  {p.IndexA}→{p.IndexB}");

        Assert.DoesNotContain(pairs, p => p.IndexA == 0 && p.IndexB == 3);

        // ...and, more usefully than any single case: everything Discover offers, Apply can run.
        foreach (var p in pairs)
        {
            var range = NortonTransform.Range(net, p, analysisIsTerm1: true, allowNegative: false);
            double n = (range.Min + range.Max) / 2;
            var ex = Record.Exception(() => NortonTransform.Apply(
                net, p, n, TransformForm.Pi, analysisIsTerm1: true, allowNegative: false, ordinal: 1));
            Assert.Null(ex);
        }
    }

    /// <summary>
    /// And when the two ever disagree again, a rebuild <b>drops the transform with a note</b> rather
    /// than throwing out of the Designer — the same report it already makes for a pair that has
    /// vanished from the ladder. A crash from a menu click is never the right answer to an internal
    /// disagreement.
    /// </summary>
    [Fact]
    public void ARebuild_ReportsAnUnapplicableTransform_RatherThanThrowing()
    {
        string src = Src("src", "Core", "Match", "MatchRebuild.cs");
        var body = Between(src, "public static SequenceResult ApplySequence(");

        Assert.Contains("catch (InvalidOperationException", body, StringComparison.Ordinal);
        Assert.Contains("dropped.Add(rec);", body, StringComparison.Ordinal);
        Assert.Contains("was dropped: it cannot be", body, StringComparison.Ordinal);
    }

    // ══ Ends and grounds ═════════════════════════════════════════════════════

    /// <summary>
    /// Each <c>TermG</c> quotes the LADDER's own port reference, in the Designer's own resistance
    /// unit and digits — the same number the plotted response is referenced to, which is the reason
    /// <c>MatchFlatten</c>'s annotation quotes it too.
    /// </summary>
    [Fact]
    public void EachTermination_QuotesTheLaddersOwnPortReference()
    {
        var (_, _, d) = Open();
        var net = d.Rebuild!.Network!;

        Assert.Equal(2, d.Ladder.Terminations.Count);
        var (t1, t2) = (d.Ladder.Terminations[0], d.Ladder.Terminations[1]);
        output.WriteLine($"{t1.InstanceName}: {t1.ResistanceText} (R1 {net.R1:F4} Ω)");
        output.WriteLine($"{t2.InstanceName}: {t2.ResistanceText} (R2 {net.R2:F4} Ω)");

        Assert.Equal(1, t1.End);
        Assert.Equal(2, t2.End);
        Assert.Equal(d.Ladder.PortLeftX,  t1.X, 6);
        Assert.Equal(d.Ladder.PortRightX, t2.X, 6);
        Assert.Equal(
            MatchValueFormat.FormatWithUnit(net.R1, MatchQuantity.Resistance,
                                            d.Settings.UnitFor(MatchQuantity.Resistance),
                                            d.Settings.SignificantDigits),
            t1.ResistanceText);

        d.Dispose();
    }

    /// <summary>
    /// A design with no shunt arm at all still gets its two terminations and no grounds — the ground
    /// rail this replaces was drawn only when a shunt existed, and a reference pin was drawn with it.
    /// </summary>
    [Fact]
    public void AnAllSeriesLadder_HasTwoTerminationsAndNoGrounds()
    {
        var net = new MatchNetwork { R1 = 50, R2 = 25 };
        net.Elements.Add(new MatchElement { Name = "L1", Type = ElementType.L, IsShunt = false, Value = 1e-9 });
        net.Elements.Add(new MatchElement { Name = "C1", Type = ElementType.C, IsShunt = false, Value = 1e-12 });

        var layout = MatchLadderLayout.Build(net, null, _ => "x", (_, r) => $"{r:F0} Ω");
        var model = MatchSchematicModel.Build(layout);

        Assert.Equal(2, model.Components.Count(c => c.Symbol == SymbolKind.TermG));
        Assert.DoesNotContain(model.Components, c => c.Symbol == SymbolKind.Ground);
        Assert.DoesNotContain(model.Components, c => c.Symbol == SymbolKind.Pin);
        Assert.Empty(model.ConnectionDots);
    }
}
