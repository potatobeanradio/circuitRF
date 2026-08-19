using System.Diagnostics;
using CircuitRF.WBond.Mom;
using Xunit.Abstractions;

namespace CircuitRF.WBond.Tests.Mom;

/// <summary>
/// What kernel W1's one-time work actually costs, stated as <b>ratios</b> rather than wall-clock
/// seconds — the same discipline <see cref="CapacitanceCostTests"/> adopted, and for the same reason:
/// a routine <c>dotnet test</c> runs Debug on whatever machine it is on, so an absolute threshold there
/// either flakes or means nothing.
///
/// <h3>Measured, Release, Apple Silicon, 40 wires × 24 segments (N_s = 1040, N_n = 1080, N_r = 1020)</h3>
/// <list type="bullet">
/// <item><b>L fill 22.6 ms; the wire-basis fill over the SAME 1,040 filaments 45.7 ms against this
///   kernel's 47.2 ms — 1.03 ×.</b> §0.3 item 1 holds: the segment fill is not more expensive, it keeps
///   more of its output.</item>
/// <item><b>P fill 8.2 ms = 0.36 × the L fill</b> (Release; ~1.2 × in Debug, where the interpreter tax
///   lands differently on Grover's transcendentals than on a reciprocal square root — which is why the
///   gate below is <c>&lt; 2 ×</c> and not <c>&lt; 1 ×</c>).</item>
/// <item><b>The assembly is 1.65 s — 54 × the two fills together</b>, split
///   cholesky(P) 129 ms / step 1's 1,020 solves <b>726 ms</b> / cholesky(G) 101 ms / step 3's 1,040
///   solves <b>656 ms</b> / step 4 <b>1.0 ms</b>.</item>
/// <item><b>200 wires (N_s = 5,200):</b> L 0.36 s, P 0.10 s, <b>assembly 313 s</b>, 1,305 MB working
///   set against a 996 MB prediction.</item>
/// </list>
///
/// <para><b>The brief's §7 has step 4 as "the largest single one-time cost … roughly two dense
/// factorisations' worth". It is the smallest step in the assembly by nearly three orders of
/// magnitude</b> — <c>Ã</c> has two non-zeros per row, so <c>K̃ = Ã Y</c> is O(N_s²) rather than the
/// O(N_s² N_r) a dense GEMM would cost. The real cost is the two batches of triangular solves, and
/// both are embarrassingly parallel over their right-hand sides. See
/// <c>src/WBond/Mom/RESOLVED.md</c>.</para>
///
/// <para>Tagged <c>Category=Benchmark</c>: at N_s = 1040 the assembly alone is ~5.8 s in Debug, which is
/// over the ~5 s threshold, and every ratio here is wall-clock-sensitive. Run with
/// <c>dotnet test --settings circuitrf.benchmark.runsettings</c>.</para>
/// </summary>
public sealed class MomCostTests(ITestOutputHelper output)
{
    private static double BestMs(Action action, int reps = 3)
    {
        action();   // warm the JIT and the thread pool before anything is timed
        double best = double.MaxValue;
        for (int i = 0; i < reps; i++)
        {
            var sw = Stopwatch.StartNew();
            action();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }
        return best;
    }

    private static WireMomMesh FortyWireMesh() =>
        WireMomMesh.Build(TestDesigns.PowerAmplifier(wireCount: 40, arrayCount: 10, pointsPerWire: 7));

    /// <summary>
    /// <b>§0.3 item 1 — the segment-basis fill costs what the wire-basis fill already costs.</b>
    ///
    /// <para>The comparison is against a design whose polyline vertices <i>are</i> the mesh's segment
    /// endpoints, so both fills walk the identical filament set. A wire-basis fill over the design's own
    /// 7-point wires would be a different measurement entirely (240 filaments against 1,040) and would
    /// prove nothing about the claim.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void TheSegmentFillCostsWhatTheWireBasisFillCostsOverTheSameFilaments()
    {
        var mesh = FortyWireMesh();
        var fine = WireMesh.Build(SubdividedDesign(mesh));

        Assert.Equal(mesh.SegmentCount, fine.FilamentCount);

        double segment = BestMs(() => SegmentInductance.Fill(mesh, parallel: true));
        double wire = BestMs(() => InductanceMatrix.Fill(fine, parallel: true));

        output.WriteLine($"N_s = {mesh.SegmentCount}: segment fill {segment:F1} ms, wire-basis fill over the same filaments {wire:F1} ms ({segment / wire:F2} x)");

        Assert.InRange(segment / wire, 0.4, 2.5);
    }

    /// <summary>
    /// <b>§5.2 — the charge fill is a fraction of the inductance fill, not 4 × it</b>, and the
    /// frequency-independent assembly is what actually costs.
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void TheChargeFillIsCheaperThanTheInductanceFill_AndTheAssemblyDominatesBoth()
    {
        var mesh = FortyWireMesh();

        double l = BestMs(() => SegmentInductance.Fill(mesh, parallel: true));
        double p = BestMs(() => NodePotential.Fill(mesh, parallel: true));
        double assemble = BestMs(() => MomAssembly.Build(mesh), reps: 1);

        output.WriteLine($"N_s = {mesh.SegmentCount}, N_n = {mesh.NodeCount}, N_r = {mesh.ReducedCount}");
        output.WriteLine($"L fill {l:F1} ms, P fill {p:F1} ms ({p / l:F2} x), assembly {assemble:F0} ms ({assemble / (l + p):F0} x the fills)");

        // §5.2's claim is "a FRACTION of the inductance fill, not 4 x it", and that is what is gated.
        // The exact ratio is configuration-sensitive -- 0.45 x in Release, ~1.2 x in Debug, where the
        // interpreter tax lands differently on Grover's transcendentals than on a reciprocal square
        // root -- so a "< 1" gate would flake on a routine Debug run for no physical reason.
        Assert.True(p < 2.0 * l, $"The P fill ({p:F1} ms) must stay a fraction of the L fill ({l:F1} ms), not a multiple.");

        // A ceiling rather than a target: the assembly IS the one-time cost of this kernel (~40 x the
        // fills as measured), and this catches an accidental order-of-growth regression rather than
        // pinning the current constant.
        Assert.True(assemble < 400.0 * (l + p),
            $"The assembly is {assemble / (l + p):F0} x the fills, against a measured ~42 x — that is an " +
            "order-of-growth regression, not a constant-factor one.");
    }

    /// <summary>
    /// The mesh report's predicted peak is not wildly wrong about what the run actually allocates.
    ///
    /// <para>It is deliberately a <b>lower</b> bound on allocation: the prediction covers the four big
    /// matrices and WM-2's own, while <c>GC.GetTotalAllocatedBytes</c> counts every transient the run
    /// makes (including the N_r right-hand-side vectors, which are re-allocated per column). A
    /// prediction below the total allocation is expected; one far above it would mean the arithmetic in
    /// §8 is wrong.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void ThePredictedPeakIsTheRightSize()
    {
        var design = TestDesigns.PowerAmplifier(wireCount: 40, arrayCount: 10, pointsPerWire: 7);
        var predicted = WireMomMesh.Predict(design);

        long before = GC.GetTotalAllocatedBytes();
        var mesh = WireMomMesh.Build(design);
        var l = SegmentInductance.Fill(mesh);
        var assembly = MomAssembly.Build(mesh);
        long allocated = GC.GetTotalAllocatedBytes() - before;

        output.WriteLine($"{predicted}");
        output.WriteLine($"{predicted.MemoryArithmetic}");
        output.WriteLine($"actually allocated {allocated / 1048576.0:F1} MB");

        GC.KeepAlive(l);
        GC.KeepAlive(assembly);

        Assert.InRange(predicted.PredictedPeakBytes / (double)allocated, 0.1, 2.0);
    }

    /// <summary>A design whose polyline vertices are exactly the mesh's own segment endpoints.</summary>
    private static WBondDesign SubdividedDesign(WireMomMesh mesh)
    {
        var design = new WBondDesign();
        foreach (var name in mesh.ArrayNames) design.Arrays.Add(new WireArray { Name = name });

        for (int w = 0; w < mesh.WireCount; w++)
        {
            var wire = new Wire { DiameterNm = mesh.Wires[w].DiameterNm };
            int s = mesh.WireSegStart[w], e = s + mesh.WireSegCount[w];
            wire.Points.Add(Point3.FromMetres(mesh.Segments[s].Ax, mesh.Segments[s].Ay, mesh.Segments[s].Az));
            for (int k = s; k < e; k++)
            {
                ref readonly var f = ref mesh.Segments[k];
                wire.Points.Add(Point3.FromMetres(f.Ax + f.Ux * f.Length, f.Ay + f.Uy * f.Length, f.Az + f.Uz * f.Length));
            }
            design.Arrays[mesh.ArrayOfWire[w]].Wires.Add(wire);
        }
        return design;
    }
}
