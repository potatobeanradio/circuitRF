// ================================================================
//  MatchMultibandDesignerTests.cs  —  match.md §18 in the Designer: the Bands selector, the f3/f4
//  row, the effective-band note, an order picker that counts match points per band, a solutions
//  panel that lists bandpass rows only, the status strip's gap line, and one undo back to Single.
//
//  Same discipline as every earlier round: view-model, geometry and source-scan tests, never pixels.
// ================================================================

using System;
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

public sealed class MatchMultibandDesignerTests(ITestOutputHelper output)
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

    /// <summary>
    /// match.md §18.4's problem: 20 Ω ‖ 2.5 pF into 50 Ω, over the two Wi-Fi bands as REQUESTED —
    /// which do not mirror, so §18.3's widening applies and the note has something to say.
    /// </summary>
    private static MatchDesign Problem(int order = 2) => new()
    {
        BandCount = 2,
        F1 = 2.4e9, F2 = 2.5e9, F3 = 5.15e9, F4 = 5.85e9,
        Order = order,
        Response = ResponseShape.ChebyshevFano,
        Term1 = new Termination(20.0, ReactanceKind.C, TerminationTopology.Parallel, 2.5e-12),
        Term2 = Termination.Resistive(50.0),
        AnalysisEnd = AnalysisEndChoice.Term1,
    };

    /// <summary>The same terminations and first band, single-band — what the mode switch starts from.</summary>
    private static MatchDesign SingleBandProblem() => new()
    {
        F1 = 2.4e9, F2 = 2.5e9,
        Order = 2,
        Response = ResponseShape.ChebyshevFano,
        Term1 = new Termination(20.0, ReactanceKind.C, TerminationTopology.Parallel, 2.5e-12),
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
    /// Switching to Dual shows the f3/f4 row and states what §18.3's symmetrisation did; a spec that
    /// already mirrors says nothing, because a note about a widening that did not happen is noise.
    /// </summary>
    [Fact]
    public void SwitchingToDual_ShowsTheSecondBandRow_AndTheEffectiveBandNoteWhenBandsWereWidened()
    {
        var (_, _, d) = Open(SingleBandProblem());
        Assert.False(d.IsDualBand);
        Assert.Equal("", d.EffectiveBandNote);

        d.BandsChoice = "Dual";
        d.WaitForAnalysis();
        Assert.True(d.IsDualBand);
        Assert.Equal(2, d.BandCount);

        // The seed is the geometric mirror of band 1, so the mode opens on a design that
        // synthesises rather than on a refusal about two zeroes.
        Assert.True(d.F3 > d.F2);
        Assert.True(d.F4 > d.F3);
        Assert.Equal("", d.EffectiveBandNote);

        // Now the REQUESTED Wi-Fi bands, which do not mirror.
        d.F3 = 5.15e9;
        d.F4 = 5.85e9;
        d.WaitForAnalysis();
        output.WriteLine(d.EffectiveBandNote);
        Assert.Contains("Band 1 widened to 2.201–2.5 GHz", d.EffectiveBandNote, StringComparison.Ordinal);
        Assert.Contains("3.588 GHz", d.EffectiveBandNote, StringComparison.Ordinal);

        d.Dispose();
    }

    /// <summary>The f3/f4 rows are HIDDEN, not disabled, while the design is single-band.</summary>
    /// <remarks>
    /// f3 and f4 are not inputs to a single-band design at all; a dimmed pair of frequencies reads as
    /// "set these later" rather than "these do not exist here" — and this window has already been
    /// bitten once by a disabled row that looked live (the Ripple field, 2026-08-20).
    /// </remarks>
    [Fact]
    public void TheSecondBandRows_AreBoundToIsDualBand_AndUseTheSameInlineEditor()
    {
        string xaml = Xaml();
        foreach (string entry in (string[])["F3Entry", "F4Entry"])
        {
            int i = xaml.IndexOf(entry, StringComparison.Ordinal);
            Assert.True(i >= 0, $"{entry} is not bound in the window");
            string around = xaml[Math.Max(0, i - 500)..i];
            Assert.Contains("IsVisible=\"{Binding IsDualBand}\"", around, StringComparison.Ordinal);
            Assert.Contains("ctl:InlineEditText", around, StringComparison.Ordinal);
        }

        Assert.Contains("BandsOptions", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding BandsChoice, Mode=TwoWay}", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding EffectiveBandNote}", xaml, StringComparison.Ordinal);
    }

    // ══ 2 — the order picker ════════════════════════════════════════════════

    /// <summary>
    /// Dual-band order is match points PER BAND, so the picker offers 1, 2, 3 and the hint counts 4n.
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void TheOrderPicker_OffersOneTwoThreeWhileDual_AndCountsFourNElements()
    {
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();

        Assert.Equal([1, 2, 3], d.OrderOptions);
        Assert.Equal(["1", "2", "3"], d.OrderChoices);
        Assert.Contains("4n", d.ElementCountHint, StringComparison.Ordinal);
        Assert.Contains("4, 8, 12", d.ElementCountHint, StringComparison.Ordinal);
        Assert.Contains("PER BAND", d.OrderTooltip, StringComparison.Ordinal);

        d.Dispose();
    }

    /// <summary>
    /// Switching an out-of-range order to Dual re-validates it — <b>silently</b>.
    /// </summary>
    /// <remarks>
    /// Owner-reported, 2026-08-28: the pane used to say <i>"Order 3 cannot absorb both ends now: …"</i>
    /// after a band-count change, and that is clutter — the order moved because a different solution
    /// card is now applied, and that card names its own order. The order picker and the green-bordered
    /// card are the two places the answer is already visible.
    /// </remarks>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void SwitchingToDual_RevalidatesTheOrder_AndSaysNothingAboutIt()
    {
        var single = SingleBandProblem();
        single.Order = 6;
        var (_, _, d) = Open(single);
        Assert.Equal(6, d.Order);

        d.BandsChoice = "Dual";
        d.WaitForAnalysis();

        output.WriteLine($"order {d.Order}, {d.AllSolutions.Count} rows");
        Assert.Contains(d.Order, (int[])[1, 2, 3]);

        // ── AND EVERY ROW BELONGS TO THIS SEARCH ─────────────────────────────
        //
        // The list is the reason this test found a race rather than a typo. A superseded search's
        // batch used to check its generation OUTSIDE RefreshGate, block on the gate the superseding
        // edit was holding, and then land single-band rows into the list that edit had just cleared —
        // 112 rows in a search that yields 62, one stray badged current, and the auto-solve dragging
        // the design back to that stray's order 6. See QueueSolutionSearch's Publish.
        Assert.All(d.AllSolutions, r => Assert.Equal(2, r.BandCount));
        Assert.All(d.AllSolutions, r => Assert.Contains(r.Order, (int[])[1, 2, 3]));

        // ...and the same in the other direction, which is the report's own gesture.
        d.BandsChoice = "Single";
        d.WaitForAnalysis();
        Assert.Contains(d.Order, d.OrderOptions);

        d.Dispose();
    }

    /// <summary>
    /// <b>A like-topology pair switched to Dual now synthesises</b> rather than refusing — MN-MB2's
    /// weighted family gives it an odd arm count, whose two ends share one orientation.
    /// </summary>
    /// <remarks>
    /// This test used to assert the opposite, and the refusal it asserted is gone with match.md
    /// §18.2's own correction. What is left of the old claim is the ELEMENT COUNT: the same order
    /// buys 4n + 2 elements instead of 4n, and the order picker offers the same 1..3 either way,
    /// because order means match points per band in both families.
    /// </remarks>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void SwitchingToDualWithALikePair_KeepsItsOrders_AndTakesTheOddArmCount()
    {
        var like = SingleBandProblem();
        like.Order = 2;
        like.Term2 = new Termination(50.0, ReactanceKind.C, TerminationTopology.Parallel, 0.3e-12);
        var (_, _, d) = Open(like);

        d.BandsChoice = "Dual";
        d.WaitForAnalysis();

        Assert.Equal([1, 2, 3], d.OrderOptions);
        Assert.False(d.Status.IsRefused, d.Status.Refusal?.Message);
        output.WriteLine($"{d.ElementCountHint}  |  {d.Status.Text}");
        Assert.Contains("(4n + 2)", d.ElementCountHint, StringComparison.Ordinal);
        Assert.True(d.Elements.Count >= 4 * d.Order + 2, $"{d.Elements.Count} elements");

        d.Dispose();
    }

    // ══ 3 — the solutions panel ═════════════════════════════════════════════

    /// <summary>
    /// <b>Bandpass rows only, and every card names the band count rather than the form</b> (match.md
    /// §18.6/§18.7): while multiband there is one form, so the form word would be the same on every
    /// card and would say nothing.
    /// </summary>
    [Fact]
    public void TheSearch_PublishesBandpassRowsOnly_TitledDualBand()
    {
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();

        Assert.NotEmpty(d.AllSolutions);
        Assert.All(d.AllSolutions, r => Assert.Equal(NetworkForm.Bandpass, r.Form));
        Assert.All(d.AllSolutions, r => Assert.Equal(2, r.BandCount));
        Assert.All(d.AllSolutions, r => Assert.Contains("dual-band", r.TitleText, StringComparison.Ordinal));

        // Orders inside 1..3, and Chebyshev/Butterworth only — the double-match Chebyshev and Bessel
        // have no dual-band prototype (§18.2), so searching for them would spend cells producing a
        // refusal.
        //
        // ORDER 1 IS OFFERED AND FINDS NOTHING HERE, which is a fact about this problem and not a
        // defect: a four-element ladder has exactly ONE Norton pair, and one transform cannot reach
        // the 4.84 : 1 the far end needs inside its positivity range. The picker still offers it (the
        // basis synthesises; it is the transform rack that cannot finish), and the panel says so with
        // MN-1's own TransformsCannotReachTarget. Orders 2 and 3 have five and nine pairs.
        var orders = d.AllSolutions.Select(r => r.Order).Distinct().Order().ToList();
        Assert.All(orders, o => Assert.InRange(o, 1, 3));
        Assert.Contains(2, orders);
        Assert.Contains(3, orders);
        Assert.All(d.AllSolutions,
            r => Assert.Contains(r.Response,
                (ResponseShape[])[ResponseShape.ChebyshevFano, ResponseShape.Butterworth]));

        var chebyshev = d.AllSolutions.First(r => r.Response == ResponseShape.ChebyshevFano);
        output.WriteLine(chebyshev.TitleText);
        Assert.StartsWith("Chebyshev · dual-band · order ", chebyshev.TitleText, StringComparison.Ordinal);

        d.Dispose();
    }

    /// <summary>The filter replaces its three form toggles with the one line that says why.</summary>
    [Fact]
    public void TheFilter_HidesItsFormGroupWhileDual_AndSaysWhy()
    {
        var (_, _, d) = Open(SingleBandProblem());
        d.WaitForAnalysis();
        Assert.True(d.Filter.ShowFormToggles);
        Assert.Equal("", d.Filter.FormGroupNote);

        d.BandsChoice = "Dual";
        d.WaitForAnalysis();
        Assert.False(d.Filter.ShowFormToggles);
        Assert.Contains("bandpass only", d.Filter.FormGroupNote, StringComparison.Ordinal);
        Assert.DoesNotContain("§", d.Filter.FormGroupNote, StringComparison.Ordinal);

        Assert.Contains("{Binding Filter.ShowFormToggles}", Xaml(), StringComparison.Ordinal);
        Assert.Contains("{Binding Filter.FormGroupNote}", Xaml(), StringComparison.Ordinal);

        d.Dispose();
    }

    // ══ 4 — the status strip ════════════════════════════════════════════════

    /// <summary>
    /// The gap mismatch is the design working (match.md §18.4), so it is stated as a number beside
    /// the in-band figure — and it is absent, not zero, for a single band.
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void TheStatusStrip_CarriesTheGapLineWhileDual_AndNothingWhenSingle()
    {
        var (_, _, single) = Open(SingleBandProblem());
        single.WaitForAnalysis();
        Assert.Equal("", single.Status.GapText);
        Assert.DoesNotContain("gap", single.Status.Text, StringComparison.Ordinal);
        single.Dispose();

        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();

        output.WriteLine(d.Status.Text);
        Assert.Contains("gap 2.5–5.15 GHz", d.Status.GapText, StringComparison.Ordinal);
        Assert.Contains("max |S11|", d.Status.GapText, StringComparison.Ordinal);
        Assert.Contains(d.Status.GapText, d.Status.Text, StringComparison.Ordinal);

        // §18.4's own number for the eight-element network, read off the strip rather than recomputed.
        Assert.Equal(0.4454, d.Status.GapMaxS11, 0.01);
        Assert.Contains("{Binding Status.GapText}", Xaml(), StringComparison.Ordinal);

        d.Dispose();
    }

    /// <summary>
    /// The response plots span the EFFECTIVE outer pair, so both bands and the gap are on screen —
    /// a plot cropped to one band would hide the mechanism.
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void ThePlotBand_SpansBothBandsAndTheGap()
    {
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();

        var f = d.PlotFrequencies();
        Assert.True(f[0] < 2.2008547e9, $"the plot starts at {f[0]:0.###e+00}, inside band 1");
        Assert.True(f[^1] > 5.85e9, $"the plot ends at {f[^1]:0.###e+00}, inside band 2");

        d.Dispose();
    }

    // ══ 5 — undo ════════════════════════════════════════════════════════════

    /// <summary>
    /// The band count is an ordinary specification edit, so ONE undo puts the single-band design back
    /// — nothing new was needed for it, which is the claim.
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void OneUndo_RestoresSingleBand()
    {
        var (vm, comp, d) = Open(SingleBandProblem());
        d.WaitForAnalysis();

        d.BandsChoice = "Dual";
        d.WaitForAnalysis();
        Assert.Equal(2, d.BandCount);

        vm.UndoRedo.Undo();
        Assert.True(MatchEmbedding.TryDecode(
            comp.Parameters.First(p => p.Name == "Design").Expression, out var back));
        Assert.Equal(1, back!.BandCount);

        d.Dispose();
    }

    // ══ 6 — the echo parameters ═════════════════════════════════════════════

    /// <summary>
    /// <c>Bands</c>, <c>F3</c> and <c>F4</c> join the echoes beside <c>F1</c>/<c>F2</c>, so a
    /// dual-band design can be read off the schematic page itself.
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void TheEchoParameters_CarryTheBandCountAndTheSecondBand()
    {
        var (_, comp, d) = Open(SingleBandProblem());
        d.BandsChoice = "Dual";
        d.F3 = 5.15e9;
        d.F4 = 5.85e9;
        d.WaitForAnalysis();

        string Echo(string name) => comp.Parameters.First(p => p.Name == name).Expression;
        Assert.Equal("2", Echo("Bands"));
        Assert.Equal(5.15, double.Parse(Echo("F3"), System.Globalization.CultureInfo.InvariantCulture), 1e-6);
        Assert.Equal(5.85, double.Parse(Echo("F4"), System.Globalization.CultureInfo.InvariantCulture), 1e-6);

        // ...and they are echoes: the component type declares them, and nothing reads them back.
        var defaults = ComponentTypeRegistry.DefaultParameters(SymbolKind.Match, 0);
        Assert.Contains(defaults, p => p.Name == "Bands" && p.Expression == "1");
        Assert.Contains(defaults, p => p.Name == "F3");
        Assert.Contains(defaults, p => p.Name == "F4");

        d.Dispose();
    }

    // ══ 7 — a band-count change lands on a solution ═════════════════════════

    /// <summary>
    /// <b>Switching Single ↔ Dual leaves the design ON a solution whenever one exists</b>
    /// (owner-reported, 2026-08-28: "when user changes the Single/Dual combobox selection, no
    /// solution is selected. A solution (if it exists) must be selected").
    /// </summary>
    /// <remarks>
    /// A band-count change rebuilds the LADDER, not just the target — a dual-band network has twice
    /// the arms and different element names — so every stored <c>TransformRecord</c> names an element
    /// that no longer exists. The rack is dropped, Π N² lands on 1 against a target of several, and
    /// the design sat on nothing while the panel filled with dozens of rows that would have reached.
    ///
    /// <para>It is the same shape as a topology change at a termination, which already asked for the
    /// auto-solve. What kept the band count out was that the request was spelled as "which END is
    /// this about" and a band-count change has no honest answer to that.</para>
    /// </remarks>
    [Fact]
    public void SwitchingBandCount_MovesTheDesignOntoASolutionThatReaches()
    {
        var (_, _, d) = Open(SingleBandProblem());
        d.WaitForAnalysis();
        d.ApplySolution(d.AllSolutions.First());
        d.WaitForAnalysis();
        Assert.True(d.Status.OnTarget);
        Assert.Equal(1, d.AllSolutions.Count(r => r.IsCurrent));

        d.BandsChoice = "Dual";
        d.WaitForAnalysis();

        output.WriteLine($"dual: {d.AllSolutions.Count} rows, current={d.AllSolutions.Count(r => r.IsCurrent)}, "
                         + $"onTarget={d.Status.OnTarget}, {d.SolutionsSummary}");
        Assert.NotEmpty(d.AllSolutions);
        Assert.Equal(1, d.AllSolutions.Count(r => r.IsCurrent));
        Assert.True(d.HasAppliedSolution);
        Assert.True(d.Status.OnTarget, "the dual-band design is not on a rack that reaches");
        Assert.All(d.AllSolutions.Where(r => r.IsCurrent), r => Assert.Equal(2, r.BandCount));

        // ...and back again, which is the same failure in the other direction.
        d.BandsChoice = "Single";
        d.WaitForAnalysis();
        Assert.Equal(1, d.AllSolutions.Count(r => r.IsCurrent));
        Assert.True(d.Status.OnTarget);

        d.Dispose();
    }

    /// <summary>
    /// The whole switch is still ONE undo entry, auto-solve included — the amend that
    /// <c>CommitSpecChangeWithAutoSolve</c> exists for.
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void SwitchingToDual_IsOneUndoEntry_EvenWithTheAutoSolve()
    {
        var (vm, comp, d) = Open(SingleBandProblem());
        d.WaitForAnalysis();
        d.ApplySolution(d.AllSolutions.First());
        d.WaitForAnalysis();

        string before = comp.Parameters.First(p => p.Name == "Design").Expression;

        d.BandsChoice = "Dual";
        d.WaitForAnalysis();
        Assert.Equal(2, d.BandCount);

        vm.UndoRedo.Undo();
        d.WaitForAnalysis();
        Assert.Equal(before, comp.Parameters.First(p => p.Name == "Design").Expression);
        Assert.Equal(1, d.BandCount);

        d.Dispose();
    }

    // ══ 8 — an undo moves the list's own highlight, and shows it ════════════

    /// <summary>
    /// <b>After an undo or a redo the list selects the card the design is now on and scrolls to it</b>
    /// (owner-reported, 2026-08-28: "if user does undo/redo the previous solution card is not
    /// highlighted. Set the scroll to the selected solution card after an undo/redo").
    /// </summary>
    /// <remarks>
    /// <b>The card's own badge was never wrong</b> — <c>RebadgeSolutions</c> runs on every refresh and
    /// an undo refreshes. What was wrong is that the window keeps a SECOND highlight on the same list,
    /// the ListBox's selection, and it moved only on a click or on a collection change. An undo is
    /// neither: it touches nothing in <c>MatchSpecKey</c>, so the search is not restarted and the
    /// collection is not rebuilt. So the selection stayed on the row the user last clicked while the
    /// green border moved back to the row the design is now on — two highlights, two cards, and the
    /// right one usually scrolled out of view.
    ///
    /// <para>Asserted at the view-model boundary, which is where this repository tests the Designer:
    /// the event fires for a move the user did not make and does NOT fire for a click. The window's
    /// half — <c>SyncSolutionSelection</c> plus <c>ScrollToApplied</c> — is a source scan below,
    /// because a ListBox's scroll offset needs a rendered, virtualized container to mean anything.</para>
    /// </remarks>
    [Fact]
    public void AnUndoOrRedo_RaisesTheAppliedSolutionMoved_ButAClickDoesNot()
    {
        var (vm, _, d) = Open(SingleBandProblem());
        d.WaitForAnalysis();

        int moved = 0;
        d.AppliedSolutionMoved += (_, _) => moved++;

        var a = d.AllSolutions.First();
        d.ApplySolution(a);
        d.WaitForAnalysis();
        var b = d.AllSolutions.Skip(5).First();
        d.ApplySolution(b);
        d.WaitForAnalysis();
        Assert.True(b.IsCurrent);
        Assert.Equal(0, moved);   // the user clicked both; the card is already under the pointer

        vm.UndoRedo.Undo();
        d.WaitForAnalysis();
        Assert.True(a.IsCurrent);
        Assert.False(b.IsCurrent);
        Assert.Equal(1, moved);

        vm.UndoRedo.Redo();
        d.WaitForAnalysis();
        Assert.True(b.IsCurrent);
        Assert.Equal(2, moved);

        d.Dispose();
    }

    /// <summary>The window answers that event by selecting the row AND bringing it into view.</summary>
    [Fact]
    public void TheWindow_SelectsAndScrollsToTheMovedRow()
    {
        string code = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Ui", "Views", "Match", "MatchDesignerWindow.axaml.cs"));

        Assert.Contains("vm.AppliedSolutionMoved += OnAppliedSolutionMoved;", code, StringComparison.Ordinal);
        Assert.Contains("vm.AppliedSolutionMoved -= OnAppliedSolutionMoved;", code, StringComparison.Ordinal);

        int i = code.IndexOf("private void OnAppliedSolutionMoved(", StringComparison.Ordinal);
        Assert.True(i >= 0, "the window has no handler for the move");
        string body = code[i..code.IndexOf("private void ScrollToAppliedOnce()", i, StringComparison.Ordinal)];
        Assert.Contains("SyncSolutionSelection();", body, StringComparison.Ordinal);
        Assert.Contains("ScrollToApplied();", body, StringComparison.Ordinal);
    }

    // ══ 8b — the Specification pane grows for the extra bands ═══════════════

    /// <summary>
    /// <b>The Specification scroller's cap is COMPUTED from the pane, not a literal</b> (owner,
    /// 2026-08-28: selecting Dual or Tri is to expand the Specification group minimally, at the
    /// Solutions group's expense, so every f-row and every note is readable without scrolling).
    /// </summary>
    /// <remarks>
    /// <b>What is asserted is the SHAPE, because this suite draws nothing.</b> The heights that
    /// decided <c>SolutionsFloor</c> were measured headlessly once and are recorded on the constant
    /// itself; what a source scan can hold shut is that they are still being used the way that
    /// measurement assumed — the cap is the pane's own height less the floor, taken again on every
    /// resize, rather than a per-band-count constant that would go stale the first time a note
    /// wrapped.
    ///
    /// <para>The AXAML's own 300 is asserted to be the same number as
    /// <c>SpecificationFloor</c>: it is the value the pane rests at until the first layout pass
    /// reaches the method, and the two drifting apart would show as the pane jumping on open.</para>
    /// </remarks>
    [Fact]
    public void TheSpecificationPane_TakesItsCapFromThePaneHeight_AndIsRetakenOnResize()
    {
        string xaml = Xaml();
        Assert.Contains("Name=\"SpecificationPane\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Name=\"SpecificationScroll\"", xaml, StringComparison.Ordinal);

        string code = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Ui", "Views", "Match", "MatchDesignerWindow.axaml.cs"));

        // Re-taken whenever the pane changes size — a window resize, and the two pane expanders,
        // which change the column's width and therefore how many lines a note wraps to.
        Assert.Contains("specPane.SizeChanged += (_, _) => SyncSpecificationCap();",
                        code, StringComparison.Ordinal);

        int i = code.IndexOf("private void SyncSpecificationCap()", StringComparison.Ordinal);
        Assert.True(i >= 0, "the window has no method that sizes the specification pane");
        string body = code[i..(i + 900)];

        // The cap is arithmetic on the LIVE pane height, so it cannot be a per-band-count table.
        Assert.Contains("pane.Bounds.Height", body, StringComparison.Ordinal);
        Assert.Contains("SolutionsFloor", body, StringComparison.Ordinal);
        Assert.Contains("scroll.MaxHeight = Math.Max(SpecificationFloor, spare);",
                        body, StringComparison.Ordinal);
        Assert.DoesNotContain("BandCount", body, StringComparison.Ordinal);

        // The AXAML's resting cap and the floor in code are one number.
        var floor = Regex.Match(code, @"SpecificationFloor = (\d+);");
        Assert.True(floor.Success, "SpecificationFloor is gone");
        Assert.Contains($"Name=\"SpecificationScroll\" MaxHeight=\"{floor.Groups[1].Value}\"",
                        xaml, StringComparison.Ordinal);
    }

    // ══ 9 — no design-note sections in anything the user reads ══════════════

    /// <summary>
    /// <b>Nothing the Designer shows quotes a design-note section</b> (owner, 2026-08-28: the user
    /// does not read those).
    /// </summary>
    /// <remarks>
    /// The refusals are the easy half to forget: MN-1 writes them in <c>src/Core/Match</c> and the
    /// status strip renders them verbatim, so a section reference added there reaches the screen
    /// without passing through any file in <c>src/Ui</c>. Both sides are scanned.
    /// </remarks>
    [Fact]
    public void NoUserVisibleStringInTheDesigner_QuotesADesignNoteSection()
    {
        foreach (string relative in (string[])
                 [
                     Path.Combine("src", "Ui", "Views", "Match", "MatchDesignerWindow.axaml"),
                     Path.Combine("src", "Ui", "Match", "MatchDesignerViewModel.cs"),
                     Path.Combine("src", "Ui", "Match", "MatchSolutionFilterViewModel.cs"),
                     Path.Combine("src", "Ui", "Match", "MatchSolutionRowViewModel.cs"),
                     Path.Combine("src", "Core", "Match", "MatchSynthesis.cs"),
                     Path.Combine("src", "Core", "Match", "MatchFormSynthesis.cs"),
                     Path.Combine("src", "Core", "Match", "MatchMultibandSynthesis.cs"),
                     Path.Combine("src", "Core", "Match", "MatchBands.cs"),
                 ])
        {
            string raw = File.ReadAllText(Path.Combine(RepoRoot(), relative));

            // Developer commentary keeps its references — this is about what reaches the screen.
            raw = Regex.Replace(raw, @"<!--.*?-->", "", RegexOptions.Singleline);
            raw = Regex.Replace(raw, @"^[ \t]*///[^\n]*$", "", RegexOptions.Multiline);
            raw = Regex.Replace(raw, @"^[ \t]*//[^\n]*$", "", RegexOptions.Multiline);

            foreach (System.Text.RegularExpressions.Match m in Regex.Matches(raw, "\"[^\"\n]*§[^\"\n]*\""))
                Assert.Fail($"{relative} shows a section reference to the user: {m.Value}");
        }
    }
}
