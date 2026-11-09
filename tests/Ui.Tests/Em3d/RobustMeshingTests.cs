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

    /// <summary>Designer round 35 — an outline runs out to (−11.641, 50.999) µm and straight back along the same line. The tip goes, the copper's own corners stay, the note says so, and the
    /// spike is not counted as an outline moving. A sharp corner of real copper stays.</summary>
    [Fact]
    public void AZeroWidthSpike_IsCutOff_AndARealSharpCornerStays()
    {
        double ax = 200 * Um, ay = 100 * Um, ux = -11.641 * Um, uy = 50.999 * Um;
        (double X, double Y)[] ring =
        [
            (0, 0), (ax + 50 * Um, 0), (ax + 50 * Um, ay),
            (ax, ay),                                   // the foot of the needle
            (ax + ux, ay + uy),                         // the tip
            (ax + 0.46 * ux, ay + 0.46 * uy),           // back down the same line
            (0, ay + 0.46 * uy),
        ];
        var stats = new Em3dOutlineCleanup.Stats();
        var kept = Em3dOutlineCleanup.Clean(ring, q => q, 0.5 * Um, stats);
        Assert.DoesNotContain(ring[4], kept);
        Assert.Equal(1, stats.Spikes);
        Assert.True(stats.LongestSpikeM > 20 * Um);
        Assert.True(stats.MaxMoveM < 0.5 * Um);
        Assert.Contains("zero-width spike", Em3dLayoutSolids.Builder.RobustNote(stats), StringComparison.Ordinal);

        // A 3° point 200 µm long is copper, however sharp.
        (double X, double Y)[] sharp = [(0, 0), (200 * Um, 5 * Um), (0, 10.5 * Um), (-50 * Um, 5 * Um)];
        var none = new Em3dOutlineCleanup.Stats();
        Assert.Equal(sharp.Length, Em3dOutlineCleanup.Clean(sharp, q => q, 0.5 * Um, none).Count);
        Assert.Equal(0, none.Spikes);
    }

    /// <summary>Designer round 39, in its shape: a 1 oz pour holding a block across a 2.54 µm slot, bridged in the middle — a
    /// slot hole and a slot running in from the edge. Both close, no copper is lost, and a 50 µm hole of the same pour stays.</summary>
    [Fact]
    public void ASlotAFewMicrometresWide_IsClosed_AndAWiderHoleStays()
    {
        const double t = 35.56 * Um, gap = 2.54 * Um;
        double half = Em3dOutlineCleanup.SlotHalfWidth(t);
        Assert.True(2 * half > gap && 2 * half < 10 * Um);
        // Pour 0..3000 × 0..1000 µm; a slot along y = 600 µm from x = 0 to 400 µm entering from the left edge, and a slot hole
        // from x = 600 to 1200 µm on the same line; a 50 µm square hole elsewhere.
        (double X, double Y)[] outer =
        [
            (0, 0), (3000 * Um, 0), (3000 * Um, 1000 * Um), (0, 1000 * Um),
            (0, 600 * Um + gap), (400 * Um, 600 * Um + gap), (400 * Um, 600 * Um), (0, 600 * Um),
        ];
        (double X, double Y)[] slot = [(600 * Um, 600 * Um), (1200 * Um, 600 * Um), (1200 * Um, 600 * Um + gap), (600 * Um, 600 * Um + gap)];
        (double X, double Y)[] hole = [(2000 * Um, 200 * Um), (2050 * Um, 200 * Um), (2050 * Um, 250 * Um), (2000 * Um, 250 * Um)];
        // A via pad's hole the slot hole runs 3 µm into, as the imported pour's did: the overlap stays hole, not an island.
        (double X, double Y)[] pad = [(1197 * Um, 400 * Um), (1500 * Um, 400 * Um), (1500 * Um, 800 * Um), (1197 * Um, 800 * Um)];
        var stats = new Em3dOutlineCleanup.Stats();
        var closed = Em3dOutlineCleanup.CloseSlots([outer, slot, hole, pad], half, stats);
        Assert.NotNull(closed);
        Assert.Equal(3, closed!.Count);                                              // the outline, the pad's hole, the 50 µm hole
        // The inlet is gone: a plain rectangle once Clean, which Robust meshing runs next, takes the grid's picometre steps.
        Assert.Equal(4, Em3dOutlineCleanup.Clean(closed[0], q => q, 0.5 * Um, new Em3dOutlineCleanup.Stats()).Count);
        double Area(IReadOnlyList<(double X, double Y)> r)
            => new CircuitRF.Engine.Mom.PlanarPolygon([.. r.Select(q => new CircuitRF.Engine.Mom.EmPoint(q.X, q.Y))]).Area();
        Assert.Equal([2500e-12, 303 * 400e-12], closed.Skip(1).Select(Area).Order(), new ToleranceComparer(1e-15));
        Assert.Equal(997 * Um * gap, stats.SlotAreaM2, 1e-14);                      // 400 µm inlet + 597 µm of hole
        Assert.Contains("filled the slots and holes narrower than", Em3dLayoutSolids.Builder.RobustNote(stats), StringComparison.Ordinal);

        // Nothing that narrow: left as drawn.
        Assert.Null(Em3dOutlineCleanup.CloseSlots([outer[..4], hole], half, new Em3dOutlineCleanup.Stats()));
    }

    /// <summary>MMIC scale: the slot width scales with the metal, so a gap any foundry's rules allow stays. A 3 µm gold meander
    /// whose own turns are 1 µm apart, and a 0.25 µm first metal with a 0.5 µm slot, come back untouched; only a 0.1 µm crack in
    /// the gold (a merge artefact, far under any spacing rule) is filled.</summary>
    [Fact]
    public void AtMmicScale_OnlyACrackFarUnderTheSpacingRuleIsFilled()
    {
        Assert.Equal(0.3 * Um, Em3dOutlineCleanup.SlotHalfWidth(3 * Um), 1e-15);
        Assert.Equal(0.025 * Um, Em3dOutlineCleanup.SlotHalfWidth(0.25 * Um), 1e-15);

        // A U-shaped meander, 10 µm wide arms, its inside gap 1 µm (one conductor).
        static (double X, double Y)[] U(double gap) =>
        [
            (0, 0), (20 * Um + gap, 0), (20 * Um + gap, 200 * Um), (10 * Um + gap, 200 * Um),
            (10 * Um + gap, 10 * Um), (10 * Um, 10 * Um), (10 * Um, 200 * Um), (0, 200 * Um),
        ];
        Assert.Null(Em3dOutlineCleanup.CloseSlots([U(1 * Um)], Em3dOutlineCleanup.SlotHalfWidth(3 * Um), new Em3dOutlineCleanup.Stats()));
        (double X, double Y)[] metal1 = [(0, 0), (50 * Um, 0), (50 * Um, 20 * Um), (0, 20 * Um)];
        (double X, double Y)[] slot = [(10 * Um, 9.75 * Um), (40 * Um, 9.75 * Um), (40 * Um, 10.25 * Um), (10 * Um, 10.25 * Um)];
        Assert.Null(Em3dOutlineCleanup.CloseSlots([metal1, slot], Em3dOutlineCleanup.SlotHalfWidth(0.25 * Um), new Em3dOutlineCleanup.Stats()));

        var stats = new Em3dOutlineCleanup.Stats();
        Assert.NotNull(Em3dOutlineCleanup.CloseSlots([U(0.1 * Um)], Em3dOutlineCleanup.SlotHalfWidth(3 * Um), stats));
        Assert.Equal(190 * Um * 0.1 * Um, stats.SlotAreaM2, 1e-16);
    }

    private sealed class ToleranceComparer(double tolerance) : IEqualityComparer<double>
    {
        public bool Equals(double a, double b) => Math.Abs(a - b) <= tolerance;
        public int GetHashCode(double v) => 0;
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
