// ================================================================
//  MatchTribandDesignerTests.cs  —  match.md §18.5 in the Designer: the Bands selector's third
//  segment, the f5/f6 row, the effective-band note for three bands, a Chebyshev-only solutions
//  panel, TWO gap lines in the status strip, and one undo back to Dual.
//
//  Same discipline as every earlier round: view-model, geometry and source-scan tests, never pixels.
// ================================================================

using System;
using System.IO;
using System.Linq;
using CircuitRF.Core.Matching;
using CircuitRF.Ui.Matching;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Match;

public sealed class MatchTribandDesignerTests(ITestOutputHelper output)
{
    // ── Fixture ───────────────────────────────────────────────────────────────

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

    /// <summary>match.md §18.5's own problem: 50 Ω ‖ 4 pF over three already-mirrored bands.</summary>
    private static MatchDesign Problem(int order = 2) => new()
    {
        BandCount = 3,
        F1 = 0.5e9, F2 = 0.6e9, F3 = 0.9e9, F4 = 1.1e9, F5 = 1.65e9, F6 = 1.98e9,
        Order = order,
        Response = ResponseShape.ChebyshevFano,
        Term1 = new Termination(50.0, ReactanceKind.C, TerminationTopology.Parallel, 4e-12),
        Term2 = Termination.Resistive(50.0),
        AnalysisEnd = AnalysisEndChoice.Term1,
    };

    private static MatchDesign DualBandProblem() => new()
    {
        BandCount = 2,
        F1 = 0.5e9, F2 = 0.6e9, F3 = 1.65e9, F4 = 1.98e9,
        Order = 2,
        Response = ResponseShape.ChebyshevFano,
        Term1 = new Termination(50.0, ReactanceKind.C, TerminationTopology.Parallel, 4e-12),
        Term2 = Termination.Resistive(50.0),
        AnalysisEnd = AnalysisEndChoice.Term1,
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Xaml() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "Views", "Match", "MatchDesignerWindow.axaml"));

    // ══ 1 — the specification pane ══════════════════════════════════════════

    /// <summary>
    /// The selector offers three segments, and Tri reveals the f5/f6 row while leaving f3/f4 shown.
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void TheBandsSelector_OffersTri_AndTriShowsTheThirdBandRow()
    {
        Assert.Equal(["Single", "Dual", "Tri"], MatchDesignerViewModel.BandsOptions);

        var (_, _, d) = Open(DualBandProblem());
        Assert.True(d.IsDualBand);
        Assert.False(d.IsTriBand);

        d.BandsChoice = "Tri";
        d.WaitForAnalysis();
        Assert.Equal(3, d.BandCount);
        Assert.True(d.IsDualBand);
        Assert.True(d.IsTriBand);

        string xaml = Xaml();
        Assert.Contains("{Binding F5Entry, Mode=TwoWay}", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding F6Entry, Mode=TwoWay}", xaml, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsTriBand}\"", xaml, StringComparison.Ordinal);

        d.Dispose();
    }

    /// <summary>
    /// <b>Dual → Tri moves the user's second band OUT to f5/f6 and seeds a new middle band</b>, which
    /// is the shape of match.md §18.3's rule: the middle band is the one that is kept and that defines
    /// omega0, so hanging a third band off the end would immediately mirror it onto the second.
    /// </summary>
    [Fact]
    public void SwitchingToTri_MovesTheSecondBandOutAndSeedsAMiddleOne()
    {
        var (_, _, d) = Open(DualBandProblem());
        double wasF3 = d.F3, wasF4 = d.F4;

        d.BandsChoice = "Tri";
        d.WaitForAnalysis();

        Assert.Equal(0.5e9, d.F1, 1.0);
        Assert.Equal(0.6e9, d.F2, 1.0);
        Assert.Equal(wasF3, d.F5, 1.0);
        Assert.Equal(wasF4, d.F6, 1.0);
        Assert.True(d.F3 > d.F2 && d.F4 > d.F3 && d.F5 > d.F4,
            $"f2 {d.F2} f3 {d.F3} f4 {d.F4} f5 {d.F5}");

        // And the mode opens on a design that synthesises rather than on a refusal.
        Assert.False(d.Status.IsRefused, d.Status.Refusal?.Message);
        output.WriteLine(d.Status.Text);

        d.Dispose();
    }

    /// <summary>
    /// The effective-band note names <b>every</b> band that moved, and says the middle one was kept.
    /// </summary>
    [Fact]
    public void TheEffectiveBandNote_NamesEveryBandThatMoved()
    {
        var design = Problem();
        design.F3 = 0.8e9;
        design.F4 = 1.25e9;
        var (_, _, d) = Open(design);
        d.WaitForAnalysis();

        output.WriteLine(d.EffectiveBandNote);
        Assert.Contains("band 1 to", d.EffectiveBandNote, StringComparison.Ordinal);
        Assert.Contains("band 3 to", d.EffectiveBandNote, StringComparison.Ordinal);
        Assert.Contains("band 2 is kept", d.EffectiveBandNote, StringComparison.Ordinal);

        d.Dispose();
    }

    /// <summary>An already-mirrored tri-band spec says nothing, exactly as the dual case does.</summary>
    [Fact]
    public void AnAlreadyMirroredTriBandSpec_SaysNothing()
    {
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();
        Assert.Equal("", d.EffectiveBandNote);
        d.Dispose();
    }

    // ══ 2 — the status strip ════════════════════════════════════════════════

    /// <summary>
    /// <b>Two gap lines</b>, both rendered, and both in the one-line <c>Text</c> a tooltip reads.
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void TheStatusStrip_ShowsBothGaps()
    {
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();

        output.WriteLine(d.Status.Text);
        Assert.Contains("gap 0.6–0.9 GHz", d.Status.GapText, StringComparison.Ordinal);
        Assert.Contains("gap 1.1–1.65 GHz", d.Status.GapText2, StringComparison.Ordinal);
        Assert.Contains(d.Status.GapText, d.Status.Text, StringComparison.Ordinal);
        Assert.Contains(d.Status.GapText2, d.Status.Text, StringComparison.Ordinal);

        // Symmetrical bands put the same mismatch in both gaps — the mirror rule showing through.
        Assert.Equal(d.Status.GapMaxS11, d.Status.Gap2MaxS11, 1e-3);
        Assert.Contains("{Binding Status.GapText2}", Xaml(), StringComparison.Ordinal);

        d.Dispose();
    }

    /// <summary>A dual-band design has one gap line and an empty second one.</summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void ADualBandDesign_HasOnlyOneGapLine()
    {
        var (_, _, d) = Open(DualBandProblem());
        d.WaitForAnalysis();
        Assert.NotEqual("", d.Status.GapText);
        Assert.Equal("", d.Status.GapText2);
        d.Dispose();
    }

    // ══ 3 — the solutions panel ═════════════════════════════════════════════

    /// <summary>
    /// Tri-band cards read <b>"Chebyshev · tri-band · order n"</b>, and every listed row is Chebyshev
    /// — Butterworth has no member over a union of intervals (match.md §18.5), so searching it would
    /// list a column of identical refusals.
    /// </summary>
    [Fact]
    public void EveryTriBandRow_IsChebyshevAndSaysTriBand()
    {
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();

        Assert.NotEmpty(d.AllSolutions);
        foreach (var row in d.AllSolutions)
        {
            Assert.Equal(3, row.BandCount);
            Assert.Equal("tri-band", row.ShapeWord);
            Assert.Equal(ResponseShape.ChebyshevFano, row.Response);
            Assert.Equal(NetworkForm.Bandpass, row.Form);
            Assert.InRange(row.Order, 1, MatchOrders.MaxTriBandOrder);
        }
        output.WriteLine(d.AllSolutions[0].TitleText);
        Assert.StartsWith(
            "Chebyshev · tri-band · order", d.AllSolutions[0].TitleText, StringComparison.Ordinal);

        d.Dispose();
    }

    /// <summary>
    /// The order picker still counts match points per band, at 4n elements — and tri-band offers
    /// orders 1 to 6 where dual-band stops at 3 (match.md §19 item 4: a narrow middle band excludes
    /// its gaps at no order below 4, so 4–6 are the only orders at which such a spec is three bands
    /// at all; the price is 16 / 20 / 24 parts, and the hint says so).
    /// </summary>
    [Fact]
    public void TheOrderPicker_CountsMatchPointsPerBand()
    {
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();

        Assert.Equal([1, 2, 3, 4, 5, 6], d.OrderOptions);
        Assert.Contains("4, 8, 12, 16, 20, 24 elements (4n)", d.ElementCountHint, StringComparison.Ordinal);
        Assert.Contains("Match points PER BAND", d.OrderTooltip, StringComparison.Ordinal);

        d.Dispose();
    }

    /// <summary>
    /// <b>The element-count hint states the FORMULA, because parity is the terminations' and not the
    /// order's</b> (match.md §18.5): a mixed pair reads 4n, a like pair 4n + 2.
    /// </summary>
    [Fact]
    public void TheElementCountHint_FollowsTheTerminationPairsParity()
    {
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();
        Assert.Contains("(4n)", d.ElementCountHint, StringComparison.Ordinal);

        // Make it a like pair — both parallel, both reactive. NOT waited on: the hint and the order
        // options are computed from the design synchronously, and waiting here means waiting for
        // the tri-band odd-family solution search, which is minutes of real CPU (Core RESOLVED
        // §MN-DCB2) and was what kept the whole Ui.Tests host alive after every other test finished.
        d.SetTermination(2, new Termination(25.0, ReactanceKind.C, TerminationTopology.Parallel, 1e-12));

        output.WriteLine(d.ElementCountHint);
        Assert.Contains("(4n + 2)", d.ElementCountHint, StringComparison.Ordinal);
        Assert.Contains("6, 10, 14, 18, 22, 26 elements", d.ElementCountHint, StringComparison.Ordinal);
        Assert.Equal([1, 2, 3, 4, 5, 6], d.OrderOptions);

        d.Dispose();
    }

    // ══ 3b — band-edge validation ═══════════════════════════════════════════

    /// <summary>
    /// <b>Frequencies typed out of order are SORTED into order, not refused</b> (owner-reported,
    /// 2026-08-28: entering the wrong order made nothing work).
    /// </summary>
    /// <remarks>
    /// f1…f6 are one ordered list — the passband boundaries in increasing frequency — so a user who
    /// puts 1.65 GHz into f3 when f5 already holds 0.9 has said what they mean unambiguously, and
    /// sorting is the only reading that keeps every number they typed. The synthesis still refuses a
    /// spec that sorting cannot rescue (two equal edges), and nothing is said about the reorder
    /// because the fields themselves visibly renumber.
    /// </remarks>
    [Fact]
    public void BandEdgesTypedOutOfOrder_AreSortedIntoOrder()
    {
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();

        // 1.5 GHz belongs between f4 (1.1) and f5 (1.65); it was typed into f3.
        d.F3 = 1.5e9;
        d.WaitForAnalysis();

        double[] edges = [d.F1, d.F2, d.F3, d.F4, d.F5, d.F6];
        output.WriteLine(string.Join(" ", edges.Select(v => (v / 1e9).ToString("0.###"))));

        // Everything the user typed is still there, in order, and the design synthesises again.
        Assert.Equal(edges.Order(), edges);
        Assert.Equal([0.5e9, 0.6e9, 1.1e9, 1.5e9, 1.65e9, 1.98e9], edges);
        Assert.False(d.Status.IsRefused, d.Status.Refusal?.Message);

        d.Dispose();
    }

    /// <summary>
    /// A single-band design's stale f3…f6 take no part in the sort — only the edges the current band
    /// count uses do.
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void OnlyTheEdgesTheBandCountUses_TakePartInTheSort()
    {
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();

        d.BandsChoice = "Single";
        d.WaitForAnalysis();

        // f3..f6 still hold the tri-band values, all of them ABOVE f2 — and an f1 typed above f2 is
        // sorted against f2 alone, not dragged behind them.
        d.F1 = 0.7e9;
        d.WaitForAnalysis();

        output.WriteLine($"{d.F1 / 1e9:0.###} {d.F2 / 1e9:0.###}  (f3 {d.F3 / 1e9:0.###})");
        Assert.Equal(0.6e9, d.F1, 1.0);
        Assert.Equal(0.7e9, d.F2, 1.0);
        Assert.Equal(0.9e9, d.F3, 1.0);

        d.Dispose();
    }

    // ══ 4 — persistence and undo ════════════════════════════════════════════

    /// <summary>
    /// The third band round-trips through the payload and through the echo parameters, and Tri → Dual
    /// → undo comes back to Tri with all six edges intact.
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void TheThirdBandRoundTripsAndUndoComesBack()
    {
        var (vm, comp, d) = Open(DualBandProblem());
        d.WaitForAnalysis();

        // Dual -> Tri, then the third band typed in full, is the gesture that writes the payload.
        d.BandsChoice = "Tri";
        d.F3 = 0.9e9;
        d.F4 = 1.1e9;
        d.F5 = 1.65e9;
        d.F6 = 1.98e9;
        d.WaitForAnalysis();

        Assert.True(MatchEmbedding.TryDecode(
            comp.Parameters.First(p => p.Name == "Design").Expression, out var stored));
        Assert.Equal(3, stored!.BandCount);
        Assert.Equal(1.65e9, stored.F5, 1.0);
        Assert.Equal(1.98e9, stored.F6, 1.0);

        string Echo(string name) => comp.Parameters.First(p => p.Name == name).Expression;
        Assert.Equal("3", Echo("Bands"));
        Assert.Equal(
            1.65, double.Parse(Echo("F5"), System.Globalization.CultureInfo.InvariantCulture), 1e-6);
        Assert.Equal(
            1.98, double.Parse(Echo("F6"), System.Globalization.CultureInfo.InvariantCulture), 1e-6);

        var defaults = ComponentTypeRegistry.DefaultParameters(SymbolKind.Match, 0);
        Assert.Contains(defaults, p => p.Name == "F5");
        Assert.Contains(defaults, p => p.Name == "F6");

        d.BandsChoice = "Dual";
        d.WaitForAnalysis();
        Assert.Equal(2, d.BandCount);

        vm.UndoRedo.Undo();
        d.WaitForAnalysis();
        Assert.Equal(3, d.BandCount);
        Assert.Equal(1.65e9, d.F5, 1.0);
        Assert.Equal(1.98e9, d.F6, 1.0);

        d.Dispose();
    }

    /// <summary>
    /// The flattened cell's record and the Properties summary both name all three bands.
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void FlattenAndThePropertiesSummary_NameAllThreeBands()
    {
        var (vm, comp, d) = Open(Problem());
        d.WaitForAnalysis();

        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(vm, comp, showClose: false);
        output.WriteLine(editor.MatchBandSummary);
        Assert.Equal("0.5 – 0.6 & 0.9 – 1.1 & 1.65 – 1.98 GHz", editor.MatchBandSummary);
        Assert.Contains("tri-band", editor.MatchOrderSummary, StringComparison.Ordinal);

        d.Dispose();
    }
}
