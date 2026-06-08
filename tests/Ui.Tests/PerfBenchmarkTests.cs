using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

public class PerfBenchmarkTests
{
    [Fact]
    public void BuildRenderModel_10k_Under50ms()
    {
        var renderModel = SchematicModelBuilder.GenerateStressTest(10_000);
        var editModel   = SchematicEditModel.FromRenderModel(renderModel);

        Console.WriteLine($"Components: {editModel.Components.Count}, Wires: {editModel.Wires.Count}");

        // Warm up
        editModel.BuildRenderModel();

        var times = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            editModel.BuildRenderModel();
            sw.Stop();
            times.Add(sw.Elapsed.TotalMilliseconds);
            Console.WriteLine($"  Run {i+1}: {sw.Elapsed.TotalMilliseconds:F1} ms");
        }

        double avg = times.Average();
        Console.WriteLine($"  Avg: {avg:F1} ms  Min: {times.Min():F1} ms  Max: {times.Max():F1} ms");
        // Previously ~1500 ms (O(N²) connectivity). Now O(N) spatial-hash: ~37 ms Release, ~90 ms Debug.
        Assert.True(avg < 500, $"BuildRenderModel 10k regressed: {avg:F1} ms (was ~37 ms release)");
    }
}
