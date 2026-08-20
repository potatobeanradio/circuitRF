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
/// Brief §8.3's measurement: <b>one slider step, ladder rebuild through response update</b>, on the
/// design doc's own order-4 interstage problem with two transforms applied.
///
/// <para>Untagged, deliberately — the whole file runs in well under the ~5 s
/// <c>Category=Benchmark</c> threshold, so it belongs in the default gate where a regression in the
/// drag path is actually noticed. It follows <c>HarmonicaDragCostTests</c>'s shape (a real gesture,
/// best-of-N minimum, the numbers printed) without its cost.</para>
/// </summary>
public class MatchDesignerDragCostTests(ITestOutputHelper output)
{
    private static MatchDesign Golden() => new()
    {
        F1 = 3.3e9, F2 = 5.0e9, Order = 4, Response = ResponseShape.ChebyshevFano,
        Term1 = new Termination(200.0, ReactanceKind.C, TerminationTopology.Parallel, 0.125e-12),
        Term2 = new Termination(1.25, ReactanceKind.C, TerminationTopology.Series, 10e-12),
    };

    private static MatchDesignerViewModel Open(out SchematicViewModel vm)
    {
        var model = new SchematicEditModel();
        var comp = new EditableComponent { InstanceName = "MN1", Symbol = SymbolKind.Match };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Match, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        comp.Parameters.First(p => p.Name == "Design").Expression = MatchEmbedding.Encode(Golden());
        model.Components.Add(comp);

        vm = new SchematicViewModel(model);
        var designer = new MatchDesignerViewModel();
        designer.SetTarget(vm, comp);
        designer.LinkTransforms = true;
        designer.AddTransform(designer.AvailablePairs().First(p => p.Display == "L1 / L2"));
        designer.AddTransform(designer.AvailablePairs().First(p => p.Display == "L3 / L4"));
        return designer;
    }

    [Fact]
    public void OneSliderStep_ItsCost_WithAndWithoutTheResponseSweep()
    {
        var designer = Open(out _);
        Assert.Equal(2, designer.Transforms.Count);
        Assert.True(designer.Rebuild!.OnTarget);

        // Warm: the first call through any of this JITs the synthesis, the elaborator and the engine.
        designer.BeginTransformDrag();
        designer.SetTransformN(0, designer.Transforms[0].N * 0.999);
        designer.EndTransformDrag();

        const int steps = 40;

        // 1. The drag path as it ships: linkage, rebuild, ladder, grid, status. No plots.
        designer.BeginTransformDrag();
        double n0 = designer.Transforms[0].N;
        var sw = Stopwatch.StartNew();
        for (int i = 1; i <= steps; i++)
            designer.SetTransformN(0, n0 * (1.0 - 0.002 * i));
        sw.Stop();
        double perStepLive = sw.Elapsed.TotalMilliseconds / steps;
        designer.EndTransformDrag();

        // 2. The same step with the response sweep included — what a move would cost if the plot were
        //    NOT held for the gesture. Outside a drag, SetTransformN refreshes the plots itself, so
        //    this is one step and one sweep, not one step and two.
        double points = designer.PlotPoints;
        n0 = designer.Transforms[0].N;
        sw.Restart();
        for (int i = 1; i <= steps; i++)
            designer.SetTransformN(0, n0 * (1.0 + 0.002 * i));
        sw.Stop();
        double perStepWithPlots = sw.Elapsed.TotalMilliseconds / steps;

        // 3. The release: one plot refresh.
        sw.Restart();
        designer.UpdatePlots();
        sw.Stop();
        double release = sw.Elapsed.TotalMilliseconds;

        output.WriteLine($"order 4, 2 transforms, {designer.Elements.Count} elements, " +
                         $"{points:F0} plot points");
        output.WriteLine($"  live drag step  (ladder + values + status)   {perStepLive,8:F2} ms");
        output.WriteLine($"  step + response (SParameterEngine sweep)     {perStepWithPlots,8:F2} ms");
        output.WriteLine($"  release        (one response sweep)          {release,8:F2} ms");
        output.WriteLine($"  the sweep is {perStepWithPlots / Math.Max(perStepLive, 1e-9):F1}x the live step, " +
                         "which is why the PLOTS are held for the gesture and nothing else is");

        Assert.True(designer.Rebuild!.OnTarget);
        // A live step has to leave room for a frame at 60 fps with the render still to do; if this
        // ever fails the ladder itself has become the bottleneck, which is the thing brief §5 says
        // must never be throttled.
        Assert.True(perStepLive < 16.0, $"a live drag step cost {perStepLive:F2} ms");

        designer.Dispose();
    }
}
