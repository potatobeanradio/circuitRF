using System.Text.Json;
using CircuitRF.Core.Design;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-dfr28-3 R-dfr28-3-2 — Robust meshing: a 3D run removes the outline points that lie within a fraction of a
//  micrometre of their neighbours' line. On in a new setup and (brief-emb-2 R-emb2-3) in a file that does not say; no
//  outline moves farther than the tolerance; a corner keeps its point; and a problem built with it off is the artwork as drawn.
// ══════════════════════════════════════════════════════════════════════════════════════════════

public sealed class RobustMeshingTests
{
    private const double Um = 1e-6;

    [Fact]
    public void ANewSetup_TurnsItOn_AndAFileThatDoesNotSayReadsOn()
    {
        var created = EmSetup.CreateNew("line", "line.clay");
        Assert.True(created.RobustMeshing);
        Assert.DoesNotContain("RobustMeshing", EmSetupPersistence.Serialize(created));      // on is the default, never written
        Assert.True(EmSetupPersistence.Deserialize(EmSetupPersistence.Serialize(created)).RobustMeshing);
        Assert.True(EmSetupPersistence.FromEmbedded(EmSetupPersistence.ToEmbedded(created)).RobustMeshing);

        created.RobustMeshing = false;
        Assert.Contains("\"RobustMeshing\": false", EmSetupPersistence.Serialize(created));
        Assert.False(EmSetupPersistence.Deserialize(EmSetupPersistence.Serialize(created)).RobustMeshing);

        // A .cem and an embedded setup that do not state it read as on.
        string unsaid = EmSetupPersistence.Serialize(new EmSetup { Name = "line", LayoutRef = "line.clay" });
        Assert.DoesNotContain("RobustMeshing", unsaid);
        Assert.True(EmSetupPersistence.Deserialize(unsaid).RobustMeshing);
        var embedded = EmSetupPersistence.ToEmbedded(new EmSetup { Name = "line" });
        Assert.False(embedded.TryGetProperty("RobustMeshing", out _));
        Assert.True(EmSetupPersistence.FromEmbedded(embedded).RobustMeshing);
    }

    /// <summary>The TP149 trace's corner, in its shape: a quarter arc of 6.89 µm segments ending in a 1.44 µm stub, a 0.1 µm
    /// edge, a straight run of collinear points, and four real corners. The stub, the 0.1 µm edge, the collinear points
    /// and some of the arc go; every original point stays within the tolerance of the result; the corners stay.</summary>
    [Fact]
    public void TheCleanup_RemovesStubsAndTinyEdges_MovesNothingFartherThanTheTolerance_AndKeepsTheCorners()
    {
        var ring = new List<(double X, double Y)> { (0, 0), (500 * Um, 0) };
        for (int k = 1; k <= 3; k++) ring.Add((500 * Um, 100 * Um * k));                     // collinear points
        ring.Add((500 * Um, 400.1 * Um));                                                    // a 0.1 µm edge
        ring.Add((500 * Um, 500 * Um));
        double r = 66 * Um, step = 6.89 * Um / r;                                            // the arc, then its stub
        for (double a = step; a < Math.PI / 2; a += step) ring.Add((434 * Um + r * Math.Cos(a), 500 * Um + r * Math.Sin(a)));
        ring.Add((434 * Um + 1.44 * Um * 0.02, 566 * Um));
        ring.Add((434 * Um, 566 * Um));
        ring.Add((0, 566 * Um));

        var stats = new Em3dOutlineCleanup.Stats();
        double tol = Em3dOutlineCleanup.Tolerance(35 * Um);
        Assert.Equal(0.5 * Um, tol);
        var kept = Em3dOutlineCleanup.Clean(ring, q => q, tol, stats);

        Assert.Equal(ring.Count - kept.Count, stats.Removed);
        Assert.True(stats.Removed >= 6, $"removed {stats.Removed}");
        Assert.InRange(stats.MaxMoveM, 0, tol);
        foreach (var corner in new[] { (0.0, 0.0), (500 * Um, 0.0), (0.0, 566 * Um) }) Assert.Contains(corner, kept);
        foreach (var p in ring) Assert.InRange(DistanceToRing(p, kept), 0, tol);
        Assert.Equal(1.0e-6, Em3dOutlineCleanup.Tolerance(50 * Um) * 2, 12);                // the cap
        Assert.Equal(40e-9, Em3dOutlineCleanup.Tolerance(2 * Um), 15);                       // a thin-film metal: 1/50
    }

    /// <summary>The generator's thru with a 0.1 µm bump and a collinear point on its top edge: on, the conductor is the
    /// rectangle and a note says what moved; off, it is the artwork as drawn and nothing is said.</summary>
    [Fact]
    public void AGeneratedProblem_LosesTheTinyEdges_OnlyWhenItIsOn()
    {
        var (setup, source) = BumpedThru();
        setup.RobustMeshing = true;
        var on = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(on.Ok, on.Refusal);
        Assert.Equal(4, Line(on.Problem!).Outline.Count);
        var note = Assert.Single(on.Notes, n => n.StartsWith("Robust meshing removed 2 of", StringComparison.Ordinal));
        Assert.Contains("no outline moved by more than 0.1 µm", note);

        setup.RobustMeshing = false;
        var off = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(off.Ok, off.Refusal);
        Assert.Equal(6, Line(off.Problem!).Outline.Count);
        Assert.DoesNotContain(off.Notes, n => n.StartsWith("Robust meshing", StringComparison.Ordinal));
    }

    private static Em3dExtrudedPolygon Line(Em3dProblem p) =>
        Assert.IsType<Em3dExtrudedPolygon>(Assert.Single(p.Solids, s => s.Role == Em3dRole.Conductor).Primitive);

    private static (EmSetup, EmLayoutSource) BumpedThru()
    {
        const string clay = """
            {
              "FormatVersion": 1, "DbuPerMicron": 1000, "DisplayUnit": "Mm", "SnapDbu": 0,
              "Shapes": [
                { "$type": "Poly", "Layer": { "Layer": 1, "Datatype": 0 },
                  "Xy": [ 0, -550000, 10000000, -550000, 10000000, 550000, 6000000, 550000, 5000000, 550100, 0, 550000 ] },
                { "$type": "Label", "Layer": { "Layer": 1, "Datatype": 0 },
                  "X": 0, "Y": 0, "Text": "1", "Height": 400000, "IsPort": true, "PortDirection": "R0" },
                { "$type": "Label", "Layer": { "Layer": 1, "Datatype": 0 },
                  "X": 10000000, "Y": 0, "Text": "2", "Height": 400000, "IsPort": true, "PortDirection": "R180" }
              ],
              "Instances": []
            }
            """;
        var view = LayoutPersistence.Deserialize(clay);
        var tech = ShippedTechnologies.Load("pcb-2layer_RO4350B_20mil_1oz");
        var setup = new EmSetup
        {
            Name = "thru", LayoutRef = "thru/layout/thru.clay",
            Frequency = new FrequencySpec("1", "10", 4, SweepKind.Linear, "GHz", "GHz"),
            Solver3D = Em3dSolverId.Palace,
        };
        return (setup, new EmLayoutSource("/nowhere/thru.clay", view, tech, view.DbuPerMicron));
    }

    private static double DistanceToRing((double X, double Y) q, IReadOnlyList<(double X, double Y)> ring)
    {
        double best = double.PositiveInfinity;
        for (int i = 0; i < ring.Count; i++)
        {
            var (a, c) = (ring[i], ring[(i + 1) % ring.Count]);
            double ux = c.X - a.X, uy = c.Y - a.Y, l2 = ux * ux + uy * uy;
            double t = l2 > 0 ? Math.Clamp(((q.X - a.X) * ux + (q.Y - a.Y) * uy) / l2, 0, 1) : 0;
            best = Math.Min(best, Math.Sqrt(Math.Pow(q.X - a.X - t * ux, 2) + Math.Pow(q.Y - a.Y - t * uy, 2)));
        }
        return best;
    }
}
