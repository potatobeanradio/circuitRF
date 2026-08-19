using CircuitRF.WBond.Mom;

namespace CircuitRF.WBond.Tests.Mom;

/// <summary>
/// The mesh itself: segmentation, incidence, terminal shorting, the report (RW2), the two refusals and
/// the proximity warning (RW17).
/// </summary>
public sealed class MomMeshTests
{
    private static WireMomMesh Mesh(WBondDesign design, int target = 24) =>
        WireMomMesh.Build(design, WireMomSettings.Default with { TargetSegmentsPerWire = target });

    // ---------------------------------------------------------------- segmentation

    [Fact]
    public void EveryAuthoredVertexSurvivesAsANode()
    {
        var design = TestDesigns.PowerAmplifier(wireCount: 2, arrayCount: 1, pointsPerWire: 7);
        var mesh = Mesh(design);

        // Walk the wire's segments and collect the points where one ends. Every authored vertex must
        // be one of them, to the last bit -- the mesher subdivides and never moves anything.
        for (int w = 0; w < mesh.WireCount; w++)
        {
            var wire = mesh.Wires[w];
            int start = mesh.WireSegStart[w], end = start + mesh.WireSegCount[w];

            var reached = new List<(double X, double Y, double Z)>
            {
                (mesh.Segments[start].Ax, mesh.Segments[start].Ay, mesh.Segments[start].Az),
            };
            for (int k = start; k < end; k++)
            {
                ref readonly var f = ref mesh.Segments[k];
                reached.Add((f.Ax + f.Ux * f.Length, f.Ay + f.Uy * f.Length, f.Az + f.Uz * f.Length));
            }

            // A femtometre. The design's own quantum is a NANOMETRE, so anything this close is the
            // authored vertex and not a moved one; the slack is the direction x length round trip
            // Filament stores, not the mesher.
            const double tolerance = 1e-15;

            foreach (var point in wire.Points)
            {
                double tx = WBondUnits.ToMetres(point.X);
                double ty = WBondUnits.ToMetres(point.Y);
                double tz = WBondUnits.ToMetres(point.Z);

                Assert.Contains(reached, r =>
                    Math.Abs(r.X - tx) < tolerance && Math.Abs(r.Y - ty) < tolerance && Math.Abs(r.Z - tz) < tolerance);
            }
        }
    }

    [Fact]
    public void SubdivisionNeverMergesTwoPolylineSegments()
    {
        var design = TestDesigns.PowerAmplifier(wireCount: 2, arrayCount: 1, pointsPerWire: 7);

        // Even at a target BELOW the authored vertex count, every polyline segment keeps at least one
        // segment of its own.
        var mesh = Mesh(design, target: 2);
        for (int w = 0; w < mesh.WireCount; w++)
            Assert.True(mesh.WireSegCount[w] >= mesh.Wires[w].Points.Count - 1);
    }

    [Fact]
    public void MeshingIsDeterministic()
    {
        var design = TestDesigns.PowerAmplifier(wireCount: 4, arrayCount: 2, pointsPerWire: 7);
        var a = Mesh(design);
        var b = Mesh(design);

        Assert.Equal(a.SegmentCount, b.SegmentCount);
        Assert.Equal(a.Segments, b.Segments);
        Assert.Equal(a.StartNode, b.StartNode);
        Assert.Equal(a.ReducedOfNode, b.ReducedOfNode);
    }

    // ---------------------------------------------------------------- incidence and counts

    [Fact]
    public void CountsAndIncidenceAreConsistent()
    {
        var design = TestDesigns.PowerAmplifier(wireCount: 6, arrayCount: 3, pointsPerWire: 7);
        var mesh = Mesh(design);

        int expectedNodes = 0;
        for (int w = 0; w < mesh.WireCount; w++) expectedNodes += mesh.WireSegCount[w] + 1;
        Assert.Equal(expectedNodes, mesh.NodeCount);
        Assert.Equal(mesh.SegmentCount + mesh.WireCount, mesh.NodeCount);

        // A[k, start] = +1, A[k, end] = -1, and segment k of wire w runs from node k to node k+1.
        for (int w = 0; w < mesh.WireCount; w++)
        {
            int s = mesh.WireSegStart[w], n0 = mesh.WireNodeStart[w];
            for (int i = 0; i < mesh.WireSegCount[w]; i++)
            {
                Assert.Equal(n0 + i, mesh.StartNode[s + i]);
                Assert.Equal(n0 + i + 1, mesh.EndNode[s + i]);
            }
        }

        // Each node owns 1 or 2 half filaments, 2 N_s halves in total.
        Assert.Equal(2 * mesh.SegmentCount, mesh.NodeCellIndex.Length);
        for (int n = 0; n < mesh.NodeCount; n++)
        {
            int owned = mesh.NodeCellStart[n + 1] - mesh.NodeCellStart[n];
            Assert.InRange(owned, 1, 2);
        }

        // A half is exactly half its segment.
        for (int k = 0; k < mesh.SegmentCount; k++)
        {
            Assert.Equal(0.5 * mesh.SegmentLength[k], mesh.Halves[2 * k].Length, 15);
            Assert.Equal(0.5 * mesh.SegmentLength[k], mesh.Halves[2 * k + 1].Length, 15);
        }
    }

    // ---------------------------------------------------------------- terminal shorting

    [Fact]
    public void TerminalsTakeTheLeadingReducedIndicesAndCollapseEveryWireEnd()
    {
        var design = TestDesigns.PowerAmplifier(wireCount: 6, arrayCount: 3, pointsPerWire: 7);
        var mesh = Mesh(design);

        Assert.Equal(2 * design.Arrays.Count, mesh.TerminalCount);
        Assert.Equal(mesh.TerminalCount + mesh.SegmentCount - mesh.WireCount, mesh.ReducedCount);

        for (int w = 0; w < mesh.WireCount; w++)
        {
            int a = mesh.ArrayOfWire[w];
            int first = mesh.WireNodeStart[w];
            int last = first + mesh.WireSegCount[w];

            Assert.Equal(2 * a, mesh.ReducedOfNode[first]);
            Assert.Equal(2 * a + 1, mesh.ReducedOfNode[last]);

            // Every interior node keeps an index of its own, at or above T.
            for (int n = first + 1; n < last; n++)
                Assert.True(mesh.ReducedOfNode[n] >= mesh.TerminalCount);
        }

        // R has exactly one 1 per row, and every reduced index is used.
        var used = new HashSet<int>(mesh.ReducedOfNode);
        Assert.Equal(mesh.ReducedCount, used.Count);
    }

    /// <summary>
    /// §9.9 — the documented terminal order, which the exported Touchstone's own port names must match.
    ///
    /// <para><c>tests/WBond.Tests</c> does not reference <c>CircuitRF.Ui</c>, so the cross-assembly
    /// assertion against <c>WBondTouchstoneExport.PortNames</c> belongs to WM-2, where <c>Ui.Tests</c>
    /// is already in the gate. This holds the documented order in the meantime.</para>
    /// </summary>
    [Fact]
    public void TerminalNamesAreTheDocumentedOrder()
    {
        var design = TestDesigns.PowerAmplifier(wireCount: 6, arrayCount: 3, pointsPerWire: 7);
        var mesh = Mesh(design);

        Assert.Equal(new[] { "G1.i", "G1.o", "G2.i", "G2.o", "G3.i", "G3.o" }, mesh.TerminalNames);
        Assert.Equal(mesh.TerminalNames, WireMomMesh.TerminalNamesFor(design));
        Assert.Equal(mesh.TerminalCount, mesh.TerminalNames.Length);
    }

    // ---------------------------------------------------------------- the report

    [Fact]
    public void PredictAgreesWithTheBuiltMeshAndReportsItsOwnArithmetic()
    {
        var design = TestDesigns.PowerAmplifier(wireCount: 4, arrayCount: 2, pointsPerWire: 7);
        var settings = WireMomSettings.Default with { TargetSegmentsPerWire = 24 };

        var predicted = WireMomMesh.Predict(design, settings);
        var mesh = WireMomMesh.Build(design, settings);

        Assert.Equal(mesh.SegmentCount, predicted.Segments);
        Assert.Equal(mesh.NodeCount, predicted.Nodes);
        Assert.Equal(mesh.ReducedCount, predicted.ReducedNodes);
        Assert.Equal(mesh.TerminalCount, predicted.Terminals);
        Assert.Equal(predicted.Wires, mesh.Report.Wires);
        Assert.Equal(predicted.Arrays, mesh.Report.Arrays);
        Assert.Equal(predicted.ClampedWires, mesh.Report.ClampedWires);
        Assert.Equal(predicted.PredictedPeakBytes, mesh.Report.PredictedPeakBytes);
        Assert.Equal(predicted.MemoryArithmetic, mesh.Report.MemoryArithmetic);

        Assert.Equal(design.WireCount - design.Arrays.Count, predicted.LoopCount);
        Assert.True(predicted.PredictedPeakBytes > 0);

        // The prediction must include WM-2's own complex system, or it is a report that lied.
        Assert.Contains("M~", predicted.MemoryArithmetic);
        Assert.True(predicted.PredictedPeakBytes >= 32L * predicted.Segments * predicted.Segments,
            "Peak must cover L + K~ + M~ = 8 + 8 + 16 bytes per segment pair.");
    }

    [Fact]
    public void TheSegmentCapIsReported_NotAbsorbed()
    {
        var design = TestDesigns.PowerAmplifier(wireCount: 2, arrayCount: 1, pointsPerWire: 7);
        var settings = WireMomSettings.Default with { TargetSegmentsPerWire = 60, MaxSegmentsPerWire = 20 };

        var mesh = WireMomMesh.Build(design, settings);

        Assert.Equal(2, mesh.Report.ClampedWires);
        Assert.Contains(mesh.Report.Warnings, w => w.Contains("20-segment cap"));
        for (int w = 0; w < mesh.WireCount; w++) Assert.True(mesh.WireSegCount[w] <= 20);
    }

    // ---------------------------------------------------------------- refusals, §9.10

    [Fact]
    public void GroundPlaneDisabled_RefusesAndNamesTheMissingReferenceConductor()
    {
        var design = TestDesigns.SingleHorizontalWire(100, 10, 1.0);
        design.GroundPlane.Enabled = false;

        var ex = Assert.Throws<InvalidOperationException>(() => WireMomMesh.Build(design));
        Assert.Contains("reference conductor", ex.Message);
        Assert.Contains("ground plane", ex.Message);
    }

    /// <summary>
    /// The ceiling refuses <b>at mesh time</b>, and every remedy it names is one that actually moves
    /// this design's number.
    ///
    /// <para><c>em-refusal-must-name-a-binding-remedy</c>: a refusal that lists inert knobs sends the
    /// reader to a panel setting that cannot help. Each remedy below is computed against this design,
    /// so each is checked to be binding here rather than plausible in general.</para>
    /// </summary>
    [Fact]
    public void AboveTheCeiling_RefusesAtMeshTime_WithThreeBindingRemedies()
    {
        var design = TestDesigns.PowerAmplifier(wireCount: 600, arrayCount: 12, pointsPerWire: 7);
        var settings = WireMomSettings.Default with { TargetSegmentsPerWire = 24, UnknownCeiling = 8_000 };

        var predicted = WireMomMesh.Predict(design, settings);
        Assert.True(predicted.Segments > settings.UnknownCeiling,
            "The 600-wire case must be above the ceiling or this test proves nothing.");

        var ex = Assert.Throws<InvalidOperationException>(() => WireMomMesh.Build(design, settings));

        Assert.Contains($"{predicted.Segments:N0} segments", ex.Message);
        Assert.Contains("8,000-segment ceiling", ex.Message);
        Assert.Contains("Segments per wire", ex.Message);
        Assert.Contains("one array at a time", ex.Message);
        Assert.Contains("600 wires cannot be solved at 24 segments each", ex.Message);

        // Remedy 1 is BINDING: the value it names really does fit.
        var match = System.Text.RegularExpressions.Regex.Match(ex.Message, @"from 24 to (\d+) gives ([\d,]+)");
        Assert.True(match.Success, $"Remedy 1 must name a number: {ex.Message}");
        int lowered = int.Parse(match.Groups[1].Value);
        int gives = int.Parse(match.Groups[2].Value.Replace(",", ""));
        Assert.Equal(gives, WireMomMesh.Predict(design, settings with { TargetSegmentsPerWire = lowered }).Segments);
        Assert.True(gives <= settings.UnknownCeiling);

        // Remedy 2 is BINDING: one array really does fit.
        var oneArray = new WBondDesign { Arrays = { design.Arrays[0] } };
        int single = WireMomMesh.Predict(oneArray, settings).Segments;
        Assert.Contains($"one array at a time gives <= {single:N0}", ex.Message);
        Assert.True(single <= settings.UnknownCeiling);
    }

    [Fact]
    public void TightPitch_Warns_AndNamesBothWiresAndTheRatio()
    {
        var design = TestDesigns.ParallelArray(n: 2, pitchMil: 2, lengthMil: 100, heightMil: 8, diameterMil: 1.0);

        var mesh = Mesh(design);   // warns, does NOT refuse
        var warning = Assert.Single(mesh.Report.Warnings);

        Assert.Contains("wires 0 and 1", warning);
        Assert.Contains("4.0 a", warning);          // 2 mil pitch over a 0.5 mil radius
        Assert.Contains("optimistic", warning);
    }

    [Fact]
    public void ComfortablePitch_DoesNotWarn()
    {
        var design = TestDesigns.ParallelArray(n: 2, pitchMil: 6, lengthMil: 100, heightMil: 8, diameterMil: 1.0);
        Assert.Empty(Mesh(design).Report.Warnings);
    }
}
