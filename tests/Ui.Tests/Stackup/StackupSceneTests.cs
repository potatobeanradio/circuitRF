using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Render;
using SkiaSharp;

// The namespace is NOT `CircuitRF.Ui.Tests.Stackup`, though the folder is: a namespace segment named
// `Stackup` shadows the `Stackup` TYPE for every file under `CircuitRF.Ui.Tests`, and two existing
// test files stop compiling ("'Stackup' is a namespace but is used like a type"). The folder is what
// the brief names; the namespace is what the compiler can live with.
namespace CircuitRF.Ui.Tests.StackupRender;

/// <summary>
/// brief-stackup-render-1-scene.md's gate. Every test here builds a scene and reads it — no window,
/// no app host, no canvas, which is the point of the scene being a pure function.
/// </summary>
// SkiaFontsTypefaceCollection: this class ASSERTS OVER RENDERED TEXT BYTES, which is the second
// half of that collection's stated membership rule and the half it says is easy to miss — a class
// like this one looks as though it touches no global at all. It does not set either typeface static;
// what it cannot survive is another class setting one WHILE it renders. Caught in a full-solution
// run (2026-09-13): two builds of one scene came back with `font-family="Helvetica"` on one side and
// IBM Plex on the other, and the same test passed alone.
[Collection(CircuitRF.Ui.Tests.SkiaFontsTypefaceCollection.Name)]
public class StackupSceneTests
{
    private const float Wide = 900f;

    /// <summary>Every technology circuitRF ships, by id, so a new one joins these gates the day it
    /// lands rather than the day someone remembers to add it.</summary>
    public static TheoryData<string> ShippedIds()
    {
        var data = new TheoryData<string>();
        foreach (var entry in ShippedTechnologies.All) data.Add(entry.Id);
        return data;
    }

    private static Technology Shipped(string id) => ShippedTechnologies.Load(id);

    // ── §3 — band height: relative within a kind, never across kinds ──────────────────────────────

    [Theory]
    [MemberData(nameof(ShippedIds))]
    public void MonotoneWithinKind_OnEveryShippedTechnology(string id)
    {
        var tech  = Shipped(id);
        var scene = StackupScene.Build(tech, Wide);
        AssertMonotoneWithinKind(scene);
    }

    [Fact]
    public void MonotoneWithinKind_AcrossFourDecadesOfThickness()
    {
        // 1:1 up to 10,000:1 within ONE kind — past anything a real stack does, and past the point
        // where either branch of the height function could stay proportional.
        foreach (double ratio in new[] { 1d, 2d, 10d, 100d, 1e3, 1e4 })
        {
            var tech = Generated(
                conductors: [1_000, (long)(1_000 * ratio)],
                dielectrics: [1_000, (long)(1_000 * ratio)]);
            AssertMonotoneWithinKind(StackupScene.Build(tech, Wide));
        }
    }

    [Fact]
    public void ExactlyProportional_WhenTheRatioFitsTheBudget()
    {
        var tech  = Generated(conductors: [10_000, 20_000], dielectrics: [50_000]);
        var scene = StackupScene.Build(tech, Wide);

        var thin  = scene.Bands.Single(b => b.Name == "C0");
        var thick = scene.Bands.Single(b => b.Name == "C1");

        Assert.False(scene.ConductorsCompressed);
        Assert.Equal(2.0 * thin.Rect.Height, thick.Rect.Height, 0.5);
    }

    [Fact]
    public void ExactlyProportional_OnTheShippedFourLayerBoard()
    {
        // The 4-layer starter is literally the 2:1 case: 1 oz outers against half-ounce inners.
        var scene = StackupScene.Build(Shipped("pcb-4layer_FR-4_62mil_1oz"), Wide);
        Assert.False(scene.ConductorsCompressed);

        float outer = scene.Bands.Single(b => b.Name == "Top Copper (1 oz)").Rect.Height;
        float inner = scene.Bands.Single(b => b.Name == "Inner 2").Rect.Height;
        Assert.Equal(2.0 * inner, outer, 0.5);
    }

    [Fact]
    public void Compressed_AndFlagged_OnTheMmicDielectrics()
    {
        // 0.2 µm of capacitor film against 100 µm of GaAs — a 500:1 ratio WITHIN one kind, which is
        // the shipped technology that makes "half as thick draws half as tall" unsatisfiable.
        var scene = StackupScene.Build(Shipped("mmic-GaAs_2LM_100um"), Wide);

        Assert.True(scene.DielectricsCompressed);
        Assert.True(scene.Bands.Single(b => b.Name == "GaAs").Rect.Height
                  > scene.Bands.Single(b => b.Name == "MIM Dielectric").Rect.Height);
    }

    [Fact]
    public void Compression_IsSceneMetadata_AndIsNeverDrawn()
    {
        // R-stk1-4, owner 2026-09-13: the picture stays clean. No note, no asterisk, no caption.
        var scene = StackupScene.Build(Shipped("mmic-GaAs_2LM_100um"), Wide);
        Assert.True(scene.DielectricsCompressed);

        foreach (var label in scene.Labels)
            foreach (string banned in new[] { "scale", "compress", "not to", "approx", "*" })
                Assert.DoesNotContain(banned, label.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void KindsAreNeverComparedAgainstEachOther()
    {
        var before = StackupScene.Build(Generated(conductors: [10_000, 20_000], dielectrics: [50_000]), Wide);
        var after  = StackupScene.Build(
            Generated(conductors: [10_000, 20_000], dielectrics: [50_000, 1_600_000]), Wide);

        foreach (string name in new[] { "C0", "C1" })
            Assert.Equal(before.Bands.Single(b => b.Name == name).Rect.Height,
                         after .Bands.Single(b => b.Name == name).Rect.Height);
    }

    private static void AssertMonotoneWithinKind(StackupScene scene)
    {
        foreach (var kind in new[] { StackupKind.Conductor, StackupKind.Dielectric })
        {
            var ofKind = scene.Bands.Where(b => b.Kind == kind).ToList();
            foreach (var a in ofKind)
                foreach (var b in ofKind)
                    if (a.ThicknessDbu > b.ThicknessDbu)
                        Assert.True(a.Rect.Height >= b.Rect.Height - 1e-3f,
                            $"{kind} {a.Name} ({a.ThicknessDbu}) drew {a.Rect.Height} px, " +
                            $"thinner {b.Name} ({b.ThicknessDbu}) drew {b.Rect.Height} px.");
        }
    }

    // ── §4 — vias ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Barrel_SpansExactlyTheTwoConductorsItNames()
    {
        var scene  = StackupScene.Build(Shipped("mmic-GaAs_2LM_100um"), Wide);
        var barrel = scene.Barrels.Single(v => v.Name == "Backside Via");

        var top    = scene.Bands.Single(b => b.Name == "Metal1");
        var bottom = scene.Bands.Single(b => b.Name == "Backside Metal");

        Assert.Equal(Math.Min(top.Rect.Top, bottom.Rect.Top),       barrel.Rect.Top,    0.01);
        Assert.Equal(Math.Max(top.Rect.Bottom, bottom.Rect.Bottom), barrel.Rect.Bottom, 0.01);
    }

    [Fact]
    public void PlatedSolidAndUnplated_AreThreeDifferentLooks()
    {
        var tech = Shipped("pcb-2layer_RO4350B_20mil_1oz");
        var via  = tech.Stackup.Layers.Single(l => l.Kind == StackupKind.Via);

        via.Fill = ViaFillKind.Plated; via.Plated = null;
        var plated = StackupScene.Build(tech, Wide).Barrels.Single();
        Assert.Equal(StackupViaLook.PlatedBarrel, plated.Look);
        Assert.True(plated.WallPx > 0f);

        via.Fill = ViaFillKind.Solid;
        Assert.Equal(StackupViaLook.SolidFill, StackupScene.Build(tech, Wide).Barrels.Single().Look);

        via.Plated = false;
        Assert.Equal(StackupViaLook.UnplatedHole, StackupScene.Build(tech, Wide).Barrels.Single().Look);
    }

    [Fact]
    public void WallScalesAcrossTheStacksOwnVias_AndNeverClosesTheHole()
    {
        var tech = Shipped("pcb-2layer_RO4350B_20mil_1oz");
        var thin = tech.Stackup.Layers.Single(l => l.Kind == StackupKind.Via);
        var thick = Clone(thin);
        thick.Name = "Fat Via";
        thick.WallThicknessDbu = thin.WallThicknessDbu * 2;
        tech.Stackup.Layers.Add(thick);

        var scene = StackupScene.Build(tech, Wide);
        float a = scene.Barrels.Single(v => v.Name == thin.Name).WallPx;
        float b = scene.Barrels.Single(v => v.Name == "Fat Via").WallPx;

        Assert.True(b > a, $"the thicker wall drew {b} px against {a} px.");
        foreach (var barrel in scene.Barrels)
            Assert.True(barrel.WallPx * 2 < barrel.Rect.Width,
                "a plated barrel whose walls meet is a solid one, which is the other look.");
    }

    [Fact]
    public void AnUnresolvableVia_IsMarkedRatherThanOmitted()
    {
        // R-stk1-6. The card list already flags it; the drawing must not disagree by staying silent.
        var tech = Shipped("pcb-2layer_RO4350B_20mil_1oz");
        var via  = tech.Stackup.Layers.Single(l => l.Kind == StackupKind.Via);
        via.SpanToLayer = "A Layer That Is Not In The Stack";

        var scene = StackupScene.Build(tech, Wide);
        Assert.Empty(scene.Barrels);

        var marker = scene.Labels.Where(l => l.LayerName == via.Name).ToList();
        Assert.NotEmpty(marker);
        Assert.Contains(marker, l => l.Style == StackupLabelStyle.Refusal);
        Assert.Contains(marker, l => l.Text.Contains("does not resolve", StringComparison.Ordinal));
    }

    [Fact]
    public void AnExplicitLaneFromTheOptions_IsHonoured()
    {
        var tech = Shipped("pcb-2layer_RO4350B_20mil_1oz");
        var via  = tech.Stackup.Layers.Single(l => l.Kind == StackupKind.Via);

        float defaulted = StackupScene.Build(tech, Wide).Barrels.Single().Rect.MidX;
        var moved = StackupScene.Build(tech, Wide, new StackupSceneOptions
        {
            ViaLanes = new Dictionary<string, float> { [via.Name] = 0.2f },
        }).Barrels.Single();

        Assert.NotEqual(defaulted, moved.Rect.MidX, 0.5);
        Assert.InRange(moved.Rect.MidX, StackupScene.Gutter, Wide - StackupScene.Gutter);
    }

    [Fact]
    public void MoreViasThanLanes_WrapRatherThanRunOffTheEdge()
    {
        var tech = Shipped("pcb-2layer_RO4350B_20mil_1oz");
        var seed = tech.Stackup.Layers.Single(l => l.Kind == StackupKind.Via);
        for (int i = 0; i < 7; i++)
        {
            var extra = Clone(seed);
            extra.Name = $"Via {i}";
            tech.Stackup.Layers.Add(extra);
        }

        var scene = StackupScene.Build(tech, Wide);
        Assert.Equal(8, scene.Barrels.Count);

        float bandLeft  = scene.Bands[0].Rect.Left;
        float bandRight = scene.Bands[0].Rect.Right;
        foreach (var barrel in scene.Barrels)
        {
            Assert.True(barrel.Rect.Left  >= bandLeft,  $"{barrel.Name} ran off the left edge.");
            Assert.True(barrel.Rect.Right <= bandRight, $"{barrel.Name} ran off the right edge.");
        }
    }

    // ── §5 — grippers ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GripperHitRectsAreLargerThanTheirGlyph_AndAreHitBeforeTheBarrel()
    {
        var scene  = StackupScene.Build(Shipped("pcb-2layer_RO4350B_20mil_1oz"), Wide);
        var barrel = scene.Barrels.Single();

        Assert.Equal((StackupScene.GripGlyphHalf + StackupScene.GripHitSlop) * 2, barrel.GripTop.Width, 0.01);

        var hit = scene.HitTest(barrel.GripTop.MidX, barrel.GripTop.MidY);
        Assert.NotNull(hit);
        Assert.Equal(StackupHitKind.ViaGripTop, hit!.Kind);
        Assert.Equal(barrel.Name, hit.LayerName);
    }

    [Fact]
    public void ZOrder_GripperBeatsBarrelBeatsBand()
    {
        var scene  = StackupScene.Build(Shipped("pcb-2layer_RO4350B_20mil_1oz"), Wide);
        var barrel = scene.Barrels.Single();

        int band = IndexOf(StackupHitKind.Band);
        int via  = IndexOf(StackupHitKind.ViaBarrel);
        int grip = IndexOf(StackupHitKind.ViaGripTop);
        int IndexOf(StackupHitKind kind)
        {
            for (int i = 0; i < scene.Hits.Count; i++) if (scene.Hits[i].Kind == kind) return i;
            return -1;
        }
        Assert.True(band < via && via < grip, "Hits must be in draw order, topmost last.");

        // A point on the barrel that is NOT on a gripper resolves to the barrel, not to the band it
        // crosses — which is the disambiguation the order exists for.
        var hit = scene.HitTest(barrel.Rect.MidX, barrel.Rect.MidY);
        Assert.Equal(StackupHitKind.ViaBarrel, hit!.Kind);
    }

    // ── §6 — text: measured, padded, and never overlapping ───────────────────────────────────────

    [Theory]
    [MemberData(nameof(ShippedIds))]
    public void NoTwoLabelsIntersect_OnEveryShippedTechnology(string id)
    {
        foreach (float width in new[] { 900f, 620f, 460f, 380f, 300f, 200f })
            AssertNoLabelOverlap(StackupScene.Build(Shipped(id), width), $"{id} at {width} px");
    }

    [Fact]
    public void NoTwoLabelsIntersect_OnTwentyAlternatingHairlineBands()
    {
        // The moment two thin bands are adjacent, two label-column entries want the same y.
        foreach (float width in new[] { 900f, 620f, 380f })
            AssertNoLabelOverlap(StackupScene.Build(HairlineStack(10), width),
                                 $"20 hairline bands at {width} px");
    }

    /// <summary>
    /// <b>A spec row lines up with the band it describes</b>, on a pane wide enough for the label
    /// column not to wrap much.
    ///
    /// <para>Owner, 2026-09-13: "too much vertical padding in the text on the right side… if we reduce
    /// it, it will be easier to align the text row with its cross-section band." It was
    /// <c>LabelPadY = 3</c>, so every line of every group carried 6 px it did not need; every CLUSTER
    /// of groups was that much taller, and <see cref="StackupScene"/>'s separator re-centres a cluster
    /// about the mean of its members' ideal centres, which displaces each member further from its own
    /// band. At 1 the mean offset over the shipped technologies fell from 39.4 px to 4.25 and the
    /// worst from 29.1 to 11.58.</para>
    ///
    /// <para>The bounds are the measurement plus headroom for a font change, and they are chosen to
    /// FAIL at the old padding: 29.1 px is over the 20 px ceiling below.</para>
    /// </summary>
    [Fact]
    public void ASpecRowLinesUpWithItsBand_OnEveryShippedTechnology()
    {
        double total = 0, worst = 0;
        int n = 0;
        string worstWho = "";

        foreach (var entry in ShippedTechnologies.All)
        {
            var scene = StackupScene.Build(Shipped(entry.Id), Wide);
            foreach (var band in scene.Bands)
            {
                var spec = scene.Labels.FirstOrDefault(
                    l => string.Equals(l.LayerName, band.Name, StringComparison.Ordinal) &&
                         l.Field == StackupField.Thickness);
                if (spec is null) continue;

                double d = Math.Abs(spec.Rect.MidY - band.Rect.MidY);
                total += d;
                n++;
                if (d > worst) { worst = d; worstWho = $"{entry.Id}/{band.Name}"; }
            }
        }

        Assert.True(n > 20, $"only {n} bands were measured — the gate proved little.");
        Assert.True(worst <= 20, $"worst offset {worst:F2} px at {worstWho}");
        Assert.True(total / n <= 8, $"mean offset {total / n:F2} px over {n} bands");
    }

    /// <summary>
    /// …and the padding is NOT what keeps two labels apart, which is what lets it be that small:
    /// <c>LabelGap</c> separates two groups and <c>LineGap</c> two wrapped lines of one, and both are
    /// strictly positive on their own account. R-stk1-9 rests on those, not on the padding.
    /// </summary>
    [Fact]
    public void TheLabelPaddingIsNotWhatKeepsTwoLabelsApart()
    {
        Assert.True(StackupScene.LabelGap > 0);
        Assert.True(StackupScene.LineGap  > 0);
        Assert.True(StackupScene.LabelPadY < StackupScene.LabelGap,
            "the padding must not be carrying the separation the two gaps are for");

        // Not zero either: brief 4 double-clicks these rects, and a rect that is exactly the glyph box
        // is one a click a pixel high misses.
        Assert.True(StackupScene.LabelPadY > 0);
    }

    [Fact]
    public void WithNothingToCollideWith_ASpecSitsOnItsBandsOwnCentreLine()
    {
        // A generated stack with NO vias: a via's own spec anchors on its barrel's centre, which on a
        // through-hole board is the same y as the dielectric it crosses, so the two share an anchor
        // and both are legitimately pushed. That is the collision case, not the quiet one.
        // Every band here is taller than one line of label text, deliberately: a band SHORTER than
        // its own spec cannot have that spec centred on it and stay inside the stack, so it is pushed
        // — which is the placement working, not a failure of it.
        var scene = StackupScene.Build(Generated([20_000, 30_000], [200_000, 400_000]), Wide);
        foreach (var band in scene.Bands)
        {
            var spec = scene.Labels.Single(l => l.LayerName == band.Name && l.Field == StackupField.Thickness);
            Assert.Equal(band.Rect.MidY, spec.Rect.MidY, 0.5);
        }
    }

    [Fact]
    public void PushedLabelsKeepTheStacksOrder_AndEachLeadsWithItsOwnLayersName()
    {
        // The owner asked for the callout lines to go (2026-09-13), so these two properties are what
        // is left to tie a pushed label back to its band, and both have to be real rather than
        // incidental: the column reads top to bottom in the stack's own order, and a label that is
        // not beside its band says which band it belongs to.
        //
        // Note what is NOT claimed. Symmetric push-apart re-centres a whole cluster about the mean of
        // its members' ideal centres, so an individual label CAN end up further from its own band
        // than a neighbour's is — measured, in the 20-hairline case. "Nearest" is not a property this
        // placement has, and asserting it would be asserting something false.
        var scene = StackupScene.Build(HairlineStack(10), Wide);

        var spec = scene.Labels
            .Where(l => l.Field == StackupField.Thickness)
            .ToDictionary(l => l.LayerName, l => l.Rect.MidY);
        Assert.True(spec.Count > 5, "the worst case should have produced a full column of specs.");

        var inStackOrder = scene.Bands.Where(b => spec.ContainsKey(b.Name)).Select(b => spec[b.Name]).ToList();
        for (int i = 1; i < inStackOrder.Count; i++)
            Assert.True(inStackOrder[i] > inStackOrder[i - 1],
                "the label column must follow the stack's own order.");

        int moved = 0;
        foreach (var band in scene.Bands)
        {
            var name = scene.Labels.Single(l => l.LayerName == band.Name && l.Field == StackupField.Name);
            // The stack's one THICK band of each kind still holds its name, which is the point of the
            // on-band rule; only a band compressed to its kind's floor gives it up.
            if (name.Style == StackupLabelStyle.BandName) continue;
            moved++;
            Assert.Equal(StackupLabelStyle.ColumnName, name.Style);
            Assert.True(name.Rect.MidY <= spec[band.Name] + 0.01f,
                $"{band.Name}'s group must LEAD with its name, not bury it.");
        }
        Assert.True(moved > 5, "the worst case should have moved most of the names off their bands.");
    }

    /// <summary>
    /// <b>The threshold IS the padding</b>, so an on-band name is inside its band by construction —
    /// which is what keeps R-stk1-9 true of a rect the separator never sees.
    ///
    /// <para>Owner, 2026-09-13: the shipped 4-layer board's 8 mil prepreg gave its name up to the
    /// label column with plainly room for it. The band draws 20.95 px, the text's face box is 16.90,
    /// and what it failed was a test against the 22.90 px rect that face box sits in when it is padded
    /// like a label in the COLUMN — padding that exists to keep two separately-placed labels apart, of
    /// which an on-band name has none.</para>
    /// </summary>
    [Fact]
    public void AnOnBandNameIsInsideItsBand_OnEveryShippedTechnology()
    {
        foreach (var entry in ShippedTechnologies.All)
        {
            var scene = StackupScene.Build(Shipped(entry.Id), Wide);
            foreach (var label in scene.Labels.Where(l => l.Style == StackupLabelStyle.BandName))
            {
                var band = scene.Bands.Single(b =>
                    string.Equals(b.Name, label.LayerName, StringComparison.Ordinal));
                Assert.True(band.Rect.Contains(label.Rect),
                    $"{entry.Id}: \"{label.Text}\" at {label.Rect} is not inside {band.Rect}.");
            }
        }
    }

    /// <summary>The 8 mil prepreg itself, named, because it is the case that was reported and a
    /// property test over every technology would go on passing if it silently moved off again.</summary>
    [Fact]
    public void TheFourLayerBoardsEightMilPrepregKeepsItsOwnName()
    {
        var scene = StackupScene.Build(Shipped("pcb-4layer_FR-4_62mil_1oz"), Wide);

        foreach (string name in new[] { "Prepreg (top)", "Prepreg (bottom)" })
        {
            var band  = scene.Bands.Single(b => b.Name == name);
            var label = scene.Labels.Single(l => l.LayerName == name && l.Field == StackupField.Name);

            Assert.Equal(StackupLabelStyle.BandName, label.Style);
            Assert.True(band.Rect.Contains(label.Rect), $"{name} at {label.Rect} left {band.Rect}.");
        }

        // …and it is NOT that the threshold simply went away: the 1 oz inner planes are thinner still
        // and keep their names in the column, which is where a band that cannot hold one puts it.
        foreach (string name in new[] { "Inner 1 (Ground Plane)", "Inner 2" })
            Assert.Equal(StackupLabelStyle.ColumnName,
                scene.Labels.Single(l => l.LayerName == name && l.Field == StackupField.Name).Style);
    }

    [Fact]
    public void ABandsNameSitsOnTheBandWhenItFits_AndMovesOffWhenItDoesNot()
    {
        var scene = StackupScene.Build(Shipped("pcb-2layer_RO4350B_20mil_1oz"), Wide);
        var band  = scene.Bands.Single(b => b.Name == "RO4350");

        var name = scene.Labels.Single(l => l.LayerName == band.Name && l.Field == StackupField.Name);
        Assert.Equal(StackupLabelStyle.BandName, name.Style);
        Assert.True(band.Rect.Contains(name.Rect),
            "a band's own name sits ON the band, in fixed dark ink.");

        // A band compressed to its kind's floor cannot hold its own padded name, so the name moves to
        // the label column ahead of the spec.
        var moved = StackupScene.Build(HairlineStack(10), Wide);
        var c0    = moved.Bands.Single(b => b.Name == "C0");
        var label = moved.Labels.Single(l => l.LayerName == "C0" && l.Field == StackupField.Name);
        Assert.Equal(StackupScene.ConductorMinHeight, c0.Rect.Height, 0.01);
        Assert.False(c0.Rect.Contains(label.Rect));
    }

    // ── R-stk1-10 — editable values are their own labels ─────────────────────────────────────────

    [Fact]
    public void EveryEditableFieldOfEveryKindIsSeparatelyAddressable()
    {
        var scene = StackupScene.Build(Shipped("mmic-GaAs_2LM_100um"), Wide);

        AssertFields("Metal2",         [StackupField.Name, StackupField.Thickness, StackupField.Sigma]);
        AssertFields("GaAs",           [StackupField.Name, StackupField.Thickness, StackupField.Epsr,
                                        StackupField.TanD]);
        AssertFields("Backside Via",   [StackupField.Name, StackupField.WallThickness, StackupField.Span]);

        void AssertFields(string layer, StackupField[] expected)
        {
            var got = scene.Labels.Where(l => l.LayerName == layer && l.Field != StackupField.None)
                                  .Select(l => l.Field).ToHashSet();
            foreach (var f in expected)
                Assert.True(got.Contains(f), $"{layer} has no addressable {f} label (has: {string.Join(", ", got)}).");
        }
    }

    [Fact]
    public void MurIsLabelledOnlyWhenItIsNotOne()
    {
        // Short labels, owner 2026-09-13: µr = 1 is every non-magnetic dielectric, which is very
        // nearly all of them, so printing it spends a whole quantity on every band to say nothing.
        // The trade is stated rather than hidden: with no label there is nothing for brief 4 to
        // double-click, and the card is where a non-magnetic dielectric's µr is edited.
        var tech = Shipped("pcb-2layer_RO4350B_20mil_1oz");
        var diel = tech.Stackup.Layers.Single(l => l.Kind == StackupKind.Dielectric);

        Assert.Equal(1.0, diel.Mur);
        Assert.DoesNotContain(StackupScene.Build(tech, Wide).Labels,
            l => l.LayerName == diel.Name && l.Field == StackupField.Mur);

        diel.Mur = 1.6;
        var labelled = StackupScene.Build(tech, Wide).Labels
            .Single(l => l.LayerName == diel.Name && l.Field == StackupField.Mur);
        Assert.Equal("1.6", labelled.Text);
    }

    [Fact]
    public void TheSpecSaysNoMoreThanItHasTo()
    {
        // Every word here is drawn on every band, so the picture pays for each one.
        var scene = StackupScene.Build(Shipped("mmic-GaAs_2LM_100um"), Wide);
        foreach (string gone in new[] { "thick", "barrel", "reference" })
            Assert.DoesNotContain(scene.Labels, l => l.Text.Contains(gone, StringComparison.Ordinal));
    }

    [Fact]
    public void ASolidViaHasNoWallThicknessLabel()
    {
        // WallThicknessDbu is meaningful on a plated FILL only, which is the only state the card lets
        // it be typed into either.
        var scene = StackupScene.Build(Shipped("mmic-GaAs_2LM_100um"), Wide);
        Assert.DoesNotContain(scene.Labels,
            l => l.LayerName == "Metal1-Metal2 Post" && l.Field == StackupField.WallThickness);
    }

    [Fact]
    public void ValuesAreFormattedExactlyAsTheCardFormatsThem()
    {
        // A drawing that rounds differently from the field it edits is an edit that changes a value
        // the user did not touch. The card's own spellings, transcribed from
        // StackupLayerRowViewModel.RefreshFromModel.
        var tech  = Shipped("pcb-2layer_RO4350B_20mil_1oz");   // display unit: mil, deliberately not µm
        var scene = StackupScene.Build(tech, Wide);
        var diel  = tech.Stackup.Layers.Single(l => l.Name == "RO4350");
        var inv   = CultureInfo.InvariantCulture;

        Assert.Equal(
            LayoutUnits.Format(diel.ThicknessDbu, tech.DefaultDisplayUnit, LayoutUnits.DefaultDbuPerMicron),
            ValueOf("RO4350", StackupField.Thickness));
        Assert.Equal(diel.Epsr.ToString("0.####",   inv), ValueOf("RO4350", StackupField.Epsr));
        Assert.Equal(diel.TanD.ToString("0.######", inv), ValueOf("RO4350", StackupField.TanD));

        var metal = tech.Stackup.Layers.First(l => l.Kind == StackupKind.Conductor);
        Assert.Equal(metal.SigmaSm.ToString("0.###e+0", inv), ValueOf(metal.Name, StackupField.Sigma));

        // The unit is its own piece, and it is the unit the card's suffix shows.
        Assert.Contains(scene.Labels,
            l => l.LayerName == "RO4350" && l.Text == LayoutUnits.Suffix(tech.DefaultDisplayUnit));

        string ValueOf(string layer, StackupField field)
            => scene.Labels.Single(l => l.LayerName == layer && l.Field == field).Text;
    }

    // ── §2 — the width, and how it degrades ──────────────────────────────────────────────────────

    [Fact]
    public void TheLabelColumnIsDroppedBeforeTheSceneCollapses()
    {
        var tech = Shipped("pcb-2layer_RO4350B_20mil_1oz");

        var wide = StackupScene.Build(tech, 900f);
        Assert.False(wide.LabelColumnDropped);
        Assert.False(wide.IsTooNarrow);
        Assert.True(wide.Bands[0].Rect.Right < 900f - StackupScene.Gutter,
            "with a label column, the bands stop short of the right margin.");

        var narrow = StackupScene.Build(tech, StackupScene.LabelColumnDropWidth - 1f);
        Assert.True(narrow.LabelColumnDropped);
        Assert.False(narrow.IsTooNarrow);
        Assert.Equal(3, narrow.Bands.Count);
        Assert.Contains(narrow.Labels, l => l.Field == StackupField.Thickness);

        // With the column dropped the specs stack BENEATH the drawing, in stack order and each
        // leading with its layer's name — never over a band that is not their own.
        float stackBottom = narrow.Bands.Max(b => b.Rect.Bottom);
        foreach (var label in narrow.Labels.Where(l => l.Style != StackupLabelStyle.Note))
            Assert.True(label.Rect.Top >= stackBottom,
                $"\"{label.Text}\" is drawn over the bands with the label column dropped.");
        Assert.DoesNotContain(narrow.Labels, l => l.Style == StackupLabelStyle.BandName);

        var tiny = StackupScene.Build(tech, StackupScene.MinRenderableWidth - 1f);
        Assert.True(tiny.IsTooNarrow);
        Assert.Empty(tiny.Bands);
        Assert.Single(tiny.Labels);
    }

    [Theory]
    [MemberData(nameof(ShippedIds))]
    public void NothingIsDrawnOutsideTheReportedExtent(string id)
    {
        // Height is INTRINSIC — brief 2's control reports it and a ScrollViewer scrolls it — so
        // anything past it is invisible with no way to reach it. Width is the pane's, and a label
        // past it is a value the user cannot read and brief 4 cannot edit: that is what made the
        // spec labels wrap.
        foreach (float width in new[] { 900f, 620f, 460f, 380f, 300f, 200f })
        {
            var scene = StackupScene.Build(Shipped(id), width);
            foreach (var band   in scene.Bands)   Assert.True(band.Rect.Bottom <= scene.Height);
            foreach (var barrel in scene.Barrels) Assert.True(barrel.GripBottom.Bottom <= scene.Height);
            foreach (var label in scene.Labels)
            {
                Assert.True(label.Rect.Bottom <= scene.Height,
                    $"{id} at {width} px: \"{label.Text}\" runs past the reported height.");
                Assert.True(label.Rect.Right <= scene.Width,
                    $"{id} at {width} px: \"{label.Text}\" runs off the right edge.");
            }
        }
    }

    [Fact]
    public void TheTwoBoundaryConditionsAreDrawnAsNotesAboveAndBelowTheStack()
    {
        var tech  = Shipped("pcb-2layer_RO4350B_20mil_1oz");
        var scene = StackupScene.Build(tech, Wide);

        // The top note is two PIECES — "Top: Open" and the aside after it — so a narrow pane wraps it
        // instead of running it off the edge.
        var notes = scene.Labels.Where(l => l.Style == StackupLabelStyle.Note).ToList();
        Assert.Contains(notes, n => n.Text == $"Top: {tech.Stackup.Top}");
        Assert.Contains(notes, n => n.Text == $"Bottom: {tech.Stackup.Bottom}");

        float stackTop    = scene.Bands.Min(b => b.Rect.Top);
        float stackBottom = scene.Bands.Max(b => b.Rect.Bottom);
        Assert.Contains(notes, n => n.Rect.Bottom <= stackTop);
        Assert.Contains(notes, n => n.Rect.Top    >= stackBottom);
    }

    // ── §1 — colours come from the technology ────────────────────────────────────────────────────

    [Fact]
    public void AConductorTakesItsFillFromTheDrawingLayerItIsBoundTo_AndADielectricHasNone()
    {
        var tech  = Shipped("mmic-GaAs_2LM_100um");
        var scene = StackupScene.Build(tech, Wide);

        var metal2 = tech.Stackup.Layers.Single(l => l.Name == "Metal2");
        var bound  = tech.Layers.Single(d => d.Key.Equals(metal2.DrawingLayers[0]));
        Assert.Equal(bound.Color, scene.Bands.Single(b => b.Name == "Metal2").Fill);

        // A dielectric is neutral grey because it is not something anyone draws on — so the scene
        // carries no colour for it at all and the renderer uses the theme's.
        Assert.Null(scene.Bands.Single(b => b.Name == "GaAs").Fill);
    }

    [Fact]
    public void TheGroundDesignatedConductorIsTheOneBandFlaggedForAHeavyEdge()
    {
        var scene = StackupScene.Build(Shipped("mmic-GaAs_2LM_100um"), Wide);
        Assert.Single(scene.Bands, b => b.IsGroundReference);
        Assert.True(scene.Bands.Single(b => b.IsGroundReference).Name == "Backside Metal");
    }

    // ── R-stk1-11 — determinism ──────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(ShippedIds))]
    public void BuildIsDeterministic_ElementWiseAndByRenderedBytes(string id)
    {
        var a = StackupScene.Build(Shipped(id), Wide);
        var b = StackupScene.Build(Shipped(id), Wide);

        Assert.Equal(a.Height, b.Height);
        Assert.Equal<IEnumerable<StackupBand>>(a.Bands,   b.Bands);
        Assert.Equal<IEnumerable<StackupBarrel>>(a.Barrels, b.Barrels);
        Assert.Equal<IEnumerable<StackupLabel>>(a.Labels,  b.Labels);
        Assert.Equal<IEnumerable<StackupHit>>(a.Hits,     b.Hits);

        // A layout pass that depended on a dictionary's hash order would produce a picture that moves
        // between runs, which brief 7's clipboard export and brief 8's doc figures both depend on it
        // not doing. Bytes, because element-wise equality cannot see a renderer reading a set.
        Assert.Equal(Svg(a), Svg(b));

        static string Svg(StackupScene scene) => PlotDocumentWriter.BuildSvgString(
            canvas => StackupRenderer.Draw(canvas, scene, StackupRenderTheme.Light),
            new PagePlacement(scene.Width, scene.Height, 0f));
    }

    [Theory]
    [MemberData(nameof(ShippedIds))]
    public void TheRendererDrawsWithoutAWindowOrAnAppHost(string id)
    {
        // Not a picture test — a reachability one. src/Render is below the UI firewall, so a scene
        // must render in a headless process exactly as it does in the application.
        var scene = StackupScene.Build(Shipped(id), Wide);
        foreach (var theme in new[] { StackupRenderTheme.Light, StackupRenderTheme.Dark })
        {
            string svg = PlotDocumentWriter.BuildSvgString(
                canvas => StackupRenderer.Draw(canvas, scene, theme,
                    new StackupOverlay { SelectedLayer = scene.Bands[0].Name, HoverLayer = scene.Bands[0].Name }),
                new PagePlacement(scene.Width, scene.Height, 0f));
            Assert.Contains("<svg", svg, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void OnBandInkIsIdenticalInBothVariants()
    {
        // A band's fill is the TECHNOLOGY's colour and is the same in light and dark, so ink that
        // followed the variant would vanish into copper in the dark one — a bug already found once
        // in DocStackupFixtures.
        Assert.Equal(StackupRenderTheme.Light.OnBandInk, StackupRenderTheme.Dark.OnBandInk);
        Assert.NotEqual(StackupRenderTheme.Light.LabelInk, StackupRenderTheme.Dark.LabelInk);
    }

    // ── A via's SPEC row: elided span names, and no "wall" (owner, 2026-09-13) ───────────────────

    /// <summary>
    /// A span name is kept whole when it is short, and otherwise cut <b>at a bracket or at a space</b>
    /// — never mid-word, because "Bottom Copper…" still names a layer a reader recognises and
    /// "Bottom Cop…" is a string that could belong to two of them.
    ///
    /// <para>A BRACKET cuts wherever it falls, even before the budget (owner, 2026-09-13): what
    /// follows one is a qualifier rather than part of the identity, so "Inner 1 (Ground Plane)" reads
    /// better as "Inner 1…" than as "Inner 1 (Ground…", which spends six more characters and leaves a
    /// bracket hanging open.</para>
    /// </summary>
    [Theory]
    [InlineData("Metal1",                 "Metal1")]                 // shorter than the budget
    [InlineData("Inner 2",                "Inner 2")]
    [InlineData("Ten charsX",             "Ten charsX")]             // exactly the budget
    [InlineData("M1 (top plate)",         "M1…")]                    // a bracket, well before the budget
    [InlineData("Top Copper (1 oz)",      "Top Copper…")]
    [InlineData("Bottom Copper (1 oz)",   "Bottom Copper…")]
    [InlineData("Inner 1 (Ground Plane)", "Inner 1…")]
    [InlineData("Inner 1 [Ground Plane]", "Inner 1…")]               // square brackets too
    [InlineData("Backside Metal",         "Backside Metal")]         // the cut would not shorten it
    [InlineData("NoSpacesAnywhereAtAll",  "NoSpacesAnywhereAtAll")]  // nowhere readable to cut
    // A bracket at index 0 is REFUSED as a cut point — it would leave nothing — so the space rule
    // takes over and the name is still shortened, just not to the empty string.
    [InlineData("(a very long qualifier)", "(a very long…")]
    public void ASpanNameIsElidedAtAWordBoundaryOrNotAtAll(string name, string shown)
        => Assert.Equal(shown, StackupScene.ElideSpanName(name));

    /// <summary>…and the scene actually emits the elided spelling, on the shipped board whose layer
    /// names are long enough to matter.</summary>
    [Fact]
    public void TheDrawingShowsTheElidedSpan_NotTheFullConductorName()
    {
        var scene = StackupScene.Build(Shipped("pcb-4layer_FR-4_62mil_1oz"), Wide);
        var spans = scene.Labels.Where(l => l.Field == StackupField.Span).Select(l => l.Text).ToList();

        Assert.Contains("Top Copper…", spans);
        Assert.DoesNotContain("Top Copper (1 oz)", spans);

        // The 4-layer board's own bracket case, which is what asked for the bracket rule.
        Assert.Contains("Inner 1…", spans);

        // The band's OWN name is untouched — only a via's span is elided.
        Assert.Contains(scene.Labels, l => l.Field == StackupField.Name && l.Text == "Top Copper (1 oz)");
    }

    /// <summary>
    /// An unresolvable via's refusal quotes the name VERBATIM. It is the string that failed to
    /// resolve, and a refusal naming a shortened version of it sends the reader looking for a layer
    /// that was never spelled that way.
    /// </summary>
    [Fact]
    public void ARefusalQuotesTheSpanNameWhole()
    {
        var tech = Shipped("pcb-4layer_FR-4_62mil_1oz");
        tech.Stackup.Layers.Single(l => l.Kind == StackupKind.Via && l.SpanToLayer == "Inner 1 (Ground Plane)")
            .SpanToLayer = "Inner 9 (Ground Plane)";

        var scene = StackupScene.Build(tech, Wide);
        Assert.Contains(scene.Labels, l => l.Text.Contains("\"Inner 9 (Ground Plane)\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// The plated wall reads "plated = 25 µm" and no longer says "wall".
    ///
    /// <para>The word was the only one on that row naming a FIELD rather than saying something about
    /// the via, and on a barrel already drawn as two walls with a bore between them it told the reader
    /// what they were looking at. The "=" is the spelling σ, εr and tanδ already use on a band.</para>
    /// </summary>
    [Fact]
    public void APlatedWallIsWrittenAsAnEquality_AndTheWordWallIsGone()
    {
        var scene = StackupScene.Build(Shipped("pcb-4layer_FR-4_62mil_1oz"), Wide);
        var via   = scene.Barrels.First(b => b.Look == StackupViaLook.PlatedBarrel);
        var row   = scene.Labels.Where(l => l.LayerName == via.Name).ToList();

        Assert.Contains(row, l => l.Text == "plated =");
        Assert.DoesNotContain(row, l => l.Text.Contains("wall", StringComparison.OrdinalIgnoreCase));

        // The value is still its own piece, so brief 4 can still double-click it.
        Assert.Contains(row, l => l.Field == StackupField.WallThickness);
    }

    /// <summary>A via that is not a plated barrel has no wall to state, so it keeps the bare word —
    /// "plated =" with nothing meaningful after it would be worse than either.</summary>
    [Theory]
    [InlineData(StackupViaLook.SolidFill,    "solid")]
    [InlineData(StackupViaLook.UnplatedHole, "unplated")]
    public void AViaWithNoWallKeepsTheBareWord(StackupViaLook look, string word)
    {
        var tech = Shipped("pcb-2layer_RO4350B_20mil_1oz");
        var via  = tech.Stackup.Layers.Single(l => l.Kind == StackupKind.Via);
        if (look == StackupViaLook.SolidFill) via.Fill = ViaFillKind.Solid;
        else                                  via.Plated = false;

        var scene = StackupScene.Build(tech, Wide);
        var row   = scene.Labels.Where(l => l.LayerName == via.Name).ToList();

        Assert.Contains(row, l => l.Text == word);
        Assert.DoesNotContain(row, l => l.Text == "plated =");
    }

    // ── A via's NAME: inside one band, and clear of the next barrel (owner, 2026-09-13) ──────────

    /// <summary>
    /// <b>A via's name lands inside ONE band</b>, wherever a band the barrel crosses is tall enough
    /// to hold it. A name straddling a boundary is half on metal and half on dielectric and reads as
    /// neither.
    ///
    /// <para>Every shipped technology, at the width the tab actually uses, because the failure is a
    /// function of where the bands happen to fall.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(ShippedIds))]
    public void AViasNameSitsInsideOneBand_OnEveryShippedTechnology(string id)
    {
        var scene = StackupScene.Build(Shipped(id), Wide);
        int checkedNames = 0;

        foreach (var barrel in scene.Barrels)
        {
            var name = scene.Labels.FirstOrDefault(
                l => l.Style == StackupLabelStyle.ViaName &&
                     string.Equals(l.LayerName, barrel.Name, StringComparison.Ordinal));
            if (name is null) continue;   // pushed into the label column instead — rule 4's other half

            // A band could hold it at all: otherwise "inside one band" is not something the drawing
            // can honour and the barrel's own middle is the stated fallback.
            var candidates = scene.Bands
                .Where(b => b.Rect.Bottom > barrel.Rect.Top && b.Rect.Top < barrel.Rect.Bottom)
                .Where(b => b.Rect.Height >= name.Rect.Height)
                .ToList();
            if (candidates.Count == 0) continue;

            checkedNames++;
            Assert.True(
                candidates.Any(b => name.Rect.Top >= b.Rect.Top - 0.01f &&
                                    name.Rect.Bottom <= b.Rect.Bottom + 0.01f),
                $"{barrel.Name}'s name at {name.Rect} straddles a band boundary.");
        }

        Assert.True(checkedNames > 0, "no via name was placed beside a barrel — the gate proved nothing.");
    }

    /// <summary>A DIELECTRIC is preferred: it is the taller band on nearly every stack and it carries
    /// no on-band name of its own.</summary>
    [Fact]
    public void AViasNamePrefersADielectricBandOverAConductor()
    {
        var scene = StackupScene.Build(Shipped("pcb-2layer_RO4350B_20mil_1oz"), Wide);
        var barrel = scene.Barrels.Single();
        var name = scene.Labels.Single(
            l => l.Style == StackupLabelStyle.ViaName &&
                 string.Equals(l.LayerName, barrel.Name, StringComparison.Ordinal));

        var band = scene.Bands.Single(b => name.Rect.Top >= b.Rect.Top - 0.01f &&
                                           name.Rect.Bottom <= b.Rect.Bottom + 0.01f);
        Assert.Equal(StackupKind.Dielectric, band.Kind);
    }

    /// <summary>
    /// <b>A via's name does not run over the next barrel's metal.</b> The name is drawn immediately
    /// to the right of its own barrel, so the lane spread has to leave that much room between one
    /// barrel and the next — which the old three-fixed-fractions spread did not, because it took no
    /// account of what anything was called.
    /// </summary>
    [Fact]
    public void ViaNamesAreClearOfTheNextBarrel_WhenTheColumnHasRoom()
    {
        var tech = Shipped("pcb-2layer_RO4350B_20mil_1oz");
        var seed = tech.Stackup.Layers.Single(l => l.Kind == StackupKind.Via);
        foreach (string n in new[] { "GND via", "Thermal", "Stitch" })
        {
            var extra = Clone(seed);
            extra.Name = n;
            tech.Stackup.Layers.Add(extra);
        }

        var scene = StackupScene.Build(tech, Wide);
        Assert.Equal(4, scene.Barrels.Count);

        foreach (var name in scene.Labels.Where(l => l.Style == StackupLabelStyle.ViaName))
            foreach (var barrel in scene.Barrels)
            {
                if (string.Equals(barrel.Name, name.LayerName, StringComparison.Ordinal)) continue;
                Assert.False(name.Rect.IntersectsWith(barrel.Rect),
                    $"\"{name.Text}\" at {name.Rect} runs over {barrel.Name}\'s barrel {barrel.Rect}.");
            }
    }

    /// <summary>…and when it does NOT have room the barrels still stay inside the column, which is
    /// R-stk1-8 and outranks the names: overlapping text is the stated fallback, a barrel off the
    /// page is not.</summary>
    [Fact]
    public void WithNoRoomForEveryName_TheBarrelsStillStayInsideTheBandColumn()
    {
        var tech = Shipped("pcb-2layer_RO4350B_20mil_1oz");
        var seed = tech.Stackup.Layers.Single(l => l.Kind == StackupKind.Via);
        for (int i = 0; i < 9; i++)
        {
            var extra = Clone(seed);
            extra.Name = $"A rather long via name {i}";
            tech.Stackup.Layers.Add(extra);
        }

        var scene = StackupScene.Build(tech, Wide);
        Assert.Equal(10, scene.Barrels.Count);

        float left = scene.BandColumn.Left, right = scene.BandColumn.Right;
        foreach (var barrel in scene.Barrels)
        {
            Assert.True(barrel.Rect.Left  >= left,  $"{barrel.Name} ran off the left edge.");
            Assert.True(barrel.Rect.Right <= right, $"{barrel.Name} ran off the right edge.");
        }
    }

    // ── The ground reference's spec piece ────────────────────────────────────────────────────────

    /// <summary>"gnd", not "ground ref" (owner, 2026-09-13): the label column wraps, and the two
    /// words took a whole wrapped line for what the band's heavy edge has already said.</summary>
    [Fact]
    public void TheGroundReferenceIsLabelledGnd()
    {
        var tech  = Shipped("pcb-2layer_RO4350B_20mil_1oz");
        var ground = tech.Stackup.Layers.First(l => l.IsGroundReference);
        var scene = StackupScene.Build(tech, Wide);

        var accents = scene.Labels
            .Where(l => l.Style == StackupLabelStyle.Accent &&
                        string.Equals(l.LayerName, ground.Name, StringComparison.Ordinal))
            .Select(l => l.Text)
            .ToList();

        Assert.Contains(StackupScene.GroundReferenceText, accents);
        Assert.Equal("gnd", StackupScene.GroundReferenceText);
        Assert.DoesNotContain(scene.Labels, l => l.Text.Contains("ground ref", StringComparison.Ordinal));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private static void AssertNoLabelOverlap(StackupScene scene, string what)
    {
        var labels = scene.Labels;
        for (int i = 0; i < labels.Count; i++)
            for (int j = i + 1; j < labels.Count; j++)
                Assert.False(labels[i].Rect.IntersectsWith(labels[j].Rect),
                    $"{what}: \"{labels[i].Text}\" {labels[i].Rect} overlaps " +
                    $"\"{labels[j].Text}\" {labels[j].Rect}.");
    }

    private static StackupLayer Clone(StackupLayer src) => new()
    {
        Kind = src.Kind, Name = src.Name, ThicknessDbu = src.ThicknessDbu,
        Epsr = src.Epsr, TanD = src.TanD, Mur = src.Mur, SigmaSm = src.SigmaSm,
        DrawingLayers = [.. src.DrawingLayers],
        IsGroundReference = src.IsGroundReference, SheetAt = src.SheetAt,
        PresentWithLayer = src.PresentWithLayer, Fill = src.Fill,
        WallThicknessDbu = src.WallThicknessDbu, Plated = src.Plated,
        SpanFromLayer = src.SpanFromLayer, SpanToLayer = src.SpanToLayer,
    };

    /// <summary>
    /// <paramref name="pairs"/> conductor/dielectric pairs at the thinnest thickness the unit can
    /// express, plus ONE thick band of each kind.
    ///
    /// <para><b>The thick outlier is the point.</b> A stack of nothing but hairlines has a 1:1 ratio
    /// within each kind, so §3's proportional branch draws every one of them at the kind's FULL
    /// height — there is nothing to be proportional against — and the picture has no collisions at
    /// all. It takes a wide ratio to drive the hairlines down onto the kind's floor, which is where
    /// two adjacent bands are shorter than the text describing them and the label placement has real
    /// work to do.</para>
    /// </summary>
    private static Technology HairlineStack(int pairs)
    {
        var thin = Enumerable.Repeat(1L, pairs).Append(1_000_000L).ToArray();
        return Generated(thin, thin);
    }

    /// <summary>A stack built to a stated shape: conductors named C0, C1, … alternating with
    /// dielectrics D0, D1, …, so a test can name the ratio it is about.</summary>
    private static Technology Generated(IReadOnlyList<long> conductors, IReadOnlyList<long> dielectrics)
    {
        var tech = new Technology { Name = "Generated", DefaultDisplayUnit = LayoutUnit.Um };
        for (int i = 0; i < Math.Max(conductors.Count, dielectrics.Count); i++)
        {
            if (i < conductors.Count)
                tech.Stackup.Layers.Add(new StackupLayer
                {
                    Kind = StackupKind.Conductor, Name = $"C{i}",
                    ThicknessDbu = conductors[i], SigmaSm = 5.8e7,
                });
            if (i < dielectrics.Count)
                tech.Stackup.Layers.Add(new StackupLayer
                {
                    Kind = StackupKind.Dielectric, Name = $"D{i}",
                    ThicknessDbu = dielectrics[i], Epsr = 4.4, TanD = 0.02,
                });
        }
        return tech;
    }
}
