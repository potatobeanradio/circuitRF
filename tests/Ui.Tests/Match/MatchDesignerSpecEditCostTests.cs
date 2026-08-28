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

/// <summary>
/// The owner's own report, 2026-08-20: <i>"slowest when I change network order or filter response
/// type — the step that involves solving the low pass prototype."</i>
/// </summary>
/// <remarks>
/// <b>Both edits are the same edit.</b> They are the two that reach <c>Refresh(specChanged: true)</c>,
/// and that is where the lowpass-prototype search runs — four times over, once per response family,
/// for enablement alone. Measured on the design doc's order-4 interstage problem before this work: a
/// specification edit cost <b>1,161 ms</b> with Chebyshev selected and over two seconds with
/// Butterworth, all of it blocking the UI thread.
///
/// <para>What this file holds shut is the <b>blocking half</b>: whatever the search costs, an edit
/// must return to the message loop promptly. The bound is deliberately loose against the numbers
/// actually measured (single-digit to ~20 ms) so it does not flake on a loaded machine, and it is
/// still an order of magnitude under the behaviour it exists to prevent coming back.</para>
///
/// <para>Untagged, like <c>MatchDesignerDragCostTests</c> beside it: the whole file runs well under
/// the ~5 s <c>Category=Benchmark</c> threshold and belongs in the default gate, where a regression
/// in the edit path is actually noticed.</para>
/// </remarks>
public class MatchDesignerSpecEditCostTests(ITestOutputHelper output)
{
    /// <summary>The design doc's interstage problem — a hard one, and the one MN-3 was built on.</summary>
    private static MatchDesign Golden() => new()
    {
        F1 = 3.3e9, F2 = 5.0e9, Order = 4, Response = ResponseShape.ChebyshevFano,
        Term1 = new Termination(200.0, ReactanceKind.C, TerminationTopology.Parallel, 0.125e-12),
        Term2 = new Termination(1.25, ReactanceKind.C, TerminationTopology.Series, 10e-12),
    };

    private static MatchDesignerViewModel Open(MatchDesign design)
    {
        var model = new SchematicEditModel();
        var comp = new EditableComponent { InstanceName = "MN1", Symbol = SymbolKind.Match };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Match, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        comp.Parameters.First(p => p.Name == "Design").Expression = MatchEmbedding.Encode(design);
        model.Components.Add(comp);

        var designer = new MatchDesignerViewModel();
        designer.SetTarget(new SchematicViewModel(model), comp);
        designer.WaitForAnalysis();
        return designer;
    }

    /// <summary>An order change hands the message loop back promptly, whatever the search then costs.</summary>
    [Fact]
    public void ChangingTheOrder_DoesNotBlockOnTheLowpassPrototypeSearch()
    {
        var designer = Open(Golden());
        var orders = designer.OrderOptions.ToList();
        Assert.True(orders.Count > 1, "the picker has to offer more than one order for this to mean anything");

        // Warm: the first pass through any of this JITs the synthesis, the elaborator and the engine.
        foreach (int n in orders) { designer.Order = n; designer.WaitForAnalysis(); }

        double worst = 0;
        foreach (int n in orders)
        {
            designer.Order = orders[0] == n ? orders[^1] : orders[0];
            designer.WaitForAnalysis();

            var sw = Stopwatch.StartNew();
            designer.Order = n;                       // the click
            sw.Stop();
            double blocking = sw.Elapsed.TotalMilliseconds;

            var background = Stopwatch.StartNew();
            designer.WaitForAnalysis();
            background.Stop();

            output.WriteLine($"order -> {n}: UI thread {blocking,7:F2} ms, " +
                             $"analysis {background.Elapsed.TotalMilliseconds,8:F2} ms");
            worst = Math.Max(worst, blocking);

            Assert.Equal(n, designer.Order);
            Assert.Equal(4, designer.ResponseOptions.Count);
        }

        Assert.True(worst < 150.0, $"an order change blocked the UI thread for {worst:F1} ms");
        designer.Dispose();
    }

    /// <summary>The same for the response family, which is the other edit that reaches the search.</summary>
    [Fact]
    public void ChangingTheResponseFamily_DoesNotBlockOnTheLowpassPrototypeSearch()
    {
        var designer = Open(Golden());
        foreach (var shape in Enum.GetValues<ResponseShape>())
        {
            designer.Response = shape;
            designer.WaitForAnalysis();
        }

        double worst = 0;
        foreach (var shape in Enum.GetValues<ResponseShape>())
        {
            designer.Response = ResponseShape.ChebyshevFano;
            designer.WaitForAnalysis();

            var sw = Stopwatch.StartNew();
            designer.Response = shape;
            sw.Stop();
            double blocking = sw.Elapsed.TotalMilliseconds;
            designer.WaitForAnalysis();

            output.WriteLine($"response -> {shape,-18} UI thread {blocking,7:F2} ms");
            worst = Math.Max(worst, blocking);
            Assert.Equal(shape, designer.Response);
        }

        Assert.True(worst < 150.0, $"a response change blocked the UI thread for {worst:F1} ms");
        designer.Dispose();
    }

    /// <summary>
    /// <b>The answer on screen is the LAST edit's, not whichever pass happened to finish last.</b>
    /// </summary>
    /// <remarks>
    /// This is the risk the move to a worker introduces and the one thing about it that could be
    /// silently wrong: a run of edits starts a run of overlapping passes, and a slow early one
    /// completing after a fast later one would leave the panel describing a design the user has
    /// already moved off — with nothing anywhere saying so. Each pass carries a generation and only
    /// the newest is allowed to write.
    /// </remarks>
    [Fact]
    public void ASequenceOfEdits_LeavesTheLastEditsAnswerOnScreen()
    {
        var designer = Open(Golden());
        var orders = designer.OrderOptions.ToList();

        // No waiting between them: this is a user working the spinner, and every intermediate pass is
        // dead on arrival.
        foreach (int n in orders) designer.Order = n;
        designer.Response = ResponseShape.Butterworth;
        designer.Order = orders[0];
        designer.WaitForAnalysis();

        Assert.Equal(orders[0], designer.Order);
        Assert.Equal(ResponseShape.Butterworth, designer.Response);
        Assert.False(designer.IsAnalysing);

        // The verdicts describe the design as it now stands: re-running the same probes by hand has
        // to agree with what the panel is showing.
        foreach (var option in designer.ResponseOptions)
        {
            var probe = designer.Design.Clone();
            probe.Response = option.Shape;
            Assert.Equal(MatchSynthesis.Synthesize(probe).Ok, option.IsEnabled);
        }

        // And so does the solutions list — for the design's OWN combination, which is the slice of it
        // a single MatchSolutionSearch.Search answers. The list spans every order and family since
        // 2026-08-28, so the whole of it is not what one search produces; what has to agree is that
        // the rows carrying this order and this family are exactly that search's own result, in its
        // own order. The probe clears QAdjust and allows negatives for the reason
        // SearchEveryCombination gives: both are filters over the answer now, not inputs to it.
        var cell = designer.Design.Clone();
        cell.QAdjust = 0.0;
        cell.AllowNegativeComponents = true;
        var expected = MatchSolutionSearch.Search(cell, includeQAdjust: true, designer.Settings.QMin);

        var here = designer.AllSolutions
            .Where(r => r.Order == designer.Design.Order && r.Response == designer.Design.Response)
            .ToList();
        Assert.Equal(expected.Solutions.Count, here.Count);
        Assert.Equal(
            expected.Solutions.Select(s => s.Fingerprint),
            here.Select(s => s.Solution.Fingerprint));

        designer.Dispose();
    }

    /// <summary>Disposing while a pass is in flight cancels it and writes nothing afterwards.</summary>
    [Fact]
    public void DisposingMidAnalysis_IsSafe()
    {
        var designer = Open(Golden());
        designer.Response = ResponseShape.Bessel;   // the most expensive family, deliberately
        designer.Dispose();
        designer.WaitForAnalysis();                 // must return rather than throw or hang
    }
}
