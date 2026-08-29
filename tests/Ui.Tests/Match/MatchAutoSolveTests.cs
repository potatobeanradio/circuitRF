// ================================================================
//  MatchAutoSolveTests.cs  —  a termination edit that breaks the match moves the design onto a
//  solution that reaches the target, rather than leaving an unmatched network on screen.
//
//  Owner-reported: presenting a network whose far end does not reach the value the user just typed
//  is confusing, and the Designer should not do it when the solution search has an answer.
//
//  Same discipline as rounds 1-7: view-model and source tests, never pixels.
// ================================================================

using System;
using System.Diagnostics;
using System.Linq;
using CircuitRF.Core.Matching;
using CircuitRF.Ui.Matching;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Match;

public sealed class MatchAutoSolveTests(ITestOutputHelper output)
{
    // ── Fixture ───────────────────────────────────────────────────────────────

    private static (SchematicViewModel Vm, EditableComponent Comp, MatchDesignerViewModel Designer)
        Open(MatchDesign design)
    {
        var model = new SchematicEditModel();
        var comp = new EditableComponent { InstanceName = "MN1", Symbol = SymbolKind.Match, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Match, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        comp.Parameters.First(p => p.Name == "Design").Expression = MatchEmbedding.Encode(design);
        model.Components.Add(comp);

        var vm = new SchematicViewModel(model);
        var designer = new MatchDesignerViewModel();
        designer.SetTarget(vm, comp);
        designer.WaitForAnalysis();
        return (vm, comp, designer);
    }

    /// <summary>The owner's own reported problem: 5 Ω ∥ 1 pF into 50 Ω over 1.8-2.2 GHz.</summary>
    private static MatchDesign Problem(int order = 4) => new()
    {
        F1 = 1.8e9, F2 = 2.2e9, Order = order, Response = ResponseShape.ChebyshevFano,
        Term1 = new Termination(5.0, ReactanceKind.C, TerminationTopology.Parallel, 1e-12),
        Term2 = Termination.Resistive(50.0),
    };

    /// <summary>Puts the design on its first offered solution, matched, with Link off.</summary>
    /// <remarks>
    /// <b>Link OFF is the interesting starting state, and it is not the default.</b> With Link on,
    /// <c>RelinkAfterSpecChange</c> absorbs most termination edits by re-driving one unlocked
    /// transform, and the auto-solve correctly never fires — that case has its own test below. What
    /// this fixture sets up is the half the linkage cannot reach.
    /// </remarks>
    private static MatchDesignerViewModel Matched(MatchDesign design, bool link = false)
    {
        var (_, _, designer) = Open(design);
        Assert.NotEmpty(designer.Solutions);
        designer.ApplySolution(designer.Solutions[0]);
        designer.WaitForAnalysis();
        Assert.True(designer.Rebuild!.OnTarget, "the fixture has to start matched");

        designer.LinkTransforms = link;
        designer.WaitForAnalysis();
        return designer;
    }

    private static string Shape(MatchDesign d) =>
        string.Join(",", d.Transforms.Select(t => $"{t.ElementA}/{t.ElementB}/{t.Form}"));

    // ── The ask ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A resistance edit the rack cannot absorb leaves the design MATCHED, on a solution that reaches
    /// the new target — not on the old transforms with a red termination beside them.
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void ChangingAResistanceTheRackCannotAbsorb_MovesOntoASolutionThatReachesTheTarget()
    {
        var designer = Matched(Problem());
        var before = designer.Design.Transforms.Select(t => t.N).ToList();

        designer.Term2.Resistance = 25.0;
        designer.WaitForAnalysis();

        output.WriteLine($"Π N² {designer.Rebuild!.Achieved:0.####} of {designer.Rebuild.Required:0.####}");

        Assert.True(designer.Rebuild.OnTarget,
            $"left off target: Π N² {designer.Rebuild.Achieved:0.####} of {designer.Rebuild.Required:0.####}");
        Assert.Equal(25.0, designer.Design.Term2.R);
        Assert.NotEqual(before, designer.Design.Transforms.Select(t => t.N).ToList());

        // Neither end is flagged, which is the whole point: the window is not presenting a mismatch.
        Assert.False(designer.Term1.IsFlagged);
        Assert.False(designer.Term2.IsFlagged);

        designer.Dispose();
    }

    /// <summary>A reactance edit is the same edit, and gets the same treatment.</summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void ChangingAReactance_AlsoMovesOntoASolutionThatReachesTheTarget()
    {
        var designer = Matched(Problem());

        designer.Term1.Reactance = 3e-12;              // 1 pF → 3 pF
        designer.WaitForAnalysis();

        Assert.Equal(3e-12, designer.Design.Term1.Value);
        Assert.True(designer.Rebuild!.OnTarget,
            $"left off target: Π N² {designer.Rebuild.Achieved:0.####} of {designer.Rebuild.Required:0.####}");
        designer.Dispose();
    }

    /// <summary>
    /// Parallel ↔ series is the other edit named in the ask, and it is the harder one: the ladder is
    /// re-synthesised, so the stored records may not even name elements that still exist.
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void ChangingTheTopology_AlsoMovesOntoASolutionThatReachesTheTarget()
    {
        var designer = Matched(Problem());

        designer.Term1.Topology = TerminationTopology.Series;
        designer.WaitForAnalysis();

        output.WriteLine($"transforms: {Shape(designer.Design)}");

        Assert.Equal(TerminationTopology.Series, designer.Design.Term1.Topology);
        Assert.True(designer.Rebuild!.OnTarget,
            $"left off target: Π N² {designer.Rebuild.Achieved:0.####} of {designer.Rebuild.Required:0.####}");
        designer.Dispose();
    }

    /// <summary>
    /// <b>The rack in place is preferred over the best-ranked one.</b> When the search still offers
    /// the same transforms in the same places, only the N's move — the user's own choice of network
    /// stands, and "the target moved" is answered by re-driving it rather than by rebuilding it.
    /// </summary>
    [Fact]
    public void TheSolutionChosen_IsTheRackAlreadyInPlaceWhenTheSearchStillOffersIt()
    {
        var (_, _, designer) = Open(Problem());

        // Deliberately NOT the first row. MN-1 ranks fewest transforms first, so row 0 is a
        // one-transform rack; picking a two-transform one is what makes "nearest" and "best-ranked"
        // different answers, and therefore what makes this test able to fail.
        var chosen = designer.Solutions.First(r => r.Solution.Transforms.Count == 2
                                                   && !r.Solution.ImplausibleValues);
        Assert.NotSame(designer.Solutions[0], chosen);
        designer.ApplySolution(chosen);
        designer.WaitForAnalysis();
        designer.LinkTransforms = false;
        designer.WaitForAnalysis();

        string shapeBefore = Shape(designer.Design);
        var nBefore = designer.Design.Transforms.Select(t => t.N).ToList();
        output.WriteLine($"before: {shapeBefore}");

        designer.Term2.Resistance = 25.0;
        designer.WaitForAnalysis();

        output.WriteLine($"after:  {Shape(designer.Design)}");
        output.WriteLine($"rows:   {string.Join(" | ", designer.Solutions.Select(Shape2))}");

        Assert.True(designer.Rebuild!.OnTarget);
        Assert.Equal(shapeBefore, Shape(designer.Design));
        Assert.NotEqual(nBefore, designer.Design.Transforms.Select(t => t.N).ToList());

        // The best-ranked answer was a different, smaller rack — so the equality above is the
        // nearest-first rule and not a coincidence of ordering.
        Assert.NotEqual(shapeBefore, Shape2(designer.Solutions[0]));

        designer.Dispose();

        static string Shape2(MatchSolutionRowViewModel r) =>
            string.Join(",", r.Solution.Transforms.Select(t => $"{t.ElementA}/{t.ElementB}/{t.Form}"));
    }

    /// <summary>
    /// An edit the LINKAGE absorbs is not a case for the solution search, and nothing is restructured
    /// out from under the user.
    /// </summary>
    [Fact]
    public void AnEditTheLinkageAbsorbs_LeavesTheRackAloneAndSaysNothing()
    {
        var designer = Matched(Problem(), link: true);
        string shapeBefore = Shape(designer.Design);
        int countBefore = designer.Design.Transforms.Count;
        int applied = designer.Design.AppliedSolutions.Count;

        designer.Term2.Resistance = 25.0;
        designer.WaitForAnalysis();

        Assert.True(designer.Rebuild!.OnTarget);
        Assert.Equal(shapeBefore, Shape(designer.Design));
        Assert.Equal(countBefore, designer.Design.Transforms.Count);
        Assert.Equal(applied, designer.Design.AppliedSolutions.Count);   // nothing was applied
        designer.Dispose();
    }

    /// <summary>
    /// Every transform locked is one of the cases <c>RelinkAfterSpecChange</c> cannot reach — and the
    /// design is still moved onto a reaching solution.
    /// </summary>
    /// <remarks>
    /// <b>The locks go with it, and that is the intended trade.</b> A lock pins one N against the
    /// LINKAGE's redistribution; it is not a veto over the whole rack, and honouring it here would
    /// leave exactly the unmatched network this behaviour exists to stop presenting. Applying a
    /// solution from the panel by hand has always replaced the records the same way.
    /// </remarks>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void EveryTransformLocked_IsStillMovedOntoAReachingSolution()
    {
        var designer = Matched(Problem(), link: true);
        designer.Design.Transforms = [.. designer.Design.Transforms.Select(t => t with { Locked = true })];
        designer.Refresh(specChanged: true);
        designer.WaitForAnalysis();

        designer.Term2.Resistance = 30.0;
        designer.WaitForAnalysis();

        output.WriteLine($"transforms: {Shape(designer.Design)}");
        Assert.True(designer.Rebuild!.OnTarget);
        Assert.All(designer.Design.Transforms, t => Assert.False(t.Locked));
        designer.Dispose();
    }

    /// <summary>
    /// The auto-solve is a TERMINATION edit's behaviour and nothing else's. An order change that
    /// leaves the rack short is still reported rather than silently rebuilt — the user changed the
    /// network, not the thing it has to match, and the solutions panel is right there.
    /// </summary>
    [Fact]
    public void ANonTerminationEdit_IsNotAutoSolved()
    {
        var designer = Matched(Problem());
        string shapeBefore = Shape(designer.Design);
        var nBefore = designer.Design.Transforms.Select(t => t.N).ToList();

        designer.Order = designer.OrderOptions.First(o => o != designer.Order);
        designer.WaitForAnalysis();

        Assert.Equal(shapeBefore, Shape(designer.Design));
        Assert.Equal(nBefore, designer.Design.Transforms.Select(t => t.N).ToList());
        Assert.False(designer.Rebuild!.OnTarget, "the fixture has to leave the rack short to mean anything");
        designer.Dispose();
    }

    /// <summary>
    /// A design whose OWN family has nothing to offer is moved onto one that does, rather than left
    /// unmatched (owner-reported, 2026-08-28: adding reactance left the termination target unmet and
    /// the design on its old rack, even though a solution existed).
    /// </summary>
    /// <remarks>
    /// <b>This test asserted the opposite until 2026-08-28, and the reasoning it carried was sound —
    /// it simply lost to the thing it was trading against.</b> It said the candidates were the
    /// design's own order and family and only those, because answering "termination 2 is 75 Ω now" by
    /// silently changing the network's ORDER would be changing a design decision the user made under
    /// cover of one they did not. What that produced in practice is worse: a Designer showing an
    /// unmatched network with a red termination, while its own list holds rows that match, leaving
    /// the user to notice the mismatch, work out that the fix is a different family, and find it.
    /// The candidate search now widens — own order and family first, then own order in any family,
    /// then the nearest order — so what the user chose is still preferred and is only given up when
    /// keeping it means presenting a mismatch.
    ///
    /// <para><b>The fixture is a family that REFUSES</b>: Bessel cannot absorb this pair, so its
    /// cells produce a refusal and no rows at any order. It is also the fixture that found the two
    /// bugs underneath the report — see <c>MatchRound9Tests</c> for both.</para>
    /// </remarks>
    [Fact]
    public void WhenTheDesignsOwnFamilyHasNothingToOffer_AnotherFamilyIsAppliedRatherThanNone()
    {
        var design = Problem();
        design.Response = ResponseShape.Bessel;
        var (_, _, designer) = Open(design);
        designer.LinkTransforms = false;
        designer.WaitForAnalysis();
        Assert.DoesNotContain(designer.AllSolutions, r => r.Response == ResponseShape.Bessel);

        designer.Term2.Resistance = 75.0;
        designer.WaitForAnalysis();

        output.WriteLine($"{designer.Design.Response} order {designer.Design.Order}");
        Assert.NotEqual(ResponseShape.Bessel, designer.Design.Response);
        Assert.True(designer.Rebuild!.OnTarget && designer.Rebuild.Refusal is null,
                    "the Designer is still presenting a network that is not a match");
        Assert.False(designer.Term1.IsFlagged);
        Assert.False(designer.Term2.IsFlagged);
        Assert.NotEmpty(designer.Design.AppliedSolutions);
        output.WriteLine(designer.SolutionsSummary);
        designer.Dispose();
    }

    /// <summary>
    /// <b>An order that IS short is a different case, and the list now answers it.</b> Order 3 on this
    /// problem was the owner's original "cannot match 50 Ω to 5 Ω ∥ 1 pF" — it reaches Π N² 1.016
    /// against a required 10 with no Q-adjust — and the cross-product search finds the Q-adjusted
    /// network that does complete there. So the auto-solve has something to move onto after all.
    /// </summary>
    [Fact]
    public void AnOrderThatOnlyCompletesWithAQAdjust_IsStillSomethingToMoveOnto()
    {
        var (_, _, designer) = Open(Problem(order: 3));
        designer.LinkTransforms = false;
        designer.WaitForAnalysis();

        // Chebyshev-Fano is the family the owner's report was about, and the one the old refusal was
        // measured on: at order 3 it reaches Π N² 1.016 against a required 10, so every buildable row
        // it produces there carries a Q-adjust. Other families reach it at order 3 unaided, which is
        // itself part of what the list is now able to say.
        var fano = designer.AllSolutions
            .Where(r => r.Order == 3 && r.Response == ResponseShape.ChebyshevFano && !r.HasNegativeComponents)
            .ToList();
        Assert.NotEmpty(fano);
        Assert.All(fano, r => Assert.True(r.Solution.QAdjust > 0,
            "Chebyshev-Fano completes at order 3 here only with a Q-adjust; that is the fixture"));

        var atThree = designer.AllSolutions.Where(r => r.Order == 3 && !r.HasNegativeComponents).ToList();
        output.WriteLine(string.Join(", ", atThree.Select(r => $"{r.TitleText} {r.QAdjustText} {r.ReturnLossText}")));

        designer.Dispose();
    }

    /// <summary>
    /// The auto-applied solution is COMMITTED — the component carries it, so the schematic, a save
    /// and an undo all see the same design the window is showing.
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void TheAutoAppliedSolution_IsWrittenBackToTheComponent()
    {
        var (_, comp, designer) = Open(Problem());
        designer.ApplySolution(designer.Solutions[0]);
        designer.WaitForAnalysis();
        designer.LinkTransforms = false;
        designer.WaitForAnalysis();

        designer.Term2.Resistance = 25.0;
        designer.WaitForAnalysis();

        Assert.True(MatchEmbedding.TryDecode(
            comp.Parameters.First(p => p.Name == "Design").Expression, out var stored));
        Assert.NotNull(stored);
        Assert.Equal(25.0, stored!.Term2.R);
        Assert.Equal(Shape(designer.Design), Shape(stored));
        Assert.Equal(
            designer.Design.Transforms.Select(t => t.N),
            stored.Transforms.Select(t => t.N));
        Assert.True(MatchRebuild.Rebuild(stored).OnTarget, "the COMMITTED design has to be the matched one");
        designer.Dispose();
    }

    /// <summary>
    /// It fires once per edit, not once per analysis pass: the pass the apply itself queues must not
    /// apply again. A loop here would be invisible except as a window that never stops working.
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void ItFiresOncePerEdit_NotOncePerPass()
    {
        var designer = Matched(Problem());
        designer.Term2.Resistance = 25.0;
        designer.WaitForAnalysis();

        var settled = designer.Design.Transforms.Select(t => (t.ElementA, t.ElementB, t.N)).ToList();
        int applied = designer.Design.AppliedSolutions.Count;

        for (int i = 0; i < 3; i++)
        {
            designer.Refresh(specChanged: true);
            designer.WaitForAnalysis();
        }

        Assert.Equal(settled, designer.Design.Transforms.Select(t => (t.ElementA, t.ElementB, t.N)).ToList());
        Assert.Equal(applied, designer.Design.AppliedSolutions.Count);
        Assert.False(designer.IsAnalysing);
        designer.Dispose();
    }

    /// <summary>
    /// <b>The search stays off the UI thread.</b> The auto-solve reads the pass that was already
    /// running; running a second search inside the edit would undo what
    /// <c>MatchDesignerSpecEditCostTests</c> holds shut, and a termination edit is typed.
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void ATerminationEdit_StillDoesNotBlockOnTheSolutionSearch()
    {
        var designer = Matched(Problem());

        // Warm: the first pass through any of this JITs the synthesis, the elaborator and the engine.
        foreach (double r in new[] { 20.0, 30.0, 40.0 }) { designer.Term2.Resistance = r; designer.WaitForAnalysis(); }

        double worst = 0;
        foreach (double r in new[] { 22.0, 33.0, 44.0, 55.0 })
        {
            var sw = Stopwatch.StartNew();
            designer.Term2.Resistance = r;                 // the keystroke
            sw.Stop();
            worst = Math.Max(worst, sw.Elapsed.TotalMilliseconds);
            designer.WaitForAnalysis();
            output.WriteLine($"R -> {r,5:F0} Ω: UI thread {sw.Elapsed.TotalMilliseconds,7:F2} ms");
        }

        Assert.True(worst < 150.0, $"a termination edit blocked the UI thread for {worst:F1} ms");
        designer.Dispose();
    }

}
