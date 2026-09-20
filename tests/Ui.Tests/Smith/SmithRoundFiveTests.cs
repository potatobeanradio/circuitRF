// ════════════════════════════════════════════════════════════════════════════
//  SmithRoundFiveTests.cs — the fifth round of owner items over the finished
//  tool (2026-09-19).
//
//  ONE TEST PER CLAIM, and only the claims whose failure would be SILENT.
//
//  The items with no test here are the ones whose evidence is a pointer event or
//  a pixel, and Ui.Tests may call no Avalonia runtime API:
//    • Escape disarming the zoom box from anywhere in the document, and a press
//      on the strip's background dropping both selections — both need a real key
//      or a real pointer, and both are one branch in the view;
//    • the Add / Insert menu glyphs at double size, which is two numbers and a
//      Header in the code-behind;
//    • the Load panel's own AXAML, whose columns mirror the generator table's.
//  What IS here is everything a wrong answer would look ordinary in.
// ════════════════════════════════════════════════════════════════════════════

using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Smith;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Render.Smith;
using CircuitRF.Ui.Smith;
using Xunit;

namespace CircuitRF.Ui.Tests.Smith;

public sealed class SmithRoundFiveTests
{
    private const double ChartZ0 = 50.0;

    /// <summary>Three rows and three elements — enough that a reorder moves something and enough
    /// that a highlight has to pick one curve out of several.</summary>
    private static SmithDesign Design()
    {
        var d = new SmithDesign();
        d.Chart.Z0Ohm = ChartZ0;
        d.Generator.Rows.Add(new SmithGeneratorRow(1.8e9, 12.0, -8.5));
        d.Generator.Rows.Add(new SmithGeneratorRow(2.0e9, 11.4, -9.1));
        d.Generator.Rows.Add(new SmithGeneratorRow(2.2e9, 10.9, -9.8));

        d.Elements.Add(new SmithElement
        {
            Kind = SmithElementKind.L, Placement = SmithPlacement.Series, Name = "L1",
            ActiveParameter = SmithParameter.L, Values = { LHenry = 2.2e-9 },
        });
        d.Elements.Add(new SmithElement
        {
            Kind = SmithElementKind.C, Placement = SmithPlacement.Shunt, Name = "C1",
            ActiveParameter = SmithParameter.C, Values = { CFarad = 1.5e-12 },
        });
        d.Elements.Add(new SmithElement
        {
            Kind = SmithElementKind.L, Placement = SmithPlacement.Series, Name = "L2",
            ActiveParameter = SmithParameter.L, Values = { LHenry = 3.3e-9 },
        });
        return d;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  1. A reorder drag previews the WHOLE drawing, not the dragged part of it
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>What the drag shows is what the drop produces.</b> Two owner reports came out of the
    /// previous preview, which moved the dragged column and left everything else on the pre-drag
    /// drawing: the part slid on top of the one already in that slot, and a shunt element's tap on
    /// the spine stayed behind while the part itself moved.
    /// </summary>
    /// <remarks>
    /// <b>The oracle is the COMMITTED move</b>, not a list of expected coordinates. A preview that
    /// agrees with a hand-written table of positions and disagrees with the drop is exactly the
    /// defect being fixed, so the drop is what it is compared against — component by component, and
    /// including the spine wires, which is the half the old preview had no way to move at all.
    /// </remarks>
    [Fact]
    public void AReorderPreviewIsTheDrawingTheDropWouldLeave()
    {
        var preview = new SmithChartViewModel(Design()).BuildReorderPreview(from: 0, to: 2);

        var committed = new SmithChartViewModel(Design());
        committed.MoveElement(0, 2);

        var a = preview.Model;
        var b = committed.Network.Model;

        Assert.Equal(b.Components.Count, a.Components.Count);
        foreach (var expected in b.Components)
        {
            var actual = Assert.Single(a.Components.Where(
                c => string.Equals(c.InstanceName, expected.InstanceName, StringComparison.Ordinal)));
            Assert.Equal(expected.X, actual.X, 6);
            Assert.Equal(expected.Y, actual.Y, 6);
        }

        // The spine. A wire is not a component and had no entry in the old preview's position map,
        // which is why a shunt's connection to the rail did not move with it.
        Assert.Equal(b.Wires.Count, a.Wires.Count);

        static string Shape(SchematicWire w)
            => string.Join(";", w.Points.Select(p => $"{p.X:F3},{p.Y:F3}"));

        Assert.Equal(b.Wires.Select(Shape).OrderBy(x => x, StringComparer.Ordinal).ToList(),
                     a.Wires.Select(Shape).OrderBy(x => x, StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// <b>Previewing is not editing.</b> The preview reorders the document's own element list to
    /// project it and must put it back — with no undo entry and no dirty mark, or moving the pointer
    /// across the strip and letting go outside it would leave the document changed and the title bar
    /// bulleted for a drag the user abandoned.
    /// </summary>
    [Fact]
    public void AReorderPreviewLeavesTheDocumentExactlyAsItWas()
    {
        var vm     = new SmithChartViewModel(Design());
        var before = vm.Design.Elements.Select(e => e.Name).ToList();

        vm.BuildReorderPreview(0, 2);
        vm.BuildReorderPreview(2, 0);
        vm.BuildReorderPreview(1, 1);   // a no-op slot, which must also not disturb anything

        Assert.Equal(before, vm.Design.Elements.Select(e => e.Name).ToList());
        Assert.False(vm.UndoRedo.CanUndo);
        Assert.False(vm.IsDirty);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  2. The Load panel
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>One row per generator row, carrying what the cascade lands on there</b> — and the numbers
    /// are <c>SmithReadings</c>', not a second arithmetic. A load impedance derived twice is two
    /// chances for a sign or a conjugate to be wrong in a quantity whose wrong value looks entirely
    /// ordinary, which is why the assertion is an identity against that one call rather than a
    /// hand-computed impedance.
    /// </summary>
    [Fact]
    public void TheLoadPanelReportsTheCascadesOwnReadingAtEveryGeneratorRow()
    {
        var vm = new SmithChartViewModel(Design());

        Assert.Equal(vm.Design.Generator.Rows.Count, vm.LoadRows.Count);

        for (int i = 0; i < vm.LoadRows.Count; i++)
        {
            double f = vm.Design.Generator.Rows[i].FrequencyHz;
            Assert.Equal(f, vm.LoadRows[i].FrequencyHz, 3);

            var z = SmithReadings.At(vm.Design, f).LoadZ;
            Assert.Equal(MatchValueFormat.FormatWithUnit(z.Real,      MatchQuantity.Resistance, "Ω", 4),
                         vm.LoadRows[i].ResistanceDisplay);
            Assert.Equal(MatchValueFormat.FormatWithUnit(z.Imaginary, MatchQuantity.Resistance, "Ω", 4),
                         vm.LoadRows[i].ReactanceDisplay);
            Assert.NotEqual(SmithLoadRowViewModel.Unavailable, vm.LoadRows[i].ReactanceDisplay);
        }
    }

    /// <summary>
    /// <b>It FOLLOWS the table and the cascade</b>, which is the whole reason it is derived rather
    /// than stored: a load panel that kept yesterday's numbers is a panel of plausible wrong ones,
    /// and nothing about it would look wrong.
    /// </summary>
    [Fact]
    public void TheLoadPanelMovesWithEveryEdit()
    {
        var vm     = new SmithChartViewModel(Design());
        string was = vm.LoadRows[0].ResistanceDisplay;

        // An element's value: the cascade lands somewhere else.
        vm.SetElementValue(0, SmithParameter.L, 12.0e-9, "test");
        Assert.NotEqual(was, vm.LoadRows[0].ResistanceDisplay);

        // A row added to the table: a row added to the panel, and undo takes both back.
        int rows = vm.LoadRows.Count;
        vm.AddGeneratorRowCommand.Execute(null);
        Assert.Equal(rows + 1, vm.LoadRows.Count);

        vm.UndoRedo.Undo();
        Assert.Equal(rows, vm.LoadRows.Count);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  3. Selecting a component highlights its trajectory
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Exactly one trajectory is emphasised, and it is the selected element's</b> — the answer to
    /// "which of these curves is this part". Deselecting puts every one of them back: a highlight
    /// that survived the deselection would be an emphasis pointing at nothing, and the next
    /// selection would appear to be two.
    /// </summary>
    [Fact]
    public void SelectingAnElementEmphasisesItsCurveAndOnlyItsCurve()
    {
        var vm = new SmithChartViewModel(Design());

        double[] Widths() => vm.ChartTraceKeys.Where(k => k.ElementIndex >= 0)
                                              .OrderBy(k => k.ElementIndex)
                                              .Select(k => k.Trace.Properties.LineWidth)
                                              .ToArray();

        // Nothing selected: every trajectory at the ordinary width, none faded.
        Assert.All(Widths(), w => Assert.Equal(SmithPlotBuilder.ElementLineWidth, w, 6));
        Assert.All(vm.ChartTraceKeys.Where(k => k.ElementIndex >= 0),
                   k => Assert.Equal(1.0, k.Trace.Properties.LineOpacity, 6));

        vm.SelectElement(1);

        var widths = Widths();
        Assert.Equal(SmithPlotBuilder.ElementLineWidthSelected, widths[1], 6);
        Assert.Equal(SmithPlotBuilder.ElementLineWidth,         widths[0], 6);
        Assert.Equal(SmithPlotBuilder.ElementLineWidth,         widths[2], 6);

        // …and the others fade back, which is what makes it readable on a busy chart.
        Assert.Equal(1.0, TraceFor(vm, 1).Properties.LineOpacity, 6);
        Assert.Equal(SmithPlotBuilder.ElementLineOpacityFaded, TraceFor(vm, 0).Properties.LineOpacity, 6);

        vm.ClearSelection();
        Assert.All(Widths(), w => Assert.Equal(SmithPlotBuilder.ElementLineWidth, w, 6));
        Assert.All(vm.ChartTraceKeys.Where(k => k.ElementIndex >= 0),
                   k => Assert.Equal(1.0, k.Trace.Properties.LineOpacity, 6));
    }

    /// <summary>
    /// <b>The highlight survives a refill.</b> Every committed edit rebuilds the trace collection
    /// from the design, so the emphasis has to be re-applied to the NEW objects — otherwise changing
    /// the selected part's value silently drops the very emphasis that says which curve is being
    /// changed, at the one moment the user is watching it move.
    /// </summary>
    [Fact]
    public void TheHighlightSurvivesTheRebuildAnEditCauses()
    {
        var vm = new SmithChartViewModel(Design());
        vm.SelectElement(2);

        vm.SetElementValue(2, SmithParameter.L, 4.7e-9, "test");

        Assert.Equal(SmithPlotBuilder.ElementLineWidthSelected,
                     TraceFor(vm, 2).Properties.LineWidth, 6);
    }

    /// <summary>
    /// <b>Nothing but the trajectories moves.</b> The load points, the band, the constant-Q arcs and
    /// above all the user's own overlays are what the cascade is being matched TO — fading them
    /// because a component was clicked would hide the target while the user aims at it.
    /// </summary>
    [Fact]
    public void TheHighlightLeavesEverythingThatIsNotATrajectoryAlone()
    {
        var vm = new SmithChartViewModel(Design());

        var others = vm.ChartTraceKeys.Where(k => k.ElementIndex < 0).ToList();
        Assert.NotEmpty(others);
        var before = others.Select(k => (k.Trace.Properties.LineWidth, k.Trace.Properties.LineOpacity))
                           .ToList();

        vm.SelectElement(0);

        Assert.Equal(before,
                     others.Select(k => (k.Trace.Properties.LineWidth, k.Trace.Properties.LineOpacity))
                           .ToList());
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  4. A Y-axis label sits beside the axis, not beside the canvas
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A Smith chart's disc is square and centred, so on a wide canvas the canvas's left edge is
    /// nowhere near the chart's.</b> That is the owner's report of 2026-09-19: the Smith Chart tool's
    /// chart region is a wide rectangle, and a label placed at the canvas edge landed a whole empty
    /// band away from the thing it names.
    ///
    /// <para>Asserted as a RELATION rather than a number — the inset is whatever the viewport says,
    /// and pinning it to a constant here would be a second copy of the margin arithmetic. What has to
    /// be true is that a wide canvas has a real inset, a square one has effectively none, and the
    /// answer is the disc's own left edge.</para>
    /// </summary>
    [Fact]
    public void TheChartInsetIsTheDiscsOwnEdge()
    {
        var plot = new SmithChartViewModel(Design()).ChartPlot;

        double wide   = PlotCanvasGeometry.ChartInset(plot, 1200.0, 300.0);
        double square = PlotCanvasGeometry.ChartInset(plot,  600.0, 600.0);

        // A canvas four times as wide as it is tall: the disc is a square of the HEIGHT, so nearly
        // half the width is empty on the left. A strip placed at 0 is that far from the chart.
        Assert.True(wide > 300.0, $"a 1200x300 canvas should inset the disc well past 300 px; got {wide}");

        // A square one: only the side margin, which is a per-cent of the width and not a band.
        Assert.True(square < 0.05 * 600.0, $"a square canvas should barely inset at all; got {square}");

        // And it IS the rectangle's left edge — the one the strips and the grid are both placed
        // from, asked of the viewport rather than derived a second time.
        var rect = PlotCanvasGeometry.ChartRect(plot, 1200.0, 300.0);
        Assert.Equal(rect.Left, wide, 6);
        Assert.True(rect.Width > 0 && rect.Height > 0);
    }

    /// <summary>
    /// <b>A Rect plot has no inset</b>, because it has no disc: its Y labels live inside the Skia
    /// margin and nothing is shifted. Asserted because the shift is applied unconditionally by the
    /// view, and a non-zero answer here would move every rectangular plot's label strips — which
    /// there are none of, so the symptom would be an empty column of the wrong width.
    /// </summary>
    [Fact]
    public void ARectPlotHasNoChartInset()
    {
        var rect = new Plot(PlotType.Rect, FreqUnit.GHz);
        Assert.Equal(0.0, PlotCanvasGeometry.ChartInset(rect, 1200.0, 300.0), 6);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  5. The close prompt and the dirty test name the same document kinds
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A dirty scratch <c>.csmith</c> let circuitRF quit with nothing asked</b> (owner report,
    /// 2026-09-19) — and so did a dirty <c>.wbond</c>.
    /// </summary>
    /// <remarks>
    /// <b>The two methods are one mechanism and this is the invariant between them.</b>
    /// <c>HasAnyDirtyWork</c> is what makes the close path STOP; <c>PromptSaveBeforeClose</c> is what
    /// it stops for. Both counted schematics, symbols, layouts, technologies and EM setups; only the
    /// first counted wBond and Smith Chart documents. So the close stopped, the prompt summed a
    /// total that named neither, read zero, and returned <c>true</c> — which the caller takes as
    /// "settled, safe to proceed". Nothing was reported anywhere; the work was simply gone.
    ///
    /// <para>Asserted on the source because nothing in this suite constructs a
    /// <c>WorkspaceViewModel</c> — it needs a live Dock factory and a shell window — and because the
    /// claim is about two method bodies AGREEING, which is exactly what a source scan can say and a
    /// behavioural test of one of them cannot. A document kind added to one and not the other is a
    /// kind whose unsaved work vanishes silently, so this fails the moment they diverge again.</para>
    /// </remarks>
    [Fact]
    public void EveryDocumentKindTheDirtyTestCountsIsAlsoOfferedBySaveBeforeClose()
    {
        string source = ReadRepoFile(Path.Combine("src", "Ui", "ViewModels", "WorkspaceViewModel.cs"));

        string dirty  = MethodBody(source, "public bool HasAnyDirtyWork(bool includeFloated = true)");
        string prompt = MethodBody(source,
            "public async Task<bool> PromptSaveBeforeClose(Window owner, string context = \"closing\", bool includeFloated = true)");

        string[] kinds =
        [
            "SchematicDocument", "SymbolEditorDocument", "LayoutDocument", "TechDocument",
            "EmSetupDocument", "DataDisplayDocument", "WBondDocument", "SmithChartDocument",
        ];

        foreach (string kind in kinds)
        {
            Assert.Contains(kind, dirty,  StringComparison.Ordinal);
            Assert.Contains(kind, prompt, StringComparison.Ordinal);
        }

        // The scratch lists too — a never-saved document is the case with the most to lose, since it
        // has no file on disk to fall back to at all.
        foreach (string list in new[] { "_scratchWBonds", "_scratchSmithCharts" })
        {
            Assert.Contains(list, dirty,  StringComparison.Ordinal);
            Assert.Contains(list, prompt, StringComparison.Ordinal);
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  6. Tools ▸ Smith Chart opens torn off unless a docked tab would be big enough
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The window is three regions and a docked tab in a standard shell has too little of any of
    /// them</b> (owner instruction, 2026-09-19), so it opens in a window of its own — through the
    /// same tear-off path harmonicaRF and a user's own drag take — unless the document region is
    /// already a full standard workspace window or larger.
    /// </summary>
    /// <remarks>
    /// Source-asserted for <c>HarmonicaOwnWindowTests</c>' own reason. The ORDER is asserted as well
    /// as the call: the region has to be measured BEFORE the tab is opened, because the bounds of a
    /// control added in this dispatcher turn are not laid out until the next one — so a measurement
    /// taken afterwards reads zero, and zero is "too small" for every shell there is.
    /// </remarks>
    [Fact]
    public void NewSmithChart_FloatsUnlessTheDockedRegionIsAlreadyAFullWindow()
    {
        string source = ReadRepoFile(Path.Combine("src", "Ui", "ViewModels", "WorkspaceViewModel.cs"));
        string body   = MethodBody(source, "private void NewSmithChart()");

        Assert.Contains("DockedDocumentRegionFitsStandardWindow()", body, StringComparison.Ordinal);
        Assert.Contains("OpenDocumentInOwnWindow(doc)",             body, StringComparison.Ordinal);

        Assert.True(body.IndexOf("DockedDocumentRegionFitsStandardWindow()", StringComparison.Ordinal)
                  < body.IndexOf("_factory.OpenDocument(doc)", StringComparison.Ordinal),
            "the document region must be measured before the tab is opened — see the method's own remarks.");
    }

    /// <summary>
    /// <b>The yardstick is the SHIPPED window size, not the shell's current one.</b> A shell the user
    /// has dragged small would otherwise lower its own bar and dock the document into a region too
    /// small to use — which is the outcome the instruction is about.
    /// </summary>
    [Fact]
    public void TheStandardWindowSizeIsTheOneWorkspaceWindowOpensAt()
    {
        string axaml = ReadRepoFile(Path.Combine("src", "Ui", "Views", "WorkspaceWindow.axaml"));

        Assert.Contains($"Width=\"{CircuitRF.Ui.ViewModels.WorkspaceViewModel.StandardWorkspaceWidth}\"",
                        axaml, StringComparison.Ordinal);
        Assert.Contains($"Height=\"{CircuitRF.Ui.ViewModels.WorkspaceViewModel.StandardWorkspaceHeight}\"",
                        axaml, StringComparison.Ordinal);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static Trace TraceFor(SmithChartViewModel vm, int elementIndex)
        => Assert.Single(vm.ChartTraceKeys.Where(k => k.ElementIndex == elementIndex)).Trace;

    /// <inheritdoc cref="HarmonicaOwnWindowTests"/>
    private static string MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' was not found — it has been renamed or removed.");

        int brace = source.IndexOf('{', start);
        int semi  = source.IndexOf(';', start);
        if (brace < 0 || (semi >= 0 && semi < brace)) return source[start..(semi + 1)];

        int depth = 0;
        for (int i = brace; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[start..(i + 1)];
        }

        Assert.Fail($"'{signature}' has no matching closing brace.");
        return "";
    }

    private static string ReadRepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root (no CLAUDE.md found walking up from this test file).");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }
}
