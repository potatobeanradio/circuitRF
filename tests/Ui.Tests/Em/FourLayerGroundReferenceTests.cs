// ================================================================
//  FourLayerGroundReferenceTests.cs — the 4-layer starter, run through the extractor rather than
//  read off the file, plus the two messages that shipping it exposed.
//
//  User question, 2026-08-30: "will users find the new 4-layer .ctech confusing to use given how the
//  ground layers are defined?" Probing all four conductors answered it — YES, and for reasons no
//  reading of the .ctech would have shown. R-em-4 resolves ground as the highest ground-designated
//  conductor BELOW the signal level, and that query returns nothing in TWO different situations:
//  no conductor is designated at all, and one is designated but sits ABOVE. Every 2-layer technology
//  makes those coincide (the only candidate is the bottom conductor), so the fallback note could say
//  "no conductor layer is marked as a ground reference" unconditionally and be right. On the first
//  technology with an INNER plane it was flatly false — a trace on a lower layer was told its
//  technology designates no ground while the Stackup tab plainly showed one ticked.
//
//  Both the message and the starter changed. These tests hold the pair together: the messages must
//  stay honest, and the starter must stay one whose every conductor reaches an actionable answer.
// ================================================================

using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;

namespace CircuitRF.Ui.Tests.Em;

public class FourLayerGroundReferenceTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private const string FourLayer = "pcb-4layer_FR-4_62mil_1oz";

    private static long Mil(double m) => (long)Math.Round(m * 25.4 * Dbu);

    private static Technology Tech() => ShippedTechnologies.Load(FourLayer);

    private static List<StackupLayer> Conductors(Technology t) =>
        [.. t.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor)];

    /// <summary>A 400 × 14 mil trace on one conductor's own drawing layer, and nothing else.</summary>
    private static PlanarExtractionResult OnConductor(Technology tech, int index)
        => PlanarExtractor.Extract(
            [new RectShape
            {
                Layer = Conductors(tech)[index].DrawingLayers[0],
                X1 = 0, Y1 = 0, X2 = Mil(400), Y2 = Mil(14),
            }],
            tech, Dbu);

    // ── The starter: every conductor reaches an answer someone can act on ─────────────────────

    /// <summary>L1 solves against Inner 1 — the whole reason the reference is an INNER plane rather
    /// than the bottom one. The note must NAME the plane: on a multi-plane stackup "the highest
    /// designated ground below the signal" is not something a user can read off the panel.</summary>
    [Fact]
    public void TopCopper_ReturnsThroughTheInnerPlane_AndTheNoteNamesIt()
    {
        var tech = Tech();
        var r = OnConductor(tech, 0);

        Assert.True(r.Ok, r.Refusal);
        Assert.Contains(r.Notes, n =>
            n.Contains($"Every port returns through '{Conductors(tech)[1].Name}'", StringComparison.Ordinal));
        Assert.DoesNotContain(r.Notes, n =>
            n.Contains("is marked as a ground reference", StringComparison.Ordinal));
    }

    /// <summary>Inner 2 solves too, against Bottom Copper. This is what the SECOND ground reference
    /// buys: with only Inner 1 designated, this layer had no plane beneath it, silently fell back to
    /// the Stackup.Bottom boundary, and solved against a reference 8 mil further away than the real
    /// one — a wrong answer with no refusal.</summary>
    [Fact]
    public void InnerSignalLayer_ReturnsThroughTheBottomPlane_NotTheStackupBoundary()
    {
        var tech = Tech();
        var r = OnConductor(tech, 2);

        Assert.True(r.Ok, r.Refusal);
        Assert.Contains(r.Notes, n =>
            n.Contains($"Every port returns through '{Conductors(tech)[3].Name}'", StringComparison.Ordinal));
        Assert.DoesNotContain(r.Notes, n =>
            n.Contains("Stackup.Bottom = Ground", StringComparison.Ordinal));
    }

    /// <summary>Both planes refuse, and the refusal must not tell the user to bind a layer that is
    /// already bound. "Draw the artwork on a conductor layer, or bind the layer it is on to a
    /// conductor entry" is exactly wrong here — the layer IS bound, to a ground conductor.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void APlane_RefusesByNamingTheGroundDesignation_NotAMissingBinding(int index)
    {
        var r = OnConductor(Tech(), index);

        Assert.False(r.Ok);
        Assert.Contains("ground-designated conductor layers", r.Refusal!, StringComparison.Ordinal);
        Assert.Contains("Ground reference", r.Refusal!, StringComparison.Ordinal);
        Assert.DoesNotContain("bind the layer it is on", r.Refusal!, StringComparison.Ordinal);
    }

    // ── The messages, on the stackups that reach them ─────────────────────────────────────────

    /// <summary>The false claim, pinned. Un-tick Bottom Copper and Inner 2 has a designated ground
    /// ABOVE it and none below — the exact shape the old message called "no conductor layer is
    /// marked as a ground reference".</summary>
    [Fact]
    public void ASignalBelowEveryDesignatedGround_IsNotToldItsTechnologyHasNone()
    {
        var tech = Tech();
        var conductors = Conductors(tech);
        conductors[3].IsGroundReference = false;

        var r = OnConductor(tech, 2);

        Assert.True(r.Ok, r.Refusal);
        var note = Assert.Single(r.Notes, n => n.Contains("Stackup.Bottom = Ground", StringComparison.Ordinal));

        Assert.DoesNotContain("No conductor layer in technology", note, StringComparison.Ordinal);
        Assert.Contains("is BELOW every ground-designated conductor", note, StringComparison.Ordinal);
        Assert.Contains(conductors[1].Name, note, StringComparison.Ordinal);   // names the one it found
        Assert.Contains("higher impedance", note, StringComparison.Ordinal);   // says what it costs
    }

    /// <summary>The other branch is still reachable and must keep its original wording: strip EVERY
    /// designation and "no conductor layer is marked as a ground reference" becomes true again.</summary>
    [Fact]
    public void AStackupWithNoDesignationAtAll_StillGetsTheOriginalMessage()
    {
        var tech = Tech();
        foreach (var c in Conductors(tech)) c.IsGroundReference = false;

        var r = OnConductor(tech, 2);

        Assert.True(r.Ok, r.Refusal);
        Assert.Contains(r.Notes, n =>
            n.Contains($"No conductor layer in technology '{tech.Name}' is marked as a ground reference",
                       StringComparison.Ordinal));
    }

    /// <summary>The bottom conductor sitting on the Stackup.Bottom boundary has a zero-height slab.
    /// "Check the stackup order" was the advice, and the order is not wrong — nothing is misordered
    /// on a correctly built board whose bottom layer is simply not a signal level.</summary>
    [Fact]
    public void TheBottomConductorAsASignal_IsNotBlamedOnTheStackupOrder()
    {
        var tech = Tech();
        var conductors = Conductors(tech);
        conductors[3].IsGroundReference = false;   // make it a signal level so it reaches the slab check

        var r = OnConductor(tech, 3);

        Assert.False(r.Ok);
        Assert.Contains("resting directly on the Stackup.Bottom = Ground boundary",
                        r.Refusal!, StringComparison.Ordinal);
        Assert.DoesNotContain("Check the stackup order", r.Refusal!, StringComparison.Ordinal);
    }

    /// <summary>The 2-layer starters cannot reach either new branch — their only ground candidate is
    /// the bottom conductor, so "none below the signal" and "none at all" coincide. Asserted so a
    /// future edit to the messages is measured against the technologies that were always fine.</summary>
    [Fact]
    public void TheTwoLayerStarter_IsUnchangedByAnyOfThis()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var r = PlanarExtractor.Extract(
            [new RectShape
            {
                Layer = Conductors(tech)[0].DrawingLayers[0],
                X1 = 0, Y1 = 0, X2 = Mil(400), Y2 = Mil(100),
            }],
            tech, Dbu);

        Assert.True(r.Ok, r.Refusal);
        Assert.Contains(r.Notes, n =>
            n.Contains("Every port returns through 'Bottom Copper (1 oz)'", StringComparison.Ordinal));
        Assert.DoesNotContain(r.Notes, n =>
            n.Contains("is BELOW every ground-designated conductor", StringComparison.Ordinal));
    }
}
