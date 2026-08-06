// L8e Tier 5 — D6/R-res-10: the refusal audit, as tests.
//
// EVERY message in the repository that said "arrives with the full-wave kernel at L8" is now in one
// of three states, and each was classified by hand:
//
//   TRUE       — the capability now exists. The message names kernel B and how to reach it.
//   MISLEADING — L8 exists but only for ONE grounded slab with ONE conductor layer, so the feature
//                still is not supported. Re-pointed at L9, by name.
//   WRONG      — the message described a capability boundary that has MOVED. Fixed.
//
// A refusal nobody asserts is a string that drifts, so there is a test per message. The two ENGINE
// messages (the sloped-dielectric refusal and the conductor-ceiling reason) are asserted in
// Engine.Tests, where they live; everything here is the Ui-side extractor's.

using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;

namespace CircuitRF.Ui.Tests.Em;

public class EmRefusalWordingTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey TopCopper = new(1, 0);

    private static long Mm(double mm) => (long)Math.Round(mm * 1000 * Dbu);

    private static EmExtractionResult Extract(params LayoutShape[] shapes)
        => CrossSectionExtractor.Extract(shapes, StarterTechnologies.Pcb2Layer(), Dbu);

    private static string Refused(EmExtractionResult r)
    {
        Assert.False(r.Ok);
        Assert.NotNull(r.Refusal);
        return r.Refusal!;
    }

    /// <summary>
    /// The shared shape every TRUE-now refusal ends with. Asserted through the constant rather than
    /// through a copy of its text, so a re-wording moves one string and every test follows it — the
    /// opposite of what "arrives in L8" did, which was to be re-typed in nine places and go stale in
    /// all nine at once.
    /// </summary>
    private static void PointsAtKernelB(string reason)
    {
        Assert.Contains(CrossSectionExtractor.PlanarAlternative, reason, StringComparison.Ordinal);
        Assert.Contains("Planar", reason, StringComparison.Ordinal);
        Assert.Contains("Auto",   reason, StringComparison.Ordinal);
        Assert.DoesNotContain("arrives in L8", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("arrives with the full-wave kernel at L8", reason, StringComparison.Ordinal);
    }

    // ══ TRUE — kernel B exists and accepts this geometry ══════════════════════════════════════

    [Fact]
    public void ABend_WasTRUE_NowNamesKernelB()
    {
        long w = Mm(2.9), l = Mm(20);
        PointsAtKernelB(Refused(Extract(new PolygonShape
        {
            Layer = TopCopper,
            Xy = [0, 0, l, 0, l, l, l - w, l, l - w, w, 0, w],
        })));
    }

    [Fact]
    public void ACurvedEdge_WasTRUE_NowNamesKernelB()
        => PointsAtKernelB(Refused(Extract(new CurveShape
        {
            Layer = TopCopper,
            Xy = [0, 0, Mm(20), 0, Mm(20), Mm(2.9), 0, Mm(2.9)],
            Edges =
            [
                new LayoutEdge { Kind = EdgeKind.Line },
                new LayoutEdge { Kind = EdgeKind.Arc, Bulge = 0.4142 },
                new LayoutEdge { Kind = EdgeKind.Line },
                new LayoutEdge { Kind = EdgeKind.Line },
            ],
        })));

    [Fact]
    public void ACircle_WasTRUE_NowNamesKernelB()
        => PointsAtKernelB(Refused(Extract(
            new CircleShape { Layer = TopCopper, Cx = Mm(5), Cy = Mm(5), R = Mm(1) })));

    [Fact]
    public void ARoundedRectangle_WasTRUE_NowNamesKernelB()
        => PointsAtKernelB(Refused(Extract(new RoundedRectShape
        {
            Layer = TopCopper, X1 = 0, Y1 = 0, X2 = Mm(20), Y2 = Mm(2.9), CornerRadius = Mm(0.5),
        })));

    [Fact]
    public void APolygonWithAHole_WasTRUE_NowNamesKernelB()
        => PointsAtKernelB(Refused(Extract(new PolygonShape
        {
            Layer = TopCopper,
            Xy    = [0, 0, Mm(20), 0, Mm(20), Mm(2.9), 0, Mm(2.9)],
            Holes = [[Mm(5), Mm(1), Mm(6), Mm(1), Mm(6), Mm(2), Mm(5), Mm(2)]],
        })));

    [Fact]
    public void ATaper_WasTRUE_NowNamesKernelB_AndTheAnalyticModelThatIsFree()
    {
        string reason = Refused(Extract(new PolygonShape
        {
            Layer = TopCopper,
            Xy    = [0, 0, Mm(20), Mm(-1), Mm(20), Mm(2), 0, Mm(1)],
        }));

        PointsAtKernelB(reason);
        // R-msh-8a's own point, restated where the user meets it: a smooth taper is exactly what
        // kernel B is NOT for, and the shipped closed-form model is free.
        Assert.Contains("MicrostripTaperModel", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void NonParallelConductors_WasTRUE_NowNameKernelB()
        => PointsAtKernelB(Refused(Extract(
            new RectShape { Layer = TopCopper, X1 = 0,      Y1 = 0, X2 = Mm(20),   Y2 = Mm(2.9) },
            new RectShape { Layer = TopCopper, X1 = Mm(30), Y1 = 0, X2 = Mm(32.9), Y2 = Mm(20) })));

    [Fact]
    public void ABentTrace_WasTRUE_NowNamesKernelB()
        => PointsAtKernelB(Refused(Extract(new PathShape
        {
            Layer = TopCopper,
            Xy    = [0, 0, Mm(20), 0, Mm(20), Mm(20)],
            Width = Mm(2.9),
        })));

    // ══ MISLEADING — L8 exists, but not for THIS. ════════════════════════════════════════════
    //
    // L9e UPDATED, NOT LOOSENED. These three used to assert the note pointed at "L9". L9 has now
    // arrived in full and two of the three capabilities STILL do not exist, which is exactly the
    // failure D7's own rule names: a phase number is a promise about a schedule, and it expires.
    // What is asserted now is the same thing one level deeper — that the note names WHY, and names
    // where the capability lives when it lives anywhere.

    /// <summary>
    /// A finite ground POUR. Kernel B does not mesh one either — its ground is the grounded slab's
    /// own laterally infinite plane, handled analytically by the Green's function (L8a's D2), and
    /// that stayed true through L9: a <c>LayerStack</c>'s PEC termination is an infinite plane by
    /// definition. So the note must name the real obstacle (the ground would have to be MESHED),
    /// not a phase.
    /// </summary>
    [Fact]
    public void AFiniteGroundPour_WasMISLEADING_NowNamesTheObstacle_NotAPhase()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var groundLayer = tech.Stackup.Layers
            .First(l => l.Kind == StackupKind.Conductor && l.IsGroundReference)
            .DrawingLayers[0];

        var result = CrossSectionExtractor.Extract(
            [
                new RectShape { Layer = TopCopper,   X1 = 0, Y1 = 0, X2 = Mm(20), Y2 = Mm(2.9) },
                new RectShape { Layer = groundLayer, X1 = 0, Y1 = 0, X2 = Mm(20), Y2 = Mm(20) },
            ],
            tech, Dbu);

        string note = Assert.Single(result.Notes, n =>
            n.Contains("ground-designated conductor layer", StringComparison.Ordinal));

        Assert.Contains("MESHED", note, StringComparison.Ordinal);
        Assert.DoesNotContain("arrives at L8", note, StringComparison.Ordinal);
        Assert.DoesNotContain("at L9", note, StringComparison.Ordinal);
        Assert.DoesNotContain(CrossSectionExtractor.PlanarAlternative, note, StringComparison.Ordinal);
    }

    /// <summary>The planar extractor's own ground refusal says the same thing from the other side —
    /// asserted so the two cannot drift into disagreeing about whose capability this is.</summary>
    [Fact]
    public void ThePlanarExtractor_AgreesThatAFiniteGroundPourIsNobodys()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var groundLayer = tech.Stackup.Layers
            .First(l => l.Kind == StackupKind.Conductor && l.IsGroundReference)
            .DrawingLayers[0];

        var result = PlanarExtractor.Extract(
            [
                new RectShape { Layer = TopCopper,   X1 = 0, Y1 = 0, X2 = Mm(20), Y2 = Mm(2.9) },
                new RectShape { Layer = groundLayer, X1 = 0, Y1 = 0, X2 = Mm(20), Y2 = Mm(20) },
            ],
            tech, Dbu);

        Assert.True(result.Ok);
        Assert.Contains(result.Notes, n =>
            n.Contains("ground-designated conductor layer", StringComparison.Ordinal) &&
            !n.Contains("at L9", StringComparison.Ordinal));
    }

    /// <summary>
    /// Vias used to point at L9. <b>L9 arrived and BUILT them</b> (L9c's basis, L9d's solve), so the
    /// refusal is NARROWED rather than deleted — kernel A still cannot carry z-directed current, and
    /// the note now says where it IS carried instead of when.
    /// </summary>
    [Fact]
    public void AVia_PointedAtL9_AndL9BuiltIt_SoTheNoteNowNamesKernelB()
    {
        string reason = Refused(Extract(
            new ViaShape { Layer = TopCopper, X = Mm(5), Y = Mm(1), PadSize = Mm(0.5), DrillSize = Mm(0.3) }));

        Assert.Contains("planar", reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("at L9", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("L8", reason, StringComparison.Ordinal);
    }

    // ══ The sweep: nothing anywhere in src/ still promises a capability "at L8" ═══════════════

    /// <summary>
    /// The audit's own completeness check. A phase-number promise is exactly the kind of string that
    /// goes stale silently, so the repository is scanned for the pattern rather than trusted to have
    /// been fully swept by hand.
    /// </summary>
    [Fact]
    public void NoUserFacingMessageInSrc_StillPromisesACapabilityArrivingAtAPHASE()
    {
        string root = RepoRoot();
        var offenders = new List<string>();

        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs",
                                                         SearchOption.AllDirectories))
        {
            int line = 0;
            foreach (string raw in File.ReadLines(file))
            {
                line++;
                string t = raw.TrimStart();
                if (t.StartsWith("//", StringComparison.Ordinal) ||
                    t.StartsWith("///", StringComparison.Ordinal) ||
                    t.StartsWith("*", StringComparison.Ordinal)) continue;   // prose, not a message

                // L9e WIDENED this from "L8" to ANY phase letter. §3's own argument: a phase
                // number is a promise about a SCHEDULE and a §-reference is a statement about a
                // DESIGN. L8d's coplanar refusals pointed at "L9", L9 arrived, and neither was
                // built — which is why the rule is now "name WHERE a capability arrives, not WHEN".
                foreach (string phrase in new[] { "arrive at L", "arrives at L", "arrive in L",
                                                  "arrives in L", "arrive with L", "arrives with L" })
                    if (raw.Contains(phrase, StringComparison.Ordinal))
                    { offenders.Add($"{Path.GetRelativePath(root, file)}:{line}  [{phrase}]"); break; }
            }
        }

        Assert.True(offenders.Count == 0,
            "These messages promise a capability arriving at a PHASE. A phase number is a promise " +
            "about a schedule and it expires — L8d's own coplanar refusals pointed at \"L9\", L9 " +
            "arrived, and neither was built. Name WHERE the capability arrives (the type, the method, " +
            "the design-note section) or say plainly that nothing provides it:\n  " +
            string.Join("\n  ", offenders));
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "circuitrf.slnx"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }
}
